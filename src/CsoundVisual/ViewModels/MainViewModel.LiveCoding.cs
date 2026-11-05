using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsoundVisual.Engine;
using CsoundVisual.Localization;

namespace CsoundVisual.ViewModels;

/// <summary>
/// Live coding and live recording.
/// <list type="bullet">
/// <item><b>Ctrl+Enter</b> evaluates code without stopping the audio: the selection if there is
/// one, otherwise the whole <c>instr … endin</c> block under the caret (recompiled with
/// <c>csoundCompileOrc</c>; new notes use the new definition). When nothing is playing it
/// starts playing instead.</item>
/// <item><b>Ctrl+Shift+Enter</b> sends the selected score lines (or the line under the caret)
/// as real-time events, e.g. <c>i "Voice" 0 2 64 0.8</c>.</item>
/// <item><b>Grabar / Record</b> records what you hear to a WAV file in <c>Documents\Csound Visual\Grabaciones</c>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The keyboard shortcuts are handled by <c>MainWindow</c>, which passes the caret offset and the
/// selection in (the view model never reads the editor control) and listens to
/// <see cref="Evaluated"/> to flash the evaluated lines.
/// </para>
/// <para>
/// Everything here runs on the UI thread. Recompiles and events are only <i>queued</i> on the
/// engine (<c>CompileOrc</c> / <see cref="CsoundEngine.SendEvent"/>); the performance thread applies
/// them at the start of its next control cycle, so the audio never stops. Code is compiled through
/// <c>CompileBlockLive</c> / <c>CompileCodeLive</c> (<c>MainViewModel.Debug.cs</c>), which keep the
/// editor's line numbers so error messages and breakpoints still point at the right lines.
/// </para>
/// <para>
/// The recording itself is done by the engine's <c>WavRecorder</c> (fed by the performance thread,
/// written to disk by its own background thread); this
/// partial only starts/stops it and shows its clock. <c>[NotifyCanExecuteChangedFor]</c> on
/// <see cref="IsRecording"/> makes the generated <c>ToggleRecordingCommand</c> re-query
/// <see cref="CanToggleRecording"/>, so the Record button greys out and back as playback starts
/// and stops (the <c>Mode</c> property in <c>MainViewModel.cs</c> carries the same attribute, so a
/// change of run mode re-queries it too).
/// </para>
/// </remarks>
public sealed partial class MainViewModel
{
    /// <summary>
    /// True while recording the live output. Generated property <c>IsRecording</c>; changing it also
    /// raises <see cref="RecordText"/> and refreshes <c>ToggleRecordingCommand.CanExecute</c>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordText))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordingCommand))]
    bool isRecording;

    /// <summary>Recording clock shown while recording ("● REC 00:12", minutes:seconds); empty otherwise. Set by <see cref="UpdateRecording"/>.</summary>
    [ObservableProperty] string recordClock = "";

    /// <summary>
    /// Label of the record button. The toolbar overlays both labels and only toggles their
    /// visibility, so the button keeps a fixed width when the text changes.
    /// </summary>
    public string RecordText => IsRecording ? Loc.S("Detener grabación", "Stop recording") : Loc.S("Grabar", "Record");

    /// <summary>Raised to flash the evaluated lines in the editor (first line, last line; 1-based, inclusive). Handled by <c>MainWindow</c>.</summary>
    public event Action<int, int>? Evaluated;

    /// <summary>Evaluates the selection or the instrument under the caret (see the class remarks).</summary>
    /// <param name="caretOffset">Absolute caret offset.</param>
    /// <param name="selection">Selected text, or empty.</param>
    /// <param name="selectionStartLine">First selected line (for the flash), when there is a selection.</param>
    /// <param name="selectionEndLine">Last selected line, when there is a selection.</param>
    /// <remarks>
    /// Order of precedence: nothing running → behave like Play; another tab is playing → only a
    /// hint (code must go to the orchestra that is running); a selection → compile exactly that
    /// text; caret inside a block → recompile the whole block; otherwise → compile the caret line
    /// alone (useful for a global statement such as <c>giTable ftgen …</c>).
    /// </remarks>
    public void EvaluateCode(int caretOffset, string selection, int selectionStartLine, int selectionEndLine)
    {
        if (SelectedDocument is not { } doc)
            return;
        if (!IsRunning)
        {
            // Nothing to evaluate into: Ctrl+Enter doubles as "Play" when idle.
            if (PlayCommand.CanExecute(null)) PlayCommand.Execute(null);
            return;
        }
        if (!doc.IsPlaying)
        {
            AddInfo(Loc.S("Ctrl+Enter evalúa código en el documento que está sonando.", "Ctrl+Enter evaluates code in the document that is playing."));
            return;
        }

        if (!string.IsNullOrWhiteSpace(selection))
        {
            CompileCodeLive(doc, selection, selectionStartLine); // keeps editor line numbers (MainViewModel.Debug.cs)
            AddInfo(Loc.S($"↻ Evaluado: líneas {selectionStartLine}–{selectionEndLine}", $"↻ Evaluated: lines {selectionStartLine}–{selectionEndLine}"));
            Evaluated?.Invoke(selectionStartLine, selectionEndLine);
            return;
        }

        var analyzer = doc.Analyzer;
        int line = analyzer.LineOf(caretOffset);
        if (analyzer.BlockAt(line) is { } block)
        {
            CompileBlockLive(doc, block);
            AddInfo(Loc.S($"↻ Recompilado {block.Label} (las notas nuevas usan la versión nueva)", $"↻ Recompiled {block.Label} (new notes use the new version)"));
            Evaluated?.Invoke(block.StartLine, block.EndLine);
        }
        else
        {
            string code = analyzer.LineText(line);
            if (string.IsNullOrWhiteSpace(code)) return;
            CompileCodeLive(doc, code, line);
            AddInfo(Loc.S($"↻ Evaluada la línea {line}", $"↻ Evaluated line {line}"));
            Evaluated?.Invoke(line, line);
        }
    }

    /// <summary>Sends score lines (selection or caret line) as real-time events.</summary>
    /// <param name="caretOffset">Absolute caret offset (used when there is no selection, and for the flash).</param>
    /// <param name="selection">Selected text, or empty to send the caret line.</param>
    /// <remarks>
    /// Each line is sent verbatim to <see cref="CsoundEngine.SendEvent"/> (queued, applied on the
    /// next control cycle), so p2 is relative to "now". Unlike <see cref="EvaluateCode"/>, this
    /// does not check that the selected tab is the one playing: the events go to whatever
    /// orchestra is running.
    /// </remarks>
    public void SendScoreLines(int caretOffset, string selection)
    {
        if (SelectedDocument is not { } doc || !IsRunning)
            return;
        var analyzer = doc.Analyzer;
        int line = analyzer.LineOf(caretOffset);
        string text = string.IsNullOrWhiteSpace(selection) ? analyzer.LineText(line) : selection;
        // One event per non-empty line, with the ';' comment stripped. Lines are not validated:
        // a non-score line (e.g. orchestra code) is passed on and Csound reports the error.
        var events = text.Split('\n').Select(l => l.Split(';')[0].Trim()).Where(l => l.Length > 0).ToList();
        foreach (var e in events)
            engine.SendEvent(e);
        if (events.Count > 0)
        {
            AddInfo(Loc.S($"→ {events.Count} evento(s): {events[0]}{(events.Count > 1 ? " …" : "")}", $"→ {events.Count} event(s): {events[0]}{(events.Count > 1 ? " …" : "")}"));
            // Flash the lines that were actually sent from. Fix: the flash used to start at the caret
            // line and span one line per event, which was wrong twice over: (1) after a normal
            // top-to-bottom drag the caret sits at the END of the selection, so the flash ran below
            // it; (2) blank and comment lines inside the selection were not counted, so the range
            // came out too short. Now the selection's real line range is computed (see SelectionLines).
            var (first, last) = string.IsNullOrWhiteSpace(selection) ? (line, line) : SelectionLines(doc, caretOffset, selection);
            Evaluated?.Invoke(first, last);
        }
        else if (string.IsNullOrWhiteSpace(selection))
        {
            // The caret line is blank or only a comment: say so instead of silently doing nothing.
            AddInfo(Loc.S("Ctrl+Shift+Enter: la línea del cursor no tiene un evento de partitura (está vacía o es un comentario).",
                          "Ctrl+Shift+Enter: the caret line has no score event (it is blank or a comment)."));
        }
    }

    /// <summary>
    /// First and last (1-based) editor lines covered by <paramref name="selection"/>, knowing only
    /// the caret offset and the selected text (the window does not pass the selection bounds).
    /// </summary>
    /// <param name="doc">The document the selection belongs to.</param>
    /// <param name="caretOffset">Absolute caret offset; AvaloniaEdit keeps the caret at one end of the selection.</param>
    /// <param name="selection">The selected text, exactly as in the document.</param>
    /// <returns>The inclusive line range of the selection.</returns>
    /// <remarks>
    /// The caret is either at the selection's end (dragged/shift-selected downwards, the usual case)
    /// or at its start (selected upwards). If the text just before the caret equals the selection,
    /// the caret is at the end. A selection that ends with a line break stops at the end of the
    /// previous line, so that trailing break does not add an extra (unselected) line.
    /// </remarks>
    static (int First, int Last) SelectionLines(DocumentViewModel doc, int caretOffset, string selection)
    {
        var text = doc.Document;
        int start = caretOffset;
        if (caretOffset >= selection.Length
            && string.Equals(text.GetText(caretOffset - selection.Length, selection.Length), selection, StringComparison.Ordinal))
            start = caretOffset - selection.Length;
        int end = start + selection.Length;              // exclusive
        if (selection.EndsWith('\n')) end--;             // do not count the line after a trailing break
        if (end > start) end--;                          // last selected character (inclusive)
        var analyzer = doc.Analyzer;
        return (analyzer.LineOf(start), Math.Max(analyzer.LineOf(start), analyzer.LineOf(end)));
    }

    /// <summary>True when a new recording may start: only during a live performance (<see cref="RunMode.Play"/>).</summary>
    bool CanRecord() => Mode == RunMode.Play;

    /// <summary>
    /// Enables the record button only while something is playing live (or while a recording is
    /// running, so it can always be stopped). Rendering to a file does not count: that already
    /// writes a WAV.
    /// </summary>
    bool CanToggleRecording() => IsRecording || CanRecord();

    /// <summary>Where live recordings are written (Documents\Csound Visual\Grabaciones).</summary>
    public static string RecordingsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Csound Visual", "Grabaciones");

    /// <summary>
    /// Opens the recordings folder in Explorer (creating it if needed). Generated
    /// <c>OpenRecordingsFolderCommand</c>, used by the Record button's right-click menu, which is
    /// hosted in a wrapper panel so it still opens while the button itself is disabled.
    /// </summary>
    [RelayCommand]
    void OpenRecordingsFolder()
    {
        Directory.CreateDirectory(RecordingsFolder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{RecordingsFolder}\"") { UseShellExecute = true });
    }

    /// <summary>Opens Explorer with the file selected (<c>explorer.exe /select,"path"</c>); does nothing if the file is gone.</summary>
    /// <param name="path">Full path of the file to reveal.</param>
    static void ShowInExplorer(string path)
    {
        if (File.Exists(path))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    /// <summary>
    /// Starts or stops recording the live output (generated <c>ToggleRecordingCommand</c>, bound to
    /// the Record button and Ctrl+Shift+R; enabled per <see cref="CanToggleRecording"/>).
    /// </summary>
    /// <remarks>
    /// The file is named after the selected document plus a local timestamp
    /// (<c>Demo_2026-11-04_21-30-05.wav</c>), so recordings never overwrite each other.
    /// <see cref="IsRecording"/> is taken from the engine's actual recorder rather than assumed,
    /// because <c>CsoundEngine.StartRecording</c> silently does nothing when the engine is not
    /// running or its sample rate is not known yet. (<c>WavRecorder</c> creates the folder itself.)
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanToggleRecording))]
    void ToggleRecording()
    {
        if (IsRecording)
        {
            FinishRecording();
            return;
        }
        if (!CanRecord())
        {
            AddInfo(Loc.S("Presiona Tocar primero: se graba lo que está sonando.", "Press Play first: recording captures what is playing."));
            return;
        }
        string folder = RecordingsFolder;
        string name = Path.GetFileNameWithoutExtension(SelectedDocument?.FileName ?? Loc.S("grabacion", "recording"));
        string path = Path.Combine(folder, $"{name}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.wav");
        // Fix: WavRecorder opens the file in its constructor, so a full disk, a read-only or
        // OneDrive-locked Documents folder, or a denied folder (Controlled Folder Access) throws here.
        // Uncaught, that exception escaped the relay command (crashing the app or being swallowed by
        // the dispatcher). Now it is reported in the console and the button stays in "Record".
        try
        {
            engine.StartRecording(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConsoleLines.Add(new ConsoleLine(Loc.S(
                $"✗ No se pudo empezar a grabar en {path}: {ex.Message}",
                $"✗ Could not start recording to {path}: {ex.Message}"), MessageKind.Error));
            IsRecording = false;
            return;
        }
        IsRecording = engine.Recorder is not null;
        if (IsRecording)
            AddInfo(Loc.S($"● Grabando en {path}", $"● Recording to {path}"));
    }

    /// <summary>
    /// Closes the recording and resets the UI state. Called by the toggle, and by the
    /// performance-finished handler in <c>MainViewModel.cs</c> so a recording never outlives the
    /// run. The "saved" console line carries
    /// <c>FilePath</c>, which makes the console line clickable (<c>GoToSource</c> then calls
    /// <see cref="ShowInExplorer"/>).
    /// </summary>
    /// <remarks>
    /// Finishing the file (flushing the last blocks, patching the RIFF sizes) can also fail with an
    /// <see cref="IOException"/> (disk full, device removed). That is reported in the console and the
    /// UI is reset anyway: the engine has already dropped the recorder, so there is nothing left to stop.
    /// </remarks>
    void FinishRecording()
    {
        try
        {
            if (engine.StopRecording() is { } path)
                ConsoleLines.Add(new ConsoleLine(Loc.S($"✓ Grabación guardada: {path}   (clic aquí para verla en el Explorador)", $"✓ Recording saved: {path}   (click here to show it in Explorer)"), MessageKind.Info) { FilePath = path });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConsoleLines.Add(new ConsoleLine(Loc.S(
                $"✗ Error al terminar la grabación (el archivo puede estar incompleto): {ex.Message}",
                $"✗ Error while finishing the recording (the file may be incomplete): {ex.Message}"), MessageKind.Error));
        }
        IsRecording = false;
        RecordClock = "";
    }

    /// <summary>
    /// Updates the recording clock (called from <see cref="Tick"/> on every animation frame while
    /// running). The clock is the recorder's written duration, i.e. audio time, not wall time.
    /// </summary>
    void UpdateRecording()
    {
        if (!IsRecording) return;
        if (engine.Recorder is { } r)
        {
            var t = TimeSpan.FromSeconds(r.Seconds);
            RecordClock = $"● REC {(int)t.TotalMinutes:00}:{t.Seconds:00}";
        }
        else
            FinishRecording(); // defensive: the recorder vanished without going through FinishRecording; resync the UI
    }
}
