using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsoundVisual.Editor;
using CsoundVisual.Engine;
using CsoundVisual.Localization;

namespace CsoundVisual.ViewModels;

/// <summary>
/// Breakpoints (roadmap item 25): F9 or a click in the gutter sets a breakpoint on a line of an
/// instrument; while playing with the app's own audio engine, Csound stops there, the device
/// plays silence, and the debug panel shows the instance and its variables. F5 continues,
/// F10 steps to the next control cycle, Shift+F9 removes every breakpoint.
/// </summary>
/// <remarks>
/// <para>
/// Engine side: <see cref="BreakpointSession"/> (one per run) on top of the Csound 7 debugger,
/// which is shared with the live values. Line numbers: <see cref="BreakpointLineMap"/>.
/// </para>
/// <para>
/// <b>Who stores what.</b> The breakpoints themselves belong to the document
/// (<see cref="DocumentViewModel.Breakpoints"/>, editor line numbers; they survive runs). Csound
/// reports stops in its <i>own</i> line numbers, which differ from the editor's (the orchestra is
/// compiled from the text inside <c>&lt;CsInstruments&gt;</c>, and live recompiles renumber
/// instruments). <see cref="BreakpointLineMap"/> translates both ways and is kept up to date by
/// <see cref="CompileBlockLive"/> / <see cref="CompileCodeLive"/>.
/// </para>
/// <para>
/// <b>Threading.</b> When Csound reaches a breakpoint, the debugger callback runs on the
/// performance thread and blocks it (the app's WASAPI engine keeps the device fed with silence, so
/// nothing glitches). The view model never receives a callback: <see cref="DebugTick"/> polls
/// <c>engine.Breakpoints</c> every UI frame (from <c>Tick</c>) and compares the hit's serial number
/// to detect a new stop. Commands (<see cref="DebugContinue"/>, <see cref="DebugStep"/>) only post a
/// request to the session, which releases the performance thread.
/// </para>
/// <para>
/// <b>Generated members.</b> The <c>[ObservableProperty]</c> fields below become public properties
/// (<c>IsDebugPaused</c>, <c>DebugHit</c>…); <c>[RelayCommand]</c> methods become
/// <c>…Command</c> properties bound by the debug panel (<see cref="Views.DebugPanel"/>) and the key
/// bindings in <c>MainWindow.Breakpoints.cs</c>. <c>[NotifyCanExecuteChangedFor]</c> re-evaluates
/// the Continue/Step buttons whenever <c>IsDebugPaused</c> changes.
/// </para>
/// </remarks>
public sealed partial class MainViewModel
{
    /// <summary>Editor ⇄ Csound line translation for the current run; <c>null</c> when not playing (or rendering).</summary>
    BreakpointLineMap? debugMap;

    /// <summary>The document that was launched with the debugger in mind (exposed as <see cref="DebugDocument"/>).</summary>
    DocumentViewModel? debugDocument;

    /// <summary>
    /// Session created by <see cref="PrepareBreakpoints"/> and handed to the engine; <c>null</c> when the
    /// run does not use the debugger. The VM keeps its own reference because the engine only
    /// attaches it (<c>engine.Breakpoints</c>) once its thread has started.
    /// </summary>
    BreakpointSession? debugSession;

    /// <summary>Serial of the last <see cref="BreakpointHit"/> shown, so each stop is published once.</summary>
    int lastHitSerial;

    /// <summary>One-shot 5 s timer that clears a transient notice from the status bar (see <see cref="ShowDebugNotice"/>).</summary>
    DispatcherTimer? noticeTimer;

    /// <summary>True while the performance is stopped at a breakpoint.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DebugContinueCommand), nameof(DebugStepCommand))]
    bool isDebugPaused;

    /// <summary>The stop shown in the debug panel (kept after continuing, until the next stop or Play).</summary>
    [ObservableProperty] BreakpointHit? debugHit;

    /// <summary>Editor line of <see cref="DebugHit"/> in <see cref="DebugDocument"/>, or 0.</summary>
    [ObservableProperty] int debugLine;

    /// <summary>True while the debug panel is shown (paused, or after a stop until closed).</summary>
    [ObservableProperty] bool isDebugPanelVisible;

    /// <summary>Status bar text: «⏸ Paused at line 42 · instr Voice · F5 continue», or a short notice.</summary>
    [ObservableProperty] string debugStatusText = "";

    /// <summary>The document the debugger refers to (the one that is playing).</summary>
    public DocumentViewModel? DebugDocument => debugDocument;

    /// <summary>Line to mark as paused in <paramref name="doc"/>, or <c>null</c>.</summary>
    /// <param name="doc">The document an editor shows (each tab asks for its own).</param>
    /// <returns>The paused editor line (1-based) when <paramref name="doc"/> is the debugged document and the run is paused; otherwise <c>null</c>.</returns>
    public int? PausedLineIn(DocumentViewModel? doc) =>
        IsDebugPaused && doc is not null && doc == debugDocument && DebugLine > 0 ? DebugLine : null;

    /// <summary>Raised when the editor must show a line (a breakpoint was hit): document, line.</summary>
    public event Action<DocumentViewModel, int>? DebugLineRequested;

    /// <summary>Raised when breakpoints or the paused line changed and the editor must repaint.</summary>
    public event Action? BreakpointsChanged;

    /// <summary>Total breakpoints in the shown document.</summary>
    /// <remarks>Computed; notified by hand after every toggle or clear.</remarks>
    public int BreakpointCount => SelectedDocument?.Breakpoints.Count ?? 0;

    /// <summary>
    /// Toggles the breakpoint of <paramref name="line"/> in the shown document. Lines where
    /// Csound cannot stop get a short explanation in the status bar instead.
    /// </summary>
    /// <param name="line">1-based editor line (from a gutter click or F9 at the caret).</param>
    /// <remarks>
    /// Removing is always allowed; adding is validated by <see cref="BreakpointRules.Check"/>
    /// (only performance-time lines inside an <c>instr</c> can stop). While playing, the change is
    /// pushed to the live session immediately (<see cref="SyncBreakpoints"/>).
    /// </remarks>
    public void ToggleBreakpoint(int line)
    {
        if (SelectedDocument is not { } doc)
            return;
        bool removing = doc.Breakpoints.Contains(line);
        if (!removing && BreakpointRules.Check(doc.Analyzer, line) is { } reason)
        {
            ShowDebugNotice("⚠ " + reason);
            return;
        }
        bool on = doc.Breakpoints.Toggle(line);
        string tail = "";
        // Warn when a new breakpoint cannot take effect in the current run: the run was started
        // without a session (no breakpoints, and live values off or a Csound-driven backend), or
        // the engine has been running for a while without attaching one. (&& binds tighter than ||.)
        if (on && doc.IsPlaying && Mode == RunMode.Play && (debugSession is null || engine.Breakpoints is null && engine.ScoreTime > 0.5))
            tail = Loc.S(" — se activa la próxima vez que toques (motor Windows · WASAPI y valores en vivo, o un breakpoint antes de Tocar)",
                         " — takes effect next time you play (Windows · WASAPI engine with live values, or a breakpoint before Play)");
        ShowDebugNotice(on
            ? Loc.S($"● Breakpoint en la línea {line}", $"● Breakpoint on line {line}") + tail
            : Loc.S($"○ Breakpoint quitado de la línea {line}", $"○ Breakpoint removed from line {line}"));
        SyncBreakpoints(doc);
        OnPropertyChanged(nameof(BreakpointCount));
        BreakpointsChanged?.Invoke();
    }

    /// <summary>Removes every breakpoint of the shown document (Shift+F9) and resumes if paused.</summary>
    [RelayCommand]
    void ClearAllBreakpoints()
    {
        // While paused, clear the document that is actually stopped (it may not be the shown tab).
        var doc = IsDebugPaused ? debugDocument ?? SelectedDocument : SelectedDocument;
        if (doc is null) return;
        int n = doc.Breakpoints.Count;
        doc.Breakpoints.Clear();
        SyncBreakpoints(doc);
        if (IsDebugPaused)
            debugSession?.Continue();
        ShowDebugNotice(Loc.S($"Breakpoints quitados: {n}", $"Breakpoints removed: {n}"));
        OnPropertyChanged(nameof(BreakpointCount));
        BreakpointsChanged?.Invoke();
    }

    /// <summary>Continues until a breakpoint is reached where that note has not stopped yet (F5 while paused).</summary>
    /// <remarks>Generated: <c>DebugContinueCommand</c>, enabled only while <see cref="IsDebugPaused"/>.</remarks>
    [RelayCommand(CanExecute = nameof(IsDebugPaused))]
    void DebugContinue() => debugSession?.Continue();

    /// <summary>Continues to the next breakpoint hit, at the latest the same line one control cycle later (F10 while paused).</summary>
    /// <remarks>
    /// Generated: <c>DebugStepCommand</c>. A true "next line" step is not available because
    /// <c>csoundDebugNext</c> does not advance in Csound 7.0, hence the "next control cycle" semantics.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(IsDebugPaused))]
    void DebugStep() => debugSession?.StepCycle();

    /// <summary>Stops the performance (also while paused).</summary>
    [RelayCommand]
    void DebugStop()
    {
        if (IsRunning) engine.Stop();
    }

    /// <summary>Hides the debug panel (it opens again at the next stop).</summary>
    [RelayCommand]
    void CloseDebugPanel()
    {
        if (!IsDebugPaused) IsDebugPanelVisible = false;
    }

    /// <summary>
    /// Called by <c>Launch</c> right before the engine starts: builds the line map and hands a
    /// breakpoint session to the engine when it can be useful.
    /// </summary>
    /// <param name="doc">The document being launched.</param>
    /// <param name="runMode">Play or Render; renders never use breakpoints.</param>
    /// <param name="host">The app's WASAPI settings, or <c>null</c> when Csound drives the device.</param>
    /// <remarks>
    /// A session is created when the document has breakpoints, or when the debugger will be on
    /// anyway for live values (app engine + live values enabled) so breakpoints added later in this
    /// run still work. Otherwise the debugger stays off to keep the run free of its overhead.
    /// All previous debug state is reset first.
    /// </remarks>
    void PrepareBreakpoints(DocumentViewModel doc, RunMode runMode, HostAudioSettings? host)
    {
        debugMap = null;
        debugSession = null;
        debugDocument = null;
        lastHitSerial = 0;
        IsDebugPaused = false;
        IsDebugPanelVisible = false;
        DebugHit = null;
        DebugLine = 0;
        DebugStatusText = "";
        int count = doc.Breakpoints.Count;
        if (runMode == RunMode.Render)
        {
            engine.UseBreakpoints(null);
            if (count > 0)
                AddInfo(Loc.S("⏸ Breakpoints ignorados al renderizar a WAV (solo funcionan al tocar en vivo).",
                              "⏸ Breakpoints are ignored when rendering to WAV (they only work while playing live)."));
            return;
        }
        debugDocument = doc;
        debugMap = new BreakpointLineMap(doc.Document, doc.Analyzer);
        // The debugger is already on for live values; otherwise turn it on only if needed.
        if (count == 0 && !(LiveValuesEnabled && host is not null))
        {
            engine.UseBreakpoints(null);
            return;
        }
        debugSession = new BreakpointSession(debugMap.ToRequests(doc.Breakpoints.Lines, doc.Analyzer));
        engine.UseBreakpoints(debugSession);
    }

    /// <summary>Sends the document's breakpoints to the running session (after a toggle or a recompile).</summary>
    /// <param name="doc">The document whose breakpoints changed; ignored unless it is the debugged one.</param>
    /// <remarks>The editor lines are translated to Csound (instrument, line) requests through <see cref="debugMap"/>.</remarks>
    void SyncBreakpoints(DocumentViewModel doc)
    {
        // The VM's own reference: requests made before the engine thread attached it are kept.
        if (doc != debugDocument || debugMap is null || debugSession is not { } session)
            return;
        session.SetBreakpoints(debugMap.ToRequests(doc.Breakpoints.Lines, doc.Analyzer));
    }

    /// <summary>
    /// Live-recompiles an instrument block so that Csound numbers it with the editor's current
    /// lines, and re-installs the breakpoints of that block.
    /// </summary>
    /// <param name="doc">Document that contains <paramref name="block"/>.</param>
    /// <param name="block">The <c>instr</c>/<c>opcode</c> block to recompile (from the analyzer).</param>
    /// <remarks>
    /// <c>engine.CompileOrcAt</c> prepends blank lines so Csound's line numbers match the editor's
    /// (when possible), and returns the Csound line where the code starts; the map then
    /// records the new numbering of that instrument. Used by literal scrubbing and Ctrl+Enter.
    /// </remarks>
    void CompileBlockLive(DocumentViewModel doc, CodeBlock block)
    {
        int orcStart = engine.CompileOrcAt(doc.Analyzer.BlockText(block), block.StartLine, debugMap?.CsInstrumentsLine ?? 0);
        if (doc == debugDocument && debugMap is not null && block.Keyword == "instr")
        {
            debugMap.Recompiled(block, orcStart);
            SyncBreakpoints(doc);
        }
    }

    /// <summary>Live-compiles arbitrary code (a selection or one line) starting at editor line <paramref name="firstLine"/>.</summary>
    /// <param name="doc">Document the code comes from.</param>
    /// <param name="code">Orchestra code to compile.</param>
    /// <param name="firstLine">1-based editor line of the first line of <paramref name="code"/>; values below 1 mean "unknown" (no remapping).</param>
    void CompileCodeLive(DocumentViewModel doc, string code, int firstLine)
    {
        int orcStart = engine.CompileOrcAt(code, Math.Max(1, firstLine), debugMap?.CsInstrumentsLine ?? 0);
        if (doc != debugDocument || debugMap is null || firstLine < 1)
            return;
        // Instruments entirely inside the evaluated lines now run with the new numbering.
        int lastLine = firstLine + code.Count(c => c == '\n');
        bool any = false;
        foreach (var block in doc.Analyzer.Blocks.Where(b => b.Keyword == "instr" && b.StartLine >= firstLine && b.EndLine <= lastLine))
        {
            debugMap.Recompiled(block, orcStart + (block.StartLine - firstLine));
            any = true;
        }
        if (any)
            SyncBreakpoints(doc);
    }

    /// <summary>Polls the engine every frame: shows a new stop, or clears the paused state.</summary>
    /// <remarks>
    /// Called from <c>Tick</c> on the UI thread. <c>session.IsPaused</c> and <c>session.Current</c>
    /// are written by the performance thread; a stop is published only when its serial differs from
    /// <see cref="lastHitSerial"/>, so a long pause is not re-announced every frame.
    /// </remarks>
    void DebugTick()
    {
        var session = engine.Breakpoints;
        if (session is null || !IsRunning)
        {
            if (IsDebugPaused)
            {
                IsDebugPaused = false;
                DebugStatusText = "";
                BreakpointsChanged?.Invoke();
            }
            if (!IsRunning && IsDebugPanelVisible)
                IsDebugPanelVisible = false; // the run ended: the snapshot no longer applies
            return;
        }
        if (session.IsPaused && session.Current is { } hit && hit.Serial != lastHitSerial)
        {
            lastHitSerial = hit.Serial;
            ShowHit(hit);
        }
        else if (!session.IsPaused && IsDebugPaused)
        {
            IsDebugPaused = false;
            DebugStatusText = "";
            BreakpointsChanged?.Invoke();
        }
    }

    /// <summary>Publishes a stop to the UI: paused line, panel, status text.</summary>
    /// <param name="hit">The stop reported by the session (Csound instrument and line, p-fields, variables).</param>
    /// <remarks>
    /// If the Csound line cannot be mapped back to the editor, the status text shows Csound's own
    /// line number and the editor is not moved. Otherwise the debugged tab is brought to the front
    /// and <see cref="DebugLineRequested"/> asks the window to scroll to the line.
    /// </remarks>
    void ShowHit(BreakpointHit hit)
    {
        int? line = debugMap?.ToEditorLine(hit.Instrument, hit.Line);
        DebugHit = hit;
        DebugLine = line ?? 0;
        IsDebugPaused = true;
        IsDebugPanelVisible = true;
        string where = line is { } l ? Loc.S($"línea {l}", $"line {l}") : Loc.S($"línea {hit.Line} de Csound", $"Csound line {hit.Line}");
        DebugStatusText = Loc.S($"⏸ En pausa en la {where} · instr {hit.Instrument} · F5 continuar · F10 siguiente ciclo",
                                $"⏸ Paused at {where} · instr {hit.Instrument} · F5 continue · F10 next cycle");
        if (debugDocument is { } doc && Documents.Contains(doc) && line is { } target)
        {
            if (SelectedDocument != doc)
                SelectedDocument = doc;
            DebugLineRequested?.Invoke(doc, target);
        }
        BreakpointsChanged?.Invoke();
    }

    /// <summary>Shows a short message in the status bar for 5 s (unless paused, which has priority).</summary>
    /// <param name="text">Already localised message.</param>
    /// <remarks>Stop + Start restarts the 5 s countdown, so a new notice always gets its full time.</remarks>
    void ShowDebugNotice(string text)
    {
        if (IsDebugPaused) return;
        DebugStatusText = text;
        noticeTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) =>
        {
            noticeTimer!.Stop();
            if (!IsDebugPaused) DebugStatusText = "";
        });
        noticeTimer.Stop();
        noticeTimer.Start();
    }

    /// <summary>For tests and the panel: «p1 1 · p2 0.5 · p3 1 · t 0.730 s · k 34».</summary>
    /// <param name="hit">The stop to describe.</param>
    /// <returns>
    /// Instrument number (p1, fractional for tagged instances), start (p2) and duration (p3) of the
    /// stopped note, the score time in seconds and the control-cycle counter; formatted with the
    /// invariant culture so decimals always use a dot.
    /// </returns>
    public static string FormatHitTiming(BreakpointHit hit) =>
        string.Create(CultureInfo.InvariantCulture,
            $"p1 {hit.InstrumentNumber:0.###} · p2 {hit.P2:0.###} · p3 {hit.P3:0.###} · t {hit.ScoreTime:0.000} s · k {hit.KCycle}");
}
