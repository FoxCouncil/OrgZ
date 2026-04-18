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
        var tracks = new List<ITunesTrack>();
        var bytes = File.ReadAllBytes(iTunesDbPath);

        // Root must be MHBD
        if (bytes.Length < 4 || !MatchMagic(bytes, 0, "mhbd"))
        {
            return tracks;
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

            if (mhsdTotalSize <= 0) break;
            pos += mhsdTotalSize;
        }

        return tracks;
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
