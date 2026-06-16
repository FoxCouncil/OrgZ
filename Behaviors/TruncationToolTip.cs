// Copyright (c) 2025 Fox Diller

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace OrgZ.Behaviors;

/// <summary>
/// Attached behavior that shows a TextBlock's full text as a tooltip <b>only when the text is
/// actually trimmed</b> (e.g. <c>TextTrimming=CharacterEllipsis</c> and it doesn't fit). When the
/// text fits, no tooltip is shown. Applied app-wide via a global <c>TextBlock</c> style; the LCD
/// opts out (it has its own scrolling display). No-ops on TextBlocks that don't trim.
/// </summary>
public sealed class TruncationToolTip
{
    private TruncationToolTip() { }

    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<TruncationToolTip, TextBlock, bool>("Enabled");

    public static bool GetEnabled(TextBlock o) => o.GetValue(EnabledProperty);
    public static void SetEnabled(TextBlock o, bool value) => o.SetValue(EnabledProperty, value);

    static TruncationToolTip()
    {
        EnabledProperty.Changed.AddClassHandler<TextBlock>(OnEnabledChanged);
    }

    private static void OnEnabledChanged(TextBlock tb, AvaloniaPropertyChangedEventArgs e)
    {
        // Re-subscribe cleanly so toggling the property doesn't stack handlers.
        tb.PropertyChanged -= OnTextBlockPropertyChanged;
        if (e.GetNewValue<bool>())
        {
            tb.PropertyChanged += OnTextBlockPropertyChanged;
            Update(tb);
        }
        else
        {
            ToolTip.SetTip(tb, null);
        }
    }

    private static void OnTextBlockPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // Width, content, trimming, or font metrics all change whether the text fits.
        if (sender is TextBlock tb &&
            (e.Property == Visual.BoundsProperty ||
             e.Property == TextBlock.TextProperty ||
             e.Property == TextBlock.TextTrimmingProperty ||
             e.Property == TextBlock.FontSizeProperty ||
             e.Property == TextBlock.FontFamilyProperty ||
             e.Property == TextBlock.FontStretchProperty ||
             e.Property == TextBlock.FontStyleProperty ||
             e.Property == TextBlock.FontWeightProperty))
        {
            Update(tb);
        }
    }

    private static void Update(TextBlock tb)
    {
        var text = tb.Text;
        if (tb.TextTrimming == TextTrimming.None || string.IsNullOrEmpty(text))
        {
            ToolTip.SetTip(tb, null);
            return;
        }

        var available = tb.Bounds.Width - tb.Padding.Left - tb.Padding.Right;
        if (available <= 0)
        {
            return; // not laid out yet — a later Bounds change re-runs this
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
            tb.FontSize,
            null);

        // Half-pixel slack absorbs rounding so non-trimmed text doesn't get a spurious tooltip.
        var trimmed = formatted.Width > available + 0.5;
        ToolTip.SetTip(tb, trimmed ? text : null);
    }
}
