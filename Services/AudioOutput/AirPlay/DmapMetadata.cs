// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// The DMAP ("digital media access protocol") tagging iTunes uses to push now-playing
/// information to a RAOP receiver, sent as a SET_PARAMETER body with content type
/// <c>application/x-dmap-tagged</c>. The whole message is one <c>mlit</c> listing item.
///
/// The CONTENTS are not free-form, which is the part that looks like it should be. The field
/// set and its order are copied from a real iPhone rather than reasoned about: <c>mper</c>,
/// <c>asal</c>, <c>asar</c>, <c>ascp</c>, <c>asgn</c>, <c>minm</c>, <c>asdk</c>, <c>caps</c>,
/// <c>astm</c> - album before artist, the title LATE, composer and genre present but empty.
/// A body of title/artist/album alone is well-formed DMAP and still gets ignored, with a
/// 200 in reply, which is a remarkably quiet way to fail.
///
/// Encoding is <see cref="DmapWriter"/>'s: one encoder for this wire format, so the widths
/// cannot drift apart.
/// </summary>
internal static class DmapMetadata
{
    /// <summary>
    /// Builds the DMAP body for a track. Empty text fields are omitted rather than sent
    /// blank - a receiver renders an empty tag as empty text, which reads as a bug on the
    /// speaker's display.
    /// </summary>
    public static byte[] Build(string? title, string? artist, string? album, TimeSpan? duration)
    {
        // The EXACT field set and order a real iPhone sends, decoded byte-for-byte from a
        // capture (2026-08-16, iPhone -> logging receiver). The two OrgZ was missing are what
        // left the tile blank:
        //   - caps: a 1-byte flag = 1. The iPhone always sends it; nothing else does the job
        //     it does (the HomePod uses it to accept the item as playable/now-playing).
        //   - the ORDER, with minm LATE and asal before asar - matched here exactly rather
        //     than reasoned about, because the earlier field-guessing was all wrong.
        // ascp (composer) and asgn (genre) the iPhone sends even when empty/short; included
        // as empty so the frame count and shape match.
        return new DmapWriter()
            .Long("mper", PersistentId(title, artist, album))   // item identity
            .String("asal", album)                              // album
            .String("asar", artist)                             // artist
            .Empty("ascp")                                      // composer - present but empty, as the iPhone sends it
            .Empty("asgn")                                      // genre - present but empty
            .String("minm", title)                              // item name (track title) - LATE, as the iPhone does
            .Char("asdk", 0)                                    // data kind
            .Char("caps", 1)                                    // THE missing flag - item is playable/now-playing
            .Int("astm", duration is { TotalMilliseconds: > 0 } d ? (int)d.TotalMilliseconds : 0)
            .Wrap("mlit");
    }

    /// <summary>A stable 64-bit persistent id for a track, from its text - FNV-1a, non-zero.</summary>
    private static long PersistentId(string? title, string? artist, string? album)
    {
        const ulong Offset = 14695981039346656037;
        const ulong Prime = 1099511628211;

        var hash = Offset;
        foreach (var b in Encoding.UTF8.GetBytes($"{title}\0{artist}\0{album}"))
        {
            hash = (hash ^ b) * Prime;
        }

        return (long)(hash | 1);   // never zero - a zero persistent id reads as "no item"
    }
}
