//------------------------------------------------------------------------------
// vsthost_test - automated console test for CsoundVisual.vst3
//
// Loads the plug-in bundle through the VST3 SDK hosting helpers and checks:
//   1. Instrument at 48 kHz / 512: note-on -> ~2 s of non-silent, finite audio
//   2. Instrument with an odd block size (333)
//   3. FX with a 220 Hz sine input -> non-silent, finite, different from the input
//   4. Parameters are renamed to the orchestra's channels
//   5. getState / setState round trip into a fresh instance
//   6. IPC: descriptor file, "status", "load" (while audio runs on another thread),
//      "set", sr override, HOST_* channels, descriptor removal on terminate
//
// Usage: vsthost_test <path to CsoundVisual.vst3>   (exit code 0 = all passed)
//        vsthost_test <path to CsoundVisual.vst3> --serve <seconds>
//            keeps one Instrument instance alive (pipe + descriptor) for manual testing of
//            the Csound Visual app against a live plug-in without a DAW.
//
// Role: built by src/CsoundVST/CMakeLists.txt (target vsthost_test) and run by
// tools/build_vst.ps1 after the Steinberg validator. It acts as a minimal VST3 host:
// no GUI, no audio device; audio is rendered by calling process() directly in a loop
// (faster than real time, except in --serve mode), and the output is only analysed.
//
// Host lifecycle exercised for every instance (the order a real DAW uses):
//   IPluginFactory::createInstance -> IComponent::initialize(hostContext)
//   -> IAudioProcessor::setupProcessing(rate, max block) -> IComponent::setActive(true)
//   -> IAudioProcessor::setProcessing(true) -> process() ... -> setProcessing(false)
//   -> setActive(false) -> terminate()
// The plug-in is a SingleComponentEffect, so the IAudioProcessor and IEditController
// are obtained from the same object with queryInterface (no separate controller class,
// no IConnectionPoint).
//
// Threading: the test's main thread plays the role of both the host UI thread and the
// audio thread, except in the IPC section where a second std::thread renders audio
// while the main thread talks to the pipe. The plug-in posts parameter renames to a
// hidden window owned by the thread that called initialize(), so the test pumps the
// Win32 message queue (pumpMessages) wherever the host's UI thread would have run.
//
// IPC framing (see source/ipcserver.h): one request per connection on the named pipe
// named in the descriptor file; each message = 4-byte little-endian length + UTF-8 JSON.
//
// Side effects: each instance creates (in initialize) and deletes (in terminate) a descriptor file in
// %LOCALAPPDATA%\CsoundVisual\vst-instances (named <pid>.<n>.json); no sound is played.
//------------------------------------------------------------------------------
#include "minijson.h"

#include "public.sdk/source/common/memorystream.h"
#include "public.sdk/source/vst/hosting/eventlist.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivstcomponent.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "pluginterfaces/vst/ivstmidicontrollers.h"

#include <windows.h>
#include <shlobj.h>

#include <atomic>
#include <chrono>
#include <clocale> // std::setlocale (minijson locale-independence check)
#include <cmath>
#include <cstdio>
#include <string>
#include <thread>
#include <vector>

using namespace Steinberg;
using namespace Steinberg::Vst;
using CsoundVisual::Json::Value;

/// Number of failed CHECKs so far; main() returns 1 when it is non-zero.
static int gFailures = 0;
/// Prints "[PASS] <message>" or "[FAIL] <message>" and counts failures. The message is a
/// printf format string plus optional arguments (it must be a string literal, because it is
/// concatenated with the prefix at compile time). A failed check does not abort the run, so
/// one execution reports every problem; later checks may then cascade.
#define CHECK(cond, ...)                                                                         \
	do                                                                                           \
	{                                                                                            \
		if (cond)                                                                                \
			std::printf ("  [PASS] " __VA_ARGS__);                                               \
		else                                                                                     \
		{                                                                                        \
			std::printf ("  [FAIL] " __VA_ARGS__);                                               \
			++gFailures;                                                                         \
		}                                                                                        \
		std::printf ("\n");                                                                      \
	} while (0)

/// The host context passed to IComponent::initialize() (SDK helper implementing
/// IHostApplication: getName() and the IMessage/IAttributeList factory).
static HostApplication gHost;

/// Dispatches all pending Win32 messages of this thread without blocking.
/// The plug-in marshals parameter renames, restartComponent() and performEdit() to a hidden
/// message-only window created on the thread that called initialize(); in a DAW the UI
/// thread's message loop runs them, here the test calls this wherever that loop would run.
static void pumpMessages ()
{
	MSG msg;
	while (PeekMessageW (&msg, nullptr, 0, 0, PM_REMOVE))
	{
		TranslateMessage (&msg);
		DispatchMessageW (&msg);
	}
}

/// Converts a UTF-16 string to UTF-8 (for printing and comparing with literals).
static std::string toUtf8 (const std::wstring& w)
{
	if (w.empty ())
		return {};
	int n = WideCharToMultiByte (CP_UTF8, 0, w.data (), (int)w.size (), nullptr, 0, nullptr, nullptr);
	std::string s (n, '\0');
	WideCharToMultiByte (CP_UTF8, 0, w.data (), (int)w.size (), s.data (), n, nullptr, nullptr);
	return s;
}

/// Returns the UTF-8 title of the parameter at position @p index (not ParamID) of @p ctrl,
/// or "?" if the plug-in rejects the index. ParameterInfo::title is a zero-terminated
/// String128 (UTF-16, 128 TChars).
static std::string titleOf (IEditController* ctrl, int index)
{
	ParameterInfo info {};
	if (ctrl->getParameterInfo (index, info) != kResultOk)
		return "?";
	std::wstring w;
	for (int i = 0; i < 128 && info.title[i]; ++i)
		w += (wchar_t)info.title[i];
	return toUtf8 (w);
}

//------------------------------------------------------------------------------
/// One plug-in instance as a host sees it: the three interfaces of the same
/// SingleComponentEffect object, plus whether it is the FX (has an audio input bus).
struct Plugin
{
	IPtr<IComponent> component;       ///< lifecycle, busses, state
	IPtr<IAudioProcessor> processor;  ///< setupProcessing / process / latency
	IPtr<IEditController> controller; ///< parameter info, normalized values, display strings
	bool fx = false;                  ///< true: feed a stereo input bus in Runner::block()

	/// Creates and initializes the factory class whose display name is @p name
	/// ("Csound Visual Instrument" or "Csound Visual FX").
	/// Steps 1-2 of the lifecycle: createInstance + initialize(&gHost). In this plug-in,
	/// initialize() also creates the hidden UI window on the calling thread, starts the IPC
	/// thread, writes the descriptor file and compiles the default orchestra.
	/// @return false if the class is missing, creation/initialize fails, or the object does
	///         not expose both IAudioProcessor and IEditController.
	bool create (const VST3::Hosting::Module::Ptr& module, const std::string& name)
	{
		for (auto& ci : module->getFactory ().classInfos ())
		{
			if (ci.name () != name)
				continue;
			component = module->getFactory ().createInstance<IComponent> (ci.ID ());
			if (!component)
				return false;
			if (component->initialize (static_cast<IHostApplication*> (&gHost)) != kResultOk)
				return false;
			processor = FUnknownPtr<IAudioProcessor> (component);
			controller = FUnknownPtr<IEditController> (component);
			return processor && controller;
		}
		return false;
	}

	/// Lifecycle steps 3-5: setupProcessing (real-time mode, 32-bit float samples,
	/// @p maxBlock = largest number of samples per process() call, @p sr in Hz), then
	/// setActive(true) and setProcessing(true). setActive(true) rebuilds the Csound instance
	/// if the host rate differs from the one it was compiled at; the pump applies the
	/// parameter renames / latency report it posts to the UI thread.
	/// @return false if setupProcessing or setActive fail (setProcessing's result is ignored).
	bool start (double sr, int maxBlock)
	{
		ProcessSetup setup {kRealtime, kSample32, maxBlock, sr};
		if (processor->setupProcessing (setup) != kResultOk)
			return false;
		if (component->setActive (true) != kResultOk)
			return false;
		processor->setProcessing (true);
		pumpMessages ();
		return true;
	}

	/// Tears the instance down in reverse order: setProcessing(false), setActive(false),
	/// terminate() (stops the IPC thread and removes the descriptor file), then releases the
	/// interface pointers. Safe to call on an instance that was never created.
	void stop ()
	{
		if (!component)
			return;
		processor->setProcessing (false);
		component->setActive (false);
		component->terminate ();
		pumpMessages ();
		controller = nullptr;
		processor = nullptr;
		component = nullptr;
	}
};

/// Renders audio block by block and gathers statistics.
/// Plays the host's audio thread: owns the stereo input/output buffers, the event list
/// (MIDI-like note events), the parameter-change queues and a ProcessContext with a running
/// transport at 128 BPM. Statistics accumulate over every block rendered with stats = true
/// until resetStats().
struct Runner
{
	Plugin& p;
	std::vector<float> inL, inR, outL, outR; ///< per-channel buffers, maxBlock samples each
	EventList events {256};                  ///< events for the next block (cleared after it)
	ParameterChanges params {32};            ///< parameter changes for the next block
	ProcessContext ctx {};                   ///< transport info sent with every block
	double sr;                               ///< sample rate in Hz
	double phase = 0;                        ///< sine oscillator phase in radians
	double sineFreq = 0; // > 0: feed a sine into the FX input (Hz, amplitude 0.5)
	// statistics (both output channels, linear full scale = 1.0)
	double peak = 0, sumSq = 0, sumDiff = 0; ///< max |out|, sum of out^2, sum of |out - in|
	long long count = 0;                     ///< number of samples accumulated (2 per frame)
	bool finite = true;                      ///< false once any NaN/Inf output was seen

	/// @param plug     started plug-in to drive
	/// @param rate     sample rate in Hz (must match Plugin::start)
	/// @param maxBlock buffer size in samples (must be >= every block() size)
	Runner (Plugin& plug, double rate, int maxBlock) : p (plug), sr (rate)
	{
		inL.resize (maxBlock);
		inR.resize (maxBlock);
		outL.resize (maxBlock);
		outR.resize (maxBlock);
		ctx.sampleRate = sr;
		ctx.tempo = 128;
		ctx.state = ProcessContext::kTempoValid | ProcessContext::kPlaying | ProcessContext::kProjectTimeMusicValid;
	}

	/// Queues a note-on for the next block() on MIDI channel 1 (index 0).
	/// @param pitch  MIDI note number (60 = middle C)
	/// @param vel    velocity, normalized 0..1
	/// @param offset sample position inside the next block (0..n-1)
	void noteOn (int pitch, float vel, int offset)
	{
		Event e {};
		e.type = Event::kNoteOnEvent;
		e.sampleOffset = offset;
		e.noteOn.channel = 0;
		e.noteOn.pitch = (int16)pitch;
		e.noteOn.velocity = vel;
		e.noteOn.noteId = -1;
		events.addEvent (e);
	}
	/// Queues a note-off (release velocity 0.5) for the next block() on channel 1.
	/// @param pitch  MIDI note number of the held note
	/// @param offset sample position inside the next block
	void noteOff (int pitch, int offset)
	{
		Event e {};
		e.type = Event::kNoteOffEvent;
		e.sampleOffset = offset;
		e.noteOff.channel = 0;
		e.noteOff.pitch = (int16)pitch;
		e.noteOff.velocity = 0.5f;
		e.noteOff.noteId = -1;
		events.addEvent (e);
	}

	/// Renders one block of @p n samples (0 is legal in VST3 and is tested).
	/// Fills the input with the sine (or silence), calls process() with the queued events and
	/// parameter changes, then clears both queues and advances the transport (samples and
	/// quarter notes). The input bus is only connected for the FX; the Instrument gets
	/// numInputs = 0. With @p stats, every output sample updates peak / RMS / |out - in|.
	void block (int n, bool stats = true)
	{
		for (int i = 0; i < n; ++i)
		{
			float s = sineFreq > 0 ? (float)(0.5 * std::sin (phase)) : 0.f;
			phase += 2 * 3.14159265358979 * sineFreq / sr;
			inL[i] = s;
			inR[i] = s;
		}
		float* ins[2] = {inL.data (), inR.data ()};
		float* outs[2] = {outL.data (), outR.data ()};
		AudioBusBuffers inBus {}, outBus {};
		inBus.numChannels = 2;
		inBus.channelBuffers32 = ins;
		outBus.numChannels = 2;
		outBus.channelBuffers32 = outs;

		ProcessData d {};
		d.processMode = kRealtime;
		d.symbolicSampleSize = kSample32;
		d.numSamples = n;
		d.numInputs = p.fx ? 1 : 0;
		d.inputs = p.fx ? &inBus : nullptr;
		d.numOutputs = 1;
		d.outputs = &outBus;
		d.inputEvents = &events;
		d.inputParameterChanges = &params;
		d.processContext = &ctx;
		p.processor->process (d);
		events.clear ();
		params.clearQueue ();
		ctx.projectTimeSamples += n;
		ctx.projectTimeMusic += n / sr * ctx.tempo / 60.0; // seconds * beats per second = quarter notes

		if (!stats)
			return;
		for (int i = 0; i < n; ++i)
		{
			for (float v : {outL[i], outR[i]})
			{
				if (!std::isfinite (v))
					finite = false;
				peak = std::max (peak, (double)std::fabs (v));
				sumSq += (double)v * v;
			}
			sumDiff += std::fabs (outL[i] - inL[i]) + std::fabs (outR[i] - inR[i]);
			count += 2;
		}
	}

	/// Root mean square of all accumulated output samples (0 if none).
	double rms () const { return count ? std::sqrt (sumSq / count) : 0; }
	/// Starts a new measurement window (keeps the oscillator phase and transport position).
	void resetStats () { peak = sumSq = sumDiff = 0; count = 0; finite = true; }
};

//------------------------------------------------------------------------------
// IPC client
//------------------------------------------------------------------------------
/// Sends one JSON request to the plug-in's named pipe and reads the reply; this is the same
/// protocol the Csound Visual app uses.
/// Framing in both directions: 4-byte little-endian length (native uint32 on x64) followed by
/// that many bytes of UTF-8 JSON. One request per connection: the handle is closed after the
/// reply, which lets the server finish the session.
/// Connecting retries up to 100 times: on ERROR_PIPE_BUSY it waits up to 2 s for a free pipe
/// instance, on any other error (e.g. the server thread has not created the pipe yet) it sleeps
/// 20 ms.
/// @param pipe    full pipe path, e.g. \\.\pipe\CsoundVisualVST.<pid>.<n> (from the descriptor)
/// @param request UTF-8 JSON request
/// @param reply   receives the UTF-8 JSON reply
/// @return false if the pipe could not be opened or any write/read failed or was short.
///         Blocks until the plug-in replies (a "load" compiles a whole orchestra first).
static bool ipcCall (const std::wstring& pipe, const std::string& request, std::string& reply)
{
	HANDLE h = INVALID_HANDLE_VALUE;
	for (int tries = 0; tries < 100; ++tries)
	{
		h = CreateFileW (pipe.c_str (), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
		if (h != INVALID_HANDLE_VALUE)
			break;
		DWORD e = GetLastError ();
		if (e == ERROR_PIPE_BUSY)
			WaitNamedPipeW (pipe.c_str (), 2000);
		else
			Sleep (20);
	}
	if (h == INVALID_HANDLE_VALUE)
		return false;
	uint32_t len = (uint32_t)request.size ();
	DWORD io = 0;
	bool ok = WriteFile (h, &len, 4, &io, nullptr) && io == 4 &&
	          WriteFile (h, request.data (), len, &io, nullptr) && io == len;
	uint32_t rlen = 0;
	if (ok)
	{
		DWORD got = 0;
		ok = ReadFile (h, &rlen, 4, &got, nullptr) && got == 4;
	}
	if (ok)
	{
		// The reply may arrive in several chunks (byte-mode pipe): read until rlen bytes.
		reply.assign (rlen, '\0');
		DWORD total = 0;
		while (ok && total < rlen)
		{
			DWORD got = 0;
			ok = ReadFile (h, reply.data () + total, rlen - total, &got, nullptr) && got > 0;
			total += got;
		}
	}
	CloseHandle (h);
	return ok;
}

/// %LOCALAPPDATA%\CsoundVisual\vst-instances: where every plug-in instance keeps a JSON
/// descriptor (kind, pid, pipe name, track, csdName, host) while it exists. Mirrors
/// CsoundVisual::descriptorDirectory() in the plug-in, but does not create the folder.
static std::wstring descriptorDir ()
{
	PWSTR p = nullptr;
	std::wstring d;
	if (SUCCEEDED (SHGetKnownFolderPath (FOLDERID_LocalAppData, 0, nullptr, &p)))
		d = p;
	CoTaskMemFree (p);
	return d + L"\\CsoundVisual\\vst-instances";
}

/// Full paths of the descriptor files written by instances in this process
/// ("<pid>.*.json"), in FindFirstFile order. Filtering by our own PID keeps the count
/// independent of a DAW or another test that may be running at the same time.
static std::vector<std::wstring> descriptorsOfThisProcess ()
{
	std::vector<std::wstring> out;
	wchar_t pattern[64];
	std::swprintf (pattern, 64, L"\\%lu.*.json", GetCurrentProcessId ());
	WIN32_FIND_DATAW fd;
	HANDLE h = FindFirstFileW ((descriptorDir () + pattern).c_str (), &fd);
	if (h == INVALID_HANDLE_VALUE)
		return out;
	do
		out.push_back (descriptorDir () + L"\\" + fd.cFileName);
	while (FindNextFileW (h, &fd));
	FindClose (h);
	return out;
}

/// Reads a whole file as raw bytes (the descriptor is UTF-8 JSON). Shares read/delete access
/// so it never blocks the plug-in's atomic replace of the file. Empty string on failure.
static std::string readFile (const std::wstring& path)
{
	std::string s;
	HANDLE f = CreateFileW (path.c_str (), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
	if (f == INVALID_HANDLE_VALUE)
		return s;
	char buf[4096];
	DWORD got;
	while (ReadFile (f, buf, sizeof (buf), &got, nullptr) && got > 0)
		s.append (buf, got);
	CloseHandle (f);
	return s;
}

/// Converts UTF-8 to UTF-16 (for the pipe name taken from the descriptor JSON).
static std::wstring widen (const std::string& s)
{
	int n = MultiByteToWideChar (CP_UTF8, 0, s.data (), (int)s.size (), nullptr, 0);
	std::wstring w (n, L'\0');
	MultiByteToWideChar (CP_UTF8, 0, s.data (), (int)s.size (), w.data (), n);
	return w;
}

/// Orchestra sent with the IPC "load" command. Each line exercises one plug-in rule:
///  * <CsOptions> with real-time device flags (-odac, -iadc, -b/-B, --midi-key, -+rtaudio):
///    they make no sense inside a plug-in and must be dropped ("CsOptions ignored" in the log).
///  * sr = 44100: must be overridden by the host rate (48000 here); ksmps = 64 must be kept.
///  * chnk "gain" (linear, type 2) and "freq" (exponential, type 3, 50..5000 Hz): become
///    parameters 1-2 in declaration order; "unused_out" (mode 2 = output) is not a parameter.
///  * chnget "HOST_BPM" + printk: proves the host tempo (128) reaches the orchestra via the log.
///  * instr 1 (always on, schedule 1, 0, -1): sine at gain 0.25 -> expected peak ~0.25.
///  * massign 0, 2 + instr 2 (cpsmidi): VST3 note events reach Csound as MIDI, peak ~0.1.
static const char* kTestCsd = R"CSD(<CsoundSynthesizer>
<CsOptions>
-odac -iadc -b 128 -B 512 --midi-key=4 -+rtaudio=wasapi
</CsOptions>
<CsInstruments>
sr = 44100        ; must be overridden by the host rate
ksmps = 64
nchnls = 2
0dbfs = 1
chnk "gain", 1, 2, 0.25, 0, 1
chnk "freq", 1, 3, 330, 50, 5000
chn_k "unused_out", 2
massign 0, 2
instr 1
  kg chnget "gain"
  kf chnget "freq"
  kbpm chnget "HOST_BPM"
  printk 0.5, kbpm
  a1 oscili kg, kf
  out a1, a1
endin
instr 2
  icps cpsmidi
  a1 oscili 0.1, icps
  out a1, a1
endin
schedule 1, 0, -1
</CsInstruments>
<CsScore>
</CsScore>
</CsoundSynthesizer>
)CSD";

//------------------------------------------------------------------------------
/// Loads the bundle with VST3::Hosting::Module (LoadLibrary of
/// Contents\x86_64-win\CsoundVisual.vst3 + GetPluginFactory), lists the factory classes, then
/// runs either the --serve loop or the four test sections in order. Every section creates its
/// own instances and stops them, so a failure in one section does not leak into the next.
/// @return 0 when every CHECK passed, 1 when any failed, 2 on bad usage or if the module
///         cannot be loaded.
int main (int argc, char* argv[])
{
	if (argc < 2)
	{
		std::printf ("usage: vsthost_test <CsoundVisual.vst3>\n");
		return 2;
	}
	std::string error;
	auto module = VST3::Hosting::Module::create (argv[1], error);
	if (!module)
	{
		std::printf ("cannot load module: %s\n", error.c_str ());
		return 2;
	}
	std::printf ("Module: %s\n", module->getName ().c_str ());
	for (auto& ci : module->getFactory ().classInfos ())
		std::printf ("  class: %s  [%s]  vendor=%s  version=%s\n", ci.name ().c_str (),
		             ci.subCategoriesString ().c_str (), ci.vendor ().c_str (), ci.version ().c_str ());

	// Host sample rate for every test; deliberately not the 44100 of kTestCsd, so the
	// sr override can be observed.
	const double sr = 48000;

	// ---------------------------------------------------------------- serve mode
	// "vsthost_test <bundle> --serve <seconds>": keep one Instrument instance running (pipe and
	// descriptor alive, audio processed in real time, output discarded) so the Csound Visual app
	// can be tested against a live plugin without a DAW.
	if (argc >= 4 && std::string (argv[2]) == "--serve")
	{
		int seconds = std::atoi (argv[3]);
		Plugin inst;
		CHECK (inst.create (module, "Csound Visual Instrument"), "create Instrument");
		CHECK (inst.start (sr, 512), "setupProcessing + setActive");
		Runner r (inst, sr, 512);
		std::printf ("serving for %d s\n", seconds);
		std::fflush (stdout);
		auto end = std::chrono::steady_clock::now () + std::chrono::seconds (seconds);
		// Roughly real time: one 512-sample block (10.7 ms at 48 kHz) per ~10 ms sleep plus
		// render time. The message pump keeps renames from IPC "load" commands flowing.
		while (std::chrono::steady_clock::now () < end)
		{
			r.block (512);
			pumpMessages ();
			std::this_thread::sleep_for (std::chrono::microseconds (10000));
		}
		std::printf ("peak while serving = %.4f\n", r.peak);
		inst.stop ();
		return gFailures == 0 ? 0 : 1;
	}

	// ---------------------------------------------------------------- 0. minijson
	// The IPC JSON code (compiled into this test too) must not depend on the C locale: a
	// host may switch LC_NUMERIC to a comma locale, which used to turn 0.5 into "0,5"
	// (invalid JSON) and make "0.5" unparsable. The locale is changed here only for these
	// checks and restored right after. Also checks the lone-high-surrogate decoding.
	std::printf ("\n== minijson\n");
	{
		std::string oldLocale = std::setlocale (LC_NUMERIC, nullptr);
		const bool commaLocale = std::setlocale (LC_NUMERIC, "de-DE") != nullptr;
		std::string dumped = Value (0.5).dump ();
		Value parsed;
		std::string jerr;
		const bool parsedOk = Value::parse ("[0.5, 48000, -1.25e-3]", parsed, jerr);
		std::setlocale (LC_NUMERIC, oldLocale.c_str ());
		std::printf ("  comma locale active: %s, 0.5 -> %s\n", commaLocale ? "yes" : "no", dumped.c_str ());
		CHECK (dumped == "0.5", "number formatting ignores the C locale");
		CHECK (parsedOk && parsed.at (0).asNumber () == 0.5 && parsed.at (1).asNumber () == 48000 &&
		           parsed.at (2).asNumber () == -1.25e-3,
		       "number parsing ignores the C locale");
		CHECK (Value (48000.0).dump () == "48000" && Value (-3.0).dump () == "-3", "integral numbers print as integers");
		Value s;
		CHECK (Value::parse ("\"\\uD800\\u0041\"", s, jerr) && s.asString () == "\xEF\xBF\xBD" "A",
		       "lone high surrogate -> U+FFFD and the next escape is kept");
		CHECK (Value::parse ("\"\\uD83D\\uDE00\"", s, jerr) && s.asString () == "\xF0\x9F\x98\x80",
		       "surrogate pair -> one UTF-8 code point");
	}

	// ---------------------------------------------------------------- 1. Instrument 512
	std::printf ("\n== Instrument, 48 kHz, 512-sample blocks\n");
	{
		Plugin inst;
		CHECK (inst.create (module, "Csound Visual Instrument"), "create Instrument");
		CHECK (inst.start (sr, 512), "setupProcessing + setActive");
		std::printf ("  latency = %u samples\n", inst.processor->getLatencySamples ());
		// The 16 generic slots ("Param 1".."Param 16") must now carry the default instrument's
		// six channel names; unused slots are titled with an em dash (U+2014, UTF-8 E2 80 94).
		std::vector<std::string> titles;
		for (int i = 0; i < 16; ++i)
			titles.push_back (titleOf (inst.controller, i));
		std::printf ("  titles:");
		for (auto& t : titles)
			std::printf (" '%s'", t.c_str ());
		std::printf ("\n");
		CHECK (titles[0] == "cutoff" && titles[5] == "volume" && titles[6] == "\xE2\x80\x94",
		       "parameters renamed to the orchestra's channels (cutoff..volume, then em dash)");
		// Display string of the default value (printed for inspection, not checked).
		String128 str {};
		inst.controller->getParamStringByValue (0, inst.controller->getParamNormalized (0), str);
		std::wstring ws;
		for (int i = 0; str[i]; ++i)
			ws += (wchar_t)str[i];
		std::printf ("  cutoff display at default = %s (normalized %.4f)\n", toUtf8 (ws).c_str (),
		             inst.controller->getParamNormalized (0));

		// ~2 s of audio: a C major chord (60/64/67) starts in block 2 at sample offsets
		// 100..102 and is released at ~1.4 s, leaving ~0.6 s of release tail.
		Runner r (inst, sr, 512);
		int blocks = (int)(2.0 * sr / 512);
		for (int b = 0; b < blocks; ++b)
		{
			if (b == 2)
			{
				r.noteOn (60, 0.8f, 100);
				r.noteOn (64, 0.7f, 101);
				r.noteOn (67, 0.7f, 102);
			}
			if (b == (int)(1.4 * sr / 512))
			{
				r.noteOff (60, 0);
				r.noteOff (64, 0);
				r.noteOff (67, 0);
			}
			r.block (512);
		}
		std::printf ("  peak = %.4f  rms = %.4f\n", r.peak, r.rms ());
		CHECK (r.finite, "output finite");
		CHECK (r.peak > 0.01, "output non-silent (peak %.4f)", r.peak);
		CHECK (r.peak < 1.0, "output does not clip (peak %.4f)", r.peak);

		// MIDI pitch bend through the hidden parameter (IMidiMapping)
		// VST3 has no MIDI controller events: hosts ask IMidiMapping which parameter a
		// controller drives and send it as a parameter change (kParamPitchBend = 100).
		FUnknownPtr<IMidiMapping> mm (inst.component);
		ParamID pb = 0;
		CHECK (mm && mm->getMidiControllerAssignment (0, 0, kPitchBend, pb) == kResultTrue && pb == 100,
		       "IMidiMapping maps pitch bend to parameter 100");
		// Other MIDI channels get their own parameters (so a controller keeps its channel):
		// channel 2 (index 1) -> 300.., channel 16 -> 300 + 14 * 6 + idx; CC64 (idx 4).
		ParamID pb2 = 0, sus16 = 0;
		CHECK (mm && mm->getMidiControllerAssignment (0, 1, kPitchBend, pb2) == kResultTrue && pb2 == 300 &&
		           mm->getMidiControllerAssignment (0, 15, kCtrlSustainOnOff, sus16) == kResultTrue &&
		           sus16 == 300 + 14 * 6 + 4,
		       "IMidiMapping maps each MIDI channel to its own parameters (ch2 bend %u, ch16 sustain %u)",
		       (unsigned)pb2, (unsigned)sus16);

		// state round trip
		// getState writes the binary state (CSD text, name, parameter values) into a memory
		// stream; a second, never-activated instance must restore it, including the renamed
		// parameters once the posted UI update has been pumped.
		MemoryStream state;
		CHECK (inst.component->getState (&state) == kResultOk, "getState");
		std::printf ("  state size = %lld bytes\n", (long long)state.getSize ());
		inst.stop ();

		// create() is checked: on failure the interface pointers are null and the calls
		// below would crash the test instead of reporting a FAIL.
		Plugin copy;
		const bool copyOk = copy.create (module, "Csound Visual Instrument");
		CHECK (copyOk, "create a second Instrument");
		if (copyOk)
		{
			state.seek (0, IBStream::kIBSeekSet, nullptr);
			CHECK (copy.component->setState (&state) == kResultOk, "setState into a fresh instance");
			pumpMessages ();
			CHECK (titleOf (copy.controller, 1) == "resonance", "restored instance has renamed parameters");

			// The saved kind is validated: an Instrument chunk must be rejected by the FX
			// class (it would compile an orchestra written for the other bus layout).
			Plugin wrongKind;
			wrongKind.fx = true;
			if (wrongKind.create (module, "Csound Visual FX"))
			{
				state.seek (0, IBStream::kIBSeekSet, nullptr);
				CHECK (wrongKind.component->setState (&state) == kResultFalse,
				       "FX rejects a state saved by the Instrument");
				wrongKind.stop ();
			}
			else
				CHECK (false, "create FX for the wrong-kind state test");
		}
		copy.stop ();
	}

	// ---------------------------------------------------------------- 2. odd block size
	// Csound renders in ksmps chunks; host blocks that are not multiples of ksmps (333, and
	// later 0, 1, 7...) must be bridged by the plug-in's internal buffering without glitches.
	std::printf ("\n== Instrument, block size 333\n");
	{
		Plugin inst;
		// Abort this section (not the whole run) if create() fails: start() would
		// dereference null interface pointers.
		if (!inst.create (module, "Csound Visual Instrument"))
		{
			CHECK (false, "create Instrument (block size 333)");
			return 1;
		}
		CHECK (inst.start (sr, 333), "start with maxBlock 333");
		Runner r (inst, sr, 333);
		for (int b = 0; b < (int)(1.5 * sr / 333); ++b)
		{
			if (b == 1)
				r.noteOn (57, 1.0f, 332); // last sample of the block: tests offset handling
			r.block (333);
		}
		std::printf ("  peak = %.4f  rms = %.4f\n", r.peak, r.rms ());
		CHECK (r.finite && r.peak > 0.01, "odd block size renders audio");
		// variable sizes, including 1 and 0
		r.resetStats ();
		int sizes[] = {1, 0, 7, 31, 32, 33, 64, 100, 333, 17};
		for (int k = 0; k < 200; ++k)
			r.block (sizes[k % 10]);
		CHECK (r.finite, "variable block sizes (0..333) stay finite");
		// Blocks LARGER than maxSamplesPerBlock (333) - a host spec violation - are now
		// rendered in chunks through the scratch buffers instead of straight into `out`.
		// A fresh note keeps the output audible; every sample must be finite and written.
		{
			Runner big (inst, sr, 1000);
			big.noteOn (60, 1.0f, 10);
			for (int k = 0; k < 20; ++k)
				big.block (1000);
			std::printf ("  oversized blocks (1000 > 333): peak = %.4f\n", big.peak);
			CHECK (big.finite && big.peak > 0.01, "blocks larger than maxSamplesPerBlock render in chunks");
		}
		inst.stop ();
	}

	// ---------------------------------------------------------------- 3. FX with sine
	// The default FX orchestra (delay + chorus + reverb, channels mix/time/feedback/tone/size)
	// is fed a 220 Hz sine at amplitude 0.5. Its output must differ from the dry input, keep
	// ringing after the input stops, and pass the input through untouched when bypassed.
	std::printf ("\n== FX, 48 kHz, 512-sample blocks, 220 Hz sine input\n");
	{
		Plugin fx;
		fx.fx = true;
		CHECK (fx.create (module, "Csound Visual FX"), "create FX");
		CHECK (fx.start (sr, 512), "setupProcessing + setActive");
		std::string t;
		for (int i = 0; i < 6; ++i)
			t += " '" + titleOf (fx.controller, i) + "'";
		std::printf ("  titles:%s\n", t.c_str ());
		CHECK (titleOf (fx.controller, 0) == "mix" && titleOf (fx.controller, 4) == "size", "FX parameters renamed");
		Runner r (fx, sr, 512);
		r.sineFreq = 220;
		for (int b = 0; b < (int)(2.0 * sr / 512); ++b)
			r.block (512);
		std::printf ("  peak = %.4f  rms = %.4f  mean |out-in| = %.4f\n", r.peak, r.rms (), r.sumDiff / r.count);
		CHECK (r.finite, "FX output finite");
		CHECK (r.peak > 0.05, "FX output non-silent");
		CHECK (r.sumDiff / r.count > 0.01, "FX output differs from the dry input (effect is applied)");
		// tail after the input stops
		r.resetStats ();
		r.sineFreq = 0;
		for (int b = 0; b < (int)(0.5 * sr / 512); ++b)
			r.block (512);
		std::printf ("  tail peak after input stops = %.4f\n", r.peak);
		CHECK (r.peak > 0.001, "FX has an audible tail (delay/reverb)");

		// Bypass: output must be the dry input delayed by exactly the reported latency.
		// The host switches bypass like any automation: a parameter change on kParamBypass
		// (200) at sample 0 of the next block. The first block lets it engage; the second is
		// compared sample by sample with the input delayed by `lat` samples, taking the
		// samples before the block start from the saved previous input (lastIn).
		uint32 lat = fx.processor->getLatencySamples ();
		int32 qi = 0;
		IParamValueQueue* q = r.params.addParameterData (200, qi);
		q->addPoint (0, 1.0, qi);
		r.sineFreq = 220;
		// The input of every block from the engaging one on is kept in `history`, and enough
		// blocks are rendered that the last block's delayed source (up to `lat` samples
		// back) is always inside it. Before, only one previous block was kept, so a latency
		// above 512 samples (an orchestra with ksmps > 512) read lastIn[512 + src] with a
		// negative index (out of bounds).
		std::vector<float> history;
		r.block (512); // bypass engages
		history.insert (history.end (), r.inL.begin (), r.inL.begin () + 512);
		const int checkBlocks = (int)(lat / 512) + 1; // >= 1; history then holds >= lat + 512 samples
		for (int k = 0; k < checkBlocks; ++k)
		{
			r.block (512);
			history.insert (history.end (), r.inL.begin (), r.inL.begin () + 512);
		}
		// Compare the last rendered block: its first input sample is history[lastStart].
		const size_t lastStart = history.size () - 512;
		double maxErr = 0;
		for (int i = 0; i < 512; ++i)
		{
			const long long src = (long long)lastStart + i - (long long)lat; // >= 0 by construction
			const float expected = src >= 0 ? history[(size_t)src] : 0.f;
			maxErr = std::max (maxErr, (double)std::fabs (r.outL[i] - expected));
		}
		std::printf ("  bypass: latency %u, max |out - delayed in| = %g\n", lat, maxErr);
		CHECK (maxErr < 1e-6, "bypass passes the dry signal delayed by the latency");
		// The bypass switch is part of the saved state (hosts expect it to survive a reload).
		MemoryStream st;
		fx.component->getState (&st);
		Plugin fx2;
		fx2.fx = true;
		const bool fx2Ok = fx2.create (module, "Csound Visual FX"); // checked: see `copy` above
		CHECK (fx2Ok, "create a second FX");
		if (fx2Ok)
		{
			st.seek (0, IBStream::kIBSeekSet, nullptr);
			fx2.component->setState (&st);
			CHECK (fx2.controller->getParamNormalized (200) == 1.0, "bypass state persisted in getState/setState");
		}
		fx2.stop ();
		fx.stop ();
	}

	// ---------------------------------------------------------------- 4. IPC
	// Plays the Csound Visual app's side of the protocol: find the instance through its
	// descriptor file, then "status", "load" (while audio runs on another thread), "set",
	// a MIDI note into the loaded orchestra, a broken "load", and descriptor removal.
	std::printf ("\n== IPC\n");
	{
		auto before = descriptorsOfThisProcess ();
		Plugin inst;
		if (!inst.create (module, "Csound Visual Instrument")) // checked: see section 2
		{
			CHECK (false, "create Instrument (IPC)");
			return 1;
		}
		inst.start (sr, 512);
		auto after = descriptorsOfThisProcess ();
		CHECK (after.size () == before.size () + 1, "descriptor file created (%d -> %d)", (int)before.size (), (int)after.size ());
		std::wstring descPath = after.empty () ? L"" : after.back ();
		std::string descText = readFile (descPath);
		std::printf ("  descriptor %s:\n    %s\n", toUtf8 (descPath).c_str (), descText.c_str ());
		Value desc;
		std::string perr;
		CHECK (Value::parse (descText, desc, perr) && desc["kind"].asString () == "instrument" &&
		           desc["pid"].asNumber () == GetCurrentProcessId () && desc["csdName"].asString () == "default",
		       "descriptor JSON content");
		std::wstring pipe = widen (desc["pipe"].asString ());

		std::string reply;
		CHECK (ipcCall (pipe, R"({"cmd":"status"})", reply), "status request");
		Value st;
		Value::parse (reply, st, perr);
		std::printf ("  status: sr=%g ksmps=%g csdName=%s channels=%d\n", st["sr"].asNumber (), st["ksmps"].asNumber (),
		             st["csdName"].asString ().c_str (), (int)st["channels"].size ());
		std::printf ("  csound: %s\n", st["csound"].asString ().c_str ());
		CHECK (st["ok"].asBool () && st["channels"].size () == 6 && st["channels"].at (0)["name"].asString () == "cutoff",
		       "status lists the 6 default channels");

		// Keep audio running on another thread while loading (exercises the swap).
		// This thread is now the "audio thread": the main thread must not call r.block()
		// until it is joined. The load is compiled on the plug-in's IPC thread and swapped in
		// under swapMutex, which the audio thread only try_locks.
		std::atomic<bool> run {true};
		std::atomic<int> audioBlocks {0};
		Runner r (inst, sr, 512);
		std::thread audio ([&] () {
			while (run.load ())
			{
				r.block (512);
				++audioBlocks;
				Sleep (1);
			}
		});

		Value load = Value::object ();
		load.set ("cmd", "load");
		load.set ("name", "ipc_test.csd");
		load.set ("csd", kTestCsd);
		auto t0 = std::chrono::steady_clock::now ();
		CHECK (ipcCall (pipe, load.dump (), reply), "load request");
		auto ms = std::chrono::duration_cast<std::chrono::milliseconds> (std::chrono::steady_clock::now () - t0).count ();
		Value lr;
		Value::parse (reply, lr, perr);
		std::printf ("  load reply in %lld ms: ok=%d channels=%s\n", (long long)ms, (int)lr["ok"].asBool (),
		             lr["channels"].dump ().c_str ());
		std::printf ("  load log:\n%s\n", lr["log"].asString ().c_str ());
		CHECK (lr["ok"].asBool (), "load ok");
		CHECK (lr["channels"].size () == 2 && lr["channels"].at (0).asString () == "gain" &&
		           lr["channels"].at (1).asString () == "freq",
		       "load reply channels = [gain, freq] (declaration order, output channel excluded)");
		CHECK (lr["log"].asString ().find ("CsOptions ignored") != std::string::npos, "device CsOptions were dropped");

		// Let the new orchestra run (and printk report HOST_BPM) before poking it.
		Sleep (300);
		CHECK (ipcCall (pipe, R"({"cmd":"set","channel":"freq","value":1234.5})", reply), "set request");
		Value sr2;
		Value::parse (reply, sr2, perr);
		CHECK (sr2["ok"].asBool (), "set ok (%s)", reply.c_str ());
		CHECK (ipcCall (pipe, R"({"cmd":"set","channel":"nope","value":1})", reply) && reply.find ("false") != std::string::npos,
		       "set on unknown channel fails cleanly");
		Sleep (600);

		CHECK (ipcCall (pipe, R"({"cmd":"status"})", reply), "status after load");
		Value::parse (reply, st, perr);
		std::printf ("  status: sr=%g ksmps=%g csdName=%s channels=%s\n", st["sr"].asNumber (), st["ksmps"].asNumber (),
		             st["csdName"].asString ().c_str (), st["channels"].dump ().c_str ());
		CHECK (st["sr"].asNumber () == 48000, "host sample rate overrides sr = 44100 of the CSD");
		CHECK (st["ksmps"].asNumber () == 64, "ksmps kept from the CSD");
		CHECK (st["csdName"].asString () == "ipc_test.csd", "csdName updated");
		CHECK (std::fabs (st["channels"].at (1)["value"].asNumber () - 1234.5) < 0.5, "set value visible in status");
		std::string log = st["log"].asString ();
		CHECK (log.find ("128.0") != std::string::npos, "HOST_BPM (128) reached the orchestra (printk in log)");

		run = false;
		audio.join ();
		std::printf ("  audio blocks rendered during IPC: %d\n", audioBlocks.load ());
		r.resetStats ();
		for (int b = 0; b < 20; ++b)
			r.block (512);
		std::printf ("  new orchestra peak = %.4f (expected ~0.25)\n", r.peak);
		CHECK (r.finite && r.peak > 0.2 && r.peak < 0.3, "new orchestra plays (sine at gain 0.25)");

		pumpMessages (); // apply the parameter renames posted by the IPC thread
		CHECK (titleOf (inst.controller, 0) == "gain" && titleOf (inst.controller, 1) == "freq" &&
		           titleOf (inst.controller, 2) == "\xE2\x80\x94",
		       "parameters renamed after IPC load (gain, freq, em dash)");
		// "freq" is exponential (chnk type 3, 50..5000 Hz): value = min * (max/min)^norm, so
		// norm = ln(value/min) / ln(max/min) ~= 0.70 for 1234.5 Hz.
		double n1 = inst.controller->getParamNormalized (1);
		std::printf ("  'freq' normalized after set = %.4f\n", n1);
		CHECK (std::fabs (n1 - std::log (1234.5 / 50) / std::log (5000.0 / 50)) < 1e-3, "set updated the parameter (exp mapping)");

		// MIDI-triggered instr 2 via massign in the loaded orchestra
		// Silence instr 1 first (gain 0) so the measured peak comes only from the note
		// (instr 2 plays at amplitude 0.1).
		r.resetStats ();
		Value set0 = Value::object ();
		ipcCall (pipe, R"({"cmd":"set","channel":"gain","value":0})", reply);
		pumpMessages ();
		for (int b = 0; b < 5; ++b)
			r.block (512);
		r.resetStats ();
		r.noteOn (69, 0.9f, 0);
		for (int b = 0; b < 20; ++b)
			r.block (512);
		std::printf ("  MIDI note through massign: peak = %.4f (expected ~0.1)\n", r.peak);
		CHECK (r.peak > 0.05 && r.peak < 0.15, "MIDI note reaches the orchestra (massign -> instr 2)");

		// bad CSD: reply ok=false with the error, old orchestra keeps running
		Value bad = Value::object ();
		bad.set ("cmd", "load");
		bad.set ("name", "broken.csd");
		bad.set ("csd", "<CsoundSynthesizer><CsInstruments>\ninstr 1\n a1 nosuchopcode 1\nendin\n</CsInstruments></CsoundSynthesizer>");
		CHECK (ipcCall (pipe, bad.dump (), reply), "load broken CSD");
		Value br;
		Value::parse (reply, br, perr);
		std::printf ("  broken load: ok=%d error=%s\n", (int)br["ok"].asBool (), br["error"].asString ().c_str ());
		CHECK (!br["ok"].asBool () && !br["log"].asString ().empty (), "broken CSD reports ok=false with a log");
		r.resetStats ();
		for (int b = 0; b < 20; ++b)
			r.block (512);
		CHECK (r.peak > 0.05, "previous orchestra still playing after a failed load");

		// terminate() (inside stop) must delete this instance's descriptor file.
		inst.stop ();
		CHECK (GetFileAttributesW (descPath.c_str ()) == INVALID_FILE_ATTRIBUTES, "descriptor removed on terminate");
	}

	std::printf ("\n%s: %d failure(s)\n", gFailures ? "FAILED" : "ALL PASSED", gFailures);
	return gFailures ? 1 : 0;
}
