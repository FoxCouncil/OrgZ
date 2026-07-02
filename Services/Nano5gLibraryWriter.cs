// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace OrgZ.Services;

/// <summary>
/// Inserts tracks into an iPod Nano 5G <c>iTunes Library.itlp</c> SQLite set
/// (Library.itdb + Locations.itdb + Dynamic.itdb), mirroring the row pattern iTunes
/// writes, then re-signs <c>Locations.itdb.cbk</c> via <see cref="ITunesLocationsCbk"/>.
///
/// Schema reverse-engineered from a real Nano 5G. Notes:
///  - <c>item</c>/<c>album</c>/<c>artist</c> use random 64-bit persistent ids; <c>composer</c>,
///    <c>track_artist</c>, and <c>genre_map</c> use small sequential ids (matching iTunes).
///  - timestamps are CFAbsoluteTime (seconds since 2001-01-01 UTC).
///  - <c>location.location_type</c> is the FourCC "FILE" (0x46494C45); <c>extension</c> is the
///    upper-cased file extension as a space-padded big-endian FourCC ("MP3 " = 0x4D503320).
///  - the per-device hash72 seed (iv/rnd) is recovered from the *current* (consistent) cbk
///    before editing, then used to re-sign the updated Locations.itdb.
/// </summary>
public sealed class Nano5gLibraryWriter
{
    public sealed record TrackInsert(
        string Title,
        string Artist,
        string Album,
        string? AlbumArtist,
        string? Genre,
        long DurationMs,
        int TrackNumber,
        int DiscNumber,
        int Year,
        int AudioFormat,       // e.g. 301 = MP3
        int BitRate,
        int SampleRate,
        int Channels,
        long FileSize,
        string LocationRelative, // e.g. "F12/ABCD.m4a" under iPod_Control/Music
        int ExtensionFourCc,     // FourCC of the extension: "MP3 "=0x4D503320, "M4A "=0x4D344120
        string KindString);      // location_kind_map kind, e.g. "MPEG audio file", "Apple Lossless audio file"

    private const int FourCcFile = 0x46494C45; // "FILE"

    private readonly string _itlpDir;
    private readonly string? _fireWireGuid;

    /// <param name="fireWireGuid">Device FireWire GUID - required to re-sign the iTunesCDB (hash58).
    /// Operations that only touch the SQLite stack + cbk (hash72) work without it.</param>
    public Nano5gLibraryWriter(string itlpDir, string? fireWireGuid = null)
    {
        _itlpDir = itlpDir;
        _fireWireGuid = fireWireGuid;
    }

    // Open-batch depth PER LIBRARY PATH: while > 0, mutations on that library skip their per-call
    // RegenerateCdb - a full library re-read + CDB recompress/re-sign - and the batch scope runs it
    // ONCE on dispose. Keyed by itlp path (not per writer instance) because batch flows like the
    // VM's playlist sync construct a fresh writer per track through the importer; every instance
    // over the same library must participate. Concurrent map only so parallel TESTS over different
    // temp libraries don't collide - real device writes are UI-serialized.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _cdbDeferDepth = new(StringComparer.OrdinalIgnoreCase);

    private string DeferKey => Path.GetFullPath(_itlpDir);

    /// <summary>
    /// Defers the (expensive, whole-library) CDB regeneration across a batch of mutations on this
    /// library: per-call <see cref="RegenerateCdb"/> is skipped while the returned scope is alive -
    /// including calls from OTHER writer instances over the same itlp - and disposing it
    /// regenerates once, so a batch of N inserts costs one rebuild instead of N. The dispose runs
    /// even when the batch partially fails, keeping the CDB consistent with whatever the SQLite
    /// stack actually holds.
    /// </summary>
    public IDisposable BeginCdbBatch()
    {
        _cdbDeferDepth.AddOrUpdate(DeferKey, 1, static (_, depth) => depth + 1);
        return new CdbBatchScope(this);
    }

    private sealed class CdbBatchScope(Nano5gLibraryWriter writer) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            var key = writer.DeferKey;
            int depth = _cdbDeferDepth.AddOrUpdate(key, 0, static (_, d) => d - 1);
            if (depth <= 0)
            {
                _cdbDeferDepth.TryRemove(key, out _);
                writer.RegenerateCdb();
            }
        }
    }

    private void RegenerateCdbUnlessDeferred()
    {
        if (!_cdbDeferDepth.TryGetValue(DeferKey, out var depth) || depth <= 0)
        {
            RegenerateCdb();
        }
    }

    /// <summary>Inserts one track across the three databases and re-signs the cbk. Returns its item pid.</summary>
    public long AddTrack(TrackInsert t)
    {
        var libPath = Path.Combine(_itlpDir, "Library.itdb");
        var locPath = Path.Combine(_itlpDir, "Locations.itdb");
        var dynPath = Path.Combine(_itlpDir, "Dynamic.itdb");
        var cbkPath = locPath + ".cbk";

        // Recover the hash72 seed from the cbk while it still matches Locations.itdb.
        if (!ITunesLocationsCbk.TryExtractSeed(File.ReadAllBytes(locPath), File.ReadAllBytes(cbkPath), out var iv, out var rnd))
        {
            throw new InvalidOperationException("Could not recover the device's hash72 seed from Locations.itdb.cbk.");
        }

        long itemPid;
        long kindId;
        using (var lib = Open(libPath))
        {
            using var tx = lib.BeginTransaction();

            long containerPid = ScalarLong(lib, "SELECT primary_container_pid FROM db_info LIMIT 1");
            long artistPid = FindOrCreateArtist(lib, t.Artist);
            long albumPid = FindOrCreateAlbum(lib, t.Album, artistPid);
            long trackArtistPid = FindOrCreateTrackArtist(lib, t.Artist);
            long genreId = string.IsNullOrEmpty(t.Genre) ? 0 : FindOrCreateGenre(lib, t.Genre!);
            kindId = FindOrCreateLocationKind(lib, t.KindString);   // location_kind_map lives in Library.itdb
            itemPid = NewRandomPid(lib, "item");

            Exec(lib, """
                INSERT INTO item
                  (pid, media_kind, is_song, date_modified, year, total_time_ms, track_number, disc_number,
                   genre_id, album_pid, artist_pid, track_artist_pid,
                   title, artist, album, album_artist, sort_title, sort_artist, sort_album,
                   title_order, artist_order, album_order, genre_order, album_artist_order, physical_order)
                VALUES
                  ($pid, 1, 1, $dm, $yr, $dur, $tn, $dn,
                   $gid, $apid, $arpid, $tapid,
                   $title, $artist, $album, $aa, $title, $artist, $album,
                   100, 100, 100, 100, 100, $po)
                """,
                ("$pid", itemPid), ("$dm", Cf2001Now()), ("$yr", t.Year), ("$dur", t.DurationMs),
                ("$tn", t.TrackNumber), ("$dn", t.DiscNumber), ("$gid", genreId),
                ("$apid", albumPid), ("$arpid", artistPid), ("$tapid", trackArtistPid),
                ("$title", t.Title), ("$artist", t.Artist), ("$album", t.Album),
                ("$aa", (object?)t.AlbumArtist ?? DBNull.Value),
                ("$po", ScalarLong(lib, "SELECT COALESCE(MAX(physical_order),0)+1 FROM item")));

            Exec(lib, """
                INSERT INTO avformat_info (item_pid, sub_id, audio_format, bit_rate, channels, sample_rate, duration)
                VALUES ($pid, 0, $fmt, $br, $ch, $sr, $dur)
                """,
                ("$pid", itemPid), ("$fmt", t.AudioFormat), ("$br", t.BitRate),
                ("$ch", t.Channels), ("$sr", (double)t.SampleRate), ("$dur", t.DurationMs * 1000));

            Exec(lib, """
                INSERT INTO item_to_container (item_pid, container_pid, physical_order, shuffle_order)
                VALUES ($pid, $cpid, $po, NULL)
                """,
                ("$pid", itemPid), ("$cpid", containerPid),
                ("$po", ScalarLong(lib, "SELECT COALESCE(MAX(physical_order),0)+1 FROM item_to_container WHERE container_pid=$c", ("$c", containerPid))));

            tx.Commit();
        }

        using (var loc = Open(locPath))
        {
            using var tx = loc.BeginTransaction();
            long baseId = ScalarLong(loc, "SELECT COALESCE((SELECT id FROM base_location WHERE path='iPod_Control/Music' LIMIT 1), 1)");
            Exec(loc, """
                INSERT INTO location
                  (item_pid, sub_id, base_location_id, location_type, location, extension, kind_id, date_created, file_size)
                VALUES ($pid, 0, $base, $lt, $loc, $ext, $kind, $dc, $fs)
                """,
                ("$pid", itemPid), ("$base", baseId), ("$lt", FourCcFile), ("$loc", t.LocationRelative),
                ("$ext", t.ExtensionFourCc), ("$kind", kindId), ("$dc", Cf2001Now()), ("$fs", t.FileSize));
            tx.Commit();
        }

        if (File.Exists(dynPath))
        {
            using var dyn = Open(dynPath);
            using var tx = dyn.BeginTransaction();
            Exec(dyn, "INSERT INTO item_stats (item_pid) VALUES ($pid)", ("$pid", itemPid));
            tx.Commit();
        }

        // Re-sign the now-modified Locations.itdb with the recovered seed.
        File.WriteAllBytes(cbkPath, ITunesLocationsCbk.Build(File.ReadAllBytes(locPath), iv, rnd));
        RegenerateCdbUnlessDeferred();
        return itemPid;
    }

    public sealed record PodcastInsert(
        string Title,            // episode title
        string ShowName,         // podcast show - used as artist + album so episodes group by show
        string? Description,     // episode show-notes
        string? FeedUrl,         // subscription / feed URL
        string? ExternalGuid,    // the RSS <guid> of the episode
        DateTime? ReleasedUtc,   // episode publish date
        long DurationMs,
        int AudioFormat,         // e.g. 301 = MP3, 502 = ALAC
        int BitRate,
        int SampleRate,
        int Channels,
        long FileSize,
        string LocationRelative, // e.g. "F12/ABCD.mp3" under iPod_Control/Music
        int ExtensionFourCc,     // FourCC of the extension: "MP3 "=0x4D503320
        string KindString);      // location_kind_map kind, e.g. "MPEG audio file"

    /// <summary>
    /// Inserts one podcast episode: a <c>media_kind=4</c> / <c>is_podcast=1</c> item plus its
    /// avformat / location / <c>podcast_info</c> rows, linked into the device's Podcasts container
    /// (<c>distinguished_kind=11</c>, created on first use) - which is what populates the firmware's
    /// Podcasts menu. Re-signs the cbk. Returns the episode's item pid.
    ///
    /// media_kind/is_podcast/container values are from libgpod (itdb_sqlite.c); the per-row
    /// podcast_info population mirrors the binary iTunesDB podcast fields and is the part most worth
    /// confirming on hardware.
    /// </summary>
    public long AddPodcastEpisode(PodcastInsert p)
    {
        var libPath = Path.Combine(_itlpDir, "Library.itdb");
        var locPath = Path.Combine(_itlpDir, "Locations.itdb");
        var dynPath = Path.Combine(_itlpDir, "Dynamic.itdb");
        var cbkPath = locPath + ".cbk";

        if (!ITunesLocationsCbk.TryExtractSeed(File.ReadAllBytes(locPath), File.ReadAllBytes(cbkPath), out var iv, out var rnd))
        {
            throw new InvalidOperationException("Could not recover the device's hash72 seed from Locations.itdb.cbk.");
        }

        long itemPid;
        long kindId;
        using (var lib = Open(libPath))
        {
            using var tx = lib.BeginTransaction();

            long artistPid = FindOrCreateArtist(lib, p.ShowName);
            long albumPid = FindOrCreateAlbum(lib, p.ShowName, artistPid);
            long trackArtistPid = FindOrCreateTrackArtist(lib, p.ShowName);
            kindId = FindOrCreateLocationKind(lib, p.KindString);
            itemPid = NewRandomPid(lib, "item");

            long released = p.ReleasedUtc is { } dt ? Cf2001(dt) : 0;
            int year = p.ReleasedUtc?.Year ?? 0;

            // media_kind=4 (ITDB_MEDIATYPE_PODCAST), is_song=0, is_podcast=1; show name fills
            // artist/album/grouping so the Podcasts menu lists episodes under their show.
            Exec(lib, """
                INSERT INTO item
                  (pid, media_kind, is_song, is_podcast, date_modified, date_released, year,
                   total_time_ms, genre_id, album_pid, artist_pid, track_artist_pid,
                   title, artist, album, album_artist, sort_title, sort_artist, sort_album,
                   description, grouping,
                   title_order, artist_order, album_order, genre_order, album_artist_order, physical_order)
                VALUES
                  ($pid, 4, 0, 1, $dm, $rel, $yr,
                   $dur, 0, $apid, $arpid, $tapid,
                   $title, $show, $show, $show, $title, $show, $show,
                   $desc, $show,
                   100, 100, 100, 100, 100, $po)
                """,
                ("$pid", itemPid), ("$dm", Cf2001Now()), ("$rel", released), ("$yr", year),
                ("$dur", p.DurationMs), ("$apid", albumPid), ("$arpid", artistPid), ("$tapid", trackArtistPid),
                ("$title", p.Title), ("$show", p.ShowName),
                ("$desc", (object?)p.Description ?? DBNull.Value),
                ("$po", ScalarLong(lib, "SELECT COALESCE(MAX(physical_order),0)+1 FROM item")));

            Exec(lib, """
                INSERT INTO avformat_info (item_pid, sub_id, audio_format, bit_rate, channels, sample_rate, duration)
                VALUES ($pid, 0, $fmt, $br, $ch, $sr, $dur)
                """,
                ("$pid", itemPid), ("$fmt", p.AudioFormat), ("$br", p.BitRate),
                ("$ch", p.Channels), ("$sr", (double)p.SampleRate), ("$dur", p.DurationMs * 1000));

            Exec(lib, """
                INSERT INTO podcast_info (item_pid, date_released, external_guid, feed_url, feed_keywords)
                VALUES ($pid, $rel, $guid, $feed, NULL)
                """,
                ("$pid", itemPid), ("$rel", released),
                ("$guid", (object?)p.ExternalGuid ?? DBNull.Value),
                ("$feed", (object?)p.FeedUrl ?? DBNull.Value));

            long podcastContainerPid = EnsurePodcastContainer(lib);
            Exec(lib, """
                INSERT INTO item_to_container (item_pid, container_pid, physical_order, shuffle_order)
                VALUES ($pid, $cpid, $po, NULL)
                """,
                ("$pid", itemPid), ("$cpid", podcastContainerPid),
                ("$po", ScalarLong(lib, "SELECT COALESCE(MAX(physical_order),0)+1 FROM item_to_container WHERE container_pid=$c", ("$c", podcastContainerPid))));

            tx.Commit();
        }

        using (var loc = Open(locPath))
        {
            using var tx = loc.BeginTransaction();
            long baseId = ScalarLong(loc, "SELECT COALESCE((SELECT id FROM base_location WHERE path='iPod_Control/Music' LIMIT 1), 1)");
            Exec(loc, """
                INSERT INTO location
                  (item_pid, sub_id, base_location_id, location_type, location, extension, kind_id, date_created, file_size)
                VALUES ($pid, 0, $base, $lt, $loc, $ext, $kind, $dc, $fs)
                """,
                ("$pid", itemPid), ("$base", baseId), ("$lt", FourCcFile), ("$loc", p.LocationRelative),
                ("$ext", p.ExtensionFourCc), ("$kind", kindId), ("$dc", Cf2001Now()), ("$fs", p.FileSize));
            tx.Commit();
        }

        if (File.Exists(dynPath))
        {
            using var dyn = Open(dynPath);
            using var tx = dyn.BeginTransaction();
            Exec(dyn, "INSERT INTO item_stats (item_pid) VALUES ($pid)", ("$pid", itemPid));
            tx.Commit();
        }

        File.WriteAllBytes(cbkPath, ITunesLocationsCbk.Build(File.ReadAllBytes(locPath), iv, rnd));
        RegenerateCdbUnlessDeferred();
        return itemPid;
    }

    /// <summary>
    /// True when a podcast episode with this show + title is already in the library. The importer
    /// checks this before copying/inserting so re-syncing the same downloads doesn't duplicate
    /// episodes - the dedup key mirrors what <see cref="AddPodcastEpisode"/> writes (media_kind=4,
    /// album = show name, title = episode title).
    /// </summary>
    public bool PodcastEpisodeExists(string showName, string title)
    {
        using var lib = Open(Path.Combine(_itlpDir, "Library.itdb"));
        return ScalarLong(lib, "SELECT COUNT(*) FROM item WHERE media_kind=4 AND title=$t AND album=$s",
            ("$t", title), ("$s", showName)) > 0;
    }

    /// <summary>
    /// Finds the device's Podcasts container (<c>distinguished_kind=11</c>) or creates it by
    /// mirroring the primary container's columns, overriding identity, name, the podcast
    /// distinguished kind, the hidden flag, and a <c>media_kinds</c> mask carrying the podcast bit
    /// (4). This container is what the firmware surfaces as the Podcasts menu. Returns its pid.
    /// </summary>
    private long EnsurePodcastContainer(SqliteConnection lib)
    {
        var columns = GetColumns(lib, "container");
        long existing = columns.Contains("distinguished_kind", StringComparer.OrdinalIgnoreCase)
            ? ScalarLong(lib, "SELECT pid FROM container WHERE distinguished_kind=11 LIMIT 1")
            : 0;
        if (existing != 0) { return existing; }

        long primaryPid = ScalarLong(lib, "SELECT primary_container_pid FROM db_info LIMIT 1");
        var primary = ReadRow(lib, "SELECT * FROM container WHERE pid=$p", ("$p", primaryPid))
            ?? throw new InvalidOperationException("Nano 5G has no primary container to mirror.");
        long newPid = NewRandomPid(lib, "container");
        long now = Cf2001Now();

        var overrides = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["pid"] = newPid,
            ["name"] = "Podcasts",
            ["distinguished_kind"] = 11L,   // the firmware's Podcasts menu
            ["media_kinds"] = 4L,           // bit 2 = podcast
            ["parent_pid"] = 0L,
            ["date_created"] = now,
            ["date_modified"] = now,
            ["is_hidden"] = 1L,             // not a user playlist - it's the Podcasts menu
            ["smart_is_dynamic"] = 0L,
            ["smart_is_filtered"] = 0L,
            ["smart_is_genius"] = 0L,
        };

        InsertContainerMirroringPrimary(lib, columns, primary, overrides);
        return newPid;
    }

    /// <summary>
    /// Inserts a container row that mirrors the primary container's columns - inheriting every
    /// column this iPod/firmware version expects - with <paramref name="overrides"/> supplying the
    /// identity/name/kind fields. Shared by the Podcasts menu container and user playlists.
    /// </summary>
    private static void InsertContainerMirroringPrimary(SqliteConnection lib, List<string> columns,
        Dictionary<string, object?> primary, Dictionary<string, object?> overrides)
    {
        var cols = new List<string>(columns.Count);
        var ps = new List<(string, object)>(columns.Count);
        int n = 0;
        foreach (var col in columns)
        {
            object? val = overrides.TryGetValue(col, out var ov) ? ov : primary.GetValueOrDefault(col);
            var prm = "$v" + n++;
            cols.Add(col);
            ps.Add((prm, val ?? DBNull.Value));
        }
        Exec(lib, $"INSERT INTO container ({string.Join(",", cols)}) VALUES ({string.Join(",", ps.Select(x => x.Item1))})", ps.ToArray());
    }

    private static long Cf2001(DateTime utc)
        => (long)(utc.ToUniversalTime() - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

    /// <summary>
    /// Links an ArtworkDB image to a track - sets <c>artwork_status</c> + <c>artwork_cache_id</c> on
    /// the item row, which the firmware resolves to the ArtworkDB mhii of that image id. Library.itdb
    /// isn't checksummed, so no cbk re-sign is needed.
    /// </summary>
    public void SetArtwork(long itemPid, int artworkCacheId)
    {
        using var lib = Open(Path.Combine(_itlpDir, "Library.itdb"));
        using var tx = lib.BeginTransaction();
        Exec(lib, "UPDATE item SET artwork_status=1, artwork_cache_id=$c WHERE pid=$p", ("$c", artworkCacheId), ("$p", itemPid));

        // Cover Flow renders ALBUM art (album.artwork_item_pid -> a track that supplies the cover).
        // Point this track's album at it when the album has no cover yet (matches iTunes: status 0).
        long albumPid = ScalarLong(lib, "SELECT album_pid FROM item WHERE pid=$p", ("$p", itemPid));
        if (albumPid != 0)
        {
            Exec(lib, "UPDATE album SET artwork_status=0, artwork_item_pid=$i WHERE pid=$a AND COALESCE(artwork_item_pid,0)=0",
                ("$i", itemPid), ("$a", albumPid));
        }
        tx.Commit();
    }

    /// <summary>
    /// Creates a regular user playlist by mirroring the device's own primary container row -
    /// inheriting every column this iPod/firmware version expects - then overriding identity,
    /// name, and the distinguished/smart flags so it reads as an ordinary named playlist. Links
    /// the given item pids via <c>item_to_container</c> in order. Library.itdb isn't checksummed,
    /// so no cbk re-sign is needed. Returns the new container pid.
    /// </summary>
    public long CreatePlaylist(string name, IReadOnlyList<long> itemPids)
    {
        using var lib = Open(Path.Combine(_itlpDir, "Library.itdb"));
        using var tx = lib.BeginTransaction();

        long primaryPid = ScalarLong(lib, "SELECT primary_container_pid FROM db_info LIMIT 1");
        long newPid = NewRandomPid(lib, "container");

        var columns = GetColumns(lib, "container");
        var primary = ReadRow(lib, "SELECT * FROM container WHERE pid=$p", ("$p", primaryPid))
            ?? throw new InvalidOperationException("Nano 5G has no primary container to mirror.");

        // Idempotent re-sync: drop any existing same-name user playlist (+ its links) first.
        if (columns.Contains("name", StringComparer.OrdinalIgnoreCase))
        {
            var distinguishedClause = columns.Contains("distinguished_kind", StringComparer.OrdinalIgnoreCase) ? " AND distinguished_kind=0" : "";
            long dupe = ScalarLong(lib, $"SELECT pid FROM container WHERE name=$n{distinguishedClause} LIMIT 1", ("$n", name));
            if (dupe != 0)
            {
                Exec(lib, "DELETE FROM item_to_container WHERE container_pid=$p", ("$p", dupe));
                Exec(lib, "DELETE FROM container WHERE pid=$p", ("$p", dupe));
            }
        }

        long now = Cf2001Now();

        // media_kinds is the bitmask of the playlist's content kinds (1=music, 4=podcast, 8=audiobook).
        // We mirror the PRIMARY container, which is media_kinds=0 (the hidden library) - but a VISIBLE
        // playlist with 0 has no category for the firmware to file it under, so it never appears. OR
        // the members' kinds; default to music if somehow empty.
        long mediaKinds = 0;
        foreach (var itemPid in itemPids)
        {
            mediaKinds |= ScalarLong(lib, "SELECT COALESCE(media_kind,1) FROM item WHERE pid=$p", ("$p", itemPid));
        }
        if (mediaKinds == 0)
        {
            mediaKinds = 1;
        }

        var overrides = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["pid"] = newPid,
            ["name"] = name,
            ["distinguished_kind"] = 0L,   // 0 = ordinary playlist, not the distinguished library
            ["parent_pid"] = 0L,
            ["media_kinds"] = mediaKinds,  // categorise it, or the firmware hides it
            ["date_created"] = now,
            ["date_modified"] = now,
            ["is_hidden"] = 0L,
            ["smart_is_dynamic"] = 0L,
            ["smart_is_filtered"] = 0L,
            ["smart_is_genius"] = 0L,
        };

        InsertContainerMirroringPrimary(lib, columns, primary, overrides);

        long order = 0;
        foreach (var itemPid in itemPids)
        {
            Exec(lib, "INSERT INTO item_to_container (item_pid, container_pid, physical_order, shuffle_order) VALUES ($i,$c,$po,NULL)",
                ("$i", itemPid), ("$c", newPid), ("$po", order++));
        }

        tx.Commit();

        // Every playlist needs a container_ui row in Dynamic.itdb, or the firmware's playlist UI has no
        // state record for the container and silently skips it (the master has one; libgpod + iOpenPod
        // both write one per playlist). Dynamic.itdb isn't covered by the cbk, so no re-sign is needed.
        var dynPath = Path.Combine(_itlpDir, "Dynamic.itdb");
        if (File.Exists(dynPath))
        {
            using var dyn = Open(dynPath);
            using var dtx = dyn.BeginTransaction();
            Exec(dyn, "DELETE FROM container_ui WHERE container_pid=$c", ("$c", newPid));
            Exec(dyn, "INSERT INTO container_ui (container_pid, play_order, is_reversed, album_field_order, repeat_mode, shuffle_items, has_been_shuffled) VALUES ($c, 0, 0, 1, 0, 0, 0)", ("$c", newPid));
            dtx.Commit();
        }

        RegenerateCdbUnlessDeferred();
        return newPid;
    }

    /// <summary>
    /// Removes a track: deletes its rows from all three databases, deletes the audio file under
    /// <paramref name="musicRoot"/>, prunes now-orphaned artist/album/track_artist/genre rows, and
    /// re-signs the cbk. Safe no-op if the item isn't present.
    /// </summary>
    public void RemoveTrack(long itemPid, string musicRoot)
    {
        var libPath = Path.Combine(_itlpDir, "Library.itdb");
        var locPath = Path.Combine(_itlpDir, "Locations.itdb");
        var dynPath = Path.Combine(_itlpDir, "Dynamic.itdb");
        var cbkPath = locPath + ".cbk";

        if (!ITunesLocationsCbk.TryExtractSeed(File.ReadAllBytes(locPath), File.ReadAllBytes(cbkPath), out var iv, out var rnd))
        {
            throw new InvalidOperationException("Could not recover the device's hash72 seed from Locations.itdb.cbk.");
        }

        string? relative;
        using (var loc = Open(locPath))
        {
            relative = ScalarStr(loc, "SELECT location FROM location WHERE item_pid=$p", ("$p", itemPid));
            using var tx = loc.BeginTransaction();
            Exec(loc, "DELETE FROM location WHERE item_pid=$p", ("$p", itemPid));
            tx.Commit();
        }

        using (var lib = Open(libPath))
        {
            long albumPid = ScalarLong(lib, "SELECT album_pid FROM item WHERE pid=$p", ("$p", itemPid));
            long artistPid = ScalarLong(lib, "SELECT artist_pid FROM item WHERE pid=$p", ("$p", itemPid));
            long trackArtistPid = ScalarLong(lib, "SELECT track_artist_pid FROM item WHERE pid=$p", ("$p", itemPid));
            long genreId = ScalarLong(lib, "SELECT genre_id FROM item WHERE pid=$p", ("$p", itemPid));

            using var tx = lib.BeginTransaction();
            Exec(lib, "DELETE FROM item WHERE pid=$p", ("$p", itemPid));
            Exec(lib, "DELETE FROM avformat_info WHERE item_pid=$p", ("$p", itemPid));
            Exec(lib, "DELETE FROM item_to_container WHERE item_pid=$p", ("$p", itemPid));
            // Podcast episodes carry a podcast_info row (date_released / feed_url); drop it too so a
            // removed episode doesn't leave an orphan behind (music tracks simply have no row here).
            Exec(lib, "DELETE FROM podcast_info WHERE item_pid=$p", ("$p", itemPid));

            PruneIfOrphan(lib, "album", "album_pid", albumPid);
            PruneIfOrphan(lib, "artist", "artist_pid", artistPid);
            PruneIfOrphan(lib, "track_artist", "track_artist_pid", trackArtistPid);
            if (genreId != 0 && ScalarLong(lib, "SELECT COUNT(*) FROM item WHERE genre_id=$g", ("$g", genreId)) == 0)
            {
                Exec(lib, "DELETE FROM genre_map WHERE id=$g", ("$g", genreId));
            }
            tx.Commit();
        }

        if (File.Exists(dynPath))
        {
            using var dyn = Open(dynPath);
            using var tx = dyn.BeginTransaction();
            Exec(dyn, "DELETE FROM item_stats WHERE item_pid=$p", ("$p", itemPid));
            tx.Commit();
        }

        if (relative is not null)
        {
            var abs = Path.Combine(musicRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(abs)) { File.Delete(abs); }
        }

        File.WriteAllBytes(cbkPath, ITunesLocationsCbk.Build(File.ReadAllBytes(locPath), iv, rnd));
        RegenerateCdbUnlessDeferred();
    }

    /// <summary>
    /// Rewrites the firmware's MASTER database - the compressed, hash72-signed <c>iTunesCDB</c> - to
    /// reflect the current SQLite library. The Nano 5G's CDB cannot be generated from scratch: its
    /// default smart playlists (Music / Movies / TV Shows / Audiobooks / Rentals) use proprietary
    /// media_kind bitmasks that NO open-source tool (libgpod, iOpenPod) can produce - they only ever
    /// parse them. So OrgZ carries its OWN canonical 5-dataset skeleton (captured once from iTunes,
    /// embedded as <c>Assets/nano5g-cdb-skeleton.cdb</c>) and builds on it: clear its track list,
    /// inject the live library, recompress + re-sign with the device's seed. The legacy
    /// <c>iTunesDB</c> is zeroed so the firmware never sees two databases. Call after each mutation.
    /// </summary>
    public void RegenerateCdb()
    {
        var iTunesDir = Path.GetDirectoryName(_itlpDir)!;
        var cdbPath = Path.Combine(iTunesDir, "iTunesCDB");
        var legacyDbPath = Path.Combine(iTunesDir, "iTunesDB");

        var mountPath = Path.GetPathRoot(_itlpDir)!;   // e.g. "E:\"
        Nano5gLibraryReader.ReadAll(_itlpDir, mountPath, out var tracks, out _);

        // Build on OrgZ's embedded canonical iTunes skeleton (not whatever's on the device); clear its
        // tracks, then inject ours.
        var doc = Nano5gCdbWriter.FromTemplate(LoadCdbSkeleton());
        ITunesDbWriter.ClearLibrary(doc);
        ITunesDbWriter.EnsureType9Dataset(doc);   // iTunes expects 6 datasets; our skeleton ships 5

        var podcastEpisodes = new List<(string Show, uint TrackId)>();
        foreach (var t in tracks)
        {
            bool isPodcast = t.MediaType == ITunesMediaType.Podcast;
            uint trackId = ITunesDbWriter.AddMusicdbTrack(doc, new NewTrack
            {
                TrackId = 0,   // assigned inside AddMusicdbTrack from the shared id space
                IpodPath = ToIpodPath(t.FilePath, mountPath),
                Title = t.Title,
                Artist = t.Artist,
                Album = t.Album,
                Genre = t.Genre,
                FileSize = t.FileSize,
                LengthMs = t.DurationMs,
                Bitrate = t.Bitrate,
                SampleRate = t.SampleRate,
                Year = t.Year,
                TrackNumber = t.TrackNumber,
                DateAddedUtc = t.DateAdded ?? DateTime.UtcNow,
                Dbid = t.Dbid,
                IsPodcast = isPodcast,
                TimeReleased = t.DateReleased,
            }, addToMasterPlaylists: !isPodcast);
            if (isPodcast)
            {
                podcastEpisodes.Add((t.Album ?? t.Artist ?? "Podcast", trackId));
            }
        }

        if (podcastEpisodes.Count > 0)
        {
            ITunesDbWriter.EnsurePodcastPlaylist(doc, podcastEpisodes);
        }

        // User playlists are deliberately NOT written into the CDB: a user-playlist mhyp makes iTunes
        // reject the whole database ("cannot read the contents"), and it doesn't surface on the
        // firmware from the CDB anyway. They live in the SQLite container (CreatePlaylist) only, until
        // the correct CDB form is confirmed (research pending).

        File.WriteAllBytes(cdbPath, Nano5gCdbWriter.Emit(doc, _fireWireGuid));
        // The firmware won't tolerate a non-empty legacy iTunesDB alongside a CDB.
        File.WriteAllBytes(legacyDbPath, Array.Empty<byte>());
    }

    /// <summary>Device-absolute path -> iTunesDB colon form (":iPod_Control:Music:F00:x.m4a").</summary>
    private static string ToIpodPath(string? fullPath, string mountPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return "";
        }
        var rel = fullPath.StartsWith(mountPath, StringComparison.OrdinalIgnoreCase)
            ? fullPath[mountPath.Length..]
            : fullPath;
        return ":" + rel.Replace('/', '\\').TrimStart('\\').Replace('\\', ':');
    }

    /// <summary>Loads OrgZ's embedded canonical Nano 5G CDB skeleton - the iTunes 5-dataset structure
    /// + default smart playlists, captured once because no open-source tool can generate them (see
    /// <see cref="RegenerateCdb"/>).</summary>
    private static byte[] LoadCdbSkeleton()
    {
        using var stream = typeof(Nano5gLibraryWriter).Assembly.GetManifestResourceStream("OrgZ.nano5g-cdb-skeleton.cdb")
            ?? throw new InvalidOperationException("Embedded Nano 5G CDB skeleton (OrgZ.nano5g-cdb-skeleton.cdb) not found.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Empties the device's library - every track, album, artist, genre, podcast, and user
    /// playlist - leaving the schema, <c>db_info</c>/<c>version_info</c>, and the primary container
    /// intact, then re-signs the cbk. Caller deletes the audio files separately. This is the database
    /// half of "erase the iPod for fresh use".
    /// </summary>
    public void WipeLibrary()
    {
        var libPath = Path.Combine(_itlpDir, "Library.itdb");
        var locPath = Path.Combine(_itlpDir, "Locations.itdb");
        var dynPath = Path.Combine(_itlpDir, "Dynamic.itdb");
        var cbkPath = locPath + ".cbk";

        if (!ITunesLocationsCbk.TryExtractSeed(File.ReadAllBytes(locPath), File.ReadAllBytes(cbkPath), out var iv, out var rnd))
        {
            throw new InvalidOperationException("Could not recover the device's hash72 seed from Locations.itdb.cbk.");
        }

        using (var lib = Open(libPath))
        {
            long primary = ScalarLong(lib, "SELECT primary_container_pid FROM db_info LIMIT 1");
            using var tx = lib.BeginTransaction();

            // All content tables - anything that doesn't exist on a given firmware is skipped.
            foreach (var table in new[]
            {
                "item", "avformat_info", "item_to_container", "album", "artist", "track_artist",
                "genre_map", "composer", "category_map", "podcast_info", "container_seed",
                "video_info", "video_characteristics", "store_link", "track_size_calc",
            })
            {
                ClearTableIfExists(lib, table);
            }

            // Keep the device's primary (library) container; drop every other container
            // (user playlists, the Podcasts menu, etc.).
            Exec(lib, "DELETE FROM container WHERE pid != $p", ("$p", primary));

            tx.Commit();
        }

        using (var loc = Open(locPath))
        {
            using var tx = loc.BeginTransaction();
            ClearTableIfExists(loc, "location");
            tx.Commit();
        }

        // Re-sign the now-empty Locations.itdb so the firmware still trusts it.
        File.WriteAllBytes(cbkPath, ITunesLocationsCbk.Build(File.ReadAllBytes(locPath), iv, rnd));

        if (File.Exists(dynPath))
        {
            using var dyn = Open(dynPath);
            using var tx = dyn.BeginTransaction();
            ClearTableIfExists(dyn, "item_stats");
            tx.Commit();
        }

        // The firmware's MASTER database is the compressed iTunesCDB - on boot it rebuilds the
        // SQLite set from it, which would repopulate the library we just emptied. Remove it (and
        // zero the legacy iTunesDB, which the firmware won't tolerate alongside a CDB) so an erased
        // device actually comes up empty. (Re-adding tracks still needs a regenerated, signed CDB.)
        var iTunesDir = Path.GetDirectoryName(_itlpDir)!;
        var cdbPath = Path.Combine(iTunesDir, "iTunesCDB");
        if (File.Exists(cdbPath))
        {
            File.Delete(cdbPath);
        }
        var legacyDbPath = Path.Combine(iTunesDir, "iTunesDB");
        if (File.Exists(legacyDbPath))
        {
            File.WriteAllBytes(legacyDbPath, Array.Empty<byte>());
        }
    }

    private static void ClearTableIfExists(SqliteConnection c, string table)
    {
        if (ScalarLong(c, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n", ("$n", table)) > 0)
        {
            Exec(c, $"DELETE FROM {table}");
        }
    }

    private static void PruneIfOrphan(SqliteConnection c, string table, string itemColumn, long pid)
    {
        if (pid == 0) { return; }
        if (ScalarLong(c, $"SELECT COUNT(*) FROM item WHERE {itemColumn}=$p", ("$p", pid)) == 0)
        {
            Exec(c, $"DELETE FROM {table} WHERE pid=$p", ("$p", pid));
        }
    }

    /// <summary>Looks up a track's item pid by its Locations.itdb relative path
    /// (e.g. "F00/ABCD.m4a"). Returns 0 when not present.</summary>
    public static long FindItemPidByLocation(string itlpDir, string locationRelative)
    {
        using var loc = Open(Path.Combine(itlpDir, "Locations.itdb"));
        return ScalarLong(loc, "SELECT item_pid FROM location WHERE location=$l LIMIT 1", ("$l", locationRelative));
    }

    // ── find-or-create ───────────────────────────────────────────────────────

    private static long FindOrCreateArtist(SqliteConnection c, string name)
    {
        long pid = ScalarLong(c, "SELECT pid FROM artist WHERE name=$n LIMIT 1", ("$n", name));
        if (pid != 0) { return pid; }
        pid = NewRandomPid(c, "artist");
        Exec(c, "INSERT INTO artist (pid, kind, name, sort_name, name_order, has_songs) VALUES ($p,2,$n,$n,100,1)", ("$p", pid), ("$n", name));
        return pid;
    }

    private static long FindOrCreateAlbum(SqliteConnection c, string name, long artistPid)
    {
        long pid = ScalarLong(c, "SELECT pid FROM album WHERE name=$n AND artist_pid=$a LIMIT 1", ("$n", name), ("$a", artistPid));
        if (pid != 0) { return pid; }
        pid = NewRandomPid(c, "album");
        Exec(c, "INSERT INTO album (pid, kind, name, sort_name, artist_pid, name_order, sort_order, has_songs) VALUES ($p,2,$n,$n,$a,100,100,1)", ("$p", pid), ("$n", name), ("$a", artistPid));
        return pid;
    }

    private static long FindOrCreateTrackArtist(SqliteConnection c, string name)
    {
        long pid = ScalarLong(c, "SELECT pid FROM track_artist WHERE name=$n LIMIT 1", ("$n", name));
        if (pid != 0) { return pid; }
        pid = ScalarLong(c, "SELECT COALESCE(MAX(pid),0)+1 FROM track_artist");
        Exec(c, "INSERT INTO track_artist (pid, name, sort_name, name_order, has_songs, has_non_compilation_tracks) VALUES ($p,$n,$n,100,1,1)", ("$p", pid), ("$n", name));
        return pid;
    }

    private static long FindOrCreateGenre(SqliteConnection c, string genre)
    {
        long id = ScalarLong(c, "SELECT id FROM genre_map WHERE genre=$g LIMIT 1", ("$g", genre));
        if (id != 0) { return id; }
        id = ScalarLong(c, "SELECT COALESCE(MAX(id),0)+1 FROM genre_map");
        Exec(c, "INSERT INTO genre_map (id, genre, genre_order, has_music) VALUES ($i,$g,$o,1)", ("$i", id), ("$g", genre), ("$o", id * 100));
        return id;
    }

    private static long FindOrCreateLocationKind(SqliteConnection c, string kind)
    {
        long id = ScalarLong(c, "SELECT id FROM location_kind_map WHERE kind=$k LIMIT 1", ("$k", kind));
        if (id != 0) { return id; }
        id = ScalarLong(c, "SELECT COALESCE(MAX(id),0)+1 FROM location_kind_map");
        Exec(c, "INSERT INTO location_kind_map (id, kind) VALUES ($i,$k)", ("$i", id), ("$k", kind));
        return id;
    }

    // ── sqlite helpers ─────────────────────────────────────────────────────────

    private static SqliteConnection Open(string path)
    {
        // Pooling off: these live on a removable device - the file handle must release on Dispose
        // (so the volume can be ejected and the .itdb re-read/re-signed) rather than linger in a pool.
        var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, params (string Name, object Value)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) { cmd.Parameters.AddWithValue(name, value); }
        cmd.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection c, string sql, params (string Name, object Value)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) { cmd.Parameters.AddWithValue(name, value); }
        var o = cmd.ExecuteScalar();
        return o is null or DBNull ? 0 : Convert.ToInt64(o);
    }

    private static string? ScalarStr(SqliteConnection c, string sql, params (string Name, object Value)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) { cmd.Parameters.AddWithValue(name, value); }
        var o = cmd.ExecuteScalar();
        return o is null or DBNull ? null : o.ToString();
    }

    private static List<string> GetColumns(SqliteConnection c, string table)
    {
        var cols = new List<string>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read()) { cols.Add(r.GetString(1)); }   // PRAGMA table_info column 1 = name
        return cols;
    }

    private static Dictionary<string, object?>? ReadRow(SqliteConnection c, string sql, params (string Name, object Value)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) { cmd.Parameters.AddWithValue(name, value); }
        using var r = cmd.ExecuteReader();
        if (!r.Read()) { return null; }
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < r.FieldCount; i++)
        {
            row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
        }
        return row;
    }

    private static long NewRandomPid(SqliteConnection c, string table)
    {
        Span<byte> b = stackalloc byte[8];
        while (true)
        {
            RandomNumberGenerator.Fill(b);
            long pid = BitConverter.ToInt64(b);
            if (pid == 0) { continue; }
            if (ScalarLong(c, $"SELECT COUNT(*) FROM {table} WHERE pid=$p", ("$p", pid)) == 0) { return pid; }
        }
    }

    private static long Cf2001Now()
        => (long)(DateTime.UtcNow - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
}
