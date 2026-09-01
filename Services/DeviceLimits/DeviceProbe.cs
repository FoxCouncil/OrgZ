// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services.DeviceLimits;

/// <summary>
/// What a device actually looks like right now - the numbers the limit checks compare against.
/// Everything here is measured, never inferred from what OrgZ thinks it wrote.
/// </summary>
public sealed record DeviceMeasurements
{
    public string? FileSystem { get; init; }
    public long VolumeBytes { get; init; }
    public long FreeBytes { get; init; }

    /// <summary>Every F####_N.ithmb with its size.</summary>
    public IReadOnlyList<(string FileName, long Bytes)> ArtworkFiles { get; init; } = [];

    /// <summary>Folders holding audio and how many entries each has: iPod_Control/Music/F## on a stock
    /// iPod, every folder under /Music on Rockbox.</summary>
    public IReadOnlyList<(string Folder, int Entries)> MusicFolders { get; init; } = [];

    /// <summary>False when the device's database is one OrgZ does not inspect here (Nano 5G SQLite,
    /// Shuffle iTunesSD, Rockbox); the database checks then report "not applicable".</summary>
    public bool DatabaseInspected { get; init; }
    public long DatabaseBytes { get; init; }
    public int TrackCount { get; init; }
    public int PlaylistCount { get; init; }

    /// <summary>Tracks whose row says neither music, podcast nor audiobook - invisible in a 6G+ iPod's menus.</summary>
    public int TracksWithoutMediaType { get; init; }

    /// <summary>Tracks that claim artwork but have no entry in the ArtworkDB - covers that were lost.</summary>
    public int ArtworkClaimsWithoutEntry { get; init; }
    public int ArtworkEntries { get; init; }

    /// <summary>Playlist entries pointing at a track id that does not exist.</summary>
    public int PlaylistItemsWithoutTrack { get; init; }
}

/// <summary>Reads the numbers off a mounted device. Read-only; safe to run at any time.</summary>
public static class DeviceProbe
{
    private static readonly Serilog.ILogger _log = Logging.For("DeviceProbe");

    public static DeviceMeasurements Measure(ConnectedDevice device)
    {
        var mount = device.MountPath;
        string? fileSystem = null;
        long volume = 0, free = 0;
        try
        {
            var drive = new DriveInfo(mount);
            fileSystem = drive.DriveFormat;
            volume = drive.TotalSize;
            free = drive.AvailableFreeSpace;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not read volume details for {Mount}", mount);
        }

        var measurements = new DeviceMeasurements
        {
            FileSystem = fileSystem,
            VolumeBytes = volume,
            FreeBytes = free,
            ArtworkFiles = IPodArtworkFiles.ListFiles(mount),
            MusicFolders = CountMusicFolders(device),
        };

        // Only the binary iTunesDB tiers are inspected; the others have their own stores.
        bool binaryDb = device.DeviceType == DeviceType.StockIPod
            && IPodCapabilities.ChecksumFor(device.IpodGeneration) is IPodChecksum.None or IPodChecksum.Hash58;
        if (!binaryDb)
        {
            return measurements;
        }

        var dbPath = Path.Combine(mount, "iPod_Control", "iTunes", "iTunesDB");
        if (!File.Exists(dbPath))
        {
            return measurements;
        }

        try
        {
            var facts = InspectBinaryDatabase(mount, dbPath);
            return measurements with
            {
                DatabaseInspected = true,
                DatabaseBytes = new FileInfo(dbPath).Length,
                TrackCount = facts.Tracks,
                PlaylistCount = facts.Playlists,
                TracksWithoutMediaType = facts.NoMediaType,
                ArtworkClaimsWithoutEntry = facts.ArtClaimsWithoutEntry,
                ArtworkEntries = facts.ArtEntries,
                PlaylistItemsWithoutTrack = facts.DanglingPlaylistItems,
            };
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not inspect the database on {Mount}", mount);
            return measurements;
        }
    }

    /// <summary>Entries per audio folder. Cheap: it counts directory entries, it never opens files.</summary>
    private static List<(string Folder, int Entries)> CountMusicFolders(ConnectedDevice device)
    {
        var result = new List<(string, int)>();
        try
        {
            if (device.DeviceType == DeviceType.StockIPod)
            {
                var music = Path.Combine(device.MountPath, "iPod_Control", "Music");
                if (Directory.Exists(music))
                {
                    foreach (var dir in Directory.EnumerateDirectories(music))
                    {
                        result.Add((Path.GetFileName(dir), Directory.EnumerateFileSystemEntries(dir).Count()));
                    }
                }
            }
            else if (device.DeviceType is DeviceType.RockboxIPod or DeviceType.RockboxOther)
            {
                var music = Path.Combine(device.MountPath, "Music");
                if (Directory.Exists(music))
                {
                    foreach (var dir in Directory.EnumerateDirectories(music, "*", SearchOption.AllDirectories).Prepend(music))
                    {
                        int entries = Directory.EnumerateFileSystemEntries(dir).Count();
                        if (entries > 0)
                        {
                            result.Add((Path.GetRelativePath(device.MountPath, dir), entries));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not count music folders on {Mount}", device.MountPath);
        }
        return result;
    }

    private sealed record DatabaseFacts(int Tracks, int Playlists, int NoMediaType, int ArtClaimsWithoutEntry, int ArtEntries, int DanglingPlaylistItems);

    private static DatabaseFacts InspectBinaryDatabase(string mount, string dbPath)
    {
        var doc = ITunesDbChunkTree.Parse(File.ReadAllBytes(dbPath));

        var artDbids = new HashSet<ulong>();
        var artPath = Path.Combine(mount, "iPod_Control", "Artwork", "ArtworkDB");
        int artEntries = 0;
        if (File.Exists(artPath))
        {
            try
            {
                foreach (var image in ArtworkDbWriter.ReadImages(ITunesDbChunkTree.Parse(File.ReadAllBytes(artPath))))
                {
                    artDbids.Add(image.Dbid);
                    artEntries++;
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "ArtworkDB on {Mount} unreadable; artwork claims are checked against nothing", mount);
            }
        }

        int tracks = 0, noMediaType = 0, claimsWithoutEntry = 0;
        var trackIds = new HashSet<uint>();
        foreach (var mhsd in doc.Root.Children.Where(c => c.Magic == "mhsd" && c.ReadHeaderInt32(0x0C) == 1))
        {
            foreach (var mhit in mhsd.Children.Where(c => c.Magic == "mhit"))
            {
                tracks++;
                trackIds.Add((uint)mhit.ReadHeaderInt32(0x10));

                if (mhit.Header.Length >= ITunesMediaType.MhitOffset + 4 && mhit.ReadHeaderInt32(ITunesMediaType.MhitOffset) == 0)
                {
                    noMediaType++;
                }

                if (mhit.Header.Length > 0xA4 && mhit.Header[0xA4] == 1)
                {
                    ulong dbid = 0;
                    for (int i = 7; i >= 0; i--)
                    {
                        dbid = (dbid << 8) | mhit.Header[0x70 + i];
                    }
                    if (!artDbids.Contains(dbid))
                    {
                        claimsWithoutEntry++;
                    }
                }
            }
        }

        int playlists = 0, dangling = 0;
        foreach (var mhsd in doc.Root.Children.Where(c => c.Magic == "mhsd" && c.ReadHeaderInt32(0x0C) is 2 or 3))
        {
            foreach (var mhyp in mhsd.Children.Where(c => c.Magic == "mhyp"))
            {
                playlists++;
                foreach (var mhip in mhyp.Children.Where(c => c.Magic == "mhip"))
                {
                    var trackId = (uint)mhip.ReadHeaderInt32(0x18);
                    if (trackId != 0 && !trackIds.Contains(trackId))
                    {
                        dangling++;   // podcast group headers carry 0 and are skipped
                    }
                }
            }
        }

        return new DatabaseFacts(tracks, playlists, noMediaType, claimsWithoutEntry, artEntries, dangling);
    }
}
