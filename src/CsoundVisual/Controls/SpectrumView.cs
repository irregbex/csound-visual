using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CsoundVisual.Theming;

namespace CsoundVisual.Controls;

/// <summary>
/// Real-time 2D spectrum analyser of the output (see <see cref="SpectrumAnalyzer"/> for the
/// analysis): logarithmic frequency axis, dB scale, smoothed bars with a slowly falling peak line.
/// </summary>
/// <remarks>
/// Sits under the oscilloscope in the main window (<c>x:Name="Spectrum"</c>). <c>MainWindow</c>
/// pushes the same scope buffer it gives the <see cref="Oscilloscope"/> every animation frame,
/// together with the engine's sample rate (or the linked DAW plug-in's buffer and rate). The
/// smoothing constants are per frame, so the visual decay speed assumes that ~60 fps cadence.
/// </remarks>
public sealed class SpectrumView : Control
{
    /// <summary>Number of log-spaced bars across the width.</summary>
    const int Bands = 96;

    /// <summary>Rounded panel background.</summary>
    static readonly IBrush Background = AppTheme.Brush("BgDeep");
    /// <summary>Pen of the vertical frequency grid lines (100 Hz, 1 kHz, 10 kHz).</summary>
    static readonly IPen Grid = new Pen(AppTheme.Brush("Grid"), 1);
    /// <summary>Colour of the grid labels.</summary>
    static readonly IBrush Label = AppTheme.Brush("TextFaint");
    /// <summary>Pen of the short horizontal peak-hold line drawn on each bar.</summary>
    static readonly IPen PeakPen = new Pen(AppTheme.Brush("Text"), 1);
    /// <summary>Vertical gradient (bottom → top) relative to the whole panel height, so tall bars reach the light colour.</summary>
    static readonly LinearGradientBrush Fill = AppTheme.Gradient(
        new RelativePoint(0, 1, RelativeUnit.Relative), new RelativePoint(0, 0, RelativeUnit.Relative),
        ("AccentDeep", 0), ("Accent", 0.6), ("AccentLight", 1));

    /// <summary>This view's own analyser (not shared with the 3D waterfall; it is not thread-safe).</summary>
    readonly SpectrumAnalyzer analyzer = new(Bands);
    /// <summary>Displayed bar heights (0…1) after smoothing, and the slowly falling peak-hold values (0…1).</summary>
    readonly double[] level = new double[Bands], peak = new double[Bands];

    /// <summary>Analyses the newest samples of <paramref name="buffer"/> (chronological, ±1.0) and repaints.</summary>
    /// <param name="buffer">Mono samples, oldest first; shorter than <see cref="SpectrumAnalyzer.Size"/> = ignored (the display freezes).</param>
    /// <param name="rate">Sample rate of the buffer in Hz; it positions the bands and the grid lines.</param>
    public void Push(float[] buffer, double rate)
    {
        if (!analyzer.Analyse(buffer, rate)) return;
        for (int b = 0; b < Bands; b++)
        {
            double norm = analyzer.Levels[b];
            // Instant rise, smooth fall (one-pole: keeps 85 % of the old height per frame); the peak
            // line falls linearly by 0.6 % of the scale (≈0.54 dB) per frame and never below the bar.
            level[b] = norm > level[b] ? norm : level[b] * 0.85 + norm * 0.15;
            peak[b] = Math.Max(level[b], peak[b] - 0.006);
        }
        InvalidateVisual();
    }

    /// <summary>Clears the display (used when a performance stops).</summary>
    public void Reset()
    {
        Array.Clear(level);
        Array.Clear(peak);
        InvalidateVisual();
    }

    /// <summary>Draws the background, the labelled frequency grid, then one bar and one peak line per band.</summary>
    /// <param name="ctx">Avalonia drawing context.</param>
    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        ctx.DrawRectangle(Background, null, b, 8, 8);
        // Hz → x pixel on the same log axis the analyser uses for its bands (20 Hz at x = 0,
        // Nyquist at the right edge), so grid lines line up with the bars.
        double nyquist = analyzer.SampleRate / 2;
        double X(double f) => Math.Log(f / SpectrumAnalyzer.MinFrequency) / Math.Log(nyquist / SpectrumAnalyzer.MinFrequency) * b.Width;
        foreach (double f in new[] { 100.0, 1000, 10000 })
        {
            // Bug fix: at low sample rates (e.g. sr = 16000, Nyquist 8 kHz) the 10 kHz line fell
            // beyond the right edge (x > width) and its label was drawn outside the control.
            // Skip any line at or above Nyquist; also guards the log against a 0/unknown rate.
            if (f >= nyquist)
                continue;
            double x = X(f);
            ctx.DrawLine(Grid, new Point(x, 4), new Point(x, b.Height - 4));
            var t = new FormattedText(f >= 1000 ? $"{f / 1000:0}k" : $"{f:0}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 9, Label);
            ctx.DrawText(t, new Point(x + 3, b.Height - t.Height - 2));
        }
        // Bars: equal width (bands are equal on the log axis), 1 px gap, full scale = height − 6 px.
        double w = b.Width / Bands;
        using (ctx.PushClip(new RoundedRect(b, 8)))
            for (int i = 0; i < Bands; i++)
            {
                double h = level[i] * (b.Height - 6);
                if (h > 0.5)
                    ctx.DrawRectangle(Fill, null, new Rect(i * w + 0.5, b.Height - h, Math.Max(1, w - 1), h));
                double py = b.Height - peak[i] * (b.Height - 6);
                if (peak[i] > 0.02)
                    ctx.DrawLine(PeakPen, new Point(i * w + 0.5, py), new Point(i * w + w - 0.5, py));
            }
    }
}
