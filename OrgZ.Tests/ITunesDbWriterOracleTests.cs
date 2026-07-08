// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// Slice C: verifies OrgZ's iTunesDB WRITER against an INDEPENDENT oracle - libgpod's own
/// <c>itdb_parse</c>, via <c>OrgZ.Tests/oracle/gpod_dump.c</c>. OrgZ emits a database; libgpod reads
/// it back; the fields must match what OrgZ wrote. Never verified with OrgZ's own reader - that is the
/// circularity that let real bugs hide (mhbd dataset-count = 0 → unreadable; MHIPs with no type-100
/// position MHOD → an empty on-device song list). See the fixture README for how to regenerate.
///
/// Two committed proofs:
///  * <see cref="Emit_reproduces_the_libgpod_blessed_bytes"/> runs everywhere (incl. Windows CI): the
///    writer must reproduce, byte-for-byte, the exact database libgpod blessed.
///  * <see cref="Libgpod_reads_back_every_field"/> runs the live oracle when <c>ORGZ_GPOD_DUMP</c>
///    points at a built <c>gpod_dump</c> (see the README's Docker one-liner) and diffs its JSON against
///    the committed golden.
/// </summary>
public class ITunesDbWriterOracleTests
{
    internal static readonly DateTime Added = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "itunesdb-write");

    /// <summary>Deterministically builds the sample library (fixed ids, dates and dbids so the bytes are
    /// stable) and writes it into a throwaway iPod mountpoint. Returns the emitted bytes.</summary>
    internal static byte[] EmitSampleLibrary(out string mountPoint)
    {
        var doc = ITunesDbWriter.CreateEmpty();
        ITunesDbWriter.AddTrack(doc, new NewTrack
        {
            TrackId = 1,
            IpodPath = ":iPod_Control:Music:F00:AAAA.mp3",
            Title = "Polaris",
            Artist = "Zero 7",
            Album = "Simple Things",
            Genre = "Electronic",
            Composer = "Henry Binns",
            FileSize = 10_136_119,
            LengthMs = 288_235,
            Bitrate = 192,
            SampleRate = 44_100,
            Year = 2001,
            TrackNumber = 1,
            TotalTracks = 11,
            DiscNumber = 1,
            TotalDiscs = 1,
            Rating = 80,
            DateAddedUtc = Added,
            Dbid = 0x1122334455667788,
        });
        ITunesDbWriter.AddTrack(doc, new NewTrack
        {
            TrackId = 2,
            IpodPath = ":iPod_Control:Music:F00:BBBB.mp3",
            Title = "Distractions",
            Artist = "Zero 7",
            Album = "Simple Things",
            Genre = "Electronic",
            Composer = "Sam Hardaker",
            FileSize = 10_099_969,
            LengthMs = 316_421,
            Bitrate = 192,
            SampleRate = 44_100,
            Year = 2001,
            TrackNumber = 3,
            TotalTracks = 11,
            DiscNumber = 2,
            TotalDiscs = 2,
            Rating = 100,
            DateAddedUtc = Added,
            Dbid = 0x2233445566778899,
        });

        ITunesDbChunkTree.Normalize(doc.Root);
        var bytes = ITunesDbChunkTree.Serialize(doc);

        mountPoint = Path.Combine(AppContext.BaseDirectory, "oracle-out");
        var dir = Path.Combine(mountPoint, "iPod_Control", "iTunes");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "iTunesDB"), bytes);
        return bytes;
    }

    [Fact]
    public void Emit_reproduces_the_libgpod_blessed_bytes()
    {
        var bytes = EmitSampleLibrary(out _);
        var blessed = File.ReadAllBytes(Path.Combine(FixtureDir, "orgz-emitted.iTunesDB"));

        // The committed fixture is the exact output libgpod parsed correctly (golden below). If the
        // writer changes, these bytes diverge - regenerate BOTH via the oracle (README) so any writer
        // change is re-blessed, never silently accepted.
        Assert.Equal(blessed, bytes);
    }

    [Fact]
    public void Libgpod_reads_back_every_field()
    {
        var oracle = Environment.GetEnvironmentVariable("ORGZ_GPOD_DUMP");
        if (string.IsNullOrWhiteSpace(oracle) || !File.Exists(oracle))
        {
            // No libgpod oracle on this host (e.g. Windows CI). The byte-reproduction test above still
            // guarantees the writer emits the libgpod-blessed database; run this in the Docker image
            // from the README to exercise the live parser.
            return;
        }

        EmitSampleLibrary(out var mountPoint);

        var psi = new ProcessStartInfo(oracle, mountPoint)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        Assert.True(proc.ExitCode == 0, $"gpod_dump failed ({proc.ExitCode}): {stderr}");

        var actual = stdout.Replace("\r\n", "\n").Trim();
        var golden = File.ReadAllText(Path.Combine(FixtureDir, "libgpod-golden.jsonl")).Replace("\r\n", "\n").Trim();
        Assert.Equal(golden, actual);
    }
}
