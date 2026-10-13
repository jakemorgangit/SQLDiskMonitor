using System.Globalization;
using System.Text;
using SQLDiskMonitor.Models;

namespace SQLDiskMonitor.Services;

public class DiskImportResult
{
    /// <summary>Disks that had at least one field filled in.</summary>
    public List<DiskConfig> Changed { get; } = [];
    public int FieldsFilled { get; set; }
    public int RowsMatched { get; set; }
    /// <summary>Volume values from the file that don't match any disk on the server.</summary>
    public List<string> UnmatchedRows { get; } = [];
    public List<string> IgnoredColumns { get; } = [];
    public List<string> RecognisedColumns { get; } = [];
    /// <summary>Per-disk summary, e.g. "F:\  LUN, Disk name, Max IOPS".</summary>
    public List<string> Details { get; } = [];
}

/// <summary>
/// Fills empty disk settings from a CSV file. Rows are matched to volumes by drive letter or mount point:
/// a column named Volume / Drive letter if there is one, otherwise the first column. Existing values are never overwritten.
/// </summary>
public static class DiskCsvImport
{
    private enum Kind { Text, Int, Number }

    private record Field(string Name, Kind Kind, string[] Aliases, Func<DiskConfig, bool> IsEmpty, Action<DiskConfig, object> Set);

    private static readonly Field[] Fields =
    [
        new("Label", Kind.Text, ["label", "volumelabel", "volumename"], d => string.IsNullOrWhiteSpace(d.Label), (d, v) => d.Label = (string)v),
        new("Drive name", Kind.Text, ["drivename", "drive name", "friendlyname"], d => string.IsNullOrWhiteSpace(d.DriveName), (d, v) => d.DriveName = (string)v),
        new("Disk name", Kind.Text, ["diskname", "resourcename", "azurediskname", "azureresourcename", "manageddisk", "manageddiskname", "cloudname"],
            d => string.IsNullOrWhiteSpace(d.DiskName), (d, v) => d.DiskName = (string)v),
        new("LUN", Kind.Int, ["lun", "lunnumber", "scsilun"], d => d.Lun == null, (d, v) => d.Lun = (int)v),
        new("Disk number", Kind.Int, ["disknumber", "disk#", "diskno", "diskindex", "physicaldisk"], d => d.DiskNumber == null, (d, v) => d.DiskNumber = (int)v),
        new("Device / model", Kind.Text, ["devicemodel", "device/model", "device", "model"], d => string.IsNullOrWhiteSpace(d.DeviceModel), (d, v) => d.DeviceModel = (string)v),
        new("Tier", Kind.Text, ["tier", "sku", "disktype", "disktier", "type"], d => string.IsNullOrWhiteSpace(d.Tier), (d, v) => d.Tier = (string)v),
        new("Max IOPS", Kind.Number, ["maxiops", "iops", "provisionediops", "baselineiops"], d => d.MaxIops == 0, (d, v) => d.MaxIops = (double)v),
        new("Max MB/s", Kind.Number, ["maxmbps", "maxmb/s", "mbps", "mb/s", "throughput", "maxthroughput", "throughputmbps", "provisionedmbps"],
            d => d.MaxMbps == 0, (d, v) => d.MaxMbps = (double)v),
        new("Burst IOPS", Kind.Number, ["burstiops", "maxburstiops"], d => d.BurstIops == 0, (d, v) => d.BurstIops = (double)v),
        new("Burst MB/s", Kind.Number, ["burstmbps", "burstmb/s", "burstthroughput", "maxburstmbps"], d => d.BurstMbps == 0, (d, v) => d.BurstMbps = (double)v),
        new("Size GB", Kind.Number, ["sizegb", "size", "disksize", "disksizegb", "provisionedsizegb", "capacitygb"], d => d.SizeGb == 0, (d, v) => d.SizeGb = (double)v),
        new("Monthly cost", Kind.Number, ["monthlycost", "cost", "costpermonth", "pricepermonth", "monthlyprice"], d => d.CostMonthly == 0, (d, v) => d.CostMonthly = (double)v),
        new("Cost per GB", Kind.Number, ["costpergb", "pricepergb"], d => d.CostPerGb == 0, (d, v) => d.CostPerGb = (double)v),
        new("Cost per IOPS", Kind.Number, ["costperiops", "priceperiops"], d => d.CostPerIops == 0, (d, v) => d.CostPerIops = (double)v),
        new("Cost per MB/s", Kind.Number, ["costpermbps", "costpermb/s", "pricepermbps"], d => d.CostPerMbps == 0, (d, v) => d.CostPerMbps = (double)v),
        new("Tags", Kind.Text, ["tags", "tag"], d => string.IsNullOrWhiteSpace(d.Tags), (d, v) => d.Tags = (string)v),
        new("Notes", Kind.Text, ["notes", "note", "description", "comments"], d => string.IsNullOrWhiteSpace(d.Notes), (d, v) => d.Notes = (string)v),
    ];

    private static readonly string[] VolumeAliases = ["volume", "drive", "driveletter", "disk letter", "diskletter", "letter", "mountpoint", "path"];

    /// <summary>
    /// A ready-to-fill CSV: the column headers the import understands, and one row per volume with its current values.
    /// </summary>
    public static string BuildTemplate(IEnumerable<DiskConfig> disks)
    {
        static string Q(string s) => s.IndexOfAny(['"', ',', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        static string N(double v) => v == 0 ? "" : v.ToString("0.####", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", new[] { "Volume" }.Concat(Fields.Select(f => f.Name)).Select(Q)));
        foreach (var d in disks)
        {
            string[] values =
            [
                d.Volume, d.Label, d.DriveName, d.DiskName, d.Lun?.ToString() ?? "", d.DiskNumber?.ToString() ?? "", d.DeviceModel, d.Tier,
                N(d.MaxIops), N(d.MaxMbps), N(d.BurstIops), N(d.BurstMbps), N(d.SizeGb), N(d.CostMonthly), N(d.CostPerGb), N(d.CostPerIops),
                N(d.CostPerMbps), d.Tags, d.Notes.ReplaceLineEndings(" ")
            ];
            sb.AppendLine(string.Join(",", values.Select(Q)));
        }
        return sb.ToString();
    }

    private static string Key(string header) =>
        new string(header.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is '#' or '/').ToArray());

    public static DiskImportResult Apply(string csv, IReadOnlyList<DiskConfig> disks)
    {
        var result = new DiskImportResult();
        var rows = Parse(csv);
        if (rows.Count == 0) return result;

        var header = rows[0];
        int volCol = Array.FindIndex(header, h => VolumeAliases.Select(Key).Contains(Key(h)));
        if (volCol < 0) volCol = 0;

        var map = new Dictionary<int, Field>();
        for (int i = 0; i < header.Length; i++)
        {
            if (i == volCol) continue;
            var k = Key(header[i]);
            var f = Fields.FirstOrDefault(f => f.Aliases.Select(Key).Contains(k));
            if (f != null && !map.ContainsValue(f))
            {
                map[i] = f;
                result.RecognisedColumns.Add(header[i].Trim());
            }
            else if (!string.IsNullOrWhiteSpace(header[i]))
            {
                result.IgnoredColumns.Add(header[i].Trim());
            }
        }

        foreach (var row in rows.Skip(1))
        {
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            var raw = volCol < row.Length ? row[volCol].Trim() : "";
            if (raw.Length == 0) continue;
            var volume = NormaliseVolume(raw);
            var disk = disks.FirstOrDefault(d => d.Volume.Equals(volume, StringComparison.OrdinalIgnoreCase));
            if (disk == null)
            {
                result.UnmatchedRows.Add(raw);
                continue;
            }
            result.RowsMatched++;

            var filled = new List<string>();
            foreach (var (col, f) in map)
            {
                if (col >= row.Length) continue;
                var text = row[col].Trim();
                if (text.Length == 0 || !f.IsEmpty(disk)) continue;
                object? value = f.Kind switch
                {
                    Kind.Text => text,
                    Kind.Int => ParseNumber(text) is { } n && n >= 0 && n == Math.Floor(n) ? (int)n : null,
                    _ => ParseNumber(text) is { } n && n > 0 ? n : null
                };
                if (value == null) continue;
                f.Set(disk, value);
                filled.Add(f.Name);
            }

            if (filled.Count > 0)
            {
                if (!result.Changed.Contains(disk)) result.Changed.Add(disk);
                result.FieldsFilled += filled.Count;
                result.Details.Add($"{disk.Volume}  {string.Join(", ", filled)}");
            }
        }
        return result;
    }

    /// <summary>"F", "f:", "F:\" and "F:/" all become "F:\"; mount points keep their path.</summary>
    public static string NormaliseVolume(string v)
    {
        v = v.Trim().Trim('"');
        if (v.Length == 1 && char.IsLetter(v[0])) v += ":";
        return SqlSource.NormaliseVolume(v.Replace('/', '\\'));
    }

    /// <summary>Lenient number parsing: ignores currency symbols and units ("£106.04", "5,000", "200 MB/s").</summary>
    public static double? ParseNumber(string s)
    {
        var cleaned = new string(s.Where(c => char.IsDigit(c) || c is '.' or ',' or '-').ToArray());
        if (cleaned.Length == 0) return null;
        if (double.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return d;
        if (double.TryParse(cleaned, NumberStyles.Number, CultureInfo.CurrentCulture, out d)) return d;
        return null;
    }

    /// <summary>Minimal RFC 4180 parser. Detects comma, semicolon or tab from the header line.</summary>
    public static List<string[]> Parse(string text)
    {
        text = text.TrimStart('\uFEFF');
        var firstLine = text.Split('\n')[0];
        char sep = new[] { ',', ';', '\t' }.OrderByDescending(c => firstLine.Count(x => x == c)).First();

        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == sep) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                rows.Add(row.ToArray()); row.Clear();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }
        return rows;
    }
}
