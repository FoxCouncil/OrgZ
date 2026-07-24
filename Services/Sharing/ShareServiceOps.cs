// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

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

    public sealed record ShareStartPayload(string? ShareName, int? Port);

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
        catch (JsonException)
        {
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

        lock (_gate)
        {
            if (_server is not null)
            {
                // Already sharing: idempotent success, reporting what's actually live.
                return Ok(_server);
            }

            try
            {
                _server = ServerFactory(name, port);
                _log.Information("Library share started: \"{Name}\" on {Port}", name, port);
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
