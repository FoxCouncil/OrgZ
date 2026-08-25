// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OrgZ.Services.DeviceHelper;

/// <summary>
/// The two Terminal Services calls needed to answer "who is sitting at this machine".
///
/// Used when running as LocalSystem, where WindowsIdentity.GetCurrent describes the machine
/// rather than the person. See <see cref="DeviceHelperInstaller.WindowsConsoleUserSid"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WtsNativeMethods
{
    internal enum WtsInfoClass
    {
        WTSUserName = 5,
        WTSDomainName = 7,
    }

    /// <summary>
    /// The session attached to the physical console, or 0xFFFFFFFF when none is. Follows Fast
    /// User Switching, so it names the session at the screen now.
    /// </summary>
    [LibraryImport("kernel32.dll")]
    internal static partial uint WTSGetActiveConsoleSessionId();

    /// <summary>
    /// <c>WTS_CURRENT_SERVER_HANDLE</c> is IntPtr.Zero - the local machine. The returned
    /// buffer belongs to WTS and must go back through <see cref="WTSFreeMemory"/>.
    /// </summary>
    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSQuerySessionInformationW(
        IntPtr server,
        uint sessionId,
        WtsInfoClass infoClass,
        out IntPtr buffer,
        out uint bytesReturned);

    [LibraryImport("wtsapi32.dll")]
    internal static partial void WTSFreeMemory(IntPtr memory);
}
