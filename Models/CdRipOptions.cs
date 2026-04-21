// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Models;

public enum RipFormat
{
    Wav = 0,
    Flac = 1,
    Mp3 = 2,
}

public enum Mp3Mode
{
    /// <summary>Variable bit rate - LAME <c>-V 0..9</c>, 0 is highest.</summary>
    Vbr,

    /// <summary>Constant bit rate - LAME <c>-b &lt;kbps&gt;</c>.</summary>
    Cbr,
}

/// <summary>
/// Codec knobs chosen by the user in the Rip Options dialog.  Persisted in
/// <see cref="Settings"/> as <c>OrgZ.Cd.LastCdRipOptions</c> JSON so the next
/// session defaults to the user's last choice.
/// </summary>
/// <remarks>
/// <para>
/// Defaults match the LAME / FLAC community consensus for "sensible":
/// FLAC compression level 5 (the default), MP3 VBR -V2 (≈190 kbps, the
/// "standard" preset and the best practical quality/size tradeoff).
/// </para>
/// </remarks>
public sealed record CdRipOptions
{
    public RipFormat Format { get; init; } = RipFormat.Flac;

    /// <summary>FLAC compression level, 0 (fast) through 8 (best).</summary>
    public int FlacCompression { get; init; } = 5;

    public Mp3Mode Mp3Mode { get; init; } = Mp3Mode.Vbr;

    /// <summary>
    /// VBR quality (0 = best, 9 = worst) when <see cref="Mp3Mode"/> is
    /// <see cref="OrgZ.Models.Mp3Mode.Vbr"/>; kbps bitrate (64-320) when
    /// <see cref="OrgZ.Models.Mp3Mode.Cbr"/>.
    /// </summary>
    public int Mp3Quality { get; init; } = 2;

    public static CdRipOptions Default => new();

    public static readonly int[] CbrBitrates = [64, 96, 128, 160, 192, 224, 256, 320];

    /// <summary>
    /// Short label for UI and activity-panel display, e.g. "FLAC (best)" /
    /// "MP3 VBR V2 (~190 kbps)".
    /// </summary>
    public string ShortLabel => Format switch
    {
        RipFormat.Wav => "WAV",
        RipFormat.Flac => FlacCompression switch
        {
            0 => "FLAC (fast)",
            8 => "FLAC (best)",
            _ => $"FLAC (level {FlacCompression})",
        },
        RipFormat.Mp3 => Mp3Mode switch
        {
            Mp3Mode.Vbr => $"MP3 VBR V{Mp3Quality}",
            Mp3Mode.Cbr => $"MP3 CBR {Mp3Quality} kbps",
            _ => "MP3",
        },
        _ => Format.ToString(),
    };
}
