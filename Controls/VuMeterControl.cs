// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace OrgZ.Controls;

/// <summary>
/// iTunes-style stereo segmented VU meter rendered in a single Render() pass.
///
/// The previous implementation created 512 <see cref="Avalonia.Controls.Shapes.Rectangle"/>
/// children (16 cols × 16 rows × 2 channels) and mutated their Fill brushes per frame.
/// Per-shape property invalidation produced visible stutter on bass-heavy passages
/// because every changed bar pushed its own re-render through Avalonia's compositor.
/// This control holds the level state directly and paints all bars + peak marks in
/// one <see cref="DrawingContext"/> pass driven by <see cref="InvalidateVisual"/> -
/// one render invalidation per frame regardless of how many bars are moving.
/// </summary>
public sealed class VuMeterControl : Control
{
    private const int ColumnsPerChannel = 16;
    private const int RowsPerColumn = 16;
    private const double ChannelGap = 16;
    private const double TickGap = 1;

    private static readonly IBrush OnBrush = new ImmutableSolidColorBrush(Color.Parse("#3A4A30"));
    private static readonly IBrush OffBrush = new ImmutableSolidColorBrush(Color.FromArgb(80, 0x3A, 0x4A, 0x30));
    private static readonly IBrush PeakBrush = new ImmutableSolidColorBrush(Color.Parse("#1E2014"));

    private readonly float[] _smoothedLeft = new float[ColumnsPerChannel];
    private readonly float[] _smoothedRight = new float[ColumnsPerChannel];
    private readonly float[] _peakLeft = new float[ColumnsPerChannel];
    private readonly float[] _peakRight = new float[ColumnsPerChannel];
    private readonly float[] _peakVelLeft = new float[ColumnsPerChannel];
    private readonly float[] _peakVelRight = new float[ColumnsPerChannel];

    // Bar falls at a constant linear rate. The "tight" feel comes from pairing
    // this with an INSTANT attack (no smoothing on the way up) -- new peaks
    // appear next frame and the only delay is the linear ramp down.
    private const float BarDecayPerSec = 1.5f;

    // Peak gravity: starts slow, accelerates. Velocity grows continuously so
    // the peak appears to float, then drop. Constant-rate peak fall (what we
    // had before) looks detached because every peak takes the same long path
    // down regardless of when it was set.
    private const float PeakInitialVel = 0.012f;
    private const float PeakAccelPerSec = 5.72f;

    /// <summary>
    /// Fill the available space (clamped by Min/Max constraints applied by the
    /// layout system on top). Without an explicit DesiredSize, an alignment of
    /// Center / Right / Left in the parent collapses the meter to 0×0 because
    /// the base Layoutable returns Size.Zero from MeasureOverride.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 360 : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? 60 : availableSize.Height;
        return new Size(w, h);
    }

    /// <summary>
    /// Pushes a new audio frame into the meter. <paramref name="dt"/> is the
    /// elapsed time since the previous Update call, in seconds (clamped by the
    /// caller to ~100 ms so a one-off hitch doesn't slam everything to zero).
    /// Triggers a single <see cref="InvalidateVisual"/> - the actual draw
    /// happens on the next compositor tick. Falls and peaks are modeled in
    /// continuous time so the look stays identical across 30 / 60 / 120 Hz.
    /// </summary>
    public void Update(ReadOnlySpan<float> left, ReadOnlySpan<float> right, float dt)
    {
        FoldChannel(left, _smoothedLeft, _peakLeft, _peakVelLeft, dt);
        FoldChannel(right, _smoothedRight, _peakRight, _peakVelRight, dt);
        InvalidateVisual();
    }

    /// <summary>
    /// Resets all bars to silent and forces a clean repaint. Call when the VU
    /// page is hidden so the meter doesn't keep stale state while invisible.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_smoothedLeft);
        Array.Clear(_smoothedRight);
        Array.Clear(_peakLeft);
        Array.Clear(_peakRight);
        Array.Clear(_peakVelLeft);
        Array.Clear(_peakVelRight);
        InvalidateVisual();
    }

    private static void FoldChannel(ReadOnlySpan<float> source, float[] smoothed, float[] peaks, float[] peakVel, float dt)
    {
        var srcLen = source.Length;
        if (srcLen == 0)
        {
            return;
        }

        float barFall = BarDecayPerSec * dt;
        // Continuous-time gravity: vel *= exp(accel * dt). Pre-computed so the
        // inner loop is a single multiply per column.
        float peakGravity = MathF.Exp(PeakAccelPerSec * dt);

        for (int c = 0; c < ColumnsPerChannel; c++)
        {
            int start = c * srcLen / ColumnsPerChannel;
            int end = (c + 1) * srcLen / ColumnsPerChannel;
            if (end <= start) end = start + 1;

            float sum = 0;
            for (int i = start; i < end; i++)
            {
                sum += source[i];
            }
            float target = Math.Clamp(sum / (end - start), 0f, 1f);

            // Bar: instant snap-up, linear fall.
            if (target > smoothed[c])
            {
                smoothed[c] = target;
            }
            else
            {
                smoothed[c] = Math.Max(target, smoothed[c] - barFall);
            }

            // Peak: gravity model. New bar at-or-above peak resets position +
            // velocity. Otherwise peak falls by velocity*dt and the velocity
            // itself grows geometrically - slow float, then a drop.
            if (smoothed[c] >= peaks[c])
            {
                peaks[c] = smoothed[c];
                peakVel[c] = PeakInitialVel;
            }
            else
            {
                peaks[c] = Math.Max(smoothed[c], peaks[c] - peakVel[c] * dt);
                peakVel[c] *= peakGravity;
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        // All math in physical pixels so positions land on device-pixel boundaries.
        // Avalonia renders fractional DIP positions with AA that varies per column -
        // that's where the drifting row-spacing came from in the shape-based version.
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

        int canvasW_phys = (int)Math.Floor(bounds.Width * scale);
        int canvasH_phys = (int)Math.Floor(bounds.Height * scale);
        int channelGap_phys = (int)Math.Round(ChannelGap * scale);
        int tickGap_phys = Math.Max(1, (int)Math.Round(TickGap * scale));

        int availableW_phys = (canvasW_phys - channelGap_phys) / 2;
        int tickW_phys = Math.Max(2, (availableW_phys - (ColumnsPerChannel - 1) * tickGap_phys) / ColumnsPerChannel);
        int tickH_phys = Math.Max(2, (canvasH_phys - (RowsPerColumn - 1) * tickGap_phys) / RowsPerColumn);

        int channelW_phys = ColumnsPerChannel * tickW_phys + (ColumnsPerChannel - 1) * tickGap_phys;
        int totalW_phys = channelW_phys * 2 + channelGap_phys;
        int startX_phys = Math.Max(0, (canvasW_phys - totalW_phys) / 2);

        int totalH_phys = RowsPerColumn * tickH_phys + (RowsPerColumn - 1) * tickGap_phys;
        int bottomPad_phys = Math.Max(0, (canvasH_phys - totalH_phys) / 2);

        DrawChannel(context, _smoothedLeft, _peakLeft, startX_phys, tickW_phys, tickH_phys, tickGap_phys, bottomPad_phys, canvasH_phys, scale, mirror: false);
        DrawChannel(context, _smoothedRight, _peakRight, startX_phys + channelW_phys + channelGap_phys, tickW_phys, tickH_phys, tickGap_phys, bottomPad_phys, canvasH_phys, scale, mirror: true);
    }

    private static void DrawChannel(DrawingContext context, float[] smoothed, float[] peaks, int originX_phys, int tickW_phys, int tickH_phys, int tickGap_phys, int bottomPad_phys, int canvasH_phys, double scale, bool mirror)
    {
        double tickW = tickW_phys / scale;
        double tickH = tickH_phys / scale;

        for (int c = 0; c < ColumnsPerChannel; c++)
        {
            int visualIndex = mirror ? (ColumnsPerChannel - 1 - c) : c;
            int colX_phys = originX_phys + visualIndex * (tickW_phys + tickGap_phys);
            double colX = colX_phys / scale;

            int lit = (int)Math.Round(smoothed[c] * RowsPerColumn);

            for (int r = 0; r < RowsPerColumn; r++)
            {
                int rowY_phys = canvasH_phys - bottomPad_phys - tickH_phys - r * (tickH_phys + tickGap_phys);
                double rowY = rowY_phys / scale;
                var brush = r < lit ? OnBrush : OffBrush;
                context.FillRectangle(brush, new Rect(colX, rowY, tickW, tickH));
            }

            int peakRow = Math.Min(RowsPerColumn - 1, (int)Math.Round(peaks[c] * RowsPerColumn));
            if (peakRow > lit && peaks[c] > 0.02f)
            {
                int peakY_phys = canvasH_phys - bottomPad_phys - tickH_phys - peakRow * (tickH_phys + tickGap_phys);
                double peakY = peakY_phys / scale;
                context.FillRectangle(PeakBrush, new Rect(colX, peakY, tickW, tickH));
            }
        }
    }
}
