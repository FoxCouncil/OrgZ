// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace OrgZ.Helpers;

/// <summary>
/// Drives a synchronised two-line marquee on both the main window's LCD and the
/// mini-player. Both surfaces want identical behaviour (5-second dwell, 40 px/s scroll,
/// center-align when the text fits) so the math, animation, and edge-fade live here.
///
/// The edge fade is a <i>motion</i> cue: it's applied only while a line is actually
/// scrolling and removed at the dwells, so a stopped line never has its first / last
/// letter sitting under the fade.
/// </summary>
internal static class MarqueeHelper
{
    private const double ScrollPxPerSec = 40.0;
    private const double DwellSec = 5.0;

    /// <summary>Cancels the previous run and starts a fresh marquee cycle.</summary>
    /// <returns>The new <see cref="CancellationTokenSource"/> — caller stores it
    /// to pass back on the next <c>Restart</c> so the previous animation gets
    /// halted before this one begins.</returns>
    public static CancellationTokenSource Restart(
        TextBlock? tb1, TextBlock? tb2,
        Border? container1, Border? container2,
        CancellationTokenSource? previous)
    {
        previous?.Cancel();
        var cts = new CancellationTokenSource();

        Dispatcher.UIThread.Post(() =>
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            // Clear any prior explicit width so the measure picks up the natural
            // desired width. No transform/alignment reset here — that's the source
            // of the visible left→center jump during resize.
            if (tb1 != null) tb1.Width = double.NaN;
            if (tb2 != null) tb2.Width = double.NaN;

            var containerWidth1 = container1?.Bounds.Width > 0 ? container1.Bounds.Width : 420;
            var containerWidth2 = container2?.Bounds.Width > 0 ? container2.Bounds.Width : 420;

            var overflow1 = MeasureOverflow(tb1, containerWidth1);
            var overflow2 = MeasureOverflow(tb2, containerWidth2);

            ApplyMarqueeState(tb1, container1, overflow1);
            ApplyMarqueeState(tb2, container2, overflow2);

            if (overflow1 <= 0 && overflow2 <= 0)
            {
                return;
            }

            // Both lines scroll at 40 px/s for their own distance; whichever is
            // shorter finishes early and waits (stopped) for the longer to finish.
            // Cycle: dwell → scroll forward → dwell → scroll back, all DwellSec/maxScroll long
            // so the two lines stay phase-locked.
            var maxOverflow = Math.Max(overflow1, overflow2);
            var maxScrollSec = maxOverflow / ScrollPxPerSec;

            if (overflow1 > 0 && tb1 != null)
            {
                _ = RunMarquee(tb1, container1, containerWidth1, overflow1, maxScrollSec, cts.Token);
            }

            if (overflow2 > 0 && tb2 != null)
            {
                _ = RunMarquee(tb2, container2, containerWidth2, overflow2, maxScrollSec, cts.Token);
            }
        }, DispatcherPriority.Render);

        return cts;
    }

    private static double MeasureOverflow(TextBlock? tb, double containerWidth)
    {
        if (tb == null || string.IsNullOrEmpty(tb.Text))
        {
            return 0;
        }

        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Max(0, tb.DesiredSize.Width - containerWidth);
    }

    private static void ApplyMarqueeState(TextBlock? tb, Border? container, double overflow)
    {
        if (tb == null)
        {
            return;
        }

        // Mask starts off either way — the running marquee turns it on only while scrolling.
        if (container != null) container.OpacityMask = null;

        if (overflow <= 0)
        {
            // Text fits: drop scroll animation transform and center natively.
            // HorizontalAlignment is more stable than translating because it
            // survives resize without per-frame reassignment.
            tb.HorizontalAlignment = HorizontalAlignment.Center;
            tb.Width = double.NaN;
            tb.RenderTransform = null;
        }
        else
        {
            // Text overflows: pin left and give the TextBlock an explicit width so its
            // translate transform sees a stable bounding box.
            tb.HorizontalAlignment = HorizontalAlignment.Left;
            tb.Width = tb.DesiredSize.Width;
            tb.RenderTransform = new TranslateTransform();
        }
    }

    // Builds the edge-fade mask, fading only the requested side(s). A side that isn't faded
    // stays fully opaque to its very edge, so the letter resting there shows intact.
    private static IBrush BuildEdgeFadeMask(double containerWidth, bool fadeLeft, bool fadeRight)
    {
        // Cap the fade at 10 px or 25 % of the container, whichever is smaller —
        // keeps the fade from swallowing the text on very narrow containers.
        var fadeWidth = Math.Min(10.0, containerWidth * 0.25);
        var fadePct = fadeWidth / Math.Max(1.0, containerWidth);

        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        };
        brush.GradientStops.Add(new GradientStop(fadeLeft ? Colors.Transparent : Colors.Black, 0));
        brush.GradientStops.Add(new GradientStop(Colors.Black, fadePct));
        brush.GradientStops.Add(new GradientStop(Colors.Black, 1 - fadePct));
        brush.GradientStops.Add(new GradientStop(fadeRight ? Colors.Transparent : Colors.Black, 1));
        return brush;
    }

    /// <summary>
    /// One line's cycle: dwell at home (stopped, no fade) → scroll forward (fade on) → wait at the
    /// far end (stopped, no fade) → dwell → scroll back (fade on) → wait. The fade brush is applied
    /// to the container only across the two moving segments. <paramref name="maxScrollSec"/> is the
    /// longer line's scroll time; a shorter line idles the difference so both lines stay in phase.
    /// </summary>
    private static async Task RunMarquee(TextBlock tb, Border? container, double containerWidth, double overflow, double maxScrollSec, CancellationToken ct)
    {
        var lineScrollSec = overflow / ScrollPxPerSec;
        var trailingIdleSec = Math.Max(0, maxScrollSec - lineScrollSec);

        // The fade marks the side with off-screen content: at home only the right fades (the left
        // edge holds the first letter), at the far end only the left fades (the right edge holds
        // the last letter), and both fade while the text is actually moving.
        var fadeBoth      = BuildEdgeFadeMask(containerWidth, fadeLeft: true,  fadeRight: true);
        var fadeRightOnly = BuildEdgeFadeMask(containerWidth, fadeLeft: false, fadeRight: true);
        var fadeLeftOnly  = BuildEdgeFadeMask(containerWidth, fadeLeft: true,  fadeRight: false);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                SetMask(container, fadeRightOnly);
                await Task.Delay(TimeSpan.FromSeconds(DwellSec), ct);              // home dwell — content off the right

                SetMask(container, fadeBoth);
                await AnimateX(tb, 0, -overflow, lineScrollSec, ct);              // scroll forward

                SetMask(container, fadeLeftOnly);
                await Task.Delay(TimeSpan.FromSeconds(trailingIdleSec), ct);      // wait for the longer line
                await Task.Delay(TimeSpan.FromSeconds(DwellSec), ct);             // far-end dwell — content off the left

                SetMask(container, fadeBoth);
                await AnimateX(tb, -overflow, 0, lineScrollSec, ct);             // scroll back

                SetMask(container, fadeRightOnly);
                await Task.Delay(TimeSpan.FromSeconds(trailingIdleSec), ct);      // wait at home, then loop
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer marquee; the new run owns the transform/mask now.
        }
    }

    private static void SetMask(Border? container, IBrush? mask)
    {
        if (container != null) container.OpacityMask = mask;
    }

    private static Task AnimateX(TextBlock tb, double from, double to, double seconds, CancellationToken ct)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromSeconds(Math.Max(0.01, seconds)),
            Easing = new LinearEasing(),
            FillMode = FillMode.Forward,   // hold the end position through the following dwell
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.XProperty, from) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(TranslateTransform.XProperty, to) } },
            }
        };
        return animation.RunAsync(tb, ct);
    }
}
