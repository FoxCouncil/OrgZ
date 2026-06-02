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

    /// <summary>
    /// Pushes a new audio frame into the meter. <paramref name="decay"/> and
    /// <paramref name="peakDecay"/> are per-frame deltas (rate × dtSec) so the
    /// fall-off speed is identical across 30 / 60 / 120 Hz displays. Triggers a
    /// single <see cref="InvalidateVisual"/> - the actual draw happens on the
    /// next compositor tick.
    /// </summary>
    public void Update(ReadOnlySpan<float> left, ReadOnlySpan<float> right, float decay, float peakDecay)
    {
        FoldChannel(left, _smoothedLeft, _peakLeft, decay, peakDecay);
        FoldChannel(right, _smoothedRight, _peakRight, decay, peakDecay);
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
        InvalidateVisual();
    }

    private static void FoldChannel(ReadOnlySpan<float> source, float[] smoothed, float[] peaks, float decay, float peakDecay)
    {
        var srcLen = source.Length;
        if (srcLen == 0)
        {
            return;
        }

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

            // Fast attack, slow linear decay toward the new target.
            if (target > smoothed[c])
            {
                smoothed[c] = target;
            }
            else
            {
                smoothed[c] = Math.Max(target, smoothed[c] - decay);
            }

            if (smoothed[c] > peaks[c])
            {
                peaks[c] = smoothed[c];
            }
            else
            {
                peaks[c] = Math.Max(smoothed[c], peaks[c] - peakDecay);
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
