//------------------------------------------------------------------------------
// CsoundVisualVstHost - VST3 hosting bridge used by Csound Visual (see vsthost.h)
//
// One VhInstance = one plug-in instance: its module, component/processor, edit controller
// (the same object for "single component" plug-ins, a separate one otherwise), the process
// buffers, two lock-protected inboxes (parameter changes and MIDI events) that the audio
// thread drains with a try-lock, and the optional editor window.
//
// VST3 IN ONE PARAGRAPH (what this file relies on)
//   A .vst3 module exports a factory. The factory creates a *component* (IComponent +
//   IAudioProcessor: the DSP side, runs on the audio thread) and an *edit controller*
//   (IEditController: parameters, texts, editor view; UI thread). They are either one object
//   ("single component") or two objects that talk through IConnectionPoint messages. The
//   host must drive the component through a fixed lifecycle:
//       createInstance -> initialize(host context)
//       -> setBusArrangements / activateBus -> setupProcessing(sample rate, block size)
//       -> setActive(true) -> setProcessing(true) -> process()... (audio thread)
//       -> setProcessing(false) -> setActive(false) -> terminate -> release
//   (teardown is the exact reverse; see vh_prepare / vh_unprepare / vh_free). Parameters are
//   "normalised" doubles 0..1 identified by a ParamID; the processor receives changes as
//   IParameterChanges queues inside ProcessData, the controller via setParamNormalized.
//   State is saved as two IBStream blobs: component (processor) state and controller state.
//
// DESIGN DECISIONS
//   * Flat C API (vsthost.h) so .NET can P/Invoke it without marshalling C++ types.
//   * Static CRT (see CMakeLists.txt): the DLL only depends on Windows system DLLs.
//   * Every call into plug-in code that is likely to fault (module load, creation,
//     activation, process, view attach/remove) runs under SEH (__try/__except): a plug-in
//     that crashes is disabled and reported instead of terminating Csound Visual. This does
//     not catch fail-fast errors (__fastfail / 0xC0000409), which Windows never lets a
//     process intercept.
//   * Threads: the audio thread only calls vh_process_stereo and never waits for anything:
//     parameter changes and MIDI from other threads go into small spin-locked "inboxes" that
//     the audio thread drains with tryLock (if a producer happens to hold the lock, the data
//     simply arrives one block later). Everything else runs on the UI thread, which also
//     owns the editor windows (plain Win32 windows created by this DLL, pumped by the app's
//     own message loop).
//   * Audio is always 32-bit float, stereo in/out on the main buses; auxiliary buses
//     (side-chains, extra outputs) are deactivated and fed silence.
//------------------------------------------------------------------------------
// Makes vsthost.h declare the functions as dllexport (see VH_API).
#define CSV_VSTHOST_BUILD
#include "vsthost.h"

// VST3 SDK hosting helpers (compiled into this DLL through the sdk_hosting target and the
// extra sources listed in CMakeLists.txt): MemoryStream (an in-memory IBStream), EventList /
// ParameterChanges (host-side implementations of the process-data interfaces),
// HostApplication (IHostApplication), Module (loads a .vst3 and wraps its factory) and
// HostProcessData (allocates the per-bus audio buffers of ProcessData).

#include "public.sdk/source/common/memorystream.h"
#include "public.sdk/source/vst/hosting/eventlist.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/vst/hosting/processdata.h"
#include "pluginterfaces/base/funknownimpl.h"
#include "pluginterfaces/gui/iplugview.h"
#include "pluginterfaces/gui/iplugviewcontentscalesupport.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivstcomponent.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/ivstmessage.h"
#include "pluginterfaces/vst/ivstmidicontrollers.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "pluginterfaces/vst/vstspeaker.h"

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <map>
#include <memory>
#include <string>
#include <vector>

using namespace Steinberg;
using namespace Steinberg::Vst;

// Internal helpers live in an anonymous namespace (internal linkage): only the vh_* functions
// are exported.
namespace {

//------------------------------------------------------------------------------
// Small helpers
//------------------------------------------------------------------------------

/// Converts a null-terminated UTF-16 string to UTF-8 (empty for null/empty input).
/// The first WideCharToMultiByte call measures (n includes the terminator), the second converts
/// into the std::string's own buffer, whose size excludes the terminator (std::string keeps
/// one extra byte for it, so writing n bytes is safe).
std::string toUtf8 (const wchar_t* w)
{
	if (!w || !*w)
		return {};
	int n = WideCharToMultiByte (CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
	std::string s (n > 0 ? n - 1 : 0, '\0');
	if (n > 1)
		WideCharToMultiByte (CP_UTF8, 0, w, -1, s.data (), n, nullptr, nullptr);
	return s;
}

/// Converts a VST3 string (TChar = char16_t, UTF-16) to UTF-8. VST3 strings (String128) are
/// fixed 128-unit arrays; the 512 cap is only a safety stop for an unterminated string.
/// On Windows wchar_t is also UTF-16, so the copy is unit-for-unit.
std::string toUtf8 (const TChar* t)
{
	std::wstring w;
	for (int i = 0; t && t[i] && i < 512; ++i)
		w += (wchar_t)t[i];
	return toUtf8 (w.c_str ());
}

/// Copies a UTF-8 string into a caller buffer (null terminated, truncated to cap - 1 bytes)
/// and returns its FULL length, so the caller can detect truncation (result >= cap) and retry.
/// @param buf  may be null (or cap 0) to only measure.
int32_t putString (const std::string& s, char* buf, int32_t cap)
{
	if (buf && cap > 0)
	{
		size_t n = std::min<size_t> (s.size (), (size_t)cap - 1);
		std::memcpy (buf, s.data (), n);
		buf[n] = 0;
	}
	return (int32_t)s.size ();
}

/// Escapes a UTF-8 string for use inside a JSON string literal (quotes, backslashes and
/// control characters). Bytes >= 0x80 are copied as they are: JSON allows raw UTF-8.
std::string jsonEscape (const std::string& s)
{
	std::string o;
	o.reserve (s.size () + 8);
	for (unsigned char c : s)
	{
		switch (c)
		{
			case '"': o += "\\\""; break;
			case '\\': o += "\\\\"; break;
			case '\n': o += "\\n"; break;
			case '\r': o += "\\r"; break;
			case '\t': o += "\\t"; break;
			default:
				if (c < 0x20)
				{
					char tmp[8];
					std::snprintf (tmp, sizeof tmp, "\\u%04x", c);
					o += tmp;
				}
				else
					o += (char)c;
		}
	}
	return o;
}

/// Runs f() under a structured exception handler: a plug-in that crashes (access violation...)
/// must not take the whole application down. Returns false when an exception was caught.
/// (Kept free of objects with destructors, as __try requires: MSVC refuses __try in a function
/// that needs C++ object unwinding, so callers wrap their work in a lambda and pass it here.)
/// Note: a C++ exception thrown by the plug-in is also an SEH exception on Windows and is
/// caught here too; the state of the plug-in afterwards is unknown, which is why callers
/// disable it rather than retry.
template <typename F>
bool sehCall (F& f)
{
	__try
	{
		f ();
		return true;
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		return false;
	}
}

/// Minimal spin lock. Producers (UI / MIDI threads) lock; the audio thread only try-locks.
/// A spin lock (rather than a mutex) because the critical sections are a push_back or a
/// vector swap, i.e. a few nanoseconds, and because the audio thread must never be put to
/// sleep by the OS: tryLock either succeeds immediately or the audio thread moves on.
struct SpinLock
{
	std::atomic_flag flag = ATOMIC_FLAG_INIT;
	/// Spins until acquired (YieldProcessor = the PAUSE instruction, friendly to the other
	/// hyper-thread). Never called by the audio thread.
	void lock ()
	{
		while (flag.test_and_set (std::memory_order_acquire))
			YieldProcessor ();
	}
	/// One attempt, never waits: true when the lock was acquired (the audio thread's path).
	bool tryLock () { return !flag.test_and_set (std::memory_order_acquire); }
	/// Releases the lock; release ordering publishes the writes made while holding it.
	void unlock () { flag.clear (std::memory_order_release); }
};

/// Lock-free single-producer / single-consumer ring of fixed capacity (a power of two), with
/// inline storage: push and pop never allocate, never block and never wait, so the producer
/// may be the audio thread. Used to carry parameter changes REPORTED BY THE PROCESSOR
/// (ProcessData::outputParameterChanges, filled by the plug-in inside process()) from the
/// audio thread (producer) to the UI thread (consumer), which applies them to the edit
/// controller. Counters are free-running (only their difference matters); the producer
/// publishes an element with a release store of `head`, the consumer frees a slot with a
/// release store of `tail`, each side acquires the other's counter.
template <typename T, size_t Capacity>
struct SpscRing
{
	static_assert ((Capacity & (Capacity - 1)) == 0, "Capacity must be a power of two");
	T items[Capacity] {};
	std::atomic<size_t> head {0}; ///< total elements ever pushed (producer-owned)
	std::atomic<size_t> tail {0}; ///< total elements ever popped (consumer-owned)

	/// Producer only. False (element dropped) when the ring is full, i.e. the consumer has
	/// not drained it for a long time; a later change of the same parameter supersedes it.
	bool push (const T& v)
	{
		size_t h = head.load (std::memory_order_relaxed);
		if (h - tail.load (std::memory_order_acquire) >= Capacity)
			return false;
		items[h & (Capacity - 1)] = v;
		head.store (h + 1, std::memory_order_release);
		return true;
	}
	/// Consumer only. False when empty.
	bool pop (T& v)
	{
		size_t t = tail.load (std::memory_order_relaxed);
		if (t == head.load (std::memory_order_acquire))
			return false;
		v = items[t & (Capacity - 1)];
		tail.store (t + 1, std::memory_order_release);
		return true;
	}
};

//------------------------------------------------------------------------------
// Host context given to every plug-in
//------------------------------------------------------------------------------
/// The IHostApplication passed to IPluginFactory3::setHostContext and to every
/// IComponent/IEditController::initialize. The SDK's HostApplication already implements
/// createInstance (IMessage / IAttributeList objects that plug-ins use to talk between their
/// processor and controller) and IPlugInterfaceSupport; we only override the host name that
/// plug-ins may display or use to enable host-specific behaviour.
class CsvHostApplication : public HostApplication
{
public:
	/// Writes "Csound Visual" into the plug-in's String128 (UTF-16, 128 units incl. terminator).
	tresult PLUGIN_API getName (String128 name) override
	{
		const wchar_t* n = L"Csound Visual";
		int i = 0;
		for (; n[i] && i < 127; ++i)
			name[i] = (TChar)n[i];
		name[i] = 0;
		return kResultTrue;
	}
};

/// The single host context shared by every plug-in instance.
/// Deliberately leaked: plug-ins may addRef it and keep it past our own lifetime (e.g. until
/// their module's DLL is unloaded), so deleting it at process exit could leave them with a
/// dangling pointer. A function-local static pointer also avoids static-destruction order
/// problems at DLL unload.
CsvHostApplication& host ()
{
	static CsvHostApplication* h = new CsvHostApplication (); // never released (plug-ins may keep it)
	return *h;
}

/// Modules shared by all instances (UI thread only), keyed by UTF-8 path.
/// weak_ptr: the cache never keeps a module alive by itself. Every VhInstance holds a strong
/// Module::Ptr; when the last instance of a module is freed the module is unloaded (the SDK's
/// Module destructor calls the plug-in's ExitDll and FreeLibrary). Expired entries are simply
/// overwritten on the next load of the same path.
std::map<std::string, std::weak_ptr<VST3::Hosting::Module>>& moduleCache ()
{
	static std::map<std::string, std::weak_ptr<VST3::Hosting::Module>> m;
	return m;
}

/// Returns the already loaded module for `path` or loads it (LoadLibrary + InitDll + factory),
/// under SEH because a module's DllMain / InitDll is plug-in code that may crash.
/// Loading the same module twice would call its InitDll twice, which many plug-ins do not
/// tolerate; sharing also saves memory when the same plug-in is inserted several times.
/// @param path   UTF-8 path of the .vst3 bundle or file.
/// @param error  receives the SDK's error text on failure.
/// @return a strong reference, or null on failure. UI thread only (the cache is not locked).
VST3::Hosting::Module::Ptr loadModule (const std::string& path, std::string& error)
{
	auto& cache = moduleCache ();
	if (auto it = cache.find (path); it != cache.end ())
		if (auto m = it->second.lock ())
			return m;
	VST3::Hosting::Module::Ptr m;
	auto load = [&] { m = VST3::Hosting::Module::create (path, error); };
	if (!sehCall (load))
	{
		error = "the module crashed while loading";
		return nullptr;
	}
	if (m)
		cache[path] = m;
	return m;
}

/// Window class name of the editor windows (registered once by registerEditorClass).
const wchar_t* kEditorClass = L"CsoundVisualVstEditor";

} // namespace

/// One queued parameter change: a VST3 ParamID and its new normalised value (0..1).
struct ParamChange
{
	ParamID id;
	ParamValue value;
};

class ComponentHandler;
class EditorFrame;

//------------------------------------------------------------------------------
// The instance
//------------------------------------------------------------------------------
/// Everything that belongs to one loaded plug-in instance; the opaque handle of the C API.
/// Created by vh_load, destroyed by vh_free. Members are grouped by the thread that uses them:
/// the plug-in objects and editor fields are UI-thread data; `data`, `inParams`, `outParams`,
/// `events`, `context`, `position` and the *Work vectors are touched only by the audio thread
/// while `prepared` is true (and by vh_prepare/vh_free while it is false); the inboxes are
/// shared and protected by their spin locks.
/// Interface pointers are IPtr<> (the SDK's smart pointer: addRef on copy, release on reset or
/// destruction), so clearing a member releases our reference to the plug-in object.
struct VhInstance
{
	VST3::Hosting::Module::Ptr module;      ///< keeps the plug-in's DLL loaded while this instance exists
	IPtr<IComponent> component;             ///< the processor object (bus info, state, activation)
	IPtr<IAudioProcessor> processor;        ///< same object as `component`, queried for IAudioProcessor
	IPtr<IEditController> controller;       ///< parameters / editor; may be null (no controller)
	IPtr<IConnectionPoint> componentCP, controllerCP; ///< set only when the two sides were connected
	IPtr<ComponentHandler> handler;         ///< our IComponentHandler given to the controller
	bool singleComponent = false;           ///< true when component and controller are one object
	bool instrument = false;                ///< true for instruments/generators: output is ADDED to L/R
	std::string name, vendor, category, subCategories, cidText; ///< class info (UTF-8), for vh_info

	// processing
	std::atomic<bool> prepared {false};     ///< audio thread may process (set last by vh_prepare)
	std::atomic<bool> crashed {false};      ///< the plug-in faulted; never processed or prepared again
	bool active = false;                    ///< setActive(true) succeeded (UI-thread bookkeeping)
	double sampleRate = 48000;              ///< Hz, as given to vh_prepare
	int32 maxBlock = 0;                     ///< max frames per process() call, as given to setupProcessing
	int32 inChannels = 0, outChannels = 0;  ///< channel count of main input/output bus after prepare
	bool hasEventInput = false;             ///< the plug-in has an event (MIDI) input bus
	HostProcessData data;                   ///< ProcessData + the audio buffers of every bus (SDK helper)
	ParameterChanges inParams {64};         ///< parameter changes for the current block (max 64 distinct ids)
	ParameterChanges outParams {64};        ///< changes reported by the processor; forwarded to ctrlFromAudio, cleared each block
	EventList events {512};                 ///< note events for the current block (max 512)
	ProcessContext context {};              ///< transport info given to the plug-in each block
	int64 position = 0;                     ///< frames processed since vh_prepare (the fake song position)

	// inboxes (filled by any thread, drained by the audio thread)
	// The audio thread swaps the inbox with its own *Work vector under tryLock, so producers
	// can keep pushing while it converts the batch. Both vectors are pre-reserved and only
	// ever clear()ed, so in the steady state no thread allocates (a producer may still grow
	// its inbox past 1024 entries; that allocation happens on the producer, never on audio).
	SpinLock paramLock;
	std::vector<ParamChange> paramInbox, paramWork;
	SpinLock eventLock;
	std::vector<Event> eventInbox, eventWork;

	// Parameter changes that must reach the EDIT CONTROLLER (UI-thread object), so the
	// editor, vh_param_get and the saved controller state follow them. In VST3 the host is
	// responsible for this mirror; before, these changes reached only the processor (or were
	// dropped), so an open editor did not move and vh_param_get returned stale values.
	// Two sources, two queues (each with a single producer):
	//   * ctrlFromAudio: changes the processor reports in outputParameterChanges (meters,
	//     automation the plug-in generates itself, ...). Producer = audio thread, so it is
	//     the lock-free SpscRing (no lock, no allocation on audio).
	//   * ctrlFromMidi: MIDI CC / pitch bend / pressure translated through IMidiMapping in
	//     vh_midi. Producer = the MIDI thread (not real-time), consumer = UI thread, so a
	//     spin-locked vector is enough (capped at 4096 like the other inboxes).
	// Consumer = drainToController() on the UI thread, called by the UI-thread entry points
	// (vh_param_get, vh_param_count, vh_info, vh_get_state, vh_take_restart_flags)
	// and by a 30 ms timer of the editor window while it is open.
	SpscRing<ParamChange, 1024> ctrlFromAudio;
	SpinLock ctrlMidiLock;
	std::vector<ParamChange> ctrlFromMidi, ctrlMidiWork;

	/// restartComponent() flags (RestartFlags: kLatencyChanged, kIoChanged,
	/// kParamValuesChanged...) accumulated since the last vh_take_restart_flags(). Set by
	/// ComponentHandler::restartComponent (any thread a plug-in chooses), read+cleared by the
	/// app through vh_take_restart_flags. Atomic OR / exchange, so no flag is ever lost.
	std::atomic<int32> restartFlags {0};

	// MIDI CC / pitch bend / pressure -> parameter (IMidiMapping), per channel; -1 = none
	// 130 controllers per channel: CC 0..127 plus the VST3 pseudo-controllers
	// kAfterTouch (128, channel pressure) and kPitchBend (129).
	std::vector<int64> midiMap; // [channel * 130 + controller]

	// editor (UI thread only)
	IPtr<IPlugView> view;                   ///< the attached editor view, null when closed
	IPtr<EditorFrame> frame;                ///< our IPlugFrame given to the view
	HWND hwnd = nullptr;                    ///< the editor's top-level window, null when closed
	bool inResize = false;                  ///< true while WE resize the window (suppresses WM_SIZE feedback)

	/// Pre-reserves the inbox/work vectors so the audio thread never allocates.
	VhInstance ()
	{
		paramInbox.reserve (1024);
		paramWork.reserve (1024);
		eventInbox.reserve (1024);
		eventWork.reserve (1024);
		ctrlFromMidi.reserve (256);
		ctrlMidiWork.reserve (256);
	}

	/// Queues a MIDI-derived parameter change for the edit controller (MIDI thread; see
	/// ctrlFromMidi). Same 4096 cap as the processor inboxes.
	void pushCtrlFromMidi (ParamID id, ParamValue v)
	{
		ctrlMidiLock.lock ();
		if (ctrlFromMidi.size () < 4096)
			ctrlFromMidi.push_back ({id, v});
		ctrlMidiLock.unlock ();
	}

	/// Queues a parameter change for the processor's next block (any thread). Changes beyond
	/// 4096 pending ones are dropped: the audio thread is evidently not running, and an
	/// unbounded queue would only grow.
	void pushParam (ParamID id, ParamValue v)
	{
		paramLock.lock ();
		if (paramInbox.size () < 4096)
			paramInbox.push_back ({id, v});
		paramLock.unlock ();
	}
	/// Queues a VST3 event (note on/off) for the next block (any thread); same 4096 cap.
	void pushEvent (const Event& e)
	{
		eventLock.lock ();
		if (eventInbox.size () < 4096)
			eventInbox.push_back (e);
		eventLock.unlock ();
	}
};

//------------------------------------------------------------------------------
// IComponentHandler: edits made in the plug-in's editor reach the processor
//------------------------------------------------------------------------------
/// The host's IComponentHandler, given to the edit controller with setComponentHandler.
/// In VST3 the controller never talks to the processor about parameter values directly: when
/// the user turns a knob in the editor, the controller calls beginEdit / performEdit / endEdit
/// on this handler, and the HOST is responsible for delivering the value to the processor in
/// the next process() call. performEdit therefore just queues the change in the instance's
/// parameter inbox. Called on the UI thread (the editor's thread).
/// U::Implements<U::Directly<...>> (funknownimpl.h) provides queryInterface / addRef / release;
/// instances are created with owned(new ...) so the IPtr holds the only initial reference.
class ComponentHandler : public U::Implements<U::Directly<IComponentHandler>>
{
public:
	explicit ComponentHandler (VhInstance* i) : inst (i) {}
	/// Back pointer to the owning instance. vh_free sets it to null before deleting the
	/// instance, because the plug-in may keep (and call) its reference to the handler longer.
	VhInstance* inst;

	/// Start of a gesture (mouse down on a knob). Only needed for automation recording.
	tresult PLUGIN_API beginEdit (ParamID) override { return kResultOk; }
	/// A new normalised value for `id` from the editor: forward it to the processor.
	tresult PLUGIN_API performEdit (ParamID id, ParamValue v) override
	{
		if (inst)
			inst->pushParam (id, v);
		return kResultOk;
	}
	/// End of a gesture. Nothing to do.
	tresult PLUGIN_API endEdit (ParamID) override { return kResultOk; }
	/// The plug-in asks the host to re-read latency, bus layout, parameter list... The flags
	/// are recorded (atomic OR into VhInstance::restartFlags) instead of acted on here: a
	/// latency/IO change needs a vh_unprepare/vh_prepare cycle, which must not overlap the
	/// audio thread and which only the app can schedule safely (VstChain unpublishes the
	/// instance first). The app polls vh_take_restart_flags() and re-prepares when it sees
	/// kLatencyChanged or kIoChanged (and re-reads parameter titles/values for
	/// kParamTitlesChanged / kParamValuesChanged). Previously the request was ignored, so a
	/// plug-in that changed its latency kept running with the stale value.
	tresult PLUGIN_API restartComponent (int32 flags) override
	{
		if (inst)
			inst->restartFlags.fetch_or (flags, std::memory_order_relaxed);
		return kResultOk;
	}
};

//------------------------------------------------------------------------------
// IPlugFrame: lets the editor ask for a new window size
//------------------------------------------------------------------------------
/// The host's IPlugFrame, given to the editor view with setFrame. Plug-ins with resizable or
/// zoomable GUIs call resizeView when they want a different size (e.g. after a zoom change, or
/// right after being attached). UI thread.
class EditorFrame : public U::Implements<U::Directly<IPlugFrame>>
{
public:
	explicit EditorFrame (VhInstance* i) : inst (i) {}
	VhInstance* inst; ///< owning instance (the frame is released with the view, before the instance)

	/// Resizes our top-level window so its CLIENT area is r (in physical pixels), then confirms
	/// the new size to the view with onSize, as the VST3 spec requires the host to do.
	tresult PLUGIN_API resizeView (IPlugView* view, ViewRect* r) override
	{
		if (!inst || !inst->hwnd || !view || !r)
			return kInvalidArgument;
		// Convert the requested client size to a window size (caption + borders).
		LONG style = GetWindowLongW (inst->hwnd, GWL_STYLE);
		LONG ex = GetWindowLongW (inst->hwnd, GWL_EXSTYLE);
		RECT rc {0, 0, r->getWidth (), r->getHeight ()};
		AdjustWindowRectEx (&rc, (DWORD)style, FALSE, (DWORD)ex);
		// inResize stops editorProc's WM_SIZE handler (sent synchronously by SetWindowPos)
		// from calling onSize itself, which would re-enter the plug-in in the middle of its
		// own resizeView call; we call onSize once, below, with the exact requested rect.
		inst->inResize = true;
		SetWindowPos (inst->hwnd, nullptr, 0, 0, rc.right - rc.left, rc.bottom - rc.top,
		              SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
		inst->inResize = false;
		view->onSize (r);
		return kResultTrue;
	}
};

namespace {

//------------------------------------------------------------------------------
// Processor -> controller parameter mirror (UI thread)
//------------------------------------------------------------------------------
/// Applies every queued processor-reported (ctrlFromAudio) and MIDI-derived (ctrlFromMidi)
/// parameter change to the edit controller with setParamNormalized, as a VST3 host must do,
/// so the editor, vh_param_get and the controller's saved state stay in sync with what the
/// processor is really using. UI thread only (the controller is a UI-thread object). The
/// MIDI vector is swapped out under its spin lock and converted after unlocking; the audio
/// ring is lock-free. The plug-in calls run under SEH: a fault marks the instance crashed.
/// Without a controller (or after a crash) the queues are simply emptied.
void drainToController (VhInstance* inst)
{
	if (!inst)
		return;
	inst->ctrlMidiLock.lock ();
	inst->ctrlMidiWork.swap (inst->ctrlFromMidi); // O(1), no allocation
	inst->ctrlMidiLock.unlock ();
	const bool apply = inst->controller && !inst->crashed.load (std::memory_order_relaxed);
	auto run = [&] {
		ParamChange pc;
		while (inst->ctrlFromAudio.pop (pc))
			if (apply)
				inst->controller->setParamNormalized (pc.id, pc.value);
		if (apply)
			for (auto& m : inst->ctrlMidiWork)
				inst->controller->setParamNormalized (m.id, m.value);
	};
	if (!sehCall (run))
		inst->crashed = true; // the controller faulted: stop using this plug-in
	inst->ctrlMidiWork.clear ();
}

/// Marks the plug-in as crashed after a fault in a UI-thread call (editor, info, prepare):
/// its state is unknown, so the audio thread stops calling it (vh_process_stereo returns 2,
/// which VstChain reports) and vh_prepare refuses it from now on.
void markCrashed (VhInstance* inst)
{
	inst->crashed = true;
	inst->prepared.store (false, std::memory_order_release);
}

/// Timer id of the editor window's controller-sync timer (see editorProc WM_TIMER).
constexpr UINT_PTR kSyncTimerId = 1;
/// Period of that timer in ms: ~33 updates per second, smooth enough for meters/knobs.
constexpr UINT kSyncTimerMs = 30;

//------------------------------------------------------------------------------
// Editor window
//------------------------------------------------------------------------------
/// Detaches and releases the editor view (IPlugView::removed + setFrame(nullptr), the reverse
/// of setFrame + attached), then drops our frame. Called from WM_DESTROY, i.e. while the
/// window still exists, because a view must be removed before its parent HWND disappears.
/// Plug-in code runs under SEH. UI thread.
void destroyView (VhInstance* inst)
{
	if (inst->view)
	{
		auto v = inst->view; // local reference keeps the view alive during the calls
		auto removed = [&] {
			v->removed ();
			v->setFrame (nullptr);
		};
		sehCall (removed);
		inst->view = nullptr;
	}
	inst->frame = nullptr;
}

/// Window procedure of the editor windows (class kEditorClass). The plug-in's own GUI is a
/// child window created by the plug-in inside our client area; this procedure only handles the
/// frame: user resizing (kept within the plug-in's constraints), closing and cleanup.
/// The owning VhInstance is stored in GWLP_USERDATA (it is passed as lpCreateParams to
/// CreateWindowExW and saved at WM_NCCREATE, the first message a window receives).
/// UI thread (the thread that created the window).
LRESULT CALLBACK editorProc (HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
	auto* inst = reinterpret_cast<VhInstance*> (GetWindowLongPtrW (hwnd, GWLP_USERDATA));
	switch (msg)
	{
		case WM_NCCREATE:
		{
			auto* cs = reinterpret_cast<CREATESTRUCTW*> (lp);
			SetWindowLongPtrW (hwnd, GWLP_USERDATA, (LONG_PTR)cs->lpCreateParams);
			break;
		}
		case WM_TIMER:
			// Controller-sync timer (started in vh_editor_open): while the editor is open,
			// mirror the processor's reported parameter changes and MIDI-driven changes into
			// the controller so the GUI follows them even if the app calls nothing.
			if (wp == kSyncTimerId)
			{
				if (inst)
					drainToController (inst);
				return 0;
			}
			break;
		case WM_SIZING:
		{
			// canResize / checkSizeConstraint are plug-in calls: they run under SEH like every
			// other call into the plug-in (before, a fault here took the whole app down).
			// A fault marks the instance crashed and closes the editor (posted, so the window
			// is not destroyed in the middle of its own message).
			bool resizable = false;
			auto canResize = [&] { resizable = inst && inst->view && inst->view->canResize () == kResultTrue; };
			if (!sehCall (canResize))
			{
				markCrashed (inst);
				PostMessageW (hwnd, WM_CLOSE, 0, 0);
				break;
			}
			if (resizable)
			{
				// Let the plug-in constrain the size the user drags to.
				// lp is the proposed WINDOW rect (screen coordinates), which we may modify in
				// place. Compute the frame thickness (an empty client rect adjusted for the
				// style), derive the proposed CLIENT size, let checkSizeConstraint snap it,
				// then rebuild the window rect from the edge being dragged (wp = WMSZ_*), so
				// the opposite edge stays put.
				auto* wr = reinterpret_cast<RECT*> (lp);
				RECT frameRc {0, 0, 0, 0};
				AdjustWindowRectEx (&frameRc, (DWORD)GetWindowLongW (hwnd, GWL_STYLE), FALSE,
				                    (DWORD)GetWindowLongW (hwnd, GWL_EXSTYLE));
				ViewRect vr {0, 0, (wr->right - wr->left) - (frameRc.right - frameRc.left),
				             (wr->bottom - wr->top) - (frameRc.bottom - frameRc.top)};
				bool snapped = false;
				auto check = [&] { snapped = inst->view->checkSizeConstraint (&vr) == kResultTrue; };
				if (!sehCall (check))
				{
					markCrashed (inst);
					PostMessageW (hwnd, WM_CLOSE, 0, 0);
					break;
				}
				if (snapped)
				{
					int w = vr.getWidth () + (frameRc.right - frameRc.left);
					int h = vr.getHeight () + (frameRc.bottom - frameRc.top);
					if (wp == WMSZ_LEFT || wp == WMSZ_TOPLEFT || wp == WMSZ_BOTTOMLEFT)
						wr->left = wr->right - w;
					else
						wr->right = wr->left + w;
					if (wp == WMSZ_TOP || wp == WMSZ_TOPLEFT || wp == WMSZ_TOPRIGHT)
						wr->top = wr->bottom - h;
					else
						wr->bottom = wr->top + h;
				}
				return TRUE; // TRUE = we processed WM_SIZING (the rect may have been changed)
			}
			break;
		}
		case WM_SIZE:
			// The client area changed size because of the user (dragging, maximize/restore):
			// tell the view. Skipped while we resize the window ourselves (inResize, see
			// EditorFrame::resizeView), when minimized (client size 0x0) and when the view
			// already has this size (avoids a resize feedback loop with plug-ins that call
			// resizeView from onSize).
			if (inst && inst->view && !inst->inResize && wp != SIZE_MINIMIZED)
			{
				RECT rc;
				GetClientRect (hwnd, &rc);
				ViewRect vr {0, 0, rc.right, rc.bottom};
				// getSize / canResize / onSize are plug-in calls: SEH-guarded (see WM_SIZING).
				auto resize = [&] {
					ViewRect cur;
					if (inst->view->getSize (&cur) == kResultTrue && cur.getWidth () == vr.getWidth () &&
					    cur.getHeight () == vr.getHeight ())
						return;
					if (inst->view->canResize () == kResultTrue)
						inst->view->onSize (&vr);
				};
				if (!sehCall (resize))
				{
					markCrashed (inst);
					PostMessageW (hwnd, WM_CLOSE, 0, 0);
				}
			}
			break;
		// The plug-in paints the whole client area; erasing it first would only flicker.
		case WM_ERASEBKGND: return 1;
		// Close button / Alt+F4: destroy the window (the same thing DefWindowProc does,
		// written out to make the lifecycle explicit). The instance itself stays loaded.
		case WM_CLOSE: DestroyWindow (hwnd); return 0;
		// Release the view while the HWND (its parent) still exists, and mark the editor as
		// closed so vh_editor_hwnd returns 0 and vh_editor_open creates a new one next time.
		case WM_DESTROY:
			KillTimer (hwnd, kSyncTimerId); // harmless if it was never started
			if (inst)
			{
				destroyView (inst);
				inst->hwnd = nullptr;
			}
			break;
		// Last message of the window: forget the instance pointer.
		case WM_NCDESTROY: SetWindowLongPtrW (hwnd, GWLP_USERDATA, 0); break;
	}
	return DefWindowProcW (hwnd, msg, wp, lp);
}

/// The HMODULE of this DLL (not of the .exe), found from the address of one of its own
/// functions; used as the hInstance of the editor window class. The refcount is unchanged,
/// so nothing needs to be freed.
HINSTANCE thisModule ()
{
	HMODULE m = nullptr;
	GetModuleHandleExW (GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
	                    reinterpret_cast<LPCWSTR> (&editorProc), &m);
	return m;
}

/// Registers the editor window class once per process (UI thread only, so the plain static
/// flag needs no synchronisation). CS_DBLCLKS lets plug-in GUIs receive double clicks; the
/// black background only shows before the plug-in has painted.
void registerEditorClass ()
{
	static bool done = false;
	if (done)
		return;
	WNDCLASSEXW wc {sizeof (wc)};
	wc.style = CS_DBLCLKS;
	wc.lpfnWndProc = editorProc;
	wc.hInstance = thisModule ();
	wc.hCursor = LoadCursorW (nullptr, IDC_ARROW);
	wc.hbrBackground = (HBRUSH)GetStockObject (BLACK_BRUSH);
	wc.lpszClassName = kEditorClass;
	// Use the application's icon when there is one. 32512 (== IDI_APPLICATION's value) is the
	// resource id under which the .NET SDK's apphost embeds <ApplicationIcon> into the .exe;
	// GetModuleHandleW(nullptr) is the .exe (CsoundVisual.exe), not this DLL.
	wc.hIcon = LoadIconW (GetModuleHandleW (nullptr), MAKEINTRESOURCEW (32512));
	if (!wc.hIcon)
		wc.hIcon = LoadIconW (nullptr, IDI_APPLICATION);
	RegisterClassExW (&wc);
	done = true;
}

//------------------------------------------------------------------------------
// MIDI mapping table (IMidiMapping), built once after loading
//------------------------------------------------------------------------------
/// VST3 has no MIDI CC events: a plug-in that reacts to CCs, pitch bend or channel pressure
/// exposes them as parameters and tells the host which parameter each controller maps to
/// through IMidiMapping (implemented by the edit controller). This queries every
/// (channel, controller) pair once, on the UI thread, so that vh_midi can translate a CC with
/// a simple table lookup from any thread without calling into the plug-in.
/// The table is filled with -1 ("not mapped") even when the plug-in has no IMidiMapping.
/// Only event bus 0 is queried, the only one that is ever activated.
void buildMidiMap (VhInstance* inst)
{
	inst->midiMap.assign (16 * 130, -1);
	FUnknownPtr<IMidiMapping> mapping (inst->controller);
	if (!mapping)
		return;
	for (int ch = 0; ch < 16; ++ch)
		for (int cc = 0; cc < 130; ++cc)
		{
			ParamID id = 0;
			if (mapping->getMidiControllerAssignment (0, (int16)ch, (CtrlNumber)cc, id) == kResultTrue)
				inst->midiMap[ch * 130 + cc] = id;
		}
}

/// Process call under SEH (a crashing plug-in is disabled instead of killing the app).
/// Audio thread. The tresult of process() is ignored: a plug-in that returns an error for one
/// block simply produces whatever is in its output buffers.
/// @return false when the plug-in faulted.
bool safeProcess (IAudioProcessor* p, ProcessData& d)
{
	auto call = [&] { p->process (d); };
	return sehCall (call);
}

/// Class id as 32 hex digits (the format VST3::UID::fromString parses back in vh_load).
std::string uidText (const VST3::UID& id) { return id.toString (); }

} // namespace

//==============================================================================
// C API
//==============================================================================
// Parameter/return conventions are documented in vsthost.h; the comments below explain the
// implementation. No C++ exception may escape an extern "C" function called from .NET, so
// every function that can throw (std::string/vector allocation, SDK helpers) catches all.

/// See vsthost.h. Any thread. Version 2 added vh_take_restart_flags (backward compatible:
/// every version-1 export is unchanged).
VH_API int32_t vh_version () { return 2; }

/// See vsthost.h. Loads (or reuses) the module, lists its factory's classes as JSON and drops
/// its reference again, so a module that no instance uses is unloaded on return. UI thread.
VH_API int32_t vh_scan_module (const wchar_t* path, char* buf, int32_t cap)
{
	try
	{
		std::string error;
		auto module = loadModule (toUtf8 (path), error);
		// Error convention: -1 - length, so even an empty message yields a negative value.
		if (!module)
			return -1 - putString (error.empty () ? "cannot load module" : error, buf, cap);
		auto& factory = module->getFactory ();
		auto fi = factory.info ();
		std::string j = "{\"vendor\":\"" + jsonEscape (fi.vendor ()) + "\",\"url\":\"" + jsonEscape (fi.url ()) +
		                "\",\"classes\":[";
		bool first = true;
		for (auto& ci : factory.classInfos ())
		{
			if (!first)
				j += ",";
			first = false;
			j += "{\"name\":\"" + jsonEscape (ci.name ()) + "\",\"vendor\":\"" + jsonEscape (ci.vendor ()) +
			     "\",\"category\":\"" + jsonEscape (ci.category ()) + "\",\"subCategories\":\"" +
			     jsonEscape (ci.subCategoriesString ()) + "\",\"cid\":\"" + uidText (ci.ID ()) +
			     "\",\"version\":\"" + jsonEscape (ci.version ()) + "\",\"sdkVersion\":\"" +
			     jsonEscape (ci.sdkVersion ()) + "\"}";
		}
		j += "]}";
		return putString (j, buf, cap);
	}
	catch (...)
	{
		return -1 - putString ("exception while scanning", buf, cap);
	}
}

/// See vsthost.h. Builds a VhInstance step by step; on any failure the partially built
/// instance is destroyed by its unique_ptr (IPtr members release the plug-in objects).
/// UI thread.
VH_API VhInstance* vh_load (const wchar_t* path, const char* cid, char* err, int32_t errCap)
{
	// Writes the reason into err and returns null (the failure value of vh_load).
	auto fail = [&] (const std::string& msg) -> VhInstance* {
		putString (msg, err, errCap);
		return nullptr;
	};
	// Declared outside the try so the catch handlers can still see the partially built
	// instance. componentReady = the component's initialize() succeeded; from then on a
	// C++ exception (std::bad_alloc from a string/vector, an SDK helper...) must not just
	// release the plug-in objects: the VST3 lifecycle requires terminate() before release.
	// The catch handlers therefore hand such an instance to vh_free, which performs the
	// complete teardown (disconnect, detach handler, terminate controller and component,
	// release in order, unload the module last). Before, the unique_ptr simply released an
	// initialized component without terminate().
	std::unique_ptr<VhInstance> inst;
	bool componentReady = false;
	auto abandon = [&] {
		if (inst && componentReady)
			vh_free (inst.release ()); // full, SEH-guarded teardown (see vh_free)
		inst.reset ();                 // not initialized: plain release is correct
	};
	try
	{
		std::string error;
		auto module = loadModule (toUtf8 (path), error);
		if (!module)
			return fail (error.empty () ? "cannot load module" : error);
		auto uid = VST3::UID::fromString (std::string (cid ? cid : ""));
		if (!uid)
			return fail ("invalid class id");
		auto& factory = module->getFactory ();
		// IPluginFactory3::setHostContext: lets the factory itself know the host (some
		// plug-ins query it before any instance exists). Harmless to repeat per load.
		factory.setHostContext (static_cast<IHostApplication*> (&host ()));

		inst.reset (new VhInstance ());
		inst->module = module;
		// Copy the class info (for vh_info). An empty class vendor falls back to the
		// factory's vendor, as DAWs display it.
		for (auto& ci : factory.classInfos ())
			if (ci.ID () == *uid)
			{
				inst->name = ci.name ();
				inst->vendor = ci.vendor ().empty () ? factory.info ().vendor () : ci.vendor ();
				inst->category = ci.category ();
				inst->subCategories = ci.subCategoriesString ();
			}
		inst->cidText = uid->toString ();
		// Sub-categories are a '|'-separated list such as "Instrument|Synth" or "Fx|EQ".
		inst->instrument = inst->subCategories.find ("Instrument") != std::string::npos;

		// Lifecycle step 1: create the component and initialize it with the host context.
		// Both run plug-in code, so they go under SEH; `ok` carries the initialize result out
		// of the lambda.
		bool ok = true;
		auto create = [&] {
			inst->component = factory.createInstance<IComponent> (*uid);
			if (inst->component &&
			    inst->component->initialize (static_cast<IHostApplication*> (&host ())) != kResultOk)
				ok = false;
		};
		if (!sehCall (create))
			return fail ("the plug-in crashed while being created");
		if (!inst->component)
			return fail ("the factory could not create the component");
		if (!ok)
			return fail ("IComponent::initialize failed");
		componentReady = true; // see `abandon` above

		// FUnknownPtr<T> = queryInterface for T (null when not supported), returned as an
		// IPtr with its own reference. The processor is the same object as the component.
		// From here on the component is initialized, so failures must terminate() it before
		// it is released (the lifecycle's reverse step).
		inst->processor = FUnknownPtr<IAudioProcessor> (inst->component);
		if (!inst->processor)
		{
			inst->component->terminate ();
			return fail ("the component has no IAudioProcessor");
		}
		// The bridge only feeds 32-bit float buffers (vh_process_stereo takes float*);
		// 64-bit-only plug-ins are practically nonexistent.
		if (inst->processor->canProcessSampleSize (kSample32) != kResultTrue)
		{
			inst->component->terminate ();
			return fail ("the plug-in cannot process 32-bit float");
		}

		// Edit controller: same object (single component) or a separate class.
		// A single-component plug-in (SingleComponentEffect in the SDK) answers
		// queryInterface(IEditController) itself and must NOT be initialized/terminated a
		// second time as a controller. Otherwise the component names its controller class
		// and we create and initialize it from the same factory. A plug-in without any
		// controller still works (no parameters, no editor); a failing controller is
		// therefore not fatal.
		inst->controller = FUnknownPtr<IEditController> (inst->component);
		if (inst->controller)
			inst->singleComponent = true;
		else
		{
			TUID ctrlCid;
			if (inst->component->getControllerClassId (ctrlCid) == kResultTrue)
			{
				auto makeCtrl = [&] {
					inst->controller = factory.createInstance<IEditController> (VST3::UID (ctrlCid));
					if (inst->controller &&
					    inst->controller->initialize (static_cast<IHostApplication*> (&host ())) != kResultOk)
						inst->controller = nullptr;
				};
				sehCall (makeCtrl);
			}
		}

		if (inst->controller)
		{
			// owned(): adopt the reference that `new` created (refcount 1) instead of adding
			// another one, so the handler is freed when the last IPtr releases it.
			inst->handler = owned (new ComponentHandler (inst.get ()));
			inst->controller->setComponentHandler (inst->handler);
			if (!inst->singleComponent)
			{
				// Let processor and controller talk to each other (IMessage), then give the
				// controller the processor's current state.
				// The connection is made by the host in both directions (each side gets the
				// other's IConnectionPoint); it is optional, many plug-ins do not use it.
				// setComponentState makes the controller's parameter values match the
				// processor's defaults: the VST3 way to sync the two halves after creation.
				// The stream must be rewound because getState left its position at the end.
				inst->componentCP = FUnknownPtr<IConnectionPoint> (inst->component);
				inst->controllerCP = FUnknownPtr<IConnectionPoint> (inst->controller);
				if (inst->componentCP && inst->controllerCP)
				{
					inst->componentCP->connect (inst->controllerCP);
					inst->controllerCP->connect (inst->componentCP);
				}
				MemoryStream s;
				if (inst->component->getState (&s) == kResultTrue)
				{
					s.seek (0, IBStream::kIBSeekSet, nullptr);
					inst->controller->setComponentState (&s);
				}
			}
			buildMidiMap (inst.get ());
		}

		// Plug-ins with an event input accept notes. Instruments are recognised by their
		// sub-category, or as a fallback by "takes notes but has no audio input".
		inst->hasEventInput = inst->component->getBusCount (kEvent, kInput) > 0;
		if (!inst->instrument && inst->hasEventInput && inst->component->getBusCount (kAudio, kInput) == 0)
			inst->instrument = true; // a generator without the sub-category
		return inst.release ();
	}
	catch (const std::exception& e)
	{
		abandon ();
		return fail (std::string ("exception: ") + e.what ());
	}
	catch (...)
	{
		abandon ();
		return fail ("unknown exception");
	}
}

/// See vsthost.h. Lifecycle teardown, first half: setProcessing(false) then setActive(false)
/// (the reverse of vh_prepare). `prepared` is cleared FIRST so a vh_process_stereo that
/// starts afterwards returns 1 without touching the plug-in; the caller must still make sure
/// no call is in flight (VstChain does). UI thread.
VH_API void vh_unprepare (VhInstance* h)
{
	if (!h || !h->processor)
		return;
	h->prepared.store (false, std::memory_order_release);
	if (h->active)
	{
		auto stop = [&] {
			h->processor->setProcessing (false);
			h->component->setActive (false);
		};
		sehCall (stop);
		h->active = false;
	}
}

/// See vsthost.h. Full teardown in the reverse order of vh_load / vh_prepare / vh_editor_open:
/// close the editor (view removed while the controller is still alive), stop processing,
/// disconnect the connection points, detach our component handler, terminate the controller
/// (only when it is a separate object) and the component, release every interface, and
/// finally drop the module reference, which unloads the DLL when this was its last instance.
/// The order matters: the module's code must stay loaded until every object it created has
/// been released. UI thread.
VH_API void vh_free (VhInstance* h)
{
	if (!h)
		return;
	vh_editor_close (h);
	vh_unprepare (h);
	auto shutdown = [&] {
		if (h->componentCP && h->controllerCP)
		{
			h->componentCP->disconnect (h->controllerCP);
			h->controllerCP->disconnect (h->componentCP);
		}
		if (h->controller)
		{
			h->controller->setComponentHandler (nullptr);
			if (!h->singleComponent)
				h->controller->terminate ();
		}
		if (h->component)
			h->component->terminate ();
	};
	sehCall (shutdown);
	// The plug-in may still hold a reference to the handler (and call it); make sure it no
	// longer points at the instance that is about to be deleted.
	if (h->handler)
		h->handler->inst = nullptr;
	h->data.unprepare (); // frees the process buffers allocated by HostProcessData::prepare
	// Release every plug-in interface now, explicitly, while the module is still loaded.
	h->componentCP = nullptr;
	h->controllerCP = nullptr;
	h->controller = nullptr;
	h->processor = nullptr;
	h->component = nullptr;
	h->handler = nullptr;
	// Keep a local reference to the module so it outlives `delete h` (the instance and all its
	// remaining members are destroyed while the plug-in DLL is still loaded), then drop it
	// last.
	auto module = h->module;
	delete h;
	module = nullptr; // last reference -> ExitDll + FreeLibrary
}

/// See vsthost.h. Lifecycle setup: bus arrangements and activation -> setupProcessing ->
/// buffer allocation -> setActive(true) -> setProcessing(true). The VST3 spec only allows
/// setBusArrangements, activateBus and setupProcessing while the component is INACTIVE,
/// which is why it starts with vh_unprepare. `prepared` is published last (release store)
/// so the audio thread sees fully built buffers. UI thread.
VH_API int32_t vh_prepare (VhInstance* h, double sampleRate, int32_t maxBlock)
{
	if (!h || !h->processor || h->crashed)
		return -1;
	try
	{
		vh_unprepare (h);
		// Tiny blocks (Csound's ksmps can be 1) still prepare the plug-in for at least 32
		// frames; process() is then simply called with fewer frames.
		maxBlock = std::max (32, maxBlock);
		// Every plug-in call of the configuration phase (bus queries and arrangements, bus
		// activation, setupProcessing, and HostProcessData::prepare, which calls
		// getBusInfo) now runs under SEH like the rest of the plug-in calls: a plug-in that
		// faults here is marked crashed and -4 is returned, instead of the fault terminating
		// the app. The bus vectors are allocated OUTSIDE the guarded lambdas, so an
		// allocation failure stays a C++ exception (-6) and is not mistaken for a crash.
		int32 nIn = 0, nOut = 0;
		auto counts = [&] {
			nIn = std::max<int32> (0, h->component->getBusCount (kAudio, kInput));
			nOut = std::max<int32> (0, h->component->getBusCount (kAudio, kOutput));
		};
		if (!sehCall (counts))
		{
			markCrashed (h);
			return -4;
		}

		// Ask for stereo main buses, keep whatever the plug-in has for auxiliary ones.
		// (SpeakerArrangement is a bit mask of speakers; kStereo = L|R.) setBusArrangements
		// takes all buses at once, so the current arrangement of each bus is read first.
		std::vector<SpeakerArrangement> ins (nIn), outs (nOut);
		int32_t configResult = 0; // set by the lambda: -2 setupProcessing refused, -3 buffers
		auto configure = [&] {
		for (int32 i = 0; i < nIn; ++i)
			h->processor->getBusArrangement (kInput, i, ins[i]);
		for (int32 i = 0; i < nOut; ++i)
			h->processor->getBusArrangement (kOutput, i, outs[i]);
		if (nIn > 0)
			ins[0] = SpeakerArr::kStereo;
		if (nOut > 0)
			outs[0] = SpeakerArr::kStereo;
		if (h->processor->setBusArrangements (nIn ? ins.data () : nullptr, nIn, nOut ? outs.data () : nullptr,
		                                      nOut) != kResultTrue)
		{
			// Refused (e.g. a mono-only plug-in): use what it proposes instead.
			for (int32 i = 0; i < nIn; ++i)
				h->processor->getBusArrangement (kInput, i, ins[i]);
			for (int32 i = 0; i < nOut; ++i)
				h->processor->getBusArrangement (kOutput, i, outs[i]);
			h->processor->setBusArrangements (nIn ? ins.data () : nullptr, nIn, nOut ? outs.data () : nullptr, nOut);
		}
		// Whatever was finally accepted, read back the real channel counts of the main buses
		// (vh_process_stereo adapts to mono or >2 channels).
		SpeakerArrangement a = 0;
		h->inChannels = nIn > 0 && h->processor->getBusArrangement (kInput, 0, a) == kResultTrue
		                    ? SpeakerArr::getChannelCount (a)
		                    : 0;
		a = 0;
		h->outChannels = nOut > 0 && h->processor->getBusArrangement (kOutput, 0, a) == kResultTrue
		                     ? SpeakerArr::getChannelCount (a)
		                     : 0;

		// Only the main buses (and the first event input) are active.
		// Inactive auxiliary buses (side-chain inputs, extra outputs) tell the plug-in it need
		// not compute them; their buffers still exist in ProcessData and are fed silence.
		for (int32 i = 0; i < nIn; ++i)
			h->component->activateBus (kAudio, kInput, i, i == 0);
		for (int32 i = 0; i < nOut; ++i)
			h->component->activateBus (kAudio, kOutput, i, i == 0);
		int32 nEv = h->component->getBusCount (kEvent, kInput);
		for (int32 i = 0; i < nEv; ++i)
			h->component->activateBus (kEvent, kInput, i, i == 0);

		// ProcessSetup = {processMode, symbolicSampleSize, maxSamplesPerBlock, sampleRate}.
		// kRealtime tells the plug-in it runs live (no offline-quality modes, no blocking).
		ProcessSetup setup {kRealtime, kSample32, maxBlock, sampleRate};
		if (h->processor->setupProcessing (setup) != kResultTrue)
		{
			configResult = -2;
			return;
		}
		// HostProcessData::prepare reads the component's bus info and allocates one float
		// buffer of maxBlock samples per channel of every bus. This is the only place the
		// process buffers are allocated (UI thread, before `prepared` is set).
		if (!h->data.prepare (*h->component, maxBlock, kSample32))
			configResult = -3;
		}; // end of `configure` (body kept at its original indentation to keep the diff small)
		if (!sehCall (configure))
		{
			markCrashed (h);
			return -4;
		}
		if (configResult != 0)
			return configResult;
		// Hook our host-side queues and transport context into ProcessData once; the audio
		// thread fills/clears them in place each block.
		h->data.inputParameterChanges = &h->inParams;
		h->data.outputParameterChanges = &h->outParams;
		h->data.inputEvents = &h->events;
		h->data.processContext = &h->context;
		h->data.processMode = kRealtime;

		// Activation runs plug-in code that often allocates large internal buffers; a fault
		// here marks the plug-in as crashed for good (vh_prepare then always returns -1).
		bool ok = true;
		auto start = [&] {
			if (h->component->setActive (true) != kResultTrue)
				ok = false;
			else
				h->processor->setProcessing (true); // kNotImplemented is fine
		};
		if (!sehCall (start))
		{
			h->crashed = true;
			return -4;
		}
		if (!ok)
			return -5;
		h->active = true;
		h->sampleRate = sampleRate;
		h->maxBlock = maxBlock;
		h->position = 0; // the fake transport restarts at 0 on every prepare
		h->context = {};
		// Publish: from now on vh_process_stereo may run (acquire load on the audio thread
		// pairs with this release store, so every write above is visible to it).
		h->prepared.store (true, std::memory_order_release);
		return 0;
	}
	catch (...)
	{
		return -6;
	}
}

/// See vsthost.h. Latency is only meaningful while active (the spec allows the plug-in to
/// change it in setActive/setupProcessing). UI thread.
VH_API int32_t vh_info (VhInstance* h, char* buf, int32_t cap)
{
	if (!h)
		return -1 - putString ("null handle", buf, cap);
	drainToController (h); // UI-thread entry point: keep the controller in sync (see there)
	// getLatencySamples / getParameterCount are plug-in calls: SEH-guarded like the others
	// (a fault marks the instance crashed; the JSON then reports "crashed":true). A crashed
	// plug-in is not called at all.
	int32 latency = 0, params = 0;
	if (!h->crashed)
	{
		auto query = [&] {
			latency = h->processor && h->active ? (int32)h->processor->getLatencySamples () : 0;
			params = h->controller ? h->controller->getParameterCount () : 0;
		};
		if (!sehCall (query))
			markCrashed (h);
	}
	std::string j = "{\"name\":\"" + jsonEscape (h->name) + "\",\"vendor\":\"" + jsonEscape (h->vendor) +
	                "\",\"subCategories\":\"" + jsonEscape (h->subCategories) + "\",\"cid\":\"" + h->cidText +
	                "\",\"instrument\":" + (h->instrument ? "true" : "false") +
	                ",\"singleComponent\":" + (h->singleComponent ? "true" : "false") +
	                ",\"inputs\":" + std::to_string (h->inChannels) + ",\"outputs\":" +
	                std::to_string (h->outChannels) + ",\"latency\":" + std::to_string (latency) +
	                ",\"parameters\":" + std::to_string (params) + ",\"editor\":" +
	                (vh_has_editor (h) ? "true" : "false") + ",\"crashed\":" + (h->crashed ? "true" : "false") +
	                "}";
	return putString (j, buf, cap);
}

/// See vsthost.h. The real-time path. Per chunk of at most maxBlock frames:
///   1. copy L/R into input bus 0 (down-mixed for mono, extra channels zeroed), silence the
///      auxiliary input buses;
///   2. move queued parameter changes and note events into ProcessData (try-lock only);
///   3. fill the ProcessContext (fake transport: always playing, 120 BPM, 4/4);
///   4. call process() under SEH;
///   5. copy (effects) or add (instruments) output bus 0 back into L/R;
///   6. clear the per-block queues and advance the song position.
/// Never blocks, never allocates (all buffers and queues were sized in vh_prepare / the
/// constructor). Audio thread only.
VH_API int32_t vh_process_stereo (VhInstance* h, float* left, float* right, int32_t frames)
{
	if (!h || !left || !right || frames <= 0)
		return 1;
	if (h->crashed.load (std::memory_order_relaxed))
		return 2;
	// acquire: pairs with the release store at the end of vh_prepare.
	if (!h->prepared.load (std::memory_order_acquire))
		return 1;

	auto& d = h->data;
	int32 offset = 0;
	// The host block may be larger than the maxBlock promised to setupProcessing: split it.
	while (offset < frames)
	{
		int32 n = std::min (frames - offset, h->maxBlock);
		float* L = left + offset;
		float* R = right + offset;

		// input bus 0
		if (d.numInputs > 0 && d.inputs[0].numChannels > 0)
		{
			auto& bus = d.inputs[0];
			// silenceFlags = 0: "no channel is known to be silent" (a set bit would let the
			// plug-in skip that channel).
			bus.silenceFlags = 0;
			if (bus.numChannels == 1)
				for (int32 i = 0; i < n; ++i)
					bus.channelBuffers32[0][i] = 0.5f * (L[i] + R[i]);
			else
			{
				std::memcpy (bus.channelBuffers32[0], L, sizeof (float) * n);
				std::memcpy (bus.channelBuffers32[1], R, sizeof (float) * n);
				for (int32 c = 2; c < bus.numChannels; ++c)
					std::memset (bus.channelBuffers32[c], 0, sizeof (float) * n);
			}
		}
		// Auxiliary inputs (side-chains...) are inactive but still present: feed them silence
		// rather than whatever a plug-in may have left in the buffers.
		for (int32 b = 1; b < d.numInputs; ++b)
			for (int32 c = 0; c < d.inputs[b].numChannels; ++c)
				std::memset (d.inputs[b].channelBuffers32[c], 0, sizeof (float) * n);

		// parameter changes and events queued by other threads (skipped if contended)
		// The lock is held only for a vector swap (O(1), no allocation); the conversion into
		// VST3 queues happens after unlocking. A contended lock just delays the batch to the
		// next chunk/block.
		if (h->paramLock.tryLock ())
		{
			h->paramWork.swap (h->paramInbox);
			h->paramLock.unlock ();
			for (auto& pc : h->paramWork)
			{
				// One queue per parameter id (addParameterData returns the existing queue for a
				// repeated id); every change is a point at sample offset 0 of this block, so the
				// last one queued wins. Changes for more than 64 distinct ids in one block are
				// dropped (addParameterData returns null when the ParameterChanges is full).
				int32 qi = 0;
				if (auto* q = h->inParams.addParameterData (pc.id, qi))
					q->addPoint (0, pc.value, qi);
			}
			h->paramWork.clear ();
		}
		// Same pattern for note events. All events carry sampleOffset 0, i.e. they take effect
		// at the start of this chunk (MIDI timing is quantised to the host block). The
		// EventList holds at most 512 events; extra ones are dropped by addEvent.
		if (h->hasEventInput && h->eventLock.tryLock ())
		{
			h->eventWork.swap (h->eventInbox);
			h->eventLock.unlock ();
			for (auto& e : h->eventWork)
				h->events.addEvent (e);
			h->eventWork.clear ();
		}

		// Transport information for tempo-synced plug-ins (delays, LFOs, arpeggiators).
		// Csound Visual has no song timeline to share, so the host reports a steady transport:
		// always playing at 120 BPM in 4/4, with the position counted in samples since
		// vh_prepare. projectTimeMusic is in quarter notes (seconds * BPM / 60);
		// systemTime is in nanoseconds from QueryPerformanceCounter.
		auto& ctx = h->context;
		ctx.state = ProcessContext::kPlaying | ProcessContext::kTempoValid | ProcessContext::kTimeSigValid |
		            ProcessContext::kContTimeValid | ProcessContext::kProjectTimeMusicValid |
		            ProcessContext::kSystemTimeValid;
		ctx.sampleRate = h->sampleRate;
		ctx.projectTimeSamples = h->position;
		ctx.continousTimeSamples = h->position;
		ctx.tempo = 120;
		ctx.timeSigNumerator = 4;
		ctx.timeSigDenominator = 4;
		ctx.projectTimeMusic = (double)h->position / h->sampleRate * ctx.tempo / 60.0;
		LARGE_INTEGER qpc, qpf;
		QueryPerformanceCounter (&qpc);
		QueryPerformanceFrequency (&qpf);
		ctx.systemTime = (int64)((double)qpc.QuadPart * 1e9 / (double)qpf.QuadPart);

		d.numSamples = n;
		d.symbolicSampleSize = kSample32;
		if (!safeProcess (h->processor, d))
		{
			// The plug-in faulted: its internal state is unknown, so it is never called from
			// the audio thread again (later calls return 2 at once). The caller (VstChain) marks
			// the slot as crashed so the UI can say so.
			// The audio of this chunk is left as it was.
			h->crashed = true;
			h->prepared = false;
			return 2;
		}

		// output bus 0
		// Instruments add their output to what is already in L/R (so Csound's sound and the
		// synth's are mixed); effects replace it. A mono output feeds both channels; output
		// channels beyond 2 are ignored.
		if (d.numOutputs > 0 && d.outputs[0].numChannels > 0)
		{
			auto& bus = d.outputs[0];
			float* o0 = bus.channelBuffers32[0];
			float* o1 = bus.numChannels > 1 ? bus.channelBuffers32[1] : o0;
			if (h->instrument)
				for (int32 i = 0; i < n; ++i)
				{
					L[i] += o0[i];
					R[i] += o1[i];
				}
			else
			{
				std::memcpy (L, o0, sizeof (float) * n);
				std::memcpy (R, o1, sizeof (float) * n);
			}
		}

		// Parameter changes the processor reported in outParams (its own automation, meters,
		// values it changed in reaction to MIDI...) must reach the edit controller too, or the
		// editor, vh_param_get and the saved controller state drift from what the processor
		// uses. The controller is a UI-thread object, so only the LAST point of each queue (the
		// value at the end of this chunk) is pushed into the lock-free ctrlFromAudio ring; the
		// UI thread applies it (drainToController). These are the SDK's host-side
		// ParameterChanges objects (our code, not the plug-in's): no lock, no allocation, no
		// plug-in call. A full ring drops the change (a later one supersedes it).
		if (h->controller)
		{
			const int32 nq = h->outParams.getParameterCount ();
			for (int32 q = 0; q < nq; ++q)
			{
				IParamValueQueue* queue = h->outParams.getParameterData (q);
				if (!queue)
					continue;
				const int32 np = queue->getPointCount ();
				int32 off = 0;
				ParamValue v = 0;
				if (np > 0 && queue->getPoint (np - 1, off, v) == kResultTrue)
					h->ctrlFromAudio.push ({queue->getParameterId (), v});
			}
		}
		// Reset the per-block queues (no deallocation).
		h->events.clear ();
		h->inParams.clearQueue ();
		h->outParams.clearQueue ();
		h->position += n;
		offset += n;
	}
	return 0;
}

/// See vsthost.h. Translates one MIDI 1.0 channel message and queues it; never calls into the
/// plug-in, so it is safe from the MIDI-input thread. midiMap is read-only after vh_load.
/// Any thread.
VH_API void vh_midi (VhInstance* h, uint8_t status, uint8_t d1, uint8_t d2)
{
	if (!h)
		return;
	int type = status & 0xF0, ch = status & 0x0F; // high nibble = message type, low = channel 0..15
	if (type == 0x90 || type == 0x80)
	{
		// Note events only make sense for plug-ins with an event input bus.
		if (!h->hasEventInput)
			return;
		Event e {};
		e.busIndex = 0;     // the only active event bus
		e.sampleOffset = 0; // start of the next block
		// MIDI convention: note on with velocity 0 is a note off. VST3 velocities are 0..1.
		// noteId -1 = "no id": the plug-in matches note offs to note ons by pitch+channel.
		if (type == 0x90 && d2 > 0)
		{
			e.type = Event::kNoteOnEvent;
			e.noteOn.channel = (int16)ch;
			e.noteOn.pitch = (int16)(d1 & 0x7F);
			e.noteOn.velocity = (d2 & 0x7F) / 127.f;
			e.noteOn.noteId = -1;
		}
		else
		{
			e.type = Event::kNoteOffEvent;
			e.noteOff.channel = (int16)ch;
			e.noteOff.pitch = (int16)(d1 & 0x7F);
			e.noteOff.velocity = (d2 & 0x7F) / 127.f;
			e.noteOff.noteId = -1;
		}
		h->pushEvent (e);
		return;
	}
	// Everything else goes through the IMidiMapping table (empty when there is no controller).
	// The value is converted to a normalised 0..1 parameter value:
	//   CC (0xBn)               : data2 0..127            -> /127
	//   pitch bend (0xEn)       : 14-bit LSB d1, MSB d2   -> /16383 (centre 8192 = ~0.5)
	//   channel pressure (0xDn) : data1 0..127            -> /127
	if (h->midiMap.empty ())
		return;
	int ctrl = -1;
	double value = 0;
	if (type == 0xB0)
	{
		ctrl = d1 & 0x7F;
		value = (d2 & 0x7F) / 127.0;
	}
	else if (type == 0xE0)
	{
		ctrl = kPitchBend;
		value = (((d2 & 0x7F) << 7) | (d1 & 0x7F)) / 16383.0;
	}
	else if (type == 0xD0)
	{
		ctrl = kAfterTouch;
		value = (d1 & 0x7F) / 127.0;
	}
	if (ctrl < 0 || ctrl >= 130)
		return;
	int64 id = h->midiMap[ch * 130 + ctrl];
	// The processor gets the change in its next block (pushParam). The controller is a
	// UI-thread object, so its copy is queued (pushCtrlFromMidi) and applied on the UI thread
	// by drainToController; before, only the processor received it, so an open editor, a
	// later vh_param_get and the saved controller state did not follow CC-driven moves.
	if (id >= 0)
	{
		h->pushParam ((ParamID)id, value);
		if (h->controller)
			h->pushCtrlFromMidi ((ParamID)id, value);
	}
}

/// See vsthost.h. UI thread.
VH_API int32_t vh_param_count (VhInstance* h)
{
	if (!h || !h->controller)
		return 0;
	drainToController (h); // UI-thread entry point: apply pending processor/MIDI changes first
	return h->controller->getParameterCount ();
}

/// See vsthost.h. Copies the plug-in's ParameterInfo field by field; the title is a String128
/// (UTF-16, at most 128 units), copied unit-for-unit into the caller's wchar_t buffer.
/// UI thread.
VH_API int32_t vh_param_info (VhInstance* h, int32_t index, uint32_t* id, wchar_t* title, int32_t titleCap,
                              int32_t* flags, int32_t* stepCount, double* defaultValue)
{
	if (!h || !h->controller)
		return -1;
	ParameterInfo info {};
	if (h->controller->getParameterInfo (index, info) != kResultTrue)
		return -2;
	if (id)
		*id = info.id;
	if (flags)
		*flags = info.flags;
	if (stepCount)
		*stepCount = info.stepCount;
	if (defaultValue)
		*defaultValue = info.defaultNormalizedValue;
	if (title && titleCap > 0)
	{
		int i = 0;
		for (; i < titleCap - 1 && i < 128 && info.title[i]; ++i)
			title[i] = (wchar_t)info.title[i];
		title[i] = 0;
	}
	return 0;
}

/// See vsthost.h. Reads the controller's copy of the value (what the editor shows). UI thread.
VH_API double vh_param_get (VhInstance* h, uint32_t id)
{
	if (!h || !h->controller)
		return 0.0;
	// Apply pending processor-reported / MIDI-driven changes first, so the value returned is
	// the one the processor is actually using (see drainToController).
	drainToController (h);
	return h->controller->getParamNormalized (id);
}

/// See vsthost.h. A host-initiated change must reach BOTH halves of a VST3 plug-in: the
/// controller (setParamNormalized, so the editor and vh_param_get reflect it) and the
/// processor (queued, delivered in the next process() call). The controller does not forward
/// host-set values to the processor by itself. UI thread.
VH_API void vh_param_set (VhInstance* h, uint32_t id, double value)
{
	if (!h)
		return;
	value = std::clamp (value, 0.0, 1.0); // normalised values are defined only in 0..1
	if (h->controller)
		h->controller->setParamNormalized (id, value);
	h->pushParam (id, value);
}

/// See vsthost.h. Asks the controller to format a normalised value with its own units
/// (getParamStringByValue); the value need not be the current one. UI thread.
VH_API int32_t vh_param_text (VhInstance* h, uint32_t id, double value, wchar_t* buf, int32_t cap)
{
	if (!h || !h->controller || !buf || cap <= 0)
		return -1;
	String128 s {};
	if (h->controller->getParamStringByValue (id, value, s) != kResultTrue)
		return -2;
	int i = 0;
	for (; i < cap - 1 && i < 128 && s[i]; ++i)
		buf[i] = (wchar_t)s[i];
	buf[i] = 0;
	return 0;
}

// State blob: "CVS1" | u32 component size | component bytes | u32 controller size | controller bytes
// (sizes in the machine's byte order, i.e. little-endian on x64). The blob is opaque to the
// app, which stores it base64-encoded in its settings to restore the chain at the next start.
/// See vsthost.h. Asks the plug-in to write its state into two in-memory IBStreams
/// (MemoryStream grows as needed) and concatenates them. The state is produced again on
/// every call, so the "query size, then fetch" pattern serialises twice; the second call
/// writes the blob only when cap is large enough. A component or controller that fails
/// getState contributes an empty section rather than failing the whole call.
/// For single-component plug-ins the component state is the whole state (the controller
/// section is empty). UI thread.
VH_API int32_t vh_get_state (VhInstance* h, uint8_t* buf, int32_t cap)
{
	if (!h || !h->component)
		return -1;
	try
	{
		// The controller's state should include the latest processor/MIDI-driven values.
		drainToController (h);
		MemoryStream comp, ctrl;
		if (h->component->getState (&comp) != kResultTrue)
			comp.setSize (0); // discard whatever was partially written
		if (h->controller && !h->singleComponent)
			if (h->controller->getState (&ctrl) != kResultTrue)
				ctrl.setSize (0);
		uint32 cs = (uint32)comp.getSize (), ks = (uint32)ctrl.getSize ();
		// magic + size + component bytes + size + controller bytes, computed in 64 bits; the
		// C API returns an int32, hence the 2 GB limit.
		int64 total = 4 + 4 + (int64)cs + 4 + (int64)ks;
		if (total > 0x7FFFFFFF)
			return -2;
		if (buf && cap >= total)
		{
			uint8_t* p = buf;
			std::memcpy (p, "CVS1", 4);
			p += 4;
			std::memcpy (p, &cs, 4);
			p += 4;
			if (cs)
				std::memcpy (p, comp.getData (), cs);
			p += cs;
			std::memcpy (p, &ks, 4);
			p += 4;
			if (ks)
				std::memcpy (p, ctrl.getData (), ks);
		}
		return (int32_t)total;
	}
	catch (...)
	{
		return -3;
	}
}

/// See vsthost.h. Parses the CVS1 blob with bounds checks (the blob comes from a settings
/// file and may be truncated or corrupt), then restores in the order the VST3 spec asks of a
/// host loading a preset/project:
///   1. IComponent::setState(component bytes)            - the processor's state;
///   2. IEditController::setComponentState(same bytes)   - lets a separate controller update
///                                                         its parameter values to match;
///   3. IEditController::setState(controller bytes)      - controller-only data (GUI size,
///                                                         editor page...).
/// Steps 2-3 are skipped for single-component plug-ins (step 1 covers everything).
/// The controller's results are ignored: a controller that cannot read its own data only
/// loses cosmetic state. Normally called while the instance is not processing (after vh_load,
/// before vh_prepare). UI thread.
VH_API int32_t vh_set_state (VhInstance* h, const uint8_t* buf, int32_t len)
{
	// 12 bytes = magic + two sizes, the smallest valid blob.
	if (!h || !h->component || !buf || len < 12 || std::memcmp (buf, "CVS1", 4) != 0)
		return -1;
	try
	{
		// memcpy instead of a cast: the blob gives no alignment guarantee. The int64 casts
		// keep the bounds checks from overflowing with hostile sizes.
		uint32 cs = 0, ks = 0;
		std::memcpy (&cs, buf + 4, 4);
		if ((int64)8 + cs + 4 > len)
			return -2;
		std::memcpy (&ks, buf + 8 + cs, 4);
		if ((int64)12 + cs + ks > len)
			return -2;
		const uint8_t* compData = buf + 8;
		const uint8_t* ctrlData = buf + 12 + cs;
		if (cs)
		{
			// Copy into a MemoryStream (the plug-in reads through IBStream) and rewind it
			// before each reader, since writing left the position at the end.
			MemoryStream s;
			s.write ((void*)compData, (int32)cs, nullptr);
			s.seek (0, IBStream::kIBSeekSet, nullptr);
			if (h->component->setState (&s) != kResultTrue)
				return -3;
			if (h->controller && !h->singleComponent)
			{
				s.seek (0, IBStream::kIBSeekSet, nullptr);
				h->controller->setComponentState (&s);
			}
		}
		if (ks && h->controller && !h->singleComponent)
		{
			MemoryStream s;
			s.write ((void*)ctrlData, (int32)ks, nullptr);
			s.seek (0, IBStream::kIBSeekSet, nullptr);
			h->controller->setState (&s);
		}
		return 0;
	}
	catch (...)
	{
		return -4;
	}
}

/// See vsthost.h. UI thread (views must be created on the UI thread).
VH_API int32_t vh_has_editor (VhInstance* h)
{
	if (!h || !h->controller)
		return 0;
	if (h->view)
		return 1; // an editor is open right now
	// Creating a view is the only reliable test; release it right away.
	// (VST3 has no "has editor" flag; createView returns null when there is none.) owned()
	// adopts the reference createView returned, so the IPtr's destructor releases the view
	// without it ever having been attached.
	int32_t result = 0;
	auto probe = [&] {
		IPtr<IPlugView> v = owned (h->controller->createView (ViewType::kEditor));
		result = v && v->isPlatformTypeSupported (kPlatformTypeHWND) == kResultTrue ? 1 : 0;
	};
	sehCall (probe);
	return result;
}

/// See vsthost.h. Editor lifecycle (IPlugView):
///   createView("editor") -> isPlatformTypeSupported(HWND) -> [setContentScaleFactor]
///   -> getSize -> create our top-level window with that client size -> setFrame(our frame)
///   -> attached(hwnd) -> [getSize again, resize] -> show.
/// Teardown (destroyView, from WM_DESTROY): removed() -> setFrame(nullptr) -> release.
/// The plug-in creates its own child window inside our window during attached().
/// UI thread (the window belongs to the calling thread, which must pump messages).
VH_API intptr_t vh_editor_open (VhInstance* h, intptr_t owner, const wchar_t* title)
{
	if (!h || !h->controller)
		return 0;
	// Already open: just bring it back (restore if minimised) instead of opening a second one.
	if (h->hwnd)
	{
		ShowWindow (h->hwnd, IsIconic (h->hwnd) ? SW_RESTORE : SW_SHOW);
		SetForegroundWindow (h->hwnd);
		return (intptr_t)h->hwnd;
	}
	registerEditorClass ();

	if (h->crashed)
		return 0; // never open the editor of a plug-in that already faulted
	IPtr<IPlugView> view;
	auto make = [&] { view = owned (h->controller->createView (ViewType::kEditor)); };
	if (!sehCall (make))
	{
		markCrashed (h);
		return 0;
	}
	if (!view)
		return 0;

	// Scale factor for plug-ins that support it (the window's monitor DPI).
	// 96 DPI = 100 %. Sizes exchanged with the view (ViewRect) are physical pixels; plug-ins
	// that implement IPlugViewContentScaleSupport scale their GUI by this factor, others draw
	// at their native size. The scale is not updated if the window later moves to a monitor
	// with a different DPI.
	UINT dpi = owner ? GetDpiForWindow ((HWND)owner) : GetDpiForSystem ();
	if (dpi == 0)
		dpi = 96;

	// Every plug-in call before the window exists (isPlatformTypeSupported,
	// setContentScaleFactor, getSize, canResize) is SEH-guarded as one step, like createView
	// and attached(): before, only those two were guarded, so a fault in, say, getSize took
	// the app down. A fault marks the instance crashed; the view is then released unattached.
	bool hwndSupported = false, resizable = false;
	ViewRect r {0, 0, 640, 400};
	auto query = [&] {
		hwndSupported = view->isPlatformTypeSupported (kPlatformTypeHWND) == kResultTrue;
		if (!hwndSupported)
			return;
		if (FUnknownPtr<IPlugViewContentScaleSupport> scale (view); scale)
			scale->setContentScaleFactor ((float)dpi / 96.f);
		// Initial client size from the view; 640x400 when it does not say (or says nonsense).
		if (view->getSize (&r) != kResultTrue || r.getWidth () <= 0 || r.getHeight () <= 0)
			r = {0, 0, 640, 400};
		resizable = view->canResize () == kResultTrue;
	};
	if (!sehCall (query))
	{
		markCrashed (h);
		return 0;
	}
	if (!hwndSupported)
		return 0;

	// A normal captioned window with close/minimise; resizable (thick frame + maximise) only
	// when the view can resize. WS_CLIPCHILDREN: our window never paints over the plug-in's
	// child window. AdjustWindowRectEx turns the client size into the outer window size.
	DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_CLIPCHILDREN;
	if (resizable)
		style |= WS_THICKFRAME | WS_MAXIMIZEBOX;
	DWORD ex = 0;
	RECT rc {0, 0, r.getWidth (), r.getHeight ()};
	AdjustWindowRectEx (&rc, style, FALSE, ex);

	// Place it 80 px right/down from the owner's top-left corner, or let Windows choose
	// (CW_USEDEFAULT) without an owner. Note: the position is not clamped to the screen, and
	// several editors opened from the same owner start at the same spot.
	int x = CW_USEDEFAULT, y = CW_USEDEFAULT;
	if (owner)
	{
		RECT orc;
		if (GetWindowRect ((HWND)owner, &orc))
		{
			x = orc.left + 80;
			y = orc.top + 80;
		}
	}
	// Created hidden (no WS_VISIBLE). Passing a non-null hWndParent to a non-child window
	// makes it OWNED: always above the owner, minimised and destroyed with it. The instance
	// goes in lpCreateParams so editorProc can store it at WM_NCCREATE. h->view is still null
	// during creation, so the WM_SIZE sent by CreateWindowExW does not reach the plug-in.
	HWND hwnd = CreateWindowExW (ex, kEditorClass, title ? title : L"VST3", style, x, y, rc.right - rc.left,
	                             rc.bottom - rc.top, (HWND)owner, nullptr, thisModule (), h);
	if (!hwnd)
		return 0;
	h->hwnd = hwnd;
	h->view = view;
	// The frame must be set BEFORE attached(), because many plug-ins call resizeView from
	// inside attached().
	h->frame = owned (new EditorFrame (h));

	// setFrame and attached() (where the plug-in builds its GUI as a child of hwnd) run
	// together under SEH. On failure (fault or refusal) the frame is detached again - also
	// under SEH, the plug-in may fault there too - and the window destroyed; removed() is
	// not called on a view that was never attached.
	tresult attached = kResultFalse;
	auto attach = [&] {
		view->setFrame (h->frame);
		attached = view->attached (hwnd, kPlatformTypeHWND);
	};
	const bool attachOk = sehCall (attach);
	if (!attachOk || attached != kResultTrue)
	{
		if (!attachOk)
			markCrashed (h);
		h->view = nullptr; // do not call removed() on a view that was never attached
		auto detach = [&] { view->setFrame (nullptr); };
		sehCall (detach);
		h->frame = nullptr;
		DestroyWindow (hwnd);
		h->hwnd = nullptr;
		return 0;
	}

	// Some editors only know their final size once attached.
	// (e.g. after applying the content scale factor). Resize the window to match; inResize
	// keeps the resulting WM_SIZE from echoing the size back to the view. getSize is a plug-in
	// call (SEH-guarded); if it faults the editor is closed again and the plug-in disabled.
	ViewRect r2;
	tresult sizeResult = kResultFalse;
	auto getSize2 = [&] { sizeResult = view->getSize (&r2); };
	if (!sehCall (getSize2))
	{
		markCrashed (h);
		DestroyWindow (hwnd); // WM_DESTROY removes the attached view (destroyView, SEH-guarded)
		return 0;
	}
	if (sizeResult == kResultTrue && (r2.getWidth () != r.getWidth () || r2.getHeight () != r.getHeight ()) &&
	    r2.getWidth () > 0 && r2.getHeight () > 0)
	{
		RECT rc2 {0, 0, r2.getWidth (), r2.getHeight ()};
		AdjustWindowRectEx (&rc2, style, FALSE, ex);
		h->inResize = true;
		SetWindowPos (hwnd, nullptr, 0, 0, rc2.right - rc2.left, rc2.bottom - rc2.top,
		              SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
		h->inResize = false;
	}
	// While the editor is open, a 30 ms timer mirrors processor-reported and MIDI-driven
	// parameter changes into the controller so the GUI follows them (see editorProc
	// WM_TIMER / drainToController). Killed in WM_DESTROY.
	SetTimer (hwnd, kSyncTimerId, kSyncTimerMs, nullptr);
	ShowWindow (hwnd, SW_SHOWNORMAL);
	UpdateWindow (hwnd);
	return (intptr_t)hwnd;
}

/// See vsthost.h. DestroyWindow is synchronous: when it returns, WM_DESTROY has run, the view
/// has been removed and released, and h->hwnd is null. UI thread.
VH_API void vh_editor_close (VhInstance* h)
{
	if (h && h->hwnd)
		DestroyWindow (h->hwnd); // WM_DESTROY releases the view
}

/// See vsthost.h. h->hwnd is cleared in WM_DESTROY; IsWindow is an extra safety check in case
/// the window was destroyed without our window procedure seeing it. UI thread.
VH_API intptr_t vh_editor_hwnd (VhInstance* h)
{
	return h && h->hwnd && IsWindow (h->hwnd) ? (intptr_t)h->hwnd : 0;
}

/// See vsthost.h. Returns and clears (atomic exchange) the restartComponent flags the plug-in
/// raised since the previous call. Also applies pending processor/MIDI parameter changes to
/// the controller, so an app that polls this from a UI timer keeps the controller in sync
/// even without an open editor. UI thread.
VH_API int32_t vh_take_restart_flags (VhInstance* h)
{
	if (!h)
		return 0;
	drainToController (h);
	return h->restartFlags.exchange (0, std::memory_order_relaxed);
}

/// See vsthost.h. A minimal message loop for console tests: dispatch everything queued, then
/// sleep until new input arrives or 10 ms pass (MsgWaitForMultipleObjects with no handles),
/// until `ms` milliseconds have elapsed. Only messages of the calling thread are dispatched.
VH_API void vh_pump (int32_t ms)
{
	ULONGLONG end = GetTickCount64 () + (ULONGLONG)std::max (0, ms);
	do
	{
		MSG msg;
		while (PeekMessageW (&msg, nullptr, 0, 0, PM_REMOVE))
		{
			TranslateMessage (&msg);
			DispatchMessageW (&msg);
		}
		if (GetTickCount64 () >= end)
			break;
		MsgWaitForMultipleObjects (0, nullptr, FALSE, 10, QS_ALLINPUT);
	} while (true);
}

/// See vsthost.h. Test helper: renders the whole window (frame included) into a 32-bit
/// top-down DIB section, counts pixels that are not (almost) black, and writes them as a
/// 24-bit bottom-up BMP. PW_RENDERFULLCONTENT (Windows 8.1+) also captures GPU-drawn content
/// (Direct2D/OpenGL editors), which plain PrintWindow returns as black. Any thread that may
/// send messages to the window (PrintWindow sends WM_PRINT); used from the UI thread.
VH_API int32_t vh_capture_window (intptr_t hwndValue, const wchar_t* bmpPath)
{
	HWND hwnd = (HWND)hwndValue;
	if (!hwnd || !IsWindow (hwnd) || !bmpPath)
		return -1;
	RECT rc;
	GetWindowRect (hwnd, &rc);
	int w = rc.right - rc.left, h = rc.bottom - rc.top;
	if (w <= 0 || h <= 0)
		return -2;
	// Off-screen memory DC with a DIB section, so the pixels can be read directly via `bits`
	// (rows top-down because of the negative height; 4 bytes per pixel, B,G,R,X).
	HDC screen = GetDC (nullptr);
	HDC mem = CreateCompatibleDC (screen);
	BITMAPINFO bi {};
	bi.bmiHeader.biSize = sizeof (BITMAPINFOHEADER);
	bi.bmiHeader.biWidth = w;
	bi.bmiHeader.biHeight = -h; // top-down
	bi.bmiHeader.biPlanes = 1;
	bi.bmiHeader.biBitCount = 32;
	bi.bmiHeader.biCompression = BI_RGB;
	void* bits = nullptr;
	HBITMAP bmp = mem ? CreateDIBSection (screen, &bi, DIB_RGB_COLORS, &bits, nullptr, 0) : nullptr;
	// CreateDIBSection fails (null, bits null) for a huge window or when GDI is out of
	// resources; before, `bits` was then dereferenced below. Clean up and report -4.
	if (!bmp || !bits)
	{
		if (bmp)
			DeleteObject (bmp);
		if (mem)
			DeleteDC (mem);
		if (screen)
			ReleaseDC (nullptr, screen);
		return -4;
	}
	HGDIOBJ old = SelectObject (mem, bmp);
	PrintWindow (hwnd, mem, 2 /* PW_RENDERFULLCONTENT */);
	GdiFlush (); // make sure GDI has finished writing into `bits` before reading them

	// "Non-black" = any of B, G, R above 8, tolerating near-black noise. The editor class
	// background is black, so an editor that never painted scores little more than the
	// pixels of the window frame (caption and borders are captured too).
	int32_t nonBlack = 0;
	auto* px = static_cast<const uint8_t*> (bits);
	for (int i = 0; i < w * h; ++i)
		if (px[i * 4] > 8 || px[i * 4 + 1] > 8 || px[i * 4 + 2] > 8)
			++nonBlack;

	// 24-bit bottom-up BMP
	// File layout: BITMAPFILEHEADER ("BM" = 0x4D42, total size, pixel offset) +
	// BITMAPINFOHEADER + rows. Each row is 3 bytes per pixel padded to a multiple of 4, and
	// rows are stored bottom row first (positive height), hence the reversed y loop below.
	int rowBytes = (w * 3 + 3) & ~3;
	BITMAPFILEHEADER fh {};
	BITMAPINFOHEADER ih {};
	ih.biSize = sizeof ih;
	ih.biWidth = w;
	ih.biHeight = h;
	ih.biPlanes = 1;
	ih.biBitCount = 24;
	ih.biCompression = BI_RGB;
	ih.biSizeImage = rowBytes * h;
	fh.bfType = 0x4D42;
	fh.bfOffBits = sizeof fh + sizeof ih;
	fh.bfSize = fh.bfOffBits + ih.biSizeImage;
	FILE* f = nullptr;
	int32_t result = nonBlack;
	if (_wfopen_s (&f, bmpPath, L"wb") == 0 && f)
	{
		fwrite (&fh, sizeof fh, 1, f);
		fwrite (&ih, sizeof ih, 1, f);
		std::vector<uint8_t> row (rowBytes, 0);
		for (int y = h - 1; y >= 0; --y)
		{
			for (int x = 0; x < w; ++x)
			{
				const uint8_t* s = px + ((size_t)y * w + x) * 4;
				row[x * 3] = s[0];
				row[x * 3 + 1] = s[1];
				row[x * 3 + 2] = s[2];
			}
			fwrite (row.data (), 1, rowBytes, f);
		}
		fclose (f);
	}
	else
		result = -3;
	SelectObject (mem, old);
	DeleteObject (bmp);
	DeleteDC (mem);
	ReleaseDC (nullptr, screen);
	return result;
}
