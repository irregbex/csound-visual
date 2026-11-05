// SoundPreviewPlayer.cs — Paint Rooms: plays a WAV file through the app's running Csound.
//
// Role: the «faithful» (spectral mask) resynthesis of the Spectrum Room is computed in C# (STFT →
// mask → inverse STFT, SpectralMask.cs). To LISTEN to it, the room writes the result to a temporary
// stereo WAV and this player streams that file from disk with Csound's diskin2 opcode, inside a small
// instrument added to whatever performance the app is running. So everything you hear in Paint
// Rooms goes through the same Csound engine, the same output device, the same meters and the same
// main Stop button. (Design choice, documented in docs/en/paint-rooms.md: rendering the whole
// result first and then playing a file is simpler and glitch-free compared with streaming a live
// STFT into Csound; a 60 s stereo file renders in about a second, and the room re-renders and
// resumes at the same place after every edit.)
//
// How (same pattern as PaintLivePlayer):
//   * If Csound is stopped, the «Paint Rooms host» orchestra is started (IPaintEngineHost) and the
//     player waits for it in Tick.
//   * The first time in a performance, instrument 9311 is compiled into it (CsoundEngine.CompileNow
//     on the Csound thread, through InvokeOnCsoundThread).
//   * Each Play starts a held note «i 9311.nnnn 0 -1 "file" skip dur» whose fractional number
//     identifies it, so Stop («i -9311.nnnn») ends exactly that one with its 30 ms fade-out.
//   * diskin2 converts the file's sample rate to the engine's when they differ (verified offline:
//     a 1 kHz sine in a 48 kHz file plays at 999.96 Hz in a 44.1 kHz orchestra).
//
// Threading: public members are for the UI thread; the Csound-thread actions only write the fields
// marked as such, read back through Volatile.
//
// Differences from PaintLivePlayer: no tables to upload (diskin2 reads the file itself), so a
// single Csound-thread action per Play is enough; and although the note is held (p3 = −1: Csound's
// «i -N» turn-off works ONLY on held notes — an earlier version used a finite p3, so Stop never
// silenced it while another document kept Csound running), the instrument turns itself off after
// p6 seconds (the rest of the file + 0.1 s), so it also ends by itself if the UI never stops it.

using CsoundVisual.Engine;

namespace CsoundVisual.Paint;

/// <summary>Plays sound files live through the app's Csound engine (see the file header).</summary>
public sealed class SoundPreviewPlayer
{
    /// <summary>Instrument number of the preview instrument.</summary>
    /// <remarks>In the 93xx range reserved for Paint Rooms (9301/9302 Paint Synth), far from user instruments.</remarks>
    public const int InstrNumber = 9311;

    /// <summary>The preview instrument (Csound 7 syntax; reads a STEREO file).</summary>
    /// <returns>Orchestra code for instr <see cref="InstrNumber"/>.</returns>
    /// <remarks>
    /// p4 is a string p-field (the file path), p5 the skip time in seconds and p6 the playing time
    /// after which the held note (p3 = −1) turns itself off. <c>diskin2 … , 1</c> =
    /// pitch ratio 1 (original speed; diskin2 still converts the file's sample rate to sr).
    /// <c>aL, aR</c> requires a stereo file — a mono file would make diskin2 fail at init. The
    /// envelope is k-rate (no <c>interp</c>); over 5 ms / 30 ms ramps that is inaudible. <c>out</c>
    /// with two arguments assumes a stereo orchestra: unlike instr 9301 (which checks
    /// <c>nchnls == 1</c>), compiled into a user's mono document this instrument would not match
    /// its channel count.
    /// </remarks>
    public static string Instrument() =>
        "\n; ---- Csound Visual · Paint Rooms · Spectrum Room preview -------------------------\n" +
        "; Plays a stereo WAV written by the Spectrum Room (its faithful resynthesis) from p5 seconds.\n" +
        "; diskin2 streams from disk and converts the file's sample rate to the orchestra's.\n" +
        $"instr {InstrNumber}\n" +
        "  aL, aR diskin2 p4, 1, p5          ; p4 = file, p5 = start (s)\n" +
        "  kEnv linsegr 0, 0.005, 1, 0.03, 0  ; 5 ms fade-in, 30 ms fade-out when stopped\n" +
        "  out aL * kEnv, aR * kEnv\n" +
        "  if timeinsts() >= p6 then           ; held note (p3 = -1): end by itself after p6 s\n" +
        "    turnoff\n" +
        "  endif\n" +
        "endin\n";

    /// <summary>The application side (engine, host orchestra start/stop).</summary>
    readonly IPaintEngineHost host;

    // ---- UI thread

    /// <summary>Path of the WAV being played (kept with backslashes; converted when the score line is built).</summary>
    string? file;

    /// <summary>Start position in the file (s) and the file's total length (s).</summary>
    double fromSeconds, fileSeconds;

    /// <summary>Play counter: selects the fractional instance number and cancels superseded launches.</summary>
    int generation;

    /// <summary>
    /// The <see cref="generation"/> cancelled by <see cref="Stop"/> (−1 = none): a launch action of
    /// that play still queued for the next control cycle returns without starting its note.
    /// Written on the UI thread, read on the Csound thread (Volatile).
    /// </summary>
    int stoppedGeneration = -1;

    /// <summary>UTC time the host orchestra was launched (start-up timeout).</summary>
    DateTime startingSince;

    /// <summary>True when this player started the host orchestra and must stop it when done.</summary>
    bool stopHostWhenIdle;

    // ---- written on the Csound thread

    /// <summary>CSOUND* instr 9311 was compiled into; reset when a performance finishes.</summary>
    IntPtr compiledFor;

    /// <summary>Score time (s) at which the current note started; NaN until the Csound-thread action ran.</summary>
    double noteStartScore = double.NaN;

    /// <summary>Creates the player for <paramref name="host"/> (the app's main view model).</summary>
    /// <param name="host">The application side; its engine's <c>Finished</c> event is subscribed to.</param>
    public SoundPreviewPlayer(IPaintEngineHost host)
    {
        this.host = host;
        // A new performance may reuse the CSOUND* address: forget what was compiled (performance thread).
        host.Engine.Finished += _ => compiledFor = IntPtr.Zero;
    }

    /// <summary>Current state (Idle, Starting = waiting for Csound, Playing).</summary>
    public PaintPlayState State { get; private set; }

    /// <summary>The file being played, or <c>null</c>.</summary>
    public string? File => State == PaintPlayState.Idle ? null : file;

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    public event Action? StateChanged;

    /// <summary>Raised with a status message (text, is-warning).</summary>
    public event Action<string, bool>? Message;

    /// <summary>
    /// Plays <paramref name="path"/> (a STEREO WAV) from <paramref name="from"/> seconds, replacing
    /// whatever this player was playing. Starts Csound (host orchestra) when it is stopped.
    /// </summary>
    /// <param name="path">WAV file (no double quotes in the path).</param>
    /// <param name="from">Start position in seconds.</param>
    /// <param name="durationSeconds">Length of the file in seconds (to know when it ends).</param>
    /// <returns>False when nothing can play (a file render is running, the path contains a quote,
    /// or Csound could not be started); true when playing or starting.</returns>
    public bool Play(string path, double from, double durationSeconds)
    {
        if (host.IsRenderingFile)
        {
            Message?.Invoke(Localization.Loc.S("Csound está renderizando un documento a WAV; espera a que termine.",
                                               "Csound is rendering a document to WAV; wait until it finishes."), true);
            return false;
        }
        // A quote would end the Csound string in the score line (no escaping is possible there).
        if (path.Contains('"')) return false;
        file = path;
        fileSeconds = durationSeconds;
        // Start at least 10 ms before the end so there is always something to hear.
        fromSeconds = Math.Clamp(from, 0, Math.Max(0, durationSeconds - 0.01));
        if (host.IsPlayingLive && host.Engine.SampleRate > 0)
            return Launch();
        if (!host.StartPaintHost())
        {
            Message?.Invoke(Localization.Loc.S("No se pudo iniciar Csound (revisa la consola de la ventana principal).",
                                               "Csound could not be started (check the console in the main window)."), true);
            return false;
        }
        stopHostWhenIdle = true;
        startingSince = DateTime.UtcNow;
        SetState(PaintPlayState.Starting);
        return true;
    }

    /// <summary>Stops the preview (30 ms fade). Stops Csound too when it was started only for this.</summary>
    /// <param name="keepHost">Leave the host orchestra running (another Paint Rooms player takes over at once).</param>
    /// <remarks>
    /// As in <see cref="PaintLivePlayer.Stop"/>: the play is first marked as cancelled
    /// (<see cref="stoppedGeneration"/>) so a launch action still queued for the next control cycle
    /// does nothing, then the turn-off is queued after it; on the single Csound thread the note is
    /// either never started or started and then turned off. (Before this fix a fast Play→Stop could
    /// start the note after the stop.)
    /// </remarks>
    public void Stop(bool keepHost = false)
    {
        if (State != PaintPlayState.Idle)
            Volatile.Write(ref stoppedGeneration, generation);
        if (State == PaintPlayState.Playing)
        {
            // Turn off exactly the current instance; its linsegr release (30 ms) plays.
            string off = $"i -{InstanceNumber(generation)} 0 0";
            host.Engine.InvokeOnCsoundThread(cs => CsoundEngine.EventNow(cs, off));
        }
        if (!keepHost && State != PaintPlayState.Idle && stopHostWhenIdle && host.IsPaintHostRunning)
            host.StopPaintHost();
        stopHostWhenIdle = false;
        SetState(PaintPlayState.Idle);
    }

    /// <summary>Position being heard (seconds in the file), or <c>null</c> when not playing; accounts for the output latency.</summary>
    public double? CurrentSeconds
    {
        get
        {
            if (State != PaintPlayState.Playing) return null;
            double start = Volatile.Read(ref noteStartScore);
            if (double.IsNaN(start)) return fromSeconds; // note not started yet
            // Score seconds since the note started, minus the output latency = seconds already heard.
            // (diskin2 resamples, so file seconds = score seconds whatever the file's rate.)
            return fromSeconds + Math.Max(0, host.Engine.ScoreTime - start - host.Engine.OutputLatencySeconds);
        }
    }

    /// <summary>Call ~30 times per second (UI thread): finishes a pending start and notices the end of the file or of Csound.</summary>
    public void Tick()
    {
        switch (State)
        {
            case PaintPlayState.Starting:
                // Same readiness test and timeouts as PaintLivePlayer.Tick.
                if (host.IsPlayingLive && host.Engine.SampleRate > 0 && host.Engine.ScoreTime > 0.02)
                    Launch();
                else if (DateTime.UtcNow - startingSince > TimeSpan.FromSeconds(10) ||
                         (!host.IsPlayingLive && DateTime.UtcNow - startingSince > TimeSpan.FromSeconds(1.5)))
                {
                    Message?.Invoke(Localization.Loc.S("Csound no pudo empezar a tocar: revisa la consola de la ventana principal.",
                                                       "Csound could not start playing: check the console in the main window."), true);
                    SetState(PaintPlayState.Idle);
                }
                break;
            case PaintPlayState.Playing:
                if (!host.IsPlayingLive || host.Engine.SampleRate <= 0)
                    SetState(PaintPlayState.Idle);   // Csound stopped (main Stop button, error…)
                // The file has been heard to the end (+150 ms for the note's tail): go idle and
                // stop the host if we started it.
                else if (CurrentSeconds is { } t && t > fileSeconds + 0.15)
                    Stop();
                break;
        }
    }

    /// <summary>Fractional instrument number of play <paramref name="gen"/>.</summary>
    /// <param name="gen">Play counter.</param>
    /// <returns>«9311.0001» … «9311.9999» (four decimals, invariant culture).</returns>
    static string InstanceNumber(int gen) =>
        InstrNumber + "." + ((gen % 9999) + 1).ToString("0000", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Queues (Csound thread, one control cycle): compile the instrument if this performance does
    /// not have it, stop the previous preview note, start the new one and remember its start time.
    /// </summary>
    /// <returns>False only when no file is set; true once the action is queued (state Playing).</returns>
    /// <remarks>
    /// Score line: <c>i 9311.nnnn 0 -1 "path" skip dur</c>: a HELD note (Csound's negative-p1
    /// turn-off only affects held notes) that turns itself off after dur = the rest of the file +
    /// 0.1 s (at least 50 ms). Compiling this tiny instrument is cheap enough for one control cycle, so unlike
    /// <see cref="PaintLivePlayer"/> no multi-stage pipeline is needed. A compile failure is thrown on
    /// the Csound thread and reported in the console by <see cref="CsoundEngine.InvokeOnCsoundThread"/>.
    /// </remarks>
    bool Launch()
    {
        if (file is null) return false;
        int previous = State == PaintPlayState.Playing ? generation : -1;
        generation++;
        int gen = generation;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        // Forward slashes: Csound strings treat «\» as an escape character.
        // Numbers in invariant culture (decimal point, never a comma).
        // Held note (p3 = −1, so «i -9311.nnnn» can turn it off); p6 = how long it may play before
        // the instrument turns itself off (the rest of the file + 0.1 s, at least 50 ms).
        string note = $"i {InstanceNumber(gen)} 0 -1 \"{file.Replace('\\', '/')}\" {fromSeconds.ToString("0.######", ci)} {Math.Max(0.05, fileSeconds - fromSeconds + 0.1).ToString("0.###", ci)}";
        string? stopPrevious = previous >= 0 ? $"i -{InstanceNumber(previous)} 0 0" : null;
        Volatile.Write(ref noteStartScore, double.NaN);
        host.Engine.InvokeOnCsoundThread(cs =>
        {
            // A newer Play took over, or Stop cancelled this one.
            if (Volatile.Read(ref generation) != gen || Volatile.Read(ref stoppedGeneration) == gen) return;
            if (cs != compiledFor)   // first play in this performance (or a new performance)
            {
                if (CsoundEngine.CompileNow(cs, Instrument()) != 0)
                    throw new InvalidOperationException("Spectrum Room: the preview instrument did not compile (see the console).");
                compiledFor = cs;
            }
            if (stopPrevious is not null) CsoundEngine.EventNow(cs, stopPrevious);
            CsoundEngine.EventNow(cs, note);
            Volatile.Write(ref noteStartScore, CsoundEngine.ScoreTimeNow(cs));
        });
        SetState(PaintPlayState.Playing);
        return true;
    }

    /// <summary>Changes <see cref="State"/> and raises <see cref="StateChanged"/> when it really changed.</summary>
    /// <param name="s">The new state.</param>
    void SetState(PaintPlayState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke();
    }
}
