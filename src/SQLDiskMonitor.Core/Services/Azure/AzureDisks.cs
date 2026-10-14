using System.Globalization;
using SQLDiskMonitor.Models;

namespace SQLDiskMonitor.Services.Azure;

/// <summary>The Azure VM a SQL Server instance runs on, saved with the server.</summary>
public class AzureVmLink
{
    public string VmId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ResourceGroup { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public string SubscriptionName { get; set; } = "";
    public string Location { get; set; } = "";
    public string ComputerName { get; set; } = "";
    public string VmSize { get; set; } = "";
    public double? MaxIops { get; set; }
    public double? MaxMbps { get; set; }
    public int? MaxDataDisks { get; set; }
    public int DataDisksAttached { get; set; }
    public DateTime SyncedAt { get; set; }

    public string CapText => MaxIops is { } i && MaxMbps is { } m ? $"{i:N0} IOPS · {m:N0} MB/s" : "unknown";
    public string SlotsText => MaxDataDisks is { } max ? $"{DataDisksAttached} of {max} data disks attached" : $"{DataDisksAttached} data disks attached";
    public string PortalUrl => $"https://portal.azure.com/#@/resource{VmId}";
}

/// <summary>Everything worth knowing about one Azure managed disk, saved with the volume it backs.</summary>
public class AzureDiskInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int? Lun { get; set; }
    public bool IsOsDisk { get; set; }
    public string SkuName { get; set; } = "";
    /// <summary>Performance tier (can be set above the size's own tier).</summary>
    public string Tier { get; set; } = "";
    /// <summary>The tier the disk's size alone gives.</summary>
    public string BaseTier { get; set; } = "";
    public double SizeGb { get; set; }
    public double Iops { get; set; }
    public double Mbps { get; set; }
    public double BurstIops { get; set; }
    public double BurstMbps { get; set; }
    public string BurstType { get; set; } = "";
    public string Caching { get; set; } = "";
    public bool WriteAccelerator { get; set; }
    public string Zone { get; set; } = "";
    public string Encryption { get; set; } = "";
    public string NetworkAccess { get; set; } = "";
    public int MaxShares { get; set; } = 1;
    public DateTime? Created { get; set; }
    public string Location { get; set; } = "";
    public double? MonthlyPrice { get; set; }
    /// <summary>Azure resource tags on the disk.</summary>
    public Dictionary<string, string> Tags { get; set; } = [];
    public string NextTier { get; set; } = "";
    public double? NextTierSizeGb { get; set; }
    public double? NextTierIops { get; set; }
    public double? NextTierMbps { get; set; }
    public double? NextTierPrice { get; set; }
    public string Expandable { get; set; } = "";
    public string PerformanceTierChange { get; set; } = "";
    public DateTime SyncedAt { get; set; }

    public string SkuText => AzureTiers.SkuDisplay(SkuName);
    public string TierText => string.IsNullOrEmpty(Tier) ? SkuText
        : BaseTier != "" && BaseTier != Tier ? $"{Tier} performance on a {BaseTier}-sized disk" : Tier;
    public string LimitsText => $"{Iops:N0} IOPS · {Mbps:N0} MB/s" + (BurstIops > Iops ? $" · bursts to {BurstIops:N0} / {BurstMbps:N0} ({BurstType})" : "");
    public string SizeText => AzureTiers.SizeText(SizeGb);
    public string NextTierText => NextTier == "" ? "—"
        : $"{NextTier} · {AzureTiers.SizeText(NextTierSizeGb ?? 0)}" +
          (NextTierIops is { } i ? $" · {i:N0} IOPS · {NextTierMbps:N0} MB/s" : "") +
          (NextTierPrice is { } p && MonthlyPrice is { } cur ? $" · {(p - cur >= 0 ? "+" : "")}{p - cur:N2}/month" : "");
    public string LunText => IsOsDisk ? "OS disk" : Lun is { } l ? $"LUN {l}" : "";
    public string Summary => $"{Name} ({LunText}, {Tier} {SizeText})";
    public string OptionsText => string.Join(" · ", new[]
    {
        $"host cache: {(string.IsNullOrEmpty(Caching) ? "None" : Caching)}",
        WriteAccelerator ? "write accelerator: on" : "",
        Zone != "" ? $"zone {Zone}" : "",
        MaxShares > 1 ? $"shared ({MaxShares} hosts)" : "",
        Encryption != "" ? Encryption.Replace("EncryptionAtRestWith", "encrypted: ") : "",
        NetworkAccess != "" ? $"network: {NetworkAccess}" : "",
        Created is { } c ? $"created {c:dd MMM yyyy}" : ""
    }.Where(s => s != ""));
}

public record AzureTier(string Name, double SizeGb, double? Iops, double? Mbps);

/// <summary>Published Azure managed disk tiers, for the base tier, next size up and bursting.</summary>
public static class AzureTiers
{
    public static readonly AzureTier[] Premium =
    [
        new("P1", 4, 120, 25), new("P2", 8, 120, 25), new("P3", 16, 120, 25), new("P4", 32, 120, 25), new("P6", 64, 240, 50),
        new("P10", 128, 500, 100), new("P15", 256, 1100, 125), new("P20", 512, 2300, 150), new("P30", 1024, 5000, 200),
        new("P40", 2048, 7500, 250), new("P50", 4096, 7500, 250), new("P60", 8192, 16000, 500), new("P70", 16384, 18000, 750),
        new("P80", 32767, 20000, 900),
    ];

    // Standard tiers: sizes only (their limits vary by size band and are taken from the disk itself).
    public static readonly AzureTier[] StandardSsd =
        new (string, double)[] { ("E1", 4), ("E2", 8), ("E3", 16), ("E4", 32), ("E6", 64), ("E10", 128), ("E15", 256), ("E20", 512), ("E30", 1024),
            ("E40", 2048), ("E50", 4096), ("E60", 8192), ("E70", 16384), ("E80", 32767) }.Select(t => new AzureTier(t.Item1, t.Item2, null, null)).ToArray();
    public static readonly AzureTier[] StandardHdd =
        new (string, double)[] { ("S4", 32), ("S6", 64), ("S10", 128), ("S15", 256), ("S20", 512), ("S30", 1024), ("S40", 2048), ("S50", 4096),
            ("S60", 8192), ("S70", 16384), ("S80", 32767) }.Select(t => new AzureTier(t.Item1, t.Item2, null, null)).ToArray();

    public static AzureTier[]? Family(string sku) => sku switch
    {
        var s when s.StartsWith("Premium_", StringComparison.OrdinalIgnoreCase) => Premium,
        var s when s.StartsWith("StandardSSD_", StringComparison.OrdinalIgnoreCase) => StandardSsd,
        var s when s.StartsWith("Standard_", StringComparison.OrdinalIgnoreCase) => StandardHdd,
        _ => null
    };

    public static bool IsV2OrUltra(string sku) =>
        sku.StartsWith("PremiumV2_", StringComparison.OrdinalIgnoreCase) || sku.StartsWith("UltraSSD_", StringComparison.OrdinalIgnoreCase);

    public static string SkuDisplay(string sku)
    {
        var parts = sku.Split('_');
        var kind = parts[0] switch
        {
            "Premium" => "Premium SSD",
            "PremiumV2" => "Premium SSD v2",
            "UltraSSD" => "Ultra Disk",
            "StandardSSD" => "Standard SSD",
            "Standard" => "Standard HDD",
            _ => parts[0]
        };
        return parts.Length > 1 ? $"{kind} ({parts[1]})" : kind;
    }

    /// <summary>Tier name in the app's preset style, e.g. "Azure P30" or "Azure Premium SSD v2".</summary>
    public static string TierName(string sku, string tier) =>
        sku.StartsWith("PremiumV2_", StringComparison.OrdinalIgnoreCase) ? "Azure Premium SSD v2"
        : sku.StartsWith("UltraSSD_", StringComparison.OrdinalIgnoreCase) ? "Azure Ultra Disk"
        : string.IsNullOrEmpty(tier) ? $"Azure {SkuDisplay(sku)}" : $"Azure {tier}";

    public static string SizeText(double gb) =>
        gb >= 1024 ? $"{gb / 1024:0.##} TiB" : $"{gb:N0} GiB";

    public static AzureTier? BaseTier(string sku, double sizeGb) => Family(sku)?.FirstOrDefault(t => t.SizeGb >= sizeGb);

    /// <summary>The next tier up from whichever is higher of the size tier and the performance tier.</summary>
    public static AzureTier? NextTier(string sku, string perfTier, double sizeGb)
    {
        var family = Family(sku);
        if (family == null) return null;
        int baseIdx = Array.FindIndex(family, t => t.SizeGb >= sizeGb);
        int perfIdx = Array.FindIndex(family, t => t.Name.Equals(perfTier, StringComparison.OrdinalIgnoreCase));
        int idx = Math.Max(baseIdx, perfIdx);
        return idx >= 0 && idx + 1 < family.Length ? family[idx + 1] : null;
    }

    public static (double Iops, double Mbps, string Type) Burst(AzureDisk d)
    {
        if (d.SkuName.StartsWith("Premium_", StringComparison.OrdinalIgnoreCase))
        {
            if (d.SizeGb <= 512) return (Math.Max(d.Iops, 3500), Math.Max(d.Mbps, 170), "credit-based");
            if (d.BurstingEnabled) return (Math.Max(d.Iops, 30000), Math.Max(d.Mbps, 1000), "on-demand");
        }
        else if (d.SkuName.StartsWith("StandardSSD_", StringComparison.OrdinalIgnoreCase) && d.SizeGb <= 1024)
        {
            return (Math.Max(d.Iops, 600), Math.Max(d.Mbps, 150), "credit-based");
        }
        return (0, 0, "");
    }

    public static string Expandable(AzureDisk d)
    {
        double max = IsV2OrUltra(d.SkuName) ? 65536 : 32767;
        if (d.MaxShares > 1) return "No: shared disk (detach from all VMs to resize)";
        if (d.SizeGb >= max) return "No: already at the maximum size";
        if (d.IsOsDisk) return "Yes, with the VM deallocated";
        if (IsV2OrUltra(d.SkuName)) return "Yes, online; IOPS and MB/s can also be changed independently";
        return "Yes, online (data disk)";
    }

    public static string PerformanceTierChange(AzureDisk d) =>
        d.SkuName.StartsWith("Premium_", StringComparison.OrdinalIgnoreCase)
            ? "Performance tier can be raised without resizing (downgrades allowed after 12 hours)"
            : IsV2OrUltra(d.SkuName) ? "IOPS and MB/s are provisioned separately and can be changed" : "";
}

public record DiskMatch(AzureDiskInfo Disk, string? Volume, string Reason);

/// <summary>Turns Azure disk details into AzureDiskInfo, matches disks to Windows volumes and applies them to disk settings.</summary>
public static class AzureSync
{
    public static async Task<List<AzureDiskInfo>> BuildInfosAsync(AzureVmDetail detail, string currencyCode, CancellationToken ct = default)
    {
        var result = new List<AzureDiskInfo>();
        foreach (var d in detail.Disks)
        {
            var next = AzureTiers.NextTier(d.SkuName, d.Tier, d.SizeGb);
            var price = await AzureClient.GetMonthlyDiskPriceAsync(d, currencyCode, ct);
            var nextPrice = next == null ? null : await AzureClient.GetMonthlyTierPriceAsync(d.SkuName, next.Name, d.Location, currencyCode, ct);
            result.Add(ToInfo(d, price, nextPrice));
        }
        return result;
    }

    public static AzureDiskInfo ToInfo(AzureDisk d, double? price, double? nextPrice)
    {
        var (bIops, bMbps, bType) = AzureTiers.Burst(d);
        var baseTier = AzureTiers.BaseTier(d.SkuName, d.SizeGb);
        var next = AzureTiers.NextTier(d.SkuName, d.Tier, d.SizeGb);
        return new AzureDiskInfo
        {
            Id = d.Id, Name = d.Name, Lun = d.Lun, IsOsDisk = d.IsOsDisk, SkuName = d.SkuName, Tier = d.Tier, BaseTier = baseTier?.Name ?? "",
            SizeGb = d.SizeGb, Iops = d.Iops, Mbps = d.Mbps, BurstIops = bIops, BurstMbps = bMbps, BurstType = bType,
            Caching = d.Caching, WriteAccelerator = d.WriteAccelerator, Zone = d.Zone, Encryption = d.Encryption, NetworkAccess = d.NetworkAccess,
            MaxShares = d.MaxShares, Created = d.Created, Location = d.Location, MonthlyPrice = price,
            NextTier = next?.Name ?? "", NextTierSizeGb = next?.SizeGb,
            NextTierIops = next?.Iops, NextTierMbps = next?.Mbps, NextTierPrice = nextPrice,
            Expandable = AzureTiers.Expandable(d), PerformanceTierChange = AzureTiers.PerformanceTierChange(d), SyncedAt = DateTime.Now,
            Tags = d.Tags ?? []
        };
    }

    /// <summary>
    /// Suggests which volume each Azure disk backs, most reliable rule first:
    /// previous sync, OS disk → C:, recorded LUN, recorded pool LUNs, a disk tag naming the volume, the volume's label or
    /// drive name inside the disk name, a unique size, an equal-sized storage pool, then Windows disk order for disks of
    /// the same size. The Azure temporary disk ("Temporary Storage") is never a managed disk, so it is skipped.
    /// </summary>
    public static List<DiskMatch> Suggest(IReadOnlyList<AzureDiskInfo> azure, IReadOnlyList<DiskConfig> volumes, IReadOnlyDictionary<string, double> volumeSizeGb)
    {
        var assigned = new Dictionary<string, (string Volume, string Reason)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = volumes.Where(v => !v.Label.Equals("Temporary Storage", StringComparison.OrdinalIgnoreCase)).ToList();

        void Assign(AzureDiskInfo a, DiskConfig v, string reason, bool exclusive = true)
        {
            assigned[a.Id] = (v.Volume, reason);
            if (exclusive) used.Add(v.Volume);
        }
        IEnumerable<AzureDiskInfo> Free() => azure.Where(a => !assigned.ContainsKey(a.Id) && !a.IsOsDisk);
        IEnumerable<DiskConfig> Open() => candidates.Where(v => !used.Contains(v.Volume) && v.Volume != "C:\\");

        // 1. Previous sync
        foreach (var a in azure)
        {
            var prev = candidates.FirstOrDefault(v => v.AzureDisks.Any(x => x.Id.Equals(a.Id, StringComparison.OrdinalIgnoreCase)));
            if (prev != null) Assign(a, prev, "previous sync");
        }

        // 2. OS disk
        var os = azure.FirstOrDefault(a => a.IsOsDisk);
        if (os != null && !assigned.ContainsKey(os.Id) && candidates.FirstOrDefault(v => v.Volume == "C:\\") is { } c && !used.Contains("C:\\"))
            Assign(os, c, "OS disk");

        // 3. Recorded LUN, 4. recorded pool LUNs
        foreach (var a in Free().Where(a => a.Lun != null).ToList())
        {
            var byLun = Open().Where(v => v.Lun == a.Lun && v.PoolLunList.Count == 0).ToList();
            if (byLun.Count == 1) { Assign(a, byLun[0], $"LUN {a.Lun}"); continue; }
            var pools = candidates.Where(v => v.PoolLunList.Contains(a.Lun!.Value)).ToList();
            if (pools.Count == 1) Assign(a, pools[0], "pool LUNs", exclusive: false);
        }
        foreach (var v in candidates.Where(v => v.PoolLunList.Count > 0 && assigned.Values.Any(x => x.Volume == v.Volume))) used.Add(v.Volume);

        // 5. A disk tag that names the volume (drive letter, mount point, label or drive name)
        foreach (var a in Free().Where(a => a.Tags.Count > 0).ToList())
        {
            var values = a.Tags.Values.Select(Norm).Where(s => s.Length > 0).ToHashSet();
            var hits = Open().Where(v => values.Contains(Norm(v.Volume)) || values.Contains(Norm(v.Volume.TrimEnd('\\', ':')))
                                         || (Norm(v.Label).Length >= 3 && values.Contains(Norm(v.Label)))
                                         || (Norm(v.DriveName).Length >= 3 && values.Contains(Norm(v.DriveName)))).ToList();
            if (hits.Count == 1) Assign(a, hits[0], "disk tag");
        }

        // 6. The volume's label or drive name inside the Azure disk name (TicketerLive1 must not match TicketerLive10)
        foreach (var v in Open().ToList())
        {
            foreach (var key in new[] { v.Label, v.DriveName }.Select(Norm).Where(k => k.Length >= 4).Distinct())
            {
                var hits = Free().Where(a => NameContains(Norm(a.Name), key)).ToList();
                // The disk must not also match a different, longer volume name (e.g. "data" inside "data2").
                if (hits.Count == 1 && !Open().Any(o => o != v && new[] { o.Label, o.DriveName }.Select(Norm)
                        .Any(k2 => k2.Length > key.Length && NameContains(Norm(hits[0].Name), k2))))
                {
                    Assign(hits[0], v, "name");
                    break;
                }
            }
        }

        // 7. Unique size, 8. equal-sized storage pool
        static bool Close(double volumeGb, double diskGb) => volumeGb > 0 && diskGb >= volumeGb && diskGb - volumeGb <= Math.Max(1, diskGb * 0.03);
        double Size(DiskConfig v) => volumeSizeGb.TryGetValue(v.Volume, out var s) ? s : 0;
        foreach (var v in Open().ToList())
        {
            var vol = Size(v);
            if (vol <= 0) continue;
            var single = Free().Where(a => Close(vol, a.SizeGb)).ToList();
            if (single.Count == 1 && Open().Count(o => Close(Size(o), single[0].SizeGb)) == 1)
            {
                Assign(single[0], v, "size");
                continue;
            }
            foreach (var group in Free().GroupBy(a => a.SizeGb).Where(g => g.Count() > 1))
            {
                if (!Close(vol, group.Key * group.Count())) continue;
                foreach (var a in group) Assign(a, v, $"storage pool of {group.Count()}", exclusive: false);
                used.Add(v.Volume);
                break;
            }
        }

        // 9. Same-sized disks: Windows usually numbers data disks in LUN order. Only used when disk numbers are known.
        foreach (var group in Free().Where(a => a.Lun != null).GroupBy(a => a.SizeGb).ToList())
        {
            var vols = Open().Where(v => v.DiskNumber != null && Close(Size(v), group.Key)).OrderBy(v => v.DiskNumber).ToList();
            var disks = group.OrderBy(a => a.Lun).ToList();
            if (vols.Count != disks.Count || vols.Count < 2) continue;
            for (int i = 0; i < vols.Count; i++) Assign(disks[i], vols[i], "disk order (check)");
        }

        return azure.Select(a => assigned.TryGetValue(a.Id, out var m) ? new DiskMatch(a, m.Volume, m.Reason) : new DiskMatch(a, null, "")).ToList();
    }

    private static string Norm(string? s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>True when <paramref name="key"/> appears in the name and isn't followed by another digit (live1 vs live10).</summary>
    private static bool NameContains(string name, string key)
    {
        for (int i = name.IndexOf(key, StringComparison.Ordinal); i >= 0; i = name.IndexOf(key, i + 1, StringComparison.Ordinal))
        {
            int after = i + key.Length;
            bool digitClash = char.IsDigit(key[^1]) && after < name.Length && char.IsDigit(name[after]);
            if (!digitClash) return true;
        }
        return false;
    }

    /// <summary>
    /// Writes Azure facts onto a volume's settings. Azure owns disk name, LUN, tier, limits, size, device and cost;
    /// label, drive name, notes and your own tags are left alone. Returns a change-log line when the tier or limits changed.
    /// </summary>
    public static string? Apply(DiskConfig d, IReadOnlyList<AzureDiskInfo> disks, bool setCost)
    {
        if (disks.Count == 0) return null;
        var oldTier = d.Tier; var oldIops = d.MaxIops; var oldMbps = d.MaxMbps; var oldSize = d.SizeGb;
        bool previouslySynced = d.HasAzure;
        bool pool = disks.Count > 1;
        var first = disks[0];

        d.DiskName = string.Join(", ", disks.Select(x => x.Name));
        if (!pool && first.Lun != null) d.Lun = first.Lun;
        var tiers = disks.Select(x => AzureTiers.TierName(x.SkuName, x.Tier)).Distinct().ToList();
        d.Tier = pool ? tiers.Count == 1 ? $"{tiers[0]} ×{disks.Count}" : "Azure (mixed pool)" : tiers[0];
        d.MaxIops = disks.Sum(x => x.Iops);
        d.MaxMbps = disks.Sum(x => x.Mbps);
        d.BurstIops = disks.Any(x => x.BurstIops > 0) ? disks.Sum(x => Math.Max(x.BurstIops, x.Iops)) : 0;
        d.BurstMbps = disks.Any(x => x.BurstMbps > 0) ? disks.Sum(x => Math.Max(x.BurstMbps, x.Mbps)) : 0;
        d.SizeGb = disks.Sum(x => x.SizeGb);
        d.DeviceModel = pool
            ? $"Storage pool of {disks.Count} Azure disks (LUN {string.Join(", ", disks.Where(x => x.Lun != null).Select(x => x.Lun))})"
            : $"Azure {first.SkuText}";
        if (setCost && disks.All(x => x.MonthlyPrice != null)) d.CostMonthly = Math.Round(disks.Sum(x => x.MonthlyPrice!.Value), 2);

        // Host caching and write accelerator are Azure facts; keep the user's other tags.
        var tags = d.TagList.Where(t => !t.StartsWith("host cache:", StringComparison.OrdinalIgnoreCase)
                                     && !t.StartsWith("write accelerator:", StringComparison.OrdinalIgnoreCase)).ToList();
        var caching = disks.Select(x => string.IsNullOrEmpty(x.Caching) ? "None" : x.Caching).Distinct().ToList();
        tags.Add($"host cache: {string.Join("/", caching)}");
        if (disks.Any(x => x.WriteAccelerator)) tags.Add("write accelerator: on");
        d.Tags = string.Join(", ", tags);
        d.AzureDisks = disks.ToList();

        var changes = new List<string>();
        if (previouslySynced || oldIops > 0 || !string.IsNullOrEmpty(oldTier))
        {
            if (!string.IsNullOrEmpty(oldTier) && oldTier != d.Tier) changes.Add($"{oldTier} → {d.Tier}");
            if (oldIops > 0 && Math.Abs(oldIops - d.MaxIops) > 0.5) changes.Add($"{oldIops:N0} → {d.MaxIops:N0} IOPS");
            if (oldMbps > 0 && Math.Abs(oldMbps - d.MaxMbps) > 0.5) changes.Add($"{oldMbps:N0} → {d.MaxMbps:N0} MB/s");
            if (oldSize > 0 && Math.Abs(oldSize - d.SizeGb) >= 1) changes.Add($"{oldSize.ToString("N0", CultureInfo.CurrentCulture)} → {d.SizeGb:N0} GB");
        }
        return changes.Count > 0 ? "Azure: " + string.Join("; ", changes) : null;
    }
}
