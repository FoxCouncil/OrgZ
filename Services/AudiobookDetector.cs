// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

/// <summary>
/// Decides which local files are audiobooks. Two layers, applied at different pipeline stages:
/// the extension (free - .m4b IS the audiobook container, so the kind is known at scan time with
/// no file IO) and the tags (needs the TagLib handle the analyzer already has open - the iTunes
/// MP4 media-type atom, or an explicit audiobook genre). Plain MP3 rips carry no container
/// signal, so a genre tag is their only automatic path; anything else is the user's explicit
/// "mark as audiobook" action.
/// </summary>
public static class AudiobookDetector
{
    /// <summary>The iTunes MP4 "stik" media-type atom value iTunes writes for Media Kind = Audiobook.</summary>
    private const byte StikAudiobook = 2;

    /// <summary>
    /// The library's audiobook home: store downloads land here, and dropping your own files in IS
    /// the import gesture - anything under it is an audiobook by location, tags or no tags.
    /// </summary>
    public const string AudiobooksFolderName = ".audiobooks";

    public static bool IsAudiobookExtension(string? extension)
        => string.Equals(extension, ".m4b", StringComparison.OrdinalIgnoreCase);

    /// <summary>Extension-or-location kind for a freshly scanned path - no file IO, safe for the fast scan pass.</summary>
    public static MediaKind KindForPath(string filePath)
        => IsAudiobookExtension(Path.GetExtension(filePath)) || IsInAudiobooksFolder(filePath)
            ? MediaKind.Audiobook
            : MediaKind.Music;

    /// <summary>Whether any directory segment of the path is the .audiobooks folder.</summary>
    internal static bool IsInAudiobooksFolder(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(dir))
        {
            if (string.Equals(Path.GetFileName(dir), AudiobooksFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    /// <summary>
    /// Whether an open file's tags identify it as an audiobook: the iTunes stik atom (what iTunes
    /// sets on a "Media Kind: Audiobook" .m4a) or a genre containing "audiobook".
    /// </summary>
    public static bool TagsSayAudiobook(TagLib.File file)
    {
        if (file.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
        {
            foreach (var box in apple.DataBoxes("stik"))
            {
                if (IsAudiobookStik(box.Data?.Data))
                {
                    return true;
                }
            }
        }

        return IsAudiobookGenre(file.Tag.JoinedGenres);
    }

    /// <summary>The stik atom is a big-endian integer payload; the media-type value is its last byte.</summary>
    internal static bool IsAudiobookStik(byte[]? stikData)
        => stikData is { Length: > 0 } && stikData[^1] == StikAudiobook;

    internal static bool IsAudiobookGenre(string? joinedGenres)
        => joinedGenres?.Contains("audiobook", StringComparison.OrdinalIgnoreCase) == true;
}
