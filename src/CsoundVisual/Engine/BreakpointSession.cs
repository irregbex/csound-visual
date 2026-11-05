using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CsoundVisual.Interop;
using CsoundVisual.Localization;

namespace CsoundVisual.Engine;

/// <summary>One breakpoint as Csound sees it: an orchestra line, optionally limited to one instrument.</summary>
/// <param name="Instrument">Instrument id as written after <c>instr</c> (<c>"1"</c>, <c>"Voice"</c>), or empty for any instrument.</param>
/// <param name="Line">Line number in Csound's numbering (see <see cref="BreakpointSession"/>).</param>
public readonly record struct BreakpointRequest(string Instrument, int Line);

/// <summary>A variable captured when a breakpoint stopped the performance.</summary>
/// <param name="Name">Variable name (<c>kcut</c>, <c>asig</c>, <c>gkVol</c>…).</param>
/// <param name="Type">Csound type: <c>i</c>, <c>k</c>, <c>a</c>, <c>S</c>, <c>f</c>, <c>k[]</c>…</param>
/// <param name="Value">Short display value (number, string, summary).</param>
/// <param name="Detail">Longer description (first samples of an a-signal, array elements…), or empty.</param>
/// <param name="Number">Numeric value when the variable is a scalar, otherwise <c>null</c>.</param>
public sealed record DebugVariableValue(string Name, string Type, string Value, string Detail, double? Number);

/// <summary>Everything the debug panel shows about one stop.</summary>
/// <param name="Serial">Increments with every stop of the session (the UI refreshes when it changes).</param>
/// <param name="Line">Csound line number of the opcode about to run.</param>
/// <param name="Opcode">Name of that opcode (<c>linseg</c>, <c>oscili.a</c>, <c>=</c>…).</param>
/// <param name="InstrumentNumber">p1 (may be fractional).</param>
/// <param name="Instrument">Instrument name for named instruments, else its number.</param>
/// <param name="P2">Start time of the instance (score seconds).</param>
/// <param name="P3">Duration of the instance (negative = held).</param>
/// <param name="ScoreTime">Score time of the stop.</param>
/// <param name="KCycle">Csound's control-cycle counter at the stop (global, not per instance).</param>
/// <param name="InstanceIndex">1-based position of this instance among the active instances of the instrument (by start time).</param>
/// <param name="InstanceCount">Active instances of the instrument.</param>
/// <param name="Locals">Variables of the instance.</param>
/// <param name="Globals">Global variables (<c>g…</c>).</param>
public sealed record BreakpointHit(int Serial, int Line, string Opcode, double InstrumentNumber, string Instrument,
    double P2, double P3, double ScoreTime, ulong KCycle, int InstanceIndex, int InstanceCount,
    IReadOnlyList<DebugVariableValue> Locals, IReadOnlyList<DebugVariableValue> Globals);

/// <summary>
/// Line breakpoints for one live performance, on top of the Csound 7 debugger
/// (<c>csoundSetBreakpoint</c> + <c>csoundSetBreakpointCallback</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Threads.</b> The UI calls <see cref="SetBreakpoints"/>, <see cref="Continue"/> and
/// <see cref="StepCycle"/> at any time; they only store requests. The engine's performance thread
/// applies them in <see cref="BeforeCycle"/> (between two <c>csoundPerformKsmps</c> calls) and
/// asks <see cref="CycleProducedAudio"/> after each call. The breakpoint callback runs on the
/// performance thread inside <c>csoundPerformKsmps</c>; it copies the variables (they are only
/// valid there) and publishes an immutable <see cref="BreakpointHit"/>.
/// </para>
/// <para>
/// <b>Pausing without blocking audio.</b> Csound does not block when it stops at a breakpoint:
/// <c>csoundPerformKsmps</c> simply returns without advancing. The engine keeps feeding the
/// device with silence while <see cref="IsPaused"/> is true (see
/// <c>CsoundEngine.PerformHost</c>), so the stream never underruns.
/// </para>
/// <para>
/// <b>Continue vs. step.</b> A line breakpoint fires on every control cycle (≈ 700 times per
/// second), so a literal «continue» would stop again 1 ms later. Instead:
/// <list type="bullet">
/// <item><see cref="Continue"/> resumes and only stops where a given instance (note) has not stopped
/// before: another breakpoint line, another instance (a chord), or a new note.</item>
/// <item><see cref="StepCycle"/> stops at the next breakpoint reached, even by the same instance
/// in the next control cycle (or a later breakpoint line in the same cycle).</item>
/// </list>
/// Hits that must be skipped are resumed immediately on the next call (each skipped cycle costs
/// one extra, empty <c>csoundPerformKsmps</c> call; the audio stays correct).
/// </para>
/// <para>
/// <b>Line numbers.</b> Csound numbers orchestra lines from the CSD's <c>&lt;CsInstruments&gt;</c>
/// tag; code recompiled live is padded by <c>CsoundEngine.CompileOrcAt</c> so the numbering
/// still matches the editor. <c>Editor/Breakpoints.cs</c> (<c>BreakpointLineMap</c>) maps editor
/// lines to these numbers.
/// </para>
/// <para>
/// <b>Csound 7.0 quirks handled here:</b> resuming in the middle of a multi-opcode line re-stops
/// forever (see <see cref="Resume"/>); <c>csoundDebugNext</c> does not advance, hence the
/// "next control cycle" step instead of a true single step; the callback keeps firing for the
/// other instances in the same cycle after a stop (see <see cref="OnHit"/>).
/// </para>
/// </remarks>
public sealed unsafe class BreakpointSession
{
    /// <summary>Resume command posted by the UI (stored as an <see cref="int"/> for <see cref="Interlocked"/>).</summary>
    enum Command { None, Continue, Step }

    /// <summary>Instrument number → name, for named instruments (filled on attach and on lookups).</summary>
    readonly Dictionary<int, string> instrumentNames = [];
    /// <summary>Handle passed to Csound as the callback's user data.</summary>
    GCHandle self;
    /// <summary>The attached Csound instance, or zero after <see cref="Detach"/>.</summary>
    IntPtr csound;
    /// <summary>True when this session called <c>csoundDebuggerInit</c> and must therefore clean it up.</summary>
    bool ownsDebugger;
    /// <summary>Samples per control cycle: the length of a-rate variable buffers.</summary>
    int ksmps = 1;

    // UI → performance thread.
    /// <summary>New breakpoint set waiting to be installed (swapped out with <see cref="Interlocked.Exchange{T}(ref T, T)"/>).</summary>
    volatile IReadOnlyList<BreakpointRequest>? pendingSet;
    /// <summary>Pending <see cref="Command"/>, consumed by <see cref="BeforeCycle"/>.</summary>
    int pendingCommand;

    // Performance thread state.
    /// <summary>The breakpoint callback fired during the current <c>csoundPerformKsmps</c> call.</summary>
    bool hitThisCall;
    /// <summary>The last hit must be skipped: resume automatically before the next call.</summary>
    bool autoContinue;
    /// <summary>The current resume came from <see cref="StepCycle"/> (stop at the very next hit).</summary>
    bool stepping;
    /// <summary>Identity of the last stop (instance memory, start time, line, k-cycle) to avoid stopping twice at one place.</summary>
    (IntPtr Instance, double P2, int Line, ulong KCycle) lastStop;
    /// <summary>Places (instance, start time, line) already stopped at; <see cref="Continue"/> skips them.</summary>
    readonly HashSet<(IntPtr Instance, double P2, int Line)> visited = [];
    /// <summary>Stop counter published in <see cref="BreakpointHit.Serial"/>.</summary>
    int serial;
    // (line, instrument number) pairs installed in Csound, those taken out for the rest of the
    // current cycle (see Resume), and the lines Csound called back for since the last resume.
    readonly HashSet<(int Line, int Instr)> installed = [];
    readonly HashSet<(int Line, int Instr)> suspended = [];
    readonly HashSet<int> hitLines = [];

    /// <summary>Stopped at a breakpoint. Written by the performance thread, read by the UI.</summary>
    volatile bool paused;
    /// <summary>Latest stop (immutable record, published with a release write).</summary>
    BreakpointHit? current;
    /// <summary>The set last installed, for the UI (published with a release write).</summary>
    IReadOnlyList<BreakpointRequest> active = [];

    /// <summary>Creates a session with an initial set of breakpoints.</summary>
    /// <param name="initial">Breakpoints to install when the performance starts (may be empty: they can be added later).</param>
    public BreakpointSession(IReadOnlyList<BreakpointRequest> initial)
    {
        pendingSet = initial;
        HasBreakpointsAtStart = initial.Count > 0;
    }

    /// <summary>True when the session was created with at least one breakpoint.</summary>
    public bool HasBreakpointsAtStart { get; }

    /// <summary>True while the performance is stopped at a breakpoint (the device plays silence).</summary>
    public bool IsPaused => paused;

    /// <summary>The latest stop, or <c>null</c> before the first one. Read it when <see cref="IsPaused"/> becomes true.</summary>
    public BreakpointHit? Current => Volatile.Read(ref current);

    /// <summary>The breakpoints currently installed in Csound.</summary>
    public IReadOnlyList<BreakpointRequest> Active => Volatile.Read(ref active);

    /// <summary>Raised on the performance thread when the performance stops at a breakpoint.</summary>
    /// <remarks>Raised from inside Csound's callback: handlers must only post to the UI thread and return.</remarks>
    public event Action<BreakpointHit>? Stopped;

    /// <summary>Replaces every breakpoint (applied before the next control cycle).</summary>
    public void SetBreakpoints(IReadOnlyList<BreakpointRequest> breakpoints) => pendingSet = breakpoints;

    /// <summary>Resumes until a breakpoint is reached where that instance has not stopped yet.</summary>
    public void Continue() => Interlocked.Exchange(ref pendingCommand, (int)Command.Continue);

    /// <summary>Resumes until the next breakpoint is reached (the same line in the next control cycle, at the latest).</summary>
    public void StepCycle() => Interlocked.Exchange(ref pendingCommand, (int)Command.Step);

    // ------------------------------------------------------------------------------------------
    //  Performance thread
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Installs the breakpoint callback. Call after compiling and before <c>csoundStart</c>.
    /// </summary>
    /// <param name="cs">Csound instance.</param>
    /// <param name="debuggerReady">True when the debugger was already initialised (by the live-value monitor).</param>
    /// <param name="namedInstruments">Named instruments of the orchestra, so stops can show names.</param>
    /// <returns><c>false</c> when the debugger could not be initialised.</returns>
    internal bool Attach(IntPtr cs, bool debuggerReady, IEnumerable<string> namedInstruments)
    {
        if (!debuggerReady)
        {
            if (CsoundNative.csoundDebuggerInit(cs) != 0)
                return false;
            ownsDebugger = true;
        }
        csound = cs;
        self = GCHandle.Alloc(this);
        CsoundNative.csoundSetBreakpointCallback(cs, &OnBreakpoint, GCHandle.ToIntPtr(self));
        foreach (var name in namedInstruments)
            if (CsoundNative.csoundGetInstrNumber(cs, name) is var n and > 0)
                instrumentNames[n] = name;
        return true;
    }

    /// <summary>Sets the control block size once known (after <c>csoundStart</c>).</summary>
    internal void Configure(int ksmpsValue) => ksmps = Math.Max(1, ksmpsValue);

    /// <summary>Applies pending requests. Called before every <c>csoundPerformKsmps</c>.</summary>
    /// <remarks>
    /// Order matters: (1) install a new set, or re-arm the lines suspended during the previous
    /// resumed cycle once that cycle is over; (2) resume — automatically for a skipped hit, or on
    /// the user's command while paused; (3) clear the per-call hit flag. A command posted while
    /// not paused is consumed and ignored.
    /// </remarks>
    internal void BeforeCycle(IntPtr cs)
    {
        if (Interlocked.Exchange(ref pendingSet, null) is { } set)
            Install(cs, set);
        else if (suspended.Count > 0 && !paused && !autoContinue)
        {
            // The resumed cycle is over: the lines suspended for it can stop again.
            foreach (var (line, instr) in suspended)
                CsoundNative.csoundSetBreakpoint(cs, line, instr, 0);
            suspended.Clear();
        }

        var command = (Command)Interlocked.Exchange(ref pendingCommand, (int)Command.None);
        if (autoContinue)
        {
            autoContinue = false;
            Resume(cs);
        }
        else if (paused && command != Command.None)
        {
            stepping = command == Command.Step;
            paused = false;
            Resume(cs);
        }
        hitThisCall = false;
    }

    /// <summary>
    /// Resumes Csound. Works around a Csound 7.0 debugger bug: resuming in the middle of a line
    /// that compiles to several opcodes (<c>asig moogladder a1 + a2, …</c>) stops again at the
    /// same opcode forever. So the lines Csound stopped at in this cycle are removed for the rest
    /// of the cycle and re-installed before the next one (see <see cref="BeforeCycle"/>).
    /// </summary>
    void Resume(IntPtr cs)
    {
        foreach (int line in hitLines)
            foreach (var (l, instr) in installed)
                if (l == line && !suspended.Contains((l, instr)))
                {
                    CsoundNative.csoundRemoveBreakpoint(cs, l, instr);
                    suspended.Add((l, instr));
                }
        hitLines.Clear();
        CsoundNative.csoundDebugContinue(cs);
    }

    /// <summary>
    /// True when the <c>csoundPerformKsmps</c> call that just returned completed a control cycle,
    /// so <c>spout</c> holds valid audio. False while paused and for calls interrupted by a hit.
    /// </summary>
    internal bool CycleProducedAudio => !hitThisCall && !paused;

    /// <summary>
    /// Removes the callback and the breakpoints and resumes Csound, so it can be destroyed
    /// cleanly. Releases the debugger only if this session initialised it.
    /// </summary>
    internal void Detach(IntPtr cs)
    {
        CsoundNative.csoundClearBreakpoints(cs);
        CsoundNative.csoundSetBreakpointCallback(cs, null, IntPtr.Zero);
        CsoundNative.csoundDebugContinue(cs);
        if (ownsDebugger)
            CsoundNative.csoundDebuggerClean(cs);
        if (self.IsAllocated) self.Free();
        paused = false;
        csound = IntPtr.Zero;
    }

    /// <summary>Clears Csound's breakpoints and installs <paramref name="set"/>.</summary>
    void Install(IntPtr cs, IReadOnlyList<BreakpointRequest> set)
    {
        CsoundNative.csoundClearBreakpoints(cs);
        installed.Clear();
        suspended.Clear();
        foreach (var bp in set.Distinct())
        {
            int instr = ResolveInstrument(cs, bp.Instrument);
            if (instr < 0 || installed.Contains((bp.Line, instr)))
                continue;
            installed.Add((bp.Line, instr));
            // While stopped, a line Csound is stopped at stays suspended until the cycle ends.
            if (paused && hitLines.Contains(bp.Line))
                continue;
            CsoundNative.csoundSetBreakpoint(cs, bp.Line, instr, 0);
        }
        Volatile.Write(ref active, set);
        // A new set (or a recompile) starts over: every instance may stop again.
        visited.Clear();
    }

    /// <summary>Instrument number for an id (<c>"3"</c>, <c>"Voice"</c>); 0 = any, -1 = unknown name.</summary>
    /// <remarks>0 is Csound's "any instrument" value for <c>csoundSetBreakpoint</c>'s instrument argument.</remarks>
    int ResolveInstrument(IntPtr cs, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return 0;
        id = id.Trim().Trim('"');
        if (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            return number;
        int n = CsoundNative.csoundGetInstrNumber(cs, id);
        if (n <= 0)
            return -1;
        instrumentNames[n] = id;
        return n;
    }

    /// <summary>
    /// Native breakpoint callback (cdecl), called by Csound on the performance thread from inside
    /// <c>csoundPerformKsmps</c> whenever an installed line is about to run.
    /// </summary>
    /// <param name="cs">The Csound instance.</param>
    /// <param name="info">Stop details owned by Csound; valid only during the call.</param>
    /// <param name="userData">The session's <see cref="GCHandle"/> as an <see cref="IntPtr"/>.</param>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnBreakpoint(IntPtr cs, CsoundNative.BreakpointInfo* info, IntPtr userData)
    {
        if (userData == IntPtr.Zero || info is null || GCHandle.FromIntPtr(userData).Target is not BreakpointSession session)
            return;
        try
        {
            session.OnHit(cs, info);
        }
        catch
        {
            // Never let an exception cross the native boundary: resume instead.
            session.autoContinue = true;
        }
    }

    /// <summary>Decides whether to stay stopped at this hit and, if so, captures it.</summary>
    /// <remarks>
    /// An instance is identified by its local variable memory (<c>LocalBase</c>) plus its start
    /// time: Csound recycles instance memory, so the pointer alone could confuse a new note with
    /// an old one. Capturing allocates (lists, strings) — acceptable because the performance is
    /// about to pause anyway.
    /// </remarks>
    void OnHit(IntPtr cs, CsoundNative.BreakpointInfo* info)
    {
        hitThisCall = true;
        if (info->CurrentOpcode is not null)
            hitLines.Add(info->CurrentOpcode->Line);
        // Csound keeps calling back for the other instances in the same control cycle after a
        // stop (verified with two overlapping notes). The first stop wins; resuming must not be
        // triggered by the later callbacks, or the engine would run while the UI shows a pause.
        if (paused)
            return;
        var instr = info->BreakpointInstr;
        if (instr is null)
        {
            autoContinue = true;
            return;
        }
        int line = info->CurrentOpcode is not null ? info->CurrentOpcode->Line : instr->Line;
        var key = (instr->LocalBase, instr->P2, line);
        bool sameAsLast = lastStop.Instance == instr->LocalBase && lastStop.P2 == instr->P2 &&
                          lastStop.Line == line && lastStop.KCycle == instr->KCounter;
        // Several opcodes on one line hit in the same cycle: never stop twice there. Continue
        // skips places this instance already stopped at; Step only skips the current one.
        if (sameAsLast || (!stepping && visited.Contains(key)))
        {
            autoContinue = true;
            return;
        }
        stepping = false;
        // An instance skipped earlier in this cycle asked to resume; this stop cancels that.
        autoContinue = false;
        visited.Add(key);
        lastStop = (instr->LocalBase, instr->P2, line, instr->KCounter);

        string opcode = info->CurrentOpcode is not null ? CsoundNative.FixedString(info->CurrentOpcode->OpName, 16) : "";
        // Position of this note among the active instances of the same (integer) instrument,
        // ordered by start time, for the "note 2 of 3" display.
        int whole = (int)instr->P1;
        int count = 0, index = 0;
        var starts = new List<(double P2, IntPtr Base)>();
        for (var i = info->InstrListHead; i is not null; i = i->Next)
            if ((int)i->P1 == whole)
                starts.Add((i->P2, i->LocalBase));
        starts.Sort((a, b) => a.P2.CompareTo(b.P2));
        count = starts.Count;
        index = starts.FindIndex(s => s.Base == instr->LocalBase) + 1;

        var locals = ReadVariables(cs, info->InstrVarList, globals: false);
        var globalList = CsoundNative.csoundDebugGetGlobalVariables(cs);
        var globals = ReadVariables(cs, globalList, globals: true);
        if (globalList is not null)
            CsoundNative.csoundDebugFreeVariables(cs, globalList);

        var hit = new BreakpointHit(++serial, line, opcode, instr->P1,
            instrumentNames.TryGetValue(whole, out var name) ? name : whole.ToString(CultureInfo.InvariantCulture),
            instr->P2, instr->P3, CsoundNative.csoundGetScoreTime(cs), instr->KCounter,
            Math.Max(1, index), Math.Max(1, count), locals, globals);
        Volatile.Write(ref current, hit);
        paused = true;
        Stopped?.Invoke(hit);
    }

    /// <summary>Copies and formats a variable list (temporaries and Csound internals are skipped).</summary>
    List<DebugVariableValue> ReadVariables(IntPtr cs, CsoundNative.DebugVariable* list, bool globals)
    {
        var result = new List<DebugVariableValue>();
        for (var v = list; v is not null; v = v->Next)
        {
            if (v->Name == IntPtr.Zero || v->TypeName == IntPtr.Zero)
                continue;
            string name = Marshal.PtrToStringUTF8(v->Name) ?? "";
            string type = Marshal.PtrToStringUTF8(v->TypeName) ?? "";
            // Only user data types: skips temporaries (#k0), Csound internals (sr, ksmps: type r;
            // this/this_instr) and the opcode/instrument definitions the global pool also lists.
            if (name.Length == 0 || name[0] == '#' || type is not ("i" or "k" or "a" or "S" or "f" or "[" or "b" or "B"))
                continue;
            if (globals && name[0] != 'g')
                continue;
            result.Add(Describe(cs, name, type, v->Data));
        }
        return result;
    }

    /// <summary>Compact invariant-culture number: up to 5 decimals, scientific notation outside 1e-4…1e6.</summary>
    static string Num(double v) =>
        v == 0 ? "0"
        : !double.IsFinite(v) ? v.ToString(CultureInfo.InvariantCulture)
        : Math.Abs(v) >= 1e6 || (v != 0 && Math.Abs(v) < 1e-4) ? v.ToString("0.###e+0", CultureInfo.InvariantCulture)
        : v.ToString("0.#####", CultureInfo.InvariantCulture);

    /// <summary>Formats one variable according to its Csound type.</summary>
    /// <param name="cs">Csound instance (needed to serialise arrays and f-signals).</param>
    /// <param name="name">Variable name.</param>
    /// <param name="type">Debugger type name: <c>i k b B</c> (one double), <c>a</c> (ksmps doubles),
    /// <c>S</c> (characters), <c>[</c> (array), <c>f</c> (PVS frame).</param>
    /// <param name="data">Pointer into the instance's memory; valid only during the callback.</param>
    DebugVariableValue Describe(IntPtr cs, string name, string type, IntPtr data)
    {
        if (data == IntPtr.Zero)
            return new(name, type, "—", "", null);
        switch (type)
        {
            case "i" or "k" or "b" or "B":
            {
                double v = *(double*)data;
                return new(name, type, Num(v), "", v);
            }
            case "a":
            {
                double* s = (double*)data;
                double sum = 0, peak = 0;
                for (int i = 0; i < ksmps; i++)
                {
                    sum += s[i] * s[i];
                    peak = Math.Max(peak, Math.Abs(s[i]));
                }
                double rms = Math.Sqrt(sum / ksmps);
                var first = Enumerable.Range(0, Math.Min(4, ksmps)).Select(i => Num(s[i]));
                return new(name, type, Loc.S($"RMS {Num(rms)} · pico {Num(peak)}", $"RMS {Num(rms)} · peak {Num(peak)}"),
                    Loc.S($"{ksmps} muestras: ", $"{ksmps} samples: ") + string.Join(", ", first) + (ksmps > 4 ? ", …" : ""), rms);
            }
            case "S":
            {
                // Csound 7's debugger hands out the characters themselves for S variables (verified:
                // the first bytes at `data` are the text, not a STRINGDAT). Read at most 120 bytes.
                byte* chars = (byte*)data;
                int length = 0;
                while (length < 120 && chars[length] != 0) length++;
                string s = System.Text.Encoding.UTF8.GetString(chars, length);
                return new(name, type, "\"" + s + (length == 120 ? "…" : "") + "\"", "", null);
            }
            case "[":
            {
                // csoundDebugSerializeArray copies up to Max numeric elements (flattened) and returns
                // the total element count; the element type and dimensions come back in `info`.
                const int Max = 16;
                double* buffer = stackalloc double[Max];
                CsoundNative.DebugArrayInfo info;
                int total = CsoundNative.csoundDebugSerializeArray(cs, data, buffer, Max, &info);
                string element = CsoundNative.FixedString(info.ElementTypeName, 16);
                string shownType = (element.Length > 0 ? element : "?") + string.Concat(Enumerable.Repeat("[]", Math.Max(1, info.Dimensions)));
                if (total <= 0)
                    return new(name, shownType, Loc.S("(vacío o no numérico)", "(empty or not numeric)"), "", null);
                int shown = Math.Min(total, Max);
                var values = Enumerable.Range(0, shown).Select(i => Num(buffer[i]));
                return new(name, shownType, Loc.S($"{total} elementos", $"{total} elements"),
                    "[" + string.Join(", ", values) + (total > shown ? ", …" : "") + "]", null);
            }
            case "f":
            {
                // PVS frame: interleaved (amplitude, frequency) float pairs per bin, NB = N/2 + 1 bins.
                // Allocated natively (32 KiB) rather than on the stack; freed below.
                const int Max = 8192;
                float* frame = (float*)NativeMemory.Alloc(Max * sizeof(float));
                try
                {
                    CsoundNative.DebugFsigInfo info;
                    int n = CsoundNative.csoundDebugSerializeFsig(cs, data, frame, Max, &info, 0);
                    if (n <= 0 || info.NB <= 0)
                        return new(name, type, Loc.S("(sin cuadro aún)", "(no frame yet)"), "", null);
                    int bins = Math.Min(info.NB, n / 2);
                    int best = 0;
                    for (int b = 1; b < bins; b++)
                        if (frame[2 * b] > frame[2 * best]) best = b;
                    return new(name, type, $"N {info.N} · {info.NB} bins",
                        Loc.S($"bin más fuerte {best}: amp {Num(frame[2 * best])} a {Num(frame[2 * best + 1])} Hz · solape {info.Overlap}",
                              $"strongest bin {best}: amp {Num(frame[2 * best])} at {Num(frame[2 * best + 1])} Hz · hop {info.Overlap}"), null);
                }
                finally
                {
                    NativeMemory.Free(frame);
                }
            }
            default:
                return new(name, type, "…", Loc.S("tipo sin vista", "type not shown"), null);
        }
    }
}
