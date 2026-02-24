using System.Text.Json.Serialization;

namespace SQLDiskMonitor;

public record IntervalStep(int Seconds, string Label);

public class ServerEntry
{
    public string DisplayName { get; set; } = "";
    public string ServerAddress { get; set; } = "";
    public bool WindowsAuth { get; set; } = true;
    public string Username { get; set; } = "";
    public bool TrustCertificate { get; set; } = true;
    public bool Encrypt { get; set; }
    public int Timeout { get; set; } = 10;
}

public class SnapshotRow
{
    public int DatabaseId { get; set; }
    public string DatabaseName { get; set; } = "";
    public int FileId { get; set; }
    public string Drive { get; set; } = "";
    public string Path { get; set; } = "";
    public string TypeDesc { get; set; } = "";
    public long Reads { get; set; }
    public long ReadStall { get; set; }
    public long Writes { get; set; }
    public long WriteStall { get; set; }
    public long BytesRead { get; set; }
    public long BytesWritten { get; set; }
}

public class Snapshot
{
    public DateTime Timestamp { get; set; }
    public List<SnapshotRow> Rows { get; set; } = new();
}

public class DeltaRow
{
    [JsonPropertyName("databaseName")] public string DatabaseName { get; set; } = "";
    [JsonPropertyName("fileId")] public int FileId { get; set; }
    [JsonPropertyName("drive")] public string Drive { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("typeDesc")] public string TypeDesc { get; set; } = "";
    [JsonPropertyName("readLatency")] public double ReadLatency { get; set; }
    [JsonPropertyName("writeLatency")] public double WriteLatency { get; set; }
    [JsonPropertyName("readIops")] public double ReadIops { get; set; }
    [JsonPropertyName("writeIops")] public double WriteIops { get; set; }
    [JsonPropertyName("readMbps")] public double ReadMbps { get; set; }
    [JsonPropertyName("writeMbps")] public double WriteMbps { get; set; }
    [JsonPropertyName("deltaReads")] public long DeltaReads { get; set; }
    [JsonPropertyName("deltaReadStall")] public long DeltaReadStall { get; set; }
    [JsonPropertyName("deltaWrites")] public long DeltaWrites { get; set; }
    [JsonPropertyName("deltaWriteStall")] public long DeltaWriteStall { get; set; }
}

public class DeltaCapture
{
    [JsonPropertyName("timestamp")] public DateTime Timestamp { get; set; }
    [JsonPropertyName("elapsedSeconds")] public double ElapsedSeconds { get; set; }
    [JsonPropertyName("rows")] public List<DeltaRow> Rows { get; set; } = new();
}

public class SessionData
{
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0";
    [JsonPropertyName("application")] public string Application { get; set; } = "SQL Disk Monitor v1.0 - Blackcat Data Solutions Ltd";
    [JsonPropertyName("server")] public string Server { get; set; } = "";
    [JsonPropertyName("capturedAt")] public DateTime CapturedAt { get; set; }
    [JsonPropertyName("intervalSeconds")] public int IntervalSeconds { get; set; }
    [JsonPropertyName("captures")] public List<DeltaCapture> Captures { get; set; } = new();
}

public class GroupInfo
{
    public string DatabaseName { get; set; } = "";
    public string TypeDesc { get; set; } = "";
    public int FileId { get; set; }
    public string Drive { get; set; } = "";
    public string Path { get; set; } = "";
}

public class ChartPoint
{
    public DateTime Time { get; set; }
    public double Value { get; set; }
    public string AllMetrics { get; set; } = "";
}
