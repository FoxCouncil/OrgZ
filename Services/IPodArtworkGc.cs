// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Reclaims a removed track's artwork: drops its mhii entry from the ArtworkDB and
/// compacts the .ithmb pixel files so the bytes actually come back. Without this the
/// Artwork folder grew monotonically for the life of the device - every removed track
/// left its RGB565 thumbnails behind forever.
/// </summary>
public static class IPodArtworkGc
{
    private static readonly ILogger _log = Logging.For("IPodArtworkGc");

    /// <summary>
    /// Reads the dbid straight off a parsed iTunesDB track (mhit header 0x70), so the
    /// remove path can identify the artwork entry before deleting the row. Null when the
    /// track isn't present.
    /// </summary>
    public static ulong? DbidForTrack(ITunesDbDocument doc, uint trackId)
    {
        foreach (var mhsd in doc.Root.Children.Where(c => c.Magic == "mhsd"))
        {
            foreach (var mhit in mhsd.Children.Where(c => c.Magic == "mhit"))
            {
                if ((uint)mhit.ReadHeaderInt32(0x10) == trackId)
                {
                    return LittleEndian.ReadUInt64(mhit.Header, 0x70);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Drops the given dbids' entries from the ArtworkDB and rewrites each .ithmb with
    /// only the surviving thumbnails (offsets recomputed). No-op when the DB is absent or
    /// none of the dbids carry art; formats left with no thumbnails lose their .ithmb
    /// file entirely. Best-effort by design - an artwork GC failure must never break the
    /// track removal it rides on.
    /// </summary>
    public static void RemoveArt(string mountPath, IReadOnlyCollection<ulong> dbids)
    {
        try
        {
            var dbPath = IPodPaths.ArtworkDb(mountPath);
            if (dbids.Count == 0 || !File.Exists(dbPath))
            {
                return;
            }

            var images = ArtworkDbWriter.ReadImages(ITunesDbChunkTree.Parse(File.ReadAllBytes(dbPath)));
            var drop = new HashSet<ulong>(dbids);
            if (!images.Any(i => drop.Contains(i.Dbid)))
            {
                return;
            }

            var keep = images.Where(i => !drop.Contains(i.Dbid)).ToList();
            var compacted = CompactIthmb(mountPath, keep);

            var doc = ArtworkDbWriter.BuildFromImages(compacted);
            ITunesDbChunkTree.Normalize(doc.Root);
            var bytes = ITunesDbChunkTree.Serialize(doc);
            ITunesDbChunkTree.Parse(bytes);   // sanity: must re-parse before it may land
            Helpers.AtomicFile.WriteAllBytes(dbPath, bytes, backup: dbPath + ".orgzbak");

            IPodArtworkReader.Invalidate(mountPath);
            _log.Information("Artwork GC on {Mount}: removed {Removed} entr(ies), kept {Kept}", mountPath, images.Count - keep.Count, keep.Count);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Artwork GC failed for {Mount} - the track removal itself is unaffected", mountPath);
        }
    }

    /// <summary>
    /// Rewrites each referenced .ithmb keeping only the surviving thumbs, packed from
    /// offset 0 in their original on-disk order, and returns the images with their
    /// offsets updated to match. F*_1.ithmb files no surviving image references are
    /// deleted outright.
    /// </summary>
    private static List<ArtImage> CompactIthmb(string mountPath, List<ArtImage> keep)
    {
        var artworkDir = IPodPaths.Artwork(mountPath);
        var newThumbs = keep.Select(i => i.Thumbs.ToArray()).ToList();

        var byFormat = keep
            .SelectMany((img, imgIdx) => img.Thumbs.Select((t, thumbIdx) => (t, imgIdx, thumbIdx)))
            .GroupBy(x => x.t.FormatId);

        var referencedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in byFormat)
        {
            var path = Path.Combine(artworkDir, $"F{group.Key}_1.ithmb");
            referencedFiles.Add(Path.GetFileName(path));
            if (!File.Exists(path))
            {
                continue;   // nothing on disk to compact; the declared offsets stay
            }

            var tmp = path + ".orgztmp";
            using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                foreach (var (t, imgIdx, thumbIdx) in group.OrderBy(x => x.t.IthmbOffset))
                {
                    int newOffset = (int)dst.Position;
                    src.Position = t.IthmbOffset;
                    int remaining = t.ImageSize;
                    while (remaining > 0)
                    {
                        int got = src.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                        if (got <= 0)
                        {
                            throw new EndOfStreamException($"{Path.GetFileName(path)} ended {remaining} bytes early (offset {t.IthmbOffset}).");
                        }

                        dst.Write(buffer, 0, got);
                        remaining -= got;
                    }

                    newThumbs[imgIdx][thumbIdx] = t with { IthmbOffset = newOffset };
                }

                dst.Flush(flushToDisk: true);
            }

            File.Move(tmp, path, overwrite: true);
        }

        if (Directory.Exists(artworkDir))
        {
            foreach (var file in Directory.EnumerateFiles(artworkDir, "F*_1.ithmb"))
            {
                if (!referencedFiles.Contains(Path.GetFileName(file)))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        _log.Debug(ex, "Couldn't delete orphaned {File}", file);
                    }
                }
            }
        }

        return keep.Select((img, i) => img with { Thumbs = newThumbs[i] }).ToList();
    }
}
