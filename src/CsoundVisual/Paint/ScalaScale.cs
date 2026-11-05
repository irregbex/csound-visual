// ScalaScale.cs — Paint Rooms foundation.
//
// Role: parses tuning files in the standard Scala format (.scl, https://www.huygens-fokker.org/scala/scl_format.html)
// so a Paint Rooms canvas can use any historical, just or microtonal tuning as its pitch axis.
// A parsed scale is a list of pitch ratios relative to the scale's first degree («1/1», implicit
// in the file) plus the «period» (the last ratio of the file, usually 2/1 = one octave) at which
// the pattern repeats. PitchAxis turns that pattern into one canvas row per playable note.
//
// Pure logic: no UI, no Csound, no threads. Unit-tested by the console harness.

using System.Globalization;

namespace CsoundVisual.Paint;

/// <summary>
/// A tuning read from a Scala <c>.scl</c> file: the ratios of one period (for example one octave)
/// relative to degree 0.
/// </summary>
/// <remarks>
/// <para><b>The .scl format</b> (all lines starting with <c>!</c> are comments):</para>
/// <list type="number">
/// <item>the first non-comment line is a free-text description (may be empty);</item>
/// <item>the second is the number of notes <i>N</i> that follow;</item>
/// <item>then <i>N</i> pitch lines. A value containing a dot is in <b>cents</b> (<c>701.955</c>);
/// otherwise it is a <b>ratio</b> (<c>3/2</c>) or a whole number (<c>2</c> = <c>2/1</c>). Anything
/// after the value on the same line is ignored (it is often a note name or a comment).</item>
/// </list>
/// <para>Degree 0 (<c>1/1</c>) is never written in the file; the last pitch line is the period
/// (normally <c>2/1</c>, but tritave scales such as Bohlen–Pierce use <c>3/1</c>).</para>
/// </remarks>
public sealed class ScalaScale
{
    /// <summary>Creates a scale from already-parsed values (used by the parser and by tests).</summary>
    /// <param name="name">Display name (usually the file name without extension).</param>
    /// <param name="description">Description line of the file.</param>
    /// <param name="ratios">
    /// Frequency ratios of degrees 1..N relative to degree 0; the last entry is the period.
    /// Must contain at least one value, all &gt; 0.
    /// </param>
    public ScalaScale(string name, string description, IReadOnlyList<double> ratios)
    {
        if (ratios.Count == 0)
            throw new FormatException("A Scala scale needs at least one pitch (the period).");
        if (ratios.Any(r => !(r > 0) || double.IsInfinity(r)))
            throw new FormatException("Scala pitches must be positive ratios.");
        if (ratios[^1] <= 1.0000001)
            throw new FormatException("The period of a Scala scale (its last pitch) must be greater than 1/1.");
        Name = name;
        Description = description;
        Ratios = ratios.ToArray();
    }

    /// <summary>Display name (file name without extension, or a built-in name).</summary>
    public string Name { get; }

    /// <summary>The description line of the file (may be empty).</summary>
    public string Description { get; }

    /// <summary>
    /// Ratios of degrees 1..N relative to degree 0, as written in the file (not necessarily sorted;
    /// <see cref="DegreeRatios"/> gives the sorted pattern actually used). The last one is the period.
    /// </summary>
    public IReadOnlyList<double> Ratios { get; }

    /// <summary>Number of notes per period (N in the file; degree 0 plus N−1 inner notes).</summary>
    public int NotesPerPeriod => Ratios.Count;

    /// <summary>The ratio at which the pattern repeats (2.0 for octave-based scales).</summary>
    public double Period => Ratios[^1];

    /// <summary>
    /// The notes of one period as ratios ≥ 1 and &lt; <see cref="Period"/>, sorted ascending:
    /// <c>[1, r1, r2, …]</c>. Ratios outside the period found in odd files are folded into it, and
    /// duplicates are dropped, so every row of the canvas has a distinct pitch.
    /// </summary>
    public double[] DegreeRatios()
    {
        var list = new List<double> { 1.0 };
        double period = Period;
        foreach (double r0 in Ratios.Take(Ratios.Count - 1))
        {
            // Fold into [1, period): some files list a pitch below 1/1 or above the period.
            double r = r0;
            while (r < 1) r *= period;
            while (r >= period) r /= period;
            list.Add(r);
        }
        list.Sort();
        // Remove (near-)duplicates: 0.01 cent apart is the same note for our purposes.
        var unique = new List<double>();
        foreach (double r in list)
            if (unique.Count == 0 || 1200 * Math.Log2(r / unique[^1]) > 0.01)
                unique.Add(r);
        return unique.ToArray();
    }

    /// <summary>Cents of a ratio (1200 cents = one octave).</summary>
    /// <param name="ratio">Frequency ratio (&gt; 0).</param>
    public static double ToCents(double ratio) =>1200.0 * Math.Log2(ratio);

    /// <summary>Reads and parses a .scl file.</summary>
    /// <param name="path">Path of the .scl file; its name without extension becomes <see cref="Name"/>.</param>
    /// <exception cref="FormatException">The file is not a valid Scala scale (the message says why).</exception>
    public static ScalaScale Load(string path) =>
        Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

    /// <summary>Parses the text of a .scl file.</summary>
    /// <param name="text">Whole file content.</param>
    /// <param name="name">Display name to give the scale.</param>
    /// <exception cref="FormatException">The text is not a valid Scala scale (the message says why).</exception>
    public static ScalaScale Parse(string text, string name)
    {
        // Keep only the meaningful lines: comments start with '!' in the first column; the
        // description line may legitimately be empty, so empty lines are kept until it is read.
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string? description = null;
        int? count = null;
        var ratios = new List<double>();
        int lineNumber = 0;
        foreach (string raw in lines)
        {
            lineNumber++;
            if (raw.StartsWith('!')) continue;
            if (description is null)
            {
                description = raw.Trim();
                continue;
            }
            string line = raw.Trim();
            if (line.Length == 0) continue;
            // Only the first word counts: the rest of a count or pitch line is a free comment.
            string token = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0];
            if (count is null)
            {
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > 4096)
                    throw new FormatException($"Line {lineNumber}: expected the number of notes, found «{token}».");
                count = n;
                continue;
            }
            if (ratios.Count == count) break; // trailing text after the last pitch is ignored
            ratios.Add(ParsePitch(token, lineNumber));
        }
        if (count is null)
            throw new FormatException("The file has no note count line (is it a Scala .scl file?).");
        if (ratios.Count < count)
            throw new FormatException($"The file announces {count} notes but contains {ratios.Count}.");
        return new ScalaScale(name, description ?? "", ratios);
    }

    /// <summary>Parses one pitch token: cents if it contains a dot, otherwise a ratio or an integer.</summary>
    /// <param name="token">First word of a pitch line.</param>
    /// <param name="lineNumber">1-based line number, for the error message.</param>
    /// <returns>The frequency ratio (always &gt; 0).</returns>
    static double ParsePitch(string token, int lineNumber)
    {
        var ci = CultureInfo.InvariantCulture;
        if (token.Contains('.'))
        {
            if (!double.TryParse(token, NumberStyles.Float, ci, out double cents))
                throw new FormatException($"Line {lineNumber}: «{token}» is not a number of cents.");
            return Math.Pow(2, cents / 1200.0);
        }
        int slash = token.IndexOf('/');
        if (slash < 0)
        {
            if (!double.TryParse(token, NumberStyles.Integer, ci, out double whole) || whole <= 0)
                throw new FormatException($"Line {lineNumber}: «{token}» is not a ratio.");
            return whole;
        }
        if (!double.TryParse(token[..slash], NumberStyles.Integer, ci, out double num) ||
            !double.TryParse(token[(slash + 1)..], NumberStyles.Integer, ci, out double den) || num <= 0 || den <= 0)
            throw new FormatException($"Line {lineNumber}: «{token}» is not a valid ratio.");
        return num / den;
    }

    /// <summary>Writes the scale back in .scl format (cents with 5 decimals), e.g. to save a built-in scale.</summary>
    /// <returns>
    /// The .scl text with LF line endings: a comment header with the file name, the description,
    /// the note count and one cents value per line (ratios are not preserved as fractions).
    /// </returns>
    public string ToScl()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("! ").Append(Name).Append(".scl\n!\n").Append(Description).Append('\n');
        sb.Append(' ').Append(Ratios.Count.ToString(CultureInfo.InvariantCulture)).Append("\n!\n");
        foreach (double r in Ratios)
            sb.Append(' ').Append(ToCents(r).ToString("0.00000", CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }
}
