using System.Text.Json;
using CsoundVisual.Engine;
using CsoundVisual.Localization;

namespace CsoundVisual.Integration;

/// <summary>A saved chain slot (<c>settings.json</c> › <c>PluginChain</c>).</summary>
/// <remarks>Plain JSON-serialisable DTO written by <see cref="VstChain.ToSettings"/> and read back
/// by <see cref="VstChain.Restore"/>; the list order is the processing order.</remarks>
public sealed class VstSlotSettings
{
    /// <summary>Path of the .vst3 module.</summary>
    public string Path { get; set; } = "";
    /// <summary>Class id (32 hex digits).</summary>
    public string ClassId { get; set; } = "";
    /// <summary>Display name when it was saved (shown if the plug-in cannot be loaded any more).</summary>
    public string Name { get; set; } = "";
    /// <summary>Plug-in state (processor + controller), base64; empty = defaults.</summary>
    public string State { get; set; } = "";
    /// <summary>Slot bypassed.</summary>
    public bool Bypass { get; set; }
}

/// <summary>
/// One plug-in instance in the chain. Created, configured and released on the UI thread; the
/// audio thread only calls <see cref="VstHostNative.vh_process_stereo"/> through
/// <see cref="VstChain.Process"/>.
/// </summary>
/// <remarks>
/// Wraps one native handle from <see cref="VstHostNative.Load"/>. Descriptive properties (name,
/// vendor, instrument) are read once from the native JSON info at construction; channel counts and
/// latency are refreshed after every successful <c>vh_prepare</c>. The editor/parameter/state
/// helpers are UI-thread only, as required by <c>vsthost.h</c>.
/// </remarks>
public sealed class VstSlot
{
    /// <summary>
    /// Native instance handle; set to <see cref="IntPtr.Zero"/> by <see cref="VstChain"/> when the
    /// slot is freed (under its MIDI lock, after the audio thread stopped using it). Every member
    /// checks it so a stale <see cref="VstSlot"/> reference held by the UI is harmless.
    /// </summary>
    internal IntPtr Handle;

    /// <summary>Backing field of <see cref="Bypass"/>; volatile because the audio thread polls it every block.</summary>
    volatile bool bypass;

    /// <summary>Backing field of <see cref="Crashed"/>; set by the audio thread, read by the UI.</summary>
    volatile bool crashed;

    /// <summary>Module path.</summary>
    public string Path { get; }
    /// <summary>Class id.</summary>
    public string ClassId { get; }
    /// <summary>Plug-in name.</summary>
    public string Name { get; }
    /// <summary>Vendor.</summary>
    public string Vendor { get; }
    /// <summary>True for instruments (they add their output to the signal and receive the app's MIDI).</summary>
    public bool IsInstrument { get; }
    /// <summary>Main input / output channel counts after preparation.</summary>
    public int Inputs { get; internal set; }
    /// <inheritdoc cref="Inputs"/>
    public int Outputs { get; internal set; }
    /// <summary>Latency reported by the plug-in, in samples (not compensated: it is an insert at the end).</summary>
    public int Latency { get; internal set; }
    /// <summary>Sample rate the slot is prepared for (0 = not prepared).</summary>
    public double PreparedRate { get; internal set; }
    /// <summary>Largest block the slot is prepared for.</summary>
    public int PreparedBlock { get; internal set; }

    /// <summary>Skips the plug-in (the signal passes unchanged). Any thread.</summary>
    public bool Bypass { get => bypass; set => bypass = value; }

    /// <summary>True once the plug-in crashed while processing; it is then skipped for good.</summary>
    public bool Crashed { get => crashed; internal set => crashed = value; }

    /// <summary>Wraps a freshly loaded handle and reads its descriptive info (UI thread).</summary>
    /// <param name="handle">Handle from <see cref="VstHostNative.Load"/>; ownership passes to the slot/chain.</param>
    /// <param name="path">Module path.</param>
    /// <param name="classId">Class id (32 hex digits).</param>
    /// <exception cref="JsonException">The native info is not valid JSON.</exception>
    /// <exception cref="KeyNotFoundException">A required member is missing from the info.</exception>
    internal VstSlot(IntPtr handle, string path, string classId)
    {
        Handle = handle;
        Path = path;
        ClassId = classId;
        string info = VstHostNative.Info(handle);
        using var doc = JsonDocument.Parse(info);
        var root = doc.RootElement;
        Name = root.GetProperty("name").GetString() ?? "?";
        Vendor = root.GetProperty("vendor").GetString() ?? "";
        IsInstrument = root.GetProperty("instrument").GetBoolean();
    }

    /// <summary>Refreshes channel counts and latency from the native side.</summary>
    internal void RefreshInfo()
    {
        using var doc = JsonDocument.Parse(VstHostNative.Info(Handle));
        var root = doc.RootElement;
        Inputs = root.GetProperty("inputs").GetInt32();
        Outputs = root.GetProperty("outputs").GetInt32();
        Latency = root.GetProperty("latency").GetInt32();
        if (root.GetProperty("crashed").GetBoolean()) Crashed = true;
    }

    /// <summary>True when the plug-in has its own editor window (UI thread).</summary>
    public bool HasEditor => Handle != IntPtr.Zero && (hasEditor ??= VstHostNative.vh_has_editor(Handle) != 0);
    bool? hasEditor; // probing creates a view: do it once

    /// <summary>True while the editor window is open (UI thread).</summary>
    public bool IsEditorOpen => Handle != IntPtr.Zero && VstHostNative.vh_editor_hwnd(Handle) != IntPtr.Zero;

    /// <summary>Opens (or brings to front) the plug-in's editor window, owned by <paramref name="owner"/>. Returns the HWND or 0.</summary>
    public IntPtr OpenEditor(IntPtr owner)
    {
        if (Handle == IntPtr.Zero) return IntPtr.Zero;
        using var guard = VstPluginManager.Guard(Path, "editor");
        return VstHostNative.vh_editor_open(Handle, owner, Name + " · Csound Visual");
    }

    /// <summary>Closes the editor window.</summary>
    public void CloseEditor()
    {
        if (Handle != IntPtr.Zero) VstHostNative.vh_editor_close(Handle);
    }

    /// <summary>The parameters (fallback UI when there is no editor).</summary>
    public List<VstHostNative.ParamInfo> Parameters() => Handle == IntPtr.Zero ? [] : VstHostNative.Parameters(Handle);

    /// <summary>Current normalized value of a parameter.</summary>
    public double GetParam(uint id) => Handle == IntPtr.Zero ? 0 : VstHostNative.vh_param_get(Handle, id);

    /// <summary>Sets a normalized parameter value (controller + processor).</summary>
    public void SetParam(uint id, double value)
    {
        if (Handle != IntPtr.Zero) VstHostNative.vh_param_set(Handle, id, value);
    }

    /// <summary>A parameter value as the plug-in displays it.</summary>
    public string? ParamText(uint id, double value) => Handle == IntPtr.Zero ? null : VstHostNative.ParamText(Handle, id, value);

    /// <summary>Serialized state, or <c>null</c>.</summary>
    public byte[]? GetState() => Handle == IntPtr.Zero ? null : VstHostNative.GetState(Handle);

    /// <summary>Restores a state.</summary>
    public bool SetState(byte[] state) => Handle != IntPtr.Zero && VstHostNative.SetState(Handle, state);

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>
/// The ordered list of VST3 plug-ins that process Csound's output (inserts, like on a mixer
/// channel). Effects replace the signal; instruments add their sound to it and play the app's
/// MIDI input (on-screen keyboard and MIDI ports).
/// </summary>
/// <remarks>
/// <para>The UI thread owns <see cref="Slots"/> and edits it; after every change the chain
/// publishes an immutable array that the audio thread reads with one volatile load per block, so
/// the audio thread never waits for the UI. Before a removed plug-in is freed the chain waits
/// until the audio thread has left the block that might still be using it (at most a few ms).</para>
/// <para>Plug-ins are prepared (setupProcessing / setActive) on the UI thread: when a run starts
/// at a different sample rate, <see cref="Prepare"/> (performance thread) hands the work to the UI
/// thread through <see cref="UiInvoke"/> and waits for it.</para>
/// <para><b>Signal path.</b> <see cref="CsoundEngine"/> calls <see cref="Process"/> once per
/// control block (ksmps frames) from <c>Step()</c>, right after <c>csoundPerformKsmps</c> and the
/// 0dbfs scaling and before meters, scope and recorder, on the app's WASAPI performance thread only
/// (see <c>CsoundEngine.Inserts.cs</c>). Only channels 1–2 pass through the plug-ins; the engine
/// handles mono fold-in/out.</para>
/// <para><b>Safe release protocol</b> (<see cref="Remove"/>, <see cref="Clear"/>): publish the new
/// array → wait until no block that may hold the old array is running (<see cref="inBlock"/> /
/// <see cref="blocks"/>) → zero <see cref="VstSlot.Handle"/> under <see cref="midiLock"/> → free.
/// If the wait times out (a plug-in hangs inside the block), the native instance is not freed but
/// parked in <see cref="deferredFree"/> until a later publish sees that block has ended.
/// Both sides use full fences (<see cref="Interlocked.Exchange(ref int, int)"/>) so the audio thread's
/// "I am in a block" flag and the UI's array swap cannot be reordered past each other's reads.</para>
/// <para>Owned by the main view model (<c>MainViewModel.Plugins.cs</c>), which installs it on the
/// engine, saves it with <see cref="ToSettings"/> and restores it at start with <see cref="Restore"/>.</para>
/// </remarks>
public sealed unsafe class VstChain : IAudioInsertChain
{
    /// <summary>Authoritative, mutable slot list; UI thread only.</summary>
    readonly List<VstSlot> slots = [];

    /// <summary>
    /// Immutable copy of <see cref="slots"/> read by the audio thread (and by <see cref="SendMidi"/>).
    /// Replaced as a whole by <see cref="Publish"/>; never mutated in place.
    /// </summary>
    VstSlot[] live = [];

    /// <summary>
    /// Serialises <see cref="SendMidi"/> (MIDI driver / UI threads) against <see cref="Free"/>, so a
    /// message is never queued on a handle that is being released. Never taken by the audio thread.
    /// </summary>
    readonly object midiLock = new();

    /// <summary>1 while the audio thread is inside <see cref="Process"/>; 0 otherwise.</summary>
    int inBlock;

    /// <summary>Count of completed <see cref="Process"/> calls; lets <see cref="Publish"/> detect that the block in flight has ended.</summary>
    long blocks;

    /// <summary>
    /// Generation number of <see cref="Prepare"/> requests. Each request takes a new number and
    /// its UI-thread work only runs if the number is still current; a request that timed out bumps
    /// it again, so the work it left queued in the dispatcher becomes a no-op when it finally runs.
    /// </summary>
    /// <remarks>
    /// Bug fix: before, a <see cref="Prepare"/> that timed out (UI thread busy for more than
    /// <see cref="UiInvoke"/>'s 5 s) left its work queued; it could later call <c>vh_prepare</c>
    /// on a slot while the audio thread was inside <c>vh_process_stereo</c>, which
    /// <c>vsthost.h</c> forbids.
    /// </remarks>
    int prepareGeneration;

    /// <summary>
    /// True once the latest <see cref="Prepare"/> request completed on the UI thread.
    /// <see cref="Process"/> passes the signal through unchanged (the chain is bypassed) while it
    /// is false, so plug-ins are never run half-prepared or while a late preparation could still
    /// be touching them. Starts true: slots added outside a run are prepared by <see cref="Add"/>.
    /// </summary>
    volatile bool prepared = true;

    /// <summary>
    /// Native handles of removed slots that could not be freed yet because the audio thread was
    /// stuck inside a block that may still be using them (a hung plug-in), each with the
    /// <see cref="blocks"/> count seen when it was unpublished. Freed by <see cref="FreeDeferred"/>
    /// once that block has ended. UI thread only.
    /// </summary>
    /// <remarks>Bug fix: <see cref="Publish"/> used to give up waiting after 500 ms and the caller
    /// freed the handle anyway, a possible use-after-free inside a plug-in that was only slow.</remarks>
    readonly List<(IntPtr Handle, long BlocksSeen)> deferredFree = [];

    /// <summary>Sample rate used to prepare plug-ins added while nothing plays (the last run's rate).</summary>
    public double SampleRate { get; private set; } = 48000;

    /// <summary>Largest block plug-ins are prepared for.</summary>
    public int MaxBlock { get; private set; } = 1024;

    /// <summary>
    /// Runs an action on the UI thread and waits for it (returns false on timeout). <c>null</c> =
    /// run directly on the calling thread (console tests).
    /// </summary>
    public Func<Action, bool>? UiInvoke { get; set; }

    /// <summary>Raised on the UI thread after the slot list changed.</summary>
    public event Action? Changed;

    /// <summary>The slots in processing order (UI thread).</summary>
    public IReadOnlyList<VstSlot> Slots => slots;

    /// <inheritdoc/>
    public bool IsEmpty => Volatile.Read(ref live).Length == 0;

    /// <summary>Loads a plug-in and appends it (UI thread). Returns the slot, or <c>null</c> with the error.</summary>
    /// <param name="path">Module path.</param>
    /// <param name="classId">Class id (32 hex digits).</param>
    /// <param name="error">Why it failed; also set on <i>success</i> when the saved state could not be restored (the slot then runs with defaults).</param>
    /// <param name="state">Optional state blob from <see cref="VstSlot.GetState"/>.</param>
    /// <param name="bypass">Initial bypass flag.</param>
    /// <remarks>The slot is fully loaded, restored and prepared at <see cref="SampleRate"/> /
    /// <see cref="MaxBlock"/> <i>before</i> it is published, so the audio thread only ever sees
    /// ready instances and adding needs no wait.</remarks>
    public VstSlot? Add(string path, string classId, out string? error, byte[]? state = null, bool bypass = false)
    {
        error = null;
        if (!VstHostNative.IsAvailable)
        {
            error = VstHostNative.LoadError;
            return null;
        }
        if (VstPluginManager.IsBlocked(path))
        {
            error = Loc.S("está desactivado (Configuración › Plugins VST3): ", "is switched off (Settings › VST3 plug-ins): ") + VstPluginManager.Describe(VstPluginManager.ReasonOf(path));
            return null;
        }
        // If the plug-in takes the whole app down while loading, the guard's marker survives and
        // the next start puts it on the black list (like Cubase's plug-in manager).
        using var guard = VstPluginManager.Guard(path, "load");
        IntPtr h = VstHostNative.Load(path, classId, out error);
        if (h == IntPtr.Zero)
            return null;
        VstSlot slot;
        try
        {
            slot = new VstSlot(h, path, classId) { Bypass = bypass };
        }
        catch (Exception ex)
        {
            VstHostNative.vh_free(h);
            error = ex.Message;
            return null;
        }
        if (state is { Length: > 0 } && !slot.SetState(state))
            error = Loc.S("no se pudo restaurar el estado guardado", "the saved state could not be restored");
        int r = VstHostNative.vh_prepare(h, SampleRate, MaxBlock);
        if (r != 0)
        {
            VstHostNative.vh_free(h);
            error = Loc.S($"el plugin no aceptó {SampleRate:0} Hz / {MaxBlock} muestras (código {r})",
                          $"the plug-in refused {SampleRate:0} Hz / {MaxBlock} samples (code {r})");
            return null;
        }
        slot.PreparedRate = SampleRate;
        slot.PreparedBlock = MaxBlock;
        slot.RefreshInfo();
        slots.Add(slot);
        Publish(false);
        return slot;
    }

    /// <summary>Removes and frees a slot (UI thread).</summary>
    public void Remove(VstSlot slot)
    {
        if (!slots.Remove(slot))
            return;
        // Unpublish and wait for the audio thread before freeing; if it is stuck in a block that
        // may still use the slot, the native free is deferred instead (see Free).
        Free(slot, Publish(true));
    }

    /// <summary>Moves a slot up (−1) or down (+1) (UI thread).</summary>
    public void Move(VstSlot slot, int delta)
    {
        int i = slots.IndexOf(slot), j = i + delta;
        if (i < 0 || j < 0 || j >= slots.Count)
            return;
        slots.RemoveAt(i);
        slots.Insert(j, slot);
        Publish(false);
    }

    /// <summary>Removes every slot (UI thread).</summary>
    public void Clear()
    {
        var old = slots.ToList();
        slots.Clear();
        bool safe = Publish(true);
        foreach (var s in old)
            Free(s, safe);
    }

    /// <summary>Tells listeners that a slot property (bypass…) changed (UI thread).</summary>
    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>
    /// Releases a slot's native instance (UI thread). Must only be called after the slot was
    /// unpublished with <c>Publish(true)</c>; idempotent thanks to the zeroed handle.
    /// </summary>
    /// <param name="slot">The unpublished slot.</param>
    /// <param name="safe">The result of that <c>Publish(true)</c>: true when the audio thread is
    /// known to have left every block that may hold the slot. When false (the audio thread is stuck
    /// in a plug-in), the handle is zeroed (so the UI and MIDI stop using it) but the native
    /// instance is parked in <see cref="deferredFree"/> instead of being freed under the audio
    /// thread's feet; <see cref="FreeDeferred"/> releases it once that block has ended.</param>
    void Free(VstSlot slot, bool safe)
    {
        lock (midiLock)
        {
            IntPtr h = slot.Handle;
            slot.Handle = IntPtr.Zero;
            if (h == IntPtr.Zero)
                return;
            if (safe)
                VstHostNative.vh_free(h);
            else
                deferredFree.Add((h, lastPublishBlocks));
        }
    }

    /// <summary>
    /// <see cref="blocks"/> value read by the last <c>Publish(true)</c> that timed out; recorded
    /// with each deferred handle by <see cref="Free"/>.
    /// </summary>
    long lastPublishBlocks;

    /// <summary>
    /// Frees parked handles (<see cref="deferredFree"/>) whose block has ended: either the block
    /// counter moved past the value seen at unpublish time, or the audio thread is no longer
    /// inside <see cref="Process"/>. Any later block reads the newer array and cannot reach them.
    /// UI thread; called at every <see cref="Publish"/>.
    /// </summary>
    void FreeDeferred()
    {
        if (deferredFree.Count == 0)
            return;
        bool idle = Volatile.Read(ref inBlock) == 0;
        long now = Interlocked.Read(ref blocks);
        lock (midiLock)
            deferredFree.RemoveAll(d =>
            {
                if (!idle && now == d.BlocksSeen)
                    return false; // the stuck block is still running: keep the instance alive
                VstHostNative.vh_free(d.Handle);
                return true;
            });
    }

    /// <summary>
    /// Publishes the current order to the audio thread. With <paramref name="waitForAudio"/>, waits
    /// until a block that may have started with the previous array has finished.
    /// </summary>
    /// <returns>True when it is safe to free plug-ins that are no longer in the published array;
    /// false when the 500 ms wait ran out with the audio thread still inside the same block (a
    /// hung plug-in). Without <paramref name="waitForAudio"/> the result is meaningless (true).</returns>
    bool Publish(bool waitForAudio)
    {
        Interlocked.Exchange(ref live, slots.ToArray()); // full fence: ordered before the inBlock read
        bool safe = true;
        // If the audio thread is inside Process now, it may have read the old array; wait until
        // that block ends (blocks changes) or the audio thread leaves (inBlock = 0). A block that
        // starts after the exchange already sees the new array. The 500 ms cap only guards against
        // a hung plug-in: when it is hit the caller must not free anything (see Free / FreeDeferred).
        if (waitForAudio && Volatile.Read(ref inBlock) != 0)
        {
            long seen = Interlocked.Read(ref blocks);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref inBlock) != 0 && Interlocked.Read(ref blocks) == seen && sw.ElapsedMilliseconds < 500)
                Thread.Sleep(1);
            if (Volatile.Read(ref inBlock) != 0 && Interlocked.Read(ref blocks) == seen)
            {
                safe = false;
                lastPublishBlocks = seen;
            }
        }
        FreeDeferred(); // earlier parked instances whose stuck block has ended by now
        Changed?.Invoke();
        return safe;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Called by the performance thread before the run's first block, so no <see cref="Process"/>
    /// call can overlap the <c>vh_prepare</c> calls. Slots already prepared for this rate and a
    /// block at least as large are left alone (re-preparing resets plug-in tails). <see cref="MaxBlock"/>
    /// only grows, so a later run with a smaller block reuses the existing preparation.
    /// </remarks>
    /// <exception cref="TimeoutException">The UI thread did not run the work within
    /// <see cref="UiInvoke"/>'s time limit.</exception>
    public string? Prepare(double sampleRate, int maxBlock)
    {
        string? text = null;
        // Bypass the chain until this request has completed (see "prepared"), and take a new
        // generation number so any older request still queued in the dispatcher turns into a no-op.
        prepared = false;
        int generation = Interlocked.Increment(ref prepareGeneration);
        void Work()
        {
            // Stale request (it timed out, or a newer Prepare superseded it): do nothing. Without
            // this check the late work could call vh_prepare while the audio thread processes.
            if (Volatile.Read(ref prepareGeneration) != generation)
                return;
            SampleRate = sampleRate;
            MaxBlock = Math.Max(MaxBlock, maxBlock);
            var failed = new List<string>();
            foreach (var s in slots)
            {
                if (s.Handle == IntPtr.Zero || s.Crashed)
                    continue;
                if (Math.Abs(s.PreparedRate - sampleRate) < 0.5 && s.PreparedBlock >= maxBlock)
                    continue;
                int r = VstHostNative.vh_prepare(s.Handle, sampleRate, MaxBlock);
                if (r == 0)
                {
                    s.PreparedRate = sampleRate;
                    s.PreparedBlock = MaxBlock;
                    s.RefreshInfo();
                }
                else
                {
                    s.PreparedRate = 0;
                    failed.Add(s.Name);
                }
            }
            text = Describe();
            if (failed.Count > 0)
                text += Loc.S(" · ⚠ no se pudieron preparar: ", " · ⚠ could not be prepared: ") + string.Join(", ", failed);
            // Re-enable processing only if no timeout/newer request invalidated this one meanwhile.
            if (Volatile.Read(ref prepareGeneration) == generation)
                prepared = true;
        }
        if (UiInvoke is { } invoke)
        {
            if (!invoke(Work))
            {
                // Cancel the queued work: when the UI thread finally runs it, the generation check
                // makes it return at once. "prepared" stays false, so the chain stays bypassed (the
                // engine also drops the chain for this run when it catches the exception below).
                Interlocked.Increment(ref prepareGeneration);
                prepared = false;
                throw new TimeoutException(Loc.S("la interfaz no respondió a tiempo para preparar los plugins",
                                                 "the UI did not answer in time to prepare the plug-ins"));
            }
        }
        else
            Work();
        return text;
    }

    /// <summary>
    /// Raised on the UI thread after a plug-in reported that its parameter values or titles
    /// changed (VST3 <c>restartComponent</c> flags 4 / 16): parameter views should re-read them.
    /// </summary>
    public event Action<VstSlot>? ParametersChanged;

    /// <summary>
    /// Reacts to the VST3 <c>restartComponent</c> requests the plug-ins raised since the last call
    /// (UI thread; the main view model calls it every 250 ms). Needs bridge ABI v2
    /// (<see cref="VstHostNative.SupportsRestartFlags"/>); does nothing with an older DLL.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Latency (8) or I/O (2) changed</b>: the plug-in must be set up again. It is first
    /// taken off the audio thread (published array without it, waiting for the current block),
    /// re-prepared with the same rate and block size, then published again — the rest of the chain
    /// keeps playing meanwhile. If the audio thread is stuck in a plug-in (the wait times out), the
    /// re-prepare is skipped this time: calling <c>vh_prepare</c> while it may still process
    /// that instance is forbidden by the bridge.</item>
    /// <item><b>Parameter values (4) or titles (16) changed</b>: <see cref="ParametersChanged"/>.</item>
    /// </list>
    /// </remarks>
    public void PollRestartRequests()
    {
        if (!VstHostNative.SupportsRestartFlags)
            return;
        const int IoChanged = 2, ParamValuesChanged = 4, LatencyChanged = 8, ParamTitlesChanged = 16;
        foreach (var s in slots.ToList())
        {
            if (s.Handle == IntPtr.Zero || s.Crashed)
                continue;
            int flags = VstHostNative.vh_take_restart_flags(s.Handle);
            if (flags == 0)
                continue;
            if ((flags & (IoChanged | LatencyChanged)) != 0 && s.PreparedRate > 0)
            {
                // Unpublish only this slot, then re-prepare it while the audio thread cannot reach it.
                Interlocked.Exchange(ref live, slots.Where(x => x != s).ToArray());
                bool safe = WaitForAudioBlock();
                if (safe && s.Handle != IntPtr.Zero && VstHostNative.vh_prepare(s.Handle, s.PreparedRate, s.PreparedBlock) == 0)
                    s.RefreshInfo();
                Publish(false); // put it back (also when the re-prepare was skipped or failed)
            }
            if ((flags & (ParamValuesChanged | ParamTitlesChanged)) != 0)
                ParametersChanged?.Invoke(s);
        }
    }

    /// <summary>
    /// Waits (≤ 500 ms) until a block that may have started with the previous live array has
    /// finished — the same handshake as <see cref="Publish"/> with <c>waitForAudio</c>.
    /// </summary>
    /// <returns>True when the audio thread is known to have left that block.</returns>
    bool WaitForAudioBlock()
    {
        if (Volatile.Read(ref inBlock) == 0)
            return true;
        long seen = Interlocked.Read(ref blocks);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref inBlock) != 0 && Interlocked.Read(ref blocks) == seen && sw.ElapsedMilliseconds < 500)
            Thread.Sleep(1);
        return !(Volatile.Read(ref inBlock) != 0 && Interlocked.Read(ref blocks) == seen);
    }

    /// <summary>"Plug-ins: A → B (bypassed) → C" for the console, or <c>null</c> when empty.</summary>
    public string? Describe()
    {
        if (slots.Count == 0)
            return null;
        var names = slots.Select(s => s.Name + (s.Bypass ? Loc.S(" (bypass)", " (bypassed)") : "") + (s.IsInstrument ? " ♪" : ""));
        return Loc.S("Plugins VST3: ", "VST3 plug-ins: ") + string.Join(" → ", names) + $" · {SampleRate:0} Hz";
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Audio (performance) thread, once per control block: no locks, no allocation. Runs every
    /// non-bypassed, non-crashed slot in order, in place on <paramref name="left"/>/<paramref name="right"/>
    /// (<paramref name="frames"/> = ksmps). A plug-in that crashes (native result 2) is marked and
    /// skipped from then on; the rest of the chain keeps working.
    /// </remarks>
    public void Process(float* left, float* right, int frames)
    {
        // The latest Prepare has not completed (it is running, timed out, or was superseded):
        // the chain is bypassed so no plug-in runs while a preparation may still touch it.
        if (!prepared)
            return;
        Interlocked.Exchange(ref inBlock, 1); // full fence: ordered before the live read
        try
        {
            var arr = Volatile.Read(ref live);
            foreach (var s in arr)
            {
                if (s.Bypass || s.Crashed)
                    continue;
                IntPtr h = s.Handle;
                if (h == IntPtr.Zero)
                    continue;
                if (VstHostNative.vh_process_stereo(h, left, right, frames) == 2)
                    s.Crashed = true;
            }
        }
        finally
        {
            Interlocked.Increment(ref blocks);
            Volatile.Write(ref inBlock, 0);
        }
    }

    /// <summary>Sends a MIDI 1.0 message to every instrument in the chain (any thread).</summary>
    /// <param name="status">Status byte including the channel (0x8n note off, 0x9n note on, 0xBn CC, 0xDn pressure, 0xEn pitch bend).</param>
    /// <param name="data1">First data byte (note / controller / bend LSB), 0…127.</param>
    /// <param name="data2">Second data byte (velocity / value / bend MSB), 0…127.</param>
    /// <remarks>Called from the MIDI hub (driver callback threads) and the on-screen keyboard. The
    /// native call only enqueues into a spin-locked inbox, so the brief <see cref="midiLock"/> never
    /// delays the audio thread.</remarks>
    public void SendMidi(byte status, byte data1, byte data2)
    {
        var arr = Volatile.Read(ref live);
        if (arr.Length == 0)
            return;
        lock (midiLock)
            foreach (var s in arr)
                if (s.IsInstrument && s.Handle != IntPtr.Zero)
                    VstHostNative.vh_midi(s.Handle, status, data1, data2);
    }

    /// <summary>Snapshot for the settings file (UI thread).</summary>
    public List<VstSlotSettings> ToSettings() =>
        [.. slots.Select(s => new VstSlotSettings
        {
            Path = s.Path,
            ClassId = s.ClassId,
            Name = s.Name,
            Bypass = s.Bypass,
            State = s.GetState() is { } st ? Convert.ToBase64String(st) : "",
        })];

    /// <summary>
    /// Rebuilds the chain from saved slots (UI thread). Returns one message per slot that could
    /// not be restored.
    /// </summary>
    /// <param name="saved">Slots in processing order, as written by <see cref="ToSettings"/>.</param>
    /// <remarks>Missing modules, switched-off (black-listed) modules and corrupt base64 states are
    /// reported or tolerated instead of aborting: the remaining slots are still restored.</remarks>
    public List<string> Restore(IEnumerable<VstSlotSettings> saved)
    {
        var problems = new List<string>();
        foreach (var s in saved)
        {
            byte[]? state = null;
            try { if (s.State.Length > 0) state = Convert.FromBase64String(s.State); } catch (FormatException) { }
            if (!File.Exists(s.Path) && !Directory.Exists(s.Path))
            {
                problems.Add($"{s.Name}: {Loc.S("no se encontró", "not found")} ({s.Path})");
                continue;
            }
            if (Add(s.Path, s.ClassId, out string? error, state, s.Bypass) is null)
                problems.Add($"{s.Name}: {error}");
            else if (error is not null)
                problems.Add($"{s.Name}: {error}");
        }
        return problems;
    }
}
