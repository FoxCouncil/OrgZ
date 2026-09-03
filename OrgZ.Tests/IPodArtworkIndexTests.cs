// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// The on-device cover index: round trip, validation against the real thumbnail files, merging a
/// sync's new covers in, and following a compaction that moved the bytes.
/// </summary>
public class IPodArtworkIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orgz-artidx-{Guid.NewGuid():N}");
    private string ArtDir => Path.Combine(_root, "iPod_Control", "Artwork");

    public IPodArtworkIndexTests()
    {
        Directory.CreateDirectory(ArtDir);
        File.WriteAllBytes(Path.Combine(ArtDir, "F1060_1.ithmb"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(ArtDir, "F1061_1.ithmb"), new byte[300]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static IReadOnlyList<ArtThumb> Cover(int bigOffset, int smallOffset, int bigFile = 1) =>
    [
        new ArtThumb(1060, 320, 320, bigOffset, 400, bigFile),
        new ArtThumb(1061, 56, 56, smallOffset, 100),
    ];

    [Fact]
    public void A_missing_index_is_empty_and_a_saved_one_round_trips()
    {
        Assert.Empty(IPodArtworkIndex.LoadCovers(_root));

        IPodArtworkIndex.Merge(_root, new Dictionary<string, IReadOnlyList<ArtThumb>> { ["COVER-A"] = Cover(0, 0), ["COVER-B"] = Cover(400, 100) });

        var back = IPodArtworkIndex.LoadCovers(_root);
        Assert.Equal(2, back.Count);
        Assert.Equal(400, back["COVER-B"].Single(t => t.FormatId == 1060).IthmbOffset);
        Assert.Equal("F1060_1.ithmb", back["COVER-B"].Single(t => t.FormatId == 1060).FileName);
        Assert.True(File.Exists(Path.Combine(_root, ".orgz", "artwork-index.json")));
    }

    [Fact]
    public void Merging_keeps_earlier_covers_and_replaces_a_repeated_hash()
    {
        IPodArtworkIndex.Merge(_root, new Dictionary<string, IReadOnlyList<ArtThumb>> { ["A"] = Cover(0, 0) });
        IPodArtworkIndex.Merge(_root, new Dictionary<string, IReadOnlyList<ArtThumb>> { ["B"] = Cover(400, 100), ["A"] = Cover(600, 200) });   // 600 + 400 fills the 1000-byte file exactly

        var back = IPodArtworkIndex.LoadCovers(_root);
        Assert.Equal(2, back.Count);
        Assert.Equal(600, back["A"].Single(t => t.FormatId == 1060).IthmbOffset);
    }

    [Fact]
    public void Entries_that_no_longer_fit_the_thumbnail_files_are_dropped_on_load()
    {
        IPodArtworkIndex.Merge(_root, new Dictionary<string, IReadOnlyList<ArtThumb>>
        {
            ["fits"] = Cover(0, 0),
            ["past-the-end"] = Cover(900, 0),           // 900 + 400 > the 1000-byte file
            ["file-missing"] = Cover(0, 0, bigFile: 2), // F1060_2.ithmb does not exist
        });

        var back = IPodArtworkIndex.LoadCovers(_root);
        Assert.Equal(["fits"], back.Keys);
    }

    [Fact]
    public void A_corrupt_index_file_reads_as_empty()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".orgz"));
        File.WriteAllText(IPodArtworkIndex.PathFor(_root), "{ not json");
        Assert.Empty(IPodArtworkIndex.LoadCovers(_root));
    }

    [Fact]
    public void Remap_follows_moved_thumbnails_and_forgets_the_rest()
    {
        IPodArtworkIndex.Merge(_root, new Dictionary<string, IReadOnlyList<ArtThumb>> { ["A"] = Cover(400, 100), ["B"] = Cover(800, 200) });

        // A compaction moved A's thumbnails to the front and dropped B's small one entirely.
        var moved = new Dictionary<(string, int), ArtThumb>
        {
            [("F1060_1.ithmb", 400)] = new ArtThumb(1060, 320, 320, 0, 400),
            [("F1061_1.ithmb", 100)] = new ArtThumb(1061, 56, 56, 0, 100),
            [("F1060_1.ithmb", 800)] = new ArtThumb(1060, 320, 320, 400, 400),
        };
        IPodArtworkIndex.Remap(_root, moved, compactedEntries: 42);

        var doc = IPodArtworkIndex.Load(_root);
        Assert.Equal(42, doc.CompactedEntries);
        Assert.Equal(["A"], doc.Covers.Keys);
        Assert.Equal(0, doc.Covers["A"].Single(s => s.Format == 1060).Offset);
    }
}
