using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CsoundVisual.Localization;

namespace CsoundVisual.Midi;

/// <summary>A MIDI input port as listed by a backend.</summary>
/// <param name="Id">Backend-specific id (WinMM device index as text; for MIDI 2.0 the Windows MIDI Services endpoint device id).</param>
/// <param name="Name">Port name.</param>
/// <param name="Backend">Backend that provides the port ("WinMM", "MIDI 2.0").</param>
public sealed record MidiPort(string Id, string Name, string Backend)
{
    /// <summary>Text for pickers.</summary>
    public string Display => $"{Name}  ·  {Backend}";

    /// <inheritdoc/>
    public override string ToString() => Display;
}

/// <summary>
/// MIDI 1.0 input through the classic Windows multimedia API (WinMM, <c>midiIn*</c>).
/// </summary>
/// <remarks>
/// <para>
/// WinMM is the "legacy" path: every MIDI driver and USB-MIDI device supports it, and on Windows
/// 11 with Windows MIDI Services the in-box service also exposes MIDI 2.0 devices to WinMM
/// (down-converted to MIDI 1.0). It is therefore the always-available backend.
/// </para>
/// <para>
/// The driver calls <see cref="OnMidiIn"/> on its own thread. The callback only decodes the
/// short message and hands it to <see cref="Received"/>; it never calls other <c>midiIn*</c>
/// functions, as the WinMM documentation requires.
/// </para>
/// </remarks>
public sealed unsafe class WinMmMidiInput : IDisposable
{
    /// <summary><c>MIM_DATA</c>: a complete short MIDI message arrived (packed in <c>dwParam1</c>).</summary>
    const int MIM_DATA = 0x3C3;
    /// <summary><c>CALLBACK_FUNCTION</c>: <c>midiInOpen</c>'s callback argument is a function pointer.</summary>
    const uint CALLBACK_FUNCTION = 0x30000;

    /// <summary>Mirror of <c>MIDIINCAPSW</c> (manufacturer id, product id, driver version, 32-char name, support flags).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MidiInCaps
    {
        public ushort Mid, Pid;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Support;
    }

    // WinMM entry points. All return MMRESULT (0 = MMSYSERR_NOERROR).

    [DllImport("winmm.dll")] static extern uint midiInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int midiInGetDevCapsW(nuint deviceId, out MidiInCaps caps, uint size);
    [DllImport("winmm.dll")] static extern int midiInOpen(out IntPtr handle, uint deviceId, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] static extern int midiInStart(IntPtr handle);
    [DllImport("winmm.dll")] static extern int midiInStop(IntPtr handle);
    [DllImport("winmm.dll")] static extern int midiInReset(IntPtr handle);
    [DllImport("winmm.dll")] static extern int midiInClose(IntPtr handle);

    /// <summary>The <c>HMIDIIN</c> returned by <c>midiInOpen</c>; zero once disposed.</summary>
    IntPtr handle;

    /// <summary>
    /// Normal GC handle to this object, passed to the driver as the callback's instance data.
    /// It keeps the object alive while the port is open and lets the static callback find it
    /// again; freed in <see cref="Dispose"/> after the port is closed.
    /// </summary>
    GCHandle self;

    /// <summary>The port this input listens to.</summary>
    public MidiPort Port { get; }

    /// <summary>Raised on the driver's thread for every channel-voice message.</summary>
    public event Action<MidiMessage>? Received;

    WinMmMidiInput(MidiPort port) => Port = port;

    /// <summary>Lists the WinMM input ports.</summary>
    public static List<MidiPort> ListPorts()
    {
        var ports = new List<MidiPort>();
        uint n = midiInGetNumDevs();
        for (uint i = 0; i < n; i++)
            if (midiInGetDevCapsW(i, out var caps, (uint)Marshal.SizeOf<MidiInCaps>()) == 0)
                ports.Add(new MidiPort(i.ToString(), caps.Name, "WinMM"));
        return ports;
    }

    /// <summary>Opens and starts a port. Throws <see cref="InvalidOperationException"/> with a readable reason.</summary>
    /// <param name="port">A WinMM port from <see cref="ListPorts"/> (its id is the device index).</param>
    /// <exception cref="InvalidOperationException">The port could not be opened; error 4 (<c>MMSYSERR_ALLOCATED</c>) means another application holds it.</exception>
    public static WinMmMidiInput Open(MidiPort port)
    {
        var input = new WinMmMidiInput(port);
        input.self = GCHandle.Alloc(input);
        delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr, void> cb = &OnMidiIn;
        int r = midiInOpen(out input.handle, uint.Parse(port.Id), (IntPtr)cb, GCHandle.ToIntPtr(input.self), CALLBACK_FUNCTION);
        if (r != 0)
        {
            input.self.Free();
            throw new InvalidOperationException(r == 4
                ? Loc.S($"El puerto MIDI «{port.Name}» está en uso por otra aplicación.", $"MIDI port «{port.Name}» is in use by another application.")
                : Loc.S($"No se pudo abrir el puerto MIDI «{port.Name}» (error {r}).", $"Could not open MIDI port «{port.Name}» (error {r})."));
        }
        midiInStart(input.handle);
        return input;
    }

    /// <summary>
    /// <c>MidiInProc</c> callback, invoked by the driver on its own thread for every message
    /// (open/close notifications, short data, SysEx buffers…). Only <c>MIM_DATA</c> is handled.
    /// </summary>
    /// <param name="handle">The <c>HMIDIIN</c>.</param>
    /// <param name="msg">Notification code (<c>MIM_*</c>).</param>
    /// <param name="instance">The <see cref="GCHandle"/> of the owning <see cref="WinMmMidiInput"/>.</param>
    /// <param name="param1">For <c>MIM_DATA</c>: the packed message, status in the low byte, then data1, data2.</param>
    /// <param name="param2">For <c>MIM_DATA</c>: driver timestamp in ms since <c>midiInStart</c> (unused; the
    /// Stopwatch clock is used so all backends share one time base).</param>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static void OnMidiIn(IntPtr handle, uint msg, IntPtr instance, IntPtr param1, IntPtr param2)
    {
        if (msg != MIM_DATA || instance == IntPtr.Zero || GCHandle.FromIntPtr(instance).Target is not WinMmMidiInput input)
            return;
        // DWORD_PTR on x64: only the low 24 bits carry the message.
        uint data = (uint)param1.ToInt64();
        byte status = (byte)data, d1 = (byte)(data >> 8), d2 = (byte)(data >> 16);
        if (status < 0x80 || status >= 0xF0)
            return; // running status is resolved by the driver; system messages are ignored
        if (MidiMessage.FromMidi1(status, d1, d2, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency) is { } m)
            input.Received?.Invoke(m);
    }

    /// <summary>Stops and closes the port, then releases the GC handle.</summary>
    /// <remarks>
    /// Order matters: <c>midiInReset</c>/<c>midiInClose</c> guarantee the driver will not call
    /// <see cref="OnMidiIn"/> again, and only then may <see cref="self"/> be freed (otherwise a late
    /// callback would dereference a dead handle). Must not be called from the callback thread.
    /// </remarks>
    public void Dispose()
    {
        if (handle != IntPtr.Zero)
        {
            midiInStop(handle);
            midiInReset(handle);
            midiInClose(handle);
            handle = IntPtr.Zero;
        }
        if (self.IsAllocated) self.Free();
    }
}
