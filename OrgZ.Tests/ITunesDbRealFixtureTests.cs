// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

/// <summary>
/// Parses <c>Fixtures/itunesdb/bripod-3tracks.itunesdb</c> - three <c>mhit</c> records lifted verbatim
/// from an iTunes-written database on a real iPod Video 5.5G, re-wrapped in a fresh mhbd envelope.
/// Unlike <see cref="ITunesDbReaderTests"/> (which synthesizes the bytes it later reads), the track
/// bytes here are untouched iTunes output, so passing proves conformance against a genuine reference
/// database rather than the parser's own assumptions. See the fixture's README for provenance.
/// </summary>
public class ITunesDbRealFixtureTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "itunesdb", "bripod-3tracks.itunesdb");

    [Fact]
    public void Reads_real_iTunes_tracks_with_load_bearing_fields()
    {
        var tracks = ITunesDbReader.Read(FixturePath, @"F:\");

        Assert.Equal(3, tracks.Count);

        Assert.Equal("Polaris", tracks[0].Title);
        Assert.Equal(288_235, tracks[0].DurationMs);
        Assert.Equal(10_136_119, tracks[0].FileSize);

        Assert.Equal("Salt Water Sound", tracks[1].Title);
        Assert.Equal(330_893, tracks[1].DurationMs);
        Assert.Equal(11_200_042, tracks[1].FileSize);

        Assert.Equal("Distractions", tracks[2].Title);
        Assert.Equal(316_421, tracks[2].DurationMs);
        Assert.Equal(10_099_969, tracks[2].FileSize);
    }

    [Fact]
    public void Total_discs_reads_as_one_not_sixty_five_thousand()
    {
        // Every track on the source device is "disc 1 of 1". Total-discs is a u16 at 0x60; the
        // pre-slice-B reader's ReadInt32(0x5E) spanned 0x5E-0x61 and swept up the 0x60 disc byte,
        // returning 65536. This asserts the real bytes decode to 1 - the regression guard.
        var tracks = ITunesDbReader.Read(FixturePath, @"F:\");

        Assert.All(tracks, t => Assert.Equal(1, t.DiscNumber));
        Assert.All(tracks, t => Assert.Equal(1, t.TotalDiscs));
    }

    [Fact]
    public void Rating_reads_from_0x1F_not_the_0x1C_flag_byte()
    {
        // The source tracks are unrated. Rating is a byte at 0x1F; the old reader pulled 0x1C, which
        // is a flags byte that is frequently non-zero, so it invented ratings on unrated tracks.
        var tracks = ITunesDbReader.Read(FixturePath, @"F:\");

        Assert.All(tracks, t => Assert.Equal(0, t.Rating));
    }
}
