// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Security.Cryptography;

namespace OrgZ.Services;

/// <summary>
/// Removes duplicate cover art from a device.
///
/// OrgZ used to write a full set of thumbnails for every track, so an album's twelve songs each
/// stored their own copy of the same picture - on a Classic that is 276 KB per track, which reached
/// about 8 GB on a 29k-track library and left the firmware crawling a hugely padded ArtworkDB
/// before it could show a cover. Imports now share one copy per cover, but a device filled by an
/// older build keeps its duplicates until this runs.
///
/// The work is: read every thumbnail, group the ones with identical bytes, write new .ithmb files
/// holding one copy of each distinct cover, and rebuild the ArtworkDB so every track points at its
/// shared copy. Tracks keep their artwork - the same pictures, stored once.
/// </summary>
public static class IPodArtworkCompactor
{
    private static readonly Serilog.ILogger _log = Logging.For("IPodArtworkCompactor");

    /// <param name="Images">Artwork entries in the database (one per track that has art).</param>
    /// <param name="DistinctCovers">How many genuinely different pictures those entries share.</param>
    public sealed record Result(int Images, int DistinctCovers, long BytesBefore, long BytesAfter)
    {
        public long BytesSaved => Math.Max(0, BytesBefore - BytesAfter);
        public bool ChangedAnything => DistinctCovers < Images;
    }

    /// <summary>
    /// Identity of one artwork entry: the bytes of all its thumbnails, in format order. Two entries
    /// with the same key are the same picture at every size, so one copy serves both.
    /// </summary>
    internal static string CoverKey(IReadOnlyList<(int FormatId, byte[] Bytes)> thumbs)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (formatId, bytes) in thumbs.OrderBy(t => t.FormatId))
        {
            hash.AppendData(BitConverter.GetBytes(formatId));
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    /// Decides the new layout without touching the disk: walks the entries in order and, for each
    /// distinct cover, assigns the offsets its thumbnails will occupy in the rebuilt .ithmb files.
    /// Entries sharing a cover get the same offsets. Pure, so the packing is testable on its own.
    /// </summary>
    internal static List<ArtImage> PlanLayout(IReadOnlyList<(ArtImage Image, string CoverKey)> entries)
    {
        var thumbsByCover = new Dictionary<string, IReadOnlyList<ArtThumb>>(StringComparer.Ordinal);
        var nextOffset = new Dictionary<int, long>();   // format id -> bytes already placed
        var planned = new List<ArtImage>(entries.Count);

        foreach (var (image, coverKey) in entries)
        {
            if (!thumbsByCover.TryGetValue(coverKey, out var shared))
            {
                var placed = new List<ArtThumb>(image.Thumbs.Count);
                foreach (var thumb in image.Thumbs)
                {
                    long offset = nextOffset.GetValueOrDefault(thumb.FormatId);
                    placed.Add(thumb with { IthmbOffset = (int)offset });
                    nextOffset[thumb.FormatId] = offset + thumb.ImageSize;
                }
                shared = placed;
                thumbsByCover[coverKey] = shared;
            }

            planned.Add(image with { Thumbs = shared });
        }

        return planned;
    }

    /// <summary>
    /// Rebuilds a device's artwork storage with one copy of each distinct cover. Reports progress as
    /// (stage, 0..1). Returns what it found; nothing is written when there are no duplicates.
    /// </summary>
    public static async Task<Result> CompactAsync(string mountPath, Action<string, double>? onProgress = null, CancellationToken ct = default)
    {
        var artDir = Path.Combine(mountPath, "iPod_Control", "Artwork");
        var dbPath = Path.Combine(artDir, "ArtworkDB");
        if (!File.Exists(dbPath))
        {
            return new Result(0, 0, 0, 0);
        }

        var images = ArtworkDbWriter.ReadImages(ITunesDbChunkTree.Parse(await File.ReadAllBytesAsync(dbPath, ct)));
        if (images.Count == 0)
        {
            return new Result(0, 0, 0, 0);
        }

        long bytesBefore = images.SelectMany(i => i.Thumbs).Sum(t => (long)t.ImageSize);

        // ── Pass 1: read every thumbnail and work out which entries share a picture. ──
        var readers = new Dictionary<int, FileStream>();
        var entries = new List<(ArtImage Image, string CoverKey)>(images.Count);
        try
        {
            for (int i = 0; i < images.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                onProgress?.Invoke("Reading artwork", (double)i / images.Count);

                var thumbBytes = new List<(int FormatId, byte[] Bytes)>(images[i].Thumbs.Count);
                bool readable = true;
                foreach (var thumb in images[i].Thumbs)
                {
                    if (!readers.TryGetValue(thumb.FormatId, out var reader))
                    {
                        var path = Path.Combine(artDir, $"F{thumb.FormatId}_1.ithmb");
                        if (!File.Exists(path))
                        {
                            readable = false;
                            break;
                        }
                        reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                        readers[thumb.FormatId] = reader;
                    }

                    if (thumb.IthmbOffset < 0 || thumb.IthmbOffset + thumb.ImageSize > reader.Length)
                    {
                        readable = false;   // entry points outside its file - leave this one alone
                        break;
                    }

                    var buffer = new byte[thumb.ImageSize];
                    reader.Position = thumb.IthmbOffset;
                    await reader.ReadExactlyAsync(buffer, ct);
                    thumbBytes.Add((thumb.FormatId, buffer));
                }

                if (!readable)
                {
                    _log.Warning("Artwork entry {ImageId} could not be read; skipping it", images[i].ImageId);
                    continue;
                }

                entries.Add((images[i], CoverKey(thumbBytes)));
            }
        }
        finally
        {
            foreach (var reader in readers.Values)
            {
                await reader.DisposeAsync();
            }
        }

        var planned = PlanLayout(entries);
        int distinct = entries.Select(e => e.CoverKey).Distinct(StringComparer.Ordinal).Count();
        long bytesAfter = planned
            .SelectMany(p => p.Thumbs)
            .DistinctBy(t => (t.FormatId, t.IthmbOffset))
            .Sum(t => (long)t.ImageSize);

        var result = new Result(entries.Count, distinct, bytesBefore, bytesAfter);
        if (!result.ChangedAnything)
        {
            _log.Information("Artwork on {Mount} is already stored once per cover ({Count} entries)", mountPath, entries.Count);
            return result;
        }

        // ── Pass 2: write the new files beside the old ones. Nothing is replaced until every
        // byte is on disk, so a failure (or an unplugged cable) leaves the device untouched. ──
        var staged = new Dictionary<int, string>();
        var sources = new Dictionary<int, FileStream>();
        try
        {
            var written = new HashSet<(int FormatId, int Offset)>();
            for (int i = 0; i < planned.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                onProgress?.Invoke("Rewriting artwork", (double)i / planned.Count);

                // planned[i] is entries[i] with its thumbnails moved to shared offsets, so the two
                // line up index for index and thumbnail for thumbnail.
                var original = entries[i].Image;
                for (int t = 0; t < planned[i].Thumbs.Count; t++)
                {
                    var target = planned[i].Thumbs[t];
                    if (!written.Add((target.FormatId, target.IthmbOffset)))
                    {
                        continue;   // this cover's bytes are already in the new file
                    }

                    var source = original.Thumbs[t];
                    if (!sources.TryGetValue(source.FormatId, out var reader))
                    {
                        reader = new FileStream(Path.Combine(artDir, $"F{source.FormatId}_1.ithmb"), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                        sources[source.FormatId] = reader;
                    }

                    if (!staged.TryGetValue(target.FormatId, out var stagedPath))
                    {
                        stagedPath = Path.Combine(artDir, $"F{target.FormatId}_1.ithmb.orgznew");
                        staged[target.FormatId] = stagedPath;
                        File.Delete(stagedPath);
                    }

                    var buffer = new byte[source.ImageSize];
                    reader.Position = source.IthmbOffset;
                    await reader.ReadExactlyAsync(buffer, ct);

                    await using var writer = new FileStream(stagedPath, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 16);
                    if (writer.Length != target.IthmbOffset)
                    {
                        throw new InvalidOperationException(
                            $"Artwork rebuild is out of step for format {target.FormatId}: file is at {writer.Length}, entry expects {target.IthmbOffset}.");
                    }
                    await writer.WriteAsync(buffer, ct);
                }
            }
        }
        catch
        {
            foreach (var path in staged.Values)
            {
                try { File.Delete(path); } catch { /* best-effort cleanup */ }
            }
            throw;
        }
        finally
        {
            foreach (var reader in sources.Values)
            {
                await reader.DisposeAsync();
            }
        }

        // ── Pass 3: the database, then the swap. ──
        onProgress?.Invoke("Saving", 0.99);
        var doc = ArtworkDbWriter.BuildFromImages(planned);
        ITunesDbChunkTree.Normalize(doc.Root);
        var dbBytes = ITunesDbChunkTree.Serialize(doc);
        ITunesDbChunkTree.Parse(dbBytes);   // must read back before it goes anywhere near the device

        var dbBackup = dbPath + ".pre-artwork-compact";
        if (!File.Exists(dbBackup))
        {
            File.Copy(dbPath, dbBackup);
        }

        foreach (var (formatId, stagedPath) in staged)
        {
            var live = Path.Combine(artDir, $"F{formatId}_1.ithmb");
            File.Move(stagedPath, live, overwrite: true);
        }
        AtomicFile.WriteAllBytes(dbPath, dbBytes, backup: null);

        _log.Information("Compacted artwork on {Mount}: {Images} entries share {Distinct} cover(s), {Before} -> {After} bytes",
            mountPath, result.Images, result.DistinctCovers, result.BytesBefore, result.BytesAfter);
        return result;
    }
}
