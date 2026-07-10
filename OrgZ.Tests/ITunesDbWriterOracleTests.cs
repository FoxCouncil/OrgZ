// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// Slice C: verifies OrgZ's iTunesDB WRITER against an INDEPENDENT oracle - libgpod's own
/// <c>itdb_parse</c>, via <c>OrgZ.Tests/oracle/gpod_dump.c</c>. OrgZ emits a database; libgpod reads
/// it back; the fields must match what OrgZ wrote. Never verified with OrgZ's own reader - that is the
/// circularity that let real bugs hide (mhbd dataset-count = 0 → unreadable; MHIPs with no type-100
/// position MHOD → an empty on-device song list). See the oracle README for how to regenerate.
///
/// Each scenario has two committed proofs:
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

    /// <summary>scenario key, committed emitted-DB fixture, committed libgpod golden.</summary>
    public static IEnumerable<object[]> Scenarios =>
    [
        ["simple", "orgz-emitted.iTunesDB", "libgpod-golden.jsonl"],
        ["edit", "orgz-emitted-edit.iTunesDB", "libgpod-golden-edit.jsonl"],
        ["podcast", "orgz-emitted-podcast.iTunesDB", "libgpod-golden-podcast.jsonl"],
    ];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Emit_reproduces_the_libgpod_blessed_bytes(string scenario, string fixture, string golden)
    {
        _ = golden;
        var bytes = EmitScenario(scenario, out _);
        var blessed = File.ReadAllBytes(Path.Combine(FixtureDir, fixture));

        // The committed fixture is the exact output libgpod parsed correctly. If the writer changes,
        // these bytes diverge - regenerate BOTH the fixture and the golden via the oracle (README) so a
        // writer change is re-blessed, never silently accepted.
        Assert.Equal(blessed, bytes);
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Libgpod_reads_back_every_field(string scenario, string fixture, string golden)
    {
        _ = fixture;
        var oracle = Environment.GetEnvironmentVariable("ORGZ_GPOD_DUMP");
        if (string.IsNullOrWhiteSpace(oracle) || !File.Exists(oracle))
        {
            // No libgpod oracle on this host (e.g. Windows CI). The byte-reproduction test still
            // guarantees the writer emits the libgpod-blessed database; run this in the Docker image
            // from the README to exercise the live parser.
            return;
        }

        EmitScenario(scenario, out var mountPoint);

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
        var goldenText = File.ReadAllText(Path.Combine(FixtureDir, golden)).Replace("\r\n", "\n").Trim();
        Assert.Equal(goldenText, actual);
    }

    // ── scenarios ─────────────────────────────────────────────────────────────

    [Fact]
    public void Podcast_grouping_nests_episodes_under_the_show_header()
    {
        // libgpod confirms the episodes are Podcasts members; this pins the show->episode GROUPING that
        // the firmware renders: one group-header MHIP (grouping flag 0x100) and every episode MHIP
        // pointing back at it via its 0x20 grouping ref, each carrying the type-100 membership MHOD.
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDir, "orgz-emitted-podcast.iTunesDB"));
        var doc = ITunesDbChunkTree.Parse(bytes);

        var podcasts = doc.Root.Children
            .Where(m => m.Magic == "mhsd" && m.ReadHeaderInt32(0x0C) == 3)
            .SelectMany(m => m.Children.Where(c => c.Magic == "mhyp"))
            .Single(p => p.Header.Length > 0x2B && (p.Header[0x2A] | (p.Header[0x2B] << 8)) == 1);

        var mhips = podcasts.Children.Where(c => c.Magic == "mhip").ToList();
        var header = Assert.Single(mhips, m => m.ReadHeaderInt32(0x10) == 0x100);   // one show group header
        int groupId = header.ReadHeaderInt32(0x14);
        var episodes = mhips.Where(m => m.ReadHeaderInt32(0x18) != 0).ToList();           // MHIPs that reference a track

        Assert.Equal(2, episodes.Count);
        Assert.All(episodes, ep => Assert.Equal(groupId, ep.ReadHeaderInt32(0x20)));      // nested under the show
        Assert.All(episodes, ep => Assert.Contains(ep.Children,
            c => c.Magic == "mhod" && c.ReadHeaderInt32(0x0C) == 100));                   // + membership MHOD
    }

    internal static byte[] EmitScenario(string scenario, out string mountPoint)
    {
        var doc = scenario switch
        {
            "simple" => BuildSimple(),
            "edit" => BuildEdit(),
            "podcast" => BuildPodcast(),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "unknown oracle scenario"),
        };

        ITunesDbChunkTree.Normalize(doc.Root);
        var bytes = ITunesDbChunkTree.Serialize(doc);

        mountPoint = Path.Combine(AppContext.BaseDirectory, "oracle-out", scenario);
        var dir = Path.Combine(mountPoint, "iPod_Control", "iTunes");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "iTunesDB"), bytes);
        return bytes;
    }

    /// <summary>Two tracks in the library, every load-bearing field set.</summary>
    private static ITunesDbDocument BuildSimple()
    {
        var doc = ITunesDbWriter.CreateEmpty();
        ITunesDbWriter.AddTrack(doc, Track(1, "AAAA", "Polaris", "Henry Binns", 1, 1, 1, 80, 10_136_119, 288_235, 0x1122334455667788));
        ITunesDbWriter.AddTrack(doc, Track(2, "BBBB", "Distractions", "Sam Hardaker", 3, 2, 2, 100, 10_099_969, 316_421, 0x2233445566778899));
        return doc;
    }

    /// <summary>Three tracks; a user playlist over a re-ordered subset; then a track removed - exercises
    /// add + user-playlist membership/order + remove (mhit and its MHIPs), all seen by libgpod.</summary>
    private static ITunesDbDocument BuildEdit()
    {
        var doc = ITunesDbWriter.CreateEmpty();
        ITunesDbWriter.AddTrack(doc, Track(1, "AAAA", "Polaris", "Henry Binns", 1, 1, 1, 80, 10_136_119, 288_235, 0x1122334455667788));
        ITunesDbWriter.AddTrack(doc, Track(2, "BBBB", "Distractions", "Sam Hardaker", 3, 2, 2, 100, 10_099_969, 316_421, 0x2233445566778899));
        ITunesDbWriter.AddTrack(doc, Track(3, "CCCC", "In the Waiting Line", "Zero 7", 4, 1, 1, 60, 9_500_000, 271_000, 0x33445566778899AA));

        ITunesDbWriter.AddPlaylist(doc, "Road Trip", [3, 1]);   // user list, re-ordered subset
        ITunesDbWriter.RemoveTrack(doc, 2);                     // drop track 2 (and its master MHIP)
        return doc;
    }

    /// <summary>Two podcast episodes under one show + an audiobook - exercises media kinds (4/8), the
    /// bookmark/unplayed flags, the podcast episode fields (release date, UTF-8 feed/enclosure URLs) and
    /// the Podcasts-playlist membership + grouping that libgpod reads back.</summary>
    private static ITunesDbDocument BuildPodcast()
    {
        var doc = ITunesDbWriter.CreateEmpty();
        ITunesDbWriter.AddTrack(doc, Episode(1, "Ep One"), addToMasterPlaylists: false);
        ITunesDbWriter.AddTrack(doc, Episode(2, "Ep Two"), addToMasterPlaylists: false);
        ITunesDbWriter.AddTrack(doc, new NewTrack
        {
            TrackId = 3,
            IpodPath = ":iPod_Control:Music:F00:AB3.m4b",
            Title = "My Book",
            Artist = "Author",
            Album = "The Book",
            IsAudiobook = true,
            FileSize = 50_000_000,
            LengthMs = 36_000_000,
            Bitrate = 64,
            SampleRate = 44_100,
            DateAddedUtc = Added,
            Dbid = 0x2000_0000_0000_0003,
        });
        ITunesDbWriter.EnsurePodcastPlaylist(doc, new (string, uint)[] { ("The Show", 1u), ("The Show", 2u) });
        return doc;
    }

    private static NewTrack Episode(uint id, string title) => new()
    {
        TrackId = id,
        IpodPath = $":iPod_Control:Music:F00:EP{id}.mp3",
        Title = title,
        Artist = "Podcaster",
        Album = "The Show",
        IsPodcast = true,
        Description = "An episode.",
        PodcastUrl = "http://example.com/ep.mp3",
        PodcastRss = "http://example.com/feed.xml",
        TimeReleased = Added,
        FileSize = 5_000_000,
        LengthMs = 1_800_000,
        Bitrate = 128,
        SampleRate = 44_100,
        DateAddedUtc = Added,
        Dbid = 0x1000_0000_0000_0000UL | id,
    };

    private static NewTrack Track(uint id, string file, string title, string composer, int trackNo,
        int disc, int totalDiscs, int rating, long size, int lengthMs, ulong dbid) => new()
    {
        TrackId = id,
        IpodPath = $":iPod_Control:Music:F00:{file}.mp3",
        Title = title,
        Artist = "Zero 7",
        Album = "Simple Things",
        Genre = "Electronic",
        Composer = composer,
        FileSize = size,
        LengthMs = lengthMs,
        Bitrate = 192,
        SampleRate = 44_100,
        Year = 2001,
        TrackNumber = trackNo,
        TotalTracks = 11,
        DiscNumber = disc,
        TotalDiscs = totalDiscs,
        Rating = rating,
        DateAddedUtc = Added,
        Dbid = dbid,
    };
}
