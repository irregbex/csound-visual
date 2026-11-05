using CsoundVisual.Engine;
using CsoundVisual.Interop;
using CsoundVisual.Music;
using N = CsoundVisual.Integration.AbletonLinkNative;

namespace CsoundVisual.Integration;

/// <summary>A snapshot of the Link session taken on the UI thread.</summary>
/// <param name="Tempo">Session tempo, BPM.</param>
/// <param name="Beat">Beat at the time of the snapshot (for the quantum in use).</param>
/// <param name="Phase">Phase within the quantum, 0 ≤ phase &lt; quantum.</param>
/// <param name="IsPlaying">Shared transport state (meaningful with start/stop sync).</param>
/// <param name="Peers">Other Link apps in the session.</param>
public readonly record struct LinkSnapshot(double Tempo, double Beat, double Phase, bool IsPlaying, int Peers);

/// <summary>
/// One Ableton Link participant for the whole app (wraps <c>CsoundVisualLink.dll</c>).
/// </summary>
/// <remarks>
/// <para><b>Threads.</b> Link session states are captured into native session-state objects that
/// must each be used by one thread at a time: <c>appState</c> (UI thread and the tool transports'
/// scheduler threads, under <c>appGate</c>) and <c>audioState</c> (Csound's performance thread only,
/// with the realtime-safe <c>capture_audio_session_state</c>).</para>
/// <para><b>Csound channels.</b> As an <see cref="IControlCycleHook"/> installed on the engine, it
/// writes before every control cycle <c>LINK_BPM</c>, <c>LINK_BEAT</c>, <c>LINK_PHASE</c>
/// (0…quantum), <c>LINK_PLAYING</c>, <c>LINK_PEERS</c> and <c>LINK_QUANTUM</c>, evaluated for the moment the block will
/// be <i>heard</i>: Link time of the block = host time when it is computed, smoothed, plus the
/// engine's output latency.</para>
/// <para><b>Score time ↔ Link time.</b> The same hook keeps a smoothed offset between Csound's score
/// clock and Link's host clock (µs), so the piano roll and step sequencer (which schedule on the
/// score clock) can be locked to the session: see <see cref="CreateTransportSync"/>.</para>
/// <para><b>Lifetime.</b> Created lazily by the main view model (<c>MainViewModel.Link.cs</c>) the
/// first time Link is switched on while the DLL is present, and kept for the whole process: the native objects are never destroyed (the
/// process exit tears Link down). Enabling/disabling only joins or leaves the network session.</para>
/// <para><b>Clocks involved.</b> Three timelines meet here: Csound's <i>score time</i> (seconds,
/// advanced by ksmps/sr per control cycle), Link's <i>host clock</i> (microseconds, shared by all
/// processes) and the session's <i>beat timeline</i> (beats, tempo-dependent, shared by all peers).
/// Link itself converts host time ↔ beats; this class maintains score time ↔ host time.</para>
/// </remarks>
public sealed unsafe class LinkSession : IControlCycleHook
{
    /// <summary>The native <c>abl_link</c> instance (one per app).</summary>
    readonly IntPtr link;

    /// <summary>Session-state scratch object for non-audio threads; guarded by <see cref="appGate"/>.</summary>
    readonly IntPtr appState;

    /// <summary>Session-state scratch object used only by the performance thread in <see cref="OnControlCycle"/>.</summary>
    readonly IntPtr audioState;

    /// <summary>Serialises every use of <see cref="appState"/> (UI thread, scheduler threads).</summary>
    readonly object appGate = new();

    /// <summary>Beats per bar; read by the audio thread with <see cref="Volatile"/>, see <see cref="Quantum"/>.</summary>
    double quantum = 4;

    // Score-time ↔ host-time mapping, written by the performance thread.
    // hostMicros(heard) = scoreTime·1e6 + mapOffset (offset already includes the output latency).
    double meanOffset;      // smoothed (host µs at computation − score µs); performance thread only
    double lastScore = -1;  // performance thread only
    double mapAge;          // seconds of score time since the smoothing was (re)seeded
    double mapOffset;       // published: meanOffset + latency (Volatile)
    volatile bool mapValid; // false until the first control cycle of the process (never reset; staleness uses mapUpdatedMicros)
    long mapUpdatedMicros;  // host time of the last update (Volatile), to detect a stopped engine

    // Last values written to Csound (diagnostics / tests), performance thread → any thread.
    double lastBpm, lastBeat, lastPhase;
    int lastPlaying, lastPeers;

    /// <summary>Creates the native Link instance (disabled) and its two session-state scratch objects.</summary>
    /// <param name="bpm">Initial tempo proposed when this app is the first peer of a session.</param>
    LinkSession(double bpm)
    {
        link = N.create(bpm);
        appState = N.createSessionState();
        audioState = N.createSessionState();
    }

    /// <summary>Creates the session (Link not yet enabled), or returns null when the DLL is missing.</summary>
    public static LinkSession? TryCreate(double bpm = 120) =>
        N.IsAvailable ? new LinkSession(Math.Clamp(bpm, 20, 999)) : null;

    /// <summary>Joins (true) or leaves (false) the Link session on the local network.</summary>
    public bool IsEnabled
    {
        get => N.isEnabled(link) != 0;
        set => N.enable(link, value ? (byte)1 : (byte)0);
    }

    /// <summary>Share play/stop with the other peers (Live: «Start Stop Sync»).</summary>
    public bool StartStopSync
    {
        get => N.isStartStopSyncEnabled(link) != 0;
        set => N.enableStartStopSync(link, value ? (byte)1 : (byte)0);
    }

    /// <summary>Beats per bar used for phase and bar-quantised starts (Live: the launch quantisation, usually 4).</summary>
    public double Quantum
    {
        get => Volatile.Read(ref quantum);
        set => Volatile.Write(ref quantum, Math.Clamp(value, 1, 16));
    }

    /// <summary>Number of other Link apps in the session.</summary>
    public int Peers => (int)N.numPeers(link);

    /// <summary>Link's host clock, microseconds (QueryPerformanceCounter-based, common to every process).</summary>
    public long ClockMicros => N.clockMicros(link);

    /// <summary>Captures the session state now (UI or any non-audio thread).</summary>
    /// <remarks>Beat and phase are evaluated at the current host time, not at the time the audio
    /// is heard (UI display only; the orchestra gets latency-compensated values).</remarks>
    public LinkSnapshot Capture()
    {
        lock (appGate)
        {
            N.captureAppSessionState(link, appState);
            long now = N.clockMicros(link);
            double q = Quantum;
            return new LinkSnapshot(N.tempo(appState), N.beatAtTime(appState, now, q), N.phaseAtTime(appState, now, q),
                N.isPlaying(appState) != 0, Peers);
        }
    }

    /// <summary>Beat of the session timeline at host time <paramref name="micros"/> (for the current quantum).</summary>
    public double BeatAtTime(long micros)
    {
        lock (appGate)
        {
            N.captureAppSessionState(link, appState);
            return N.beatAtTime(appState, micros, Quantum);
        }
    }

    /// <summary>Proposes a new tempo to the session (every peer follows).</summary>
    /// <param name="bpm">Tempo in beats per minute, clamped to 20…999.</param>
    public void SetTempo(double bpm)
    {
        lock (appGate)
        {
            N.captureAppSessionState(link, appState);
            // Skip no-op commits: every commit is broadcast to the peers, and the tool transports
            // (ToolTransport.Link.cs) call this whenever their tempo value is set.
            if (Math.Abs(N.tempo(appState) - bpm) < 1e-3) return;
            N.setTempo(appState, Math.Clamp(bpm, 20, 999), N.clockMicros(link));
            N.commitAppSessionState(link, appState);
        }
    }

    /// <summary>Shared transport: true while the session is playing.</summary>
    public bool IsPlaying
    {
        get
        {
            lock (appGate)
            {
                N.captureAppSessionState(link, appState);
                return N.isPlaying(appState) != 0;
            }
        }
    }

    /// <summary>Stops the shared transport now (only with start/stop sync; no-op when already stopped).</summary>
    public void RequestStop()
    {
        if (!StartStopSync) return;
        lock (appGate)
        {
            N.captureAppSessionState(link, appState);
            if (N.isPlaying(appState) == 0) return;
            N.setIsPlaying(appState, 0, (ulong)N.clockMicros(link));
            N.commitAppSessionState(link, appState);
        }
    }

    /// <summary>Starts the shared transport at host time <paramref name="micros"/> with <paramref name="beat"/> there (test helper / app).</summary>
    /// <param name="micros">Link host time (µs) of the start.</param>
    /// <param name="beat">Beat requested at that moment; Link honours it modulo the quantum
    /// (it only shifts the local beat count, never the shared phase).</param>
    public void RequestStart(long micros, double beat)
    {
        lock (appGate)
        {
            N.captureAppSessionState(link, appState);
            N.setIsPlayingAndRequestBeatAtTime(appState, 1, (ulong)micros, beat, Quantum);
            N.commitAppSessionState(link, appState);
        }
    }

    /// <summary>Values last written into the <c>LINK_*</c> channels (any thread; for tests and diagnostics).</summary>
    public (double Bpm, double Beat, double Phase, bool Playing, int Peers) LastWritten =>
        (Volatile.Read(ref lastBpm), Volatile.Read(ref lastBeat), Volatile.Read(ref lastPhase), Volatile.Read(ref lastPlaying) != 0, Volatile.Read(ref lastPeers));

    /// <summary>
    /// True when the score-time ↔ Link-time mapping is current (Csound running with this hook
    /// installed and a control cycle in the last half second).
    /// </summary>
    public bool HasClockMapping => mapValid && N.clockMicros(link) - Volatile.Read(ref mapUpdatedMicros) < 500_000;

    /// <summary>Host time (µs) at which audio computed for score time <paramref name="scoreTime"/> is heard.</summary>
    public long HostMicrosAtScoreTime(double scoreTime) => (long)(scoreTime * 1e6 + Volatile.Read(ref mapOffset));

    /// <summary>Score time whose audio is heard at host time <paramref name="micros"/>.</summary>
    public double ScoreTimeAtHostMicros(long micros) => (micros - Volatile.Read(ref mapOffset)) / 1e6;

    // ------------------------------------------------------------------------------------------
    // Performance thread

    /// <summary>Time constant of the score/host offset smoothing, seconds.</summary>
    const double OffsetTau = 1.0;

    /// <summary>A jump larger than this (µs) re-seeds the smoothing (new run, stall, device change).</summary>
    const double OffsetResetMicros = 80_000;

    /// <inheritdoc/>
    /// <remarks>
    /// Runs on Csound's performance thread before every <c>csoundPerformKsmps</c>: it must not
    /// block or allocate. It only uses the realtime-safe Link calls and the
    /// <see cref="audioState"/> scratch object, and <c>csoundSetControlChannel</c> marshals the
    /// short channel names on the stack (<c>LibraryImport</c> UTF-8).
    /// </remarks>
    public void OnControlCycle(IntPtr csound, double blockScoreTime, double sampleRate, double outputLatency)
    {
        long now = N.clockMicros(link);
        double d = now - blockScoreTime * 1e6;
        // Blocks are computed in bursts (one device period at a time), so d is a saw-tooth around
        // its mean; a one-second exponential average gives a steady offset that still follows the
        // slow drift between the sound card's clock and the host clock. Right after a (re)start it
        // is a plain running mean (weight dt/age), so it settles within a few periods instead of
        // carrying the first sample's error for a whole time constant.
        if (!mapValid || blockScoreTime < lastScore || Math.Abs(d - meanOffset) > OffsetResetMicros)
        {
            meanOffset = d;
            mapAge = 0;
        }
        else
        {
            double dt = blockScoreTime - lastScore;
            mapAge += dt;
            // One-pole low-pass with the exact discrete coefficient 1 − e^(−dt/τ); while the age is
            // shorter than τ the running-mean weight dt/age is larger and wins.
            double weight = Math.Max(1 - Math.Exp(-dt / OffsetTau), dt / Math.Max(dt, mapAge));
            meanOffset += (d - meanOffset) * weight;
        }
        lastScore = blockScoreTime;
        double offset = meanOffset + outputLatency * 1e6;
        Volatile.Write(ref mapOffset, offset);
        Volatile.Write(ref mapUpdatedMicros, now);
        mapValid = true;

        // Host time (µs) at which the first sample of this block leaves the speakers.
        long heard = (long)(blockScoreTime * 1e6 + offset);
        double q = Volatile.Read(ref quantum);
        N.captureAudioSessionState(link, audioState);
        double bpm = N.tempo(audioState);
        double beat = N.beatAtTime(audioState, heard, q);
        double phase = N.phaseAtTime(audioState, heard, q);
        int playing = N.isPlaying(audioState) != 0 ? 1 : 0;
        int peers = (int)N.numPeers(link);

        CsoundNative.csoundSetControlChannel(csound, "LINK_BPM", bpm);
        CsoundNative.csoundSetControlChannel(csound, "LINK_BEAT", beat);
        CsoundNative.csoundSetControlChannel(csound, "LINK_PHASE", phase);
        CsoundNative.csoundSetControlChannel(csound, "LINK_PLAYING", playing);
        CsoundNative.csoundSetControlChannel(csound, "LINK_PEERS", peers);
        CsoundNative.csoundSetControlChannel(csound, "LINK_QUANTUM", q);

        Volatile.Write(ref lastBpm, bpm);
        Volatile.Write(ref lastBeat, beat);
        Volatile.Write(ref lastPhase, phase);
        Volatile.Write(ref lastPlaying, playing);
        Volatile.Write(ref lastPeers, peers);
    }

    // ------------------------------------------------------------------------------------------
    // Tool transports

    /// <summary>A new sync object for one <see cref="TransportScheduler"/> (each tool has its own start bar).</summary>
    public ITransportSync CreateTransportSync() => new TransportSync(this);

    /// <summary>
    /// Locks a tool transport to the session: linear transport beat <i>L</i> is Link beat
    /// <c>origin + L</c>, where <c>origin</c> is the downbeat (multiple of the quantum) chosen at start.
    /// </summary>
    /// <remarks>
    /// Both methods run on non-audio threads (UI on start, the scheduler thread on every pump) and
    /// therefore use the shared <see cref="appState"/> under <see cref="appGate"/>. Mapping a beat
    /// goes Link beat → host µs (Link's timeline) → score time (this session's smoothed offset).
    /// </remarks>
    sealed class TransportSync(LinkSession s) : ITransportSync
    {
        /// <summary>Link beat (a multiple of the quantum) that corresponds to transport beat 0.</summary>
        double origin;

        /// <inheritdoc/>
        public bool BeginAt(double earliestScoreTime, out double startScoreTime, out double bpm)
        {
            startScoreTime = earliestScoreTime;
            bpm = 120;
            if (!s.IsEnabled || !s.HasClockMapping) return false;
            lock (s.appGate)
            {
                N.captureAppSessionState(s.link, s.appState);
                double q = s.Quantum;
                long earliest = s.HostMicrosAtScoreTime(earliestScoreTime);
                double beat = N.beatAtTime(s.appState, earliest, q);
                double start = Math.Ceiling(beat / q - 1e-9) * q;   // next downbeat
                bool sessionPlaying = N.isPlaying(s.appState) != 0;
                if (s.StartStopSync && sessionPlaying)
                {
                    // Another peer started (or we follow a start): if its start downbeat is still
                    // ahead of us, begin exactly there, as Live does.
                    // Host time of the last play transition, rounded to the bar it falls on.
                    long tp = (long)N.timeForIsPlaying(s.appState);
                    double startBeat = Math.Round(N.beatAtTime(s.appState, tp, q) / q) * q;
                    if (N.timeAtBeat(s.appState, startBeat, q) >= earliest)
                        start = startBeat;
                }
                else if (s.StartStopSync)
                {
                    // We start the shared transport, on our first downbeat.
                    N.setIsPlaying(s.appState, 1, (ulong)N.timeAtBeat(s.appState, start, q));
                    N.commitAppSessionState(s.link, s.appState);
                }
                origin = start;
                startScoreTime = s.ScoreTimeAtHostMicros(N.timeAtBeat(s.appState, start, q));
                bpm = N.tempo(s.appState);
                return true;
            }
        }

        /// <inheritdoc/>
        public bool Map(double linearBeat, out double scoreTime, out double bpm)
        {
            scoreTime = 0;
            bpm = 120;
            if (!s.IsEnabled || !s.HasClockMapping) return false;
            lock (s.appGate)
            {
                N.captureAppSessionState(s.link, s.appState);
                scoreTime = s.ScoreTimeAtHostMicros(N.timeAtBeat(s.appState, origin + linearBeat, s.Quantum));
                bpm = N.tempo(s.appState);
                return true;
            }
        }
    }
}
