// SequencerTimeline.cs — Paint Rooms · Image Sequencer: the arrangement view (lanes × bars).
//
// Role: draws the SequencerDocument's clips on a grid of LANES (rows) and STEPS (columns, grouped in
// beats and bars) and turns mouse gestures into requests the room carries out (so the room keeps
// the undo history and the model consistent):
//   * left click on an empty cell   → PlaceRequested(lane, step snapped to the bar);
//   * left DRAG on empty cells       → a ghost clip of the current pattern follows the mouse and
//                                      ClipDrawn(lane, start, length) on release: the length is
//                                      chosen while drawing (snapped to the beat, Shift = step);
//   * drag of a clip's RIGHT EDGE    → ClipResized(clip, length) (same snapping): longer or
//                                      shorter; the room decides whether the content loops or is
//                                      stretched (its «Stretch» box, see NewClipsStretch);
//   * left drag of a clip            → it follows the mouse (snapped to the beat, or to the step
//                                      with Shift) and ClipMoved(clip, lane, step) on release;
//   * a click without moving         → ClipSelected(clip) (also raised when a drag starts);
//   * double click on a clip         → ClipOpened(clip) (the room shows its pattern in the editor);
//   * right click on a clip          → ClipDeleteRequested(clip);
//   * click / drag on the top ruler  → CursorStep (the «play from» cursor) and CursorMoved.
// Every clip is drawn as a rounded block in its pattern's colour with a miniature of the pattern
// (one cell per step, lit rows in the text colour, reversed / inverted / transposed like the sound)
// and a caption: pattern name, «×repeats», transposition, «⇆» reverse, «↕» invert, gain.
//
// Geometry: the whole arrangement always fits the width (no horizontal scroll):
//     x(step) = HeaderWidth + step · StepWidth,  StepWidth = (Bounds.Width − HeaderWidth) / TotalSteps
//     lane l spans [RulerHeight + l·LaneHeight, … + LaneHeight)
// Colours come from the app theme (AppTheme roles), never fixed hex values.
// UI thread only; knows nothing about sound.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CsoundVisual.Localization;
using CsoundVisual.Paint;
using CsoundVisual.Theming;

namespace CsoundVisual.Controls;

/// <summary>The Image Sequencer's arrangement view (see the file header).</summary>
public sealed class SequencerTimeline : Control
{
    /// <summary>Width of the lane-name column on the left (DIP).</summary>
    public const double HeaderWidth = 64;

    /// <summary>Height of the bar ruler on top (DIP).</summary>
    public const double RulerHeight = 22;

    /// <summary>Theme roles used as clip colours, cycled by <see cref="SeqPattern.ColorIndex"/>.</summary>
    public static readonly string[] ClipRoles = ["Accent", "Good", "Warn", "SynKRate", "SynARate", "SynIRate", "Hot", "AccentLight"];

    /// <summary>Backing field of <see cref="Document"/>.</summary>
    SequencerDocument? doc;

    /// <summary>Clip being dragged, its grab offset in steps, and its current (snapped) target.</summary>
    SeqClip? dragClip;
    int dragOffset, dragLane, dragStep;
    /// <summary>Pointer position at the press (a press that did not move is a click).</summary>
    Point pressAt;
    /// <summary>True once the pointer moved more than 3 DIP during a clip press.</summary>
    bool dragMoved;
    /// <summary>True while the ruler is being dragged (moves the cursor).</summary>
    bool rulerDrag;

    /// <summary>True while a new clip is being drawn by dragging on empty cells.</summary>
    bool drawing;
    /// <summary>Lane, first step and current length (steps, 0 = not dragged far enough yet) of the clip being drawn.</summary>
    int drawLane, drawStart, drawLen;

    /// <summary>Clip whose right edge is being dragged, and its current (snapped) length.</summary>
    SeqClip? resizeClip;
    int resizeLen;

    /// <summary>Distance (DIP) from a clip's right edge within which a press resizes instead of moving.</summary>
    const double EdgeGrip = 7;

    /// <summary>Pattern placed by clicks and drawn by drags (the room's current pattern; 0 = none).</summary>
    public int CurrentPatternId { get; set; }

    /// <summary>Whether clips drawn or resized now are stretched (the room's «Stretch» box); used for the preview ghost.</summary>
    public bool NewClipsStretch { get; set; }

    /// <summary>Miniature bitmaps per pattern id, with the image version and object they were drawn from.</summary>
    readonly Dictionary<int, (SpectralImage Image, long Version, WriteableBitmap Bitmap)> thumbs = [];

    /// <summary>Creates the view.</summary>
    public SequencerTimeline()
    {
        Focusable = true;
        ClipToBounds = true;
        AppTheme.Changed += OnThemeChanged;
    }

    /// <summary>Theme map used for the miniatures (built lazily, dropped when the theme changes).</summary>
    uint[]? colorMap;

    /// <summary>Theme switched: the miniatures are recoloured (rebuilt on the next render).</summary>
    void OnThemeChanged()
    {
        colorMap = null;
        foreach (var t in thumbs.Values) t.Bitmap.Dispose();
        thumbs.Clear();
        InvalidateVisual();
    }

    /// <summary>The document shown (patterns, clips, timing).</summary>
    public SequencerDocument? Document
    {
        get => doc;
        set { doc = value; thumbs.Clear(); InvalidateVisual(); }
    }

    /// <summary>The selected clip (highlighted), or <c>null</c>.</summary>
    public SeqClip? SelectedClip
    {
        get => selected;
        set { selected = value; InvalidateVisual(); }
    }

    /// <summary>Backing field of <see cref="SelectedClip"/>.</summary>
    SeqClip? selected;

    /// <summary>Step being heard while playing (draws the play line), or <c>null</c>.</summary>
    public double? PlayStep
    {
        get => playStep;
        set { if (playStep == value) return; playStep = value; InvalidateVisual(); }
    }

    /// <summary>Backing field of <see cref="PlayStep"/>.</summary>
    double? playStep;

    /// <summary>The «play from» cursor in steps (0…TotalSteps).</summary>
    public double CursorStep
    {
        get => cursorStep;
        set { cursorStep = Math.Clamp(value, 0, doc?.TotalSteps ?? 0); CursorMoved?.Invoke(); InvalidateVisual(); }
    }

    /// <summary>Backing field of <see cref="CursorStep"/>.</summary>
    double cursorStep;

    /// <summary>Left click on an empty cell: (lane, step at the start of that bar).</summary>
    public event Action<int, int>? PlaceRequested;
    /// <summary>A clip was dragged to (lane, step).</summary>
    public event Action<SeqClip, int, int>? ClipMoved;
    /// <summary>A new clip was drawn by dragging: (lane, first step, length in steps).</summary>
    public event Action<int, int, int>? ClipDrawn;
    /// <summary>A clip's right edge was dragged: (clip, new length in steps).</summary>
    public event Action<SeqClip, int>? ClipResized;
    /// <summary>A clip was clicked (or a drag started on it); <c>null</c> = click on empty space with a selection active.</summary>
    public event Action<SeqClip?>? ClipSelected;
    /// <summary>A clip was double-clicked.</summary>
    public event Action<SeqClip>? ClipOpened;
    /// <summary>Right click on a clip.</summary>
    public event Action<SeqClip>? ClipDeleteRequested;
    /// <summary>The cursor moved (ruler click or <see cref="CursorStep"/> set).</summary>
    public event Action? CursorMoved;
    /// <summary>Hover read-out text (bar.beat.step, lane, clip under the mouse) or "".</summary>
    public event Action<string>? Hover;

    /// <summary>DIP per step.</summary>
    double StepWidth => doc is null ? 1 : Math.Max(0.5, (Bounds.Width - HeaderWidth) / doc.TotalSteps);

    /// <summary>DIP per lane.</summary>
    double LaneHeight => doc is null ? 30 : Math.Max(18, (Bounds.Height - RulerHeight) / Math.Max(1, doc.Lanes));

    /// <summary>Screen x of a step.</summary>
    /// <param name="step">Step (fractional allowed).</param>
    public double XOf(double step) => HeaderWidth + step * StepWidth;

    /// <summary>Screen y of the top of a lane.</summary>
    /// <param name="lane">Lane index.</param>
    public double YOf(int lane) => RulerHeight + lane * LaneHeight;

    /// <summary>Step under a screen x (fractional, clamped to the arrangement).</summary>
    /// <param name="x">DIP.</param>
    public double StepAt(double x) => Math.Clamp((x - HeaderWidth) / StepWidth, 0, doc?.TotalSteps ?? 0);

    /// <summary>Lane under a screen y (clamped).</summary>
    /// <param name="y">DIP.</param>
    public int LaneAt(double y) => Math.Clamp((int)Math.Floor((y - RulerHeight) / LaneHeight), 0, Math.Max(0, (doc?.Lanes ?? 1) - 1));

    /// <summary>The clip drawn at a point (the topmost, i.e. the last in the list), or <c>null</c>.</summary>
    /// <param name="p">Point in control DIP.</param>
    public SeqClip? ClipAt(Point p)
    {
        if (doc is null || p.Y < RulerHeight || p.X < HeaderWidth) return null;
        int lane = LaneAt(p.Y);
        double step = StepAt(p.X);
        for (int i = doc.Clips.Count - 1; i >= 0; i--)
        {
            var c = doc.Clips[i];
            if (c.Lane == lane && step >= c.StartStep && step < c.StartStep + doc.ClipSteps(c)) return c;
        }
        return null;
    }

    /// <summary>True when <paramref name="p"/> is on the grip zone at the right end of <paramref name="c"/> (resize, not move).</summary>
    /// <param name="c">The clip under the pointer.</param>
    /// <param name="p">Point in control DIP.</param>
    /// <remarks>Very narrow clips (under 3 grips wide) have no grip, so they can still be moved.</remarks>
    bool NearRightEdge(SeqClip c, Point p)
    {
        double x0 = XOf(c.StartStep), x1 = XOf(c.StartStep + doc!.ClipSteps(c));
        return x1 - x0 >= 3 * EdgeGrip && p.X >= x1 - EdgeGrip && p.X <= x1 + 1;
    }

    /// <summary>Centre of a clip in control DIP (tests click it).</summary>
    /// <param name="c">The clip.</param>
    public Point CenterOf(SeqClip c) =>
        new(XOf(c.StartStep + (doc?.ClipSteps(c) ?? 1) / 2.0), YOf(c.Lane) + LaneHeight / 2);

    // ------------------------------------------------------------------------------------------
    //  Drawing
    // ------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>Order: background, lane stripes, grid (steps faint, beats, bars strong), clips, drag ghost, cursor, play line, ruler, lane names.</remarks>
    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        ctx.FillRectangle(AppTheme.Brush("BgDeep"), b);
        if (doc is null) return;
        int total = doc.TotalSteps, lanes = doc.Lanes;
        double sw = StepWidth, lh = LaneHeight, right = XOf(total);
        // Alternate lane stripes.
        for (int l = 0; l < lanes; l++)
            if (l % 2 == 1) ctx.FillRectangle(AppTheme.Brush("Surface"), new Rect(HeaderWidth, YOf(l), right - HeaderWidth, lh));
        // Grid: steps (only when wide enough), beats, bars.
        var faint = new Pen(AppTheme.Brush("Grid"), 0.5);
        var beatPen = new Pen(AppTheme.Brush("Line"), 1);
        var barPen = new Pen(AppTheme.Brush("TextFaint"), 1.5);
        double bottom = YOf(lanes);
        for (int s = 0; s <= total; s++)
        {
            bool bar = s % doc.StepsPerBar == 0, beat = s % Math.Max(1, doc.StepsPerBeat) == 0;
            if (!bar && !beat && sw < 6) continue;
            double x = Math.Round(XOf(s)) + 0.5;
            ctx.DrawLine(bar ? barPen : beat ? beatPen : faint, new Point(x, RulerHeight), new Point(x, bottom));
        }
        for (int l = 0; l <= lanes; l++)
            ctx.DrawLine(beatPen, new Point(HeaderWidth, YOf(l)), new Point(right, YOf(l)));

        // Clips.
        foreach (var c in doc.Clips)
        {
            if (ReferenceEquals(c, dragClip) && dragMoved) continue;   // drawn as the ghost below
            DrawClip(ctx, c, c.Lane, c.StartStep, ghost: false);
        }
        if (dragClip is { } dc && dragMoved) DrawClip(ctx, dc, dragLane, dragStep, ghost: true);
        // Ghosts of a clip being drawn or resized (what the room will create on release).
        if (drawing && drawLen > 0 && CurrentPatternId != 0)
            DrawClip(ctx, new SeqClip(-1, CurrentPatternId, drawLane, drawStart, Length: drawLen, Stretch: NewClipsStretch), drawLane, drawStart, ghost: true);
        if (resizeClip is { } rc && resizeLen > 0)
            DrawClip(ctx, rc with { Length = resizeLen, Repeats = 1, Stretch = NewClipsStretch }, rc.Lane, rc.StartStep, ghost: true);

        // Cursor and play line.
        var cx = XOf(cursorStep);
        ctx.DrawLine(new Pen(AppTheme.Brush("AccentHi"), 2), new Point(cx, 0), new Point(cx, bottom));
        if (playStep is { } ps)
        {
            double px = XOf(ps);
            ctx.DrawLine(new Pen(AppTheme.Brush("Hot"), 2), new Point(px, RulerHeight), new Point(px, bottom));
        }

        // Ruler: bar numbers.
        ctx.FillRectangle(AppTheme.Brush("Bg"), new Rect(0, 0, b.Width, RulerHeight));
        var tf = new Typeface(FontFamily.Default);
        int every = Math.Max(1, (int)Math.Ceiling(28 / (sw * doc.StepsPerBar)));   // label spacing ≥ 28 DIP
        for (int bar = 0; bar < doc.Bars; bar += every)
        {
            var ft = new FormattedText((bar + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, tf, 11, AppTheme.Brush("TextDim"));
            ctx.DrawText(ft, new Point(XOf(bar * doc.StepsPerBar) + 3, 4));
        }
        // Cursor head (triangle) on the ruler.
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(new Point(cx - 6, 2), true);
            g.LineTo(new Point(cx + 6, 2));
            g.LineTo(new Point(cx, RulerHeight - 4));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(AppTheme.Brush("AccentHi"), null, head);

        // Lane names column.
        ctx.FillRectangle(AppTheme.Brush("Bg"), new Rect(0, RulerHeight, HeaderWidth, bottom - RulerHeight));
        for (int l = 0; l < lanes; l++)
        {
            var ft = new FormattedText(Loc.S("Pista ", "Lane ") + (l + 1), System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, tf, 11, AppTheme.Brush("TextDim"));
            ctx.DrawText(ft, new Point(8, YOf(l) + (lh - ft.Height) / 2));
        }
    }

    /// <summary>Draws one clip block with its miniature and caption.</summary>
    /// <param name="ctx">Drawing context.</param>
    /// <param name="c">The clip.</param>
    /// <param name="lane">Lane to draw it in (the drag target while dragging).</param>
    /// <param name="start">First step (the drag target while dragging).</param>
    /// <param name="ghost">True for the semi-transparent drag preview.</param>
    void DrawClip(DrawingContext ctx, SeqClip c, int lane, int start, bool ghost)
    {
        if (doc!.Find(c.PatternId) is not { } p) return;
        int len = doc.ClipSteps(c);
        if (len <= 0) return;
        double x0 = XOf(start), x1 = Math.Min(XOf(Math.Min(start + len, doc.TotalSteps)), Bounds.Width);
        var r = new Rect(x0 + 1, YOf(lane) + 2, Math.Max(2, x1 - x0 - 2), LaneHeight - 4);
        var colour = AppTheme.ColorOf(ClipRoles[((p.ColorIndex % ClipRoles.Length) + ClipRoles.Length) % ClipRoles.Length]);
        using (ctx.PushOpacity(ghost ? 0.55 : 1))
        {
            ctx.DrawRectangle(new SolidColorBrush(colour, 0.32), new Pen(new SolidColorBrush(colour), ReferenceEquals(c, selected) ? 2.5 : 1), r, 5, 5);
            // Miniature: one bitmap pixel per cell, drawn once per pass of the pattern (the last pass
            // cut by the clip's end), or once over the whole clip when it is stretched; flipped for
            // reverse / invert.
            var bmp = Thumb(p);
            if (bmp is not null)
            {
                double repW = c.Stretch ? len * StepWidth : p.Steps * StepWidth;
                int passes = c.Stretch ? 1 : (len + p.Steps - 1) / p.Steps;
                using (ctx.PushClip(r.Deflate(2)))
                    for (int k = 0; k < passes; k++)
                    {
                        var dest = new Rect(XOf(start) + k * repW, r.Y + 2, repW, r.Height - 4);
                        if (dest.X > x1) break;
                        // Transposition shifts the miniature vertically by rows (one row = height / rows).
                        double rowH = dest.Height / p.Image.Height;
                        dest = dest.Translate(new Vector(0, -c.Transpose * rowH));
                        var m = Matrix.CreateTranslation(-dest.Center.X, -dest.Center.Y) *
                                Matrix.CreateScale(c.Reverse ? -1 : 1, c.Invert ? -1 : 1) *
                                Matrix.CreateTranslation(dest.Center.X, dest.Center.Y);
                        using (ctx.PushTransform(m))
                        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.LowQuality }))
                            ctx.DrawImage(bmp, new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height), dest);
                    }
            }
            // Caption.
            string reps = c.Stretch ? (len != p.Steps ? " ↔" : "") : len % p.Steps == 0 ? (len / p.Steps > 1 ? " ×" + len / p.Steps : "") : $" ×{(double)len / p.Steps:0.##}";
            string cap = p.Name + reps + (c.Transpose != 0 ? (c.Transpose > 0 ? " +" : " ") + c.Transpose : "") +
                         (c.Reverse ? " ⇆" : "") + (c.Invert ? " ↕" : "") + (Math.Abs(c.GainDb) > 0.05 ? $" {c.GainDb:+0.#;-0.#} dB" : "");
            var ft = new FormattedText(cap, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 11, AppTheme.Brush("Text"));
            if (ft.Width + 8 < r.Width || r.Width > 40)
                using (ctx.PushClip(r))
                {
                    // Dark rounded backing: the miniature can be bright (an imported picture in its
                    // own colours), and the caption must stay readable on top of it.
                    ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), new Rect(r.X + 2, r.Y + 2, ft.Width + 7, ft.Height + 1), 3);
                    ctx.DrawText(ft, new Point(r.X + 5, r.Y + 2));
                }
        }
    }

    /// <summary>The miniature of a pattern (rebuilt when its pixels changed), or <c>null</c>.</summary>
    /// <param name="p">The pattern.</param>
    /// <returns>A Steps × Rows bitmap: lit cells in the text colour with alpha = level (noisy cells dimmer).</returns>
    unsafe WriteableBitmap? Thumb(SeqPattern p)
    {
        var img = p.Image;
        if (thumbs.TryGetValue(p.Id, out var t) && ReferenceEquals(t.Image, img) && t.Version == img.Version) return t.Bitmap;
        t.Bitmap?.Dispose();
        var bmp = new WriteableBitmap(new PixelSize(img.Width, img.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        // Same colours as the pattern editor (PaintCanvas.CellColor: theme map for equal layers, red /
        // green true colour for stereo cells, noise texture), so an imported picture looks the same
        // in the arrangement. Silent cells stay transparent so the clip's own colour shows behind
        // the notes.
        colorMap ??= PaintCanvas.ThemeColorMap();
        using (var fb = bmp.Lock())
        {
            byte* basePtr = (byte*)fb.Address;
            int h = img.Height;
            var nz = img.Noise;
            for (int x = 0; x < img.Width; x++)
                for (int y = 0; y < h; y++)
                {
                    float l = img.Left[x * h + y], r = img.Right[x * h + y];
                    uint px = l <= 0.004f && r <= 0.004f ? 0u
                        : PaintCanvas.CellColor(colorMap, l, r, nz?[x * h + y] ?? 0f, ((x + y) & 1) == 1);
                    *(uint*)(basePtr + (h - 1 - y) * fb.RowBytes + x * 4) = px;
                }
        }
        thumbs[p.Id] = (img, img.Version, bmp);
        return bmp;
    }

    // ------------------------------------------------------------------------------------------
    //  Mouse
    // ------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (doc is null) return;
        Focus();
        var p = e.GetPosition(this);
        var pt = e.GetCurrentPoint(this).Properties;
        if (p.Y < RulerHeight && pt.IsLeftButtonPressed)
        {
            rulerDrag = true;
            CursorStep = Math.Round(StepAt(p.X));
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        var clip = ClipAt(p);
        if (pt.IsRightButtonPressed)
        {
            if (clip is not null) ClipDeleteRequested?.Invoke(clip);
            e.Handled = true;
            return;
        }
        if (!pt.IsLeftButtonPressed) return;
        if (clip is not null && NearRightEdge(clip, p))
        {
            // Grab the right edge: change the clip's length.
            selected = clip;
            ClipSelected?.Invoke(clip);
            resizeClip = clip;
            resizeLen = 0;
            pressAt = p;
            e.Pointer.Capture(this);
        }
        else if (clip is not null)
        {
            if (e.ClickCount >= 2) { ClipOpened?.Invoke(clip); e.Handled = true; return; }
            selected = clip;
            ClipSelected?.Invoke(clip);
            dragClip = clip;
            dragOffset = (int)Math.Floor(StepAt(p.X)) - clip.StartStep;
            dragLane = clip.Lane;
            dragStep = clip.StartStep;
            dragMoved = false;
            pressAt = p;
            e.Pointer.Capture(this);
        }
        else if (p.X >= HeaderWidth && p.Y >= RulerHeight)
        {
            // Empty cell: a click places the current pattern at the start of the bar (on release);
            // a drag draws a clip whose length follows the mouse.
            int snap = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : Math.Max(1, doc.StepsPerBeat);
            drawing = true;
            drawLane = LaneAt(p.Y);
            drawStart = (int)Math.Floor(StepAt(p.X) / snap) * snap;
            drawLen = 0;
            pressAt = p;
            e.Pointer.Capture(this);
        }
        e.Handled = true;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (doc is null) return;
        var p = e.GetPosition(this);
        if (rulerDrag) { CursorStep = Math.Round(StepAt(p.X)); return; }
        if (drawing || resizeClip is not null)
        {
            if (Math.Abs(p.X - pressAt.X) < 4 && (drawing ? drawLen : resizeLen) == 0) return;
            int snap = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : Math.Max(1, doc.StepsPerBeat);
            int start = drawing ? drawStart : resizeClip!.StartStep;
            int end = (int)Math.Round(StepAt(p.X) / snap) * snap;
            int len = Math.Clamp(end - start, 1, Math.Max(1, doc.TotalSteps - start));
            if (drawing) drawLen = len; else resizeLen = len;
            Hover?.Invoke(Loc.S($"Largo: {len} pasos ({(double)len / doc.StepsPerBar:0.##} compases) · Shift = paso a paso", $"Length: {len} steps ({(double)len / doc.StepsPerBar:0.##} bars) · Shift = single steps"));
            InvalidateVisual();
            return;
        }
        if (dragClip is { } c)
        {
            if (!dragMoved && Math.Abs(p.X - pressAt.X) + Math.Abs(p.Y - pressAt.Y) < 4) return;
            dragMoved = true;
            // Snap to the beat, or to the single step with Shift.
            int snap = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : Math.Max(1, doc.StepsPerBeat);
            int raw = (int)Math.Floor(StepAt(p.X)) - dragOffset;
            dragStep = Math.Clamp((int)Math.Round((double)raw / snap) * snap, 0, Math.Max(0, doc.TotalSteps - 1));
            dragLane = LaneAt(p.Y);
            InvalidateVisual();
            return;
        }
        // Hover read-out: bar.beat.step and what is under the mouse.
        if (p.X < HeaderWidth || p.Y < RulerHeight) { Hover?.Invoke(""); return; }
        int s = (int)Math.Floor(StepAt(p.X));
        int bar = s / doc.StepsPerBar + 1, beat = s % doc.StepsPerBar / Math.Max(1, doc.StepsPerBeat) + 1, st = s % Math.Max(1, doc.StepsPerBeat) + 1;
        var under = ClipAt(p);
        bool edge = under is not null && NearRightEdge(under, p);
        Cursor = edge ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
        string what = edge
            ? Loc.S(" · arrastra el borde = cambiar el largo del clip", " · drag the edge = change the clip's length")
            : under is not null && doc.Find(under.PatternId) is { } up
            ? Loc.S($" · «{up.Name}» (doble clic = editar, clic derecho = quitar, arrastra el borde derecho = largo)", $" · «{up.Name}» (double-click = edit, right-click = remove, drag the right edge = length)")
            : Loc.S(" · clic = poner el patrón actual aquí · arrastrar = dibujar un clip del largo que quieras", " · click = place the current pattern here · drag = draw a clip of any length");
        Hover?.Invoke(Loc.S($"Compás {bar}.{beat}.{st} · pista {LaneAt(p.Y) + 1}", $"Bar {bar}.{beat}.{st} · lane {LaneAt(p.Y) + 1}") + what);
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        rulerDrag = false;
        if (drawing)
        {
            drawing = false;
            if (drawLen > 0) ClipDrawn?.Invoke(drawLane, drawStart, drawLen);
            else
            {
                // A plain click: the classic placement at the start of the bar.
                int step = (int)Math.Floor(StepAt(pressAt.X) / doc!.StepsPerBar) * doc.StepsPerBar;
                PlaceRequested?.Invoke(drawLane, step);
            }
            drawLen = 0;
            InvalidateVisual();
        }
        if (resizeClip is { } rc)
        {
            resizeClip = null;
            if (resizeLen > 0) ClipResized?.Invoke(rc, resizeLen);
            resizeLen = 0;
            InvalidateVisual();
        }
        if (dragClip is { } c)
        {
            if (dragMoved && (dragLane != c.Lane || dragStep != c.StartStep)) ClipMoved?.Invoke(c, dragLane, dragStep);
            dragClip = null;
            dragMoved = false;
            InvalidateVisual();
        }
        e.Pointer.Capture(null);
    }

    /// <inheritdoc/>
    /// <remarks>Capture lost without a release (Alt+Tab, a dialog): a clip being drawn, resized or moved is abandoned.</remarks>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        drawing = false; drawLen = 0;
        resizeClip = null; resizeLen = 0;
        dragClip = null; dragMoved = false;
        rulerDrag = false;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Hover?.Invoke("");
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        AppTheme.Changed -= OnThemeChanged;
        foreach (var t in thumbs.Values) t.Bitmap.Dispose();
        thumbs.Clear();
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AppTheme.Changed -= OnThemeChanged;
        AppTheme.Changed += OnThemeChanged;
    }
}
