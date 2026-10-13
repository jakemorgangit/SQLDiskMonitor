using System.IO;
using SQLDiskMonitor.Data;
using SQLDiskMonitor.Models;
using SQLDiskMonitor.Services;
using SQLDiskMonitor.Services.Azure;
using Xunit;

namespace SQLDiskMonitor.Tests;

public sealed class TempStore : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sdm-test-{Guid.NewGuid():N}.db");
    public Store Store { get; }
    public TempStore() => Store = new Store(Path);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { Path, Path + "-wal", Path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}

public class PressureTests
{
    private readonly AppSettings _cfg = new();

    [Fact]
    public void Capacity_uses_whichever_limit_is_closer()
    {
        var disk = new DiskConfig { MaxIops = 1000, MaxMbps = 100 };
        var s = new VolumeSample { ReadIops = 200, WriteIops = 100, ReadMbps = 80, WriteMbps = 0 };
        Pressure.Score(s, disk, hostsLog: false, _cfg);
        Assert.Equal(80, s.CapacityPct, 1);
        Assert.Equal(80, s.Pressure, 1);
    }

    [Fact]
    public void Capacity_is_capped_at_100_when_bursting()
    {
        var disk = new DiskConfig { MaxIops = 500, BurstIops = 3500 };
        var s = new VolumeSample { ReadIops = 2000 };
        Pressure.Score(s, disk, false, _cfg);
        Assert.Equal(100, s.CapacityPct);
    }

    [Fact]
    public void Latency_is_ignored_below_minimum_iops()
    {
        var s = new VolumeSample { ReadIops = 1, ReadLatencyMs = 500 };
        Pressure.Score(s, null, false, _cfg);
        Assert.Equal(0, s.Pressure);
    }

    [Fact]
    public void Log_volumes_use_stricter_write_thresholds()
    {
        var data = new VolumeSample { WriteIops = 100, WriteLatencyMs = 8 };
        var log = new VolumeSample { WriteIops = 100, WriteLatencyMs = 8 };
        Pressure.Score(data, null, hostsLog: false, _cfg);
        Pressure.Score(log, null, hostsLog: true, _cfg);
        Assert.True(data.Pressure < Pressure.Elevated, $"data {data.Pressure}");
        Assert.True(log.Pressure >= Pressure.High, $"log {log.Pressure}");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 30)]
    [InlineData(20, 70)]
    [InlineData(50, 95)]
    [InlineData(1000, 100)]
    public void Latency_curve_hits_band_edges(double ms, double expected) =>
        Assert.Equal(expected, Pressure.LatencyCurve(ms, 10, 20, 50), 1);
}

public class DeltaEngineTests
{
    private static RawFile File(int db, int id, string vol, string type, long reads, long readStall, long writes = 0, long bytesRead = 0) => new()
    {
        DatabaseId = db, FileId = id, DatabaseName = $"db{db}", LogicalName = $"f{id}", TypeDesc = type, PhysicalName = $"{vol}f{id}",
        Volume = vol, Reads = reads, ReadStall = readStall, Writes = writes, BytesRead = bytesRead, TotalBytes = 100L << 30, AvailableBytes = 40L << 30
    };

    [Fact]
    public void Computes_rates_and_weighted_latency_per_volume()
    {
        using var t = new TempStore();
        var server = new ServerConnection { Name = "s", Address = "x" };
        t.Store.SaveServer(server);
        var engine = new DeltaEngine(server.Id, t.Store, () => new AppSettings());
        var t0 = new DateTime(2026, 10, 1, 12, 0, 0);

        Assert.Null(engine.Process(new RawSnapshot { Time = t0, Files = [File(5, 1, "D:\\", "ROWS", 1000, 5000), File(5, 3, "D:\\", "ROWS", 0, 0)] }, []));
        var batch = engine.Process(new RawSnapshot
        {
            Time = t0.AddSeconds(10),
            // file 1: +1000 reads at 10ms each; file 3: +100 reads at 100ms each, 100 MiB read
            Files = [File(5, 1, "D:\\", "ROWS", 2000, 15000), File(5, 3, "D:\\", "ROWS", 100, 10000, bytesRead: 100L << 20)]
        }, []);

        Assert.NotNull(batch);
        var v = Assert.Single(batch!.Volumes);
        Assert.Equal(110, v.ReadIops, 3);
        Assert.Equal(10, v.ReadMbps, 3);
        Assert.Equal(20000.0 / 1100, v.ReadLatencyMs, 3);
        Assert.Equal(2, batch.Files.Count);
        Assert.Equal(40, v.FreeGb / v.TotalGb * 100, 3);
    }

    [Fact]
    public void Counter_reset_skips_the_file_for_one_interval()
    {
        using var t = new TempStore();
        var server = new ServerConnection { Name = "s", Address = "x" };
        t.Store.SaveServer(server);
        var engine = new DeltaEngine(server.Id, t.Store, () => new AppSettings());
        var t0 = DateTime.Now.AddMinutes(-5);
        engine.Process(new RawSnapshot { Time = t0, Files = [File(5, 1, "D:\\", "ROWS", 5000, 5000)] }, []);
        var batch = engine.Process(new RawSnapshot { Time = t0.AddSeconds(15), Files = [File(5, 1, "D:\\", "ROWS", 10, 10)] }, []);
        Assert.Equal(0, batch!.Volumes.Single().ReadIops);
        Assert.Empty(batch.Files);
    }

    [Fact]
    public void Inventory_and_samples_are_persisted()
    {
        using var t = new TempStore();
        var server = new ServerConnection { Name = "s", Address = "x" };
        t.Store.SaveServer(server);
        var engine = new DeltaEngine(server.Id, t.Store, () => new AppSettings());
        var t0 = DateTime.Now.AddMinutes(-5);
        engine.Process(new RawSnapshot { Time = t0, Files = [File(5, 1, "D:\\", "ROWS", 0, 0), File(5, 2, "L:\\", "LOG", 0, 0)] }, []);
        engine.Process(new RawSnapshot { Time = t0.AddSeconds(15), Files = [File(5, 1, "D:\\", "ROWS", 150, 300), File(5, 2, "L:\\", "LOG", 0, 0, writes: 300)] }, []);

        Assert.Equal(2, t.Store.GetFiles(server.Id).Count);
        Assert.Equal(["D:\\", "L:\\"], t.Store.GetDisks(server.Id).Select(d => d.Volume));
        var series = t.Store.GetVolumeSeries(server.Id, t0.AddMinutes(-1), DateTime.Now, 1);
        Assert.Equal(10, series.Single(s => s.Volume == "D:\\").ReadIops, 3);
        Assert.Equal(20, series.Single(s => s.Volume == "L:\\").WriteIops, 3);
    }
}

public class DiskConfigTests
{
    [Fact]
    public void Manual_disk_details_round_trip()
    {
        using var t = new TempStore();
        var server = new ServerConnection { Name = "s", Address = "x" };
        t.Store.SaveServer(server);
        t.Store.SaveDisk(new DiskConfig
        {
            ServerId = server.Id, Volume = "E:\\Mounts\\Data01\\", Label = "Data01", DriveName = "SQL data", DiskName = "sqlprod01-datadisk-00", Lun = 0, DiskNumber = 5, DeviceModel = "PURE FlashArray",
            Tier = "SAN / array LUN", MaxIops = 20000, MaxMbps = 400, Tags = "SAN: Pure-02, host cache: n/a ,  owner: DBA", Notes = "Thin provisioned"
        });
        t.Store.AddNote(new DiskNote { ServerId = server.Id, Volume = "E:\\Mounts\\Data01\\", Timestamp = DateTime.Now, Text = "Migrated from VNX" });

        var d = Assert.Single(t.Store.GetDisks(server.Id));
        Assert.Equal(0, d.Lun);
        Assert.Equal(5, d.DiskNumber);
        Assert.Equal("SQL data", d.DriveName);
        Assert.Equal("sqlprod01-datadisk-00", d.DiskName);
        Assert.Equal("SQL data  ·  sqlprod01-datadisk-00", d.NamesText);
        Assert.Equal("PURE FlashArray", d.DeviceModel);
        Assert.Equal(["SAN: Pure-02", "host cache: n/a", "owner: DBA"], d.TagList);
        Assert.Equal("Migrated from VNX", Assert.Single(t.Store.GetNotes(server.Id, d.Volume)).Text);
    }
}

public class DiskCsvImportTests
{
    private static List<DiskConfig> Disks() =>
    [
        new() { Volume = "F:\\", Label = "SQLVMDATA1", Lun = 1 },
        new() { Volume = "G:\\", Label = "SQLVMLOG" },
        new() { Volume = "E:\\Mounts\\Data01\\" },
    ];

    [Fact]
    public void Fills_only_empty_fields_matched_by_drive_letter()
    {
        var disks = Disks();
        var csv = "Drive,Label,LUN,Disk name,Max IOPS,Max MB/s,Owner\n" +
                  "F,New label,5,sqlvm-datadisk-01,\"5,000\",200 MB/s,DBA\n" +
                  "g:\\,,2,sqlvm-logdisk-01,£2300,\n" +
                  "E:\\Mounts\\Data01,,0,,,\n" +
                  "Z,,9,,,\n";
        var r = DiskCsvImport.Apply(csv, disks);

        var f = disks[0];
        Assert.Equal("SQLVMDATA1", f.Label);          // already set: kept
        Assert.Equal(1, f.Lun);                       // already set: kept
        Assert.Equal("sqlvm-datadisk-01", f.DiskName);
        Assert.Equal(5000, f.MaxIops);
        Assert.Equal(200, f.MaxMbps);

        var g = disks[1];
        Assert.Equal(2, g.Lun);
        Assert.Equal("sqlvm-logdisk-01", g.DiskName);
        Assert.Equal(2300, g.MaxIops);

        Assert.Equal(0, disks[2].Lun);                // LUN 0 is a real value
        Assert.Equal(["Z"], r.UnmatchedRows);
        Assert.Equal(["Owner"], r.IgnoredColumns);
        Assert.Equal(3, r.Changed.Count);
        Assert.Equal(3, r.RowsMatched);
    }

    [Fact]
    public void Uses_first_column_when_no_volume_header_and_detects_semicolons()
    {
        var disks = Disks();
        DiskCsvImport.Apply("Drv;Drive name;Tags\nG;Transaction logs;host cache: None, owner: DBA\n", disks);
        Assert.Equal("Transaction logs", disks[1].DriveName);
        Assert.Equal("host cache: None, owner: DBA", disks[1].Tags);
    }

    [Fact]
    public void Template_round_trips_and_app_export_is_importable()
    {
        var source = new List<DiskConfig>
        {
            new() { Volume = "F:\\", Label = "Data", DriveName = "SQL data", DiskName = "vm-datadisk-00", Lun = 0, DiskNumber = 2, DeviceModel = "Msft Virtual Disk",
                    Tier = "Azure P30", MaxIops = 5000, MaxMbps = 200, BurstIops = 0, SizeGb = 1024, CostMonthly = 106.04, Tags = "host cache: ReadOnly, owner: DBA",
                    Notes = "Line one\nline two, with comma" }
        };
        var template = DiskCsvImport.BuildTemplate(source);
        Assert.StartsWith("Volume,Label,Drive name,Disk name,LUN,Disk number", template);

        var target = new List<DiskConfig> { new() { Volume = "F:\\" } };
        var r = DiskCsvImport.Apply(template, target);
        var t = target[0];
        Assert.Empty(r.IgnoredColumns);
        Assert.Equal(("Data", "SQL data", "vm-datadisk-00", 0, 2), (t.Label, t.DriveName, t.DiskName, t.Lun!.Value, t.DiskNumber!.Value));
        Assert.Equal(("Msft Virtual Disk", "Azure P30", 5000.0, 200.0, 1024.0, 106.04), (t.DeviceModel, t.Tier, t.MaxIops, t.MaxMbps, t.SizeGb, t.CostMonthly));
        Assert.Equal("host cache: ReadOnly, owner: DBA", t.Tags);
        Assert.Equal("Line one line two, with comma", t.Notes);

        // The Inventory "Export CSV" starts with a server column; the volume column is found by name.
        var exported = "server,volume,label,drive_name,disk_name,lun\nSQL01,F:\\,Data,SQL data,vm-datadisk-00,0\n";
        var blank = new List<DiskConfig> { new() { Volume = "F:\\" } };
        DiskCsvImport.Apply(exported, blank);
        Assert.Equal(("Data", "vm-datadisk-00", 0), (blank[0].Label, blank[0].DiskName, blank[0].Lun!.Value));
    }
}

public class AzureTests
{
    private static AzureDisk Disk(string name, int? lun, double size, string tier, double iops, double mbps, string sku = "Premium_LRS",
        string caching = "ReadOnly", bool os = false, bool bursting = false, int shares = 1) =>
        new($"/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/disks/{name}", name, lun, os, caching, false, size, sku, tier, iops, mbps, bursting, "uksouth", shares);

    [Fact]
    public void Next_tier_uses_the_higher_of_size_and_performance_tier()
    {
        Assert.Equal("P40", AzureTiers.NextTier("Premium_LRS", "P30", 1024)!.Name);
        Assert.Equal("P40", AzureTiers.NextTier("Premium_LRS", "P30", 512)!.Name);   // P20-sized disk running at P30
        Assert.Equal("E40", AzureTiers.NextTier("StandardSSD_LRS", "E30", 1024)!.Name);
        Assert.Null(AzureTiers.NextTier("Premium_LRS", "P80", 32767));
        Assert.Null(AzureTiers.NextTier("PremiumV2_LRS", "", 2048));
        Assert.Equal("P20", AzureTiers.BaseTier("Premium_LRS", 500)!.Name);
    }

    [Fact]
    public void Burst_and_expandable_follow_azure_rules()
    {
        Assert.Equal((3500, 170, "credit-based"), AzureTiers.Burst(Disk("a", 0, 512, "P20", 2300, 150)));
        Assert.Equal((30000, 1000, "on-demand"), AzureTiers.Burst(Disk("b", 0, 1024, "P30", 5000, 200, bursting: true)));
        Assert.Equal((0, 0, ""), AzureTiers.Burst(Disk("c", 0, 1024, "P30", 5000, 200)));

        Assert.StartsWith("Yes, online", AzureTiers.Expandable(Disk("d", 0, 1024, "P30", 5000, 200)));
        Assert.StartsWith("Yes, with the VM deallocated", AzureTiers.Expandable(Disk("e", null, 127, "P10", 500, 100, os: true)));
        Assert.StartsWith("No: shared", AzureTiers.Expandable(Disk("f", 0, 1024, "P30", 5000, 200, shares: 2)));
        Assert.StartsWith("No: already at the maximum", AzureTiers.Expandable(Disk("g", 0, 32767, "P80", 20000, 900)));
    }

    private static AzureDiskInfo Info(AzureDisk d) => new()
    {
        Id = d.Id, Name = d.Name, Lun = d.Lun, IsOsDisk = d.IsOsDisk, SkuName = d.SkuName, Tier = d.Tier, SizeGb = d.SizeGb,
        Iops = d.Iops, Mbps = d.Mbps, Caching = d.Caching, MonthlyPrice = 100
    };

    [Fact]
    public void Suggests_os_lun_size_and_storage_pool_matches()
    {
        var azure = new[]
        {
            Info(Disk("vm-os", null, 127, "P10", 500, 100, os: true, caching: "ReadWrite")),
            Info(Disk("vm-log", 3, 512, "P20", 2300, 150, caching: "None")),
            Info(Disk("vm-data-0", 0, 1024, "P30", 5000, 200)),
            Info(Disk("vm-data-1", 1, 1024, "P30", 5000, 200)),
            Info(Disk("vm-data-2", 2, 1024, "P30", 5000, 200)),
            Info(Disk("vm-misc", 4, 256, "P15", 1100, 125)),
        };
        var volumes = new List<DiskConfig>
        {
            new() { Volume = "C:\\" },
            new() { Volume = "G:\\", Lun = 3 },        // LUN entered by hand
            new() { Volume = "F:\\" },                 // SQLVMDATA1-style pool of three P30s
            new() { Volume = "M:\\" },                 // single disk, matched by size
            new() { Volume = "D:\\" },                 // temporary storage: no managed disk
        };
        var sizes = new Dictionary<string, double> { ["C:\\"] = 126.4, ["G:\\"] = 511.9, ["F:\\"] = 3069.9, ["M:\\"] = 255.9, ["D:\\"] = 64 };

        var m = AzureSync.Suggest(azure, volumes, sizes).ToDictionary(x => x.Disk.Name);
        Assert.Equal(("C:\\", "OS disk"), (m["vm-os"].Volume, m["vm-os"].Reason));
        Assert.Equal(("G:\\", "LUN 3"), (m["vm-log"].Volume, m["vm-log"].Reason));
        Assert.All(new[] { "vm-data-0", "vm-data-1", "vm-data-2" }, n => Assert.Equal("F:\\", m[n].Volume));
        Assert.Equal(("M:\\", "size"), (m["vm-misc"].Volume, m["vm-misc"].Reason));
    }

    [Fact]
    public void Apply_sets_azure_fields_keeps_user_fields_and_logs_changes()
    {
        var pool = new[] { "0", "1", "2" }.Select(i => Info(Disk($"vm-data-{i}", int.Parse(i), 1024, "P30", 5000, 200))).ToList();
        var f = new DiskConfig
        {
            Volume = "F:\\", Label = "SQLVMDATA1", DriveName = "SQL data", Notes = "keep me", Lun = 7,
            Tier = "Azure P20", MaxIops = 2300, MaxMbps = 150, Tags = "owner: DBA, host cache: None"
        };
        var note = AzureSync.Apply(f, pool, setCost: true);

        Assert.Equal(("SQLVMDATA1", "SQL data", "keep me", 7), (f.Label, f.DriveName, f.Notes, f.Lun!.Value)); // pool: LUN untouched
        Assert.Equal("vm-data-0, vm-data-1, vm-data-2", f.DiskName);
        Assert.Equal("Azure P30 ×3", f.Tier);
        Assert.Equal((15000.0, 600.0, 3072.0, 300.0), (f.MaxIops, f.MaxMbps, f.SizeGb, f.CostMonthly));
        Assert.Contains("LUN 0, 1, 2", f.DeviceModel);
        Assert.Equal(["owner: DBA", "host cache: ReadOnly"], f.TagList);
        Assert.Equal(3, f.AzureDisks.Count);
        Assert.Equal("Azure: Azure P20 → Azure P30 ×3; 2,300 → 15,000 IOPS; 150 → 600 MB/s", note);

        var single = new DiskConfig { Volume = "G:\\" };
        Assert.Null(AzureSync.Apply(single, [Info(Disk("vm-log", 3, 512, "P20", 2300, 150, caching: "None"))], setCost: false));
        Assert.Equal((3, "Azure P20", 0.0), (single.Lun!.Value, single.Tier, single.CostMonthly));
    }

    [Fact]
    public void Azure_details_persist_with_disks_and_servers()
    {
        using var t = new TempStore();
        var server = new ServerConnection { Name = "s", Address = "x", Azure = new AzureVmLink { VmId = "/subscriptions/s/vm", VmSize = "Standard_E16ds_v5", MaxIops = 25600, MaxMbps = 600 } };
        t.Store.SaveServer(server);
        var d = new DiskConfig { ServerId = server.Id, Volume = "F:\\" };
        AzureSync.Apply(d, [Info(Disk("vm-data-0", 0, 1024, "P30", 5000, 200))], false);
        t.Store.SaveDisk(d);

        var s2 = t.Store.GetServers().Single();
        Assert.Equal(("Standard_E16ds_v5", 25600.0), (s2.Azure!.VmSize, s2.Azure.MaxIops!.Value));
        var d2 = t.Store.GetDisks(server.Id).Single();
        Assert.Equal("vm-data-0", Assert.Single(d2.AzureDisks).Name);
        Assert.Equal("P30", d2.AzureDisks[0].Tier);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("72f988bf-86f1-41af-91ab-2d7cd011db47", true)]
    [InlineData("ticketer.onmicrosoft.com", true)]
    [InlineData("ticketer.com", true)]
    [InlineData("Ticketer", false)]
    [InlineData("Ticketer Ltd", false)]
    public void Tenant_must_be_a_guid_or_domain(string tenant, bool ok) =>
        Assert.Equal(ok, AzureClient.ValidateTenant(tenant) == null);

    [Fact]
    public void Friendly_error_keeps_the_azure_message_and_drops_the_traceback()
    {
        var cli = new Exception("Azure CLI authentication failed due to an unknown error. See the troubleshooting guide for more information. " +
            "ERROR: ... HTTP status: 400, Error: {\"error\":\"invalid_tenant\",\"error_description\":\"AADSTS90002: Tenant 'ticketer' not found. Check to make sure you have the correct tenant ID\"}\nTraceback (most recent call last):\n  File ...");
        Assert.Equal("AADSTS90002: Tenant 'ticketer' not found.", AzureClient.FriendlyError(cli));
        Assert.Equal("Something broke", AzureClient.FriendlyError(new Exception("Something broke\nTraceback (most recent call last): ...")));
    }

    /// <summary>Uses the public Azure retail price API (no sign-in). Runs when SQLDM_TEST_NETWORK=1.</summary>
    [Fact]
    public async Task Retail_price_lookup_returns_monthly_disk_price()
    {
        if (Environment.GetEnvironmentVariable("SQLDM_TEST_NETWORK") != "1") return;
        var p30 = await AzureClient.GetMonthlyTierPriceAsync("Premium_LRS", "P30", "uksouth", "GBP");
        Assert.True(p30 is > 50 and < 500, $"P30 price {p30}");
        Assert.Null(await AzureClient.GetMonthlyTierPriceAsync("PremiumV2_LRS", "", "uksouth", "GBP"));
    }
}

public class ReportTests
{
    [Fact]
    public async Task Report_contains_requested_sections_only()
    {
        using var t = new TempStore();
        var cfg = new AppSettings { CostsEnabled = true, CurrencySymbol = "£" };
        var server = new ServerConnection { Name = "Prod <SQL>", Address = "demo", HostName = "SQL01" };
        server.Azure = DemoSource.DemoAzure().Vm;
        t.Store.SaveServer(server);
        foreach (var d in DemoSource.DemoDisks(server.Id)) t.Store.SaveDisk(d);
        Collector.BackfillDemo(t.Store, server.Id, cfg, 1);

        var html = await SQLDiskMonitor.Services.Reports.ReportBuilder.BuildAsync(t.Store, server, cfg, DateTime.Now.AddDays(-1), DateTime.Now,
            new SQLDiskMonitor.Services.Reports.ReportOptions { Charts = false, Files = true }, "9.9.9");

        Assert.StartsWith("<!doctype html>", html.TrimStart());
        Assert.Contains("Storage report · Prod &lt;SQL&gt;", html);       // names are HTML-encoded
        foreach (var section in new[] { "<h2>Summary</h2>", "<h2>Disks</h2>", "Pressure by hour of day", "Findings and recommendations",
                                        "<h2>Space</h2>", "<h2>Costs</h2>", "<h2>IO waits</h2>", "Appendix: database files" })
            Assert.Contains(section, html);
        Assert.DoesNotContain("<h2>Trends</h2>", html);
        Assert.Contains("Standard_E8ds_v5", html);
        Assert.Contains("&#163;", html);                                     // £, as an HTML entity
    }
}

public class AnalyticsTests
{
    [Fact]
    public void Demo_week_produces_capacity_and_layout_findings()
    {
        using var t = new TempStore();
        var cfg = new AppSettings { CostsEnabled = true };
        var server = new ServerConnection { Name = "demo", Address = "demo" };
        t.Store.SaveServer(server);
        foreach (var d in DemoSource.DemoDisks(server.Id)) t.Store.SaveDisk(d);
        Collector.BackfillDemo(t.Store, server.Id, cfg, 2);

        var a = Analytics.Analyse(t.Store, server.Id, DateTime.Now.AddDays(-2), DateTime.Now, cfg);
        Assert.Equal(4, a.Disks.Count);
        Assert.Contains(a.Findings, f => f.Category == "Capacity" && f.Volume == "T:\\");
        Assert.Contains(a.Findings, f => f.Category == "Layout");
        Assert.Contains(a.Findings, f => f.Category == "Files"); // HR uses percent growth
        Assert.Equal(24, a.Hourly.Where(h => h.Volume == "D:\\").Select(h => h.Hour).Distinct().Count());

        // Space: D: grows (SalesDB, Warehouse), autogrowth is recorded, and used space inside files is known.
        var dVol = a.Space.Volumes.Single(v => v.Volume == "D:\\");
        Assert.True(dVol.GrowthGbPerDay > 1, $"growth {dVol.GrowthGbPerDay}");
        Assert.NotNull(dVol.DaysToFull);
        Assert.Contains(a.Space.Databases, x => x.Database == "SalesDB" && x.GrowthEvents > 0 && x.UsedGb > 0);
    }

    [Fact]
    public void Space_alerts_follow_settings()
    {
        using var t = new TempStore();
        var server = new ServerConnection { Name = "demo", Address = "demo" };
        t.Store.SaveServer(server);
        foreach (var d in DemoSource.DemoDisks(server.Id)) t.Store.SaveDisk(d);
        Collector.BackfillDemo(t.Store, server.Id, new AppSettings(), 2);
        var from = DateTime.Now.AddDays(-2);

        // T: (tempdb) has 7% free on the volume, but its pre-sized files are mostly empty.
        bool LowSpace(AppSettings cfg) => Analytics.Analyse(t.Store, server.Id, from, DateTime.Now, cfg).Findings
            .Any(f => f.Category == "Space" && f.Volume == "T:\\" && f.Title.Contains("%"));

        Assert.True(LowSpace(new AppSettings { CountFileFreeSpace = false }));
        Assert.False(LowSpace(new AppSettings { CountFileFreeSpace = true }));

        var off = Analytics.Analyse(t.Store, server.Id, from, DateTime.Now, new AppSettings { SpaceInHealth = false });
        Assert.DoesNotContain(off.Findings, f => f.Category == "Space");

        var tVol = off.Space.Volumes.Single(v => v.Volume == "T:\\");
        Assert.True(tVol.FreeInFilesGb > 40, $"free in files {tVol.FreeInFilesGb}");
        Assert.True(tVol.EffectiveFreePct > 40 && tVol.FreePct < 10);
    }

    [Fact]
    public void Percentile_matches_nearest_rank()
    {
        var v = Enumerable.Range(1, 100).Select(i => (double)i).ToList();
        Assert.Equal(95, Analytics.Percentile(v, 95));
        Assert.Equal(1, Analytics.Percentile(v, 0));
        Assert.Equal(0, Analytics.Percentile([], 50));
    }
}

public class UpdateTests
{
    [Theory]
    [InlineData("v2.1.0", "2.1.0")]
    [InlineData("2.1", "2.1.0")]
    [InlineData("v3", "3.0.0")]
    [InlineData("v2.0.1-beta", "2.0.1")]
    public void Parses_release_tags(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateService.ParseTag(tag));

    [Fact]
    public void Garbage_tag_is_rejected() => Assert.Null(UpdateService.ParseTag("latest"));
}

/// <summary>
/// Runs against a real instance when SQLDM_TEST_SERVER is set (e.g. ".\SQLEXPRESS"), using Windows authentication.
/// </summary>
public class SqlServerIntegrationTests
{
    [Fact]
    public async Task Captures_files_volumes_and_waits()
    {
        var address = Environment.GetEnvironmentVariable("SQLDM_TEST_SERVER");
        if (string.IsNullOrEmpty(address)) return;

        var server = new ServerConnection { Name = "it", Address = address, TrustCertificate = true };
        var info = await SqlSource.TestAsync(server, null);
        Assert.Contains("SQL Server", info);

        var src = new SqlSource(server);
        var snap = await src.CaptureAsync(CancellationToken.None);
        Assert.Contains(snap.Files, f => f.DatabaseName == "master" && f.TypeDesc == "ROWS");
        Assert.Contains(snap.Files, f => f.DatabaseId == 2);
        Assert.All(snap.Files, f => Assert.EndsWith("\\", f.Volume));
        Assert.All(snap.Files, f => Assert.True(f.TotalBytes > 0));
        Assert.NotEmpty(snap.Waits);
        Assert.False(string.IsNullOrEmpty(snap.HostName));

        var used = await src.CaptureSpaceUsedAsync(CancellationToken.None);
        Assert.Contains(used, kv => kv.Key.Db == 1 && kv.Value > 0);   // master data file
        Assert.All(used, kv => Assert.Contains(snap.Files, f => f.DatabaseId == kv.Key.Db && f.FileId == kv.Key.File));

        using var t = new TempStore();
        t.Store.SaveServer(server);
        var engine = new DeltaEngine(server.Id, t.Store, () => new AppSettings());
        engine.Process(snap, []);
        await Task.Delay(1500);
        var batch = engine.Process(await src.CaptureAsync(CancellationToken.None), []);
        Assert.NotNull(batch);
        Assert.NotEmpty(batch!.Volumes);
    }
}
