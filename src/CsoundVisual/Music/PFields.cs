using System.Globalization;
using System.Text.RegularExpressions;

namespace CsoundVisual.Music;

/// <summary>How a note's pitch is written into p4 of the generated <c>i</c> statements.</summary>
public enum PitchMapping
{
    /// <summary>MIDI note number, 60 = middle C (the instrument uses <c>cpsmidinn(p4)</c> or <c>mtof:i(p4)</c>).</summary>
    MidiNote,

    /// <summary>Frequency in Hz (A4 = 440, equal temperament); the instrument uses p4 directly.</summary>
    Hz,

    /// <summary>Octave-point-pitch-class, 8.00 = middle C, 8.09 = A4 (the instrument uses <c>cpspch(p4)</c>).</summary>
    Cpspch,

    /// <summary>Csound «oct» (octave-point-decimal), 8.0 = middle C, 8.75 = A4 (the instrument uses <c>cpsoct(p4)</c>).</summary>
    Oct,
}

/// <summary>How a note's velocity is written into p5.</summary>
public enum VelocityMapping
{
    /// <summary>0…1 (velocity / 127), ready to multiply an amplitude (the demo's <c>Voice</c> does <c>asig * p5</c>).</summary>
    Unit,

    /// <summary>0…127, the raw MIDI velocity.</summary>
    Midi127,
}

/// <summary>An instrument of the target document, as offered in the tools' instrument pickers.</summary>
/// <param name="Id">Instrument number or name exactly as declared (<c>1</c>, <c>Voice</c>), without quotes.</param>
/// <param name="Comment">Trailing comment of the <c>instr</c> line (e.g. <c>Kick</c>), or empty.</param>
public sealed record InstrumentChoice(string Id, string Comment)
{
    /// <summary>True for numbered instruments (<c>instr 1</c>).</summary>
    public bool IsNumber => double.TryParse(Id, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>The token written as p1 of a score line: numbers as is, names quoted (<c>"Voice"</c>).</summary>
    public string Token => PFields.InstrumentToken(Id);

    /// <summary>Text shown in the picker: «1 · Kick» or «Voice».</summary>
    public string Display => Comment.Length > 0 ? $"{Id} · {Comment}" : Id;

    /// <inheritdoc/>
    public override string ToString() => Display;
}

/// <summary>
/// Conversions between the tools' musical units (MIDI notes, 1…127 velocities, beats) and the
/// numbers written into Csound score statements, plus small text helpers (note names, instrument
/// tokens). All formatting is culture-invariant (a decimal point, never a comma).
/// </summary>
/// <remarks>
/// Used by <see cref="ScoreBlocks"/> (export/import of <c>i</c> lines), <see cref="TransportScheduler"/>
/// (p4/p5 of live events) and the piano roll / step sequencer views. The mapping codes
/// (<see cref="Code(PitchMapping)"/>) are persisted in score block headers, so they must never change.
/// </remarks>
public static class PFields
{
    /// <summary>Pitch-class names, index = MIDI note mod 12 (sharps only).</summary>
    static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>Note name with octave in scientific pitch notation: 60 = C4, 69 = A4 = 440 Hz.</summary>
    /// <remarks>
    /// This is the Csound manual's convention (<c>cpspch 8.00</c> = C4 = MIDI 60). Cubase shows the
    /// same key as «C3» by default, so the tools always show the MIDI number next to the name.
    /// </remarks>
    public static string NoteName(int midi) => Names[((midi % 12) + 12) % 12] + (midi / 12 - 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>True for the black keys (C#, D#, F#, G#, A#).</summary>
    public static bool IsBlack(int midi) => (((midi % 12) + 12) % 12) is 1 or 3 or 6 or 8 or 10;

    /// <summary>Writes a number with up to <paramref name="decimals"/> decimals, invariant culture, no trailing zeros.</summary>
    /// <param name="value">Number to format.</param>
    /// <param name="decimals">Maximum number of decimals kept (rounded half to even).</param>
    public static string Num(double value, int decimals = 6)
    {
        double r = Math.Round(value, decimals);
        if (r == 0) r = 0; // no "-0"
        return r.ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture);
    }

    /// <summary>p1 token of an instrument id: numbers stay as they are, names are quoted.</summary>
    public static string InstrumentToken(string id)
    {
        id = id.Trim().Trim('"');
        return double.TryParse(id, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? id : "\"" + id + "\"";
    }

    /// <summary>p4 text for MIDI note <paramref name="midi"/> in the chosen mapping.</summary>
    /// <param name="midi">MIDI note number (0…127).</param>
    /// <param name="mapping">Target notation.</param>
    /// <returns>Culture-invariant text ready to paste into a score line.</returns>
    public static string Pitch(int midi, PitchMapping mapping) => mapping switch
    {
        // Equal temperament: f = 440 · 2^((n − 69) / 12).
        // pch: integer part = octave (MIDI 60 → 8), two decimals = semitone 00…11.
        // oct: octave + semitone/12 as a decimal fraction, 3.0 = MIDI 0.
        PitchMapping.Hz => Num(440.0 * Math.Pow(2, (midi - 69) / 12.0), 4),
        PitchMapping.Cpspch => (midi / 12 + 3).ToString(CultureInfo.InvariantCulture) + "." + (midi % 12).ToString("00", CultureInfo.InvariantCulture),
        PitchMapping.Oct => Num(3 + midi / 12.0, 6),
        _ => midi.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Inverse of <see cref="Pitch"/>: the nearest MIDI note for a p4 value, or <c>null</c> when it is out of 0…127.</summary>
    /// <param name="value">p4 as found in a score line.</param>
    /// <param name="mapping">Notation the value is written in.</param>
    /// <remarks>Microtonal values (Hz between semitones, pch decimals beyond .11) round to the nearest note.</remarks>
    public static int? ParsePitch(double value, PitchMapping mapping)
    {
        double n = mapping switch
        {
            PitchMapping.Hz => value > 0 ? 69 + 12 * Math.Log2(value / 440.0) : double.NaN,
            PitchMapping.Cpspch => (Math.Floor(value) - 3) * 12 + Math.Round((value - Math.Floor(value)) * 100),
            PitchMapping.Oct => (value - 3) * 12,
            _ => value,
        };
        if (double.IsNaN(n)) return null;
        int midi = (int)Math.Round(n);
        return midi is >= 0 and <= 127 ? midi : null;
    }

    /// <summary>p5 text for a 1…127 velocity in the chosen mapping.</summary>
    public static string Velocity(int velocity, VelocityMapping mapping) => mapping == VelocityMapping.Midi127
        ? Math.Clamp(velocity, 0, 127).ToString(CultureInfo.InvariantCulture)
        : Num(Math.Clamp(velocity, 0, 127) / 127.0, 4);

    /// <summary>Inverse of <see cref="Velocity"/>: a 1…127 velocity from a p5 value.</summary>
    /// <remarks>Clamped to at least 1 so an imported note is never silent (velocity 0 would be a note-off in MIDI terms).</remarks>
    public static int ParseVelocity(double value, VelocityMapping mapping) =>
        Math.Clamp((int)Math.Round(mapping == VelocityMapping.Midi127 ? value : value * 127), 1, 127);

    /// <summary>Stable short code of a pitch mapping (stored in the score block header).</summary>
    public static string Code(PitchMapping m) => m switch
    {
        PitchMapping.Hz => "hz",
        PitchMapping.Cpspch => "pch",
        PitchMapping.Oct => "oct",
        _ => "midi",
    };

    /// <summary>Stable short code of a velocity mapping (stored in the score block header).</summary>
    public static string Code(VelocityMapping m) => m == VelocityMapping.Midi127 ? "127" : "unit";

    /// <summary>Parses a pitch mapping code (unknown → MIDI note).</summary>
    public static PitchMapping ParsePitchCode(string? code) => code switch
    {
        "hz" => PitchMapping.Hz,
        "pch" => PitchMapping.Cpspch,
        "oct" => PitchMapping.Oct,
        _ => PitchMapping.MidiNote,
    };

    /// <summary>Parses a velocity mapping code (unknown → 0…1).</summary>
    public static VelocityMapping ParseVelocityCode(string? code) => code == "127" ? VelocityMapping.Midi127 : VelocityMapping.Unit;

    /// <summary>
    /// One <c>instr</c> header line: the id list up to an optional <c>;</c> comment (captured as
    /// <c>c</c>). Multiline mode so <c>^</c>/<c>$</c> match at every line.
    /// </summary>
    static readonly Regex InstrLine = new(@"^\s*instr\s+[^;\r\n]*?(?:;\s*(?<c>[^\r\n]*))?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Lists the instruments of a CSD/ORC text from its <c>instr</c> lines, with the trailing comment
    /// of each line as a label (<c>instr 1 ; Kick</c> → «1 · Kick»). An <c>instr 1, 2</c> line yields
    /// two entries. Lines inside block comments are not filtered (rare in practice).
    /// </summary>
    public static List<InstrumentChoice> FindInstruments(string text)
    {
        var list = new List<InstrumentChoice>();
        foreach (Match m in InstrLine.Matches(text))
        {
            string line = m.Value.Trim();
            int semi = line.IndexOf(';');
            string head = (semi >= 0 ? line[..semi] : line).Trim();
            if (head.Length <= 5) continue; // just "instr" with no id
            // head[5..] skips the keyword; '+' marks a named instrument that takes the next free number.
            string comment = m.Groups["c"].Success ? m.Groups["c"].Value.Trim() : "";
            if (comment.Length > 28) comment = comment[..28].TrimEnd() + "…";
            foreach (var id in head[5..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                string clean = id.Trim('"', '+');
                if (clean.Length > 0 && !list.Any(c => c.Id == clean))
                    list.Add(new InstrumentChoice(clean, comment));
            }
        }
        return list;
    }
}
