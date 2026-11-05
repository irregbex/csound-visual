// SpectralAnalysis.cs — Paint Rooms: SOUND → IMAGE (used by the «Spectrum Room»).
//
// Role: turns a recording (AudioData) into a SpectralImage on ANY PitchAxis — logarithmic, linear,
// 12-tone, microtonal, Scala — so the user can look at a sound, edit it with the same brushes and
// image operations as Paint Synth, and resynthesise it (SpectralMask for the faithful path, the
// Paint Synth Csound engine for the creative «additive» path).
//
// ------------------------------------------------------------------------------------------------
// THE DSP, STEP BY STEP
// ------------------------------------------------------------------------------------------------
// 1. Short-time Fourier transform (same conventions as Paint/Stft.cs): frame m is CENTRED on sample
//    m·hop (it covers m·hop − N/2 … m·hop + N/2 − 1, zero outside the signal), multiplied by a
//    periodic window w (Hann by default) and FFT'd; bins k = 0 … N/2 have centre frequencies
//    k·sr/N. N = FrameSize (power of two), hop = N / Overlap.
//    We do NOT call Stft.Forward, which keeps every complex frame in memory (16 bytes × N/2 per frame:
//    ~90 MB per channel for one minute at hop 512, almost 2 GB for ten minutes). Instead frames are
//    computed in a streaming loop, used at once and forgotten; only the finished image is kept.
//
// 2. Calibration: the magnitude of bin k is scaled by 2 / (N · cg), cg = mean(w) (the window's
//    coherent gain), so a sine of amplitude A reads exactly A at its PEAK bin (Stft.Magnitudes uses
//    the same scale). A sine's energy is spread over the window's main lobe (≈ 4 bins for Hann); the
//    scaled POWERS of all those bins add up to A² · ENBW, where ENBW = N·Σw² / (Σw)² is the window's
//    equivalent noise bandwidth in bins (1.5 for Hann). Dividing the summed power by ENBW therefore
//    makes a sine read A² again once its lobe has been collected into one row — and makes broadband
//    noise read the true power that falls inside each row's band.
//
// 3. True frequency (phase vocoder): every bin's frequency is re-estimated from how much its phase
//    advanced since the previous frame (Stft.TrueFrequency). All bins of a sine's main lobe then
//    agree on the sine's exact frequency, even when it lies between bin centres. This is what lets
//    the energy land in the RIGHT ROW on a coarse or microtonal axis: on a 12-tone axis a 440 Hz sine
//    lands on A4 only, not smeared over G#4…A#4 by the window's lobe. The first frame has no
//    predecessor and uses the bin centres.
//
// 4. Bin → row distribution: the true frequency is converted to a fractional row r with
//    PitchAxis.RowOf (log-interpolated between row frequencies) and the bin's POWER is split
//    linearly between the two nearest rows ⌊r⌋ and ⌊r⌋+1 (weights 1−t and t, t = r − ⌊r⌋), so a
//    frequency exactly on a row goes entirely to that row and one between two notes is shared.
//    Energy BEYOND the axis (DC and rumble below the lowest row, air above the highest) is added to
//    the edge row: the first and last rows mean «this frequency and everything beyond it». That keeps
//    the picture's total energy equal to the sound's, and lets the faithful resynthesis treat those
//    frequencies like the edge rows (erasing all rows of a moment really silences it). The DC bin is
//    included at a quarter of its scaled power (the 2/(N·cg) scale is meant for bins that have a
//    mirrored negative-frequency twin; DC does not). RowOf is a binary search plus two
//    logarithms; it is replaced here by a precomputed lookup table (RowLookup, 16 points per bin)
//    because it runs for every bin of every frame.
//
// 5. Frames → columns: the image has W columns spanning the whole duration T; column c is centred on
//    tc = (c + 0.5)·T/W. Each column averages the row powers of the frames around it with a
//    triangular weight max(0, 1 − |tm − tc| / R), R = max(T/W, hop/sr): when columns are finer than
//    frames this is plain linear interpolation between the two nearest frames; when they are coarser
//    it is a smooth average of all the frames inside the column. (SpectralMask uses the inverse
//    mapping: the gain of frame m is interpolated between the columns around tm.)
//
// 6. Power → level (what the canvas shows and the brushes edit): dB = 10·log10(power) (0 dB = a
//    full-scale sine), level = clamp((dB − floor) / (0 − floor), 0, 1). The scale is PERCEPTUAL: equal
//    steps of brightness are equal steps of dB; with the default floor of −90 dB, level 1 = 0 dBFS,
//    0.5 = −45 dB and 0 = −90 dB or quieter. Because levels are linear in dB, «gain ±x dB» is just
//    «level ± x / |floor|», and the faithful resynthesis turns a level DIFFERENCE back into a gain.
//
// Threading: Analyze is pure computation on its own copies (call it from a background task; it
// reports progress and honours cancellation). The image it returns is new and not yet shared, so
// the caller may hand it to the UI thread.
//
// Performance (measured on the development PC with the console harness): 60 s of stereo 48 kHz audio,
// N = 2048, overlap 4, 512 rows → 0.1–0.2 s on 24 hardware threads, the mask resynthesis 0.2 s;
// N = 8192, overlap 8 → ≈ 0.25 s. Work is split into blocks of columns that run in
// parallel; each block recomputes the one or two frames it shares with its neighbours, so no locks
// are needed and the output arrays are written by exactly one thread each.

using System.Numerics;

namespace CsoundVisual.Paint;

/// <summary>How a sound is analysed into an image (persisted with the Spectrum Room preferences).</summary>
public sealed record SpectrumAnalysisSettings
{
    /// <summary>Smallest accepted FFT size (samples).</summary>
    public const int MinFrameSize = 256;

    /// <summary>Largest accepted FFT size (samples).</summary>
    public const int MaxFrameSize = 32768;

    /// <summary>
    /// FFT size N (power of two). Bigger = finer frequency resolution (sr/N Hz per bin: 23 Hz at
    /// 2048 / 48 kHz) but blurrier in time (the frame lasts N/sr: 43 ms at 2048 / 48 kHz).
    /// </summary>
    public int FrameSize { get; init; } = 2048;

    /// <summary>Frames per frame length: hop = FrameSize / Overlap (2, 4 or 8; 4 is the usual choice).</summary>
    public int Overlap { get; init; } = 4;

    /// <summary>
    /// Duration of one image column in milliseconds (the time resolution of the PICTURE), or 0 for
    /// «automatic» = one column per analysis hop. Capped so the image has at most
    /// <see cref="SpectralImage.MaxWidth"/> columns.
    /// </summary>
    public double ColumnMs { get; init; }

    /// <summary>Level 0 of the picture in dB (default −90): anything quieter is black. Range −160 … −20.</summary>
    public double FloorDb { get; init; } = -90;

    /// <summary>Analysis window (Hann is the right choice for the faithful resynthesis).</summary>
    public WindowKind Window { get; init; } = WindowKind.Hann;

    /// <summary>Hop size in samples.</summary>
    public int HopSize => Math.Max(1, FrameSize / Math.Max(1, Overlap));

    /// <summary>A copy with every value clamped to its valid range (FrameSize rounded to a power of two).</summary>
    public SpectrumAnalysisSettings Normalized()
    {
        int n = MinFrameSize;
        while (n < FrameSize && n < MaxFrameSize) n <<= 1;
        int ov = Overlap switch { <= 2 => 2, <= 4 => 4, _ => 8 };
        return this with
        {
            FrameSize = n,
            Overlap = ov,
            ColumnMs = double.IsFinite(ColumnMs) ? Math.Clamp(ColumnMs, 0, 1000) : 0,
            FloorDb = double.IsFinite(FloorDb) ? Math.Clamp(FloorDb, -160, -20) : -90,
        };
    }
}

/// <summary>
/// The result of <see cref="SpectralAnalysis.Analyze"/>: the analysed sound, the settings, and the
/// ORIGINAL image (never edited; the faithful resynthesis compares the edited image with it).
/// </summary>
/// <param name="Audio">The analysed sound (all channels; the image uses the first two).</param>
/// <param name="Name">Display name (file name without folder), or a description.</param>
/// <param name="Settings">The (normalised) analysis settings used.</param>
/// <param name="Original">The unedited analysis image (Left = channel 1, Right = channel 2 or a copy of channel 1).</param>
public sealed record SpectrumAnalysis(AudioData Audio, string Name, SpectrumAnalysisSettings Settings, SpectralImage Original)
{
    /// <summary>Sample rate of the sound (Hz).</summary>
    public int SampleRate => Audio.SampleRate;

    /// <summary>True when the sound has two (or more) channels.</summary>
    public bool Stereo => Audio.Channels.Length >= 2;

    /// <summary>Range of the level scale in dB (positive: 90 for a −90 dB floor).</summary>
    public double RangeDb => -Settings.FloorDb;
}

/// <summary>
/// Fast «frequency → fractional row» conversion for one axis and one FFT grid: a table sampled
/// every 1/16 of a bin, linearly interpolated. Equivalent to <see cref="PitchAxis.RowOf"/> within
/// ~0.02 rows everywhere, ~20× faster (no binary search, no logarithm). Immutable, thread-safe.
/// </summary>
public sealed class RowLookup
{
    /// <summary>Table points per FFT bin.</summary>
    const int PointsPerBin = 16;

    readonly double[] table;
    readonly double hzPerPoint;

    /// <summary>Builds the table from 0 Hz to Nyquist (plus one bin of margin).</summary>
    /// <param name="axis">Pitch axis of the image.</param>
    /// <param name="sampleRate">Sample rate of the sound (Hz).</param>
    /// <param name="frameSize">FFT size N.</param>
    public RowLookup(PitchAxis axis, double sampleRate, int frameSize)
    {
        hzPerPoint = sampleRate / frameSize / PointsPerBin;
        int n = (frameSize / 2 + 2) * PointsPerBin + 1;
        table = new double[n];
        for (int i = 0; i < n; i++)
        {
            double hz = i * hzPerPoint;
            // 0 Hz has no pitch (RowOf returns −∞): use a frequency just above 0 instead.
            table[i] = axis.RowOf(Math.Max(hz, hzPerPoint * 0.25));
        }
    }

    /// <summary>Fractional row of <paramref name="hz"/> (may be &lt; 0 or ≥ the row count outside the axis; −∞ for hz ≤ 0).</summary>
    public double RowOf(double hz)
    {
        if (!(hz > 0)) return double.NegativeInfinity;
        double p = hz / hzPerPoint;
        int i = (int)p;
        if (i >= table.Length - 1) return table[^1] + (p - (table.Length - 1)) * (table[^1] - table[^2]);
        double t = p - i;
        return table[i] + (table[i + 1] - table[i]) * t;
    }
}

/// <summary>Sound → image analysis (see the file header for the maths).</summary>
public static class SpectralAnalysis
{
    /// <summary>Power floor used instead of 0 before taking logarithms (−300 dB).</summary>
    const double TinyPower = 1e-30;

    /// <summary>Longest sound accepted (seconds) — the image duration limit of <see cref="SpectralImage"/>.</summary>
    public const double MaxSeconds = 3600;

    /// <summary>
    /// Analyses <paramref name="audio"/> into an image on <paramref name="axis"/>.
    /// Channels 1 and 2 become the Left and Right layers; a mono sound gets identical layers;
    /// channels beyond the second are ignored.
    /// </summary>
    /// <param name="audio">The sound.</param>
    /// <param name="name">Display name stored in the result.</param>
    /// <param name="axis">Pitch axis of the image (its RowCount is the height).</param>
    /// <param name="settings">FFT size, overlap, column duration, dB floor, window.</param>
    /// <param name="progress">Receives 0…1 (from worker threads; wrap a UI update in <see cref="Progress{T}"/>).</param>
    /// <param name="cancel">Stops the analysis (throws <see cref="OperationCanceledException"/>).</param>
    /// <exception cref="ArgumentException">Empty or too long sound.</exception>
    public static SpectrumAnalysis Analyze(AudioData audio, string name, PitchAxis axis, SpectrumAnalysisSettings settings,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var s = settings.Normalized();
        if (audio.Frames < 2 || audio.Channels.Length == 0) throw new ArgumentException("The sound is empty.");
        if (audio.Seconds > MaxSeconds) throw new ArgumentException($"The sound is longer than {MaxSeconds / 60:0} minutes.");

        int width = ColumnCount(audio, s);
        var image = new SpectralImage(width, axis, audio.Seconds);
        int layers = Math.Min(2, audio.Channels.Length);
        var lookup = new RowLookup(axis, audio.SampleRate, s.FrameSize);

        // Progress is counted in columns over all layers; reported at most every ~1 %.
        long totalWork = (long)width * layers, done = 0, lastReported = 0;
        void Advance(int columns)
        {
            long now = Interlocked.Add(ref done, columns);
            if (progress is null || now - Interlocked.Read(ref lastReported) < totalWork / 100) return;
            Interlocked.Exchange(ref lastReported, now);
            progress.Report((double)now / totalWork);
        }

        for (int layer = 0; layer < layers; layer++)
            AnalyzeChannel(audio.Channels[layer], audio.SampleRate, s, image, lookup, layer == 0 ? image.Left : image.Right, Advance, cancel);
        if (layers == 1) Array.Copy(image.Left, image.Right, image.Left.Length);
        progress?.Report(1);
        return new SpectrumAnalysis(audio, name, s, image);
    }

    /// <summary>
    /// Number of image columns for a sound: duration / column duration, or one column per hop in
    /// «automatic» mode; clamped to [<see cref="SpectralImage.MinWidth"/>, <see cref="SpectralImage.MaxWidth"/>].
    /// </summary>
    public static int ColumnCount(AudioData audio, SpectrumAnalysisSettings settings)
    {
        var s = settings.Normalized();
        double cols = s.ColumnMs > 0 ? audio.Seconds * 1000 / s.ColumnMs : (double)audio.Frames / s.HopSize;
        return (int)Math.Clamp(Math.Round(cols), SpectralImage.MinWidth, SpectralImage.MaxWidth);
    }

    /// <summary>
    /// Analyses one channel into one layer (<paramref name="levels"/>, column-major like the image).
    /// Parallel over blocks of columns; each block streams the frames it needs (see the file header,
    /// steps 1–6).
    /// </summary>
    static void AnalyzeChannel(float[] signal, int sampleRate, SpectrumAnalysisSettings s, SpectralImage image, RowLookup lookup,
        float[] levels, Action<int> advance, CancellationToken cancel)
    {
        int n = s.FrameSize, hop = s.HopSize, bins = n / 2 + 1, h = image.Height, w = image.Width;
        int frameCount = signal.Length / hop + 1;            // frame m centred on sample m·hop (as Stft.Forward)
        double seconds = (double)signal.Length / sampleRate;  // = image.DurationSeconds (before its 3600 s clamp)
        double colSec = seconds / w, frameSec = (double)hop / sampleRate;
        double radius = Math.Max(colSec, frameSec);           // half-width of the triangular frame → column kernel

        var window = WindowFunction.Create(s.Window, n);
        double sumW = 0, sumW2 = 0;
        foreach (double v in window) { sumW += v; sumW2 += v * v; }
        double scale = 2.0 / sumW;                            // = 2 / (N · coherent gain): a sine reads its amplitude
        double enbw = n * sumW2 / (sumW * sumW);              // equivalent noise bandwidth in bins (1.5 for Hann)
        double powerScale = scale * scale / enbw;             // |X|² → calibrated power, lobe-summed
        double rangeDb = -s.FloorDb;
        var stft = new StftSettings(n, hop, s.Window);    // for Stft.TrueFrequency

        // Blocks of columns: enough blocks to keep every core busy, big enough that the frames
        // recomputed at block borders (≈ 2·radius worth) are a small fraction of the work.
        int blocks = Math.Clamp(Environment.ProcessorCount * 4, 1, Math.Max(1, w / 8));
        var options = new ParallelOptions { CancellationToken = cancel };
        Parallel.For(0, blocks, options, b =>
        {
            int c0 = (int)((long)w * b / blocks), c1 = (int)((long)w * (b + 1) / blocks);
            if (c1 <= c0) return;
            // Column centres of this block and the frames whose kernel reaches them.
            double t0 = (c0 + 0.5) * colSec - radius, t1 = (c1 - 0.5) * colSec + radius;
            int m0 = Math.Max(0, (int)Math.Ceiling(t0 / frameSec - 1e-9));
            int m1 = Math.Min(frameCount - 1, (int)Math.Floor(t1 / frameSec + 1e-9));

            // Per-block accumulators (only this thread writes them).
            var acc = new double[(c1 - c0) * h];
            var weight = new double[c1 - c0];
            var buf = new Complex[n];
            var rowPower = new double[h];
            var prevPhase = new double[bins];
            var phase = new double[bins];
            bool havePrev = false;

            // The frame just before the block provides the phases for the true frequencies of the first one.
            if (m0 > 0)
            {
                Transform(signal, (m0 - 1) * hop - n / 2, window, buf);
                for (int k = 0; k < bins; k++) prevPhase[k] = buf[k].Phase;
                havePrev = true;
            }

            for (int m = m0; m <= m1; m++)
            {
                cancel.ThrowIfCancellationRequested();
                Transform(signal, m * hop - n / 2, window, buf);
                Array.Clear(rowPower);
                // DC: no pitch, a quarter of the scaled power (see step 4), always into the bottom row.
                rowPower[0] += buf[0].Real * buf[0].Real * powerScale * 0.25;
                for (int k = 1; k < bins - 1; k++)   // the Nyquist bin is left out (no usable pitch)
                {
                    var x = buf[k];
                    double p = (x.Real * x.Real + x.Imaginary * x.Imaginary) * powerScale;
                    double ph = x.Phase;
                    phase[k] = ph;
                    if (p < TinyPower) continue;
                    // Step 3: true frequency from the phase advance (bin centre for the first frame).
                    double hz = havePrev ? Stft.TrueFrequency(prevPhase[k], ph, k, stft, sampleRate) : (double)k * sampleRate / n;
                    // Step 4: linear split between the two nearest rows; beyond the axis → the edge row.
                    double r = lookup.RowOf(hz);
                    if (!(r > 0)) { rowPower[0] += p; continue; }          // also −∞ (0 Hz) and NaN
                    if (r >= h - 1) { rowPower[h - 1] += p; continue; }
                    int i0 = (int)Math.Floor(r);
                    double t = r - i0;
                    if (i0 >= 0) rowPower[i0] += p * (1 - t);
                    if (i0 + 1 < h) rowPower[i0 + 1] += p * t;
                }
                (prevPhase, phase) = (phase, prevPhase);
                havePrev = true;

                // Step 5: add this frame to every column of the block its triangular kernel reaches.
                double tm = m * frameSec;
                int ca = Math.Max(c0, (int)Math.Ceiling((tm - radius) / colSec - 0.5));
                int cb = Math.Min(c1 - 1, (int)Math.Floor((tm + radius) / colSec - 0.5));
                for (int c = ca; c <= cb; c++)
                {
                    double wt = 1 - Math.Abs(tm - (c + 0.5) * colSec) / radius;
                    if (wt <= 0) continue;
                    weight[c - c0] += wt;
                    int o = (c - c0) * h;
                    for (int y = 0; y < h; y++) acc[o + y] += wt * rowPower[y];
                }
            }

            // Step 6: power → perceptual level.
            for (int c = c0; c < c1; c++)
            {
                double wsum = weight[c - c0];
                int o = (c - c0) * h, dst = c * h;
                for (int y = 0; y < h; y++)
                {
                    double p = wsum > 0 ? acc[o + y] / wsum : 0;
                    levels[dst + y] = (float)LevelFromDb(10 * Math.Log10(p + TinyPower), rangeDb);
                }
            }
            advance(c1 - c0);
        });
    }

    /// <summary>
    /// Windows the N samples starting at <paramref name="start"/> (zero outside the signal) into
    /// <paramref name="buf"/> and FFTs them in place (frame convention of <see cref="Stft.Forward"/>).
    /// </summary>
    internal static void Transform(float[] signal, int start, double[] window, Complex[] buf)
    {
        int n = buf.Length;
        for (int i = 0; i < n; i++)
        {
            int j = start + i;
            buf[i] = new Complex((uint)j < (uint)signal.Length ? signal[j] * window[i] : 0, 0);
        }
        Fft.Transform(buf);
    }

    // ------------------------------------------------------------------------------------------
    //  Level ↔ dB ↔ amplitude helpers (the perceptual scale of step 6)
    // ------------------------------------------------------------------------------------------

    /// <summary>Level 0…1 of a dB value for a scale spanning <paramref name="rangeDb"/> dB below 0 dB.</summary>
    public static double LevelFromDb(double db, double rangeDb) => Math.Clamp((db + rangeDb) / rangeDb, 0, 1);

    /// <summary>dB value of a level (level 0 maps to −<paramref name="rangeDb"/>, the floor).</summary>
    public static double DbFromLevel(double level, double rangeDb) => (Math.Clamp(level, 0, 1) - 1) * rangeDb;

    /// <summary>
    /// Copy of an analysis image converted to Paint Synth levels, so the Paint Synth engine plays
    /// each row at the amplitude that was measured: amplitude a = 10^(dB/20) of the dB level, then
    /// level_synth = a^(1/curve) because Paint Synth maps level → amplitude as level^curve
    /// (<see cref="PaintSynthSettings.LevelToAmplitude"/>). Cells at level 0 stay silent. The result
    /// looks darker than the analysis (quiet partials become very dark), which is the honest picture
    /// of their loudness in Paint Synth.
    /// </summary>
    /// <param name="analysis">Image with perceptual (dB) levels.</param>
    /// <param name="rangeDb">dB range of those levels (−floor).</param>
    /// <param name="curve">Paint Synth's brightness → loudness exponent (<see cref="PaintSynthSettings.Curve"/>).</param>
    public static SpectralImage ToSynthLevels(SpectralImage analysis, double rangeDb, double curve)
    {
        var dst = new SpectralImage(analysis.Width, analysis.Axis, analysis.DurationSeconds);
        ToSynthLevels(analysis, dst, 0, analysis.Width, rangeDb, curve);
        return dst;
    }

    /// <summary>Converts columns [x0, x1) of <paramref name="analysis"/> into <paramref name="dst"/> (same size) — see <see cref="ToSynthLevels(SpectralImage, double, double)"/>.</summary>
    public static void ToSynthLevels(SpectralImage analysis, SpectralImage dst, int x0, int x1, double rangeDb, double curve)
    {
        int h = analysis.Height;
        double inv = 1 / Math.Max(0.1, curve);
        // 256-entry lookup would quantise; the conversion is cheap enough to compute directly.
        static float Convert(float level, double rangeDb, double inv) =>
            level <= 0 ? 0f : (float)Math.Pow(Math.Pow(10, DbFromLevel(level, rangeDb) / 20), inv);
        for (int i = x0 * h; i < x1 * h; i++)
        {
            dst.Left[i] = Convert(analysis.Left[i], rangeDb, inv);
            dst.Right[i] = Convert(analysis.Right[i], rangeDb, inv);
        }
    }

    /// <summary>
    /// «Freeze»: a new image of <paramref name="width"/> columns lasting <paramref name="seconds"/>
    /// in which every column is the spectrum found around column <paramref name="column"/> of
    /// <paramref name="source"/> (power-averaged over ±<paramref name="spread"/> columns to steady the
    /// partials), with short fades in and out (≈ 5 % of the length each) so the tone does not click.
    /// Played through the Paint Synth engine it becomes a sustained tone or pad.
    /// </summary>
    /// <param name="source">Analysis image (perceptual levels).</param>
    /// <param name="column">Column to freeze.</param>
    /// <param name="width">Columns of the result.</param>
    /// <param name="seconds">Duration of the result.</param>
    /// <param name="rangeDb">dB range of the levels (for averaging in the power domain).</param>
    /// <param name="spread">Neighbour columns on each side to average.</param>
    public static SpectralImage Freeze(SpectralImage source, int column, int width, double seconds, double rangeDb, int spread = 2)
    {
        int h = source.Height;
        column = Math.Clamp(column, 0, source.Width - 1);
        var avgL = new double[h];
        var avgR = new double[h];
        int a = Math.Max(0, column - spread), b = Math.Min(source.Width - 1, column + spread), count = b - a + 1;
        // Average in POWER (10^(dB/10)), not in level: two columns at −20 and −80 dB average to −23 dB, not −50.
        for (int x = a; x <= b; x++)
            for (int y = 0; y < h; y++)
            {
                float l = source.Left[x * h + y], r = source.Right[x * h + y];
                if (l > 0) avgL[y] += Math.Pow(10, DbFromLevel(l, rangeDb) / 10);
                if (r > 0) avgR[y] += Math.Pow(10, DbFromLevel(r, rangeDb) / 10);
            }
        var colL = new float[h];
        var colR = new float[h];
        for (int y = 0; y < h; y++)
        {
            colL[y] = avgL[y] > 0 ? (float)LevelFromDb(10 * Math.Log10(avgL[y] / count), rangeDb) : 0;
            colR[y] = avgR[y] > 0 ? (float)LevelFromDb(10 * Math.Log10(avgR[y] / count), rangeDb) : 0;
        }
        var img = new SpectralImage(width, source.Axis, seconds);
        int fade = Math.Max(1, img.Width / 20);
        for (int x = 0; x < img.Width; x++)
        {
            // The fade is applied in dB (level steps), so it sounds like an even swell: the first
            // and last columns are «rangeDb» quieter, i.e. silent.
            double g = Math.Min(1, Math.Min((x + 0.5) / fade, (img.Width - x - 0.5) / fade));
            float drop = (float)(1 - g);
            int o = x * h;
            for (int y = 0; y < h; y++)
            {
                img.Left[o + y] = colL[y] > 0 ? Math.Max(0, colL[y] - drop) : 0;
                img.Right[o + y] = colR[y] > 0 ? Math.Max(0, colR[y] - drop) : 0;
            }
        }
        return img;
    }
}
