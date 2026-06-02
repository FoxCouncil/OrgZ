// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace OrgZ.Helpers;

/// <summary>
/// Drives a synchronised two-line marquee with edge-fade on both the main
/// window's LCD and the mini-player. Both surfaces want identical behaviour
/// (5-second dwell, 40 px/s scroll, 10 px edge fade, center-align when the
/// text fits) so the math, animation, and OpacityMask logic lives here.
/// </summary>
internal static class MarqueeHelper
{
    /// <summary>Cancels the previous run and starts a fresh marquee cycle.</summary>
    /// <returns>The new <see cref="CancellationTokenSource"/> - caller stores it
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
            // desired width. No transform/alignment reset here - that's the source
            // of the visible left→center jump during resize.
            if (tb1 != null) tb1.Width = double.NaN;
            if (tb2 != null) tb2.Width = double.NaN;

            var containerWidth1 = container1?.Bounds.Width > 0 ? container1.Bounds.Width : 420;
            var containerWidth2 = container2?.Bounds.Width > 0 ? container2.Bounds.Width : 420;

            var overflow1 = MeasureOverflow(tb1, containerWidth1);
            var overflow2 = MeasureOverflow(tb2, containerWidth2);

            ApplyMarqueeState(tb1, container1, overflow1, containerWidth1);
            ApplyMarqueeState(tb2, container2, overflow2, containerWidth2);

            if (overflow1 <= 0 && overflow2 <= 0)
            {
                return;
            }

            // Both lines scroll at 40 px/s for their own distance; whichever is
            // shorter waits at each end for the longer to finish.
            // Cycle: 5s dwell → scroll forward → 5s dwell → scroll back.
            var maxOverflow = Math.Max(overflow1, overflow2);
            var maxScrollSec = maxOverflow / 40.0;
            var dwellSec = 5.0;
            var totalSec = 2 * dwellSec + 2 * maxScrollSec;

            if (overflow1 > 0 && tb1 != null)
            {
                RunSyncedMarquee(tb1, overflow1, totalSec, dwellSec, maxScrollSec, cts);
            }

            if (overflow2 > 0 && tb2 != null)
            {
                RunSyncedMarquee(tb2, overflow2, totalSec, dwellSec, maxScrollSec, cts);
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

    private static void ApplyMarqueeState(TextBlock? tb, Border? container, double overflow, double containerWidth)
    {
        if (tb == null)
        {
            return;
        }

        if (overflow <= 0)
        {
            // Text fits: drop scroll animation transform and center natively.
            // HorizontalAlignment is more stable than translating because it
            // survives resize without per-frame reassignment.
            tb.HorizontalAlignment = HorizontalAlignment.Center;
            tb.Width = double.NaN;
            tb.RenderTransform = null;
            if (container != null) container.OpacityMask = null;
        }
        else
        {
            // Text overflows: pin left, give the TextBlock an explicit width so
            // its translate transform sees a stable bounding box, and attach the
            // edge fade - capped at 10 px so wide containers don't grow it.
            tb.HorizontalAlignment = HorizontalAlignment.Left;
            tb.Width = tb.DesiredSize.Width;
            tb.RenderTransform = new TranslateTransform();
            if (container != null) container.OpacityMask = BuildEdgeFadeMask(containerWidth);
        }
    }

    private static IBrush BuildEdgeFadeMask(double containerWidth)
    {
        // Cap the fade at 10 px or 25 % of the container, whichever is smaller -
        // keeps the fade from swallowing the text on very narrow containers.
        var fadeWidth = Math.Min(10.0, containerWidth * 0.25);
        var fadePct = fadeWidth / Math.Max(1.0, containerWidth);

        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        };
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        brush.GradientStops.Add(new GradientStop(Colors.Black, fadePct));
        brush.GradientStops.Add(new GradientStop(Colors.Black, 1 - fadePct));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        return brush;
    }

    private static void RunSyncedMarquee(TextBlock tb, double overflow, double totalSec, double dwellSec, double maxScrollSec, CancellationTokenSource cts)
    {
        // This line scrolls at 40 px/s for its own distance, then waits for the
        // longer line. Cycle: dwell | scroll fwd | wait | dwell | scroll back | wait.
        var lineScrollSec = overflow / 40.0;

        double CueFrac(double seconds) => Math.Min(seconds / totalSec, 1.0);

        var animation = new Animation
        {
            Duration = TimeSpan.FromSeconds(totalSec),
            IterationCount = new IterationCount(ulong.MaxValue),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.XProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(CueFrac(dwellSec)), Setters = { new Setter(TranslateTransform.XProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(CueFrac(dwellSec + lineScrollSec)), Setters = { new Setter(TranslateTransform.XProperty, -overflow) } },
                new KeyFrame { Cue = new Cue(CueFrac(dwellSec + maxScrollSec + dwellSec)), Setters = { new Setter(TranslateTransform.XProperty, -overflow) } },
                new KeyFrame { Cue = new Cue(CueFrac(dwellSec + maxScrollSec + dwellSec + lineScrollSec)), Setters = { new Setter(TranslateTransform.XProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(1.0), Setters = { new Setter(TranslateTransform.XProperty, 0.0) } },
            }
        };

        animation.RunAsync(tb, cts.Token);
    }
}
