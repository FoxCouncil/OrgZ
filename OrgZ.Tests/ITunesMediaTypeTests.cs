// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

/// <summary>
/// Pins the single owner of the iTunes media-kind knowledge: the value→MediaKind mapping every
/// reader/projection funnels through, and the constants the writers/readers agree on. If the
/// mapping ever grows (video=6, etc.), this is where the expectation lives.
/// </summary>
public class ITunesMediaTypeTests
{
    [Theory]
    [InlineData(ITunesMediaType.Audio,     MediaKind.Music)]
    [InlineData(ITunesMediaType.Podcast,   MediaKind.Podcast)]
    [InlineData(ITunesMediaType.Audiobook, MediaKind.Audiobook)]
    [InlineData(0,  MediaKind.Music)]    // unset / pre-podcast-era mhit
    [InlineData(6,  MediaKind.Music)]    // video - unmapped kinds read as Music, never crash
    [InlineData(32, MediaKind.Music)]    // music video
    [InlineData(-1, MediaKind.Music)]
    public void ToKind_maps_known_kinds_and_defaults_unknown_to_music(int mediaType, MediaKind expected)
    {
        Assert.Equal(expected, ITunesMediaType.ToKind(mediaType));
    }

    [Fact]
    public void Constants_match_the_iTunesDB_wire_values()
    {
        // These are Apple's on-disk values (libgpod's ITDB_MEDIATYPE_*) and the MHIT field offset -
        // changing any silently desyncs every iPod written before the change.
        Assert.Equal(1, ITunesMediaType.Audio);
        Assert.Equal(4, ITunesMediaType.Podcast);
        Assert.Equal(8, ITunesMediaType.Audiobook);
        Assert.Equal(0xD0, ITunesMediaType.MhitOffset);
    }
}
