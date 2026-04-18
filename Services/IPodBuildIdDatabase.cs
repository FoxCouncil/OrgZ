// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

/// <summary>
/// iPod firmware build-ID → human version resolver. Apple encrypts the osos image on
/// 5G and later so the human version string never appears as plaintext on disk —
/// it has to be derived from the <c>vers</c> field in the firmware image directory
/// (which IS plaintext in the directory header).
///
/// Two distinct encoding schemes depending on iPod generation:
///
/// <para><b>Linear 0x00MMppRR</b> — 1G through 4G, Mini 1G/2G, Photo, and 5G/5.5G
/// pre-1.3 firmware releases. Apple's classic Mac "vers" resource format:</para>
/// <code>
///   byte 0 (MSB): major version (plain binary int 0–255)
///   byte 1:       packed BCD — hi nibble = minor, lo nibble = patch
///   byte 2:       release stage (0x20=dev, 0x40=alpha, 0x60=beta, 0x80=release)
///   byte 3 (LSB): pre-release build number
/// </code>
/// Examples from real captures (dstaley/ipod-sysinfo):
/// <list type="bullet">
///   <item>0x02308000 → "2.3"     (iPod 3G)</item>
///   <item>0x03118000 → "3.1.1"   (iPod 4G)</item>
///   <item>0x04218000 → "4.2.1"   (Photo 4G internal buildID)</item>
///   <item>0x01218000 → "1.2.1"   (Photo 4G visibleBuildID)</item>
///   <item>0x02618000 → "2.6.1"   (Mini 1G/2G internal)</item>
///   <item>0x01418000 → "1.4.1"   (Mini 1G/2G visible)</item>
/// </list>
/// NB: Apple tracks two parallel version numbers per release — an internal "buildID"
/// and a user-facing "visibleBuildID". Our <c>osos.vers</c> field typically contains
/// the internal one, so decoded versions may differ from what the iPod's About screen
/// shows (e.g. Photo 4G osos.vers="4.2.1" but user sees "1.2.1"). We decode whatever
/// we find and mark it as "internal" if no visible-form override is in the table.
///
/// <para><b>Build-tag 0x0000Bxxx</b> — introduced around the iPod Video 5.5G for the
/// 1.3 firmware series and carried forward to Classic 6G/6.5G/7G, Nano 4G+, Shuffle
/// 3G+, Touch. Sequential 16-bit build counter in the low word. Not algorithmically
/// decodable; requires the per-(generation, buildId) table below.</para>
///
/// Table seeded from the user's FOXPOD (Video 5.5G 1.3 = 0x0000B012) and grows with
/// community contributions. The diagnostic log writes every MISS so unknown entries
/// can be added from real captures.
/// </summary>
public static class IPodBuildIdDatabase
{
    /// <summary>
    /// Resolves a (generation, buildId) pair to a human firmware version. Tries the
    /// explicit lookup table first, then falls back to algorithmic BCD decoding for
    /// the linear form used on pre-5.5G iPods. Returns null when the value is neither
    /// in the table nor recognizably linear.
    /// </summary>
    public static string? LookupVersion(string? generation, uint buildId)
    {
        // Table lookup — always preferred when a known entry exists
        if (!string.IsNullOrWhiteSpace(generation)
            && _table.TryGetValue((generation, buildId), out var tableHit))
        {
            return tableHit;
        }

        // Algorithmic decode for the linear 0x00MMppRR form
        return DecodeLinearForm(buildId);
    }

    /// <summary>
    /// Decodes the classic Mac "vers"-resource form used on 1G through 4G iPods
    /// (and the pre-1.3 firmware of 5G/5.5G). Returns null if the value doesn't
    /// structurally match the format (e.g., non-BCD minor/patch nibbles, or a
    /// non-release stage byte). Caller should treat a null return as "unknown"
    /// and fall through to the hex display.
    /// </summary>
    private static string? DecodeLinearForm(uint vers)
    {
        byte major = (byte)((vers >> 24) & 0xFF);
        byte minorPatch = (byte)((vers >> 16) & 0xFF);
        byte stage = (byte)((vers >> 8) & 0xFF);

        // Reject obviously non-linear values — 5.5G 1.3 and newer build-tag form
        // has major=0, which fails this check and falls through to the table.
        if (major == 0)
        {
            return null;
        }

        // Validate BCD nibbles in byte 1
        int minor = (minorPatch >> 4) & 0x0F;
        int patch = minorPatch & 0x0F;
        if (minor > 9 || patch > 9)
        {
            return null;
        }

        // Release stage 0x80 is final-release; other stages exist (dev/alpha/beta)
        // but we don't annotate them in the display for brevity.
        if (stage != 0x20 && stage != 0x40 && stage != 0x60 && stage != 0x80)
        {
            return null;
        }

        // Drop the patch when it's zero ("2.3" not "2.3.0")
        return patch == 0
            ? $"{major}.{minor}"
            : $"{major}.{minor}.{patch}";
    }

    // Per-generation, per-buildID override table. Used when either:
    //   • The value uses the build-tag form (0x0000Bxxx) which isn't algorithmically
    //     decodable (all 5.5G 1.3+, Classic, Nano 4G+, Shuffle 3G+, Touch), OR
    //   • We want to display the "visibleBuildID" instead of the algorithmic buildID
    //     decode (e.g., Photo 4G where the two differ).
    // Keys use the same generation strings that IPodModelDatabase emits.
    private static readonly Dictionary<(string generation, uint buildId), string> _table = new()
    {
        // ===== iPod Video 5G — pre-1.3 uses linear form (handled algorithmically); =====
        // ===== 1.3 series uses build-tag form (needs table) =====
        [("Video 5G", 0x0000B011)] = "1.3",
        [("Video 5G", 0x0000B012)] = "1.3",
        [("Video 5G", 0x0000B021)] = "1.3.1",

        // ===== iPod Video 5.5G — same as 5G, 1.3 series needs the table =====
        [("Video 5.5G", 0x0000B011)] = "1.3",       // early 1.3 build
        [("Video 5.5G", 0x0000B012)] = "1.3",       // final 1.3 (FOXPOD)
        [("Video 5.5G", 0x0000B021)] = "1.3.1",

        // ===== Photo (4G variant) — algorithmic decode gives "4.2.1" (internal), =====
        // ===== but we override to the visibleBuildID form the user actually sees =====
        [("Photo", 0x04218000)] = "1.2.1",

        // ===== Mini 1G/2G — same internal-vs-visible discrepancy =====
        [("Mini 1G", 0x02618000)] = "1.4.1",
        [("Mini 2G", 0x02618000)] = "1.4.1",

        // ===== Classic 6G / 6.5G / 7G — build-tag form, community data needed =====
        // [("Classic 6G", 0x????????)] = "1.0",
        // [("Classic 6G", 0x????????)] = "1.1",
        // [("Classic 6G", 0x????????)] = "1.1.1",
        // [("Classic 6G", 0x????????)] = "1.1.2",
        // [("Classic 6G", 0x????????)] = "2.0",
        // [("Classic 6G", 0x????????)] = "2.0.1",
        // [("Classic 6G", 0x????????)] = "2.0.3",
        // [("Classic 6G", 0x????????)] = "2.0.4",

        // ===== Nano 4G/5G/6G/7G — build-tag form, community data needed =====
        // Populate from firmware partition dumps via the diagnostic log's MISS message.
    };
}
