// PaintLivePlayer.cs — Paint Rooms foundation.
//
// Role: plays a SpectralImage LIVE through the app's running Csound performance, and keeps the
// sound in step with the canvas while you paint.
//
// How (Csound work runs on the Csound thread through InvokeOnCsoundThread, as a short pipeline of
// stages — one per control cycle, ~1–3 ms each — and the note starts only in the last stage, so it
// never sees half-filled tables; see Launch):
//   1. the first time in a performance, compile the Paint Synth instruments (PaintCsoundCode) —
//      unless the performance is the «Paint Rooms host» orchestra, which already contains them;
//   2. compile global code that creates a fresh, empty table set (ftgen) for this image and frees
//      the sets of older plays (ftfree) — every Play uses a new set from a ring of 40, so a
//      previous note that is still fading out keeps reading its own, untouched tables;
//   3. copy the numbers straight into the tables' memory (csoundGetTable + memcpy: ~4 MB for a
//      512×1024 stereo image, 96 columns per cycle);
//   4. set the gain/attack/release channels and start a HELD note «i 9301.nnnn 0 -1 …» whose
//      fractional instrument number identifies this play, so «i -9301.nnnn» stops exactly it
//      (with its 80 ms fade-out) without touching a newer one.
// If Csound is not playing, the host (MainViewModel) starts the «Paint Rooms host» orchestra
// (HostCsd) first; the player waits for it (Tick) and then plays.
//
// While playing, edits are coalesced and re-uploaded ~30 times per second (only the changed
// columns), the automatic level follows, and gain/attack/release move live through channels.
// Changes that alter the instrument's p-fields (mode, curve, glide, duration…) restart the note at
// the current position.
//
// Threading: every public member is for the UI thread (or one test thread). The Csound-thread
// actions only touch fields marked as written there, through Volatile.
//   * UI thread: Play / Stop / Tick / UpdateSettings / OnImageEdited, building PaintTables, and all
//     the fields in the «UI thread» block below.
//   * Csound performance thread: the lambdas queued with CsoundEngine.InvokeOnCsoundThread. They
//     run between two control cycles (never during one), so calling the Csound API there is safe;
//     each receives the CSOUND* of the running performance. They capture IMMUTABLE copies of what
//     they need (strings, arrays copied out of the tables, ints), never the UI-thread fields.
//   * Engine.Finished (performance thread, after the performance ended) resets compiledFor.
//
// Csound 7 notes: the instruments are added with csoundCompileOrc on the Csound thread
// (CsoundEngine.CompileNow), global code containing ftgen/ftfree runs at once (instr 0 semantics),
// and score events are sent with csoundEventString (CsoundEngine.EventNow). Fractional instrument
// numbers are kept for the life of the note, so «i -9301.0007» turns off only that instance and,
// because the note was held (p3 = −1), lets its linsegr release play.
//
// Used by: the Paint Synth room (through PaintSession.Player, created by MainViewModel), the Image
// Sequencer room (its OWN player, Slot 1) and PaintSelfTest.
//
// Several players at once: each player has a SLOT (constructor argument). The slot selects a
// separate ring of table sets (31000 + 16·(40·slot + gen % 40)) and a separate range of fractional
// instance numbers (slot 0: 9301.0001…9301.4999, slot 1: 9301.5001…9301.9999), so the Paint Synth
// player and the sequencer's player can sound together in the same performance without ever
// touching each other's tables or notes. HostInUseByOthers keeps a player from stopping the «Paint
// Rooms host» orchestra while another player still sounds through it.

using CsoundVisual.Engine;

namespace CsoundVisual.Paint;

/// <summary>What the live player needs from the application (implemented by MainViewModel; by a small stand-in in tests).</summary>
/// <remarks>
/// Shared by <see cref="PaintLivePlayer"/>, <see cref="SoundPreviewPlayer"/> and the Image Filter
/// player: all Paint Rooms sound goes through the app's single <see cref="CsoundEngine"/>, either
/// into the user's running document or into the small host orchestra. All members are called on
/// the UI thread.
/// </remarks>
public interface IPaintEngineHost
{
    /// <summary>The app's Csound engine.</summary>
    CsoundEngine Engine { get; }

    /// <summary>True while the engine performs in real time.</summary>
    bool IsPlayingLive { get; }

    /// <summary>True while the engine is busy rendering a document to a file (nothing can play then).</summary>
    bool IsRenderingFile { get; }

    /// <summary>
    /// Starts the engine on the «Paint Rooms host» orchestra (<see cref="PaintLivePlayer.HostCsd"/>)
    /// when it is stopped. Returns false when that is not possible now.
    /// </summary>
    bool StartPaintHost();

    /// <summary>True when the running performance is the host started by <see cref="StartPaintHost"/>.</summary>
    bool IsPaintHostRunning { get; }

    /// <summary>Stops the engine if (and only if) it is running the Paint Rooms host.</summary>
    void StopPaintHost();
}

/// <summary>State of the <see cref="PaintLivePlayer"/>.</summary>
public enum PaintPlayState
{
    /// <summary>Nothing playing.</summary>
    Idle,
    /// <summary>Waiting for Csound to start (the host orchestra was launched).</summary>
    Starting,
    /// <summary>An image note is playing.</summary>
    Playing,
}

/// <summary>Plays images live through the app's Csound engine (see the file header).</summary>
public sealed class PaintLivePlayer
{
    /// <summary>Number of table sets in the ring (each Play takes the next one).</summary>
    /// <remarks>
    /// Table numbers (slot 0) 31000 … 31000 + 16·40 − 1 = 31639; slot 1 uses the next 640 (31640 … 32279). Only the current and the previous set are
    /// kept alive (older ones are freed with <c>ftfree</c> by the next Launch), so the ring size
    /// only has to be &gt; 2; 40 makes a reused number very unlikely to meet a stale table.
    /// </remarks>
    const int SetRing = 40;

    /// <summary>Number of player slots (see the file header): each has its own table ring and instance range.</summary>
    public const int Slots = 2;

    /// <summary>The application (engine access, starting/stopping the host orchestra).</summary>
    readonly IPaintEngineHost host;

    /// <summary>This player's slot 0…<see cref="Slots"/>−1 (table ring and instance-number range).</summary>
    readonly int slot;

    // ---- written on the UI thread

    /// <summary>The image being played (the session's canvas object; never copied).</summary>
    SpectralImage? image;

    /// <summary>The tables of the current play (built by <see cref="Launch"/>, updated by <see cref="FlushEdits"/>).</summary>
    PaintTables? tables;

    /// <summary>Current synthesis settings (see <see cref="UpdateSettings"/>).</summary>
    PaintSynthSettings settings = new();

    /// <summary>Column (fractional) where the current note started; the base of <see cref="CurrentColumn"/>.</summary>
    double startColumn;

    /// <summary>
    /// Play counter: incremented by every <see cref="Launch"/>. It selects the table set (gen % 40) and
    /// the fractional instance number, and lets a queued pipeline notice that a newer Launch took
    /// over (read on the Csound thread with <see cref="Volatile"/>).
    /// </summary>
    int generation;

    /// <summary>
    /// The <see cref="generation"/> that <see cref="Stop"/> cancelled (−1 = none). Queued pipeline and
    /// upload stages of that play check it on the Csound thread and do nothing, so a Play→Stop made
    /// while the tables are still being uploaded can never start a held note afterwards (see
    /// <see cref="Stop"/>). Written on the UI thread with <see cref="Volatile"/>, read on the Csound thread.
    /// </summary>
    int stoppedGeneration = -1;

    /// <summary>Coalesced dirty column range [dirtyX0, dirtyX1) since the last upload; empty when X1 ≤ X0.</summary>
    int dirtyX0 = int.MaxValue, dirtyX1 = -1;

    /// <summary>UTC time <see cref="Play"/> launched the host orchestra (start-up timeout in <see cref="Tick"/>).</summary>
    DateTime startingSince;

    /// <summary>True when THIS player started the host orchestra, so it stops it again when it goes idle.</summary>
    bool stopHostWhenIdle;

    // ---- written on the Csound thread (inside actions), read on the UI thread

    /// <summary>CSOUND* the instruments were compiled into (<see cref="IntPtr.Zero"/> = none yet / performance ended).</summary>
    IntPtr compiledFor;                  // CSOUND* the instruments were compiled into

    /// <summary>Bases of the table sets currently allocated in <see cref="compiledFor"/> (Csound thread only).</summary>
    readonly List<int> liveSets = [];    // table set bases allocated in compiledFor (Csound thread only)

    /// <summary>Score time (s) at which the current note started; NaN until the last pipeline stage ran.</summary>
    double noteStartScore = double.NaN;  // score time at which the current note started

    /// <summary>Creates a player for <paramref name="host"/>.</summary>
    /// <param name="host">The application side; its <see cref="IPaintEngineHost.Engine"/> is subscribed to for the player's lifetime.</param>
    /// <param name="slot">0 for Paint Synth's player, 1 for the Image Sequencer's (clamped to 0…<see cref="Slots"/>−1).
    /// Two players that may sound at the same time must use different slots.</param>
    public PaintLivePlayer(IPaintEngineHost host, int slot = 0)
    {
        this.host = host;
        this.slot = Math.Clamp(slot, 0, Slots - 1);
        // When a performance ends its CSOUND* is destroyed and the next one may get the same
        // address: forget what was compiled so the instruments are compiled again (performance thread).
        host.Engine.Finished += _ =>
        {
            compiledFor = IntPtr.Zero;
        };
    }

    /// <summary>Detaches from the engine (call when the window that owns the player closes).</summary>
    /// <remarks>Currently just <see cref="Stop"/>: the <c>Finished</c> subscription stays (it is harmless).</remarks>
    public void Detach() => Stop();

    /// <summary>Current state.</summary>
    public PaintPlayState State { get; private set; }

    /// <summary>The settings in use (set with <see cref="UpdateSettings"/>).</summary>
    public PaintSynthSettings Settings => settings;

    /// <summary>The tables of the current note (for status displays), or <c>null</c>.</summary>
    public PaintTables? Tables => tables;

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    public event Action? StateChanged;

    /// <summary>Raised with a message for the status line (text, is-warning).</summary>
    public event Action<string, bool>? Message;

    /// <summary>
    /// Optional: returns true while something else (another room's player) still plays through the
    /// «Paint Rooms host»; then this player going idle does not stop the host.
    /// </summary>
    public Func<bool>? HostInUseByOthers { get; set; }

    /// <summary>True when this player may stop the host orchestra now (it started it and nobody else needs it).</summary>
    bool MayStopHost => stopHostWhenIdle && host.IsPaintHostRunning && !(HostInUseByOthers?.Invoke() ?? false);

    /// <summary>
    /// The minimal orchestra the app starts when Paint Rooms plays while Csound is stopped: the
    /// Paint Synth instruments 9301/9302 (already compiled, so nothing has to be compiled while
    /// audio runs) and a score that keeps it alive.
    /// </summary>
    /// <param name="ksmps">Control period in samples (32 at 48 kHz = 0.67 ms per control cycle).</param>
    /// <returns>A complete .csd (no &lt;CsOptions&gt;: the caller passes the output flags).</returns>
    /// <remarks>
    /// <see cref="Launch"/> skips compiling 9301/9302 when <see cref="IPaintEngineHost.IsPaintHostRunning"/>.
    /// Other players (e.g. <see cref="SoundPreviewPlayer"/>) compile their own instruments into it.
    /// <c>f 0 36000</c> keeps the performance alive for 10 hours without any note. sr is fixed at
    /// 48 kHz; the tables are always built for the engine's actual <c>SampleRate</c>/<c>Ksmps</c>.
    /// </remarks>
    public static string HostCsd(int ksmps = 32) =>
        "<CsoundSynthesizer>\n<CsInstruments>\n" +
        "; Paint Rooms host: keeps Csound running while Paint Rooms plays images through it.\n" +
        "; It already contains the Paint Synth instruments (9301/9302), so nothing has to be\n" +
        "; compiled while audio runs; the image tables are created and filled live.\n" +
        "sr = 48000\nksmps = " + ksmps + "\nnchnls = 2\n0dbfs = 1\n\n" + PaintCsoundCode.Instruments() +
        "\n</CsInstruments>\n<CsScore>\nf 0 36000   ; stay alive for up to 10 hours\n</CsScore>\n</CsoundSynthesizer>\n";

    /// <summary>
    /// Plays <paramref name="img"/> from column <paramref name="fromColumn"/>, restarting if
    /// something is already playing. Starts Csound (host orchestra) when it is stopped.
    /// </summary>
    /// <param name="img">The image (kept by reference: later edits are reported with <see cref="OnImageEdited"/>).</param>
    /// <param name="synth">Synthesis settings.</param>
    /// <param name="fromColumn">Start column (fractional; clamped to the image).</param>
    /// <returns>False when it cannot play (rendering, Csound unavailable, bad sample…); <see cref="Message"/> says why.</returns>
    /// <remarks>
    /// Two paths: Csound already performing → <see cref="Launch"/> now (state Playing at once, the
    /// sound starts a few control cycles later); Csound stopped → start the host, go to Starting, and
    /// let <see cref="Tick"/> launch once the engine reports a sample rate.
    /// </remarks>
    public bool Play(SpectralImage img, PaintSynthSettings synth, double fromColumn)
    {
        // A file render owns the engine: nothing can be compiled into it.
        if (host.IsRenderingFile)
        {
            Message?.Invoke(Localization.Loc.S("Csound está renderizando un documento a WAV; espera a que termine.",
                                               "Csound is rendering a document to WAV; wait until it finishes."), true);
            return false;
        }
        image = img;
        settings = synth;
        startColumn = Math.Clamp(fromColumn, 0, img.Width - 1);
        if (host.IsPlayingLive && host.Engine.SampleRate > 0)
            return Launch();
        if (!host.StartPaintHost())
        {
            Message?.Invoke(Localization.Loc.S("No se pudo iniciar Csound (revisa la consola de la ventana principal).",
                                               "Csound could not be started (check the console in the main window)."), true);
            return false;
        }
        // We started the host, so we also stop it when the image stops (see Stop / Tick).
        stopHostWhenIdle = true;
        startingSince = DateTime.UtcNow;
        SetState(PaintPlayState.Starting);
        Message?.Invoke(Localization.Loc.S("Iniciando Csound…", "Starting Csound…"), false);
        return true;
    }

    /// <summary>Stops the image (with its fade-out). Stops Csound too when it was started only for Paint Rooms.</summary>
    /// <remarks>
    /// Race-free with an upload still in flight (the first ~15 control cycles after
    /// <see cref="Launch"/>): the current play number is first marked as cancelled
    /// (<see cref="stoppedGeneration"/>), so every stage that has not run yet returns at once and the
    /// note is never started; THEN the turn-off event is queued. Both run on the single Csound
    /// thread in queue order, so either the last stage already started the note (and the turn-off
    /// queued after it stops it) or the stage sees the mark and does nothing — there is no ordering
    /// in which a held note (p3 = −1) survives the Stop. (Before this fix the pipeline kept going
    /// and started a note that nothing would ever turn off, while the UI said «stopped».)
    /// </remarks>
    public void Stop()
    {
        if (State != PaintPlayState.Idle)
            Volatile.Write(ref stoppedGeneration, generation);
        if (State == PaintPlayState.Playing)
        {
            // Negative fractional instrument = turn off exactly this instance, with release.
            string off = "i -" + InstanceNumber(generation) + " 0 0";
            host.Engine.InvokeOnCsoundThread(cs => CsoundEngine.EventNow(cs, off));
        }
        if (State != PaintPlayState.Idle && MayStopHost)
            host.StopPaintHost();
        stopHostWhenIdle = false;
        SetState(PaintPlayState.Idle);
    }

    /// <summary>
    /// Column sounding now (fractional), or <c>null</c> when not playing. Accounts for the output
    /// latency so the cursor matches what you hear.
    /// </summary>
    public double? CurrentColumn
    {
        get
        {
            if (State != PaintPlayState.Playing || tables is null || image is null) return null;
            double start = Volatile.Read(ref noteStartScore);
            if (double.IsNaN(start)) return startColumn; // note not started yet (pipeline still uploading)
            // Seconds of the note already HEARD: score time runs ahead of the speakers by the
            // output latency (WASAPI buffer), so subtract it. Then seconds → columns (W / pass s).
            double t = host.Engine.ScoreTime - start - host.Engine.OutputLatencySeconds;
            double col = startColumn + Math.Max(0, t) * tables.Width / tables.PassSeconds;
            if (settings.Loop) col %= tables.Width;
            return col;
        }
    }

    /// <summary>
    /// Call ~30 times per second on the UI thread: finishes a pending start, uploads coalesced
    /// edits, and notices when the note ended or Csound stopped.
    /// </summary>
    public void Tick()
    {
        switch (State)
        {
            case PaintPlayState.Starting:
                // Ready = performing, sr known, and at least 20 ms of score time elapsed (the
                // performance loop really runs, so queued Csound-thread actions will execute).
                if (host.IsPlayingLive && host.Engine.SampleRate > 0 && host.Engine.ScoreTime > 0.02)
                    Launch();
                // Give up after 10 s, or after 1.5 s if the engine is not even performing (it
                // failed to compile or to open the device: the console says why).
                else if (DateTime.UtcNow - startingSince > TimeSpan.FromSeconds(10) ||
                         (!host.IsPlayingLive && DateTime.UtcNow - startingSince > TimeSpan.FromSeconds(1.5)))
                {
                    Message?.Invoke(Localization.Loc.S("Csound no pudo empezar a tocar: revisa la consola de la ventana principal.",
                                                       "Csound could not start playing: check the console in the main window."), true);
                    SetState(PaintPlayState.Idle);
                }
                break;
            case PaintPlayState.Playing:
                // Csound stopped under us (main Stop button, error, the document ended).
                if (!host.IsPlayingLive || host.Engine.SampleRate <= 0)
                {
                    SetState(PaintPlayState.Idle);
                    break;
                }
                FlushEdits();
                // A one-shot note ends by itself after the last column plus the release tail.
                // Same tail as the instrument (6 release time constants) plus 0.25 s, converted to
                // columns; the check is in columns because CurrentColumn is not wrapped when not looping.
                if (!settings.Loop && tables is not null && CurrentColumn is { } c &&
                    c > tables.Width + (settings.ReleaseMs / 1000 * 6 + 0.25) * tables.Width / tables.PassSeconds)
                {
                    if (MayStopHost) host.StopPaintHost();
                    stopHostWhenIdle = false;
                    SetState(PaintPlayState.Idle);
                }
                break;
        }
    }

    /// <summary>
    /// Tells the player that columns [x0, x1) of <paramref name="img"/> changed (call from the
    /// image's <see cref="SpectralImage.Changed"/> event). Uploaded at the next <see cref="Tick"/>.
    /// </summary>
    /// <param name="img">The edited image; ignored unless it is the one playing.</param>
    /// <param name="x0">First changed column.</param>
    /// <param name="x1">One past the last changed column.</param>
    /// <remarks>Only widens the dirty range (cheap; called for every brush dab). Many edits per tick
    /// coalesce into one upload of their union.</remarks>
    public void OnImageEdited(SpectralImage img, int x0, int x1)
    {
        if (State != PaintPlayState.Playing || !ReferenceEquals(img, image)) return;
        dirtyX0 = Math.Min(dirtyX0, x0);
        dirtyX1 = Math.Max(dirtyX1, x1);
    }

    /// <summary>
    /// New synthesis settings. Gain, auto level, attack and release change live; anything else
    /// restarts the note at the current position.
    /// </summary>
    /// <param name="synth">The new settings (stored even when nothing is playing).</param>
    public void UpdateSettings(PaintSynthSettings synth)
    {
        var old = settings;
        settings = synth;
        if (State != PaintPlayState.Playing || tables is null || image is null) return;
        // Record trick: copy the OLD settings with the four live fields taken from the new ones; if
        // that equals the new settings, nothing else changed and no restart is needed.
        bool liveOnly = old with { GainDb = synth.GainDb, AutoLevel = synth.AutoLevel, AttackMs = synth.AttackMs, ReleaseMs = synth.ReleaseMs } == synth;
        if (liveOnly)
        {
            tables.UpdateGain(synth);
            PushChannels();
        }
        else
            Restart();
    }

    /// <summary>Restarts the current image at the current position (e.g. after the duration or the image object changed).</summary>
    /// <param name="newImage">A replacement image object (e.g. after a resize), or <c>null</c> to keep the current one.</param>
    /// <remarks>Does nothing unless playing. The new note fades in over 10 ms while the old one
    /// fades out over 80 ms, so a restart is a short cross-fade rather than a click.</remarks>
    public void Restart(SpectralImage? newImage = null)
    {
        if (State != PaintPlayState.Playing || image is null) return;
        double col = CurrentColumn ?? startColumn;
        if (newImage is not null)
        {
            // Keep the same RELATIVE position when the width changed.
            col = col / Math.Max(1, image.Width) * newImage.Width;
            image = newImage;
        }
        startColumn = Math.Clamp(settings.Loop ? col % image.Width : col, 0, image.Width - 1);
        Launch();
    }

    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The fractional instrument number of play <paramref name="gen"/>: 9301.0001 … 9301.4999 for
    /// slot 0, 9301.5001 … 9301.9999 for slot 1.
    /// </summary>
    /// <param name="gen">Play counter (<see cref="generation"/>).</param>
    /// <returns>Text for a score line; always four decimals so Csound does not round two plays to the same instance.</returns>
    string InstanceNumber(int gen) =>
        PaintCsoundCode.InstrNumber + "." + (slot * 5000 + (gen % 4999) + 1).ToString("0000", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>First table number of the set used by play <paramref name="gen"/> (this slot's ring of <see cref="SetRing"/> sets).</summary>
    /// <param name="gen">Play counter.</param>
    /// <returns>31000 + 16·(40·slot + gen mod 40): slot 0 uses 31000…31639, slot 1 31640…32279.</returns>
    int SetBase(int gen) => PaintCsoundCode.FirstTable + PaintCsoundCode.TablesPerSet * (SetRing * slot + gen % SetRing);

    /// <summary>Columns copied into Csound per control cycle while a new image is uploaded.</summary>
    /// <remarks>96 columns × 512 rows × 2 layers ≈ 98 000 doubles (≈ 0.8 MB) per cycle; a 1024-column
    /// image needs 11 cycles.</remarks>
    const int ColumnsPerCycle = 96;

    /// <summary>
    /// Builds the tables (UI thread) and queues the upload as a short PIPELINE of Csound-thread
    /// stages, one per control cycle, so no single cycle does more than ~1–3 ms of extra work
    /// (measured: compiling the instruments takes ≈ 9 ms and creating + filling a 512×1024 stereo
    /// set ≈ 7 ms; done in one cycle that is a dropout with 10 ms periods). Stages:
    /// [compile instruments, only when not already in the performance] → stop the previous note,
    /// free old sets, create the small tables and copy their data → create the left image table →
    /// create the right one → copy <see cref="ColumnsPerCycle"/> columns per cycle → set the
    /// channels and start the note. A newer Launch (or another performance) aborts an unfinished
    /// pipeline.
    /// </summary>
    /// <returns>False when the tables could not be built (e.g. sample-loop mode without a readable
    /// sample); <see cref="Message"/> then says why. True once the pipeline is queued.</returns>
    /// <remarks>
    /// Exceptions thrown by a stage surface on the Csound thread through
    /// <see cref="CsoundEngine.InvokeOnCsoundThread"/> (reported in the console); the pipeline then
    /// simply stops, and this player stays in Playing until the next Stop/Play.
    /// Everything the stages need is captured as locals here (strings, the tables object, ints), so
    /// later UI-thread changes to the fields cannot leak into an upload in flight.
    /// </remarks>
    bool Launch()
    {
        if (image is null) return false;
        var engine = host.Engine;
        PaintTables t;
        try
        {
            // Built for the RUNNING orchestra's sr and ksmps (the noise coefficients and the
            // Nyquist muting depend on them). ≈ 2–5 ms for a 512 × 1024 image, on the UI thread.
            t = PaintTables.Build(image, settings, engine.SampleRate, Math.Max(1, engine.Ksmps));
        }
        catch (Exception ex)
        {
            Message?.Invoke(ex.Message, true);
            if (State == PaintPlayState.Starting) SetState(PaintPlayState.Idle);
            return false;
        }
        // The note to stop (only if one is playing), then a new play number and its table set.
        int previous = State == PaintPlayState.Playing ? generation : -1;
        generation++;
        int gen = generation;
        int setBase = SetBase(gen);
        const int imgL = PaintCsoundCode.TableMap.ImageL, imgR = PaintCsoundCode.TableMap.ImageR, imgN = PaintCsoundCode.TableMap.ImageN;
        // Pre-render all code and score text on the UI thread: the Csound-thread stages only
        // execute it. The two big image ftgens are split off so each gets its own control cycle.
        var lines = PaintCsoundCode.LiveTableLines(t, setBase);
        // The big tables (left, right and — for a picture with a noise layer — the noisiness) each get
        // their own control cycle; the placeholder noisiness table of a tonal picture is small.
        bool mixed = t.Mixed;
        string smallTables = PaintCsoundCode.ChannelInit(t) + string.Concat(lines.Where(l => !(l.Offset is imgL or imgR || (mixed && l.Offset == imgN))).Select(l => l.Code));
        string imageLCode = lines.First(l => l.Offset == imgL).Code;
        string imageRCode = lines.First(l => l.Offset == imgR).Code;
        string imageNCode = lines.First(l => l.Offset == imgN).Code;
        // Held note (p3 = −1): it lasts until «i -9301.nnnn» or until the instrument turns itself
        // off (one-shot plays, see the instrument's last section).
        string note = $"i {InstanceNumber(gen)} 0 -1 {PaintCsoundCode.Fields(t, setBase, startColumn, settings.Loop)}";
        string? stopPrevious = previous >= 0 ? $"i -{InstanceNumber(previous)} 0 0" : null;
        var smallData = PaintCsoundCode.LiveData(t).Where(d => d.Offset is not (imgL or imgR or imgN)).ToList();
        bool instrumentsInHost = host.IsPaintHostRunning; // the host orchestra already contains them
        double gain = t.OutputGain, attack = t.Settings.AttackMs / 1000, release = t.Settings.ReleaseMs / 1000; // linear, s, s
        tables = t;
        // The new tables already contain every edit made so far.
        dirtyX0 = int.MaxValue; dirtyX1 = -1;
        // CurrentColumn shows startColumn until the last stage records the real start time.
        Volatile.Write(ref noteStartScore, double.NaN);

        // ---- the stages (each runs on the Csound thread, one per control cycle)
        var stages = new List<Action<IntPtr>>
        {
            // Stage 0: make sure this performance has instruments 9301/9302 (≈ 9 ms when compiling).
            cs =>
            {
                if (cs == compiledFor) return;
                // A new performance: compile the instruments (unless the host orchestra has them)
                // and forget the table sets of the previous performance.
                if (!instrumentsInHost && CsoundEngine.CompileNow(cs, PaintCsoundCode.Instruments()) != 0)
                    throw new InvalidOperationException("Paint Rooms: the instrument did not compile (see the console).");
                compiledFor = cs;
                liveSets.Clear();
            },
            // Stage 1: stop the previous note (its 80 ms release starts), free older table sets,
            // create this set's small tables (+ the channel init) and fill the C#-computed ones.
            cs =>
            {
                if (stopPrevious is not null)
                    CsoundEngine.EventNow(cs, stopPrevious);
                // Free the sets of older plays, always keeping the previous one: its note may still
                // be fading out (it was just stopped, here or by Stop() a moment ago).
                var free = new System.Text.StringBuilder();
                int keepPrev = SetBase(gen - 1);
                foreach (int b in liveSets.ToList()) // ToList: the list is modified in the loop
                {
                    if (b == keepPrev) continue;
                    // ftfree only tables that exist (an aborted pipeline may have left a set
                    // half-created); «ftfree n, 0» frees at once (0 = at init time).
                    for (int k = 0; k < PaintCsoundCode.TablesPerSet; k++)
                        if (CsoundEngine.TableLength(cs, b + k) > 0)
                            free.Append("ftfree ").Append(b + k).Append(", 0\n");
                    liveSets.Remove(b);
                }
                if (free.Length > 0) CsoundEngine.CompileNow(cs, free.ToString());
                if (CsoundEngine.CompileNow(cs, smallTables) != 0)
                    throw new InvalidOperationException("Paint Rooms: the tables could not be created (out of memory?).");
                liveSets.Add(setBase);
                foreach (var (offset, values) in smallData)
                    CsoundEngine.CopyToTable(cs, setBase + offset, values);
            },
            // Stages 2 and 3: allocate the two big (zero-filled) image tables, one per cycle.
            cs => { if (CsoundEngine.CompileNow(cs, imageLCode) != 0) throw new InvalidOperationException("Paint Rooms: out of memory for the image."); },
            cs => { if (CsoundEngine.CompileNow(cs, imageRCode) != 0) throw new InvalidOperationException("Paint Rooms: out of memory for the image."); },
        };
        if (mixed)
            stages.Add(cs => { if (CsoundEngine.CompileNow(cs, imageNCode) != 0) throw new InvalidOperationException("Paint Rooms: out of memory for the noise layer."); });
        // Stages 4…: copy the image, ColumnsPerCycle columns of both layers per control cycle,
        // straight into the tables' memory (column-major, so a column range is one contiguous span).
        int h = t.Height;
        for (int x0 = 0; x0 < t.Width; x0 += ColumnsPerCycle)
        {
            // The extra silent column at the end is already zero after ftgen.
            // a, n: per-iteration copies, so each lambda captures its own column range.
            int a = x0, n = Math.Min(ColumnsPerCycle, t.Width - x0);
            stages.Add(cs =>
            {
                CsoundEngine.CopyToTable(cs, setBase + imgL, t.ImageL.AsSpan(a * h, n * h), a * h);
                CsoundEngine.CopyToTable(cs, setBase + imgR, t.ImageR.AsSpan(a * h, n * h), a * h);
                if (mixed) CsoundEngine.CopyToTable(cs, setBase + imgN, t.ImageN.AsSpan(a * h, n * h), a * h);
            });
        }
        // Last stage: channels, then the note — only now are all its tables complete. Recording
        // the start score time here makes CurrentColumn follow this note.
        stages.Add(cs =>
        {
            CsoundEngine.ControlNow(cs, PaintCsoundCode.GainChannel, gain);
            CsoundEngine.ControlNow(cs, PaintCsoundCode.AttackChannel, attack);
            CsoundEngine.ControlNow(cs, PaintCsoundCode.ReleaseChannel, release);
            CsoundEngine.EventNow(cs, note);
            Volatile.Write(ref noteStartScore, CsoundEngine.ScoreTimeNow(cs));
        });

        // Runner: executes stage `next`, then queues itself again; an action queued by an action
        // runs at the NEXT control cycle (see CsoundEngine.InvokeOnCsoundThread).
        // pipelineCs / next are captured by the local function and only touched on the Csound thread.
        IntPtr pipelineCs = IntPtr.Zero;
        int next = 0;
        void Runner(IntPtr cs)
        {
            // A newer Launch took over, or Stop cancelled this play: drop the remaining stages.
            if (Volatile.Read(ref generation) != gen || Volatile.Read(ref stoppedGeneration) == gen) return;
            if (next == 0) pipelineCs = cs;
            else if (cs != pipelineCs) return;                   // the performance changed meanwhile
            stages[next++](cs);
            if (next < stages.Count) engine.InvokeOnCsoundThread(Runner);
        }
        engine.InvokeOnCsoundThread(Runner);

        // Playing from the UI's point of view as soon as the pipeline is queued (the sound follows
        // ≈ 15 control cycles later, ~10 ms at ksmps 32 / 48 kHz). The status shows the total gain in dB.
        SetState(PaintPlayState.Playing);
        Message?.Invoke(Localization.Loc.S(
            $"▶ {t.Width}×{t.Height}, {t.Settings.Mode}, {(t.Stereo ? "estéreo" : "mono")} · nivel automático {20 * Math.Log10(Math.Max(1e-9, t.OutputGain)):+0.0;-0.0} dB",
            $"▶ {t.Width}×{t.Height}, {t.Settings.Mode}, {(t.Stereo ? "stereo" : "mono")} · gain {20 * Math.Log10(Math.Max(1e-9, t.OutputGain)):+0.0;-0.0} dB"), false);
        return true;
    }

    /// <summary>Uploads the columns edited since the last tick (UI thread → one Csound-thread copy).</summary>
    /// <remarks>
    /// Also re-estimates the automatic level (the peak may have changed) and sends the new gain with
    /// the data, so the level follows the painting. Restarts instead when the image changed size or
    /// became stereo while a mono note (one bank) plays. If the edit arrives while the upload
    /// pipeline is still running, the copy goes to tables that may not exist yet (a harmless no-op
    /// in <see cref="CsoundEngine.CopyToTable"/>) — the pipeline's later column stages read the
    /// already-updated arrays anyway.
    /// The upload is addressed by PLAY, not by note: it only runs if its play number is still the
    /// current one and was not stopped, and only in the performance that holds this player's tables
    /// (<see cref="compiledFor"/>). An earlier version compared with the CSOUND* of the last note
    /// that STARTED, which is still unset (or stale) while the first upload pipeline of a new
    /// performance runs: edits made during those cycles to already-uploaded columns were dropped.
    /// </remarks>
    void FlushEdits()
    {
        if (dirtyX1 <= dirtyX0 || tables is not { } t || image is null) return;
        int x0 = Math.Max(0, dirtyX0), x1 = Math.Min(t.Width, dirtyX1);
        dirtyX0 = int.MaxValue; dirtyX1 = -1;
        if (image.Width != t.Width || image.Height != t.Height) { Restart(); return; }
        // Level → amplitude for the dirty columns only (≈ 2 ms for the whole image, far less here).
        t.UpdateColumns(image, x0, x1);
        bool nowStereo = image.IsStereo();
        if (nowStereo && !t.Stereo)
        {
            // The note runs one bank for both sides: it must restart with two.
            Restart();
            return;
        }
        if (!t.Mixed && t.Settings.Mode == SynthMode.Sines && image.HasNoise())
        {
            // A tonal note has no noise banks: the first noisy cell needs a note with the mix on.
            Restart();
            return;
        }
        // (Noisy → all tonal again keeps the mixed note: correct, the noise banks just play silence.)
        // (Stereo → mono is not handled: the two banks keep running, which is correct, just costlier.)
        t.UpdateGain(settings);
        int h = t.Height, setBase = SetBase(generation);
        // Copy the changed span out now: the UI thread may edit the arrays again before Csound runs.
        var l = t.ImageL.AsSpan(x0 * h, (x1 - x0) * h).ToArray();
        var r = t.ImageR.AsSpan(x0 * h, (x1 - x0) * h).ToArray();
        var nz = t.Mixed ? t.ImageN.AsSpan(x0 * h, (x1 - x0) * h).ToArray() : null;
        double gain = t.OutputGain;
        int gen = generation;
        host.Engine.InvokeOnCsoundThread(cs =>
        {
            // A newer play, a Stop, or another performance: those tables are not this play's.
            if (Volatile.Read(ref generation) != gen || Volatile.Read(ref stoppedGeneration) == gen || cs != compiledFor) return;
            CsoundEngine.CopyToTable(cs, setBase + PaintCsoundCode.TableMap.ImageL, l, x0 * h);
            CsoundEngine.CopyToTable(cs, setBase + PaintCsoundCode.TableMap.ImageR, r, x0 * h);
            if (nz is not null) CsoundEngine.CopyToTable(cs, setBase + PaintCsoundCode.TableMap.ImageN, nz, x0 * h);
            CsoundEngine.ControlNow(cs, PaintCsoundCode.GainChannel, gain);
        });
    }

    /// <summary>Sends gain/attack/release to their channels.</summary>
    /// <remarks>Through the engine's thread-safe <see cref="CsoundEngine.SetControl"/> (UI thread);
    /// times are converted from ms to the seconds the instrument expects.</remarks>
    void PushChannels()
    {
        if (tables is null) return;
        var e = host.Engine;
        e.SetControl(PaintCsoundCode.GainChannel, tables.OutputGain);
        e.SetControl(PaintCsoundCode.AttackChannel, settings.AttackMs / 1000);
        e.SetControl(PaintCsoundCode.ReleaseChannel, settings.ReleaseMs / 1000);
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
