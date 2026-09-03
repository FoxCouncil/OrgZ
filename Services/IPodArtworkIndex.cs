// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrgZ.Services;

/// <summary>
/// Remembers, on the device, which cover pictures are already stored and where their thumbnails
/// live: <c>/.orgz/artwork-index.json</c>, keyed by the picture's content hash.
///
/// Within one sync the write batch already shares a cover between the tracks that use it. Without
/// this file the next sync started from nothing and wrote every album's cover once more; with it a
/// cover that is on the device is reused across every sync. The file is a cache: every entry is
/// checked against the real thumbnail files when loaded, and anything that no longer lines up is
/// dropped, so a stale or missing index only costs a re-render, never a wrong picture.
/// </summary>
public static class IPodArtworkIndex
{
    private const string FileName = "artwork-index.json";
    private static readonly Serilog.ILogger _log = Logging.For("IPodArtworkIndex");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public sealed class Document
    {
        /// <summary>Cover content hash -> its thumbnails on the device.</summary>
        public Dictionary<string, List<Slot>> Covers { get; set; } = new(StringComparer.Ordinal);

        /// <summary>How many ArtworkDB entries there were when the device was last de-duplicated. Lets the
        /// "stored once per track" check tell a library of singles apart from one that never ran it.</summary>
        public int? CompactedEntries { get; set; }
    }

    public sealed record Slot(int Format, int File, int Offset, int Size, int Width, int Height)
    {
        public ArtThumb ToThumb() => new(Format, Width, Height, Offset, Size, File);
        public static Slot From(ArtThumb t) => new(t.FormatId, t.FileIndex, t.IthmbOffset, t.ImageSize, t.Width, t.Height);
    }

    public static string PathFor(string mountPath) => Path.Combine(mountPath, ".orgz", FileName);

    /// <summary>The index, with every entry that no longer points inside an existing thumbnail file removed.</summary>
    public static Document Load(string mountPath)
    {
        var path = PathFor(mountPath);
        if (!File.Exists(path))
        {
            return new Document();
        }

        Document doc;
        try
        {
            doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Json) ?? new Document();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Artwork index at {Path} unreadable; starting empty", path);
            return new Document();
        }

        var artDir = Path.Combine(mountPath, "iPod_Control", "Artwork");
        var lengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long LengthOf(string fileName)
        {
            if (!lengths.TryGetValue(fileName, out var len))
            {
                var p = Path.Combine(artDir, fileName);
                len = File.Exists(p) ? new FileInfo(p).Length : -1;
                lengths[fileName] = len;
            }
            return len;
        }

        var valid = new Dictionary<string, List<Slot>>(StringComparer.Ordinal);
        foreach (var (hash, slots) in doc.Covers)
        {
            if (slots.Count > 0 && slots.All(s => s.Size > 0 && s.Offset >= 0 && (long)s.Offset + s.Size <= LengthOf(IPodArtworkFiles.FileName(s.Format, s.File))))
            {
                valid[hash] = slots;
            }
        }
        if (valid.Count != doc.Covers.Count)
        {
            _log.Information("Artwork index on {Mount}: dropped {Dropped} entr(ies) that no longer match the thumbnail files", mountPath, doc.Covers.Count - valid.Count);
        }
        doc.Covers = valid;
        return doc;
    }

    public static void Save(string mountPath, Document doc)
    {
        try
        {
            var path = PathFor(mountPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Helpers.AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(doc, Json));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not save the artwork index on {Mount}", mountPath);
        }
    }

    /// <summary>The device's covers as the batch wants them: hash -> thumbnails.</summary>
    public static Dictionary<string, IReadOnlyList<ArtThumb>> LoadCovers(string mountPath)
        => Load(mountPath).Covers.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ArtThumb>)kv.Value.Select(s => s.ToThumb()).ToList(), StringComparer.Ordinal);

    /// <summary>Adds (or replaces) the covers a sync wrote and saves. Existing entries stay.</summary>
    public static void Merge(string mountPath, IReadOnlyDictionary<string, IReadOnlyList<ArtThumb>> covers)
    {
        if (covers.Count == 0)
        {
            return;
        }
        var doc = Load(mountPath);
        foreach (var (hash, thumbs) in covers)
        {
            doc.Covers[hash] = thumbs.Select(Slot.From).ToList();
        }
        Save(mountPath, doc);
    }

    /// <summary>
    /// After a compaction moved thumbnails, points every remembered cover at its new place and records
    /// the entry count. <paramref name="moved"/> maps (old file, old offset) -> new thumbnail; a cover
    /// whose thumbnails weren't all moved is forgotten (it will simply be re-rendered next time).
    /// </summary>
    public static void Remap(string mountPath, IReadOnlyDictionary<(string File, int Offset), ArtThumb> moved, int compactedEntries)
    {
        var doc = Load(mountPath);
        var remapped = new Dictionary<string, List<Slot>>(StringComparer.Ordinal);
        foreach (var (hash, slots) in doc.Covers)
        {
            var next = new List<Slot>(slots.Count);
            foreach (var slot in slots)
            {
                if (!moved.TryGetValue((IPodArtworkFiles.FileName(slot.Format, slot.File), slot.Offset), out var target))
                {
                    next = null;
                    break;
                }
                next.Add(Slot.From(target));
            }
            if (next is not null)
            {
                remapped[hash] = next;
            }
        }
        doc.Covers = remapped;
        doc.CompactedEntries = compactedEntries;
        Save(mountPath, doc);
    }
}
