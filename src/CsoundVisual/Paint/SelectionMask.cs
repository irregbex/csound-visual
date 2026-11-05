// SelectionMask.cs — Paint Rooms foundation: shaped, soft-edged selections.
//
// Role: a selection is no longer just a rectangle of cells. A SelectionMask is a bounding CellRect
// plus a COVERAGE value 0…1 for every cell inside it: 1 = fully selected, 0 = not selected, values in
// between = partly selected (the anti-aliased rim of an ellipse or a freehand shape, or a feathered
// edge). Every operation that uses the selection blends its result by the coverage:
//     new value = old + (operation result − old) · coverage
// so a spectral edit fades out over the rim instead of stopping at a hard step (a hard step in a
// spectrogram becomes a click in time or a ringing edge in frequency).
//
// Shapes (all built in IMAGE units — x = fractional column, y = fractional row, row 0 at the
// bottom, cell (x, y) covering [x, x+1) × [y, y+1), the same frame as PaintCanvas.ToImage):
//   * Rectangle — whole cells (kept without a coverage array: «IsRectangle»);
//   * Ellipse   — a box dragged on screen (Shift = a circle in SCREEN pixels, converted by the canvas);
//   * Polygon   — the lasso: any closed outline, self-intersections allowed (non-zero winding);
//   * Spline    — a closed Catmull-Rom curve through clicked points, sampled into a polygon.
// Combining: Replace, Add (union = maximum coverage) and Subtract (a · (1 − b)).
// Feather: a separable box blur of the coverage (3 passes ≈ Gaussian), anisotropic radii so the
// soft edge has the same width ON SCREEN in time and pitch.
//
// Coverage anti-aliasing (Rasterize): each cell is sampled on SUB = 4 horizontal scanlines (at
// y = cell + (k + 0.5)/4). On each scanline the shape's interior is a set of x-intervals; every
// interval adds, to each cell it crosses, the EXACT fraction of the cell's width it covers. So the
// coverage is exact horizontally and 4× supersampled vertically: 0…1 in steps no coarser than 1/4
// vertically, continuous horizontally.
//
// Outline (marching ants): the 0.5-coverage contour, traced along cell edges into closed loops
// (TraceOutline), so the dashed line follows the real shape of ANY mask, including combined and
// feathered ones.
//
// Immutable: every operation returns a new mask (PaintSession compares selections by reference).
// Pure computation, any thread.

namespace CsoundVisual.Paint;

/// <summary>How a newly drawn shape combines with the existing selection.</summary>
public enum SelectionCombine
{
    /// <summary>The shape becomes the selection (no modifier).</summary>
    Replace,
    /// <summary>Union with the existing selection (Shift): coverage = max(old, shape).</summary>
    Add,
    /// <summary>Removes the shape from the existing selection (Alt): coverage = old · (1 − shape).</summary>
    Subtract,
}

/// <summary>A selection of image cells with a per-cell coverage 0…1 (see the file header).</summary>
public sealed class SelectionMask
{
    /// <summary>Vertical supersampling of the rasteriser: scanlines per cell row.</summary>
    const int Sub = 4;

    /// <summary>Coverage below this is treated as «not selected» (trims bounds, ignores float dust).</summary>
    const float Epsilon = 1f / 512;

    /// <summary>
    /// Per-cell coverage over <see cref="Bounds"/>, column-major like <see cref="SpectralImage"/>
    /// (index = (x − X0) · Bounds.Height + (y − Y0)); <c>null</c> = every cell of the bounds is 1.
    /// </summary>
    readonly float[]? coverage;

    /// <summary>Lazily traced outline (see <see cref="Outline"/>).</summary>
    IReadOnlyList<(double X, double Y)[]>? outline;

    /// <summary>Creates a mask (callers go through the factories, which trim and validate).</summary>
    /// <param name="bounds">Non-empty bounding rectangle, inside the image.</param>
    /// <param name="coverage">Coverage over the bounds (column-major) or <c>null</c> for a full rectangle.</param>
    SelectionMask(CellRect bounds, float[]? coverage)
    {
        Bounds = bounds;
        this.coverage = coverage;
    }

    /// <summary>The smallest rectangle containing every selected cell (half-open, like every <see cref="CellRect"/>).</summary>
    public CellRect Bounds { get; }

    /// <summary>True for a plain rectangle (every cell of <see cref="Bounds"/> fully selected).</summary>
    public bool IsRectangle => coverage is null;

    /// <summary>A plain rectangular selection (the M tool, Ctrl+A, the pitch-ruler band).</summary>
    /// <param name="r">The rectangle; it is assumed to lie inside the image.</param>
    /// <returns>The mask, or <c>null</c> for an empty rectangle.</returns>
    public static SelectionMask? Rectangle(CellRect r) => r.IsEmpty ? null : new SelectionMask(r, null);

    /// <summary>Lets code written for rectangles assign a <see cref="CellRect"/> where a selection is expected.</summary>
    /// <param name="r">The rectangle.</param>
    public static implicit operator SelectionMask?(CellRect r) => Rectangle(r);

    /// <summary>Coverage of cell (x, y): 0 outside the bounds.</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    public float this[int x, int y]
    {
        get
        {
            if (!Bounds.Contains(x, y)) return 0;
            return coverage is null ? 1 : coverage[(x - Bounds.X0) * Bounds.Height + (y - Bounds.Y0)];
        }
    }

    /// <summary>True when cell (x, y) is at least half selected (hit test for «drag inside to move»).</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    public bool Contains(int x, int y) => this[x, y] >= 0.5f;

    /// <summary>Sum of the coverage = selected area in cells (a full cell counts 1).</summary>
    public double Area
    {
        get
        {
            if (coverage is null) return (double)Bounds.Width * Bounds.Height;
            double a = 0;
            foreach (float c in coverage) a += c;
            return a;
        }
    }

    /// <summary>The coverage of every cell of <see cref="Bounds"/>, column-major (a new array; all ones for a rectangle).</summary>
    /// <returns>Width·Height values.</returns>
    public float[] ToArray()
    {
        if (coverage is not null) return (float[])coverage.Clone();
        var a = new float[Bounds.Width * Bounds.Height];
        Array.Fill(a, 1f);
        return a;
    }

    // ------------------------------------------------------------------------------------------
    //  Shapes
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// An anti-aliased ellipse inscribed in the box (xa, ya)–(xb, yb), image units, clipped to a
    /// <paramref name="width"/> × <paramref name="height"/> image.
    /// </summary>
    /// <param name="xa">One corner, fractional column.</param>
    /// <param name="ya">One corner, fractional row.</param>
    /// <param name="xb">Opposite corner, fractional column.</param>
    /// <param name="yb">Opposite corner, fractional row.</param>
    /// <param name="width">Image columns (clip).</param>
    /// <param name="height">Image rows (clip).</param>
    /// <returns>The mask, or <c>null</c> when nothing of it lies on the image or it has no area.</returns>
    /// <remarks>
    /// Centre (cx, cy), semi-axes a = |xb − xa|/2, b = |yb − ya|/2. On a scanline at height y the
    /// ellipse (x−cx)²/a² + (y−cy)²/b² ≤ 1 is the interval cx ± a·√(1 − ((y−cy)/b)²), empty when
    /// |y − cy| ≥ b. Its area is π·a·b (the unit tests check the coverage sum against it).
    /// </remarks>
    public static SelectionMask? Ellipse(double xa, double ya, double xb, double yb, int width, int height)
    {
        double cx = (xa + xb) / 2, cy = (ya + yb) / 2, a = Math.Abs(xb - xa) / 2, b = Math.Abs(yb - ya) / 2;
        if (a <= 1e-9 || b <= 1e-9) return null;
        return Rasterize(cx - a, cy - b, cx + a, cy + b, width, height, (y, spans) =>
        {
            double t = (y - cy) / b;
            if (Math.Abs(t) >= 1) return;
            double half = a * Math.Sqrt(1 - t * t);
            spans.Add((cx - half, cx + half));
        });
    }

    /// <summary>
    /// A closed polygon (the lasso, or a sampled spline), anti-aliased, clipped to the image. The
    /// last point connects back to the first; self-intersecting outlines are filled with the
    /// non-zero winding rule (a figure-eight fills both loops, a loop drawn twice stays filled).
    /// </summary>
    /// <param name="points">Vertices in image units (at least 3).</param>
    /// <param name="width">Image columns (clip).</param>
    /// <param name="height">Image rows (clip).</param>
    /// <returns>The mask, or <c>null</c> for fewer than 3 points or no area on the image.</returns>
    /// <remarks>
    /// Scanline fill: for each scanline y, every polygon edge (p, q) whose y-range contains y
    /// (half-open: min ≤ y &lt; max, so a vertex exactly on a scanline is counted once) crosses it
    /// at x = p.X + (y − p.Y)·(q.X − p.X)/(q.Y − p.Y), with direction +1 (upward edge) or −1
    /// (downward). Sorted by x, a running sum of the directions is the winding number; the spans
    /// where it is non-zero are inside. Cost: O(scanlines × edges), fine for a few thousand points.
    /// </remarks>
    public static SelectionMask? Polygon(IReadOnlyList<(double X, double Y)> points, int width, int height)
    {
        if (points.Count < 3) return null;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in points)
        {
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        if (maxX - minX <= 1e-9 || maxY - minY <= 1e-9) return null;
        var pts = points.ToArray();
        var crossings = new List<(double X, int Dir)>();
        return Rasterize(minX, minY, maxX, maxY, width, height, (y, spans) =>
        {
            crossings.Clear();
            for (int i = 0; i < pts.Length; i++)
            {
                var p = pts[i];
                var q = pts[(i + 1) % pts.Length];   // closing edge: last → first
                if (p.Y == q.Y) continue;            // horizontal edges never cross a scanline
                bool up = q.Y > p.Y;
                double lo = up ? p.Y : q.Y, hi = up ? q.Y : p.Y;
                if (y < lo || y >= hi) continue;
                crossings.Add((p.X + (y - p.Y) * (q.X - p.X) / (q.Y - p.Y), up ? 1 : -1));
            }
            if (crossings.Count < 2) return;
            crossings.Sort((m, n) => m.X.CompareTo(n.X));
            int winding = 0;
            for (int k = 0; k < crossings.Count - 1; k++)
            {
                winding += crossings[k].Dir;
                if (winding != 0) spans.Add((crossings[k].X, crossings[k + 1].X));
            }
        });
    }

    /// <summary>A closed, smooth Catmull-Rom spline through <paramref name="points"/>, filled (see <see cref="CatmullRom"/>).</summary>
    /// <param name="points">Control points in image units (at least 3); the curve passes through every one.</param>
    /// <param name="width">Image columns (clip).</param>
    /// <param name="height">Image rows (clip).</param>
    /// <returns>The mask, or <c>null</c> for fewer than 3 points or no area.</returns>
    public static SelectionMask? Spline(IReadOnlyList<(double X, double Y)> points, int width, int height) =>
        points.Count < 3 ? null : Polygon(CatmullRom(points, closed: true), width, height);

    /// <summary>
    /// Samples a CENTRIPETAL Catmull-Rom spline through <paramref name="points"/> into a polyline
    /// (used for the spline selection and for its on-screen preview).
    /// </summary>
    /// <param name="points">Control points (any units; the curve passes through each of them).</param>
    /// <param name="closed">True: the curve also runs from the last point back to the first, smoothly.</param>
    /// <param name="samplesPerUnit">Samples per unit of chord length (≥ 4 per segment, ≤ 256).</param>
    /// <returns>The sampled points, starting at the first control point (the closing point is not repeated).</returns>
    /// <remarks>
    /// Segment Pᵢ → Pᵢ₊₁ uses the four points Pᵢ₋₁, Pᵢ, Pᵢ₊₁, Pᵢ₊₂ (wrapping when closed; an open curve
    /// repeats its end points). Knots tⱼ₊₁ = tⱼ + |Pⱼ₊₁ − Pⱼ|^½ («centripetal», α = ½): unlike the
    /// uniform variant it never forms cusps or loops inside a segment when points are unevenly
    /// spaced, so a quick sketch of 5–10 clicks gives the outline you expect. Each sample is the
    /// Barry–Goldman pyramid: three linear interpolations A₁…A₃, two B₁, B₂, then C — all of them
    /// blends of two points with weights from the knots. A Catmull-Rom curve may bulge slightly
    /// outside the polygon of its points (about 1/8 of a side for a square), never far.
    /// </remarks>
    public static List<(double X, double Y)> CatmullRom(IReadOnlyList<(double X, double Y)> points, bool closed, double samplesPerUnit = 1)
    {
        var result = new List<(double X, double Y)>();
        int n = points.Count;
        if (n == 0) return result;
        if (n < 3)
        {
            result.AddRange(points);
            return result;
        }
        (double X, double Y) P(int i) => closed ? points[((i % n) + n) % n] : points[Math.Clamp(i, 0, n - 1)];
        int segments = closed ? n : n - 1;
        for (int s = 0; s < segments; s++)
        {
            var p0 = P(s - 1); var p1 = P(s); var p2 = P(s + 1); var p3 = P(s + 2);
            // Knot spacing = square root of the chord length; a tiny floor avoids 0/0 for repeated points.
            static double Step((double X, double Y) a, (double X, double Y) b) => Math.Max(1e-6, Math.Sqrt(Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y))));
            double t0 = 0, t1 = t0 + Step(p0, p1), t2 = t1 + Step(p1, p2), t3 = t2 + Step(p2, p3);
            double chord = Math.Sqrt((p2.X - p1.X) * (p2.X - p1.X) + (p2.Y - p1.Y) * (p2.Y - p1.Y));
            int count = (int)Math.Clamp(Math.Ceiling(chord * samplesPerUnit), 4, 256);
            for (int k = 0; k < count; k++)
            {
                double t = t1 + (t2 - t1) * k / count;
                // Blend of a and b at t for the knot interval [ta, tb].
                static (double X, double Y) L((double X, double Y) a, (double X, double Y) b, double ta, double tb, double t)
                {
                    double u = (t - ta) / (tb - ta);
                    return (a.X + (b.X - a.X) * u, a.Y + (b.Y - a.Y) * u);
                }
                var a1 = L(p0, p1, t0, t1, t); var a2 = L(p1, p2, t1, t2, t); var a3 = L(p2, p3, t2, t3, t);
                var b1 = L(a1, a2, t0, t2, t); var b2 = L(a2, a3, t1, t3, t);
                result.Add(L(b1, b2, t1, t2, t));
            }
        }
        if (!closed) result.Add(points[n - 1]);
        return result;
    }

    // ------------------------------------------------------------------------------------------
    //  Combining, feathering, moving
    // ------------------------------------------------------------------------------------------

    /// <summary>Combines a new shape with the current selection.</summary>
    /// <param name="current">The existing selection, or <c>null</c>.</param>
    /// <param name="shape">The newly drawn shape, or <c>null</c> (nothing drawn).</param>
    /// <param name="mode">Replace, Add (max) or Subtract (current · (1 − shape)).</param>
    /// <returns>The resulting selection, or <c>null</c> when nothing remains selected.</returns>
    public static SelectionMask? Combine(SelectionMask? current, SelectionMask? shape, SelectionCombine mode)
    {
        switch (mode)
        {
            case SelectionCombine.Replace: return shape;
            case SelectionCombine.Add when current is null: return shape;
            case SelectionCombine.Add when shape is null: return current;
            case SelectionCombine.Subtract when current is null || shape is null: return current;
        }
        var a = current!;
        var b = shape!;
        // Add works on the union of the two bounds, Subtract only on the current bounds (it can
        // only shrink). FromCoverage trims the result to the cells that stay selected.
        var r = mode == SelectionCombine.Add
            ? new CellRect(Math.Min(a.Bounds.X0, b.Bounds.X0), Math.Min(a.Bounds.Y0, b.Bounds.Y0), Math.Max(a.Bounds.X1, b.Bounds.X1), Math.Max(a.Bounds.Y1, b.Bounds.Y1))
            : a.Bounds;
        var cov = new float[r.Width * r.Height];
        for (int x = r.X0; x < r.X1; x++)
            for (int y = r.Y0; y < r.Y1; y++)
            {
                float ca = a[x, y], cb = b[x, y];
                cov[(x - r.X0) * r.Height + (y - r.Y0)] = mode == SelectionCombine.Add ? Math.Max(ca, cb) : ca * (1 - cb);
            }
        return FromCoverage(r, cov);
    }

    /// <summary>
    /// A softened copy: the coverage is blurred so the edge fades over about ±<paramref name="radiusX"/>
    /// columns and ±<paramref name="radiusY"/> rows. Grows the bounds by the blur reach (clipped to the image).
    /// </summary>
    /// <param name="radiusX">Soft-edge half-width in columns (the canvas converts screen pixels with its zoom).</param>
    /// <param name="radiusY">Soft-edge half-width in rows.</param>
    /// <param name="width">Image columns (clip).</param>
    /// <param name="height">Image rows (clip).</param>
    /// <returns>The feathered mask (this mask itself when both radii are below half a cell).</returns>
    /// <remarks>
    /// Three passes of a box filter of half-width r ≈ radius/2 per axis (separable, running sums,
    /// O(cells) whatever the radius): the reach is ±3r ≈ ±1.5·radius and the profile across a
    /// straight edge is a smooth S from 1 to 0 that is monotonic (box filters never overshoot).
    /// Cells outside the image are treated as 0 (the blur sees the image border as unselected).
    /// </remarks>
    public SelectionMask Feathered(double radiusX, double radiusY, int width, int height)
    {
        int rx = (int)Math.Round(radiusX / 2), ry = (int)Math.Round(radiusY / 2);
        if (rx <= 0 && ry <= 0) return this;
        // Grown bounds: every cell the three passes can reach.
        var r = new CellRect(Math.Max(0, Bounds.X0 - 3 * rx), Math.Max(0, Bounds.Y0 - 3 * ry),
                             Math.Min(width, Bounds.X1 + 3 * rx), Math.Min(height, Bounds.Y1 + 3 * ry));
        int w = r.Width, h = r.Height;
        var cov = new float[w * h];
        for (int x = Bounds.X0; x < Bounds.X1; x++)
            for (int y = Bounds.Y0; y < Bounds.Y1; y++)
                cov[(x - r.X0) * h + (y - r.Y0)] = this[x, y];
        var line = new float[Math.Max(w, h)];
        for (int pass = 0; pass < 3; pass++)
        {
            if (rx > 0) for (int y = 0; y < h; y++) BoxLine(cov, line, w, rx, i => i * h + y);   // along time
            if (ry > 0) for (int x = 0; x < w; x++) BoxLine(cov, line, h, ry, i => x * h + i);   // along pitch
        }
        return FromCoverage(r, cov) ?? this;
    }

    /// <summary>One running-sum box filter over a line of the coverage (zeros beyond its ends).</summary>
    /// <param name="a">The coverage array, edited in place.</param>
    /// <param name="line">Scratch buffer at least <paramref name="len"/> long.</param>
    /// <param name="len">Cells on the line.</param>
    /// <param name="rad">Box half-width.</param>
    /// <param name="idx">Maps position 0…len−1 on the line to an index of <paramref name="a"/>.</param>
    static void BoxLine(float[] a, float[] line, int len, int rad, Func<int, int> idx)
    {
        for (int i = 0; i < len; i++) line[i] = a[idx(i)];
        float At(int i) => i < 0 || i >= len ? 0 : line[i];
        double sum = 0;
        for (int k = -rad; k <= rad; k++) sum += At(k);
        for (int i = 0; i < len; i++)
        {
            a[idx(i)] = (float)(sum / (2 * rad + 1));
            sum += At(i + rad + 1) - At(i - rad);
        }
    }

    /// <summary>The same shape moved by (dx, dy) cells, clipped to the image (cells moved off it are dropped).</summary>
    /// <param name="dx">Columns to the right.</param>
    /// <param name="dy">Rows up.</param>
    /// <param name="width">Image columns.</param>
    /// <param name="height">Image rows.</param>
    /// <returns>The moved mask, or <c>null</c> when it left the image completely.</returns>
    public SelectionMask? Translated(int dx, int dy, int width, int height)
    {
        var moved = new CellRect(Bounds.X0 + dx, Bounds.Y0 + dy, Bounds.X1 + dx, Bounds.Y1 + dy);
        return FromCoverage(moved, coverage, width, height);
    }

    /// <summary>
    /// A mask from a coverage array over <paramref name="r"/>, clipped to a <paramref name="width"/> ×
    /// <paramref name="height"/> image (the rectangle may lie partly outside it, e.g. a paste near the edge).
    /// </summary>
    /// <param name="r">Rectangle the array covers (column-major, r.Width · r.Height values).</param>
    /// <param name="cov">Coverage values, or <c>null</c> for «all ones».</param>
    /// <param name="width">Image columns.</param>
    /// <param name="height">Image rows.</param>
    /// <returns>The trimmed mask, or <c>null</c> when nothing is selected on the image.</returns>
    public static SelectionMask? FromCoverage(CellRect r, float[]? cov, int width, int height)
    {
        var c = new CellRect(Math.Max(0, r.X0), Math.Max(0, r.Y0), Math.Min(width, r.X1), Math.Min(height, r.Y1));
        if (c.IsEmpty) return null;
        if (cov is null) return new SelectionMask(c, null);
        if (c == r) return FromCoverage(r, (float[])cov.Clone());
        var sub = new float[c.Width * c.Height];
        for (int x = c.X0; x < c.X1; x++)
            Array.Copy(cov, (x - r.X0) * r.Height + (c.Y0 - r.Y0), sub, (x - c.X0) * c.Height, c.Height);
        return FromCoverage(c, sub);
    }

    /// <summary>
    /// A mask that takes ownership of <paramref name="cov"/> over <paramref name="r"/>: trims the
    /// bounds to the cells above <see cref="Epsilon"/> and drops the array when everything is 1.
    /// </summary>
    /// <param name="r">Rectangle the array covers.</param>
    /// <param name="cov">Coverage (column-major); not copied when no trimming is needed.</param>
    /// <returns>The mask, or <c>null</c> when nothing is selected.</returns>
    static SelectionMask? FromCoverage(CellRect r, float[] cov)
    {
        int h = r.Height;
        int x0 = int.MaxValue, x1 = -1, y0 = int.MaxValue, y1 = -1;
        bool allOne = true;
        for (int x = 0; x < r.Width; x++)
            for (int y = 0; y < h; y++)
            {
                float v = cov[x * h + y];
                if (v < Epsilon) { cov[x * h + y] = 0; allOne = false; continue; }
                if (v < 1) allOne = false;
                if (v > 1) cov[x * h + y] = 1;
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
            }
        if (x1 < 0) return null;
        if (allOne) return new SelectionMask(r, null);
        var t = new CellRect(r.X0 + x0, r.Y0 + y0, r.X0 + x1 + 1, r.Y0 + y1 + 1);
        if (t == r) return new SelectionMask(r, cov);
        var sub = new float[t.Width * t.Height];
        for (int x = 0; x < t.Width; x++)
            Array.Copy(cov, (x + x0) * h + y0, sub, x * t.Height, t.Height);
        return new SelectionMask(t, sub);
    }

    // ------------------------------------------------------------------------------------------
    //  Rasteriser
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Shared anti-aliased scanline rasteriser (see the file header). <paramref name="spansAt"/>
    /// receives a scanline height y (image units) and adds the shape's interior x-intervals there.
    /// </summary>
    /// <param name="minX">Shape bounding box, image units (used to limit the work).</param>
    /// <param name="minY">Bounding box bottom.</param>
    /// <param name="maxX">Bounding box right.</param>
    /// <param name="maxY">Bounding box top.</param>
    /// <param name="width">Image columns (clip).</param>
    /// <param name="height">Image rows (clip).</param>
    /// <param name="spansAt">Fills the list with the interior intervals (xa ≤ xb) of the scanline at y.</param>
    /// <returns>The mask, or <c>null</c> when nothing of the shape covers the image.</returns>
    /// <remarks>
    /// Each scanline contributes 1/Sub of a cell's height. An interval [xa, xb) adds to cell column
    /// c the length of [xa, xb) ∩ [c, c+1), i.e. the exact horizontal fraction it covers; the cells
    /// strictly between the end cells get a full 1/Sub each. The sum over the Sub scanlines of a cell
    /// row is therefore the area fraction of the cell inside the shape (up to the vertical sampling).
    /// </remarks>
    static SelectionMask? Rasterize(double minX, double minY, double maxX, double maxY, int width, int height,
        Action<double, List<(double A, double B)>> spansAt)
    {
        var r = new CellRect(Math.Max(0, (int)Math.Floor(minX)), Math.Max(0, (int)Math.Floor(minY)),
                             Math.Min(width, (int)Math.Floor(maxX) + 1), Math.Min(height, (int)Math.Floor(maxY) + 1));
        if (r.IsEmpty) return null;
        int h = r.Height;
        var cov = new float[r.Width * h];
        var spans = new List<(double A, double B)>();
        const float w = 1f / Sub;
        for (int row = r.Y0; row < r.Y1; row++)
            for (int k = 0; k < Sub; k++)
            {
                double y = row + (k + 0.5) / Sub;     // scanline through the middle of the sub-row
                spans.Clear();
                spansAt(y, spans);
                foreach (var (sa, sb) in spans)
                {
                    // Clip the interval to the mask's columns.
                    double a = Math.Max(sa, r.X0), b = Math.Min(sb, r.X1);
                    if (b <= a) continue;
                    int ca = (int)Math.Floor(a), cb = (int)Math.Floor(b);
                    if (cb >= r.X1) cb = r.X1 - 1;
                    int yi = row - r.Y0;
                    if (ca == cb)
                    {
                        cov[(ca - r.X0) * h + yi] += (float)(b - a) * w;   // both ends in one cell
                        continue;
                    }
                    cov[(ca - r.X0) * h + yi] += (float)(ca + 1 - a) * w;  // partial first cell
                    for (int c = ca + 1; c < cb; c++) cov[(c - r.X0) * h + yi] += w;
                    cov[(cb - r.X0) * h + yi] += (float)(b - cb) * w;      // partial last cell
                }
            }
        return FromCoverage(r, cov);
    }

    // ------------------------------------------------------------------------------------------
    //  Outline
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The boundary of the selected region (cells with coverage ≥ 0.5) as closed loops of points
    /// in image units (cell corners), with collinear points removed. Computed once and cached.
    /// </summary>
    /// <remarks>
    /// Tracing: every cell edge between an inside and an outside cell becomes a DIRECTED unit edge
    /// with the inside on its left (counter-clockwise around the region, y up). Edges are chained
    /// end-to-start into loops; at a corner shared by two diagonal cells two edges leave the same
    /// vertex, and either choice yields valid closed loops. Collinear runs are merged so a straight
    /// side is one segment — this keeps the dash pattern of the marching ants continuous.
    /// </remarks>
    public IReadOnlyList<(double X, double Y)[]> Outline => outline ??= TraceOutline();

    /// <summary>Implements <see cref="Outline"/>.</summary>
    /// <returns>The loops (each closed implicitly: the last point connects to the first).</returns>
    List<(double X, double Y)[]> TraceOutline()
    {
        var loops = new List<(double X, double Y)[]>();
        if (coverage is null)
        {
            var b = Bounds;
            loops.Add([(b.X0, b.Y0), (b.X1, b.Y0), (b.X1, b.Y1), (b.X0, b.Y1)]);
            return loops;
        }
        bool In(int x, int y) => this[x, y] >= 0.5f;
        // Outgoing edges per start vertex; key = packed (x, y).
        var next = new Dictionary<long, List<(int X, int Y)>>();
        static long Key(int x, int y) => ((long)x << 32) | (uint)y;
        void AddEdge(int x0, int y0, int x1, int y1)
        {
            if (!next.TryGetValue(Key(x0, y0), out var l)) next[Key(x0, y0)] = l = [];
            l.Add((x1, y1));
        }
        var bb = Bounds;
        for (int x = bb.X0; x <= bb.X1; x++)
            for (int y = bb.Y0; y <= bb.Y1; y++)
            {
                bool c = In(x, y);
                // Vertical edge at x between cells (x−1, y) and (x, y): inside on the right → go down; inside on the left → go up.
                bool l = In(x - 1, y);
                if (c && !l) AddEdge(x, y + 1, x, y);
                else if (l && !c) AddEdge(x, y, x, y + 1);
                // Horizontal edge at y between cells (x, y−1) and (x, y): inside above → go right (inside on the left); inside below → go left.
                bool d = In(x, y - 1);
                if (c && !d) AddEdge(x, y, x + 1, y);
                else if (d && !c) AddEdge(x + 1, y, x, y);
            }
        while (next.Count > 0)
        {
            var startKey = next.Keys.First();
            int sx = (int)(startKey >> 32), sy = (int)(uint)startKey;
            var loop = new List<(double X, double Y)>();
            int cx = sx, cy = sy;
            while (next.TryGetValue(Key(cx, cy), out var outs))
            {
                var (nx, ny) = outs[^1];
                outs.RemoveAt(outs.Count - 1);
                if (outs.Count == 0) next.Remove(Key(cx, cy));
                loop.Add((cx, cy));
                cx = nx; cy = ny;
                if (cx == sx && cy == sy) break;
            }
            // Merge collinear points: drop a point when the edges before and after it are parallel.
            var simple = new List<(double X, double Y)>();
            for (int i = 0; i < loop.Count; i++)
            {
                var p = loop[(i - 1 + loop.Count) % loop.Count]; var q = loop[i]; var s = loop[(i + 1) % loop.Count];
                double cross = (q.X - p.X) * (s.Y - q.Y) - (q.Y - p.Y) * (s.X - q.X);
                if (Math.Abs(cross) > 1e-9) simple.Add(q);
            }
            if (simple.Count >= 3) loops.Add([.. simple]);
        }
        return loops;
    }
}
