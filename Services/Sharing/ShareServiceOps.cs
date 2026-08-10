// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net.NetworkInformation;
using System.Text.Json;
using OrgZ.Services.DeviceHelper;
using Serilog;

namespace OrgZ.Services.Sharing;

/// <summary>
/// Hosts the library share inside the background service, so a closed GUI doesn't take
/// the library off the air. Three ops: share-start, share-stop, share-status.
/// </summary>
public static class ShareServiceOps
{
    private static readonly ILogger _log = Logging.For("ShareServiceOps");

    public const string OpShareStart = "share-start";
    public const string OpShareStop = "share-stop";
    public const string OpShareStatus = "share-status";

    /// <summary>Default share port - IANA-unassigned, stable so firewall rules stick.</summary>
    public const int DefaultPort = 7391;

    private static readonly Lock _gate = new();
    private static LibraryShareServer? _server;

    /// <summary>
    /// Where the live share's config survives a service restart. Deliberately the daemon
    /// account's OWN app data (systemprofile on Windows, root elsewhere): restore points a
    /// privileged process at whatever database this file names, so only the service and
    /// administrators may be able to write it.
    /// </summary>
    internal static string ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrgZ", "share.json");

    private static readonly JsonSerializerOptions _configJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public sealed record ShareStartPayload(string? ShareName, int? Port, string? LibraryDb = null);

    /// <summary>Test seam: builds (and starts) the server.</summary>
    internal static Func<string, int, LibraryShareServer> ServerFactory = (name, port) =>
    {
        var server = new LibraryShareServer(name, port);
        server.Start();
        return server;
    };

    public static void RegisterAll()
    {
        DeviceHelperDaemon.RegisterOp(OpShareStart, HandleStart);
        DeviceHelperDaemon.RegisterOp(OpShareStop, HandleStop);
        DeviceHelperDaemon.RegisterOp(OpShareStatus, HandleStatus);
    }

    /// <summary>Lenient parse: a share can start with no payload at all (defaults).</summary>
    internal static ShareStartPayload ParseStartPayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new ShareStartPayload(null, null);
        }

        try
        {
            return JsonSerializer.Deserialize<ShareStartPayload>(payloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new ShareStartPayload(null, null);
        }
        catch (JsonException ex)
        {
            // Defaults are the right recovery, but a torn/garbled config must leave a trail -
            // "share silently didn't restore" was undiagnosable without it.
            _log.Warning(ex, "Share start payload unparseable - using defaults");
            return new ShareStartPayload(null, null);
        }
    }

    /// <summary>Ports outside the usable range (or privileged) fall back to the default.</summary>
    internal static int ResolvePort(int? requested)
        => requested is > 1024 and <= 65535 ? requested.Value : DefaultPort;

    internal static string ResolveName(string? requested)
        => string.IsNullOrWhiteSpace(requested) ? $"{Environment.MachineName} Library" : requested.Trim();

    internal static DeviceHelperProtocol.Response HandleStart(DeviceHelperProtocol.Request request)
    {
        var payload = ParseStartPayload(request.PayloadJson);
        var name = ResolveName(payload.ShareName);
        var port = ResolvePort(payload.Port);

        // The service runs as LocalSystem, whose %APPDATA% holds an empty library - so
        // the client tells us where the real database lives. Found the hard way: a
        // service-hosted share answered every request with a connection reset, because
        // LoadAll threw on the systemprofile's tableless db and the handler aborted.
        var adopted = MediaCache.AdoptClientDatabase(payload.LibraryDb);
        if (adopted)
        {
            _log.Information("Serving library database {Db}", payload.LibraryDb);
        }
        else if (payload.LibraryDb is not null)
        {
            _log.Warning("Ignoring library db path {Db}: not a rooted, existing file", payload.LibraryDb);
        }

        lock (_gate)
        {
            if (_server is not null)
            {
                // Already sharing: idempotent success, reporting what's actually live.
                // A re-start naming a database still updates the persisted config, so
                // the next restart replays what's being served now.
                if (adopted)
                {
                    PersistConfig(_server.ShareName, _server.Port, payload.LibraryDb);
                }
                return Ok(_server);
            }

            try
            {
                _server = ServerFactory(name, port);
                _log.Information("Library share started: \"{Name}\" on {Port}", name, port);
                PersistConfig(name, port, adopted ? payload.LibraryDb : null);
                return Ok(_server);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Library share failed to start");
                _server = null;
                return new(DeviceHelperProtocol.Version, Ok: false, null, null, null, ex.Message);
            }
        }
    }

    internal static DeviceHelperProtocol.Response HandleStop(DeviceHelperProtocol.Request request)
    {
        lock (_gate)
        {
            _server?.Dispose();
            _server = null;
            DeleteConfig();
        }

        return new(DeviceHelperProtocol.Version, Ok: true, null, null, null, null,
            JsonSerializer.Serialize(new { sharing = false }));
    }

    internal static DeviceHelperProtocol.Response HandleStatus(DeviceHelperProtocol.Request request)
    {
        lock (_gate)
        {
            return _server is null
                ? new(DeviceHelperProtocol.Version, Ok: true, null, null, null, null, JsonSerializer.Serialize(new { sharing = false }))
                : Ok(_server);
        }
    }

    private static DeviceHelperProtocol.Response Ok(LibraryShareServer server)
        => new(DeviceHelperProtocol.Version, Ok: true, null, null, null, null,
            JsonSerializer.Serialize(new { sharing = true, name = server.ShareName, port = server.Port }));

    // ── Restart survival ──────────────────────────────────────
    // A share the SERVICE brings back (reboot, crash, update relaunch) has no client
    // attached to hand it the library path, so the last successful start writes its
    // config down and the daemon replays it at startup. Stop retires the config -
    // a share turned off stays off across restarts.

    private static void PersistConfig(string name, int port, string? libraryDb)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            // Atomic: a torn config made the share silently not restore after a restart.
            Helpers.AtomicFile.WriteAllBytes(ConfigPath, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ShareStartPayload(name, port, libraryDb), _configJson)));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Share config not persisted to {Path}; a service restart will not bring the share back", ConfigPath);
        }
    }

    private static void DeleteConfig()
    {
        try
        {
            File.Delete(ConfigPath);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Stale share config at {Path} could not be removed; a service restart may bring the share back", ConfigPath);
        }
    }

    /// <summary>
    /// Called once at daemon startup. Waits (bounded) for the network before restoring:
    /// at boot the service can come up before any NIC has an address, and the mDNS
    /// advertiser enumerates interfaces exactly once at Start.
    /// </summary>
    public static async Task RestoreOnStartupAsync()
    {
        if (!File.Exists(ConfigPath))
        {
            return;
        }

        for (var waited = 0; waited < 30_000 && !NetworkInterface.GetIsNetworkAvailable(); waited += 1_000)
        {
            await Task.Delay(1_000);
        }

        RestorePersistedShare();
    }

    /// <summary>
    /// Replays the persisted share config. Refuses to start without the owner's database -
    /// restoring one without it would serve the daemon account's empty library, which is
    /// the exact bug the libraryDb payload exists to fix.
    /// </summary>
    internal static void RestorePersistedShare()
    {
        ShareStartPayload? config;
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return;
            }

            config = JsonSerializer.Deserialize<ShareStartPayload>(File.ReadAllText(ConfigPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Share config at {Path} is unreadable; share not restored", ConfigPath);
            return;
        }

        if (config is null || !MediaCache.AdoptClientDatabase(config.LibraryDb))
        {
            _log.Warning("Share not restored: persisted config names no usable library database ({Db})", config?.LibraryDb ?? "none");
            return;
        }

        lock (_gate)
        {
            if (_server is not null)
            {
                return;   // a client's share-start beat us to it
            }

            var name = ResolveName(config.ShareName);
            var port = ResolvePort(config.Port);
            try
            {
                _server = ServerFactory(name, port);
                _log.Information("Library share restored: \"{Name}\" on {Port}, serving {Db}", name, port, config.LibraryDb);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Library share failed to restore");
                _server = null;
            }
        }
    }

    /// <summary>Test hook: drops any running share without going through the op.</summary>
    internal static void ResetForTests()
    {
        lock (_gate)
        {
            _server?.Dispose();
            _server = null;
        }
    }
}
