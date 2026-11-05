using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using CsoundVisual.Engine;
using CsoundVisual.Integration;
using CsoundVisual.Localization;

namespace CsoundVisual.ViewModels;

/// <summary>
/// Ableton Link (tempo, beat and phase sync with Live, Bitwig, phone apps… on the local network).
/// </summary>
/// <remarks>
/// <para>While Link is on, the engine runs <see cref="LinkSession"/> as its control-cycle hook, so
/// every orchestra can read <c>LINK_BPM</c>, <c>LINK_BEAT</c>, <c>LINK_PHASE</c>,
/// <c>LINK_PLAYING</c> and <c>LINK_PEERS</c> with <c>chnget</c>; the piano roll and the step
/// sequencer lock their transport to the session (see <c>Views/ToolTransport</c>).</para>
/// <para>Everything degrades gracefully: without <c>CsoundVisualLink.dll</c> next to the exe,
/// <see cref="IsLinkAvailable"/> is false and enabling Link only prints an explanation.</para>
/// <para><b>UI binding.</b> Unlike most of the view model, these properties are hand-written (not
/// <c>[ObservableProperty]</c>): the on/off state lives in the <see cref="LinkSession"/>, and
/// quantum / start-stop sync live directly in <see cref="AppSettings"/> (saved on every change), so
/// there is no backing field to generate. <see cref="OnLinkChanged"/> raises the change
/// notifications for the whole group at once. Consumers: <see cref="Views.LinkStatusView"/> (title
/// bar indicator and Settings › Sync card) and the music tools' transport.</para>
/// <para><b>Threading.</b> The engine calls the session as a control-cycle hook on the performance
/// thread; the view model only reads snapshots (<c>LinkSession.Capture</c>) on the UI thread, every
/// 200 ms, and raises notifications only when something visible changed.</para>
/// </remarks>
public sealed partial class MainViewModel
{
    /// <summary>The Link participant; created on first enable and then kept (re-enabling reuses it).</summary>
    LinkSession? linkSession;

    /// <summary>200 ms poll timer for peers/tempo/playing; runs only while Link is on.</summary>
    DispatcherTimer? linkTimer;

    /// <summary>Last polled number of peers (other apps in the session).</summary>
    int linkPeers;

    /// <summary>Last polled session tempo, in BPM.</summary>
    double linkTempo;

    /// <summary>Last polled shared transport state (start/stop sync).</summary>
    bool linkPlaying;

    /// <summary>The Link participant, or null when Link was never enabled or the DLL is missing.</summary>
    public LinkSession? AbletonLink => linkSession;

    /// <summary>True when <c>CsoundVisualLink.dll</c> could be loaded.</summary>
    public bool IsLinkAvailable => AbletonLinkNative.IsAvailable;

    /// <summary>Restores Link from the settings (called once at start-up).</summary>
    /// <remarks>Passes <c>save: false</c>: restoring must not rewrite the setting it was read from.</remarks>
    public void InitLink()
    {
        if (settings.LinkEnabled)
            SetLinkEnabled(true, save: false);
    }

    /// <summary>Joins or leaves the Link session; saved.</summary>
    /// <remarks>
    /// Bound two-way by the Settings › Sync toggle. The getter reflects the real session state, so if
    /// enabling fails (DLL missing) the toggle snaps back after <see cref="OnLinkChanged"/>.
    /// </remarks>
    public bool IsLinkEnabled
    {
        get => linkSession?.IsEnabled == true;
        set => SetLinkEnabled(value, save: true);
    }

    /// <summary>Other Link apps currently in the session.</summary>
    public int LinkPeers => linkPeers;

    /// <summary>Session tempo (BPM) while Link is on.</summary>
    public double LinkTempo => linkTempo;

    /// <summary>True while the shared Link transport plays (start/stop sync).</summary>
    public bool LinkPlaying => linkPlaying;

    /// <summary>Short status for the title bar: «Link · 2 peers · 120.0 BPM», «Link off» or «Link unavailable».</summary>
    public string LinkStatusText
    {
        get
        {
            if (!IsLinkAvailable)
                return Loc.S("Link no disponible", "Link unavailable");
            if (!IsLinkEnabled)
                return Loc.S("Link apagado", "Link off");
            string peers = linkPeers == 1 ? Loc.S("1 par", "1 peer") : Loc.S($"{linkPeers} pares", $"{linkPeers} peers");
            // Invariant culture: the tempo always shows a decimal point ("120.0"), whatever the Windows locale.
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Link · {peers} · {linkTempo:0.0} BPM");
        }
    }

    /// <summary>Tooltip of the status control: what Link does and what the orchestra receives.</summary>
    public string LinkStatusTip => IsLinkAvailable
        ? Loc.S("Ableton Link: sincroniza tempo, pulso y fase con Live, Bitwig y apps del celular en la red local. Clic para activar/desactivar. Las orquestas leen LINK_BPM, LINK_BEAT, LINK_PHASE, LINK_PLAYING y LINK_PEERS con chnget.",
                "Ableton Link: syncs tempo, beat and phase with Live, Bitwig and phone apps on the local network. Click to turn on/off. Orchestras read LINK_BPM, LINK_BEAT, LINK_PHASE, LINK_PLAYING and LINK_PEERS with chnget.")
        : Loc.S("Falta CsoundVisualLink.dll junto al programa (se compila con tools\\build_link.ps1).",
                "CsoundVisualLink.dll is missing next to the program (build it with tools\\build_link.ps1).");

    /// <summary>Beats per bar for phase and bar-quantised starts (1…16, default 4); saved.</summary>
    /// <remarks>
    /// The quantum is Link's phase length: <c>LINK_PHASE</c> runs 0…quantum and the music tools start
    /// on the next multiple of it, aligned with the other peers. A missing/invalid saved value reads
    /// as 4. The setter rounds to whole beats, clamps, saves and forwards to a live session.
    /// </remarks>
    public double LinkQuantum
    {
        get => Math.Clamp(settings.LinkQuantum <= 0 ? 4 : settings.LinkQuantum, 1, 16);
        set
        {
            value = Math.Clamp(Math.Round(value), 1, 16);
            if (value == settings.LinkQuantum) return;
            settings.LinkQuantum = value;
            settings.Save();
            if (linkSession is not null) linkSession.Quantum = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Share play/stop with the other peers (Live: «Start Stop Sync»); saved.</summary>
    public bool LinkStartStopSync
    {
        get => settings.LinkStartStopSync;
        set
        {
            if (value == settings.LinkStartStopSync) return;
            settings.LinkStartStopSync = value;
            settings.Save();
            if (linkSession is not null) linkSession.StartStopSync = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Turns Link on or off (title-bar control, settings).</summary>
    /// <remarks>Generated command: <c>ToggleLinkCommand</c>.</remarks>
    [RelayCommand]
    void ToggleLink() => IsLinkEnabled = !IsLinkEnabled;

    /// <summary>Does the actual work of enabling or disabling Link.</summary>
    /// <param name="on">True to join the session, false to leave it.</param>
    /// <param name="save">True to persist the choice in <see cref="AppSettings.LinkEnabled"/> (user action); false when restoring at start-up.</param>
    /// <remarks>
    /// <para>On: create the session once (seeded with the piano roll's tempo so a lone peer starts
    /// at a familiar BPM), apply quantum / start-stop sync, install it as the engine's
    /// <c>CycleHook</c> (it writes the <c>LINK_*</c> channels every control cycle, also for a run
    /// already in progress) and start polling.</para>
    /// <para>Off: disable the session, detach the hook only if it is still ours, stop polling and,
    /// if Csound is running, zero <c>LINK_BPM</c>/<c>LINK_PLAYING</c>/<c>LINK_PEERS</c> so orchestras
    /// notice and fall back to their own tempo.</para>
    /// </remarks>
    void SetLinkEnabled(bool on, bool save)
    {
        if (on && !IsLinkAvailable)
        {
            ConsoleLines.Add(new ConsoleLine(Loc.S(
                $"⚠ Ableton Link no está disponible: {AbletonLinkNative.LoadError}. Compílalo con tools\\build_link.ps1; el resto de la app funciona igual.",
                $"⚠ Ableton Link is not available: {AbletonLinkNative.LoadError}. Build it with tools\\build_link.ps1; the rest of the app works as usual."), MessageKind.Warning));
            OnLinkChanged(); // lets a two-way bound toggle snap back to "off"
            return;
        }
        if (on)
        {
            // Native creation can fail even when the DLL loaded (TryCreate returns null, or the
            // native constructor throws, e.g. a missing entry point in an older DLL). Previously the
            // method returned silently here, so the user got no explanation and a two-way bound
            // toggle stayed visually "on" while Link was off. Now: report it in the console, do NOT
            // save "on" (the saved preference only changes on success), and raise the change
            // notifications so every toggle snaps back to the real state.
            if (linkSession is null)
            {
                string? why = null;
                try { linkSession = LinkSession.TryCreate(PianoRoll.Bpm); }
                catch (Exception ex) { why = ex.Message; }
                if (linkSession is null)
                {
                    why ??= AbletonLinkNative.LoadError;
                    ConsoleLines.Add(new ConsoleLine(Loc.S(
                        $"⚠ No se pudo crear la sesión de Ableton Link: {why}. Link queda apagado.",
                        $"⚠ Could not create the Ableton Link session: {why}. Link stays off."), MessageKind.Warning));
                    OnLinkChanged();
                    return;
                }
            }
            linkSession.Quantum = LinkQuantum;
            linkSession.StartStopSync = LinkStartStopSync;
            linkSession.IsEnabled = true;
            engine.CycleHook = linkSession;
            linkTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Normal, (_, _) => PollLink());
            linkTimer.Start();
            ConsoleLines.Add(new ConsoleLine(Loc.S(
                "🔗 Ableton Link activado: las orquestas reciben LINK_BPM, LINK_BEAT, LINK_PHASE, LINK_PLAYING y LINK_PEERS; el piano roll y el secuenciador siguen el tempo y empiezan en el compás.",
                "🔗 Ableton Link on: orchestras receive LINK_BPM, LINK_BEAT, LINK_PHASE, LINK_PLAYING and LINK_PEERS; the piano roll and step sequencer follow the tempo and start on the bar."), MessageKind.Info));
        }
        else if (linkSession is not null)
        {
            linkSession.IsEnabled = false;
            if (ReferenceEquals(engine.CycleHook, linkSession))
                engine.CycleHook = null;
            linkTimer?.Stop();
            if (engine.IsBusy)
            {
                // Orchestras fall back to their own tempo when LINK_BPM is 0.
                engine.SetControl("LINK_BPM", 0);
                engine.SetControl("LINK_PLAYING", 0);
                engine.SetControl("LINK_PEERS", 0);
            }
            // Reset the whole polled snapshot, not only the peer count: PollLink returns early while
            // Link is off, so a stale LinkPlaying = true / LinkTempo would otherwise survive the
            // switch-off (consumers such as the music tools' transport would keep seeing a playing
            // shared transport), and the next switch-on would compare against stale values.
            linkPeers = 0;
            linkTempo = 0;
            linkPlaying = false;
        }
        if (save && settings.LinkEnabled != on)
        {
            settings.LinkEnabled = on;
            settings.Save();
        }
        PollLink();
        OnLinkChanged();
    }

    /// <summary>Refreshes peers/tempo (every 200 ms while Link is on).</summary>
    /// <remarks>
    /// Notifies only on a visible change (tempo differences below 0.005 BPM are ignored, since the
    /// display has one decimal) and logs peer joins/leaves in the console.
    /// </remarks>
    void PollLink()
    {
        if (linkSession is null || !linkSession.IsEnabled) return;
        var snap = linkSession.Capture();
        if (snap.Peers == linkPeers && Math.Abs(snap.Tempo - linkTempo) < 0.005 && snap.IsPlaying == linkPlaying) return;
        if (snap.Peers != linkPeers)
            ConsoleLines.Add(new ConsoleLine(Loc.S($"🔗 Link: {snap.Peers} par(es) en la sesión.", $"🔗 Link: {snap.Peers} peer(s) in the session."), MessageKind.Info));
        linkPeers = snap.Peers;
        linkTempo = snap.Tempo;
        linkPlaying = snap.IsPlaying;
        OnLinkChanged();
    }

    /// <summary>Raises <c>PropertyChanged</c> for every Link property shown in the UI (they are computed, so they cannot notify themselves).</summary>
    void OnLinkChanged()
    {
        OnPropertyChanged(nameof(IsLinkEnabled));
        OnPropertyChanged(nameof(LinkPeers));
        OnPropertyChanged(nameof(LinkTempo));
        OnPropertyChanged(nameof(LinkPlaying));
        OnPropertyChanged(nameof(LinkStatusText));
        OnPropertyChanged(nameof(LinkStatusTip));
    }
}
