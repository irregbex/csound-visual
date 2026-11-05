using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CsoundVisual.Controls;
using CsoundVisual.Localization;
using CsoundVisual.Music;
using CsoundVisual.Theming;
using CsoundVisual.ViewModels;

namespace CsoundVisual.Views;

/// <summary>
/// Step sequencer window (Tools › Step sequencer, Ctrl+Shift+P): tracks × sixteenth steps that
/// play Csound instruments live, with swing, per-step velocity, mute/solo, probability and
/// Euclidean/random fills; ships with a synthesised drum kit (<c>Assets/DrumKit.csd</c>) and a
/// groove, so «Open drum kit» + Play makes a beat immediately. Built in code.
/// </summary>
/// <remarks>
/// <para>Each row has ordinary controls (name, instrument, note, velocity, probability, M/S, menu)
/// laid out at <see cref="StepGridView.RowHeight"/>, next to the <see cref="StepGridView"/> that
/// draws the steps. The pattern lives in <see cref="MainViewModel.Sequencer"/> (kept for the
/// session); the scheduler gets a copy after every edit.</para>
/// <para>Notes are <c>i &lt;instr&gt; &lt;offset&gt; &lt;one step&gt; &lt;p4 = note&gt; &lt;p5 = velocity&gt;</c>;
/// the kit's drums set their own decay length, melodic instruments get a sixteenth.</para>
/// <para><b>Layout</b> (a <see cref="DockPanel"/>): a wrapping tool bar (Play, tempo, swing, step
/// count, Open drum kit, Export + loops, Import, «⋯»); the target-document line; a vertical
/// <see cref="ScrollViewer"/> holding a two-column grid — per-track header controls on the left
/// (<see cref="headers"/>, first child = column titles) and the drawn <see cref="StepGridView"/> on
/// the right, row-aligned because both use <see cref="StepGridView.HeaderHeight"/> and
/// <see cref="StepGridView.RowHeight"/>; a «+ Track» footer; and the status line.</para>
/// <para><b>Undo model.</b> Unlike the piano roll (whose model has its own history), undo/redo
/// here are whole-pattern clones kept by the window (<see cref="undo"/>, <see cref="redo"/>, max
/// 100); <see cref="Checkpoint"/> is called before structural edits and by the grid at the start
/// of each mouse gesture (<see cref="StepGridView.EditStarting"/>). The history is lost when the
/// window closes; the pattern itself is not.</para>
/// <para><b>Re-entrancy guard.</b> <see cref="syncing"/> is set while code writes control values
/// so their change handlers do not treat it as a user edit.</para>
/// <para><b>Target document.</b> Same rule as the piano roll: <see cref="MainViewModel.ToolDocument"/>
/// (playing document, else the editor's), refreshed by a 700 ms timer and view-model changes.</para>
/// </remarks>
public sealed class StepSequencerWindow : Window
{
    /// <summary>The app's view model (engine, documents, drum kit, score-writing helpers).</summary>
    readonly MainViewModel vm;

    /// <summary>
    /// The pattern being edited; the same object as <see cref="MainViewModel.Sequencer"/>. Not
    /// readonly: undo/redo/import swap in a different instance (see <see cref="Load"/>).
    /// </summary>
    StepPattern pattern;

    /// <summary>The drawn step grid (cells, playhead column, accents) for <see cref="pattern"/>.</summary>
    readonly StepGridView grid = new();

    /// <summary>Play/stop logic; it always receives a <see cref="StepPattern.Clone"/>, never the edited instance.</summary>
    readonly ToolTransport transport;

    /// <summary>Left column: child 0 = column titles, then one row of controls per track (<see cref="BuildHeader"/>).</summary>
    readonly StackPanel headers = new() { Spacing = 0 };

    /// <summary>▶/■ button; content swapped in <see cref="UpdatePlayButton"/>.</summary>
    readonly Button playButton;

    /// <summary>Tempo box (BPM, 20–400); one step = a sixteenth note.</summary>
    readonly NumericUpDown tempo;

    /// <summary>Swing slider in whole percent (0–75), stored in the pattern as a 0–0.75 fraction of a step.</summary>
    readonly Slider swing = new() { Minimum = 0, Maximum = 75, Width = 130, VerticalAlignment = VerticalAlignment.Center, TickFrequency = 1, IsSnapToTickEnabled = true };

    /// <summary>«NN %» readout next to <see cref="swing"/> (fixed width so the bar does not move).</summary>
    readonly TextBlock swingText = new() { Width = 38, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };

    /// <summary>Pattern length picker; indexes <see cref="StepChoices"/>.</summary>
    readonly ComboBox stepsPicker = new() { Width = 76, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>How many repetitions «Export to score» writes (<see cref="StepPattern.ExportLoops"/>).</summary>
    readonly NumericUpDown loops;

    /// <summary>«Document: x.csd (playing)» — which document the tool addresses.</summary>
    readonly TextBlock docLabel = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("AccentHi") };

    /// <summary>Left part of the status line: hover text from <see cref="StepGridView.Hover"/>.</summary>
    readonly TextBlock status = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = AppTheme.Brush("TextDim") };

    /// <summary>Right part of the status line: transport / Csound messages.</summary>
    readonly TextBlock message = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };

    /// <summary>700 ms background timer calling <see cref="RefreshTarget"/>.</summary>
    readonly DispatcherTimer docTimer;

    /// <summary>Undo history: pattern clones taken before each edit (oldest first, at most 100).</summary>
    readonly List<StepPattern> undo = [];

    /// <summary>Redo history: clones pushed by <see cref="Undo"/>; cleared by every new <see cref="Checkpoint"/>.</summary>
    readonly List<StepPattern> redo = [];

    /// <summary>The instrument combo box of each header row, in track order (refilled when the document changes).</summary>
    readonly List<ComboBox> instrumentPickers = [];

    /// <summary>Instruments defined in the target document (from <see cref="MainViewModel.InstrumentsOf"/>).</summary>
    List<InstrumentChoice> instruments = [];

    /// <summary>The document whose <c>TextChanged</c> event is currently observed.</summary>
    DocumentViewModel? watchedDoc;

    /// <summary>Set when the target's text (or the target) changed: the next <see cref="RefreshTarget"/> re-reads its instruments.</summary>
    bool docTextChanged = true;

    /// <summary>Re-entrancy guard: true while code writes control values.</summary>
    bool syncing;

    /// <summary>Allowed pattern lengths in sixteenth steps: half a bar, one bar, two bars (4/4).</summary>
    static readonly int[] StepChoices = [8, 16, 32];

    /// <summary>Creates the window for <paramref name="viewModel"/>.</summary>
    /// <param name="viewModel">The main view model; its <see cref="MainViewModel.Sequencer"/> pattern is edited in place.</param>
    public StepSequencerWindow(MainViewModel viewModel)
    {
        vm = viewModel;
        pattern = vm.Sequencer;
        Title = "Csound Visual · " + Loc.S("Secuenciador por pasos", "Step sequencer");
        Width = 1260;
        Height = 640;
        MinWidth = 820;
        MinHeight = 360;
        Background = AppTheme.Brush("BgDeep");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        grid.Pattern = pattern;
        transport = new ToolTransport(vm, pattern.Clone()) { Bpm = pattern.Bpm };

        playButton = ToolUi.Button(Loc.S("Tocar", "Play"),
            Loc.S("Tocar / detener el patrón en loop (Espacio). ", "Play / stop the pattern in a loop (Space). ") + ToolUi.PlayExplanation,
            TogglePlay, "IconPlay", primary: true);
        playButton.MinWidth = 104;
        tempo = ToolUi.Number(20, 400, pattern.Bpm, 1, Loc.S("Tempo en pulsos por minuto (un paso = una semicorchea). Se puede cambiar mientras suena.", "Tempo in beats per minute (one step = a sixteenth note). Can be changed while playing."), "0.#", 132);
        tempo.ValueChanged += (_, e) =>
        {
            if (syncing || e.NewValue is not { } v) return;
            pattern.Bpm = (double)v;
            transport.Bpm = pattern.Bpm;
            Edited();
        };
        swing.Value = Math.Round(pattern.Swing * 100);
        ToolTip.SetTip(swing, Loc.S("Swing: retrasa las semicorcheas pares (las «y») una fracción de paso. 0 % = recto, 33 % ≈ tresillo, 50 % = puntillo, 75 % = máximo.",
                                    "Swing: delays the even sixteenths (the «ands») by a fraction of a step. 0 % = straight, 33 % ≈ triplet feel, 50 % = dotted, 75 % = maximum."));
        // Slider has no ValueChanged-style event that carries only user changes, so filter PropertyChanged.
        // The readout is updated even while syncing; only the model write is skipped.
        swing.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            swingText.Text = $"{swing.Value:0} %";
            if (syncing) return;
            pattern.Swing = swing.Value / 100;
            Edited();
        };
        swingText.Text = $"{swing.Value:0} %";
        stepsPicker.ItemsSource = StepChoices.Select(s => s.ToString(CultureInfo.InvariantCulture)).ToList();
        stepsPicker.SelectedIndex = Math.Max(0, Array.IndexOf(StepChoices, pattern.StepCount));
        ToolTip.SetTip(stepsPicker, Loc.S("Pasos del patrón (semicorcheas): 8 = medio compás, 16 = un compás, 32 = dos compases. Los pasos de más se conservan al achicar.",
                                          "Steps in the pattern (sixteenths): 8 = half a bar, 16 = one bar, 32 = two bars. Extra steps are kept when shrinking."));
        stepsPicker.SelectionChanged += (_, _) =>
        {
            if (syncing || stepsPicker.SelectedIndex < 0) return;
            Checkpoint();
            pattern.StepCount = StepChoices[stepsPicker.SelectedIndex];
            grid.InvalidateMeasure(); // re-layout: the cell width is the control width divided by the step count
            Edited();
        };
        loops = ToolUi.Number(1, 999, pattern.ExportLoops, 1, Loc.S("Cuántas veces se escribe el patrón al exportarlo a la partitura.", "How many times the pattern is written when exported to the score."), "0", 112);
        // Export-only setting: does not affect playback, so no snapshot/Edited().
        loops.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) pattern.ExportLoops = (int)v; };

        var kit = ToolUi.Button(Loc.S("Abrir kit de batería", "Open drum kit"),
            Loc.S("Abre en una pestaña un kit de batería sintetizado (bombo, caja, hi-hats, palmas, tom, aro, cencerro = instrumentos 1 a 8, p4 = nota para afinar, p5 = velocidad). Las pistas por defecto ya apuntan a él: ábrelo y presiona Tocar.",
                  "Opens a synthesised drum kit in a tab (kick, snare, hi-hats, clap, tom, rim, cowbell = instruments 1 to 8, p4 = note for tuning, p5 = velocity). The default tracks already point at it: open it and press Play."),
            OpenKit, "IconNew");
        var export = ToolUi.Button(Loc.S("Exportar a partitura", "Export to score"),
            Loc.S("Escribe el patrón (repetido «Vueltas» veces, con swing, mute/solo) como líneas «i» al final de <CsScore>, en un bloque marcado «; --- step sequencer begin/end ---» que también guarda el patrón para volver a abrirlo. Si el bloque existe, se reemplaza.",
                  "Writes the pattern (repeated «Loops» times, with swing, mute/solo) as «i» lines at the end of <CsScore>, in a block marked «; --- step sequencer begin/end ---» that also stores the pattern so it can be reopened. An existing block is replaced."),
            ExportToScore, "IconSend");
        var import = ToolUi.Button(Loc.S("Importar", "Import"),
            Loc.S("Carga el patrón guardado en el bloque del secuenciador del documento (Ctrl+Z lo deshace).", "Loads the pattern stored in the document's step sequencer block (Ctrl+Z undoes it)."),
            ImportFromScore, "IconOpen");
        // No-op click action: the flyout needs the button itself as its anchor.
        var more = ToolUi.Button("⋯", Loc.S("Más: patrón por defecto, aleatorio, borrar, agregar pista, p4/p5, deshacer.", "More: default groove, random, clear, add track, p4/p5, undo."), () => { });
        more.Click += (_, _) => ShowMoreMenu(more);

        // Tool bar row (WrapPanel: wraps instead of clipping on narrow windows).
        var row1 = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 2) };
        foreach (var c in new Control[]
        {
            playButton, ToolUi.Captioned(Loc.S("Tempo", "Tempo"), tempo),
            ToolUi.Captioned(Loc.S("Swing", "Swing"), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { swing, swingText } }),
            ToolUi.Captioned(Loc.S("Pasos", "Steps"), stepsPicker), ToolUi.Separator(),
            kit, ToolUi.Separator(), export, ToolUi.Captioned(Loc.S("Vueltas", "Loops"), loops), import, more,
        })
        {
            c.Margin = new Thickness(0, 2, 8, 2);
            row1.Children.Add(c);
        }
        ToolTip.SetTip(docLabel, Loc.S("Documento al que apunta el secuenciador: el que suena o, con Csound detenido, el del editor.", "Document the sequencer addresses: the one playing or, with Csound stopped, the one in the editor."));
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(12, 0, 12, 6), Children = { docLabel } };

        // ---- headers + grid in one vertical scroll viewer
        // Both columns scroll together, so header row i always lines up with grid row i. Column-title
        // widths match the control widths used in BuildHeader (92, 128, 52+34+spacing, 52, 62, 30+30).
        var columnTitles = new StackPanel
        {
            Orientation = Orientation.Horizontal, Height = StepGridView.HeaderHeight, Spacing = 4,
            Children =
            {
                ColumnTitle(Loc.S("Pista", "Track"), 92), ColumnTitle(Loc.S("Instrumento", "Instrument"), 128), ColumnTitle(Loc.S("Nota", "Note"), 90),
                ColumnTitle(Loc.S("Vel.", "Vel."), 52), ColumnTitle(Loc.S("Prob.", "Prob."), 62), ColumnTitle("M  S", 64),
            },
        };
        headers.Children.Add(columnTitles);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(10, 0, 10, 0) };
        body.Children.Add(headers);
        Grid.SetColumn(grid, 1);
        grid.Margin = new Thickness(8, 0, 0, 0);
        grid.VerticalAlignment = VerticalAlignment.Top;
        body.Children.Add(grid);
        // Horizontal scrolling disabled: the grid stretches its cells to the available width instead
        // (its desired height = header + one row per track, so the vertical bar appears with many tracks).
        var scroller = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        ToolTip.SetTip(grid, Loc.S("Clic = encender/apagar un paso (arrastra para pintar varios) · Shift+arrastrar arriba/abajo o rueda = velocidad del paso · clic derecho = acento.",
                                   "Click = step on/off (drag to paint several) · Shift+drag up/down or wheel = step velocity · right-click = accent."));
        ToolTip.SetShowDelay(grid, 1500);

        var addTrack = ToolUi.Button(Loc.S("+ Pista", "+ Track"), Loc.S("Agrega una pista (instrumento 1, Do central). Máximo 16.", "Adds a track (instrument 1, middle C). Up to 16."), AddTrack);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 10, 0), Children = { addTrack } };

        var statusBar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 6, 12, 8) };
        statusBar.Children.Add(status);
        Grid.SetColumn(message, 1);
        statusBar.Children.Add(message);
        ToolTip.SetTip(status, Loc.S("Lo que hay bajo el mouse.", "What is under the mouse."));
        ToolTip.SetTip(message, Loc.S("Estado del transporte y de Csound.", "Transport and Csound status."));

        // Dock order: rows on top, status bar at the very bottom with the footer above it, scroller fills the rest.
        var root = new DockPanel();
        DockPanel.SetDock(row1, Dock.Top);
        DockPanel.SetDock(row2, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(row1);
        root.Children.Add(row2);
        root.Children.Add(statusBar);
        root.Children.Add(footer);
        root.Children.Add(scroller);
        Content = root;

        // ---- wiring
        // One checkpoint per mouse gesture (a whole painted stroke is a single undo step).
        grid.EditStarting += Checkpoint;
        grid.Edited += Edited;
        grid.Hover += t => status.Text = t;
        // Audition a step when it is switched on (only audible while Csound plays).
        grid.StepSwitchedOn += (row, step) => AuditionTrack(pattern.Tracks[row], pattern.Tracks[row].Steps[step]);
        transport.StateChanged += UpdatePlayButton;
        transport.Message += ShowMessage;
        transport.TempoFollowed += bpm =>
        {
            // Ableton Link changed the tempo: show it without proposing it back to the session.
            syncing = true;
            pattern.Bpm = bpm;
            tempo.Value = (decimal)Math.Clamp(Math.Round(bpm, 2), 20, 400);
            syncing = false;
        };
        // Playhead: timeline beat → highlighted step column (-1 = none).
        transport.Tick += beat => grid.CurrentStep = beat is { } b ? StepAt(b) : -1;
        vm.PropertyChanged += OnViewModelChanged;
        docTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) => RefreshTarget());
        docTimer.Start();
        // Tunnel: the window handles Space / Ctrl+Z / Ctrl+Y before focused buttons or combo boxes do.
        AddHandler(KeyDownEvent, OnWindowKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            RefreshTarget(); // must run first: AutoImport needs the target document
            AutoImportOnFirstOpen();
            RebuildHeaders();
            ShowMessage(vm.IsPlayingLive ? vm.ToolEngineStatus
                : Loc.S("Para sonar: «Abrir kit de batería» y luego ▶ (inicia Csound con el kit).", "To hear it: «Open drum kit», then ▶ (starts Csound with the kit)."), false);
        };
        // Closing stops only this tool's transport; the pattern stays in the view model for the session.
        Closed += (_, _) =>
        {
            transport.Stop();
            transport.Dispose();
            docTimer.Stop();
            vm.PropertyChanged -= OnViewModelChanged;
            // Bug fix: the TextChanged subscription on the watched document (made in RefreshTarget)
            // was never removed, so the closed window stayed alive through that long-lived document.
            if (watchedDoc is not null) watchedDoc.Document.TextChanged -= OnDocumentTextChanged;
            watchedDoc = null;
        };
        RebuildHeaders();
        UpdatePlayButton();
    }

    /// <summary>A small, faint column title above the track header controls.</summary>
    /// <param name="text">Title text.</param>
    /// <param name="width">Width in DIP; must match the controls below it in <see cref="BuildHeader"/>.</param>
    /// <returns>The title text block.</returns>
    static TextBlock ColumnTitle(string text, double width) =>
        new() { Text = text, Width = width, FontSize = 11, Foreground = AppTheme.Brush("TextFaint"), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Step being heard at timeline beat <paramref name="beat"/> (swing-aware).</summary>
    /// <param name="beat">Timeline beat from the transport (already folded into the pattern loop).</param>
    /// <returns>Step index 0 … StepCount − 1.</returns>
    int StepAt(double beat)
    {
        // Straight grid position first (StepBeats = a sixteenth = 0.25 beat)...
        int s = (int)Math.Floor(beat / StepPattern.StepBeats);
        // ...then, if swing delays this step and its swung start has not arrived yet, the previous step is still sounding.
        if (s > 0 && beat < pattern.StepStart(s)) s--;
        return Math.Clamp(s, 0, pattern.StepCount - 1);
    }

    // ------------------------------------------------------------------------------------------
    //  Track headers
    // ------------------------------------------------------------------------------------------

    /// <summary>Recreates the per-track controls (after adding/removing tracks or loading a pattern).</summary>
    void RebuildHeaders()
    {
        // Keep child 0 (the column titles); drop every track row.
        while (headers.Children.Count > 1) headers.Children.RemoveAt(1);
        instrumentPickers.Clear();
        for (int i = 0; i < pattern.Tracks.Count; i++)
            headers.Children.Add(BuildHeader(pattern.Tracks[i], i));
        grid.InvalidateMeasure();
        grid.InvalidateVisual();
    }

    /// <summary>
    /// Builds one track's header row: name, instrument, MIDI note (+ note name), default velocity,
    /// probability, Mute, Solo and the per-track «⋯» menu.
    /// </summary>
    /// <param name="track">The track the controls edit directly (no binding: handlers write its fields).</param>
    /// <param name="index">Row index, used by «Remove the track» (rows are rebuilt after removal, so it stays valid).</param>
    /// <returns>A horizontal row exactly <see cref="StepGridView.RowHeight"/> tall, aligned with the grid row.</returns>
    /// <remarks>
    /// Bug fix: the inline controls (name, instrument, note, probability, M/S) used to edit the track
    /// without an undo checkpoint, so Ctrl+Z skipped them (and undid the previous grid edit instead).
    /// They now go through <see cref="HeaderCheckpoint"/>, which coalesces a burst of changes on one
    /// control (typing a name, spinning a number) into a single undo step. Their handlers also respect
    /// <see cref="syncing"/>, like every other programmatic-change guard in this window.
    /// The default velocity box is not an undoable edit: it only affects steps switched on later.
    /// </remarks>
    Control BuildHeader(StepTrack track, int index)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Height = StepGridView.RowHeight, VerticalAlignment = VerticalAlignment.Center };

        var name = new TextBox { Text = track.Name, Width = 92, MinWidth = 92, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Padding = new Thickness(6, 3), MinHeight = 28 };
        ToolTip.SetTip(name, Loc.S("Nombre de la pista (solo para ti; se guarda con el patrón).", "Track name (just for you; stored with the pattern)."));
        name.TextChanged += (_, _) =>
        {
            if (syncing) return;
            HeaderCheckpoint(name);
            track.Name = name.Text ?? ""; // cosmetic: no snapshot needed
        };

        var instr = new ComboBox { Width = 128, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, MinHeight = 28 };
        ToolTip.SetTip(instr, Loc.S("Instrumento que toca esta pista (p1). Con el kit: 1 bombo, 2 caja, 3 hi-hat cerrado, 4 abierto, 5 palmas, 6 tom, 7 aro, 8 cencerro.",
                                    "Instrument this track plays (p1). With the kit: 1 kick, 2 snare, 3 closed hat, 4 open hat, 5 clap, 6 tom, 7 rim, 8 cowbell."));
        instrumentPickers.Add(instr);
        FillInstrumentPicker(instr, track);
        instr.SelectionChanged += (_, _) =>
        {
            if (syncing || instr.SelectedItem is not InstrumentChoice c) return;
            HeaderCheckpoint(instr);
            track.Instrument = c.Id;
            Edited();
        };

        var noteName = new TextBlock { Text = PFields.NoteName(track.Note), Width = 34, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = AppTheme.Brush("TextDim") };
        var note = ToolUi.Number(0, 127, track.Note, 1, Loc.S("Nota MIDI que se envía en p4 (60 = Do central = C4). En el kit afina el tambor (el tom la usa como altura).", "MIDI note sent as p4 (60 = middle C = C4). In the kit it tunes the drum (the tom uses it as its pitch). Type it or use ↑/↓ / the wheel."), "0", 52, spinner: false);
        note.ValueChanged += (_, e) =>
        {
            // syncing guard (bug fix): programmatic value changes must not count as user edits.
            if (syncing || e.NewValue is not { } v) return;
            HeaderCheckpoint(note);
            track.Note = (int)v;
            noteName.Text = PFields.NoteName(track.Note);
            Edited();
        };
        ToolTip.SetTip(noteName, Loc.S("Nombre de la nota (60 = C4 = Do central; Cubase la llama C3).", "Note name (60 = C4 = middle C; Cubase calls it C3)."));

        var vel = ToolUi.Number(1, 127, track.Velocity, 1, Loc.S("Velocidad que recibe un paso al encenderlo (1…127). Cada paso puede tener la suya: Shift+arrastrar o rueda sobre el paso.", "Velocity a step gets when switched on (1…127). Each step can have its own: Shift+drag or wheel over the step."), "0", 52, spinner: false);
        // Default velocity for newly switched-on steps only: existing steps keep theirs, so playback is unchanged.
        vel.ValueChanged += (_, e) => { if (e.NewValue is { } v) track.Velocity = (int)v; };

        var prob = ToolUi.Number(0, 100, track.Probability, 5, Loc.S("Probabilidad (%) de que cada paso encendido suene en cada vuelta: menos de 100 hace el ritmo más vivo.", "Probability (%) that each active step plays on each pass: below 100 makes the rhythm more alive."), "0'%'", 62, spinner: false);
        prob.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) { HeaderCheckpoint(prob); track.Probability = (int)v; Edited(); } };

        var mute = new ToggleButton { Content = "M", IsChecked = track.Mute, Width = 30, Padding = new Thickness(0, 4), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(mute, Loc.S("Silenciar esta pista.", "Mute this track."));
        mute.IsCheckedChanged += (_, _) => { if (syncing) return; HeaderCheckpoint(mute); track.Mute = mute.IsChecked == true; Edited(); };
        var solo = new ToggleButton { Content = "S", IsChecked = track.Solo, Width = 30, Padding = new Thickness(0, 4), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(solo, Loc.S("Solo: si alguna pista está en solo, solo suenan las pistas en solo.", "Solo: when any track is soloed, only soloed tracks play."));
        solo.IsCheckedChanged += (_, _) => { if (syncing) return; HeaderCheckpoint(solo); track.Solo = solo.IsChecked == true; Edited(); };

        var menu = new Button { Content = "⋯", Classes = { "tool" }, Padding = new Thickness(8, 4), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(menu, Loc.S("Relleno euclidiano, aleatorio, rotar, borrar, escuchar o quitar la pista.", "Euclidean or random fill, rotate, clear, audition or remove the track."));
        menu.Click += (_, _) => ShowTrackMenu(menu, track, index);

        row.Children.Add(name);
        row.Children.Add(instr);
        row.Children.Add(note);
        row.Children.Add(noteName);
        row.Children.Add(vel);
        row.Children.Add(prob);
        row.Children.Add(mute);
        row.Children.Add(solo);
        row.Children.Add(menu);
        return row;
    }

    /// <summary>
    /// Fills one track's instrument combo box with the document's instruments, adding the track's
    /// current instrument flagged «not in the document» when missing (so it is never silently changed).
    /// </summary>
    /// <param name="picker">The combo box to fill.</param>
    /// <param name="track">The track whose instrument must be selected.</param>
    void FillInstrumentPicker(ComboBox picker, StepTrack track)
    {
        var list = instruments.ToList(); // a copy per picker: the insertion below must not leak into the shared list
        if (!list.Any(c => c.Id == track.Instrument))
            list.Insert(0, new InstrumentChoice(track.Instrument, Loc.S("no está en el documento", "not in the document")));
        syncing = true;
        picker.ItemsSource = list;
        picker.SelectedItem = list.First(c => c.Id == track.Instrument);
        syncing = false;
    }

    /// <summary>Opens the per-track «⋯» flyout: Euclidean fills E(k, steps), random fill, rotate, clear, audition, remove.</summary>
    /// <param name="anchor">The track's «⋯» button.</param>
    /// <param name="track">The track the actions apply to.</param>
    /// <param name="index">The track's row index (for removal).</param>
    void ShowTrackMenu(Control anchor, StepTrack track, int index)
    {
        var menu = new MenuFlyout();
        var euclid = new MenuItem { Header = Loc.S("Relleno euclidiano", "Euclidean fill") };
        ToolTip.SetTip(euclid, Loc.S("Reparte k golpes lo más parejo posible en los pasos (E(3,8) = tresillo cubano, E(4,16) = bombo en negras).", "Spreads k hits as evenly as possible over the steps (E(3,8) = Cuban tresillo, E(4,16) = four on the floor)."));
        for (int k = 1; k <= Math.Min(16, pattern.StepCount); k++)
        {
            int hits = k; // per-iteration copy for the click closure
            var item = new MenuItem { Header = $"{hits} " + Loc.S("golpes", "hits") + $"  ·  E({hits},{pattern.StepCount})" };
            item.Click += (_, _) =>
            {
                Checkpoint();
                var on = StepPattern.Euclid(hits, pattern.StepCount);
                // Steps that stay on keep their own velocity; newly lit ones get the track's default.
                for (int s = 0; s < pattern.StepCount; s++) track.Steps[s] = on[s] ? (track.Steps[s] > 0 ? track.Steps[s] : track.Velocity) : 0;
                Edited();
            };
            euclid.Items.Add(item);
        }
        menu.Items.Add(euclid);
        void Add(string header, Action action, string? tip = null)
        {
            var item = new MenuItem { Header = header };
            if (tip is not null) ToolTip.SetTip(item, tip);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add(Loc.S("Aleatorio (≈ 30 %)", "Random (≈ 30 %)"), () => { Checkpoint(); RandomFill(track, 0.3); Edited(); });
        Add(Loc.S("Rotar → (un paso a la derecha)", "Rotate → (one step right)"), () => { Checkpoint(); Rotate(track, 1); Edited(); });
        Add(Loc.S("Rotar ← (un paso a la izquierda)", "Rotate ← (one step left)"), () => { Checkpoint(); Rotate(track, -1); Edited(); });
        Add(Loc.S("Borrar los pasos", "Clear the steps"), () => { Checkpoint(); Array.Clear(track.Steps); Edited(); });
        Add(Loc.S("Escuchar la pista", "Audition the track"), () => AuditionTrack(track, track.Velocity));
        menu.Items.Add(new Separator());
        Add(Loc.S("Quitar la pista", "Remove the track"), () =>
        {
            if (pattern.Tracks.Count <= 1) return; // always keep at least one track
            Checkpoint();
            pattern.Tracks.RemoveAt(index);
            RebuildHeaders();
            Edited();
        });
        menu.ShowAt(anchor);
    }

    /// <summary>Randomly switches steps on with probability <paramref name="density"/>; the rest are cleared.</summary>
    /// <param name="track">Track to fill (only the first StepCount steps are touched).</param>
    /// <param name="density">Probability 0…1 that a step is on.</param>
    /// <remarks>Velocities vary around the track's default (−20…+14), clamped to 30–127 so every hit is audible.</remarks>
    void RandomFill(StepTrack track, double density)
    {
        var rng = new Random();
        for (int s = 0; s < pattern.StepCount; s++)
            track.Steps[s] = rng.NextDouble() < density ? Math.Clamp(track.Velocity + rng.Next(-20, 15), 30, 127) : 0;
    }

    /// <summary>Rotates the active steps of a track circularly.</summary>
    /// <param name="track">Track to rotate (steps beyond StepCount are left alone).</param>
    /// <param name="by">Steps to the right (negative = left).</param>
    void Rotate(StepTrack track, int by)
    {
        int n = pattern.StepCount;
        var copy = track.Steps.Take(n).ToArray();
        // ((x % n) + n) % n: a true modulo, so negative shifts wrap correctly.
        for (int s = 0; s < n; s++) track.Steps[((s + by) % n + n) % n] = copy[s];
    }

    /// <summary>«+ Track»: appends a track (first instrument of the document, else «1»; middle C, velocity 100). Max 16 tracks.</summary>
    void AddTrack()
    {
        if (pattern.Tracks.Count >= 16) return;
        Checkpoint();
        string instr = instruments.FirstOrDefault()?.Id ?? "1";
        pattern.Tracks.Add(new StepTrack { Name = Loc.S($"Pista {pattern.Tracks.Count + 1}", $"Track {pattern.Tracks.Count + 1}"), Instrument = instr, Note = 60, Velocity = 100 });
        RebuildHeaders();
        Edited();
    }

    /// <summary>Plays one note of a track right away (only while Csound plays).</summary>
    /// <param name="track">Track whose instrument, note and p4/p5 mapping are used.</param>
    /// <param name="velocity">MIDI velocity (0 is raised to 1).</param>
    /// <remarks>Duration = one sixteenth at the current tempo (seconds = 0.25 beat × 60 / BPM), like playback.</remarks>
    void AuditionTrack(StepTrack track, int velocity)
    {
        if (!vm.IsPlayingLive) return;
        string fields = PFields.Pitch(track.Note, pattern.PitchMapping) + " " + PFields.Velocity(Math.Max(1, velocity), pattern.VelocityMapping);
        vm.Engine.SendEvent($"i {PFields.InstrumentToken(track.Instrument)} 0 {PFields.Num(StepPattern.StepBeats * 60 / pattern.Bpm)} {fields}");
    }

    // ------------------------------------------------------------------------------------------
    //  Edits, undo
    // ------------------------------------------------------------------------------------------

    /// <summary>After any change: give the scheduler a fresh copy and repaint.</summary>
    /// <remarks>
    /// Also sets <see cref="MainViewModel.SequencerEdited"/>, which stops a later window opening from
    /// auto-importing the document's block over the user's work (<see cref="AutoImportOnFirstOpen"/>).
    /// </remarks>
    void Edited()
    {
        vm.SequencerEdited = true;
        transport.Source = pattern.Clone();
        grid.InvalidateVisual();
    }

    /// <summary>Saves a clone of the current pattern as an undo step (history capped at 100) and clears redo.</summary>
    void Checkpoint()
    {
        undo.Add(pattern.Clone());
        if (undo.Count > 100) undo.RemoveAt(0);
        redo.Clear();
        // Any other checkpoint ends a header-edit burst, so the next header edit gets its own step.
        lastHeaderEditSource = null;
    }

    /// <summary>Header control that took the latest coalesced checkpoint (see <see cref="HeaderCheckpoint"/>).</summary>
    object? lastHeaderEditSource;
    /// <summary><see cref="Environment.TickCount64"/> (ms) of the latest header edit.</summary>
    long lastHeaderEditTicks;

    /// <summary>
    /// Undo checkpoint for an inline track-header edit, coalesced: a new step is taken when the edit
    /// comes from a different control than the previous one, or after more than a second of no
    /// header edits. Typing a name or spinning a number box is therefore one undo step.
    /// </summary>
    /// <param name="source">The header control being edited.</param>
    void HeaderCheckpoint(object source)
    {
        long now = Environment.TickCount64;
        if (!ReferenceEquals(source, lastHeaderEditSource) || now - lastHeaderEditTicks > 1000)
            Checkpoint();
        lastHeaderEditSource = source;
        lastHeaderEditTicks = now;
    }

    /// <summary>Ctrl+Z: pushes the current pattern to redo and loads the latest undo clone.</summary>
    void Undo()
    {
        if (undo.Count == 0) return;
        redo.Add(pattern.Clone());
        Load(undo[^1]);
        undo.RemoveAt(undo.Count - 1);
    }

    /// <summary>Ctrl+Y / Ctrl+Shift+Z: the inverse of <see cref="Undo"/>.</summary>
    void Redo()
    {
        if (redo.Count == 0) return;
        undo.Add(pattern.Clone());
        Load(redo[^1]);
        redo.RemoveAt(redo.Count - 1);
    }

    /// <summary>Makes <paramref name="p"/> the edited pattern and refreshes every control.</summary>
    /// <param name="p">The new pattern instance (an undo clone, an imported block or the default groove).</param>
    /// <remarks>The instance replaces <see cref="MainViewModel.Sequencer"/> too, so it survives closing the window.</remarks>
    void Load(StepPattern p)
    {
        pattern = p;
        vm.Sequencer = p;
        grid.Pattern = p;
        syncing = true;
        tempo.Value = (decimal)p.Bpm;
        swing.Value = Math.Round(p.Swing * 100);
        swingText.Text = $"{swing.Value:0} %";
        stepsPicker.SelectedIndex = Math.Max(0, Array.IndexOf(StepChoices, p.StepCount));
        loops.Value = p.ExportLoops;
        syncing = false;
        transport.Bpm = p.Bpm;
        RebuildHeaders();
        Edited();
    }

    /// <summary>
    /// The window's «⋯» flyout, rebuilt on every open: undo/redo, example groove, random/clear all,
    /// clear mute/solo, p4 and p5 mapping submenus (current choice marked ✓) and «remove block».
    /// </summary>
    /// <param name="anchor">The «⋯» button.</param>
    void ShowMoreMenu(Control anchor)
    {
        var menu = new MenuFlyout();
        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add(Loc.S("Deshacer (Ctrl+Z)", "Undo (Ctrl+Z)"), Undo, undo.Count > 0);
        Add(Loc.S("Rehacer (Ctrl+Y)", "Redo (Ctrl+Y)"), Redo, redo.Count > 0);
        menu.Items.Add(new Separator());
        Add(Loc.S("Patrón de ejemplo (kit de batería)", "Example groove (drum kit)"), () => { Checkpoint(); Load(StepPattern.DefaultGroove(Loc.IsEnglish)); });
        Add(Loc.S("Aleatorio en todas las pistas", "Random fill on every track"), () =>
        {
            Checkpoint();
            // Denser on hi-hat tracks (GM notes 42 closed / 46 open hat), sparser elsewhere.
            foreach (var t in pattern.Tracks) RandomFill(t, t.Note is 42 or 46 ? 0.45 : 0.22);
            Edited();
        });
        Add(Loc.S("Borrar todos los pasos", "Clear every step"), () => { Checkpoint(); foreach (var t in pattern.Tracks) Array.Clear(t.Steps); Edited(); });
        Add(Loc.S("Quitar mute y solo", "Clear mute and solo"), () => { Checkpoint(); foreach (var t in pattern.Tracks) { t.Mute = false; t.Solo = false; } RebuildHeaders(); Edited(); });
        menu.Items.Add(new Separator());
        var p4 = new MenuItem { Header = Loc.S("p4 (nota) se escribe como…", "p4 (note) is written as…") };
        string[] p4Names = [Loc.S("nota MIDI (60)", "MIDI note (60)"), "Hz (261.63)", "cpspch (8.00)", "oct (8.0)"];
        // Item order must match the PitchMapping / VelocityMapping enums (index cast to the enum).
        for (int i = 0; i < 4; i++)
        {
            var mapping = (PitchMapping)i;
            var item = new MenuItem { Header = (pattern.PitchMapping == mapping ? "✓ " : "    ") + p4Names[i] };
            item.Click += (_, _) => { pattern.PitchMapping = mapping; Edited(); };
            p4.Items.Add(item);
        }
        menu.Items.Add(p4);
        var p5 = new MenuItem { Header = Loc.S("p5 (velocidad) se escribe como…", "p5 (velocity) is written as…") };
        string[] p5Names = ["0…1", "0…127"];
        for (int i = 0; i < 2; i++)
        {
            var mapping = (VelocityMapping)i;
            var item = new MenuItem { Header = (pattern.VelocityMapping == mapping ? "✓ " : "    ") + p5Names[i] };
            item.Click += (_, _) => { pattern.VelocityMapping = mapping; Edited(); };
            p5.Items.Add(item);
        }
        menu.Items.Add(p5);
        menu.Items.Add(new Separator());
        Add(Loc.S("Quitar el bloque del secuenciador del documento", "Remove the step sequencer block from the document"), RemoveBlock,
            vm.ToolDocument is { } d && ScoreBlocks.ReadBlock(d.Document.Text, ScoreBlocks.StepSequencer) is not null);
        menu.ShowAt(anchor);
    }

    // ------------------------------------------------------------------------------------------
    //  Target document, transport, score
    // ------------------------------------------------------------------------------------------

    /// <summary>Re-targets after the selected tab or run mode changed (posted so the view model finishes updating first).</summary>
    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedDocument) or nameof(MainViewModel.Mode))
            Dispatcher.UIThread.Post(RefreshTarget, DispatcherPriority.Background);
    }

    /// <summary>Flags the target's text as changed; the instrument list is re-parsed lazily by <see cref="RefreshTarget"/>.</summary>
    void OnDocumentTextChanged(object? sender, EventArgs e) => docTextChanged = true;

    /// <summary>
    /// Follows the target document (moving the <c>TextChanged</c> subscription), updates the
    /// document label and, when its text changed, re-reads the instruments and refills every
    /// track's picker.
    /// </summary>
    void RefreshTarget()
    {
        var doc = vm.ToolDocument;
        if (doc != watchedDoc)
        {
            if (watchedDoc is not null) watchedDoc.Document.TextChanged -= OnDocumentTextChanged;
            watchedDoc = doc;
            if (doc is not null) doc.Document.TextChanged += OnDocumentTextChanged;
            docTextChanged = true;
        }
        docLabel.Text = doc is null ? Loc.S("Sin documento", "No document")
            : Loc.S("Documento: ", "Document: ") + doc.FileName + (vm.PlayingDocument == doc ? Loc.S("  (sonando)", "  (playing)") : "");
        if (!docTextChanged) return;
        docTextChanged = false;
        instruments = MainViewModel.InstrumentsOf(doc);
        for (int i = 0; i < instrumentPickers.Count && i < pattern.Tracks.Count; i++)
            FillInstrumentPicker(instrumentPickers[i], pattern.Tracks[i]);
    }

    /// <summary>
    /// When the session's pattern has not been edited yet and the target document contains a step
    /// sequencer block, loads it (reopening a saved piece restores its pattern).
    /// </summary>
    void AutoImportOnFirstOpen()
    {
        if (vm.SequencerEdited || vm.ToolDocument is not { } doc) return;
        if (ScoreBlocks.ImportSequencer(doc.Document.Text) is { } p)
        {
            Load(p);
            vm.SequencerEdited = false; // Load → Edited() set it; an automatic import is not a user edit
            ShowMessage(Loc.S($"Patrón cargado del bloque del secuenciador de {doc.FileName}.", $"Pattern loaded from the step sequencer block of {doc.FileName}."), false);
        }
    }

    /// <summary>
    /// ▶/■ and Space. Before starting, checks which instruments the audible, non-empty tracks use
    /// against the target document: warns about missing ones and refuses to start when none exists
    /// (typically: the drum kit is not open). The pattern always starts at beat 0 and loops.
    /// </summary>
    void TogglePlay()
    {
        if (transport.IsPlaying)
        {
            transport.Stop();
            grid.CurrentStep = -1;
            return;
        }
        var doc = vm.ToolDocument;
        if (doc is not null)
        {
            // Instruments the document defines vs. the ones the pattern would actually trigger.
            var have = MainViewModel.InstrumentsOf(doc).Select(c => c.Id).ToHashSet();
            var used = pattern.Tracks.Where(t => pattern.IsAudible(t) && t.Steps.Take(pattern.StepCount).Any(v => v > 0)).Select(t => t.Instrument).Distinct().ToList();
            var missing = used.Where(i => !have.Contains(i)).ToList();
            if (missing.Count > 0)
            {
                ShowMessage(Loc.S($"⚠ {doc.FileName} no tiene los instrumentos {string.Join(", ", missing)}. ¿Abriste el kit de batería? («Abrir kit de batería»)",
                                  $"⚠ {doc.FileName} has no instrument {string.Join(", ", missing)}. Did you open the drum kit? («Open drum kit»)"), true);
                // Nothing of the pattern exists in this document: playing would only print errors.
                if (missing.Count == used.Count) return;
            }
        }
        transport.Source = pattern.Clone();
        transport.Bpm = pattern.Bpm;
        transport.Play(doc, 0);
    }

    /// <summary>Shows Play or Stop on the button and clears the playhead column when stopped.</summary>
    void UpdatePlayButton()
    {
        bool playing = transport.IsPlaying;
        playButton.Content = ToolUi.Content(playing ? Loc.S("Detener", "Stop") : Loc.S("Tocar", "Play"), playing ? "IconStop" : "IconPlay");
        if (!playing) grid.CurrentStep = -1;
    }

    /// <summary>Shows a message on the right of the status line (and as its tooltip, since it may be trimmed).</summary>
    /// <param name="text">Message (already localised).</param>
    /// <param name="warning">True to use the Warn colour.</param>
    void ShowMessage(string text, bool warning)
    {
        message.Text = text;
        message.Foreground = warning ? AppTheme.Brush("Warn") : AppTheme.Brush("TextDim");
        ToolTip.SetTip(message, text);
    }

    /// <summary>
    /// «Open drum kit»: stops the transport and opens the embedded <c>Assets/DrumKit.csd</c> in a tab
    /// via <see cref="MainViewModel.OpenDrumKit"/> (which switches a running Csound to it), then
    /// re-targets so the tracks find instruments 1–8.
    /// </summary>
    void OpenKit()
    {
        bool wasPlaying = transport.IsPlaying;
        transport.Stop();
        var doc = vm.OpenDrumKit();
        docTextChanged = true;
        RefreshTarget();
        ShowMessage(doc is null ? Loc.S("No se pudo abrir el kit.", "Could not open the kit.")
            : wasPlaying || vm.IsRunning
                ? Loc.S("Kit de batería abierto; Csound cambia al kit. Presiona ▶.", "Drum kit opened; Csound switches to the kit. Press ▶.")
                : Loc.S("Kit de batería abierto en una pestaña. Presiona ▶ (inicia Csound con el kit).", "Drum kit opened in a tab. Press ▶ (starts Csound with the kit)."), false);
    }

    /// <summary>
    /// «Export to score»: writes the pattern, repeated <see cref="StepPattern.ExportLoops"/> times
    /// (swing, mute/solo applied; times in seconds), as a marked block in the target's
    /// <c>&lt;CsScore&gt;</c> together with a <c>;@seq</c> header that stores the pattern for re-import.
    /// </summary>
    void ExportToScore()
    {
        if (vm.ToolDocument is not { } doc)
        {
            ShowMessage(Loc.S("No hay documento abierto.", "No document is open."), true);
            return;
        }
        string block = ScoreBlocks.BuildSequencerBlock(pattern);
        if (!vm.WriteScoreBlock(doc, ScoreBlocks.StepSequencer, block))
        {
            ShowMessage(Loc.S("El documento no es un .csd (no tiene <CsoundSynthesizer>).", "The document is not a .csd (no <CsoundSynthesizer>)."), true);
            return;
        }
        // Summary for the message: count the «i» lines and the exported duration (beats → seconds).
        int notes = block.Split('\n').Count(l => l.StartsWith("i ", StringComparison.Ordinal));
        double seconds = pattern.ExportLoops * pattern.LengthBeats * 60 / pattern.Bpm;
        string text = Loc.S($"✓ Patrón exportado a {doc.FileName}: {pattern.ExportLoops} vueltas, {notes} notas, {seconds:0.0} s. Guarda el archivo para conservarlo.",
                            $"✓ Pattern exported to {doc.FileName}: {pattern.ExportLoops} loops, {notes} notes, {seconds:0.0} s. Save the file to keep it.");
        ShowMessage(text, false);
        vm.ToolInfo("🥁 " + text);
    }

    /// <summary>«Import»: loads the pattern stored in the target's step sequencer block (undoable with Ctrl+Z).</summary>
    void ImportFromScore()
    {
        if (vm.ToolDocument is not { } doc)
        {
            ShowMessage(Loc.S("No hay documento abierto.", "No document is open."), true);
            return;
        }
        if (ScoreBlocks.ImportSequencer(doc.Document.Text) is not { } p)
        {
            ShowMessage(Loc.S($"{doc.FileName} no tiene un bloque del secuenciador (se crea con «Exportar a partitura»).", $"{doc.FileName} has no step sequencer block (it is created by «Export to score»)."), true);
            return;
        }
        Checkpoint();
        Load(p);
        ShowMessage(Loc.S($"✓ Patrón cargado de {doc.FileName} ({p.Tracks.Count} pistas).", $"✓ Pattern loaded from {doc.FileName} ({p.Tracks.Count} tracks)."), false);
    }

    /// <summary>Removes the step sequencer block from the target document (one editor undo step); the pattern stays.</summary>
    void RemoveBlock()
    {
        if (vm.ToolDocument is not { } doc || ScoreBlocks.MakeRemoveEdit(doc.Document.Text, ScoreBlocks.StepSequencer) is not { } edit) return;
        doc.Document.Replace(edit.Offset, edit.Length, edit.Text);
        ShowMessage(Loc.S("Bloque del secuenciador quitado (Ctrl+Z en el editor lo devuelve).", "Step sequencer block removed (Ctrl+Z in the editor brings it back)."), false);
    }

    /// <summary>
    /// Window-level shortcuts (tunnelling): Space = play/stop, Ctrl+Z = undo, Ctrl+Y or Ctrl+Shift+Z =
    /// redo — all ignored while a text box (e.g. a track name) has the focus, so typing still works.
    /// </summary>
    void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        bool typing = FocusManager?.GetFocusedElement() is TextBox;
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && !typing)
        {
            TogglePlay();
            e.Handled = true;
        }
        else if (!typing && e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (!typing && (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Y || e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.Z)) { Redo(); e.Handled = true; }
    }
}
