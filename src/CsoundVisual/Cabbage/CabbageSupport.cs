using System.Text;
using System.Text.RegularExpressions;
using CsoundVisual.Engine;

namespace CsoundVisual.Cabbage;

/// <summary>
/// Glue between Cabbage instruments and Csound Visual: turns widgets into channel specs (so the
/// ordinary channel machinery — initial values on start, MIDI learn, the VST3 link — covers them)
/// and prepares a Cabbage .csd so plain Csound 7 can run it.
/// </summary>
/// <remarks>
/// <para>Three jobs, all pure text/data functions with no UI:</para>
/// <list type="number">
/// <item><b>Channels.</b> <see cref="ChannelSpecs"/>/<see cref="Merge"/> turn widgets into
/// <see cref="ChannelSpec"/>s, the same records <c>CsdTools.DetectChannels</c> produces for
/// <c>chnk</c>/<c>chnget</c> lines. The document view model builds one
/// <c>ChannelControlViewModel</c> per spec, and the Cabbage widgets (<see cref="CabbagePanel"/>)
/// bind to those view models, so a widget and its side-panel slider are the same channel.</item>
/// <item><b>Running.</b> <see cref="PrepareForCsound"/> injects UDO stand-ins for the opcodes only
/// the Cabbage host provides (<c>cabbageGetValue</c>, …) so plain Csound compiles the file.</item>
/// <item><b>Annotations.</b> <see cref="FromAnnotations"/> builds an automatic form from
/// <c>;@knob</c>-style comments in ordinary documents (no <c>&lt;Cabbage&gt;</c> section needed).</item>
/// </list>
/// </remarks>
public static partial class CabbageSupport
{
    /// <summary>Widget types that only display values written by the orchestra (no channel spec).</summary>
    static readonly HashSet<string> DisplayOnly = new(StringComparer.Ordinal) { "hmeter", "vmeter", "label", "image", "groupbox", "line", "keyboard", "csoundoutput", "infobutton", "gentable", "soundfiler", "signaldisplay" };

    /// <summary>
    /// The numeric channels the widgets of <paramref name="form"/> drive, with Cabbage's range and
    /// initial value. A channel used by several widgets keeps the first declaration.
    /// </summary>
    /// <param name="form">Parsed form.</param>
    /// <returns>One spec per distinct numeric channel, in depth-first widget order.</returns>
    /// <remarks>
    /// String channels (file buttons, text editors) and display-only widgets are skipped: they
    /// have no numeric value to send. A range is flagged exponential when Cabbage's skew is clearly
    /// logarithmic-looking (&lt; 0.6) and the minimum is positive (an exponential map needs min &gt; 0).
    /// </remarks>
    public static List<ChannelSpec> ChannelSpecs(CabbageForm form)
    {
        var result = new List<ChannelSpec>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in form.AllWidgets())
        {
            if (w.IsStringChannel || DisplayOnly.Contains(w.Type) || w.Channels.Count == 0)
                continue;
            for (int i = 0; i < w.Channels.Count; i++)
            {
                var r = RangeOf(w, i);
                if (seen.Add(w.Channels[i]))
                    result.Add(new ChannelSpec(w.Channels[i], r.Value, r.Min, r.Max,
                        Exponential: r.Skew < 0.6 && r.Min > 0, FromDeclaration: true));
            }
        }
        return result;
    }

    /// <summary>
    /// The range (with the initial value in <see cref="CabbageRange.Value"/>) of channel number
    /// <paramref name="index"/> of a widget, following Cabbage's defaults for each widget type.
    /// The result always has <c>Max &gt; Min</c>.
    /// </summary>
    /// <param name="w">The widget.</param>
    /// <param name="index">Channel index: 0 = first (X of an XY pad, low end of a range slider), 1 = second.</param>
    /// <returns>A non-degenerate range whose <see cref="CabbageRange.Value"/> is clamped inside it.</returns>
    public static CabbageRange RangeOf(CabbageWidget w, int index = 0)
    {
        CabbageRange r;
        // At least two choices so a list-less combobox still has a valid 1…2 range.
        int choices = Math.Max(2, w.Choices.Count);
        switch (w.Type)
        {
            case "button":
            case "checkbox":
                // On/off: 0 or 1, step 1.
                r = new CabbageRange(0, 1, w.Value ?? 0, 1, 1);
                break;
            case "combobox":
                // Cabbage comboboxes are 1-based: item n sends n.
                r = new CabbageRange(1, choices, w.Value ?? 1, 1, 1);
                break;
            case "optionbutton":
                // Option buttons cycle through their texts and are 0-based.
                r = new CabbageRange(0, choices - 1, w.Value ?? 0, 1, 1);
                break;
            case "xypad":
                r = (index == 0 ? w.RangeX : w.RangeY) ?? w.Range ?? new CabbageRange(0, 1, 0.5);
                break;
            case "hrange":
            case "vrange":
                // Both channels share one range; the second channel's initial value is Value2
                // ("range(0, 1, 0.2:0.8)"), or the top of the range when none was given.
                r = w.Range ?? new CabbageRange(0, 1, 0, 1, 0.01, 1);
                if (index == 1) r = r with { Value = double.IsNaN(r.Value2) ? r.Max : r.Value2 };
                break;
            case "encoder":
            {
                // Encoders are unbounded in Cabbage; give them a usable window around the value
                // (0…2×value) unless min()/max()/range() say otherwise.
                double v = w.Value ?? w.Range?.Value ?? 0;
                double min = w.MinOverride ?? w.Range?.Min ?? Math.Min(0, v);
                double max = w.MaxOverride ?? w.Range?.Max ?? Math.Max(min + 1, v * 2);
                r = new CabbageRange(min, max, v, 1, w.IncrementOverride ?? w.Range?.Increment ?? 0.01);
                break;
            }
            default:
                // Sliders: range(…) wins; otherwise old-style min/max/increment and value(…).
                r = w.Range ?? new CabbageRange(w.MinOverride ?? 0, w.MaxOverride ?? 1, 0, 1, w.IncrementOverride ?? 0.01);
                if (w.Value is { } value && w.Range is null) r = r with { Value = value };
                break;
        }
        // Repair inverted (swap) or empty (widen by 1) ranges so callers can divide by Max − Min.
        if (!(r.Max > r.Min))
            r = r.Max < r.Min ? r with { Min = r.Max, Max = r.Min } : r with { Max = r.Min + 1 };
        return r with { Value = Math.Clamp(r.Value, r.Min, r.Max) };
    }

    /// <summary>
    /// Merges the channels found in the orchestra (<paramref name="detected"/>) with those of the
    /// Cabbage widgets: widget channels come first, in widget order, with Cabbage's range and
    /// default; channels only the orchestra mentions follow.
    /// </summary>
    /// <param name="detected">Channels found by scanning the orchestra (<c>chnk</c>, <c>chnget</c>…).</param>
    /// <param name="form">The document's form, or <c>null</c> (then <paramref name="detected"/> is returned as is).</param>
    /// <returns>The channel list the side panel and the widgets share.</returns>
    public static List<ChannelSpec> Merge(List<ChannelSpec> detected, CabbageForm? form)
    {
        if (form is null) return detected;
        var cabbage = ChannelSpecs(form);
        var names = new HashSet<string>(cabbage.Select(c => c.Name), StringComparer.Ordinal);
        return [.. cabbage, .. detected.Where(d => !names.Contains(d.Name))];
    }

    /// <summary>Names of the channels driven by widgets (used to mark the ordinary sliders).</summary>
    public static HashSet<string> WidgetChannels(CabbageForm? form) =>
        form is null ? [] : new HashSet<string>(ChannelSpecs(form).Select(c => c.Name), StringComparer.Ordinal);

    // ------------------------------------------------------------------------------------------
    //  Running a Cabbage .csd with plain Csound
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Stand-ins for the opcodes the Cabbage host adds to Csound, written as UDOs over the channel
    /// bus. Values work exactly as in Cabbage (widgets write channels; <c>cabbageGetValue</c> reads
    /// them); widget attribute changes (<c>cabbageSet</c>) are accepted and ignored.
    /// </summary>
    /// <remarks>
    /// Each entry holds every overload Csound needs to resolve the call forms used in real Cabbage
    /// instruments (k/i/S outputs, with or without a trigger). <c>changed2</c> provides the
    /// "value changed" trigger. The two-string <c>cabbageGet</c> forms (reading a widget
    /// <i>attribute</i>) return neutral dummies (0, empty arrays, "") because no widget state
    /// is exposed to the orchestra. The text is raw Csound code inserted verbatim.
    /// </remarks>
    static readonly (string Opcode, string Code)[] Shims =
    [
        ("cabbageGetValue", """
            opcode cabbageGetValue, k, S
              Sc xin
              kv chnget Sc
              xout kv
            endop
            opcode cabbageGetValue, kk, S
              Sc xin
              kv chnget Sc
              kt changed2 kv
              xout kv, kt
            endop
            opcode cabbageGetValue, i, S
              Sc xin
              iv chnget Sc
              xout iv
            endop
            opcode cabbageGetValue, S, S
              Sc xin
              Sv chnget Sc
              xout Sv
            endop
            opcode cabbageGetValue, Sk, S
              Sc xin
              Sv chnget Sc
              kt changed2 Sv
              xout Sv, kt
            endop
            """),
        ("cabbageGet", """
            opcode cabbageGet, k, S
              Sc xin
              kv chnget Sc
              xout kv
            endop
            opcode cabbageGet, kk, S
              Sc xin
              kv chnget Sc
              kt changed2 kv
              xout kv, kt
            endop
            opcode cabbageGet, i, S
              Sc xin
              iv chnget Sc
              xout iv
            endop
            opcode cabbageGet, S, S
              Sc xin
              Sv chnget Sc
              xout Sv
            endop
            opcode cabbageGet, Sk, S
              Sc xin
              Sv chnget Sc
              kt changed2 Sv
              xout Sv, kt
            endop
            opcode cabbageGet, i, SS
              Sc, Si xin
              xout 0
            endop
            opcode cabbageGet, i[], SS
              Sc, Si xin
              iv[] init 4
              xout iv
            endop
            opcode cabbageGet, S[], SS
              Sc, Si xin
              Sv[] init 2
              xout Sv
            endop
            opcode cabbageGet, k, SS
              Sc, Si xin
              xout k(0)
            endop
            opcode cabbageGet, S, SS
              Sc, Si xin
              xout ""
            endop
            """),
        ("cabbageSetValue", """
            opcode cabbageSetValue, 0, Sk
              Sc, kv xin
              chnset kv, Sc
            endop
            opcode cabbageSetValue, 0, Skk
              Sc, kv, kt xin
              if kt != 0 then
                chnset kv, Sc
              endif
            endop
            opcode cabbageSetValue, 0, SS
              Sc, Sv xin
              chnset Sv, Sc
            endop
            opcode cabbageSetValue, 0, SSk
              Sc, Sv, kt xin
              if kt != 0 then
                chnset Sv, Sc
              endif
            endop
            """),
        ("cabbageSet", """
            opcode cabbageSet, 0, SS
              Sc, Si xin
            endop
            opcode cabbageSet, 0, SSS
              Sc, Si, Sa xin
            endop
            opcode cabbageSet, 0, SSi
              Sc, Si, i1 xin
            endop
            opcode cabbageSet, 0, SSii
              Sc, Si, i1, i2 xin
            endop
            opcode cabbageSet, 0, SSiii
              Sc, Si, i1, i2, i3 xin
            endop
            opcode cabbageSet, 0, SSiiii
              Sc, Si, i1, i2, i3, i4 xin
            endop
            opcode cabbageSet, 0, kSS
              kt, Sc, Si xin
            endop
            opcode cabbageSet, 0, kSSS
              kt, Sc, Si, Sa xin
            endop
            opcode cabbageSet, 0, kSSk
              kt, Sc, Si, k1 xin
            endop
            opcode cabbageSet, 0, kSSkk
              kt, Sc, Si, k1, k2 xin
            endop
            opcode cabbageSet, 0, kSSkkk
              kt, Sc, Si, k1, k2, k3 xin
            endop
            opcode cabbageSet, 0, kSSkkkk
              kt, Sc, Si, k1, k2, k3, k4 xin
            endop
            """),
        ("cabbageChanged", """
            opcode cabbageChanged, k, S
              Sc xin
              kv chnget Sc
              kt changed2 kv
              xout kt
            endop
            opcode cabbageChanged, k, Sk
              Sc, kth xin
              kv chnget Sc
              kt changed2 kv
              xout kt
            endop
            """),
    ];

    /// <summary>
    /// Prepares a .csd (already without its <c>&lt;CsOptions&gt;</c>) for plain Csound 7: when the
    /// orchestra uses Cabbage-only opcodes (<c>cabbageGetValue</c>, <c>cabbageSetValue</c>,
    /// <c>cabbageGet</c>, <c>cabbageSet</c>, <c>cabbageChanged</c>) and does not define them, UDO
    /// stand-ins are inserted right after <c>&lt;CsInstruments&gt;</c>, followed by a
    /// <c>#line</c> directive so Csound's error line numbers still match the editor.
    /// </summary>
    /// <param name="csd">The .csd text that will be compiled.</param>
    /// <param name="notes">Messages for the console (what was added, which Cabbage opcodes cannot work).</param>
    /// <returns>The text to compile (unchanged when nothing was needed).</returns>
    /// <remarks>
    /// Csound itself ignores the <c>&lt;Cabbage&gt;</c> section (it only reads the
    /// <c>&lt;CsoundSynthesizer&gt;</c> element), so the section is left in place — verified with
    /// Csound 7.0.
    /// </remarks>
    public static string PrepareForCsound(string csd, out List<string> notes)
    {
        notes = [];
        var orc = OrchestraRegex().Match(csd);
        if (!orc.Success) return csd;
        string code = orc.Groups[2].Value;
        // Fast exit for ordinary documents.
        if (!code.Contains("cabbage", StringComparison.Ordinal)) return csd;
        // Strip comments only in the copy used for detection (the compiled text keeps them), so a
        // commented-out cabbageSet does not trigger a stand-in.
        code = CommentRegex().Replace(code, "");

        // Cabbage opcodes the orchestra calls, minus those it defines itself as UDOs.
        var used = new SortedSet<string>(OpcodeUseRegex().Matches(code).Select(m => m.Groups[1].Value), StringComparer.Ordinal);
        if (used.Count == 0) return csd;
        var defined = new HashSet<string>(UdoRegex().Matches(code).Select(m => m.Groups[1].Value), StringComparer.Ordinal);

        var shims = new StringBuilder();
        var added = new List<string>();
        foreach (var (opcode, body) in Shims)
            if (used.Contains(opcode) && !defined.Contains(opcode))
            {
                // Normalise to LF so the inserted block counts lines the same way as the #line maths below.
                shims.Append(body.Replace("\r", "")).Append('\n');
                added.Add(opcode);
            }
        var missing = used.Where(u => !defined.Contains(u) && !Shims.Any(s => s.Opcode == u)).ToList();
        if (missing.Count > 0)
            notes.Add(Localization.Loc.S($"opcodes exclusivos de Cabbage que Csound Visual no puede proveer: {string.Join(", ", missing)}",
                                         $"Cabbage-only opcodes Csound Visual cannot provide: {string.Join(", ", missing)}"));
        if (added.Count == 0) return csd;
        notes.Add(Localization.Loc.S($"opcodes de Cabbage reemplazados por lecturas/escrituras de canales: {string.Join(", ", added)}",
                                     $"Cabbage opcodes provided as channel stand-ins: {string.Join(", ", added)}"));

        // Compiled through the API (csoundCompileCSD), Csound reports .csd file line numbers. The
        // stand-ins go right after the tag; «#line N» then gives the original next line its own
        // number again, so error messages still point at the right editor line.
        int at = orc.Groups[1].Index + orc.Groups[1].Length;   // just after "<CsInstruments>"
        // Line of the tag = 1 + newlines before it, so the line after the tag = 2 + newlines.
        int nextLine = 2;
        for (int i = 0; i < at; i++)
            if (csd[i] == '\n') nextLine++;
        return csd.Insert(at, "\n" + shims + "#line " + nextLine);
    }

    // ------------------------------------------------------------------------------------------
    //  «;@knob» annotations in ordinary Csound Visual documents (roadmap item 15)
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The annotation lines of <paramref name="csd"/> (used to detect changes cheaply), or
    /// <c>null</c> when the document has none.
    /// </summary>
    /// <param name="csd">Document text.</param>
    /// <returns>
    /// The trimmed annotation lines joined by newlines. The document view model compares this key
    /// with the previous one and rebuilds the form only when it changed, so typing elsewhere in
    /// the document does not recreate the widgets.
    /// </returns>
    public static string? AnnotationKey(string csd)
    {
        if (!csd.Contains(";@", StringComparison.Ordinal)) return null;
        var lines = AnnotationRegex().Matches(csd).Select(m => m.Value.Trim()).ToList();
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>
    /// Builds a form from widget annotations written as comments on channel lines of an ordinary
    /// document, laid out automatically:
    /// <code>
    /// chnk "cutoff", 1, 3, 1000, 20, 20000  ;@knob "Cutoff"
    /// chnk "res", 1, 2, 0.3, 0, 0.95        ;@slider
    /// chnk "wave", 1, 1, 1, 1, 3            ;@combo Saw|Square|Triangle
    /// chnk "x", 1, 2, 0.5, 0, 1             ;@xy "y"
    /// kgo chnget "go"                       ;@button   (also: momentary, checkbox, vslider)
    /// </code>
    /// The range comes from the <c>chnk</c> declaration (exponential → skew 0.3). Returns
    /// <c>null</c> when there are no annotations.
    /// </summary>
    /// <param name="csd">Document text.</param>
    /// <param name="channels">Channels detected in the orchestra (source of each widget's range and default).</param>
    /// <returns>An auto-laid-out form flagged <see cref="CabbageForm.FromAnnotations"/>, or <c>null</c>.</returns>
    /// <remarks>
    /// Layout is a simple flow: widgets are placed left to right with 10 px gaps in a 540 px wide
    /// form and wrap to a new row when the next one would not fit; the row height is the tallest
    /// widget of the row. The form height is computed at the end.
    /// </remarks>
    public static CabbageForm? FromAnnotations(string csd, IReadOnlyList<ChannelSpec> channels)
    {
        var matches = AnnotationRegex().Matches(csd);
        if (matches.Count == 0) return null;
        var form = new CabbageForm { FromAnnotations = true, Width = 540 };
        // Flow-layout cursor (Cabbage pixels) and the height of the current row.
        double x = 10, y = 10, rowH = 0;
        // Channels already given a widget (also the Y channel of an XY pad), so each appears once.
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in matches)
        {
            string channel = m.Groups["ch"].Value, kind = m.Groups["kind"].Value.ToLowerInvariant(), rest = m.Groups["rest"].Value.Trim();
            if (!used.Add(channel)) continue;
            // Unknown channel (e.g. annotation on a line the detector did not understand): 0…1, default 0.5.
            var spec = channels.FirstOrDefault(c => c.Name == channel) ?? new ChannelSpec(channel, 0.5, 0, 1);
            // First quoted string after the kind: the label, or for @xy the Y channel name.
            string? label = QuotedRegex().Match(rest) is { Success: true } q ? q.Groups[1].Value : null;
            // Increment heuristic: wide ranges (> 20 units, e.g. Hz) step by 1, narrow ones by 0.001.
            var range = new CabbageRange(spec.Minimum, spec.Maximum, spec.Default, spec.Exponential ? 0.3 : 1,
                spec.Maximum - spec.Minimum > 20 ? 1 : 0.001);
            int lineNo = 1 + csd.Take(m.Index).Count(c => c == '\n');
            // Each kind sets the widget and its fixed size in Cabbage pixels.
            CabbageWidget w;
            double width, height;
            switch (kind)
            {
                case "slider": case "hslider":
                    w = new CabbageWidget { Type = "hslider", Line = lineNo, Range = range }; width = 520; height = 28; break;
                case "vslider":
                    w = new CabbageWidget { Type = "vslider", Line = lineNo, Range = range }; width = 50; height = 160; break;
                case "button": case "momentary":
                    w = new CabbageWidget { Type = "button", Line = lineNo, Value = spec.Default, Latched = kind == "button" }; width = 100; height = 32; break;
                case "checkbox": case "toggle":
                    w = new CabbageWidget { Type = "checkbox", Line = lineNo, Value = spec.Default }; width = 130; height = 24; break;
                case "combo": case "combobox":
                    // Items are the '|'-separated words after the kind (the quoted label, if any, removed).
                    w = new CabbageWidget { Type = "combobox", Line = lineNo, Value = spec.Default };
                    w.Items = [.. (label is null ? rest : rest.Replace("\"" + label + "\"", "")).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                    width = 150; height = 28; break;
                case "xy": case "xypad":
                {
                    // For @xy the quoted string names the Y channel, not a label; that channel is
                    // marked used so its own annotation (if any) does not create a second widget.
                    string? other = label;
                    label = null;
                    var ySpec = other is null ? null : channels.FirstOrDefault(c => c.Name == other);
                    w = new CabbageWidget { Type = "xypad", Line = lineNo, RangeX = range };
                    if (other is not null)
                    {
                        used.Add(other);
                        w.RangeY = ySpec is null ? new CabbageRange(0, 1, 0.5)
                            : new CabbageRange(ySpec.Minimum, ySpec.Maximum, ySpec.Default, ySpec.Exponential ? 0.3 : 1, ySpec.Maximum - ySpec.Minimum > 20 ? 1 : 0.001);
                    }
                    width = 220; height = 220;
                    w.Channels.Add(channel);
                    if (other is not null) w.Channels.Add(other);
                    break;
                }
                default:
                    w = new CabbageWidget { Type = "rslider", Line = lineNo, Range = range }; width = 84; height = 96; break;
            }
            if (w.Channels.Count == 0) w.Channels.Add(channel);
            // Caption: the label or, failing that, the channel name. A combobox gets no Text so that
            // its Choices come from Items (Text would otherwise be taken as the item list).
            if (w.Type != "combobox" || label is not null) w.Text = [label ?? channel];
            if (w.Type == "combobox") w.Text = [];
            if (w.Type == "button") w.Text = [label ?? channel];

            // Wrap to the next row unless this is already the first widget of the row.
            if (x + width > form.Width - 10 && x > 10)
            {
                x = 10;
                y += rowH + 10;
                rowH = 0;
            }
            w.Bounds = new CabbageRect(x, y, width, height);
            form.Widgets.Add(w);
            x += width + 10;
            rowH = Math.Max(rowH, height);
        }
        form.Height = y + rowH + 10;
        return form;
    }

    /// <summary>A channel line with a <c>;@kind rest</c> comment: the first quoted name is the channel.</summary>
    /// <remarks>
    /// Groups: <c>ch</c> = first quoted string before the <c>;</c> (the channel name),
    /// <c>kind</c> = the annotation word, <c>rest</c> = the remainder of the line (label/items).
    /// Multiline so <c>^</c>/<c>$</c> match each line; <c>[^;\n]</c> keeps the match on one line and
    /// before the comment.
    /// </remarks>
    [GeneratedRegex(@"^[^;\n]*?""(?<ch>[^""\n]+)""[^;\n]*;\s*@(?<kind>knob|slider|hslider|vslider|button|momentary|checkbox|toggle|combo|combobox|xy|xypad)\b(?<rest>[^\n]*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex AnnotationRegex();

    /// <summary>A double-quoted string; group 1 = its content.</summary>
    [GeneratedRegex(@"""([^""]*)""")]
    private static partial Regex QuotedRegex();

    /// <summary>Group 1: the opening tag; group 2: the orchestra.</summary>
    [GeneratedRegex(@"(<CsInstruments>)(.*?)</CsInstruments>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex OrchestraRegex();

    /// <summary>Csound comments: <c>;</c> and <c>//</c> to end of line, <c>/* … */</c> blocks (quotes not considered).</summary>
    [GeneratedRegex(@";[^\n]*|//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    /// <summary>A call to a Cabbage opcode (<c>cabbage</c> followed by an upper-case letter); group 1 = name.</summary>
    [GeneratedRegex(@"\b(cabbage[A-Z]\w*)\b")]
    private static partial Regex OpcodeUseRegex();

    /// <summary>A UDO definition line <c>opcode name, …</c>; group 1 = name.</summary>
    [GeneratedRegex(@"^\s*opcode\s+(\w+)", RegexOptions.Multiline)]
    private static partial Regex UdoRegex();
}
