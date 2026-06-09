// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

/// <summary>iTunesDB integrity check a generation requires before the firmware trusts the DB.</summary>
public enum IPodChecksum
{
    /// <summary>Pre-2007 click-wheel iPods: no checksum, DB written as-is.</summary>
    None,
    /// <summary>Classic 6G–7G, Nano 3G/4G (2007–08): HMAC-SHA1 keyed off the FireWireGuid.</summary>
    Hash58,
    /// <summary>Nano 5G (2009): hash72 + SQLite "iTunes Library.itlp" + compressed iTunesCDB.</summary>
    Hash72,
    /// <summary>Nano 6G/7G (2010+): hashAB + SQLite + compressed iTunesCDB.</summary>
    HashAB,
}

/// <summary>
/// Per-generation iPod sync capabilities, keyed by the <c>IpodGeneration</c>
/// strings from <see cref="IPodModelDatabase"/> (e.g. "Video 5.5G", "Nano 4G").
///
/// Tables sourced from iOpenPod's <c>ipod_device/capabilities.py</c> + libgpod's
/// model/artwork tables. Conservative on writing: a generation is only declared
/// writable once its checksum is actually implemented — writing the wrong art
/// correlation IDs blanks/crashes the display, and a missing/bad iTunesDB
/// checksum makes the iPod show "0 songs". The cover-format IDs are populated for
/// every generation so artwork is ready the moment its tier becomes writable.
/// </summary>
public static class IPodCapabilities
{
    private sealed record Caps(
        (int FormatId, int Width, int Height)[] CoverFormats,
        IPodChecksum Checksum);

    // Cover-art .ithmb formats (correlation id + pixel size, RGB565-LE) per generation.
    private static readonly Dictionary<string, Caps> _table = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Tier 1: no checksum (writable today) ──────────────────────────
        ["1G"]         = new([], IPodChecksum.None),            // monochrome, no artwork
        ["2G"]         = new([], IPodChecksum.None),
        ["3G"]         = new([], IPodChecksum.None),
        ["4G"]         = new([], IPodChecksum.None),
        ["Mini 1G"]    = new([], IPodChecksum.None),            // monochrome
        ["Mini 2G"]    = new([], IPodChecksum.None),
        ["Photo"]      = new([(1017, 56, 56), (1016, 140, 140)], IPodChecksum.None),
        ["Nano 1G"]    = new([(1031, 42, 42), (1027, 100, 100)], IPodChecksum.None),
        ["Nano 2G"]    = new([(1031, 42, 42), (1027, 100, 100)], IPodChecksum.None),
        ["Video 5G"]   = new([(1028, 100, 100), (1029, 200, 200)], IPodChecksum.None),   // validated
        ["Video 5.5G"] = new([(1028, 100, 100), (1029, 200, 200)], IPodChecksum.None),   // validated

        // ── Tier 2: hash58 (FireWireGuid HMAC-SHA1) ───────────────────────
        ["Classic 6G"]   = new([(1055, 128, 128), (1061, 56, 56), (1068, 128, 128), (1060, 320, 320)], IPodChecksum.Hash58),
        ["Classic 6.5G"] = new([(1055, 128, 128), (1061, 56, 56), (1068, 128, 128), (1060, 320, 320)], IPodChecksum.Hash58),
        ["Classic 7G"]   = new([(1055, 128, 128), (1061, 56, 56), (1068, 128, 128), (1060, 320, 320)], IPodChecksum.Hash58),
        ["Nano 3G"]      = new([(1061, 56, 56), (1055, 128, 128), (1068, 128, 128), (1060, 320, 320)], IPodChecksum.Hash58),
        ["Nano 4G"]      = new([(1055, 128, 128), (1068, 128, 128), (1071, 240, 240), (1074, 50, 50), (1078, 80, 80), (1084, 240, 240)], IPodChecksum.Hash58),

        // ── Tier 3: hash72/hashAB + SQLite/compressed CDB (separate stack) ─
        ["Nano 5G"] = new([(1056, 128, 128), (1078, 80, 80), (1073, 240, 240), (1074, 50, 50)], IPodChecksum.Hash72),
        ["Nano 6G"] = new([(1073, 240, 240), (1085, 88, 88), (1089, 58, 58), (1074, 50, 50)], IPodChecksum.HashAB),

        // Shuffles (iTunesSD, no screen) and Touch (iOS/SQLite) are intentionally absent.
    };

    /// <summary>The checksum a generation requires, or null if we don't know the model.</summary>
    public static IPodChecksum? ChecksumFor(string? generation)
        => generation is not null && _table.TryGetValue(generation, out var c) ? c.Checksum : null;

    /// <summary>
    /// Cover-art formats for a generation, or empty when the generation has no
    /// artwork (or is unknown) — in which case the track imports without art.
    /// </summary>
    public static IReadOnlyList<(int FormatId, int Width, int Height)> CoverFormatsFor(string? generation)
        => generation is not null && _table.TryGetValue(generation, out var c) ? c.CoverFormats : [];

    /// <summary>
    /// True when we can safely write this generation's iTunesDB today: no-checksum
    /// click-wheel models plus hash58 (Classic 6G–7G, Nano 3G/4G — needs the
    /// FireWireGuid). hash72/hashAB stay off — those need the SQLite stack and, for
    /// hashAB, a proprietary algorithm not available in open source.
    /// </summary>
    public static bool SupportsDatabaseWrite(string? generation)
        => ChecksumFor(generation) is IPodChecksum.None or IPodChecksum.Hash58;
}
