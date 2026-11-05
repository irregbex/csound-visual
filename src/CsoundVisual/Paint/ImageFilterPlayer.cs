// ImageFilterPlayer.cs — Paint Rooms · «Image Filter»: plays the filter LIVE through the app's Csound.
//
// Role: runs the Image Filter instrument (ImageFilterCode, instr 9351) inside the app's running
// Csound performance — on a sound file or on the live audio input — and keeps the sound in step
// with the canvas while you paint and turn the knobs. Modelled on PaintLivePlayer (same host,
// same staged upload), see that file for the background.
//
// How (all Csound work runs on the Csound performance thread through
// CsoundEngine.InvokeOnCsoundThread, as a PIPELINE of short stages, one per control cycle, so no
// cycle does more than a millisecond or two of extra work and the note never sees half-filled
// tables):
//   1. the first time in a performance, compile instr 9351/9352 (≈ a few ms, once);
//   2. stop the previous filter note, free the table sets of older plays (a ring of 16 sets, the
//      previous one is kept because its note may still be fading out), create the small tables
//      (two EQ curves + a scratch column) and set the live channels;
//   3. create the left gain table, then (next cycle) the right one — ftgen of a 4–17 MB table;
//   4. copy the gain data straight into the tables' memory (csoundGetTable + memcpy), at most
//      CopyValuesPerCycle values per table per cycle;
//   5. start a HELD note «i 9351.nnnn 0 -1 …» whose fractional number identifies this play, so
//      «i -9351.nnnn» stops exactly it (with its 80 ms fade-out).
// If Csound is not playing, the host (MainViewModel) starts the «Paint Rooms host» orchestra first
// (it contains no filter instrument: stage 1 compiles it) and the player waits for it in Tick.
//
// While playing: painting is coalesced and re-uploaded ~30 times per second (only the changed gain
// columns); mix, output gain and smoothing move live through channels; a change of the level →
// gain rule re-uploads every column; anything structural (FFT size, overlap, time mapping, speed,
// glide, source looping) restarts the note at the current position.
//
// Threading: every public member is for the UI thread (or one test thread). The Csound-thread
// actions only touch fields documented as written there, through Volatile where read back.
// Values the actions need from the UI side (note text, table data, channel values) are captured
// into locals when the pipeline is built, so a later UI change cannot tear what Csound sees.
//
// Users: ImageFilterRoom (the room's Play/Stop, timer Tick, paint notifications, knob changes)
// and ImageFilterSelfTest (--filter-test). Offline renders do NOT go through this class: they use
// ImageFilterRenderer with a separate Csound instance.

using CsoundVisual.Engine;
using CsoundVisual.Localization;

namespace CsoundVisual.Paint;

/// <summary>
/// What the filter plays: a stereo WAV prepared for diskin2 (<see cref="Prepare"/>) or the live
/// input. Immutable.
/// </summary>
/// <param name="LiveInput">True = the app engine's live input (inch 1/2).</param>
/// <param name="WavPath">Stereo float WAV read by diskin2 (file sources).</param>
/// <param name="Seconds">Length of the file (0 for live input).</param>
/// <param name="SampleRate">Sample rate of the file (0 for live input).</param>
/// <param name="Audio">The decoded sound (file sources; used for renders and the waveform).</param>
/// <param name="Name">Display name (file name, «shared sound», «live input»).</param>
public sealed record FilterSource(bool LiveInput, string? WavPath, double Seconds, int SampleRate, AudioData? Audio, string Name)
{
    /// <summary>The live input source.</summary>
    public static FilterSource Live => new(true, null, 0, 0, null, Loc.S("entrada en vivo", "live input"));

    /// <summary>Folder for the stereo copies handed to diskin2 (%TEMP%\csoundvisual_filter_src).</summary>
    public static string TempFolder => Path.Combine(Path.GetTempPath(), "csoundvisual_filter_src");

    /// <summary>
    /// Writes <paramref name="audio"/> as a stereo 32-bit float WAV into <see cref="TempFolder"/>
    /// (mono → both sides, more than two channels → the first two) and returns the source. The
    /// instrument always reads a stereo file, so the picture's left/right layers can pan any source.
    /// Copies older than a day are deleted on the way (left over from earlier sessions).
    /// </summary>
    /// <param name="audio">The decoded sound (any channel count, any sample rate).</param>
    /// <param name="name">Display name for the status line.</param>
    /// <returns>A file source pointing at the new temporary WAV (unique name per call).</returns>
    /// <remarks>
    /// The copy is written at the sound's own sample rate; diskin2 converts it to the engine's
    /// rate if they differ. Blocking file I/O: the room calls it through <c>Task.Run</c> for
    /// opened files, but synchronously for the shared sound (ImageFilterRoom.CurrentSource).
    /// </remarks>
    public static FilterSource Prepare(AudioData audio, string name)
    {
        Directory.CreateDirectory(TempFolder);
        foreach (var old in Directory.EnumerateFiles(TempFolder, "*.wav"))
        {
            try { if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1)) File.Delete(old); }
            catch { /* in use by another app instance: leave it */ }
        }
        string path = Path.Combine(TempFolder, "source_" + Guid.NewGuid().ToString("N")[..12] + ".wav");
        WavIO.WriteFloat32(path, ImageFilterCode.StereoOf(audio), audio.SampleRate);
        return new FilterSource(false, path, audio.Seconds, audio.SampleRate, audio, name);
    }
}

/// <summary>Plays the Image Filter live through the app's Csound engine (see the file header).</summary>
public sealed class ImageFilterPlayer
{
    /// <summary>Number of table sets in the ring (each Play takes the next one).</summary>
    const int SetRing = 16;

    /// <summary>Values copied into one gain table per control cycle during an upload (≈ 0.8 MB, well under a millisecond).</summary>
    const int CopyValuesPerCycle = 100_000;

    /// <summary>The app (or test) side that owns the Csound engine and can start/stop the «Paint Rooms host».</summary>
    readonly IPaintEngineHost host;

    // ---- written on the UI thread
    /// <summary>The picture being played (the room's canvas image; edits arrive via <see cref="OnImageEdited"/>).</summary>
    SpectralImage? image;
    /// <summary>Gain tables of the current note (UI-side copy that edits are recomputed into).</summary>
    ImageFilterTables? tables;
    /// <summary>Row → FFT-bin map, reused by the next <see cref="ImageFilterTables.Build"/> when still valid (saves its cost).</summary>
    FilterBinMap? map;
    /// <summary>Sanitised settings in use.</summary>
    ImageFilterSettings settings = new();
    /// <summary>What is being filtered.</summary>
    FilterSource? source;
    double startColumn;          // image column where the current note started
    double skipSeconds;          // seconds into the file where the current note started
    double passSeconds;          // seconds per pass over the image for the current note
    bool loopImage;              // the current note wraps around the image
    double noteDuration;         // > 0: one-shot note length (s)
    /// <summary>
    /// Play counter: incremented by every <see cref="Launch"/>. Picks the fractional instance number
    /// and the table set, and lets queued pipeline stages notice they are stale (read with
    /// <see cref="Volatile"/> on the Csound thread).
    /// </summary>
    int generation;
    /// <summary>
    /// The <see cref="generation"/> cancelled by <see cref="Stop"/> (−1 = none): queued stages of that
    /// play return without doing anything, so a Play→Stop during the upload can never start a held
    /// note afterwards (see <see cref="Stop"/>). Written on the UI thread, read on the Csound thread (Volatile).
    /// </summary>
    int stoppedGeneration = -1;
    /// <summary>Image columns [dirtyX0, dirtyX1) painted since the last flush (empty when dirtyX1 ≤ dirtyX0).</summary>
    int dirtyX0 = int.MaxValue, dirtyX1 = -1;
    /// <summary>The gain rule changed: the next <see cref="FlushEdits"/> re-uploads every column.</summary>
    bool fullReupload;
    /// <summary>UTC time <see cref="PaintPlayState.Starting"/> began (start-up timeout in <see cref="Tick"/>).</summary>
    DateTime startingSince;
    /// <summary>This player started the host orchestra, so it should stop it when it goes idle.</summary>
    bool stopHostWhenIdle;

    // ---- written on the Csound thread (inside actions), read on the UI thread
    IntPtr compiledFor;                  // CSOUND* the instruments were compiled into
    readonly List<int> liveSets = [];    // table set bases allocated in compiledFor (Csound thread only)
    double noteStartScore = double.NaN;  // score time at which the current note started (NaN = not yet)

    /// <summary>Creates a player for <paramref name="host"/>.</summary>
    /// <param name="host">Owner of the engine; the player subscribes to its <see cref="CsoundEngine.Finished"/> event.</param>
    public ImageFilterPlayer(IPaintEngineHost host)
    {
        this.host = host;
        // A finished performance destroys its CSOUND*; the next one may reuse the address, so
        // forget what was compiled (runs on the performance thread at the end of the performance).
        host.Engine.Finished += _ =>
        {
            compiledFor = IntPtr.Zero;
        };
    }

    /// <summary>
    /// Optional: returns true while something else (e.g. Paint Synth's player) still plays through
    /// the «Paint Rooms host»; then stopping the filter does not stop the host.
    /// </summary>
    public Func<bool>? HostInUseByOthers { get; set; }

    /// <summary>Current state (shares <see cref="PaintPlayState"/> with Paint Synth's player).</summary>
    public PaintPlayState State { get; private set; }

    /// <summary>The settings in use.</summary>
    public ImageFilterSettings Settings => settings;

    /// <summary>The source of the current/last play.</summary>
    public FilterSource? Source => source;

    /// <summary>The gain tables of the current note, or <c>null</c>.</summary>
    public ImageFilterTables? Tables => tables;

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    public event Action? StateChanged;

    /// <summary>Raised with a message for the status line (text, is-warning).</summary>
    public event Action<string, bool>? Message;

    /// <summary>
    /// Filters <paramref name="src"/> with <paramref name="img"/>, the picture starting at image
    /// column <paramref name="fromColumn"/>; restarts if something is already playing; starts Csound
    /// (host orchestra) when it is stopped.
    /// </summary>
    /// <param name="img">The filter picture (kept by reference: later edits are reported through <see cref="OnImageEdited"/>).</param>
    /// <param name="filter">Filter settings (sanitised here).</param>
    /// <param name="src">What to filter.</param>
    /// <param name="fromColumn">Image column (fractional) where the picture starts — the canvas cursor.</param>
    /// <returns>False when it cannot play now; <see cref="Message"/> says why.</returns>
    /// <remarks>
    /// Two paths: when Csound is already performing (any document, or the host) the note is
    /// launched at once; otherwise the host orchestra is started, the state becomes
    /// <see cref="PaintPlayState.Starting"/> and <see cref="Tick"/> launches once the engine runs.
    /// </remarks>
    public bool Play(SpectralImage img, ImageFilterSettings filter, FilterSource src, double fromColumn)
    {
        if (host.IsRenderingFile)
        {
            Message?.Invoke(Loc.S("Csound está renderizando un documento a WAV; espera a que termine.",
                                  "Csound is rendering a document to WAV; wait until it finishes."), true);
            return false;
        }
        if (!src.LiveInput && (src.WavPath is null || !File.Exists(src.WavPath)))
        {
            Message?.Invoke(Loc.S("Elige primero un sonido (archivo WAV, el de otra sala o la entrada en vivo).",
                                  "Choose a sound first (a WAV file, another room's sound or the live input)."), true);
            return false;
        }
        image = img;
        settings = filter.Sanitized();
        source = src;
        startColumn = Math.Clamp(fromColumn, 0, img.Width - 1);
        if (host.IsPlayingLive && host.Engine.SampleRate > 0)
            return Launch();
        if (!host.StartPaintHost())
        {
            Message?.Invoke(Loc.S("No se pudo iniciar Csound (revisa la consola de la ventana principal).",
                                  "Csound could not be started (check the console in the main window)."), true);
            return false;
        }
        stopHostWhenIdle = true;
        startingSince = DateTime.UtcNow;
        SetState(PaintPlayState.Starting);
        Message?.Invoke(Loc.S("Iniciando Csound…", "Starting Csound…"), false);
        return true;
    }

    /// <summary>Stops the filter (with its fade-out); stops Csound too when it was started only for Paint Rooms and nothing else uses it.</summary>
    /// <remarks>
    /// A negative instrument number with the same fractional part releases exactly this play's
    /// held note, so its <c>linsegr</c> 80 ms fade-out runs. Queued on the Csound thread like
    /// everything else, so it is ordered after any pipeline stage queued before it.
    /// Race-free with an upload in flight: the play is first marked as cancelled
    /// (<see cref="stoppedGeneration"/>), so its remaining stages return without starting the note,
    /// and only then the turn-off is queued; on the single Csound thread either the note already
    /// started (and the turn-off queued after it stops it) or it never starts. Before this fix a
    /// fast Play→Stop could leave a held note (p3 = −1) sounding while the UI said «stopped».
    /// </remarks>
    public void Stop()
    {
        if (State != PaintPlayState.Idle)
            Volatile.Write(ref stoppedGeneration, generation);
        if (State == PaintPlayState.Playing)
        {
            string off = "i -" + InstanceNumber(generation) + " 0 0";
            host.Engine.InvokeOnCsoundThread(cs => CsoundEngine.EventNow(cs, off));
        }
        if (State != PaintPlayState.Idle && stopHostWhenIdle && host.IsPaintHostRunning && !(HostInUseByOthers?.Invoke() ?? false))
            host.StopPaintHost();
        stopHostWhenIdle = false;
        SetState(PaintPlayState.Idle);
    }

    /// <summary>Seconds of sound between the moment it enters the filter and the moment you hear it, filter part only.</summary>
    double FilterLatencySeconds => host.Engine.SampleRate > 0 ? settings.LatencySamples / host.Engine.SampleRate : 0;

    /// <summary>Seconds since the note started, as heard (output latency and filter latency removed), or <c>null</c>.</summary>
    /// <remarks>
    /// Uses the engine's score time (seconds of audio Csound has computed), so it follows the
    /// audio clock rather than the wall clock. Returns 0 while the upload pipeline has not yet
    /// started the note (<c>noteStartScore</c> still NaN).
    /// </remarks>
    double? HeardElapsed
    {
        get
        {
            if (State != PaintPlayState.Playing) return null;
            double start = Volatile.Read(ref noteStartScore);
            if (double.IsNaN(start)) return 0;
            return Math.Max(0, host.Engine.ScoreTime - start - host.Engine.OutputLatencySeconds - FilterLatencySeconds);
        }
    }

    /// <summary>
    /// Image column (fractional) that filters what you hear now, or <c>null</c> when not playing
    /// (drives the canvas play line).
    /// </summary>
    public double? CurrentColumn
    {
        get
        {
            if (HeardElapsed is not { } t || image is null || passSeconds <= 0) return null;
            // Columns per second = Width / passSeconds (mirrors kPos in the instrument's section 5).
            double col = startColumn + t * image.Width / passSeconds;
            return loopImage ? col % image.Width : Math.Min(col, image.Width - 1);
        }
    }

    /// <summary>Position in the source file (seconds) that you hear now, or <c>null</c> (live input / not playing).</summary>
    public double? SourcePosition
    {
        get
        {
            if (HeardElapsed is not { } t || source is not { LiveInput: false, Seconds: > 0 } s) return null;
            double p = skipSeconds + t;
            return settings.LoopSource ? p % s.Seconds : Math.Min(p, s.Seconds);
        }
    }

    /// <summary>
    /// Call ~30 times per second on the UI thread: finishes a pending start, uploads coalesced
    /// edits, and notices when a one-shot note ended or Csound stopped.
    /// </summary>
    public void Tick()
    {
        switch (State)
        {
            case PaintPlayState.Starting:
                // Wait until the host really performs (sr known, > 20 ms of score time computed),
                // so the compile/upload actions are executed by THIS performance.
                if (host.IsPlayingLive && host.Engine.SampleRate > 0 && host.Engine.ScoreTime > 0.02)
                    Launch();
                // Give up after 10 s, or after 1.5 s if the engine is not even running (start failed).
                else if (DateTime.UtcNow - startingSince > TimeSpan.FromSeconds(10) ||
                         (!host.IsPlayingLive && DateTime.UtcNow - startingSince > TimeSpan.FromSeconds(1.5)))
                {
                    Message?.Invoke(Loc.S("Csound no pudo empezar a tocar: revisa la consola de la ventana principal.",
                                          "Csound could not start playing: check the console in the main window."), true);
                    SetState(PaintPlayState.Idle);
                }
                break;
            case PaintPlayState.Playing:
                // Csound stopped under us (main Stop button, document ended): just go idle.
                if (!host.IsPlayingLive || host.Engine.SampleRate <= 0)
                {
                    SetState(PaintPlayState.Idle);
                    break;
                }
                FlushEdits();
                // A one-shot note (file not looping) turns itself off in Csound after p16 seconds;
                // notice it here with 0.3 s of slack for the fade-out and the output latency.
                double start = Volatile.Read(ref noteStartScore);
                if (noteDuration > 0 && !double.IsNaN(start) && host.Engine.ScoreTime - start > noteDuration + 0.3)
                {
                    if (stopHostWhenIdle && host.IsPaintHostRunning && !(HostInUseByOthers?.Invoke() ?? false)) host.StopPaintHost();
                    stopHostWhenIdle = false;
                    SetState(PaintPlayState.Idle);
                }
                break;
        }
    }

    /// <summary>
    /// Tells the player that columns [x0, x1) of <paramref name="img"/> changed (call from the
    /// session's PixelsChanged). Uploaded at the next <see cref="Tick"/>.
    /// </summary>
    /// <param name="img">The edited image; ignored unless it is the one being played.</param>
    /// <param name="x0">First changed column (inclusive).</param>
    /// <param name="x1">End of the changed columns (exclusive).</param>
    /// <remarks>Cheap: it only widens the dirty range, so a brush stroke's many events coalesce into one upload per tick.</remarks>
    public void OnImageEdited(SpectralImage img, int x0, int x1)
    {
        if (State != PaintPlayState.Playing || !ReferenceEquals(img, image)) return;
        dirtyX0 = Math.Min(dirtyX0, x0);
        dirtyX1 = Math.Max(dirtyX1, x1);
    }

    /// <summary>
    /// New filter settings. Mix, output gain and smoothing change live; the level → gain rule
    /// re-uploads the tables; anything else restarts the note at the current position.
    /// </summary>
    /// <param name="filter">The new settings (sanitised here; stored even when not playing).</param>
    public void UpdateSettings(ImageFilterSettings filter)
    {
        filter = filter.Sanitized();
        var old = settings;
        settings = filter;
        if (State != PaintPlayState.Playing || tables is null || image is null) return;
        // Copy the old settings with every «live-changeable» field taken from the new ones: if the
        // result still differs from the new settings, a structural field changed → restart.
        var liveOnly = old with
        {
            Mix = filter.Mix, OutputDb = filter.OutputDb, SmoothingMs = filter.SmoothingMs,
            Response = filter.Response, RangeDb = filter.RangeDb, BoostDb = filter.BoostDb, NormalizeRender = filter.NormalizeRender,
        };
        if (liveOnly != filter)
        {
            Restart();
            return;
        }
        // Live channels (the instrument glides them with portk); NormalizeRender only affects renders.
        var e = host.Engine;
        e.SetControl(ImageFilterCode.MixChannel, filter.Mix);
        e.SetControl(ImageFilterCode.GainChannel, ImageFilterCode.OutputGain(filter));
        e.SetControl(ImageFilterCode.SmoothChannel, filter.SmoothingMs / 1000);
        if (tables.UpdateGainRule(image, filter))
            fullReupload = true;
    }

    /// <summary>Restarts at the current position (e.g. the image object, its duration or the source changed).</summary>
    /// <param name="newImage">A replacement picture (e.g. a new canvas), or <c>null</c> to keep the current one.</param>
    /// <param name="newSource">A replacement source, or <c>null</c> to keep the current one.</param>
    /// <remarks>Does nothing unless playing. The new note fades in while the old one fades out (no gap, no click).</remarks>
    public void Restart(SpectralImage? newImage = null, FilterSource? newSource = null)
    {
        if (State != PaintPlayState.Playing || image is null) return;
        double col = CurrentColumn ?? startColumn;
        if (newImage is not null)
        {
            // Keep the same relative position when the new picture has a different width.
            col = col / Math.Max(1, image.Width) * newImage.Width;
            image = newImage;
        }
        if (newSource is not null) source = newSource;
        startColumn = Math.Clamp(col, 0, image.Width - 1);
        Launch();
    }

    // ------------------------------------------------------------------------------------------

    /// <summary>The fractional instrument number of play <paramref name="gen"/>: 9351.0001 … 9351.9999.</summary>
    /// <param name="gen">Play counter (<see cref="generation"/>).</param>
    /// <returns>The instance number as score text (wraps every 9999 plays; four digits so Csound matches it exactly).</returns>
    static string InstanceNumber(int gen) =>
        ImageFilterCode.InstrNumber + "." + ((gen % 9999) + 1).ToString("0000", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>First table number of the set used by play <paramref name="gen"/>.</summary>
    /// <param name="gen">Play counter.</param>
    /// <returns><see cref="ImageFilterCode.FirstTable"/> + 8 × (gen mod <see cref="SetRing"/>).</returns>
    static int SetBase(int gen) => ImageFilterCode.FirstTable + ImageFilterCode.TablesPerSet * (gen % SetRing);

    /// <summary>
    /// Builds the gain tables (UI thread) and queues the upload pipeline (see the file header).
    /// A newer Launch (or another performance) aborts an unfinished pipeline.
    /// </summary>
    /// <returns>False when the tables could not be built (the message is shown); true once the pipeline is queued.</returns>
    /// <remarks>
    /// The state becomes <see cref="PaintPlayState.Playing"/> immediately, before the pipeline has
    /// run; the note itself starts a few control cycles later (one cycle per stage), which is why
    /// <see cref="HeardElapsed"/> treats a NaN start time as «0 s elapsed».
    /// </remarks>
    bool Launch()
    {
        if (image is null || source is null) return false;
        var engine = host.Engine;
        double sr = engine.SampleRate;
        ImageFilterTables t;
        try
        {
            t = ImageFilterTables.Build(image, settings, sr, map);
            map = t.Map;
        }
        catch (Exception ex)
        {
            Message?.Invoke(ex.Message, true);
            if (State == PaintPlayState.Starting) SetState(PaintPlayState.Idle);
            return false;
        }
        // pvsanal needs at least one control period per hop; with a larger ksmps frames are skipped.
        string? ksmpsWarning = engine.Ksmps > settings.Hop
            ? Loc.S($"⚠ ksmps ({engine.Ksmps}) es mayor que el salto de la FFT ({settings.Hop}): usa una FFT más grande o menos solapamiento.",
                    $"⚠ ksmps ({engine.Ksmps}) is larger than the FFT hop ({settings.Hop}): use a bigger FFT or less overlap.")
            : null;

        // ---- time mapping of this note
        var src = source;
        bool fit = settings.TimeMode == FilterTimeMode.FitToSound && !src.LiveInput;
        passSeconds = settings.PassSeconds(src.LiveInput ? 0 : src.Seconds);
        loopImage = !fit || settings.LoopSource;
        // In «fit» mode the image and the file run together: start the file at the cursor's moment.
        // (Exact at speed 1; at other speeds the picture deliberately runs faster/slower than the file.)
        skipSeconds = fit ? startColumn / image.Width * src.Seconds : 0;
        if (!src.LiveInput && skipSeconds >= src.Seconds - 0.01) skipSeconds = 0;
        // One-shot length = rest of the file + the filter latency (so the tail comes out) + 0.1 s;
        // 0 = held until stopped (live input or looping file).
        noteDuration = src.LiveInput || settings.LoopSource ? 0 : src.Seconds - skipSeconds + settings.LatencySamples / sr + 0.1;

        // Everything the Csound-thread stages need is captured into locals now (UI thread), so
        // later UI changes cannot alter what a queued stage does.
        int previous = State == PaintPlayState.Playing ? generation : -1;
        generation++;
        int gen = generation;
        int setBase = SetBase(gen);
        var spec = new ImageFilterCode.PlaySpec(t.ToGainColumn(startColumn), loopImage, src.LiveInput, skipSeconds, settings.LoopSource, noteDuration, src.WavPath);
        string note = $"i {InstanceNumber(gen)} 0 -1 {ImageFilterCode.Fields(t, settings, setBase, passSeconds, spec)}";
        string? stopPrevious = previous >= 0 ? $"i -{InstanceNumber(previous)} 0 0" : null;
        // Split the table lines: the three small tables (with the channel init) go in one stage,
        // each big gain table in a stage of its own (allocating MBs is the slow part).
        var lines = ImageFilterCode.LiveTableLines(t, setBase);
        string smallTables = ImageFilterCode.ChannelInit(settings) +
                             string.Concat(lines.Where(l => l.Offset is not (ImageFilterCode.TableMap.GainL or ImageFilterCode.TableMap.GainR)).Select(l => l.Code));
        string gainLCode = lines.First(l => l.Offset == ImageFilterCode.TableMap.GainL).Code;
        string gainRCode = lines.First(l => l.Offset == ImageFilterCode.TableMap.GainR).Code;
        double mix = settings.Mix, gain = ImageFilterCode.OutputGain(settings), smooth = settings.SmoothingMs / 1000;
        tables = t;
        dirtyX0 = int.MaxValue; dirtyX1 = -1; fullReupload = false;
        Volatile.Write(ref noteStartScore, double.NaN);

        // The pipeline (file header, steps 1–5). Each lambda runs on the Csound performance thread
        // with the live CSOUND*; a throw is reported by the engine and ends the pipeline there.
        var stages = new List<Action<IntPtr>>
        {
            // Stage 1: compile instr 9351/9352 once per performance.
            cs =>
            {
                if (cs == compiledFor) return;
                // A new performance: compile the filter instruments, forget older table sets.
                if (CsoundEngine.CompileNow(cs, ImageFilterCode.Instruments()) != 0)
                    throw new InvalidOperationException("Image Filter: the instrument did not compile (see the console).");
                compiledFor = cs;
                liveSets.Clear();
            },
            // Stage 2: release the previous note, free stale sets, create the small tables + channels.
            cs =>
            {
                if (stopPrevious is not null)
                    CsoundEngine.EventNow(cs, stopPrevious);
                // Free older sets, keeping the previous one (its note may still be fading out).
                var free = new System.Text.StringBuilder();
                int keepPrev = SetBase(gen - 1);
                foreach (int b in liveSets.ToList())   // ToList: the loop removes from liveSets
                {
                    if (b == keepPrev) continue;
                    // Only free tables that exist (ftfree of a missing table is a Csound error).
                    for (int k = 0; k < ImageFilterCode.TablesPerSet; k++)
                        if (CsoundEngine.TableLength(cs, b + k) > 0)
                            free.Append("ftfree ").Append(b + k).Append(", 0\n");
                    liveSets.Remove(b);
                }
                if (free.Length > 0) CsoundEngine.CompileNow(cs, free.ToString());
                if (CsoundEngine.CompileNow(cs, smallTables) != 0)
                    throw new InvalidOperationException("Image Filter: the tables could not be created.");
                liveSets.Add(setBase);
            },
            // Stage 3a / 3b: allocate the big (zero-filled) gain tables, one per cycle.
            cs => { if (CsoundEngine.CompileNow(cs, gainLCode) != 0) throw new InvalidOperationException("Image Filter: out of memory for the gain table."); },
            cs => { if (CsoundEngine.CompileNow(cs, gainRCode) != 0) throw new InvalidOperationException("Image Filter: out of memory for the gain table."); },
        };
        // Stage 4: copy the gains into the tables' memory, CopyValuesPerCycle values per table per
        // cycle. The lambdas read t.GainL/GainR directly (no copy of up to 2 × 17 MB): FlushEdits may
        // update t in place on the UI thread meanwhile, but it then queues its own snapshot of the
        // changed span, which runs after and wins, so a torn read is overwritten within cycles.
        for (int a = 0; a < t.GainL.Length; a += CopyValuesPerCycle)
        {
            int from = a, n = Math.Min(CopyValuesPerCycle, t.GainL.Length - a);
            stages.Add(cs =>
            {
                CsoundEngine.CopyToTable(cs, setBase + ImageFilterCode.TableMap.GainL, t.GainL.AsSpan(from, n), from);
                CsoundEngine.CopyToTable(cs, setBase + ImageFilterCode.TableMap.GainR, t.GainR.AsSpan(from, n), from);
            });
        }
        // Stage 5: set the channels (again, in case they moved meanwhile in another play) and
        // start the held note; record when it started, for the play line.
        stages.Add(cs =>
        {
            CsoundEngine.ControlNow(cs, ImageFilterCode.MixChannel, mix);
            CsoundEngine.ControlNow(cs, ImageFilterCode.GainChannel, gain);
            CsoundEngine.ControlNow(cs, ImageFilterCode.SmoothChannel, smooth);
            CsoundEngine.EventNow(cs, note);
            Volatile.Write(ref noteStartScore, CsoundEngine.ScoreTimeNow(cs));
        });
        RunPipeline(engine, stages, gen);

        SetState(PaintPlayState.Playing);
        if (ksmpsWarning is not null)
            Message?.Invoke(ksmpsWarning, true);
        else if (src.LiveInput && !engine.HasLiveInput)
            Message?.Invoke(Loc.S("⚠ No hay entrada de audio: elige una en Configuración › Audio › Entrada (y vuelve a tocar). Mientras tanto el filtro recibe silencio.",
                                  "⚠ No audio input: choose one in Settings › Audio › Input (then play again). Until then the filter receives silence."), true);
        else
            Message?.Invoke(Loc.S(
                $"▶ {src.Name} · FFT {settings.FftSize}/{settings.Overlap} · latencia {settings.LatencyMs(sr):0} ms · {t.Columns}×{t.Bins} ganancias",
                $"▶ {src.Name} · FFT {settings.FftSize}/{settings.Overlap} · latency {settings.LatencyMs(sr):0} ms · {t.Columns}×{t.Bins} gains"), false);
        return true;
    }

    /// <summary>
    /// Runs <paramref name="stages"/> on the Csound thread, one per control cycle (an action queued
    /// by an action runs at the NEXT cycle), aborting when a newer play started or the performance changed.
    /// </summary>
    /// <param name="engine">The engine whose performance thread runs the stages.</param>
    /// <param name="stages">The stages, in order.</param>
    /// <param name="gen">The play they belong to; compared with <see cref="generation"/> before each stage.</param>
    /// <remarks>
    /// The local <c>Runner</c> re-queues itself after each stage instead of queueing every stage at
    /// once: that is what spreads the work over successive control cycles. <c>pipelineCs</c> and
    /// <c>next</c> are only touched on the Csound thread.
    /// </remarks>
    void RunPipeline(CsoundEngine engine, List<Action<IntPtr>> stages, int gen)
    {
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
    }

    /// <summary>
    /// Uploads the gain columns changed since the last tick (or all of them after a gain-rule
    /// change): UI-thread recompute, copy of the changed span, chunked Csound-thread copies.
    /// </summary>
    /// <remarks>
    /// Runs in <see cref="Tick"/> (~30 Hz), so painting reaches the sound within ≈ 33 ms plus a few
    /// control cycles. If the image's size or axis changed since the tables were built, it
    /// restarts instead (the tables no longer fit). The copy stages are cancelled like the upload
    /// pipeline (newer play or Stop) and only write into the performance that holds the tables.
    /// </remarks>
    void FlushEdits()
    {
        if (tables is not { } t || image is null) return;
        int c0, c1;   // gain-column range to upload
        if (fullReupload)
        {
            fullReupload = false;
            dirtyX0 = int.MaxValue; dirtyX1 = -1;
            (c0, c1) = (0, t.Columns);
        }
        else
        {
            if (dirtyX1 <= dirtyX0) return;
            int x0 = Math.Max(0, dirtyX0), x1 = Math.Min(image.Width, dirtyX1);
            dirtyX0 = int.MaxValue; dirtyX1 = -1;
            if (image.Width != t.ImageWidth || image.Height != t.Map.Rows) { Restart(); return; }
            (c0, c1) = t.UpdateColumns(image, x0, x1);
        }
        if (c1 <= c0) return;
        int b = t.Bins, setBase = SetBase(generation);
        // Copy the changed span out now: the UI thread may change the arrays again before Csound runs.
        var l = t.GainL.AsSpan(c0 * b, (c1 - c0) * b).ToArray();
        var r = t.GainR.AsSpan(c0 * b, (c1 - c0) * b).ToArray();
        var stages = new List<Action<IntPtr>>();
        for (int a = 0; a < l.Length; a += CopyValuesPerCycle)
        {
            int from = a, n = Math.Min(CopyValuesPerCycle, l.Length - a);
            stages.Add(cs =>
            {
                // Only into the performance that holds this player's tables (the play number is
                // checked by RunPipeline). Comparing with the CSOUND* of the last STARTED note, as an
                // earlier version did, dropped edits made while the first upload of a new
                // performance was still running (that handle was unset or stale then).
                if (cs != compiledFor) return;
                CsoundEngine.CopyToTable(cs, setBase + ImageFilterCode.TableMap.GainL, l.AsSpan(from, n), c0 * b + from);
                CsoundEngine.CopyToTable(cs, setBase + ImageFilterCode.TableMap.GainR, r.AsSpan(from, n), c0 * b + from);
            });
        }
        RunPipeline(host.Engine, stages, generation);
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
