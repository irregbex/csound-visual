// ImageFilterSettings.cs — Paint Rooms · «Image Filter» room: its parameters and persisted preferences.
//
// Role: every parameter of «how a picture filters a sound» as an immutable record
// (ImageFilterSettings), plus what the room remembers between sessions (ImageFilterPrefs: the
// settings, the filter canvas's own axis/size/brush preferences, the last source file and kind).
//
// The model in one paragraph: the filter image is a SpectralImage (columns = time, rows = pitch on a
// PitchAxis, two layers L/R with levels 0…1). At any moment the column under the play head is a
// STEREO EQUALISER CURVE: the level of each row is the gain of the band around that row's
// frequency, the left layer filtering the left channel and the right layer the right channel.
// Csound does the filtering with a phase vocoder (pvsanal → pvsmaska → pvsynth, one chain per
// channel), so every FFT bin gets its own gain; ImageFilterTables maps rows → bins.
//
// Immutable on purpose (like PaintSynthSettings): the UI builds a new record on every change and
// hands snapshots to the live player and the renderer, which run on other threads.

namespace CsoundVisual.Paint;

/// <summary>How the filter image's width is mapped onto time.</summary>
public enum FilterTimeMode
{
    /// <summary>
    /// The image is stretched over the whole source sound: its first column filters the first
    /// moment of the file, its last column the last moment. Live input has no length, so it falls
    /// back to <see cref="Seconds"/>.
    /// </summary>
    FitToSound,
    /// <summary>The image lasts <see cref="ImageFilterSettings.LoopSeconds"/> and repeats.</summary>
    Seconds,
    /// <summary>The image lasts <see cref="ImageFilterSettings.Bars"/> bars of 4/4 at <see cref="ImageFilterSettings.Bpm"/> and repeats (tempo-synced patterns).</summary>
    Bars,
}

/// <summary>How a painted level (0…1) becomes a gain.</summary>
public enum FilterResponse
{
    /// <summary>Gain = level (× boost): 0.5 = −6 dB. The simplest rule: «brightness is gain».</summary>
    Linear,
    /// <summary>
    /// Level spreads over a decibel range: 1 = boost, 0.5 = boost − range/2 dB, 0 = silence. Grey
    /// pictures sound more «musical» (mid greys are audible but clearly lower).
    /// </summary>
    Decibels,
}

/// <summary>Where the filter takes its input sound from.</summary>
public enum FilterSourceKind
{
    /// <summary>A WAV file chosen in the room (picker or drag-and-drop).</summary>
    File,
    /// <summary>The sound another room shared through <see cref="PaintSession.Sound"/> (e.g. the Spectrum Room).</summary>
    Shared,
    /// <summary>The live audio input of the app's engine (Settings › Audio › Input): voice, guitar…</summary>
    LiveInput,
}

/// <summary>Parameters of the image → filter conversion (immutable; build a new one with <c>with</c>).</summary>
public sealed record ImageFilterSettings
{
    /// <summary>FFT sizes offered in the room (frame = window size).</summary>
    public static readonly int[] FftSizes = [256, 512, 1024, 2048, 4096];

    /// <summary>Overlap factors offered (frames per window length): 4 is the classic Hann overlap, 8 smoother.</summary>
    public static readonly int[] Overlaps = [4, 8];

    /// <summary>
    /// FFT frame size in samples (256…4096, power of two). Bigger = finer frequency resolution
    /// (narrower bands, bins sr/N Hz apart) but more latency and «smeared» attacks. Default 1024
    /// (47 Hz bins at 48 kHz).
    /// </summary>
    public int FftSize { get; init; } = 1024;

    /// <summary>Overlap factor (4 or 8): hop = FftSize / Overlap samples between analysis frames.</summary>
    public int Overlap { get; init; } = 4;

    /// <summary>How the image's width maps to time (see <see cref="FilterTimeMode"/>).</summary>
    public FilterTimeMode TimeMode { get; init; } = FilterTimeMode.FitToSound;

    /// <summary>Length of one pass over the image in <see cref="FilterTimeMode.Seconds"/> mode (0.05…3600 s).</summary>
    public double LoopSeconds { get; init; } = 4;

    /// <summary>Bars of 4/4 per pass in <see cref="FilterTimeMode.Bars"/> mode (0.25…256).</summary>
    public double Bars { get; init; } = 2;

    /// <summary>Tempo for <see cref="FilterTimeMode.Bars"/> mode (20…400 BPM).</summary>
    public double Bpm { get; init; } = 120;

    /// <summary>Speed of the play head over the image (0.05…8; 2 = the image passes twice as fast).</summary>
    public double Speed { get; init; } = 1;

    /// <summary>Blend neighbouring columns (smooth morphing) instead of stepping from column to column.</summary>
    public bool Glide { get; init; } = true;

    /// <summary>
    /// Time constant (ms) of the per-bin one-pole smoothing applied in Csound to the gain curve
    /// (0.5…2000). Avoids «zipper» noise when the image changes abruptly; long values make the
    /// filter lag behind the picture like a slow envelope.
    /// </summary>
    public double SmoothingMs { get; init; } = 25;

    /// <summary>Level → gain rule.</summary>
    public FilterResponse Response { get; init; } = FilterResponse.Linear;

    /// <summary>Decibel range of <see cref="FilterResponse.Decibels"/> (6…96 dB): level 0.5 = boost − range/2.</summary>
    public double RangeDb { get; init; } = 48;

    /// <summary>
    /// Gain of a fully bright pixel in dB (0…+12). 0 = white is unity (the filter can only cut);
    /// higher values let bright areas BOOST their bands.
    /// </summary>
    public double BoostDb { get; init; }

    /// <summary>Dry/wet mix 0…1: 0 = only the (latency-aligned) source, 1 = only the filtered sound. Live.</summary>
    public double Mix { get; init; } = 1;

    /// <summary>Output gain in dB (−60…+12; the self-tests use −96). Live.</summary>
    public double OutputDb { get; init; }

    /// <summary>Live preview of a file source: repeat it (the image keeps its own timing).</summary>
    public bool LoopSource { get; init; } = true;

    /// <summary>Renders: normalise the TRUE peak to −1 dBTP (otherwise the level is kept and only lowered if its true peak would exceed −1 dBTP).</summary>
    public bool NormalizeRender { get; init; }

    /// <summary>Hop size in samples (FftSize / Overlap).</summary>
    public int Hop => Math.Max(1, FftSize / Math.Max(1, Overlap));

    /// <summary>Number of FFT bins per frame, DC to Nyquist: FftSize/2 + 1.</summary>
    public int Bins => FftSize / 2 + 1;

    /// <summary>
    /// Latency of the phase-vocoder chain in samples: FftSize + 2·Hop. MEASURED with Csound 7
    /// (impulse through pvsanal → pvsmaska → pvsynth; 256/64 → 384, 1024/256 → 1536, 1024/128 →
    /// 1280, 4096/1024 → 6144, identical for ksmps 1…256): a full analysis window must be filled
    /// before the first frame exists, and the overlap-add output buffer adds two hops.
    /// </summary>
    public int LatencySamples => FftSize + 2 * Hop;

    /// <summary>Latency in milliseconds at <paramref name="sampleRate"/> (for the room's read-out).</summary>
    public double LatencyMs(double sampleRate) => LatencySamples * 1000.0 / Math.Max(1, sampleRate);

    /// <summary>
    /// Seconds one pass over the image lasts, given the source length (<paramref name="soundSeconds"/>,
    /// ≤ 0 for live input): the time mode's length divided by <see cref="Speed"/>.
    /// </summary>
    public double PassSeconds(double soundSeconds)
    {
        double basis = TimeMode switch
        {
            FilterTimeMode.FitToSound when soundSeconds > 0 => soundSeconds,
            FilterTimeMode.Bars => Math.Clamp(Bars, 0.25, 256) * 4 * 60 / Math.Clamp(Bpm, 20, 400),
            _ => Math.Clamp(LoopSeconds, 0.05, 3600),
        };
        return Math.Clamp(basis / Math.Clamp(Speed, 0.05, 8), 0.02, 36000);
    }

    /// <summary>
    /// Converts a painted level (0…1) into a linear gain with <see cref="Response"/>,
    /// <see cref="RangeDb"/> and <see cref="BoostDb"/>. Levels at or below 0.001 are silence in
    /// both rules, so black really mutes (and the «> 30 dB rejection» of unpainted areas holds).
    /// </summary>
    public double LevelToGain(double level)
    {
        if (level <= 0.001) return 0;
        level = Math.Min(1, level);
        double boost = Math.Pow(10, Math.Clamp(BoostDb, 0, 12) / 20);
        return Response == FilterResponse.Linear
            ? level * boost
            : boost * Math.Pow(10, -(1 - level) * Math.Clamp(RangeDb, 6, 96) / 20);
    }

    /// <summary>Returns a copy whose numbers are all inside their documented ranges (settings read from disk may be anything).</summary>
    public ImageFilterSettings Sanitized() => this with
    {
        FftSize = FftSizes.Contains(FftSize) ? FftSize : 1024,
        Overlap = Overlaps.Contains(Overlap) ? Overlap : 4,
        LoopSeconds = Math.Clamp(LoopSeconds, 0.05, 3600),
        Bars = Math.Clamp(Bars, 0.25, 256),
        Bpm = Math.Clamp(Bpm, 20, 400),
        Speed = Math.Clamp(Speed, 0.05, 8),
        SmoothingMs = Math.Clamp(SmoothingMs, 0.5, 2000),
        RangeDb = Math.Clamp(RangeDb, 6, 96),
        BoostDb = Math.Clamp(BoostDb, 0, 12),
        Mix = Math.Clamp(Mix, 0, 1),
        OutputDb = Math.Clamp(OutputDb, -96, 12),
    };
}

/// <summary>
/// What the Image Filter room remembers in <c>settings.json</c> (property <c>Filter</c> of
/// <see cref="PaintRoomSettings"/>): the filter parameters, the filter canvas's own preferences
/// (axis, size, brush — separate from Paint Synth's), and the last source.
/// </summary>
public sealed class ImageFilterPrefs
{
    /// <summary>Filter parameters.</summary>
    public ImageFilterSettings Settings { get; set; } = new();

    /// <summary>
    /// Preferences of the filter canvas. Defaults suit an equaliser: logarithmic 30 Hz – 18 kHz
    /// (almost the whole audible range), 384 rows, 512 columns.
    /// </summary>
    public PaintRoomSettings Canvas { get; set; } = new()
    {
        AxisKind = PitchAxisKind.Logarithmic, LowHz = 30, HighHz = 18000, Rows = 384, Width = 512, DurationSeconds = 4,
        BrushSize = 28, Opacity = 1, Hardness = 0.4,
    };

    /// <summary>Where the input comes from.</summary>
    public FilterSourceKind Source { get; set; } = FilterSourceKind.File;

    /// <summary>Last WAV file used as source (re-loaded when the room opens, if it still exists).</summary>
    public string? SourcePath { get; set; }
}
