using System.Collections.Concurrent;
using SQLDiskMonitor.Data;
using SQLDiskMonitor.Models;

namespace SQLDiskMonitor.Services;

public record SampleBatch(long ServerId, DateTime Time, IReadOnlyList<VolumeSample> Volumes, IReadOnlyList<FileSample> Files);

/// <summary>
/// Turns successive cumulative-counter snapshots into per-interval rates, scores them, stores them,
/// and announces each batch. One instance per monitored server.
/// </summary>
public class DeltaEngine(long serverId, Store store, Func<AppSettings> settings)
{
    private RawSnapshot? _prev;

    public SampleBatch? Process(RawSnapshot snap, IReadOnlyCollection<DiskConfig> disks, bool persistInventory = true)
    {
        var cfg = settings();

        if (persistInventory)
        {
            store.EnsureDisks(serverId, snap.Files.GroupBy(f => f.Volume)
                .Select(g => (g.Key, g.First().VolumeLabel, g.First().TotalBytes / 1073741824.0)));
            store.UpsertFiles(serverId, snap.Files.Select(f => new DbFile
            {
                DatabaseId = f.DatabaseId, FileId = f.FileId, DatabaseName = f.DatabaseName, LogicalName = f.LogicalName,
                TypeDesc = f.TypeDesc, PhysicalName = f.PhysicalName, Volume = f.Volume, SizeMb = f.SizePages / 128.0,
                MaxSizePages = f.MaxSize, Growth = f.Growth, IsPercentGrowth = f.IsPercentGrowth, LastSeen = snap.Time
            }));
        }

        var prev = _prev;
        _prev = snap;
        if (prev == null) return null;

        double elapsed = (snap.Time - prev.Time).TotalSeconds;
        if (elapsed < 0.5) return null;

        var prevFiles = prev.Files.ToDictionary(f => (f.DatabaseId, f.FileId));
        var fileSamples = new List<FileSample>();
        var perVolume = new Dictionary<string, (long r, long w, long br, long bw, long rs, long ws, int pending, RawFile any, bool log)>();
        var logWrites = new Dictionary<string, long>();

        foreach (var f in snap.Files)
        {
            if (!prevFiles.TryGetValue((f.DatabaseId, f.FileId), out var p)) continue;
            long dr = f.Reads - p.Reads, dw = f.Writes - p.Writes;
            long dbr = f.BytesRead - p.BytesRead, dbw = f.BytesWritten - p.BytesWritten;
            long drs = f.ReadStall - p.ReadStall, dws = f.WriteStall - p.WriteStall;
            // Counters reset on restart / database re-attach: skip this interval for the file.
            if (dr < 0 || dw < 0 || dbr < 0 || dbw < 0 || drs < 0 || dws < 0) continue;

            var v = perVolume.TryGetValue(f.Volume, out var a) ? a : (0, 0, 0, 0, 0, 0, 0, f, false);
            perVolume[f.Volume] = (v.r + dr, v.w + dw, v.br + dbr, v.bw + dbw, v.rs + drs, v.ws + dws, v.pending + f.Pending, f,
                v.log || f.TypeDesc == "LOG");

            if (f.TypeDesc == "LOG") logWrites[f.Volume] = logWrites.GetValueOrDefault(f.Volume) + dw;

            if (dr + dw > 0)
            {
                fileSamples.Add(new FileSample
                {
                    ServerId = serverId, Timestamp = snap.Time, DatabaseId = f.DatabaseId, FileId = f.FileId,
                    ReadIops = dr / elapsed, WriteIops = dw / elapsed,
                    ReadMbps = dbr / 1048576.0 / elapsed, WriteMbps = dbw / 1048576.0 / elapsed,
                    ReadLatencyMs = dr > 0 ? (double)drs / dr : 0, WriteLatencyMs = dw > 0 ? (double)dws / dw : 0,
                    StallMsPerSec = (drs + dws) / elapsed
                });
            }
        }

        // Volumes with no files matched this interval still get a zero sample so charts show idle time.
        foreach (var f in snap.Files.GroupBy(x => x.Volume))
            if (!perVolume.ContainsKey(f.Key))
                perVolume[f.Key] = (0, 0, 0, 0, 0, 0, 0, f.First(), f.Any(x => x.TypeDesc == "LOG"));

        var diskMap = disks.ToDictionary(d => d.Volume, StringComparer.OrdinalIgnoreCase);
        var volSamples = new List<VolumeSample>();
        foreach (var (vol, a) in perVolume)
        {
            var s = new VolumeSample
            {
                ServerId = serverId, Volume = vol, Timestamp = snap.Time, ElapsedSec = elapsed,
                ReadIops = a.r / elapsed, WriteIops = a.w / elapsed,
                ReadMbps = a.br / 1048576.0 / elapsed, WriteMbps = a.bw / 1048576.0 / elapsed,
                ReadLatencyMs = a.r > 0 ? (double)a.rs / a.r : 0, WriteLatencyMs = a.w > 0 ? (double)a.ws / a.w : 0,
                PendingIo = a.pending,
                TotalGb = a.any.TotalBytes / 1073741824.0, FreeGb = a.any.AvailableBytes / 1073741824.0
            };
            // Log-write latency targets apply when the volume's writes are mostly log writes, not just because a small log lives there.
            bool logVolume = a.w > 0 ? logWrites.GetValueOrDefault(vol) * 2 > a.w : a.log;
            Pressure.Score(s, diskMap.GetValueOrDefault(vol), logVolume, cfg);
            volSamples.Add(s);
        }

        var waitSamples = new List<WaitSample>();
        foreach (var (type, cur) in snap.Waits)
        {
            if (!prev.Waits.TryGetValue(type, out var p)) continue;
            long dt = cur.Tasks - p.Tasks, dm = cur.Ms - p.Ms;
            if (dt < 0 || dm < 0) continue;
            waitSamples.Add(new WaitSample { ServerId = serverId, Timestamp = snap.Time, WaitType = type, WaitMsPerSec = dm / elapsed, WaitsPerSec = dt / elapsed });
        }

        store.InsertSamples(volSamples, fileSamples, waitSamples);
        return new SampleBatch(serverId, snap.Time, volSamples, fileSamples);
    }

    /// <summary>Stores allocated size (from the snapshot) and used space (when available) for every file.</summary>
    public void ProcessSpace(RawSnapshot snap, IReadOnlyDictionary<(int Db, int File), long>? usedPages) =>
        store.InsertFileSpace(serverId, snap.Time, snap.Files.Select(f =>
            (f.DatabaseId, f.FileId, f.SizePages / 128.0,
             usedPages != null && usedPages.TryGetValue((f.DatabaseId, f.FileId), out var u) ? u / 128.0 : (double?)null)));

    public static readonly TimeSpan SpaceInterval = TimeSpan.FromMinutes(15);
}

/// <summary>Runs a polling loop per monitored server.</summary>
public class Collector(Store store, Func<AppSettings> settings)
{
    private readonly ConcurrentDictionary<long, (CancellationTokenSource Cts, Task Loop)> _loops = new();
    private readonly ConcurrentDictionary<long, List<VolumeSample>> _recent = new();
    private DateTime _lastPurge = DateTime.MinValue;

    // The demo simulation carries on from its backfilled state so sizes and counters stay continuous.
    private static readonly ConcurrentDictionary<long, DemoSource> DemoInstances = new();

    public event Action<SampleBatch>? SampleCollected;
    public event Action<ServerConnection>? StatusChanged;

    public bool IsRunning(long serverId) => _loops.ContainsKey(serverId);

    /// <summary>The most recent samples (in-memory) for live sparklines.</summary>
    public IReadOnlyList<VolumeSample> Recent(long serverId)
    {
        if (!_recent.TryGetValue(serverId, out var list)) return [];
        lock (list) return list.ToList();
    }

    public void Start(ServerConnection server)
    {
        if (_loops.ContainsKey(server.Id)) return;
        var cts = new CancellationTokenSource();
        var task = Task.Run(() => RunAsync(server, cts.Token));
        _loops[server.Id] = (cts, task);
    }

    public void Stop(long serverId)
    {
        if (_loops.TryRemove(serverId, out var l)) l.Cts.Cancel();
    }

    public void StopAll()
    {
        foreach (var id in _loops.Keys.ToList()) Stop(id);
    }

    public void Restart(ServerConnection server)
    {
        Stop(server.Id);
        Start(server);
    }

    private void SetStatus(ServerConnection s, string text, bool error, bool collecting)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            s.Status = text;
            s.HasError = error;
            s.IsCollecting = collecting;
            StatusChanged?.Invoke(s);
        });
    }

    private async Task RunAsync(ServerConnection server, CancellationToken ct)
    {
        ISnapshotSource source = server.IsDemo ? DemoInstances.GetOrAdd(server.Id, _ => new DemoSource()) : new SqlSource(server);
        var engine = new DeltaEngine(server.Id, store, settings);
        int failures = 0;
        var lastSpace = DateTime.MinValue;
        SetStatus(server, "Connecting…", false, true);

        while (!ct.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1, settings().SampleIntervalSeconds));
            try
            {
                var snap = await source.CaptureAsync(ct);
                if (!string.IsNullOrEmpty(snap.HostName) && (server.HostName != snap.HostName || server.Version != snap.Version))
                {
                    var host = snap.HostName; var ver = snap.Version;
                    System.Windows.Application.Current?.Dispatcher.Invoke(() => { server.HostName = host; server.Version = ver; });
                    store.SaveServer(server);
                }

                var batch = engine.Process(snap, store.GetDisks(server.Id));
                failures = 0;

                if (snap.Time - lastSpace >= DeltaEngine.SpaceInterval)
                {
                    lastSpace = snap.Time;
                    Dictionary<(int, int), long>? used = null;
                    // Used-space is best effort: a database we cannot enter must not stop IO collection.
                    try { used = await source.CaptureSpaceUsedAsync(ct); } catch (Exception) when (!ct.IsCancellationRequested) { }
                    engine.ProcessSpace(snap, used);
                }
                if (batch != null)
                {
                    var list = _recent.GetOrAdd(server.Id, _ => []);
                    lock (list)
                    {
                        list.AddRange(batch.Volumes);
                        var cut = DateTime.Now.AddMinutes(-30);
                        list.RemoveAll(x => x.Timestamp < cut);
                    }
                    SampleCollected?.Invoke(batch);
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => server.LastSample = batch.Time);
                    SetStatus(server, $"Collecting every {interval.TotalSeconds:0}s", false, true);
                }
                else
                {
                    SetStatus(server, "Baseline captured — first sample in " + interval.TotalSeconds.ToString("0") + "s", false, true);
                }

                if (DateTime.Now - _lastPurge > TimeSpan.FromHours(1))
                {
                    _lastPurge = DateTime.Now;
                    var cfg = settings();
                    store.Purge(cfg.RetentionDays, cfg.FileRetentionDays);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                SetStatus(server, $"Error: {ex.Message}", true, true);
                engine = new DeltaEngine(server.Id, store, settings); // re-baseline after an outage
                interval = TimeSpan.FromSeconds(Math.Min(300, interval.TotalSeconds * Math.Min(failures, 8)));
            }

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
        SetStatus(server, "Stopped", false, false);
    }

    /// <summary>Generates <paramref name="days"/> of history for the demo server so trends and heatmaps are populated.</summary>
    public static void BackfillDemo(Store store, long serverId, AppSettings cfg, int days, IProgress<double>? progress = null)
    {
        var demo = new DemoSource();
        DemoInstances[serverId] = demo;
        var engine = new DeltaEngine(serverId, store, () => cfg);
        var disks = store.GetDisks(serverId);
        var start = DateTime.Now.AddDays(-days);
        var end = DateTime.Now.AddMinutes(-1);
        var step = TimeSpan.FromMinutes(1);
        bool first = true;
        int total = (int)((end - start) / step), i = 0;
        for (var t = start; t < end; t += step, i++)
        {
            var snap = demo.Capture(t);
            engine.Process(snap, disks, first);
            if (i % 15 == 0) engine.ProcessSpace(snap, demo.CaptureSpaceUsed());
            if (first) { first = false; disks = store.GetDisks(serverId); }
            if (i % 200 == 0) progress?.Report((double)i / total);
        }
        progress?.Report(1);
    }
}
