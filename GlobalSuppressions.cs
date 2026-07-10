// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Style", "IDE0130:Namespace does not match folder structure", Justification = "<Pending>", Scope = "namespace", Target = "~N:OrgZ")]

// These types compile on every platform but reach their platform-specific APIs only behind a
// runtime OperatingSystem.Is* guard the analyzer can't follow across method boundaries. The WMI
// (System.Management) paths are Windows-only and gated by OperatingSystem.IsWindows(); the
// device-helper daemon's named-pipe path is likewise Windows-gated and its GetUnixFileMode /
// chmod path is gated by !OperatingSystem.IsWindows(). Suppressing keeps the CA1416 gate meaningful
// for genuinely unguarded call sites elsewhere.
[assembly: SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Windows WMI paths guarded by OperatingSystem.IsWindows()", Scope = "type", Target = "~T:OrgZ.Services.DeviceDetectionService")]
[assembly: SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Windows WMI paths guarded by OperatingSystem.IsWindows()", Scope = "type", Target = "~T:OrgZ.Services.DeviceFingerprint")]
[assembly: SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Windows COM ProgID path guarded by OperatingSystem.IsWindows()", Scope = "type", Target = "~T:OrgZ.Services.DeviceEjector")]
[assembly: SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Windows named-pipe and unix file-mode paths each guarded by an OperatingSystem.Is* check", Scope = "type", Target = "~T:OrgZ.Services.DeviceHelper.DeviceHelperDaemon")]

#if WINDOWS
[assembly: SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Windows-only COM interop", Scope = "type", Target = "~T:OrgZ.Services.ButtonPressedHandlerFactory")]
[assembly: SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Windows-only COM interop", Scope = "type", Target = "~T:OrgZ.Services.SmtcService")]
[assembly: SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Windows-only COM interop", Scope = "type", Target = "~T:OrgZ.Services.TaskbarThumbBarService")]
[assembly: SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Windows-only service host (#if WINDOWS), runs only as a Windows service", Scope = "type", Target = "~T:OrgZ.Services.DeviceHelper.DeviceHelperWindowsService")]
#endif
