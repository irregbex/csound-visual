using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using CsoundVisual.Controls;
using CsoundVisual.Localization;
using CsoundVisual.Theming;

namespace CsoundVisual.Editor;

/// <summary>
/// What the <see cref="ScrubController"/> needs from the rest of the application. Implemented by
/// the main view model, always acting on the document currently shown in the editor.
/// </summary>
/// <remarks>
/// The interface keeps the controller free of view-model and engine details: it never talks to
/// Csound directly. All members are called on the UI thread; implementations that touch the engine
/// do so through the engine's request queues (e.g. <c>CsoundEngine.SetControl</c>, throttled
/// <c>CompileOrc</c>), which the performance thread drains between control cycles.
/// </remarks>
public interface IScrubHost
{
    /// <summary>Describes the token at an absolute offset of the shown document, or <c>null</c>.</summary>
    /// <remarks>Typically <see cref="CsoundAnalyzer.Inspect"/> on the document's cached analysis.
    /// Called on every pointer move, so it must be fast.</remarks>
    TokenInsight? Inspect(int offset);

    /// <summary>Drives a control channel (moves its slider and, if playing, the engine) in real time.</summary>
    /// <param name="channel">Channel name as used by <c>chnget</c>/<c>chnk</c>.</param>
    /// <param name="value">New value in the channel's own units (already clamped to its range).</param>
    void SetChannelValue(string channel, double value);

    /// <summary>
    /// Called every time a dragged literal was rewritten in the document (and once more with the
    /// original text when the drag is cancelled), so the host can apply it live.
    /// </summary>
    /// <remarks>
    /// <paramref name="token"/> already carries the new <see cref="TokenInsight.Text"/>,
    /// <see cref="TokenInsight.Length"/> and <see cref="TokenInsight.Value"/>. The host decides what
    /// "live" means from the token's context: a literal inside an <c>instr</c> triggers a throttled
    /// recompile of that block, a <c>chnk</c> default also sets the channel, header/score numbers
    /// only change the text.
    /// </remarks>
    void LiteralScrubbed(TokenInsight token);

    /// <summary>Current live value of a variable token while the document plays, or <c>null</c>.</summary>
    /// <remarks>Reads the latest immutable snapshot published by <see cref="Engine.LiveValueMonitor"/>;
    /// never blocks. Polled ~15 times per second while the card is open.</remarks>
    Engine.LiveValue? GetLiveValue(TokenInsight token);

    /// <summary>Pins a variable in the watch panel.</summary>
    void Watch(TokenInsight token);

    /// <summary>Moves the caret to a line of the shown document.</summary>
    void GoToDefinition(int line);

    /// <summary>Opens the Csound manual page of an opcode.</summary>
    void OpenOpcodeHelp(string opcode);
}

/// <summary>
/// Adds two behaviours to an AvaloniaEdit <see cref="TextEditor"/>:
/// <list type="bullet">
/// <item><b>Hover help</b>: resting the mouse on a variable, number, p-field, channel name or
/// opcode shows a card explaining it, with its known range and current value.</item>
/// <item><b>Scrubbing</b>: pressing on a draggable token and moving the mouse changes its value —
/// right/up increases, left/down decreases — clamped to its range and applied in real time.
/// <c>Shift</c> moves 10× finer, <c>Ctrl</c> 10× coarser, and <c>Esc</c> restores the value it
/// had before the drag started.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Input is intercepted with <i>tunnelling</i> handlers on the <see cref="AvaloniaEdit.Editing.TextArea"/>
/// so they run before AvaloniaEdit's own selection handling. A press on a draggable token is
/// swallowed; if the mouse is released without moving more than <see cref="DragThreshold"/>
/// pixels it is treated as a normal click (the caret is placed there), otherwise it becomes a drag.
/// Presses on anything else, and presses with modifier keys, are left to the editor untouched.
/// </para>
/// <para>
/// Literal drags are wrapped in a single undo group, so one <c>Ctrl+Z</c> reverts a whole drag
/// and cancelling with <c>Esc</c> simply undoes that group.
/// </para>
/// <para>
/// <b>Value mapping.</b> The drag distance is <c>dx − dy</c> in DIPs from the press point (so a
/// diagonal up-right drag counts double). With a known range (<see cref="TokenInsight.Minimum"/> …
/// <see cref="TokenInsight.Maximum"/>) <see cref="PixelsForFullRange"/> DIPs sweep the whole range,
/// linearly or, for <see cref="TokenInsight.Exponential"/> ranges such as frequencies, in equal
/// ratios. Without a range the value moves in steps of about 1 % of its decade per 2 DIPs. The
/// value is always computed from the <i>start</i> value plus the total displacement (not
/// incrementally), so rounding never accumulates and returning to the press point restores the
/// start value.
/// </para>
/// <para>
/// <b>Lifetime and threading.</b> One controller per main window, created once for the shared
/// editor; it follows whichever document is shown because every query goes through
/// <see cref="IScrubHost"/>. All work happens on the UI thread (pointer events and two
/// <see cref="DispatcherTimer"/>s). The hover card is a plain <see cref="Border"/> placed on an
/// overlay <see cref="Canvas"/> above the editor (not a popup), and is not hit-testable, so it
/// never steals the pointer from the text.
/// </para>
/// </remarks>
public sealed class ScrubController
{
    /// <summary>Pixels the mouse must travel before a press turns into a drag.</summary>
    const double DragThreshold = 3;

    /// <summary>Pixels of movement that sweep a whole known range (before Shift/Ctrl scaling).</summary>
    const double PixelsForFullRange = 400;

    /// <summary>Delay before the hover card appears.</summary>
    static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(350);

    /// <summary>Four-way arrow shown over draggable tokens and during a drag.</summary>
    static readonly Cursor ScrubCursor = new(StandardCursorType.SizeAll);

    /// <summary>The code editor being extended.</summary>
    readonly TextEditor editor;
    /// <summary>Canvas over the editor that hosts the hover card; also the coordinate space of <see cref="pressPoint"/> and <see cref="hoverPoint"/>.</summary>
    readonly Canvas overlay;
    /// <summary>Application services (analysis, channels, live values, navigation).</summary>
    readonly IScrubHost host;
    /// <summary>Background renderer that marks the hovered or dragged token.</summary>
    readonly TokenHighlightRenderer highlight = new();
    /// <summary>One-shot timer: shows the card after the pointer rested <see cref="HoverDelay"/> on a token.</summary>
    readonly DispatcherTimer hoverTimer;
    /// <summary>Refreshes the card's live section (~15 Hz) while it is visible; stops itself when the card hides.</summary>
    readonly DispatcherTimer liveTimer;

    // Hover card parts (built in code so the controller is self-contained).
    readonly Border card;
    readonly TextBlock titleText, valueText, subtitleText, rangeText, hintText;
    readonly RangeIndicator rangeBar;
    readonly StackPanel detailsPanel;
    readonly TextBlock liveText;
    readonly Sparkline liveSpark;

    /// <summary>Token under the pointer (pending or shown card), or <c>null</c>.</summary>
    TokenInsight? hovered;
    /// <summary>Pointer position (overlay coordinates) when <see cref="hovered"/> was found; the card is placed next to it.</summary>
    Point hoverPoint;

    // Drag state. Lifecycle: OnPressed sets `pressed` (no drag yet) → OnMoved past DragThreshold
    // calls BeginScrub (`scrubbing` = true) → UpdateScrub per move → Commit (release / capture
    // lost) or Cancel (Esc). `pressed` is cleared at the end of every path.

    /// <summary>Token pressed with the left button, from press until release/cancel; <c>null</c> otherwise.</summary>
    /// <remarks>The same instance is mutated during a literal drag (Text, Length, Value) to track the rewritten text.</remarks>
    TokenInsight? pressed;
    /// <summary>Pointer captured on press, so <see cref="Cancel"/> can release it outside a pointer event.</summary>
    IPointer? capturedPointer;
    /// <summary>Press position in overlay coordinates; displacement is measured from here.</summary>
    Point pressPoint;
    /// <summary>True once the press turned into a drag.</summary>
    bool scrubbing;
    /// <summary>True when a literal drag modified the document at least once (Esc must then undo).</summary>
    bool textChanged;
    /// <summary>Value at drag start (restored by Esc) and the last value applied (avoids redundant updates).</summary>
    double startValue, currentValue;
    /// <summary>Decimal places used to quantize and format the dragged value (see <see cref="DecimalsFor"/>).</summary>
    int decimals;
    /// <summary>Literal text before the drag, restored on cancel.</summary>
    string originalText = "";

    /// <summary>Attaches the behaviours to <paramref name="editor"/>.</summary>
    /// <param name="editor">The code editor.</param>
    /// <param name="overlay">A <see cref="Canvas"/> laid over the editor where the card is placed.</param>
    /// <param name="host">Application services (analysis, channels, live recompile).</param>
    public ScrubController(TextEditor editor, Canvas overlay, IScrubHost host)
    {
        this.editor = editor;
        this.overlay = overlay;
        this.host = host;

        (card, titleText, valueText, subtitleText, rangeText, rangeBar, detailsPanel, hintText, liveText, liveSpark) = BuildCard();
        overlay.Children.Add(card);

        // While the card is open, its live value is refreshed ~15 times per second.
        liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
        liveTimer.Tick += (_, _) =>
        {
            if (card.IsVisible && hovered is not null)
                UpdateLive(hovered);
            else
                liveTimer.Stop();
        };

        hoverTimer = new DispatcherTimer { Interval = HoverDelay };
        hoverTimer.Tick += (_, _) =>
        {
            hoverTimer.Stop();
            if (hovered is not null && !scrubbing)
                ShowCard(hovered, hoverPoint);
        };

        var area = editor.TextArea;
        area.TextView.BackgroundRenderers.Add(highlight);
        // Tunnel handlers run before the TextArea's own (bubbling) selection logic, so a press on a
        // draggable token can be swallowed before AvaloniaEdit starts a selection.
        area.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        area.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        area.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        area.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        area.PointerExited += (_, _) => { if (pressed is null) HideHover(); };
        // Capture lost mid-drag (window deactivated, another control grabbed the pointer): keep the
        // value reached so far rather than leaving an open undo group.
        area.PointerCaptureLost += (_, _) => { if (scrubbing) Commit(); pressed = null; };
        area.TextView.ScrollOffsetChanged += (_, _) => { if (!scrubbing) HideHover(); };
        editor.ContextRequested += OnContextRequested;
        editor.TextChanged += (_, _) => { if (!scrubbing) HideHover(); };
    }

    /// <summary>True while a value is being dragged.</summary>
    public bool IsScrubbing => scrubbing;

    // ------------------------------------------------------------------------------------------
    //  Pointer handling
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Plain left press (no modifiers, not a double-click) on a draggable token: remember it,
    /// capture the pointer and swallow the event. Anything else is left to the editor.
    /// </summary>
    void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var area = editor.TextArea;
        if (!e.GetCurrentPoint(area).Properties.IsLeftButtonPressed || e.ClickCount != 1 || e.KeyModifiers != KeyModifiers.None)
            return;
        var insight = HitTest(e);
        if (insight is null || insight.Scrub == ScrubMode.None)
            return;

        pressed = insight;
        pressPoint = e.GetPosition(overlay);
        capturedPointer = e.Pointer;
        e.Pointer.Capture(area);
        area.Focus();
        e.Handled = true;
    }

    /// <summary>
    /// While pressed: start or continue the drag. Otherwise: hover tracking (cursor shape, and a
    /// restart of the hover delay whenever the pointer reaches a different token).
    /// </summary>
    void OnMoved(object? sender, PointerEventArgs e)
    {
        if (pressed is not null)
        {
            var p = e.GetPosition(overlay);
            if (!scrubbing)
            {
                // Manhattan distance is enough for a click-vs-drag threshold.
                if (Math.Abs(p.X - pressPoint.X) + Math.Abs(p.Y - pressPoint.Y) < DragThreshold)
                    return;
                BeginScrub();
            }
            UpdateScrub(p, e.KeyModifiers);
            e.Handled = true;
            return;
        }

        var insight = HitTest(e);
        editor.TextArea.TextView.Cursor = insight is { Scrub: not ScrubMode.None } ? ScrubCursor : null;
        if (insight is null)
        {
            HideHover();
            return;
        }
        // Still on the same token: keep the pending timer / visible card as they are.
        if (insight.SameToken(hovered))
            return;

        HideHover();
        hovered = insight;
        hoverPoint = e.GetPosition(overlay);
        hoverTimer.Start();
    }

    /// <summary>Ends a press: commits a drag, or turns a press that never moved into a caret placement.</summary>
    void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (pressed is null)
            return;
        e.Handled = true;
        if (scrubbing)
            Commit();
        else if (OffsetAt(e) is { } offset)
        {
            // A click without drag: behave like a normal click and place the caret.
            editor.TextArea.ClearSelection();
            editor.TextArea.Caret.Offset = offset;
        }
        pressed = null;
        capturedPointer = null;
        // Releasing capture raises PointerCaptureLost; by now scrubbing/pressed are already reset,
        // so that handler does nothing.
        e.Pointer.Capture(null);
    }

    /// <summary>
    /// F1 opens the manual for the opcode under the caret; Esc during a drag cancels it; any other
    /// key while not dragging hides the hover card (typing should not be covered by it).
    /// </summary>
    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1 && !scrubbing)
        {
            // F1: manual page of the opcode under the caret.
            if (host.Inspect(editor.TextArea.Caret.Offset) is { Kind: InsightKind.Opcode } op)
                host.OpenOpcodeHelp(op.Text);
            e.Handled = true;
            return;
        }
        if (scrubbing && e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
        }
        else if (!scrubbing)
            HideHover();
    }

    // ------------------------------------------------------------------------------------------
    //  Scrubbing
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Turns the pending press into a drag: snapshots the start value/text, chooses the precision,
    /// opens an undo group for literal drags and shows the card in its "active" form.
    /// </summary>
    void BeginScrub()
    {
        var token = pressed!;
        scrubbing = true;
        textChanged = false;
        startValue = currentValue = token.Value ?? 0;
        originalText = token.Text;
        decimals = DecimalsFor(token);
        if (token.Scrub == ScrubMode.Literal)
            editor.Document.UndoStack.StartUndoGroup();

        hoverTimer.Stop();
        hovered = token;
        highlight.Show(token.Start, token.Length, isActive: true);
        ShowCard(token, pressPoint);
        editor.TextArea.TextView.Cursor = ScrubCursor;
    }

    /// <summary>
    /// Computes the value for the current pointer position and applies it if it changed after
    /// quantization (so sub-step jitter does not rewrite the document or flood the channel).
    /// </summary>
    /// <param name="p">Pointer position in overlay coordinates.</param>
    /// <param name="modifiers">Shift = ×0.1 (fine), Ctrl/Meta = ×10 (coarse), read on every move so they can be pressed mid-drag.</param>
    void UpdateScrub(Point p, KeyModifiers modifiers)
    {
        var token = pressed!;
        // Right and up increase, left and down decrease (screen Y grows downwards, hence the minus).
        double delta = (p.X - pressPoint.X) - (p.Y - pressPoint.Y);
        double factor = modifiers.HasFlag(KeyModifiers.Shift) ? 0.1
            : modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta) ? 10 : 1;

        double v = Quantize(token, ValueFor(token, startValue, delta * factor));
        if (v.Equals(currentValue))
            return;
        currentValue = v;
        Apply(token, v);
    }

    /// <summary>Writes a new value: into the channel, or into the document text for literals.</summary>
    void Apply(TokenInsight token, double value)
    {
        token.Value = value;
        if (token.Scrub == ScrubMode.Channel)
            host.SetChannelValue(token.Channel!, value);
        else
        {
            // Replace the literal in place. The start offset never moves (the token is the only
            // thing edited), but the length can change ("9.9" → "10.0"), so it is tracked here.
            string text = FormatLiteral(value);
            editor.Document.Replace(token.Start, token.Length, text);
            token.Length = text.Length;
            token.Text = text;
            textChanged = true;
            host.LiteralScrubbed(token);
        }
        highlight.Show(token.Start, token.Length, isActive: true);
        editor.TextArea.TextView.InvalidateLayer(highlight.Layer);
        UpdateCard(token);
    }

    /// <summary>Ends a drag keeping the current value; closes the literal undo group so the whole drag is one Ctrl+Z.</summary>
    void Commit()
    {
        var token = pressed;
        scrubbing = false;
        if (token?.Scrub == ScrubMode.Literal)
            editor.Document.UndoStack.EndUndoGroup();
        EndScrubVisuals();
    }

    /// <summary>
    /// Esc during a drag: restores the start value. Channels are simply set back; literals close
    /// the undo group and undo it (one step, since the group collapsed every rewrite), then tell
    /// the host so a live instrument is recompiled with the original number. Also releases the
    /// pointer capture, since this runs from a key event.
    /// </summary>
    void Cancel()
    {
        var token = pressed!;
        scrubbing = false;
        if (token.Scrub == ScrubMode.Channel)
        {
            token.Value = startValue;
            host.SetChannelValue(token.Channel!, startValue);
        }
        else
        {
            editor.Document.UndoStack.EndUndoGroup();
            if (textChanged)
                editor.Document.UndoStack.Undo();
            token.Text = originalText;
            token.Length = originalText.Length;
            token.Value = startValue;
            host.LiteralScrubbed(token);
        }
        pressed = null;
        capturedPointer?.Capture(null);
        capturedPointer = null;
        EndScrubVisuals();
    }

    /// <summary>Hides card and highlight and restores the normal text cursor.</summary>
    void EndScrubVisuals()
    {
        HideHover();
        editor.TextArea.TextView.Cursor = null;
    }

    /// <summary>Maps a pointer displacement (in pixels) to a value, linear or exponential, clamped to the bounds.</summary>
    /// <param name="token">The dragged token (range, exponential flag, decimals).</param>
    /// <param name="start">Value when the drag started.</param>
    /// <param name="delta">Total scaled displacement in DIPs since the press (positive = increase).</param>
    static double ValueFor(TokenInsight token, double start, double delta)
    {
        if (token.Minimum is { } lo && token.Maximum is { } hi && hi > lo)
        {
            if (token.Exponential && lo > 0)
            {
                // Work in normalised log space: t = log(v/lo) / log(hi/lo) ∈ [0, 1], so equal
                // pixel distances give equal frequency ratios (an octave is the same width anywhere).
                double t0 = Math.Log(Math.Clamp(start, lo, hi) / lo) / Math.Log(hi / lo);
                double t = Math.Clamp(t0 + delta / PixelsForFullRange, 0, 1);
                return lo * Math.Pow(hi / lo, t);
            }
            return Math.Clamp(start + delta / PixelsForFullRange * (hi - lo), lo, hi);
        }

        // Unknown range: step relative to the literal's own magnitude and precision, 1 step per 2 px.
        // unit = smallest change the literal can show (0.01 for "0.25"); magnitude = 1 % of the
        // power of ten below the value (800 → 10^2 / 100 = 1; 0.25 → 10^-1 / 100 = 0.001); the
        // larger of the two wins, so the step never goes below the literal's last digit.
        double unit = Math.Pow(10, -token.Decimals);
        double magnitude = start != 0 ? Math.Pow(10, Math.Floor(Math.Log10(Math.Abs(start)))) / 100 : unit;
        double step = Math.Max(unit, magnitude);
        double v = start + Math.Round(delta / 2) * step;
        if (token.Minimum is { } min) v = Math.Max(min, v);
        if (token.Maximum is { } max) v = Math.Min(max, v);
        return v;
    }

    /// <summary>Rounds to an integer for integer-only tokens (table numbers, counts…) or to <see cref="decimals"/> places.</summary>
    double Quantize(TokenInsight token, double v) =>
        token.IntegerOnly ? Math.Round(v) : Math.Round(v, decimals, MidpointRounding.AwayFromZero);

    /// <summary>Digits to keep: the literal's own, or enough to make one pixel of movement visible.</summary>
    static int DecimalsFor(TokenInsight token)
    {
        if (token.IntegerOnly)
            return 0;
        int own = token.Kind == InsightKind.Number ? token.Decimals : 3;
        if (token.Minimum is { } lo && token.Maximum is { } hi && hi > lo)
        {
            // One DIP moves (hi − lo) / PixelsForFullRange; keep enough digits to show that step
            // (range 0…1 → 0.0025 per DIP → 3 decimals), capped at 4.
            int needed = (int)Math.Ceiling(-Math.Log10((hi - lo) / PixelsForFullRange));
            return Math.Clamp(Math.Max(own, needed), 0, 4);
        }
        return own;
    }

    /// <summary>Text written into the code: fixed <see cref="decimals"/> places, invariant culture (Csound needs a '.' decimal point).</summary>
    string FormatLiteral(double v) => v.ToString("F" + decimals, CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------------------------------
    //  Context menu
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Right-click menu of the editor: token-specific actions (watch a variable, go to its
    /// definition, open an opcode's manual page) followed by the usual editing commands.
    /// </summary>
    /// <remarks>
    /// The menu is rebuilt on every request (it depends on the token and on undo state). Breakpoint
    /// items appear only when the host is the real <see cref="ViewModels.MainViewModel"/> (a direct
    /// dependency outside <see cref="IScrubHost"/>, kept for brevity). The menu also opens from the
    /// keyboard (Shift+F10 / menu key); then there is no pointer position and the caret line is used.
    /// </remarks>
    void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        HideHover();
        var tv = editor.TextArea.TextView;
        TokenInsight? token = null;
        if (e.TryGetPosition(tv, out var point) && tv.GetPosition(point + tv.ScrollOffset) is { } pos)
            token = host.Inspect(editor.Document.GetOffset(pos.Location));

        var items = new List<Control>();
        // Local factory: a menu item with a tooltip and a click action.
        MenuItem Item(string header, string tip, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            ToolTip.SetTip(mi, tip);
            mi.Click += (_, _) => action();
            return mi;
        }
        if (token is { Kind: InsightKind.Variable } v)
        {
            items.Add(Item(Loc.S($"Observar «{v.Text}»", $"Watch «{v.Text}»"),
                Loc.S("Fija la variable en el panel Observar para ver su valor en vivo mientras suena.",
                    "Pins the variable in the Watch panel to see its live value while playing."), () => host.Watch(v)));
            if (v.DefinitionLine is { } line)
                items.Add(Item(Loc.S($"Ir a la definición (línea {line})", $"Go to definition (line {line})"),
                    Loc.S("Mueve el cursor a la línea donde se define la variable.", "Moves the caret to the line where the variable is defined."),
                    () => host.GoToDefinition(line)));
        }
        if (token is { Kind: InsightKind.Opcode } op)
            items.Add(Item(Loc.S($"Ayuda de «{op.Text}» en el manual  (F1)", $"«{op.Text}» in the manual  (F1)"),
                Loc.S("Abre la página del opcode en el manual de Csound (navegador).", "Opens the opcode's page in the Csound manual (browser)."),
                () => host.OpenOpcodeHelp(op.Text)));
        if (host is ViewModels.MainViewModel debugHost && editor.Document is { } document)
        {
            // Breakpoint on the clicked line (or the caret line when opened from the keyboard).
            int bpLine = e.TryGetPosition(tv, out var p2) && tv.GetPosition(p2 + tv.ScrollOffset) is { } bpPos ? bpPos.Line : editor.TextArea.Caret.Line;
            bool has = debugHost.SelectedDocument?.Breakpoints.Contains(bpLine) == true;
            items.Add(Item(has ? Loc.S($"Quitar breakpoint (línea {bpLine})   (F9)", $"Remove breakpoint (line {bpLine})   (F9)")
                               : Loc.S($"Poner breakpoint (línea {bpLine})   (F9)", $"Set breakpoint (line {bpLine})   (F9)"),
                Loc.S("Al tocar, Csound se detiene antes de esta línea y el panel Depurar muestra las variables de la nota.",
                      "While playing, Csound stops before this line and the Debug panel shows the note's variables."),
                () => debugHost.ToggleBreakpoint(bpLine), bpLine <= document.LineCount));
            if (debugHost.BreakpointCount > 0)
                items.Add(Item(Loc.S("Quitar todos los breakpoints   (Shift+F9)", "Remove all breakpoints   (Shift+F9)"),
                    Loc.S("Quita todos los breakpoints de este archivo.", "Removes every breakpoint of this file."),
                    () => debugHost.ClearAllBreakpointsCommand.Execute(null)));
        }
        if (items.Count > 0)
            items.Add(new Separator());
        items.Add(Item(Loc.S("Deshacer   (Ctrl+Z)", "Undo   (Ctrl+Z)"), Loc.S("Deshace el último cambio.", "Undoes the last change."), () => editor.Undo(), editor.CanUndo));
        items.Add(Item(Loc.S("Rehacer   (Ctrl+Y)", "Redo   (Ctrl+Y)"), Loc.S("Rehace el último cambio deshecho.", "Redoes the last undone change."), () => editor.Redo(), editor.CanRedo));
        items.Add(new Separator());
        items.Add(Item(Loc.S("Cortar   (Ctrl+X)", "Cut   (Ctrl+X)"), Loc.S("Corta la selección al portapapeles.", "Cuts the selection to the clipboard."), () => editor.Cut()));
        items.Add(Item(Loc.S("Copiar   (Ctrl+C)", "Copy   (Ctrl+C)"), Loc.S("Copia la selección al portapapeles.", "Copies the selection to the clipboard."), () => editor.Copy()));
        items.Add(Item(Loc.S("Pegar   (Ctrl+V)", "Paste   (Ctrl+V)"), Loc.S("Pega desde el portapapeles.", "Pastes from the clipboard."), () => editor.Paste()));
        items.Add(Item(Loc.S("Seleccionar todo   (Ctrl+A)", "Select all   (Ctrl+A)"), Loc.S("Selecciona todo el texto.", "Selects all the text."), () => editor.SelectAll()));

        new ContextMenu { ItemsSource = items }.Open(editor);
        e.Handled = true;
    }

    /// <summary>Shows or hides the live section of the card for <paramref name="token"/>.</summary>
    /// <remarks>
    /// The section is visible only while the host has a live value (document playing, live values
    /// on, variable seen by the debugger). For audio variables the value is the block peak. The
    /// sparkline shows the monitor's history ring (≈3 s).
    /// </remarks>
    void UpdateLive(TokenInsight token)
    {
        var live = host.GetLiveValue(token);
        liveText.IsVisible = liveSpark.IsVisible = live is not null;
        if (live is null)
            return;
        // Precision by magnitude: 1234 / 12.3 / 0.123.
        string Fmt(double x) => x.ToString(Math.Abs(x) >= 1000 ? "0" : Math.Abs(x) >= 10 ? "0.0" : "0.000", CultureInfo.InvariantCulture);
        liveText.Text = Loc.S($"● en vivo: {Fmt(live.Value)}{(live.IsAudio ? " (pico)" : "")}   ·   mín {Fmt(live.Minimum)}  máx {Fmt(live.Maximum)}",
                              $"● live: {Fmt(live.Value)}{(live.IsAudio ? " (peak)" : "")}   ·   min {Fmt(live.Minimum)}  max {Fmt(live.Maximum)}") +
                        (live.Instances > 1 ? Loc.S($"   ·   {live.Instances} notas", $"   ·   {live.Instances} notes") : "");
        liveSpark.Values = live.History;
    }

    // ------------------------------------------------------------------------------------------
    //  Hit testing
    // ------------------------------------------------------------------------------------------

    /// <summary>Document offset under the pointer, or <c>null</c> outside the text.</summary>
    int? OffsetAt(PointerEventArgs e)
    {
        // TextView.GetPosition expects document-space visual coordinates, hence + ScrollOffset.
        var tv = editor.TextArea.TextView;
        var pos = tv.GetPosition(e.GetPosition(tv) + tv.ScrollOffset);
        return pos is { } p ? editor.Document.GetOffset(p.Location) : null;
    }

    /// <summary>
    /// The token under the pointer. The analyzer works on the caret position nearest to the
    /// pointer, so the result is re-checked against the token's on-screen bounds; otherwise
    /// hovering the empty space after a line would still report its last word.
    /// </summary>
    TokenInsight? HitTest(PointerEventArgs e)
    {
        if (editor.Document is null || OffsetAt(e) is not { } offset)
            return null;
        var insight = host.Inspect(offset);
        if (insight is null)
            return null;

        // Token box in view coordinates: left edge and line top/bottom from the start position,
        // right edge from the end position (assumes the token does not wrap). 1 DIP of slack
        // horizontally so the very edge of a character still counts.
        var tv = editor.TextArea.TextView;
        var p = e.GetPosition(tv);
        var start = new AvaloniaEdit.TextViewPosition(editor.Document.GetLocation(insight.Start));
        var end = new AvaloniaEdit.TextViewPosition(editor.Document.GetLocation(insight.Start + insight.Length));
        var top = tv.GetVisualPosition(start, VisualYPosition.LineTop) - tv.ScrollOffset;
        var bottom = tv.GetVisualPosition(start, VisualYPosition.LineBottom) - tv.ScrollOffset;
        var right = tv.GetVisualPosition(end, VisualYPosition.LineTop) - tv.ScrollOffset;
        bool inside = p.X >= top.X - 1 && p.X <= right.X + 1 && p.Y >= top.Y && p.Y <= bottom.Y;
        return inside ? insight : null;
    }

    // ------------------------------------------------------------------------------------------
    //  Hover card
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Fills and shows the card for <paramref name="token"/> near <paramref name="anchor"/>
    /// (overlay coordinates): 14 DIP right and 22 DIP below the pointer, flipped above the pointer
    /// or pushed left when it would leave the overlay.
    /// </summary>
    void ShowCard(TokenInsight token, Point anchor)
    {
        highlight.Show(token.Start, token.Length, isActive: scrubbing);
        editor.TextArea.TextView.InvalidateLayer(highlight.Layer);
        UpdateCard(token);
        UpdateLive(token);
        if (!liveTimer.IsEnabled) liveTimer.Start();
        card.IsVisible = true;
        // Measure now (not on the next layout pass) to know the size for edge avoidance.
        card.Measure(Size.Infinity);
        var size = card.DesiredSize;
        double x = anchor.X + 14, y = anchor.Y + 22;
        if (x + size.Width > overlay.Bounds.Width - 8)
            x = Math.Max(8, overlay.Bounds.Width - size.Width - 8);
        if (y + size.Height > overlay.Bounds.Height - 8)
            y = Math.Max(8, anchor.Y - size.Height - 12);
        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, y);
    }

    /// <summary>
    /// Copies the token's description into the card: title (truncated to 40 characters),
    /// subtitle, current value, range text and bar, detail lines and the drag hint (which switches
    /// to "Esc = back to …" during a drag). Called on show and after every applied drag step.
    /// </summary>
    void UpdateCard(TokenInsight token)
    {
        // Numbers show their live text (it changes while dragging); other tokens their fixed title.
        string title = token.Kind == InsightKind.Number ? token.Text : token.Title;
        titleText.Text = title.Length > 40 ? title[..40] + "…" : title;
        subtitleText.Text = token.Subtitle ?? "";
        subtitleText.IsVisible = token.Subtitle is not null;

        valueText.Text = token.Value is { } v ? "= " + FormatDisplay(token, v) : "";
        valueText.IsVisible = token.Value is not null && token.Kind != InsightKind.Number;

        rangeText.Text = token.RangeText ?? "";
        rangeText.IsVisible = token.RangeText is not null;
        bool showBar = token.Minimum is { } lo && token.Maximum is { } hi && hi > lo && token.Value is not null && token.RangeText is not null;
        rangeBar.IsVisible = showBar;
        if (showBar)
            rangeBar.Set(token.Minimum!.Value, token.Maximum!.Value, token.Value!.Value, token.Exponential);

        detailsPanel.Children.Clear();
        foreach (var line in token.Details)
            detailsPanel.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = AppTheme.Brush("Text") });

        hintText.IsVisible = token.Scrub != ScrubMode.None;
        hintText.Text = scrubbing
            ? Loc.S($"Esc = volver a {FormatDisplay(token, startValue)}   ·   Shift fino   ·   Ctrl rápido",
                    $"Esc = back to {FormatDisplay(token, startValue)}   ·   Shift fine   ·   Ctrl coarse")
            : token.Scrub == ScrubMode.Channel
                ? Loc.S("Arrastra: → ↑ sube · ← ↓ baja · cambia el canal en tiempo real · Esc cancela",
                        "Drag: → ↑ up · ← ↓ down · changes the channel in real time · Esc cancels")
                : Loc.S("Arrastra: → ↑ sube · ← ↓ baja · reescribe el número · Esc cancela",
                        "Drag: → ↑ up · ← ↓ down · rewrites the number · Esc cancels");
    }

    /// <summary>Value text for the card: literal formatting while rewriting code, magnitude-based precision otherwise.</summary>
    string FormatDisplay(TokenInsight token, double v) =>
        token.IntegerOnly ? v.ToString("0", CultureInfo.InvariantCulture)
        : scrubbing && token.Scrub == ScrubMode.Literal ? FormatLiteral(v)
        : v.ToString(Math.Abs(v) >= 100 ? "0" : Math.Abs(v) >= 10 ? "0.0" : "0.###", CultureInfo.InvariantCulture);

    /// <summary>Cancels a pending hover, hides the card and removes the token highlight. (The live timer stops itself on its next tick.)</summary>
    void HideHover()
    {
        hoverTimer.Stop();
        hovered = null;
        card.IsVisible = false;
        highlight.Clear();
        editor.TextArea.TextView.InvalidateLayer(highlight.Layer);
    }


    /// <summary>
    /// Builds the hover card's visual tree in code (so the controller needs no XAML) and returns
    /// the root plus the parts that are updated later. Layout, top to bottom: title + value,
    /// subtitle, range text, range bar, live value, sparkline, detail lines, drag hint. Colours
    /// are theme brushes, so the card follows theme changes without being rebuilt.
    /// </summary>
    static (Border, TextBlock, TextBlock, TextBlock, TextBlock, RangeIndicator, StackPanel, TextBlock, TextBlock, Sparkline) BuildCard()
    {
        var code = new FontFamily("Cascadia Code, Cascadia Mono, Consolas, monospace");
        var title = new TextBlock { FontFamily = code, FontWeight = FontWeight.Bold, FontSize = 15, Foreground = AppTheme.Brush("AccentLight") };
        var value = new TextBlock { FontFamily = code, FontSize = 14, Foreground = AppTheme.Brush("Good"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Children = { title, value } };
        var subtitle = new TextBlock { FontSize = 11.5, Foreground = AppTheme.Brush("Accent") };
        var range = new TextBlock { FontSize = 11.5, Foreground = AppTheme.Brush("TextDim"), Margin = new Thickness(0, 4, 0, 0) };
        var bar = new RangeIndicator { Height = 10, Margin = new Thickness(0, 0, 0, 2) };
        var details = new StackPanel { Spacing = 3, Margin = new Thickness(0, 4, 0, 0) };
        var hint = new TextBlock { FontSize = 11, Foreground = AppTheme.Brush("TextDim"), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        var live = new TextBlock { FontSize = 12, FontFamily = code, Foreground = AppTheme.Brush("Good"), Margin = new Thickness(0, 6, 0, 2), IsVisible = false, TextWrapping = TextWrapping.Wrap };
        var spark = new Sparkline { Height = 28, IsVisible = false };

        var border = new Border
        {
            Background = AppTheme.Brush("Surface_F2"),
            BorderBrush = AppTheme.Brush("TextFaint"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10),
            MaxWidth = 460,
            MinWidth = 220,
            IsVisible = false,
            // Never intercept the pointer: the card can sit over the text being hovered/dragged.
            IsHitTestVisible = false,
            BoxShadow = BoxShadows.Parse("0 8 28 0 #A0000000"),
            Child = new StackPanel { Spacing = 2, Children = { header, subtitle, range, bar, live, spark, details, hint } },
        };
        return (border, title, value, subtitle, range, bar, details, hint, live, spark);
    }
}
