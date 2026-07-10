// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text;

namespace OrgZ.Services;

/// <summary>
/// One entry in an iPod Shuffle's <c>iTunesSD</c> track list. <see cref="IpodPath"/> is the device-root
/// path with forward slashes (e.g. <c>/iPod_Control/Music/F00/Song.mp3</c>).
/// </summary>
public sealed record ShuffleSdTrack(
    string IpodPath,
    int FileType,                    // 1 = MP3, 2 = AAC, 4 = WAV
    int Volume = 100,                // 0 (-100%) .. 100 (0%) .. 200 (+100%); 100 = unchanged
    int StartTimeMs = 0,
    int StopTimeMs = 0,              // 0 = play to end
    bool PlayInShuffle = true,
    bool Bookmarkable = false);

/// <summary>
/// Reads and writes the classic <c>iTunesSD</c> database used by the iPod Shuffle 1G/2G - the screenless
/// players have no iTunesDB, they play from this file. Layout is the WikiPodLinux / libgpod
/// (<c>itdb_itunesdb.c</c>, <c>itdb_shuffle_write_file</c>) spec: an 18-byte header then one 558-byte
/// entry per track. Every integer is 24-bit BIG-endian; the path is UTF-16 LITTLE-endian, 261 code units
/// (522 bytes), zero-padded. (Shuffle 3G/4G use the newer <c>bdhs</c> format - a separate writer.)
/// </summary>
public static class ShuffleSdWriter
{
    private const int EntrySize = 0x22E;     // 558
    private const int PathBytes = 522;       // 261 UTF-16 code units
    private const int HeaderSize = 0x12;     // 18

    /// <summary>Writes <c>{iTunesDir}/iTunesSD</c> listing the given tracks in order (this IS the Shuffle's
    /// playlist). An empty list writes a valid header with zero entries - that's how the device is "erased".</summary>
    public static void Write(string iTunesDir, IReadOnlyList<ShuffleSdTrack> tracks)
    {
        Directory.CreateDirectory(iTunesDir);
        using var ms = new MemoryStream(HeaderSize + tracks.Count * EntrySize);

        // ── header (18 bytes) ──
        Put24(ms, tracks.Count);
        Put24(ms, 0x010600);
        Put24(ms, HeaderSize);
        Put24(ms, 0);
        Put24(ms, 0);
        Put24(ms, 0);

        // ── one 558-byte entry per track ──
        foreach (var t in tracks)
        {
            Put24(ms, EntrySize);
            Put24(ms, 0x5AA501);
            Put24(ms, t.StartTimeMs / 256);      // 256 ms increments
            Put24(ms, 0);
            Put24(ms, 0);
            Put24(ms, t.StopTimeMs / 256);
            Put24(ms, 0);
            Put24(ms, 0);
            Put24(ms, Math.Clamp(t.Volume, 0, 200));
            Put24(ms, t.FileType);
            Put24(ms, 0x000200);

            var utf16 = Encoding.Unicode.GetBytes(t.IpodPath);  // UTF-16 LE, no BOM, no NUL
            if (utf16.Length > PathBytes) { utf16 = utf16[..PathBytes]; }
            ms.Write(utf16, 0, utf16.Length);
            for (int p = utf16.Length; p < PathBytes; p++) { ms.WriteByte(0); }

            ms.WriteByte((byte)(t.PlayInShuffle ? 1 : 0));   // skip-when-shuffling flag
            ms.WriteByte((byte)(t.Bookmarkable ? 1 : 0));    // bookmarkable flag
            ms.WriteByte(0);
        }

        var sdPath = Path.Combine(iTunesDir, "iTunesSD");
        AtomicFile.WriteAllBytes(sdPath, ms.ToArray(), backup: sdPath + ".orgzbak");
    }

    /// <summary>Parses <c>{iTunesDir}/iTunesSD</c> back into its track list. Returns empty when the file is
    /// missing or malformed.</summary>
    public static List<ShuffleSdTrack> Read(string iTunesDir)
    {
        var path = Path.Combine(iTunesDir, "iTunesSD");
        var result = new List<ShuffleSdTrack>();
        if (!File.Exists(path))
        {
            return result;
        }

        var b = File.ReadAllBytes(path);
        if (b.Length < HeaderSize)
        {
            return result;
        }

        int count = Get24(b, 0);
        int off = HeaderSize;
        for (int i = 0; i < count && off + EntrySize <= b.Length; i++, off += EntrySize)
        {
            int volume = Get24(b, off + 0x18);
            int fileType = Get24(b, off + 0x1B);
            int start = Get24(b, off + 0x06) * 256;
            int stop = Get24(b, off + 0x0F) * 256;
            var ipodPath = Encoding.Unicode.GetString(b, off + 0x21, PathBytes).TrimEnd('\0');
            bool shuffle = b[off + 0x22B] != 0;
            bool bookmark = b[off + 0x22C] != 0;
            result.Add(new ShuffleSdTrack(ipodPath, fileType, volume, start, stop, shuffle, bookmark));
        }
        return result;
    }

    /// <summary>iTunesSD file-type code from a file extension: MP3 = 1, AAC family = 2, WAV = 4 (default MP3).</summary>
    public static int FileTypeFor(string pathOrExtension)
    {
        var ext = Path.GetExtension(pathOrExtension).ToLowerInvariant();
        return ext switch
        {
            ".wav" => 4,
            ".m4a" or ".aac" or ".m4b" or ".mp4" => 2,
            _ => 1,
        };
    }

    private static void Put24(Stream s, int v)
    {
        s.WriteByte((byte)((v >> 16) & 0xFF));
        s.WriteByte((byte)((v >> 8) & 0xFF));
        s.WriteByte((byte)(v & 0xFF));
    }

    private static int Get24(byte[] b, int off) => (b[off] << 16) | (b[off + 1] << 8) | b[off + 2];
}
