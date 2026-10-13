using System.IO;
using Microsoft.Data.Sqlite;
using SQLDiskMonitor.Models;

namespace SQLDiskMonitor.Data;

/// <summary>Local SQLite history: servers, disk configuration, notes, file inventory and time-series samples.</summary>
public class Store
{
    private readonly string _connectionString;

    public Store(string? path = null)
    {
        path ??= Path.Combine(AppSettings.DataFolder, "history.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Cache = SqliteCacheMode.Shared, Pooling = true }.ToString();
        Initialise();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Initialise()
    {
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS servers(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                address TEXT NOT NULL,
                auth INTEGER NOT NULL DEFAULT 0,
                username TEXT NOT NULL DEFAULT '',
                trust_cert INTEGER NOT NULL DEFAULT 1,
                encrypt INTEGER NOT NULL DEFAULT 0,
                timeout INTEGER NOT NULL DEFAULT 10,
                monitor INTEGER NOT NULL DEFAULT 1,
                host_name TEXT NOT NULL DEFAULT '',
                version TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS disks(
                server_id INTEGER NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
                volume TEXT NOT NULL,
                label TEXT NOT NULL DEFAULT '',
                lun INTEGER NULL,
                tier TEXT NOT NULL DEFAULT '',
                max_iops REAL NOT NULL DEFAULT 0,
                max_mbps REAL NOT NULL DEFAULT 0,
                burst_iops REAL NOT NULL DEFAULT 0,
                burst_mbps REAL NOT NULL DEFAULT 0,
                size_gb REAL NOT NULL DEFAULT 0,
                cost_monthly REAL NOT NULL DEFAULT 0,
                cost_per_gb REAL NOT NULL DEFAULT 0,
                cost_per_iops REAL NOT NULL DEFAULT 0,
                cost_per_mbps REAL NOT NULL DEFAULT 0,
                notes TEXT NOT NULL DEFAULT '',
                disk_number INTEGER NULL,
                device_model TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(server_id, volume));
            CREATE TABLE IF NOT EXISTS disk_notes(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                server_id INTEGER NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
                volume TEXT NOT NULL,
                ts INTEGER NOT NULL,
                text TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS files(
                server_id INTEGER NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
                database_id INTEGER NOT NULL,
                file_id INTEGER NOT NULL,
                database_name TEXT NOT NULL,
                logical_name TEXT NOT NULL,
                type_desc TEXT NOT NULL,
                physical_name TEXT NOT NULL,
                volume TEXT NOT NULL,
                size_mb REAL NOT NULL,
                max_size INTEGER NOT NULL,
                growth INTEGER NOT NULL,
                is_percent_growth INTEGER NOT NULL,
                last_seen INTEGER NOT NULL,
                PRIMARY KEY(server_id, database_id, file_id));
            CREATE TABLE IF NOT EXISTS volume_samples(
                server_id INTEGER NOT NULL,
                volume TEXT NOT NULL,
                ts INTEGER NOT NULL,
                elapsed REAL NOT NULL,
                read_iops REAL, write_iops REAL, read_mbps REAL, write_mbps REAL,
                read_lat REAL, write_lat REAL, pending REAL,
                total_gb REAL, free_gb REAL,
                pressure REAL, capacity REAL, latency_score REAL);
            CREATE INDEX IF NOT EXISTS ix_vs ON volume_samples(server_id, ts);
            CREATE TABLE IF NOT EXISTS file_samples(
                server_id INTEGER NOT NULL,
                ts INTEGER NOT NULL,
                database_id INTEGER NOT NULL,
                file_id INTEGER NOT NULL,
                read_iops REAL, write_iops REAL, read_mbps REAL, write_mbps REAL,
                read_lat REAL, write_lat REAL, stall REAL);
            CREATE INDEX IF NOT EXISTS ix_fs ON file_samples(server_id, ts);
            CREATE TABLE IF NOT EXISTS wait_samples(
                server_id INTEGER NOT NULL,
                ts INTEGER NOT NULL,
                wait_type TEXT NOT NULL,
                wait_ms_per_sec REAL,
                waits_per_sec REAL);
            CREATE INDEX IF NOT EXISTS ix_ws ON wait_samples(server_id, ts);
            CREATE TABLE IF NOT EXISTS file_space(
                server_id INTEGER NOT NULL,
                ts INTEGER NOT NULL,
                database_id INTEGER NOT NULL,
                file_id INTEGER NOT NULL,
                size_mb REAL NOT NULL,
                used_mb REAL NULL);
            CREATE INDEX IF NOT EXISTS ix_fsp ON file_space(server_id, ts);
            """);
        // Columns added after the first schema; ignore "duplicate column" on databases that already have them.
        foreach (var alter in new[] { "ALTER TABLE files ADD COLUMN used_mb REAL NULL", "ALTER TABLE disks ADD COLUMN tags TEXT NOT NULL DEFAULT ''",
                                         "ALTER TABLE disks ADD COLUMN drive_name TEXT NOT NULL DEFAULT ''",
                                         "ALTER TABLE disks ADD COLUMN disk_name TEXT NOT NULL DEFAULT ''" })
        {
            try { Exec(c, alter); } catch (SqliteException) { }
        }
    }

    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static IEnumerable<SqliteDataReader> Query(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return r;
    }

    public static long ToUnix(DateTime t) => new DateTimeOffset(t.ToUniversalTime()).ToUnixTimeSeconds();
    public static DateTime FromUnix(long s) => DateTimeOffset.FromUnixTimeSeconds(s).LocalDateTime;

    // ───────────────────────── Servers ─────────────────────────

    public List<ServerConnection> GetServers()
    {
        using var c = Open();
        return Query(c, "SELECT id,name,address,auth,username,trust_cert,encrypt,timeout,monitor,host_name,version FROM servers ORDER BY name")
            .Select(r => new ServerConnection
            {
                Id = r.GetInt64(0), Name = r.GetString(1), Address = r.GetString(2), Auth = (AuthMode)r.GetInt32(3),
                Username = r.GetString(4), TrustCertificate = r.GetInt32(5) == 1, Encrypt = r.GetInt32(6) == 1,
                Timeout = r.GetInt32(7), Monitor = r.GetInt32(8) == 1, HostName = r.GetString(9), Version = r.GetString(10)
            }).ToList();
    }

    public void SaveServer(ServerConnection s)
    {
        using var c = Open();
        var args = new (string, object?)[]
        {
            ("$id", s.Id), ("$name", s.Name), ("$address", s.Address), ("$auth", (int)s.Auth), ("$user", s.Username),
            ("$trust", s.TrustCertificate ? 1 : 0), ("$enc", s.Encrypt ? 1 : 0), ("$timeout", s.Timeout),
            ("$mon", s.Monitor ? 1 : 0), ("$host", s.HostName), ("$ver", s.Version)
        };
        if (s.Id == 0)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO servers(name,address,auth,username,trust_cert,encrypt,timeout,monitor,host_name,version)
                VALUES($name,$address,$auth,$user,$trust,$enc,$timeout,$mon,$host,$ver); SELECT last_insert_rowid();
                """;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            s.Id = (long)cmd.ExecuteScalar()!;
        }
        else
        {
            Exec(c, """
                UPDATE servers SET name=$name,address=$address,auth=$auth,username=$user,trust_cert=$trust,encrypt=$enc,
                timeout=$timeout,monitor=$mon,host_name=$host,version=$ver WHERE id=$id
                """, args);
        }
    }

    public void DeleteServer(long id)
    {
        using var c = Open();
        Exec(c, """
            DELETE FROM volume_samples WHERE server_id=$id;
            DELETE FROM file_samples WHERE server_id=$id;
            DELETE FROM wait_samples WHERE server_id=$id;
            DELETE FROM file_space WHERE server_id=$id;
            DELETE FROM servers WHERE id=$id;
            """, ("$id", id));
    }

    // ───────────────────────── Disks & notes ─────────────────────────

    public List<DiskConfig> GetDisks(long serverId)
    {
        using var c = Open();
        return Query(c, """
            SELECT volume,label,lun,tier,max_iops,max_mbps,burst_iops,burst_mbps,size_gb,cost_monthly,cost_per_gb,
                   cost_per_iops,cost_per_mbps,notes,disk_number,device_model,tags,drive_name,disk_name
            FROM disks WHERE server_id=$s ORDER BY volume
            """, ("$s", serverId))
            .Select(r => new DiskConfig
            {
                ServerId = serverId, Volume = r.GetString(0), Label = r.GetString(1), Lun = r.IsDBNull(2) ? null : r.GetInt32(2),
                Tier = r.GetString(3), MaxIops = r.GetDouble(4), MaxMbps = r.GetDouble(5), BurstIops = r.GetDouble(6),
                BurstMbps = r.GetDouble(7), SizeGb = r.GetDouble(8), CostMonthly = r.GetDouble(9), CostPerGb = r.GetDouble(10),
                CostPerIops = r.GetDouble(11), CostPerMbps = r.GetDouble(12), Notes = r.GetString(13),
                DiskNumber = r.IsDBNull(14) ? null : r.GetInt32(14), DeviceModel = r.GetString(15), Tags = r.GetString(16),
                DriveName = r.GetString(17), DiskName = r.GetString(18)
            }).ToList();
    }

    public void EnsureDisks(long serverId, IEnumerable<(string Volume, string Label, double TotalGb)> volumes)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var v in volumes)
            Exec(c, "INSERT OR IGNORE INTO disks(server_id,volume,label,size_gb) VALUES($s,$v,$l,$g)",
                ("$s", serverId), ("$v", v.Volume), ("$l", v.Label), ("$g", Math.Round(v.TotalGb, 1)));
        tx.Commit();
    }

    public void SaveDisk(DiskConfig d)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO disks(server_id,volume,label,lun,tier,max_iops,max_mbps,burst_iops,burst_mbps,size_gb,cost_monthly,
                              cost_per_gb,cost_per_iops,cost_per_mbps,notes,disk_number,device_model,tags,drive_name,disk_name)
            VALUES($s,$v,$label,$lun,$tier,$iops,$mbps,$biops,$bmbps,$size,$cm,$cgb,$ciops,$cmbps,$notes,$dn,$model,$tags,$drive,$disk)
            ON CONFLICT(server_id,volume) DO UPDATE SET label=$label,lun=$lun,tier=$tier,max_iops=$iops,max_mbps=$mbps,
                burst_iops=$biops,burst_mbps=$bmbps,size_gb=$size,cost_monthly=$cm,cost_per_gb=$cgb,cost_per_iops=$ciops,
                cost_per_mbps=$cmbps,notes=$notes,disk_number=$dn,device_model=$model,tags=$tags,drive_name=$drive,disk_name=$disk
            """,
            ("$s", d.ServerId), ("$v", d.Volume), ("$label", d.Label), ("$lun", d.Lun), ("$tier", d.Tier),
            ("$iops", d.MaxIops), ("$mbps", d.MaxMbps), ("$biops", d.BurstIops), ("$bmbps", d.BurstMbps),
            ("$size", d.SizeGb), ("$cm", d.CostMonthly), ("$cgb", d.CostPerGb), ("$ciops", d.CostPerIops),
            ("$cmbps", d.CostPerMbps), ("$notes", d.Notes), ("$dn", d.DiskNumber), ("$model", d.DeviceModel), ("$tags", d.Tags),
            ("$drive", d.DriveName), ("$disk", d.DiskName));
    }

    public List<DiskNote> GetNotes(long serverId, string? volume = null)
    {
        using var c = Open();
        return Query(c, "SELECT id,volume,ts,text FROM disk_notes WHERE server_id=$s AND ($v IS NULL OR volume=$v) ORDER BY ts DESC",
                ("$s", serverId), ("$v", volume))
            .Select(r => new DiskNote { Id = r.GetInt64(0), ServerId = serverId, Volume = r.GetString(1), Timestamp = FromUnix(r.GetInt64(2)), Text = r.GetString(3) })
            .ToList();
    }

    public void AddNote(DiskNote n)
    {
        using var c = Open();
        Exec(c, "INSERT INTO disk_notes(server_id,volume,ts,text) VALUES($s,$v,$t,$x)",
            ("$s", n.ServerId), ("$v", n.Volume), ("$t", ToUnix(n.Timestamp)), ("$x", n.Text));
    }

    public void DeleteNote(long id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM disk_notes WHERE id=$id", ("$id", id));
    }

    // ───────────────────────── Files ─────────────────────────

    public void UpsertFiles(long serverId, IEnumerable<DbFile> files)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO files(server_id,database_id,file_id,database_name,logical_name,type_desc,physical_name,volume,size_mb,
                              max_size,growth,is_percent_growth,last_seen)
            VALUES($s,$db,$f,$dbn,$ln,$t,$p,$v,$sz,$mx,$g,$pg,$ls)
            ON CONFLICT(server_id,database_id,file_id) DO UPDATE SET database_name=$dbn,logical_name=$ln,type_desc=$t,
                physical_name=$p,volume=$v,size_mb=$sz,max_size=$mx,growth=$g,is_percent_growth=$pg,last_seen=$ls
            """;
        var latest = DateTime.MinValue;
        var ps = new[] { "$s", "$db", "$f", "$dbn", "$ln", "$t", "$p", "$v", "$sz", "$mx", "$g", "$pg", "$ls" }
            .Select(n => cmd.Parameters.Add(new SqliteParameter(n, null))).ToArray();
        foreach (var f in files)
        {
            object[] vals = [serverId, f.DatabaseId, f.FileId, f.DatabaseName, f.LogicalName, f.TypeDesc, f.PhysicalName, f.Volume,
                f.SizeMb, f.MaxSizePages, f.Growth, f.IsPercentGrowth ? 1 : 0, ToUnix(f.LastSeen)];
            for (int i = 0; i < ps.Length; i++) ps[i].Value = vals[i];
            cmd.ExecuteNonQuery();
            if (f.LastSeen > latest) latest = f.LastSeen;
        }
        // Files not seen for a day (dropped databases) are removed from the inventory.
        if (latest > DateTime.MinValue)
            Exec(c, "DELETE FROM files WHERE server_id=$s AND last_seen < $cut", ("$s", serverId), ("$cut", ToUnix(latest.AddDays(-1))));
        tx.Commit();
    }

    public List<DbFile> GetFiles(long serverId)
    {
        using var c = Open();
        return Query(c, """
            SELECT database_id,file_id,database_name,logical_name,type_desc,physical_name,volume,size_mb,max_size,growth,is_percent_growth,last_seen,used_mb
            FROM files WHERE server_id=$s ORDER BY volume, database_name, file_id
            """, ("$s", serverId))
            .Select(r => new DbFile
            {
                ServerId = serverId, DatabaseId = r.GetInt32(0), FileId = r.GetInt32(1), DatabaseName = r.GetString(2),
                LogicalName = r.GetString(3), TypeDesc = r.GetString(4), PhysicalName = r.GetString(5), Volume = r.GetString(6),
                SizeMb = r.GetDouble(7), MaxSizePages = r.GetInt64(8), Growth = r.GetInt64(9), IsPercentGrowth = r.GetInt32(10) == 1,
                LastSeen = FromUnix(r.GetInt64(11)), UsedMb = r.IsDBNull(12) ? null : r.GetDouble(12)
            }).ToList();
    }

    // ───────────────────────── Samples ─────────────────────────

    public void InsertSamples(IReadOnlyCollection<VolumeSample> volumes, IReadOnlyCollection<FileSample> files, IReadOnlyCollection<WaitSample> waits)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO volume_samples(server_id,volume,ts,elapsed,read_iops,write_iops,read_mbps,write_mbps,read_lat,write_lat,
                                           pending,total_gb,free_gb,pressure,capacity,latency_score)
                VALUES($s,$v,$t,$e,$ri,$wi,$rm,$wm,$rl,$wl,$p,$tg,$fg,$pr,$cap,$ls)
                """;
            var ps = new[] { "$s", "$v", "$t", "$e", "$ri", "$wi", "$rm", "$wm", "$rl", "$wl", "$p", "$tg", "$fg", "$pr", "$cap", "$ls" }
                .Select(n => cmd.Parameters.Add(new SqliteParameter(n, null))).ToArray();
            foreach (var s in volumes)
            {
                object[] vals = [s.ServerId, s.Volume, ToUnix(s.Timestamp), s.ElapsedSec, s.ReadIops, s.WriteIops, s.ReadMbps, s.WriteMbps,
                    s.ReadLatencyMs, s.WriteLatencyMs, s.PendingIo, s.TotalGb, s.FreeGb, s.Pressure, s.CapacityPct, s.LatencyScore];
                for (int i = 0; i < ps.Length; i++) ps[i].Value = vals[i];
                cmd.ExecuteNonQuery();
            }
        }

        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO file_samples(server_id,ts,database_id,file_id,read_iops,write_iops,read_mbps,write_mbps,read_lat,write_lat,stall)
                VALUES($s,$t,$d,$f,$ri,$wi,$rm,$wm,$rl,$wl,$st)
                """;
            var ps = new[] { "$s", "$t", "$d", "$f", "$ri", "$wi", "$rm", "$wm", "$rl", "$wl", "$st" }
                .Select(n => cmd.Parameters.Add(new SqliteParameter(n, null))).ToArray();
            foreach (var s in files)
            {
                object[] vals = [s.ServerId, ToUnix(s.Timestamp), s.DatabaseId, s.FileId, s.ReadIops, s.WriteIops, s.ReadMbps, s.WriteMbps,
                    s.ReadLatencyMs, s.WriteLatencyMs, s.StallMsPerSec];
                for (int i = 0; i < ps.Length; i++) ps[i].Value = vals[i];
                cmd.ExecuteNonQuery();
            }
        }

        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO wait_samples(server_id,ts,wait_type,wait_ms_per_sec,waits_per_sec) VALUES($s,$t,$w,$ms,$n)";
            var ps = new[] { "$s", "$t", "$w", "$ms", "$n" }.Select(n => cmd.Parameters.Add(new SqliteParameter(n, null))).ToArray();
            foreach (var s in waits)
            {
                object[] vals = [s.ServerId, ToUnix(s.Timestamp), s.WaitType, s.WaitMsPerSec, s.WaitsPerSec];
                for (int i = 0; i < ps.Length; i++) ps[i].Value = vals[i];
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    /// <summary>Volume samples averaged into buckets so long ranges stay chartable.</summary>
    public List<VolumeSample> GetVolumeSeries(long serverId, DateTime from, DateTime to, int bucketSeconds)
    {
        bucketSeconds = Math.Max(1, bucketSeconds);
        using var c = Open();
        return Query(c, """
            SELECT volume, (ts / $b) * $b AS bts, AVG(read_iops), AVG(write_iops), AVG(read_mbps), AVG(write_mbps),
                   CASE WHEN SUM(read_iops) > 0 THEN SUM(read_lat * read_iops) / SUM(read_iops) ELSE 0 END,
                   CASE WHEN SUM(write_iops) > 0 THEN SUM(write_lat * write_iops) / SUM(write_iops) ELSE 0 END,
                   AVG(pending), AVG(total_gb), AVG(free_gb), AVG(pressure), AVG(capacity), AVG(latency_score), SUM(elapsed)
            FROM volume_samples WHERE server_id=$s AND ts BETWEEN $f AND $t
            GROUP BY volume, bts ORDER BY bts
            """, ("$s", serverId), ("$f", ToUnix(from)), ("$t", ToUnix(to)), ("$b", bucketSeconds))
            .Select(r => new VolumeSample
            {
                ServerId = serverId, Volume = r.GetString(0), Timestamp = FromUnix(r.GetInt64(1)),
                ReadIops = r.GetDouble(2), WriteIops = r.GetDouble(3), ReadMbps = r.GetDouble(4), WriteMbps = r.GetDouble(5),
                ReadLatencyMs = r.GetDouble(6), WriteLatencyMs = r.GetDouble(7), PendingIo = r.GetDouble(8),
                TotalGb = r.GetDouble(9), FreeGb = r.GetDouble(10), Pressure = r.GetDouble(11), CapacityPct = r.GetDouble(12),
                LatencyScore = r.GetDouble(13), ElapsedSec = r.GetDouble(14)
            }).ToList();
    }

    public record FileStat(int DatabaseId, int FileId, double ReadIops, double WriteIops, double ReadMbps, double WriteMbps,
        double ReadLatency, double WriteLatency, double StallMsPerSec, double PeakIops);

    /// <summary>Per-file averages over a range (latency weighted by IO count).</summary>
    public List<FileStat> GetFileStats(long serverId, DateTime from, DateTime to)
    {
        using var c = Open();
        var n = Math.Max(1, CountSampleTimes(c, serverId, from, to));
        return Query(c, """
            SELECT database_id, file_id, SUM(read_iops), SUM(write_iops), SUM(read_mbps), SUM(write_mbps),
                   CASE WHEN SUM(read_iops) > 0 THEN SUM(read_lat * read_iops) / SUM(read_iops) ELSE 0 END,
                   CASE WHEN SUM(write_iops) > 0 THEN SUM(write_lat * write_iops) / SUM(write_iops) ELSE 0 END,
                   SUM(stall), MAX(read_iops + write_iops)
            FROM file_samples WHERE server_id=$s AND ts BETWEEN $f AND $t
            GROUP BY database_id, file_id
            """, ("$s", serverId), ("$f", ToUnix(from)), ("$t", ToUnix(to)))
            // Zero-activity samples are not stored, so average over the number of collection ticks, not stored rows.
            .Select(r => new FileStat(r.GetInt32(0), r.GetInt32(1), r.GetDouble(2) / n, r.GetDouble(3) / n, r.GetDouble(4) / n,
                r.GetDouble(5) / n, r.GetDouble(6), r.GetDouble(7), r.GetDouble(8) / n, r.GetDouble(9)))
            .ToList();
    }

    private static int CountSampleTimes(SqliteConnection c, long serverId, DateTime from, DateTime to) =>
        Query(c, "SELECT COUNT(DISTINCT ts) FROM volume_samples WHERE server_id=$s AND ts BETWEEN $f AND $t",
            ("$s", serverId), ("$f", ToUnix(from)), ("$t", ToUnix(to))).Select(r => r.GetInt32(0)).First();

    public record DatabaseSeriesPoint(string DatabaseName, DateTime Time, double Iops, double Mbps, double ReadLatency, double WriteLatency);

    /// <summary>IO per database over time, for the "by database" chart grouping.</summary>
    public List<DatabaseSeriesPoint> GetDatabaseSeries(long serverId, DateTime from, DateTime to, int bucketSeconds, string? volume)
    {
        bucketSeconds = Math.Max(1, bucketSeconds);
        using var c = Open();
        return Query(c, """
            SELECT f.database_name, (s.ts / $b) * $b AS bts, COUNT(DISTINCT s.ts) AS ticks,
                   SUM(s.read_iops + s.write_iops), SUM(s.read_mbps + s.write_mbps),
                   CASE WHEN SUM(s.read_iops) > 0 THEN SUM(s.read_lat * s.read_iops) / SUM(s.read_iops) ELSE 0 END,
                   CASE WHEN SUM(s.write_iops) > 0 THEN SUM(s.write_lat * s.write_iops) / SUM(s.write_iops) ELSE 0 END
            FROM file_samples s
            JOIN files f ON f.server_id = s.server_id AND f.database_id = s.database_id AND f.file_id = s.file_id
            WHERE s.server_id=$s AND s.ts BETWEEN $f AND $t AND ($v IS NULL OR f.volume = $v)
            GROUP BY f.database_name, bts ORDER BY bts
            """, ("$s", serverId), ("$f", ToUnix(from)), ("$t", ToUnix(to)), ("$b", bucketSeconds), ("$v", volume))
            .Select(r =>
            {
                double ticks = Math.Max(1, r.GetInt32(2));
                return new DatabaseSeriesPoint(r.GetString(0), FromUnix(r.GetInt64(1)), r.GetDouble(3) / ticks, r.GetDouble(4) / ticks, r.GetDouble(5), r.GetDouble(6));
            }).ToList();
    }

    public record WaitStat(string WaitType, double AvgWaitMsPerSec, double AvgMsPerWait, double PeakWaitMsPerSec);

    public List<WaitStat> GetWaitStats(long serverId, DateTime from, DateTime to)
    {
        using var c = Open();
        return Query(c, """
            SELECT wait_type, AVG(wait_ms_per_sec),
                   CASE WHEN SUM(waits_per_sec) > 0 THEN SUM(wait_ms_per_sec) / SUM(waits_per_sec) ELSE 0 END,
                   MAX(wait_ms_per_sec)
            FROM wait_samples WHERE server_id=$s AND ts BETWEEN $f AND $t GROUP BY wait_type HAVING MAX(wait_ms_per_sec) > 0 ORDER BY 2 DESC
            """, ("$s", serverId), ("$f", ToUnix(from)), ("$t", ToUnix(to)))
            .Select(r => new WaitStat(r.GetString(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3))).ToList();
    }

    public record HourCell(string Volume, int DayOfWeek, int Hour, double AvgPressure, double MaxPressure, double AvgIops, double AvgLatency, int Samples);

    /// <summary>Pressure by volume × local hour of day (and day of week) for the heatmaps.</summary>
    public List<HourCell> GetHourlyPressure(long serverId, DateTime from, DateTime to)
    {
        using var c = Open();
        return Query(c, """
            SELECT volume, CAST(strftime('%w', ts, 'unixepoch', 'localtime') AS INTEGER) AS dow,
                   CAST(strftime('%H', ts, 'unixepoch', 'localtime') AS INTEGER) AS hr,
                   AVG(pressure), MAX(pressure), AVG(read_iops + write_iops),
                   CASE WHEN SUM(read_iops + write_iops) > 0 THEN SUM(read_lat * read_iops + write_lat * write_iops) / SUM(read_iops + write_iops) ELSE 0 END,
                   COUNT(*)
            FROM volume_samples WHERE server_id=$s AND ts BETWEEN $f AND $t
            GROUP BY volume, dow, hr
            """, ("$s", serverId), ("$f", ToUnix(from)), ("$t", ToUnix(to)))
            .Select(r => new HourCell(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetInt32(7)))
            .ToList();
    }

    public (DateTime? First, DateTime? Last, long Count) GetDataRange(long serverId)
    {
        using var c = Open();
        return Query(c, "SELECT MIN(ts), MAX(ts), COUNT(*) FROM volume_samples WHERE server_id=$s", ("$s", serverId))
            .Select(r => (r.IsDBNull(0) ? (DateTime?)null : FromUnix(r.GetInt64(0)), r.IsDBNull(1) ? (DateTime?)null : FromUnix(r.GetInt64(1)), r.GetInt64(2)))
            .First();
    }

    public record SpacePoint(int DatabaseId, int FileId, DateTime Time, double SizeMb, double? UsedMb);

    /// <summary>Records allocated and used space per file and keeps the latest used figure on the inventory.</summary>
    public void InsertFileSpace(long serverId, DateTime ts, IEnumerable<(int Db, int File, double SizeMb, double? UsedMb)> rows)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var r in rows)
        {
            Exec(c, "INSERT INTO file_space(server_id,ts,database_id,file_id,size_mb,used_mb) VALUES($s,$t,$d,$f,$sz,$u)",
                ("$s", serverId), ("$t", ToUnix(ts)), ("$d", r.Db), ("$f", r.File), ("$sz", r.SizeMb), ("$u", r.UsedMb));
            if (r.UsedMb != null)
                Exec(c, "UPDATE files SET used_mb=$u WHERE server_id=$s AND database_id=$d AND file_id=$f",
                    ("$s", serverId), ("$d", r.Db), ("$f", r.File), ("$u", r.UsedMb));
        }
        tx.Commit();
    }

    public List<SpacePoint> GetFileSpaceHistory(long serverId, DateTime from, DateTime to)
    {
        using var c = Open();
        return Query(c, "SELECT database_id,file_id,ts,size_mb,used_mb FROM file_space WHERE server_id=$s AND ts BETWEEN $f AND $t ORDER BY ts",
                ("$s", serverId), ("$f", ToUnix(from)), ("$t", ToUnix(to)))
            .Select(r => new SpacePoint(r.GetInt32(0), r.GetInt32(1), FromUnix(r.GetInt64(2)), r.GetDouble(3), r.IsDBNull(4) ? null : r.GetDouble(4)))
            .ToList();
    }

    public void Purge(int retentionDays, int fileRetentionDays)
    {
        using var c = Open();
        var cut = ToUnix(DateTime.Now.AddDays(-retentionDays));
        var fileCut = ToUnix(DateTime.Now.AddDays(-fileRetentionDays));
        Exec(c, """
            DELETE FROM volume_samples WHERE ts < $cut;
            DELETE FROM wait_samples WHERE ts < $cut;
            DELETE FROM file_space WHERE ts < $cut;
            DELETE FROM file_samples WHERE ts < $fcut;
            """, ("$cut", cut), ("$fcut", fileCut));
    }

    public void ClearSamples(long serverId)
    {
        using var c = Open();
        Exec(c, """
            DELETE FROM volume_samples WHERE server_id=$s;
            DELETE FROM file_samples WHERE server_id=$s;
            DELETE FROM wait_samples WHERE server_id=$s;
            DELETE FROM file_space WHERE server_id=$s;
            """, ("$s", serverId));
    }

    public long DatabaseSizeBytes()
    {
        var path = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }
}
