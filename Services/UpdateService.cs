// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Serilog;
using Velopack;
using Velopack.Sources;

namespace OrgZ.Services;

/// <summary>
/// Updates, on the user's schedule rather than ours.
///
/// OrgZ used to check AND download in the background at every launch, then let Velopack
/// apply the staged package on some later start. That was tolerable while the app lived in
/// a user-writable directory. It isn't now: a PerMachine install lands in Program Files, so
/// applying an update elevates - and a UAC prompt appearing unbidden at startup, for
/// something the user never asked for, is the worst possible moment to ask.
///
/// So: check quietly, tell the user through the Help menu, and do nothing else until they
/// choose. The elevation then happens inside a gesture they initiated, where Windows'
/// own consent dialog reads as the confirmation step it actually is.
/// </summary>
public sealed class UpdateService
{
    private static readonly ILogger _log = Logging.For("Updates");

    private const string RepoUrl = "https://github.com/FoxCouncil/OrgZ";

    /// <summary>What the Help menu shows when no newer version is known.</summary>
    internal const string CheckLabel = "Check for Updates...";

    /// <summary>What it shows once one is waiting. Fox's wording; the version rides in the log, not the menu.</summary>
    internal const string AvailableLabel = "There are updates...";

    private UpdateInfo? _pending;

    /// <summary>The version found by the last check, or null when there's nothing newer.</summary>
    public string? PendingVersion => _pending?.TargetFullRelease?.Version?.ToString();

    /// <summary>
    /// The Help-menu label. Pure so the two states are pinned by tests rather than by
    /// clicking through the app to see which string appears.
    /// </summary>
    internal static string MenuLabel(bool updateAvailable) => updateAvailable ? AvailableLabel : CheckLabel;

    /// <summary>
    /// Looks for a newer release WITHOUT downloading anything. Cheap, quiet, and safe to
    /// run at startup: it touches no privileged directory and can't surface a prompt.
    /// Returns true when something newer exists.
    /// </summary>
    public async Task<bool> CheckAsync()
    {
        try
        {
            var mgr = BuildManager();
            if (mgr is null || !mgr.IsInstalled)
            {
                // A dev run or a portable copy has no install to update.
                return false;
            }

            _pending = await mgr.CheckForUpdatesAsync();
            if (_pending != null)
            {
                _log.Information("Update available: {Version}", PendingVersion);
            }

            return _pending != null;
        }
        catch (Exception ex)
        {
            // Being offline is not an error worth telling anyone about.
            _log.Debug(ex, "Update check failed");
            return false;
        }
    }

    /// <summary>
    /// Downloads and applies the pending update, restarting into it. Only ever called from
    /// an explicit user action - this is where the UAC prompt happens, because the install
    /// lives in Program Files. Returns an error string, or null when the app is on its way
    /// down to restart.
    /// </summary>
    public async Task<string?> ApplyAsync()
    {
        if (_pending is null)
        {
            return "No update is pending.";
        }

        try
        {
            var mgr = BuildManager();
            if (mgr is null)
            {
                return "Updates aren't available for this copy of OrgZ.";
            }

            _log.Information("Downloading update {Version}", PendingVersion);
            await mgr.DownloadUpdatesAsync(_pending);

            _log.Information("Applying update and restarting");
            mgr.ApplyUpdatesAndRestart(_pending);
            return null;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Applying the update failed");
            return ex.Message;
        }
    }

    private static UpdateManager? BuildManager()
    {
        try
        {
            return new UpdateManager(new GithubSource(RepoUrl, null, false, null), null, null);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not construct the update manager");
            return null;
        }
    }
}
