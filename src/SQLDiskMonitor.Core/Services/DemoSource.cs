namespace SQLDiskMonitor.Services;

/// <summary>
/// Synthetic SQL Server with a realistic daily workload: business-hours OLTP, an overnight ETL that hammers tempdb,
/// and a 22:00 full backup that saturates the data disk's throughput. Lets people explore the app without a server.
/// </summary>
public class DemoSource : ISnapshotSource
{
    private record DemoFile(int Db, int File, string DbName, string Logical, string Type, string Volume, string Label, double TotalGb,
        double SizeMb, double ReadWeight, double WriteWeight, double ReadKb, double WriteKb, double BaseLatency);

    private static readonly DemoFile[] Files =
    [
        new(1, 1, "master", "master", "ROWS", "C:\\", "OS", 127, 6, 0.002, 0.002, 64, 8, 4),
        new(1, 2, "master", "mastlog", "LOG", "C:\\", "OS", 127, 2, 0.0, 0.002, 64, 4, 3),
        new(4, 1, "msdb", "MSDBData", "ROWS", "C:\\", "OS", 127, 120, 0.004, 0.004, 64, 8, 4),
        new(4, 2, "msdb", "MSDBLog", "LOG", "C:\\", "OS", 127, 40, 0.0, 0.004, 64, 4, 3),
        new(5, 1, "SalesDB", "SalesDB", "ROWS", "D:\\", "SQLData", 1024, 412_000, 0.46, 0.20, 64, 8, 5),
        new(5, 3, "SalesDB", "SalesDB_Archive", "ROWS", "D:\\", "SQLData", 1024, 210_000, 0.10, 0.02, 128, 8, 6),
        new(5, 2, "SalesDB", "SalesDB_log", "LOG", "L:\\", "SQLLogs", 512, 48_000, 0.0, 0.55, 60, 12, 1.2),
        new(6, 1, "Warehouse", "Warehouse", "ROWS", "D:\\", "SQLData", 1024, 298_000, 0.30, 0.10, 64, 64, 6),
        new(6, 2, "Warehouse", "Warehouse_log", "LOG", "L:\\", "SQLLogs", 512, 31_000, 0.0, 0.25, 60, 32, 1.5),
        new(7, 1, "HR", "HR", "ROWS", "D:\\", "SQLData", 1024, 18_000, 0.05, 0.02, 64, 8, 5),
        new(7, 2, "HR", "HR_log", "LOG", "D:\\", "SQLData", 1024, 4_000, 0.0, 0.03, 60, 4, 3),
        new(2, 1, "tempdb", "tempdev", "ROWS", "T:\\", "TempDB", 128, 16_384, 0.03, 0.05, 64, 64, 1),
        new(2, 3, "tempdb", "temp2", "ROWS", "T:\\", "TempDB", 128, 16_384, 0.03, 0.05, 64, 64, 1),
        new(2, 4, "tempdb", "temp3", "ROWS", "T:\\", "TempDB", 128, 16_384, 0.03, 0.05, 64, 64, 1),
        new(2, 5, "tempdb", "temp4", "ROWS", "T:\\", "TempDB", 128, 16_384, 0.03, 0.05, 64, 64, 1),
        new(2, 2, "tempdb", "templog", "LOG", "T:\\", "TempDB", 128, 8_192, 0.0, 0.04, 60, 16, 1),
    ];

    private readonly Dictionary<(int, int), RawFile> _counters = [];
    private readonly Dictionary<string, (long Tasks, long Ms)> _waits = [];
    private readonly Random _rng = new(42);
    private DateTime _last;

    public DemoSource()
    {
        foreach (var f in Files)
        {
            var free = f.Volume switch { "C:\\" => 0.42, "D:\\" => 0.16, "L:\\" => 0.61, _ => 0.07 };
            _counters[(f.Db, f.File)] = new RawFile
            {
                DatabaseId = f.Db, FileId = f.File, DatabaseName = f.DbName, LogicalName = f.Logical, TypeDesc = f.Type,
                PhysicalName = $"{f.Volume}{(f.Volume == "C:\\" ? "Program Files\\Microsoft SQL Server\\MSSQL16.MSSQLSERVER\\MSSQL\\DATA\\" : "SQL\\")}{f.Logical}.{(f.Type == "LOG" ? "ldf" : f.File == 1 ? "mdf" : "ndf")}",
                SizePages = (long)(f.SizeMb * 128), MaxSize = f.Type == "LOG" ? 268435456 : -1,
                Growth = f.DbName == "HR" ? 10 : f.Logical == "SalesDB" ? 131072 : f.Logical == "Warehouse" ? 65536 : 8192, IsPercentGrowth = f.DbName == "HR",
                Volume = f.Volume, VolumeLabel = f.Label,
                TotalBytes = (long)(f.TotalGb * 1024 * 1024 * 1024), AvailableBytes = (long)(f.TotalGb * free * 1024 * 1024 * 1024)
            };
        }
        foreach (var w in SqlSource.IoWaits) _waits[w] = (0, 0);
        foreach (var f in Files)
            _used[(f.Db, f.File)] = f.SizeMb * 128 * (f.Type == "LOG" ? 0.25 : f.Db == 2 ? 0.15 : 0.968);
    }

    private readonly Dictionary<(int, int), double> _used = [];

    // Data growth in MB/day: SalesDB grows steadily, Warehouse a bit slower, the rest barely.
    private static double GrowthMbPerDay(DemoFile f) => f.Type == "LOG" || f.Db == 2 ? 0 : f.Logical switch
    {
        "SalesDB" => 1500, "Warehouse" => 700, "SalesDB_Archive" => 40, "HR" => 25, _ => 1
    };

    public Task<Dictionary<(int Db, int File), long>> CaptureSpaceUsedAsync(CancellationToken ct) =>
        Task.FromResult(CaptureSpaceUsed());

    public Dictionary<(int Db, int File), long> CaptureSpaceUsed() => _used.ToDictionary(kv => kv.Key, kv => (long)kv.Value);

    private void Grow(double secs)
    {
        foreach (var f in Files)
        {
            var key = (f.Db, f.File);
            var c = _counters[key];
            _used[key] += GrowthMbPerDay(f) * 128 / 86400 * secs;
            if (f.Type == "LOG") _used[key] = c.SizePages * (0.2 + 0.15 * _rng.NextDouble());
            // Autogrowth when the file is nearly full; the volume loses that space.
            while (_used[key] > c.SizePages * 0.97)
            {
                long grow = c.IsPercentGrowth ? c.SizePages * c.Growth / 100 : c.Growth;
                c.SizePages += grow;
                foreach (var other in _counters.Values.Where(o => o.Volume == c.Volume))
                    other.AvailableBytes -= grow * 8192;
            }
        }
    }

    public Task<RawSnapshot> CaptureAsync(CancellationToken ct) => Task.FromResult(Capture(DateTime.Now));

    /// <summary>Advances the simulated counters to <paramref name="now"/> and returns a snapshot.</summary>
    public RawSnapshot Capture(DateTime now)
    {
        if (_last != default)
        {
            var secs = (now - _last).TotalSeconds;
            if (secs > 0) { Advance(now, secs); Grow(secs); }
        }
        _last = now;

        return new RawSnapshot
        {
            Time = now,
            HostName = "DEMO-SQL01",
            Version = "16.0.4135.4 Enterprise Edition (64-bit) — demo data",
            Files = _counters.Values.Select(f => f.Clone()).ToList(),
            Waits = new Dictionary<string, (long, long)>(_waits)
        };
    }

    private void Advance(DateTime now, double secs)
    {
        double h = now.Hour + now.Minute / 60.0;
        bool weekend = now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        // OLTP: ramps up from 08:00, lunchtime dip, tails off after 18:00.
        double oltp = weekend ? 0.15 : Bell(h, 10.5, 2.2) * 0.9 + Bell(h, 15, 2.0) * 1.0 + 0.08;
        // ETL 01:00-03:30: heavy tempdb and Warehouse writes.
        double etl = h is >= 1 and < 3.5 ? 1.0 : 0;
        // Full backup at 22:00 for ~50 minutes: big sequential reads on D:.
        double backup = h is >= 22 and < 22.85 ? 1.0 : 0;
        // Month-end style Monday spike
        double monday = now.DayOfWeek == DayOfWeek.Monday ? 1.25 : 1.0;

        double noise() => 0.85 + _rng.NextDouble() * 0.3;

        // Total workload IOPS for the instance at this moment.
        double baseIops = 2000 * oltp * monday;
        double totalWaitMs = 0;

        // Demand per file first; each volume then delivers at most its (burst) limit and the excess shows up as latency.
        var plan = new List<(DemoFile F, double R, double W, double Rkb, double Wkb)>();
        foreach (var f in Files)
        {
            double readIops = baseIops * f.ReadWeight * noise();
            double writeIops = baseIops * f.WriteWeight * noise();
            double readKb = f.ReadKb, writeKb = f.WriteKb;
            if (etl > 0 && (f.Db == 2 || f.DbName == "Warehouse"))
            {
                readIops += 700 * noise() * (f.Db == 2 ? 0.6 : 1);
                writeIops += 1000 * noise() * (f.Db == 2 ? 0.7 : 1);
                readKb = 128; writeKb = 64;
            }
            if (backup > 0 && f.Type == "ROWS" && f.Volume == "D:\\")
            {
                readIops += 300 * noise() * (f.SizeMb / 400_000);
                readKb = 1024;
            }
            plan.Add((f, readIops, writeIops, readKb, writeKb));
        }

        foreach (var vol in plan.GroupBy(p => p.F.Volume))
        {
            var (capIops, capMbps) = vol.Key switch
            {
                "D:\\" => (5000.0, 200.0),
                "L:\\" => (3500.0, 170.0),
                "T:\\" => (3500.0, 170.0),
                _ => (3500.0, 170.0)
            };
            double iops = vol.Sum(p => p.R + p.W);
            double mbps = vol.Sum(p => p.R * p.Rkb + p.W * p.Wkb) / 1024;
            double demand = Math.Max(iops / capIops, mbps / capMbps);
            double scale = demand > 1 ? 1 / demand : 1;
            // Latency rises sharply as demand approaches and passes what the disk can deliver.
            double latFactor = 1 + Math.Pow(Math.Min(demand, 1.4), 6) * 2.5;

            foreach (var (f, r, w, rkb, wkb) in vol)
            {
                var c = _counters[(f.Db, f.File)];
                double readLat = f.BaseLatency * latFactor * noise();
                double writeLat = f.BaseLatency * (f.Type == "LOG" ? 1 : 1.3) * latFactor * noise();
                long dr = (long)(r * scale * secs), dw = (long)(w * scale * secs);
                c.Reads += dr;
                c.Writes += dw;
                c.BytesRead += (long)(dr * rkb * 1024);
                c.BytesWritten += (long)(dw * wkb * 1024);
                c.ReadStall += (long)(dr * readLat);
                c.WriteStall += (long)(dw * writeLat);
                c.Pending = demand > 0.9 ? _rng.Next(2, 12) : _rng.Next(0, 2);
                totalWaitMs += dr * readLat + dw * writeLat;

                if (f.Type == "LOG") Bump("WRITELOG", dw / 3, (long)(dw / 3 * writeLat));
                else Bump(_rng.NextDouble() > 0.2 ? "PAGEIOLATCH_SH" : "PAGEIOLATCH_EX", dr / 4, (long)(dr / 4 * readLat));
            }
        }
        if (backup > 0) Bump("BACKUPIO", (long)(80 * secs), (long)(8000 * secs));
        Bump("IO_COMPLETION", (long)(5 * secs), (long)(totalWaitMs * 0.01));
    }

    private void Bump(string wait, long tasks, long ms)
    {
        var w = _waits[wait];
        _waits[wait] = (w.Tasks + tasks, w.Ms + ms);
    }

    private static double Bell(double x, double mu, double sigma) => Math.Exp(-Math.Pow(x - mu, 2) / (2 * sigma * sigma));


    /// <summary>A simulated Azure VM and managed disks for the demo server, so the Azure panel and VM limit show.</summary>
    public static (Azure.AzureVmLink Vm, Dictionary<string, List<Azure.AzureDiskInfo>> Disks) DemoAzure()
    {
        const string rg = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-sql-prod/providers/Microsoft.Compute";
        Azure.AzureDiskInfo Disk(string name, int? lun, double size, string tier, double iops, double mbps, string caching, double price, double? next, bool os = false) =>
            Azure.AzureSync.ToInfo(new Azure.AzureDisk($"{rg}/disks/{name}", name, lun, os, caching, false, size, "Premium_LRS", tier, iops, mbps, false, "uksouth",
                Zone: "1", Encryption: "EncryptionAtRestWithPlatformKey", NetworkAccess: "AllowAll", Created: new DateTime(2025, 3, 14)), price, next);
        var vm = new Azure.AzureVmLink
        {
            VmId = $"{rg}/virtualMachines/sqlprod01", Name = "sqlprod01", ResourceGroup = "rg-sql-prod", SubscriptionName = "Demo subscription",
            Location = "uksouth", ComputerName = "DEMO-SQL01", VmSize = "Standard_E8ds_v5", MaxIops = 12800, MaxMbps = 290, MaxDataDisks = 16,
            DataDisksAttached = 3, SyncedAt = DateTime.Now.AddHours(-3)
        };
        return (vm, new Dictionary<string, List<Azure.AzureDiskInfo>>
        {
            ["C:\\"] = [Disk("sqlprod01-osdisk", null, 127, "P10", 500, 100, "ReadWrite", 15.43, 29.79, os: true)],
            ["D:\\"] = [Disk("sqlprod01-datadisk-00", 0, 1024, "P30", 5000, 200, "ReadOnly", 106.04, 203.01)],
            ["L:\\"] = [Disk("sqlprod01-logdisk-00", 1, 512, "P20", 2300, 150, "None", 56.20, 106.04)],
            ["T:\\"] = [Disk("sqlprod01-tempdbdisk-00", 2, 128, "P10", 500, 100, "ReadOnly", 15.43, 29.79)],
        });
    }

    /// <summary>Disk definitions for the demo server so capacity and cost analysis light up immediately.</summary>
    public static IEnumerable<Models.DiskConfig> DemoDisks(long serverId) =>
    [
        new() { ServerId = serverId, Volume = "C:\\", Label = "OS", DriveName = "Operating system", DiskName = "sqlprod01-osdisk", Lun = null, Tier = "Azure P10", MaxIops = 500, MaxMbps = 100, BurstIops = 3500, BurstMbps = 170, SizeGb = 127, CostMonthly = 15.43, DiskNumber = 0, DeviceModel = "Virtual Disk", Tags = "OS disk, host cache: ReadWrite" },
        new() { ServerId = serverId, Volume = "D:\\", Label = "SQLData", DriveName = "SQL data", DiskName = "sqlprod01-datadisk-00", Lun = 0, Tier = "Azure P30", MaxIops = 5000, MaxMbps = 200, SizeGb = 1024, CostMonthly = 106.04, DiskNumber = 2, DeviceModel = "Msft Virtual Disk", Notes = "Read caching enabled. Upgraded from P20 2026-06-01 after backup window overran.", Tags = "host cache: ReadOnly, storage pool: none, owner: DBA" },
        new() { ServerId = serverId, Volume = "L:\\", Label = "SQLLogs", DriveName = "SQL transaction logs", DiskName = "sqlprod01-logdisk-00", Lun = 1, Tier = "Azure P20", MaxIops = 2300, MaxMbps = 150, BurstIops = 3500, BurstMbps = 170, SizeGb = 512, CostMonthly = 56.20, DiskNumber = 3, DeviceModel = "Msft Virtual Disk", Notes = "Host caching: None (log).", Tags = "host cache: None, write accelerator: off" },
        new() { ServerId = serverId, Volume = "T:\\", Label = "TempDB", DriveName = "TempDB", DiskName = "sqlprod01-tempdbdisk-00", Lun = 2, Tier = "Azure P10", MaxIops = 500, MaxMbps = 100, BurstIops = 3500, BurstMbps = 170, SizeGb = 128, CostMonthly = 15.43, DiskNumber = 4, DeviceModel = "Msft Virtual Disk", Tags = "host cache: ReadOnly, candidate: local NVMe" },
    ];
}
