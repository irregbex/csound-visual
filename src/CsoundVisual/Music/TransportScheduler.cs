using System.Diagnostics;

namespace CsoundVisual.Music;

/// <summary>One note produced by a pattern source, in the source's own timeline (beats).</summary>
/// <param name="Beat">Start, in beats of the source timeline (already swung, for the step sequencer).</param>
/// <param name="LengthBeats">Duration in beats.</param>
/// <param name="Instrument">p1 token (<c>1</c> or <c>"Voice"</c>).</param>
/// <param name="Fields">Everything after p3 (e.g. <c>60 0.787</c>).</param>
/// <param name="Tag">Optional tag for the UI (e.g. the sequencer track index), not sent to Csound.</param>
public readonly record struct PatternEvent(double Beat, double LengthBeats, string Instrument, string Fields, int Tag = -1);

/// <summary>
/// Something the <see cref="TransportScheduler"/> can play: the piano roll or the step sequencer.
/// Called from the scheduler thread, so implementations must be thread-safe (both tools hand the
/// scheduler an immutable snapshot that the UI replaces atomically after every edit).
/// </summary>
public interface IPatternSource
{
    /// <summary>Appends the events whose start lies in [<paramref name="fromBeat"/>, <paramref name="toBeat"/>) of the source timeline.</summary>
    /// <param name="fromBeat">Inclusive window start, timeline beats.</param>
    /// <param name="toBeat">Exclusive window end, timeline beats.</param>
    /// <param name="into">List to append to (owned by the scheduler; do not keep a reference).</param>
    void Collect(double fromBeat, double toBeat, List<PatternEvent> into);

    /// <summary>Loop region (beats); used when <see cref="Loop"/> is true.</summary>
    (double Start, double End) LoopRange { get; }

    /// <summary>Whether playback wraps from <see cref="LoopRange"/>'s end back to its start.</summary>
    bool Loop { get; }

    /// <summary>Where playback stops when not looping (beats; usually the end of the last note).</summary>
    double End { get; }
}

/// <summary>Clock and event sink of a running Csound performance (abstracted so the scheduler can be tested offline).</summary>
public interface IScoreTarget
{
    /// <summary>Current score time of the performance, seconds (advances in bursts of one audio buffer).</summary>
    double ScoreTime { get; }

    /// <summary>True while the performance runs (when it stops, the transport stops too).</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Queues <c>i &lt;instrument&gt; &lt;offset&gt; &lt;duration&gt; &lt;fields&gt;</c> so that it starts at the
    /// absolute score time <paramref name="atScoreTime"/>. The offset (p2) is computed by the
    /// performance thread itself right before the control cycle that receives the event, so it is
    /// exact whatever the delay between this call and that cycle.
    /// </summary>
    void SendAt(double atScoreTime, string instrument, double durationSeconds, string fields);
}

/// <summary>
/// Real-time note scheduler shared by the piano roll and the step sequencer.
/// </summary>
/// <remarks>
/// <para><b>Clock.</b> Time is measured on Csound's own score clock (<see cref="IScoreTarget.ScoreTime"/>),
/// not on the wall clock, so there is no drift between the two: whatever the audio device's
/// real sample rate, beat <i>b</i> sounds at score time <c>anchorTime + (b − anchorBeat)·60/bpm</c>.</para>
/// <para><b>Look-ahead.</b> A dedicated thread (above-normal priority, paced by a
/// <see cref="Stopwatch"/> every ~4 ms) asks the source for every event whose start falls before
/// <c>ScoreTime + LookAhead</c> and queues it with its absolute score time. The performance thread
/// converts that into the p2 offset just before the next control cycle (<c>i 1 0.032 0.5 60 0.8</c>),
/// so events start on the exact control period they belong to, independently of thread jitter or
/// of the audio buffer size (the score clock advances one buffer at a time; the look-ahead —
/// 120 ms by default — covers buffers up to ~100 ms).</para>
/// <para><b>Loop.</b> The transport runs on a linear beat counter; <see cref="MapToTimeline"/>
/// folds it into the source's loop region, cutting the scheduling window at the loop end so that
/// notes right before and right after the wrap are both sent.</para>
/// <para><b>Tempo changes</b> re-anchor the mapping at the point already scheduled, so nothing is
/// sent twice and nothing is skipped.</para>
/// <para>The scheduling core (<see cref="Pump"/>) is deterministic and can be driven by hand
/// (tests); <see cref="Start"/> runs it on the thread.</para>
/// </remarks>
public sealed class TransportScheduler : IDisposable
{
    /// <summary>How far ahead of the score clock events are queued, seconds.</summary>
    public double LookAhead { get; set; } = 0.12;

    /// <summary>Events that would start more than this many seconds late are dropped instead of played in a bunch (after a stall).</summary>
    public const double MaxLateness = 0.25;

    /// <summary>Clock and event sink (the engine, or a fake in tests).</summary>
    readonly IScoreTarget target;
    /// <summary>
    /// Guards the tempo mapping (<see cref="anchorTime"/>, <see cref="anchorBeat"/>, <see cref="bpm"/>,
    /// <see cref="scheduledBeat"/>, <see cref="startBeat"/>) and the <see cref="source"/>/<see cref="sync"/>
    /// references, which the UI thread changes while the scheduler thread reads them.
    /// </summary>
    readonly object gate = new();
    /// <summary>Reused list for <see cref="IPatternSource.Collect"/>; only touched by the pumping thread, so steady-state pumps do not allocate.</summary>
    readonly List<PatternEvent> scratch = [];
    /// <summary>Current pattern snapshot (see <see cref="Source"/>).</summary>
    IPatternSource source;
    /// <summary>Optional external timeline (see <see cref="Sync"/>).</summary>
    ITransportSync? sync;
    /// <summary>The scheduler thread, or <c>null</c> when stopped or driven by hand.</summary>
    Thread? thread;
    /// <summary>Run flag: cleared by <see cref="Stop"/> (any thread) and polled by <see cref="Run"/>.</summary>
    volatile bool running;
    /// <summary>Tempo in quarter notes per minute (see <see cref="Bpm"/>).</summary>
    double bpm = 120;

    // Mapping linear beats ↔ score time: time = anchorTime + (beat − anchorBeat) · 60 / bpm.
    double anchorTime, anchorBeat;
    // Everything before this linear beat has already been sent.
    double scheduledBeat;
    // Timeline position where playback started (linear beat 0 = startBeat on the timeline).
    double startBeat;

    /// <summary>Creates a scheduler that plays <paramref name="source"/> into <paramref name="target"/>.</summary>
    /// <param name="target">Csound clock and event sink (normally an <see cref="EngineScoreTarget"/>).</param>
    /// <param name="source">Initial pattern snapshot; can be replaced later through <see cref="Source"/>.</param>
    public TransportScheduler(IScoreTarget target, IPatternSource source)
    {
        this.target = target;
        this.source = source;
    }

    /// <summary>Raised (on the scheduler thread) for every event sent, with its absolute score time.</summary>
    public event Action<PatternEvent, double>? EventSent;

    /// <summary>Raised (on the scheduler thread) when playback stops by itself: end reached or Csound stopped.</summary>
    /// <remarks>The argument is <see cref="IScoreTarget.IsRunning"/> at that moment: <c>true</c> = the pattern ended, <c>false</c> = Csound stopped.</remarks>
    public event Action<bool>? StoppedByItself;

    /// <summary>True while the transport plays.</summary>
    public bool IsPlaying => running;

    /// <summary>Tempo in beats (quarter notes) per minute, 20…400. May be changed while playing.</summary>
    public double Bpm
    {
        get => bpm;
        set
        {
            value = Math.Clamp(value, 20, 400);
            lock (gate)
            {
                if (running)
                {
                    // Re-anchor at the point already scheduled: what was sent stays valid.
                    anchorTime = TimeOfLinear(scheduledBeat);
                    anchorBeat = scheduledBeat;
                }
                bpm = value;
            }
        }
    }

    /// <summary>The pattern being played (swapped atomically; takes effect for events not yet sent).</summary>
    public IPatternSource Source
    {
        get => source;
        set { lock (gate) source = value; }
    }

    /// <summary>
    /// Optional external timeline (Ableton Link) the transport locks to: start on its next
    /// downbeat, then follow its tempo and phase. Set before <see cref="Start"/>; null = free-running.
    /// </summary>
    public ITransportSync? Sync
    {
        get => sync;
        set { lock (gate) sync = value; }
    }

    /// <summary>Seconds per beat at the current tempo.</summary>
    public double SecondsPerBeat => 60.0 / bpm;

    /// <summary>Score time of linear beat <paramref name="linear"/> under the current anchor.</summary>
    double TimeOfLinear(double linear) => anchorTime + (linear - anchorBeat) * 60.0 / bpm;

    /// <summary>Linear transport beat at score time <paramref name="time"/> (may be negative before the start).</summary>
    public double LinearBeatAt(double time)
    {
        lock (gate) return anchorBeat + (time - anchorTime) * bpm / 60.0;
    }

    /// <summary>
    /// Timeline position (what the playhead shows) at score time <paramref name="time"/>, or
    /// <c>null</c> when not playing or before the first beat.
    /// </summary>
    public double? TimelineBeatAt(double time)
    {
        if (!running) return null;
        double linear = LinearBeatAt(time);
        if (linear < 0) return startBeat;
        IPatternSource src;
        lock (gate) src = source;
        return MapToTimeline(linear, startBeat, src.Loop, src.LoopRange);
    }

    /// <summary>
    /// Folds a linear transport beat into the source timeline: playback starts at
    /// <paramref name="start"/>; with <paramref name="loop"/> and a start before the loop end, it
    /// wraps from the loop end back to the loop start.
    /// </summary>
    /// <param name="linear">Beats elapsed since playback started (≥ 0).</param>
    /// <param name="start">Timeline beat where playback started.</param>
    /// <param name="loop">Whether the source loops.</param>
    /// <param name="range">Loop region in timeline beats.</param>
    /// <returns>The timeline beat (what the playhead shows).</returns>
    public static double MapToTimeline(double linear, double start, bool loop, (double Start, double End) range)
    {
        // Starting at or after the loop end means "play past the loop": no wrapping at all.
        double t = start + linear;
        double len = range.End - range.Start;
        if (!loop || len <= 1e-9 || start >= range.End || t < range.End)
            return t;
        return range.Start + (t - range.End) % len;
    }

    /// <summary>
    /// Starts playing from timeline beat <paramref name="fromBeat"/>. The first beat is placed
    /// <paramref name="leadIn"/> seconds after the current score time (room for the look-ahead).
    /// </summary>
    /// <param name="fromBeat">Timeline position to start from.</param>
    /// <param name="leadIn">Delay before the first beat, seconds.</param>
    /// <param name="runThread">False for tests: <see cref="Pump"/> is then called by hand.</param>
    public void Start(double fromBeat, double leadIn = 0.06, bool runThread = true)
    {
        Stop();
        lock (gate)
        {
            startBeat = Math.Max(0, fromBeat);
            anchorBeat = 0;
            anchorTime = target.ScoreTime + leadIn;
            // Locked to an external timeline (Ableton Link): start on its next downbeat.
            if (sync is not null && sync.BeginAt(anchorTime, out double syncStart, out double syncBpm))
            {
                anchorTime = syncStart;
                bpm = Math.Clamp(syncBpm, 20, 400);
            }
            scheduledBeat = 0;
        }
        running = true;
        if (!runThread) return;
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Transport scheduler",
            Priority = ThreadPriority.AboveNormal,
        };
        thread.Start();
    }

    /// <summary>Stops the transport (notes already sounding finish their duration).</summary>
    /// <remarks>
    /// Waits up to 500 ms for the thread to finish its current pump. Events already handed to
    /// Csound (up to <see cref="LookAhead"/> ahead) still play. Safe to call from the scheduler
    /// thread itself (e.g. from a <see cref="StoppedByItself"/> handler): it does not join itself.
    /// </remarks>
    public void Stop()
    {
        running = false;
        var t = thread;
        thread = null;
        if (t is not null && t != Thread.CurrentThread)
            t.Join(500);
    }

    /// <summary>Scheduler thread body: pump every ~4 ms until stopped.</summary>
    void Run()
    {
        var clock = Stopwatch.StartNew();
        long next = 0;
        while (running)
        {
            try
            {
                if (!Pump())
                {
                    running = false;
                    StoppedByItself?.Invoke(target.IsRunning);
                    break;
                }
            }
            catch
            {
                // A faulty source must never kill the app; the transport just stops.
                running = false;
                StoppedByItself?.Invoke(target.IsRunning);
                break;
            }
            // Fixed 4 ms grid measured on the Stopwatch. Thread.Sleep may overshoot (Windows timer
            // resolution can be up to ~15.6 ms); that is harmless because the look-ahead is far
            // larger and Pump always catches up to ScoreTime + LookAhead in one pass.
            next += 4;
            long wait = next - clock.ElapsedMilliseconds;
            if (wait > 0)
                Thread.Sleep((int)wait);
            else
                next = clock.ElapsedMilliseconds; // fell behind (debugger, sleep): do not burst
        }
    }

    /// <summary>
    /// One scheduling pass: sends every event that starts before <c>ScoreTime + LookAhead</c> and
    /// has not been sent yet. Returns <c>false</c> when playback is over (end reached without
    /// loop, or the performance stopped).
    /// </summary>
    public bool Pump()
    {
        if (!running) return false;
        if (!target.IsRunning) return false;
        double now = target.ScoreTime;
        IPatternSource src;
        double from, to, start;
        lock (gate)
        {
            src = source;
            from = scheduledBeat;
            // Locked to an external timeline: re-anchor on it at the point already scheduled
            // (follows tempo changes and phase corrections of the session).
            if (sync is not null && sync.Map(scheduledBeat, out double syncTime, out double syncBpm))
            {
                anchorBeat = scheduledBeat;
                anchorTime = syncTime;
                bpm = Math.Clamp(syncBpm, 20, 400);
            }
            // Linear beat that sounds at now + LookAhead: the end of this pass's window.
            to = anchorBeat + (now + LookAhead - anchorTime) * bpm / 60.0;
            start = startBeat;
            if (to <= from) return true; // score clock has not advanced enough since the last pass
            // Claim the window before sending, so the next pass starts exactly where this one ends.
            scheduledBeat = to;
        }

        var (loopStart, loopEnd) = src.LoopRange;
        bool loop = src.Loop && loopEnd - loopStart > 1e-6 && start < loopEnd;
        if (!loop && start + from >= src.End)
        {
            // Everything was sent: keep the playhead running until the end is actually reached.
            lock (gate) return TimeOfLinear(src.End - start) > now;
        }

        // Walk [from, to) of linear beats pass by pass. Pass 0 is the run-in from the start
        // position to the loop end; pass k ≥ 1 is the k-th repetition of the loop region. Inside a
        // pass, timeline = linear − offset with an offset computed from the integer pass number,
        // so consecutive windows split each pass at exactly the same boundaries (an event right
        // on the wrap point is sent once, never zero or two times).
        if (!loop)
        {
            CollectAndSend(src, start + from, start + to, -start, now);
            return true;
        }
        double runIn = loopEnd - start;                 // linear length of pass 0
        double len = loopEnd - loopStart;
        int firstPass = from < runIn ? 0 : 1 + (int)Math.Floor((from - runIn) / len);
        // The 10000-pass cap only guards against a pathologically tiny loop; a normal window
        // spans one or two passes.
        for (int k = firstPass; k < firstPass + 10000; k++)
        {
            double passStart = k == 0 ? 0 : runIn + (k - 1) * len;
            double passEnd = k == 0 ? runIn : runIn + k * len;
            if (passStart >= to) break;
            double a = Math.Max(from, passStart), b = Math.Min(to, passEnd);
            if (b <= a) continue;
            double offset = k == 0 ? -start : runIn + (k - 1) * len - loopStart;
            CollectAndSend(src, a - offset, b - offset, offset, now);
        }
        return true;
    }

    /// <summary>
    /// Collects the source events of timeline range [<paramref name="tlFrom"/>, <paramref name="tlTo"/>)
    /// and sends them; <paramref name="offset"/> converts their timeline beat to the linear beat
    /// (linear = timeline + offset) whose score time is computed from the tempo anchor.
    /// </summary>
    void CollectAndSend(IPatternSource src, double tlFrom, double tlTo, double offset, double now)
    {
        scratch.Clear();
        src.Collect(tlFrom, tlTo, scratch);
        foreach (var ev in scratch)
        {
            double at, dur;
            lock (gate)
            {
                at = TimeOfLinear(ev.Beat + offset);
                dur = ev.LengthBeats * 60.0 / bpm;
            }
            if (at < now - MaxLateness) continue; // too late to be musical: skip rather than burst
            // A 1 ms floor keeps p3 positive (p3 = 0 would mean "init pass only" in Csound).
            target.SendAt(at, ev.Instrument, Math.Max(0.001, dur), ev.Fields);
            EventSent?.Invoke(ev, at);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();
}
