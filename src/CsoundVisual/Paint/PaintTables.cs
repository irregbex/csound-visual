// PaintTables.cs — Paint Rooms foundation.
//
// Role: converts a SpectralImage + PaintSynthSettings into the NUMBERS the Csound instrument
// reads (its function tables), for a given orchestra sample rate (sr) and control period (ksmps):
//
//   image L/R   (W+1)·H linear amplitudes, column-major. Levels go through the floor + curve of
//               the settings; rows that would sound at or above 0.475·sr are silenced (no
//               aliasing). The extra last column is all zeros: the instrument reads it once it has
//               played past the end, so it needs no «past the end» branch.
//   freq        H values: the oscillator frequency of each row in Hz (sines, noise bands), or the
//               loop rate of each row in the sample-loop mode (see below).
//   noise coef  H values: one-pole smoothing coefficient per row, which sets the band width of
//               that row's noise (noise-band mode).
//   noise norm  H values: gain per row that gives every noise band the power of a sine of the
//               same amplitude, whatever its width.
//   noise       65536 Gaussian random numbers with unit variance (fixed seed: renders repeat).
//   sample      2^k samples: the chosen sound file, mono, resampled to a power-of-two length
//               (adsynt2 needs power-of-two wave tables), peak-normalised.
//   noisiness   (W+1)·H values 0…1, only when the picture has a noise layer with noisy cells
//               (SpectralImage.HasNoise) and the mode is Sines («Mixed»): the instrument then
//               crossfades every row between its sine and its noise band per cell (equal power:
//               sine × √(1−n), noise × √n). Not touched by the level curve. The other modes ignore
//               the layer: Noise bands is all noise already, and in Sample loop the oscillator
//               «frequencies» are loop rates, not Hz, so a noise band would land at the wrong pitch.
//
// Also estimates the loudest moment of the result to set the automatic level (EstimatePeak).
// Pure computation (no Csound calls): usable from any thread. The live player builds the tables on
// the UI thread (a few ms) and only hands finished arrays (or copies of spans) to the Csound
// thread; the offline renderer builds them on a thread-pool task.
//
// Units: frequencies in Hz, times in seconds unless the name says Ms, amplitudes linear (1 = a
// full-scale sine before the output gain). Layout: column-major, index = column · H + row, row 0 =
// lowest pitch — the same as SpectralImage, so a column is one contiguous span (fast ftslice and
// fast partial uploads).
//
// Users: PaintLivePlayer (live), PaintRenderer / PaintCsoundCode.WriteStandaloneCsd (offline and
// «Show Csound code»), PaintSelfTest.

namespace CsoundVisual.Paint;

/// <summary>Everything the Paint Synth instrument needs, as plain arrays (see the file header).</summary>
public sealed class PaintTables
{
    /// <summary>Length of the shared Gaussian noise table (a power of two).</summary>
    /// <remarks>The instrument reads H consecutive values from a random offset every control
    /// period, so 65536 values give plenty of distinct windows for 512 rows.</remarks>
    public const int NoiseLength = 65536;

    /// <summary>Length of the sine/cosine wave tables (power of two; adsynt2 does not interpolate, so bigger = cleaner).</summary>
    public const int WaveLength = 16384;

    /// <summary>Longest sample table (2^20 points ≈ 21.8 s at 48 kHz before resampling).</summary>
    /// <remarks>The material itself is cut at 20 s first (see <see cref="MakeSampleWave"/>); for
    /// files above 52.4 kHz, 20 s exceed 2^20 frames and the table is then a downsampled copy.</remarks>
    public const int MaxSampleLength = 1 << 20;

    /// <summary>Target peak (linear) of the automatic level: −6 dBFS, leaving room for the estimate's error.</summary>
    public const double AutoLevelTarget = 0.5;

    /// <summary>Lazily created backing field of <see cref="Noise"/>.</summary>
    /// <remarks>Not locked: two threads racing on first use would each compute the same
    /// deterministic array, and either result is fine.</remarks>
    static double[]? noise;

    /// <summary>Private: instances are made by <see cref="Build"/> only.</summary>
    PaintTables() { }

    /// <summary>Image width (columns) — the image tables hold one extra silent column.</summary>
    public int Width { get; private init; }

    /// <summary>Image height (rows) = oscillators per bank.</summary>
    public int Height { get; private init; }

    /// <summary>Seconds to play the whole width once.</summary>
    public double PassSeconds { get; private init; }

    /// <summary>Orchestra sample rate the tables were computed for (Hz).</summary>
    public double SampleRate { get; private init; }

    /// <summary>Orchestra control period (samples) the noise coefficients were computed for.</summary>
    public int Ksmps { get; private init; }

    /// <summary>Synthesis settings the tables were computed with.</summary>
    /// <remarks>Its gain, auto-level, attack and release fields are refreshed by <see cref="UpdateGain"/>;
    /// everything else stays as at <see cref="Build"/> time.</remarks>
    public PaintSynthSettings Settings { get; private set; } = new();

    /// <summary>Left amplitudes, (Width + 1)·Height, column-major.</summary>
    public double[] ImageL { get; private init; } = [];

    /// <summary>Right amplitudes, same layout as <see cref="ImageL"/>.</summary>
    public double[] ImageR { get; private init; } = [];

    /// <summary>Per-row oscillator frequencies (Hz), or loop rates in sample-loop mode.</summary>
    public double[] Frequencies { get; private init; } = [];

    /// <summary>Per-row one-pole coefficients of the noise modulators (noise-band mode).</summary>
    public double[] NoiseCoef { get; private init; } = [];

    /// <summary>Per-row gains of the noise modulators (noise-band mode).</summary>
    public double[] NoiseNorm { get; private init; } = [];

    /// <summary>The sample-loop wave table (power-of-two length), or <c>null</c> in the other modes.</summary>
    public double[]? SampleWave { get; private init; }

    /// <summary>
    /// True when the picture has noisy cells and the mode is Sines: the instrument runs its per-cell
    /// tone/noise crossfade (p14 = 1) and reads <see cref="ImageN"/>.
    /// </summary>
    /// <remarks>Fixed per note (like <see cref="Stereo"/>): the live player restarts the note when a
    /// tonal picture becomes noisy.</remarks>
    public bool Mixed { get; private init; }

    /// <summary>Per-cell noisiness 0…1, (Width + 1)·Height column-major (last column silent/tonal), or empty when not <see cref="Mixed"/>.</summary>
    public double[] ImageN { get; private init; } = [];

    /// <summary>True when the left and right layers differ (the instrument then runs two banks).</summary>
    /// <remarks>Set once by <see cref="Build"/> (and only ever raised by <see cref="NoteStereo"/>);
    /// it travels to Csound as p11, so a change requires a new note.</remarks>
    public bool Stereo { get; private set; }

    /// <summary>Per-row flag: true when the row is silenced because its frequency is too close to Nyquist.</summary>
    /// <remarks>Sines/noise: frequency ≥ 0.475·sr (a little below Nyquist, sr/2). Sample loop:
    /// transposition ratio &gt; 16 (4 octaves) instead.</remarks>
    public bool[] Muted { get; private init; } = [];

    /// <summary>Estimated peak (linear) of the output at gain 1 (see <see cref="EstimatePeak"/>).</summary>
    public double PeakEstimate { get; private set; }

    /// <summary>
    /// Linear gain the instrument multiplies everything by: automatic level (if on) × the user's
    /// gain in dB. Sent to the <c>PaintRooms.gain</c> channel.
    /// </summary>
    public double OutputGain { get; private set; } = 1;

    /// <summary>The shared Gaussian noise table (unit variance, fixed seed so renders are repeatable).</summary>
    /// <remarks>Shared by every table set (and written once to each standalone .csd as
    /// <c>_noise.wav</c>); callers must not modify it.</remarks>
    public static double[] Noise => noise ??= MakeNoise();

    /// <summary>
    /// Builds every table for <paramref name="image"/>.
    /// </summary>
    /// <param name="image">The canvas.</param>
    /// <param name="settings">Synthesis settings.</param>
    /// <param name="sampleRate">Orchestra sr (Hz).</param>
    /// <param name="ksmps">Orchestra ksmps (samples per control period).</param>
    /// <param name="sample">Decoded sample for the sample-loop mode (null = read <see cref="PaintSynthSettings.SamplePath"/>, or none).</param>
    /// <returns>The complete tables, with <see cref="OutputGain"/> already computed.</returns>
    /// <exception cref="InvalidOperationException">Sample-loop mode without a readable sample.</exception>
    /// <exception cref="InvalidDataException">The sample file is not a WAV <see cref="WavIO"/> can read.</exception>
    public static PaintTables Build(SpectralImage image, PaintSynthSettings settings, double sampleRate, int ksmps, AudioData? sample = null)
    {
        int w = image.Width, h = image.Height;
        // kr = control rate (control periods per second): the noise modulators and the instrument's
        // smoothing filters are updated at this rate, not at sr.
        double kr = sampleRate / Math.Max(1, ksmps);
        var freqs = image.Axis.Frequencies(); // Hz per row, row 0 = lowest
        // Oscillators at or above 0.475·sr would alias (fold back below Nyquist): silence them.
        var muted = new bool[h];
        for (int y = 0; y < h; y++)
            muted[y] = freqs[y] >= 0.475 * sampleRate;

        // ---- sample loop: wave table + per-row loop rates
        double[]? wave = null;
        double[] oscFreq = freqs;
        if (settings.Mode == SynthMode.SampleLoop)
        {
            if (sample is null)
            {
                if (settings.SamplePath is not { } p || !File.Exists(p))
                    throw new InvalidOperationException("Choose a sound file (WAV) for the sample-loop mode.");
                sample = WavIO.Read(p);
            }
            (wave, double loopSeconds) = MakeSampleWave(sample);
            // adsynt2 plays the whole table once per cycle of its frequency, so a frequency of
            // 1/loopSeconds plays the file at its original speed. A row at f Hz must transpose by
            // f / root, hence: rate = (f / root) / loopSeconds.
            double root = settings.SampleRootHz > 0 ? settings.SampleRootHz : PitchAxis.DefaultBaseHz;
            oscFreq = new double[h];
            for (int y = 0; y < h; y++)
            {
                double ratio = freqs[y] / root;
                oscFreq[y] = ratio / loopSeconds;
                // Transposing up by more than ~4 octaves only produces aliasing: silence those rows.
                // (This REPLACES the Nyquist test above for this mode: what aliases here is the
                // sample's content, not the loop rate, which is tiny.)
                muted[y] = ratio > 16;
            }
        }

        // ---- noise bands: width per row from the spacing to the neighbouring rows
        var coef = new double[h];
        var norm = new double[h];
        double width = Math.Clamp(settings.NoiseWidth, 0.05, 8);
        for (int y = 0; y < h; y++)
        {
            // Local row spacing in Hz (mean of the gaps to the neighbours; the edge rows reuse their
            // only gap), at least 0.5 Hz. Computed in every mode; only the noise mode reads it.
            double below = y > 0 ? freqs[y] - freqs[y - 1] : freqs[Math.Min(1, h - 1)] - freqs[0];
            double above = y < h - 1 ? freqs[y + 1] - freqs[y] : below;
            double spacing = Math.Max(0.5, 0.5 * (below + above));
            // A one-pole low-pass of white noise, updated at kr, used as the amplitude of a
            // quadrature pair, makes a band about twice its cutoff wide around the row frequency.
            // Cutoff fc = band/2, capped at kr/6 so the modulator stays well below Nyquist of kr.
            double fc = Math.Min(0.5 * width * spacing, kr / 6);
            // One-pole low-pass y += c·(x − y) with cutoff fc at sample rate kr: c = 1 − e^(−2π·fc/kr).
            double c = 1 - Math.Exp(-2 * Math.PI * fc / kr);
            coef[y] = c;
            // Variance of the low-passed unit-variance noise is c/(2−c); each quadrature
            // component must have variance 1/2 so the band has the power A²/2 of a sine of
            // amplitude A, hence the gain sqrt((2−c)/(2c)).
            norm[y] = Math.Sqrt((2 - c) / (2 * c));
        }

        // Per-cell tone/noise mixing only in Sines mode and only when something is noisy (see the header).
        bool mixed = settings.Mode == SynthMode.Sines && image.HasNoise();
        var t = new PaintTables
        {
            Mixed = mixed,
            ImageN = mixed ? new double[(w + 1) * h] : [],
            Width = w,
            Height = h,
            PassSeconds = image.DurationSeconds,
            SampleRate = sampleRate,
            Ksmps = ksmps,
            Settings = settings,
            ImageL = new double[(w + 1) * h],
            ImageR = new double[(w + 1) * h],
            Frequencies = oscFreq,
            NoiseCoef = coef,
            NoiseNorm = norm,
            SampleWave = wave,
            Muted = muted,
        };
        // Fill the image tables (the extra column W stays zero), then decide mono/stereo and the gain.
        t.UpdateColumns(image, 0, w);
        t.Stereo = image.IsStereo();
        t.UpdateGain(settings);
        return t;
    }

    /// <summary>
    /// Recomputes the amplitudes of columns [x0, x1) from <paramref name="image"/> (live painting).
    /// The image must have the same size as the tables.
    /// </summary>
    /// <param name="image">The canvas (same width and height as the tables).</param>
    /// <param name="x0">First column (clamped to 0…Width).</param>
    /// <param name="x1">One past the last column (clamped to x0…Width).</param>
    /// <remarks>Muted rows are written as 0 whatever is painted there. Uses the level curve of
    /// <see cref="Settings"/> (the build-time floor and curve).</remarks>
    public void UpdateColumns(SpectralImage image, int x0, int x1)
    {
        int h = Height;
        x0 = Math.Clamp(x0, 0, Width);
        x1 = Math.Clamp(x1, x0, Width);
        // Level → amplitude lookup (levels are floats 0…1; a 4097-entry table is exact enough and
        // avoids a Math.Pow per cell: 512×1024 cells convert in ~2 ms).
        var lut = AmplitudeTable(Settings);
        // Noisiness is copied as it is (no curve: 0.5 means an equal-power half/half mix). An image
        // whose layer was removed while Mixed tables are in use simply uploads zeros (= tonal).
        var src = Mixed ? image.Noise : null;
        if (Mixed)
            for (int x = x0; x < x1; x++)
                for (int y = 0; y < h; y++)
                    ImageN[x * h + y] = src is null ? 0 : Math.Clamp(src[x * h + y], 0f, 1f);
        for (int x = x0; x < x1; x++)
        {
            int o = x * h;
            for (int y = 0; y < h; y++)
            {
                if (Muted[y])
                {
                    ImageL[o + y] = ImageR[o + y] = 0;
                    continue;
                }
                // Round the level to the nearest of the 4097 LUT steps (index 0…4096).
                ImageL[o + y] = lut[(int)(Math.Clamp(image.Left[o + y], 0f, 1f) * 4096 + 0.5f)];
                ImageR[o + y] = lut[(int)(Math.Clamp(image.Right[o + y], 0f, 1f) * 4096 + 0.5f)];
            }
        }
    }

    /// <summary>
    /// Re-evaluates <see cref="PeakEstimate"/> and <see cref="OutputGain"/>
    /// (after edits or a gain change). <paramref name="settings"/> may differ from the build
    /// settings only in gain/level fields.
    /// </summary>
    /// <param name="settings">Source of the live fields (gain dB, auto level, attack, release);
    /// any other difference is ignored.</param>
    /// <remarks>
    /// <see cref="Stereo"/> is NOT re-evaluated here (it is fixed per note, see <see cref="NoteStereo"/>).
    /// Cost: one pass over both layers (≈ 1 M cells for 512 × 1024), done at every live upload.
    /// </remarks>
    public void UpdateGain(PaintSynthSettings settings)
    {
        // Keep the build-time mode, curve, floor…; take only the four live fields.
        Settings = Settings with { GainDb = settings.GainDb, AutoLevel = settings.AutoLevel, AttackMs = settings.AttackMs, ReleaseMs = settings.ReleaseMs };
        PeakEstimate = EstimatePeak(ImageL, ImageR, Width, Height, Settings.Mode, Mixed ? ImageN : null);
        // Automatic level: scale the estimated peak to −6 dBFS, but never by more than ×1000
        // (+60 dB), so a nearly silent canvas does not blow up a faint stroke. Silent canvas → 1.
        double auto = Settings.AutoLevel && PeakEstimate > 1e-9 ? Math.Min(1000, AutoLevelTarget / PeakEstimate) : 1;
        // Then the user's gain: dB → linear (10^(dB/20)), clamped to −96…+24 dB.
        OutputGain = auto * Math.Pow(10, Math.Clamp(Settings.GainDb, -96, 24) / 20);
    }

    /// <summary>Marks the tables stereo when an edit made the layers differ (never switches back while playing).</summary>
    /// <param name="stereo">True to mark stereo; false leaves the flag as it is.</param>
    /// <remarks>Currently unused: <see cref="PaintLivePlayer"/> restarts the note instead when a mono
    /// image becomes stereo.</remarks>
    public void NoteStereo(bool stereo) => Stereo |= stereo;

    /// <summary>
    /// Estimated output peak at gain 1: the maximum over columns and channels of
    /// min(Σa, 4.2·√(Σa²/2)) for oscillators (Σa = all partials in phase, the worst case; the
    /// second term = 4.2 × the RMS of many partials with random phases, which is what really
    /// happens with hundreds of rows), and 4.2·√(Σa²/2) for noise (Gaussian, no fixed peak).
    /// </summary>
    /// <param name="l">Left amplitudes (column-major, at least width·height values).</param>
    /// <param name="r">Right amplitudes, same layout.</param>
    /// <param name="width">Columns to examine (the silent extra column is not needed).</param>
    /// <param name="height">Rows per column.</param>
    /// <param name="mode">Synthesis mode (noise uses the RMS-based term only).</param>
    /// <param name="noise">Per-cell noisiness (same layout) of a mixed picture, or <c>null</c>. A column
    /// in which any audible cell is noticeably noisy (n &gt; 0.01) is estimated like noise (RMS term
    /// only); the equal-power crossfade keeps each row's power a²/2 whatever the mix, so the RMS term
    /// stays exact.</param>
    /// <returns>Estimated linear peak at gain 1 (0 for a silent image).</returns>
    /// <remarks>
    /// √(Σa²/2) is the RMS of a sum of sines with amplitudes a (each contributes a²/2 of power);
    /// 4.2 × RMS is a Gaussian crest factor (≈ 12.5 dB: exceeded with probability ~3·10⁻⁵).
    /// Attack/release smoothing and glide only lower the real peak, so the estimate is safe.
    /// </remarks>
    public static double EstimatePeak(double[] l, double[] r, int width, int height, SynthMode mode, double[]? noise = null)
    {
        double worst = 0;
        foreach (var layer in new[] { l, r })
            for (int x = 0; x < width; x++)
            {
                double s1 = 0, s2 = 0;
                bool noisy = false;
                int o = x * height;
                for (int y = 0; y < height; y++)
                {
                    double a = layer[o + y];
                    s1 += a;
                    s2 += a * a;
                    if (noise is not null && a > 0 && noise[o + y] > 0.01) noisy = true;
                }
                double rmsPeak = 4.2 * Math.Sqrt(s2 / 2);
                double est = mode == SynthMode.NoiseBands || noisy ? rmsPeak : Math.Min(s1, rmsPeak);
                if (est > worst) worst = est;
            }
        return worst;
    }

    /// <summary>Level (0…1 in 4096 steps) → amplitude, with the settings' floor and curve.</summary>
    /// <param name="s">Settings providing <see cref="PaintSynthSettings.LevelToAmplitude"/>.</param>
    /// <returns>4097 amplitudes: entry i is the amplitude of level i/4096.</returns>
    static double[] AmplitudeTable(PaintSynthSettings s)
    {
        var lut = new double[4097];
        for (int i = 0; i <= 4096; i++)
            lut[i] = s.LevelToAmplitude(i / 4096.0);
        return lut;
    }

    /// <summary>
    /// Mono, peak-normalised, resampled to the next power-of-two length (at most
    /// <see cref="MaxSampleLength"/>; longer files are cut). Returns the table and the duration
    /// in seconds of the material it holds (one loop).
    /// </summary>
    /// <param name="audio">The decoded sample (any rate and channel count).</param>
    /// <returns>The wave table (peak 1, faded ends) and the real duration of one loop in seconds,
    /// which <see cref="Build"/> needs to compute the loop rates.</returns>
    /// <exception cref="InvalidOperationException">Fewer than 16 frames.</exception>
    /// <remarks>
    /// The power-of-two length is the next one ≥ the material (capped at <see cref="MaxSampleLength"/>),
    /// so short samples are UPsampled onto more points (no loss), long ones may be slightly
    /// downsampled without an anti-alias filter (acceptable for textures).
    /// </remarks>
    static (double[] Wave, double LoopSeconds) MakeSampleWave(AudioData audio)
    {
        float[] mono = audio.Mono();
        if (mono.Length < 16)
            throw new InvalidOperationException("The sound file is too short.");
        int maxFrames = (int)Math.Min(mono.Length, (long)audio.SampleRate * 20); // ≤ 20 s of material
        int n = 1;
        while (n < maxFrames && n < MaxSampleLength) n <<= 1; // n = next power of two ≥ maxFrames (≤ 2^20)
        double loopSeconds = (double)maxFrames / audio.SampleRate;
        var wave = new double[n];
        double step = (double)maxFrames / n; // source frames per table point (≤ 1 except for long hi-res files)
        double peak = 0;
        // Linear-interpolation resampling of the first maxFrames frames onto n points.
        for (int i = 0; i < n; i++)
        {
            double s = i * step;
            int i0 = (int)s;
            int i1 = Math.Min(i0 + 1, maxFrames - 1);
            double v = mono[i0] + (mono[i1] - mono[i0]) * (s - i0);
            wave[i] = v;
            peak = Math.Max(peak, Math.Abs(v));
        }
        if (peak > 1e-9)
            for (int i = 0; i < n; i++) wave[i] /= peak;
        // A short fade at both ends hides the click where the loop wraps around.
        // Linear ramps of up to 256 table points, at most 1/8 of the table.
        int fade = Math.Min(n / 8, 256);
        for (int i = 0; i < fade; i++)
        {
            double g = (double)i / fade;
            wave[i] *= g;
            wave[n - 1 - i] *= g;
        }
        return (wave, loopSeconds);
    }

    /// <summary>Gaussian noise with unit variance (Box–Muller, fixed seed).</summary>
    /// <returns><see cref="NoiseLength"/> independent N(0, 1) values.</returns>
    static double[] MakeNoise()
    {
        // Seeded System.Random: the same sequence on every run and machine of this .NET version,
        // so a render is bit-repeatable.
        var rng = new Random(20261104);
        var a = new double[NoiseLength];
        for (int i = 0; i < a.Length; i += 2)
        {
            // Box–Muller: two uniforms → two independent Gaussians m·cos θ and m·sin θ, with
            // m = √(−2 ln u1) and θ = 2π·u2. u1 = 1 − NextDouble() lies in (0, 1], so ln never sees 0.
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            double m = Math.Sqrt(-2 * Math.Log(u1));
            a[i] = m * Math.Cos(2 * Math.PI * u2);
            if (i + 1 < a.Length) a[i + 1] = m * Math.Sin(2 * Math.PI * u2);
        }
        return a;
    }
}
