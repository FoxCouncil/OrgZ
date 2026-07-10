// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

/// <summary>
/// The one place OrgZ locates an external command-line tool (ffmpeg, flac, lame). System PATH
/// wins first - a distro packager's or power user's build stays current and its security fixes
/// reach the user without waiting for an OrgZ release - then a copy bundled in <c>tools/</c>
/// next to the app (the AppImage / .app / portable-Windows layout). Every caller used to carry
/// its own copy of this search; this is the single source of truth.
/// </summary>
internal static class ExecutableResolver
{
    /// <summary>Full path to <paramref name="name"/> on PATH, else a bundled copy, else null.</summary>
    public static string? Find(string name)
    {
        var fileName = WithPlatformExtension(name);
        if (TryFindOnPath(fileName, out var onPath))
        {
            return onPath;
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", fileName);
        return File.Exists(bundled) ? bundled : null;
    }

    /// <summary>
    /// Like <see cref="Find"/>, but returns the bare executable name when the tool is nowhere to
    /// be found, so a subsequent <c>Process.Start</c> raises the missing-tool error itself (which
    /// the encoder ctor rewrites into a user-facing "install flac/lame" message).
    /// </summary>
    public static string FindOrName(string name) => Find(name) ?? WithPlatformExtension(name);

    private static string WithPlatformExtension(string name)
        => OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name + ".exe"
            : name;

    private static bool TryFindOnPath(string fileName, out string fullPath)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            var sep = OperatingSystem.IsWindows() ? ';' : ':';
            foreach (var dir in pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate))
                    {
                        fullPath = candidate;
                        return true;
                    }
                }
                catch
                {
                    // A malformed PATH entry (illegal chars) - skip it, keep scanning.
                }
            }
        }

        fullPath = "";
        return false;
    }
}
