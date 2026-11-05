//------------------------------------------------------------------------------
// Csound Visual VST3 - one compiled, running Csound instance (implementation)
//
// Sections:
//   * Channel range mapping       - normalised 0..1 parameter <-> channel units
//   * Audio-thread message routing - thread_local "am I the audio thread" flag and
//                                    the drain of the lock-free message FIFO
//   * Csound callbacks             - message and external-MIDI C callbacks
//   * CSD preprocessing            - make a stand-alone CSD safe inside a DAW
//   * Build / destroy              - CsoundInstance::build() (non-audio thread)
//   * Audio thread                 - renderRange(), channel stores, host info
//
// Real-time rules for the audio-thread section: no locks, no heap allocation,
// no system calls, bounded loops. Everything that could violate them (creating
// channels, listing channels, compiling) is done once in build().
//------------------------------------------------------------------------------
#include "csoundengine.h"

#include <algorithm>
#include <atomic>
#include <cctype>
#include <cmath>
#include <cstdio> // std::snprintf (option and banner formatting); was only included transitively
#include <cstring>
#include <mutex>

namespace CsoundVisual {

//==============================================================================
// Channel range mapping
//==============================================================================
/// True when the channel has a usable (non-empty, non-inverted) range.
static bool hasRange (const ChannelDesc& c) { return c.max > c.min; }

/// Normalised 0..1 -> channel units.
///   LIN / NO_HINTS: v = min + n * (max - min)
///   INT:            same, rounded to the nearest integer
///   EXP:            v = min * (max/min)^n  (equal ratios per equal parameter steps, the
///                   natural mapping for frequencies); only when both bounds are > 0,
///                   otherwise falls back to linear because log of <= 0 is undefined.
double ChannelDesc::fromNormalized (double n) const
{
	n = std::clamp (n, 0.0, 1.0);
	if (!hasRange (*this))
		return n; // no hints: plain 0..1
	if (behav == CSOUND_CONTROL_CHANNEL_EXP && min > 0 && max > 0)
		return min * std::pow (max / min, n);
	double v = min + n * (max - min);
	if (behav == CSOUND_CONTROL_CHANNEL_INT)
		v = std::round (v);
	return v;
}

/// Channel units -> normalised 0..1 (exact inverse of fromNormalized except for INT
/// rounding). The value is clamped to [min, max] first, and the result again to 0..1 to
/// absorb floating-point error.
double ChannelDesc::toNormalized (double v) const
{
	if (!hasRange (*this))
		return std::clamp (v, 0.0, 1.0);
	v = std::clamp (v, min, max);
	if (behav == CSOUND_CONTROL_CHANNEL_EXP && min > 0 && max > 0)
		return std::clamp (std::log (v / min) / std::log (max / min), 0.0, 1.0);
	return std::clamp ((v - min) / (max - min), 0.0, 1.0);
}

//==============================================================================
// Audio-thread message routing
//==============================================================================
// Csound's message callback has no idea which thread it runs on. A thread_local flag,
// set around process() by the plug-in, tells it whether it may take MessageLog's mutex
// (non-audio threads) or must use the lock-free FIFO instead (audio thread).
static thread_local bool tlsAudioThread = false;

void setAudioThreadFlag (bool isAudioThread) { tlsAudioThread = isAudioThread; }

/// Process-wide mutex that keeps drainAudioThreadMessages() single-consumer even if two
/// non-audio threads (IPC and UI) drain at the same moment. Function-local static so it is
/// constructed on first use (no static-initialisation-order issues inside a DLL). It is
/// shared by all plug-in instances, which is harmless: draining is short and rare.
static std::mutex& drainMutex ()
{
	static std::mutex m;
	return m;
}

/// Pops the audio-thread text in 1 KiB chunks and appends each chunk to the log. Chunks
/// may cut a line (or a UTF-8 sequence) anywhere; MessageLog::append joins fragments until
/// a newline, so the split is invisible in the result. The extra byte in `buf` is for the
/// terminating NUL that append() expects.
void drainAudioThreadMessages (EngineShared& shared)
{
	std::lock_guard<std::mutex> lock (drainMutex ()); // serializes consumers only
	unsigned char buf[1025];
	for (;;)
	{
		size_t n = shared.audioThreadText.pop (buf, 1024);
		if (n == 0)
			break;
		buf[n] = 0;
		shared.log.append (reinterpret_cast<const char*> (buf));
	}
}

//==============================================================================
// Csound callbacks
//==============================================================================
// All callbacks find their CsoundInstance through csoundGetHostData(): build() passes the
// instance pointer as hostData to csoundCreate, so it is valid for the whole life of cs.

/// Message sink. `attr` (message type / colour) is ignored; colour escapes inside the text
/// are stripped later by MessageLog. On the audio thread the text is pushed to the FIFO
/// (lock-free, all-or-nothing: a fragment that does not fit is dropped, never waited for);
/// on any other thread it goes straight to the log under its mutex.
void CsoundInstance::message (CSOUND* cs, int32_t /*attr*/, const char* str)
{
	if (!str)
		return;
	auto& api = CsoundApi::get ();
	auto* self = static_cast<CsoundInstance*> (api.GetHostData (cs));
	if (!self || !self->shared)
		return;
	if (tlsAudioThread)
		self->shared->audioThreadText.push (reinterpret_cast<const unsigned char*> (str),
		                                    std::strlen (str)); // dropped when full, never blocks
	else
		self->shared->log.append (str);
}

/// Called by Csound during csoundStart for the "-M0" device (see build()). The device name
/// is irrelevant: there is exactly one MIDI input, the DAW's event stream.
int32_t CsoundInstance::midiOpen (CSOUND* cs, void** userData, const char*)
{
	*userData = CsoundApi::get ().GetHostData (cs);
	return 0;
}

/// Called by Csound at the start of every control cycle (inside csoundPerformKsmps, so on
/// the audio thread). Returns raw MIDI bytes queued by queueMidi(); Csound's MIDI parser
/// reassembles messages, so returning a partial message is fine (the rest comes on the
/// next call). Falls back to hostData if the open callback did not set userData.
int32_t CsoundInstance::midiRead (CSOUND* cs, void* userData, unsigned char* buf, int32_t nBytes)
{
	auto* self = static_cast<CsoundInstance*> (userData ? userData : CsoundApi::get ().GetHostData (cs));
	if (!self || nBytes <= 0)
		return 0;
	return (int32_t)self->midi.pop (buf, (size_t)nBytes);
}

/// Nothing to close: the FIFO belongs to the CsoundInstance.
int32_t CsoundInstance::midiClose (CSOUND*, void*) { return 0; }

//==============================================================================
// CSD preprocessing
//==============================================================================
// A CSD written for the stand-alone Csound Visual app typically asks for an audio
// device (-odac), a MIDI device (-Ma), a sample rate, etc. Inside a DAW the plug-in owns
// all of that, so the text is rewritten before compiling. All parsing here is lexical
// (string searches), deliberately simple, and runs only on the loading thread.
namespace {

/// Splits CsOptions text into tokens (whitespace separated, ';' / '#' line comments and
/// /* */ block comments removed, "double quoted" tokens kept together).
/// Quotes stay part of the token (e.g. --omacro:NAME="a b" stays one token). An
/// unterminated quote extends to the end of the text.
std::vector<std::string> tokenizeOptions (const std::string& text)
{
	std::vector<std::string> out;
	std::string cur;
	size_t i = 0;
	auto flush = [&] () {
		if (!cur.empty ())
			out.push_back (cur);
		cur.clear ();
	};
	while (i < text.size ())
	{
		char c = text[i];
		if (c == ';' || c == '#')
		{
			flush ();
			while (i < text.size () && text[i] != '\n')
				++i;
			continue;
		}
		if (c == '/' && i + 1 < text.size () && text[i + 1] == '*')
		{
			flush ();
			size_t e = text.find ("*/", i + 2);
			i = e == std::string::npos ? text.size () : e + 2;
			continue;
		}
		if (c == '"')
		{
			size_t e = text.find ('"', i + 1);
			if (e == std::string::npos)
				e = text.size () - 1;
			cur += text.substr (i, e - i + 1);
			i = e + 1;
			continue;
		}
		if (std::isspace ((unsigned char)c))
		{
			flush ();
			++i;
			continue;
		}
		cur += c;
		++i;
	}
	flush ();
	return out;
}

/// True if `s` begins with `prefix` (same as C++20 std::string::starts_with).
bool startsWith (const std::string& s, const char* prefix)
{
	return s.compare (0, std::strlen (prefix), prefix) == 0;
}

/// Options that make no sense inside a plug-in (devices, files, sample rate, channel
/// counts, MIDI devices, buffers): the plug-in sets its own. Returns 1 when the token is
/// dropped, 2 when it and the following argument are dropped, 0 to keep it.
/// Matching is by prefix, so "-odac", "-o dac.wav", "--output=dac" and "-+rtaudio=pa" are
/// all caught. Everything not listed (e.g. -m, -d, --midi-key, --omacro, --smacro, -+...
/// opcode-specific settings) is kept and passed to csoundSetOption.
int dropOption (const std::string& t)
{
	// Short flags that take their argument as the next token when written alone.
	static const char* withArg[] = {"-o", "-i", "-r", "-b", "-B", "-M", "-F", "-Q", "-L", "-U", "-+rtaudio", "-+rtmidi"};
	for (auto* a : withArg)
		if (t == a)
			return 2;
	// -o output, -i input, -r sample rate, -b/-B buffer sizes, -M MIDI in device, -F MIDI
	// file, -Q MIDI out device, -L line events, -U utility, -n no sound (the plug-in sets
	// its own -n), followed by the long forms and real-time/daemon/utility modes.
	static const char* prefixes[] = {
	    "-o", "-i", "-r", "-b", "-B", "-M", "-F", "-Q", "-L", "-U", "-n",
	    "-+rtaudio", "-+rtmidi", "-+mute_tracks", "-+raw_controller_mode",
	    "--output", "--input", "--nosound", "--sample-rate", "--srate", "--nchnls",
	    "--iobufsamps", "--hardwarebufsamps", "--midi-device", "--midifile", "--midioutfile",
	    "--realtime", "--sched", "--daemon", "--port", "--udp", "--utility", "--devices",
	    "--midi-devices", "--get-system-sr", "--use-system-sr", "--syntax-check-only",
	};
	for (auto* p : prefixes)
		if (startsWith (t, p))
			return 1;
	return 0;
}

} // namespace

/// Rewrites a CSD (or bare orchestra) for plug-in use:
///   0. bare orchestra text (no <CsoundSynthesizer>/<CsInstruments>) is wrapped in a CSD;
///   1. <CsOptions> is tokenised; harmless options are returned in userOptions, the rest in
///      droppedOptions, and the section body is emptied;
///   2. "f0 z" is inserted at the top of <CsScore> (or a score is added) so performance
///      never ends on its own.
/// Emptying <CsOptions> matters because csoundCompileCSD is called before csoundStart,
/// and in that order Csound honours the CSD's <CsOptions> element - it would otherwise
/// re-apply the very options (e.g. -odac) that were filtered out.
/// Only the first <CsOptions>...</CsOptions> pair is processed. Tag matching is
/// case-sensitive, as in Csound.
std::string prepareCsd (const std::string& input, std::vector<std::string>& userOptions,
                        std::vector<std::string>& droppedOptions)
{
	std::string csd = input;
	userOptions.clear ();
	droppedOptions.clear ();

	// Plain orchestra text (no CSD tags): wrap it.
	if (csd.find ("<CsoundSynthesizer>") == std::string::npos &&
	    csd.find ("<CsInstruments>") == std::string::npos)
	{
		csd = "<CsoundSynthesizer>\n<CsInstruments>\n" + csd +
		      "\n</CsInstruments>\n<CsScore>\n</CsScore>\n</CsoundSynthesizer>\n";
	}

	// 1. <CsOptions>: keep the harmless options (e.g. --midi-key, --omacro), drop the rest,
	//    then remove the section so it cannot override the plug-in's own options.
	size_t o1 = csd.find ("<CsOptions>");
	size_t o2 = o1 == std::string::npos ? o1 : csd.find ("</CsOptions>", o1);
	if (o1 != std::string::npos && o2 != std::string::npos)
	{
		size_t bodyStart = o1 + std::strlen ("<CsOptions>");
		auto tokens = tokenizeOptions (csd.substr (bodyStart, o2 - bodyStart));
		for (size_t i = 0; i < tokens.size (); ++i)
		{
			int d = dropOption (tokens[i]);
			if (d == 0)
				userOptions.push_back (tokens[i]);
			else
			{
				droppedOptions.push_back (tokens[i]);
				if (d == 2 && i + 1 < tokens.size ())
					droppedOptions.push_back (tokens[++i]);
			}
		}
		// The tags stay; only their body is replaced by a single newline.
		csd.erase (bodyStart, o2 - bodyStart);
		csd.insert (bodyStart, "\n");
	}

	// 2. Keep performing forever: a DAW instance must never "finish". Insert "f0 z" (an
	//    infinitely long dummy event) at the start of the score.
	//    "<CsScore" without '>' means a tag with attributes (e.g. <CsScore bin="...">, an
	//    external score generator); such a score is left untouched. A CSD with no score at
	//    all gets a minimal one so it does not end immediately. Explicit "e" statements or
	//    exitnow in the orchestra can still end it (see README, Known limitations).
	size_t s1 = csd.find ("<CsScore>");
	if (s1 != std::string::npos)
		csd.insert (s1 + std::strlen ("<CsScore>"), "\nf0 z\n");
	else if (csd.find ("<CsScore") == std::string::npos)
	{
		size_t end = csd.find ("</CsoundSynthesizer>");
		if (end != std::string::npos)
			csd.insert (end, "<CsScore>\nf0 z\n</CsScore>\n");
	}
	return csd;
}

//------------------------------------------------------------------------------
/// Lexical scan for chnk / chn_k / chnexport followed by a string literal. Recognises both
/// the classic form   chn_k "cutoff", 1, 2, 1000, 20, 20000
/// and the functional form   chnk:i("cutoff", ...)  (optional ":type", spaces, '(').
/// Duplicates are reported once, at their first occurrence. The whole CSD is scanned
/// (not only <CsInstruments>), which is harmless because these words do not appear
/// elsewhere in practice. Strings may contain \" escapes; multi-line {{ }} strings are not
/// treated specially.
std::vector<std::string> declaredChannelNames (const std::string& csd)
{
	// Strip comments first (';' and '//' to end of line, '/* */' blocks), keeping strings.
	// Strings must be copied verbatim so a ';' inside "a;b" is not taken as a comment.
	std::string code;
	code.reserve (csd.size ());
	for (size_t i = 0; i < csd.size ();)
	{
		char c = csd[i];
		if (c == '"')
		{
			// Find the closing quote (skipping backslash escapes); a newline also ends the
			// literal so an unbalanced quote cannot swallow the rest of the file.
			size_t e = i + 1;
			while (e < csd.size () && csd[e] != '"' && csd[e] != '\n')
				e += (csd[e] == '\\' && e + 1 < csd.size ()) ? 2 : 1;
			code.append (csd, i, std::min (e + 1, csd.size ()) - i);
			i = e + 1;
			continue;
		}
		if (c == ';' || (c == '/' && i + 1 < csd.size () && csd[i + 1] == '/'))
		{
			while (i < csd.size () && csd[i] != '\n')
				++i;
			continue;
		}
		if (c == '/' && i + 1 < csd.size () && csd[i + 1] == '*')
		{
			size_t e = csd.find ("*/", i + 2);
			i = e == std::string::npos ? csd.size () : e + 2;
			code += ' '; // keep tokens on either side of the comment separate
			continue;
		}
		code += c;
		++i;
	}

	// Walk the comment-free text word by word; only whole identifiers match, so names such
	// as "mychnk" or "chnk2" are not mistaken for the opcodes.
	std::vector<std::string> names;
	auto isIdent = [] (char ch) { return std::isalnum ((unsigned char)ch) || ch == '_'; };
	for (size_t i = 0; i < code.size ();)
	{
		if (!isIdent (code[i]))
		{
			++i;
			continue;
		}
		size_t start = i;
		while (i < code.size () && isIdent (code[i]))
			++i;
		std::string word = code.substr (start, i - start);
		if (word != "chnk" && word != "chn_k" && word != "chnexport")
			continue;
		size_t j = i;
		// optional ":type" annotation (functional syntax) and '('
		if (j < code.size () && code[j] == ':')
		{
			++j;
			while (j < code.size () && isIdent (code[j]))
				++j;
		}
		while (j < code.size () && (code[j] == ' ' || code[j] == '\t' || code[j] == '('))
			++j;
		if (j < code.size () && code[j] == '"')
		{
			size_t e = code.find ('"', j + 1);
			if (e != std::string::npos)
			{
				std::string name = code.substr (j + 1, e - j - 1);
				if (!name.empty () && std::find (names.begin (), names.end (), name) == names.end ())
					names.push_back (name);
			}
		}
	}
	return names;
}

//==============================================================================
// Build / destroy
//==============================================================================
std::unique_ptr<CsoundInstance> CsoundInstance::build (const BuildParams& params,
                                                       EngineShared& shared, std::string& error)
{
	const CsoundApi& api = CsoundApi::get ();
	if (!api.ok)
	{
		error = api.status;
		shared.log.line ("[Csound Visual] " + api.status);
		return nullptr;
	}

	// From here on, every early `return nullptr` destroys `inst`, whose destructor calls
	// csoundDestroy: no Csound instance can leak on a failed build.
	std::unique_ptr<CsoundInstance> inst (new CsoundInstance ());
	inst->api = &api;
	inst->shared = &shared;
	inst->fxInput = params.fxInput;

	// hostData = this instance, so the static callbacks can find it (csoundGetHostData).
	inst->cs = api.Create (inst.get (), api.pluginsDirUtf8.c_str ());
	if (!inst->cs)
	{
		error = "csoundCreate returned NULL";
		shared.log.line ("[Csound Visual] " + error);
		return nullptr;
	}
	CSOUND* cs = inst->cs;
	// Install the message callback first so compile errors end up in the log (and in the
	// "load" reply) instead of the host's invisible stdout.
	api.SetMessageStringCallback (cs, &CsoundInstance::message);
	// The plug-in feeds spin and reads spout itself; Csound must not open audio devices.
	api.SetHostAudioIO (cs);
	// MIDI comes from the DAW through the callbacks below.
	api.SetHostMIDIIO (cs);
	api.SetExternalMidiInOpenCallback (cs, &CsoundInstance::midiOpen);
	api.SetExternalMidiReadCallback (cs, &CsoundInstance::midiRead);
	api.SetExternalMidiInCloseCallback (cs, &CsoundInstance::midiClose);

	std::vector<std::string> userOptions, dropped;
	std::string text = prepareCsd (params.csd, userOptions, dropped);
	if (!dropped.empty ())
	{
		std::string d;
		for (auto& t : dropped)
			d += " " + t;
		shared.log.line ("[Csound Visual] CsOptions ignored inside the plug-in:" + d);
	}
	// csoundSetOption splits on spaces, so every option is passed on its own.
	for (auto& opt : userOptions)
		api.SetOption (cs, opt.c_str ());

	// The plug-in's own options come last so they override anything the user kept:
	//   -n             no sound file/device output (audio is taken from spout by the host)
	//   -d             no graphic displays (ftable windows) inside the DAW
	//   -m0            minimal message level (keeps the audio thread quiet)
	//   -M0            enable MIDI input on "device 0"; with SetHostMIDIIO this simply
	//                  activates the external MIDI callbacks above
	//   --nchnls=2 / --nchnls_i=2   stereo out / stereo in, matching the plug-in busses;
	//                  command-line values override the orchestra header
	//   --sample-rate  the host's rate, so sr always matches (kr = sr / ksmps follows; the
	//                  orchestra's own ksmps is kept)
	char srOpt[64];
	std::snprintf (srOpt, sizeof (srOpt), "--sample-rate=%.0f", params.sampleRate);
	const char* own[] = {"-n", "-d", "-m0", "-M0", "--nchnls=2", "--nchnls_i=2", srOpt};
	for (auto* opt : own)
		api.SetOption (cs, opt);

	// mode 1 = `text` is the CSD itself (not a file name); async 0 = compile synchronously
	// on this thread. Called before csoundStart, so the score is pre-processed normally.
	int r = api.CompileCSD (cs, text.c_str (), 1, 0);
	if (r != 0)
	{
		error = "Csound compilation failed (code " + std::to_string (r) + ")";
		shared.log.line ("[Csound Visual] " + error);
		return nullptr; // destructor destroys the CSOUND instance
	}
	r = api.Start (cs);
	if (r != 0)
	{
		error = "csoundStart failed (code " + std::to_string (r) + ")";
		shared.log.line ("[Csound Visual] " + error);
		return nullptr;
	}

	// Engine properties are fixed from csoundStart on; cache them so the audio thread and
	// the IPC "status" reply never need to call into Csound for them.
	inst->sr = api.GetSr (cs);
	inst->ksmps = api.GetKsmps (cs);
	inst->nchnlsOut = api.GetChannels (cs, 0);
	inst->nchnlsIn = api.GetChannels (cs, 1);
	inst->zeroDbfs = api.Get0dBFS (cs);
	if (!(inst->zeroDbfs > 0)) // also catches NaN; avoids dividing by zero below
		inst->zeroDbfs = 1;
	inst->invZeroDbfs = 1.0 / inst->zeroDbfs;
	inst->spin = api.GetSpin (cs);
	inst->spout = api.GetSpout (cs);
	if (inst->ksmps == 0 || inst->nchnlsOut == 0 || !inst->spout)
	{
		error = "Csound reported an invalid configuration (ksmps/nchnls)";
		shared.log.line ("[Csound Visual] " + error);
		return nullptr;
	}
	// Should not happen (--sample-rate was forced); if it does, audio plays at the wrong
	// pitch/speed but keeps working, so only warn.
	if (std::fabs (inst->sr - params.sampleRate) > 0.5)
		shared.log.line ("[Csound Visual] warning: Csound sr (" + std::to_string (inst->sr) +
		                 ") differs from the host sample rate (" +
		                 std::to_string (params.sampleRate) + ")");

	// --- control channels -------------------------------------------------------------
	// Keep only k-rate control channels the orchestra reads (INPUT direction); audio,
	// string and pvs channels and output-only channels cannot be parameters. HOST_* are
	// reserved for transport info written by the plug-in itself.
	std::vector<ChannelDesc> listed;
	ControlChannelInfo* list = nullptr;
	int n = api.ListChannels (cs, &list);
	for (int i = 0; i < n && list; ++i)
	{
		const auto& ci = list[i];
		if (!ci.name)
			continue;
		if ((ci.type & CSOUND_CHANNEL_TYPE_MASK) != CSOUND_CONTROL_CHANNEL)
			continue;
		if (!(ci.type & CSOUND_INPUT_CHANNEL))
			continue;
		std::string name = ci.name;
		if (startsWith (name, "HOST_"))
			continue;
		ChannelDesc d;
		d.name = name;
		d.behav = ci.hints.behav;
		if (ci.hints.behav != CSOUND_CONTROL_CHANNEL_NO_HINTS)
		{
			d.min = ci.hints.min;
			d.max = ci.hints.max;
			d.dflt = ci.hints.dflt;
		}
		else
		{
			// No hints (e.g. a channel only created by chnget): treat as a 0..1 value.
			d.min = 0;
			d.max = 1;
			d.dflt = 0;
		}
		listed.push_back (d);
	}
	// The list (and the name strings in it) belongs to Csound; names were copied above.
	if (list)
		api.DeleteChannelList (cs, list);

	// Declared order first (chnk / chn_k / chnexport in source order), then the rest by name.
	// csoundListChannels returns channels in an internal (hash) order, which would make the
	// parameter slots shuffle between edits; the author's declaration order is stable and
	// meaningful. The original text (params.csd) is scanned, not the preprocessed one.
	auto declared = declaredChannelNames (params.csd);
	std::vector<ChannelDesc> ordered;
	for (auto& name : declared)
	{
		auto it = std::find_if (listed.begin (), listed.end (),
		                        [&] (const ChannelDesc& c) { return c.name == name; });
		if (it != listed.end ())
		{
			ordered.push_back (*it);
			listed.erase (it);
		}
	}
	std::sort (listed.begin (), listed.end (),
	           [] (const ChannelDesc& a, const ChannelDesc& b) { return a.name < b.name; });
	for (auto& c : listed)
		ordered.push_back (c);

	// Cache each channel's storage pointer now: csoundGetChannelPtr may allocate and takes
	// Csound's internal lock, so it must never be called from the audio thread. The pointers
	// stay valid until csoundDestroy (channels are never freed during performance).
	for (auto& c : ordered)
	{
		void* p = nullptr;
		if (api.GetChannelPtr (cs, &p, c.name.c_str (), CSOUND_CONTROL_CHANNEL | CSOUND_INPUT_CHANNEL) == 0)
			c.ptr = static_cast<double*> (p);
	}
	// Map the first kNumSlots channels that have storage to parameter slots 0..15.
	inst->channels = std::move (ordered);
	inst->slotChannel.fill (-1);
	for (int i = 0, slot = 0; i < (int)inst->channels.size () && slot < kNumSlots; ++i)
		if (inst->channels[i].ptr)
			inst->slotChannel[slot++] = i;

	// Host transport channels (created here so the audio thread never allocates).
	// GetChannelPtr creates a channel that the orchestra does not mention yet, so
	// `chnget "HOST_BPM"` always works. They get sensible initial values until the first
	// writeHostInfo() (HOST_SR = sr, HOST_BPM = 120).
	auto hostChannel = [&] (const char* name) -> double* {
		void* p = nullptr;
		if (api.GetChannelPtr (cs, &p, name, CSOUND_CONTROL_CHANNEL | CSOUND_INPUT_CHANNEL) != 0)
			return nullptr;
		return static_cast<double*> (p);
	};
	inst->hostBpm = hostChannel ("HOST_BPM");
	inst->hostPlaying = hostChannel ("HOST_PLAYING");
	inst->hostPpq = hostChannel ("HOST_PPQ");
	inst->hostSr = hostChannel ("HOST_SR");
	if (inst->hostSr)
		storeChannel (inst->hostSr, inst->sr);
	if (inst->hostBpm)
		storeChannel (inst->hostBpm, 120);

	char banner[200];
	std::snprintf (banner, sizeof (banner),
	               "[Csound Visual] running: sr = %.0f, ksmps = %u, nchnls = %u, nchnls_i = %u, 0dbfs = %g, %d control channel(s)",
	               inst->sr, inst->ksmps, inst->nchnlsOut, inst->nchnlsIn, inst->zeroDbfs,
	               (int)inst->channels.size ());
	shared.log.line (banner);
	return inst;
}

/// Destroys the Csound instance. Runs on the thread that drops the last unique_ptr - the
/// loading/UI thread by design, never the audio thread (csoundDestroy frees memory and
/// unloads per-instance state). The audio thread cannot be using this instance any more:
/// it was unpublished under swapMutex before being destroyed.
CsoundInstance::~CsoundInstance ()
{
	if (cs && api)
		api->Destroy (cs);
	cs = nullptr;
}

/// Linear search; channel lists are short (tens of entries) and this is not called on the
/// audio thread.
int CsoundInstance::findChannel (const std::string& name) const
{
	for (size_t i = 0; i < channels.size (); ++i)
		if (channels[i].name == name)
			return (int)i;
	return -1;
}

//==============================================================================
// Audio thread
//==============================================================================
// std::atomic_ref (C++20) gives atomic access to Csound's plain `double` storage without
// changing its type. Relaxed ordering is enough: each channel is an independent value and
// nothing else is published through it. Csound allocates channel storage 8-byte aligned,
// as atomic_ref<double> requires.
void CsoundInstance::storeChannel (double* ptr, double v)
{
	if (ptr)
		std::atomic_ref<double> (*ptr).store (v, std::memory_order_relaxed);
}

double CsoundInstance::loadChannel (const double* ptr)
{
	if (!ptr)
		return 0;
	return std::atomic_ref<double> (*const_cast<double*> (ptr)).load (std::memory_order_relaxed);
}

/// Four relaxed 8-byte stores per block; HOST_SR is rewritten every time so an orchestra
/// that writes to it (chnset) cannot leave a wrong value behind.
void CsoundInstance::writeHostInfo (const HostInfo& info)
{
	storeChannel (hostBpm, info.bpm);
	storeChannel (hostPlaying, info.playing);
	storeChannel (hostPpq, info.ppq);
	storeChannel (hostSr, sr);
}

/// Converts one output sample (already scaled to host full scale) to float.
static inline float sanitize (double x)
{
	// Replaces NaN/Inf (and absurd values) with silence so a broken orchestra cannot
	// blow up the host's mixer. |x| < 64 (about +36 dBFS) passes unchanged, so ordinary
	// overs are left for the host to meter/limit; NaN fails the comparison and becomes 0.
	return (std::fabs (x) < 64.0) ? (float)x : 0.0f;
}

/// Per-frame spin/spout exchange. For each host frame f in [start, end):
///   1. (FX only) write the host input frame into spin[pos], scaled from -1..1 to 0dbfs;
///   2. read spout[pos] (the frame Csound computed during the previous control cycle),
///      scale it to -1..1 and write it to the host output;
///   3. advance pos; after ksmps frames, run csoundPerformKsmps, which consumes the ksmps
///      input frames just collected and refills spout with ksmps new output frames.
/// Hence output lags input by exactly ksmps frames, independently of the host block size,
/// and the first ksmps frames after build are the zero-initialised spout (silence).
/// Mono orchestras (nchnls = 1, if Csound ever reports it) are duplicated to both sides;
/// for the FX, a mono host input feeds every Csound input channel.
void CsoundInstance::renderRange (const float* const* in, int numIn, float* const* out, int numOut,
                                  int start, int end)
{
	usedFlag.store (true, std::memory_order_relaxed);
	if (done.load (std::memory_order_relaxed)) // atomic: also read by the IPC thread
	{
		// Performance already ended: keep producing silence until a new instance arrives.
		for (int c = 0; c < numOut; ++c)
			if (out[c])
				std::memset (out[c] + start, 0, sizeof (float) * (size_t)(end - start));
		return;
	}
	const uint32_t nchO = nchnlsOut;
	const uint32_t nchI = nchnlsIn;
	for (int f = start; f < end; ++f)
	{
		// spin/spout are interleaved: frame `pos`, channel c is at [pos * nch + c].
		if (fxInput && spin && nchI > 0)
		{
			csfloat* dst = spin + (size_t)pos * nchI;
			for (uint32_t c = 0; c < nchI; ++c)
			{
				const float* src = (numIn > 0 && in) ? in[std::min<int> ((int)c, numIn - 1)] : nullptr;
				dst[c] = src ? (csfloat)src[f] * zeroDbfs : 0.0;
			}
		}
		const csfloat* frame = spout + (size_t)pos * nchO;
		double l = frame[0] * invZeroDbfs;
		double r = nchO > 1 ? frame[1] * invZeroDbfs : l;
		if (numOut > 0 && out[0])
			out[0][f] = sanitize (l);
		if (numOut > 1 && out[1])
			out[1][f] = sanitize (r);

		if (++pos >= ksmps)
		{
			// One full control cycle collected: let Csound compute the next one. This is
			// where MIDI (midiRead), channel values and score events are consumed.
			pos = 0;
			int res = api->PerformKsmps (cs);
			if (res != 0)
			{
				// Non-zero = score finished (despite "f0 z": e.g. an explicit "e"/exitnow) or a
				// performance error. Report it through the lock-free FIFO (no log mutex on the
				// audio thread) and silence the rest of this range.
				done.store (true, std::memory_order_relaxed); // read by finished() on the IPC thread
				static const char msg[] = "[Csound Visual] performance ended (score finished or error); output is silent until the next load.\n";
				shared->audioThreadText.push (reinterpret_cast<const unsigned char*> (msg), sizeof (msg) - 1);
				for (int c = 0; c < numOut; ++c)
					if (out[c] && f + 1 < end)
						std::memset (out[c] + f + 1, 0, sizeof (float) * (size_t)(end - f - 1));
				return;
			}
		}
	}
}

} // namespace CsoundVisual
