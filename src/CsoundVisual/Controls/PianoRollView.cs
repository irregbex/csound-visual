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
/// The piano roll editor surface: piano keys on the left, a bar/beat ruler on top, the note grid
/// and a velocity lane at the bottom, all drawn by this one control (fast with thousands of notes
/// and no visual tree per note).
/// </summary>
/// <remarks>
/// <para><b>Mouse</b> (Cubase-like): click on empty grid = new note (drag right while holding to
/// set its length, move up/down while holding to set its velocity); drag a note = move (all
/// selected notes move together; Alt+drag copies); drag the right edge = resize; right-click on a
/// note or double-click = delete (the whole selection when the note is selected); right-drag on
/// empty grid = box selection (Shift/Ctrl add to the selection), right-click on empty grid without
/// moving = deselect; Ctrl+click on a note = add/remove it from the selection; Ctrl+drag on empty
/// grid = box selection too; middle-drag = pan.</para>
/// <para><b>Ruler</b>: click = set the start position (green line); drag = set the loop range.
/// <b>Keys</b>: click = audition the note. <b>Velocity lane</b>: drag across the bars to paint
/// velocities (only the selected notes when there is a selection).</para>
/// <para><b>Wheel</b>: scroll pitch; Shift = scroll time; Ctrl = zoom time around the mouse;
/// Ctrl+Shift (or Alt) = zoom pitch.</para>
/// <para><b>Keys</b> (when focused): Delete, Ctrl+A, Ctrl+C/V, Ctrl+D (duplicate), Ctrl+Z/Y,
/// arrows (move by grid / semitone; Shift+↑↓ = octave), Q (quantize), Esc (deselect).</para>
/// <para>Positions are in beats; the window converts to seconds only when sending to Csound.</para>
/// <para>
/// <b>Layout</b> (top to bottom): ruler (<see cref="RulerHeight"/>), note grid (whatever height is
/// left, <see cref="GridHeight"/>), a <see cref="LaneGap"/> gap, the velocity lane
/// (<see cref="LaneHeight"/>). The piano keys occupy the left <see cref="KeyWidth"/> pixels beside
/// the grid. There is no Avalonia <c>ScrollViewer</c>: scrolling and zoom are this control's own
/// state (<see cref="ScrollX"/>, <see cref="ScrollY"/>, <see cref="PixelsPerBeat"/>,
/// <see cref="RowHeight"/>); the window mirrors them in separate scroll bars through
/// <see cref="ViewChanged"/>.
/// </para>
/// <para>
/// <b>Units and transforms.</b> Time is in beats (quarter notes), pitch in MIDI notes 0…127 with
/// 127 at the top. <c>x = KeyWidth + (beat − scrollX) · pixelsPerBeat</c> and
/// <c>y = RulerHeight + (127 − pitch) · rowHeight − scrollY</c> (see <see cref="XOf"/>,
/// <see cref="YOf"/> and their inverses <see cref="BeatOf"/>, <see cref="PitchOf"/>). Bars are
/// assumed to be 4/4 (four beats) for the grid lines and the ruler numbers.
/// </para>
/// <para>
/// <b>Model.</b> All note data lives in a <see cref="PianoRollModel"/> owned by the view model
/// (<c>MainViewModel.MusicTools</c>), so closing the window keeps the notes. The control mutates it
/// directly on the UI thread, calls <see cref="PianoRollModel.Checkpoint"/> once per gesture for
/// undo, and raises <see cref="Edited"/> so the window can hand a fresh snapshot to the scheduler.
/// It repaints whenever the model raises <see cref="PianoRollModel.Changed"/>.
/// </para>
/// <para>
/// <b>Pointer state machine.</b> A press picks a <see cref="DragMode"/> from the zone and buttons;
/// moves are interpreted per mode against the press position and the notes' original values saved
/// in <see cref="dragOrigin"/> (so every move is computed from the start of the gesture, never
/// accumulated, which avoids drift and lets snapping work); the release commits (undo checkpoint
/// cleanup, <see cref="Edited"/>) and returns to <see cref="DragMode.None"/>. The pointer is captured
/// during every drag so it keeps working outside the control.
/// </para>
/// </remarks>
public sealed class PianoRollView : Control
{
    /// <summary>Width of the piano keys column.</summary>
    public const double KeyWidth = 62;

    /// <summary>Height of the ruler.</summary>
    public const double RulerHeight = 26;

    /// <summary>Height of the velocity lane.</summary>
    public const double LaneHeight = 78;

    /// <summary>Vertical space (px) between the bottom of the note grid and the velocity lane.</summary>
    const double LaneGap = 6;
    /// <summary>Width (px) of the zone at the right end of a note that grabs it for resizing instead of moving.</summary>
    const double EdgeGrip = 6;

    /// <summary>Background of the whole control.</summary>
    static readonly IBrush BgBrush = AppTheme.Brush("Bg");
    /// <summary>Darker row background for black-key pitches.</summary>
    static readonly IBrush RowDark = AppTheme.Brush("BgDeep");
    /// <summary>Velocity lane and top-left corner background.</summary>
    static readonly IBrush SurfaceBrush = AppTheme.Brush("Surface");
    /// <summary>Ruler background.</summary>
    static readonly IBrush Surface2Brush = AppTheme.Brush("Surface2");
    /// <summary>White piano key.</summary>
    static readonly IBrush KeyWhite = AppTheme.Brush("PianoWhite");
    /// <summary>Black piano key (also the colour of the C labels on the keys).</summary>
    static readonly IBrush KeyBlack = AppTheme.Brush("BgDeep");
    /// <summary>Unselected note, velocity bar, auditioned key and focus border.</summary>
    static readonly IBrush NoteFill = AppTheme.Brush("Accent");
    /// <summary>Selected note and its velocity bar.</summary>
    static readonly IBrush NoteSelected = AppTheme.Brush("AccentHi");
    /// <summary>Note-name text drawn inside wide notes.</summary>
    static readonly IBrush NoteText = AppTheme.Brush("OnAccent");
    /// <summary>Bar numbers in the ruler.</summary>
    static readonly IBrush TextDim = AppTheme.Brush("TextDim");
    /// <summary>Beat labels in the ruler and the "bar"/"velocity" captions.</summary>
    static readonly IBrush TextFaint = AppTheme.Brush("TextFaint");
    /// <summary>Loop range band in the ruler.</summary>
    static readonly IBrush LoopFill = AppTheme.Brush("Accent_40");
    /// <summary>Faint loop range shading across the note grid.</summary>
    static readonly IBrush LoopShade = AppTheme.Brush("AccentHi_10");
    /// <summary>Playhead triangle in the ruler.</summary>
    static readonly IBrush PlayheadBrush = AppTheme.Brush("Hot");
    /// <summary>Translucent fill of the selection box.</summary>
    static readonly IBrush BoxFill = AppTheme.Brush("AccentHi_22");
    /// <summary>Key highlighted under the mouse.</summary>
    static readonly IBrush HoverKey = AppTheme.Brush("Accent_66");
    /// <summary>Sub-beat grid lines (at the snap resolution).</summary>
    static readonly IPen GridPen = new Pen(AppTheme.Brush("Grid"), 1);
    /// <summary>Beat lines.</summary>
    static readonly IPen BeatPen = new Pen(AppTheme.Brush("Line"), 1);
    /// <summary>Bar lines (every 4 beats), the most visible.</summary>
    static readonly IPen BarPen = new Pen(AppTheme.Brush("TextFaint"), 1);
    /// <summary>Horizontal line under every C (octave boundary).</summary>
    static readonly IPen OctavePen = new Pen(AppTheme.Brush("Line"), 1);
    /// <summary>Outline of unselected notes.</summary>
    static readonly IPen NoteEdge = new Pen(AppTheme.Brush("AccentDeep"), 1);
    /// <summary>Outline of selected notes.</summary>
    static readonly IPen NoteEdgeSelected = new Pen(AppTheme.Brush("Text"), 1.5);
    /// <summary>Outline of the piano keys.</summary>
    static readonly IPen KeyEdge = new Pen(AppTheme.Brush("Line"), 1);
    /// <summary>Dashed outline of the selection box.</summary>
    static readonly IPen BoxPen = new Pen(AppTheme.Brush("AccentHi"), 1, new DashStyle([3, 3], 0));
    /// <summary>Playhead line.</summary>
    static readonly IPen PlayheadPen = new Pen(AppTheme.Brush("Hot"), 1.5);
    /// <summary>Dashed green start-position line.</summary>
    static readonly IPen CursorPen = new Pen(AppTheme.Brush("Good"), 1.2, new DashStyle([4, 3], 0));
    /// <summary>Font of all texts drawn by the control.</summary>
    static readonly Typeface Face = new("Segoe UI Variable Text, Segoe UI, sans-serif");

    /// <summary>What the current pointer gesture does; chosen in <see cref="OnPointerPressed"/>.</summary>
    enum DragMode
    {
        /// <summary>No gesture: moves only update hover feedback.</summary>
        None,
        /// <summary>Dragging the selected notes in time and pitch.</summary>
        Move,
        /// <summary>Dragging the right edge of the selected notes (length).</summary>
        Resize,
        /// <summary>A note was just created: horizontal = length, vertical = velocity.</summary>
        Create,
        /// <summary>Rubber-band selection rectangle.</summary>
        Box,
        /// <summary>Painting velocities in the lane.</summary>
        Velocity,
        /// <summary>Pressed in the ruler: click = start position, drag = loop range.</summary>
        Loop,
        /// <summary>Middle-button panning.</summary>
        Pan,
        /// <summary>Holding a piano key (auditioning; sliding plays other keys).</summary>
        Key,
    }

    /// <summary>Backing field of <see cref="Model"/>.</summary>
    PianoRollModel model = new();
    /// <summary>Horizontal zoom, pixels per beat (backing field of <see cref="PixelsPerBeat"/>).</summary>
    double pixelsPerBeat = 80;
    /// <summary>Vertical zoom, pixels per semitone row (backing field of <see cref="RowHeight"/>).</summary>
    double rowHeight = 14;
    /// <summary>Backing field of <see cref="ScrollX"/>.</summary>
    double scrollX;            // beats at the left edge of the grid
    /// <summary>Backing field of <see cref="ScrollY"/>.</summary>
    double scrollY;            // pixels from the top of pitch 127
    /// <summary>Backing field of <see cref="Playhead"/>.</summary>
    double? playhead;
    /// <summary>Start position in beats (backing field of <see cref="StartBeat"/>); also where Ctrl+V pastes.</summary>
    double cursor;

    /// <summary>The gesture in progress.</summary>
    DragMode drag;

    /// <summary>
    /// True after an Alt+press on a note until the pointer actually moves: the selection is only
    /// duplicated (by <see cref="CommitAltCopy"/>) once the drag really starts.
    /// </summary>
    /// <remarks>
    /// Bug fix: the copies used to be made on the press itself, so every Alt+click without
    /// dragging stacked an invisible duplicate exactly on top of each selected note.
    /// </remarks>
    bool pendingAltCopy;

    /// <summary>Velocity the note being created started with (vertical drag adds to it).</summary>
    int createVelocity;
    /// <summary>Pointer position at the press (px); drags measure their offsets from it.</summary>
    Point pressPoint;
    /// <summary>Beat under the pointer at the press (unsnapped).</summary>
    double pressBeat;
    /// <summary>Pitch row under the pointer at the press.</summary>
    int pressPitch;
    /// <summary>The note grabbed by a Move/Resize/Create gesture (it is the one that snaps; the rest follow).</summary>
    RollNote? dragNote;
    /// <summary>Start, length and pitch of every note involved in the drag, as they were at the press.</summary>
    Dictionary<RollNote, (double Start, double Length, int Pitch)> dragOrigin = [];
    /// <summary>Current selection rectangle in control coordinates (px), while <see cref="DragMode.Box"/>.</summary>
    Rect box;
    /// <summary>Previous pointer position of a pan drag (pan is incremental, unlike the other drags).</summary>
    Point lastPan;
    /// <summary>Pitch row under the mouse, highlighted on the keys; <c>null</c> outside the grid and keys.</summary>
    int? hoverPitch;
    /// <summary>Key being held down in <see cref="DragMode.Key"/> (drawn pressed), or <c>null</c>.</summary>
    int? auditionPitch;

    /// <summary>Makes the control focusable (for the editing shortcuts) and clipped to its bounds by default.</summary>
    static PianoRollView()
    {
        FocusableProperty.OverrideDefaultValue<PianoRollView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<PianoRollView>(true);
    }

    /// <summary>Creates the view.</summary>
    public PianoRollView()
    {
    }

    /// <summary>
    /// Subscribes to theme changes: the brushes recolour themselves, but this control only redraws
    /// on invalidation, so it must repaint explicitly when the palette switches.
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

    /// <summary>The edited model.</summary>
    /// <remarks>
    /// Swapping the model moves the <see cref="PianoRollModel.Changed"/> subscription to the new
    /// instance. The model outlives the window (it belongs to the view model), and the view stays
    /// subscribed to it after the window closes; that only keeps the closed view alive until the
    /// next model swap.
    /// </remarks>
    public PianoRollModel Model
    {
        get => model;
        set
        {
            model.Changed -= OnModelChanged;
            model = value;
            model.Changed += OnModelChanged;
            InvalidateVisual();
        }
    }

    /// <summary>Repaints whenever the model reports a change (edits, undo/redo, selection, loop).</summary>
    void OnModelChanged() => InvalidateVisual();

    /// <summary>Raised when the user edited notes (the window refreshes the scheduler snapshot and marks the roll as modified).</summary>
    public event Action? Edited;

    /// <summary>Raised to audition a note (key click, new note): (pitch, velocity 1…127).</summary>
    /// <remarks>There is no matching "stop": the window plays a short fixed-length Csound event (0.35 s) on the roll's instrument.</remarks>
    public event Action<int, int>? Audition;

    /// <summary>Raised with a status text describing what is under the mouse.</summary>
    public event Action<string>? Hover;

    /// <summary>Raised when scrolling or zoom changed (the window syncs its scroll bars).</summary>
    public event Action? ViewChanged;

    /// <summary>Raised when the start position (cursor) or the loop range was changed in the ruler.</summary>
    public event Action? TransportEdited;

    // The four view properties below clamp, keep the scroll position legal, repaint and raise
    // ViewChanged on every set (so the window's scroll bars follow mouse-driven zoom/scroll too).

    /// <summary>Horizontal zoom, pixels per beat (8…600).</summary>
    public double PixelsPerBeat
    {
        get => pixelsPerBeat;
        set { pixelsPerBeat = Math.Clamp(value, 8, 600); ClampScroll(); InvalidateVisual(); ViewChanged?.Invoke(); }
    }

    /// <summary>Vertical zoom, pixels per semitone (6…40).</summary>
    public double RowHeight
    {
        get => rowHeight;
        set { rowHeight = Math.Clamp(value, 6, 40); ClampScroll(); InvalidateVisual(); ViewChanged?.Invoke(); }
    }

    /// <summary>Beat shown at the left edge of the grid.</summary>
    public double ScrollX
    {
        get => scrollX;
        set { scrollX = value; ClampScroll(); InvalidateVisual(); ViewChanged?.Invoke(); }
    }

    /// <summary>Vertical scroll, pixels from the top (pitch 127).</summary>
    public double ScrollY
    {
        get => scrollY;
        set { scrollY = value; ClampScroll(); InvalidateVisual(); ViewChanged?.Invoke(); }
    }

    /// <summary>Playhead position (beats), or <c>null</c> when stopped.</summary>
    public double? Playhead
    {
        get => playhead;
        set
        {
            if (playhead == value) return;
            playhead = value;
            InvalidateVisual();
        }
    }

    /// <summary>Start position for playback and paste (beats), the green line.</summary>
    public double StartBeat
    {
        get => cursor;
        set { cursor = Math.Max(0, value); InvalidateVisual(); }
    }

    /// <summary>Total timeline length the scroll bar covers, beats.</summary>
    /// <remarks>The furthest of the last note end, the loop end and the start cursor, plus 32 beats (8 bars) of empty room to draw into.</remarks>
    public double ContentBeats => Math.Max(Math.Max(model.ContentEnd, model.LoopEnd), Math.Max(cursor, 0)) + 32;

    /// <summary>Visible time span, beats.</summary>
    public double ViewportBeats => Math.Max(1, (Bounds.Width - KeyWidth) / pixelsPerBeat);

    /// <summary>Total height of the 128 rows, pixels.</summary>
    public double ContentHeight => 128 * rowHeight;

    /// <summary>Visible height of the note grid, pixels.</summary>
    public double GridHeight => Math.Max(10, Bounds.Height - RulerHeight - LaneHeight - LaneGap);

    /// <summary>
    /// Keeps the scroll position legal: horizontally from 0 up to the point where the content end
    /// sits mid-view (half a viewport of empty space may show on the right); vertically within
    /// the 128 rows (no empty space below pitch 0).
    /// </summary>
    void ClampScroll()
    {
        scrollX = Math.Clamp(scrollX, 0, Math.Max(0, ContentBeats - ViewportBeats * 0.5));
        scrollY = Math.Clamp(scrollY, 0, Math.Max(0, ContentHeight - GridHeight));
    }

    /// <summary>Scrolls so that <paramref name="pitch"/> is in the middle of the grid.</summary>
    /// <param name="pitch">MIDI note to centre (the window passes the average pitch of the notes, or 66 when empty).</param>
    public void CenterOnPitch(int pitch)
    {
        scrollY = (127 - pitch) * rowHeight - GridHeight / 2;
        ClampScroll();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>Scrolls horizontally (only when needed) so the playhead stays visible while playing.</summary>
    public void FollowPlayhead()
    {
        if (playhead is not { } p) return;
        // Page-style follow: when the playhead leaves the view (or enters its last 8 %), jump so it
        // sits 5 % from the left edge, instead of scrolling continuously.
        if (p < scrollX || p > scrollX + ViewportBeats * 0.92)
            ScrollX = Math.Max(0, p - ViewportBeats * 0.05);
    }

    // ------------------------------------------------------------------------------------------
    //  Geometry
    // ------------------------------------------------------------------------------------------

    /// <summary>Y (px) of the top of the note grid (just below the ruler).</summary>
    double GridTop => RulerHeight;
    /// <summary>Y (px) of the bottom of the note grid.</summary>
    double GridBottom => RulerHeight + GridHeight;
    /// <summary>Y (px) of the top of the velocity lane.</summary>
    double LaneTop => GridBottom + LaneGap;
    /// <summary>Y (px) of the bottom of the velocity lane.</summary>
    double LaneBottom => LaneTop + LaneHeight;

    /// <summary>Beat → x pixel (control coordinates).</summary>
    /// <param name="beat">Position in beats.</param>
    /// <returns>X in pixels; values left of <see cref="KeyWidth"/> are scrolled out of view.</returns>
    double XOf(double beat) => KeyWidth + (beat - scrollX) * pixelsPerBeat;
    /// <summary>X pixel → beat (inverse of <see cref="XOf"/>, unsnapped, may be negative over the keys).</summary>
    /// <param name="x">X in pixels.</param>
    /// <returns>Position in beats.</returns>
    double BeatOf(double x) => scrollX + (x - KeyWidth) / pixelsPerBeat;
    /// <summary>Pitch → y pixel of the <i>top</i> of its row (pitch 127 is the first row).</summary>
    /// <param name="pitch">MIDI note.</param>
    /// <returns>Y in pixels.</returns>
    double YOf(int pitch) => GridTop + (127 - pitch) * rowHeight - scrollY;
    /// <summary>Y pixel → pitch of the row containing it, clamped to 0…127 (inverse of <see cref="YOf"/>).</summary>
    /// <param name="y">Y in pixels.</param>
    /// <returns>MIDI note.</returns>
    int PitchOf(double y) => Math.Clamp(127 - (int)Math.Floor((y - GridTop + scrollY) / rowHeight), 0, 127);

    /// <summary>On-screen rectangle of a note; at least 3 px wide so very short notes stay visible and clickable.</summary>
    /// <param name="n">The note.</param>
    /// <returns>The rectangle in control coordinates.</returns>
    Rect NoteRect(RollNote n) => new(XOf(n.Start), YOf(n.Pitch), Math.Max(3, n.Length * pixelsPerBeat), rowHeight);

    /// <summary>True when <paramref name="p"/> is over the note grid.</summary>
    bool InGrid(Point p) => p.X >= KeyWidth && p.Y >= GridTop && p.Y < GridBottom;
    /// <summary>True when <paramref name="p"/> is over the ruler (right of the keys column).</summary>
    bool InRuler(Point p) => p.X >= KeyWidth && p.Y < RulerHeight;
    /// <summary>True when <paramref name="p"/> is over the piano keys.</summary>
    bool InKeys(Point p) => p.X < KeyWidth && p.Y >= GridTop && p.Y < GridBottom;
    /// <summary>True when <paramref name="p"/> is over the velocity lane.</summary>
    bool InLane(Point p) => p.X >= KeyWidth && p.Y >= LaneTop && p.Y < LaneBottom;

    /// <summary>Topmost note under a point (the last drawn wins, like on screen).</summary>
    /// <param name="p">Point in control coordinates.</param>
    /// <returns>The note, or <c>null</c>. Linear scan from the end of the list (notes are drawn in list order).</returns>
    RollNote? NoteAt(Point p)
    {
        for (int i = model.Notes.Count - 1; i >= 0; i--)
            if (NoteRect(model.Notes[i]).Contains(p))
                return model.Notes[i];
        return null;
    }

    /// <summary>Grid spacing for drawing: the snap, made coarser when lines would be too dense.</summary>
    /// <returns>Line spacing in beats: the snap (a sixteenth when snap is off), doubled until lines are at least 9 px apart.</returns>
    double DrawStep()
    {
        double step = model.Snap > 0 ? model.Snap : 0.25;
        while (step * pixelsPerBeat < 9) step *= 2;
        return step;
    }

    // ------------------------------------------------------------------------------------------
    //  Drawing
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Draws everything in painter's order: grid rows, loop shading, vertical lines, notes and the
    /// selection box (clipped to the grid), then the ruler, the keys and the velocity lane, and
    /// finally the start cursor and the playhead across all of them, plus a focus border.
    /// </summary>
    /// <param name="ctx">Avalonia drawing context.</param>
    /// <remarks>
    /// Only the visible pitch rows and beats are iterated, and notes outside the view are skipped,
    /// so the cost depends on what is on screen rather than on the length of the piece (notes are
    /// still tested one by one).
    /// </remarks>
    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.FillRectangle(BgBrush, new Rect(size));
        var gridRect = new Rect(KeyWidth, GridTop, Math.Max(0, size.Width - KeyWidth), GridHeight);
        var laneRect = new Rect(KeyWidth, LaneTop, Math.Max(0, size.Width - KeyWidth), LaneHeight);
        // Visible time span in beats.
        double firstBeat = scrollX, lastBeat = BeatOf(size.Width);

        // Rows (black-key rows darker) and octave lines.
        using (ctx.PushClip(gridRect))
        {
            // Visible pitch range: `top` is the highest pitch (top row), `bottom` the lowest.
            int top = PitchOf(GridTop), bottom = PitchOf(GridBottom - 1);
            for (int p = bottom; p <= top; p++)
            {
                double y = YOf(p);
                if (PFields.IsBlack(p))
                    ctx.FillRectangle(RowDark, new Rect(KeyWidth, y, gridRect.Width, rowHeight));
                if (p % 12 == 0)
                    ctx.DrawLine(OctavePen, new Point(KeyWidth, y + rowHeight - 0.5), new Point(size.Width, y + rowHeight - 0.5));
            }

            // Loop region shading.
            if (model.LoopOn)
            {
                double lx0 = XOf(model.LoopStart), lx1 = XOf(model.LoopEnd);
                ctx.FillRectangle(LoopShade, new Rect(lx0, GridTop, Math.Max(0, lx1 - lx0), GridHeight));
            }

            // Vertical lines: grid steps, beats, bars (4/4).
            double step = DrawStep();
            for (double b = Math.Floor(firstBeat / step) * step; b <= lastBeat; b += step)
            {
                // Round to a whole pixel and add 0.5 so 1-px lines land on a pixel centre (crisp, not blurred over two columns).
                double x = Math.Round(XOf(b)) + 0.5;
                // Float tolerance: with triplet snaps (1/3) the accumulated b is never exactly integral.
                bool bar = Math.Abs(b / 4 - Math.Round(b / 4)) < 1e-6;
                bool beat = Math.Abs(b - Math.Round(b)) < 1e-6;
                ctx.DrawLine(bar ? BarPen : beat ? BeatPen : GridPen, new Point(x, GridTop), new Point(x, GridBottom));
            }

            // Notes.
            foreach (var n in model.Notes)
            {
                var r = NoteRect(n);
                if (r.Right < KeyWidth || r.X > size.Width || r.Bottom < GridTop || r.Y > GridBottom) continue;
                bool sel = model.Selected.Contains(n);
                var inner = r.Deflate(new Thickness(0.5, 1, 0.5, 1));
                // Unselected notes fade with velocity (45 % opacity at velocity 0, opaque at 127),
                // so dynamics are visible without looking at the lane; selected notes are always opaque.
                using (ctx.PushOpacity(sel ? 1 : 0.45 + 0.55 * n.Velocity / 127.0))
                    ctx.DrawRectangle(sel ? NoteSelected : NoteFill, sel ? NoteEdgeSelected : NoteEdge, inner, 3, 3);
                // Note name inside the note only when it is wide and tall enough to be legible.
                if (inner.Width > 34 && rowHeight >= 11)
                {
                    var t = Text(PFields.NoteName(n.Pitch), Math.Min(10.5, rowHeight - 3), NoteText);
                    using (ctx.PushClip(inner))
                        ctx.DrawText(t, new Point(inner.X + 4, inner.Y + (inner.Height - t.Height) / 2));
                }
            }

            if (drag == DragMode.Box)
                ctx.DrawRectangle(BoxFill, BoxPen, box);
        }

        DrawRuler(ctx, size, firstBeat, lastBeat);
        DrawKeys(ctx);
        DrawLane(ctx, laneRect);

        // Cursor (start position) and playhead over everything.
        double cx = XOf(cursor);
        if (cx >= KeyWidth && cx <= size.Width)
            ctx.DrawLine(CursorPen, new Point(cx, 0), new Point(cx, LaneBottom));
        if (playhead is { } ph)
        {
            double px = XOf(ph);
            if (px >= KeyWidth && px <= size.Width)
            {
                ctx.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, LaneBottom));
                // Downward triangle (12 px wide, 9 px tall) at the top of the ruler, like a DAW playhead.
                var tri = new StreamGeometry();
                using (var g = tri.Open())
                {
                    g.BeginFigure(new Point(px - 6, 0), true);
                    g.LineTo(new Point(px + 6, 0));
                    g.LineTo(new Point(px, 9));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(PlayheadBrush, null, tri);
            }
        }
        if (IsFocused)
            ctx.DrawRectangle(null, new Pen(NoteFill, 1), new Rect(size).Deflate(0.5));
    }

    /// <summary>Builds a text run in the control's font.</summary>
    /// <param name="s">Text.</param>
    /// <param name="size">Font size (px).</param>
    /// <param name="brush">Colour.</param>
    /// <returns>The formatted text, ready for <c>DrawingContext.DrawText</c>.</returns>
    static FormattedText Text(string s, double size, IBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush);

    /// <summary>
    /// Draws the ruler: loop band (also while it is being dragged), bar numbers with long ticks,
    /// beat ticks with "bar.beat" labels when zoomed in (&gt; 70 px per beat), and the localised
    /// "bar" caption in the corner above the keys.
    /// </summary>
    /// <param name="ctx">Drawing context.</param>
    /// <param name="size">Control size.</param>
    /// <param name="firstBeat">First visible beat.</param>
    /// <param name="lastBeat">Last visible beat.</param>
    void DrawRuler(DrawingContext ctx, Size size, double firstBeat, double lastBeat)
    {
        var ruler = new Rect(KeyWidth, 0, Math.Max(0, size.Width - KeyWidth), RulerHeight);
        ctx.FillRectangle(Surface2Brush, ruler);
        using (ctx.PushClip(ruler))
        {
            if (model.LoopOn || drag == DragMode.Loop)
            {
                double lx0 = XOf(model.LoopStart), lx1 = XOf(model.LoopEnd);
                ctx.FillRectangle(LoopFill, new Rect(lx0, 3, Math.Max(0, lx1 - lx0), RulerHeight - 10));
            }
            // Bar numbers; beats as ticks. Thin out the labels when zoomed far out.
            int every = pixelsPerBeat * 4 < 40 ? 4 : pixelsPerBeat * 4 < 80 ? 2 : 1;
            for (double b = Math.Floor(firstBeat); b <= lastBeat; b += 1)
            {
                double x = Math.Round(XOf(b)) + 0.5;
                bool bar = ((int)b) % 4 == 0;
                ctx.DrawLine(bar ? BarPen : BeatPen, new Point(x, bar ? 4 : RulerHeight - 7), new Point(x, RulerHeight));
                int barNo = (int)b / 4;
                if (bar && barNo % every == 0)
                    ctx.DrawText(Text((barNo + 1).ToString(CultureInfo.InvariantCulture), 11, TextDim), new Point(x + 4, 4));
                else if (!bar && pixelsPerBeat > 70)
                    ctx.DrawText(Text($"{barNo + 1}.{(int)b % 4 + 1}", 9, TextFaint), new Point(x + 3, 9));
            }
        }
        ctx.FillRectangle(SurfaceBrush, new Rect(0, 0, KeyWidth, RulerHeight));
        ctx.DrawText(Text(Loc.S("compás", "bar"), 10, TextFaint), new Point(8, 7));
    }

    /// <summary>
    /// Draws the piano keys column, one key per visible pitch row (black keys are 62 % of the column
    /// wide). The held key is shown in the note colour, the hovered key highlighted, and every C is
    /// labelled with its note name when the rows are at least 8 px tall.
    /// </summary>
    /// <param name="ctx">Drawing context.</param>
    void DrawKeys(DrawingContext ctx)
    {
        var keys = new Rect(0, GridTop, KeyWidth, GridHeight);
        using (ctx.PushClip(keys))
        {
            int top = PitchOf(GridTop), bottom = PitchOf(GridBottom - 1);
            for (int p = bottom; p <= top; p++)
            {
                double y = YOf(p);
                bool black = PFields.IsBlack(p);
                var r = new Rect(0, y, black ? KeyWidth * 0.62 : KeyWidth, rowHeight);
                IBrush fill = p == auditionPitch ? NoteFill : p == hoverPitch ? HoverKey : black ? KeyBlack : KeyWhite;
                ctx.DrawRectangle(fill, KeyEdge, r.Deflate(new Thickness(0, 0, 0.5, 0)));
                if (p % 12 == 0 && rowHeight >= 8)
                {
                    var t = Text(PFields.NoteName(p), Math.Min(10, rowHeight - 2), KeyBlack);
                    ctx.DrawText(t, new Point(KeyWidth - t.Width - 5, y + (rowHeight - t.Height) / 2));
                }
            }
        }
    }

    /// <summary>
    /// Draws the velocity lane: one thin bar per note at its start, height ∝ velocity (full lane
    /// height − 6 px = 127), with a small cap; a guide line marks velocity ≈64.
    /// </summary>
    /// <param name="ctx">Drawing context.</param>
    /// <param name="lane">Lane rectangle (right of the keys column).</param>
    void DrawLane(DrawingContext ctx, Rect lane)
    {
        ctx.FillRectangle(SurfaceBrush, lane);
        ctx.FillRectangle(SurfaceBrush, new Rect(0, LaneTop, KeyWidth, LaneHeight));
        var t = Text(Loc.S("velocidad", "velocity"), 10, TextFaint);
        ctx.DrawText(t, new Point(6, LaneTop + 4));
        using (ctx.PushClip(lane))
        {
            ctx.DrawLine(GridPen, new Point(lane.X, lane.Y + lane.Height / 2), new Point(lane.Right, lane.Y + lane.Height / 2));
            // Unselected first, selected last (OrderBy false < true), so selected bars are on top
            // when chords stack several bars at the same x.
            foreach (var n in model.Notes.OrderBy(n => model.Selected.Contains(n)))
            {
                double x = XOf(n.Start);
                if (x < lane.X - 8 || x > lane.Right) continue;
                double h = (lane.Height - 6) * n.Velocity / 127.0;
                bool sel = model.Selected.Contains(n);
                // Bar 4…8 px wide (narrower for very short notes); the cap is 6…12 px. PaintVelocity
                // hit-tests with the same cap width so painting matches what is drawn.
                var bar = new Rect(x, lane.Bottom - 3 - h, Math.Max(4, Math.Min(8, n.Length * pixelsPerBeat - 2)), h);
                ctx.FillRectangle(sel ? NoteSelected : NoteFill, bar);
                ctx.FillRectangle(sel ? NoteSelected : NoteFill, new Rect(x, bar.Y - 2, Math.Max(6, Math.Min(12, n.Length * pixelsPerBeat)), 2));
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    //  Mouse
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts a gesture. The zone and button decide the <see cref="DragMode"/>, checked in this
    /// order: middle button anywhere = pan; left on keys = audition; left on ruler = start/loop;
    /// left on velocity lane = paint velocities; then, in the grid: right on empty = box,
    /// right on a note or double-click on a note = delete, left on empty = create (Ctrl = box),
    /// Ctrl+left on a note = toggle selection, left on a note = move or resize (Alt = copy first).
    /// </summary>
    /// <param name="e">Pointer event.</param>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // Take keyboard focus so Delete, Ctrl+Z, arrows… act on this roll right after a click.
        Focus();
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        pressPoint = p;
        pressBeat = BeatOf(p.X);
        pressPitch = PitchOf(p.Y);

        if (props.IsMiddleButtonPressed)
        {
            drag = DragMode.Pan;
            lastPan = p;
            e.Pointer.Capture(this);
            return;
        }

        if (InKeys(p) && props.IsLeftButtonPressed)
        {
            drag = DragMode.Key;
            auditionPitch = pressPitch;
            Audition?.Invoke(pressPitch, 100);
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }

        if (InRuler(p) && props.IsLeftButtonPressed)
        {
            drag = DragMode.Loop;
            e.Pointer.Capture(this);
            return;
        }

        if (InLane(p) && props.IsLeftButtonPressed)
        {
            // One undo checkpoint for the whole stroke; dropped on release if nothing changed.
            model.Checkpoint();
            drag = DragMode.Velocity;
            lastVelocityPoint = null;
            PaintVelocity(p);
            e.Pointer.Capture(this);
            return;
        }

        if (!InGrid(p)) return;
        var hit = NoteAt(p);

        if (props.IsRightButtonPressed && hit is null)
        {
            // Right-drag on empty grid = box selection (Shift/Ctrl adds); a right-click without
            // moving clears the selection.
            drag = DragMode.Box;
            box = new Rect(p, p);
            e.Pointer.Capture(this);
            return;
        }
        if (props.IsRightButtonPressed || (hit is not null && e.ClickCount == 2 && props.IsLeftButtonPressed))
        {
            if (hit is not null)
            {
                // Right-click on a selected note deletes the whole selection.
                var victims = model.Selected.Contains(hit) ? model.Selected.ToList() : [hit];
                model.Delete(victims);
                Edited?.Invoke();
            }
            return;
        }
        if (!props.IsLeftButtonPressed) return;

        if (hit is null)
        {
            if (ctrl)
            {
                drag = DragMode.Box;
                box = new Rect(p, p);
                e.Pointer.Capture(this);
                return;
            }
            // New note at the grid line at or before the click; holding and dragging sets its length.
            // model.Add takes the undo checkpoint and makes the note the only selection; it starts
            // with the last used length and velocity and is auditioned immediately.
            double start = Math.Max(0, model.SnapFloor(pressBeat));
            var note = model.Add(start, pressPitch, model.LastLength, LastVelocity);
            Audition?.Invoke(note.Pitch, note.Velocity);
            dragNote = note;
            dragOrigin = new() { [note] = (note.Start, note.Length, note.Pitch) };
            createVelocity = note.Velocity;
            drag = DragMode.Create;
            e.Pointer.Capture(this);
            Edited?.Invoke();
            return;
        }

        if (ctrl)
        {
            // Ctrl+click on a note: toggle it in the selection (no drag).
            if (!model.Selected.Remove(hit)) model.Selected.Add(hit);
            model.NotifyChanged();
            return;
        }
        // Grabbing an unselected note selects only it; grabbing a selected one keeps the selection
        // so the whole group moves together.
        if (!model.Selected.Contains(hit))
        {
            model.Selected.Clear();
            model.Selected.Add(hit);
        }
        model.Checkpoint();
        // Alt+drag duplicates the selection and drags the copies. The duplication itself is
        // deferred to the first real movement (see pendingAltCopy / CommitAltCopy), so an Alt+click
        // that does not drag leaves the notes untouched.
        pendingAltCopy = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        // Snapshot every selected note as it is now; moves are computed from these originals.
        dragNote = hit;
        dragOrigin = model.Selected.ToDictionary(n => n, n => (n.Start, n.Length, n.Pitch));
        // The last EdgeGrip pixels of the grabbed note resize; anywhere else moves.
        bool onEdge = p.X >= NoteRect(hit).Right - EdgeGrip;
        drag = onEdge ? DragMode.Resize : DragMode.Move;
        if (drag == DragMode.Move) Audition?.Invoke(hit.Pitch, hit.Velocity);
        e.Pointer.Capture(this);
        model.NotifyChanged();
    }

    /// <summary>Velocity of the last note touched, used for new notes (like a pencil remembering its pressure).</summary>
    public int LastVelocity { get; set; } = 100;

    /// <summary>
    /// Advances the gesture in progress according to <see cref="drag"/>; with no gesture, updates
    /// the hovered key, the mouse cursor shape (resize / move / hand) and the status text.
    /// </summary>
    /// <param name="e">Pointer event.</param>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        double beat = BeatOf(p.X);
        switch (drag)
        {
            case DragMode.Pan:
                // Content follows the mouse ("grab and drag"): px → beats horizontally, px vertically.
                ScrollX = scrollX - (p.X - lastPan.X) / pixelsPerBeat;
                ScrollY = scrollY - (p.Y - lastPan.Y);
                lastPan = p;
                return;
            case DragMode.Key:
                // Sliding along the keys auditions each new key (no note-off is sent: Audition
                // events are short one-shot notes played by the window).
                int kp = PitchOf(p.Y);
                if (kp != auditionPitch) { auditionPitch = kp; Audition?.Invoke(kp, 100); InvalidateVisual(); }
                return;
            case DragMode.Loop:
                // Loop range between the press and the pointer (either direction), snapped. Under
                // 4 px of movement it is still a click (handled on release as "set start").
                double a = model.SnapBeat(Math.Max(0, Math.Min(pressBeat, beat)));
                double b = model.SnapBeat(Math.Max(0, Math.Max(pressBeat, beat)));
                if (Math.Abs(p.X - pressPoint.X) > 4 && b > a)
                {
                    model.LoopStart = a;
                    model.LoopEnd = b;
                    model.LoopOn = true;
                    TransportEdited?.Invoke();
                    InvalidateVisual();
                }
                return;
            case DragMode.Velocity:
                PaintVelocity(p);
                return;
            case DragMode.Box:
                // Normalised rectangle between press and pointer; selection is applied on release.
                box = new Rect(Math.Min(pressPoint.X, p.X), Math.Min(pressPoint.Y, p.Y), Math.Abs(p.X - pressPoint.X), Math.Abs(p.Y - pressPoint.Y));
                InvalidateVisual();
                return;
            case DragMode.Create:
            case DragMode.Resize:
                if (dragNote is null) return;
                // Alt+resize: duplicate now that the pointer really moved (no-op otherwise).
                if (drag == DragMode.Resize && !CommitAltCopy(p)) return;
                {
                    var o = dragOrigin[dragNote];
                    // New end of the grabbed note: the pointer beat rounded UP to the grid once it is
                    // more than a quarter of a grid step past a line (feels like "drag into the cell
                    // to include it"). All involved notes change length by the same delta.
                    double end = model.Snap > 0 ? Math.Ceiling(beat / model.Snap - 0.25) * model.Snap : beat;
                    double delta = end - (o.Start + o.Length);
                    if (drag == DragMode.Create)
                    {
                        // While creating: up/down sets the new note's velocity (up = louder),
                        // 0.8 velocity units per pixel from the press point. It also becomes the
                        // velocity of the next new note.
                        int vel = Math.Clamp(createVelocity + (int)Math.Round((pressPoint.Y - p.Y) * 0.8), 1, 127);
                        if (vel != dragNote.Velocity)
                        {
                            dragNote.Velocity = vel;
                            LastVelocity = vel;
                            Hover?.Invoke(Describe(dragNote));
                            model.NotifyChanged();
                        }
                    }
                    // A new note keeps its default length until the mouse has moved 4 px sideways,
                    // so a purely vertical velocity drag does not also shorten it.
                    if (drag == DragMode.Create && Math.Abs(p.X - pressPoint.X) < 4) return;
                    foreach (var (n, orig) in dragOrigin)
                        n.Length = Math.Max(model.MinLength, orig.Length + delta);
                    model.LastLength = dragNote.Length;
                    Hover?.Invoke(Describe(dragNote));
                    model.NotifyChanged();
                }
                return;
            case DragMode.Move:
                if (dragNote is null) return;
                // Alt+drag: duplicate now that the pointer really moved (no-op otherwise).
                if (!CommitAltCopy(p)) return;
                {
                    var o = dragOrigin[dragNote];
                    double dBeat = beat - pressBeat;
                    // The grabbed note snaps to the grid; the others keep their relative positions.
                    double newStart = model.SnapBeat(o.Start + dBeat);
                    // Dead zone: under 3 px of horizontal movement the time stays exactly as it was
                    // (an off-grid note is not snapped just because it was transposed).
                    if (Math.Abs(p.X - pressPoint.X) < 3) newStart = o.Start;
                    double shift = newStart - o.Start;
                    // Clamp the group as a whole: no note may start before beat 0 or leave 0…127,
                    // so the chord/phrase keeps its shape at the edges.
                    shift = Math.Max(shift, -dragOrigin.Values.Min(v => v.Start));
                    int dPitch = PitchOf(p.Y) - pressPitch;
                    dPitch = Math.Clamp(dPitch, -dragOrigin.Values.Min(v => v.Pitch), 127 - dragOrigin.Values.Max(v => v.Pitch));
                    int before = dragNote.Pitch;
                    foreach (var (n, orig) in dragOrigin)
                    {
                        n.Start = orig.Start + shift;
                        n.Pitch = orig.Pitch + dPitch;
                    }
                    // Audition only when the pitch actually changed, not on every pixel.
                    if (dragNote.Pitch != before) Audition?.Invoke(dragNote.Pitch, dragNote.Velocity);
                    Hover?.Invoke(Describe(dragNote));
                    model.NotifyChanged();
                }
                return;
        }

        // Not dragging: hover feedback and cursor shape.
        // (A new Cursor object is assigned on every move; cheap enough, and it keeps the code flat.)
        int? hp = InKeys(p) || InGrid(p) ? PitchOf(p.Y) : null;
        if (hp != hoverPitch) { hoverPitch = hp; InvalidateVisual(); }
        var hit = InGrid(p) ? NoteAt(p) : null;
        Cursor = hit is not null && p.X >= NoteRect(hit).Right - EdgeGrip ? new Cursor(StandardCursorType.SizeWestEast)
               : hit is not null ? new Cursor(StandardCursorType.SizeAll)
               : InRuler(p) || InLane(p) ? new Cursor(StandardCursorType.Hand)
               : Avalonia.Input.Cursor.Default;
        if (hit is not null)
            Hover?.Invoke(Describe(hit));
        else if (InGrid(p) || InKeys(p))
            Hover?.Invoke($"{PFields.NoteName(PitchOf(p.Y))} ({PitchOf(p.Y)})   ·   {Position(beat)}");
        else if (InRuler(p))
            Hover?.Invoke(Loc.S("Regla: clic = posición de inicio · arrastra = rango del loop", "Ruler: click = start position · drag = loop range") + "   ·   " + Position(beat));
        else if (InLane(p))
            Hover?.Invoke(Loc.S("Velocidad: arrastra sobre las barras para dibujarla", "Velocity: drag over the bars to draw it"));
    }

    /// <summary>
    /// Performs the duplication of an Alt+drag once the pointer has moved at least 3 px from the
    /// press (the same dead zone the move uses). The originals stay where they were; the clones are
    /// appended (so they draw on top), become the selection, and <see cref="dragNote"/> /
    /// <see cref="dragOrigin"/> are redirected to them, so the rest of the gesture drags the copies.
    /// </summary>
    /// <param name="p">Current pointer position.</param>
    /// <returns>
    /// <c>false</c> while an Alt copy is still pending inside the dead zone (the caller must not
    /// move anything yet); <c>true</c> when there is no pending copy or it was just made.
    /// </returns>
    bool CommitAltCopy(Point p)
    {
        if (!pendingAltCopy) return true;
        if (Math.Abs(p.X - pressPoint.X) < 3 && Math.Abs(p.Y - pressPoint.Y) < 3) return false;
        pendingAltCopy = false;
        // Nothing has moved yet, so each clone starts exactly at its original's press snapshot.
        var map = dragOrigin.Keys.ToDictionary(n => n, n => n.Clone());
        model.Notes.AddRange(map.Values);
        dragOrigin = map.ToDictionary(kv => kv.Value, kv => dragOrigin[kv.Key]);
        if (dragNote is not null) dragNote = map[dragNote];
        model.Selected.Clear();
        model.Selected.UnionWith(map.Values);
        model.NotifyChanged();
        return true;
    }

    /// <summary>«bar.beat.sixteenth» of a position, 1-based like a DAW.</summary>
    /// <param name="beat">Position in beats (negative values count as 0). Assumes 4/4.</param>
    /// <returns>For example <c>"3.2.4"</c> for beat 9.75.</returns>
    public static string Position(double beat)
    {
        beat = Math.Max(0, beat);
        int bar = (int)(beat / 4), b = (int)(beat % 4), six = (int)((beat - Math.Floor(beat)) * 4);
        return $"{bar + 1}.{b + 1}.{six + 1}";
    }

    /// <summary>Status-line text for a note: name and MIDI number, position, length in beats and velocity (bilingual).</summary>
    /// <param name="n">The note.</param>
    /// <returns>The localised description.</returns>
    static string Describe(RollNote n) =>
        $"{PFields.NoteName(n.Pitch)} ({n.Pitch})   ·   {Position(n.Start)}   ·   " +
        Loc.S($"duración {PFields.Num(n.Length, 3)} pulsos   ·   velocidad {n.Velocity}", $"length {PFields.Num(n.Length, 3)} beats   ·   velocity {n.Velocity}");

    /// <summary>Previous pointer position while painting velocities (null at the start of a stroke).</summary>
    Point? lastVelocityPoint;

    /// <summary>Velocity (1…127) for a y position in the lane.</summary>
    /// <param name="y">Pointer Y in pixels; clamped to the lane's 3 px inner margins, bottom = 1, top = 127.</param>
    /// <returns>The velocity.</returns>
    int VelocityAt(double y) =>
        Math.Clamp((int)Math.Round((LaneBottom - 3 - Math.Clamp(y, LaneTop + 3, LaneBottom - 3)) / (LaneHeight - 6) * 127), 1, 127);

    /// <summary>
    /// Paints velocities along the stroke from the previous pointer position to <paramref name="p"/>:
    /// every bar crossed gets the velocity of the line at its x (like drawing a line in a DAW's
    /// controller lane), so fast mouse moves never skip notes. Only selected notes are touched
    /// when there is a selection.
    /// </summary>
    /// <param name="p">Current pointer position.</param>
    void PaintVelocity(Point p)
    {
        // First event of a stroke: the segment degenerates to a point.
        var from = lastVelocityPoint ?? p;
        lastVelocityPoint = p;
        double x0 = Math.Min(from.X, p.X), x1 = Math.Max(from.X, p.X);
        int v0 = VelocityAt(from.Y), v1 = VelocityAt(p.Y);
        bool onlySelected = model.Selected.Count > 0;
        bool any = false;
        foreach (var n in model.Notes)
        {
            if (onlySelected && !model.Selected.Contains(n)) continue;
            double x = XOf(n.Start);
            double w = Math.Max(6, Math.Min(12, n.Length * pixelsPerBeat));
            // Skip bars (same width as drawn in DrawLane, ±2 px tolerance) that the segment does not cross.
            if (x + w < x0 - 2 || x > x1 + 2) continue;
            // Linear interpolation along the segment: t = 0 at the previous point, 1 at the current
            // one. A (nearly) vertical move uses the current velocity for every bar under it.
            double t = Math.Abs(p.X - from.X) < 1 ? 1 : Math.Clamp((x - from.X) / (p.X - from.X), 0, 1);
            n.Velocity = (int)Math.Round(v0 + (v1 - v0) * t);
            any = true;
        }
        if (any)
        {
            LastVelocity = v1;
            Hover?.Invoke(Loc.S($"velocidad {v1}", $"velocity {v1}"));
            model.NotifyChanged();
        }
    }

    /// <summary>
    /// Finishes the gesture: ruler click = set the start position; box = apply the selection
    /// (or clear it when the mouse did not move); move/resize/velocity = drop the undo checkpoint
    /// if nothing changed, remember the velocity and raise <see cref="Edited"/>.
    /// </summary>
    /// <param name="e">Pointer event.</param>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        // Reset the mode first so the InvalidateVisual calls below already draw the idle state
        // (e.g. no selection box).
        var mode = drag;
        drag = DragMode.None;
        e.Pointer.Capture(null);
        switch (mode)
        {
            case DragMode.Key:
                auditionPitch = null;
                InvalidateVisual();
                break;
            case DragMode.Loop:
                if (Math.Abs(p.X - pressPoint.X) <= 4)
                {
                    StartBeat = Math.Max(0, model.SnapBeat(pressBeat));
                    TransportEdited?.Invoke();
                }
                break;
            case DragMode.Box:
                {
                    var r = box;
                    bool moved = Math.Abs(p.X - pressPoint.X) > 4 || Math.Abs(p.Y - pressPoint.Y) > 4;
                    // (A former "create a note on a plain click" branch lived here; it was dead code,
                    // since a plain left click on empty grid creates the note in OnPointerPressed.)
                    if (!moved)
                    {
                        if (model.Selected.Count > 0) { model.Selected.Clear(); model.NotifyChanged(); }
                        break;
                    }
                    // Shift or Ctrl held at release = add to the selection, otherwise replace it.
                    // Any overlap with the box counts (the note need not be fully inside).
                    if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !e.KeyModifiers.HasFlag(KeyModifiers.Control)) model.Selected.Clear();
                    foreach (var n in model.Notes)
                        if (NoteRect(n).Intersects(r)) model.Selected.Add(n);
                    model.NotifyChanged();
                }
                break;
            case DragMode.Move:
            case DragMode.Resize:
            case DragMode.Velocity:
                model.DiscardCheckpointIfUnchanged();
                if (dragNote is not null) LastVelocity = dragNote.Velocity;
                Edited?.Invoke();
                break;
            case DragMode.Create:
                // Creating always changes the model (model.Add's checkpoint is kept).
                Edited?.Invoke();
                break;
        }
        dragNote = null;
        dragOrigin = [];
        pendingAltCopy = false;
    }

    /// <summary>
    /// Bug fix: if the pointer capture is lost mid-gesture (a popup or another control grabs the
    /// pointer, the window loses capture…) no release event arrives, so the gesture would stay
    /// "stuck" and keep reacting to plain mouse moves. The gesture is ended here the same way a
    /// release would end it, minus the click-only actions (set start / clear selection).
    /// </summary>
    /// <param name="e">Capture-lost event.</param>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        var mode = drag;
        if (mode == DragMode.None) return;
        drag = DragMode.None;
        if (mode is DragMode.Move or DragMode.Resize or DragMode.Velocity)
        {
            model.DiscardCheckpointIfUnchanged();
            Edited?.Invoke();
        }
        else if (mode == DragMode.Create)
            Edited?.Invoke();
        auditionPitch = null;
        dragNote = null;
        dragOrigin = [];
        pendingAltCopy = false;
        InvalidateVisual();
    }

    /// <summary>Removes the key hover highlight when the mouse leaves the control.</summary>
    /// <param name="e">Pointer event.</param>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (hoverPitch is not null) { hoverPitch = null; InvalidateVisual(); }
    }

    /// <summary>
    /// Wheel: plain = scroll pitch (3 rows per notch); Shift, or a horizontal wheel/touchpad swipe =
    /// scroll time (10 % of the view per notch); Ctrl = zoom time ×1.2 per notch keeping the beat
    /// under the mouse fixed; Ctrl+Shift or Alt = zoom pitch ×1.15 keeping the row under the mouse
    /// fixed. Always handled, so the window never scrolls behind the roll.
    /// </summary>
    /// <param name="e">Wheel event.</param>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        double dy = e.Delta.Y, dx = e.Delta.X;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        // Bug fix: a purely horizontal wheel/touchpad event has Delta.Y == 0, and the zoom
        // branches below read "dy > 0 ? in : out", so it used to zoom out. Zooms now ignore a zero
        // vertical delta; an event with no movement at all is ignored entirely.
        if (dy == 0 && dx == 0) return;
        if ((ctrl || alt) && dy == 0) { e.Handled = true; return; }
        if ((ctrl && shift) || alt)
        {
            // Vertical zoom around the pitch under the mouse.
            // Remember which row is under the pointer and how far into it, zoom, then solve
            // YOf(pitch) + offset = p.Y for the new scrollY. (The offset is kept in old-zoom pixels,
            // a sub-row approximation that is invisible in practice.)
            int pitch = PitchOf(p.Y);
            double offset = p.Y - YOf(pitch);
            RowHeight = rowHeight * (dy > 0 ? 1.15 : 1 / 1.15);
            ScrollY = (127 - pitch) * rowHeight - (p.Y - GridTop) + offset;
        }
        else if (ctrl)
        {
            // Horizontal zoom around the beat under the mouse: after zooming, choose scrollX so that
            // XOf(beat) is again the pointer x. Over the keys the anchor is the grid's left edge.
            double beat = BeatOf(Math.Max(p.X, KeyWidth));
            PixelsPerBeat = pixelsPerBeat * (dy > 0 ? 1.2 : 1 / 1.2);
            ScrollX = beat - (Math.Max(p.X, KeyWidth) - KeyWidth) / pixelsPerBeat;
        }
        else if (shift || Math.Abs(dx) > Math.Abs(dy))
            ScrollX = scrollX - (Math.Abs(dx) > Math.Abs(dy) ? dx : dy) * ViewportBeats * 0.1;
        else
            ScrollY = scrollY - dy * rowHeight * 3;
        e.Handled = true;
    }

    // ------------------------------------------------------------------------------------------
    //  Keyboard
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Editing shortcuts while the roll has focus (listed in the class remarks). Every edit goes
    /// through a <see cref="PianoRollModel"/> method that takes its own undo checkpoint. Keys that
    /// fall through the switch to the end are marked handled and raise <see cref="Edited"/>;
    /// Ctrl+A and Esc only change the selection and return early; unknown keys are left unhandled
    /// so window-level shortcuts (Space = play…) still work.
    /// </summary>
    /// <param name="e">Key event.</param>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        // Left/Right nudge by one grid step (a sixteenth when snap is off).
        double step = model.Snap > 0 ? model.Snap : 0.25;
        switch (e.Key)
        {
            case Key.Delete or Key.Back:
                // Bug fix: with nothing selected nothing is deleted, so do not raise Edited (it
                // would mark the document as modified for no change).
                if (model.Selected.Count == 0) { e.Handled = true; return; }
                model.Delete(model.Selected.ToList());
                break;
            case Key.A when ctrl:
                model.SelectAll();
                return;
            case Key.C when ctrl:
                // Bug fix: copying never changes the notes, so it returns without raising Edited
                // (which marked the document as modified). With nothing selected the clipboard is
                // also left alone instead of being emptied.
                if (model.Selected.Count > 0)
                {
                    model.Copy();
                    Hover?.Invoke(Loc.S($"{model.Selected.Count} notas copiadas", $"{model.Selected.Count} notes copied"));
                }
                e.Handled = true;
                return;
            case Key.V when ctrl:
                model.Paste(cursor);
                break;
            case Key.D when ctrl:
                // Duplicate = copy + paste right after the copied block (Paste(null)). With nothing
                // selected nothing changes (and the clipboard is kept), so no Edited either.
                if (model.Selected.Count == 0) { e.Handled = true; return; }
                if (model.Copy() > 0) model.Paste(null);
                break;
            // Ctrl+Shift+Z must be tested before plain Ctrl+Z (case guards are evaluated in order).
            case Key.Z when ctrl && shift:
            case Key.Y when ctrl:
                model.Redo();
                break;
            case Key.Z when ctrl:
                model.Undo();
                break;
            case Key.Up:
                model.Nudge(0, shift ? 12 : 1);
                break;
            case Key.Down:
                model.Nudge(0, shift ? -12 : -1);
                break;
            case Key.Left:
                model.Nudge(-step, 0);
                break;
            case Key.Right:
                model.Nudge(step, 0);
                break;
            case Key.Q when !ctrl:
                model.Quantize();
                break;
            case Key.Escape:
                model.Selected.Clear();
                model.NotifyChanged();
                return;
            default:
                return;
        }
        e.Handled = true;
        Edited?.Invoke();
    }

    /// <summary>Repaints to show the focus border (the cue that the editing shortcuts apply here).</summary>
    /// <param name="e">Focus event.</param>
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    /// <summary>Repaints to hide the focus border.</summary>
    /// <param name="e">Focus event.</param>
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    /// <summary>
    /// After a resize the viewport (and so the legal scroll range) changed: re-clamp the scroll
    /// position and tell the window to update its scroll bars.
    /// </summary>
    /// <param name="e">Size event.</param>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ClampScroll();
        ViewChanged?.Invoke();
    }
}
