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

    public Nano5gLibraryWriter(string itlpDir) => _itlpDir = itlpDir;

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
        return itemPid;
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
    }

    private static void PruneIfOrphan(SqliteConnection c, string table, string itemColumn, long pid)
    {
        if (pid == 0) { return; }
        if (ScalarLong(c, $"SELECT COUNT(*) FROM item WHERE {itemColumn}=$p", ("$p", pid)) == 0)
        {
            Exec(c, $"DELETE FROM {table} WHERE pid=$p", ("$p", pid));
        }
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

    private static long NewRandomPid(SqliteConnection c, string table)
    {
        while (true)
        {
            Span<byte> b = stackalloc byte[8];
            RandomNumberGenerator.Fill(b);
            long pid = BitConverter.ToInt64(b);
            if (pid == 0) { continue; }
            if (ScalarLong(c, $"SELECT COUNT(*) FROM {table} WHERE pid=$p", ("$p", pid)) == 0) { return pid; }
        }
    }

    private static long Cf2001Now()
        => (long)(DateTime.UtcNow - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
}
