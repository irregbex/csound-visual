using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CsoundVisual.Music;

/// <summary>A text replacement in a document: replace <see cref="Length"/> characters at <see cref="Offset"/> with <see cref="Text"/>.</summary>
/// <param name="Offset">Start of the replaced range.</param>
/// <param name="Length">Length of the replaced range (0 = insertion).</param>
/// <param name="Text">New text.</param>
public readonly record struct TextEdit(int Offset, int Length, string Text)
{
    /// <summary>Applies the edit to <paramref name="source"/> (tests; the app applies it to the editor document so it can be undone there).</summary>
    public string ApplyTo(string source) => source[..Offset] + Text + source[(Offset + Length)..];
}

/// <summary>
/// Reading and writing the tools' marked blocks in a CSD's <c>&lt;CsScore&gt;</c>, and parsing
/// <c>i</c> statements back into notes.
/// </summary>
/// <remarks>
/// <para><b>Format.</b> Each tool owns one block delimited by marker comments; exporting again
/// replaces it, nothing else in the score is touched:</para>
/// <code>
/// ; --- piano roll begin ---
/// ; Csound Visual · piano roll (Tools › Piano roll). Re-exporting replaces this block.
/// ;@roll tempo=120 instr="Voice" pitch=midi vel=unit snap=0.25 loop=0:16:1
/// ; times in seconds at 120 BPM (1 beat = 0.5 s) · p4 = MIDI note · p5 = velocity 0-1
/// i "Voice" 0     0.5   60 0.7874
/// ; --- piano roll end ---
/// </code>
/// <para><b>Seconds, not a <c>t</c> statement.</b> p2/p3 are written in seconds (beats × 60 / BPM).
/// A <c>t</c> statement would also re-time everything else in the score (the demo's
/// <c>i "Reverb" 0 3600</c> would last 3600 beats) and Csound allows only one per section, so
/// converting is the only choice that never changes the rest of the file. The tempo is kept in
/// the <c>;@</c> header line, so importing converts back to beats exactly (positions are rounded
/// to 1/960 beat).</para>
/// <para>The <c>;@</c> header lines are plain comments for Csound; they also store the settings
/// (and, for the step sequencer, the whole pattern, one <c>;@track</c> line per row) so that
/// opening the tool on that document restores what was edited.</para>
/// </remarks>
public static class ScoreBlocks
{
    /// <summary>Block name of the piano roll.</summary>
    public const string PianoRoll = "piano roll";

    /// <summary>Block name of the step sequencer.</summary>
    public const string StepSequencer = "step sequencer";

    /// <summary>First line of a block.</summary>
    public static string BeginMarker(string kind) => $"; --- {kind} begin ---";

    /// <summary>Last line of a block.</summary>
    public static string EndMarker(string kind) => $"; --- {kind} end ---";

    /// <summary>Opening <c>&lt;CsScore&gt;</c> tag (attributes such as <c>bin="…"</c> allowed), case-insensitive.</summary>
    static readonly Regex ScoreOpen = new(@"<CsScore\b[^>]*>", RegexOptions.IgnoreCase);
    /// <summary>Closing <c>&lt;/CsScore&gt;</c> tag, case-insensitive.</summary>
    static readonly Regex ScoreClose = new(@"</CsScore\s*>", RegexOptions.IgnoreCase);

    /// <summary>Range of the text between <c>&lt;CsScore&gt;</c> and <c>&lt;/CsScore&gt;</c>, or <c>null</c>.</summary>
    public static (int Start, int End)? ScoreRange(string csd)
    {
        var open = ScoreOpen.Match(csd);
        if (!open.Success) return null;
        var close = ScoreClose.Match(csd, open.Index + open.Length);
        if (!close.Success) return null;
        return (open.Index + open.Length, close.Index);
    }

    /// <summary>
    /// Range of a whole block (from the start of its begin line to after its end line's newline)
    /// inside the score, or <c>null</c>.
    /// </summary>
    public static (int Start, int End)? BlockRange(string csd, string kind)
    {
        if (ScoreRange(csd) is not { } score) return null;
        int b = csd.IndexOf(BeginMarker(kind), score.Start, score.End - score.Start, StringComparison.Ordinal);
        if (b < 0) return null;
        int e = csd.IndexOf(EndMarker(kind), b, score.End - b, StringComparison.Ordinal);
        if (e < 0) return null;
        e += EndMarker(kind).Length;
        if (e < csd.Length && csd[e] == '\r') e++;
        if (e < csd.Length && csd[e] == '\n') e++;
        return (b, e);
    }

    /// <summary>Text of a block (markers included), or <c>null</c> when the document has none.</summary>
    public static string? ReadBlock(string csd, string kind) =>
        BlockRange(csd, kind) is { } r ? csd[r.Start..r.End] : null;

    /// <summary>
    /// The edit that puts <paramref name="block"/> (complete, markers included, ending with a
    /// newline) into the document: replacing the previous block of that kind, else appended at
    /// the end of <c>&lt;CsScore&gt;</c>; a document without a score gets one before
    /// <c>&lt;/CsoundSynthesizer&gt;</c> (or at the very end). Returns <c>null</c> for a text that
    /// is not a CSD at all.
    /// </summary>
    /// <param name="csd">Current document text.</param>
    /// <param name="kind"><see cref="PianoRoll"/> or <see cref="StepSequencer"/>.</param>
    /// <param name="block">Block built by <see cref="BuildPianoRollBlock"/> or <see cref="BuildSequencerBlock"/> ('\n' line ends).</param>
    public static TextEdit? MakeInsertEdit(string csd, string kind, string block)
    {
        // Keep the document's own line-ending style (blocks are built with '\n').
        string nl = csd.Contains("\r\n") ? "\r\n" : "\n";
        block = block.Replace("\r\n", "\n").Replace("\n", nl);
        if (BlockRange(csd, kind) is { } existing)
            return new TextEdit(existing.Start, existing.End - existing.Start, block);
        if (ScoreRange(csd) is { } score)
        {
            // Insert before </CsScore>, on a line of its own.
            int at = score.End;
            string before = at > 0 && csd[at - 1] != '\n' ? nl : "";
            return new TextEdit(at, 0, before + block);
        }
        int synth = csd.IndexOf("</CsoundSynthesizer>", StringComparison.OrdinalIgnoreCase);
        if (synth < 0)
            return null;
        string pre = synth > 0 && csd[synth - 1] != '\n' ? nl : "";
        return new TextEdit(synth, 0, pre + "<CsScore>" + nl + block + "</CsScore>" + nl);
    }

    /// <summary>The edit that removes a block, or <c>null</c> when there is none.</summary>
    public static TextEdit? MakeRemoveEdit(string csd, string kind) =>
        BlockRange(csd, kind) is { } r ? new TextEdit(r.Start, r.End - r.Start, "") : null;

    // ------------------------------------------------------------------------------------------
    //  Header lines «;@name key=value key="quoted value" …»
    // ------------------------------------------------------------------------------------------

    /// <summary><c>key=value</c> or <c>key="value with spaces"</c> (no escapes inside quotes).</summary>
    static readonly Regex KeyValue = new(@"(\w+)=(""(?:[^""]*)""|\S*)", RegexOptions.CultureInvariant);

    /// <summary>Parses the key=value pairs of a <c>;@</c> header line (quotes removed).</summary>
    /// <param name="line">Whole header line, e.g. <c>;@track name="Closed hat" instr=3 …</c>.</param>
    /// <returns>Keys are case-insensitive; a repeated key keeps its last value.</returns>
    public static Dictionary<string, string> ParseHeader(string line)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in KeyValue.Matches(line))
            d[m.Groups[1].Value] = m.Groups[2].Value.Trim('"');
        return d;
    }

    /// <summary>Quotes a header value when it contains spaces (quotes inside are dropped).</summary>
    static string Q(string value)
    {
        value = value.Replace("\"", "");
        return value.Length == 0 || value.Any(char.IsWhiteSpace) ? "\"" + value + "\"" : value;
    }

    /// <summary>Header value <paramref name="key"/> as an invariant-culture double, or <paramref name="fallback"/> when missing/invalid.</summary>
    static double D(Dictionary<string, string> h, string key, double fallback) =>
        h.TryGetValue(key, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;

    /// <summary>Header value <paramref name="key"/> as an invariant-culture integer, or <paramref name="fallback"/> when missing/invalid.</summary>
    static int I(Dictionary<string, string> h, string key, int fallback) =>
        h.TryGetValue(key, out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    /// <summary>Lines of a text without line terminators.</summary>
    static IEnumerable<string> Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    // ------------------------------------------------------------------------------------------
    //  i statements
    // ------------------------------------------------------------------------------------------

    /// <summary>A parsed <c>i</c> statement (numeric fields only).</summary>
    /// <param name="Instrument">p1 without quotes.</param>
    /// <param name="Fields">p2, p3, p4… as numbers.</param>
    public sealed record ScoreLine(string Instrument, double[] Fields);

    /// <summary>
    /// Parses one score line if it is an <c>i</c> statement whose p2… fields are plain numbers
    /// (carry <c>.</c>, ramps <c>&lt; &gt;</c>, <c>+</c>, expressions <c>[…]</c> and macros are not
    /// understood and make the line be skipped). Comments are removed first.
    /// </summary>
    /// <param name="line">One score line (leading whitespace allowed).</param>
    /// <returns>The statement, or <c>null</c> when the line is not a readable <c>i</c> statement or has fewer than two fields (p2, p3).</returns>
    public static ScoreLine? ParseILine(string line)
    {
        // Strip ';' and '//' comments, but only outside double quotes. Bug fix: the line used to
        // be cut at the first ';' or '//' anywhere, so a quoted instrument name or string p-field
        // containing one (i "a;b" …, i 1 0 1 "http://…") lost the rest of the statement.
        bool quoted = false;
        for (int c = 0; c < line.Length; c++)
        {
            if (line[c] == '"') quoted = !quoted;
            else if (!quoted && (line[c] == ';' || (line[c] == '/' && c + 1 < line.Length && line[c + 1] == '/')))
            {
                line = line[..c];
                break;
            }
        }
        line = line.Trim();
        if (line.Length < 2 || line[0] != 'i') return null;
        // "i 1 …", "i1 …" and "i "name" …" are statements; "if", "init"… are not.
        if (!(char.IsWhiteSpace(line[1]) || line[1] == '"' || char.IsDigit(line[1]))) return null;
        string rest = line[1..].TrimStart();
        if (rest.Length == 0) return null;
        string instr;
        if (rest[0] == '"')
        {
            int close = rest.IndexOf('"', 1);
            if (close < 0) return null;
            instr = rest[1..close];
            rest = rest[(close + 1)..];
        }
        else
        {
            int sp = 0;
            while (sp < rest.Length && !char.IsWhiteSpace(rest[sp])) sp++;
            instr = rest[..sp];
            rest = rest[sp..];
        }
        var parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var fields = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out fields[i]))
                return null;
        return fields.Length >= 2 ? new ScoreLine(instr, fields) : null;
    }

    /// <summary>
    /// True when a score's p1 refers to <paramref name="instrument"/>: same name, or the same
    /// number (fractional instances like <c>1.2</c> count as instrument 1).
    /// </summary>
    public static bool SameInstrument(string p1, string instrument)
    {
        p1 = p1.Trim('"');
        instrument = instrument.Trim('"');
        if (string.Equals(p1, instrument, StringComparison.Ordinal)) return true;
        return double.TryParse(p1, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) &&
               double.TryParse(instrument, NumberStyles.Float, CultureInfo.InvariantCulture, out double b) &&
               Math.Floor(a) == Math.Floor(b);
    }

    /// <summary>Rounds a beat position to the import resolution (1/960 beat).</summary>
    public static double RoundTick(double beat) => Math.Round(beat / PianoRollModel.Tick) * PianoRollModel.Tick;

    // ------------------------------------------------------------------------------------------
    //  Piano roll
    // ------------------------------------------------------------------------------------------

    /// <summary>Builds the piano roll block (markers included) for the model's notes and settings.</summary>
    /// <returns>The block text with '\n' line ends; <see cref="MakeInsertEdit"/> adapts them to the document.</returns>
    public static string BuildPianoRollBlock(PianoRollModel model)
    {
        var p = model.Snapshot();
        double spb = 60.0 / model.Bpm; // seconds per beat: beats × spb = p2/p3 in seconds
        var sb = new StringBuilder();
        sb.Append(BeginMarker(PianoRoll)).Append('\n');
        sb.Append("; Csound Visual · piano roll (Tools › Piano roll). Re-exporting replaces this block.\n");
        sb.Append(";@roll tempo=").Append(PFields.Num(model.Bpm, 4))
          .Append(" instr=").Append(Q(model.Instrument))
          .Append(" pitch=").Append(PFields.Code(model.PitchMapping))
          .Append(" vel=").Append(PFields.Code(model.VelocityMapping))
          .Append(" snap=").Append(PFields.Num(model.Snap, 6))
          .Append(" loop=").Append(PFields.Num(model.LoopStart, 6)).Append(':').Append(PFields.Num(model.LoopEnd, 6)).Append(':').Append(model.LoopOn ? '1' : '0')
          .Append('\n');
        sb.Append("; times in seconds at ").Append(PFields.Num(model.Bpm, 4)).Append(" BPM (1 beat = ").Append(PFields.Num(spb, 6))
          .Append(" s) · p4 = ").Append(PitchLabel(model.PitchMapping)).Append(" · p5 = ").Append(VelocityLabel(model.VelocityMapping)).Append('\n');
        foreach (var n in p.Notes)
            sb.Append(FormatI(p.InstrumentToken, n.Start * spb, n.Length * spb, p.FieldsOf(n)));
        sb.Append(EndMarker(PianoRoll)).Append('\n');
        return sb.ToString();
    }

    // Note: the i lines of the piano roll block cover the whole roll, not just the loop region;
    // the loop only affects live playback.

    /// <summary>One aligned <c>i</c> line (p2/p3 with 6 decimals at most) ending with a newline.</summary>
    public static string FormatI(string instrumentToken, double startSeconds, double durationSeconds, string fields) =>
        $"i {instrumentToken} {PFields.Num(startSeconds),-10} {PFields.Num(durationSeconds),-10} {fields}\n";

    /// <summary>What p4 means, for the comment line.</summary>
    public static string PitchLabel(PitchMapping m) => m switch
    {
        PitchMapping.Hz => "frequency in Hz",
        PitchMapping.Cpspch => "pitch-class (cpspch)",
        PitchMapping.Oct => "octave-point-decimal (cpsoct)",
        _ => "MIDI note",
    };

    /// <summary>What p5 means, for the comment line.</summary>
    public static string VelocityLabel(VelocityMapping m) => m == VelocityMapping.Midi127 ? "velocity 0-127" : "velocity 0-1";

    /// <summary>Result of reading notes back from a score.</summary>
    /// <param name="Notes">The notes found.</param>
    /// <param name="FromBlock">True when they came from the tool's own block (settings restored too).</param>
    /// <param name="Skipped">i statements of the instrument that could not be read (carry, expressions, held notes…).</param>
    public sealed record RollImport(List<RollNote> Notes, bool FromBlock, int Skipped);

    /// <summary>
    /// Reads piano roll notes from a CSD. When the document has a piano roll block, its header
    /// restores the settings into <paramref name="model"/> (tempo, instrument, mapping, snap, loop)
    /// and the block's <c>i</c> lines become the notes. Otherwise every <c>i</c> statement of
    /// <c>model.Instrument</c> in the score is read with the model's current tempo and mapping.
    /// The notes are returned, not applied (the caller decides, so the import can be undone).
    /// </summary>
    public static RollImport ImportPianoRoll(string csd, PianoRollModel model)
    {
        string? block = ReadBlock(csd, PianoRoll);
        bool fromBlock = block is not null;
        string text;
        if (block is not null)
        {
            text = block;
            if (Lines(block).FirstOrDefault(l => l.StartsWith(";@roll", StringComparison.Ordinal)) is { } header)
                ApplyRollHeader(ParseHeader(header), model);
        }
        else if (ScoreRange(csd) is { } score)
            text = csd[score.Start..score.End];
        else
            return new RollImport([], false, 0);

        // Beats per second at the (possibly just restored) tempo: seconds × bps = beats.
        double bps = model.Bpm / 60.0;
        var notes = new List<RollNote>();
        int skipped = 0;
        foreach (var raw in Lines(text))
        {
            string trimmed = raw.TrimStart();
            if (!trimmed.StartsWith('i')) continue;
            var line = ParseILine(trimmed);
            if (line is null)
            {
                // Count only lines that look like statements for our instrument.
                if (trimmed.Contains(PFields.InstrumentToken(model.Instrument), StringComparison.Ordinal)) skipped++;
                continue;
            }
            if (!fromBlock && !SameInstrument(line.Instrument, model.Instrument)) continue;
            var f = line.Fields; // f[0] = p2, f[1] = p3, f[2] = p4, f[3] = p5
            if (f.Length < 3 || f[1] <= 0) { skipped++; continue; }
            if (PFields.ParsePitch(f[2], model.PitchMapping) is not int pitch) { skipped++; continue; }
            int vel = f.Length >= 4 ? PFields.ParseVelocity(f[3], model.VelocityMapping) : 100;
            notes.Add(new RollNote
            {
                Start = RoundTick(f[0] * bps),
                Length = Math.Max(PianoRollModel.Tick, RoundTick(f[1] * bps)),
                Pitch = pitch,
                Velocity = vel,
            });
        }
        return new RollImport(notes, fromBlock, skipped);
    }

    /// <summary>Copies the settings of a <c>;@roll</c> header into the model.</summary>
    static void ApplyRollHeader(Dictionary<string, string> h, PianoRollModel model)
    {
        model.Bpm = Math.Clamp(D(h, "tempo", model.Bpm), 20, 400);
        if (h.TryGetValue("instr", out var instr) && instr.Length > 0) model.Instrument = instr;
        if (h.TryGetValue("pitch", out var pm)) model.PitchMapping = PFields.ParsePitchCode(pm);
        if (h.TryGetValue("vel", out var vm)) model.VelocityMapping = PFields.ParseVelocityCode(vm);
        model.Snap = Math.Max(0, D(h, "snap", model.Snap));
        if (h.TryGetValue("loop", out var loop))
        {
            var parts = loop.Split(':');
            if (parts.Length == 3 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double a) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && b > a)
            {
                model.LoopStart = a;
                model.LoopEnd = b;
                model.LoopOn = parts[2] == "1";
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    //  Step sequencer
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds the step sequencer block: a <c>;@seq</c> header, one <c>;@track</c> line per row
    /// (the whole pattern, so it can be restored) and the <c>i</c> lines of
    /// <see cref="StepPattern.ExportLoops"/> passes, swing and mute/solo applied. Probability is
    /// rolled once per step and pass with a fixed seed, so the same pattern always exports the same.
    /// </summary>
    public static string BuildSequencerBlock(StepPattern pattern)
    {
        double spb = 60.0 / pattern.Bpm;
        var sb = new StringBuilder();
        sb.Append(BeginMarker(StepSequencer)).Append('\n');
        sb.Append("; Csound Visual · step sequencer (Tools › Step sequencer). Re-exporting replaces this block.\n");
        sb.Append(";@seq tempo=").Append(PFields.Num(pattern.Bpm, 4))
          .Append(" steps=").Append(pattern.StepCount.ToString(CultureInfo.InvariantCulture))
          .Append(" swing=").Append(PFields.Num(pattern.Swing, 4))
          .Append(" loops=").Append(pattern.ExportLoops.ToString(CultureInfo.InvariantCulture))
          .Append(" pitch=").Append(PFields.Code(pattern.PitchMapping))
          .Append(" vel=").Append(PFields.Code(pattern.VelocityMapping)).Append('\n');
        foreach (var t in pattern.Tracks)
        {
            sb.Append(";@track name=").Append(Q(t.Name))
              .Append(" instr=").Append(Q(t.Instrument))
              .Append(" note=").Append(t.Note.ToString(CultureInfo.InvariantCulture))
              .Append(" vel=").Append(t.Velocity.ToString(CultureInfo.InvariantCulture))
              .Append(" mute=").Append(t.Mute ? '1' : '0')
              .Append(" solo=").Append(t.Solo ? '1' : '0')
              .Append(" prob=").Append(t.Probability.ToString(CultureInfo.InvariantCulture))
              .Append(" steps=").Append(string.Join(',', t.Steps.Take(pattern.StepCount).Select(v => v.ToString(CultureInfo.InvariantCulture))))
              .Append('\n');
        }
        sb.Append("; ").Append(pattern.ExportLoops.ToString(CultureInfo.InvariantCulture)).Append(" × ").Append(pattern.StepCount.ToString(CultureInfo.InvariantCulture))
          .Append(" steps of 1/16 · times in seconds at ").Append(PFields.Num(pattern.Bpm, 4)).Append(" BPM · p4 = ")
          .Append(PitchLabel(pattern.PitchMapping)).Append(" · p5 = ").Append(VelocityLabel(pattern.VelocityMapping)).Append('\n');
        var rng = new Random(12345);
        var events = new List<PatternEvent>();
        for (int pass = 0; pass < Math.Max(1, pattern.ExportLoops); pass++)
        {
            events.Clear();
            pattern.Collect(0, pattern.LengthBeats, events, rng);
            double passStart = pass * pattern.LengthBeats;
            foreach (var e in events.OrderBy(e => e.Beat).ThenBy(e => e.Tag))
                sb.Append(FormatI(e.Instrument, (passStart + e.Beat) * spb, e.LengthBeats * spb, e.Fields));
        }
        sb.Append(EndMarker(StepSequencer)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Restores a pattern from the document's step sequencer block, or <c>null</c> when there is none (or it has no tracks).</summary>
    /// <remarks>
    /// Only the <c>;@seq</c>/<c>;@track</c> header lines are read; the <c>i</c> lines are derived
    /// data and ignored. Every value is clamped to its valid range, so a hand-edited header
    /// cannot produce an invalid pattern.
    /// </remarks>
    public static StepPattern? ImportSequencer(string csd)
    {
        if (ReadBlock(csd, StepSequencer) is not { } block) return null;
        var p = new StepPattern();
        foreach (var line in Lines(block))
        {
            if (line.StartsWith(";@seq", StringComparison.Ordinal))
            {
                var h = ParseHeader(line);
                p.Bpm = Math.Clamp(D(h, "tempo", p.Bpm), 20, 400);
                int steps = I(h, "steps", 16);
                p.StepCount = steps is 8 or 16 or 32 ? steps : 16;
                p.Swing = Math.Clamp(D(h, "swing", 0), 0, 0.75);
                p.ExportLoops = Math.Clamp(I(h, "loops", 4), 1, 999);
                if (h.TryGetValue("pitch", out var pm)) p.PitchMapping = PFields.ParsePitchCode(pm);
                if (h.TryGetValue("vel", out var vm)) p.VelocityMapping = PFields.ParseVelocityCode(vm);
            }
            else if (line.StartsWith(";@track", StringComparison.Ordinal))
            {
                var h = ParseHeader(line);
                var t = new StepTrack
                {
                    Name = h.GetValueOrDefault("name", ""),
                    Instrument = h.GetValueOrDefault("instr", "1"),
                    Note = Math.Clamp(I(h, "note", 60), 0, 127),
                    Velocity = Math.Clamp(I(h, "vel", 100), 1, 127),
                    Mute = h.GetValueOrDefault("mute") == "1",
                    Solo = h.GetValueOrDefault("solo") == "1",
                    Probability = Math.Clamp(I(h, "prob", 100), 0, 100),
                };
                var steps = h.GetValueOrDefault("steps", "").Split(',', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < steps.Length && i < StepTrack.MaxSteps; i++)
                    if (int.TryParse(steps[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                        t.Steps[i] = Math.Clamp(v, 0, 127);
                p.Tracks.Add(t);
            }
        }
        return p.Tracks.Count > 0 ? p : null;
    }
}
