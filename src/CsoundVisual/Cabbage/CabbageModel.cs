using System.Globalization;

// ---------------------------------------------------------------------------------------------
// Data model of a Cabbage GUI description.
//
// Cabbage (https://cabbageaudio.com) lets a .csd describe a plug-in front panel inside a
// <Cabbage> … </Cabbage> section: one widget per line, each line being a widget type followed by
// identifiers such as bounds(x, y, w, h), channel("cutoff"), range(20, 20000, 2000, 0.3, 1).
// Csound itself ignores that section.
//
// Pipeline in Csound Visual:
//   text  --CabbageParser.Parse-->  CabbageForm (this file: pure data, no Avalonia)
//         --CabbagePanel-->         Avalonia controls (CabbageControls.cs) bound to the document's
//                                   ChannelControlViewModel instances, so moving a Cabbage knob
//                                   writes the same Csound channel as the side-panel slider.
// Everything here is immutable-ish plain data so it can be parsed and tested without a UI.
// Coordinates are "Cabbage pixels" (the units used in bounds()); the panel scales them.
// ---------------------------------------------------------------------------------------------

namespace CsoundVisual.Cabbage;

/// <summary>Position and size of a widget in Cabbage pixels, relative to its parent (the form or a plant).</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct CabbageRect(double X, double Y, double Width, double Height);

/// <summary>
/// A Cabbage <c>range(min, max, value, skew, increment)</c>. <see cref="Skew"/> follows Cabbage's
/// (JUCE's) definition: the normalised slider position is <c>((v − min)/(max − min))^skew</c>, so
/// 1 is linear and values below 1 give more resolution to the low end (good for frequencies).
/// </summary>
/// <param name="Min">Lowest value.</param>
/// <param name="Max">Highest value.</param>
/// <param name="Value">Initial (default) value.</param>
/// <param name="Skew">Skew factor (1 = linear).</param>
/// <param name="Increment">Step size (0 = continuous).</param>
/// <param name="Value2">Second initial value of a range slider (<c>hrange</c>: <c>range(0, 1, 0.2:0.8)</c>), else NaN.</param>
public readonly record struct CabbageRange(double Min, double Max, double Value, double Skew = 1, double Increment = 0.01, double Value2 = double.NaN)
{
    /// <summary>Maps a value to the 0…1 position of the control, applying the skew.</summary>
    /// <param name="v">Value in the range's units (Hz, dB, …); clamped to [Min, Max].</param>
    /// <returns>Normalised control position 0…1 (0 for a degenerate range).</returns>
    public double ToNormalized(double v)
    {
        // A degenerate or inverted range has no meaningful position; pin it to the start.
        if (Max <= Min) return 0;
        double p = Math.Clamp((v - Min) / (Max - Min), 0, 1);
        return Skew > 0 && Math.Abs(Skew - 1) > 1e-9 ? Math.Pow(p, Skew) : p;
    }

    /// <summary>Maps a 0…1 control position back to a value (inverse skew, then the increment).</summary>
    /// <param name="n">Normalised control position; clamped to 0…1.</param>
    /// <returns>The value, snapped to <see cref="Increment"/> and clamped to the range.</returns>
    public double FromNormalized(double n)
    {
        n = Math.Clamp(n, 0, 1);
        // Inverse of ToNormalized: p = n^(1/skew). The 1e-9 test avoids a needless Pow for skew 1.
        if (Skew > 0 && Math.Abs(Skew - 1) > 1e-9) n = Math.Pow(n, 1 / Skew);
        return Snap(Min + n * (Max - Min));
    }

    /// <summary>Rounds to the increment (relative to <see cref="Min"/>) and clamps to the range.</summary>
    /// <param name="v">Raw value.</param>
    /// <returns>The snapped and clamped value.</returns>
    public double Snap(double v)
    {
        // The grid is anchored at Min (not 0) so range(1, 10, 5, 1, 2) yields 1, 3, 5, … like Cabbage.
        // The clamp uses Min/Max in either order so a reversed range never throws in Math.Clamp.
        if (Increment > 0)
            v = Min + Math.Round((v - Min) / Increment) * Increment;
        return Math.Clamp(v, Math.Min(Min, Max), Math.Max(Min, Max));
    }
}

/// <summary>
/// A colour given to a Cabbage identifier. Numeric forms (<c>colour(r, g, b[, a])</c>, a single
/// grey level, <c>"#rrggbb"</c>, <c>"#aarrggbb"</c>) are decoded here; names (<c>"white"</c>,
/// <c>"cornflowerblue"</c>) are kept in <see cref="Name"/> and resolved by the UI layer, which knows
/// the CSS colour names.
/// </summary>
/// <param name="A">Alpha 0…255.</param>
/// <param name="R">Red 0…255.</param>
/// <param name="G">Green 0…255.</param>
/// <param name="B">Blue 0…255.</param>
/// <param name="Name">Colour name when given by name (ARGB is then 255,0,0,0 until resolved).</param>
public readonly record struct CabbageColour(byte A, byte R, byte G, byte B, string? Name = null)
{
    /// <summary>
    /// Parses the arguments of a colour identifier. Returns <c>null</c> when they cannot be read
    /// as a colour (the caller reports it).
    /// </summary>
    /// <param name="args">Identifier arguments, quotes already removed.</param>
    /// <returns>The colour, or <c>null</c> when the arguments are not a recognisable colour.</returns>
    public static CabbageColour? Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return null;
        // Form 1: three or four numbers, r, g, b[, a] in 0…255.
        if (args.Count >= 3 && args.Take(Math.Min(4, args.Count)).All(a => CabbageParser.TryNumber(a, out _)))
        {
            CabbageParser.TryNumber(args[0], out double r);
            CabbageParser.TryNumber(args[1], out double g);
            CabbageParser.TryNumber(args[2], out double b);
            double a = 255;
            if (args.Count >= 4) CabbageParser.TryNumber(args[3], out a);
            return new CabbageColour(Byte(a), Byte(r), Byte(g), Byte(b));
        }
        string s = args[0].Trim();
        // Form 2: a single number is a grey level.
        if (CabbageParser.TryNumber(s, out double grey))
            return new CabbageColour(255, Byte(grey), Byte(grey), Byte(grey));
        // Form 3: hexadecimal, with or without the leading '#'.
        string hex = s.StartsWith('#') ? s[1..] : s;
        if ((hex.Length == 6 || hex.Length == 8) && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
        {
            // Cabbage writes 8-digit colours as AARRGGBB (JUCE's toString order).
            if (hex.Length == 6) v |= 0xFF000000;
            return new CabbageColour((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }
        // Form 4: a colour name ("cornflower blue" → "cornflowerblue"); resolved later by CabbageSupport.
        if (s.Length > 0 && s.All(c => char.IsLetter(c) || c == ' '))
            return new CabbageColour(255, 0, 0, 0, s.Replace(" ", "").ToLowerInvariant());
        return null;
    }

    /// <summary>Rounds and clamps a colour component to 0…255.</summary>
    static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    /// <inheritdoc/>
    public override string ToString() => Name ?? $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

/// <summary>One identifier of a widget line, e.g. <c>range(20, 20000, 2000, 0.3, 1)</c>.</summary>
/// <param name="Name">Identifier name in lower case, including an index suffix (<c>colour:1</c>).</param>
/// <param name="Args">Arguments with quotes removed.</param>
public sealed record CabbageIdentifier(string Name, IReadOnlyList<string> Args);

/// <summary>
/// One widget of a <c>&lt;Cabbage&gt;</c> section. The commonly used identifiers are decoded into
/// properties; every identifier (known or not) is also kept in <see cref="Identifiers"/>.
/// </summary>
/// <remarks>
/// Filled by <see cref="CabbageParser"/> (properties are mutable only so the parser can apply
/// identifiers one at a time) and read by <see cref="CabbagePanel"/>, which turns each widget into
/// an Avalonia control. <c>NaN</c> in numeric properties means "not given, use the default".
/// </remarks>
public sealed class CabbageWidget
{
    /// <summary>Widget type in lower case (<c>rslider</c>, <c>button</c>, …).</summary>
    public required string Type { get; init; }

    /// <summary>1-based line of the .csd where the widget is declared.</summary>
    public int Line { get; init; }

    /// <summary>True when <see cref="Type"/> is one Csound Visual knows (others are drawn as placeholders).</summary>
    public bool IsKnownType => CabbageParser.KnownTypes.Contains(Type);

    /// <summary>Every identifier, in order (a later duplicate overrides an earlier one in the decoded properties).</summary>
    public List<CabbageIdentifier> Identifiers { get; } = [];

    /// <summary>Position and size relative to the parent.</summary>
    public CabbageRect Bounds { get; set; }

    /// <summary>Channel names (<c>xypad</c> and <c>hrange</c> have two).</summary>
    public List<string> Channels { get; } = [];

    /// <summary>The <c>range(…)</c> identifier, if any.</summary>
    public CabbageRange? Range { get; set; }

    /// <summary>XY pad horizontal range (<c>rangeX</c>).</summary>
    public CabbageRange? RangeX { get; set; }

    /// <summary>XY pad vertical range (<c>rangeY</c>).</summary>
    public CabbageRange? RangeY { get; set; }

    /// <summary>The <c>value(…)</c> identifier, if any.</summary>
    public double? Value { get; set; }

    /// <summary>The strings of <c>text(…)</c>: label, or off/on captions of a button, or the items of a combobox.</summary>
    public List<string> Text { get; set; } = [];

    /// <summary>The <c>items(…)</c> of a combobox/optionbutton.</summary>
    public List<string> Items { get; set; } = [];

    /// <summary>Caption of a groupbox (or the form).</summary>
    public string? Caption { get; set; }

    /// <summary>Colours by identifier name (<c>colour</c>, <c>colour:1</c>, <c>fontcolour</c>, <c>trackercolour</c>…).</summary>
    public Dictionary<string, CabbageColour> Colours { get; } = new(StringComparer.Ordinal);

    /// <summary>Children declared inside <c>{ … }</c> (plants); their bounds are relative to this widget.</summary>
    public List<CabbageWidget> Children { get; } = [];

    /// <summary><c>visible(0)</c> hides the widget.</summary>
    public bool Visible { get; set; } = true;

    /// <summary><c>active(0)</c> makes the widget display-only.</summary>
    public bool Active { get; set; } = true;

    /// <summary><c>alpha(…)</c>, 0…1.</summary>
    public double Alpha { get; set; } = 1;

    /// <summary><c>latched(0)</c> turns a button into a momentary one (Cabbage's default is latched).</summary>
    public bool Latched { get; set; } = true;

    /// <summary><c>radioGroup(n)</c>: buttons/checkboxes with the same non-zero group are mutually exclusive.</summary>
    public int RadioGroup { get; set; }

    /// <summary><c>file(…)</c> of an image.</summary>
    public string? File { get; set; }

    /// <summary><c>shape(…)</c>: "rounded", "sharp", "ellipse", "circle"…</summary>
    public string? Shape { get; set; }

    /// <summary><c>outlineThickness(…)</c>.</summary>
    public double OutlineThickness { get; set; } = double.NaN;

    /// <summary><c>corners(…)</c> radius.</summary>
    public double Corners { get; set; } = double.NaN;

    /// <summary><c>align(…)</c> of a label: "left", "right", "centre".</summary>
    public string? Align { get; set; }

    /// <summary><c>fontSize(…)</c>, if given.</summary>
    public double FontSize { get; set; } = double.NaN;

    /// <summary><c>channelType("string")</c> or a widget that only carries strings (file button, text editor).</summary>
    public bool IsStringChannel { get; set; }

    /// <summary>Plant name (<c>plant("…")</c>), informative.</summary>
    public string? Plant { get; set; }

    /// <summary><c>min(…)</c>/<c>max(…)</c>/<c>increment(…)</c> given separately (encoder, old sliders).</summary>
    public double? MinOverride { get; set; }

    /// <inheritdoc cref="MinOverride"/>
    public double? MaxOverride { get; set; }

    /// <inheritdoc cref="MinOverride"/>
    public double? IncrementOverride { get; set; }

    /// <summary>Returns the colour given to <paramref name="name"/> or the first of <paramref name="fallbacks"/> that exists.</summary>
    /// <param name="name">Preferred identifier name (e.g. <c>colour:1</c>).</param>
    /// <param name="fallbacks">Names tried in order when <paramref name="name"/> is absent.</param>
    /// <returns>The colour, or <c>null</c> when none was given (the UI then uses its theme default).</returns>
    public CabbageColour? Colour(string name, params string[] fallbacks)
    {
        if (Colours.TryGetValue(name, out var c)) return c;
        foreach (var f in fallbacks)
            if (Colours.TryGetValue(f, out c)) return c;
        return null;
    }

    /// <summary>Label shown with the widget (first <c>text</c> string, or the caption).</summary>
    public string Label => Text.Count > 0 ? Text[0] : Caption ?? "";

    /// <summary>The choices of a combobox/optionbutton: <c>items(…)</c>, or <c>text(…)</c> as Cabbage 2 also accepts.</summary>
    public IReadOnlyList<string> Choices => Items.Count > 0 ? Items : Text;

    /// <inheritdoc/>
    public override string ToString() => $"{Type} {string.Join("/", Channels)} @{Bounds.X},{Bounds.Y} {Bounds.Width}x{Bounds.Height}";
}

/// <summary>A parsed <c>&lt;Cabbage&gt;</c> section.</summary>
/// <remarks>
/// Produced by <see cref="CabbageParser.Parse"/> (or <see cref="CabbageSupport.FromAnnotations"/>)
/// and held by the document view model (<see cref="CsoundVisual.ViewModels.DocumentViewModel.CabbageForm"/>,
/// re-parsed when the text changes); the default 400×300
/// size matches Cabbage when the <c>form</c> line has no <c>size(…)</c>.
/// </remarks>
public sealed class CabbageForm
{
    /// <summary>Window caption (<c>form caption(…)</c>).</summary>
    public string Caption { get; set; } = "";

    /// <summary>
    /// True when the form was built from <c>;@knob</c>-style annotations of an ordinary document
    /// (see <see cref="CabbageSupport.FromAnnotations"/>), not from a <c>&lt;Cabbage&gt;</c> section.
    /// </summary>
    public bool FromAnnotations { get; init; }

    /// <summary>Form width in Cabbage pixels.</summary>
    public double Width { get; set; } = 400;

    /// <summary>Form height in Cabbage pixels.</summary>
    public double Height { get; set; } = 300;

    /// <summary>Background colour of the form.</summary>
    public CabbageColour? Colour { get; set; }

    /// <summary>The <c>form</c> line itself (null when the section had none).</summary>
    public CabbageWidget? FormWidget { get; set; }

    /// <summary>Top-level widgets (children are nested in <see cref="CabbageWidget.Children"/>).</summary>
    public List<CabbageWidget> Widgets { get; } = [];

    /// <summary>Problems found while parsing (line, message), e.g. unknown widget types or bad arguments.</summary>
    public List<(int Line, string Message)> Warnings { get; } = [];

    /// <summary>Identifier names that were seen but are not interpreted (with how many times).</summary>
    public SortedDictionary<string, int> IgnoredIdentifiers { get; } = new(StringComparer.Ordinal);

    /// <summary>Widget types that are not drawn (with how many times).</summary>
    public SortedDictionary<string, int> UnsupportedTypes { get; } = new(StringComparer.Ordinal);

    /// <summary>Every widget, depth first (parents before their children).</summary>
    public IEnumerable<CabbageWidget> AllWidgets()
    {
        // Iterative pre-order walk; items are pushed in reverse so they pop in declaration order.
        var stack = new Stack<CabbageWidget>(Enumerable.Reverse(Widgets));
        while (stack.Count > 0)
        {
            var w = stack.Pop();
            yield return w;
            for (int i = w.Children.Count - 1; i >= 0; i--)
                stack.Push(w.Children[i]);
        }
    }
}
