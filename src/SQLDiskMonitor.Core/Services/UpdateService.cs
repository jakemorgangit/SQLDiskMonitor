using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace SQLDiskMonitor.Services;

public record ReleaseInfo(Version Version, string Tag, string Name, string Notes, string HtmlUrl, string? MsiUrl, string? MsiName, long MsiSize);

/// <summary>Checks the GitHub repository for a newer release and installs its MSI.</summary>
public static class UpdateService
{
    public const string Repo = "jakemorgangit/SQLDiskMonitor";
    public static string RepoUrl => $"https://github.com/{Repo}";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"SQLDiskMonitor/{CurrentVersion}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static Version? ParseTag(string tag)
    {
        var t = tag.TrimStart('v', 'V').Split('-', '+')[0];
        if (!t.Contains('.')) t += ".0";
        return Version.TryParse(t, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    /// <summary>Returns the latest release if it is newer than the running version, otherwise null.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseTag(tag);
        if (version == null || version <= CurrentVersion) return null;

        string? url = null, name = null;
        long size = 0;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var n = a.GetProperty("name").GetString() ?? "";
            if (!n.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) continue;
            url = a.GetProperty("browser_download_url").GetString();
            name = n;
            size = a.GetProperty("size").GetInt64();
            break;
        }
        return new ReleaseInfo(version, tag, root.GetProperty("name").GetString() ?? tag,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            root.GetProperty("html_url").GetString() ?? RepoUrl, url, name, size);
    }

    public static async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (release.MsiUrl == null) throw new InvalidOperationException("This release has no MSI installer attached.");
        var dir = Path.Combine(Path.GetTempPath(), "SQLDiskMonitor-update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, release.MsiName ?? $"SQLDiskMonitor-{release.Version}.msi");

        using var resp = await Http.GetAsync(release.MsiUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? release.MsiSize;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress?.Report((double)read / total);
        }
        return path;
    }

    /// <summary>Runs the MSI (which upgrades in place) and relaunches the app once it finishes. The caller should then exit.</summary>
    public static void InstallAndRestart(string msiPath)
    {
        var exe = Environment.ProcessPath ?? "";
        var log = Path.Combine(Path.GetDirectoryName(msiPath)!, "install.log");
        var cmd = $"/c start \"\" /wait msiexec.exe /i \"{msiPath}\" /passive /norestart /l*v \"{log}\" & start \"\" \"{exe}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false });
    }
}
