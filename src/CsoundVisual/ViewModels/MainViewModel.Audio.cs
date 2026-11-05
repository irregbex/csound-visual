using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CsoundVisual.Audio;
using CsoundVisual.Engine;
using CsoundVisual.Interop;
using CsoundVisual.Localization;

namespace CsoundVisual.ViewModels;

/// <summary>An audio output path the user can choose in Settings › Audio (<see cref="Views.AudioSettingsView"/>).</summary>
/// <remarks>
/// <para>
/// There are two families of backends. <b>Host</b> backends (<see cref="HostMode"/> not null) are the
/// app's own WASAPI engine (<see cref="WasapiOutput"/>): Csound runs with host audio I/O and the app
/// pulls each buffer from it. <b>Csound</b> backends (<see cref="CsoundModule"/> not null) let Csound
/// open the device itself through one of its real-time modules (<c>-+rtaudio=pa_cb</c> and so on);
/// see <see cref="MainViewModel.ApplyAudioOptions"/> for the command-line options each family produces.
/// </para>
/// <para>
/// The record implements <see cref="INotifyPropertyChanged"/> only so that the computed, localised
/// <see cref="Label"/>/<see cref="Hint"/> can be refreshed in an open ComboBox after the language
/// changes (<see cref="RefreshTexts"/>); its data never changes after construction. Both language
/// variants are stored because <see cref="Loc.S"/> picks one at read time.
/// </para>
/// </remarks>
/// <param name="Id">Stable id stored in the settings (<c>"wasapi-shared"</c>, <c>"wasapi-exclusive"</c>, <c>"csound:pa_cb"</c>…).</param>
/// <param name="LabelEs">Name shown in the picker (Spanish).</param>
/// <param name="LabelEn">Name shown in the picker (English).</param>
/// <param name="HintEs">Tooltip explaining when to use it (Spanish).</param>
/// <param name="HintEn">Tooltip explaining when to use it (English).</param>
/// <param name="HostMode">For the app's own WASAPI output: shared or exclusive; <c>null</c> when Csound drives the device.</param>
/// <param name="CsoundModule">For Csound-driven output: the <c>-+rtaudio</c> module.</param>
public sealed record AudioBackend(string Id, string LabelEs, string LabelEn, string HintEs, string HintEn, WasapiMode? HostMode, string? CsoundModule)
    : INotifyPropertyChanged
{
    /// <summary>True when the application's own WASAPI output is used.</summary>
    public bool IsHost => HostMode is not null;

    /// <summary>Name shown in the picker, in the active language.</summary>
    public string Label => Loc.S(LabelEs, LabelEn);

    /// <summary>Tooltip, in the active language.</summary>
    public string Hint => Loc.S(HintEs, HintEn);

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Makes the bindings re-read <see cref="Label"/> and <see cref="Hint"/> (after a language change).</summary>
    public void RefreshTexts()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hint)));
    }

    /// <inheritdoc/>
    public override string ToString() => Label;
}

/// <summary>A latency preset as shown in the picker.</summary>
/// <remarks>
/// The preset means different things per backend: with the app's WASAPI engine it selects the
/// device period (see <see cref="LatencyPreset"/>); with Csound-driven backends it is translated to
/// <c>-b</c>/<c>-B</c> buffer sizes in <see cref="MainViewModel.ApplyAudioOptions"/>. Implements
/// <see cref="INotifyPropertyChanged"/> only for language refreshes, like <see cref="AudioBackend"/>.
/// </remarks>
/// <param name="Preset">The preset (its <c>ToString()</c> is what <see cref="AppSettings.LatencyPreset"/> stores).</param>
/// <param name="LabelEs">Name shown in the picker (Spanish).</param>
/// <param name="LabelEn">Name shown in the picker (English).</param>
/// <param name="HintEs">Tooltip (Spanish).</param>
/// <param name="HintEn">Tooltip (English).</param>
public sealed record LatencyOption(LatencyPreset Preset, string LabelEs, string LabelEn, string HintEs, string HintEn)
    : INotifyPropertyChanged
{
    /// <summary>Name shown in the picker, in the active language.</summary>
    public string Label => Loc.S(LabelEs, LabelEn);

    /// <summary>Tooltip, in the active language.</summary>
    public string Hint => Loc.S(HintEs, HintEn);

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Makes the bindings re-read <see cref="Label"/> and <see cref="Hint"/> (after a language change).</summary>
    public void RefreshTexts()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hint)));
    }

    /// <inheritdoc/>
    public override string ToString() => Label;
}

/// <summary>A choice for overriding the orchestra's ksmps (0 = keep the file's value).</summary>
/// <remarks>
/// A non-zero value is passed to Csound as <c>--ksmps=N</c>, which overrides the <c>ksmps</c> line of
/// the orchestra header: smaller values give finer control-rate timing at a higher CPU cost.
/// Stored in <see cref="AppSettings.KsmpsOverride"/>.
/// </remarks>
/// <param name="Value">ksmps, or 0 to keep the file's value.</param>
/// <param name="LabelEs">Name shown in the picker (Spanish).</param>
/// <param name="LabelEn">Name shown in the picker (English).</param>
public sealed record KsmpsOption(int Value, string LabelEs, string LabelEn) : INotifyPropertyChanged
{
    /// <summary>Creates a choice whose name is the same in every language (e.g. "64").</summary>
    public KsmpsOption(int value, string label) : this(value, label, label) { }

    /// <summary>Name shown in the picker, in the active language.</summary>
    public string Label => Loc.S(LabelEs, LabelEn);

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Makes the bindings re-read <see cref="Label"/> (after a language change).</summary>
    public void RefreshTexts() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));

    /// <inheritdoc/>
    public override string ToString() => Label;
}

/// <summary>
/// Audio output selection: backend (the app's own WASAPI shared/exclusive output, or one of
/// Csound's modules such as PortAudio for ASIO), output device, latency preset and ksmps override.
/// </summary>
/// <remarks>
/// <para>
/// The app's own WASAPI output is the default because Csound 7's built-in WASAPI module always
/// opens a ≈22 ms shared buffer and cannot use exclusive mode on many devices (it only tries
/// float formats). PortAudio remains available for ASIO drivers.
/// </para>
/// <para>
/// <b>Data flow.</b> The pickers in <see cref="Views.AudioSettingsView"/> bind two-way to the
/// <c>Selected…</c> properties below. Each one is a CommunityToolkit <c>[ObservableProperty]</c>:
/// the source generator turns the private field (e.g. <c>selectedAudioBackend</c>) into a public
/// property (<c>SelectedAudioBackend</c>) that raises <c>PropertyChanged</c> and then calls the
/// matching <c>partial void On…Changed</c> hook implemented here. Those hooks persist the choice in
/// <see cref="AppSettings"/> (saved immediately to <c>%APPDATA%\CsoundVisual\settings.json</c>).
/// Nothing is applied to a running performance: the choices are read by
/// <see cref="ApplyAudioOptions"/> the next time <c>Play</c> starts Csound.
/// </para>
/// <para>
/// <b>The <see cref="restoringAudio"/> guard.</b> When the lists are (re)filled and the saved
/// selection is restored programmatically, the hooks must not write back to the settings (that
/// would, for instance, overwrite a saved device that is merely unplugged with the first device of
/// the list). The flag is raised around every such restore.
/// </para>
/// <para>
/// <b>Threading.</b> Device enumeration (COM / Csound catalog instances) runs on pool threads via
/// <c>Task.Run</c>; everything after each <c>await</c> resumes on the UI thread
/// (the Avalonia synchronisation context), so the collections are only touched there. The live
/// statistics (<see cref="UpdateAudioStats"/>) are polled from the window's per-frame
/// <see cref="Tick"/>; the engine exposes them as thread-safe snapshots.
/// </para>
/// </remarks>
public sealed partial class MainViewModel
{
    /// <summary>Matches PortAudio device names such as <c>"Speakers (X) [Windows WASAPI, 0 in, 2 out]"</c>.</summary>
    /// <remarks>
    /// Groups: <c>name</c> = the device name, <c>api</c> = PortAudio host API ("ASIO", "Windows WASAPI"…),
    /// <c>in</c>/<c>out</c> = channel counts. Used by <see cref="SortPortAudio"/>. The body is generated
    /// at compile time by the <c>[GeneratedRegex]</c> source generator.
    /// </remarks>
    [GeneratedRegex(@"^(?<name>.*?)\s*\[(?<api>[^,\]]+),\s*(?<in>\d+) in,\s*(?<out>\d+) out\]\s*$")]
    private static partial Regex PortAudioName();

    /// <summary>Audio backends: the app's own WASAPI output first, then Csound's modules.</summary>
    public ObservableCollection<AudioBackend> AudioBackends { get; } = [];

    /// <summary>Output devices of <see cref="SelectedAudioBackend"/> (refilled by <see cref="LoadDevicesAsync"/> whenever the backend changes).</summary>
    public ObservableCollection<AudioDevice> OutputDevices { get; } = [];

    /// <summary>Id of the «None» entry of <see cref="InputDevices"/> (also the saved value).</summary>
    const string NoInputId = "none";

    /// <summary>
    /// Live input choices: «None» first, then «Windows default» (id <c>""</c>, the default when
    /// nothing was saved, like the output) and every active capture endpoint (WASAPI id).
    /// </summary>
    public ObservableCollection<AudioDevice> InputDevices { get; } = [];

    /// <summary>Selected live input («None» = Csound's <c>in</c> reads silence, as before).</summary>
    /// <remarks>
    /// Saved in <see cref="AppSettings.InputDevice"/> by <c>OnSelectedInputDeviceChanged</c>
    /// (<c>"none"</c> = no input, <c>""</c> = Windows default, otherwise a WASAPI endpoint id). Only
    /// the app's WASAPI engine honours it (see <see cref="ApplyAudioOptions"/>).
    /// </remarks>
    [ObservableProperty] AudioDevice? selectedInputDevice;

    /// <summary>True while a live input feeds Csound: shows the input meter in the output card.</summary>
    [ObservableProperty] bool isInputMeterVisible;

    /// <summary>True while Csound runs: shows the DSP/RAM row under the output meters.</summary>
    [ObservableProperty] bool isLoadMeterVisible;

    /// <summary>
    /// DSP load in percent, clamped to 0…100 for the progress bar: share of each audio buffer's time
    /// Csound needs to compute it (the engine's <c>DspLoad</c> is already smoothed).
    /// </summary>
    /// <remarks>
    /// <c>[NotifyPropertyChangedFor]</c> makes the generated setter also raise <c>PropertyChanged</c>
    /// for <see cref="DspWarn"/> and <see cref="DspDanger"/>, which are computed from this value and
    /// switch the bar's colour style (green / yellow at 70 % / red at 90 %).
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DspWarn), nameof(DspDanger))]
    double dspPercent;

    /// <summary>Highest recent DSP load (slowly falling), drawn as a thin marker.</summary>
    [ObservableProperty] double dspPeakPercent;

    /// <summary>«DSP 23 % · peak 41 % · dropouts 0» (or why it is not measured).</summary>
    [ObservableProperty] string dspText = "";

    /// <summary>«RAM 412 MB» of the whole app (Csound runs inside it).</summary>
    [ObservableProperty] string memoryText = "";

    /// <summary>Load between 70 % and 90 %: little headroom left.</summary>
    public bool DspWarn => DspPercent >= 70 && DspPercent < 90;

    /// <summary>Load of 90 % or more: dropouts are imminent.</summary>
    public bool DspDanger => DspPercent >= 90;

    /// <summary><see cref="Environment.TickCount64"/> (ms) of the last RAM reading; throttles <see cref="MemoryText"/> to once per second.</summary>
    long lastMemoryRead;

    /// <summary>True when the selected backend is the app's own WASAPI engine (the only one that uses the input picker).</summary>
    /// <remarks>Computed; <c>OnSelectedAudioBackendChanged</c> raises its change notification by hand.</remarks>
    public bool IsHostBackend => SelectedAudioBackend is { IsHost: true };

    /// <summary>
    /// Device label passed to the engine when Csound drives the device itself (PortAudio…), so it
    /// can report the device's Windows rate next to sr; <c>null</c> with the app's WASAPI engine.
    /// </summary>
    /// <remarks>
    /// Set by <see cref="ApplyAudioOptions"/> (empty string = Csound's default <c>dac</c>) and read
    /// right afterwards by <c>Launch</c> in <c>MainViewModel.cs</c> when it builds the
    /// <see cref="EngineJob"/>.
    /// </remarks>
    string? csoundDeviceLabel;

    /// <summary>Latency presets, in picker order (index 1, «Low», is the fallback when nothing is saved).</summary>
    public IReadOnlyList<LatencyOption> LatencyOptions { get; } =
    [
        new(LatencyPreset.Minimum, "Mínima", "Minimum",
            "El periodo más corto que acepta el dispositivo (≈3 ms en muchos equipos con Windows 11). Ideal para tocar en vivo; si oyes cortes, sube a «Baja».",
            "The shortest period the device accepts (≈3 ms on many Windows 11 machines). Best for live playing; if you hear dropouts, switch to «Low»."),
        new(LatencyPreset.Low, "Baja", "Low",
            "Cerca del doble del mínimo. Buen equilibrio entre respuesta y estabilidad.",
            "About twice the minimum. A good balance between responsiveness and stability."),
        new(LatencyPreset.Normal, "Normal (estable)", "Normal (stable)",
            "El periodo por defecto del dispositivo (≈10 ms en modo compartido). El más robusto, para mezclar o equipos cargados.",
            "The device's default period (≈10 ms in shared mode). The most robust, for mixing or busy machines."),
    ];

    /// <summary>ksmps override choices (index 0, «From file», is the default).</summary>
    public IReadOnlyList<KsmpsOption> KsmpsOptions { get; } =
    [
        new(0, "Del archivo", "From file"), new(16, "16"), new(32, "32"), new(64, "64"), new(128, "128"), new(256, "256"),
    ];

    /// <summary>Selected backend (saved as <see cref="AppSettings.AudioBackend"/>; changing it reloads <see cref="OutputDevices"/>).</summary>
    [ObservableProperty] AudioBackend? selectedAudioBackend;

    /// <summary>Selected output device (saved per backend in <see cref="AppSettings.OutputDevices"/>).</summary>
    [ObservableProperty] AudioDevice? selectedOutputDevice;

    /// <summary>Selected latency preset (saved as <see cref="AppSettings.LatencyPreset"/>).</summary>
    [ObservableProperty] LatencyOption? selectedLatency;

    /// <summary>Selected ksmps override (saved as <see cref="AppSettings.KsmpsOverride"/>).</summary>
    [ObservableProperty] KsmpsOption? selectedKsmps;

    /// <summary>True while devices are being enumerated.</summary>
    [ObservableProperty] bool isLoadingDevices;

    /// <summary>Live audio statistics shown in the status bar while playing ("DSP 12 % · cortes 0").</summary>
    [ObservableProperty] string audioStats = "";

    /// <summary>Description of the stream that was actually opened, for the Audio card.</summary>
    /// <remarks>Copied from <c>CsoundEngine.AudioDescription</c> once the engine has opened the device; cleared by <c>Launch</c>.</remarks>
    [ObservableProperty] string audioStreamInfo = "";

    /// <summary>
    /// True while a picker's selection is being set by code (list refill or restore from the
    /// settings); the <c>On…Changed</c> hooks then skip saving. See the class remarks.
    /// </summary>
    bool restoringAudio;

    /// <summary>
    /// Re-reads the translated names of the backends, latency presets and ksmps choices in the
    /// pickers (subscribed to <see cref="Loc.Changed"/> in the constructor).
    /// </summary>
    void RefreshAudioTexts()
    {
        foreach (var b in AudioBackends) b.RefreshTexts();
        foreach (var l in LatencyOptions) l.RefreshTexts();
        foreach (var k in KsmpsOptions) k.RefreshTexts();
        if (InputDevices.Count > 0)
            _ = LoadInputDevicesAsync(); // «None» / «Windows default» are created in the active language
        // This is the view model's only Loc.Changed handler (MainViewModel.cs subscribes it), so the
        // other partials' computed, translated texts are re-notified from here too:
        RefreshDawTexts(); // the output card title («SALIDA» / «OUTPUT»), MainViewModel.Daw.cs
        OnLinkChanged();   // the Link status text and tooltip, MainViewModel.Link.cs
    }

    /// <summary>User preferences (the window stores its size and position here).</summary>
    public AppSettings Settings => settings;

    /// <summary>
    /// Opens the classic Windows Sound control panel (mmsys.cpl): device ▸ Properties ▸
    /// Advanced is where the sample rate (e.g. 48000 Hz for 3 ms exclusive mode) is changed.
    /// </summary>
    /// <remarks>
    /// <c>[RelayCommand]</c> generates the bindable <c>OpenSoundPanelCommand</c> property from this
    /// method (the "Windows Sound panel" button). Failures are reported in the console, not thrown.
    /// </remarks>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void OpenSoundPanel()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("control.exe", "mmsys.cpl,,0") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AddInfo(Loc.S("No se pudo abrir el panel de sonido: ", "Could not open the Sound control panel: ") + ex.Message);
        }
    }

    /// <summary>Shows a text report in a window (title, text); provided by the view.</summary>
    /// <remarks>
    /// The view model never references Avalonia windows; the main window assigns this delegate
    /// (a modal <see cref="Views.InfoDialog"/>). When it is unset the report is silently dropped.
    /// </remarks>
    public Func<string, string, Task>? ShowReport { get; set; }

    /// <summary>
    /// Asks every output device what it can do (formats, shared and exclusive latency) without
    /// playing anything, and shows the report.
    /// </summary>
    /// <remarks>
    /// Generated command: <c>AudioDiagnosticsCommand</c> (an async relay command; the button is
    /// disabled while it runs). <see cref="WasapiOutput.Probe"/> activates COM audio clients, so it
    /// runs on a pool (MTA) thread; the report is shown back on the UI thread.
    /// </remarks>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    async Task AudioDiagnostics()
    {
        var devices = await Task.Run(WasapiOutput.Probe);
        var text = Loc.S("Lo que puede hacer cada salida de audio de este equipo (sin reproducir nada):\n\n",
                         "What each audio output of this computer can do (nothing is played):\n\n") +
                   string.Join("\n\n", devices) +
                   Loc.S("\n\nConsejos:\n" +
                         "• «exclusivo» con periodo de 3 ms da la latencia más baja sin ASIO (elige «Windows · WASAPI exclusivo»).\n" +
                         "• «compartido … sin modo de baja latencia» significa que el driver no ofrece periodos cortos: queda en ≈10 ms.\n" +
                         "• Los audífonos Bluetooth agregan su propia latencia (100 ms o más) que ninguna app puede quitar.\n" +
                         "• Para ASIO (p. ej. Yamaha Steinberg USB ASIO) usa «Csound · PortAudio».",
                         "\n\nTips:\n" +
                         "• «exclusive» with a 3 ms period gives the lowest latency without ASIO (choose «Windows · WASAPI exclusive»).\n" +
                         "• «shared … no low-latency mode» means the driver offers no short periods: it stays at ≈10 ms.\n" +
                         "• Bluetooth headphones add their own latency (100 ms or more) that no app can remove.\n" +
                         "• For ASIO (e.g. Yamaha Steinberg USB ASIO) use «Csound · PortAudio».");
        if (ShowReport is not null)
            await ShowReport(Loc.S("Diagnóstico de audio", "Audio diagnostics"), text);
    }

    /// <summary>Fills the backend list (own WASAPI first, then Csound's modules) and restores the saved choices.</summary>
    /// <remarks>
    /// Called once at start-up from <c>LoadCatalogAsync</c>, after the opcode list. Csound's modules
    /// are discovered with a short-lived Csound instance (<see cref="CsoundCatalog.GetAudioModules"/>),
    /// so the list reflects the plug-ins actually installed. Known module ids get friendly bilingual
    /// names and hints; unknown ones keep Csound's label.
    /// </remarks>
    async Task LoadAudioBackendsAsync()
    {
        var modules = await Task.Run(CsoundCatalog.GetAudioModules);
        // Latency and ksmps are restored under the guard (no write-back). The backend is assigned
        // AFTER the guard is lowered on purpose: its On…Changed hook must run to load the device list
        // (re-saving the same backend id is harmless).
        restoringAudio = true;
        AudioBackends.Clear();
        AudioBackends.Add(new AudioBackend("wasapi-shared",
            "Windows · WASAPI compartido (recomendado)", "Windows · WASAPI shared (recommended)",
            "La salida estándar de Windows 11 manejada por la app, con periodos cortos (IAudioClient3). Las demás aplicaciones siguen sonando.",
            "The standard Windows 11 output, driven by the app with short periods (IAudioClient3). Other apps keep playing.",
            WasapiMode.Shared, null));
        AudioBackends.Add(new AudioBackend("wasapi-exclusive",
            "Windows · WASAPI exclusivo (latencia mínima)", "Windows · WASAPI exclusive (lowest latency)",
            "La app toma el dispositivo para ella sola: la latencia más baja sin ASIO. Las demás aplicaciones se silencian mientras suena.",
            "The app takes the device for itself: the lowest latency without ASIO. Other apps are muted while it plays.",
            WasapiMode.Exclusive, null));
        foreach (var m in modules)
        {
            var (labelEs, labelEn) = m.Id switch
            {
                "pa_cb" => ("Csound · PortAudio (ASIO, MME, WDM-KS)", "Csound · PortAudio (ASIO, MME, WDM-KS)"),
                "pa_bl" => ("Csound · PortAudio bloqueante", "Csound · PortAudio blocking"),
                "wasapi" => ("Csound · WASAPI interno", "Csound · built-in WASAPI"),
                _ => ("Csound · " + m.Label, "Csound · " + m.Label),
            };
            var (hintEs, hintEn) = m.Id switch
            {
                "pa_cb" => ("Csound maneja el audio con PortAudio. Úsalo para drivers ASIO (p. ej. Yamaha Steinberg USB ASIO).",
                            "Csound drives the audio through PortAudio. Use it for ASIO drivers (e.g. Yamaha Steinberg USB ASIO)."),
                "wasapi" => ("El módulo WASAPI de Csound 7: siempre abre un buffer compartido de ≈22 ms. Incluido por compatibilidad.",
                             "Csound 7's WASAPI module: always opens a ≈22 ms shared buffer. Included for compatibility."),
                _ => ("Módulo de audio en tiempo real de Csound.", "Csound real-time audio module."),
            };
            AudioBackends.Add(new AudioBackend("csound:" + m.Id, labelEs, labelEn, hintEs, hintEn, null, m.Id));
        }
        SelectedLatency = LatencyOptions.FirstOrDefault(l => l.Preset.ToString() == settings.LatencyPreset) ?? LatencyOptions[1];
        SelectedKsmps = KsmpsOptions.FirstOrDefault(k => k.Value == settings.KsmpsOverride) ?? KsmpsOptions[0];
        restoringAudio = false;
        SelectedAudioBackend = AudioBackends.FirstOrDefault(b => b.Id == settings.AudioBackend) ?? AudioBackends[0];
        await LoadInputDevicesAsync();
    }

    /// <summary>
    /// Fills <see cref="InputDevices"/> (capture endpoints are enumerated on a pool thread, which
    /// is MTA as COM requires) and restores the saved choice. A saved device that is not plugged in
    /// now shows «None» without forgetting the saved id.
    /// </summary>
    /// <remarks>
    /// Error handling: also called fire-and-forget (after a language switch), so a failure of the
    /// endpoint enumeration is caught and reported in the console instead of being lost in an
    /// unobserved task. The list then contains only «None» (so the picker is never empty) and the
    /// saved device id is left untouched; <c>restoringAudio</c> is restored in <c>finally</c>.
    /// </remarks>
    async Task LoadInputDevicesAsync()
    {
        List<WasapiEndpoint> endpoints;
        try
        {
            endpoints = await Task.Run(WasapiInput.ListEndpoints);
        }
        catch (Exception ex)
        {
            ConsoleLines.Add(new ConsoleLine(Loc.S(
                $"⚠ No se pudo listar los dispositivos de entrada de audio: {ex.Message}",
                $"⚠ Could not list the audio input devices: {ex.Message}"), MessageKind.Warning));
            endpoints = [];
        }
        // Save and restore the previous guard value instead of resetting it to false: this method
        // can run while another restore is in progress (e.g. from LoadAudioBackendsAsync).
        bool wasRestoring = restoringAudio;
        restoringAudio = true;
        try
        {
            InputDevices.Clear();
            InputDevices.Add(new AudioDevice(NoInputId, Loc.S("Ninguna", "None"), 0));
            foreach (var e in endpoints)
                InputDevices.Add(new AudioDevice(e.Id ?? "", e.Name, 0));
            // Nothing saved yet → «Windows default», like the output. When Windows has no usable
            // recording device (microphone disabled or blocked) the engine just plays without input
            // and says so once in the console (see CsoundEngine).
            string saved = settings.InputDevice ?? "";
            // Restore the «Input channels» routing together with the device (engine default = stereo).
            engine.InputRouting = (Audio.InputRouting)Math.Clamp(settings.InputRouting, 0, 3);
            OnPropertyChanged(nameof(InputRoutingIndex));
            // A missing device falls back to «None» on screen only; the guard keeps settings.InputDevice intact.
            SelectedInputDevice = InputDevices.FirstOrDefault(d => d.Id == saved) ?? InputDevices[0];
        }
        finally
        {
            restoringAudio = wasRestoring;
        }
    }

    /// <summary>
    /// Generated-property hook for <see cref="SelectedInputDevice"/> (CommunityToolkit calls this
    /// <c>partial</c> method after the value changes): persists a user choice.
    /// </summary>
    /// <param name="value">The new selection.</param>
    partial void OnSelectedInputDeviceChanged(AudioDevice? value)
    {
        if (value is null || restoringAudio) return;
        settings.InputDevice = value.Id;
        settings.Save();
    }

    /// <summary>
    /// Labels of the «Input channels» choices, in <see cref="Audio.InputRouting"/> order (the
    /// combo box index IS the enum value). Rebuilt on each read so they follow the UI language.
    /// </summary>
    public IReadOnlyList<string> InputRoutingChoices =>
    [
        Loc.S("Estéreo: entrada 1 → izquierda, entrada 2 → derecha (dos canales independientes)",
              "Stereo: input 1 → left, input 2 → right (two independent channels)"),
        Loc.S("Mono: solo la entrada 1, a ambos lados", "Mono: input 1 only, to both sides"),
        Loc.S("Mono: solo la entrada 2, a ambos lados", "Mono: input 2 only, to both sides"),
        Loc.S("Mono: entradas 1 + 2 sumadas, a ambos lados", "Mono: inputs 1 + 2 summed, to both sides"),
    ];

    /// <summary>
    /// Selected «Input channels» routing (index into <see cref="InputRoutingChoices"/>). Applied
    /// to the engine immediately — also while playing — and saved in the settings.
    /// </summary>
    public int InputRoutingIndex
    {
        get => (int)engine.InputRouting;
        set
        {
            int v = Math.Clamp(value, 0, 3);
            if (v == (int)engine.InputRouting) return;
            engine.InputRouting = (Audio.InputRouting)v;
            settings.InputRouting = v;
            settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Generated-property hook for <see cref="SelectedAudioBackend"/>: notifies
    /// <see cref="IsHostBackend"/>, persists a user choice and reloads the device list for the new
    /// backend (fire-and-forget; <see cref="LoadDevicesAsync"/> ignores stale results).
    /// </summary>
    /// <param name="value">The new backend.</param>
    partial void OnSelectedAudioBackendChanged(AudioBackend? value)
    {
        OnPropertyChanged(nameof(IsHostBackend)); // computed property: always re-evaluate, even while restoring
        if (value is null || restoringAudio) return;
        settings.AudioBackend = value.Id;
        settings.Save();
        _ = LoadDevicesAsync(value);
    }

    /// <summary>
    /// Fills <see cref="OutputDevices"/> for <paramref name="backend"/> and restores the device saved
    /// for that backend (or the first one, usually the Windows default).
    /// </summary>
    /// <param name="backend">The backend whose devices to list.</param>
    /// <remarks>
    /// Host backends list WASAPI render endpoints; Csound backends ask Csound for the module's
    /// devices (slow: it spins up a Csound instance) and then sort them with
    /// <see cref="SortPortAudio"/>. Both run on a pool thread. If the user switched backend while the
    /// enumeration ran, the result is discarded so a slow list cannot overwrite a newer one.
    /// <para>Error handling: this method is called fire-and-forget (<c>_ = LoadDevicesAsync(…)</c>),
    /// so an exception (COM failure while enumerating endpoints, Csound failing to start the module…)
    /// used to vanish into an unobserved task and leave <see cref="IsLoadingDevices"/> stuck at true
    /// (spinner forever) and possibly <c>restoringAudio</c> stuck at true (every later device choice
    /// silently not saved). Now the exception is caught and reported in the console, and both flags
    /// are always reset in <c>finally</c>. <see cref="IsLoadingDevices"/> is reset only when this call
    /// is still the current one, because a newer load (other backend) owns the flag otherwise.</para>
    /// </remarks>
    async Task LoadDevicesAsync(AudioBackend backend)
    {
        IsLoadingDevices = true;
        bool wasRestoring = restoringAudio;
        try
        {
            var devices = await Task.Run(() => backend.IsHost
                ? WasapiOutput.ListEndpoints().Select(e => new AudioDevice(e.Id ?? "", e.Name, 0)).ToList()
                : SortPortAudio(CsoundCatalog.GetOutputDevices(backend.CsoundModule!)));
            if (SelectedAudioBackend != backend)
                return; // the user picked another backend meanwhile
            restoringAudio = true;
            OutputDevices.Clear();
            foreach (var d in devices)
                OutputDevices.Add(d);
            string? saved = settings.OutputDevices.GetValueOrDefault(backend.Id);
            SelectedOutputDevice = OutputDevices.FirstOrDefault(d => d.Id == saved) ?? OutputDevices.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ConsoleLines.Add(new ConsoleLine(Loc.S(
                $"⚠ No se pudo listar los dispositivos de salida de «{backend.Label}»: {ex.Message}",
                $"⚠ Could not list the output devices of «{backend.Label}»: {ex.Message}"), MessageKind.Warning));
        }
        finally
        {
            restoringAudio = wasRestoring;
            if (SelectedAudioBackend == backend)
                IsLoadingDevices = false;
        }
    }

    /// <summary>Puts ASIO devices first and tags each PortAudio device with its host API ("ASIO · …").</summary>
    /// <param name="devices">Devices as reported by Csound (names in PortAudio's bracketed format).</param>
    /// <returns>
    /// The devices ordered ASIO → WASAPI → WDM-KS → DirectSound → others (MME), each renamed to
    /// <c>"API · name"</c>. Names that do not match <see cref="PortAudioName"/> keep their text and
    /// go first (rank −1). <c>OrderBy</c> is stable, so Csound's order is kept within each API.
    /// Only the display name changes; the <c>Id</c> passed to <c>-o</c> is untouched.
    /// </returns>
    static List<AudioDevice> SortPortAudio(List<AudioDevice> devices)
    {
        static int Rank(string api) => api switch
        {
            "ASIO" => 0,
            "Windows WASAPI" => 1,
            "Windows WDM-KS" => 2,
            "Windows DirectSound" => 3,
            _ => 4,
        };
        var parsed = devices.Select(d =>
        {
            var m = PortAudioName().Match(d.Name);
            if (!m.Success) return (Device: d, Rank: -1);
            string api = m.Groups["api"].Value;
            string shortApi = api.Replace("Windows ", "");
            return (Device: d with { Name = $"{shortApi} · {m.Groups["name"].Value}" }, Rank: Rank(api));
        });
        return [.. parsed.OrderBy(p => p.Rank).Select(p => p.Device)];
    }

    /// <summary>Generated-property hook for <see cref="SelectedOutputDevice"/>: remembers the device per backend.</summary>
    /// <param name="value">The new device.</param>
    partial void OnSelectedOutputDeviceChanged(AudioDevice? value)
    {
        if (value is null || restoringAudio || SelectedAudioBackend is null) return;
        outputFallback = false; // an explicit choice by the user: try that device again
        retriedStalledOutput = false;
        settings.OutputDevices[SelectedAudioBackend.Id] = value.Id;
        settings.Save();
    }

    /// <summary>Generated-property hook for <see cref="SelectedLatency"/>: persists the preset name.</summary>
    /// <param name="value">The new preset.</param>
    partial void OnSelectedLatencyChanged(LatencyOption? value)
    {
        if (value is null || restoringAudio) return;
        settings.LatencyPreset = value.Preset.ToString();
        settings.Save();
    }

    /// <summary>Generated-property hook for <see cref="SelectedKsmps"/>: persists the override (0 = from file).</summary>
    /// <param name="value">The new choice.</param>
    partial void OnSelectedKsmpsChanged(KsmpsOption? value)
    {
        if (value is null || restoringAudio) return;
        settings.KsmpsOverride = value.Value;
        settings.Save();
    }

    /// <summary>
    /// Adds the real-time output options for the selected backend to <paramref name="options"/>
    /// and returns the host WASAPI settings when the app drives the device itself.
    /// </summary>
    /// <param name="options">
    /// Csound command-line options already extracted from the document's <c>&lt;CsOptions&gt;</c>
    /// (<c>CsdTools.ExtractOptions</c>); modified in place. Options inserted at the front come
    /// before the file's own ones; Csound applies them in order, so where both set the same flag the
    /// later one (the file's, or the ones appended at the end here) wins.
    /// </param>
    /// <returns>
    /// The <see cref="HostAudioSettings"/> for the app's WASAPI engine (device id, shared/exclusive,
    /// latency preset, optional live input), or <c>null</c> when Csound opens the device itself.
    /// </returns>
    /// <remarks>
    /// Called only by <c>Play</c> (rendering to a file builds its own options). Side effect: sets
    /// <see cref="csoundDeviceLabel"/>. The latency preset maps to Csound buffer sizes as
    /// Minimum = <c>-b128 -B256</c>, Low = <c>-b256 -B512</c>, Normal = <c>-b512 -B2048</c> (frames).
    /// </remarks>
    HostAudioSettings? ApplyAudioOptions(List<string> options)
    {
        var backend = SelectedAudioBackend;
        var preset = SelectedLatency?.Preset ?? LatencyPreset.Low;
        if (SelectedKsmps is { Value: > 0 } k)
            options.Add("--ksmps=" + k.Value);

        // "-odac" at index 0: with the host engine it just tells Csound to run in real time (the
        // host audio I/O callbacks replace the device); for Csound backends it is rewritten below.
        options.Insert(0, "-odac");
        // Live input: «None» → null; «Windows default» (id "") → null device id; else the endpoint id.
        AudioInputSettings? inputSettings = SelectedInputDevice is { Id: not NoInputId } inDev
            ? new AudioInputSettings(inDev.Id.Length == 0 ? null : inDev.Id)
            : null;
        csoundDeviceLabel = null;
        if (backend is { IsHost: true })
        {
            string? outputId = string.IsNullOrEmpty(SelectedOutputDevice?.Id) ? null : SelectedOutputDevice!.Id;
            if (outputFallback && outputId is not null)
            {
                // The chosen output stalled earlier in this session: keep using Windows' default
                // output (the saved choice is untouched) until the user picks an output again.
                AddInfo(Loc.S($"🔁 Usando la salida predeterminada de Windows: «{SelectedOutputDevice!.Name}» no respondió. Elígela de nuevo en Configuración › Audio para volver a intentarlo.",
                              $"🔁 Using the Windows default output: «{SelectedOutputDevice!.Name}» did not respond. Pick it again in Settings › Audio to retry it."));
                outputId = null;
            }
            return new HostAudioSettings(outputId, backend.HostMode!.Value, preset, inputSettings);
        }

        if (inputSettings is not null)
            AddInfo(Loc.S("La entrada elegida en Configuración › Audio solo se usa con «Windows · WASAPI». Con PortAudio/ASIO agrega -iadc (o -iadcN) en <CsOptions>.",
                          "The input chosen in Settings › Audio is only used with «Windows · WASAPI». With PortAudio/ASIO add -iadc (or -iadcN) to <CsOptions>."));

        // Csound drives the device: module, device and buffer sizes (-b software, -B hardware).
        csoundDeviceLabel = SelectedOutputDevice is { Id: { Length: > 0 } and not "dac" } chosen ? chosen.Name : "";
        // Csound device ids look like "dac3"; the placeholder "-odac" at index 0 becomes "-odac3".
        options[0] = "-o" + (SelectedOutputDevice?.Id is { Length: > 0 } id ? id : "dac");
        if (backend?.CsoundModule is { } module)
            options.Insert(0, "-+rtaudio=" + module);
        var (b, bb) = preset switch
        {
            LatencyPreset.Minimum => (128, 256),
            LatencyPreset.Low => (256, 512),
            _ => (512, 2048),
        };
        options.Add($"-b{b}");
        options.Add($"-B{bb}");
        return null;
    }

    /// <summary>
    /// DSP/RAM row: the DSP load is the time Csound spends computing a buffer divided by the
    /// buffer's duration (100 % = it barely keeps up; above that the sound breaks up). It is the
    /// useful number for real-time audio, unlike the overall CPU percentage. RAM is read once a
    /// second (Csound runs inside the app, so its tables and samples count there).
    /// </summary>
    /// <param name="load">
    /// <c>CsoundEngine.DspLoad</c> as a fraction (1.0 = 100 %), or a negative value when it is not
    /// measured (Csound-driven backends and offline renders, where the app does not time the buffers).
    /// </param>
    /// <remarks>
    /// Runs every UI frame while Csound is running (via <see cref="UpdateAudioStats"/>). The peak
    /// marker is a simple decaying maximum: it follows any new high immediately and otherwise falls
    /// 0.15 percentage points per frame (≈9 points per second at 60 fps). <c>Tick</c> hides the row
    /// and resets the peak when the performance stops.
    /// </remarks>
    void UpdateLoadMeter(double load)
    {
        if (!IsLoadMeterVisible) IsLoadMeterVisible = true;
        if (load < 0)
        {
            DspPercent = 0;
            DspPeakPercent = 0;
            DspText = Loc.S("DSP: se mide con el motor «Windows · WASAPI»", "DSP: measured with the «Windows · WASAPI» engine");
        }
        else
        {
            double pct = load * 100;
            DspPercent = Math.Min(pct, 100);
            DspPeakPercent = Math.Min(100, Math.Max(pct, DspPeakPercent - 0.15));
            DspText = Loc.S($"DSP {pct:0} %  ·  pico {DspPeakPercent:0} %  ·  cortes {engine.Underruns}",
                            $"DSP {pct:0} %  ·  peak {DspPeakPercent:0} %  ·  dropouts {engine.Underruns}");
        }
        long now = Environment.TickCount64;
        if (now - lastMemoryRead >= 1000)
        {
            lastMemoryRead = now;
            // Working set = physical memory in use by the process (app + Csound + plug-ins), in MiB.
            MemoryText = $"RAM {Environment.WorkingSet / (1024.0 * 1024):0} MB";
        }
    }

    /// <summary>
    /// True after the chosen output device stalled (its clock stopped, see
    /// <c>CsoundEngine.LastRunOutputStalled</c>): later runs of this session use the Windows default
    /// output instead. Cleared when the user selects an output again. Not saved.
    /// </summary>
    bool outputFallback;

    /// <summary>True once the automatic restart on the default output was done (only one try per stall).</summary>
    bool retriedStalledOutput;

    /// <summary>
    /// Called by <c>OnFinished</c>: when a live run ended because the chosen output device stalled,
    /// switches to the Windows default output and plays the same document again, once. Returns true
    /// when a restart was queued (the caller then lets <c>StartQueuedPlayback</c> run it).
    /// </summary>
    /// <param name="finishedMode">Mode of the run that just ended.</param>
    bool TryRestartOnDefaultOutput(RunMode finishedMode)
    {
        if (finishedMode != RunMode.Play || !engine.LastRunOutputStalled || retriedStalledOutput)
            return false;
        if (string.IsNullOrEmpty(SelectedOutputDevice?.Id) || lastRunDocument is not { } doc || !Documents.Contains(doc))
            return false; // already on the default output, or nothing to replay (e.g. a tool's host)
        outputFallback = true;
        retriedStalledOutput = true;
        AddInfo(Loc.S($"🔁 «{SelectedOutputDevice!.Name}» no responde: se suelta y se vuelve a tocar con la salida predeterminada de Windows.",
                      $"🔁 «{SelectedOutputDevice!.Name}» is not responding: it is released and playback restarts on the Windows default output."));
        playAfterStop ??= doc;
        return true;
    }

    /// <summary>Refreshes <see cref="AudioStats"/> from the engine (called every frame while running).</summary>
    /// <remarks>
    /// Called from <see cref="Tick"/> on the UI thread. The engine fields read here are written by
    /// the performance / capture threads and are only snapshots. Properties are assigned only when
    /// their value differs where that matters (<see cref="AudioStreamInfo"/>,
    /// <see cref="IsInputMeterVisible"/>) to avoid needless layout work; the generated setters also
    /// skip equal values, so the text properties cost little when unchanged.
    /// </remarks>
    void UpdateAudioStats()
    {
        if (engine.AudioDescription is { } d && AudioStreamInfo != d)
            AudioStreamInfo = d;
        double load = engine.DspLoad;
        string inputStats = engine.InputReader is { } reader
            ? Loc.S($"  ·  entrada: cortes {reader.Underruns}", $"  ·  input: dropouts {reader.Underruns}")
            : "";
        AudioStats = load < 0
            ? ""
            : Loc.S($"DSP {load * 100:0} %  ·  cortes {engine.Underruns}", $"DSP {load * 100:0} %  ·  dropouts {engine.Underruns}") + inputStats;
        UpdateLoadMeter(load);
        bool hasInput = engine.HasLiveInput;
        if (IsInputMeterVisible != hasInput)
            IsInputMeterVisible = hasInput;
    }
}
