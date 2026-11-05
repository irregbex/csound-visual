using System.Runtime.InteropServices;

namespace CsoundVisual.Integration;

/// <summary>
/// Raw bindings to <c>CsoundVisualLink.dll</c>: Ableton Link 3.1.5 with its official C extension
/// (<c>abl_link.h</c>), built by <c>tools/build_link.ps1</c> from <c>src/AbletonLink</c>.
/// </summary>
/// <remarks>
/// <para>The DLL is optional. It is loaded by full path from the app folder with
/// <see cref="NativeLibrary.TryLoad(string, out IntPtr)"/> and every entry point is resolved into an
/// unmanaged function pointer, so a missing DLL never throws a <see cref="DllNotFoundException"/>
/// at a call site: <see cref="IsAvailable"/> is simply false and Link is disabled.</para>
/// <para>Calls through these pointers are blittable (handles are pointer-sized structs passed by
/// value, which the Windows x64 ABI passes exactly like a pointer; C <c>bool</c> is one byte) so
/// they allocate nothing and are safe to use from the audio thread for the functions Link marks
/// realtime-safe (<c>capture_audio_session_state</c>, <c>clock_micros</c>, the session-state
/// getters, <c>num_peers</c>).</para>
/// <para><b>Who uses it.</b> Only <see cref="LinkSession"/> (aliased there as <c>N</c>) calls the
/// function pointers; the rest of the app talks to <see cref="LinkSession"/> and only reads
/// <see cref="IsAvailable"/> / <see cref="LoadError"/> here (status display, <see cref="LinkSelfTest"/>).</para>
/// <para><b>Conventions of the C API.</b> Times are Link host-clock microseconds (<c>int64</c>,
/// QueryPerformanceCounter based, identical in every process of the machine); beats are
/// <c>double</c>; <c>quantum</c> is beats per bar (phase is reported in <c>[0, quantum)</c>).
/// Booleans are passed as one <c>byte</c> (0 = false). Handles (<c>abl_link</c>,
/// <c>abl_link_session_state</c>) are opaque pointers owned by the caller: every
/// <c>create*</c> must be paired with the matching <c>destroy*</c>.</para>
/// </remarks>
public static unsafe class AbletonLinkNative
{
    /// <summary>File name of the native library, next to <c>CsoundVisual.exe</c>.</summary>
    public const string DllName = "CsoundVisualLink.dll";

    /// <summary>Serialises the one-time load in <see cref="EnsureLoaded"/>.</summary>
    static readonly object gate = new();

    /// <summary>
    /// Set (with release semantics) once the load was tried, successfully or not; the fast path of
    /// <see cref="EnsureLoaded"/> reads it without taking the lock. The DLL is never retried.
    /// </summary>
    static bool attempted;

    /// <summary>Human-readable reason the DLL is unusable; <c>null</c> after a successful load.</summary>
    static string? loadError;

    /// <summary>True when the DLL was found and every entry point was resolved.</summary>
    public static bool IsAvailable
    {
        get
        {
            EnsureLoaded();
            return loadError is null;
        }
    }

    /// <summary>Why the DLL could not be used (null when <see cref="IsAvailable"/>).</summary>
    public static string? LoadError
    {
        get
        {
            EnsureLoaded();
            return loadError;
        }
    }

    // --- entry points (see abl_link.h for the full contract of each function) ---
    // Each field holds the address of one abl_link_* export, typed with its exact C signature
    // (delegate* unmanaged uses the platform default calling convention, i.e. the single x64 ABI).
    // They stay null until EnsureLoaded() succeeded; callers must check IsAvailable first.
    // Grouped by purpose:
    //   lifetime      create(bpm) → abl_link*, destroy
    //   network       enable / isEnabled, enableStartStopSync / isStartStopSyncEnabled, numPeers
    //   clock         clockMicros (host time, µs)
    //   session state createSessionState / destroySessionState; capture/commit in two flavours:
    //                 *Audio* (realtime-safe, only from the audio thread) and *App* (any other thread)
    //   timeline      tempo / setTempo(bpm, atMicros), beatAtTime / phaseAtTime(micros, quantum),
    //                 timeAtBeat(beat, quantum), requestBeatAtTime (quantised, polite) and
    //                 forceBeatAtTime (rewrites the shared beat grid: rude, unused by the app)
    //   transport     setIsPlaying(playing, atMicros), isPlaying, timeForIsPlaying,
    //                 requestBeatAtStartPlayingTime, setIsPlayingAndRequestBeatAtTime
    internal static delegate* unmanaged<double, IntPtr> create;
    internal static delegate* unmanaged<IntPtr, void> destroy;
    internal static delegate* unmanaged<IntPtr, byte> isEnabled;
    internal static delegate* unmanaged<IntPtr, byte, void> enable;
    internal static delegate* unmanaged<IntPtr, byte> isStartStopSyncEnabled;
    internal static delegate* unmanaged<IntPtr, byte, void> enableStartStopSync;
    internal static delegate* unmanaged<IntPtr, ulong> numPeers;
    internal static delegate* unmanaged<IntPtr, long> clockMicros;
    internal static delegate* unmanaged<IntPtr> createSessionState;
    internal static delegate* unmanaged<IntPtr, void> destroySessionState;
    internal static delegate* unmanaged<IntPtr, IntPtr, void> captureAudioSessionState;
    internal static delegate* unmanaged<IntPtr, IntPtr, void> commitAudioSessionState;
    internal static delegate* unmanaged<IntPtr, IntPtr, void> captureAppSessionState;
    internal static delegate* unmanaged<IntPtr, IntPtr, void> commitAppSessionState;
    internal static delegate* unmanaged<IntPtr, double> tempo;
    internal static delegate* unmanaged<IntPtr, double, long, void> setTempo;
    internal static delegate* unmanaged<IntPtr, long, double, double> beatAtTime;
    internal static delegate* unmanaged<IntPtr, long, double, double> phaseAtTime;
    internal static delegate* unmanaged<IntPtr, double, double, long> timeAtBeat;
    internal static delegate* unmanaged<IntPtr, double, long, double, void> requestBeatAtTime;
    internal static delegate* unmanaged<IntPtr, double, ulong, double, void> forceBeatAtTime;
    internal static delegate* unmanaged<IntPtr, byte, ulong, void> setIsPlaying;
    internal static delegate* unmanaged<IntPtr, byte> isPlaying;
    internal static delegate* unmanaged<IntPtr, ulong> timeForIsPlaying;
    internal static delegate* unmanaged<IntPtr, double, double, void> requestBeatAtStartPlayingTime;
    internal static delegate* unmanaged<IntPtr, byte, ulong, double, double, void> setIsPlayingAndRequestBeatAtTime;

    /// <summary>Loads the DLL once and resolves the entry points (thread-safe, never throws).</summary>
    static void EnsureLoaded()
    {
        if (Volatile.Read(ref attempted)) return;
        lock (gate)
        {
            if (attempted) return;
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, DllName);
                if (!File.Exists(path))
                    loadError = "not found: " + path;
                else if (!NativeLibrary.TryLoad(path, out IntPtr lib))
                    loadError = "could not load " + path;
                else
                    Resolve(lib);
            }
            catch (Exception ex)
            {
                loadError = ex.Message;
            }
            Volatile.Write(ref attempted, true);
        }
    }

    /// <summary>
    /// Fills every entry-point field from the loaded module. The library handle is never freed:
    /// the pointers must stay valid for the whole process lifetime.
    /// </summary>
    /// <param name="lib">Module handle returned by <see cref="NativeLibrary.TryLoad(string, out IntPtr)"/>.</param>
    /// <exception cref="EntryPointNotFoundException">An export is missing (an old or foreign DLL);
    /// caught by <see cref="EnsureLoaded"/> and reported through <see cref="LoadError"/>.</exception>
    static void Resolve(IntPtr lib)
    {
        IntPtr F(string name) => NativeLibrary.GetExport(lib, name);
        create = (delegate* unmanaged<double, IntPtr>)F("abl_link_create");
        destroy = (delegate* unmanaged<IntPtr, void>)F("abl_link_destroy");
        isEnabled = (delegate* unmanaged<IntPtr, byte>)F("abl_link_is_enabled");
        enable = (delegate* unmanaged<IntPtr, byte, void>)F("abl_link_enable");
        isStartStopSyncEnabled = (delegate* unmanaged<IntPtr, byte>)F("abl_link_is_start_stop_sync_enabled");
        enableStartStopSync = (delegate* unmanaged<IntPtr, byte, void>)F("abl_link_enable_start_stop_sync");
        numPeers = (delegate* unmanaged<IntPtr, ulong>)F("abl_link_num_peers");
        clockMicros = (delegate* unmanaged<IntPtr, long>)F("abl_link_clock_micros");
        createSessionState = (delegate* unmanaged<IntPtr>)F("abl_link_create_session_state");
        destroySessionState = (delegate* unmanaged<IntPtr, void>)F("abl_link_destroy_session_state");
        captureAudioSessionState = (delegate* unmanaged<IntPtr, IntPtr, void>)F("abl_link_capture_audio_session_state");
        commitAudioSessionState = (delegate* unmanaged<IntPtr, IntPtr, void>)F("abl_link_commit_audio_session_state");
        captureAppSessionState = (delegate* unmanaged<IntPtr, IntPtr, void>)F("abl_link_capture_app_session_state");
        commitAppSessionState = (delegate* unmanaged<IntPtr, IntPtr, void>)F("abl_link_commit_app_session_state");
        tempo = (delegate* unmanaged<IntPtr, double>)F("abl_link_tempo");
        setTempo = (delegate* unmanaged<IntPtr, double, long, void>)F("abl_link_set_tempo");
        beatAtTime = (delegate* unmanaged<IntPtr, long, double, double>)F("abl_link_beat_at_time");
        phaseAtTime = (delegate* unmanaged<IntPtr, long, double, double>)F("abl_link_phase_at_time");
        timeAtBeat = (delegate* unmanaged<IntPtr, double, double, long>)F("abl_link_time_at_beat");
        requestBeatAtTime = (delegate* unmanaged<IntPtr, double, long, double, void>)F("abl_link_request_beat_at_time");
        forceBeatAtTime = (delegate* unmanaged<IntPtr, double, ulong, double, void>)F("abl_link_force_beat_at_time");
        setIsPlaying = (delegate* unmanaged<IntPtr, byte, ulong, void>)F("abl_link_set_is_playing");
        isPlaying = (delegate* unmanaged<IntPtr, byte>)F("abl_link_is_playing");
        timeForIsPlaying = (delegate* unmanaged<IntPtr, ulong>)F("abl_link_time_for_is_playing");
        requestBeatAtStartPlayingTime = (delegate* unmanaged<IntPtr, double, double, void>)F("abl_link_request_beat_at_start_playing_time");
        setIsPlayingAndRequestBeatAtTime = (delegate* unmanaged<IntPtr, byte, ulong, double, double, void>)F("abl_link_set_is_playing_and_request_beat_at_time");
    }
}
