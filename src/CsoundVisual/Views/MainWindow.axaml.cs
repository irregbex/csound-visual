using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Rendering;
using CsoundVisual.Editor;
using CsoundVisual.Engine;
using CsoundVisual.Localization;
using CsoundVisual.ViewModels;
using CsoundVisual.Theming;

namespace CsoundVisual.Views;

/// <summary>
/// Code-behind of the main window. Holds everything that is purely view-related: editor setup
/// and highlighting, the hover/drag controller, the 60 fps meter/scope refresh, responsive
/// layout, file pickers, the save prompt and the app-drawn title bar.
/// </summary>
/// <remarks>
/// <para>
/// <b>MVVM split.</b> The layout lives in <c>MainWindow.axaml</c> and binds to
/// <see cref="MainViewModel"/>; everything the view model must not know about (controls, timers,
/// dialogs, the clipboard, other windows) is here. The view model asks for UI through delegate
/// properties that this class fills in <see cref="OnDataContextChanged"/>
/// (<c>PickOpenFile</c>, <c>ShowPianoRoll</c>, <c>ConfirmSave</c>…) and raises plain events
/// (<c>GoToLineRequested</c>, <c>Evaluated</c>, <c>ExternalNote</c>…) that this class turns into
/// editor or control actions.
/// </para>
/// <para>
/// <b>Threading.</b> Everything here runs on the Avalonia UI thread. Data produced on other threads
/// (Csound performance thread, WASAPI capture, MIDI callbacks) is never pushed into the controls
/// directly: <see cref="OnFrame"/> pulls it at ~60 fps from lock-free buffers in
/// <see cref="CsoundEngine"/>, and view-model events that may come from elsewhere are re-posted with
/// <see cref="Dispatcher.UIThread"/>.
/// </para>
/// <para>
/// <b>Secondary windows</b> (3D, settings, examples, piano roll, step sequencer, plug-in chain, Paint
/// Rooms) are single-instance: each has a field that is set when it opens and cleared on
/// <c>Closed</c>, so a second request just activates the open one. All but the 3D window
/// are owned by this window (<c>Show(this)</c>): they stay above it and close with it.
/// </para>
/// <para>The breakpoint gutter and debugger UI are in the other part, <c>MainWindow.Breakpoints.cs</c>.</para>
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>
    /// Window width below which the toolbar shows icons only. Measured, not fixed: whenever the
    /// labelled toolbar is laid out, the width it needs (toolbar + title + status pill + margins)
    /// becomes the threshold, so adding buttons or switching language never lets the toolbar
    /// slide under the status pill. Starts with a safe estimate.
    /// </summary>
    /// <remarks>
    /// Five levels (0–4), each used only when the previous one does not fit: 0 = labels, 1 = icons only,
    /// 2 = less used buttons folded into «⋯», 3 = status pill shortened to the clock, 4 = no status pill
    /// and the 3D button folded too. The width each
    /// level needs is measured while it is shown (0 = not measured yet = assume it fits).
    /// </remarks>
    readonly double[] levelWidth = [1900, 0, 0, 0, 0];

    /// <summary>Current toolbar level (see <see cref="levelWidth"/>).</summary>
    int toolbarLevel;

    /// <summary>Ableton Link status button in the title bar (visible while Link is on).</summary>
    LinkStatusView? linkIndicator;

    /// <summary>True while the less used toolbar buttons are folded into the «⋯» menu.</summary>
    bool overflowMode;

    /// <summary>Below this width the side panel is hidden automatically (once per transition).</summary>
    const double NarrowWidth = 900;

    /// <summary>Width of the side panel when shown.</summary>
    const double SidePanelWidth = 360;

    /// <summary>File-type filter of the open/save pickers for Csound sources (.csd, .orc, .sco, .udo).</summary>
    static readonly FilePickerFileType CsoundFiles = new("Csound") { Patterns = ["*.csd", "*.orc", "*.sco", "*.udo"] };

    /// <summary>File-type filter of the «Render to WAV» save picker.</summary>
    static readonly FilePickerFileType WavFiles = new("WAV") { Patterns = ["*.wav"] };

    /// <summary>Reused per frame: per-channel output peaks (linear 0…1) taken from the engine.</summary>
    readonly float[] peakBuffer = new float[CsoundEngine.MaxChannels];

    /// <summary>Reused per frame: per-channel live-input peaks (linear 0…1), capture side.</summary>
    readonly float[] inputPeakBuffer = new float[Audio.WasapiInput.MaxMeterChannels];

    /// <summary>Reused per frame: the latest <see cref="CsoundEngine.ScopeSize"/> output samples
    /// (oldest → newest) for the oscilloscope and the spectrum.</summary>
    readonly float[] scopeBuffer = new float[CsoundEngine.ScopeSize];

    /// <summary>
    /// Last <see cref="MainViewModel.DawMetersVersion"/> pushed to the scope: the DAW plug-in sends a
    /// new scope block less often than 60 fps, so the same block is not pushed (and re-analysed) twice.
    /// </summary>
    int lastDawMeters = -1;

    /// <summary>16 ms (~60 fps) render-priority timer driving <see cref="OnFrame"/>; started when the window opens.</summary>
    readonly DispatcherTimer frameTimer;

    /// <summary>450 ms debounce after the last keystroke before the channel sliders are re-detected.</summary>
    readonly DispatcherTimer channelDebounce;

    /// <summary>Hover help and drag-to-change of numbers/variables in the editor (draws on <c>Overlay</c>).</summary>
    ScrubController? scrubber;

    /// <summary>Completion popup of the editor (opcodes with signatures, variables, keywords); also forced with Ctrl+Space.</summary>
    CompletionProvider? completion;

    /// <summary>Open 3D window, or null. The only secondary window not owned by this one (it may live on another monitor in full screen).</summary>
    Visual3DWindow? visual3D;

    /// <summary>Open Settings window, or null.</summary>
    SettingsWindow? settingsWindow;

    /// <summary>Open example browser, or null.</summary>
    ExamplesWindow? examplesWindow;

    /// <summary>Open piano roll, or null.</summary>
    PianoRollWindow? pianoRoll;

    /// <summary>Open step sequencer, or null.</summary>
    StepSequencerWindow? sequencer;

    /// <summary>Open VST3 plug-in chain window, or null.</summary>
    PluginChainWindow? pluginChainWindow;

    /// <summary>Open Paint Rooms window, or null.</summary>
    PaintRoomsWindow? paintRooms;

    /// <summary>
    /// Editor background renderer that briefly highlights the lines just evaluated with Ctrl+Enter;
    /// its opacity fades from 1 to 0 driven by <see cref="flashTimer"/>.
    /// </summary>
    readonly FlashRenderer flash = new();

    /// <summary>30 ms timer fading <see cref="flash"/> (−0.06 per tick, ≈0.5 s in total); stops itself at 0.</summary>
    readonly DispatcherTimer flashTimer;

    /// <summary>Whether the window was below <see cref="NarrowWidth"/> at the last layout, so the side
    /// panel is auto-hidden/shown only on the transition, not on every resize.</summary>
    bool wasNarrow;

    /// <summary>Set once unsaved documents are resolved: the next <see cref="Window.Closing"/> really closes.</summary>
    bool allowClose;

    /// <summary>True while the "save changes?" prompt of <see cref="OnClosing"/> is open.</summary>
    bool closePromptOpen;

    /// <summary>The view model (the window's DataContext), or null before it is attached.</summary>
    MainViewModel? vm;

    /// <summary>Creates the window. The view model is attached through <see cref="StyledElement.DataContext"/>.</summary>
    /// <remarks>
    /// Only wiring that does not need the view model happens here (editor options, timers, input
    /// handlers, the responsive-layout measurement). Handlers that need it read the <see cref="vm"/>
    /// field at run time and do nothing while it is null; the view-model-specific wiring is in
    /// <see cref="OnDataContextChanged"/>. App.axaml.cs creates the window with
    /// <c>new MainWindow { DataContext = vm }</c>, i.e. the DataContext arrives right after this constructor.
    /// </remarks>
    public MainWindow()
    {
        InitializeComponent();
        SetupEditor();
        SetupBreakpoints(); // MainWindow.Breakpoints.cs

        // Render priority: the meters are refreshed together with the frame being composed.
        frameTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => OnFrame());
        // Channel sliders are re-detected 450 ms after the user stops typing.
        channelDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(450), DispatcherPriority.Background, (_, _) =>
        {
            channelDebounce!.Stop();
            vm?.SelectedDocument?.RefreshChannels();
        });

        // Fade of the "just evaluated" highlight: 1 → 0 in steps of 0.06 every 30 ms (≈0.5 s).
        flashTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, (_, _) =>
        {
            flash.Opacity -= 0.06;
            if (flash.Opacity <= 0) { flash.Opacity = 0; flashTimer!.Stop(); }
            Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        });
        Editor.TextArea.TextView.BackgroundRenderers.Add(flash);
        completion = new CompletionProvider(Editor);
        // Tunnel: the window sees Ctrl+Enter / Ctrl+Space / F3 before AvaloniaEdit would insert a
        // new line or a space, so the shortcut can mark the event handled.
        Editor.TextArea.AddHandler(KeyDownEvent, OnEditorKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Restart the debounce on every edit: sliders follow the code once typing pauses.
        Editor.TextChanged += (_, _) =>
        {
            channelDebounce.Stop();
            channelDebounce.Start();
        };
        // LIVE EVENT card: Enter in the text box sends the line like the button does.
        EventBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && vm?.SendEventCommand.CanExecute(null) == true)
                vm.SendEventCommand.Execute(null);
        };
        // Console: tapping a line that names a source line jumps there (e.Source is the TextBlock
        // inside the item; its DataContext is the ConsoleLine).
        ConsoleList.Tapped += (_, e) =>
        {
            if (e.Source is StyledElement { DataContext: ConsoleLine line } && vm is not null)
                vm.GoToSource(line);
        };
        SetupConsoleCopy();
        SetupEditorUndoButtons();
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
        // Watch cards: a click (not on the ✕ button) opens the enlarged view; clicking the dimmed
        // background or Esc closes it.
        WatchList.AddHandler(TappedEvent, (_, e) =>
        {
            if (vm is null || e.Source is not Avalonia.Visual source) return;
            if (source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
            if (source.FindAncestorOfType<Grid>(includeSelf: true)?.DataContext is WatchItemViewModel item)
                vm.FocusedWatch = item;
        });
        WatchDim.Tapped += (_, _) => vm?.CloseFocusedWatchCommand.Execute(null);
        // Esc closes the enlarged watch; other Esc uses (editor scrub undo, dialogs) are unaffected
        // because the key is only consumed while the overlay is shown.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && vm is { IsWatchFocused: true })
            {
                vm.FocusedWatch = null;
                e.Handled = true;
            }
        };
        // Re-measure what the title bar needs at the current level whenever it is laid out. Deferred
        // until the layout pass is over (the parts resize one after the other), and the toolbar's
        // width is the sum of its buttons' desired widths: when it does not fit, its own bounds are
        // clipped to the column and would understate it.
        // measurePending coalesces the many SizeChanged/LayoutUpdated notifications of one layout
        // pass into a single Background-priority measurement (which runs after layout and render).
        bool measurePending = false;
        void Measure()
        {
            if (vm is null || measurePending) return;
            measurePending = true;
            Dispatcher.UIThread.Post(() =>
            {
                measurePending = false;
                var shown = Toolbar.Children.Where(c => c.IsVisible).ToList();
                double toolbar = shown.Sum(c => c.DesiredSize.Width) + Toolbar.Spacing * Math.Max(0, shown.Count - 1);
                // 14 + 148 = the title grid's side margins; 40 = breathing room on both sides.
                double needed = toolbar + TitleLeft.DesiredSize.Width + TitleRight.DesiredSize.Width + 14 + 148 + 40;
                // The 1 px tolerance avoids a feedback loop: applying a level changes the layout,
                // which triggers another measurement that must settle.
                if (Math.Abs(needed - levelWidth[toolbarLevel]) > 1)
                {
                    levelWidth[toolbarLevel] = needed;
                    ApplyResponsiveLayout(Bounds.Width);
                }
            }, DispatcherPriority.Background);
        }
        Toolbar.SizeChanged += (_, _) => Measure();
        TitleRight.SizeChanged += (_, _) => Measure();
        Toolbar.LayoutUpdated += (_, _) => Measure();
        MoreButton.Click += (_, _) => ShowMoreMenu();
        // Ableton Link indicator: created once, inserted at the left of the title bar's right group,
        // opted out of the drag area (ElementRole User) so it is clickable, and re-laid-out when
        // Link is switched on/off (its presence changes the width the title bar needs).
        // Caution: Avalonia raises DataContextChanged inside base.OnDataContextChanged, which the
        // override below calls BEFORE it assigns the vm field; on the first DataContext this handler
        // therefore still sees vm == null and returns without creating the indicator.
        DataContextChanged += (_, _) =>
        {
            if (vm is null || linkIndicator is not null) return;
            linkIndicator = LinkStatusView.For(vm);
            linkIndicator.IsVisible = vm.IsLinkEnabled;
            Avalonia.Controls.Chrome.WindowDecorationProperties.SetElementRole(linkIndicator, Avalonia.Input.WindowDecorationsElementRole.User);
            TitleRight.Children.Insert(0, linkIndicator);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsLinkEnabled))
                    ApplyResponsiveLayout(Bounds.Width);
            };
        };
        // Bounds are restored on Opened (screens are known only once the platform window exists);
        // the drawn title text is hidden after the Loaded pass, when Avalonia's title bar exists,
        // and again after every maximize/restore because the title bar is rebuilt.
        Opened += (_, _) =>
        {
            RestoreWindowBounds();
            frameTimer.Start();
            Dispatcher.UIThread.Post(HideDrawnTitle, DispatcherPriority.Loaded);
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
                Dispatcher.UIThread.Post(HideDrawnTitle, DispatcherPriority.Loaded);
        };
        Closing += OnClosing;
    }

    /// <summary>
    /// Connects the view to a new <see cref="MainViewModel"/>: subscribes to its property changes,
    /// fills the view model's UI-service delegates (file pickers, dialogs, the "show window X"
    /// actions) and its events (go-to-line, evaluation flash, MIDI notes, opcode list loaded).
    /// </summary>
    /// <param name="e">Unused event data.</param>
    /// <remarks>
    /// In practice this runs once, right after construction. Only the
    /// <see cref="OnViewModelPropertyChanged"/> subscription is undone for a previous view model;
    /// the lambdas attached here are not, so attaching a second view model would duplicate them
    /// (e.g. <c>DawButton.Click</c>).
    /// </remarks>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (vm is not null)
            vm.PropertyChanged -= OnViewModelPropertyChanged;

        vm = DataContext as MainViewModel;
        if (vm is null) return;

        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.PickOpenFile = PickOpenFileAsync;
        vm.PickSaveFile = PickSaveFileAsync;
        DawButton.Click += (_, _) => ShowDawMenu(DawButton);
        // Single-instance secondary windows: activate the open one, or create, remember, forget on
        // close and show it owned by this window. The same pattern repeats below.
        vm.ShowPianoRoll = () =>
        {
            if (pianoRoll is { } open) { open.Activate(); return; }
            pianoRoll = new PianoRollWindow(vm) { Icon = Icon };
            pianoRoll.Closed += (_, _) => pianoRoll = null;
            pianoRoll.Show(this);
        };
        vm.ShowStepSequencer = () =>
        {
            if (sequencer is { } open) { open.Activate(); return; }
            sequencer = new StepSequencerWindow(vm) { Icon = Icon };
            sequencer.Closed += (_, _) => sequencer = null;
            sequencer.Show(this);
        };
        // Paint Rooms (Ctrl+Shift+I): compose and design sound by painting images (Views/PaintRoomsWindow.cs).
        vm.ShowPaintRooms = () =>
        {
            if (paintRooms is { } open) { open.Activate(); return; }
            paintRooms = new PaintRoomsWindow(vm) { Icon = Icon };
            paintRooms.Closed += (_, _) => paintRooms = null;
            paintRooms.Show(this);
        };
        vm.ShowPluginChain = () =>
        {
            if (pluginChainWindow is { } open) { open.Activate(); return; }
            pluginChainWindow = new PluginChainWindow(vm) { Icon = Icon };
            pluginChainWindow.Closed += (_, _) => pluginChainWindow = null;
            pluginChainWindow.Show(this);
        };
        // VST3 chain: hook into the engine and restore the saved plug-ins once the window is up.
        if (IsVisible) vm.InitPluginHost(); else Opened += (_, _) => vm?.InitPluginHost();
        vm.ShowExamples = () =>
        {
            if (examplesWindow is { } open) { open.Activate(); return; }
            examplesWindow = new ExamplesWindow(vm) { Icon = Icon };
            examplesWindow.Closed += (_, _) => examplesWindow = null;
            examplesWindow.Show(this);
        };
        vm.ShowSettings = () =>
        {
            if (settingsWindow is { } open) { open.Activate(); return; }
            settingsWindow = new SettingsWindow(vm) { Icon = Icon };
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show(this);
        };
        vm.ShowVisual3D = () =>
        {
            if (visual3D is { } open) { open.Activate(); return; }
            visual3D = new Visual3DWindow(vm) { Icon = Icon };
            visual3D.Closed += (_, _) => visual3D = null;
            // Not owned (an owned window always stays above its owner), so the 3D view can be
            // arranged independently, e.g. full screen on another monitor. Because it is not owned
            // it does not close with this window: OnClosing closes it explicitly.
            visual3D.Show();
        };
        vm.ShowReport = (title, text) => new InfoDialog(title, text) { Icon = Icon }.ShowDialog(this);
        vm.ConfirmSave = doc => new ConfirmSaveDialog(doc.FileName) { Icon = Icon }.ShowDialog<SaveChoice>(this);
        vm.ConsoleLines.CollectionChanged += (_, _) => ScrollConsoleToEnd();
        // Posted at Background priority so the tab switch (if any) and the new document's layout
        // happen first; GoToLine then ignores the request if another tab got selected meanwhile.
        vm.GoToLineRequested += (doc, line) => Dispatcher.UIThread.Post(() => GoToLine(doc, line), DispatcherPriority.Background);
        vm.ErrorLinesChanged += () => Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        // The opcode list is loaded in the background: once known, rebuild the highlighting with
        // the full opcode rule (see LoadHighlighting). Posted because the event may come off the UI thread.
        vm.OpcodesLoaded += () => Dispatcher.UIThread.Post(() => Editor.SyntaxHighlighting = LoadHighlighting());

        scrubber = new ScrubController(Editor, Overlay, vm);

        // Ctrl+Enter evaluated lines first..last: flash them and start the fade.
        vm.Evaluated += (first, last) =>
        {
            flash.FirstLine = first;
            flash.LastLine = last;
            flash.Opacity = 1;
            flashTimer.Start();
        };

        // On-screen keyboard ↔ MIDI hub.
        Piano.NoteOn += vm.KeyboardNoteOn;
        Piano.NoteOff += vm.KeyboardNoteOff;
        vm.ExternalNote += (note, on) => Piano.SetExternal(note, on);
    }

    // ------------------------------------------------------------------------------------------
    //  Editor
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// One-time configuration of the AvaloniaEdit <c>Editor</c>: highlighting, indentation (2
    /// spaces, tabs converted), theme colours for the current line / selection / caret, Ctrl+wheel
    /// zoom, the background renderers (error lines, word occurrences) and the status-bar cursor read-out.
    /// </summary>
    /// <remarks>
    /// Runs from the constructor, before any view model exists; the closures read <see cref="vm"/>
    /// when they fire. The evaluation-flash renderer is added separately in the constructor.
    /// </remarks>
    void SetupEditor()
    {
        Editor.SyntaxHighlighting = LoadHighlighting();
        Editor.Options.HighlightCurrentLine = true;
        Editor.Options.ShowTabs = false;
        Editor.Options.IndentationSize = 2;
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.EnableHyperlinks = false;
        Editor.TextArea.TextView.Margin = new Thickness(10, 0, 0, 0);
        Editor.TextArea.TextView.CurrentLineBackground = AppTheme.Brush("Grid");
        Editor.TextArea.TextView.CurrentLineBorder = new Pen(AppTheme.Brush("Line"));
        Editor.TextArea.SelectionBrush = AppTheme.Brush("Selection");
        Editor.TextArea.Caret.CaretBrush = AppTheme.Brush("AccentHi");
        // AppTheme.Brush returns the theme's shared brush objects, which are recoloured in place
        // on a preset change; only the frozen highlighting definition needs rebuilding.
        AppTheme.Changed += OnThemeChanged;
        // Ctrl+wheel zooms the code (same setting as «Tamaño de letra» in Configuración).
        Editor.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (vm is null || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            vm.EditorFontSize += e.Delta.Y > 0 ? 1 : -1;
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Error lines are read lazily through a delegate, so the renderer follows tab switches
        // without being re-created.
        Editor.TextArea.TextView.BackgroundRenderers.Add(
            new ErrorLineRenderer(() => (IReadOnlyCollection<int>?)vm?.SelectedDocument?.ErrorLines ?? []));
        Editor.TextArea.TextView.BackgroundRenderers.Add(occurrences);
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCursorStatus();
        // Edits move the occurrences: forget them and search again around the caret.
        Editor.TextChanged += (_, _) => { occurrences.Invalidate(); UpdateCursorStatus(); };
        Editor.DocumentChanged += (_, _) => { occurrences.Invalidate(); UpdateCursorStatus(); };
    }

    /// <summary>
    /// A new colour preset was applied: the syntax definition is frozen once loaded, so it is
    /// rebuilt with the new colours; every control is repainted (custom controls draw with the
    /// theme's brushes, which already hold the new colours).
    /// </summary>
    void OnThemeChanged()
    {
        Editor.SyntaxHighlighting = LoadHighlighting();
        Editor.TextArea.TextView.Redraw();
        foreach (var visual in this.GetVisualDescendants())
            visual.InvalidateVisual();
    }

    /// <summary>
    /// Loads the embedded Csound highlighting definition. Once the real opcode list is known, an
    /// extra rule colouring every opcode name is inserted right after the built-in opcode list,
    /// so it wins over the variable-prefix rules (otherwise <c>init</c> would look like an
    /// i-variable).
    /// </summary>
    /// <returns>The highlighting definition, or null if the embedded resource is missing.</returns>
    /// <remarks>
    /// Internal and static because the preview editor of <see cref="ExamplesWindow"/> reuses it. Rebuilt on
    /// every theme change and when the opcode list arrives, never cached: AvaloniaEdit freezes a
    /// loaded definition, so its colours cannot be changed afterwards.
    /// </remarks>
    internal static IHighlightingDefinition? LoadHighlighting()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("CsoundVisual.Editor.Csound.xshd");
        if (stream is null)
            return null;
        // The colours in Csound.xshd are placeholders: each <Color name="X" foreground="…"> takes the
        // active preset's «SynX» colour.
        string xshd = new StreamReader(stream).ReadToEnd();
        // Group 1 keeps the attribute prefix, group 2 is the colour name; Avalonia's Color.ToString()
        // gives "#aarrggbb", and the opaque "#ff" prefix is cut to the "#rrggbb" form the XSHD expects.
        xshd = Regex.Replace(xshd, @"(<Color name=""(\w+)""\s+foreground="")#[0-9A-Fa-f]+",
            m => m.Groups[1].Value + AppTheme.ColorOf("Syn" + m.Groups[2].Value).ToString().Replace("#ff", "#", StringComparison.OrdinalIgnoreCase));
        using var reader = XmlReader.Create(new StringReader(xshd));
        var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);

        if (OpcodeIndex.IsLoaded && definition.GetNamedColor("Opcode") is { } opcodeColor)
        {
            // Longest names first so the alternation prefers e.g. "oscili" over "osc" (\b alone
            // already prevents partial words; the order just keeps the regex unambiguous).
            var names = OpcodeIndex.Names.OrderByDescending(n => n.Length).Select(Regex.Escape);
            var rule = new HighlightingRule
            {
                Regex = new Regex(@"\b(?:" + string.Join("|", names) + @")\b", RegexOptions.Compiled | RegexOptions.CultureInvariant),
                Color = opcodeColor,
            };
            var rules = definition.MainRuleSet.Rules;
            // Rules are tried in order; inserting right after the XSHD's own opcode rule puts the new
            // rule ahead of the variable-prefix rules that come later (or first, if none is found).
            int builtIn = rules.ToList().FindIndex(r => r.Color == opcodeColor);
            rules.Insert(builtIn >= 0 ? builtIn + 1 : 0, rule);
        }
        return definition;
    }

    /// <summary>
    /// Live-coding shortcuts in the editor: Ctrl+Enter evaluates the selection or the instrument
    /// under the caret, Ctrl+Shift+Enter sends score lines as events, Ctrl+Space opens completion,
    /// F3 / Shift+F3 jump between occurrences of the word under the caret.
    /// </summary>
    /// <param name="sender">The editor's text area (tunnel handler).</param>
    /// <param name="e">Key data; marked handled when a shortcut is consumed.</param>
    /// <remarks>
    /// For evaluation the selected line range (1-based, 0 = no selection) is passed along so the
    /// view model can report exactly which lines were evaluated (they are flashed via <c>Evaluated</c>).
    /// </remarks>
    void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (vm is null)
            return;
        if (e.Key == Key.F3)
        {
            JumpToOccurrence(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : +1);
            e.Handled = true;
            return;
        }
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;
        var area = Editor.TextArea;
        if (e.Key == Key.Enter)
        {
            string selection = area.Selection.IsEmpty ? "" : area.Selection.GetText();
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                vm.SendScoreLines(area.Caret.Offset, selection);
            else
                vm.EvaluateCode(area.Caret.Offset, selection,
                    area.Selection.IsEmpty ? 0 : area.Selection.StartPosition.Line,
                    area.Selection.IsEmpty ? 0 : area.Selection.EndPosition.Line);
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            completion?.Show();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Background renderer boxing every occurrence of the word under the caret; also the source of
    /// the occurrence list used by <see cref="UpdateCursorStatus"/> and <see cref="JumpToOccurrence"/>.
    /// </summary>
    readonly WordOccurrenceRenderer occurrences = new();

    /// <summary>
    /// Status bar «Ln, Col» plus the word under the caret: how many times it appears in this file
    /// and in the other open tabs (so a variable like ishift can be traced through the project).
    /// Also refreshes the occurrence highlight.
    /// </summary>
    void UpdateCursorStatus()
    {
        if (vm is null) return;
        var caret = Editor.TextArea.Caret;
        if (Editor.Document is { } document)
            occurrences.Update(document, caret.Offset);
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        string status = $"Ln {caret.Line}, Col {caret.Column}";
        if (occurrences.Word is { } word && occurrences.Offsets.Count > 0)
        {
            int others = vm.Documents.Where(d => d != vm.SelectedDocument)
                                     .Sum(d => WordOccurrenceRenderer.FindAll(d.Document.Text, word).Count());
            status += Loc.S($"   ·   «{word}» ×{occurrences.Offsets.Count}", $"   ·   «{word}» ×{occurrences.Offsets.Count}");
            if (others > 0)
                status += Loc.S($" (+{others} en otras pestañas)", $" (+{others} in other tabs)");
            if (occurrences.Offsets.Count > 1)
                status += "   F3 ▸";
        }
        vm.CursorText = status;
    }

    /// <summary>F3 / Shift+F3: moves the caret to the next / previous occurrence of the highlighted word.</summary>
    void JumpToOccurrence(int direction)
    {
        if (occurrences.Word is null || occurrences.Offsets.Count < 2) return;
        int caret = Editor.TextArea.Caret.Offset;
        var list = occurrences.Offsets;
        // Offsets are sorted. Forward: first start after the caret; backward: last occurrence that
        // ends before the caret (so the one the caret is in is skipped). Both wrap around.
        int target = direction > 0
            ? list.FirstOrDefault(o => o > caret, list[0])
            : list.LastOrDefault(o => o + occurrences.Word.Length < caret, list[^1]);
        Editor.TextArea.Caret.Offset = target;
        Editor.TextArea.Selection = AvaloniaEdit.Editing.Selection.Create(Editor.TextArea, target, target + occurrences.Word.Length);
        Editor.TextArea.Caret.BringCaretToView();
    }

    /// <summary>Folds (or unfolds) the less used toolbar buttons into the «⋯» menu (toolbar level ≥ 2).</summary>
    /// <param name="on">True to hide those buttons and show <c>MoreButton</c>.</param>
    void SetOverflow(bool on)
    {
        if (on == overflowMode) return;
        overflowMode = on;
        foreach (var b in new Control[] { NewButton, OpenButton, SaveAsButton, ExamplesButton, PianoButton, StepsButton, PaintButton, PluginsButton, RenderButton, DawButton })
            b.IsVisible = !on;
        MoreButton.IsVisible = on;
    }

    /// <summary>«⋯» menu of the folded toolbar buttons, with their shortcuts.</summary>
    /// <remarks>
    /// Built fresh on every click, so labels follow the current language and the optional entries
    /// (3D when its button is hidden at level 4, Ableton Link when available) are current. The
    /// <c>InputGesture</c> of each item only displays the shortcut; the real key bindings are in the AXAML.
    /// </remarks>
    void ShowMoreMenu()
    {
        if (vm is null) return;
        var menu = new MenuFlyout();
        void Add(string text, KeyGesture? gesture, Action action)
        {
            var item = new MenuItem { Header = text, InputGesture = gesture };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        var ctrl = KeyModifiers.Control;
        Add(Loc.Instance["MW002"], new KeyGesture(Key.N, ctrl), () => vm.NewCommand.Execute(null));
        Add(Loc.Instance["MW004"], new KeyGesture(Key.O, ctrl), () => vm.OpenCommand.Execute(null));
        Add(Loc.Instance["Toolbar.SaveAs"], new KeyGesture(Key.S, ctrl | KeyModifiers.Shift), () => vm.SaveAsCommand.Execute(null));
        menu.Items.Add(new Separator());
        Add(Loc.Instance["Toolbar.Examples"], new KeyGesture(Key.E, ctrl), () => vm.OpenExamplesBrowserCommand.Execute(null));
        if (!View3DButton.IsVisible)
            Add("3D", new KeyGesture(Key.D3, ctrl), () => vm.Open3DCommand.Execute(null));
        Add(Loc.Instance["Tools.PianoRoll"], new KeyGesture(Key.P, ctrl), () => vm.OpenPianoRollCommand.Execute(null));
        Add(Loc.Instance["Tools.Sequencer"], new KeyGesture(Key.P, ctrl | KeyModifiers.Shift), () => vm.OpenStepSequencerCommand.Execute(null));
        Add(Loc.Instance["Toolbar.Paint"], new KeyGesture(Key.I, ctrl | KeyModifiers.Shift), () => vm.OpenPaintRoomsCommand.Execute(null));
        Add(Loc.S("Plugins VST3", "VST3 plug-ins"), new KeyGesture(Key.V, ctrl | KeyModifiers.Shift), () => vm.OpenPluginChainCommand.Execute(null));
        if (vm.IsLinkAvailable)
            Add(vm.IsLinkEnabled ? Loc.S("Desactivar Ableton Link", "Turn Ableton Link off") : Loc.S("Activar Ableton Link", "Turn Ableton Link on"), null, () => vm.ToggleLinkCommand.Execute(null));
        menu.Items.Add(new Separator());
        Add(Loc.Instance["MW018"], new KeyGesture(Key.R, ctrl), () => vm.RenderCommand.Execute(null));
        // Posted: the DAW sub-menu opens after this flyout has closed, anchored on the same button.
        Add(Loc.Instance["Daw.Button"] + " …", null, () => Dispatcher.UIThread.Post(() => ShowDawMenu(MoreButton)));
        menu.ShowAt(MoreButton);
    }

    /// <summary>
    /// «Enviar al DAW» menu: one entry per running «Csound Visual» VST3 instance (track · orchestra),
    /// plus «unlink». With no instance it explains how to add the plugin in Cubase.
    /// </summary>
    /// <param name="anchor">Control the flyout opens at (the DAW button, or «⋯» when it is folded).</param>
    /// <remarks>The instance list is discovered on each opening (<c>FindDawTargets</c>), so it is always current.</remarks>
    void ShowDawMenu(Control anchor)
    {
        if (vm is null) return;
        var menu = new MenuFlyout();
        var targets = vm.FindDawTargets();
        if (targets.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = Loc.S("No hay ningún plugin «Csound Visual» abierto. En Cubase: agrega «Csound Visual Instrument» (pista de instrumento) o «Csound Visual FX» (inserto) y vuelve a intentar.",
                               "No «Csound Visual» plugin is open. In Cubase: add «Csound Visual Instrument» (instrument track) or «Csound Visual FX» (insert) and try again."),
                IsEnabled = false,
            });
        }
        foreach (var target in targets)
        {
            var item = new MenuItem { Header = "⇢ " + target.Display };
            item.Click += async (_, _) => await vm.SendToDawAsync(target);
            menu.Items.Add(item);
        }
        if (vm.LinkedDaw is not null)
        {
            menu.Items.Add(new Separator());
            var unlink = new MenuItem { Header = Loc.S("Desvincular sliders del DAW", "Unlink sliders from the DAW") };
            unlink.Click += (_, _) => vm.UnlinkDaw();
            menu.Items.Add(unlink);
        }
        menu.ShowAt(anchor);
    }

    /// <summary>
    /// Editor Undo / Redo arrow buttons next to the tabs. They act on the AvaloniaEdit editor (the
    /// shown document's own undo stack: every tab keeps its history) and are greyed out when there is
    /// nothing to undo / redo.
    /// </summary>
    /// <remarks>
    /// The enabled state follows <c>UndoStack.CanUndo/CanRedo</c> (INotifyPropertyChanged) of the
    /// current document; the subscription moves when the editor shows another document
    /// (<c>TextEditor.DocumentChanged</c>). After a click the focus goes back to the editor so typing
    /// continues where it was.
    /// </remarks>
    void SetupEditorUndoButtons()
    {
        AvaloniaEdit.Document.UndoStack? watched = null;
        void Update()
        {
            EditorUndoButton.IsEnabled = Editor.CanUndo;
            EditorRedoButton.IsEnabled = Editor.CanRedo;
        }
        void OnStack(object? s, PropertyChangedEventArgs e) => Update();
        void Rewire()
        {
            if (watched is not null) watched.PropertyChanged -= OnStack;
            watched = Editor.Document?.UndoStack;
            if (watched is not null) watched.PropertyChanged += OnStack;
            Update();
        }
        Editor.DocumentChanged += (_, _) => Rewire();
        EditorUndoButton.Click += (_, _) => { if (Editor.CanUndo) Editor.Undo(); Editor.Focus(); };
        EditorRedoButton.Click += (_, _) => { if (Editor.CanRedo) Editor.Redo(); Editor.Focus(); };
        Rewire();
        if (Environment.GetEnvironmentVariable("CSV_UNDO_TEST") is { Length: > 0 } logPath)
            Dispatcher.UIThread.Post(() => RunUndoSelfTest(logPath), DispatcherPriority.ApplicationIdle);
        // Test hook: CSV_IMPORT_PICTURE = picture path → opens Paint Rooms and its import dialog (see
        // PaintImportDialog's CSV_IMPORT_SNAPSHOT), then exits. Never set in normal use.
        if (Environment.GetEnvironmentVariable("CSV_IMPORT_PICTURE") is { Length: > 0 } picturePath)
            Dispatcher.UIThread.Post(async () =>
            {
                if (DataContext is not MainViewModel vm) return;
                vm.ShowPaintRooms?.Invoke();
                await System.Threading.Tasks.Task.Delay(2500);
                if (paintRooms?.Rooms[0] is PaintSynthRoom synth) await synth.ImportPictureAsync(picturePath, alwaysAsk: true);
                Environment.Exit(0);
            }, DispatcherPriority.ApplicationIdle);
        // Test hook: CSV_SELECTION_MENU_TEST = log path → opens Paint Rooms, clicks every room's
        // «Selection ▾» button and logs the menu entries (checks the menu opens without errors).
        if (Environment.GetEnvironmentVariable("CSV_SELECTION_MENU_TEST") is { Length: > 0 } menuLog)
            Dispatcher.UIThread.Post(async () =>
            {
                var log = new System.Text.StringBuilder();
                try
                {
                    if (DataContext is MainViewModel vm) vm.ShowPaintRooms?.Invoke();
                    await System.Threading.Tasks.Task.Delay(2500);
                    var buttons = paintRooms!.GetLogicalDescendants().OfType<Button>()
                        .Where(b => Avalonia.Automation.AutomationProperties.GetName(b) == "SelectionMenu").ToList();
                    log.AppendLine($"Selection buttons found: {buttons.Count}");
                    foreach (var b in buttons)
                    {
                        b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                        await System.Threading.Tasks.Task.Delay(500);
                        if (b.GetValue(Avalonia.Controls.Primitives.FlyoutBase.AttachedFlyoutProperty) is null) { }
                        var popupItems = TopLevel.GetTopLevel(paintRooms)!.GetVisualDescendants().OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList();
                        log.AppendLine($"clicked OK; menu items visible in window tree: {popupItems.Count}");
                    }
                }
                catch (Exception ex) { log.AppendLine("EXCEPTION " + ex); }
                System.IO.File.WriteAllText(menuLog, log.ToString());
                Environment.Exit(0);
            }, DispatcherPriority.ApplicationIdle);
        if (Environment.GetEnvironmentVariable("CSV_SETTINGS_UNDO_TEST") is { Length: > 0 } settingsLog)
            Dispatcher.UIThread.Post(() => RunSettingsUndoSelfTest(settingsLog), DispatcherPriority.ApplicationIdle);
        if (Environment.GetEnvironmentVariable("CSV_PAINT_SCREENSHOT") is { Length: > 0 } shotPath)
            Dispatcher.UIThread.Post(() => RunPaintScreenshot(shotPath), DispatcherPriority.ApplicationIdle);
        if (Environment.GetEnvironmentVariable("CSV_SEQ_SAVE_TEST") is { Length: > 0 } seqLog)
            Dispatcher.UIThread.Post(() => RunSequencerSaveSelfTest(seqLog), DispatcherPriority.ApplicationIdle);
        if (Environment.GetEnvironmentVariable("CSV_PAINT_PLAY_TEST") is { Length: > 0 } playLog)
            Dispatcher.UIThread.Post(() => RunPaintPlaySelfTest(playLog), DispatcherPriority.ApplicationIdle);
        if (Environment.GetEnvironmentVariable("CSV_PAINT_UNDO_TEST") is { Length: > 0 } paintLog)
            Dispatcher.UIThread.Post(() => RunPaintUndoSelfTest(paintLog), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Documentation hook (environment variable <c>CSV_PAINT_SCREENSHOT</c> = PNG path): opens Paint
    /// Rooms on the Image Sequencer tab (<c>CSV_PAINT_TAB</c> = another tab index), renders the
    /// window to the PNG at 1440×880 and exits without saving anything. Never active in normal use.
    /// </summary>
    /// <param name="pngPath">Where to write the screenshot.</param>
    async void RunPaintScreenshot(string pngPath)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;
            vm.ShowPaintRooms?.Invoke();
            await System.Threading.Tasks.Task.Delay(1500);
            var win = paintRooms!;
            win.WindowState = WindowState.Normal;
            win.Width = 1440; win.Height = 880;
            var tabs = win.GetLogicalDescendants().OfType<TabControl>().First();
            tabs.SelectedIndex = int.TryParse(Environment.GetEnvironmentVariable("CSV_PAINT_TAB"), out int t) ? t : 3;
            // Paint Synth: load an example (CSV_PAINT_PRESET = its id, e.g. «landscape») so the canvas is not empty.
            if (Environment.GetEnvironmentVariable("CSV_PAINT_PRESET") is { Length: > 0 } presetId &&
                CsoundVisual.Paint.PaintPresets.All.FirstOrDefault(x => x.Id == presetId) is { } preset)
                typeof(PaintSynthRoom).GetMethod("LoadPreset", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(win.Rooms[0], [preset]);
            await System.Threading.Tasks.Task.Delay(2500);
            var size = new PixelSize((int)win.ClientSize.Width, (int)win.ClientSize.Height);
            using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
            rtb.Render(win);
            rtb.Save(pngPath);
        }
        catch (Exception ex) { System.IO.File.WriteAllText(pngPath + ".error.txt", ex.ToString()); }
        Environment.Exit(0);
    }

    /// <summary>
    /// Test hook (environment variable <c>CSV_SEQ_SAVE_TEST</c> = log file path): opens the Image
    /// Sequencer and logs whether it reports unsaved changes right after opening (must be false),
    /// after placing a clip (true), and whether the untouched Paint Synth picture asks (false); saves a PNG of the arrangement
    /// next to the log (clip miniatures in the editor's colours). Exits WITHOUT closing the windows,
    /// so the settings file is never written. Never active in normal use.
    /// </summary>
    /// <param name="logPath">Where to write the log.</param>
    async void RunSequencerSaveSelfTest(string logPath)
    {
        var log = new System.Text.StringBuilder();
        try
        {
            if (DataContext is not MainViewModel vm) return;
            vm.ShowPaintRooms?.Invoke();
            await System.Threading.Tasks.Task.Delay(2500);
            var tabs = paintRooms!.GetLogicalDescendants().OfType<TabControl>().First();
            tabs.SelectedIndex = 3;
            await System.Threading.Tasks.Task.Delay(1500);
            var room = (ImageSequencerRoom)paintRooms!.Rooms[3];
            log.AppendLine($"after open: unsaved {room.HasUnsavedChanges}");
            var timeline = paintRooms!.GetLogicalDescendants().OfType<CsoundVisual.Controls.SequencerTimeline>().First();
            var png = System.IO.Path.ChangeExtension(logPath, ".png");
            var size = new PixelSize((int)timeline.Bounds.Width, (int)timeline.Bounds.Height);
            using (var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(size)) { rtb.Render(timeline); rtb.Save(png); }
            log.AppendLine($"arrangement png: {png}");
            room.PlaceClip(3, 0);
            log.AppendLine($"after placing a clip: unsaved {room.HasUnsavedChanges}");
            // (No DiscardChanges here: for work auto-saved by an older version it rewrites the settings file.)
            log.AppendLine($"Paint Synth picture unsaved (should be False, nothing painted): {vm.PaintSynthHasUnsavedPicture}");
            log.AppendLine($"Spectrum Room unsaved (should be False, no sound analysed): {paintRooms!.Rooms[1].HasUnsavedChanges}");
            log.AppendLine($"Image Filter canvas unsaved (should be False, starter preset): {vm.ImageFilterUnsavedCanvas is not null}");
            var filterCanvas = CsoundVisual.Views.ImageFilterRoom.CanvasFor(vm.PaintSession)!;
            filterCanvas.Image.Left[0] = 0.5f; filterCanvas.NotifyEdited();
            log.AppendLine($"Image Filter canvas after an edit (should be True): {vm.ImageFilterUnsavedCanvas is not null}");
            // A real brush stroke on the filter canvas, from a «saved» state.
            filterCanvas.MarkPictureSaved();
            filterCanvas.Brushes.BeginStroke(CsoundVisual.Paint.PaintTool.Pencil, new CsoundVisual.Paint.BrushSettings { Size = 4, Opacity = 1, Hardness = 1 }, 20, 20, 2, 2);
            filterCanvas.Brushes.StrokeTo(60, 40);
            filterCanvas.Brushes.EndStroke();
            log.AppendLine($"Image Filter canvas after a brush stroke (should be True): {vm.ImageFilterUnsavedCanvas is not null} (flag {filterCanvas.HasUnsavedPicture}, blank {filterCanvas.PictureIsBlank})");
            var rooms = paintRooms!.Rooms;
            log.AppendLine($"rooms asking on close: Paint Synth {rooms[0].HasUnsavedChanges} (False), Image Filter {rooms[2].HasUnsavedChanges} (True)");
            var ps = vm.PaintSession;
            ps.Brushes.BeginStroke(CsoundVisual.Paint.PaintTool.Pencil, new CsoundVisual.Paint.BrushSettings { Size = 4, Opacity = 1, Hardness = 1 }, 20, 20, 2, 2);
            ps.Brushes.StrokeTo(80, 60); ps.Brushes.EndStroke();
            log.AppendLine($"Paint Synth after a stroke: asks {rooms[0].HasUnsavedChanges} (True)");
            rooms[0].DiscardChanges();
            log.AppendLine($"Paint Synth after Don't save: asks {rooms[0].HasUnsavedChanges} (False), blank {ps.PictureIsBlank} (True), can undo {ps.History.CanUndo} (False)");
            rooms[2].DiscardChanges();
            log.AppendLine($"Image Filter after Don't save: asks {rooms[2].HasUnsavedChanges} (False), state forgotten {CsoundVisual.Views.ImageFilterRoom.CanvasFor(ps) is null} (True)");
            var save = paintRooms.GetLogicalDescendants().OfType<Button>().FirstOrDefault(x => Avalonia.Automation.AutomationProperties.GetName(x) == "Save");
            log.AppendLine($"Save button: {(save is null ? "MISSING" : "present, tip «" + ToolTip.GetTip(save) + "»")}");
        }
        catch (Exception ex) { log.AppendLine("EXCEPTION " + ex); }
        System.IO.File.WriteAllText(logPath, log.ToString());
        Environment.Exit(0);
    }

    /// <summary>
    /// Test hook (environment variable <c>CSV_SETTINGS_UNDO_TEST</c> = log file path): in each Paint
    /// Rooms room, changes settings through the same number boxes / paths the user uses, then
    /// undoes and redoes through the room (the window's ↶ ↷), logging the values. Exits at the end.
    /// Uses reflection to reach the rooms' private controls; never active in normal use.
    /// </summary>
    /// <param name="logPath">Where to write the log.</param>
    async void RunSettingsUndoSelfTest(string logPath)
    {
        var log = new System.Text.StringBuilder();
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Any)!.GetValue(o)!;
        try
        {
            if (DataContext is not MainViewModel vm) return;
            vm.ShowPaintRooms?.Invoke();
            await System.Threading.Tasks.Task.Delay(2500);
            var rooms = paintRooms!.Rooms;

            // ---- Paint Synth: duration box, then gain box twice (one coalesced step)
            {
                var room = rooms[0]; var s = room.EditSession;
                var dur = Field<NumericUpDown>(room, "durationBox"); var gain = Field<NumericUpDown>(room, "gainBox");
                double d0 = s.Image.DurationSeconds, g0 = s.Synth.GainDb;
                dur.Value = (decimal)(d0 + 5);
                await System.Threading.Tasks.Task.Delay(1700);    // outside the coalescing window
                gain.Value = (decimal)(g0 + 2); gain.Value = (decimal)(g0 + 4);
                log.AppendLine($"PaintSynth changed: dur {s.Image.DurationSeconds} gain {s.Synth.GainDb} (was {d0} / {g0}); undo label «{room.UndoLabel}»");
                room.Undo(); log.AppendLine($"  undo1: dur {s.Image.DurationSeconds} gain {s.Synth.GainDb} box {gain.Value}");
                room.Undo(); log.AppendLine($"  undo2: dur {s.Image.DurationSeconds} gain {s.Synth.GainDb} box {dur.Value}");
                room.Redo(); room.Redo(); log.AppendLine($"  redo2: dur {s.Image.DurationSeconds} gain {s.Synth.GainDb}");
                room.Undo(); room.Undo();
            }
            // ---- Spectrum Room: gain step box (no sound needed)
            {
                var room = rooms[1];
                var gain = Field<NumericUpDown>(room, "gainBox"); var sp = Field<CsoundVisual.Paint.SpectrumRoomSettings>(room, "sp");
                double g0 = sp.GainDb;
                gain.Value = (decimal)(g0 - 3);
                log.AppendLine($"Spectrum changed: gain {sp.GainDb} (was {g0}); undo label «{room.UndoLabel}»");
                room.Undo(); log.AppendLine($"  undo: gain {sp.GainDb} box {gain.Value}");
                room.Redo(); log.AppendLine($"  redo: gain {sp.GainDb}"); room.Undo();
            }
            // ---- Image Filter: mix box
            {
                var room = rooms[2];
                var mix = Field<NumericUpDown>(room, "mixBox");
                var prefs = Field<object>(room, "prefs");
                CsoundVisual.Paint.ImageFilterSettings Cur() => (CsoundVisual.Paint.ImageFilterSettings)prefs.GetType().GetProperty("Settings")!.GetValue(prefs)!;
                double m0 = Cur().Mix;
                mix.Value = (decimal)(m0 * 100 > 50 ? 30 : 80);
                log.AppendLine($"Filter changed: mix {Cur().Mix} (was {m0}); undo label «{room.UndoLabel}»");
                room.Undo(); log.AppendLine($"  undo: mix {Cur().Mix} box {mix.Value}");
                room.Redo(); log.AppendLine($"  redo: mix {Cur().Mix}"); room.Undo();
            }
            // ---- Image Sequencer: tempo, then a pattern selection; undo must go newest first
            {
                var room = rooms[3];
                var tempo = Field<NumericUpDown>(room, "tempoBox");
                var doc = (CsoundVisual.Paint.SequencerDocument)room.GetType().GetProperty("Doc", Any)!.GetValue(room)!;
                double t0 = doc.Tempo;
                tempo.Value = (decimal)(t0 + 10);
                var ps = room.EditSession;
                ps.ChangeSelection(new CsoundVisual.Paint.CellRect(0, 0, 2, 2));
                log.AppendLine($"Sequencer changed: tempo {doc.Tempo} (was {t0}), selection {(ps.Selection is null ? "none" : "set")}; undo label «{room.UndoLabel}»");
                room.Undo(); log.AppendLine($"  undo1: tempo {doc.Tempo} selection {(ps.Selection is null ? "none" : "set")}");
                room.Undo(); log.AppendLine($"  undo2: tempo {doc.Tempo} box {tempo.Value}");
                room.Redo(); log.AppendLine($"  redo1: tempo {doc.Tempo} selection {(ps.Selection is null ? "none" : "set")}");
                room.Undo();
            }
        }
        catch (Exception ex) { log.AppendLine("EXCEPTION " + ex); }
        System.IO.File.WriteAllText(logPath, log.ToString());
        Environment.Exit(0);
    }

    /// <summary>
    /// Test hook (environment variable <c>CSV_PAINT_PLAY_TEST</c> = log file path; the picture in
    /// <c>CSV_PLAY_PICTURE</c>): plays Paint Synth INAUDIBLY (−90 dB, no automatic level), replaces the
    /// image with an imported picture while it plays (like opening another picture), then stops and
    /// plays again, logging the player state, the size of the tables actually playing and the
    /// console. Exits at the end. Never active in normal use.
    /// </summary>
    /// <param name="logPath">Where to write the log.</param>
    async void RunPaintPlaySelfTest(string logPath)
    {
        var log = new System.Text.StringBuilder();
        if (DataContext is not MainViewModel vm) return;
        int consoleStart = vm.ConsoleLines.Count;
        try
        {
            vm.ShowPaintRooms?.Invoke();
            await System.Threading.Tasks.Task.Delay(2500);
            var session = vm.PaintSession;
            var player = session.Player!;
            player.Message += (text, warn) => log.AppendLine($"   [player msg{(warn ? " WARN" : "")}] {text}");
            session.Synth = session.Synth with { GainDb = -90, AutoLevel = false, Loop = true };
            string State() => $"state {player.State} tables {player.Tables?.Width}×{player.Tables?.Height} image {session.Image.Width}×{session.Image.Height} col {player.CurrentColumn:0.0} dsp {vm.Engine.DspLoad:0.00} underruns {vm.Engine.Underruns} score {vm.Engine.ScoreTime:0.0}";
            log.AppendLine("before: " + State());
            player.Play(session.Image, session.Synth, 0);
            for (int i = 0; i < 6; i++) { await System.Threading.Tasks.Task.Delay(500); log.AppendLine("playing: " + State()); }
            // Open another picture while playing: large, logarithmic 1024 rows, full width.
            string pic = Environment.GetEnvironmentVariable("CSV_PLAY_PICTURE") ?? "";
            using (var decoded = await System.Threading.Tasks.Task.Run(() => CsoundVisual.Paint.ImageCodec.Decode(pic)))
            {
                var axis = CsoundVisual.Paint.PitchAxis.Logarithmic(session.Image.Axis.LowHz, session.Image.Axis.HighHz, 1024);
                var img = await System.Threading.Tasks.Task.Run(() => CsoundVisual.Paint.ImageCodec.FromPicture(decoded, decoded.Bitmap.Width, axis, 12, CsoundVisual.Paint.ColorMapping.Brightness, CsoundVisual.Paint.ImportFit.Stretch));
                log.AppendLine($"importing {img.Width}×{img.Height} while playing");
                session.ReplaceImage(img, "import");
            }
            for (int i = 0; i < 8; i++) { await System.Threading.Tasks.Task.Delay(500); log.AppendLine("after import: " + State()); }
            player.Stop();
            await System.Threading.Tasks.Task.Delay(800);
            log.AppendLine("stopped: " + State());
            player.Play(session.Image, session.Synth, 0);
            for (int i = 0; i < 6; i++) { await System.Threading.Tasks.Task.Delay(500); log.AppendLine("play again: " + State()); }
            player.Stop();
            await System.Threading.Tasks.Task.Delay(500);
        }
        catch (Exception ex) { log.AppendLine("EXCEPTION " + ex); }
        log.AppendLine("---- console:");
        for (int i = consoleStart; i < vm.ConsoleLines.Count; i++) log.AppendLine(vm.ConsoleLines[i].Text);
        System.IO.File.WriteAllText(logPath, log.ToString());
        Environment.Exit(0);
    }

    /// <summary>
    /// Test hook (environment variable <c>CSV_PAINT_UNDO_TEST</c> = log file path): opens Paint
    /// Rooms, draws four pencil strokes on the visible canvas with synthesized pointer events (the
    /// real press → move → release path), then clicks the header's Undo button step by step, logging
    /// the history labels and the painted total after each step, and exits. Never active in normal use.
    /// </summary>
    /// <param name="logPath">Where to write the log.</param>
    async void RunPaintUndoSelfTest(string logPath)
    {
        var log = new System.Text.StringBuilder();
        try
        {
            if (DataContext is not MainViewModel vm) return;
            vm.ShowPaintRooms?.Invoke();
            await System.Threading.Tasks.Task.Delay(2500);
            var win = paintRooms!;
            var canvas = win.GetVisualDescendants().OfType<CsoundVisual.Controls.PaintCanvas>().First(c => c.IsEffectivelyVisible);
            var session = canvas.Session!;
            canvas.Tool = CsoundVisual.Paint.PaintTool.Pencil;
            var undo = win.GetVisualDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Undo");
            double Sum() { double s = 0; foreach (var v in session.Image.Left) s += v; return s; }
            string State() => $"sum {Sum():0.0} canUndo {session.History.CanUndo} next «{session.History.UndoLabel}» canRedo {session.History.CanRedo} tool {canvas.Tool}";
            log.AppendLine("open: " + State());
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            var props = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
            var moveProps = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
            var upProps = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
            ulong t = 1;
            for (int k = 0; k < 4; k++)
            {
                var a = new Point(150 + k * 120, canvas.Bounds.Height / 2);
                var root = (Visual)win;
                Point R(Point p) => canvas.TranslatePoint(p, root) ?? p;
                canvas.RaiseEvent(new PointerPressedEventArgs(canvas, pointer, root, R(a), t++, props, KeyModifiers.None));
                for (int i = 1; i <= 5; i++)
                    canvas.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, canvas, pointer, root, R(a + new Point(i * 15, i * 6)), t++, moveProps, KeyModifiers.None));
                canvas.RaiseEvent(new PointerReleasedEventArgs(canvas, pointer, root, R(a + new Point(75, 30)), t++, upProps, KeyModifiers.None, MouseButton.Left));
                await System.Threading.Tasks.Task.Delay(300);
                log.AppendLine($"stroke {k + 1}: " + State());
            }
            // Two selection changes after the strokes: undo must step back through them first.
            session.ChangeSelection(new CsoundVisual.Paint.CellRect(10, 10, 60, 60));
            log.AppendLine("select A: " + State() + $" sel {session.Selection?.Bounds}");
            session.ChangeSelection(new CsoundVisual.Paint.CellRect(100, 20, 300, 90));
            log.AppendLine("select B: " + State() + $" sel {session.Selection?.Bounds}");
            for (int n = 1; n <= 8 && session.History.CanUndo; n++)
            {
                undo.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await System.Threading.Tasks.Task.Delay(300);
                log.AppendLine($"undo {n}: " + State() + $" sel {session.Selection?.Bounds}");
            }
        }
        catch (Exception ex) { log.AppendLine("EXCEPTION " + ex); }
        System.IO.File.WriteAllText(logPath, log.ToString());
        Environment.Exit(0);
    }

    /// <summary>
    /// Test hook (environment variable <c>CSV_UNDO_TEST</c> = log file path): types a few words
    /// into the editor through the editor's real text-input path (so every handler the app attaches
    /// runs: completion, live values, undo granularity…), then presses Undo until nothing is left,
    /// logging the text after each step, and closes the window. Used to check how many steps
    /// typing produces; never active in normal use.
    /// </summary>
    /// <param name="logPath">Where to write the log.</param>
    async void RunUndoSelfTest(string logPath)
    {
        var log = new System.Text.StringBuilder();
        static string Show(string s) => s.Replace("\r", "").Replace("\n", "\\n");
        try
        {
            Editor.Focus();
            Editor.TextArea.Caret.Offset = Editor.Document.TextLength;
            int start = Editor.Document.TextLength;
            log.AppendLine($"start length {start}, undo stack can undo: {Editor.CanUndo}");
            foreach (char ch in "\nabc def\nxyz")
            {
                Editor.TextArea.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = ch.ToString(), Source = Editor.TextArea });
                await System.Threading.Tasks.Task.Delay(40);
            }
            log.AppendLine($"typed: [{Show(Editor.Text[start..])}] caret {Editor.TextArea.Caret.Offset} line {Editor.TextArea.Caret.Line} scrollY {Editor.VerticalOffset:0}");
            for (int n = 1; n <= 30 && Editor.CanUndo; n++)
            {
                EditorUndoButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await System.Threading.Tasks.Task.Delay(40);
                string tail = Editor.Text.Length > start ? Editor.Text[start..] : $"(shorter than start: {Editor.Text.Length})";
                log.AppendLine($"undo {n}: [{Show(tail)}] caret {Editor.TextArea.Caret.Offset} line {Editor.TextArea.Caret.Line} scrollY {Editor.VerticalOffset:0}");
            }
            for (int n = 1; n <= 30 && Editor.CanRedo; n++)
            {
                EditorRedoButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await System.Threading.Tasks.Task.Delay(40);
                log.AppendLine($"redo {n}: [{Show(Editor.Text.Length > start ? Editor.Text[start..] : "")}] caret {Editor.TextArea.Caret.Offset} line {Editor.TextArea.Caret.Line} scrollY {Editor.VerticalOffset:0}");
            }
        }
        catch (Exception ex) { log.AppendLine("EXCEPTION " + ex); }
        System.IO.File.WriteAllText(logPath, log.ToString());
        // Exit directly: the document is modified, so Close() would stop at the «save changes?» question.
        Environment.Exit(0);
    }

    /// <summary>
    /// Console copy: right-click a line → «Copy line» / «Copy selected lines» / «Copy all»;
    /// Ctrl+C copies the selected lines (Ctrl+click and Shift+click select several).
    /// </summary>
    /// <remarks>
    /// The line under the pointer is captured in a tunnel PointerPressed handler (before the
    /// ListBox changes its selection), because the context menu itself does not know which item
    /// was right-clicked.
    /// </remarks>
    void SetupConsoleCopy()
    {
        ConsoleLine? clicked = null;
        ConsoleList.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(ConsoleList).Properties.IsRightButtonPressed)
                clicked = (e.Source as StyledElement)?.DataContext as ConsoleLine;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var copyLine = new MenuItem { Header = Loc.S("Copiar línea", "Copy line") };
        var copySelected = new MenuItem { Header = Loc.S("Copiar líneas seleccionadas", "Copy selected lines"), InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        var copyAll = new MenuItem { Header = Loc.S("Copiar toda la consola", "Copy whole console") };
        copyLine.Click += async (_, _) => { if (clicked is not null) await CopyText(clicked.Text); };
        copySelected.Click += async (_, _) => await CopySelectedConsoleLines();
        copyAll.Click += async (_, _) => { if (vm is not null) await CopyText(string.Join(Environment.NewLine, vm.ConsoleLines.Select(l => l.Text))); };
        var menu = new ContextMenu { Items = { copyLine, copySelected, new Separator(), copyAll } };
        menu.Opening += (_, _) =>
        {
            copyLine.IsEnabled = clicked is not null;
            copySelected.IsEnabled = ConsoleList.SelectedItems?.Count > 0;
        };
        ConsoleList.ContextMenu = menu;
        ConsoleList.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { await CopySelectedConsoleLines(); e.Handled = true; }
        };
    }

    /// <summary>
    /// Copies the selected console lines to the clipboard in console order (not in the order they
    /// were clicked), one per line.
    /// </summary>
    async Task CopySelectedConsoleLines()
    {
        if (vm is null || ConsoleList.SelectedItems is not { Count: > 0 } items) return;
        var chosen = items.OfType<ConsoleLine>().ToHashSet();
        await CopyText(string.Join(Environment.NewLine, vm.ConsoleLines.Where(chosen.Contains).Select(l => l.Text)));
    }

    /// <summary>Puts <paramref name="text"/> on the system clipboard (no-op if the window has none).</summary>
    /// <param name="text">Plain text to copy.</param>
    async Task CopyText(string text)
    {
        if (Clipboard is { } clipboard)
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
    }

    /// <summary>Selects a whole line of a document, scrolls to it and focuses the editor.</summary>
    /// <param name="doc">Document the line belongs to; ignored unless it is the one shown.</param>
    /// <param name="line">1-based line number; ignored when beyond the end of the document.</param>
    void GoToLine(DocumentViewModel doc, int line)
    {
        if (vm?.SelectedDocument != doc || line > doc.Document.LineCount)
            return;
        var dl = doc.Document.GetLineByNumber(line);
        Editor.TextArea.Caret.Line = line;
        Editor.TextArea.Caret.Column = 1;
        Editor.Select(dl.Offset, dl.Length);
        Editor.ScrollToLine(line);
        Editor.Focus();
    }

    // ------------------------------------------------------------------------------------------
    //  Frame loop, layout
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The ~60 fps UI frame (<see cref="frameTimer"/>): lets the view model update its polled state
    /// (<c>Tick</c>: drains Csound's console messages, updates the clock…) and feeds the output card.
    /// </summary>
    /// <remarks>
    /// Three sources, by priority: the DAW plug-in when linked («Send to DAW»), the running engine,
    /// or nothing (the meters keep their last state; they are reset in
    /// <see cref="OnViewModelPropertyChanged"/> when the engine stops). Engine data is pulled with
    /// lock-free reads (<see cref="CsoundEngine.TakePeaks"/>, <see cref="CsoundEngine.CopyScope"/>)
    /// while the performance thread keeps writing; a torn read only affects one drawn frame.
    /// <c>Math.Max(n, 1)</c> keeps at least one meter bar before the engine reports its channel count.
    /// </remarks>
    void OnFrame()
    {
        if (vm is null) return;
        vm.Tick();
        if (vm.IsDawLinked)
        {
            // Linked to the VST3 plugin: the output card shows what the plugin plays in the DAW.
            Meter.Push(vm.DawPeaks);
            if (vm.DawMetersVersion != lastDawMeters)
            {
                lastDawMeters = vm.DawMetersVersion;
                Scope.Push(vm.DawScope);
                Spectrum.Push(vm.DawScope, vm.DawSampleRate);
            }
        }
        else if (vm.IsRunning)
        {
            int n = vm.Engine.TakePeaks(peakBuffer);
            Meter.Push(peakBuffer.AsSpan(0, Math.Max(n, 1)));
            vm.Engine.CopyScope(scopeBuffer);
            Scope.Push(scopeBuffer);
            Spectrum.Push(scopeBuffer, vm.Engine.SampleRate);
            if (vm.IsInputMeterVisible)
            {
                // Live input peaks (capture side, before resampling), one bar per input channel.
                int k = vm.Engine.TakeInputPeaks(inputPeakBuffer);
                InMeter.Push(inputPeakBuffer.AsSpan(0, Math.Max(k, 1)));
            }
        }
    }

    /// <summary>
    /// Reacts to view-model changes that need imperative view work: side-panel visibility, clearing
    /// the meters when nothing feeds them any more, and repainting the editor background (error
    /// lines) when another document is shown.
    /// </summary>
    /// <param name="sender">The view model.</param>
    /// <param name="e">Name of the changed property.</param>
    void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsSidePanelVisible):
                ApplySidePanel();
                break;
            // Engine stopped while not linked to the DAW, or the DAW link ended while the engine
            // is stopped: no source left, so blank the output card.
            case nameof(MainViewModel.Mode) when vm is { IsRunning: false, IsDawLinked: false }:
            case nameof(MainViewModel.IsDawLinked) when vm is { IsRunning: false }:
                Meter.Reset();
                InMeter.Reset();
                Scope.Push([]);
                Spectrum.Reset();
                break;
            case nameof(MainViewModel.SelectedDocument):
                Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
                break;
        }
    }

    /// <summary>
    /// Picks the toolbar level for the window width and applies it, then auto-hides / shows the side
    /// panel when the width crosses <see cref="NarrowWidth"/>.
    /// </summary>
    /// <param name="width">Window width in device-independent pixels.</param>
    /// <remarks>
    /// The level is the first one whose measured width fits (<see cref="levelWidth"/>; 0 = not yet
    /// measured, treated as fitting). Changing level changes the layout, which re-measures the new
    /// level in the constructor's <c>Measure</c> and may call this method again until it settles.
    /// </remarks>
    void ApplyResponsiveLayout(double width)
    {
        if (vm is null) return;
        int level = 0;
        while (level < 4 && width < levelWidth[level]) level++;
        toolbarLevel = level;
        vm.IsCompact = level >= 1;
        // Icons only: «Play» keeps its label but no longer sets the common button width.
        if (PlayButton.Content is Grid { ColumnDefinitions: [var playColumn, ..] })
            playColumn.SharedSizeGroup = level >= 1 ? null : "Bar";
        SetOverflow(level >= 2);
        // Level 3: pill narrowed to 150 px and the status word hidden, leaving dot + clocks.
        StatusPill.Width = level >= 3 ? 150 : 270;
        StatusLabel.IsVisible = level < 3;
        StatusPill.IsVisible = level < 4;
        linkIndicator?.SetValue(IsVisibleProperty, level < 4 && vm.IsLinkEnabled);
        View3DButton.IsVisible = level < 4;

        // When the window becomes narrow the side panel hides once; the user can reopen it (Ctrl+B).
        bool narrow = width < NarrowWidth;
        if (narrow != wasNarrow)
        {
            wasNarrow = narrow;
            vm.IsSidePanelVisible = !narrow;
        }
    }

    /// <summary>
    /// Shows or hides the side panel per <c>IsSidePanelVisible</c>. The columns are collapsed to 0
    /// (not just the controls hidden) so the editor takes the freed width.
    /// </summary>
    /// <remarks>Showing it again resets its width to <see cref="SidePanelWidth"/>, discarding a splitter drag.</remarks>
    void ApplySidePanel()
    {
        bool show = vm?.IsSidePanelVisible ?? true;
        var cols = Body.ColumnDefinitions;
        cols[1].Width = new GridLength(show ? 6 : 0);
        cols[2].Width = new GridLength(show ? SidePanelWidth : 0);
        SidePanel.IsVisible = show;
        SideSplitter.IsVisible = show;
    }

    /// <summary>
    /// Keeps the newest console line in view. Posted at Background priority so a burst of lines
    /// added in one go causes one scroll after the ListBox has created their containers.
    /// </summary>
    void ScrollConsoleToEnd()
    {
        if (vm is null || vm.ConsoleLines.Count == 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (vm.ConsoleLines.Count > 0)
                ConsoleList.ScrollIntoView(vm.ConsoleLines.Count - 1);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Restores the last size/position if it is still on a connected screen; otherwise (first run,
    /// monitor unplugged) sizes the window to at most 85 % of the current screen's working area
    /// and centres it. The window starts maximized only if it was maximized when it closed.
    /// </summary>
    void RestoreWindowBounds()
    {
        if (vm is null) return;
        var s = vm.Settings;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;

        WindowState = WindowState.Normal;
        // Saved bounds are used only if a point just inside the saved top-left corner (+40, +20 px,
        // i.e. on the title bar) lies on a connected screen's working area, so the window can always
        // be grabbed. Width/Height are DIPs; Position and WorkingArea are physical pixels, hence the
        // division / multiplication by the screen's scaling factor.
        if (s.WindowX is int x && s.WindowY is int y && s.WindowWidth is double w && s.WindowHeight is double h &&
            Screens.All.FirstOrDefault(sc => sc.WorkingArea.Contains(new PixelPoint(x + 40, y + 20))) is { } saved)
        {
            double scale = saved.Scaling;
            Width = Math.Min(w, saved.WorkingArea.Width / scale);
            Height = Math.Min(h, saved.WorkingArea.Height / scale);
            Position = new PixelPoint(x, y);
        }
        else
        {
            var wa = screen.WorkingArea;
            double scale = screen.Scaling;
            Width = Math.Min(1440, wa.Width / scale * 0.85);
            Height = Math.Min(900, wa.Height / scale * 0.85);
            Position = new PixelPoint(wa.X + (int)((wa.Width - Width * scale) / 2), wa.Y + (int)((wa.Height - Height * scale) / 2));
        }
        if (s.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    /// <summary>Remembers size and position (the normal, non-maximized ones) for the next start.</summary>
    void SaveWindowBounds()
    {
        if (vm is null) return;
        var s = vm.Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            s.WindowX = Position.X;
            s.WindowY = Position.Y;
            s.WindowWidth = Width;
            s.WindowHeight = Height;
        }
        s.Save();
    }

    /// <summary>
    /// On Windows, Avalonia 12 draws its own title bar including the window title text. The
    /// app's toolbar already shows the name, so only those title texts are hidden; the caption
    /// buttons stay.
    /// </summary>
    /// <remarks>
    /// The title bar is not part of this window's content but of its visual parent (the
    /// decorations host), so the search starts there and skips anything inside the window itself.
    /// A TextBlock is recognised by its text being equal to <see cref="Window.Title"/>.
    /// </remarks>
    void HideDrawnTitle()
    {
        if (this.GetVisualParent() is not { } host) return;
        foreach (var tb in host.GetVisualDescendants().OfType<TextBlock>())
            if (!this.IsVisualAncestorOf(tb) && tb.Text == Title)
                tb.IsVisible = false;
    }

    /// <summary>Asks about unsaved documents before closing; the close is retried once they are resolved.</summary>
    /// <param name="sender">This window.</param>
    /// <param name="e">Closing arguments; cancelled while the prompt is pending.</param>
    /// <remarks>
    /// <para>
    /// Closing cannot be awaited, so the first request is always cancelled, the prompt runs
    /// asynchronously, and if the user agrees <see cref="allowClose"/> is set and <see cref="Window.Close()"/>
    /// is called again; that second pass takes the first branch and does the real shutdown.
    /// </para>
    /// <para>
    /// Shutdown order: stop Csound, save the window bounds, release MIDI, close the secondary windows
    /// (the unowned 3D window would otherwise keep the app alive), then save and free the VST3
    /// chain. <c>async void</c> is acceptable here because it is an event handler.
    /// </para>
    /// </remarks>
    async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (allowClose || vm is null)
        {
            vm?.Engine.Stop();
            SaveWindowBounds();
            // Close MIDI ports and the Windows MIDI Services session cleanly.
            vm?.Midi.Dispose();
            // The 3D window would otherwise keep the application alive (last-window shutdown mode).
            visual3D?.Close();
            settingsWindow?.Close();
            examplesWindow?.Close();
            pianoRoll?.Close();
            sequencer?.Close();
            pluginChainWindow?.Close();
            vm?.ShutdownPluginHost(); // saves the chain (with each plug-in's state) and frees the plug-ins
            return;
        }
        e.Cancel = true;
        // A second close request (e.g. clicking X again) while the prompt is open is ignored.
        if (closePromptOpen)
            return;
        closePromptOpen = true;
        try
        {
            // Paint Rooms work kept between sessions (Image Sequencer) is asked about first.
            if (paintRooms is { } pr && !await pr.ConfirmUnsavedAsync()) return;
            // Pictures that live only for the app run (Paint Synth, Image Filter): offer to export them
            // as PNG. A cancelled picker or a failed write abandons the close, so nothing is lost.
            var pictures = new List<(CsoundVisual.Paint.PaintSession Session, string Name)>();
            if (vm.PaintSynthHasUnsavedPicture) pictures.Add((vm.PaintSession, Loc.S("la imagen de Paint Synth (se guarda como PNG)", "the Paint Synth picture (saved as PNG)")));
            if (vm.ImageFilterUnsavedCanvas is { } filterCanvas) pictures.Add((filterCanvas, Loc.S("la imagen del Image Filter (se guarda como PNG)", "the Image Filter picture (saved as PNG)")));
            foreach (var (picSession, picName) in pictures)
            {
                var choice = await new ConfirmSaveDialog(picName).ShowDialog<SaveChoice>(this);
                if (choice == SaveChoice.Cancel) return;
                if (choice == SaveChoice.Save)
                {
                    try { if (await PaintSynthRoom.ExportPngWithPickerAsync(this, picSession) is null) return; }
                    catch (Exception ex) { vm.ConsoleLines.Add(new ConsoleLine(Loc.S("✗ No se pudo guardar el PNG: ", "✗ Could not save the PNG: ") + ex.Message, MessageKind.Error)); return; }
                }
            }
            if (await vm.ConfirmCloseAllAsync())
            {
                allowClose = true;
                vm.Engine.Stop();
                Close();
            }
        }
        finally
        {
            closePromptOpen = false;
        }
    }

    // ------------------------------------------------------------------------------------------
    //  File pickers
    // ------------------------------------------------------------------------------------------

    /// <summary>Shows the «Open Csound file» picker (assigned to <c>MainViewModel.PickOpenFile</c>).</summary>
    /// <returns>The chosen local path, or null when cancelled or the item is not a local file.</returns>
    async Task<string?> PickOpenFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.Instance["Dialog.OpenCsd"],
            AllowMultiple = false,
            FileTypeFilter = [CsoundFiles, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Shows the save picker (assigned to <c>MainViewModel.PickSaveFile</c>), used both for
    /// «Save as» and for «Render to WAV».</summary>
    /// <param name="suggestedName">Initial file name.</param>
    /// <param name="extension">Default extension without dot; <c>"wav"</c> switches title and filter to rendering.</param>
    /// <returns>The chosen local path, or null when cancelled.</returns>
    async Task<string?> PickSaveFileAsync(string suggestedName, string extension)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = extension == "wav" ? Loc.Instance["Dialog.RenderWav"] : Loc.Instance["Dialog.SaveCsd"],
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = [extension == "wav" ? WavFiles : CsoundFiles],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }
}
