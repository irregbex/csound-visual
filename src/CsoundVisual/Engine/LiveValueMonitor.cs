using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CsoundVisual.Interop;

namespace CsoundVisual.Engine;

/// <summary>Live state of one orchestra variable, as last seen by the <see cref="LiveValueMonitor"/>.</summary>
/// <param name="Name">Variable name (e.g. <c>kcut</c>, <c>gaRevL</c>).</param>
/// <param name="Instrument">Instrument number, or 0 for a global variable.</param>
/// <param name="Value">Current value. For audio variables: the peak of the last control block.</param>
/// <param name="Minimum">Smallest value seen since the performance started.</param>
/// <param name="Maximum">Largest value seen since the performance started.</param>
/// <param name="Instances">Active instances of the instrument when sampled (polyphony).</param>
/// <param name="IsAudio">True for a-rate variables (the value is a block peak).</param>
/// <param name="History">
/// Recent values, oldest first, for sparklines: up to <see cref="LiveValueMonitor.HistoryLength"/>
/// samples, i.e. ≈4.5 s for instrument locals (sampled at ≈20 Hz) and ≈9 s for globals (sampled
/// every other time, ≈10 Hz). A fresh array per snapshot, never mutated afterwards.
/// </param>
public sealed record LiveValue(string Name, int Instrument, double Value, double Minimum, double Maximum,
    int Instances, bool IsAudio, float[] History);

/// <summary>
/// Samples the variables of every active instrument instance during the performance using the
/// Csound 7 debugger API (<c>csoundSetDebugCallback</c>), so the editor can show what a
/// variable holds <i>right now</i>.
/// </summary>
/// <remarks>
/// <para>
/// The debug callback fires after every control cycle on the performance thread. To keep its
/// cost negligible the monitor only samples every <c>kr / 20</c> cycles (≈20 times per second)
/// and only reads plain numeric variables (<c>i</c>, <c>k</c>, <c>b</c>, <c>B</c>) and audio
/// variables (<c>a</c>, reduced to the block peak). Strings, arrays and f-signals are skipped.
/// </para>
/// <para>
/// When an instrument has several active instances (overlapping notes), the most recently
/// started one (largest p2) is reported, together with the instance count.
/// </para>
/// <para>
/// Results are published as an immutable dictionary swapped atomically, so the UI thread can
/// read it at any time without locks.
/// </para>
/// <para>
/// <b>Lifecycle</b> (driven by <c>CsoundEngine</c> on the performance thread):
/// <see cref="Attach"/> after <c>csoundCompileCSD</c> → <c>csoundStart</c> → <see cref="Configure"/>
/// → callbacks during the performance → <see cref="Detach"/> before <c>csoundDestroy</c>. The UI
/// (hover tooltips, the Watch panel, the channel cards) only reads <see cref="Values"/>.
/// </para>
/// <para>
/// <b>Memory ownership.</b> The instance and variable lists returned by the debugger API are
/// allocated by Csound and must be returned with <c>csoundDebugFreeInstrInstances</c> /
/// <c>csoundDebugFreeVariables</c>; the <c>Data</c> pointers inside point into live instrument
/// memory and are only valid during the callback. Everything kept is copied into managed objects.
/// </para>
/// <para>
/// <b>Cost.</b> A sampling pass allocates a few small dictionaries, strings and arrays on the
/// performance thread (≈20 times per second, not every cycle). Values persist after an
/// instrument stops: the last sample stays visible (with its last instance count) until the
/// performance ends.
/// </para>
/// </remarks>
public sealed unsafe class LiveValueMonitor
{
    /// <summary>Number of values kept per variable for sparklines.</summary>
    public const int HistoryLength = 90;

    /// <summary>Approximate sampling rate of the monitor, in Hz.</summary>
    const double SampleRateHz = 20;

    /// <summary>Mutable per-variable accumulator, touched only by the performance thread.</summary>
    sealed class Entry
    {
        /// <summary>Last sampled value; running minimum and maximum since the performance started.</summary>
        public double Value, Min = double.MaxValue, Max = double.MinValue;
        /// <summary>Instance count of the owning instrument at the last sample (1 for globals).</summary>
        public int Instances;
        /// <summary>True for a-rate variables.</summary>
        public bool IsAudio;
        /// <summary>Circular history of the last <see cref="HistoryLength"/> values.</summary>
        public readonly float[] Ring = new float[HistoryLength];
        /// <summary>Next slot of <see cref="Ring"/> to write.</summary>
        public int Write;
        /// <summary>Valid values in <see cref="Ring"/> (≤ <see cref="HistoryLength"/>).</summary>
        public int Count;
    }

    /// <summary>Accumulators keyed by (instrument number or 0, variable name). Performance thread only.</summary>
    readonly Dictionary<(int, string), Entry> entries = [];
    /// <summary>Latest immutable snapshot; replaced with a release write, read by the UI with an acquire read.</summary>
    IReadOnlyDictionary<(int, string), LiveValue> published = new Dictionary<(int, string), LiveValue>();
    /// <summary>Handle passed to Csound as the callback's user data; keeps this object reachable while attached.</summary>
    GCHandle self;
    /// <summary>
    /// <c>every</c>: sample once per this many control cycles; <c>cycle</c>: callbacks so far;
    /// <c>ksmps</c>: samples per a-rate variable block.
    /// </summary>
    int every = 1, cycle, ksmps = 1;

    /// <summary>Instrument name → number, resolved after compilation (named instruments only).</summary>
    public IReadOnlyDictionary<string, int> InstrumentNumbers { get; private set; } = new Dictionary<string, int>();

    /// <summary>Latest values, keyed by (instrument number or 0 for globals, variable name).</summary>
    public IReadOnlyDictionary<(int Instrument, string Name), LiveValue> Values => Volatile.Read(ref published);

    /// <summary>Looks up a variable: a local of <paramref name="instrument"/>, or a global when 0.</summary>
    /// <returns>The latest sample, or <c>null</c> if the variable was never seen (instrument not playing yet, unsupported type…).</returns>
    public LiveValue? Get(int instrument, string name) => Values.GetValueOrDefault((instrument, name));

    /// <summary>
    /// Enables the debugger and installs the callback. Call after compiling and before
    /// <c>csoundStart</c>, on the performance thread.
    /// </summary>
    /// <param name="cs">Csound instance.</param>
    /// <param name="instrumentNames">Named instruments of the orchestra, resolved to numbers for the UI.</param>
    /// <returns><c>false</c> when the debugger could not be initialised (live values are then unavailable).</returns>
    public bool Attach(IntPtr cs, IEnumerable<string> instrumentNames)
    {
        if (CsoundNative.csoundDebuggerInit(cs) != 0)
            return false;
        // A normal (not pinned) handle is enough: Csound only stores the opaque IntPtr and hands
        // it back to OnDebug, which resolves it with GCHandle.FromIntPtr.
        self = GCHandle.Alloc(this);
        CsoundNative.csoundSetDebugCallback(cs, &OnDebug, GCHandle.ToIntPtr(self));

        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in instrumentNames)
            if (CsoundNative.csoundGetInstrNumber(cs, name) is var n and > 0)
                numbers[name] = n;
        InstrumentNumbers = numbers;
        return true;
    }

    /// <summary>Sets the sampling divider once kr and ksmps are known (after <c>csoundStart</c>).</summary>
    /// <param name="kr">Control rate in Hz (control cycles per second); e.g. 48000/32 = 1500 → sample every 75 cycles.</param>
    /// <param name="ksmpsValue">Samples per control cycle: the length of every a-rate variable buffer.</param>
    public void Configure(double kr, int ksmpsValue)
    {
        every = Math.Max(1, (int)Math.Round(kr / SampleRateHz));
        ksmps = Math.Max(1, ksmpsValue);
    }

    /// <summary>Removes the callback and releases the debugger. Call before destroying the instance.</summary>
    /// <remarks>The callback is removed first so no call can arrive with a freed <see cref="GCHandle"/>.</remarks>
    public void Detach(IntPtr cs)
    {
        CsoundNative.csoundSetDebugCallback(cs, null, IntPtr.Zero);
        CsoundNative.csoundDebuggerClean(cs);
        if (self.IsAllocated) self.Free();
    }

    /// <summary>
    /// Native debug callback (cdecl), invoked by Csound on the performance thread after each
    /// control cycle while the debugger is attached. Must not throw: an exception cannot cross
    /// back into native code.
    /// </summary>
    /// <param name="cs">The Csound instance.</param>
    /// <param name="userData">The <see cref="GCHandle"/> of the monitor, as an <see cref="IntPtr"/>.</param>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnDebug(IntPtr cs, IntPtr userData)
    {
        // Every managed exception is swallowed here, exactly like BreakpointSession.OnBreakpoint
        // does: an exception escaping an [UnmanagedCallersOnly] method cannot unwind through
        // Csound's native frames and terminates the whole process (taking the user's unsaved
        // work with it). Losing one snapshot of live values is harmless; the next control cycle
        // simply tries again.
        try
        {
            if (userData != IntPtr.Zero && GCHandle.FromIntPtr(userData).Target is LiveValueMonitor monitor)
                monitor.Collect(cs);
        }
        catch
        {
            // Intentionally ignored (see above): never let an exception cross the native boundary.
        }
    }

    /// <summary>Samples all instances (and every other time the globals) and publishes a snapshot.</summary>
    void Collect(IntPtr cs)
    {
        if (++cycle % every != 0)
            return;

        // Latest instance per instrument number, plus instance counts. The list is a native
        // singly linked list; P1 holds the instrument number (fractional for tagged notes, hence
        // the truncation), P2 the start time used to pick the most recent note.
        var latest = new Dictionary<int, IntPtr>();
        var counts = new Dictionary<int, int>();
        CsoundNative.DebugInstr* list = CsoundNative.csoundDebugGetInstrInstances(cs);
        for (var inst = list; inst is not null; inst = inst->Next)
        {
            int number = (int)inst->P1;
            counts[number] = counts.GetValueOrDefault(number) + 1;
            if (!latest.TryGetValue(number, out var best) || ((CsoundNative.DebugInstr*)best)->P2 <= inst->P2)
                latest[number] = (IntPtr)inst;
        }
        foreach (var (number, ptr) in latest)
        {
            CsoundNative.DebugVariable* vars = CsoundNative.csoundDebugGetVariables(cs, (CsoundNative.DebugInstr*)ptr);
            Read(vars, number, counts[number], globalsOnly: false);
            CsoundNative.csoundDebugFreeVariables(cs, vars);
        }
        if (list is not null)
            CsoundNative.csoundDebugFreeInstrInstances(cs, list);

        // Globals change less often and are many in large orchestras: sample them every other pass.
        if ((cycle / every) % 2 == 0)
        {
            CsoundNative.DebugVariable* globals = CsoundNative.csoundDebugGetGlobalVariables(cs);
            Read(globals, 0, 1, globalsOnly: true);
            if (globals is not null)
                CsoundNative.csoundDebugFreeVariables(cs, globals);
        }

        Publish();
    }

    /// <summary>Walks a debugger variable list and folds every supported value into <see cref="entries"/>.</summary>
    /// <param name="vars">Native linked list (may be null); owned by the caller, which frees it.</param>
    /// <param name="instrument">Key for the entries: instrument number, or 0 for globals.</param>
    /// <param name="instances">Polyphony to record with each value.</param>
    /// <param name="globalsOnly">Keep only user globals (names starting with <c>g</c>).</param>
    /// <remarks>
    /// Scalar types (<c>i</c>, <c>k</c>, <c>b</c>, <c>B</c>) store a single <c>double</c> (Csound
    /// is built with 64-bit MYFLT). An <c>a</c> variable is <c>ksmps</c> doubles; its absolute peak
    /// is kept. Non-finite values are ignored so one NaN does not poison min/max.
    /// </remarks>
    void Read(CsoundNative.DebugVariable* vars, int instrument, int instances, bool globalsOnly)
    {
        for (var v = vars; v is not null; v = v->Next)
        {
            if (v->Name == IntPtr.Zero || v->Data == IntPtr.Zero || v->TypeName == IntPtr.Zero)
                continue;
            string? name = Marshal.PtrToStringUTF8(v->Name);
            // Skip compiler temporaries ("#k0") and, for globals, Csound's internal ones (sr, ksmps…).
            if (string.IsNullOrEmpty(name) || name[0] == '#' || (globalsOnly && name[0] != 'g'))
                continue;
            string? type = Marshal.PtrToStringUTF8(v->TypeName);
            double value;
            bool audio = false;
            switch (type)
            {
                case "k" or "i" or "b" or "B":
                    value = *(double*)v->Data;
                    break;
                case "a":
                {
                    double* samples = (double*)v->Data;
                    double peak = 0;
                    for (int i = 0; i < ksmps; i++)
                        if (Math.Abs(samples[i]) > Math.Abs(peak)) peak = samples[i];
                    value = Math.Abs(peak);
                    audio = true;
                    break;
                }
                default:
                    continue;
            }
            if (!double.IsFinite(value))
                continue;

            var key = (instrument, name);
            if (!entries.TryGetValue(key, out var e))
                entries[key] = e = new Entry();
            e.Value = value;
            e.Instances = instances;
            e.IsAudio = audio;
            if (value < e.Min) e.Min = value;
            if (value > e.Max) e.Max = value;
            e.Ring[e.Write] = (float)value;
            e.Write = (e.Write + 1) % HistoryLength;
            if (e.Count < HistoryLength) e.Count++;
        }
    }

    /// <summary>
    /// Builds a new immutable snapshot (fresh dictionary, fresh history arrays unrolled oldest
    /// first) and publishes it with a single reference swap.
    /// </summary>
    void Publish()
    {
        var snapshot = new Dictionary<(int, string), LiveValue>(entries.Count);
        foreach (var ((instrument, name), e) in entries)
        {
            var history = new float[e.Count];
            int start = (e.Write - e.Count + HistoryLength) % HistoryLength;
            for (int i = 0; i < e.Count; i++)
                history[i] = e.Ring[(start + i) % HistoryLength];
            snapshot[(instrument, name)] = new LiveValue(name, instrument, e.Value, e.Min, e.Max, e.Instances, e.IsAudio, history);
        }
        Volatile.Write(ref published, snapshot);
    }
}
