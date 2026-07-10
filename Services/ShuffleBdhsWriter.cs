// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text;

namespace OrgZ.Services;

/// <summary>
/// Reads and writes the newer <c>iTunesSD</c> ("bdhs") database used by the iPod Shuffle 3G/4G - the
/// VoiceOver-era players that dropped the classic <see cref="ShuffleSdWriter"/> format. Layout is ported
/// from libgpod's <c>itdb_itunesdb.c</c> (<c>write_bdhs</c>/<c>write_hths</c>/<c>write_rths</c>/
/// <c>write_hphs</c>/<c>write_lphs</c>): a 64-byte <c>bdhs</c> header, a <c>hths</c> track table of
/// 372-byte <c>rths</c> entries, and a <c>hphs</c> playlist table with one master <c>lphs</c> listing
/// every track. Everything is LITTLE-endian; paths are 256-byte UTF-8, null-padded, forward slashes.
/// </summary>
public static class ShuffleBdhsWriter
{
    private const int BdhsSize = 64;
    private const int RthsSize = 372;
    private const int PathBytes = 256;
    private const int RthsPathOffset = 0x18;   // start/stop/volume/filetype (4×4) after magic+len = 24

    public static void Write(string iTunesDir, IReadOnlyList<ShuffleSdTrack> tracks)
    {
        Directory.CreateDirectory(iTunesDir);
        var w = new Le();

        // ── bdhs: main header (64 bytes); offsets/counts back-patched by the sub-writers ──
        long bdhs = w.Pos;
        w.Magic("bdhs");
        w.U32(0x02000003);
        w.U32(0);                       // [ 8] length - patched below
        w.U32((uint)tracks.Count);      // [12] track count
        w.U32(0);                       // [16] non-empty playlist count - patched by hphs
        w.U32(0); w.U32(0);             // [20] unknown
        w.U8(0);                        // [28] limit max volume (off)
        w.U8(1);                        // [29] voiceover (on)
        w.U16(0);                       // [30] unknown
        w.U32(0);                       // [32] non-podcast track count - patched by hths
        w.U32(0);                       // [36] track header offset - patched by hths
        w.U32(0);                       // [40] playlist header offset - patched by hphs
        for (int i = 0; i < 5; i++) { w.U32(0); }   // [44] unknown
        w.PatchU32(bdhs + 8, (uint)(w.Pos - bdhs)); // fix_header: length = 64

        // ── hths: track list header + per-track offset table, then each rths ──
        w.PatchU32(36, (uint)w.Pos);    // bdhs track-header offset
        long hths = w.Pos;
        w.Magic("hths");
        w.U32(0);                       // length - patched (fix_short_header, +4)
        w.U32((uint)tracks.Count);
        w.U32(0); w.U32(0);             // unknown
        long trackTable = w.Pos;
        for (int i = 0; i < tracks.Count; i++) { w.U32(0); }   // per-track rths offsets
        w.PatchU32(hths + 4, (uint)(w.Pos - hths));            // hths header length

        for (int i = 0; i < tracks.Count; i++)
        {
            w.PatchU32(trackTable + i * 4, (uint)w.Pos);
            WriteRths(w, tracks[i], dbid: (ulong)(i + 1));
        }
        w.PatchU32(32, (uint)tracks.Count);   // bdhs non-podcast count (all tracks are plain music)

        // ── hphs: playlist list header with one master playlist listing every track ──
        w.PatchU32(16, 1);              // bdhs playlist count
        w.PatchU32(40, (uint)w.Pos);    // bdhs playlist-header offset
        long hphs = w.Pos;
        w.Magic("hphs");
        w.U32(0);                       // length - patched
        w.U16(1);                       // playlist count
        w.U16(0);                       // unknown
        w.U16(1);                       // non-podcast playlists
        w.U16(1);                       // master playlists
        w.U16(1);                       // non-audiobook playlists
        w.U16(0);                       // unknown
        long plTable = w.Pos;
        w.U32(0);                       // one playlist offset
        w.PatchU32(hphs + 4, (uint)(w.Pos - hphs));

        w.PatchU32(plTable, (uint)w.Pos);
        WriteMasterLphs(w, tracks.Count);

        var sdPath = Path.Combine(iTunesDir, "iTunesSD");
        AtomicFile.WriteAllBytes(sdPath, w.ToArray(), backup: sdPath + ".orgzbak");
    }

    private static void WriteRths(Le w, ShuffleSdTrack t, ulong dbid)
    {
        long rths = w.Pos;
        w.Magic("rths");
        w.U32(0);                       // length - patched
        w.U32((uint)t.StartTimeMs);
        w.U32((uint)t.StopTimeMs);
        w.U32(0);                       // volume gain (neutral)
        w.U32((uint)t.FileType);
        w.StringPad(t.IpodPath, PathBytes);
        w.U32(0);                       // bookmark time
        w.U8((byte)(t.PlayInShuffle ? 0 : 1));   // NB: inverted vs old format (1 = skip)
        w.U8((byte)(t.Bookmarkable ? 1 : 0));
        w.U8(0);                        // gapless album
        w.U8(0);                        // unknown
        w.U32(0);                       // pregap
        w.U32(0);                       // postgap
        w.U32(0);                       // sample count
        w.U32(0);                       // unknown
        w.U32(0);                       // gapless data
        w.U32(0);                       // unknown
        w.U32(0);                       // album id
        w.U16(0);                       // track number
        w.U16(0);                       // disc number
        w.U32(0); w.U32(0);             // unknown
        w.U64(dbid);                    // dbid / voiceover filename
        w.U32(0);                       // artist id
        for (int i = 0; i < 8; i++) { w.U32(0); }   // unknown
        w.PatchU32(rths + 4, (uint)(w.Pos - rths));  // rths length (= 372)
    }

    private static void WriteMasterLphs(Le w, int trackCount)
    {
        long lphs = w.Pos;
        w.Magic("lphs");
        w.U32(0);                       // length - patched
        w.U32((uint)trackCount);        // tracks in playlist
        w.U32(0);                       // non-podcast count - patched (+12)
        w.U32(0); w.U32(0);             // master voiceover = 0
        w.U32(1);                       // stype: 1 = master
        for (int i = 0; i < 4; i++) { w.U32(0); }   // unknown
        for (int i = 0; i < trackCount; i++) { w.U32((uint)i); }   // track indices, in order
        w.PatchU32(lphs + 12, (uint)trackCount);
        w.PatchU32(lphs + 4, (uint)(w.Pos - lphs));
    }

    /// <summary>Parses the bdhs iTunesSD back to its track list (path + file type). Empty on missing/bad file.</summary>
    public static List<ShuffleSdTrack> Read(string iTunesDir)
    {
        var path = Path.Combine(iTunesDir, "iTunesSD");
        var result = new List<ShuffleSdTrack>();
        if (!File.Exists(path))
        {
            return result;
        }
        var b = File.ReadAllBytes(path);
        if (b.Length < BdhsSize || b[0] != (byte)'b' || b[1] != (byte)'d' || b[2] != (byte)'h' || b[3] != (byte)'s')
        {
            return result;
        }

        int trackCount = (int)U32(b, 12);
        int hthsOff = (int)U32(b, 36);
        if (hthsOff <= 0 || hthsOff + 20 > b.Length)
        {
            return result;
        }
        int table = hthsOff + 20;   // hths: magic(4)+len(4)+count(4)+2×unk(8) then the offset table
        for (int i = 0; i < trackCount && table + i * 4 + 4 <= b.Length; i++)
        {
            int off = (int)U32(b, table + i * 4);
            if (off <= 0 || off + RthsSize > b.Length || b[off] != (byte)'r') { continue; }
            // rths: magic(4) len(4) start(4) stop(4) volume(4) filetype(4) then the 256-byte path.
            int start = (int)U32(b, off + 0x08);
            int stop = (int)U32(b, off + 0x0C);
            int fileType = (int)U32(b, off + 0x14);
            var ipodPath = Encoding.UTF8.GetString(b, off + RthsPathOffset, PathBytes).TrimEnd('\0');
            result.Add(new ShuffleSdTrack(ipodPath, fileType, 100, start, stop));
        }
        return result;
    }

    private static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    /// <summary>Little-endian growable binary sink with absolute-offset back-patching.</summary>
    private sealed class Le
    {
        private readonly MemoryStream _ms = new();
        public long Pos => _ms.Position;
        public void Magic(string s) => _ms.Write(Encoding.ASCII.GetBytes(s));
        public void U8(byte v) => _ms.WriteByte(v);
        public void U16(ushort v) { _ms.WriteByte((byte)v); _ms.WriteByte((byte)(v >> 8)); }
        public void U32(uint v) { for (int i = 0; i < 4; i++) { _ms.WriteByte((byte)(v >> (i * 8))); } }
        public void U64(ulong v) { for (int i = 0; i < 8; i++) { _ms.WriteByte((byte)(v >> (i * 8))); } }
        public void StringPad(string s, int total)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            if (bytes.Length > total) { bytes = bytes[..total]; }
            _ms.Write(bytes);
            for (int i = bytes.Length; i < total; i++) { _ms.WriteByte(0); }
        }
        public void PatchU32(long offset, uint v)
        {
            long save = _ms.Position;
            _ms.Position = offset;
            U32(v);
            _ms.Position = save;
        }
        public byte[] ToArray() => _ms.ToArray();
    }
}
