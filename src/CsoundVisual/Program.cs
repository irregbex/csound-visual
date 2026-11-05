using Avalonia;
using CsoundVisual.Audio;

namespace CsoundVisual;

/// <summary>
/// Process entry point: either starts the Avalonia UI or runs one of the head-less command-line
/// diagnostic / self-test modes and exits with a status code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Command-line test modes.</b> Each mode is selected by a switch anywhere in the argument list;
/// the arguments <i>after</i> the switch are passed to it (<c>args.Skip(index + 1)</c>). They exist so
/// the audio engine, the live input, breakpoints, VST3, Ableton Link and the Paint Rooms can be
/// verified from a terminal or a script without opening a window and, except where the mode says
/// otherwise, without anything audible (the orchestras output silence or the output is muted).
/// The project is a <c>WinExe</c>, so their <see cref="Console"/> output is seen when standard output
/// is redirected or piped. Exit code 0 = success; 1 = Csound not found (where checked); other values
/// = failure, as documented by each mode.
/// </para>
/// <para>
/// The first match wins, in the order tested by <see cref="Main"/>. Only when no switch matches is the
/// UI started, and then the full <c>args</c> array reaches Avalonia (and through it
/// <see cref="App"/>, which opens a file passed as the first argument).
/// </para>
/// <para>
/// <b>Threading.</b> WASAPI and MMDevice COM objects are created on dedicated MTA threads, exactly as
/// the real engine does (<see cref="Engine.CsoundEngine"/> runs its audio thread in the MTA), because
/// <see cref="Main"/> itself is <c>[STAThread]</c> for Avalonia / OLE drag-and-drop.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>
    /// Dispatches the command line: runs a diagnostic mode when its switch is present, otherwise
    /// starts Avalonia with the classic desktop lifetime (one main window; the process ends when it
    /// closes).
    /// </summary>
    /// <param name="args">Raw command-line arguments.</param>
    /// <returns>The process exit code (0 = success).</returns>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>--audio-probe</c>: prints what every audio output can do (formats, shared and
    /// exclusive latency) and exits without opening the UI or playing anything.</item>
    /// <item><c>--audio-test</c>, <c>--input-probe</c>, <c>--input-test</c>: see
    /// <see cref="AudioSelfTest"/>, <see cref="InputProbe"/>, <see cref="InputSelfTest"/>.</item>
    /// <item><c>--breakpoint-test</c>, VST3 switches, <c>--link-test</c>, <c>--paint-test</c>,
    /// <c>--picture-test</c>, <c>--filter-test</c>: forwarded to the self-test classes named in the
    /// inline comments below.</item>
    /// </list>
    /// </remarks>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--audio-probe"))
        {
            // COM objects are used from a dedicated MTA thread, like the audio engine does
            // (Main is [STAThread]; SetApartmentState below makes the choice explicit).
            var t = new Thread(() =>
            {
                foreach (var device in WasapiOutput.Probe())
                    Console.WriteLine(device + "\n");
            });
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            t.Join();
            return 0;
        }
        // "is var x and >= 0" captures the switch position and tests it in one expression;
        // everything after the switch becomes the mode's own argument list.
        if (Array.IndexOf(args, "--audio-test") is var at and >= 0)
            return AudioSelfTest(args.Skip(at + 1).ToArray());
        if (Array.IndexOf(args, "--input-probe") is var ip and >= 0)
            return InputProbe(args.Skip(ip + 1).ToArray());
        if (Array.IndexOf(args, "--input-test") is var it and >= 0)
            return InputSelfTest(args.Skip(it + 1).ToArray());
        // Breakpoints through the real (muted) WASAPI engine (Engine/BreakpointSelfTest.cs).
        if (Array.IndexOf(args, "--breakpoint-test") is var bt and >= 0)
            return Engine.BreakpointSelfTest.Run(args.Skip(bt + 1).ToArray());
        // VST3 host: scanner worker process and console tests (Integration/VstHostCli.cs).
        if (Integration.VstHostCli.TryRun(args, out int vstCode))
            return vstCode;
        // Ableton Link end-to-end check with a second peer process, silent (Integration/LinkSelfTest.cs).
        if (Array.IndexOf(args, "--link-test") is var lt and >= 0)
            return Integration.LinkSelfTest.Run(args.Skip(lt + 1).ToArray());
        // Paint Rooms: a 512×1024 image played live through the WASAPI engine, inaudible (Paint/PaintSelfTest.cs).
        if (Array.IndexOf(args, "--paint-test") is var pt and >= 0)
            return Paint.PaintSelfTest.Run(args.Skip(pt + 1).ToArray());
        // Paint Rooms: picture import check — decodes each file and prints the canvas each fit mode makes.
        if (Array.IndexOf(args, "--picture-test") is var pic and >= 0)
            return Paint.ImageCodec.SelfTest(args.Skip(pic + 1).ToArray());
        // Paint Rooms · Image Filter: file and live-input filtering through the WASAPI engine, inaudible (Paint/ImageFilterSelfTest.cs).
        if (Array.IndexOf(args, "--filter-test") is var ft and >= 0)
            return Paint.ImageFilterSelfTest.Run(args.Skip(ft + 1).ToArray());
        // No test switch: normal start. Avalonia owns the message loop until the main window closes.
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// <c>--audio-test [shared|exclusive] [minimum|low|normal] ["part of device name"]</c>:
    /// runs a silent 3-second Csound performance through the application's own WASAPI output and
    /// prints the opened stream, the DSP load and the number of dropouts. Nothing audible is played.
    /// </summary>
    /// <param name="a">Arguments after the switch: mode (anything starting with "ex" = exclusive,
    /// otherwise shared), <see cref="LatencyPreset"/> name (default Minimum), and a case-insensitive
    /// fragment of the output device name (default: the Windows default output).</param>
    /// <returns>0 when Csound finished with code 0; 1 when Csound is not installed; 2 otherwise
    /// (including the 15-second timeout, which leaves the code at −1).</returns>
    /// <remarks>
    /// This is the measurement behind the latency figures in the documentation: the engine's own
    /// counters (<see cref="Engine.CsoundEngine.DspLoad"/>, <see cref="Engine.CsoundEngine.Underruns"/>,
    /// <see cref="Engine.CsoundEngine.WorstWakeMs"/>) are printed after the run.
    /// </remarks>
    static int AudioSelfTest(string[] a)
    {
        // Loads csound7_64.dll from the installed Csound 7 (Interop/CsoundLocator.cs); nothing works without it.
        if (!Interop.CsoundLocator.Initialize())
        {
            Console.WriteLine("Csound no encontrado.");
            return 1;
        }
        var mode = a.Length > 0 && a[0].StartsWith("ex", StringComparison.OrdinalIgnoreCase) ? WasapiMode.Exclusive : WasapiMode.Shared;
        var preset = a.Length > 1 ? Enum.Parse<LatencyPreset>(a[1], ignoreCase: true) : LatencyPreset.Minimum;
        // null = default output endpoint; an unmatched name also falls back to the default.
        string? deviceId = null;
        if (a.Length > 2)
            deviceId = WasapiOutput.ListEndpoints().FirstOrDefault(e => e.Id is not null && e.Name.Contains(a[2], StringComparison.OrdinalIgnoreCase))?.Id;

        // Csound requires every <Cs…> tag on its own line. 48 kHz, ksmps 32 (0.67 ms control period),
        // one instrument writing zeros for 3 seconds: the engine does all its real work, the speakers get silence.
        const string silent = "<CsoundSynthesizer>\n<CsInstruments>\nsr = 48000\nksmps = 32\nnchnls = 2\n0dbfs = 1\n" +
                              "instr 1\n  aZero = 0\n  out aZero, aZero\nendin\n</CsInstruments>\n<CsScore>\ni 1 0 3\n</CsScore>\n</CsoundSynthesizer>\n";
        var engine = new Engine.CsoundEngine();
        using var done = new ManualResetEventSlim();
        int code = -1;
        // Finished is raised on the performance thread when the score ends; the main thread just waits.
        engine.Finished += c => { code = c; done.Set(); };
        // -odac = real-time output; with HostAudioSettings the engine calls csoundSetHostAudioIO, so the
        // samples go to the app's own WASAPI output (Audio/WasapiOutput.cs), not to Csound's RT module;
        // -d = no graphical displays, -m0 = minimal console messages.
        engine.Start(new Engine.EngineJob(silent, ["-odac", "-d", "-m0"], null, new Engine.HostAudioSettings(deviceId, mode, preset)));
        done.Wait(TimeSpan.FromSeconds(15));
        // Csound's messages were queued by the message callback; print only the meaningful kinds.
        while (engine.Messages.TryDequeue(out var line))
            if (line.Kind is Engine.MessageKind.Info or Engine.MessageKind.Error or Engine.MessageKind.Warning)
                Console.WriteLine(line.Text);
        Console.WriteLine($"resultado {code} · DSP {engine.DspLoad * 100:0.#} % · cortes {engine.Underruns} · peor despertar {engine.WorstWakeMs:0.##} ms");
        return code == 0 ? 0 : 2;
    }

    /// <summary>
    /// <c>--input-probe ["part of device name" | loopback] [seconds]</c>: lists the capture endpoints,
    /// then records from the chosen one (default: the Windows default recording device; <c>loopback</c>
    /// = what the default output is playing) for a few seconds and prints its peak level every
    /// 0.25 s. Nothing is played.
    /// </summary>
    /// <param name="a">Arguments after the switch: device-name fragment or <c>loopback</c> (empty or
    /// missing = default capture device), then the duration in seconds (invariant culture, default 2).</param>
    /// <returns>0 on success, 2 when the device could not be opened or capture failed.</returns>
    /// <remarks>
    /// Csound is not involved: this checks only <see cref="WasapiInput"/> and its
    /// <see cref="InputRingBuffer"/>. The probe thread plays the consumer role that the Csound
    /// performance thread normally plays (draining <see cref="WasapiInput.Ring"/>), so the "≈ Hz"
    /// figure is the effective capture rate and reveals clock or packet problems.
    /// </remarks>
    static int InputProbe(string[] a)
    {
        int code = 0;
        // COM objects are used from MTA threads, like the audio engine does.
        var t = new Thread(() =>
        {
            var endpoints = WasapiInput.ListEndpoints();
            Console.WriteLine("Capture endpoints:");
            foreach (var e in endpoints)
                Console.WriteLine($"  {e.Name}{(e.Id is null ? "" : "  [" + e.Id + "]")}");
            string? id = a.Length > 0 && a[0].Length > 0
                ? endpoints.FirstOrDefault(e => e.Id is not null && e.Name.Contains(a[0], StringComparison.OrdinalIgnoreCase))?.Id
                : null;
            double seconds = a.Length > 1 && double.TryParse(a[1], System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 2;
            try
            {
                bool loopback = a.Length > 0 && a[0].Equals("loopback", StringComparison.OrdinalIgnoreCase);
                using var input = loopback ? WasapiInput.OpenLoopback(null) : WasapiInput.Open(id, LatencyPreset.Low);
                if (loopback)
                    Console.WriteLine("(loopback of the default output: what Windows is playing)");
                Console.WriteLine(input.Description);
                input.Start();
                var peaks = new float[WasapiInput.MaxMeterChannels];
                var frame = new float[input.Channels];
                long frames0 = 0;
                // One report every 250 ms, so seconds × 4 iterations.
                for (int i = 0; i < seconds * 4; i++)
                {
                    Thread.Sleep(250);
                    // Per-channel peak (linear, 0…1) since the previous call; n = channels written.
                    int n = input.TakePeaks(peaks);
                    // Act as the consumer: drain the ring and measure the RMS of the first channel.
                    int count = 0;
                    double sum = 0;
                    while (input.Ring.TryReadFrame(frame)) { sum += frame[0] * (double)frame[0]; count++; }
                    frames0 += count;
                    double rms = count > 0 ? Math.Sqrt(sum / count) : 0;
                    Console.WriteLine($"  t={(i + 1) * 0.25:0.00}s  frames={count,6}  rms ch1 {(rms > 1e-7 ? $"{20 * Math.Log10(rms),6:0.0}" : "  -inf")} dBFS  peaks dBFS: " +
                        string.Join("  ", peaks.Take(n).Select(p => p > 1e-6f ? $"{20 * Math.Log10(p),6:0.0}" : "  -inf")));
                }
                Console.WriteLine($"captured {frames0} frames in {seconds:0.##} s ≈ {frames0 / seconds:0} Hz · glitches {input.Glitches} · dropped {input.Ring.DroppedFrames}" +
                    (input.Error is { } err ? " · error: " + err : ""));
            }
            catch (Exception ex)
            {
                Console.WriteLine("error: " + ex.Message);
                code = 2;
            }
        });
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();
        return code;
    }

    /// <summary>
    /// <c>--input-test ["part of input name" | loopback] [seconds]</c>: runs the full engine path — the app's
    /// WASAPI output (default device) plus the live input feeding Csound's <c>in</c> — with an
    /// orchestra whose output is pure silence, and prints the RMS that Csound measures on its input
    /// every half second. Nothing audible is played.
    /// </summary>
    /// <param name="a">Arguments after the switch: input-name fragment or <c>loopback</c> (empty or
    /// missing = default capture device), then the duration in whole seconds (default 3).</param>
    /// <returns>0 when Csound finished with code 0; 1 when Csound is not installed; 2 otherwise.</returns>
    /// <remarks>
    /// Besides Csound's own <c>printks</c> lines, the main thread polls
    /// <see cref="Engine.CsoundEngine.InputReader"/> every 500 ms and prints the state of the adaptive
    /// input reader (ring fill against its target, the drift correction in ppm that keeps the capture
    /// and render clocks aligned, dropouts and overruns). The loop gives up 15 s after the expected end.
    /// </remarks>
    static int InputSelfTest(string[] a)
    {
        if (!Interop.CsoundLocator.Initialize())
        {
            Console.WriteLine("Csound not found.");
            return 1;
        }
        // "loopback" = what the default output plays (diagnostics; works even with the microphone blocked).
        bool loopback = a.Length > 0 && a[0].Equals("loopback", StringComparison.OrdinalIgnoreCase);
        string? id = !loopback && a.Length > 0 && a[0].Length > 0
            ? WasapiInput.ListEndpoints().FirstOrDefault(e => e.Id is not null && e.Name.Contains(a[0], StringComparison.OrdinalIgnoreCase))?.Id
            : null;
        int seconds = a.Length > 1 && int.TryParse(a[1], out int s) ? s : 3;
        // instr 1 reads both input channels, prints their RMS every 0.5 s (printks) and outputs silence.
        // The "\\n" in the C# literal becomes "\n" inside the Csound string, i.e. printks' own newline escape.
        string csd = "<CsoundSynthesizer>\n<CsInstruments>\nsr = 48000\nksmps = 32\nnchnls = 2\n0dbfs = 1\n" +
                     "instr 1\n  ain1, ain2 in\n  kL rms ain1\n  kR rms ain2\n  printks \"input rms L %.5f  R %.5f\\n\", 0.5, kL, kR\n" +
                     "  aZero = 0\n  out aZero, aZero\nendin\n</CsInstruments>\n<CsScore>\ni 1 0 " + seconds + "\n</CsScore>\n</CsoundSynthesizer>\n";
        var engine = new Engine.CsoundEngine();
        using var done = new ManualResetEventSlim();
        int code = -1;
        engine.Finished += c => { code = c; done.Set(); };
        engine.Start(new Engine.EngineJob(csd, ["-odac", "-d", "-m0"], null,
            new Engine.HostAudioSettings(null, WasapiMode.Shared, LatencyPreset.Low, new Engine.AudioInputSettings(id, loopback))));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // done.Wait(500) doubles as the 0.5 s polling interval; InputReader is null until the
        // performance thread has opened the input, hence the pattern match.
        while (!done.Wait(500))
        {
            if (engine.InputReader is { } r)
                Console.WriteLine($"  [{sw.Elapsed.TotalSeconds:0.0}s] ring fill {r.SmoothedFill:0} / target {r.TargetFrames} frames · drift correction {r.CorrectionPpm:+0;-0} ppm · input dropouts {r.Underruns} · overruns {r.Overruns}");
            if (sw.Elapsed.TotalSeconds > seconds + 15) break;
        }
        while (engine.Messages.TryDequeue(out var line))
            if (line.Kind is Engine.MessageKind.Info or Engine.MessageKind.Error or Engine.MessageKind.Warning || line.Text.Contains("rms"))
                Console.WriteLine(line.Text);
        Console.WriteLine($"result {code} · DSP {engine.DspLoad * 100:0.#} % · output dropouts {engine.Underruns}");
        return code == 0 ? 0 : 2;
    }

    /// <summary>
    /// Avalonia app builder; also used by the XAML previewer (which looks for a public static
    /// method with exactly this name and signature, so do not rename it).
    /// </summary>
    /// <returns>A builder for <see cref="App"/> with the platform back-end auto-detected (Win32 + Skia)
    /// and Avalonia's internal log sent to <see cref="System.Diagnostics.Trace"/>.</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
