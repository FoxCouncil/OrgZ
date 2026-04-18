// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

public class DeviceFingerprintHelperTests
{
    // ===== ExtractAppleFireWireGuid — finds 16-hex-char Apple FW GUID inside USB descriptors =====

    [Fact]
    public void ExtractAppleFireWireGuid_finds_GUID_at_string_start()
    {
        // Apple OUI is 000A27 — 16 chars total for the full 64-bit GUID
        var input = "000A2700153A9E6B";
        Assert.Equal("000A2700153A9E6B", DeviceFingerprint.ExtractAppleFireWireGuid(input));
    }

    [Fact]
    public void ExtractAppleFireWireGuid_finds_GUID_embedded_in_iFlash_synthetic_serial()
    {
        // iFlash bridge prepends 11 chars before embedding the real iPod GUID
        var input = "10000000000000A2700153A9E6BTRAILING";
        var result = DeviceFingerprint.ExtractAppleFireWireGuid(input);
        Assert.Equal("000A2700153A9E6B", result);
    }

    [Fact]
    public void ExtractAppleFireWireGuid_lowercase_is_normalized()
    {
        Assert.Equal("000A2700153A9E6B", DeviceFingerprint.ExtractAppleFireWireGuid("000a2700153a9e6b"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NoOuiHere")]
    [InlineData("FFFFFFFFFFFFFFFF")]   // hex but no Apple OUI
    public void ExtractAppleFireWireGuid_returns_null_for_non_Apple_inputs(string? input)
    {
        Assert.Null(DeviceFingerprint.ExtractAppleFireWireGuid(input));
    }

    [Fact]
    public void ExtractAppleFireWireGuid_returns_null_when_too_few_chars_after_OUI()
    {
        // OUI present but only 12 total chars — need 16 for full GUID
        Assert.Null(DeviceFingerprint.ExtractAppleFireWireGuid("000A27001234"));
    }

    [Fact]
    public void ExtractAppleFireWireGuid_returns_null_when_OUI_followed_by_non_hex()
    {
        Assert.Null(DeviceFingerprint.ExtractAppleFireWireGuid("000A27ZZZZZZZZZZ"));
    }

    // ===== MapRockboxTargetToGeneration — Rockbox target string → libgpod generation =====

    [Theory]
    [InlineData("ipod1g2g",   "2G")]
    [InlineData("ipod3g",     "3G")]
    [InlineData("ipod4g",     "4G")]
    [InlineData("ipodcolor",  "Photo")]
    [InlineData("ipodphoto",  "Photo")]
    [InlineData("ipodmini1g", "Mini 1G")]
    [InlineData("ipodmini2g", "Mini 2G")]
    [InlineData("ipodnano1g", "Nano 1G")]
    [InlineData("ipodnano2g", "Nano 2G")]
    [InlineData("ipodnano3g", "Nano 3G")]
    [InlineData("ipodnano4g", "Nano 4G")]
    [InlineData("ipodvideo",  "Video 5G")]   // covers both 5G and 5.5G; serial refines later
    [InlineData("ipod6g",     "Classic 6G")] // covers 6G/6.5G/7G; serial refines
    public void MapRockboxTargetToGeneration_known_targets(string target, string expected)
    {
        Assert.Equal(expected, DeviceFingerprint.MapRockboxTargetToGeneration(target));
    }

    [Theory]
    [InlineData("IPODVIDEO", "Video 5G")]   // case-insensitive
    [InlineData("IpodVideo", "Video 5G")]
    public void MapRockboxTargetToGeneration_is_case_insensitive(string target, string expected)
    {
        Assert.Equal(expected, DeviceFingerprint.MapRockboxTargetToGeneration(target));
    }

    [Theory]
    [InlineData("sansafuze")]      // non-iPod Rockbox target
    [InlineData("ipodbogus")]
    [InlineData("")]
    public void MapRockboxTargetToGeneration_unknown_returns_null(string target)
    {
        Assert.Null(DeviceFingerprint.MapRockboxTargetToGeneration(target));
    }

    // ===== CleanupUsbModelString — strip "USB Device" / "USB" / "Device" trailing junk =====

    [Theory]
    [InlineData("Apple iPod USB Device", "Apple iPod")]
    [InlineData("iFlash USB Device",     "iFlash")]
    [InlineData("Generic USB",           "Generic")]
    [InlineData("Some Device",           "Some")]
    [InlineData("Apple iPod",            "Apple iPod")]   // already clean
    [InlineData("",                      "")]
    public void CleanupUsbModelString_strips_known_suffixes(string raw, string expected)
    {
        Assert.Equal(expected, DeviceFingerprint.CleanupUsbModelString(raw));
    }

    [Fact]
    public void CleanupUsbModelString_is_case_insensitive_for_suffix_match()
    {
        Assert.Equal("Apple iPod", DeviceFingerprint.CleanupUsbModelString("Apple iPod usb device"));
    }

    // ===== FormatFirmwareVersion — extracts the parenthesized human form when present =====

    [Theory]
    [InlineData("0x011d1420 (1.3)", "iPod OS 1.3")]
    [InlineData("0x02218000 (2.2.1)", "iPod OS 2.2.1")]
    [InlineData("1.3",              "iPod OS 1.3")]   // no parens — pass-through
    [InlineData("",                 "iPod OS ")]      // edge: empty input still gets prefix
    public void FormatFirmwareVersion_handles_paren_and_plain_forms(string input, string expected)
    {
        Assert.Equal(expected, DeviceFingerprint.FormatFirmwareVersion(input));
    }

    // ===== CleanupUsbSerial — nibble-swapped hex-encoded serial decode =====

    [Fact]
    public void CleanupUsbSerial_decodes_nibble_swapped_hex()
    {
        // "8K7285F4V9P" with each char as nibble-swapped hex
        // '8' (0x38) → "83", 'K' (0x4B) → "B4", etc.
        // Hand-built: take "8K7285F4V9P", encode each char's ASCII byte as nibble-swapped hex
        var raw = "";
        foreach (var c in "8K7285F4V9P")
        {
            var b = (byte)c;
            // nibble-swapped: low nibble first, high nibble second
            raw += $"{b & 0x0F:X}{(b >> 4) & 0x0F:X}";
        }

        var decoded = DeviceFingerprint.CleanupUsbSerial(raw);
        Assert.Equal("8K7285F4V9P", decoded);
    }

    [Fact]
    public void CleanupUsbSerial_returns_input_when_not_hex()
    {
        Assert.Equal("PLAIN-SERIAL", DeviceFingerprint.CleanupUsbSerial("PLAIN-SERIAL"));
    }

    [Fact]
    public void CleanupUsbSerial_returns_input_when_too_short()
    {
        Assert.Equal("ABCD", DeviceFingerprint.CleanupUsbSerial("ABCD"));   // 4 chars — below the 8-char threshold
    }
}
