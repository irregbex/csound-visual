using Avalonia.Threading;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsoundVisual.Engine;
using CsoundVisual.Localization;
using CsoundVisual.Midi;

namespace CsoundVisual.ViewModels;

/// <summary>A MIDI input port with an on/off switch in the MIDI card.</summary>
/// <remarks>
/// The item does not open the port itself: the generated <c>IsEnabled</c> setter calls
/// <see cref="OnIsEnabledChanged(bool)"/>, which forwards to the <c>toggled</c> callback supplied by
/// <see cref="MainViewModel"/> (<c>OnPortToggled</c>). That keeps the port logic and the settings
/// persistence in one place while the checkbox in the card simply binds <c>IsEnabled</c>.
/// </remarks>
/// <param name="port">The port as listed by <see cref="MidiHub.ListPorts"/>.</param>
/// <param name="toggled">Called with the item and its new state whenever <c>IsEnabled</c> changes.</param>
public sealed partial class MidiPortItem(MidiPort port, Action<MidiPortItem, bool> toggled) : ObservableObject
{
    /// <summary>The port.</summary>
    public MidiPort Port { get; } = port;

    /// <summary>Whether the port is open (its messages reach Csound and the UI).</summary>
    [ObservableProperty] bool isEnabled;

    /// <summary>Error from the last open attempt, if any (shown under the port name).</summary>
    [ObservableProperty] string? error;

    /// <summary>
    /// CommunityToolkit hook: runs after <c>IsEnabled</c> changes. Note that the callback may set
    /// <c>IsEnabled</c> back to <c>false</c> when opening fails, which re-enters this method with
    /// <c>false</c> (harmless: closing a port that is not open does nothing).
    /// </summary>
    /// <param name="value">The new state.</param>
    partial void OnIsEnabledChanged(bool value) => toggled(this, value);
}

/// <summary>
/// MIDI: input ports, the MIDI monitor, the on-screen keyboard and MIDI learn (controller →
/// slider mappings). All MIDI goes through one <see cref="MidiHub"/>, which also feeds Csound.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threads.</b> Port messages arrive on driver threads (WinMM callback, Windows MIDI Services
/// event thread) and go straight into <see cref="MidiHub.Inject"/>, which queues them both for
/// Csound (read by the performance thread through the host MIDI callbacks) and for the UI. This
/// partial never sees the driver threads: <see cref="ProcessMidi"/> drains the UI queue from
/// <see cref="Tick"/> on every animation frame, so everything below runs on the UI thread.
/// </para>
/// <para>
/// <b>Persistence.</b> Enabled ports are saved by display name in
/// <see cref="AppSettings.MidiInputs"/>; controller mappings in <see cref="AppSettings.MidiBindings"/>
/// as <c>"channel:cc" → channel name</c> (channel 0-based). Mappings are by <i>channel name</i>,
/// not per document, so a CC learned on <c>cutoff</c> drives the <c>cutoff</c> slider of whatever
/// document is selected.
/// </para>
/// <para>
/// <b>Values.</b> <see cref="MidiMessage.Value"/> is normalised 0…1 at full resolution (7-bit for
/// MIDI 1.0, 32-bit for MIDI 2.0 controllers), so a mapped slider moves smoothly with MIDI 2.0.
/// </para>
/// </remarks>
public sealed partial class MainViewModel
{
    /// <summary>The single MIDI router of the app (ports, on-screen keyboard → Csound, UI, learn).</summary>
    readonly MidiHub midiHub = new();

    /// <summary>The MIDI router (the window connects the on-screen keyboard to it).</summary>
    public MidiHub Midi => midiHub;

    /// <summary>Available input ports.</summary>
    public ObservableCollection<MidiPortItem> MidiPorts { get; } = [];

    /// <summary>Last received messages as text, newest first (capped at 60 entries by <see cref="ProcessMidi"/>).</summary>
    public ObservableCollection<string> MidiMonitor { get; } = [];

    /// <summary>Status line about Windows MIDI Services (MIDI 2.0) shown in the MIDI card: "active · origin of the API" or the reason it is unavailable.</summary>
    [ObservableProperty] string midi2Info = "";

    /// <summary>Whether the on-screen keyboard strip is shown.</summary>
    [ObservableProperty] bool isKeyboardVisible = true;

    /// <summary>The slider waiting for a controller to be moved (MIDI learn), or <c>null</c>.</summary>
    ChannelControlViewModel? learning;

    /// <summary>
    /// Raised on the UI thread for incoming notes, so the keyboard can highlight them
    /// (MIDI note number, <c>true</c> = note-on). Also raised for notes played on the on-screen
    /// keyboard itself, since those go through the hub too.
    /// </summary>
    public event Action<int, bool>? ExternalNote;

    /// <summary>
    /// Lists ports and re-opens the ones enabled last time (generated <c>RefreshMidiPortsCommand</c>,
    /// the refresh button of the MIDI card; also run once by the constructor at start-up).
    /// </summary>
    /// <remarks>
    /// Open ports are closed first because the item list is rebuilt from scratch. Setting
    /// <c>IsEnabled</c> on a new item goes through <see cref="OnPortToggled"/>, which opens it.
    /// </remarks>
    [RelayCommand]
    void RefreshMidiPorts()
    {
        var enabled = settings.MidiInputs.ToHashSet();
        foreach (var p in MidiPorts.Where(p => p.IsEnabled)) midiHub.Close(p.Port);
        MidiPorts.Clear();
        foreach (var port in MidiHub.ListPorts())
        {
            var item = new MidiPortItem(port, OnPortToggled);
            MidiPorts.Add(item);
            if (enabled.Contains(port.Display))
                item.IsEnabled = true;
        }
        Midi2Info = MidiHub.Midi2Status is { } why
            ? Loc.S("MIDI 2.0 no disponible: ", "MIDI 2.0 unavailable: ") + why
            : Loc.S("MIDI 2.0 activo · ", "MIDI 2.0 active · ") + Midi2Input.ApiOrigin();
    }

    /// <summary>
    /// Opens or closes a port when its switch changes, and saves the list of enabled ports.
    /// </summary>
    /// <param name="item">The port item whose switch changed.</param>
    /// <param name="on">New state.</param>
    /// <remarks>
    /// <para>
    /// On an open failure the error is shown in the console and under the port, and the switch is
    /// turned back off. That nested change re-enters this method with <paramref name="on"/> =
    /// <c>false</c> while <see cref="revertingFailedPort"/> is set, so it only closes the port and
    /// does <b>not</b> save.
    /// </para>
    /// <para>
    /// Bug fix: previously that nested call saved the settings, so a port that failed to open
    /// once (busy in another app, driver hiccup) was removed from the saved list for good, and
    /// every save also dropped ports that were simply unplugged at that moment. Now only an
    /// explicit user choice changes a listed port's saved state, and saved ports that are not
    /// listed right now are kept, so they re-open automatically when they come back.
    /// </para>
    /// </remarks>
    void OnPortToggled(MidiPortItem item, bool on)
    {
        if (on)
        {
            item.Error = midiHub.Open(item.Port);
            if (item.Error is not null)
            {
                ConsoleLines.Add(new ConsoleLine(item.Error, MessageKind.Warning));
                revertingFailedPort = true;
                try { item.IsEnabled = false; }
                finally { revertingFailedPort = false; }
                return;
            }
        }
        else
        {
            midiHub.Close(item.Port);
            // Switch turned off by the failure path above, not by the user: keep the saved choice.
            if (revertingFailedPort) return;
        }
        // Saved = ports the user enabled among those listed now + saved ports that are not listed
        // right now (unplugged or temporarily missing), which must survive this save.
        var listed = MidiPorts.Select(p => p.Port.Display).ToHashSet();
        settings.MidiInputs = [.. settings.MidiInputs.Where(name => !listed.Contains(name)),
                               .. MidiPorts.Where(p => p.IsEnabled).Select(p => p.Port.Display)];
        settings.Save();
    }

    /// <summary>
    /// True while <see cref="OnPortToggled"/> is switching a port back off because it failed to
    /// open; the nested toggle then skips saving so the failure does not erase the user's choice.
    /// </summary>
    bool revertingFailedPort;

    /// <summary>Starts (or cancels) MIDI learn for a slider: the next controller moved is bound to it.</summary>
    /// <param name="channel">The slider; clicking the learn button of the slider already learning cancels.</param>
    /// <remarks>
    /// Generated <c>LearnMidiCommand</c>, bound from each slider's learn button. Only one slider
    /// learns at a time: starting on another slider cancels the previous one. The binding itself
    /// is made in <see cref="ProcessMidi"/> when the next CC arrives.
    /// </remarks>
    [RelayCommand]
    void LearnMidi(ChannelControlViewModel channel)
    {
        if (learning is not null) learning.IsLearning = false;
        if (learning == channel) { learning = null; return; } // second click on the same slider = cancel
        learning = channel;
        channel.IsLearning = true;
    }

    /// <summary>Removes the MIDI mapping of a slider (every CC mapped to its channel name) and saves.</summary>
    /// <param name="channel">The slider whose mapping is removed.</param>
    /// <remarks>
    /// Reached through <see cref="ChannelControlViewModel.ForgetMidiHandler"/> (installed in the
    /// constructor): the slider's context menu lives in a popup outside the window's visual tree,
    /// so it binds the slider's own <c>ForgetMidiCommand</c>, which calls back here. The generated
    /// <c>ForgetMidiCommand</c> of this class exists too but the menu does not use it.
    /// </remarks>
    [RelayCommand]
    void ForgetMidi(ChannelControlViewModel channel)
    {
        foreach (var key in settings.MidiBindings.Where(kv => kv.Value == channel.Name).Select(kv => kv.Key).ToList())
            settings.MidiBindings.Remove(key);
        settings.Save();
        channel.MidiBinding = null;
    }

    /// <summary>Shows each slider's current mapping (called when the shown document changes).</summary>
    /// <param name="doc">Document whose sliders get their "CC n · ch n" label (or none).</param>
    void ApplyMidiBindingLabels(DocumentViewModel doc)
    {
        // FirstOrDefault on a dictionary yields default(KeyValuePair) (Key == null) when nothing
        // matches; the pattern then fails and the label is cleared. With several CCs mapped to
        // one channel, only the first one found is shown.
        foreach (var ch in doc.Channels)
            ch.MidiBinding = settings.MidiBindings.FirstOrDefault(kv => kv.Value == ch.Name).Key is { } key ? FormatBinding(key) : null;
    }

    /// <summary>Turns a mapping key <c>"channel:cc"</c> (channel 0-based) into the label <c>"CC 74 · ch 1"</c> (channel 1-based, as musicians count).</summary>
    /// <param name="key">Key of <see cref="AppSettings.MidiBindings"/>.</param>
    /// <returns>The label text.</returns>
    static string FormatBinding(string key)
    {
        var parts = key.Split(':');
        return $"CC {parts[1]} · ch {int.Parse(parts[0]) + 1}";
    }

    /// <summary>On-screen keyboard (and Cabbage keyboards): a key went down. Injected on MIDI channel 1 (index 0) like a real port.</summary>
    /// <param name="note">MIDI note number; anything outside 0…127 is ignored.</param>
    /// <param name="velocity">Normalised velocity 0…1.</param>
    /// <remarks>
    /// Bug fix: the note used to be cast to <c>byte</c> unchecked, so a note above 127 (the
    /// on-screen keyboard can show keys up to 204) silently wrapped or produced an invalid MIDI
    /// data byte. Out-of-range notes are now dropped here, the single entry point shared by the
    /// main keyboard and every Cabbage <c>keyboard</c> widget.
    /// </remarks>
    public void KeyboardNoteOn(int note, double velocity)
    {
        if (note is < 0 or > 127) return;
        midiHub.Inject(new MidiMessage(MidiKind.NoteOn, 0, (byte)note, velocity));
    }

    /// <summary>On-screen keyboard: a key went up.</summary>
    /// <param name="note">MIDI note number; anything outside 0…127 is ignored (see <see cref="KeyboardNoteOn"/>).</param>
    public void KeyboardNoteOff(int note)
    {
        if (note is < 0 or > 127) return;
        midiHub.Inject(new MidiMessage(MidiKind.NoteOff, 0, (byte)note, 0));
    }

    /// <summary>True once the «press Play» hint was shown; cleared when a performance starts.</summary>
    bool idleMidiHintShown;

    /// <summary>Re-arms the «press Play» hint whenever a performance starts (and hides the bubble).</summary>
    /// <param name="value">The new run mode.</param>
    /// <remarks>
    /// CommunityToolkit partial hook of the <c>Mode</c> property declared in <c>MainViewModel.cs</c>;
    /// a partial class may implement such a hook in any of its files, and this one is about MIDI.
    /// </remarks>
    partial void OnModeChanged(RunMode value)
    {
        if (value == RunMode.Play)
        {
            idleMidiHintShown = false;
            PlayHintVisible = false;
        }
    }

    /// <summary>
    /// True while the bubble under «Tocar» is shown: notes are arriving (MIDI keyboard or the
    /// on-screen one) but Csound is not running, so nothing can sound. Hidden after a few seconds
    /// of silence or as soon as the user presses Tocar.
    /// </summary>
    [ObservableProperty] bool playHintVisible;

    /// <summary>One-shot 5-second timer that hides the bubble; created lazily, restarted by every new note.</summary>
    DispatcherTimer? playHintTimer;

    /// <summary>Shows the «press Tocar» bubble and (re)starts its 5-second hide timer.</summary>
    /// <remarks>
    /// Every note-on while stopped restarts the timer (Stop + Start), so the bubble stays up while
    /// the user keeps playing and disappears 5 s after the last note. The timer is a
    /// <see cref="DispatcherTimer"/>, so its tick runs on the UI thread like the rest of this class.
    /// </remarks>
    void ShowPlayHint()
    {
        PlayHintVisible = true;
        playHintTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Normal, (_, _) =>
        {
            PlayHintVisible = false;
            playHintTimer!.Stop();
        });
        playHintTimer.Stop();
        playHintTimer.Start();
    }

    /// <summary>
    /// Processes the MIDI received since the last frame: monitor, MIDI learn, controller
    /// mappings and keyboard highlighting. Called from <see cref="Tick"/> on every animation frame,
    /// whether or not Csound is running (so the monitor and learn work while stopped).
    /// </summary>
    /// <remarks>
    /// Csound gets the same messages independently (the hub queues them for the performance
    /// thread), so the slider updates done here are only for <i>mapped</i> controllers: they set
    /// <c>slider.Value</c>, which in turn sends the channel value to Csound like a mouse drag.
    /// </remarks>
    void ProcessMidi()
    {
        var messages = midiHub.DrainForUi(); // at most 256 per frame; the rest waits for the next frame
        if (messages.Count == 0)
            return;
        foreach (var m in messages)
        {
            MidiMonitor.Insert(0, m.ToString());
            // Notes only sound while Csound is playing; say so once per stopped period (a monitor
            // full of notes and no sound looked like a bug).
            if (m.Kind == MidiKind.NoteOn && Mode != RunMode.Play)
                ShowPlayHint();
            if (m.Kind == MidiKind.NoteOn && Mode != RunMode.Play && !idleMidiHintShown)
            {
                idleMidiHintShown = true;
                AddInfo(Loc.S("🎹 Llegan notas MIDI pero Csound no está tocando: presiona Tocar (F5) y vuelve a tocar el teclado.",
                              "🎹 MIDI notes are arriving but Csound is not playing: press Play (F5) and play the keyboard again."));
                MidiMonitor.Insert(0, Loc.S("↳ Csound detenido: presiona Tocar para oír las notas", "↳ Csound stopped: press Play to hear the notes"));
            }
            if (m.Kind is MidiKind.NoteOn or MidiKind.NoteOff)
                ExternalNote?.Invoke(m.Index, m.Kind == MidiKind.NoteOn);
            if (m.Kind != MidiKind.ControlChange)
                continue;

            string key = $"{m.Channel}:{m.Index}"; // "channel:cc", channel 0-based
            if (learning is { } target)
            {
                // A slider has one controller: drop its previous mapping(s) first. The dictionary
                // is keyed by CC, so the assignment below also takes this CC away from any other
                // slider it drove.
                foreach (var stale in settings.MidiBindings.Where(kv => kv.Value == target.Name).Select(kv => kv.Key).ToList())
                    settings.MidiBindings.Remove(stale);
                settings.MidiBindings.TryGetValue(key, out string? previousOwner);
                settings.MidiBindings[key] = target.Name;
                settings.Save();
                target.MidiBinding = FormatBinding(key);
                // Bug fix: the slider that lost this CC kept showing "CC n · ch n" until the next
                // document switch, although the controller no longer moved it. Refresh its label
                // now (to another CC still mapped to it, or none) and tell the user in the console.
                if (previousOwner is not null && previousOwner != target.Name)
                {
                    if (SelectedDocument?.FindChannel(previousOwner) is { } robbed)
                        robbed.MidiBinding = settings.MidiBindings.FirstOrDefault(kv => kv.Value == previousOwner).Key is { } other
                            ? FormatBinding(other) : null;
                    AddInfo(Loc.S($"MIDI learn: CC {m.Index} (canal {m.Channel + 1}) ya no controla «{previousOwner}»",
                                  $"MIDI learn: CC {m.Index} (channel {m.Channel + 1}) no longer drives «{previousOwner}»"));
                }
                target.IsLearning = false;
                learning = null;
                AddInfo(Loc.S($"MIDI learn: CC {m.Index} (canal {m.Channel + 1}) → «{target.Name}»", $"MIDI learn: CC {m.Index} (channel {m.Channel + 1}) → «{target.Name}»"));
            }
            // Apply the mapping (including the CC that was just learned) to the selected document's
            // slider: 0…1 maps exponentially for exponential sliders (equal ratios per step, needs
            // min > 0), linearly otherwise.
            if (settings.MidiBindings.TryGetValue(key, out string? channelName) && SelectedDocument?.FindChannel(channelName) is { } slider)
                slider.Value = slider.Exponential && slider.Minimum > 0
                    ? slider.Minimum * Math.Pow(slider.Maximum / slider.Minimum, m.Value)
                    : slider.Minimum + m.Value * (slider.Maximum - slider.Minimum);
        }
        while (MidiMonitor.Count > 60) // keep the monitor short: oldest entries are at the end
            MidiMonitor.RemoveAt(MidiMonitor.Count - 1);
    }
}
