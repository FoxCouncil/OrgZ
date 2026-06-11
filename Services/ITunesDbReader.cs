// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text;

namespace OrgZ.Services;

/// <summary>
/// Parses Apple's iTunesDB binary format found on stock-firmware iPods at
/// iPod_Control/iTunes/iTunesDB. Chunk structure:
///   MHBD  - database root
///   MHSD  - dataset (tracks, playlists, etc.)
///   MHLT  - track list
///   MHIT  - track item (fixed-size header + variable MHOD children)
///   MHOD  - data object (typed: title, album, artist, path, etc.)
/// Strings are UTF-16LE. Paths use ':' separators (convert to '\' on Windows).
///
/// Reference: https://www.ipodlinux.org/ITunesDB/
/// </summary>
public static class ITunesDbReader
{
    public class ITunesTrack
    {
        public uint TrackId { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? Genre { get; set; }
        public string? Composer { get; set; }
        public string? FilePath { get; set; }  // full path relative to device root
        public long FileSize { get; set; }
        public int DurationMs { get; set; }
        public int Bitrate { get; set; }
        public int SampleRate { get; set; }
        public int Year { get; set; }
        public int TrackNumber { get; set; }
        public int TotalTracks { get; set; }
        public int DiscNumber { get; set; }
        public int TotalDiscs { get; set; }
        public int PlayCount { get; set; }
        public int SkipCount { get; set; }
        public int Rating { get; set; }          // 0-100 in iTunesDB (0, 20, 40, 60, 80, 100)
        public DateTime? LastPlayed { get; set; }
        public DateTime? DateAdded { get; set; }

        /// <summary>64-bit persistent id (MHIT @0x70). Matches the ArtworkDB mhii.song_id.</summary>
        public ulong Dbid { get; set; }
    }

    // MHOD data-object types we care about
    private const int MHOD_TITLE     = 1;
    private const int MHOD_LOCATION  = 2;
    private const int MHOD_ALBUM     = 3;
    private const int MHOD_ARTIST    = 4;
    private const int MHOD_GENRE     = 5;
    private const int MHOD_COMPOSER  = 12;

    // Mac HFS epoch: 1904-01-01 UTC
    private static readonly DateTime MacEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static List<ITunesTrack> Read(string iTunesDbPath, string mountPath)
    {
        ReadAll(iTunesDbPath, mountPath, out var tracks, out _);
        return tracks;
    }

    /// <summary>
    /// A single playlist entry from the iTunesDB. Name comes from the first string MHOD
    /// child of the MHYP; TrackIds is the ordered list of MHIP references. Apple hides
    /// one "master library" playlist at the front (flag byte is 1 in MHYP header) that
    /// contains every track — we skip it so it doesn't pollute the sidebar.
    /// </summary>
    public class ITunesPlaylist
    {
        public uint PlaylistId { get; set; }
        public string? Name { get; set; }
        public List<uint> TrackIds { get; set; } = [];
        public bool IsMaster { get; set; }
        public bool IsPodcastPlaylist { get; set; }
    }

    /// <summary>
    /// Parses both tracks and playlists in one pass. Playlists reference tracks by
    /// iTunesDB TrackId; callers should map those to <see cref="MediaItem.Id"/> (usually
    /// <c>device:{mount}:{trackId}</c>) to join against the live track list.
    /// </summary>
    public static void ReadAll(string iTunesDbPath, string mountPath, out List<ITunesTrack> tracks, out List<ITunesPlaylist> playlists)
    {
        tracks = [];
        playlists = [];

        var bytes = File.ReadAllBytes(iTunesDbPath);

        // Root must be MHBD
        if (bytes.Length < 4 || !MatchMagic(bytes, 0, "mhbd"))
        {
            return;
        }

        int headerSize = ReadInt32(bytes, 4);
        int totalSize = ReadInt32(bytes, 8);

        // Walk MHSDs inside the MHBD
        int pos = headerSize;
        while (pos < totalSize - 8 && pos + 12 <= bytes.Length)
        {
            if (!MatchMagic(bytes, pos, "mhsd"))
            {
                break;
            }

            int mhsdHeaderSize = ReadInt32(bytes, pos + 4);
            int mhsdTotalSize = ReadInt32(bytes, pos + 8);
            int mhsdType = ReadInt32(bytes, pos + 12);

            if (mhsdType == 1)
            {
                // Dataset type 1 = tracks; the MHLT sits right after the MHSD header
                int mhltPos = pos + mhsdHeaderSize;
                if (mhltPos + 12 <= bytes.Length && MatchMagic(bytes, mhltPos, "mhlt"))
                {
                    int mhltHeaderSize = ReadInt32(bytes, mhltPos + 4);
                    int trackCount = ReadInt32(bytes, mhltPos + 8);

                    int mhitPos = mhltPos + mhltHeaderSize;
                    for (int i = 0; i < trackCount && mhitPos + 12 <= bytes.Length; i++)
                    {
                        var track = ReadMhit(bytes, mhitPos, mountPath, out int mhitTotalSize);
                        if (track != null)
                        {
                            tracks.Add(track);
                        }
                        if (mhitTotalSize <= 0) break;
                        mhitPos += mhitTotalSize;
                    }
                }
            }
            else if (mhsdType == 2 || mhsdType == 3)
            {
                // Dataset type 2 = playlists (legacy). Type 3 is the "playlists v2" format
                // used on newer iPods; the MHLP layout is the same, so we handle both.
                // MHLP sits right after the MHSD header.
                int mhlpPos = pos + mhsdHeaderSize;
                if (mhlpPos + 12 <= bytes.Length && MatchMagic(bytes, mhlpPos, "mhlp"))
                {
                    int mhlpHeaderSize = ReadInt32(bytes, mhlpPos + 4);
                    int playlistCount = ReadInt32(bytes, mhlpPos + 8);

                    int mhypPos = mhlpPos + mhlpHeaderSize;
                    for (int i = 0; i < playlistCount && mhypPos + 12 <= bytes.Length; i++)
                    {
                        var pl = ReadMhyp(bytes, mhypPos, out int mhypTotalSize);
                        if (pl != null && !pl.IsMaster && !pl.IsPodcastPlaylist)
                        {
                            playlists.Add(pl);
                        }
                        if (mhypTotalSize <= 0) break;
                        mhypPos += mhypTotalSize;
                    }
                }
            }

            if (mhsdTotalSize <= 0) break;
            pos += mhsdTotalSize;
        }
    }

    private static ITunesTrack? ReadMhit(byte[] bytes, int pos, string mountPath, out int totalSize)
    {
        totalSize = 0;
        if (!MatchMagic(bytes, pos, "mhit"))
        {
            return null;
        }

        int headerSize = ReadInt32(bytes, pos + 4);
        totalSize = ReadInt32(bytes, pos + 8);
        int childCount = ReadInt32(bytes, pos + 12);

        var track = new ITunesTrack
        {
            TrackId = (uint)ReadInt32(bytes, pos + 16),
            FileSize = ReadInt32(bytes, pos + 36),
            DurationMs = ReadInt32(bytes, pos + 40),
            TrackNumber = ReadInt32(bytes, pos + 44),
            TotalTracks = ReadInt32(bytes, pos + 48),
            Year = ReadInt32(bytes, pos + 52),
            Bitrate = ReadInt32(bytes, pos + 56),
            SampleRate = (int)((uint)ReadInt32(bytes, pos + 60) >> 16),
            PlayCount = ReadInt32(bytes, pos + 80),
            LastPlayed = ReadMacDate(ReadInt32(bytes, pos + 88)),
            DiscNumber = ReadInt32(bytes, pos + 92),
            TotalDiscs = ReadInt32(bytes, pos + 94),
            Rating = bytes.Length > pos + 28 ? bytes[pos + 28] : 0,
            DateAdded = ReadMacDate(ReadInt32(bytes, pos + 104)),
            SkipCount = ReadInt32(bytes, pos + 156),
            Dbid = pos + 0x78 <= bytes.Length ? BitConverter.ToUInt64(bytes, pos + 0x70) : 0,
        };

        // Walk MHOD children
        int childPos = pos + headerSize;
        for (int i = 0; i < childCount && childPos + 8 <= bytes.Length; i++)
        {
            if (!MatchMagic(bytes, childPos, "mhod"))
            {
                break;
            }

            int mhodHeaderSize = ReadInt32(bytes, childPos + 4);
            int mhodTotalSize = ReadInt32(bytes, childPos + 8);
            int mhodType = ReadInt32(bytes, childPos + 12);

            // String data starts at childPos + mhodHeaderSize (the "string header"),
            // with a 16-byte string descriptor: position[0]=1, bytelen at offset 4, then UTF-16LE bytes
            if (mhodHeaderSize > 0 && childPos + mhodHeaderSize + 16 <= bytes.Length)
            {
                int strLen = ReadInt32(bytes, childPos + mhodHeaderSize + 4);
                int strStart = childPos + mhodHeaderSize + 16;
                if (strLen > 0 && strStart + strLen <= bytes.Length)
                {
                    var text = Encoding.Unicode.GetString(bytes, strStart, strLen);
                    switch (mhodType)
                    {
                        case MHOD_TITLE: track.Title = text; break;
                        case MHOD_LOCATION: track.FilePath = ConvertIPodPath(text, mountPath); break;
                        case MHOD_ALBUM: track.Album = text; break;
                        case MHOD_ARTIST: track.Artist = text; break;
                        case MHOD_GENRE: track.Genre = text; break;
                        case MHOD_COMPOSER: track.Composer = text; break;
                    }
                }
            }

            if (mhodTotalSize <= 0) break;
            childPos += mhodTotalSize;
        }

        return track;
    }

    /// <summary>
    /// Parses one MHYP playlist header and its MHIP track-reference children. MHYP layout:
    ///   [00..03] 'mhyp'
    ///   [04..07] headerSize
    ///   [08..0B] totalSize
    ///   [0C..0F] number of MHOD children (name + sort order + smart rules)
    ///   [10..13] number of MHIP children (tracks in this playlist)
    ///   [14]     master flag (1 = library/master playlist — skip)
    ///   [15..17] padding
    ///   [18..1B] playlist ID (uint32)
    ///   ...
    /// Then the MHODs and MHIPs follow starting at headerSize. MHIP layout:
    ///   [00..03] 'mhip'
    ///   [04..07] headerSize
    ///   [08..0B] totalSize
    ///   [0C..0F] number of MHOD children (optional extras)
    ///   [10..13] podcast grouping ref
    ///   [14..17] group ID
    ///   [18..1B] trackId (matches iTunesDB MHIT.TrackId)
    ///   [1C..1F] timestamp
    ///   ...
    /// </summary>
    private static ITunesPlaylist? ReadMhyp(byte[] bytes, int pos, out int totalSize)
    {
        totalSize = 0;
        if (!MatchMagic(bytes, pos, "mhyp"))
        {
            return null;
        }

        int headerSize = ReadInt32(bytes, pos + 4);
        totalSize = ReadInt32(bytes, pos + 8);
        int mhodCount = ReadInt32(bytes, pos + 12);
        int mhipCount = ReadInt32(bytes, pos + 16);
        byte masterFlag = bytes.Length > pos + 20 ? bytes[pos + 20] : (byte)0;
        uint playlistId = bytes.Length > pos + 0x1B ? (uint)ReadInt32(bytes, pos + 0x1C) : 0u;

        var playlist = new ITunesPlaylist
        {
            PlaylistId = playlistId,
            IsMaster = masterFlag == 1,
        };

        // Walk the MHOD children first (playlist name, sort order, etc) then the MHIPs.
        // Both child blocks sit contiguously starting at headerSize.
        int childPos = pos + headerSize;
        for (int i = 0; i < mhodCount && childPos + 8 <= bytes.Length; i++)
        {
            if (!MatchMagic(bytes, childPos, "mhod"))
            {
                break;
            }

            int mhodHeaderSize = ReadInt32(bytes, childPos + 4);
            int mhodTotalSize = ReadInt32(bytes, childPos + 8);
            int mhodType = ReadInt32(bytes, childPos + 12);

            if (mhodType == MHOD_TITLE && mhodHeaderSize > 0 && childPos + mhodHeaderSize + 16 <= bytes.Length)
            {
                int strLen = ReadInt32(bytes, childPos + mhodHeaderSize + 4);
                int strStart = childPos + mhodHeaderSize + 16;
                if (strLen > 0 && strStart + strLen <= bytes.Length)
                {
                    playlist.Name = Encoding.Unicode.GetString(bytes, strStart, strLen);
                }
            }

            // Type 52 signals the podcast auto-playlist (iTunes internal). Skip it so
            // Podcasts don't show up in the OrgZ sidebar as a regular playlist.
            if (mhodType == 52)
            {
                playlist.IsPodcastPlaylist = true;
            }

            if (mhodTotalSize <= 0) break;
            childPos += mhodTotalSize;
        }

        for (int i = 0; i < mhipCount && childPos + 8 <= bytes.Length; i++)
        {
            if (!MatchMagic(bytes, childPos, "mhip"))
            {
                break;
            }

            int mhipHeaderSize = ReadInt32(bytes, childPos + 4);
            int mhipTotalSize = ReadInt32(bytes, childPos + 8);

            if (bytes.Length > childPos + 0x1B)
            {
                uint trackId = (uint)ReadInt32(bytes, childPos + 0x18);
                playlist.TrackIds.Add(trackId);
            }

            if (mhipTotalSize <= 0) break;
            childPos += mhipTotalSize;
        }

        return playlist;
    }

    /// <summary>
    /// Converts ":iPod_Control:Music:F23:ABCD.mp3" (iTunesDB format) to
    /// "E:\iPod_Control\Music\F23\ABCD.mp3" (absolute Windows path).
    /// </summary>
    private static string ConvertIPodPath(string iPodPath, string mountPath)
    {
        var relative = iPodPath.TrimStart(':').Replace(':', Path.DirectorySeparatorChar);
        return Path.Combine(mountPath, relative);
    }

    private static bool MatchMagic(byte[] bytes, int pos, string magic)
    {
        if (pos + 4 > bytes.Length) return false;
        return bytes[pos] == magic[0]
            && bytes[pos + 1] == magic[1]
            && bytes[pos + 2] == magic[2]
            && bytes[pos + 3] == magic[3];
    }

    private static int ReadInt32(byte[] bytes, int pos)
    {
        if (pos + 4 > bytes.Length) return 0;
        return bytes[pos] | (bytes[pos + 1] << 8) | (bytes[pos + 2] << 16) | (bytes[pos + 3] << 24);
    }

    private static DateTime? ReadMacDate(int macSeconds)
    {
        if (macSeconds == 0) return null;
        try
        {
            return MacEpoch.AddSeconds((uint)macSeconds);
        }
        catch
        {
            return null;
        }
    }
}
