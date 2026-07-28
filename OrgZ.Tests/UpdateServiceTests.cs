// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// The update entry point in the Help menu.
///
/// The behaviour under test is a policy, not an algorithm: OrgZ must never download or
/// elevate on its own. It used to do both - a background download at every launch, applied
/// by Velopack on some later start - which was survivable only while the app lived in a
/// user-writable directory. Installing to Program Files makes applying an update an
/// elevated write, so an unattended apply means a UAC prompt appearing at a startup the
/// user didn't connect to anything. The menu label is the whole of the user-facing
/// contract, so it's what gets pinned.
/// </summary>
public class UpdateServiceTests
{
    [Fact]
    public void The_menu_offers_to_check_when_nothing_is_known()
    {
        Assert.Equal("Check for Updates...", UpdateService.MenuLabel(updateAvailable: false));
    }

    [Fact]
    public void The_menu_announces_an_update_once_one_is_found()
    {
        Assert.Equal("There are updates...", UpdateService.MenuLabel(updateAvailable: true));
    }

    [Fact]
    public void The_two_labels_are_distinguishable_to_a_reader()
    {
        // They drive the same menu entry, so a user tells the states apart by text alone.
        Assert.NotEqual(UpdateService.MenuLabel(true), UpdateService.MenuLabel(false));
        Assert.EndsWith("...", UpdateService.MenuLabel(true), StringComparison.Ordinal);
        Assert.EndsWith("...", UpdateService.MenuLabel(false), StringComparison.Ordinal);
    }

    [Fact]
    public void A_shipped_build_logs_enough_to_diagnose_a_bad_startup()
    {
        // Release used to default to Warning, so an entire session produced a single line
        // and every Information-level breadcrumb - the startup timings, "Update available",
        // the whole update flow - was discarded. A user reporting "it hung" left nothing
        // to read, and the instrumentation added to diagnose that was silently dropped.
        Assert.True(Logging.LevelSwitch.MinimumLevel <= Serilog.Events.LogEventLevel.Information,
            $"shipped log level is {Logging.LevelSwitch.MinimumLevel}; Information-level diagnostics would be discarded");
    }

    [Fact]
    public async Task Applying_with_nothing_pending_refuses_instead_of_reaching_for_the_network()
    {
        // Guards the ordering the UI depends on: the menu can only ever apply an update
        // that a prior check actually found.
        var svc = new UpdateService();

        Assert.Null(svc.PendingVersion);
        Assert.Equal("No update is pending.", await svc.ApplyAsync());
    }

    [Fact]
    public async Task A_check_on_an_uninstalled_copy_reports_nothing_rather_than_throwing()
    {
        // The test host, a dev run and a portable copy all have no Velopack install. That
        // has to be a quiet "no", because this runs at every startup.
        var svc = new UpdateService();

        Assert.False(await svc.CheckAsync());
        Assert.Null(svc.PendingVersion);
    }

    [Fact]
    public async Task Repeated_checks_stay_quiet_and_leave_nothing_pending()
    {
        // Startup calls this every launch; it must be side-effect free when there's
        // nothing to do - no partial state that would let the menu offer a phantom update.
        var svc = new UpdateService();

        for (var i = 0; i < 3; i++)
        {
            Assert.False(await svc.CheckAsync());
        }

        Assert.Null(svc.PendingVersion);
        Assert.Equal("Check for Updates...", UpdateService.MenuLabel(false));
    }
}
