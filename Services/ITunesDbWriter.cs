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
    public long FileSize { get; init; }
    public int LengthMs { get; init; }
    public int Bitrate { get; init; }
    public int SampleRate { get; init; }
    public int Year { get; init; }
    public int TrackNumber { get; init; }
    public DateTime DateAddedUtc { get; init; }

    /// <summary>64-bit persistent id. Must match the ArtworkDB mhii.song_id when art is attached.</summary>
    public ulong Dbid { get; init; }
    public bool HasArtwork { get; init; }
    public int ArtworkSize { get; init; }
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

    public static void AddTrack(ITunesDbDocument doc, NewTrack track)
    {
        var tracksMhsd = FindMhsd(doc, type: 1)
            ?? throw new InvalidDataException("iTunesDB has no track dataset (MHSD type 1).");

        tracksMhsd.Children.Add(BuildMhit(track));

        // Reference the track from every master playlist. iPods read the v2 list
        // (MHSD type 3); libgpod keeps the legacy type-2 list in sync, so we add
        // the MHIP to both masters to match what the firmware/iTunes expect.
        bool referenced = false;
        foreach (var master in MasterPlaylists(doc))
        {
            master.Children.Add(BuildMhip(track.TrackId));
            referenced = true;
        }
        if (!referenced)
        {
            throw new InvalidDataException("iTunesDB has no master playlist to add the track to.");
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
        mhit.WriteHeaderInt32(0x24, (int)t.FileSize);
        mhit.WriteHeaderInt32(0x28, t.LengthMs);
        mhit.WriteHeaderInt32(0x2C, t.TrackNumber);
        mhit.WriteHeaderInt32(0x34, t.Year);
        mhit.WriteHeaderInt32(0x38, t.Bitrate);
        mhit.WriteHeaderInt32(0x3C, t.SampleRate << 16);
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

        // String MHODs. Location (type 2) is required; the rest are added when present.
        AddStringMhod(mhit, 2, t.IpodPath);
        AddStringMhod(mhit, 1, t.Title);
        AddStringMhod(mhit, 3, t.Album);
        AddStringMhod(mhit, 4, t.Artist);
        AddStringMhod(mhit, 5, t.Genre);
        return mhit;
    }

    private static void AddStringMhod(ITunesDbChunk parent, int mhodType, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        parent.Children.Add(BuildStringMhod(mhodType, text));
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

    private static ITunesDbChunk BuildMhip(uint trackId)
    {
        var mhip = new ITunesDbChunk { Magic = "mhip", Header = new byte[MhipHeaderSize] };
        WriteAscii(mhip.Header, 0, "mhip");
        mhip.WriteHeaderInt32(0x04, MhipHeaderSize);
        // 0x08 total size + 0x0C mhod count set by Normalize.
        mhip.WriteHeaderInt32(0x18, (int)trackId);   // referenced track id
        return mhip;
    }

    private static int MacSeconds(DateTime utc)
        => (int)(utc.ToUniversalTime() - MacEpoch).TotalSeconds;

    private static void WriteAscii(byte[] dest, int offset, string magic)
    {
        for (int i = 0; i < magic.Length; i++)
        {
            dest[offset + i] = (byte)magic[i];
        }
    }
}
