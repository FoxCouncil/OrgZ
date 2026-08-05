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
            // Running as root, we must never execute a binary a lesser user could have swapped
            // out from under us - that turns a device read into arbitrary root code execution.
            if (!OperatingSystem.IsWindows() && !BinaryIsTrustworthy(out var why))
            {
                _log.Fatal("Refusing to run privileged: {Why}", why);
                return 1;
            }

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
        var ownerUid = ReadOwnerUid();

        // A stale socket file from a previous run blocks bind - remove it first. But only
        // if it's actually DEAD: unlinking a live daemon's socket would yank the endpoint
        // out from under it and leave two daemons, one unreachable. A quick connect probe
        // distinguishes the two - a live owner accepts, a stale file refuses.
        if (File.Exists(DeviceHelperProtocol.Endpoint))
        {
            try
            {
                using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await probe.ConnectAsync(new UnixDomainSocketEndPoint(DeviceHelperProtocol.Endpoint), new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
                _log.Error("Another daemon is already serving {Endpoint} — refusing to start a second instance", DeviceHelperProtocol.Endpoint);
                return;
            }
            catch
            {
                // Nobody home - it's a stale file from a dead process.
                File.Delete(DeviceHelperProtocol.Endpoint);
            }
        }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(DeviceHelperProtocol.Endpoint));
        listener.Listen(backlog: 16);

        // The socket now lives in root-owned /var/run, so the PATH can't be hijacked. Perms:
        // when we know the owner UID (recorded by the installer), hand the socket to them and
        // lock everyone else out at the filesystem layer - 0600 + chown owner; root, being the
        // daemon, connects regardless. Without a known owner (a legacy install) we fall back to
        // world-connect, but the peer-cred gate below is the real authorization either way.
        if (!OperatingSystem.IsWindows())
        {
            if (ownerUid is uint owner)
            {
                _ = Chown(DeviceHelperProtocol.Endpoint, owner, unchecked((uint)-1));
                _ = Chmod(DeviceHelperProtocol.Endpoint, 0b110_000_000);   // 0600
            }
            else
            {
                _ = Chmod(DeviceHelperProtocol.Endpoint, 0b110_110_110);   // 0666 (legacy fallback)
            }
        }

        _log.Information("Listening on unix socket {Endpoint} (owner uid {Owner})", DeviceHelperProtocol.Endpoint, ownerUid?.ToString() ?? "unset");
        while (!ct.IsCancellationRequested)
        {
            var conn = await listener.AcceptAsync(ct);
            if (!IsAuthorizedPeer(conn, ownerUid))
            {
                conn.Dispose();
                continue;
            }
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

    /// <summary>The owner UID the installer stamped into the service definition, if any.</summary>
    private static uint? ReadOwnerUid()
        => uint.TryParse(Environment.GetEnvironmentVariable("ORGZ_HELPER_OWNER_UID"), out var uid) ? uid : null;

    /// <summary>
    /// Reads the kernel-verified peer UID off the connection and applies
    /// <see cref="IsPeerAllowed"/>. Split so the policy - the part that decides who may
    /// drive a root daemon - is testable without standing up a socket pair.
    /// </summary>
    private static bool IsAuthorizedPeer(Socket conn, uint? ownerUid)
    {
        var readable = PeerCredentials.TryGetPeerUid(conn, out var peer);
        var allowed = IsPeerAllowed(ownerUid, readable, peer);

        if (!allowed)
        {
            if (readable)
            {
                _log.Warning("Refusing connection from uid {Peer}: not owner {Owner}", peer, ownerUid);
            }
            else
            {
                _log.Warning("Refusing connection: peer credentials unreadable while an owner UID is enforced");
            }
        }

        return allowed;
    }

    /// <summary>
    /// Who may talk to the privileged daemon. Serve only the owner the installer recorded;
    /// root is always allowed, since it can do everything this daemon does anyway and
    /// locking it out only breaks diagnostics.
    ///
    /// Fail CLOSED when an owner is configured but the credentials can't be read - an
    /// unanswerable "who are you" must not become "anyone". Fail OPEN only for a legacy
    /// install that recorded no owner, so upgrading doesn't brick an existing setup;
    /// there, the socket's 0666 mode is all that stands guard, which is why every install
    /// path since has stamped an owner UID.
    /// </summary>
    internal static bool IsPeerAllowed(uint? ownerUid, bool credentialsReadable, uint peerUid)
    {
        if (!credentialsReadable)
        {
            return ownerUid is null;
        }

        return ownerUid is not uint owner || peerUid == owner || peerUid == 0;
    }

    /// <summary>
    /// True unless the running executable (or its directory) could be swapped by a non-root
    /// user - the world-writable cases that would let a device read become root code execution.
    /// A per-user install (exe owned by that user, not world/group writable) passes, since only
    /// that user - the one we serve - can touch it.
    /// </summary>
    private static bool BinaryIsTrustworthy(out string why)
    {
        why = "";
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return true;   // can't determine the path - don't hard-block on that alone
        }

        try
        {
            var exeMode = File.GetUnixFileMode(exe);
            if ((exeMode & UnixFileMode.OtherWrite) != 0)
            {
                why = $"executable {exe} is world-writable";
                return false;
            }

            var dir = Path.GetDirectoryName(exe);
            if (dir is not null)
            {
                var dirMode = File.GetUnixFileMode(dir);
                // A world-writable directory without the sticky bit lets anyone rename the exe aside.
                if ((dirMode & UnixFileMode.OtherWrite) != 0 && (dirMode & UnixFileMode.StickyBit) == 0)
                {
                    why = $"directory {dir} is world-writable without the sticky bit";
                    return false;
                }
                if ((exeMode & UnixFileMode.GroupWrite) != 0 || (dirMode & UnixFileMode.GroupWrite) != 0)
                {
                    _log.Warning("Privileged binary {Exe} is group-writable — verify the group contains no untrusted users", exe);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "binary trust check skipped (filesystem error)");
            return true;
        }

        return true;
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

        // The account actually running the daemon must be able to create each successive pipe
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

    /// <summary>
    /// Serves exactly one request/response on an already-accepted connection. Internal so
    /// the transport can be exercised over a real pipe rather than only a MemoryStream.
    /// NOTE for callers in tests: the reload op ends the process by design.
    /// </summary>
    internal static async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        var request = await DeviceHelperProtocol.ReadMessageAsync<DeviceHelperProtocol.Request>(stream, ct);
        if (request == null)
        {
            return;
        }

        var response = Handle(request);
        await DeviceHelperProtocol.WriteMessageAsync(stream, response, ct);

        if (request.Op == DeviceHelperProtocol.OpReload && response.Ok)
        {
            await stream.FlushAsync(ct);
            _log.Information("Reload requested — exiting so launchd relaunches the updated binary");
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// Reload restarts the process, and a burn or sync mid-flight would die with it -
    /// a half-burned coaster or a half-written device database. Busy means "not now";
    /// the caller retries once the job drains. The exit itself only happens when this
    /// handler answered Ok (see the gate in ServeAsync).
    /// </summary>
    private static DeviceHelperProtocol.Response HandleReload(DeviceHelperProtocol.Request _)
    {
        if (CdServiceOps.CurrentJob is not null)
        {
            return Fail("busy: a disc job is in progress — reload refused");
        }
        if (SyncServiceOps.CurrentJob is not null)
        {
            return Fail("busy: a device sync is in progress — reload refused");
        }
        return Ok();
    }

    // ── Op registry ──────────────────────────────────────────
    // The service host: features contribute privileged ops here (CD burn/erase, iPod
    // sync, library sharing arrive as their own registrations) instead of growing a
    // switch. Built-ins cover liveness, capability discovery, and identity reads.
    // Registration is process-local and must happen before RunAsync starts serving.
    private static readonly Dictionary<string, Func<DeviceHelperProtocol.Request, DeviceHelperProtocol.Response>> _ops = new(StringComparer.Ordinal)
    {
        [DeviceHelperProtocol.OpPing] = _ => Ok(),
        [DeviceHelperProtocol.OpReload] = HandleReload,
        [DeviceHelperProtocol.OpStatus] = HandleStatus,
        [DeviceHelperProtocol.OpReadIdentity] = HandleReadIdentity,
    };

    internal static IReadOnlyCollection<string> RegisteredOps => _ops.Keys;

    /// <summary>Registers a privileged op. Duplicate names throw - two features silently
    /// fighting over one op is a bug, not a configuration.</summary>
    internal static void RegisterOp(string op, Func<DeviceHelperProtocol.Request, DeviceHelperProtocol.Response> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(op);
        ArgumentNullException.ThrowIfNull(handler);
        if (!_ops.TryAdd(op, handler))
        {
            throw new InvalidOperationException($"Device helper op '{op}' is already registered.");
        }
    }

    private static DeviceHelperProtocol.Response Ok(string? resultJson = null)
        => new(DeviceHelperProtocol.Version, Ok: true, null, null, null, null, resultJson);

    private static DeviceHelperProtocol.Response Fail(string error)
        => new(DeviceHelperProtocol.Version, Ok: false, null, null, null, error);

    internal static DeviceHelperProtocol.Response Handle(DeviceHelperProtocol.Request request)
    {
        if (request.Version != DeviceHelperProtocol.Version)
        {
            return Fail($"protocol version mismatch (service {DeviceHelperProtocol.Version}, client {request.Version})");
        }

        if (!_ops.TryGetValue(request.Op, out var handler))
        {
            return Fail($"unknown op '{request.Op}'");
        }

        try
        {
            return handler(request);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "op {Op} failed for {MountPath}", request.Op, request.MountPath);
            return Fail(ex.Message);
        }
    }

    private static DeviceHelperProtocol.Response HandleStatus(DeviceHelperProtocol.Request request)
    {
        var status = new
        {
            protocol = DeviceHelperProtocol.Version,
            service = typeof(DeviceHelperDaemon).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            ops = _ops.Keys.Order().ToArray(),
        };
        return Ok(System.Text.Json.JsonSerializer.Serialize(status));
    }

    private static DeviceHelperProtocol.Response HandleReadIdentity(DeviceHelperProtocol.Request request)
    {
        var id = IPodFirmwarePartition.ReadIdentityElevated(request.MountPath, request.Generation);
        var ok = id.Serial != null || id.Version != null || id.ModelNumber != null;
        _log.Information("read-identity {MountPath}: ok={Ok} serial={Serial} version={Version}", request.MountPath, ok, id.Serial, id.Version);
        // On failure ship the diagnostic tail back so the miss can be diagnosed
        // without root access to the daemon's own log file.
        var diagTail = id.Diagnostic.Length > 1500 ? id.Diagnostic[^1500..] : id.Diagnostic;
        return new(DeviceHelperProtocol.Version, ok, id.Serial, id.Version, id.ModelNumber, ok ? null : diagTail);
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "chmod")]
    private static extern int Chmod(string path, uint mode);

    // owner/group of (uint)-1 means "leave unchanged" - we only ever set the owner.
    [DllImport("libc", SetLastError = true, EntryPoint = "chown")]
    private static extern int Chown(string path, uint owner, uint group);
}
