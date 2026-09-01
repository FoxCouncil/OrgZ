// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services.DeviceLimits;

public enum FindingLevel
{
    Ok,
    Warning,
    Failed,
}

/// <summary>One check's verdict. <paramref name="Message"/> is written for the status line: numbers, plain words.</summary>
public sealed record Finding(string Id, FindingLevel Level, string Message);

/// <summary>
/// A named rule about a device that must hold after every sync. It reads measurements and limits and
/// never touches the device, so a new rule is a new class and nothing else.
/// </summary>
public interface IDeviceInvariant
{
    string Id { get; }
    Finding Evaluate(DeviceMeasurements m, IReadOnlyList<DeviceLimit> limits);
}

public sealed record VerificationReport(IReadOnlyList<Finding> Findings)
{
    public FindingLevel Worst => Findings.Count == 0 ? FindingLevel.Ok : Findings.Max(f => f.Level);
    public IEnumerable<Finding> Problems => Findings.Where(f => f.Level != FindingLevel.Ok);

    /// <summary>One line for the status bar: the worst finding, plus how many others there are.</summary>
    public string Summary()
    {
        var problems = Problems.OrderByDescending(f => f.Level).ToList();
        if (problems.Count == 0)
        {
            return "Device checks passed.";
        }
        var first = problems[0].Message;
        return problems.Count == 1 ? first : $"{first} (+{problems.Count - 1} more - see the log)";
    }
}

/// <summary>
/// Runs every invariant against a device. The three bugs that shipped together in August 2026 - covers
/// stored once per track, an artwork file stuck at the FAT32 ceiling, music rows with no media type -
/// were each fine per track and only visible in aggregate or on real firmware. These checks look at the
/// aggregate, on the real device, after every sync.
/// </summary>
public static class DeviceVerifier
{
    private static readonly Serilog.ILogger _log = Logging.For("DeviceVerifier");

    /// <summary>Space a sync must leave free: the firmware needs working room for its own database rewrites.</summary>
    public const long FreeSpaceFloorBytes = 200L * 1024 * 1024;

    public static readonly IReadOnlyList<IDeviceInvariant> Invariants =
    [
        new FileSystemIsFat32(),
        new TracksHaveAMediaType(),
        new ArtworkClaimsHaveEntries(),
        new PlaylistItemsResolve(),
        new ArtworkFilesUnderCeiling(),
        new MusicFoldersUnderLimit(),
        new DatabaseFitsInRam(),
        new TrackCountWithinBudget(),
        new FreeSpaceAboveFloor(),
        new RockboxFoldersBrowsable(),
    ];

    /// <summary>Measures the device and evaluates every invariant. Read-only.</summary>
    public static VerificationReport Verify(ConnectedDevice device)
    {
        var report = Evaluate(DeviceProbe.Measure(device), DeviceLimitCatalog.For(device));
        foreach (var finding in report.Problems)
        {
            if (finding.Level == FindingLevel.Failed)
            {
                _log.Error("Device check {Id} on {Mount}: {Message}", finding.Id, device.MountPath, finding.Message);
            }
            else
            {
                _log.Warning("Device check {Id} on {Mount}: {Message}", finding.Id, device.MountPath, finding.Message);
            }
        }
        return report;
    }

    /// <summary>The pure half: measurements + limits in, findings out. Every test drives this.</summary>
    public static VerificationReport Evaluate(DeviceMeasurements m, IReadOnlyList<DeviceLimit> limits)
        => new(Invariants.Select(i => i.Evaluate(m, limits)).ToList());

    /// <summary>
    /// Before a sync: would adding this much cross a limit? Projects the planned additions onto the
    /// current measurements and evaluates the same invariants, so preflight and post-sync can never
    /// disagree about what counts as a problem.
    /// </summary>
    public static VerificationReport Preflight(ConnectedDevice device, long bytesToAdd, int tracksToAdd)
    {
        var m = DeviceProbe.Measure(device);
        var limits = DeviceLimitCatalog.For(device);
        return Evaluate(Project(m, bytesToAdd, tracksToAdd), limits);
    }

    /// <summary>What the device will measure after the planned additions, assuming an even spread across
    /// the music folders and about a kilobyte of database per track (what a real 29k-track Classic shows).</summary>
    internal static DeviceMeasurements Project(DeviceMeasurements m, long bytesToAdd, int tracksToAdd)
    {
        if (tracksToAdd <= 0 && bytesToAdd <= 0)
        {
            return m;
        }

        const long DatabaseBytesPerTrack = 1024;
        int folders = Math.Max(1, IPodTrackImporter.MusicFolderCount);
        int perFolder = (int)Math.Ceiling(tracksToAdd / (double)folders);
        var projectedFolders = m.MusicFolders.Count == 0
            ? new List<(string, int)> { ("F00", perFolder) }
            : m.MusicFolders.Select(f => (f.Folder, f.Entries + perFolder)).ToList();

        return m with
        {
            FreeBytes = Math.Max(0, m.FreeBytes - bytesToAdd),
            TrackCount = m.TrackCount + tracksToAdd,
            DatabaseBytes = m.DatabaseInspected ? m.DatabaseBytes + tracksToAdd * DatabaseBytesPerTrack : m.DatabaseBytes,
            MusicFolders = projectedFolders,
        };
    }

    private static Finding Ok(string id, string message) => new(id, FindingLevel.Ok, message);
    private static Finding NotApplicable(string id) => new(id, FindingLevel.Ok, "not applicable");

    private static string Size(long bytes)
    {
        var (n, unit, _) = Helpers.FormatHelper.ReduceBytes(bytes);
        return $"{n:0.#} {unit}";
    }

    // ── the invariants ──────────────────────────────────────────────────────

    private sealed class FileSystemIsFat32 : IDeviceInvariant
    {
        public string Id => "filesystem";
        public Finding Evaluate(DeviceMeasurements m, IReadOnlyList<DeviceLimit> limits)
        {
            if (string.IsNullOrEmpty(m.FileSystem))
            {
                return NotApplicable(Id);
            }
            return m.FileSystem.StartsWith("FAT", StringComparison.OrdinalIgnoreCase)
                ? Ok(Id, $"{m.FileSystem} volume")
                : new Finding(Id, FindingLevel.Warning, $"The volume is {m.FileSystem}, but the firmware expects FAT32 - it may not boot from it.");
        }
    }

    private sealed class TracksHaveAMediaType : IDeviceInvariant
    {
        public string Id => "tracks-have-media-type";
        public Finding Evaluate(DeviceMeasurements m, IReadOnlyList<DeviceLimit> limits)
        {
            if (!m.DatabaseInspected)
            {
                return NotApplicable(Id);
            }
            return m.TracksWithoutMediaType == 0
                ? Ok(Id, $"all {m.TrackCount} tracks carry a media type")
                : new Finding(Id, FindingLevel.Failed, $"{m.TracksWithoutMediaType} track(s) have no media type and won't appear in the iPod's menus.");
        }
    }

    private sealed class ArtworkClaimsHaveEntries : IDeviceInvariant
    {
        public string Id => "artwork-claims-have-entries";
        public Finding Evaluate(DeviceMeasurements m, IReadOnlyList<DeviceLimit> limits)
        {
            if (!m.DatabaseInspected)
            {
                return NotApplicable(Id);
            }
            return m.ArtworkClaimsWithoutEntry == 0
                ? Ok(Id, $"{m.ArtworkEntries} artwork entries, every claim backed")
                : new Finding(Id, FindingLevel.Failed, $"{m.ArtworkClaimsWithoutEntry} track(s) claim cover art that isn't stored on the device.");
        }
    }

    private sealed class PlaylistItemsResolve : IDeviceInvariant
    {
        public string Id => "playlist-items-resolve";
        public Finding Evaluate(DeviceMeasurements m, IReadOnlyList<DeviceLimit> limits)
        {
            if (!m.DatabaseInspected)
            {
                return NotApplicable(Id);
            }
            return m.PlaylistItemsWithoutTrack == 0
                ? Ok(Id, $"{m.PlaylistCount} playlist(s), every entry points at a track")
                : new Finding(Id, FindingLevel.Failed, $"{m.PlaylistItemsWithoutTrack} playlist entr{(m.PlaylistItemsWithoutTrack == 1 ? "y points" : "ies point")} at tracks that don't exist.");
        }
    }

    private sealed class ArtworkFilesUnderCeiling : IDeviceInvariant
    {
        public string Id => "artwork-files-under-ceiling";
        public Finding Evaluate(DeviceMeasurements m, IReadOnlyList<DeviceLimit> limits)
        {
            var hard = limits.Find(LimitIds.FileMaxBytes);
            var rollover = limits.Find(LimitIds.ArtworkFileMaxBytes);
            if (m.ArtworkFiles.Count == 0 || (hard is null && rollover is null))
            {
                return NotApplicable(Id);
            }

            var (name, bytes) = m.ArtworkFiles.MaxBy(f => f.Bytes);
            if (hard is not null && bytes >= hard.Value - 1024 * 1024)
            {
                return new Finding(Id, FindingLevel.Failed, $"{name} is at the {Size(hard.Value)} file ceiling ({Size(bytes)}); covers written after it filled were lost.");
            }
            if (rollover is not null && bytes >= rollover.Value)
            {
                return new Finding(Id, FindingLevel.Warning, $"{name} is past the roll-over point ({Size(bytes)}); the next cover starts a new file.");
            }
            return Ok(Id, $"largest artwork file {name} is {Size(bytes)}");
        }
    }

    private sealed class MusicFoldersUnderLimit : IDeviceInvariant
    {
        public string Id => "music-folders-under-limit";
        public Finding Evaluate(DeviceMeasurements m, IReadOnlyList<DeviceLimit> limits)
        {
            var limit = limits.Find(LimitIds.DirectoryMaxEntries);
            if (limit is null || m.MusicFolders.Count == 0)
            {
                return NotApplicable(Id);
            }

            var (folder, entries) = m.MusicFolders.MaxBy(f => f.Entries);
            if (entries >= limit.Value)
            {
                return new Finding(Id, FindingLevel.Failed, $"Folder {folder} holds {entries:N0} entries - the filesystem's limit is {limit.Value:N0}; new tracks can't be created there.");
            }
            if (entries >= limit.Value * 0.8)
            {
                return new Finding(Id, FindingLevel.Warning, $"Folder {folder} holds {entries:N0} entries, near the {limit.Value:N0} limit.");
            }
            return Ok(Id, $"fullest music folder {folder} has {entries:N0} entries");
        }
    }

    private sealed class DatabaseFitsInRam : IDeviceInvariant
    {
        public string Id => "database-fits-in-ram";
        public Finding Evaluate(DeviceMeasurements m, IReadOnlyList<DeviceLimit> limits)
        {
            var limit = limits.Find(LimitIds.DatabaseMaxBytes);
            if (limit is null || !m.DatabaseInspected)
            {
                return NotApplicable(Id);
            }
            if (m.DatabaseBytes >= limit.Value)
            {
                return new Finding(Id, FindingLevel.Warning, $"The database is {Size(m.DatabaseBytes)}; this model can only load about {Size(limit.Value)} into memory.");
            }
            if (m.DatabaseBytes >= limit.Value / 2)
            {
                return new Finding(Id, FindingLevel.Warning, $"The database is {Size(m.DatabaseBytes)} - fine on a 64 MB (160 GB) Classic, past what a 32 MB (80/120 GB) one can load.");
            }
            return Ok(Id, $"database {Size(m.DatabaseBytes)}");
        }
    }

    private sealed class TrackCountWithinBudget : IDeviceInvariant
    {
        public string Id => "track-count-within-budget";
        public Finding Evaluate(DeviceMeasurements m, IReadOnlyList<DeviceLimit> limits)
        {
            var limit = limits.Find(LimitIds.TracksMax);
            if (limit is null || !m.DatabaseInspected)
            {
                return NotApplicable(Id);
            }
            if (m.TrackCount >= limit.Value)
            {
                return new Finding(Id, FindingLevel.Warning, $"{m.TrackCount:N0} tracks; this model tops out around {limit.Value:N0}.");
            }
            if (m.TrackCount >= limit.Value * 0.9)
            {
                return new Finding(Id, FindingLevel.Warning, $"{m.TrackCount:N0} tracks, close to this model's {limit.Value:N0} ceiling.");
            }
            return Ok(Id, $"{m.TrackCount:N0} tracks");
        }
    }

    private sealed class FreeSpaceAboveFloor : IDeviceInvariant
    {
        public string Id => "free-space";
        public Finding Evaluate(DeviceMeasurements m, IReadOnlyList<DeviceLimit> limits)
        {
            if (m.VolumeBytes <= 0)
            {
                return NotApplicable(Id);
            }
            return m.FreeBytes >= FreeSpaceFloorBytes
                ? Ok(Id, $"{Size(m.FreeBytes)} free")
                : new Finding(Id, FindingLevel.Warning, $"Only {Size(m.FreeBytes)} free; the firmware needs about {Size(FreeSpaceFloorBytes)} of working room.");
        }
    }

    private sealed class RockboxFoldersBrowsable : IDeviceInvariant
    {
        public string Id => "rockbox-folders-browsable";
        public Finding Evaluate(DeviceMeasurements m, IReadOnlyList<DeviceLimit> limits)
        {
            var limit = limits.Find(LimitIds.BrowserMaxEntries);
            if (limit is null || m.MusicFolders.Count == 0)
            {
                return NotApplicable(Id);
            }
            var over = m.MusicFolders.Where(f => f.Entries > limit.Value).ToList();
            return over.Count == 0
                ? Ok(Id, "every folder fits in Rockbox's browser")
                : new Finding(Id, FindingLevel.Warning, $"{over.Count} folder(s) hold more than {limit.Value} entries; Rockbox's browser won't list past that unless its limit setting is raised (e.g. {over[0].Folder}: {over[0].Entries}).");
        }
    }
}
