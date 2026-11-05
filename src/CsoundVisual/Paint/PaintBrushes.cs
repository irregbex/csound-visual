// PaintBrushes.cs — Paint Rooms foundation.
//
// Role: the painting tools as pure logic on a SpectralImage, independent of the UI so they can be
// unit-tested and reused by every room. The canvas control converts mouse positions to IMAGE
// coordinates (x = fractional column, y = fractional row, row 0 at the bottom) and calls
// BeginStroke / StrokeTo / EndStroke, or the shape/fill helpers.
//
// Stroke model (like a «stroke opacity» in image editors): during one stroke every touched cell
// keeps the MAXIMUM brush coverage it received (0…1) and its value is
//     base + (target − base) · coverage       (painting only ever brightens: target ≥ base)
// where base is the cell's value when the stroke started. So going over the same place twice in
// one stroke does not build up, a soft edge stays soft, and painting one layer (pan) never darkens
// the other. The eraser uses target 0 and only darkens.
//
// Brush footprint: the size is given in SCREEN pixels; the canvas passes the radii in image units
// (rx = size/2 / pixelsPerColumn, ry = size/2 / pixelsPerRow) so a round brush stays round on screen
// at any zoom. Hardness shapes the edge: coverage = 1 inside hardness·radius, then a smooth
// (cosine) fall to 0 at the radius. The airbrush uses a Gaussian instead.
//
// Pan: −1 = left layer only, 0 = both, +1 = right only, linear balance law (the near side keeps
// full level up to the centre).
//
// Every edit is recorded in the session history (Begin/Extend/Commit) before the pixels change.
// UI thread only.
//
// Notifications: while a stroke runs, every StrokeTo ends with Flush(), which calls
// SpectralImage.NotifyChanged(minX, maxX + 1) for just the columns touched since the previous flush.
// The canvas recolours those bitmap columns and the live player re-uploads those table columns to
// Csound, so painting during playback is heard immediately. EndStroke / the one-shot tools then raise
// PaintSession.Edited once (dirty flag, auto-level, status line).
//
// Storage reminder: SpectralImage.Left / Right are column-major float arrays (cell (x, y) at index
// x · Height + y), so a range of columns is one contiguous slice — which is why history, copies and
// notifications all work in COLUMN ranges [x0, x1).
//
// Noise amount (BrushSettings.Noise, 0 = tone … 1 = noise band): every painting tool (not the
// eraser) also moves the cell's NOISINESS (SpectralImage.Noise) towards the brush's noise amount
// with the same coverage model, n = base + (target − base) · coverage — a «replace» blend, not a
// lighten, so painting tone (0) over a noisy cell really makes it tonal again. The layer is created
// on the first stroke that needs it (noise amount > 0); while a picture has no layer and the brush
// paints pure tone nothing extra happens. With NoiseOnly the levels are left alone and only the
// noisiness is painted (turn existing notes noisy or tonal). Blur and smudge carry the noise layer
// along with the levels.

namespace CsoundVisual.Paint;

/// <summary>The tools of the Paint Synth room.</summary>
public enum PaintTool
{
    /// <summary>Hard round brush.</summary>
    Pencil,
    /// <summary>Soft Gaussian brush.</summary>
    Airbrush,
    /// <summary>Random dots inside the brush (textures, grains).</summary>
    Spray,
    /// <summary>Paints a fundamental plus N harmonics (k·f) with decreasing amplitude.</summary>
    Harmonics,
    /// <summary>Straight line (drag from start to end).</summary>
    Line,
    /// <summary>Filled rectangle (drag a corner to the opposite one).</summary>
    Rectangle,
    /// <summary>Filled ellipse (drag the bounding box).</summary>
    Ellipse,
    /// <summary>Linear gradient: from full opacity at the start of the drag to nothing at its end (inside the selection, or the whole canvas).</summary>
    Gradient,
    /// <summary>Darkens towards silence.</summary>
    Eraser,
    /// <summary>Blurs (averages neighbours) under the brush.</summary>
    Blur,
    /// <summary>Drags the picture along with the brush.</summary>
    Smudge,
    /// <summary>Rectangular selection: move, copy, paste, delete; limits the image operations.</summary>
    Select,
    /// <summary>Elliptical selection (drag the box; Shift while dragging = a circle on screen).</summary>
    SelectEllipse,
    /// <summary>Freehand (lasso) selection: draw any closed outline; it closes itself on release.</summary>
    SelectLasso,
    /// <summary>Spline selection: click points; a smooth closed Catmull-Rom curve runs through them (double-click or Enter closes).</summary>
    SelectSpline,
}

/// <summary>Brush parameters shared by the tools (immutable snapshot).</summary>
public sealed record BrushSettings
{
    /// <summary>Diameter in screen pixels.</summary>
    public double Size { get; init; } = 10;
    /// <summary>Target level 0…1 (how bright the paint is).</summary>
    public double Opacity { get; init; } = 0.85;
    /// <summary>0 = soft edge … 1 = hard edge.</summary>
    public double Hardness { get; init; } = 0.8;
    /// <summary>−1 left only … 0 both … +1 right only.</summary>
    public double Pan { get; init; }
    /// <summary>Harmonics brush: partials including the fundamental (1…64).</summary>
    public int Harmonics { get; init; } = 8;
    /// <summary>Harmonics brush: partial k gets k^−Rolloff of the opacity.</summary>
    public double Rolloff { get; init; } = 1;
    /// <summary>Harmonics brush: only odd partials (1, 3, 5…).</summary>
    public bool OddOnly { get; init; }
    /// <summary>Noise amount painted into the noise layer: 0 = pure tone (sine) … 1 = noise band (see the file header).</summary>
    public double Noise { get; init; }
    /// <summary>True: paint ONLY the noise amount (levels untouched), to make existing sound noisier or more tonal.</summary>
    public bool NoiseOnly { get; init; }

    /// <summary>True when a stroke with these settings writes the noise layer of an image that has none yet.</summary>
    public bool CreatesNoise => Noise > 0.0005;

    /// <summary>Target levels for the two layers from <see cref="Opacity"/> and <see cref="Pan"/> (balance law).</summary>
    /// <returns>
    /// (left, right) levels 0…1: both equal to the opacity at pan 0; at pan p &gt; 0 the left one is
    /// scaled by 1 − p (the right stays full), and symmetrically for p &lt; 0.
    /// </returns>
    public (double Left, double Right) Targets()
    {
        double p = Math.Clamp(Pan, -1, 1);
        return (Opacity * Math.Min(1, 1 - p), Opacity * Math.Min(1, 1 + p));
    }
}

/// <summary>A rectangle of cells: columns [X0, X1) and rows [Y0, Y1).</summary>
/// <param name="X0">First column.</param>
/// <param name="Y0">First (lowest) row.</param>
/// <param name="X1">Column after the last.</param>
/// <param name="Y1">Row after the last.</param>
public readonly record struct CellRect(int X0, int Y0, int X1, int Y1)
{
    /// <summary>Columns covered.</summary>
    public int Width => X1 - X0;
    /// <summary>Rows covered.</summary>
    public int Height => Y1 - Y0;
    /// <summary>True when it covers no cell.</summary>
    public bool IsEmpty => X1 <= X0 || Y1 <= Y0;
    /// <summary>The rectangle spanned by two fractional points, clipped to an image.</summary>
    /// <param name="xa">First point, fractional column.</param>
    /// <param name="ya">First point, fractional row.</param>
    /// <param name="xb">Second point, fractional column.</param>
    /// <param name="yb">Second point, fractional row.</param>
    /// <param name="img">Image to clip to.</param>
    /// <returns>Every cell touched by the span, INCLUDING the cells containing both points (hence the floor + 1 on the far edges).</returns>
    public static CellRect FromPoints(double xa, double ya, double xb, double yb, SpectralImage img) =>
        new(Math.Clamp((int)Math.Floor(Math.Min(xa, xb)), 0, img.Width), Math.Clamp((int)Math.Floor(Math.Min(ya, yb)), 0, img.Height),
            Math.Clamp((int)Math.Floor(Math.Max(xa, xb)) + 1, 0, img.Width), Math.Clamp((int)Math.Floor(Math.Max(ya, yb)) + 1, 0, img.Height));
    /// <summary>The whole image.</summary>
    /// <param name="img">The image.</param>
    public static CellRect All(SpectralImage img) => new(0, 0, img.Width, img.Height);
    /// <summary>True when the cell is inside.</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    public bool Contains(int x, int y) => x >= X0 && x < X1 && y >= Y0 && y < Y1;
}

/// <summary>Applies the painting tools to the session's image (see the file header).</summary>
/// <remarks>
/// One instance per <see cref="PaintSession"/> (exposed as <c>PaintSession.Brushes</c>); the canvas
/// drives it from mouse events. A freehand stroke is a three-call protocol —
/// <see cref="BeginStroke"/>, any number of <see cref="StrokeTo"/>, <see cref="EndStroke"/> — and the
/// shape tools (<see cref="Line"/>, <see cref="FillShape"/>, <see cref="Gradient"/>) are single calls.
/// Each of them becomes exactly one undo step.
/// </remarks>
public sealed class BrushEngine
{
    /// <summary>The session whose current image is painted and whose history records the edits.</summary>
    readonly PaintSession session;

    /// <summary>Random source of the spray tool (unseeded: every spray stroke differs).</summary>
    readonly Random rng = new();

    // ---- state of the stroke in progress

    /// <summary>Image being painted by the current stroke; null when no stroke is in progress.</summary>
    SpectralImage? img;

    /// <summary>Full copies of both layers taken at stroke start; the stroke model blends from these.</summary>
    float[]? baseL, baseR;     // values at stroke start (only cells touched are read)

    /// <summary>Copy of the noise layer at stroke start (<c>null</c> when the stroke does not touch noise).</summary>
    float[]? baseN;

    /// <summary>Largest coverage (0…1) each cell has received in this stroke; same layout as the image arrays.</summary>
    float[]? coverage;          // max coverage per cell during this stroke (both layers share it)

    /// <summary>Tool of the current stroke.</summary>
    PaintTool tool;

    /// <summary>Brush settings captured at stroke start (later UI changes do not affect a running stroke).</summary>
    BrushSettings brush = new();

    /// <summary>Brush radii of the current stroke, in columns and rows (at least half a cell).</summary>
    double rx = 1, ry = 1;     // brush radii in image units

    /// <summary>Last stroke point (image units), the start of the next interpolated segment.</summary>
    double lastX, lastY;

    /// <summary>Column range touched since the last <see cref="Flush"/> (inclusive; empty when maxX &lt; minX).</summary>
    int minX, maxX;             // columns touched so far (for notifications)

    /// <summary>
    /// Indices of cells whose coverage became non-zero in this stroke. Currently only filled and
    /// cleared: <see cref="coverage"/> is reallocated per stroke, so the list is not read.
    /// </summary>
    readonly List<int> touched = []; // cell indices whose coverage is non-zero (to reset quickly)

    /// <summary>
    /// The image as it was before the current stroke, built the first time the stroke made the
    /// image wider (<see cref="Grow"/>); <c>null</c> while the stroke has not grown it.
    /// </summary>
    /// <remarks>
    /// A growing stroke cannot be stored as a column-range capture (the capture belongs to the
    /// narrower image object, which is replaced), so it is stored as an image REPLACEMENT instead:
    /// undo puts this pre-stroke image back, which removes both the paint and the added columns in
    /// one step. It is rebuilt from <see cref="baseL"/> / <see cref="baseR"/> / <see cref="baseN"/>,
    /// which hold exactly the pre-stroke values.
    /// </remarks>
    SpectralImage? preStroke;

    /// <summary>Creates the engine for <paramref name="session"/>.</summary>
    /// <param name="session">Owning session.</param>
    public BrushEngine(PaintSession session) => this.session = session;

    /// <summary>True while a stroke is in progress.</summary>
    public bool InStroke => img is not null;

    /// <summary>
    /// Starts a freehand stroke (pencil, airbrush, spray, harmonics, eraser, blur, smudge) at
    /// (<paramref name="x"/>, <paramref name="y"/>) in image units, with brush radii
    /// <paramref name="radiusX"/>, <paramref name="radiusY"/> in image units.
    /// </summary>
    /// <param name="paintTool">The tool (the canvas passes <see cref="PaintTool.Eraser"/> for the right button).</param>
    /// <param name="settings">Brush settings, captured for the whole stroke.</param>
    /// <param name="x">Start column (fractional).</param>
    /// <param name="y">Start row (fractional, 0 = bottom).</param>
    /// <param name="radiusX">Horizontal radius in columns.</param>
    /// <param name="radiusY">Vertical radius in rows.</param>
    /// <remarks>
    /// Cost: clones both layers and allocates a coverage array the size of the image (three arrays of
    /// Width × Height floats, a few MB for 1024 × 512) once per stroke, so every stamp can blend from
    /// the stroke-start values in O(footprint). A stroke still open is ended (committed) first.
    /// </remarks>
    public void BeginStroke(PaintTool paintTool, BrushSettings settings, double x, double y, double radiusX, double radiusY)
    {
        EndStroke();
        img = session.Image;
        tool = paintTool;
        brush = settings;
        rx = Math.Max(0.5, radiusX);
        ry = Math.Max(0.5, radiusY);
        int n = img.Left.Length;
        baseL = (float[])img.Left.Clone();
        baseR = (float[])img.Right.Clone();
        // The noise layer is created BEFORE the history capture below, so the undo entry holds the
        // (all-zero) noise of the first columns; later columns are captured by Extend as usual.
        if (settings.CreatesNoise || settings.NoiseOnly) img.EnsureNoise();
        baseN = img.Noise is { } nz ? (float[])nz.Clone() : null;
        coverage = new float[n];
        touched.Clear();
        minX = int.MaxValue; maxX = int.MinValue;
        // Capture the columns under the first dab (+1 margin) for undo; Footprint extends it as the stroke moves.
        session.History.Begin(img, (int)Math.Floor(x - rx - 1), (int)Math.Ceiling(x + rx + 1), ToolName(paintTool));
        lastX = x; lastY = y;
        Stamp(x, y, 0, 0); // first dab: no motion yet (smudge does nothing until the pointer moves)
        Flush();
    }

    /// <summary>Continues the stroke to (x, y), stamping along the way (spacing ≈ a quarter of the brush).</summary>
    /// <param name="x">New column (fractional).</param>
    /// <param name="y">New row (fractional).</param>
    /// <remarks>
    /// Pointer events arrive tens of DIP apart on a fast drag; interpolating dabs every 0.25 radius
    /// (measured in the ELLIPTICAL metric of the brush, so it is correct for any zoom ratio) keeps
    /// the line continuous. Ignored when no stroke is in progress.
    /// </remarks>
    public void StrokeTo(double x, double y)
    {
        if (img is null) return;
        double dx = x - lastX, dy = y - lastY;
        // Distance in «brush radii»: stamps every 0.25 radius keeps hard edges smooth.
        double dist = Math.Sqrt(dx * dx / (rx * rx) + dy * dy / (ry * ry));
        int steps = Math.Max(1, (int)Math.Ceiling(dist / 0.25));
        double px = lastX, py = lastY;
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            double sx = lastX + dx * t, sy = lastY + dy * t;
            Stamp(sx, sy, sx - px, sy - py);
            px = sx; py = sy;
        }
        lastX = x; lastY = y;
        Flush();
    }

    /// <summary>Ends the stroke and stores it in the history.</summary>
    /// <remarks>Safe to call when no stroke is in progress (does nothing). Raises <c>PaintSession.Edited</c> and releases the per-stroke arrays.</remarks>
    public void EndStroke()
    {
        if (img is null) return;
        // A stroke that made the image wider is one «image replaced» step (see preStroke);
        // otherwise the usual column-range capture is committed.
        if (preStroke is { } pre) { session.History.PushReplaced(pre, ToolName(tool)); preStroke = null; }
        else session.History.Commit();
        session.NotifyEdited();
        img = null;
        baseL = baseR = baseN = coverage = null;
        touched.Clear();
    }

    /// <summary>
    /// Makes the image of the stroke in progress wider (Image Sequencer: a pattern grows while the
    /// user paints past its end). The new columns are silent; the stroke goes on painting into them.
    /// </summary>
    /// <param name="newWidth">The new column count (ignored unless larger than the current one).</param>
    /// <returns>True when the image was replaced by a wider one.</returns>
    /// <remarks>
    /// The image arrays are column-major (cell (x, y) at x·Height + y), so adding columns at the
    /// right only appends to the arrays: the existing cells, the stroke-start copies and the coverage
    /// keep their indices and are simply extended with zeros. The duration grows in proportion (same
    /// seconds per column, i.e. the same step length). The wider image replaces the session's image
    /// WITHOUT a history step; the whole stroke becomes one step at <see cref="EndStroke"/>.
    /// </remarks>
    public bool Grow(int newWidth)
    {
        if (img is null || newWidth <= img.Width || baseL is null || baseR is null || coverage is null) return false;
        int n0 = img.Left.Length, n1 = newWidth * img.Height;
        if (preStroke is null)
        {
            // First growth of this stroke: rebuild the pre-stroke image and drop the column capture.
            var pre = new SpectralImage(img.Width, img.Axis, img.DurationSeconds);
            Array.Copy(baseL, pre.Left, n0);
            Array.Copy(baseR, pre.Right, n0);
            if (baseN is not null) Array.Copy(baseN, pre.EnsureNoise(), n0);
            preStroke = pre;
            session.History.Cancel();
        }
        var big = new SpectralImage(newWidth, img.Axis, img.DurationSeconds * newWidth / img.Width);
        Array.Copy(img.Left, big.Left, n0);
        Array.Copy(img.Right, big.Right, n0);
        if (img.Noise is { } nz) Array.Copy(nz, big.EnsureNoise(), n0);
        Array.Resize(ref baseL, n1);
        Array.Resize(ref baseR, n1);
        if (baseN is not null) Array.Resize(ref baseN, n1);
        Array.Resize(ref coverage, n1);
        img = big;
        session.ReplaceImage(big, "grow", undoable: false);
        return true;
    }

    /// <summary>Draws a straight line with the pencil footprint (one undo step).</summary>
    /// <param name="settings">Brush settings.</param>
    /// <param name="xa">Start column.</param>
    /// <param name="ya">Start row.</param>
    /// <param name="xb">End column.</param>
    /// <param name="yb">End row.</param>
    /// <param name="radiusX">Brush radius in columns.</param>
    /// <param name="radiusY">Brush radius in rows.</param>
    public void Line(BrushSettings settings, double xa, double ya, double xb, double yb, double radiusX, double radiusY)
    {
        BeginStroke(PaintTool.Pencil, settings, xa, ya, radiusX, radiusY);
        // Capture the whole span for undo in one go (cheaper than growing it stamp by stamp).
        session.History.Extend((int)Math.Floor(Math.Min(xa, xb) - rx - 1), (int)Math.Ceiling(Math.Max(xa, xb) + rx + 1));
        StrokeTo(xb, yb);
        EndStroke();
    }

    /// <summary>
    /// Fills a rectangle or ellipse spanned by two points with the brush target (lightening), with
    /// a soft border when hardness &lt; 1.
    /// </summary>
    /// <param name="ellipse">True = ellipse inscribed in the rectangle; false = the rectangle itself.</param>
    /// <param name="settings">Brush settings (opacity, pan, hardness).</param>
    /// <param name="xa">First corner, column.</param>
    /// <param name="ya">First corner, row.</param>
    /// <param name="xb">Opposite corner, column.</param>
    /// <param name="yb">Opposite corner, row.</param>
    /// <remarks>
    /// The soft border is measured in NORMALISED shape coordinates (−1…1 from the centre to the
    /// edge): its width is (1 − hardness) of the half-width / half-height, ending in a raised-cosine
    /// fall to 0 at the edge (see <see cref="EdgeCoverage"/>). Hardness 0 therefore fades from the
    /// centre outwards. The fill is lighten-only and is one undo step.
    /// </remarks>
    public void FillShape(bool ellipse, BrushSettings settings, double xa, double ya, double xb, double yb)
    {
        var image = session.Image;
        var r = CellRect.FromPoints(xa, ya, xb, yb, image);
        if (r.IsEmpty) return;
        if (settings.CreatesNoise) image.EnsureNoise();   // before the capture (see BeginStroke)
        session.History.Begin(image, r.X0, r.X1, ellipse ? "ellipse" : "rectangle");
        var (tl, tr) = settings.Targets();
        var nz = image.Noise;
        float tn = (float)Math.Clamp(settings.Noise, 0, 1);
        double cx = (r.X0 + r.X1) / 2.0, cy = (r.Y0 + r.Y1) / 2.0;
        double ax = Math.Max(0.5, r.Width / 2.0), ay = Math.Max(0.5, r.Height / 2.0);
        double soft = 1 - Math.Clamp(settings.Hardness, 0, 1);
        int h = image.Height;
        for (int x = r.X0; x < r.X1; x++)
            for (int y = r.Y0; y < r.Y1; y++)
            {
                double u = (x + 0.5 - cx) / ax, v = (y + 0.5 - cy) / ay; // −1…1 inside
                // Normalised «distance» to the edge: Euclidean for the ellipse, Chebyshev (max norm) for the rectangle.
                double d = ellipse ? Math.Sqrt(u * u + v * v) : Math.Max(Math.Abs(u), Math.Abs(v));
                double cov = EdgeCoverage(d, soft);
                if (cov <= 0) continue;
                int i = x * h + y;
                if (nz is not null) nz[i] += (tn - nz[i]) * (float)cov;   // replace-blend of the noisiness
                if (settings.NoiseOnly) continue;
                image.Left[i] = Lighten(image.Left[i], tl, cov);
                image.Right[i] = Lighten(image.Right[i], tr, cov);
            }
        session.History.Commit();
        image.NotifyChanged(r.X0, r.X1);
        session.NotifyEdited();
    }

    /// <summary>
    /// Linear gradient from the brush target at (xa, ya) to nothing at (xb, yb), lightening the
    /// cells of <paramref name="area"/> (the selection, or the whole image). Cells before the start
    /// get the full target; cells past the end are untouched.
    /// </summary>
    /// <param name="settings">Brush settings (opacity and pan give the start level).</param>
    /// <param name="xa">Start column (full level).</param>
    /// <param name="ya">Start row.</param>
    /// <param name="xb">End column (zero level).</param>
    /// <param name="yb">End row.</param>
    /// <param name="area">Cells affected (the canvas passes <c>PaintSession.Region</c>: the selection's bounds or the whole image).</param>
    /// <param name="mask">The selection shape, or <c>null</c>: each cell's coverage scales the gradient there, so a shaped (or feathered) selection limits it softly.</param>
    /// <remarks>The gradient runs along the drag direction in IMAGE units, so the same drag gives different screen angles at different zoom ratios.</remarks>
    public void Gradient(BrushSettings settings, double xa, double ya, double xb, double yb, CellRect area, SelectionMask? mask = null)
    {
        var image = session.Image;
        if (area.IsEmpty) return;
        if (settings.CreatesNoise) image.EnsureNoise();   // before the capture (see BeginStroke)
        session.History.Begin(image, area.X0, area.X1, "gradient");
        var (tl, tr) = settings.Targets();
        var nz = image.Noise;
        float tn = (float)Math.Clamp(settings.Noise, 0, 1);
        double dx = xb - xa, dy = yb - ya, len2 = Math.Max(1e-9, dx * dx + dy * dy);
        int h = image.Height;
        for (int x = area.X0; x < area.X1; x++)
            for (int y = area.Y0; y < area.Y1; y++)
            {
                // Projection of the cell centre on the drag vector: 0 at the start, 1 at the end.
                double t = ((x + 0.5 - xa) * dx + (y + 0.5 - ya) * dy) / len2;
                double cov = (1 - Math.Clamp(t, 0, 1)) * (mask?[x, y] ?? 1f);
                if (cov <= 0) continue;
                int i = x * h + y;
                if (nz is not null) nz[i] += (tn - nz[i]) * (float)cov;
                if (settings.NoiseOnly) continue;
                image.Left[i] = Lighten(image.Left[i], tl, cov);
                image.Right[i] = Lighten(image.Right[i], tr, cov);
            }
        session.History.Commit();
        image.NotifyChanged(area.X0, area.X1);
        session.NotifyEdited();
    }

    /// <summary>Short English name of a tool (history labels).</summary>
    /// <param name="t">The tool.</param>
    /// <returns>The enum name in lower case («pencil», «airbrush»…).</returns>
    public static string ToolName(PaintTool t) => t.ToString().ToLowerInvariant();

    // ------------------------------------------------------------------------------------------

    /// <summary>Coverage of a point at normalised distance <paramref name="d"/> (0 centre, 1 edge) with edge softness 0…1.</summary>
    /// <param name="d">Normalised distance from the centre (1 = on the edge).</param>
    /// <param name="soft">Width of the soft border as a fraction of the radius (1 − hardness).</param>
    /// <returns>1 inside 1 − soft, a raised-cosine fall to 0 at d = 1, 0 outside.</returns>
    static double EdgeCoverage(double d, double soft)
    {
        if (d >= 1) return 0;
        double inner = 1 - soft;
        if (d <= inner) return 1;
        // Raised-cosine fall from 1 at the inner radius to 0 at the edge.
        double t = (d - inner) / Math.Max(1e-6, soft);
        return 0.5 + 0.5 * Math.Cos(Math.PI * t);
    }

    /// <summary>Lightening blend: move <paramref name="value"/> towards a brighter target by <paramref name="cov"/>; never darkens.</summary>
    /// <param name="value">Current level.</param>
    /// <param name="target">Brush target level.</param>
    /// <param name="cov">Coverage 0…1 (fraction of the way to the target).</param>
    static float Lighten(float value, double target, double cov) =>
        target > value ? (float)(value + (target - value) * cov) : value;

    /// <summary>Applies one brush footprint centred at (x, y); (mx, my) is the motion since the previous stamp (smudge).</summary>
    /// <param name="x">Centre column.</param>
    /// <param name="y">Centre row.</param>
    /// <param name="mx">Motion in columns since the previous stamp.</param>
    /// <param name="my">Motion in rows since the previous stamp.</param>
    /// <remarks>
    /// For blur and smudge, <see cref="Footprint"/> only records the history range and the touched
    /// columns; the actual pixel work is done by <see cref="BlurAt"/> / <see cref="SmudgeAt"/>.
    /// </remarks>
    void Stamp(double x, double y, double mx, double my)
    {
        if (img is null) return;
        if (tool == PaintTool.Harmonics)
        {
            StampHarmonics(x, y);
            return;
        }
        Footprint(x, y, 1.0, tool);
        if (tool == PaintTool.Smudge) SmudgeAt(x, y, mx, my);
        else if (tool == PaintTool.Blur) BlurAt(x, y);
    }

    /// <summary>
    /// Harmonics: the fundamental at row y has frequency f; partial k is stamped at the row of k·f
    /// (log-interpolated on the axis, so it works on every axis kind) with opacity·k^−rolloff.
    /// </summary>
    /// <param name="x">Centre column.</param>
    /// <param name="y">Row of the fundamental (fractional).</param>
    /// <remarks>
    /// <c>count</c> partials are painted (the fundamental included; with OddOnly, k = 1, 3, 5…), k is
    /// capped at 256 as a safety bound, and the loop stops at the first partial above the canvas.
    /// Each partial uses the pencil footprint with its gain &lt; 1, which bypasses the «no
    /// build-up» shortcut in <see cref="Footprint"/> so overlapping partials combine by max.
    /// </remarks>
    void StampHarmonics(double x, double y)
    {
        var axis = img!.Axis;
        double f = axis.FrequencyAt(y);
        int count = Math.Clamp(brush.Harmonics, 1, 64);
        int made = 0;
        for (int k = 1; made < count && k <= 256; k++)
        {
            if (brush.OddOnly && k % 2 == 0) continue;
            made++;
            double row = axis.RowOf(f * k);
            if (row > img.Height - 0.5) break; // the rest is above the top of the canvas
            Footprint(x, row, Math.Pow(k, -Math.Max(0, brush.Rolloff)), PaintTool.Pencil);
        }
    }

    /// <summary>
    /// Writes the footprint of the brush at (x, y) scaled by <paramref name="gain"/>, according to
    /// <paramref name="kind"/>: hard/soft disc (pencil, harmonics, eraser), Gaussian (airbrush),
    /// random dots (spray). Blur and smudge only extend the history range here.
    /// </summary>
    /// <param name="x">Centre column (fractional).</param>
    /// <param name="y">Centre row (fractional).</param>
    /// <param name="gain">Multiplier of the target levels (1 for normal brushes, k^−rolloff for harmonic k).</param>
    /// <param name="kind">Footprint shape / blend to apply.</param>
    void Footprint(double x, double y, double gain, PaintTool kind)
    {
        var image = img!;
        int h = image.Height;
        // Bounding box of the elliptical footprint in cells (inclusive), clipped to the image.
        int x0 =Math.Max(0, (int)Math.Floor(x - rx)), x1 = Math.Min(image.Width - 1, (int)Math.Ceiling(x + rx));
        int y0 = Math.Max(0, (int)Math.Floor(y - ry)), y1 = Math.Min(h - 1, (int)Math.Ceiling(y + ry));
        if (x0 > x1 || y0 > y1) return;
        // Undo capture BEFORE the cells change (Extend reads the new columns now).
        session.History.Extend(x0, x1 + 1);
        minX = Math.Min(minX, x0); maxX = Math.Max(maxX, x1);
        if (kind is PaintTool.Blur or PaintTool.Smudge) return;

        var (tl, tr) = brush.Targets();
        tl *= gain; tr *= gain;
        double soft = 1 - Math.Clamp(brush.Hardness, 0, 1);
        bool erase = kind == PaintTool.Eraser;
        // Noise layer written by this footprint (painting tools only; the eraser leaves it alone).
        var nz = erase ? null : image.Noise;
        float tn = (float)Math.Clamp(brush.Noise, 0, 1);
        for (int cx = x0; cx <= x1; cx++)
            for (int cy = y0; cy <= y1; cy++)
            {
                // Cell centre in brush-normalised coordinates: d = 1 on the ellipse's edge.
                double u = (cx + 0.5 - x) / rx, v = (cy + 0.5 - y) / ry;
                double d = Math.Sqrt(u * u + v * v);
                double cov;
                switch (kind)
                {
                    case PaintTool.Airbrush:
                        if (d >= 1) continue;
                        // Gaussian with σ = 0.25 radius at hardness 1 up to 0.55 radius at hardness 0
                        // (wider for softer settings), cut off at the radius.
                        double sigma = 0.25 + 0.3 * soft;
                        cov = Math.Exp(-d * d / (2 * sigma * sigma));
                        break;
                    case PaintTool.Spray:
                        if (d >= 1) continue;
                        // ~6 % of the cells per stamp get a dot of random brightness.
                        if (rng.NextDouble() > 0.06) continue;
                        cov = 0.4 + 0.6 * rng.NextDouble();
                        break;
                    default:
                        cov = EdgeCoverage(d, soft);
                        break;
                }
                if (cov <= 0.001) continue;
                int i = cx * h + cy;
                if (coverage![i] == 0) touched.Add(i);
                // Stroke model: a dab weaker than what this cell already received in the stroke
                // changes nothing — except spray (random dots must keep landing) and harmonic
                // partials (gain < 1: a weaker partial may share a cell with a stronger one).
                if (cov <= coverage[i] && kind != PaintTool.Spray && gain >= 1) continue;
                float c = (float)Math.Max(coverage[i], cov);
                coverage[i] = c;
                if (erase)
                {
                    // Darken towards 0 on the layers the pan selects (both when centred). The strength
                    // is the coverage alone; opacity only matters through the pan gate (tl, tr > 0).
                    var (el, er) = (tl > 0 ? 1.0 : 0.0, tr > 0 ? 1.0 : 0.0);
                    image.Left[i] = (float)(baseL![i] * (1 - c * el));
                    image.Right[i] = (float)(baseR![i] * (1 - c * er));
                }
                else
                {
                    // Noisiness: replace-blend from the stroke-start value by the cell's max coverage
                    // (no build-up within a stroke either). baseN is null only when the layer was
                    // created by ANOTHER edit during this stroke, which cannot happen; 0 is the fallback.
                    if (nz is not null)
                    {
                        float bn = baseN?[i] ?? 0f;
                        nz[i] = bn + (tn - bn) * c;
                    }
                    if (brush.NoiseOnly) continue;
                    // Lighten relative to the value at stroke start (no build-up within a stroke),
                    // but keep what earlier stamps of this stroke already made brighter (harmonics overlap).
                    image.Left[i] = Math.Max(image.Left[i], Lighten(baseL![i], tl, c));
                    image.Right[i] = Math.Max(image.Right[i], Lighten(baseR![i], tr, c));
                }
            }
    }

    /// <summary>Blur brush: each cell under the brush moves towards the 3×3 average by the coverage × opacity.</summary>
    /// <param name="x">Centre column.</param>
    /// <param name="y">Centre row.</param>
    /// <remarks>
    /// Unlike the painting brushes, blur works on the CURRENT values (not the stroke-start copy), so
    /// passing over the same place keeps blurring. The edge softness is at least 0.3 and the strength
    /// is halved (× 0.5) so a single dab is gentle; the 3×3 average is clipped at the image borders.
    /// </remarks>
    void BlurAt(double x, double y)
    {
        var image = img!;
        int h = image.Height;
        int x0 = Math.Max(0, (int)Math.Floor(x - rx)), x1 = Math.Min(image.Width - 1, (int)Math.Ceiling(x + rx));
        int y0 = Math.Max(0, (int)Math.Floor(y - ry)), y1 = Math.Min(h - 1, (int)Math.Ceiling(y + ry));
        double soft = 1 - Math.Clamp(brush.Hardness, 0, 1);
        // Read from a copy of the affected columns (±1 for the neighbours) so the result does not
        // depend on scan order. Column-major storage makes that one contiguous copy per layer.
        var (srcL, srcR, srcN, xs) = Columns(image, x0 - 1, x1 + 2);
        int off = xs * h;
        for (int cx = x0; cx <= x1; cx++)
            for (int cy = y0; cy <= y1; cy++)
            {
                double u = (cx + 0.5 - x) / rx, v = (cy + 0.5 - y) / ry;
                double cov = EdgeCoverage(Math.Sqrt(u * u + v * v), Math.Max(0.3, soft)) * brush.Opacity * 0.5;
                if (cov <= 0) continue;
                double sl = 0, sr = 0, sn = 0; int n = 0;
                for (int ax = Math.Max(0, cx - 1); ax <= Math.Min(image.Width - 1, cx + 1); ax++)
                    for (int ay = Math.Max(0, cy - 1); ay <= Math.Min(h - 1, cy + 1); ay++)
                    {
                        sl += srcL[ax * h + ay - off]; sr += srcR[ax * h + ay - off]; n++;
                        if (srcN is not null) sn += srcN[ax * h + ay - off];
                    }
                int i = cx * h + cy;
                image.Left[i] = (float)(srcL[i - off] + (sl / n - srcL[i - off]) * cov);
                image.Right[i] = (float)(srcR[i - off] + (sr / n - srcR[i - off]) * cov);
                if (srcN is not null) image.Noise![i] = (float)(srcN[i - off] + (sn / n - srcN[i - off]) * cov);
            }
    }

    /// <summary>Smudge brush: pulls the picture from behind the brush along its motion (mx, my in image units).</summary>
    /// <param name="x">Centre column.</param>
    /// <param name="y">Centre row.</param>
    /// <param name="mx">Motion in columns since the previous stamp.</param>
    /// <param name="my">Motion in rows since the previous stamp.</param>
    /// <remarks>
    /// Each cell is blended (by coverage × opacity) towards the cell one motion step BEHIND it, so
    /// repeated stamps carry content forward along the drag. The source columns are copied first
    /// (with a reach of |mx| + 1 columns on each side) so reads are not affected by this stamp's writes.
    /// </remarks>
    void SmudgeAt(double x, double y, double mx, double my)
    {
        if (Math.Abs(mx) < 1e-6 && Math.Abs(my) < 1e-6) return;
        var image = img!;
        int h = image.Height;
        int x0 = Math.Max(0, (int)Math.Floor(x - rx)), x1 = Math.Min(image.Width - 1, (int)Math.Ceiling(x + rx));
        int y0 = Math.Max(0, (int)Math.Floor(y - ry)), y1 = Math.Min(h - 1, (int)Math.Ceiling(y + ry));
        session.History.Extend((int)Math.Floor(x0 - Math.Abs(mx) - 1), (int)Math.Ceiling(x1 + Math.Abs(mx) + 2));
        double soft = 1 - Math.Clamp(brush.Hardness, 0, 1);
        int reach = (int)Math.Ceiling(Math.Abs(mx)) + 1;
        var (srcL, srcR, srcN, xs) = Columns(image, x0 - reach, x1 + reach + 1);
        int off = xs * h;
        for (int cx = x0; cx <= x1; cx++)
            for (int cy = y0; cy <= y1; cy++)
            {
                double u = (cx + 0.5 - x) / rx, v = (cy + 0.5 - y) / ry;
                double cov = EdgeCoverage(Math.Sqrt(u * u + v * v), Math.Max(0.3, soft)) * brush.Opacity;
                if (cov <= 0) continue;
                // Sample the cell the brush came from (bilinear would be smoother; nearest is enough
                // because stamps are dense).
                int sx = Math.Clamp((int)Math.Round(cx - mx), Math.Max(0, x0 - reach), Math.Min(image.Width - 1, x1 + reach));
                int sy = Math.Clamp((int)Math.Round(cy - my), 0, h - 1);
                int i = cx * h + cy, j = sx * h + sy;
                image.Left[i] = (float)(srcL[i - off] + (srcL[j - off] - srcL[i - off]) * cov);
                image.Right[i] = (float)(srcR[i - off] + (srcR[j - off] - srcR[i - off]) * cov);
                if (srcN is not null) image.Noise![i] = (float)(srcN[i - off] + (srcN[j - off] - srcN[i - off]) * cov);
            }
    }

    /// <summary>
    /// Copies columns [xa, xb) (clipped to the image) of the layers; returns the copies and the
    /// first column copied (cell (x, y) is at index x·Height + y − first·Height).
    /// </summary>
    /// <param name="image">Source image.</param>
    /// <param name="xa">First column (inclusive).</param>
    /// <param name="xb">End column (exclusive).</param>
    /// <returns>Left, right, noise (<c>null</c> when the image has no noise layer) and the first column.</returns>
    static (float[] L, float[] R, float[]? N, int First) Columns(SpectralImage image, int xa, int xb)
    {
        xa = Math.Clamp(xa, 0, image.Width);
        xb = Math.Clamp(xb, xa, image.Width);
        int h = image.Height, n = (xb - xa) * h;
        var l = new float[n];
        var r = new float[n];
        Array.Copy(image.Left, xa * h, l, 0, n);
        Array.Copy(image.Right, xa * h, r, 0, n);
        float[]? nz = null;
        if (image.Noise is { } src)
        {
            nz = new float[n];
            Array.Copy(src, xa * h, nz, 0, n);
        }
        return (l, r, nz, xa);
    }

    /// <summary>Notifies the columns touched since the last flush (canvas repaint, live upload).</summary>
    /// <remarks>Called once per <see cref="BeginStroke"/> / <see cref="StrokeTo"/>, not per stamp, to batch the notifications of a whole pointer move.</remarks>
    void Flush()
    {
        if (img is null || maxX < minX) return;
        img.NotifyChanged(minX, maxX + 1);
        minX = int.MaxValue; maxX = int.MinValue;
    }
}
