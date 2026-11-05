// CsoundEngine.Invoke.cs — generic «run this on the Csound thread» requests.
//
// Added for Paint Rooms (live image synthesis), usable by any tool: some jobs must touch the
// running Csound instance directly and atomically between two control cycles — compile code that
// creates tables, copy hundreds of thousands of numbers straight into those tables' memory,
// set channels and start a note, all in the same gap so the note never sees half-filled tables.
// The existing request queues (SetControl, SendEvent, CompileOrc) are separate and unordered with
// respect to each other, so they cannot express that.

using System.Collections.Concurrent;
using CsoundVisual.Interop;

namespace CsoundVisual.Engine;

public sealed unsafe partial class CsoundEngine
{
    /// <summary>Actions queued by <see cref="InvokeOnCsoundThread"/>, drained by <see cref="RunInvokes"/>.</summary>
    readonly ConcurrentQueue<Action<IntPtr>> pendingInvokes = new();

    /// <summary>
    /// Queues <paramref name="action"/> to run on the performance thread right before the next
    /// control cycle, with the native <c>CSOUND*</c> as argument (valid only during the call).
    /// Actions run in the order queued, after channel values and before score events of the same
    /// cycle. They run inside the audio loop: keep them short (a millisecond or two — split bigger
    /// jobs into steps; an action queued from inside an action runs at the NEXT control cycle, so a
    /// job can re-queue itself to continue). Exceptions are caught and reported on the console.
    /// When no performance is active the action waits for the next one, so callers that care
    /// (Paint Rooms) only queue while the engine is running and verify the tables they expect.
    /// </summary>
    public void InvokeOnCsoundThread(Action<IntPtr> action) => pendingInvokes.Enqueue(action);

    /// <summary>Runs the queued actions (performance thread, from <c>Step</c>).</summary>
    void RunInvokes(IntPtr cs)
    {
        // Only the actions queued BEFORE this cycle: one that queues another (a multi-step job
        // spreading its work over several cycles) gets it run at the next cycle, not this one.
        for (int n = pendingInvokes.Count; n > 0 && pendingInvokes.TryDequeue(out var action); n--)
        {
            try
            {
                action(cs);
            }
            catch (Exception ex)
            {
                Post("⚠ " + ex.Message, MessageKind.Warning);
            }
        }
    }

    /// <summary>
    /// Copies <paramref name="data"/> into function table <paramref name="table"/> starting at
    /// index <paramref name="offset"/> (performance thread only, e.g. inside an
    /// <see cref="InvokeOnCsoundThread"/> action). Copies only what fits; returns the number of
    /// values written, or −1 when the table does not exist.
    /// </summary>
    public static int CopyToTable(IntPtr cs, int table, ReadOnlySpan<double> data, int offset = 0)
    {
        double* ptr;
        int len = CsoundNative.csoundGetTable(cs, &ptr, table);
        if (len <= 0 || ptr is null) return -1;
        if (offset < 0 || offset >= len) return 0;
        int n = Math.Min(data.Length, len - offset);
        data[..n].CopyTo(new Span<double>(ptr + offset, n));
        return n;
    }

    /// <summary>Length of function table <paramref name="table"/>, or a value ≤ 0 when it does not exist (performance thread only).</summary>
    public static int TableLength(IntPtr cs, int table) => CsoundNative.csoundTableLength(cs, table);

    /// <summary>
    /// Compiles orchestra code synchronously (performance thread only): global statements such as
    /// <c>ftgen</c> run immediately, so their tables exist when this returns. Returns Csound's
    /// result code (0 = success).
    /// </summary>
    public static int CompileNow(IntPtr cs, string orc) => CsoundNative.csoundCompileOrc(cs, orc, 0);

    /// <summary>Sends a score line synchronously (performance thread only).</summary>
    public static void EventNow(IntPtr cs, string scoreLine) => CsoundNative.csoundEventString(cs, scoreLine, 0);

    /// <summary>Sets a control channel synchronously (performance thread only).</summary>
    public static void ControlNow(IntPtr cs, string channel, double value) => CsoundNative.csoundSetControlChannel(cs, channel, value);

    /// <summary>Current score time in seconds (performance thread only): the start of the next control cycle.</summary>
    public static double ScoreTimeNow(IntPtr cs) => CsoundNative.csoundGetScoreTime(cs);
}
