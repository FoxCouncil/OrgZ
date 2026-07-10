// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace OrgZ.Services.DeviceHelper;

/// <summary>
/// The OS-verified credentials of the process on the other end of a unix-domain socket.
/// Nothing in the payload can be trusted to say who the caller is, but the peer UID is
/// stamped by the kernel and cannot be spoofed - so it's the only sound answer to "who is
/// actually connected". The root daemon uses it to serve none but its owner.
/// </summary>
internal static class PeerCredentials
{
    // Linux: getsockopt(SOL_SOCKET, SO_PEERCRED) fills struct ucred { pid_t pid; uid_t uid; gid_t gid; }.
    private const int SolSocketLinux = 1;
    private const int SoPeercredLinux = 17;

    [DllImport("libc", SetLastError = true, EntryPoint = "getsockopt")]
    private static extern int GetSockOpt(int fd, int level, int optname, byte[] optval, ref uint optlen);

    // macOS / BSD: getpeereid hands back the effective uid/gid of the peer directly.
    [DllImport("libc", SetLastError = true, EntryPoint = "getpeereid")]
    private static extern int GetPeerEid(int fd, out uint euid, out uint egid);

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUidNative();

    /// <summary>The real UID of the current process - on the install path, the invoking user before any elevation.</summary>
    public static uint CurrentUid() => GetUidNative();

    /// <summary>
    /// Kernel-verified UID of the socket's peer. Returns false (and uid = 0) when the platform
    /// has no supported mechanism or the syscall fails; callers must choose fail-open vs
    /// fail-closed explicitly rather than reading 0 as "nobody".
    /// </summary>
    public static bool TryGetPeerUid(Socket socket, out uint uid)
    {
        uid = 0;
        var fd = (int)socket.Handle;

        if (OperatingSystem.IsMacOS())
        {
            return GetPeerEid(fd, out uid, out _) == 0;
        }

        if (OperatingSystem.IsLinux())
        {
            var buf = new byte[12];        // pid(4) uid(4) gid(4)
            uint len = 12;
            if (GetSockOpt(fd, SolSocketLinux, SoPeercredLinux, buf, ref len) == 0)
            {
                uid = BitConverter.ToUInt32(buf, 4);
                return true;
            }
        }

        return false;
    }
}
