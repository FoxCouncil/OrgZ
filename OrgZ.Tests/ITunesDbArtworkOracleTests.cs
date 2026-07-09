// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// Slice F: verifies OrgZ's <see cref="ArtworkDbWriter"/> + the RGB565 <c>.ithmb</c> against libgpod's
/// own artwork reader (<c>OrgZ.Tests/oracle/artwork_dump.c</c>, itdb_parse + itdb_artwork_get_pixbuf).
/// OrgZ writes an ArtworkDB + thumbnail for a track; libgpod reads it back - linking the artwork to the
/// track by dbid, decoding the thumbnail to its native dimensions, and returning pixels that survive
/// the RGB565 round-trip. Never verified with OrgZ's own reader.
///
/// libgpod only parses the ArtworkDB for a device it recognises as supporting cover art, so the
/// mountpoint carries a minimal <c>iPod_Control/Device/SysInfo</c> with the iPod Video model
/// (ModelNumStr MA446 → "A446" after libgpod drops the leading letter).
///  * <see cref="Emit_reproduces_the_libgpod_blessed_artworkdb"/> runs everywhere (CI).
///  * <see cref="Libgpod_reads_the_artwork_back_for_the_track"/> runs the live oracle when
///    <c>ORGZ_ARTWORK_DUMP</c> points at a built <c>artwork_dump</c> (see oracle/README.md).
/// </summary>
public class ITunesDbArtworkOracleTests
{
    private const ulong Dbid = 0x1122334455667788;
    private const int FormatId = 1028;   // iPod Video list thumbnail: 100x100 RGB565-LE
    private const int Dim = 100;
    private static readonly DateTime Added = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "itunesdb-write");

    /// <summary>Writes a full artwork mountpoint (iTunesDB + ArtworkDB + F1028_1.ithmb + SysInfo) and
    /// returns its path plus the exact ArtworkDB bytes. The thumbnail is a deterministic solid red
    /// (RGB565 0xF800 → little-endian 00 F8) so the pixel round-trip is checkable.</summary>
    internal static (string mount, byte[] artworkDb) EmitArtwork(string subdir)
    {
        var doc = ITunesDbWriter.CreateEmpty();
        ITunesDbWriter.AddTrack(doc, new NewTrack
        {
            TrackId = 1,
            IpodPath = ":iPod_Control:Music:F00:AAAA.mp3",
            Title = "Art Track",
            Artist = "A",
            Album = "Al",
            FileSize = 1_000_000,
            LengthMs = 60_000,
            Bitrate = 128,
            SampleRate = 44_100,
            DateAddedUtc = Added,
            Dbid = Dbid,
            HasArtwork = true,
            ArtworkSize = Dim * Dim * 2,
        });
        ITunesDbChunkTree.Normalize(doc.Root);
        var itdb = ITunesDbChunkTree.Serialize(doc);

        var artDoc = ArtworkDbWriter.Build(Dbid, 100, [new ArtThumb(FormatId, Dim, Dim, 0, Dim * Dim * 2)], Dim * Dim * 2);
        ITunesDbChunkTree.Normalize(artDoc.Root);
        var artworkDb = ITunesDbChunkTree.Serialize(artDoc);

        var ithmb = new byte[Dim * Dim * 2];
        for (int i = 0; i < ithmb.Length; i += 2) { ithmb[i] = 0x00; ithmb[i + 1] = 0xF8; }

        var mount = Path.Combine(AppContext.BaseDirectory, "artwork-out", subdir);
        Directory.CreateDirectory(Path.Combine(mount, "iPod_Control", "iTunes"));
        Directory.CreateDirectory(Path.Combine(mount, "iPod_Control", "Artwork"));
        Directory.CreateDirectory(Path.Combine(mount, "iPod_Control", "Device"));
        File.WriteAllBytes(Path.Combine(mount, "iPod_Control", "iTunes", "iTunesDB"), itdb);
        File.WriteAllBytes(Path.Combine(mount, "iPod_Control", "Artwork", "ArtworkDB"), artworkDb);
        File.WriteAllBytes(Path.Combine(mount, "iPod_Control", "Artwork", $"F{FormatId}_1.ithmb"), ithmb);
        File.WriteAllText(Path.Combine(mount, "iPod_Control", "Device", "SysInfo"), "ModelNumStr: MA446\n");
        return (mount, artworkDb);
    }

    [Fact]
    public void Emit_reproduces_the_libgpod_blessed_artworkdb()
    {
        var (_, artworkDb) = EmitArtwork("repro");
        var blessed = File.ReadAllBytes(Path.Combine(FixtureDir, "orgz-emitted-artworkdb.bin"));
        Assert.Equal(blessed, artworkDb);
    }

    [Fact]
    public void Libgpod_reads_the_artwork_back_for_the_track()
    {
        var oracle = Environment.GetEnvironmentVariable("ORGZ_ARTWORK_DUMP");
        if (string.IsNullOrWhiteSpace(oracle) || !File.Exists(oracle))
        {
            return;   // gated - needs a built artwork_dump (libgpod + gdk-pixbuf); see oracle/README.md
        }

        var (mount, _) = EmitArtwork("oracle");
        var psi = new ProcessStartInfo(oracle, $"\"{mount}\" MA446")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        Assert.True(proc.ExitCode == 0, $"artwork_dump failed ({proc.ExitCode}): {stderr}");
        var line = stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                         .Single(l => l.Contains("\"id\":1,"));

        // libgpod linked the ArtworkDB entry to the track by dbid, decoded the native dimensions, and the
        // red thumbnail survived the RGB565 round-trip (0xF800 -> R=248).
        Assert.Contains($"\"art_dbid\":{Dbid}", line);
        Assert.Contains("\"w\":100,\"h\":100", line);
        Assert.Matches(@"""px0"":\[2[0-5]\d,\d,\d\]", line);   // red: R in 200s, G and B tiny
    }

    [Fact]
    public void Libgpod_reads_artwork_added_to_a_real_library()
    {
        var path = Environment.GetEnvironmentVariable("ORGZ_REAL_ITUNESDB");
        var oracle = Environment.GetEnvironmentVariable("ORGZ_ARTWORK_DUMP");
        if (path is null || !File.Exists(path) || string.IsNullOrWhiteSpace(oracle) || !File.Exists(oracle))
        {
            return;   // gated - needs a real 5.5G DB + a built artwork_dump
        }

        const ulong artDbid = 0x00A2_0126_0126_0126;   // distinct from any real track's dbid
        var original = File.ReadAllBytes(path);
        ITunesDbReader.ReadAll(original, @"X:\", out var origTracks, out _);

        // Add a track WITH artwork into the real library.
        var doc = ITunesDbChunkTree.Parse(original);
        uint newId = ITunesDbWriter.NextTrackId(doc);
        ITunesDbWriter.AddTrack(doc, new NewTrack
        {
            TrackId = newId, IpodPath = ":iPod_Control:Music:F99:ART.mp3", Title = "OrgZ Art",
            Artist = "OrgZ", Album = "OrgZ", FileSize = 1_000_000, LengthMs = 60_000, Bitrate = 128,
            SampleRate = 44_100, DateAddedUtc = Added, Dbid = artDbid, HasArtwork = true,
            ArtworkSize = Dim * Dim * 2,
        });
        ITunesDbChunkTree.Normalize(doc.Root);
        var itdb = ITunesDbChunkTree.Serialize(doc);

        var artDoc = ArtworkDbWriter.Build(artDbid, 100, [new ArtThumb(FormatId, Dim, Dim, 0, Dim * Dim * 2)], Dim * Dim * 2);
        ITunesDbChunkTree.Normalize(artDoc.Root);
        var artworkDb = ITunesDbChunkTree.Serialize(artDoc);
        var ithmb = new byte[Dim * Dim * 2];
        for (int i = 0; i < ithmb.Length; i += 2) { ithmb[i] = 0x00; ithmb[i + 1] = 0xF8; }

        var mount = Path.Combine(Path.GetTempPath(), "orgz-artreal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(mount, "iPod_Control", "iTunes"));
        Directory.CreateDirectory(Path.Combine(mount, "iPod_Control", "Artwork"));
        Directory.CreateDirectory(Path.Combine(mount, "iPod_Control", "Device"));
        File.WriteAllBytes(Path.Combine(mount, "iPod_Control", "iTunes", "iTunesDB"), itdb);
        File.WriteAllBytes(Path.Combine(mount, "iPod_Control", "Artwork", "ArtworkDB"), artworkDb);
        File.WriteAllBytes(Path.Combine(mount, "iPod_Control", "Artwork", $"F{FormatId}_1.ithmb"), ithmb);
        File.WriteAllText(Path.Combine(mount, "iPod_Control", "Device", "SysInfo"), "ModelNumStr: MA446\n");

        try
        {
            var psi = new ProcessStartInfo(oracle, $"\"{mount}\" MA446")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            Assert.True(proc.ExitCode == 0, $"artwork_dump failed ({proc.ExitCode}): {stderr}");
            var lines = stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(origTracks.Count + 1, lines.Length);                       // originals preserved + 1
            var mine = lines.Single(l => l.Contains($"\"id\":{newId},"));
            Assert.Contains($"\"art_dbid\":{artDbid}", mine);                       // artwork linked to the new track
            Assert.Contains("\"w\":100,\"h\":100", mine);
            Assert.Matches(@"""px0"":\[2[0-5]\d,\d,\d\]", mine);
        }
        finally
        {
            try { Directory.Delete(mount, recursive: true); } catch { /* best-effort */ }
        }
    }
}
