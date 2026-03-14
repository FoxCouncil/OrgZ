// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Style", "IDE0130:Namespace does not match folder structure", Justification = "<Pending>", Scope = "namespace", Target = "~N:OrgZ")]

#if WINDOWS
[assembly: SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Windows-only COM interop", Scope = "type", Target = "~T:OrgZ.Services.ButtonPressedHandlerFactory")]
[assembly: SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Windows-only COM interop", Scope = "type", Target = "~T:OrgZ.Services.SmtcService")]
[assembly: SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Windows-only COM interop", Scope = "type", Target = "~T:OrgZ.Services.TaskbarThumbBarService")]
#endif
