//------------------------------------------------------------------------------
// Csound Visual VST3 - run-time binding of the Csound 7 C API
//
// The plug-in never links against csound7_64.lib. Instead it loads
// csound7_64.dll with LoadLibraryExW (full path) and resolves every function it
// needs with GetProcAddress. That way the plug-in DLL itself always loads, even
// if Csound is missing: the host can still scan it, and the plug-in outputs
// silence and reports the problem through its IPC "status" reply.
//
// Only the small subset of csound.h used by the plug-in is mirrored here. The
// Csound 7 build shipped with Csound Visual uses 64-bit doubles for cs_float
// (see include/csound/float-version.h, USE_DOUBLE), which is verified at load
// time with csoundGetSizeOfCsFloat().
//
// Why mirror the declarations instead of including csound.h?
//   * The plug-in must build without the Csound SDK headers on the include path
//     and must not pull in csound.h's PUBLIC/dllimport decorations, which would
//     force an import library.
//   * Every type below is a plain re-declaration whose memory layout has to
//     match csound.h exactly (structs are passed by pointer across the DLL
//     boundary). If a future Csound changes one of these structs, this file must
//     be updated by hand.
//
// Who uses it:
//   * csoundengine.cpp (CsoundInstance) calls the function pointers to create,
//     compile and run instances.
//   * plugin.cpp reads CsoundApi::get().status for the IPC "status" reply and
//     uses toUtf8/fromUtf8 for paths, pipe names and descriptor text.
//
// Threading: CsoundApi::get() is safe to call from any thread (std::call_once);
// the table is immutable once it returns. Whether a given Csound function may be
// called concurrently is Csound's own rule: the plug-in only ever drives one
// CSOUND* from one thread at a time (built on a non-audio thread, then rendered
// exclusively on the audio thread).
//------------------------------------------------------------------------------
#pragma once

#include <cstdint>
#include <string>

namespace CsoundVisual {

/// Opaque Csound instance (CSOUND* in csound.h). Never dereferenced by the plug-in;
/// only passed back to Csound functions.
struct CSOUND_;
using CSOUND = CSOUND_;

/// Sample type of this Csound build (cs_float / MYFLT). Must be double: CsoundApi::get()
/// refuses any build whose csoundGetSizeOfCsFloat() is not sizeof(double).
using csfloat = double;

// --- Constants mirrored from csound.h -----------------------------------------
// Values copied verbatim from csound.h; they are part of Csound's ABI.
enum : int32_t
{
	// csoundInitialize() flags.
	CSOUNDINIT_NO_SIGNAL_HANDLER = 1, ///< do not install SIGINT/SIGSEGV... handlers (would hijack the DAW's)
	CSOUNDINIT_NO_ATEXIT = 2,         ///< do not register an atexit() cleanup hook in the host process

	// controlChannelType bits (channel kind in the low 4 bits, direction flags above).
	CSOUND_CONTROL_CHANNEL = 1,     ///< k-rate scalar (one cs_float)
	CSOUND_CHANNEL_TYPE_MASK = 15,  ///< mask that isolates the channel kind
	CSOUND_INPUT_CHANNEL = 16,      ///< host -> orchestra (what chnget / chnk "i" read)
	CSOUND_OUTPUT_CHANNEL = 32,     ///< orchestra -> host

	// controlChannelBehavior: how the hints' min/max range should be interpreted.
	CSOUND_CONTROL_CHANNEL_NO_HINTS = 0, ///< no range declared (plug-in assumes 0..1)
	CSOUND_CONTROL_CHANNEL_INT = 1,      ///< integer steps between min and max
	CSOUND_CONTROL_CHANNEL_LIN = 2,      ///< linear between min and max
	CSOUND_CONTROL_CHANNEL_EXP = 3,      ///< exponential (min and max must be > 0)
};

/// Mirror of controlChannelHints_t (csound.h). Layout must match exactly: Csound fills an
/// array of ControlChannelInfo (which embeds this struct) in csoundListChannels().
struct ControlChannelHints
{
	int32_t behav; ///< controlChannelBehavior (enum = int)
	csfloat dflt;  ///< default value, in channel units
	csfloat min;   ///< lower bound, in channel units
	csfloat max;   ///< upper bound, in channel units
	int32_t x;     ///< GUI layout hints (unused by the plug-in)
	int32_t y;
	int32_t width;
	int32_t height;
	char* attributes; ///< free-form attribute string; owned by the channel list
};

/// Mirror of controlChannelInfo_t (csound.h): one element of the list returned by
/// csoundListChannels(). `name` and `hints.attributes` are owned by Csound and are only
/// valid until csoundDeleteChannelList() is called on the list.
struct ControlChannelInfo
{
	char* name;   ///< channel name (UTF-8 / ASCII as written in the orchestra)
	int32_t type; ///< controlChannelType bit set (kind | INPUT | OUTPUT)
	ControlChannelHints hints;
};

// --- Function pointer types ---------------------------------------------------
/// csoundSetMessageStringCallback(): receives every message Csound prints, already
/// formatted. `attr` holds CSOUNDMSG_* type/colour bits (ignored by the plug-in). `str` is a
/// fragment, not necessarily a whole line. Runs on whatever thread made Csound print.
using MessageStringCallback = void (*) (CSOUND*, int32_t attr, const char* str);
/// External MIDI input "open device" callback. May store a per-device pointer in *userData,
/// which Csound hands back to the read/close callbacks. Returns 0 on success.
using MidiInOpenCallback = int32_t (*) (CSOUND*, void** userData, const char* devName);
/// External MIDI read callback: called by Csound once per control cycle (inside
/// csoundPerformKsmps); copies up to nBytes raw MIDI bytes into buf and returns how many
/// were written (0 = nothing pending).
using MidiReadCallback = int32_t (*) (CSOUND*, void* userData, unsigned char* buf, int32_t nBytes);
/// External MIDI "close device" callback. Returns 0 on success.
using MidiInCloseCallback = int32_t (*) (CSOUND*, void* userData);

/// Table of Csound entry points, resolved once per process by CsoundApi::get().
///
/// Each member has the same signature as the csound.h function whose name is the member
/// name prefixed with "csound" (Initialize -> csoundInitialize, ...). All pointers are null
/// until get() succeeds; callers must test `ok` first. The table lives in a function-local
/// static and the DLL is never unloaded (FreeLibrary is never called), so the pointers stay
/// valid until the process exits. Not unloading avoids having to prove that no CSOUND
/// instance, opcode library or callback is still alive when the last plug-in goes away.
struct CsoundApi
{
	// --- process-wide setup --------------------------------------------------
	int32_t (*Initialize) (int32_t flags) = nullptr;                               ///< once per process, CSOUNDINIT_* flags
	CSOUND* (*Create) (void* hostData, const char* opcodedir) = nullptr;           ///< hostData = owning CsoundInstance*
	void (*Destroy) (CSOUND*) = nullptr;
	int32_t (*GetVersion) () = nullptr;                                            ///< e.g. 7000 for 7.0.0 (major*1000 + minor*10 + patch)
	int32_t (*GetSizeOfCsFloat) () = nullptr;                                      ///< 8 for double builds
	int32_t (*SetGlobalEnv) (const char* name, const char* value) = nullptr;       ///< must precede the first Create
	// --- compile / perform -----------------------------------------------------
	int32_t (*SetOption) (CSOUND*, const char* option) = nullptr;                  ///< one command-line option per call
	int32_t (*CompileCSD) (CSOUND*, const char* csd, int32_t mode, int32_t async) = nullptr; ///< mode 1 = csd is CSD text, 0 = file name
	int32_t (*Start) (CSOUND*) = nullptr;
	int32_t (*PerformKsmps) (CSOUND*) = nullptr;                                   ///< one control cycle; non-zero = finished/error
	// --- audio buffers and engine properties -------------------------------------
	csfloat* (*GetSpin) (CSOUND*) = nullptr;   ///< input buffer, ksmps * nchnls_i interleaved, in 0dbfs units
	const csfloat* (*GetSpout) (CSOUND*) = nullptr; ///< output buffer, ksmps * nchnls interleaved, in 0dbfs units
	csfloat (*GetSr) (CSOUND*) = nullptr;      ///< sample rate in Hz
	uint32_t (*GetKsmps) (CSOUND*) = nullptr;  ///< frames per control cycle
	uint32_t (*GetChannels) (CSOUND*, int32_t isInput) = nullptr; ///< nchnls (0) or nchnls_i (1)
	csfloat (*Get0dBFS) (CSOUND*) = nullptr;   ///< amplitude that corresponds to digital full scale
	void* (*GetHostData) (CSOUND*) = nullptr;  ///< the hostData pointer given to Create
	// --- host I/O hooks ------------------------------------------------------
	void (*SetHostAudioIO) (CSOUND*) = nullptr; ///< host owns spin/spout: Csound opens no audio device
	void (*SetHostMIDIIO) (CSOUND*) = nullptr;  ///< MIDI goes through the external callbacks below
	void (*SetExternalMidiInOpenCallback) (CSOUND*, MidiInOpenCallback) = nullptr;
	void (*SetExternalMidiReadCallback) (CSOUND*, MidiReadCallback) = nullptr;
	void (*SetExternalMidiInCloseCallback) (CSOUND*, MidiInCloseCallback) = nullptr;
	void (*SetMessageStringCallback) (CSOUND*, MessageStringCallback) = nullptr;
	// --- named channels (software bus) ---------------------------------------------
	int32_t (*ListChannels) (CSOUND*, ControlChannelInfo** lst) = nullptr;       ///< returns count; free with DeleteChannelList
	void (*DeleteChannelList) (CSOUND*, ControlChannelInfo* lst) = nullptr;
	int32_t (*GetChannelPtr) (CSOUND*, void** p, const char* name, int32_t type) = nullptr; ///< creates the channel if missing; 0 = OK
	void (*SetControlChannel) (CSOUND*, const char* name, csfloat val) = nullptr; ///< resolved but not used (pointers are cached instead)

	/// True once every entry point was resolved, the float size was verified and
	/// csoundInitialize ran. When false, every function pointer must be treated as unusable.
	bool ok = false;
	/// Human readable description of what was loaded ("Csound 7.0.0 loaded from ..."), or
	/// why loading failed (list of paths tried, missing exports...). Reported verbatim in the
	/// IPC "status" reply and in the plug-in's message log.
	std::string status;
	/// Installation root that was used (folder that contains bin\, plugins64\, samples\).
	/// Informational only (also part of `status`).
	std::wstring root;
	/// Opcode plug-in directory (<root>\plugins64, UTF-8, passed to csoundCreate).
	std::string pluginsDirUtf8;

	/// Loads Csound once per process (thread-safe, idempotent) and returns the table.
	/// Never throws. Check ok before calling any function pointer.
	/// A failed load is not retried: the result is cached for the life of the process, so
	/// installing Csound later requires restarting the host.
	/// @return reference to the process-wide table (never null, lives until exit).
	static const CsoundApi& get ();
};

/// Converts UTF-16 to UTF-8 (WideCharToMultiByte, CP_UTF8). Returns "" for empty input.
/// Invalid UTF-16 (lone surrogates) is replaced by U+FFFD by Windows.
std::string toUtf8 (const std::wstring& s);
/// Converts UTF-8 to UTF-16 (MultiByteToWideChar, CP_UTF8). Returns L"" for empty input.
/// Invalid byte sequences are replaced by U+FFFD by Windows.
std::wstring fromUtf8 (const std::string& s);
/// Folder that contains this plug-in DLL (…\CsoundVisual.vst3\Contents\x86_64-win), without
/// trailing backslash. Found from the module handle of the DLL itself, so it is correct no
/// matter which executable loaded the plug-in. Returns L"" on failure.
std::wstring moduleDirectory ();

} // namespace CsoundVisual
