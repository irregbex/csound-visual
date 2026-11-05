using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CsoundVisual.Theming;

namespace CsoundVisual.Controls;

/// <summary>
/// A thin horizontal bar showing where <see cref="Value"/> sits between <see cref="Minimum"/> and
/// <see cref="Maximum"/>. Used inside the editor's hover card.
/// </summary>
/// <remarks>
/// The properties are plain CLR properties (not Avalonia styled properties), so they cannot be
/// bound in AXAML and setting them one by one does not repaint; callers use <see cref="Set"/>,
/// which assigns everything and invalidates once. It is created in code by
/// <c>Editor.ScrubController</c> for the hover card, with the range inferred by <c>CsoundAnalyzer</c>;
/// <see cref="Logarithmic"/> mirrors exponential channel ranges (Csound <c>chnk</c> itype 3).
/// </remarks>
public sealed class RangeIndicator : Control
{
    /// <summary>Background of the 4 px bar.</summary>
    static readonly IBrush Track = AppTheme.Brush("BgDeep");
    /// <summary>Filled part, from <see cref="Minimum"/> up to the value.</summary>
    static readonly IBrush Fill = AppTheme.Brush("AccentMid");
    /// <summary>Round 10 px thumb marking the value.</summary>
    static readonly IBrush Thumb = AppTheme.Brush("AccentLight");

    /// <summary>Lower end of the bar.</summary>
    public double Minimum { get; set; }

    /// <summary>Upper end of the bar.</summary>
    public double Maximum { get; set; } = 1;

    /// <summary>Marked value (clamped to the range when drawn).</summary>
    public double Value { get; set; }

    /// <summary>Plots the value on a logarithmic scale (for exponential ranges with Minimum &gt; 0).</summary>
    public bool Logarithmic { get; set; }

    /// <summary>Updates all values at once and repaints.</summary>
    /// <param name="min">Lower end of the range.</param>
    /// <param name="max">Upper end of the range.</param>
    /// <param name="value">Value to mark.</param>
    /// <param name="logarithmic">Requests a log scale; ignored unless <paramref name="min"/> is positive (log of 0 or a negative is undefined).</param>
    public void Set(double min, double max, double value, bool logarithmic)
    {
        Minimum = min;
        Maximum = max;
        Value = value;
        Logarithmic = logarithmic && min > 0;
        InvalidateVisual();
    }

    /// <summary>Asks for no width (the parent stretches it) and a fixed 10 px height (the thumb diameter).</summary>
    /// <param name="availableSize">Space offered by the parent (unused).</param>
    /// <returns>Desired size 0 × 10.</returns>
    protected override Size MeasureOverride(Size availableSize) => new(0, 10);

    /// <summary>Draws the track, the fill up to the value and the thumb.</summary>
    /// <param name="context">Avalonia drawing context.</param>
    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        // Rendered with zero size while the hover card is hidden; nothing to draw then.
        if (w < 12 || h <= 0)
            return;
        // t = position of the value along the bar, 0…1. An empty or inverted range leaves it at 0.
        double t = 0;
        if (Maximum > Minimum)
        {
            double v = Math.Clamp(Value, Minimum, Maximum);
            t = Logarithmic
                ? Math.Log(v / Minimum) / Math.Log(Maximum / Minimum)
                : (v - Minimum) / (Maximum - Minimum);
        }
        var track = new Rect(0, h / 2 - 2, w, 4);
        context.DrawRectangle(Track, null, track, 2, 2);
        context.DrawRectangle(Fill, null, new Rect(0, h / 2 - 2, w * t, 4), 2, 2);
        // Thumb centre kept 5 px (its radius) inside each end so it is never cut off.
        context.DrawEllipse(Thumb, null, new Point(Math.Clamp(w * t, 5, w - 5), h / 2), 5, 5);
    }
}
