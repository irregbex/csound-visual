using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CsoundVisual.Localization;
using CsoundVisual.ViewModels;
using CsoundVisual.Visual3D;
using CsoundVisual.Theming;

namespace CsoundVisual.Views;

/// <summary>
/// Detachable 3D window (it can live on a second monitor and go full screen with F11). It hosts
/// a <see cref="GlView"/> and lets the user pick a scene: waterfall spectrogram, XYZ pad (three
/// channels at once) or spatial binaural panner. Built in code.
/// </summary>
/// <remarks>
/// <para>A ~60 Hz timer (16 ms, <see cref="DispatcherPriority.Render"/>) copies the engine's
/// output buffer into the shared <see cref="AudioFeed"/>, bumps its version so the waterfall
/// knows there is new data, and redraws the <see cref="LabelOverlay"/> (axis numbers and units over
/// the GPU image). The GL view renders on its own schedule and reads the feed. If OpenGL fails,
/// the view is replaced by an explanation and the rest of the app keeps working.</para>
/// <para>The pad and the panner act on the channels (sliders) of the document shown in the main
/// window. The panel names that document, and whenever the scene is opened or the document
/// changes, the sphere/source is placed where the sliders are, so nothing jumps.</para>
/// <para>Units: the pad works in normalised 0…1 per axis, mapped to each slider's own range by
/// <see cref="MainViewModel.SetChannelNormalized"/> / <see cref="MainViewModel.GetChannelNormalized"/>;
/// the panner works in degrees (azimuth, elevation) and a distance, sent as the raw values of
/// the channels <c>az</c>, <c>el</c> and <c>dist</c> (as read by <c>Assets/Binaural3D.csd</c>).</para>
/// <para>Waterfall settings (palette, bands, rows/s, seconds) are persisted to
/// <see cref="AppSettings"/> as soon as they change.</para>
/// <para>Language: every translatable text is applied through <see cref="Tr"/>, which runs the
/// assignment once and remembers it; <see cref="OnLanguageChanged"/> (subscribed to the static
/// <see cref="Loc.Changed"/> and unsubscribed on close) replays them, so an open window follows a
/// live language switch without rebuilding the controls (rebuilding would re-parent the
/// <see cref="GlView"/> and lose its OpenGL context).</para>
/// </remarks>
public sealed class Visual3DWindow : Window
{
    /// <summary>Dim text brush (theme brush instance; recoloured in place on theme change).</summary>
    static readonly IBrush Dim = AppTheme.Brush("TextDim");
    /// <summary>Highlight text brush.</summary>
    static readonly IBrush Hi = AppTheme.Brush("AccentHi");
    /// <summary>Monospaced font for numeric read-outs (keeps columns from jittering).</summary>
    static readonly FontFamily Mono = new("Cascadia Code, Consolas, monospace");

    /// <summary>The application view model (engine, channels, settings).</summary>
    readonly MainViewModel vm;
    /// <summary>Audio snapshot shared with the scenes (scope buffer, sample rate, running flag, version).</summary>
    readonly AudioFeed feed = new();
    /// <summary>The OpenGL control that draws the current scene.</summary>
    readonly GlView view = new();
    /// <summary>2D text layer over the GL image (axis labels of the current scene).</summary>
    readonly LabelOverlay overlay;
    /// <summary>Scene 0: waterfall spectrogram of the output.</summary>
    readonly WaterfallScene waterfall;
    /// <summary>Scene 1: XYZ pad driving three channels at once.</summary>
    readonly XyzPadScene pad = new();
    /// <summary>Scene 2: binaural panner driving az/el/dist.</summary>
    readonly SpatialScene spatial = new();
    /// <summary>The scenes in picker order.</summary>
    readonly IScene3D[] scenes;
    /// <summary>Help text of the current scene (bottom bar; also the view's tooltip).</summary>
    readonly TextBlock help = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Dim };
    /// <summary>Top bar status: scene info, GPU fps/context, playing or stopped.</summary>
    readonly TextBlock status = new() { FontSize = 11, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>Error text shown instead of the GL view when OpenGL cannot start.</summary>
    readonly TextBlock failure = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = AppTheme.Brush("Danger"), Margin = new Thickness(30), IsVisible = false };
    /// <summary>Channel chosen for each pad axis (X, Y, Z).</summary>
    readonly ComboBox[] axisPickers = [new(), new(), new()];
    /// <summary>Current display value of each pad axis' channel.</summary>
    readonly TextBlock[] axisValues = [new(), new(), new()];
    /// <summary>"Controls: file" — which document the pad acts on.</summary>
    readonly TextBlock padDocument = new() { FontSize = 12, Foreground = Hi, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>Bottom controls of the waterfall (palette, resolution, reset view).</summary>
    readonly StackPanel waterfallPanel = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    /// <summary>Bottom controls of the pad (document, axis pickers and values).</summary>
    readonly StackPanel padPanel = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
    /// <summary>Bottom controls of the panner (template button, polar read-out).</summary>
    readonly StackPanel spatialPanel = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
    /// <summary>"azimuth … elevation … distance …" read-out of the panner.</summary>
    readonly TextBlock spatialText = new() { FontFamily = Mono, FontSize = 12, Foreground = Hi, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>The 16 ms UI timer that runs <see cref="Tick"/> while the window is open.</summary>
    readonly DispatcherTimer timer;
    /// <summary>
    /// True while <see cref="RefreshAxisChannels"/> refills the axis pickers, so their
    /// SelectionChanged events do not trigger a sync per picker (one sync is done at the end).
    /// </summary>
    bool syncing;
    /// <summary>
    /// Text assignments registered by <see cref="Tr"/>, replayed in order by
    /// <see cref="OnLanguageChanged"/> when the interface language changes.
    /// </summary>
    readonly List<Action> retext = [];
    /// <summary>
    /// True while <see cref="OnLanguageChanged"/> replays <see cref="retext"/>. Re-filling a combo
    /// box (to translate its items) raises SelectionChanged; the waterfall handlers check this flag
    /// so a language switch does not reconfigure the scene (which would clear its history) nor
    /// rewrite the settings file.
    /// </summary>
    bool retexting;

    /// <summary>Creates the window for <paramref name="viewModel"/>.</summary>
    /// <param name="viewModel">The main view model.</param>
    public Visual3DWindow(MainViewModel viewModel)
    {
        vm = viewModel;
        Title = "Csound Visual · 3D";
        Width = 1100;
        Height = 760;
        MinWidth = 520;
        MinHeight = 360;
        Background = AppTheme.Brush("BgDeep");
        waterfall = new WaterfallScene(feed) { ColorMap = vm.Settings.WaterfallPalette };
        waterfall.Configure(vm.Settings.WaterfallBands, vm.Settings.WaterfallRate, vm.Settings.WaterfallSeconds);
        scenes = [waterfall, pad, spatial];
        overlay = new LabelOverlay(view);

        var picker = new ComboBox { ItemsSource = scenes.Select(s => s.Name).ToList(), SelectedIndex = 0, MinWidth = 260 };
        // Scene names are translated: re-filled (keeping the selection) on a language switch.
        retext.Add(() => SetItems(picker, scenes.Select(s => s.Name)));
        Tr(() => ToolTip.SetTip(picker, Loc.S("Elige la visualización o el control 3D.", "Choose the 3D visualisation or control.")));
        picker.SelectionChanged += (_, _) => SelectScene(picker.SelectedIndex);

        var full = new Button { Classes = { "tool" } };
        Tr(() => full.Content = Loc.S("Pantalla completa (F11)", "Full screen (F11)"));
        Tr(() => ToolTip.SetTip(full, Loc.S("Ocupa toda la pantalla (ideal en un segundo monitor o proyector). F11 o Esc para volver.", "Fills the whole screen (ideal on a second monitor or projector). F11 or Esc to return.")));
        full.Click += (_, _) => ToggleFullScreen();

        // Layout: top bar ("3D" + scene picker | status | full screen), bottom bar (help + the
        // panel of the current scene), and the GL view filling the centre with the label overlay
        // and the failure text stacked over it in the same grid cell.
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(14, 10) };
        top.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { new TextBlock { Text = "3D", FontWeight = FontWeight.Bold, Foreground = Hi, VerticalAlignment = VerticalAlignment.Center }, picker } });
        top.Children.Add(new Border { [Grid.ColumnProperty] = 1, Child = status, Margin = new Thickness(16, 0) });
        top.Children.Add(new Border { [Grid.ColumnProperty] = 2, Child = full });

        BuildWaterfallPanel();
        BuildPadPanel();
        BuildSpatialPanel();

        var bottom = new StackPanel { Spacing = 8, Margin = new Thickness(14, 8, 14, 12), Children = { help, waterfallPanel, padPanel, spatialPanel } };
        var centre = new Grid { Children = { view, overlay, failure } };

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(centre);
        Content = root;

        // GlView reports GL initialisation errors from its render path: marshal to the UI thread.
        view.Failed += reason => Dispatcher.UIThread.Post(() =>
        {
            failure.Text = reason + Loc.S("\n\nLa app sigue funcionando; las vistas 2D del panel lateral no dependen de la GPU.", "\n\nThe app keeps working; the 2D views in the side panel do not depend on the GPU.");
            failure.IsVisible = true;
            view.IsVisible = false;
        });
        pad.Changed += OnPadChanged;
        spatial.Changed += OnSpatialChanged;
        vm.PropertyChanged += OnViewModelChanged;
        // Loc.Changed is static: it must be unsubscribed on close (below) or the closed window
        // would stay alive and keep reacting to language switches.
        Loc.Changed += OnLanguageChanged;

        // 16 ms ≈ 60 Hz, at Render priority so feeding the scene keeps pace with the frames.
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
        Opened += (_, _) => timer.Start();
        Closed += (_, _) =>
        {
            timer.Stop();
            vm.PropertyChanged -= OnViewModelChanged;
            Loc.Changed -= OnLanguageChanged;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F11 || (e.Key == Key.Escape && WindowState == WindowState.FullScreen))
            {
                ToggleFullScreen();
                e.Handled = true;
            }
        };
        SelectScene(0);
    }

    /// <summary>
    /// Applies a translatable text now and registers it for <see cref="OnLanguageChanged"/>.
    /// </summary>
    /// <param name="apply">Assignment that reads <see cref="Loc.S"/> (e.g. a button's content or tooltip).</param>
    void Tr(Action apply)
    {
        apply();
        retext.Add(apply);
    }

    /// <summary>
    /// Re-fills <paramref name="box"/> with <paramref name="items"/> (translated labels) and
    /// restores the previously selected index, since replacing <c>ItemsSource</c> clears it.
    /// </summary>
    /// <param name="box">Combo box to re-fill.</param>
    /// <param name="items">Labels in the same order as before.</param>
    static void SetItems(ComboBox box, IEnumerable<string> items)
    {
        int index = box.SelectedIndex;
        box.ItemsSource = items.ToList();
        box.SelectedIndex = index;
    }

    /// <summary>
    /// <see cref="Loc.Changed"/> handler (UI thread): replays every registered text and refreshes
    /// the texts that depend on state (scene help, pad document, polar read-out). The status bar
    /// and the GL overlay labels already read <see cref="Loc.S"/> on every tick/frame.
    /// </summary>
    void OnLanguageChanged()
    {
        retexting = true;
        try
        {
            foreach (var apply in retext) apply();
        }
        finally
        {
            retexting = false;
        }
        if (view.Scene is { } scene)
        {
            help.Text = scene.Help;
            ToolTip.SetTip(view, scene.Help);
        }
        if (view.Scene == pad) RefreshAxisChannels();
        var (az, el, dist) = spatial.Polar;
        spatialText.Text = PolarText(az, el, dist);
    }

    /// <summary>Switches between full screen and a normal window (F11, Esc in full screen, or the button).</summary>
    void ToggleFullScreen() =>
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

    /// <summary>
    /// Shows scene <paramref name="index"/> in the GL view, swaps the help text and the bottom
    /// panel, and syncs the pad/panner with the current slider values so the control starts where
    /// the sound is.
    /// </summary>
    /// <param name="index">Index into <see cref="scenes"/>; negative (no selection) is ignored.</param>
    void SelectScene(int index)
    {
        if (index < 0) return;
        var scene = scenes[index];
        view.Scene = scene;
        help.Text = scene.Help;
        ToolTip.SetTip(view, scene.Help);
        waterfallPanel.IsVisible = scene == waterfall;
        padPanel.IsVisible = scene == pad;
        spatialPanel.IsVisible = scene == spatial;
        if (scene == pad) RefreshAxisChannels();
        if (scene == spatial) SyncSpatialFromSliders();
    }

    /// <summary>The main window switched documents (e.g. the binaural template was opened): follow it.</summary>
    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SelectedDocument)) return;
        if (view.Scene == pad) RefreshAxisChannels();
        if (view.Scene == spatial) SyncSpatialFromSliders();
    }

    /// <summary>
    /// Waterfall controls: false-colour palette, resolution choices (bands, rows per second,
    /// seconds of history) and "Initial view". Every change reconfigures the scene and is saved.
    /// </summary>
    void BuildWaterfallPanel()
    {
        var label = new TextBlock { Foreground = Hi, VerticalAlignment = VerticalAlignment.Center };
        Tr(() => label.Text = Loc.S("Colores", "Colours"));
        var palette = new ComboBox { ItemsSource = ColorMaps.Names, SelectedIndex = Math.Clamp(waterfall.ColorMap, 0, ColorMaps.Names.Count - 1), MinWidth = 200 };
        // Palette names are translated too; SetItems keeps the selected index.
        retext.Add(() => SetItems(palette, ColorMaps.Names));
        Tr(() => ToolTip.SetTip(palette, Loc.S("Paleta de colores falsos de la cascada: el color indica el nivel (de bajo a alto). Viridis, Magma e Inferno son perceptualmente uniformes: pasos iguales de nivel se ven como pasos iguales de color.", "False-colour palette of the waterfall: the colour shows the level (low to high). Viridis, Magma and Inferno are perceptually uniform: equal level steps look like equal colour steps.")));
        palette.SelectionChanged += (_, _) =>
        {
            // Skip the transient -1 / restore of a language re-fill: the palette did not change.
            if (palette.SelectedIndex < 0 || retexting) return;
            waterfall.ColorMap = palette.SelectedIndex;
            vm.Settings.WaterfallPalette = palette.SelectedIndex;
            vm.Settings.Save();
        };
        var reset = new Button { Classes = { "tool" } };
        Tr(() => reset.Content = Loc.S("Vista inicial", "Initial view"));
        Tr(() => ToolTip.SetTip(reset, Loc.S("Vuelve la cámara a la posición inicial (también con doble clic en la vista).", "Returns the camera to its initial position (also by double-clicking the view).")));
        reset.Click += (_, _) => view.ResetCamera();
        waterfallPanel.Children.Add(label);
        waterfallPanel.Children.Add(palette);

        // Resolution: bands across, spectra per second, seconds of history. A saved value that
        // is not in the list (Array.IndexOf → -1) falls back to the given default index.
        int[] bandChoices = [32, 64, 96, 128, 192, 256];
        int[] rateChoices = [10, 15, 30, 60];
        double[] secondChoices = [2, 3, 5, 8, 12, 20];
        var bands = Choice(() => bandChoices.Select(b => Loc.S($"{b} bandas", $"{b} bands")), Array.IndexOf(bandChoices, waterfall.Bands), 1,
            () => Loc.S("Bandas de frecuencia de izquierda a derecha (escala logarítmica). Más bandas = más detalle en agudos; en graves el límite es la FFT de 2048 muestras (≈ 23 Hz por bin a 48 kHz), así que bandas muy finas bajo ~200 Hz se repiten.",
                  "Frequency bands from left to right (logarithmic scale). More bands = more detail in the highs; in the lows the limit is the 2048-sample FFT (≈ 23 Hz per bin at 48 kHz), so very fine bands below ~200 Hz repeat."));
        var rate = Choice(() => rateChoices.Select(r => Loc.S($"{r} filas/s", $"{r} rows/s")), Array.IndexOf(rateChoices, waterfall.RowsPerSecond), 2,
            () => Loc.S("Espectros por segundo: cuántas filas nuevas entran por segundo. Más = movimiento más fluido y transitorios más finos en el tiempo (cada espectro mira los últimos ~43 ms).",
                  "Spectra per second: how many new rows enter each second. More = smoother motion and finer transients in time (each spectrum looks at the last ~43 ms)."));
        var seconds = Choice(() => secondChoices.Select(s => Loc.S($"{s:0} s de historia", $"{s:0} s history")), Array.IndexOf(secondChoices, Math.Round(waterfall.Seconds)), 1,
            () => Loc.S($"Cuánto tiempo hacia atrás se ve. Bandas × filas se limita a {WaterfallScene.MaxCells} celdas para no cargar la GPU, así que con muchas bandas y filas/s la historia puede quedar más corta (la barra inferior dice la real).",
                  $"How far back in time you see. Bands × rows is limited to {WaterfallScene.MaxCells} cells to keep the GPU light, so with many bands and rows/s the history may be shorter (the bottom bar shows the real one)."));
        // Reconfigures the scene with the three choices (this clears its history). The scene
        // clamps the row count so bands × rows ≤ MaxCells; the requested seconds are saved as
        // chosen, the bands/rate as the scene accepted them.
        void Apply()
        {
            // retexting: a language switch only re-fills the labels; reconfiguring would clear the history.
            if (retexting || bands.SelectedIndex < 0 || rate.SelectedIndex < 0 || seconds.SelectedIndex < 0) return;
            waterfall.Configure(bandChoices[bands.SelectedIndex], rateChoices[rate.SelectedIndex], secondChoices[seconds.SelectedIndex]);
            vm.Settings.WaterfallBands = waterfall.Bands;
            vm.Settings.WaterfallRate = waterfall.RowsPerSecond;
            vm.Settings.WaterfallSeconds = secondChoices[seconds.SelectedIndex];
            vm.Settings.Save();
        }
        bands.SelectionChanged += (_, _) => Apply();
        rate.SelectionChanged += (_, _) => Apply();
        seconds.SelectionChanged += (_, _) => Apply();
        waterfallPanel.Children.Add(bands);
        waterfallPanel.Children.Add(rate);
        waterfallPanel.Children.Add(seconds);
        waterfallPanel.Children.Add(reset);
    }

    /// <summary>A combo box of fixed choices with a tooltip, both re-translated on a language switch.</summary>
    /// <param name="items">Produces the labels to show in the active language.</param>
    /// <param name="index">Initially selected index, or -1 when the current value is not a listed choice.</param>
    /// <param name="fallback">Index used when <paramref name="index"/> is -1.</param>
    /// <param name="tip">Produces the tooltip in the active language.</param>
    /// <returns>The combo box.</returns>
    /// <remarks>The labels are registered in <see cref="retext"/> (re-filled by <see cref="SetItems"/>,
    /// which keeps the selection) and the tooltip through <see cref="Tr"/>.</remarks>
    ComboBox Choice(Func<IEnumerable<string>> items, int index, int fallback, Func<string> tip)
    {
        var box = new ComboBox { ItemsSource = items().ToList(), SelectedIndex = index >= 0 ? index : fallback, MinWidth = 110 };
        retext.Add(() => SetItems(box, items()));
        Tr(() => ToolTip.SetTip(box, tip()));
        return box;
    }

    /// <summary>Pad controls: the controlled document and, per axis X/Y/Z, a channel picker and its current value.</summary>
    void BuildPadPanel()
    {
        padPanel.Children.Add(padDocument);
        Tr(() => ToolTip.SetTip(padDocument, Loc.S("Documento cuyos sliders mueve el pad: el que se muestra en la ventana principal.", "Document whose sliders the pad moves: the one shown in the main window.")));
        string[] axes = ["X", "Y", "Z"];
        for (int i = 0; i < 3; i++)
        {
            var label = new TextBlock { Text = axes[i], FontWeight = FontWeight.Bold, Foreground = Hi, VerticalAlignment = VerticalAlignment.Center };
            axisPickers[i].MinWidth = 150;
            // Locals captured by the registered lambda (the loop variable i would be stale).
            var axisPicker = axisPickers[i];
            var axis = axes[i];
            Tr(() => ToolTip.SetTip(axisPicker, Loc.S($"Canal que controla el eje {axis} (sus valores van del mínimo al máximo del slider).", $"Channel controlled by the {axis} axis (its values go from the slider's minimum to its maximum).")));
            axisPickers[i].SelectionChanged += (_, _) => SyncPadFromSliders();
            axisValues[i].FontFamily = Mono;
            axisValues[i].Foreground = Dim;
            axisValues[i].VerticalAlignment = VerticalAlignment.Center;
            padPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { label, axisPickers[i], axisValues[i] } });
        }
    }

    /// <summary>Panner controls: "Open binaural template" (opens <c>Assets/Binaural3D.csd</c> in a tab) and the polar read-out.</summary>
    void BuildSpatialPanel()
    {
        var template = new Button { Classes = { "tool", "primary" } };
        Tr(() => template.Content = Loc.S("Abrir plantilla binaural", "Open binaural template"));
        Tr(() => ToolTip.SetTip(template, Loc.S("Abre en una pestaña un instrumento con hrtfmove2 que lee los canales az, el y dist. Detén lo que suena, presiona Tocar y escucha con audífonos.", "Opens in a tab an instrument with hrtfmove2 that reads the az, el and dist channels. Stop what is playing, press Play and listen on headphones.")));
        template.Click += (_, _) => vm.OpenTemplate("Binaural3D");
        spatialPanel.Children.Add(template);
        spatialPanel.Children.Add(spatialText);
        var (az, el, dist) = spatial.Polar;
        spatialText.Text = PolarText(az, el, dist);
    }

    /// <summary>Fills the axis pickers with the shown document's channels, keeping sensible defaults.</summary>
    /// <remarks>
    /// Each axis keeps its previous channel if the new document has it; otherwise X/Y/Z take the
    /// document's 1st/2nd/3rd channel (or nothing).
    /// </remarks>
    void RefreshAxisChannels()
    {
        var doc = vm.SelectedDocument;
        padDocument.Text = doc is null ? Loc.S("Sin documento", "No document") : Loc.S("Controla: ", "Controls: ") + doc.FileName;
        var names = doc?.Channels.Select(c => c.Name).ToList() ?? [];
        syncing = true;
        for (int i = 0; i < 3; i++)
        {
            var previous = axisPickers[i].SelectedItem as string;
            axisPickers[i].ItemsSource = names;
            axisPickers[i].SelectedItem = previous is not null && names.Contains(previous) ? previous : names.ElementAtOrDefault(i);
        }
        syncing = false;
        SyncPadFromSliders();
    }

    /// <summary>Places the sphere where the chosen sliders are (no values are sent).</summary>
    /// <remarks>
    /// Also refreshes the axis labels drawn by the scene (channel name, min, max) and the value
    /// read-outs. An axis without a channel keeps its current position. <c>SetPositionSilently</c>
    /// moves the sphere without raising <see cref="XyzPadScene.Changed"/>, so the channels are not
    /// written back (which would be a feedback loop and could round the values).
    /// </remarks>
    void SyncPadFromSliders()
    {
        if (syncing) return;
        var p = pad.Position;
        float[] v = [p.X, p.Y, p.Z];
        for (int i = 0; i < 3; i++)
        {
            string? name = axisPickers[i].SelectedItem as string;
            if (name is not null && vm.GetChannelNormalized(name) is { } t)
                v[i] = t;
            var info = name is not null ? vm.SelectedDocument?.FindChannel(name) : null;
            pad.Axes[i] = info is null ? ("—", "", "") : (name!, Format(info.Minimum), Format(info.Maximum));
            axisValues[i].Text = info?.DisplayValue ?? "—";
        }
        pad.SetPositionSilently(new Vector3(v[0], v[1], v[2]));
        UpdatePadValueText();
    }

    /// <summary>
    /// The user dragged the sphere: writes each axis' normalised 0…1 position to its channel
    /// (the view model maps it to the slider range, moves the slider and sends it to Csound) and
    /// shows the resulting display values.
    /// </summary>
    /// <param name="p">New position, each component normalised 0…1.</param>
    void OnPadChanged(Vector3 p)
    {
        float[] v = [p.X, p.Y, p.Z];
        for (int i = 0; i < 3; i++)
            if (axisPickers[i].SelectedItem is string name)
                axisValues[i].Text = vm.SetChannelNormalized(name, v[i]);
        UpdatePadValueText();
    }

    /// <summary>Gives the scene the joined axis values to draw next to the sphere (unassigned axes omitted).</summary>
    void UpdatePadValueText() =>
        pad.ValueText = string.Join("  ·  ", axisValues.Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t) && t != "—"));

    /// <summary>Places the source from the document's az/el/dist sliders when it has them.</summary>
    void SyncSpatialFromSliders()
    {
        var doc = vm.SelectedDocument;
        if (doc?.FindChannel("az") is { } az && doc.FindChannel("el") is { } el && doc.FindChannel("dist") is { } dist)
            spatial.SetPolarSilently((float)az.Value, (float)el.Value, (float)dist.Value);
        var (a, e, d) = spatial.Polar;
        spatialText.Text = PolarText(a, e, d);
    }

    /// <summary>
    /// The user moved the source: sends the raw polar values to the channels <c>az</c>, <c>el</c>
    /// and <c>dist</c> through <see cref="MainViewModel.SendChannel"/> (via the shown document's
    /// slider when it declares the channel, clamped to its range; otherwise straight to the engine
    /// while playing).
    /// </summary>
    /// <param name="az">Azimuth in degrees.</param>
    /// <param name="el">Elevation in degrees.</param>
    /// <param name="dist">Distance (scene units, as expected by the binaural template).</param>
    void OnSpatialChanged(float az, float el, float dist)
    {
        vm.SendChannel("az", az);
        vm.SendChannel("el", el);
        vm.SendChannel("dist", dist);
        spatialText.Text = PolarText(az, el, dist);
    }

    /// <summary>Fixed-width, invariant-culture polar read-out in the active language.</summary>
    static string PolarText(float az, float el, float dist) =>
        Loc.IsEnglish
            ? string.Create(CultureInfo.InvariantCulture, $"azimuth {az,6:0.0}°   elevation {el,5:0.0}°   distance {dist:0.00}")
            : string.Create(CultureInfo.InvariantCulture, $"azimut {az,6:0.0}°   elevación {el,5:0.0}°   distancia {dist:0.00}");

    /// <summary>Compact number for axis labels: fewer decimals as the magnitude grows (1234, 12.5, 0.25).</summary>
    static string Format(double v) =>
        v.ToString(Math.Abs(v) >= 100 ? "0" : Math.Abs(v) >= 10 ? "0.#" : "0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Timer tick (UI thread): feeds the latest output samples to the scenes, invalidates the
    /// label overlay and updates the status text. <c>CopyScope</c> copies the engine's latest
    /// scope samples (output ring, <c>ScopeSize</c> = 2048) into the feed's buffer; the waterfall adds a new spectrum row only when
    /// <c>feed.Version</c> changed and its row interval has elapsed.
    /// </summary>
    void Tick()
    {
        var engine = vm.Engine;
        feed.Running = vm.IsRunning;
        if (feed.Running)
        {
            engine.CopyScope(feed.Scope);
            feed.SampleRate = engine.SampleRate;
        }
        feed.Version++;
        overlay.InvalidateVisual();
        string gpu = view.ContextInfo is { } info ? Loc.S($"GPU {view.Fps} fps · ", $"GPU {view.Fps} fps · ") + info : Loc.S("iniciando GPU…", "starting GPU…");
        if (view.Scene == waterfall) gpu = waterfall.Info + "   ·   " + gpu;
        status.Text = gpu +
                      (vm.IsRunning ? Loc.S("   ·   sonando", "   ·   playing") : Loc.S("   ·   detenido (presiona Tocar)", "   ·   stopped (press Play)"));
    }
}
