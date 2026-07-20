// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using Serilog;

namespace OrgZ.Services;

/// <summary>Result of importing one track onto a device.</summary>
public sealed record IPodImportResult(uint TrackId, string IpodPath, string DestFile, string? Title, ulong Dbid);

/// <summary>
/// Copies a library track onto a stock iPod and registers it in the iTunesDB.
/// Formats the iPod can play (MP3/AAC/ALAC/AIFF/WAV) are copied as-is; anything
/// else (FLAC) is transcoded to ALAC (.m4a, lossless) via ffmpeg. The file lands
/// in iPod_Control/Music/F00 and the DB is updated through <see cref="ITunesDbWriter"/>
/// with a backup + atomic replace + re-parse check.
/// </summary>
public static class IPodTrackImporter
{
    private static readonly ILogger _log = Logging.For("IPodImport");

    // Containers/codecs a stock iPod (5G/5.5G era) plays without transcoding.
    private static readonly HashSet<string> CompatibleExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".m4b", ".aac", ".aif", ".aiff", ".wav" };

    /// <summary>True when a stock iPod plays this file extension as-is (no transcode needed).</summary>
    internal static bool IsNativelyCompatible(string extension) => CompatibleExtensions.Contains(extension);

    /// <summary>
    /// Container we produce on disk: the source extension (lower-cased) when natively
    /// playable, otherwise ALAC in an .m4a wrapper.
    /// </summary>
    internal static string TargetExtension(string sourceExtension)
        => IsNativelyCompatible(sourceExtension) ? sourceExtension.ToLowerInvariant() : ".m4a";

    /// <summary>True when the file's audio codec is ALAC (regardless of extension - it hides in the same
    /// .m4a container as AAC). The devices that can't decode ALAC (iPod 1G/2G, Shuffle 1G/2G) need this
    /// container-level check; unreadable files read as "not ALAC" so they fall back to a plain copy.</summary>
    internal static bool IsAlacFile(string sourceFile)
    {
        try
        {
            using var f = TagLib.File.Create(sourceFile);
            return f.Properties.Codecs?.OfType<TagLib.IAudioCodec>().Any(c => c.Description?.Contains("alac", StringComparison.OrdinalIgnoreCase) == true) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Stock iPods (5G/5.5G) decode ALAC only up to 48 kHz / 16-bit, and the iTunesDB stores
    /// the sample rate as <c>rate &lt;&lt; 16</c> (so anything &gt; 65535 corrupts). Keep a
    /// source rate already &lt;= 48 kHz, otherwise resample to CD 44.1 kHz.
    /// </summary>
    internal static int TargetSampleRate(int sourceSampleRate)
        => sourceSampleRate is > 0 and <= 48000 ? sourceSampleRate : 44100;

    public static async Task<IPodImportResult> ImportAsync(
        string mountPath,
        string sourceFile,
        string ffmpegPath,
        string? generation = null,
        string? fireWireGuid = null,
        Action<string, double>? onProgress = null,   // ("transcode"|"copy", 0..1) - each phase runs its own 0..1
        CancellationToken ct = default)
    {
        if (!File.Exists(sourceFile))
        {
            throw new FileNotFoundException("Source track not found.", sourceFile);
        }

        // Refuse generations whose iTunesDB we can't safely write (they need a
        // checksum we don't generate) - better to decline than brick the library view.
        if (!IPodCapabilities.SupportsDatabaseWrite(generation))
        {
            throw new NotSupportedException(
                $"Writing to this iPod ('{generation ?? "unknown"}') isn't supported yet — it requires an iTunesDB checksum OrgZ doesn't generate.");
        }
        if (IPodCapabilities.ChecksumFor(generation) == IPodChecksum.Hash58 && string.IsNullOrWhiteSpace(fireWireGuid))
        {
            throw new InvalidOperationException(
                $"This iPod ('{generation}') needs its FireWireGuid to checksum the iTunesDB (hash58).");
        }

        // --- source tags + audio properties ---
        // Read length/sample-rate from the SOURCE: TagLib reports these reliably for
        // FLAC, whereas the freshly-muxed ALAC output sometimes reads back as 0. The
        // values are codec-invariant for a lossless->lossless transcode anyway.
        // Audiobook-ness is detected here too (container/location, then tags), so every
        // import path - drag-drop, playlist sync, send-to-device - lands a library
        // audiobook as an iPod audiobook without any caller having to say so.
        string? title = null, artist = null, album = null, genre = null;
        int year = 0, trackNo = 0, srcLengthMs = 0, srcSampleRate = 0;
        bool isAudiobook = AudiobookDetector.KindForPath(sourceFile) == MediaKind.Audiobook;
        try
        {
            using var tf = TagLib.File.Create(sourceFile);
            title   = NullIfEmpty(tf.Tag.Title) ?? Path.GetFileNameWithoutExtension(sourceFile);
            artist  = NullIfEmpty(tf.Tag.FirstPerformer) ?? NullIfEmpty(tf.Tag.FirstAlbumArtist);
            album   = NullIfEmpty(tf.Tag.Album);
            genre   = NullIfEmpty(tf.Tag.FirstGenre);
            year    = (int)tf.Tag.Year;
            trackNo = (int)tf.Tag.Track;
            srcLengthMs   = (int)tf.Properties.Duration.TotalMilliseconds;
            srcSampleRate = tf.Properties.AudioSampleRate;
            isAudiobook  |= AudiobookDetector.TagsSayAudiobook(tf);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Tag read failed for {Source}; importing with filename only", sourceFile);
            title ??= Path.GetFileNameWithoutExtension(sourceFile);
        }

        // Nano 5G (Hash72) reads the "iTunes Library.itlp" SQLite stack, not the binary
        // iTunesDB - route to the SQLite writer (which re-signs Locations.itdb.cbk).
        if (IPodCapabilities.ChecksumFor(generation) == IPodChecksum.Hash72)
        {
            return await ImportToNano5gAsync(mountPath, sourceFile, ffmpegPath, generation, fireWireGuid,
                title, artist, album, genre, year, trackNo, srcLengthMs, srcSampleRate, isAudiobook, onProgress, ct);
        }

        // --- produce an iPod-compatible file ---
        var ext = Path.GetExtension(sourceFile);
        bool compatible = IsNativelyCompatible(ext);
        string targetExt = TargetExtension(ext);

        // The FireWire-era iPod 1G/2G never got ALAC - Apple shipped Apple Lossless decode (mid-2004
        // firmware) to dock-connector models only. Same silent-skip failure the Shuffle 2G showed on
        // metal, so those two transcode to AAC 256k instead, and an ALAC-in-.m4a source (which the
        // extension alone can't reveal) counts as incompatible for them.
        bool supportsAlac = generation is not ("1G" or "2G");
        if (compatible && !supportsAlac
            && (ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m4b", StringComparison.OrdinalIgnoreCase))
            && IsAlacFile(sourceFile))
        {
            compatible = false;
        }

        // Stock iPods (5G/5.5G) decode ALAC only up to 48 kHz / 16-bit - hi-res
        // (e.g. 96/24) won't play and also overflows the iTunesDB sample-rate field
        // (stored as rate << 16, so anything > 65535 corrupts). Target CD resolution:
        // keep the source rate when it's already <= 48 kHz, otherwise resample to
        // 44.1 kHz, and force 16-bit. That's the lossless ceiling the hardware supports.
        int targetSampleRate = TargetSampleRate(srcSampleRate);

        string producedFile;
        bool producedIsTemp = false;
        if (compatible)
        {
            producedFile = sourceFile;
        }
        else
        {
            producedFile = Path.Combine(Path.GetTempPath(), "orgz_alac_" + Guid.NewGuid().ToString("N")[..8] + ".m4a");
            producedIsTemp = true;
            var report = onProgress;
            if (supportsAlac)
            {
                await TranscodeToAlacAsync(ffmpegPath, sourceFile, producedFile, targetSampleRate, srcLengthMs, report == null ? null : f => report("transcode", f), ct);
            }
            else
            {
                await TranscodeToAacAsync(ffmpegPath, sourceFile, producedFile, targetSampleRate, srcLengthMs, report == null ? null : f => report("transcode", f), ct);
            }
        }

        try
        {
            // --- copy onto the device ---
            const string folder = "F00";
            var destDir = Path.Combine(mountPath, "iPod_Control", "Music", folder);
            Directory.CreateDirectory(destDir);

            var destFile = UniqueTrackPath(destDir, targetExt);
            string fileName = Path.GetFileName(destFile);
            var copyReport = onProgress;
            await CopyFileWithProgressAsync(producedFile, destFile, copyReport == null ? null : f => copyReport("copy", f), ct);
            string ipodPath = $":iPod_Control:Music:{folder}:{fileName}";

            // --- output properties ---
            long fileSize = new FileInfo(destFile).Length;
            // Duration is preserved across the transcode; sample rate is whatever we
            // targeted (transcode) or the source's (passthrough). We don't trust
            // TagLib for ALAC properties (it reads sample rate back as 0).
            int lengthMs = srcLengthMs;
            int sampleRate = compatible ? srcSampleRate : targetSampleRate;
            if (lengthMs == 0)
            {
                try
                {
                    using var of = TagLib.File.Create(destFile);
                    lengthMs = (int)of.Properties.Duration.TotalMilliseconds;
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Could not read duration of {Dest}", destFile);
                }
            }
            // Average bitrate (kbps) from the encoded size - meaningful for VBR/lossless,
            // where a single nominal bitrate doesn't exist.
            int bitrate = lengthMs > 0 ? (int)(fileSize * 8L / lengthMs) : 0;

            // --- album art (best-effort): cover -> RGB565 .ithmb + ArtworkDB ---
            // The track's dbid ties the iTunesDB MHIT to the ArtworkDB mhii.
            ulong dbid = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
            var coverFormats = IPodCapabilities.CoverFormatsFor(generation);
            var (hasArt, artSize, _) = await TryWriteArtworkAsync(mountPath, sourceFile, ffmpegPath, dbid, coverFormats, ct);

            // --- iTunesDB: parse -> add -> normalize -> verify -> backup -> atomic write ---
            var dbPath = Path.Combine(mountPath, "iPod_Control", "iTunes", "iTunesDB");
            var doc = ITunesDbChunkTree.Parse(File.ReadAllBytes(dbPath));

            uint trackId = ITunesDbWriter.NextTrackId(doc);
            ITunesDbWriter.AddTrack(doc, new NewTrack
            {
                TrackId      = trackId,
                IpodPath     = ipodPath,
                Title        = title,
                Artist       = artist,
                Album        = album,
                Genre        = genre,
                Year         = year,
                TrackNumber  = trackNo,
                FileSize     = fileSize,
                LengthMs     = lengthMs,
                Bitrate      = bitrate,
                SampleRate   = sampleRate,
                DateAddedUtc = DateTime.UtcNow,
                Dbid         = dbid,
                HasArtwork   = hasArt,
                ArtworkSize  = artSize,
                IsAudiobook  = isAudiobook,
            });

            var outBytes = CommitDb(doc, dbPath, mountPath, generation, fireWireGuid,
                verify: (vt, _) => vt.Any(t => t.TrackId == trackId),
                failureMessage: "Re-parse of the new iTunesDB did not contain the added track; aborting write.");

            _log.Information("Imported '{Title}' as track {Id} -> {IpodPath} ({Bytes} byte DB)", title, trackId, ipodPath, outBytes.Length);
            return new IPodImportResult(trackId, ipodPath, destFile, title, dbid);
        }
        finally
        {
            if (producedIsTemp && File.Exists(producedFile))
            {
                try { File.Delete(producedFile); } catch { }
            }
        }
    }

    /// <summary>
    /// Writes a user playlist (name + ordered track ids, which must already exist as MHITs)
    /// to the binary iTunesDB, re-checksums (Hash58 when the generation needs it), verifies the
    /// playlist re-parses, backs up the original once, and commits atomically. For Hash72 (Nano 5G)
    /// iPods the SQLite path is used instead (<see cref="Nano5gLibraryWriter.CreatePlaylist"/>).
    /// </summary>
    public static void AddPlaylist(string mountPath, string? generation, string? fireWireGuid, string name, IReadOnlyList<uint> trackIds)
    {
        var dbPath = Path.Combine(mountPath, "iPod_Control", "iTunes", "iTunesDB");
        var doc = ITunesDbChunkTree.Parse(File.ReadAllBytes(dbPath));

        ITunesDbWriter.AddPlaylist(doc, name, trackIds);

        var outBytes = CommitDb(doc, dbPath, mountPath, generation, fireWireGuid,
            verify: (_, playlists) => playlists.Any(p => string.Equals(p.Name, name, StringComparison.Ordinal)),
            failureMessage: "Re-parse of the new iTunesDB did not contain the playlist; aborting write.");

        _log.Information("Wrote playlist '{Name}' ({Count} tracks) to iTunesDB ({Bytes} bytes)", name, trackIds.Count, outBytes.Length);
    }

    /// <summary>One downloaded episode to push: local file + the metadata for its podcast MHIT.</summary>
    public sealed record PodcastEpisodeImport(string LocalFile, string Title, string Show, string? Description, string? RssUrl, DateTime PubDateUtc, int LengthMs, string? CoverImagePath = null);

    /// <summary>
    /// Copies every episode's file (MP3/AAC - passthrough, no transcode) into iPod_Control/Music and
    /// adds its podcast MHIT into ONE parsed iTunesDB, then normalizes/checksums/verifies/backs-up
    /// and writes a single time - loading + committing once rather than re-writing the whole DB per
    /// episode. Returns the number of episodes written.
    /// </summary>
    public static int AddPodcastEpisodes(string mountPath, string? generation, string? fireWireGuid, IReadOnlyList<PodcastEpisodeImport> episodes, Action<int, int>? onProgress = null)
    {
        if (episodes.Count == 0)
        {
            return 0;
        }

        const string folder = "F00";
        var destDir = Path.Combine(mountPath, "iPod_Control", "Music", folder);
        Directory.CreateDirectory(destDir);

        var dbPath = Path.Combine(mountPath, "iPod_Control", "iTunes", "iTunesDB");
        if (!File.Exists(dbPath))
        {
            // No binary iTunesDB here - either an empty/non-iTunes iPod or a SQLite-DB model
            // (Nano 5G+), which this Hash58 path doesn't handle. Fail clearly, don't crash.
            throw new FileNotFoundException($"This iPod has no iTunesDB at '{dbPath}'. Podcast sync currently needs an iTunes-format (binary) database.", dbPath);
        }
        var dbBytes = File.ReadAllBytes(dbPath);
        var doc = ITunesDbChunkTree.Parse(dbBytes);

        // Idempotent re-sync: episodes already on the device (matched by show + title) are skipped so
        // re-syncing the same downloads doesn't duplicate them. Podcast tracks are written with
        // Album = show name, which is the dedup key used below. Reads the same bytes the parse used -
        // no second trip through the file.
        ITunesDbReader.ReadAll(dbBytes, mountPath, out var existingTracks, out _);
        var existing = new HashSet<(string Show, string Title)>(
            existingTracks.Select(t => (t.Album ?? string.Empty, t.Title ?? string.Empty)));

        int added = 0;
        var podcastEntries = new List<(string Show, uint TrackId)>(episodes.Count);
        for (int i = 0; i < episodes.Count; i++)
        {
            var ep = episodes[i];
            onProgress?.Invoke(i + 1, episodes.Count);
            if (existing.Contains((ep.Show, ep.Title)))
            {
                continue;
            }
            try
            {
                var destFile = UniqueTrackPath(destDir, Path.GetExtension(ep.LocalFile));
                var fileName = Path.GetFileName(destFile);
                File.Copy(ep.LocalFile, destFile);

                uint trackId = ITunesDbWriter.NextTrackId(doc);
                // addToMasterPlaylists:false - podcasts must NOT be in the Library/MPL, so they
                // surface only under Podcasts (per the iTunesDB spec).
                ITunesDbWriter.AddTrack(doc, new NewTrack
                {
                    TrackId      = trackId,
                    IpodPath     = $":iPod_Control:Music:{folder}:{fileName}",
                    Title        = ep.Title,
                    Artist       = ep.Show,
                    Album        = ep.Show,
                    FileSize     = new FileInfo(destFile).Length,
                    LengthMs     = ep.LengthMs,
                    DateAddedUtc = DateTime.UtcNow,
                    Dbid         = (ulong)Random.Shared.NextInt64(1, long.MaxValue),
                    IsPodcast    = true,
                    Description  = ep.Description,
                    PodcastRss   = ep.RssUrl,
                    TimeReleased = ep.PubDateUtc,
                }, addToMasterPlaylists: false);
                podcastEntries.Add((ep.Show, trackId));
                existing.Add((ep.Show, ep.Title));
                added++;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to add podcast {File}", ep.LocalFile);
            }
        }

        if (added == 0)
        {
            return 0;
        }

        // Build the Podcasts playlist: per-show group headers (groupflag=256) + grouped episodes.
        // That show hierarchy is what the iPod's Podcasts menu actually renders.
        ITunesDbWriter.EnsurePodcastPlaylist(doc, podcastEntries);

        var outBytes = CommitDb(doc, dbPath, mountPath, generation, fireWireGuid,
            verify: (vt, _) => vt.Count > 0,
            failureMessage: "Re-parse of the new iTunesDB produced no tracks; aborting write.");

        _log.Information("Imported {Added} podcast episode(s) to iTunesDB ({Bytes} bytes)", added, outBytes.Length);
        return added;
    }

    /// <summary>
    /// Extracts the source's embedded cover, renders each thumbnail size in
    /// <paramref name="coverFormats"/> as raw RGB565-LE into
    /// iPod_Control/Artwork/F{id}_1.ithmb, and writes an ArtworkDB linking
    /// <paramref name="dbid"/> to them. Returns (false, 0) and writes nothing when
    /// the generation has no validated art formats or the source has no cover art.
    /// </summary>
    private static async Task<(bool ok, int artworkSize, int imageId)> TryWriteArtworkAsync(
        string mountPath, string sourceFile, string ffmpegPath, ulong dbid,
        IReadOnlyList<(int FormatId, int Width, int Height)> coverFormats, CancellationToken ct)
    {
        if (coverFormats.Count == 0)
        {
            return (false, 0, 0);   // generation without validated artwork formats - import without art
        }

        try
        {
            var artDir = Path.Combine(mountPath, "iPod_Control", "Artwork");
            Directory.CreateDirectory(artDir);
            var dbPath = Path.Combine(artDir, "ArtworkDB");

            // Read existing entries so we APPEND this track's art rather than clobber
            // every other track's (the from-scratch Build would otherwise drop them).
            var existing = new List<ArtImage>();
            if (File.Exists(dbPath))
            {
                try
                {
                    existing = ArtworkDbWriter.ReadImages(ITunesDbChunkTree.Parse(File.ReadAllBytes(dbPath)));
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Existing ArtworkDB unreadable; rebuilding from this track only");
                    existing = [];
                }
            }
            int imageId = ArtworkDbWriter.NextImageId(existing);

            var thumbs = new List<ArtThumb>();
            int totalSize = 0;
            foreach (var (formatId, w, h) in coverFormats)
            {
                int expected = w * h * 2;
                var ithmb = Path.Combine(artDir, $"F{formatId}_1.ithmb");
                var staged = ithmb + ".new";
                if (!await ExtractRgb565Async(ffmpegPath, sourceFile, w, h, staged, ct))
                {
                    return (false, 0, 0);   // no cover stream / ffmpeg failed
                }
                var raw = await File.ReadAllBytesAsync(staged, ct);
                File.Delete(staged);
                if (raw.Length != expected)
                {
                    _log.Warning("Thumbnail F{Fmt} is {Actual}B, expected {Expected}B", formatId, raw.Length, expected);
                    return (false, 0, 0);
                }
                // Append after any existing thumbnails already packed in this format's file.
                long offset = File.Exists(ithmb) ? new FileInfo(ithmb).Length : 0;
                try
                {
                    await using var fs = new FileStream(ithmb, FileMode.Append, FileAccess.Write);
                    await fs.WriteAsync(raw, ct);
                    await fs.FlushAsync(ct);
                }
                catch
                {
                    // A torn append would leave bytes the about-to-be-written ArtworkDB never
                    // records; roll the file back to its pre-append length so a retry starts clean.
                    try { using var t = new FileStream(ithmb, FileMode.Open, FileAccess.Write); t.SetLength(offset); } catch { }
                    throw;
                }
                thumbs.Add(new ArtThumb(formatId, w, h, (int)offset, expected));
                totalSize += expected;
            }

            var allImages = new List<ArtImage>(existing) { new(dbid, imageId, thumbs, totalSize) };
            var doc = ArtworkDbWriter.BuildFromImages(allImages);
            ITunesDbChunkTree.Normalize(doc.Root);
            var bytes = ITunesDbChunkTree.Serialize(doc);
            ITunesDbChunkTree.Parse(bytes);   // sanity: must re-parse

            AtomicFile.WriteAllBytes(dbPath, bytes, backup: dbPath + ".orgzbak");

            _log.Information("Wrote ArtworkDB image {ImageId} (+{Count} thumbnails, {Bytes}B); {Total} image(s) total", imageId, thumbs.Count, totalSize, allImages.Count);
            return (true, totalSize, imageId);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Artwork generation failed; importing without art");
            return (false, 0, 0);
        }
    }

    private static async Task<bool> ExtractRgb565Async(string ffmpegPath, string source, int w, int h, string dest, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
                 {
                     "-y", "-i", source,
                     "-map", "0:v:0",            // embedded cover (attached picture)
                     "-frames:v", "1",
                     "-vf", $"scale={w}:{h}",
                     "-pix_fmt", "rgb565le",
                     "-f", "rawvideo",
                     dest,
                 })
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg (artwork).");
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            _log.Warning("ffmpeg artwork extract failed ({W}x{H}, exit {Code})", w, h, proc.ExitCode);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Nano 5G (Hash72) podcast import: the SQLite sibling of <see cref="AddPodcastEpisodes"/>.
    /// Passes MP3/AAC through (transcodes anything else to ALAC), copies each episode under
    /// iPod_Control/Music, and writes it via <see cref="Nano5gLibraryWriter.AddPodcastEpisode"/>
    /// (media_kind=4 + the Podcasts container). Returns the number of episodes written.
    /// </summary>
    public static async Task<int> AddPodcastEpisodesNano5gAsync(
        string mountPath, IReadOnlyList<PodcastEpisodeImport> episodes, string ffmpegPath, string? fireWireGuid,
        Action<int, int>? onProgress = null, CancellationToken ct = default)
    {
        if (episodes.Count == 0)
        {
            return 0;
        }

        var itlp = Path.Combine(mountPath, "iPod_Control", "iTunes", "iTunes Library.itlp");
        var writer = new Nano5gLibraryWriter(itlp, fireWireGuid);
        const string folder = "F00";
        var destDir = Path.Combine(mountPath, "iPod_Control", "Music", folder);
        Directory.CreateDirectory(destDir);

        // One CDB regeneration for the whole batch instead of one per episode - each regeneration
        // re-reads the entire on-device library and recompresses + re-signs the iTunesCDB.
        using var cdbBatch = writer.BeginCdbBatch();

        int added = 0;
        for (int i = 0; i < episodes.Count; i++)
        {
            var ep = episodes[i];
            onProgress?.Invoke(i + 1, episodes.Count);

            // Idempotent re-sync: skip episodes already on the device so syncing the same
            // downloads again doesn't duplicate them (checked before any transcode/copy).
            if (writer.PodcastEpisodeExists(ep.Show, ep.Title))
            {
                continue;
            }

            var ext = Path.GetExtension(ep.LocalFile).ToLowerInvariant();
            int audioFormat, extFourCc;
            string kindString, targetExt;
            string produced = ep.LocalFile;
            bool producedIsTemp = false;

            if (ext == ".mp3")
            {
                audioFormat = 301; extFourCc = 0x4D503320; kindString = "MPEG audio file"; targetExt = ".mp3";
            }
            else if (ext is ".m4a" or ".m4b" or ".aac")
            {
                audioFormat = 502; extFourCc = 0x4D344120; kindString = "MPEG-4 audio file"; targetExt = ".m4a";
            }
            else   // FLAC/WAV/OGG/etc → ALAC (lossless), same as the music path
            {
                audioFormat = 502; extFourCc = 0x4D344120; kindString = "Apple Lossless audio file"; targetExt = ".m4a";
                produced = Path.Combine(Path.GetTempPath(), "orgz_pcast_" + Guid.NewGuid().ToString("N")[..8] + ".m4a");
                producedIsTemp = true;
                await TranscodeToAlacAsync(ffmpegPath, ep.LocalFile, produced, 44100, 0, null, ct);
            }

            try
            {
                var destFile = UniqueTrackPath(destDir, targetExt);
                var fileName = Path.GetFileName(destFile);
                File.Copy(produced, destFile);

                long fileSize = new FileInfo(destFile).Length;
                int lengthMs = ep.LengthMs;
                int sampleRate = 44100;
                int channels = 2;
                try
                {
                    using var of = TagLib.File.Create(destFile);
                    if (lengthMs == 0) { lengthMs = (int)of.Properties.Duration.TotalMilliseconds; }
                    if (of.Properties.AudioSampleRate > 0) { sampleRate = of.Properties.AudioSampleRate; }
                    if (of.Properties.AudioChannels > 0) { channels = of.Properties.AudioChannels; }
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Could not read audio properties of {Dest}", destFile);
                }
                int bitrate = lengthMs > 0 ? (int)(fileSize * 8L / lengthMs) : 192;

                long pid = writer.AddPodcastEpisode(new Nano5gLibraryWriter.PodcastInsert(
                    Title: ep.Title,
                    ShowName: ep.Show,
                    Description: ep.Description,
                    FeedUrl: ep.RssUrl,
                    ExternalGuid: null,
                    ReleasedUtc: ep.PubDateUtc,
                    DurationMs: lengthMs,
                    AudioFormat: audioFormat,
                    BitRate: bitrate,
                    SampleRate: sampleRate,
                    Channels: channels,
                    FileSize: fileSize,
                    LocationRelative: $"{folder}/{fileName}",
                    ExtensionFourCc: extFourCc,
                    KindString: kindString));

                // Episode artwork: prefer the show/episode cover from the feed (podcasts often carry no
                // embedded art), falling back to the audio file's embedded cover. Either way it's
                // rendered into the ArtworkDB + linked to the item, exactly the way the music path does.
                var artSource = !string.IsNullOrEmpty(ep.CoverImagePath) && File.Exists(ep.CoverImagePath)
                    ? ep.CoverImagePath
                    : ep.LocalFile;
                var (hasArt, _, imageId) = await TryWriteArtworkAsync(
                    mountPath, artSource, ffmpegPath, (ulong)pid, IPodCapabilities.CoverFormatsFor("Nano 5G"), ct);
                if (hasArt)
                {
                    writer.SetArtwork(pid, imageId);
                }
                added++;
            }
            finally
            {
                if (producedIsTemp)
                {
                    try { File.Delete(produced); } catch { /* best-effort temp cleanup */ }
                }
            }
        }

        _log.Information("Nano 5G: added {Count} podcast episode(s)", added);
        return added;
    }

    /// <summary>
    /// Nano 5G import path: the device reads the "iTunes Library.itlp" SQLite stack, not the binary
    /// iTunesDB. We ensure an MP3 (the format proven on-device; the Nano 5G's own library is MP3),
    /// copy it under iPod_Control/Music, then insert the row via <see cref="Nano5gLibraryWriter"/>,
    /// which re-signs Locations.itdb.cbk. ALAC/AAC-native playback and artwork are follow-ups.
    /// </summary>
    private static async Task<IPodImportResult> ImportToNano5gAsync(
        string mountPath, string sourceFile, string ffmpegPath, string? generation, string? fireWireGuid,
        string? title, string? artist, string? album, string? genre,
        int year, int trackNo, int srcLengthMs, int srcSampleRate, bool isAudiobook,
        Action<string, double>? onProgress, CancellationToken ct)
    {
        // The Nano 5G plays MP3 and MP4-container AAC/ALAC. Pass those through; transcode anything
        // else (FLAC/WAV/AIFF) to ALAC so it stays lossless - never down to MP3.
        var ext = Path.GetExtension(sourceFile).ToLowerInvariant();
        bool passthrough = ext is ".mp3" or ".m4a" or ".m4b" or ".aac";

        int audioFormat, extFourCc;
        string kindString, targetExt;
        string produced = sourceFile;
        bool producedIsTemp = false;
        int recordedSampleRate = srcSampleRate > 0 ? srcSampleRate : 44100;

        if (ext == ".mp3")
        {
            audioFormat = 301; extFourCc = 0x4D503320; kindString = "MPEG audio file"; targetExt = ".mp3";
        }
        else if (passthrough)   // .m4a/.m4b/.aac - already an MP4-container codec the iPod plays
        {
            audioFormat = 502; extFourCc = 0x4D344120; kindString = "MPEG-4 audio file"; targetExt = ".m4a";
        }
        else                     // FLAC/WAV/AIFF/etc → ALAC (lossless)
        {
            audioFormat = 502; extFourCc = 0x4D344120; kindString = "Apple Lossless audio file"; targetExt = ".m4a";
            recordedSampleRate = TargetSampleRate(srcSampleRate);
            produced = Path.Combine(Path.GetTempPath(), "orgz_alac_" + Guid.NewGuid().ToString("N")[..8] + ".m4a");
            producedIsTemp = true;
            var report = onProgress;
            await TranscodeToAlacAsync(ffmpegPath, sourceFile, produced, recordedSampleRate, srcLengthMs, report == null ? null : f => report("transcode", f), ct);
        }

        try
        {
            const string folder = "F00";
            var destDir = Path.Combine(mountPath, "iPod_Control", "Music", folder);
            Directory.CreateDirectory(destDir);
            var destFile = UniqueTrackPath(destDir, targetExt);
            var fileName = Path.GetFileName(destFile);
            var copyReport = onProgress;
            await CopyFileWithProgressAsync(produced, destFile, copyReport == null ? null : f => copyReport("copy", f), ct);

            long fileSize = new FileInfo(destFile).Length;
            int lengthMs = srcLengthMs;
            if (lengthMs == 0)
            {
                try
                {
                    using var of = TagLib.File.Create(destFile);
                    lengthMs = (int)of.Properties.Duration.TotalMilliseconds;
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Could not read duration of {Dest}", destFile);
                }
            }
            int bitrate = lengthMs > 0 ? (int)(fileSize * 8L / lengthMs) : 256;

            var itlp = Path.Combine(mountPath, "iPod_Control", "iTunes", "iTunes Library.itlp");
            var writer = new Nano5gLibraryWriter(itlp, fireWireGuid);
            long pid = writer.AddTrack(new Nano5gLibraryWriter.TrackInsert(
                Title: title ?? Path.GetFileNameWithoutExtension(sourceFile),
                Artist: artist ?? "Unknown Artist",
                Album: album ?? "Unknown Album",
                AlbumArtist: null,
                Genre: genre,
                DurationMs: lengthMs,
                TrackNumber: trackNo,
                DiscNumber: 0,
                Year: year,
                AudioFormat: audioFormat,
                BitRate: bitrate,
                SampleRate: recordedSampleRate,
                Channels: 2,
                FileSize: fileSize,
                LocationRelative: $"{folder}/{fileName}",
                ExtensionFourCc: extFourCc,
                KindString: kindString,
                IsAudiobook: isAudiobook));

            // Album art: same ArtworkDB/.ithmb subsystem as the binary path; link it from SQLite via
            // artwork_cache_id -> ArtworkDB image id (Library.itdb isn't checksummed, so no cbk re-sign).
            var (hasArt, _, imageId) = await TryWriteArtworkAsync(
                mountPath, sourceFile, ffmpegPath, (ulong)pid, IPodCapabilities.CoverFormatsFor(generation), ct);
            if (hasArt)
            {
                writer.SetArtwork(pid, imageId);
            }

            _log.Information("Nano 5G: added '{Title}' (pid={Pid}, art={Art}) -> {Dest}", title, pid, hasArt, destFile);
            return new IPodImportResult((uint)(pid & 0xFFFFFFFF), $":iPod_Control:Music:{folder}:{fileName}", destFile, title, (ulong)pid);
        }
        finally
        {
            if (producedIsTemp)
            {
                try { File.Delete(produced); } catch { /* best-effort temp cleanup */ }
            }
        }
    }

    internal static async Task TranscodeToAlacAsync(string ffmpegPath, string input, string output, int sampleRate, int durationMs, Action<double>? onProgress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(input);
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:a:0");          // first audio stream only
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("alac");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add(sampleRate.ToString());   // cap to a rate the iPod can decode
        psi.ArgumentList.Add("-sample_fmt");
        psi.ArgumentList.Add("s16p");                  // 16-bit (5.5G ALAC ceiling)
        psi.ArgumentList.Add("-map_metadata");
        psi.ArgumentList.Add("0");
        // moov before mdat, like every iTunes-written file (see TranscodeToAacAsync).
        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add("+faststart");

        _log.Information("Transcoding to ALAC: {Input} -> {Output}", input, output);
        await RunFfmpegAsync(psi, output, durationMs, onProgress, ct);
    }

    /// <summary>AAC 256 kbps in an .m4a wrapper - the lossy fallback for the devices that can't
    /// decode ALAC (iPod 1G/2G, Shuffle 1G/2G). Everything else gets <see cref="TranscodeToAlacAsync"/>.</summary>
    internal static async Task TranscodeToAacAsync(string ffmpegPath, string input, string output, int sampleRate, int durationMs, Action<double>? onProgress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(input);
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:a:0");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add("256k");
        // Vintage fixed-function decoders (Shuffle 2G's SigmaTel-era SoC) choke on encoder features
        // Apple's own AAC encoder never emits - disable PNS and intensity stereo so the stream looks
        // like what the firmware grew up on.
        psi.ArgumentList.Add("-aac_pns");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-aac_is");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add(sampleRate.ToString());
        psi.ArgumentList.Add("-map_metadata");
        psi.ArgumentList.Add("0");
        // moov before mdat, like every iTunes-written file - hardware players stream the container
        // front-to-back and stutter (or worse) when the sample tables live at the end.
        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add("+faststart");

        _log.Information("Transcoding to AAC: {Input} -> {Output}", input, output);
        await RunFfmpegAsync(psi, output, durationMs, onProgress, ct);
    }

    /// <summary>
    /// Runs ffmpeg with the output path appended, reporting real progress when asked: ffmpeg's
    /// <c>-progress pipe:1</c> stream carries <c>out_time_us</c> lines, and fraction = out_time over
    /// the source duration. (ffmpeg quirk: the <c>out_time_ms</c> key is ALSO microseconds.) Stdout
    /// is always drained so the pipe can't fill and deadlock the encode.
    /// </summary>
    private static async Task RunFfmpegAsync(ProcessStartInfo psi, string output, int durationMs, Action<double>? onProgress, CancellationToken ct)
    {
        psi.ArgumentList.Add("-nostats");
        psi.ArgumentList.Add("-progress");
        psi.ArgumentList.Add("pipe:1");
        psi.ArgumentList.Add(output);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        try
        {
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct)) != null)
            {
                if (onProgress == null || durationMs <= 0)
                {
                    continue;   // draining only
                }
                if ((line.StartsWith("out_time_us=", StringComparison.Ordinal) || line.StartsWith("out_time_ms=", StringComparison.Ordinal))
                    && long.TryParse(line.AsSpan(line.IndexOf('=') + 1), out var us) && us > 0)
                {
                    onProgress(Math.Clamp(us / 1000.0 / durationMs, 0, 1));
                }
            }

            var stderr = await stderrTask;
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
            {
                var tail = stderr.Length > 800 ? stderr[^800..] : stderr;
                throw new InvalidOperationException($"ffmpeg exited {proc.ExitCode}: {tail}");
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled mid-encode: kill ffmpeg and remove its partial output.
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { File.Delete(output); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Chunked file copy with byte-accurate progress - the device write is the slow phase on
    /// USB-1.1-era iPods, and File.Copy gives no feedback at all. A cancelled copy deletes its torn
    /// destination before rethrowing: no half-files left on the device.</summary>
    internal static async Task CopyFileWithProgressAsync(string source, string dest, Action<double>? onProgress, CancellationToken ct)
    {
        try
        {
            const int BufSize = 1 << 20;
            await using var src = File.OpenRead(source);
            await using var dst = File.Create(dest);
            var buf = new byte[BufSize];
            long total = src.Length, done = 0;
            int read;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                {
                    onProgress?.Invoke((double)done / total);
                }
            }
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(dest); } catch { /* best effort - the stream is closed by now */ }
            throw;
        }
    }

    /// <summary>
    /// Shared commit tail for every binary-iTunesDB mutation: normalizes + serializes
    /// <paramref name="doc"/>, applies the generation's integrity checksum (hash58 is keyed off the
    /// device FireWireGuid - without it Classic/Nano 3G+ show "0 songs"), re-parses the result and
    /// checks it against <paramref name="verify"/> (when given), backs the original up once
    /// (.orgzbak), and swaps the new bytes in atomically via a temp file. Returns the committed
    /// bytes so callers can log the size. Throws before any write when verification fails.
    /// </summary>
    internal static byte[] CommitDb(ITunesDbDocument doc, string dbPath, string mountPath, string? generation, string? fireWireGuid,
        Func<List<ITunesDbReader.ITunesTrack>, List<ITunesDbReader.ITunesPlaylist>, bool>? verify = null, string? failureMessage = null)
    {
        ITunesDbChunkTree.Normalize(doc.Root);
        var outBytes = ITunesDbChunkTree.Serialize(doc);

        if (IPodCapabilities.ChecksumFor(generation) == IPodChecksum.Hash58)
        {
            ITunesDbHash58.Apply(outBytes, fireWireGuid);
            _log.Information("Applied hash58 checksum (FireWireGuid keyed)");
        }

        // Sanity: the exact bytes we're about to commit must re-parse and pass the caller's check.
        if (verify is not null)
        {
            ITunesDbReader.ReadAll(outBytes, mountPath, out var tracks, out var playlists);
            if (!verify(tracks, playlists))
            {
                throw new InvalidDataException(failureMessage ?? "Re-parse of the new iTunesDB failed verification; aborting write.");
            }
        }

        var backup = dbPath + ".orgzbak";
        var hadBackup = File.Exists(backup);
        AtomicFile.WriteAllBytes(dbPath, outBytes, backup: backup);
        if (!hadBackup && File.Exists(backup))
        {
            _log.Information("Backed up original iTunesDB to {Backup}", backup);
        }
        return outBytes;
    }

    private static string RandomTrackName()
    {
        // iTunes-style 4 uppercase letters.
        Span<char> name = stackalloc char[4];
        for (int i = 0; i < name.Length; i++)
        {
            name[i] = (char)('A' + Random.Shared.Next(26));
        }
        return new string(name);
    }

    /// <summary>
    /// A destination path in <paramref name="destDir"/> proven not to collide with an existing
    /// file. RandomTrackName draws from only 26^4 names, so on a well-stocked device a plain
    /// File.Copy(overwrite:true) would eventually clobber another track's audio while the
    /// database still pointed at it - silent, unrecoverable track loss. We spin a fresh name
    /// until the slot is free; callers then copy WITHOUT overwrite.
    /// </summary>
    internal static string UniqueTrackPath(string destDir, string ext)
    {
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            var candidate = Path.Combine(destDir, RandomTrackName() + ext);
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
        // 1000 straight collisions is astronomically unlikely; widen the namespace and move on.
        return Path.Combine(destDir, RandomTrackName() + "_" + Guid.NewGuid().ToString("N")[..6] + ext);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
