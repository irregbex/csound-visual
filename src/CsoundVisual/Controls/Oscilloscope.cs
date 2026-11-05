using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CsoundVisual.Theming;

namespace CsoundVisual.Controls;

/// <summary>
/// Oscilloscope of the mono mix of the output. Each frame shows a fixed window of samples that
/// starts at a rising zero crossing, so periodic waves stand still instead of scrolling.
/// </summary>
/// <remarks>
/// <para>
/// <c>MainWindow</c> calls <see cref="Push"/> every animation frame with a copy of the engine's
/// scope ring (<c>CsoundEngine.ScopeSize</c> samples, unrolled oldest → newest), or with the linked
/// DAW plug-in's scope buffer. An empty array clears the trace.
/// </para>
/// <para>
/// Drawn in code: rounded dark background, a centre line and three vertical quarter lines, then the
/// trace twice (a wide translucent "glow" pen under a thin bright one). Samples are clamped to
/// ±1 (0 dBFS) so overs flatten at the edges instead of leaving the panel.
/// </para>
/// </remarks>
public sealed class Oscilloscope : Control
{
    /// <summary>Last buffer received from <see cref="Push"/> (kept by reference, not copied).</summary>
    float[] samples = [];
    /// <summary>Number of samples spread across the full width (at most half the buffer, see <see cref="Render"/>).</summary>
    int visible;

    /// <summary>Panel background.</summary>
    static readonly IBrush Background = AppTheme.Brush("BgDeep");
    /// <summary>Pen of the centre line and the quarter-width divisions.</summary>
    static readonly IPen Grid = new Pen(AppTheme.Brush("Grid"), 1);
    /// <summary>Wide, translucent pen drawn under the trace to give it a soft glow.</summary>
    static readonly IPen Glow = new Pen(AppTheme.Brush("Accent_40"), 5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    /// <summary>Thin bright pen of the waveform itself.</summary>
    static readonly IPen Trace = new Pen(AppTheme.Brush("AccentHi"), 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

    /// <summary>Receives the whole ring buffer in chronological order (newest last) and displays <paramref name="windowSize"/> samples of it.</summary>
    /// <param name="buffer">Mono samples, oldest first. The array is kept by reference until the next call, so it must only be refilled on the UI thread (MainWindow refills its scope buffer right before each push).</param>
    /// <param name="windowSize">Samples to show across the width; limited to half the buffer so the trigger search has the older half to look in.</param>
    public void Push(float[] buffer, int windowSize = 1024)
    {
        samples = buffer;
        visible = Math.Min(windowSize, buffer.Length / 2);
        InvalidateVisual();
    }

    /// <summary>Draws the background grid and, when enough samples are available, the triggered trace.</summary>
    /// <param name="ctx">Avalonia drawing context.</param>
    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        ctx.DrawRectangle(Background, null, b, 8, 8);
        double mid = b.Height / 2;
        ctx.DrawLine(Grid, new Point(0, mid), new Point(b.Width, mid));
        for (int i = 1; i < 4; i++)
            ctx.DrawLine(Grid, new Point(b.Width * i / 4, 6), new Point(b.Width * i / 4, b.Height - 6));

        if (visible < 2 || samples.Length < visible * 2)
            return;

        // Look for a rising zero crossing in the older half so the trace does not jitter between frames.
        // The search window is the last 2·visible samples minus the final `visible`, so a full window
        // of samples always follows the trigger. With no crossing (silence, DC) the window simply
        // starts at `start` and the trace scrolls freely.
        int start = samples.Length - visible * 2;
        int trigger = start;
        for (int i = start + 1; i < samples.Length - visible; i++)
        {
            if (samples[i - 1] < 0 && samples[i] >= 0) { trigger = i; break; }
        }

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            // Pixels per unit of amplitude: ±1.0 reaches 6 px from the top/bottom edge.
            double amp = (b.Height / 2 - 6);
            for (int i = 0; i < visible; i++)
            {
                double x = b.Width * i / (visible - 1);
                double y = mid - Math.Clamp(samples[trigger + i], -1f, 1f) * amp;
                if (i == 0) g.BeginFigure(new Point(x, y), false);
                else g.LineTo(new Point(x, y));
            }
            g.EndFigure(false);
        }
        using (ctx.PushClip(new RoundedRect(b, 8)))
        {
            ctx.DrawGeometry(null, Glow, geo);
            ctx.DrawGeometry(null, Trace, geo);
        }
    }
}
