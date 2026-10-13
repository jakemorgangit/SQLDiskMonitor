using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using OxyPlot;
using SQLDiskMonitor.Controls;
using SQLDiskMonitor.Data;
using SQLDiskMonitor.Models;

namespace SQLDiskMonitor.Services.Reports;

public class ReportOptions
{
    public bool Summary { get; set; } = true;
    public bool Disks { get; set; } = true;
    public bool Heatmap { get; set; } = true;
    public bool Charts { get; set; } = true;
    public bool Findings { get; set; } = true;
    public bool Space { get; set; } = true;
    public bool Costs { get; set; } = true;
    public bool Waits { get; set; } = true;
    public bool Files { get; set; }
}

/// <summary>
/// Builds a self-contained, print-friendly HTML storage report (charts embedded as images) that also renders to PDF.
/// Always uses the light palette so it prints cleanly whatever the app theme.
/// </summary>
public static class ReportBuilder
{
    private record Data(AnalysisResult Analysis, List<DiskConfig> Disks, List<DbFile> Files, List<VolumeSample> Series, List<DiskNote> Notes);

    public static async Task<string> BuildAsync(Store store, ServerConnection server, AppSettings cfg, DateTime from, DateTime to, ReportOptions o, string appVersion)
    {
        int bucket = (int)Math.Max(cfg.SampleIntervalSeconds, (to - from).TotalSeconds / 500);
        var data = await Task.Run(() => new Data(
            Analytics.Analyse(store, server.Id, from, to, cfg), store.GetDisks(server.Id), store.GetFiles(server.Id),
            store.GetVolumeSeries(server.Id, from, to, bucket), store.GetNotes(server.Id)));

        // Charts are drawn on the UI thread with the light palette.
        var charts = new List<(string Title, string Png)>();
        if (o.Charts && data.Series.Count > 0)
        {
            Palette.Override = Palette.LightPalette();
            try
            {
                charts.Add(("Disk pressure score", Chart(data, from, to, "Pressure", "score", s => s.Pressure, 100, true)));
                charts.Add(("IOPS", Chart(data, from, to, "IOPS", "IO/s", s => s.TotalIops, half: true)));
                charts.Add(("Throughput", Chart(data, from, to, "Throughput", "MB/s", s => s.TotalMbps, half: true)));
                charts.Add(("Read latency", Chart(data, from, to, "Read latency", "ms", s => s.ReadLatencyMs, half: true)));
                charts.Add(("Write latency", Chart(data, from, to, "Write latency", "ms", s => s.WriteLatencyMs, half: true)));
            }
            finally
            {
                Palette.Override = null;
            }
        }

        return Html(server, cfg, from, to, o, appVersion, data, charts);
    }

    private static string Chart(Data d, DateTime from, DateTime to, string title, string unit, Func<VolumeSample, double> value, double? max = null, bool bands = false, bool half = false)
    {
        var m = SQLDiskMonitor.Controls.Charts.TimeChart(title, unit, max);
        m.Title = null;
        SQLDiskMonitor.Controls.Charts.SetSpan(m, to - from);
        if (bands) SQLDiskMonitor.Controls.Charts.AddPressureBands(m);
        int i = 0;
        foreach (var g in d.Series.GroupBy(s => s.Volume).OrderBy(g => g.Key))
            m.Series.Add(SQLDiskMonitor.Controls.Charts.Line(g.Key, i++, g.OrderBy(s => s.Timestamp).Select(s => (s.Timestamp, value(s))), unit));
        SQLDiskMonitor.Controls.Charts.AddNotes(m, d.Notes, from, to, labels: false);
        m.Background = OxyColors.White;

        using var ms = new MemoryStream();
        new OxyPlot.Wpf.PngExporter { Width = half ? 620 : 1000, Height = half ? 250 : 240, Resolution = 144 }.Export(m, ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string N(double v, string f = "N0") => v.ToString(f, CultureInfo.CurrentCulture);

    private static string Band(double p) => Pressure.Band(p);
    private static string BandClass(double p) => p >= Pressure.Critical ? "crit" : p >= Pressure.High ? "high" : p >= Pressure.Elevated ? "warn" : "good";
    private static string Pill(double p) => $"<span class=\"pill {BandClass(p)}\">{N(p)} {Band(p)}</span>";

    private static string HeatColor(double p)
    {
        // Light palette: green, amber, orange, red; low scores fade towards white.
        var (r, g, b) = p >= Pressure.Critical ? (207, 34, 46) : p >= Pressure.High ? (188, 76, 0) : p >= Pressure.Elevated ? (191, 135, 0) : (26, 127, 55);
        var a = Math.Clamp(0.18 + p / 100 * 0.82, 0.18, 1);
        return $"rgba({r},{g},{b},{a.ToString("0.00", CultureInfo.InvariantCulture)})";
    }

    private static string Html(ServerConnection s, AppSettings cfg, DateTime from, DateTime to, ReportOptions o, string version, Data d, List<(string Title, string Png)> charts)
    {
        var a = d.Analysis;
        var cur = cfg.CurrencySymbol;
        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Storage report · {{E(s.Name)}}</title>
            <style>
            @page { size: A4; margin: 14mm 12mm; }
            :root { --text:#1f2328; --muted:#57606a; --subtle:#8c959f; --line:#d8dee4; --soft:#f6f8fa; --accent:#0969da;
                    --good:#1a7f37; --warn:#9a6700; --high:#bc4c00; --crit:#cf222e; }
            * { box-sizing: border-box; }
            body { font-family: "Segoe UI", system-ui, sans-serif; color: var(--text); margin: 0; background: #fff; font-size: 12.5px; line-height: 1.45; }
            .page { max-width: 1060px; margin: 0 auto; padding: 28px 24px 40px; }
            header { display: flex; justify-content: space-between; align-items: flex-end; border-bottom: 3px solid var(--accent); padding-bottom: 14px; margin-bottom: 22px; }
            header h1 { font-size: 26px; font-weight: 600; margin: 0 0 4px; }
            header .meta { color: var(--muted); font-size: 12px; }
            header .brand { text-align: right; color: var(--muted); font-size: 11.5px; }
            header .brand b { color: var(--accent); font-size: 13px; }
            h2 { font-size: 16px; font-weight: 600; margin: 30px 0 10px; padding-bottom: 6px; border-bottom: 1px solid var(--line); break-after: avoid; }
            h3 { font-size: 13px; font-weight: 600; margin: 16px 0 6px; color: var(--muted); }
            p.lead { font-size: 13.5px; margin: 0 0 14px; }
            .kpis { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 10px; margin: 6px 0 4px; }
            .kpi { background: var(--soft); border: 1px solid var(--line); border-radius: 8px; padding: 10px 12px; break-inside: avoid; }
            .kpi .l { font-size: 10px; font-weight: 600; letter-spacing: .04em; color: var(--subtle); text-transform: uppercase; }
            .kpi .v { font-size: 20px; font-weight: 600; margin-top: 2px; overflow-wrap: anywhere; }
            .kpi .v.sm { font-size: 14px; padding-top: 4px; }
            .kpi .s { font-size: 11px; color: var(--muted); }
            table { border-collapse: collapse; width: 100%; font-size: 11.5px; }
            th, td { text-align: left; padding: 5px 7px; border-bottom: 1px solid #eaeef2; vertical-align: top; }
            th { color: var(--muted); font-weight: 600; background: var(--soft); border-bottom: 1px solid var(--line); }
            td.n, th.n { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
            tr { break-inside: avoid; }
            .muted { color: var(--muted); } .small { font-size: 10.5px; }
            .pill { display: inline-block; padding: 1px 8px; border-radius: 10px; font-size: 10.5px; font-weight: 600; color: #fff; white-space: nowrap; }
            .pill.good { background: var(--good); } .pill.warn { background: var(--warn); } .pill.high { background: var(--high); } .pill.crit { background: var(--crit); }
            .heat td { padding: 0; height: 22px; text-align: center; font-size: 9.5px; color: #1f2328; border: 2px solid #fff; border-radius: 3px; }
            .heat td.lbl { text-align: left; padding: 0 8px 0 0; font-weight: 600; font-size: 11px; white-space: nowrap; }
            .heat th { background: none; border: none; font-weight: 400; font-size: 9.5px; text-align: center; padding: 2px 0; color: var(--subtle); }
            .legend span { display: inline-block; width: 10px; height: 10px; border-radius: 2px; margin: 0 4px 0 12px; vertical-align: -1px; }
            .chart { break-inside: avoid; margin: 6px 0 14px; }
            .charts2 { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
            .chart img { width: 100%; border: 1px solid #eaeef2; border-radius: 6px; }
            .chart .t { font-weight: 600; font-size: 12px; margin-bottom: 4px; }
            .finding { border-left: 4px solid var(--subtle); background: var(--soft); padding: 9px 12px; margin: 8px 0; border-radius: 0 6px 6px 0; break-inside: avoid; }
            .finding.Critical { border-color: var(--crit); } .finding.High { border-color: var(--high); } .finding.Medium { border-color: var(--warn); } .finding.Low { border-color: var(--accent); }
            .finding .sev { font-size: 10px; font-weight: 700; letter-spacing: .05em; color: var(--muted); }
            .finding.Critical .sev { color: var(--crit); } .finding.High .sev { color: var(--high); } .finding.Medium .sev { color: var(--warn); }
            .finding .t { font-weight: 600; font-size: 13px; margin: 2px 0; }
            .finding .r { color: var(--muted); margin-top: 4px; }
            .bar { height: 6px; background: #e3e8ee; border-radius: 3px; overflow: hidden; min-width: 70px; }
            .bar i { display: block; height: 100%; border-radius: 3px; }
            footer { margin-top: 36px; padding-top: 10px; border-top: 1px solid var(--line); color: var(--subtle); font-size: 10.5px; display: flex; justify-content: space-between; }
            @media print { .page { padding: 0; max-width: none; } h2 { margin-top: 22px; } }
            </style></head><body><div class="page">
            <header>
              <div>
                <h1>Storage report · {{E(s.Name)}}</h1>
                <div class="meta">{{E(s.HostName)}}{{(string.IsNullOrEmpty(s.Version) ? "" : " · " + E(s.Version))}}</div>
                <div class="meta">Period {{from:ddd d MMM yyyy HH:mm}} – {{to:ddd d MMM yyyy HH:mm}} ({{Span(to - from)}})</div>
              </div>
              <div class="brand"><b>SQL Disk Monitor</b><br>Generated {{DateTime.Now:d MMM yyyy HH:mm}}<br>v{{E(version)}}</div>
            </header>
            """);

        var azure = s.Azure;
        var disks = a.Disks;

        if (o.Summary)
        {
            var worst = disks.OrderByDescending(x => x.P95Pressure).FirstOrDefault();
            int crit = a.Findings.Count(f => f.Severity == Severity.Critical), high = a.Findings.Count(f => f.Severity == Severity.High);
            var first = a.Space.FirstToFill;
            sb.Append("<h2>Summary</h2><p class=\"lead\">");
            sb.Append($"{disks.Count} disk(s) and {d.Files.Select(f => f.DatabaseId).Distinct().Count()} database(s) were monitored over {Span(to - from)}. ");
            if (worst != null)
                sb.Append($"The busiest disk was <b>{E(worst.Volume)} {E(worst.Config?.Label)}</b> at p95 pressure {N(worst.P95Pressure)} ({Band(worst.P95Pressure).ToLowerInvariant()})" +
                          (worst.PeakHour >= 0 ? $", peaking around {worst.PeakHour:00}:00. " : ". "));
            sb.Append(crit + high == 0 ? "There are no critical or high findings." : $"There {(crit + high == 1 ? "is" : "are")} <b>{crit} critical</b> and <b>{high} high</b> finding(s) below.");
            sb.Append("</p><div class=\"kpis\">");
            if (worst != null) Kpi(sb, "Highest p95 pressure", Pill(worst.P95Pressure), $"{E(worst.Volume)} {E(worst.Config?.Label)}");
            Kpi(sb, "Findings", $"{a.Findings.Count}", $"{crit} critical · {high} high");
            Kpi(sb, "Capacity", $"{N(a.Space.TotalGb)} GB", $"{N(a.Space.UsedGb)} GB used · {N(a.Space.FreeGb)} GB free");
            if (first != null) Kpi(sb, "First to fill", first.DaysToFullText, $"{E(first.Volume)} at {first.GrowthText}");
            if (cfg.CostsEnabled && d.Disks.Sum(x => x.MonthlyCost) > 0) Kpi(sb, "Storage cost", $"{E(cur)}{N(d.Disks.Sum(x => x.MonthlyCost), "N2")}", "per month");
            if (azure != null) Kpi(sb, "Azure VM", E(azure.VmSize), $"disk limit {E(azure.CapText)}", small: true);
            sb.Append("</div>");
        }

        if (o.Disks && disks.Count > 0)
        {
            sb.Append("<h2>Disks</h2><table><tr><th>Disk</th><th>Names</th><th>LUN</th><th>Tier</th><th class=n>Limit IOPS</th><th class=n>Limit MB/s</th>" +
                      "<th class=n>p95 IOPS</th><th class=n>p95 MB/s</th><th class=n>Read ms</th><th class=n>Write ms</th><th>p95 pressure</th><th class=n>Free</th></tr>");
            foreach (var x in disks)
            {
                var c = x.Config;
                var names = string.Join("<br>", new[] { c?.Label, c?.DriveName, c?.DiskName }.Where(v => !string.IsNullOrWhiteSpace(v)).Select(E));
                sb.Append($"<tr><td><b>{E(x.Volume)}</b></td><td>{names}</td><td>{c?.Lun}</td><td>{E(c?.Tier)}</td>" +
                          $"<td class=n>{(c?.MaxIops > 0 ? N(c.MaxIops) : "—")}</td><td class=n>{(c?.MaxMbps > 0 ? N(c.MaxMbps) : "—")}</td>" +
                          $"<td class=n>{N(x.P95Iops)}</td><td class=n>{N(x.P95Mbps, "N1")}</td><td class=n>{N(x.AvgReadLatency, "N1")}</td><td class=n>{N(x.AvgWriteLatency, "N1")}</td>" +
                          $"<td>{Pill(x.P95Pressure)}</td><td class=n>{N(x.FreePct)}%</td></tr>");
                if (c is { HasAzure: true })
                {
                    var az = c.AzureDisks;
                    sb.Append($"<tr><td></td><td colspan=11 class=\"muted small\">Azure: {string.Join(" · ", az.Select(z => E(z.Summary)))}" +
                              (az.Count == 1 && az[0].NextTier != "" ? $" · next size up {E(az[0].NextTierText)}" : "") +
                              $" · expandable: {E(az[0].Expandable)}</td></tr>");
                }
                if (!string.IsNullOrWhiteSpace(c?.Notes)) sb.Append($"<tr><td></td><td colspan=11 class=\"muted small\">{E(c!.Notes)}</td></tr>");
            }
            sb.Append("</table>");
            if (azure != null)
                sb.Append($"<p class=\"muted small\">Azure VM {E(azure.Name)} ({E(azure.VmSize)}, {E(azure.Location)}): uncached disk limit {E(azure.CapText)}; {E(azure.SlotsText)}.</p>");
        }

        if (o.Heatmap && a.Hourly.Count > 0)
        {
            sb.Append("<h2>Pressure by hour of day</h2><p class=\"muted\">Average pressure per disk for each hour, across the period.</p><table class=\"heat\"><tr><th></th>");
            for (int h = 0; h < 24; h++) sb.Append($"<th>{h:00}</th>");
            sb.Append("</tr>");
            foreach (var g in a.Hourly.GroupBy(h => h.Volume).OrderBy(g => g.Key))
            {
                var label = d.Disks.FirstOrDefault(x => x.Volume == g.Key)?.Label;
                sb.Append($"<tr><td class=lbl>{E(g.Key)} <span class=\"muted\">{E(label)}</span></td>");
                var byHour = g.GroupBy(c => c.Hour).ToDictionary(x => x.Key, x => x.Sum(c => c.AvgPressure * c.Samples) / Math.Max(1, x.Sum(c => c.Samples)));
                for (int h = 0; h < 24; h++)
                    sb.Append(byHour.TryGetValue(h, out var v) ? $"<td style=\"background:{HeatColor(v)};color:{(v >= 55 ? "#fff" : "#1f2328")}\">{N(v)}</td>" : "<td style=\"background:#f6f8fa\"></td>");
                sb.Append("</tr>");
            }
            sb.Append("</table><div class=\"legend small muted\" style=\"margin-top:6px\"><span style=\"background:#1a7f37\"></span>Healthy &lt;40" +
                      "<span style=\"background:#bf8700\"></span>Elevated 40–70<span style=\"background:#bc4c00\"></span>High 70–90<span style=\"background:#cf222e\"></span>Critical 90+</div>");
        }

        if (charts.Count > 0)
        {
            sb.Append("<h2>Trends</h2>");
            string ChartHtml((string Title, string Png) c) =>
                $"<div class=\"chart\"><div class=\"t\">{E(c.Title)}</div><img alt=\"{E(c.Title)}\" src=\"data:image/png;base64,{c.Png}\"></div>";
            // Pressure full width; IOPS with throughput and read with write latency side by side.
            sb.Append(ChartHtml(charts[0]));
            for (int i = 1; i < charts.Count; i += 2)
                sb.Append("<div class=\"charts2\">" + ChartHtml(charts[i]) + (i + 1 < charts.Count ? ChartHtml(charts[i + 1]) : "") + "</div>");
        }

        if (o.Findings)
        {
            sb.Append("<h2>Findings and recommendations</h2>");
            if (a.Findings.Count == 0) sb.Append("<p>No issues found in this period.</p>");
            foreach (var f in a.Findings)
                sb.Append($"<div class=\"finding {f.Severity}\"><div class=sev>{f.SeverityText} · {E(f.Category)}{(f.Volume != "" ? " · " + E(f.Volume) : "")}</div>" +
                          $"<div class=t>{E(f.Title)}</div><div>{E(f.Detail)}</div>{(f.Recommendation != "" ? $"<div class=r>{E(f.Recommendation)}</div>" : "")}</div>");
        }

        if (o.Space && a.Space.Volumes.Count > 0)
        {
            sb.Append("<h2>Space</h2><table><tr><th>Disk</th><th class=n>Total GB</th><th class=n>Used GB</th><th>Used</th><th class=n>Free GB</th>" +
                      "<th class=n>Free in files GB</th><th class=n>Headroom GB</th><th class=n>Volume growth</th><th class=n>Data growth</th><th class=n>Headroom lasts</th></tr>");
            foreach (var v in a.Space.Volumes)
                sb.Append($"<tr><td><b>{E(v.Volume)}</b> <span class=muted>{E(v.Label)}</span></td><td class=n>{N(v.TotalGb)}</td><td class=n>{N(v.UsedGb)}</td>" +
                          $"<td><div class=bar><i style=\"width:{N(v.UsedPct, "0")}%;background:{(v.UsedPct >= 90 ? "#cf222e" : v.UsedPct >= 75 ? "#bc4c00" : "#0969da")}\"></i></div></td>" +
                          $"<td class=n>{N(v.FreeGb)}</td><td class=n>{(v.FreeInFilesGb is { } fi ? N(fi, "N1") : "—")}</td><td class=n>{N(v.EffectiveFreeGb)}</td>" +
                          $"<td class=n>{E(v.GrowthText)}</td><td class=n>{E(v.DataGrowthText)}</td><td class=n>{E(v.EffectiveDaysToFullText)}</td></tr>");
            sb.Append("</table><h3>Largest databases</h3><table><tr><th>Database</th><th>Disks</th><th class=n>Data GB</th><th class=n>Log GB</th>" +
                      "<th class=n>Used GB</th><th class=n>Free in files GB</th><th class=n>Used MB/day</th><th class=n>Autogrowths</th></tr>");
            foreach (var db in a.Space.Databases.Take(15))
                sb.Append($"<tr><td>{E(db.Database)}</td><td>{E(db.Volumes)}</td><td class=n>{N(db.DataGb, "N1")}</td><td class=n>{N(db.LogGb, "N1")}</td>" +
                          $"<td class=n>{(db.UsedGb is { } u ? N(u, "N1") : "—")}</td><td class=n>{(db.FreeInFilesGb is { } fr ? N(fr, "N1") : "—")}</td>" +
                          $"<td class=n>{(db.UsedMbPerDay is { } r ? N(r) : "—")}</td><td class=n>{db.GrowthEvents}</td></tr>");
            sb.Append("</table>");
        }

        if (o.Costs && cfg.CostsEnabled && d.Disks.Any(x => x.MonthlyCost > 0))
        {
            double total = d.Disks.Sum(x => x.MonthlyCost);
            sb.Append($"<h2>Costs</h2><p>Storage for this server costs <b>{E(cur)}{N(total, "N2")}</b> a month ({E(cur)}{N(total * 12)} a year).</p>" +
                      "<table><tr><th>Disk</th><th>Tier</th><th class=n>Monthly</th><th class=n>p95 used of limit</th><th class=n>Headroom cost</th></tr>");
            foreach (var c in d.Disks.Where(x => x.MonthlyCost > 0).OrderByDescending(x => x.MonthlyCost))
            {
                var st = disks.FirstOrDefault(x => x.Volume == c.Volume);
                double util = st == null ? 0 : Math.Max(st.IopsUtilP95, st.MbpsUtilP95);
                sb.Append($"<tr><td><b>{E(c.Volume)}</b> <span class=muted>{E(c.Label)}</span></td><td>{E(c.Tier)}</td><td class=n>{E(cur)}{N(c.MonthlyCost, "N2")}</td>" +
                          $"<td class=n>{(!c.HasLimits ? "—" : util > 100 ? "over baseline (burst)" : N(util) + "%")}</td><td class=n>{(c.HasLimits ? E(cur) + N(c.MonthlyCost * Math.Clamp(1 - util / 100, 0, 1), "N2") : "—")}</td></tr>");
            }
            sb.Append("</table>");
        }

        if (o.Waits && a.Waits.Count > 0)
        {
            sb.Append("<h2>IO waits</h2><table><tr><th>Wait</th><th class=n>Avg wait ms/s</th><th class=n>Avg ms per wait</th><th class=n>Peak ms/s</th></tr>");
            foreach (var w in a.Waits)
                sb.Append($"<tr><td>{E(w.WaitType)}</td><td class=n>{N(w.AvgWaitMsPerSec, "N1")}</td><td class=n>{N(w.AvgMsPerWait, "N2")}</td><td class=n>{N(w.PeakWaitMsPerSec)}</td></tr>");
            sb.Append("</table>");
        }

        if (o.Files && d.Files.Count > 0)
        {
            var stats = a.FileStats.ToDictionary(x => (x.DatabaseId, x.FileId));
            sb.Append("<h2>Appendix: database files</h2><table><tr><th>Database</th><th>File</th><th>Type</th><th>Disk</th><th class=n>Size GB</th>" +
                      "<th class=n>Used GB</th><th>Growth</th><th class=n>Avg IOPS</th><th class=n>Read ms</th><th class=n>Write ms</th><th>Path</th></tr>");
            foreach (var f in d.Files.OrderBy(f => f.Volume).ThenBy(f => f.DatabaseName).ThenBy(f => f.FileId))
            {
                stats.TryGetValue((f.DatabaseId, f.FileId), out var st);
                sb.Append($"<tr><td>{E(f.DatabaseName)}</td><td>{E(f.LogicalName)}</td><td>{f.TypeDesc}</td><td>{E(f.Volume)}</td><td class=n>{N(f.SizeMb / 1024, "N2")}</td>" +
                          $"<td class=n>{(f.UsedMb is { } u ? N(u / 1024, "N2") : "—")}</td><td>{E(f.GrowthDisplay)}</td><td class=n>{(st == null ? "—" : N(st.ReadIops + st.WriteIops, "N1"))}</td>" +
                          $"<td class=n>{(st == null ? "—" : N(st.ReadLatency, "N1"))}</td><td class=n>{(st == null ? "—" : N(st.WriteLatency, "N1"))}</td><td class=\"muted small\">{E(f.PhysicalName)}</td></tr>");
            }
            sb.Append("</table>");
        }

        sb.Append($"""
            <footer><span>SQL Disk Monitor v{E(version)} · {E(s.Name)}</span><span>Pressure = max(capacity used vs limits, latency score). Latency thresholds: data {N(cfg.DataLatencyWarn)} ms, log {N(cfg.LogLatencyWarn)} ms.</span></footer>
            </div></body></html>
            """);
        return sb.ToString();
    }

    private static void Kpi(StringBuilder sb, string label, string value, string sub, bool small = false) =>
        sb.Append($"<div class=\"kpi\"><div class=l>{E(label)}</div><div class=\"v{(small ? " sm" : "")}\">{value}</div><div class=s>{sub}</div></div>");

    private static string Span(TimeSpan t) =>
        t.TotalDays >= 1.5 ? $"{t.TotalDays:0} days" : t.TotalHours >= 1.5 ? $"{t.TotalHours:0} hours" : $"{t.TotalMinutes:0} minutes";
}
