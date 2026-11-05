// SpectrumRoom.cs — Paint Rooms: the «Spectrum Room» tab (sound → picture → sound).
//
// Role: load a recording, see it as a picture on any pitch axis, edit the picture with the same
// brushes, selection and image operations as Paint Synth, and turn it back into sound in one of two
// ways the user chooses:
//   * «Spectral mask» (faithful): the ORIGINAL recording is filtered by a time-frequency gain mask
//     = edited picture ÷ original analysis (Paint/SpectralMask.cs, computed in C#). Untouched parts
//     sound identical, erased parts disappear, painted gain changes become level changes.
//     Preview: the result is rendered to a temporary WAV on a background task and played through
//     the app's Csound with diskin2 (Paint/SoundPreviewPlayer.cs); after an edit it is re-rendered
//     and resumes where it was.
//   * «Additive» (creative): the picture is played by the Paint Synth Csound engine (one oscillator
//     or noise band per row) live, exactly like Paint Synth, and rendered with PaintRenderer. Phases
//     and noise character are lost — it becomes a «painted» version of the sound.
// Room tools: band selection on the pitch ruler, solo / remove / gain ±dB on the selection, extract
// the selection to a WAV, freeze one moment into a sustained tone, send the picture to Paint Synth,
// send the sound to the session (for the Image Filter room).
//
// Data: the room owns a PRIVATE PaintSession (its own image, history, selection, brushes), so its
// edits never touch Paint Synth's canvas until «Send to Paint Synth». Each analysed image is linked
// to its SpectrumAnalysis (sound + settings + unedited original) through a ConditionalWeakTable, so
// undoing back to an older analysis (another file, other settings) still resynthesises correctly.
//
// Threading: everything here runs on the UI thread except the explicitly backgrounded work
// (loading/decoding, analysis, mask rendering, WAV writing), which runs in Task.Run on SNAPSHOTS
// (images are cloned on the UI thread first) and reports progress through Progress<T> (marshalled
// back to the UI thread). One CancellationTokenSource covers the current background job; starting a
// new job cancels the previous one. Csound is only touched through the players (their actions run on
// the Csound thread).
//
// Layout (built in code like the other rooms, themed, every string bilingual through Loc.S):
//   row 1  sound + transport: Open sound…, Use session sound, Play/Stop, ⇤, Resynthesis, Voice,
//          Render WAV, Normalise
//   row 2  analysis: FFT size, overlap, column ms, floor dB, axis, low/high Hz, rows/steps, Analyse
//   row 3  sound tools: Solo, Remove, Gain dB + Apply, Extract WAV…, Freeze ▾,
//          → Paint Synth, Sound → session ▾, ?
//   left   tools palette + brush
//   centre file information, waveform strip (time-aligned), canvas with scroll bars
//   bottom hover read-out (with dB), messages, progress bar + Cancel

using System.Globalization;
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

/// <summary>The «Spectrum Room»: analyse a sound into a picture, edit it, resynthesise it (see the file header).</summary>
public sealed class SpectrumRoom : IPaintRoom
{
    /// <summary>What is sounding right now, started by this room.</summary>
    enum Playing
    {
        /// <summary>Nothing started by this room is playing.</summary>
        None,
        /// <summary>The spectral-mask preview WAV is playing through <see cref="preview"/> (diskin2).</summary>
        Mask,
        /// <summary>The picture is playing live through the shared Paint Synth player (additive).</summary>
        Additive,
    }

    /// <summary>The window context: shared session, main view model (<c>ctx.App</c>) and the Paint Rooms window (<c>ctx.Window</c>, owner of dialogs and pickers).</summary>
    readonly PaintRoomContext ctx;
    /// <summary>The window's session (Paint Synth's image, player, session sound).</summary>
    readonly PaintSession shared;
    /// <summary>This room's private session (analysis image, history, selection, brushes).</summary>
    readonly PaintSession session;
    /// <summary>Persisted room preferences (<c>shared.Settings.Spectrum</c>), written back on every change.</summary>
    readonly SpectrumRoomSettings sp;
    /// <summary>
    /// Links every image produced by an analysis to that analysis. Weak keys: when an image falls
    /// out of the undo history and is collected, its entry (and the sound it references) can go too.
    /// </summary>
    readonly ConditionalWeakTable<SpectralImage, SpectrumAnalysis> analyses = new();
    /// <summary>The most recent analysis (fallback for images without a link).</summary>
    SpectrumAnalysis? current;
    /// <summary>The sound being analysed (decoded samples + file information), or <c>null</c> before the first load.</summary>
    LoadedSound? sound;

    // ---- views
    /// <summary>The shared Paint canvas control, bound to the private <see cref="session"/>.</summary>
    readonly PaintCanvas canvas = new();
    /// <summary>Waveform of <see cref="sound"/> above the canvas, following its horizontal zoom and scroll.</summary>
    readonly WaveformStrip strip;
    /// <summary>Horizontal (time) scroll bar; value = <see cref="PaintCanvas.ScrollX"/>.</summary>
    readonly ScrollBar hScroll = new() { Orientation = Orientation.Horizontal, Height = 12 };
    /// <summary>Vertical (pitch) scroll bar; value = <see cref="PaintCanvas.ScrollY"/>.</summary>
    readonly ScrollBar vScroll = new() { Orientation = Orientation.Vertical, Width = 12 };
    /// <summary>The room's whole UI (returned by <see cref="Content"/>).</summary>
    readonly DockPanel root = new();
    /// <summary>
    /// UI refresh tick every 33 ms (~30 Hz), running from construction to <see cref="Dispose"/> (also
    /// while another tab is shown — it is cheap when nothing plays): see <see cref="OnTick"/>.
    /// </summary>
    readonly DispatcherTimer timer;
    /// <summary>Debounce: re-render the mask preview after edits.</summary>
    readonly DispatcherTimer editTimer;
    /// <summary>Debounce: re-analyse after the analysis settings changed.</summary>
    readonly DispatcherTimer analyseTimer;

    // ---- controls
    // Built in the constructor; the ones referenced later (to sync, enable or read them) are fields.
    // The row-2 number boxes write straight into sp; SyncControls writes sp back into them.

    /// <summary>Row 1 / row 2 / status buttons whose caption, icon or enabled state changes at run time.</summary>
    readonly Button playButton, renderButton, sessionSoundButton, analyseButton, cancelButton;
    /// <summary>Resynthesis mode: index = <see cref="SpectrumResynthesis"/> (0 mask, 1 additive).</summary>
    readonly ComboBox modeBox = new() { Width = 214, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>Voice of the additive resynthesis: 0 sines, 1 noise bands.</summary>
    readonly ComboBox voiceBox = new() { Width = 130, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>«Normalise» toggle for written WAV files (<see cref="SpectrumRoomSettings.Normalize"/>).</summary>
    readonly ToggleButton normalizeToggle;
    /// <summary>FFT frame size in samples (items are the ints themselves).</summary>
    readonly ComboBox fftBox = new() { Width = 92, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>Window overlap 2×/4×/8× (index 0/1/2).</summary>
    readonly ComboBox overlapBox = new() { Width = 70, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>
    /// Number boxes: column length (ms), floor (dB), low/high frequency (Hz), rows (log/linear axes),
    /// steps per octave (equal temperament) and the «Apply gain» amount (dB).
    /// </summary>
    readonly NumericUpDown columnBox, floorBox, lowBox, highBox, rowsBox, stepsBox, gainBox;
    /// <summary>Pitch axis kind: index = <see cref="PitchAxisKind"/>.</summary>
    readonly ComboBox axisBox = new() { Width = 200, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>Captioned wrappers hidden when they do not apply (rows: log/linear only; steps: ET only; voice: additive only).</summary>
    readonly StackPanel rowsPanel, stepsPanel, voicePanel;
    /// <summary>Palette buttons by tool, so <see cref="SelectTool"/> can check exactly one.</summary>
    readonly Dictionary<PaintTool, ToggleButton> toolButtons = [];

    /// <summary>The «SELECTION» options (feather), shown while a selection tool is active (built in <see cref="BuildPalette"/>).</summary>
    StackPanel? selectionPanel;
    /// <summary>Brush diameter in SCREEN pixels (not image cells), 1–120.</summary>
    readonly Slider sizeSlider = new() { Minimum = 1, Maximum = 120, Width = 150, Value = 16 };
    /// <summary>Paint level 0.02–1 (a perceptual level: 1 = 0 dB, 0 = the floor; shown in dB).</summary>
    readonly Slider opacitySlider = new() { Minimum = 0.02, Maximum = 1, Width = 150, Value = 0.7 };
    /// <summary>Brush edge hardness 0 (soft) – 1 (hard).</summary>
    readonly Slider hardSlider = new() { Minimum = 0, Maximum = 1, Width = 150, Value = 0.6 };
    /// <summary>Stereo layer painted: −1 left only, 0 both, +1 right only.</summary>
    readonly Slider panSlider = new() { Minimum = -1, Maximum = 1, Width = 150, Value = 0 };
    /// <summary>Value captions next to the four brush sliders.</summary>
    readonly TextBlock sizeText = Small(), opacityText = Small(), hardText = Small(), panText = Small();
    /// <summary>File information line above the waveform (name, rate, channels, length, picture size, resolutions).</summary>
    readonly TextBlock infoText = new() { FontSize = 12, Foreground = AppTheme.Brush("TextDim"), Margin = new Thickness(2, 0, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis };
    /// <summary>Status bar: what is under the mouse (time, frequency/note, level in dB).</summary>
    readonly TextBlock hoverText = new() { FontSize = 12, Foreground = AppTheme.Brush("TextDim"), TextTrimming = TextTrimming.CharacterEllipsis };
    /// <summary>Status bar: last message from <see cref="Say"/> (red when it is a warning).</summary>
    readonly TextBlock messageText = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right };
    /// <summary>Progress 0–1 of the current background job (indeterminate while loading).</summary>
    readonly ProgressBar progressBar = new() { Width = 160, Height = 6, Minimum = 0, Maximum = 1, IsVisible = false, Margin = new Thickness(12, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

    // ---- playback state
    /// <summary>Plays the mask preview WAV through the app's Csound (diskin2) and reports its position.</summary>
    readonly SoundPreviewPlayer preview;
    /// <summary>Which of the two players this room started (the other one is idle).</summary>
    Playing playing;
    /// <summary>Paint-Synth-level copy being played by the shared player.</summary>
    SpectralImage? additiveImage;
    /// <summary>The room image it was made from (edits are forwarded).</summary>
    SpectralImage? additiveSource;
    /// <summary>Solo region of the current play (null = everything).</summary>
    SelectionMask? playSolo;
    /// <summary>A play waiting for the other player to release Csound (run once by <see cref="OnTick"/>, see <see cref="StartPlayer"/>).</summary>
    Action? pendingStart;
    /// <summary>When <see cref="pendingStart"/> was queued (UTC); <see cref="OnTick"/> runs it 0.4 s later.</summary>
    DateTime pendingSince;
    /// <summary>Last mask preview WAV.</summary>
    string? previewFile;
    /// <summary>
    /// What <see cref="previewFile"/> was rendered from: the image object, its edit version and the
    /// solo region. Equal key = the file is still valid and can be replayed without re-rendering.
    /// </summary>
    (SpectralImage Image, long Version, SelectionMask? Solo)? previewKey;
    /// <summary>Per-process folder for preview WAVs (%TEMP%\csoundvisual_spectrum\&lt;pid&gt;), deleted on dispose.</summary>
    readonly string tempFolder = Path.Combine(Path.GetTempPath(), "csoundvisual_spectrum", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    /// <summary>Numbers the preview files: a new name each time, because the previous file may still be open in diskin2 (Windows cannot overwrite an open file).</summary>
    int tempCounter;

    // ---- background work
    /// <summary>Cancellation of the current background job; <c>null</c> = idle (see <see cref="BeginWork"/>).</summary>
    CancellationTokenSource? work;
    /// <summary>True while code writes into controls, so their change handlers do not react to it.</summary>
    bool syncing;
    /// <summary>Last pointer position over the canvas (device-independent pixels, canvas coordinates), for the hover read-out; <c>null</c> until the pointer first moves over it.</summary>
    Point? lastPointer;
    /// <summary>True while a band selection is being dragged on the pitch ruler.</summary>
    bool bandDrag;
    /// <summary>Fractional image row where the band drag started.</summary>
    double bandStartRow;

    /// <summary>Creates the room for the window context <paramref name="context"/>.</summary>
    /// <param name="context">Shared session, main view model and window (from <see cref="PaintRoomsWindow"/>).</param>
    /// <remarks>
    /// Builds the whole UI, wires the private session's events and starts the UI timer. No sound
    /// is loaded yet; the canvas shows the private session's initial empty picture.
    /// </remarks>
    public SpectrumRoom(PaintRoomContext context)
    {
        ctx = context;
        shared = context.Session;
        // Older settings files have no Spectrum section: create it on first use (saved with the rest).
        sp = shared.Settings.Spectrum ??= new SpectrumRoomSettings();
        // The private session gets a settings object of its own, seeded from the room's axis
        // preferences, so its empty initial picture already has the room's pitch axis. Scala path and
        // base frequency are borrowed from Paint Synth (the room has no Scala loader of its own).
        var roomPrefs = new PaintRoomSettings
        {
            AxisKind = sp.AxisKind, LowHz = sp.LowHz, HighHz = sp.HighHz, Rows = sp.Rows, StepsPerOctave = sp.StepsPerOctave,
            ScalaPath = shared.Settings.ScalaPath, BaseHz = shared.Settings.BaseHz, Width = 1024, DurationSeconds = 10,
        };
        session = new PaintSession(roomPrefs);
        canvas.Session = session;
        strip = new WaveformStrip(canvas) { Height = 84 };
        preview = new SoundPreviewPlayer(context.App);
        preview.Message += Say;
        preview.StateChanged += UpdatePlayButton;

        // ================= row 1: sound + transport =================
        var openButton = ToolUi.Button(Loc.S("Abrir sonido…", "Open sound…"),
            Loc.S("Abre un WAV, AIFF, FLAC, OGG o MP3 y lo analiza (también puedes arrastrar el archivo al lienzo). Los formatos que no son WAV los decodifica Csound.",
                  "Opens a WAV, AIFF, FLAC, OGG or MP3 file and analyses it (you can also drag the file onto the canvas). Formats other than WAV are decoded by Csound."),
            () => _ = OpenSoundAsync(), "IconOpen", primary: true);
        sessionSoundButton = ToolUi.Button(Loc.S("Usar sonido de la sesión", "Use session sound"),
            Loc.S("Analiza el sonido que otra sala dejó en la sesión (por ejemplo, el resultado del Filtro de imagen).",
                  "Analyses the sound another room left in the session (for example the result of the Image Filter)."),
            UseSessionSound);
        playButton = ToolUi.Button(Loc.S("Tocar", "Play"),
            Loc.S("Escucha el resultado desde el cursor amarillo (Espacio), con la resíntesis elegida. Si Csound está detenido, se inicia solo.",
                  "Listens to the result from the yellow cursor (Space), with the chosen resynthesis. If Csound is stopped it starts by itself."),
            () => TogglePlay(null), "IconPlay", primary: true);
        playButton.MinWidth = 100;
        var homeButton = ToolUi.Button("⇤", Loc.S("Cursor al comienzo (Inicio).", "Cursor to the start (Home)."), () => canvas.CursorColumn = 0);
        modeBox.ItemsSource = new[] { Loc.S("Máscara espectral (fiel)", "Spectral mask (faithful)"), Loc.S("Aditiva (Paint Synth)", "Additive (Paint Synth)") };
        ToolTip.SetTip(modeBox, Loc.S(
            "Cómo vuelve la imagen a sonido.\n• Máscara espectral: filtra la grabación ORIGINAL con la imagen (editada ÷ original). Lo que no tocaste suena idéntico; lo borrado desaparece. Ideal para limpiar, quitar notas o ruidos.\n• Aditiva: Csound toca la imagen con un oscilador por fila, como Paint Synth. Pierde las fases y el carácter del ruido: es una versión «pintada», creativa.",
            "How the picture becomes sound again.\n• Spectral mask: filters the ORIGINAL recording with the picture (edited ÷ original). What you did not touch sounds identical; what you erased disappears. Best for cleaning, removing notes or noises.\n• Additive: Csound plays the picture with one oscillator per row, like Paint Synth. Loses the phases and noise character: a creative, «painted» version."));
        modeBox.SelectedIndex = (int)sp.Mode;
        modeBox.SelectionChanged += (_, _) =>
        {
            if (syncing || modeBox.SelectedIndex < 0) return;
            // Switching path stops the current play: the two modes use different players.
            StopAll();
            var mode = (SpectrumResynthesis)modeBox.SelectedIndex;
            ChangeSettings(() => sp.Mode = mode, reanalyse: false);
            voicePanel!.IsVisible = sp.Mode == SpectrumResynthesis.Additive;
        };
        voiceBox.ItemsSource = new[] { Loc.S("Senos", "Sines"), Loc.S("Bandas de ruido", "Noise bands") };
        voiceBox.SelectedIndex = sp.AdditiveMode == SynthMode.NoiseBands ? 1 : 0;
        ToolTip.SetTip(voiceBox, Loc.S("Voz de la resíntesis aditiva: senos (tonos claros) o bandas de ruido (aire, respiración).", "Voice of the additive resynthesis: sines (clear tones) or noise bands (air, breath)."));
        voiceBox.SelectionChanged += (_, _) => { if (!syncing) { var m = voiceBox.SelectedIndex == 1 ? SynthMode.NoiseBands : SynthMode.Sines; ChangeSettings(() => sp.AdditiveMode = m, reanalyse: false); StopAll(); } };
        voicePanel = ToolUi.Captioned(Loc.S("Voz", "Voice"), voiceBox);
        voicePanel.IsVisible = sp.Mode == SpectrumResynthesis.Additive;
        renderButton = ToolUi.Button(Loc.S("Renderizar WAV", "Render WAV"),
            Loc.S("Escribe el resultado (con la resíntesis elegida) a un WAV de 24 bits.", "Writes the result (with the chosen resynthesis) to a 24-bit WAV file."),
            () => _ = RenderAsync(), "IconRender");
        normalizeToggle = ToolUi.Toggle(Loc.S("Normalizar", "Normalise"),
            Loc.S("Lleva el pico de los WAV que escribes a −1 dBFS. Apagado: conserva el nivel original (solo baja si fuera a saturar).",
                  "Brings the peak of the WAV files you write to −1 dBFS. Off: keeps the original level (only lowered if it would clip)."));
        normalizeToggle.IsChecked = sp.Normalize;
        normalizeToggle.IsCheckedChanged += (_, _) => { if (!syncing) { bool on = normalizeToggle.IsChecked == true; ChangeSettings(() => sp.Normalize = on, reanalyse: false); } };
        var row1 = Row();
        AddAll(row1, openButton, sessionSoundButton, ToolUi.Separator(), playButton, homeButton, ToolUi.Separator(),
            ToolUi.Captioned(Loc.S("Resíntesis", "Resynthesis"), modeBox), voicePanel, renderButton, normalizeToggle);

        // ================= row 2: analysis =================
        // Every change here is stored in sp at once and schedules a debounced re-analysis
        // (AnalysisSettingsChanged); the picture is replaced through the undo history.
        fftBox.ItemsSource = new[] { 512, 1024, 2048, 4096, 8192, 16384 };
        fftBox.SelectedItem = sp.Analysis.FrameSize;
        ToolTip.SetTip(fftBox, Loc.S("Tamaño de la FFT. Más grande = más detalle en altura (notas graves separadas) pero más borroso en el tiempo. 2048 es un buen comienzo; 4096–8192 para música tonal lenta; 512–1024 para percusión.",
                                     "FFT size. Bigger = more pitch detail (low notes separated) but blurrier in time. 2048 is a good start; 4096–8192 for slow tonal music; 512–1024 for percussion."));
        overlapBox.ItemsSource = new[] { "2×", "4×", "8×" };
        overlapBox.SelectedIndex = sp.Analysis.Overlap switch { 2 => 0, 8 => 2, _ => 1 };
        ToolTip.SetTip(overlapBox, Loc.S("Solapamiento de las ventanas (salto = FFT ÷ solapamiento). 4× es lo normal; 8× afina el tiempo y la estimación de frecuencia; 2× es más rápido.",
                                         "Window overlap (hop = FFT ÷ overlap). 4× is normal; 8× refines time and frequency estimation; 2× is faster."));
        columnBox = ToolUi.Number(0, 1000, sp.ColumnMs, 1, Loc.S("Milisegundos por columna de la imagen (resolución en el tiempo del dibujo). 0 = automático: una columna por salto de la FFT.",
                                                                  "Milliseconds per picture column (time resolution of the drawing). 0 = automatic: one column per FFT hop."), "0.#", 70, spinner: false);
        floorBox = ToolUi.Number(-160, -20, sp.FloorDb, 6, Loc.S("Piso en dB: lo más silencioso que se ve (negro). −90 muestra casi todo; −60 limpia el ruido de fondo de la imagen.",
                                                                 "Floor in dB: the quietest level shown (black). −90 shows almost everything; −60 hides the background noise in the picture."), "0", 70, spinner: false);
        axisBox.ItemsSource = new[] { Loc.S("Lineal (Hz)", "Linear (Hz)"), Loc.S("Logarítmico (octavas)", "Logarithmic (octaves)"), Loc.S("Temperamento igual", "Equal temperament"), Loc.S("Escala Scala (de Paint Synth)", "Scala scale (from Paint Synth)") };
        axisBox.SelectedIndex = (int)sp.AxisKind;
        ToolTip.SetTip(axisBox, Loc.S("Eje de altura del análisis. Logarítmico = como se oye; Temperamento igual = una fila por nota (la energía cae en la nota más cercana); Scala usa la escala cargada en Paint Synth.",
                                      "Pitch axis of the analysis. Logarithmic = as we hear; Equal temperament = one row per note (energy lands on the nearest note); Scala uses the scale loaded in Paint Synth."));
        lowBox = ToolUi.Number(PitchAxis.MinHz, PitchAxis.MaxHz, sp.LowHz, 10, Loc.S("Frecuencia más grave analizada (Hz).", "Lowest analysed frequency (Hz)."), "0.#", 80, spinner: false);
        highBox = ToolUi.Number(PitchAxis.MinHz, PitchAxis.MaxHz, sp.HighHz, 100, Loc.S("Frecuencia más aguda analizada (Hz). Lo que queda fuera no se ve; con la máscara sigue a la fila del borde más cercana.", "Highest analysed frequency (Hz). What lies outside is not shown; with the mask it follows the nearest edge row."), "0.#", 88, spinner: false);
        rowsBox = ToolUi.Number(16, PitchAxis.MaxRows, sp.Rows, 32, Loc.S("Filas de los ejes lineal y logarítmico.", "Rows of the linear and logarithmic axes."), "0", 74, spinner: false);
        stepsBox = ToolUi.Number(1, 200, sp.StepsPerOctave, 1, Loc.S("Notas por octava (12 = semitonos, 24 = cuartos de tono…).", "Notes per octave (12 = semitones, 24 = quarter tones…)."), "0", 64, spinner: false);
        rowsPanel = ToolUi.Captioned(Loc.S("Filas", "Rows"), rowsBox);
        stepsPanel = ToolUi.Captioned(Loc.S("Notas/oct", "Steps/oct"), stepsBox);
        analyseButton = ToolUi.Button(Loc.S("Analizar", "Analyse"),
            Loc.S("Vuelve a analizar el sonido con estos ajustes (reemplaza la imagen; Ctrl+Z vuelve a la anterior con sus ediciones).",
                  "Analyses the sound again with these settings (replaces the picture; Ctrl+Z goes back to the previous one with its edits)."),
            () => _ = AnalyseAsync(), "IconRefresh");
        fftBox.SelectionChanged += (_, _) => { if (!syncing && fftBox.SelectedItem is int n) ChangeSettings(() => sp.FrameSize = n, reanalyse: true); };
        overlapBox.SelectionChanged += (_, _) => { if (!syncing && overlapBox.SelectedIndex >= 0) { int o = overlapBox.SelectedIndex switch { 0 => 2, 2 => 8, _ => 4 }; ChangeSettings(() => sp.Overlap = o, reanalyse: true); } };
        columnBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) ChangeSettings(() => sp.ColumnMs = (double)v, reanalyse: true); };
        floorBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) ChangeSettings(() => sp.FloorDb = (double)v, reanalyse: true); };
        axisBox.SelectionChanged += (_, _) =>
        {
            if (syncing || axisBox.SelectedIndex < 0) return;
            var kind = (PitchAxisKind)axisBox.SelectedIndex;
            if (kind == PitchAxisKind.Scala && (shared.Settings.ScalaPath is null || !File.Exists(shared.Settings.ScalaPath)))
            {
                // No scale to use: refuse the choice and put the combo back (guarded, so this
                // handler does not run again for the programmatic change).
                Say(Loc.S("Primero carga una escala .scl en Paint Synth («Cargar .scl…»).", "Load a .scl scale in Paint Synth first («Load .scl…»)."), true);
                syncing = true;
                axisBox.SelectedIndex = (int)sp.AxisKind;
                syncing = false;
                return;
            }
            ChangeSettings(() => sp.AxisKind = kind, reanalyse: true);
        };
        lowBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) ChangeSettings(() => sp.LowHz = (double)v, reanalyse: true); };
        highBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) ChangeSettings(() => sp.HighHz = (double)v, reanalyse: true); };
        rowsBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) ChangeSettings(() => sp.Rows = (int)v, reanalyse: true); };
        stepsBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) ChangeSettings(() => sp.StepsPerOctave = (int)v, reanalyse: true); };
        var row2 = Row();
        AddAll(row2, ToolUi.Captioned("FFT", fftBox), ToolUi.Captioned(Loc.S("Solape", "Overlap"), overlapBox),
            ToolUi.Captioned(Loc.S("Columna ms", "Column ms"), columnBox), ToolUi.Captioned(Loc.S("Piso dB", "Floor dB"), floorBox), ToolUi.Separator(),
            ToolUi.Captioned(Loc.S("Eje", "Axis"), axisBox), ToolUi.Captioned(Loc.S("Grave Hz", "Low Hz"), lowBox), ToolUi.Captioned(Loc.S("Agudo Hz", "High Hz"), highBox),
            rowsPanel, stepsPanel, analyseButton);

        // ================= row 3: sound tools =================
        var soloButton = ToolUi.Button(Loc.S("Solo ▶", "Solo ▶"),
            Loc.S("Escucha SOLO la selección (lo demás en silencio), con la resíntesis elegida. Selecciona con ⬚ (M) o arrastrando en la regla de alturas.",
                  "Listens ONLY to the selection (everything else silent), with the chosen resynthesis. Select with ⬚ (M) or by dragging on the pitch ruler."),
            () => { if (session.Selection is { } sel) TogglePlay(sel); else Warn(); }, "IconPlay");
        var removeButton = ToolUi.Button(Loc.S("Quitar", "Remove"),
            Loc.S("Borra la selección (Supr): con la máscara, eso desaparece del sonido.", "Erases the selection (Del): with the mask, that part disappears from the sound."),
            () => { if (session.Selection is null) Warn(); else { session.DeleteSelection(); Say(Loc.S("Selección quitada (Ctrl+Z deshace).", "Selection removed (Ctrl+Z undoes)."), false); } }, "IconDelete");
        gainBox = ToolUi.Number(-60, 24, sp.GainDb, 1, Loc.S("Ganancia en dB para «Aplicar ganancia» (negativo = más suave).", "Gain in dB for «Apply gain» (negative = softer)."), "+0;-0;0", 64, spinner: false);
        gainBox.ValueChanged += (_, e) => { if (!syncing && e.NewValue is { } v) ChangeSettings(() => sp.GainDb = (double)v, reanalyse: false); };
        var gainButton = ToolUi.Button(Loc.S("Aplicar ganancia", "Apply gain"),
            Loc.S("Sube o baja la selección (o todo) los dB indicados: el brillo cambia en la misma medida.", "Raises or lowers the selection (or everything) by the given dB: the brightness changes accordingly."),
            ApplyGain);
        var extractButton = ToolUi.Button(Loc.S("Extraer WAV…", "Extract WAV…"),
            Loc.S("Guarda SOLO la selección como un WAV nuevo (con la máscara: el sonido original de esa zona de tiempo y frecuencia).",
                  "Saves ONLY the selection as a new WAV (with the mask: the original sound of that time-frequency region)."),
            () => _ = ExtractAsync(), "IconSave");
        var freezeMenu = MenuButton(Loc.S("Congelar ▾", "Freeze ▾"),
            Loc.S("Convierte el instante del cursor amarillo en un tono sostenido (lo toca el motor de Paint Synth).",
                  "Turns the moment at the yellow cursor into a sustained tone (played by the Paint Synth engine)."), BuildFreezeMenu);
        var toSynthButton = ToolUi.Button(Loc.S("→ Paint Synth", "→ Paint Synth"),
            Loc.S("Copia esta imagen al lienzo de Paint Synth para seguir pintándola (con niveles convertidos para que suene con su volumen real). Ctrl+Z allá la deshace.",
                  "Copies this picture to the Paint Synth canvas to keep painting it (levels converted so it plays at its real loudness). Ctrl+Z there undoes it."),
            SendToPaintSynth);
        var toSessionMenu = MenuButton(Loc.S("Sonido → sesión ▾", "Sound → session ▾"),
            Loc.S("Deja un sonido en la sesión para que otra sala (Filtro de imagen) lo use.", "Leaves a sound in the session for another room (Image Filter) to use."), BuildSessionMenu);
        var helpButton = ToolUi.Button("?", Loc.S("Ayuda rápida de Spectrum Room.", "Spectrum Room quick help."), ShowHelp);
        var row3 = Row();
        AddAll(row3, soloButton, removeButton, ToolUi.Captioned(Loc.S("Ganancia dB", "Gain dB"), gainBox), gainButton, extractButton, ToolUi.Separator(),
            freezeMenu, toSynthButton, toSessionMenu, helpButton);

        // ================= palette, canvas, strip =================
        // `surface` grid: rows = info line, waveform strip, canvas (*), horizontal scroll bar;
        // columns = content (*), vertical scroll bar. The strip and the canvas share one rounded
        // frame (top corners on the strip, bottom corners on the canvas) so they read as one block.
        var palette = BuildPalette();
        var surface = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(0, 0, 10, 0) };
        surface.Children.Add(infoText);
        var stripBorder = new Border { Child = strip, CornerRadius = new CornerRadius(8, 8, 0, 0), ClipToBounds = true, BorderBrush = AppTheme.Brush("Line"), BorderThickness = new Thickness(1, 1, 1, 0) };
        ToolTip.SetTip(stripBorder, Loc.S("Forma de onda del sonido analizado, alineada con la imagen. Clic = mover el cursor amarillo.", "Waveform of the analysed sound, aligned with the picture. Click = move the yellow cursor."));
        Grid.SetRow(stripBorder, 1);
        surface.Children.Add(stripBorder);
        var canvasBorder = new Border { Child = canvas, CornerRadius = new CornerRadius(0, 0, 8, 8), ClipToBounds = true, BorderBrush = AppTheme.Brush("Line"), BorderThickness = new Thickness(1) };
        Grid.SetRow(canvasBorder, 2);
        surface.Children.Add(canvasBorder);
        Grid.SetRow(vScroll, 2);
        Grid.SetColumn(vScroll, 1);
        Grid.SetRow(hScroll, 3);
        surface.Children.Add(vScroll);
        surface.Children.Add(hScroll);
        // Two-way binding by hand: the bars move the canvas; the canvas's ViewChanged (zoom, wheel,
        // resize) moves the bars back under the `syncing` guard to avoid a feedback loop.
        hScroll.Scroll += (_, _) => { if (!syncing) canvas.ScrollX = hScroll.Value; };
        vScroll.Scroll += (_, _) => { if (!syncing) canvas.ScrollY = vScroll.Value; };
        canvas.ViewChanged += SyncScrollBars;
        // The canvas's own hover text shows raw 0…1 levels; it is only used as a trigger here and
        // replaced by the room's version in dB (empty text = pointer left the picture).
        canvas.Hover += t => hoverText.Text = t.Length == 0 ? "" : HoverText();
        ToolTip.SetTip(canvas, Loc.S(
            "Izquierdo = herramienta · derecho = borrar · arrastrar en la regla de alturas = seleccionar una banda de frecuencias en todo el tiempo · rueda = altura, Ctrl+rueda = zoom en el tiempo · suelta aquí un archivo de sonido para analizarlo",
            "Left = tool · right = erase · drag on the pitch ruler = select a frequency band over the whole time · wheel = pitch, Ctrl+wheel = time zoom · drop a sound file here to analyse it"));
        ToolTip.SetShowDelay(canvas, 2000);
        DragDrop.SetAllowDrop(canvas, true);
        canvas.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        canvas.AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(strip, true);
        strip.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        strip.AddHandler(DragDrop.DropEvent, OnDrop);
        // Band selection on the pitch ruler: tunnelling handlers run before the canvas's own (which ignores the ruler).
        canvas.AddHandler(InputElement.PointerPressedEvent, OnCanvasPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        canvas.AddHandler(InputElement.PointerMovedEvent, OnCanvasMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        canvas.AddHandler(InputElement.PointerReleasedEvent, OnCanvasReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // ================= status =================
        cancelButton = ToolUi.Button(Loc.S("Cancelar", "Cancel"), Loc.S("Detiene el trabajo en curso.", "Stops the work in progress."), () => work?.Cancel());
        cancelButton.IsVisible = false;
        var status = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(12, 6, 12, 8) };
        status.Children.Add(hoverText);
        Grid.SetColumn(messageText, 1);
        Grid.SetColumn(progressBar, 2);
        Grid.SetColumn(cancelButton, 3);
        status.Children.Add(messageText);
        status.Children.Add(progressBar);
        status.Children.Add(cancelButton);

        // DockPanel order matters: the three tool rows on top, status at the bottom, palette on the
        // left, and the last child (surface) fills what remains.
        DockPanel.SetDock(row1, Dock.Top);
        DockPanel.SetDock(row2, Dock.Top);
        DockPanel.SetDock(row3, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        DockPanel.SetDock(palette, Dock.Left);
        root.Children.Add(row1);
        root.Children.Add(row2);
        root.Children.Add(row3);
        root.Children.Add(status);
        root.Children.Add(palette);
        root.Children.Add(surface);

        // ================= wiring =================
        // All handlers below are unsubscribed in Dispose (the shared session outlives the room).
        session.ImageReplaced += OnImageReplaced;
        session.PixelsChanged += OnPixelsChanged;
        session.Edited += OnEdited;
        shared.SoundChanged += UpdateSessionSoundButton;
        if (shared.Player is { } player) player.StateChanged += UpdatePlayButton;
        // Debounce delays: 450 ms after the last edit before re-rendering the mask preview (a brush
        // stroke produces many Edited events), 600 ms after the last settings change before
        // re-analysing (typing a number fires ValueChanged per keystroke).
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => OnTick());
        timer.Start();
        editTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(450), DispatcherPriority.Background, (_, _) => { editTimer!.Stop(); OnEditSettled(); });
        analyseTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => { analyseTimer!.Stop(); _ = AnalyseAsync(); });
        SyncControls();
        UpdateInfo();
        UpdateSessionSoundButton();
        UpdatePlayButton();
        Say(Loc.S("Abre un sonido (o arrástralo aquí) para verlo como imagen. ¿Primera vez? Lee «?».", "Open a sound (or drag it here) to see it as a picture. First time? Read «?»."), false);
    }

    // ------------------------------------------------------------------------------------------
    //  IPaintRoom
    // ------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public string Title => "Spectrum Room";

    /// <inheritdoc/>
    /// <remarks>The room's private session (the analysed picture and its own history).</remarks>
    public PaintSession EditSession => session;

    /// <inheritdoc/>
    public string Tip => Loc.S("Convierte una grabación en imagen, edítala con los pinceles y vuelve a escucharla: quitar notas, limpiar ruidos, congelar, pintar encima.",
                               "Turns a recording into a picture, edit it with the brushes and listen again: remove notes, clean noises, freeze, paint over it.");

    /// <inheritdoc/>
    public Control Content => root;

    /// <summary>The canvas (tests drive it).</summary>
    public PaintCanvas Canvas => canvas;

    /// <summary>The room's private session (its image, history and selection).</summary>
    public PaintSession Session => session;

    /// <summary>The analysis of the current picture, or <c>null</c> before a sound was loaded.</summary>
    public SpectrumAnalysis? Analysis => AnalysisFor(session.Image);

    /// <summary>True while a background job (load, analysis, render) runs.</summary>
    public bool IsBusy => work is not null;

    /// <inheritdoc/>
    public void OnActivated()
    {
        SyncControls();
        UpdateSessionSoundButton();
        canvas.Focus();
    }

    /// <inheritdoc/>
    public void OnDeactivated() => StopAll();

    /// <inheritdoc/>
    /// <remarks>
    /// Ctrl shortcuts are checked first, so Ctrl+A (select all) and Ctrl+O (open) win over the
    /// plain-letter tool keys A (airbrush) and O (ellipse). Every key is ignored while typing.
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
            case Key.Space: TogglePlay(null); return true;
            case Key.Z when ctrl && shift: session.Redo(); return true;   // Ctrl+Shift+Z = redo, like Ctrl+Y
            case Key.Z when ctrl: session.Undo(); return true;
            case Key.Y when ctrl: session.Redo(); return true;
            case Key.C when ctrl: session.Copy(); Say(Loc.S("Copiado.", "Copied."), false); return true;
            case Key.X when ctrl: session.Copy(); session.DeleteSelection(); return true;
            // Paste at the yellow cursor's column, at the selection's lowest row (or row 0, the lowest pitch).
            case Key.V when ctrl: session.Paste((int)Math.Round(canvas.CursorColumn), session.Selection?.Bounds.Y0 ?? 0); return true;
            case Key.A when ctrl: session.ChangeSelection(CellRect.All(session.Image)); return true;
            case Key.Delete or Key.Back: session.DeleteSelection(); return true;
            case Key.Escape: session.ChangeSelection(null); return true;
            case Key.Home: canvas.CursorColumn = 0; return true;
            // [ and ]: brush size in geometric steps of 1.25× (feels even at any size).
            case Key.OemOpenBrackets: sizeSlider.Value = Math.Max(sizeSlider.Minimum, sizeSlider.Value / 1.25); return true;
            case Key.OemCloseBrackets: sizeSlider.Value = Math.Min(sizeSlider.Maximum, sizeSlider.Value * 1.25); return true;
            case Key.D0 when ctrl: canvas.ZoomToFit(); return true;
            case Key.O when ctrl: _ = OpenSoundAsync(); return true;
        }
        if (ctrl || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return false;
        PaintTool? tool = e.Key switch
        {
            Key.P => PaintTool.Pencil, Key.A => PaintTool.Airbrush, Key.S => PaintTool.Spray, Key.L => PaintTool.Line,
            Key.R => PaintTool.Rectangle, Key.O => PaintTool.Ellipse, Key.G => PaintTool.Gradient, Key.E => PaintTool.Eraser,
            Key.B => PaintTool.Blur, Key.U => PaintTool.Smudge,
            // M = rectangle, Shift+M = ellipse, F = lasso, N = spline selection.
            _ => PaintSelectionUi.ToolForKey(e.Key, shift),
        };
        if (tool is { } t) { SelectTool(t); return true; }
        return false;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Called by <see cref="PaintRoomsWindow"/> when it closes: stops the timers and any background
    /// job, stops what this room is playing, detaches from both sessions and deletes the preview WAVs.
    /// </remarks>
    public void Dispose()
    {
        timer.Stop();
        editTimer.Stop();
        analyseTimer.Stop();
        work?.Cancel();
        StopAll();
        session.ImageReplaced -= OnImageReplaced;
        session.PixelsChanged -= OnPixelsChanged;
        session.Edited -= OnEdited;
        shared.SoundChanged -= UpdateSessionSoundButton;
        if (shared.Player is { } player) player.StateChanged -= UpdatePlayButton;
        canvas.Session = null;
        // The preview files: the last one may still be open in diskin2 for a moment; best effort.
        try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, recursive: true); } catch { /* in use: left for the OS temp cleanup */ }
    }

    // ------------------------------------------------------------------------------------------
    //  Palette
    // ------------------------------------------------------------------------------------------

    /// <summary>Builds the left column: 14 tool buttons (the four selection tools first; the harmonics brush is a Paint Synth tool), the brush sliders and the selection options (feather).</summary>
    /// <returns>A vertically scrolling column (scrolls on short windows).</returns>
    /// <remarks>Ordered by usefulness for spectral editing: selection and eraser first.</remarks>
    Control BuildPalette()
    {
        // (tool, glyph on the button, Spanish tip, English tip, keyboard shortcut)
        (PaintTool Tool, string Glyph, string Es, string En, string Key)[] tools =
        [
            (PaintTool.Select, "⬚", "Selección rectangular: arrastra un rectángulo (o arrastra en la regla de alturas para una banda). Luego Solo, Quitar, Ganancia o Extraer." + PaintSelectionUi.ModifierHint, "Rectangle selection: drag a rectangle (or drag on the pitch ruler for a band). Then Solo, Remove, Gain or Extract." + PaintSelectionUi.ModifierHint, "M"),
            .. PaintSelectionUi.ShapedTools(),
            (PaintTool.Eraser, "⌫", "Goma: borra lo que toca; con la máscara, eso desaparece del sonido (también: botón derecho).", "Eraser: erases what it touches; with the mask, that disappears from the sound (also: right button).", "E"),
            (PaintTool.Airbrush, "◍", "Aerógrafo: sube suavemente el nivel (con la máscara: realza esa zona).", "Airbrush: softly raises the level (with the mask: boosts that region).", "A"),
            (PaintTool.Pencil, "✎", "Lápiz: pincel de borde duro.", "Pencil: hard-edged brush.", "P"),
            (PaintTool.Spray, "⁘", "Spray: puntos al azar.", "Spray: random dots.", "S"),
            (PaintTool.Line, "╱", "Línea.", "Line.", "L"),
            (PaintTool.Rectangle, "▭", "Rectángulo relleno.", "Filled rectangle.", "R"),
            (PaintTool.Ellipse, "◯", "Elipse rellena.", "Filled ellipse.", "O"),
            (PaintTool.Gradient, "◩", "Degradado (en la selección o toda la imagen).", "Gradient (in the selection or the whole picture).", "G"),
            (PaintTool.Blur, "≈", "Desenfocar bajo el pincel.", "Blur under the brush.", "B"),
            (PaintTool.Smudge, "☁", "Difuminar: arrastra la imagen.", "Smudge: drags the picture along.", "U"),
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
            // Automation name = enum name: screen readers and UI tests find the button by it.
            Avalonia.Automation.AutomationProperties.SetName(b, t.Tool.ToString());
            var tool = t.Tool;   // local copy for the closure
            b.Click += (_, _) => SelectTool(tool);
            toolButtons[tool] = b;
            grid.Children.Add(b);
        }
        ToolTip.SetTip(sizeSlider, Loc.S("Tamaño del pincel en píxeles de pantalla (también [ y ]).", "Brush size in screen pixels (also [ and ])."));
        ToolTip.SetTip(opacitySlider, Loc.S("Brillo de la pintura (1 = 0 dB, la mitad = la mitad del rango en dB).", "Paint brightness (1 = 0 dB, half = half the dB range)."));
        ToolTip.SetTip(hardSlider, Loc.S("Dureza del borde.", "Edge hardness."));
        ToolTip.SetTip(panSlider, Loc.S("Capa que pintas: −1 = solo izquierda (rojo), 0 = ambas, +1 = solo derecha (verde).", "Layer you paint: −1 = left only (red), 0 = both, +1 = right only (green)."));
        // Every brush slider also shows the brush ON the canvas for a moment (real size, hardness,
        // level, pan colour): the pointer is on the slider, so the usual circle under the mouse
        // would not be visible.
        foreach (var s in new[] { sizeSlider, opacitySlider, hardSlider, panSlider })
            s.ValueChanged += (_, _) => { OnBrushChanged(); canvas.ShowParameterPreview(PaintCanvas.ParameterPreview.Brush); };
        var panButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (label, v, es, en) in new[] { ("L", -1.0, "Solo izquierda", "Left only"), ("C", 0.0, "Ambos lados", "Both sides"), ("R", 1.0, "Solo derecha", "Right only") })
        {
            var b = new Button { Content = label, Width = 44, Padding = new Thickness(0, 3), HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(b, Loc.S(es, en));
            b.Click += (_, _) => panSlider.Value = v;
            panButtons.Children.Add(b);
        }
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
        stack.Children.Add(new TextBlock
        {
            Text = Loc.S("Arrastra en la regla de alturas (izquierda del lienzo) para seleccionar una banda de frecuencias.",
                         "Drag on the pitch ruler (left of the canvas) to select a frequency band."),
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = AppTheme.Brush("TextDim"), Margin = new Thickness(2, 10, 0, 0),
        });
        // The feather is shared with Paint Synth (stored in the shared settings).
        selectionPanel = PaintSelectionUi.OptionsPanel(canvas, shared.Settings);
        stack.Children.Add(selectionPanel);
        OnBrushChanged();
        SelectTool(PaintTool.Select);
        return new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    /// <summary>Makes <paramref name="tool"/> current on the canvas and in the palette.</summary>
    /// <param name="tool">The tool to select; its palette button is checked, all others unchecked.</param>
    void SelectTool(PaintTool tool)
    {
        canvas.Tool = tool;
        foreach (var (t, b) in toolButtons) b.IsChecked = t == tool;
        if (selectionPanel is not null) selectionPanel.IsVisible = PaintCanvas.IsSelectionTool(tool);
        canvas.InvalidateVisual();
    }

    /// <summary>Copies the brush sliders into the canvas brush and refreshes their captions (level shown in dB).</summary>
    /// <remarks>
    /// Also called after a new analysis (<see cref="OnImageReplaced"/>), because the dB shown for the
    /// level slider depends on the floor of the current picture's analysis (<see cref="RangeDb"/>).
    /// The pan caption treats |pan| ≤ 0.02 as centred.
    /// </remarks>
    void OnBrushChanged()
    {
        canvas.Brush = new BrushSettings { Size = sizeSlider.Value, Opacity = opacitySlider.Value, Hardness = hardSlider.Value, Pan = panSlider.Value };
        sizeText.Text = $"{sizeSlider.Value:0} px";
        opacityText.Text = $"{SpectralAnalysis.DbFromLevel(opacitySlider.Value, RangeDb):0} dB";
        hardText.Text = $"{hardSlider.Value * 100:0} %";
        double p = panSlider.Value;
        panText.Text = p < -0.02 ? Loc.S($"{-p * 100:0} % izq.", $"{-p * 100:0} % L") : p > 0.02 ? Loc.S($"{p * 100:0} % der.", $"{p * 100:0} % R") : Loc.S("ambos", "both");
    }

    // ------------------------------------------------------------------------------------------
    //  Loading and analysis
    // ------------------------------------------------------------------------------------------

    /// <summary>dB range of the current picture's levels (−floor of its analysis, or of the settings).</summary>
    double RangeDb => AnalysisFor(session.Image)?.RangeDb ?? -sp.FloorDb;

    /// <summary>The analysis linked to <paramref name="img"/>, else the latest one (images made by Replace operations).</summary>
    /// <param name="img">A picture of the private session (current or from the history).</param>
    /// <returns>Its analysis; <see cref="current"/> for unlinked images; <c>null</c> before the first analysis.</returns>
    /// <remarks>
    /// The fallback assumes an unlinked image (e.g. produced by a «Replace» image operation, which
    /// creates a new image object) belongs to the latest analysis. Its size may differ from that
    /// analysis' original: <see cref="SpectralMask.BuildGains"/> resamples the original to the edited
    /// geometry in that case.
    /// </remarks>
    SpectrumAnalysis? AnalysisFor(SpectralImage img) => analyses.TryGetValue(img, out var a) ? a : current;

    /// <summary>«Open sound…»: file picker with the supported formats.</summary>
    async Task OpenSoundAsync()
    {
        var files = await ctx.Window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.S("Abrir sonido", "Open sound"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Loc.S("Sonidos", "Sounds")) { Patterns = SoundFileLoader.Extensions.Select(e => "*" + e).ToArray() },
                new FilePickerFileType("WAV") { Patterns = ["*.wav"] },
            ],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            await LoadFileAsync(path);
    }

    /// <summary>Loads a sound file on a background task (Csound decodes non-WAV formats) and analyses it.</summary>
    /// <param name="path">Local path of a WAV/AIFF/FLAC/OGG/MP3 file.</param>
    /// <returns>A task that completes after loading AND the follow-up analysis (or a failure message).</returns>
    /// <remarks>
    /// Public for tests. Two jobs in a row: the load job ends (<see cref="EndWork"/>) before
    /// <see cref="AnalyseAsync"/> begins its own, so Cancel applies to whichever is running.
    /// </remarks>
    public async Task LoadFileAsync(string path)
    {
        StopAll();
        var token = BeginWork(Loc.S($"Abriendo {Path.GetFileName(path)}…", $"Opening {Path.GetFileName(path)}…"), indeterminate: true);
        try
        {
            var loaded = await SoundFileLoader.LoadAsync(path, token);
            sound = loaded;
            EndWork();
            if (loaded.DecodedByCsound)
                Say(Loc.S($"{Path.GetFileName(path)}: decodificado con Csound (libsndfile).", $"{Path.GetFileName(path)}: decoded with Csound (libsndfile)."), false);
            await AnalyseAsync();
        }
        catch (OperationCanceledException)
        {
            EndWork();
            Say(Loc.S("Cancelado.", "Cancelled."), true);
        }
        catch (Exception ex)
        {
            EndWork();
            Say(Loc.S("No se pudo abrir: ", "Could not open: ") + ex.Message, true);
        }
    }

    /// <summary>«Use session sound»: analyses the sound another room left in the session.</summary>
    void UseSessionSound()
    {
        if (shared.Sound is not { } s)
        {
            Say(Loc.S("No hay sonido en la sesión todavía.", "There is no sound in the session yet."), true);
            return;
        }
        StopAll();
        sound = new LoadedSound(s, Loc.S("sonido de la sesión", "session sound"), false);
        _ = AnalyseAsync();
    }

    /// <summary>Loads audio already in memory (tests, other rooms) and analyses it.</summary>
    /// <param name="audio">The samples (not copied: the room keeps a reference).</param>
    /// <param name="name">Display name, also used for output file names.</param>
    /// <returns>The analysis task.</returns>
    public Task LoadAudioAsync(AudioData audio, string name)
    {
        StopAll();
        sound = new LoadedSound(audio, name, false);
        return AnalyseAsync();
    }

    /// <summary>
    /// Analysis settings or axis changed: re-analyse 0.6 s after the last change when a sound is
    /// loaded (typing «16000» in a number box must not start four analyses).
    /// </summary>
    void AnalysisSettingsChanged()
    {
        // The re-analysis this schedules belongs to the settings step just recorded (which also
        // remembers the picture before it), so its picture replacement is not a step of its own.
        analysisFromSettings = true;
        SyncControls();
        if (sound is null) return;
        analyseTimer.Stop();
        analyseTimer.Start();
    }

    /// <summary>
    /// Analyses the loaded sound on a background task with the current settings and axis, then makes
    /// the result the room's picture (undoable) and links it to its analysis.
    /// </summary>
    /// <returns>A task that completes when the picture is in place (or the job failed / was cancelled).</returns>
    /// <remarks>
    /// The axis and settings are captured on the UI thread before <c>Task.Run</c>, so changing the
    /// controls during the analysis cannot affect it (it schedules a new analysis instead). Progress
    /// callbacks arrive on the UI thread (<see cref="Progress{T}"/> captured the UI context) and are
    /// ignored once the job was cancelled, so a superseded job cannot move the newer job's bar.
    /// </remarks>
    public async Task AnalyseAsync()
    {
        if (sound is not { } snd)
        {
            Say(Loc.S("Primero abre un sonido.", "Open a sound first."), true);
            return;
        }
        StopAll();
        var axis = sp.BuildAxis(shared.Settings.ScalaPath, shared.Settings.BaseHz, out string? axisError);
        if (axisError is not null) Say(Loc.S("Eje no válido, uso logarítmico: ", "Invalid axis, using logarithmic: ") + axisError, true);
        var settings = sp.Analysis;
        string name = Path.GetFileName(snd.Path);
        // Started by a settings change (see AnalysisSettingsChanged)? Then the result is not an
        // undo step of its own, and it is dropped if that change was undone meanwhile.
        bool fromSettings = analysisFromSettings;
        analysisFromSettings = false;
        int epoch = settingsEpoch;
        var token = BeginWork(Loc.S("Analizando…", "Analysing…"), indeterminate: false);
        var progress = new Progress<double>(p => { if (!token.IsCancellationRequested) progressBar.Value = p; });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var an = await Task.Run(() => SpectralAnalysis.Analyze(snd.Audio, name, axis, settings, progress, token), token);
            sw.Stop();
            EndWork();
            if (fromSettings && epoch != settingsEpoch) return;   // its settings were undone meanwhile
            current = an;
            var img = an.Original.Clone();       // the picture the user edits; the original stays untouched
            // Link BEFORE ReplaceImage: its ImageReplaced handler already looks the analysis up.
            analyses.AddOrUpdate(img, an);
            session.Selection = null;
            session.ReplaceImage(img, "analyse", undoable: !fromSettings);
            session.MarkPictureSaved();   // a fresh analysis can always be made again: nothing to lose yet
            session.ImageName = Path.GetFileNameWithoutExtension(name);
            canvas.CursorColumn = 0;
            canvas.ZoomToFit();
            Say(Loc.S($"✓ Analizado en {sw.Elapsed.TotalSeconds:0.0} s: {img.Width}×{img.Height}. Edita la imagen y presiona Tocar.",
                      $"✓ Analysed in {sw.Elapsed.TotalSeconds:0.0} s: {img.Width}×{img.Height}. Edit the picture and press Play."), false);
        }
        catch (OperationCanceledException)
        {
            // A newer job replaced this one (or Cancel was pressed): nothing to do.
            if (work is null || work.Token == token) { EndWork(); Say(Loc.S("Análisis cancelado.", "Analysis cancelled."), true); }
        }
        catch (Exception ex)
        {
            EndWork();
            Say(Loc.S("El análisis falló: ", "The analysis failed: ") + ex.Message, true);
        }
    }

    // ------------------------------------------------------------------------------------------
    //  Background work bookkeeping
    // ------------------------------------------------------------------------------------------

    /// <summary>Cancels the previous job and starts a new one: shows the progress bar and Cancel, returns the job's token.</summary>
    /// <param name="message">Status message shown while the job runs.</param>
    /// <param name="indeterminate">True for jobs without measurable progress (file loading).</param>
    /// <returns>The token the job must observe.</returns>
    /// <remarks>
    /// Only one job at a time: the previous token is cancelled (its source is not disposed; it is
    /// left to the GC). A job that ends must call <see cref="EndWork"/> only if it is still the
    /// current job (<c>work.Token == token</c>), otherwise it would hide the newer job's progress —
    /// <see cref="AnalyseAsync"/> and the mask preview check this, the other jobs do not.
    /// </remarks>
    CancellationToken BeginWork(string message, bool indeterminate)
    {
        work?.Cancel();
        work = new CancellationTokenSource();
        progressBar.IsIndeterminate = indeterminate;
        progressBar.Value = 0;
        progressBar.IsVisible = true;
        cancelButton.IsVisible = true;
        Say(message, false);
        return work.Token;
    }

    /// <summary>Ends the current job (hides the progress bar and Cancel).</summary>
    void EndWork()
    {
        work = null;
        progressBar.IsVisible = false;
        cancelButton.IsVisible = false;
    }

    // ------------------------------------------------------------------------------------------
    //  Session events
    // ------------------------------------------------------------------------------------------

    /// <summary>A new picture object (analysis, operation, undo of those): stop playing it, refresh the info and the strip.</summary>
    /// <remarks>
    /// Undo across analyses lands here too: the waveform switches to the sound of the analysis the
    /// restored image belongs to, and the brush captions to its dB floor.
    /// </remarks>
    void OnImageReplaced()
    {
        StopAll();
        var an = AnalysisFor(session.Image);
        strip.Audio = an?.Audio;
        UpdateInfo();
        OnBrushChanged();
    }

    /// <summary>Pixels changed: forward to the additive play (its converted copy follows).</summary>
    /// <param name="x0">First changed column.</param>
    /// <param name="x1">Column after the last changed one (exclusive).</param>
    /// <remarks>
    /// Raised during a brush stroke, many times a second, so only the changed columns are converted
    /// to Paint Synth levels and pushed to the live player together with the changed range.
    /// Ignored for the mask path, which re-renders on <see cref="OnEdited"/> instead.
    /// </remarks>
    void OnPixelsChanged(int x0, int x1)
    {
        if (playing != Playing.Additive || additiveImage is null || !ReferenceEquals(additiveSource, session.Image)) return;
        SpectralAnalysis.ToSynthLevels(session.Image, additiveImage, x0, x1, RangeDb, shared.Synth.Curve);
        if (playSolo is { } s) ZeroOutside(additiveImage, s, x0, x1);
        shared.Player?.OnImageEdited(additiveImage, x0, x1);
    }

    /// <summary>A complete edit: while the mask preview plays, re-render it shortly (debounced).</summary>
    void OnEdited()
    {
        if (playing == Playing.Mask)
        {
            editTimer.Stop();
            editTimer.Start();
        }
    }

    /// <summary>The edits settled while the mask preview plays: re-render and resume at the current position.</summary>
    void OnEditSettled()
    {
        if (playing != Playing.Mask) return;
        // Resume where the preview is now (seconds), or at the yellow cursor if the player has no position.
        double from = preview.CurrentSeconds ?? canvas.CursorColumn * session.Image.SecondsPerColumn;
        _ = PlayMaskAsync(playSolo, from);
    }

    // ------------------------------------------------------------------------------------------
    //  Playback
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Play / Stop (button, Space, Solo). <paramref name="solo"/> = play only that region (from its
    /// start); null = everything from the yellow cursor.
    /// </summary>
    /// <param name="solo">Selection to solo (any shape; soft edges fade), or <c>null</c>.</param>
    void TogglePlay(SelectionMask? solo)
    {
        if (playing != Playing.None || pendingStart is not null)
        {
            StopAll();
            Say(Loc.S("■ Detenido.", "■ Stopped."), false);
            return;
        }
        if (AnalysisFor(session.Image) is null)
        {
            Say(Loc.S("Primero abre un sonido.", "Open a sound first."), true);
            return;
        }
        var img = session.Image;
        // Start column (fractional): the solo region's left edge, else the cursor — unless the
        // cursor sits on the last column (left there by a previous play), then from the start.
        double fromCol = solo is { } s ? s.Bounds.X0 : canvas.CursorColumn >= img.Width - 1 ? 0 : canvas.CursorColumn;
        if (sp.Mode == SpectrumResynthesis.SpectralMask)
            _ = PlayMaskAsync(solo, fromCol * img.SecondsPerColumn);
        else
            PlayAdditive(img, solo, fromCol);
    }

    /// <summary>
    /// Mask preview: renders (or reuses) the masked sound to a temporary stereo WAV on a background
    /// task, then plays it through Csound from <paramref name="fromSeconds"/>.
    /// </summary>
    /// <param name="solo">Region to keep (everything else silenced), or <c>null</c>.</param>
    /// <param name="fromSeconds">Start position in the sound, seconds.</param>
    /// <remarks>
    /// The rendered file is cached by (image object, edit version, solo): pressing Play again without
    /// editing replays it instantly. The preview is a 32-bit float stereo WAV at the sound's rate,
    /// lowered to a 0.99 peak if the edits boosted it beyond full scale.
    /// </remarks>
    async Task PlayMaskAsync(SelectionMask? solo, double fromSeconds)
    {
        var img = session.Image;
        if (AnalysisFor(img) is not { } an) return;
        playSolo = solo;
        // Reuse the previous preview only if it was made from exactly this state and still exists.
        string? file = previewKey is { } k && ReferenceEquals(k.Image, img) && k.Version == img.Version && ReferenceEquals(k.Solo, solo) && previewFile is not null && File.Exists(previewFile)
            ? previewFile : null;
        if (file is null)
        {
            var snapshot = img.Clone();                         // UI thread: the user may keep editing
            long version = img.Version;
            var token = BeginWork(Loc.S("Preparando la escucha (máscara espectral)…", "Preparing the preview (spectral mask)…"), indeterminate: false);
            var progress = new Progress<double>(p => { if (!token.IsCancellationRequested) progressBar.Value = p; });
            Directory.CreateDirectory(tempFolder);
            string path = Path.Combine(tempFolder, $"preview_{++tempCounter}.wav");
            try
            {
                double scaled = await Task.Run(() =>
                {
                    // Background thread: works only on the snapshot and the immutable analysis.
                    var gains = SpectralMask.BuildGains(an.Original, snapshot, an.RangeDb, solo);
                    var result = SpectralMask.Apply(an, gains, progress, token);
                    // Preview safety: never send more than full scale to the output (boosts can exceed it).
                    double peak = result.Peak(), g = peak > 0.99 ? 0.99 / peak : 1;
                    var stereo = SpectralMask.AsStereo(result).Select(c => g == 1 ? c : c.Select(v => (float)(v * g)).ToArray()).ToArray();
                    WavIO.WriteFloat32(path, stereo, result.SampleRate);
                    return 20 * Math.Log10(g);   // the safety gain in dB (0 = untouched), for the message below
                }, token);
                EndWork();
                CleanOldPreviews(path);
                previewFile = path;
                // Keyed with the version captured BEFORE rendering: an edit made during the render
                // bumps img.Version, so the next Play renders again.
                previewKey = (img, version, solo);
                file = path;
                if (scaled < -0.05) Say(Loc.S($"La escucha se bajó {scaled:0.0} dB para no saturar.", $"The preview was lowered {scaled:0.0} dB to avoid clipping."), true);
            }
            catch (OperationCanceledException)
            {
                if (work is null || work.Token == token) EndWork();
                return;
            }
            catch (Exception ex)
            {
                EndWork();
                Say(Loc.S("No se pudo preparar la escucha: ", "Could not prepare the preview: ") + ex.Message, true);
                return;
            }
        }
        if (!ReferenceEquals(session.Image, img)) return;       // the picture changed meanwhile
        double seconds = an.Audio.Seconds;
        StartPlayer(Playing.Mask, () =>
        {
            if (preview.Play(file, fromSeconds, seconds))
            {
                playing = Playing.Mask;
                Say(solo is null ? Loc.S("▶ Máscara espectral: la grabación original filtrada por tu imagen.", "▶ Spectral mask: the original recording filtered by your picture.")
                                 : Loc.S("▶ Solo de la selección.", "▶ Solo of the selection."), false);
            }
            UpdatePlayButton();
        });
    }

    /// <summary>Additive play: the picture (converted to Paint Synth levels) through the shared live player.</summary>
    /// <param name="img">The room picture (perceptual dB levels).</param>
    /// <param name="solo">Region to keep, or <c>null</c>.</param>
    /// <param name="fromColumn">Start column (fractional).</param>
    /// <remarks>
    /// The room's levels are linear in dB, Paint Synth's follow its own loudness curve, so the
    /// picture is converted into a separate image (<see cref="additiveImage"/>) that the player
    /// uploads to Csound; later brush strokes are mirrored into it by <see cref="OnPixelsChanged"/>.
    /// </remarks>
    void PlayAdditive(SpectralImage img, SelectionMask? solo, double fromColumn)
    {
        if (shared.Player is not { } player)
        {
            Say(Loc.S("Csound no está disponible.", "Csound is not available."), true);
            return;
        }
        playSolo = solo;
        additiveSource = img;
        additiveImage = SpectralAnalysis.ToSynthLevels(img, RangeDb, shared.Synth.Curve);
        if (solo is { } s) ZeroOutside(additiveImage, s, 0, additiveImage.Width);
        var image = additiveImage;
        var synth = AdditiveSynth();
        StartPlayer(Playing.Additive, () =>
        {
            if (player.Play(image, synth, fromColumn))
            {
                playing = Playing.Additive;
                Say(Loc.S("▶ Aditiva: Csound toca la imagen con el motor de Paint Synth.", "▶ Additive: Csound plays the picture with the Paint Synth engine."), false);
            }
            UpdatePlayButton();
        });
    }

    /// <summary>
    /// Runs <paramref name="start"/> now, or — when the OTHER player (mask preview vs. live image
    /// player) was sounding — stops it and runs <paramref name="start"/> 0.4 s later, once Csound
    /// has settled (stopping one player may stop the «Paint Rooms host» orchestra it started; the
    /// next player then starts it again cleanly instead of launching into a stopping performance).
    /// </summary>
    /// <param name="kind">Which player <paramref name="start"/> will use.</param>
    /// <param name="start">Starts the player and sets <see cref="playing"/> on success (UI thread).</param>
    void StartPlayer(Playing kind, Action start)
    {
        bool otherBusy = kind == Playing.Mask
            ? shared.Player?.State is PaintPlayState.Playing or PaintPlayState.Starting
            : preview.State != PaintPlayState.Idle;
        if (!otherBusy)
        {
            start();
            return;
        }
        if (kind == Playing.Mask) shared.Player?.Stop(); else preview.Stop();
        playing = Playing.None;
        pendingStart = start;
        pendingSince = DateTime.UtcNow;
    }

    /// <summary>Stops whatever this room started (mask preview or additive play) and cancels a pending start.</summary>
    /// <remarks>
    /// The shared player is stopped only if THIS room started it (<see cref="Playing.Additive"/>):
    /// Paint Synth may be using it. The null-conditional on <see cref="editTimer"/> is defensive:
    /// control handlers wired in the constructor could in principle fire before the timer is created.
    /// </remarks>
    void StopAll()
    {
        pendingStart = null;
        editTimer?.Stop();
        if (preview.State != PaintPlayState.Idle) preview.Stop();
        if (playing == Playing.Additive && shared.Player is { State: not PaintPlayState.Idle } p) p.Stop();
        playing = Playing.None;
        canvas.PlayColumn = null;
        strip.PlaySeconds = null;
        UpdatePlayButton();
    }

    /// <summary>The Paint Synth settings used by the additive path (the shared ones, one pass, the chosen voice).</summary>
    /// <returns>A copy (record <c>with</c>): the shared settings themselves are not modified.</returns>
    PaintSynthSettings AdditiveSynth() => shared.Synth with { Mode = sp.AdditiveMode, Loop = false };

    /// <summary>Keeps only the selection in columns [x0, x1) (solo): every level is multiplied by the cell's coverage.</summary>
    /// <param name="img">Image modified in place (both stereo layers); Paint Synth levels.</param>
    /// <param name="keep">The selection: coverage 1 keeps a cell, 0 silences it, a soft rim lowers it.</param>
    /// <param name="x0">First column to process.</param>
    /// <param name="x1">Column after the last one to process.</param>
    static void ZeroOutside(SpectralImage img, SelectionMask keep, int x0, int x1)
    {
        // Column-major storage: cell (x, y) is at index x·height + y.
        int h = img.Height;
        for (int x = x0; x < x1; x++)
            for (int y = 0; y < h; y++)
            {
                float c = keep[x, y];
                if (c >= 1) continue;
                img.Left[x * h + y] *= c;
                img.Right[x * h + y] *= c;
            }
    }

    /// <summary>~30 Hz: drives the players, runs a pending start, moves the play lines.</summary>
    /// <remarks>
    /// The players have no events for «finished»: they are polled here, and a player found idle
    /// while <see cref="playing"/> says otherwise means it ended (or Csound stopped). Play positions
    /// are converted between seconds (mask preview, waveform strip) and columns (canvas) with
    /// <see cref="SpectralImage.SecondsPerColumn"/>.
    /// </remarks>
    void OnTick()
    {
        preview.Tick();
        if (pendingStart is { } start && DateTime.UtcNow - pendingSince > TimeSpan.FromMilliseconds(400))
        {
            pendingStart = null;
            start();
        }
        if (playing == Playing.Additive)
        {
            var player = shared.Player;
            player?.Tick();
            if (player is null || player.State == PaintPlayState.Idle) { playing = Playing.None; UpdatePlayButton(); }
            double? col = player?.CurrentColumn;
            canvas.PlayColumn = col;
            strip.PlaySeconds = col * session.Image.SecondsPerColumn;
        }
        else if (playing == Playing.Mask)
        {
            if (preview.State == PaintPlayState.Idle) { playing = Playing.None; UpdatePlayButton(); }
            double? t = preview.CurrentSeconds;
            strip.PlaySeconds = t;
            canvas.PlayColumn = t / session.Image.SecondsPerColumn;
        }
        else if (canvas.PlayColumn is not null)
        {
            canvas.PlayColumn = null;
            strip.PlaySeconds = null;
        }
    }

    /// <summary>Shows Play or Stop on the transport button.</summary>
    /// <remarks>
    /// «Stop» also while a start is pending or the preview player is still starting Csound, so a
    /// second press cancels instead of starting twice. Subscribed to both players' StateChanged
    /// and also called after every start/stop.
    /// </remarks>
    void UpdatePlayButton()
    {
        bool on = playing != Playing.None || pendingStart is not null || preview.State == PaintPlayState.Starting;
        playButton.Content = ToolUi.Content(on ? Loc.S("Detener", "Stop") : Loc.S("Tocar", "Play"), on ? "IconStop" : "IconPlay");
    }

    /// <summary>Deletes older preview files (keeps <paramref name="keep"/> and the one playing).</summary>
    /// <param name="keep">The file just rendered.</param>
    /// <remarks>Best effort: files still open in Csound are retried at the next call or on dispose.</remarks>
    void CleanOldPreviews(string keep)
    {
        try
        {
            foreach (string f in Directory.GetFiles(tempFolder, "preview_*.wav"))
                if (!string.Equals(f, keep, StringComparison.OrdinalIgnoreCase) && !string.Equals(f, preview.File, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { /* still open in Csound: next time */ }
        }
        catch { /* folder gone */ }
    }

    // ------------------------------------------------------------------------------------------
    //  Rendering, extraction, room tools
    // ------------------------------------------------------------------------------------------

    /// <summary>«Render WAV»: the whole result with the chosen resynthesis.</summary>
    /// <remarks>The save dialog comes from the main view model (<c>PickSaveFile</c>); the suggested name carries the mode («_mask» / «_additive»).</remarks>
    async Task RenderAsync()
    {
        if (AnalysisFor(session.Image) is not { } an) { Say(Loc.S("Primero abre un sonido.", "Open a sound first."), true); return; }
        if (ctx.App.PickSaveFile is not { } pick) return;
        string suffix = sp.Mode == SpectrumResynthesis.SpectralMask ? "_mask" : "_additive";
        if (await pick(SafeName(session.ImageName) + suffix + ".wav", "wav") is not { } path) return;
        if (await WriteResultAsync(an, path, null)) session.MarkPictureSaved();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The edited picture exists only while the window is open, so closing would lose the edits made
    /// since the analysis (or since the last «Render WAV»).
    /// </remarks>
    public bool HasUnsavedChanges => session.HasUnsavedPicture && AnalysisFor(session.Image) is not null;

    /// <inheritdoc/>
    public string UnsavedName => Loc.S("el sonido editado del Spectrum Room (se guarda como WAV)", "the Spectrum Room's edited sound (saved as WAV)");

    /// <inheritdoc/>
    /// <remarks>«Save» = Render WAV of the whole result with the current resynthesis mode.</remarks>
    public async Task<bool> SaveChangesAsync()
    {
        if (AnalysisFor(session.Image) is not { } an) { Say(Loc.S("Primero abre un sonido.", "Open a sound first."), true); return true; }
        if (ctx.App.PickSaveFile is not { } pick) return true;
        string suffix = sp.Mode == SpectrumResynthesis.SpectralMask ? "_mask" : "_additive";
        if (await pick(SafeName(session.ImageName) + suffix + ".wav", "wav") is not { } path) return false;
        if (!await WriteResultAsync(an, path, null)) return false;
        session.MarkPictureSaved();
        return true;
    }

    /// <summary>«Extract WAV…»: only the selection.</summary>
    /// <remarks>
    /// Without a selection it only warns. The file keeps the full duration: everything outside the
    /// selection is silent.
    /// </remarks>
    async Task ExtractAsync()
    {
        if (AnalysisFor(session.Image) is not { } an) { Say(Loc.S("Primero abre un sonido.", "Open a sound first."), true); return; }
        if (session.Selection is null) { Warn(); return; }
        if (ctx.App.PickSaveFile is not { } pick) return;
        if (await pick(SafeName(session.ImageName) + "_extract.wav", "wav") is not { } path) return;
        await WriteResultAsync(an, path, session.Selection);
    }

    /// <summary>
    /// Writes the result to <paramref name="path"/> (24-bit): with the mask in C# on a background task,
    /// or additively through <see cref="PaintRenderer"/>. <paramref name="solo"/> limits it to a region.
    /// </summary>
    /// <param name="an">The analysis whose original sound the mask filters.</param>
    /// <param name="path">Destination WAV (overwritten).</param>
    /// <param name="solo">Region to keep, or <c>null</c> for everything.</param>
    /// <returns><c>true</c> when the file was written.</returns>
    /// <remarks>
    /// Public for tests. The picture is cloned on the UI thread first, so the user can keep
    /// editing during the job. The resynthesis mode is read once, at the start.
    /// </remarks>
    public async Task<bool> WriteResultAsync(SpectrumAnalysis an, string path, SelectionMask? solo)
    {
        var img = session.Image.Clone();
        bool normalize = sp.Normalize;
        if (sp.Mode == SpectrumResynthesis.SpectralMask)
        {
            var token = BeginWork(Loc.S("Escribiendo WAV (máscara espectral)…", "Writing WAV (spectral mask)…"), indeterminate: false);
            var progress = new Progress<double>(p => { if (!token.IsCancellationRequested) progressBar.Value = p; });
            try
            {
                double gainDb = await Task.Run(() =>
                {
                    var gains = SpectralMask.BuildGains(an.Original, img, an.RangeDb, solo);
                    var result = SpectralMask.Apply(an, gains, progress, token);
                    return SpectralMask.WriteWav24(path, result, normalize);
                }, token);
                EndWork();
                string msg = Loc.S($"✓ WAV guardado: {path} (ganancia {gainDb:+0.0;-0.0} dB)", $"✓ WAV saved: {path} (gain {gainDb:+0.0;-0.0} dB)");
                Say(msg, false);
                ctx.App.PaintInfo("Spectrum Room: " + msg);
                return true;
            }
            catch (OperationCanceledException) { EndWork(); Say(Loc.S("Cancelado.", "Cancelled."), true); }
            catch (Exception ex) { EndWork(); Say(Loc.S("✗ No se pudo escribir: ", "✗ Could not write: ") + ex.Message, true); }
            return false;
        }
        var synthImg = SpectralAnalysis.ToSynthLevels(img, an.RangeDb, shared.Synth.Curve);
        if (solo is { } s) ZeroOutside(synthImg, s, 0, synthImg.Width);
        return await RenderAdditiveAsync(synthImg, AdditiveSynth() with { NormalizeRender = normalize }, path);
    }

    /// <summary>Renders a Paint-Synth-level image to <paramref name="path"/> with Csound (progress, cancel).</summary>
    /// <param name="synthImg">Image already in Paint Synth levels.</param>
    /// <param name="synth">Synthesis settings (voice, envelope, normalisation).</param>
    /// <param name="path">Destination WAV.</param>
    /// <returns><c>true</c> when Csound rendered the file successfully.</returns>
    /// <remarks>
    /// <see cref="PaintRenderer"/> runs a private <c>CsoundEngine</c> (its own CSOUND* and thread),
    /// independent of the app's live engine, so a render does not interrupt what is playing.
    /// </remarks>
    async Task<bool> RenderAdditiveAsync(SpectralImage synthImg, PaintSynthSettings synth, string path)
    {
        var token = BeginWork(Loc.S("Renderizando con Csound…", "Rendering with Csound…"), indeterminate: false);
        var progress = new Progress<double>(p => { if (!token.IsCancellationRequested) progressBar.Value = p; });
        try
        {
            var r = await PaintRenderer.RenderAsync(synthImg, synth, path, progress, token);
            EndWork();
            if (!r.Success) { Say(Loc.S("✗ El render falló: ", "✗ The render failed: ") + (r.Errors.FirstOrDefault() ?? "?"), true); return false; }
            string msg = Loc.S($"✓ WAV guardado: {path} ({r.Seconds:0.0} s, Csound tardó {r.RenderSeconds:0.0} s)", $"✓ WAV saved: {path} ({r.Seconds:0.0} s, Csound took {r.RenderSeconds:0.0} s)");
            Say(msg, false);
            ctx.App.PaintInfo("Spectrum Room: " + msg);
            return true;
        }
        catch (OperationCanceledException) { EndWork(); Say(Loc.S("Render cancelado.", "Render cancelled."), true); }
        catch (Exception ex) { EndWork(); Say(Loc.S("✗ El render falló: ", "✗ The render failed: ") + ex.Message, true); }
        return false;
    }

    /// <summary>«Apply gain»: adds gain/|floor| to the levels of the selection (levels are linear in dB), one undo step.</summary>
    /// <remarks>
    /// Levels map linearly to dB over the analysis range (level = (dB − floor) / −floor), so a gain
    /// of g dB is a constant level offset g / range: e.g. +6 dB with a −90 dB floor adds 0.0667.
    /// Results are clamped to [0, 1]: boosts saturate at 0 dB, cuts bottom out at the floor.
    /// </remarks>
    void ApplyGain()
    {
        if (AnalysisFor(session.Image) is null) { Say(Loc.S("Primero abre un sonido.", "Open a sound first."), true); return; }
        double db = sp.GainDb, range = RangeDb;
        float delta = (float)(db / range);
        var op = new ImageOperation("spectrum.gain", "Ganancia", "Gain", "", "", null, OperationKind.InPlace, (img, r, _) =>
        {
            // Raising: every cell, even «silent» ones (their true level is below the floor and is raised too).
            // Lowering: cells reaching 0 count as erased.
            ImageOps.ForEach(img, r, v => Math.Clamp(v + delta, 0f, 1f));
            return null;
        });
        session.Apply(op, db);
        Say(Loc.S($"Ganancia {db:+0;-0;0} dB en {(session.Selection is null ? "toda la imagen" : "la selección")} (Ctrl+Z deshace).",
                  $"Gain {db:+0;-0;0} dB on {(session.Selection is null ? "the whole picture" : "the selection")} (Ctrl+Z undoes)."), false);
    }

    /// <summary>«→ Paint Synth»: converts the picture to Paint Synth levels and makes it the Paint Synth canvas (undoable there).</summary>
    void SendToPaintSynth() => SendImageToPaintSynth(session.Image, session.ImageName);

    /// <summary>Sends <paramref name="img"/> (perceptual levels) to the shared canvas, converted to Paint Synth levels.</summary>
    /// <param name="img">The room picture or a frozen tone made from it.</param>
    /// <param name="name">Name given to the Paint Synth canvas (used for its file names).</param>
    void SendImageToPaintSynth(SpectralImage img, string name)
    {
        if (AnalysisFor(session.Image) is null) { Say(Loc.S("Primero abre un sonido.", "Open a sound first."), true); return; }
        StopAll();
        var converted = SpectralAnalysis.ToSynthLevels(img, RangeDb, shared.Synth.Curve);
        shared.ReplaceImage(converted, "spectrum");
        shared.ImageName = name;
        Say(Loc.S("✓ Enviado a Paint Synth: abre esa pestaña para pintar encima (Ctrl+Z allá lo deshace).",
                  "✓ Sent to Paint Synth: open that tab to paint over it (Ctrl+Z there undoes it)."), false);
    }

    /// <summary>Freeze menu: length, play, render, send.</summary>
    /// <param name="menu">Fresh flyout to fill (rebuilt at every click so the length shown is current).</param>
    /// <remarks>
    /// A frozen tone gets at least 30 ms attack and 300 ms release so a sustained spectrum fades in
    /// and out instead of clicking.
    /// </remarks>
    void BuildFreezeMenu(MenuFlyout menu)
    {
        menu.Items.Add(Item(Loc.S($"Duración del tono: {sp.FreezeSeconds:0.#} s…", $"Tone length: {sp.FreezeSeconds:0.#} s…"), () => _ = AskFreezeLengthAsync()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Loc.S("▶ Tocar el instante congelado", "▶ Play the frozen moment"), () =>
        {
            if (MakeFreeze() is not { } f) return;
            StopAll();
            var synthImg = SpectralAnalysis.ToSynthLevels(f, RangeDb, shared.Synth.Curve);
            if (shared.Player is not { } player) return;
            // No source image: edits to the room picture are not forwarded to a frozen tone.
            additiveSource = null;
            additiveImage = synthImg;
            playSolo = null;
            StartPlayer(Playing.Additive, () =>
            {
                if (player.Play(synthImg, AdditiveSynth() with { AttackMs = Math.Max(30, shared.Synth.AttackMs), ReleaseMs = Math.Max(300, shared.Synth.ReleaseMs) }, 0))
                {
                    playing = Playing.Additive;
                    Say(Loc.S("▶ Instante congelado (motor de Paint Synth).", "▶ Frozen moment (Paint Synth engine)."), false);
                }
                UpdatePlayButton();
            });
        }));
        menu.Items.Add(Item(Loc.S("Renderizar el instante congelado a WAV…", "Render the frozen moment to WAV…"), () => _ = RenderFreezeAsync()));
        menu.Items.Add(Item(Loc.S("Instante congelado → Paint Synth", "Frozen moment → Paint Synth"), () =>
        {
            if (MakeFreeze() is { } f) SendImageToPaintSynth(f, session.ImageName + "_freeze");
        }));
    }

    /// <summary>Asks for the freeze length (seconds).</summary>
    async Task AskFreezeLengthAsync()
    {
        if (await PaintParameterDialog.AskAsync(ctx.Window, Loc.S("Congelar", "Freeze"), Loc.S("Duración (s)", "Length (s)"), 0.5, 600, sp.FreezeSeconds, 1, "0.#") is { } v)
            ChangeSettings(() => sp.FreezeSeconds = v, reanalyse: false);
    }

    /// <summary>The frozen image at the yellow cursor, or <c>null</c> (with a message) when there is no analysis.</summary>
    /// <returns>A new image: the cursor column's spectrum held for <see cref="SpectrumRoomSettings.FreezeSeconds"/>.</returns>
    /// <remarks>
    /// With a selection only what it covers is frozen: the picture is first multiplied by the
    /// selection's coverage (cells outside → silent, a soft rim → softer), so e.g. a lasso around
    /// one note's harmonics freezes just that note. If the cursor lies outside the selection's
    /// columns, the middle column of the selection is frozen instead.
    /// </remarks>
    SpectralImage? MakeFreeze()
    {
        if (AnalysisFor(session.Image) is null) { Say(Loc.S("Primero abre un sonido.", "Open a sound first."), true); return null; }
        var img = session.Image;
        int col = (int)Math.Clamp(Math.Floor(canvas.CursorColumn), 0, img.Width - 1);
        if (session.Selection is { } sel)
        {
            if (col < sel.Bounds.X0 || col >= sel.Bounds.X1) col = (sel.Bounds.X0 + sel.Bounds.X1) / 2;
            img = img.Clone();
            ZeroOutside(img, sel, 0, img.Width);
        }
        // 20 columns per second (50 ms each), clamped to 16…4096 columns.
        int width = (int)Math.Clamp(Math.Round(sp.FreezeSeconds * 20), 16, 4096);
        return SpectralAnalysis.Freeze(img, col, width, sp.FreezeSeconds, RangeDb);
    }

    /// <summary>Freeze › Render: the frozen tone through Csound to a WAV.</summary>
    async Task RenderFreezeAsync()
    {
        if (MakeFreeze() is not { } f || ctx.App.PickSaveFile is not { } pick) return;
        if (await pick(SafeName(session.ImageName) + "_freeze.wav", "wav") is not { } path) return;
        var synth = AdditiveSynth() with { AttackMs = Math.Max(30, shared.Synth.AttackMs), ReleaseMs = Math.Max(300, shared.Synth.ReleaseMs), NormalizeRender = sp.Normalize };
        await RenderAdditiveAsync(SpectralAnalysis.ToSynthLevels(f, RangeDb, shared.Synth.Curve), synth, path);
    }

    /// <summary>«Sound → session» menu: the original sound, or the edited one (rendered with the mask).</summary>
    /// <param name="menu">Fresh flyout to fill.</param>
    void BuildSessionMenu(MenuFlyout menu)
    {
        menu.Items.Add(Item(Loc.S("Sonido original", "Original sound"), () =>
        {
            if (AnalysisFor(session.Image) is not { } an) { Say(Loc.S("Primero abre un sonido.", "Open a sound first."), true); return; }
            shared.Sound = an.Audio;
            Say(Loc.S("✓ El sonido original quedó en la sesión (el Filtro de imagen puede usarlo).", "✓ The original sound is now in the session (the Image Filter can use it)."), false);
        }));
        menu.Items.Add(Item(Loc.S("Sonido editado (máscara espectral)", "Edited sound (spectral mask)"), () => _ = SendEditedSoundAsync()));
    }

    /// <summary>Renders the edited sound with the mask (background) and puts it in the session.</summary>
    /// <returns>A task that completes when <see cref="PaintSession.Sound"/> was set (or the job failed).</returns>
    /// <remarks>Always the mask path, whatever the chosen resynthesis: the session wants the faithful sound.</remarks>
    public async Task SendEditedSoundAsync()
    {
        if (AnalysisFor(session.Image) is not { } an) { Say(Loc.S("Primero abre un sonido.", "Open a sound first."), true); return; }
        var img = session.Image.Clone();
        var token = BeginWork(Loc.S("Calculando el sonido editado…", "Computing the edited sound…"), indeterminate: false);
        var progress = new Progress<double>(p => { if (!token.IsCancellationRequested) progressBar.Value = p; });
        try
        {
            var result = await Task.Run(() => SpectralMask.Apply(an, SpectralMask.BuildGains(an.Original, img, an.RangeDb), progress, token), token);
            EndWork();
            shared.Sound = result;
            Say(Loc.S("✓ El sonido editado quedó en la sesión.", "✓ The edited sound is now in the session."), false);
        }
        catch (OperationCanceledException) { EndWork(); Say(Loc.S("Cancelado.", "Cancelled."), true); }
        catch (Exception ex) { EndWork(); Say(ex.Message, true); }
    }

    /// <summary>Image menu from <see cref="ImageOps.All"/> (applies to the room's selection or picture).</summary>
    /// <param name="menu">Fresh flyout to fill; the first, disabled item says what the operations will affect.</param>
    /// <remarks>A separator is inserted wherever the operation group changes.</remarks>
    void BuildImageMenu(MenuFlyout menu)
    {
        string scope = session.Selection is null ? Loc.S("(toda la imagen)", "(whole picture)") : Loc.S("(selección)", "(selection)");
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

    /// <summary>Runs an image operation (asking for its parameter first).</summary>
    /// <param name="op">The operation; applied through the private session (one undo step).</param>
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

    // ------------------------------------------------------------------------------------------
    //  Band selection on the pitch ruler
    // ------------------------------------------------------------------------------------------

    /// <summary>Tunnel press on the canvas: a left press on the pitch ruler starts a band selection (all columns).</summary>
    /// <param name="sender">The canvas.</param>
    /// <param name="e">The press; handled (so the canvas does not see it) only when it starts a band.</param>
    void OnCanvasPressed(object? sender, PointerPressedEventArgs e)
    {
        // The pitch ruler is the strip x < RulerWidth on the left, below the time ruler (y ≥ RulerHeight).
        // Positions are canvas DIPs; ToImage converts them to fractional image (column, row).
        var p = e.GetPosition(canvas);
        if (p.X >= PaintCanvas.RulerWidth || p.Y < PaintCanvas.RulerHeight || !e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
        bandDrag = true;
        bandStartRow = canvas.ToImage(p).Y;
        SelectBand(bandStartRow, bandStartRow);
        // Capture: the drag keeps reporting even when the pointer leaves the ruler or the canvas.
        e.Pointer.Capture(canvas);
        e.Handled = true;
    }

    /// <summary>Tunnel move: extends the band while dragging; also remembers the pointer for the hover read-out.</summary>
    /// <param name="sender">The canvas.</param>
    /// <param name="e">The move; handled only during a band drag.</param>
    void OnCanvasMoved(object? sender, PointerEventArgs e)
    {
        lastPointer = e.GetPosition(canvas);
        if (!bandDrag) return;
        SelectBand(bandStartRow, canvas.ToImage(lastPointer.Value).Y);
        e.Handled = true;
    }

    /// <summary>Tunnel release: ends the band selection.</summary>
    /// <param name="sender">The canvas.</param>
    /// <param name="e">The release; handled only when it ends a band drag.</param>
    /// <remarks>Reports the band's frequency range: centre of the lowest row – centre of the highest row.</remarks>
    void OnCanvasReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!bandDrag) return;
        bandDrag = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        if (session.Selection is { Bounds: var s })
        {
            var axis = session.Image.Axis;
            Say(Loc.S($"Banda {PitchAxis.FormatHz(axis.FrequencyOf(s.Y0))} – {PitchAxis.FormatHz(axis.FrequencyOf(s.Y1 - 1))} Hz seleccionada: Solo, Quitar, Ganancia o Extraer.",
                      $"Band {PitchAxis.FormatHz(axis.FrequencyOf(s.Y0))} – {PitchAxis.FormatHz(axis.FrequencyOf(s.Y1 - 1))} Hz selected: Solo, Remove, Gain or Extract."), false);
        }
    }

    /// <summary>Selects rows between two fractional rows over the whole time.</summary>
    /// <param name="rowA">One end of the band (fractional row, any order).</param>
    /// <param name="rowB">The other end.</param>
    /// <remarks>
    /// Both rows the ends fall in are included (floor of the lower end … floor of the upper end, Y1
    /// exclusive) and the band is always at least one row, clamped to the image. Public for tests.
    /// The band is always a full-width RECTANGLE mask (crisp edges: a band filter should cut exactly
    /// where you dragged); it replaces the selection.
    /// </remarks>
    public void SelectBand(double rowA, double rowB)
    {
        var img = session.Image;
        int y0 = Math.Clamp((int)Math.Floor(Math.Min(rowA, rowB)), 0, img.Height - 1);
        int y1 = Math.Clamp((int)Math.Floor(Math.Max(rowA, rowB)) + 1, y0 + 1, img.Height);
        session.ChangeSelection(SelectionMask.Rectangle(new CellRect(0, y0, img.Width, y1)));
    }

    // ------------------------------------------------------------------------------------------
    //  Display helpers
    // ------------------------------------------------------------------------------------------

    /// <summary>Hover read-out with levels in dB (the canvas's own shows 0…1 levels).</summary>
    /// <returns>«time s · [note label ·] frequency Hz (note) · L dB R dB», or "" outside the picture.</returns>
    /// <remarks>
    /// Time is the column centre ((x + 0.5)·seconds per column); frequency is the row's centre
    /// frequency on the axis. Level 0 is shown as −∞ dB (erased / below the floor).
    /// </remarks>
    string HoverText()
    {
        if (lastPointer is not { } p) return "";
        var img = session.Image;
        var (x, y) = canvas.ToImage(p);
        int cx = (int)Math.Floor(x), cy = (int)Math.Floor(y);
        if (!img.Contains(cx, cy)) return "";
        double hz = img.Axis.FrequencyOf(cy), range = RangeDb;
        string Db(float v) => v <= 0 ? "−∞" : SpectralAnalysis.DbFromLevel(v, range).ToString("0", CultureInfo.InvariantCulture);
        string label = img.Axis.IsDiscrete ? img.Axis.LabelOf(cy) + " · " : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"{(cx + 0.5) * img.SecondsPerColumn:0.000} s  ·  {label}{hz.ToString(hz < 100 ? "0.00" : "0.0", CultureInfo.InvariantCulture)} Hz ({PitchAxis.NoteName(hz)})  ·  L {Db(img.GetLeft(cx, cy))} dB  R {Db(img.GetRight(cx, cy))} dB");
    }

    /// <summary>The information line above the waveform: file, rate, channels, duration, analysis resolution.</summary>
    void UpdateInfo()
    {
        if (AnalysisFor(session.Image) is not { } an)
        {
            infoText.Text = Loc.S("Sin sonido. «Abrir sonido…» o arrastra un WAV/AIFF/FLAC/OGG/MP3 al lienzo.", "No sound. «Open sound…» or drag a WAV/AIFF/FLAC/OGG/MP3 onto the canvas.");
            return;
        }
        var a = an.Audio;
        var s = an.Settings;
        var img = session.Image;
        // The analysis is stereo at most: files with more channels are reduced to two.
        string ch = a.Channels.Length switch { 1 => Loc.S("mono", "mono"), 2 => Loc.S("estéreo", "stereo"), var n => Loc.S($"{n} canales (se usan 2)", $"{n} channels (2 used)") };
        // FFT resolution: bin spacing sr/N in Hz; window length N/sr shown in ms.
        double binHz = (double)a.SampleRate / s.FrameSize;
        infoText.Text = (
            $"{an.Name}  ·  {a.SampleRate} Hz  ·  {ch}  ·  {a.Seconds:0.00} s  ·  " +
            Loc.S($"imagen {img.Width}×{img.Height}, FFT {s.FrameSize} ({1000.0 * s.FrameSize / a.SampleRate:0} ms, {binHz:0.#} Hz/bin), salto {s.HopSize}, columna {img.SecondsPerColumn * 1000:0.#} ms, piso {s.FloorDb:0} dB",
                  $"picture {img.Width}×{img.Height}, FFT {s.FrameSize} ({1000.0 * s.FrameSize / a.SampleRate:0} ms, {binHz:0.#} Hz/bin), hop {s.HopSize}, column {img.SecondsPerColumn * 1000:0.#} ms, floor {s.FloorDb:0} dB"));
    }

    /// <summary>Shows the preferences in the controls without triggering their handlers.</summary>
    /// <remarks>Also shows/hides the axis- and mode-dependent fields (rows, steps per octave, voice).</remarks>
    /// <summary>True when the next analysis was scheduled by a settings change (see <see cref="AnalysisSettingsChanged"/>).</summary>
    bool analysisFromSettings;

    /// <summary>Incremented by every undo/redo of the settings: an analysis started before it is stale.</summary>
    int settingsEpoch;

    /// <summary>
    /// Changes the room's settings as ONE undoable step: snapshots the settings and the current
    /// picture, applies <paramref name="change"/>, records the step and, for analysis settings,
    /// schedules the re-analysis.
    /// </summary>
    /// <param name="change">Writes the new value(s) into <c>sp</c>.</param>
    /// <param name="reanalyse">True for settings that change the analysis (FFT, overlap, axis, floor…).</param>
    /// <remarks>
    /// The snapshot includes the picture because an analysis setting replaces it: undoing the
    /// setting brings back the picture as it was (with any painting on it, and — through the
    /// analysis link of that picture object — its analysis) without analysing again.
    /// </remarks>
    void ChangeSettings(Action change, bool reanalyse)
    {
        var before = (sp.Clone(), session.Image);
        change();
        session.PushState("spectrum", before, RestoreSettings);
        if (reanalyse) AnalysisSettingsChanged();
    }

    /// <summary>Undo/redo of a settings step: puts back the settings and the picture of the snapshot.</summary>
    /// <param name="state">The snapshot: (settings, picture).</param>
    /// <returns>The snapshot of what was replaced (for the opposite step).</returns>
    object? RestoreSettings(object? state)
    {
        var (settings, picture) = ((SpectrumRoomSettings, SpectralImage))state!;
        var replaced = (sp.Clone(), session.Image);
        // A pending or running re-analysis belongs to the settings being undone: stop it.
        analyseTimer.Stop();
        analysisFromSettings = false;
        settingsEpoch++;
        StopAll();
        sp.CopyFrom(settings);
        if (!ReferenceEquals(picture, session.Image)) session.ReplaceImage(picture, "spectrum", undoable: false);
        SyncControls();
        normalizeToggle.IsChecked = sp.Normalize;
        gainBox.Value = (decimal)sp.GainDb;
        syncing = true;
        voiceBox.SelectedIndex = sp.AdditiveMode == SynthMode.NoiseBands ? 1 : 0;
        syncing = false;
        return replaced;
    }

    void SyncControls()
    {
        syncing = true;
        var a = sp.Analysis;
        fftBox.SelectedItem = a.FrameSize;
        overlapBox.SelectedIndex = a.Overlap switch { 2 => 0, 8 => 2, _ => 1 };
        columnBox.Value = (decimal)sp.ColumnMs;
        floorBox.Value = (decimal)sp.FloorDb;
        axisBox.SelectedIndex = (int)sp.AxisKind;
        lowBox.Value = (decimal)sp.LowHz;
        highBox.Value = (decimal)sp.HighHz;
        rowsBox.Value = sp.Rows;
        stepsBox.Value = sp.StepsPerOctave;
        rowsPanel.IsVisible = sp.AxisKind is PitchAxisKind.Linear or PitchAxisKind.Logarithmic;
        stepsPanel.IsVisible = sp.AxisKind == PitchAxisKind.EqualTemperament;
        modeBox.SelectedIndex = (int)sp.Mode;
        voicePanel.IsVisible = sp.Mode == SpectrumResynthesis.Additive;
        syncing = false;
    }

    /// <summary>Enables «Use session sound» only when the session holds a sound.</summary>
    void UpdateSessionSoundButton() => sessionSoundButton.IsEnabled = shared.Sound is not null;

    /// <summary>Matches the scroll bars to the canvas view.</summary>
    /// <remarks>Units: columns for the horizontal bar, rows for the vertical one (ViewportSize = visible count, so the thumb is proportional).</remarks>
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

    /// <summary>Accepts sound files dragged over the canvas or the waveform.</summary>
    /// <param name="sender">The canvas or the waveform strip.</param>
    /// <param name="e">Drag data; only the first local file is considered, by extension.</param>
    void OnDragOver(object? sender, DragEventArgs e)
    {
        var f = e.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        e.DragEffects = f is not null && SoundFileLoader.IsSoundFile(f) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>A dropped sound file is loaded and analysed.</summary>
    /// <param name="sender">The canvas or the waveform strip.</param>
    /// <param name="e">Drop data (first local file only).</param>
    void OnDrop(object? sender, DragEventArgs e)
    {
        var f = e.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (f is not null && SoundFileLoader.IsSoundFile(f)) _ = LoadFileAsync(f);
        e.Handled = true;
    }

    /// <summary>Says that a selection is needed; returns <c>null</c> so it can stand in for a missing selection.</summary>
    /// <returns>Always <c>null</c> (currently every caller ignores it).</returns>
    SelectionMask? Warn()
    {
        Say(Loc.S("Primero selecciona una zona (⬚, tecla M) o arrastra en la regla de alturas para una banda.",
                  "Select a region first (⬚, key M) or drag on the pitch ruler for a band."), true);
        return null;
    }

    /// <summary>The «?» quick help.</summary>
    /// <remarks>Shown as a modal dialog over the Paint Rooms window; the returned task is not awaited.</remarks>
    void ShowHelp()
    {
        string text = Loc.S(
            "QUÉ ES\nUn sonido convertido en imagen: tiempo → derecha, altura ↑, brillo = volumen en dB (blanco = 0 dB, negro = el piso). Rojo/verde = solo izquierda/derecha.\n\nCÓMO SE USA\n1. Abre un sonido (o arrástralo).\n2. Edita: borra (E o botón derecho), selecciona (M) o arrastra en la regla de alturas para una banda, Quitar, Ganancia, Selección ▾ (bajo las herramientas).\n3. Escucha (Espacio). «Máscara espectral» conserva la grabación y aplica tus cambios; «Aditiva» la vuelve a sintetizar con Csound.\n4. Renderiza o extrae a WAV.\n\nATAJOS\nEspacio tocar · M selección · Mayús+M elipse · F lazo · N spline (Enter cierra, Esc cancela); Mayús agrega, Alt resta\nE goma · A aerógrafo · Ctrl+Z/Y (o Ctrl+Mayús+Z) · Supr quita la selección · Esc la deselecciona · Ctrl+O abrir · Inicio = cursor al comienzo\n\nLa guía con tutoriales paso a paso está en la documentación (Paint Rooms › Spectrum Room).",
            "WHAT IT IS\nA sound turned into a picture: time → right, pitch ↑, brightness = loudness in dB (white = 0 dB, black = the floor). Red/green = left/right only.\n\nHOW TO USE IT\n1. Open a sound (or drag it in).\n2. Edit: erase (E or right button), select (M) or drag on the pitch ruler for a band, Remove, Gain, Selection ▾ (under the tools).\n3. Listen (Space). «Spectral mask» keeps the recording and applies your changes; «Additive» resynthesises it with Csound.\n4. Render or extract to WAV.\n\nSHORTCUTS\nSpace play · M selection · Shift+M ellipse · F lasso · N spline (Enter closes, Esc cancels); Shift adds, Alt subtracts\nE eraser · A airbrush · Ctrl+Z/Y (or Ctrl+Shift+Z) · Del removes the selection · Esc deselects · Ctrl+O open · Home = cursor to start\n\nThe guide with step-by-step tutorials is in the documentation (Paint Rooms › Spectrum Room).");
        _ = new InfoDialog(Loc.S("Spectrum Room · ayuda", "Spectrum Room · help"), text).ShowDialog(ctx.Window);
    }

    /// <summary>Shows a message at the bottom right (accent colour, or the warning colour).</summary>
    /// <param name="text">Message; also set as tooltip, since long messages are trimmed with an ellipsis.</param>
    /// <param name="warning">True for problems (warning colour).</param>
    /// <remarks>UI thread only; also subscribed to <see cref="SoundPreviewPlayer"/>'s messages.</remarks>
    void Say(string text, bool warning)
    {
        messageText.Text = text;
        messageText.Foreground = warning ? AppTheme.Brush("Warn") : AppTheme.Brush("AccentHi");
        ToolTip.SetTip(messageText, text);
    }

    /// <summary>A file-name-safe version of <paramref name="name"/>.</summary>
    /// <param name="name">Any display name.</param>
    /// <returns>The name with every character Windows forbids in file names replaced by '_'.</returns>
    static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>A tool-bar row that wraps when the window is narrow.</summary>
    static WrapPanel Row() => new() { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 4, 10, 2) };

    /// <summary>Adds controls to a row with the standard spacing.</summary>
    /// <param name="panel">The row.</param>
    /// <param name="controls">Controls to append; their Margin is overwritten.</param>
    static void AddAll(Panel panel, params Control[] controls)
    {
        foreach (var c in controls)
        {
            c.Margin = new Thickness(0, 2, 6, 2);
            panel.Children.Add(c);
        }
    }

    /// <summary>Small dim caption for slider values.</summary>
    static TextBlock Small() => new() { FontSize = 11, Foreground = AppTheme.Brush("TextDim"), HorizontalAlignment = HorizontalAlignment.Right };

    /// <summary>Section caption of the tools column.</summary>
    static TextBlock Caption(string text) =>
        new() { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = AppTheme.Brush("AccentHi"), Margin = new Thickness(2, 6, 0, 2) };

    /// <summary>A slider with its label and value above it.</summary>
    /// <param name="label">Caption on the left.</param>
    /// <param name="value">Value caption on the right (updated by <see cref="OnBrushChanged"/>).</param>
    /// <param name="slider">The slider below.</param>
    /// <returns>The composed control.</returns>
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
    /// <param name="click">Action run when the item is clicked.</param>
    /// <returns>The new item.</returns>
    static MenuItem Item(string header, Action click)
    {
        var m = new MenuItem { Header = header };
        m.Click += (_, _) => click();
        return m;
    }

    /// <summary>A button that opens a menu built fresh each time (current values in the texts).</summary>
    /// <param name="text">Button caption (ends with ▾ by convention).</param>
    /// <param name="tip">Tooltip.</param>
    /// <param name="build">Fills a new <see cref="MenuFlyout"/> at every click.</param>
    /// <returns>The button.</returns>
    static Button MenuButton(string text, string tip, Action<MenuFlyout> build)
    {
        // ToolUi.Button needs an action; the real work is the Click handler below, which needs `b`.
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
