# SQL Disk Monitor

Real-time disk I/O performance monitoring for SQL Server. Tracks read/write latency, IOPS, and throughput across all database files with live charts.

**v1.0** by [Jake Morgan](https://blackcat.wales) @ Blackcat Data Solutions Ltd

---

## Features

- **Six live charts** in a 3×2 grid — Avg Read/Write Latency (ms), Read/Write IOPS, Read/Write Throughput (MB/s)
- **Configurable capture interval** — from 1 second to 5 minutes via slider
- **Saved connections** — stored securely in Windows Credential Manager with support for Windows Auth and SQL Auth
- **Grouping modes** — view data grouped by Database, Drive, or individual File
- **Interactive legend** — click to show/hide series, expand/collapse database file groups
- **Hover tooltips** — dark-themed tracker that follows the cursor across data points
- **Session save/load** — export to JSON, reload later with a date/time range filter
- **CSV export** — full delta data for offline analysis
- **Chart image export** — save the current chart view as PNG
- **Diagnostic dump** — Ctrl+D captures a raw before/after snapshot to verify physical I/O activity
- **Dark theme** — sleek navy/blue interface styled for long monitoring sessions
- **Standalone exe** — single-file self-contained deployment, no runtime install required

## Screenshot

![SQL Disk Monitor](https://blackcat.wales/assets/img/SQLDiskMonitor.gif)

## Requirements

- **OS:** Windows 10/11 (x64)
- **Target:** SQL Server 2016+ (uses `sys.dm_io_virtual_file_stats` and `sys.master_files`)
- **Permissions:** `VIEW SERVER STATE` on the target SQL Server instance

No .NET runtime installation is needed — the published exe is fully self-contained.

## Download

Grab the latest `SQLDiskMonitor.exe` from the [Releases](../../releases) page.

## Build from Source

Prerequisites: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

```bash
cd SQLDiskMonitor/SQLDiskMonitor
dotnet publish -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true
```

The output exe will be in `bin/Release/net8.0-windows/win-x64/publish/`.

## Usage

1. **Launch** `SQLDiskMonitor.exe`
2. **Add a server** — click "+ Add Server" in the left sidebar, enter connection details, and save
3. **Connect** — double-click a saved server (or right-click → Connect)
4. **Set the interval** — drag the slider to choose capture frequency (1s–5min)
5. **Start capturing** — click the green **Start** button (or press F5)
6. **Filter** — use the Drive, DB, and Group dropdowns to focus the view
7. **Toggle series** — click legend items to show/hide individual databases, drives, or files
8. **Stop** — click **Stop** (or press F6)

### Keyboard Shortcuts

| Key | Action |
|---|---|
| F5 | Start capture |
| F6 | Stop capture |
| Ctrl+S | Save session to JSON |
| Ctrl+O | Load session from JSON |
| Ctrl+D | Diagnostic dump |

### Connection Options

| Option | Description |
|---|---|
| Windows Auth | Uses the current Windows identity (Kerberos/NTLM) |
| SQL Auth | Username/password — password stored in Windows Credential Manager |
| Trust Certificate | Skips TLS certificate validation (useful for self-signed certs) |
| Encrypt | Forces TLS encryption on the connection |
| Timeout | Connection timeout in seconds (default 10) |

## How It Works

On each capture tick the tool queries `sys.dm_io_virtual_file_stats` joined to `sys.master_files` to get cumulative I/O counters for every database file. It computes deltas between consecutive snapshots to derive:

| Metric | Calculation |
|---|---|
| Avg Read Latency (ms) | Δ `io_stall_read_ms` / Δ `num_of_reads` |
| Avg Write Latency (ms) | Δ `io_stall_write_ms` / Δ `num_of_writes` |
| Read IOPS | Δ `num_of_reads` / elapsed seconds |
| Write IOPS | Δ `num_of_writes` / elapsed seconds |
| Read Throughput (MB/s) | Δ `num_of_bytes_read` / 1 048 576 / elapsed seconds |
| Write Throughput (MB/s) | Δ `num_of_bytes_written` / 1 048 576 / elapsed seconds |

These are **physical I/O** metrics. Reads served entirely from the buffer cache will show as zero — this is expected and healthy. If you need to force physical reads for testing, run `DBCC DROPCLEANBUFFERS` (not recommended on production).

## Tech Stack

- .NET 8 / WPF
- [OxyPlot](https://oxyplot.github.io/) for charting
- [Microsoft.Data.SqlClient](https://github.com/dotnet/SqlClient) for SQL Server connectivity
- Windows Credential Manager (P/Invoke to `advapi32.dll`) for secure credential storage

## License

MIT

## Author

**Jake Morgan** - [Blackcat Data Solutions Ltd](https://blackcat.wales)
