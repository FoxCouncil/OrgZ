// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// Verifies the ArtworkDB writer accumulates multiple images (so syncing a 2nd
/// track doesn't clobber the 1st track's art) by round-tripping the built DB back
/// through the parser.
/// </summary>
public class ArtworkDbWriterTests
{
    private static byte[] Serialize(ITunesDbDocument doc)
    {
        ITunesDbChunkTree.Normalize(doc.Root);
        return ITunesDbChunkTree.Serialize(doc);
    }

    [Fact]
    public void NextImageId_starts_at_100_then_increments()
    {
        Assert.Equal(100, ArtworkDbWriter.NextImageId([]));
        Assert.Equal(106, ArtworkDbWriter.NextImageId(
            [new ArtImage(1, 100, [], 0), new ArtImage(2, 105, [], 0)]));
    }

    [Fact]
    public void Build_then_read_yields_one_image()
    {
        var doc = ArtworkDbWriter.Build(0xAAAAUL, 100, [new ArtThumb(1028, 100, 100, 0, 20000)], 20000);
        var read = ArtworkDbWriter.ReadImages(ITunesDbChunkTree.Parse(Serialize(doc)));

        var img = Assert.Single(read);
        Assert.Equal(0xAAAAUL, img.Dbid);
        Assert.Equal(100, img.ImageId);
        var thumb = Assert.Single(img.Thumbs);
        Assert.Equal(1028, thumb.FormatId);
        Assert.Equal(100, thumb.Width);
        Assert.Equal(20000, thumb.ImageSize);
    }

    [Fact]
    public void BuildFromImages_roundtrips_multiple_entries_and_offsets()
    {
        var img1 = new ArtImage(0x1111UL, 100,
            [new ArtThumb(1028, 100, 100, 0, 20000), new ArtThumb(1029, 200, 200, 0, 80000)], 20000);
        var img2 = new ArtImage(0x2222UL, 101,
            [new ArtThumb(1028, 100, 100, 20000, 20000), new ArtThumb(1029, 200, 200, 80000, 80000)], 50000);

        var read = ArtworkDbWriter.ReadImages(
            ITunesDbChunkTree.Parse(Serialize(ArtworkDbWriter.BuildFromImages([img1, img2]))));

        Assert.Equal(2, read.Count);
        Assert.Equal([0x1111UL, 0x2222UL], read.Select(i => i.Dbid));
        Assert.Equal([100, 101], read.Select(i => i.ImageId));

        // The second image's large thumbnail must point past the first (append offset).
        var big = read[1].Thumbs.Single(t => t.FormatId == 1029);
        Assert.Equal(80000, big.IthmbOffset);
        Assert.Equal(200, big.Width);
    }

    [Fact]
    public void Appending_preserves_prior_image()
    {
        // First track's art on disk.
        var existing = ArtworkDbWriter.ReadImages(ITunesDbChunkTree.Parse(
            Serialize(ArtworkDbWriter.Build(0xAAAAUL, 100, [new ArtThumb(1028, 100, 100, 0, 20000)], 20000))));

        // Merge in a second track at the next id / next ithmb offset.
        var merged = new List<ArtImage>(existing)
        {
            new(0xBBBBUL, ArtworkDbWriter.NextImageId(existing), [new ArtThumb(1028, 100, 100, 20000, 20000)], 20000),
        };

        var read = ArtworkDbWriter.ReadImages(
            ITunesDbChunkTree.Parse(Serialize(ArtworkDbWriter.BuildFromImages(merged))));

        Assert.Equal(2, read.Count);
        Assert.Contains(read, i => i.Dbid == 0xAAAAUL);
        Assert.Contains(read, i => i.Dbid == 0xBBBBUL);
    }
}
