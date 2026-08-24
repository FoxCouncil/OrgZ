// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Helpers;

namespace OrgZ.Tests;

/// <summary>
/// Six separate filename sanitizers collapsed into <see cref="SafeName"/>. Their outputs are on
/// disk in real libraries and on real iPods, so the consolidation had to be behaviour-preserving,
/// not behaviour-improving: each test below reimplements the original expression the style
/// replaced and asserts the shared helper still agrees with it, character for character.
///
/// The oracles pin the invalid-character set as a literal instead of calling
/// Path.GetInvalidFileNameChars(). That call is the one the implementation itself makes, so
/// an oracle built from it is self-referential - and on Linux and macOS .NET returns only
/// { '\0', '/' }, so both sides would degrade in lockstep while ':' '?' '*' '"' '|' and
/// '\' sailed straight through into a FAT32 iPod path, or into a library folder that later
/// has to open on Windows. The set below is the Windows/FAT rule, which is the rule every
/// consumer of these names needs whichever host happened to run the rip or the sync.
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

    /// <summary>
    /// The characters a path segment must never contain, on every host: the C0 controls plus
    /// the nine reserved punctuation characters. Byte-for-byte what
    /// Path.GetInvalidFileNameChars() answers on Windows - pinned by
    /// <see cref="The_pinned_set_is_exactly_the_windows_rule"/> - so names already on disk and
    /// on devices keep resolving.
    /// </summary>
    private static readonly char[] Invalid = [.. Enumerable.Range(0, 32).Select(c => (char)c), '"', '<', '>', '|', ':', '*', '?', '\\', '/'];

    [SkippableFact]
    public void The_pinned_set_is_exactly_the_windows_rule()
    {
        // Windows is the only host whose framework answer is the one we want, so it is the
        // only host that can check the literal above. Everywhere else the literal IS the rule.
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows is the reference for the invalid-character set");

        Assert.Equal(Path.GetInvalidFileNameChars().Order(), Invalid.Order());
    }

    // ── The original implementations, verbatim, as the oracle ──

    private static string OldSanitizeFileName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(Invalid.Contains(c) ? '_' : c);
        }
        return sb.ToString().Trim();
    }

    private static string OldSanitizeFolderName(string s)
        => string.Join("_", s.Split(Invalid)).TrimEnd('.', ' ');

    private static string OldIPodSanitize(string s)
        => string.Join("_", s.Split(Invalid));

    private static string OldSanitizeSegment(string s)
    {
        var cleaned = new string(s.Select(c => Invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length > 0 ? cleaned : "Unknown";
    }

    private static string OldSanitizeForFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var result = new char[value.Length];
        int len = 0;
        foreach (var ch in value)
        {
            if (ch < 0x20 || Array.IndexOf(Invalid, ch) >= 0)
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
    public void Every_style_handles_the_reserved_characters_the_same_way_on_every_platform()
    {
        // Literal expectations, not a framework round-trip: these names land on FAT32 iPods
        // and in libraries that get synced from Windows, so the rule cannot vary with the
        // host that produced them.
        const string reserved = "a<b>c:d\"e/f\\g|h?i*j";

        Assert.Equal("a_b_c_d_e_f_g_h_i_j", SafeName.ReplaceOnly(reserved));
        Assert.Equal("a_b_c_d_e_f_g_h_i_j", SafeName.For(reserved, SafeName.Style.Replace));
        Assert.Equal("a_b_c_d_e_f_g_h_i_j", SafeName.For(reserved, SafeName.Style.ReplaceTrimTrailing));
        Assert.Equal("a_b_c_d_e_f_g_h_i_j", SafeName.For(reserved, SafeName.Style.ReplaceOrUnknown));
        Assert.Equal("abcdefghij", SafeName.For(reserved, SafeName.Style.Drop));

        // Control characters are replaced by every style but Drop, which removes them.
        Assert.Equal("Tab_Separated", SafeName.ReplaceOnly("Tab\tSeparated"));
        Assert.Equal("TabSeparated", SafeName.For("Tab\tSeparated", SafeName.Style.Drop));
    }

    [Fact]
    public void Null_is_handled_without_throwing()
    {
        Assert.Equal(string.Empty, SafeName.For(null, SafeName.Style.Drop));
        Assert.Equal(string.Empty, SafeName.For(null, SafeName.Style.Replace));
        Assert.Equal("Unknown", SafeName.For(null, SafeName.Style.ReplaceOrUnknown));
    }
}
