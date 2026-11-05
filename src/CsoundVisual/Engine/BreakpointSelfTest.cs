using System.Diagnostics;
using CsoundVisual.Audio;
using CsoundVisual.Editor;

namespace CsoundVisual.Engine;

/// <summary>
/// <c>--breakpoint-test &lt;file.csd&gt; &lt;line&gt; [shift]</c>: runs a .csd through the app's own WASAPI
/// engine (default output, shared mode, <b>muted</b>: the device only receives silence) with a
/// breakpoint on <c>line</c> and the live-value monitor enabled, and checks the whole cycle:
/// stop → variables → time frozen while paused without dropouts → next cycle → continue to
/// another note → (optional) live recompile shifted by <c>shift</c> lines with the breakpoint
/// following it → remove all → the performance runs to the end.
/// </summary>
/// <remarks>
/// <para>
/// Dispatched from <c>Program.cs</c> before any window is created; output goes to the console
/// (the app is a WinExe, so pipe it: <c>CsoundVisual.exe --breakpoint-test x.csd 30 | more</c>).
/// The test thread polls the engine's public state (<see cref="CsoundEngine.Breakpoints"/>,
/// <see cref="CsoundEngine.ScoreTime"/>, <see cref="CsoundEngine.Underruns"/>) exactly as the UI
/// does, so it exercises the real cross-thread hand-offs.
/// </para>
/// <para>
/// A second run checks that pressing Stop while paused ends the performance, this time with the
/// live-value monitor off so the session must initialise Csound's debugger by itself.
/// </para>
/// </remarks>
internal static class BreakpointSelfTest
{
    /// <summary>Time base for the log lines.</summary>
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>Prints a line prefixed with the elapsed time in seconds.</summary>
    static void Say(string text) => Console.WriteLine($"[{Clock.Elapsed.TotalSeconds,6:0.000}s] {text}");

    /// <summary>Entry point of the self-test.</summary>
    /// <param name="a">Arguments after the switch: CSD path, editor line (1-based), optional line shift for the recompile step.</param>
    /// <returns>0 when every check passed, 1 for bad usage / missing Csound / invalid line, 2 when a check failed.</returns>
    public static int Run(string[] a)
    {
        if (a.Length < 2 || !File.Exists(a[0]) || !int.TryParse(a[1], out int line))
        {
            Console.WriteLine("usage: --breakpoint-test <file.csd> <line> [shift]");
            return 1;
        }
        int shift = a.Length > 2 && int.TryParse(a[2], out int s) ? s : 0;
        if (!Interop.CsoundLocator.Initialize())
        {
            Console.WriteLine("Csound not found.");
            return 1;
        }
        string text = File.ReadAllText(a[0]).Replace("\r\n", "\n");
        var analyzer = new CsoundAnalyzer(text);
        if (BreakpointRules.Check(analyzer, line) is { } why)
            Say("rule check: " + why);
        if (analyzer.BlockAt(line) is not { Keyword: "instr" } block)
        {
            Say($"line {line} is not inside an instrument");
            return 1;
        }
        // One request per id of the instrument (an `instr 1, Voice` block has two ids). For a
        // whole CSD started from the top, editor lines and Csound's line numbers coincide.
        var ids = block.Ids.Select(i => i.Trim('"')).ToList();
        var named = analyzer.Blocks.Where(b => b.Keyword == "instr").SelectMany(b => b.Ids).Select(i => i.Trim('"'))
            .Where(i => !int.TryParse(i, out _)).ToList();
        int tagLine = Enumerable.Range(1, analyzer.LineCount).FirstOrDefault(l => analyzer.LineText(l).Contains("<CsInstruments>", StringComparison.OrdinalIgnoreCase));

        var (csd, options) = CsdTools.ExtractOptions(text);
        options.Add("-odac");
        options.Add("-d");
        var engine = new CsoundEngine { MuteOutput = true };
        // Like the app's sliders: every detected channel starts at its default value.
        foreach (var channel in CsdTools.DetectChannels(text))
            engine.SetControl(channel.Name, channel.Default);
        var session = new BreakpointSession([.. ids.Select(id => new BreakpointRequest(id, line))]);
        engine.UseBreakpoints(session);
        using var done = new ManualResetEventSlim();
        int code = -1;
        engine.Finished += c => { code = c; done.Set(); };
        Say($"start {Path.GetFileName(a[0])} · breakpoint line {line} in {block.Label} · <CsInstruments> on line {tagLine}");
        engine.Start(new EngineJob(csd, options, Path.GetDirectoryName(Path.GetFullPath(a[0])),
            new HostAudioSettings(null, WasapiMode.Shared, LatencyPreset.Low), LiveValues: true, InstrumentNames: named));

        int failures = 0;
        void Check(bool ok, string what)
        {
            Say((ok ? "PASS " : "FAIL ") + what);
            if (!ok) failures++;
        }

        // Polls (every 5 ms) for a stop newer than `afterSerial`; null on timeout or when the run ended.
        BreakpointHit? WaitHit(int afterSerial, double seconds)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds && !done.IsSet)
            {
                if (engine.Breakpoints is { IsPaused: true, Current: { } h } && h.Serial > afterSerial)
                    return h;
                Thread.Sleep(5);
            }
            return null;
        }

        void Print(BreakpointHit h)
        {
            Say($"HIT #{h.Serial}: line {h.Line} before «{h.Opcode}» · instr {h.Instrument} · note {h.InstanceIndex}/{h.InstanceCount} · p1 {h.InstrumentNumber} p2 {h.P2:0.###} p3 {h.P3:0.###} · t {h.ScoreTime:0.0000} · k {h.KCycle}");
            foreach (var v in h.Locals)
                Console.WriteLine($"           {v.Name,-12} {v.Type,-4} {v.Value}{(v.Detail.Length > 0 ? "   | " + v.Detail : "")}");
            foreach (var v in h.Globals)
                Console.WriteLine($"   global  {v.Name,-12} {v.Type,-4} {v.Value}{(v.Detail.Length > 0 ? "   | " + v.Detail : "")}");
        }

        // 1. First stop.
        var hit1 = WaitHit(0, 10);
        Check(hit1 is not null && hit1.Line == line, $"stops at line {line}");
        if (hit1 is null) { engine.Stop(); done.Wait(5000); Dump(engine); return 2; }
        Print(hit1);

        // 2. Paused: score time frozen, no dropouts while the device keeps running on silence.
        double t0 = engine.ScoreTime;
        int u0 = engine.Underruns;
        Thread.Sleep(700);
        Check(Math.Abs(engine.ScoreTime - t0) < 1e-9 && engine.IsPausedAtBreakpoint, $"paused 0.7 s: score time frozen at {t0:0.0000}");
        Check(engine.Underruns == u0, $"no dropouts while paused (underruns {engine.Underruns})");

        // 3. Next control cycle.
        session.StepCycle();
        var hit2 = WaitHit(hit1.Serial, 5);
        Check(hit2 is not null && hit2.Line == line && hit2.P2 == hit1.P2 && hit2.KCycle == hit1.KCycle + 1,
            $"step → same note, next k-cycle (k {hit1.KCycle} → {hit2?.KCycle})");
        if (hit2 is not null) Print(hit2);
        var last = hit2 ?? hit1;

        // 4. Continue → another note.
        session.Continue();
        var hit3 = WaitHit(last.Serial, 8);
        Check(hit3 is not null && (hit3.P2 != hit1.P2 || hit3.InstrumentNumber != hit1.InstrumentNumber),
            $"continue → stops in another note (p2 {hit1.P2:0.###} → {hit3?.P2:0.###})");
        if (hit3 is not null) { Print(hit3); last = hit3; }

        // 5. Live recompile as if `shift` lines had been inserted above the instrument.
        if (shift != 0 && hit3 is not null)
        {
            int newStart = block.StartLine + shift;
            int orcStart = engine.CompileOrcAt(analyzer.BlockText(block), newStart, tagLine);
            int newLine = line + shift;
            session.SetBreakpoints([.. ids.Select(id => new BreakpointRequest(id, newLine))]);
            Say($"recompiled {block.Label} at editor line {newStart} (Csound numbers it from {orcStart}); breakpoint moved to {newLine}");
            session.Continue();
            var hit4 = WaitHit(last.Serial, 8);
            Check(hit4 is not null && hit4.Line == newLine, $"after recompile stops at line {newLine} (got {hit4?.Line})");
            if (hit4 is not null) { Print(hit4); last = hit4; }
        }

        // 6. Remove all and let it play to the end.
        session.SetBreakpoints([]);
        session.Continue();
        var extra = WaitHit(last.Serial, 2.5);
        Check(extra is null, "after removing all breakpoints it does not stop again");
        int liveCount = engine.Live?.Values.Count ?? 0;
        Check(liveCount > 0, $"live-value monitor works alongside ({liveCount} values sampled)");
        if (!done.Wait(TimeSpan.FromSeconds(30)))
        {
            Say("still playing after 30 s: stopping");
            engine.Stop();
            done.Wait(5000);
        }
        Check(code == 0, $"performance finished with result {code}");
        Dump(engine);
        Say($"first run: underruns {engine.Underruns} · worst wake {engine.WorstWakeMs:0.##} ms · DSP {engine.DspLoad * 100:0.#} %");

        // 7. A second run: Stop while paused must end the performance cleanly.
        var session2 = new BreakpointSession([.. ids.Select(id => new BreakpointRequest(id, line))]);
        engine.UseBreakpoints(session2);
        done.Reset();
        engine.Start(new EngineJob(csd, options, Path.GetDirectoryName(Path.GetFullPath(a[0])),
            new HostAudioSettings(null, WasapiMode.Shared, LatencyPreset.Low), LiveValues: false, InstrumentNames: named));
        var again = WaitHit(0, 10);
        Check(again is not null, "second run (live values off: the session enables the debugger itself) stops again");
        var stopClock = Stopwatch.StartNew();
        engine.Stop();
        Check(done.Wait(TimeSpan.FromSeconds(5)), $"Stop while paused ends the run ({stopClock.ElapsedMilliseconds} ms, result {code})");
        Dump(engine);
        Say($"{failures} failure(s)");
        return failures == 0 ? 0 : 2;
    }

    /// <summary>Drains the engine's console queue, printing only errors, warnings, breakpoint (⏸) and stream (🔊) lines.</summary>
    static void Dump(CsoundEngine engine)
    {
        while (engine.Messages.TryDequeue(out var m))
            if (m.Kind is MessageKind.Error or MessageKind.Warning || m.Text.StartsWith("⏸", StringComparison.Ordinal) || m.Text.StartsWith("🔊", StringComparison.Ordinal))
                Console.WriteLine("    console: " + m.Text);
    }
}
