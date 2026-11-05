//------------------------------------------------------------------------------
// Csound Visual VST3 - named-pipe IPC server and instance descriptor file
//
// Every plug-in instance runs one IpcServer on its own thread:
//   pipe name : \\.\pipe\CsoundVisualVST.<pid>.<instanceCounter>
//   framing   : 4-byte little-endian length + UTF-8 JSON, both directions
//   sessions  : one request per connection; the server writes the reply and
//               waits (bounded) until the client closes its end, so the reply
//               is never discarded by DisconnectNamedPipe.
// A second listening instance of the pipe is created as soon as a client
// connects, so a following client never sees "pipe not found".
//
// The descriptor file %LOCALAPPDATA%\CsoundVisual\vst-instances\<pid>.<n>.json
// tells the Csound Visual app which instances exist (see README.md).
//
// Why a named pipe:
//   * Local-only, no TCP port to pick, allocate or get blocked by a firewall;
//     PIPE_REJECT_REMOTE_CLIENTS refuses connections from other machines.
//   * Default pipe security (null SECURITY_ATTRIBUTES): only the creating user,
//     SYSTEM and administrators get write access, so other user accounts cannot
//     send orchestras to the plug-in.
//   * Works identically in every DAW, needs no extra dependency, and .NET's
//     NamedPipeClientStream (src/CsoundVisual/Integration/VstBridge.cs) speaks it.
//
// Message flow (one connection = one round trip):
//   client -> server : uint32 LE length N, then N bytes of UTF-8 JSON request
//   server -> client : uint32 LE length M, then M bytes of UTF-8 JSON reply
//   client closes its end; server disconnects and serves the next connection.
// The request/reply *content* is not interpreted here: the Handler (implemented
// in plugin.cpp) parses the JSON and implements the commands "load", "status",
// "set" and "meters" (documented in README.md, section "IPC protocol").
//
// Threading: start()/stop() are called on the host UI thread (plug-in
// initialize/terminate); run() and therefore the Handler execute on the
// server's own thread, which may block freely (it compiles orchestras) because
// it is neither the UI nor the audio thread. Requests are served strictly one
// at a time per plug-in instance.
//------------------------------------------------------------------------------
#pragma once

#include <atomic>
#include <functional>
#include <string>
#include <thread>

namespace CsoundVisual {

/// Named-pipe request/reply server running on a private thread. One per plug-in instance.
/// Not copyable/movable in practice (owns a thread that captures `this`).
class IpcServer
{
public:
	/// Handles one request (UTF-8 JSON text) and returns the reply (UTF-8 JSON text).
	/// Runs on the IPC thread. The request is passed exactly as received (it is not
	/// validated as UTF-8 or JSON here). Exceptions are caught by the server and turned
	/// into {"ok":false,"error":"internal error"}.
	using Handler = std::function<std::string (const std::string& request)>;

	IpcServer () = default;
	/// Stops the server (joins the thread) if still running.
	~IpcServer () { stop (); }

	/// Starts the server thread. Returns false if the thread could not be created.
	/// Restarts cleanly if already running (calls stop() first).
	/// @param pipeName full pipe path, e.g. L"\\\\.\\pipe\\CsoundVisualVST.1234.1"
	/// @param handler  request handler, called on the IPC thread; must stay callable until
	///                 stop() returns
	bool start (const std::wstring& pipeName, Handler handler);
	/// Stops the thread and closes the pipe (blocks until the thread has exited).
	/// Signals stopEvent, which aborts any pending connect/read/write wait at once; a
	/// Handler call already in progress (e.g. a compile) is allowed to finish first.
	/// Safe to call repeatedly and when never started.
	void stop ();

private:
	/// Thread body: accept loop, one request/reply per connection (see ipcserver.cpp).
	void run ();

	std::wstring name;         ///< pipe path given to start()
	Handler handler;           ///< request handler given to start()
	std::thread thread;        ///< server thread (joinable while running)
	void* stopEvent = nullptr; // HANDLE (manual-reset event; void* keeps <windows.h> out of this header)
};

/// %LOCALAPPDATA%\CsoundVisual\vst-instances (created if needed). Empty on failure.
/// This folder is the rendezvous point with the Csound Visual app, which lists it to find
/// running plug-in instances and their pipe names.
std::wstring descriptorDirectory ();

/// Atomically writes a UTF-8 text file (write to .tmp, then replace).
/// A reader therefore sees either the old or the new complete file, never a partial one.
/// @return true on success; on failure the .tmp file is removed and `path` is unchanged
bool writeTextFileAtomic (const std::wstring& path, const std::string& utf8);

/// Removes descriptor files left behind by processes that no longer exist
/// (e.g. after a host crash). Called once per process.
/// The owning PID is parsed from the file name prefix ("<pid>.<n>.json").
void cleanStaleDescriptors ();

} // namespace CsoundVisual
