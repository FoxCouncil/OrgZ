// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

/// <summary>
/// iTunes media-kind values, owned in one place for every layer that reads or writes them: the
/// binary iTunesDB (MHIT media_type at <see cref="MhitOffset"/>, written by <see cref="ITunesDbWriter"/>
/// and read back by <see cref="ITunesDbReader"/>), the Nano 5G SQLite library (item.media_kind), and
/// the device-side MediaItem projection. Keeps the reader/writer offset agreement and the
/// value→kind mapping from drifting across files.
/// </summary>
public static class ITunesMediaType
{
    public const int Audio = 1;
    public const int Podcast = 4;      // ITDB_MEDIATYPE_PODCAST
    public const int Audiobook = 8;    // ITDB_MEDIATYPE_AUDIOBOOK

    /// <summary>Offset of the u32 media_type field inside an MHIT header (libgpod layout).</summary>
    public const int MhitOffset = 0xD0;

    /// <summary>Maps an iTunes media kind onto OrgZ's MediaKind; unknown values read as Music.</summary>
    public static MediaKind ToKind(int mediaType) => mediaType switch
    {
        Podcast => MediaKind.Podcast,
        Audiobook => MediaKind.Audiobook,
        _ => MediaKind.Music,
    };
}
