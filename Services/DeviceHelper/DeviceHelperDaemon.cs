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

    /// <summary>The owner SID the installer stamped into the service definition, if any. Windows' ORGZ_HELPER_OWNER_UID.</summary>
    private static string? ReadOwnerSid()
    {
        var sid = Environment.GetEnvironmentVariable("ORGZ_HELPER_OWNER_SID");
        return string.IsNullOrWhiteSpace(sid) ? null : sid.Trim();
    }

    /// <summary>
    /// Who may drive the LocalSystem service on Windows. The exact counterpart of
    /// <see cref="IsPeerAllowed"/>, and split out for the same reason: this is the policy that
    /// decides who gets to run privileged operations, so it is testable without a pipe.
    ///
    /// Fails CLOSED when an owner is recorded but the caller cannot be identified. Fails OPEN
    /// only when no owner was recorded at all, so an install from before the SID was stamped
    /// keeps working instead of bricking - the same upgrade concession the unix side makes.
    /// </summary>
    internal static bool IsCallerAllowed(string? ownerSid, string? callerSid, bool callerIsAdministrator)
    {
        if (ownerSid is null)
        {
            return true;
        }

        if (string.IsNullOrEmpty(callerSid))
        {
            return false;
        }

        // Administrators and LocalSystem can already do everything this service does, so
        // refusing them buys no security and costs diagnosability.
        return string.Equals(callerSid, ownerSid, StringComparison.OrdinalIgnoreCase)
            || string.Equals(callerSid, "S-1-5-18", StringComparison.OrdinalIgnoreCase)
            || callerIsAdministrator;
    }

    /// <summary>
    /// Resolves the connected client's SID by impersonating it, and applies
    /// <see cref="IsCallerAllowed"/>.
    ///
    /// This is the check the Windows leg never had. The pipe is reachable by every logged-on
    /// user, and the ops behind it stopped being read-only identity queries at protocol v2 -
    /// they now write files and adopt databases as LocalSystem. Without a caller check that is
    /// a local privilege escalation on any machine with the MSI installed.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsAuthorizedCaller(NamedPipeServerStream server, string? ownerSid)
    {
        if (ownerSid is null)
        {
            return true;
        }

        string? callerSid = null;
        var isAdmin = false;

        try
        {
            server.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                callerSid = identity.User?.Value;
                isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            });
        }
        catch (Exception ex)
        {
            // An unanswerable "who are you" must not become "anyone".
            _log.Warning(ex, "Refusing pipe connection: caller identity could not be read");
            return false;
        }

        if (!IsCallerAllowed(ownerSid, callerSid, isAdmin))
        {
            _log.Warning("Refusing pipe connection from {Caller}: not owner {Owner}", callerSid ?? "<unknown>", ownerSid);
            return false;
        }

        return true;
    }

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
        var ownerSid = ReadOwnerSid();

        while (!ct.IsCancellationRequested)
        {
            var server = CreateNamedPipe(ownerSid);
            await server.WaitForConnectionAsync(ct);
            _ = Task.Run(async () =>
            {
                try
                {
                    // Identity is checked before a single byte is read: the ACL keeps honest
                    // callers out, this keeps out anyone who got a handle anyway.
                    if (!IsAuthorizedCaller(server, ownerSid))
                    {
                        return;
                    }

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
    private static NamedPipeServerStream CreateNamedPipe(string? ownerSid)
    {
        // Read/write for the ONE user this helper was installed for - not every authenticated
        // user, which is what this granted until the ops behind it grew teeth. At protocol v2
        // the daemon stopped being read-only identity queries: cd-run and sync-run write a
        // caller-named path and share-start adopts a caller-named database, all as LocalSystem.
        // A pipe any logged-on account can drive therefore hands any local user SYSTEM-level
        // file write and read on a machine that merely has the MSI installed.
        //
        // Clients deliberately do NOT get CreateNewInstance - that would let a user stand up a
        // rogue pipe of the same name and MITM the channel.
        var security = new PipeSecurity();

        // No recorded owner means an install from before the SID was stamped. Keep the old
        // grant so an upgrade does not brick it; the daemon's caller check has the same
        // fail-open rule for exactly this case, and every current install path records one.
        var client = ownerSid is null
            ? new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)
            : new SecurityIdentifier(ownerSid);

        security.AddAccessRule(new PipeAccessRule(client, PipeAccessRights.ReadWrite, AccessControlType.Allow));
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
