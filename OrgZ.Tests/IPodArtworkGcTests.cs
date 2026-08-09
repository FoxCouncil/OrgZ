// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

/// <summary>
/// Artwork GC: removing a track's dbid must drop its mhii from the ArtworkDB AND give the
/// .ithmb bytes back, with the surviving entries' offsets rewritten to match the compacted
/// file. Runs against a synthetic mount - no fixtures needed.
/// </summary>
public class IPodArtworkGcTests : IDisposable
{
    private readonly string _mount;
    private readonly string _artworkDir;

    private const int FormatId = 1017;   // 5.5G 128x128 - any id works, the GC is format-agnostic

    public IPodArtworkGcTests()
    {
        _mount = Path.Combine(Path.GetTempPath(), $"orgz-artgc-{Guid.NewGuid():N}");
        _artworkDir = Path.Combine(_mount, "iPod_Control", "Artwork");
        Directory.CreateDirectory(_artworkDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_mount, recursive: true); } catch { }
    }

    /// <summary>Writes an ArtworkDB + one ithmb holding N same-size fake thumbnails, byte value = image index.</summary>
    private List<ArtImage> Seed(int count, int thumbSize)
    {
        var images = new List<ArtImage>();
        var ithmb = new byte[count * thumbSize];
        for (int i = 0; i < count; i++)
        {
            Array.Fill(ithmb, (byte)(i + 1), i * thumbSize, thumbSize);
            images.Add(new ArtImage(
                Dbid: (ulong)(0x1000 + i),
                ImageId: 100 + i,
                Thumbs: [new ArtThumb(FormatId, 128, 128, i * thumbSize, thumbSize)],
                OrigImgSize: thumbSize));
        }

        File.WriteAllBytes(Path.Combine(_artworkDir, $"F{FormatId}_1.ithmb"), ithmb);

        var doc = ArtworkDbWriter.BuildFromImages(images);
        ITunesDbChunkTree.Normalize(doc.Root);
        File.WriteAllBytes(Path.Combine(_artworkDir, "ArtworkDB"), ITunesDbChunkTree.Serialize(doc));
        return images;
    }

    private List<ArtImage> ReadBack()
        => ArtworkDbWriter.ReadImages(ITunesDbChunkTree.Parse(File.ReadAllBytes(Path.Combine(_artworkDir, "ArtworkDB"))));

    [Fact]
    public void Removing_the_middle_entry_compacts_the_ithmb_and_rewrites_offsets()
    {
        const int size = 512;
        Seed(3, size);

        IPodArtworkGc.RemoveArt(_mount, [0x1001]);   // the middle image (fill byte 2)

        var kept = ReadBack();
        Assert.Equal(2, kept.Count);
        Assert.DoesNotContain(kept, i => i.Dbid == 0x1001);

        // Compacted file: exactly the two survivors, back to back, in original order.
        var ithmb = File.ReadAllBytes(Path.Combine(_artworkDir, $"F{FormatId}_1.ithmb"));
        Assert.Equal(2 * size, ithmb.Length);
        Assert.All(ithmb.Take(size), b => Assert.Equal(1, b));
        Assert.All(ithmb.Skip(size), b => Assert.Equal(3, b));

        // Offsets in the rewritten DB point at the compacted positions.
        Assert.Equal(0, kept.Single(i => i.Dbid == 0x1000).Thumbs[0].IthmbOffset);
        Assert.Equal(size, kept.Single(i => i.Dbid == 0x1002).Thumbs[0].IthmbOffset);
    }

    [Fact]
    public void Removing_the_last_entry_deletes_the_orphaned_ithmb()
    {
        Seed(1, 256);

        IPodArtworkGc.RemoveArt(_mount, [0x1000]);

        Assert.Empty(ReadBack());
        Assert.False(File.Exists(Path.Combine(_artworkDir, $"F{FormatId}_1.ithmb")));
    }

    [Fact]
    public void Unknown_dbid_leaves_everything_untouched()
    {
        Seed(2, 256);
        var before = File.ReadAllBytes(Path.Combine(_artworkDir, "ArtworkDB"));

        IPodArtworkGc.RemoveArt(_mount, [0xDEAD]);

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_artworkDir, "ArtworkDB")));
        Assert.Equal(2 * 256, new FileInfo(Path.Combine(_artworkDir, $"F{FormatId}_1.ithmb")).Length);
    }

    [Fact]
    public void Missing_artwork_db_is_a_silent_noop()
    {
        // No Seed - nothing to do, nothing to throw.
        IPodArtworkGc.RemoveArt(_mount, [0x1000]);
    }
}
