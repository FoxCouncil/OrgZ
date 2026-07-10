// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text;

namespace OrgZ.Services;

/// <summary>
/// Fields for a track being written into the iTunesDB. <see cref="IpodPath"/> is
/// the device-relative location in iTunesDB form (":iPod_Control:Music:F00:ABCD.m4a").
/// </summary>
public sealed record NewTrack
{
    public required uint TrackId { get; init; }
    public required string IpodPath { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? Genre { get; init; }
    public string? Composer { get; init; }
    public long FileSize { get; init; }
    public int LengthMs { get; init; }
    public int Bitrate { get; init; }
    public int SampleRate { get; init; }
    public int Year { get; init; }
    public int TrackNumber { get; init; }
    public int TotalTracks { get; init; }
    public int DiscNumber { get; init; }
    public int TotalDiscs { get; init; }
    /// <summary>iTunesDB star rating in its 0-100 scale (20 per star). Byte at mhit 0x1F.</summary>
    public int Rating { get; init; }
    public DateTime DateAddedUtc { get; init; }

    /// <summary>64-bit persistent id. Must match the ArtworkDB mhii.song_id when art is attached.</summary>
    public ulong Dbid { get; init; }
    public bool HasArtwork { get; init; }
    public int ArtworkSize { get; init; }

    // --- Podcast fields (set IsPodcast to mark the MHIT as a podcast episode) ---
    /// <summary>Marks this as a podcast: mediatype=4, bookmarkable, skip-on-shuffle, unplayed dot.</summary>
    public bool IsPodcast { get; init; }

    /// <summary>Marks this as an audiobook: mediatype=8, bookmarkable, skip-on-shuffle - the iPod
    /// files it under Audiobooks and remembers the playback position.</summary>
    public bool IsAudiobook { get; init; }
    /// <summary>Episode description (MHOD type 14).</summary>
    public string? Description { get; init; }
    /// <summary>Episode enclosure (audio) URL (MHOD type 15).</summary>
    public string? PodcastUrl { get; init; }
    /// <summary>Show feed/RSS URL (MHOD type 16).</summary>
    public string? PodcastRss { get; init; }
    /// <summary>Episode publish date - written to time_released (0x8C). Null leaves it zero.</summary>
    public DateTime? TimeReleased { get; init; }
}

/// <summary>
/// Mutates a parsed <see cref="ITunesDbDocument"/> to add a track: appends a new
/// MHIT (+ its string MHODs) to the track dataset (MHSD type 1) and references it
/// from every master/library playlist (MHSD types 2 and 3) via a new MHIP. Callers
/// then <see cref="ITunesDbChunkTree.Normalize"/> + <see cref="ITunesDbChunkTree.Serialize"/>.
///
/// MHIT field offsets follow the documented layout
/// (https://www.ipodlinux.org/ITunesDB/); only the fields a stock iPod needs to
/// list and play a track are set - the rest stay zero. The header length is the
/// 0x184 (388-byte) modern format used by the iTunes 7-era firmware (iPod 5.5G).
/// </summary>
public static class ITunesDbWriter
{
    private const int MhitHeaderSize = 0x184;
    private const int MhodStringHeaderSize = 0x18;   // 24
    private const int MhipHeaderSize = 0x4C;         // 76

    private static readonly DateTime MacEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Builds a minimal, valid empty iTunesDB: an MHBD with a track dataset
    /// (MHSD type 1 → empty MHLT) and a playlists-v2 dataset (MHSD type 3 → MHLP
    /// with a single hidden master playlist). <see cref="AddTrack"/> can append to
    /// it directly. Callers <see cref="ITunesDbChunkTree.Normalize"/> +
    /// <see cref="ITunesDbChunkTree.Serialize"/> before writing.
    /// </summary>
    public static ITunesDbDocument CreateEmpty()
    {
        // 0xBC is the mhbd header size iTunes itself writes (libgpod's layout). It must be at
        // LEAST 0x6C: hash58 embeds its 20-byte HMAC at 0x58..0x6C INSIDE the header - a smaller
        // header puts the hash tail on top of the first child mhsd's magic and destroys the DB.
        var mhbd = NewChunk("mhbd", 0xBC);
        mhbd.WriteHeaderInt32(0x10, 0x19);   // db version (iPod Video / iTunes 7 era)

        var tracks = NewChunk("mhsd", 0x60);
        tracks.WriteHeaderInt32(0x0C, 1);    // dataset type 1 = tracks
        tracks.Children.Add(NewChunk("mhlt", 0x5C));

        var playlists = NewChunk("mhsd", 0x60);
        playlists.WriteHeaderInt32(0x0C, 3); // dataset type 3 = playlists (v2)
        playlists.Children.Add(NewChunk("mhlp", 0x5C));

        var master = NewChunk("mhyp", 0x6C);
        master.Header[0x14] = 1;             // master/library flag
        master.WriteHeaderInt32(0x1C, 1);    // playlist id
        master.Children.Add(BuildStringMhod(1, "OrgZ"));   // playlist name
        playlists.Children.Add(master);

        mhbd.Children.Add(tracks);
        mhbd.Children.Add(playlists);
        return new ITunesDbDocument { Root = mhbd };
    }

    /// <summary>Next free track id = max existing MHIT id + 1 (1 on an empty library).</summary>
    public static uint NextTrackId(ITunesDbDocument doc)
    {
        uint max = 0;
        var tracks = FindMhsd(doc, type: 1);
        if (tracks is not null)
        {
            foreach (var mhit in tracks.Children.Where(c => c.Magic == "mhit"))
            {
                max = Math.Max(max, (uint)mhit.ReadHeaderInt32(0x10));
            }
        }
        return max + 1;
    }

    public static void AddTrack(ITunesDbDocument doc, NewTrack track, bool addToMasterPlaylists = true)
    {
        var tracksMhsd = FindMhsd(doc, type: 1)
            ?? throw new InvalidDataException("iTunesDB has no track dataset (MHSD type 1).");

        tracksMhsd.Children.Add(BuildMhit(track));

        // Reference the track from every master playlist. iPods read the v2 list
        // (MHSD type 3); libgpod keeps the legacy type-2 list in sync, so we add
        // the MHIP to both masters to match what the firmware/iTunes expect.
        // Podcasts pass addToMasterPlaylists:false - they must NOT be in the Library/MPL,
        // which is what makes them appear only under the iPod's Podcasts menu.
        if (!addToMasterPlaylists)
        {
            return;
        }

        bool referenced = false;
        foreach (var master in MasterPlaylists(doc))
        {
            int position = master.Children.Count(c => c.Magic == "mhip");
            master.Children.Add(BuildMhip(track.TrackId, position));
            referenced = true;
        }
        if (!referenced)
        {
            throw new InvalidDataException("iTunesDB has no master playlist to add the track to.");
        }
    }

    /// <summary>
    /// Removes a track: drops its MHIT (id at 0x10) from the track dataset and every MHIP that
    /// references it (track id at 0x18) from every playlist in both playlist datasets - master,
    /// user, and the Podcasts list. Podcast/group-header MHIPs reference no track (0x18 == 0) so
    /// they're never matched. Returns true if the MHIT was found. Callers Normalize + Serialize
    /// (and re-checksum) after.
    /// </summary>
    public static bool RemoveTrack(ITunesDbDocument doc, uint trackId)
    {
        var tracksMhsd = FindMhsd(doc, type: 1);
        bool removed = tracksMhsd is not null
            && tracksMhsd.Children.RemoveAll(c => c.Magic == "mhit" && (uint)c.ReadHeaderInt32(0x10) == trackId) > 0;

        foreach (var mhsd in doc.Root.Children.Where(c => c.Magic == "mhsd"
                     && (c.ReadHeaderInt32(12) == 2 || c.ReadHeaderInt32(12) == 3)))
        {
            foreach (var mhyp in mhsd.Children.Where(c => c.Magic == "mhyp"))
            {
                mhyp.Children.RemoveAll(c => c.Magic == "mhip" && (uint)c.ReadHeaderInt32(0x18) == trackId);
            }
        }
        return removed;
    }

    // ── iTunes "musicdb" (db version 0x73 / Nano 5G CDB) ──────────────────────
    // The Nano 5G CDB uses a much richer track record than the legacy 0x184 mhit: a 624-byte mhit with
    // 8 MHODs, cross-linked to album (mhia, dataset 4) and artist (mhii, dataset 8) table rows by a
    // shared id space, plus an (empty) dataset 9. iTunes REJECTS the whole CDB ("cannot read the
    // contents") when it meets the short legacy mhit. So we CLONE iTunes's own structures - these molds
    // are the raw headers captured from a real iTunes-written CDB - and override only the per-row fields.
    private static readonly byte[] MhitMold = Convert.FromBase64String("bWhpdHACAAAaBQAACAAAAPfBAQABAAAAIDNQTQABAADewu7lLSA1AEO6AgALAAAAEAAAANsHAACAAAAAAABErAAAAAAAAAAAAAAAAAAAAAAKAAAAAAAAAKOZSeYBAAAAAQAAAAAAAAAZJerlAAAAAJU4WJj5Q6qZAAAAAAEA//+FUgkAAAAAAABELEcAAAAADAAAAAAAAAABAAAAAAAAAJ4sSuYBAAAAlThYmPlDqpkAAAEAAAAAABACAABQQ3gAAAAAAAAAAAAgBAAAAwAAAgEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAvlysAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAANMEBAIhF2jt8rwwyLSA1AAAAAACAgICAgIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEUBAAAAAAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAvMEBAAAAAAAAAAAAAAAAAAAAAAD4wQEAAAAAAPT///////9/AAAAAAAAAAACAAAAAAAAAAAAAAAuR+YKAAAAADJFaukBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
    private static readonly byte[] MhiaMold = Convert.FromBase64String("bWhpYVgAAAA6AQAAAwAAADTBAQCNQgfGgrRACwIAAACVOFiY+UOqmQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==");
    private static readonly byte[] MhiiMold = Convert.FromBase64String("bWhpaVAAAACeAAAAAQAAALzBAQAs1upRQb76awIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
    private static readonly byte[] Mhsd9Mold = Convert.FromBase64String("bWhzZGAAAACAAAAACQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

    /// <summary>Adds a track in the iTunes musicdb (Nano 5G CDB) shape: a 624-byte mhit cloned from the
    /// iTunes mold, cross-linked to find-or-created album (mhia) + artist (mhii) rows. Use this - NOT
    /// <see cref="AddTrack"/> - when building the CDB, or iTunes rejects the file.</summary>
    public static uint AddMusicdbTrack(ITunesDbDocument doc, NewTrack t, bool addToMasterPlaylists = true)
    {
        var tracksMhsd = FindMhsd(doc, type: 1)
            ?? throw new InvalidDataException("iTunesCDB has no track dataset (MHSD type 1).");

        // Album + artist rows first so the track id (shared counter) lands above their ids.
        long artistId = FindOrCreateArtistRow(doc, t.Artist ?? string.Empty);
        long albumId = FindOrCreateAlbumRow(doc, t.Album ?? string.Empty, t.Artist ?? string.Empty);
        uint trackId = (uint)NextMusicdbId(doc);

        var mhit = new ITunesDbChunk { Magic = "mhit", Header = (byte[])MhitMold.Clone() };
        // Standard mhit fields - these offsets are version-stable, so the same writes the legacy 0x184
        // path uses land correctly inside the 624-byte record; the mold supplies iTunes's trailing chrome.
        mhit.WriteHeaderInt32(0x10, (int)trackId);
        mhit.WriteHeaderInt32(0x14, 1);
        mhit.WriteHeaderInt32(0x24, (int)t.FileSize);
        mhit.WriteHeaderInt32(0x28, t.LengthMs);
        mhit.WriteHeaderInt32(0x2C, t.TrackNumber);
        mhit.WriteHeaderInt32(0x30, 0);
        mhit.WriteHeaderInt32(0x34, t.Year);
        mhit.WriteHeaderInt32(0x38, t.Bitrate);
        mhit.WriteHeaderInt32(0x3C, t.SampleRate << 16);
        mhit.WriteHeaderInt32(0x68, MacSeconds(t.DateAddedUtc));
        Array.Clear(mhit.Header, 0x70, 8);                 // drop the mold track's persistent id
        for (int i = 0; i < 8; i++) { mhit.Header[0x70 + i] = (byte)((t.Dbid >> (8 * i)) & 0xFF); }
        Array.Clear(mhit.Header, 0x124, 8);                // drop the mold track's album-hash
        mhit.WriteHeaderInt32(0x120, (int)albumId);        // → album (mhia) row
        mhit.WriteHeaderInt32(0x1E0, (int)artistId);       // → artist (mhii) row

        // Fields the 624-byte record DUPLICATES - iTunes/firmware read these copies, so a mold leftover
        // here poisons the reconciled SQLite (the cause of wrong file sizes + "!" missing-file flags):
        mhit.WriteHeaderInt32(0x18, FileTypeFourCc(t.IpodPath));   // filetype FourCC ("MP3 " / "M4A ")
        mhit.WriteHeaderInt32(0x12C, (int)t.FileSize);             // file_size also lives at 0x12C
        Array.Clear(mhit.Header, 0xA8, 8);
        for (int i = 0; i < 8; i++) { mhit.Header[0xA8 + i] = (byte)((t.Dbid >> (8 * i)) & 0xFF); }   // dbid also @0xA8
        // No artwork - clear the mold's artwork pointers so iTunes doesn't hunt a missing thumbnail.
        mhit.Header[0xA4] = 0;
        mhit.WriteHeaderInt32(0x7C, 0);
        mhit.WriteHeaderInt32(0x80, 0);

        if (t.IsPodcast)
        {
            ApplyPodcastMhitFlags(mhit, t);
        }
        else if (t.IsAudiobook)
        {
            ApplyAudiobookMhitFlags(mhit);
        }

        // MHODs in iTunes order (title, artist, album-artist, album, genre, kind, location).
        AddStringMhod(mhit, 1, t.Title);
        AddStringMhod(mhit, 4, t.Artist);
        AddStringMhod(mhit, 22, t.Artist);                 // album artist (fallback = artist)
        AddStringMhod(mhit, 3, t.Album);
        AddStringMhod(mhit, 5, t.Genre);
        AddStringMhod(mhit, 6, KindFor(t.IpodPath));
        AddStringMhod(mhit, 2, t.IpodPath);
        if (t.IsPodcast)
        {
            AddStringMhod(mhit, 14, t.Description);
            AddStringMhod(mhit, 15, t.PodcastUrl);
            AddStringMhod(mhit, 16, t.PodcastRss);
        }
        tracksMhsd.Children.Add(mhit);

        if (addToMasterPlaylists)
        {
            foreach (var master in MasterPlaylists(doc))
            {
                int position = master.Children.Count(c => c.Magic == "mhip");
                master.Children.Add(BuildMhip(trackId, position));
            }
        }
        return trackId;
    }

    /// <summary>Adds the empty dataset-9 iTunes writes (the Nano 5G expects 6 datasets, our skeleton
    /// ships 5). Normalize re-counts the mhbd dataset total, so just appending it is enough.</summary>
    public static void EnsureType9Dataset(ITunesDbDocument doc)
    {
        if (doc.Root.Children.Any(c => c.Magic == "mhsd" && c.ReadHeaderInt32(0x0C) == 9))
        {
            return;
        }
        doc.Root.Children.Add(new ITunesDbChunk { Magic = "mhsd", Header = (byte[])Mhsd9Mold.Clone() });
    }

    private static long FindOrCreateArtistRow(ITunesDbDocument doc, string artist)
    {
        var ds = FindMhsd(doc, type: 8);
        if (ds is null) { return 0; }
        foreach (var mhii in ds.Children.Where(c => c.Magic == "mhii"))
        {
            if (string.Equals(ReadMhodString(mhii, 300), artist, StringComparison.Ordinal))
            {
                return (uint)mhii.ReadHeaderInt32(0x10);
            }
        }
        long id = NextMusicdbId(doc);
        var row = new ITunesDbChunk { Magic = "mhii", Header = (byte[])MhiiMold.Clone() };
        row.WriteHeaderInt32(0x10, (int)id);
        WriteRandom8(row.Header, 0x14);
        row.Children.Add(BuildStringMhod(300, artist));
        ds.Children.Add(row);
        return id;
    }

    private static long FindOrCreateAlbumRow(ITunesDbDocument doc, string album, string artist)
    {
        var ds = FindMhsd(doc, type: 4);
        if (ds is null) { return 0; }
        foreach (var mhia in ds.Children.Where(c => c.Magic == "mhia"))
        {
            if (string.Equals(ReadMhodString(mhia, 200), album, StringComparison.Ordinal)
                && string.Equals(ReadMhodString(mhia, 201), artist, StringComparison.Ordinal))
            {
                return (uint)mhia.ReadHeaderInt32(0x10);
            }
        }
        long id = NextMusicdbId(doc);
        var row = new ITunesDbChunk { Magic = "mhia", Header = (byte[])MhiaMold.Clone() };
        row.WriteHeaderInt32(0x10, (int)id);
        WriteRandom8(row.Header, 0x14);
        row.Children.Add(BuildStringMhod(200, album));
        row.Children.Add(BuildStringMhod(201, artist));
        row.Children.Add(BuildStringMhod(202, artist));
        ds.Children.Add(row);
        return id;
    }

    /// <summary>iTunes draws track / album / artist ids from one shared counter, so the next id is the
    /// max across all three datasets + 1.</summary>
    private static long NextMusicdbId(ITunesDbDocument doc)
    {
        long max = 0;
        foreach (var mhsd in doc.Root.Children.Where(c => c.Magic == "mhsd"))
        {
            foreach (var row in mhsd.Children.Where(c => c.Magic is "mhit" or "mhia" or "mhii"))
            {
                max = Math.Max(max, (uint)row.ReadHeaderInt32(0x10));
            }
        }
        return max + 1;
    }

    private static void WriteRandom8(byte[] header, int offset)
    {
        Span<byte> b = stackalloc byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        b.CopyTo(header.AsSpan(offset, 8));
    }

    private static string KindFor(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".m4a" or ".aac" or ".m4b" => "MPEG-4 audio file",
            ".aif" or ".aiff" => "AIFF audio file",
            ".wav" => "WAV audio file",
            _ => "MPEG audio file",
        };
    }

    /// <summary>The filetype FourCC iTunes stores at mhit 0x18 (space-padded, big-endian).</summary>
    private static int FileTypeFourCc(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".m4a" or ".aac" or ".m4b" => 0x4D344120,   // "M4A "
            ".wav" => 0x57415620,                        // "WAV "
            _ => 0x4D503320,                             // "MP3 "
        };
    }

    /// <summary>
    /// Strips the library down to its skeleton: drops every track (MHIT) from the track dataset and
    /// every track reference (MHIP) from the master/library playlists, while leaving all datasets,
    /// the album/type-8 lists, and user/smart playlists intact. Used to reuse an iTunes-written CDB
    /// as a template - clear its tracks, then re-add the current library via <see cref="AddTrack"/> -
    /// so the emitted CDB keeps iTunes's exact multi-dataset structure.
    /// </summary>
    public static void ClearLibrary(ITunesDbDocument doc)
    {
        foreach (var mhsd in doc.Root.Children.Where(c => c.Magic == "mhsd"))
        {
            if (mhsd.ReadHeaderInt32(0x0C) == 1)
            {
                mhsd.Children.RemoveAll(c => c.Magic == "mhit");   // track dataset: drop all tracks
            }
            else
            {
                // Playlist datasets: empty only the master/library list (flag byte 0x14 == 1); leave
                // user + default/smart playlists (which AddPlaylist re-syncs / iTunes owns) alone.
                foreach (var mhyp in mhsd.Children.Where(c => c.Magic == "mhyp" && c.Header.Length > 0x14 && c.Header[0x14] == 1))
                {
                    mhyp.Children.RemoveAll(c => c.Magic == "mhip");
                }
            }
        }
    }

    /// <summary>Next free playlist id = max existing MHYP id + 1 (master is usually 1).</summary>
    public static uint NextPlaylistId(ITunesDbDocument doc)
    {
        uint max = 1;
        foreach (var mhsd in doc.Root.Children.Where(c => c.Magic == "mhsd"
                     && (c.ReadHeaderInt32(12) == 2 || c.ReadHeaderInt32(12) == 3)))
        {
            foreach (var mhyp in mhsd.Children.Where(c => c.Magic == "mhyp"))
            {
                max = Math.Max(max, (uint)mhyp.ReadHeaderInt32(0x1C));
            }
        }
        return max + 1;
    }

    /// <summary>
    /// Appends a regular (non-master) user playlist to the v2 playlists dataset (MHSD type 3):
    /// an MHYP carrying the name MHOD plus one MHIP per track. Mirrors the master-playlist shape
    /// minus the master flag. The track ids must already exist as MHITs (added via
    /// <see cref="AddTrack"/> or already on the device). Callers Normalize + Serialize after.
    /// </summary>
    public static void AddPlaylist(ITunesDbDocument doc, string name, IReadOnlyList<uint> trackIds)
    {
        var playlists = FindMhsd(doc, type: 3)
            ?? throw new InvalidDataException("iTunesDB has no playlists dataset (MHSD type 3).");

        // Idempotent re-sync: drop any existing user playlist with this name before re-adding.
        RemovePlaylistsByName(doc, name);

        // Build a user playlist exactly the way libgpod's write_playlist (itdb_itunesdb.c:5473) does -
        // the public reference, no iTunes needed. 108-byte mhyp; type byte 0 (visible user list) at
        // 0x14; the 64-bit persistent id at 0x1C (the bug was writing it to 0x20, which corrupted the
        // always-0 field at 0x24); string-mhod-count 1 at 0x28; then a TITLE mhod + the "long" type-100
        // playlist-pref mhod the firmware requires (mk_long_mhod_id_playlist) - mhodnum=2, NO type-102.
        // That type-100 blob is generic (no playlist id inside) so we reuse the master's. Then one mhip
        // per track.
        uint pid = NextPlaylistId(doc);
        var mhyp = NewChunk("mhyp", 0x6C);
        // Header[0x14] stays 0 - a visible user playlist (not the master/library list).
        ulong id64 = 0x4F52475A00000000UL | pid;               // unique, ORGZ-namespaced, can't collide
        for (int i = 0; i < 8; i++)
        {
            mhyp.Header[0x1C + i] = (byte)(id64 >> (8 * i));    // 64-bit persistent id @ 0x1C
        }
        mhyp.Header[0x28] = 1;                                  // string mhod count
        mhyp.Children.Add(BuildStringMhod(1, name));           // TITLE (mhod type 1)

        var longMhod = MasterPlaylists(doc).FirstOrDefault()?.Children
            .FirstOrDefault(c => c.Magic == "mhod" && c.ReadHeaderInt32(0x0C) == 100);
        if (longMhod is not null)
        {
            mhyp.Children.Add(longMhod.Clone());               // type-100 playlist-pref mhod
        }
        int position = 0;
        foreach (var tid in trackIds)
        {
            mhyp.Children.Add(BuildMhip(tid, position++));
        }
        playlists.Children.Add(mhyp);

        // Keep the MHLP playlist count in step with the dataset for hosts that read it.
        var mhlp = playlists.Children.FirstOrDefault(c => c.Magic == "mhlp");
        mhlp?.WriteHeaderInt32(0x08, playlists.Children.Count(c => c.Magic == "mhyp"));
    }

    /// <summary>Removes every non-master playlist whose name MHOD equals <paramref name="name"/>
    /// from both playlist datasets, so a re-sync replaces rather than duplicates - and a mirror sync
    /// can prune an orphaned playlist. The master/Library list is always left intact.</summary>
    public static void RemovePlaylistsByName(ITunesDbDocument doc, string name)
    {
        foreach (var mhsd in doc.Root.Children.Where(c => c.Magic == "mhsd"
                     && (c.ReadHeaderInt32(12) == 2 || c.ReadHeaderInt32(12) == 3)))
        {
            var dupes = mhsd.Children
                .Where(c => c.Magic == "mhyp"
                    && !(c.Header.Length > 0x14 && c.Header[0x14] == 1)   // never the master/library
                    && string.Equals(ReadMhodString(c, 1), name, StringComparison.Ordinal))
                .ToList();
            foreach (var d in dupes)
            {
                mhsd.Children.Remove(d);
            }
        }
    }

    /// <summary>Reads a string MHOD of the given type from a chunk's children (UTF-16LE body
    /// after the 16-byte sub-header), or null when absent/malformed.</summary>
    private static string? ReadMhodString(ITunesDbChunk parent, int mhodType)
    {
        foreach (var mhod in parent.Children.Where(c => c.Magic == "mhod"))
        {
            if (mhod.ReadHeaderInt32(0x0C) != mhodType || mhod.Body is null || mhod.Body.Length < 16)
            {
                continue;
            }
            int len = BitConverter.ToInt32(mhod.Body, 4);
            if (len < 0 || 16 + len > mhod.Body.Length)
            {
                continue;
            }
            return Encoding.Unicode.GetString(mhod.Body, 16, len);
        }
        return null;
    }

    /// <summary>
    /// Finds (or creates) the special Podcasts playlist - the one the iPod firmware reads to
    /// populate its Podcasts menu - and appends an MHIP for each episode track id. The playlist is
    /// identified/marked by <c>podcastflag = ITDB_PL_FLAG_PODCASTS (1)</c>, a 16-bit value at MHYP
    /// offset 0x2A (per libgpod itdb_playlist_set_podcasts). A flat episode list for now; show
    /// grouping (group-header MHIPs) is a follow-up. Callers Normalize + Serialize after.
    /// </summary>
    public static void EnsurePodcastPlaylist(ITunesDbDocument doc, IReadOnlyList<(string Show, uint TrackId)> episodes)
    {
        if (episodes.Count == 0)
        {
            return;
        }

        // Group episodes by show, preserving first-seen order - each show becomes a Podcasts submenu.
        var order = new List<string>();
        var byShow = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
        foreach (var (show, tid) in episodes)
        {
            var key = string.IsNullOrWhiteSpace(show) ? "Podcast" : show;
            if (!byShow.TryGetValue(key, out var list))
            {
                list = new List<uint>();
                byShow[key] = list;
                order.Add(key);
            }
            list.Add(tid);
        }

        // Write into BOTH playlist datasets (legacy type 2 + v2 type 3), sharing one playlist id.
        int pid = -1;
        foreach (var datasetType in new[] { 3, 2 })
        {
            var mhsd = FindMhsd(doc, datasetType);
            if (mhsd is null)
            {
                continue;
            }

            var pl = mhsd.Children.FirstOrDefault(c => c.Magic == "mhyp"
                && c.Header.Length > 0x2B && (c.Header[0x2A] | (c.Header[0x2B] << 8)) == 1);

            if (pl is not null)
            {
                pid = pl.ReadHeaderInt32(0x1C);
                foreach (var stale in pl.Children.Where(c => c.Magic == "mhip").ToList())
                {
                    pl.Children.Remove(stale);   // rebuild membership from this sync
                }
            }
            else
            {
                if (pid < 0) { pid = (int)NextPlaylistId(doc); }

                // Build the Podcasts list by MIRRORING this dataset's master playlist - inheriting its
                // exact header size and every field this firmware/iTunes version expects - then
                // overriding identity + flags. A hand-rolled 0x6C mhyp is the OLD libgpod shape; for
                // this db version iTunes writes a larger header (0xB8) and rejects the whole CDB
                // ("cannot read the contents") when it meets the short one - the same failure that made
                // user-playlist mhyps unwritable to the CDB. Cloning the master is the proven shape (it
                // mirrors what CreatePlaylist does on the SQLite side).
                var master = mhsd.Children.FirstOrDefault(c => c.Magic == "mhyp"
                                 && c.Header.Length > 0x14 && c.Header[0x14] == 1)
                    ?? throw new InvalidDataException("iTunesCDB has no master playlist to model the Podcasts list on.");
                pl = master.Clone();
                pl.Children.RemoveAll(c => c.Magic == "mhip");    // rebuild membership from this sync
                pl.Header[0x14] = 0;                              // not the master/library list
                pl.WriteHeaderInt32(0x1C, pid);                   // its own playlist id
                pl.Header[0x2A] = 1;                              // podcastflag = ITDB_PL_FLAG_PODCASTS
                pl.Header[0x2B] = 0;
                pl.WriteHeaderInt32(0x2C, 0x18);                  // list sort order = 24 (release date - the Podcasts value)

                // Rename the cloned title MHOD to "Podcasts"; keep the master's type-100/102 chrome and
                // its string-MHOD-count (0x28) intact so the header stays self-consistent.
                pl.Children.RemoveAll(c => c.Magic == "mhod" && c.ReadHeaderInt32(0x0C) == 1);
                pl.Children.Insert(0, BuildStringMhod(1, "Podcasts"));
                mhsd.Children.Add(pl);
            }

            // Per show: a group-header MHIP (groupflag=256 + its own group id + a title MHOD),
            // then one MHIP per episode whose grouping ref points back at that header's group id.
            // Start group/episode ids ABOVE the 256 groupflag sentinel so no group id (and thus no
            // episode's groupref) ever equals 256 - otherwise the firmware reads that group's
            // episodes as headers.
            int nextId = 0x1000;
            int position = 0;
            foreach (var show in order)
            {
                int groupId = nextId++;
                var header = NewChunk("mhip", MhipHeaderSize);
                header.WriteHeaderInt32(0x10, 0x100);            // grouping flag 0x100 = podcast GROUP header
                header.WriteHeaderInt32(0x14, groupId);          // this group's id (episodes point here via 0x20)
                // 0x18 trackid stays 0 - a header references no track
                header.Children.Add(BuildStringMhod(1, show));   // show name → Podcasts submenu
                pl.Children.Add(header);

                foreach (var tid in byShow[show])
                {
                    var ep = NewChunk("mhip", MhipHeaderSize);
                    // 0x10 grouping flag stays 0 - a normal (episode) entry
                    ep.WriteHeaderInt32(0x14, nextId++);         // this entry's unique id
                    ep.WriteHeaderInt32(0x18, (int)tid);         // the episode track
                    ep.WriteHeaderInt32(0x20, groupId);          // podcast grouping reference → parent group
                    // Episodes need the type-100 MHOD_ID_PLAYLIST position child too - without it libgpod
                    // and the firmware don't count them as members and the Podcasts menu reads empty (the
                    // same bug the music library had). The grouping ref nests them under the show header.
                    ep.Children.Add(BuildPlaylistPositionMhod(position++));
                    pl.Children.Add(ep);
                }
            }
        }
    }

    private static ITunesDbChunk? FindMhsd(ITunesDbDocument doc, int type)
        => doc.Root.Children.FirstOrDefault(c => c.Magic == "mhsd" && c.ReadHeaderInt32(12) == type);

    private static IEnumerable<ITunesDbChunk> MasterPlaylists(ITunesDbDocument doc)
    {
        foreach (var mhsd in doc.Root.Children.Where(c => c.Magic == "mhsd"
                     && (c.ReadHeaderInt32(12) == 2 || c.ReadHeaderInt32(12) == 3)))
        {
            foreach (var mhyp in mhsd.Children.Where(c => c.Magic == "mhyp"))
            {
                // Master/library flag is the byte at MHYP+0x14.
                if (mhyp.Header.Length > 0x14 && mhyp.Header[0x14] == 1)
                {
                    yield return mhyp;
                }
            }
        }
    }

    private static ITunesDbChunk BuildMhit(NewTrack t)
    {
        var mhit = new ITunesDbChunk { Magic = "mhit", Header = new byte[MhitHeaderSize] };
        WriteAscii(mhit.Header, 0, "mhit");
        mhit.WriteHeaderInt32(0x04, MhitHeaderSize);
        // 0x08 total size + 0x0C mhod count are filled by Normalize.
        mhit.WriteHeaderInt32(0x10, (int)t.TrackId);     // unique id
        mhit.WriteHeaderInt32(0x14, 1);                  // visible
        if (t.Rating is > 0 and <= 100)
        {
            mhit.Header[0x1F] = (byte)t.Rating;          // star rating (0-100), single byte
        }
        mhit.WriteHeaderInt32(0x24, (int)t.FileSize);
        mhit.WriteHeaderInt32(0x28, t.LengthMs);
        mhit.WriteHeaderInt32(0x2C, t.TrackNumber);
        mhit.WriteHeaderInt32(0x30, t.TotalTracks);
        mhit.WriteHeaderInt32(0x34, t.Year);
        mhit.WriteHeaderInt32(0x38, t.Bitrate);
        mhit.WriteHeaderInt32(0x3C, t.SampleRate << 16);
        WriteUInt16(mhit.Header, 0x5C, t.DiscNumber);    // disc number (u16)
        WriteUInt16(mhit.Header, 0x60, t.TotalDiscs);    // total discs (u16)
        mhit.WriteHeaderInt32(0x68, MacSeconds(t.DateAddedUtc));   // date added

        // 64-bit persistent id (links to ArtworkDB mhii.song_id). Even without
        // artwork a unique dbid is good hygiene - the iPod uses it to de-dup.
        if (t.Dbid != 0)
        {
            for (int i = 0; i < 8; i++)
            {
                mhit.Header[0x70 + i] = (byte)((t.Dbid >> (8 * i)) & 0xFF);
            }
        }
        if (t.HasArtwork)
        {
            mhit.Header[0xA4] = 1;                              // has_artwork (0x01 = yes)
            mhit.WriteHeaderInt32(0x7C, 1);                     // artwork_count (low 16 bits)
            mhit.WriteHeaderInt32(0x80, t.ArtworkSize);         // artwork_size
        }

        // Podcast episode: mark the mediatype + bookmark/unplayed flags and write the
        // podcast MHODs. Audiobooks get their own mediatype + bookmark cluster.
        if (t.IsPodcast)
        {
            ApplyPodcastMhitFlags(mhit, t);
        }
        else if (t.IsAudiobook)
        {
            ApplyAudiobookMhitFlags(mhit);
        }

        // String MHODs. Location (type 2) is required; the rest are added when present.
        AddStringMhod(mhit, 2, t.IpodPath);
        AddStringMhod(mhit, 1, t.Title);
        AddStringMhod(mhit, 3, t.Album);
        AddStringMhod(mhit, 4, t.Artist);
        AddStringMhod(mhit, 5, t.Genre);
        AddStringMhod(mhit, 12, t.Composer);
        if (t.IsPodcast)
        {
            AddStringMhod(mhit, 14, t.Description);        // episode description (UTF-16 string mhod)
            AddPlainStringMhod(mhit, 15, t.PodcastUrl);   // enclosure (audio) URL - plain UTF-8
            AddPlainStringMhod(mhit, 16, t.PodcastRss);   // show RSS/feed URL - plain UTF-8
        }
        return mhit;
    }

    /// <summary>
    /// Marks an mhit as a podcast episode: media_type = podcast plus the bookmark/unplayed flag
    /// cluster and the release date. Offsets pinned against libgpod's mhit layout. Shared by the
    /// legacy iTunesDB writer (<see cref="BuildMhit"/>) and the Nano 5G CDB writer
    /// (<see cref="AddMusicdbTrack"/>) so the two paths can't drift.
    /// </summary>
    private static void ApplyPodcastMhitFlags(ITunesDbChunk mhit, NewTrack t)
    {
        mhit.WriteHeaderInt32(ITunesMediaType.MhitOffset, ITunesMediaType.Podcast);
        mhit.Header[0xA5] = 1;              // skip_when_shuffling
        mhit.Header[0xA6] = 1;              // remember_playback_position (bookmarkable)
        mhit.Header[0xA7] = 1;              // flag4 - REQUIRED for podcasts (spec: must be 0x1/0x2)
        mhit.Header[0xB2] = 2;              // mark_unplayed: 2 = new/unplayed (blue dot)
        if (t.TimeReleased is { } rel)
        {
            mhit.WriteHeaderInt32(0x8C, MacSeconds(rel));   // time_released (episode pubdate)
        }
    }

    /// <summary>
    /// Marks an mhit as an audiobook: media_type = 8 plus the bookmark cluster - audiobooks
    /// remember their playback position and stay out of shuffle, matching what libgpod writes
    /// for ITDB_MEDIATYPE_AUDIOBOOK. Unlike podcasts there is no unplayed dot, no flag4
    /// requirement, and no release date.
    /// </summary>
    private static void ApplyAudiobookMhitFlags(ITunesDbChunk mhit)
    {
        mhit.WriteHeaderInt32(ITunesMediaType.MhitOffset, ITunesMediaType.Audiobook);
        mhit.Header[0xA5] = 1;              // skip_when_shuffling
        mhit.Header[0xA6] = 1;              // remember_playback_position (bookmarkable)
    }

    private static void AddStringMhod(ITunesDbChunk parent, int mhodType, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        parent.Children.Add(BuildStringMhod(mhodType, text));
    }

    private static void AddPlainStringMhod(ITunesDbChunk parent, int mhodType, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        parent.Children.Add(BuildPlainStringMhod(mhodType, text));
    }

    /// <summary>Podcast URL/RSS mhods (types 15/16) are a plain UTF-8 string straight after the 24-byte
    /// header - libgpod reads (total_size − header_size) raw bytes at header_size. Writing them as the
    /// UTF-16 <see cref="BuildStringMhod"/> made libgpod/iTunes read the inner string-type "1" as the URL.</summary>
    private static ITunesDbChunk BuildPlainStringMhod(int mhodType, string text)
    {
        var mhod = new ITunesDbChunk { Magic = "mhod", Header = new byte[MhodStringHeaderSize] };
        WriteAscii(mhod.Header, 0, "mhod");
        mhod.WriteHeaderInt32(0x04, MhodStringHeaderSize);   // header = 24
        // 0x08 total size (= 24 + UTF-8 byte length) set by Normalize.
        mhod.WriteHeaderInt32(0x0C, mhodType);
        mhod.Body = Encoding.UTF8.GetBytes(text);
        return mhod;
    }

    private static ITunesDbChunk BuildStringMhod(int mhodType, string text)
    {
        var utf16 = Encoding.Unicode.GetBytes(text);
        var mhod = new ITunesDbChunk { Magic = "mhod", Header = new byte[MhodStringHeaderSize] };
        WriteAscii(mhod.Header, 0, "mhod");
        mhod.WriteHeaderInt32(0x04, MhodStringHeaderSize);
        // 0x08 total size set by Normalize.
        mhod.WriteHeaderInt32(0x0C, mhodType);

        // 16-byte string sub-header (position=1, byte length, two reserved) then UTF-16LE.
        var body = new byte[16 + utf16.Length];
        ITunesDbChunkTree.WriteInt32(body, 0, 1);              // position
        ITunesDbChunkTree.WriteInt32(body, 4, utf16.Length);  // byte length (not chars)
        Buffer.BlockCopy(utf16, 0, body, 16, utf16.Length);
        mhod.Body = body;
        return mhod;
    }

    private static ITunesDbChunk BuildMhip(uint trackId, int position)
    {
        var mhip = new ITunesDbChunk { Magic = "mhip", Header = new byte[MhipHeaderSize] };
        WriteAscii(mhip.Header, 0, "mhip");
        mhip.WriteHeaderInt32(0x04, MhipHeaderSize);
        // 0x08 total size + 0x0C mhod count set by Normalize.
        mhip.WriteHeaderInt32(0x14, (int)trackId);   // playlist-entry id (nonzero, as real iTunes writes)
        mhip.WriteHeaderInt32(0x18, (int)trackId);   // referenced track id
        // libgpod (get_mhip) - and the iPod firmware - only add a track to a playlist when the MHIP
        // carries a type-100 MHOD_ID_PLAYLIST position child. Without it the whole library reads as an
        // empty song list even though the tracks are on disk. Proven against libgpod's itdb_parse.
        mhip.Children.Add(BuildPlaylistPositionMhod(position));
        return mhip;
    }

    /// <summary>The per-entry position MHOD (type 100) that libgpod/the firmware require inside every
    /// playlist MHIP: a 24-byte header + 20-byte body whose first int32 is the track's index.</summary>
    private static ITunesDbChunk BuildPlaylistPositionMhod(int position)
    {
        var mhod = new ITunesDbChunk { Magic = "mhod", Header = new byte[MhodStringHeaderSize] };
        WriteAscii(mhod.Header, 0, "mhod");
        mhod.WriteHeaderInt32(0x04, MhodStringHeaderSize);   // header = 24
        // 0x08 total size (= 24 + 20 = 44) set by Normalize.
        mhod.WriteHeaderInt32(0x0C, 100);                    // MHOD_ID_PLAYLIST (position index)
        var body = new byte[20];
        ITunesDbChunkTree.WriteInt32(body, 0, position);     // track position in the playlist
        mhod.Body = body;
        return mhod;
    }

    private static ITunesDbChunk NewChunk(string magic, int headerSize)
    {
        var c = new ITunesDbChunk { Magic = magic, Header = new byte[headerSize] };
        WriteAscii(c.Header, 0, magic);
        c.WriteHeaderInt32(0x04, headerSize);
        // total size (0x08) and count fields are filled by ITunesDbChunkTree.Normalize.
        return c;
    }

    private static int MacSeconds(DateTime utc)
    {
        // iTunesDB timestamps are uint32 seconds since 1904. A 2026 date is ~3.85e9
        // seconds - past int.MaxValue - so cast through uint to keep the bit pattern
        // the firmware (and ITunesDbReader, which reads it back as uint) expects.
        double seconds = (utc.ToUniversalTime() - MacEpoch).TotalSeconds;
        if (seconds <= 0 || seconds > uint.MaxValue)
        {
            return 0;
        }
        return unchecked((int)(uint)seconds);
    }

    private static void WriteAscii(byte[] dest, int offset, string magic)
    {
        for (int i = 0; i < magic.Length; i++)
        {
            dest[offset + i] = (byte)magic[i];
        }
    }

    /// <summary>Writes a little-endian unsigned 16-bit value (disc#/total-discs live as u16s;
    /// writing an int32 there would spill into the neighbouring field).</summary>
    private static void WriteUInt16(byte[] dest, int offset, int value) => LittleEndian.WriteUInt16(dest, offset, value);
}
