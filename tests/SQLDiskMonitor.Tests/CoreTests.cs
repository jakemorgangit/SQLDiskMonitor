using System.IO;
using SQLDiskMonitor.Data;
using SQLDiskMonitor.Models;
using SQLDiskMonitor.Services;
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
