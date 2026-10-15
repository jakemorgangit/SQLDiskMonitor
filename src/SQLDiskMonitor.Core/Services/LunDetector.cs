using System.Management;

namespace SQLDiskMonitor.Services;

public record DetectedDisk(string AccessPath, int DiskNumber, int? Lun, string Model, double SizeGb, string Location);

/// <summary>
/// Maps volumes to physical disks and SCSI LUNs on the SQL Server host over WMI (Storage + CIMv2 namespaces).
/// Needs the current Windows user to be allowed remote WMI on the host (normally local admin).
/// </summary>
public static class LunDetector
{
    public static Task<List<DetectedDisk>> DetectAsync(string host, CancellationToken ct = default) => Task.Run(() => Detect(host), ct);

    private static List<DetectedDisk> Detect(string host)
    {
        bool local = string.IsNullOrWhiteSpace(host) || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) || host is "." or "localhost";
        string Root(string ns) => local ? $@"\\.\{ns}" : $@"\\{host}\{ns}";
        var opts = new ConnectionOptions { Impersonation = ImpersonationLevel.Impersonate, Authentication = AuthenticationLevel.PacketPrivacy, Timeout = TimeSpan.FromSeconds(20) };

        // Win32_DiskDrive gives the SCSI LUN by disk index.
        var cim = new ManagementScope(Root(@"root\cimv2"), opts);
        cim.Connect();
        var drives = new Dictionary<int, (int? Lun, string Model, double SizeGb)>();
        using (var s = new ManagementObjectSearcher(cim, new ObjectQuery("SELECT Index, SCSILogicalUnit, Model, Size FROM Win32_DiskDrive")))
        {
            foreach (ManagementObject d in s.Get())
            {
                int idx = Convert.ToInt32(d["Index"]);
                int? lun = d["SCSILogicalUnit"] is { } l ? Convert.ToInt32(l) : null;
                double size = d["Size"] is { } sz ? Convert.ToDouble(sz) / 1073741824.0 : 0;
                drives[idx] = (lun, d["Model"]?.ToString() ?? "", size);
            }
        }

        // MSFT_Partition gives every access path (drive letters and mount points) for each disk.
        var storage = new ManagementScope(Root(@"root\Microsoft\Windows\Storage"), opts);
        storage.Connect();
        var locations = new Dictionary<int, string>();
        using (var s = new ManagementObjectSearcher(storage, new ObjectQuery("SELECT Number, Location FROM MSFT_Disk")))
            foreach (ManagementObject d in s.Get())
                locations[Convert.ToInt32(d["Number"])] = d["Location"]?.ToString() ?? "";

        var result = new List<DetectedDisk>();
        using (var s = new ManagementObjectSearcher(storage, new ObjectQuery("SELECT DiskNumber, AccessPaths FROM MSFT_Partition")))
        {
            foreach (ManagementObject p in s.Get())
            {
                int disk = Convert.ToInt32(p["DiskNumber"]);
                if (p["AccessPaths"] is not string[] paths) continue;
                drives.TryGetValue(disk, out var info);
                var lun = info.Lun ?? ParseLun(locations.GetValueOrDefault(disk, ""));
                foreach (var path in paths.Where(x => !x.StartsWith(@"\\?\")))
                    result.Add(new DetectedDisk(SqlSource.NormaliseVolume(path), disk, lun, info.Model ?? "", info.SizeGb, locations.GetValueOrDefault(disk, "")));
            }
        }
        return result;
    }

    /// <summary>Parses "... : LUN 3" style locations from MSFT_Disk.</summary>
    private static int? ParseLun(string location)
    {
        var i = location.LastIndexOf("LUN", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var digits = new string(location[(i + 3)..].SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : null;
    }
}
