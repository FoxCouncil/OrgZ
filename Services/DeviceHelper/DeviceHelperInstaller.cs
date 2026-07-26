// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using Serilog;

namespace OrgZ.Services.DeviceHelper;

/// <summary>
/// Installs (and removes) the privileged device-helper as a real OS service - a macOS
/// LaunchDaemon, a Linux systemd unit, or a Windows service - each running the OrgZ
/// executable with <c>--device-helper</c> as root / LocalSystem at boot. This is the ONE
/// authorization the whole design costs: approve the install once, and every device read
/// afterward is silent, on every OS. Modelled on how iTunes installs AppleMobileDeviceService.
/// </summary>
public static class DeviceHelperInstaller
{
    private static readonly ILogger _log = Logging.For("DeviceHelperInstaller");

    internal const string MacLabel = "com.foxcouncil.orgz.devicehelper";
    internal const string LinuxUnit = "orgz-devicehelper";
    internal const string WindowsService = "OrgZDeviceHelper";

    internal const string MacPlistPath = $"/Library/LaunchDaemons/{MacLabel}.plist";
    internal const string LinuxUnitPath = $"/etc/systemd/system/{LinuxUnit}.service";

    public sealed record InstallResult(bool Ok, string Detail);

    /// <summary>
    /// Three states, not two. "Installed but stopped" is a real place to be - it's where
    /// you park the service while rebuilding, since a running one holds the executable
    /// open - and a UI that only knows running/not-running cannot offer the way back.
    /// </summary>
    public enum ServiceState
    {
        NotInstalled,
        Stopped,
        Running,
    }

    /// <summary>Path to the OrgZ executable the service should launch in helper mode.</summary>
    private static string ExePath => Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "OrgZ.exe" : "OrgZ");

    public static async Task<InstallResult> InstallAsync()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return await InstallMacAsync();
            }
            if (OperatingSystem.IsLinux())
            {
                return await InstallLinuxAsync();
            }
            if (OperatingSystem.IsWindows())
            {
                return await InstallWindowsAsync();
            }
            return new(false, "unsupported platform");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Device helper install failed");
            return new(false, ex.Message);
        }
    }

    // ── Command construction (pure - these are what the tests pin) ─────────

    /// <summary>
    /// The LaunchDaemon plist. <paramref name="ownerUid"/> is captured while we are still
    /// the invoking user (pre-elevation) so the root daemon can restrict its socket to
    /// this UID and refuse every other local account.
    /// </summary>
    internal static string MacPlist(string exePath, string dotnetRoot, uint? ownerUid) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key><string>{MacLabel}</string>
            <key>ProgramArguments</key>
            <array>
                <string>{exePath}</string>
                <string>--device-helper</string>
            </array>
            <key>EnvironmentVariables</key>
            <dict><key>DOTNET_ROOT</key><string>{dotnetRoot}</string><key>ORGZ_HELPER_OWNER_UID</key><string>{ownerUid}</string></dict>
            <key>RunAtLoad</key><true/>
            <key>KeepAlive</key><true/>
        </dict>
        </plist>
        """;

    /// <summary>
    /// One privileged shell run via osascript → a single macOS auth dialog. It drops the
    /// plist, fixes ownership (launchd refuses a non-root-owned daemon), and boots it.
    /// </summary>
    internal static string MacInstallScript(string stagedPlist) =>
        $"cp '{stagedPlist}' '{MacPlistPath}' && chown root:wheel '{MacPlistPath}' && chmod 644 '{MacPlistPath}' && " +
        $"launchctl bootout system/{MacLabel} 2>/dev/null; launchctl bootstrap system '{MacPlistPath}'";

    internal static string MacUninstallScript() =>
        $"launchctl bootout system/{MacLabel} 2>/dev/null; rm -f '{MacPlistPath}'";

    /// <summary>Unloads the daemon but leaves the plist in place, so Start can boot it again.</summary>
    internal static string MacStopScript() => $"launchctl bootout system/{MacLabel}";

    internal static string MacStartScript() => $"launchctl bootstrap system '{MacPlistPath}'";

    internal static string LinuxUnitFile(string exePath, uint? ownerUid) => $"""
        [Unit]
        Description=OrgZ device helper (privileged iPod identity reads)
        After=multi-user.target

        [Service]
        Type=simple
        ExecStart={exePath} --device-helper
        Environment=ORGZ_HELPER_OWNER_UID={ownerUid}
        Restart=on-failure
        User=root

        [Install]
        WantedBy=multi-user.target
        """;

    internal static string LinuxInstallScript(string stagedUnit) =>
        $"cp '{stagedUnit}' '{LinuxUnitPath}' && systemctl daemon-reload && systemctl enable --now {LinuxUnit}.service";

    internal static string LinuxUninstallScript() =>
        $"systemctl disable --now {LinuxUnit}.service; rm -f '{LinuxUnitPath}'; systemctl daemon-reload";

    internal static string LinuxStopScript() => $"systemctl stop {LinuxUnit}.service";

    internal static string LinuxStartScript() => $"systemctl start {LinuxUnit}.service";

    /// <summary>
    /// <c>sc create</c> needs the space after each '='; binPath is quoted so the
    /// <c>--device-helper</c> argument rides along inside it. start=auto so the service
    /// survives reboots, the way AppleMobileDeviceService does.
    /// </summary>
    internal static string WindowsInstallArguments(string exePath) =>
        $"/c sc create {WindowsService} binPath= \"\\\"{exePath}\\\" --device-helper\" start= auto DisplayName= \"OrgZ Device Helper\" " +
        $"&& sc description {WindowsService} \"Privileged iPod identity reads for OrgZ.\" " +
        $"&& sc start {WindowsService}";

    // '&' not '&&': delete must run even when the service was already stopped.
    internal static string WindowsUninstallArguments() => $"/c sc stop {WindowsService} & sc delete {WindowsService}";

    internal static string WindowsStopArguments() => $"/c sc stop {WindowsService}";

    internal static string WindowsStartArguments() => $"/c sc start {WindowsService}";

    // ── macOS: LaunchDaemon in /Library/LaunchDaemons, loaded via launchctl bootstrap ──
    private static async Task<InstallResult> InstallMacAsync()
    {
        var dotnetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
        var tmp = Path.Combine(Path.GetTempPath(), $"{MacLabel}.plist");
        await File.WriteAllTextAsync(tmp, MacPlist(ExePath, dotnetRoot, PeerCredentials.CurrentUid()));

        return await RunElevatedMacAsync(MacInstallScript(tmp), "OrgZ needs to install its device helper so it can read iPods without asking each time.");
    }

    // ── Linux: systemd unit in /etc/systemd/system, enabled + started via systemctl ──
    private static async Task<InstallResult> InstallLinuxAsync()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"{LinuxUnit}.service");
        await File.WriteAllTextAsync(tmp, LinuxUnitFile(ExePath, PeerCredentials.CurrentUid()));

        // pkexec surfaces the polkit auth dialog on a desktop session; fall back to sudo -n.
        return await RunElevatedLinuxAsync(LinuxInstallScript(tmp));
    }

    // ── Windows: a LocalSystem service via sc.exe, created under a UAC elevation ──
    private static Task<InstallResult> InstallWindowsAsync()
        => RunElevatedWindowsAsync("cmd.exe", WindowsInstallArguments(ExePath));

    public static async Task<InstallResult> UninstallAsync()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return await RunElevatedMacAsync(MacUninstallScript(), "OrgZ is removing its device helper.");
            }
            if (OperatingSystem.IsLinux())
            {
                return await RunElevatedLinuxAsync(LinuxUninstallScript());
            }
            if (OperatingSystem.IsWindows())
            {
                return await RunElevatedWindowsAsync("cmd.exe", WindowsUninstallArguments());
            }
            return new(false, "unsupported platform");
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }

    /// <summary>
    /// Stops the service without removing it. This is the escape hatch for developing
    /// against an installed helper: a running service holds the OrgZ executable open, so
    /// a rebuild fails on a file lock until it's parked.
    /// </summary>
    public static async Task<InstallResult> StopAsync()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return await RunElevatedMacAsync(MacStopScript(), "OrgZ is stopping its device helper.");
            }
            if (OperatingSystem.IsLinux())
            {
                return await RunElevatedLinuxAsync(LinuxStopScript());
            }
            if (OperatingSystem.IsWindows())
            {
                return await RunElevatedWindowsAsync("cmd.exe", WindowsStopArguments());
            }
            return new(false, "unsupported platform");
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }

    /// <summary>Starts an already-installed service back up.</summary>
    public static async Task<InstallResult> StartAsync()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return await RunElevatedMacAsync(MacStartScript(), "OrgZ is starting its device helper.");
            }
            if (OperatingSystem.IsLinux())
            {
                return await RunElevatedLinuxAsync(LinuxStartScript());
            }
            if (OperatingSystem.IsWindows())
            {
                return await RunElevatedWindowsAsync("cmd.exe", WindowsStartArguments());
            }
            return new(false, "unsupported platform");
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }

    // ── Already-elevated paths (the PerMachine MSI's install hooks) ───────

    /// <summary>
    /// How long an install hook may take before we give up. Velopack terminates the
    /// after-install callback at 30 s and the before-uninstall callback at 30 s, and a
    /// half-run <c>sc</c> chain is worse than none - so stop well inside that.
    /// </summary>
    internal static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Registers the service from a process that ALREADY holds administrator rights -
    /// the PerMachine MSI's post-install hook. Same commands as <see cref="InstallAsync"/>
    /// but run directly rather than through a "runas" ShellExecute: raising UAC inside an
    /// installer that the user already elevated would be a second prompt for nothing, and
    /// a prompt is not something a fast callback can wait on anyway.
    /// </summary>
    public static Task<InstallResult> InstallElevatedAsync()
        => RunShellAsync(WindowsInstallArguments(ExePath));

    /// <summary>
    /// Removes the service from an already-elevated uninstaller. Uninstalling OrgZ must
    /// never leave a LocalSystem service behind pointing at an executable that no longer
    /// exists - it would fail to start forever and sit in services.msc as litter.
    /// </summary>
    public static Task<InstallResult> UninstallElevatedAsync()
        => RunShellAsync(WindowsUninstallArguments());

    private static async Task<InstallResult> RunShellAsync(string arguments)
    {
        // cmd.exe with a chained command line: ArgumentList would quote the '&&' chain
        // into a single literal argument, so this is one of the rare correct uses of
        // ProcessStartInfo.Arguments.
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var p = Process.Start(psi);
            if (p == null)
            {
                return new(false, "failed to start cmd.exe");
            }

            using var cts = new CancellationTokenSource(HookTimeout);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return new(false, $"timed out after {HookTimeout.TotalSeconds:0}s");
            }

            var stdout = await p.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = await p.StandardError.ReadToEndAsync(CancellationToken.None);
            _log.Information("service command exited {Code}: {Out} {Err}", p.ExitCode, stdout.Trim(), stderr.Trim());

            return p.ExitCode == 0 ? new(true, "ok") : new(false, $"exited {p.ExitCode}: {stderr.Trim()}{stdout.Trim()}");
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }

    // ── State ─────────────────────────────────────────────────

    /// <summary>
    /// Asks the OS what it thinks of the service. Deliberately not the socket ping the
    /// GUI uses elsewhere: a ping can only ever say "answering" or "not answering", which
    /// collapses "stopped" and "never installed" into one useless state.
    /// </summary>
    public static async Task<ServiceState> QueryStateAsync()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var (code, stdout, _) = await CaptureAsync("sc.exe", ["query", WindowsService]);
                return ParseWindowsQuery(code, stdout);
            }
            if (OperatingSystem.IsLinux())
            {
                if (!File.Exists(LinuxUnitPath))
                {
                    return ServiceState.NotInstalled;
                }

                var (_, stdout, _) = await CaptureAsync("/usr/bin/systemctl", ["is-active", $"{LinuxUnit}.service"]);
                return ParseLinuxIsActive(stdout);
            }
            if (OperatingSystem.IsMacOS())
            {
                if (!File.Exists(MacPlistPath))
                {
                    return ServiceState.NotInstalled;
                }

                // launchctl print exits non-zero for a label that isn't bootstrapped.
                var (code, _, _) = await CaptureAsync("/bin/launchctl", ["print", $"system/{MacLabel}"]);
                return code == 0 ? ServiceState.Running : ServiceState.Stopped;
            }
        }
        catch (Exception ex)
        {
            // A missing sc.exe / systemctl tells us nothing, so claim nothing.
            _log.Debug(ex, "Service state query failed");
        }

        return ServiceState.NotInstalled;
    }

    /// <summary>
    /// Reads <c>sc query</c>. A non-zero exit is the "service does not exist" path
    /// (1060), so it means not installed rather than a state we failed to read.
    /// </summary>
    internal static ServiceState ParseWindowsQuery(int exitCode, string stdout)
    {
        if (exitCode != 0)
        {
            return ServiceState.NotInstalled;
        }

        foreach (var line in stdout.Split('\n'))
        {
            if (!line.Contains("STATE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A service on its way up is treated as running: the user asked for it, and
            // offering Start again would be a no-op that looks like a failure.
            return line.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) || line.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase)
                ? ServiceState.Running
                : ServiceState.Stopped;
        }

        // Installed enough to answer, but no STATE line we understood.
        return ServiceState.Stopped;
    }

    /// <summary>Reads <c>systemctl is-active</c> for a unit file we already know exists.</summary>
    internal static ServiceState ParseLinuxIsActive(string stdout)
    {
        var text = stdout.Trim();
        return text is "active" or "activating" ? ServiceState.Running : ServiceState.Stopped;
    }

    /// <summary>What the Services tab says, and which buttons that implies.</summary>
    internal static string DescribeState(ServiceState state) => state switch
    {
        ServiceState.Running => "Installed and running",
        ServiceState.Stopped => "Installed, stopped",
        _ => "Not installed",
    };

    private static async Task<(int ExitCode, string StdOut, string StdErr)> CaptureAsync(string fileName, string[] args)
    {
        var psi = new ProcessStartInfo { FileName = fileName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi);
        if (p == null)
        {
            return (-1, string.Empty, $"failed to start {fileName}");
        }

        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, stdout, stderr);
    }

    private static async Task<InstallResult> RunElevatedMacAsync(string shellScript, string prompt)
    {
        // osascript's "with administrator privileges" shows the one Touch-ID/password dialog
        // and runs the script as root. Escape embedded quotes for the AppleScript string.
        var escaped = shellScript.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var appleScript = $"do shell script \"{escaped}\" with prompt \"{prompt}\" with administrator privileges";
        return await RunAsync("/usr/bin/osascript", ["-e", appleScript]);
    }

    private static async Task<InstallResult> RunElevatedLinuxAsync(string shellScript)
    {
        if (await RunAsync("/usr/bin/pkexec", ["/bin/sh", "-c", shellScript]) is { Ok: true } ok)
        {
            return ok;
        }
        return await RunAsync("/usr/bin/sudo", ["-n", "/bin/sh", "-c", shellScript]);
    }

    private static async Task<InstallResult> RunElevatedWindowsAsync(string fileName, string args)
    {
        // ShellExecute verb "runas" is the UAC elevation gesture.
        var psi = new ProcessStartInfo { FileName = fileName, Arguments = args, UseShellExecute = true, Verb = "runas", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        try
        {
            using var p = Process.Start(psi);
            if (p == null)
            {
                return new(false, "failed to start elevated process");
            }
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? new(true, "ok") : new(false, $"exited {p.ExitCode}");
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);   // includes the user cancelling the UAC prompt
        }
    }

    private static async Task<InstallResult> RunAsync(string fileName, string[] args)
    {
        var (code, stdout, stderr) = await CaptureAsync(fileName, args);
        _log.Information("{File} exited {Code}: {Out} {Err}", fileName, code, stdout.Trim(), stderr.Trim());

        return code == 0 ? new(true, "ok") : new(false, string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim());
    }
}
