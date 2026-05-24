// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

public class DiscIdServiceTests
{
    // The canonical worked example from https://musicbrainz.org/doc/Disc_ID_Calculation
    // (first=1, last=6, lead-out=95462). Independently reproduced and confirmed against
    // the published Disc ID, so this is a fixed golden vector.
    private static readonly int[] CanonicalOffsets = [150, 15363, 32314, 46592, 63414, 80489];
    private const int CanonicalLeadOut = 95462;
    private const string CanonicalDiscId = "49HHV7Eb8UKF3aQiNmu1GR8vKTY-";

    // -- ComputeDiscId --

    [Fact]
    public void ComputeDiscId_MatchesMusicBrainzWorkedExample()
    {
        var id = DiscIdService.ComputeDiscId(1, 6, CanonicalOffsets, CanonicalLeadOut);

        Assert.Equal(CanonicalDiscId, id);
    }

    [Fact]
    public void ComputeDiscId_IsAlways28Characters()
    {
        // SHA-1 is 20 bytes → 28 base64 chars (including one '=' → '-' pad char).
        var id = DiscIdService.ComputeDiscId(1, 6, CanonicalOffsets, CanonicalLeadOut);

        Assert.Equal(28, id.Length);
    }

    [Fact]
    public void ComputeDiscId_NeverContainsStandardBase64Chars()
    {
        // MusicBrainz substitutes + → . , / → _ , = → - so the ID is URL-safe.
        // Sweep a spread of TOCs to exercise hashes that would otherwise emit +, /, =.
        for (int seed = 1; seed <= 200; seed++)
        {
            var offsets = new[] { 150, 150 + seed * 137, 150 + seed * 521, 150 + seed * 1009 };
            var id = DiscIdService.ComputeDiscId(1, 4, offsets, 150 + seed * 2000);

            Assert.DoesNotContain('+', id);
            Assert.DoesNotContain('/', id);
            Assert.DoesNotContain('=', id);
        }
    }

    [Fact]
    public void ComputeDiscId_IgnoresOffsetsBeyondSlot99()
    {
        // The spec hashes exactly 99 offset slots; anything past index 98 must not change the ID.
        var ninetyNine = new int[99];
        for (int i = 0; i < 99; i++)
        {
            ninetyNine[i] = 150 + i * 1000;
        }

        var hundred = new int[100];
        Array.Copy(ninetyNine, hundred, 99);
        hundred[99] = 999999; // extra slot that should be dropped

        var a = DiscIdService.ComputeDiscId(1, 99, ninetyNine, 200000);
        var b = DiscIdService.ComputeDiscId(1, 99, hundred, 200000);

        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeDiscId_PadsMissingSlotsWithZero()
    {
        // A short array must produce the same ID as one explicitly zero-filled to 99 slots,
        // because unused track slots hash as 00000000.
        var padded = new int[99];
        Array.Copy(CanonicalOffsets, padded, CanonicalOffsets.Length);

        var fromShort = DiscIdService.ComputeDiscId(1, 6, CanonicalOffsets, CanonicalLeadOut);
        var fromPadded = DiscIdService.ComputeDiscId(1, 6, padded, CanonicalLeadOut);

        Assert.Equal(fromShort, fromPadded);
    }

    [Fact]
    public void ComputeDiscId_IsSensitiveToLeadOut()
    {
        var a = DiscIdService.ComputeDiscId(1, 6, CanonicalOffsets, CanonicalLeadOut);
        var b = DiscIdService.ComputeDiscId(1, 6, CanonicalOffsets, CanonicalLeadOut + 1);

        Assert.NotEqual(a, b);
    }

    // -- MsfToLba (Red Book: 75 frames per second, 2-second pregap = 150 frames) --

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(0, 0, 74, 74)]
    [InlineData(0, 1, 0, 75)]
    [InlineData(0, 2, 0, 150)]   // standard CD pregap
    [InlineData(1, 0, 0, 4500)]
    [InlineData(1, 2, 3, 4653)]
    [InlineData(74, 0, 0, 333000)]
    public void MsfToLba_ConvertsCorrectly(int m, int s, int f, int expected)
    {
        Assert.Equal(expected, DiscIdService.MsfToLba(m, s, f));
    }

    // -- BuildTocString --

    [Fact]
    public void BuildTocString_JoinsFirstLastLeadOutThenOffsets()
    {
        var toc = DiscIdService.BuildTocString(1, 6, CanonicalOffsets, CanonicalLeadOut);

        Assert.Equal("1+6+95462+150+15363+32314+46592+63414+80489", toc);
    }

    [Fact]
    public void BuildTocString_SingleTrack()
    {
        var toc = DiscIdService.BuildTocString(1, 1, [150], 95462);

        Assert.Equal("1+1+95462+150", toc);
    }
}
