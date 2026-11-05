// PaintCanvas.cs — Paint Rooms: the custom-drawn canvas control.
//
// Role: shows the session's SpectralImage and turns mouse gestures into brush strokes, shapes and
// selections (through the session's BrushEngine). Used as-is by every room: Paint Synth, the
// Spectrum Room (which puts a time-aligned WaveformStrip above it) and the Image Filter. The canvas
// knows nothing about sound: it only edits the PaintSession; the room listens to the session's
// events to push changes to Csound.
//
// Data flow: mouse → BrushEngine (session.Brushes) edits SpectralImage → session raises
// PixelsChanged(x0, x1) → UpdateBitmap recolours just those columns → InvalidateVisual. Replacing the
// whole image (open, undo of a resize, new canvas) raises ImageReplaced and rebuilds the bitmap.
//
// Drawing (fast at any size): the image lives in a WriteableBitmap with ONE PIXEL PER CELL, kept in
// sync column-by-column when the image's pixels change, and drawn scaled to the zoom with a single
// DrawImage (nearest-neighbour when zoomed in, so cells stay crisp; filtered when zoomed out).
// Colours: cells whose two layers are equal use a perceptual colour map built from the app theme
// (black → deep accent → accent → light → white, monotonic in brightness); cells where left ≠ right
// are drawn in TRUE colour with the export convention (red = left, green = right, blue = min),
// so stereo painting is visible at a glance.
// Noise layer (SpectralImage.Noise, per-cell noisiness 0 = tone … 1 = noise band): while ShowNoise
// is on, a noisy cell is drawn DESATURATED towards grey and, on every other cell of a checkerboard
// ((x + y) odd), darkened — a grainy «hatched» texture whose strength grows with the noisiness, so
// tone and noise are told apart at a glance while brightness still reads as loudness (the average
// brightness of the texture drops by at most ~20 %). Silent cells are never textured.
//
// Geometry («image units»): x = fractional column (cell x spans [x, x+1)), y = fractional row
// (row 0 at the BOTTOM, cell y spans [y, y+1)). Screen ↔ image:
//     sx = RulerWidth + (x − ScrollX)·ZoomX
//     sy = RulerHeight + (Height − y − ScrollY)·ZoomY      (ScrollY = rows hidden above the view)
// ZoomX / ZoomY are DIP per column / per row. Time in seconds = x · image.SecondsPerColumn; the
// frequency of a row comes from image.Axis (PitchAxis). Because ScrollY counts from the TOP while
// rows count from the BOTTOM, the formula flips y with (Height − y).
// Rulers: pitch on the left (labels from PitchAxis), time on top (seconds); clicking/dragging the
// time ruler moves the play-from cursor.
//
// Mouse: left = current tool; right = eraser (freehand tools) or clear the selection; middle drag =
// pan; wheel = scroll pitch, Shift+wheel = scroll time, Ctrl+wheel = zoom time, Ctrl+Shift+wheel or
// Alt+wheel = zoom pitch. Keys are handled by the room (it owns undo, clipboard and transport); the
// room first offers Enter / Esc / Backspace to HandleSelectionKey while a spline is being placed.
//
// Selection tools (all produce a SelectionMask, see Paint/SelectionMask.cs):
//   * Select (rectangle) and SelectEllipse: drag a box. For the ellipse, Shift pressed DURING the
//     drag makes it a circle in SCREEN pixels (equal width and height on screen, so a different
//     number of columns and rows when the zooms differ) — like Photoshop, a Shift already held at
//     the press means «add» and only counts as «circle» after being released and pressed again;
//   * SelectLasso: the pointer path (points ≥ 2 DIP apart) is the outline; release closes it;
//   * SelectSpline: each click adds a point; the preview is the closed Catmull-Rom curve through the
//     points plus the mouse position; double-click or Enter closes, Esc cancels, Backspace removes
//     the last point.
// Modifiers at the press: plain = replace, Shift = add, Alt = subtract (SelectionCombine). A plain
// press inside the current selection with the rectangle, ellipse or lasso tool moves it instead.
// The «feather» (screen pixels, SelectionFeather) softens the new shape before it is combined.
// Coordinates: shapes are built in image units (ToImage); a screen distance d becomes d / ZoomX
// columns and d / ZoomY rows. The marching ants are the mask's traced outline (cell corners)
// converted with ToScreen and drawn twice — a dark solid line under a light dashed one whose dash
// offset advances on a timer, so the outline is visible on any picture and appears to «march».
// UI thread only.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CsoundVisual.Localization;
using CsoundVisual.Paint;
using CsoundVisual.Theming;

namespace CsoundVisual.Controls;

/// <summary>Custom-drawn, zoomable Paint Rooms canvas (see the file header).</summary>
public sealed class PaintCanvas : Control
{
    /// <summary>Width of the pitch ruler on the left (DIP).</summary>
    public const double RulerWidth = 62;

    /// <summary>Height of the time ruler on top (DIP).</summary>
    public const double RulerHeight = 22;

    /// <summary>Font of the ruler labels (font fallback list).</summary>
    static readonly Typeface Face = new("Segoe UI, Inter, Arial");

    /// <summary>Backing field of <see cref="Session"/>.</summary>
    PaintSession? session;

    /// <summary>The image currently shown (cached <c>session.Image</c>, refreshed on <c>ImageReplaced</c>).</summary>
    SpectralImage? image;

    /// <summary>One pixel per cell (width = columns, height = rows, bitmap row 0 = highest pitch); null without an image.</summary>
    WriteableBitmap? bitmap;

    /// <summary>
    /// 256-entry colour map for cells whose two layers are equal, indexed by round(level · 255).
    /// Each entry is a 0xAARRGGBB uint, which on little-endian x86/x64 is the byte order B, G, R, A
    /// expected by <see cref="PixelFormat.Bgra8888"/>; alpha is always 255, so «premultiplied» is a no-op.
    /// </summary>
    uint[] mono = new uint[256];       // colour map for equal layers (BGRA premultiplied, opaque)

    /// <summary>View state: DIP per column / row, and the first visible column / rows hidden above (fractional).</summary>
    double zoomX = 1, zoomY = 1, scrollX, scrollY;

    /// <summary>
    /// True until a successful <see cref="ZoomToFit"/>: the control may receive an image before its
    /// first layout (Bounds = 0), so fitting is retried in <see cref="OnSizeChanged"/>.
    /// </summary>
    bool fitPending = true;

    // ---- gesture state

    /// <summary>What the current mouse drag is doing (decided on pointer press).</summary>
    /// <remarks>
    /// <c>None</c> = no drag; <c>Stroke</c> = freehand stroke through <see cref="BrushEngine"/>;
    /// <c>Shape</c> = line / rectangle / ellipse / gradient, previewed while dragging and applied on
    /// release; <c>SelectNew</c> = rubber-banding a new selection; <c>SelectMove</c> = moving the
    /// existing selection (applied on release, in whole cells); <c>Pan</c> = middle-button panning;
    /// <c>Ruler</c> = dragging the play-from cursor along the time ruler.
    /// </remarks>
    /// <remarks>
    /// <c>SelectNew</c> covers the three drag-based selection tools (rectangle, ellipse, lasso;
    /// <see cref="dragTool"/> says which). The spline is click-based and lives in <see cref="spline"/>.
    /// </remarks>
    enum Drag { None, Stroke, Shape, SelectNew, SelectMove, Pan, Ruler }

    /// <summary>How the selection being drawn combines with the current one (decided at the press from Shift / Alt).</summary>
    SelectionCombine selectMode;

    /// <summary>Shift was down when the ellipse drag started (then it means «add», not «circle»).</summary>
    bool shiftAtPress;

    /// <summary>Shift was released at least once since the press (a later Shift then means «circle»).</summary>
    bool shiftReleased;

    /// <summary>Shift is currently down (tracked from pointer events, for the circle preview).</summary>
    bool shiftNow;

    /// <summary>Lasso outline being drawn, in image units (empty when no lasso is in progress).</summary>
    readonly List<(double X, double Y)> lasso = [];

    /// <summary>Spline control points placed so far, image units (empty = no spline in progress).</summary>
    readonly List<(double X, double Y)> spline = [];

    /// <summary>Combine mode of the spline in progress (from the modifiers of its first click).</summary>
    SelectionCombine splineMode;

    /// <summary>Dash offset of the marching ants (advanced by <see cref="antsTimer"/>).</summary>
    double antsPhase;

    /// <summary>Moves the marching ants ~7 times per second while something is selected (runs only while attached).</summary>
    readonly DispatcherTimer antsTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };

    /// <summary>The drag in progress.</summary>
    Drag drag;

    /// <summary>Tool of the drag in progress (the eraser for a right-button stroke).</summary>
    PaintTool dragTool;

    /// <summary>Screen position (control DIP) where the drag started; used by panning.</summary>
    Point dragStartScreen;

    /// <summary>Image coordinates where the drag started and where the pointer is now.</summary>
    (double X, double Y) dragStart, dragNow;

    /// <summary>Scroll position when a pan started (the pan offsets it by the pointer travel).</summary>
    double panScrollX, panScrollY;

    /// <summary>Last pointer position over the control (draws the brush outline), or null when outside.</summary>
    Point? hover;

    /// <summary>Creates the canvas.</summary>
    /// <remarks>
    /// Focusable so the room's key bindings work after a click on the canvas. Subscribes to theme
    /// changes (the colour map follows the accent colour); see <see cref="OnAttachedToVisualTree"/>.
    /// </remarks>
    public PaintCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        BuildColorMap();
        AppTheme.Changed += OnThemeChanged;
        antsTimer.Tick += (_, _) =>
        {
            if (session?.Selection is null && spline.Count == 0) return;
            antsPhase = (antsPhase + 1) % 14;   // 14 = one dash period (4 on + 3 off, in 2-DIP steps)
            InvalidateVisual();
        };
    }

    // ------------------------------------------------------------------------------------------
    //  Public state
    // ------------------------------------------------------------------------------------------

    /// <summary>The session whose image is shown and edited.</summary>
    /// <remarks>
    /// Setting it moves the three event subscriptions (image replaced, pixels changed, selection
    /// changed) from the old session to the new one and rebuilds the bitmap. The room sets it to
    /// null when disposed, which also releases the canvas from the session's events.
    /// </remarks>
    public PaintSession? Session
    {
        get => session;
        set
        {
            if (session is not null)
            {
                session.ImageReplaced -= OnImageReplaced;
                session.PixelsChanged -= OnPixelsChanged;
                session.SelectionChanged -= InvalidateVisual;
            }
            session = value;
            if (session is not null)
            {
                session.ImageReplaced += OnImageReplaced;
                session.PixelsChanged += OnPixelsChanged;
                session.SelectionChanged += InvalidateVisual;
            }
            OnImageReplaced();
        }
    }

    /// <summary>The tool used by the left mouse button.</summary>
    /// <remarks>Changing the tool abandons a spline selection that is still being placed.</remarks>
    public PaintTool Tool
    {
        get => tool;
        set
        {
            if (value != tool) CancelSpline();
            tool = value;
        }
    }

    /// <summary>Backing field of <see cref="Tool"/>.</summary>
    PaintTool tool = PaintTool.Pencil;

    /// <summary>Soft edge given to selections, in screen pixels (0 = crisp, anti-aliased only).</summary>
    /// <remarks>
    /// <para>Converted to columns and rows with the current zoom when a shape is finished, so the
    /// edge looks equally soft in time and in pitch on screen.</para>
    /// <para>Changing it is LIVE for the selection drawn last: as long as that selection has not been
    /// changed by anything else (moved, pasted, Ctrl+A…), its shape is feathered again from the
    /// crisp original (see <see cref="lastShape"/>), so moving the Feather slider shows the result
    /// at once on the canvas instead of only affecting the next selection. When there is no such
    /// selection, a feathered «ghost» box is shown for a moment instead (see
    /// <see cref="ShowParameterPreview"/>).</para>
    /// </remarks>
    public double SelectionFeather
    {
        get => selectionFeather;
        set
        {
            if (Math.Abs(selectionFeather - value) < 1e-9) return;
            selectionFeather = value;
            RefeatherLastSelection();
        }
    }

    /// <summary>Backing field of <see cref="SelectionFeather"/>.</summary>
    double selectionFeather;

    /// <summary>
    /// The last selection shape as drawn (crisp, before the feather), the selection it was combined
    /// with, the combine mode and the zoom at that moment: enough to rebuild the result with another
    /// feather. <see cref="lastCommitted"/> is the mask that was produced; when the session's
    /// selection is no longer that exact object, something else changed it and the live feather stops.
    /// </summary>
    SelectionMask? lastShape, lastBase, lastCommitted;

    /// <summary>Combine mode of <see cref="lastShape"/> (see <see cref="CommitSelection"/>).</summary>
    SelectionCombine lastMode;

    /// <summary>Zoom (DIP per column, per row) when <see cref="lastShape"/> was committed.</summary>
    (double X, double Y) lastZoom;

    /// <summary>
    /// True while the session's selection is still the one drawn last on this canvas, so a feather
    /// change re-shapes it live (the room shows the feather ghost only when this is false).
    /// </summary>
    public bool FeatherIsLive => session is not null && image is not null && lastShape is not null
                                 && ReferenceEquals(session.Selection, lastCommitted);

    /// <summary>
    /// Re-applies the current <see cref="SelectionFeather"/> to the selection drawn last, if it is
    /// still the session's selection (see <see cref="FeatherIsLive"/>); otherwise does nothing.
    /// </summary>
    void RefeatherLastSelection()
    {
        if (!FeatherIsLive || lastShape is null || image is null || session is null) return;
        var shape = selectionFeather > 0
            ? lastShape.Feathered(selectionFeather / lastZoom.X, selectionFeather / lastZoom.Y, image.Width, image.Height)
            : lastShape;
        lastCommitted = SelectionMask.Combine(lastBase, shape, lastMode);
        session.Selection = lastCommitted;
    }

    /// <summary>What <see cref="ShowParameterPreview"/> draws.</summary>
    public enum ParameterPreview
    {
        /// <summary>Nothing (no preview is showing).</summary>
        None,
        /// <summary>The brush footprint: size, hardness, opacity, pan colour and noise grain.</summary>
        Brush,
        /// <summary>A box with the soft edge of the current <see cref="SelectionFeather"/>.</summary>
        Feather,
    }

    /// <summary>The preview being shown (None when hidden).</summary>
    ParameterPreview preview;

    /// <summary>When the preview disappears (UTC).</summary>
    DateTime previewUntil;

    /// <summary>Hides the preview when its time is up (ticks only while a preview is visible).</summary>
    DispatcherTimer? previewTimer;

    /// <summary>
    /// Shows, for about 1.5 s, what a palette parameter looks like ON the canvas: the room calls it
    /// whenever the user moves a brush slider (size, opacity, hardness, pan, noise) or the feather.
    /// </summary>
    /// <param name="what">Brush footprint or feather box.</param>
    /// <remarks>
    /// The sliders sit outside the canvas, so the mouse is not over it and the usual brush circle
    /// (drawn under the pointer) is not visible. The preview is drawn at the centre of the visible
    /// image area (or under the pointer when it is over the canvas), at the real on-screen size the
    /// brush will paint with, so «Size 40» really looks 40 DIP wide. Each call restarts the timer,
    /// so the preview stays up while the slider keeps moving.
    /// </remarks>
    public void ShowParameterPreview(ParameterPreview what)
    {
        preview = what;
        previewUntil = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
        if (previewTimer is null)
        {
            previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            previewTimer.Tick += (_, _) =>
            {
                if (DateTime.UtcNow < previewUntil) return;
                preview = ParameterPreview.None;
                previewTimer!.Stop();
                InvalidateVisual();
            };
        }
        previewTimer.Start();
        InvalidateVisual();
    }

    /// <summary>Number of points of the spline selection being placed (0 = none; for tests and the status line).</summary>
    public int SplinePointCount => spline.Count;

    /// <summary>True for the four selection tools.</summary>
    /// <param name="t">A tool.</param>
    public static bool IsSelectionTool(PaintTool t) => t is PaintTool.Select or PaintTool.SelectEllipse or PaintTool.SelectLasso or PaintTool.SelectSpline;

    /// <summary>Brush parameters (size in screen pixels, opacity, hardness, pan, harmonics…).</summary>
    public BrushSettings Brush { get; set; } = new();

    /// <summary>Draw the noise layer as a grainy, desaturated texture (see the file header). Default on.</summary>
    /// <remarks>Setting it recolours the whole bitmap.</remarks>
    public bool ShowNoise
    {
        get => showNoise;
        set
        {
            if (showNoise == value) return;
            showNoise = value;
            if (image is not null) UpdateBitmap(0, image.Width);
            InvalidateVisual();
        }
    }

    /// <summary>Backing field of <see cref="ShowNoise"/>.</summary>
    bool showNoise = true;

    /// <summary>Column being heard while playing (draws the play line), or <c>null</c>.</summary>
    /// <remarks>Fractional column; the room updates it from a UI timer that polls the player's position.</remarks>
    public double? PlayColumn
    {
        get => playColumn;
        set
        {
            if (playColumn == value) return;
            playColumn = value;
            InvalidateVisual();
        }
    }

    /// <summary>Backing field of <see cref="PlayColumn"/>.</summary>
    double? playColumn;

    /// <summary>Column where playback starts (the «play from» cursor, set by clicking the time ruler).</summary>
    /// <remarks>
    /// Fractional column clamped to 0 … image width. Every assignment raises <see cref="CursorMoved"/>
    /// (even when the value did not change) and repaints.
    /// </remarks>
    public double CursorColumn
    {
        get => cursorColumn;
        set
        {
            cursorColumn = Math.Clamp(value, 0, image?.Width ?? 0);
            CursorMoved?.Invoke();
            InvalidateVisual();
        }
    }

    /// <summary>Backing field of <see cref="CursorColumn"/>.</summary>
    double cursorColumn;

    /// <summary>Pixels per column (time zoom).</summary>
    public double ZoomX => zoomX;

    /// <summary>Pixels per row (pitch zoom).</summary>
    public double ZoomY => zoomY;

    /// <summary>First visible column (fractional); clamped to 0 … <see cref="MaxScrollX"/>, raises <see cref="ViewChanged"/>.</summary>
    public double ScrollX
    {
        get => scrollX;
        set { scrollX = Math.Clamp(value, 0, MaxScrollX); ViewChanged?.Invoke(); InvalidateVisual(); }
    }

    /// <summary>Rows hidden above the view (fractional; 0 = the top row is visible); clamped to 0 … <see cref="MaxScrollY"/>, raises <see cref="ViewChanged"/>.</summary>
    public double ScrollY
    {
        get => scrollY;
        set { scrollY = Math.Clamp(value, 0, MaxScrollY); ViewChanged?.Invoke(); InvalidateVisual(); }
    }

    /// <summary>Largest <see cref="ScrollX"/>.</summary>
    public double MaxScrollX => image is null ? 0 : Math.Max(0, image.Width - VisibleColumns);

    /// <summary>Largest <see cref="ScrollY"/>.</summary>
    public double MaxScrollY => image is null ? 0 : Math.Max(0, image.Height - VisibleRows);

    /// <summary>Columns that fit in the view (fractional, at least 1; the area right of the pitch ruler ÷ <see cref="ZoomX"/>).</summary>
    public double VisibleColumns => Math.Max(1, (Bounds.Width - RulerWidth) / zoomX);

    /// <summary>Rows that fit in the view (fractional, at least 1; the area below the time ruler ÷ <see cref="ZoomY"/>).</summary>
    public double VisibleRows => Math.Max(1, (Bounds.Height - RulerHeight) / zoomY);

    /// <summary>Raised when zoom or scroll changed (the room syncs its scroll bars).</summary>
    public event Action? ViewChanged;

    /// <summary>
    /// Largest width a freehand stroke may grow the image to; 0 (default) = strokes never grow it.
    /// </summary>
    /// <remarks>
    /// Used by the Image Sequencer so a pattern gets longer while the user paints past its end. When
    /// it is larger than the image, <see cref="ZoomToFit"/> leaves some empty room after the last
    /// column (marked by a dashed line) to paint into. During the stroke the view does not move
    /// (moving it under a still pointer would keep growing the image); it is refitted on release.
    /// </remarks>
    public int GrowLimit { get; set; }

    /// <summary>Raised after a stroke that made the image wider has ended (the room refreshes lengths and labels).</summary>
    public event Action? Grown;

    /// <summary>True when the stroke in progress has made the image wider (refit + <see cref="Grown"/> on release).</summary>
    bool grewThisStroke;

    /// <summary>Columns of empty room <see cref="ZoomToFit"/> leaves after the image when growing is possible.</summary>
    int GrowRoom => image is null || GrowLimit <= image.Width ? 0 : Math.Min(GrowLimit - image.Width, Math.Max(4, image.Width / 4));

    /// <summary>Grows the image (stroke in progress) so the column under <paramref name="x"/> exists.</summary>
    /// <param name="x">Pointer column (fractional, may be past the end).</param>
    void GrowTo(double x)
    {
        if (GrowLimit <= 0 || session is null || image is null || x < image.Width) return;
        int need = Math.Min(GrowLimit, (int)Math.Floor(x) + 1);
        if (session.Brushes.Grow(need)) grewThisStroke = true;
    }

    /// <summary>Ends a stroke that grew the image: fits the view to the new width and raises <see cref="Grown"/>.</summary>
    void FinishGrow()
    {
        if (!grewThisStroke) return;
        grewThisStroke = false;
        ZoomToFit();
        Grown?.Invoke();
    }

    /// <summary>Raised with the hover read-out text (time, pitch, levels), or "" when the mouse leaves.</summary>
    public event Action<string>? Hover;

    /// <summary>Raised when <see cref="CursorColumn"/> moved.</summary>
    public event Action? CursorMoved;

    /// <summary>Fits the whole image in the view (next layout pass if the size is not known yet).</summary>
    /// <remarks>
    /// Time is fitted exactly (the whole width shows all columns). Pitch is fitted too, but never
    /// below 0.25 DIP per row, so very tall images may still need vertical scrolling.
    /// </remarks>
    public void ZoomToFit()
    {
        // A control smaller than the rulers + 10 DIP has not been laid out yet: retry on the next size change.
        if (image is null || Bounds.Width <= RulerWidth + 10 || Bounds.Height <= RulerHeight + 10)
        {
            fitPending = true;
            return;
        }
        fitPending = false;
        // With GrowLimit some empty room is left after the last column to paint into.
        zoomX = (Bounds.Width - RulerWidth) / (image.Width + GrowRoom);
        zoomY = Math.Max(0.25, (Bounds.Height - RulerHeight) / image.Height);
        scrollX = 0;
        scrollY = 0;
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Multiplies the time zoom by <paramref name="factor"/>, keeping column <paramref name="anchorColumn"/> under the same screen x.</summary>
    /// <param name="factor">Zoom multiplier (&gt; 1 zooms in).</param>
    /// <param name="anchorColumn">Fractional column to keep in place (the mouse position for Ctrl+wheel); null = the centre of the view.</param>
    /// <remarks>Limits: from half the fit-to-width zoom (the whole image in half the view) up to 64 DIP per column.</remarks>
    public void ZoomTime(double factor, double? anchorColumn = null)
    {
        if (image is null) return;
        // Remember where the anchor is on screen, change the zoom, then choose the scroll that puts
        // the anchor back at the same sx: sx = RulerWidth + (anchor − scrollX)·zoomX solved for scrollX.
        double anchor = anchorColumn ?? scrollX + VisibleColumns / 2;
        double sx = RulerWidth + (anchor - scrollX) * zoomX;
        zoomX = Math.Clamp(zoomX * factor, (Bounds.Width - RulerWidth) / image.Width * 0.5, 64);
        scrollX = Math.Clamp(anchor - (sx - RulerWidth) / zoomX, 0, MaxScrollX);
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Multiplies the pitch zoom by <paramref name="factor"/>, keeping the row under the anchor fixed.</summary>
    /// <param name="factor">Zoom multiplier (&gt; 1 zooms in).</param>
    /// <param name="anchorScreenY">Control y (DIP) to keep fixed; null = the middle of the view.</param>
    /// <remarks>
    /// Limits 0.25 … 48 DIP per row. Works in «rows from the top» (the same frame as
    /// <see cref="ScrollY"/>), so the flip between rows and screen y is not needed here.
    /// </remarks>
    public void ZoomPitch(double factor, double? anchorScreenY = null)
    {
        if (image is null) return;
        double sy = anchorScreenY ?? RulerHeight + VisibleRows * zoomY / 2;
        double rowsFromTop = scrollY + (sy - RulerHeight) / zoomY;
        zoomY = Math.Clamp(zoomY * factor, 0.25, 48);
        scrollY = Math.Clamp(rowsFromTop - (sy - RulerHeight) / zoomY, 0, MaxScrollY);
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Image coordinates (fractional column, fractional row) of a point of this control.</summary>
    /// <param name="p">Point in control DIP.</param>
    /// <returns>(column, row) with row 0 at the bottom; may lie outside the image. (0, 0) without an image.</returns>
    public (double X, double Y) ToImage(Point p) =>
        image is null ? (0, 0) : (scrollX + (p.X - RulerWidth) / zoomX, image.Height - scrollY - (p.Y - RulerHeight) / zoomY);

    /// <summary>Control coordinates of image point (x, y) (inverse of <see cref="ToImage"/>).</summary>
    /// <param name="x">Fractional column.</param>
    /// <param name="y">Fractional row (0 = bottom edge of the lowest row).</param>
    /// <returns>Point in control DIP; <c>default</c> without an image.</returns>
    public Point ToScreen(double x, double y) =>
        image is null ? default : new Point(RulerWidth + (x - scrollX) * zoomX, RulerHeight + (image.Height - y - scrollY) * zoomY);

    /// <summary>Brush radii in image units for the current zoom (a round brush stays round on screen).</summary>
    /// <returns>(columns, rows): half of <see cref="BrushSettings.Size"/> (screen DIP) divided by each zoom, at least half a cell.</returns>
    public (double Rx, double Ry) BrushRadii() => (Math.Max(0.5, Brush.Size / 2 / zoomX), Math.Max(0.5, Brush.Size / 2 / zoomY));

    // ------------------------------------------------------------------------------------------
    //  Image ↔ bitmap
    // ------------------------------------------------------------------------------------------

    /// <summary>The session's image object changed: rebuilds the one-pixel-per-cell bitmap and fits the view when the size changed.</summary>
    /// <remarks>
    /// Handler of <c>PaintSession.ImageReplaced</c> (UI thread), also called directly when
    /// <see cref="Session"/> is set. The bitmap is 96 DPI so that one bitmap pixel = one DIP before
    /// scaling; the zoom then does all the scaling in <see cref="Render"/>.
    /// </remarks>
    void OnImageReplaced()
    {
        CancelSpline();
        var old = image;
        image = session?.Image;
        bitmap?.Dispose();
        bitmap = null;
        if (image is not null)
        {
            bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            UpdateBitmap(0, image.Width);
            // Keep the zoom when only the pixels were replaced with an image of the same shape (undo),
            // and while a stroke is growing the image (the view must stay still under the pointer).
            if (old is null || (old.Width != image.Width || old.Height != image.Height) && drag != Drag.Stroke) ZoomToFit();
            cursorColumn = Math.Min(cursorColumn, image.Width);
        }
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Pixels changed: recolours those columns of the bitmap and repaints.</summary>
    /// <param name="x0">First changed column (inclusive).</param>
    /// <param name="x1">End of the changed columns (exclusive).</param>
    void OnPixelsChanged(int x0, int x1)
    {
        UpdateBitmap(x0, x1);
        InvalidateVisual();
    }

    /// <summary>Theme switched: rebuilds the colour map and recolours the whole bitmap.</summary>
    void OnThemeChanged()
    {
        BuildColorMap();
        if (image is not null) UpdateBitmap(0, image.Width);
        InvalidateVisual();
    }

    /// <summary>
    /// Builds the 256-entry colour map for equal layers from the theme: black → AccentDeep → Accent
    /// → AccentHi → AccentLight → white, interpolated in RGB; each entry is BGRA, opaque.
    /// </summary>
    /// <remarks>
    /// The stop positions (0, 0.22, 0.5, 0.72, 0.88, 1) keep the ramp monotonic in brightness for the
    /// blue theme presets, so «brighter = louder» holds whatever accent the user picked.
    /// </remarks>
    void BuildColorMap() => mono = ThemeColorMap();

    /// <summary>
    /// The 256-entry colour map for cells whose two layers are equal, built from the current theme
    /// (see <see cref="BuildColorMap"/>). Public so other views (the Image Sequencer's clip
    /// miniatures) colour cells exactly like the canvas.
    /// </summary>
    /// <returns>A new map: index = level·255, value = opaque BGRA (0xAARRGGBB).</returns>
    public static uint[] ThemeColorMap()
    {
        (double T, Color C)[] stops =
        [
            (0, Color.FromRgb(0, 0, 0)), (0.22, AppTheme.ColorOf("AccentDeep")), (0.5, AppTheme.ColorOf("Accent")),
            (0.72, AppTheme.ColorOf("AccentHi")), (0.88, AppTheme.ColorOf("AccentLight")), (1, Color.FromRgb(255, 255, 255)),
        ];
        var map = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            // Find the segment [stops[s], stops[s+1]] containing t, then interpolate linearly inside it.
            int s = 0;
            while (s < stops.Length - 2 && t > stops[s + 1].T) s++;
            var (t0, c0) = stops[s];
            var (t1, c1) = stops[s + 1];
            double u = Math.Clamp((t - t0) / (t1 - t0), 0, 1);
            byte r = (byte)(c0.R + (c1.R - c0.R) * u), g = (byte)(c0.G + (c1.G - c0.G) * u), b = (byte)(c0.B + (c1.B - c0.B) * u);
            map[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        }
        return map;
    }

    /// <summary>The colour of one cell, exactly as the canvas shows it.</summary>
    /// <param name="map">The theme map from <see cref="ThemeColorMap"/>.</param>
    /// <param name="left">Left level 0…1.</param>
    /// <param name="right">Right level 0…1.</param>
    /// <param name="noise">Noisiness 0…1 of the cell, or 0 when there is no noise layer / it is hidden.</param>
    /// <param name="odd">Checkerboard parity of the cell ((x + y) odd), for the noise texture.</param>
    /// <returns>Opaque BGRA (0xAARRGGBB).</returns>
    /// <remarks>Equal layers (within one 8-bit step) use the theme map; different layers are true colour: red = left, green = right, blue = the smaller one.</remarks>
    public static uint CellColor(uint[] map, float left, float right, float noise, bool odd)
    {
        uint px;
        if (MathF.Abs(left - right) < 0.004f)
            px = map[(int)(Math.Clamp(left, 0f, 1f) * 255 + 0.5f)];
        else
        {
            uint rr = (uint)(Math.Clamp(left, 0f, 1f) * 255), gg = (uint)(Math.Clamp(right, 0f, 1f) * 255);
            px = 0xFF000000u | (rr << 16) | (gg << 8) | Math.Min(rr, gg);
        }
        if (noise > 0.02f && (left > 0.004f || right > 0.004f)) px = NoiseTexture(px, noise, odd);
        return px;
    }

    /// <summary>Recolours columns [x0, x1) of the bitmap from the image (bitmap row 0 = top = highest pitch).</summary>
    /// <param name="x0">First column to recolour (inclusive; clamped).</param>
    /// <param name="x1">End column (exclusive; clamped).</param>
    /// <remarks>
    /// Writes straight into the locked framebuffer (unsafe pointer, one uint per pixel; rows are
    /// <c>RowBytes</c> apart, which may exceed width·4). The image is column-major (index x·h + y) and
    /// the bitmap row-major, so each column is a vertical strip of pixel writes; for a stroke this
    /// touches only the few columns it changed.
    /// </remarks>
    unsafe void UpdateBitmap(int x0, int x1)
    {
        if (bitmap is null || image is null) return;
        x0 = Math.Clamp(x0, 0, image.Width);
        x1 = Math.Clamp(x1, 0, image.Width);
        int h = image.Height;
        using var fb = bitmap.Lock();
        byte* basePtr = (byte*)fb.Address;
        var l = image.Left;
        var r = image.Right;
        var nz = showNoise ? image.Noise : null;
        for (int x = x0; x < x1; x++)
        {
            int o = x * h;
            for (int y = 0; y < h; y++)
            {
                // Equal layers → theme map; different → true colour; noise → texture (see CellColor).
                uint px = CellColor(mono, l[o + y], r[o + y], nz?[o + y] ?? 0f, ((x + y) & 1) == 1);
                // Vertical flip: image row y (0 = bottom) → bitmap row h − 1 − y (0 = top).
                *(uint*)(basePtr + (h - 1 - y) * fb.RowBytes + x * 4) = px;
            }
        }
    }

    /// <summary>
    /// Applies the noise-layer texture to one opaque BGRA pixel: desaturate towards its own grey
    /// (Rec. 601 luma) by 0.7·n, and darken the «odd» cells of the checkerboard by 0.4·n.
    /// </summary>
    /// <param name="px">The cell colour (0xAARRGGBB, opaque).</param>
    /// <param name="n">Noisiness 0…1.</param>
    /// <param name="odd">True for the checkerboard cells that get darkened.</param>
    /// <returns>The textured colour, still opaque.</returns>
    /// <remarks>
    /// Plain float maths on three channels: cheap enough for a full-canvas recolour
    /// (≈ 0.5 M cells) in a few milliseconds. The mean brightness over a 2×2 block drops by 0.2·n
    /// at most, so «brighter = louder» still holds.
    /// </remarks>
    static uint NoiseTexture(uint px, float n, bool odd)
    {
        n = Math.Clamp(n, 0f, 1f);
        float rr = (px >> 16) & 0xFF, gg = (px >> 8) & 0xFF, bb = px & 0xFF;
        float grey = 0.299f * rr + 0.587f * gg + 0.114f * bb;
        float s = 0.7f * n;                       // desaturation amount
        float k = odd ? 1 - 0.4f * n : 1;         // checkerboard darkening
        uint R = (uint)Math.Clamp((rr + (grey - rr) * s) * k, 0, 255);
        uint G = (uint)Math.Clamp((gg + (grey - gg) * s) * k, 0, 255);
        uint B = (uint)Math.Clamp((bb + (grey - bb) * s) * k, 0, 255);
        return 0xFF000000u | (R << 16) | (G << 8) | B;
    }

    // ------------------------------------------------------------------------------------------
    //  Rendering
    // ------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>Completes a pending fit; otherwise keeps the zoom and only re-clamps the scroll to the new size.</remarks>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (fitPending) ZoomToFit();
        else
        {
            scrollX = Math.Clamp(scrollX, 0, MaxScrollX);
            scrollY = Math.Clamp(scrollY, 0, MaxScrollY);
            ViewChanged?.Invoke();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Layer order: background → image (one scaled DrawImage) → grid, selection, drag preview,
    /// cursors and brush outline (all clipped to the image area) → the two rulers → the corner
    /// square where the rulers meet (painted last so labels never show through it).
    /// </remarks>
    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.FillRectangle(AppTheme.Brush("BgDeep"), bounds);
        if (image is null || bitmap is null) return;
        // The image area: everything right of the pitch ruler and below the time ruler.
        var area = new Rect(RulerWidth, RulerHeight, Math.Max(0, bounds.Width - RulerWidth), Math.Max(0, bounds.Height - RulerHeight));

        // ---- the image: source rectangle in bitmap pixels (y from the top) → destination.
        // Bitmap y counts from the top just like ScrollY, so the source starts at (scrollX, scrollY).
        // When the image is smaller than the view, only its own extent is drawn (the rest stays BgDeep).
        double visCols = Math.Min(image.Width - scrollX, area.Width / zoomX);
        double visRows = Math.Min(image.Height - scrollY, area.Height / zoomY);
        if (visCols > 0 && visRows > 0)
        {
            var src = new Rect(scrollX, scrollY, visCols, visRows);
            var dst = new Rect(area.X, area.Y, visCols * zoomX, visRows * zoomY);
            var mode = zoomX >= 1 && zoomY >= 1 ? BitmapInterpolationMode.None : BitmapInterpolationMode.MediumQuality;
            using (ctx.PushClip(area))
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
                ctx.DrawImage(bitmap, src, dst);
        }

        using (ctx.PushClip(area))
        {
            DrawGrid(ctx, area);
            DrawGrowMarker(ctx, area);
            DrawSelection(ctx);
            DrawDragPreview(ctx);
            DrawCursors(ctx, area);
            DrawBrushOutline(ctx);
        }
        DrawPitchRuler(ctx, bounds);
        DrawTimeRuler(ctx, bounds);
        ctx.FillRectangle(AppTheme.Brush("Surface"), new Rect(0, 0, RulerWidth, RulerHeight));
    }

    /// <summary>When strokes can grow the image (<see cref="GrowLimit"/>): a dashed line at its end and a hint in the empty room after it.</summary>
    /// <param name="ctx">Drawing context (already clipped to the image area).</param>
    /// <param name="area">The image area in control DIP.</param>
    void DrawGrowMarker(DrawingContext ctx, Rect area)
    {
        if (GrowLimit <= image!.Width) return;
        double sx = ToScreen(image.Width, 0).X;
        if (sx < area.Left || sx > area.Right) return;
        ctx.DrawLine(new Pen(AppTheme.Brush("Accent"), 1.5, new DashStyle([4, 4], 0)), new Point(sx, area.Top), new Point(sx, area.Bottom));
        double room = area.Right - sx;
        if (room < 60) return;
        var ft = new FormattedText(Loc.S("pinta aquí\npara alargar", "paint here\nto lengthen"), System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Face, 11, AppTheme.Brush("TextDim")) { TextAlignment = TextAlignment.Center, MaxTextWidth = room - 8 };
        ctx.DrawText(ft, new Point(sx + 4, area.Top + 8));
    }

    /// <summary>Faint lines at period starts (C rows / octaves of C on continuous axes) and at each time-ruler tick.</summary>
    /// <param name="ctx">Drawing context (already clipped to the image area).</param>
    /// <param name="area">The image area in control DIP.</param>
    /// <remarks>
    /// On discrete axes the line sits at the BOTTOM edge of each period-start row (row y spans
    /// [y, y+1)); it becomes the stronger «Line» colour once rows are at least 3 DIP tall. On
    /// continuous axes the line goes through the CENTRE of the row nearest to each C.
    /// The vertical lines use the same step as <see cref="DrawTimeRuler"/>.
    /// </remarks>
    void DrawGrid(DrawingContext ctx, Rect area)
    {
        var pen = new Pen(AppTheme.Brush("Grid"), 1);
        var strong = new Pen(AppTheme.Brush("Line"), 1);
        var axis = image!.Axis;
        if (axis.IsDiscrete)
        {
            for (int y = 0; y < image.Height; y++)
            {
                if (!axis.IsPeriodStart(y)) continue;
                double sy = ToScreen(0, y).Y;
                if (sy < area.Top || sy > area.Bottom) continue;
                ctx.DrawLine(zoomY >= 3 ? strong : pen, new Point(area.Left, sy), new Point(area.Right, sy));
            }
        }
        else
        {
            // Octaves of C (C0 = 16.35 Hz) on continuous axes.
            for (double f = 16.351597831287414; f < axis.HighHz; f *= 2)
            {
                if (f < axis.LowHz) continue;
                double sy = ToScreen(0, axis.RowOf(f) + 0.5).Y;
                if (sy >= area.Top && sy <= area.Bottom)
                    ctx.DrawLine(pen, new Point(area.Left, sy), new Point(area.Right, sy));
            }
        }
        // 90 DIP / zoomX = columns per 90 DIP; × seconds per column = seconds per ~90 DIP → rounded up
        // to a nice step, so ticks are at least ~90 DIP apart at any zoom.
        double step = NiceStep(image.SecondsPerColumn * 90 / zoomX);
        for (double t = 0; t <= image.DurationSeconds + 1e-9; t += step)
        {
            double sx = ToScreen(t / image.SecondsPerColumn, 0).X;
            if (sx >= area.Left && sx <= area.Right)
                ctx.DrawLine(pen, new Point(sx, area.Top), new Point(sx, area.Bottom));
        }
    }

    /// <summary>
    /// Draws the selection: a translucent fill and the marching ants along the mask's real outline
    /// (offset by the drag while it is being moved).
    /// </summary>
    /// <param name="ctx">Drawing context (clipped to the image area).</param>
    /// <remarks>
    /// The outline loops are in image units (cell corners, see <see cref="SelectionMask.Outline"/>);
    /// each point goes through <see cref="ToScreen"/>. Holes of a subtracted selection run the other
    /// way round, so the non-zero fill leaves them unfilled.
    /// </remarks>
    void DrawSelection(DrawingContext ctx)
    {
        if (session?.Selection is not { } s) return;
        var geo = LoopsToScreen(s.Outline, true);
        // While moving, preview the offset in WHOLE cells (as MoveSelection will apply it); rows grow
        // upwards, so a positive row offset moves the outline up (negative screen y).
        var shift = drag == Drag.SelectMove
            ? Matrix.CreateTranslation(Math.Round(dragNow.X - dragStart.X) * zoomX, -Math.Round(dragNow.Y - dragStart.Y) * zoomY)
            : Matrix.Identity;
        using (ctx.PushTransform(shift))
        {
            // A crisp rectangle is filled with its outline; any other shape is filled from its real
            // coverage so a feathered edge is SEEN fading out (the outline alone sits at 50 %).
            if (s.IsRectangle || CoverageBitmap(s) is not { } cov)
                ctx.DrawGeometry(SelectionTintBrush, null, geo);
            else
            {
                var b = s.Bounds;
                var dst = new Rect(ToScreen(b.X0, b.Y1), ToScreen(b.X1, b.Y0));
                using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality }))
                    ctx.DrawImage(cov, new Rect(0, 0, b.Width, b.Height), dst);
            }
            DrawAnts(ctx, geo);
        }
    }

    /// <summary>
    /// Overlay colour of selections: a translucent PINK, the same in every theme.
    /// </summary>
    /// <remarks>
    /// The theme's «Selection» colour is a lighter shade of the accent, and the picture itself is
    /// drawn in accent shades, so a selection used to look merely «brighter» and was hard to tell
    /// from loud paint. Pink is not used by any theme's colour map, so it always reads as an overlay.
    /// </remarks>
    static readonly Color SelectionTint = Color.FromArgb(0x6C, 0xFF, 0x4F, 0xB4);

    /// <summary>Brush of <see cref="SelectionTint"/> (for crisp rectangles and the outline fill).</summary>
    static readonly IBrush SelectionTintBrush = new SolidColorBrush(SelectionTint).ToImmutable();

    /// <summary>The mask the cached <see cref="coverageBitmap"/> was built for.</summary>
    SelectionMask? coverageFor;

    /// <summary>Colour the cached coverage bitmap was tinted with (rebuilt if it ever changes).</summary>
    Color coverageColor;

    /// <summary>One pixel per cell of the selection's bounds: <see cref="SelectionTint"/> × the cell's coverage.</summary>
    WriteableBitmap? coverageBitmap;

    /// <summary>
    /// The selection's soft coverage as a tinted bitmap (cached per mask object and theme colour).
    /// </summary>
    /// <param name="s">A non-rectangular selection.</param>
    /// <returns>The bitmap (row 0 = the TOP row of the bounds, like the image bitmap), or <c>null</c> when the bounds are empty.</returns>
    /// <remarks>
    /// Built once per selection change (a new mask object), O(cells of the bounds): even a selection
    /// over a whole 4096-column canvas takes a few milliseconds, and nothing is rebuilt while only
    /// scrolling or zooming. Premultiplied BGRA: every channel scales with the coverage.
    /// </remarks>
    unsafe WriteableBitmap? CoverageBitmap(SelectionMask s)
    {
        var color = SelectionTint;
        if (ReferenceEquals(s, coverageFor) && color == coverageColor && coverageBitmap is not null) return coverageBitmap;
        var b = s.Bounds;
        if (b.IsEmpty) return null;
        coverageBitmap?.Dispose();
        coverageBitmap = new WriteableBitmap(new PixelSize(b.Width, b.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = coverageBitmap.Lock())
        {
            byte* basePtr = (byte*)fb.Address;
            for (int row = 0; row < b.Height; row++)
            {
                int y = b.Y1 - 1 - row;   // bitmap rows run top-down, image rows bottom-up
                uint* line = (uint*)(basePtr + row * fb.RowBytes);
                for (int x = 0; x < b.Width; x++)
                {
                    float c = s[b.X0 + x, y];
                    double a = color.A / 255.0 * c;
                    byte A = (byte)Math.Round(255 * a);
                    byte R = (byte)Math.Round(color.R * a), G = (byte)Math.Round(color.G * a), B = (byte)Math.Round(color.B * a);
                    line[x] = (uint)(B | G << 8 | R << 16 | A << 24);
                }
            }
        }
        coverageFor = s;
        coverageColor = color;
        return coverageBitmap;
    }

    /// <summary>Marching ants: a dark solid line under a light dashed one whose offset follows <see cref="antsPhase"/>.</summary>
    /// <param name="ctx">Drawing context.</param>
    /// <param name="geo">The outline in screen coordinates.</param>
    void DrawAnts(DrawingContext ctx, Geometry geo)
    {
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 1), geo);
        ctx.DrawGeometry(null, new Pen(Brushes.White, 1, new DashStyle([4, 3], -antsPhase / 2)), geo);
    }

    /// <summary>Builds a screen-space geometry from loops (or one open polyline) given in image units.</summary>
    /// <param name="loops">Point lists in image units.</param>
    /// <param name="closed">Close each figure (last point back to the first).</param>
    /// <returns>A stream geometry with one figure per loop (non-zero fill).</returns>
    StreamGeometry LoopsToScreen(IEnumerable<IReadOnlyList<(double X, double Y)>> loops, bool closed)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        g.SetFillRule(FillRule.NonZero);
        foreach (var loop in loops)
        {
            if (loop.Count < 2) continue;
            g.BeginFigure(ToScreen(loop[0].X, loop[0].Y), closed);
            for (int i = 1; i < loop.Count; i++) g.LineTo(ToScreen(loop[i].X, loop[i].Y));
            g.EndFigure(closed);
        }
        return geo;
    }

    /// <summary>Rubber-band preview of the shape, line, gradient or new selection being dragged.</summary>
    /// <param name="ctx">Drawing context (clipped to the image area).</param>
    /// <remarks>
    /// Only a preview: nothing is written to the image until <see cref="OnPointerReleased"/>. The
    /// line preview uses the brush size in DIP as its pen width; the gradient shows its start point
    /// (filled dot) and end point (ring).
    /// </remarks>
    void DrawDragPreview(DrawingContext ctx)
    {
        DrawSplinePreview(ctx);
        if (drag is not (Drag.Shape or Drag.SelectNew)) return;
        var a = ToScreen(dragStart.X, dragStart.Y);
        var b = ToScreen(dragNow.X, dragNow.Y);
        var pen = new Pen(AppTheme.Brush("AccentLight"), 1.5);
        if (drag == Drag.SelectNew)
        {
            Geometry geo = dragTool switch
            {
                PaintTool.SelectEllipse => new EllipseGeometry(new Rect(a, ToScreen(EllipseEnd().X, EllipseEnd().Y))),
                PaintTool.SelectLasso => LoopsToScreen([lasso.Append(dragNow).ToList()], true),
                _ => new RectangleGeometry(new Rect(a, b)),
            };
            DrawAnts(ctx, geo);
            return;
        }
        switch (dragTool)
        {
            case PaintTool.Line:
                ctx.DrawLine(new Pen(AppTheme.Brush("AccentLight"), Math.Max(1.5, Brush.Size)), a, b);
                break;
            case PaintTool.Rectangle:
                ctx.DrawRectangle(AppTheme.Brush("Accent_40"), pen, new Rect(a, b));
                break;
            case PaintTool.Ellipse:
                ctx.DrawEllipse(AppTheme.Brush("Accent_40"), pen, new Rect(a, b));
                break;
            case PaintTool.Gradient:
                ctx.DrawLine(pen, a, b);
                ctx.DrawEllipse(AppTheme.Brush("AccentLight"), null, a, 4, 4);
                ctx.DrawEllipse(null, pen, b, 4, 4);
                break;
        }
    }

    /// <summary>
    /// The spline selection being placed: the closed Catmull-Rom curve through the points and the
    /// mouse position (the point the next click would add), and a small square on every point.
    /// </summary>
    /// <param name="ctx">Drawing context (clipped to the image area).</param>
    /// <remarks>
    /// The curve is sampled in IMAGE units (exactly the curve the mask will fill) with enough
    /// samples for the zoom (about one per 3 DIP), then converted with <see cref="ToScreen"/>.
    /// </remarks>
    void DrawSplinePreview(DrawingContext ctx)
    {
        if (spline.Count == 0) return;
        var pts = new List<(double X, double Y)>(spline);
        if (hover is { } hp && hp.X >= RulerWidth && hp.Y >= RulerHeight) pts.Add(ToImage(hp));
        var curve = SelectionMask.CatmullRom(pts, closed: pts.Count >= 3, samplesPerUnit: Math.Max(1, Math.Max(zoomX, zoomY) / 3));
        DrawAnts(ctx, LoopsToScreen([curve], pts.Count >= 3));
        var fill = AppTheme.Brush("AccentLight");
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)), 1);
        foreach (var (x, y) in spline)
        {
            var c = ToScreen(x, y);
            ctx.DrawRectangle(fill, edge, new Rect(c.X - 3, c.Y - 3, 6, 6));
        }
    }

    /// <summary>
    /// The far corner of the ellipse selection being dragged, in image units: the pointer, or — with
    /// the circle constraint — the corner that makes the box square ON SCREEN.
    /// </summary>
    /// <returns>(column, row) of the corner opposite <see cref="dragStart"/>.</returns>
    /// <remarks>
    /// Screen ↔ image: the drag vector is measured in DIP (columns × ZoomX, rows × ZoomY), the larger
    /// of |dx| and |dy| is used for both with their own signs, and the result is converted back with
    /// <see cref="ToImage"/>. Circle = Shift down now, unless that Shift has been held since the press
    /// (then it meant «add»).
    /// </remarks>
    (double X, double Y) EllipseEnd()
    {
        bool circle = shiftNow && (!shiftAtPress || shiftReleased);
        if (!circle) return dragNow;
        var a = ToScreen(dragStart.X, dragStart.Y);
        var b = ToScreen(dragNow.X, dragNow.Y);
        double dx = b.X - a.X, dy = b.Y - a.Y, side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return ToImage(new Point(a.X + (dx < 0 ? -side : side), a.Y + (dy < 0 ? -side : side)));
    }

    /// <summary>Selection combine mode from the keyboard modifiers of a press: Shift = add, Alt = subtract, none = replace.</summary>
    /// <param name="m">The modifiers.</param>
    static SelectionCombine ModeOf(KeyModifiers m) =>
        m.HasFlag(KeyModifiers.Alt) ? SelectionCombine.Subtract : m.HasFlag(KeyModifiers.Shift) ? SelectionCombine.Add : SelectionCombine.Replace;

    /// <summary>
    /// Finishes a new selection shape: softens it by <see cref="SelectionFeather"/> (screen pixels →
    /// columns and rows with the current zoom) and combines it with the current selection.
    /// </summary>
    /// <param name="shape">The new shape, or <c>null</c> when nothing was drawn.</param>
    /// <param name="mode">Replace / add / subtract.</param>
    void CommitSelection(SelectionMask? shape, SelectionCombine mode)
    {
        if (session is null || image is null) return;
        // Remember the crisp shape and its context so a later feather change can rebuild it live.
        lastShape = shape;
        lastBase = session.Selection;
        lastMode = mode;
        lastZoom = (zoomX, zoomY);
        if (shape is not null && SelectionFeather > 0)
            shape = shape.Feathered(SelectionFeather / zoomX, SelectionFeather / zoomY, image.Width, image.Height);
        lastCommitted = SelectionMask.Combine(session.Selection, shape, mode);
        // One undo step per drawn shape; the live feather later re-shapes it WITHOUT new steps.
        session.ChangeSelection(lastCommitted);
    }

    /// <summary>
    /// Keys for a spline selection in progress (the room calls this before its own shortcuts):
    /// Enter closes it, Esc cancels it, Backspace removes the last point.
    /// </summary>
    /// <param name="key">The key pressed.</param>
    /// <returns>True when the key was used (only while a spline is being placed).</returns>
    public bool HandleSelectionKey(Key key)
    {
        if (spline.Count == 0) return false;
        switch (key)
        {
            case Key.Enter or Key.Return: CloseSpline(); return true;
            case Key.Escape: CancelSpline(); return true;
            case Key.Back:
                spline.RemoveAt(spline.Count - 1);
                InvalidateVisual();
                return true;
        }
        return false;
    }

    /// <summary>Closes the spline being placed into a selection (needs 3 points; fewer just cancels).</summary>
    public void CloseSpline()
    {
        if (image is not null && spline.Count >= 3)
            CommitSelection(SelectionMask.Spline(spline, image.Width, image.Height), splineMode);
        CancelSpline();
    }

    /// <summary>Abandons the spline being placed (the current selection is kept).</summary>
    public void CancelSpline()
    {
        if (spline.Count == 0) return;
        spline.Clear();
        InvalidateVisual();
    }

    /// <summary>The play-from cursor (dashed, warning colour) and, while playing, the play line (solid, «Good» colour).</summary>
    /// <param name="ctx">Drawing context (clipped to the image area).</param>
    /// <param name="area">The image area (lines outside it are skipped).</param>
    void DrawCursors(DrawingContext ctx, Rect area)
    {
        double cx = ToScreen(cursorColumn, 0).X;
        if (cx >= area.Left && cx <= area.Right)
            ctx.DrawLine(new Pen(AppTheme.Brush("Warn"), 1, new DashStyle([3, 3], 0)), new Point(cx, area.Top), new Point(cx, area.Bottom));
        if (playColumn is { } pc)
        {
            double px = ToScreen(pc, 0).X;
            if (px >= area.Left && px <= area.Right)
                ctx.DrawLine(new Pen(AppTheme.Brush("Good"), 2), new Point(px, area.Top), new Point(px, area.Bottom));
        }
    }

    /// <summary>Circle of the brush size under the mouse; for the harmonics brush also ticks where the partials k·f will land.</summary>
    /// <param name="ctx">Drawing context (clipped to the image area).</param>
    /// <remarks>
    /// Hidden over the rulers, while panning or dragging the cursor, and for tools that have no round
    /// footprint (select, rectangle, ellipse, gradient). The radius is in screen DIP (the brush size is
    /// defined on screen, see <see cref="BrushRadii"/>). Harmonic ticks: the fundamental is the
    /// frequency under the mouse; partial k sits at row RowOf(k·f) — up to <c>Harmonics − 1</c>
    /// overtones, odd ones only when <c>OddOnly</c>, stopping above the top of the image.
    /// </remarks>
    void DrawBrushOutline(DrawingContext ctx)
    {
        // A parameter preview (a palette slider just moved) wins over the normal outline: it is
        // drawn under the pointer when the pointer is over the image, else at the view's centre.
        if (preview != ParameterPreview.None)
        {
            var area = new Rect(RulerWidth, RulerHeight, Math.Max(0, Bounds.Width - RulerWidth), Math.Max(0, Bounds.Height - RulerHeight));
            var at = hover is { } hp0 && area.Contains(hp0) ? hp0 : area.Center;
            if (preview == ParameterPreview.Brush) DrawBrushFootprint(ctx, at, labelled: true);
            else DrawFeatherGhost(ctx, at);
            return;
        }
        if (hover is not { } p || drag is Drag.Pan or Drag.Ruler || p.X < RulerWidth || p.Y < RulerHeight) return;
        if (IsSelectionTool(Tool) || Tool is PaintTool.Rectangle or PaintTool.Ellipse or PaintTool.Gradient) return;
        double r = Math.Max(2, Brush.Size / 2);
        var pen = new Pen(AppTheme.Brush("Text"), 1);
        ctx.DrawEllipse(null, pen, p, r, r);
        // Inner dashed ring = where the hardness core ends and the soft edge begins.
        if (Brush.Hardness is > 0.02 and < 0.98 && r > 6)
            ctx.DrawEllipse(null, new Pen(AppTheme.Brush("TextDim"), 1, new DashStyle([2, 3], 0)), p, r * Brush.Hardness, r * Brush.Hardness);
        if (Tool == PaintTool.Harmonics && image is not null)
        {
            // Small ticks where the harmonics will land.
            var (x, y) = ToImage(p);
            double f = image.Axis.FrequencyAt(y);
            int made = 0;
            for (int k = 2; made < Math.Max(0, Brush.Harmonics - 1) && k <= 256; k++)
            {
                if (Brush.OddOnly && k % 2 == 0) continue;
                made++;
                double row = image.Axis.RowOf(f * k);
                if (row > image.Height) break;
                var hp = ToScreen(x, row);
                ctx.DrawLine(pen, new Point(hp.X - r, hp.Y), new Point(hp.X + r, hp.Y));
            }
        }
    }

    /// <summary>
    /// The brush as it will paint, at its real on-screen size: a filled disc whose solid core is the
    /// hardness, whose brightness is the opacity, whose colour is the pan (red = left only, green =
    /// right only, theme accent = both) and which is speckled when the brush paints noise.
    /// </summary>
    /// <param name="ctx">Drawing context (clipped to the image area).</param>
    /// <param name="c">Centre in control DIP.</param>
    /// <param name="labelled">Also write the values under the disc (for the slider preview).</param>
    /// <remarks>
    /// The radial gradient mirrors <c>BrushEngine.EdgeCoverage</c>: coverage 1 up to
    /// hardness·radius, then a smooth fall to 0 at the radius (the cosine shape is approximated with
    /// a few gradient stops). The airbrush is Gaussian in reality; the preview still shows its size.
    /// </remarks>
    void DrawBrushFootprint(DrawingContext ctx, Point c, bool labelled)
    {
        double r = Math.Max(2, Brush.Size / 2);
        double hard = Math.Clamp(Brush.Hardness, 0, 1), op = Math.Clamp(Brush.Opacity, 0, 1);
        var accent = AppTheme.ColorOf("Accent");
        var side = Brush.Pan < 0 ? Color.FromRgb(255, 70, 70) : Color.FromRgb(70, 225, 110);
        double t = Math.Abs(Math.Clamp(Brush.Pan, -1, 1));
        var col = Color.FromRgb((byte)(accent.R + (side.R - accent.R) * t), (byte)(accent.G + (side.G - accent.G) * t), (byte)(accent.B + (side.B - accent.B) * t));
        Color At(double coverage) => Color.FromArgb((byte)Math.Round(255 * op * coverage), col.R, col.G, col.B);

        var stops = new GradientStops { new GradientStop(At(1), 0), new GradientStop(At(1), hard) };
        // Cosine fall from the core to the rim: 0.5·(1 + cos(π·u)), u = 0…1 across the soft band.
        for (int i = 1; i <= 4; i++)
        {
            double u = i / 4.0;
            stops.Add(new GradientStop(At(0.5 * (1 + Math.Cos(Math.PI * u))), hard + (1 - hard) * u));
        }
        var fill = new RadialGradientBrush
        {
            GradientStops = stops,
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
        };
        ctx.DrawEllipse(fill, new Pen(AppTheme.Brush("Text"), 1), c, r, r);
        if (hard is > 0.02 and < 0.98 && r > 6)
            ctx.DrawEllipse(null, new Pen(AppTheme.Brush("TextDim"), 1, new DashStyle([2, 3], 0)), c, r * hard, r * hard);

        // Noise grain: dark specks inside the disc, as many as the noise amount (stable pattern, so
        // the preview does not flicker while the slider moves).
        if (Brush.CreatesNoise)
        {
            var rng = new Random(12345);
            var speck = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0));
            int n = (int)(Math.Clamp(Brush.Noise, 0, 1) * Math.Min(600, r * r * 0.6));
            for (int i = 0; i < n; i++)
            {
                double a = rng.NextDouble() * 2 * Math.PI, d = Math.Sqrt(rng.NextDouble()) * r;
                ctx.FillRectangle(speck, new Rect(c.X + Math.Cos(a) * d, c.Y + Math.Sin(a) * d, 1.5, 1.5));
            }
        }

        if (!labelled) return;
        string pan = Math.Abs(Brush.Pan) < 0.01 ? "L+R" : Brush.Pan < 0 ? $"L {-Brush.Pan * 100:0} %" : $"R {Brush.Pan * 100:0} %";
        string text = $"{Brush.Size:0} px · {hard * 100:0} % · {op * 100:0} % · {pan}" + (Brush.CreatesNoise ? $" · {Loc.S("ruido", "noise")} {Brush.Noise * 100:0} %" : "");
        DrawPreviewLabel(ctx, text, new Point(c.X, c.Y + r + 6));
    }

    /// <summary>
    /// The feather preview: a box with exactly the soft edge new selections get (drawn while the
    /// Feather slider moves and there is no live selection to re-shape).
    /// </summary>
    /// <param name="ctx">Drawing context (clipped to the image area).</param>
    /// <param name="c">Centre in control DIP.</param>
    /// <remarks>
    /// <see cref="SelectionMask.Feathered"/> blurs with three box passes of half-width ≈ feather/2,
    /// so the edge fades over about ±1.5·(feather/2) on each side of the drawn line; the ghost draws
    /// that band as concentric strokes, each with the coverage of a smooth S-curve, around a
    /// dashed line marking where the shape was drawn.
    /// </remarks>
    void DrawFeatherGhost(DrawingContext ctx, Point c)
    {
        double f = SelectionFeather;
        var box = new Rect(c.X - 70, c.Y - 45, 140, 90);
        var color = SelectionTint;
        if (f < 0.5)
            ctx.FillRectangle(new SolidColorBrush(color), box);
        else
        {
            double reach = Math.Max(1, 0.75 * f);   // ±1.5 × the box half-width f/2
            int steps = Math.Clamp((int)Math.Ceiling(2 * reach), 2, 48);
            double inner = Math.Min(reach, Math.Min(box.Width, box.Height) / 2 - 1);
            // Core (fully selected) then rings outward from −reach to +reach around the line.
            var core = box.Deflate(inner);
            if (core.Width > 0 && core.Height > 0) ctx.FillRectangle(new SolidColorBrush(color), core);
            for (int i = 0; i < steps; i++)
            {
                double d0 = -inner + (inner + reach) * i / steps, d1 = -inner + (inner + reach) * (i + 1) / steps;
                double u = ((d0 + d1) / 2 + reach) / (2 * reach);           // 0 inside … 1 outside
                double cov = 0.5 * (1 + Math.Cos(Math.PI * Math.Clamp(u, 0, 1)));
                var ring = new SolidColorBrush(Color.FromArgb((byte)(color.A * cov), color.R, color.G, color.B));
                var outer = box.Inflate(d1);
                var geo = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(outer), new RectangleGeometry(box.Inflate(d0)));
                ctx.DrawGeometry(ring, null, geo);
            }
        }
        ctx.DrawRectangle(null, new Pen(AppTheme.Brush("Text"), 1, new DashStyle([4, 3], 0)), box);
        DrawPreviewLabel(ctx, f < 0.5 ? Loc.S("Suavizado: nítido", "Feather: crisp") : $"{Loc.S("Suavizado", "Feather")} {f:0} px", new Point(c.X, box.Bottom + Math.Max(0.75 * f, 0) + 6));
    }

    /// <summary>A small label on a dark backing, centred horizontally on <paramref name="top"/>.</summary>
    /// <param name="ctx">Drawing context.</param>
    /// <param name="text">The text (already localised by the caller).</param>
    /// <param name="top">Top-centre point.</param>
    void DrawPreviewLabel(DrawingContext ctx, string text, Point top)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 11, AppTheme.Brush("Text"));
        var back = new Rect(top.X - ft.Width / 2 - 5, top.Y, ft.Width + 10, ft.Height + 4);
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)), back, 3);
        ctx.DrawText(ft, new Point(back.X + 5, back.Y + 2));
    }

    /// <summary>Left ruler: row labels from the axis, at least ~13 DIP apart; period starts (C rows) in the brighter colour.</summary>
    /// <param name="ctx">Drawing context (not clipped).</param>
    /// <param name="bounds">The whole control.</param>
    void DrawPitchRuler(DrawingContext ctx, Rect bounds)
    {
        var axis = image!.Axis;
        ctx.FillRectangle(AppTheme.Brush("Surface"), new Rect(0, RulerHeight, RulerWidth, bounds.Height - RulerHeight));
        ctx.DrawLine(new Pen(AppTheme.Brush("Line"), 1), new Point(RulerWidth - 0.5, RulerHeight), new Point(RulerWidth - 0.5, bounds.Height));
        var text = AppTheme.Brush("TextDim");
        var bright = AppTheme.Brush("Text");
        // Label every n-th row so labels are at least 15 DIP apart; discrete axes prefer period starts.
        int every = Math.Max(1, (int)Math.Ceiling(15 / zoomY));
        // Visible row range: top = row at the top edge of the area, bottom = row at its bottom edge
        // (minus one for safety); only these rows are considered, so the cost is independent of image height.
        int top =(int)Math.Floor(image.Height - scrollY), bottom = (int)Math.Floor(image.Height - scrollY - (bounds.Height - RulerHeight) / zoomY) - 1;
        double lastY = double.MaxValue;
        for (int y = Math.Max(0, bottom); y < Math.Min(image.Height, top + 1); y++)
        {
            bool period = axis.IsPeriodStart(y);
            // Discrete axes: period starts always qualify; other notes only while rows are not too
            // crowded (every ≤ 3). Continuous axes: every n-th row. Then the 13 DIP spacing test
            // (against the last label drawn) removes any remaining overlap.
            bool show = axis.IsDiscrete ? (period ? true : every == 1 || (every <= 3 && y % every == 0)) : y % every == 0;
            if (!show) continue;
            double sy = ToScreen(0, y + 0.5).Y; // label at the vertical centre of the row
            if (sy < RulerHeight + 6 || Math.Abs(lastY - sy) < 13) continue;
            lastY = sy;
            var ft = new FormattedText(axis.LabelOf(y), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10.5, period ? bright : text);
            ctx.DrawText(ft, new Point(RulerWidth - 6 - ft.Width, sy - ft.Height / 2));
            ctx.DrawLine(new Pen(period ? bright : text, 1), new Point(RulerWidth - 4, sy), new Point(RulerWidth - 1, sy));
        }
    }

    /// <summary>Top ruler: seconds at a «nice» step (1, 2, 5 × 10^n) for the zoom, plus the play-from triangle.</summary>
    /// <param name="ctx">Drawing context (not clipped).</param>
    /// <param name="bounds">The whole control.</param>
    void DrawTimeRuler(DrawingContext ctx, Rect bounds)
    {
        ctx.FillRectangle(AppTheme.Brush("Surface"), new Rect(RulerWidth, 0, bounds.Width - RulerWidth, RulerHeight));
        ctx.DrawLine(new Pen(AppTheme.Brush("Line"), 1), new Point(RulerWidth, RulerHeight - 0.5), new Point(bounds.Width, RulerHeight - 0.5));
        var text = AppTheme.Brush("TextDim");
        double secPerCol = image!.SecondsPerColumn;
        double step = NiceStep(secPerCol * 90 / zoomX);
        for (double t = 0; t <= image.DurationSeconds + 1e-9; t += step)
        {
            double sx = ToScreen(t / secPerCol, 0).X;
            if (sx < RulerWidth - 1 || sx > bounds.Width) continue;
            ctx.DrawLine(new Pen(text, 1), new Point(sx, RulerHeight - 6), new Point(sx, RulerHeight));
            var ft = new FormattedText(t.ToString(step < 1 ? "0.##" : "0", System.Globalization.CultureInfo.InvariantCulture) + " s",
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10.5, text);
            ctx.DrawText(ft, new Point(sx + 3, 3));
        }
        // Play-from cursor marker: a small triangle.
        double cx = ToScreen(cursorColumn, 0).X;
        if (cx >= RulerWidth - 1)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(cx - 6, 2), true);
                g.LineTo(new Point(cx + 6, 2));
                g.LineTo(new Point(cx, RulerHeight - 2));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(AppTheme.Brush("Warn"), null, geo);
        }
    }

    /// <summary>1, 2 or 5 × 10^n not smaller than <paramref name="raw"/> (ruler tick spacing).</summary>
    /// <param name="raw">Minimum spacing (seconds).</param>
    /// <returns>The step; 1 for a non-positive or NaN input.</returns>
    static double NiceStep(double raw)
    {
        // p = the power of ten at or below raw; the answer is the first of 1p, 2p, 5p, 10p that reaches raw.
        if (!(raw > 0)) return 1;
        double p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (double m in new[] { 1.0, 2, 5, 10 })
            if (m * p >= raw) return m * p;
        return 10 * p;
    }

    // ------------------------------------------------------------------------------------------
    //  Mouse
    // ------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>
    /// Decides what the drag will be, in priority order: middle button → pan; press on the time
    /// ruler → move the play-from cursor; press on the pitch ruler → nothing; Select tool → new
    /// selection or move (right button clears it); shape tools → preview until release; anything
    /// else → a freehand stroke (the right button always strokes with the eraser). The pointer is
    /// captured so the drag continues outside the control.
    /// </remarks>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (image is null || session is null) return;
        Focus();
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        var pos = ToImage(p);
        dragStartScreen = p;
        dragStart = dragNow = pos;
        e.Pointer.Capture(this);
        e.Handled = true;

        if (props.IsMiddleButtonPressed)
        {
            drag = Drag.Pan;
            panScrollX = scrollX; panScrollY = scrollY;
            return;
        }
        if (p.Y < RulerHeight && p.X >= RulerWidth)
        {
            drag = Drag.Ruler;
            CursorColumn = Math.Clamp(pos.X, 0, image.Width);
            return;
        }
        if (p.X < RulerWidth) { drag = Drag.None; return; }

        bool right = props.IsRightButtonPressed;
        if (IsSelectionTool(Tool))
        {
            // Right button: abandon a spline in progress, else remove the selection.
            if (right)
            {
                if (spline.Count > 0) CancelSpline(); else session.ChangeSelection(null);
                drag = Drag.None;
                return;
            }
            var mode = ModeOf(e.KeyModifiers);
            if (Tool == PaintTool.SelectSpline)
            {
                // Click-based: every press adds a point; the second press of a double-click closes.
                drag = Drag.None;
                if (spline.Count == 0) splineMode = mode;
                if (e.ClickCount >= 2 && spline.Count >= 3) CloseSpline();
                else spline.Add(pos);
                InvalidateVisual();
                return;
            }
            // A plain press inside the selection moves it; otherwise a new shape is drawn.
            var s = session.Selection;
            bool inside = s is { } sel && sel.Contains((int)Math.Floor(pos.X), (int)Math.Floor(pos.Y));
            drag = mode == SelectionCombine.Replace && inside ? Drag.SelectMove : Drag.SelectNew;
            dragTool = Tool;
            selectMode = mode;
            shiftAtPress = shiftNow = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            shiftReleased = false;
            lasso.Clear();
            if (Tool == PaintTool.SelectLasso) lasso.Add(pos);
            InvalidateVisual();
            return;
        }
        var tool = right ? PaintTool.Eraser : Tool;
        if (!right && tool is PaintTool.Line or PaintTool.Rectangle or PaintTool.Ellipse or PaintTool.Gradient)
        {
            drag = Drag.Shape;
            dragTool = tool;
            InvalidateVisual();
            return;
        }
        drag = Drag.Stroke;
        dragTool = tool;
        // The radii are fixed for the whole stroke (zooming mid-stroke does not change the footprint).
        var (rx, ry) = BrushRadii();
        session.Brushes.BeginStroke(tool, Brush, pos.X, pos.Y, rx, ry);
        // A press in the empty room after the end (GrowLimit) grows the image right away.
        grewThisStroke = false;
        if (pos.X >= image.Width) { GrowTo(pos.X); session.Brushes.StrokeTo(pos.X, pos.Y); }
        UpdateHover(p);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Continues the current drag (pan offsets the scroll saved at press time by the pointer travel
    /// converted to columns/rows; a stroke forwards the point to the brush engine, which interpolates
    /// dabs between pointer events), refreshes the hover read-out and repaints for the outline/preview.
    /// </remarks>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (image is null || session is null) return;
        var p = e.GetPosition(this);
        hover = p;
        var pos = ToImage(p);
        dragNow = pos;
        switch (drag)
        {
            case Drag.Pan:
                scrollX = Math.Clamp(panScrollX - (p.X - dragStartScreen.X) / zoomX, 0, MaxScrollX);
                scrollY = Math.Clamp(panScrollY - (p.Y - dragStartScreen.Y) / zoomY, 0, MaxScrollY);
                ViewChanged?.Invoke();
                break;
            case Drag.Ruler:
                CursorColumn = Math.Clamp(pos.X, 0, image.Width);
                break;
            case Drag.Stroke:
                GrowTo(pos.X);   // only with GrowLimit: the pattern gets longer under the pencil
                session.Brushes.StrokeTo(pos.X, pos.Y);
                break;
            case Drag.SelectNew:
                shiftNow = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                if (!shiftNow) shiftReleased = true;
                // Lasso: keep a point every ≥ 2 DIP of pointer travel (screen distance between the
                // last kept point and this one, converted from image units with the zooms).
                if (dragTool == PaintTool.SelectLasso && lasso.Count > 0)
                {
                    var (lx, ly) = lasso[^1];
                    if (Math.Abs(pos.X - lx) * zoomX >= 2 || Math.Abs(pos.Y - ly) * zoomY >= 2) lasso.Add(pos);
                }
                break;
        }
        UpdateHover(p);
        InvalidateVisual();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Commits the drag: ends the stroke (one undo step), applies the shape between the press and
    /// release points, sets or clears the selection, or moves it by whole cells.
    /// </remarks>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (session is null || image is null) { drag = Drag.None; return; }
        var pos = ToImage(e.GetPosition(this));
        dragNow = pos;
        switch (drag)
        {
            case Drag.Stroke:
                session.Brushes.EndStroke();
                drag = Drag.None;   // so the refit below is not suppressed as «mid-stroke»
                FinishGrow();
                break;
            case Drag.Shape:
                var (rx, ry) = BrushRadii();
                switch (dragTool)
                {
                    case PaintTool.Line: session.Brushes.Line(Brush, dragStart.X, dragStart.Y, pos.X, pos.Y, rx, ry); break;
                    case PaintTool.Rectangle: session.Brushes.FillShape(false, Brush, dragStart.X, dragStart.Y, pos.X, pos.Y); break;
                    case PaintTool.Ellipse: session.Brushes.FillShape(true, Brush, dragStart.X, dragStart.Y, pos.X, pos.Y); break;
                    case PaintTool.Gradient: session.Brushes.Gradient(Brush, dragStart.X, dragStart.Y, pos.X, pos.Y, session.Region, session.Selection); break;
                }
                break;
            case Drag.SelectNew:
                shiftNow = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                if (!shiftNow) shiftReleased = true;
                // A click without dragging (less than 3 DIP in both directions): a plain click
                // clears the selection, a Shift/Alt click leaves it as it is.
                bool click = Math.Abs(pos.X - dragStart.X) * zoomX < 3 && Math.Abs(pos.Y - dragStart.Y) * zoomY < 3;
                if (click && (dragTool != PaintTool.SelectLasso || lasso.Count < 3))
                {
                    if (selectMode == SelectionCombine.Replace) session.ChangeSelection(null);
                    break;
                }
                SelectionMask? shape = dragTool switch
                {
                    PaintTool.SelectEllipse => SelectionMask.Ellipse(dragStart.X, dragStart.Y, EllipseEnd().X, EllipseEnd().Y, image.Width, image.Height),
                    PaintTool.SelectLasso => SelectionMask.Polygon(lasso.Append(pos).ToList(), image.Width, image.Height),
                    _ => CellRect.FromPoints(dragStart.X, dragStart.Y, pos.X, pos.Y, image),
                };
                CommitSelection(shape, selectMode);
                lasso.Clear();
                break;
            case Drag.SelectMove:
                session.MoveSelection((int)Math.Round(pos.X - dragStart.X), (int)Math.Round(pos.Y - dragStart.Y));
                break;
        }
        drag = Drag.None;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Capture can be lost without a release (Alt+Tab, a dialog, the window losing focus): a stroke
    /// in progress is ended so its undo step is recorded; a pending shape or selection is discarded.
    /// After a normal release <c>drag</c> is already None and this does nothing.
    /// </remarks>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (drag == Drag.Stroke) session?.Brushes.EndStroke();
        drag = Drag.None;
        FinishGrow();
        lasso.Clear();
    }

    /// <inheritdoc/>
    /// <remarks>Hides the brush outline and clears the hover read-out.</remarks>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        hover = null;
        Hover?.Invoke("");
        InvalidateVisual();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Ctrl+Shift or Alt → pitch zoom around the mouse row; Ctrl → time zoom around the mouse column;
    /// Shift or a horizontal wheel/touchpad swipe → scroll time; plain wheel → scroll pitch. Each
    /// notch zooms by 1.25× (or 0.8×) or scrolls 60 DIP.
    /// </remarks>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (image is null) return;
        var p = e.GetPosition(this);
        var mods = e.KeyModifiers;
        // Use the vertical delta, or the horizontal one for tilt wheels and sideways touchpad swipes.
        double d = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (mods.HasFlag(KeyModifiers.Control) && mods.HasFlag(KeyModifiers.Shift) || mods.HasFlag(KeyModifiers.Alt))
            ZoomPitch(d > 0 ? 1.25 : 0.8, p.Y);
        else if (mods.HasFlag(KeyModifiers.Control))
            ZoomTime(d > 0 ? 1.25 : 0.8, ToImage(p).X);
        else if (mods.HasFlag(KeyModifiers.Shift) || e.Delta.X != 0)
            ScrollX = scrollX - d * 60 / zoomX;
        else
            ScrollY = scrollY - d * 60 / zoomY;
        e.Handled = true;
    }

    /// <summary>Publishes the hover read-out: time, frequency, note, levels under the mouse.</summary>
    /// <param name="p">Pointer position in control DIP.</param>
    /// <remarks>
    /// Raises <see cref="Hover"/> with "" over the rulers or outside the image. The time is the
    /// CENTRE of the hovered column; the frequency is the row's own frequency (not interpolated), and
    /// the note name is always the nearest 12-tone note with its deviation in cents.
    /// </remarks>
    void UpdateHover(Point p)
    {
        if (image is null) return;
        if (p.X < RulerWidth || p.Y < RulerHeight)
        {
            Hover?.Invoke("");
            return;
        }
        var (x, y) = ToImage(p);
        int cx = (int)Math.Floor(x), cy = (int)Math.Floor(y);
        if (!image.Contains(cx, cy))
        {
            Hover?.Invoke("");
            return;
        }
        double hz = image.Axis.FrequencyOf(cy);
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        string label = image.Axis.IsDiscrete ? image.Axis.LabelOf(cy) + " · " : "";
        // N = noisiness of the cell, shown only when the picture has a noise layer.
        string noise = image.Noise is null ? "" : string.Create(ci, $"  N {image.GetNoise(cx, cy):0.00}");
        Hover?.Invoke(string.Create(ci, $"{(cx + 0.5) * image.SecondsPerColumn:0.000} s  ·  {label}{hz.ToString(hz < 100 ? "0.00" : "0.0", ci)} Hz  ({PitchAxis.NoteName(hz)})  ·  " +
                      $"L {image.GetLeft(cx, cy):0.00}  R {image.GetRight(cx, cy):0.00}{noise}  ·  col {cx}, row {cy}"));
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // Only the theme subscription is dropped here: a TabControl detaches the content of a
        // hidden tab, and the canvas must keep its session (the room clears it when disposed).
        AppTheme.Changed -= OnThemeChanged;
        antsTimer.Stop();
        CancelSpline();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Re-subscribes to theme changes; the «−= then +=» pair makes sure the handler is attached
    /// exactly once (the constructor already subscribed before the first attachment).
    /// </remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AppTheme.Changed -= OnThemeChanged;
        AppTheme.Changed += OnThemeChanged;
        antsTimer.Start();
    }
}
