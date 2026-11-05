using System.Collections.Frozen;
using System.Text;
using CsoundVisual.Interop;
using CsoundVisual.Localization;

namespace CsoundVisual.Editor;

/// <summary>
/// Process-wide index of the opcodes known to the loaded Csound, used by the analyzer (to tell
/// variables from opcodes), by the hover help (signatures) and by syntax highlighting.
/// </summary>
/// <remarks>
/// The real list (≈2500 names) is loaded once in the background with
/// <see cref="CsoundCatalog.LoadOpcodes"/>. Until it arrives — or if Csound is missing — a small
/// built-in list of common opcodes is used so the editor still behaves sensibly.
/// <para>
/// Threading: <see cref="LoadFromCsound"/> runs on a background thread (it creates a short-lived
/// Csound instance) and publishes the result by replacing <see cref="Signatures"/> with a new
/// immutable <see cref="FrozenDictionary{TKey, TValue}"/> in a single reference assignment, so UI
/// readers see either the empty dictionary or the complete one, never a partial list. The
/// <see cref="Loaded"/> event lets the main window refresh highlighting once the list arrives
/// (subscribers must marshal to the UI thread themselves).
/// </para>
/// <para>
/// Consumers: <see cref="CsoundAnalyzer"/> (opcode vs. variable), <see cref="CompletionProvider"/>
/// (names and signatures), the hover card (<see cref="FormatSignature"/>,
/// <see cref="DescribeType"/>) and the syntax highlighter (<see cref="Names"/>).
/// </para>
/// </remarks>
public static class OpcodeIndex
{
    /// <summary>
    /// Common opcodes recognised before the real list is loaded (or when no Csound installation
    /// was found). Includes <c>chn_k</c>, the pre-Csound-7 spelling of <c>chnk</c>, so old files
    /// are still analysed sensibly.
    /// </summary>
    static readonly FrozenSet<string> Fallback = new[]
    {
        "oscil", "oscili", "oscil3", "poscil", "poscil3", "vco", "vco2", "pluck", "foscil", "foscili", "buzz", "gbuzz",
        "noise", "rand", "randi", "randh", "random", "rnd", "birnd", "line", "linseg", "linsegr", "expon", "expseg",
        "expsegr", "transeg", "adsr", "madsr", "mxadsr", "xadsr", "linen", "linenr", "port", "portk", "lag", "lagud",
        "moogladder", "moogladder2", "moogvcf", "lpf18", "butlp", "buthp", "butbp", "butterlp", "butterhp", "butterbp",
        "tone", "atone", "reson", "resonz", "delay", "delayr", "delayw", "deltap", "deltapi", "deltap3", "vdelay",
        "comb", "alpass", "reverb", "reverbsc", "freeverb", "out", "outs", "outch", "in", "ins", "pan2",
        "chnget", "chnset", "chnk", "chn_k", "chnexport", "chnmix", "chnclear", "clear", "soundin", "diskin",
        "diskin2", "loscil", "table", "tablei", "table3", "tab", "tab_i", "tablew", "ftgen", "init", "print", "printk",
        "printks", "prints", "printf", "event", "event_i", "schedule", "schedulek", "schedkwhen", "turnoff", "metro",
        "trigger", "changed", "mtof", "cpsmidinn", "cpspch", "cpsoct", "octpch", "ampdb", "dbamp", "ampdbfs", "limit",
        "clip", "int", "frac", "abs", "exp", "log", "sqrt", "sin", "cos", "lfo", "vibr", "vibrato", "massign",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>All opcode overloads keyed by name; empty until <see cref="LoadFromCsound"/> completes.</summary>
    public static FrozenDictionary<string, OpcodeSignature[]> Signatures { get; private set; } =
        FrozenDictionary<string, OpcodeSignature[]>.Empty;

    /// <summary>True once the real list from Csound is available.</summary>
    public static bool IsLoaded => Signatures.Count > 0;

    /// <summary>Raised (on the loading thread) when the real list has been loaded.</summary>
    public static event Action? Loaded;

    /// <summary>Loads the opcode list from Csound. Blocking; call it from a background thread.</summary>
    /// <remarks>
    /// Does nothing (and does not raise <see cref="Loaded"/>) when Csound returned no opcodes, so
    /// the fallback list stays in use. Requires <see cref="CsoundLocator.Initialize"/> to have
    /// succeeded first.
    /// </remarks>
    public static void LoadFromCsound()
    {
        var list = CsoundCatalog.LoadOpcodes();
        if (list.Count == 0)
            return;
        Signatures = list;
        Loaded?.Invoke();
    }

    /// <summary>True if <paramref name="name"/> is an opcode (case-sensitive, like Csound).</summary>
    public static bool IsOpcode(string name) => Signatures.ContainsKey(name) || Fallback.Contains(name);

    /// <summary>Overloads of <paramref name="name"/>, or an empty array.</summary>
    public static OpcodeSignature[] Get(string name) => Signatures.GetValueOrDefault(name) ?? [];

    /// <summary>All names usable for syntax highlighting (real list when loaded, otherwise the fallback).</summary>
    public static IEnumerable<string> Names => IsLoaded ? Signatures.Keys : Fallback;

    /// <summary>
    /// Converts a Csound type string (e.g. <c>"akoO"</c>) into readable tokens
    /// (<c>["a", "k", "[i]", "[k]"]</c>). Used for signatures and argument help.
    /// </summary>
    /// <remarks>
    /// Csound's input type letters: <c>a k i S f w</c> are the basic rates; <c>o p q v j h</c>
    /// optional i-rate; <c>O J V P</c> optional k-rate; <c>m M N n y z Z W *</c> variable-length
    /// lists; <c>x</c> a-or-k; <c>T U</c> string-or-number; <c>.</c> any type; <c>:Name;</c> a named
    /// type (Csound 7); <c>[]</c> after a letter marks an array.
    /// </remarks>
    /// <param name="types">An input or output type string as reported by Csound's opcode list.</param>
    /// <returns>One readable token per argument: optional arguments in brackets (<c>[i]</c>),
    /// variable-length lists with an ellipsis (<c>k…</c>), arrays with a <c>[]</c> suffix.</returns>
    public static List<string> ReadableTypes(string types)
    {
        var result = new List<string>();
        for (int i = 0; i < types.Length; i++)
        {
            char c = types[i];
            string token;
            if (c == ':')
            {
                // Csound 7 named (struct) type ":Name;" — take the name verbatim and jump past ';'.
                int end = types.IndexOf(';', i + 1);
                if (end < 0) end = types.Length;
                token = types[(i + 1)..end];
                i = end;
            }
            else
            {
                token = c switch
                {
                    'a' or 'k' or 'i' or 'S' or 'f' or 'w' or 'b' => c.ToString(),
                    'B' => "b",
                    'x' => "a|k",
                    'T' => "S|i",
                    'U' => "S|k|i",
                    'l' => Loc.S("etiqueta", "label"),
                    'o' or 'p' or 'q' or 'v' or 'j' or 'h' => "[i]",
                    'O' or 'J' or 'V' or 'P' => "[k]",
                    'm' or 'n' => "i…",
                    'y' => "a…",
                    'z' => "k…",
                    'Z' => "k,a…",
                    'W' => "S…",
                    'M' or 'N' or '*' => "*…",
                    '.' or '?' => "*",
                    _ => c.ToString(),
                };
            }
            // Array suffix: "k[]" (or a multi-dimensional "k[][]" up to the first ']') after the letter.
            if (i + 1 < types.Length && types[i + 1] == '[')
            {
                int close = types.IndexOf(']', i + 1);
                if (close > 0) { token += "[]"; i = close; }
            }
            result.Add(token);
        }
        return result;
    }

    /// <summary>Formats one overload as <c>"a  ←  moogladder(a, k, k, [i])"</c>.</summary>
    /// <param name="name">Opcode name to print.</param>
    /// <param name="sig">The overload; outputs are omitted when it has none, and a localized
    /// "(deprecated)" suffix is added when Csound flags it.</param>
    public static string FormatSignature(string name, OpcodeSignature sig)
    {
        var sb = new StringBuilder();
        var outs = ReadableTypes(sig.OutTypes);
        if (outs.Count > 0)
            sb.Append(string.Join(", ", outs)).Append("  ←  ");
        sb.Append(name).Append('(').Append(string.Join(", ", ReadableTypes(sig.InTypes))).Append(')');
        if (sig.Deprecated)
            sb.Append(Loc.S("   (obsoleto)", "   (deprecated)"));
        return sb.ToString();
    }

    /// <summary>Localized description of a readable type token, for argument help.</summary>
    /// <param name="token">A token produced by <see cref="ReadableTypes"/>.</param>
    /// <returns>A description such as "control (k-rate), optional"; unknown tokens (lists, arrays,
    /// named types) are returned unchanged apart from the optional suffix.</returns>
    public static string DescribeType(string token) => token.TrimStart('[').TrimEnd(']', '…') switch
    {
        "a" => "audio (a-rate)",
        "k" => "control (k-rate)",
        "i" => Loc.S("inicialización (i-rate)", "init-time (i-rate)"),
        "S" => Loc.S("texto", "string"),
        "f" => Loc.S("señal espectral", "spectral signal (f-sig)"),
        "a|k" => Loc.S("audio o control", "audio or control"),
        _ => token,
    } + (token.StartsWith('[') ? Loc.S(", opcional", ", optional") : "");
}
