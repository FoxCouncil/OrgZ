// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// Slice D: OrgZ must mutate a REAL iTunes-written iTunesDB (add/remove tracks + playlists) without
/// corrupting everything iTunes put there that OrgZ doesn't model - the type-4 album table, the type-5
/// built-in playlists, per-track sort MHODs, the full 5-dataset set. Point <c>ORGZ_REAL_ITUNESDB</c> at
/// a real device DB (e.g. a mounted iPod's <c>iPod_Control/iTunes/iTunesDB</c>) to exercise it; skipped
/// otherwise so CI stays hermetic and no personal library is committed.
///
///  * <see cref="Mutating_a_real_itunes_db_preserves_the_datasets_it_does_not_model"/> is the
///    PRESERVATION half - a byte-level dataset diff, Docker-free.
///  * <see cref="Libgpod_accepts_the_mutated_real_db_and_sees_the_changes"/> is the ACCEPTANCE half -
///    it feeds the mutated DB to libgpod's itdb_parse (via <c>ORGZ_GPOD_DUMP</c>) and checks the
///    independent oracle reads iTunes's untouched content plus OrgZ's edits. Proven on a real iPod
///    Video 5.5G database (2919 tracks, 274 albums) via WSL libgpod.
/// </summary>
public class ITunesDbRealMutationTests
{
    private const string NewTitle = "OrgZ Slice D";

    private static string? RealDbPath =>
        Environment.GetEnvironmentVariable("ORGZ_REAL_ITUNESDB") is { } p && File.Exists(p) ? p : null;

    /// <summary>Add a track, add a user playlist over it, remove an existing music track - the whole
    /// point being that OrgZ starts from iTunes's own database, not <c>CreateEmpty</c>.</summary>
    private static byte[] Mutate(byte[] original, out uint addedId, out uint removedId)
    {
        var doc = ITunesDbChunkTree.Parse(original);
        ITunesDbReader.ReadAll(original, @"X:\", out var origTracks, out _);
        Assert.NotEmpty(origTracks);

        addedId = ITunesDbWriter.NextTrackId(doc);
        ITunesDbWriter.AddTrack(doc, new NewTrack
        {
            TrackId = addedId,
            IpodPath = ":iPod_Control:Music:F99:ORGZ.mp3",
            Title = NewTitle,
            Artist = "OrgZ",
            Album = "OrgZ",
            FileSize = 1_000_000,
            LengthMs = 60_000,
            Bitrate = 128,
            SampleRate = 44_100,
            DateAddedUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Dbid = 0x0126_0126_0126_0126,
        });
        ITunesDbWriter.AddPlaylist(doc, NewTitle, [addedId]);
        removedId = origTracks[0].TrackId;
        Assert.True(ITunesDbWriter.RemoveTrack(doc, removedId));

        ITunesDbChunkTree.Normalize(doc.Root);
        return ITunesDbChunkTree.Serialize(doc);
    }

    [Fact]
    public void Mutating_a_real_itunes_db_preserves_the_datasets_it_does_not_model()
    {
        var path = RealDbPath;
        if (path is null)
        {
            return;   // gated - see class summary
        }
        var original = File.ReadAllBytes(path);

        var origAlbums = DatasetBytes(original, 4);      // album table
        var origBuiltins = DatasetBytes(original, 5);    // built-in playlists
        ITunesDbReader.ReadAll(original, @"X:\", out var origTracks, out _);

        var mutated = Mutate(original, out uint addedId, out uint removedId);

        // PRESERVATION: the datasets OrgZ doesn't model survive byte-for-byte.
        Assert.Equal(origAlbums, DatasetBytes(mutated, 4));
        Assert.Equal(origBuiltins, DatasetBytes(mutated, 5));

        // CHANGES applied and readable: +1 new track, -1 removed, new user playlist present.
        ITunesDbReader.ReadAll(mutated, @"X:\", out var newTracks, out var newPlaylists);
        Assert.Contains(newTracks, t => t.TrackId == addedId);
        Assert.DoesNotContain(newTracks, t => t.TrackId == removedId);
        Assert.Equal(origTracks.Count, newTracks.Count);   // +1 add, -1 remove nets to zero
        Assert.Contains(newPlaylists, pl => pl.Name == NewTitle);
    }

    [Fact]
    public void Libgpod_accepts_the_mutated_real_db_and_sees_the_changes()
    {
        var path = RealDbPath;
        var oracle = Environment.GetEnvironmentVariable("ORGZ_GPOD_DUMP");
        if (path is null || string.IsNullOrWhiteSpace(oracle) || !File.Exists(oracle))
        {
            return;   // gated - needs both a real DB and a built gpod_dump (see oracle/README.md)
        }

        var original = File.ReadAllBytes(path);
        ITunesDbReader.ReadAll(original, @"X:\", out var origTracks, out _);
        var mutated = Mutate(original, out uint addedId, out uint removedId);

        var mount = Path.Combine(Path.GetTempPath(), "orgz-sliceD-" + Guid.NewGuid().ToString("N"));
        var itDir = Path.Combine(mount, "iPod_Control", "iTunes");
        Directory.CreateDirectory(itDir);
        File.WriteAllBytes(Path.Combine(itDir, "iTunesDB"), mutated);

        try
        {
            var psi = new ProcessStartInfo(oracle, mount)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            // libgpod must ACCEPT the mutated real database at all - that's the structural-integrity gate.
            Assert.True(proc.ExitCode == 0, $"libgpod rejected the mutated real DB ({proc.ExitCode}): {stderr}");

            var lines = stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            int trackLines = lines.Count(l => l.Contains("\"kind\":\"track\""));

            // Untouched content preserved + changes applied, as libgpod sees them.
            Assert.Equal(origTracks.Count, trackLines);                                  // +1 add, -1 remove
            Assert.Contains(lines, l => l.Contains($"\"id\":{addedId},"));               // new track present
            Assert.DoesNotContain(lines, l => l.Contains($"\"id\":{removedId},"));       // removed track gone
            Assert.Contains(lines, l => l.Contains("\"kind\":\"playlist\"") && l.Contains($"\"name\":\"{NewTitle}\"")
                                        && l.Contains($"[{addedId}]"));                  // new playlist over the new track
        }
        finally
        {
            try { Directory.Delete(mount, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>Returns the bytes of the MHSD with the given dataset type (0 length if absent).</summary>
    private static byte[] DatasetBytes(byte[] db, int type)
    {
        int pos = ReadInt32(db, 4);   // mhbd header size → first mhsd
        while (pos + 16 <= db.Length && db[pos] == (byte)'m' && db[pos + 1] == (byte)'h'
               && db[pos + 2] == (byte)'s' && db[pos + 3] == (byte)'d')
        {
            int total = ReadInt32(db, pos + 8);
            if (ReadInt32(db, pos + 12) == type)
            {
                return db[pos..(pos + total)];
            }
            pos += total;
        }
        return [];
    }

    private static int ReadInt32(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
}
