using System.Globalization;
using System.Text.RegularExpressions;

namespace CsoundVisual.Engine;

/// <summary>A control channel declared or read by the orchestra, with the range used for its slider.</summary>
/// <param name="Name">Channel name.</param>
/// <param name="Default">Initial value.</param>
/// <param name="Minimum">Lower bound of the slider.</param>
/// <param name="Maximum">Upper bound of the slider.</param>
/// <param name="Exponential">True when declared with <c>chnk</c> itype 3 (exponential range).</param>
/// <param name="FromDeclaration">True when the range comes from a <c>chnk</c> declaration, false when it was guessed.</param>
public sealed record ChannelSpec(string Name, double Default, double Minimum, double Maximum,
    bool Exponential = false, bool FromDeclaration = false);

/// <summary>Text utilities over <c>.csd</c> documents that do not need the full analyzer.</summary>
/// <remarks>
/// <para>
/// Pure, thread-safe string functions (regex-based, no Csound instance). Used by
/// <c>MainViewModel</c> before playing or rendering a document (<see cref="ExtractOptions"/>: the
/// file's options are handed to the engine, which passes them to <c>csoundSetOption</c> together
/// with its own, after this class dropped the ones the app controls) and by <c>DocumentViewModel</c> to build the slider list
/// (<see cref="DetectChannels"/>).
/// </para>
/// <para>
/// The heuristics are deliberately shallow; <c>Editor/CsoundAnalyzer</c> is the full analyzer.
/// </para>
/// </remarks>
public static partial class CsdTools
{
    /// <summary>Short flags that take their value as a separate token (<c>-b 512</c>), so the two are joined.</summary>
    /// <remarks>
    /// Joining keeps each value with its flag, so the list holds one entry per option and the
    /// output filter drops a flag together with its value (<c>-o out.wav</c> → <c>-oout.wav</c>,
    /// removed as a whole). Covers -b/-B (buffer sizes), -k/-r (rates),
    /// -m (message level), -i (input), -M (MIDI device), -Q, -F (MIDI file), -L, -j, -t (tempo).
    /// </remarks>
    const string ShortFlagsWithValue = "bBkrmiMQFLjt";

    /// <summary>
    /// Removes the content of the <c>&lt;CsOptions&gt;</c> block and returns its options, minus
    /// the output related ones (<c>-o</c>, <c>-W</c>, <c>-A</c>, <c>-n</c>, <c>-d</c>…), which the
    /// application decides itself depending on whether it plays or renders.
    /// </summary>
    /// <remarks>
    /// The removed content is replaced with the same number of line breaks so line numbers in
    /// Csound's error messages still match the editor.
    /// <para>
    /// Lines starting with <c>;</c> or <c>#</c> are treated as comments. A leading <c>csound</c>
    /// token and any <c>*.csd</c> file name are ignored, as are bare non-option tokens.
    /// </para>
    /// </remarks>
    /// <param name="text">The whole <c>.csd</c> text.</param>
    /// <returns>
    /// The text with the <c>&lt;CsOptions&gt;</c> content blanked (tags kept), and the remaining
    /// options, one per entry (the engine later joins them with spaces into a single
    /// <c>csoundSetOption</c> call, which Csound 7 splits on spaces; a value with spaces keeps its
    /// double quotes glued to the flag, the form Csound accepts — see <see cref="SplitOptionTokens"/>).
    /// Without a block, the text is
    /// returned unchanged with an empty list.
    /// </returns>
    public static (string Csd, List<string> Options) ExtractOptions(string text)
    {
        var options = new List<string>();
        var m = CsOptionsBlock().Match(text);
        if (!m.Success)
            return (text, options);

        string block = m.Groups[1].Value;
        var tokens = block
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l[0] != ';' && l[0] != '#')
            // Quote-aware split: «-i "C:\My Samples\in.wav"» must stay one value (see SplitOptionTokens).
            .SelectMany(SplitOptionTokens)
            .ToList();

        for (int i = 0; i < tokens.Count; i++)
        {
            string t = tokens[i];
            if (i == 0 && t.Equals("csound", StringComparison.OrdinalIgnoreCase))
                continue;
            if (t.EndsWith(".csd", StringComparison.OrdinalIgnoreCase) || !t.StartsWith('-'))
                continue;

            bool takesValue = t.Length == 2 && ShortFlagsWithValue.Contains(t[1])
                              && i + 1 < tokens.Count && !tokens[i + 1].StartsWith('-');
            if (takesValue)
                t += tokens[++i];

            if (IsOutputOption(t))
                continue;
            options.Add(t);
        }

        string newlines = new('\n', block.Count(c => c == '\n'));
        string csd = text.Remove(m.Groups[1].Index, block.Length).Insert(m.Groups[1].Index, newlines);
        return (csd, options);
    }

    /// <summary>
    /// Splits one <c>&lt;CsOptions&gt;</c> line into tokens on blanks, except inside double quotes,
    /// keeping the quotes in the token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bug fix: the line used to be split on every space, so a quoted path such as
    /// <c>-i "C:\My Samples\in.wav"</c> or <c>--env:SSDIR="C:\My Samples"</c> was cut in two (the
    /// second half was then dropped as a bare non-option token) and Csound got a broken path.
    /// </para>
    /// <para>
    /// The quotes are deliberately kept: verified against the Csound 7.0 DLL, <c>csoundSetOption</c>
    /// splits its string on spaces but honours double quotes glued to the flag, so
    /// <c>-o"C:\a b\x.wav"</c> and <c>--output="C:\a b\x.wav"</c> (alone or among other options in
    /// the same string) write the right file, whereas <c>-o "C:\a b\x.wav"</c> (quote after a
    /// space) does not. <see cref="ExtractOptions"/> glues a short flag to its separate value
    /// without a space (<c>-i</c> + <c>"a b.wav"</c> → <c>-i"a b.wav"</c>), which is exactly the
    /// accepted form, so the engine can keep joining the options with spaces.
    /// </para>
    /// <para>An unterminated quote simply runs to the end of the line.</para>
    /// </remarks>
    /// <param name="line">One trimmed, non-comment line of the block.</param>
    /// <returns>The tokens in order; never empty strings.</returns>
    static IEnumerable<string> SplitOptionTokens(string line)
    {
        var token = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (char c in line)
        {
            if (c == '"')
                inQuotes = !inQuotes;
            else if (!inQuotes && c is ' ' or '\t' or '\r')
            {
                if (token.Length > 0)
                {
                    yield return token.ToString();
                    token.Clear();
                }
                continue;
            }
            token.Append(c);
        }
        if (token.Length > 0)
            yield return token.ToString();
    }

    /// <summary>True for options that select the output target or displays, which the app overrides.</summary>
    /// <remarks>
    /// <c>-o</c>/<c>--output</c> (destination), <c>-+rtaudio</c> (the app picks the audio module),
    /// <c>-W/-A/-J/-h/--wave/--aiff</c> (file type, only meaningful for renders), <c>-n/--nosound</c>
    /// (no output), <c>-d/--nodisplays</c> (display handling) and <c>-+rtmidi</c> (see below).
    /// </remarks>
    static bool IsOutputOption(string t) =>
        (t.StartsWith("-o") && !t.StartsWith("--")) ||
        t.StartsWith("--output") ||
        t.StartsWith("-+rtaudio") ||
        // MIDI input is provided by the app (host MIDI): Cabbage instruments ship «-+rtmidi=NULL»,
        // which would otherwise switch the app's MIDI (and the on-screen keyboards) off.
        t.StartsWith("-+rtmidi") ||
        t is "-W" or "-A" or "-J" or "-h" or "-n" or "-d" or "--wave" or "--aiff" or "--nosound" or "--nodisplays";

    /// <summary>
    /// Detects the control channels of the orchestra: <c>chnk</c>/<c>chn_k</c> declarations (with
    /// their range when itype is 1–3) and <c>chnget "name"</c> reads (range guessed from the name).
    /// </summary>
    /// <param name="text">The whole <c>.csd</c> (or orchestra) text. Comments are not stripped, so a commented-out declaration still counts.</param>
    /// <returns>One spec per distinct name; a declaration wins over a later <c>chnget</c> of the same name.</returns>
    /// <remarks>
    /// Csound 7 renamed <c>chn_k</c> to <c>chnk</c>; both spellings are matched. In <c>chnk</c>,
    /// itype 2 is linear and itype 3 exponential (minimum must be &gt; 0).
    /// </remarks>
    public static List<ChannelSpec> DetectChannels(string text)
    {
        var found = new Dictionary<string, ChannelSpec>(StringComparer.Ordinal);

        foreach (Match m in ChnK().Matches(text))
        {
            string name = m.Groups[1].Value;
            // Group 2 is ",imode,itype,idflt,imin,imax"; non-numeric arguments (variables,
            // expressions) become NaN and make the declaration fall back to a guessed range.
            double[] args = m.Groups[2].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN)
                .ToArray();
            // chnk "name", imode, itype, idflt, imin, imax   (itype 1 = integer, 2 = linear, 3 = exponential)
            if (args.Length >= 5 && args.Take(5).All(double.IsFinite) && args[1] is >= 1 and <= 3 && args[4] > args[3])
                found[name] = new ChannelSpec(name, Math.Clamp(args[2], args[3], args[4]), args[3], args[4],
                    Exponential: args[1] == 3, FromDeclaration: true);
            else
                found[name] = Guess(name);
        }

        foreach (Match m in ChnGet().Matches(text))
            found.TryAdd(m.Groups[1].Value, Guess(m.Groups[1].Value));

        return [.. found.Values];
    }

    /// <summary>Guesses a sensible slider range from the channel name when none was declared.</summary>
    /// <returns>Frequency-like names: 20–8000 Hz (default 1000); tempo-like: 40–240 BPM (120); anything else: 0–1 (0.5).</returns>
    static ChannelSpec Guess(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("freq") || n.Contains("cutoff") || n.Contains("hz"))
            return new(name, 1000, 20, 8000);
        if (n.Contains("bpm") || n.Contains("tempo"))
            return new(name, 120, 40, 240);
        return new(name, 0.5, 0, 1);
    }

    /// <summary>Captures the content between <c>&lt;CsOptions&gt;</c> and <c>&lt;/CsOptions&gt;</c>.</summary>
    [GeneratedRegex(@"<CsOptions>(.*?)</CsOptions>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CsOptionsBlock();

    /// <summary>Captures the channel name and up to five arguments of a <c>chnk</c>/<c>chn_k</c> declaration.</summary>
    [GeneratedRegex(@"\bchn_?k\s*\(?\s*""([^""]+)""\s*((?:,[^,\n;)]*){0,5})")]
    private static partial Regex ChnK();

    /// <summary>Captures the channel name read by <c>chnget</c> (classic and functional syntax).</summary>
    [GeneratedRegex(@"\bchnget(?::[ika])?\s*\(?\s*""([^""]+)""")]
    private static partial Regex ChnGet();
}
