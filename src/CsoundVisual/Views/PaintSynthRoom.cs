// PaintSynthRoom.cs — Paint Rooms: the «Paint Synth» room (first tab of the Paint Rooms window).
//
// Role: compose and design sound by PAINTING: a time × pitch canvas (PaintCanvas) with brushes,
// image operations, picture import, starter examples, and a transport that plays the picture live
// through Csound or renders it to WAV. Built in code (like the other tool windows), themed through
// AppTheme, every visible string bilingual through Loc.S.
//
// Layout:
//   row 1  transport (Play/Stop, Loop, ⏮, Render WAV, Show Csound code) · synthesis (mode,
//          duration, gain, attack, release, glide, «Sound ▾» with the rarer settings)
//   row 2  pitch axis (kind, low/high Hz, rows or steps, .scl, base Hz) · columns · menus
//          (File, Image, Examples, Help)
//   left   tool palette + brush settings (size, opacity, hardness, pan, NOISE amount with «noise
//          only» and «show noise layer», harmonics)
//   centre the canvas with scroll bars; bottom: hover read-out, messages, DSP load.
//
// All state lives in the shared PaintSession (MainViewModel.PaintSession): this class only builds
// controls and forwards changes; the live player (session.Player) follows edits and settings.
// UI thread only.
//
// How the pieces talk (all on the UI thread):
//   * controls → session: every control's change handler writes the session (Synth settings via
//     SetSynth, the axis via ApplyAxis → session.Retune, the duration directly on the image) and the
//     persisted preferences (prefs = PaintRoomSettings, saved by the window).
//   * session → controls: SynthChanged / ImageReplaced call SyncFromSession, which writes every
//     control back. The «syncing» flag is raised meanwhile so those writes do not re-enter the
//     change handlers (which would loop or record spurious undo steps).
//   * session → player: SynthChanged → Player.UpdateSettings (live channels or a restart),
//     ImageReplaced → Player.Restart, PixelsChanged → Player.OnImageEdited (the dirty columns are
//     uploaded to the Csound tables on the next Tick).
//   * timer (≈ 30 Hz DispatcherTimer) → Player.Tick, the play line on the canvas, the DSP / dropout
//     read-out from the app's CsoundEngine.
//   * long work runs off the UI thread and comes back to it through await: picture decoding and
//     fitting (Task.Run), the offline render (PaintRenderer, private Csound engine).
// The room may exist without a player (Csound not found): every player call is null-conditional.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CsoundVisual.Controls;
using CsoundVisual.Localization;
using CsoundVisual.Paint;
using CsoundVisual.Theming;

namespace CsoundVisual.Views;

/// <summary>The «Paint Synth» room: paint a picture, hear it through Csound (see the file header).</summary>
public sealed class PaintSynthRoom : IPaintRoom
{
    /// <summary>Window context: the owner window, the shared session and the app services (engine, pickers, editor).</summary>
    readonly PaintRoomContext ctx;

    /// <summary>The shared Paint Rooms session (image, synthesis settings, history, live player).</summary>
    readonly PaintSession session;

    /// <summary>Persisted preferences (axis, brush, import choices); the same object as <c>session.Settings</c>.</summary>
    readonly PaintRoomSettings prefs;

    /// <summary>The drawing surface.</summary>
    readonly PaintCanvas canvas = new();

    /// <summary>Time scroll bar (units = columns, like <see cref="PaintCanvas.ScrollX"/>).</summary>
    readonly ScrollBar hScroll = new() { Orientation = Orientation.Horizontal, Height = 12 };

    /// <summary>Pitch scroll bar (units = rows hidden above the view, like <see cref="PaintCanvas.ScrollY"/>).</summary>
    readonly ScrollBar vScroll = new() { Orientation = Orientation.Vertical, Width = 12 };

    /// <summary>≈ 30 Hz UI timer (33 ms, render priority) that runs <see cref="OnTick"/>.</summary>
    readonly DispatcherTimer timer;

    /// <summary>Root of the room's content (<see cref="Content"/>): tool rows on top, status at the bottom, palette left, canvas centre.</summary>
    readonly DockPanel root = new();

    // ---- transport & synthesis controls

    /// <summary>Play / Stop (its content follows the player state, see <see cref="UpdatePlayButton"/>).</summary>
    readonly Button playButton;

    /// <summary>Loop on/off (<see cref="PaintSynthSettings.Loop"/>).</summary>
    readonly ToggleButton loopToggle;

    /// <summary>Render WAV; becomes «Cancel render» while a render runs.</summary>
    readonly Button renderButton;

    /// <summary>Synthesis mode; the item index equals the <see cref="SynthMode"/> value.</summary>
    readonly ComboBox modeBox = new() { Width = 150, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Duration of the whole canvas (seconds).</summary>
    readonly NumericUpDown durationBox;

    /// <summary>Output gain (dB) on top of the automatic level.</summary>
    readonly NumericUpDown gainBox;

    /// <summary>Per-row attack time (ms).</summary>
    readonly NumericUpDown attackBox;

    /// <summary>Per-row release time (ms).</summary>
    readonly NumericUpDown releaseBox;

    /// <summary>Glide: blend neighbouring columns (on) or step from column to column (off).</summary>
    readonly ToggleButton glideToggle;

    // ---- axis controls

    /// <summary>Axis kind; the item index equals the <see cref="PitchAxisKind"/> value.</summary>
    readonly ComboBox axisBox = new() { Width = 196, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Lowest frequency of the axis (Hz).</summary>
    readonly NumericUpDown lowBox;

    /// <summary>Highest frequency of the axis (Hz).</summary>
    readonly NumericUpDown highBox;

    /// <summary>Row count of the continuous axes.</summary>
    readonly NumericUpDown rowsBox;

    /// <summary>Steps per octave of the equal-tempered axis.</summary>
    readonly NumericUpDown stepsBox;

    /// <summary>Frequency of scale degree 0 (Hz) for the discrete axes.</summary>
    readonly NumericUpDown baseBox;

    /// <summary>Number of columns (time resolution); changing it resamples the image.</summary>
    readonly NumericUpDown widthBox;

    /// <summary>«Load .scl…», visible only on the Scala axis.</summary>
    readonly Button sclButton;

    /// <summary>Captioned wrappers of <see cref="rowsBox"/> / <see cref="stepsBox"/>, shown or hidden per axis kind.</summary>
    readonly StackPanel rowsPanel, stepsPanel;

    // ---- brush controls

    /// <summary>The palette's toggle button of each tool (exactly one is checked).</summary>
    readonly Dictionary<PaintTool, ToggleButton> toolButtons = [];

    /// <summary>Brush diameter in screen pixels (1…120).</summary>
    readonly Slider sizeSlider = new() { Minimum = 1, Maximum = 120, Width = 150 };

    /// <summary>Paint level 0.02…1 (brightness = loudness).</summary>
    readonly Slider opacitySlider = new() { Minimum = 0.02, Maximum = 1, Width = 150 };

    /// <summary>Edge hardness 0 (soft) … 1 (crisp).</summary>
    readonly Slider hardSlider = new() { Minimum = 0, Maximum = 1, Width = 150 };

    /// <summary>Paint pan −1 (left) … +1 (right).</summary>
    readonly Slider panSlider = new() { Minimum = -1, Maximum = 1, Width = 150 };

    /// <summary>Noise amount painted into the noise layer: 0 = tone (sine) … 1 = noise band.</summary>
    readonly Slider noiseSlider = new() { Minimum = 0, Maximum = 1, Width = 150 };

    /// <summary>«Noise only»: the brush changes only the noise amount, not the levels.</summary>
    readonly CheckBox noiseOnlyBox = new();

    /// <summary>«Show noise»: draw the noise layer as a grainy texture on the canvas.</summary>
    readonly CheckBox showNoiseBox = new();

    /// <summary>Harmonics brush: number of partials (fundamental included).</summary>
    NumericUpDown harmonicsBox = null!;   // created in BuildPalette

    /// <summary>Harmonics brush: roll-off exponent (partial k at k^−rolloff).</summary>
    NumericUpDown rolloffBox = null!;     // created in BuildPalette

    /// <summary>Harmonics brush: odd partials only.</summary>
    readonly CheckBox oddBox = new();

    /// <summary>The harmonics options, visible only while the harmonics tool is selected.</summary>
    readonly StackPanel harmonicsPanel = new() { Spacing = 4 };

    /// <summary>The «SELECTION» options (feather), shown while a selection tool is active (built in <see cref="BuildPalette"/>).</summary>
    StackPanel? selectionPanel;

    /// <summary>Value captions shown above the four brush sliders.</summary>
    readonly TextBlock sizeText = Small(), opacityText = Small(), hardText = Small(), panText = Small(), noiseText = Small();

    // ---- status

    /// <summary>Bottom-left hover read-out (time, pitch, levels), fed by <see cref="PaintCanvas.Hover"/>.</summary>
    readonly TextBlock hoverText = new() { FontSize = 12, Foreground = AppTheme.Brush("TextDim"), TextTrimming = TextTrimming.CharacterEllipsis };

    /// <summary>Bottom-right message line (see <see cref="Say"/>).</summary>
    readonly TextBlock messageText = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right };

    /// <summary>DSP load and dropout count while playing.</summary>
    readonly TextBlock dspText = new() { FontSize = 12, Foreground = AppTheme.Brush("TextDim"), Margin = new Thickness(16, 0, 0, 0), MinWidth = 120 };

    /// <summary>True while code writes the controls (<see cref="SyncFromSession"/>, <see cref="SyncScrollBars"/>): change handlers ignore those writes.</summary>
    bool syncing;

    /// <summary>True while a WAV render is in progress (the render button then cancels it).</summary>
    bool rendering;

    /// <summary>Engine underrun count already accounted for in the current play.</summary>
    int dropoutsSeen;          // engine dropouts already noticed during this play

    /// <summary>Whether the «try cheaper settings» hint was already shown in the current play.</summary>
    bool dropoutHintShown;     // the «try cheaper settings» hint was shown for this play

    /// <summary>Cancels the render in progress (second click on the render button, or <see cref="Dispose"/>).</summary>
    CancellationTokenSource? renderCancel;

    /// <summary>Creates the room for the window context <paramref name="context"/>.</summary>
    /// <param name="context">Window context (owner window, shared session, app services).</param>
    /// <remarks>
    /// Builds the whole UI in code, wires it to the session and the live player, starts the ≈ 30 Hz
    /// timer and shows the session's current state. Must run on the UI thread.
    /// </remarks>
    public PaintSynthRoom(PaintRoomContext context)
    {
        ctx = context;
        session = context.Session;
        prefs = session.Settings;
        canvas.Session = session;

        // ================= transport =================
        playButton = ToolUi.Button(Loc.S("Tocar", "Play"),
            Loc.S("Toca el lienzo con Csound desde el cursor amarillo (Espacio). Pintar mientras suena se escucha al instante. Si Csound está detenido, se inicia solo.",
                  "Plays the canvas through Csound from the yellow cursor (Space). Painting while it plays is heard at once. If Csound is stopped it starts by itself."),
            TogglePlay, "IconPlay", primary: true);
        playButton.MinWidth = 100;
        loopToggle = ToolUi.Toggle(Loc.S("Loop", "Loop"), Loc.S("Repetir el lienzo sin fin mientras suena.", "Repeat the canvas endlessly while playing."), "IconRefresh");
        loopToggle.IsChecked = session.Synth.Loop;
        loopToggle.IsCheckedChanged += (_, _) => { if (!syncing) SetSynth(session.Synth with { Loop = loopToggle.IsChecked == true }); };
        var homeButton = ToolUi.Button("⇤", Loc.S("Llevar el cursor de inicio al comienzo (Inicio).", "Move the play-from cursor to the beginning (Home)."), () => canvas.CursorColumn = 0);
        renderButton = ToolUi.Button(Loc.S("Renderizar WAV", "Render WAV"),
            Loc.S("Escribe el lienzo a un archivo WAV de 24 bits con Csound (una pasada, sin loop), normalizado a −1 dBFS. Después arrástralo a Cubase o SpectraLayers.",
                  "Writes the canvas to a 24-bit WAV file with Csound (one pass, no loop), normalised to −1 dBFS. Then drag it into Cubase or SpectraLayers."),
            () => _ = RenderAsync(), "IconRender");
        var codeButton = ToolUi.Button(Loc.S("Ver código Csound", "Show Csound code"),
            Loc.S("Escribe el .csd que hace este sonido (instrumento comentado + tablas en archivos al lado) en Documentos\\Csound Visual\\Paint Rooms y lo abre en una pestaña del editor: F5 lo toca, y puedes estudiarlo o cambiarlo.",
                  "Writes the .csd that makes this sound (commented instrument + tables in files next to it) to Documents\\Csound Visual\\Paint Rooms and opens it in an editor tab: F5 plays it, and you can study or change it."),
            ShowCode, "IconBook");

        modeBox.ItemsSource = new[] { Loc.S("Senos", "Sines"), Loc.S("Bandas de ruido", "Noise bands"), Loc.S("Sample en loop", "Sample loop") };
        modeBox.SelectedIndex = (int)session.Synth.Mode;
        ToolTip.SetTip(modeBox, Loc.S(
            "Cómo suena cada fila: Senos = un oscilador puro por fila (tonos claros). Bandas de ruido = una banda angosta de ruido por fila (aire, viento, mar). Sample en loop = cada fila repite un WAV transpuesto a su altura (elige el archivo en «Sonido ▾»).",
            "How each row sounds: Sines = one pure oscillator per row (clear tones). Noise bands = one narrow band of noise per row (air, wind, sea). Sample loop = each row loops a WAV transposed to its pitch (choose the file in «Sound ▾»)."));
        modeBox.SelectionChanged += (_, _) =>
        {
            if (syncing || modeBox.SelectedIndex < 0) return;
            var mode = (SynthMode)modeBox.SelectedIndex;
            if (mode == SynthMode.SampleLoop && session.Synth.SamplePath is null)
                Say(Loc.S("Elige un WAV en «Sonido ▾ › Elegir sample…» para el modo sample.", "Choose a WAV in «Sound ▾ › Choose sample…» for the sample mode."), true);
            SetSynth(session.Synth with { Mode = mode });
        };
        durationBox = ToolUi.Number(0.1, 3600, session.Image.DurationSeconds, 0.5,
            Loc.S("Duración de todo el lienzo en segundos (no cambia los píxeles: estira o comprime el tiempo).", "Duration of the whole canvas in seconds (does not change the pixels: stretches or squeezes time)."), "0.##", 92, spinner: false);
        durationBox.ValueChanged += (_, e) =>
        {
            if (syncing || e.NewValue is not { } v) return;
            // Duration only changes seconds per column (not the pixels); undoable through the
            // session, which raises DurationChanged (handled below) here and on undo/redo.
            session.SetDuration((double)v);
        };
        gainBox = ToolUi.Number(-60, 12, session.Synth.GainDb, 1,
            Loc.S("Ganancia en dB sobre el nivel automático (que deja el punto más fuerte cerca de −6 dBFS). Cambia en vivo.", "Gain in dB on top of the automatic level (which puts the loudest moment near −6 dBFS). Changes live."), "+0;-0;0", 64, spinner: false);
        gainBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) SetSynth(session.Synth with { GainDb = (double)v }); };
        attackBox = ToolUi.Number(0.5, 2000, session.Synth.AttackMs, 5,
            Loc.S("Ataque (ms): cuánto tarda una fila en subir hacia un píxel más brillante. Corto = percusivo, largo = suave.", "Attack (ms): how long a row takes to rise towards a brighter pixel. Short = percussive, long = soft."), "0.#", 76, spinner: false);
        attackBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) SetSynth(session.Synth with { AttackMs = (double)v }); };
        releaseBox = ToolUi.Number(0.5, 4000, session.Synth.ReleaseMs, 10,
            Loc.S("Caída (ms): cuánto tarda una fila en bajar hacia un píxel más oscuro. Largo = cola tipo reverb.", "Release (ms): how long a row takes to fall towards a darker pixel. Long = reverb-like tail."), "0.#", 76, spinner: false);
        releaseBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) SetSynth(session.Synth with { ReleaseMs = (double)v }); };
        glideToggle = ToolUi.Toggle(Loc.S("Suave", "Glide"),
            Loc.S("Encendido: mezcla las columnas vecinas (sonido continuo). Apagado: salta de columna en columna (rítmico, «pixelado»).", "On: blends neighbouring columns (continuous sound). Off: jumps from column to column (rhythmic, «pixelated»)."));
        glideToggle.IsChecked = session.Synth.Glide;
        glideToggle.IsCheckedChanged += (_, _) => { if (!syncing) SetSynth(session.Synth with { Glide = glideToggle.IsChecked == true }); };
        var soundMenu = MenuButton(Loc.S("Sonido ▾", "Sound ▾"), Loc.S("Ajustes menos frecuentes de la síntesis.", "Less frequent synthesis settings."), BuildSoundMenu);

        var row1 = Row();
        AddAll(row1, playButton, loopToggle, homeButton, ToolUi.Separator(), renderButton, codeButton, ToolUi.Separator(),
            ToolUi.Captioned(Loc.S("Modo", "Mode"), modeBox), ToolUi.Captioned(Loc.S("Duración s", "Length s"), durationBox),
            ToolUi.Captioned(Loc.S("Ganancia dB", "Gain dB"), gainBox), ToolUi.Captioned(Loc.S("Ataque", "Attack"), attackBox),
            ToolUi.Captioned(Loc.S("Caída", "Release"), releaseBox), glideToggle);

        // ================= pitch axis =================
        axisBox.ItemsSource = new[] { Loc.S("Lineal (Hz)", "Linear (Hz)"), Loc.S("Logarítmico (octavas)", "Logarithmic (octaves)"), Loc.S("Temperamento igual", "Equal temperament"), Loc.S("Escala Scala (.scl)", "Scala scale (.scl)") };
        ToolTip.SetTip(axisBox, Loc.S(
            "Qué altura tiene cada fila. Logarítmico: cada octava mide lo mismo (lo musical). Lineal: cada fila suma los mismos Hz (espectros). Temperamento igual: UNA FILA POR NOTA (12 = piano, 24 = cuartos de tono…). Scala: una fila por nota de cualquier afinación .scl. Al cambiarlo, lo pintado se conserva en su altura (Ctrl+Z deshace).",
            "Which pitch each row has. Logarithmic: every octave is the same height (musical). Linear: every row adds the same Hz (spectra). Equal temperament: ONE ROW PER NOTE (12 = piano, 24 = quarter tones…). Scala: one row per note of any .scl tuning. When changed, what is painted keeps its pitch (Ctrl+Z undoes)."));
        axisBox.SelectionChanged += (_, _) =>
        {
            if (syncing || axisBox.SelectedIndex < 0) return;
            var kind = (PitchAxisKind)axisBox.SelectedIndex;
            // Scala needs a file: ask for it right away (cancelling reverts the combo).
            if (kind == PitchAxisKind.Scala && (prefs.ScalaPath is null || !File.Exists(prefs.ScalaPath)))
            {
                _ = LoadScalaAsync();
                return;
            }
            prefs.AxisKind = kind;
            ApplyAxis();
        };
        lowBox = ToolUi.Number(PitchAxis.MinHz, PitchAxis.MaxHz, prefs.LowHz, 10, Loc.S("Frecuencia más grave del lienzo (Hz).", "Lowest frequency of the canvas (Hz)."), "0.#", 84, spinner: false);
        highBox = ToolUi.Number(PitchAxis.MinHz, PitchAxis.MaxHz, prefs.HighHz, 100, Loc.S("Frecuencia más aguda del lienzo (Hz).", "Highest frequency of the canvas (Hz)."), "0.#", 92, spinner: false);
        rowsBox = ToolUi.Number(16, PitchAxis.MaxRows, prefs.Rows, 32, Loc.S("Filas (resolución de altura) de los ejes lineal y logarítmico. Más filas = más detalle y más CPU.", "Rows (pitch resolution) of the linear and logarithmic axes. More rows = more detail and more CPU."), "0", 80, spinner: false);
        stepsBox = ToolUi.Number(1, 200, prefs.StepsPerOctave, 1, Loc.S("Notas por octava: 12 = semitonos, 24 = cuartos de tono, 19, 31, 53…", "Notes per octave: 12 = semitones, 24 = quarter tones, 19, 31, 53…"), "0", 70, spinner: false);
        baseBox = ToolUi.Number(1, 2000, prefs.BaseHz, 1, Loc.S("Frecuencia del grado 0 de la escala (Hz). 261,63 = Do central con La4 = 440 Hz; usa 256,87 para La = 432.", "Frequency of scale degree 0 (Hz). 261.63 = middle C with A4 = 440 Hz; use 256.87 for A = 432."), "0.###", 80, spinner: false);
        widthBox = ToolUi.Number(SpectralImage.MinWidth, SpectralImage.MaxWidth, session.Image.Width, 64, Loc.S("Columnas (resolución en el tiempo). Cambiarlo remuestrea la imagen (Ctrl+Z deshace).", "Columns (time resolution). Changing it resamples the image (Ctrl+Z undoes)."), "0", 84, spinner: false);
        lowBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) { prefs.LowHz = (double)v; ApplyAxis(); } };
        highBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) { prefs.HighHz = (double)v; ApplyAxis(); } };
        rowsBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) { prefs.Rows = (int)v; ApplyAxis(); } };
        stepsBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) { prefs.StepsPerOctave = (int)v; ApplyAxis(); } };
        baseBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) { prefs.BaseHz = (double)v; ApplyAxis(); } };
        widthBox.ValueChanged += (_, e) =>
        {
            if (syncing || e.NewValue is not { } v || (int)v == session.Image.Width) return;
            // Same axis, new width: Retune resamples the columns (undoable) and raises ImageReplaced.
            session.Retune(session.Image.Axis, (int)v);
        };
        sclButton = ToolUi.Button(Loc.S("Cargar .scl…", "Load .scl…"), Loc.S("Elige un archivo de afinación Scala (.scl). Hay miles gratis en el archivo de Scala (huygens-fokker.org).", "Choose a Scala tuning file (.scl). Thousands are free in the Scala archive (huygens-fokker.org)."), () => _ = LoadScalaAsync());
        rowsPanel = ToolUi.Captioned(Loc.S("Filas", "Rows"), rowsBox);
        stepsPanel = ToolUi.Captioned(Loc.S("Notas/oct", "Steps/oct"), stepsBox);

        var fileMenu = MenuButton(Loc.S("Archivo ▾", "File ▾"), Loc.S("Lienzo nuevo, importar una imagen, exportar PNG.", "New canvas, import a picture, export PNG."), BuildFileMenu);
        var examplesMenu = MenuButton(Loc.S("Ejemplos ▾", "Examples ▾"), Loc.S("Imágenes de partida para escuchar y modificar.", "Starter images to listen to and modify."), BuildExamplesMenu);
        var helpButton = ToolUi.Button("?", Loc.S("Ayuda rápida de Paint Synth.", "Paint Synth quick help."), ShowHelp);

        var row2 = Row();
        AddAll(row2, ToolUi.Captioned(Loc.S("Eje", "Axis"), axisBox), ToolUi.Captioned(Loc.S("Grave Hz", "Low Hz"), lowBox), ToolUi.Captioned(Loc.S("Agudo Hz", "High Hz"), highBox),
            rowsPanel, stepsPanel, sclButton, ToolUi.Captioned(Loc.S("Grado 0 Hz", "Degree 0 Hz"), baseBox), ToolUi.Separator(),
            ToolUi.Captioned(Loc.S("Columnas", "Columns"), widthBox), ToolUi.Separator(), fileMenu, examplesMenu, soundMenu, helpButton);

        // ================= tools palette =================
        var palette = BuildPalette();

        // ================= canvas + scroll bars =================
        // 2 × 2 grid: canvas top-left (inside a rounded, clipped border), vertical bar right, horizontal
        // bar below. Scroll bars and canvas share units (columns / rows), so values pass straight through;
        // «syncing» stops the bar → canvas → ViewChanged → bar feedback loop.
        var surface = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(0, 0, 10, 0) };
        surface.Children.Add(new Border { Child = canvas, CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderBrush = AppTheme.Brush("Line"), BorderThickness = new Thickness(1) });
        Grid.SetColumn(vScroll, 1);
        Grid.SetRow(hScroll, 1);
        surface.Children.Add(vScroll);
        surface.Children.Add(hScroll);
        hScroll.Scroll += (_, _) => { if (!syncing) canvas.ScrollX = hScroll.Value; };
        vScroll.Scroll += (_, _) => { if (!syncing) canvas.ScrollY = vScroll.Value; };
        canvas.ViewChanged += SyncScrollBars;
        canvas.Hover += t => hoverText.Text = t;
        ToolTip.SetTip(canvas, Loc.S(
            "Izquierdo = herramienta · derecho = borrar (o quitar la selección) · botón central = mover la vista · rueda = altura, Shift+rueda = tiempo, Ctrl+rueda = zoom en el tiempo, Alt+rueda = zoom en la altura · clic en la regla de tiempo = cursor de inicio · suelta una imagen aquí para importarla",
            "Left = tool · right = erase (or clear the selection) · middle button = pan · wheel = pitch, Shift+wheel = time, Ctrl+wheel = time zoom, Alt+wheel = pitch zoom · click the time ruler = play-from cursor · drop a picture here to import it"));
        ToolTip.SetShowDelay(canvas, 2000);
        DragDrop.SetAllowDrop(canvas, true);
        canvas.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        canvas.AddHandler(DragDrop.DropEvent, OnDrop);

        // ================= status bar =================
        var status = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(12, 6, 12, 8) };
        status.Children.Add(hoverText);
        Grid.SetColumn(messageText, 1);
        Grid.SetColumn(dspText, 2);
        status.Children.Add(messageText);
        status.Children.Add(dspText);
        ToolTip.SetTip(dspText, Loc.S("Carga de DSP de Csound (motor Windows·WASAPI de la app) mientras suena.", "Csound DSP load (the app's Windows·WASAPI engine) while playing."));

        // DockPanel order matters: docked children are laid out in insertion order and the LAST child
        // (the canvas surface) fills what remains.
        DockPanel.SetDock(row1, Dock.Top);
        DockPanel.SetDock(row2, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        DockPanel.SetDock(palette, Dock.Left);
        root.Children.Add(row1);
        root.Children.Add(row2);
        root.Children.Add(status);
        root.Children.Add(palette);
        root.Children.Add(surface);

        // ================= wiring =================
        // Every subscription made here is undone in Dispose (the session outlives the room).
        session.ImageReplaced += OnImageReplaced;
        session.PixelsChanged += OnPixelsChanged;
        // Duration changed (box, undo or redo): show it, redraw the time ruler and restart the
        // player, whose tables encode the column timing.
        session.DurationChanged += () =>
        {
            SyncFromSession();
            canvas.InvalidateVisual();
            session.Player?.Restart();
        };
        session.SynthChanged += OnSynthChanged;
        if (session.Player is { } player)
        {
            player.StateChanged += UpdatePlayButton;
            player.Message += Say;
        }
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => OnTick());
        timer.Start();
        SyncFromSession();
        UpdatePlayButton();
        Say(Loc.S("Pinta en el lienzo y presiona Tocar (Espacio). ¿Primera vez? Prueba «Ejemplos ▾».", "Paint on the canvas and press Play (Space). First time? Try «Examples ▾»."), false);
    }

    // ------------------------------------------------------------------------------------------
    //  IPaintRoom
    // ------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public string Title => Loc.S("Paint Synth", "Paint Synth");

    /// <inheritdoc/>
    /// <remarks>The shared session: Paint Synth edits the shared canvas.</remarks>
    public PaintSession EditSession => session;

    /// <inheritdoc/>
    public string Tip => Loc.S("Pinta sonido: el tiempo va a la derecha, la altura hacia arriba, el brillo es volumen y rojo/verde es izquierda/derecha.",
                               "Paint sound: time runs right, pitch goes up, brightness is loudness and red/green is left/right.");

    /// <inheritdoc/>
    public Control Content => root;

    /// <summary>The canvas (tests and other rooms may drive it).</summary>
    public PaintCanvas Canvas => canvas;

    /// <inheritdoc/>
    /// <remarks>Re-syncs the controls (another room may have changed the shared session) and focuses the canvas so the shortcuts work at once.</remarks>
    public void OnActivated()
    {
        SyncFromSession();
        canvas.Focus();
    }

    /// <inheritdoc/>
    public void OnDeactivated() { }

    /// <inheritdoc/>
    /// <remarks>
    /// Room shortcuts (see the quick help): Space play/stop, Ctrl+Z / Ctrl+Y undo / redo, clipboard,
    /// selection, Home, [ ] brush size, Ctrl+0 / Ctrl± time zoom, then single letters for the tools.
    /// Nothing is handled while a text box has the focus, so typing numbers is never hijacked.
    /// Ctrl+V pastes at the play-from cursor column, on the row of the current selection (or row 0).
    /// </remarks>
    public bool HandleKey(KeyEventArgs e, bool focusIsTextInput)
    {
        if (focusIsTextInput) return false;
        // A spline selection being placed takes Enter / Esc / Backspace first.
        if (canvas.HandleSelectionKey(e.Key)) return true;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); return true;
            case Key.Z when ctrl && shift: session.Redo(); return true;   // Ctrl+Shift+Z = redo, like Ctrl+Y
            case Key.Z when ctrl: session.Undo(); return true;
            case Key.Y when ctrl: session.Redo(); return true;
            case Key.C when ctrl: session.Copy(); Say(Loc.S("Copiado.", "Copied."), false); return true;
            case Key.X when ctrl: session.Copy(); session.DeleteSelection(); return true;
            case Key.V when ctrl:
                session.Paste((int)Math.Round(canvas.CursorColumn), session.Selection?.Bounds.Y0 ?? 0);
                return true;
            case Key.A when ctrl: session.ChangeSelection(CellRect.All(session.Image)); return true;
            case Key.Delete or Key.Back: session.DeleteSelection(); return true;
            case Key.Escape: session.ChangeSelection(null); return true;
            case Key.Home: canvas.CursorColumn = 0; return true;
            case Key.OemOpenBrackets: sizeSlider.Value = Math.Max(sizeSlider.Minimum, sizeSlider.Value / 1.25); return true;
            case Key.OemCloseBrackets: sizeSlider.Value = Math.Min(sizeSlider.Maximum, sizeSlider.Value * 1.25); return true;
            case Key.D0 when ctrl: canvas.ZoomToFit(); return true;
            case Key.OemPlus or Key.Add when ctrl: canvas.ZoomTime(1.25); return true;
            case Key.OemMinus or Key.Subtract when ctrl: canvas.ZoomTime(0.8); return true;
        }
        // Tool letters only without Ctrl/Alt (Ctrl+A, Ctrl+S… belong to the commands above or to the window).
        if (ctrl || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return false;
        PaintTool? tool = e.Key switch
        {
            Key.P => PaintTool.Pencil, Key.A => PaintTool.Airbrush, Key.S => PaintTool.Spray, Key.H => PaintTool.Harmonics,
            Key.L => PaintTool.Line, Key.R => PaintTool.Rectangle, Key.O => PaintTool.Ellipse, Key.G => PaintTool.Gradient,
            Key.E => PaintTool.Eraser, Key.B => PaintTool.Blur, Key.U => PaintTool.Smudge,
            // M = rectangle, Shift+M = ellipse, F = lasso, N = spline selection.
            _ => PaintSelectionUi.ToolForKey(e.Key, shift),
        };
        if (tool is { } t) { SelectTool(t); return true; }
        return false;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Stops the timer, cancels a running render, stops playback and removes every subscription to
    /// the shared session and player; finally detaches the canvas from the session.
    /// </remarks>
    public void Dispose()
    {
        timer.Stop();
        renderCancel?.Cancel();
        session.Player?.Stop();
        session.ImageReplaced -= OnImageReplaced;
        session.PixelsChanged -= OnPixelsChanged;
        session.SynthChanged -= OnSynthChanged;
        if (session.Player is { } player)
        {
            player.StateChanged -= UpdatePlayButton;
            player.Message -= Say;
        }
        canvas.Session = null;
    }

    // ------------------------------------------------------------------------------------------
    //  Tools palette
    // ------------------------------------------------------------------------------------------

    /// <summary>Builds the left column: the 15 tool buttons (3 × 5 grid, the last row = the shaped selection tools), the brush sliders, pan buttons, the harmonics options and the selection options (feather).</summary>
    /// <returns>A vertical scroll viewer (the column scrolls when the window is short).</returns>
    /// <remarks>
    /// Creates <see cref="harmonicsBox"/> and <see cref="rolloffBox"/> (hence their <c>null!</c>
    /// initialisers), restores every brush control from the preferences and selects the remembered tool.
    /// </remarks>
    Control BuildPalette()
    {
        // (tool, button glyph, Spanish tip, English tip, shortcut letter) — the menu order is the grid order.
        (PaintTool Tool, string Glyph, string Es, string En, string Key)[] tools =
        [
            (PaintTool.Pencil, "✎", "Lápiz: pincel de borde duro. Un trazo horizontal = una nota sostenida.", "Pencil: hard-edged brush. A horizontal stroke = a held note.", "P"),
            (PaintTool.Airbrush, "◍", "Aerógrafo: pincel suave (gaussiano). Ideal para fundidos y nubes de sonido.", "Airbrush: soft (Gaussian) brush. Great for fades and sound clouds.", "A"),
            (PaintTool.Spray, "⁘", "Spray: puntos al azar. Granos, crepitar, lluvia.", "Spray: random dots. Grains, crackle, rain.", "S"),
            (PaintTool.Harmonics, "≣", "Pincel armónico: pinta la fundamental y sus armónicos (2f, 3f…) cada vez más suaves. Un trazo = una nota con timbre.", "Harmonics brush: paints the fundamental and its harmonics (2f, 3f…), each softer. One stroke = a note with timbre.", "H"),
            (PaintTool.Line, "╱", "Línea: arrastra de un punto a otro. En diagonal = glissando.", "Line: drag from one point to another. Diagonal = glissando.", "L"),
            (PaintTool.Rectangle, "▭", "Rectángulo relleno: un bloque de muchas alturas a la vez (cluster, ruido).", "Filled rectangle: a block of many pitches at once (cluster, noise).", "R"),
            (PaintTool.Ellipse, "◯", "Elipse rellena: un bloque redondeado (entra y sale gradualmente).", "Filled ellipse: a rounded block (fades in and out).", "O"),
            (PaintTool.Gradient, "◩", "Degradado: arrastra; pinta del brillo elegido al inicio hasta nada al final (en la selección o todo el lienzo).", "Gradient: drag; paints from the chosen brightness at the start to nothing at the end (in the selection or the whole canvas).", "G"),
            (PaintTool.Eraser, "⌫", "Goma: apaga lo que toca (también: botón derecho con cualquier pincel).", "Eraser: silences what it touches (also: right button with any brush).", "E"),
            (PaintTool.Blur, "≈", "Desenfocar: suaviza bajo el pincel (tonos → bandas, ataques más suaves).", "Blur: softens under the brush (tones → bands, softer attacks).", "B"),
            (PaintTool.Smudge, "☁", "Difuminar: arrastra la imagen con el pincel (doblar notas, estirar ataques).", "Smudge: drags the picture along with the brush (bend notes, stretch attacks).", "U"),
            (PaintTool.Select, "⬚", "Selección rectangular: arrastra un rectángulo; arrastrar dentro lo mueve; Ctrl+C/X/V, Supr. Las operaciones de «Selección ▾» (bajo las herramientas) se aplican a la selección." + PaintSelectionUi.ModifierHint, "Rectangle selection: drag a rectangle; drag inside moves it; Ctrl+C/X/V, Del. «Selection ▾» operations (under the tools) apply to the selection." + PaintSelectionUi.ModifierHint, "M"),
            .. PaintSelectionUi.ShapedTools(),
        ];
        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var t in tools)
        {
            var b = new ToggleButton
            {
                // A text glyph, or a drawn icon for the shaped selection tools (PaintToolIcons).
                Content = PaintToolIcons.Content(t.Glyph),
                Width = 46, Height = 38, Margin = new Thickness(2), Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(7),
            };
            ToolTip.SetTip(b, Loc.S(t.Es, t.En) + "  (" + t.Key + ")");
            // Automation name = the enum name: screen readers and UI tests find the buttons by it.
            Avalonia.Automation.AutomationProperties.SetName(b, t.Tool.ToString());
            var tool = t.Tool; // per-iteration copy captured by the click handler
            b.Click += (_, _) => SelectTool(tool);
            toolButtons[tool] = b;
            grid.Children.Add(b);
        }

        // Restore the sliders BEFORE subscribing to ValueChanged, so restoring does not fire OnBrushChanged
        // while harmonicsBox / rolloffBox do not exist yet.
        sizeSlider.Value = prefs.BrushSize;
        opacitySlider.Value = prefs.Opacity;
        hardSlider.Value = prefs.Hardness;
        panSlider.Value = prefs.Pan;
        noiseSlider.Value = prefs.BrushNoise;
        noiseOnlyBox.IsChecked = prefs.NoiseOnly;
        showNoiseBox.IsChecked = prefs.ShowNoise;
        canvas.ShowNoise = prefs.ShowNoise;
        ToolTip.SetTip(noiseSlider, Loc.S(
            "Ruido de la pintura: 0 = tono puro (un seno por fila), 1 = banda de ruido (aire, soplo, platillos), entre medio = mezcla. No cambia el volumen. Se guarda en la capa de ruido de cada celda (en el lienzo se ve granulado). Suena en el modo Senos.",
            "Noise of the paint: 0 = pure tone (one sine per row), 1 = noise band (air, breath, cymbals), in between = a mix. It does not change the loudness. It is stored in each cell's noise layer (grainy on the canvas). Heard in the Sines mode."));
        noiseOnlyBox.Content = Loc.S("Solo ruido", "Noise only");
        ToolTip.SetTip(noiseOnlyBox, Loc.S("El pincel cambia SOLO la cantidad de ruido de lo que ya está pintado (no el volumen): vuelve ruidosas o tonales notas existentes.",
                                           "The brush changes ONLY the noise amount of what is already painted (not the loudness): makes existing notes noisy or tonal."));
        showNoiseBox.Content = Loc.S("Ver capa de ruido", "Show noise layer");
        ToolTip.SetTip(showNoiseBox, Loc.S("Dibuja las celdas ruidosas con textura granulada y menos color.", "Draws noisy cells with a grainy, less colourful texture."));
        noiseOnlyBox.IsCheckedChanged += (_, _) => OnBrushChanged();
        showNoiseBox.IsCheckedChanged += (_, _) => { prefs.ShowNoise = showNoiseBox.IsChecked == true; canvas.ShowNoise = prefs.ShowNoise; };
        ToolTip.SetTip(sizeSlider, Loc.S("Tamaño del pincel en píxeles de pantalla (también [ y ]).", "Brush size in screen pixels (also [ and ])."));
        ToolTip.SetTip(opacitySlider, Loc.S("Brillo de la pintura = volumen de lo que pintas (1 = máximo).", "Paint brightness = loudness of what you paint (1 = maximum)."));
        ToolTip.SetTip(hardSlider, Loc.S("Dureza del borde: 1 = borde nítido, 0 = muy suave.", "Edge hardness: 1 = crisp edge, 0 = very soft."));
        ToolTip.SetTip(panSlider, Loc.S("Panorama de la pintura: −1 = solo izquierda (rojo), 0 = ambos lados, +1 = solo derecha (verde).", "Pan of the paint: −1 = left only (red), 0 = both sides, +1 = right only (green)."));
        // Every brush slider also shows the brush ON the canvas for a moment (real size, hardness,
        // level, pan colour, noise grain): the pointer is on the slider, so the usual circle under
        // the mouse would not be visible.
        foreach (var s in new[] { sizeSlider, opacitySlider, hardSlider, panSlider, noiseSlider })
            s.ValueChanged += (_, _) => { OnBrushChanged(); canvas.ShowParameterPreview(PaintCanvas.ParameterPreview.Brush); };
        var panButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (label, v, es, en) in new[] { ("L", -1.0, "Solo izquierda", "Left only"), ("C", 0.0, "Centro (ambos)", "Centre (both)"), ("R", 1.0, "Solo derecha", "Right only") })
        {
            var b = new Button { Content = label, Width = 44, Padding = new Thickness(0, 3), HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(b, Loc.S(es, en));
            b.Click += (_, _) => panSlider.Value = v;
            panButtons.Children.Add(b);
        }

        harmonicsBox = ToolUi.Number(1, 64, prefs.Harmonics, 1, Loc.S("Parciales incluida la fundamental.", "Partials including the fundamental."), "0", 70, spinner: false);
        rolloffBox = ToolUi.Number(0, 4, prefs.Rolloff, 0.25, Loc.S("Caída de los armónicos: el parcial k suena con 1/k^caída (0 = todos iguales, 1 = diente de sierra, 2 = suave).", "Harmonic roll-off: partial k sounds at 1/k^rolloff (0 = all equal, 1 = sawtooth-like, 2 = mellow)."), "0.##", 70, spinner: false);
        oddBox.Content = Loc.S("Solo impares", "Odd only");
        ToolTip.SetTip(oddBox, Loc.S("Solo los armónicos impares (1, 3, 5…): sonido hueco, como un clarinete o una onda cuadrada.", "Only odd harmonics (1, 3, 5…): hollow sound, like a clarinet or a square wave."));
        oddBox.IsChecked = prefs.OddOnly;
        harmonicsBox.ValueChanged += (_, _) => OnBrushChanged();
        rolloffBox.ValueChanged += (_, _) => OnBrushChanged();
        oddBox.IsCheckedChanged += (_, _) => OnBrushChanged();
        harmonicsPanel.Children.Add(Caption(Loc.S("ARMÓNICOS", "HARMONICS")));
        harmonicsPanel.Children.Add(ToolUi.Captioned(Loc.S("Cantidad", "Count"), harmonicsBox));
        harmonicsPanel.Children.Add(ToolUi.Captioned(Loc.S("Caída", "Roll-off"), rolloffBox));
        harmonicsPanel.Children.Add(oddBox);

        var stack = new StackPanel { Spacing = 3, Margin = new Thickness(10, 4, 10, 0), Width = 160 };
        stack.Children.Add(Caption(Loc.S("HERRAMIENTAS", "TOOLS")));
        stack.Children.Add(grid);
        // Selection commands + image operations, right under the tools they belong to.
        stack.Children.Add(PaintSelectionUi.SelectionMenuButton(session, BuildImageMenu));
        stack.Children.Add(Caption(Loc.S("PINCEL", "BRUSH")));
        stack.Children.Add(Labeled(Loc.S("Tamaño", "Size"), sizeText, sizeSlider));
        stack.Children.Add(Labeled(Loc.S("Brillo", "Level"), opacityText, opacitySlider));
        stack.Children.Add(Labeled(Loc.S("Dureza", "Hardness"), hardText, hardSlider));
        stack.Children.Add(Labeled(Loc.S("Panorama", "Pan"), panText, panSlider));
        stack.Children.Add(panButtons);
        stack.Children.Add(new Border { Height = 6 });
        stack.Children.Add(Labeled(Loc.S("Ruido", "Noise"), noiseText, noiseSlider));
        stack.Children.Add(noiseOnlyBox);
        stack.Children.Add(showNoiseBox);
        stack.Children.Add(new Border { Height = 8 });
        stack.Children.Add(harmonicsPanel);
        selectionPanel = PaintSelectionUi.OptionsPanel(canvas, prefs);
        stack.Children.Add(selectionPanel);
        OnBrushChanged();
        SelectTool(prefs.Tool);
        return new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    /// <summary>Makes <paramref name="tool"/> current (canvas, toggle buttons, harmonics panel visibility, preferences).</summary>
    /// <param name="tool">The tool to select.</param>
    /// <remarks>Also re-checks the button that was clicked (a ToggleButton un-checks itself when clicked while checked).</remarks>
    void SelectTool(PaintTool tool)
    {
        prefs.Tool = tool;
        canvas.Tool = tool;
        foreach (var (t, b) in toolButtons) b.IsChecked = t == tool;
        harmonicsPanel.IsVisible = tool == PaintTool.Harmonics;
        if (selectionPanel is not null) selectionPanel.IsVisible = PaintCanvas.IsSelectionTool(tool);
        canvas.InvalidateVisual();
    }

    /// <summary>Copies the brush controls into the preferences and the canvas, and refreshes the value captions.</summary>
    /// <remarks>
    /// The canvas gets a NEW immutable <see cref="BrushSettings"/> each time, so a stroke in progress
    /// keeps the settings it started with. The <c>?.</c> on the harmonics boxes covers the first call
    /// from <see cref="BuildPalette"/>, made before they are created.
    /// </remarks>
    void OnBrushChanged()
    {
        prefs.BrushSize = sizeSlider.Value;
        prefs.Opacity = opacitySlider.Value;
        prefs.Hardness = hardSlider.Value;
        prefs.Pan = panSlider.Value;
        prefs.Harmonics = (int)(harmonicsBox?.Value ?? prefs.Harmonics);
        prefs.Rolloff = (double)(rolloffBox?.Value ?? (decimal)prefs.Rolloff);
        prefs.OddOnly = oddBox.IsChecked == true;
        prefs.BrushNoise = noiseSlider.Value;
        prefs.NoiseOnly = noiseOnlyBox.IsChecked == true;
        canvas.Brush = new BrushSettings
        {
            Size = prefs.BrushSize, Opacity = prefs.Opacity, Hardness = prefs.Hardness, Pan = prefs.Pan,
            Harmonics = prefs.Harmonics, Rolloff = prefs.Rolloff, OddOnly = prefs.OddOnly,
            Noise = prefs.BrushNoise, NoiseOnly = prefs.NoiseOnly,
        };
        noiseText.Text = prefs.BrushNoise < 0.005 ? Loc.S("tono", "tone") : prefs.BrushNoise > 0.995 ? Loc.S("ruido", "noise") : $"{prefs.BrushNoise * 100:0} %";
        sizeText.Text = $"{prefs.BrushSize:0} px";
        opacityText.Text = $"{prefs.Opacity * 100:0} %";
        hardText.Text = $"{prefs.Hardness * 100:0} %";
        panText.Text = prefs.Pan < -0.02 ? Loc.S($"{-prefs.Pan * 100:0} % izq.", $"{-prefs.Pan * 100:0} % L") : prefs.Pan > 0.02 ? Loc.S($"{prefs.Pan * 100:0} % der.", $"{prefs.Pan * 100:0} % R") : Loc.S("centro", "centre");
        canvas.InvalidateVisual();
    }

    // ------------------------------------------------------------------------------------------
    //  Session ↔ controls
    // ------------------------------------------------------------------------------------------

    /// <summary>Shows the session's image and synthesis settings in the controls (without triggering their change handlers).</summary>
    /// <remarks>
    /// Called after every session change and on activation. Shows / hides the axis-specific controls
    /// (rows for continuous axes, steps for equal temperament, .scl button for Scala, degree-0 Hz for
    /// discrete axes) and copies the visible axis back into the preferences.
    /// </remarks>
    void SyncFromSession()
    {
        syncing = true;
        var img = session.Image;
        var axis = img.Axis;
        axisBox.SelectedIndex = (int)axis.Kind;
        lowBox.Value = (decimal)Math.Round(axis.LowHz, 2);
        highBox.Value = (decimal)Math.Round(axis.HighHz, 2);
        if (!axis.IsDiscrete) rowsBox.Value = axis.RowCount;
        if (axis.Kind == PitchAxisKind.EqualTemperament) stepsBox.Value = axis.StepsPerOctave;
        baseBox.Value = (decimal)Math.Round(axis.BaseHz, 3);
        rowsPanel.IsVisible = !axis.IsDiscrete;
        stepsPanel.IsVisible = axis.Kind == PitchAxisKind.EqualTemperament;
        sclButton.IsVisible = axis.Kind == PitchAxisKind.Scala;
        baseBox.IsEnabled = axis.IsDiscrete;
        // The preferences follow what is shown (also after undo/redo, presets and imports), so
        // the next session starts with the axis the user last saw.
        prefs.AxisKind = axis.Kind;
        prefs.LowHz = axis.LowHz;
        prefs.HighHz = axis.HighHz;
        if (!axis.IsDiscrete) prefs.Rows = axis.RowCount;
        if (axis.Kind == PitchAxisKind.EqualTemperament) prefs.StepsPerOctave = axis.StepsPerOctave;
        if (axis.IsDiscrete) prefs.BaseHz = axis.BaseHz;
        widthBox.Value = img.Width;
        durationBox.Value = (decimal)img.DurationSeconds;
        var s = session.Synth;
        modeBox.SelectedIndex = (int)s.Mode;
        gainBox.Value = (decimal)s.GainDb;
        attackBox.Value = (decimal)s.AttackMs;
        releaseBox.Value = (decimal)s.ReleaseMs;
        glideToggle.IsChecked = s.Glide;
        loopToggle.IsChecked = s.Loop;
        syncing = false;
    }

    /// <summary>Stores new synthesis settings in the session (the live player and the controls follow through SynthChanged).</summary>
    /// <param name="s">The new settings (usually <c>session.Synth with { … }</c>).</param>
    void SetSynth(PaintSynthSettings s) => session.Synth = s;

    /// <summary>Session settings changed: the live player follows (live or restart) and the controls are refreshed.</summary>
    void OnSynthChanged()
    {
        session.Player?.UpdateSettings(session.Synth);
        SyncFromSession();
    }

    /// <summary>A new image object (preset, import, retune, undo of those): refresh the controls and keep playing it from the same place.</summary>
    void OnImageReplaced()
    {
        SyncFromSession();
        session.Player?.Restart(session.Image);
    }

    /// <summary>Pixels changed: the live player re-uploads those columns at its next tick.</summary>
    /// <param name="x0">First changed column (inclusive).</param>
    /// <param name="x1">End of the changed columns (exclusive).</param>
    void OnPixelsChanged(int x0, int x1) =>session.Player?.OnImageEdited(session.Image, x0, x1);

    /// <summary>Rebuilds the pitch axis from the controls and retunes the image (undoable).</summary>
    /// <remarks>
    /// Reads the preferences (which the axis controls have just written), not the controls. On a
    /// missing .scl or an invalid range (e.g. too many notes) it reports the problem and re-syncs, so
    /// the controls show the axis that is really in use. An unchanged axis is not retuned (no empty
    /// undo step). Retune keeps every painted cell at its pitch on the new axis.
    /// </remarks>
    void ApplyAxis()
    {
        if (prefs.AxisKind == PitchAxisKind.Scala && (prefs.ScalaPath is null || !File.Exists(prefs.ScalaPath)))
        {
            SyncFromSession();
            sclButton.IsVisible = true;
            Say(Loc.S("Elige un archivo .scl con «Cargar .scl…».", "Choose a .scl file with «Load .scl…»."), true);
            return;
        }
        var axis = prefs.BuildAxis(out string? error);
        if (error is not null)
        {
            Say(Loc.S("Eje no válido: ", "Invalid axis: ") + error, true);
            SyncFromSession();
            return;
        }
        if (axis.Equals(session.Image.Axis)) { SyncFromSession(); return; }
        session.Retune(axis);
        Say(Loc.S("Eje: ", "Axis: ") + axis.Describe(), false);
    }

    /// <summary>Matches the scroll bars to the canvas zoom and scroll position.</summary>
    void SyncScrollBars()
    {
        syncing = true;
        hScroll.Maximum = canvas.MaxScrollX;
        hScroll.ViewportSize = canvas.VisibleColumns;
        hScroll.Value = canvas.ScrollX;
        vScroll.Maximum = canvas.MaxScrollY;
        vScroll.ViewportSize = canvas.VisibleRows;
        vScroll.Value = canvas.ScrollY;
        syncing = false;
    }

    // ------------------------------------------------------------------------------------------
    //  Transport
    // ------------------------------------------------------------------------------------------

    /// <summary>Play/stop (button and Space): plays from the yellow cursor, or from the start when the cursor is at the end.</summary>
    /// <remarks>
    /// Any non-idle state (starting or playing) counts as «playing», so a second press during the
    /// start-up also stops. The «at the end» test uses the last column, so a cursor left at the end
    /// by a finished play does not start an empty pass.
    /// </remarks>
    void TogglePlay()
    {
        if (session.Player is not { } player)
        {
            Say(Loc.S("Csound no está disponible.", "Csound is not available."), true);
            return;
        }
        if (player.State != PaintPlayState.Idle)
        {
            player.Stop();
            Say(Loc.S("■ Detenido.", "■ Stopped."), false);
        }
        else
            player.Play(session.Image, session.Synth, canvas.CursorColumn >= session.Image.Width - 1 ? 0 : canvas.CursorColumn);
    }

    /// <summary>Shows Play or Stop on the transport button according to the player state.</summary>
    /// <remarks>Handler of the player's StateChanged; also hides the play line once playback is over.</remarks>
    void UpdatePlayButton()
    {
        bool on = session.Player?.State is PaintPlayState.Playing or PaintPlayState.Starting;
        playButton.Content = ToolUi.Content(on ? Loc.S("Detener", "Stop") : Loc.S("Tocar", "Play"), on ? "IconStop" : "IconPlay");
        if (!on) canvas.PlayColumn = null;
    }

    /// <summary>~30 Hz: drives the live player, moves the play line and shows the DSP load.</summary>
    /// <remarks>
    /// <c>Player.Tick</c> uploads pending edited columns and follows the performance. DspLoad &lt; 0
    /// means «not measured yet». The dropout hint needs more than 2 NEW underruns since the last check
    /// (a single glitch at start-up is normal), and is shown at most once per play; the counters are
    /// re-based whenever nothing is playing.
    /// </remarks>
    void OnTick()
    {
        var player = session.Player;
        player?.Tick();
        canvas.PlayColumn = player?.CurrentColumn;
        var engine = ctx.App.Engine;
        dspText.Text = player?.State == PaintPlayState.Playing && engine.DspLoad >= 0
            ? $"DSP {engine.DspLoad * 100:0} %" + (engine.Underruns > 0 ? Loc.S($" · {engine.Underruns} cortes", $" · {engine.Underruns} dropouts") : "")
            : "";
        // Dropouts while playing: suggest the cheaper settings once per performance.
        if (player?.State == PaintPlayState.Playing && engine.Underruns > dropoutsSeen + 2)
        {
            dropoutsSeen = engine.Underruns;
            if (!dropoutHintShown)
            {
                dropoutHintShown = true;
                Say(session.Synth.Mode == SynthMode.NoiseBands && session.Synth.SmoothNoise
                    ? Loc.S("⚠ Hay cortes: prueba «Sonido ▾ › Ruido suave» apagado (mitad de CPU), menos filas, o una latencia mayor en Configuración › Audio.",
                            "⚠ Dropouts: try «Sound ▾ › Smooth noise» off (half the CPU), fewer rows, or a higher latency in Settings › Audio.")
                    : Loc.S("⚠ Hay cortes: prueba menos filas o una latencia mayor en Configuración › Audio.",
                            "⚠ Dropouts: try fewer rows or a higher latency in Settings › Audio."), true);
            }
        }
        else if (player?.State != PaintPlayState.Playing)
        {
            dropoutsSeen = engine.Underruns;
            dropoutHintShown = false;
        }
    }

    /// <summary>Render WAV: asks for the file (app's save picker), renders through Csound with progress, reports the result (a second click cancels).</summary>
    /// <remarks>
    /// The render runs on a PRIVATE Csound instance (see <see cref="PaintRenderer"/>), so live playback
    /// and editing may continue; the image is snapshotted when the render starts. Progress reports
    /// arrive on the UI thread (Progress&lt;T&gt; captures the synchronisation context). The
    /// default file name is the image name with invalid file-name characters replaced by «_».
    /// </remarks>
    async Task RenderAsync()
    {
        if (rendering)
        {
            renderCancel?.Cancel();
            return;
        }
        if (ctx.App.PickSaveFile is not { } pick) return;
        if (session.Synth.Mode == SynthMode.SampleLoop && session.Synth.SamplePath is null)
        {
            Say(Loc.S("Elige primero un sample en «Sonido ▾».", "Choose a sample in «Sound ▾» first."), true);
            return;
        }
        string name = string.Concat(session.ImageName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (await pick(name + ".wav", "wav") is not { } path) return;
        rendering = true;
        renderCancel = new CancellationTokenSource();
        renderButton.Content = ToolUi.Content(Loc.S("Cancelar render", "Cancel render"), "IconStop");
        var progress = new Progress<double>(p => Say(Loc.S($"Renderizando con Csound… {p * 100:0} %", $"Rendering with Csound… {p * 100:0} %"), false));
        try
        {
            var r = await PaintRenderer.RenderAsync(session.Image, session.Synth, path, progress, renderCancel.Token);
            if (r.Success)
            {
                string msg = Loc.S($"✓ WAV guardado: {path} ({r.Seconds:0.0} s, Csound tardó {r.RenderSeconds:0.0} s, normalizado {r.AppliedGainDb:+0.0;-0.0} dB)",
                                   $"✓ WAV saved: {path} ({r.Seconds:0.0} s, Csound took {r.RenderSeconds:0.0} s, normalised {r.AppliedGainDb:+0.0;-0.0} dB)");
                Say(msg, false);
                ctx.App.PaintInfo("Paint Rooms: " + msg);
            }
            else
                Say(Loc.S("✗ El render falló: ", "✗ The render failed: ") + (r.Errors.FirstOrDefault() ?? "?"), true);
        }
        catch (OperationCanceledException)
        {
            Say(Loc.S("Render cancelado.", "Render cancelled."), true);
        }
        catch (Exception ex)
        {
            Say(Loc.S("✗ El render falló: ", "✗ The render failed: ") + ex.Message, true);
        }
        finally
        {
            rendering = false;
            renderButton.Content = ToolUi.Content(Loc.S("Renderizar WAV", "Render WAV"), "IconRender");
        }
    }

    /// <summary>Show Csound code: writes the standalone .csd of the current image and opens it in an editor tab.</summary>
    /// <remarks>The .csd goes to a new timestamped folder under Documents\Csound Visual\Paint Rooms (<see cref="PaintRenderer.ExportCode"/>).</remarks>
    void ShowCode()
    {
        try
        {
            string csd = PaintRenderer.ExportCode(session.Image, session.Synth, session.ImageName);
            ctx.App.OpenPaintCode(csd);
            Say(Loc.S($"Código abierto en el editor: {csd}", $"Code opened in the editor: {csd}"), false);
        }
        catch (Exception ex)
        {
            Say(ex.Message, true);
        }
    }

    // ------------------------------------------------------------------------------------------
    //  Menus
    // ------------------------------------------------------------------------------------------

    /// <summary>File menu: new canvas, import, export, Scala, and the colour mapping used when importing.</summary>
    /// <param name="menu">The fresh flyout to fill (see <see cref="MenuButton"/>).</param>
    /// <remarks>«New canvas» keeps the width and duration and uses the axis from the preferences; it is undoable.</remarks>
    void BuildFileMenu(MenuFlyout menu)
    {
        menu.Items.Add(Item(Loc.S("Lienzo nuevo (vacío)", "New canvas (empty)"), () =>
        {
            var axis = prefs.BuildAxis(out _);
            session.ReplaceImage(new SpectralImage(session.Image.Width, axis, session.Image.DurationSeconds), "new");
            session.ImageName = "untitled";
            session.MarkPictureSaved();   // an empty canvas has nothing to lose
            canvas.CursorColumn = 0;
        }));
        menu.Items.Add(Item(Loc.S("Importar imagen…", "Import picture…"), () => _ = ImportPictureAsync()));
        // Export: 16-bit (default: lossless round trips of levels and noise) or 8-bit (smaller, universal).
        var export = new MenuItem { Header = Loc.S("Exportar PNG", "Export PNG") };
        export.Items.Add(Item((prefs.Export16Bit ? "● " : "○ ") + Loc.S("16 bits por canal (sin pérdida)…", "16-bit per channel (lossless)…"), () => { prefs.Export16Bit = true; _ = ExportPngAsync(); }));
        export.Items.Add(Item((prefs.Export16Bit ? "○ " : "● ") + Loc.S("8 bits por canal…", "8-bit per channel…"), () => { prefs.Export16Bit = false; _ = ExportPngAsync(); }));
        menu.Items.Add(export);
        menu.Items.Add(Item(Loc.S("Cargar escala Scala (.scl)…", "Load Scala scale (.scl)…"), () => _ = LoadScalaAsync()));
        menu.Items.Add(new Separator());
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
            var item = Item((prefs.ImportMapping == mm ? "● " : "○ ") + Loc.S(m.Es, m.En), () => prefs.ImportMapping = mm);
            mapping.Items.Add(item);
        }
        menu.Items.Add(mapping);
    }

    /// <summary>Operations part of the «Selection ▾» menu (under the tools), generated from <see cref="ImageOps.All"/> (scope line first: selection or whole canvas).</summary>
    /// <param name="menu">The fresh flyout to fill.</param>
    /// <remarks>A separator is inserted whenever the operation group changes; operations with a parameter get «…» and ask for it.</remarks>
    void BuildImageMenu(MenuFlyout menu)
    {
        string scope = session.Selection is null ? Loc.S("(todo el lienzo)", "(whole canvas)") : Loc.S("(selección)", "(selection)");
        menu.Items.Add(new MenuItem { Header = scope, IsEnabled = false });
        int group = 0;
        foreach (var op in ImageOps.All)
        {
            if (op.Group != group) { menu.Items.Add(new Separator()); group = op.Group; }
            var o = op;
            var item = Item(Loc.S(op.NameEs, op.NameEn) + (op.Parameter is null ? "" : "…"), () => _ = RunOperationAsync(o));
            ToolTip.SetTip(item, Loc.S(op.TipEs, op.TipEn));
            menu.Items.Add(item);
        }
    }

    /// <summary>Examples menu, generated from <see cref="PaintPresets.All"/>.</summary>
    /// <param name="menu">The fresh flyout to fill.</param>
    void BuildExamplesMenu(MenuFlyout menu)
    {
        foreach (var p in PaintPresets.All)
        {
            var preset = p;
            var item = Item(Loc.S(p.NameEs, p.NameEn), () => LoadPreset(preset));
            ToolTip.SetTip(item, Loc.S(p.TipEs, p.TipEn));
            menu.Items.Add(item);
        }
    }

    /// <summary>Sound menu: the less frequent synthesis settings, with their current values in the item texts.</summary>
    /// <param name="menu">The fresh flyout to fill.</param>
    /// <remarks>
    /// Check marks (☑ / ☐, ● / ○) are part of the item text because the menu is rebuilt on every
    /// open. Toggles read <c>session.Synth</c> at CLICK time (not the snapshot <c>s</c>), so they
    /// always flip the current value; the numeric items open <see cref="AskSynthAsync"/>.
    /// </remarks>
    void BuildSoundMenu(MenuFlyout menu)
    {
        var s = session.Synth;
        menu.Items.Add(Item((s.AutoLevel ? "☑ " : "☐ ") + Loc.S("Nivel automático", "Automatic level"), () => SetSynth(session.Synth with { AutoLevel = !session.Synth.AutoLevel })));
        menu.Items.Add(Item(Loc.S($"Curva brillo → volumen: {s.Curve:0.##}…", $"Brightness → loudness curve: {s.Curve:0.##}…"), () => _ = AskSynthAsync(
            Loc.S("Curva (exponente)", "Curve (exponent)"), 0.5, 4, s.Curve, 0.25, v => session.Synth with { Curve = v })));
        menu.Items.Add(Item(Loc.S($"Umbral de silencio: {s.Floor:0.###}…", $"Silence floor: {s.Floor:0.###}…"), () => _ = AskSynthAsync(
            Loc.S("Umbral (0…0,5)", "Floor (0…0.5)"), 0, 0.5, s.Floor, 0.01, v => session.Synth with { Floor = v })));
        menu.Items.Add(Item(Loc.S($"Ancho de las bandas de ruido: {s.NoiseWidth:0.##}×…", $"Noise band width: {s.NoiseWidth:0.##}×…"), () => _ = AskSynthAsync(
            Loc.S("Ancho (× separación de filas)", "Width (× row spacing)"), 0.1, 4, s.NoiseWidth, 0.1, v => session.Synth with { NoiseWidth = v })));
        var smooth = Item((s.SmoothNoise ? "☑ " : "☐ ") + Loc.S("Ruido suave (2 osciladores por fila)", "Smooth noise (2 oscillators per row)"),
            () => SetSynth(session.Synth with { SmoothNoise = !session.Synth.SmoothNoise }));
        ToolTip.SetTip(smooth, Loc.S("Apagado = ruido «liviano»: la mitad de CPU; las bandas angostas suenan un poco más a trémolo. Úsalo si una imagen grande en estéreo con bandas de ruido entrecorta.",
                                     "Off = «light» noise: half the CPU; narrow bands sound slightly more like tremolo. Use it if a big stereo image with noise bands stutters."));
        menu.Items.Add(smooth);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Loc.S("Elegir sample (WAV)…", "Choose sample (WAV)…") + (s.SamplePath is { } sp ? "  [" + Path.GetFileName(sp) + "]" : ""), () => _ = ChooseSampleAsync()));
        menu.Items.Add(Item(Loc.S($"Altura original del sample: {s.SampleRootHz:0.##} Hz…", $"Sample root pitch: {s.SampleRootHz:0.##} Hz…"), () => _ = AskSynthAsync(
            Loc.S("Hz a los que el sample suena sin transponer", "Hz at which the sample plays untransposed"), 8, 8000, s.SampleRootHz, 1, v => session.Synth with { SampleRootHz = v })));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item((s.NormalizeRender ? "☑ " : "☐ ") + Loc.S("Normalizar el render a −1 dBTP (pico real)", "Normalise renders to −1 dBTP (true peak)"), () => SetSynth(session.Synth with { NormalizeRender = !session.Synth.NormalizeRender })));
        var rates = new MenuItem { Header = Loc.S($"Frecuencia del render: {s.RenderSampleRate} Hz", $"Render sample rate: {s.RenderSampleRate} Hz") };
        foreach (int r in new[] { 44100, 48000, 88200, 96000 })
        {
            int rr = r;
            rates.Items.Add(Item((s.RenderSampleRate == r ? "● " : "○ ") + r + " Hz", () => SetSynth(session.Synth with { RenderSampleRate = rr })));
        }
        menu.Items.Add(rates);
    }

    /// <summary>Replaces the canvas with a starter image (undoable), adopts its suggested synthesis settings (keeping Loop) and axis.</summary>
    /// <param name="preset">The example to load.</param>
    /// <remarks>
    /// The settings are set BEFORE the image so the player's restart on ImageReplaced already uses
    /// them. Only the image replacement is undoable; the adopted synthesis settings stay.
    /// </remarks>
    void LoadPreset(PaintPreset preset)
    {
        var (img, synth) = preset.Build(session.Synth);
        session.Synth = synth with { Loop = session.Synth.Loop };
        session.ReplaceImage(img, "preset");
        session.ImageName = preset.NameEn.Replace(' ', '_');
        prefs.AxisKind = img.Axis.Kind;
        prefs.LowHz = img.Axis.LowHz;
        prefs.HighHz = img.Axis.HighHz;
        if (!img.Axis.IsDiscrete) prefs.Rows = img.Axis.RowCount;
        if (img.Axis.Kind == PitchAxisKind.EqualTemperament) prefs.StepsPerOctave = img.Axis.StepsPerOctave;
        canvas.CursorColumn = 0;
        SyncFromSession();
        session.MarkPictureSaved();   // an untouched example can always be loaded again
        Say(Loc.S($"Ejemplo «{preset.NameEs}»: {preset.TipEs}", $"Example «{preset.NameEn}»: {preset.TipEn}"), false);
    }

    /// <summary>Runs an image operation on the selection (or everything), asking for its parameter first when it has one.</summary>
    /// <param name="op">The operation from <see cref="ImageOps.All"/>.</param>
    /// <remarks>Cancelling the parameter dialog does nothing. <c>session.Apply</c> records the undo step and raises PixelsChanged / ImageReplaced as needed.</remarks>
    async Task RunOperationAsync(ImageOperation op)
    {
        double value = 0;
        if (op.Parameter is { } p)
        {
            if (await PaintParameterDialog.AskAsync(ctx.Window, Loc.S(op.NameEs, op.NameEn), Loc.S(p.LabelEs, p.LabelEn), p.Min, p.Max, p.Default, p.Step, p.Format) is not { } v)
                return;
            value = v;
        }
        session.Apply(op, value);
        Say(Loc.S(op.NameEs, op.NameEn) + " ✓", false);
    }

    /// <summary>Asks for one synthesis number in a small dialog and applies it through <paramref name="make"/>.</summary>
    /// <param name="label">Prompt shown next to the number.</param>
    /// <param name="min">Smallest accepted value.</param>
    /// <param name="max">Largest accepted value.</param>
    /// <param name="value">Initial value.</param>
    /// <param name="step">Spinner increment.</param>
    /// <param name="make">Builds the new settings from the chosen value.</param>
    async Task AskSynthAsync(string label, double min, double max, double value, double step, Func<double, PaintSynthSettings> make)
    {
        if (await PaintParameterDialog.AskAsync(ctx.Window, Loc.S("Sonido", "Sound"), label, min, max, value, step, "0.###") is { } v)
            SetSynth(make(v));
    }

    // ------------------------------------------------------------------------------------------
    //  Files
    // ------------------------------------------------------------------------------------------

    /// <summary>File › Import picture: an open picker filtered to the supported picture formats.</summary>
    async Task ImportPictureAsync()
    {
        var files = await ctx.Window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.S("Importar imagen", "Import picture"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Loc.S("Imágenes", "Pictures")) { Patterns = ImageCodec.Extensions.Select(e => "*" + e).ToArray() }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            await ImportPictureAsync(path, alwaysAsk: true);
    }

    /// <summary>Synchronous entry kept for callers/tests: imports with the dialog rules of a drop.</summary>
    /// <param name="path">Picture file.</param>
    public void ImportPicture(string path) => _ = ImportPictureAsync(path, alwaysAsk: false);

    /// <summary>
    /// Imports a picture of ANY size and shape into the canvas (undoable). The file is decoded on a
    /// background thread (large photos take a moment; the Windows decoder also applies the EXIF
    /// rotation of phone photos), then <see cref="PaintImportDialog"/> asks how to fit it — unless
    /// the user ticked «don't ask again» and this is a drop (<paramref name="alwaysAsk"/> false), in
    /// which case the remembered choices are used. The canvas keeps the current pitch axis (the
    /// picture's height becomes the rows); its width becomes columns as chosen.
    /// </summary>
    /// <param name="path">Picture file.</param>
    /// <param name="alwaysAsk">True for File › Import picture (always shows the dialog).</param>
    /// <returns>A task that completes when the import finished, failed or was cancelled (errors are reported in the status line, never thrown).</returns>
    public async Task ImportPictureAsync(string path, bool alwaysAsk)
    {
        DecodedPicture picture;
        try
        {
            Say(Loc.S($"Leyendo {Path.GetFileName(path)}…", $"Reading {Path.GetFileName(path)}…"), false);
            picture = await Task.Run(() => ImageCodec.Decode(path));
        }
        catch (Exception ex)
        {
            Say(Loc.S("No se pudo leer la imagen: ", "Could not read the picture: ") + ex.Message, true);
            return;
        }
        // The decoded picture holds native pixel memory: disposed when this block ends, after the
        // background FromPicture has finished with it.
        using (picture)
        {
            var fit = prefs.ImportFit;
            var mapping = prefs.ImportMapping;
            double duration = session.Image.DurationSeconds;
            // Resolution: the dialog may choose another pitch axis / rows and the columns; without
            // the dialog the canvas's own axis and width are kept.
            var axis = session.Image.Axis;
            // «Full detail» without the dialog: one column per picture pixel.
            int columns = fit == ImportFit.FullDetail ? Math.Min(picture.Bitmap.Width, SpectralImage.MaxWidth) : session.Image.Width;
            if (alwaysAsk || prefs.AskOnImport)
            {
                var choice = await PaintImportDialog.AskAsync(ctx.Window, Path.GetFileName(path), picture,
                    session.Image.Axis, session.Image.Width, prefs, duration);
                if (choice is null)
                {
                    Say(Loc.S("Importación cancelada.", "Import cancelled."), false);
                    return;
                }
                (fit, mapping, duration, columns, axis) = (choice.Fit, choice.Mapping, choice.DurationSeconds, choice.Columns, choice.Axis);
                prefs.ImportFit = fit;
                prefs.ImportMapping = mapping;
                if (choice.DontAskAgain) prefs.AskOnImport = false;
            }
            try
            {
                // Scaling and colour mapping of a big picture also take a moment: off the UI thread.
                // (With «keep proportions» FromPicture derives the same columns from the axis rows.)
                var img = await Task.Run(() => ImageCodec.FromPicture(picture, columns, axis, duration, mapping, fit));
                session.ReplaceImage(img, "import");
                session.ImageName = Path.GetFileNameWithoutExtension(path);
                Say(Loc.S($"Imagen importada: {Path.GetFileName(path)} ({picture.OriginalWidth} × {picture.OriginalHeight} px, {(picture.BitsPerChannel >= 16 ? "16" : "8")} bits → {img.Width} × {img.Height}). Tip: «Selección ▾ › Umbral» o «Contraste» limpia el ruido de las fotos.",
                          $"Picture imported: {Path.GetFileName(path)} ({picture.OriginalWidth} × {picture.OriginalHeight} px, {(picture.BitsPerChannel >= 16 ? "16" : "8")}-bit → {img.Width} × {img.Height}). Tip: «Selection ▾ › Threshold» or «Contrast» cleans photo noise."), false);
            }
            catch (Exception ex)
            {
                Say(Loc.S("No se pudo importar: ", "Could not import: ") + ex.Message, true);
            }
        }
    }

    /// <summary>File › Export PNG (16- or 8-bit, <see cref="PaintRoomSettings.Export16Bit"/>): red = left, green = right, blue = noise layer (or min).</summary>
    /// <remarks>One PNG pixel per cell, highest pitch at the top. Written synchronously on the UI thread (fast for canvas sizes).
    /// The status line says which import mapping restores the picture exactly.</remarks>
    /// <returns>True when the file was written.</returns>
    async Task<bool> ExportPngAsync()
    {
        try
        {
            if (await ExportPngWithPickerAsync(ctx.Window, session) is not { } path) return false;
            string bits = prefs.Export16Bit ? "16" : "8";
            if (session.Image.Noise is not null)
                Say(Loc.S($"PNG de {bits} bits guardado: {path} (R = izquierda, G = derecha, B = ruido; reimpórtalo con «RGB: … B = ruido»)",
                          $"{bits}-bit PNG saved: {path} (R = left, G = right, B = noise; re-import it with «RGB: … B = noise»)"), false);
            else
                Say(Loc.S($"PNG de {bits} bits guardado: {path} (rojo = izquierda, verde = derecha; reimpórtalo con «Rojo → izquierda»)",
                          $"{bits}-bit PNG saved: {path} (red = left, green = right; re-import it with «Red → left»)"), false);
            return true;
        }
        catch (Exception ex)
        {
            Say(ex.Message, true);
            return false;
        }
    }

    /// <inheritdoc/>
    public bool HasUnsavedChanges => session.HasUnsavedPicture && !session.PictureIsBlank;

    /// <inheritdoc/>
    public string UnsavedName => Loc.S("la imagen de Paint Synth (se guarda como PNG)", "the Paint Synth picture (saved as PNG)");

    /// <inheritdoc/>
    /// <remarks>«Save» = Export PNG (16 or 8 bits as chosen in File ▾ › Export PNG).</remarks>
    public Task<bool> SaveChangesAsync() => ExportPngAsync();

    /// <inheritdoc/>
    /// <remarks>
    /// «Don't save» discards the picture: the canvas becomes empty (same size and axis) and its
    /// history is cleared, so reopening Paint Rooms does not bring it back.
    /// </remarks>
    public void DiscardChanges()
    {
        session.Player?.Stop();
        var axis = prefs.BuildAxis(out _);
        session.ReplaceImage(new SpectralImage(session.Image.Width, axis, session.Image.DurationSeconds), "new", undoable: false);
        session.History.Clear();
        session.ImageName = "untitled";
        session.MarkPictureSaved();
    }

    /// <summary>
    /// Asks where to save and exports the session's picture as PNG (16 or 8 bits per channel, as in
    /// the preferences); marks the picture as saved. Shared by File ▾ › Export PNG and by the
    /// «Save the Paint Synth picture?» prompt when the app closes.
    /// </summary>
    /// <param name="owner">Window that owns the save picker.</param>
    /// <param name="session">The Paint Synth session.</param>
    /// <returns>The path written, or <c>null</c> when the picker was cancelled.</returns>
    /// <exception cref="Exception">Writing the file failed (the caller reports it).</exception>
    public static async Task<string?> ExportPngWithPickerAsync(TopLevel owner, PaintSession session)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.S("Exportar PNG", "Export PNG"),
            SuggestedFileName = session.ImageName + ".png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"] }],
            ShowOverwritePrompt = true,
        });
        if (file?.TryGetLocalPath() is not { } path) return null;
        ImageCodec.ExportPng(session.Image, path, session.Settings.Export16Bit);
        session.MarkPictureSaved();
        return path;
    }

    /// <summary>Loads a .scl file and switches the axis to it; cancelling or an invalid file leaves the axis as it was.</summary>
    async Task LoadScalaAsync()
    {
        var files = await ctx.Window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.S("Cargar escala Scala", "Load Scala scale"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Scala") { Patterns = ["*.scl"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
        {
            SyncFromSession(); // cancelled: the axis combo shows the current axis again
            return;
        }
        try
        {
            // Parse once here to validate the file and show its description; ApplyAxis → prefs.BuildAxis
            // reads it again from ScalaPath.
            var scale = ScalaScale.Load(path);
            prefs.ScalaPath = path;
            prefs.AxisKind = PitchAxisKind.Scala;
            Say(Loc.S($"Escala «{scale.Name}»: {scale.Description} ({scale.NotesPerPeriod} notas)", $"Scale «{scale.Name}»: {scale.Description} ({scale.NotesPerPeriod} notes)"), false);
            ApplyAxis();
        }
        catch (Exception ex)
        {
            Say(Loc.S("Archivo .scl no válido: ", "Invalid .scl file: ") + ex.Message, true);
            SyncFromSession();
        }
    }

    /// <summary>Sound › Choose sample: reads the WAV to validate it and switches to the sample-loop mode.</summary>
    async Task ChooseSampleAsync()
    {
        var files = await ctx.Window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.S("Elegir sample", "Choose sample"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("WAV") { Patterns = ["*.wav"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            var a = WavIO.Read(path);
            SetSynth(session.Synth with { SamplePath = path, Mode = SynthMode.SampleLoop });
            Say(Loc.S($"Sample: {Path.GetFileName(path)} ({a.Seconds:0.##} s). Ajusta «Altura original» para afinarlo.", $"Sample: {Path.GetFileName(path)} ({a.Seconds:0.##} s). Set «Root pitch» to tune it."), false);
        }
        catch (Exception ex)
        {
            Say(ex.Message, true);
        }
    }

    /// <summary>Accepts files dragged over the canvas.</summary>
    /// <param name="sender">The canvas.</param>
    /// <param name="e">Drag data; any file shows the copy cursor (the extension is checked on drop).</param>
    void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Dropped file: a picture is imported, a WAV becomes the sample, a .scl becomes the axis.</summary>
    /// <param name="sender">The canvas.</param>
    /// <param name="e">Drop data; only the first file is used and other extensions are ignored.</param>
    /// <remarks>
    /// Unlike the menu commands, a dropped WAV or .scl is not parsed here first: a bad WAV surfaces
    /// when the player / renderer loads it, a bad .scl as an «Invalid axis» message from ApplyAxis.
    /// </remarks>
    void OnDrop(object? sender, DragEventArgs e)
    {
        var file = e.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (file is null) return;
        string ext = Path.GetExtension(file).ToLowerInvariant();
        if (ImageCodec.Extensions.Contains(ext)) ImportPicture(file);
        else if (ext == ".wav") { SetSynth(session.Synth with { SamplePath = file, Mode = SynthMode.SampleLoop }); Say(Loc.S("Sample: ", "Sample: ") + Path.GetFileName(file), false); }
        else if (ext == ".scl") { prefs.ScalaPath = file; prefs.AxisKind = PitchAxisKind.Scala; ApplyAxis(); }
        e.Handled = true;
    }

    /// <summary>The «?» quick help: the colour rule and the shortcuts.</summary>
    void ShowHelp()
    {
        string text = Loc.S(
            "CÓMO FUNCIONA\n• De izquierda a derecha = tiempo. De abajo hacia arriba = altura (la regla izquierda dice cuál).\n• Brillo = volumen. Rojo = solo izquierda, verde = solo derecha, el color del tema = ambos lados.\n• Ruido (control del pincel): 0 = tono puro, 1 = banda de ruido; las celdas ruidosas se ven granuladas. Actúa en el modo Senos (Bandas de ruido ya es todo ruido).\n• Tocar (Espacio) suena desde el cursor amarillo; pinta mientras suena y se escucha al instante.\n\nATAJOS\nP lápiz · A aerógrafo · S spray · H armónicos · L línea · R rectángulo · O elipse · G degradado · E goma · B desenfocar · U difuminar\nM selección · Mayús+M elipse · F lazo · N spline (Enter cierra, Esc cancela); Mayús agrega, Alt resta\nCtrl+Z / Ctrl+Y (o Ctrl+Mayús+Z) deshacer / rehacer · Ctrl+C / X / V · Supr borra la selección · Esc la quita · Inicio = cursor al comienzo · Ctrl+0 ver todo\nBotón derecho = borrar · rueda = altura · Shift+rueda = tiempo · Ctrl+rueda = zoom · Alt+rueda = zoom de altura\n\nLa guía completa con un tutorial paso a paso está en Ayuda › Documentación (Paint Rooms).",
            "HOW IT WORKS\n• Left to right = time. Bottom to top = pitch (the left ruler tells which).\n• Brightness = loudness. Red = left only, green = right only, the theme colour = both sides.\n• Noise (brush control): 0 = pure tone, 1 = noise band; noisy cells look grainy. Acts in the Sines mode (Noise bands is all noise already).\n• Play (Space) sounds from the yellow cursor; paint while it plays and you hear it at once.\n\nSHORTCUTS\nP pencil · A airbrush · S spray · H harmonics · L line · R rectangle · O ellipse · G gradient · E eraser · B blur · U smudge\nM selection · Shift+M ellipse · F lasso · N spline (Enter closes, Esc cancels); Shift adds, Alt subtracts\nCtrl+Z / Ctrl+Y (or Ctrl+Shift+Z) undo / redo · Ctrl+C / X / V · Del clears the selection · Esc removes it · Home = cursor to start · Ctrl+0 fit all\nRight button = erase · wheel = pitch · Shift+wheel = time · Ctrl+wheel = zoom · Alt+wheel = pitch zoom\n\nThe full guide with a step-by-step tutorial is in the documentation (Paint Rooms).");
        // Picture import: what each «Colours mean» choice does (the same texts as the import dialog,
        // so the two never disagree).
        var import = new System.Text.StringBuilder();
        import.Append("\n\n").Append(Loc.S("IMPORTAR IMÁGENES (Archivo ▾ › Importar imagen…, o arrastrarla al lienzo)", "IMPORTING PICTURES (File ▾ › Import picture…, or drag it onto the canvas)"));
        import.Append('\n').Append(PaintImportDialog.PictureBasics);
        foreach (var (m, es, en) in new[]
        {
            (ColorMapping.Brightness, "Brillo = volumen (mono)", "Brightness = loudness (mono)"),
            (ColorMapping.RedGreen, "Rojo = izquierda, verde = derecha", "Red = left, green = right"),
            (ColorMapping.BrightnessHuePan, "Brillo = volumen, color = posición estéreo", "Brightness = loudness, hue = stereo position"),
            (ColorMapping.RgbNoise, "RGB", "RGB"),
            (ColorMapping.RgbaNoiseMask, "RGBA", "RGBA"),
        })
            import.Append("\n• ").Append(Loc.S(es, en)).Append(": ").Append(PaintImportDialog.MappingHelp(m));
        text += import.ToString();
        _ = new InfoDialog(Loc.S("Paint Synth · ayuda", "Paint Synth · help"), text).ShowDialog(ctx.Window);
    }

    // ------------------------------------------------------------------------------------------
    //  Small helpers
    // ------------------------------------------------------------------------------------------

    /// <summary>Shows a message at the bottom right (accent colour, or the warning colour).</summary>
    /// <param name="text">Message (also set as the tooltip, since the line is trimmed with an ellipsis).</param>
    /// <param name="warning">True = warning colour.</param>
    /// <remarks>Also the handler of the player's Message event.</remarks>
    void Say(string text, bool warning)
    {
        messageText.Text = text;
        messageText.Foreground = warning ? AppTheme.Brush("Warn") : AppTheme.Brush("AccentHi");
        ToolTip.SetTip(messageText, text);
    }

    /// <summary>A tool-bar row that wraps when the window is narrow.</summary>
    static WrapPanel Row() => new() { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 10, 2) };

    /// <summary>Adds controls to a tool-bar row with the standard spacing.</summary>
    /// <param name="panel">The row.</param>
    /// <param name="controls">Controls to append (their Margin is overwritten).</param>
    static void AddAll(Panel panel, params Control[] controls)
    {
        foreach (var c in controls)
        {
            c.Margin = new Thickness(0, 2, 6, 2);
            panel.Children.Add(c);
        }
    }

    /// <summary>Small dim caption used for slider values.</summary>
    static TextBlock Small() => new() { FontSize = 11, Foreground = AppTheme.Brush("TextDim"), HorizontalAlignment = HorizontalAlignment.Right };

    /// <summary>Section caption of the tools column («TOOLS», «BRUSH»…).</summary>
    /// <param name="text">Caption text.</param>
    static TextBlock Caption(string text) =>
        new() { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = AppTheme.Brush("AccentHi"), Margin = new Thickness(2, 6, 0, 2) };

    /// <summary>A slider with its label on the left and its value on the right above it.</summary>
    /// <param name="label">Label text.</param>
    /// <param name="value">Value caption (updated by <see cref="OnBrushChanged"/>).</param>
    /// <param name="slider">The slider.</param>
    static Control Labeled(string label, TextBlock value, Slider slider)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = AppTheme.Brush("Text") });
        Grid.SetColumn(value, 1);
        head.Children.Add(value);
        return new StackPanel { Spacing = 0, Children = { head, slider } };
    }

    /// <summary>A menu item that runs <paramref name="click"/>.</summary>
    /// <param name="header">Item text.</param>
    /// <param name="click">Action run on click.</param>
    static MenuItem Item(string header, Action click)
    {
        var m = new MenuItem { Header = header };
        m.Click += (_, _) => click();
        return m;
    }

    /// <summary>A button that opens a menu built fresh each time (so check marks and values are current).</summary>
    /// <param name="text">Button text.</param>
    /// <param name="tip">Tooltip.</param>
    /// <param name="build">Fills the new flyout.</param>
    static Button MenuButton(string text, string tip, Action<MenuFlyout> build)
    {
        // ToolUi.Button needs an action; the real work is the Click handler below, which needs «b».
        var b = ToolUi.Button(text, tip, () => { });
        b.Click += (_, _) =>
        {
            var menu = new MenuFlyout();
            build(menu);
            menu.ShowAt(b);
        };
        return b;
    }
}
