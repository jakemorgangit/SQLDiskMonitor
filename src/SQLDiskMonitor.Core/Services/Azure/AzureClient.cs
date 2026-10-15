using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using SQLDiskMonitor.Models;

namespace SQLDiskMonitor.Services.Azure;

public record AzureVm(string Id, string Name, string ResourceGroup, string SubscriptionId, string SubscriptionName,
    string Location, string ComputerName, string VmSize)
{
    public override string ToString() => Name;
}

public record AzureDisk(string Id, string Name, int? Lun, bool IsOsDisk, string Caching, bool WriteAccelerator, double SizeGb,
    string SkuName, string Tier, double Iops, double Mbps, bool BurstingEnabled, string Location,
    int MaxShares = 1, string Encryption = "", string NetworkAccess = "", string Zone = "", string DiskState = "", DateTime? Created = null,
    Dictionary<string, string>? Tags = null);

public record AzureVmDetail(AzureVm Vm, List<AzureDisk> Disks, double? VmMaxIops, double? VmMaxMbps, int? MaxDataDisks, int DataDisksAttached);

/// <summary>
/// Read-only Azure Resource Manager access: sign-in, VM search (Resource Graph), disk details, VM size limits and
/// retail disk prices. Reader on the subscriptions or resource groups is enough.
/// </summary>
public class AzureClient
{
    private const string Arm = "https://management.azure.com";
    private static readonly string[] Scopes = ["https://management.azure.com/.default"];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly Dictionary<string, (double? Iops, double? Mbps, int? MaxDisks)> SizeCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly TokenCredential _credential;
    private readonly AppSettings _cfg;
    private Dictionary<string, string>? _subscriptions;
    private readonly string? _tenantProblem;

    private void CheckTenant()
    {
        if (_tenantProblem != null) throw new InvalidOperationException(_tenantProblem);
    }

    private static string RecordPath => Path.Combine(AppSettings.DataFolder, "azure-auth.json");

    public AzureClient(AppSettings cfg)
    {
        _cfg = cfg;
        // A bad tenant is reported when the client is first used, so windows can still open.
        _tenantProblem = ValidateTenant(cfg.AzureTenantId);
        var tenant = string.IsNullOrWhiteSpace(cfg.AzureTenantId) || _tenantProblem != null ? null : cfg.AzureTenantId.Trim();
        if (cfg.AzureAuth == "AzureCli")
        {
            _credential = new AzureCliCredential(new AzureCliCredentialOptions { TenantId = tenant });
        }
        else
        {
            var options = new InteractiveBrowserCredentialOptions
            {
                TenantId = tenant,
                // Tokens are cached (DPAPI-protected) so the browser only appears when the session has expired.
                TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = "SQLDiskMonitor" },
                AuthenticationRecord = LoadRecord()
            };
            _credential = new InteractiveBrowserCredential(options);
        }
    }

    private static AuthenticationRecord? LoadRecord()
    {
        try
        {
            if (!File.Exists(RecordPath)) return null;
            using var fs = File.OpenRead(RecordPath);
            return AuthenticationRecord.Deserialize(fs);
        }
        catch { return null; }
    }

    public static string? SignedInAccount()
    {
        var r = LoadRecord();
        return r?.Username;
    }

    public static void SignOut()
    {
        if (File.Exists(RecordPath)) File.Delete(RecordPath);
    }

    /// <summary>Signs in (browser) or checks the Azure CLI login. Returns the account name.</summary>
    public async Task<string> SignInAsync(CancellationToken ct = default)
    {
        CheckTenant();
        if (_credential is InteractiveBrowserCredential browser)
        {
            var record = await browser.AuthenticateAsync(new TokenRequestContext(Scopes), ct);
            await using var fs = File.Create(RecordPath);
            await record.SerializeAsync(fs, ct);
            return record.Username;
        }
        var token = await _credential.GetTokenAsync(new TokenRequestContext(Scopes), ct);
        return AccountFromToken(token.Token) ?? "Azure CLI account";
    }

    private static string? AccountFromToken(string jwt)
    {
        try
        {
            var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            foreach (var claim in new[] { "upn", "unique_name", "email" })
                if (doc.RootElement.TryGetProperty(claim, out var v)) return v.GetString();
        }
        catch { }
        return null;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        CheckTenant();
        var token = await _credential.GetTokenAsync(new TokenRequestContext(Scopes), ct);
        using var req = new HttpRequestMessage(method, url.StartsWith("http") ? url : Arm + url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            string message = text;
            try
            {
                using var err = JsonDocument.Parse(text);
                if (err.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)) message = m.GetString() ?? text;
            }
            catch { }
            throw new InvalidOperationException($"Azure returned {(int)resp.StatusCode}: {message}");
        }
        return JsonDocument.Parse(text);
    }

    public async Task<Dictionary<string, string>> GetSubscriptionsAsync(CancellationToken ct = default)
    {
        if (_subscriptions != null) return _subscriptions;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? url = "/subscriptions?api-version=2022-12-01";
        while (url != null)
        {
            using var doc = await SendAsync(HttpMethod.Get, url, null, ct);
            foreach (var s in doc.RootElement.GetProperty("value").EnumerateArray())
                result[Str(s, "subscriptionId")] = Str(s, "displayName");
            url = doc.RootElement.TryGetProperty("nextLink", out var n) ? n.GetString() : null;
        }
        return _subscriptions = result;
    }

    /// <summary>Runs a Resource Graph query across every subscription the account can read.</summary>
    private async Task<List<JsonElement>> GraphAsync(string query, CancellationToken ct)
    {
        var subs = await GetSubscriptionsAsync(ct);
        if (subs.Count == 0) throw new InvalidOperationException("This account can't see any Azure subscriptions.");
        var rows = new List<JsonElement>();
        string? skipToken = null;
        do
        {
            var body = new Dictionary<string, object> { ["subscriptions"] = subs.Keys.ToArray(), ["query"] = query };
            if (skipToken != null) body["options"] = new { skipToken };
            using var doc = await SendAsync(HttpMethod.Post, "/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01", body, ct);
            foreach (var r in doc.RootElement.GetProperty("data").EnumerateArray()) rows.Add(r.Clone());
            skipToken = doc.RootElement.TryGetProperty("$skipToken", out var t) ? t.GetString() : null;
        } while (skipToken != null && rows.Count < 5000);
        return rows;
    }

    public async Task<List<AzureVm>> SearchVmsAsync(CancellationToken ct = default)
    {
        var subs = await GetSubscriptionsAsync(ct);
        var rows = await GraphAsync("""
            Resources
            | where type =~ 'microsoft.compute/virtualmachines'
            | project id, name, resourceGroup, subscriptionId, location,
                      computerName = tostring(properties.osProfile.computerName),
                      vmSize = tostring(properties.hardwareProfile.vmSize)
            | order by name asc
            """, ct);
        return rows.Select(r => new AzureVm(Str(r, "id"), Str(r, "name"), Str(r, "resourceGroup"), Str(r, "subscriptionId"),
            subs.GetValueOrDefault(Str(r, "subscriptionId"), ""), Str(r, "location"), Str(r, "computerName"), Str(r, "vmSize"))).ToList();
    }

    /// <summary>The VM, its OS and data disks with their provisioned performance, and the VM size's uncached IO limits.</summary>
    public async Task<AzureVmDetail> GetVmDetailAsync(string vmId, CancellationToken ct = default)
    {
        vmId = vmId.Trim();
        var id = vmId.Replace("'", "");
        var vmRows = await GraphAsync($"""
            Resources
            | where type =~ 'microsoft.compute/virtualmachines' and id =~ '{id}'
            | project id, name, resourceGroup, subscriptionId, location, properties
            """, ct);
        if (vmRows.Count == 0) throw new InvalidOperationException($"No VM found with ID {vmId}, or this account can't read it.");
        var v = vmRows[0];
        var props = v.GetProperty("properties");
        var subs = await GetSubscriptionsAsync(ct);
        var vm = new AzureVm(Str(v, "id"), Str(v, "name"), Str(v, "resourceGroup"), Str(v, "subscriptionId"),
            subs.GetValueOrDefault(Str(v, "subscriptionId"), ""), Str(v, "location"),
            Path2(props, "osProfile", "computerName"), Path2(props, "hardwareProfile", "vmSize"));

        // LUN, caching and write accelerator live on the VM; size, SKU and performance live on each disk.
        var attach = new Dictionary<string, (int? Lun, bool Os, string Caching, bool Wa)>(StringComparer.OrdinalIgnoreCase);
        if (props.TryGetProperty("storageProfile", out var sp))
        {
            if (sp.TryGetProperty("osDisk", out var os) && os.TryGetProperty("managedDisk", out var om))
                attach[Str(om, "id")] = (null, true, Str(os, "caching"), Bool(os, "writeAcceleratorEnabled"));
            if (sp.TryGetProperty("dataDisks", out var dds))
                foreach (var dd in dds.EnumerateArray())
                    if (dd.TryGetProperty("managedDisk", out var md))
                        attach[Str(md, "id")] = (dd.GetProperty("lun").GetInt32(), false, Str(dd, "caching"), Bool(dd, "writeAcceleratorEnabled"));
        }

        var diskRows = await GraphAsync($"""
            Resources
            | where type =~ 'microsoft.compute/disks' and managedBy =~ '{id}'
            | project id, name, location, sku, zones, tags, properties
            """, ct);
        var disks = new List<AzureDisk>();
        foreach (var d in diskRows)
        {
            var dp = d.GetProperty("properties");
            attach.TryGetValue(Str(d, "id"), out var a);
            disks.Add(new AzureDisk(Str(d, "id"), Str(d, "name"), a.Lun, a.Os, a.Caching ?? "", a.Wa,
                Num(dp, "diskSizeGB"), d.TryGetProperty("sku", out var sku) ? Str(sku, "name") : "", Str(dp, "tier"),
                Num(dp, "diskIOPSReadWrite"), Num(dp, "diskMBpsReadWrite"), Bool(dp, "burstingEnabled"), Str(d, "location"),
                MaxShares: (int)Math.Max(1, Num(dp, "maxShares")),
                Encryption: dp.TryGetProperty("encryption", out var enc) ? Str(enc, "type") : "",
                NetworkAccess: Str(dp, "networkAccessPolicy"),
                Zone: d.TryGetProperty("zones", out var z) && z.ValueKind == JsonValueKind.Array ? string.Join(",", z.EnumerateArray().Select(x => x.GetString())) : "",
                DiskState: Str(dp, "diskState"),
                Created: DateTime.TryParse(Str(dp, "timeCreated"), out var tc) ? tc : null,
                Tags: d.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Object
                    ? tg.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString())
                    : null));
        }
        disks.Sort((x, y) => (x.IsOsDisk ? -1 : x.Lun ?? 999).CompareTo(y.IsOsDisk ? -1 : y.Lun ?? 999));

        var (iops, mbps, maxDisks) = await GetVmSizeLimitsAsync(vm.SubscriptionId, vm.Location, vm.VmSize, ct);
        return new AzureVmDetail(vm, disks, iops, mbps, maxDisks, disks.Count(d => !d.IsOsDisk));
    }

    /// <summary>Uncached disk IOPS and MB/s and the data disk limit for a VM size, from the Compute resource SKUs API.</summary>
    public async Task<(double? Iops, double? Mbps, int? MaxDisks)> GetVmSizeLimitsAsync(string subscriptionId, string location, string vmSize, CancellationToken ct = default)
    {
        var key = $"{location}|{vmSize}";
        if (SizeCache.TryGetValue(key, out var cached)) return cached;
        try
        {
            using var doc = await SendAsync(HttpMethod.Get,
                $"/subscriptions/{subscriptionId}/providers/Microsoft.Compute/skus?api-version=2021-07-01&$filter=location eq '{location}'", null, ct);
            foreach (var s in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                if (Str(s, "resourceType") != "virtualMachines" || !Str(s, "name").Equals(vmSize, StringComparison.OrdinalIgnoreCase)) continue;
                double? iops = null, mbps = null;
                int? maxDisks = null;
                if (s.TryGetProperty("capabilities", out var caps))
                    foreach (var c in caps.EnumerateArray())
                    {
                        var name = Str(c, "name");
                        if (!double.TryParse(Str(c, "value"), System.Globalization.CultureInfo.InvariantCulture, out var val)) continue;
                        if (name == "UncachedDiskIOPS") iops = val;
                        else if (name == "UncachedDiskBytesPerSecond") mbps = val / 1048576.0;
                        else if (name == "MaxDataDiskCount") maxDisks = (int)val;
                    }
                return SizeCache[key] = (iops, mbps, maxDisks);
            }
        }
        catch (InvalidOperationException) { /* limits are a bonus; the sync still works without them */ }
        return SizeCache[key] = (null, null, null);
    }

    /// <summary>Monthly retail price for a Premium/Standard SSD or Standard HDD tier (Azure public price list, no sign-in).</summary>
    public static Task<double?> GetMonthlyDiskPriceAsync(AzureDisk disk, string currencyCode, CancellationToken ct = default) =>
        GetMonthlyTierPriceAsync(disk.SkuName, disk.Tier, disk.Location, currencyCode, ct);

    public static async Task<double?> GetMonthlyTierPriceAsync(string skuName, string tier, string location, string currencyCode, CancellationToken ct = default)
    {
        var (product, redundancy) = skuName switch
        {
            var s when s.StartsWith("Premium_", StringComparison.OrdinalIgnoreCase) => ("Premium SSD Managed Disks", s[8..]),
            var s when s.StartsWith("StandardSSD_", StringComparison.OrdinalIgnoreCase) => ("Standard SSD Managed Disks", s[12..]),
            var s when s.StartsWith("Standard_", StringComparison.OrdinalIgnoreCase) => ("Standard HDD Managed Disks", s[9..]),
            _ => ("", "")
        };
        if (product == "" || string.IsNullOrEmpty(tier)) return null;  // Premium SSD v2 / Ultra are billed per GiB, IOPS and MB/s
        var sku = $"{tier} {redundancy}";
        var cacheKey = $"{location}|{sku}|{product}|{currencyCode}";
        lock (PriceCache) if (PriceCache.TryGetValue(cacheKey, out var hit)) return hit;
        var url = $"https://prices.azure.com/api/retail/prices?currencyCode='{currencyCode}'&$filter=serviceName eq 'Storage' and armRegionName eq '{location}' and skuName eq '{sku}' and productName eq '{product}' and priceType eq 'Consumption'";
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
            foreach (var item in doc.RootElement.GetProperty("Items").EnumerateArray())
                if (Str(item, "meterName") == $"{sku} Disk" && Str(item, "unitOfMeasure") == "1/Month")
                {
                    var price = item.GetProperty("retailPrice").GetDouble();
                    lock (PriceCache) PriceCache[cacheKey] = price;
                    return price;
                }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        return null;
    }

    private static readonly Dictionary<string, double?> PriceCache = [];

    public static string CurrencyCode(string symbol) => symbol.Trim() switch
    {
        "£" => "GBP", "€" => "EUR", "$" => "USD", "¥" => "JPY", "kr" => "SEK", "A$" => "AUD", "C$" => "CAD", "CHF" => "CHF",
        var s when s.Length == 3 && s.All(char.IsLetter) => s.ToUpperInvariant(),
        _ => "USD"
    };

    public record AzureTenant(string Id, string Name, string Domain)
    {
        public override string ToString() => string.IsNullOrEmpty(Domain) ? $"{Name}  ({Id})" : $"{Name}  ({Domain})";
    }

    /// <summary>
    /// A tenant must be a GUID or a domain (contoso.onmicrosoft.com, contoso.com). Display names such as "Contoso" are rejected
    /// with an explanation rather than sent to Azure. Returns null when the value is fine (blank means the default tenant).
    /// </summary>
    public static string? ValidateTenant(string? tenant)
    {
        var t = tenant?.Trim() ?? "";
        if (t.Length == 0 || Guid.TryParse(t, out _)) return null;
        if (t.Contains('.') && !t.Contains(' ') && !t.Contains('/')) return null;
        return $"'{t}' isn't a tenant ID. Use the tenant's GUID or a domain such as {t.ToLowerInvariant().Replace(" ", "")}.onmicrosoft.com, " +
               "or leave it blank to use your default tenant. After signing in you can pick from your tenants.";
    }

    /// <summary>A one-line explanation from an Azure or Azure CLI error, without stack traces.</summary>
    public static string FriendlyError(Exception ex)
    {
        var text = ex.Message;
        var aad = System.Text.RegularExpressions.Regex.Match(text, @"AADSTS\d+:[^.\r\n]*\.?");
        if (aad.Success) return aad.Value.Trim();
        if (text.Contains("az login", StringComparison.OrdinalIgnoreCase) || text.Contains("Please run 'az login'", StringComparison.OrdinalIgnoreCase))
            return "The Azure CLI isn't signed in. Run az login in a terminal, then try again.";
        if (text.Contains("Azure CLI not installed", StringComparison.OrdinalIgnoreCase))
            return "The Azure CLI isn't installed. Install it, or use browser sign-in instead.";
        var cut = text.IndexOfAny(['\r', '\n']);
        if (cut > 0) text = text[..cut];
        foreach (var marker in new[] { " See the troubleshooting guide", " Traceback", " ERROR:" })
        {
            var i = text.IndexOf(marker, StringComparison.Ordinal);
            if (i > 20) text = text[..i];
        }
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    /// <summary>Tenants the signed-in account belongs to.</summary>
    public async Task<List<AzureTenant>> GetTenantsAsync(CancellationToken ct = default)
    {
        var result = new List<AzureTenant>();
        using var doc = await SendAsync(HttpMethod.Get, "/tenants?api-version=2022-12-01", null, ct);
        foreach (var t in doc.RootElement.GetProperty("value").EnumerateArray())
            result.Add(new AzureTenant(Str(t, "tenantId"), Str(t, "displayName") is { Length: > 0 } n ? n : Str(t, "tenantId"), Str(t, "defaultDomain")));
        return result.OrderBy(t => t.Name).ToList();
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static string Path2(JsonElement e, string a, string b) => e.TryGetProperty(a, out var x) ? Str(x, b) : "";
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    private static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
