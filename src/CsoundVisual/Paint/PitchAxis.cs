// PitchAxis.cs — Paint Rooms foundation.
//
// Role: the vertical axis of every Paint Rooms canvas. It decides which frequency (Hz) each image
// row sounds at, and gives the labels drawn on the pitch ruler. Row 0 is ALWAYS the lowest pitch
// (the bottom of the canvas); rows increase upwards. Every SpectralImage carries one PitchAxis
// whose RowCount equals the image height.
//
// Four kinds of axis:
//   * Linear       — rows evenly spaced in Hz (good for spectra of harmonic sounds, the
//                    «Spectrum Room» resynthesis, or very bright noise textures);
//   * Logarithmic  — rows evenly spaced in octaves (the musical default: every octave gets the
//                    same height, like a piano roll with fine resolution);
//   * Equal temperament with any number of steps per octave (12 = the piano, 24 = quarter tones,
//                    19, 31, 53…): ONE ROW PER NOTE, so painting is like a piano roll;
//   * Scala        — one row per note of any .scl tuning (just intonation, gamelan, Bohlen–Pierce…),
//                    repeated at the scale's period.
//
// Pure logic: immutable, thread-safe, no UI and no Csound.

using System.Globalization;

namespace CsoundVisual.Paint;

/// <summary>How the rows of a canvas map to frequencies.</summary>
public enum PitchAxisKind
{
    /// <summary>Rows evenly spaced in Hz between the low and high limits.</summary>
    Linear,
    /// <summary>Rows evenly spaced in log-frequency (octaves) between the limits.</summary>
    Logarithmic,
    /// <summary>One row per note of an equal-tempered scale with any number of steps per octave.</summary>
    EqualTemperament,
    /// <summary>One row per note of a Scala (.scl) tuning, repeated at its period.</summary>
    Scala,
}

/// <summary>
/// Immutable mapping between canvas rows and frequencies (row 0 = lowest pitch, at the bottom).
/// Create one with <see cref="Linear"/>, <see cref="Logarithmic"/>, <see cref="EqualTemperament"/>
/// or <see cref="FromScala"/>.
/// </summary>
/// <remarks>
/// <para><b>Continuous axes</b> (linear, logarithmic) have a free number of rows chosen by the
/// caller (resolution). <b>Discrete axes</b> (equal temperament, Scala) have exactly one row per
/// note that falls inside [<see cref="LowHz"/>, <see cref="HighHz"/>]; their row count therefore
/// follows from the range and the scale.</para>
/// <para><b>Base frequency.</b> For discrete axes, <see cref="BaseHz"/> is the frequency of scale
/// degree 0 in octave «4». The default, 261.6256 Hz, is middle C when A4 = 440 Hz, so 12-tone
/// rows are labelled with ordinary note names (C4, C#4…). Other divisions are labelled
/// «octave·degree» (for example <c>4·7</c>).</para>
/// </remarks>
public sealed class PitchAxis : IEquatable<PitchAxis>
{
    /// <summary>Middle C (C4) when A4 = 440 Hz: the default frequency of scale degree 0.</summary>
    public const double DefaultBaseHz = 261.6255653005986;

    /// <summary>Most rows an axis may have (also bounds CPU: one oscillator per row and channel).</summary>
    public const int MaxRows = 2048;

    /// <summary>Fewest rows an axis may have.</summary>
    public const int MinRows = 2;

    /// <summary>Lowest frequency accepted for the limits (Hz).</summary>
    public const double MinHz = 8;

    /// <summary>Highest frequency accepted for the limits (Hz).</summary>
    public const double MaxHz = 24000;

    /// <summary>12-tone note names with sharps, indexed by pitch class (0 = C).</summary>
    static readonly string[] SharpNames =["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>Row frequencies in Hz, ascending (index = row).</summary>
    readonly double[] frequencies;

    /// <summary>
    /// For discrete axes: (octave, degree) of each row, used for labels. Octave 4 contains
    /// <see cref="BaseHz"/>. Null for continuous axes.
    /// </summary>
    readonly (int Octave, int Degree)[]? notes;

    /// <summary>Stores already validated values; only the static factories call it.</summary>
    /// <param name="kind">Kind of mapping.</param>
    /// <param name="low">Validated lower limit (Hz).</param>
    /// <param name="high">Validated upper limit (Hz).</param>
    /// <param name="steps">Steps per octave (equal temperament) or 0.</param>
    /// <param name="baseHz">Frequency of degree 0 in octave 4 (Hz).</param>
    /// <param name="scale">Scala tuning or null.</param>
    /// <param name="frequencies">Row frequencies, ascending (ownership is taken, not copied).</param>
    /// <param name="notes">(octave, degree) labels per row for discrete axes, or null.</param>
    PitchAxis(PitchAxisKind kind, double low, double high, int steps, double baseHz, ScalaScale? scale,
        double[] frequencies, (int, int)[]? notes)
    {
        Kind = kind;
        LowHz = low;
        HighHz = high;
        StepsPerOctave = steps;
        BaseHz = baseHz;
        Scale = scale;
        this.frequencies = frequencies;
        this.notes = notes;
    }

    /// <summary>The kind of mapping.</summary>
    public PitchAxisKind Kind { get; }

    /// <summary>Lower frequency limit in Hz (the bottom of the canvas for continuous axes).</summary>
    public double LowHz { get; }

    /// <summary>Upper frequency limit in Hz (the top of the canvas for continuous axes).</summary>
    public double HighHz { get; }

    /// <summary>Steps per octave of an equal-tempered axis (12 = semitones); 0 for the other kinds.</summary>
    public int StepsPerOctave { get; }

    /// <summary>Frequency of scale degree 0 in octave 4, for discrete axes (Hz).</summary>
    public double BaseHz { get; }

    /// <summary>The Scala tuning of a <see cref="PitchAxisKind.Scala"/> axis, otherwise <c>null</c>.</summary>
    public ScalaScale? Scale { get; }

    /// <summary>Number of rows (= height of an image using this axis).</summary>
    public int RowCount => frequencies.Length;

    /// <summary>True for axes with one row per note (equal temperament and Scala).</summary>
    public bool IsDiscrete => notes is not null;

    /// <summary>All row frequencies in Hz, ascending (a copy).</summary>
    public double[] Frequencies() => (double[])frequencies.Clone();

    /// <summary>Frequency of <paramref name="row"/> in Hz (clamped to the valid rows).</summary>
    public double FrequencyOf(int row) => frequencies[Math.Clamp(row, 0, frequencies.Length - 1)];

    /// <summary>
    /// Frequency at a fractional row (for the hover read-out and the harmonics brush): interpolated
    /// in log-frequency between the two neighbouring rows, extrapolated with the outer spacing.
    /// </summary>
    /// <param name="row">Fractional row (may be outside 0 … RowCount − 1).</param>
    /// <returns>Frequency in Hz.</returns>
    public double FrequencyAt(double row)
    {
        // Pick the segment [i, i+1] containing the row (the first/last segment when outside), then
        // interpolate linearly in ln(Hz); t outside 0…1 extrapolates the edge segment's ratio.
        int n = frequencies.Length;
        if (n == 1) return frequencies[0];
        int i = (int)Math.Floor(row);
        i = Math.Clamp(i, 0, n - 2);
        double t = row - i;
        double l0 = Math.Log(frequencies[i]), l1 = Math.Log(frequencies[i + 1]);
        return Math.Exp(l0 + (l1 - l0) * t);
    }

    /// <summary>
    /// Fractional row of a frequency (inverse of <see cref="FrequencyAt"/>): log-interpolated between
    /// the rows around it. Frequencies outside the axis return values below 0 or above
    /// <see cref="RowCount"/> − 1 (callers clip).
    /// </summary>
    /// <param name="hz">Frequency in Hz.</param>
    /// <returns>Fractional row; −∞ for <paramref name="hz"/> ≤ 0.</returns>
    public double RowOf(double hz)
    {
        int n = frequencies.Length;
        if (hz <= 0) return double.NegativeInfinity;
        double lh = Math.Log(hz);
        // Binary search for the last row whose frequency is <= hz.
        int lo = 0, hi = n - 1;
        // Outside the axis: extrapolate with the log spacing of the first / last pair of rows.
        if (hz <= frequencies[0])
            return (lh - Math.Log(frequencies[0])) / (Math.Log(frequencies[1]) - Math.Log(frequencies[0]));
        if (hz >= frequencies[n - 1])
            return n - 1 + (lh - Math.Log(frequencies[n - 1])) / (Math.Log(frequencies[n - 1]) - Math.Log(frequencies[n - 2]));
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (frequencies[mid] <= hz) lo = mid; else hi = mid;
        }
        double a = Math.Log(frequencies[lo]), b = Math.Log(frequencies[hi]);
        return lo + (lh - a) / (b - a);
    }

    /// <summary>The row whose frequency is nearest to <paramref name="hz"/> in pitch, or −1 when outside the axis by more than half a row.</summary>
    /// <param name="hz">Frequency in Hz.</param>
    public int NearestRow(double hz)
    {
        double r = RowOf(hz);
        if (double.IsNaN(r) || r < -0.5 || r > RowCount - 0.5) return -1;
        return (int)Math.Round(r);
    }

    /// <summary>
    /// Short ruler label of a row: the note name for 12-tone rows («A4»), «octave·degree» for other
    /// discrete scales («4·7»), and the frequency for continuous axes («440», «1.2k»).
    /// </summary>
    /// <param name="row">Row index (clamped to the valid rows).</param>
    public string LabelOf(int row)
    {
        row = Math.Clamp(row, 0, RowCount - 1);
        if (notes is not null)
        {
            var (oct, deg) = notes[row];
            if (Kind == PitchAxisKind.EqualTemperament && StepsPerOctave == 12)
                return SharpNames[deg] + oct.ToString(CultureInfo.InvariantCulture);
            return oct.ToString(CultureInfo.InvariantCulture) + "·" + deg.ToString(CultureInfo.InvariantCulture);
        }
        return FormatHz(frequencies[row]);
    }

    /// <summary>
    /// True when <paramref name="row"/> starts a period (degree 0: the «C» rows of a 12-tone axis).
    /// The canvas draws a stronger grid line there.
    /// </summary>
    /// <remarks>Always false on continuous axes (linear, logarithmic): they have no scale degrees.</remarks>
    /// <param name="row">Row index (clamped to the valid rows).</param>
    public bool IsPeriodStart(int row)
    {
        if (notes is not null)
            return notes[Math.Clamp(row, 0, RowCount - 1)].Degree == 0;
        return false;
    }

    /// <summary>Compact frequency text: «27.5», «440», «1.25k», «12k».</summary>
    /// <param name="hz">Frequency in Hz.</param>
    /// <returns>Invariant-culture text without unit (kHz values get a «k» suffix).</returns>
    public static string FormatHz(double hz)
    {
        var ci = CultureInfo.InvariantCulture;
        if (hz >= 10000) return (hz / 1000).ToString("0.#", ci) + "k";
        if (hz >= 1000) return (hz / 1000).ToString("0.##", ci) + "k";
        if (hz >= 100) return hz.ToString("0", ci);
        return hz.ToString("0.#", ci);
    }

    /// <summary>
    /// Nearest note of the standard tuning (12-tone, A4 = 440 Hz) with the deviation in cents:
    /// «A4», «C#3 +12c». Used by the hover read-out on every axis kind.
    /// </summary>
    /// <param name="hz">Frequency in Hz.</param>
    /// <returns>The label, or an empty string for a non-positive or NaN frequency.</returns>
    public static string NoteName(double hz)
    {
        if (!(hz > 0)) return "";
        // Fractional MIDI note; octave numbering puts MIDI 60 in octave 4 (C4), and the
        // double modulo keeps the pitch class positive for notes below MIDI 0.
        double midi = 69 + 12 * Math.Log2(hz / 440.0);
        int n = (int)Math.Round(midi);
        int cents = (int)Math.Round((midi - n) * 100);
        int oct = (int)Math.Floor(n / 12.0) - 1;
        string name = SharpNames[((n % 12) + 12) % 12] + oct.ToString(CultureInfo.InvariantCulture);
        return cents == 0 ? name : name + (cents > 0 ? " +" : " ") + cents.ToString(CultureInfo.InvariantCulture) + "c";
    }

    /// <summary>One-line description for status lines and the generated Csound code comments.</summary>
    public string Describe()
    {
        var ci = CultureInfo.InvariantCulture;
        string range = $"{FormatHz(LowHz)}–{FormatHz(HighHz)} Hz";
        return Kind switch
        {
            PitchAxisKind.Linear => $"linear {range}, {RowCount} rows",
            PitchAxisKind.Logarithmic => $"logarithmic {range}, {RowCount} rows ({RowCount / Math.Log2(HighHz / LowHz):0.#} per octave)",
            PitchAxisKind.EqualTemperament => $"{StepsPerOctave}-tone equal temperament {range}, degree 0 = {BaseHz.ToString("0.###", ci)} Hz, {RowCount} notes",
            _ => $"Scala «{Scale?.Name}» ({Scale?.NotesPerPeriod} notes per period {Scale?.Period.ToString("0.####", ci)}) {range}, degree 0 = {BaseHz.ToString("0.###", ci)} Hz, {RowCount} notes",
        };
    }

    // ------------------------------------------------------------------------------------------
    //  Factories
    // ------------------------------------------------------------------------------------------

    /// <summary>Checks and orders the frequency limits.</summary>
    /// <param name="low">Requested lower limit (Hz).</param>
    /// <param name="high">Requested upper limit (Hz).</param>
    /// <returns>Both limits clamped to <see cref="MinHz"/>…<see cref="MaxHz"/> and swapped if reversed.</returns>
    /// <exception cref="ArgumentException">A limit is NaN, or the range is narrower than 1 % (ratio &lt; 1.01).</exception>
    static (double Low, double High) Limits(double low, double high)
    {
        if (double.IsNaN(low) || double.IsNaN(high))
            throw new ArgumentException("Frequency limits must be numbers.");
        low = Math.Clamp(low, MinHz, MaxHz);
        high = Math.Clamp(high, MinHz, MaxHz);
        if (high < low) (low, high) = (high, low);
        if (high / low < 1.01)
            throw new ArgumentException("The high frequency must be above the low one.");
        return (low, high);
    }

    /// <summary>Rows evenly spaced in Hz: row r = low + (high − low)·r/(rows − 1).</summary>
    /// <param name="lowHz">Frequency of row 0.</param>
    /// <param name="highHz">Frequency of the top row.</param>
    /// <param name="rows">Number of rows (resolution), <see cref="MinRows"/>…<see cref="MaxRows"/>.</param>
    public static PitchAxis Linear(double lowHz, double highHz, int rows)
    {
        var (low, high) = Limits(lowHz, highHz);
        rows = Math.Clamp(rows, MinRows, MaxRows);
        var f = new double[rows];
        for (int r = 0; r < rows; r++)
            f[r] = low + (high - low) * r / (rows - 1);
        return new PitchAxis(PitchAxisKind.Linear, low, high, 0, DefaultBaseHz, null, f, null);
    }

    /// <summary>Rows evenly spaced in octaves: row r = low·(high/low)^(r/(rows − 1)).</summary>
    /// <param name="lowHz">Frequency of row 0.</param>
    /// <param name="highHz">Frequency of the top row.</param>
    /// <param name="rows">Number of rows (resolution), <see cref="MinRows"/>…<see cref="MaxRows"/>.</param>
    public static PitchAxis Logarithmic(double lowHz, double highHz, int rows)
    {
        var (low, high) = Limits(lowHz, highHz);
        rows = Math.Clamp(rows, MinRows, MaxRows);
        var f = new double[rows];
        double ratio = high / low;
        for (int r = 0; r < rows; r++)
            f[r] = low * Math.Pow(ratio, (double)r / (rows - 1));
        return new PitchAxis(PitchAxisKind.Logarithmic, low, high, 0, DefaultBaseHz, null, f, null);
    }

    /// <summary>
    /// One row per note of an equal-tempered scale: note n has frequency base·2^(n/steps); every
    /// note inside [low, high] gets a row.
    /// </summary>
    /// <param name="lowHz">Lowest frequency to include.</param>
    /// <param name="highHz">Highest frequency to include.</param>
    /// <param name="stepsPerOctave">Divisions of the octave (1…200; 12 = semitones).</param>
    /// <param name="baseHz">Frequency of degree 0 in octave 4 (default: C4 = 261.63 Hz).</param>
    /// <exception cref="ArgumentException">The range holds fewer than 2 or more than <see cref="MaxRows"/> notes.</exception>
    public static PitchAxis EqualTemperament(double lowHz, double highHz, int stepsPerOctave = 12, double baseHz = DefaultBaseHz)
    {
        var (low, high) = Limits(lowHz, highHz);
        stepsPerOctave = Math.Clamp(stepsPerOctave, 1, 200);
        if (!(baseHz > 0)) baseHz = DefaultBaseHz;
        // Note indices relative to the base: n from ceil(steps·log2(low/base)) to floor(steps·log2(high/base)).
        // A tiny epsilon keeps a limit that is exactly a note (e.g. 27.5 Hz = A0) inside the range.
        int n0 = (int)Math.Ceiling(stepsPerOctave * Math.Log2(low / baseHz) - 1e-9);
        int n1 = (int)Math.Floor(stepsPerOctave * Math.Log2(high / baseHz) + 1e-9);
        int count = n1 - n0 + 1;
        if (count < MinRows || count > MaxRows)
            throw new ArgumentException($"That range holds {count} notes; it must be {MinRows}…{MaxRows}.");
        var f = new double[count];
        var labels = new (int, int)[count];
        for (int i = 0; i < count; i++)
        {
            int n = n0 + i;
            f[i] = baseHz * Math.Pow(2, (double)n / stepsPerOctave);
            // Floor division (not truncation) so negative n map to lower octaves with degree 0…steps−1.
            int octave = (int)Math.Floor((double)n / stepsPerOctave);
            labels[i]= (4 + octave, n - octave * stepsPerOctave);
        }
        return new PitchAxis(PitchAxisKind.EqualTemperament, low, high, stepsPerOctave, baseHz, null, f, labels);
    }

    /// <summary>
    /// One row per note of a Scala tuning: note (k, j) = base · period^k · ratio_j for every period
    /// k and degree j whose frequency falls inside [low, high].
    /// </summary>
    /// <param name="scale">The tuning.</param>
    /// <param name="lowHz">Lowest frequency to include.</param>
    /// <param name="highHz">Highest frequency to include.</param>
    /// <param name="baseHz">Frequency of degree 0 in «octave» (period) 4.</param>
    /// <exception cref="ArgumentException">The range holds fewer than 2 or more than <see cref="MaxRows"/> notes.</exception>
    public static PitchAxis FromScala(ScalaScale scale, double lowHz, double highHz, double baseHz = DefaultBaseHz)
    {
        var (low, high) = Limits(lowHz, highHz);
        if (!(baseHz > 0)) baseHz = DefaultBaseHz;
        double[] degrees = scale.DegreeRatios();
        double period = scale.Period;
        // Periods k such that base·period^k·[1, period) can intersect [low, high].
        // One extra period on each side is a cheap safety margin; the range test below discards
        // the excess. Rows come out ascending because the degree ratios are ascending and below the period.
        int k0 = (int)Math.Floor(Math.Log(low / baseHz) / Math.Log(period)) - 1;
        int k1 = (int)Math.Ceiling(Math.Log(high / baseHz) / Math.Log(period)) + 1;
        var f = new List<double>();
        var labels = new List<(int, int)>();
        for (int k = k0; k <= k1; k++)
            for (int j = 0; j < degrees.Length; j++)
            {
                double hz = baseHz * Math.Pow(period, k) * degrees[j];
                if (hz >= low * (1 - 1e-9) && hz <= high * (1 + 1e-9))
                {
                    f.Add(hz);
                    labels.Add((4 + k, j));
                }
            }
        if (f.Count < MinRows || f.Count > MaxRows)
            throw new ArgumentException($"That range holds {f.Count} notes of the scale; it must be {MinRows}…{MaxRows}.");
        return new PitchAxis(PitchAxisKind.Scala, low, high, 0, baseHz, scale, [.. f], [.. labels]);
    }

    /// <summary>The same kind of axis with another number of rows (continuous axes only; discrete ones are returned unchanged).</summary>
    /// <param name="rows">New number of rows (clamped to <see cref="MinRows"/>…<see cref="MaxRows"/>).</param>
    public PitchAxis WithRows(int rows) => Kind switch
    {
        PitchAxisKind.Linear => Linear(LowHz, HighHz, rows),
        PitchAxisKind.Logarithmic => Logarithmic(LowHz, HighHz, rows),
        _ => this,
    };

    /// <inheritdoc/>
    /// <remarks>Two axes are equal when they have the same kind and exactly the same row frequencies (labels and limits follow from those).</remarks>
    public bool Equals(PitchAxis? other) =>
        other is not null && other.Kind == Kind && other.frequencies.AsSpan().SequenceEqual(frequencies);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as PitchAxis);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Kind, RowCount, frequencies[0], frequencies[^1]);

    /// <inheritdoc/>
    public override string ToString() => Describe();
}
