using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using CsoundVisual.Localization;
using CsoundVisual.Theming;

namespace CsoundVisual.Editor;

/// <summary>
/// Autocompletion for the code editor: after typing two letters of a word, a list offers the
/// matching opcodes (with their signatures), the variables already used in the document and the
/// language keywords. Enter/Tab accepts, Esc closes, typing keeps filtering.
/// </summary>
/// <remarks>
/// <para>
/// The main window creates one instance for its single shared <see cref="TextEditor"/> (tabs swap
/// the editor's document, not the editor), and it lives as long as the window. It hooks the text area's
/// <c>TextEntering</c>/<c>TextEntered</c> events and drives AvaloniaEdit's stock
/// <see cref="CompletionWindow"/>; filtering while typing is done by the window itself, so the
/// item list is built only once, when the window opens.
/// </para>
/// <para>
/// Everything runs on the UI thread. The list is rebuilt from the whole document text each time it
/// opens (a regex scan for variable-looking words, plus the static keyword table and
/// <see cref="OpcodeIndex.Names"/>); this is cheap enough for hand-written <c>.csd</c> files and
/// avoids keeping an index in sync with edits. The result is capped at 400 entries to keep the
/// popup responsive.
/// </para>
/// <para>
/// Ordering: AvaloniaEdit picks the best match by <see cref="ICompletionData.Priority"/> among
/// equally good prefix matches, so document variables (2) win over keywords (1) and opcodes (0).
/// </para>
/// </remarks>
public sealed partial class CompletionProvider
{
    /// <summary>
    /// Csound language keywords and orchestra header constants offered in the list. Opcodes are
    /// not listed here; they come from <see cref="OpcodeIndex"/>.
    /// </summary>
    static readonly string[] Keywords = ["instr", "endin", "opcode", "endop", "if", "then", "elseif", "else", "endif",
        "while", "do", "od", "until", "for", "in", "goto", "return", "struct", "sr", "kr", "ksmps", "nchnls", "nchnls_i", "0dbfs", "A4"];

    /// <summary>The editor this provider serves.</summary>
    readonly TextEditor editor;

    /// <summary>
    /// The popup currently shown, or <c>null</c> when no list is open. Cleared by the window's
    /// <c>Closed</c> event, so a non-null value always means "open".
    /// </summary>
    CompletionWindow? window;

    /// <summary>Attaches autocompletion to <paramref name="editor"/>.</summary>
    /// <param name="editor">The AvaloniaEdit control to watch; its text-input events are subscribed for the provider's lifetime.</param>
    public CompletionProvider(TextEditor editor)
    {
        this.editor = editor;
        editor.TextArea.TextEntered += OnTextEntered;
        editor.TextArea.TextEntering += OnTextEntering;
    }

    /// <summary>Opens the list explicitly (Ctrl+Space).</summary>
    public void Show() => Open(force: true);

    /// <summary>
    /// Raised before a typed character is inserted. If the list is open and the character ends a
    /// word (space, parenthesis, comma, operator…), the selected item is inserted first, so typing
    /// <c>vco2</c>+<c>(</c> completes the word and then inserts the parenthesis, like in an IDE.
    /// </summary>
    void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        // A non-word character while the list is open accepts the selected item first.
        if (window is not null && e.Text is { Length: > 0 } t && !IsWordChar(t[0]))
            window.CompletionList.RequestInsertion(e);
    }

    /// <summary>
    /// Raised after a character was inserted: a single word character typed while no list is open
    /// may open one (only once the word under the caret has at least two characters, see
    /// <see cref="Open"/>). Pasted text (more than one character) never opens the list.
    /// </summary>
    void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (window is null && e.Text is { Length: 1 } t && IsWordChar(t[0]))
            Open(force: false);
    }

    /// <summary>
    /// Builds the item list for the word that ends at the caret and shows the popup.
    /// </summary>
    /// <param name="force">
    /// <c>true</c> for an explicit request (Ctrl+Space): opens even with an empty or one-letter
    /// prefix. <c>false</c> for automatic opening while typing: requires at least two letters.
    /// In both cases nothing opens inside a comment or string, or when nothing matches.
    /// </param>
    void Open(bool force)
    {
        var doc = editor.Document;
        int caret = editor.TextArea.Caret.Offset;
        // Walk back from the caret to the start of the current word; [start, caret) is the
        // prefix the window will replace when an item is accepted.
        int start = caret;
        while (start > 0 && IsWordChar(doc.GetCharAt(start - 1))) start--;
        string prefix = doc.GetText(start, caret - start);
        if (!force && prefix.Length < 2)
            return;
        if (InCommentOrString(doc, caret))
            return;

        var items = BuildItems(doc.Text, prefix);
        if (items.Count == 0)
            return;

        window = new CompletionWindow(editor.TextArea) { StartOffset = start, EndOffset = caret, MinWidth = 340 };
        foreach (var item in items)
            window.CompletionList.CompletionData.Add(item);
        // Pre-select the best match and let the window keep filtering as the user types.
        window.CompletionList.SelectItem(prefix);
        window.Closed += (_, _) => window = null;
        window.Show();
    }

    /// <summary>Characters that can appear in a Csound identifier (letters, digits, underscore).</summary>
    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Cheap line-local test: is <paramref name="offset"/> after a <c>;</c> or <c>//</c> comment
    /// marker, or inside an unterminated double-quoted string, on its own line?
    /// </summary>
    /// <remarks>
    /// Heuristic only: a <c>;</c> inside a string, escaped quotes, <c>{{ }}</c> strings and
    /// <c>/* */</c> block comments are not understood. False positives merely suppress the popup.
    /// </remarks>
    static bool InCommentOrString(TextDocument doc, int offset)
    {
        var line = doc.GetLineByOffset(offset);
        string before = doc.GetText(line.Offset, offset - line.Offset);
        return before.Contains(';') || before.Contains("//") || before.Count(c => c == '"') % 2 == 1;
    }

    /// <summary>
    /// Collects the entries whose text starts with <paramref name="prefix"/> (case-insensitive):
    /// document variables, then keywords, then opcodes, without duplicates.
    /// </summary>
    /// <param name="text">The whole document text, scanned for variable names.</param>
    /// <param name="prefix">The word fragment before the caret; empty matches everything.</param>
    /// <returns>At most 400 items, in insertion order (the window sorts by its own rules).</returns>
    static List<ICompletionData> BuildItems(string text, string prefix)
    {
        var items = new List<ICompletionData>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool Matches(string s) => prefix.Length == 0 || s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        // Variables used in this document first: they are what you type most.
        // The word being typed is itself in the text, so it is skipped (m.Value != prefix) to avoid
        // offering exactly what is already there. Words that are keywords (instr, if…) or opcode
        // names (init, adsr, oscili…) also match the loose regex; they are skipped here so they
        // are listed once, under their real kind, instead of as a "variable" that would win the
        // selection by priority. (The check runs before seen.Add, so the keyword/opcode loops
        // below still add them.)
        foreach (Match m in VariableWord().Matches(text))
            if (Matches(m.Value) && m.Value != prefix && !IsLanguageWord(m.Value) && seen.Add(m.Value))
                items.Add(new Item(m.Value, "variable", Loc.S("Variable usada en este archivo.", "Variable used in this file."), 2));
        foreach (string k in Keywords)
            if (Matches(k) && seen.Add(k))
                items.Add(new Item(k, Loc.S("palabra clave", "keyword"), Loc.S("Palabra clave del lenguaje Csound.", "Csound language keyword."), 1));
        foreach (string op in OpcodeIndex.Names)
            if (Matches(op) && seen.Add(op))
            {
                // Row shows the first signature; the tooltip lists up to six overloads.
                var sigs = OpcodeIndex.Get(op);
                string first = sigs.Length > 0 ? OpcodeIndex.FormatSignature(op, sigs[0]) : op;
                string all = sigs.Length > 0 ? string.Join("\n", sigs.Take(6).Select(s => OpcodeIndex.FormatSignature(op, s))) : "opcode";
                items.Add(new Item(op, first, all + Loc.S("\n\nF1 sobre el opcode abre su página en el manual.", "\n\nF1 on the opcode opens its page in the manual."), 0));
            }
        return items.Count > 400 ? items.Take(400).ToList() : items;
    }

    /// <summary>
    /// True for words that belong to the language rather than to the document: the
    /// <see cref="Keywords"/> table and every known opcode name (<see cref="OpcodeIndex.IsOpcode"/>).
    /// </summary>
    /// <param name="word">A word matched by <see cref="VariableWord"/>.</param>
    static bool IsLanguageWord(string word) =>
        Array.IndexOf(Keywords, word) >= 0 || OpcodeIndex.IsOpcode(word);

    /// <summary>
    /// Words that look like Csound variables: an optional <c>g</c> (global) prefix, a rate letter
    /// (<c>a</c> audio, <c>i</c> init, <c>k</c> control, <c>S</c> string, <c>f</c> f-signal,
    /// <c>w</c> spectral) and then the name.
    /// </summary>
    /// <remarks>
    /// Loose by design: the second alternative also matches ordinary words that merely begin with
    /// one of those letters (for example <c>instr</c>, <c>if</c>, <c>init</c>, <c>adsr</c>);
    /// <see cref="BuildItems"/> filters those out with <see cref="IsLanguageWord"/>.
    /// </remarks>
    [GeneratedRegex(@"\b(?:g?[aikSfw][A-Z_][A-Za-z0-9_]*|g?[aikSfw][a-z][A-Za-z0-9_]*\d*)\b")]
    private static partial Regex VariableWord();

    /// <summary>One entry of the completion list.</summary>
    /// <param name="text">The word inserted when the entry is accepted (also what the window filters on).</param>
    /// <param name="detail">Short dimmed label shown to the right of the word (kind or first signature).</param>
    /// <param name="description">Tooltip text shown beside the list for the selected entry.</param>
    /// <param name="priority">Tie-breaker for selection: 2 variables, 1 keywords, 0 opcodes.</param>
    sealed class Item(string text, string detail, string description, double priority) : ICompletionData
    {
        /// <summary>No icon is shown.</summary>
        public Avalonia.Media.IImage? Image => null;

        /// <inheritdoc/>
        public string Text { get; } = text;

        /// <summary>Row visual: the word in the code font followed by the dimmed <c>detail</c> label (theme brush <c>TextDim</c>).</summary>
        public object Content { get; } = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = text, FontFamily = new FontFamily("Cascadia Code, Consolas, monospace") },
                new TextBlock { Text = detail, Foreground = AppTheme.Brush("TextDim"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        /// <inheritdoc/>
        public object Description { get; } = description;

        /// <inheritdoc/>
        public double Priority { get; } = priority;

        /// <summary>Replaces the typed prefix (the window's start…end segment) with <see cref="Text"/>.</summary>
        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, Text);
    }
}
