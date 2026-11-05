// ImageSequencerRoom.cs — Paint Rooms: the «Image Sequencer» room (fourth tab).
//
// Role: build melodic phrases and rhythmic loops out of small painted PATTERNS arranged on a
// timeline (see Paint/ImageSequencer.cs for the model and why the arrangement is COMPOSED into one
// canvas and played by the Paint Synth engine instead of being scheduled note by note).
//
// Layout (built in code, themed through AppTheme, every visible string bilingual through Loc.S):
//   row 1   transport: Play/Stop (Space), ▶ Pattern (solo preview), Loop, ⇤, Render WAV,
//           → Paint Synth, Show Csound code · tempo, steps/beat, beats/bar, bars, lanes, gate ·
//           sound: mode, gain, attack, release · «File ▾» (new pattern, import picture, colour
//           mapping — same place and wording as Paint Synth's File ▾), «Examples ▾», «Sound ▾»,
//           «Axis ▾», «?»
//   left    PATTERNS: library list + New / Duplicate / Rename / Delete / Import picture, the
//           pattern's length and Legato; then the paint tools and the brush (incl. Noise)
//   centre  top: the pattern editor (PaintCanvas on the selected pattern's own PaintSession, so
//           brushes, selections, clipboard and undo are the Paint Synth ones);
//           bottom: the ARRANGEMENT (SequencerTimeline) with its own ↶ / ↷ and, on the right, the
//           CLIP inspector (pattern, repeats, transpose, reverse, invert, gain, delete).
//   bottom  hover read-out, messages, DSP load.
//
// State that must survive switching tabs and closing / reopening the window lives in RoomState,
// kept per shared PaintSession (ConditionalWeakTable, like the Image Filter room): the document,
// the room's OWN PaintLivePlayer (slot 1, so it can sound together with Paint Synth's player) and
// the arrangement undo stacks. The document is written into PaintRoomSettings.Sequencer (and thus
// settings.json) whenever the room is deactivated or disposed.
//
// Undo: the window's ↶ / ↷ and Ctrl+Z / Ctrl+Y act on the PATTERN being edited (its session's
// ImageHistory). Arrangement edits (place, move, delete, clip settings) have their own small undo
// (snapshots of the clip list): the ↶ / ↷ buttons above the timeline, and Ctrl+Z / Ctrl+Y while the
// timeline was the last thing clicked.
//
// Live updates: a ≈ 30 Hz timer polls a cheap signature of the model (arrangement revision + every
// pattern's image object and Version); when it changes and something is playing, the arrangement is
// recomposed and only the columns that differ are copied into the canvas being played
// (SequencerComposer.CopyChanged), which the player uploads to Csound at its next tick. Changes of
// size or tempo restart the note at the same position.
//
// UI thread only.

using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsoundVisual.Controls;
using CsoundVisual.Localization;
using CsoundVisual.Paint;
using CsoundVisual.Theming;

namespace CsoundVisual.Views;

/// <summary>The «Image Sequencer» room (see the file header).</summary>
public sealed class ImageSequencerRoom : IPaintRoom
{
    /// <summary>Long-lived state of the room (one per shared session; survives closing the window).</summary>
    sealed class RoomState
    {
        /// <summary>The document (patterns, clips, timing, sound).</summary>
        public required SequencerDocument Doc { get; set; }
        /// <summary>The room's own live player (slot 1).</summary>
        public required PaintLivePlayer Player { get; init; }
        /// <summary>The persisted settings object (= shared Settings.Sequencer).</summary>
        public required SequencerPrefs Prefs { get; init; }
        /// <summary>Arrangement undo / redo: snapshots of the clip list AND of the timing / sound settings.</summary>
        public Stack<ArrangementSnapshot> Undo { get; } = new();
        /// <summary>See <see cref="Undo"/>.</summary>
        public Stack<ArrangementSnapshot> Redo { get; } = new();
        /// <summary>Id of the pattern shown in the editor.</summary>
        public int CurrentPattern { get; set; }
        /// <summary>
        /// <see cref="Fingerprint"/> of the document as last saved (or as loaded): the document has
        /// unsaved changes when its fingerprint differs.
        /// </summary>
        public string Baseline { get; set; } = "";
    }

    /// <summary>
    /// A text that changes whenever anything worth saving changes: the document written into a
    /// throw-away <see cref="SequencerPrefs"/> (patterns as base64 levels, clips, timing, sound) and
    /// serialised to JSON.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <returns>The fingerprint (compared, never stored on disk).</returns>
    /// <remarks>The selected pattern and the loop toggle are left out on purpose: they are view
    /// preferences, not work, and must not trigger «Save changes?».</remarks>
    static string Fingerprint(SequencerDocument doc)
    {
        var tmp = new SequencerPrefs();
        doc.SaveTo(tmp, 0, false);
        return System.Text.Json.JsonSerializer.Serialize(tmp);
    }

    /// <summary>Everything the arrangement history restores: the clips and the document-wide settings.</summary>
    /// <param name="Clips">The clip list.</param>
    /// <param name="Tempo">Beats per minute.</param>
    /// <param name="StepsPerBeat">Steps per beat.</param>
    /// <param name="BeatsPerBar">Beats per bar.</param>
    /// <param name="Bars">Arrangement length in bars.</param>
    /// <param name="Lanes">Visible lanes.</param>
    /// <param name="Gate">Note length as a fraction of the step.</param>
    /// <param name="Synth">Sound settings.</param>
    /// <param name="Loop">Loop toggle.</param>
    /// <param name="Key">What changed («clips» or a setting name): coalescing key and tooltip label.</param>
    /// <param name="When">When the step was taken or last extended (UTC).</param>
    sealed record ArrangementSnapshot(List<SeqClip> Clips, double Tempo, int StepsPerBeat, int BeatsPerBar, int Bars, int Lanes,
        double Gate, PaintSynthSettings Synth, bool Loop, string Key, DateTime When);

    /// <summary>Room state per shared session (weak: it goes away with the session).</summary>
    static readonly ConditionalWeakTable<PaintSession, RoomState> States = new();

    /// <summary>The window context (owner window, shared session, app services).</summary>
    readonly PaintRoomContext ctx;
    /// <summary>The shared Paint Rooms session (Paint Synth's canvas: target of «→ Paint Synth»).</summary>
    readonly PaintSession shared;
    /// <summary>This room's long-lived state.</summary>
    readonly RoomState state;
    /// <summary>The pattern editor.</summary>
    readonly PaintCanvas canvas = new();
    /// <summary>The arrangement view.</summary>
    readonly SequencerTimeline timeline = new() { MinHeight = 120 };
    /// <summary>≈ 30 Hz UI timer: player tick, play lines, model polling, DSP read-out.</summary>
    readonly DispatcherTimer timer;
    /// <summary>Root of the room's content.</summary>
    readonly DockPanel root = new();

    // ---- transport, timing and sound controls
    readonly Button playButton, soloButton, renderButton;
    readonly ToggleButton loopToggle;
    readonly NumericUpDown tempoBox, stepsBeatBox, beatsBarBox, barsBox, lanesBox, gateBox, gainBox, attackBox, releaseBox;
    readonly ComboBox modeBox = new() { Width = 130, VerticalAlignment = VerticalAlignment.Center };

    // ---- library and pattern controls
    readonly ListBox library = new() { Height = 150, Margin = new Thickness(0, 2, 0, 4) };
    readonly NumericUpDown stepsBox;
    readonly CheckBox legatoBox = new();

    /// <summary>
    /// «Stretch»: when on, changing a pattern's Steps squeezes or stretches its content in time
    /// (<see cref="SequencerDocument.TimeStretched"/>); when off, steps are cut or silence is added
    /// at the end. Not persisted (starts off, the safer behaviour).
    /// </summary>
    readonly CheckBox stretchBox = new();
    readonly TextBlock patternTitle = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };

    // ---- brush controls
    readonly Dictionary<PaintTool, ToggleButton> toolButtons = [];
    readonly Slider sizeSlider = new() { Minimum = 1, Maximum = 120, Width = 160 };
    readonly Slider opacitySlider = new() { Minimum = 0.02, Maximum = 1, Width = 160 };
    readonly Slider noiseSlider = new() { Minimum = 0, Maximum = 1, Width = 160 };
    readonly Slider panSlider = new() { Minimum = -1, Maximum = 1, Width = 160 };
    readonly CheckBox noiseOnlyBox = new(), showNoiseBox = new();
    readonly TextBlock sizeText = Small(), opacityText = Small(), noiseText = Small(), panText = Small();

    // ---- clip inspector
    readonly StackPanel inspector = new() { Spacing = 6, Width = 196, Margin = new Thickness(10, 0, 0, 0) };
    readonly ComboBox clipPatternBox = new() { Width = 132 };
    // (created in BuildInspector, called from the constructor)
    NumericUpDown repeatsBox = null!, transposeBox = null!, clipGainBox = null!;
    readonly CheckBox reverseBox = new(), invertBox = new();

    /// <summary>Inspector: the clip's length in steps (also set by drawing / dragging its right edge).</summary>
    NumericUpDown clipLenBox = null!;

    /// <summary>Inspector: the clip's «Stretch» (one pass fitted to the clip instead of looping).</summary>
    readonly CheckBox clipStretchBox = new();
    readonly Button arrUndo, arrRedo;

    // ---- status
    readonly TextBlock hoverText = new() { FontSize = 12, Foreground = AppTheme.Brush("TextDim"), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock messageText = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right };
    readonly TextBlock dspText = new() { FontSize = 12, Foreground = AppTheme.Brush("TextDim"), Margin = new Thickness(16, 0, 0, 0), MinWidth = 120 };

    /// <summary>True while code writes the controls (their change handlers then do nothing).</summary>
    bool syncing;
    /// <summary>True when the last click was on the timeline (Ctrl+Z / Delete then act on the arrangement).</summary>
    bool arrangementFocus;
    /// <summary>Incremented by every arrangement / timing edit (part of the model signature).</summary>
    int revision;
    /// <summary>Model signature at the last poll (see the file header).</summary>
    long lastSignature;
    /// <summary>The canvas the player is playing (arrangement or solo pattern), or <c>null</c>.</summary>
    SpectralImage? composed;
    /// <summary>Composed columns per step of <see cref="composed"/>.</summary>
    int composedSub = 1;
    /// <summary>The pattern being previewed alone (▶ Pattern), or <c>null</c> when the arrangement plays.</summary>
    SeqPattern? soloPattern;
    /// <summary>A render is running (the render button cancels it).</summary>
    bool rendering;
    /// <summary>Cancels the running render.</summary>
    CancellationTokenSource? renderCancel;
    /// <summary>The Paint Synth player's previous HostInUseByOthers (restored on Dispose).</summary>
    readonly Func<bool>? sharedHostCheckBefore;

    /// <summary>Creates the room.</summary>
    /// <param name="context">Window context.</param>
    public ImageSequencerRoom(PaintRoomContext context)
    {
        ctx = context;
        shared = context.Session;
        state = States.GetValue(shared, _ => CreateState(context));
        var doc = Doc;
        timeline.Document = doc;

        // ================= transport =================
        playButton = ToolUi.Button(Loc.S("Tocar", "Play"),
            Loc.S("Toca el arreglo con Csound desde el cursor (Espacio). Puedes pintar patrones y mover clips mientras suena.",
                  "Plays the arrangement through Csound from the cursor (Space). You can paint patterns and move clips while it plays."),
            () => TogglePlay(solo: false), "IconPlay", primary: true);
        playButton.MinWidth = 96;
        soloButton = ToolUi.Button(Loc.S("Patrón", "Pattern"), Loc.S("Escucha solo el patrón que estás pintando, en loop.", "Listen to the pattern you are painting, alone, looped."),
            () => TogglePlay(solo: true), "IconPlay");
        loopToggle = ToolUi.Toggle(Loc.S("Loop", "Loop"), Loc.S("Repetir el arreglo sin fin.", "Repeat the arrangement endlessly."), "IconRefresh");
        loopToggle.IsChecked = state.Prefs.Loop;
        loopToggle.IsCheckedChanged += (_, _) => { if (!syncing) { PushArrangementUndo("loop"); state.Prefs.Loop = loopToggle.IsChecked == true; RestartIfPlaying(); } };
        var home = ToolUi.Button("⇤", Loc.S("Cursor al comienzo (Inicio).", "Cursor to the start (Home)."), () => timeline.CursorStep = 0);
        renderButton = ToolUi.Button(Loc.S("Renderizar WAV", "Render WAV"),
            Loc.S("Escribe el arreglo completo (una pasada) a un WAV de 24 bits con Csound, normalizado a −1 dBTP (pico real). La frecuencia se elige en «Sonido ▾».",
                  "Writes the whole arrangement (one pass) to a 24-bit WAV with Csound, normalised to −1 dBTP (true peak). The sample rate is chosen in «Sound ▾»."),
            () => _ = RenderAsync(), "IconRender");
        var toSynth = ToolUi.Button(Loc.S("→ Paint Synth", "→ Paint Synth"),
            Loc.S("Manda el arreglo compuesto como lienzo a Paint Synth (se puede deshacer allá), para seguir pintándolo o procesarlo.",
                  "Sends the composed arrangement to Paint Synth as a canvas (undoable there), to keep painting or processing it."), SendToPaintSynth);
        var code = ToolUi.Button(Loc.S("Ver código Csound", "Show Csound code"),
            Loc.S("Escribe el .csd que toca este arreglo y lo abre en el editor.", "Writes the .csd that plays this arrangement and opens it in the editor."), ShowCode, "IconBook");

        tempoBox = ToolUi.Number(20, 400, doc.Tempo, 1, Loc.S("Tempo en pulsos por minuto.", "Tempo in beats per minute."), "0.#", 70, spinner: false);
        stepsBeatBox = ToolUi.Number(1, 8, doc.StepsPerBeat, 1, Loc.S("Pasos por pulso: 4 = semicorcheas, 3 = tresillos, 2 = corcheas.", "Steps per beat: 4 = sixteenths, 3 = triplets, 2 = eighths."), "0", 54, spinner: false);
        beatsBarBox = ToolUi.Number(1, 16, doc.BeatsPerBar, 1, Loc.S("Pulsos por compás (4 = 4/4).", "Beats per bar (4 = 4/4)."), "0", 54, spinner: false);
        barsBox = ToolUi.Number(1, 64, doc.Bars, 1, Loc.S("Largo del arreglo en compases (= largo del loop).", "Arrangement length in bars (= loop length)."), "0", 58, spinner: false);
        lanesBox = ToolUi.Number(1, 8, doc.Lanes, 1, Loc.S("Pistas visibles (todas suenan a la vez; solo ordenan).", "Visible lanes (all sound together; they only organise)."), "0", 54, spinner: false);
        gateBox = ToolUi.Number(12.5, 100, doc.Gate * 100, 12.5, Loc.S("Largo de cada nota en % del paso. Menos de 100 % = las notas repetidas se vuelven a atacar.", "Length of each note in % of the step. Below 100 % repeated notes re-attack."), "0.#", 64, spinner: false);
        tempoBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) { PushArrangementUndo("tempo"); Doc.Tempo = (double)v; Doc.UpdatePatternDurations(); Touch(); } };
        stepsBeatBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) { PushArrangementUndo("steps"); Doc.StepsPerBeat = (int)v; Doc.UpdatePatternDurations(); Touch(); } };
        beatsBarBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) { PushArrangementUndo("beats"); Doc.BeatsPerBar = (int)v; Touch(); } };
        barsBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) { PushArrangementUndo("bars"); Doc.Bars = (int)v; Touch(); } };
        lanesBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) { PushArrangementUndo("lanes"); Doc.Lanes = (int)v; Touch(); } };
        gateBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) { PushArrangementUndo("gate"); Doc.Gate = (double)v / 100; Touch(); } };

        modeBox.ItemsSource = new[] { Loc.S("Senos", "Sines"), Loc.S("Bandas de ruido", "Noise bands") };
        ToolTip.SetTip(modeBox, Loc.S("Senos: cada celda suena como tono, o como ruido según su capa de ruido (el control «Ruido» del pincel). Bandas de ruido: todo suena como ruido.",
                                      "Sines: each cell sounds as a tone, or as noise according to its noise layer (the brush's «Noise» control). Noise bands: everything sounds as noise."));
        modeBox.SelectionChanged += (_, _) => { if (!syncing && modeBox.SelectedIndex >= 0) SetSynth(Doc.Synth with { Mode = (SynthMode)modeBox.SelectedIndex }); };
        gainBox = ToolUi.Number(-60, 12, doc.Synth.GainDb, 1, Loc.S("Ganancia en dB sobre el nivel automático.", "Gain in dB on top of the automatic level."), "+0;-0;0", 60, spinner: false);
        attackBox = ToolUi.Number(0.5, 2000, doc.Synth.AttackMs, 1, Loc.S("Ataque de cada nota (ms).", "Attack of every note (ms)."), "0.#", 60, spinner: false);
        releaseBox = ToolUi.Number(0.5, 4000, doc.Synth.ReleaseMs, 5, Loc.S("Caída de cada nota (ms). Corta = seco y rítmico.", "Release of every note (ms). Short = dry and rhythmic."), "0.#", 64, spinner: false);
        gainBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) SetSynth(Doc.Synth with { GainDb = (double)v }); };
        attackBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) SetSynth(Doc.Synth with { AttackMs = (double)v }); };
        releaseBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) SetSynth(Doc.Synth with { ReleaseMs = (double)v }); };
        // File ▾ mirrors Paint Synth's File ▾ (same label, same position before Examples ▾) so
        // «import a picture» is found in the same place in every room; the 🖼 library button stays
        // as a shortcut.
        var fileMenu = MenuButton(Loc.S("Archivo ▾", "File ▾"), Loc.S("Patrón nuevo, importar una imagen como patrón, qué significan los colores al importar.", "New pattern, import a picture as a pattern, what colours mean when importing."), BuildFileMenu);
        var soundMenu = MenuButton(Loc.S("Sonido ▾", "Sound ▾"), Loc.S("Ajustes menos frecuentes del sonido y del render.", "Less frequent sound and render settings."), BuildSoundMenu);
        var examples = MenuButton(Loc.S("Ejemplos ▾", "Examples ▾"), Loc.S("Volver a cargar el ejemplo o empezar vacío.", "Reload the example or start empty."), BuildExamplesMenu);
        var axisMenu = MenuButton(Loc.S("Eje ▾", "Axis ▾"), Loc.S("Las alturas de las filas de todos los patrones.", "The pitches of the rows of every pattern."), BuildAxisMenu);
        var help = ToolUi.Button("?", Loc.S("Ayuda rápida del Image Sequencer.", "Image Sequencer quick help."), ShowHelp);

        var row1 = Row();
        AddAll(row1, playButton, soloButton, loopToggle, home, ToolUi.Separator(), renderButton, toSynth, code, ToolUi.Separator(),
            ToolUi.Captioned("BPM", tempoBox), ToolUi.Captioned(Loc.S("Pasos/pulso", "Steps/beat"), stepsBeatBox), ToolUi.Captioned(Loc.S("Pulsos/comp.", "Beats/bar"), beatsBarBox),
            ToolUi.Captioned(Loc.S("Compases", "Bars"), barsBox), ToolUi.Captioned(Loc.S("Pistas", "Lanes"), lanesBox), ToolUi.Captioned(Loc.S("Gate %", "Gate %"), gateBox), ToolUi.Separator(),
            ToolUi.Captioned(Loc.S("Modo", "Mode"), modeBox), ToolUi.Captioned(Loc.S("Ganancia dB", "Gain dB"), gainBox), ToolUi.Captioned(Loc.S("Ataque", "Attack"), attackBox),
            ToolUi.Captioned(Loc.S("Caída", "Release"), releaseBox), ToolUi.Separator(), fileMenu, examples, soundMenu, axisMenu, help);

        // ================= left: library + tools =================
        stepsBox = ToolUi.Number(2, SequencerDocument.MaxPatternSteps, 16, 1, Loc.S("Largo del patrón en pasos (corta o agrega silencio al final; Ctrl+Z deshace).", "Pattern length in steps (cuts or adds silence at the end; Ctrl+Z undoes)."), "0", 70, spinner: false);
        stepsBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v && Current is { } p && (int)v != p.Steps) ResizePattern(p, (int)v); };
        legatoBox.Content = Loc.S("Legato", "Legato");
        ToolTip.SetTip(legatoBox, Loc.S("Une un paso encendido con el siguiente de la misma fila en una nota larga (sin volver a atacar).", "Joins a lit step with the next one on the same row into one long note (no re-attack)."));
        stretchBox.Content = Loc.S("Estirar", "Stretch");
        ToolTip.SetTip(stretchBox, Loc.S("Activado: cambiar «Pasos» comprime o estira el contenido del patrón, y los clips que dibujas o alargas en el arreglo comprimen o estiran el patrón para llenarlos. Desactivado: corta o agrega silencio, y los clips repiten o cortan el patrón.",
                                         "On: changing «Steps» squeezes or stretches the pattern's content, and clips you draw or resize in the arrangement squeeze or stretch the pattern to fill them. Off: cuts or adds silence, and clips loop or cut the pattern."));
        stretchBox.IsCheckedChanged += (_, _) => timeline.NewClipsStretch = stretchBox.IsChecked == true;
        legatoBox.IsCheckedChanged += (_, _) => { if (!syncing && Current is { } p) { p.Legato = legatoBox.IsChecked == true; Touch(); } };
        library.SelectionChanged += (_, _) => { if (!syncing && library.SelectedIndex >= 0 && library.SelectedIndex < Doc.Patterns.Count) ShowPattern(Doc.Patterns[library.SelectedIndex]); };
        var libButtons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new[]
        {
            SmallButton("+", Loc.S("Patrón nuevo (vacío, 16 pasos).", "New pattern (empty, 16 steps)."), NewPattern),
            SmallButton("⧉", Loc.S("Duplicar el patrón.", "Duplicate the pattern."), DuplicatePattern),
            SmallButton("✎", Loc.S("Cambiar el nombre.", "Rename."), () => _ = RenamePatternAsync()),
            SmallButton("🗑", Loc.S("Borrar el patrón (y sus clips).", "Delete the pattern (and its clips)."), DeletePattern),
            SmallButton("🖼", Loc.S("Importar una imagen como patrón nuevo (también de 16 bits, RGB/RGBA).", "Import a picture as a new pattern (16-bit and RGB/RGBA too)."), () => _ = ImportPatternAsync()),
        })
            libButtons.Children.Add(b);
        var left = new StackPanel { Spacing = 3, Margin = new Thickness(10, 4, 10, 0), Width = 186 };
        left.Children.Add(Caption(Loc.S("PATRONES", "PATTERNS")));
        left.Children.Add(library);
        left.Children.Add(libButtons);
        left.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ToolUi.Captioned(Loc.S("Pasos", "Steps"), stepsBox), new Border { Child = legatoBox, VerticalAlignment = VerticalAlignment.Bottom } } });
        left.Children.Add(stretchBox);
        left.Children.Add(BuildTools());
        var leftScroll = new ScrollViewer { Content = left, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        // ================= centre: pattern editor over the arrangement =================
        canvas.Hover += t => hoverText.Text = t;
        // Painting past the end of a pattern makes it longer (one undo step with the stroke).
        canvas.GrowLimit = SequencerDocument.MaxPatternSteps;
        canvas.Grown += () => { if (Current is { } p) PatternLengthChanged(p); };
        canvas.AddHandler(InputElement.PointerPressedEvent, (_, _) => arrangementFocus = false, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        timeline.AddHandler(InputElement.PointerPressedEvent, (_, _) => arrangementFocus = true, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ToolTip.SetTip(canvas, Loc.S("Pinta el patrón: una columna = un paso, una fila = una nota. Izquierdo = herramienta, derecho = borrar. El control «Ruido» del pincel vuelve ruidosas las notas (platillos, cajas).",
                                     "Paint the pattern: one column = one step, one row = one note. Left = tool, right = erase. The brush's «Noise» control makes notes noisy (cymbals, snares)."));
        ToolTip.SetShowDelay(canvas, 2500);
        var editorHead = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        editorHead.Children.Add(patternTitle);
        var editor = new DockPanel();
        DockPanel.SetDock(editorHead, Dock.Top);
        editor.Children.Add(editorHead);
        editor.Children.Add(new Border { Child = canvas, CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderBrush = AppTheme.Brush("Line"), BorderThickness = new Thickness(1) });

        arrUndo = SmallButton("↶", Loc.S("Deshacer el último cambio del arreglo.", "Undo the last arrangement change."), ArrangementUndo);
        arrRedo = SmallButton("↷", Loc.S("Rehacer el cambio del arreglo.", "Redo the arrangement change."), ArrangementRedo);
        Avalonia.Automation.AutomationProperties.SetName(arrUndo, "ArrangementUndo");
        var arrHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 4) };
        arrHead.Children.Add(Caption(Loc.S("ARREGLO", "ARRANGEMENT")));
        arrHead.Children.Add(arrUndo);
        arrHead.Children.Add(arrRedo);
        arrHead.Children.Add(new TextBlock
        {
            Text = Loc.S("clic = poner el patrón · arrastrar en vacío = dibujar un clip del largo que quieras · borde derecho = largo · arrastrar un clip = mover (Mayús = por paso) · doble clic = editar · clic derecho = quitar · regla = cursor",
                         "click = place the pattern · drag on empty cells = draw a clip of any length · right edge = length · drag a clip = move (Shift = by step) · double-click = edit · right-click = remove · ruler = cursor"),
            FontSize = 11, Foreground = AppTheme.Brush("TextDim"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        BuildInspector();
        var arrBody = new DockPanel();
        // The inspector scrolls when the arrangement area is short (the splitter can make it small).
        var inspectorScroll = new ScrollViewer { Content = inspector, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        DockPanel.SetDock(inspectorScroll, Dock.Right);
        arrBody.Children.Add(inspectorScroll);
        arrBody.Children.Add(new Border { Child = timeline, CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderBrush = AppTheme.Brush("Line"), BorderThickness = new Thickness(1) });
        var arrangement = new DockPanel();
        DockPanel.SetDock(arrHead, Dock.Top);
        arrangement.Children.Add(arrHead);
        arrangement.Children.Add(arrBody);

        var centre = new Grid { RowDefinitions = new RowDefinitions("3*,Auto,2*"), Margin = new Thickness(0, 0, 10, 0) };
        centre.Children.Add(editor);
        var splitter = new GridSplitter { Height = 5, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(splitter, 1);
        centre.Children.Add(splitter);
        Grid.SetRow(arrangement, 2);
        centre.Children.Add(arrangement);

        // ================= timeline events =================
        timeline.PlaceRequested += PlaceClip;
        timeline.ClipMoved += MoveClip;
        timeline.ClipDrawn += DrawClip;
        // Dragging a clip's right edge: the new length, looped or stretched as the «Stretch» box says.
        timeline.ClipResized += (c, len) => ReplaceClip(c, c with { Length = len, Repeats = 1, Stretch = stretchBox.IsChecked == true });
        timeline.ClipSelected += c => { SelectClip(c); };
        timeline.ClipOpened += c => { if (Doc.Find(c.PatternId) is { } p) ShowPattern(p); };
        timeline.ClipDeleteRequested += DeleteClip;
        timeline.Hover += t => hoverText.Text = t;

        // ================= status =================
        var status = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(12, 6, 12, 8) };
        status.Children.Add(hoverText);
        Grid.SetColumn(messageText, 1);
        Grid.SetColumn(dspText, 2);
        status.Children.Add(messageText);
        status.Children.Add(dspText);

        DockPanel.SetDock(row1, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        DockPanel.SetDock(leftScroll, Dock.Left);
        root.Children.Add(row1);
        root.Children.Add(status);
        root.Children.Add(leftScroll);
        root.Children.Add(centre);

        // ================= wiring =================
        var player = state.Player;
        player.StateChanged += UpdatePlayButtons;
        player.Message += Say;
        // Two players share the «Paint Rooms host» orchestra: neither stops it under the other's feet.
        player.HostInUseByOthers = () => shared.Player?.State is PaintPlayState.Playing or PaintPlayState.Starting;
        if (shared.Player is { } sp)
        {
            sharedHostCheckBefore = sp.HostInUseByOthers;
            var before = sharedHostCheckBefore;
            sp.HostInUseByOthers = () => state.Player.State is PaintPlayState.Playing or PaintPlayState.Starting || (before?.Invoke() ?? false);
        }
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => OnTick());
        timer.Start();
        RefreshLibrary();
        ShowPattern(Doc.Find(state.CurrentPattern) ?? Doc.Patterns.FirstOrDefault());
        SyncControls();
        UpdatePlayButtons();
        SelectClip(null);
        Say(Loc.S("Pinta patrones arriba, ponlos en el arreglo abajo y presiona Tocar (Espacio). Ya hay un ejemplo cargado.",
                  "Paint patterns at the top, place them in the arrangement below and press Play (Space). An example is already loaded."), false);
    }

    /// <summary>Creates the per-session state: the saved document (or the starter example) and the player (slot 1).</summary>
    /// <param name="context">Window context.</param>
    static RoomState CreateState(PaintRoomContext context)
    {
        var prefs = context.Session.Settings.Sequencer ??= new SequencerPrefs();
        prefs.Canvas ??= SequencerDocument.DefaultCanvasPrefs();
        var doc = SequencerDocument.Load(prefs);
        return new RoomState
        {
            Doc = doc, Prefs = prefs, Player = new PaintLivePlayer(context.App, slot: 1),
            CurrentPattern = doc.Find(prefs.SelectedPattern)?.Id ?? doc.Patterns.FirstOrDefault()?.Id ?? 0,
            Baseline = Fingerprint(doc),
        };
    }

    // ------------------------------------------------------------------------------------------
    //  IPaintRoom
    // ------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public string Title => "Image Sequencer";

    /// <inheritdoc/>
    public string Tip => Loc.S("Frases y loops hechos de imágenes: pinta patrones cortos y ordénalos en el tiempo (transponer, invertir, repetir).",
                               "Phrases and loops made of pictures: paint short patterns and arrange them in time (transpose, reverse, repeat).");

    /// <inheritdoc/>
    /// <remarks>The session of the pattern being edited (the window's ↶ / ↷ undo pattern edits).</remarks>
    public PaintSession EditSession => Current?.Session ?? shared;

    /// <inheritdoc/>
    public Control Content => root;

    /// <summary>The document (tests).</summary>
    public SequencerDocument Doc => state.Doc;

    /// <summary>The pattern editor canvas (tests).</summary>
    public PaintCanvas Canvas => canvas;

    /// <summary>The arrangement view (tests).</summary>
    public SequencerTimeline Timeline => timeline;

    /// <summary>The room's player (tests).</summary>
    public PaintLivePlayer Player => state.Player;

    /// <summary>The pattern shown in the editor.</summary>
    public SeqPattern? Current => Doc.Find(state.CurrentPattern);

    /// <inheritdoc/>
    public void OnActivated()
    {
        SyncControls();
        canvas.Focus();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Stops this room's playback (so it never runs unseen). The document is NOT written to the
    /// settings here: it is kept for the next session only when the user saves it (File ▾ › Save,
    /// or «Save» in the prompt shown when Paint Rooms or the app closes).
    /// </remarks>
    public void OnDeactivated() => state.Player.Stop();

    /// <summary>True after «Don't save»: the work was dropped and must not be asked about again.</summary>
    bool discarded;

    /// <inheritdoc/>
    public bool HasUnsavedChanges => !discarded && (Fingerprint(Doc) != state.Baseline || (!state.Prefs.Saved && state.Prefs.Patterns.Count > 0));

    /// <inheritdoc/>
    public string UnsavedName => Loc.S("Image Sequencer (patrones y arreglo)", "Image Sequencer (patterns and arrangement)");

    /// <inheritdoc/>
    public Task<bool> SaveChangesAsync() { SaveChanges(); return Task.FromResult(true); }

    /// <summary>Saves for the next session: writes the document into the settings and the settings to disk, and makes this the new saved state.</summary>
    public void SaveChanges()
    {
        Save();
        state.Prefs.Saved = true;
        state.Baseline = Fingerprint(Doc);
        ctx.App.SavePaintSettings();
        Say(Loc.S("✓ Patrones y arreglo guardados: estarán en la próxima sesión.", "✓ Patterns and arrangement saved: they will be there next session."), false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The settings still hold the last saved document (nothing is written without saving), so
    /// forgetting the in-memory state is enough: the next time the room opens (this session or
    /// the next) it loads the saved document again.
    /// </remarks>
    public void DiscardChanges()
    {
        state.Player.Stop();
        if (!state.Prefs.Saved && state.Prefs.Patterns.Count > 0)
        {
            // Work auto-saved by an older version was never saved by the user: forget it, so the
            // next session starts with the example.
            state.Prefs.Patterns = [];
            state.Prefs.Clips = [];
            ctx.App.SavePaintSettings();
        }
        States.Remove(shared);
        discarded = true;
    }

    /// <inheritdoc/>
    public bool HandleKey(KeyEventArgs e, bool focusIsTextInput)
    {
        if (focusIsTextInput) return false;
        if (canvas.HandleSelectionKey(e.Key)) return true;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var ps = Current?.Session;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(solo: shift); return true;
            // One undo order for the whole room: the newest change, pattern or arrangement/settings.
            case Key.Z when ctrl && shift: Redo(); return true;
            case Key.Z when ctrl: Undo(); return true;
            case Key.Y when ctrl: Redo(); return true;
            case Key.C when ctrl: ps?.Copy(); return true;
            case Key.X when ctrl: ps?.Copy(); ps?.DeleteSelection(); return true;
            case Key.V when ctrl: ps?.Paste(0, ps.Selection?.Bounds.Y0 ?? 0); return true;
            case Key.A when ctrl: ps?.ChangeSelection(CellRect.All(ps.Image)); return true;
            case Key.Delete or Key.Back:
                if ((arrangementFocus || ps?.Selection is null) && timeline.SelectedClip is { } c) DeleteClip(c);
                else ps?.DeleteSelection();
                return true;
            case Key.Escape: ps?.ChangeSelection(null); return true;
            case Key.Home: timeline.CursorStep = 0; return true;
            case Key.D0 when ctrl: canvas.ZoomToFit(); return true;
        }
        if (ctrl || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return false;
        PaintTool? tool = e.Key switch
        {
            Key.P => PaintTool.Pencil, Key.A => PaintTool.Airbrush, Key.S => PaintTool.Spray, Key.H => PaintTool.Harmonics,
            Key.L => PaintTool.Line, Key.R => PaintTool.Rectangle, Key.E => PaintTool.Eraser, Key.B => PaintTool.Blur,
            _ => PaintSelectionUi.ToolForKey(e.Key, shift),
        };
        if (tool is { } t && toolButtons.ContainsKey(t)) { SelectTool(t); return true; }
        return false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        timer.Stop();
        renderCancel?.Cancel();
        state.Player.Stop();
        state.Player.StateChanged -= UpdatePlayButtons;
        state.Player.Message -= Say;
        state.Player.HostInUseByOthers = null;
        if (shared.Player is { } sp) sp.HostInUseByOthers = sharedHostCheckBefore;
        DetachComposed();
        foreach (var (session, onPush) in watched) session.History.Pushed -= onPush;
        watched.Clear();
        foreach (var (session, onReplaced) in watchedReplaced) session.ImageReplaced -= onReplaced;
        watchedReplaced.Clear();
        canvas.Session = null;
    }

    /// <summary>Writes the document into the persisted settings object (only <see cref="SaveChanges"/> calls it).</summary>
    void Save() => Doc.SaveTo(state.Prefs, state.CurrentPattern, state.Prefs.Loop);

    // ------------------------------------------------------------------------------------------
    //  Library and pattern editor
    // ------------------------------------------------------------------------------------------

    /// <summary>Rebuilds the library list (name, steps, legato mark) and selects the current pattern.</summary>
    void RefreshLibrary()
    {
        syncing = true;
        // Each item: a dot in the pattern's timeline colour, then the name and length in the text colour
        // (readable on the selection highlight too).
        library.ItemsSource = Doc.Patterns.Select(p => new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                new TextBlock { Text = "●", Foreground = new SolidColorBrush(AppTheme.ColorOf(SequencerTimeline.ClipRoles[p.ColorIndex % SequencerTimeline.ClipRoles.Length])) },
                new TextBlock { Text = p.Name + "  ·  " + Loc.S($"{p.Steps} pasos", $"{p.Steps} steps") + (p.Legato ? "  ·  legato" : ""), Foreground = AppTheme.Brush("Text") },
            },
        }).ToList();
        library.SelectedIndex = Doc.Patterns.FindIndex(p => p.Id == state.CurrentPattern);
        clipPatternBox.ItemsSource = Doc.Patterns.Select(p => p.Name).ToList();
        syncing = false;
    }

    /// <summary>Shows a pattern in the editor (canvas, title, steps, legato) and selects it in the library.</summary>
    /// <param name="p">The pattern, or <c>null</c> when the library is empty.</param>
    void ShowPattern(SeqPattern? p)
    {
        state.CurrentPattern = p?.Id ?? 0;
        timeline.CurrentPatternId = p?.Id ?? 0;   // the pattern a drag on empty cells draws
        if (p is not null) Watch(p);
        canvas.Session = p?.Session;
        if (p is not null) canvas.ZoomToFit();
        syncing = true;
        patternTitle.Text = p is null ? Loc.S("No hay patrones: crea uno con «+».", "No patterns: create one with «+».")
            : Loc.S($"Patrón «{p.Name}» · {p.Steps} pasos · {p.Image.Height} filas ({AxisName(true)})", $"Pattern «{p.Name}» · {p.Steps} steps · {p.Image.Height} rows ({AxisName(false)})");
        if (p is not null) { stepsBox.Value = p.Steps; legatoBox.IsChecked = p.Legato; }
        library.SelectedIndex = p is null ? -1 : Doc.Patterns.IndexOf(p);
        syncing = false;
        if (soloPattern is not null && p is not null && !ReferenceEquals(p, soloPattern) && state.Player.State != PaintPlayState.Idle)
            TogglePlay(solo: true, restart: true);   // the solo preview follows the pattern being edited
    }

    /// <summary>Short bilingual name of the current axis preset (for the editor title).</summary>
    /// <param name="spanish">True for Spanish.</param>
    string AxisName(bool spanish) => Doc.AxisPreset switch
    {
        1 => spanish ? "notas C2…C7" : "notes C2…C7",
        2 => spanish ? "logarítmico 40 Hz…16 kHz" : "logarithmic 40 Hz…16 kHz",
        _ => spanish ? "notas C1…C9" : "notes C1…C9",
    };

    /// <summary>«+»: a new empty pattern (16 steps, or one bar when the bar is longer) placed after the others.</summary>
    void NewPattern()
    {
        var p = Doc.AddPattern(Loc.S("Patrón ", "Pattern ") + (Doc.Patterns.Count + 1), Math.Max(16, Doc.StepsPerBar));
        RefreshLibrary();
        ShowPattern(p);
        Touch();
        Say(Loc.S("Patrón nuevo: píntalo y luego haz clic en el arreglo para ponerlo.", "New pattern: paint it, then click in the arrangement to place it."), false);
    }

    /// <summary>«⧉»: duplicates the current pattern (pixels, legato) as «name 2».</summary>
    void DuplicatePattern()
    {
        if (Current is not { } p) return;
        var copy = Doc.Duplicate(p, p.Name + " 2");
        RefreshLibrary();
        ShowPattern(copy);
        Touch();
    }

    /// <summary>«🗑»: deletes the current pattern and its clips (the clip removal can be undone in the arrangement).</summary>
    void DeletePattern()
    {
        if (Current is not { } p) return;
        PushArrangementUndo();
        Doc.RemovePattern(p);
        RefreshLibrary();
        ShowPattern(Doc.Patterns.FirstOrDefault());
        SelectClip(null);
        Touch();
        Say(Loc.S($"Patrón «{p.Name}» borrado.", $"Pattern «{p.Name}» deleted."), false);
    }

    /// <summary>«✎»: asks for a new name.</summary>
    async Task RenamePatternAsync()
    {
        if (Current is not { } p) return;
        if (await TextPromptDialog.AskAsync(ctx.Window, Loc.S("Nombre del patrón", "Pattern name"), p.Name) is not { Length: > 0 } name) return;
        p.Name = name.Trim();
        p.Session.ImageName = p.Name;
        RefreshLibrary();
        ShowPattern(p);
        Touch();
    }

    /// <summary>Changes the current pattern's length: columns are cut or silent ones added at the end (undoable in the pattern).</summary>
    /// <param name="p">The pattern.</param>
    /// <param name="steps">New length.</param>
    void ResizePattern(SeqPattern p, int steps)
    {
        var src = p.Image;
        SpectralImage img;
        if (stretchBox.IsChecked == true)
            img = SequencerDocument.TimeStretched(src, steps, Doc.StepSeconds);   // squeeze / stretch the content
        else
        {
            // Cut steps at the end, or add silent ones (column-major arrays: a prefix copy).
            img = new SpectralImage(steps, src.Axis, steps * Doc.StepSeconds);
            int n = Math.Min(steps, src.Width) * src.Height;
            Array.Copy(src.Left, img.Left, n);
            Array.Copy(src.Right, img.Right, n);
            if (src.Noise is { } nz) Array.Copy(nz, img.EnsureNoise(), n);
        }
        p.Session.ReplaceImage(img, "retune");
        RefreshLibrary();
        ShowPattern(p);
        Touch();
    }

    /// <summary>«🖼»: imports a picture as a new pattern (any size; 16-bit and the RGB / RGBA mappings supported).</summary>
    /// <remarks>
    /// The picture's height becomes the axis rows; with «keep proportions» its width becomes steps
    /// (clamped to 2…<see cref="SequencerDocument.MaxPatternSteps"/>), otherwise it is fitted to the
    /// current pattern's length.
    /// </remarks>
    async Task ImportPatternAsync()
    {
        var files = await ctx.Window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.S("Importar imagen como patrón", "Import picture as pattern"),
            FileTypeFilter = [new FilePickerFileType(Loc.S("Imágenes", "Pictures")) { Patterns = ImageCodec.Extensions.Select(e => "*" + e).ToArray() }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await ImportPatternAsync(path);
    }

    /// <summary>Imports <paramref name="path"/> as a new pattern (tests call this directly).</summary>
    /// <param name="path">Picture file.</param>
    /// <param name="ask">Show the import dialog (fit and colour mapping).</param>
    /// <returns>The new pattern, or <c>null</c> when cancelled or failed (the status line says why).</returns>
    public async Task<SeqPattern?> ImportPatternAsync(string path, bool ask = true)
    {
        var cprefs = state.Prefs.Canvas;
        DecodedPicture picture;
        try { picture = await Task.Run(() => ImageCodec.Decode(path)); }
        catch (Exception ex) { Say(Loc.S("No se pudo leer la imagen: ", "Could not read the picture: ") + ex.Message, true); return null; }
        using (picture)
        {
            var fit = cprefs.ImportFit;
            var mapping = cprefs.ImportMapping;
            int current = Current?.Steps ?? 16;
            // «Full detail» without the dialog: one step per picture pixel (as many as a pattern allows).
            if (fit == ImportFit.FullDetail) current = Math.Clamp(picture.Bitmap.Width, 2, SequencerDocument.MaxPatternSteps);
            if (ask)
            {
                // Patterns share the document's axis (no axis choice) and are at most MaxPatternSteps long.
                var choice = await PaintImportDialog.AskAsync(ctx.Window, Path.GetFileName(path), picture, Doc.Axis, current, cprefs, 1,
                    showDuration: false, allowAxis: false, maxColumns: SequencerDocument.MaxPatternSteps);
                if (choice is null) return null;
                (fit, mapping) = (choice.Fit, choice.Mapping);
                current = Math.Clamp(choice.Columns, 2, SequencerDocument.MaxPatternSteps);
                cprefs.ImportFit = fit;
                cprefs.ImportMapping = mapping;
            }
            int steps = fit == ImportFit.KeepProportions
                ? Math.Clamp(ImageCodec.ColumnsFor(picture.Bitmap.Width, picture.Bitmap.Height, Doc.Axis.RowCount), 2, SequencerDocument.MaxPatternSteps)
                : current;
            var axis = Doc.Axis;
            var img = await Task.Run(() => ImageCodec.FromPicture(picture, steps, axis, steps * 0.125, mapping, fit == ImportFit.FitInside ? ImportFit.FitInside : ImportFit.Stretch));
            var p = Doc.AddPattern(Path.GetFileNameWithoutExtension(path), img);
            RefreshLibrary();
            ShowPattern(p);
            Touch();
            string depthEs = (picture.BitsPerChannel >= 16 ? "16 bits" : "8 bits") + (picture.HasAlpha ? " con alfa" : "");
            Say(Loc.S($"Imagen importada como patrón «{p.Name}»: {picture.OriginalWidth} × {picture.OriginalHeight} px, {depthEs} → {p.Steps} pasos × {img.Height} filas{(img.Noise is null ? "" : ", con capa de ruido")}.",
                      $"Picture imported as pattern «{p.Name}»: {picture.OriginalWidth} × {picture.OriginalHeight} px, {picture.DepthText} → {p.Steps} steps × {img.Height} rows{(img.Noise is null ? "" : ", with a noise layer")}."), false);
            return p;
        }
    }

    // ------------------------------------------------------------------------------------------
    //  Arrangement
    // ------------------------------------------------------------------------------------------

    /// <summary>Click on an empty cell: places the current pattern there (selected afterwards).</summary>
    /// <param name="lane">Lane.</param>
    /// <param name="step">First step (start of the bar under the mouse).</param>
    public void PlaceClip(int lane, int step)
    {
        if (Current is not { } p) { Say(Loc.S("Crea o elige un patrón primero.", "Create or choose a pattern first."), true); return; }
        PushArrangementUndo();
        var c = new SeqClip(Doc.NewId(), p.Id, lane, step);
        Doc.Clips.Add(c);
        SelectClip(c);
        Touch();
    }

    /// <summary>A clip was drawn by dragging on empty cells: the current pattern with the drawn length.</summary>
    /// <param name="lane">Lane.</param>
    /// <param name="step">First step.</param>
    /// <param name="length">Length in steps (looped or stretched as the «Stretch» box says).</param>
    void DrawClip(int lane, int step, int length)
    {
        if (Current is not { } p) { Say(Loc.S("Crea o elige un patrón primero.", "Create or choose a pattern first."), true); return; }
        PushArrangementUndo();
        var c = new SeqClip(Doc.NewId(), p.Id, lane, step, Length: length, Stretch: stretchBox.IsChecked == true);
        Doc.Clips.Add(c);
        SelectClip(c);
        Touch();
    }

    /// <summary>A clip was dragged to (lane, step).</summary>
    /// <param name="c">The clip.</param>
    /// <param name="lane">New lane.</param>
    /// <param name="step">New first step.</param>
    void MoveClip(SeqClip c, int lane, int step) => ReplaceClip(c, c with { Lane = lane, StartStep = step });

    /// <summary>Removes a clip (undoable in the arrangement).</summary>
    /// <param name="c">The clip.</param>
    public void DeleteClip(SeqClip c)
    {
        int i = Doc.Clips.FindIndex(x => x.Id == c.Id);
        if (i < 0) return;
        PushArrangementUndo();
        Doc.Clips.RemoveAt(i);
        if (timeline.SelectedClip?.Id == c.Id) SelectClip(null);
        Touch();
    }

    /// <summary>Replaces a clip by an edited copy (same id), undoable, and keeps it selected.</summary>
    /// <param name="old">The clip as it is.</param>
    /// <param name="edited">The new version.</param>
    public void ReplaceClip(SeqClip old, SeqClip edited)
    {
        int i = Doc.Clips.FindIndex(x => x.Id == old.Id);
        if (i < 0 || Doc.Clips[i] == edited) return;
        PushArrangementUndo();
        Doc.Clips[i] = edited;
        SelectClip(edited);
        Touch();
    }

    /// <summary>Selects a clip in the timeline and shows it in the inspector (or disables the inspector).</summary>
    /// <param name="c">The clip or <c>null</c>.</param>
    void SelectClip(SeqClip? c)
    {
        timeline.SelectedClip = c;
        syncing = true;
        inspector.IsEnabled = c is not null;
        if (c is not null)
        {
            clipPatternBox.SelectedIndex = Doc.Patterns.FindIndex(p => p.Id == c.PatternId);
            int len = Doc.ClipSteps(c), ps = Doc.Find(c.PatternId)?.Steps ?? 1;
            repeatsBox.Value = c.Length > 0 ? Math.Clamp((len + ps - 1) / ps, 1, 64) : c.Repeats;
            clipLenBox.Value = len;
            clipStretchBox.IsChecked = c.Stretch;
            transposeBox.Value = c.Transpose;
            reverseBox.IsChecked = c.Reverse;
            invertBox.IsChecked = c.Invert;
            clipGainBox.Value = (decimal)c.GainDb;
        }
        syncing = false;
    }

    /// <summary>Builds the clip inspector (right of the timeline).</summary>
    void BuildInspector()
    {
        // No spinner arrows: the inspector is narrow (type, use the mouse wheel or the arrow keys).
        repeatsBox = ToolUi.Number(1, 64, 1, 1, Loc.S("Cuántas veces seguidas suena el patrón.", "How many times in a row the pattern plays."), "0", 76, spinner: false);
        transposeBox = ToolUi.Number(-48, 48, 0, 1, Loc.S("Transponer en filas (en el eje de notas: semitonos; 12 = una octava).", "Transpose in rows (on the note axis: semitones; 12 = an octave)."), "+0;-0;0", 76, spinner: false);
        clipGainBox = ToolUi.Number(-48, 12, 0, 1, Loc.S("Ganancia del clip en dB.", "Clip gain in dB."), "+0.#;-0.#;0", 76, spinner: false);
        reverseBox.Content = Loc.S("Al revés (tiempo)", "Reverse (time)");
        invertBox.Content = Loc.S("Invertir (altura)", "Invert (pitch)");
        ToolTip.SetTip(invertBox, Loc.S("Da vuelta el patrón dentro de las filas que usa: lo agudo pasa a grave (inversión melódica).", "Flips the pattern within the rows it uses: high becomes low (melodic inversion)."));
        void Edit(Func<SeqClip, SeqClip> f) { if (!syncing && timeline.SelectedClip is { } c) ReplaceClip(c, f(c)); }
        clipPatternBox.SelectionChanged += (_, _) => { if (clipPatternBox.SelectedIndex >= 0 && clipPatternBox.SelectedIndex < Doc.Patterns.Count) Edit(c => c with { PatternId = Doc.Patterns[clipPatternBox.SelectedIndex].Id }); };
        // Repeats sets the length back to whole passes of the pattern (Length 0 = Steps × Repeats).
        repeatsBox.ValueChanged += (_, e) => { if (e.NewValue is { } v) Edit(c => c with { Repeats = (int)v, Length = 0 }); };
        clipLenBox = ToolUi.Number(1, SequencerDocument.MaxTotalSteps, 16, 1, Loc.S("Largo del clip en pasos (también se cambia arrastrando su borde derecho en el arreglo).", "Clip length in steps (also changed by dragging its right edge in the arrangement)."), "0", 76, spinner: false);
        clipLenBox.ValueChanged += (_, e) => { if (e.NewValue is { } v) Edit(c => c with { Length = (int)v, Repeats = 1 }); };
        clipStretchBox.Content = Loc.S("Estirar", "Stretch");
        ToolTip.SetTip(clipStretchBox, Loc.S("Activado: el patrón (una pasada) se comprime o estira para llenar el clip. Desactivado: suena a su velocidad y se repite o se corta.",
                                             "On: one pass of the pattern is squeezed or stretched to fill the clip. Off: it plays at its own speed and loops or is cut."));
        clipStretchBox.IsCheckedChanged += (_, _) => Edit(c => c with { Stretch = clipStretchBox.IsChecked == true });
        transposeBox.ValueChanged += (_, e) => { if (e.NewValue is { } v) Edit(c => c with { Transpose = (int)v }); };
        clipGainBox.ValueChanged += (_, e) => { if (e.NewValue is { } v) Edit(c => c with { GainDb = (double)v }); };
        reverseBox.IsCheckedChanged += (_, _) => Edit(c => c with { Reverse = reverseBox.IsChecked == true });
        invertBox.IsCheckedChanged += (_, _) => Edit(c => c with { Invert = invertBox.IsChecked == true });
        var del = ToolUi.Button(Loc.S("Quitar clip", "Remove clip"), Loc.S("Quita el clip del arreglo (Supr).", "Removes the clip from the arrangement (Del)."), () => { if (timeline.SelectedClip is { } c) DeleteClip(c); });
        inspector.Children.Add(Caption(Loc.S("CLIP", "CLIP")));
        inspector.Children.Add(ToolUi.Captioned(Loc.S("Patrón", "Pattern"), clipPatternBox));
        inspector.Children.Add(ToolUi.Captioned(Loc.S("Repeticiones", "Repeats"), repeatsBox));
        inspector.Children.Add(ToolUi.Captioned(Loc.S("Largo (pasos)", "Length (steps)"), clipLenBox));
        inspector.Children.Add(clipStretchBox);
        inspector.Children.Add(ToolUi.Captioned(Loc.S("Transponer", "Transpose"), transposeBox));
        inspector.Children.Add(ToolUi.Captioned(Loc.S("Ganancia dB", "Gain dB"), clipGainBox));
        inspector.Children.Add(reverseBox);
        inspector.Children.Add(invertBox);
        inspector.Children.Add(del);
    }

    /// <summary>The current clips and settings as a snapshot.</summary>
    /// <param name="key">What is about to change (see <see cref="ArrangementSnapshot.Key"/>).</param>
    ArrangementSnapshot Snapshot(string key) => new([.. Doc.Clips], Doc.Tempo, Doc.StepsPerBeat, Doc.BeatsPerBar, Doc.Bars, Doc.Lanes,
        Doc.Gate, Doc.Synth, state.Prefs.Loop, key, DateTime.UtcNow);

    /// <summary>Changes of the same setting closer together than this are one undo step (spinner clicks, typing).</summary>
    static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Remembers the clips and settings before an arrangement or settings edit (clears the redo
    /// stack). Repeated changes of the same SETTING within <see cref="CoalesceWindow"/> extend the
    /// last step instead (one undo goes back to before the whole burst).
    /// </summary>
    /// <param name="key">«clips» for arrangement edits, or the name of the setting being changed.</param>
    void PushArrangementUndo(string key = "clips")
    {
        if (restoringArrangement) return;
        if (key != "clips" && state.Redo.Count == 0 && state.Undo.TryPeek(out var top) && top.Key == key && DateTime.UtcNow - top.When < CoalesceWindow)
        {
            state.Undo.Pop();
            state.Undo.Push(top with { When = DateTime.UtcNow });
            return;
        }
        state.Undo.Push(Snapshot(key));
        if (state.Undo.Count > 200) { var keep = state.Undo.Take(200).Reverse().ToList(); state.Undo.Clear(); foreach (var k in keep) state.Undo.Push(k); }
        state.Redo.Clear();
        undoOrder.Push(ArrangementToken);
        redoOrder.Clear();
    }

    /// <summary>True while a snapshot is being restored (its control updates must not record steps).</summary>
    bool restoringArrangement;

    /// <summary>Undoes the last arrangement / settings edit (the ARRANGEMENT section's ↶).</summary>
    public void ArrangementUndo()
    {
        if (state.Undo.Count == 0) return;
        SwapArrangement(state.Undo, state.Redo);
        MoveToken(undoOrder, redoOrder, ArrangementToken);
    }

    /// <summary>Redoes the last undone arrangement / settings edit.</summary>
    public void ArrangementRedo()
    {
        if (state.Redo.Count == 0) return;
        SwapArrangement(state.Redo, state.Undo);
        MoveToken(redoOrder, undoOrder, ArrangementToken);
    }

    /// <summary>Restores the clips and settings from <paramref name="from"/>, saving the current ones on <paramref name="to"/>.</summary>
    /// <param name="from">Stack to pop.</param>
    /// <param name="to">Stack that receives the current state.</param>
    void SwapArrangement(Stack<ArrangementSnapshot> from, Stack<ArrangementSnapshot> to)
    {
        if (from.Count == 0) return;
        var snap = from.Pop();
        to.Push(Snapshot(snap.Key) with { When = DateTime.MinValue });
        restoringArrangement = true;
        try
        {
            Doc.Clips.Clear();
            // Clips of patterns deleted meanwhile cannot come back.
            Doc.Clips.AddRange(snap.Clips.Where(c => Doc.Find(c.PatternId) is not null));
            bool timingChanged = snap.Tempo != Doc.Tempo || snap.StepsPerBeat != Doc.StepsPerBeat;
            Doc.Tempo = snap.Tempo;
            Doc.StepsPerBeat = snap.StepsPerBeat;
            Doc.BeatsPerBar = snap.BeatsPerBar;
            Doc.Bars = snap.Bars;
            Doc.Lanes = snap.Lanes;
            Doc.Gate = snap.Gate;
            bool soundChanged = snap.Synth != Doc.Synth || snap.Loop != state.Prefs.Loop;
            Doc.Synth = snap.Synth;
            state.Prefs.Loop = snap.Loop;
            if (timingChanged) Doc.UpdatePatternDurations();
            SyncControls();
            SelectClip(timeline.SelectedClip is { } s ? Doc.Clips.FirstOrDefault(c => c.Id == s.Id) : null);
            Touch();
            if (soundChanged && state.Player.State != PaintPlayState.Idle) state.Player.UpdateSettings(PlaySynth());
        }
        finally { restoringArrangement = false; }
    }

    // ---- one undo order across the patterns and the arrangement (the window's ↶ ↷ and Ctrl+Z)

    /// <summary>Marks an arrangement / settings step in <see cref="undoOrder"/>.</summary>
    static readonly object ArrangementToken = new();

    /// <summary>
    /// Which history holds each step, newest on top: <see cref="ArrangementToken"/> or the
    /// pattern's <see cref="PaintSession"/>. Lets ↶ undo whatever was changed LAST, whether it was
    /// a stroke in a pattern or a tempo change.
    /// </summary>
    readonly Stack<object> undoOrder = new();

    /// <summary>Same as <see cref="undoOrder"/> for the steps undone (redo order).</summary>
    readonly Stack<object> redoOrder = new();

    /// <summary>Pattern sessions whose history this room listens to (unsubscribed in Dispose).</summary>
    readonly Dictionary<PaintSession, Action> watched = [];

    /// <summary>Starts recording the steps of a pattern's history in <see cref="undoOrder"/> (once per pattern).</summary>
    /// <param name="p">The pattern being shown.</param>
    void Watch(SeqPattern p)
    {
        if (watched.ContainsKey(p.Session)) return;
        var session = p.Session;
        Action onPush = () => { undoOrder.Push(session); redoOrder.Clear(); };
        session.History.Pushed += onPush;
        watched[session] = onPush;
        // The image object of a pattern is replaced by resizes, growing strokes and their undo/redo:
        // keep the steps box, the title, the library and the arrangement in step with its length.
        Action onReplaced = () => { if (PatternOf(session) is { } q) PatternLengthChanged(q); };
        session.ImageReplaced += onReplaced;
        watchedReplaced[session] = onReplaced;
    }

    /// <summary>Handlers added to each watched session's <c>ImageReplaced</c> (removed in <see cref="Dispose"/>).</summary>
    readonly Dictionary<PaintSession, Action> watchedReplaced = [];

    /// <summary>A pattern's length may have changed: refreshes the library, the editor title and Steps (when it is the shown one) and the arrangement.</summary>
    /// <param name="p">The pattern.</param>
    void PatternLengthChanged(SeqPattern p)
    {
        RefreshLibrary();
        if (ReferenceEquals(p, Current))
        {
            syncing = true;
            patternTitle.Text = Loc.S($"Patrón «{p.Name}» · {p.Steps} pasos · {p.Image.Height} filas ({AxisName(true)})", $"Pattern «{p.Name}» · {p.Steps} steps · {p.Image.Height} rows ({AxisName(false)})");
            stepsBox.Value = p.Steps;
            syncing = false;
        }
        Touch();
    }

    /// <summary>Moves the newest occurrence of <paramref name="token"/> from one order stack to the other.</summary>
    /// <param name="from">Stack to take it from (left unchanged when absent).</param>
    /// <param name="to">Stack to push it on.</param>
    /// <param name="token">The token.</param>
    static void MoveToken(Stack<object> from, Stack<object> to, object token)
    {
        var items = from.ToList();                 // newest first
        int i = items.IndexOf(token);
        if (i < 0) return;
        items.RemoveAt(i);
        from.Clear();
        for (int k = items.Count - 1; k >= 0; k--) from.Push(items[k]);
        to.Push(token);
    }

    /// <summary>The pattern that owns <paramref name="session"/>, or <c>null</c> when it was deleted.</summary>
    /// <param name="session">A pattern session.</param>
    SeqPattern? PatternOf(PaintSession session) => Doc.Patterns.FirstOrDefault(p => ReferenceEquals(p.Session, session));

    /// <summary>The newest step of <paramref name="order"/> that can still be applied (stale ones are dropped).</summary>
    /// <param name="order">Undo or redo order.</param>
    /// <param name="undo">True to check undo stacks, false for redo stacks.</param>
    /// <returns>The token, or <c>null</c>.</returns>
    object? Peek(Stack<object> order, bool undo)
    {
        while (order.TryPeek(out var t))
        {
            bool ok = t == ArrangementToken
                ? (undo ? state.Undo.Count : state.Redo.Count) > 0
                : t is PaintSession ps && PatternOf(ps) is not null && (undo ? ps.History.CanUndo : ps.History.CanRedo);
            if (ok) return t;
            order.Pop();
        }
        return null;
    }

    /// <inheritdoc/>
    /// <remarks>Undoes the newest step of all the room's histories; a pattern step first shows that pattern.</remarks>
    public void Undo()
    {
        if (Peek(undoOrder, undo: true) is { } t)
        {
            undoOrder.Pop();
            if (t == ArrangementToken) SwapArrangement(state.Undo, state.Redo);
            else if (t is PaintSession ps) { if (PatternOf(ps) is { } p && !ReferenceEquals(p, Current)) ShowPattern(p); ps.Undo(); }
            redoOrder.Push(t);
        }
        else Current?.Session.Undo();   // steps made before this window opened
    }

    /// <inheritdoc/>
    public void Redo()
    {
        if (Peek(redoOrder, undo: false) is { } t)
        {
            redoOrder.Pop();
            if (t == ArrangementToken) SwapArrangement(state.Redo, state.Undo);
            else if (t is PaintSession ps) { if (PatternOf(ps) is { } p && !ReferenceEquals(p, Current)) ShowPattern(p); ps.Redo(); }
            undoOrder.Push(t);
        }
        else Current?.Session.Redo();
    }

    /// <inheritdoc/>
    public bool CanUndo => Peek(undoOrder, undo: true) is not null || Current?.Session.History.CanUndo == true;

    /// <inheritdoc/>
    public bool CanRedo => Peek(redoOrder, undo: false) is not null || Current?.Session.History.CanRedo == true;

    /// <inheritdoc/>
    public string? UndoLabel => Peek(undoOrder, undo: true) switch
    {
        { } t when t == ArrangementToken => state.Undo.Peek().Key == "clips" ? "arrangement" : "sequencer",
        PaintSession ps => ps.History.UndoLabel,
        _ => Current?.Session.History.UndoLabel,
    };

    /// <inheritdoc/>
    public string? RedoLabel => Peek(redoOrder, undo: false) switch
    {
        { } t when t == ArrangementToken => state.Redo.Peek().Key == "clips" ? "arrangement" : "sequencer",
        PaintSession ps => ps.History.RedoLabel,
        _ => Current?.Session.History.RedoLabel,
    };

    /// <summary>Marks the arrangement / timing as changed (redraw now, recompose at the next tick).</summary>
    void Touch()
    {
        revision++;
        timeline.InvalidateVisual();
        UpdateArrangementButtons();
    }

    /// <summary>Enables the arrangement ↶ / ↷.</summary>
    void UpdateArrangementButtons()
    {
        arrUndo.IsEnabled = state.Undo.Count > 0;
        arrRedo.IsEnabled = state.Redo.Count > 0;
    }

    // ------------------------------------------------------------------------------------------
    //  Playback
    // ------------------------------------------------------------------------------------------

    /// <summary>The synthesis used for playing and rendering: the room's settings, glide off, loop from the toggle.</summary>
    PaintSynthSettings PlaySynth() => Doc.Synth with { Glide = false, Loop = state.Prefs.Loop || soloPattern is not null };

    /// <summary>Composes what should play now: the arrangement, or the solo pattern looped alone.</summary>
    SpectralImage ComposeNow()
    {
        if (soloPattern is { } p)
            return SequencerComposer.Compose(Doc, [new SeqClip(0, p.Id, 0, 0)], p.Steps);
        return SequencerComposer.Compose(Doc);
    }

    /// <summary>Play / Stop (Space) of the arrangement, or of the current pattern alone (▶ Pattern, Shift+Space).</summary>
    /// <param name="solo">True for the pattern preview.</param>
    /// <param name="restart">True: (re)start even if playing (used when the solo pattern changes).</param>
    public void TogglePlay(bool solo, bool restart = false)
    {
        var player = state.Player;
        if (player.State != PaintPlayState.Idle && !restart)
        {
            player.Stop();
            soloPattern = null;
            Say(Loc.S("■ Detenido.", "■ Stopped."), false);
            return;
        }
        if (solo && Current is null) return;
        soloPattern = solo ? Current : null;
        DetachComposed();
        composed = ComposeNow();
        composedSub = composed.Width / Math.Max(1, soloPattern?.Steps ?? Doc.TotalSteps);
        composed.Changed += OnComposedChanged;
        double from = soloPattern is null ? Math.Min(timeline.CursorStep, Doc.TotalSteps - 1) * composedSub : 0;
        if (player.State != PaintPlayState.Idle) { player.Stop(); }
        player.Play(composed, PlaySynth(), from);
        lastSignature = Signature();
    }

    /// <summary>Forwards edits of the playing canvas to the player (uploaded at its next tick).</summary>
    /// <param name="x0">First changed column.</param>
    /// <param name="x1">End of the changed columns.</param>
    void OnComposedChanged(int x0, int x1) { if (composed is not null) state.Player.OnImageEdited(composed, x0, x1); }

    /// <summary>Unsubscribes from the playing canvas.</summary>
    void DetachComposed()
    {
        if (composed is not null) composed.Changed -= OnComposedChanged;
    }

    /// <summary>Restarts the note with the current settings (loop / sound changes) when playing.</summary>
    void RestartIfPlaying()
    {
        if (state.Player.State == PaintPlayState.Idle) return;
        state.Player.UpdateSettings(PlaySynth());
    }

    /// <summary>Stores new sound settings (the player follows: gain/attack/release live, others restart).</summary>
    /// <param name="s">The settings.</param>
    void SetSynth(PaintSynthSettings s)
    {
        if (s == Doc.Synth) return;
        PushArrangementUndo("synth");
        Doc.Synth = s;
        Touch();
        if (state.Player.State != PaintPlayState.Idle) state.Player.UpdateSettings(PlaySynth());
    }

    /// <summary>A cheap signature of everything that affects the sound (see the file header).</summary>
    long Signature()
    {
        long h = revision * 1000003L;
        foreach (var p in Doc.Patterns)
            h = h * 31 + p.Id * 7919 + RuntimeHelpers.GetHashCode(p.Image) * 17 + p.Image.Version;
        return h;
    }

    /// <summary>Recomposes while playing: copies only the changed columns, or restarts when the size or timing changed.</summary>
    void Recompose()
    {
        if (composed is null || state.Player.State == PaintPlayState.Idle) return;
        if (soloPattern is not null && Doc.Find(soloPattern.Id) is null) { state.Player.Stop(); return; }
        var fresh = ComposeNow();
        if (fresh.Width == composed.Width && fresh.Height == composed.Height && fresh.Axis.Equals(composed.Axis))
        {
            bool timing = Math.Abs(fresh.DurationSeconds - composed.DurationSeconds) > 1e-9;
            SequencerComposer.CopyChanged(fresh, composed);
            if (timing)
            {
                composed.DurationSeconds = fresh.DurationSeconds;
                state.Player.Restart();
            }
        }
        else
        {
            DetachComposed();
            composed = fresh;
            composedSub = composed.Width / Math.Max(1, soloPattern?.Steps ?? Doc.TotalSteps);
            composed.Changed += OnComposedChanged;
            state.Player.Restart(composed);
        }
    }

    /// <summary>≈ 30 Hz: player tick, model polling, play lines, DSP read-out.</summary>
    void OnTick()
    {
        var player = state.Player;
        player.Tick();
        long sig = Signature();
        if (sig != lastSignature)
        {
            lastSignature = sig;
            timeline.InvalidateVisual();
            Recompose();
            if (Current is { } cp && !syncing && (int)(stepsBox.Value ?? 0) != cp.Steps) ShowPattern(cp);   // e.g. undo of a length change
        }
        double? col = player.CurrentColumn;
        if (col is { } c && composedSub > 0)
        {
            double step = c / composedSub;
            if (soloPattern is { } sp)
            {
                timeline.PlayStep = null;
                canvas.PlayColumn = ReferenceEquals(sp, Current) ? step % sp.Steps : null;
            }
            else
            {
                timeline.PlayStep = step;
                canvas.PlayColumn = PatternColumnAt(step);
            }
        }
        else
        {
            timeline.PlayStep = null;
            canvas.PlayColumn = null;
        }
        var engine = ctx.App.Engine;
        dspText.Text = player.State == PaintPlayState.Playing && engine.DspLoad >= 0
            ? $"DSP {engine.DspLoad * 100:0} %" + (engine.Underruns > 0 ? Loc.S($" · {engine.Underruns} cortes", $" · {engine.Underruns} dropouts") : "")
            : "";
    }

    /// <summary>Column of the edited pattern being heard at <paramref name="step"/> (inside one of its clips), or <c>null</c>.</summary>
    /// <param name="step">Arrangement step (fractional).</param>
    double? PatternColumnAt(double step)
    {
        if (Current is not { } p) return null;
        foreach (var c in Doc.Clips)
        {
            if (c.PatternId != p.Id) continue;
            double rel = step - c.StartStep;
            if (rel < 0 || rel >= Doc.ClipSteps(c)) continue;
            double inPattern = rel % p.Steps;
            return c.Reverse ? p.Steps - inPattern : inPattern;
        }
        return null;
    }

    /// <summary>Shows Play/Stop on the two transport buttons.</summary>
    void UpdatePlayButtons()
    {
        bool on = state.Player.State is PaintPlayState.Playing or PaintPlayState.Starting;
        playButton.Content = ToolUi.Content(on && soloPattern is null ? Loc.S("Detener", "Stop") : Loc.S("Tocar", "Play"), on && soloPattern is null ? "IconStop" : "IconPlay");
        soloButton.Content = ToolUi.Content(on && soloPattern is not null ? Loc.S("Detener", "Stop") : Loc.S("Patrón", "Pattern"), on && soloPattern is not null ? "IconStop" : "IconPlay");
        if (!on)
        {
            timeline.PlayStep = null;
            canvas.PlayColumn = null;
            if (state.Player.State == PaintPlayState.Idle) soloPattern = null;
        }
    }

    // ------------------------------------------------------------------------------------------
    //  Render, hand-off, code
    // ------------------------------------------------------------------------------------------

    /// <summary>Render WAV: composes the arrangement and renders one pass with Csound (true-peak −1 dBTP, 24-bit).</summary>
    async Task RenderAsync()
    {
        if (rendering) { renderCancel?.Cancel(); return; }
        if (ctx.App.PickSaveFile is not { } pick) return;
        if (await pick("Image_Sequencer.wav", "wav") is not { } path) return;
        rendering = true;
        renderCancel = new CancellationTokenSource();
        renderButton.Content = ToolUi.Content(Loc.S("Cancelar render", "Cancel render"), "IconStop");
        var progress = new Progress<double>(p => Say(Loc.S($"Renderizando con Csound… {p * 100:0} %", $"Rendering with Csound… {p * 100:0} %"), false));
        try
        {
            var img = SequencerComposer.Compose(Doc);
            var r = await PaintRenderer.RenderAsync(img, Doc.Synth with { Glide = false, Loop = false }, path, progress, renderCancel.Token);
            if (r.Success)
            {
                string msg = Loc.S($"✓ WAV guardado: {path} ({r.Seconds:0.0} s a {Doc.Synth.RenderSampleRate} Hz, normalizado {r.AppliedGainDb:+0.0;-0.0} dB)",
                                   $"✓ WAV saved: {path} ({r.Seconds:0.0} s at {Doc.Synth.RenderSampleRate} Hz, normalised {r.AppliedGainDb:+0.0;-0.0} dB)");
                Say(msg, false);
                ctx.App.PaintInfo("Paint Rooms · Image Sequencer: " + msg);
            }
            else Say(Loc.S("✗ El render falló: ", "✗ The render failed: ") + (r.Errors.FirstOrDefault() ?? "?"), true);
        }
        catch (OperationCanceledException) { Say(Loc.S("Render cancelado.", "Render cancelled."), true); }
        catch (Exception ex) { Say(Loc.S("✗ El render falló: ", "✗ The render failed: ") + ex.Message, true); }
        finally
        {
            rendering = false;
            renderButton.Content = ToolUi.Content(Loc.S("Renderizar WAV", "Render WAV"), "IconRender");
        }
    }

    /// <summary>«→ Paint Synth»: the composed arrangement becomes Paint Synth's canvas (undoable there) and that tab is shown.</summary>
    public void SendToPaintSynth()
    {
        var img = SequencerComposer.Compose(Doc);
        // Adopt the sequencer's sound (glide off keeps the steps crisp), keep Paint Synth's loop choice.
        shared.Synth = shared.Synth with
        {
            Mode = Doc.Synth.Mode, AttackMs = Doc.Synth.AttackMs, ReleaseMs = Doc.Synth.ReleaseMs, Glide = false,
            Curve = Doc.Synth.Curve, Floor = Doc.Synth.Floor, GainDb = Doc.Synth.GainDb,
        };
        shared.ReplaceImage(img, "import");
        shared.ImageName = "Image_Sequencer";
        state.Player.Stop();
        if (root.FindAncestorOfType<TabControl>() is { } tabs) tabs.SelectedIndex = 0;
        Say(Loc.S($"Arreglo enviado a Paint Synth: {img.Width} columnas × {img.Height} filas, {img.DurationSeconds:0.##} s.", $"Arrangement sent to Paint Synth: {img.Width} columns × {img.Height} rows, {img.DurationSeconds:0.##} s."), false);
    }

    /// <summary>«Show Csound code»: the standalone .csd of the composed arrangement, opened in the editor.</summary>
    void ShowCode()
    {
        try
        {
            string csd = PaintRenderer.ExportCode(SequencerComposer.Compose(Doc), Doc.Synth with { Glide = false, Loop = false }, "Image_Sequencer");
            ctx.App.OpenPaintCode(csd);
            Say(Loc.S($"Código abierto en el editor: {csd}", $"Code opened in the editor: {csd}"), false);
        }
        catch (Exception ex) { Say(ex.Message, true); }
    }

    // ------------------------------------------------------------------------------------------
    //  Menus
    // ------------------------------------------------------------------------------------------

    /// <summary>Sound ▾: smooth noise, curve, automatic level, render normalisation and sample rate.</summary>
    /// <param name="menu">The fresh flyout.</param>
    void BuildSoundMenu(MenuFlyout menu)
    {
        var s = Doc.Synth;
        menu.Items.Add(Item((s.AutoLevel ? "☑ " : "☐ ") + Loc.S("Nivel automático", "Automatic level"), () => SetSynth(Doc.Synth with { AutoLevel = !Doc.Synth.AutoLevel })));
        menu.Items.Add(Item((s.SmoothNoise ? "☑ " : "☐ ") + Loc.S("Ruido suave (2 osciladores por fila)", "Smooth noise (2 oscillators per row)"), () => SetSynth(Doc.Synth with { SmoothNoise = !Doc.Synth.SmoothNoise })));
        menu.Items.Add(Item(Loc.S($"Curva brillo → volumen: {s.Curve:0.##}…", $"Brightness → loudness curve: {s.Curve:0.##}…"), () => _ = AskAsync(Loc.S("Curva (exponente)", "Curve (exponent)"), 0.5, 4, s.Curve, 0.25, v => Doc.Synth with { Curve = v })));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item((s.NormalizeRender ? "☑ " : "☐ ") + Loc.S("Normalizar el render a −1 dBTP (pico real)", "Normalise renders to −1 dBTP (true peak)"), () => SetSynth(Doc.Synth with { NormalizeRender = !Doc.Synth.NormalizeRender })));
        var rates = new MenuItem { Header = Loc.S($"Frecuencia del render: {s.RenderSampleRate} Hz", $"Render sample rate: {s.RenderSampleRate} Hz") };
        foreach (int r in new[] { 44100, 48000, 88200, 96000 })
        {
            int rr = r;
            rates.Items.Add(Item((s.RenderSampleRate == r ? "● " : "○ ") + r + " Hz", () => SetSynth(Doc.Synth with { RenderSampleRate = rr })));
        }
        menu.Items.Add(rates);
    }

    /// <summary>Asks for one number and applies it to the sound settings.</summary>
    async Task AskAsync(string label, double min, double max, double value, double step, Func<double, PaintSynthSettings> make)
    {
        if (await PaintParameterDialog.AskAsync(ctx.Window, Loc.S("Sonido", "Sound"), label, min, max, value, step, "0.###") is { } v) SetSynth(make(v));
    }

    /// <summary>File ▾: new pattern, import a picture as a new pattern, and the colour mapping used when importing.</summary>
    /// <param name="menu">The fresh flyout.</param>
    /// <remarks>
    /// Same wording as Paint Synth's File ▾. The colour mapping is the shared canvas preference
    /// (<c>state.Prefs.Canvas.ImportMapping</c>), so choosing it here also changes Paint Synth's and
    /// vice versa; the import dialog shows and can change it as well.
    /// </remarks>
    void BuildFileMenu(MenuFlyout menu)
    {
        menu.Items.Add(Item(Loc.S("Patrón nuevo (vacío)", "New pattern (empty)"), NewPattern));
        menu.Items.Add(Item(Loc.S("Importar imagen como patrón…", "Import picture as pattern…"), () => _ = ImportPatternAsync()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item((HasUnsavedChanges ? "● " : "") + Loc.S("Guardar para la próxima sesión", "Save for next session"), SaveChanges));
        menu.Items.Add(new Separator());
        var cprefs = state.Prefs.Canvas;
        var mapping = new MenuItem { Header = Loc.S("Al importar, los colores son…", "When importing, colours mean…") };
        (ColorMapping M, string Es, string En)[] maps =
        [
            (ColorMapping.Brightness, "Brillo → ambos lados (mono)", "Brightness → both sides (mono)"),
            (ColorMapping.RedGreen, "Rojo → izquierda, verde → derecha", "Red → left, green → right"),
            (ColorMapping.BrightnessHuePan, "Brillo → volumen, color → panorama", "Brightness → level, hue → pan"),
            (ColorMapping.RgbNoise, "RGB: R = izquierda, G = derecha, B = ruido", "RGB: R = left, G = right, B = noise"),
            (ColorMapping.RgbaNoiseMask, "RGBA: RGB + alfa = máscara", "RGBA: RGB + alpha = mask"),
        ];
        foreach (var m in maps)
        {
            var mm = m.M;
            mapping.Items.Add(Item((cprefs.ImportMapping == mm ? "● " : "○ ") + Loc.S(m.Es, m.En), () => cprefs.ImportMapping = mm));
        }
        menu.Items.Add(mapping);
    }

    /// <summary>Examples ▾: reload the starter example, or start with an empty arrangement (both undoable only as a whole reload).</summary>
    /// <param name="menu">The fresh flyout.</param>
    void BuildExamplesMenu(MenuFlyout menu)
    {
        menu.Items.Add(Item(Loc.S("Ejemplo: batería + bajo + arpegio (8 compases)", "Example: drums + bass + arpeggio (8 bars)"), () => LoadDocument(SequencerDocument.Starter(state.Prefs.Canvas))));
        menu.Items.Add(Item(Loc.S("Empezar vacío (un patrón, sin clips)", "Start empty (one pattern, no clips)"), () =>
        {
            var d = new SequencerDocument(state.Prefs.Canvas, Doc.AxisPreset) { Tempo = Doc.Tempo, StepsPerBeat = Doc.StepsPerBeat, BeatsPerBar = Doc.BeatsPerBar, Synth = Doc.Synth };
            d.AddPattern(Loc.S("Patrón 1", "Pattern 1"), d.StepsPerBar);
            LoadDocument(d);
        }));
    }

    /// <summary>Replaces the whole document (stops playback, clears the arrangement history).</summary>
    /// <param name="d">The new document.</param>
    void LoadDocument(SequencerDocument d)
    {
        state.Player.Stop();
        state.Doc = d;
        state.Undo.Clear();
        state.Redo.Clear();
        timeline.Document = d;
        RefreshLibrary();
        ShowPattern(d.Patterns.FirstOrDefault());
        SelectClip(null);
        SyncControls();
        Touch();
    }

    /// <summary>Axis ▾: the three pitch axis presets (retunes every pattern, keeping pitches in Hz).</summary>
    /// <param name="menu">The fresh flyout.</param>
    void BuildAxisMenu(MenuFlyout menu)
    {
        (string Es, string En)[] names =
        [
            ("Notas C1…C9 (97 filas: del bombo a los platillos)", "Notes C1…C9 (97 rows: kick drum to cymbals)"),
            ("Notas C2…C7 (61 filas, teclado compacto)", "Notes C2…C7 (61 rows, compact keyboard)"),
            ("Logarítmico 40 Hz…16 kHz (192 filas, altura libre)", "Logarithmic 40 Hz…16 kHz (192 rows, free pitch)"),
        ];
        for (int i = 0; i < names.Length; i++)
        {
            int k = i;
            menu.Items.Add(Item((Doc.AxisPreset == i ? "● " : "○ ") + Loc.S(names[i].Es, names[i].En), () =>
            {
                Doc.SetAxis(k);
                ShowPattern(Current);
                Touch();
                Say(Loc.S("Eje: ", "Axis: ") + Doc.Axis.Describe(), false);
            }));
        }
    }

    /// <summary>The «?» quick help.</summary>
    void ShowHelp()
    {
        string text = Loc.S(
            "CÓMO FUNCIONA\n• Arriba pintas un PATRÓN: cada columna es un paso, cada fila una nota (el eje de notas va de C1 a C9).\n• Abajo, en el ARREGLO, haces clic para poner el patrón elegido, o arrastras en celdas vacías para dibujar un clip del largo que quieras; arrastra el borde derecho de un clip para alargarlo o acortarlo (con «Estirar» el patrón se comprime o estira; sin él se repite o se corta). Arrastra un clip para moverlo, doble clic para editarlo, clic derecho para quitarlo.\n• Pintar más allá del final de un patrón lo alarga.\n• Cada clip puede repetirse, transponerse (en semitonos), ir al revés, invertirse y tener su ganancia (panel CLIP).\n• El control «Ruido» del pincel vuelve ruidosa una nota (platillos, cajas); 0 = tono puro.\n• Tocar (Espacio) suena desde el cursor; «Patrón» (Mayús+Espacio) escucha solo el patrón.\n\nATAJOS\nP lápiz · A aerógrafo · S spray · H armónicos · L línea · R rectángulo · E goma · B desenfocar · M selección\nCtrl+Z / Ctrl+Y deshace lo último que cambiaste (patrón, arreglo o ajuste) · Supr quita el clip elegido · Inicio = cursor al comienzo\n\nGUARDAR\nLos patrones y el arreglo quedan para la próxima sesión solo si los guardas (Archivo ▾ › Guardar para la próxima sesión, o «Guardar» al cerrar).\n\nLa guía completa con un tutorial está en la documentación (Paint Rooms › Image Sequencer).",
            "HOW IT WORKS\n• At the top you paint a PATTERN: each column is a step, each row a note (the note axis runs from C1 to C9).\n• Below, in the ARRANGEMENT, click to place the chosen pattern, or drag on empty cells to draw a clip of any length; drag a clip's right edge to make it longer or shorter (with «Stretch» the pattern is squeezed or stretched; without it, it loops or is cut). Drag a clip to move it, double-click to edit it, right-click to remove it.\n• Painting past the end of a pattern makes it longer.\n• Every clip can repeat, transpose (in semitones), play backwards, invert and have its own gain (CLIP panel).\n• The brush's «Noise» control makes a note noisy (cymbals, snares); 0 = pure tone.\n• Play (Space) sounds from the cursor; «Pattern» (Shift+Space) plays the pattern alone.\n\nSHORTCUTS\nP pencil · A airbrush · S spray · H harmonics · L line · R rectangle · E eraser · B blur · M selection\nCtrl+Z / Ctrl+Y undo your newest change (pattern, arrangement or setting) · Del removes the selected clip · Home = cursor to start\n\nSAVING\nPatterns and arrangement are kept for the next session only when you save them (File ▾ › Save for next session, or «Save» when closing).\n\nThe full guide with a tutorial is in the documentation (Paint Rooms › Image Sequencer).");
        _ = new InfoDialog(Loc.S("Image Sequencer · ayuda", "Image Sequencer · help"), text).ShowDialog(ctx.Window);
    }

    // ------------------------------------------------------------------------------------------
    //  Controls ↔ document
    // ------------------------------------------------------------------------------------------

    /// <summary>Writes the document's timing and sound into the controls (without triggering their handlers).</summary>
    void SyncControls()
    {
        syncing = true;
        var d = Doc;
        tempoBox.Value = (decimal)d.Tempo;
        stepsBeatBox.Value = d.StepsPerBeat;
        beatsBarBox.Value = d.BeatsPerBar;
        barsBox.Value = d.Bars;
        lanesBox.Value = d.Lanes;
        gateBox.Value = (decimal)(d.Gate * 100);
        modeBox.SelectedIndex = d.Synth.Mode == SynthMode.NoiseBands ? 1 : 0;
        gainBox.Value = (decimal)d.Synth.GainDb;
        attackBox.Value = (decimal)d.Synth.AttackMs;
        releaseBox.Value = (decimal)d.Synth.ReleaseMs;
        loopToggle.IsChecked = state.Prefs.Loop;
        syncing = false;
        UpdateArrangementButtons();
    }

    /// <summary>The tools grid and brush controls (Noise included) of the left column.</summary>
    /// <returns>The panel.</returns>
    Control BuildTools()
    {
        var cprefs = state.Prefs.Canvas;
        (PaintTool Tool, string Glyph, string Es, string En, string Key)[] tools =
        [
            (PaintTool.Pencil, "✎", "Lápiz: un clic = una nota en un paso.", "Pencil: one click = one note on one step.", "P"),
            (PaintTool.Airbrush, "◍", "Aerógrafo: notas suaves.", "Airbrush: soft notes.", "A"),
            (PaintTool.Spray, "⁘", "Spray: puntos al azar (ritmos sueltos, granos).", "Spray: random dots (loose rhythms, grains).", "S"),
            (PaintTool.Harmonics, "≣", "Pincel armónico: la nota y sus armónicos.", "Harmonics brush: the note and its harmonics.", "H"),
            (PaintTool.Line, "╱", "Línea: escalas y glissandos escalonados.", "Line: scales and stepped glissandos.", "L"),
            (PaintTool.Rectangle, "▭", "Rectángulo: acordes y bloques de ruido.", "Rectangle: chords and noise blocks.", "R"),
            (PaintTool.Eraser, "⌫", "Goma (también el botón derecho).", "Eraser (also the right button).", "E"),
            (PaintTool.Blur, "≈", "Desenfocar.", "Blur.", "B"),
            (PaintTool.Select, "⬚", "Selección rectangular (Ctrl+C/X/V, Supr)." , "Rectangle selection (Ctrl+C/X/V, Del).", "M"),
            .. PaintSelectionUi.ShapedTools(),
        ];
        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var t in tools)
        {
            var b = new ToggleButton
            {
                Content = PaintToolIcons.Content(t.Glyph), Width = 40, Height = 34, Margin = new Thickness(2), Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(7),
            };
            ToolTip.SetTip(b, Loc.S(t.Es, t.En) + "  (" + t.Key + ")");
            Avalonia.Automation.AutomationProperties.SetName(b, t.Tool.ToString());
            var tool = t.Tool;
            b.Click += (_, _) => SelectTool(tool);
            toolButtons[tool] = b;
            grid.Children.Add(b);
        }
        sizeSlider.Value = cprefs.BrushSize;
        opacitySlider.Value = cprefs.Opacity;
        noiseSlider.Value = cprefs.BrushNoise;
        panSlider.Value = cprefs.Pan;
        noiseOnlyBox.IsChecked = cprefs.NoiseOnly;
        showNoiseBox.IsChecked = cprefs.ShowNoise;
        canvas.ShowNoise = cprefs.ShowNoise;
        noiseOnlyBox.Content = Loc.S("Solo ruido", "Noise only");
        showNoiseBox.Content = Loc.S("Ver capa de ruido", "Show noise layer");
        ToolTip.SetTip(noiseSlider, Loc.S("Ruido de la pintura: 0 = tono (seno), 1 = banda de ruido (platillos, cajas). No cambia el volumen.", "Noise of the paint: 0 = tone (sine), 1 = noise band (cymbals, snares). It does not change the loudness."));
        ToolTip.SetTip(noiseOnlyBox, Loc.S("Cambia solo la cantidad de ruido de lo ya pintado.", "Changes only the noise amount of what is already painted."));
        ToolTip.SetTip(panSlider, Loc.S("Panorama: −1 izquierda, +1 derecha.", "Pan: −1 left, +1 right."));
        // Each brush slider also shows the brush on the canvas for a moment (size, level, pan, noise).
        foreach (var s in new[] { sizeSlider, opacitySlider, noiseSlider, panSlider })
            s.ValueChanged += (_, _) => { OnBrushChanged(); canvas.ShowParameterPreview(PaintCanvas.ParameterPreview.Brush); };
        noiseOnlyBox.IsCheckedChanged += (_, _) => OnBrushChanged();
        showNoiseBox.IsCheckedChanged += (_, _) => { cprefs.ShowNoise = showNoiseBox.IsChecked == true; canvas.ShowNoise = cprefs.ShowNoise; };
        var stack = new StackPanel { Spacing = 3, Margin = new Thickness(0, 8, 0, 0) };
        stack.Children.Add(Caption(Loc.S("HERRAMIENTAS", "TOOLS")));
        stack.Children.Add(grid);
        stack.Children.Add(Caption(Loc.S("PINCEL", "BRUSH")));
        stack.Children.Add(Labeled(Loc.S("Tamaño", "Size"), sizeText, sizeSlider));
        stack.Children.Add(Labeled(Loc.S("Brillo", "Level"), opacityText, opacitySlider));
        stack.Children.Add(Labeled(Loc.S("Ruido", "Noise"), noiseText, noiseSlider));
        stack.Children.Add(noiseOnlyBox);
        stack.Children.Add(Labeled(Loc.S("Panorama", "Pan"), panText, panSlider));
        stack.Children.Add(showNoiseBox);
        stack.Children.Add(PaintSelectionUi.OptionsPanel(canvas, cprefs));
        OnBrushChanged();
        SelectTool(toolButtons.ContainsKey(cprefs.Tool) ? cprefs.Tool : PaintTool.Pencil);
        return stack;
    }

    /// <summary>Makes a tool current.</summary>
    /// <param name="tool">The tool.</param>
    void SelectTool(PaintTool tool)
    {
        state.Prefs.Canvas.Tool = tool;
        canvas.Tool = tool;
        foreach (var (t, b) in toolButtons) b.IsChecked = t == tool;
    }

    /// <summary>Copies the brush controls into the canvas and the preferences.</summary>
    void OnBrushChanged()
    {
        var c = state.Prefs.Canvas;
        c.BrushSize = sizeSlider.Value;
        c.Opacity = opacitySlider.Value;
        c.BrushNoise = noiseSlider.Value;
        c.Pan = panSlider.Value;
        c.NoiseOnly = noiseOnlyBox.IsChecked == true;
        canvas.Brush = new BrushSettings { Size = c.BrushSize, Opacity = c.Opacity, Hardness = c.Hardness, Pan = c.Pan, Noise = c.BrushNoise, NoiseOnly = c.NoiseOnly, Harmonics = 4, Rolloff = 1.5 };
        sizeText.Text = $"{c.BrushSize:0} px";
        opacityText.Text = $"{c.Opacity * 100:0} %";
        noiseText.Text = c.BrushNoise < 0.005 ? Loc.S("tono", "tone") : c.BrushNoise > 0.995 ? Loc.S("ruido", "noise") : $"{c.BrushNoise * 100:0} %";
        panText.Text = c.Pan < -0.02 ? Loc.S($"{-c.Pan * 100:0} % izq.", $"{-c.Pan * 100:0} % L") : c.Pan > 0.02 ? Loc.S($"{c.Pan * 100:0} % der.", $"{c.Pan * 100:0} % R") : Loc.S("centro", "centre");
    }

    // ------------------------------------------------------------------------------------------
    //  Small helpers
    // ------------------------------------------------------------------------------------------

    /// <summary>Shows a message at the bottom right.</summary>
    void Say(string text, bool warning)
    {
        messageText.Text = text;
        messageText.Foreground = warning ? AppTheme.Brush("Warn") : AppTheme.Brush("AccentHi");
        ToolTip.SetTip(messageText, text);
    }

    /// <summary>The last status message (tests).</summary>
    public string LastMessage => messageText.Text ?? "";

    static WrapPanel Row() => new() { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 10, 2) };

    static void AddAll(Panel panel, params Control[] controls)
    {
        foreach (var c in controls) { c.Margin = new Thickness(0, 2, 6, 2); panel.Children.Add(c); }
    }

    static TextBlock Small() => new() { FontSize = 11, Foreground = AppTheme.Brush("TextDim"), HorizontalAlignment = HorizontalAlignment.Right };

    static TextBlock Caption(string text) =>
        new() { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = AppTheme.Brush("AccentHi"), Margin = new Thickness(2, 6, 0, 2), VerticalAlignment = VerticalAlignment.Center };

    static Control Labeled(string label, TextBlock value, Slider slider)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = AppTheme.Brush("Text") });
        Grid.SetColumn(value, 1);
        head.Children.Add(value);
        return new StackPanel { Spacing = 0, Children = { head, slider } };
    }

    /// <summary>A small square button with a glyph.</summary>
    static Button SmallButton(string glyph, string tip, Action click)
    {
        var b = new Button { Content = glyph, MinWidth = 32, Padding = new Thickness(6, 3), Margin = new Thickness(0, 0, 4, 4), HorizontalContentAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    static MenuItem Item(string header, Action click)
    {
        var m = new MenuItem { Header = header };
        m.Click += (_, _) => click();
        return m;
    }

    static Button MenuButton(string text, string tip, Action<MenuFlyout> build)
    {
        var b = ToolUi.Button(text, tip, () => { });
        b.Click += (_, _) => { var menu = new MenuFlyout(); build(menu); menu.ShowAt(b); };
        return b;
    }
}

/// <summary>Small modal dialog asking for one line of text (pattern names).</summary>
public sealed class TextPromptDialog : Window
{
    /// <summary>The accepted text, or <c>null</c> when cancelled.</summary>
    string? result;

    /// <summary>Builds the dialog.</summary>
    /// <param name="title">Title and heading.</param>
    /// <param name="value">Initial text (selected).</param>
    TextPromptDialog(string title, string value)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Background = AppTheme.Brush("Bg");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var box = new TextBox { Text = value, MaxLength = 60 };
        var ok = new Button { Content = Loc.S("Aceptar", "OK"), Classes = { "primary" }, MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = Loc.S("Cancelar", "Cancel"), MinWidth = 90, IsCancel = true };
        ok.Click += (_, _) => { result = box.Text; Close(); };
        cancel.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 14,
            Children =
            {
                new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = AppTheme.Brush("Text") },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        Opened += (_, _) => { box.Focus(); box.SelectAll(); };
    }

    /// <summary>Shows the dialog over <paramref name="owner"/>; returns the text or <c>null</c>.</summary>
    /// <param name="owner">Owner window.</param>
    /// <param name="title">Title and heading.</param>
    /// <param name="value">Initial text.</param>
    public static async Task<string?> AskAsync(Window owner, string title, string value)
    {
        var d = new TextPromptDialog(title, value) { Icon = owner.Icon };
        await d.ShowDialog(owner);
        return d.result;
    }
}
