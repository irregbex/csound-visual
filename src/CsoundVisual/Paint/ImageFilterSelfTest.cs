// ImageFilterSelfTest.cs — Paint Rooms · «Image Filter» real-time check (command line, no UI, inaudible).
//
// Role: `CsoundVisual.exe --filter-test [seconds] [file|live|both] [fft/overlap …]` runs the Image
// Filter LIVE through the app's own WASAPI engine (shared mode, Windows default output) using
// exactly the path the room uses (ImageFilterPlayer → «Paint Rooms host» orchestra → staged table
// upload through InvokeOnCsoundThread → instr 9351), while «painting» into the filter picture every
// 100 ms to exercise the live gain-column uploads. The output gain is −96 dB, so nothing audible
// reaches the speakers. For each case it prints the DSP load (time Csound spends computing ÷ time
// available), dropouts, the worst device wake-up gap and any Csound error.
//
//   file — a generated 6-second stereo noise file (diskin2 source), looped;
//   live — the live-input path (inch 1/2) fed by a LOOPBACK capture of the default output (the
//          system's own sound: no microphone needed — Windows' microphone privacy setting does not
//          apply to loopback). The filter's own −96 dB output adds nothing audible.
// Default FFT settings tested: 1024/4 (the room's default) and 4096/8 (the most expensive).
//
// Arguments (all optional, positional):
//   [seconds]      how long each case plays (default 8; parsed with the invariant culture);
//   [file|live|both] which sources to test (default both);
//   [fft/overlap …] any number of FFT size / overlap pairs, e.g. «512/4 2048/8».
// Exit code: the worst case result — 0 = clean, 2 = Csound errors (or no DSP-load samples at all,
// i.e. the engine never reached a playing state), 3 = dropouts. Dispatched from Program.Main,
// which passes every argument after «--filter-test». The measured figures in docs/en/paint-rooms.md
// («Measured») come from this command.
//
// Threading: everything runs on the calling (main) thread, which polls the player every 30 ms the
// way the room's UI timer does; the engine runs its own Csound performance thread and WASAPI
// threads, exactly as in the app.

using CsoundVisual.Audio;
using CsoundVisual.Engine;

namespace CsoundVisual.Paint;

/// <summary>The <c>--filter-test</c> command (see the file header).</summary>
public static class ImageFilterSelfTest
{
    /// <summary>A minimal <see cref="IPaintEngineHost"/> around a private engine with the app's WASAPI output (and optional loopback input).</summary>
    /// <param name="loopbackInput">
    /// True to open the default output device's loopback stream as the engine's live input, so the
    /// «live» case has a real signal on <c>inch 1/2</c> without needing a microphone.
    /// </param>
    /// <remarks>
    /// In the app this role is played by the main window, which owns the shared engine; here the
    /// host owns a private <see cref="CsoundEngine"/> that only ever runs the Paint Rooms host
    /// orchestra, so «playing live» and «host running» are both simply «the engine is busy».
    /// </remarks>
    sealed class Host(bool loopbackInput) : IPaintEngineHost
    {
        /// <summary>The private engine (Csound performance thread + WASAPI output).</summary>
        public CsoundEngine Engine { get; } = new();
        /// <inheritdoc/>
        public bool IsPlayingLive => Engine.IsBusy;
        /// <inheritdoc/>
        public bool IsRenderingFile => false;
        /// <inheritdoc/>
        public bool IsPaintHostRunning => Engine.IsBusy;

        /// <summary>
        /// Starts the shared «Paint Rooms host» orchestra (<see cref="PaintLivePlayer.HostCsd"/>) on
        /// the WASAPI default output in shared mode with the low-latency preset. <c>-odac</c> asks for
        /// real-time output (served by the engine's host audio given in <see cref="HostAudioSettings"/>),
        /// <c>-d</c> suppresses graphs, <c>-m0</c> silences the
        /// per-note messages. Idempotent: returns true at once if the engine already runs.
        /// </summary>
        /// <returns>Always true (start-up failures surface later as engine error messages).</returns>
        public bool StartPaintHost()
        {
            if (Engine.IsBusy) return true;
            Engine.Start(new EngineJob(PaintLivePlayer.HostCsd(), ["-odac", "-d", "-m0"], null,
                new HostAudioSettings(null, WasapiMode.Shared, LatencyPreset.Low, loopbackInput ? new AudioInputSettings(null, Loopback: true) : null)));
            return true;
        }

        /// <summary>Stops the engine (asynchronous: <see cref="CsoundEngine.IsBusy"/> drops a moment later).</summary>
        public void StopPaintHost() => Engine.Stop();
    }

    /// <summary>Runs the test; returns the process exit code (0 = no dropouts and no errors).</summary>
    /// <param name="args">The command-line arguments after <c>--filter-test</c> (see the file header).</param>
    /// <returns>1 when Csound cannot be loaded; otherwise the worst <see cref="RunCase"/> result (0, 2 or 3).</returns>
    public static int Run(string[] args)
    {
        if (!PaintRenderer.InitializeCsound())
        {
            Console.WriteLine("Csound not found.");
            return 1;
        }
        double seconds = args.Length > 0 && double.TryParse(args[0], System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 8;
        string which = args.Length > 1 ? args[1] : "both";
        // Every remaining argument of the form «N/M» is an FFT size / overlap pair; others are ignored.
        var ffts = args.Skip(2).Select(a => a.Split('/')).Where(p => p.Length == 2 && int.TryParse(p[0], out _) && int.TryParse(p[1], out _))
                       .Select(p => (int.Parse(p[0]), int.Parse(p[1]))).ToList();
        if (ffts.Count == 0) ffts = [(1024, 4), (4096, 8)];

        // The filter picture: the «slow sweep» preset on the room's default canvas (384 rows × 512 columns).
        var prefs = new ImageFilterPrefs();
        var image = new SpectralImage(prefs.Canvas.Width, prefs.Canvas.BuildAxis(out _), 4);
        ImageFilterPresets.All.First(p => p.Id == "sweep").Draw(image);
        Console.WriteLine($"Filter picture: {image.Width} columns × {image.Height} rows ({image.Axis.Describe()})");

        // A 6 s stereo noise file (−12 dBFS peak) for the file source. Fixed seeds (5 here, 3 for
        // the painting) make every run use the same material and strokes, so loads are comparable.
        // FilterSource.Prepare writes it to a temporary WAV that diskin2 reads; deleted at the end.
        var rng = new Random(5);
        var noise = new AudioData([new float[6 * 48000], new float[6 * 48000]], 48000);
        foreach (var ch in noise.Channels)
            for (int i = 0; i < ch.Length; i++) ch[i] = (float)((rng.NextDouble() * 2 - 1) * 0.25);
        var fileSource = FilterSource.Prepare(noise, "noise.wav");

        int worst = 0;
        foreach (var (fft, overlap) in ffts)
        {
            // −96 dB output: the filter runs at full cost but is inaudible. Each case gets a fresh
            // clone of the picture because RunCase paints into it.
            var settings = new ImageFilterSettings { FftSize = fft, Overlap = overlap, OutputDb = -96, TimeMode = FilterTimeMode.Seconds, LoopSeconds = 4 };
            if (which is "file" or "both")
                worst = Math.Max(worst, RunCase(image.Clone(), settings, fileSource, false, seconds));
            if (which is "live" or "both")
                worst = Math.Max(worst, RunCase(image.Clone(), settings, FilterSource.Live, true, seconds));
        }
        try { File.Delete(fileSource.WavPath!); } catch { /* temp file */ }
        return worst;
    }

    /// <summary>Plays one case for <paramref name="seconds"/>, painting every 100 ms; prints the measurements. Returns 0 (fine), 2 (errors) or 3 (dropouts).</summary>
    /// <param name="image">The filter picture (painted into during the test: pass a clone).</param>
    /// <param name="settings">FFT size/overlap, time mode and the −96 dB output gain.</param>
    /// <param name="source">The prepared noise file, or <see cref="FilterSource.Live"/>.</param>
    /// <param name="loopback">True to open the loopback capture as live input (the «live» case).</param>
    /// <param name="seconds">Playing time in seconds before the player is stopped.</param>
    /// <returns>0 = clean, 2 = Csound errors or no load samples, 3 = dropouts.</returns>
    /// <remarks>
    /// Each case uses a brand-new host/engine, so one case's start-up or teardown cannot affect the
    /// next. The loop runs for <paramref name="seconds"/> + 1.5 s: the first second is left alone
    /// (start-up, table uploads) before painting and load sampling begin, and the extra 1.5 s
    /// lets the stop/fade-out complete.
    /// </remarks>
    static int RunCase(SpectralImage image, ImageFilterSettings settings, FilterSource source, bool loopback, double seconds)
    {
        var host = new Host(loopback);
        var player = new ImageFilterPlayer(host);
        var messages = new List<string>();
        player.Message += (m, _) => messages.Add(m);
        Console.WriteLine($"\n== {(source.LiveInput ? "live input (loopback)" : "file source")} · FFT {settings.FftSize}/{settings.Overlap} " +
                          $"(latency {settings.LatencySamples} samples = {settings.LatencyMs(48000):0.0} ms at 48 kHz) · {seconds:0.#} s, output −96 dB");
        player.Play(image, settings, source, 0);

        var rng = new Random(3);
        var loads = new List<double>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Times (seconds since start) of the next simulated stroke and the next DSP-load sample.
        double nextPaint = 1, nextSample = 1;
        int strokes = 0, lastUnderruns = 0;
        bool changedLive = false;
        bool hadInput = false;          // the engine had a live input while the filter played
        int inputChannels = 0;
        float inputPeak = 0;            // loudest input sample seen (the loopback carries whatever the PC plays)
        Span<float> peaks = stackalloc float[8];   // per-channel input peaks (up to 8 channels)
        while (sw.Elapsed.TotalSeconds < seconds + 1.5)
        {
            // Stand-in for the room's UI timer: Tick advances the player's state machine
            // (starting → playing, deferred uploads, end of file).
            Thread.Sleep(30);
            player.Tick();
            double t = sw.Elapsed.TotalSeconds;
            if (player.State == PaintPlayState.Playing && t > nextPaint && t < seconds)
            {
                // Simulated painting: a soft vertical block of 4 columns × 40 rows at a random place
                // (left layer full, right layer 0.3), then the same notifications a brush stroke
                // sends, so the player re-uploads columns x…x+4 to the running instrument.
                int x = rng.Next(image.Width - 6), y = rng.Next(image.Height - 40);
                for (int dx = 0; dx < 4; dx++)
                    for (int dy = 0; dy < 40; dy++) image.Set(x + dx, y + dy, 1, 0.3f);
                image.NotifyChanged(x, x + 4);
                player.OnImageEdited(image, x, x + 4);
                strokes++;
                nextPaint = t + 0.1;
            }
            if (!changedLive && t > seconds / 2 && player.State == PaintPlayState.Playing)
            {
                // Halfway: a gain-rule change (full re-upload) and a mix change (live channel).
                player.UpdateSettings(settings with { Response = FilterResponse.Decibels, Mix = 0.7 });
                changedLive = true;
            }
            if (player.State == PaintPlayState.Playing && host.Engine.HasLiveInput)
            {
                hadInput = true;
                inputChannels = host.Engine.InputChannels;
                int n = host.Engine.TakeInputPeaks(peaks);
                for (int i = 0; i < n; i++) inputPeak = Math.Max(inputPeak, peaks[i]);
            }
            if (t > nextSample && host.Engine.DspLoad >= 0 && player.State == PaintPlayState.Playing)
            {
                loads.Add(host.Engine.DspLoad);
                if (host.Engine.Underruns != lastUnderruns)
                {
                    Console.WriteLine($"   t={t:0.00} s: dropouts so far {host.Engine.Underruns}");
                    lastUnderruns = host.Engine.Underruns;
                }
                nextSample = t + 0.25;
            }
            if (t > seconds && player.State != PaintPlayState.Idle)
                player.Stop();
            if (t > seconds && !host.Engine.IsBusy) break;
        }
        // Stopping the player leaves the host orchestra running (as in the app); stop the engine
        // and wait up to 2 s (100 × 20 ms) for its performance thread to finish.
        host.Engine.Stop();
        for (int i = 0; i < 100 && host.Engine.IsBusy; i++) Thread.Sleep(20);

        // Drain the engine's message queue: count errors, echo errors and warnings.
        int errors = 0;
        while (host.Engine.Messages.TryDequeue(out var line))
        {
            if (line.Kind is MessageKind.Error) errors++;
            if (line.Kind is MessageKind.Error or MessageKind.Warning)
                Console.WriteLine("   " + line.Text);
        }
        foreach (var m in messages) Console.WriteLine("   player: " + m);
        double mean = loads.Count > 0 ? loads.Average() : -1, max = loads.Count > 0 ? loads.Max() : -1;
        Console.WriteLine($"   DSP load mean {mean * 100:0.0} %, max {max * 100:0.0} % ({loads.Count} samples) · dropouts {host.Engine.Underruns} · " +
                          $"worst wake-up {host.Engine.WorstWakeMs:0.##} ms · live uploads {strokes} · Csound errors {errors}" +
                          (source.LiveInput ? $" · live input active {hadInput} ({inputChannels} ch, peak {20 * Math.Log10(Math.Max(inputPeak, 1e-9)):0.0} dBFS)" : ""));
        return errors > 0 || loads.Count == 0 ? 2 : host.Engine.Underruns > 0 ? 3 : 0;
    }
}
