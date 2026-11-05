using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CsoundVisual.Theming;

// ---------------------------------------------------------------------------------------------
// The drawn Cabbage widgets: knob, linear/range slider, number box, button/checkbox/option
// button, XY pad and a placeholder for unsupported types. CabbagePanel creates them and links
// them to channel view models.
//
// Design: each is a plain Control that paints itself in Render (no templates, no AXAML) so it can
// honour Cabbage's per-widget colours and arbitrary sizes, and stay cheap when a form has dozens of
// widgets. All geometry is in the control's own pixels, i.e. Cabbage pixels before the panel's
// Viewbox scaling. Values are kept in the widget's real units (Hz, dB, …); the 0…1 "normalised"
// position used for drawing and dragging goes through CabbageRange.ToNormalized/FromNormalized,
// which apply Cabbage's skew.
//
// Value flow: the Value/Value2/ValueY setters only store and repaint (used when the channel view
// model changes). User gestures call SetByUser/RaiseValue, which also raise ValueChanged, so a
// change coming from the view model is never echoed back to it.
// Colours: Cabbage colours when the widget gives them, otherwise AppTheme roles, so undecorated
// forms follow the app theme. All of this runs on the UI thread.
// ---------------------------------------------------------------------------------------------

namespace CsoundVisual.Cabbage;

/// <summary>Colour helpers for Cabbage widgets (Cabbage colours with theme-based fallbacks).</summary>
public static class CabbageColours
{
    /// <summary>
    /// The Avalonia colour of a Cabbage colour, or <paramref name="fallback"/> when it is missing or
    /// its name is unknown. Names are CSS names (Cabbage uses JUCE's, which are the same list;
    /// "grey" spellings are mapped to "gray").
    /// </summary>
    /// <param name="c">Colour from the widget, or <c>null</c> when not given.</param>
    /// <param name="fallback">Colour used when <paramref name="c"/> is missing or unknown (usually a theme role).</param>
    /// <returns>The Avalonia colour.</returns>
    public static Color Resolve(CabbageColour? c, Color fallback)
    {
        if (c is not { } col) return fallback;
        if (col.Name is { } name)
        {
            string n = name.Replace("grey", "gray");
            return Color.TryParse(n, out var named) ? named : fallback;
        }
        return Color.FromArgb(col.A, col.R, col.G, col.B);
    }

    /// <summary>A brush for <see cref="Resolve"/>.</summary>
    /// <remarks>A new, non-shared brush each call: it does not follow later theme changes (panels are rebuilt instead).</remarks>
    public static IBrush Brush(CabbageColour? c, Color fallback) => new SolidColorBrush(Resolve(c, fallback));

    /// <summary>Current colour of a theme role.</summary>
    /// <param name="role">Role name in <see cref="AppTheme"/> (e.g. "Text", "AccentHi", "BgDeep").</param>
    public static Color Theme(string role) => AppTheme.ColorOf(role);
}

/// <summary>
/// Base of the drawn Cabbage controls: a value (or two) inside a <see cref="CabbageRange"/>,
/// a label, colours, and a <see cref="ValueChanged"/> event raised only by user gestures.
/// </summary>
public abstract class CabbageValueControl : Control, Avalonia.Rendering.ICustomHitTest
{
    /// <summary>
    /// The whole rectangle receives the pointer, not only the drawn pixels (Avalonia's default hit
    /// test of a drawn control ignores empty areas, e.g. beside a slider's track).
    /// </summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <summary>Range, skew and default of the (first) value.</summary>
    public CabbageRange Range { get; set; } = new(0, 1, 0);

    /// <summary>Widget description (label, colours…).</summary>
    public CabbageWidget Widget { get; }

    /// <summary>Backing field of <see cref="Value"/> (real units, clamped to <see cref="Range"/>).</summary>
    double value;

    /// <summary>Creates the control for <paramref name="widget"/>.</summary>
    protected CabbageValueControl(CabbageWidget widget)
    {
        Widget = widget;
        ClipToBounds = false;
    }

    /// <summary>Current value.</summary>
    /// <remarks>
    /// Setting it only clamps, stores and repaints; it does <b>not</b> raise
    /// <see cref="ValueChanged"/> (that is reserved for user gestures, see <see cref="SetByUser"/>).
    /// Set <see cref="Range"/> first: the clamp uses the range current at the time of the set.
    /// </remarks>
    public double Value
    {
        get => value;
        set
        {
            // Here 'value' is the setter's implicit parameter (it shadows the field); the field is 'this.value'.
            double v = Math.Clamp(value, Range.Min, Range.Max);
            if (v.Equals(this.value)) return;
            this.value = v;
            InvalidateVisual();
        }
    }

    /// <summary>Raised when the user changes the value (index of the value, new value).</summary>
    /// <remarks>Index 0 = <see cref="Value"/>; index 1 = the second value (range slider high end, XY pad Y).</remarks>
    public event Action<int, double>? ValueChanged;

    /// <summary>Sets a value from a user gesture and raises <see cref="ValueChanged"/>.</summary>
    /// <param name="v">New value in real units; snapped to the increment and clamped.</param>
    /// <param name="index">0 for the main value. Other indexes only raise the event (subclasses store them).</param>
    protected void SetByUser(double v, int index = 0)
    {
        // Snapping here means drags move in increment steps and no redundant events fire between steps.
        v = Range.Snap(v);
        if (index == 0)
        {
            if (v.Equals(Value)) return;
            Value = v;
        }
        ValueChanged?.Invoke(index, v);
    }

    /// <summary>Raises <see cref="ValueChanged"/> for a secondary value.</summary>
    protected void RaiseValue(int index, double v) => ValueChanged?.Invoke(index, v);

    /// <summary>Formats a value with as many decimals as the increment needs.</summary>
    /// <param name="v">Value to show.</param>
    /// <returns>Invariant-culture text: 0.01 → 2 decimals, 1 or more → none, continuous → 3, at most 4.</returns>
    protected string Format(double v)
    {
        // decimals = ceil(−log10(increment)); the 1e-9 keeps exact powers of ten (0.1 → 1, not 2).
        double inc = Range.Increment;
        int decimals = inc <= 0 ? 3 : inc >= 1 ? 0 : Math.Clamp((int)Math.Ceiling(-Math.Log10(inc) - 1e-9), 0, 4);
        return v.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }

    /// <summary>Text for drawing.</summary>
    /// <param name="s">Text.</param>
    /// <param name="size">Font size in pixels (at least 1).</param>
    /// <param name="brush">Foreground.</param>
    /// <param name="bold">Semi-bold when true.</param>
    /// <returns>A formatted text in the default font family.</returns>
    protected static FormattedText Text(string s, double size, IBrush brush, bool bold = false) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), Math.Max(1, size), brush);

    /// <summary>Draws <paramref name="t"/> centred horizontally in <paramref name="width"/> at <paramref name="y"/>.</summary>
    protected static void DrawCentered(DrawingContext ctx, FormattedText t, double width, double y) =>
        ctx.DrawText(t, new Point((width - t.Width) / 2, y));

    /// <summary>Colour of the text (fontcolour/textcolour or the theme text colour).</summary>
    protected Color TextColour => CabbageColours.Resolve(Widget.Colour("textcolour", "fontcolour"), CabbageColours.Theme("Text"));

    /// <summary>Tracker (value) colour.</summary>
    protected Color TrackerColour => CabbageColours.Resolve(Widget.Colour("trackercolour"), CabbageColours.Theme("AccentHi"));

    /// <summary>Body colour.</summary>
    protected Color BodyColour => CabbageColours.Resolve(Widget.Colour("colour"), CabbageColours.Theme("Surface2"));

    /// <summary>True while the pointer is over the control or dragging (the value is shown).</summary>
    protected bool Hot { get; set; }

    /// <inheritdoc/>
    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        Hot = true;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Hot = false;
        InvalidateVisual();
    }
}

/// <summary>
/// Rotary knob (<c>rslider</c>, <c>encoder</c>): drag up/down (Ctrl = fine), mouse wheel,
/// double-click resets to the default. A 270° arc shows the value, as in Cabbage.
/// </summary>
public sealed class CabbageKnob : CabbageValueControl
{
    // 135° = bottom-left (7:30 o'clock) because screen Y points down; +270° ends at bottom-right.
    const double StartAngle = 135, Sweep = 270;   // degrees, clockwise from +X (screen coordinates)
    /// <summary>Pointer position at the drag anchor (re-anchored while Ctrl is held).</summary>
    Point dragStart;
    /// <summary>Normalised value (0…1) at the drag anchor.</summary>
    double dragNorm;
    /// <summary>True between a left press and its release (shows the value instead of the label).</summary>
    bool dragging;

    /// <summary>Creates a knob.</summary>
    public CabbageKnob(CabbageWidget widget) : base(widget) => Cursor = new Cursor(StandardCursorType.SizeNorthSouth);

    /// <summary>Shows the value under the knob permanently (<c>valueTextBox(1)</c>).</summary>
    public bool AlwaysShowValue { get; set; }

    /// <inheritdoc/>
    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        string label = Widget.Label;
        // Layout: a text strip at the bottom (label/value) and the largest circle that fits above it,
        // centred horizontally and vertically in the remaining space. d = diameter, r = radius.
        double textSize = Math.Clamp(Math.Min(w, h) * 0.15, 7, 16);
        double labelH = label.Length > 0 || AlwaysShowValue ? textSize * 1.35 : 0;
        double d = Math.Max(4, Math.Min(w, h - labelH) - 4);
        var c = new Point(w / 2, 2 + d / 2 + Math.Max(0, (h - labelH - d - 4) / 2));
        double r = d / 2;
        double thick = Math.Max(2, r * 0.18);   // arc stroke width

        // Track and tracker arcs (the arc is drawn on the stroke's centre line, hence r − thick/2).
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), thick, lineCap: PenLineCap.Round), Arc(c, r - thick / 2, StartAngle, Sweep));
        double n = Range.ToNormalized(Value);
        if (Range.Min < 0 && Range.Max > 0)
        {
            // Bipolar: draw from the zero position.
            double z = Range.ToNormalized(0);
            double a0 = StartAngle + Sweep * Math.Min(z, n), a1 = StartAngle + Sweep * Math.Max(z, n);
            if (a1 - a0 > 0.5)
                ctx.DrawGeometry(null, new Pen(new SolidColorBrush(TrackerColour), thick, lineCap: PenLineCap.Round), Arc(c, r - thick / 2, a0, a1 - a0));
        }
        // Unipolar: from the start of the sweep; skipped near zero so a round cap does not draw a dot.
        else if (n > 0.002)
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(TrackerColour), thick, lineCap: PenLineCap.Round), Arc(c, r - thick / 2, StartAngle, Sweep * n));

        // Body with a soft gradient and a pointer line.
        // br = body radius, inside the arc with a gap; the light comes from the upper left.
        double br = r - thick * 1.6;
        if (br > 2)
        {
            var body = BodyColour;
            var grad = new RadialGradientBrush
            {
                GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Lighten(body, 0.25), 0),
                    new GradientStop(body, 0.7),
                    new GradientStop(Darken(body, 0.35), 1),
                },
            };
            ctx.DrawEllipse(grad, new Pen(new SolidColorBrush(CabbageColours.Resolve(Widget.Colour("outlinecolour"), Darken(body, 0.5))), 1), c, br, br);
            double ang = (StartAngle + Sweep * n) * Math.PI / 180;
            var tip = new Point(c.X + Math.Cos(ang) * br * 0.85, c.Y + Math.Sin(ang) * br * 0.85);
            var from = new Point(c.X + Math.Cos(ang) * br * 0.25, c.Y + Math.Sin(ang) * br * 0.25);
            ctx.DrawLine(new Pen(new SolidColorBrush(CabbageColours.Resolve(Widget.Colour("markercolour"), TextColour)), Math.Max(1.5, br * 0.12), lineCap: PenLineCap.Round), from, tip);
        }

        // Label under the knob, replaced by the value while hovering or dragging.
        if (labelH > 0)
        {
            bool showValue = dragging || Hot || (AlwaysShowValue && label.Length == 0);
            var t = Text(showValue ? Format(Value) : label, textSize, new SolidColorBrush(TextColour));
            DrawCentered(ctx, t, w, h - labelH + (labelH - t.Height) / 2);
        }
        else if (dragging || Hot)
        {
            // No text strip: show the value over the knob body while hovering/dragging.
            var t = Text(Format(Value), Math.Max(7, r * 0.42), new SolidColorBrush(TextColour), true);
            ctx.DrawText(t, new Point(c.X - t.Width / 2, c.Y - t.Height / 2));
        }
    }

    /// <summary>An open circular arc.</summary>
    /// <param name="c">Centre.</param>
    /// <param name="r">Radius.</param>
    /// <param name="startDeg">Start angle in degrees, clockwise from +X on screen.</param>
    /// <param name="sweepDeg">Clockwise sweep in degrees (above 180 the large-arc flag is set).</param>
    /// <returns>The arc geometry (not closed, not filled).</returns>
    static StreamGeometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        var g = new StreamGeometry();
        using var gc = g.Open();
        double a0 = startDeg * Math.PI / 180, a1 = (startDeg + sweepDeg) * Math.PI / 180;
        gc.BeginFigure(new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0)), false);
        gc.ArcTo(new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1)), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise);
        gc.EndFigure(false);
        return g;
    }

    /// <summary>Mixes <paramref name="c"/> towards white by fraction <paramref name="f"/> (0…1); alpha kept.</summary>
    internal static Color Lighten(Color c, double f) =>
        Color.FromArgb(c.A, (byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f));

    /// <summary>Mixes <paramref name="c"/> towards black by fraction <paramref name="f"/> (0…1); alpha kept.</summary>
    internal static Color Darken(Color c, double f) =>
        Color.FromArgb(c.A, (byte)(c.R * (1 - f)), (byte)(c.G * (1 - f)), (byte)(c.B * (1 - f)));

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // Double-click: back to the range's default value.
        if (e.ClickCount == 2)
        {
            SetByUser(Range.Value);
            e.Handled = true;
            return;
        }
        // Relative drag: remember where it started and the normalised value there; moves are
        // measured from this anchor, so clicking does not make the knob jump.
        dragging = true;
        dragStart = e.GetPosition(this);
        dragNorm = Range.ToNormalized(Value);
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!dragging) return;
        var p = e.GetPosition(this);
        // 200 px for the whole range (1000 px with Ctrl for fine adjustments).
        // Up = more (screen Y grows downwards, hence start − current). Works in normalised units so
        // a skewed range feels the same as on screen.
        double span = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 1000 : 200;
        double n = Math.Clamp(dragNorm + (dragStart.Y - p.Y) / span, 0, 1);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Re-anchor so switching Ctrl on/off mid-drag does not jump.
            dragNorm = n;
            dragStart = p;
        }
        SetByUser(Range.FromNormalized(n));
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!dragging) return;
        dragging = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // Each notch moves 2 % of the travel (0.2 % with Ctrl), whatever the wheel's delta size.
        double step = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 0.002 : 0.02;
        double n = Math.Clamp(Range.ToNormalized(Value) + Math.Sign(e.Delta.Y) * step, 0, 1);
        double v = Range.FromNormalized(n);
        if (v.Equals(Value) && Range.Increment > 0)
            v = Range.Snap(Value + Math.Sign(e.Delta.Y) * Range.Increment);   // coarse increments: at least one step
        SetByUser(v);
        e.Handled = true;
    }
}

/// <summary>
/// Linear slider (<c>hslider</c>, <c>vslider</c>) and range slider (<c>hrange</c>, <c>vrange</c>,
/// two values). Click or drag to set (Ctrl = fine), wheel, double-click resets.
/// </summary>
public sealed class CabbageSlider : CabbageValueControl
{
    /// <summary>True for <c>vslider</c>/<c>vrange</c> (bottom = minimum).</summary>
    readonly bool vertical;
    /// <summary>True for a two-thumb range slider (<see cref="CabbageValueControl.Value"/> = low, <see cref="Value2"/> = high).</summary>
    readonly bool isRange;
    /// <summary>Backing field of <see cref="Value2"/>.</summary>
    double value2;
    /// <summary>Thumb being dragged: -1 = none, 0 = main/low value, 1 = high value of a range slider.</summary>
    int dragIndex = -1;
    /// <summary>Previous pointer position (used for the relative Ctrl fine drag).</summary>
    Point dragStart;
    /// <summary>Normalised value at the previous pointer position.</summary>
    double dragNorm;

    /// <summary>Creates a slider.</summary>
    public CabbageSlider(CabbageWidget widget, bool vertical, bool isRange) : base(widget)
    {
        this.vertical = vertical;
        this.isRange = isRange;
        Cursor = new Cursor(vertical ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast);
    }

    /// <summary>Second value of a range slider.</summary>
    /// <remarks>Like <see cref="CabbageValueControl.Value"/>: clamps and repaints, never raises <see cref="CabbageValueControl.ValueChanged"/>.</remarks>
    public double Value2
    {
        get => value2;
        set { value2 = Math.Clamp(value, Range.Min, Range.Max); InvalidateVisual(); }
    }

    /// <summary>Default of the second value.</summary>
    public double Default2 { get; set; }

    /// <summary>
    /// Computes where the track goes. Vertical: label on top, value text at the bottom, a narrow
    /// track centred between them. Horizontal: label at the left, value text at the right (sized
    /// for the widest possible value so the track does not change length while dragging), track
    /// in between.
    /// </summary>
    /// <returns>The track rectangle, the text size and the width reserved for the label.</returns>
    /// <remarks>Shared by <see cref="Render"/> and the pointer handlers so hit testing matches drawing.</remarks>
    (Rect Track, double TextSize, double LabelW) Layout()
    {
        double w = Bounds.Width, h = Bounds.Height;
        string label = Widget.Label;
        if (vertical)
        {
            double ts = Math.Clamp(w * 0.3, 7, 14);
            double top = label.Length > 0 ? ts * 1.4 : 2;
            double bottom = ts * 1.4;
            return (new Rect(w / 2 - Math.Max(2, w * 0.12), top + 4, Math.Max(4, w * 0.24), Math.Max(4, h - top - bottom - 8)), ts, 0);
        }
        double tsz = Math.Clamp(h * 0.45, 7, 14);
        double labelW = label.Length > 0 ? Math.Min(w * 0.3, Text(label, tsz, Brushes.White).Width + 8) : 0;
        // Room for the widest value text (both values for a range slider).
        string widest = Format(Range.Min).Length > Format(Range.Max).Length ? Format(Range.Min) : Format(Range.Max);
        if (isRange) widest = widest + "–" + widest;
        double valueW = Math.Min(w * (isRange ? 0.45 : 0.3), Text(widest, tsz * 0.9, Brushes.White).Width + 4);
        double th = Math.Max(4, h * 0.28);
        return (new Rect(labelW + 6, h / 2 - th / 2, Math.Max(4, w - labelW - valueW - 12), th), tsz, labelW);
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext ctx)
    {
        var (track, ts, labelW) = Layout();
        double w = Bounds.Width, h = Bounds.Height;
        var textBrush = new SolidColorBrush(TextColour);
        var trackBrush = new SolidColorBrush(BodyColour);
        ctx.DrawRectangle(trackBrush, new Pen(new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)), 1), track, 3, 3);

        // Filled part of the track, in normalised units: between the two thumbs for a range slider,
        // from zero for a bipolar range (min < 0 < max), else from the minimum.
        double n1 = Range.ToNormalized(Value), n2 = isRange ? Range.ToNormalized(Value2) : n1;
        double from = isRange ? Math.Min(n1, n2) : (Range.Min < 0 && Range.Max > 0 ? Range.ToNormalized(0) : 0);
        double to = isRange ? Math.Max(n1, n2) : n1;
        if (from > to) (from, to) = (to, from);
        var tracker = new SolidColorBrush(TrackerColour);
        Rect fill = vertical
            ? new Rect(track.X, track.Bottom - track.Height * to, track.Width, track.Height * (to - from))
            : new Rect(track.X + track.Width * from, track.Y, track.Width * (to - from), track.Height);
        ctx.DrawRectangle(tracker, null, fill, 3, 3);

        // Thumbs: a rectangle across the track, 3.2× the track's thickness long.
        var thumbBrush = new SolidColorBrush(CabbageColours.Resolve(Widget.Colour("colour:1", "markercolour"), CabbageColours.Theme("PianoWhite")));
        foreach (double n in isRange ? new[] { n1, n2 } : [n1])
        {
            double tw = vertical ? track.Width * 3.2 : Math.Max(6, track.Height * 1.1);
            double th = vertical ? Math.Max(6, track.Width * 1.1) : track.Height * 3.2;
            var center = vertical ? new Point(track.Center.X, track.Bottom - track.Height * n) : new Point(track.X + track.Width * n, track.Center.Y);
            ctx.DrawRectangle(thumbBrush, new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 1),
                new Rect(center.X - tw / 2, center.Y - th / 2, tw, th), 2, 2);
        }

        string valueText = isRange ? $"{Format(Value)}–{Format(Value2)}" : Format(Value);
        if (vertical)
        {
            if (Widget.Label.Length > 0) DrawCentered(ctx, Text(Widget.Label, ts, textBrush), w, 0);
            DrawCentered(ctx, Text(valueText, ts * 0.9, textBrush), w, h - ts * 1.35);
        }
        else
        {
            if (Widget.Label.Length > 0)
            {
                var lt = Text(Widget.Label, ts, textBrush);
                ctx.DrawText(lt, new Point(2, (h - lt.Height) / 2));
            }
            var vt = Text(valueText, ts * 0.9, textBrush);
            ctx.DrawText(vt, new Point(w - vt.Width - 2, (h - vt.Height) / 2));
        }
    }

    /// <summary>Normalised track position under <paramref name="p"/> (not clamped; may be outside 0…1).</summary>
    double NormAt(Point p)
    {
        var (track, _, _) = Layout();
        return vertical ? (track.Bottom - p.Y) / track.Height : (p.X - track.X) / track.Width;
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        double n = Math.Clamp(NormAt(p), 0, 1);
        // Absolute: the thumb jumps to the click. On a range slider the nearer thumb is taken
        // (ties go to the low one).
        dragIndex = isRange && Math.Abs(n - Range.ToNormalized(Value2)) < Math.Abs(n - Range.ToNormalized(Value)) ? 1 : 0;
        if (e.ClickCount == 2)
        {
            if (dragIndex == 0) SetByUser(Range.Value);
            else { Value2 = Default2; RaiseValue(1, Default2); }
            dragIndex = -1;
            e.Handled = true;
            return;
        }
        Apply(n);
        dragStart = p;
        dragNorm = n;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <summary>
    /// Sets the dragged thumb to normalised position <paramref name="n"/>. The two thumbs of a
    /// range slider cannot cross: the high one stops at the low value and vice versa.
    /// </summary>
    void Apply(double n)
    {
        double v = Range.FromNormalized(n);
        if (dragIndex == 1)
        {
            v = Math.Max(v, Value);
            if (!v.Equals(Value2)) { Value2 = v; RaiseValue(1, v); }
        }
        else
        {
            if (isRange) v = Math.Min(v, Value2);
            SetByUser(v);
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragIndex < 0) return;
        var p = e.GetPosition(this);
        double n;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Fine: relative movement at 1/5 speed from the previous position.
            var (track, _, _) = Layout();
            double delta = vertical ? (dragStart.Y - p.Y) / track.Height : (p.X - dragStart.X) / track.Width;
            n = Math.Clamp(dragNorm + delta * 0.2, 0, 1);
        }
        else
            n = Math.Clamp(NormAt(p), 0, 1);   // normal: thumb follows the pointer
        // Always track the last point so toggling Ctrl mid-drag continues smoothly.
        dragStart = p;
        dragNorm = n;
        Apply(n);
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (dragIndex < 0) return;
        dragIndex = -1;
        e.Pointer.Capture(null);
    }

    /// <inheritdoc/>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // Ambiguous which thumb the wheel would move, so range sliders ignore it.
        if (isRange) return;
        double step = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 0.002 : 0.02;
        SetByUser(Range.FromNormalized(Math.Clamp(Range.ToNormalized(Value) + Math.Sign(e.Delta.Y) * step, 0, 1)));
        e.Handled = true;
    }
}

/// <summary>Number box (<c>nslider</c>): drag up/down to change, wheel, double-click resets.</summary>
public sealed class CabbageNumberBox : CabbageValueControl
{
    /// <summary>Pointer position when the drag started.</summary>
    Point dragStart;
    /// <summary>Value (real units) when the drag started; the drag offsets from it.</summary>
    double dragValue;
    /// <summary>True between a left press and its release.</summary>
    bool dragging;

    /// <summary>Creates a number box.</summary>
    public CabbageNumberBox(CabbageWidget widget) : base(widget) => Cursor = new Cursor(StandardCursorType.SizeNorthSouth);

    /// <inheritdoc/>
    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        string label = Widget.Label;
        // Label (if any) on top, then a box with the value centred in it.
        double ts = Math.Clamp(h * (label.Length > 0 ? 0.32 : 0.55), 7, 16);
        double boxTop = label.Length > 0 ? ts * 1.3 : 0;
        var box = new Rect(0, boxTop, w, Math.Max(4, h - boxTop));
        ctx.DrawRectangle(new SolidColorBrush(CabbageColours.Resolve(Widget.Colour("colour"), CabbageColours.Theme("BgDeep"))),
            new Pen(new SolidColorBrush(CabbageColours.Resolve(Widget.Colour("outlinecolour"), CabbageColours.Theme("Line"))), 1), box, 3, 3);
        var brush = new SolidColorBrush(TextColour);
        if (label.Length > 0) DrawCentered(ctx, Text(label, ts * 0.9, brush), w, 0);
        var vt = Text(Format(Value), Math.Clamp(box.Height * 0.6, 7, 18), new SolidColorBrush(CabbageColours.Resolve(Widget.Colour("fontcolour"), CabbageColours.Theme("AccentHi"))));
        ctx.DrawText(vt, new Point((w - vt.Width) / 2, box.Y + (box.Height - vt.Height) / 2));
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) { SetByUser(Range.Value); e.Handled = true; return; }
        dragging = true;
        dragStart = e.GetPosition(this);
        dragValue = Value;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!dragging) return;
        // Linear in real units (no skew): pixels dragged upwards since the press × value per pixel.
        double dy = dragStart.Y - e.GetPosition(this).Y;
        double step = Range.Increment > 0 ? Range.Increment : (Range.Max - Range.Min) / 200;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) dy *= 0.2;
        // One increment per 2 pixels, but never slower than the whole range in 1000 px.
        double perPixel = Math.Max(step / 2, (Range.Max - Range.Min) / 1000);
        SetByUser(dragValue + dy * perPixel);
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        dragging = false;
        e.Pointer.Capture(null);
    }

    /// <inheritdoc/>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // One increment per notch (1 % of the range when continuous).
        double step = Range.Increment > 0 ? Range.Increment : (Range.Max - Range.Min) / 100;
        SetByUser(Value + Math.Sign(e.Delta.Y) * step);
        e.Handled = true;
    }
}

/// <summary>
/// Button, checkbox and option button. A latched button/checkbox toggles 0/1; <c>latched(0)</c>
/// buttons are momentary (1 while pressed); an option button cycles through its items.
/// </summary>
public sealed class CabbageButton : CabbageValueControl
{
    /// <summary>Kinds of button.</summary>
    public enum Kind
    {
        /// <summary>Push button with off/on colours and captions.</summary>
        Button,
        /// <summary>Tick box with a label.</summary>
        CheckBox,
        /// <summary>Cycles through items on each click.</summary>
        Option,
    }

    /// <summary>Which of the three behaviours this button has.</summary>
    readonly Kind kind;
    /// <summary>True while the left button is held on the control (pressed look; momentary buttons send 1).</summary>
    bool pressed;

    /// <summary>Creates a button of the given kind.</summary>
    public CabbageButton(CabbageWidget widget, Kind kind) : base(widget)
    {
        this.kind = kind;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        bool on = Value >= 0.5;
        if (kind == Kind.CheckBox)
        {
            // Square (or round with shape("ellipse")) box at the left, as tall as the widget,
            // filled with colour:1 when on and colour:0 when off; the label follows it.
            double s = Math.Min(h, w);
            bool round = Widget.Shape is "ellipse" or "circle";
            var box = new Rect(0, (h - s) / 2, s, s);
            var onColour = CabbageColours.Resolve(Widget.Colour("colour:1", "colour"), CabbageColours.Theme("Accent"));
            var offColour = CabbageColours.Resolve(Widget.Colour("colour:0"), CabbageColours.Theme("BgDeep"));
            IBrush fill = new SolidColorBrush(on ? onColour : offColour);
            var pen = new Pen(new SolidColorBrush(CabbageColours.Resolve(Widget.Colour("outlinecolour"), CabbageColours.Theme("Line"))), 1);
            if (round) ctx.DrawEllipse(fill, pen, box.Center, s / 2, s / 2);
            else ctx.DrawRectangle(fill, pen, box, s * 0.18, s * 0.18);
            if (Widget.Label.Length > 0 && w > s + 4)
            {
                var t = Text(Widget.Label, Math.Clamp(h * 0.75, 7, 16), new SolidColorBrush(TextColour));
                ctx.DrawText(t, new Point(s + 4, (h - t.Height) / 2));
            }
            return;
        }

        // Push button / option button: rounded rectangle with off/on colours and captions.
        var c0 = CabbageColours.Resolve(Widget.Colour("colour:0", "colour"), CabbageColours.Theme("Surface2"));
        var c1 = CabbageColours.Resolve(Widget.Colour("colour:1"), CabbageColours.Theme("Accent"));
        var f0 = CabbageColours.Resolve(Widget.Colour("fontcolour:0", "fontcolour", "textcolour"), CabbageColours.Theme("Text"));
        var f1 = CabbageColours.Resolve(Widget.Colour("fontcolour:1", "fontcolour", "textcolour"), CabbageColours.Theme("OnAccent"));
        // Cabbage convention: index 0 = off state, index 1 = on state (colour:0/1, fontcolour:0/1,
        // text("off", "on")). Option buttons never "light", they just darken while pressed.
        bool lit = kind == Kind.Button && (on || pressed);
        var back = lit ? c1 : c0;
        if (pressed && kind == Kind.Option) back = CabbageKnob.Darken(back, 0.2);
        double radius = double.IsNaN(Widget.Corners) ? Math.Min(6, h / 3) : Widget.Corners;
        ctx.DrawRectangle(new SolidColorBrush(back), new Pen(new SolidColorBrush(CabbageColours.Resolve(Widget.Colour("outlinecolour"), CabbageKnob.Darken(back, 0.45))), 1),
            new Rect(0.5, 0.5, Math.Max(1, w - 1), Math.Max(1, h - 1)), radius, radius);
        string caption = kind == Kind.Option
            ? (Widget.Choices.Count > 0 ? Widget.Choices[(int)Math.Clamp(Math.Round(Value), 0, Widget.Choices.Count - 1)] : Format(Value))
            : Widget.Text.Count >= 2 && on ? Widget.Text[1] : Widget.Text.Count > 0 ? Widget.Text[0] : "";
        if (caption.Length > 0)
        {
            var t = Text(caption, Math.Clamp(h * 0.5, 7, 16), new SolidColorBrush(lit ? f1 : f0), true);
            // Too wide: shrink the font proportionally so the caption fits (minimum 6 px).
            if (t.Width > w - 4) t = Text(caption, Math.Max(6, Math.Clamp(h * 0.5, 7, 16) * (w - 4) / t.Width), new SolidColorBrush(lit ? f1 : f0), true);
            ctx.DrawText(t, new Point((w - t.Width) / 2, (h - t.Height) / 2));
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        pressed = true;
        // Capture so the release arrives here even if the pointer leaves (a momentary button must send 0).
        e.Pointer.Capture(this);
        switch (kind)
        {
            case Kind.Option:
                // Next item, wrapping to the first after the last.
                SetByUser(Value + 1 > Range.Max ? Range.Min : Value + 1);
                break;
            case Kind.Button when !Widget.Latched:
                // Momentary: 1 while held, back to 0 on release.
                SetByUser(1);
                break;
            default:
                // Latched button or checkbox: toggle.
                SetByUser(Value >= 0.5 ? 0 : 1);
                break;
        }
        InvalidateVisual();
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!pressed) return;
        pressed = false;
        e.Pointer.Capture(null);
        if (kind == Kind.Button && !Widget.Latched)
            SetByUser(0);
        InvalidateVisual();
    }
}

/// <summary>XY pad: two channels (x to the right, y upwards). Drag the ball; double-click resets both.</summary>
/// <remarks>
/// X is the base class's <see cref="CabbageValueControl.Value"/> (channel 0, <see cref="CabbageValueControl.Range"/>);
/// Y is <see cref="ValueY"/> (channel 1, <see cref="RangeY"/>). Clicking jumps the ball to the
/// pointer (absolute positioning, unlike the knob's relative drag).
/// </remarks>
public sealed class CabbageXYPad : CabbageValueControl
{
    /// <summary>Backing field of <see cref="ValueY"/>.</summary>
    double y;
    /// <summary>True between a left press and its release.</summary>
    bool dragging;

    /// <summary>Creates an XY pad.</summary>
    public CabbageXYPad(CabbageWidget widget) : base(widget) => Cursor = new Cursor(StandardCursorType.Cross);

    /// <summary>Range of the vertical value.</summary>
    public CabbageRange RangeY { get; set; } = new(0, 1, 0);

    /// <summary>Vertical value.</summary>
    /// <remarks>Clamps and repaints only; set <see cref="RangeY"/> first.</remarks>
    public double ValueY
    {
        get => y;
        set { y = Math.Clamp(value, RangeY.Min, RangeY.Max); InvalidateVisual(); }
    }

    /// <summary>The pad surface: the whole control minus a text strip at the bottom for the value readout.</summary>
    Rect Area()
    {
        double ts = Math.Clamp(Bounds.Height * 0.06, 7, 13);
        return new Rect(0, 0, Bounds.Width, Math.Max(10, Bounds.Height - ts * 1.6));
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext ctx)
    {
        var a = Area();
        double ts = Math.Clamp(Bounds.Height * 0.06, 7, 13);
        var bg = CabbageColours.Resolve(Widget.Colour("colour"), CabbageColours.Theme("BgDeep"));
        ctx.DrawRectangle(new SolidColorBrush(bg), new Pen(new SolidColorBrush(CabbageColours.Resolve(Widget.Colour("outlinecolour"), CabbageColours.Theme("Line"))), 1), a, 4, 4);
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
        for (int i = 1; i < 4; i++)
        {
            ctx.DrawLine(gridPen, new Point(a.X + a.Width * i / 4, a.Y), new Point(a.X + a.Width * i / 4, a.Bottom));
            ctx.DrawLine(gridPen, new Point(a.X, a.Y + a.Height * i / 4), new Point(a.Right, a.Y + a.Height * i / 4));
        }
        // Value → pixel: X grows to the right, Y grows upwards (hence Bottom − …).
        double px = a.X + a.Width * Range.ToNormalized(Value), py = a.Bottom - a.Height * RangeY.ToNormalized(ValueY);
        var ball = CabbageColours.Resolve(Widget.Colour("ballcolour", "trackercolour"), CabbageColours.Theme("AccentHi"));
        var crossPen = new Pen(new SolidColorBrush(Color.FromArgb(90, ball.R, ball.G, ball.B)), 1);
        ctx.DrawLine(crossPen, new Point(px, a.Y), new Point(px, a.Bottom));
        ctx.DrawLine(crossPen, new Point(a.X, py), new Point(a.Right, py));
        double r = Math.Clamp(Math.Min(a.Width, a.Height) * 0.04, 4, 12);
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(70, ball.R, ball.G, ball.B)), null, new Point(px, py), r * 2, r * 2);
        ctx.DrawEllipse(new SolidColorBrush(ball), new Pen(Brushes.White, 1), new Point(px, py), r, r);

        // Readout strip: "xchannel value   ychannel value"; the label is drawn faintly inside the pad.
        var brush = new SolidColorBrush(TextColour);
        var t = Text($"{Widget.Channels.ElementAtOrDefault(0)} {Format(Value)}   {Widget.Channels.ElementAtOrDefault(1)} {Format(ValueY)}", ts, brush);
        ctx.DrawText(t, new Point(2, a.Bottom + (Bounds.Height - a.Bottom - t.Height) / 2));
        if (Widget.Label.Length > 0)
        {
            var lt = Text(Widget.Label, ts, new SolidColorBrush(Color.FromArgb(150, brush.Color.R, brush.Color.G, brush.Color.B)));
            ctx.DrawText(lt, new Point(a.X + 6, a.Y + 4));
        }
    }

    /// <summary>
    /// Pixel → values: moves the ball to <paramref name="p"/> (clamped to the pad), raising
    /// <see cref="CabbageValueControl.ValueChanged"/> for X (index 0) and, when it changed, Y (index 1).
    /// </summary>
    void Apply(Point p)
    {
        var a = Area();
        double nx = Math.Clamp((p.X - a.X) / a.Width, 0, 1), ny = Math.Clamp((a.Bottom - p.Y) / a.Height, 0, 1);
        SetByUser(Range.FromNormalized(nx));
        double vy = RangeY.FromNormalized(ny);
        if (!vy.Equals(ValueY))
        {
            ValueY = vy;
            RaiseValue(1, vy);
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            SetByUser(Range.Value);
            ValueY = RangeY.Value;
            RaiseValue(1, ValueY);
            e.Handled = true;
            return;
        }
        dragging = true;
        e.Pointer.Capture(this);
        Apply(e.GetPosition(this));
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!dragging) return;
        Apply(e.GetPosition(this));
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        dragging = false;
        e.Pointer.Capture(null);
    }
}

/// <summary>A meter (<c>hmeter</c>/<c>vmeter</c>) or any widget Csound Visual does not draw: an outlined box with its name.</summary>
/// <remarks>
/// Keeps the form's layout readable when a widget type is unsupported (gentable, soundfiler…):
/// the dashed outline and the caption tell the user something is there. Not interactive.
/// </remarks>
public sealed class CabbagePlaceholder : Control
{
    /// <summary>The widget represented (for its background colour).</summary>
    readonly CabbageWidget widget;
    /// <summary>Text drawn inside the box (wrapped to the box width).</summary>
    readonly string caption;

    /// <summary>Creates a placeholder with <paramref name="caption"/>.</summary>
    public CabbagePlaceholder(CabbageWidget widget, string caption)
    {
        this.widget = widget;
        this.caption = caption;
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(0.5, 0.5, Math.Max(1, Bounds.Width - 1), Math.Max(1, Bounds.Height - 1));
        var bg = CabbageColours.Resolve(widget.Colour("colour", "tablebackgroundcolour"), Color.FromArgb(60, 0, 0, 0));
        ctx.DrawRectangle(new SolidColorBrush(bg), new Pen(AppTheme.Brush("TextFaint"), 1, new DashStyle([4, 3], 0)), r, 3, 3);
        double size = Math.Clamp(Math.Min(Bounds.Height * 0.3, 12), 6, 12);
        var t = new FormattedText(caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, AppTheme.Brush("TextDim"));
        t.MaxTextWidth = Math.Max(10, Bounds.Width - 6);
        t.MaxTextHeight = Math.Max(size, Bounds.Height - 4);
        ctx.DrawText(t, new Point(4, 3));
    }
}
