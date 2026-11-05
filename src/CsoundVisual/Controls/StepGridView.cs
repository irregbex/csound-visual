using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CsoundVisual.Localization;
using CsoundVisual.Music;
using CsoundVisual.Theming;

namespace CsoundVisual.Controls;

/// <summary>
/// The step grid of the step sequencer: one row per track, one cell per step (sixteenth note),
/// drawn by this control. The row headers (instrument, note, mute/solo…) are ordinary controls
/// laid out by the window with the same <see cref="RowHeight"/>.
/// </summary>
/// <remarks>
/// <para><b>Mouse</b>: click a cell = on/off; keep the button down and sweep across cells to paint
/// the same state; keep it down and move up/down on the cell (or Shift+drag, or the wheel) = that
/// step's velocity,
/// shown as the height of the bar; right-click = accent on/off (127 ↔ the track's velocity).</para>
/// <para>Cells are grouped by beats (4 sixteenths) with alternating shades; the step being heard
/// is highlighted (<see cref="CurrentStep"/>).</para>
/// <para>
/// <b>Model and ownership.</b> The control edits the <see cref="StepPattern"/> it is given
/// <i>in place</i> (<c>track.Steps[step]</c> = 0 for off, 1…127 for on with that velocity) on the
/// UI thread. It never talks to the scheduler or Csound: it raises <see cref="EditStarting"/> before
/// a gesture (the window takes an undo checkpoint), <see cref="Edited"/> after every change (the
/// window republishes an immutable snapshot of the pattern to <c>TransportScheduler</c>),
/// <see cref="StepSwitchedOn"/> to audition a newly enabled step and <see cref="Hover"/> for the
/// status line. The playhead (<see cref="CurrentStep"/>) is pushed by the window from the
/// transport's tick.
/// </para>
/// <para>
/// <b>Coordinates.</b> x → step: <c>step = ⌊x / CellWidth⌋</c>; y → row:
/// <c>row = ⌊(y − HeaderHeight) / RowHeight⌋</c>. The cell width stretches to fill the control
/// (minimum 10 px); rows have a fixed height so the window's row headers line up with them.
/// The window puts this control in a vertical-only <c>ScrollViewer</c>.
/// </para>
/// <para>
/// <b>Pointer state machine</b> (left button):
/// <list type="number">
/// <item><b>Press</b> on a cell: the cell toggles at once and <see cref="paintState"/> remembers
/// the new state ("on" or "off"). With Shift held, the press goes straight to velocity mode instead.</item>
/// <item><b>Velocity takeover</b>: while the pointer is still over the pressed cell, the first
/// clearly vertical movement (more than 5 px and 1.5× more vertical than horizontal) cancels the
/// toggle-painting and switches to velocity mode on that cell, starting from its original velocity
/// (so a press that had switched a step off restores and edits it).</item>
/// <item><b>Paint</b>: otherwise, moving sideways paints the same state along the <i>pressed row</i>
/// (the row never changes mid-gesture), filling every step between two pointer events.</item>
/// <item><b>Velocity mode</b>: velocity = start value + 1.2 per pixel moved upwards, clamped 1…127.</item>
/// <item><b>Release</b>: every gesture field is reset.</item>
/// </list>
/// Right-click toggles the accent and the wheel nudges the velocity of an enabled step by ±6
/// (one undo checkpoint per wheel burst); neither captures the pointer. Losing the pointer capture
/// ends a gesture like a release does.
/// </para>
/// </remarks>
public sealed class StepGridView : Control
{
    /// <summary>Height of a track row (the window aligns the row headers to it).</summary>
    public const double RowHeight = 40;

    /// <summary>Height of the step-number header above the rows.</summary>
    public const double HeaderHeight = 22;

    /// <summary>Background behind the cells.</summary>
    static readonly IBrush Back = AppTheme.Brush("Bg");
    /// <summary>Cell colour for odd-numbered beats (steps 1–4, 9–12, …).</summary>
    static readonly IBrush CellA = AppTheme.Brush("Surface");
    /// <summary>Cell colour for even-numbered beats, so beats alternate in shade.</summary>
    static readonly IBrush CellB = AppTheme.Brush("Surface2");
    /// <summary>Velocity bar of an enabled step.</summary>
    static readonly IBrush On = AppTheme.Brush("Accent");
    /// <summary>Velocity bar of an accented step (velocity ≥ 115) and the hover outline.</summary>
    static readonly IBrush Accent = AppTheme.Brush("AccentHi");
    /// <summary>Translucent overlay on the column being heard.</summary>
    static readonly IBrush Now = AppTheme.Brush("Hot_55");
    /// <summary>Step-number colour of the column being heard.</summary>
    static readonly IBrush NowHeader = AppTheme.Brush("Hot");
    /// <summary>Beat numbers in the header.</summary>
    static readonly IBrush Dim = AppTheme.Brush("TextDim");
    /// <summary>The "·" between beat numbers in the header.</summary>
    static readonly IBrush Faint = AppTheme.Brush("TextFaint");
    /// <summary>Outline of every cell.</summary>
    static readonly IPen CellEdge = new Pen(AppTheme.Brush("Line"), 1);
    /// <summary>Brighter outline of enabled cells.</summary>
    static readonly IPen OnEdge = new Pen(AppTheme.Brush("AccentLight"), 1);
    /// <summary>Font of the header numbers (Windows 11 UI font with fallbacks).</summary>
    static readonly Typeface Face = new("Segoe UI Variable Text, Segoe UI, sans-serif");

    /// <summary>Backing field of <see cref="Pattern"/>; edited in place.</summary>
    StepPattern pattern = new();
    /// <summary>Backing field of <see cref="CurrentStep"/>.</summary>
    int currentStep = -1;
    /// <summary>
    /// Paint gesture in progress: <c>true</c> = switching steps on, <c>false</c> = switching them off,
    /// <c>null</c> = not painting (idle, or the gesture became a velocity drag).
    /// </summary>
    bool? paintState;          // painting on/off while sweeping
    /// <summary>Row being painted (the pressed row; fixed for the whole sweep), −1 when idle.</summary>
    int paintRow = -1;
    /// <summary>Step reached by the previous paint event; the next event fills from here to the pointer.</summary>
    int lastPaintStep;
    /// <summary>Cell whose velocity is being dragged, or <c>null</c> when not in velocity mode.</summary>
    (int Row, int Step)? velocityCell;
    /// <summary>Pointer Y (px) where the velocity drag is anchored.</summary>
    double velocityStartY;
    /// <summary>Velocity of the cell at the anchor; the drag offset is added to it.</summary>
    int velocityStartValue;
    /// <summary>Cell under the mouse (outlined), or <c>null</c>.</summary>
    (int Row, int Step)? hover;
    /// <summary>Cell where the current left-button paint gesture started (candidate for the velocity takeover).</summary>
    (int Row, int Step)? pressCell;
    /// <summary>Pointer position at the press, used to classify the first movement as vertical or horizontal.</summary>
    Point pressPoint;
    /// <summary>Velocity the pressed cell had before the press toggled it (0 = it was off).</summary>
    int pressOriginal;
    /// <summary>Cell edited by the previous wheel notch, or <c>null</c> (reset by any press).</summary>
    (int Row, int Step)? lastWheelCell;
    /// <summary><see cref="Environment.TickCount64"/> of the previous wheel notch (ms).</summary>
    long lastWheelTicks;

    /// <summary>Clips drawing to the bounds by default (the playhead overlay and hover outline could otherwise spill out).</summary>
    static StepGridView()
    {
        ClipToBoundsProperty.OverrideDefaultValue<StepGridView>(true);
    }

    /// <summary>Creates the view.</summary>
    public StepGridView()
    {
    }

    /// <summary>
    /// Subscribes to theme changes. The theme brushes recolour themselves, but this control is
    /// only redrawn on invalidation, so it must repaint explicitly when the palette switches.
    /// </summary>
    /// <param name="e">Attachment event.</param>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AppTheme.Changed += InvalidateVisual;
    }

    /// <summary>Unsubscribes from <see cref="AppTheme.Changed"/>.</summary>
    /// <param name="e">Attachment event.</param>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // The theme is static: unsubscribe so a closed window can be collected.
        AppTheme.Changed -= InvalidateVisual;
    }

    /// <summary>The edited pattern.</summary>
    /// <remarks>Setting it re-measures (the height depends on the track count) and repaints. The instance is edited in place, not copied.</remarks>
    public StepPattern Pattern
    {
        get => pattern;
        set { pattern = value; InvalidateMeasure(); InvalidateVisual(); }
    }

    /// <summary>Step being heard (highlighted), or −1.</summary>
    public int CurrentStep
    {
        get => currentStep;
        set
        {
            if (currentStep == value) return;
            currentStep = value;
            InvalidateVisual();
        }
    }

    /// <summary>Raised after every edit of the steps.</summary>
    /// <remarks>Fires for every pointer event of a drag (not once per gesture), so handlers should be cheap.</remarks>
    public event Action? Edited;

    /// <summary>Raised when a gesture that will change steps begins (the window saves an undo point).</summary>
    public event Action? EditStarting;

    /// <summary>Raised when a step is switched on (row, step), so the window can audition it.</summary>
    /// <remarks>Only for the pressed cell of a left-click; cells turned on by a sweep, Shift+press or right-click are not auditioned.</remarks>
    public event Action<int, int>? StepSwitchedOn;

    /// <summary>Raised with a status text about the cell under the mouse.</summary>
    public event Action<string>? Hover;

    /// <summary>Width of one cell for the current size and step count.</summary>
    double CellWidth => Math.Max(10, Bounds.Width / Math.Max(1, pattern.StepCount));

    /// <summary>Takes the offered width (capped at 2000 px for unconstrained parents) and exactly the height of the header plus one row per track.</summary>
    /// <param name="availableSize">Space offered by the parent.</param>
    /// <returns>The desired size.</returns>
    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Min(availableSize.Width, 2000), HeaderHeight + pattern.Tracks.Count * RowHeight);

    /// <summary>Hit test: the cell under a point.</summary>
    /// <param name="p">Point in control coordinates (px).</param>
    /// <returns>(row, step), or <c>null</c> over the header, left of the grid or past the last row/step.</returns>
    (int Row, int Step)? CellAt(Point p)
    {
        if (p.Y < HeaderHeight || p.X < 0) return null;
        int row = (int)((p.Y - HeaderHeight) / RowHeight);
        int step = (int)(p.X / CellWidth);
        if (row < 0 || row >= pattern.Tracks.Count || step < 0 || step >= pattern.StepCount) return null;
        return (row, step);
    }

    /// <summary>Drawn rectangle of a cell: its slot inset by 3 px vertically and by a right-hand gap.</summary>
    /// <param name="row">Track index.</param>
    /// <param name="step">Step index.</param>
    /// <returns>The cell rectangle in control coordinates.</returns>
    Rect CellRect(int row, int step)
    {
        double w = CellWidth;
        // Wider gap after the last sixteenth of each beat so beats read as groups of four.
        double gap = step % 4 == 3 ? 5 : 2;
        return new Rect(step * w + 1, HeaderHeight + row * RowHeight + 3, w - gap - 1, RowHeight - 6);
    }

    /// <summary>
    /// Draws the header (beat numbers), then every row (inaudible tracks — muted, or not soloed
    /// while another track is — at 38 % opacity), then the playhead column on top.
    /// </summary>
    /// <param name="ctx">Avalonia drawing context.</param>
    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Back, new Rect(Bounds.Size));
        double w = CellWidth;
        // Same rule as StepPattern.IsAudible, evaluated once instead of per track.
        bool anySolo = pattern.Tracks.Any(t => t.Solo);

        // Step numbers (beat numbers on the first step of each beat).
        for (int s = 0; s < pattern.StepCount; s++)
        {
            bool beat = s % 4 == 0;
            var brush = s == currentStep ? NowHeader : beat ? Dim : Faint;
            string label = beat ? (s / 4 + 1).ToString(CultureInfo.InvariantCulture) : "·";
            var t = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, beat ? 11 : 10, brush);
            ctx.DrawText(t, new Point(s * w + (w - t.Width) / 2 - 1, (HeaderHeight - t.Height) / 2));
        }

        for (int r = 0; r < pattern.Tracks.Count; r++)
        {
            var track = pattern.Tracks[r];
            bool audible = !track.Mute && (!anySolo || track.Solo);
            using (ctx.PushOpacity(audible ? 1 : 0.38))
            {
                for (int s = 0; s < pattern.StepCount; s++)
                {
                    var rect = CellRect(r, s);
                    ctx.DrawRectangle((s / 4) % 2 == 0 ? CellA : CellB, CellEdge, rect, 4, 4);
                    int vel = track.Steps[s];
                    if (vel > 0)
                    {
                        // Bar height = velocity; accents (≥ 115) in the highlight colour.
                        double h = Math.Max(5, (rect.Height - 2) * vel / 127.0);
                        var bar = new Rect(rect.X + 1, rect.Bottom - 1 - h, rect.Width - 2, h);
                        ctx.DrawRectangle(vel >= 115 ? Accent : On, null, bar, 3, 3);
                        ctx.DrawRectangle(null, OnEdge, rect, 4, 4);
                    }
                    if (hover is { } hv && hv.Row == r && hv.Step == s)
                        ctx.DrawRectangle(null, new Pen(Accent, 1.5), rect.Inflate(1), 5, 5);
                }
            }
        }

        if (currentStep >= 0 && currentStep < pattern.StepCount)
            ctx.FillRectangle(Now, new Rect(currentStep * w, HeaderHeight, w - 2, pattern.Tracks.Count * RowHeight));
    }

    /// <summary>
    /// Starts a gesture: right button = toggle accent; left button = toggle the cell and begin a
    /// paint sweep (or, with Shift, begin a velocity drag). See the class remarks for the state machine.
    /// </summary>
    /// <param name="e">Pointer event.</param>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        if (CellAt(p) is not { } cell) return;
        var track = pattern.Tracks[cell.Row];
        var props = e.GetCurrentPoint(this).Properties;
        // A click ends any wheel burst, so the next wheel edit gets its own undo step.
        lastWheelCell = null;
        // Undo checkpoint before anything is modified.
        if (props.IsLeftButtonPressed || props.IsRightButtonPressed)
            EditStarting?.Invoke();
        if (props.IsRightButtonPressed)
        {
            // Accent toggle: an accented step (≥ 115) drops back to the track velocity, capped at
            // 114 so it does not still count as an accent; anything else (including off) → 127.
            int v = track.Steps[cell.Step];
            track.Steps[cell.Step] = v >= 115 ? Math.Min(track.Velocity, 114) : 127;
            Changed(cell);
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        // Capture so moves/release keep arriving while the drag leaves the control.
        e.Pointer.Capture(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Shift+press: velocity drag right away (an off step is switched on first).
            if (track.Steps[cell.Step] == 0) track.Steps[cell.Step] = track.Velocity;
            velocityCell = cell;
            velocityStartY = p.Y;
            velocityStartValue = track.Steps[cell.Step];
            Changed(cell);
            return;
        }
        // Plain press: toggle now and remember the gesture so OnPointerMoved can either keep
        // painting this state along the row or convert it into a velocity drag.
        pressCell = cell;
        pressPoint = p;
        pressOriginal = track.Steps[cell.Step];
        paintState = track.Steps[cell.Step] == 0;
        paintRow = cell.Row;
        lastPaintStep = cell.Step;
        track.Steps[cell.Step] = paintState.Value ? track.Velocity : 0;
        if (paintState.Value) StepSwitchedOn?.Invoke(cell.Row, cell.Step);
        Changed(cell);
    }

    /// <summary>
    /// Advances the gesture in progress (velocity drag, velocity takeover, paint sweep) or, with no
    /// gesture, updates the hover outline and the status text.
    /// </summary>
    /// <param name="e">Pointer event.</param>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (velocityCell is { } vc)
        {
            // Velocity drag: absolute offset from the anchor (not incremental), 1.2 velocity units
            // per pixel, up = louder (screen Y decreases upwards). Never reaches 0 (= off).
            var t = pattern.Tracks[vc.Row];
            t.Steps[vc.Step] = Math.Clamp(velocityStartValue + (int)Math.Round((velocityStartY - p.Y) * 1.2), 1, 127);
            Changed(vc);
            return;
        }
        var cell = CellAt(p);
        // Holding the button on the pressed cell and moving up/down sets that step's velocity
        // (instead of painting): up = louder. Decided by the first clear vertical movement.
        if (paintState is not null && pressCell is { } pc && lastPaintStep == pc.Step)
        {
            double dx = p.X - pressPoint.X, dy = p.Y - pressPoint.Y;
            if (Math.Abs(dy) > 5 && Math.Abs(dy) > Math.Abs(dx) * 1.5)
            {
                var t = pattern.Tracks[pc.Row];
                // A press that switched an existing step off becomes an edit of that step instead.
                int start = pressOriginal > 0 ? pressOriginal : t.Velocity;
                t.Steps[pc.Step] = start;
                paintState = null;
                velocityCell = pc;
                velocityStartY = pressPoint.Y;
                velocityStartValue = start;
                // Re-enter once: now in velocity mode, so the current offset is applied immediately.
                OnPointerMoved(e);
                return;
            }
        }
        if (paintState is { } state)
        {
            // Paint every cell of the row between the last painted step and the pointer (a fast
            // sweep may skip cells between two mouse events). The row stays the one pressed.
            int step = Math.Clamp((int)(p.X / CellWidth), 0, pattern.StepCount - 1);
            var t = pattern.Tracks[paintRow];
            int a = Math.Min(lastPaintStep, step), b = Math.Max(lastPaintStep, step);
            bool changed = false;
            for (int s = a; s <= b; s++)
            {
                // Painting "on" keeps the velocity of steps that are already on.
                int want = state ? (t.Steps[s] > 0 ? t.Steps[s] : t.Velocity) : 0;
                if (t.Steps[s] == want) continue;
                t.Steps[s] = want;
                changed = true;
            }
            lastPaintStep = step;
            if (changed) Changed((paintRow, step));
            return;
        }
        // Idle: hover feedback. The status text is only updated when entering a cell, so the
        // window keeps showing the last cell's description when the pointer leaves the grid.
        if (cell != hover)
        {
            hover = cell;
            InvalidateVisual();
            if (cell is { } h)
                Hover?.Invoke(Describe(h));
        }
    }

    /// <summary>Ends any gesture and releases the pointer capture.</summary>
    /// <param name="e">Pointer event.</param>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndGesture();
        e.Pointer.Capture(null);
    }

    /// <summary>
    /// Bug fix: when the pointer capture is taken away without a release (a popup or another
    /// control grabs the pointer, the window loses capture…), the paint or velocity mode used to
    /// stay active, so plain mouse moves kept painting cells or changing a velocity. The gesture
    /// now ends here exactly as on a release.
    /// </summary>
    /// <param name="e">Capture-lost event.</param>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndGesture();
    }

    /// <summary>Resets every gesture field (paint sweep, velocity drag, pressed cell) to idle.</summary>
    void EndGesture()
    {
        paintState = null;
        velocityCell = null;
        pressCell = null;
        paintRow = -1;
    }

    /// <summary>Removes the hover outline when the mouse leaves the control.</summary>
    /// <param name="e">Pointer event.</param>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (hover is not null) { hover = null; InvalidateVisual(); }
    }

    /// <summary>
    /// Wheel over an enabled step: velocity ±6 per notch (1…127) and the event is consumed. Over an
    /// off step or outside the cells the event bubbles so the surrounding ScrollViewer scrolls.
    /// </summary>
    /// <param name="e">Wheel event.</param>
    /// <remarks>
    /// <para>
    /// Bug fix 1: a purely horizontal wheel/touchpad event (<c>Delta.Y == 0</c>) used to count as
    /// "down" (−6); it is now ignored and bubbles to the scroll viewer.
    /// </para>
    /// <para>
    /// Bug fix 2: wheel edits used to change the pattern without <see cref="EditStarting"/>, so they
    /// could not be undone on their own. Now an undo checkpoint is taken at the start of each wheel
    /// burst: the first notch on a cell, or a notch after more than a second of wheel silence.
    /// A continuous spin on one cell is therefore one undo step, not one per notch.
    /// </para>
    /// </remarks>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0) return; // horizontal-only: not a velocity gesture
        if (CellAt(e.GetPosition(this)) is not { } cell) return;
        var t = pattern.Tracks[cell.Row];
        if (t.Steps[cell.Step] == 0) return; // let the scroll viewer scroll
        long now = Environment.TickCount64;
        if (lastWheelCell != cell || now - lastWheelTicks > 1000)
            EditStarting?.Invoke();
        lastWheelCell = cell;
        lastWheelTicks = now;
        t.Steps[cell.Step] = Math.Clamp(t.Steps[cell.Step] + (e.Delta.Y > 0 ? 6 : -6), 1, 127);
        Changed(cell);
        e.Handled = true;
    }

    /// <summary>Common tail of every edit: repaint, refresh the status text for the cell, raise <see cref="Edited"/>.</summary>
    /// <param name="cell">The cell that changed (or the last cell of a sweep).</param>
    void Changed((int Row, int Step) cell)
    {
        InvalidateVisual();
        Hover?.Invoke(Describe(cell));
        Edited?.Invoke();
    }

    /// <summary>
    /// Status-line text for a cell: track name, step number and beat.sixteenth position, its state
    /// (off / velocity, accent), followed by a short reminder of the mouse gestures. Bilingual via
    /// <see cref="Loc.S"/>.
    /// </summary>
    /// <param name="c">The cell.</param>
    /// <returns>The localised description.</returns>
    string Describe((int Row, int Step) c)
    {
        var t = pattern.Tracks[c.Row];
        int v = t.Steps[c.Step];
        string pos = $"{c.Step / 4 + 1}.{c.Step % 4 + 1}";
        string state = v == 0 ? Loc.S("apagado", "off") : Loc.S($"velocidad {v}", $"velocity {v}") + (v >= 115 ? Loc.S(" (acento)", " (accent)") : "");
        return $"{t.Name} · {Loc.S("paso", "step")} {c.Step + 1} ({pos}) · {state}   —   " +
               Loc.S("clic = encender/apagar · mantener y mover arriba/abajo (o rueda) = velocidad · clic derecho = acento",
                     "click = on/off · hold and move up/down (or wheel) = velocity · right-click = accent");
    }
}
