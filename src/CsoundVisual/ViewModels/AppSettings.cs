using System.Text.Json;

namespace CsoundVisual.ViewModels;

/// <summary>
/// User preferences persisted between sessions in <c>%APPDATA%\CsoundVisual\settings.json</c>.
/// Failures to read or write are ignored: settings are a convenience, never required.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Full path of the settings file.</summary>
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CsoundVisual", "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Audio backend id (<c>"wasapi-shared"</c>, <c>"wasapi-exclusive"</c>, <c>"csound:pa_cb"</c>…).</summary>
    public string? AudioBackend { get; set; }

    /// <summary>Chosen output device per backend (WASAPI endpoint id, or Csound device id such as <c>"dac3"</c>).</summary>
    public Dictionary<string, string> OutputDevices { get; set; } = [];

    /// <summary>
    /// Live audio input of the app's WASAPI engine: <c>null</c> or <c>"none"</c> = no input (the
    /// default, nothing changes for existing users), <c>""</c> = the Windows default recording
    /// device, anything else = a capture endpoint id.
    /// </summary>
    public string? InputDevice { get; set; }

    /// <summary>
    /// Routing of the live input's channels to Csound (<see cref="Audio.InputRouting"/> as int:
    /// 0 = stereo pair, 1 = input 1 to all, 2 = input 2 to all, 3 = sum to all).
    /// </summary>
    public int InputRouting { get; set; }

    /// <summary>Latency preset name (<c>Minimum</c>, <c>Low</c>, <c>Normal</c>).</summary>
    public string? LatencyPreset { get; set; }

    /// <summary>ksmps override; 0 keeps the value written in the file.</summary>
    public int KsmpsOverride { get; set; }

    /// <summary>Whether variable values are sampled while playing (shown in the hover card and watch panel).</summary>
    public bool LiveValues { get; set; } = true;

    /// <summary>MIDI input ports enabled last time ("name  ·  backend").</summary>
    public List<string> MidiInputs { get; set; } = [];

    /// <summary>MIDI learn mappings: "channel:cc" (0-based channel) → control channel name.</summary>
    public Dictionary<string, string> MidiBindings { get; set; } = [];

    /// <summary>Main window position in physical pixels (null on first run).</summary>
    public int? WindowX { get; set; }

    /// <summary>Main window position in physical pixels (null on first run).</summary>
    public int? WindowY { get; set; }

    /// <summary>Main window size in logical pixels when not maximized.</summary>
    public double? WindowWidth { get; set; }

    /// <summary>Main window size in logical pixels when not maximized.</summary>
    public double? WindowHeight { get; set; }

    /// <summary>Whether the main window was maximized when the app closed.</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>Interface language: "en" (default) or "es".</summary>
    public string? Language { get; set; }

    /// <summary>Code editor font size in points (null = default 14).</summary>
    public double? EditorFontSize { get; set; }

    /// <summary>Wrap long lines in the code editor.</summary>
    public bool EditorWordWrap { get; set; }

    /// <summary>Colour preset of the app (a name from <c>AppTheme.Presets</c>; null = default).</summary>
    public string? ThemePreset { get; set; }

    /// <summary>False-colour palette of the 3D waterfall (index into <c>ColorMaps.Names</c>).</summary>
    public int WaterfallPalette { get; set; }

    /// <summary>Frequency bands of the 3D waterfall.</summary>
    public int WaterfallBands { get; set; } = 64;

    /// <summary>Spectra per second (new rows per second) of the 3D waterfall.</summary>
    public int WaterfallRate { get; set; } = 30;

    /// <summary>Seconds of history shown by the 3D waterfall.</summary>
    public double WaterfallSeconds { get; set; } = 3;

    /// <summary>Ableton Link: join the session on the local network (restored at start-up).</summary>
    public bool LinkEnabled { get; set; }

    /// <summary>Ableton Link quantum: beats per bar for phase and bar-quantised starts (Live's default is 4).</summary>
    public double LinkQuantum { get; set; } = 4;

    /// <summary>Ableton Link: share play/stop with the other peers («Start Stop Sync»).</summary>
    public bool LinkStartStopSync { get; set; } = true;

    /// <summary>Files that were open when the application closed, restored on the next start.</summary>
    public List<string> OpenFiles { get; set; } = [];

    /// <summary>VST3 insert chain after Csound (plug-in path, class id, state, bypass), restored at start.</summary>
    public List<Integration.VstSlotSettings> PluginChain { get; set; } = [];

    /// <summary>VST3 modules that are ignored (lower-case path → reason, see <c>VstPluginManager</c>).</summary>
    public Dictionary<string, string> PluginBlacklist { get; set; } = [];

    /// <summary>Paint Rooms: last pitch axis, scale file, canvas size, synthesis settings and brush (null until first used).</summary>
    public Paint.PaintRoomSettings? Paint { get; set; }

    /// <summary>Loads the settings, or returns defaults when the file is missing or invalid.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception)
        {
            // A corrupt settings file must never prevent the app from starting.
        }
        return new();
    }

    /// <summary>Writes the settings to disk.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception)
        {
            // Not being able to save preferences is not worth interrupting the user.
        }
    }
}
