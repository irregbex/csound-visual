using System.Collections.Concurrent;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CsoundVisual.Controls;
using CsoundVisual.Localization;
using CsoundVisual.Midi;
using CsoundVisual.Music;
using CsoundVisual.Theming;
using CsoundVisual.ViewModels;

namespace CsoundVisual.Views;

/// <summary>
/// Piano roll window (Tools › Piano roll, Ctrl+P): draws notes on a pitch × time grid and plays
/// them live through Csound, or writes them into the document's score. Built in code.
/// </summary>
/// <remarks>
/// <para>The notes live in <see cref="MainViewModel.PianoRoll"/>, so closing and reopening the
/// window keeps them for the session; «Insert into score» stores them in the document (inside a
/// marked block) so they are saved with the file and restored next time («Import»).</para>
/// <para>Playback goes through <see cref="ToolTransport"/> (look-ahead scheduler on Csound's score
/// clock); each note becomes <c>i &lt;instr&gt; &lt;offset&gt; &lt;dur&gt; &lt;p4&gt; &lt;p5&gt;</c> with p4/p5 mapped
/// as chosen. With «Record MIDI» on, notes played on a MIDI keyboard (or the on-screen one) while
/// the transport runs are added to the roll, snapped to the grid.</para>
/// <para><b>Layout</b> (a <see cref="DockPanel"/>, top to bottom): a wrapping tool bar (transport,
/// tempo, grid, target instrument and p4/p5 mapping, Record MIDI, Insert/Import, «⋯» menu); a line
/// with the target document and the «changes not inserted» marker; the drawing surface
/// (<see cref="PianoRollView"/> plus external horizontal/vertical scroll bars); and a status line
/// (what is under the mouse on the left, transport messages on the right).</para>
/// <para><b>Why built in code.</b> Like the other tool windows, the content is assembled in C#
/// with <see cref="ToolUi"/> helpers rather than AXAML: most controls need imperative wiring to the
/// model (snapshots, pending marker) and none of them binds to a view-model property directly.</para>
/// <para><b>Target document.</b> <see cref="MainViewModel.ToolDocument"/> = the document Csound is
/// playing, or the one shown in the editor when Csound is stopped. A 700 ms background timer and
/// <see cref="MainViewModel"/> property changes keep the label and instrument list up to date.</para>
/// <para><b>Re-entrancy guard.</b> Whenever code (not the user) writes a control value it sets
/// <see cref="syncing"/>, and every change handler returns early while it is set, so programmatic
/// updates never feed back into the model.</para>
/// <para><b>Threading.</b> MIDI arrives on the driver thread (<see cref="OnMidi"/>) and is queued;
/// everything else, including <see cref="DrainMidi"/>, runs on the UI thread.</para>
/// </remarks>
public sealed class PianoRollWindow : Window
{
    /// <summary>The app's view model (engine, documents, MIDI hub, score-writing helpers).</summary>
    readonly MainViewModel vm;

    /// <summary>The session-wide note model (<see cref="MainViewModel.PianoRoll"/>), shared with <see cref="view"/>; outlives this window.</summary>
    readonly PianoRollModel model;

    /// <summary>The drawn editor surface: keyboard, ruler with loop, note grid and velocity lane.</summary>
    readonly PianoRollView view = new();

    /// <summary>Play/stop logic and look-ahead scheduler fed with <see cref="PianoRollModel.Snapshot"/> copies.</summary>
    readonly ToolTransport transport;

    /// <summary>External horizontal scroll bar; units are timeline beats (synced in <see cref="SyncScrollBars"/>).</summary>
    readonly ScrollBar hScroll = new() { Orientation = Orientation.Horizontal, Height = 12 };

    /// <summary>External vertical scroll bar; units are pixels of the pitch grid.</summary>
    readonly ScrollBar vScroll = new() { Orientation = Orientation.Vertical, Width = 12 };

    /// <summary>▶/■ button; its content swaps between Play and Stop in <see cref="UpdatePlayButton"/>.</summary>
    readonly Button playButton;

    /// <summary>Loop on/off, mirrored to <see cref="PianoRollModel.LoopOn"/>.</summary>
    readonly ToggleButton loopToggle;

    /// <summary>«Record MIDI»: when checked and playing, incoming notes are written into the roll.</summary>
    readonly ToggleButton recordToggle;

    /// <summary>Tempo box (BPM, 20–400), mirrored to <see cref="PianoRollModel.Bpm"/> and the transport.</summary>
    readonly NumericUpDown tempo;

    /// <summary>Grid/snap picker; indexes <see cref="Snaps"/>.</summary>
    readonly ComboBox snapPicker = new() { Width = 96, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Target instrument (p1), filled with <see cref="InstrumentChoice"/> items from the target document.</summary>
    readonly ComboBox instrumentPicker = new() { MinWidth = 150, MaxWidth = 240, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>How the pitch is written into p4; the index is cast to <see cref="PitchMapping"/>.</summary>
    readonly ComboBox pitchPicker = new() { Width = 168, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>How the velocity is written into p5; the index is cast to <see cref="VelocityMapping"/>.</summary>
    readonly ComboBox velocityPicker = new() { Width = 120, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>«Document: x.csd (playing)» — which document the tool addresses.</summary>
    readonly TextBlock docLabel = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("AccentHi") };

    /// <summary>«● changes not inserted into the score» marker (see <see cref="UpdatePending"/>).</summary>
    readonly TextBlock pending = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("Warn") };

    /// <summary>Left part of the status line: hover description from <see cref="PianoRollView.Hover"/>.</summary>
    readonly TextBlock status = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = AppTheme.Brush("TextDim") };

    /// <summary>Right part of the status line: transport / Csound messages (<see cref="ShowMessage"/>).</summary>
    readonly TextBlock message = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };

    /// <summary>700 ms background-priority timer that calls <see cref="RefreshTarget"/> (cheap when nothing changed).</summary>
    readonly DispatcherTimer docTimer;

    /// <summary>
    /// MIDI notes received on the driver thread, each stamped with the Csound score time (seconds)
    /// at arrival; drained on the UI thread by <see cref="DrainMidi"/>.
    /// </summary>
    readonly ConcurrentQueue<(MidiMessage Message, double ScoreTime)> midiIn = new();

    /// <summary>Recorded notes whose note-off has not arrived yet: MIDI note number → (start beat, velocity 1–127).</summary>
    readonly Dictionary<int, (double Beat, int Velocity)> openNotes = [];

    /// <summary>The document whose <c>TextChanged</c> event is currently observed (to refresh the instrument list).</summary>
    DocumentViewModel? watchedDoc;

    /// <summary>Set when the watched document's text changed (or the target changed); makes <see cref="RefreshTarget"/> re-read the instruments.</summary>
    bool docTextChanged = true;
    /// <summary>
    /// Beat of the latest transport tick, kept after the playhead is hidden on stop so
    /// <see cref="CloseOpenNotes"/> can still end held recorded notes at the right place.
    /// </summary>
    double? lastTickBeat;

    /// <summary>Re-entrancy guard: true while code writes control values, so their change handlers ignore the event.</summary>
    bool syncing;

    /// <summary>True once the current MIDI take has created its undo checkpoint (one Ctrl+Z removes the whole take).</summary>
    bool takeStarted;

    /// <summary>Ensures the «Csound must be in Play mode to hear notes» hint is shown only once per window.</summary>
    bool auditionHintShown;

    /// <summary>
    /// Grid choices: label and length in beats (quarter notes). «T» entries are triplets
    /// (2/3 of the straight value); «—» (0) means no snapping. The order is the picker's index.
    /// </summary>
    static readonly (string Label, double Beats)[] Snaps =
    [
        ("1/1", 4), ("1/2", 2), ("1/4", 1), ("1/8", 0.5), ("1/16", 0.25), ("1/32", 0.125),
        ("1/4 T", 2.0 / 3), ("1/8 T", 1.0 / 3), ("1/16 T", 1.0 / 6), ("—", 0),
    ];

    /// <summary>Creates the window for <paramref name="viewModel"/>.</summary>
    /// <param name="viewModel">The main view model; its <see cref="MainViewModel.PianoRoll"/> is edited in place.</param>
    /// <remarks>
    /// Builds the whole UI (sections marked «----» below), wires view/model/transport events and
    /// installs the <c>Opened</c> / <c>Closed</c> handlers that
    /// subscribe to and release the app-wide events (MIDI hub, view-model changes).
    /// </remarks>
    public PianoRollWindow(MainViewModel viewModel)
    {
        vm = viewModel;
        model = vm.PianoRoll;
        Title = "Csound Visual · Piano roll";
        Width = 1240;
        Height = 760;
        MinWidth = 720;
        MinHeight = 420;
        Background = AppTheme.Brush("BgDeep");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        view.Model = model;
        transport = new ToolTransport(vm, model.Snapshot()) { Bpm = model.Bpm };

        // ---- transport
        playButton = ToolUi.Button(Loc.S("Tocar", "Play"),
            Loc.S("Tocar / detener el piano roll (Espacio). Empieza en la línea verde (clic en la regla para moverla). ",
                  "Play / stop the piano roll (Space). Starts at the green line (click the ruler to move it). ") + ToolUi.PlayExplanation,
            TogglePlay, "IconPlay", primary: true);
        playButton.MinWidth = 104;
        loopToggle = ToolUi.Toggle(Loc.S("Loop", "Loop"),
            Loc.S("Repetir la región marcada en la regla (arrastra en la regla para elegirla).", "Repeat the region marked in the ruler (drag in the ruler to set it)."), "IconRefresh");
        loopToggle.IsChecked = model.LoopOn;
        loopToggle.IsCheckedChanged += (_, _) =>
        {
            if (syncing) return;
            model.LoopOn = loopToggle.IsChecked == true;
            PushSnapshot();
            view.InvalidateVisual();
        };
        tempo = ToolUi.Number(20, 400, model.Bpm, 1, Loc.S("Tempo en pulsos (negras) por minuto. Se puede cambiar mientras suena.", "Tempo in beats (quarter notes) per minute. Can be changed while playing."), "0.#", 132);
        tempo.ValueChanged += (_, e) =>
        {
            if (syncing || e.NewValue is not { } v) return;
            model.Bpm = (double)v;
            // Through the Bpm setter: the scheduler re-anchors and, with Ableton Link on, the session follows.
            transport.Bpm = model.Bpm;
            MarkPending(); // the tempo is part of the inserted block (times are written in seconds)
        };

        snapPicker.ItemsSource = Snaps.Select(s => s.Label).ToList();
        snapPicker.SelectedIndex = Math.Max(0, Array.FindIndex(Snaps, s => Math.Abs(s.Beats - model.Snap) < 1e-9));
        ToolTip.SetTip(snapPicker, Loc.S("Cuadrícula: las notas nuevas, movidas y estiradas se ajustan a ella. T = tresillos, — = libre. Q cuantiza la selección.",
                                         "Grid: new, moved and resized notes snap to it. T = triplets, — = free. Q quantizes the selection."));
        snapPicker.SelectionChanged += (_, _) =>
        {
            if (syncing || snapPicker.SelectedIndex < 0) return;
            model.Snap = Snaps[snapPicker.SelectedIndex].Beats;
            // Re-fit the length used for the next drawn note to the new grid (at least one cell).
            if (model.Snap > 0) model.LastLength = Math.Max(model.Snap, Math.Round(model.LastLength / model.Snap) * model.Snap);
            view.InvalidateVisual();
        };

        // ---- target
        ToolTip.SetTip(instrumentPicker, Loc.S("Instrumento que tocan las notas (p1). La lista sale del documento que suena o, si Csound está detenido, del que se ve en el editor.",
                                               "Instrument the notes play (p1). The list comes from the document playing or, when Csound is stopped, the one shown in the editor."));
        instrumentPicker.SelectionChanged += (_, _) =>
        {
            if (syncing || instrumentPicker.SelectedItem is not InstrumentChoice c) return;
            model.Instrument = c.Id;
            model.InstrumentChosen = true; // a user choice: RefreshTarget no longer auto-picks «Voice»
            PushSnapshot();
            MarkPending();
        };
        pitchPicker.ItemsSource = new[]
        {
            Loc.S("p4 = nota MIDI", "p4 = MIDI note"), Loc.S("p4 = Hz", "p4 = Hz"),
            Loc.S("p4 = cpspch (8.00)", "p4 = cpspch (8.00)"), Loc.S("p4 = oct (8.0)", "p4 = oct (8.0)"),
        };
        // Item order must match the PitchMapping enum (the index is cast both ways).
        pitchPicker.SelectedIndex = (int)model.PitchMapping;
        ToolTip.SetTip(pitchPicker, Loc.S("Cómo se escribe la altura en p4: nota MIDI (60 = Do central; el instrumento usa cpsmidinn(p4)), frecuencia en Hz, octava.clase (cpspch: 8.00 = Do central) u oct (cpsoct: 8.0 = Do central).",
                                          "How the pitch is written into p4: MIDI note (60 = middle C; the instrument uses cpsmidinn(p4)), frequency in Hz, octave.pitch-class (cpspch: 8.00 = middle C) or oct (cpsoct: 8.0 = middle C)."));
        pitchPicker.SelectionChanged += (_, _) =>
        {
            if (syncing || pitchPicker.SelectedIndex < 0) return;
            model.PitchMapping = (PitchMapping)pitchPicker.SelectedIndex;
            PushSnapshot();
            MarkPending();
        };
        velocityPicker.ItemsSource = new[] { Loc.S("p5 = 0…1", "p5 = 0…1"), Loc.S("p5 = 0…127", "p5 = 0…127") };
        velocityPicker.SelectedIndex = (int)model.VelocityMapping;
        ToolTip.SetTip(velocityPicker, Loc.S("Cómo se escribe la velocidad en p5: 0…1 (lista para multiplicar la amplitud, como la «Voice» de la demo) o 0…127 (MIDI).",
                                             "How the velocity is written into p5: 0…1 (ready to multiply an amplitude, like the demo's «Voice») or 0…127 (MIDI)."));
        velocityPicker.SelectionChanged += (_, _) =>
        {
            if (syncing || velocityPicker.SelectedIndex < 0) return;
            model.VelocityMapping = (VelocityMapping)velocityPicker.SelectedIndex;
            PushSnapshot();
            MarkPending();
        };

        recordToggle = ToolUi.Toggle(Loc.S("Grabar MIDI", "Record MIDI"),
            Loc.S("Mientras el piano roll toca, las notas que llegan por MIDI (tu teclado o el de pantalla) se agregan a la cuadrícula, ajustadas a ella. Ctrl+Z deshace la toma completa.",
                  "While the piano roll plays, notes arriving over MIDI (your keyboard or the on-screen one) are added to the grid, snapped to it. Ctrl+Z undoes the whole take."));
        // Replace the plain caption with a red «record» dot + text (the dot uses the theme's Record brush).
        recordToggle.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 7,
            Children = { new Avalonia.Controls.Shapes.Ellipse { Width = 10, Height = 10, Fill = AppTheme.Brush("Record"), VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = Loc.S("Grabar MIDI", "Record MIDI") } },
        };
        recordToggle.IsCheckedChanged += (_, _) =>
        {
            // Turning recording off finishes held notes; either way the next recorded note starts a new take (new undo step).
            if (recordToggle.IsChecked != true) CloseOpenNotes();
            takeStarted = false;
        };

        var insert = ToolUi.Button(Loc.S("Insertar en la partitura", "Insert into score"),
            Loc.S("Escribe las notas como líneas «i» al final de <CsScore> del documento, en un bloque marcado «; --- piano roll begin/end ---» (si ya existe, se reemplaza). Tiempos en segundos al tempo elegido. Ctrl+Z en el editor lo deshace.",
                  "Writes the notes as «i» lines at the end of the document's <CsScore>, in a block marked «; --- piano roll begin/end ---» (replaced if it already exists). Times in seconds at the chosen tempo. Ctrl+Z in the editor undoes it."),
            InsertIntoScore, "IconSend");
        var import = ToolUi.Button(Loc.S("Importar", "Import"),
            Loc.S("Lee las notas del documento: el bloque del piano roll si existe (con su tempo y ajustes), si no, todas las líneas «i» del instrumento elegido (al tempo actual). Reemplaza las notas actuales (Ctrl+Z lo deshace).",
                  "Reads the notes from the document: the piano roll block if there is one (with its tempo and settings), otherwise every «i» line of the chosen instrument (at the current tempo). Replaces the current notes (Ctrl+Z undoes it)."),
            ImportFromScore, "IconOpen");
        // No-op click action: the menu needs the button itself as anchor, which the lambda cannot capture before it exists.
        var more = ToolUi.Button("⋯", Loc.S("Más: cuantizar, seleccionar, borrar, duplicar, deshacer, zoom, ayuda.", "More: quantize, select, delete, duplicate, undo, zoom, help."), () => { });
        more.Click += (_, _) => ShowMoreMenu(more);

        // Tool bar row: a WrapPanel so narrow windows wrap the controls onto a second line instead of clipping them.
        var row1 = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 2) };
        foreach (var c in new Control[]
        {
            playButton, loopToggle, ToolUi.Captioned(Loc.S("Tempo", "Tempo"), tempo), ToolUi.Captioned(Loc.S("Cuadrícula", "Grid"), snapPicker), ToolUi.Separator(),
            ToolUi.Captioned(Loc.S("Instrumento", "Instrument"), instrumentPicker), pitchPicker, velocityPicker, ToolUi.Separator(),
            recordToggle, ToolUi.Separator(), insert, import, more,
        })
        {
            c.Margin = new Thickness(0, 2, 6, 2);
            row1.Children.Add(c);
        }
        ToolTip.SetTip(docLabel, Loc.S("Documento al que apunta el piano roll: el que suena o, con Csound detenido, el del editor.", "Document the piano roll addresses: the one playing or, with Csound stopped, the one in the editor."));
        ToolTip.SetTip(pending, Loc.S("Las notas cambiaron desde la última vez que se insertaron en la partitura. Se conservan mientras la app esté abierta; para guardarlas con el archivo usa «Insertar en la partitura» y luego Guardar.",
                                      "The notes changed since they were last inserted into the score. They are kept while the app is open; to save them with the file use «Insert into score», then Save."));
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(12, 0, 12, 6), Children = { docLabel, pending } };

        // ---- editor surface with scroll bars
        // 2×2 grid: view (rounded, clipped border) | vertical bar; horizontal bar below. The view draws
        // everything itself and exposes ScrollX/ScrollY, so the bars are plain ScrollBars kept in sync by hand.
        var surface = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(10, 0, 10, 0) };
        surface.Children.Add(new Border { Child = view, CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderBrush = AppTheme.Brush("Line"), BorderThickness = new Thickness(1) });
        Grid.SetColumn(vScroll, 1);
        Grid.SetRow(hScroll, 1);
        surface.Children.Add(vScroll);
        surface.Children.Add(hScroll);
        ToolTip.SetTip(view, Loc.S(
            "Clic en vacío = nota nueva (mantén y arrastra: derecha = duración, arriba/abajo = velocidad) · clic derecho y arrastrar en vacío = seleccionar con un rectángulo (Supr borra) · arrastrar nota = mover (Alt = copiar) · borde derecho = duración · clic derecho o doble clic = borrar · Ctrl+clic = agregar/quitar de la selección · rueda = altura, Shift+rueda = tiempo, Ctrl+rueda = zoom · Supr, Ctrl+Z/Y, Ctrl+C/V/D, flechas, Q",
            "Click on empty space = new note (hold and drag: right = length, up/down = velocity) · right-drag on empty space = box select (Del deletes) · drag a note = move (Alt = copy) · right edge = length · right-click or double-click = delete · Ctrl+click = add/remove from selection · wheel = pitch, Shift+wheel = time, Ctrl+wheel = zoom · Del, Ctrl+Z/Y, Ctrl+C/V/D, arrows, Q"));
        ToolTip.SetShowDelay(view, 1500); // long delay: the big gesture tooltip must not pop up while drawing
        ToolTip.SetTip(hScroll, Loc.S("Desplazar en el tiempo (también Shift+rueda).", "Scroll in time (also Shift+wheel)."));
        ToolTip.SetTip(vScroll, Loc.S("Desplazar en altura (también la rueda).", "Scroll in pitch (also the wheel)."));
        hScroll.Scroll += (_, _) => { if (!syncing) view.ScrollX = hScroll.Value; };
        vScroll.Scroll += (_, _) => { if (!syncing) view.ScrollY = vScroll.Value; };
        view.ViewChanged += SyncScrollBars;

        // ---- status line
        var statusBar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 6, 12, 8) };
        statusBar.Children.Add(status);
        var right = message;
        Grid.SetColumn(right, 1);
        statusBar.Children.Add(right);
        ToolTip.SetTip(status, Loc.S("Lo que hay bajo el mouse.", "What is under the mouse."));
        ToolTip.SetTip(message, Loc.S("Estado del transporte y de Csound.", "Transport and Csound status."));

        // Dock order matters: top rows and status bar first, the surface last so it fills the rest.
        var root = new DockPanel();
        DockPanel.SetDock(row1, Dock.Top);
        DockPanel.SetDock(row2, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(row1);
        root.Children.Add(row2);
        root.Children.Add(statusBar);
        root.Children.Add(surface);
        Content = root;

        // ---- wiring
        // Note edits: feed the scheduler (heard on its next pass) and flag «not inserted».
        view.Edited += () => { PushSnapshot(); MarkPending(); };
        // Loop/start edits made in the ruler: not part of the score, so no pending mark.
        view.TransportEdited += () =>
        {
            syncing = true;
            loopToggle.IsChecked = model.LoopOn;
            syncing = false;
            PushSnapshot();
        };
        view.Hover += text => status.Text = text;
        view.Audition += Audition;
        model.Changed += OnModelChanged;
        transport.StateChanged += UpdatePlayButton;
        transport.Message += ShowMessage;
        transport.TempoFollowed += bpm =>
        {
            // Ableton Link changed the tempo: show it without proposing it back to the session.
            syncing = true;
            model.Bpm = bpm;
            tempo.Value = (decimal)Math.Clamp(Math.Round(bpm, 2), 20, 400);
            syncing = false;
        };
        // ~33 Hz while the window lives: recorded MIDI is processed here too, so it is drained at the playhead rate.
        transport.Tick += beat =>
        {
            DrainMidi();
            view.Playhead = beat;
            lastTickBeat = beat;
            view.FollowPlayhead();
        };
        vm.Midi.MessageReceived += OnMidi;
        vm.PropertyChanged += OnViewModelChanged;
        docTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) => RefreshTarget());
        docTimer.Start();

        // Tunnel routing: the window sees Space before the focused child (e.g. a Button would treat it as a click).
        AddHandler(KeyDownEvent, OnWindowKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            RefreshTarget();
            AutoImportOnFirstOpen();
            // Initial view: centre on the notes (or around F#4 = 66 when empty) at a readable zoom.
            view.CenterOnPitch(model.Notes.Count > 0 ? (int)Math.Round(model.Notes.Average(n => n.Pitch)) : 66);
            if (model.Notes.Count == 0) view.PixelsPerBeat = 70;
            view.Focus();
            ShowMessage(vm.IsPlayingLive ? vm.ToolEngineStatus : Loc.S("Csound detenido: ▶ lo inicia con el documento elegido.", "Csound stopped: ▶ starts it with the chosen document."), false);
        };
        // Closing stops only this tool's transport (Csound keeps playing) and detaches from long-lived objects.
        Closed += (_, _) =>
        {
            transport.Stop();
            transport.Dispose();
            docTimer.Stop();
            vm.Midi.MessageReceived -= OnMidi;
            vm.PropertyChanged -= OnViewModelChanged;
            model.Changed -= OnModelChanged;
            // Bug fix: the TextChanged subscription on the watched document (made in RefreshTarget)
            // was never removed, so a closed window stayed reachable from that long-lived document
            // (a leak, plus a handler running for a dead window on every keystroke).
            if (watchedDoc is not null) watchedDoc.Document.TextChanged -= OnDocumentTextChanged;
            watchedDoc = null;
            view.Model = new PianoRollModel(); // releases the view's subscription to the session model
        };
        UpdatePlayButton();
        UpdatePending();
    }

    // ------------------------------------------------------------------------------------------
    //  Target document and instruments
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The target document can change when the selected tab or the run mode changes; the refresh is
    /// posted at background priority so it runs after the view model has finished updating.
    /// </summary>
    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedDocument) or nameof(MainViewModel.Mode))
            Dispatcher.UIThread.Post(RefreshTarget, DispatcherPriority.Background);
    }

    /// <summary>Only flags the change; the instrument list is re-parsed lazily by the next <see cref="RefreshTarget"/> (≤ 700 ms), not on every keystroke.</summary>
    void OnDocumentTextChanged(object? sender, EventArgs e) => docTextChanged = true;

    /// <summary>Follows the target document (playing one, else the editor's) and refreshes its instrument list when its text changed.</summary>
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

        var list = MainViewModel.InstrumentsOf(doc);
        // First open: prefer the demo's «Voice», else the first instrument.
        if (!list.Any(c => c.Id == model.Instrument) && !model.InstrumentChosen)
        {
            var pick = list.FirstOrDefault(c => c.Id == "Voice") ?? list.FirstOrDefault();
            if (pick is not null) { model.Instrument = pick.Id; PushSnapshot(); }
        }
        // Keep the current instrument selectable even when the document lacks it, flagged as missing,
        // so switching documents never silently changes what the roll plays.
        if (!list.Any(c => c.Id == model.Instrument))
            list.Insert(0, new InstrumentChoice(model.Instrument, Loc.S("no está en el documento", "not in the document")));
        syncing = true;
        instrumentPicker.ItemsSource = list;
        instrumentPicker.SelectedItem = list.First(c => c.Id == model.Instrument);
        syncing = false;
    }

    /// <summary>When the roll is empty and the document has a piano roll block, load it (reopening a saved piece).</summary>
    void AutoImportOnFirstOpen()
    {
        if (model.Notes.Count > 0 || vm.ToolDocument is not { } doc) return;
        if (ScoreBlocks.ReadBlock(doc.Document.Text, ScoreBlocks.PianoRoll) is null) return;
        ImportFromScore();
    }

    // ------------------------------------------------------------------------------------------
    //  Transport
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// ▶/■ and Space: stops (finishing held recorded notes) or starts from the green start line
    /// (<see cref="PianoRollView.StartBeat"/>) after pushing a fresh snapshot and the tempo.
    /// An empty roll only starts when Record MIDI is on (to record onto it).
    /// </summary>
    void TogglePlay()
    {
        if (transport.IsPlaying)
        {
            // Bug fix: Stop() raises StateChanged → UpdatePlayButton, which clears view.Playhead;
            // closing the held notes afterwards measured them against beat 0, so every note still
            // held while recording got the minimum length. Now the queued MIDI is drained and the
            // held notes are closed at the current playhead first, and only then the transport stops.
            DrainMidi();
            CloseOpenNotes();
            transport.Stop();
            return;
        }
        if (model.Notes.Count == 0 && recordToggle.IsChecked != true)
        {
            ShowMessage(Loc.S("No hay notas: haz clic en la cuadrícula para dibujar una (o activa Grabar MIDI).", "No notes yet: click the grid to draw one (or turn Record MIDI on)."), true);
            return;
        }
        WarnIfInstrumentMissing();
        PushSnapshot();
        transport.Bpm = model.Bpm;
        transport.Play(vm.ToolDocument, view.StartBeat);
        view.Focus();
    }

    /// <summary>Shows Play or Stop on the button (from <see cref="ToolTransport.StateChanged"/>) and hides the playhead when stopped.</summary>
    void UpdatePlayButton()
    {
        bool playing = transport.IsPlaying;
        playButton.Content = ToolUi.Content(playing ? Loc.S("Detener", "Stop") : Loc.S("Tocar", "Play"), playing ? "IconStop" : "IconPlay");
        if (!playing) view.Playhead = null;
    }

    /// <summary>
    /// Hands the scheduler an immutable copy of the notes and settings. The scheduler thread never
    /// reads the live model, so every edit must be followed by a new snapshot to be heard.
    /// </summary>
    void PushSnapshot() => transport.Source = model.Snapshot();

    /// <summary>Shows <paramref name="text"/> on the right of the status line (also as tooltip, since it may be trimmed).</summary>
    /// <param name="text">Message (already localised).</param>
    /// <param name="warning">True to colour it with the theme's Warn brush.</param>
    void ShowMessage(string text, bool warning)
    {
        message.Text = text;
        message.Foreground = warning ? AppTheme.Brush("Warn") : AppTheme.Brush("TextDim");
        ToolTip.SetTip(message, text);
    }

    /// <summary>Warns when the chosen instrument is not defined in the target document (Csound would reject every note).</summary>
    void WarnIfInstrumentMissing()
    {
        var list = MainViewModel.InstrumentsOf(vm.ToolDocument);
        if (list.Count > 0 && !list.Any(c => c.Id == model.Instrument))
            ShowMessage(Loc.S($"⚠ El instrumento {model.Instrument} no está en el documento: Csound dirá «instr not found».",
                              $"⚠ Instrument {model.Instrument} is not in the document: Csound will say «instr not found»."), true);
    }

    /// <summary>Plays a short note when a key is clicked or a note is drawn/moved (only while Csound plays).</summary>
    /// <param name="pitch">MIDI note number.</param>
    /// <param name="velocity">MIDI velocity 1–127.</param>
    /// <remarks>
    /// Sends an immediate (p2 = 0) 0.35 s <c>i</c> event with the same p4/p5 mapping as playback,
    /// so the audition sounds exactly like the roll will.
    /// </remarks>
    void Audition(int pitch, int velocity)
    {
        if (!vm.IsPlayingLive)
        {
            if (!auditionHintShown)
            {
                auditionHintShown = true;
                ShowMessage(Loc.S("Para oír las notas al dibujarlas, Csound debe estar en Tocar (▶ o F5).", "To hear notes while drawing, Csound must be in Play mode (▶ or F5)."), false);
            }
            return;
        }
        string fields = PFields.Pitch(pitch, model.PitchMapping) + " " + PFields.Velocity(velocity, model.VelocityMapping);
        vm.Engine.SendEvent($"i {PFields.InstrumentToken(model.Instrument)} 0 0.35 {fields}");
    }

    /// <summary>Tunnelling key handler of the window (see the constructor); other keys go on to <see cref="PianoRollView"/>.</summary>
    void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // Space = play/stop, like a DAW, unless typing in a text box.
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && FocusManager?.GetFocusedElement() is not TextBox)
        {
            TogglePlay();
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------------------------------
    //  Score
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// «Insert into score»: writes the roll as a marked block of <c>i</c> lines (times in seconds at
    /// the current tempo, plus a <c>;@roll</c> header for exact re-import) into the target document
    /// through <see cref="MainViewModel.WriteScoreBlock"/>, which replaces a previous block, selects
    /// that document's tab and highlights the block. Clears the pending marker.
    /// </summary>
    void InsertIntoScore()
    {
        if (vm.ToolDocument is not { } doc)
        {
            ShowMessage(Loc.S("No hay documento abierto.", "No document is open."), true);
            return;
        }
        if (model.Notes.Count == 0)
        {
            ShowMessage(Loc.S("No hay notas que insertar.", "There are no notes to insert."), true);
            return;
        }
        WarnIfInstrumentMissing();
        string block = ScoreBlocks.BuildPianoRollBlock(model);
        if (!vm.WriteScoreBlock(doc, ScoreBlocks.PianoRoll, block))
        {
            ShowMessage(Loc.S("El documento no es un .csd (no tiene <CsoundSynthesizer>).", "The document is not a .csd (no <CsoundSynthesizer>)."), true);
            return;
        }
        model.SavedRevision = model.Revision;
        UpdatePending();
        string text = Loc.S($"✓ {model.Notes.Count} notas insertadas en la partitura de {doc.FileName} (a {PFields.Num(model.Bpm, 2)} BPM). Guarda el archivo para conservarlas.",
                            $"✓ {model.Notes.Count} notes inserted into the score of {doc.FileName} (at {PFields.Num(model.Bpm, 2)} BPM). Save the file to keep them.");
        ShowMessage(text, false);
        vm.ToolInfo("🎹 " + text);
    }

    /// <summary>
    /// «Import»: reads the piano roll block (exact, with its tempo and settings) or, without one,
    /// every plain <c>i</c> line of the chosen instrument (converted at the current tempo), then
    /// replaces the notes (undoable), syncs the controls and scrolls to the imported notes.
    /// Lines using score shortcuts (<c>.</c>, <c>+</c>, expressions) are counted as skipped.
    /// </summary>
    void ImportFromScore()
    {
        if (vm.ToolDocument is not { } doc)
        {
            ShowMessage(Loc.S("No hay documento abierto.", "No document is open."), true);
            return;
        }
        var result = ScoreBlocks.ImportPianoRoll(doc.Document.Text, model);
        if (result.Notes.Count == 0)
        {
            ShowMessage(result.FromBlock
                ? Loc.S("El bloque del piano roll está vacío.", "The piano roll block is empty.")
                : Loc.S($"No hay líneas «i» del instrumento {model.Instrument} en la partitura.", $"There are no «i» lines for instrument {model.Instrument} in the score."), true);
            SyncControlsFromModel();
            return;
        }
        model.ReplaceAll(result.Notes);
        model.InstrumentChosen = true;
        model.SavedRevision = model.Revision; // the roll now equals the document: nothing pending
        SyncControlsFromModel();
        PushSnapshot();
        UpdatePending();
        view.CenterOnPitch((int)Math.Round(result.Notes.Average(n => n.Pitch)));
        view.ScrollX = Math.Max(0, result.Notes.Min(n => n.Start) - 1); // one beat of margin before the first note
        ShowMessage((result.FromBlock
            ? Loc.S($"✓ {result.Notes.Count} notas cargadas del bloque del piano roll de {doc.FileName}.", $"✓ {result.Notes.Count} notes loaded from the piano roll block of {doc.FileName}.")
            : Loc.S($"✓ {result.Notes.Count} notas importadas de la partitura (instrumento {model.Instrument}, {PFields.Num(model.Bpm, 2)} BPM).", $"✓ {result.Notes.Count} notes imported from the score (instrument {model.Instrument}, {PFields.Num(model.Bpm, 2)} BPM)."))
            + (result.Skipped > 0 ? Loc.S($" {result.Skipped} líneas no se pudieron leer (usan «.», «+», expresiones…).", $" {result.Skipped} lines could not be read (they use «.», «+», expressions…).") : ""),
            result.Skipped > 0);
    }

    /// <summary>Puts the model's settings back into the controls (after an import restored them).</summary>
    void SyncControlsFromModel()
    {
        syncing = true;
        tempo.Value = (decimal)model.Bpm;
        transport.Bpm = model.Bpm;
        loopToggle.IsChecked = model.LoopOn;
        pitchPicker.SelectedIndex = (int)model.PitchMapping;
        velocityPicker.SelectedIndex = (int)model.VelocityMapping;
        int snap = Array.FindIndex(Snaps, s => Math.Abs(s.Beats - model.Snap) < 1e-6);
        if (snap >= 0) snapPicker.SelectedIndex = snap;
        syncing = false;
        docTextChanged = true; // the imported instrument may differ: rebuild the picker list
        RefreshTarget();
        view.InvalidateVisual();
    }

    /// <summary>Any change of notes or selection: the scheduler gets a fresh snapshot (edits made while playing are heard on the next pass).</summary>
    void OnModelChanged()
    {
        PushSnapshot();
        UpdatePending();
    }

    /// <summary>
    /// Records that the roll differs from what was last written to the score: bumps
    /// <see cref="PianoRollModel.Revision"/> (compared with <see cref="PianoRollModel.SavedRevision"/>).
    /// </summary>
    void MarkPending()
    {
        model.Revision++;
        UpdatePending();
    }

    /// <summary>Shows the «● changes not inserted» marker when the revision moved past the inserted one (and there are notes).</summary>
    void UpdatePending() =>
        pending.Text = model.Revision != model.SavedRevision && model.Notes.Count > 0
            ? Loc.S("● cambios sin insertar en la partitura", "● changes not inserted into the score")
            : "";

    // ------------------------------------------------------------------------------------------
    //  More menu
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds the «⋯» flyout fresh each time, so every item's enabled state reflects the current
    /// model (undo stack, selection, notes, presence of a block in the document).
    /// </summary>
    /// <param name="anchor">The «⋯» button the flyout opens under.</param>
    void ShowMoreMenu(Control anchor)
    {
        var menu = new MenuFlyout();
        // Every item: run the action, then push a snapshot, mark pending and give the focus back to the grid
        // (so keyboard shortcuts keep working after using the menu).
        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => { action(); PushSnapshot(); MarkPending(); view.Focus(); };
            menu.Items.Add(item);
        }
        Add(Loc.S("Deshacer (Ctrl+Z)", "Undo (Ctrl+Z)"), model.Undo, model.CanUndo);
        Add(Loc.S("Rehacer (Ctrl+Y)", "Redo (Ctrl+Y)"), model.Redo, model.CanRedo);
        menu.Items.Add(new Separator());
        Add(Loc.S("Seleccionar todo (Ctrl+A)", "Select all (Ctrl+A)"), model.SelectAll, model.Notes.Count > 0);
        Add(Loc.S("Cuantizar al inicio de la cuadrícula (Q)", "Quantize starts to the grid (Q)"), model.Quantize, model.Notes.Count > 0 && model.Snap > 0);
        Add(Loc.S("Duplicar selección (Ctrl+D)", "Duplicate selection (Ctrl+D)"), () => { if (model.Copy() > 0) model.Paste(null); }, model.Selected.Count > 0);
        Add(Loc.S("Borrar selección (Supr)", "Delete selection (Del)"), () => model.Delete(model.Selected.ToList()), model.Selected.Count > 0);
        Add(Loc.S("Borrar todas las notas", "Delete all notes"), () => model.Delete(model.Notes.ToList()), model.Notes.Count > 0);
        menu.Items.Add(new Separator());
        Add(Loc.S("Loop = de la línea verde a la última nota", "Loop = from the green line to the last note"), () =>
        {
            // Loop end = the last note's end rounded up to a whole 4/4 bar (at least one beat after the start).
            double end = Math.Max(view.StartBeat + 1, Math.Ceiling(model.ContentEnd / 4) * 4);
            model.LoopStart = view.StartBeat;
            model.LoopEnd = end;
            model.LoopOn = true;
            syncing = true; loopToggle.IsChecked = true; syncing = false;
            view.InvalidateVisual();
        }, model.Notes.Count > 0);
        Add(Loc.S("Ver todas las notas", "Zoom to fit all notes"), ZoomToFit, model.Notes.Count > 0);
        Add(Loc.S("Volver al inicio (línea verde a 1.1.1)", "Back to start (green line to 1.1.1)"), () => { view.StartBeat = 0; view.ScrollX = 0; });
        menu.Items.Add(new Separator());
        Add(Loc.S("Quitar el bloque del piano roll del documento", "Remove the piano roll block from the document"), RemoveBlock,
            vm.ToolDocument is { } d && ScoreBlocks.ReadBlock(d.Document.Text, ScoreBlocks.PianoRoll) is not null);
        menu.ShowAt(anchor);
    }

    /// <summary>
    /// «Zoom to fit»: horizontal zoom so all notes (+1 beat) fit the grid width (excluding the key
    /// column and a 30 px margin; at least 8 px per beat), and row height so the pitch range plus
    /// 6 rows of margin fits (6–24 px per row), centred on the middle pitch.
    /// </summary>
    void ZoomToFit()
    {
        if (model.Notes.Count == 0) return;
        double first = model.Notes.Min(n => n.Start), last = model.ContentEnd;
        view.PixelsPerBeat = Math.Max(8, (view.Bounds.Width - PianoRollView.KeyWidth - 30) / Math.Max(1, last - first + 1));
        view.ScrollX = Math.Max(0, first - 0.5);
        int lo = model.Notes.Min(n => n.Pitch), hi = model.Notes.Max(n => n.Pitch);
        view.RowHeight = Math.Clamp(view.GridHeight / Math.Max(12, hi - lo + 6), 6, 24);
        view.CenterOnPitch((lo + hi) / 2);
    }

    /// <summary>
    /// Removes the marked piano roll block from the target document with a single AvaloniaEdit
    /// replace (one undo step in the editor). The notes stay in the roll.
    /// </summary>
    void RemoveBlock()
    {
        if (vm.ToolDocument is not { } doc || ScoreBlocks.MakeRemoveEdit(doc.Document.Text, ScoreBlocks.PianoRoll) is not { } edit) return;
        doc.Document.Replace(edit.Offset, edit.Length, edit.Text);
        model.SavedRevision = -1; // no revision matches: the notes are now always «not inserted»
        UpdatePending();
        ShowMessage(Loc.S("Bloque del piano roll quitado de la partitura (Ctrl+Z en el editor lo devuelve).", "Piano roll block removed from the score (Ctrl+Z in the editor brings it back)."), false);
    }

    /// <summary>
    /// Mirrors the view's scroll state into the external scroll bars (from <see cref="PianoRollView.ViewChanged"/>).
    /// </summary>
    /// <remarks>
    /// Horizontal unit = beats: the range ends half a viewport before the content end so the last
    /// notes can be scrolled to the middle (the view itself may extend past it).
    /// Vertical unit = pixels: range = total pitch-grid height minus the visible grid height.
    /// <see cref="syncing"/> stops the bars' <c>Scroll</c> events from writing back into the view.
    /// </remarks>
    void SyncScrollBars()
    {
        syncing = true;
        hScroll.Minimum = 0;
        hScroll.Maximum = Math.Max(0, view.ContentBeats - view.ViewportBeats * 0.5);
        hScroll.ViewportSize = view.ViewportBeats;
        hScroll.Value = view.ScrollX;
        vScroll.Minimum = 0;
        vScroll.Maximum = Math.Max(0, view.ContentHeight - view.GridHeight);
        vScroll.ViewportSize = view.GridHeight;
        vScroll.Value = view.ScrollY;
        syncing = false;
    }

    // ------------------------------------------------------------------------------------------
    //  MIDI recording
    // ------------------------------------------------------------------------------------------

    /// <summary>Called on the MIDI driver thread: queue notes with the score time they arrived at.</summary>
    /// <param name="m">The incoming message (only note-on/off are kept; everything else is ignored).</param>
    /// <remarks>
    /// The score time is sampled here, at arrival, rather than when the UI drains the queue, so
    /// the recorded position does not depend on UI latency (up to one 30 ms tick).
    /// </remarks>
    void OnMidi(MidiMessage m)
    {
        if (m.Kind is MidiKind.NoteOn or MidiKind.NoteOff)
            midiIn.Enqueue((m, vm.Engine.ScoreTime));
    }

    /// <summary>Turns queued MIDI notes into roll notes while recording (UI thread, every transport tick).</summary>
    void DrainMidi()
    {
        bool recording = recordToggle.IsChecked == true && transport.IsPlaying;
        // Always empty the queue (so stale notes are not recorded later), but only use them while recording.
        while (midiIn.TryDequeue(out var item))
        {
            if (!recording) continue;
            // Score seconds → timeline beat (loop-aware); null before the transport's first beat.
            if (transport.BeatAt(item.ScoreTime) is not { } beat) continue;
            var m = item.Message;
            // m.Index = note number; m.Value = normalised velocity 0…1 (high-resolution, MIDI 2.0 aware) → 1–127.
            if (m.Kind == MidiKind.NoteOn)
                openNotes[m.Index] = (beat, Math.Clamp((int)Math.Round(m.Value * 127), 1, 127));
            else if (openNotes.Remove(m.Index, out var open))
                AddRecorded(m.Index, open.Beat, beat, open.Velocity);
        }
        if (!recording && openNotes.Count > 0) CloseOpenNotes();
    }

    /// <summary>
    /// Ends every held recorded note at the current playhead (or after the minimum length when the
    /// playhead is unknown / earlier), e.g. when recording or playback stops with keys still down.
    /// </summary>
    /// <remarks>
    /// When the transport stopped by itself (or from elsewhere) the playhead is already cleared, so
    /// the last beat seen by the transport tick (<see cref="lastTickBeat"/>) is used instead of 0;
    /// otherwise every held note would be cut to the minimum length.
    /// </remarks>
    void CloseOpenNotes()
    {
        double now = view.Playhead ?? lastTickBeat ?? 0;
        foreach (var (pitch, open) in openNotes.ToList())
            AddRecorded(pitch, open.Beat, Math.Max(now, open.Beat + model.MinLength), open.Velocity);
        openNotes.Clear();
    }

    /// <summary>Adds one recorded note to the model, snapped to the grid.</summary>
    /// <param name="pitch">MIDI note number.</param>
    /// <param name="startBeat">Timeline beat of the note-on.</param>
    /// <param name="endBeat">Timeline beat of the note-off; smaller than the start when the loop wrapped in between.</param>
    /// <param name="velocity">MIDI velocity 1–127.</param>
    /// <remarks>
    /// The start snaps to the grid; the length is rounded to whole grid cells (never below
    /// <see cref="PianoRollModel.MinLength"/>). The notes list is edited directly and the change is
    /// announced with <see cref="PianoRollModel.NotifyChanged"/> (the first note of a take sets the
    /// undo checkpoint).
    /// </remarks>
    void AddRecorded(int pitch, double startBeat, double endBeat, int velocity)
    {
        if (!takeStarted)
        {
            model.Checkpoint(); // one undo step for the whole take
            takeStarted = true;
        }
        double start = Math.Max(0, model.SnapBeat(startBeat));
        if (endBeat < startBeat) endBeat = model.LoopOn ? model.LoopEnd : startBeat + model.MinLength; // wrapped around the loop
        double length = Math.Max(model.MinLength, model.Snap > 0 ? Math.Round((endBeat - startBeat) / model.Snap) * model.Snap : endBeat - startBeat);
        model.Notes.Add(new RollNote { Start = start, Length = length, Pitch = pitch, Velocity = velocity });
        model.NotifyChanged();
        MarkPending();
        status.Text = Loc.S($"● grabada {PFields.NoteName(pitch)} ({pitch}) en {PianoRollView.Position(start)}", $"● recorded {PFields.NoteName(pitch)} ({pitch}) at {PianoRollView.Position(start)}");
    }
}
