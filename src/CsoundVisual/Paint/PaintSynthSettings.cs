// PaintSynthSettings.cs — Paint Rooms foundation.
//
// Role: every parameter of «how an image becomes sound» (synthesis mode, level curve, smoothing,
// gain, noise bandwidth, sample, loop…) as an immutable record, plus the persisted preferences of
// the Paint Rooms window (PaintRoomSettings, stored inside the app's settings.json).
//
// Immutable on purpose: the UI builds a new record on every change and hands snapshots to the
// synthesis (live player, offline renderer) running on other threads, so no locking is needed.


namespace CsoundVisual.Paint;

/// <summary>How each image row is turned into sound.</summary>
public enum SynthMode
{
    /// <summary>One sine oscillator per row (additive synthesis): pure, clear tones.</summary>
    Sines,
    /// <summary>
    /// One narrow band of noise per row (two quadrature oscillators whose amplitudes are randomly
    /// modulated): breathy, airy, wind- or sea-like textures. Band width follows the row spacing.
    /// </summary>
    NoiseBands,
    /// <summary>
    /// Every row loops a chosen sound file transposed to the row's pitch (the file's root pitch
    /// sounds at its original speed): turns any recording into a playable texture.
    /// </summary>
    SampleLoop,
}

/// <summary>How the colours of an imported picture become the left/right levels (and, for the RGB modes, the noise layer).</summary>
/// <remarks>Stored in the settings as an int: new values are appended at the END only, so saved choices stay valid.</remarks>
public enum ColorMapping
{
    /// <summary>Brightness (luma) → both layers (mono).</summary>
    Brightness,
    /// <summary>Red → left layer, green → right layer (blue is ignored); yellow = both. Matches the app's own PNG export.</summary>
    RedGreen,
    /// <summary>Brightness → level, hue → pan (red/magenta left, green/cyan right, yellow/blue/grey centre), scaled by saturation.</summary>
    BrightnessHuePan,
    /// <summary>
    /// RGB processing: red → left level, green → right level, BLUE → the cell's noisiness (0 = pure
    /// sine, 1 = noise band; SpectralImage.Noise). Alpha is ignored (every pixel counts as opaque).
    /// The exact inverse of the PNG export of a picture that has a noise layer.
    /// </summary>
    RgbNoise,
    /// <summary>
    /// RGBA processing: as <see cref="RgbNoise"/>, and the ALPHA channel multiplies the levels
    /// (transparent = silence, half transparent = half the level): left = R·A, right = G·A,
    /// noise = B (alpha-weighted when pixels are averaged).
    /// </summary>
    RgbaNoiseMask,
}

/// <summary>Parameters of the image → sound conversion (immutable; build a new one with <c>with</c>).</summary>
/// <remarks>
/// All members are value types or strings, so record VALUE equality is meaningful:
/// <see cref="PaintSession.Synth"/> ignores identical copies, and
/// <see cref="PaintLivePlayer.UpdateSettings"/> compares records to decide whether a change can be
/// applied live (gain, auto level, attack, release) or needs a new note. Keep new members
/// equality-friendly (no arrays or mutable objects).
/// </remarks>
public sealed record PaintSynthSettings
{
    /// <summary>Synthesis mode.</summary>
    public SynthMode Mode { get; init; } = SynthMode.Sines;

    /// <summary>Output gain in dB on top of the automatic level (−60…+12).</summary>
    public double GainDb { get; init; }

    /// <summary>
    /// Automatic level: the gain is computed from the image so that its loudest moment peaks near
    /// −6 dBFS (estimate from the column sums, see <see cref="PaintTables.EstimatePeak"/>). Off =
    /// a level of 1.0 is a full-scale sine.
    /// </summary>
    public bool AutoLevel { get; init; } = true;

    /// <summary>Time constant (ms) with which a row's amplitude RISES towards a brighter column (0.5…2000).</summary>
    public double AttackMs { get; init; } = 8;

    /// <summary>Time constant (ms) with which a row's amplitude FALLS towards a darker column (0.5…4000).</summary>
    public double ReleaseMs { get; init; } = 40;

    /// <summary>Interpolate linearly between neighbouring columns (smooth) instead of stepping at every column (rhythmic, «pixelated»).</summary>
    public bool Glide { get; init; } = true;

    /// <summary>
    /// Level → amplitude exponent: amplitude = level^Curve. 1 = linear (pale pixels stay loud),
    /// 2 (default) ≈ perceptual (a 50 % grey is −12 dB), 3–4 = only bright pixels matter.
    /// </summary>
    public double Curve { get; init; } = 2;

    /// <summary>Levels at or below this (0…0.5) are silent: cleans the faint noise of photographs.</summary>
    public double Floor { get; init; } = 0.02;

    /// <summary>Noise-band mode: band width as a multiple of the spacing between neighbouring rows (0.1…4).</summary>
    public double NoiseWidth { get; init; } = 1;

    /// <summary>
    /// Noise-band mode: true (default) = «smooth» noise, two oscillators per row (sine and cosine
    /// with independent random amplitudes — a true narrow-band Gaussian noise); false = «light»,
    /// one oscillator per row (half the CPU; narrow bands sound slightly more like random tremolo).
    /// </summary>
    public bool SmoothNoise { get; init; } = true;

    /// <summary>Sample-loop mode: the sound file (WAV) each row loops; <c>null</c> = none chosen.</summary>
    public string? SamplePath { get; init; }

    /// <summary>Sample-loop mode: the pitch (Hz) at which the file plays at its original speed.</summary>
    public double SampleRootHz { get; init; } = PitchAxis.DefaultBaseHz;

    /// <summary>Play the image over and over (live preview only; renders play it once).</summary>
    public bool Loop { get; init; }

    /// <summary>Sample rate of WAV renders (44100, 48000, 88200 or 96000).</summary>
    public int RenderSampleRate { get; init; } = 48000;

    /// <summary>Normalise renders so the TRUE peak is exactly −1 dBTP (otherwise they are only scaled down if their true peak would exceed −1 dBTP).</summary>
    public bool NormalizeRender { get; init; } = true;

    /// <summary>
    /// Converts a painted level (0…1) to a linear amplitude with <see cref="Floor"/> and
    /// <see cref="Curve"/>: 0 at or below the floor, then ((level − floor)/(1 − floor))^curve.
    /// </summary>
    /// <param name="level">Painted level 0…1 (values above 1 are treated as 1).</param>
    /// <returns>Linear amplitude 0…1. Floor is clamped to 0…0.5 and the curve to 0.25…6, so
    /// out-of-range settings from an edited settings.json cannot break the synthesis.</returns>
    /// <remarks>Evaluated 4097 times per table build (a lookup table, see <see cref="PaintTables"/>),
    /// never per pixel.</remarks>
    public double LevelToAmplitude(double level)
    {
        double floor = Math.Clamp(Floor, 0, 0.5);
        if (level <= floor) return 0;
        double t = Math.Min(1, (level - floor) / (1 - floor));
        return Math.Pow(t, Math.Clamp(Curve, 0.25, 6));
    }
}

/// <summary>
/// Paint Rooms preferences persisted in <c>settings.json</c> (property <c>Paint</c> of
/// <c>AppSettings</c>): last pitch axis and scale file, canvas size, synthesis settings and brush.
/// </summary>
/// <remarks>A mutable class (unlike <see cref="PaintSynthSettings"/>): serialised with
/// System.Text.Json, so properties need public setters; it is only touched on the UI thread.</remarks>
public sealed class PaintRoomSettings
{
    /// <summary>Pitch axis kind of new canvases.</summary>
    public PitchAxisKind AxisKind { get; set; } = PitchAxisKind.Logarithmic;

    /// <summary>Low frequency limit (Hz).</summary>
    public double LowHz { get; set; } = 40;

    /// <summary>High frequency limit (Hz).</summary>
    public double HighHz { get; set; } = 12000;

    /// <summary>Rows of continuous axes (resolution).</summary>
    public int Rows { get; set; } = 512;

    /// <summary>Steps per octave of equal-tempered axes.</summary>
    public int StepsPerOctave { get; set; } = 12;

    /// <summary>Frequency of scale degree 0 (Hz) for discrete axes.</summary>
    public double BaseHz { get; set; } = PitchAxis.DefaultBaseHz;

    /// <summary>Last Scala file used (re-loaded at start when it still exists).</summary>
    public string? ScalaPath { get; set; }

    /// <summary>Columns of new canvases.</summary>
    public int Width { get; set; } = 1024;

    /// <summary>Duration of new canvases (seconds).</summary>
    public double DurationSeconds { get; set; } = 12;

    /// <summary>Synthesis parameters.</summary>
    public PaintSynthSettings Synth { get; set; } = new();

    /// <summary>Colour mapping used when importing pictures.</summary>
    public ColorMapping ImportMapping { get; set; } = ColorMapping.Brightness;

    /// <summary>File › Export PNG writes 16 bits per channel (true, default: lossless round trips) or 8 bits (smaller, universal).</summary>
    public bool Export16Bit { get; set; } = true;

    /// <summary>Brush noise amount 0 (tone) … 1 (noise band) painted into the noise layer (BrushSettings.Noise).</summary>
    public double BrushNoise { get; set; }

    /// <summary>The brush paints only the noise amount, leaving the levels alone (BrushSettings.NoiseOnly).</summary>
    public bool NoiseOnly { get; set; }

    /// <summary>The canvas draws the noise layer as a grainy texture (PaintCanvas.ShowNoise).</summary>
    public bool ShowNoise { get; set; } = true;

    /// <summary>Image Sequencer room: tempo, patterns, arrangement and its sound (null until the room is first used).</summary>
    public SequencerPrefs? Sequencer { get; set; }

    /// <summary>How imported pictures are fitted to the canvas (last choice of the import dialog).</summary>
    public ImportFit ImportFit { get; set; } = ImportFit.FullDetail;

    /// <summary>
    /// Show the import dialog for every picture (true) or import directly with the remembered
    /// <see cref="ImportFit"/>, <see cref="ImportMapping"/> and current duration (false: the
    /// dialog's «don't ask again» box). File › Import picture always asks.
    /// </summary>
    public bool AskOnImport { get; set; } = true;

    /// <summary>Selected tool.</summary>
    public PaintTool Tool { get; set; } = PaintTool.Pencil;

    /// <summary>Brush size in screen pixels.</summary>
    public double BrushSize { get; set; } = 10;

    /// <summary>Brush opacity (target level) 0…1.</summary>
    public double Opacity { get; set; } = 0.85;

    /// <summary>Brush hardness 0 (soft edge) … 1 (hard edge).</summary>
    public double Hardness { get; set; } = 0.8;

    /// <summary>Brush pan −1 (left only) … 0 (both) … +1 (right only).</summary>
    public double Pan { get; set; }

    /// <summary>Harmonics brush: number of partials including the fundamental.</summary>
    public int Harmonics { get; set; } = 8;

    /// <summary>Harmonics brush: amplitude roll-off exponent (partial k gets k^−rolloff).</summary>
    public double Rolloff { get; set; } = 1;

    /// <summary>Harmonics brush: odd partials only (hollow, clarinet-like).</summary>
    public bool OddOnly { get; set; }

    /// <summary>Soft edge of new selections, in screen pixels (0 = crisp; see <c>PaintCanvas.SelectionFeather</c>).</summary>
    /// <remarks>The Spectrum Room uses the value of the shared (Paint Synth) settings; the Image Filter its own canvas settings.</remarks>
    public double SelectionFeather { get; set; }

    /// <summary>Spectrum Room preferences (analysis resolution, dB floor, axis, resynthesis mode).</summary>
    public SpectrumRoomSettings Spectrum { get; set; } = new();

    /// <summary>
    /// Image Filter room preferences (filter parameters, its own canvas preferences, last source);
    /// <c>null</c> until the room is first opened. Deliberately NOT initialised here: the filter's
    /// own canvas preferences are a PaintRoomSettings too, so a default instance would recurse.
    /// </summary>
    public ImageFilterPrefs? Filter { get; set; }

    /// <summary>Builds the pitch axis these settings describe (falls back to a logarithmic axis on errors).</summary>
    /// <param name="error">Why the requested axis could not be built, or <c>null</c>.</param>
    /// <returns>The requested axis; on any failure a logarithmic axis over the same range, and if
    /// even that is invalid (e.g. LowHz ≥ HighHz in a hand-edited file) the default 40 Hz–12 kHz × 512.</returns>
    public PitchAxis BuildAxis(out string? error)
    {
        error = null;
        try
        {
            switch (AxisKind)
            {
                case PitchAxisKind.Linear: return PitchAxis.Linear(LowHz, HighHz, Rows);
                case PitchAxisKind.EqualTemperament: return PitchAxis.EqualTemperament(LowHz, HighHz, StepsPerOctave, BaseHz);
                case PitchAxisKind.Scala when ScalaPath is not null && File.Exists(ScalaPath):
                    return PitchAxis.FromScala(ScalaScale.Load(ScalaPath), LowHz, HighHz, BaseHz);
                case PitchAxisKind.Scala:
                    error = "Scala file not found";
                    break;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        try { return PitchAxis.Logarithmic(LowHz, HighHz, Rows); }
        catch { return PitchAxis.Logarithmic(40, 12000, 512); }
    }
}
