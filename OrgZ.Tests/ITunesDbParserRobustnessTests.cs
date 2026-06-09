// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text;
using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// Hardening tests for the chunk tree: it must reject malformed input rather than
/// read out of bounds, accept both database roots (mhbd / mhfd), and recompute
/// list counts after the tree is mutated.
/// </summary>
public class ITunesDbParserRobustnessTests
{
    [Fact]
    public void Parse_rejects_empty_input()
        => Assert.Throws<InvalidDataException>(() => ITunesDbChunkTree.Parse([]));

    [Fact]
    public void Parse_rejects_non_database_root()
    {
        var junk = Encoding.ASCII.GetBytes("XXXX....header..pad.");
        Assert.Throws<InvalidDataException>(() => ITunesDbChunkTree.Parse(junk));
    }

    [Fact]
    public void Parse_rejects_header_size_below_minimum()
    {
        var b = new byte[32];
        "mhbd"u8.CopyTo(b);
        b[4] = 4;   // headerSize = 4 (< 12) - must be rejected, not trusted
        Assert.Throws<InvalidDataException>(() => ITunesDbChunkTree.Parse(b));
    }

    [Fact]
    public void Parse_rejects_total_size_past_buffer()
    {
        var b = new byte[32];
        "mhbd"u8.CopyTo(b);
        b[4] = 12;            // headerSize = 12
        b[8] = 0xFF; b[9] = 0xFF;   // totalSize ~64KB, far past the 32-byte buffer
        Assert.Throws<InvalidDataException>(() => ITunesDbChunkTree.Parse(b));
    }

    [Fact]
    public void ArtworkDb_mhfd_root_roundtrips_byte_for_byte()
    {
        var doc = ArtworkDbWriter.Build(0xAAAAUL, 100, [new ArtThumb(1028, 100, 100, 0, 20000)], 20000);
        ITunesDbChunkTree.Normalize(doc.Root);
        var bytes = ITunesDbChunkTree.Serialize(doc);

        var reparsed = ITunesDbChunkTree.Parse(bytes);   // must accept the 'mhfd' root
        Assert.Equal("mhfd", reparsed.Root.Magic);
        ITunesDbChunkTree.Normalize(reparsed.Root);
        Assert.Equal(bytes, ITunesDbChunkTree.Serialize(reparsed));
    }

    [Fact]
    public void Normalize_tracks_count_after_add_and_remove()
    {
        var doc = ITunesDbWriter.CreateEmpty();
        ITunesDbWriter.AddTrack(doc, new NewTrack { TrackId = 1, IpodPath = ":i:a.m4a" });
        ITunesDbWriter.AddTrack(doc, new NewTrack { TrackId = 2, IpodPath = ":i:b.m4a" });
        ITunesDbChunkTree.Normalize(doc.Root);

        var tracksMhsd = doc.Root.Children.Single(c => c.Magic == "mhsd" && c.ReadHeaderInt32(0x0C) == 1);
        var mhlt = tracksMhsd.Children.Single(c => c.Magic == "mhlt");
        Assert.Equal(2, mhlt.ReadHeaderInt32(8));   // list count tracks the items

        tracksMhsd.Children.RemoveAll(c => c.Magic == "mhit" && c.ReadHeaderInt32(0x10) == 1);
        ITunesDbChunkTree.Normalize(doc.Root);
        Assert.Equal(1, mhlt.ReadHeaderInt32(8));
    }

    [Fact]
    public void Serialized_size_matches_root_consumed_size()
    {
        var doc = ITunesDbWriter.CreateEmpty();
        ITunesDbWriter.AddTrack(doc, new NewTrack { TrackId = 1, IpodPath = ":i:a.m4a", Title = "A" });
        ITunesDbChunkTree.Normalize(doc.Root);
        var bytes = ITunesDbChunkTree.Serialize(doc);
        Assert.Equal(doc.Root.ConsumedSize, bytes.Length);
        Assert.Equal(bytes.Length, doc.Root.ReadHeaderInt32(8));   // mhbd total size field
    }
}
