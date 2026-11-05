// SpectralImage.cs — Paint Rooms foundation.
//
// Role: the canvas data shared by every Paint Rooms room. An image is a grid of
//   Width  time columns (left → right = time; the whole width lasts DurationSeconds) ×
//   Height pitch rows    (bottom → top = low → high; row r sounds at Axis.FrequencyOf(r) Hz)
// with TWO float layers, Left and Right, holding a «level» 0…1 per cell (0 = silence). A mono
// image simply has identical layers. Levels are what the user paints (like pixel brightness);
// the synthesis converts them to linear amplitudes with a curve (see PaintSynthSettings.Curve).
//
// Memory layout: COLUMN-MAJOR, index = x·Height + y. One column (one moment in time) is a
// contiguous run of Height floats, which is exactly what the Csound instrument reads per control
// period (one spectral frame) and what the table upload copies — so live edits of a few columns
// translate into a few contiguous memory copies.
//
// Threading: an image is mutated on the UI thread only (brushes, operations). Other threads get
// copies (the synthesis converts columns to new arrays before handing them to Csound; background
// jobs such as the Spectrum Room's mask render work on a Clone taken on the UI thread).
//
// Who uses it: PaintSession (current image + undo history of image objects), PaintCanvas (draws
// it, applies brushes), ImageOps (image menu), the live player and PaintRenderer (synthesis),
// SpectralAnalysis (sound → image) and SpectralMask (image → gains). The meaning of a level
// depends on the room: Paint Synth levels go through PaintSynthSettings.Curve; Spectrum Room
// levels are linear in dB above a floor (SpectralAnalysis.DbFromLevel / ToSynthLevels convert).
//
// Edit protocol: write into Left/Right (or Set), then call NotifyChanged(x0, x1) once for the
// touched column range; that bumps Version and raises Changed so the observers update.
//
// Optional third layer — NOISINESS (Noise, null when unused): per cell 0…1, how noisy versus tonal
// the cell sounds (0 = a pure sine, 1 = a narrow band of noise around the row's pitch, values in
// between = an equal-power crossfade of both, see PaintCsoundCode). It is NOT a level: a cell's
// loudness still comes from Left/Right only, so the noise layer never makes anything louder. It is
// shared by both channels (a stereo picture has one noisiness per cell). It is created on demand
// (EnsureNoise) by the noise brush, the «RGB: B = noise» picture import and the «noise amount»
// image operation, and travels with every copy of the pixels: Clone, CopyFrom, Retuned, the undo
// history, the selection operations and the clipboard. Pictures that never used it keep
// Noise == null and cost nothing extra (memory, CPU, synthesis), so older behaviour is unchanged.

namespace CsoundVisual.Paint;

/// <summary>Which layer(s) of a <see cref="SpectralImage"/> an operation affects.</summary>
[Flags]
public enum PaintLayers
{
    /// <summary>Neither layer.</summary>
    None = 0,
    /// <summary>The left channel layer.</summary>
    Left = 1,
    /// <summary>The right channel layer.</summary>
    Right = 2,
    /// <summary>Both layers.</summary>
    Both = Left | Right,
}

/// <summary>
/// A time × pitch canvas with a left and a right level layer (0…1 per cell), a duration and a
/// <see cref="PitchAxis"/>. Column-major storage: index = x·<see cref="Height"/> + y, row 0 = lowest pitch.
/// </summary>
public sealed class SpectralImage
{
    /// <summary>Largest width accepted (columns).</summary>
    public const int MaxWidth = 8192;

    /// <summary>Smallest width accepted (columns).</summary>
    public const int MinWidth = 2;

    /// <summary>Backing field of <see cref="DurationSeconds"/> (seconds, always valid after the setter's clamp).</summary>
    double duration;

    /// <summary>Creates a silent image.</summary>
    /// <param name="width">Time columns, <see cref="MinWidth"/>…<see cref="MaxWidth"/>.</param>
    /// <param name="axis">Pitch axis; its <see cref="PitchAxis.RowCount"/> becomes the height.</param>
    /// <param name="durationSeconds">Seconds the whole width lasts when played (&gt; 0).</param>
    /// <remarks>
    /// Out-of-range widths are clamped silently, so callers must read <see cref="Width"/> back
    /// rather than assume the value they passed. Memory: 2 layers × Width × Height × 4 bytes
    /// (8192 × 512 ≈ 32 MB).
    /// </remarks>
    public SpectralImage(int width, PitchAxis axis, double durationSeconds)
    {
        Width = Math.Clamp(width, MinWidth, MaxWidth);
        Axis = axis;
        Height = axis.RowCount;
        DurationSeconds = durationSeconds;
        Left = new float[Width * Height];
        Right = new float[Width * Height];
    }

    /// <summary>
    /// Optional noisiness layer, 0…1 per cell (0 = pure tone, 1 = noise band), column-major like
    /// <see cref="Left"/>; <c>null</c> while the picture has never used noise (see the file header).
    /// </summary>
    /// <remarks>
    /// Shared by both channels. Mutable in place on the UI thread like the level layers; create it
    /// with <see cref="EnsureNoise"/>. A layer that exists but holds only zeros sounds exactly like
    /// no layer (see <see cref="HasNoise"/>).
    /// </remarks>
    public float[]? Noise { get; private set; }

    /// <summary>Returns the noise layer, creating it (all zeros = all tonal) when it does not exist yet.</summary>
    /// <returns>The layer array (Width·Height floats).</returns>
    /// <remarks>Creating the layer does not change the sound and raises no event.</remarks>
    public float[] EnsureNoise() => Noise ??= new float[Width * Height];

    /// <summary>Removes the noise layer (every cell becomes tonal again). Call <see cref="NotifyChanged()"/> afterwards.</summary>
    public void DropNoise() => Noise = null;

    /// <summary>
    /// True when the noise layer exists AND at least one cell has a noisiness above 0.0005 (about
    /// 1/2000): only then does the synthesis need its per-cell tone/noise crossfade.
    /// </summary>
    /// <returns><c>false</c> for a missing or all-zero layer (one linear scan, early exit at the first noisy cell).</returns>
    public bool HasNoise()
    {
        if (Noise is not { } n) return false;
        foreach (float v in n) if (v > 0.0005f) return true;
        return false;
    }

    /// <summary>Noisiness of a cell (0 outside the image or without a noise layer).</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <returns>0…1 (0 = tonal).</returns>
    public float GetNoise(int x, int y) => Noise is { } n && Contains(x, y) ? n[x * Height + y] : 0f;

    /// <summary>Number of time columns.</summary>
    public int Width { get; }

    /// <summary>Number of pitch rows (always <c>Axis.RowCount</c>).</summary>
    public int Height { get; }

    /// <summary>Row ↔ frequency mapping. Replacing the axis needs a new image (see <see cref="Retuned"/>).</summary>
    public PitchAxis Axis { get; }

    /// <summary>Left layer levels 0…1, column-major (index = x·Height + y). Mutable in place (UI thread).</summary>
    public float[] Left { get; }

    /// <summary>Right layer levels 0…1, column-major (index = x·Height + y). Mutable in place (UI thread).</summary>
    public float[] Right { get; }

    /// <summary>Seconds the whole width lasts (0.05…3600). Changing it does not touch the pixels.</summary>
    /// <remarks>Non-finite values (NaN, ±∞) become 10 s; finite ones are clamped. Does not raise <see cref="Changed"/>.</remarks>
    public double DurationSeconds
    {
        get => duration;
        set => duration = double.IsFinite(value) ? Math.Clamp(value, 0.05, 3600) : 10;
    }

    /// <summary>Seconds per column.</summary>
    /// <remarks>Column x spans [x, x+1)·SecondsPerColumn; its centre is at (x + 0.5)·SecondsPerColumn.</remarks>
    public double SecondsPerColumn => DurationSeconds / Width;

    /// <summary>
    /// Incremented by every <see cref="NotifyChanged(int, int)"/>; lets observers (canvas bitmap, live
    /// synthesis) tell whether they are up to date without comparing pixels.
    /// </summary>
    public long Version { get; private set; }

    /// <summary>
    /// Raised (UI thread) after pixels changed, with the dirty column range
    /// [<c>x0</c>, <c>x1</c>) (exclusive end). The canvas repaints that part of its bitmap and the
    /// live synthesis re-uploads those columns to Csound.
    /// </summary>
    public event Action<int, int>? Changed;

    /// <summary>Linear index of cell (x, y): x·Height + y.</summary>
    /// <param name="x">Column (time).</param>
    /// <param name="y">Row (pitch, 0 = lowest).</param>
    /// <returns>Index into <see cref="Left"/>/<see cref="Right"/>; not range-checked.</returns>
    public int Index(int x, int y) => x * Height + y;

    /// <summary>True when (x, y) is inside the image.</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <returns>
    /// <c>true</c> for 0 ≤ x &lt; Width and 0 ≤ y &lt; Height (the unsigned casts turn negative
    /// values into huge ones, so one comparison per axis checks both bounds).
    /// </returns>
    public bool Contains(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

    /// <summary>Left level of a cell (0 outside the image).</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <returns>Level 0…1.</returns>
    public float GetLeft(int x, int y) => Contains(x, y) ? Left[x * Height + y] : 0f;

    /// <summary>Right level of a cell (0 outside the image).</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <returns>Level 0…1.</returns>
    public float GetRight(int x, int y) => Contains(x, y) ? Right[x * Height + y] : 0f;

    /// <summary>Sets both levels of a cell (clamped to 0…1); ignored outside the image. Call <see cref="NotifyChanged()"/> afterwards.</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <param name="left">New left level.</param>
    /// <param name="right">New right level.</param>
    public void Set(int x, int y, float left, float right)
    {
        if (!Contains(x, y)) return;
        int i = x * Height + y;
        Left[i] = Math.Clamp(left, 0f, 1f);
        Right[i] = Math.Clamp(right, 0f, 1f);
    }

    /// <summary>Announces that columns [x0, x1) changed (bumps <see cref="Version"/> and raises <see cref="Changed"/>).</summary>
    /// <param name="x0">First changed column (clamped to the image).</param>
    /// <param name="x1">Column after the last changed one (clamped); an empty range does nothing.</param>
    /// <remarks>UI thread: handlers (canvas, live player) run synchronously inside this call.</remarks>
    public void NotifyChanged(int x0, int x1)
    {
        x0 = Math.Clamp(x0, 0, Width);
        x1 = Math.Clamp(x1, 0, Width);
        if (x1 <= x0) return;
        Version++;
        Changed?.Invoke(x0, x1);
    }

    /// <summary>Announces that the whole image changed.</summary>
    public void NotifyChanged() => NotifyChanged(0, Width);

    /// <summary>True when any cell differs between the layers by more than <paramref name="tolerance"/> (the synthesis then needs two oscillator banks).</summary>
    /// <param name="tolerance">Largest level difference still considered «the same» (absorbs float rounding).</param>
    /// <returns><c>true</c> at the first differing cell (early exit); <c>false</c> after scanning everything.</returns>
    public bool IsStereo(float tolerance = 1e-4f)
    {
        var l = Left; var r = Right;
        for (int i = 0; i < l.Length; i++)
            if (MathF.Abs(l[i] - r[i]) > tolerance)
                return true;
        return false;
    }

    /// <summary>True when every cell of both layers is 0.</summary>
    /// <returns><c>false</c> as soon as one positive level is found.</returns>
    public bool IsSilent()
    {
        foreach (float v in Left) if (v > 0) return false;
        foreach (float v in Right) if (v > 0) return false;
        return true;
    }

    /// <summary>Deep copy (pixels, duration, axis).</summary>
    /// <returns>
    /// A new, independent object: <see cref="Version"/> restarts at 0 and no <see cref="Changed"/>
    /// subscribers are copied. The <see cref="PitchAxis"/> instance is shared, not copied (axes are
    /// never modified after construction).
    /// </returns>
    public SpectralImage Clone()
    {
        var c = new SpectralImage(Width, Axis, DurationSeconds);
        Array.Copy(Left, c.Left, Left.Length);
        Array.Copy(Right, c.Right, Right.Length);
        // The noise layer is copied only when it exists, so a tonal picture stays layer-free.
        if (Noise is { } n) Array.Copy(n, c.EnsureNoise(), n.Length);
        return c;
    }

    /// <summary>Copies the pixels of <paramref name="source"/> (same size) into this image and notifies.</summary>
    /// <param name="source">Image with the same width and height (the axis is not compared).</param>
    /// <exception cref="ArgumentException">The sizes differ.</exception>
    /// <remarks>The noise layer follows the source: copied when it has one, dropped when it has none.</remarks>
    public void CopyFrom(SpectralImage source)
    {
        if (source.Width != Width || source.Height != Height)
            throw new ArgumentException("Images must have the same size.");
        Array.Copy(source.Left, Left, Left.Length);
        Array.Copy(source.Right, Right, Right.Length);
        if (source.Noise is { } n) Array.Copy(n, EnsureNoise(), n.Length);
        else Noise = null;
        NotifyChanged();
    }

    /// <summary>
    /// A new image with another width (time resolution) and/or another pitch axis, keeping the
    /// sound as close as possible: horizontally the columns are linearly interpolated (when
    /// stretching) or averaged (when compressing); vertically every painted pitch moves to the row
    /// of the new axis that is nearest <b>in frequency</b>, so a chord stays the same chord when
    /// switching for example from a logarithmic axis to 12-tone equal temperament.
    /// </summary>
    /// <param name="newWidth">Columns of the result.</param>
    /// <param name="newAxis">Axis of the result (null = same axis).</param>
    /// <returns>A new image with the same duration; this image is not modified.</returns>
    /// <remarks>
    /// Two vertical cases are combined: when the new axis is COARSER, several source rows fold onto
    /// one destination row (max); when it is FINER, destination rows that received nothing are
    /// filled by linear interpolation between the two source rows around their frequency
    /// (<see cref="PitchAxis.RowOf"/> gives the fractional source row). Pitches outside the new
    /// axis' range by more than half a row (<see cref="PitchAxis.NearestRow"/> returns −1) are dropped.
    /// The noise layer (when present) is resampled with the same horizontal rule; vertically a
    /// destination row that receives several source rows takes the noisiness of the LOUDEST of them
    /// (the one whose level won the max), and interpolated rows interpolate it linearly.
    /// </remarks>
    public SpectralImage Retuned(int newWidth, PitchAxis? newAxis = null)
    {
        newAxis ??= Axis;
        var dst = new SpectralImage(newWidth, newAxis, DurationSeconds);
        int h0 = Height, h1 = dst.Height;
        // 1) Vertical mapping table, computed once: for every source row, the nearest destination row (or -1).
        var rowMap = new int[h0];
        for (int y = 0; y < h0; y++)
            rowMap[y] = newAxis.NearestRow(Axis.FrequencyOf(y));
        // Destination rows that receive no source row (the new axis is finer) are interpolated
        // from the source at their exact frequency.
        var covered = new bool[h1];
        foreach (int r in rowMap) if (r >= 0) covered[r] = true;
        var interp = new double[h1];
        for (int y = 0; y < h1; y++)
            interp[y] = covered[y] ? -1 : Axis.RowOf(newAxis.FrequencyOf(y));

        // 2) Horizontal resampling into a temporary column, then the vertical mapping.
        var colL = new float[h0];
        var colR = new float[h0];
        // Noise layer (optional): resampled alongside; «loud» remembers, per destination row, the
        // level max(L, R) of the source row whose noisiness was taken (the loudest one wins).
        var colN = Noise is null ? null : new float[h0];
        var dstN = Noise is null ? null : dst.EnsureNoise();
        var loud = Noise is null ? null : new float[h1];
        for (int x = 0; x < dst.Width; x++)
        {
            ResampleColumn(x, dst.Width, colL, colR, colN);
            int o = x * h1;
            if (loud is not null) Array.Fill(loud, -1f);
            for (int y = 0; y < h0; y++)
            {
                int r = rowMap[y];
                if (r < 0) continue;
                // Several source rows may fold onto one note: keep the loudest (max), not the sum,
                // so a dense image does not saturate the coarse axis.
                if (colL[y] > dst.Left[o + r]) dst.Left[o + r] = colL[y];
                if (colR[y] > dst.Right[o + r]) dst.Right[o + r] = colR[y];
                if (dstN is not null)
                {
                    float lv = MathF.Max(colL[y], colR[y]);
                    if (lv > loud![r]) { loud[r] = lv; dstN[o + r] = colN![y]; }
                }
            }
            for (int y = 0; y < h1; y++)
            {
                // s = fractional source row of this (uncovered) destination row; −1 = covered,
                // or the frequency lies outside the source axis.
                double s = interp[y];
                if (s < 0 || s > h0 - 1) continue;
                int i0 = (int)s; int i1 = Math.Min(i0 + 1, h0 - 1);
                float t = (float)(s - i0);
                dst.Left[o + y] = colL[i0] + (colL[i1] - colL[i0]) * t;
                dst.Right[o + y] = colR[i0] + (colR[i1] - colR[i0]) * t;
                if (dstN is not null) dstN[o + y] = colN![i0] + (colN[i1] - colN[i0]) * t;
            }
        }
        return dst;
    }

    /// <summary>
    /// Fills <paramref name="colL"/>/<paramref name="colR"/> (length Height) with the source column
    /// at destination column <paramref name="x"/> of <paramref name="newWidth"/>: box average when
    /// compressing (several source columns per destination column), linear interpolation otherwise.
    /// </summary>
    /// <param name="x">Destination column.</param>
    /// <param name="newWidth">Destination width.</param>
    /// <param name="colL">Receives the left levels of the resampled column (source row order).</param>
    /// <param name="colR">Receives the right levels.</param>
    /// <param name="colN">Receives the noisiness with the same rule, or <c>null</c> to skip the noise layer.</param>
    void ResampleColumn(int x, int newWidth, float[] colL, float[] colR, float[]? colN = null)
    {
        // The noise source is only read when a destination column was asked for.
        var nz = colN is null ? null : Noise;
        int h = Height;
        // Source columns per destination column (> 1 = compressing). The 1.0001 threshold keeps
        // «same width» on the interpolation path, where it reduces to an exact copy (t = 0).
        double scale = (double)Width / newWidth;
        if (scale > 1.0001)
        {
            // Compressing: average the source columns covered by [x, x+1)·scale.
            int a = (int)Math.Floor(x * scale), b = Math.Min(Width, (int)Math.Ceiling((x + 1) * scale));
            Array.Clear(colL); Array.Clear(colR);
            if (colN is not null) Array.Clear(colN);
            for (int sx = a; sx < b; sx++)
                for (int y = 0; y < h; y++)
                {
                    colL[y] += Left[sx * h + y];
                    colR[y] += Right[sx * h + y];
                    if (nz is not null) colN![y] += nz[sx * h + y];
                }
            float inv = 1f / Math.Max(1, b - a);
            for (int y = 0; y < h; y++) { colL[y] *= inv; colR[y] *= inv; }
            if (colN is not null) for (int y = 0; y < h; y++) colN[y] *= inv;
        }
        else
        {
            // Stretching (or same width): sample at the centre of the destination column. Centres are
            // aligned: destination centre (x + 0.5) maps to source position (x + 0.5)·scale, and
            // source column i's centre is at i + 0.5, hence the −0.5 to get a fractional column index.
            // Before the first / after the last source centre the edge column is held (clamps).
            double s = (x + 0.5) * scale - 0.5;
            int i0 = Math.Clamp((int)Math.Floor(s), 0, Width - 1), i1 = Math.Min(i0 + 1, Width - 1);
            float t = (float)Math.Clamp(s - i0, 0, 1);
            for (int y = 0; y < h; y++)
            {
                colL[y] = Left[i0 * h + y] + (Left[i1 * h + y] - Left[i0 * h + y]) * t;
                colR[y] = Right[i0 * h + y] + (Right[i1 * h + y] - Right[i0 * h + y]) * t;
                if (nz is not null) colN![y] = nz[i0 * h + y] + (nz[i1 * h + y] - nz[i0 * h + y]) * t;
            }
        }
    }
}
