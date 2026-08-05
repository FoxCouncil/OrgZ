// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text;

namespace OrgZ.Helpers;

/// <summary>
/// Turns arbitrary tag text into something a filesystem will accept. This was six separate
/// implementations that disagreed - one replaced invalid characters with '_', another dropped
/// them, one trimmed trailing dots, one didn't - so a track could land under different names
/// depending on whether it arrived by rip, sync, playlist export, or download.
///
/// The variants are kept as <see cref="Style"/> options rather than unified: their outputs are
/// already on disk and on devices, so changing the shapes would orphan existing files.
/// </summary>
public static class SafeName
{
    public enum Style
    {
        /// <summary>Invalid chars → '_', nothing trimmed. (On-device iPod paths.)</summary>
        ReplaceOnly,

        /// <summary>Invalid chars → '_', then Trim(). (Playlist keys / export file names.)</summary>
        Replace,

        /// <summary>Invalid chars → '_', then TrimEnd('.', ' '). (Library folder + file segments.)</summary>
        ReplaceTrimTrailing,

        /// <summary>Invalid chars → '_', Trim(), TrimEnd('.'), and "Unknown" when nothing survives. (Downloads.)</summary>
        ReplaceOrUnknown,

        /// <summary>Invalid AND control chars removed outright, then Trim() and TrimEnd('.'). (CD rip names.)</summary>
        Drop,
    }

    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    /// <summary>Shorthand for <see cref="Style.ReplaceOnly"/> - the on-device iPod path form.</summary>
    public static string ReplaceOnly(string value) => For(value, Style.ReplaceOnly);

    /// <summary>Makes <paramref name="value"/> safe for one path segment, per <paramref name="style"/>.</summary>
    public static string For(string? value, Style style)
    {
        if (value is null)
        {
            return style == Style.ReplaceOrUnknown ? "Unknown" : string.Empty;
        }

        if (style == Style.Drop)
        {
            var kept = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                // Control characters are legal in a filename on some platforms; the rip
                // path has always dropped them anyway.
                if (c >= 0x20 && Array.IndexOf(Invalid, c) < 0)
                {
                    kept.Append(c);
                }
            }
            return kept.ToString().Trim().TrimEnd('.');
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(Array.IndexOf(Invalid, c) >= 0 ? '_' : c);
        }

        return style switch
        {
            // Device paths trim nothing: an iPod path is matched against what's already in
            // its database, so reshaping it would orphan tracks currently on the device.
            Style.ReplaceOnly => sb.ToString(),

            // Identical output to the string.Join("_", s.Split(invalid)).TrimEnd('.', ' ')
            // form this replaced (Split+Join collapses to per-character replacement).
            Style.ReplaceTrimTrailing => sb.ToString().TrimEnd('.', ' '),

            Style.ReplaceOrUnknown => sb.ToString().Trim().TrimEnd('.') is { Length: > 0 } cleaned ? cleaned : "Unknown",

            _ => sb.ToString().Trim(),
        };
    }
}
