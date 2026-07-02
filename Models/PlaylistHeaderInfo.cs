// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Media.Imaging;

namespace OrgZ.Models;

/// <summary>
/// Backing data for the playlist/Favorites header bar: the name, where the playlist came
/// from, a one-line stats summary, and up to four album covers for the 2×2 mosaic icon.
/// Built by the view model on view-switch; consumed by the <c>PlaylistHeader</c> control.
/// </summary>
public sealed class PlaylistHeaderInfo
{
    public required string Name { get; init; }

    /// <summary>Where it came from - "Library", "Favorites", "M3U8", "PLS", ...</summary>
    public required string SourceLabel { get; init; }

    /// <summary>Preformatted "12 songs · 48:21 · 312 MB".</summary>
    public required string Summary { get; init; }

    // Up to four covers for the mosaic. Null tiles fall back to the generic glyph.
    public Bitmap? Cover1 { get; init; }
    public Bitmap? Cover2 { get; init; }
    public Bitmap? Cover3 { get; init; }
    public Bitmap? Cover4 { get; init; }

    /// <summary>True when at least one cover was found (drives the mosaic vs. fallback glyph).</summary>
    public bool HasAnyCover => Cover1 is not null || Cover2 is not null || Cover3 is not null || Cover4 is not null;

    /// <summary>
    /// Exactly one cover - shown as a single full-size square, not a 2×2 of four shrunken copies.
    /// (2-3 distinct covers are cycle-filled to four by the VM, so Cover2 is set in those cases.)
    /// </summary>
    public bool IsSingleCover => Cover1 is not null && Cover2 is null;

    /// <summary>The 2×2 mosaic shows only when there are multiple covers to arrange.</summary>
    public bool HasMosaic => HasAnyCover && !IsSingleCover;

    /// <summary>
    /// False when <see cref="SourceLabel"/> only repeats <see cref="Name"/> (e.g. the Favorites
    /// view, where both are "Favorites"); lets the header drop the redundant sub-bar label.
    /// </summary>
    public bool ShowSourceLabel => !string.Equals(SourceLabel, Name, System.StringComparison.OrdinalIgnoreCase);
}
