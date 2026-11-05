using System.Globalization;
using System.Text.RegularExpressions;
using CsoundVisual.Localization;

namespace CsoundVisual.Editor;

/// <summary>What kind of token a <see cref="TokenInsight"/> describes.</summary>
public enum InsightKind
{
    /// <summary>An orchestra variable such as <c>kcut</c> or <c>gaRevL</c>.</summary>
    Variable,
    /// <summary>A numeric literal (orchestra or score).</summary>
    Number,
    /// <summary>A p-field reference such as <c>p4</c>.</summary>
    PField,
    /// <summary>A reserved header constant (<c>sr</c>, <c>ksmps</c>, <c>nchnls</c>, <c>0dbfs</c>…).</summary>
    HeaderConstant,
    /// <summary>A channel name inside a string literal (<c>chnget "cutoff"</c>).</summary>
    ChannelName,
    /// <summary>An opcode name.</summary>
    Opcode,
}

/// <summary>How a token can be changed by dragging it with the mouse.</summary>
public enum ScrubMode
{
    /// <summary>Not draggable.</summary>
    None,
    /// <summary>Dragging changes a control channel in real time (the code text is not modified).</summary>
    Channel,
    /// <summary>Dragging rewrites the numeric literal in the code (and recompiles the instrument live).</summary>
    Literal,
}

/// <summary>Live state of a control channel, supplied by the view model to the analyzer.</summary>
/// <param name="Minimum">Lower bound of the channel's slider.</param>
/// <param name="Maximum">Upper bound of the channel's slider.</param>
/// <param name="Value">Current slider value.</param>
/// <param name="Exponential">True if the channel was declared with an exponential range.</param>
/// <remarks>
/// The analyzer never owns channel state: it calls back into the view model (which holds one
/// <c>ChannelControlViewModel</c> per <c>chnk</c>/<c>chnget</c> channel) so the card always shows
/// the slider's current value. A <c>null</c> answer means "no slider for that name", in which case
/// the token is described but cannot be dragged as a channel.
/// </remarks>
public sealed record ChannelState(double Minimum, double Maximum, double Value, bool Exponential);

/// <summary>An <c>instr … endin</c> or <c>opcode … endop</c> block of the orchestra.</summary>
/// <param name="Keyword"><c>"instr"</c> or <c>"opcode"</c>.</param>
/// <param name="Name">Name or number list as written after the keyword (e.g. <c>Voice</c>, <c>1, 2</c>).</param>
/// <param name="StartLine">1-based line of the <c>instr</c>/<c>opcode</c> keyword.</param>
/// <param name="EndLine">1-based line of the closing <c>endin</c>/<c>endop</c>.</param>
/// <remarks>
/// Line numbers refer to the text snapshot the <see cref="CsoundAnalyzer"/> was built from. Also
/// used by live coding (the block text is what Ctrl+Enter / literal drags recompile) and by
/// <see cref="BreakpointLineMap"/>. For a UDO, <see cref="Name"/> holds only the opcode name (the
/// type lists after it are dropped).
/// </remarks>
public sealed record CodeBlock(string Keyword, string Name, int StartLine, int EndLine)
{
    /// <summary>Text used in the help, e.g. <c>"instr Voice"</c>.</summary>
    public string Label => $"{Keyword} {Name}";

    /// <summary>The instrument identifiers declared by this block (<c>"1, 2"</c> → <c>["1", "2"]</c>).</summary>
    public IEnumerable<string> Ids => Name.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// The result of inspecting the token under the mouse: what to show in the hover card and how
/// the token may be dragged.
/// </summary>
/// <remarks>
/// <see cref="Start"/>, <see cref="Length"/>, <see cref="Text"/> and <see cref="Value"/> are
/// mutable because the scrub controller updates them while a literal is being rewritten.
/// The other settable properties are filled step by step by the analyzer's <c>Describe…</c>
/// helpers while the insight is being built; consumers treat them as read-only.
/// </remarks>
public sealed class TokenInsight
{
    /// <summary>Kind of token.</summary>
    public required InsightKind Kind { get; init; }

    /// <summary>Absolute document offset of the token (for strings: of the content, without quotes).</summary>
    public required int Start { get; set; }

    /// <summary>Length of the token in characters.</summary>
    public required int Length { get; set; }

    /// <summary>Token text as currently in the document.</summary>
    public required string Text { get; set; }

    /// <summary>Card heading (usually the token itself).</summary>
    public required string Title { get; init; }

    /// <summary>One-line classification shown under the title.</summary>
    public string? Subtitle { get; set; }

    /// <summary>Explanatory lines (definition, usage, meaning of the argument…).</summary>
    public List<string> Details { get; } = [];

    /// <summary>Lower bound used while dragging (may exist without a known range, e.g. "not negative").</summary>
    public double? Minimum { get; set; }

    /// <summary>Upper bound used while dragging.</summary>
    public double? Maximum { get; set; }

    /// <summary>Human readable range shown in the card, or <c>null</c> when no meaningful range is known.</summary>
    public string? RangeText { get; set; }

    /// <summary>Current value (literal value, or current channel value).</summary>
    public double? Value { get; set; }

    /// <summary>True when only whole numbers make sense (ksmps, nchnls, chnk modes…).</summary>
    public bool IntegerOnly { get; set; }

    /// <summary>True when dragging should move exponentially (frequency-like ranges with min &gt; 0).</summary>
    public bool Exponential { get; set; }

    /// <summary>Digits after the decimal point of the original literal, kept while rewriting it.</summary>
    public int Decimals { get; set; }

    /// <summary>How the token can be dragged.</summary>
    public ScrubMode Scrub { get; set; }

    /// <summary>
    /// For <see cref="ScrubMode.Channel"/>: the channel being driven. For a literal that is the
    /// default value of a <c>chnk</c> declaration: the channel that should follow the literal live.
    /// </summary>
    public string? Channel { get; set; }

    /// <summary>
    /// Enclosing instrument. For a literal: dragging it recompiles this instrument into the running
    /// engine. For a local variable: the instrument whose live values are shown.
    /// </summary>
    public CodeBlock? Block { get; init; }

    /// <summary>1-based line where a variable is first defined (for "go to definition"), if known.</summary>
    public int? DefinitionLine { get; set; }

    /// <summary>True for global variables (<c>g…</c>); their live values are looked up as globals.</summary>
    public bool IsGlobal { get; init; }

    /// <summary>True if <paramref name="other"/> describes the same token (same place and kind).</summary>
    /// <remarks>Used by the scrub controller to avoid restarting the hover delay while the pointer
    /// moves within one token (each pointer move produces a fresh insight object).</remarks>
    public bool SameToken(TokenInsight? other) =>
        other is not null && other.Start == Start && other.Length == Length && other.Kind == Kind;
}

/// <summary>
/// Lightweight, heuristic static analysis of a CSD/orchestra used by the editor's hover help and
/// drag-to-change ("scrubbing") feature.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately not a full Csound parser. It splits the text into sections
/// (<c>&lt;CsInstruments&gt;</c>, <c>&lt;CsScore&gt;</c>) and instrument blocks, and parses single
/// lines into "statements" (outputs, opcode, arguments) for both classic syntax
/// (<c>kcut chnget "cutoff"</c>) and assignments (<c>ifreq = cpsmidinn(p4)</c>). That is enough to
/// answer "where is this variable defined", "what range can it take" and "which argument of which
/// opcode is this number", which is what the help needs.
/// </para>
/// <para>
/// An analyzer is bound to one snapshot of the text and never changes what it reports; callers
/// cache one per document revision. All offsets are absolute document offsets; all line numbers
/// are 1-based. Internally it lazily caches parsed statements (<c>statements</c>), so an instance
/// is <b>not thread-safe</b>: use it from one thread at a time (in the app, the UI thread).
/// </para>
/// <para>
/// <b>Pipeline.</b> The constructor indexes line starts, locates the <c>&lt;CsInstruments&gt;</c>
/// and <c>&lt;CsScore&gt;</c> sections (a file with neither is treated as bare orchestra code) and
/// finds the <c>instr</c>/<c>opcode</c> blocks. <see cref="Inspect"/> then classifies the token
/// under an offset in this order: comment → string (channel name) → header constant → number →
/// (score lines stop here) → p-field → variable → opcode. Each kind has an <c>Inspect…</c> method
/// that builds a <see cref="TokenInsight"/>; numbers are further explained by their context
/// (score p-field, <c>chnk</c> argument, header assignment, opcode argument) and variables by
/// <see cref="InferRange"/>, which looks at the defining statement.
/// </para>
/// <para>
/// <b>Consumers.</b> The hover card and scrubbing (<see cref="ScrubController"/> through
/// <see cref="IScrubHost.Inspect"/>), live coding (<see cref="Blocks"/>, <see cref="BlockAt"/>,
/// <see cref="BlockText"/>), breakpoints (<see cref="BreakpointRules"/>,
/// <see cref="BreakpointLineMap"/>). It has no UI dependencies, so it can be exercised from a
/// plain console test project.
/// </para>
/// </remarks>
public sealed partial class CsoundAnalyzer
{
    /// <summary>Opcodes that read a control channel named by their first argument.</summary>
    static readonly HashSet<string> ChannelReaders = ["chnget", "chngetk", "chngeti", "chngetks", "chngeta"];

    /// <summary>Opcodes whose string argument is a channel name.</summary>
    static readonly HashSet<string> ChannelOpcodes = ["chnget", "chngetk", "chngeti", "chngetks", "chngeta", "chnset",
        "chnsetk", "chnseti", "chnk", "chn_k", "chn_a", "chn_S", "chnexport", "chnmix", "chnclear"];

    /// <summary>Opcodes that schedule an instrument and take its p-fields as arguments.</summary>
    static readonly HashSet<string> Schedulers = ["schedule", "schedulek", "schedkwhen", "event", "event_i"];

    /// <summary>Help text (Spanish and English) and drag range for reserved header constants.</summary>
    static readonly Dictionary<string, (string HelpEs, string HelpEn, double Min, double Max, bool Integer)> HeaderConstants = new()
    {
        ["sr"] = ("Frecuencia de muestreo: muestras de audio por segundo.", "Sample rate: audio samples per second.", 8000, 192000, true),
        ["kr"] = ("Frecuencia de control: ciclos de control por segundo (normalmente se deriva de sr / ksmps).", "Control rate: control cycles per second (usually derived from sr / ksmps).", 100, 48000, false),
        ["ksmps"] = ("Muestras por ciclo de control. Más chico = control más fino pero más CPU.", "Samples per control cycle. Smaller = finer control but more CPU.", 1, 4096, true),
        ["nchnls"] = ("Número de canales de salida.", "Number of output channels.", 1, 64, true),
        ["nchnls_i"] = ("Número de canales de entrada.", "Number of input channels.", 1, 64, true),
        ["0dbfs"] = ("Amplitud que equivale a 0 dB de escala completa (lo habitual es 1).", "Amplitude that equals 0 dB full scale (usually 1).", 0.0001, 32768, false),
        ["A4"] = ("Frecuencia de referencia del La 4, en Hz.", "Reference frequency of A4, in Hz.", 400, 480, false),
    };

    /// <summary>
    /// Suggested ranges for well-known opcode arguments, keyed by opcode and 0-based argument
    /// index. Amplitudes assume <c>0dbfs = 1</c>. Help is stored in Spanish and English.
    /// </summary>
    static readonly Dictionary<(string, int), (double Min, double Max, string HelpEs, string HelpEn)> KnownArguments = BuildKnownArguments();

    /// <summary>The analysed text snapshot.</summary>
    readonly string text;

    /// <summary>Offset of the first character of each line; index 0 is line 1. Always starts with 0.</summary>
    readonly List<int> lineStarts = [0];

    /// <summary>
    /// First and last 1-based content lines (exclusive of the tag lines) of the orchestra and score
    /// sections, or -1 when the section is absent. A file without sections gets the whole text as
    /// orchestra.
    /// </summary>
    readonly int orcStart, orcEnd, scoStart, scoEnd;

    /// <summary>Instrument and UDO blocks in source order (see <see cref="FindBlocks"/>).</summary>
    readonly List<CodeBlock> blocks = [];

    /// <summary>Lazy per-line cache for <see cref="ParseStatement"/>; a <c>null</c> value means "not a statement".</summary>
    readonly Dictionary<int, Statement?> statements = [];

    /// <summary>Analyzes <paramref name="text"/> (a full CSD, or plain orchestra code).</summary>
    /// <remarks>Cost is one linear scan plus a regex per orchestra line; statements are parsed later, on demand.</remarks>
    public CsoundAnalyzer(string text)
    {
        this.text = text;
        // Only '\n' starts a line, so CRLF text works too ('\r' is trimmed by LineText).
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n')
                lineStarts.Add(i + 1);

        (orcStart, orcEnd) = FindSection("CsInstruments");
        (scoStart, scoEnd) = FindSection("CsScore");
        if (orcStart < 0 && scoStart < 0)
            (orcStart, orcEnd) = (1, LineCount);
        FindBlocks();
    }

    /// <summary>Number of lines in the text.</summary>
    public int LineCount => lineStarts.Count;

    /// <summary>Instrument and UDO blocks, in order.</summary>
    public IReadOnlyList<CodeBlock> Blocks => blocks;

    /// <summary>Returns the 1-based line containing <paramref name="offset"/>.</summary>
    public int LineOf(int offset)
    {
        // Exact hit = offset is a line start (index → line idx+1). Otherwise BinarySearch returns
        // the complement of the next larger start; that index is already the 1-based line number.
        int idx = lineStarts.BinarySearch(offset);
        return idx >= 0 ? idx + 1 : ~idx;
    }

    /// <summary>Absolute offset where <paramref name="line"/> starts.</summary>
    public int LineStart(int line) => lineStarts[line - 1];

    /// <summary>Text of <paramref name="line"/> without its line break.</summary>
    public string LineText(int line)
    {
        int s = lineStarts[line - 1];
        int e = line < lineStarts.Count ? lineStarts[line] : text.Length;
        return text[s..e].TrimEnd('\r', '\n');
    }

    /// <summary>Full source of a block, from its <c>instr</c> line to its <c>endin</c> line.</summary>
    public string BlockText(CodeBlock block) =>
        string.Join("\n", Enumerable.Range(block.StartLine, block.EndLine - block.StartLine + 1).Select(LineText));

    /// <summary>The block containing <paramref name="line"/>, or <c>null</c> for header/score lines.</summary>
    public CodeBlock? BlockAt(int line) => blocks.FirstOrDefault(b => line >= b.StartLine && line <= b.EndLine);

    /// <summary>True if <paramref name="line"/> lies inside <c>&lt;CsScore&gt;</c>.</summary>
    public bool InScore(int line) => scoStart > 0 && line >= scoStart && line <= scoEnd;

    /// <summary>True if <paramref name="line"/> lies inside the orchestra.</summary>
    public bool InOrchestra(int line) => orcStart > 0 && line >= orcStart && line <= orcEnd;

    // ------------------------------------------------------------------------------------------
    //  Entry point
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Describes the token at <paramref name="offset"/>, or returns <c>null</c> when there is
    /// nothing useful to say (whitespace, comments, keywords, punctuation…).
    /// </summary>
    /// <param name="offset">Absolute document offset under the mouse.</param>
    /// <param name="channels">Returns the live state of a channel by name, or <c>null</c> if unknown.</param>
    /// <returns>A fresh insight (callers may mutate it, see <see cref="TokenInsight"/>), or <c>null</c>.</returns>
    public TokenInsight? Inspect(int offset, Func<string, ChannelState?> channels)
    {
        if (offset < 0 || offset > text.Length)
            return null;
        int line = LineOf(offset);
        string raw = LineText(line);
        int lineStart = LineStart(line);
        int col = offset - lineStart;
        if (col > raw.Length)
            return null;

        // The order of the checks below matters: a word inside a comment or a string must not be
        // reported as a variable/opcode, and a header constant like "sr" must win over the
        // variable heuristics.
        int comment = CommentStart(raw);
        if ((comment >= 0 && col >= comment) || InBlockComment(offset))
            return null;

        if (StringAt(raw, col) is { } quotes)
            return InspectString(line, raw, quotes.Open, quotes.Close, channels);

        var (ws, we) = WordAt(raw, col);
        if (ws == we)
            return null;
        string word = raw[ws..we];

        if (HeaderConstants.ContainsKey(word) && InOrchestra(line))
            return InspectHeaderConstant(word, lineStart + ws);

        if (NumberLiteral().IsMatch(word))
        {
            // Include a unary minus ("-2" in "ftgen 0, 0, 8, -2, ...").
            if (ws > 0 && raw[ws - 1] == '-' && IsUnaryContext(raw, ws - 1))
                ws--;
            return InspectNumber(line, raw, ws, we, channels);
        }

        // In the score only numbers are explained; words there are statement letters or names.
        if (InScore(line))
            return null;

        if (PFieldName().Match(word) is { Success: true } pm)
            return InspectPField(int.Parse(pm.Groups[1].Value, CultureInfo.InvariantCulture), line, lineStart + ws, word);

        return InspectVariable(word, line, lineStart + ws, channels)
               ?? (IsOpcodeName(word) ? InspectOpcode(word, lineStart + ws) : null);
    }

    // ------------------------------------------------------------------------------------------
    //  Token kinds
    // ------------------------------------------------------------------------------------------

    /// <summary>Help for a string literal; only channel names are interesting.</summary>
    TokenInsight? InspectString(int line, string raw, int open, int close, Func<string, ChannelState?> channels)
    {
        string content = raw[(open + 1)..close];
        if (content.Length == 0)
            return null;

        // The opcode owning the string: the innermost function call around it
        // (kcut = chnget:k("cutoff")) or, in classic syntax, the line's opcode.
        var st = ParseStatement(line);
        var (callOp, _) = CallContext(raw, open);
        string? op = callOp ?? st?.Opcode;
        if (op is null || !ChannelOpcodes.Contains(op))
            return null;

        var insight = new TokenInsight
        {
            Kind = InsightKind.ChannelName,
            Start = LineStart(line) + open + 1,
            Length = content.Length,
            Text = content,
            Title = $"\"{content}\"",
            Subtitle = Loc.S("canal de control", "control channel"),
        };
        insight.Details.Add(Loc.S($"Nombre de un canal de control usado por {op}. La app crea un slider para cada canal.",
            $"Name of a control channel used by {op}. The app creates a slider for each channel."));
        ApplyChannel(insight, content, channels);
        return insight;
    }

    /// <summary>Help for a reserved header constant such as <c>ksmps</c>.</summary>
    /// <remarks>The constant's name itself is not draggable (<see cref="ScrubMode.None"/>); the
    /// card points the user to the number on its assignment line, which is.</remarks>
    TokenInsight InspectHeaderConstant(string word, int start)
    {
        var (helpEs, helpEn, min, max, integer) = HeaderConstants[word];
        var insight = new TokenInsight
        {
            Kind = InsightKind.HeaderConstant,
            Start = start,
            Length = word.Length,
            Text = word,
            Title = word,
            Subtitle = Loc.S("constante del encabezado", "header constant"),
            RangeText = Loc.S($"rango habitual: {Format(min)} – {Format(max)}", $"typical range: {Format(min)} – {Format(max)}"),
            IntegerOnly = integer,
        };
        insight.Details.Add(Loc.S(helpEs, helpEn));
        if (HeaderValue(word) is { } v)
        {
            insight.Value = v;
            insight.Details.Add(Loc.S($"Valor en este archivo: {Format(v)}. Arrastra el número de su línea para cambiarlo.",
                $"Value in this file: {Format(v)}. Drag the number on its line to change it."));
        }
        return insight;
    }

    /// <summary>Help and drag behaviour for a numeric literal, depending on where it appears.</summary>
    /// <param name="line">1-based line of the literal.</param>
    /// <param name="raw">Text of that line.</param>
    /// <param name="ws">Start column (including a unary minus, if any).</param>
    /// <param name="we">End column (exclusive).</param>
    /// <param name="channels">Channel lookup (unused for plain literals; kept for symmetry with the other inspectors).</param>
    /// <remarks>
    /// Every literal is draggable as <see cref="ScrubMode.Literal"/>. What happens on drag depends
    /// on <see cref="TokenInsight.Block"/> (inside an <c>instr</c>: live recompile) and
    /// <see cref="TokenInsight.Channel"/> (a <c>chnk</c> default: the channel follows); elsewhere
    /// only the text changes and the new value applies on the next run.
    /// </remarks>
    TokenInsight InspectNumber(int line, string raw, int ws, int we, Func<string, ChannelState?> channels)
    {
        string word = raw[ws..we];
        double value = double.Parse(word, NumberStyles.Float, CultureInfo.InvariantCulture);
        int dot = word.IndexOf('.');
        var block = BlockAt(line);
        var insight = new TokenInsight
        {
            Kind = InsightKind.Number,
            Start = LineStart(line) + ws,
            Length = we - ws,
            Text = word,
            Title = word,
            Subtitle = Loc.S("número", "number"),
            Value = value,
            Decimals = dot < 0 ? 0 : word.Length - dot - 1,
            Scrub = ScrubMode.Literal,
            Block = block?.Keyword == "instr" ? block : null,
            // Most musical quantities are not negative; keep positive literals positive.
            Minimum = value >= 0 ? 0 : null,
        };

        if (InScore(line))
        {
            DescribeScoreNumber(insight, raw, ws);
            return insight;
        }

        // Orchestra contexts, most specific first: chnk declaration, header assignment
        // ("ksmps = 32"), then generic opcode/function argument.
        var st = ParseStatement(line);
        int abs = LineStart(line) + ws;
        if (st is { Opcode: "chnk" or "chn_k" })
            DescribeChnkArgument(insight, st, ArgIndexAt(st, abs));
        else if (st is { Opcode: "=", Outputs: [var header] } && HeaderConstants.TryGetValue(header, out var hc))
        {
            insight.Subtitle = Loc.S($"valor de {header}", $"value of {header}");
            insight.Details.Add(Loc.S(hc.HelpEs, hc.HelpEn));
            SetRange(insight, hc.Min, hc.Max, Loc.S("rango habitual", "typical range"));
            insight.IntegerOnly = hc.Integer;
            insight.Details.Add(Loc.S("Se aplica al volver a tocar.", "Takes effect the next time you play."));
            return insight;
        }
        else
            DescribeOpcodeArgument(insight, raw, ws, st, abs);

        if (insight.Block is not null)
            insight.Details.Add(Loc.S($"Mientras suena, arrastrar recompila {insight.Block.Label}: las notas nuevas usan el valor nuevo.",
                $"While playing, dragging recompiles {insight.Block.Label}: new notes use the new value."));
        else if (block is null && insight.Channel is null)
            insight.Details.Add(Loc.S("Está en el encabezado: el cambio se aplica al volver a tocar.",
                "This is in the header: the change takes effect the next time you play."));
        return insight;
    }

    /// <summary>Help for a p-field reference: meaning, values used in the score, and who schedules the instrument.</summary>
    /// <param name="n">P-field number (4 for <c>p4</c>).</param>
    /// <param name="line">1-based line of the reference.</param>
    /// <param name="start">Absolute offset of the token.</param>
    /// <param name="word">The token text (<c>p4</c>).</param>
    /// <remarks>
    /// The range shown is the min/max of the literal values found in the score's <c>i</c> lines for
    /// this instrument; expressions passed by <c>schedule</c>/<c>event</c> calls are listed (up to
    /// three) but not evaluated. Not draggable.
    /// </remarks>
    TokenInsight InspectPField(int n, int line, int start, string word)
    {
        var block = BlockAt(line);
        var insight = new TokenInsight
        {
            Kind = InsightKind.PField,
            Start = start,
            Length = word.Length,
            Text = word,
            Title = word,
            Subtitle = Loc.S($"p-field {n} (parámetro de la nota)", $"p-field {n} (note parameter)"),
        };
        insight.Details.Add(n switch
        {
            1 => Loc.S("Número del instrumento que suena.", "Number of the instrument being played."),
            2 => Loc.S("Momento de inicio de la nota, en segundos (o beats si hay tempo).", "Start time of the note, in seconds (or beats if a tempo is set)."),
            3 => Loc.S("Duración de la nota en segundos; un valor negativo la mantiene sonando.", "Duration of the note in seconds; a negative value holds it indefinitely."),
            _ => Loc.S($"Valor que recibe la nota en la posición {n} de su evento (i {(block?.Name ?? "…")} inicio duración p4 p5 …).",
                $"Value the note receives at position {n} of its event (i {(block?.Name ?? "…")} start duration p4 p5 …)."),
        });
        if (block is null)
            return insight;

        var values = ScoreValues(block, n);
        if (values.Count > 0)
        {
            insight.RangeText = Loc.S($"en la partitura: {Format(values.Min())} – {Format(values.Max())}  ({values.Count} notas)",
                $"in the score: {Format(values.Min())} – {Format(values.Max())}  ({values.Count} notes)");
            insight.Minimum = values.Min();
            insight.Maximum = values.Max();
        }

        foreach (var (callLine, opcode, expr) in SchedulerCalls(block, n).Take(3))
            insight.Details.Add(Loc.S($"Línea {callLine}: {opcode} envía {word} = {expr}", $"Line {callLine}: {opcode} sends {word} = {expr}"));

        if (values.Count == 0 && !SchedulerCalls(block, n).Any())
            insight.Details.Add(Loc.S("No se encontraron notas para este instrumento en la partitura ni en schedule/event.",
                "No notes for this instrument were found in the score or in schedule/event calls."));
        return insight;
    }

    /// <summary>
    /// Help for an orchestra variable: rate, definition, reassignments, usage count and an inferred
    /// range. Returns <c>null</c> when <paramref name="name"/> is not a variable (e.g. an opcode).
    /// </summary>
    /// <remarks>
    /// A word counts as a variable when some statement in its scope assigns it, or, failing that,
    /// when it is spelled like one (rate prefix) and is not an opcode or instrument name. Locals are
    /// searched in their own block (or in the header code when outside any block); globals
    /// (<c>g…</c>) in the whole orchestra. If a channel is behind it (<c>kcut chnget "cutoff"</c>),
    /// the insight becomes draggable as <see cref="ScrubMode.Channel"/>.
    /// </remarks>
    TokenInsight? InspectVariable(string name, int line, int start, Func<string, ChannelState?> channels)
    {
        var block = BlockAt(line);
        bool global = GlobalVariable().IsMatch(name);
        var scope = ScopeLines(global ? null : block).ToList();
        // Every statement in scope that writes the variable; the first one is "the definition",
        // the rest are reassignments.
        var defs = scope.Select(ParseStatement).OfType<Statement>()
            .Where(s => s.Outputs.Any(o => BaseName(o) == name)).ToList();

        if (defs.Count == 0 && (!VariableName().IsMatch(name) || IsOpcodeName(name)))
            return null;
        // Instrument names such as "Sequencer" look like S-variables but are not.
        if (defs.Count == 0 && blocks.Any(b => b.Ids.Contains(name)))
            return null;

        char rate = RateOf(name, defs.FirstOrDefault());
        var (rateLabel, rateHelp) = DescribeRate(rate);
        var insight = new TokenInsight
        {
            Kind = InsightKind.Variable,
            Start = start,
            Length = name.Length,
            Text = name,
            Title = name,
            Subtitle = rateLabel + (global ? " · global" : block is not null ? Loc.S($" · local de {block.Label}", $" · local to {block.Label}") : ""),
            Block = !global && block?.Keyword == "instr" ? block : null,
            IsGlobal = global,
        };
        insight.Details.Add(rateHelp + (global ? Loc.S(" Al ser global (g…), todos los instrumentos la comparten.", " Being global (g…), it is shared by all instruments.") : ""));

        if (defs.Count > 0)
        {
            var first = defs[0];
            insight.DefinitionLine = first.Line;
            insight.Details.Add(Loc.S($"Definida en la línea {first.Line}:  {LineText(first.Line).Trim()}",
                $"Defined on line {first.Line}:  {LineText(first.Line).Trim()}"));
            if (defs.Count > 1)
                insight.Details.Add(Loc.S("Reasignada en líneas ", "Reassigned on lines ") + string.Join(", ", defs.Skip(1).Take(6).Select(d => d.Line)) + ".");

            // The range comes from the first definition only; later reassignments may widen it.
            var range = InferRange(first, block, channels, 0);
            if (range.Note is not null)
                insight.Details.Add(char.ToUpperInvariant(range.Note[0]) + range.Note[1..] + ".");
            insight.Value = range.Value;
            insight.IntegerOnly = range.Integer;
            if (range.Min is { } lo && range.Max is { } hi)
                SetRange(insight, lo, hi, range.Channel is not null ? Loc.S("rango del canal", "channel range") : Loc.S("rango posible", "possible range"));
            if (range.Channel is not null)
                ApplyChannel(insight, range.Channel, channels);
        }
        else
        {
            insight.Details.Add(global
                ? Loc.S("No se encontró su definición: puede venir de otro archivo incluido.",
                    "Its definition was not found: it may come from another included file.")
                : Loc.S("No se encontró dónde se define en este instrumento.",
                    "Could not find where it is defined in this instrument."));
        }

        int uses = scope.Sum(l => WordCount(LineText(l), name));
        insight.Details.Add(Loc.S($"Aparece {uses} {(uses == 1 ? "vez" : "veces")} en {(global ? "la orquesta" : block?.Label ?? "el encabezado")}.",
            $"Appears {uses} {(uses == 1 ? "time" : "times")} in {(global ? "the orchestra" : block?.Label ?? "the header")}."));
        return insight;
    }

    /// <summary>Help for an opcode: its overloads as readable signatures.</summary>
    static TokenInsight InspectOpcode(string name, int start)
    {
        var sigs = OpcodeIndex.Get(name);
        var insight = new TokenInsight
        {
            Kind = InsightKind.Opcode,
            Start = start,
            Length = name.Length,
            Text = name,
            Title = name,
            Subtitle = "opcode" + (sigs.Length > 0 && sigs.All(s => s.Deprecated) ? Loc.S(" · obsoleto en Csound 7", " · deprecated in Csound 7") : ""),
        };
        foreach (var sig in sigs.Take(6))
            insight.Details.Add(OpcodeIndex.FormatSignature(name, sig));
        if (sigs.Length > 6)
            insight.Details.Add(Loc.S($"… y {sigs.Length - 6} variantes más.", $"… and {sigs.Length - 6} more overloads."));
        if (sigs.Length > 0)
            insight.Details.Add(Loc.S("a audio · k control · i init · S texto · [ ] opcional · … lista variable",
                "a audio · k control · i init · S string · [ ] optional · … variable-length list"));
        return insight;
    }

    // ------------------------------------------------------------------------------------------
    //  Number contexts
    // ------------------------------------------------------------------------------------------

    /// <summary>Describes a number inside a score statement (which p-field, what it means).</summary>
    /// <param name="insight">The number's insight, completed in place.</param>
    /// <param name="raw">The score line.</param>
    /// <param name="ws">Column where the number starts.</param>
    /// <remarks>
    /// The p-field index is the count of whitespace-separated tokens before the number, plus one,
    /// after removing the statement letter. Score syntax extras (carry <c>.</c>, ramps, macros,
    /// expressions in <c>[ ]</c> with spaces) are not understood and can shift the count.
    /// </remarks>
    static void DescribeScoreNumber(TokenInsight insight, string raw, int ws)
    {
        string trimmed = raw.TrimStart();
        char statement = trimmed.Length > 0 ? trimmed[0] : ' ';
        // Tokens before the number: the statement letter may be glued to p1 ("i1 0 2").
        string before = raw[..ws].TrimStart();
        if (before.Length > 0 && char.IsLetter(before[0]))
            before = before[1..];
        int p = before.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Length + 1;

        insight.Subtitle = Loc.S($"partitura · p{p} de una línea '{statement}'", $"score · p{p} of an '{statement}' statement");
        insight.Details.Add(statement switch
        {
            'i' => p switch
            {
                1 => Loc.S("Instrumento que toca la nota.", "Instrument that plays the note."),
                2 => Loc.S("Inicio de la nota (segundos).", "Note start time (seconds)."),
                3 => Loc.S("Duración de la nota (segundos).", "Note duration (seconds)."),
                _ => Loc.S($"Parámetro p{p} que recibe el instrumento.", $"Parameter p{p} passed to the instrument."),
            },
            'f' => p switch
            {
                1 => Loc.S("Número de la tabla.", "Function table number."),
                2 => Loc.S("Momento en que se crea la tabla.", "Time at which the table is created."),
                3 => Loc.S("Tamaño de la tabla (puntos).", "Table size (points)."),
                4 => Loc.S("Rutina GEN que la genera (negativa = sin normalizar).", "GEN routine that fills it (negative = not normalized)."),
                _ => Loc.S("Argumento de la rutina GEN.", "Argument of the GEN routine."),
            },
            't' => Loc.S("Tempo en BPM.", "Tempo in BPM."),
            _ => Loc.S("Valor de la partitura.", "Score value."),
        });
        // Times and sizes cannot be negative (a negative i-p3 means "held", but a drag should not
        // produce it by accident); table numbers, sizes and GEN numbers are integers.
        if (statement == 'i' && p is 2 or 3 || statement == 'f' && p is 2 or 3)
            insight.Minimum = 0;
        if (statement == 'f' && p is 1 or 3 or 4)
            insight.IntegerOnly = true;
        insight.Details.Add(Loc.S("Se aplica al volver a tocar.", "Takes effect the next time you play."));
    }

    /// <summary>Describes one argument of a <c>chnk</c> declaration and links the default value to its channel.</summary>
    /// <param name="insight">The number's insight, completed in place.</param>
    /// <param name="st">The parsed <c>chnk "name", imode, itype, idflt, imin, imax</c> statement.</param>
    /// <param name="index">0-based argument index of the number (0 is the name).</param>
    /// <remarks>
    /// Csound 7 renamed <c>chn_k</c> to <c>chnk</c> (both are accepted here). In Csound 7 itype 2 is
    /// linear and 3 exponential (minimum must be &gt; 0), which is what makes the default value drag
    /// exponentially.
    /// </remarks>
    static void DescribeChnkArgument(TokenInsight insight, Statement st, int index)
    {
        string channel = Unquote(st.Args.FirstOrDefault().Text ?? "");
        insight.Subtitle = Loc.S($"declaración del canal \"{channel}\"", $"declaration of channel \"{channel}\"");
        switch (index)
        {
            case 1:
                insight.Details.Add(Loc.S("imode: 1 = entrada, 2 = salida, 3 = entrada y salida.", "imode: 1 = input, 2 = output, 3 = input and output."));
                SetRange(insight, 1, 3, Loc.S("valores", "values"));
                insight.IntegerOnly = true;
                break;
            case 2:
                insight.Details.Add(Loc.S("itype: 0 = sin rango, 1 = entero, 2 = lineal, 3 = exponencial (mínimo > 0).",
                    "itype: 0 = no range, 1 = integer, 2 = linear, 3 = exponential (minimum > 0)."));
                SetRange(insight, 0, 3, Loc.S("valores", "values"));
                insight.IntegerOnly = true;
                break;
            case 3:
                insight.Details.Add(Loc.S("Valor por defecto del canal. Mientras suena, el canal sigue al número que arrastras.",
                    "Default value of the channel. While playing, the channel follows the number you drag."));
                double? lo = TryNumber(st.Args.ElementAtOrDefault(4).Text), hi = TryNumber(st.Args.ElementAtOrDefault(5).Text);
                if (lo is { } a && hi is { } b && b > a)
                {
                    SetRange(insight, a, b, Loc.S("rango del canal", "channel range"));
                    insight.Exponential = TryNumber(st.Args.ElementAtOrDefault(2).Text) == 3 && a > 0;
                }
                insight.Channel = channel;
                break;
            case 4:
                insight.Details.Add(Loc.S("Valor mínimo del canal (y del slider).", "Minimum value of the channel (and its slider)."));
                break;
            case 5:
                insight.Details.Add(Loc.S("Valor máximo del canal (y del slider).", "Maximum value of the channel (and its slider)."));
                break;
        }
    }

    /// <summary>Describes a literal used as an opcode argument: which argument, its type and a suggested range.</summary>
    /// <param name="insight">The number's insight, completed in place.</param>
    /// <param name="raw">The line text.</param>
    /// <param name="ws">Column of the literal.</param>
    /// <param name="st">The parsed line, or <c>null</c>.</param>
    /// <param name="abs">Absolute offset of the literal (to find its argument index in <paramref name="st"/>).</param>
    /// <remarks>
    /// Known (opcode, argument) pairs get a suggested range from <see cref="KnownArguments"/>; a range
    /// spanning two decades or more (e.g. 20–20000 Hz) drags exponentially. The expected type comes
    /// from the first non-deprecated overload, so it may be wrong for opcodes whose overloads
    /// differ in argument types.
    /// </remarks>
    static void DescribeOpcodeArgument(TokenInsight insight, string raw, int ws, Statement? st, int abs)
    {
        // Function-call syntax first (moogladder(a1, 800, 0.3)); otherwise the classic-syntax
        // opcode of the line, unless the line is a plain assignment (its "opcode" is "=", "+=", …).
        var (op, index) = CallContext(raw, ws);
        if (op is null && st?.Opcode is { } stOp && stOp != "=" && !stOp.EndsWith('='))
            (op, index) = (stOp, ArgIndexAt(st, abs));
        if (op is null || index < 0)
            return;

        insight.Subtitle = Loc.S($"argumento {index + 1} de {op}", $"argument {index + 1} of {op}");
        if (KnownArguments.TryGetValue((op, index), out var known))
        {
            string help = Loc.S(known.HelpEs, known.HelpEn);
            insight.Details.Add(char.ToUpperInvariant(help[0]) + help[1..] + ".");
            SetRange(insight, known.Min, known.Max, Loc.S("rango sugerido", "suggested range"));
            insight.Exponential = known.Min > 0 && known.Max / known.Min >= 100;
        }
        var sig = OpcodeIndex.Get(op).FirstOrDefault(s => !s.Deprecated) ?? OpcodeIndex.Get(op).FirstOrDefault();
        if (sig is not null && OpcodeIndex.ReadableTypes(sig.InTypes) is var types && index < types.Count)
            insight.Details.Add(Loc.S($"Tipo esperado: {OpcodeIndex.DescribeType(types[index])}.", $"Expected type: {OpcodeIndex.DescribeType(types[index])}."));
    }

    // ------------------------------------------------------------------------------------------
    //  Range inference
    // ------------------------------------------------------------------------------------------

    /// <summary>Result of <see cref="InferRange"/>.</summary>
    /// <param name="Min">Lowest value the variable can take, if known.</param>
    /// <param name="Max">Highest value, if known.</param>
    /// <param name="Value">A constant value (literal assignment, <c>init</c>, channel's current value), if known.</param>
    /// <param name="Channel">Name of the control channel the variable reads, if any (makes it draggable).</param>
    /// <param name="Note">Lower-case explanation ("reads the control channel …"), capitalised by the caller.</param>
    /// <param name="Integer">True when only whole values make sense (triggers, table numbers).</param>
    readonly record struct RangeInfo(double? Min, double? Max, double? Value, string? Channel, string? Note, bool Integer);

    /// <summary>
    /// Infers what a variable holds from the statement that defines it: a channel, a literal, an
    /// envelope, an oscillator amplitude, a random range… Follows simple chains (smoothing opcodes,
    /// plain copies) up to three levels deep.
    /// </summary>
    /// <param name="def">The statement that assigns the variable.</param>
    /// <param name="block">Scope used to resolve other variables mentioned in <paramref name="def"/>.</param>
    /// <param name="channels">Live channel lookup.</param>
    /// <param name="depth">Recursion depth; <see cref="VariableRange"/> stops at 3 to bound the work and break cycles (<c>kx = ky</c>, <c>ky = kx</c>).</param>
    RangeInfo InferRange(Statement def, CodeBlock? block, Func<string, ChannelState?> channels, int depth)
    {
        string op = def.Opcode ?? "";
        var args = def.Args;
        // Literal value of argument i, or null when it is not a plain number.
        double? Num(int i) => TryNumber(args.ElementAtOrDefault(i).Text);
        // Bipolar range ±|amp| for oscillators and noise generators whose first argument is the
        // amplitude (a literal, or a variable whose own range is known).
        RangeInfo Amp(string what)
        {
            string a = args.ElementAtOrDefault(0).Text ?? "?";
            double? amp = Num(0) ?? VariableRange(a, block, channels, depth)?.Max;
            return amp is { } m
                ? new RangeInfo(-Math.Abs(m), Math.Abs(m), null, null,
                    Loc.S($"{what} con amplitud {a}: oscila entre ±{Format(Math.Abs(m))}", $"{what} with amplitude {a}: swings within ±{Format(Math.Abs(m))}"), false)
                : new RangeInfo(null, null, null, null, Loc.S($"{what} con amplitud {a}", $"{what} with amplitude {a}"), false);
        }

        switch (op)
        {
            case var _ when ChannelReaders.Contains(op) && args.Count > 0 && IsQuoted(args[0].Text):
            {
                string ch = Unquote(args[0].Text);
                var state = channels(ch);
                return new RangeInfo(state?.Minimum, state?.Maximum, state?.Value, ch,
                    Loc.S($"lee el canal de control \"{ch}\": arrástrala para cambiarlo en tiempo real",
                        $"reads the control channel \"{ch}\": drag it to change it in real time"), false);
            }
            case "=":
            {
                // Assignment: a literal, a single function call (analysed as if it were the
                // classic-syntax opcode), a copy of another variable, or a general expression.
                string expr = args.ElementAtOrDefault(0).Text?.Trim() ?? "";
                if (TryNumber(expr) is { } v)
                    return new RangeInfo(null, null, v, null, Loc.S($"vale {Format(v)}", $"equals {Format(v)}"), false);
                if (FunctionCall().Match(expr) is { Success: true } fm)
                {
                    var inner = new Statement(def.Line, def.Outputs, fm.Groups[1].Value,
                        SplitArgs(fm.Groups[2].Value, 0));
                    var r = InferRange(inner, block, channels, depth + 1);
                    return r with { Note = r.Note ?? Loc.S($"resultado de {expr}", $"result of {expr}") };
                }
                if (VariableName().IsMatch(expr) && VariableRange(expr, block, channels, depth) is { } copy)
                    return copy with { Note = Loc.S($"copia de {expr}", $"copy of {expr}") + (copy.Note is null ? "" : $" ({copy.Note})") };
                return new RangeInfo(null, null, null, null, Loc.S($"se calcula como {expr}", $"computed as {expr}"), false);
            }
            case "init":
                return Num(0) is { } iv
                    ? new RangeInfo(null, null, iv, null, Loc.S($"valor inicial {Format(iv)}", $"initial value {Format(iv)}"), false)
                    : new RangeInfo(null, null, null, null,
                        Loc.S($"valor inicial {args.ElementAtOrDefault(0).Text}", $"initial value {args.ElementAtOrDefault(0).Text}"), false);
            case "random":
                return Num(0) is { } rl && Num(1) is { } rh
                    ? new RangeInfo(rl, rh, null, null, Loc.S($"número aleatorio entre {Format(rl)} y {Format(rh)}", $"random number between {Format(rl)} and {Format(rh)}"), false)
                    : new RangeInfo(null, null, null, null, Loc.S("número aleatorio", "random number"), false);
            case "rnd":
                return Num(0) is { } r0 ? new RangeInfo(0, r0, null, null, Loc.S($"aleatorio entre 0 y {Format(r0)}", $"random between 0 and {Format(r0)}"), false) : default;
            case "birnd":
                return Num(0) is { } b0 ? new RangeInfo(-b0, b0, null, null, Loc.S($"aleatorio entre ±{Format(b0)}", $"random within ±{Format(b0)}"), false) : default;
            case "limit":
                return Num(1) is { } ll && Num(2) is { } lh
                    ? new RangeInfo(ll, lh, null, null, Loc.S($"limitada entre {Format(ll)} y {Format(lh)}", $"limited between {Format(ll)} and {Format(lh)}"), false)
                    : new RangeInfo(null, null, null, null, Loc.S("valor limitado", "limited value"), false);
            // Breakpoint generators: values sit at even positions (value, time, value, …), or every
            // third position for transeg (value, time, curve, value, …).
            case "line" or "expon":
                return Breakpoints(args, 0, 2, op);
            case "linseg" or "expseg" or "linsegr" or "expsegr":
                return Breakpoints(args, 0, 2, op, every: true);
            case "transeg":
                return Breakpoints(args, 0, 3, op, every: true);
            case "madsr" or "adsr" or "mxadsr" or "xadsr":
                return new RangeInfo(0, 1, null, null, Loc.S("envolvente ADSR: sube a 1, baja al sustain y vuelve a 0 al soltar",
                    "ADSR envelope: rises to 1, falls to the sustain level and returns to 0 on release"), false);
            case "linen" or "linenr":
                return Num(0) is { } la
                    ? new RangeInfo(0, la, null, null, Loc.S($"envolvente de 0 a {Format(la)}", $"envelope from 0 to {Format(la)}"), false)
                    : new RangeInfo(null, null, null, null, Loc.S("envolvente", "envelope"), false);
            case "oscil" or "oscili" or "oscil3" or "poscil" or "poscil3" or "vco" or "vco2" or "lfo" or "buzz"
                or "gbuzz" or "pluck" or "foscil" or "foscili" or "vibr":
                return Amp(Loc.S("oscilador ", "oscillator ") + op);
            case "rand" or "randi" or "randh" or "noise":
                return Amp(Loc.S("ruido ", "noise ") + op);
            case "metro" or "trigger" or "changed" or "changed2":
                return new RangeInfo(0, 1, null, null, Loc.S("disparo: vale 1 en el ciclo del pulso y 0 el resto del tiempo",
                    "trigger: 1 on the control cycle of the pulse, 0 the rest of the time"), true);
            case "port" or "portk" or "lag" or "lineto" or "tlineto" or "tonek":
            {
                // Smoothers do not change the range of their input: inherit it (and its channel,
                // so dragging a smoothed variable still drives the slider).
                string src = args.ElementAtOrDefault(0).Text ?? "";
                var inner = VariableRange(src, block, channels, depth);
                return inner is { } r
                    ? r with { Note = Loc.S($"versión suavizada de {src}", $"smoothed version of {src}") + (r.Note is null ? "" : Loc.S($", que {r.Note}", $", which {r.Note}")) }
                    : new RangeInfo(null, null, null, null, Loc.S($"versión suavizada de {src}", $"smoothed version of {src}"), false);
            }
            case "table" or "tablei" or "table3" or "tab" or "tab_i":
                return new RangeInfo(null, null, null, null,
                    Loc.S($"valor leído de la tabla {args.ElementAtOrDefault(1).Text}", $"value read from table {args.ElementAtOrDefault(1).Text}"), false);
            case "cpsmidinn" or "mtof" or "cpspch" or "cpsoct":
                return new RangeInfo(null, null, null, null, Loc.S("frecuencia en Hz", "frequency in Hz"), false);
            case "ftgen" or "ftgenonce":
                return new RangeInfo(null, null, null, null,
                    Loc.S($"número de la tabla generada con GEN {args.ElementAtOrDefault(3).Text} de tamaño {args.ElementAtOrDefault(2).Text}",
                        $"number of the table generated with GEN {args.ElementAtOrDefault(3).Text}, size {args.ElementAtOrDefault(2).Text}"), true);
            default:
                return new RangeInfo(null, null, null, null, op.Length > 0 ? Loc.S($"salida de {op}", $"output of {op}") : null, false);
        }
    }

    /// <summary>Range of a breakpoint generator (<c>linseg a, dur, b, …</c>): min/max of its point values.</summary>
    /// <param name="args">The generator's arguments.</param>
    /// <param name="first">Index of the first value.</param>
    /// <param name="stride">Distance between values (2 for value/time pairs, 3 for transeg).</param>
    /// <param name="op">Opcode name, for the note.</param>
    /// <param name="every"><c>false</c> stops after two values (<c>line</c>/<c>expon</c> have only a start and an end).</param>
    /// <remarks>Non-literal points are skipped, so the range may be incomplete.</remarks>
    static RangeInfo Breakpoints(IReadOnlyList<Arg> args, int first, int stride, string op, bool every = false)
    {
        var points = new List<double>();
        for (int i = first; i < args.Count; i += stride)
        {
            if (TryNumber(args[i].Text) is { } v) points.Add(v);
            if (!every && points.Count == 2) break;
        }
        return points.Count > 0
            ? new RangeInfo(points.Min(), points.Max(), null, null,
                Loc.S($"segmentos de {op} entre {Format(points.Min())} y {Format(points.Max())}", $"{op} segments between {Format(points.Min())} and {Format(points.Max())}"), false)
            : new RangeInfo(null, null, null, null, Loc.S($"segmentos de {op}", $"{op} segments"), false);
    }

    /// <summary>Range of another variable, found through its first definition in the same scope.</summary>
    RangeInfo? VariableRange(string name, CodeBlock? block, Func<string, ChannelState?> channels, int depth)
    {
        if (depth >= 3 || !VariableName().IsMatch(name))
            return null;
        var scope = ScopeLines(GlobalVariable().IsMatch(name) ? null : block);
        var def = scope.Select(ParseStatement).OfType<Statement>().FirstOrDefault(s => s.Outputs.Any(o => BaseName(o) == name));
        return def is null ? null : InferRange(def, block, channels, depth + 1);
    }

    /// <summary>True for Csound opcodes and for user-defined opcodes (UDOs) declared in this orchestra.</summary>
    bool IsOpcodeName(string name) =>
        OpcodeIndex.IsOpcode(name) || blocks.Any(b => b.Keyword == "opcode" && b.Name == name);

    /// <summary>Fills range, value and drag mode of an insight from a live channel.</summary>
    /// <remarks>
    /// <see cref="TokenInsight.Channel"/> is always set; the token only becomes draggable
    /// (<see cref="ScrubMode.Channel"/>) when the view model actually has a slider for that name.
    /// </remarks>
    static void ApplyChannel(TokenInsight insight, string channel, Func<string, ChannelState?> channels)
    {
        insight.Channel = channel;
        if (channels(channel) is not { } state)
            return;
        insight.Scrub = ScrubMode.Channel;
        insight.Value = state.Value;
        insight.Exponential = state.Exponential && state.Minimum > 0;
        SetRange(insight, state.Minimum, state.Maximum, Loc.S("rango del canal", "channel range"));
    }

    /// <summary>Sets drag bounds and the displayed range text.</summary>
    static void SetRange(TokenInsight insight, double min, double max, string label)
    {
        insight.Minimum = min;
        insight.Maximum = max;
        insight.RangeText = $"{label}: {Format(min)} – {Format(max)}";
    }

    // ------------------------------------------------------------------------------------------
    //  Score and scheduler lookups (p-fields)
    // ------------------------------------------------------------------------------------------

    /// <summary>Numeric values of p-field <paramref name="n"/> in every score <c>i</c> line for <paramref name="block"/>.</summary>
    /// <remarks>
    /// Matches lines starting with <c>i</c> whose p1 is one of the block's ids (number or quoted
    /// name). Tokens are split on whitespace, so p1 glued to the letter (<c>i1 0 2</c>) works; carry
    /// symbols (<c>.</c>, <c>+</c>) and expressions are skipped because they are not numbers.
    /// </remarks>
    List<double> ScoreValues(CodeBlock block, int n)
    {
        var ids = block.Ids.ToHashSet();
        var values = new List<double>();
        if (scoStart < 0)
            return values;
        for (int line = scoStart; line <= scoEnd; line++)
        {
            string raw = LineText(line);
            int c = CommentStart(raw);
            string code = (c >= 0 ? raw[..c] : raw).Trim();
            if (!code.StartsWith('i'))
                continue;
            var tokens = code[1..].Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < n || !ids.Contains(Unquote(tokens[0])))
                continue;
            if (TryNumber(tokens[n - 1]) is { } v)
                values.Add(v);
        }
        return values;
    }

    /// <summary>Orchestra calls (<c>schedule</c>, <c>schedulek</c>, <c>event</c>…) that start <paramref name="block"/>, with the expression they pass as p-field <paramref name="n"/>.</summary>
    IEnumerable<(int Line, string Opcode, string Expression)> SchedulerCalls(CodeBlock block, int n)
    {
        var ids = block.Ids.ToHashSet();
        for (int line = Math.Max(orcStart, 1); line <= orcEnd && line <= LineCount; line++)
        {
            var st = ParseStatement(line);
            if (st?.Opcode is not { } op || !Schedulers.Contains(op))
                continue;
            // event "i", instr, p2, p3, …   versus   schedule instr, p2, p3, …
            int shift = op.StartsWith("event") ? 1 : 0;
            if (st.Args.Count <= shift || !ids.Contains(Unquote(st.Args[shift].Text)))
                continue;
            int argIndex = shift + n - 1;
            if (argIndex < st.Args.Count)
                yield return (line, op, st.Args[argIndex].Text);
        }
    }

    /// <summary>Value assigned to a header constant (<c>ksmps = 64</c>), if any.</summary>
    /// <remarks>Only header lines (outside blocks) are searched; the first literal assignment wins.
    /// Options such as <c>--ksmps</c> or the app's sample-rate override are not taken into account.</remarks>
    double? HeaderValue(string constant)
    {
        for (int line = Math.Max(orcStart, 1); line <= orcEnd && line <= LineCount; line++)
        {
            if (BlockAt(line) is not null)
                continue;
            if (ParseStatement(line) is { Opcode: "=", Outputs: [var o] } st && o == constant)
                return TryNumber(st.Args.ElementAtOrDefault(0).Text);
        }
        return null;
    }

    // ------------------------------------------------------------------------------------------
    //  Statement parsing
    // ------------------------------------------------------------------------------------------

    /// <summary>One argument of a statement and its absolute document offset.</summary>
    /// <param name="Text">Trimmed argument text (may be an expression or a quoted string).</param>
    /// <param name="Offset">Absolute offset of its first non-blank character.</param>
    /// <remarks>A <c>default(Arg)</c> (from <c>ElementAtOrDefault</c>) has <c>Text == null</c>; callers guard with <c>?.</c>/<c>??</c>.</remarks>
    readonly record struct Arg(string Text, int Offset);

    /// <summary>A single parsed orchestra line.</summary>
    /// <param name="Line">1-based line number.</param>
    /// <param name="Outputs">Output variable names as written (may carry <c>:type</c> or <c>[]</c>).</param>
    /// <param name="Opcode">Opcode name, <c>"="</c>/<c>"+="</c>… for assignments, or a keyword such as <c>if</c>.</param>
    /// <param name="Args">Top-level comma separated arguments (for assignments: one argument holding the expression).</param>
    sealed record Statement(int Line, IReadOnlyList<string> Outputs, string? Opcode, IReadOnlyList<Arg> Args);

    /// <summary>Parses <paramref name="line"/> into a <see cref="Statement"/> (cached), or <c>null</c> for blank lines.</summary>
    /// <remarks>
    /// Also <c>null</c> for comment-only lines, CSD tag lines (<c>&lt;…&gt;</c>) and labels
    /// (<c>loop:</c>). Recognised forms, tried in order:
    /// <list type="number">
    /// <item>assignment: <c>out = expr</c> and compound <c>+= -= *= /=</c> (but not <c>==</c>);</item>
    /// <item>classic: <c>out1, out2 opcode arg, arg</c> — with a disambiguation for lines that are
    /// really <c>opcode arg, arg</c> (the regex cannot tell <c>outs aL, aR</c> from
    /// <c>kx outs …</c>, so a first word that is an opcode or keyword is taken as the opcode);</item>
    /// <item>opcode only: <c>opcode args…</c> (e.g. <c>turnoff</c>).</item>
    /// </list>
    /// Multi-line statements (<c>\</c> continuation) are seen line by line only.
    /// </remarks>
    Statement? ParseStatement(int line)
    {
        if (statements.TryGetValue(line, out var cached))
            return cached;

        string raw = LineText(line);
        int c = CommentStart(raw);
        string code = c >= 0 ? raw[..c] : raw;
        int lineStart = LineStart(line);
        Statement? st = null;

        if (string.IsNullOrWhiteSpace(code) || code.TrimStart().StartsWith('<') || Label().IsMatch(code))
            st = null;
        else if (Assignment().Match(code) is { Success: true } a)
        {
            // The whole right-hand side is one argument; its offset skips leading blanks.
            var g = a.Groups["expr"];
            st = new Statement(line, [a.Groups["out"].Value], a.Groups["op"].Value,
                [new Arg(g.Value.Trim(), lineStart + g.Index + (g.Value.Length - g.Value.TrimStart().Length))]);
        }
        else if (Classic().Match(code) is { Success: true } m)
        {
            string outs = m.Groups["outs"].Value;
            string opc = m.Groups["opc"].Value;
            var outputs = outs.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            bool firstIsOpcode = outputs.Length == 1 && (IsOpcodeName(BaseName(outputs[0])) || Keywords.Contains(outputs[0]));
            if (firstIsOpcode || (!IsOpcodeName(opc) && outputs.Length == 1))
            {
                // "outs aL, aR" / "if k == 1 then": the first word is the opcode and there are no outputs.
                var g = m.Groups["outs"];
                st = new Statement(line, [], BaseName(outputs[0]), SplitArgs(code[(g.Index + g.Length)..], lineStart + g.Index + g.Length));
            }
            else
            {
                var g = m.Groups["args"];
                st = new Statement(line, outputs, opc, SplitArgs(g.Value, lineStart + g.Index));
            }
        }
        else if (OpcodeOnly().Match(code) is { Success: true } o)
        {
            var g = o.Groups["args"];
            st = new Statement(line, [], o.Groups["opc"].Value, SplitArgs(g.Value, lineStart + g.Index));
        }

        statements[line] = st;
        return st;
    }

    /// <summary>Splits on top-level commas (ignoring commas inside parentheses, brackets and strings).</summary>
    /// <param name="s">Argument text (after the opcode).</param>
    /// <param name="baseOffset">Absolute offset of <c>s[0]</c>, so each <see cref="Arg.Offset"/> is absolute.</param>
    /// <returns>Non-empty trimmed arguments in order. The text after the last top-level comma is
    /// always returned as the final argument, even while the user is still typing it with an
    /// unterminated string or an unclosed parenthesis (e.g. <c>prints "hello</c>).</returns>
    static List<Arg> SplitArgs(string s, int baseOffset)
    {
        var args = new List<Arg>();
        int depth = 0, start = 0;
        bool inString = false;
        for (int i = 0; i <= s.Length; i++)
        {
            // A virtual ',' after the last character flushes the final argument.
            bool atEnd = i == s.Length;
            char ch = atEnd ? ',' : s[i];
            if (ch == '"' && (i == 0 || s[i - 1] != '\\')) inString = !inString;
            // Bug fix: the virtual end comma must flush even inside an unterminated string or an
            // unclosed bracket; previously `continue`/`depth > 0` swallowed it and the last
            // argument (the one being typed) disappeared from hover and signature help.
            if (inString && !atEnd) continue;
            if (ch is '(' or '[') depth++;
            else if (ch is ')' or ']') depth--;
            else if (ch == ',' && (depth <= 0 || atEnd))
            {
                string piece = s[start..i];
                int lead = piece.Length - piece.TrimStart().Length;
                if (piece.Trim().Length > 0)
                    args.Add(new Arg(piece.Trim(), baseOffset + start + lead));
                start = i + 1;
            }
        }
        return args;
    }

    /// <summary>Index of the argument of <paramref name="st"/> that contains <paramref name="offset"/>.</summary>
    /// <returns>The last argument starting at or before <paramref name="offset"/> (arguments are in
    /// offset order), or -1 when the offset precedes all of them (e.g. on the opcode name).</returns>
    static int ArgIndexAt(Statement st, int offset)
    {
        int index = -1;
        for (int i = 0; i < st.Args.Count; i++)
            if (st.Args[i].Offset <= offset) index = i;
        return index;
    }

    /// <summary>
    /// If column <paramref name="col"/> is inside a function-call style argument list
    /// (<c>limit(x, 40, 18000)</c>), returns the function name and argument index; otherwise
    /// <c>(null, -1)</c>.
    /// </summary>
    /// <remarks>
    /// Scans backwards from the column, tracking bracket depth: commas at depth 0 count the
    /// argument index; the first unmatched <c>(</c> preceded by a name is the call. An unmatched
    /// <c>[</c> (array index) means "not a call argument". Quote toggling ignores escapes.
    /// </remarks>
    static (string? Name, int Index) CallContext(string raw, int col)
    {
        int depth = 0, commas = 0;
        bool inString = false;
        for (int i = col - 1; i >= 0; i--)
        {
            char c = raw[i];
            if (c == '"') inString = !inString;
            if (inString) continue;
            if (c is ')' or ']') depth++;
            else if (c is '(' or '[')
            {
                if (depth == 0)
                {
                    if (c != '(') return (null, -1);
                    int end = i;
                    // Skip a rate annotation such as "chnget:k(".
                    int j = end - 1;
                    while (j >= 0 && (char.IsLetterOrDigit(raw[j]) || raw[j] is '_' or ':')) j--;
                    string name = BaseName(raw[(j + 1)..end]);
                    if (name.Length > 0 && char.IsLetter(name[0]))
                        return (name, commas);
                    // A grouping parenthesis such as "(0.35 + kenv)": keep looking outwards.
                    commas = 0;
                    continue;
                }
                depth--;
            }
            else if (c == ',' && depth == 0) commas++;
        }
        return (null, -1);
    }

    // ------------------------------------------------------------------------------------------
    //  Text helpers
    // ------------------------------------------------------------------------------------------

    /// <summary>Lines visible to a variable: the block's lines, the header (for <c>null</c>), or the whole orchestra for globals.</summary>
    IEnumerable<int> ScopeLines(CodeBlock? block)
    {
        if (block is not null)
            return Enumerable.Range(block.StartLine, block.EndLine - block.StartLine + 1);
        int from = Math.Max(orcStart, 1), to = Math.Min(orcEnd, LineCount);
        return to >= from ? Enumerable.Range(from, to - from + 1) : [];
    }

    /// <summary>Returns the 1-based first and last content lines of a <c>&lt;tag&gt;</c> section, or (-1, -1).</summary>
    /// <remarks>Uses the first opening and first closing tag (case-insensitive); content is the lines strictly between them.</remarks>
    (int, int) FindSection(string tag)
    {
        int open = text.IndexOf($"<{tag}>", StringComparison.OrdinalIgnoreCase);
        int close = text.IndexOf($"</{tag}>", StringComparison.OrdinalIgnoreCase);
        if (open < 0 || close < open)
            return (-1, -1);
        return (LineOf(open) + 1, LineOf(close) - 1);
    }

    /// <summary>Finds every <c>instr … endin</c> and <c>opcode … endop</c> block in the orchestra.</summary>
    /// <remarks>
    /// Simple state machine over the orchestra lines: a header opens a block, the next
    /// <c>endin</c>/<c>endop</c> closes it (the two are not matched against each other). A header
    /// seen while a block is open is ignored, and a block that is never closed is not reported.
    /// For UDOs the name is cut at the first comma (<c>opcode Foo, a, ak</c> → <c>Foo</c>).
    /// </remarks>
    void FindBlocks()
    {
        int from = Math.Max(orcStart, 1), to = Math.Min(orcEnd, LineCount);
        string? keyword = null, name = null;
        int startLine = 0;
        for (int line = from; line <= to; line++)
        {
            string raw = LineText(line);
            if (keyword is null && BlockStart().Match(raw) is { Success: true } m)
            {
                keyword = m.Groups[1].Value;
                name = m.Groups[2].Value.Trim();
                if (keyword == "opcode")
                    name = name.Split(',')[0].Trim();
                startLine = line;
            }
            else if (keyword is not null && BlockEnd().IsMatch(raw))
            {
                blocks.Add(new CodeBlock(keyword, name!, startLine, line));
                keyword = null;
            }
        }
    }

    /// <summary>Column where a <c>;</c> or <c>//</c> comment starts on the line (outside strings), or -1.</summary>
    static int CommentStart(string raw)
    {
        bool inString = false;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '"' && (i == 0 || raw[i - 1] != '\\')) inString = !inString;
            else if (!inString && (c == ';' || (c == '/' && i + 1 < raw.Length && raw[i + 1] == '/')))
                return i;
        }
        return -1;
    }

    /// <summary>True if <paramref name="offset"/> is inside a <c>/* … */</c> comment.</summary>
    /// <remarks>
    /// <para>Scans the document from the start up to the offset with a small state machine, so a
    /// <c>/*</c> that sits inside a double-quoted string (<c>prints "a /* b"</c>) or after a
    /// <c>;</c> or <c>//</c> line comment no longer opens a block comment. (The previous version
    /// only searched backwards for the nearest <c>/*</c> and was fooled by both cases, hiding
    /// hover help for the rest of the file.)</para>
    /// <para>Strings and line comments end at the end of their line, as in Csound; a backslash
    /// escapes a quote. An unclosed <c>/*</c> still covers everything after it, which is what
    /// Csound does too. The scan is linear in the offset and only runs on hover.</para>
    /// </remarks>
    bool InBlockComment(int offset)
    {
        int end = Math.Min(offset, text.Length);
        bool inString = false, inLine = false, inBlock = false;
        for (int i = 0; i < end; i++)
        {
            char c = text[i];
            if (inBlock)
            {
                // The whole "*/" must lie before the offset for the comment to be closed there.
                if (c == '*' && i + 1 < end && text[i + 1] == '/') { inBlock = false; i++; }
                continue;
            }
            if (c == '\n') { inString = false; inLine = false; continue; }
            if (inLine) continue;
            if (inString)
            {
                if (c == '"' && text[i - 1] != '\\') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == ';') inLine = true;
            else if (c == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/') inLine = true;
                else if (text[i + 1] == '*') { inBlock = true; i++; }
            }
        }
        return inBlock;
    }

    /// <summary>If <paramref name="col"/> is inside a string literal, returns the positions of its quotes.</summary>
    /// <remarks>Pairs unescaped quotes left to right on the line; a column on the closing quote
    /// counts as inside (so hovering the end of <c>"cutoff"</c> still works), the opening one does not.</remarks>
    static (int Open, int Close)? StringAt(string raw, int col)
    {
        int open = -1;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '"' || (i > 0 && raw[i - 1] == '\\'))
                continue;
            if (open < 0) open = i;
            else
            {
                if (col > open && col <= i) return (open, i);
                open = -1;
            }
        }
        return null;
    }

    /// <summary>Bounds of the identifier/number around <paramref name="col"/>.</summary>
    /// <returns>Start (inclusive) and end (exclusive) columns; <c>(0, 0)</c> when there is no word.</returns>
    /// <remarks>
    /// '.' counts as a word character so decimals such as <c>0.25</c> are one token. When the
    /// column is just after a word (the caret position nearest to the pointer often is), the word
    /// to its left is used.
    /// </remarks>
    static (int Start, int End) WordAt(string raw, int col)
    {
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '.';
        int s = Math.Min(col, raw.Length), e = s;
        if (s < raw.Length && !IsWord(raw[s]) && s > 0 && IsWord(raw[s - 1])) { s--; e = s; }
        if (s >= raw.Length || !IsWord(raw[s])) return (0, 0);
        while (s > 0 && IsWord(raw[s - 1])) s--;
        while (e < raw.Length && IsWord(raw[e])) e++;
        return (s, e);
    }

    /// <summary>True if the <c>-</c> at <paramref name="minus"/> is a sign rather than a subtraction.</summary>
    /// <remarks>It is a sign when nothing but blanks, a separator or an operator precedes it
    /// (<c>, -2</c>, <c>(-1</c>, <c>= -0.5</c>); after an operand (<c>kx - 2</c>) it is a subtraction.</remarks>
    static bool IsUnaryContext(string raw, int minus)
    {
        int i = minus - 1;
        while (i >= 0 && raw[i] is ' ' or '\t') i--;
        return i < 0 || raw[i] is ',' or '(' or '=' or '*' or '/' or '+' or '-' or '[';
    }

    /// <summary>Number of whole-word occurrences of <paramref name="word"/> in <paramref name="line"/> (comments excluded).</summary>
    static int WordCount(string line, string word)
    {
        int c = CommentStart(line);
        string code = c >= 0 ? line[..c] : line;
        // Not preceded by a word char or '.', not followed by a word char (a ':k' annotation or
        // '[' index after the name still counts as a use).
        return Regex.Matches(code, $@"(?<![\w.]){Regex.Escape(word)}(?![\w])").Count;
    }

    /// <summary>Strips a <c>:type</c> annotation and array brackets from a variable name.</summary>
    static string BaseName(string s)
    {
        int colon = s.IndexOf(':');
        if (colon >= 0) s = s[..colon];
        int bracket = s.IndexOf('[');
        if (bracket >= 0) s = s[..bracket];
        return s.Trim();
    }

    /// <summary>Rate letter of a variable: from a Csound 7 type annotation (<c>x:k</c>) if present, else from its prefix.</summary>
    static char RateOf(string name, Statement? def)
    {
        string? declared = def?.Outputs.FirstOrDefault(o => BaseName(o) == name);
        if (declared is not null && declared.IndexOf(':') is var c and >= 0 && c + 1 < declared.Length)
            return declared[c + 1];
        string n = name.StartsWith('g') && name.Length > 1 ? name[1..] : name;
        return n.Length > 0 ? n[0] : '?';
    }

    /// <summary>Localized label and explanation for a rate letter.</summary>
    static (string Label, string Help) DescribeRate(char rate) => rate switch
    {
        'a' => (Loc.S("variable de audio (a-rate)", "audio variable (a-rate)"),
                Loc.S("Lleva una señal de audio: un valor por cada muestra, ksmps valores en cada ciclo de control.",
                      "Carries an audio signal: one value per sample, ksmps values in each control cycle.")),
        'k' => (Loc.S("variable de control (k-rate)", "control variable (k-rate)"),
                Loc.S("Se actualiza una vez por ciclo de control (sr / ksmps veces por segundo).",
                      "Updated once per control cycle (sr / ksmps times per second).")),
        'i' => (Loc.S("variable de inicialización (i-rate)", "init-time variable (i-rate)"),
                Loc.S("Se calcula una sola vez, cuando arranca la nota (o el encabezado).",
                      "Computed only once, when the note (or the header) starts.")),
        'S' => (Loc.S("variable de texto (S)", "string variable (S)"), Loc.S("Contiene un string.", "Holds a string.")),
        'f' => (Loc.S("señal espectral (f-sig)", "spectral signal (f-sig)"),
                Loc.S("Flujo de análisis de fase-vocoder (opcodes pvs).", "Phase-vocoder analysis stream (pvs opcodes).")),
        'w' => (Loc.S("señal espectral (w-sig)", "spectral signal (w-sig)"), Loc.S("Formato espectral antiguo.", "Legacy spectral format.")),
        _ => ("variable", Loc.S("Variable de la orquesta.", "Orchestra variable.")),
    };

    /// <summary>Parses an invariant-culture number, or returns <c>null</c>.</summary>
    static double? TryNumber(string? s) =>
        s is not null && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>True if <paramref name="s"/> is a quoted string literal.</summary>
    static bool IsQuoted(string s) => s.Length >= 2 && s[0] == '"' && s[^1] == '"';

    /// <summary>Removes surrounding quotes, if any.</summary>
    static string Unquote(string s) => IsQuoted(s.Trim()) ? s.Trim()[1..^1] : s.Trim();

    /// <summary>Compact invariant formatting used in help texts.</summary>
    /// <remarks>Up to 2 decimals for whole or large numbers, up to 4 otherwise; trailing zeros dropped; always '.' as decimal point.</remarks>
    public static string Format(double v) =>
        v.ToString(Math.Abs(v) >= 1000 || v == Math.Floor(v) ? "0.##" : "0.####", CultureInfo.InvariantCulture);

    /// <summary>Control-flow keywords that can start a line but are not opcodes.</summary>
    static readonly HashSet<string> Keywords = ["if", "elseif", "else", "endif", "fi", "while", "until", "do", "od",
        "then", "goto", "igoto", "kgoto", "instr", "endin", "opcode", "endop", "return", "for", "in", "switch", "case", "default", "endsw"];

    /// <summary>Builds the <see cref="KnownArguments"/> table: (opcode, 0-based argument index) → (min, max, help ES, help EN).</summary>
    /// <remarks>
    /// Ranges are practical slider ranges for musical use, not Csound's hard limits. Amplitudes
    /// assume <c>0dbfs = 1</c>. Help strings start in lower case; the caller capitalises them.
    /// </remarks>
    static Dictionary<(string, int), (double, double, string, string)> BuildKnownArguments()
    {
        var d = new Dictionary<(string, int), (double, double, string, string)>();
        void Add(string op, int i, double lo, double hi, string es, string en) => d[(op, i)] = (lo, hi, es, en);
        foreach (var f in new[] { "moogladder", "moogladder2", "moogvcf", "moogvcf2", "lpf18", "butlp", "buthp", "butterlp", "butterhp", "tone", "atone", "tonex", "zdf_ladder", "diode_ladder", "K35_lpf", "K35_hpf" })
            Add(f, 1, 20, 20000, "frecuencia de corte en Hz", "cutoff frequency in Hz");
        foreach (var f in new[] { "moogladder", "moogladder2", "lpf18", "moogvcf", "moogvcf2" })
            Add(f, 2, 0, 1, "resonancia (0 = nada, cerca de 1 = auto-oscilación)", "resonance (0 = none, close to 1 = self-oscillation)");
        Add("lpf18", 3, 0, 1, "distorsión", "distortion");
        Add("reverbsc", 2, 0, 0.99, "realimentación: tamaño de la sala (0–1)", "feedback: room size (0–1)");
        Add("reverbsc", 3, 100, 20000, "frecuencia de corte del filtro interno en Hz", "cutoff frequency of the internal filter in Hz");
        Add("freeverb", 2, 0, 1, "tamaño de la sala (0–1)", "room size (0–1)");
        Add("freeverb", 3, 0, 1, "amortiguación de agudos (0–1)", "high-frequency damping (0–1)");
        Add("pan2", 1, 0, 1, "posición estéreo: 0 = izquierda, 0.5 = centro, 1 = derecha", "stereo position: 0 = left, 0.5 = center, 1 = right");
        foreach (var f in new[] { "vco2", "oscili", "oscil", "oscil3", "poscil", "poscil3", "pluck", "lfo", "buzz", "foscili", "rand", "randi", "randh", "noise" })
            Add(f, 0, 0, 1, "amplitud (con 0dbfs = 1)", "amplitude (with 0dbfs = 1)");
        foreach (var f in new[] { "vco2", "oscili", "oscil", "oscil3", "poscil", "poscil3", "pluck", "buzz" })
            Add(f, 1, 1, 20000, "frecuencia en Hz", "frequency in Hz");
        Add("lfo", 1, 0.01, 50, "frecuencia del LFO en Hz", "LFO frequency in Hz");
        foreach (var f in new[] { "madsr", "adsr", "mxadsr", "xadsr" })
        {
            Add(f, 0, 0, 10, "tiempo de ataque en segundos", "attack time in seconds");
            Add(f, 1, 0, 10, "tiempo de decaimiento en segundos", "decay time in seconds");
            Add(f, 2, 0, 1, "nivel de sustain (0–1)", "sustain level (0–1)");
            Add(f, 3, 0, 10, "tiempo de relajación en segundos", "release time in seconds");
        }
        Add("metro", 0, 0.01, 50, "pulsos por segundo (Hz)", "pulses per second (Hz)");
        foreach (var f in new[] { "port", "portk" })
            Add(f, 1, 0, 2, "tiempo de medio camino en segundos (cuánto suaviza)", "half-time in seconds (how much it smooths)");
        Add("delayr", 0, 0, 10, "largo máximo del delay en segundos", "maximum delay length in seconds");
        Add("schedulek", 1, 0, 60, "retardo de inicio en segundos", "start delay in seconds");
        Add("schedulek", 2, 0, 60, "duración en segundos", "duration in seconds");
        Add("schedule", 1, 0, 60, "retardo de inicio en segundos", "start delay in seconds");
        Add("schedule", 2, 0, 60, "duración en segundos", "duration in seconds");
        return d;
    }

    // Source-generated regular expressions (compiled at build time, no runtime Regex construction).

    /// <summary>Unsigned decimal literal with optional exponent: <c>440</c>, <c>0.25</c>, <c>.5</c>, <c>1e-3</c>. The sign is handled separately.</summary>
    [GeneratedRegex(@"^(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$")]
    private static partial Regex NumberLiteral();

    /// <summary>A p-field reference <c>p</c>+digits; group 1 is the number.</summary>
    [GeneratedRegex(@"^p(\d+)$")]
    private static partial Regex PFieldName();

    /// <summary>Spelled like a variable: optional <c>g</c>, a rate letter (a i k S f w) and at least one more character.</summary>
    [GeneratedRegex(@"^g?[aikSfw][A-Za-z0-9_]+$")]
    private static partial Regex VariableName();

    /// <summary>Spelled like a global variable (<c>g</c> + rate letter …).</summary>
    [GeneratedRegex(@"^g[aikSfw][A-Za-z0-9_]*$")]
    private static partial Regex GlobalVariable();

    /// <summary>
    /// Assignment line: <c>out</c> (with optional Csound 7 <c>:type</c> and <c>[]</c>), an
    /// operator <c>= += -= *= /=</c> not followed by another <c>=</c> (so <c>==</c> comparisons in
    /// <c>if</c> lines do not match), and the expression.
    /// </summary>
    [GeneratedRegex(@"^\s*(?<out>[A-Za-z_]\w*(?::\w+)?(?:\[\])*)\s*(?<op>\+=|-=|\*=|/=|=)(?!=)(?<expr>.*)$")]
    private static partial Regex Assignment();

    /// <summary>
    /// Classic syntax: a comma-separated output list, whitespace, the opcode (optional
    /// <c>:rate</c> annotation, not captured) and the rest as arguments. Ambiguous with
    /// "opcode arg1, arg2" lines; <see cref="ParseStatement"/> resolves that.
    /// </summary>
    [GeneratedRegex(@"^\s*(?<outs>(?:[A-Za-z_]\w*(?::\w+)?(?:\[\])*\s*,\s*)*[A-Za-z_]\w*(?::\w+)?(?:\[\])*)\s+(?<opc>[A-Za-z_]\w*)(?::\w+)?\b(?<args>.*)$")]
    private static partial Regex Classic();

    /// <summary>A line that is just an opcode, optionally followed by arguments.</summary>
    [GeneratedRegex(@"^\s*(?<opc>[A-Za-z_]\w*)(?::\w+)?\b(?<args>.*)$")]
    private static partial Regex OpcodeOnly();

    /// <summary>A goto label on its own line (<c>loop:</c>).</summary>
    [GeneratedRegex(@"^\s*\w+:\s*$")]
    private static partial Regex Label();

    /// <summary>A whole expression that is a single function call <c>name(args)</c> / <c>name:k(args)</c>; groups: name, argument text.</summary>
    [GeneratedRegex(@"^([A-Za-z_]\w*)(?::\w+)?\((.*)\)$")]
    private static partial Regex FunctionCall();

    /// <summary><c>instr</c>/<c>opcode</c> header; group 1 keyword, group 2 the name/id list (trailing <c>;</c> comment removed).</summary>
    [GeneratedRegex(@"^\s*(instr|opcode)\s+(.+?)\s*(?:;.*)?$")]
    private static partial Regex BlockStart();

    /// <summary><c>endin</c>/<c>endop</c> at the start of a line.</summary>
    [GeneratedRegex(@"^\s*(endin|endop)\b")]
    private static partial Regex BlockEnd();
}
