// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services.DeviceLimits;

/// <summary>Hard: the device or its filesystem refuses beyond this. Soft: it degrades or is unreliable beyond this.</summary>
public enum LimitSeverity
{
    Hard,
    Soft,
}

public enum LimitScope
{
    File,
    Directory,
    Volume,
    Database,
    Tracks,
    Artwork,
}

/// <summary>
/// One thing a device cannot do past a number. <paramref name="Source"/> says where the number came from -
/// a filesystem specification, Apple's published figures, on-metal measurement, or community reports -
/// so a reader can judge how much to trust it.
/// </summary>
public sealed record DeviceLimit(string Id, LimitScope Scope, long Value, LimitSeverity Severity, string What, string Source);

/// <summary>Stable ids: the checks look limits up by these, so a provider for a new platform slots in by reusing them.</summary>
public static class LimitIds
{
    public const string FileMaxBytes = "fs.file.max-bytes";
    public const string DirectoryMaxEntries = "fs.directory.max-entries";
    public const string VolumeMaxBytes = "fs.volume.max-bytes";
    public const string MusicFolders = "ipod.music.folders";
    public const string DatabaseMaxBytes = "ipod.database.max-bytes";
    public const string TracksMax = "ipod.tracks.max";
    public const string ArtworkFileMaxBytes = "ipod.artwork.file.max-bytes";
    public const string BrowserMaxEntries = "rockbox.browser.max-entries";
}

/// <summary>
/// Contributes the limits of one platform or layer. Providers are independent: the FAT32 one knows
/// nothing about iPods, the iPod one nothing about Rockbox. Adding a new kind of player means adding a
/// provider, not touching the checks.
/// </summary>
public interface IDeviceLimitProvider
{
    IEnumerable<DeviceLimit> LimitsFor(ConnectedDevice device);
}

/// <summary>What the FAT32 filesystem itself imposes. Every player OrgZ writes to is a FAT32 volume.</summary>
public sealed class Fat32LimitProvider : IDeviceLimitProvider
{
    public const long MaxFileBytes = 4L * 1024 * 1024 * 1024 - 1;      // 4 GiB - 1
    public const int MaxDirectoryEntries = 65_534;                      // long names spend extra slots per file
    public const long MaxVolumeBytes = 2L * 1024 * 1024 * 1024 * 1024; // 2 TiB with 512-byte sectors (MBR)

    private const string Spec = "FAT32 specification (Microsoft)";

    public IEnumerable<DeviceLimit> LimitsFor(ConnectedDevice device)
    {
        if (device.DeviceType == DeviceType.Unknown)
        {
            yield break;
        }

        yield return new DeviceLimit(LimitIds.FileMaxBytes, LimitScope.File, MaxFileBytes, LimitSeverity.Hard,
            "A single file cannot exceed 4 GiB - 1 byte; a write past it fails or truncates.", Spec);
        yield return new DeviceLimit(LimitIds.DirectoryMaxEntries, LimitScope.Directory, MaxDirectoryEntries, LimitSeverity.Hard,
            "A directory holds at most 65,534 entries, and every long filename spends extra ones.", Spec);
        yield return new DeviceLimit(LimitIds.VolumeMaxBytes, LimitScope.Volume, MaxVolumeBytes, LimitSeverity.Hard,
            "An MBR volume with 512-byte sectors addresses at most 2 TiB.", Spec);
    }
}

/// <summary>
/// What an Apple-firmware iPod imposes on top of its filesystem: how the firmware expects the music
/// folders laid out, how much database it can hold in RAM, and the artwork file ceiling.
/// </summary>
public sealed class IPodGenerationLimitProvider : IDeviceLimitProvider
{
    /// <summary>The Classic 6G/6.5G/7G load the whole iTunesDB into RAM. 160 GB models have 64 MB and
    /// handle roughly 50,000 tracks; 80/120 GB models have 32 MB and about half that. The number moves
    /// with how much metadata each track carries (about 1 KB per track is typical).</summary>
    public const long ClassicDatabaseMaxBytes = 50L * 1024 * 1024;
    public const int ClassicTracksMax = 50_000;

    private const string Community = "iFlash / iFixit community reports on flash-modded Classics";

    public IEnumerable<DeviceLimit> LimitsFor(ConnectedDevice device)
    {
        if (device.DeviceType != DeviceType.StockIPod)
        {
            yield break;
        }

        yield return new DeviceLimit(LimitIds.MusicFolders, LimitScope.Directory, IPodTrackImporter.MusicFolderCount, LimitSeverity.Soft,
            "iTunes spreads tracks across iPod_Control/Music/F00-F49 so no one folder grows huge and slow.",
            "iTunes behaviour, observed on every stock iPod");
        yield return new DeviceLimit(LimitIds.ArtworkFileMaxBytes, LimitScope.Artwork, IPodArtworkFiles.DefaultMaxFileBytes, LimitSeverity.Hard,
            "A thumbnail file rolls over to the next F####_N.ithmb before the FAT32 ceiling; one that reached it lost every later cover.",
            "FAT32 specification; failure observed on-metal (Classic 7G, 2026-09-01)");

        if (IPodCapabilities.ChecksumFor(device.IpodGeneration) == IPodChecksum.Hash58 && device.IpodGeneration?.StartsWith("Classic", StringComparison.OrdinalIgnoreCase) == true)
        {
            yield return new DeviceLimit(LimitIds.DatabaseMaxBytes, LimitScope.Database, ClassicDatabaseMaxBytes, LimitSeverity.Soft,
                "The Classic loads its whole database into RAM: about 50 MB on 64 MB (160 GB) models, about 25 MB on 32 MB (80/120 GB) models. Past that it reboots or shows an empty library.",
                Community);
            yield return new DeviceLimit(LimitIds.TracksMax, LimitScope.Tracks, ClassicTracksMax, LimitSeverity.Soft,
                "About 50,000 tracks on a 64 MB Classic, about 20-25,000 on a 32 MB one - it is really the database size that matters.",
                Community);
        }
    }
}

/// <summary>What Rockbox imposes: its file browser only lists so many entries per folder before it truncates.</summary>
public sealed class RockboxLimitProvider : IDeviceLimitProvider
{
    /// <summary>"Max Files in Dir Browser" defaults to 400 and can be raised to 10,000 in steps of 50 (reboot required).</summary>
    public const int DefaultBrowserMaxEntries = 400;

    public IEnumerable<DeviceLimit> LimitsFor(ConnectedDevice device)
    {
        if (device.DeviceType is not (DeviceType.RockboxIPod or DeviceType.RockboxOther))
        {
            yield break;
        }

        yield return new DeviceLimit(LimitIds.BrowserMaxEntries, LimitScope.Directory, DefaultBrowserMaxEntries, LimitSeverity.Soft,
            "Rockbox's file browser shows at most this many entries in one folder (its \"Max Files in Dir Browser\" setting, default 400).",
            "Rockbox manual, System > Limits");
    }
}

/// <summary>
/// The limits that apply to one device, merged from every provider. When two providers speak to the
/// same limit the smaller number wins, and Hard beats Soft - the device is only as generous as its
/// strictest layer.
/// </summary>
public static class DeviceLimitCatalog
{
    private static readonly IDeviceLimitProvider[] Providers =
    [
        new Fat32LimitProvider(),
        new IPodGenerationLimitProvider(),
        new RockboxLimitProvider(),
    ];

    public static IReadOnlyList<DeviceLimit> For(ConnectedDevice device) => Merge(Providers.SelectMany(p => p.LimitsFor(device)));

    /// <summary>The merge rule on its own, so a test can feed it two providers' worth of limits.</summary>
    public static IReadOnlyList<DeviceLimit> Merge(IEnumerable<DeviceLimit> limits)
    {
        var merged = new Dictionary<string, DeviceLimit>(StringComparer.Ordinal);
        foreach (var limit in limits)
        {
            if (!merged.TryGetValue(limit.Id, out var existing))
            {
                merged[limit.Id] = limit;
                continue;
            }

            var strictest = limit.Value < existing.Value ? limit : existing;
            var severity = (limit.Severity == LimitSeverity.Hard || existing.Severity == LimitSeverity.Hard) ? LimitSeverity.Hard : LimitSeverity.Soft;
            merged[limit.Id] = strictest with { Severity = severity };
        }
        return merged.Values.OrderBy(l => l.Id, StringComparer.Ordinal).ToList();
    }

    public static DeviceLimit? Find(this IReadOnlyList<DeviceLimit> limits, string id) => limits.FirstOrDefault(l => l.Id == id);
}
