using SQLDiskMonitor.Data;
using SQLDiskMonitor.Models;
using SQLDiskMonitor.Services;
using SQLDiskMonitor.ViewModels;

namespace SQLDiskMonitor.Hosting;

/// <summary>A server connection owned by the hosting app. Credentials stay with the host; only a connection string factory is passed in.</summary>
/// <param name="DisplayName">What the user calls the server in the host.</param>
/// <param name="Server">Server as typed (host, host\instance, host,port…). Used to find the server's saved disk settings and history.</param>
/// <param name="IsProduction">The host marked the server as production.</param>
/// <param name="ConnectionString">Returns a ready-to-use Microsoft.Data.SqlClient connection string.</param>
public sealed record HostedServer(string DisplayName, string Server, bool IsProduction, Func<string> ConnectionString);

/// <summary>What a hosting app (for example Blackcat Studio) offers SQL Disk Monitor.</summary>
public interface IDiskMonitorHost
{
    /// <summary>Servers currently connected in the host, which the user may add to the monitor.</summary>
    IReadOnlyList<HostedServer> AvailableServers();

    /// <summary>Writes to the host's error log.</summary>
    void Log(Exception ex, string? message = null);
}

/// <summary>
/// Process-wide state when SQL Disk Monitor runs inside another app: one settings file, one history database and one
/// collector shared by every open view, so two tabs on the same server never sample it twice. Collection for a server
/// stops when the last view using it closes. UI thread only.
/// </summary>
public static class DiskMonitorRuntime
{
    private static Store? _store;
    private static AppSettings? _settings;
    private static Collector? _collector;
    private static readonly Dictionary<long, (ServerConnection Server, int Refs)> Attached = [];

    public static bool IsHosted => _store != null;

    /// <summary>Opens settings and history in <paramref name="dataFolder"/>. Only the first call has any effect.</summary>
    public static void EnsureInitialized(string dataFolder)
    {
        if (_store != null) return;
        AppSettings.DataFolderOverride = dataFolder;
        var settings = AppSettings.Load();
        // The host owns updates, start-up, the tray and the theme.
        settings.CheckForUpdates = false;
        settings.MinimizeToTray = false;
        settings.StartWithWindows = false;
        settings.StartMinimized = false;
        var store = new Store();
        _settings = settings;
        _collector = new Collector(store, () => _settings);
        _store = store;
    }

    /// <summary>A view model for one hosted view. Dispose it when the view closes.</summary>
    public static MainViewModel CreateViewModel(IDiskMonitorHost host)
    {
        if (_store == null) throw new InvalidOperationException("Call DiskMonitorRuntime.EnsureInitialized first.");
        return new MainViewModel(_store, _settings!, _collector, host);
    }

    /// <summary>Finds (or creates) the saved row for a hosted server, starts collection and counts the reference.</summary>
    internal static ServerConnection Acquire(HostedServer hosted)
    {
        var store = _store ?? throw new InvalidOperationException("Not initialised.");
        var live = Attached.Values.FirstOrDefault(a => SameServer(a.Server, hosted.Server)).Server;
        if (live != null)
        {
            live.ExternalConnectionString = hosted.ConnectionString; // the newest tab's connection
            live.IsProduction = hosted.IsProduction;
            Attached[live.Id] = (live, Attached[live.Id].Refs + 1);
            return live;
        }

        var saved = store.GetServers();
        var server = saved.FirstOrDefault(s => !s.IsDemo && SameServer(s, hosted.Server));
        if (server == null)
        {
            server = new ServerConnection { Name = UniqueName(saved, hosted), Address = hosted.Server, Monitor = true };
        }
        else
        {
            // Follow renames in the host, unless another saved server already uses that name (names are unique).
            var desired = hosted.DisplayName.Trim();
            if (desired.Length > 0 && server.Name != desired && !saved.Any(o => o.Id != server.Id && o.Name == desired))
                server.Name = desired;
        }
        server.ExternalConnectionString = hosted.ConnectionString;
        server.IsProduction = hosted.IsProduction;
        store.SaveServer(server);

        Attached[server.Id] = (server, 1);
        if (server.Monitor) _collector!.Start(server);
        return server;
    }

    /// <summary>Drops one reference; collection for the server stops with the last one.</summary>
    internal static void Release(long serverId)
    {
        if (!Attached.TryGetValue(serverId, out var a)) return;
        if (a.Refs > 1)
        {
            Attached[serverId] = (a.Server, a.Refs - 1);
            return;
        }
        Attached.Remove(serverId);
        _collector?.Stop(serverId);
    }

    internal static bool SameServer(ServerConnection s, string server) => s.Address.Trim().Equals(server.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string UniqueName(List<ServerConnection> saved, HostedServer hosted)
    {
        var baseName = hosted.DisplayName.Trim().Length > 0 ? hosted.DisplayName.Trim() : hosted.Server.Trim();
        var name = baseName;
        if (saved.Any(s => s.Name == name)) name = $"{baseName} ({hosted.Server.Trim()})";
        for (int i = 2; saved.Any(s => s.Name == name); i++) name = $"{baseName} ({i})";
        return name;
    }
}
