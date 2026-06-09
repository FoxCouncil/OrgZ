// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using Serilog;

namespace OrgZ.Services;

/// <summary>Result of importing one track onto a device.</summary>
public sealed record IPodImportResult(uint TrackId, string IpodPath, string DestFile, string? Title);

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

    public static async Task<IPodImportResult> ImportAsync(
        string mountPath,
        string sourceFile,
        string ffmpegPath,
        string? generation = null,
        string? fireWireGuid = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(sourceFile))
        {
            throw new FileNotFoundException("Source track not found.", sourceFile);
        }

        // Refuse generations whose iTunesDB we can't safely write (they need a
        // checksum we don't generate) — better to decline than brick the library view.
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
        string? title = null, artist = null, album = null, genre = null;
        int year = 0, trackNo = 0, srcLengthMs = 0, srcSampleRate = 0;
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
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Tag read failed for {Source}; importing with filename only", sourceFile);
            title ??= Path.GetFileNameWithoutExtension(sourceFile);
        }

        // --- produce an iPod-compatible file ---
        var ext = Path.GetExtension(sourceFile);
        bool compatible = CompatibleExtensions.Contains(ext);
        string targetExt = compatible ? ext.ToLowerInvariant() : ".m4a";

        // Stock iPods (5G/5.5G) decode ALAC only up to 48 kHz / 16-bit — hi-res
        // (e.g. 96/24) won't play and also overflows the iTunesDB sample-rate field
        // (stored as rate << 16, so anything > 65535 corrupts). Target CD resolution:
        // keep the source rate when it's already <= 48 kHz, otherwise resample to
        // 44.1 kHz, and force 16-bit. That's the lossless ceiling the hardware supports.
        int targetSampleRate = srcSampleRate is > 0 and <= 48000 ? srcSampleRate : 44100;

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
            await TranscodeToAlacAsync(ffmpegPath, sourceFile, producedFile, targetSampleRate, ct);
        }

        try
        {
            // --- copy onto the device ---
            const string folder = "F00";
            var destDir = Path.Combine(mountPath, "iPod_Control", "Music", folder);
            Directory.CreateDirectory(destDir);

            string fileName = RandomTrackName() + targetExt;
            var destFile = Path.Combine(destDir, fileName);
            File.Copy(producedFile, destFile, overwrite: true);
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
            // Average bitrate (kbps) from the encoded size — meaningful for VBR/lossless,
            // where a single nominal bitrate doesn't exist.
            int bitrate = lengthMs > 0 ? (int)(fileSize * 8L / lengthMs) : 0;

            // --- album art (best-effort): cover -> RGB565 .ithmb + ArtworkDB ---
            // The track's dbid ties the iTunesDB MHIT to the ArtworkDB mhii.
            ulong dbid = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
            var coverFormats = IPodCapabilities.CoverFormatsFor(generation);
            var (hasArt, artSize) = await TryWriteArtworkAsync(mountPath, sourceFile, ffmpegPath, dbid, coverFormats, ct);

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
            });

            ITunesDbChunkTree.Normalize(doc.Root);
            var outBytes = ITunesDbChunkTree.Serialize(doc);

            // Apply the generation's integrity checksum (in place) before commit.
            // hash58 keys off the device FireWireGuid; without it Classic/Nano 3G+
            // show "0 songs".
            if (IPodCapabilities.ChecksumFor(generation) == IPodChecksum.Hash58)
            {
                ITunesDbHash58.Apply(outBytes, fireWireGuid);
                _log.Information("Applied hash58 checksum (FireWireGuid keyed)");
            }

            // Sanity: the bytes we're about to commit must re-parse and contain the track.
            ITunesDbReader.ReadAll(WriteToTemp(outBytes, out var verifyPath), mountPath, out var vt, out _);
            try { File.Delete(verifyPath); } catch { }
            if (vt.All(t => t.TrackId != trackId))
            {
                throw new InvalidDataException("Re-parse of the new iTunesDB did not contain the added track; aborting write.");
            }

            var backup = dbPath + ".orgzbak";
            if (!File.Exists(backup))
            {
                File.Copy(dbPath, backup);
                _log.Information("Backed up original iTunesDB to {Backup}", backup);
            }

            var tmp = dbPath + ".orgztmp";
            File.WriteAllBytes(tmp, outBytes);
            File.Move(tmp, dbPath, overwrite: true);

            _log.Information("Imported '{Title}' as track {Id} -> {IpodPath} ({Bytes} byte DB)", title, trackId, ipodPath, outBytes.Length);
            return new IPodImportResult(trackId, ipodPath, destFile, title);
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
    /// Extracts the source's embedded cover, renders each thumbnail size in
    /// <paramref name="coverFormats"/> as raw RGB565-LE into
    /// iPod_Control/Artwork/F{id}_1.ithmb, and writes an ArtworkDB linking
    /// <paramref name="dbid"/> to them. Returns (false, 0) and writes nothing when
    /// the generation has no validated art formats or the source has no cover art.
    /// </summary>
    private static async Task<(bool ok, int artworkSize)> TryWriteArtworkAsync(
        string mountPath, string sourceFile, string ffmpegPath, ulong dbid,
        IReadOnlyList<(int FormatId, int Width, int Height)> coverFormats, CancellationToken ct)
    {
        if (coverFormats.Count == 0)
        {
            return (false, 0);   // generation without validated artwork formats — import without art
        }

        try
        {
            var artDir = Path.Combine(mountPath, "iPod_Control", "Artwork");
            Directory.CreateDirectory(artDir);

            var thumbs = new List<ArtThumb>();
            int totalSize = 0;
            foreach (var (formatId, w, h) in coverFormats)
            {
                var ithmb = Path.Combine(artDir, $"F{formatId}_1.ithmb");
                if (!await ExtractRgb565Async(ffmpegPath, sourceFile, w, h, ithmb, ct))
                {
                    return (false, 0);   // no cover stream / ffmpeg failed
                }
                int expected = w * h * 2;
                if (new FileInfo(ithmb).Length != expected)
                {
                    _log.Warning("Thumbnail {File} is {Actual}B, expected {Expected}B", ithmb, new FileInfo(ithmb).Length, expected);
                    return (false, 0);
                }
                thumbs.Add(new ArtThumb(formatId, w, h, IthmbOffset: 0, ImageSize: expected));
                totalSize += expected;
            }

            var doc = ArtworkDbWriter.Build(dbid, imageId: 100, thumbs, origImgSize: totalSize);
            ITunesDbChunkTree.Normalize(doc.Root);
            var bytes = ITunesDbChunkTree.Serialize(doc);
            ITunesDbChunkTree.Parse(bytes);   // sanity: must re-parse

            var dbPath = Path.Combine(artDir, "ArtworkDB");
            if (File.Exists(dbPath) && !File.Exists(dbPath + ".orgzbak"))
            {
                File.Copy(dbPath, dbPath + ".orgzbak");
            }
            var tmp = dbPath + ".orgztmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, dbPath, overwrite: true);

            _log.Information("Wrote ArtworkDB + {Count} thumbnails ({Bytes}B)", thumbs.Count, totalSize);
            return (true, totalSize);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Artwork generation failed; importing without art");
            return (false, 0);
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

    private static async Task TranscodeToAlacAsync(string ffmpegPath, string input, string output, int sampleRate, CancellationToken ct)
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
        psi.ArgumentList.Add(output);

        _log.Information("Transcoding to ALAC: {Input} -> {Output}", input, output);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            var tail = stderr.Length > 800 ? stderr[^800..] : stderr;
            throw new InvalidOperationException($"ffmpeg exited {proc.ExitCode}: {tail}");
        }
    }

    private static string WriteToTemp(byte[] bytes, out string path)
    {
        path = Path.Combine(Path.GetTempPath(), "orgz_itdb_verify_" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllBytes(path, bytes);
        return path;
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

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
