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

    private const string MacLabel = "com.foxcouncil.orgz.devicehelper";
    private const string LinuxUnit = "orgz-devicehelper";
    private const string WindowsService = "OrgZDeviceHelper";

    public sealed record InstallResult(bool Ok, string Detail);

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

    // ── macOS: LaunchDaemon in /Library/LaunchDaemons, loaded via launchctl bootstrap ──
    private static async Task<InstallResult> InstallMacAsync()
    {
        var plistPath = $"/Library/LaunchDaemons/{MacLabel}.plist";
        var dotnetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
        // Captured here, while we're still the invoking user (pre-elevation), so the root
        // daemon can restrict its socket to this UID and refuse every other local account.
        var ownerUid = PeerCredentials.CurrentUid();
        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key><string>{MacLabel}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{ExePath}</string>
                    <string>--device-helper</string>
                </array>
                <key>EnvironmentVariables</key>
                <dict><key>DOTNET_ROOT</key><string>{dotnetRoot}</string><key>ORGZ_HELPER_OWNER_UID</key><string>{ownerUid}</string></dict>
                <key>RunAtLoad</key><true/>
                <key>KeepAlive</key><true/>
            </dict>
            </plist>
            """;

        // One privileged shell run via osascript → a single macOS auth dialog. It drops the
        // plist, fixes ownership (launchd refuses a non-root-owned daemon), and boots it.
        var tmp = Path.Combine(Path.GetTempPath(), $"{MacLabel}.plist");
        await File.WriteAllTextAsync(tmp, plist);
        var script =
            $"cp '{tmp}' '{plistPath}' && chown root:wheel '{plistPath}' && chmod 644 '{plistPath}' && " +
            $"launchctl bootout system/{MacLabel} 2>/dev/null; launchctl bootstrap system '{plistPath}'";

        return await RunElevatedMacAsync(script, "OrgZ needs to install its device helper so it can read iPods without asking each time.");
    }

    // ── Linux: systemd unit in /etc/systemd/system, enabled + started via systemctl ──
    private static async Task<InstallResult> InstallLinuxAsync()
    {
        var unitPath = $"/etc/systemd/system/{LinuxUnit}.service";
        // Captured while we're still the invoking user (pre-elevation) so the root daemon can
        // restrict its socket to this UID and refuse every other local account.
        var ownerUid = PeerCredentials.CurrentUid();
        var unit = $"""
            [Unit]
            Description=OrgZ device helper (privileged iPod identity reads)
            After=multi-user.target

            [Service]
            Type=simple
            ExecStart={ExePath} --device-helper
            Environment=ORGZ_HELPER_OWNER_UID={ownerUid}
            Restart=on-failure
            User=root

            [Install]
            WantedBy=multi-user.target
            """;

        var tmp = Path.Combine(Path.GetTempPath(), $"{LinuxUnit}.service");
        await File.WriteAllTextAsync(tmp, unit);
        var script =
            $"cp '{tmp}' '{unitPath}' && systemctl daemon-reload && systemctl enable --now {LinuxUnit}.service";

        // pkexec surfaces the polkit auth dialog on a desktop session; fall back to sudo -n.
        return await RunElevatedLinuxAsync(script);
    }

    // ── Windows: a LocalSystem service via sc.exe, created under a UAC elevation ──
    private static async Task<InstallResult> InstallWindowsAsync()
    {
        // sc create needs the space after each '='; binPath quoted so the --device-helper arg
        // rides along. start=auto so it survives reboots, like AppleMobileDeviceService.
        var binPath = $"\\\"{ExePath}\\\" --device-helper";
        var args =
            $"/c sc create {WindowsService} binPath= \"{binPath}\" start= auto DisplayName= \"OrgZ Device Helper\" " +
            $"&& sc description {WindowsService} \"Privileged iPod identity reads for OrgZ.\" " +
            $"&& sc start {WindowsService}";

        return await RunElevatedWindowsAsync("cmd.exe", args);
    }

    public static async Task<InstallResult> UninstallAsync()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return await RunElevatedMacAsync(
                    $"launchctl bootout system/{MacLabel} 2>/dev/null; rm -f '/Library/LaunchDaemons/{MacLabel}.plist'",
                    "OrgZ is removing its device helper.");
            }
            if (OperatingSystem.IsLinux())
            {
                return await RunElevatedLinuxAsync(
                    $"systemctl disable --now {LinuxUnit}.service; rm -f '/etc/systemd/system/{LinuxUnit}.service'; systemctl daemon-reload");
            }
            if (OperatingSystem.IsWindows())
            {
                return await RunElevatedWindowsAsync("cmd.exe", $"/c sc stop {WindowsService} & sc delete {WindowsService}");
            }
            return new(false, "unsupported platform");
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
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
            return p.ExitCode == 0 ? new(true, "installed") : new(false, $"installer exited {p.ExitCode}");
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);   // includes the user cancelling the UAC prompt
        }
    }

    private static async Task<InstallResult> RunAsync(string fileName, string[] args)
    {
        var psi = new ProcessStartInfo { FileName = fileName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi);
        if (p == null)
        {
            return new(false, $"failed to start {fileName}");
        }
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        _log.Information("{File} exited {Code}: {Out} {Err}", fileName, p.ExitCode, stdout.Trim(), stderr.Trim());
        return p.ExitCode == 0 ? new(true, "installed") : new(false, string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim());
    }
}
