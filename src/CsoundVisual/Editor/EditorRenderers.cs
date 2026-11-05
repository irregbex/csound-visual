using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CsoundVisual.Theming;

namespace CsoundVisual.Editor;

// This file holds the AvaloniaEdit background renderers that the main window adds to each editor's
// TextView.BackgroundRenderers. They all draw on KnownLayer.Background (behind the text, above the
// current-line highlight) and run on the UI thread during the TextView's render pass. None of them
// triggers a redraw by itself: the owner changes their state and then calls
// TextView.InvalidateLayer(KnownLayer.Background) (or Redraw) so Draw is called again.
//
// Colours come from AppTheme.Brush(role). AppTheme hands out one stable brush object per role and
// recolours it in place when the user switches theme, so caching those brushes in static fields is
// safe and the highlights follow the theme without being recreated.

/// <summary>
/// Draws a rounded highlight behind the token under the mouse: subtle while hovering, stronger
/// while it is being dragged.
/// </summary>
/// <remarks>
/// Driven by <see cref="ScrubController"/>: it calls <see cref="Show"/> when a draggable or
/// hoverable token is under the pointer and <see cref="Clear"/> when the pointer leaves it.
/// </remarks>
public sealed class TokenHighlightRenderer : IBackgroundRenderer
{
    /// <summary>Fill while merely hovering (accent at ~30 % alpha).</summary>
    static readonly IBrush HoverFill = AppTheme.Brush("Accent_30");
    /// <summary>Fill while the token is being dragged (accent at ~60 % alpha).</summary>
    static readonly IBrush ActiveFill = AppTheme.Brush("Accent_60");
    /// <summary>1-px outline added while dragging.</summary>
    static readonly IPen ActivePen = new Pen(AppTheme.Brush("AccentHi"), 1);

    /// <summary>Highlighted document range, or <c>null</c> when nothing is highlighted.</summary>
    /// <remarks>
    /// A plain <see cref="TextSegment"/> that is <i>not</i> registered in a
    /// <see cref="TextSegmentCollection{T}"/>, so it does not move with edits; the owner refreshes
    /// it after every change (during a drag the literal is rewritten and re-shown each step).
    /// </remarks>
    TextSegment? segment;

    /// <summary>True while dragging: use <see cref="ActiveFill"/> and <see cref="ActivePen"/>.</summary>
    bool active;

    /// <inheritdoc/>
    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>Highlights <paramref name="length"/> characters from <paramref name="start"/>.</summary>
    /// <param name="start">Absolute document offset.</param>
    /// <param name="length">Number of characters.</param>
    /// <param name="isActive">True while dragging (stronger look).</param>
    public void Show(int start, int length, bool isActive)
    {
        segment = new TextSegment { StartOffset = start, Length = length };
        active = isActive;
    }

    /// <summary>Removes the highlight.</summary>
    public void Clear() => segment = null;

    /// <inheritdoc/>
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        // A stale segment beyond the end of a shortened document would make the geometry builder throw.
        if (segment is null || textView.Document is null || segment.EndOffset > textView.Document.TextLength)
            return;
        // BackgroundGeometryBuilder handles segments that wrap over several visual lines and
        // skips the parts that are scrolled out of view.
        var builder = new BackgroundGeometryBuilder { CornerRadius = 3, AlignToWholePixels = true };
        builder.AddSegment(textView, segment);
        if (builder.CreateGeometry() is { } geometry)
            drawingContext.DrawGeometry(active ? ActiveFill : HoverFill, active ? ActivePen : null, geometry);
    }
}

/// <summary>
/// Briefly flashes the lines that were just evaluated with Ctrl+Enter (live coding feedback).
/// The window lowers <see cref="Opacity"/> to zero over ≈0.5 s.
/// </summary>
/// <remarks>
/// Purely a view of three properties: the owner (the main window's live-coding handler) sets the
/// line range and <see cref="Opacity"/> = 1, then animates <see cref="Opacity"/> down with a timer,
/// invalidating the background layer each tick. The colour is a fixed light green (#C5E478) rather
/// than a theme role so "evaluated" reads the same in every theme.
/// </remarks>
public sealed class FlashRenderer : IBackgroundRenderer
{
    /// <summary>First flashed line (1-based).</summary>
    public int FirstLine { get; set; }

    /// <summary>Last flashed line (1-based).</summary>
    public int LastLine { get; set; }

    /// <summary>Current strength, 0 (invisible) … 1.</summary>
    public double Opacity { get; set; }

    /// <inheritdoc/>
    public KnownLayer Layer => KnownLayer.Background;

    /// <inheritdoc/>
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Opacity <= 0 || !textView.VisualLinesValid) return;
        // Peak alpha 110/255 (~43 %) so the text stays readable under the flash.
        var brush = new SolidColorBrush(Color.FromArgb((byte)(110 * Opacity), 0xC5, 0xE4, 0x78));
        // Only the visual lines currently laid out (on screen) are visited. VisualTop is in
        // document coordinates, so subtract the vertical scroll offset to get view coordinates.
        foreach (var visual in textView.VisualLines)
        {
            int n = visual.FirstDocumentLine.LineNumber;
            if (n < FirstLine || n > LastLine) continue;
            drawingContext.DrawRectangle(brush, null, new Rect(0, visual.VisualTop - textView.ScrollOffset.Y, textView.Bounds.Width, visual.Height));
        }
    }
}

/// <summary>
/// Paints a red band behind every line Csound reported an error for, so problems are visible in
/// the code and not only in the console.
/// </summary>
/// <param name="lines">Returns the 1-based line numbers to mark for the document currently shown.
/// Called on every draw, so it must be cheap; the owner typically returns the error set of the
/// selected <c>DocumentViewModel</c>, filled from Csound's compile messages.</param>
/// <remarks>
/// Each marked line gets a translucent red band across the view plus a solid 3-px bar at the left
/// edge. Lines that wrap are only marked on their first visual line.
/// </remarks>
public sealed class ErrorLineRenderer(Func<IReadOnlyCollection<int>> lines) : IBackgroundRenderer
{
    /// <summary>Translucent band behind the line.</summary>
    static readonly IBrush Fill = AppTheme.Brush("Record_40");
    /// <summary>Solid marker bar at the left edge.</summary>
    static readonly IBrush Edge = AppTheme.Brush("Danger");

    /// <inheritdoc/>
    public KnownLayer Layer => KnownLayer.Background;

    /// <inheritdoc/>
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        var marked = lines();
        if (marked.Count == 0 || !textView.VisualLinesValid)
            return;
        foreach (var visual in textView.VisualLines)
        {
            if (!marked.Contains(visual.FirstDocumentLine.LineNumber))
                continue;
            double y = visual.VisualTop - textView.ScrollOffset.Y;
            drawingContext.DrawRectangle(Fill, null, new Rect(0, y, textView.Bounds.Width, visual.Height));
            drawingContext.DrawRectangle(Edge, null, new Rect(0, y, 3, visual.Height));
        }
    }
}

/// <summary>
/// Marks every occurrence of the word under the caret (e.g. <c>ishift</c>) so its other uses
/// in the file are easy to spot; the occurrence at the caret gets an outline. Whole words only
/// (letters, digits, underscore), case-sensitive like Csound.
/// </summary>
/// <remarks>
/// The main window calls <see cref="Update"/> when the caret moves and <see cref="Invalidate"/>
/// after an edit (offsets shift, so the cached list is no longer valid). The same
/// <see cref="Offsets"/> list drives F3/Shift+F3 navigation, and the static <see cref="FindAll"/>
/// is reused to count the word in the other open tabs for the status bar. Nothing is drawn when
/// the word occurs only once.
/// </remarks>
public sealed class WordOccurrenceRenderer : IBackgroundRenderer
{
    /// <summary>Fill behind every occurrence.</summary>
    static readonly IBrush Fill = AppTheme.Brush("Accent_40");
    /// <summary>Outline drawn around the occurrence under the caret.</summary>
    static readonly IPen CurrentPen = new Pen(AppTheme.Brush("AccentHi"), 1);

    /// <summary>Word being highlighted, or <c>null</c>.</summary>
    public string? Word { get; private set; }

    /// <summary>Start offsets of every occurrence in the document (ascending).</summary>
    public List<int> Offsets { get; } = [];

    /// <summary>Offset of the occurrence under the caret (-1 if none).</summary>
    public int Current { get; private set; } = -1;

    /// <inheritdoc/>
    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>Forgets the current word so the next <see cref="Update"/> searches again (after an edit).</summary>
    public void Invalidate()
    {
        Word = null;
        Offsets.Clear();
    }

    /// <summary>True for characters that can be part of a Csound identifier.</summary>
    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Finds the word around <paramref name="offset"/> and all its occurrences. Numbers and
    /// one-letter words are ignored (too many matches to be useful).
    /// </summary>
    /// <param name="document">The editor document (its full text is scanned).</param>
    /// <param name="offset">Caret offset; a caret just after or just before a word counts as on it.</param>
    /// <returns>True when the highlighted word changed (the caller should redraw).</returns>
    /// <remarks>
    /// When the word is unchanged only <see cref="Current"/> is updated and the occurrence list is
    /// reused, so moving the caret inside a long file does not rescan it every keystroke.
    /// </remarks>
    public bool Update(TextDocument document, int offset)
    {
        string text = document.Text;
        offset = Math.Clamp(offset, 0, text.Length);
        int start = offset, end = offset;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        while (end < text.Length && IsWordChar(text[end])) end++;
        string? word = end - start >= 2 && !char.IsDigit(text[start]) ? text[start..end] : null;

        bool changed = word != Word;
        Word = word;
        Current = word is null ? -1 : start;
        if (!changed) return false;
        Offsets.Clear();
        if (word is not null)
            Offsets.AddRange(FindAll(text, word));
        return true;
    }

    /// <summary>Whole-word occurrences of <paramref name="word"/> in <paramref name="text"/>.</summary>
    /// <returns>Start offsets in ascending order (lazy; ordinal, case-sensitive search).</returns>
    public static IEnumerable<int> FindAll(string text, string word)
    {
        // Ordinal IndexOf finds candidates; a candidate counts only if the characters on both
        // sides are not identifier characters (so "kcut" does not match inside "kcutoff").
        for (int i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + word.Length, StringComparison.Ordinal))
        {
            bool leftOk = i == 0 || !IsWordChar(text[i - 1]);
            int after = i + word.Length;
            bool rightOk = after >= text.Length || !IsWordChar(text[after]);
            if (leftOk && rightOk)
                yield return i;
        }
    }

    /// <inheritdoc/>
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Word is null || Offsets.Count < 2 || textView.Document is null)
            return;
        int length = Word.Length, docLength = textView.Document.TextLength;
        foreach (int start in Offsets)
        {
            // Offsets are ascending, so the first one past the end means the rest are stale too
            // (the document shrank and Invalidate has not run yet).
            if (start + length > docLength) break;
            var builder = new BackgroundGeometryBuilder { CornerRadius = 3, AlignToWholePixels = true };
            builder.AddSegment(textView, new TextSegment { StartOffset = start, Length = length });
            if (builder.CreateGeometry() is { } geometry)
                drawingContext.DrawGeometry(Fill, start == Current ? CurrentPen : null, geometry);
        }
    }
}
