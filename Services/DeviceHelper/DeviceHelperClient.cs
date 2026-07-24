// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.IO.Pipes;
using System.Net.Sockets;
using Serilog;

namespace OrgZ.Services.DeviceHelper;

/// <summary>
/// OrgZ-side client for the privileged device-helper service. Every call is best-effort:
/// if the service isn't installed (or is down) the methods return quietly so the caller
/// falls back to the per-operation elevation path. The whole point of the service is that
/// when it IS installed these calls succeed with no UAC / auth prompt at all.
/// </summary>
public static class DeviceHelperClient
{
    private static readonly ILogger _log = Logging.For("DeviceHelperClient");
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Quick liveness check - is the service installed and answering?</summary>
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            var pong = await ExchangeAsync(new DeviceHelperProtocol.Request(
                DeviceHelperProtocol.Version, DeviceHelperProtocol.OpPing, MountPath: "", Generation: null));
            return pong is { Ok: true };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Asks the service to read the iPod's privileged identity (serial + OS version +
    /// Apple model number) from the raw firmware partition. Returns null when the service
    /// isn't reachable - the caller then uses the prompt-based fallback.
    /// </summary>
    public static async Task<DeviceHelperProtocol.Response?> ReadIdentityAsync(string mountPath, string? generation)
    {
        try
        {
            var resp = await ExchangeAsync(new DeviceHelperProtocol.Request(
                DeviceHelperProtocol.Version, DeviceHelperProtocol.OpReadIdentity, mountPath, generation));
            if (resp is { Ok: false })
            {
                _log.Debug("Device helper read-identity failed for {MountPath}: {Error}", mountPath, resp.Error);
            }
            return resp;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Device helper unavailable for read-identity on {MountPath}", mountPath);
            return null;
        }
    }

    /// <summary>
    /// Hands a disc-operation spec to the service's cd-run op. True when the service
    /// accepted the job (progress then flows through the spec's progress file); false
    /// when it's unreachable or refused (not installed, busy, version mismatch).
    /// </summary>
    public static async Task<bool> RunCdSpecAsync(string specPath, string progressPath)
    {
        try
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new { specPath, progressPath });
            var resp = await ExchangeAsync(new DeviceHelperProtocol.Request(
                DeviceHelperProtocol.Version, "cd-run", MountPath: "", Generation: null, PayloadJson: payload));
            if (resp is { Ok: false })
            {
                _log.Debug("Service refused cd-run: {Error}", resp.Error);
            }
            return resp is { Ok: true };
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Service unavailable for cd-run");
            return false;
        }
    }

    /// <summary>
    /// Hands a device sync to the service so it survives the GUI closing. True when
    /// the service accepted it (progress then flows through the progress file);
    /// false when unreachable, busy, or refused - caller syncs in-process instead.
    /// </summary>
    public static async Task<bool> RunSyncAsync(string mountPath, string progressPath, IReadOnlyList<string> mediaIds)
    {
        try
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new { mountPath, progressPath, mediaIds });
            var resp = await ExchangeAsync(new DeviceHelperProtocol.Request(
                DeviceHelperProtocol.Version, SyncServiceOps.OpSyncRun, mountPath, Generation: null, PayloadJson: payload));
            if (resp is { Ok: false })
            {
                _log.Debug("Service refused sync-run: {Error}", resp.Error);
            }
            return resp is { Ok: true };
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Service unavailable for sync-run");
            return false;
        }
    }

    /// <summary>What the service reports about the hosted library share.</summary>
    public sealed record ShareState(bool Sharing, string? Name, int Port);

    /// <summary>Starts hosting the library share. Null when the service is unreachable.</summary>
    public static Task<ShareState?> StartShareAsync(string shareName, int port)
        => ShareOpAsync(Sharing.ShareServiceOps.OpShareStart,
            System.Text.Json.JsonSerializer.Serialize(new { shareName, port }));

    public static Task<ShareState?> StopShareAsync()
        => ShareOpAsync(Sharing.ShareServiceOps.OpShareStop, null);

    public static Task<ShareState?> ShareStatusAsync()
        => ShareOpAsync(Sharing.ShareServiceOps.OpShareStatus, null);

    private static async Task<ShareState?> ShareOpAsync(string op, string? payload)
    {
        try
        {
            var resp = await ExchangeAsync(new DeviceHelperProtocol.Request(
                DeviceHelperProtocol.Version, op, MountPath: "", Generation: null, PayloadJson: payload));
            if (resp is not { Ok: true })
            {
                _log.Debug("Service refused {Op}: {Error}", op, resp?.Error);
                return null;
            }

            return ParseShareState(resp.ResultJson);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Service unavailable for {Op}", op);
            return null;
        }
    }

    /// <summary>Reads a share-op result. Malformed/absent JSON reads as "not sharing".</summary>
    internal static ShareState ParseShareState(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return new ShareState(false, null, 0);
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            var sharing = root.TryGetProperty("sharing", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.True;
            var name = root.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String ? n.GetString() : null;
            var port = root.TryGetProperty("port", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Number ? p.GetInt32() : 0;
            return new ShareState(sharing, name, port);
        }
        catch (System.Text.Json.JsonException)
        {
            return new ShareState(false, null, 0);
        }
    }

    private static async Task<DeviceHelperProtocol.Response?> ExchangeAsync(DeviceHelperProtocol.Request request)
    {
        using var cts = new CancellationTokenSource(CallTimeout);
        await using var stream = await OpenAsync(cts.Token);
        await DeviceHelperProtocol.WriteMessageAsync(stream, request, cts.Token);
        return await DeviceHelperProtocol.ReadMessageAsync<DeviceHelperProtocol.Response>(stream, cts.Token);
    }

    private static async Task<Stream> OpenAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", DeviceHelperProtocol.Endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, ct);
            return pipe;
        }

        // macOS / Linux: unix-domain socket. Socket.Connect is synchronous but immediate for
        // a local socket; run it off the connect path with the same short timeout budget.
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(ConnectTimeout);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(DeviceHelperProtocol.Endpoint), connectCts.Token);
        return new NetworkStream(socket, ownsSocket: true);
    }
}
