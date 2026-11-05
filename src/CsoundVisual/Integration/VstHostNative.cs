using System.Runtime.InteropServices;
using System.Text;

namespace CsoundVisual.Integration;

/// <summary>
/// P/Invoke declarations for <c>CsoundVisualVstHost.dll</c>, the native VST3 hosting bridge built
/// from <c>src/VstHost</c> (see <c>vsthost.h</c> there for the full contract). The DLL is optional:
/// <see cref="IsAvailable"/> tells whether it could be loaded, and nothing else in this class may
/// be called when it is false.
/// </summary>
/// <remarks>
/// Threading: <see cref="vh_process_stereo"/> runs on the audio thread; <see cref="vh_midi"/> may be
/// called from any thread (a short spin-locked inbox that the audio thread only try-locks);
/// <see cref="vh_param_set"/> also queues through such an inbox but additionally updates the edit
/// controller, which VST3 expects on the UI thread, so the app calls it from the UI thread only;
/// everything else belongs to the UI thread (the thread that pumps the plug-in editor windows).
/// <see cref="vh_free"/>, <see cref="vh_prepare"/> and <see cref="vh_unprepare"/> must never overlap
/// a <see cref="vh_process_stereo"/> call on the same instance (<see cref="VstChain"/> guarantees it).
/// <para><b>Marshalling and ownership.</b> Plug-in handles (<c>IntPtr h</c>) are opaque native
/// objects created by <see cref="vh_load"/> and owned by the caller until <see cref="vh_free"/>
/// (<see cref="VstChain"/> owns them in the app). Text crosses the boundary in two encodings, as
/// the native side expects: file paths and window titles are UTF-16 (<c>wchar_t*</c>, Windows
/// wide APIs), class ids and every JSON/error buffer are UTF-8 bytes, and parameter titles/values
/// are VST3 <c>String128</c> UTF-16 buffers (128 chars + terminator; the managed side passes 130).
/// Output buffers are always caller-allocated and pinned with <c>fixed</c> only for the call; the
/// native side never keeps a pointer to them.</para>
/// <para><b>Return conventions.</b> Most integer results are 0 on success and a negative code on
/// failure (codes listed in <c>vsthost.h</c>). The "size-returning" functions
/// (<see cref="vh_scan_module"/>, <see cref="vh_info"/>, <see cref="vh_get_state"/>) return the full
/// length needed (negative = error), see <see cref="CallJson"/>. Every function tolerates a null
/// handle.</para>
/// </remarks>
public static unsafe partial class VstHostNative
{
    /// <summary>File name of the bridge DLL (next to CsoundVisual.exe).</summary>
    public const string DllName = "CsoundVisualVstHost";

    /// <summary>Cached result of the load attempt: 0 = unknown, 1 = loaded, -1 = missing.</summary>
    static int availability; // 0 = unknown, 1 = loaded, -1 = missing

    /// <summary>Reason the DLL is unusable (set together with <c>availability = -1</c>).</summary>
    static string? loadError;

    /// <summary>True when the bridge DLL could be loaded (the VST3 host features are usable).</summary>
    /// <remarks>
    /// The first read loads the DLL from the exe folder by full path; the <c>LibraryImport</c>
    /// stubs below then bind to that already-loaded module. <c>vh_version() ≥ 1</c> also rejects an
    /// incompatible build (a missing export throws and is caught). The check is not synchronised:
    /// two threads racing on the first read would just both try the (idempotent) load. In practice
    /// it is first read on the UI thread.
    /// </remarks>
    public static bool IsAvailable
    {
        get
        {
            if (availability == 0)
            {
                string path = Path.Combine(AppContext.BaseDirectory, DllName + ".dll");
                try
                {
                    if (NativeLibrary.TryLoad(path, out _) && vh_version() >= 1)
                        availability = 1;
                    else
                    {
                        availability = -1;
                        loadError = File.Exists(path) ? "cannot load " + path : path + " not found";
                    }
                }
                catch (Exception ex)
                {
                    availability = -1;
                    loadError = ex.Message;
                }
            }
            return availability > 0;
        }
    }

    /// <summary>Why the bridge is unavailable (after <see cref="IsAvailable"/> returned false).</summary>
    public static string? LoadError => loadError;

    /// <summary>ABI version of the bridge (≥ 1 for this app; 2 adds <see cref="vh_take_restart_flags"/>).</summary>
    [LibraryImport(DllName)] internal static partial int vh_version();

    /// <summary>
    /// Bridge v2+: returns the VST3 <c>restartComponent</c> flags the plug-in raised since the last
    /// call and clears them (2 = I/O changed, 4 = parameter values changed, 8 = latency changed,
    /// 16 = parameter titles changed); also applies pending processor→controller parameter
    /// changes. UI thread only. Do not call when <see cref="SupportsRestartFlags"/> is false (the
    /// export does not exist in a v1 DLL and the call would throw <see cref="EntryPointNotFoundException"/>).
    /// </summary>
    [LibraryImport(DllName)] internal static partial int vh_take_restart_flags(IntPtr h);

    /// <summary>
    /// True when the loaded bridge exports <see cref="vh_take_restart_flags"/> (ABI version ≥ 2).
    /// Cached after the first query; false whenever the bridge is unavailable.
    /// </summary>
    public static bool SupportsRestartFlags => supportsRestartFlags ??= IsAvailable && vh_version() >= 2;

    /// <summary>Cache of <see cref="SupportsRestartFlags"/> (<c>null</c> = not asked yet).</summary>
    static bool? supportsRestartFlags;
    /// <summary>Opens a <c>.vst3</c> module and writes its class list as UTF-8 JSON into <paramref name="buf"/>; returns the JSON length (retry when ≥ <paramref name="cap"/>), or −1 − (error length) with the error text in the buffer. Runs the module's DLL entry code: the app calls it only in the scan worker process.</summary>
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16)] internal static partial int vh_scan_module(string path, byte* buf, int cap);
    /// <summary>Creates the processor + controller of class <paramref name="cid"/> (32 hex chars) from module <paramref name="path"/>; returns the handle, or zero with a UTF-8 reason in <paramref name="err"/>.</summary>
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16)] internal static partial IntPtr vh_load(string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string cid, byte* err, int errCap);
    /// <summary>Closes the editor, terminates the plug-in and releases the handle (UI thread; never while the audio thread may be inside <see cref="vh_process_stereo"/>).</summary>
    [LibraryImport(DllName)] internal static partial void vh_free(IntPtr h);
    /// <summary>Sets up processing (sample rate in Hz, maximum block in frames, raised to ≥ 32; larger host blocks are split) and activates the plug-in; 0 on success, negative code on failure (−4 = crashed while activating).</summary>
    [LibraryImport(DllName)] internal static partial int vh_prepare(IntPtr h, double sampleRate, int maxBlock);
    /// <summary>Deactivates processing (before a re-prepare at another rate, or before freeing).</summary>
    [LibraryImport(DllName)] internal static partial void vh_unprepare(IntPtr h);
    /// <summary>Writes the instance's JSON description; size-returning like <see cref="vh_scan_module"/>.</summary>
    [LibraryImport(DllName)] internal static partial int vh_info(IntPtr h, byte* buf, int cap);
    /// <summary>Audio thread, real-time safe: processes <paramref name="frames"/> frames in place on two non-interleaved float channels (effects replace, instruments add their output). Returns 0 = processed, 1 = not prepared / bad arguments (audio untouched), 2 = the plug-in crashed (caught by SEH) and is disabled for good.</summary>
    [LibraryImport(DllName)] internal static partial int vh_process_stereo(IntPtr h, float* left, float* right, int frames);
    /// <summary>Any thread: queues one MIDI 1.0 channel message (status byte with the channel in its low nibble, two 7-bit data bytes) for the next block. Notes become VST3 note events; CC, pitch bend and channel pressure go through the plug-in's IMidiMapping (dropped without one).</summary>
    [LibraryImport(DllName)] internal static partial void vh_midi(IntPtr h, byte status, byte data1, byte data2);
    /// <summary>Number of parameters exposed by the controller.</summary>
    [LibraryImport(DllName)] internal static partial int vh_param_count(IntPtr h);
    /// <summary>Describes parameter <paramref name="index"/>: id, <c>String128</c> title, VST3 flags, step count (0 = continuous) and normalised default; 0 on success.</summary>
    [LibraryImport(DllName)] internal static partial int vh_param_info(IntPtr h, int index, uint* id, char* title, int titleCap, int* flags, int* stepCount, double* defaultValue);
    /// <summary>Current normalised value (0…1) of parameter <paramref name="id"/>, from the controller.</summary>
    [LibraryImport(DllName)] internal static partial double vh_param_get(IntPtr h, uint id);
    /// <summary>UI thread: sets a normalised value (clamped to 0…1) on the controller (the editor shows it) and queues it for the processor's next block.</summary>
    [LibraryImport(DllName)] internal static partial void vh_param_set(IntPtr h, uint id, double value);
    /// <summary>Formats a normalised value as the plug-in displays it (UTF-16 into <paramref name="buf"/>); 0 on success.</summary>
    [LibraryImport(DllName)] internal static partial int vh_param_text(IntPtr h, uint id, double value, char* buf, int cap);
    /// <summary>Serialises the full state into an opaque blob (<c>"CVS1"</c>, u32 size + component bytes, u32 size + controller bytes); returns the total size and writes nothing when the buffer is null or too small; negative on error.</summary>
    [LibraryImport(DllName)] internal static partial int vh_get_state(IntPtr h, byte* buf, int cap);
    /// <summary>Restores a blob written by <see cref="vh_get_state"/>; 0 on success.</summary>
    [LibraryImport(DllName)] internal static partial int vh_set_state(IntPtr h, byte* buf, int len);
    /// <summary>1 when the plug-in offers an HWND-embeddable editor view, else 0 (creates and releases a view to find out).</summary>
    [LibraryImport(DllName)] internal static partial int vh_has_editor(IntPtr h);
    /// <summary>Opens (or brings to front) the editor in a top-level Win32 window owned by <paramref name="owner"/>; returns its HWND or zero.</summary>
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16)] internal static partial IntPtr vh_editor_open(IntPtr h, IntPtr owner, string title);
    /// <summary>Closes the editor window if it is open.</summary>
    [LibraryImport(DllName)] internal static partial void vh_editor_close(IntPtr h);
    /// <summary>HWND of the open editor window, or zero.</summary>
    [LibraryImport(DllName)] internal static partial IntPtr vh_editor_hwnd(IntPtr h);
    /// <summary>Pumps Win32 messages for about <paramref name="ms"/> milliseconds (console tests that have no UI message loop).</summary>
    [LibraryImport(DllName)] internal static partial void vh_pump(int ms);
    /// <summary>Test helper: captures a window with PrintWindow into a 24-bit <c>.bmp</c> file; returns the number of non-black pixels (a rough "something was drawn" check) or a negative error.</summary>
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16)] internal static partial int vh_capture_window(IntPtr hwnd, string bmpPath);

    /// <summary>
    /// Calls a "size-returning" JSON function twice if needed (first with a 64 KB buffer) and
    /// returns the UTF-8 text. A negative result means failure: the text is then the error.
    /// </summary>
    /// <param name="call">Invokes the native function with (pinned buffer, capacity in bytes) and returns its result.</param>
    /// <returns><c>Ok</c> and the JSON text, or <c>!Ok</c> and the error text (the native side writes
    /// it null-terminated and returns −1 − length).</returns>
    /// <remarks>UI/worker thread only: allocates managed buffers. The result is ≥ cap when the text
    /// did not fit, so the second attempt uses exactly the reported size + terminator.</remarks>
    internal static (bool Ok, string Text) CallJson(Func<IntPtr, int, int> call)
    {
        int cap = 64 * 1024;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var buf = new byte[cap];
            int n;
            fixed (byte* p = buf)
                n = call((IntPtr)p, cap);
            if (n < 0)
            {
                int len = Array.IndexOf(buf, (byte)0);
                return (false, Encoding.UTF8.GetString(buf, 0, len < 0 ? 0 : len));
            }
            if (n < cap)
                return (true, Encoding.UTF8.GetString(buf, 0, n));
            cap = n + 1;
        }
        return (false, "buffer too small");
    }

    /// <summary>Scans one module: UTF-8 JSON <c>{vendor, url, classes:[{name, vendor, category, subCategories, cid, version, sdkVersion}]}</c>.</summary>
    public static (bool Ok, string Json) ScanModule(string path) =>
        CallJson((p, cap) => vh_scan_module(path, (byte*)p, cap));

    /// <summary>Loads an instance, or returns <see cref="IntPtr.Zero"/> with the reason.</summary>
    /// <param name="path">Module path (<c>.vst3</c> bundle folder or file).</param>
    /// <param name="cid">Class id, 32 hex digits, as reported by the scan.</param>
    /// <param name="error">UTF-8 reason decoded from a 1 KB buffer, or <c>null</c> on success.</param>
    /// <returns>A handle owned by the caller (free with <see cref="vh_free"/>); it still needs <see cref="vh_prepare"/> before processing.</returns>
    public static IntPtr Load(string path, string cid, out string? error)
    {
        var err = new byte[1024];
        IntPtr h;
        fixed (byte* e = err)
            h = vh_load(path, cid, e, err.Length);
        int len = Array.IndexOf(err, (byte)0);
        error = h == IntPtr.Zero ? Encoding.UTF8.GetString(err, 0, len < 0 ? 0 : len) : null;
        return h;
    }

    /// <summary>The instance's JSON description (name, instrument, channels, editor…).</summary>
    public static string Info(IntPtr h) => CallJson((p, cap) => vh_info(h, (byte*)p, cap)).Text;

    /// <summary>Serialized state (processor + controller), or <c>null</c>.</summary>
    public static byte[]? GetState(IntPtr h)
    {
        // Two-call pattern: query the size, then fill an exactly-sized buffer. If the state changed
        // size in between (user tweaking the editor), the mismatch makes the result null.
        int n = vh_get_state(h, null, 0);
        if (n <= 0) return null;
        var buf = new byte[n];
        fixed (byte* p = buf)
            n = vh_get_state(h, p, buf.Length);
        return n == buf.Length ? buf : null;
    }

    /// <summary>Restores a state produced by <see cref="GetState"/>. True on success.</summary>
    public static bool SetState(IntPtr h, byte[] state)
    {
        fixed (byte* p = state)
            return vh_set_state(h, p, state.Length) == 0;
    }

    /// <summary>One parameter's description.</summary>
    /// <param name="Index">Position in the controller's list (0-based).</param>
    /// <param name="Id">Stable VST3 ParamID used by every other parameter call.</param>
    /// <param name="Title">Display title.</param>
    /// <param name="Flags">Raw <c>Steinberg::Vst::ParameterInfo::flags</c> bit mask.</param>
    /// <param name="StepCount">0 = continuous; n = n + 1 discrete values.</param>
    /// <param name="Default">Default normalised value (0…1).</param>
    public readonly record struct ParamInfo(int Index, uint Id, string Title, int Flags, int StepCount, double Default)
    {
        /// <summary>ParameterInfo::kCanAutomate.</summary>
        public bool CanAutomate => (Flags & 1) != 0;
        /// <summary>ParameterInfo::kIsReadOnly.</summary>
        public bool IsReadOnly => (Flags & 2) != 0;
        /// <summary>ParameterInfo::kIsHidden.</summary>
        public bool IsHidden => (Flags & 16) != 0;
        /// <summary>ParameterInfo::kIsBypass.</summary>
        public bool IsBypass => (Flags & 0x10000) != 0;
    }

    /// <summary>Lists the plug-in's parameters.</summary>
    public static List<ParamInfo> Parameters(IntPtr h)
    {
        var list = new List<ParamInfo>();
        int n = vh_param_count(h);
        // VST3 titles are String128 (128 UTF-16 units); one buffer is reused for every parameter.
        var title = new char[130];
        for (int i = 0; i < n; i++)
        {
            uint id; int flags, steps; double def;
            fixed (char* t = title)
                if (vh_param_info(h, i, &id, t, title.Length, &flags, &steps, &def) != 0)
                    continue;
            int len = Array.IndexOf(title, '\0');
            list.Add(new ParamInfo(i, id, new string(title, 0, len < 0 ? title.Length : len), flags, steps, def));
        }
        return list;
    }

    /// <summary>A parameter value as the plug-in displays it, or <c>null</c>.</summary>
    public static string? ParamText(IntPtr h, uint id, double value)
    {
        var buf = new char[130];
        fixed (char* p = buf)
            if (vh_param_text(h, id, value, p, buf.Length) != 0)
                return null;
        int len = Array.IndexOf(buf, '\0');
        return new string(buf, 0, len < 0 ? buf.Length : len);
    }
}
