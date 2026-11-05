using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsoundVisual.Editor;
using CsoundVisual.Engine;
using CsoundVisual.Interop;
using CsoundVisual.Localization;

namespace CsoundVisual.ViewModels;


/// <summary>What the engine is currently doing.</summary>
public enum RunMode
{
    /// <summary>Idle.</summary>
    None,
    /// <summary>Playing to the audio device in real time.</summary>
    Play,
    /// <summary>Rendering to a WAV file as fast as possible.</summary>
    Render,
}

/// <summary>Answer of the "unsaved changes" prompt.</summary>
public enum SaveChoice
{
    /// <summary>
    /// Abort the operation. MUST stay the first member (value 0): closing the dialog with the
    /// window's ✕ makes <c>ShowDialog&lt;SaveChoice&gt;</c> return <c>default(SaveChoice)</c>, and
    /// that has to mean «cancel», never «save».
    /// </summary>
    Cancel = 0,
    /// <summary>Save, then continue.</summary>
    Save,
    /// <summary>Continue without saving.</summary>
    Discard,
}

/// <summary>
/// Root view model of the main window: open documents (tabs), the Csound engine, audio device
/// selection, console, tables, and the services the editor's hover/drag feature needs
/// (<see cref="IScrubHost"/>).
/// </summary>
/// <remarks>
/// <para>
/// The view model never references Avalonia controls. Anything that needs the window (file
/// pickers, the save prompt, moving the caret) goes through the delegates/events at the top of
/// the class, which <c>MainWindow</c> provides.
/// </para>
/// <para>
/// <b>Partial class map.</b> This file holds the core: construction, documents and files, the
/// play / render / stop life cycle, the console and the <see cref="IScrubHost"/> services. The other
/// parts live next to it as <c>MainViewModel.&lt;Area&gt;.cs</c>: Audio (devices, latency, meters,
/// recording), Midi, Live / LiveCoding (watches, Ctrl+Enter evaluation), Debug (breakpoints), Daw
/// («Send to DAW»), Link (Ableton Link), Plugins (VST3 chain), Paint, MusicTools (piano roll, step
/// sequencer), Visual3D, Examples, Appearance and Settings.
/// </para>
/// <para>
/// <b>MVVM conventions</b> (CommunityToolkit.Mvvm source generators): a private field marked
/// <c>[ObservableProperty]</c> (e.g. <c>mode</c>) generates the public property (<c>Mode</c>) with
/// change notification, plus optional <c>partial void OnModeChanged(...)</c> hooks that any part may
/// implement; <c>[NotifyPropertyChangedFor]</c> re-raises dependent computed properties and
/// <c>[NotifyCanExecuteChangedFor]</c> re-queries commands. A method marked <c>[RelayCommand]</c>
/// (e.g. <c>Play</c>) generates <c>PlayCommand</c>; <c>CanExecute = nameof(CanStart)</c> wires its
/// enabled state; async methods become async commands (the button stays disabled while running).
/// </para>
/// <para>
/// <b>Threading.</b> The class lives on the UI thread. Engine callbacks (<see cref="CsoundEngine.Finished"/>,
/// <c>TablesCaptured</c>) arrive on the Csound performance thread and are re-posted with
/// <see cref="Dispatcher.UIThread"/>; console messages are queued by the engine and drained by
/// <see cref="Tick"/>, which the window calls every frame.
/// </para>
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IScrubHost
{
    /// <summary>Console cap: the oldest lines are dropped beyond this, so a chatty orchestra cannot grow memory or slow the ListBox.</summary>
    const int MaxConsoleLines = 4000;

    /// <summary>Text of a new, empty document.</summary>
    /// <remarks>
    /// A minimal working orchestra: a <c>chnk</c> declaration (a Csound 7 opcode) creates the "amp"
    /// channel with range 0…1 and default 0.5, so the LIVE CONTROLS card shows a slider at once;
    /// instr 1 is a band-limited saw (<c>vco2</c>) at p4 Hz scaled by that channel; the score plays it
    /// 4 s at 220 Hz. Also the fallback when the embedded demo cannot be read.
    /// </remarks>
    const string NewDocumentTemplate =
        "<CsoundSynthesizer>\n<CsOptions>\n-odac\n</CsOptions>\n<CsInstruments>\nsr = 48000\nksmps = 64\nnchnls = 2\n0dbfs = 1\n\n" +
        "chnk \"amp\", 1, 2, 0.5, 0, 1\n\ninstr 1\n  kamp chnget \"amp\"\n  asig vco2 kamp * 0.3, p4\n  out asig, asig\nendin\n" +
        "</CsInstruments>\n<CsScore>\ni 1 0 4 220\n</CsScore>\n</CsoundSynthesizer>\n";

    /// <summary>The app's single Csound engine (one performance at a time); exposed as <see cref="Engine"/>.</summary>
    readonly CsoundEngine engine = new();

    /// <summary>False when Csound could not be loaded at start-up; disables Play / Render (<see cref="CanStart"/>).</summary>
    readonly bool csoundAvailable;

    /// <summary>Persistent user settings, loaded once here and saved by the parts that change them.</summary>
    readonly AppSettings settings = AppSettings.Load();

    /// <summary>Throttle for live recompilation while a literal is dragged (see <see cref="LiteralScrubbed"/>).</summary>
    readonly DispatcherTimer recompileTimer;

    /// <summary>
    /// The instrument waiting to be recompiled: its document and the editor line where the block
    /// starts (the block is looked up again at flush time because the text keeps changing). Only the
    /// latest request is kept.
    /// </summary>
    (DocumentViewModel Doc, int StartLine)? pendingRecompile;

    /// <summary>
    /// Render in progress: <c>renderTemp</c> = space-free temporary WAV Csound writes to,
    /// <c>renderTarget</c> = the path the user chose; <see cref="OnFinished"/> moves one to the other.
    /// </summary>
    string? renderTemp, renderTarget;

    /// <summary>
    /// Document of the current or last performance. Console line numbers and error lines refer to
    /// it, even when another tab is shown.
    /// </summary>
    DocumentViewModel? lastRunDocument;

    /// <summary>The document whose <c>PropertyChanged</c> is relayed to <see cref="WindowTitle"/> (the selected one).</summary>
    DocumentViewModel? observedDocument;

    /// <summary>Creates the view model.</summary>
    /// <param name="csoundAvailable">False when no Csound installation was found (everything that needs the engine is disabled).</param>
    /// <remarks>
    /// Created by <c>App</c> on the UI thread after the UI language has been applied, so every text
    /// built here is already in the right language. Restores the tabs open last time (missing
    /// files are skipped; with none left the demo opens), then, if Csound is present, starts the
    /// background opcode / audio-device discovery (<see cref="LoadCatalogAsync"/>).
    /// </remarks>
    public MainViewModel(bool csoundAvailable)
    {
        this.csoundAvailable = csoundAvailable;
        // Static hook: the slider rows' «forget MIDI» menu calls back into this instance.
        ChannelControlViewModel.ForgetMidiHandler = ForgetMidi;
        Loc.Changed += RefreshAudioTexts;
        // Both engine events are raised on the Csound performance thread: hop to the UI thread.
        engine.Finished += code => Dispatcher.UIThread.Post(() => OnFinished(code));
        engine.TablesCaptured += tables => Dispatcher.UIThread.Post(() => ShowTables(tables));
        InitLink(); // Ableton Link, if it was on last time (MainViewModel.Link.cs)

        // Live recompilation is throttled: at most one compile every 120 ms while dragging.
        recompileTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        recompileTimer.Tick += (_, _) => FlushRecompile();

        foreach (var path in settings.OpenFiles.Where(File.Exists))
            AddDocument(File.ReadAllText(path), path);
        if (Documents.Count == 0)
            AddDocument(LoadDemo(), null).UntitledName = "Demo.csd";
        SelectedDocument = Documents[0];
        // Set the backing field directly: going through the property would run its change hook
        // (which saves the setting) during construction.
        liveValuesEnabled = settings.LiveValues;

        if (csoundAvailable)
        {
            csoundVersion = CsoundLocator.VersionText;
            AddInfo(Loc.S($"{CsoundVersion} cargado desde {CsoundLocator.Home}", $"{CsoundVersion} loaded from {CsoundLocator.Home}"));
            RefreshMidiPorts();
            _ = LoadCatalogAsync();
        }
        else
        {
            ConsoleLines.Add(new ConsoleLine(
                Loc.S("No se encontró csound7_64.dll. Define CSOUND_VISUAL_HOME o instala Csound 7.",
                      "csound7_64.dll not found. Set CSOUND_VISUAL_HOME or install Csound 7."), MessageKind.Error));
        }
    }

    // ------------------------------------------------------------------------------------------
    //  Services provided by the window
    // ------------------------------------------------------------------------------------------

    /// <summary>Shows an "open file" picker; returns the chosen path or <c>null</c>.</summary>
    public Func<Task<string?>>? PickOpenFile { get; set; }

    /// <summary>Shows a "save file" picker for (suggested name, extension); returns the path or <c>null</c>.</summary>
    public Func<string, string, Task<string?>>? PickSaveFile { get; set; }

    /// <summary>Asks whether to save a dirty document before closing it.</summary>
    public Func<DocumentViewModel, Task<SaveChoice>>? ConfirmSave { get; set; }

    /// <summary>Asks the view to show a line of a document (select its tab, move the caret, scroll).</summary>
    public event Action<DocumentViewModel, int>? GoToLineRequested;

    /// <summary>Raised when the error lines of a document changed and the editor must repaint.</summary>
    public event Action? ErrorLinesChanged;

    /// <summary>Raised once the full opcode list is available (the view refreshes highlighting).</summary>
    public event Action? OpcodesLoaded;

    // ------------------------------------------------------------------------------------------
    //  State
    // ------------------------------------------------------------------------------------------

    /// <summary>Open documents, one per tab.</summary>
    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    /// <summary>Console lines (Csound output and app messages).</summary>
    public ObservableCollection<ConsoleLine> ConsoleLines { get; } = [];


    /// <summary>Function tables captured from the running performance.</summary>
    public ObservableCollection<TableSnapshot> Tables { get; } = [];

    /// <summary>Csound version text for the status bar.</summary>
    public string CsoundVersion => csoundVersion ?? Loc.S("Csound no encontrado", "Csound not found");

    /// <summary>Version of the loaded Csound, or <c>null</c> when none was found.</summary>
    readonly string? csoundVersion;

    /// <summary>The engine (the window reads meters and scope from it every frame).</summary>
    public CsoundEngine Engine => engine;

    /// <summary>Document shown in the editor.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    DocumentViewModel? selectedDocument;

    /// <summary>What the engine is doing; drives button states.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsIdle), nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand), nameof(RenderCommand), nameof(StopCommand), nameof(SendEventCommand), nameof(RefreshTablesCommand), nameof(ToggleRecordingCommand))]
    RunMode mode;

    /// <summary>Table shown in the table viewer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TableInfo))]
    TableSnapshot? selectedTable;

    /// <summary>Score time display (mm:ss.d).</summary>
    [ObservableProperty] string timeText = "00:00.0";

    /// <summary>"48000 Hz · 2 ch · ksmps 64" while running.</summary>
    [ObservableProperty] string engineInfo = "";

    /// <summary>Caret position for the status bar.</summary>
    [ObservableProperty] string cursorText = "Ln 1, Col 1";

    /// <summary>Text of the live event box (pre-filled with an example that matches the demo's «Voice» instrument).</summary>
    [ObservableProperty] string liveEvent = "i \"Voice\" 0 1 64 0.9 0.5";

    /// <summary>True when the window is narrow: toolbar shows icons only (set by <c>MainWindow.ApplyResponsiveLayout</c>, toolbar level ≥ 1).</summary>
    [ObservableProperty] bool isCompact;

    /// <summary>Whether the right-hand panel is shown.</summary>
    [ObservableProperty] bool isSidePanelVisible = true;

    /// <summary>True while playing or rendering.</summary>
    public bool IsRunning => Mode != RunMode.None;

    /// <summary>True while idle.</summary>
    public bool IsIdle => Mode == RunMode.None;

    /// <summary>True when at least one table was captured.</summary>
    public bool HasTables => Tables.Count > 0;

    /// <summary>Window title.</summary>
    public string WindowTitle => $"{SelectedDocument?.Title ?? Loc.S("sin título", "untitled")} — Csound Visual";

    /// <summary>Range text under the table viewer.</summary>
    public string TableInfo => SelectedTable is { } t
        ? Loc.S($"mín {CsoundAnalyzer.Format(t.Minimum)}  ·  máx {CsoundAnalyzer.Format(t.Maximum)}",
                $"min {CsoundAnalyzer.Format(t.Minimum)}  ·  max {CsoundAnalyzer.Format(t.Maximum)}")
        : "";

    /// <summary>Status pill text.</summary>
    public string StatusText => Mode switch
    {
        RunMode.Play => Loc.S("Sonando", "Playing"),
        RunMode.Render => Loc.S("Renderizando", "Rendering"),
        _ => Loc.S("Listo", "Ready"),
    };

    // ------------------------------------------------------------------------------------------
    //  Documents and files
    // ------------------------------------------------------------------------------------------

    /// <summary>Creates a tab for <paramref name="text"/> and appends it (without selecting it).</summary>
    /// <param name="text">Initial document text.</param>
    /// <param name="path">File the text came from, or null for an untitled document.</param>
    /// <returns>The new document.</returns>
    /// <remarks>
    /// Each document gets <see cref="SendChannelValue"/> as its slider sink, so slider moves reach
    /// the engine (and the DAW plug-in when linked).
    /// </remarks>
    DocumentViewModel AddDocument(string text, string? path)
    {
        var doc = new DocumentViewModel(text, path, SendChannelValue);
        Documents.Add(doc);
        return doc;
    }

    /// <summary>
    /// Generated-property hook for <see cref="SelectedDocument"/>: moves the title relay to the new
    /// document, re-detects its channel sliders and re-labels their MIDI assignments.
    /// </summary>
    /// <param name="value">The newly shown document, or null.</param>
    partial void OnSelectedDocumentChanged(DocumentViewModel? value)
    {
        // Relay the selected document's title changes to the window title.
        if (observedDocument is not null)
            observedDocument.PropertyChanged -= OnSelectedDocumentPropertyChanged;
        observedDocument = value;
        if (value is not null)
        {
            value.PropertyChanged += OnSelectedDocumentPropertyChanged;
            value.RefreshChannels();
            ApplyMidiBindingLabels(value);
        }
    }

    /// <summary>Re-raises <see cref="WindowTitle"/> when the shown document's title changes (rename, dirty mark).</summary>
    void OnSelectedDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.Title))
            OnPropertyChanged(nameof(WindowTitle));
    }

    /// <summary>Opens a new tab with a small template.</summary>
    [RelayCommand]
    void New()
    {
        var doc = AddDocument(NewDocumentTemplate, null);
        // The template itself is not a change worth a "save?" prompt.
        doc.IsDirty = false;
        SelectedDocument = doc;
    }

    /// <summary>Asks for a file and opens it in a tab.</summary>
    [RelayCommand]
    async Task Open()
    {
        if (PickOpenFile is null) return;
        if (await PickOpenFile() is { } path)
            OpenPath(path);
    }

    /// <summary>Opens <paramref name="path"/>, or switches to its tab if it is already open.</summary>
    /// <param name="path">Full path of a Csound file (from the picker, the command line, drag and drop or the examples).</param>
    /// <remarks>
    /// Paths are compared case-insensitively (Windows file system). <c>FollowWithPlayback</c>: if
    /// another document is playing, the performance switches to this one (when idle, opening never
    /// starts sound). Read errors go to the console instead of throwing.
    /// </remarks>
    public void OpenPath(string path)
    {
        var existing = Documents.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedDocument = existing;
            FollowWithPlayback(existing);
            return;
        }
        try
        {
            var doc = AddDocument(File.ReadAllText(path), path);
            doc.IsDirty = false;
            SelectedDocument = doc;
            FollowWithPlayback(doc);
            AddInfo(Loc.S($"Abierto {path}", $"Opened {path}"));
        }
        catch (Exception ex)
        {
            ConsoleLines.Add(new ConsoleLine(Loc.S($"No se pudo abrir {path}: {ex.Message}", $"Could not open {path}: {ex.Message}"), MessageKind.Error));
        }
    }

    /// <summary>Saves the shown document (asking for a name if it has none).</summary>
    [RelayCommand]
    async Task Save()
    {
        if (SelectedDocument is { } doc)
            await SaveDocumentAsync(doc);
    }

    /// <summary>Saves the shown document under a new name.</summary>
    [RelayCommand]
    async Task SaveAs()
    {
        if (SelectedDocument is { } doc)
            await SaveDocumentAsAsync(doc);
    }

    /// <summary>
    /// True for the demo files of the source tree (Assets\Demo.csd, Assets\DemoEnglish.csd):
    /// opened by hand they must not be overwritten, or the next build would ship the edits.
    /// </summary>
    static bool IsBundledDemo(string path) =>
        Path.GetFileName(Path.GetDirectoryName(path)) is "Assets" &&
        Path.GetFileName(path) is "Demo.csd" or "DemoEnglish.csd";

    /// <summary>«Save»: writes to the document's own file, or falls back to «Save as».</summary>
    /// <param name="doc">Document to save.</param>
    /// <returns>True when the text was written; false when the picker was cancelled or writing failed.</returns>
    async Task<bool> SaveDocumentAsync(DocumentViewModel doc)
    {
        // Bundled examples are never overwritten: saving one asks for a new name.
        if (doc.FilePath is null || ExampleCatalog.Contains(doc.FilePath) || IsBundledDemo(doc.FilePath))
            return await SaveDocumentAsAsync(doc);
        return Write(doc, doc.FilePath);
    }

    /// <summary>«Save as»: asks for a path (suggesting the current file name, or the template name
    /// such as «DrumKit.csd», or «new.csd») and writes there.</summary>
    /// <param name="doc">Document to save.</param>
    /// <returns>True when the text was written.</returns>
    async Task<bool> SaveDocumentAsAsync(DocumentViewModel doc)
    {
        if (PickSaveFile is null) return false;
        string? path = await PickSaveFile(doc.FilePath is null ? doc.UntitledName ?? Loc.S("nuevo.csd", "new.csd") : Path.GetFileName(doc.FilePath), "csd");
        return path is not null && Write(doc, path);
    }

    /// <summary>Writes the document text to <paramref name="path"/> and marks it saved there (new file path, clean state).</summary>
    /// <param name="doc">Document to write.</param>
    /// <param name="path">Destination file; overwritten.</param>
    /// <returns>True on success; on failure the error is shown in the console.</returns>
    /// <remarks>Written as UTF-8 without BOM (the <c>File.WriteAllText</c> default), which Csound reads fine.</remarks>
    bool Write(DocumentViewModel doc, string path)
    {
        try
        {
            File.WriteAllText(path, doc.Document.Text);
            doc.MarkSaved(path);
            return true;
        }
        catch (Exception ex)
        {
            ConsoleLines.Add(new ConsoleLine(Loc.S($"No se pudo guardar {path}: {ex.Message}", $"Could not save {path}: {ex.Message}"), MessageKind.Error));
            return false;
        }
    }

    /// <summary>Closes a tab, asking to save it first when it has unsaved changes.</summary>
    [RelayCommand]
    async Task CloseDocument(DocumentViewModel doc)
    {
        if (!await ResolveUnsavedAsync(doc))
            return;
        if (doc.IsPlaying)
            engine.Stop();

        // There is always at least one tab: closing the last one opens a fresh template. Otherwise
        // the tab that slides into the closed one's position becomes selected.
        int index = Documents.IndexOf(doc);
        Documents.Remove(doc);
        if (Documents.Count == 0)
            New();
        else if (SelectedDocument == doc || SelectedDocument is null)
            SelectedDocument = Documents[Math.Clamp(index, 0, Documents.Count - 1)];
    }

    /// <summary>
    /// Called when the window is about to close: asks about every unsaved document and stores
    /// the list of open files. Returns <c>false</c> if the user cancelled.
    /// </summary>
    public async Task<bool> ConfirmCloseAllAsync()
    {
        foreach (var doc in Documents.ToList())
        {
            // Show each document while its prompt is open, so the user sees what is being asked about.
            SelectedDocument = doc;
            if (!await ResolveUnsavedAsync(doc))
                return false;
        }
        // Untitled documents are not remembered (they have no file to reopen).
        settings.OpenFiles = [.. Documents.Where(d => d.FilePath is not null).Select(d => d.FilePath!)];
        settings.Save();
        return true;
    }

    /// <summary>Returns <c>true</c> when it is OK to drop <paramref name="doc"/>'s changes (saved, discarded or clean).</summary>
    /// <param name="doc">Document about to be closed.</param>
    /// <remarks>Without a window (no <see cref="ConfirmSave"/> delegate) nothing can be asked, so it answers true.</remarks>
    async Task<bool> ResolveUnsavedAsync(DocumentViewModel doc)
    {
        if (!doc.IsDirty || ConfirmSave is null)
            return true;
        // Cancel (also the ✕ of the dialog, see SaveChoice) → false; a failed or cancelled save → false.
        return await ConfirmSave(doc) switch
        {
            SaveChoice.Save => await SaveDocumentAsync(doc),
            SaveChoice.Discard => true,
            _ => false,
        };
    }

    // ------------------------------------------------------------------------------------------
    //  Performance
    // ------------------------------------------------------------------------------------------

    /// <summary>CanExecute of Play / Render: Csound is loaded and nothing is running (one performance at a time).</summary>
    bool CanStart() => csoundAvailable && Mode == RunMode.None;

    /// <summary>CanExecute of Stop and of the commands that need a running performance.</summary>
    bool CanStop() => Mode != RunMode.None;

    /// <summary>Plays the shown document on the selected audio device.</summary>
    /// <remarks>
    /// The document's <c>&lt;CsOptions&gt;</c> are extracted (minus the ones the app controls);
    /// <c>ApplyAudioOptions</c> (MainViewModel.Audio.cs) adds the output flags for the chosen back-end
    /// and returns the WASAPI host settings, or null when Csound's own audio module (PortAudio…) is used.
    /// <c>-d</c> suppresses Csound's graphical table displays.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanStart))]
    void Play()
    {
        if (SelectedDocument is not { } doc) return;
        var (csd, options) = CsdTools.ExtractOptions(doc.Document.Text);
        var host = ApplyAudioOptions(options);
        options.Add("-d");
        Launch(doc, csd, options, RunMode.Play, host);
    }

    /// <summary>Renders the shown document to a WAV file chosen by the user.</summary>
    /// <remarks>
    /// Offline, as fast as the CPU allows: <c>-W</c> = WAV format, <c>-o&lt;temp&gt;</c> = output file.
    /// The file's own output flags (<c>-odac</c>, <c>-W</c>, <c>-A</c>, <c>-n</c>…) were already removed
    /// by <c>CsdTools.ExtractOptions</c>, so they cannot redirect the render. On completion
    /// <see cref="OnFinished"/> moves the temporary file to the chosen target (or deletes it on error).
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanStart))]
    async Task Render()
    {
        if (SelectedDocument is not { } doc || PickSaveFile is null) return;
        string suggested = Path.ChangeExtension(doc.FileName, ".wav");
        if (await PickSaveFile(suggested, "wav") is not { } target)
            return;

        // csoundSetOption splits on spaces: render to a space-free temp path, then move the file.
        renderTarget = target;
        renderTemp = Path.Combine(Path.GetTempPath(), $"csoundvisual_{Guid.NewGuid():N}.wav");
        if (renderTemp.Contains(' '))
        {
            ConsoleLines.Add(new ConsoleLine(Loc.S("La carpeta temporal tiene espacios; no se puede renderizar.",
                "The temp folder path contains spaces; cannot render."), MessageKind.Error));
            return;
        }

        var (csd, options) = CsdTools.ExtractOptions(doc.Document.Text);
        options.Insert(0, "-o" + renderTemp);
        options.Insert(0, "-W");
        options.Add("-d");
        Launch(doc, csd, options, RunMode.Render);
    }

    /// <summary>Common start of a performance (Play) or a render: resets the run state and starts the engine.</summary>
    /// <param name="doc">Document being run; becomes <see cref="lastRunDocument"/> and shows the "playing" dot.</param>
    /// <param name="csd">CSD text with its <c>&lt;CsOptions&gt;</c> block blanked out (line numbers kept).</param>
    /// <param name="options">Command-line options for <c>csoundSetOption</c>, in order.</param>
    /// <param name="runMode">Play or Render.</param>
    /// <param name="host">WASAPI host settings when the app's own audio engine plays; null for renders
    /// and for Csound-managed audio modules.</param>
    /// <remarks>
    /// The engine starts its own performance thread and returns at once; the end of the run comes
    /// back through <see cref="CsoundEngine.Finished"/> → <see cref="OnFinished"/>. Live values, the
    /// MIDI hub and the device label only make sense in real time, so they are passed for Play only.
    /// The working directory is the document's folder, so relative sample paths in the orchestra
    /// resolve as they would from the command line.
    /// </remarks>
    void Launch(DocumentViewModel doc, string csd, List<string> options, RunMode runMode, HostAudioSettings? host = null)
    {
        AudioStats = "";
        AudioStreamInfo = "";
        ConsoleLines.Clear();
        Tables.Clear();
        OnPropertyChanged(nameof(HasTables));
        doc.ErrorLines.Clear();
        ErrorLinesChanged?.Invoke();

        AddInfo($"{(runMode == RunMode.Play ? Loc.S("Tocando", "Playing") : Loc.S("Renderizando", "Rendering"))} {doc.FileName}   [{string.Join(' ', options)}]");
        // Cabbage instruments: stand-ins for the Cabbage-only opcodes (cabbageGetValue…).
        csd = Cabbage.CabbageSupport.PrepareForCsound(csd, out var cabbageNotes);
        foreach (var note in cabbageNotes)
            AddInfo("Cabbage: " + note);
        lastRunDocument = doc;
        doc.IsPlaying = true;
        // Queue every slider's current value (the engine applies pending controls before its
        // first cycles), so the performance starts where the sliders are, not at the chnk defaults.
        foreach (var ch in doc.Channels)
            ch.Resend();
        // Setting Mode re-evaluates every command's CanExecute ([NotifyCanExecuteChangedFor]).
        Mode = runMode;
        PrepareBreakpoints(doc, runMode, host); // MainViewModel.Debug.cs
        engine.Start(new EngineJob(csd, options, doc.FilePath is null ? null : Path.GetDirectoryName(doc.FilePath), host,
            LiveValues: LiveValuesEnabled && runMode == RunMode.Play, InstrumentNames: NamedInstruments(doc),
            Midi: runMode == RunMode.Play ? midiHub : null,
            CsoundDevice: runMode == RunMode.Play && host is null ? csoundDeviceLabel : null));
    }

    /// <summary>Stops the performance.</summary>
    /// <remarks>Asynchronous: the engine fades out and ends on its thread; <see cref="Mode"/> returns to
    /// None only in <see cref="OnFinished"/>.</remarks>
    [RelayCommand(CanExecute = nameof(CanStop))]
    void Stop() => engine.Stop();

    /// <summary>Sends the live event line to the running performance.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    void SendEvent()
    {
        if (string.IsNullOrWhiteSpace(LiveEvent)) return;
        engine.SendEvent(LiveEvent);
        AddInfo($"→ {LiveEvent}");
    }

    /// <summary>Captures the function tables again (they may change during the performance).</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    void RefreshTables() => engine.RequestTables();

    /// <summary>Empties the console.</summary>
    [RelayCommand]
    void ClearConsole() => ConsoleLines.Clear();

    /// <summary>Shows or hides the right-hand panel.</summary>
    [RelayCommand]
    void ToggleSidePanel() => IsSidePanelVisible = !IsSidePanelVisible;

    /// <summary>Jumps to the CSD line a console message refers to.</summary>
    /// <param name="line">Tapped console line.</param>
    /// <remarks>
    /// A line naming a file (e.g. a written WAV) shows that file in Explorer instead. Line numbers
    /// refer to <see cref="lastRunDocument"/>, which is selected first; nothing happens if that tab
    /// was closed since.
    /// </remarks>
    public void GoToSource(ConsoleLine line)
    {
        if (line.FilePath is { } file)
        {
            ShowInExplorer(file);
            return;
        }
        if (line.SourceLine is not { } n || lastRunDocument is not { } doc || !Documents.Contains(doc))
            return;
        SelectedDocument = doc;
        GoToLineRequested?.Invoke(doc, n);
    }

    /// <summary>
    /// End of a performance or render (posted to the UI thread from <see cref="CsoundEngine.Finished"/>):
    /// flushes the last messages, closes a recording, resets the run state, completes a render and
    /// starts whatever was queued to play next.
    /// </summary>
    /// <param name="code">Csound's result: 0 = finished normally, anything else = error.</param>
    /// <remarks>
    /// The order matters: messages are drained first so the final Csound output appears above the
    /// «Finished» line; <see cref="Mode"/> is reset before the restart / queued-playback calls, which
    /// need <see cref="CanStart"/> to be true again.
    /// </remarks>
    void OnFinished(int code)
    {
        DrainMessages();
        if (IsRecording)
            FinishRecording();
        var finishedMode = Mode;
        if (lastRunDocument is not null)
            lastRunDocument.IsPlaying = false;
        Mode = RunMode.None;
        recompileTimer.Stop();
        pendingRecompile = null;

        if (finishedMode == RunMode.Render && renderTemp is not null && renderTarget is not null)
        {
            try
            {
                // Success: move into place (overwriting; the picker already confirmed it). Failure:
                // discard the partial temp file.
                if (code == 0 && File.Exists(renderTemp))
                {
                    File.Move(renderTemp, renderTarget, overwrite: true);
                    AddInfo(Loc.S($"✓ WAV guardado en {renderTarget}", $"✓ WAV saved to {renderTarget}"));
                }
                else if (File.Exists(renderTemp))
                    File.Delete(renderTemp);
            }
            catch (Exception ex)
            {
                ConsoleLines.Add(new ConsoleLine(Loc.S($"No se pudo guardar el WAV: {ex.Message}", $"Could not save the WAV: {ex.Message}"), MessageKind.Error));
            }
            renderTemp = renderTarget = null;
        }

        AddInfo(code == 0 ? Loc.S("■ Terminado", "■ Finished") : Loc.S($"■ Terminado con error ({code})", $"■ Finished with error ({code})"));
        // A stalled output device ends the run: retry once on the Windows default output.
        TryRestartOnDefaultOutput(finishedMode);
        StartQueuedPlayback();
    }

    /// <summary>Called by the window on every animation frame: drains console messages and updates the clock.</summary>
    /// <remarks>
    /// ~60 times per second on the UI thread. Besides the console it processes the MIDI input queued
    /// by the MIDI callbacks (<c>ProcessMidi</c>, MainViewModel.Midi.cs), the breakpoint state and, while running, the score
    /// clock (mm:ss.d), audio statistics, watched variables, the recording clock and the engine
    /// summary. Values are polled from the engine rather than pushed, so the performance thread
    /// never waits on the UI.
    /// </remarks>
    public void Tick()
    {
        DrainMessages();
        ProcessMidi();
        DebugTick(); // breakpoints (MainViewModel.Debug.cs)
        if (!IsRunning)
        {
            // Idle: hide the live-input and DSP meters (checked first to avoid needless notifications).
            if (IsInputMeterVisible)
                IsInputMeterVisible = false;
            if (IsLoadMeterVisible)
            {
                IsLoadMeterVisible = false;
                DspPeakPercent = 0;
            }
            return;
        }
        // Score time in seconds → "mm:ss.d" (tenths from the milliseconds).
        var t = TimeSpan.FromSeconds(engine.ScoreTime);
        TimeText = $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}";
        UpdateAudioStats();
        UpdateWatches();
        UpdateRecording();
        EngineInfo = engine.SampleRate > 0
            ? $"{engine.SampleRate:0} Hz · {engine.OutputChannels} ch · ksmps {engine.Ksmps}"
            : "";
    }

    /// <summary>
    /// Moves Csound's queued messages into <see cref="ConsoleLines"/> and records the source lines
    /// of errors in the run document (red marks in the editor).
    /// </summary>
    /// <remarks>
    /// At most 500 lines per call (per frame), so a flood of messages is spread over several
    /// frames instead of freezing the UI; the console is then trimmed to <see cref="MaxConsoleLines"/>.
    /// The engine's queue is a concurrent queue filled by Csound's message callback on the
    /// performance thread.
    /// </remarks>
    void DrainMessages()
    {
        bool errorsChanged = false;
        for (int n = 0; n < 500 && engine.Messages.TryDequeue(out var line); n++)
        {
            ConsoleLines.Add(line);
            if (line.IsError && line.SourceLine is { } l && lastRunDocument is { } doc)
                errorsChanged |= doc.ErrorLines.Add(l);
        }
        while (ConsoleLines.Count > MaxConsoleLines)
            ConsoleLines.RemoveAt(0);
        if (errorsChanged)
            ErrorLinesChanged?.Invoke();
    }

    /// <summary>Appends an app message (Info kind) to the console.</summary>
    /// <param name="text">Already localised text.</param>
    void AddInfo(string text) => ConsoleLines.Add(new ConsoleLine(text, MessageKind.Info));

    /// <summary>
    /// Replaces the TABLES card content with a new capture, keeping the same table selected when it
    /// still exists (so a refresh does not jump back to the first table).
    /// </summary>
    /// <param name="tables">Snapshots taken on the performance thread (copies, safe to read here).</param>
    void ShowTables(IReadOnlyList<TableSnapshot> tables)
    {
        int? keep = SelectedTable?.Number;
        Tables.Clear();
        foreach (var t in tables)
            Tables.Add(t);
        SelectedTable = Tables.FirstOrDefault(t => t.Number == keep) ?? Tables.FirstOrDefault();
        OnPropertyChanged(nameof(HasTables));
    }

    // ------------------------------------------------------------------------------------------
    //  Audio devices and opcode catalog
    // ------------------------------------------------------------------------------------------

    /// <summary>Loads the opcode list, then the audio modules and devices, in the background.</summary>
    /// <remarks>
    /// Started fire-and-forget from the constructor. Querying Csound for its opcode list takes a
    /// noticeable moment, so it runs on the thread pool; the continuation resumes on the UI thread
    /// (the captured synchronisation context), where the documents re-analyse their code with the
    /// full opcode list and the window rebuilds the highlighting.
    /// </remarks>
    async Task LoadCatalogAsync()
    {
        await Task.Run(OpcodeIndex.LoadFromCsound);
        foreach (var doc in Documents)
            doc.InvalidateAnalysis();
        OpcodesLoaded?.Invoke();

        await LoadAudioBackendsAsync();
    }

    /// <summary>Text of the embedded start-up demo in the current UI language (<c>Assets/Demo*.csd</c>).</summary>
    /// <returns>The demo, or <see cref="NewDocumentTemplate"/> if the resource is missing.</returns>
    static string LoadDemo()
    {
        // Same orchestra in both languages; only the comments differ.
        string name = Loc.IsEnglish ? "CsoundVisual.Assets.DemoEnglish.csd" : "CsoundVisual.Assets.Demo.csd";
        using var s = typeof(MainViewModel).Assembly.GetManifestResourceStream(name);
        return s is null ? NewDocumentTemplate : new StreamReader(s).ReadToEnd();
    }

    // ------------------------------------------------------------------------------------------
    //  IScrubHost: hover help and drag-to-change in the editor
    //  (ScrubController in the view calls these while the pointer hovers or drags a token)
    // ------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public TokenInsight? Inspect(int offset) =>
        SelectedDocument is { } doc ? doc.Analyzer.Inspect(offset, doc.ChannelState) : null;

    /// <inheritdoc/>
    public void SetChannelValue(string channel, double value)
    {
        if (SelectedDocument?.FindChannel(channel) is { } slider)
            slider.Value = value;       // the slider forwards it to the engine when playing
        else if (SelectedDocument?.IsPlaying == true)
            engine.SetControl(channel, value);
    }

    /// <inheritdoc/>
    public void LiteralScrubbed(TokenInsight token)
    {
        if (SelectedDocument is not { } doc)
            return;

        // The default value of a chnk declaration drives its channel directly.
        if (token.Channel is { } channel && token.Value is { } v)
            SetChannelValue(channel, v);

        // A literal inside an instrument: recompile that instrument into the running engine.
        // Throttled by recompileTimer (120 ms): a drag produces many values per second, but only
        // the latest text is compiled when the timer fires (FlushRecompile).
        if (token.Block is { } block && doc.IsPlaying && Mode == RunMode.Play)
        {
            pendingRecompile = (doc, block.StartLine);
            if (!recompileTimer.IsEnabled)
                recompileTimer.Start();
        }
    }

    /// <summary>Compiles the latest version of the instrument being edited by a drag.</summary>
    /// <remarks>
    /// One-shot per request: the timer is stopped here and restarted by the next
    /// <see cref="LiteralScrubbed"/>. The block is found again by its start line in the current
    /// analysis because the text has changed since the request; if the performance stopped meanwhile
    /// nothing is compiled.
    /// </remarks>
    void FlushRecompile()
    {
        recompileTimer.Stop();
        if (pendingRecompile is not { } p || !p.Doc.IsPlaying)
            return;
        pendingRecompile = null;
        var analyzer = p.Doc.Analyzer;
        if (analyzer.Blocks.FirstOrDefault(b => b.StartLine == p.StartLine) is { } block)
            CompileBlockLive(p.Doc, block); // keeps editor line numbers (breakpoints, error lines)
    }
}
