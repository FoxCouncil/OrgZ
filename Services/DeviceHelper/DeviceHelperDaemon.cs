// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Serilog;

namespace OrgZ.Services.DeviceHelper;

/// <summary>
/// The privileged half of the device helper - a long-lived listener installed to run as
/// root (macOS/Linux) or LocalSystem (Windows). Because it already holds the privilege, it
/// opens the iPod's raw disk directly and answers OrgZ's identity queries with NO per-call
/// UAC/auth prompt. Reached from <see cref="Program"/> when OrgZ is launched with
/// <c>--device-helper</c> by the installed service definition.
/// </summary>
public static class DeviceHelperDaemon
{
    private static readonly ILogger _log = Logging.For("DeviceHelperDaemon");

    public static async Task<int> RunAsync(CancellationToken ct = default)
    {
        _log.Information("Device helper daemon starting (uid/privileged listener) on {Endpoint}", DeviceHelperProtocol.Endpoint);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                await RunNamedPipeAsync(ct);
            }
            else
            {
                await RunUnixSocketAsync(ct);
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            _log.Fatal(ex, "Device helper daemon crashed");
            return 1;
        }
    }

    private static async Task RunUnixSocketAsync(CancellationToken ct)
    {
        // A stale socket file from a previous run blocks bind - remove it first.
        if (File.Exists(DeviceHelperProtocol.Endpoint))
        {
            File.Delete(DeviceHelperProtocol.Endpoint);
        }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(DeviceHelperProtocol.Endpoint));
        listener.Listen(backlog: 16);

        // Any local user must be able to connect (the socket is created root-owned). The
        // helper only ever READS device identity, so world-connect is not a privilege leak.
        if (!OperatingSystem.IsWindows())
        {
            _ = Chmod(DeviceHelperProtocol.Endpoint, 0b110_110_110);   // 0666
        }

        _log.Information("Listening on unix socket {Endpoint}", DeviceHelperProtocol.Endpoint);
        while (!ct.IsCancellationRequested)
        {
            var conn = await listener.AcceptAsync(ct);
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var stream = new NetworkStream(conn, ownsSocket: true);
                    await ServeAsync(stream, ct);
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "connection handler error");
                }
            }, ct);
        }
    }

    private static async Task RunNamedPipeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var server = CreateNamedPipe();
            await server.WaitForConnectionAsync(ct);
            _ = Task.Run(async () =>
            {
                try
                {
                    await ServeAsync(server, ct);
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "pipe handler error");
                }
                finally
                {
                    server.Dispose();
                }
            }, ct);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateNamedPipe()
    {
        // Grant authenticated users read/write so a non-elevated OrgZ can connect to the
        // LocalSystem-owned pipe; the daemon only exposes read-only identity queries. Clients
        // deliberately do NOT get CreateNewInstance - that would let any user stand up a rogue
        // pipe of the same name and MITM the channel.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        // The account actually running the daemon must be able to create EACH successive pipe
        // instance (FullControl carries CreateNewInstance). LocalSystem is covered above in the
        // installed service; this line additionally covers a daemon run under any other account
        // (e.g. a developer running it directly) so the accept loop doesn't die on instance #2.
        var owner = WindowsIdentity.GetCurrent().User;
        if (owner != null)
        {
            security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            DeviceHelperProtocol.Endpoint, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 0, security);
    }

    private static async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        var request = await DeviceHelperProtocol.ReadMessageAsync<DeviceHelperProtocol.Request>(stream, ct);
        if (request == null)
        {
            return;
        }

        var response = Handle(request);
        await DeviceHelperProtocol.WriteMessageAsync(stream, response, ct);

        if (request.Op == DeviceHelperProtocol.OpReload)
        {
            await stream.FlushAsync(ct);
            _log.Information("Reload requested — exiting so launchd relaunches the updated binary");
            Environment.Exit(0);
        }
    }

    private static DeviceHelperProtocol.Response Handle(DeviceHelperProtocol.Request request)
    {
        if (request.Version != DeviceHelperProtocol.Version)
        {
            return new(DeviceHelperProtocol.Version, Ok: false, null, null, null, $"protocol version mismatch (service {DeviceHelperProtocol.Version}, client {request.Version})");
        }

        switch (request.Op)
        {
            case DeviceHelperProtocol.OpPing:
            case DeviceHelperProtocol.OpReload:
            {
                return new(DeviceHelperProtocol.Version, Ok: true, null, null, null, null);
            }

            case DeviceHelperProtocol.OpReadIdentity:
            {
                try
                {
                    var id = IPodFirmwarePartition.ReadIdentityElevated(request.MountPath, request.Generation);
                    var ok = id.Serial != null || id.Version != null || id.ModelNumber != null;
                    _log.Information("read-identity {MountPath}: ok={Ok} serial={Serial} version={Version}", request.MountPath, ok, id.Serial, id.Version);
                    // On failure ship the diagnostic tail back so the miss can be diagnosed
                    // without root access to the daemon's own log file.
                    var diagTail = id.Diagnostic.Length > 1500 ? id.Diagnostic[^1500..] : id.Diagnostic;
                    return new(DeviceHelperProtocol.Version, ok, id.Serial, id.Version, id.ModelNumber, ok ? null : diagTail);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "read-identity failed for {MountPath}", request.MountPath);
                    return new(DeviceHelperProtocol.Version, Ok: false, null, null, null, ex.Message);
                }
            }

            default:
            {
                return new(DeviceHelperProtocol.Version, Ok: false, null, null, null, $"unknown op '{request.Op}'");
            }
        }
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "chmod")]
    private static extern int Chmod(string path, uint mode);
}
