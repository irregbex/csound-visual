using System.Runtime.InteropServices;

namespace CsoundVisual.Interop;

/// <summary>
/// Breakpoint part of the Csound 7 debugger API (<c>csdebug.h</c>). The live-value imports
/// (<c>csoundDebuggerInit</c>, <c>csoundSetDebugCallback</c>, instance and variable lists) are in
/// <c>CsoundNative.cs</c>; both features share one debugger per <c>CSOUND*</c>.
/// </summary>
/// <remarks>
/// Behaviour verified against the Csound 7.0 binary in <c>@Source\Csound7</c> (see
/// <c>Engine/BreakpointSession.cs</c> for how the app uses it):
/// <list type="bullet">
/// <item>Line numbers are the line numbers of the <b>.csd</b> text given to <c>csoundCompileCSD</c>.
/// Code compiled later with <c>csoundCompileOrc</c> is numbered from 1 <i>plus</i> the line of the
/// CSD's <c>&lt;CsInstruments&gt;</c> tag.</item>
/// <item>A line breakpoint stops <i>before</i> the first perf-time opcode tagged with that line,
/// once per control cycle and per instance. Init-only lines (<c>ifreq = p4</c>) and UDO bodies
/// never stop. Expression temporaries are sometimes tagged with the next line.</item>
/// <item>The <c>instr</c> argument of <see cref="csoundSetBreakpoint"/> filters by instrument number
/// (0 = any instrument).</item>
/// <item>While stopped, <c>csoundPerformKsmps</c> returns 0 immediately without advancing time and
/// leaves <c>spout</c> untouched (stale). After <see cref="csoundDebugContinue"/>, the next call
/// completes the interrupted cycle and its <c>spout</c> is valid.</item>
/// <item><c>csoundDebugNext</c> does not advance in this build (it stops again at the same opcode
/// forever), so it is not used.</item>
/// </list>
/// </remarks>
internal static unsafe partial class CsoundNative
{
    /// <summary>Mirror of <c>debug_opcode_t</c>: opcode name (16 bytes), source line, next, prev.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DebugOpcode
    {
        /// <summary>Opcode name, NUL-terminated UTF-8 in a fixed 16-byte buffer (read with <see cref="FixedString"/>).</summary>
        public fixed byte OpName[16];
        /// <summary>Source line of the opcode in the compiled text (see the line-numbering rules above).</summary>
        public int Line;
        /// <summary>Next opcode in the instrument chain (<c>debug_opcode_t*</c>); not walked by the app.</summary>
        public IntPtr Next;
        /// <summary>Previous opcode in the chain (<c>debug_opcode_t*</c>); not walked by the app.</summary>
        public IntPtr Prev;
    }

    /// <summary>Mirror of <c>debug_bkpt_info_t</c>, passed to the breakpoint callback.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BreakpointInfo
    {
        /// <summary>The instance that stopped.</summary>
        public DebugInstr* BreakpointInstr;
        /// <summary>Its variables (owned by Csound, valid during the callback).</summary>
        public DebugVariable* InstrVarList;
        /// <summary>Every active instance.</summary>
        public DebugInstr* InstrListHead;
        /// <summary>The opcode about to run (line breakpoints).</summary>
        public DebugOpcode* CurrentOpcode;
    }

    /// <summary>Mirror of <c>debug_array_info_t</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DebugArrayInfo
    {
        /// <summary>Number of dimensions of the array.</summary>
        public int Dimensions;
        /// <summary>Size of one element in bytes.</summary>
        public int ArrayMemberSize;
        /// <summary>Total element count over all dimensions.</summary>
        public int TotalElements;
        /// <summary>Csound type name of the elements (<c>"k"</c>, <c>"i"</c>, <c>"S"</c>…), NUL-terminated in 16 bytes.</summary>
        public fixed byte ElementTypeName[16];
    }

    /// <summary>Mirror of <c>debug_fsig_info_t</c>: the analysis parameters of an f-signal (<c>PVSDAT</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DebugFsigInfo
    {
        /// <summary>
        /// <c>N</c> = FFT size; <c>NB</c> = number of bins (N/2 + 1); <c>Overlap</c> = hop size in
        /// samples; <c>WinSize</c> = analysis window length; <c>WinType</c> = Csound window id;
        /// <c>Format</c> = frame format (0 = amp/freq pairs).
        /// </summary>
        public int N, NB, Overlap, WinSize, WinType, Format;
        /// <summary>Frame counter of the f-signal (advances once per hop).</summary>
        public uint FrameCount;
        /// <summary>Non-zero for sliding (per-sample) analysis.</summary>
        public int Sliding;
    }

    /// <summary>Sets a line breakpoint (<paramref name="instr"/> 0 = any instrument). Queued, applied in the next cycle.</summary>
    /// <param name="csound">Instance with the debugger initialised.</param>
    /// <param name="line">Line in the compiled CSD text (1-based).</param>
    /// <param name="instr">Instrument number filter; 0 = any instrument.</param>
    /// <param name="skip">Number of control cycles to let pass before stopping again (0 = stop every cycle).</param>
    [LibraryImport(Lib)]
    public static partial void csoundSetBreakpoint(IntPtr csound, int line, int instr, int skip);

    /// <summary>Removes a line breakpoint.</summary>
    [LibraryImport(Lib)]
    public static partial void csoundRemoveBreakpoint(IntPtr csound, int line, int instr);

    /// <summary>Removes every breakpoint.</summary>
    [LibraryImport(Lib)]
    public static partial void csoundClearBreakpoints(IntPtr csound);

    /// <summary>Installs the callback Csound calls (on the performance thread) when a breakpoint stops it.</summary>
    [LibraryImport(Lib)]
    public static partial void csoundSetBreakpointCallback(IntPtr csound,
        delegate* unmanaged[Cdecl]<IntPtr, BreakpointInfo*, IntPtr, void> callback, IntPtr userData);

    /// <summary>Resumes a performance stopped at a breakpoint.</summary>
    [LibraryImport(Lib)]
    public static partial void csoundDebugContinue(IntPtr csound);

    /// <summary>Copies a numeric array's elements; returns the total element count (0 for non-numeric arrays).</summary>
    /// <param name="csound">Instance (stopped at a breakpoint or inside the debug callback).</param>
    /// <param name="varData"><see cref="DebugVariable.Data"/> of an array variable.</param>
    /// <param name="outBuf">Caller-owned buffer receiving up to <paramref name="bufMax"/> doubles.</param>
    /// <param name="bufMax">Capacity of <paramref name="outBuf"/> in elements.</param>
    /// <param name="info">Receives the array shape.</param>
    [LibraryImport(Lib)]
    public static partial int csoundDebugSerializeArray(IntPtr csound, IntPtr varData, double* outBuf, int bufMax, DebugArrayInfo* info);

    /// <summary>Copies an f-signal's current frame (amp, freq pairs); returns 2·NB, or 0 before the first frame.</summary>
    /// <param name="csound">Instance (stopped at a breakpoint or inside the debug callback).</param>
    /// <param name="varData"><see cref="DebugVariable.Data"/> of an f-signal variable.</param>
    /// <param name="outBuf">Caller-owned buffer of <c>float</c> (PVS frames are single precision even in double builds).</param>
    /// <param name="bufMax">Capacity of <paramref name="outBuf"/> in floats.</param>
    /// <param name="info">Receives the analysis parameters.</param>
    /// <param name="localKsmps">ksmps of the owning instrument (used for sliding analysis).</param>
    [LibraryImport(Lib)]
    public static partial int csoundDebugSerializeFsig(IntPtr csound, IntPtr varData, float* outBuf, int bufMax, DebugFsigInfo* info, int localKsmps);
}
