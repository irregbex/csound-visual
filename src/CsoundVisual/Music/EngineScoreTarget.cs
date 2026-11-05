using CsoundVisual.Engine;

namespace CsoundVisual.Music;

/// <summary>
/// Connects the <see cref="TransportScheduler"/> to the app's <see cref="CsoundEngine"/>: the
/// clock is the engine's score time and notes go through <see cref="CsoundEngine.SendEventAt"/>.
/// </summary>
/// <remarks>
/// Called from the scheduler's own thread. <see cref="CsoundEngine.ScoreTime"/> is a snapshot the
/// performance thread publishes once per control cycle, and <see cref="CsoundEngine.SendEventAt"/>
/// only enqueues; the absolute time is converted into p2 on the performance thread just before
/// the next cycle, so neither call blocks on Csound.
/// </remarks>
/// <param name="engine">The engine.</param>
/// <param name="isPlaying">True while the engine performs in real time (Play mode; not while rendering).</param>
public sealed class EngineScoreTarget(CsoundEngine engine, Func<bool> isPlaying) : IScoreTarget
{
    /// <inheritdoc/>
    public double ScoreTime => engine.ScoreTime;

    /// <inheritdoc/>
    public bool IsRunning => engine.IsBusy && isPlaying();

    /// <inheritdoc/>
    public void SendAt(double atScoreTime, string instrument, double durationSeconds, string fields) =>
        engine.SendEventAt(atScoreTime, instrument, durationSeconds, fields);
}
