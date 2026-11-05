namespace CsoundVisual.Music;

/// <summary>
/// External timeline the <see cref="TransportScheduler"/> can lock to (Ableton Link). When a sync
/// is attached, the scheduler asks it where linear beat 0 starts and, on every pump, re-anchors
/// its beat → score-time mapping on the external timeline (tempo and phase), so notes follow the
/// session even when another peer changes the tempo.
/// </summary>
/// <remarks>Called from the UI thread (start) and the scheduler thread (pumps): must be thread-safe.</remarks>
public interface ITransportSync
{
    /// <summary>
    /// Chooses where playback starts: the first downbeat of the external timeline (phase 0 of the
    /// quantum) at or after <paramref name="earliestScoreTime"/>.
    /// </summary>
    /// <param name="earliestScoreTime">Earliest Csound score time at which the first beat may sound.</param>
    /// <param name="startScoreTime">Score time of linear beat 0.</param>
    /// <param name="bpm">Current tempo of the external timeline.</param>
    /// <returns>False when the sync cannot map times right now (the scheduler then runs free).</returns>
    bool BeginAt(double earliestScoreTime, out double startScoreTime, out double bpm);

    /// <summary>Score time at which linear transport beat <paramref name="linearBeat"/> sounds, and the current tempo.</summary>
    /// <returns>False when the mapping is momentarily unavailable (the previous anchor is kept).</returns>
    bool Map(double linearBeat, out double scoreTime, out double bpm);
}
