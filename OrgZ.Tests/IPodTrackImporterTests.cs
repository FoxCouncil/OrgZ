// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

/// <summary>
/// The pure decision rules for importing a track onto a stock iPod: which source formats
/// play natively (copied as-is) vs. need transcoding to ALAC, the produced container, and
/// the 48 kHz ceiling the hardware + iTunesDB sample-rate field enforce.
/// </summary>
public class IPodTrackImporterTests
{
    [Theory]
    [InlineData(".mp3")]
    [InlineData(".m4a")]
    [InlineData(".m4b")]
    [InlineData(".aac")]
    [InlineData(".aif")]
    [InlineData(".aiff")]
    [InlineData(".wav")]
    [InlineData(".MP3")]   // case-insensitive
    [InlineData(".Wav")]
    public void IsNativelyCompatible_true_for_stock_playable_formats(string ext)
        => Assert.True(IPodTrackImporter.IsNativelyCompatible(ext));

    [Theory]
    [InlineData(".flac")]
    [InlineData(".ogg")]
    [InlineData(".opus")]
    [InlineData(".wma")]
    [InlineData(".alac")]
    [InlineData("")]
    public void IsNativelyCompatible_false_for_formats_needing_transcode(string ext)
        => Assert.False(IPodTrackImporter.IsNativelyCompatible(ext));

    [Theory]
    [InlineData(".mp3", ".mp3")]
    [InlineData(".M4A", ".m4a")]    // native -> lower-cased source extension
    [InlineData(".Wav", ".wav")]
    [InlineData(".flac", ".m4a")]   // transcoded -> ALAC in .m4a
    [InlineData(".ogg", ".m4a")]
    public void TargetExtension_keeps_native_else_m4a(string source, string expected)
        => Assert.Equal(expected, IPodTrackImporter.TargetExtension(source));

    [Theory]
    [InlineData(44100, 44100)]
    [InlineData(48000, 48000)]
    [InlineData(32000, 32000)]
    [InlineData(96000, 44100)]   // hi-res resampled down to CD
    [InlineData(88200, 44100)]
    [InlineData(0, 44100)]       // unknown -> default
    [InlineData(-1, 44100)]
    public void TargetSampleRate_caps_at_48k(int source, int expected)
        => Assert.Equal(expected, IPodTrackImporter.TargetSampleRate(source));
}
