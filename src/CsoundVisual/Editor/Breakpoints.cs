using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using CsoundVisual.Engine;
using CsoundVisual.Localization;
using CsoundVisual.Theming;

namespace CsoundVisual.Editor;

// Editor side of the breakpoint feature (see docs/en/editor-intelligence.md, "Breakpoints").
//
//   BreakpointStore        per-document set of breakpoint lines that survives edits (text anchors)
//   BreakpointRules        where Csound 7's debugger can actually stop (k/a-rate lines in instr blocks)
//   BreakpointLineMap      editor line <-> Csound line translation for the code that is running
//   BreakpointMargin       clickable gutter strip (red dots, yellow "paused here" arrow)
//   BreakpointLineRenderer background bands for breakpoint lines and the paused line
//
// The engine side lives in Engine/BreakpointSession.cs and Engine/CsoundEngine.Debugger.cs; the
// glue (which document is playing, F9/F5/F10, the Debug panel) is in MainViewModel.Debug.cs and
// MainWindow.Breakpoints.cs. Everything in this file runs on the UI thread only: the performance
// thread never touches these objects, it receives plain BreakpointRequest values instead.

/// <summary>
/// The breakpoints of one document, kept on the right lines while the text is edited: each one
/// is a <see cref="TextAnchor"/> at the start of its line that moves with insertions before it.
/// Breakpoints live only for the session (they are not saved in the file).
/// </summary>
/// <remarks>
/// Owned by the document's view model (one store per open tab). When the line holding an anchor
/// is deleted the anchor becomes <see cref="TextAnchor.IsDeleted"/> and is dropped lazily the next
/// time <see cref="Lines"/> is read. Two anchors can end up on the same line (e.g. after joining
/// lines); <see cref="Lines"/> reports that line once.
/// </remarks>
public sealed class BreakpointStore
{
    /// <summary>The document the anchors belong to.</summary>
    readonly TextDocument document;

    /// <summary>One anchor per breakpoint, at the start offset of its line. Unordered.</summary>
    readonly List<TextAnchor> anchors = [];

    /// <summary>Creates an empty store for <paramref name="document"/>.</summary>
    public BreakpointStore(TextDocument document) => this.document = document;

    /// <summary>Raised when a breakpoint is added or removed (not when edits move them).</summary>
    public event Action? Changed;

    /// <summary>1-based lines that have a breakpoint, ascending, without duplicates.</summary>
    /// <remarks>Builds a new list on every read (and purges deleted anchors as a side effect).</remarks>
    public IReadOnlyList<int> Lines
    {
        get
        {
            anchors.RemoveAll(a => a.IsDeleted);
            return [.. anchors.Select(a => a.Line).Distinct().Order()];
        }
    }

    /// <summary>Number of breakpoints.</summary>
    public int Count => Lines.Count;

    /// <summary>True when <paramref name="line"/> has a breakpoint.</summary>
    public bool Contains(int line) => anchors.Any(a => !a.IsDeleted && a.Line == line);

    /// <summary>Adds or removes the breakpoint of <paramref name="line"/>; returns true when it is now set.</summary>
    /// <param name="line">1-based editor line. Out-of-range lines are ignored (returns <c>false</c>, no event).</param>
    /// <remarks>
    /// Does not validate the line against <see cref="BreakpointRules"/>; callers check that first
    /// so they can explain a refusal in the status bar.
    /// </remarks>
    public bool Toggle(int line)
    {
        if (Contains(line))
        {
            // Remove every anchor on that line (there may be duplicates) and purge deleted ones.
            anchors.RemoveAll(a => a.IsDeleted || a.Line == line);
            Changed?.Invoke();
            return false;
        }
        if (line < 1 || line > document.LineCount)
            return false;
        var anchor = document.CreateAnchor(document.GetLineByNumber(line).Offset);
        // Typing at the start of the line (or Enter there) pushes the breakpoint along with the code.
        anchor.MovementType = AnchorMovementType.AfterInsertion;
        anchors.Add(anchor);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Removes every breakpoint.</summary>
    public void Clear()
    {
        if (anchors.Count == 0) return;
        anchors.Clear();
        Changed?.Invoke();
    }
}

/// <summary>Rules for where a breakpoint can stop (what Csound 7's debugger supports).</summary>
/// <remarks>
/// Csound's debugger checks breakpoints only while walking the k-rate/a-rate opcode chain of an
/// instrument instance (once per control cycle). Init-time code, header code and the bodies of
/// user-defined opcodes are never visited by that check, so a breakpoint there would silently
/// never fire. These rules reject such lines up front with an explanation. The i-rate detection is
/// a textual heuristic (<see cref="InitOnly"/>), not real type inference.
/// </remarks>
public static partial class BreakpointRules
{
    /// <summary>
    /// Checks a line. Returns <c>null</c> when a breakpoint there can stop the performance,
    /// otherwise a short explanation for the status bar.
    /// </summary>
    /// <param name="analyzer">Analysis of the current text.</param>
    /// <param name="line">1-based line.</param>
    public static string? Check(CsoundAnalyzer analyzer, int line)
    {
        if (line < 1 || line > analyzer.LineCount)
            return Loc.S("Esa línea no existe.", "That line does not exist.");
        var block = analyzer.BlockAt(line);
        if (block is null)
            return Loc.S("Los breakpoints van en líneas de código dentro de un instrumento (instr … endin) de <CsInstruments>.",
                         "Breakpoints go on code lines inside an instrument (instr … endin) in <CsInstruments>.");
        if (block.Keyword != "instr")
            return Loc.S("El depurador de Csound 7 no se detiene dentro de opcodes de usuario (UDO): pon el breakpoint en la línea del instrumento que lo llama.",
                         "Csound 7's debugger does not stop inside user-defined opcodes (UDOs): put the breakpoint on the instrument line that calls it.");
        if (line == block.StartLine || line == block.EndLine)
            return Loc.S("Pon el breakpoint en una línea de código del instrumento, no en «instr» ni en «endin».",
                         "Put the breakpoint on a code line of the instrument, not on «instr» or «endin».");
        string code = StripComment(analyzer.LineText(line)).Trim();
        if (code.Length == 0)
            return Loc.S("La línea está vacía o es un comentario: no hay nada donde detenerse.",
                         "The line is empty or a comment: there is nothing to stop at.");
        if (InitOnly().IsMatch(code))
            return Loc.S("Esta línea solo corre al iniciar la nota (tasa i): el depurador de Csound se detiene solo en código de tasa k/a.",
                         "This line only runs when the note starts (i-rate): Csound's debugger only stops at k/a-rate code.");
        return null;
    }

    /// <summary>
    /// Cuts a line at its first <c>;</c> or <c>//</c> comment marker. Does not know about strings,
    /// so a <c>;</c> inside quotes also cuts (harmless here: it only decides "is there code?").
    /// </summary>
    static string StripComment(string line)
    {
        int semi = line.IndexOf(';');
        if (semi >= 0) line = line[..semi];
        int slashes = line.IndexOf("//", StringComparison.Ordinal);
        if (slashes >= 0) line = line[..slashes];
        return line;
    }

    /// <summary>Lines whose outputs are all i-variables (<c>ifreq = p4</c>, <c>iamp, ifn init …</c>) or plain declarations.</summary>
    /// <remarks>
    /// First alternative: one or more comma-separated <c>i</c>/<c>gi</c> names (optionally arrays
    /// <c>[]</c>) followed by <c>=</c>, <c>init</c> or any opcode name and a space, excluding lines
    /// that begin with the keywords <c>if</c>/<c>elseif</c>. Second alternative: statements that
    /// only act at init time even without outputs (<c>print</c>, <c>prints</c>, <c>massign</c>,
    /// <c>pgmassign</c>). Applied to the comment-stripped, trimmed line.
    /// </remarks>
    [GeneratedRegex(@"^(?!if\b|elseif\b)(?:gi|i)\w*(?:\[\])?\s*(?:,\s*(?:gi|i)\w*(?:\[\])?\s*)*(?:=|init\b|[a-z]\w*\s)|^(?:prints|print|massign|pgmassign)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex InitOnly();
}

/// <summary>
/// Translates editor lines into the line numbers Csound uses for the running code, and back,
/// for one playing document.
/// </summary>
/// <remarks>
/// <para>
/// When a document starts playing, the CSD is compiled as it is in the editor (the
/// <c>&lt;CsOptions&gt;</c> content is replaced by blank lines), so Csound's line numbers equal the
/// editor's. Afterwards the user edits the text while the old code keeps running, so the map keeps
/// one entry per instrument block: where it starts in Csound's numbering and a text anchor that
/// follows the block in the editor. An editor line inside a block maps to
/// <c>orcStart + (line − blockStartNow)</c>.
/// </para>
/// <para>
/// A live recompile (Ctrl+Enter, dragging a number) compiles the block with
/// <see cref="CsoundEngine.CompileOrcAt"/>, which pads it so Csound numbers it with the
/// editor's current lines; <see cref="Recompiled"/> then resets that block's entry.
/// </para>
/// </remarks>
/// <remarks>
/// Created by the view model when a document starts playing and discarded when the performance
/// stops. Used in both directions: <see cref="ToRequests"/> when breakpoints are sent to the
/// engine, <see cref="ToEditorLine"/> when the engine reports where it paused.
/// </remarks>
public sealed class BreakpointLineMap
{
    /// <summary>One compiled instrument block.</summary>
    sealed class Entry
    {
        /// <summary>
        /// The block's instrument ids as written after <c>instr</c> (numbers or names, quotes
        /// removed). <c>instr 1, Bass</c> yields two ids; a breakpoint is requested for each.
        /// </summary>
        public required string[] Ids;
        /// <summary>Line of the <c>instr</c> header in Csound's numbering of the running code.</summary>
        public required int OrcStart;
        /// <summary><c>endin</c> line minus <c>instr</c> line when compiled (the block spans OrcStart … OrcStart + Length inclusive).</summary>
        public required int Length;
        /// <summary>
        /// Anchor at the start of the <c>instr</c> line in the editor; follows the block as lines are
        /// inserted or removed above it (<c>SurviveDeletion</c> keeps it usable if its own line is
        /// edited away).
        /// </summary>
        public required TextAnchor Anchor;
    }

    /// <summary>The editor document of the playing tab.</summary>
    readonly TextDocument document;

    /// <summary>One entry per instrument block as last compiled (initial compile or live recompile).</summary>
    readonly List<Entry> entries = [];

    /// <summary>Line of the <c>&lt;CsInstruments&gt;</c> tag when the document was compiled (Csound numbers live code after it).</summary>
    public int CsInstrumentsLine { get; }

    /// <summary>Builds the map for a document that is about to be compiled as it is now.</summary>
    /// <param name="document">The editor document.</param>
    /// <param name="analyzer">An analysis of exactly the text being compiled; at this moment
    /// editor lines and Csound lines coincide, so each block's <c>OrcStart</c> is its editor line.</param>
    public BreakpointLineMap(TextDocument document, CsoundAnalyzer analyzer)
    {
        this.document = document;
        CsInstrumentsLine = FindCsInstrumentsLine(analyzer);
        foreach (var block in analyzer.Blocks.Where(b => b.Keyword == "instr"))
            entries.Add(NewEntry(block, block.StartLine));
    }

    /// <summary>First line containing <c>&lt;CsInstruments&gt;</c> (case-insensitive), or 0 for a bare orchestra.</summary>
    static int FindCsInstrumentsLine(CsoundAnalyzer analyzer)
    {
        for (int line = 1; line <= analyzer.LineCount; line++)
            if (analyzer.LineText(line).Contains("<CsInstruments>", StringComparison.OrdinalIgnoreCase))
                return line;
        return 0;
    }

    /// <summary>Creates an entry for <paramref name="block"/> (as it is now in the editor) compiled at Csound line <paramref name="orcStart"/>.</summary>
    Entry NewEntry(CodeBlock block, int orcStart)
    {
        // AfterInsertion: text typed at the very start of the instr line pushes the anchor along,
        // keeping it on the header line rather than on a new line inserted above it.
        var anchor = document.CreateAnchor(document.GetLineByNumber(Math.Clamp(block.StartLine, 1, document.LineCount)).Offset);
        anchor.MovementType = AnchorMovementType.AfterInsertion;
        anchor.SurviveDeletion = true;
        return new Entry { Ids = [.. block.Ids.Select(Clean)], OrcStart = orcStart, Length = block.EndLine - block.StartLine, Anchor = anchor };
    }

    /// <summary>Normalises an instrument id for comparison: trims spaces and surrounding quotes (<c>"Voice"</c> → <c>Voice</c>).</summary>
    static string Clean(string id) => id.Trim().Trim('"');

    /// <summary>Records that <paramref name="block"/> (as it is now in the editor) was recompiled starting at Csound line <paramref name="orcStart"/>.</summary>
    /// <remarks>
    /// Any older entry sharing an id with the block is replaced: Csound's new definition of that
    /// instrument supersedes the old one for new notes. (Notes already running keep the old code
    /// and its old line numbers until they end.)
    /// </remarks>
    public void Recompiled(CodeBlock block, int orcStart)
    {
        var ids = block.Ids.Select(Clean).ToHashSet();
        entries.RemoveAll(e => e.Ids.Any(ids.Contains));
        entries.Add(NewEntry(block, orcStart));
    }

    /// <summary>Csound breakpoints for the given editor lines (lines outside known instruments are skipped).</summary>
    /// <param name="editorLines">1-based lines from the document's <see cref="BreakpointStore"/>.</param>
    /// <param name="analyzer">Analysis of the editor's <i>current</i> text, used to find the block each line is in now.</param>
    /// <returns>One request per (instrument id, Csound line); an <c>instr 1, Bass</c> block gives two per line.</returns>
    /// <remarks>
    /// The offset of the line inside its block is measured in the current text
    /// (<c>line − block.StartLine</c>) and added to the block's compiled start. This is exact as long as
    /// the lines <i>inside</i> the block were not edited since it was compiled; edits above the block
    /// are absorbed because the current <c>StartLine</c> moved by the same amount.
    /// </remarks>
    public List<BreakpointRequest> ToRequests(IEnumerable<int> editorLines, CsoundAnalyzer analyzer)
    {
        var list = new List<BreakpointRequest>();
        foreach (int line in editorLines)
        {
            if (analyzer.BlockAt(line) is not { Keyword: "instr" } block)
                continue;
            var ids = block.Ids.Select(Clean).ToList();
            var entry = entries.FirstOrDefault(e => e.Ids.Any(ids.Contains));
            if (entry is null)
                continue; // a new instrument that was never compiled
            int orcLine = entry.OrcStart + (line - block.StartLine);
            foreach (var id in entry.Ids)
                list.Add(new BreakpointRequest(id, orcLine));
        }
        return list;
    }

    /// <summary>Editor line of Csound line <paramref name="orcLine"/> in <paramref name="instrument"/>, or <c>null</c>.</summary>
    /// <param name="instrument">Instrument id reported by the engine (number or name, quotes optional).</param>
    /// <param name="orcLine">Line in Csound's numbering of the running code.</param>
    /// <remarks>
    /// Prefers the entry of that instrument whose compiled range contains the line; if the id does
    /// not match (the engine may report a number for a named instrument), any entry whose range
    /// contains the line is used. The result is the block's current editor position (its anchor)
    /// plus the offset inside the block, or <c>null</c> if that falls outside the document.
    /// </remarks>
    public int? ToEditorLine(string instrument, int orcLine)
    {
        var entry = entries.FirstOrDefault(e => e.Ids.Contains(Clean(instrument)) && orcLine >= e.OrcStart && orcLine <= e.OrcStart + e.Length)
                    ?? entries.FirstOrDefault(e => orcLine >= e.OrcStart && orcLine <= e.OrcStart + e.Length);
        if (entry is null)
            return null;
        int line = entry.Anchor.Line + (orcLine - entry.OrcStart);
        return line >= 1 && line <= document.LineCount ? line : null;
    }
}

/// <summary>
/// Gutter left of the line numbers: a red dot per breakpoint and a yellow arrow on the line where
/// the performance is paused. Clicking a line toggles its breakpoint.
/// </summary>
/// <remarks>
/// Inserted by the main window into <c>TextArea.LeftMargins</c> before the line numbers. The margin
/// holds no state of its own except the hover line: it reads the shown document's store and the
/// paused line through delegates on every render, so switching tabs needs only a redraw. It redraws
/// itself when the text view scrolls or relayouts; the owner calls <c>InvalidateVisual</c> when the
/// breakpoints or the paused line change.
/// </remarks>
public sealed class BreakpointMargin : AbstractMargin
{
    /// <summary>Returns the store of the document currently shown (or <c>null</c>).</summary>
    readonly Func<BreakpointStore?> store;
    /// <summary>Returns the editor line where the shown document is paused, or <c>null</c>.</summary>
    readonly Func<int?> pausedLine;
    /// <summary>Click handler: validates and toggles the breakpoint of a line (owner logic).</summary>
    readonly Action<int> toggle;
    /// <summary>Line under the mouse (draws a faint "ghost" dot as a click hint), 0 for none.</summary>
    int hoverLine;

    /// <summary>Creates the margin.</summary>
    /// <param name="store">Breakpoints of the shown document.</param>
    /// <param name="pausedLine">Line where the shown document is paused, or <c>null</c>.</param>
    /// <param name="toggle">Called with the clicked line.</param>
    public BreakpointMargin(Func<BreakpointStore?> store, Func<int?> pausedLine, Action<int> toggle)
    {
        this.store = store;
        this.pausedLine = pausedLine;
        this.toggle = toggle;
        Cursor = new Cursor(StandardCursorType.Hand);
        Background = Brushes.Transparent;
    }

    /// <summary>Background (transparent, so the whole strip is clickable).</summary>
    public IBrush Background { get; set; }

    /// <summary>Fixed 18-DIP width; height 0 because the editor stretches margins to its own height.</summary>
    protected override Size MeasureOverride(Size availableSize) => new(18, 0);

    /// <summary>Moves the scroll/layout subscriptions from the old text view to the new one.</summary>
    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
            oldTextView.ScrollOffsetChanged -= OnVisualLinesChanged;
        }
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
            newTextView.ScrollOffsetChanged += OnVisualLinesChanged;
        }
        InvalidateVisual();
    }

    /// <summary>Scroll or relayout of the text view: the dots must follow their lines.</summary>
    void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>Line under a point of this margin, or 0.</summary>
    /// <param name="p">Point in this margin's coordinates (same vertical origin as the text view).</param>
    int LineAt(Point p)
    {
        // Visual tops are in document coordinates: add the scroll offset to the view-relative Y.
        if (TextView is not { VisualLinesValid: true } view) return 0;
        var visual = view.GetVisualLineFromVisualTop(p.Y + view.ScrollOffset.Y);
        return visual?.FirstDocumentLine.LineNumber ?? 0;
    }

    /// <summary>Left click toggles the breakpoint of the clicked line; the event is handled so the
    /// text area does not also move the caret or start a selection.</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        int line = LineAt(e.GetPosition(this));
        if (line > 0)
        {
            toggle(line);
            InvalidateVisual();
        }
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        int line = LineAt(e.GetPosition(this));
        if (line != hoverLine) { hoverLine = line; InvalidateVisual(); }
    }

    /// <inheritdoc/>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        hoverLine = 0;
        InvalidateVisual();
    }

    /// <summary>
    /// Draws, for each visible line: a red dot (breakpoint), a faint dot (hover hint) and, on the
    /// paused line, a yellow arrow over the dot.
    /// </summary>
    public override void Render(DrawingContext context)
    {
        // Filling the whole strip (even transparently) makes it hit-testable everywhere.
        context.FillRectangle(Background, new Rect(Bounds.Size));
        if (TextView is not { VisualLinesValid: true } view) return;
        var lines = store()?.Lines ?? [];
        int? paused = pausedLine();
        var dot = AppTheme.Brush("Record");
        var ghost = AppTheme.Brush("Danger_40");
        var arrow = AppTheme.Brush("Warn");
        double cx = Bounds.Width / 2;
        foreach (var visual in view.VisualLines)
        {
            int n = visual.FirstDocumentLine.LineNumber;
            double top = visual.VisualTop - view.ScrollOffset.Y;
            // Centre on the first wrapped row of a long line, not on the middle of the whole block.
            double cy = top + Math.Min(visual.Height, visual.TextLines.FirstOrDefault()?.Height ?? visual.Height) / 2;
            // Radius scales with the font size (Ctrl+wheel zoom) but never exceeds 5.5 DIP.
            double r = Math.Min(5.5, visual.Height * 0.32);
            if (lines.Contains(n))
                context.DrawEllipse(dot, null, new Point(cx, cy), r, r);
            else if (n == hoverLine)
                context.DrawEllipse(ghost, null, new Point(cx, cy), r, r);
            if (paused == n)
            {
                // ➜ arrow over the dot: a 7-point polygon (shaft from the left, triangular head
                // pointing right), outlined in the deep background colour so it stands out on red.
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(cx - r - 1, cy - r * 0.45), true);
                    c.LineTo(new Point(cx + 0.5, cy - r * 0.45));
                    c.LineTo(new Point(cx + 0.5, cy - r - 1));
                    c.LineTo(new Point(cx + r + 2, cy));
                    c.LineTo(new Point(cx + 0.5, cy + r + 1));
                    c.LineTo(new Point(cx + 0.5, cy + r * 0.45));
                    c.LineTo(new Point(cx - r - 1, cy + r * 0.45));
                    c.EndFigure(true);
                }
                context.DrawGeometry(arrow, new Pen(AppTheme.Brush("BgDeep"), 1), g);
            }
        }
    }
}

/// <summary>
/// Editor background for breakpoints: a faint red band on lines with a breakpoint and a yellow
/// band on the line where the performance is paused.
/// </summary>
/// <remarks>
/// Uses the same delegates as <see cref="BreakpointMargin"/>. The paused band wins over the
/// breakpoint band on the same line. Like the other renderers it only paints the visible lines.
/// </remarks>
public sealed class BreakpointLineRenderer : IBackgroundRenderer
{
    /// <summary>Returns the store of the document currently shown (or <c>null</c>).</summary>
    readonly Func<BreakpointStore?> store;
    /// <summary>Returns the editor line where the shown document is paused, or <c>null</c>.</summary>
    readonly Func<int?> pausedLine;

    /// <summary>Creates the renderer.</summary>
    /// <param name="store">Breakpoints of the shown document.</param>
    /// <param name="pausedLine">Line where the shown document is paused, or <c>null</c>.</param>
    public BreakpointLineRenderer(Func<BreakpointStore?> store, Func<int?> pausedLine)
    {
        this.store = store;
        this.pausedLine = pausedLine;
    }

    /// <inheritdoc/>
    public KnownLayer Layer => KnownLayer.Background;

    /// <inheritdoc/>
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid) return;
        var lines = store()?.Lines ?? [];
        int? paused = pausedLine();
        if (lines.Count == 0 && paused is null) return;
        var band = AppTheme.Brush("Danger_18");
        var pausedBand = AppTheme.Brush("Warn_40");
        foreach (var visual in textView.VisualLines)
        {
            int n = visual.FirstDocumentLine.LineNumber;
            var rect = new Rect(0, visual.VisualTop - textView.ScrollOffset.Y, textView.Bounds.Width, visual.Height);
            if (paused == n)
                drawingContext.DrawRectangle(pausedBand, new Pen(AppTheme.Brush("Warn"), 1), rect);
            else if (lines.Contains(n))
                drawingContext.DrawRectangle(band, null, rect);
        }
    }
}
