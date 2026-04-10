// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Security.Cryptography;
using System.Text;

namespace OrgZ.Services;

/// <summary>
/// Computes MusicBrainz DiscIDs from CD TOC data per the spec at
/// https://musicbrainz.org/doc/Disc_ID_Calculation
/// </summary>
public static class DiscIdService
{
    /// <summary>
    /// Computes the MusicBrainz DiscID from a TOC.
    /// </summary>
    /// <param name="firstTrack">First track number (usually 1).</param>
    /// <param name="lastTrack">Last track number.</param>
    /// <param name="trackOffsets">LBA offsets for each track (index 0 = track 1).</param>
    /// <param name="leadOutOffset">LBA offset of the lead-out track.</param>
    /// <returns>The base64-encoded DiscID string (MusicBrainz format).</returns>
    public static string ComputeDiscId(int firstTrack, int lastTrack, int[] trackOffsets, int leadOutOffset)
    {
        // Build the input string: first_track (2 hex), last_track (2 hex),
        // lead-out offset (8 hex), then 99 track offsets (8 hex each, 0-padded)
        var sb = new StringBuilder(808);
        sb.AppendFormat("{0:X2}", firstTrack);
        sb.AppendFormat("{0:X2}", lastTrack);
        sb.AppendFormat("{0:X8}", leadOutOffset);

        for (int i = 0; i < 99; i++)
        {
            if (i < trackOffsets.Length)
            {
                sb.AppendFormat("{0:X8}", trackOffsets[i]);
            }
            else
            {
                sb.Append("00000000");
            }
        }

        // SHA-1 hash
        var hash = SHA1.HashData(Encoding.ASCII.GetBytes(sb.ToString()));

        // Base64 with MusicBrainz's custom alphabet: + → . , / → _ , = → -
        var b64 = Convert.ToBase64String(hash);
        return b64.Replace('+', '.').Replace('/', '_').Replace('=', '-');
    }

    /// <summary>
    /// Converts MSF (minutes/seconds/frames) to LBA (Logical Block Address).
    /// Red Book: 75 frames per second.
    /// </summary>
    public static int MsfToLba(int minutes, int seconds, int frames)
    {
        return (minutes * 60 + seconds) * 75 + frames;
    }

    /// <summary>
    /// Builds the MusicBrainz TOC string for fuzzy lookup.
    /// Format: "firstTrack lastTrack leadOutOffset offset1 offset2 ..."
    /// </summary>
    public static string BuildTocString(int firstTrack, int lastTrack, int[] trackOffsets, int leadOutOffset)
    {
        var parts = new List<string>
        {
            firstTrack.ToString(),
            lastTrack.ToString(),
            leadOutOffset.ToString()
        };

        foreach (var offset in trackOffsets)
        {
            parts.Add(offset.ToString());
        }

        return string.Join("+", parts);
    }
}
