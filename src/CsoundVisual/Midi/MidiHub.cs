using System.Collections.Concurrent;

namespace CsoundVisual.Midi;

/// <summary>
/// Central MIDI router. Messages from hardware ports and from the on-screen keyboard enter here
/// and are fanned out to three consumers:
/// <list type="bullet">
/// <item>the Csound engine, as MIDI 1.0 bytes pulled by Csound's MIDI read callback each control cycle;</item>
/// <item>the UI (MIDI monitor, MIDI learn and controller → slider mappings), drained every frame;</item>
/// <item>optional raw listeners (<see cref="MessageReceived"/>), called on the producing thread.</item>
/// </list>
/// </summary>
/// <remarks>
/// The hub is backend-agnostic: <see cref="Midi2Input"/> (Windows MIDI Services, MIDI 2.0 / UMP,
/// full 16/32-bit resolution) and <see cref="WinMmMidiInput"/> (MIDI 1.0, legacy) both feed it
/// the same <see cref="MidiMessage"/> values.
/// <para>
/// <b>Threads.</b> <see cref="Inject"/> runs on whichever thread produced the message: the WinMM
/// driver callback thread, the Windows MIDI Services (WinRT) event thread, or the UI thread for
/// the on-screen keyboard. <see cref="ReadForCsound"/> runs on the Csound performance thread;
/// <see cref="DrainForUi"/> on the UI thread. The hand-off between them uses lock-free
/// <see cref="ConcurrentQueue{T}"/>s, so no producer ever waits for Csound or the UI.
/// <see cref="Open"/>, <see cref="Close"/> and <see cref="Dispose"/> touch the port list without a
/// lock and must only be called from the UI thread.
/// </para>
/// </remarks>
public sealed class MidiHub : IDisposable
{
    // Whole messages (not bytes), so concurrent producers can never interleave their bytes.
    /// <summary>MIDI 1.0 messages waiting for Csound's read callback (producer threads → performance thread).</summary>
    readonly ConcurrentQueue<(byte Status, byte D1, byte D2)> toCsound = new();
    /// <summary>Full-resolution messages waiting for the UI (producer threads → UI thread).</summary>
    readonly ConcurrentQueue<MidiMessage> toUi = new();
    /// <summary>Open ports and the backend object that delivers their messages (UI thread only).</summary>
    readonly List<(MidiPort Port, IDisposable Input)> inputs = [];

    /// <summary>Raised on the producing thread (driver or UI) for every message.</summary>
    public event Action<MidiMessage>? MessageReceived;

    /// <summary>Ports currently open.</summary>
    public IReadOnlyList<MidiPort> OpenPorts => [.. inputs.Select(i => i.Port)];

    /// <summary>Why MIDI 2.0 is unavailable, or <c>null</c> when it works.</summary>
    public static string? Midi2Status => Midi2Input.IsAvailable(out string? reason) ? null : reason;

    /// <summary>True when Csound should enable MIDI input (a port is open or the keyboard is used).</summary>
    /// <remarks>Read by the engine when a performance starts, to decide whether to install the host MIDI callbacks.</remarks>
    public bool Active { get; set; } = true;

    /// <summary>
    /// Lists every available input port: Windows MIDI Services (MIDI 2.0) endpoints first, then
    /// the classic WinMM ports. The same device usually appears in both; open only one of them,
    /// otherwise its notes arrive twice.
    /// </summary>
    public static List<MidiPort> ListPorts() => [.. Midi2Input.ListPorts(), .. WinMmMidiInput.ListPorts()];

    /// <summary>Opens a port (if not already open). Returns an error message, or <c>null</c> on success.</summary>
    /// <param name="port">A port from <see cref="ListPorts"/>; its <see cref="MidiPort.Backend"/> selects the implementation.</param>
    /// <returns><c>null</c> on success (or when already open), otherwise the backend's error text for the UI.</returns>
    public string? Open(MidiPort port)
    {
        if (inputs.Any(i => i.Port.Id == port.Id && i.Port.Backend == port.Backend))
            return null;
        try
        {
            if (port.Backend == Midi2Input.BackendName)
            {
                var input = Midi2Input.Open(port);
                input.Received += Inject;
                inputs.Add((port, input));
            }
            else
            {
                var input = WinMmMidiInput.Open(port);
                input.Received += Inject;
                inputs.Add((port, input));
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Closes a port.</summary>
    public void Close(MidiPort port)
    {
        foreach (var entry in inputs.Where(i => i.Port.Id == port.Id && i.Port.Backend == port.Backend).ToList())
        {
            if (entry.Input is WinMmMidiInput w) w.Received -= Inject;
            if (entry.Input is Midi2Input m) m.Received -= Inject;
            entry.Input.Dispose();
            inputs.Remove(entry);
        }
    }

    /// <summary>Injects a message (from a port, the on-screen keyboard or a MIDI file) into the hub.</summary>
    /// <remarks>
    /// Thread-safe and non-blocking; called on the producer's thread. The message is converted to
    /// MIDI 1.0 for Csound here, so the performance thread only copies bytes.
    /// </remarks>
    public void Inject(MidiMessage message)
    {
        toCsound.Enqueue(message.ToMidi1());
        toUi.Enqueue(message);
        // Bug fix (unbounded queues): both queues used to grow without limit whenever their
        // consumer was not draining — the UI queue while the window is minimised or the UI thread
        // is busy, the Csound queue while nothing is playing (it is only cleared when the next
        // performance starts) — so a controller streaming data (aftertouch, clock, a held
        // mod wheel) slowly ate memory. Each queue is now capped; when full the OLDEST messages
        // are dropped (the newest state is what matters for meters, learn and mappings) and
        // counted. Count/TryDequeue are lock-free, so the producer thread still never blocks.
        while (toUi.Count > MaxQueued && toUi.TryDequeue(out _))
            Interlocked.Increment(ref droppedForUi);
        while (toCsound.Count > MaxQueued && toCsound.TryDequeue(out _))
            Interlocked.Increment(ref droppedForCsound);
        MessageReceived?.Invoke(message);
    }

    /// <summary>
    /// Maximum messages kept in each queue. Far above what a consumer that is running needs (the
    /// UI drains every frame, Csound every control cycle), so it only bites when one stalls.
    /// </summary>
    const int MaxQueued = 4096;
    /// <summary>Messages discarded from the UI queue because it was full (interlocked).</summary>
    long droppedForUi;
    /// <summary>Messages discarded from the Csound queue because it was full (interlocked).</summary>
    long droppedForCsound;

    /// <summary>Messages dropped (oldest first) because the UI did not drain its queue in time.</summary>
    public long DroppedForUi => Interlocked.Read(ref droppedForUi);

    /// <summary>Messages dropped (oldest first) because Csound did not read its queue (typically: nothing playing).</summary>
    public long DroppedForCsound => Interlocked.Read(ref droppedForCsound);

    /// <summary>
    /// Copies pending MIDI 1.0 bytes into Csound's buffer (called from the performance thread
    /// by the MIDI read callback). Only whole messages are copied.
    /// </summary>
    /// <param name="buffer">Csound's MIDI input buffer (owned by Csound, valid only during the callback).</param>
    /// <param name="capacity">Size of <paramref name="buffer"/> in bytes.</param>
    /// <returns>Number of bytes written. Every message is written with its status byte (no running status).</returns>
    public unsafe int ReadForCsound(byte* buffer, int capacity)
    {
        int n = 0;
        // Stop while 3 bytes of room remain, so a message is never split across two callbacks;
        // what does not fit stays queued for the next control cycle.
        while (n + 3 <= capacity && toCsound.TryDequeue(out var m))
        {
            buffer[n++] = m.Status;
            buffer[n++] = m.D1;
            if (MidiMessage.DataLength(m.Status) == 2)
                buffer[n++] = m.D2;
        }
        return n;
    }

    /// <summary>Drops bytes queued for Csound (used when a performance starts).</summary>
    public void ClearCsoundQueue()
    {
        while (toCsound.TryDequeue(out _)) { }
    }

    /// <summary>Takes the messages received since the last call, for the UI.</summary>
    /// <param name="max">Upper bound per call, so a flood of controller data cannot stall one UI frame; the rest waits for the next call.</param>
    /// <returns>A new list in arrival order (possibly empty).</returns>
    public List<MidiMessage> DrainForUi(int max = 256)
    {
        var list = new List<MidiMessage>();
        while (list.Count < max && toUi.TryDequeue(out var m))
            list.Add(m);
        return list;
    }

    /// <summary>Closes every port and shuts down the shared Windows MIDI Services session.</summary>
    public void Dispose()
    {
        foreach (var i in inputs) i.Input.Dispose();
        inputs.Clear();
        Midi2Input.Shutdown();
    }
}
