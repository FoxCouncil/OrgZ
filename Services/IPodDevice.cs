// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Linq;
using OrgZ.Models;

namespace OrgZ.Services;

/// <summary>One downloaded podcast episode to push to a device, format-agnostic.</summary>
public sealed record PodcastPush(string LocalFile, string Title, string Show, string? Description, string? FeedUrl, DateTime PubDateUtc, int LengthMs, string? CoverImagePath = null);

/// <summary>The whole contents of a device: every track as a <see cref="MediaItem"/>, plus its playlists.</summary>
public sealed record DeviceLibrary(IReadOnlyList<MediaItem> Tracks, IReadOnlyList<DevicePlaylist> Playlists)
{
    public static readonly DeviceLibrary Empty = new([], []);
}

/// <summary>
/// Polymorphic per-device iPod abstraction: capability queries ("does this model do podcasts?")
/// plus operations that internally pick the format and the right on-device database. Build one with
/// <see cref="For"/>; the call site stops caring whether it's a Rockbox box, a binary-iTunesDB iPod,
/// or a SQLite Nano 5G. Operations a tier hasn't implemented yet throw
/// <see cref="NotImplementedException"/> so the gaps are loud and greppable, never silent.
/// </summary>
public abstract class IPodDevice
{
    protected ConnectedDevice Device { get; }
    protected IPodDevice(ConnectedDevice device) => Device = device;

    public string Name => Device.Name;
    public string MountPath => Device.MountPath;
    public string? Generation => Device.IpodGeneration;

    // ── capabilities ─────────────────────────────────────────────────────────
    public abstract bool SupportsDatabaseWrite { get; }
    public abstract bool SupportsPlaylists { get; }
    public abstract bool SupportsPodcasts { get; }
    public abstract bool SupportsArtwork { get; }

    /// <summary>
    /// Whether the tier can carry audiobooks as AUDIOBOOKS (media_type/media_kind 8, bookmarkable)
    /// rather than mislabeled songs. Off by default - each tier that really writes the kind claims
    /// it explicitly. The Shuffle stays false: our iTunesSD/bdhs writers carry no audiobook concept
    /// (the bookmark flag is written as 0), and a book shuffled into songs is worse than absent.
    /// </summary>
    public virtual bool SupportsAudiobooks => false;

    // ── operations (default = explicit gap) ──────────────────────────────────
    /// <summary>Pushes downloaded podcast episodes; returns how many were written.</summary>
    public virtual Task<int> AddPodcastsAsync(IReadOnlyList<PodcastPush> episodes, string ffmpegPath, Action<int, int>? onProgress = null, CancellationToken ct = default)
        => throw new NotImplementedException($"AddPodcasts is not implemented for {GetType().Name} ({Generation ?? "?"}).");

    /// <summary>
    /// Erases the device's library - every track + playlist - leaving it empty and ready to load
    /// (e.g. a second-hand iPod you want for yourself). Returns the number of audio files removed.
    /// </summary>
    public virtual Task<int> EraseAsync(CancellationToken ct = default)
        => throw new NotImplementedException($"Erase is not implemented for {GetType().Name} ({Generation ?? "?"}).");

    /// <summary>
    /// Removes one item - any media kind (music / podcast / audiobook) - from the device: its
    /// database rows and its audio file, so a re-scan no longer shows it. Each tier locates the item
    /// its own way (SQLite pid, iTunesDB track id, or just the file path). Throws if it can't be found.
    /// </summary>
    public virtual Task RemoveTrackAsync(MediaItem item, CancellationToken ct = default)
        => throw new NotImplementedException($"Remove is not implemented for {GetType().Name} ({Generation ?? "?"}).");

    /// <summary>
    /// Imports one library track onto the device - transcoding to whatever the tier needs - and returns
    /// the device-side <see cref="MediaItem"/> (its on-device path + id) so the caller can drop it into a
    /// playlist or the live view. Each tier picks its own format + database.
    /// </summary>
    public virtual Task<MediaItem> AddTrackAsync(MediaItem libraryTrack, string ffmpegPath, CancellationToken ct = default)
        => throw new NotImplementedException($"AddTrack is not implemented for {GetType().Name} ({Generation ?? "?"}).");

    /// <summary>
    /// Writes (or idempotently replaces) a native user playlist named <paramref name="name"/> referencing
    /// the given device tracks - the ones returned by <see cref="AddTrackAsync"/> / <see cref="ReadLibraryAsync"/>.
    /// Each tier reads the id it needs off the <see cref="MediaItem"/> (SQLite pid via <c>Dbid</c>, iTunesDB
    /// id via <c>Id</c>, or the file path for Rockbox), so the caller never juggles per-tier id types.
    /// </summary>
    public virtual Task CreatePlaylistAsync(string name, IReadOnlyList<MediaItem> deviceTracks, CancellationToken ct = default)
        => throw new NotImplementedException($"CreatePlaylist is not implemented for {GetType().Name} ({Generation ?? "?"}).");

    /// <summary>
    /// Reads the device's whole library - tracks (any media kind) + playlists - from its authoritative
    /// store (SQLite .itlp, binary iTunesDB, or a filesystem walk). <paramref name="onBatch"/>, when set,
    /// receives tracks incrementally so a slow scan fills the UI as it runs.
    /// </summary>
    public virtual Task<DeviceLibrary> ReadLibraryAsync(Action<IReadOnlyList<MediaItem>>? onBatch = null, Action<string>? onProgress = null, CancellationToken ct = default)
        => throw new NotImplementedException($"ReadLibrary is not implemented for {GetType().Name} ({Generation ?? "?"}).");

    /// <summary>
    /// Opens a batch scope around a run of consecutive writes, letting a tier defer expensive
    /// per-operation work until the scope closes - the Nano 5G defers its full-CDB regeneration so
    /// an M-track sync rebuilds once instead of M times. Null when the tier has nothing to defer.
    /// Always dispose: disposal performs the deferred work, even after a partial failure.
    /// </summary>
    public virtual IDisposable? BeginBatchWrite() => null;

    // ── factory ──────────────────────────────────────────────────────────────
    /// <summary>Resolves the right device tier for a connected device (its model + checksum).</summary>
    public static IPodDevice For(ConnectedDevice device) => device.DeviceType switch
    {
        // The Rockbox tier is really the FILESYSTEM tier - plain files + M3U8s, no database - which
        // is exactly what any generic mass-storage player is too.
        DeviceType.RockboxIPod or DeviceType.RockboxOther or DeviceType.GenericPlayer => new RockboxIPod(device),
        DeviceType.StockIPod => IPodCapabilities.ChecksumFor(device.IpodGeneration) switch
        {
            IPodChecksum.Hash72 => new Nano5gIPod(device),
            IPodChecksum.Hash58 or IPodChecksum.None => new BinaryIPod(device),
            IPodChecksum.ITunesSD => new ShuffleIPod(device),
            _ => new UnsupportedIPod(device),   // hashAB+ : no open-source signer
        },
        _ => new UnsupportedIPod(device),       // Unknown
    };

    /// <summary>Maps a list of <see cref="PodcastPush"/> to the importer's episode record.</summary>
    protected static List<IPodTrackImporter.PodcastEpisodeImport> ToEpisodes(IReadOnlyList<PodcastPush> eps)
        => eps.Select(e => new IPodTrackImporter.PodcastEpisodeImport(e.LocalFile, e.Title, e.Show, e.Description, e.FeedUrl, e.PubDateUtc, e.LengthMs, e.CoverImagePath)).ToList();

    /// <summary>Best-effort recursive delete of every file under a directory; returns how many went.</summary>
    protected static int DeleteFilesUnder(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return 0;
        }
        int removed = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch
            {
                // skip locked / inaccessible files
            }
        }
        return removed;
    }

    /// <summary>Strips the device's Music root to a "Fxx/NAME.ext" Locations.itdb key, or null when
    /// the path isn't under iPod_Control/Music.</summary>
    protected static string? RelativeUnderMusic(string? absolutePath, string musicRoot)
    {
        if (string.IsNullOrEmpty(absolutePath))
        {
            return null;
        }
        var rel = Path.GetRelativePath(musicRoot, absolutePath);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            return null;
        }
        return rel.Replace('\\', '/');
    }

    /// <summary>Parses the trailing numeric id from a device MediaItem id ("device:{mount}:{id}"). The
    /// id is the last colon-segment, so a mount path containing a colon (e.g. "E:\") is safe.</summary>
    protected static bool TryParseDeviceItemId(string? itemId, out uint trackId)
    {
        trackId = 0;
        if (string.IsNullOrEmpty(itemId))
        {
            return false;
        }
        int colon = itemId.LastIndexOf(':');
        return uint.TryParse(colon >= 0 ? itemId.AsSpan(colon + 1) : itemId.AsSpan(), out trackId);
    }

    /// <summary>Builds a device-side <see cref="MediaItem"/> from a parsed track row (shared by the binary
    /// iTunesDB and Nano 5G SQLite readers). SQLite tracks key on the 64-bit pid; binary-DB tracks on the
    /// 32-bit id, so <c>Id</c> encodes whichever is present.</summary>
    protected static MediaItem DeviceItemFromTrack(ITunesDbReader.ITunesTrack t, string mountPath)
    {
        var ext = !string.IsNullOrEmpty(t.FilePath) ? Path.GetExtension(t.FilePath) : null;
        return new MediaItem
        {
            Id = t.Pid != 0 ? $"device:{mountPath}:{t.Pid}" : $"device:{mountPath}:{t.TrackId}",
            Kind = ITunesMediaType.ToKind(t.MediaType),
            Title = t.Title,
            Artist = t.Artist,
            Album = t.Album,
            Genre = t.Genre,
            Composer = t.Composer,
            Year = t.Year > 0 ? (uint)t.Year : null,
            Track = t.TrackNumber > 0 ? (uint)t.TrackNumber : null,
            TotalTracks = t.TotalTracks > 0 ? (uint)t.TotalTracks : null,
            Disc = t.DiscNumber > 0 ? (uint)t.DiscNumber : null,
            TotalDiscs = t.TotalDiscs > 0 ? (uint)t.TotalDiscs : null,
            Duration = t.DurationMs > 0 ? TimeSpan.FromMilliseconds(t.DurationMs) : null,
            FilePath = t.FilePath,
            FileName = !string.IsNullOrEmpty(t.FilePath) ? Path.GetFileName(t.FilePath) : null,
            Extension = ext,
            FileSize = t.FileSize,
            AudioBitrate = t.Bitrate > 0 ? t.Bitrate : null,
            SampleRate = t.SampleRate > 0 ? t.SampleRate : null,
            PlayCount = t.PlayCount,
            Rating = t.Rating > 0 ? t.Rating / 20 : null,
            LastPlayed = t.LastPlayed,
            DateAdded = t.DateAdded ?? DateTime.UtcNow,
            IsAnalyzed = true,
            Source = $"device:{mountPath}",
            StreamUrl = t.FilePath,
            Dbid = t.Dbid != 0 ? t.Dbid : null,
        };
    }

    /// <summary>Builds the device-side <see cref="MediaItem"/> for a track just imported via
    /// <see cref="IPodTrackImporter.ImportAsync"/>: carries the on-device path, the id (for playlist
    /// membership + removal), and the dbid (the Nano 5G item pid). Metadata comes from the library source.</summary>
    protected MediaItem DeviceItemFromImport(MediaItem source, IPodImportResult result)
    {
        long size = 0;
        try { size = new FileInfo(result.DestFile).Length; } catch { /* size stays 0 - cosmetic */ }
        return new MediaItem
        {
            Id = $"device:{MountPath}:{result.TrackId}",
            Kind = source.Kind,
            Title = result.Title ?? source.Title,
            Artist = source.Artist,
            Album = source.Album,
            Genre = source.Genre,
            Composer = source.Composer,
            Year = source.Year,
            Track = source.Track,
            TotalTracks = source.TotalTracks,
            Disc = source.Disc,
            TotalDiscs = source.TotalDiscs,
            Duration = source.Duration,
            FilePath = result.DestFile,
            FileName = Path.GetFileName(result.DestFile),
            Extension = Path.GetExtension(result.DestFile),
            FileSize = size,
            HasAlbumArt = source.HasAlbumArt,
            IsAnalyzed = true,
            Source = $"device:{MountPath}",
            StreamUrl = result.DestFile,
            Dbid = result.Dbid != 0 ? result.Dbid : null,
        };
    }
}

/// <summary>iPod Nano 5G (Hash72): the "iTunes Library.itlp" SQLite stack.</summary>
public sealed class Nano5gIPod : IPodDevice
{
    public Nano5gIPod(ConnectedDevice device) : base(device) { }

    public override bool SupportsDatabaseWrite => true;
    public override bool SupportsPlaylists => true;
    public override bool SupportsPodcasts => true;
    public override bool SupportsArtwork => true;
    public override bool SupportsAudiobooks => true;   // media_kind=8 via the SQLite writer

    public override Task<int> AddPodcastsAsync(IReadOnlyList<PodcastPush> episodes, string ffmpegPath, Action<int, int>? onProgress = null, CancellationToken ct = default)
        => Task.Run(() => IPodTrackImporter.AddPodcastEpisodesNano5gAsync(MountPath, ToEpisodes(episodes), ffmpegPath, Device.FireWireGuid, onProgress, ct), ct);

    public override Task<int> EraseAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            // Delete the audio + artwork caches, then empty the SQLite library and re-sign the cbk.
            int removed = DeleteFilesUnder(Path.Combine(MountPath, "iPod_Control", "Music"))
                        + DeleteFilesUnder(Path.Combine(MountPath, "iPod_Control", "Artwork"));
            new Nano5gLibraryWriter(Path.Combine(MountPath, "iPod_Control", "iTunes", "iTunes Library.itlp")).WipeLibrary();
            return removed;
        }, ct);

    public override Task RemoveTrackAsync(MediaItem item, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var itlp = Path.Combine(MountPath, "iPod_Control", "iTunes", "iTunes Library.itlp");
            var musicRoot = Path.Combine(MountPath, "iPod_Control", "Music");
            var relative = RelativeUnderMusic(item.FilePath, musicRoot)
                ?? throw new InvalidOperationException($"“{item.Title}” isn't under the iPod's Music folder.");
            long pid = Nano5gLibraryWriter.FindItemPidByLocation(itlp, relative);
            if (pid == 0)
            {
                throw new InvalidOperationException($"“{item.Title}” isn't in the iPod database.");
            }
            new Nano5gLibraryWriter(itlp, Device.FireWireGuid).RemoveTrack(pid, musicRoot);
        }, ct);

    public override Task<MediaItem> AddTrackAsync(MediaItem libraryTrack, string ffmpegPath, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            var r = await IPodTrackImporter.ImportAsync(MountPath, libraryTrack.FilePath!, ffmpegPath, Generation, Device.FireWireGuid, ct);
            return DeviceItemFromImport(libraryTrack, r);
        }, ct);

    public override Task CreatePlaylistAsync(string name, IReadOnlyList<MediaItem> deviceTracks, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var itlp = Path.Combine(MountPath, "iPod_Control", "iTunes", "iTunes Library.itlp");
            var musicRoot = Path.Combine(MountPath, "iPod_Control", "Music");
            // Resolve each device item back to its SQLite item pid by on-device location - works for both
            // just-imported and already-present tracks, and doesn't rely on the pid riding in the item.
            var pids = new List<long>(deviceTracks.Count);
            foreach (var t in deviceTracks)
            {
                var rel = RelativeUnderMusic(t.FilePath, musicRoot);
                long pid = rel is null ? 0 : Nano5gLibraryWriter.FindItemPidByLocation(itlp, rel);
                if (pid != 0)
                {
                    pids.Add(pid);
                }
            }
            if (pids.Count > 0)
            {
                new Nano5gLibraryWriter(itlp, Device.FireWireGuid).CreatePlaylist(name, pids);
            }
        }, ct);

    public override Task<DeviceLibrary> ReadLibraryAsync(Action<IReadOnlyList<MediaItem>>? onBatch = null, Action<string>? onProgress = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var itlp = Path.Combine(MountPath, "iPod_Control", "iTunes", "iTunes Library.itlp");
            onProgress?.Invoke("Reading iTunes Library.itlp...");
            Nano5gLibraryReader.ReadAll(itlp, MountPath, out var tracks, out var playlists);
            var items = tracks.Select(t => DeviceItemFromTrack(t, MountPath)).ToList();
            onBatch?.Invoke(items);
            return new DeviceLibrary(items, playlists);
        }, ct);

    public override IDisposable? BeginBatchWrite()
        => new Nano5gLibraryWriter(Path.Combine(MountPath, "iPod_Control", "iTunes", "iTunes Library.itlp"), Device.FireWireGuid).BeginCdbBatch();
}

/// <summary>Pre-Nano-5G stock iPods (no checksum or Hash58): the binary iTunesDB.</summary>
public sealed class BinaryIPod : IPodDevice
{
    public BinaryIPod(ConnectedDevice device) : base(device) { }

    public override bool SupportsDatabaseWrite => true;
    public override bool SupportsPlaylists => true;
    public override bool SupportsPodcasts => true;
    public override bool SupportsArtwork => true;
    public override bool SupportsAudiobooks => true;   // media_type=8 in the binary iTunesDB MHIT

    public override Task<int> AddPodcastsAsync(IReadOnlyList<PodcastPush> episodes, string ffmpegPath, Action<int, int>? onProgress = null, CancellationToken ct = default)
        => Task.Run(() => IPodTrackImporter.AddPodcastEpisodes(MountPath, Device.IpodGeneration, Device.FireWireGuid, ToEpisodes(episodes), onProgress), ct);

    public override Task RemoveTrackAsync(MediaItem item, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!TryParseDeviceItemId(item.Id, out var trackId))
            {
                throw new InvalidOperationException($"Couldn't identify “{item.Title}” on the iPod.");
            }
            var dbPath = Path.Combine(MountPath, "iPod_Control", "iTunes", "iTunesDB");
            if (!File.Exists(dbPath))
            {
                throw new FileNotFoundException($"This iPod has no iTunesDB at '{dbPath}'.", dbPath);
            }

            var doc = ITunesDbChunkTree.Parse(File.ReadAllBytes(dbPath));
            if (!ITunesDbWriter.RemoveTrack(doc, trackId))
            {
                throw new InvalidOperationException($"“{item.Title}” isn't in the iPod database.");
            }

            // Same write discipline as the add path: checksum, one-time backup, atomic swap.
            IPodTrackImporter.CommitDb(doc, dbPath, MountPath, Device.IpodGeneration, Device.FireWireGuid);

            if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
            {
                File.Delete(item.FilePath);
            }
        }, ct);

    public override Task<int> EraseAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            // Delete the audio + artwork caches, then clear the iTunesDB's track + playlist lists and
            // re-sign it - the reset for every pre-Nano-5G stock iPod (1st-gen FireWire → Classic).
            // Same write discipline as the remove path: one-time backup, then atomic swap.
            int removed = DeleteFilesUnder(Path.Combine(MountPath, "iPod_Control", "Music"))
                        + DeleteFilesUnder(Path.Combine(MountPath, "iPod_Control", "Artwork"));

            var dbPath = Path.Combine(MountPath, "iPod_Control", "iTunes", "iTunesDB");
            if (File.Exists(dbPath) && new FileInfo(dbPath).Length > 0)
            {
                var doc = ITunesDbChunkTree.Parse(File.ReadAllBytes(dbPath));
                ITunesDbWriter.ClearLibrary(doc);
                IPodTrackImporter.CommitDb(doc, dbPath, MountPath, Device.IpodGeneration, Device.FireWireGuid);
            }
            return removed;
        }, ct);

    public override Task<MediaItem> AddTrackAsync(MediaItem libraryTrack, string ffmpegPath, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            var r = await IPodTrackImporter.ImportAsync(MountPath, libraryTrack.FilePath!, ffmpegPath, Generation, Device.FireWireGuid, ct);
            return DeviceItemFromImport(libraryTrack, r);
        }, ct);

    public override Task CreatePlaylistAsync(string name, IReadOnlyList<MediaItem> deviceTracks, CancellationToken ct = default)
        => Task.Run(() =>
        {
            // Binary iTunesDB playlists reference the 32-bit MHIT id, which rides in the device item Id
            // ("device:{mount}:{id}").
            var trackIds = new List<uint>(deviceTracks.Count);
            foreach (var t in deviceTracks)
            {
                if (TryParseDeviceItemId(t.Id, out var id) && id != 0)
                {
                    trackIds.Add(id);
                }
            }
            if (trackIds.Count > 0)
            {
                IPodTrackImporter.AddPlaylist(MountPath, Generation, Device.FireWireGuid, name, trackIds);
            }
        }, ct);

    public override Task<DeviceLibrary> ReadLibraryAsync(Action<IReadOnlyList<MediaItem>>? onBatch = null, Action<string>? onProgress = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var dbPath = Path.Combine(MountPath, "iPod_Control", "iTunes", "iTunesDB");
            // A 0-byte iTunesDB is the empty stub left beside a Nano 5G CDB - treat it as "no DB" and walk
            // the filesystem, matching the pre-refactor fall-through.
            if (!(File.Exists(dbPath) && new FileInfo(dbPath).Length > 0))
            {
                onProgress?.Invoke("No iTunesDB — walking filesystem");
                return FilesystemLibraryScanner.Scan(Device, onBatch, onProgress, ct);
            }

            onProgress?.Invoke("Parsing iTunesDB...");
            ITunesDbReader.ReadAll(dbPath, MountPath, out var tracks, out var itunesPlaylists);
            var playlists = itunesPlaylists
                .Where(pl => !string.IsNullOrWhiteSpace(pl.Name))
                .Select(pl => new DevicePlaylist
                {
                    Name = pl.Name!,
                    Key = $"MHYP:{pl.PlaylistId}",
                    TrackIds = pl.TrackIds.Select(tid => $"device:{MountPath}:{tid}").ToList(),
                })
                .ToList();
            var items = tracks.Select(t => DeviceItemFromTrack(t, MountPath)).ToList();
            onBatch?.Invoke(items);
            return new DeviceLibrary(items, playlists);
        }, ct);
}

/// <summary>Rockbox (on an iPod or any other player): plain files on disk + a .m3u8, no iTunes DB.</summary>
public sealed class RockboxIPod : IPodDevice
{
    public RockboxIPod(ConnectedDevice device) : base(device) { }

    public override bool SupportsDatabaseWrite => false;   // filesystem player, no iTunesDB to write
    public override bool SupportsPlaylists => true;
    public override bool SupportsPodcasts => true;
    public override bool SupportsArtwork => true;          // sidecar/embedded art, the player handles it
    public override bool SupportsAudiobooks => true;       // plain files - .m4b by container, tagged MP3s by genre, both re-detected on read

    public override Task<int> AddPodcastsAsync(IReadOnlyList<PodcastPush> episodes, string ffmpegPath, Action<int, int>? onProgress = null, CancellationToken ct = default)
        => Task.Run(async () =>
    {
        // Rockbox plays straight off the filesystem: drop each episode under /Podcasts/<show>/ and
        // merge a /Podcasts/Podcasts.m3u8 pointing at them. No transcode - Rockbox is omnivorous.
        var podRoot = Path.Combine(MountPath, "Podcasts");
        Directory.CreateDirectory(podRoot);
        var m3u = Path.Combine(podRoot, "Podcasts.m3u8");
        var lines = File.Exists(m3u) ? (await File.ReadAllLinesAsync(m3u, ct)).ToList() : new List<string>();

        int added = 0;
        for (int i = 0; i < episodes.Count; i++)
        {
            var e = episodes[i];
            onProgress?.Invoke(i + 1, episodes.Count);
            var showDir = Path.Combine(podRoot, Sanitize(e.Show));
            Directory.CreateDirectory(showDir);
            var dest = Path.Combine(showDir, Sanitize(e.Title) + Path.GetExtension(e.LocalFile));
            File.Copy(e.LocalFile, dest, overwrite: true);

            var entry = "/Podcasts/" + Sanitize(e.Show) + "/" + Path.GetFileName(dest);
            if (!lines.Contains(entry))
            {
                lines.Add(entry);
            }
            added++;
        }

        await File.WriteAllLinesAsync(m3u, lines, ct);
        return added;
    }, ct);

    public override Task RemoveTrackAsync(MediaItem item, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath))
            {
                throw new FileNotFoundException($"“{item.Title}” isn't on {Name}.", item.FilePath ?? string.Empty);
            }

            // Prune the track from any .m3u8 playlist that points at it (entries are root-absolute, e.g.
            // "/Music/Artist/Album/Track.mp3"), then delete the file. Rockbox rebuilds its tagcache from
            // the filesystem, so no database to touch.
            var rooted = "/" + Path.GetRelativePath(MountPath, item.FilePath).Replace('\\', '/');
            foreach (var m3u in Directory.EnumerateFiles(MountPath, "*.m3u8", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var lines = File.ReadAllLines(m3u).ToList();
                if (lines.RemoveAll(l => string.Equals(l.Trim(), rooted, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    File.WriteAllLines(m3u, lines);
                }
            }

            File.Delete(item.FilePath);
        }, ct);

    public override Task<MediaItem> AddTrackAsync(MediaItem libraryTrack, string ffmpegPath, CancellationToken ct = default)
        => Task.Run(() =>
        {
            // Rockbox plays straight off disk: copy into /Music/{Artist}/{Album}/ (no transcode) and hand
            // back a device-side item pointing at the on-device path.
            var dest = Path.GetFullPath(Path.Combine(MountPath, BuildMusicRelativePath(libraryTrack).TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (!File.Exists(dest))
            {
                File.Copy(libraryTrack.FilePath!, dest);
            }
            long size = 0;
            try { size = new FileInfo(dest).Length; } catch { /* cosmetic */ }
            return new MediaItem
            {
                Id = dest,
                Kind = libraryTrack.Kind,
                Source = $"device:{MountPath}",
                FilePath = dest,
                StreamUrl = dest,
                FileName = Path.GetFileName(dest),
                Title = libraryTrack.Title,
                Artist = libraryTrack.Artist,
                Album = libraryTrack.Album,
                Genre = libraryTrack.Genre,
                Duration = libraryTrack.Duration,
                Track = libraryTrack.Track,
                Year = libraryTrack.Year,
                FileSize = size,
                IsAnalyzed = true,
            };
        }, ct);

    public override Task CreatePlaylistAsync(string name, IReadOnlyList<MediaItem> deviceTracks, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            // A Rockbox playlist is an M3U of root-absolute device paths ("/Music/Artist/Album/x.mp3")
            // in /Playlists/ - the Playlist Catalog folder. Rockbox writes .m3u8 (UTF-8) itself, so we
            // match that (it reads .m3u too, but .m3u8 is the convention and handles non-ASCII names).
            var playlistsDir = Path.Combine(MountPath, "Playlists");
            Directory.CreateDirectory(playlistsDir);
            var target = Path.Combine(playlistsDir, Sanitize(name) + ".m3u8");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("#EXTM3U");
            sb.Append("#PLAYLIST:").AppendLine(name);
            foreach (var t in deviceTracks)
            {
                if (string.IsNullOrEmpty(t.FilePath))
                {
                    continue;
                }
                sb.AppendLine("/" + Path.GetRelativePath(MountPath, t.FilePath).Replace('\\', '/'));
            }
            await File.WriteAllTextAsync(target, sb.ToString(), ct);
        }, ct);

    public override Task<DeviceLibrary> ReadLibraryAsync(Action<IReadOnlyList<MediaItem>>? onBatch = null, Action<string>? onProgress = null, CancellationToken ct = default)
        => Task.Run(() => FilesystemLibraryScanner.Scan(Device, onBatch, onProgress, ct), ct);

    public override Task<int> EraseAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            // Wipe the OrgZ-managed content roots - music, podcasts, playlists - and NOTHING else.
            // /.rockbox is the firmware: the device must stay bootable, so it is never touched, and
            // neither is anything else the user keeps on the drive. Returns audio files removed
            // (playlist files go too but don't count - same accounting as the other tiers).
            int removed = DeleteFilesUnder(Path.Combine(MountPath, "Music"))
                        + DeleteFilesUnder(Path.Combine(MountPath, "Podcasts"));
            DeleteFilesUnder(Path.Combine(MountPath, "Playlists"));
            return removed;
        }, ct);

    private static string BuildMusicRelativePath(MediaItem track)
    {
        var artist = Sanitize(string.IsNullOrWhiteSpace(track.Artist) ? "Unknown Artist" : track.Artist!);
        var album = Sanitize(string.IsNullOrWhiteSpace(track.Album) ? "Unknown Album" : track.Album!);
        var file = Sanitize(!string.IsNullOrEmpty(track.FileName) ? track.FileName! : Path.GetFileName(track.FilePath ?? "track"));
        return $"/Music/{artist}/{album}/{file}";
    }

    private static string Sanitize(string s) => string.Join("_", s.Split(Path.GetInvalidFileNameChars()));
}

/// <summary>
/// iPod Shuffle: screenless, no iTunesDB - audio files live under <c>iPod_Control/Music/F00</c> and
/// the play order is the <c>iTunesSD</c> track list (big-endian classic on 1G/2G, little-endian
/// "bdhs" on 3G/4G). Podcasts and playlists ARE offered, they just fold into the single track
/// list: episodes sync as plain tracks (no grouping to show), and syncing a playlist replaces the
/// device's whole list - which is what a Shuffle is.
/// </summary>
public sealed class ShuffleIPod : IPodDevice
{
    public ShuffleIPod(ConnectedDevice device) : base(device) { }

    public override bool SupportsDatabaseWrite => true;
    public override bool SupportsPlaylists => true;    // the playlist BECOMES the device's one list
    public override bool SupportsPodcasts => true;     // episodes land as plain tracks
    public override bool SupportsArtwork => false;

    private string ITunesDir => Path.Combine(MountPath, "iPod_Control", "iTunes");
    private string MusicDir => Path.Combine(MountPath, "iPod_Control", "Music", "F00");

    /// <summary>3G/4G Shuffles use the newer little-endian "bdhs" iTunesSD; 1G/2G the classic format.</summary>
    private bool UsesBdhs => Generation is not null && (Generation.Contains("3G") || Generation.Contains("4G"));
    private List<ShuffleSdTrack> ReadSd() => UsesBdhs ? ShuffleBdhsWriter.Read(ITunesDir) : ShuffleSdWriter.Read(ITunesDir);
    private void WriteSd(IReadOnlyList<ShuffleSdTrack> list)
    {
        if (UsesBdhs) { ShuffleBdhsWriter.Write(ITunesDir, list); }
        else { ShuffleSdWriter.Write(ITunesDir, list); }
    }

    public override Task<MediaItem> AddTrackAsync(MediaItem libraryTrack, string ffmpegPath, CancellationToken ct = default)
        => Task.Run(() =>
        {
            // Shuffle plays any file straight off disk (no transcode): copy into Music/F00, then add it to
            // the iTunesSD list so the firmware will actually play it.
            Directory.CreateDirectory(MusicDir);
            var dest = CopyIntoMusic(libraryTrack.FilePath!);
            AppendToSd(dest);

            long size = 0;
            try { size = new FileInfo(dest).Length; } catch { /* cosmetic */ }
            return new MediaItem
            {
                Id = dest,
                Kind = libraryTrack.Kind,
                Source = $"device:{MountPath}",
                FilePath = dest,
                StreamUrl = dest,
                FileName = Path.GetFileName(dest),
                Title = libraryTrack.Title,
                Artist = libraryTrack.Artist,
                Album = libraryTrack.Album,
                Genre = libraryTrack.Genre,
                Duration = libraryTrack.Duration,
                Track = libraryTrack.Track,
                Year = libraryTrack.Year,
                FileSize = size,
                IsAnalyzed = true,
            };
        }, ct);

    public override Task<int> AddPodcastsAsync(IReadOnlyList<PodcastPush> episodes, string ffmpegPath, Action<int, int>? onProgress = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            Directory.CreateDirectory(MusicDir);
            var list = ReadSd();
            int added = 0;
            for (int i = 0; i < episodes.Count; i++)
            {
                onProgress?.Invoke(i + 1, episodes.Count);
                var dest = CopyIntoMusic(episodes[i].LocalFile);
                var ipodPath = ToIpodPath(dest);
                if (!list.Any(x => string.Equals(x.IpodPath, ipodPath, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(new ShuffleSdTrack(ipodPath, ShuffleSdWriter.FileTypeFor(dest)));
                    added++;
                }
            }
            WriteSd(list);
            return added;
        }, ct);

    public override Task CreatePlaylistAsync(string name, IReadOnlyList<MediaItem> deviceTracks, CancellationToken ct = default)
        => Task.Run(() =>
        {
            // A Shuffle has one track list - the sync order - so a "playlist" write just sets iTunesSD to
            // exactly these tracks (the name has nowhere to live on a screenless device).
            var list = deviceTracks
                .Where(t => !string.IsNullOrEmpty(t.FilePath))
                .Select(t => new ShuffleSdTrack(ToIpodPath(t.FilePath!), ShuffleSdWriter.FileTypeFor(t.FilePath!)))
                .ToList();
            WriteSd(list);
        }, ct);

    public override Task<DeviceLibrary> ReadLibraryAsync(Action<IReadOnlyList<MediaItem>>? onBatch = null, Action<string>? onProgress = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            onProgress?.Invoke("Reading iTunesSD...");
            var items = new List<MediaItem>();
            foreach (var e in ReadSd())
            {
                var abs = Path.GetFullPath(Path.Combine(MountPath, e.IpodPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
                long size = 0;
                try { size = File.Exists(abs) ? new FileInfo(abs).Length : 0; } catch { /* cosmetic */ }
                items.Add(new MediaItem
                {
                    Id = abs,
                    Kind = MediaKind.Music,
                    Source = $"device:{MountPath}",
                    FilePath = abs,
                    StreamUrl = abs,
                    FileName = Path.GetFileName(abs),
                    Title = Path.GetFileNameWithoutExtension(abs),
                    Extension = Path.GetExtension(abs),
                    FileSize = size,
                    IsAnalyzed = true,
                });
            }
            onBatch?.Invoke(items);
            return new DeviceLibrary(items, []);
        }, ct);

    public override Task RemoveTrackAsync(MediaItem item, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (string.IsNullOrEmpty(item.FilePath))
            {
                throw new InvalidOperationException($"Couldn't identify “{item.Title}” on {Name}.");
            }
            var ipodPath = ToIpodPath(item.FilePath);
            var list = ReadSd();
            list.RemoveAll(e => string.Equals(e.IpodPath, ipodPath, StringComparison.OrdinalIgnoreCase));
            WriteSd(list);
            if (File.Exists(item.FilePath))
            {
                File.Delete(item.FilePath);
            }
        }, ct);

    public override Task<int> EraseAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            int removed = DeleteFilesUnder(Path.Combine(MountPath, "iPod_Control", "Music"));
            WriteSd([]);   // valid header, zero entries
            return removed;
        }, ct);

    private string ToIpodPath(string absolute) => "/" + Path.GetRelativePath(MountPath, absolute).Replace('\\', '/');

    private string CopyIntoMusic(string sourceFile)
    {
        Directory.CreateDirectory(MusicDir);
        var dest = Path.Combine(MusicDir, Sanitize(Path.GetFileNameWithoutExtension(sourceFile)) + Path.GetExtension(sourceFile));
        if (!File.Exists(dest))
        {
            File.Copy(sourceFile, dest);
        }
        return dest;
    }

    private void AppendToSd(string destFile)
    {
        var ipodPath = ToIpodPath(destFile);
        var list = ReadSd();
        if (!list.Any(e => string.Equals(e.IpodPath, ipodPath, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(new ShuffleSdTrack(ipodPath, ShuffleSdWriter.FileTypeFor(destFile)));
            WriteSd(list);
        }
    }

    private static string Sanitize(string s) => string.Join("_", s.Split(Path.GetInvalidFileNameChars()));
}

/// <summary>Detected but not safely writable yet (Nano 6G/7G HashAB - no open-source signer - plus
/// Touch and undetected generations). Every operation throws the loud base-class gap.</summary>
public sealed class UnsupportedIPod : IPodDevice
{
    public UnsupportedIPod(ConnectedDevice device) : base(device) { }

    public override bool SupportsDatabaseWrite => false;
    public override bool SupportsPlaylists => false;
    public override bool SupportsPodcasts => false;
    public override bool SupportsArtwork => false;
    // every operation inherits the NotImplementedException default.
}
