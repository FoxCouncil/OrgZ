// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// The artwork de-duplicator's decision half: which entries count as the same picture, and where
/// each distinct picture lands in the rebuilt .ithmb files. Pinned as pure functions - the file
/// rewrite around them is I/O, but a packing mistake here would point a track's cover at the wrong
/// bytes, which on a device looks like album art belonging to a different album.
/// </summary>
public class IPodArtworkCompactorTests
{
    private const int Big = 1060;    // 320x320 on a Classic
    private const int Small = 1061;  // 56x56

    private static ArtImage Image(ulong dbid, int imageId, int bigOffset, int smallOffset) => new(
        dbid, imageId,
        [
            new ArtThumb(Big, 320, 320, bigOffset, 400),
            new ArtThumb(Small, 56, 56, smallOffset, 100),
        ],
        1234);

    private static byte[] Fill(byte value, int length) => Enumerable.Repeat(value, length).ToArray();

    [Fact]
    public void CoverKey_matches_identical_pictures_and_separates_different_ones()
    {
        var a = IPodArtworkCompactor.CoverKey([(Big, Fill(1, 400)), (Small, Fill(2, 100))]);
        var sameBytesOtherOrder = IPodArtworkCompactor.CoverKey([(Small, Fill(2, 100)), (Big, Fill(1, 400))]);
        var differentPicture = IPodArtworkCompactor.CoverKey([(Big, Fill(9, 400)), (Small, Fill(2, 100))]);

        Assert.Equal(a, sameBytesOtherOrder);   // format order must not decide identity
        Assert.NotEqual(a, differentPicture);
    }

    [Fact]
    public void CoverKey_separates_the_same_bytes_used_at_a_different_size()
    {
        // Without the format id in the hash, a small thumbnail whose bytes happen to match a slice
        // of a larger one could be treated as the same cover.
        var asBig = IPodArtworkCompactor.CoverKey([(Big, Fill(7, 100))]);
        var asSmall = IPodArtworkCompactor.CoverKey([(Small, Fill(7, 100))]);
        Assert.NotEqual(asBig, asSmall);
    }

    [Fact]
    public void PlanLayout_gives_one_copy_per_cover_and_packs_it_tight()
    {
        // Three tracks off one album (same cover) then a track from another album - the shape that
        // wasted about 8 GB on a 29k-track library.
        const string albumA = "COVER-A";
        const string albumB = "COVER-B";
        var planned = IPodArtworkCompactor.PlanLayout(
        [
            (Image(10, 101, 0, 0), albumA),
            (Image(11, 102, 400, 100), albumA),
            (Image(12, 103, 800, 200), albumA),
            (Image(13, 104, 1200, 300), albumB),
        ]);

        // Every track keeps its own entry: same dbid, same image id, still two thumbnails.
        Assert.Equal([10UL, 11UL, 12UL, 13UL], planned.Select(p => p.Dbid));
        Assert.Equal([101, 102, 103, 104], planned.Select(p => p.ImageId));
        Assert.All(planned, p => Assert.Equal(2, p.Thumbs.Count));

        // The three album-A tracks now point at one copy, at the start of each file.
        foreach (var entry in planned.Take(3))
        {
            Assert.Equal(0, entry.Thumbs.Single(t => t.FormatId == Big).IthmbOffset);
            Assert.Equal(0, entry.Thumbs.Single(t => t.FormatId == Small).IthmbOffset);
        }

        // Album B follows immediately - no gap left by the copies that were dropped.
        Assert.Equal(400, planned[3].Thumbs.Single(t => t.FormatId == Big).IthmbOffset);
        Assert.Equal(100, planned[3].Thumbs.Single(t => t.FormatId == Small).IthmbOffset);

        // Two distinct covers stored, not four: 1000 bytes instead of 2000.
        long stored = planned.SelectMany(p => p.Thumbs)
            .DistinctBy(t => (t.FormatId, t.IthmbOffset))
            .Sum(t => (long)t.ImageSize);
        Assert.Equal(1000, stored);
    }

    [Fact]
    public void PlanLayout_rolls_a_format_into_the_next_file_at_the_ceiling()
    {
        // Three distinct covers, 400 bytes each at the big format, with a 1000-byte ceiling: two fit
        // the first file, the third must open F1060_2 at offset 0. The small format never fills.
        var planned = IPodArtworkCompactor.PlanLayout(
        [
            (Image(10, 101, 0, 0), "A"),
            (Image(11, 102, 400, 100), "B"),
            (Image(12, 103, 800, 200), "C"),
        ], fileLimit: 1000);

        var bigs = planned.Select(p => p.Thumbs.Single(t => t.FormatId == Big)).ToList();
        Assert.Equal([(1, 0), (1, 400), (2, 0)], bigs.Select(t => (t.FileIndex, t.IthmbOffset)));
        Assert.All(planned.Select(p => p.Thumbs.Single(t => t.FormatId == Small)), t => Assert.Equal(1, t.FileIndex));
    }

    [Fact]
    public void PlanLayout_leaves_an_already_compact_device_alone()
    {
        var planned = IPodArtworkCompactor.PlanLayout(
        [
            (Image(10, 101, 0, 0), "A"),
            (Image(11, 102, 400, 100), "B"),
        ]);

        Assert.Equal(0, planned[0].Thumbs.Single(t => t.FormatId == Big).IthmbOffset);
        Assert.Equal(400, planned[1].Thumbs.Single(t => t.FormatId == Big).IthmbOffset);
    }
}
