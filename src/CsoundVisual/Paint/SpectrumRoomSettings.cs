// SpectrumRoomSettings.cs — Paint Rooms: persisted preferences of the «Spectrum Room».
//
// Stored in settings.json inside the Paint Rooms preferences (PaintRoomSettings.Spectrum), so the
// room reopens with the analysis resolution, dB floor, pitch axis and resynthesis mode last used.
// Plain mutable properties for System.Text.Json (reflection-based, like the rest of AppSettings).
//
// Values are stored as the user typed them and are NOT validated here: a hand-edited settings.json
// may contain anything. Validation happens where they are used — Analysis goes through
// SpectrumAnalysisSettings.Normalized (FFT size rounded up to a power of two, overlap snapped to
// 2/4/8, column ms and floor clamped), BuildAxis through PaintRoomSettings.BuildAxis (falls back to
// a logarithmic axis on error). The SpectrumRoom UI writes every change straight into this object
// (UI thread); MainViewModel.SavePaintSettings persists it when Paint Rooms closes.

namespace CsoundVisual.Paint;

/// <summary>How the Spectrum Room turns its edited picture back into sound.</summary>
public enum SpectrumResynthesis
{
    /// <summary>Faithful: the original recording through a time-frequency gain mask (C#, STFT → mask → ISTFT).</summary>
    SpectralMask,
    /// <summary>Creative: the picture played by the Paint Synth Csound engine (one oscillator or noise band per row).</summary>
    Additive,
}

/// <summary>Spectrum Room preferences (see the file header).</summary>
public sealed class SpectrumRoomSettings
{
    /// <summary>FFT size (power of two, 256…32768).</summary>
    /// <remarks>
    /// Trade-off: frequency resolution sr/N Hz per bin vs. time resolution N/sr seconds per frame.
    /// 2048 at 48 kHz = 23.4 Hz per bin and a 43 ms window. The UI offers 512…16384.
    /// </remarks>
    public int FrameSize { get; set; } = 2048;

    /// <summary>Overlap factor (2, 4 or 8): hop = FrameSize / Overlap.</summary>
    public int Overlap { get; set; } = 4;

    /// <summary>Milliseconds per image column (0 = automatic, one column per hop).</summary>
    /// <remarks>
    /// Either way the picture is limited to <see cref="SpectralImage.MaxWidth"/> columns, so for long
    /// sounds the automatic setting gives columns longer than one hop.
    /// </remarks>
    public double ColumnMs { get; set; }

    /// <summary>dB value shown as black (level 0).</summary>
    /// <remarks>
    /// Defines the perceptual level scale: level = (dB − floor) / (−floor), so with −90 a level of
    /// 0.5 is −45 dB. It is also the analysis' «range» used to convert level differences back into gains.
    /// </remarks>
    public double FloorDb { get; set; } = -90;

    /// <summary>Pitch axis kind of the analysis.</summary>
    public PitchAxisKind AxisKind { get; set; } = PitchAxisKind.Logarithmic;

    /// <summary>Lowest analysed frequency (Hz).</summary>
    public double LowHz { get; set; } = 30;

    /// <summary>Highest analysed frequency (Hz).</summary>
    public double HighHz { get; set; } = 16000;

    /// <summary>Rows of continuous axes.</summary>
    /// <remarks>Only for <see cref="PitchAxisKind.Logarithmic"/> and <see cref="PitchAxisKind.Linear"/>; discrete axes get one row per note.</remarks>
    public int Rows { get; set; } = 512;

    /// <summary>Steps per octave of equal temperament.</summary>
    /// <remarks>Only for <see cref="PitchAxisKind.EqualTemperament"/> (12 = semitones, 24 = quarter tones…).</remarks>
    public int StepsPerOctave { get; set; } = 12;

    /// <summary>Resynthesis used by Play / Render.</summary>
    public SpectrumResynthesis Mode { get; set; } = SpectrumResynthesis.SpectralMask;

    /// <summary>Additive path: Paint Synth mode (sines or noise bands).</summary>
    public SynthMode AdditiveMode { get; set; } = SynthMode.Sines;

    /// <summary>Normalise rendered and extracted WAV files to −1 dBTP (true peak).</summary>
    /// <remarks>Off (default): the level is kept, and only lowered when the file would clip.</remarks>
    public bool Normalize { get; set; }

    /// <summary>Seconds of a «freeze» tone.</summary>
    /// <remarks>The Freeze dialog accepts 0.5…600 s.</remarks>
    public double FreezeSeconds { get; set; } = 8;

    /// <summary>Gain step of «Gain ±dB on selection».</summary>
    /// <remarks>dB, −60…+24 in the UI; negative = softer.</remarks>
    public double GainDb { get; set; } = -6;

    /// <summary>A copy of every setting (a snapshot for undo; the class itself is mutable).</summary>
    /// <returns>A new object with the same values (all members are values, so a shallow copy is complete).</returns>
    public SpectrumRoomSettings Clone() => (SpectrumRoomSettings)MemberwiseClone();

    /// <summary>Copies every setting from <paramref name="other"/> into this object (undo/redo of a snapshot).</summary>
    /// <param name="other">The snapshot to restore.</param>
    /// <remarks>This object is the one stored in the app settings, so it is updated in place, never replaced.</remarks>
    public void CopyFrom(SpectrumRoomSettings other)
    {
        FrameSize = other.FrameSize; Overlap = other.Overlap; ColumnMs = other.ColumnMs; FloorDb = other.FloorDb;
        AxisKind = other.AxisKind; LowHz = other.LowHz; HighHz = other.HighHz; Rows = other.Rows; StepsPerOctave = other.StepsPerOctave;
        Mode = other.Mode; AdditiveMode = other.AdditiveMode; Normalize = other.Normalize; FreezeSeconds = other.FreezeSeconds; GainDb = other.GainDb;
    }

    /// <summary>The analysis settings these preferences describe (computed; not stored in settings.json).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SpectrumAnalysisSettings Analysis => new SpectrumAnalysisSettings
    {
        FrameSize = FrameSize, Overlap = Overlap, ColumnMs = ColumnMs, FloorDb = FloorDb,
    }.Normalized();

    /// <summary>
    /// Builds the analysis pitch axis. Scala uses the scale file chosen in Paint Synth
    /// (<paramref name="scalaPath"/>) and the degree-0 frequency <paramref name="baseHz"/>;
    /// any error falls back to a logarithmic axis (<paramref name="error"/> says why).
    /// </summary>
    /// <param name="scalaPath">Path of the .scl file loaded in Paint Synth, or <c>null</c>.</param>
    /// <param name="baseHz">Frequency of scale degree 0 (Hz), shared with Paint Synth.</param>
    /// <param name="error">Receives <c>null</c> on success, else the reason for the fallback.</param>
    /// <returns>The axis to analyse on (never <c>null</c>).</returns>
    /// <remarks>
    /// Delegates to <see cref="PaintRoomSettings.BuildAxis"/> through a throw-away settings object, so
    /// the Spectrum Room and Paint Synth build axes with exactly the same rules.
    /// </remarks>
    public PitchAxis BuildAxis(string? scalaPath, double baseHz, out string? error)
    {
        var p = new PaintRoomSettings
        {
            AxisKind = AxisKind, LowHz = LowHz, HighHz = HighHz, Rows = Rows, StepsPerOctave = StepsPerOctave,
            ScalaPath = scalaPath, BaseHz = baseHz,
        };
        return p.BuildAxis(out error);
    }
}
