// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// hash72 + Locations.itdb.cbk (Nano 5G). The pure crypto round-trip always runs; the
/// byte-match against a real iTunes-written cbk is gated on a device fixture staged at
/// <c>%LOCALAPPDATA%\OrgZ\nano5g-fixture</c> (Locations.itdb + Locations.itdb.cbk) and
/// no-ops when absent (it can't live in CI - it's one specific device's data).
/// </summary>
public class ITunesDbHash72Tests
{
    [Fact]
    public void Generate_then_Extract_round_trips()
    {
        var sha1 = new byte[20];
        var iv = new byte[16];
        var rnd = new byte[12];
        for (int i = 0; i < 20; i++) { sha1[i] = (byte)(0x10 + i); }
        for (int i = 0; i < 16; i++) { iv[i] = (byte)(0xA0 + i); }
        for (int i = 0; i < 12; i++) { rnd[i] = (byte)(0x30 + i); }

        var sig = ITunesDbHash72.Generate(sha1, iv, rnd);

        Assert.Equal(46, sig.Length);
        Assert.Equal(0x01, sig[0]);
        Assert.Equal(0x00, sig[1]);
        Assert.Equal(rnd, sig[2..14]);

        Assert.True(ITunesDbHash72.Extract(sig, sha1, out var iv2, out var rnd2));
        Assert.Equal(iv, iv2);
        Assert.Equal(rnd, rnd2);
    }

    [Fact]
    public void Extract_rejects_bad_prefix()
    {
        var sig = new byte[46];
        sig[0] = 0x02; // not the 0x01 0x00 hash72 prefix
        Assert.False(ITunesDbHash72.Extract(sig, new byte[20], out _, out _));
    }

    [Fact]
    public void HashInfo_round_trips()
    {
        var uuid = new byte[20];
        var iv = new byte[16];
        var rnd = new byte[12];
        for (int i = 0; i < 20; i++) { uuid[i] = (byte)(i + 1); }
        for (int i = 0; i < 16; i++) { iv[i] = (byte)(0x80 + i); }
        for (int i = 0; i < 12; i++) { rnd[i] = (byte)(0x40 + i); }

        var blob = ITunesDbHash72.BuildHashInfo(uuid, iv, rnd);
        Assert.Equal(54, blob.Length);
        Assert.True(ITunesDbHash72.TryParseHashInfo(blob, out var u2, out var iv2, out var rnd2));
        Assert.Equal(uuid, u2);
        Assert.Equal(iv, iv2);
        Assert.Equal(rnd, rnd2);
    }

    [Fact]
    public void Cbk_byte_matches_iTunes_on_device_fixture()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrgZ", "nano5g-fixture");
        var locPath = Path.Combine(dir, "Locations.itdb");
        var cbkPath = Path.Combine(dir, "Locations.itdb.cbk");
        if (!File.Exists(locPath) || !File.Exists(cbkPath))
        {
            return; // device fixture absent - integration check skipped on this machine
        }

        var locations = File.ReadAllBytes(locPath);
        var realCbk = File.ReadAllBytes(cbkPath);

        // Independent halves first so a failure pinpoints the broken side.
        var blockSha1s = ITunesLocationsCbk.ComputeBlockSha1s(locations);
        var finalSha1 = ITunesLocationsCbk.ComputeFinalSha1(locations);
        Assert.Equal(realCbk[46..66], finalSha1);   // final SHA1 region matches iTunes
        Assert.Equal(realCbk[66..], blockSha1s);    // per-block SHA1 table matches iTunes

        // Crypto: recover the device seed from iTunes' own signature, re-sign, expect identity.
        Assert.True(ITunesLocationsCbk.TryExtractSeed(locations, realCbk, out var iv, out var rnd));
        var rebuilt = ITunesLocationsCbk.Build(locations, iv, rnd);
        Assert.Equal(realCbk, rebuilt);
    }
}
