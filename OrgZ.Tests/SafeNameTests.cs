// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Helpers;

namespace OrgZ.Tests;

/// <summary>
/// Six separate filename sanitizers collapsed into <see cref="SafeName"/>. Their outputs are on
/// disk in real libraries and on real iPods, so the consolidation had to be behaviour-preserving,
/// not behaviour-improving: each test below reimplements the original expression the style
/// replaced and asserts the shared helper still agrees with it, character for character.
/// </summary>
public class SafeNameTests
{
    private static readonly string[] Samples =
    [
        "Simple Name",
        "AC/DC",
        @"Back\Slash",
        "Colon: The Sequel",
        "Question?Mark",
        "Star*Struck",
        "Pipe|Dream",
        "Quote\"Unquote",
        "<Angle>Brackets",
        "Trailing dot.",
        "Trailing dots...",
        "Trailing space ",
        "Trailing dot and space . ",
        " Leading space",
        "...Baby One More Time",
        "Multiple///Invalid",
        "",
        "   ",
        "...",
        "BellControl",
        "Tab\tSeparated",
        "Ünïcödé Stäys",
    ];

    // ── The original implementations, verbatim, as the oracle ──

    private static string OldSanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }
        return sb.ToString().Trim();
    }

    private static string OldSanitizeFolderName(string s)
        => string.Join("_", s.Split(Path.GetInvalidFileNameChars())).TrimEnd('.', ' ');

    private static string OldIPodSanitize(string s)
        => string.Join("_", s.Split(Path.GetInvalidFileNameChars()));

    private static string OldSanitizeSegment(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length > 0 ? cleaned : "Unknown";
    }

    private static string OldSanitizeForFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var result = new char[value.Length];
        int len = 0;
        foreach (var ch in value)
        {
            if (ch < 0x20 || Array.IndexOf(invalid, ch) >= 0)
            {
                continue;
            }

            result[len++] = ch;
        }

        return new string(result, 0, len).Trim().TrimEnd('.');
    }

    // ── Equivalence ──

    [Fact]
    public void Replace_matches_the_old_export_file_name_rules()
    {
        foreach (var sample in Samples)
        {
            Assert.Equal(OldSanitizeFileName(sample), SafeName.For(sample, SafeName.Style.Replace));
        }
    }

    [Fact]
    public void ReplaceTrimTrailing_matches_the_old_library_folder_rules()
    {
        foreach (var sample in Samples)
        {
            Assert.Equal(OldSanitizeFolderName(sample), SafeName.For(sample, SafeName.Style.ReplaceTrimTrailing));
        }
    }

    [Fact]
    public void ReplaceOnly_matches_the_old_on_device_path_rules()
    {
        // The device style trims nothing - an on-device path has to keep matching what the
        // iPod's own database already says.
        foreach (var sample in Samples)
        {
            Assert.Equal(OldIPodSanitize(sample), SafeName.ReplaceOnly(sample));
        }
    }

    [Fact]
    public void ReplaceOrUnknown_matches_the_old_download_segment_rules()
    {
        foreach (var sample in Samples)
        {
            Assert.Equal(OldSanitizeSegment(sample), SafeName.For(sample, SafeName.Style.ReplaceOrUnknown));
        }
    }

    [Fact]
    public void Drop_matches_the_old_cd_rip_rules()
    {
        foreach (var sample in Samples)
        {
            Assert.Equal(OldSanitizeForFileName(sample), SafeName.For(sample, SafeName.Style.Drop));
        }
    }

    // ── The differences between styles are real and deliberate ──

    [Fact]
    public void The_styles_genuinely_differ_so_none_can_be_quietly_merged()
    {
        // Note a space is a LEGAL filename character - only the trimming differs.
        const string awkward = " AC/DC. ";

        Assert.Equal(" AC_DC. ", SafeName.ReplaceOnly(awkward));                              // nothing trimmed at all
        Assert.Equal("AC_DC.", SafeName.For(awkward, SafeName.Style.Replace));                // Trim() both ends
        Assert.Equal(" AC_DC", SafeName.For(awkward, SafeName.Style.ReplaceTrimTrailing));    // TrimEnd('.', ' ') only
        Assert.Equal("ACDC", SafeName.For(awkward, SafeName.Style.Drop));                     // dropped, not replaced

        // Only the download style invents a fallback, and only when nothing survives.
        Assert.Equal("Unknown", SafeName.For("...", SafeName.Style.ReplaceOrUnknown));
        Assert.Equal("...", SafeName.For("...", SafeName.Style.Replace));
        Assert.Equal(string.Empty, SafeName.For("///", SafeName.Style.Drop));
        Assert.Equal("___", SafeName.For("///", SafeName.Style.Replace));
    }

    [Fact]
    public void Null_is_handled_without_throwing()
    {
        Assert.Equal(string.Empty, SafeName.For(null, SafeName.Style.Drop));
        Assert.Equal(string.Empty, SafeName.For(null, SafeName.Style.Replace));
        Assert.Equal("Unknown", SafeName.For(null, SafeName.Style.ReplaceOrUnknown));
    }
}
