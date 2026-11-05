using System.Diagnostics;
using System.Globalization;
using CsoundVisual.Audio;
using CsoundVisual.Engine;
using CsoundVisual.Music;

namespace CsoundVisual.Integration;

/// <summary>
/// <c>CsoundVisual.exe --link-test [seconds] [--peer path\to\link_peer_test.exe]</c>: an end-to-end
/// Ableton Link check without UI and without sound.
/// </summary>
/// <remarks>
/// <para>Starts a <see cref="LinkSession"/> at 120 BPM and a silent Csound performance through the
/// app's WASAPI output (the orchestra writes zeros), installs the session as the engine's cycle
/// hook, then launches <c>link_peer_test.exe</c> (a second Link peer in another process, built by
/// <c>tools/build_link.ps1</c>) that joins at 97 BPM, changes the tempo to 133 at 4 s, starts the
/// shared transport at 7 s, stops it at 9 s and changes the tempo to 126 at 10 s; this side
/// proposes 140 BPM at 6 s and starts a Link-locked tool transport at 7.5 s.</para>
/// <para>The peer prints one grid line every 100 ms, <c>G &lt;hostMicros&gt; &lt;peers&gt; &lt;tempo&gt;
/// &lt;beat&gt; …</c>; because both processes share Link's host clock, evaluating this side's
/// timeline at the same <c>hostMicros</c> gives a direct phase comparison. The orchestra prints a
/// <c>LINKCHK</c> line every 0.25 s with what it read from the <c>LINK_*</c> channels.</para>
/// <para>Checks: both sides see one peer; tempo converges both ways; the beat of the two
/// timelines at identical host times agrees (error printed in ms); the orchestra's
/// <c>LINK_*</c> channels follow the peer (tempo, peers, playing, beat rate, phase = beat mod 4);
/// and a <see cref="TransportScheduler"/> locked to Link starts on a downbeat and keeps every
/// note on an integer Link beat, across the tempo changes.</para>
/// </remarks>
public static class LinkSelfTest
{
    /// <summary>Beats per bar used by both peers.</summary>
    const double Quantum = 4;

    /// <summary>Runs the test; returns 0 when every check passed.</summary>
    /// <param name="a">Arguments after <c>--link-test</c>: optional duration in seconds (minimum 11,
    /// default 12) and <c>--peer &lt;path&gt;</c>.</param>
    /// <returns>0 = all passed; 1 = prerequisite missing (Link DLL, peer exe, Csound); 2 = Csound did
    /// not start; 3 = at least one check failed.</returns>
    /// <remarks>Runs on the console thread; the loop polls every 15 ms until the peer process exits
    /// (there is no extra timeout: a hung peer hangs the test).</remarks>
    public static int Run(string[] a)
    {
        var ci = CultureInfo.InvariantCulture;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        double seconds = a.Length > 0 && double.TryParse(a[0], NumberStyles.Float, ci, out double s) ? Math.Max(11, s) : 12;
        int pi = Array.IndexOf(a, "--peer");
        string? peerExe = pi >= 0 && pi + 1 < a.Length ? a[pi + 1] : FindPeer();
        var failures = new List<string>();
        void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what);
            if (!ok) failures.Add(what);
        }

        if (!AbletonLinkNative.IsAvailable)
        {
            Console.WriteLine("Link DLL not available: " + AbletonLinkNative.LoadError);
            return 1;
        }
        if (peerExe is null || !File.Exists(peerExe))
        {
            Console.WriteLine("link_peer_test.exe not found (build it with tools\\build_link.ps1, or pass --peer <path>).");
            return 1;
        }
        if (!Interop.CsoundLocator.Initialize())
        {
            Console.WriteLine("Csound not found.");
            return 1;
        }

        var link = LinkSession.TryCreate(120)!;
        link.Quantum = Quantum;
        link.StartStopSync = true;
        link.IsEnabled = true;
        Console.WriteLine($"Link on (120 BPM, quantum {Quantum}); peer: {peerExe}");

        // --- silent Csound performance with the LINK_* hook ---------------------------------
        // instr 1 reads and prints the channels; instr 2 is the empty target of the tool transport.
        int total = (int)Math.Ceiling(seconds + 3);
        string csd = "<CsoundSynthesizer>\n<CsInstruments>\nsr = 48000\nksmps = 32\nnchnls = 2\n0dbfs = 1\n" +
                     "instr 1\n  kbpm chnget \"LINK_BPM\"\n  kbeat chnget \"LINK_BEAT\"\n  kph chnget \"LINK_PHASE\"\n  kpl chnget \"LINK_PLAYING\"\n  kpe chnget \"LINK_PEERS\"\n" +
                     "  kt times\n  printks \"LINKCHK %.4f %.4f %.6f %.6f %d %d\\n\", 0.25, kt, kbpm, kbeat, kph, kpl, kpe\n" +
                     "  aZero = 0\n  out aZero, aZero\nendin\n" +
                     "instr 2\nendin\n" +
                     "</CsInstruments>\n<CsScore>\ni 1 0 " + total + "\n</CsScore>\n</CsoundSynthesizer>\n";
        var engine = new CsoundEngine { CycleHook = link };
        using var done = new ManualResetEventSlim();
        engine.Finished += _ => done.Set();
        engine.Start(new EngineJob(csd, ["-odac", "-d"], null, new HostAudioSettings(null, WasapiMode.Shared, LatencyPreset.Low)));
        var wait = Stopwatch.StartNew();
        while (engine.ScoreTime < 0.3 && wait.Elapsed.TotalSeconds < 10 && !done.IsSet) Thread.Sleep(20);
        if (engine.ScoreTime < 0.3)
        {
            Drain(engine, []);
            Console.WriteLine("Csound did not start.");
            return 2;
        }
        Console.WriteLine($"Csound running · output latency estimate {engine.OutputLatencySeconds * 1000:0.0} ms · {engine.AudioDescription}");

        // --- a tool transport locked to Link, playing one silent note per beat ----------------
        var target = new EngineScoreTarget(engine, () => true);
        var scheduler = new TransportScheduler(target, new EveryBeat()) { Sync = link.CreateTransportSync() };
        // Each note's Link beat is evaluated when it is sent, on the timeline in force at that moment.
        var sent = new List<(double Beat, double LinkBeat, double Tempo)>();
        scheduler.EventSent += (ev, at) =>
        {
            double lb = link.BeatAtTime(link.HostMicrosAtScoreTime(at));
            double tempo = link.Capture().Tempo;
            lock (sent) sent.Add((ev.Beat, lb, tempo));
        };

        // --- second peer -------------------------------------------------------------------------
        var psi = new ProcessStartInfo(peerExe,
            $"--bpm 97 --seconds {seconds.ToString(ci)} --grid-ms 100 --quantum {Quantum.ToString(ci)} --start-stop --tempo-at 4:133 --play-at 7 --stop-at 9 --tempo-at 10:126")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var peer = Process.Start(psi)!;
        var peerLines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        peer.OutputDataReceived += (_, e) => { if (e.Data is { } l) peerLines.Enqueue(l); };
        peer.BeginOutputReadLine();

        var clock = Stopwatch.StartNew();
        bool proposed140 = false, schedulerStarted = false;
        double ourTempoAfter133 = 0, peerTempoInitial = 0, peerTempoAfter140 = 0;
        bool sawPlaying = false, sawStoppedAgain = false;
        int ourPeersMax = 0, peerPeersMax = 0;
        var beatErrorsMs = new List<double>();
        var orchestra = new List<string>();
        while (!peer.HasExited || !peerLines.IsEmpty)
        {
            double t = clock.Elapsed.TotalSeconds;
            if (!schedulerStarted && t > 7.5)
            {
                schedulerStarted = true;
                scheduler.Start(0);
            }
            if (!proposed140 && t > 6)
            {
                proposed140 = true;
                link.SetTempo(140);
                Console.WriteLine("  (this side proposes 140 BPM)");
            }
            var snap = link.Capture();
            ourPeersMax = Math.Max(ourPeersMax, snap.Peers);
            if (t > 5 && t < 5.9) ourTempoAfter133 = snap.Tempo;
            if (t > 7.6 && t < 8.6 && snap.IsPlaying) sawPlaying = true;
            if (t > 9.6 && sawPlaying && !snap.IsPlaying) sawStoppedAgain = true;

            while (peerLines.TryDequeue(out var line))
            {
                // Grid line from the peer: G <hostMicros> <peers> <tempo> <beat> … (≥ 7 fields).
                var f = line.Split(' ');
                if (f[0] != "G" || f.Length < 7) continue;
                long gridT = long.Parse(f[1], ci);
                int peerPeers = int.Parse(f[2], ci);
                double peerTempo = double.Parse(f[3], ci), peerBeat = double.Parse(f[4], ci);
                peerPeersMax = Math.Max(peerPeersMax, peerPeers);
                double elapsed = clock.Elapsed.TotalSeconds;
                if (elapsed > 2.5 && elapsed < 3.5) peerTempoInitial = peerTempo;
                if (elapsed > 6.8 && elapsed < 7.5) peerTempoAfter140 = peerTempo;
                if (peerPeers == 1 && elapsed > 2)
                {
                    // Link shares the phase (beat modulo the quantum), not the beat number: each
                    // peer may count bars from a different origin (e.g. after it started playing).
                    double ours = link.BeatAtTime(gridT);
                    double tempoNow = link.Capture().Tempo;
                    double diff = ours - peerBeat;
                    // Wrap the beat difference to the nearest multiple of the quantum, then convert
                    // beats to milliseconds (60000 / BPM ms per beat).
                    beatErrorsMs.Add(Math.Abs(diff - Quantum * Math.Round(diff / Quantum)) * 60000.0 / tempoNow);
                }
            }
            Drain(engine, orchestra);
            Thread.Sleep(15);
        }
        scheduler.Stop();
        engine.Stop();
        done.Wait(5000);
        Drain(engine, orchestra);

        Console.WriteLine();
        Console.WriteLine("Session");
        Check(ourPeersMax == 1 && peerPeersMax == 1, $"peers: this side {ourPeersMax}, peer {peerPeersMax} (expected 1 / 1)");
        Check(Math.Abs(peerTempoInitial - 120) < 0.01, $"peer joined at 97 BPM and adopted the session tempo: {peerTempoInitial:0.000}");
        Check(Math.Abs(ourTempoAfter133 - 133) < 0.01, $"peer proposed 133 → this side: {ourTempoAfter133:0.000}");
        Check(Math.Abs(peerTempoAfter140 - 140) < 0.01, $"this side proposed 140 → peer: {peerTempoAfter140:0.000}");
        Check(sawPlaying && sawStoppedAgain, $"start/stop sync: playing seen {sawPlaying}, stopped again {sawStoppedAgain}");
        if (beatErrorsMs.Count > 0)
        {
            beatErrorsMs.Sort();
            double p50 = beatErrorsMs[beatErrorsMs.Count / 2], max = beatErrorsMs[^1];
            Check(max < 2.0, $"phase agreement (beat mod {Quantum}) at {beatErrorsMs.Count} common host times: median {p50 * 1000:0.#} µs, max {max * 1000:0.#} µs");
        }
        else Check(false, "no common grid samples");

        Console.WriteLine("Csound channels (orchestra prints every 0.25 s)");
        var rows = orchestra.Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(f => f.Length >= 7 && f[0] == "LINKCHK")
            .Select(f => (T: P(f[1]), Bpm: P(f[2]), Beat: P(f[3]), Phase: P(f[4]), Playing: (int)P(f[5]), Peers: (int)P(f[6])))
            .ToList();
        foreach (var r in rows.Where((_, i) => i % 4 == 0))
            Console.WriteLine($"    t={r.T,6:0.00}  LINK_BPM={r.Bpm,8:0.000}  LINK_BEAT={r.Beat,9:0.000}  LINK_PHASE={r.Phase:0.000}  LINK_PLAYING={r.Playing}  LINK_PEERS={r.Peers}");
        Check(rows.Count > 20, $"{rows.Count} channel samples");
        Check(rows.Any(r => Math.Abs(r.Bpm - 133) < 0.01) && rows.Any(r => Math.Abs(r.Bpm - 140) < 0.01) && rows.Any(r => Math.Abs(r.Bpm - 126) < 0.01), "LINK_BPM followed 133 (peer), 140 (this side) and 126 (peer)");
        Check(rows.Any(r => r.Peers == 1), "LINK_PEERS = 1 while the peer is in the session");
        Check(rows.Any(r => r.Playing == 1) && rows.Last().Playing == 0, "LINK_PLAYING went 1 with the peer's play and back to 0");
        Check(rows.All(r => Math.Abs(r.Phase - ((r.Beat % Quantum) + Quantum) % Quantum) < 1e-3 || Math.Abs(Math.Abs(r.Phase - ((r.Beat % Quantum) + Quantum) % Quantum) - Quantum) < 1e-3),
              "LINK_PHASE = LINK_BEAT mod quantum");
        // Steady state (after the 1-second smoothing of the score/host clock offset has settled).
        double worstRate = 0;
        for (int i = 1; i < rows.Count; i++)
        {
            var (p, c) = (rows[i - 1], rows[i]);
            if (p.T < 1.5 || Math.Abs(p.Bpm - c.Bpm) > 1e-6 || c.T - p.T < 0.2) continue; // skip settling and tempo changes
            double expected = (c.T - p.T) * c.Bpm / 60;
            worstRate = Math.Max(worstRate, Math.Abs(c.Beat - p.Beat - expected) * 60000 / c.Bpm);
        }
        Check(worstRate < 2, $"LINK_BEAT advances at LINK_BPM/60 per score second after settling (worst jitter {worstRate:0.00} ms per 0.25 s)");

        Console.WriteLine("Tool transport locked to Link (one note per beat)");
        List<(double Beat, double LinkBeat, double Tempo)> notes;
        lock (sent) notes = [.. sent];
        Check(notes.Count > 6, $"{notes.Count} notes scheduled (started 7.5 s, after the peer's play)");
        if (notes.Count > 0)
        {
            double firstLink = notes[0].LinkBeat;
            Check(Math.Abs(firstLink / Quantum - Math.Round(firstLink / Quantum)) * Quantum < 0.01,
                  $"first note on a downbeat: Link beat {firstLink:0.0000} (quantum {Quantum})");
            double worst = notes.Max(n => Math.Abs(n.LinkBeat - Math.Round(n.LinkBeat)) * 60000 / n.Tempo);
            Check(worst < 1, $"every note on an integer Link beat, at {string.Join(" → ", notes.Select(n => n.Tempo.ToString("0", CultureInfo.InvariantCulture)).Distinct())} BPM (worst {worst:0.000} ms)");
            Check(notes.Select((n, i) => n.LinkBeat - firstLink - i).All(d => Math.Abs(d) < 0.01), "consecutive notes on consecutive Link beats (phase kept through the tempo change)");
        }

        Console.WriteLine();
        Console.WriteLine(failures.Count == 0 ? "ALL PASS" : $"{failures.Count} FAILURE(S)");
        link.IsEnabled = false;
        return failures.Count == 0 ? 0 : 3;

        static double P(string v) => double.Parse(v, CultureInfo.InvariantCulture);
    }

    /// <summary>Empties the engine's message queue: keeps the orchestra's <c>LINKCHK</c> lines in
    /// <paramref name="into"/> and echoes Csound errors to the console.</summary>
    static void Drain(CsoundEngine engine, List<string> into)
    {
        while (engine.Messages.TryDequeue(out var line))
        {
            if (line.Text.Contains("LINKCHK")) into.Add(line.Text.Trim());
            else if (line.Kind == MessageKind.Error) Console.WriteLine("  csound: " + line.Text);
        }
    }

    /// <summary>Looks for link_peer_test.exe next to the exe, then in src\AbletonLink\build\bin\Release above it.</summary>
    static string? FindPeer()
    {
        string here = Path.Combine(AppContext.BaseDirectory, "link_peer_test.exe");
        if (File.Exists(here)) return here;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, "src", "AbletonLink", "build", "bin", "Release", "link_peer_test.exe");
            if (File.Exists(p)) return p;
            p = Path.Combine(dir.FullName, "AbletonLink", "build", "bin", "Release", "link_peer_test.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>A silent pattern: instr 2 on every beat, forever.</summary>
    sealed class EveryBeat : IPatternSource
    {
        /// <inheritdoc/>
        public void Collect(double fromBeat, double toBeat, List<PatternEvent> into)
        {
            for (double b = Math.Ceiling(fromBeat); b < toBeat; b++)
                into.Add(new PatternEvent(b, 0.25, "2", "0"));
        }
        /// <inheritdoc/>
        public (double Start, double End) LoopRange => (0, 0);
        /// <inheritdoc/>
        public bool Loop => false;
        /// <inheritdoc/>
        public double End => 1e9; // effectively endless: the test stops the scheduler itself
    }
}
