using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CsoundVisual.Cabbage;

/// <summary>
/// Tolerant parser of the <c>&lt;Cabbage&gt; … &lt;/Cabbage&gt;</c> section of a .csd (the widget
/// declarations of <see href="https://cabbageaudio.com">Cabbage</see> instruments).
/// </summary>
/// <remarks>
/// <para>
/// Cabbage 2 syntax: one widget per line, <c>type identifier(args), identifier(args) …</c>
/// (commas between identifiers are optional, identifier names are case-insensitive), <c>;</c>
/// comments, <c>#define NAME identifiers</c> macros used as <c>$NAME</c>, and child widgets inside
/// <c>{ … }</c> after a <c>groupbox</c>/<c>image</c> line (their bounds are relative to the parent).
/// A line that starts with an identifier (<c>shape("rounded"), …</c>) continues the previous widget.
/// </para>
/// <para>
/// The newer Cabbage 3 JSON form (<c>&lt;Cabbage&gt;[ {"type": "rotarySlider", …} ]&lt;/Cabbage&gt;</c>)
/// is read on a best-effort basis and mapped to the same model.
/// </para>
/// <para>
/// The parser never throws on bad input: unknown widget types, identifiers and malformed
/// arguments are kept and reported in <see cref="CabbageForm.Warnings"/>,
/// <see cref="CabbageForm.UnsupportedTypes"/> and <see cref="CabbageForm.IgnoredIdentifiers"/>.
/// </para>
/// <para>
/// It is a pure function of the text (no Avalonia, no Csound), called by
/// <see cref="CsoundVisual.ViewModels.DocumentViewModel"/> whenever the document changes; the
/// result feeds <see cref="CabbagePanel"/>. Csound itself never sees the widgets: it ignores the
/// <c>&lt;Cabbage&gt;</c> section (see <see cref="CabbageSupport.PrepareForCsound"/> for the UDO
/// stand-ins added before compiling a Cabbage document).
/// </para>
/// </remarks>
public static partial class CabbageParser
{
    /// <summary>Widget types Csound Visual draws (lower case).</summary>
    public static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        "form", "rslider", "hslider", "vslider", "nslider", "encoder", "button", "checkbox", "combobox",
        "optionbutton", "xypad", "keyboard", "label", "groupbox", "image", "line", "csoundoutput",
        "hrange", "vrange", "hmeter", "vmeter", "infobutton",
    };

    /// <summary>Widget types whose channel carries text, not numbers.</summary>
    static readonly HashSet<string> StringTypes = new(StringComparer.Ordinal)
    {
        "filebutton", "texteditor", "listbox", "presetbutton", "textbox", "webview",
    };

    /// <summary>Identifiers that are interpreted (lower case; colour identifiers are recognised by name).</summary>
    static readonly HashSet<string> Interpreted = new(StringComparer.Ordinal)
    {
        "bounds", "pos", "size", "channel", "channels", "range", "rangex", "rangey", "value", "text", "items",
        "caption", "visible", "active", "alpha", "latched", "radiogroup", "file", "shape", "outlinethickness",
        "corners", "align", "fontsize", "channeltype", "plant", "min", "max", "increment",
    };

    /// <summary>Identifiers that only matter to Cabbage itself (plugin metadata, GUI refresh…): not reported.</summary>
    /// <remarks>
    /// Anything neither here nor in <see cref="Interpreted"/> (and not a colour) is counted in
    /// <see cref="CabbageForm.IgnoredIdentifiers"/> so the UI can say what was not understood.
    /// "pluginid" appears twice in the initializer; the duplicate is harmless (HashSet.Add returns false).
    /// </remarks>
    static readonly HashSet<string> Silent = new(StringComparer.Ordinal)
    {
        "pluginid", "pluginid", "guimode", "guirefresh", "scrollbars", "identchannel", "popuptext", "valuetextbox",
        "textbox", "trackerinsideradius", "trackeroutsideradius", "trackerthickness", "popup", "popupprefix",
        "popuppostfix", "markerthickness", "markerstart", "markerend", "wrap", "readonly", "velocity", "mode",
        "style", "fontstyle", "zoom", "tablenumber", "tablenumbers", "amprange", "samplerange", "displaytype",
        "signalvariable", "rotate", "crop", "filmstrip", "imgfile", "gradient", "keywidth", "midctrl",
        "keypressbaseoctave", "mouseinteraction", "linethickness", "repeatinterval", "presetnameastext",
        "userfolder", "mountpoint", "decimalplaces", "sliderincrement", "titlebargradient", "opcodedir",
        "bundle", "typeface", "automatable", "presetignore", "valueprefix", "valuepostfix", "showscrubber",
        "scrubberposition", "tablegridcolour", "fill", "kind", "sort", "populate", "widgetarray", "line",
    };

    /// <summary>
    /// Finds the Cabbage section of a .csd. Returns its inner text and the 1-based line number of
    /// the <c>&lt;Cabbage&gt;</c> tag, or <c>null</c> when the document has none.
    /// </summary>
    /// <param name="csd">Full text of the .csd.</param>
    /// <returns>The section body (between the tags) and the tag's line, or <c>null</c>.</returns>
    public static (string Section, int TagLine)? FindSection(string csd)
    {
        var m = SectionRegex().Match(csd);
        if (!m.Success) return null;
        // Count newlines before the match to turn the character offset into a 1-based line number.
        int tagLine = 1;
        for (int i = 0; i < m.Index; i++)
            if (csd[i] == '\n') tagLine++;
        return (m.Groups[1].Value, tagLine);
    }

    /// <summary>True when the text has a <c>&lt;Cabbage&gt;</c> section.</summary>
    /// <param name="csd">Full text of the .csd.</param>
    /// <remarks>The cheap <c>Contains</c> check runs first so ordinary documents skip the regex.</remarks>
    public static bool HasSection(string csd) => csd.Contains("<Cabbage>", StringComparison.OrdinalIgnoreCase) && FindSection(csd) is not null;

    /// <summary>Parses the Cabbage section of a .csd, or returns <c>null</c> when there is none.</summary>
    /// <param name="csd">Full text of the .csd.</param>
    /// <returns>The parsed form (never throws), or <c>null</c> when the document has no section.</returns>
    public static CabbageForm? Parse(string csd) =>
        FindSection(csd) is { } s ? ParseSection(s.Section, s.TagLine) : null;

    /// <summary>
    /// Parses the inner text of a Cabbage section. <paramref name="tagLine"/> is the line of the
    /// opening tag (the section text starts on that line), used for the line numbers reported.
    /// </summary>
    /// <param name="section">Text between <c>&lt;Cabbage&gt;</c> and <c>&lt;/Cabbage&gt;</c>.</param>
    /// <param name="tagLine">1-based line of the opening tag in the .csd.</param>
    /// <returns>The form; problems are reported in it, never thrown.</returns>
    public static CabbageForm ParseSection(string section, int tagLine = 1)
    {
        var form = new CabbageForm();
        try
        {
            // Syntax sniffing: Cabbage 3 sections are a JSON array (or a single object);
            // classic sections always start with a widget type word.
            string trimmed = section.TrimStart();
            if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
                ParseJson(trimmed, tagLine, form);
            else
                ParseClassic(section, tagLine, form);
        }
        catch (JsonException ex)
        {
            form.Warnings.Add((tagLine + (int)(ex.LineNumber ?? 0), "invalid Cabbage JSON: " + ex.Message));
        }
        catch (Exception ex)
        {
            // Defensive: a parser bug must never take the app down.
            form.Warnings.Add((tagLine, "internal parser error: " + ex.Message));
        }
        return form;
    }

    // ------------------------------------------------------------------------------------------
    //  Cabbage 2 (classic) syntax
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Line-by-line reader of the classic syntax. Keeps three pieces of state while scanning:
    /// the <c>#define</c> macros seen so far, a stack of open <c>{ … }</c> parents (plants), and
    /// the last widget (target of continuation lines and of a lone <c>{</c> on the next line).
    /// </summary>
    /// <param name="section">Section text.</param>
    /// <param name="tagLine">Line of the opening tag, so line <c>i</c> of the section is <c>tagLine + i</c>.</param>
    /// <param name="form">Form being filled.</param>
    static void ParseClassic(string section, int tagLine, CabbageForm form)
    {
        section = StripBlockComments(section);
        var macros = new Dictionary<string, string>(StringComparer.Ordinal);
        // Top of the stack = the widget that new widgets become children of.
        var parents = new Stack<CabbageWidget>();
        // Most recent widget: continuation lines ("shape(…), corners(…)") and "{" apply to it.
        CabbageWidget? last = null;
        string[] lines = section.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNo = tagLine + i;
            string line = StripLineComment(lines[i]).Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#define", StringComparison.OrdinalIgnoreCase))
            {
                var dm = DefineRegex().Match(line);
                if (dm.Success) macros[dm.Groups[1].Value] = dm.Groups[2].Value;
                else form.Warnings.Add((lineNo, "malformed #define"));
                continue;
            }
            if (line.Contains('$'))
                line = ExpandMacros(line, macros, form, lineNo);

            // Closing braces (possibly several, possibly followed by more text).
            while (line.StartsWith('}'))
            {
                if (parents.Count > 0) parents.Pop();
                else form.Warnings.Add((lineNo, "'}' without a matching '{'"));
                line = line[1..].TrimStart(' ', '\t', ',');
            }
            if (line.Length == 0) continue;
            // A "{" alone on its line opens a block for the widget declared just before it.
            if (line == "{")
            {
                if (last is not null) parents.Push(last);
                else form.Warnings.Add((lineNo, "'{' without a widget before it"));
                continue;
            }
            // "groupbox bounds(…) {": the widget on this line becomes the parent of what follows.
            bool opensBlock = line.EndsWith('{');
            if (opensBlock) line = line[..^1].TrimEnd();

            // The first word is either a widget type or (on continuation lines) an identifier name;
            // which one is decided by whether "(" follows it.
            var tm = TypeRegex().Match(line);
            if (!tm.Success)
            {
                form.Warnings.Add((lineNo, $"cannot read line: {Shorten(line)}"));
                continue;
            }

            string word = tm.Groups[1].Value;
            string rest = line[tm.Length..];
            if (rest.TrimStart().StartsWith('('))
            {
                // Starts with an identifier: continuation of the previous widget's line.
                if (last is null)
                {
                    form.Warnings.Add((lineNo, $"identifiers without a widget: {Shorten(line)}"));
                    continue;
                }
                ApplyIdentifiers(last, ParseIdentifiers(line, form, lineNo), form, lineNo);
                if (opensBlock) parents.Push(last);
                continue;
            }

            var widget = new CabbageWidget { Type = word.ToLowerInvariant(), Line = lineNo };
            ApplyIdentifiers(widget, ParseIdentifiers(rest, form, lineNo), form, lineNo);
            // The "form" line describes the window, not a widget: it is not added to the tree.
            if (widget.Type == "form")
                ApplyForm(form, widget);
            else
            {
                if (parents.Count > 0) parents.Peek().Children.Add(widget);
                else form.Widgets.Add(widget);
                Classify(widget, form);
            }
            last = widget;
            if (opensBlock) parents.Push(widget);
        }
        if (parents.Count > 0)
            form.Warnings.Add((tagLine + lines.Length - 1, $"{parents.Count} '{{' block(s) not closed"));
    }

    /// <summary>Copies the <c>form</c> line's identifiers to the form.</summary>
    /// <param name="form">Form being filled.</param>
    /// <param name="w">The parsed <c>form</c> line; its <c>size(…)</c> lands in <see cref="CabbageWidget.Bounds"/>.</param>
    static void ApplyForm(CabbageForm form, CabbageWidget w)
    {
        form.FormWidget = w;
        // caption(…) wins; Cabbage 3 JSON forms often only have a "text".
        if (w.Caption is { } c) form.Caption = c;
        else if (w.Text.Count > 0) form.Caption = w.Text[0];
        if (w.Bounds.Width > 0 && w.Bounds.Height > 0)
        {
            form.Width = w.Bounds.Width;
            form.Height = w.Bounds.Height;
        }
        form.Colour = w.Colour("colour", "colour:0");
    }

    /// <summary>
    /// Marks string-channel widget types and counts (and warns about) widget types that are not
    /// drawn, so the UI can list them.
    /// </summary>
    /// <param name="w">A non-form widget just parsed.</param>
    /// <param name="form">Form receiving the counts and warnings.</param>
    static void Classify(CabbageWidget w, CabbageForm form)
    {
        if (StringTypes.Contains(w.Type)) w.IsStringChannel = true;
        if (!w.IsKnownType)
        {
            form.UnsupportedTypes[w.Type] = form.UnsupportedTypes.GetValueOrDefault(w.Type) + 1;
            form.Warnings.Add((w.Line, $"widget '{w.Type}' is not supported (shown as a placeholder)"));
        }
    }

    /// <summary>Parses <c>name(args), name(args) …</c> (commas and spaces between identifiers are optional).</summary>
    /// <param name="text">The part of the line after the widget type (or a whole continuation line).</param>
    /// <param name="form">Form receiving warnings.</param>
    /// <param name="lineNo">Line number for warnings.</param>
    /// <returns>The identifiers in order, names lower-cased, arguments split and unquoted.</returns>
    /// <remarks>
    /// Hand-written scanner rather than a regex because arguments may contain quoted text with
    /// commas or parentheses (<c>text("a, b (c)")</c>) and nested parentheses.
    /// </remarks>
    static List<CabbageIdentifier> ParseIdentifiers(string text, CabbageForm form, int lineNo)
    {
        var list = new List<CabbageIdentifier>();
        int i = 0;   // scan position in text
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
            if (!(char.IsLetter(c) || c == '_'))
            {
                form.Warnings.Add((lineNo, $"unexpected '{c}' in: {Shorten(text.Trim())}"));
                // Skip to the next separator.
                while (i < text.Length && text[i] != ',' && !char.IsWhiteSpace(text[i])) i++;
                continue;
            }
            // Identifier name; ':' is allowed so indexed names like "colour:1" stay one token.
            int start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or ':')) i++;
            string name = text[start..i].ToLowerInvariant();
            int j = i;
            while (j < text.Length && text[j] is ' ' or '\t') j++;
            if (j >= text.Length || text[j] != '(')
            {
                form.Warnings.Add((lineNo, $"identifier '{name}' without arguments"));
                continue;
            }
            // Find the matching ')' outside quotes.
            int depth = 0, k = j;
            char quote = '\0';
            for (; k < text.Length; k++)
            {
                char ch = text[k];
                if (quote != '\0') { if (ch == quote) quote = '\0'; continue; }
                if (ch is '"' or '\'') { quote = ch; continue; }
                if (ch == '(') depth++;
                else if (ch == ')' && --depth == 0) break;
            }
            if (k >= text.Length)
            {
                // Unbalanced: keep what we have (tolerant) and stop, the rest of the line is unreadable.
                form.Warnings.Add((lineNo, $"missing ')' after '{name}'"));
                list.Add(new CabbageIdentifier(name, SplitArgs(text[(j + 1)..])));
                break;
            }
            list.Add(new CabbageIdentifier(name, SplitArgs(text[(j + 1)..k])));
            i = k + 1;
        }
        return list;
    }

    /// <summary>Splits arguments at top-level commas and removes the quotes around strings.</summary>
    /// <param name="args">Text between the identifier's parentheses.</param>
    /// <returns>
    /// One entry per argument. Quoted arguments keep their inner spaces exactly; unquoted ones are
    /// trimmed. An empty argument list (<c>()</c>) gives an empty list.
    /// </returns>
    static List<string> SplitArgs(string args)
    {
        var result = new List<string>();
        var sb = new StringBuilder();   // current argument
        char quote = '\0';              // quote char while inside a string, else '\0'
        bool quoted = false;            // the current argument contained a quoted string
        int depth = 0;                  // parenthesis depth (commas inside nested parens do not split)
        foreach (char ch in args)
        {
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else sb.Append(ch);
                continue;
            }
            if (ch is '"' or '\'')
            {
                // Spaces between the comma and the opening quote are not part of the string.
                if (!quoted && sb.ToString().Trim().Length == 0) sb.Clear();
                quote = ch;
                quoted = true;
                continue;
            }
            if (ch == '(') depth++;
            if (ch == ')') depth--;
            if (ch == ',' && depth == 0)
            {
                result.Add(quoted ? sb.ToString() : sb.ToString().Trim());
                sb.Clear();
                quoted = false;
                continue;
            }
            if (quoted && char.IsWhiteSpace(ch)) continue;   // spaces between a closing quote and the comma
            sb.Append(ch);
        }
        // Last argument: added also when empty after a comma ("a,") or when it was "" (quoted).
        if (sb.Length > 0 || quoted || result.Count > 0)
            result.Add(quoted ? sb.ToString() : sb.ToString().Trim());
        return result;
    }

    /// <summary>Decodes the identifiers into the widget's properties.</summary>
    /// <param name="w">Widget to fill (later identifiers overwrite earlier ones, as in Cabbage).</param>
    /// <param name="ids">Identifiers from <see cref="ParseIdentifiers"/>.</param>
    /// <param name="form">Form receiving warnings and the ignored-identifier counts.</param>
    /// <param name="lineNo">Line number for warnings.</param>
    static void ApplyIdentifiers(CabbageWidget w, List<CabbageIdentifier> ids, CabbageForm form, int lineNo)
    {
        foreach (var id in ids)
        {
            w.Identifiers.Add(id);
            var a = id.Args;
            // Cabbage accepts the US spelling too (fontcolor, trackercolor…); normalise to "colour".
            string name = id.Name.Replace("color", "colour");
            // Per-identifier try: one bad identifier must not lose the rest of the line.
            try
            {
                // Every *colour* identifier (colour, colour:1, fontcolour, outlinecolour…) is stored by name;
                // the UI decides which one it uses for which part of the control.
                if (name.Contains("colour"))
                {
                    if (CabbageColour.Parse(a) is { } col) w.Colours[name] = col;
                    else form.Warnings.Add((lineNo, $"cannot read colour {id.Name}({string.Join(",", a)})"));
                    continue;
                }
                switch (name)
                {
                    case "bounds":
                        if (Numbers(a, 4) is { } b) w.Bounds = new CabbageRect(b[0], b[1], b[2], b[3]);
                        else Bad(form, lineNo, id);
                        break;
                    case "pos":
                        if (Numbers(a, 2) is { } p) w.Bounds = w.Bounds with { X = p[0], Y = p[1] };
                        else Bad(form, lineNo, id);
                        break;
                    case "size":
                        if (Numbers(a, 2) is { } s) w.Bounds = w.Bounds with { Width = s[0], Height = s[1] };
                        else Bad(form, lineNo, id);
                        break;
                    case "channel":
                    case "channels":
                        w.Channels.Clear();
                        w.Channels.AddRange(a.Where(x => x.Length > 0));
                        break;
                    case "range":
                        if (ParseRange(a) is { } r) w.Range = r; else Bad(form, lineNo, id);
                        break;
                    case "rangex":
                        if (ParseRange(a) is { } rx) w.RangeX = rx; else Bad(form, lineNo, id);
                        break;
                    case "rangey":
                        if (ParseRange(a) is { } ry) w.RangeY = ry; else Bad(form, lineNo, id);
                        break;
                    case "value":
                        if (a.Count > 0 && TryNumber(a[0], out double v)) w.Value = v; else Bad(form, lineNo, id);
                        break;
                    case "text": w.Text = [.. a]; break;
                    case "items": w.Items = [.. a]; break;
                    case "caption": w.Caption = a.Count > 0 ? a[0] : ""; break;
                    case "visible": w.Visible = Flag(a, true); break;
                    case "active": w.Active = Flag(a, true); break;
                    case "latched": w.Latched = Flag(a, true); break;
                    case "alpha": if (a.Count > 0 && TryNumber(a[0], out double al)) w.Alpha = Math.Clamp(al, 0, 1); break;
                    case "radiogroup":
                        // Cabbage accepts numbers or names: any non-empty, non-zero value groups.
                        // Names map to their (process-local) hash with bit 0 forced on so it is never 0;
                        // only equality within this session matters.
                        w.RadioGroup = a.Count == 0 ? 0 : TryNumber(a[0], out double g) ? (int)g : a[0].GetHashCode() | 1;
                        break;
                    case "file": w.File = a.Count > 0 ? a[0] : null; break;
                    case "shape": w.Shape = a.Count > 0 ? a[0].ToLowerInvariant() : null; break;
                    case "outlinethickness": if (a.Count > 0 && TryNumber(a[0], out double ot)) w.OutlineThickness = ot; break;
                    case "corners": if (a.Count > 0 && TryNumber(a[0], out double co)) w.Corners = co; break;
                    case "align": w.Align = a.Count > 0 ? a[0].ToLowerInvariant() : null; break;
                    case "fontsize": if (a.Count > 0 && TryNumber(a[0], out double fs)) w.FontSize = fs; break;
                    case "channeltype": w.IsStringChannel = a.Count > 0 && a[0].Equals("string", StringComparison.OrdinalIgnoreCase); break;
                    case "plant": w.Plant = a.Count > 0 ? a[0] : null; break;
                    case "min": if (a.Count > 0 && TryNumber(a[0], out double mn)) w.MinOverride = mn; break;
                    case "max": if (a.Count > 0 && TryNumber(a[0], out double mx)) w.MaxOverride = mx; break;
                    case "increment": if (a.Count > 0 && TryNumber(a[0], out double inc)) w.IncrementOverride = inc; break;
                    default:
                        if (!Silent.Contains(name))
                            form.IgnoredIdentifiers[name] = form.IgnoredIdentifiers.GetValueOrDefault(name) + 1;
                        break;
                }
            }
            catch (Exception ex)
            {
                form.Warnings.Add((lineNo, $"{id.Name}: {ex.Message}"));
            }
        }
    }

    /// <summary>Reports an identifier whose arguments could not be read.</summary>
    static void Bad(CabbageForm form, int lineNo, CabbageIdentifier id) =>
        form.Warnings.Add((lineNo, $"cannot read {id.Name}({string.Join(", ", id.Args)})"));

    /// <summary>Reads <c>min, max, value[, skew[, increment]]</c>; value may be <c>a:b</c> (range sliders).</summary>
    /// <param name="a">Arguments of <c>range</c>/<c>rangeX</c>/<c>rangeY</c>.</param>
    /// <returns>The range, or <c>null</c> when min/max are not numbers. Missing or empty value = min,
    /// missing/invalid skew = 1, missing/negative increment = 0.01 (Cabbage's default).</returns>
    static CabbageRange? ParseRange(IReadOnlyList<string> a)
    {
        if (a.Count < 2 || !TryNumber(a[0], out double min) || !TryNumber(a[1], out double max))
            return null;
        double value = min, value2 = double.NaN;
        if (a.Count >= 3)
        {
            string v = a[2];
            // Search from index 1 so a ':' at position 0 is never taken as the separator.
            // Bug fix: IndexOf(':', 1) throws ArgumentOutOfRangeException on an empty argument
            // ("range(0, 1, )", common while typing), which dropped the whole range. Strings
            // shorter than 2 characters cannot hold "a:b", so they skip the search and an empty
            // value falls back to the default (min) below, like any missing argument.
            int colon = v.Length > 1 ? v.IndexOf(':', 1) : -1;
            if (colon > 0 && TryNumber(v[..colon], out double v1) && TryNumber(v[(colon + 1)..], out double v2))
            {
                value = v1;
                value2 = v2;
            }
            else if (!TryNumber(v, out value))
                value = min;
        }
        double skew = a.Count >= 4 && TryNumber(a[3], out double sk) && sk > 0 ? sk : 1;
        double inc = a.Count >= 5 && TryNumber(a[4], out double ic) && ic >= 0 ? ic : 0.01;
        return new CabbageRange(min, max, value, skew, inc, value2);
    }

    /// <summary>Reads the first <paramref name="count"/> arguments as numbers.</summary>
    /// <returns>The numbers, or <c>null</c> when there are fewer arguments or one is not numeric.</returns>
    static double[]? Numbers(IReadOnlyList<string> a, int count)
    {
        if (a.Count < count) return null;
        var r = new double[count];
        for (int i = 0; i < count; i++)
            if (!TryNumber(a[i], out r[i])) return null;
        return r;
    }

    /// <summary>Reads a 0/1 flag argument (any non-zero number is true); <paramref name="fallback"/> when absent or not numeric.</summary>
    static bool Flag(IReadOnlyList<string> a, bool fallback) =>
        a.Count > 0 && TryNumber(a[0], out double v) ? v != 0 : fallback;

    /// <summary>Parses a number the way Cabbage writes them (<c>.5</c>, <c>-1</c>, <c>1e3</c>), culture-invariant.</summary>
    /// <param name="s">Text to parse (surrounding spaces ignored).</param>
    /// <param name="value">The number, when successful.</param>
    /// <returns>True for a finite number; NaN and infinities are rejected.</returns>
    public static bool TryNumber(string s, out double value) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>
    /// Replaces each <c>$NAME</c> (optionally closed as <c>$NAME$</c>) by the body of its
    /// <c>#define</c>. The body is padded with spaces so it never glues to neighbouring
    /// identifiers; undefined macros are dropped with a warning.
    /// </summary>
    static string ExpandMacros(string line, Dictionary<string, string> macros, CabbageForm form, int lineNo)
    {
        return MacroUseRegex().Replace(line, m =>
        {
            if (macros.TryGetValue(m.Groups[1].Value, out var body)) return " " + body + " ";
            form.Warnings.Add((lineNo, $"undefined macro ${m.Groups[1].Value}"));
            return " ";
        });
    }

    /// <summary>Removes a <c>;</c> or <c>//</c> comment that is outside double quotes.</summary>
    /// <remarks>Only <c>"</c> starts a string here (a <c>'</c> in a comment such as "don't" must not hide the rest).</remarks>
    static string StripLineComment(string line)
    {
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c == '"') quote = c;
            else if (c == ';') return line[..i];
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') return line[..i];
        }
        return line;
    }

    /// <summary>Blanks <c>/* … */</c> comments, keeping the line breaks so line numbers stay right.</summary>
    static string StripBlockComments(string text)
    {
        if (!text.Contains("/*")) return text;
        return BlockCommentRegex().Replace(text, m => new string('\n', m.Value.Count(ch => ch == '\n')));
    }

    /// <summary>Truncates text quoted in a warning to 70 characters.</summary>
    static string Shorten(string s) => s.Length > 70 ? s[..70] + "…" : s;

    // ------------------------------------------------------------------------------------------
    //  Cabbage 3 (JSON) syntax, best effort
    // ------------------------------------------------------------------------------------------

    /// <summary>Cabbage 3 type names → Cabbage 2 names.</summary>
    static readonly Dictionary<string, string> JsonTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["form"] = "form", ["rotarySlider"] = "rslider", ["horizontalSlider"] = "hslider", ["verticalSlider"] = "vslider",
        ["numberSlider"] = "nslider", ["button"] = "button", ["checkBox"] = "checkbox", ["comboBox"] = "combobox",
        ["optionButton"] = "optionbutton", ["xyPad"] = "xypad", ["keyboard"] = "keyboard", ["label"] = "label",
        ["groupBox"] = "groupbox", ["image"] = "image", ["csoundOutput"] = "csoundoutput", ["genTable"] = "gentable",
        ["horizontalRangeSlider"] = "hrange", ["verticalRangeSlider"] = "vrange", ["textEditor"] = "texteditor",
        ["fileButton"] = "filebutton", ["infoButton"] = "infobutton", ["encoder"] = "encoder",
        ["horizontalMeter"] = "hmeter", ["verticalMeter"] = "vmeter",
    };

    /// <summary>
    /// Reads a Cabbage 3 JSON section (an array of widget objects, or one object). Trailing commas
    /// and comments are tolerated. JSON gives no per-widget line numbers, so every widget is
    /// reported at the tag line. A <see cref="JsonException"/> propagates to
    /// <see cref="ParseSection"/>, which turns it into a warning.
    /// </summary>
    static void ParseJson(string json, int tagLine, CabbageForm form)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        IEnumerable<JsonElement> items = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray()
            : [doc.RootElement];
        foreach (var e in items)
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var w = JsonWidget(e, tagLine, form);
            if (w.Type == "form") ApplyForm(form, w);
            else
            {
                form.Widgets.Add(w);
                Classify(w, form);
            }
        }
    }

    /// <summary>
    /// Maps one Cabbage 3 JSON widget object onto the Cabbage 2 model: type renamed through
    /// <see cref="JsonTypes"/>, <c>bounds {left, top, width, height}</c>, text/label, items,
    /// value, channels (with per-channel ranges for XY pads), colours and nested children.
    /// Unknown properties are ignored silently (best effort).
    /// </summary>
    /// <param name="e">The widget's JSON object.</param>
    /// <param name="tagLine">Line reported for the widget.</param>
    /// <param name="form">Form (unused for now beyond the recursive calls).</param>
    /// <returns>The widget (never null).</returns>
    static CabbageWidget JsonWidget(JsonElement e, int tagLine, CabbageForm form)
    {
        string rawType = Str(e, "type") ?? "unknown";
        var w = new CabbageWidget { Type = JsonTypes.TryGetValue(rawType, out var t) ? t : rawType.ToLowerInvariant(), Line = tagLine };
        if (e.TryGetProperty("bounds", out var b) && b.ValueKind == JsonValueKind.Object)
            w.Bounds = new CabbageRect(Num(b, "left") ?? 0, Num(b, "top") ?? 0, Num(b, "width") ?? 0, Num(b, "height") ?? 0);
        if (e.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Object)
            w.Bounds = w.Bounds with { Width = Num(s, "width") ?? 0, Height = Num(s, "height") ?? 0 };
        w.Caption = Str(e, "caption");
        if (Str(e, "text") is { } text) w.Text = [text];
        else if (e.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.Object)
            w.Text = [.. new[] { Str(tx, "off") ?? Str(tx, "default") ?? "", Str(tx, "on") ?? "" }.Where(x => x.Length > 0)];
        if (e.TryGetProperty("label", out var lab) && lab.ValueKind == JsonValueKind.Object && Str(lab, "text") is { } lt)
            w.Text = [lt];
        if (e.TryGetProperty("items", out var it))
            w.Items = it.ValueKind == JsonValueKind.Array ? [.. it.EnumerateArray().Select(x => x.ToString())]
                    : it.ValueKind == JsonValueKind.String ? [.. it.GetString()!.Split(',').Select(x => x.Trim())] : [];
        if (Num(e, "value") is { } v) w.Value = v;
        if (e.TryGetProperty("visible", out var vis) && vis.ValueKind is JsonValueKind.False or JsonValueKind.Number)
            w.Visible = vis.ValueKind == JsonValueKind.Number ? vis.GetDouble() != 0 : false;
        if (Str(e, "file") is { } file) w.File = file;

        // Channels: "channel": "x", or "channels": [{"id": "x", "range": {...}}, …]
        if (Str(e, "channel") is { } ch) w.Channels.Add(ch);
        if (e.TryGetProperty("channels", out var chs) && chs.ValueKind == JsonValueKind.Array)
        {
            int n = 0;
            foreach (var c in chs.EnumerateArray())
            {
                string? id = c.ValueKind == JsonValueKind.String ? c.GetString() : Str(c, "id") ?? Str(c, "channel");
                if (id is null) continue;
                w.Channels.Add(id);
                if (c.ValueKind == JsonValueKind.Object && c.TryGetProperty("range", out var cr) && JsonRange(cr) is { } r)
                {
                    // First channel's range is both the main range and the XY pad's X; the second is Y.
                    if (n == 0) { w.Range = r; w.RangeX = r; } else w.RangeY = r;
                }
                n++;
            }
        }
        if (e.TryGetProperty("range", out var rg) && JsonRange(rg) is { } range) w.Range = range;
        if (e.TryGetProperty("rangeX", out var rgx) && JsonRange(rgx) is { } rangeX) w.RangeX = rangeX;
        if (e.TryGetProperty("rangeY", out var rgy) && JsonRange(rgy) is { } rangeY) w.RangeY = rangeY;

        // Colours: "colour": "#rrggbb" or {"fill": "#…", "stroke": {…}}; "font": {"colour": "…"}.
        if (e.TryGetProperty("colour", out var col))
        {
            if (col.ValueKind == JsonValueKind.String && CabbageColour.Parse([col.GetString()!]) is { } c0) w.Colours["colour"] = c0;
            else if (col.ValueKind == JsonValueKind.Object)
            {
                if (Str(col, "fill") is { } fill && CabbageColour.Parse([fill]) is { } cf) w.Colours["colour"] = cf;
                if (col.TryGetProperty("tracker", out var tr) && tr.ValueKind == JsonValueKind.Object && Str(tr, "fill") is { } tf
                    && CabbageColour.Parse([tf]) is { } ct) w.Colours["trackercolour"] = ct;
            }
        }
        if (e.TryGetProperty("font", out var font) && font.ValueKind == JsonValueKind.Object && Str(font, "colour") is { } fc
            && CabbageColour.Parse([fc]) is { } cfc) w.Colours["fontcolour"] = cfc;
        if (e.TryGetProperty("children", out var kids) && kids.ValueKind == JsonValueKind.Array)
            foreach (var k in kids.EnumerateArray())
                if (k.ValueKind == JsonValueKind.Object) w.Children.Add(JsonWidget(k, tagLine, form));
        return w;
    }

    /// <summary>Reads a JSON <c>{min, max, defaultValue|value, skew, increment}</c> object (defaults 0, 1, min, 1, 0.01).</summary>
    static CabbageRange? JsonRange(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) return null;
        double min = Num(r, "min") ?? 0, max = Num(r, "max") ?? 1;
        return new CabbageRange(min, max, Num(r, "defaultValue") ?? Num(r, "value") ?? min, Num(r, "skew") ?? 1, Num(r, "increment") ?? 0.01);
    }

    /// <summary>String property <paramref name="name"/> of an object, or <c>null</c> when absent or not a string.</summary>
    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>Numeric property <paramref name="name"/> (a JSON number or a numeric string), or <c>null</c>.</summary>
    static double? Num(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number) return p.GetDouble();
        if (p.ValueKind == JsonValueKind.String && TryNumber(p.GetString()!, out double d)) return d;
        return null;
    }

    // Source-generated regexes (compiled at build time, no runtime Regex construction).

    /// <summary>The first <c>&lt;Cabbage&gt;…&lt;/Cabbage&gt;</c> block (lazy, across lines); group 1 = body.</summary>
    [GeneratedRegex(@"<Cabbage>(.*?)</Cabbage>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SectionRegex();

    /// <summary><c>#define NAME body</c>; group 1 = name, group 2 = body (rest of the line).</summary>
    [GeneratedRegex(@"^#define\s+([A-Za-z_]\w*)\s*(.*)$")]
    private static partial Regex DefineRegex();

    /// <summary>A macro use <c>$NAME</c> or <c>$NAME$</c>; group 1 = name.</summary>
    [GeneratedRegex(@"\$([A-Za-z_]\w*)\$?")]
    private static partial Regex MacroUseRegex();

    /// <summary>The leading word of a line (widget type or identifier name); group 1 = the word.</summary>
    [GeneratedRegex(@"^([A-Za-z_]\w*)")]
    private static partial Regex TypeRegex();

    /// <summary>A <c>/* … */</c> block comment, possibly spanning lines.</summary>
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockCommentRegex();
}
