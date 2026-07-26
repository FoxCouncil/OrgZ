// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.DeviceHelper;
using OrgZ.Views;
using State = OrgZ.Services.DeviceHelper.DeviceHelperInstaller.ServiceState;

namespace OrgZ.Tests;

/// <summary>
/// The installer's command construction, the service-state reading, and the buttons the
/// Services tab offers for each state.
///
/// This code registers a LocalSystem/root daemon that issues raw SCSI, and until now it
/// had no tests at all - a typo in an sc.exe argument or a plist key is a broken install
/// discovered by a user, on a machine, under a UAC prompt. Every command below is pinned
/// as a string because that string IS the product here; there is no runtime behaviour to
/// assert without actually installing a service on the test machine.
///
/// Adversarial cases attack the details that bite: paths with spaces (Program Files),
/// quoting that must survive cmd.exe's parser, the '&' vs '&&' distinction that decides
/// whether an uninstall completes, and state parsing fed the output of a service that
/// isn't there.
/// </summary>
public class DeviceHelperInstallerTests
{
    // ── Windows: sc.exe arguments ─────────────────────────────

    [Fact]
    public void The_windows_install_registers_an_auto_start_service_that_runs_the_helper()
    {
        var args = DeviceHelperInstaller.WindowsInstallArguments(@"C:\Apps\OrgZ\OrgZ.exe");

        Assert.StartsWith("/c sc create OrgZDeviceHelper ", args, StringComparison.Ordinal);

        // sc's quirk: the space after each '=' is required, and omitting it silently
        // produces a service with an empty binPath.
        Assert.Contains("binPath= ", args, StringComparison.Ordinal);
        Assert.Contains("start= auto", args, StringComparison.Ordinal);
        Assert.DoesNotContain("binPath=\"", args, StringComparison.Ordinal);

        // The exe path is quoted INSIDE the quoted binPath value, so a path with spaces
        // survives both cmd.exe and the service control manager.
        Assert.Contains(@"""\""C:\Apps\OrgZ\OrgZ.exe\"" --device-helper""", args, StringComparison.Ordinal);

        // And it actually starts, rather than waiting for a reboot to prove itself.
        Assert.Contains("&& sc start OrgZDeviceHelper", args, StringComparison.Ordinal);
    }

    [Fact]
    public void A_windows_path_with_spaces_stays_one_argument()
    {
        var args = DeviceHelperInstaller.WindowsInstallArguments(@"C:\Program Files\OrgZ\OrgZ.exe");

        Assert.Contains(@"""\""C:\Program Files\OrgZ\OrgZ.exe\"" --device-helper""", args, StringComparison.Ordinal);
    }

    [Fact]
    public void The_windows_uninstall_deletes_even_when_the_service_is_already_stopped()
    {
        var args = DeviceHelperInstaller.WindowsUninstallArguments();

        // '&' not '&&' - with '&&' a stop that fails (service already stopped, exit 1062)
        // would skip the delete and leave the service registered forever.
        Assert.Contains("sc stop OrgZDeviceHelper & sc delete OrgZDeviceHelper", args, StringComparison.Ordinal);
        Assert.DoesNotContain("&&", args, StringComparison.Ordinal);
    }

    [Fact]
    public void Stop_and_start_touch_only_the_running_state_never_the_registration()
    {
        var stop = DeviceHelperInstaller.WindowsStopArguments();
        var start = DeviceHelperInstaller.WindowsStartArguments();

        Assert.Equal("/c sc stop OrgZDeviceHelper", stop);
        Assert.Equal("/c sc start OrgZDeviceHelper", start);

        // The whole point of Stop is that Start can undo it - neither may delete.
        Assert.DoesNotContain("delete", stop, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("create", start, StringComparison.OrdinalIgnoreCase);
    }

    // ── macOS: the LaunchDaemon plist ─────────────────────────

    [Fact]
    public void The_plist_launches_the_helper_as_a_keepalive_daemon_pinned_to_the_installing_user()
    {
        var plist = DeviceHelperInstaller.MacPlist("/Applications/OrgZ.app/Contents/MacOS/OrgZ", "/Users/fox/.dotnet", 501);

        Assert.Contains("<key>Label</key><string>com.foxcouncil.orgz.devicehelper</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<string>/Applications/OrgZ.app/Contents/MacOS/OrgZ</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<string>--device-helper</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>RunAtLoad</key><true/>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>KeepAlive</key><true/>", plist, StringComparison.Ordinal);

        // DOTNET_ROOT: a root daemon doesn't inherit the user's shell environment and
        // won't find the runtime without it.
        Assert.Contains("<key>DOTNET_ROOT</key><string>/Users/fox/.dotnet</string>", plist, StringComparison.Ordinal);

        // The owner UID is the whole authorization model - without it the daemon's socket
        // would serve every local account on the machine.
        Assert.Contains("<key>ORGZ_HELPER_OWNER_UID</key><string>501</string>", plist, StringComparison.Ordinal);
    }

    [Fact]
    public void The_mac_install_fixes_ownership_before_bootstrapping()
    {
        var script = DeviceHelperInstaller.MacInstallScript("/tmp/staged.plist");

        // launchd refuses to load a daemon plist that isn't root-owned and 644, and the
        // failure message it gives is famously unhelpful.
        var chown = script.IndexOf("chown root:wheel", StringComparison.Ordinal);
        var chmod = script.IndexOf("chmod 644", StringComparison.Ordinal);
        var bootstrap = script.IndexOf("launchctl bootstrap", StringComparison.Ordinal);

        Assert.True(chown > 0 && chmod > chown && bootstrap > chmod, script);

        // A reinstall must unload the old one first, and must not abort when there was
        // nothing loaded - hence '2>/dev/null;' rather than '&&'.
        Assert.Contains("launchctl bootout system/com.foxcouncil.orgz.devicehelper 2>/dev/null;", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_mac_stop_unloads_but_leaves_the_plist_so_start_can_undo_it()
    {
        Assert.Contains("bootout", DeviceHelperInstaller.MacStopScript(), StringComparison.Ordinal);
        Assert.DoesNotContain("rm ", DeviceHelperInstaller.MacStopScript(), StringComparison.Ordinal);

        Assert.Contains("bootstrap system '/Library/LaunchDaemons/com.foxcouncil.orgz.devicehelper.plist'", DeviceHelperInstaller.MacStartScript(), StringComparison.Ordinal);

        // Uninstall, by contrast, must remove the plist - otherwise the next boot
        // resurrects a service the user asked us to remove.
        Assert.Contains("rm -f '/Library/LaunchDaemons/com.foxcouncil.orgz.devicehelper.plist'", DeviceHelperInstaller.MacUninstallScript(), StringComparison.Ordinal);
    }

    // ── Linux: the systemd unit ───────────────────────────────

    [Fact]
    public void The_systemd_unit_runs_the_helper_as_root_with_the_owner_uid()
    {
        var unit = DeviceHelperInstaller.LinuxUnitFile("/opt/orgz/OrgZ", 1000);

        Assert.Contains("ExecStart=/opt/orgz/OrgZ --device-helper", unit, StringComparison.Ordinal);
        Assert.Contains("Environment=ORGZ_HELPER_OWNER_UID=1000", unit, StringComparison.Ordinal);
        Assert.Contains("User=root", unit, StringComparison.Ordinal);
        Assert.Contains("Restart=on-failure", unit, StringComparison.Ordinal);
        Assert.Contains("WantedBy=multi-user.target", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void The_linux_install_reloads_the_daemon_before_enabling_the_unit()
    {
        var script = DeviceHelperInstaller.LinuxInstallScript("/tmp/staged.service");

        var reload = script.IndexOf("systemctl daemon-reload", StringComparison.Ordinal);
        var enable = script.IndexOf("systemctl enable --now", StringComparison.Ordinal);

        // Without the reload first, systemd enables the unit it already had cached.
        Assert.True(reload > 0 && enable > reload, script);
        Assert.Contains("/etc/systemd/system/orgz-devicehelper.service", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_linux_stop_and_start_leave_the_unit_file_alone()
    {
        Assert.Equal("systemctl stop orgz-devicehelper.service", DeviceHelperInstaller.LinuxStopScript());
        Assert.Equal("systemctl start orgz-devicehelper.service", DeviceHelperInstaller.LinuxStartScript());

        // Uninstall disables it so a reboot doesn't bring it back, then removes the file
        // and reloads so systemd forgets it entirely.
        var uninstall = DeviceHelperInstaller.LinuxUninstallScript();
        Assert.Contains("disable --now", uninstall, StringComparison.Ordinal);
        Assert.Contains("rm -f '/etc/systemd/system/orgz-devicehelper.service'", uninstall, StringComparison.Ordinal);
        Assert.Contains("daemon-reload", uninstall, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_platform_uses_the_same_service_identity()
    {
        // Three names, one service. A rename on one platform and not the others produces
        // an uninstall that silently leaves the service running.
        Assert.Equal("OrgZDeviceHelper", DeviceHelperInstaller.WindowsService);
        Assert.Equal("com.foxcouncil.orgz.devicehelper", DeviceHelperInstaller.MacLabel);
        Assert.Equal("orgz-devicehelper", DeviceHelperInstaller.LinuxUnit);

        Assert.Contains(DeviceHelperInstaller.WindowsService, DeviceHelperInstaller.WindowsUninstallArguments(), StringComparison.Ordinal);
        Assert.Contains(DeviceHelperInstaller.MacLabel, DeviceHelperInstaller.MacUninstallScript(), StringComparison.Ordinal);
        Assert.Contains(DeviceHelperInstaller.LinuxUnit, DeviceHelperInstaller.LinuxUninstallScript(), StringComparison.Ordinal);
    }

    // ── Reading the state back ────────────────────────────────

    [Fact]
    public void A_running_windows_service_reads_as_running()
    {
        const string output = """
            SERVICE_NAME: OrgZDeviceHelper
                    TYPE               : 10  WIN32_OWN_PROCESS
                    STATE              : 4  RUNNING
                                            (STOPPABLE, NOT_PAUSABLE, ACCEPTS_SHUTDOWN)
                    WIN32_EXIT_CODE    : 0  (0x0)
            """;

        Assert.Equal(State.Running, DeviceHelperInstaller.ParseWindowsQuery(0, output));
    }

    [Fact]
    public void A_stopped_windows_service_reads_as_installed_not_missing()
    {
        const string output = """
            SERVICE_NAME: OrgZDeviceHelper
                    TYPE               : 10  WIN32_OWN_PROCESS
                    STATE              : 1  STOPPED
            """;

        // The distinction this whole feature exists for: stopped is not uninstalled.
        Assert.Equal(State.Stopped, DeviceHelperInstaller.ParseWindowsQuery(0, output));
    }

    [Fact]
    public void A_service_on_its_way_up_counts_as_running()
    {
        const string output = "        STATE              : 2  START_PENDING";

        // Offering Start again during START_PENDING is a no-op the user reads as a failure.
        Assert.Equal(State.Running, DeviceHelperInstaller.ParseWindowsQuery(0, output));
    }

    [Theory]
    [InlineData(1060, "[SC] EnumQueryServicesStatus:OpenService FAILED 1060:\r\n\r\nThe specified service does not exist as an installed service.")]
    [InlineData(1, "")]
    [InlineData(-1, "anything at all")]
    public void A_query_that_did_not_succeed_reads_as_not_installed(int exitCode, string output)
    {
        Assert.Equal(State.NotInstalled, DeviceHelperInstaller.ParseWindowsQuery(exitCode, output));
    }

    [Fact]
    public void A_successful_query_we_cannot_parse_errs_toward_stopped_not_missing()
    {
        // Claiming "not installed" would offer Install for a service that already exists,
        // and sc create would fail with 1073. Stopped at least offers Start/Uninstall.
        Assert.Equal(State.Stopped, DeviceHelperInstaller.ParseWindowsQuery(0, "some future sc.exe output"));
        Assert.Equal(State.Stopped, DeviceHelperInstaller.ParseWindowsQuery(0, ""));
    }

    [Theory]
    [InlineData("active\n", true)]
    [InlineData("activating\n", true)]
    [InlineData("inactive\n", false)]
    [InlineData("failed\n", false)]
    [InlineData("unknown", false)]
    [InlineData("", false)]
    public void Systemd_is_active_maps_to_running_only_when_it_really_is(string output, bool running)
    {
        Assert.Equal(running ? State.Running : State.Stopped, DeviceHelperInstaller.ParseLinuxIsActive(output));
    }

    // ── What the Services tab shows and offers ────────────────

    [Fact]
    public void Each_state_offers_exactly_the_actions_that_make_sense()
    {
        var notInstalled = SettingsDialog.ButtonsFor(State.NotInstalled);
        Assert.True(notInstalled.Install);
        Assert.False(notInstalled.Start || notInstalled.Stop || notInstalled.Uninstall);

        var stopped = SettingsDialog.ButtonsFor(State.Stopped);
        Assert.True(stopped.Start);
        Assert.True(stopped.Uninstall);
        Assert.False(stopped.Install);   // installing over an existing service fails with 1073
        Assert.False(stopped.Stop);

        var running = SettingsDialog.ButtonsFor(State.Running);
        Assert.True(running.Stop);
        Assert.True(running.Uninstall);
        Assert.False(running.Install || running.Start);
    }

    [Fact]
    public void The_status_line_reads_plainly_in_each_state()
    {
        Assert.Equal("Not installed", SettingsDialog.DescribeServiceState(State.NotInstalled, answering: false));
        Assert.Equal("Installed, stopped", SettingsDialog.DescribeServiceState(State.Stopped, answering: false));
        Assert.Equal("Installed and running", SettingsDialog.DescribeServiceState(State.Running, answering: true));
    }

    [Fact]
    public void The_lifecycle_card_ships_only_in_development_builds()
    {
        // A shipping user makes one choice - standalone (per-op UAC) or installed
        // (asked once, then silent). Start/Stop/Uninstall past that point can only put
        // them somewhere broken. The pre-commit suite runs in Release, so this assertion
        // is what actually holds the shipped state - not a comment saying it should.
#if DEBUG
        Assert.True(SettingsDialog.ShowServiceLifecycle);
#else
        Assert.False(SettingsDialog.ShowServiceLifecycle);
#endif
    }

    [Fact]
    public void The_installer_stays_reachable_to_code_even_when_the_card_is_hidden()
    {
        // Not #if'd out of the build: these tests run in Release, and a future installer
        // (Velopack post-install, a CLI switch) will want the same commands.
        Assert.NotEmpty(DeviceHelperInstaller.WindowsInstallArguments(@"C:\OrgZ\OrgZ.exe"));
        Assert.NotEmpty(DeviceHelperInstaller.LinuxUnitFile("/opt/orgz/OrgZ", 1000));
        Assert.NotEmpty(DeviceHelperInstaller.MacPlist("/Applications/OrgZ", "/Users/fox/.dotnet", 501));
    }

    [Fact]
    public void The_status_line_never_contradicts_a_helper_that_is_plainly_answering()
    {
        // A developer running `OrgZ --device-helper` by hand with nothing installed: the
        // rest of the app will happily use it, so the Services tab must not call it absent.
        Assert.Equal("Running, not installed", SettingsDialog.DescribeServiceState(State.NotInstalled, answering: true));
        Assert.Contains("answering", SettingsDialog.DescribeServiceState(State.Stopped, answering: true), StringComparison.Ordinal);
    }
}
