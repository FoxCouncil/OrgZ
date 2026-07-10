// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace OrgZ.Services.DeviceHelper;

/// <summary>
/// Wire contract shared by <see cref="DeviceHelperClient"/> (in OrgZ) and
/// <see cref="DeviceHelperDaemon"/> (in the privileged service). One length-prefixed
/// UTF-8 JSON request, one length-prefixed JSON response, per connection. Kept small and
/// versioned so a newer OrgZ can talk to an older installed service (and refuse politely
/// on mismatch) rather than corrupting the stream.
/// </summary>
public static class DeviceHelperProtocol
{
    public const int Version = 1;

    /// <summary>Named pipe (Windows) / unix-socket file (macOS, Linux) the service listens on.
    /// Deliberately under root-owned /var/run, NOT /tmp: /tmp is world-writable, so a local
    /// user could pre-create or symlink the path and race the daemon's bind. /var/run is 0755
    /// root:root - only root creates the socket there, so the path itself can't be hijacked.</summary>
    public static string Endpoint => OperatingSystem.IsWindows()
        ? "orgz-devicehelper"
        : "/var/run/orgz-devicehelper.sock";

    public sealed record Request(int Version, string Op, string MountPath, string? Generation);

    public sealed record Response(int Version, bool Ok, string? Serial, string? FirmwareVersion, string? ModelNumber, string? Error);

    public const string OpReadIdentity = "read-identity";
    public const string OpPing = "ping";
    // Asks the daemon to exit; launchd's KeepAlive immediately relaunches it, picking up an
    // updated on-disk binary. Lets development iterate the helper without re-running the
    // (elevation-prompting) installer each time.
    public const string OpReload = "reload";

    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task WriteMessageAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, _json);
        var lenPrefix = new byte[4];
        BitConverter.TryWriteBytes(lenPrefix, bytes.Length);
        await stream.WriteAsync(lenPrefix, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<T?> ReadMessageAsync<T>(Stream stream, CancellationToken ct)
    {
        var lenPrefix = new byte[4];
        try
        {
            await stream.ReadExactlyAsync(lenPrefix, ct);
        }
        catch (EndOfStreamException)
        {
            return default;   // peer closed before sending - treat as no message
        }

        var len = BitConverter.ToInt32(lenPrefix);
        if (len is <= 0 or > 1_000_000)
        {
            return default;
        }

        var body = new byte[len];
        await stream.ReadExactlyAsync(body, ct);
        return JsonSerializer.Deserialize<T>(body, _json);
    }

    /// <summary>Connects a client socket to the unix-domain endpoint (macOS/Linux).</summary>
    public static Socket ConnectUnixSocket()
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(Endpoint));
        return socket;
    }
}
