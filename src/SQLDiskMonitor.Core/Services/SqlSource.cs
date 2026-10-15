using Microsoft.Data.SqlClient;
using SQLDiskMonitor.Models;

namespace SQLDiskMonitor.Services;

public class RawFile
{
    public int DatabaseId, FileId;
    public string DatabaseName = "", LogicalName = "", TypeDesc = "", PhysicalName = "";
    public long Reads, BytesRead, ReadStall, Writes, BytesWritten, WriteStall;
    public long SizePages, MaxSize, Growth;
    public bool IsPercentGrowth;
    public string Volume = "", VolumeLabel = "";
    public long TotalBytes, AvailableBytes;
    public int Pending;

    public RawFile Clone() => (RawFile)MemberwiseClone();
}

public class RawSnapshot
{
    public DateTime Time;
    public List<RawFile> Files = [];
    public Dictionary<string, (long Tasks, long Ms)> Waits = [];
    public string HostName = "", Version = "";
}

public interface ISnapshotSource
{
    Task<RawSnapshot> CaptureAsync(CancellationToken ct);

    /// <summary>Pages used inside each database file, keyed by (database_id, file_id). Heavier, so collected less often.</summary>
    Task<Dictionary<(int Db, int File), long>> CaptureSpaceUsedAsync(CancellationToken ct);
}

/// <summary>Reads cumulative file IO counters, volume mapping and IO waits from a SQL Server instance.</summary>
public class SqlSource(ServerConnection server) : ISnapshotSource
{
    public static readonly string[] IoWaits =
    [
        "PAGEIOLATCH_SH", "PAGEIOLATCH_EX", "PAGEIOLATCH_UP", "WRITELOG", "IO_COMPLETION",
        "ASYNC_IO_COMPLETION", "BACKUPIO", "LOGBUFFER", "WRITE_COMPLETION"
    ];

    private const string SnapshotSql = """
        SET NOCOUNT ON;
        SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

        SELECT vfs.database_id, vfs.file_id, DB_NAME(vfs.database_id), mf.name, mf.type_desc, mf.physical_name,
               vfs.num_of_reads, vfs.num_of_bytes_read, vfs.io_stall_read_ms,
               vfs.num_of_writes, vfs.num_of_bytes_written, vfs.io_stall_write_ms,
               CAST(mf.size AS bigint), CAST(mf.max_size AS bigint), CAST(mf.growth AS bigint), mf.is_percent_growth,
               vs.volume_mount_point, vs.logical_volume_name, vs.total_bytes, vs.available_bytes,
               ISNULL(p.pending, 0)
        FROM sys.dm_io_virtual_file_stats(NULL, NULL) vfs
        JOIN sys.master_files mf ON mf.database_id = vfs.database_id AND mf.file_id = vfs.file_id
        CROSS APPLY sys.dm_os_volume_stats(vfs.database_id, vfs.file_id) vs
        LEFT JOIN (SELECT io_handle, COUNT(*) AS pending FROM sys.dm_io_pending_io_requests GROUP BY io_handle) p
               ON p.io_handle = vfs.file_handle
        WHERE vfs.database_id <> 32767;

        SELECT wait_type, waiting_tasks_count, wait_time_ms FROM sys.dm_os_wait_stats WHERE wait_type IN ({WAITS});

        SELECT CAST(SERVERPROPERTY('ComputerNamePhysicalNetBIOS') AS nvarchar(256)),
               CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)) + ' ' + CAST(SERVERPROPERTY('Edition') AS nvarchar(128));
        """;

    public static string BuildConnectionString(ServerConnection s, string? password = null)
    {
        // A hosting app (Blackcat Studio) supplies the whole connection string, credentials included.
        if (s.ExternalConnectionString is { } external) return external();

        var b = new SqlConnectionStringBuilder
        {
            DataSource = s.Address,
            InitialCatalog = "master",
            TrustServerCertificate = s.TrustCertificate,
            Encrypt = s.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            ConnectTimeout = Math.Max(1, s.Timeout),
            ApplicationName = "SQL Disk Monitor"
        };
        switch (s.Auth)
        {
            case AuthMode.Windows:
                b.IntegratedSecurity = true;
                break;
            case AuthMode.Sql:
                b.UserID = s.Username;
                b.Password = password ?? CredentialStore.LoadPassword(s.Id);
                break;
            case AuthMode.EntraIntegrated:
                b.Authentication = SqlAuthenticationMethod.ActiveDirectoryIntegrated;
                break;
            case AuthMode.EntraPassword:
                // Deprecated by Microsoft (SqlClient 7 marks it obsolete) but still supported; kept for existing saved servers.
#pragma warning disable CS0618
                b.Authentication = SqlAuthenticationMethod.ActiveDirectoryPassword;
#pragma warning restore CS0618
                b.UserID = s.Username;
                b.Password = password ?? CredentialStore.LoadPassword(s.Id);
                break;
        }
        return b.ConnectionString;
    }

    /// <summary>Connects and checks the permissions the collector needs. Returns a description of the instance.</summary>
    public static async Task<string> TestAsync(ServerConnection s, string? password, CancellationToken ct = default)
    {
        await using var c = new SqlConnection(BuildConnectionString(s, password));
        await c.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT @@SERVERNAME, CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)), CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
                   HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE')
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        var name = r.IsDBNull(0) ? s.Address : r.GetString(0);
        var perms = r.IsDBNull(3) ? 0 : r.GetInt32(3);
        var text = $"{name} — SQL Server {r.GetString(1)} ({r.GetString(2)})";
        if (perms != 1) text += "\n\nWarning: this login lacks VIEW SERVER STATE, which the collector needs.";
        return text;
    }

    public async Task<RawSnapshot> CaptureAsync(CancellationToken ct)
    {
        await using var c = new SqlConnection(BuildConnectionString(server));
        await c.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = SnapshotSql.Replace("{WAITS}", string.Join(",", IoWaits.Select(w => $"'{w}'")));
        cmd.CommandTimeout = 30;

        var snap = new RawSnapshot { Time = DateTime.Now };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            snap.Files.Add(new RawFile
            {
                DatabaseId = r.GetInt16(0),
                FileId = r.GetInt16(1),
                DatabaseName = r.IsDBNull(2) ? $"db_{r.GetInt16(0)}" : r.GetString(2),
                LogicalName = r.GetString(3),
                TypeDesc = r.GetString(4),
                PhysicalName = r.GetString(5),
                Reads = r.GetInt64(6),
                BytesRead = r.GetInt64(7),
                ReadStall = r.GetInt64(8),
                Writes = r.GetInt64(9),
                BytesWritten = r.GetInt64(10),
                WriteStall = r.GetInt64(11),
                SizePages = r.GetInt64(12),
                MaxSize = r.GetInt64(13),
                Growth = r.GetInt64(14),
                IsPercentGrowth = r.GetBoolean(15),
                Volume = NormaliseVolume(r.IsDBNull(16) ? "?" : r.GetString(16)),
                VolumeLabel = r.IsDBNull(17) ? "" : r.GetString(17),
                TotalBytes = r.IsDBNull(18) ? 0 : r.GetInt64(18),
                AvailableBytes = r.IsDBNull(19) ? 0 : r.GetInt64(19),
                Pending = r.GetInt32(20)
            });
        }

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            snap.Waits[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2));

        await r.NextResultAsync(ct);
        if (await r.ReadAsync(ct))
        {
            snap.HostName = r.IsDBNull(0) ? "" : r.GetString(0);
            snap.Version = r.IsDBNull(1) ? "" : r.GetString(1);
        }
        return snap;
    }

    // Visits each accessible online database to read FILEPROPERTY(SpaceUsed). Databases that refuse access are skipped.
    private const string SpaceSql = """
        SET NOCOUNT ON;
        CREATE TABLE #used(database_id int, file_id int, used_pages bigint);
        DECLARE @sql nvarchar(max) = N'';
        SELECT @sql += N'BEGIN TRY USE ' + QUOTENAME(name) + N'; INSERT #used SELECT DB_ID(), file_id, CAST(FILEPROPERTY(name, ''SpaceUsed'') AS bigint) FROM sys.database_files; END TRY BEGIN CATCH END CATCH;'
        FROM sys.databases
        WHERE state = 0 AND HAS_DBACCESS(name) = 1 AND source_database_id IS NULL AND is_in_standby = 0;
        EXEC sys.sp_executesql @sql;
        SELECT database_id, file_id, ISNULL(used_pages, 0) FROM #used;
        """;

    public async Task<Dictionary<(int Db, int File), long>> CaptureSpaceUsedAsync(CancellationToken ct)
    {
        await using var c = new SqlConnection(BuildConnectionString(server));
        await c.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = SpaceSql;
        cmd.CommandTimeout = 120;
        var result = new Dictionary<(int, int), long>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result[(r.GetInt32(0), r.GetInt32(1))] = r.GetInt64(2);
        return result;
    }

    public static string NormaliseVolume(string v)
    {
        v = v.Trim();
        if (v.Length == 0) return "?";
        if (!v.EndsWith('\\') && !v.EndsWith('/')) v += v.Contains('/') ? "/" : "\\";
        return v.Length >= 2 && v[1] == ':' ? char.ToUpperInvariant(v[0]) + v[1..] : v;
    }
}
