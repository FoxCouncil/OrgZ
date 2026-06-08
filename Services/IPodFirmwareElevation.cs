// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Serilog;

namespace OrgZ.Services;

/// <summary>
/// User-initiated path to <see cref="IPodFirmwarePartition.TryReadOsosVersion"/>
/// that goes through <see cref="CdElevation"/>. The firmware partition read
/// opens <c>\\.\PhysicalDriveN</c> with SCSI pass-through, which fails as a
/// standard user - same admin gate the burn pipeline already trips. Re-uses
/// the burn/rip elevation harness so the user sees one UAC prompt per call,
/// no separate LocalSystem service.
/// </summary>
internal static class IPodFirmwareElevation
{
    private static readonly ILogger _log = Logging.For("IPodFirmwareElevation");

    /// <summary>
    /// Reads the iPod OS version from the firmware partition. Spawns an
    /// elevated <c>cd-helper</c> child on Windows; falls through to the
    /// in-process call on platforms that don't need elevation (none today -
    /// the underlying partition reader is Windows-only - so the non-Windows
    /// branch just returns null).
    /// </summary>
    public static async Task<IPodFirmwareReadResult> ReadAsync(
        string drivePath,
        string? generation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivePath);

        if (!CdElevation.RequiresElevation)
        {
            // Linux/macOS: TryReadOsosVersion's implementation is `#if WINDOWS`
            // only - calling it here returns null with a "not supported"
            // diagnostic, which is the same shape the elevated path returns
            // on a read failure. Caller treats it identically.
            var inProcVersion = IPodFirmwarePartition.TryReadOsosVersion(drivePath, generation, out var inProcDiag);
            return new IPodFirmwareReadResult(inProcVersion, inProcDiag, UserDeclined: false);
        }

        var spec = new CdHelperSpec
        {
            Operation = "ipod-firmware-read",
            DrivePath = drivePath,
            IpodGeneration = generation,
        };

        string? version = null;
        string? diagnostic = null;
        var userDeclined = false;

        var exitCode = await CdElevation.RunElevatedAsync(spec, evt =>
        {
            switch (evt.Type)
            {
                case "ipod-firmware-result":
                    version = evt.OsosVersion;
                    diagnostic = evt.Message;
                    break;
                case "error":
                    diagnostic = evt.Message;
                    if (string.Equals(evt.Message, "Administrator access declined.", StringComparison.Ordinal))
                    {
                        userDeclined = true;
                    }
                    break;
            }
        }, cancellationToken);

        // Exit code 3 = ran cleanly but didn't decode a version (e.g. partition
        // read worked but the build ID isn't in the per-generation table yet).
        // Anything else with no event = elevation failed before the worker ran.
        if (exitCode != 0 && exitCode != 3 && version is null && diagnostic is null)
        {
            diagnostic = $"Elevated firmware helper exited with code {exitCode}.";
        }

        _log.Information(
            "iPod firmware elevated read: drive={Drive} generation={Generation} version={Version} declined={Declined} exit={Exit}",
            drivePath, generation ?? "(unknown)", version ?? "(none)", userDeclined, exitCode);

        return new IPodFirmwareReadResult(version, diagnostic, userDeclined);
    }
}

/// <summary>
/// Outcome of an <see cref="IPodFirmwareElevation.ReadAsync"/> call. Version
/// is null when the partition couldn't be read (or no entry matched). The
/// diagnostic carries the per-attempt log lines that <see cref="IPodFirmwarePartition"/>
/// accumulates so callers can surface them when debugging. UserDeclined is
/// true when the user dismissed the UAC prompt; callers can use that to
/// avoid retrying or to show a different message.
/// </summary>
internal readonly record struct IPodFirmwareReadResult(string? Version, string? Diagnostic, bool UserDeclined);
