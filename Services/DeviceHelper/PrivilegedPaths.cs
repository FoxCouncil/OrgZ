// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Security.AccessControl;
using System.Security.Principal;
using Serilog;

namespace OrgZ.Services.DeviceHelper;

/// <summary>
/// Decides whether a path a CLIENT named may be touched by the privileged daemon.
///
/// Every op that takes a path from the wire is a candidate arbitrary-file primitive: the
/// daemon runs as LocalSystem or root, so "write the progress file here" becomes "overwrite
/// any file on the machine" and "serve this library database" becomes "read any file on the
/// machine" unless something says otherwise. This is that something.
///
/// The rule is ownership, not a path allowlist. A path allowlist has to be kept in step with
/// every feature that adds a directory, and it says nothing about symlinks or junctions
/// pointing out of it. Asking the filesystem who owns the target answers the question that
/// actually matters - "could the caller have written here themselves?" - and if they could,
/// letting the daemon write there grants them nothing they did not already have.
/// </summary>
internal static class PrivilegedPaths
{
    private static readonly ILogger _log = Logging.For("PrivilegedPaths");

    /// <summary>
    /// True when <paramref name="path"/> may be read or written on a client's behalf.
    ///
    /// For a file that must already exist the file's own owner is checked; for one the daemon
    /// is about to create, the containing directory's is, since that is what governs whether
    /// the caller could have created it unaided.
    ///
    /// Fails CLOSED on Windows whenever an owner is recorded and the target is owned by
    /// somebody else. On unix it defers to the connection's kernel-verified peer UID gate,
    /// which has already established that the caller IS the owner before any op runs.
    /// </summary>
    internal static bool MayUse(string? path, out string reason)
    {
        reason = string.Empty;

        if (!OperatingSystem.IsWindows())
        {
            // The unix daemon refuses the connection outright unless the peer UID matches the
            // owner the installer stamped, so by the time an op sees a path the caller is
            // already the one account this helper serves.
            return true;
        }

        var owner = Environment.GetEnvironmentVariable("ORGZ_HELPER_OWNER_SID");
        if (string.IsNullOrWhiteSpace(owner))
        {
            // No recorded owner: a legacy install, or an in-process host where the code is
            // already running as the user. Checked FIRST, and before anything else about the
            // path, so this adds no new rejections anywhere the privilege boundary is absent -
            // callers keep whatever validation they already had.
            return true;
        }

        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            reason = "path is not rooted";
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path);
            var target = File.Exists(full) ? full : Path.GetDirectoryName(full);

            if (string.IsNullOrEmpty(target) || !Directory.Exists(target) && !File.Exists(target))
            {
                reason = "path does not exist";
                return false;
            }

            var sid = OwnerSidOf(target);
            if (sid is null)
            {
                reason = "owner could not be read";
                return false;
            }

            // The owner themselves, or a path the caller could not have written anyway because
            // it belongs to an administrator - the latter is the case worth refusing loudly.
            if (!string.Equals(sid, owner, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"owned by {sid}, not the helper's owner";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not decide ownership of {Path}", path);
            reason = "ownership check failed";
            return false;
        }
    }

    /// <summary>Logs and returns false when a client-named path is refused, so callers stay one line.</summary>
    internal static bool Refuse(string op, string? path, out string message)
    {
        if (MayUse(path, out var reason))
        {
            message = string.Empty;
            return false;
        }

        message = $"{op}: refusing path {path} ({reason})";
        _log.Warning("Refusing {Op} path {Path}: {Reason}", op, path, reason);
        return true;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? OwnerSidOf(string target)
    {
        var owner = Directory.Exists(target)
            ? new DirectoryInfo(target).GetAccessControl().GetOwner(typeof(SecurityIdentifier))
            : new FileInfo(target).GetAccessControl().GetOwner(typeof(SecurityIdentifier));

        return (owner as SecurityIdentifier)?.Value;
    }
}
