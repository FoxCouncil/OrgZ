// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.IO.Compression;
using System.Text;
using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// Offline proof that the Nano 5G CDB writer produces a CDB with the SAME structure iTunes does.
/// The fixture <c>itunes-nano5g-empty.cdb</c> is a real iTunes-written empty Nano 5G CDB; we inject a
/// track into it via the template path and assert the 5-dataset structure + version survive and the
/// track lands. No device required - this is what gates touching real hardware.
/// </summary>
public class Nano5gCdbWriterTests
{
    private const int HeaderLen = 0xF4;

    private static string GoldenPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "itunes-nano5g-empty.cdb");

    private static byte[] Decompress(byte[] cdb)
    {
        using var src = new MemoryStream(cdb, HeaderLen, cdb.Length - HeaderLen);
        using var z = new ZLibStream(src, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        z.CopyTo(outMs);
        var body = outMs.ToArray();
        var plain = new byte[HeaderLen + body.Length];
        Array.Copy(cdb, 0, plain, 0, HeaderLen);
        Array.Copy(body, 0, plain, HeaderLen, body.Length);
        return plain;
    }

    private static int CountMagic(byte[] b, string magic)
    {
        var m = Encoding.ASCII.GetBytes(magic);
        int n = 0;
        for (int i = 0; i + m.Length <= b.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < m.Length; j++)
            {
                if (b[i + j] != m[j]) { hit = false; break; }
            }
            if (hit) { n++; }
        }
        return n;
    }

    [Fact]
    public void Golden_iTunes_Template_Is_The_Expected_Five_Dataset_Shape()
    {
        Assert.True(File.Exists(GoldenPath), $"golden iTunes CDB fixture missing at {GoldenPath}");
        var golden = File.ReadAllBytes(GoldenPath);
        Assert.Equal("mhbd", Encoding.ASCII.GetString(golden, 0, 4));
        Assert.Equal(5, BitConverter.ToInt32(golden, 0x14));        // datasets iTunes writes
        Assert.Equal(0x73, BitConverter.ToInt32(golden, 0x10));     // db version
        Assert.Equal(0, CountMagic(Decompress(golden), "mhit"));    // empty library
    }

    [Fact]
    public void Injecting_A_Track_Preserves_iTunes_Structure_And_Adds_The_Track()
    {
        var golden = File.ReadAllBytes(GoldenPath);

        var doc = Nano5gCdbWriter.FromTemplate(golden);
        ITunesDbWriter.ClearLibrary(doc);
        ITunesDbWriter.AddTrack(doc, new NewTrack
        {
            TrackId = 1,
            IpodPath = ":iPod_Control:Music:F00:TEST.m4a",
            Title = "OfflineProofTrack",
            Artist = "Proof Artist",
            Album = "Proof Album",
            FileSize = 4_200_000,
            LengthMs = 222_000,
            Dbid = 0x1122334455667788,
        });

        const string fwGuid = "000A2700203CFBA5";
        var cdb = Nano5gCdbWriter.Emit(doc, fwGuid);

        // The 244-byte header is byte-identical to iTunes's own - that's the whole point - except the
        // fields we rewrite: total_len (0x08), the hash58 signature (0x58, scheme 1), and the now-unused
        // hash72 field (0x72) we leave as the skeleton's. If any OTHER header byte drifted, the structure
        // no longer matches iTunes.
        for (int i = 0; i < HeaderLen; i++)
        {
            bool rewritten = (i >= 0x08 && i < 0x0C)
                          || (i >= 0x58 && i < 0x58 + 20)
                          || (i >= 0x72 && i < 0x72 + ITunesDbHash72.SignatureLength);
            if (!rewritten)
            {
                Assert.True(golden[i] == cdb[i], $"header byte 0x{i:X2} drifted from iTunes: golden=0x{golden[i]:X2} cdb=0x{cdb[i]:X2}");
            }
        }
        Assert.Equal("mhbd", Encoding.ASCII.GetString(cdb, 0, 4));
        Assert.Equal(5, BitConverter.ToInt32(cdb, 0x14));           // 5 datasets PRESERVED
        Assert.Equal(0x73, BitConverter.ToInt32(cdb, 0x10));        // version PRESERVED
        Assert.Equal(1, BitConverter.ToInt32(cdb, 0x30));           // hashing scheme = hash58
        Assert.Equal(cdb.Length, BitConverter.ToInt32(cdb, 0x08));  // total_len = file size

        // hash58 is correctly applied: re-signing the emitted file reproduces the same 0x58.
        var resign = (byte[])cdb.Clone();
        ITunesDbHash58.Apply(resign, fwGuid);
        Assert.Equal(cdb[0x58..(0x58 + 20)], resign[0x58..(0x58 + 20)]);

        // Body decompresses, has the same datasets as the golden, plus exactly our one track.
        var plain = Decompress(cdb);
        var goldenPlain = Decompress(golden);
        Assert.Equal(CountMagic(goldenPlain, "mhsd"), CountMagic(plain, "mhsd"));   // datasets unchanged
        Assert.Equal(1, CountMagic(plain, "mhit"));                                 // exactly one track
        Assert.Contains("OfflineProofTrack", Encoding.Unicode.GetString(plain));    // title MHOD (UTF-16LE)
    }
}
