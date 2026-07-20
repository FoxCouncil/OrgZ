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

    /// <summary>True when the device has one flat, user-orderable play order (the Shuffle's iTunesSD
    /// list IS its play order). Menu-navigated iPods order by their own indexes, so they stay false.</summary>
    public virtual bool SupportsReorder => false;

    /// <summary>Whether the sidebar shows Podcasts/Audiobooks sub-views under the device. A screenless
    /// one-list player (Shuffle) has no such menus - pushed episodes fold into its single track list -
    /// so the sub-views would only ever be empty and are hidden entirely.</summary>
    public virtual bool HasKindSubViews => true;

    /// <summary>Whether <see cref="AddTrackAsync"/> works on this tier at all - the honest "can this
    /// device take songs" gate. Distinct from <see cref="SupportsPlaylists"/>: a playlist synced to a
    /// tracks-only device still delivers its songs, just without the native playlist.</summary>
    public virtual bool SupportsTrackAdd => false;

    /// <summary>Whether <see cref="AddTrackAsync"/> would transcode this track rather than copy it -
    /// so progress UI can say "Transcoding" only when that's the truth. Base is false (Rockbox and
    /// generic players copy everything); each stock tier answers with its own compatibility rules.</summary>
    public virtual bool WillTranscode(MediaItem libraryTrack) => false;

    /// <summary>Persists a new flat play order. <paramref name="orderedTracks"/> are the device's own
    /// items (from <see cref="ReadLibraryAsync"/>) in the desired order; tracks on the device that the
    /// caller doesn't mention keep their old relative order after the mentioned ones.</summary>
    public virtual Task ReorderAsync(IReadOnlyList<MediaItem> orderedTracks, CancellationToken ct = default)
        => throw new NotImplementedException($"Reorder is not implemented for {GetType().Name} ({Generation ?? "?"}).");

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
    /// <paramref name="onProgress"/>, when set, receives ("transcode"|"copy", 0..1) - each phase runs
    /// its own 0..1 so progress UI can show a true bar per stage.
    /// </summary>
    public virtual Task<MediaItem> AddTrackAsync(MediaItem libraryTrack, string ffmpegPath, Action<string, double>? onProgress = null, CancellationToken ct = default)
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
    /// Removes a device playlist by name - mirror-sync pruning of an orphaned playlist. Best-effort
    /// and idempotent: the base is a no-op, so a tier that can't (or needn't) prune simply skips
    /// rather than throwing.
    /// </summary>
    public virtual Task RemovePlaylistAsync(string name, CancellationToken ct = default) => Task.CompletedTask;

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
    public override bool SupportsTrackAdd => true;

    /// <summary>Mirrors ImportToNano5gAsync's pass-through set (.mp3/.m4a/.m4b/.aac): the 5G takes
    /// MP3 and MP4-container codecs as-is and transcodes everything else (FLAC/WAV/AIFF) to ALAC.</summary>
    public override bool WillTranscode(MediaItem libraryTrack)
        => !string.IsNullOrEmpty(libraryTrack.FilePath)
           && Path.GetExtension(libraryTrack.FilePath).ToLowerInvariant() is not (".mp3" or ".m4a" or ".m4b" or ".aac");

    public override Task<int> AddPodcastsAsync(IReadOnlyList<PodcastPush> episodes, string ffmpegPath, Action<int, int>? onProgress = null, CancellationToken ct = default)
        => Task.Run(() => IPodTrackImporter.AddPodcastEpisodesNano5gAsync(MountPath, ToEpisodes(episodes), ffmpegPath, Device.FireWireGuid, onProgress, ct), ct);

    public override Task<int> EraseAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            // Delete the audio + artwork caches, then empty the SQLite library and re-sign the cbk.
            int removed = DeleteFilesUnder(IPodPaths.Music(MountPath))
                        + DeleteFilesUnder(IPodPaths.Artwork(MountPath));
            new Nano5gLibraryWriter(IPodPaths.Itlp(MountPath)).WipeLibrary();
            return removed;
        }, ct);

    public override Task RemoveTrackAsync(MediaItem item, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var itlp = IPodPaths.Itlp(MountPath);
            var musicRoot = IPodPaths.Music(MountPath);
            var relative = RelativeUnderMusic(item.FilePath, musicRoot)
                ?? throw new InvalidOperationException($"“{item.Title}” isn't under the iPod's Music folder.");
            long pid = Nano5gLibraryWriter.FindItemPidByLocation(itlp, relative);
            if (pid == 0)
            {
                throw new InvalidOperationException($"“{item.Title}” isn't in the iPod database.");
            }
            new Nano5gLibraryWriter(itlp, Device.FireWireGuid).RemoveTrack(pid, musicRoot);
        }, ct);

    public override Task<MediaItem> AddTrackAsync(MediaItem libraryTrack, string ffmpegPath, Action<string, double>? onProgress = null, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            var r = await IPodTrackImporter.ImportAsync(MountPath, libraryTrack.FilePath!, ffmpegPath, Generation, Device.FireWireGuid, onProgress, ct);
            return DeviceItemFromImport(libraryTrack, r);
        }, ct);

    public override Task CreatePlaylistAsync(string name, IReadOnlyList<MediaItem> deviceTracks, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var itlp = IPodPaths.Itlp(MountPath);
            var musicRoot = IPodPaths.Music(MountPath);
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
            var itlp = IPodPaths.Itlp(MountPath);
            onProgress?.Invoke("Reading iTunes Library.itlp...");
            Nano5gLibraryReader.ReadAll(itlp, MountPath, out var tracks, out var playlists);
            var items = tracks.Select(t => DeviceItemFromTrack(t, MountPath)).ToList();
            onBatch?.Invoke(items);
            return new DeviceLibrary(items, playlists);
        }, ct);

    public override IDisposable? BeginBatchWrite()
        => new Nano5gLibraryWriter(IPodPaths.Itlp(MountPath), Device.FireWireGuid).BeginCdbBatch();
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
    public override bool SupportsTrackAdd => true;

    /// <summary>Mirrors <see cref="IPodTrackImporter.ImportAsync"/>'s decision: non-native formats
    /// transcode, and on the ALAC-less 1G/2G an ALAC-in-.m4a source re-encodes to AAC too.</summary>
    public override bool WillTranscode(MediaItem libraryTrack)
    {
        if (string.IsNullOrEmpty(libraryTrack.FilePath))
        {
            return false;
        }
        var ext = Path.GetExtension(libraryTrack.FilePath);
        if (!IPodTrackImporter.IsNativelyCompatible(ext))
        {
            return true;
        }
        return Generation is "1G" or "2G"
            && (ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m4b", StringComparison.OrdinalIgnoreCase))
            && IPodTrackImporter.IsAlacFile(libraryTrack.FilePath);
    }

    public override Task<int> AddPodcastsAsync(IReadOnlyList<PodcastPush> episodes, string ffmpegPath, Action<int, int>? onProgress = null, CancellationToken ct = default)
        => Task.Run(() => IPodTrackImporter.AddPodcastEpisodes(MountPath, Device.IpodGeneration, Device.FireWireGuid, ToEpisodes(episodes), onProgress), ct);

    public override Task RemoveTrackAsync(MediaItem item, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!TryParseDeviceItemId(item.Id, out var trackId))
            {
                throw new InvalidOperationException($"Couldn't identify “{item.Title}” on the iPod.");
            }
            var dbPath = IPodPaths.ITunesDb(MountPath);
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
            int removed = DeleteFilesUnder(IPodPaths.Music(MountPath))
                        + DeleteFilesUnder(IPodPaths.Artwork(MountPath));

            var dbPath = IPodPaths.ITunesDb(MountPath);
            if (File.Exists(dbPath) && new FileInfo(dbPath).Length > 0)
            {
                var doc = ITunesDbChunkTree.Parse(File.ReadAllBytes(dbPath));
                ITunesDbWriter.ClearLibrary(doc);
                IPodTrackImporter.CommitDb(doc, dbPath, MountPath, Device.IpodGeneration, Device.FireWireGuid);
            }
            return removed;
        }, ct);

    public override Task<MediaItem> AddTrackAsync(MediaItem libraryTrack, string ffmpegPath, Action<string, double>? onProgress = null, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            var r = await IPodTrackImporter.ImportAsync(MountPath, libraryTrack.FilePath!, ffmpegPath, Generation, Device.FireWireGuid, onProgress, ct);
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

    public override Task RemovePlaylistAsync(string name, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var dbPath = IPodPaths.ITunesDb(MountPath);
            if (!File.Exists(dbPath))
            {
                return;
            }
            // Reuse the proven drop-by-name (the same call the idempotent playlist re-sync makes), then
            // the standard commit discipline - checksum, one-time backup, atomic swap. Masters are safe.
            var doc = ITunesDbChunkTree.Parse(File.ReadAllBytes(dbPath));
            ITunesDbWriter.RemovePlaylistsByName(doc, name);
            IPodTrackImporter.CommitDb(doc, dbPath, MountPath, Device.IpodGeneration, Device.FireWireGuid);
        }, ct);

    public override Task<DeviceLibrary> ReadLibraryAsync(Action<IReadOnlyList<MediaItem>>? onBatch = null, Action<string>? onProgress = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var dbPath = IPodPaths.ITunesDb(MountPath);
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
    public override bool SupportsTrackAdd => true;         // filesystem copy - always available

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

    public override Task<MediaItem> AddTrackAsync(MediaItem libraryTrack, string ffmpegPath, Action<string, double>? onProgress = null, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            // Rockbox plays straight off disk: copy into /Music/{Artist}/{Album}/ (no transcode) and hand
            // back a device-side item pointing at the on-device path.
            var dest = Path.GetFullPath(Path.Combine(MountPath, BuildMusicRelativePath(libraryTrack).TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (!File.Exists(dest))
            {
                await IPodTrackImporter.CopyFileWithProgressAsync(libraryTrack.FilePath!, dest, onProgress == null ? null : f => onProgress("copy", f), ct);
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
                Composer = libraryTrack.Composer,
                Duration = libraryTrack.Duration,
                Track = libraryTrack.Track,
                TotalTracks = libraryTrack.TotalTracks,
                Disc = libraryTrack.Disc,
                TotalDiscs = libraryTrack.TotalDiscs,
                Year = libraryTrack.Year,
                Extension = Path.GetExtension(dest),
                HasAlbumArt = libraryTrack.HasAlbumArt,
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

    public override Task RemovePlaylistAsync(string name, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var target = Path.Combine(MountPath, "Playlists", Sanitize(name) + ".m3u8");
            if (File.Exists(target))
            {
                File.Delete(target);
            }
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
/// "bdhs" on 3G/4G). The hardware has no playlist or podcast concept, so both capabilities are
/// honestly false: a playlist sync just appends its tracks to the one list, and podcasts aren't
/// offered at all - only LEGIT support, no folding episodes into the song pile. (The 3G/4G bdhs
/// format has typed playlist/podcast list sections + VoiceOver - real support is possible there,
/// pending hardware to validate on.) Drag-reorder of the device grid IS the ordering tool.
/// </summary>
public sealed class ShuffleIPod : IPodDevice
{
    public ShuffleIPod(ConnectedDevice device) : base(device) { }

    public override bool SupportsDatabaseWrite => true;
    public override bool SupportsPlaylists => false;   // the hardware has NO playlist concept - a playlist sync delivers its tracks (appended), nothing more
    public override bool SupportsPodcasts => false;    // no podcast concept either - only LEGIT support counts, no folding episodes into the song list (3G/4G bdhs typed lists could do it for real, pending metal)
    public override bool SupportsArtwork => false;
    public override bool SupportsReorder => true;      // the iTunesSD list IS the play order
    public override bool HasKindSubViews => false;     // one flat list, no Podcasts/Audiobooks menus
    public override bool SupportsTrackAdd => true;

    public override bool WillTranscode(MediaItem libraryTrack)
        => !string.IsNullOrEmpty(libraryTrack.FilePath) && !PlaysNatively(libraryTrack.FilePath);

    public override Task ReorderAsync(IReadOnlyList<MediaItem> orderedTracks, CancellationToken ct = default)
        => Task.Run(() =>
        {
            // Reorder the EXISTING iTunesSD entries so each keeps its own volume/start/stop/flags -
            // never rebuild them from MediaItems. Entries the caller didn't mention (e.g. podcasts
            // hidden from the music view) keep their old relative order after the mentioned ones.
            var current = ReadSd();
            var byPath = new Dictionary<string, ShuffleSdTrack>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in current)
            {
                byPath[entry.IpodPath] = entry;
            }

            var result = new List<ShuffleSdTrack>(current.Count);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in orderedTracks)
            {
                if (string.IsNullOrEmpty(t.FilePath))
                {
                    continue;
                }
                var ipodPath = ToIpodPath(t.FilePath);
                if (byPath.TryGetValue(ipodPath, out var entry) && used.Add(ipodPath))
                {
                    result.Add(entry);
                }
            }
            foreach (var entry in current)
            {
                if (!used.Contains(entry.IpodPath))
                {
                    result.Add(entry);
                }
            }
            WriteSd(result);
        }, ct);

    private string ITunesDir => IPodPaths.ITunesDir(MountPath);
    private string MusicDir => Path.Combine(IPodPaths.Music(MountPath), "F00");

    /// <summary>3G/4G Shuffles use the newer little-endian "bdhs" iTunesSD; 1G/2G the classic format.</summary>
    private bool UsesBdhs => Generation is not null && (Generation.Contains("3G") || Generation.Contains("4G"));
    private List<ShuffleSdTrack> ReadSd() => UsesBdhs ? ShuffleBdhsWriter.Read(ITunesDir) : ShuffleSdWriter.Read(ITunesDir);
    private void WriteSd(IReadOnlyList<ShuffleSdTrack> list)
    {
        if (UsesBdhs) { ShuffleBdhsWriter.Write(ITunesDir, list); }
        else { ShuffleSdWriter.Write(ITunesDir, list); }
    }

    private static readonly Serilog.ILogger _log = Logging.For("ShuffleSync");

    /// <summary>Extensions the Shuffle firmware decodes natively (every generation): MP3, AAC-family
    /// .m4a/.m4b, WAV. Anything else (FLAC, OGG, ...) MUST be transcoded first: hardware-confirmed on a
    /// real 2G that a copied FLAC is silently skipped by the firmware. An .m4a needs a second look on
    /// 1G/2G - see <see cref="PlaysNatively"/>.</summary>
    private static readonly HashSet<string> NativeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".m4b", ".aac", ".wav" };

    /// <summary>ALAC decode arrived with the Shuffle 3G (2009). The 1G/2G lack the horsepower - iTunes
    /// itself converts lossless to AAC when syncing them. Hardware-confirmed on a real 2G: a perfectly
    /// valid ALAC .m4a is silently skipped just like the FLAC was.</summary>
    private bool SupportsAlac => UsesBdhs;

    /// <summary>True when this Shuffle's firmware can decode the file as-is. Extension check first; for
    /// an .m4a/.m4b on a 1G/2G the container has to be asked whether the codec is ALAC (the extension
    /// can't tell), because those generations only decode the AAC family.</summary>
    private bool PlaysNatively(string sourceFile)
    {
        var ext = Path.GetExtension(sourceFile);
        if (!NativeExtensions.Contains(ext))
        {
            return false;
        }
        if (SupportsAlac || (!ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".m4b", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return !IPodTrackImporter.IsAlacFile(sourceFile);
    }

    /// <summary>ffmpeg's tag mapping into the iTunes .m4a atoms drops fields (track/disc numbers, genre,
    /// art) - re-copy the source's tags onto the staged file with TagLib so the device file carries the
    /// full metadata. Best-effort: a tag failure never blocks the sync.</summary>
    private static void CopyTags(string sourceFile, string stagedFile)
    {
        try
        {
            using var src = TagLib.File.Create(sourceFile);
            using var dst = TagLib.File.Create(stagedFile);
            src.Tag.CopyTo(dst.Tag, overwrite: true);
            dst.Tag.Pictures = src.Tag.Pictures;
            dst.Save();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Couldn't copy tags from {Source} onto the transcoded file", sourceFile);
        }
    }

    /// <summary>Lands a source file in Music/F00 in a format this Shuffle can play: native formats copy
    /// as-is, everything else transcodes into an .m4a (ALAC lossless on 3G/4G, AAC 256k on 1G/2G) and
    /// gets the source's tags re-applied. Names are iTunes-style random 4-caps - the only alphabet the
    /// firmware has ever parsed (a single U+2019 in an iTunesSD path made a real 2G silently skip the
    /// track). Progress: ("transcode", 0..1) for the encode, then ("copy", 0..1) for the device write -
    /// the slow phase on the Shuffle's USB 1.1 link.</summary>
    private async Task<string> StageIntoMusicAsync(string sourceFile, string ffmpegPath, int sourceSampleRate, int durationMs, Action<string, double>? onProgress, CancellationToken ct)
    {
        Directory.CreateDirectory(MusicDir);
        if (PlaysNatively(sourceFile))
        {
            var nativeDest = IPodTrackImporter.UniqueTrackPath(MusicDir, Path.GetExtension(sourceFile));
            await IPodTrackImporter.CopyFileWithProgressAsync(sourceFile, nativeDest, onProgress == null ? null : f => onProgress("copy", f), ct);
            return nativeDest;
        }

        var dest = IPodTrackImporter.UniqueTrackPath(MusicDir, ".m4a");
        var staged = Path.Combine(Path.GetTempPath(), "orgz_shuf_" + Guid.NewGuid().ToString("N")[..8] + ".m4a");
        try
        {
            int rate = IPodTrackImporter.TargetSampleRate(sourceSampleRate);
            if (SupportsAlac)
            {
                await IPodTrackImporter.TranscodeToAlacAsync(ffmpegPath, sourceFile, staged, rate, durationMs, onProgress == null ? null : f => onProgress("transcode", f), ct);
            }
            else
            {
                await IPodTrackImporter.TranscodeToAacAsync(ffmpegPath, sourceFile, staged, rate, durationMs, onProgress == null ? null : f => onProgress("transcode", f), ct);
            }
            CopyTags(sourceFile, staged);
            await IPodTrackImporter.CopyFileWithProgressAsync(staged, dest, onProgress == null ? null : f => onProgress("copy", f), ct);
        }
        finally
        {
            try { File.Delete(staged); } catch { /* temp cleanup - best effort */ }
        }
        return dest;
    }

    public override Task<MediaItem> AddTrackAsync(MediaItem libraryTrack, string ffmpegPath, Action<string, double>? onProgress = null, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            // Stage into Music/F00 (copy or transcode - the firmware only plays MP3/AAC/ALAC/WAV), then
            // add it to the iTunesSD so the firmware plays it AND to the iTunesDB so iTunes sees it -
            // both files, every time, exactly like iTunes itself (see the co-habitation block below).
            var dest = await StageIntoMusicAsync(libraryTrack.FilePath!, ffmpegPath, libraryTrack.SampleRate ?? 0, (int)(libraryTrack.Duration?.TotalMilliseconds ?? 0), onProgress, ct);
            AppendToSd(dest);
            SyncDbWithSd(dest, libraryTrack);

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
                Composer = libraryTrack.Composer,
                Duration = libraryTrack.Duration,
                Track = libraryTrack.Track,
                TotalTracks = libraryTrack.TotalTracks,
                Disc = libraryTrack.Disc,
                TotalDiscs = libraryTrack.TotalDiscs,
                Year = libraryTrack.Year,
                Extension = Path.GetExtension(dest),
                HasAlbumArt = libraryTrack.HasAlbumArt,
                FileSize = size,
                IsAnalyzed = true,
            };
        }, ct);

    public override Task<DeviceLibrary> ReadLibraryAsync(Action<IReadOnlyList<MediaItem>>? onBatch = null, Action<string>? onProgress = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            onProgress?.Invoke("Reading iTunesSD...");

            // The iTunesSD is only paths - no titles, no artists. But iTunes writes a full iTunesDB
            // beside it on every Shuffle it syncs (the firmware ignores it; iTunes reads it back), so
            // that's where the metadata lives. Join SD entries to DB rows by device path; entries the
            // DB doesn't know (OrgZ-copied files, foreign syncs) fall back to the file's own tags.
            var byPath = new Dictionary<string, ITunesDbReader.ITunesTrack>(StringComparer.OrdinalIgnoreCase);
            var dbPath = IPodPaths.ITunesDb(MountPath);
            if (File.Exists(dbPath) && new FileInfo(dbPath).Length > 0)
            {
                onProgress?.Invoke("Parsing iTunesDB...");
                foreach (var t in ITunesDbReader.Read(dbPath, MountPath))
                {
                    if (!string.IsNullOrEmpty(t.FilePath))
                    {
                        byPath[t.FilePath] = t;
                    }
                }
            }

            var items = new List<MediaItem>();
            foreach (var e in ReadSd())
            {
                ct.ThrowIfCancellationRequested();
                var abs = Path.GetFullPath(Path.Combine(MountPath, e.IpodPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
                if (byPath.TryGetValue(abs, out var dbTrack))
                {
                    items.Add(DeviceItemFromTrack(dbTrack, MountPath));
                    continue;
                }

                long size = 0;
                try { size = File.Exists(abs) ? new FileInfo(abs).Length : 0; } catch { /* cosmetic */ }
                var item = new MediaItem
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
                };
                if (size > 0)
                {
                    AudioFileAnalyzer.AnalyzeFile(item);
                }
                item.IsAnalyzed = true;
                items.Add(item);
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
            RemoveDbRow(item.FilePath);
            if (File.Exists(item.FilePath))
            {
                File.Delete(item.FilePath);
            }
        }, ct);

    public override Task<int> EraseAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            int removed = DeleteFilesUnder(IPodPaths.Music(MountPath));
            WriteSd([]);   // valid header, zero entries

            // Keep iTunes's view in step: an erased device must read as empty there too.
            var dbPath = IPodPaths.ITunesDb(MountPath);
            if (File.Exists(dbPath) && new FileInfo(dbPath).Length > 0)
            {
                var doc = ITunesDbChunkTree.Parse(File.ReadAllBytes(dbPath));
                ITunesDbWriter.ClearLibrary(doc);
                IPodTrackImporter.CommitDb(doc, dbPath, MountPath, Device.IpodGeneration, Device.FireWireGuid);
            }
            return removed;
        }, ct);

    // ── iTunes co-habitation ─────────────────────────────────────────────────
    // The firmware plays from iTunesSD, but iTUNES reads the iTunesDB - and writes BOTH on every
    // sync. So must we: without DB rows our tracks are invisible in iTunes, and an iTunes sync
    // would rewrite the iTunesSD from its stale view, clobbering every OrgZ-added track.
    // (Hardware-found: a device OrgZ had synced for a day showed its 2023 iTunes library, and
    // nothing else, when plugged into iTunes.)

    /// <summary>Brings the iTunesDB in line with the iTunesSD after an add: every SD entry gets a DB
    /// row. The just-added file carries its library metadata; entries from before OrgZ maintained
    /// the DB are backfilled with tags read from their files, so one new add heals the whole device.
    /// Existing rows (iTunes's own) are never touched.</summary>
    private void SyncDbWithSd(string justAddedDest, MediaItem justAddedMeta)
    {
        var dbPath = IPodPaths.ITunesDb(MountPath);
        ITunesDbDocument doc;
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(dbPath) && new FileInfo(dbPath).Length > 0)
        {
            var bytes = File.ReadAllBytes(dbPath);
            doc = ITunesDbChunkTree.Parse(bytes);
            ITunesDbReader.ReadAll(bytes, MountPath, out var rows, out _);
            foreach (var row in rows)
            {
                if (!string.IsNullOrEmpty(row.FilePath))
                {
                    known.Add(row.FilePath);
                }
            }
        }
        else
        {
            doc = ITunesDbWriter.CreateEmpty();
        }

        bool changed = false;
        foreach (var e in ReadSd())
        {
            var abs = Path.GetFullPath(Path.Combine(MountPath, e.IpodPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            if (known.Contains(abs) || !File.Exists(abs))
            {
                continue;
            }

            MediaItem meta;
            if (string.Equals(abs, justAddedDest, StringComparison.OrdinalIgnoreCase))
            {
                meta = justAddedMeta;
            }
            else
            {
                meta = new MediaItem { Id = abs, Kind = MediaKind.Music, FilePath = abs, Title = Path.GetFileNameWithoutExtension(abs) };
                AudioFileAnalyzer.AnalyzeFile(meta);
            }

            ITunesDbWriter.AddTrack(doc, new NewTrack
            {
                TrackId = ITunesDbWriter.NextTrackId(doc),
                IpodPath = ":" + Path.GetRelativePath(MountPath, abs).Replace('\\', ':').Replace('/', ':'),
                Title = meta.Title,
                Artist = meta.Artist,
                Album = meta.Album,
                Genre = meta.Genre,
                Composer = meta.Composer,
                FileSize = new FileInfo(abs).Length,
                LengthMs = (int)(meta.Duration?.TotalMilliseconds ?? 0),
                Bitrate = meta.AudioBitrate ?? 0,
                SampleRate = meta.SampleRate ?? 0,
                Year = (int)(meta.Year ?? 0),
                TrackNumber = (int)(meta.Track ?? 0),
                TotalTracks = (int)(meta.TotalTracks ?? 0),
                DiscNumber = (int)(meta.Disc ?? 0),
                TotalDiscs = (int)(meta.TotalDiscs ?? 0),
                DateAddedUtc = DateTime.UtcNow,
                Dbid = (ulong)Random.Shared.NextInt64(1, long.MaxValue),
            });
            changed = true;
        }

        if (changed)
        {
            IPodTrackImporter.CommitDb(doc, dbPath, MountPath, Device.IpodGeneration, Device.FireWireGuid);
        }
    }

    /// <summary>Drops the removed track's iTunesDB row (matched by on-device path) so iTunes never
    /// shows a ghost. Rows we can't match - or a device with no DB at all - are left alone.</summary>
    private void RemoveDbRow(string absoluteFilePath)
    {
        var dbPath = IPodPaths.ITunesDb(MountPath);
        if (!File.Exists(dbPath) || new FileInfo(dbPath).Length == 0)
        {
            return;
        }
        var bytes = File.ReadAllBytes(dbPath);
        ITunesDbReader.ReadAll(bytes, MountPath, out var rows, out _);
        var row = rows.FirstOrDefault(t => string.Equals(t.FilePath, absoluteFilePath, StringComparison.OrdinalIgnoreCase));
        if (row == null)
        {
            return;
        }
        var doc = ITunesDbChunkTree.Parse(bytes);
        if (ITunesDbWriter.RemoveTrack(doc, row.TrackId))
        {
            IPodTrackImporter.CommitDb(doc, dbPath, MountPath, Device.IpodGeneration, Device.FireWireGuid);
        }
    }

    private string ToIpodPath(string absolute) => "/" + Path.GetRelativePath(MountPath, absolute).Replace('\\', '/');

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
