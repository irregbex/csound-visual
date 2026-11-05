//------------------------------------------------------------------------------
// CsoundVisualVstHost - a tiny VST3 hosting bridge for Csound Visual (C# / P/Invoke)
//
// WHAT IT IS
//   The public interface of CsoundVisualVstHost.dll, a small native DLL that lets the
//   managed app (C#/Avalonia) load VST3 plug-ins, run audio through them, drive their
//   parameters, save/restore their state and open their editor windows. It wraps the
//   Steinberg VST3 SDK hosting helpers (public.sdk/source/vst/hosting) behind a flat C API.
//
// WHO CALLS IT
//   src/CsoundVisual/Integration/VstHostNative.cs declares every function below as a
//   P/Invoke; VstChain.cs (the insert chain run on Csound's output) and VstHostCli.cs
//   (command-line tests) are the only users. test/vsthost_native_test.cpp calls it
//   directly from C++ to test plug-ins without .NET.
//
// WHY A C API
//   C++ classes, std::string, exceptions and COM-like VST3 interfaces cannot cross a
//   P/Invoke boundary safely. A flat extern "C" API with an opaque handle, plain integers,
//   doubles and caller-owned buffers is trivially marshalled by .NET and keeps the SDK
//   (and its C++ runtime) entirely inside the DLL. The DLL links the CRT statically, so it
//   depends only on Windows system libraries.
//
// CONVENTIONS
//   * Strings are UTF-16 (wchar_t) for paths, titles and parameter texts, UTF-8 for JSON
//     and error messages.
//   * Output buffers are always owned by the caller: the DLL copies into `buf` (at most
//     cap - 1 bytes/characters plus a null terminator) and never keeps the pointer.
//   * Functions that produce variable-length text return the FULL length needed, so the
//     caller can retry with a bigger buffer when the returned value >= cap.
//   * Parameter values are VST3 "normalised" values: doubles in 0..1 (the plug-in maps them
//     to dB, Hz, steps... itself; vh_param_text shows that mapping).
//   * Every function is safe to call with a null handle (it returns an error / does nothing).
//
// THREADING CONTRACT (same as a DAW)
//   * vh_process_stereo  -> the audio thread only (never blocks, never allocates in the
//                           steady state; drains the inboxes below with a try-lock).
//   * vh_midi / vh_param_set -> may be called from a non-UI thread for their queueing part
//                           (a short spin lock that the audio thread only try-locks).
//                           Note: vh_param_set also calls IEditController::setParamNormalized,
//                           which the VST3 spec expects on the UI thread; the app only calls
//                           it from the UI thread.
//   * everything else    -> the UI thread (the thread that owns the editor windows and pumps
//                           their messages; in the app that is Avalonia's UI thread).
//   * vh_free / vh_prepare / vh_unprepare must never overlap a vh_process_stereo call on
//     the same instance; VstChain guarantees this by unpublishing the instance from the
//     audio thread before touching it.
//------------------------------------------------------------------------------
#pragma once

#include <stdint.h>

// VH_API: every exported function is extern "C" (undecorated name, so .NET finds it by its
// plain name) and __declspec(dllexport) when building the DLL itself (vsthost.cpp defines
// CSV_VSTHOST_BUILD before including this header) or dllimport for C++ clients such as
// vsthost_native_test. The default calling convention on x64 is the only one, so no
// __stdcall/__cdecl is needed.
#ifdef CSV_VSTHOST_BUILD
#define VH_API extern "C" __declspec(dllexport)
#else
#define VH_API extern "C" __declspec(dllimport)
#endif

/// Opaque plug-in instance (defined in vsthost.cpp). Created by vh_load, destroyed by vh_free;
/// the caller holds it as an IntPtr and must not use it after vh_free.
typedef struct VhInstance VhInstance;

/// Bridge API version (increase when the ABI changes). Currently 2.
///   1: the original API.
///   2: adds vh_take_restart_flags; all version-1 exports are unchanged, so a client written
///      for version 1 keeps working (check vh_version() >= 2 before calling the new export).
/// Thread: any.
VH_API int32_t vh_version ();

/// Loads a module (.vst3 bundle folder or single-file .vst3) and writes a UTF-8 JSON
/// description of its factory and classes into buf:
///   {"vendor":..,"url":..,"classes":[{"name","vendor","category","subCategories","cid",
///    "version","sdkVersion"}, ...]}
/// `category` is e.g. "Audio Module Class" (processors) or "Component Controller Class";
/// `cid` is the 32-hex-digit class id to pass to vh_load.
/// @param path  UTF-16 path of the module.
/// @param buf   caller buffer (may be null when cap is 0, to query the size).
/// @param cap   size of buf in bytes, including room for the null terminator.
/// @return the JSON length in bytes (excluding the null terminator; retry with a bigger buffer
///         when it is >= cap), or, on error, -1 - (length of the error text), with the error
///         text written into buf.
/// Thread: UI thread (shares the module cache with vh_load). Note: this runs the plug-in's
/// DLL entry code inside the calling process; the app does its scanning in a child process
/// so a crashing/hanging module cannot take it down.
VH_API int32_t vh_scan_module (const wchar_t* path, char* buf, int32_t cap);

/// Creates an instance of the class `cid` (32 hex digits) from the module at `path`:
/// creates and initializes the component (processor), finds or creates its edit controller,
/// connects the two and synchronises the controller with the processor's state. The instance
/// is NOT ready to process yet: call vh_prepare.
/// @param path    UTF-16 path of the module (modules are shared between instances).
/// @param cid     class id as printed by vh_scan_module (ASCII, 32 hex digits).
/// @param err     receives a UTF-8 reason on failure (may be null).
/// @param errCap  size of err in bytes.
/// @return a new instance owned by the caller (release with vh_free), or null on failure.
/// Thread: UI thread.
VH_API VhInstance* vh_load (const wchar_t* path, const char* cid, char* err, int32_t errCap);

/// Releases an instance: closes its editor, stops processing, disconnects and terminates the
/// controller and component, and unloads the module when no other instance uses it.
/// `h` is invalid afterwards. Null is ignored.
/// Thread: UI thread, and only once the audio thread can no longer call vh_process_stereo on it.
VH_API void vh_free (VhInstance* h);

/// (Re)configures processing: deactivates if needed, asks for stereo main buses (falling back
/// to what the plug-in proposes), calls setupProcessing(realtime, 32-bit float, maxBlock,
/// sampleRate), allocates the process buffers, then setActive(true) + setProcessing(true).
/// @param sampleRate  in Hz (e.g. 48000).
/// @param maxBlock    largest number of frames that will be processed in one plug-in call;
///                    values below 32 are raised to 32. vh_process_stereo splits larger
///                    host blocks into chunks of this size.
/// @return 0 on success; -1 bad/crashed instance, -2 setupProcessing refused, -3 buffer
///         allocation failed, -4 the plug-in crashed while being configured (bus setup,
///         setupProcessing) or activated (it is then disabled), -5 setActive refused,
///         -6 C++ exception.
/// Thread: UI thread, never concurrently with vh_process_stereo on the same instance.
VH_API int32_t vh_prepare (VhInstance* h, double sampleRate, int32_t maxBlock);

/// Stops processing and deactivates (setProcessing(false), setActive(false)); keeps the
/// instance, its state and its editor. vh_process_stereo returns 1 until the next vh_prepare.
/// Thread: UI thread.
VH_API void vh_unprepare (VhInstance* h);

/// UTF-8 JSON with the instance's properties:
///   {"name","vendor","subCategories","cid","instrument","singleComponent","inputs","outputs",
///    "latency","parameters","editor","crashed"}
/// `inputs`/`outputs` are the channel counts of the main audio buses after vh_prepare;
/// `latency` is in samples (0 when not prepared). Same return convention as vh_scan_module.
/// Thread: UI thread (it may create a temporary editor view to answer "editor").
VH_API int32_t vh_info (VhInstance* h, char* buf, int32_t cap);

/// Processes `frames` frames of stereo audio in place (two separate, non-interleaved float
/// buffers, nominally -1..1). Effects replace L/R with their output; instruments ADD their
/// output to L/R (so they mix with the sound already there). A mono plug-in input receives
/// (L+R)/2; a mono output is copied to both channels.
/// @return 0 on success, 1 when the instance is not prepared or the arguments are invalid
///         (audio untouched), 2 when the plug-in crashed (it is then disabled for good).
/// Thread: the audio thread only. Real-time safe: no locks waited on, no allocation.
VH_API int32_t vh_process_stereo (VhInstance* h, float* left, float* right, int32_t frames);

/// Queues a MIDI 1.0 channel message for the next processed block. Note on/off become VST3
/// note events (velocity 0..127 mapped to 0..1; note on with velocity 0 = note off);
/// CC (0xBn), pitch bend (0xEn) and channel pressure (0xDn) are converted to normalised
/// parameter changes through IMidiMapping when the plug-in offers it, and dropped otherwise;
/// such a change goes to the processor (next block) AND, on the UI thread, to the edit
/// controller (see vh_param_get), so an open editor follows it. Other messages are ignored.
/// @param status  status byte including the channel in the low nibble.
/// Thread: any (short spin lock; never waits on the audio thread).
VH_API void vh_midi (VhInstance* h, uint8_t status, uint8_t data1, uint8_t data2);

/// Number of parameters exposed by the edit controller (0 without a controller).
/// Thread: UI thread.
VH_API int32_t vh_param_count (VhInstance* h);

/// Parameter info by index (0..vh_param_count-1): id (the stable ParamID used by the other
/// vh_param_* functions), title (UTF-16, truncated to titleCap - 1 characters), flags
/// (Steinberg::Vst::ParameterInfo::flags: kCanAutomate, kIsReadOnly, kIsBypass, kIsList...),
/// step count (0 = continuous, n = n+1 discrete values) and default normalised value (0..1).
/// Every out pointer may be null.
/// @return 0 on success, -1 no controller, -2 the plug-in rejected the index.
/// Thread: UI thread.
VH_API int32_t vh_param_info (VhInstance* h, int32_t index, uint32_t* id, wchar_t* title, int32_t titleCap,
                              int32_t* flags, int32_t* stepCount, double* defaultValue);

/// Current normalised value (0..1) of a parameter as the edit controller sees it (0 without a
/// controller). Parameter changes reported by the processor (outputParameterChanges) and
/// MIDI-driven changes (vh_midi) are queued and applied to the controller on the UI thread:
/// by this function, vh_param_count, vh_info, vh_get_state, vh_take_restart_flags, and a
/// 30 ms timer of the editor window while it is open. Thread: UI thread.
VH_API double vh_param_get (VhInstance* h, uint32_t id);

/// Sets a parameter (normalised, clamped to 0..1): updates the controller (so the editor
/// shows it) and queues the change for the processor's next block.
/// Thread: UI thread (see the threading note at the top of this file).
VH_API void vh_param_set (VhInstance* h, uint32_t id, double value);

/// Value as the plug-in displays it ("-3.0 dB", "440 Hz"), for a given normalised value, in
/// UTF-16, truncated to cap - 1 characters. Returns 0 on success, -1 bad arguments,
/// -2 the plug-in has no text for it. Thread: UI thread.
VH_API int32_t vh_param_text (VhInstance* h, uint32_t id, double value, wchar_t* buf, int32_t cap);

/// Serializes the processor (component) and controller state into one opaque blob:
///   "CVS1" | u32 component size | component bytes | u32 controller size | controller bytes
/// (little-endian sizes). Returns the total size needed; nothing is written when buf is null or
/// cap is smaller than that, so call once with buf = null / cap = 0 to query the size.
/// Negative on error (-1 bad handle, -2 larger than 2 GB, -3 exception).
/// Thread: UI thread.
VH_API int32_t vh_get_state (VhInstance* h, uint8_t* buf, int32_t cap);

/// Restores a blob produced by vh_get_state (component state first, mirrored to the
/// controller with setComponentState, then the controller's own state).
/// @return 0 on success, -1 bad arguments / not a CVS1 blob, -2 truncated blob, -3 the
///         component rejected its state, -4 exception.
/// Thread: UI thread.
VH_API int32_t vh_set_state (VhInstance* h, const uint8_t* buf, int32_t len);

/// True (1) when the plug-in has an editor that can be embedded in an HWND, otherwise 0.
/// Thread: UI thread (it creates and immediately releases a view to find out).
VH_API int32_t vh_has_editor (VhInstance* h);

/// Opens the plug-in's editor in a top-level window owned by `owner` (may be null) with the
/// given title. An owned window stays above its owner and is destroyed with it. Must be called
/// on a thread that pumps window messages (the app's UI thread): the window and the plug-in's
/// view live on that thread.
/// @return the window handle (HWND), or 0 on failure. Calling it again while the window is
///         open just restores/shows it and brings it to the front.
VH_API intptr_t vh_editor_open (VhInstance* h, intptr_t owner, const wchar_t* title);

/// Closes the editor window (no-op when closed). The view is released in WM_DESTROY.
/// Thread: UI thread.
VH_API void vh_editor_close (VhInstance* h);

/// The editor window handle while it is open, otherwise 0 (the user may close it at any
/// time with the window's close button). Thread: UI thread.
VH_API intptr_t vh_editor_hwnd (VhInstance* h);

/// (Since API version 2.) Returns the IComponentHandler::restartComponent flags the plug-in
/// raised since the previous call, and clears them. Bit values are Steinberg::Vst::RestartFlags:
///   kReloadComponent 1, kIoChanged 2, kParamValuesChanged 4, kLatencyChanged 8,
///   kParamTitlesChanged 16, kMidiCCAssignmentChanged 32, kNoteExpressionChanged 64,
///   kIoTitlesChanged 128, kPrefetchableSupportChanged 256, kRoutingInfoChanged 512,
///   kKeyswitchChanged 1024, kParamIDMappingChanged 2048.
/// What the caller should do: for kLatencyChanged or kIoChanged (or kReloadComponent),
/// re-prepare the instance (vh_prepare with the same sample rate / block size; it
/// deactivates first) at a moment when the audio thread is not inside vh_process_stereo -
/// until then the plug-in keeps running with its old latency/bus layout; for
/// kParamTitlesChanged / kParamValuesChanged, re-read vh_param_info / vh_param_get.
/// The DLL itself never acts on these flags (a re-prepare must not overlap processing, and
/// only the app can schedule it safely). Also applies pending processor/MIDI parameter
/// changes to the controller (see vh_param_get), so polling it from a UI timer (e.g. every
/// 100-250 ms) is enough to keep everything in sync. Null handle: returns 0.
/// Thread: UI thread.
VH_API int32_t vh_take_restart_flags (VhInstance* h);

/// Dispatches pending window messages of the calling thread for up to `ms` milliseconds
/// (console tests only; the app's UI thread already runs a message loop).
VH_API void vh_pump (int32_t ms);

/// Captures a window with PrintWindow(PW_RENDERFULLCONTENT) into a 24-bit .bmp file
/// (test helper used by `CsoundVisual.exe --vsthost-test --editor`).
/// @return the number of non-black pixels found (a rough "something was drawn" check),
///         -1 invalid window/path, -2 empty window, -3 the file could not be written,
///         -4 the off-screen bitmap could not be created (window too large / GDI exhausted).
VH_API int32_t vh_capture_window (intptr_t hwnd, const wchar_t* bmpPath);
