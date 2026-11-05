using CsoundVisual.Interop;

namespace CsoundVisual.Engine;

/// <summary>
/// Code that runs on the performance thread right before every control cycle (e.g. Ableton Link
/// writing its <c>LINK_*</c> channels). Implementations must be cheap and must not allocate or
/// block: they run once per <c>ksmps</c> frames inside the audio loop.
/// </summary>
/// <remarks>
/// The only implementation today is <see cref="Integration.LinkSession"/>, which converts the
/// block's score time plus the output latency into a Link beat/phase and writes it into Csound
/// channels so the orchestra plays in sync with the other Link peers.
/// </remarks>
public interface IControlCycleHook
{
    /// <summary>Called before <c>csoundPerformKsmps</c>.</summary>
    /// <param name="csound">The native Csound instance (valid only during the call).</param>
    /// <param name="blockScoreTime">Score time of the first frame of the block about to be computed, seconds.</param>
    /// <param name="sampleRate">Orchestra sample rate.</param>
    /// <param name="outputLatency">Estimated delay from computing a block to hearing it, seconds (0 if unknown).</param>
    void OnControlCycle(IntPtr csound, double blockScoreTime, double sampleRate, double outputLatency);
}

/// <summary>Per-control-cycle hook support of the engine (see <see cref="IControlCycleHook"/>).</summary>
public sealed unsafe partial class CsoundEngine
{
    /// <summary>Backing field of <see cref="CycleHook"/>; written by the UI thread, read by the performance thread.</summary>
    IControlCycleHook? cycleHook;

    /// <summary>Optional per-control-cycle hook (see <see cref="IControlCycleHook"/>); may be swapped at any time.</summary>
    public IControlCycleHook? CycleHook
    {
        get => Volatile.Read(ref cycleHook);
        set => Volatile.Write(ref cycleHook, value);
    }

    /// <summary>
    /// Estimated output latency of the current performance, seconds: with the app's own WASAPI
    /// output, the stream latency reported by Windows plus one device period, plus half a period
    /// for the burst in which blocks are computed. 0 when Csound drives the device itself
    /// (PortAudio/ASIO: the delay is unknown to the host).
    /// </summary>
    public double OutputLatencySeconds { get; private set; }

    /// <summary>Records the latency of the WASAPI stream just opened (called from <see cref="Run"/>).</summary>
    /// <remarks>
    /// <see cref="Audio.WasapiOutput.LatencyMs"/> already contains one period plus Windows' stream
    /// latency. The extra half period accounts for the burst: all blocks of a period are computed
    /// together at the period start, so on average a block is computed half a period before its
    /// position in the buffer would suggest.
    /// </remarks>
    void NoteOutputLatency(Audio.WasapiOutput? output) =>
        OutputLatencySeconds = output is null || output.SampleRate <= 0
            ? 0
            : output.LatencyMs / 1000.0 + 0.5 * output.PeriodFrames / output.SampleRate;

    /// <summary>Runs the cycle hook, if any, for the block about to be performed (performance thread).</summary>
    void RunCycleHook(IntPtr cs)
    {
        // Read once: the UI may swap the hook concurrently; this block uses a consistent instance.
        var hook = Volatile.Read(ref cycleHook);
        if (hook is null) return;
        try
        {
            hook.OnControlCycle(cs, CsoundNative.csoundGetScoreTime(cs), SampleRate, OutputLatencySeconds);
        }
        catch
        {
            // A faulty hook must never stop the audio: drop it.
            Volatile.Write(ref cycleHook, null);
        }
    }
}
