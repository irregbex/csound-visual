using System.Runtime.InteropServices;

namespace CsoundVisual.Interop;

/// <summary>
/// Raw P/Invoke bindings for <c>csound7_64.dll</c> (Csound 7, built with 64-bit <c>double</c> samples,
/// so every <c>cs_float</c> in the C headers maps to <see cref="double"/> here).
/// </summary>
/// <remarks>
/// <para>
/// Only the subset of the API the application actually uses is declared. Every signature mirrors
/// <c>@Source/Csound7/include/csound/csound.h</c> (and <c>csound_misc.h</c> for the opcode list);
/// if Csound is upgraded, re-check them against the new headers.
/// </para>
/// <para>
/// The library is resolved at runtime by <see cref="CsoundLocator"/>, which installs a
/// <see cref="NativeLibrary"/> import resolver pointing to the installation it found. Nothing here
/// may be called before <see cref="CsoundLocator.Initialize"/> succeeded.
/// </para>
/// <para>
/// Threading: a <c>CSOUND*</c> instance is not thread-safe. The application only touches a
/// performing instance from its own performance thread (see <c>CsoundEngine</c>); short-lived
/// helper instances (device and opcode enumeration) are created, used and destroyed on a single
/// background thread.
/// </para>
/// </remarks>
internal static unsafe partial class CsoundNative
{
    /// <summary>Base name of the native library (without extension).</summary>
    public const string Lib = "csound7_64";

    /// <summary><c>CSOUNDINIT_NO_SIGNAL_HANDLER</c>: do not let Csound install SIGINT/SIGTERM handlers.</summary>
    public const int CSOUNDINIT_NO_SIGNAL_HANDLER = 1;

    /// <summary><c>CSOUNDINIT_NO_ATEXIT</c>: do not let Csound register an <c>atexit</c> cleanup hook.</summary>
    public const int CSOUNDINIT_NO_ATEXIT = 2;

    /// <summary>Mask that extracts the message type from a message attribute word (<c>msg_attr.h</c>).</summary>
    public const int CSOUNDMSG_TYPE_MASK = 0x7000;

    /// <summary>Message type: error. Csound also uses it for plain informational lines, see <c>CsoundEngine</c>.</summary>
    public const int CSOUNDMSG_ERROR = 0x1000;

    /// <summary>Message type: warning.</summary>
    public const int CSOUNDMSG_WARNING = 0x4000;

    /// <summary>
    /// Mirror of the C <c>CS_AUDIODEVICE</c> struct: one entry of the list returned by
    /// <see cref="csoundGetAudioDevList"/>. All strings are NUL-terminated UTF-8 in fixed 128-byte buffers.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CsAudioDevice
    {
        /// <summary>Human readable name, e.g. <c>"Speakers (Realtek USB2.0 Audio) [Windows WDM-KS, 0 in, 2 out]"</c>.</summary>
        public fixed byte DeviceName[128];

        /// <summary>Identifier to pass after <c>-o</c>, e.g. <c>"dac13"</c>.</summary>
        public fixed byte DeviceId[128];

        /// <summary>Name of the real-time module that reported the device.</summary>
        public fixed byte RtModule[128];

        /// <summary>Maximum channel count supported by the device.</summary>
        public int MaxNchnls;

        /// <summary>1 for output devices, 0 for input devices.</summary>
        public int IsOutput;
    }

    /// <summary>
    /// Mirror of the C <c>opcodeListEntry</c> struct returned by <see cref="csoundNewOpcodeList"/>.
    /// One opcode name appears once per overload (type signature).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct OpcodeListEntry
    {
        /// <summary>Opcode name (UTF-8 <c>char*</c>).</summary>
        public IntPtr OpName;

        /// <summary>Output type string, e.g. <c>"a"</c> or <c>""</c> for no outputs.</summary>
        public IntPtr OutTypes;

        /// <summary>Input type string, e.g. <c>"kkoOOo"</c>.</summary>
        public IntPtr InTypes;

        /// <summary>Internal opcode flags (unused by the app).</summary>
        public int Flags;

        /// <summary>Non-zero when this overload is deprecated.</summary>
        public int Deprecated;
    }

    /// <summary>One-time global initialisation; must run before the first <see cref="csoundCreate"/>.</summary>
    [LibraryImport(Lib)]
    public static partial int csoundInitialize(int flags);

    /// <summary>Creates an engine instance. <paramref name="opcodedir"/> overrides the plugin search directory.</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr csoundCreate(IntPtr hostData, string? opcodedir);

    /// <summary>Destroys an instance, closing audio devices and flushing pending messages.</summary>
    [LibraryImport(Lib)]
    public static partial void csoundDestroy(IntPtr csound);

    /// <summary>Returns the version as an integer, e.g. <c>7000</c> for 7.0.0.</summary>
    [LibraryImport(Lib)]
    public static partial int csoundGetVersion();

    /// <summary>
    /// Applies command-line style options. Several options may be passed in one string separated
    /// by spaces, which also means a single option cannot contain a bare space. A value with spaces
    /// (e.g. a file path) works only when quoted with the quote glued to the flag:
    /// <c>-o"C:\a b\x.wav"</c> or <c>--output="C:\a b\x.wav"</c> (verified on Csound 7.0;
    /// <c>-o "C:\a b\x.wav"</c> fails). Must be called before compiling.
    /// </summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int csoundSetOption(IntPtr csound, string option);

    /// <summary>Compiles a CSD. <paramref name="mode"/> 1 means <paramref name="csd"/> is the CSD text itself, 0 a file name.</summary>
    /// <param name="csound">Instance created by <see cref="csoundCreate"/>.</param>
    /// <param name="csd">CSD text or path, depending on <paramref name="mode"/>.</param>
    /// <param name="mode">1 = text, 0 = file name.</param>
    /// <param name="async">Non-zero to queue the compilation instead of running it synchronously (the app passes 0).</param>
    /// <returns>0 on success, a negative Csound error code otherwise.</returns>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int csoundCompileCSD(IntPtr csound, string csd, int mode, int async);

    /// <summary>
    /// Compiles orchestra code into a running instance. Instruments defined in <paramref name="orc"/>
    /// replace existing definitions with the same name/number; new events use the new definition.
    /// </summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int csoundCompileOrc(IntPtr csound, string orc, int async);

    /// <summary>
    /// Disables Csound's own audio device handling for this instance: the host reads
    /// <see cref="csoundGetSpout"/> after each <see cref="csoundPerformKsmps"/> and feeds the
    /// device itself. Must be called after <see cref="csoundCreate"/> and before compiling.
    /// </summary>
    [LibraryImport(Lib)]
    public static partial void csoundSetHostAudioIO(IntPtr csound);

    /// <summary>Prepares the instance for performance (opens audio devices, runs the header code).</summary>
    [LibraryImport(Lib)]
    public static partial int csoundStart(IntPtr csound);

    /// <summary>Performs one control cycle (<c>ksmps</c> frames). 0 = keep going, &gt;0 = score finished, &lt;0 = error.</summary>
    [LibraryImport(Lib)]
    public static partial int csoundPerformKsmps(IntPtr csound);

    /// <summary>Sample rate of the running instance.</summary>
    [LibraryImport(Lib)]
    public static partial double csoundGetSr(IntPtr csound);

    /// <summary>Frames per control cycle.</summary>
    [LibraryImport(Lib)]
    public static partial uint csoundGetKsmps(IntPtr csound);

    /// <summary>Number of output (<paramref name="isInput"/> = 0) or input (1) channels.</summary>
    [LibraryImport(Lib)]
    public static partial uint csoundGetChannels(IntPtr csound, int isInput);

    /// <summary>Value of <c>0dbfs</c> (full-scale amplitude).</summary>
    [LibraryImport(Lib)]
    public static partial double csoundGet0dBFS(IntPtr csound);

    /// <summary>
    /// Pointer to the interleaved output buffer of the last control cycle
    /// (<c>ksmps * nchnls</c> doubles). Valid until the instance is reset or destroyed.
    /// </summary>
    [LibraryImport(Lib)]
    public static partial double* csoundGetSpout(IntPtr csound);

    /// <summary>
    /// Pointer to the interleaved input buffer (<c>ksmps * nchnls_i</c> doubles, 0dbfs scale) read by
    /// <c>in</c>/<c>inch</c> during the next control cycle. With host audio I/O the host writes it
    /// before every <see cref="csoundPerformKsmps"/>; Csound never clears it (checked on Csound 7.0),
    /// and no <c>-i</c> option is needed.
    /// </summary>
    [LibraryImport(Lib)]
    public static partial double* csoundGetSpin(IntPtr csound);

    /// <summary>Current score time in seconds.</summary>
    [LibraryImport(Lib)]
    public static partial double csoundGetScoreTime(IntPtr csound);

    /// <summary>
    /// Installs a message callback receiving already formatted strings. The callback may receive
    /// partial lines; the caller must join them on <c>'\n'</c>.
    /// </summary>
    /// <remarks>
    /// C signature: <c>void (*)(CSOUND*, int attr, const char* message)</c>. <c>attr</c> carries the
    /// message type (mask with <see cref="CSOUNDMSG_TYPE_MASK"/>); <c>message</c> is UTF-8 and only
    /// valid during the call, so the callback must copy it. It is invoked on whatever thread is
    /// running Csound at that moment (compile, start, or the performance thread).
    /// </remarks>
    [LibraryImport(Lib)]
    public static partial void csoundSetMessageStringCallback(
        IntPtr csound, delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void> callback);

    /// <summary>Writes a value into a named control (k-rate) channel.</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void csoundSetControlChannel(IntPtr csound, string name, double value);

    /// <summary>Schedules score events given as text (one or more lines).</summary>
    /// <remarks>
    /// <paramref name="async"/> = 0 inserts the events immediately (call from the performance
    /// thread between control cycles); non-zero queues them through Csound's own lock-free message
    /// FIFO so they are picked up at the start of the next cycle.
    /// </remarks>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void csoundEventString(IntPtr csound, string message, int async);

    /// <summary>Length of function table <paramref name="table"/>, or -1 if it does not exist.</summary>
    [LibraryImport(Lib)]
    public static partial int csoundTableLength(IntPtr csound, int table);

    /// <summary>Gets a pointer to the data of a function table; returns its length or -1.</summary>
    /// <remarks>
    /// <c>*tablePtr</c> receives a pointer into Csound's own table storage (not a copy): it stays
    /// valid only until the table is redefined or the instance is destroyed, so read it on the
    /// performance thread and copy what the UI needs.
    /// </remarks>
    [LibraryImport(Lib)]
    public static partial int csoundGetTable(IntPtr csound, double** tablePtr, int tableNum);

    /// <summary>
    /// Enumerates loaded modules. Returns 0 while <paramref name="number"/> is valid.
    /// The first call also loads the real-time module selected with <c>-+rtaudio</c>.
    /// </summary>
    [LibraryImport(Lib)]
    public static partial int csoundGetModule(IntPtr csound, int number, IntPtr* name, IntPtr* type);

    /// <summary>
    /// Lists audio devices of the active real-time module. With a null <paramref name="list"/> it
    /// only returns the count; otherwise it fills one <see cref="CsAudioDevice"/> per device.
    /// </summary>
    [LibraryImport(Lib)]
    public static partial int csoundGetAudioDevList(IntPtr csound, CsAudioDevice* list, int isOutput);

    /// <summary>Allocates an alphabetically sorted list of every opcode overload. Free with <see cref="csoundDisposeOpcodeList"/>.</summary>
    [LibraryImport(Lib)]
    public static partial int csoundNewOpcodeList(IntPtr csound, OpcodeListEntry** list);

    /// <summary>Frees a list returned by <see cref="csoundNewOpcodeList"/>.</summary>
    [LibraryImport(Lib)]
    public static partial void csoundDisposeOpcodeList(IntPtr csound, OpcodeListEntry* list);

    // ---------------------------------------------------------------------------------------
    //  Debugger (csdebug.h) — used for live variable values
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Mirror of <c>debug_instr_t</c>: one active instrument instance (64-bit layout).
    /// Offsets: varPoolHead 0, lclbas 8, instrptr 16, p1 24, p2 32, p3 40, kcounter 48, line 56, next 64.
    /// </summary>
    /// <remarks>
    /// The list is allocated by Csound and owned by the caller until it is handed back to
    /// <see cref="csoundDebugFreeInstrInstances"/>; the pointers inside (<see cref="VarPoolHead"/>,
    /// <see cref="LocalBase"/>, <see cref="InstrPtr"/>) point into Csound's live data and are only
    /// valid during the current control cycle (i.e. inside the debug callback that obtained them).
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct DebugInstr
    {
        /// <summary>Head of the instance's variable pool (<c>CS_VAR_POOL*</c>); opaque to the app.</summary>
        public IntPtr VarPoolHead;

        /// <summary>Base address of the instance's local variable storage (<c>MYFLT*</c>); opaque to the app.</summary>
        public IntPtr LocalBase;

        /// <summary>The <c>INSDS*</c> of the running instance; used by Csound to identify it.</summary>
        public IntPtr InstrPtr;

        /// <summary>p-fields 1–3 of the instance: instrument number (fractional for tied/tagged notes), start time and duration in seconds (p3 &lt; 0 = held).</summary>
        public double P1, P2, P3;

        /// <summary>Number of control cycles the instance has run so far.</summary>
        public ulong KCounter;

        /// <summary>Orchestra line currently executing (meaningful when stopped at a breakpoint).</summary>
        public int Line;

        /// <summary>Next instance in the list, or <c>null</c> at the end.</summary>
        public DebugInstr* Next;
    }

    /// <summary>Mirror of <c>debug_variable_t</c>: name, Csound type name ("k", "a", "i", "S"…), data pointer, next.</summary>
    /// <remarks>
    /// Owned by the caller until passed to <see cref="csoundDebugFreeVariables"/>. <see cref="Data"/>
    /// points directly at Csound's storage for the variable: one <c>double</c> for i/k types,
    /// <c>ksmps</c> doubles for a-rate, a <c>STRINGDAT*</c> for strings. Read it only on the
    /// performance thread, during the same control cycle.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct DebugVariable
    {
        /// <summary>Variable name as written in the orchestra (UTF-8 <c>char*</c>), e.g. <c>"kcut"</c>.</summary>
        public IntPtr Name;

        /// <summary>Csound type name (UTF-8 <c>char*</c>): <c>"k"</c>, <c>"a"</c>, <c>"i"</c>, <c>"S"</c>, <c>"f"</c>…</summary>
        public IntPtr TypeName;

        /// <summary>Pointer to the variable's value storage (see remarks for the layout per type).</summary>
        public IntPtr Data;

        /// <summary>Next variable in the list, or <c>null</c> at the end.</summary>
        public DebugVariable* Next;
    }

    /// <summary>Enables the debugger (small performance cost). Call after create, before start.</summary>
    [LibraryImport(Lib)]
    public static partial int csoundDebuggerInit(IntPtr csound);

    /// <summary>Releases the debugger structures.</summary>
    [LibraryImport(Lib)]
    public static partial void csoundDebuggerClean(IntPtr csound);

    /// <summary>
    /// Registers a non-stopping callback that fires after every control cycle (inside
    /// <c>csoundPerformKsmps</c>, on the performance thread). It must return quickly.
    /// </summary>
    [LibraryImport(Lib)]
    public static partial void csoundSetDebugCallback(IntPtr csound, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> callback, IntPtr userData);

    /// <summary>Linked list of active instrument instances (free with <see cref="csoundDebugFreeInstrInstances"/>).</summary>
    [LibraryImport(Lib)]
    public static partial DebugInstr* csoundDebugGetInstrInstances(IntPtr csound);

    /// <summary>Frees a list from <see cref="csoundDebugGetInstrInstances"/>.</summary>
    [LibraryImport(Lib)]
    public static partial void csoundDebugFreeInstrInstances(IntPtr csound, DebugInstr* list);

    /// <summary>Variables of one instance (free with <see cref="csoundDebugFreeVariables"/>).</summary>
    [LibraryImport(Lib)]
    public static partial DebugVariable* csoundDebugGetVariables(IntPtr csound, DebugInstr* instr);

    /// <summary>Global variables (gk…, ga…, gi…), same format as <see cref="csoundDebugGetVariables"/>.</summary>
    [LibraryImport(Lib)]
    public static partial DebugVariable* csoundDebugGetGlobalVariables(IntPtr csound);

    /// <summary>Frees a variable list.</summary>
    [LibraryImport(Lib)]
    public static partial void csoundDebugFreeVariables(IntPtr csound, DebugVariable* list);

    /// <summary>Instrument number assigned to a named instrument, or -1.</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int csoundGetInstrNumber(IntPtr csound, string name);

    // ---------------------------------------------------------------------------------------
    //  Host MIDI I/O (csound_rtmidi.h)
    // ---------------------------------------------------------------------------------------

    /// <summary>Replaces Csound's MIDI module with the external callbacks below. Call before compiling.</summary>
    [LibraryImport(Lib)]
    public static partial void csoundSetHostMIDIIO(IntPtr csound);

    /// <summary>Callback Csound calls to open MIDI input (return 0 = success).</summary>
    /// <remarks>
    /// C signature: <c>int (*)(CSOUND*, void** userData, const char* devName)</c>. The callback may
    /// store a per-device handle in <c>*userData</c>; Csound passes it back to the read and close
    /// callbacks. Called on the thread that runs <see cref="csoundStart"/>.
    /// </remarks>
    [LibraryImport(Lib)]
    public static partial void csoundSetExternalMidiInOpenCallback(IntPtr csound, delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, IntPtr, int> callback);

    /// <summary>Callback Csound calls every control cycle to read pending MIDI bytes (returns the byte count).</summary>
    /// <remarks>
    /// C signature: <c>int (*)(CSOUND*, void* userData, unsigned char* buf, int nbytes)</c>. The
    /// callback copies at most <c>nbytes</c> bytes of raw MIDI 1.0 stream data (complete messages,
    /// status byte first) into <c>buf</c> and returns how many it wrote. It runs on the performance
    /// thread inside <see cref="csoundPerformKsmps"/>, so it must not block or allocate.
    /// </remarks>
    [LibraryImport(Lib)]
    public static partial void csoundSetExternalMidiReadCallback(IntPtr csound, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, int, int> callback);

    /// <summary>Callback Csound calls to close MIDI input.</summary>
    /// <remarks>C signature: <c>int (*)(CSOUND*, void* userData)</c>; called while the instance is being destroyed or reset.</remarks>
    [LibraryImport(Lib)]
    public static partial void csoundSetExternalMidiInCloseCallback(IntPtr csound, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> callback);

    /// <summary>Reads a NUL-terminated UTF-8 string from a fixed-size struct buffer.</summary>
    /// <param name="buffer">First byte of a <c>fixed byte[]</c> field such as <see cref="CsAudioDevice.DeviceName"/>.</param>
    /// <param name="capacity">Size of the buffer in bytes; the scan stops there even if no NUL was found.</param>
    /// <returns>The decoded string (empty if the first byte is NUL).</returns>
    public static string FixedString(byte* buffer, int capacity)
    {
        int len = 0;
        while (len < capacity && buffer[len] != 0) len++;
        return System.Text.Encoding.UTF8.GetString(buffer, len);
    }
}
