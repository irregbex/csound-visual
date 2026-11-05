//------------------------------------------------------------------------------
// Csound Visual VST3 - the plug-in class (Instrument and FX share this class)
//
// CsoundVisualPlugin is what the host instantiates for both factory classes
// (factory.cpp): "Csound Visual Instrument" (MIDI in -> stereo out) and
// "Csound Visual FX" (stereo in + MIDI in -> stereo out). It owns one running
// Csound orchestra (a CsoundInstance, csoundengine.h), exposes 16 generic
// automatable parameters that are renamed after the orchestra's control
// channels, converts VST3 events to MIDI bytes for Csound, saves the CSD text in
// the host project, and serves the Csound Visual app over a per-instance named
// pipe (IpcServer, ipcserver.h). There is no custom editor: hosts show their
// generic parameter editor.
//
// VST3 lifecycle as this class sees it:
//   constructor -> initialize()     busses, parameters, UI window, IPC server,
//                                   first compile of the default/current CSD
//   setState()/setComponentState()  project load (may come before or after
//                                   activation)
//   setupProcessing()               sample rate and max block size; scratch
//                                   buffers are (re)allocated here
//   setActive(true)                 rebuild at the host sample rate if needed
//   setProcessing(true/false)       nothing to do (no-op)
//   process()                       audio thread, repeatedly
//   setActive(false) -> terminate() -> destructor (last release())
//
// Threads:
//   * host UI thread : initialize/terminate, setActive, setState/getState,
//                      parameter queries, and the hidden window's messages;
//   * host audio thread : process() only (never allocates, never blocks);
//   * IPC thread (one per instance, owned by `ipc`) : handleIpc().
//
// Architecture (see README.md for the full picture):
//   * SingleComponentEffect: processor and edit controller are one object, so the
//     IPC thread, the audio thread and the host's UI thread all see the same state
//     without IConnectionPoint messaging.
//   * The current CsoundInstance is owned by `active`. It is replaced with a
//     "build fully, then swap" protocol: the new instance is compiled and started
//     on a non-audio thread, published under `swapMutex` (the audio thread only
//     ever try_locks it and outputs silence for that one block if it is busy),
//     and the old instance is destroyed afterwards, off the audio thread.
//   * `lifetimeMutex` serializes all non-audio operations that create, destroy or
//     inspect instances (setActive, setState, IPC load/status/set).
//   * Anything that must reach the host's UI thread (parameter renames,
//     restartComponent, performEdit) is marshalled through a hidden message-only
//     window created on the thread that called initialize().
//   * Why a named pipe for the app link: it needs no network port, no firewall
//     prompt and no admin rights, is local-only (PIPE_REJECT_REMOTE_CLIENTS),
//     and needs only plain Win32 calls here and NamedPipeClientStream in the
//     .NET app (Integration/VstBridge.cs). The app discovers instances
//     through the descriptor JSON files written by updateDescriptor().
//------------------------------------------------------------------------------
#pragma once

#include "csoundengine.h"
#include "ipcserver.h"

#include "pluginterfaces/vst/ivstchannelcontextinfo.h"
#include "pluginterfaces/vst/ivstmidicontrollers.h"
#include "public.sdk/source/vst/vstsinglecomponenteffect.h"

#include <array>
#include <atomic>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

namespace CsoundVisual {

/// Which factory class an instance was created as. Decides the bus layout (the FX
/// has a stereo audio input), the default orchestra, whether the host input is fed
/// into Csound's spin, and what bypass outputs (delayed dry signal vs silence).
enum class PluginKind
{
	Instrument, ///< "Csound Visual Instrument": MIDI in -> stereo out
	Fx          ///< "Csound Visual FX": stereo in + MIDI in -> stereo out
};

/// Parameter IDs (VST3 ParamID tags). These numbers are stored in host projects
/// and automation, so they must never change.
///
/// All VST3 parameter values are normalised doubles in 0..1:
///   * slots 0..15: mapped to the channel's real range by ChannelDesc::fromNormalized
///     (linear, exponential or integer, from the chnk hints);
///   * pitch bend: 0..1 -> 14-bit bend 0..16383 (0.5 = centre);
///   * CCs and aftertouch: 0..1 -> 7-bit 0..127;
///   * bypass: off below 0.5, on from 0.5 (step count 1).
enum ParamIds : Steinberg::Vst::ParamID
{
	kParamSlotFirst = 0, ///< 0..15: "Param 1".."Param 16", renamed after each compile
	kParamPitchBend = 100, ///< hidden, mapped from MIDI pitch bend through IMidiMapping
	kParamModWheel,        ///< CC1
	kParamBreath,          ///< CC2
	kParamExpression,      ///< CC11
	kParamSustain,         ///< CC64
	kParamAftertouch,      ///< channel pressure
	kParamBypass = 200,    ///< host bypass switch (kIsBypass)
	/// First id of the hidden MIDI parameters of MIDI channels 2..16 (see midiParamId()).
	/// 100..105 above stay the parameters of MIDI channel 1, so projects and automation
	/// saved before per-channel mapping existed keep their meaning.
	kParamMidiChannelsBase = 300,
};
/// Number of hidden MIDI parameters per MIDI channel (pitch bend, CC1, CC2, CC11, CC64,
/// aftertouch, in that order; index 0..5 = id - kParamPitchBend for channel 1).
constexpr int kNumMidiParams = 6;
/// MIDI channels covered by the hidden parameters (all 16 of the event bus).
constexpr int kNumMidiChannels = 16;

/// Hidden MIDI parameter id for MIDI channel `ch` (0..15) and controller index `idx`
/// (0..kNumMidiParams-1). Why per channel: VST3 delivers controllers only as parameter
/// changes (IMidiMapping), and a parameter carries no MIDI channel. With one parameter
/// set shared by all channels the plug-in had to guess the channel (it used the channel
/// of the last note-on), so a CC or pitch bend on channel 2 while channel 1 played went
/// to the wrong channel. One set per channel keeps every controller on its own channel.
///   channel 1:      100..105   (the original ids, unchanged)
///   channel 2..16:  300 + (ch - 1) * 6 + idx   -> 300..389
constexpr Steinberg::Vst::ParamID midiParamId (int ch, int idx)
{
	return ch == 0 ? (Steinberg::Vst::ParamID)(kParamPitchBend + idx)
	               : (Steinberg::Vst::ParamID)(kParamMidiChannelsBase + (ch - 1) * kNumMidiParams + idx);
}

/// Inverse of midiParamId(): true when `id` is a hidden MIDI parameter, and then sets the
/// MIDI channel (0..15) and controller index (0..kNumMidiParams-1).
constexpr bool decodeMidiParamId (Steinberg::Vst::ParamID id, int& ch, int& idx)
{
	if (id >= (Steinberg::Vst::ParamID)kParamPitchBend && id < (Steinberg::Vst::ParamID)(kParamPitchBend + kNumMidiParams))
	{
		ch = 0;
		idx = (int)(id - kParamPitchBend);
		return true;
	}
	if (id >= (Steinberg::Vst::ParamID)kParamMidiChannelsBase &&
	    id < (Steinberg::Vst::ParamID)(kParamMidiChannelsBase + (kNumMidiChannels - 1) * kNumMidiParams))
	{
		int rel = (int)(id - kParamMidiChannelsBase);
		ch = 1 + rel / kNumMidiParams;
		idx = rel % kNumMidiParams;
		return true;
	}
	return false;
}

/// The plug-in object: VST3 audio processor and edit controller in one
/// (SingleComponentEffect), plus IMidiMapping (so the host delivers pitch bend,
/// CCs and aftertouch as parameter changes; VST3 has no MIDI CC events) and
/// ChannelContext::IInfoListener (so the host tells us the track name, which the
/// app shows when listing instances).
///
/// Ownership: created by the factory with a reference count of 1 and destroyed by
/// the host's final release(); ref counting comes from FObject via
/// SingleComponentEffect (see OBJ_METHODS/REFCOUNT_METHODS below).
class CsoundVisualPlugin : public Steinberg::Vst::SingleComponentEffect,
                           public Steinberg::Vst::IMidiMapping,
                           public Steinberg::Vst::ChannelContext::IInfoListener
{
public:
	/// Sets up the default CSD of this kind and the parameter defaults. Does no I/O
	/// and starts no threads; everything else happens in initialize().
	explicit CsoundVisualPlugin (PluginKind kind);
	/// Stops the IPC thread, removes the descriptor file, destroys the hidden window and
	/// the Csound instance (normally already done by terminate(); repeated here for hosts
	/// that release without terminating).
	~CsoundVisualPlugin () override;

	/// Factory create functions (see factory.cpp). Return a new object with one
	/// reference owned by the caller, cast to IAudioProcessor* so the FUnknown*
	/// conversion is unambiguous with multiple inheritance.
	static Steinberg::FUnknown* createInstrument (void*);
	static Steinberg::FUnknown* createFx (void*);

	// --- IPluginBase ---
	/// Host UI thread, once after construction. Adds busses and parameters, reads
	/// the host name, creates the UI-marshalling window, starts the IPC server,
	/// writes the descriptor file and compiles the current CSD. `context` is the
	/// host's IHostApplication (not owned).
	Steinberg::tresult PLUGIN_API initialize (Steinberg::FUnknown* context) SMTG_OVERRIDE;
	/// Host UI thread, before destruction. Stops IPC, deletes the descriptor file,
	/// destroys the Csound instance and the hidden window.
	Steinberg::tresult PLUGIN_API terminate () SMTG_OVERRIDE;

	// --- IComponent ---
	/// Activation (state != 0) rebuilds the orchestra when there is none, when the host
	/// sample rate differs from the instance's, or when the instance has already
	/// rendered audio (so every activation starts the score from time 0). Deactivation
	/// keeps the instance. Host UI thread.
	Steinberg::tresult PLUGIN_API setActive (Steinberg::TBool state) SMTG_OVERRIDE;
	/// Restores a project chunk written by getState() (format documented in plugin.cpp)
	/// and recompiles the saved CSD with the saved parameter values. Returns
	/// kResultFalse for a stream that is not ours, is truncated, or was saved by the
	/// other plug-in class (Instrument vs FX).
	Steinberg::tresult PLUGIN_API setState (Steinberg::IBStream* state) SMTG_OVERRIDE;
	/// Writes the CSD name and text, the 16 slot values and the bypass flag.
	Steinberg::tresult PLUGIN_API getState (Steinberg::IBStream* state) SMTG_OVERRIDE;

	// --- IAudioProcessor ---
	/// Accepts only the native layout: stereo out, plus stereo in for the FX.
	Steinberg::tresult PLUGIN_API setBusArrangements (Steinberg::Vst::SpeakerArrangement* inputs,
	                                                  Steinberg::int32 numIns,
	                                                  Steinberg::Vst::SpeakerArrangement* outputs,
	                                                  Steinberg::int32 numOuts) SMTG_OVERRIDE;
	/// 32-bit float only (Csound itself computes in double internally).
	Steinberg::tresult PLUGIN_API canProcessSampleSize (Steinberg::int32 symbolicSampleSize) SMTG_OVERRIDE;
	/// Stores the host sample rate (Hz) and preallocates the audio-thread buffers for
	/// maxSamplesPerBlock frames. Called while inactive.
	Steinberg::tresult PLUGIN_API setupProcessing (Steinberg::Vst::ProcessSetup& setup) SMTG_OVERRIDE;
	/// No-op: Csound keeps running between setProcessing calls.
	Steinberg::tresult PLUGIN_API setProcessing (Steinberg::TBool state) SMTG_OVERRIDE;
	/// Audio thread. Converts parameter changes and events to MIDI bytes, writes the
	/// slot values into the Csound channels, renders the block (split at event
	/// offsets), applies bypass and updates the meters for the app.
	Steinberg::tresult PLUGIN_API process (Steinberg::Vst::ProcessData& data) SMTG_OVERRIDE;
	/// ksmps of the running orchestra, in samples (the spin/spout scheme delays the
	/// output by exactly one control period).
	Steinberg::uint32 PLUGIN_API getLatencySamples () SMTG_OVERRIDE;
	/// Always kInfiniteTail (reverbs and delays may ring indefinitely).
	Steinberg::uint32 PLUGIN_API getTailSamples () SMTG_OVERRIDE;

	// --- IEditController ---
	/// Host -> plug-in parameter change outside process() (generic editor, automation
	/// read on the UI side, state restore). Mirrors slot and bypass values into the
	/// atomics read by the audio thread, then lets the SDK store it.
	Steinberg::tresult PLUGIN_API setParamNormalized (Steinberg::Vst::ParamID tag,
	                                                  Steinberg::Vst::ParamValue value) SMTG_OVERRIDE;
	/// Display text of a slot value in channel units (e.g. "2400" for a cutoff);
	/// an em dash for unused slots. Other parameters use the SDK default.
	Steinberg::tresult PLUGIN_API getParamStringByValue (Steinberg::Vst::ParamID tag,
	                                                     Steinberg::Vst::ParamValue valueNormalized,
	                                                     Steinberg::Vst::String128 string) SMTG_OVERRIDE;
	/// Inverse of getParamStringByValue: parses a number typed in channel units.
	Steinberg::tresult PLUGIN_API getParamValueByString (Steinberg::Vst::ParamID tag,
	                                                     Steinberg::Vst::TChar* string,
	                                                     Steinberg::Vst::ParamValue& valueNormalized) SMTG_OVERRIDE;
	/// Controller half of the state restore. A no-op: in a single component the
	/// component state has already been applied by setState().
	Steinberg::tresult PLUGIN_API setComponentState (Steinberg::IBStream* state) SMTG_OVERRIDE;

	// --- IMidiMapping ---
	/// Maps pitch bend, CC1, CC2, CC11, CC64 and channel aftertouch on event bus 0 to the
	/// hidden parameters of that MIDI channel (100..105 for channel 1, 300..389 for
	/// channels 2..16, see midiParamId). Other controllers: kResultFalse.
	Steinberg::tresult PLUGIN_API getMidiControllerAssignment (Steinberg::int32 busIndex,
	                                                           Steinberg::int16 channel,
	                                                           Steinberg::Vst::CtrlNumber midiControllerNumber,
	                                                           Steinberg::Vst::ParamID& id) SMTG_OVERRIDE;

	// --- IInfoListener ---
	/// The host reports the track (channel) context; we keep the track name for the
	/// descriptor file and the "status" reply.
	Steinberg::tresult PLUGIN_API setChannelContextInfos (Steinberg::Vst::IAttributeList* list) SMTG_OVERRIDE;

	// FObject boilerplate: OBJ_METHODS declares the class-name/isA helpers,
	// REFCOUNT_METHODS forwards addRef/release to SingleComponentEffect (one shared
	// reference count for all interfaces). queryInterface adds our two extra
	// interfaces in front of the base class's list.
	OBJ_METHODS (CsoundVisualPlugin, SingleComponentEffect)
	Steinberg::tresult PLUGIN_API queryInterface (const Steinberg::TUID iid, void** obj) SMTG_OVERRIDE;
	REFCOUNT_METHODS (SingleComponentEffect)

	/// Called by the hidden window procedure on the UI thread.
	/// Applies whatever is pending (pendingTitles, pendingLatency, pendingEdit):
	/// renames the slot parameters, pushes values to the host and calls
	/// restartComponent. Must only run on the thread that called initialize().
	void applyUiUpdate ();

private:
	/// UI-side copy of one parameter slot's channel mapping (for titles, display
	/// strings and typed values). desc.ptr is always null in this copy: the UI never
	/// touches Csound memory. Guarded by uiMutex.
	struct SlotInfo
	{
		bool used = false; ///< false: no channel assigned (the slot shows an em dash)
		ChannelDesc desc;  ///< name, range hints and default of the assigned channel
	};
	/// One MIDI 1.0 message collected in process() before rendering.
	struct MidiEv
	{
		Steinberg::int32 offset; ///< frame offset within the current block
		unsigned char bytes[3];  ///< status byte + up to two data bytes
		unsigned char size;      ///< number of valid bytes (2 or 3)
	};

	// --- engine management (non-audio threads) ---
	/// Builds an instance for `csd` at the current host sample rate and swaps it in.
	/// Caller must hold lifetimeMutex. resetParams: use the channel defaults as parameter
	/// values (new orchestra); otherwise keep the current parameter values.
	/// Returns false and fills `error` when Csound cannot build the orchestra; the
	/// running instance (if any) is then left untouched. On success the previous
	/// instance is destroyed on the calling thread and pendingTitles (and
	/// pendingLatency if ksmps changed) are set; the caller then calls
	/// scheduleUiUpdate() after releasing lifetimeMutex. Never on the audio thread.
	bool rebuildLocked (const std::string& csd, bool resetParams, std::string& error);
	/// Makes sure an instance exists (builds the current CSD if not). Holds lifetimeMutex.
	/// (i.e. the caller must already hold it; a failed build is silently ignored and
	/// leaves `active` null; the error string is discarded, but Csound's own messages
	/// are in the log returned by "status".)
	void ensureInstanceLocked ();
	/// Asks the UI thread to rename parameters / push values / report latency.
	/// Returns at once when nothing is pending; runs applyUiUpdate() directly when
	/// already on the UI thread, otherwise posts kMsgUiUpdate to the hidden window.
	/// Must not be called while holding lifetimeMutex (restartComponent may re-enter
	/// setActive, which takes it).
	void scheduleUiUpdate ();
	/// (Re)writes this instance's descriptor JSON file (pipe, pid, kind, track,
	/// csdName, host) atomically. Any non-audio thread; takes descMutex.
	void updateDescriptor ();
	/// Deletes the descriptor file (terminate()). Takes descMutex.
	void removeDescriptor ();
	/// IPC thread: parses one JSON request ("load", "status", "meters", "set") and
	/// returns the JSON reply text. Never throws; malformed input yields
	/// {"ok":false,"error":...}.
	std::string handleIpc (const std::string& request);
	/// Sets csdName and its copy used by the descriptor file. Caller holds lifetimeMutex.
	void setCsdNameLocked (const std::string& name);

	/// Host sample rate in Hz from setupProcessing(), or 48000 before the host has
	/// told us (the default orchestra is then compiled at 48 kHz and rebuilt on
	/// activation if the host rate differs).
	double currentSampleRate () const;

	// --- identity ---
	PluginKind kind;            ///< Instrument or FX, fixed at construction
	int instanceNumber = 0;     ///< 1, 2, 3... per process (gInstanceCounter); part of the pipe name
	std::wstring pipeName;      ///< \\.\pipe\CsoundVisualVST.<pid>.<instanceNumber>
	std::wstring descriptorPath; ///< %LOCALAPPDATA%\CsoundVisual\vst-instances\<pid>.<n>.json, empty if unavailable

	// --- engine ---
	EngineShared shared; // must outlive every CsoundInstance (declared first)
	/// Serializes every non-audio operation that creates, destroys or inspects
	/// `active` (setActive, setState, IPC load/status/set, destruction). The audio
	/// thread never takes it, so it may be held for a whole Csound compile.
	std::mutex lifetimeMutex;
	/// Guards the `active` pointer itself. Held by writers only for the pointer swap;
	/// the audio thread only try_locks it (and renders silence if that fails).
	/// Lock order: lifetimeMutex before swapMutex.
	std::mutex swapMutex;
	/// The running orchestra (null if Csound could not be loaded or the very first
	/// compile failed). Replaced only by rebuildLocked().
	std::unique_ptr<CsoundInstance> active;
	std::string csdText;            // guarded by lifetimeMutex; full CSD source (saved in the project)
	std::string csdName = "default"; // guarded by lifetimeMutex; display name sent by the app
	std::atomic<double> hostSampleRate {48000.0}; ///< Hz, set by setupProcessing()
	std::atomic<Steinberg::uint32> latency {0};    ///< samples (= ksmps), read by getLatencySamples()
	std::atomic<bool> componentActive {false};     ///< last setActive() state (reported by "status")
	/// False until the slot values have been set once (by the first compile or a
	/// setState). The first compile then initialises them to the channel defaults;
	/// later rebuilds keep them. Guarded by lifetimeMutex.
	bool paramsInitialized = false; // guarded by lifetimeMutex

	// --- parameters ---
	/// Current normalised (0..1) value of each slot parameter. Written by the audio
	/// thread (automation), the UI thread (setParamNormalized) and the IPC thread
	/// ("set", "load"); read by all of them. Lock-free atomics, last writer wins.
	std::array<std::atomic<double>, kNumSlots> paramNorm;
	/// Last normalised value of each hidden MIDI parameter, index = ch * kNumMidiParams + idx
	/// (see midiParamId / decodeMidiParamId). Audio thread writes; kept for diagnostics.
	std::array<std::atomic<double>, kNumMidiChannels * kNumMidiParams> midiParamNorm;
	std::mutex uiMutex;                      // guards uiSlots
	std::array<SlotInfo, kNumSlots> uiSlots; // mapping copy for display strings / titles

	// --- audio thread scratch (preallocated) ---
	/// MIDI messages of the current block (parameter-derived + note events), sorted
	/// by offset before rendering. Fixed size so process() never allocates; extra
	/// events beyond 1024 per block are dropped.
	std::array<MidiEv, 1024> midiEvents {};
	// (The former `lastNoteChannel` guess is gone: every hidden MIDI parameter now belongs
	// to one MIDI channel, see midiParamId().)
	/// Csound renders here first so that bypass can output the delayed dry signal (FX) or
	/// silence (Instrument) while Csound keeps running. Sized in setupProcessing().
	std::vector<float> scratchL, scratchR;
	/// Dry-signal delay line (latency-compensated bypass for the FX).
	/// kDryRing frames per channel (a power of two, so indices wrap with a mask); the
	/// usable delay is at most kDryRing - 1 frames, far above any sensible ksmps.
	static constexpr int kDryRing = 16384;
	std::vector<float> dryL, dryR; ///< sized in setupProcessing(); audio thread only afterwards
	int dryWrite = 0;              ///< next write index into dryL/dryR (audio thread)
	/// Host bypass state (kParamBypass >= 0.5). Written by process() from automation,
	/// by setParamNormalized() and setState(); read by process().
	std::atomic<bool> bypass {false};

	// Output meters for the Csound Visual app (IPC command "meters"): the audio thread keeps the
	// maximum |sample| per channel since the last read and a ring of the mono mix; the pipe thread
	// takes the peaks (resetting them) and copies the newest scope samples. Torn reads of the ring
	// are harmless for a display.
	static constexpr int kScopeSize = 4096; // power of two
	std::array<float, kScopeSize> scopeRing {};    ///< mono mix (L+R)/2 of the output, oldest overwritten
	std::atomic<Steinberg::uint32> scopeWrite {0}; ///< total samples written (wraps); index = value & (kScopeSize-1)
	std::array<std::atomic<float>, 2> meterPeak {}; ///< max |sample| (L, R) since the last "meters" request, linear 0..1+

	// --- UI thread marshalling ---
	void* uiWindow = nullptr; // HWND
	unsigned long uiThreadId = 0; ///< Win32 thread ID of the thread that called initialize()
	/// Set by rebuildLocked(): slot titles/defaults changed -> rename parameters and
	/// restartComponent(kParamTitlesChanged | kParamValuesChanged).
	std::atomic<bool> pendingTitles {false};
	/// Set by rebuildLocked() when ksmps changed -> restartComponent(kLatencyChanged).
	std::atomic<bool> pendingLatency {false};
	/// Avoids compiling an orchestra twice when a load changes ksmps. rebuildLocked() sets
	/// this (with the time, GetTickCount64 ms) when it announces a latency change; the host
	/// answers kLatencyChanged by deactivating and re-activating us, and setActive(true)
	/// would normally rebuild because the new instance has already rendered audio
	/// (used()). If this flag is set, recent (< kLatencyRestartWindowMs) and the sample rate
	/// is unchanged, setActive keeps the just-built instance instead. Guarded by
	/// lifetimeMutex (written in rebuildLocked, consumed in setActive).
	bool latencyRestartPending = false;
	unsigned long long latencyRestartTick = 0; ///< GetTickCount64() when the flag was set
	static constexpr unsigned long long kLatencyRestartWindowMs = 10000;
	/// Detaches and destroys the hidden window (terminate() and, if the host skipped
	/// terminate(), the destructor). Safe to call twice.
	void destroyUiWindow ();
	std::array<std::atomic<bool>, kNumSlots> pendingEdit; // performEdit requested (IPC "set")

	// --- IPC ---
	IpcServer ipc; ///< pipe server thread; calls handleIpc() for each request
	std::mutex descMutex; ///< guards trackName, descCsdName, hostName and the descriptor file writes
	std::string trackName;               ///< from setChannelContextInfos (empty until the host sends it)
	std::string descCsdName = "default"; ///< copy of csdName readable without lifetimeMutex
	std::string hostName;                ///< IHostApplication::getName, e.g. "Cubase"
};

} // namespace CsoundVisual
