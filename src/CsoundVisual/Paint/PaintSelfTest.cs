// PaintSelfTest.cs — Paint Rooms real-time check (command line, no UI, inaudible).
//
// Role: `CsoundVisual.exe --paint-test [picture|preset-id|dense] [seconds] [sines|noise|both]`
// plays a 512-row × 1024-column image LIVE through the app's own WASAPI engine (shared mode, Windows
// default output) using exactly the path the Paint Synth room uses (PaintLivePlayer → host
// orchestra → InvokeOnCsoundThread table upload → instr 9301), while «painting» into it every
// 100 ms to exercise the live column uploads. The output gain is −96 dB below the automatic level, so nothing audible
// reaches the speakers. Prints the DSP load (time Csound spends computing ÷ time available),
// dropouts, the worst device wake-up gap and any Csound error.
//
//   picture   — a PNG/JPG file (imported with brightness → both layers, then meant to be made
//               stereo by fading the right layer so the two-bank path is measured — but see the
//               note in MakeImage: as written, both layers end up equal, so it plays mono);
//   preset id — chord | sweep | rhythm | landscape (resampled to 1024 × 512 if needed);
//   dense     — worst case: every cell lit with random levels in both layers (default);
//   sequencer — the Image Sequencer's starter arrangement composed into one canvas (97 rows, 8 bars,
//               noisy drums: in «sines» mode it plays through the per-cell tone/noise mix).
//
//   seconds   — how long each mode plays (default 8).
//   modes     — sines | noise | light | mixed | both (sines + noise, the default) | all (the four).
//               «noise» is the smooth noise-band mode (sine + cosine bank per side), «light» the
//               one-bank variant (PaintSynthSettings.SmoothNoise = false), «mixed» the Sines mode
//               with a NOISE LAYER (random noisiness per cell): the per-cell tone/noise crossfade,
//               which runs the sine bank and the smooth noise banks together (the heaviest path).
//
// Exit code (the worst over the modes run): 0 = fine, 1 = Csound not found, 2 = Csound errors
// (or no DSP-load samples at all, i.e. the note never played), 3 = dropouts.
//
// The test owns a PRIVATE CsoundEngine (the nested Host class): it does not need the main window,
// MainViewModel or Avalonia. Run it from a console so Console.WriteLine is visible.

using CsoundVisual.Audio;
using CsoundVisual.Engine;

namespace CsoundVisual.Paint;

/// <summary>The <c>--paint-test</c> command (see the file header).</summary>
public static class PaintSelfTest
{
    /// <summary>A minimal <see cref="IPaintEngineHost"/> around a private engine with the app's WASAPI output.</summary>
    /// <remarks>
    /// Stands in for MainViewModel: the only performance this engine ever runs is the Paint Rooms
    /// host orchestra, so «playing live» and «host running» are both simply «engine busy».
    /// </remarks>
    sealed class Host : IPaintEngineHost
    {
        /// <summary>The private engine (one per mode run, disposed of by stopping it).</summary>
        public CsoundEngine Engine { get; } = new();

        /// <summary>Engine busy = performing; the player waits for <c>SampleRate &gt; 0</c> itself.</summary>
        public bool IsPlayingLive => Engine.IsBusy; // the player waits for SampleRate > 0 itself

        /// <summary>Never: the self-test does not render files.</summary>
        public bool IsRenderingFile => false;

        /// <summary>Engine busy = the host orchestra is running (it is the only thing this engine runs).</summary>
        public bool IsPaintHostRunning => Engine.IsBusy;

        /// <summary>
        /// Starts <see cref="PaintLivePlayer.HostCsd"/> in real time: <c>-odac</c> = the app's own
        /// WASAPI output (default device, shared mode, low-latency preset), <c>-d</c> no displays,
        /// <c>-m0</c> quiet console.
        /// </summary>
        /// <returns>Always true (start errors show up later as Csound messages / no DSP samples).</returns>
        public bool StartPaintHost()
        {
            if (Engine.IsBusy) return true;
            Engine.Start(new EngineJob(PaintLivePlayer.HostCsd(), ["-odac", "-d", "-m0"], null,
                new HostAudioSettings(null, WasapiMode.Shared, LatencyPreset.Low)));
            return true;
        }

        /// <summary>Stops the private engine.</summary>
        public void StopPaintHost() => Engine.Stop();
    }

    /// <summary>Runs the test; returns the process exit code (0 = no dropouts and no errors).</summary>
    /// <param name="args">The arguments AFTER <c>--paint-test</c>: [picture|preset-id|dense] [seconds] [modes] (see the file header).</param>
    /// <returns>The worst code of the modes run (0 fine, 1 no Csound, 2 errors, 3 dropouts).</returns>
    public static int Run(string[] args)
    {
        // Locates and loads the Csound library (CsoundLocator) before any engine is created.
        if (!PaintRenderer.InitializeCsound())
        {
            Console.WriteLine("Csound not found.");
            return 1;
        }
        // Positional arguments; «seconds» is parsed with the invariant culture (8.5, not 8,5).
        string what = args.Length > 0 ? args[0] : "dense";
        double seconds = args.Length > 1 && double.TryParse(args[1], System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 8;
        string modes = args.Length > 2 ? args[2] : "both";
        var image = MakeImage(what);
        Console.WriteLine($"Image «{what}»: {image.Width} columns × {image.Height} rows, {image.DurationSeconds:0.#} s, stereo {image.IsStereo()}");

        int worst = 0;
        // Modes: sines, noise (smooth: 2 banks per side), light (noise with 1 bank per side), both (sines + noise), all.
        foreach (var (mode, smooth, name) in new[] { (SynthMode.Sines, true, "sines"), (SynthMode.NoiseBands, true, "noise"), (SynthMode.NoiseBands, false, "light"), (SynthMode.Sines, true, "mixed") })
        {
            bool wanted = modes == name || modes == "all" || (modes == "both" && name is "sines" or "noise");
            if (!wanted) continue;
            // Each mode gets a fresh copy (the simulated painting modifies the image) and its own engine.
            var copy = image.Clone();
            if (name == "mixed")
            {
                // A noise layer with random noisiness 0…1 in every cell (fixed seed).
                var rng = new Random(17);
                var n = copy.EnsureNoise();
                for (int i = 0; i < n.Length; i++) n[i] = (float)rng.NextDouble();
            }
            worst = Math.Max(worst, RunMode(copy, mode, smooth, seconds));
        }
        return worst;
    }

    /// <summary>Plays <paramref name="image"/> in one synthesis mode for <paramref name="seconds"/>, painting into it every 100 ms, and prints the measurements. Returns 0 (fine), 2 (errors) or 3 (dropouts).</summary>
    /// <param name="image">The test image (modified by the simulated painting).</param>
    /// <param name="mode">Synthesis mode to measure.</param>
    /// <param name="smoothNoise">Noise-band mode: true = two banks (sine + cosine) per side, false = one.</param>
    /// <param name="seconds">Playing time in seconds (the loop runs 1.5 s longer to let Csound stop).</param>
    /// <returns>0 fine, 2 Csound errors or no DSP-load samples, 3 dropouts.</returns>
    /// <remarks>
    /// The loop below is a stand-in for the Paint Rooms window's 30 ms UI timer: it calls
    /// <see cref="PaintLivePlayer.Tick"/> (which finishes the start and uploads coalesced edits) on
    /// this one thread, exactly as the UI thread would.
    /// </remarks>
    static int RunMode(SpectralImage image, SynthMode mode, bool smoothNoise, double seconds)
    {
        var host = new Host();
        var player = new PaintLivePlayer(host);
        string? lastMessage = null;
        player.Message += (m, _) => lastMessage = m;
        // −96 dB on top of the automatic level (which already peaks near −6 dBFS): the full DSP
        // work, nothing audible (the UI stops at −60 dB; PaintTables accepts down to −96).
        var settings = new PaintSynthSettings { Mode = mode, SmoothNoise = smoothNoise, GainDb = -96, Loop = true, AttackMs = 8, ReleaseMs = 40 };
        Console.WriteLine($"\n== {mode}: playing for {seconds:0.#} s through the app's WASAPI engine (shared, default output), gain −96 dB");
        // Csound is not running yet: Play starts the host orchestra and returns in state Starting;
        // the Tick calls below launch the note once the engine reports a sample rate.
        player.Play(image, settings, 0);

        // Fixed seed: the same strokes every run, so results are comparable. Timings in seconds
        // since start: first stroke and first DSP-load sample after 1 s (start-up excluded).
        var rng = new Random(3);
        var loads = new List<double>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double nextPaint = 1, nextSample = 1;
        int strokes = 0, lastUnderruns = 0;
        while (sw.Elapsed.TotalSeconds < seconds + 1.5)
        {
            Thread.Sleep(30);   // ≈ the UI timer period
            player.Tick();
            double t = sw.Elapsed.TotalSeconds;
            if (player.State == PaintPlayState.Playing && t > nextPaint && t < seconds)
            {
                // Simulated live painting: a short horizontal stroke of 6 columns at a random place.
                int x = rng.Next(image.Width - 8), y = rng.Next(image.Height);
                // Same sequence as a brush: change the pixels, notify, tell the player which
                // columns are dirty (they are uploaded at the next Tick).
                for (int dx = 0; dx < 6; dx++) image.Set(x + dx, y, 1, 0.5f);
                image.NotifyChanged(x, x + 6);
                player.OnImageEdited(image, x, x + 6);
                strokes++;
                nextPaint = t + 0.1;
            }
            // Sample the engine's DSP load (fraction 0…1; negative = not measured yet) every 250 ms
            // and report new dropouts (WASAPI buffer underruns) as they happen.
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
            // Time is up: Stop also stops the host orchestra (the player started it), then wait
            // for the engine to finish (at most until the 1.5 s grace period ends).
            if (t > seconds && player.State != PaintPlayState.Idle)
                player.Stop();
            if (t > seconds && !host.Engine.IsBusy) break;
        }
        // Belt and braces: make sure the engine is down (wait up to 2 s) before reading its log.
        host.Engine.Stop();
        for (int i = 0; i < 100 && host.Engine.IsBusy; i++) Thread.Sleep(20);

        // Drain the engine's console queue: count errors, echo the interesting lines.
        int errors = 0;
        while (host.Engine.Messages.TryDequeue(out var line))
        {
            if (line.Kind is MessageKind.Error) errors++;
            if (line.Kind is MessageKind.Error or MessageKind.Warning or MessageKind.Info)
                Console.WriteLine("   " + line.Text);
        }
        if (lastMessage is not null) Console.WriteLine("   player: " + lastMessage);
        double mean = loads.Count > 0 ? loads.Average() : -1, max = loads.Count > 0 ? loads.Max() : -1;
        Console.WriteLine($"   DSP load mean {mean * 100:0.0} %, max {max * 100:0.0} % ({loads.Count} samples) · dropouts {host.Engine.Underruns} · " +
                          $"worst wake-up {host.Engine.WorstWakeMs:0.##} ms · live uploads {strokes} · late events {host.Engine.LateEvents} · Csound errors {errors}");
        return errors > 0 || loads.Count == 0 ? 2 : host.Engine.Underruns > 0 ? 3 : 0;
    }

    /// <summary>Builds the 512 × 1024 test image: a picture file, a preset resampled to that size, or the dense worst case.</summary>
    /// <param name="what">A picture path, a preset id (<see cref="PaintPresets.All"/>), or anything else for «dense».</param>
    /// <returns>A 1024-column image on a 512-row logarithmic axis, 40 Hz … 12 kHz (the app's default canvas).</returns>
    static SpectralImage MakeImage(string what)
    {
        var axis = PitchAxis.Logarithmic(40, 12000, 512);
        if (what == "sequencer")
            return SequencerComposer.Compose(SequencerDocument.Starter(SequencerDocument.DefaultCanvasPrefs()));
        if (File.Exists(what))
        {
            // 20 s long; brightness mapping puts the same levels in both layers (mono).
            var img = ImageCodec.Import(what, 1024, axis, 20, ColorMapping.Brightness);
            // Intended: make left and right differ so the two-bank (stereo) path is measured.
            // NOTE: ImageOps.Fade scales BOTH layers by the same gain, so after it (and the max
            // below) the layers are still identical and the image stays mono (known limitation:
            // the picture variant actually measures the one-bank path).
            ImageOps.Fade(img, CellRect.All(img), false); // left/right differ → two banks
            for (int i = 0; i < img.Left.Length; i++) img.Left[i] = Math.Max(img.Left[i], img.Right[i]);
            return img;
        }
        // A preset at its own size, retuned/resampled onto the 1024 × 512 test canvas (pitches kept in Hz).
        if (PaintPresets.All.FirstOrDefault(p => p.Id == what) is { } preset)
        {
            var (pi, _) = preset.Build(new PaintSynthSettings()); // the preset's suggested settings are not used
            var resized = pi.Retuned(1024, axis);
            resized.DurationSeconds = pi.DurationSeconds;
            return resized;
        }
        // Dense worst case: every cell of both layers lit, independently (levels 0.2…1), 20 s long —
        // stereo, all 512 oscillators per bank busy, every column different.
        var dense = new SpectralImage(1024, axis, 20);
        var rng = new Random(11);
        for (int i = 0; i < dense.Left.Length; i++)
        {
            dense.Left[i] = (float)(0.2 + 0.8 * rng.NextDouble());
            dense.Right[i] = (float)(0.2 + 0.8 * rng.NextDouble());
        }
        return dense;
    }
}
