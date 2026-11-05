using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CsoundVisual.Engine;
using CsoundVisual.Localization;
using CsoundVisual.Theming;

namespace CsoundVisual.Controls;

/// <summary>
/// Draws the contents of a captured function table: as bars for short tables (≤ 64 points, e.g.
/// scales or step sequences) and as a filled curve otherwise. The vertical scale follows the
/// table's own range, including zero only when it is close to or inside that range.
/// </summary>
/// <remarks>
/// <para>
/// Lives in the "Tables" card of the main window, bound to <c>MainViewModel.SelectedTable</c>
/// (<c>Table="{Binding SelectedTable}"</c>). The <see cref="TableSnapshot"/> is an immutable copy
/// that the engine reads from Csound on its own thread; the view only ever sees finished snapshots,
/// so drawing needs no locking. A new snapshot instance triggers a repaint through
/// <c>AffectsRender</c>.
/// </para>
/// <para>
/// With no table (or fewer than two points) it shows a localised hint (<see cref="Loc.S"/>) instead.
/// </para>
/// </remarks>
public sealed class TableView : Control
{
    /// <summary>The table to draw (bindable).</summary>
    public static readonly StyledProperty<TableSnapshot?> TableProperty =
        AvaloniaProperty.Register<TableView, TableSnapshot?>(nameof(Table));

    /// <summary>Rounded panel background.</summary>
    static readonly IBrush Background = AppTheme.Brush("BgDeep");
    /// <summary>Pen of the zero (baseline) line.</summary>
    static readonly IPen Grid = new Pen(AppTheme.Brush("Grid"), 1);
    /// <summary>Pen of the curve; its brush also paints the 2 px cap on top of each bar.</summary>
    static readonly IPen Line = new Pen(AppTheme.Brush("AccentHi"), 1.5, lineJoin: PenLineJoin.Round);
    /// <summary>Translucent fill under the curve and of the bars.</summary>
    static readonly IBrush Area = AppTheme.Brush("Accent_30");
    /// <summary>Colour of the empty-state hint and of the bar value labels.</summary>
    static readonly IBrush Empty = AppTheme.Brush("TextFaint");

    /// <summary>Registers <see cref="TableProperty"/> as a property that invalidates the visual when it changes.</summary>
    static TableView() => AffectsRender<TableView>(TableProperty);

    /// <summary>The table to draw.</summary>
    public TableSnapshot? Table
    {
        get => GetValue(TableProperty);
        set => SetValue(TableProperty, value);
    }

    /// <summary>
    /// Draws the empty-state hint, or the zero line plus either bars (≤ 64 points, via
    /// <see cref="DrawBars"/>) or a filled curve.
    /// </summary>
    /// <param name="ctx">Avalonia drawing context.</param>
    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        ctx.DrawRectangle(Background, null, b, 8, 8);

        // Property pattern: checks the point count and captures the snapshot in a local, so the
        // styled property is read only once per frame.
        if (Table is not { Points.Length: > 1 } table)
        {
            var hint = new FormattedText(Loc.S("Las tablas aparecen al tocar", "Tables appear while playing"), System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, 11, Empty);
            ctx.DrawText(hint, new Point((b.Width - hint.Width) / 2, (b.Height - hint.Height) / 2));
            return;
        }

        const double pad = 6;
        double lo = table.Minimum, hi = table.Maximum;
        double span = hi - lo;
        // Include zero when the data crosses it or sits near it, so waveforms stay centred.
        // "Near" = the gap to zero is at most twice the data's own span; e.g. a table of MIDI
        // notes 60…72 is NOT pulled down to zero, but an envelope 0.1…1 is.
        if (lo > 0 && lo <= span * 2) lo = 0;
        if (hi < 0 && -hi <= span * 2) hi = 0;
        // Constant table: open a ±1 range around it so it draws as a centred flat line.
        if (hi - lo < 1e-12) { lo -= 1; hi += 1; }
        // Value → y pixel (hi at the top) and point index → x pixel, both inset by `pad`.
        double Y(double v) => pad + (hi - v) / (hi - lo) * (b.Height - 2 * pad);
        double X(int i) => pad + i * (b.Width - 2 * pad) / (table.Points.Length - 1);

        // Baseline: the zero line when zero is in range, otherwise the edge nearest to zero.
        // Bars and the filled area grow from it.
        double zero = Y(Math.Clamp(0, lo, hi));
        ctx.DrawLine(Grid, new Point(pad, zero), new Point(b.Width - pad, zero));

        if (table.Points.Length <= 64)
        {
            DrawBars(ctx, table, b, pad, Y, zero);
            return;
        }

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var g = line.Open())
        using (var a = area.Open())
        {
            a.BeginFigure(new Point(X(0), zero), true);
            for (int i = 0; i < table.Points.Length; i++)
            {
                var p = new Point(X(i), Y(table.Points[i]));
                if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
                a.LineTo(p);
            }
            a.LineTo(new Point(X(table.Points.Length - 1), zero));
            a.EndFigure(true);
            g.EndFigure(false);
        }
        using (ctx.PushClip(new RoundedRect(b, 8)))
        {
            ctx.DrawGeometry(Area, null, area);
            ctx.DrawGeometry(null, Line, line);
        }
    }

    /// <summary>One rounded bar per table point, each labelled with its value when there is room.</summary>
    /// <param name="ctx">Drawing context.</param>
    /// <param name="table">Table with 2…64 points.</param>
    /// <param name="b">Panel rectangle (local coordinates).</param>
    /// <param name="pad">Inner margin in pixels.</param>
    /// <param name="y">Value → y pixel mapping computed by <see cref="Render"/>.</param>
    /// <param name="zero">Y pixel of the baseline the bars grow from (up for positive values, down for negative).</param>
    static void DrawBars(DrawingContext ctx, TableSnapshot table, Rect b, double pad, Func<double, double> y, double zero)
    {
        // Each point gets an equal slot; the bar fills 70 % of it (min 2 px), centred. Labels
        // (formatted by ValueFormat) only appear when a slot is at least 26 px wide.
        int n = table.Points.Length;
        double slot = (b.Width - 2 * pad) / n;
        double width = Math.Max(2, slot * 0.7);
        for (int i = 0; i < n; i++)
        {
            double top = y(table.Points[i]);
            double x = pad + i * slot + (slot - width) / 2;
            var rect = new Rect(x, Math.Min(top, zero), width, Math.Max(1, Math.Abs(zero - top)));
            ctx.DrawRectangle(Area, null, rect, 2, 2);
            ctx.DrawRectangle(Line.Brush, null, new Rect(x, top - 1, width, 2));
            if (slot >= 26)
            {
                var label = new FormattedText(ValueFormat.ValueLabel(table.Points[i]), System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Typeface.Default, 9.5, Empty);
                ctx.DrawText(label, new Point(x + (width - label.Width) / 2, Math.Max(2, Math.Min(top, zero) - label.Height - 1)));
            }
        }
    }
}
