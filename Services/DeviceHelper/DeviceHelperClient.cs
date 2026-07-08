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
