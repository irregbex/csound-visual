// ImageFilterTables.cs — Paint Rooms · «Image Filter»: turns the filter IMAGE into per-FFT-bin GAIN tables.
//
// Role: Csound filters with a phase vocoder. pvsanal cuts the signal into FFT frames of N samples;
// each frame has N/2 + 1 «bins» (bin k = the frequency k·sr/N, from DC to Nyquist, spaced sr/N Hz
// apart). pvsmaska multiplies the amplitude of every bin by the value at the same index of a table.
// So what Csound needs, for every moment, is ONE GAIN PER BIN — while the picture has one level per
// ROW on a musical pitch axis (logarithmic, equal-tempered, Scala…). This class does that
// translation in C#, once per column, and stores the result as two big column-major tables
// («gain images», one per channel) that the Csound instrument slices column by column — exactly the
// way Paint Synth's instrument slices its image tables.
//
// ROW → BIN MAPPING (FilterBinMap; computed once per axis / FFT size / sample rate):
//   * bin k covers the frequency band [f_k − Δ/2, f_k + Δ/2] with f_k = k·sr/N and Δ = sr/N;
//   * that band, expressed in fractional rows with PitchAxis.RowOf, spans [rowLo, rowHi];
//   * if at least one row CENTRE falls inside the band (low frequencies on a log axis, where one
//     47 Hz bin covers several rows), the bin takes the MAXIMUM level of those rows — a thin painted
//     line must not vanish because the FFT cannot resolve it (averaging would turn a single bright
//     row among four into −12 dB);
//   * otherwise (high frequencies, where many bins share one row) the bin interpolates LINEARLY
//     between the two rows around RowOf(f_k), i.e. linearly in log-frequency on a log axis — so a
//     painted row becomes a smooth bell-shaped band instead of a staircase;
//   * outside the axis (below the lowest row / above the highest) the edge rows are extended: what
//     you paint on the bottom row also governs everything below it (and DC); same at the top.
//
// LEVEL → GAIN: ImageFilterSettings.LevelToGain (linear or decibel rule, optional boost), applied
// after the mapping so interpolation happens on what the user painted.
//
// SIZE: a 1024-column image with N = 1024 is 1024 × 513 values per channel (4 MB of doubles). For
// big FFTs and very wide images the gain tables use fewer columns (at most MaxCells per channel,
// sampled with linear interpolation along time): one column per few milliseconds is still far finer
// than the analysis hop.
//
// Threading: built on the UI thread (or a worker for renders); the arrays are then only read, or
// updated in place for a column range on the UI thread and copied before going to Csound.

namespace CsoundVisual.Paint;

/// <summary>Precomputed mapping from FFT bins to fractional rows of a <see cref="PitchAxis"/> (see the file header).</summary>
public sealed class FilterBinMap
{
    /// <summary>Lower row of the interpolation pair, per bin (interpolation mode).</summary>
    readonly int[] row0;
    /// <summary>Weight of the upper row (row0 + 1), per bin, 0…1.</summary>
    readonly float[] frac;
    /// <summary>First row whose centre lies inside the bin's band, or −1 when none (then interpolate).</summary>
    readonly int[] spanLo;
    /// <summary>Last row whose centre lies inside the bin's band (inclusive).</summary>
    readonly int[] spanHi;

    /// <summary>Number of bins (FftSize/2 + 1).</summary>
    public int Bins { get; }

    /// <summary>Rows of the axis the map was built for.</summary>
    public int Rows { get; }

    /// <summary>FFT size the map was built for.</summary>
    public int FftSize { get; }

    /// <summary>Sample rate the map was built for.</summary>
    public double SampleRate { get; }

    /// <summary>The axis the map was built for.</summary>
    public PitchAxis Axis { get; }

    /// <summary>Builds the map for <paramref name="axis"/>, an FFT of <paramref name="fftSize"/> samples at <paramref name="sampleRate"/> Hz.</summary>
    /// <param name="axis">Pitch axis of the picture (row ↔ Hz).</param>
    /// <param name="fftSize">FFT size N in samples (bins = N/2 + 1).</param>
    /// <param name="sampleRate">Sample rate in Hz (sets the bin spacing sr/N).</param>
    /// <remarks>Cost O(bins) calls of <see cref="PitchAxis.RowOf"/>; cached by the player and rebuilt only when <see cref="Matches"/> fails.</remarks>
    public FilterBinMap(PitchAxis axis, int fftSize, double sampleRate)
    {
        Axis = axis;
        FftSize = fftSize;
        SampleRate = sampleRate;
        Bins = fftSize / 2 + 1;
        Rows = axis.RowCount;
        row0 = new int[Bins];
        frac = new float[Bins];
        spanLo = new int[Bins];
        spanHi = new int[Bins];
        double df = sampleRate / fftSize;   // bin spacing in Hz
        int h = Rows;
        for (int k = 0; k < Bins; k++)
        {
            double f = k * df;
            // Centre row (fractional). DC (f = 0) has no position on a log axis: use the bottom row.
            double r = f <= 0 ? 0 : axis.RowOf(f);
            r = double.IsFinite(r) ? Math.Clamp(r, 0, h - 1) : 0;
            // Lower row of the pair, capped at h − 2 so row0 + 1 is still a valid row (and 0 for a
            // one-row axis, where Interp returns row 0 directly).
            int i0 = Math.Min((int)Math.Floor(r), h - 2 < 0 ? 0 : h - 2);
            row0[k] = Math.Max(0, i0);
            frac[k] = h < 2 ? 0 : (float)Math.Clamp(r - row0[k], 0, 1);

            // Band edges in rows. Rows whose centre (integer row index) lies inside [lo, hi] are
            // «covered» by this bin. Outside the axis the band is clamped, so the bottom bins all
            // cover row 0 and the top bins row h−1 (edge extension).
            double lo = f - df / 2 <= 0 ? double.NegativeInfinity : axis.RowOf(f - df / 2);
            double hi = axis.RowOf(f + df / 2);
            int a = (int)Math.Ceiling(Math.Max(lo, 0));
            int b = (int)Math.Floor(Math.Min(hi, h - 1));
            if (!double.IsFinite(hi)) b = -1;
            if (a <= b && b >= 0 && a <= h - 1)
            {
                spanLo[k] = a;
                spanHi[k] = b;
            }
            else
                spanLo[k] = spanHi[k] = -1;
        }
    }

    /// <summary>
    /// The level of bin <paramref name="k"/> for one image column (<paramref name="column"/> holds
    /// the column's levels, row 0 first).
    /// </summary>
    /// <param name="column">Brightness 0…1 of every row of one image column (length = <see cref="Rows"/>).</param>
    /// <param name="k">Bin index 0…<see cref="Bins"/> − 1.</param>
    /// <returns>The brightness that bin should get (still a level; the gain rule is applied by the caller).</returns>
    public float BinLevel(ReadOnlySpan<float> column, int k)
    {
        int lo = spanLo[k];
        if (lo >= 0)
        {
            // Several row centres inside the bin: keep the loudest (a thin line stays audible).
            float m = 0;
            for (int y = lo; y <= spanHi[k]; y++)
                if (column[y] > m) m = column[y];
            // Include the interpolated value at the bin centre too: when the bin covers exactly
            // one row centre off to one side, its neighbour may be the brighter one at the centre.
            float c = Interp(column, k);
            return c > m ? c : m;
        }
        return Interp(column, k);
    }

    /// <summary>Linear interpolation between the two rows around bin <paramref name="k"/>'s centre.</summary>
    /// <param name="column">Levels of one image column.</param>
    /// <param name="k">Bin index.</param>
    /// <returns>column[row0] blended towards column[row0 + 1] by frac (row 0 alone on a one-row axis).</returns>
    float Interp(ReadOnlySpan<float> column, int k)
    {
        int i0 = row0[k];
        if (Rows < 2) return column[0];
        float t = frac[k];
        return column[i0] + (column[i0 + 1] - column[i0]) * t;
    }

    /// <summary>True when this map was built for exactly these parameters (reuse it instead of rebuilding).</summary>
    /// <param name="axis">Axis to compare (value equality of <see cref="PitchAxis"/>).</param>
    /// <param name="fftSize">FFT size to compare.</param>
    /// <param name="sampleRate">Sample rate to compare (within 0.5 Hz, so 44100 vs 44100.0001 still matches).</param>
    /// <returns>True when the cached map can be reused.</returns>
    public bool Matches(PitchAxis axis, int fftSize, double sampleRate) =>
        fftSize == FftSize && Math.Abs(sampleRate - SampleRate) < 0.5 && axis.Equals(Axis);
}

/// <summary>
/// The per-bin gain tables of one filter image: <see cref="GainL"/> / <see cref="GainR"/>, each
/// <see cref="Columns"/> × <see cref="Bins"/> values, column-major (index = column·Bins + bin),
/// exactly as Csound's instrument slices them (see the file header).
/// </summary>
public sealed class ImageFilterTables
{
    /// <summary>Most values per channel table (≈ 17.6 MB as doubles); wider images are sampled along time.</summary>
    public const int MaxCells = 2_200_000;

    /// <summary>The bin mapping used.</summary>
    public FilterBinMap Map { get; }

    /// <summary>The settings the gains were computed with (level → gain rule).</summary>
    public ImageFilterSettings Settings { get; private set; }

    /// <summary>Columns of the gain tables (≤ the image width).</summary>
    public int Columns { get; }

    /// <summary>Bins per column (FftSize/2 + 1).</summary>
    public int Bins => Map.Bins;

    /// <summary>Width of the image the tables were built from (for mapping edited image columns).</summary>
    public int ImageWidth { get; }

    /// <summary>Left-channel gains, column-major.</summary>
    public double[] GainL { get; }

    /// <summary>Right-channel gains, column-major.</summary>
    public double[] GainR { get; }

    /// <summary>Creates the tables and computes every column.</summary>
    /// <param name="image">Source picture.</param>
    /// <param name="settings">Gain rule (and FFT size, through <paramref name="map"/>).</param>
    /// <param name="map">Bin map matching the image's axis and the FFT size / sample rate.</param>
    ImageFilterTables(SpectralImage image, ImageFilterSettings settings, FilterBinMap map)
    {
        Map = map;
        Settings = settings;
        ImageWidth = image.Width;
        // As many gain columns as image columns, unless that would exceed MaxCells values per table.
        Columns = Math.Max(1, Math.Min(image.Width, MaxCells / map.Bins));
        GainL = new double[Columns * map.Bins];
        GainR = new double[Columns * map.Bins];
        UpdateColumns(image, 0, image.Width);
    }

    /// <summary>
    /// Builds the gain tables of <paramref name="image"/> for an FFT of <paramref name="settings"/>.FftSize
    /// at <paramref name="sampleRate"/>; reuses <paramref name="map"/> when it matches.
    /// </summary>
    /// <param name="image">The filter picture.</param>
    /// <param name="settings">FFT size and level → gain rule.</param>
    /// <param name="sampleRate">Sample rate the tables are for (the engine's, or the source's for renders).</param>
    /// <param name="map">A previously built map to reuse, or <c>null</c>.</param>
    /// <returns>New tables; their <see cref="Map"/> can be passed back next time.</returns>
    /// <remarks>Cost proportional to Columns × Bins (up to <see cref="MaxCells"/> per channel): call it off the hot paths.</remarks>
    public static ImageFilterTables Build(SpectralImage image, ImageFilterSettings settings, double sampleRate, FilterBinMap? map = null)
    {
        if (map is null || !map.Matches(image.Axis, settings.FftSize, sampleRate))
            map = new FilterBinMap(image.Axis, settings.FftSize, sampleRate);
        return new ImageFilterTables(image, settings, map);
    }

    /// <summary>
    /// Recomputes the gain columns affected by image columns [<paramref name="x0"/>, <paramref name="x1"/>)
    /// and returns the gain-column range that changed (inclusive start, exclusive end).
    /// </summary>
    /// <param name="image">The same picture the tables were built from (same width, height and axis).</param>
    /// <param name="x0">First changed image column (inclusive).</param>
    /// <param name="x1">End of the changed image columns (exclusive).</param>
    /// <returns>The gain columns [C0, C1) that were rewritten.</returns>
    /// <exception cref="ArgumentException">When the image size no longer matches (the player then restarts instead).</exception>
    public (int C0, int C1) UpdateColumns(SpectralImage image, int x0, int x1)
    {
        if (image.Width != ImageWidth || image.Height != Map.Rows)
            throw new ArgumentException("The image no longer matches the filter tables (size or axis changed).");
        // Gain column c samples the image at fractional column s(c) = (c + 0.5)·W/C − 0.5; it
        // depends on image columns floor(s) and floor(s)+1. Find every c whose pair touches [x0, x1).
        double scale = (double)ImageWidth / Columns;
        int c0 = Math.Max(0, (int)Math.Floor((x0 - 1 + 0.5) / scale - 0.5));
        int c1 = Math.Min(Columns, (int)Math.Ceiling((x1 + 0.5) / scale - 0.5) + 1);
        int h = image.Height, b = Bins;
        var colL = new float[h];
        var colR = new float[h];
        for (int c = c0; c < c1; c++)
        {
            // 1) The image column(s) for this gain column (identity when Columns == ImageWidth).
            if (Columns == ImageWidth)
            {
                image.Left.AsSpan(c * h, h).CopyTo(colL);
                image.Right.AsSpan(c * h, h).CopyTo(colR);
            }
            else
            {
                double s = (c + 0.5) * scale - 0.5;
                int i0 = Math.Clamp((int)Math.Floor(s), 0, ImageWidth - 1), i1 = Math.Min(i0 + 1, ImageWidth - 1);
                float t = (float)Math.Clamp(s - i0, 0, 1);
                for (int y = 0; y < h; y++)
                {
                    colL[y] = image.Left[i0 * h + y] + (image.Left[i1 * h + y] - image.Left[i0 * h + y]) * t;
                    colR[y] = image.Right[i0 * h + y] + (image.Right[i1 * h + y] - image.Right[i0 * h + y]) * t;
                }
            }
            // 2) Rows → bins, then level → gain.
            int o = c * b;
            for (int k = 0; k < b; k++)
            {
                GainL[o + k] = Settings.LevelToGain(Map.BinLevel(colL, k));
                GainR[o + k] = Settings.LevelToGain(Map.BinLevel(colR, k));
            }
        }
        return (c0, c1);
    }

    /// <summary>
    /// Applies new settings. When only the level → gain rule changed (response, range, boost)
    /// every column is recomputed in place and the method returns true (re-upload the tables);
    /// it returns false when nothing in the tables depends on what changed.
    /// </summary>
    /// <param name="image">The picture (to recompute from).</param>
    /// <param name="settings">The new settings (stored either way).</param>
    /// <returns>True when the gains were recomputed and must be re-uploaded.</returns>
    public bool UpdateGainRule(SpectralImage image, ImageFilterSettings settings)
    {
        bool changed = settings.Response != Settings.Response || settings.RangeDb != Settings.RangeDb || settings.BoostDb != Settings.BoostDb;
        Settings = settings;
        if (changed) UpdateColumns(image, 0, image.Width);
        return changed;
    }

    /// <summary>The gain-column position (fractional) that corresponds to image column <paramref name="imageColumn"/>.</summary>
    /// <param name="imageColumn">Fractional image column.</param>
    /// <returns>Fractional gain column (pixel-centre aligned: the ±0.5 terms map column centres onto each other).</returns>
    public double ToGainColumn(double imageColumn) => (imageColumn + 0.5) * Columns / ImageWidth - 0.5;

    /// <summary>The image column (fractional) that corresponds to gain column <paramref name="gainColumn"/>.</summary>
    /// <param name="gainColumn">Fractional gain column.</param>
    /// <returns>Fractional image column (inverse of <see cref="ToGainColumn"/>).</returns>
    public double ToImageColumn(double gainColumn) => (gainColumn + 0.5) * ImageWidth / Columns - 0.5;
}
