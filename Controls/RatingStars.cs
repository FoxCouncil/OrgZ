// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Optris.Icons.Avalonia;

namespace OrgZ.Controls;

/// <summary>
/// The five-star rating cell, drawn rather than assembled.
///
/// The template this replaces built a StackPanel, five Panels and ten Icon controls per row -
/// sixteen controls and five converter-backed bindings - and a DataGrid rebuilds a cell's
/// template every time its row container recycles, which while scrolling is continuous. The
/// stars themselves were never the cost; the tree around them was.
///
/// Here one control fills two shared geometries straight into the drawing context. Same
/// vectors, same positions, no layout children, no per-slot bindings, and still resolution
/// independent - which matters most on the HiDPI panels where the scrolling was worst.
/// </summary>
public sealed class RatingStars : Control
{
    /// <summary>FontAwesome's viewBox. Both star glyphs are authored in this space.</summary>
    private const double GlyphWidth = 576;
    private const double GlyphHeight = 512;

    /// <summary>Slot metrics, matching the Panel/Spacing the assembled template used.</summary>
    private const double SlotWidth = 15;
    private const double SlotHeight = 14;
    private const double SlotSpacing = 1;
    /// <summary>
    /// Not the Icon control's nominal FontSize of 12: that renders the glyph at its font
    /// metrics, not as a Uniform stretch of the viewBox, so the ink came out a pixel taller.
    /// This is the size that reproduces the ink box the assembled stars actually drew,
    /// measured off a render rather than derived.
    /// </summary>
    private const double IconSize = 11.0;

    /// <summary>The unrated star's opacity - the "no rating here" look.</summary>
    private const double OutlineOpacity = 0.25;

    private static Geometry? _outline;
    private static Geometry? _filled;

    public static readonly StyledProperty<int?> RatingProperty =
        AvaloniaProperty.Register<RatingStars, int?>(nameof(Rating));

    /// <summary>Painted with the row's foreground, so a selected row inverts like its text.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<RatingStars>();

    static RatingStars()
    {
        AffectsRender<RatingStars>(RatingProperty, ForegroundProperty);
    }

    public int? Rating
    {
        get => GetValue(RatingProperty);
        set => SetValue(RatingProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
        => new((SlotWidth * 5) + (SlotSpacing * 4), SlotHeight);

    public override void Render(DrawingContext context)
    {
        var brush = Foreground ?? Brushes.White;
        var outline = OutlineGeometry();
        var filled = FilledGeometry();

        if (outline is null || filled is null)
        {
            return;
        }

        // The glyph is authored 576x512; scale it Uniform into the icon box the way the Icon
        // control did, then centre that box in its slot.
        var scale = Math.Min(IconSize / GlyphWidth, IconSize / GlyphHeight);
        var drawnWidth = GlyphWidth * scale;
        var drawnHeight = GlyphHeight * scale;
        var insetX = (SlotWidth - drawnWidth) / 2;
        // Half a pixel low: the assembled Icons sat on the odd-pixel boundary of their Panel,
        // and centring the geometry exactly puts the ink one row higher than they drew it.
        var insetY = ((SlotHeight - drawnHeight) / 2) + 0.5;

        var rating = Rating ?? 0;

        for (var slot = 0; slot < 5; slot++)
        {
            var x = (slot * (SlotWidth + SlotSpacing)) + insetX;

            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(x, insetY)))
            {
                // The faint outline is always under, so a partly-rated row shows the empty slots.
                using (context.PushOpacity(OutlineOpacity))
                {
                    context.DrawGeometry(brush, null, outline);
                }

                if (slot < rating)
                {
                    context.DrawGeometry(brush, null, filled);
                }
            }
        }
    }

    /// <summary>
    /// Resolved once for the process. IconProvider parses the glyph out of the FontAwesome
    /// metadata, which is not work to repeat per row, per cell, or per frame.
    /// </summary>
    private static Geometry? OutlineGeometry() => _outline ??= GeometryFor("fa-regular fa-star");

    private static Geometry? FilledGeometry() => _filled ??= GeometryFor("fa-solid fa-star");

    private static Geometry? GeometryFor(string value)
    {
        try
        {
            return new IconImage { Value = value }.Drawing is GeometryDrawing drawing ? drawing.Geometry : null;
        }
        catch (Exception)
        {
            // No icon provider registered (unit tests, a trimmed host): draw nothing rather
            // than take the grid down over decoration.
            return null;
        }
    }
}
