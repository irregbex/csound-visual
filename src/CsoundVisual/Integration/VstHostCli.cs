using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using CsoundVisual.Audio;
using CsoundVisual.Engine;

namespace CsoundVisual.Integration;

/// <summary>
/// Command-line modes of the VST3 host (no UI is opened):
/// <list type="bullet">
/// <item><c>--vst3-scan-worker &lt;list&gt; &lt;out&gt;</c> — internal: the scanner's worker process.</item>
/// <item><c>--vst3-list [full]</c> — scans the standard VST3 folders and prints every plug-in.</item>
/// <item><c>--vsthost-test &lt;path.vst3&gt; [class name] [seconds] [--set "Title=0.9"]… [--editor &lt;capture.bmp&gt;]</c> — loads a
/// plug-in, processes a 440 Hz sine through it in memory (nothing is played) and prints
/// peak/RMS before and after, its parameters, a state round trip and, with <c>--editor</c>, opens
/// its window, checks it is visible and sized, captures it with PrintWindow and closes it with WM_CLOSE.
/// <c>--set</c> sets a parameter (by title, normalised 0…1) before processing.</item>
/// <item><c>--vsthost-engine-test &lt;path.vst3&gt;[|&lt;path2.vst3&gt;] [seconds]</c> — plays the demo through
/// the real WASAPI engine (default output, volume channel at 5 %, at most 3 s) twice, without and
/// with the chain, recording each run to a WAV, and compares the levels.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>Invoked from <c>Program.cs</c> before Avalonia starts, so these modes run on the console's
/// main thread, which here plays the role of the "UI thread" required by <c>vsthost.h</c>; editor
/// windows are kept alive with <see cref="VstHostNative.vh_pump"/> since there is no message loop.</para>
/// <para>Exit codes of <c>--vsthost-test</c>: 0 pass, 1 bridge missing, 2 no class, 3 load failed,
/// 4 non-finite output or crash, 5 editor did not open, 6 editor not visible/sized or did not close.
/// <c>--vsthost-engine-test</c>: 0 pass, 1 bridge/Csound missing, 2 no class, 3 load failed,
/// 4 a run failed, 5 nothing recorded.</para>
/// <para>Environment switches: <c>CSV_VSTTEST_THREAD=&lt;MB&gt;</c> runs <c>--vsthost-test</c> on a new
/// STA thread with that stack size (used to investigate plug-ins that crash on the default thread);
/// <c>CSV_VSTTEST_VERBOSE</c> prints every Csound message of the engine test.</para>
/// </remarks>
public static unsafe class VstHostCli
{
    /// <summary>Runs a mode when <paramref name="args"/> asks for one.</summary>
    /// <returns>True when a mode ran (the process should exit with <paramref name="code"/>).</returns>
    public static bool TryRun(string[] args, out int code)
    {
        code = 0;
        int i;
        if ((i = Array.IndexOf(args, "--vst3-scan-worker")) >= 0 && args.Length > i + 2)
        {
            code = VstScanner.RunWorker(args[i + 1], args[i + 2]);
            return true;
        }
        if ((i = Array.IndexOf(args, "--vst3-list")) >= 0)
        {
            code = List(args.Skip(i + 1).Contains("full"));
            return true;
        }
        if ((i = Array.IndexOf(args, "--vsthost-test")) >= 0 && args.Length > i + 1)
        {
            if (Environment.GetEnvironmentVariable("CSV_VSTTEST_THREAD") is { } stackMb)
            {
                // Diagnostics: run on a fresh STA thread with a given stack size (MB).
                int result = 0;
                var t = new Thread(() => result = PluginTest(args.Skip(i + 1).ToArray()), int.Parse(stackMb) * 1024 * 1024);
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                t.Join();
                code = result;
            }
            else
                code = PluginTest(args.Skip(i + 1).ToArray());
            return true;
        }
        if ((i = Array.IndexOf(args, "--vsthost-engine-test")) >= 0 && args.Length > i + 1)
        {
            code = EngineTest(args.Skip(i + 1).ToArray());
            return true;
        }
        return false;
    }

    /// <summary>Prints why the native bridge is missing; true when it is usable.</summary>
    static bool CheckBridge()
    {
        if (VstHostNative.IsAvailable) return true;
        Console.WriteLine("VST3 host bridge not available: " + VstHostNative.LoadError);
        return false;
    }

    /// <summary><c>--vst3-list</c>: scans (through the cache and worker processes) and prints every class.</summary>
    /// <param name="full">Ignore the cache and re-inspect every module.</param>
    static int List(bool full)
    {
        if (!CheckBridge()) return 1;
        var sw = Stopwatch.StartNew();
        var modules = VstScanner.Scan(full, new Progress<string>(_ => { }), CancellationToken.None);
        int fx = 0, inst = 0;
        foreach (var m in modules)
        {
            if (m.Error is not null)
                Console.WriteLine($"  ✗ {Path.GetFileName(m.Path)}: {m.Error}");
            foreach (var c in m.Classes)
            {
                Console.WriteLine($"  {(c.IsInstrument ? "♪" : "·")} {c.Name}  [{c.Vendor}]  {c.SubCategories}  {c.ClassId}  {Path.GetFileName(c.Path)}");
                if (c.IsInstrument) inst++; else fx++;
            }
        }
        Console.WriteLine($"{modules.Count} modules · {fx} effects · {inst} instruments · {modules.Count(m => m.Error is not null)} with errors · {sw.Elapsed.TotalSeconds:0.0} s · cache {VstScanner.CachePath}");
        return 0;
    }

    /// <summary>Chooses a class of a module: by (partial) name, else the first effect, else the first class.</summary>
    static VstPluginInfo? PickClass(string path, string? name)
    {
        var (ok, json) = VstHostNative.ScanModule(path);
        if (!ok)
        {
            Console.WriteLine("scan failed: " + json);
            return null;
        }
        var entry = VstScanner.Parse(path, json);
        foreach (var c in entry.Classes)
            Console.WriteLine($"  class: {c.Name} [{c.Vendor}] {c.SubCategories} {c.ClassId}");
        return (name is null ? null : entry.Classes.FirstOrDefault(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
               ?? entry.Classes.FirstOrDefault(c => !c.IsInstrument) ?? entry.Classes.FirstOrDefault();
    }

    /// <summary>Peak (max |x|) and RMS over both channels of a stereo range, linear full-scale units.</summary>
    static (double Peak, double Rms) Measure(float[] l, float[] r, int from, int count)
    {
        double peak = 0, sum = 0;
        for (int k = from; k < from + count; k++)
        {
            peak = Math.Max(peak, Math.Max(Math.Abs(l[k]), Math.Abs(r[k])));
            sum += l[k] * (double)l[k] + r[k] * (double)r[k];
        }
        return (peak, Math.Sqrt(sum / Math.Max(1, 2 * count)));
    }

    /// <summary>Formats a linear amplitude as dBFS (20·log10), or "-inf" for silence.</summary>
    static string Db(double v) => v > 1e-9 ? (20 * Math.Log10(v)).ToString("0.0", CultureInfo.InvariantCulture) + " dBFS" : "-inf";

    /// <summary><c>--vsthost-test</c>: offline load / process / parameters / state / editor check of one plug-in.</summary>
    /// <param name="a">Arguments after the switch; the first is the module path. Of the remaining
    /// free arguments, the first non-number is the class name filter and the first positive number
    /// the duration in seconds (default 2).</param>
    /// <returns>Exit code (see the class remarks).</returns>
    static int PluginTest(string[] a)
    {
        if (!CheckBridge()) return 1;
        string path = a[0];
        string? editorShot = null;
        var rest = new List<string>();
        var sets = new List<string>(); // --set "Parameter title=normalized value"
        for (int k = 1; k < a.Length; k++)
        {
            if (a[k] == "--editor" && k + 1 < a.Length) { editorShot = a[++k]; continue; }
            if (a[k] == "--set" && k + 1 < a.Length) { sets.Add(a[++k]); continue; }
            rest.Add(a[k]);
        }
        string? name = rest.FirstOrDefault(s => !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        double seconds = rest.Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : -1).FirstOrDefault(v => v > 0, 2);

        Console.WriteLine($"module: {path}");
        var info = PickClass(path, name);
        if (info is null) { Console.WriteLine("no class found"); return 2; }
        Console.WriteLine($"testing: {info.Name} ({(info.IsInstrument ? "instrument" : "effect")})");

        var chain = new VstChain();
        var sw = Stopwatch.StartNew();
        var slot = chain.Add(path, info.ClassId, out string? error);
        if (slot is null) { Console.WriteLine("load failed: " + error); return 3; }
        Console.WriteLine($"loaded in {sw.ElapsedMilliseconds} ms · {VstHostNative.Info(slot.Handle)}");

        const double sr = 48000;
        const int block = 64; // like a Csound ksmps
        chain.Prepare(sr, block);
        foreach (var s in sets)
        {
            int eq = s.LastIndexOf('=');
            if (eq <= 0 || !double.TryParse(s[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) continue;
            var p = slot.Parameters().FirstOrDefault(p => p.Title.Equals(s[..eq], StringComparison.OrdinalIgnoreCase));
            if (p.Title is null) { Console.WriteLine($"set: no parameter '{s[..eq]}'"); continue; }
            slot.SetParam(p.Id, v);
            Console.WriteLine($"set: {p.Title} = {v} ({slot.ParamText(p.Id, v)})");
        }
        int frames = (int)(sr * seconds);
        var inL = new float[frames]; var inR = new float[frames];
        for (int k = 0; k < frames; k++)
            inL[k] = inR[k] = (float)(0.25 * Math.Sin(2 * Math.PI * 440 * k / sr));
        var outL = (float[])inL.Clone(); var outR = (float[])inR.Clone();

        // Instruments get silence in and a middle C (note on, velocity 100) that is released half
        // way through, so "after" measures the instrument's own sound.
        if (slot.IsInstrument)
        {
            chain.SendMidi(0x90, 60, 100);
            Array.Clear(outL); Array.Clear(outR); Array.Clear(inL); Array.Clear(inR);
        }
        sw.Restart();
        fixed (float* l = outL, r = outR)
            for (int k = 0; k + block <= frames; k += block)
            {
                if (slot.IsInstrument && k == frames / 2) chain.SendMidi(0x80, 60, 0);
                chain.Process(l + k, r + k, block);
            }
        double cpu = sw.Elapsed.TotalSeconds;
        var before = Measure(inL, inR, 0, frames);
        var after = Measure(outL, outR, 0, frames);
        double diff = 0;
        for (int k = 0; k < frames; k++) diff += (outL[k] - inL[k]) * (double)(outL[k] - inL[k]);
        bool finite = outL.All(float.IsFinite) && outR.All(float.IsFinite);
        Console.WriteLine($"input : peak {before.Peak:0.0000} ({Db(before.Peak)})  rms {before.Rms:0.0000} ({Db(before.Rms)})");
        Console.WriteLine($"output: peak {after.Peak:0.0000} ({Db(after.Peak)})  rms {after.Rms:0.0000} ({Db(after.Rms)})  finite {finite}");
        Console.WriteLine($"difference rms {Math.Sqrt(diff / frames):0.0000} · processed {seconds:0.##} s in {cpu * 1000:0} ms ({seconds / Math.Max(cpu, 1e-6):0}x real time) · crashed {slot.Crashed}");

        var ps = slot.Parameters();
        Console.WriteLine($"parameters: {ps.Count}");
        foreach (var p in ps.Where(p => !p.IsHidden).Take(8))
            Console.WriteLine($"  [{p.Id}] {p.Title} = {slot.GetParam(p.Id):0.###} ({slot.ParamText(p.Id, slot.GetParam(p.Id))})");

        // State round trip into a second instance.
        var state = slot.GetState();
        Console.WriteLine($"state: {state?.Length ?? 0} bytes");
        if (state is not null)
        {
            var copy = chain.Add(path, info.ClassId, out string? err2, state);
            Console.WriteLine(copy is null ? "state round trip: load failed " + err2 : $"state round trip: restored into a new instance ({err2 ?? "ok"})");
            if (copy is not null) chain.Remove(copy);
        }

        int result = finite && !slot.Crashed ? 0 : 4;
        if (editorShot is not null)
            result = Math.Max(result, EditorTest(slot, editorShot));
        chain.Clear();
        Console.WriteLine(result == 0 ? "PASS" : "FAIL " + result);
        return result;
    }

    // Minimal user32 imports for the editor check (classic DllImport: test-only, not hot).
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }

    /// <summary>Opens the editor, checks it, captures it with PrintWindow and closes it with WM_CLOSE.</summary>
    static int EditorTest(VstSlot slot, string shot)
    {
        Console.WriteLine($"editor: has editor = {slot.HasEditor}");
        if (!slot.HasEditor) return 0;
        IntPtr hwnd = slot.OpenEditor(IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { Console.WriteLine("editor: open FAILED"); return 5; }
        VstHostNative.vh_pump(1500); // let it draw
        GetClientRect(hwnd, out var rc);
        bool visible = IsWindowVisible(hwnd);
        int drawn = VstHostNative.vh_capture_window(hwnd, shot);
        Console.WriteLine($"editor: hwnd 0x{hwnd:X} visible {visible} client {rc.Right}x{rc.Bottom} · captured {shot} ({drawn} non-black pixels)");
        PostMessageW(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        VstHostNative.vh_pump(300);
        bool closed = !IsWindow(hwnd) && !slot.IsEditorOpen;
        Console.WriteLine($"editor: closed with WM_CLOSE = {closed}");
        return visible && rc.Right > 0 && rc.Bottom > 0 && closed ? 0 : 6;
    }

    /// <summary><c>--vsthost-engine-test</c>: the embedded demo through the real engine, dry then with the chain.</summary>
    /// <param name="a">First: module paths separated by <c>|</c> (chain order); optional second: seconds per run (≤ 3, default 1.5).</param>
    /// <returns>Exit code (see the class remarks).</returns>
    static int EngineTest(string[] a)
    {
        if (!CheckBridge()) return 1;
        if (!Interop.CsoundLocator.Initialize()) { Console.WriteLine("Csound not found."); return 1; }
        var paths = a[0].Split('|', StringSplitOptions.RemoveEmptyEntries);
        double seconds = a.Length > 1 && double.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? Math.Min(s, 3) : 1.5;
        string csd;
        using (var st = typeof(VstHostCli).Assembly.GetManifestResourceStream("CsoundVisual.Assets.Demo.csd")!)
        using (var rd = new StreamReader(st))
            csd = rd.ReadToEnd();

        var chain = new VstChain();
        foreach (var p in paths)
        {
            var info = PickClass(p, null);
            if (info is null) return 2;
            var slot = chain.Add(p, info.ClassId, out string? err);
            Console.WriteLine(slot is null ? $"load failed: {info.Name}: {err}" : $"chain + {slot.Name}");
            if (slot is null) return 3;
        }

        var dry = RunEngine(csd, null, seconds, "dry");
        var wet = RunEngine(csd, chain, seconds, "chain");
        chain.Clear();
        if (dry is null || wet is null) return 4;
        Console.WriteLine($"dry  : peak {dry.Value.Peak:0.0000} rms {dry.Value.Rms:0.00000} ({Db(dry.Value.Rms)}) · {dry.Value.Frames} frames recorded");
        Console.WriteLine($"chain: peak {wet.Value.Peak:0.0000} rms {wet.Value.Rms:0.00000} ({Db(wet.Value.Rms)}) · {wet.Value.Frames} frames recorded");
        return wet.Value.Frames > 0 && double.IsFinite(wet.Value.Rms) ? 0 : 5;
    }

    /// <summary>
    /// Plays <paramref name="csd"/> through the app's shared-mode WASAPI engine (Low preset) with
    /// <paramref name="chain"/> as inserts, records the post-insert signal to a temporary WAV and
    /// measures it.
    /// </summary>
    /// <param name="csd">Orchestra text.</param>
    /// <param name="chain">Insert chain, or <c>null</c> for the dry reference run.</param>
    /// <param name="seconds">Recording length (wall-clock).</param>
    /// <param name="label">Tag for console lines and the WAV file name.</param>
    /// <returns>Peak, RMS and recorded frames, or <c>null</c> when the run failed.</returns>
    /// <remarks>Audible but quiet: the default output plays the demo at 5 % volume.</remarks>
    static (double Peak, double Rms, long Frames)? RunEngine(string csd, VstChain? chain, double seconds, string label)
    {
        var engine = new CsoundEngine { Inserts = chain };
        using var done = new ManualResetEventSlim();
        int code = -1;
        engine.Finished += c => { code = c; done.Set(); };
        string wav = Path.Combine(Path.GetTempPath(), $"csv-vsthost-{label}-{Environment.ProcessId}.wav");
        engine.Start(new EngineJob(csd, ["-odac", "-d", "-m0"], null, new HostAudioSettings(null, WasapiMode.Shared, LatencyPreset.Low)));
        // The demo's sliders (chnk defaults are applied by the UI, not by Csound), with the
        // volume at 5 %: quiet, someone may be using the PC.
        foreach (var (channel, value) in new[] { ("cutoff", 1800.0), ("resonance", 0.35), ("detune", 0.15), ("tempo", 6.0), ("reverb", 0.35), ("volume", 0.05) })
            engine.SetControl(channel, value);
        var sw = Stopwatch.StartNew();
        while (engine.SampleRate <= 0 && sw.Elapsed.TotalSeconds < 5) Thread.Sleep(5);
        engine.StartRecording(wav);
        // The demo plays for an hour: stop it after `seconds` (a short, quiet run).
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        engine.Stop();
        done.Wait(TimeSpan.FromSeconds(10));
        engine.StopRecording();
        while (engine.Messages.TryDequeue(out var line))
            if (line.Kind is MessageKind.Info or MessageKind.Error or MessageKind.Warning || Environment.GetEnvironmentVariable("CSV_VSTTEST_VERBOSE") is not null)
                Console.WriteLine($"  [{label}] {line.Text}");
        Console.WriteLine($"  [{label}] result {code} · DSP {engine.DspLoad * 100:0.#} % · dropouts {engine.Underruns}");
        if (code != 0) return null;
        return ReadWav(wav);
    }

    /// <summary>Level of a 32-bit float WAV written by <see cref="WavRecorder"/>.</summary>
    static (double Peak, double Rms, long Frames)? ReadWav(string path)
    {
        try
        {
            // Minimal RIFF walk: skip the 12-byte "RIFF size WAVE" header, then chunks of
            // id(4) + size(4) + payload padded to an even length. fmt: channels at offset 10
            // (8 header + 2 format tag). data: float32 samples, interleaved.
            var bytes = File.ReadAllBytes(path);
            int pos = 12, channels = 2;
            while (pos + 8 <= bytes.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
                int size = BitConverter.ToInt32(bytes, pos + 4);
                if (id == "fmt ") channels = BitConverter.ToInt16(bytes, pos + 10);
                if (id == "data")
                {
                    int n = Math.Min(size, bytes.Length - pos - 8) / 4;
                    double peak = 0, sum = 0;
                    for (int k = 0; k < n; k++)
                    {
                        float v = BitConverter.ToSingle(bytes, pos + 8 + k * 4);
                        peak = Math.Max(peak, Math.Abs(v));
                        sum += v * (double)v;
                    }
                    File.Delete(path);
                    return (peak, Math.Sqrt(sum / Math.Max(1, n)), n / Math.Max(1, channels));
                }
                pos += 8 + size + (size & 1);
            }
        }
        catch (Exception ex) { Console.WriteLine("wav: " + ex.Message); }
        return null;
    }
}
