// ImageFilterRoom.cs — Paint Rooms: the «Image Filter» room (a picture used as a moving stereo EQ).
//
// Role: choose a SOUND (a WAV file, the sound another room shared, or the live audio input), paint
// or import a FILTER PICTURE, and hear the sound through it: every column of the picture is an
// equaliser curve for one moment (row = band around that row's frequency, brightness = gain), the
// left layer filters the left channel and the right layer the right channel. Csound does the
// filtering live (ImageFilterPlayer) and offline (ImageFilterRenderer, 24-bit WAV), and «Show
// Csound code» opens the readable .csd. Built in code like Paint Synth, themed through AppTheme,
// every visible string bilingual through Loc.S.
//
// The filter picture is NOT the Paint Synth canvas: the room keeps its own PaintSession (own image,
// undo history, selection, axis — logarithmic 30 Hz–18 kHz by default — and brush), kept for the
// whole app run in a table keyed by the shared session, so closing the window loses nothing. «Canvas
// ▾» copies the Paint Synth picture in, or sends the filter picture to Paint Synth.
//
// Layout:
//   row 1  transport (Play/Stop, Loop source, ⇤, Render WAV, Show Csound code) · source (kind,
//          Open WAV…, name) · Mix % · Output dB
//   row 2  time mapping (Fit / Seconds / Bars, seconds, bars, BPM, speed) · FFT size, overlap,
//          latency read-out · smoothing, glide · menus (Filter, Canvas, Image, Examples, ?)
//   left   tool palette + brush (size, level, hardness, pan with L/C/R)
//   centre the source waveform (with its play position) above the canvas with scroll bars
//   bottom hover read-out, messages, DSP load.
//
// Data flow while playing:
//   canvas edit → PaintSession.PixelsChanged → OnPixelsChanged → player.OnImageEdited (dirty range)
//   → 30 Hz DispatcherTimer (OnTick) → player.Tick → ImageFilterPlayer.FlushEdits → Csound tables.
//   knob change → SetFilter → prefs.Settings (persisted) → player.UpdateSettings (live channel,
//   re-upload or restart). The same timer moves the canvas play line and the waveform position
//   from player.CurrentColumn / SourcePosition and refreshes the DSP read-out.
//
// Settings: everything the user chooses lives in shared.Settings.Filter (ImageFilterPrefs: the
// ImageFilterSettings record, the source kind and last WAV path, the canvas settings), so it is
// saved with the other Paint Rooms settings. The «syncing» flag is raised while code writes
// controls from those settings so the controls' change handlers do not write them back.
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
using CsoundVisual.Controls;
using CsoundVisual.Localization;
using CsoundVisual.Paint;
using CsoundVisual.Theming;

namespace CsoundVisual.Views;

/// <summary>The «Image Filter» room (see the file header).</summary>
public sealed class ImageFilterRoom : IPaintRoom
{
    /// <summary>
    /// What the room keeps for the whole app run, per shared Paint Rooms session: its own canvas
    /// session, its live player and the current source (so reopening the window resumes).
    /// </summary>
    sealed class RoomState
    {
        /// <summary>The filter canvas (image, history, selection, brush).</summary>
        public required PaintSession Canvas { get; init; }
        /// <summary>The live player.</summary>
        public required ImageFilterPlayer Player { get; init; }
        /// <summary>The prepared file source (picker / drop), or <c>null</c>.</summary>
        public FilterSource? File { get; set; }
        /// <summary>The prepared copy of the shared sound, and the AudioData it was made from.</summary>
        public FilterSource? Shared { get; set; }
        /// <summary>The AudioData behind <see cref="Shared"/> (to notice when another room replaces it).</summary>
        public AudioData? SharedFrom { get; set; }
    }

    /// <summary>Room state per shared session (weak: it goes away with the session).</summary>
    static readonly ConditionalWeakTable<PaintSession, RoomState> States = new();

    /// <summary>
    /// The filter canvas of a shared Paint Rooms session, or <c>null</c> when the Image Filter was
    /// never opened. It outlives the window (reopening resumes it) but not the app, so the main
    /// window asks on exit whether to save it as PNG.
    /// </summary>
    /// <param name="shared">The shared Paint Rooms session.</param>
    /// <returns>The canvas session, or <c>null</c>.</returns>
    public static PaintSession? CanvasFor(PaintSession shared) => States.TryGetValue(shared, out var st) ? st.Canvas : null;

    /// <summary>The Paint Rooms window context (shared session, app host, window for dialogs, editor hand-off).</summary>
    readonly PaintRoomContext ctx;
    readonly PaintSession shared;      // the Paint Rooms session (Paint Synth's picture, shared sound)
    /// <summary>This room's long-lived state (survives closing and reopening the window).</summary>
    readonly RoomState state;
    /// <summary>The filter picture's own session (= <c>state.Canvas</c>).</summary>
    readonly PaintSession canvasSession;
    /// <summary>The persisted room settings (inside the shared Paint settings).</summary>
    readonly ImageFilterPrefs prefs;
    /// <summary>Canvas settings of the filter picture (axis, size, brush), = <c>prefs.Canvas</c>.</summary>
    readonly PaintRoomSettings canvasPrefs;
    /// <summary>The live player (= <c>state.Player</c>).</summary>
    readonly ImageFilterPlayer player;
    /// <summary>The paintable canvas control (shared with Paint Synth's implementation).</summary>
    readonly PaintCanvas canvas = new();
    /// <summary>The source sound's waveform strip above the canvas.</summary>
    readonly SourceWaveform waveform = new();
    readonly ScrollBar hScroll = new() { Orientation = Orientation.Horizontal, Height = 12 };
    readonly ScrollBar vScroll = new() { Orientation = Orientation.Vertical, Width = 12 };
    /// <summary>~30 Hz UI timer (33 ms): drives <see cref="ImageFilterPlayer.Tick"/>, the play line and the read-outs.</summary>
    readonly DispatcherTimer timer;
    /// <summary>The room's whole UI (returned by <see cref="Content"/>); also the drag-and-drop target.</summary>
    readonly DockPanel root = new();

    // ---- transport & source
    readonly Button playButton;
    readonly ToggleButton loopToggle;
    readonly Button renderButton;
    /// <summary>Source kind; the item index is the <see cref="FilterSourceKind"/> value.</summary>
    readonly ComboBox sourceBox = new() { Width = 176, VerticalAlignment = VerticalAlignment.Center };
    readonly Button openButton;
    /// <summary>Name and length of the current source.</summary>
    readonly TextBlock sourceText = new() { FontSize = 12, Foreground = AppTheme.Brush("TextDim"), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis };
    /// <summary>Dry/wet mix in % (the settings hold 0…1).</summary>
    readonly NumericUpDown mixBox;
    /// <summary>Output gain in dB.</summary>
    readonly NumericUpDown outBox;

    // ---- time, FFT, smoothing
    /// <summary>Time mapping; the item index is the <see cref="FilterTimeMode"/> value.</summary>
    readonly ComboBox timeBox = new() { Width = 162, VerticalAlignment = VerticalAlignment.Center };
    readonly NumericUpDown secondsBox, barsBox, bpmBox, speedBox, smoothBox;
    /// <summary>Captioned wrappers, shown or hidden according to the time mode.</summary>
    readonly StackPanel secondsPanel, barsPanel, bpmPanel;
    /// <summary>FFT size; the item index points into <see cref="ImageFilterSettings.FftSizes"/>.</summary>
    readonly ComboBox fftBox = new() { Width = 84, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>Overlap; the item index points into <see cref="ImageFilterSettings.Overlaps"/>.</summary>
    readonly ComboBox overlapBox = new() { Width = 64, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>Filter latency read-out (samples → ms at the engine's rate).</summary>
    readonly TextBlock latencyText = new() { FontSize = 12, Foreground = AppTheme.Brush("TextDim"), VerticalAlignment = VerticalAlignment.Center };
    readonly ToggleButton glideToggle;

    // ---- brush
    /// <summary>The palette's tool buttons, kept to show which tool is active.</summary>
    readonly Dictionary<PaintTool, ToggleButton> toolButtons = [];

    /// <summary>The «SELECTION» options (feather), shown while a selection tool is active (built in <see cref="BuildPalette"/>).</summary>
    StackPanel? selectionPanel;
    readonly Slider sizeSlider = new() { Minimum = 1, Maximum = 160, Width = 150 };
    readonly Slider levelSlider = new() { Minimum = 0.02, Maximum = 1, Width = 150 };
    readonly Slider hardSlider = new() { Minimum = 0, Maximum = 1, Width = 150 };
    readonly Slider panSlider = new() { Minimum = -1, Maximum = 1, Width = 150 };
    /// <summary>Value read-outs next to the brush sliders.</summary>
    readonly TextBlock sizeText = Small(), levelText = Small(), hardText = Small(), panText = Small();

    // ---- status
    readonly TextBlock hoverText = new() { FontSize = 12, Foreground = AppTheme.Brush("TextDim"), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock messageText = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right };
    readonly TextBlock dspText = new() { FontSize = 12, Foreground = AppTheme.Brush("TextDim"), Margin = new Thickness(16, 0, 0, 0), MinWidth = 120 };

    /// <summary>True while code writes the controls from the settings (suppresses their change handlers).</summary>
    bool syncing;
    /// <summary>True while an offline render runs (the Render button becomes Cancel).</summary>
    bool rendering;
    /// <summary>Cancels the running render, or <c>null</c>.</summary>
    CancellationTokenSource? renderCancel;

    /// <summary>Creates the room for the window context <paramref name="context"/>.</summary>
    /// <param name="context">Window context; its session is the shared Paint Rooms session.</param>
    /// <remarks>
    /// Builds the whole UI in code (no XAML), then wires the session and player events and starts
    /// the 33 ms timer. A new room object is created each time the Paint Rooms window opens, but the
    /// canvas, player and sources come back from <see cref="States"/>.
    /// </remarks>
    public ImageFilterRoom(PaintRoomContext context)
    {
        ctx = context;
        shared = context.Session;
        // Repair settings from older versions (missing filter block or canvas) before using them.
        prefs = shared.Settings.Filter ??= new ImageFilterPrefs();
        prefs.Settings = (prefs.Settings ?? new ImageFilterSettings()).Sanitized();
        prefs.Canvas ??= new ImageFilterPrefs().Canvas;
        canvasPrefs = prefs.Canvas;
        // First room of this session creates the state; later ones (window reopened) get it back.
        state = States.GetValue(shared, _ => CreateState(context, prefs));
        canvasSession = state.Canvas;
        player = state.Player;
        canvas.Session = canvasSession;

        // ================= row 1: transport, source, mix =================
        playButton = ToolUi.Button(Loc.S("Tocar", "Play"),
            Loc.S("Filtra el sonido con la imagen a través de Csound, desde el cursor amarillo (Espacio). Pintar mientras suena se escucha al instante. Si Csound está detenido, se inicia solo.",
                  "Filters the sound with the picture through Csound, from the yellow cursor (Space). Painting while it plays is heard at once. If Csound is stopped it starts by itself."),
            TogglePlay, "IconPlay", primary: true);
        playButton.MinWidth = 100;
        loopToggle = ToolUi.Toggle(Loc.S("Loop", "Loop"), Loc.S("Repetir el archivo de sonido sin fin mientras suena (la imagen sigue su propio tiempo).", "Repeat the sound file endlessly while playing (the picture keeps its own timing)."), "IconRefresh");
        loopToggle.IsCheckedChanged += (_, _) => { if (!syncing) SetFilter(prefs.Settings with { LoopSource = loopToggle.IsChecked == true }); };
        var homeButton = ToolUi.Button("⇤", Loc.S("Llevar el cursor de inicio al comienzo (Inicio).", "Move the play-from cursor to the beginning (Home)."), () => canvas.CursorColumn = 0);
        renderButton = ToolUi.Button(Loc.S("Renderizar WAV", "Render WAV"),
            Loc.S("Filtra el archivo completo con Csound (sin tiempo real) y lo guarda como WAV de 24 bits, alineado con el original (la latencia del filtro se compensa): ponlo en la misma posición de Cubase y queda a tiempo.",
                  "Filters the whole file with Csound (offline) and saves a 24-bit WAV aligned with the original (the filter latency is compensated): put it at the same position in Cubase and it is in time."),
            () => _ = RenderAsync(), "IconRender");
        var codeButton = ToolUi.Button(Loc.S("Ver código Csound", "Show Csound code"),
            Loc.S("Escribe el .csd que hace este filtrado (instrumento comentado + tablas y sonido en archivos al lado) en Documentos\\Csound Visual\\Paint Rooms y lo abre en el editor: F5 lo toca.",
                  "Writes the .csd that does this filtering (commented instrument + tables and sound in files next to it) to Documents\\Csound Visual\\Paint Rooms and opens it in the editor: F5 plays it."),
            ShowCode, "IconBook");

        sourceBox.ItemsSource = new[] { Loc.S("Archivo de sonido", "Sound file"), Loc.S("Sonido de otra sala", "Other room's sound"), Loc.S("Entrada en vivo", "Live input") };
        ToolTip.SetTip(sourceBox, Loc.S(
            "Qué se filtra. Archivo: un WAV (botón «Abrir WAV…» o arrástralo aquí). Sonido de otra sala: el que compartió otra sala (p. ej. Spectrum Room). Entrada en vivo: tu micrófono o guitarra (elige la entrada en Configuración › Audio); usa audífonos para evitar acoples.",
            "What gets filtered. File: a WAV («Open WAV…» or drag it here). Other room's sound: the one another room shared (e.g. the Spectrum Room). Live input: your microphone or guitar (choose the input in Settings › Audio); use headphones to avoid feedback."));
        // Item order = FilterSourceKind values (File, Shared, LiveInput).
        sourceBox.SelectionChanged += (_, _) =>
        {
            if (syncing || sourceBox.SelectedIndex < 0) return;
            prefs.Source = (FilterSourceKind)sourceBox.SelectedIndex;
            OnSourceChanged();
        };
        openButton = ToolUi.Button(Loc.S("Abrir WAV…", "Open WAV…"), Loc.S("Elige el archivo de sonido a filtrar (también puedes arrastrar un WAV a la ventana).", "Choose the sound file to filter (you can also drag a WAV onto the window)."), () => _ = OpenWavAsync());
        mixBox = ToolUi.Number(0, 100, prefs.Settings.Mix * 100, 5,
            Loc.S("Mezcla seco/filtrado en %: 100 = solo lo filtrado, 0 = solo el original (alineado con la latencia del filtro). Cambia en vivo.", "Dry/wet mix in %: 100 = only the filtered sound, 0 = only the original (aligned with the filter latency). Changes live."), "0", 70, spinner: false);
        mixBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) SetFilter(prefs.Settings with { Mix = (double)v / 100 }); };
        outBox = ToolUi.Number(-60, 12, prefs.Settings.OutputDb, 1,
            Loc.S("Ganancia de salida en dB. Hay un limitador suave a −1 dBFS por seguridad. Cambia en vivo.", "Output gain in dB. A soft limiter at −1 dBFS keeps it safe. Changes live."), "+0;-0;0", 64, spinner: false);
        outBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) SetFilter(prefs.Settings with { OutputDb = (double)v }); };

        var row1 = Row();
        AddAll(row1, playButton, loopToggle, homeButton, ToolUi.Separator(), renderButton, codeButton, ToolUi.Separator(),
            ToolUi.Captioned(Loc.S("Fuente", "Source"), sourceBox), openButton, sourceText, ToolUi.Separator(),
            ToolUi.Captioned(Loc.S("Mezcla %", "Mix %"), mixBox), ToolUi.Captioned(Loc.S("Salida dB", "Output dB"), outBox));

        // ================= row 2: time, FFT, smoothing, menus =================
        timeBox.ItemsSource = new[] { Loc.S("Ajustar al sonido", "Fit to sound"), Loc.S("Loop en segundos", "Loop in seconds"), Loc.S("Loop en compases", "Loop in bars") };
        ToolTip.SetTip(timeBox, Loc.S(
            "Cómo dura la imagen. Ajustar al sonido: la imagen se estira sobre todo el archivo (la primera columna filtra el comienzo, la última el final). Loop en segundos / compases: la imagen dura eso y se repite (compases de 4/4 al tempo elegido). Con la entrada en vivo se usa el loop.",
            "How long the picture lasts. Fit to sound: the picture is stretched over the whole file (the first column filters the start, the last one the end). Loop in seconds / bars: the picture lasts that long and repeats (4/4 bars at the chosen tempo). Live input uses the loop."));
        timeBox.SelectionChanged += (_, _) => { if (!syncing && timeBox.SelectedIndex >= 0) SetFilter(prefs.Settings with { TimeMode = (FilterTimeMode)timeBox.SelectedIndex }); };
        secondsBox = ToolUi.Number(0.05, 3600, prefs.Settings.LoopSeconds, 0.5, Loc.S("Segundos que dura una pasada de la imagen.", "Seconds one pass over the picture lasts."), "0.##", 60, spinner: false);
        secondsBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) SetFilter(prefs.Settings with { LoopSeconds = (double)v }); };
        barsBox = ToolUi.Number(0.25, 256, prefs.Settings.Bars, 1, Loc.S("Compases de 4/4 por pasada de la imagen.", "4/4 bars per pass over the picture."), "0.##", 52, spinner: false);
        barsBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) SetFilter(prefs.Settings with { Bars = (double)v }); };
        bpmBox = ToolUi.Number(20, 400, prefs.Settings.Bpm, 1, Loc.S("Tempo en BPM (negras por minuto).", "Tempo in BPM (quarter notes per minute)."), "0.##", 56, spinner: false);
        bpmBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) SetFilter(prefs.Settings with { Bpm = (double)v }); };
        speedBox = ToolUi.Number(0.05, 8, prefs.Settings.Speed, 0.25, Loc.S("Velocidad del cabezal sobre la imagen: 2 = la imagen pasa el doble de rápido.", "Speed of the play head over the picture: 2 = the picture passes twice as fast."), "0.##", 50, spinner: false);
        speedBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) SetFilter(prefs.Settings with { Speed = (double)v }); };
        secondsPanel = ToolUi.Captioned(Loc.S("Segundos", "Seconds"), secondsBox);
        barsPanel = ToolUi.Captioned(Loc.S("Compases", "Bars"), barsBox);
        bpmPanel = ToolUi.Captioned("BPM", bpmBox);

        fftBox.ItemsSource = ImageFilterSettings.FftSizes.Select(n => n.ToString()).ToArray();
        ToolTip.SetTip(fftBox, Loc.S(
            "Tamaño de la FFT. Más grande = bandas más finas (útil para el peine armónico) pero más latencia y ataques más borrosos. 1024 es un buen equilibrio; 256–512 para tocar en vivo con poca latencia.",
            "FFT size. Bigger = finer bands (useful for the harmonic comb) but more latency and blurrier attacks. 1024 is a good balance; 256–512 to play live with little latency."));
        fftBox.SelectionChanged += (_, _) => { if (!syncing && fftBox.SelectedIndex >= 0) SetFilter(prefs.Settings with { FftSize = ImageFilterSettings.FftSizes[fftBox.SelectedIndex] }); };
        overlapBox.ItemsSource = ImageFilterSettings.Overlaps.Select(n => "×" + n).ToArray();
        ToolTip.SetTip(overlapBox, Loc.S("Solapamiento de las ventanas FFT: ×4 es el clásico; ×8 suaviza los cambios rápidos de la imagen (el doble de CPU).", "Overlap of the FFT windows: ×4 is the classic; ×8 smooths fast picture changes (twice the CPU)."));
        overlapBox.SelectionChanged += (_, _) => { if (!syncing && overlapBox.SelectedIndex >= 0) SetFilter(prefs.Settings with { Overlap = ImageFilterSettings.Overlaps[overlapBox.SelectedIndex] }); };
        ToolTip.SetTip(latencyText, Loc.S("Latencia del filtro: FFT + 2 saltos. En el render se compensa; en vivo se suma a la del audio.", "Filter latency: FFT + 2 hops. Renders compensate it; live it adds to the audio latency."));
        smoothBox = ToolUi.Number(0.5, 2000, prefs.Settings.SmoothingMs, 5,
            Loc.S("Suavizado (ms) de la curva de ecualización: evita «chasquidos» cuando la imagen cambia de golpe. Largo = el filtro sigue a la imagen con retraso, como una envolvente lenta. Cambia en vivo.",
                  "Smoothing (ms) of the EQ curve: avoids «zipper» clicks when the picture changes abruptly. Long = the filter follows the picture lazily, like a slow envelope. Changes live."), "0.#", 56, spinner: false);
        smoothBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) SetFilter(prefs.Settings with { SmoothingMs = (double)v }); };
        glideToggle = ToolUi.Toggle(Loc.S("Suave", "Glide"),
            Loc.S("Encendido: mezcla las columnas vecinas (el filtro se transforma de forma continua). Apagado: salta de columna en columna (patrones rítmicos nítidos).", "On: blends neighbouring columns (the filter morphs continuously). Off: jumps from column to column (crisp rhythmic patterns)."));
        glideToggle.IsCheckedChanged += (_, _) => { if (!syncing) SetFilter(prefs.Settings with { Glide = glideToggle.IsChecked == true }); };

        var filterMenu = MenuButton(Loc.S("Filtro ▾", "Filter ▾"), Loc.S("Cómo el brillo se vuelve ganancia, refuerzo de los brillantes, normalización del render.", "How brightness becomes gain, boost of bright areas, render normalisation."), BuildFilterMenu);
        var canvasMenu = MenuButton(Loc.S("Lienzo ▾", "Canvas ▾"), Loc.S("Lienzo blanco o negro, importar una imagen, traer o enviar la imagen de Paint Synth, eje de alturas.", "White or black canvas, import a picture, bring or send the Paint Synth picture, pitch axis."), BuildCanvasMenu);
        var examplesMenu = MenuButton(Loc.S("Ejemplos ▾", "Examples ▾"), Loc.S("Imágenes de filtro de partida.", "Starter filter pictures."), BuildExamplesMenu);
        var helpButton = ToolUi.Button("?", Loc.S("Ayuda rápida del Filtro de imagen.", "Image Filter quick help."), ShowHelp);

        var row2 = Row();
        AddAll(row2, ToolUi.Captioned(Loc.S("Tiempo", "Time"), timeBox), secondsPanel, barsPanel, bpmPanel,
            ToolUi.Captioned(Loc.S("Velocidad", "Speed"), speedBox), ToolUi.Separator(),
            ToolUi.Captioned("FFT", fftBox), ToolUi.Captioned(Loc.S("Solape", "Overlap"), overlapBox), latencyText,
            ToolUi.Captioned(Loc.S("Suavizar ms", "Smooth ms"), smoothBox), glideToggle, ToolUi.Separator(),
            filterMenu, canvasMenu, examplesMenu, helpButton);

        // ================= palette, canvas, status =================
        var palette = BuildPalette();

        // surface: 2 columns (canvas | vertical scroll bar) × 3 rows (waveform 58 px | canvas | horizontal bar).
        var surface = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 10, 0) };
        var waveBorder = new Border { Child = waveform, Height = 58, CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderBrush = AppTheme.Brush("Line"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 6) };
        ToolTip.SetTip(waveBorder, Loc.S("El sonido de entrada (con su posición mientras suena). Suelta aquí un WAV para filtrarlo.", "The input sound (with its position while playing). Drop a WAV here to filter it."));
        surface.Children.Add(waveBorder);
        var canvasBorder = new Border { Child = canvas, CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderBrush = AppTheme.Brush("Line"), BorderThickness = new Thickness(1) };
        Grid.SetRow(canvasBorder, 1);
        surface.Children.Add(canvasBorder);
        Grid.SetColumn(vScroll, 1);
        Grid.SetRow(vScroll, 1);
        Grid.SetRow(hScroll, 2);
        surface.Children.Add(vScroll);
        surface.Children.Add(hScroll);
        hScroll.Scroll += (_, _) => { if (!syncing) canvas.ScrollX = hScroll.Value; };
        vScroll.Scroll += (_, _) => { if (!syncing) canvas.ScrollY = vScroll.Value; };
        canvas.ViewChanged += SyncScrollBars;
        canvas.Hover += t => hoverText.Text = t;
        ToolTip.SetTip(canvas, Loc.S(
            "Cada columna es una curva de ecualización: brillo = ganancia de la banda de esa fila (negro = silencio, blanco = sin cambio). Rojo = solo canal izquierdo, verde = solo derecho. Izquierdo = herramienta · derecho = borrar · rueda = altura, Ctrl+rueda = zoom · suelta un WAV o una imagen aquí",
            "Every column is an EQ curve: brightness = gain of that row's band (black = silence, white = unchanged). Red = left channel only, green = right only. Left = tool · right = erase · wheel = pitch, Ctrl+wheel = zoom · drop a WAV or a picture here"));
        // A long delay so the explanatory tooltip does not pop up over the canvas while painting.
        ToolTip.SetShowDelay(canvas, 2000);
        // Drops are accepted anywhere in the room (WAV → source, picture → filter).
        DragDrop.SetAllowDrop(root, true);
        root.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        root.AddHandler(DragDrop.DropEvent, OnDrop);

        var status = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(12, 6, 12, 8) };
        status.Children.Add(hoverText);
        Grid.SetColumn(messageText, 1);
        Grid.SetColumn(dspText, 2);
        status.Children.Add(messageText);
        status.Children.Add(dspText);
        ToolTip.SetTip(dspText, Loc.S("Carga de DSP de Csound (motor Windows·WASAPI de la app) mientras suena.", "Csound DSP load (the app's Windows·WASAPI engine) while playing."));

        // DockPanel: the docked children first (top rows, bottom status, left palette); the last
        // child (the surface) fills the remaining space.
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
        canvasSession.ImageReplaced += OnImageReplaced;
        canvasSession.PixelsChanged += OnPixelsChanged;
        shared.SoundChanged += OnSharedSoundChanged;
        player.StateChanged += UpdatePlayButton;
        player.Message += Say;
        // Do not stop the «Paint Rooms host» under Paint Synth's feet.
        player.HostInUseByOthers = () => shared.Player?.State is PaintPlayState.Playing or PaintPlayState.Starting;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => OnTick());
        timer.Start();
        SyncFromSettings();
        ShowSource();
        UpdatePlayButton();
        // Reload the last session's WAV in the background (quietly) when the file still exists.
        if (prefs.Source == FilterSourceKind.File && state.File is null && prefs.SourcePath is { } last && File.Exists(last))
            _ = LoadWavAsync(last, quiet: true);
        Say(Loc.S("Elige un sonido, pinta (o elige un ejemplo) y presiona Tocar (Espacio). Brillo = ganancia; rojo/verde = izquierda/derecha.",
                  "Choose a sound, paint (or pick an example) and press Play (Space). Brightness = gain; red/green = left/right."), false);
    }

    /// <summary>Creates the per-session state: the filter canvas (starting with the «slow sweep» example) and the player.</summary>
    /// <param name="context">Window context (its <c>App</c> is the engine host the player uses).</param>
    /// <param name="prefs">Room settings (the canvas settings give the axis, size and duration).</param>
    /// <returns>The new state, stored in <see cref="States"/> by the caller.</returns>
    /// <remarks>Called once per shared session, i.e. once per app run in practice.</remarks>
    static RoomState CreateState(PaintRoomContext context, ImageFilterPrefs prefs)
    {
        var canvas = new PaintSession(prefs.Canvas);
        if (canvas.Image.IsSilent())
        {
            // First use: a picture that does something audible right away.
            var img = new SpectralImage(canvas.Image.Width, canvas.Image.Axis, canvas.Image.DurationSeconds);
            ImageFilterPresets.All[0].Draw(img);
            canvas.ReplaceImage(img, "preset", undoable: false);
            canvas.ImageName = "Slow_sweep";
            canvas.MarkPictureSaved();   // the starter preset is not the user's work
        }
        return new RoomState { Canvas = canvas, Player = new ImageFilterPlayer(context.App) };
    }

    // ------------------------------------------------------------------------------------------
    //  IPaintRoom
    // ------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public string Title => Loc.S("Filtro de imagen", "Image Filter");

    /// <inheritdoc/>
    /// <remarks>The filter's own canvas session (its picture and history).</remarks>
    public PaintSession EditSession => canvasSession;

    /// <inheritdoc/>
    public string Tip => Loc.S("Una imagen como ecualizador estéreo que cambia en el tiempo: filtra un archivo, el sonido de otra sala o tu micrófono en vivo.",
                               "A picture as a time-varying stereo equaliser: filter a file, another room's sound or your microphone live.");

    /// <inheritdoc/>
    public Control Content => root;

    /// <summary>The filter canvas (tests drive it).</summary>
    public PaintCanvas Canvas => canvas;

    /// <summary>The room's own canvas session (tests).</summary>
    public PaintSession CanvasSession => canvasSession;

    /// <summary>The live player (tests).</summary>
    public ImageFilterPlayer Player => player;

    /// <inheritdoc/>
    /// <remarks>Re-reads the settings and the source (another room may have changed the shared sound or the engine's rate meanwhile).</remarks>
    public void OnActivated()
    {
        SyncFromSettings();
        ShowSource();
        canvas.Focus();
    }

    /// <inheritdoc/>
    /// <remarks>Nothing to do: the filter keeps playing while another tab is shown.</remarks>
    public void OnDeactivated() { }

    /// <inheritdoc/>
    /// <remarks>
    /// Space = play/stop, Ctrl+Z/Y = undo/redo, Ctrl+C/X/V/A = clipboard and select all (paste goes
    /// to the yellow cursor column), Del/Backspace, Esc, Home, [ and ] = brush size ÷/× 1.25,
    /// Ctrl+0 = zoom to fit; plain letters pick tools (same keys as Paint Synth). Ignored while a
    /// text box has the focus, so typing numbers does not trigger shortcuts.
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
            case Key.Z when ctrl && shift: canvasSession.Redo(); return true;   // Ctrl+Shift+Z = redo, like Ctrl+Y
            case Key.Z when ctrl: canvasSession.Undo(); return true;
            case Key.Y when ctrl: canvasSession.Redo(); return true;
            case Key.C when ctrl: canvasSession.Copy(); Say(Loc.S("Copiado.", "Copied."), false); return true;
            case Key.X when ctrl: canvasSession.Copy(); canvasSession.DeleteSelection(); return true;
            case Key.V when ctrl: canvasSession.Paste((int)Math.Round(canvas.CursorColumn), canvasSession.Selection?.Bounds.Y0 ?? 0); return true;
            case Key.A when ctrl: canvasSession.ChangeSelection(CellRect.All(canvasSession.Image)); return true;
            case Key.Delete or Key.Back: canvasSession.DeleteSelection(); return true;
            case Key.Escape: canvasSession.ChangeSelection(null); return true;
            case Key.Home: canvas.CursorColumn = 0; return true;
            case Key.OemOpenBrackets: sizeSlider.Value = Math.Max(sizeSlider.Minimum, sizeSlider.Value / 1.25); return true;
            case Key.OemCloseBrackets: sizeSlider.Value = Math.Min(sizeSlider.Maximum, sizeSlider.Value * 1.25); return true;
            case Key.D0 when ctrl: canvas.ZoomToFit(); return true;
        }
        if (ctrl || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return false;
        PaintTool? tool = e.Key switch
        {
            Key.P => PaintTool.Pencil, Key.A => PaintTool.Airbrush, Key.L => PaintTool.Line, Key.R => PaintTool.Rectangle,
            Key.O => PaintTool.Ellipse, Key.G => PaintTool.Gradient, Key.E => PaintTool.Eraser, Key.B => PaintTool.Blur,
            Key.U => PaintTool.Smudge,
            // M = rectangle, Shift+M = ellipse, F = lasso, N = spline selection.
            _ => PaintSelectionUi.ToolForKey(e.Key, shift),
        };
        if (tool is { } t) { SelectTool(t); return true; }
        return false;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Called when the Paint Rooms window closes: stops the timer, any render and the filter, and
    /// unhooks every event so the long-lived session/player do not keep this room alive. The canvas
    /// session and player themselves survive in <see cref="States"/>.
    /// </remarks>
    public void Dispose()
    {
        timer.Stop();
        renderCancel?.Cancel();
        player.Stop();
        canvasSession.ImageReplaced -= OnImageReplaced;
        canvasSession.PixelsChanged -= OnPixelsChanged;
        shared.SoundChanged -= OnSharedSoundChanged;
        player.StateChanged -= UpdatePlayButton;
        player.Message -= Say;
        player.HostInUseByOthers = null;
        canvas.Session = null;
    }

    // ------------------------------------------------------------------------------------------
    //  Palette
    // ------------------------------------------------------------------------------------------

    /// <summary>Builds the left column: 13 tool buttons (with the four selection tools), the brush sliders, the pan buttons and the selection options (feather).</summary>
    /// <returns>A vertically scrolling column (so it still fits on short windows).</returns>
    Control BuildPalette()
    {
        // (tool, glyph on the button, Spanish tip, English tip, keyboard shortcut). Paint Synth's
        // Harmonics and Spray tools are left out: they make no sense for an EQ picture.
        (PaintTool Tool, string Glyph, string Es, string En, string Key)[] tools =
        [
            (PaintTool.Pencil, "✎", "Lápiz: borde duro. Una línea horizontal deja pasar una banda angosta.", "Pencil: hard edge. A horizontal line lets one narrow band through.", "P"),
            (PaintTool.Airbrush, "◍", "Aerógrafo: pincel suave para bandas anchas y aperturas graduales.", "Airbrush: soft brush for wide bands and gradual openings.", "A"),
            (PaintTool.Line, "╱", "Línea: en diagonal = barrido de filtro.", "Line: diagonal = filter sweep.", "L"),
            (PaintTool.Rectangle, "▭", "Rectángulo: deja pasar un rango de frecuencias durante un tiempo (un «paso» de compuerta).", "Rectangle: lets a frequency range through for a while (a gate «step»).", "R"),
            (PaintTool.Ellipse, "◯", "Elipse: un bloque que se abre y se cierra suavemente.", "Ellipse: a block that opens and closes smoothly.", "O"),
            (PaintTool.Gradient, "◩", "Degradado: vertical = inclinación espectral (más grave o más brillante); horizontal = fundido.", "Gradient: vertical = spectral tilt (darker or brighter); horizontal = fade.", "G"),
            (PaintTool.Eraser, "⌫", "Goma: corta lo que toca (también: botón derecho). Sobre un lienzo blanco, la goma «talla» muescas.", "Eraser: cuts what it touches (also: right button). On a white canvas the eraser «carves» notches.", "E"),
            (PaintTool.Blur, "≈", "Desenfocar: suaviza bordes (filtros más suaves, sin chasquidos).", "Blur: softens edges (gentler filters, no clicks).", "B"),
            (PaintTool.Smudge, "☁", "Difuminar: arrastra la imagen con el pincel.", "Smudge: drags the picture along with the brush.", "U"),
            (PaintTool.Select, "⬚", "Selección rectangular: arrastra un rectángulo; Ctrl+C/X/V, Supr. «Selección ▾» (bajo las herramientas) se aplica a la selección." + PaintSelectionUi.ModifierHint, "Rectangle selection: drag a rectangle; Ctrl+C/X/V, Del. «Selection ▾» (under the tools) applies to the selection." + PaintSelectionUi.ModifierHint, "M"),
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
            // Automation name = the enum name, so UI tests can find the button language-independently.
            Avalonia.Automation.AutomationProperties.SetName(b, t.Tool.ToString());
            var tool = t.Tool;
            b.Click += (_, _) => SelectTool(tool);
            toolButtons[tool] = b;
            grid.Children.Add(b);
        }
        sizeSlider.Value = canvasPrefs.BrushSize;
        levelSlider.Value = canvasPrefs.Opacity;
        hardSlider.Value = canvasPrefs.Hardness;
        panSlider.Value = canvasPrefs.Pan;
        ToolTip.SetTip(sizeSlider, Loc.S("Tamaño del pincel en píxeles de pantalla (también [ y ]).", "Brush size in screen pixels (also [ and ])."));
        ToolTip.SetTip(levelSlider, Loc.S("Brillo de la pintura = ganancia de la banda (1 = sin cambio, o el refuerzo de «Filtro ▾»).", "Paint brightness = band gain (1 = unchanged, or the «Filter ▾» boost)."));
        ToolTip.SetTip(hardSlider, Loc.S("Dureza del borde: 1 = nítido (filtro abrupto), 0 = muy suave.", "Edge hardness: 1 = crisp (steep filter), 0 = very soft."));
        ToolTip.SetTip(panSlider, Loc.S("−1 = solo el canal izquierdo (rojo), 0 = ambos, +1 = solo el derecho (verde).", "−1 = left channel only (red), 0 = both, +1 = right only (green)."));
        // Every brush slider also shows the brush ON the canvas for a moment (real size, hardness,
        // level, pan colour): the pointer is on the slider, so the usual circle under the mouse
        // would not be visible.
        foreach (var s in new[] { sizeSlider, levelSlider, hardSlider, panSlider })
            s.ValueChanged += (_, _) => { OnBrushChanged(); canvas.ShowParameterPreview(PaintCanvas.ParameterPreview.Brush); };
        var panButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (label, v, es, en) in new[] { ("L", -1.0, "Solo izquierda", "Left only"), ("C", 0.0, "Centro (ambos)", "Centre (both)"), ("R", 1.0, "Solo derecha", "Right only") })
        {
            var b = new Button { Content = label, Width = 44, Padding = new Thickness(0, 3), HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(b, Loc.S(es, en));
            b.Click += (_, _) => panSlider.Value = v;
            panButtons.Children.Add(b);
        }
        var legend = new TextBlock
        {
            Text = Loc.S("Negro = silencio\nBlanco = sin cambio\nRojo = solo izquierda\nVerde = solo derecha", "Black = silence\nWhite = unchanged\nRed = left only\nGreen = right only"),
            FontSize = 11, Foreground = AppTheme.Brush("TextDim"), Margin = new Thickness(2, 10, 0, 0), TextWrapping = TextWrapping.Wrap,
        };
        var stack = new StackPanel { Spacing = 3, Margin = new Thickness(10, 4, 10, 0), Width = 160 };
        stack.Children.Add(Caption(Loc.S("HERRAMIENTAS", "TOOLS")));
        stack.Children.Add(grid);
        // Selection commands + image operations, right under the tools they belong to.
        stack.Children.Add(PaintSelectionUi.SelectionMenuButton(canvasSession, BuildImageMenu));
        stack.Children.Add(Caption(Loc.S("PINCEL", "BRUSH")));
        stack.Children.Add(Labeled(Loc.S("Tamaño", "Size"), sizeText, sizeSlider));
        stack.Children.Add(Labeled(Loc.S("Brillo", "Level"), levelText, levelSlider));
        stack.Children.Add(Labeled(Loc.S("Dureza", "Hardness"), hardText, hardSlider));
        stack.Children.Add(Labeled(Loc.S("Panorama", "Pan"), panText, panSlider));
        stack.Children.Add(panButtons);
        stack.Children.Add(legend);
        selectionPanel = PaintSelectionUi.OptionsPanel(canvas, canvasPrefs);
        stack.Children.Add(selectionPanel);
        OnBrushChanged();
        // A tool this palette does not offer (stored by an older version) falls back to the airbrush.
        SelectTool(canvasPrefs.Tool is PaintTool.Harmonics or PaintTool.Spray ? PaintTool.Airbrush : canvasPrefs.Tool);
        return new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    /// <summary>Makes <paramref name="tool"/> current (canvas, buttons, preferences).</summary>
    /// <param name="tool">The tool to select.</param>
    void SelectTool(PaintTool tool)
    {
        canvasPrefs.Tool = tool;
        canvas.Tool = tool;
        foreach (var (t, b) in toolButtons) b.IsChecked = t == tool;
        if (selectionPanel is not null) selectionPanel.IsVisible = PaintCanvas.IsSelectionTool(tool);
        canvas.InvalidateVisual();
    }

    /// <summary>Copies the brush controls into the preferences and the canvas, and refreshes the captions.</summary>
    void OnBrushChanged()
    {
        canvasPrefs.BrushSize = sizeSlider.Value;
        canvasPrefs.Opacity = levelSlider.Value;
        canvasPrefs.Hardness = hardSlider.Value;
        canvasPrefs.Pan = panSlider.Value;
        canvas.Brush = new BrushSettings { Size = canvasPrefs.BrushSize, Opacity = canvasPrefs.Opacity, Hardness = canvasPrefs.Hardness, Pan = canvasPrefs.Pan };
        sizeText.Text = $"{canvasPrefs.BrushSize:0} px";
        // Show the brush level also as the gain it produces under the current brightness → gain rule.
        double g = prefs.Settings.LevelToGain(canvasPrefs.Opacity);
        levelText.Text = $"{canvasPrefs.Opacity * 100:0} % · {(g > 0 ? 20 * Math.Log10(g) : double.NegativeInfinity):+0;-0;0} dB";
        hardText.Text = $"{canvasPrefs.Hardness * 100:0} %";
        panText.Text = canvasPrefs.Pan < -0.02 ? Loc.S($"{-canvasPrefs.Pan * 100:0} % izq.", $"{-canvasPrefs.Pan * 100:0} % L")
                     : canvasPrefs.Pan > 0.02 ? Loc.S($"{canvasPrefs.Pan * 100:0} % der.", $"{canvasPrefs.Pan * 100:0} % R") : Loc.S("centro", "centre");
        canvas.InvalidateVisual();
    }

    // ------------------------------------------------------------------------------------------
    //  Settings ↔ controls
    // ------------------------------------------------------------------------------------------

    /// <summary>Shows the filter settings in the controls (without triggering their handlers) and refreshes everything derived from them.</summary>
    void SyncFromSettings()
    {
        syncing = true;
        var s = prefs.Settings;
        sourceBox.SelectedIndex = (int)prefs.Source;
        loopToggle.IsChecked = s.LoopSource;
        mixBox.Value = (decimal)Math.Round(s.Mix * 100);
        outBox.Value = (decimal)s.OutputDb;
        timeBox.SelectedIndex = (int)s.TimeMode;
        secondsBox.Value = (decimal)s.LoopSeconds;
        barsBox.Value = (decimal)s.Bars;
        bpmBox.Value = (decimal)s.Bpm;
        speedBox.Value = (decimal)s.Speed;
        fftBox.SelectedIndex = Array.IndexOf(ImageFilterSettings.FftSizes, s.FftSize);
        overlapBox.SelectedIndex = Array.IndexOf(ImageFilterSettings.Overlaps, s.Overlap);
        smoothBox.Value = (decimal)s.SmoothingMs;
        glideToggle.IsChecked = s.Glide;
        secondsPanel.IsVisible = s.TimeMode == FilterTimeMode.Seconds;
        barsPanel.IsVisible = bpmPanel.IsVisible = s.TimeMode == FilterTimeMode.Bars;
        // Latency in ms needs a sample rate: the running engine's, else the source's, else 48 kHz.
        double sr = ctx.App.Engine.SampleRate > 0 ? ctx.App.Engine.SampleRate : CurrentSource()?.SampleRate is > 0 and var r ? r : 48000;
        latencyText.Text = Loc.S($"latencia {s.LatencyMs(sr):0} ms", $"latency {s.LatencyMs(sr):0} ms");
        syncing = false;
        SyncDuration();
        OnBrushChanged();
    }

    /// <summary>Stores new filter settings; the live player follows (live, re-upload or restart) and the controls are refreshed.</summary>
    /// <param name="s">The new settings (sanitised; an unchanged value is ignored, which also stops feedback loops from the controls).</param>
    void SetFilter(ImageFilterSettings s)
    {
        s = s.Sanitized();
        if (s == prefs.Settings) return;
        // Every filter setting is undoable (coalesced per burst); the swap comes back through here,
        // and the session ignores the push while it is applying an undo/redo.
        var before = prefs.Settings;
        canvasSession.PushState("filter", before, st => { var cur = prefs.Settings; SetFilter((ImageFilterSettings)st!); return cur; });
        prefs.Settings = s;
        player.UpdateSettings(s);
        SyncFromSettings();
    }

    /// <summary>
    /// Makes the canvas's duration equal to one pass over the picture (so the time ruler shows real
    /// seconds): the sound's length in «fit» mode, the loop length otherwise, divided by the speed.
    /// </summary>
    void SyncDuration()
    {
        var src = CurrentSource();
        double pass = prefs.Settings.PassSeconds(src is { LiveInput: false } ? src.Seconds : 0);
        if (Math.Abs(canvasSession.Image.DurationSeconds - pass) > 1e-6)
        {
            canvasSession.Image.DurationSeconds = pass;
            canvasPrefs.DurationSeconds = canvasSession.Image.DurationSeconds;
            canvas.InvalidateVisual();
        }
    }

    /// <summary>A new canvas image object (example, import, undo of those): keep the duration in sync and keep playing it.</summary>
    /// <remarks>The player's tables belong to the old image object, so a playing filter must restart on the new one.</remarks>
    void OnImageReplaced()
    {
        SyncDuration();
        player.Restart(canvasSession.Image);
    }

    /// <summary>Pixels changed: the live player re-uploads those gain columns at its next tick.</summary>
    /// <param name="x0">First changed column (inclusive).</param>
    /// <param name="x1">End of the changed columns (exclusive).</param>
    void OnPixelsChanged(int x0, int x1) => player.OnImageEdited(canvasSession.Image, x0, x1);

    /// <summary>Matches the scroll bars to the canvas zoom and scroll position.</summary>
    /// <remarks>Units are canvas cells (columns / rows); <c>syncing</c> stops the bars' Scroll handlers echoing the values back.</remarks>
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
    //  Source
    // ------------------------------------------------------------------------------------------

    /// <summary>The source chosen in the combo, prepared for Csound, or <c>null</c> when there is none yet.</summary>
    /// <returns>The live source, the (cached) prepared shared sound, or the opened file.</returns>
    /// <remarks>
    /// The shared sound is prepared lazily and cached in <see cref="RoomState.Shared"/>; a new
    /// <see cref="AudioData"/> object from another room (reference comparison) triggers a new copy.
    /// That preparation writes a temporary WAV synchronously on the UI thread.
    /// </remarks>
    FilterSource? CurrentSource()
    {
        switch (prefs.Source)
        {
            case FilterSourceKind.LiveInput:
                return FilterSource.Live;
            case FilterSourceKind.Shared:
                if (shared.Sound is not { Frames: > 0 } sound) return null;
                if (!ReferenceEquals(sound, state.SharedFrom) || state.Shared is null)
                {
                    state.Shared = FilterSource.Prepare(sound, Loc.S("sonido de otra sala", "other room's sound"));
                    state.SharedFrom = sound;
                }
                return state.Shared;
            default:
                return state.File;
        }
    }

    /// <summary>The source kind changed (combo or a drop): refresh the read-outs and keep playing with the new source.</summary>
    void OnSourceChanged()
    {
        ShowSource();
        SyncDuration();
        SyncFromSettings();
        if (player.State == PaintPlayState.Playing)
        {
            if (CurrentSource() is { } src) player.Restart(newSource: src);
            else player.Stop();
        }
    }

    /// <summary>Shows the source name/length, the waveform, and which buttons apply.</summary>
    /// <remarks>Loop and Render are disabled for the live input (it has no end and cannot be rendered offline).</remarks>
    void ShowSource()
    {
        var src = CurrentSource();
        openButton.IsVisible = prefs.Source == FilterSourceKind.File;
        loopToggle.IsEnabled = prefs.Source != FilterSourceKind.LiveInput;
        renderButton.IsEnabled = prefs.Source != FilterSourceKind.LiveInput;
        waveform.Audio = src?.Audio;
        waveform.Caption = prefs.Source switch
        {
            FilterSourceKind.LiveInput => ctx.App.Engine.HasLiveInput || player.State != PaintPlayState.Idle
                ? Loc.S("🎤 Entrada en vivo — usa audífonos para evitar acoples", "🎤 Live input — use headphones to avoid feedback")
                : Loc.S("🎤 Entrada en vivo — elige el dispositivo en Configuración › Audio › Entrada; usa audífonos", "🎤 Live input — choose the device in Settings › Audio › Input; use headphones"),
            FilterSourceKind.Shared when src is null => Loc.S("Ninguna sala ha compartido un sonido todavía (p. ej. Spectrum Room › enviar al Filtro).", "No room has shared a sound yet (e.g. Spectrum Room › send to the filter)."),
            FilterSourceKind.File when src is null => Loc.S("Abre o arrastra aquí un archivo WAV para filtrarlo.", "Open or drag a WAV file here to filter it."),
            _ => null,
        };
        sourceText.Text = src is null ? "" : src.LiveInput ? "" : $"{src.Name} · {src.Seconds:0.0} s · {src.SampleRate} Hz";
        ToolTip.SetTip(sourceText, prefs.SourcePath);
    }

    /// <summary>Another room put a new sound in the shared session: use it when the source is «other room's sound».</summary>
    void OnSharedSoundChanged()
    {
        if (prefs.Source != FilterSourceKind.Shared) return;
        // Drop the cached copy so CurrentSource prepares the new sound (and a playing filter restarts on it).
        state.Shared = null;
        OnSourceChanged();
        Say(Loc.S("Llegó un sonido nuevo de otra sala.", "A new sound arrived from another room."), false);
    }

    /// <summary>Open WAV…: picker filtered to WAV.</summary>
    /// <returns>A task that completes when the picker closed and the file (if any) is loaded.</returns>
    async Task OpenWavAsync()
    {
        var files = await ctx.Window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.S("Sonido a filtrar", "Sound to filter"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("WAV") { Patterns = ["*.wav"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            await LoadWavAsync(path);
    }

    /// <summary>
    /// Reads a WAV and prepares its stereo copy for diskin2 on a worker thread, then makes it the
    /// source (switching the combo to «Sound file»).
    /// </summary>
    /// <param name="path">WAV file (any channel count; mono is duplicated to both sides).</param>
    /// <param name="quiet">True when restoring the last session's file at start-up: no status messages on success.</param>
    /// <returns>A task that completes when the source is ready (errors are reported in the status line, not thrown).</returns>
    /// <remarks>Public so the window's drag-and-drop and tests can load files. The path is remembered in the settings.</remarks>
    public async Task LoadWavAsync(string path, bool quiet = false)
    {
        try
        {
            if (!quiet) Say(Loc.S("Leyendo ", "Reading ") + Path.GetFileName(path) + "…", false);
            var src = await Task.Run(() => FilterSource.Prepare(WavIO.Read(path), Path.GetFileName(path)));
            state.File = src;
            prefs.SourcePath = path;
            prefs.Source = FilterSourceKind.File;
            OnSourceChanged();
            if (!quiet)
                Say(Loc.S($"Fuente: {src.Name} ({src.Seconds:0.0} s). Presiona Tocar.", $"Source: {src.Name} ({src.Seconds:0.0} s). Press Play."), false);
        }
        catch (Exception ex)
        {
            Say(Loc.S("No se pudo leer el WAV: ", "Could not read the WAV: ") + ex.Message, true);
        }
    }

    // ------------------------------------------------------------------------------------------
    //  Transport
    // ------------------------------------------------------------------------------------------

    /// <summary>Play/stop (button and Space): filters from the yellow cursor (from the start when the cursor is at the end).</summary>
    void TogglePlay()
    {
        if (player.State != PaintPlayState.Idle)
        {
            player.Stop();
            Say(Loc.S("■ Detenido.", "■ Stopped."), false);
            return;
        }
        if (CurrentSource() is not { } src)
        {
            Say(prefs.Source == FilterSourceKind.Shared
                ? Loc.S("Ninguna sala ha compartido un sonido: elige «Archivo de sonido» o «Entrada en vivo».", "No room has shared a sound: choose «Sound file» or «Live input».")
                : Loc.S("Primero abre un WAV («Abrir WAV…» o arrástralo a la ventana).", "Open a WAV first («Open WAV…» or drag it onto the window)."), true);
            return;
        }
        var img = canvasSession.Image;
        // A cursor left on the last column (e.g. after a one-shot play) restarts from the beginning.
        player.Play(img, prefs.Settings, src, canvas.CursorColumn >= img.Width - 1 ? 0 : canvas.CursorColumn);
    }

    /// <summary>Shows Play or Stop on the transport button according to the player state.</summary>
    void UpdatePlayButton()
    {
        bool on = player.State is PaintPlayState.Playing or PaintPlayState.Starting;
        playButton.Content = ToolUi.Content(on ? Loc.S("Detener", "Stop") : Loc.S("Tocar", "Play"), on ? "IconStop" : "IconPlay");
        if (!on)
        {
            canvas.PlayColumn = null;
            waveform.Position = null;
        }
        ShowSource();
    }

    /// <summary>~30 Hz: drives the player, moves the play line and the waveform cursor, shows the DSP load.</summary>
    /// <remarks>
    /// Runs at <see cref="DispatcherPriority.Render"/> so the play line keeps moving while the
    /// user paints. DSP load = Csound compute time ÷ available time per buffer; dropouts are the
    /// engine's underrun count.
    /// </remarks>
    void OnTick()
    {
        player.Tick();
        canvas.PlayColumn = player.CurrentColumn;
        waveform.Position = player.SourcePosition;
        var engine = ctx.App.Engine;
        dspText.Text = player.State == PaintPlayState.Playing && engine.DspLoad >= 0
            ? $"DSP {engine.DspLoad * 100:0} %" + (engine.Underruns > 0 ? Loc.S($" · {engine.Underruns} cortes", $" · {engine.Underruns} dropouts") : "")
            : "";
    }

    /// <summary>Render WAV: asks for the file, renders through Csound with progress (a second click cancels).</summary>
    /// <returns>A task that completes when the render finished, failed or was cancelled (all reported in the status line).</returns>
    /// <remarks>
    /// The render uses a private Csound instance (<see cref="ImageFilterRenderer"/>), so the live
    /// filter or the main document can keep playing meanwhile. Live input cannot be rendered.
    /// </remarks>
    async Task RenderAsync()
    {
        if (rendering)
        {
            renderCancel?.Cancel();
            return;
        }
        if (CurrentSource() is not { Audio: { } audio } src)
        {
            Say(prefs.Source == FilterSourceKind.LiveInput
                ? Loc.S("La entrada en vivo no se puede renderizar: grábala primero (Grabar en la ventana principal) y ábrela como archivo.", "The live input cannot be rendered: record it first (Record in the main window) and open it as a file.")
                : Loc.S("Primero elige un sonido.", "Choose a sound first."), true);
            return;
        }
        if (ctx.App.PickSaveFile is not { } pick) return;
        // Suggested name: «<source>_filtered.wav», with characters Windows forbids replaced by «_».
        string stem = Path.GetFileNameWithoutExtension(src.Name);
        string name = string.Concat((stem + "_filtered").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (await pick(name + ".wav", "wav") is not { } path) return;
        rendering = true;
        renderCancel = new CancellationTokenSource();
        renderButton.Content = ToolUi.Content(Loc.S("Cancelar render", "Cancel render"), "IconStop");
        // Progress<T> captures the UI SynchronizationContext, so the callback runs on the UI thread.
        var progress = new Progress<double>(p => Say(Loc.S($"Filtrando con Csound… {p * 100:0} %", $"Filtering with Csound… {p * 100:0} %"), false));
        try
        {
            var r = await ImageFilterRenderer.RenderAsync(canvasSession.Image, prefs.Settings, audio, path, progress, renderCancel.Token);
            if (r.Success)
            {
                string msg = Loc.S($"✓ WAV guardado: {path} ({r.Seconds:0.0} s, Csound tardó {r.RenderSeconds:0.0} s, latencia compensada {r.LatencySamples} muestras, ganancia {r.AppliedGainDb:+0.0;-0.0} dB)",
                                   $"✓ WAV saved: {path} ({r.Seconds:0.0} s, Csound took {r.RenderSeconds:0.0} s, latency compensated {r.LatencySamples} samples, gain {r.AppliedGainDb:+0.0;-0.0} dB)");
                Say(msg, false);
                ctx.App.PaintInfo("Paint Rooms · Image Filter: " + msg);
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

    /// <summary>Show Csound code: writes the standalone .csd (with the source, or for the live input) and opens it in an editor tab.</summary>
    /// <remarks>Synchronous: the gain tables and the source copy are written on the UI thread (a short pause for long sounds).</remarks>
    void ShowCode()
    {
        try
        {
            var src = CurrentSource();
            if (src is null)
            {
                Say(Loc.S("Primero elige un sonido (o la entrada en vivo).", "Choose a sound (or the live input) first."), true);
                return;
            }
            string csd = ImageFilterRenderer.ExportCode(canvasSession.Image, prefs.Settings, src.LiveInput ? null : src.Audio, canvasSession.ImageName);
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

    /// <summary>Filter menu: level → gain rule, decibel range, boost, render normalisation.</summary>
    /// <param name="menu">A fresh flyout to fill (see <see cref="MenuButton"/>); ● / ○ / ☑ / ☐ prefixes show the current choices.</param>
    void BuildFilterMenu(MenuFlyout menu)
    {
        var s = prefs.Settings;
        menu.Items.Add(Item((s.Response == FilterResponse.Linear ? "● " : "○ ") + Loc.S("Brillo = ganancia lineal (gris 50 % = −6 dB)", "Brightness = linear gain (50 % grey = −6 dB)"),
            () => SetFilter(prefs.Settings with { Response = FilterResponse.Linear })));
        menu.Items.Add(Item((s.Response == FilterResponse.Decibels ? "● " : "○ ") + Loc.S($"Brillo en decibelios (rango {s.RangeDb:0} dB)", $"Brightness in decibels (range {s.RangeDb:0} dB)"),
            () => SetFilter(prefs.Settings with { Response = FilterResponse.Decibels })));
        menu.Items.Add(Item(Loc.S($"Rango en decibelios: {s.RangeDb:0} dB…", $"Decibel range: {s.RangeDb:0} dB…"), () => _ = AskAsync(
            Loc.S("Rango (dB): gris 50 % = refuerzo − rango/2", "Range (dB): 50 % grey = boost − range/2"), 6, 96, s.RangeDb, 6, v => prefs.Settings with { RangeDb = v, Response = FilterResponse.Decibels })));
        var boost = Item(Loc.S($"Refuerzo del blanco: {s.BoostDb:+0.#;-0.#;0} dB…", $"White boost: {s.BoostDb:+0.#;-0.#;0} dB…"), () => _ = AskAsync(
            Loc.S("Ganancia de un píxel blanco (0…+12 dB)", "Gain of a white pixel (0…+12 dB)"), 0, 12, s.BoostDb, 1, v => prefs.Settings with { BoostDb = v }));
        ToolTip.SetTip(boost, Loc.S("0 dB = el blanco deja la banda igual (el filtro solo corta). Más = las zonas brillantes refuerzan sus bandas.", "0 dB = white leaves the band unchanged (the filter only cuts). More = bright areas boost their bands."));
        menu.Items.Add(boost);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item((s.NormalizeRender ? "☑ " : "☐ ") + Loc.S("Normalizar el render a −1 dBTP (pico real)", "Normalise renders to −1 dBTP (true peak)"), () => SetFilter(prefs.Settings with { NormalizeRender = !prefs.Settings.NormalizeRender })));
    }

    /// <summary>Canvas menu: white/black canvas, import/export, exchange with Paint Synth, pitch axis, colour mapping.</summary>
    /// <param name="menu">A fresh flyout to fill.</param>
    /// <remarks>
    /// Pictures exchanged with Paint Synth are CLONED, so the two canvases never share pixel arrays;
    /// each copy is an undoable replacement in the receiving session. A picture sent to Paint Synth
    /// takes Paint Synth's duration (the filter's duration is one pass, which means something else).
    /// Changing the axis resamples the picture (<c>Retune</c>) and stores the axis in the canvas settings.
    /// </remarks>
    void BuildCanvasMenu(MenuFlyout menu)
    {
        menu.Items.Add(Item(Loc.S("Lienzo blanco (todo pasa)", "White canvas (everything passes)"), () => NewCanvas(1)));
        menu.Items.Add(Item(Loc.S("Lienzo negro (nada pasa)", "Black canvas (nothing passes)"), () => NewCanvas(0)));
        menu.Items.Add(Item(Loc.S("Importar imagen…", "Import picture…"), () => _ = ImportPictureAsync()));
        menu.Items.Add(Item(Loc.S("Exportar PNG…", "Export PNG…"), () => _ = ExportPngAsync()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Loc.S("Traer la imagen de Paint Synth", "Use the Paint Synth picture"), () =>
        {
            var img = shared.Image.Clone();
            canvasSession.ReplaceImage(img, "from-paint-synth");
            canvasSession.ImageName = shared.ImageName;
            Say(Loc.S("Imagen de Paint Synth copiada al filtro (Ctrl+Z deshace).", "Paint Synth picture copied into the filter (Ctrl+Z undoes)."), false);
        }));
        menu.Items.Add(Item(Loc.S("Enviar esta imagen a Paint Synth", "Send this picture to Paint Synth"), () =>
        {
            var img = canvasSession.Image.Clone();
            img.DurationSeconds = shared.Image.DurationSeconds;
            shared.ReplaceImage(img, "from-image-filter");
            shared.ImageName = canvasSession.ImageName;
            Say(Loc.S("Imagen enviada a Paint Synth (allí, Ctrl+Z deshace).", "Picture sent to Paint Synth (Ctrl+Z there undoes)."), false);
        }));
        menu.Items.Add(new Separator());
        var axis = new MenuItem { Header = Loc.S("Eje de alturas", "Pitch axis") };
        (string Es, string En, Func<PitchAxis> Make)[] axes =
        [
            ("Logarítmico 30 Hz – 18 kHz (recomendado)", "Logarithmic 30 Hz – 18 kHz (recommended)", () => PitchAxis.Logarithmic(30, 18000, 384)),
            ("Logarítmico fino 20 Hz – 20 kHz, 768 filas", "Fine logarithmic 20 Hz – 20 kHz, 768 rows", () => PitchAxis.Logarithmic(20, 20000, 768)),
            ("Lineal 0 – 20 kHz (como un espectro)", "Linear 0 – 20 kHz (like a spectrum)", () => PitchAxis.Linear(20, 20000, 512)),
            ("Temperamento igual, 12 notas (una fila por semitono)", "Equal temperament, 12 notes (one row per semitone)", () => PitchAxis.EqualTemperament(30, 18000, 12)),
            ("El mismo eje que Paint Synth", "The same axis as Paint Synth", () => shared.Image.Axis),
        ];
        foreach (var a in axes)
        {
            var make = a.Make;
            axis.Items.Add(Item(Loc.S(a.Es, a.En), () =>
            {
                var ax = make();
                if (ax.Equals(canvasSession.Image.Axis)) return;
                canvasSession.Retune(ax);
                canvasPrefs.AxisKind = ax.Kind;
                canvasPrefs.LowHz = ax.LowHz;
                canvasPrefs.HighHz = ax.HighHz;
                // A discrete (equal-tempered/Scala) axis derives its row count from the range, so only continuous axes store Rows.
                if (!ax.IsDiscrete) canvasPrefs.Rows = ax.RowCount;
                Say(Loc.S("Eje: ", "Axis: ") + ax.Describe(), false);
            }));
        }
        menu.Items.Add(axis);
        var mapping = new MenuItem { Header = Loc.S("Al importar, los colores son…", "When importing, colours mean…") };
        (ColorMapping M, string Es, string En)[] maps =
        [
            (ColorMapping.Brightness, "Brillo → ambos canales", "Brightness → both channels"),
            (ColorMapping.RedGreen, "Rojo → izquierda, verde → derecha", "Red → left, green → right"),
            (ColorMapping.BrightnessHuePan, "Brillo → ganancia, color → panorama", "Brightness → gain, hue → pan"),
        ];
        foreach (var m in maps)
        {
            var mm = m.M;
            mapping.Items.Add(Item((canvasPrefs.ImportMapping == mm ? "● " : "○ ") + Loc.S(m.Es, m.En), () => canvasPrefs.ImportMapping = mm));
        }
        menu.Items.Add(mapping);
    }

    /// <summary>Image menu, generated from <see cref="ImageOps.All"/> (scope line first).</summary>
    /// <param name="menu">A fresh flyout to fill; a separator is inserted whenever the operation group changes.</param>
    void BuildImageMenu(MenuFlyout menu)
    {
        string scope = canvasSession.Selection is null ? Loc.S("(todo el lienzo)", "(whole canvas)") : Loc.S("(selección)", "(selection)");
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

    /// <summary>Examples menu, generated from <see cref="ImageFilterPresets.All"/>.</summary>
    /// <param name="menu">A fresh flyout to fill.</param>
    void BuildExamplesMenu(MenuFlyout menu)
    {
        foreach (var p in ImageFilterPresets.All)
        {
            var preset = p;
            var item = Item(Loc.S(p.NameEs, p.NameEn), () => LoadPreset(preset));
            ToolTip.SetTip(item, Loc.S(p.TipEs, p.TipEn));
            menu.Items.Add(item);
        }
    }

    /// <summary>Draws a starter picture on a fresh canvas of the current size/axis (undoable) and adopts its time settings.</summary>
    /// <param name="preset">The example to load.</param>
    /// <remarks>Public for tests. While playing, the filter switches to the new picture and timing at once.</remarks>
    public void LoadPreset(FilterPreset preset)
    {
        var cur = canvasSession.Image;
        var img = new SpectralImage(cur.Width, cur.Axis, cur.DurationSeconds);
        preset.Draw(img);
        // Time settings first, so the replacement below already plays with the preset's timing.
        SetFilter(preset.Time(prefs.Settings));
        canvasSession.ReplaceImage(img, "preset");
        canvasSession.ImageName = preset.NameEn.Replace(' ', '_');
        canvasSession.MarkPictureSaved();   // an untouched example can always be loaded again
        canvas.CursorColumn = 0;
        SyncDuration();
        Say(Loc.S($"Ejemplo «{preset.NameEs}»: {preset.TipEs}", $"Example «{preset.NameEn}»: {preset.TipEn}"), false);
    }

    /// <summary>Replaces the canvas by a uniform one (1 = white: unity everywhere, 0 = black: silence), undoable.</summary>
    /// <param name="level">Brightness of every cell on both layers.</param>
    void NewCanvas(float level)
    {
        var cur = canvasSession.Image;
        var img = new SpectralImage(cur.Width, cur.Axis, cur.DurationSeconds);
        // A new SpectralImage is all zeros (black) already; only white needs filling.
        if (level > 0)
        {
            Array.Fill(img.Left, level);
            Array.Fill(img.Right, level);
        }
        canvasSession.ReplaceImage(img, "new");
        canvasSession.ImageName = level > 0 ? "white" : "black";
        canvasSession.MarkPictureSaved();   // a uniform canvas has nothing to lose
        Say(level > 0
            ? Loc.S("Lienzo blanco: el sonido pasa sin cambios. Talla muescas con la goma (botón derecho).", "White canvas: the sound passes unchanged. Carve notches with the eraser (right button).")
            : Loc.S("Lienzo negro: nada pasa. Pinta las bandas que quieres oír.", "Black canvas: nothing passes. Paint the bands you want to hear."), false);
    }

    /// <summary>Runs an image operation on the selection (or everything), asking for its parameter first when it has one.</summary>
    /// <param name="op">The operation; <see cref="PaintSession.Apply"/> records the undo step and raises the change events (which reach the player).</param>
    /// <returns>A task that completes after the dialog (if any) and the operation.</returns>
    async Task RunOperationAsync(ImageOperation op)
    {
        double value = 0;
        if (op.Parameter is { } p)
        {
            if (await PaintParameterDialog.AskAsync(ctx.Window, Loc.S(op.NameEs, op.NameEn), Loc.S(p.LabelEs, p.LabelEn), p.Min, p.Max, p.Default, p.Step, p.Format) is not { } v)
                return;
            value = v;
        }
        canvasSession.Apply(op, value);
        Say(Loc.S(op.NameEs, op.NameEn) + " ✓", false);
    }

    /// <summary>Asks for one number in a small dialog and applies it through <paramref name="make"/>.</summary>
    /// <param name="label">Prompt next to the number box.</param>
    /// <param name="min">Smallest value allowed.</param>
    /// <param name="max">Largest value allowed.</param>
    /// <param name="value">Initial value.</param>
    /// <param name="step">Increment of the number box.</param>
    /// <param name="make">Builds the new settings from the value (applied with <see cref="SetFilter"/>; nothing happens on Cancel).</param>
    /// <returns>A task that completes when the dialog closes.</returns>
    async Task AskAsync(string label, double min, double max, double value, double step, Func<double, ImageFilterSettings> make)
    {
        if (await PaintParameterDialog.AskAsync(ctx.Window, Loc.S("Filtro", "Filter"), label, min, max, value, step, "0.##") is { } v)
            SetFilter(make(v));
    }

    // ------------------------------------------------------------------------------------------
    //  Files and drag-and-drop
    // ------------------------------------------------------------------------------------------

    /// <summary>Canvas › Import picture: picker filtered to the supported picture formats.</summary>
    /// <returns>A task that completes when the picker closes and the picture is imported.</returns>
    async Task ImportPictureAsync()
    {
        var files = await ctx.Window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.S("Importar imagen como filtro", "Import picture as filter"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Loc.S("Imágenes", "Pictures")) { Patterns = ImageCodec.Extensions.Select(e => "*" + e).ToArray() }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            ImportPicture(path);
    }

    /// <summary>Imports a picture file as the filter (same size and axis), undoable.</summary>
    /// <param name="path">Picture file (any of <see cref="ImageCodec.Extensions"/>).</param>
    /// <remarks>
    /// The picture is stretched to the current canvas (unlike Paint Synth's import, no fit choice)
    /// and its colours are read with the «When importing, colours mean…» rule. Decoding runs
    /// synchronously on the UI thread; errors go to the status line.
    /// </remarks>
    public void ImportPicture(string path)
    {
        try
        {
            var cur = canvasSession.Image;
            var img = ImageCodec.Import(path, cur.Width, cur.Axis, cur.DurationSeconds, canvasPrefs.ImportMapping);
            canvasSession.ReplaceImage(img, "import");
            canvasSession.ImageName = Path.GetFileNameWithoutExtension(path);
            Say(Loc.S($"Imagen importada como filtro: {Path.GetFileName(path)}. Tip: «Selección ▾ › Contraste» hace el filtro más marcado.",
                      $"Picture imported as filter: {Path.GetFileName(path)}. Tip: «Selection ▾ › Contrast» makes the filter more pronounced."), false);
        }
        catch (Exception ex)
        {
            Say(Loc.S("No se pudo importar: ", "Could not import: ") + ex.Message, true);
        }
    }

    /// <summary>Canvas › Export PNG (red = left, green = right).</summary>
    /// <returns>True when the file was written (false: picker cancelled or write failed).</returns>
    async Task<bool> ExportPngAsync()
    {
        var file = await ctx.Window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.S("Exportar PNG", "Export PNG"),
            SuggestedFileName = canvasSession.ImageName + ".png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"] }],
            ShowOverwritePrompt = true,
        });
        if (file?.TryGetLocalPath() is not { } path) return false;
        try
        {
            ImageCodec.ExportPng(canvasSession.Image, path);
            canvasSession.MarkPictureSaved();
            Say(Loc.S($"PNG guardado: {path}", $"PNG saved: {path}"), false);
            return true;
        }
        catch (Exception ex)
        {
            Say(ex.Message, true);
            return false;
        }
    }

    /// <summary>True after «Don't save»: the canvas was dropped and must not be asked about again.</summary>
    bool discarded;

    /// <inheritdoc/>
    public bool HasUnsavedChanges => !discarded && canvasSession.HasUnsavedPicture && !canvasSession.PictureIsBlank;

    /// <inheritdoc/>
    public string UnsavedName => Loc.S("la imagen del Image Filter (se guarda como PNG)", "the Image Filter picture (saved as PNG)");

    /// <inheritdoc/>
    /// <remarks>«Save» = Canvas ▾ › Export PNG.</remarks>
    public Task<bool> SaveChangesAsync() => ExportPngAsync();

    /// <inheritdoc/>
    /// <remarks>
    /// «Don't save» forgets the room state (canvas, player, prepared sources): the next time the
    /// room opens it starts again from the starter filter picture.
    /// </remarks>
    public void DiscardChanges()
    {
        player.Stop();
        States.Remove(shared);
        discarded = true;
    }

    /// <summary>Accepts files dragged over the room.</summary>
    /// <param name="sender">The root panel.</param>
    /// <param name="e">Drag data; any file shows the Copy cursor (the type is checked on drop).</param>
    void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Dropped file: a WAV becomes the source, a picture becomes the filter.</summary>
    /// <param name="sender">The root panel.</param>
    /// <param name="e">Drop data; only the first file is used, other types are ignored.</param>
    void OnDrop(object? sender, DragEventArgs e)
    {
        var file = e.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (file is null) return;
        string ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext == ".wav") _ = LoadWavAsync(file);
        else if (ImageCodec.Extensions.Contains(ext)) ImportPicture(file);
        e.Handled = true;
    }

    /// <summary>The «?» quick help.</summary>
    void ShowHelp()
    {
        string text = Loc.S(
            "CÓMO FUNCIONA\n• Elige qué filtrar en «Fuente»: un WAV, el sonido de otra sala o la entrada en vivo (micrófono, guitarra).\n• Cada columna de la imagen es un ecualizador para ese momento: la altura de la fila es la frecuencia y el brillo su ganancia (negro = silencio, blanco = sin cambio).\n• La capa izquierda (rojo) filtra el canal izquierdo, la derecha (verde) el derecho: pintando de un lado mueves el sonido en el estéreo.\n• «Tiempo»: estirar la imagen sobre el sonido, o repetirla cada N segundos o compases.\n• Csound lo hace con un vocoder de fase (pvsanal → pvsmaska → pvsynth). «Ver código Csound» te lo muestra.\n\nATAJOS\nEspacio tocar/detener · P lápiz · A aerógrafo · L línea · R rectángulo · O elipse · G degradado · E goma · B desenfocar · U difuminar\nM selección · Mayús+M elipse · F lazo · N spline (Enter cierra, Esc cancela); Mayús agrega, Alt resta\nCtrl+Z / Ctrl+Y / Ctrl+Mayús+Z · Ctrl+C / X / V · Supr · Esc · Inicio · Ctrl+0 ver todo · botón derecho = borrar\n\nLa guía completa con tutoriales paso a paso está en la documentación (Paint Rooms › Image Filter).",
            "HOW IT WORKS\n• Choose what to filter in «Source»: a WAV, another room's sound or the live input (microphone, guitar).\n• Every column of the picture is an equaliser for that moment: the row's height is the frequency and its brightness the gain (black = silence, white = unchanged).\n• The left layer (red) filters the left channel, the right layer (green) the right one: painting on one side moves the sound in the stereo field.\n• «Time»: stretch the picture over the sound, or repeat it every N seconds or bars.\n• Csound does it with a phase vocoder (pvsanal → pvsmaska → pvsynth). «Show Csound code» shows you how.\n\nSHORTCUTS\nSpace play/stop · P pencil · A airbrush · L line · R rectangle · O ellipse · G gradient · E eraser · B blur · U smudge\nM selection · Shift+M ellipse · F lasso · N spline (Enter closes, Esc cancels); Shift adds, Alt subtracts\nCtrl+Z / Ctrl+Y / Ctrl+Shift+Z · Ctrl+C / X / V · Del · Esc · Home · Ctrl+0 fit all · right button = erase\n\nThe full guide with step-by-step tutorials is in the documentation (Paint Rooms › Image Filter).");
        _ = new InfoDialog(Loc.S("Filtro de imagen · ayuda", "Image Filter · help"), text).ShowDialog(ctx.Window);
    }

    // ------------------------------------------------------------------------------------------
    //  Small helpers
    // ------------------------------------------------------------------------------------------

    /// <summary>Shows a message at the bottom right (accent colour, or the warning colour).</summary>
    /// <param name="text">The message (also set as tooltip, since long ones are trimmed).</param>
    /// <param name="warning">True for the warning colour. Matches the player's <see cref="ImageFilterPlayer.Message"/> signature.</param>
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
    /// <param name="controls">Controls in order (their Margin is overwritten).</param>
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

    /// <summary>Section caption of the tools column.</summary>
    /// <param name="text">Caption text (upper case by convention).</param>
    /// <returns>A small semi-bold accent-coloured text block.</returns>
    static TextBlock Caption(string text) =>
        new() { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = AppTheme.Brush("AccentHi"), Margin = new Thickness(2, 6, 0, 2) };

    /// <summary>A slider with its label on the left and its value on the right above it.</summary>
    /// <param name="label">Label text.</param>
    /// <param name="value">Value read-out (updated elsewhere).</param>
    /// <param name="slider">The slider.</param>
    /// <returns>A two-line stack: header grid (label · value), then the slider.</returns>
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
    /// <returns>The item.</returns>
    static MenuItem Item(string header, Action click)
    {
        var m = new MenuItem { Header = header };
        m.Click += (_, _) => click();
        return m;
    }

    /// <summary>A button that opens a menu built fresh each time (so check marks and values are current).</summary>
    /// <param name="text">Button text (with «▾»).</param>
    /// <param name="tip">Tooltip.</param>
    /// <param name="build">Fills the new flyout.</param>
    /// <returns>The button.</returns>
    static Button MenuButton(string text, string tip, Action<MenuFlyout> build)
    {
        // ToolUi.Button needs an action; the real work is the extra Click handler below, which
        // needs the button itself as the flyout's anchor.
        var b = ToolUi.Button(text, tip, () => { });
        b.Click += (_, _) =>
        {
            var menu = new MenuFlyout();
            build(menu);
            menu.ShowAt(b);
        };
        return b;
    }

    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// A small drawn strip with the source's waveform (min/max envelope per pixel column, both
    /// channels mixed) and a vertical line at the play position. Shows a caption instead when
    /// there is no file (live input, nothing loaded).
    /// </summary>
    sealed class SourceWaveform : Control
    {
        /// <summary>Backing field of <see cref="Audio"/>.</summary>
        AudioData? audio;
        float[]? mins, maxs;     // envelope cache for the current width (one entry per pixel column)
        /// <summary>Backing field of <see cref="Position"/> (seconds).</summary>
        double? position;
        /// <summary>Backing field of <see cref="Caption"/>.</summary>
        string? caption;

        /// <summary>The sound shown (or <c>null</c>).</summary>
        public AudioData? Audio
        {
            get => audio;
            set
            {
                if (ReferenceEquals(value, audio)) return;
                audio = value;
                mins = maxs = null;
                InvalidateVisual();
            }
        }

        /// <summary>Play position in seconds (or <c>null</c>).</summary>
        public double? Position
        {
            get => position;
            set
            {
                if (value == position) return;
                position = value;
                InvalidateVisual();
            }
        }

        /// <summary>Text shown when there is no waveform (or <c>null</c>).</summary>
        public string? Caption
        {
            get => caption;
            set
            {
                if (value == caption) return;
                caption = value;
                InvalidateVisual();
            }
        }

        /// <summary>Computes the min/max envelope for <paramref name="columns"/> pixel columns (cached until the size or the sound changes).</summary>
        /// <param name="columns">Width in device-independent pixels.</param>
        void Envelope(int columns)
        {
            if (audio is null || (mins is not null && mins.Length == columns)) return;
            mins = new float[columns];
            maxs = new float[columns];
            int frames = audio.Frames, nch = audio.Channels.Length;
            for (int c = 0; c < columns; c++)
            {
                // Frames [a, b) fall in pixel column c (64-bit products: long files × wide strips overflow int).
                int a = (int)((long)c * frames / columns), b = Math.Max(a + 1, (int)((long)(c + 1) * frames / columns));
                float lo = 0, hi = 0;
                // At most ~400 samples per column are inspected: plenty for a 58-pixel-high strip.
                int step = Math.Max(1, (b - a) / 400);
                for (int i = a; i < Math.Min(b, frames); i += step)
                {
                    float v = 0;
                    for (int ch = 0; ch < nch; ch++) v += audio.Channels[ch][i];
                    v /= Math.Max(1, nch);
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
                mins[c] = lo;
                maxs[c] = hi;
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// One vertical line per pixel column from max to min (amplitude ±1 → ±(half height − 3 px));
        /// the play position is a 2 px line at <c>position / length × width</c>.
        /// </remarks>
        public override void Render(DrawingContext context)
        {
            var r = new Rect(Bounds.Size);
            context.FillRectangle(AppTheme.Brush("BgDeep"), r);
            if (audio is null || audio.Frames == 0)
            {
                if (caption is not null)
                {
                    var ft = new FormattedText(caption, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 13, AppTheme.Brush("TextDim"));
                    context.DrawText(ft, new Point(12, (r.Height - ft.Height) / 2));
                }
                return;
            }
            int w = Math.Max(1, (int)r.Width);
            Envelope(w);
            double mid = r.Height / 2, scale = r.Height / 2 - 3;
            var pen = new Pen(AppTheme.Brush("Accent"), 1);
            for (int x = 0; x < w; x++)
                context.DrawLine(pen, new Point(x + 0.5, mid - maxs![x] * scale), new Point(x + 0.5, mid - mins![x] * scale + 0.5));
            if (position is { } p && audio.Seconds > 0)
            {
                double px = p / audio.Seconds * r.Width;
                context.DrawLine(new Pen(AppTheme.Brush("Good"), 2), new Point(px, 0), new Point(px, r.Height));
            }
        }
    }
}
