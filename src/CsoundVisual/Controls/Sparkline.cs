using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CsoundVisual.Theming;

namespace CsoundVisual.Controls;

/// <summary>
/// A tiny line chart of recent values (oldest left, newest right) with a dot on the latest value.
/// Used for live variable values in the hover card and the watch panel.
/// </summary>
/// <remarks>
/// <para>
/// The vertical scale auto-fits every frame to the minimum and maximum of <see cref="Values"/>, so
/// the chart shows the <i>shape</i> of the recent history, not absolute levels. A flat history is
/// drawn as a horizontal line in the middle.
/// </para>
/// <para>
/// In AXAML it is bound to the history ring that <c>LiveValueMonitor</c> publishes for a watched
/// variable (<c>Values="{Binding History}"</c>); in the editor hover card <c>ScrubController</c>
/// sets <see cref="Values"/> from code. Because repaint is driven by
/// <c>AffectsRender</c>, mutating an array in place does
/// nothing visible: the publisher must hand over a new array each time (which the immutable
/// live-value snapshots already do).
/// </para>
/// </remarks>
public sealed class Sparkline : Control
{
    /// <summary>The values to draw (bindable). A new array instance triggers a repaint.</summary>
    public static readonly StyledProperty<float[]?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, float[]?>(nameof(Values));

    /// <summary>Rounded background of the chart.</summary>
    static readonly IBrush Background = AppTheme.Brush("BgDeep");
    /// <summary>Pen of the line itself.</summary>
    static readonly IPen Line = new Pen(AppTheme.Brush("Good"), 1.4, lineJoin: PenLineJoin.Round);
    /// <summary>Translucent fill between the line and the bottom.</summary>
    static readonly IBrush Area = AppTheme.Brush("Good_25");
    /// <summary>Dot on the newest value.</summary>
    static readonly IBrush Dot = AppTheme.Brush("Text");

    /// <summary>Registers <see cref="ValuesProperty"/> as a property that invalidates the visual when it changes.</summary>
    static Sparkline() => AffectsRender<Sparkline>(ValuesProperty);

    /// <summary>The values to draw.</summary>
    public float[]? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>Takes all the offered width (160 px when unconstrained, e.g. inside a horizontal stack) and a default height of 28 px.</summary>
    /// <param name="availableSize">Space offered by the parent.</param>
    /// <returns>The desired size; an explicit <c>Height</c>/<c>MinHeight</c> on the element still wins.</returns>
    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width, 28);

    /// <summary>Draws the background, the filled area, the line and the dot on the latest value.</summary>
    /// <param name="ctx">Avalonia drawing context.</param>
    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        if (b.Width < 4 || b.Height < 4)
            return;
        ctx.DrawRectangle(Background, null, b, 4, 4);
        var v = Values;
        if (v is null || v.Length < 2)
            return;

        // Auto-scale to the data range; a constant history gets an artificial ±0.5 range so it
        // draws as a centred flat line instead of dividing by zero.
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (float x in v) { if (x < lo) lo = x; if (x > hi) hi = x; }
        if (hi - lo < 1e-9f) { lo -= 0.5f; hi += 0.5f; }
        // Data → pixels: index i spreads evenly over the width, value runs from `hi` at the top to
        // `lo` at the bottom (screen Y grows downwards), both inset by `pad` px.
        const double pad = 3;
        double X(int i) => pad + i * (b.Width - 2 * pad) / (v.Length - 1);
        double Y(float x) => pad + (hi - x) / (hi - lo) * (b.Height - 2 * pad);

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var g = line.Open())
        using (var a = area.Open())
        {
            // Both figures are built in one pass: the line is open, the area starts and ends on the
            // bottom edge and is closed so it can be filled.
            a.BeginFigure(new Point(X(0), b.Height - pad), true);
            for (int i = 0; i < v.Length; i++)
            {
                var p = new Point(X(i), Y(v[i]));
                if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
                a.LineTo(p);
            }
            a.LineTo(new Point(X(v.Length - 1), b.Height - pad));
            a.EndFigure(true);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(Area, null, area);
        ctx.DrawGeometry(null, Line, line);
        ctx.DrawEllipse(Dot, null, new Point(X(v.Length - 1), Y(v[^1])), 2.5, 2.5);
    }
}
