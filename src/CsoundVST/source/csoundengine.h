//------------------------------------------------------------------------------
// Csound Visual VST3 - one compiled, running Csound instance
//
// A CsoundInstance is built completely on a non-audio thread (create, set
// options, compile, start, list channels, pre-create the HOST_* channels), and
// only then handed to the audio thread. Once published, the audio thread is the
// only thread that calls renderRange() / queueMidi() / writeHostInfo(); other
// threads may only read the immutable description (sr, ksmps, channel list) and
// store channel values through the cached channel pointers (8-byte aligned
// atomic stores).
//
// Lifetime / hand-off (implemented in plugin.cpp, summarised here):
//   1. IPC or UI thread: CsoundInstance::build() -> fully started instance.
//   2. The new unique_ptr is swapped into the plug-in under a mutex that the
//      audio thread only ever try_locks (on contention it outputs one block of
//      silence instead of waiting).
//   3. The old instance is destroyed on the loading thread, never on the audio
//      thread, because csoundDestroy frees memory and may take a while.
//
// Audio is exchanged with the classic spin/spout scheme: every host frame is
// written to spin and read from spout at position `pos`; when ksmps frames have
// been accumulated csoundPerformKsmps() runs. This works for any host block
// size and adds a constant latency of exactly ksmps frames (reported to the
// host through IAudioProcessor::getLatencySamples).
//
// Units used throughout: sample rate in Hz, ksmps and positions in frames (one
// frame = one sample per channel), host audio as float in -1..1 full scale,
// Csound audio as double in -0dbfs..+0dbfs, plug-in parameters normalised 0..1,
// channel values in the orchestra's own units (Hz, seconds, ...).
//
// This file also declares the CSD preprocessing helpers (prepareCsd,
// declaredChannelNames) that adapt a stand-alone orchestra to plug-in use.
//------------------------------------------------------------------------------
#pragma once

#include "csoundapi.h"
#include "messagelog.h"

#include <array>
#include <atomic>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

namespace CsoundVisual {

/// Number of generic automatable parameters exposed by the plug-in. The first kNumSlots
/// input control channels (in CsoundInstance::channels order) are mapped to them.
constexpr int kNumSlots = 16;

/// Description of one input control channel of the orchestra (a k-rate channel the
/// orchestra reads, e.g. declared with chnk "cutoff", "i" or created by chnget).
/// Copyable value type; `ptr` is non-owning.
struct ChannelDesc
{
	std::string name;                           ///< channel name as used in the orchestra
	int behav = CSOUND_CONTROL_CHANNEL_NO_HINTS; ///< CSOUND_CONTROL_CHANNEL_* range interpretation
	double dflt = 0, min = 0, max = 1;          ///< default and range in channel units (0/0..1 without hints)
	double* ptr = nullptr; ///< Csound's storage for the channel value (valid while the instance lives)

	/// Maps a normalized 0..1 value to the channel range (exponential for EXP hints).
	/// Input is clamped to 0..1; INT channels are rounded to whole numbers; a channel with
	/// no usable range (max <= min) returns the normalised value unchanged.
	double fromNormalized (double n) const;
	/// Inverse of fromNormalized, clamped to 0..1.
	double toNormalized (double v) const;
};

/// Lock-free single-producer/single-consumer byte FIFO (capacity must be a power of two).
/// Used for MIDI (producer: process(), consumer: Csound's MIDI read callback, both on the
/// audio thread) and for Csound messages emitted on the audio thread (consumer: whichever
/// non-audio thread calls drainAudioThreadMessages(), usually the IPC thread).
///
/// Implementation: writePos and readPos are free-running counters (they are never
/// wrapped; size_t overflow is harmless because only their difference is used, and that
/// is always <= Capacity). The slot index is counter & (Capacity - 1). The producer
/// publishes data with a release store of writePos, the consumer frees space with a
/// release store of readPos; each side acquires the other's counter. No locks, no
/// allocation, bounded time: safe on the audio thread. Exactly one producer thread and one
/// consumer thread may use it at a time.
template <size_t Capacity>
class ByteFifo
{
	static_assert ((Capacity & (Capacity - 1)) == 0, "Capacity must be a power of two");

public:
	/// Writes all bytes or nothing. Returns false when there is not enough room.
	/// All-or-nothing keeps MIDI messages and text fragments from being split: a dropped
	/// message is lost entirely instead of corrupting the byte stream. Producer side only.
	bool push (const unsigned char* data, size_t n)
	{
		size_t w = writePos.load (std::memory_order_relaxed); // own counter: relaxed is enough
		size_t r = readPos.load (std::memory_order_acquire);  // pairs with pop()'s release
		if (Capacity - (w - r) < n) // free space = Capacity - used
			return false;
		for (size_t i = 0; i < n; ++i)
			buffer[(w + i) & (Capacity - 1)] = data[i];
		writePos.store (w + n, std::memory_order_release); // publish the bytes written above
		return true;
	}
	/// Reads up to max bytes; returns the number read (0 when empty). Consumer side only.
	size_t pop (unsigned char* out, size_t max)
	{
		size_t r = readPos.load (std::memory_order_relaxed);  // own counter
		size_t w = writePos.load (std::memory_order_acquire); // pairs with push()'s release
		size_t n = w - r; // bytes available
		if (n > max)
			n = max;
		for (size_t i = 0; i < n; ++i)
			out[i] = buffer[(r + i) & (Capacity - 1)];
		readPos.store (r + n, std::memory_order_release); // hand the slots back to the producer
		return n;
	}
	/// Consumer-side reset (drops everything currently queued). Must only be called by the
	/// consumer thread, like pop(). (Currently unused by the plug-in.)
	void clear () { readPos.store (writePos.load (std::memory_order_acquire), std::memory_order_release); }

private:
	std::array<unsigned char, Capacity> buffer {}; ///< storage, inline (no heap allocation)
	std::atomic<size_t> writePos {0}; ///< total bytes ever written (producer-owned)
	std::atomic<size_t> readPos {0};  ///< total bytes ever read (consumer-owned)
};

/// Objects shared by all Csound instances of one plug-in instance. Owned by the plug-in
/// object and outlives every CsoundInstance it builds (each instance keeps a raw pointer).
/// The C callbacks reach it through the instance: csoundCreate's hostData is the
/// CsoundInstance*, whose `shared` member points here.
struct EngineShared
{
	MessageLog log;                  ///< last ~200 lines (status reply)
	ByteFifo<16384> audioThreadText; ///< messages produced on the audio thread (lock-free), 16 KiB
};

/// Host transport information written into the HOST_* channels every block.
struct HostInfo
{
	double bpm = 120;   ///< tempo in beats per minute
	double playing = 0; ///< 1 while the host transport is running, else 0
	double ppq = 0;     ///< musical position in quarter notes since the project start
};

/// Build request for CsoundInstance::build().
struct BuildParams
{
	std::string csd;   ///< full CSD text
	double sampleRate = 48000; ///< host sample rate in Hz; forced on Csound with --sample-rate
	bool fxInput = false; ///< true for the FX plug-in (stereo input into spin)
};

/// One compiled, started Csound engine (one CSOUND*) plus everything needed to drive it
/// from a VST3 process() call. Non-copyable in practice (owns the CSOUND*; created only
/// through build() and held by std::unique_ptr). Destroying it calls csoundDestroy, so it
/// must be destroyed on a non-audio thread.
class CsoundInstance
{
public:
	/// Destroys the CSOUND instance (if any). Never call on the audio thread.
	~CsoundInstance ();

	/// Creates and starts a new instance. Never call on the audio thread. On failure returns
	/// nullptr and fills error; Csound's own messages go to shared.log in both cases.
	/// Steps: csoundCreate, install callbacks and host I/O, preprocess the CSD (prepareCsd),
	/// apply kept user options then the plug-in's own options, compile, start, read the
	/// engine properties, enumerate and order the input control channels, cache their
	/// pointers and pre-create the HOST_* channels.
	/// @param params CSD text, host sample rate and instrument/FX mode
	/// @param shared log + FIFO of the owning plug-in; must outlive the returned instance
	/// @param error  receives a one-line reason on failure (unchanged on success)
	/// @return a running instance, or nullptr on failure
	static std::unique_ptr<CsoundInstance> build (const BuildParams& params, EngineShared& shared,
	                                              std::string& error);

	/// Renders host frames [start, end) of the current block. in/out are host channel
	/// pointer arrays (numIn may be 0 and `in` null for the instrument; individual out[c]
	/// may be null and are skipped). Only out[0] and out[1] are written (stereo).
	/// MIDI bytes must have been queued with queueMidi() before rendering the frame they
	/// belong to: the caller splits the block at each event offset and calls renderRange()
	/// for each piece, so events land on the next control-cycle boundary after their
	/// offset (control-rate accuracy, not sample accuracy).
	/// Audio thread only. Never blocks, never allocates.
	void renderRange (const float* const* in, int numIn, float* const* out, int numOut, int start,
	                  int end);

	/// Queues one MIDI message (1..3 bytes) for the next control cycle. Audio thread only.
	/// Silently dropped when the 8 KiB FIFO is full (only possible with a flood of events
	/// within one cycle).
	void queueMidi (const unsigned char* bytes, int n) { midi.push (bytes, (size_t)n); }

	/// Writes host transport info into the HOST_* channels (HOST_BPM, HOST_PLAYING,
	/// HOST_PPQ, and HOST_SR = Csound's sr). Audio thread, once per block.
	void writeHostInfo (const HostInfo& info);

	/// Writes a value to a control channel through its cached pointer (any thread while
	/// the instance is alive). Uses an 8-byte relaxed atomic store via std::atomic_ref, so
	/// Csound (reading the same double on the audio thread) never sees a torn value; on x64
	/// an aligned 8-byte store is a single plain instruction, so this costs nothing.
	/// The new value is picked up at Csound's next control cycle. ptr == nullptr is ignored.
	static void storeChannel (double* ptr, double v);
	/// Reads a control channel through its cached pointer (relaxed atomic load).
	/// Returns 0 for ptr == nullptr.
	static double loadChannel (const double* ptr);

	// --- immutable description (valid after build) ---------------------------------
	double sr = 0;            ///< Csound's sample rate in Hz (should equal the host rate)
	uint32_t ksmps = 0;       ///< frames per control cycle = plug-in latency in frames
	uint32_t nchnlsOut = 0;   ///< interleaved channels in spout (forced to 2 by options)
	uint32_t nchnlsIn = 0;    ///< interleaved channels in spin (forced to 2 by options)
	double zeroDbfs = 1;      ///< Csound amplitude of digital full scale (0dbfs), > 0
	/// All input control channels, declared ones first in declaration order.
	/// Channels named HOST_* are excluded (they are written by the plug-in).
	std::vector<ChannelDesc> channels;
	/// For each parameter slot: index into channels, or -1 when unused.
	/// Filled with the first kNumSlots channels whose pointer could be obtained.
	std::array<int, kNumSlots> slotChannel {};

	/// True after the score ended or a performance error (the instance then outputs silence).
	/// Written by the audio thread; read by the IPC thread for the "status" reply.
	/// `done` is a std::atomic<bool> because it crosses threads (audio thread writes, IPC
	/// thread reads). A plain bool here was a data race (undefined behaviour in C++, and the
	/// compiler was free to cache the value); relaxed ordering is enough because the flag
	/// publishes no other data - the reader only reports it.
	bool finished () const { return done.load (std::memory_order_relaxed); }
	/// True once renderRange() was called at least once (the instance is no longer "fresh").
	/// plugin.cpp uses this to decide whether a freshly built instance can be reused as-is
	/// (e.g. by setActive) or must be rebuilt so the score starts from time 0.
	bool used () const { return usedFlag.load (std::memory_order_relaxed); }

	/// Finds a channel by name (exact, case-sensitive); returns -1 when it does not exist.
	int findChannel (const std::string& name) const;

private:
	/// Private: instances only come from build(), which guarantees a fully started engine.
	CsoundInstance () = default;

	// --- Csound C callbacks (static: Csound calls plain C function pointers) ----------
	/// MIDI "device open": stores the CsoundInstance* (hostData) as the device user data.
	static int32_t midiOpen (CSOUND*, void** userData, const char*);
	/// MIDI read: drains up to nBytes from the `midi` FIFO. Runs inside csoundPerformKsmps,
	/// i.e. on the audio thread, same thread as the producer.
	static int32_t midiRead (CSOUND*, void* userData, unsigned char* buf, int32_t nBytes);
	/// MIDI "device close": nothing to release.
	static int32_t midiClose (CSOUND*, void*);
	/// Message callback: routes Csound's text either to the lock-free FIFO (audio thread)
	/// or directly to the locked MessageLog (any other thread).
	static void message (CSOUND*, int32_t attr, const char* str);

	const CsoundApi* api = nullptr;  ///< process-wide function table (never null after build)
	CSOUND* cs = nullptr;            ///< owned Csound instance
	csfloat* spin = nullptr;         ///< Csound's input buffer: ksmps * nchnlsIn doubles, interleaved
	const csfloat* spout = nullptr;  ///< Csound's output buffer: ksmps * nchnlsOut doubles, interleaved
	double invZeroDbfs = 1;          ///< 1 / zeroDbfs, to scale Csound output to host -1..1
	uint32_t pos = 0;                ///< current frame inside spin/spout, 0..ksmps-1 (audio thread)
	/// Set once csoundPerformKsmps reported the end. Written on the audio thread, read on the
	/// IPC thread (finished()), hence atomic; all accesses are relaxed (see finished()).
	std::atomic<bool> done {false};
	bool fxInput = false;            ///< copy host input into spin (FX plug-in)
	std::atomic<bool> usedFlag {false}; ///< see used()
	ByteFifo<8192> midi;             ///< raw MIDI bytes waiting for the next control cycle
	double* hostBpm = nullptr;       ///< HOST_BPM channel storage
	double* hostPlaying = nullptr;   ///< HOST_PLAYING channel storage
	double* hostPpq = nullptr;       ///< HOST_PPQ channel storage
	double* hostSr = nullptr;        ///< HOST_SR channel storage
	EngineShared* shared = nullptr;  ///< owning plug-in's log and FIFO (not owned)
};

/// Marks the calling thread as an audio thread for the message callback: messages Csound
/// prints while this flag is set go to the lock-free FIFO instead of the locked log.
/// The flag is thread_local; process() sets it on entry and clears it on exit (RAII scope
/// in plugin.cpp), because hosts may call process() from different worker threads.
void setAudioThreadFlag (bool isAudioThread);

/// Moves messages captured on the audio thread into the log (call from a non-audio thread).
/// Serialised by an internal mutex so concurrent callers keep the FIFO single-consumer.
/// Called before compiling and before building a "status" reply, so the log is current.
void drainAudioThreadMessages (EngineShared& shared);

/// Preprocesses a CSD for use inside the plug-in (see csoundengine.cpp for details).
/// Returns the text to compile; userOptions receives the CsOptions tokens that were kept.
/// droppedOptions receives the tokens (and their arguments) that were removed because the
/// plug-in controls them itself (audio/MIDI devices, files, sample rate, channel counts).
std::string prepareCsd (const std::string& csd, std::vector<std::string>& userOptions,
                        std::vector<std::string>& droppedOptions);

/// Names of control channels declared with chnk / chn_k / chnexport, in source order.
/// A purely lexical scan (comments stripped, first string literal after the opcode name),
/// used only to give the parameter slots a stable, author-defined order.
std::vector<std::string> declaredChannelNames (const std::string& csd);

} // namespace CsoundVisual
