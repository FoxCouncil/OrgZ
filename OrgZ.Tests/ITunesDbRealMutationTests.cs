// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// Slice D: OrgZ must mutate a REAL iTunes-written iTunesDB (add/remove tracks + playlists) without
/// corrupting everything iTunes put there that OrgZ doesn't model - the type-4 album table, the type-5
/// built-in playlists, per-track sort MHODs, the full 5-dataset set. Point <c>ORGZ_REAL_ITUNESDB</c> at
/// a real device DB (e.g. <c>/Volumes/BriPod/iPod_Control/iTunes/iTunesDB</c>) to exercise it; skipped
/// otherwise so CI stays hermetic and no personal library is committed.
///
/// This is the PRESERVATION half (a byte-level dataset diff - Docker-free). The ACCEPTANCE half
/// (libgpod reading the mutated DB back field-exact) lives with the oracle harness.
/// </summary>
public class ITunesDbRealMutationTests
{
    private static string? RealDbPath =>
        Environment.GetEnvironmentVariable("ORGZ_REAL_ITUNESDB") is { } p && File.Exists(p) ? p : null;

    [Fact]
    public void Mutating_a_real_itunes_db_preserves_the_datasets_it_does_not_model()
    {
        var path = RealDbPath;
        if (path is null)
        {
            // Gated: set ORGZ_REAL_ITUNESDB to a real device iTunesDB (e.g. a mounted iPod) to run this.
            return;
        }
        var original = File.ReadAllBytes(path);

        // Baseline: the album table (type 4) and built-in playlists (type 5) - the rich datasets OrgZ's
        // CreateEmpty never authors - plus the track/playlist counts a reader sees.
        var origAlbums = DatasetBytes(original, 4);
        var origBuiltins = DatasetBytes(original, 5);
        ITunesDbReader.ReadAll(original, @"X:\", out var origTracks, out _);
        Assert.NotEmpty(origTracks);

        // Mutate: add a track, add a user playlist over it, remove an existing MUSIC track.
        var doc = ITunesDbChunkTree.Parse(original);
        uint newId = ITunesDbWriter.NextTrackId(doc);
        ITunesDbWriter.AddTrack(doc, new NewTrack
        {
            TrackId = newId,
            IpodPath = ":iPod_Control:Music:F99:ORGZ.mp3",
            Title = "OrgZ Slice D",
            Artist = "OrgZ",
            Album = "OrgZ",
            FileSize = 1_000_000,
            LengthMs = 60_000,
            Bitrate = 128,
            SampleRate = 44_100,
            DateAddedUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Dbid = 0x0126_0126_0126_0126,
        });
        ITunesDbWriter.AddPlaylist(doc, "OrgZ Slice D", [newId]);
        uint removeId = origTracks[0].TrackId;
        Assert.True(ITunesDbWriter.RemoveTrack(doc, removeId));

        ITunesDbChunkTree.Normalize(doc.Root);
        var mutated = ITunesDbChunkTree.Serialize(doc);

        // PRESERVATION: the datasets OrgZ doesn't model survive byte-for-byte.
        Assert.Equal(origAlbums, DatasetBytes(mutated, 4));
        Assert.Equal(origBuiltins, DatasetBytes(mutated, 5));

        // CHANGES applied and readable: +1 new track, -1 removed, new user playlist present.
        ITunesDbReader.ReadAll(mutated, @"X:\", out var newTracks, out var newPlaylists);
        Assert.Contains(newTracks, t => t.TrackId == newId);
        Assert.DoesNotContain(newTracks, t => t.TrackId == removeId);
        Assert.Equal(origTracks.Count, newTracks.Count);   // +1 add, -1 remove nets to zero
        Assert.Contains(newPlaylists, pl => pl.Name == "OrgZ Slice D");
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
