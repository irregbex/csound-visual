using CsoundVisual.Integration;
using CsoundVisual.Localization;
using CsoundVisual.Music;
using CsoundVisual.ViewModels;

namespace CsoundVisual.Views;

/// <summary>
/// Ableton Link for the tool transports (piano roll, step sequencer). While Link is on:
/// <list type="bullet">
/// <item>the transport follows the session tempo (<see cref="TempoFollowed"/> lets the window update
/// its tempo box) and a tempo typed in the tool is proposed to the session;</item>
/// <item>playback starts on the next downbeat of the session (phase 0 of the quantum) and stays
/// phase-locked: the scheduler re-anchors its beat → score-time mapping on Link's timeline at
/// every pass (<see cref="ITransportSync"/>);</item>
/// <item>with start/stop sync, a peer's play/stop starts/stops the transport, and the tool's
/// ▶/■ starts/stops the session.</item>
/// </list>
/// </summary>
/// <remarks>
/// Everything here runs on the UI thread: <see cref="FollowLink"/> is polled from the 30 ms
/// <c>DispatcherTimer</c> of the main part of this class (<c>ToolTransport.cs</c>), reading a
/// snapshot of the session with <see cref="LinkSession.Capture"/>. The phase locking itself is done
/// by the scheduler thread through <see cref="ITransportSync"/>; this file only decides when the
/// sync object is attached and mirrors tempo and play/stop. A local ▶ starts the shared session
/// inside <see cref="ITransportSync.BeginAt"/> (on the first downbeat), a local ■ stops it in
/// <see cref="PropagateLinkStop"/>.
/// </remarks>
public sealed partial class ToolTransport
{
    /// <summary>
    /// Sync adapter created once from the session (<see cref="LinkSession.CreateTransportSync"/>) and
    /// reused on later starts; attached to the scheduler in <see cref="PrepareSync"/>.
    /// </summary>
    ITransportSync? linkSync;

    /// <summary>
    /// Session play state seen at the previous tick, used to detect a peer's play/stop edge.
    /// <c>null</c> = unknown (Link off or start/stop sync off), so the first tick never fires an edge.
    /// </summary>
    bool? lastLinkPlaying;

    /// <summary>
    /// Set around internal <see cref="Stop"/> calls (restart from <see cref="Play"/>, or a stop that
    /// came <i>from</i> the session) so <see cref="PropagateLinkStop"/> does not tell the session to stop.
    /// </summary>
    bool restarting;

    /// <summary>Document of the last local ▶, replayed when a Link peer starts the shared transport.</summary>
    DocumentViewModel? lastPlayDoc;

    /// <summary>Start beat (timeline beats) of the last local ▶, replayed together with <see cref="lastPlayDoc"/>.</summary>
    double lastPlayFrom;

    /// <summary>Raised (UI thread) when the transport adopted a new tempo from the Link session.</summary>
    public event Action<double>? TempoFollowed;

    /// <summary>The Link session when it is on, otherwise null.</summary>
    LinkSession? ActiveLink => vm.AbletonLink is { IsEnabled: true } l ? l : null;

    /// <summary>True when the transport is (or will be) locked to Ableton Link.</summary>
    public bool IsLinked => ActiveLink is not null;

    /// <summary>
    /// A tempo set in the tool goes to the session (every peer follows) — but only once the tool
    /// has adopted the session tempo, so that merely opening a tool window never changes the
    /// tempo of Live and the other peers.
    /// </summary>
    /// <param name="bpm">The tempo just set on the transport.</param>
    void PushLinkTempo(double bpm)
    {
        if (linkTempoAdopted)
            ActiveLink?.SetTempo(bpm);
    }

    /// <summary>
    /// True once <see cref="FollowLink"/> has synchronised the scheduler with the session tempo at
    /// least once since Link was switched on; reset whenever Link is off. Gates <see cref="PushLinkTempo"/>.
    /// </summary>
    bool linkTempoAdopted;

    /// <summary>Remembers what to play when a Link peer starts the shared transport.</summary>
    void RememberPlay(DocumentViewModel? doc, double fromBeat)
    {
        lastPlayDoc = doc;
        lastPlayFrom = fromBeat;
    }

    /// <summary>
    /// Attaches (or detaches) the Link sync right before the scheduler starts. With a sync attached
    /// the scheduler waits for the next quantum boundary and maps beats to score time through the
    /// Link timeline; with <c>null</c> it free-runs on Csound's score clock.
    /// </summary>
    void PrepareSync() =>
        scheduler.Sync = ActiveLink is { } l ? (linkSync ??= l.CreateTransportSync()) : null;

    /// <summary>Status-line note after a synced start.</summary>
    void AnnounceLinkStart()
    {
        if (ActiveLink is not { } l) return;
        Message?.Invoke(Loc.S(
            $"🔗 Link: empieza en el próximo compás ({l.Quantum:0} pulsos) a {scheduler.Bpm:0.0} BPM, en fase con {l.Peers} par(es).",
            $"🔗 Link: starts on the next bar ({l.Quantum:0} beats) at {scheduler.Bpm:0.0} BPM, in phase with {l.Peers} peer(s)."), false);
    }

    /// <summary>The tool's ■ stops the shared transport too (start/stop sync), unless it is a restart.</summary>
    /// <param name="wasPlaying">Whether the transport was playing (or pending) before the stop; a no-op stop is not propagated.</param>
    void PropagateLinkStop(bool wasPlaying)
    {
        if (wasPlaying && !restarting)
            ActiveLink?.RequestStop();
    }

    /// <summary>
    /// Every timer tick: adopt the session tempo, and follow the peers' play/stop when start/stop
    /// sync is on.
    /// </summary>
    void FollowLink()
    {
        if (ActiveLink is not { } l)
        {
            lastLinkPlaying = null;
            linkTempoAdopted = false;
            return;
        }
        var snap = l.Capture();
        // Small tolerance: Link reports the tempo as a double that jitters in the last digits;
        // without it TempoFollowed would fire (and the tempo box would be rewritten) on every tick.
        // The scheduler is set directly (not through Bpm) so the adopted tempo is not echoed back.
        if (Math.Abs(snap.Tempo - scheduler.Bpm) > 0.005)
        {
            scheduler.Bpm = snap.Tempo;
            TempoFollowed?.Invoke(snap.Tempo);
        }
        linkTempoAdopted = true;
        if (!vm.LinkStartStopSync)
        {
            lastLinkPlaying = null;
            return;
        }
        bool playing = snap.IsPlaying;
        // Edge detection: react only to a change of the session state, never to the steady state,
        // so a local ■ while the session keeps playing is not immediately undone.
        if (lastLinkPlaying is bool before && before != playing)
        {
            if (playing && !IsPlaying)
                Play(lastPlayDoc ?? vm.ToolDocument, lastPlayFrom);
            else if (!playing && IsPlaying)
            {
                restarting = true; // do not send the stop back to the session
                Stop();
                restarting = false;
                Message?.Invoke(Loc.S("■ Link: otro programa detuvo el transporte.", "■ Link: another app stopped the transport."), false);
            }
        }
        lastLinkPlaying = playing;
    }
}
