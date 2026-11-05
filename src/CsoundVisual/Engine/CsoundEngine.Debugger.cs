using CsoundVisual.Localization;

namespace CsoundVisual.Engine;

/// <summary>Breakpoint support of the engine (see <see cref="BreakpointSession"/>).</summary>
/// <remarks>
/// <para>
/// Flow: the UI builds a <see cref="BreakpointSession"/> and hands it over with
/// <see cref="UseBreakpoints"/> before <see cref="Start"/>; the performance thread installs it
/// after compiling (<see cref="AttachBreakpoints"/>), lets it apply pending requests before each
/// <c>csoundPerformKsmps</c> (<see cref="DebugBeforeCycle"/>), and checks afterwards whether the
/// call produced audio (<see cref="DebugAfterCycle"/>). While Csound is stopped at a breakpoint,
/// each "cycle" returns without computing, so the host loop writes silence to the device instead
/// of <c>spout</c>: the stream keeps running and nothing glitches.
/// </para>
/// <para>
/// Only the app's own WASAPI engine supports this, because it owns the loop and decides what the
/// device receives for a cycle that produced no audio. When Csound drives the device itself
/// (PortAudio/ASIO) or renders to a file, its own I/O consumes <c>spout</c> directly and the host
/// has no way to substitute silence, so breakpoints are ignored there.
/// </para>
/// </remarks>
public sealed unsafe partial class CsoundEngine
{
    /// <summary>Session handed over by <see cref="UseBreakpoints"/>, taken (and cleared) by the next run.</summary>
    BreakpointSession? requestedBreakpoints;

    /// <summary>
    /// True when the last <see cref="Step"/> did not complete a control cycle (stopped at a
    /// breakpoint, or interrupted by a hit that is resumed on the next call): <c>spout</c> holds
    /// no valid audio and the cycle must not be played, metered or recorded.
    /// </summary>
    bool cycleWithoutAudio;

    /// <summary>
    /// Diagnostics only (<c>--breakpoint-test</c>): the device receives silence (the output gain is
    /// forced to 0) while Csound computes normally, so self-tests can use the real audio path quietly.
    /// </summary>
    public bool MuteOutput { get; set; }

    /// <summary>Breakpoints of the current run, or <c>null</c> (not playing, or not the app's own audio engine).</summary>
    public BreakpointSession? Breakpoints { get; private set; }

    /// <summary>True while the performance is stopped at a breakpoint (the device plays silence).</summary>
    public bool IsPausedAtBreakpoint => Breakpoints is { IsPaused: true };

    /// <summary>
    /// Hands a breakpoint session to the next <see cref="Start"/>. Breakpoints only work with the
    /// application's own WASAPI engine; otherwise the run prints a note once and ignores them.
    /// </summary>
    public void UseBreakpoints(BreakpointSession? session) => requestedBreakpoints = session;

    /// <summary>
    /// Queues orchestra code so that Csound numbers its lines like the editor does: the code is
    /// preceded by blank lines, because Csound numbers code compiled during the performance from 1
    /// plus the line of the CSD's <c>&lt;CsInstruments&gt;</c> tag. Line breakpoints and error
    /// messages then keep pointing at the right editor lines after a live recompile.
    /// </summary>
    /// <param name="orc">Orchestra code (an <c>instr … endin</c> block, a selection…).</param>
    /// <param name="firstLine">Editor line of the first line of <paramref name="orc"/>.</param>
    /// <param name="csInstrumentsLine">Line of the <c>&lt;CsInstruments&gt;</c> tag in the CSD that was started.</param>
    /// <returns>The line Csound will assign to the first line of the code.</returns>
    /// <remarks>
    /// Example: <c>&lt;CsInstruments&gt;</c> on line 3, instrument starting on editor line 20 →
    /// 16 blank lines are prepended, and Csound numbers the code's first line 1 + 16 + 3 = 20.
    /// When the code starts at or above that origin no padding is possible and the returned line
    /// differs from <paramref name="firstLine"/>.
    /// </remarks>
    public int CompileOrcAt(string orc, int firstLine, int csInstrumentsLine)
    {
        int padding = firstLine - 1 - csInstrumentsLine;
        if (padding > 0)
            orc = new string('\n', padding) + orc;
        CompileOrc(orc);
        return Math.Max(padding, 0) + 1 + csInstrumentsLine;
    }

    /// <summary>Takes the requested session and installs it (performance thread, after compiling).</summary>
    /// <param name="cs">The compiled Csound instance.</param>
    /// <param name="job">The run being started (for its named instruments).</param>
    /// <param name="appAudioEngine"><c>true</c> when the app's own WASAPI loop drives the performance.</param>
    /// <remarks>
    /// Csound 7 has one debugger per instance: when the <see cref="LiveValueMonitor"/> already
    /// initialised it (<see cref="Live"/> not null) the session shares it instead of initialising
    /// it again (<c>debuggerReady</c>).
    /// </remarks>
    void AttachBreakpoints(IntPtr cs, EngineJob job, bool appAudioEngine)
    {
        Breakpoints = null;
        cycleWithoutAudio = false;
        var session = Interlocked.Exchange(ref requestedBreakpoints, null);
        if (session is null)
            return;
        if (!appAudioEngine)
        {
            if (session.HasBreakpointsAtStart)
                Post(Loc.S("⏸ Breakpoints ignorados: solo funcionan al tocar con el motor de audio de la app (Windows · WASAPI), no al renderizar a WAV ni cuando Csound maneja el dispositivo (PortAudio/ASIO).",
                           "⏸ Breakpoints ignored: they only work while playing with the app's own audio engine (Windows · WASAPI), not when rendering to WAV or when Csound drives the device (PortAudio/ASIO)."),
                     MessageKind.Info);
            return;
        }
        if (session.Attach(cs, debuggerReady: Live is not null, job.InstrumentNames ?? []))
            Breakpoints = session;
        else
            Post(Loc.S("⚠ No se pudo activar el depurador de Csound: los breakpoints no funcionarán.",
                       "⚠ Could not enable Csound's debugger: breakpoints will not work."), MessageKind.Warning);
    }

    /// <summary>Removes the breakpoints and resumes Csound before it is destroyed.</summary>
    void DetachBreakpoints(IntPtr cs)
    {
        Breakpoints?.Detach(cs);
    }

    /// <summary>Applies pending breakpoint requests right before <c>csoundPerformKsmps</c>.</summary>
    /// <remarks>Also enforces <see cref="MuteOutput"/> by zeroing the 1/0dbfs output scale every cycle.</remarks>
    void DebugBeforeCycle(IntPtr cs)
    {
        if (MuteOutput)
            scale = 0;
        if (Breakpoints is not { } bp)
            return;
        bp.Configure(Ksmps);
        bp.BeforeCycle(cs);
    }

    /// <summary>Records whether the call that just returned produced a complete control cycle.</summary>
    void DebugAfterCycle() => cycleWithoutAudio = Breakpoints is { CycleProducedAudio: false };
}
