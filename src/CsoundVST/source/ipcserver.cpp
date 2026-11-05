//------------------------------------------------------------------------------
// Csound Visual VST3 - named-pipe IPC server and instance descriptor file
//
// Implementation notes:
//   * The pipe is opened with FILE_FLAG_OVERLAPPED so that every blocking step
//     (ConnectNamedPipe, ReadFile, WriteFile) can be waited on together with the
//     stop event. That is what lets IpcServer::stop() end the thread promptly
//     instead of hanging in a synchronous call until a client shows up.
//   * Every read/write wait has a timeout (kIoTimeoutMs), so a client that
//     connects and then stalls cannot block the instance's IPC forever.
//   * Byte-mode pipe (not message mode): message boundaries come from the 4-byte
//     length prefix, which works the same for any client library.
//   * A failure at any step simply drops the connection; the client sees a
//     broken pipe and may retry. The loop then accepts the next client.
//------------------------------------------------------------------------------
#include "ipcserver.h"

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <shlobj.h>

#include <algorithm> // std::min (incremental request read)
#include <cstdint>
#include <vector>

namespace CsoundVisual {

namespace {

constexpr DWORD kBufferSize = 1 << 16;      // 64 KiB: pipe buffer size and max bytes per ReadFile/WriteFile
constexpr uint32_t kMaxMessage = 64u << 20; // 64 MB sanity limit
constexpr DWORD kIoTimeoutMs = 10000;       // per read/write chunk, in milliseconds

/// Waits for an overlapped operation to finish, the stop event, or a timeout.
/// Returns true when the I/O completed successfully; `transferred` gets the byte count.
/// On stop/timeout/wait failure the pending I/O is cancelled and *waited for* before
/// returning: the OVERLAPPED and the data buffer live on the caller's stack, so the
/// kernel must be done with them before the caller's frame goes away.
bool waitIo (HANDLE pipe, OVERLAPPED& ov, HANDLE stopEvent, DWORD timeoutMs, DWORD& transferred)
{
	HANDLE handles[2] = {ov.hEvent, stopEvent};
	DWORD w = WaitForMultipleObjects (2, handles, FALSE, timeoutMs);
	if (w != WAIT_OBJECT_0) // stop event (WAIT_OBJECT_0 + 1), WAIT_TIMEOUT or WAIT_FAILED
	{
		CancelIoEx (pipe, &ov);
		GetOverlappedResult (pipe, &ov, &transferred, TRUE); // wait for the cancellation
		return false;
	}
	return GetOverlappedResult (pipe, &ov, &transferred, FALSE) != FALSE;
}

/// Reads exactly `size` bytes (a pipe read may return fewer than requested), in chunks of
/// at most kBufferSize. `ioEvent` is a manual-reset event reused for every operation.
/// ReadFile on an overlapped handle either completes at once (TRUE) or returns
/// ERROR_IO_PENDING and completes later; both paths end in GetOverlappedResult.
/// @return false on error, stop, timeout, or when the client closed the pipe (0 bytes /
///         ERROR_BROKEN_PIPE) before all bytes arrived
bool readExact (HANDLE pipe, void* data, DWORD size, HANDLE stopEvent, HANDLE ioEvent)
{
	auto* p = static_cast<uint8_t*> (data);
	while (size > 0)
	{
		OVERLAPPED ov {};
		ov.hEvent = ioEvent;
		ResetEvent (ioEvent);
		DWORD got = 0;
		DWORD chunk = size < kBufferSize ? size : kBufferSize;
		if (!ReadFile (pipe, p, chunk, &got, &ov))
		{
			if (GetLastError () != ERROR_IO_PENDING)
				return false;
			if (!waitIo (pipe, ov, stopEvent, kIoTimeoutMs, got))
				return false;
		}
		else if (!GetOverlappedResult (pipe, &ov, &got, FALSE))
			return false;
		if (got == 0)
			return false;
		p += got;
		size -= got;
	}
	return true;
}

/// Writes all `size` bytes, in chunks of at most kBufferSize; same overlapped pattern and
/// failure semantics as readExact(). A write blocks (pending) while the pipe buffer is
/// full, i.e. until the client reads, so large replies are paced by the client.
bool writeAll (HANDLE pipe, const void* data, DWORD size, HANDLE stopEvent, HANDLE ioEvent)
{
	auto* p = static_cast<const uint8_t*> (data);
	while (size > 0)
	{
		OVERLAPPED ov {};
		ov.hEvent = ioEvent;
		ResetEvent (ioEvent);
		DWORD put = 0;
		DWORD chunk = size < kBufferSize ? size : kBufferSize;
		if (!WriteFile (pipe, p, chunk, &put, &ov))
		{
			if (GetLastError () != ERROR_IO_PENDING)
				return false;
			if (!waitIo (pipe, ov, stopEvent, kIoTimeoutMs, put))
				return false;
		}
		else if (!GetOverlappedResult (pipe, &ov, &put, FALSE))
			return false;
		if (put == 0)
			return false;
		p += put;
		size -= put;
	}
	return true;
}

/// Creates one new server instance of the pipe, ready for ConnectNamedPipe.
///   PIPE_ACCESS_DUPLEX          request and reply over the same handle
///   FILE_FLAG_OVERLAPPED        asynchronous I/O (see file header)
///   PIPE_TYPE_BYTE/READMODE_BYTE byte stream; framing is done by the length prefix
///   PIPE_WAIT                   blocking mode (required for overlapped I/O semantics)
///   PIPE_REJECT_REMOTE_CLIENTS  local machine only
///   PIPE_UNLIMITED_INSTANCES    allows the "spare" listening instance next to the one
///                               being served (at most two exist at a time in practice)
///   nullptr security            default DACL: write access for the creating user, SYSTEM
///                               and administrators only
///   FILE_FLAG_FIRST_PIPE_INSTANCE (only when `first` is true) the call fails with
///                               ERROR_ACCESS_DENIED if any instance of this pipe name
///                               already exists. Used for the very first instance created by
///                               run(): the name is predictable (pid + counter), so another
///                               process could pre-create it ("pipe squatting") and our
///                               CreateNamedPipeW would silently become a second instance of
///                               *their* pipe, letting them receive the app's requests (CSD
///                               text). With the flag we refuse to join a foreign pipe; run()
///                               keeps retrying and the app simply cannot connect, which is
///                               the safe failure. The spare instances created later must NOT
///                               use the flag, because our own first instance exists then.
/// @return the pipe handle, or INVALID_HANDLE_VALUE on failure
HANDLE createListeningPipe (const std::wstring& name, bool first)
{
	DWORD openMode = PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED;
	if (first)
		openMode |= FILE_FLAG_FIRST_PIPE_INSTANCE;
	return CreateNamedPipeW (name.c_str (), openMode,
	                         PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
	                         PIPE_UNLIMITED_INSTANCES, kBufferSize, kBufferSize, 0, nullptr);
}

} // namespace

//------------------------------------------------------------------------------
/// Creates the manual-reset stop event, then the thread. The thread reads `name`,
/// `handler` and `stopEvent`, which are all set before it starts and not modified until
/// stop() has joined it.
bool IpcServer::start (const std::wstring& pipeName, Handler h)
{
	stop ();
	name = pipeName;
	handler = std::move (h);
	// Manual reset: once set it stays signalled, so every later wait in run() sees it.
	stopEvent = CreateEventW (nullptr, TRUE, FALSE, nullptr);
	if (!stopEvent)
		return false;
	try
	{
		thread = std::thread ([this] () { run (); });
	}
	catch (...) // std::system_error when the OS refuses to create the thread
	{
		CloseHandle (stopEvent);
		stopEvent = nullptr;
		return false;
	}
	return true;
}

/// Signal, join, then close the event (closing before the join would invalidate the
/// handle the thread is waiting on).
void IpcServer::stop ()
{
	if (stopEvent)
		SetEvent (stopEvent);
	if (thread.joinable ())
		thread.join ();
	if (stopEvent)
		CloseHandle (stopEvent);
	stopEvent = nullptr;
}

//------------------------------------------------------------------------------
/// Accept loop. Each iteration:
///   1. make sure a listening pipe instance exists;
///   2. wait (overlapped, no timeout) until a client connects or stop is signalled;
///   3. immediately create the next listening instance, so the pipe name never vanishes
///      while this client is being served;
///   4. read the length-prefixed request, call the Handler, write the length-prefixed reply;
///   5. wait (bounded) for the client to close, then disconnect and close this instance.
/// Requests are therefore served sequentially; a client arriving meanwhile connects to the
/// spare instance and its request waits in the pipe buffer until step 2 picks it up.
void IpcServer::run ()
{
	HANDLE stop = stopEvent;
	// Two manual-reset events: one for ConnectNamedPipe, one reused for all reads/writes.
	HANDLE connectEvent = CreateEventW (nullptr, TRUE, FALSE, nullptr);
	HANDLE ioEvent = CreateEventW (nullptr, TRUE, FALSE, nullptr);
	HANDLE listening = INVALID_HANDLE_VALUE;
	// True until this thread has successfully created its first pipe instance; that one is
	// created with FILE_FLAG_FIRST_PIPE_INSTANCE (see createListeningPipe) so we never join a
	// pipe name that another process created first.
	bool needFirstInstance = true;

	while (WaitForSingleObject (stop, 0) == WAIT_TIMEOUT) // until stop() signals
	{
		if (listening == INVALID_HANDLE_VALUE)
		{
			listening = createListeningPipe (name, needFirstInstance);
			if (listening != INVALID_HANDLE_VALUE)
				needFirstInstance = false;
			if (listening == INVALID_HANDLE_VALUE)
			{
				// Should not happen; retry later without spinning.
				if (WaitForSingleObject (stop, 500) == WAIT_OBJECT_0)
					break;
				continue;
			}
		}

		// --- wait for a client --------------------------------------------------------
		OVERLAPPED ov {};
		ov.hEvent = connectEvent;
		ResetEvent (connectEvent);
		bool connected = false;
		// Overlapped ConnectNamedPipe has three success shapes: immediate TRUE (rare),
		// ERROR_PIPE_CONNECTED (a client connected between CreateNamedPipe and this call -
		// common for the spare instance), or ERROR_IO_PENDING (wait for a client).
		if (ConnectNamedPipe (listening, &ov))
			connected = true;
		else
		{
			DWORD err = GetLastError ();
			if (err == ERROR_PIPE_CONNECTED)
				connected = true;
			else if (err == ERROR_IO_PENDING)
			{
				DWORD dummy = 0;
				connected = waitIo (listening, ov, stop, INFINITE, dummy);
			}
		}
		// This instance now belongs to the session; `listening` is recreated below.
		HANDLE pipe = listening;
		listening = INVALID_HANDLE_VALUE;
		if (!connected) // stop requested or connect error: discard and loop (re-checks stop)
		{
			CloseHandle (pipe);
			continue;
		}
		// Next clients can connect (and queue) while this one is served.
		// If this fails, the top of the loop retries after the current session.
		// (Never with FILE_FLAG_FIRST_PIPE_INSTANCE: our own instance `pipe` exists right now.)
		listening = createListeningPipe (name, false);

		// --- one request / one reply ------------------------------------------------------
		// Header: request length as uint32 little-endian, assembled byte by byte so the
		// result does not depend on host endianness.
		uint8_t header[4];
		if (readExact (pipe, header, 4, stop, ioEvent))
		{
			uint32_t len = (uint32_t)header[0] | ((uint32_t)header[1] << 8) |
			               ((uint32_t)header[2] << 16) | ((uint32_t)header[3] << 24);
			// Oversized length (garbage or a non-protocol client): drop the connection
			// without replying rather than allocating an absurd buffer.
			if (len <= kMaxMessage)
			{
				// The buffer grows as the data actually arrives, one chunk (<= kBufferSize =
				// 64 KiB) at a time, instead of allocating the full declared length up
				// front. Before, a 4-byte header alone made the plug-in allocate up to 64 MB
				// inside the DAW process even if the client never sent a single body byte.
				// Now a client has to really transfer N bytes to make us hold N bytes, and
				// std::string's geometric growth keeps the copying amortised O(N).
				std::string request;
				request.reserve (len < kBufferSize ? len : kBufferSize);
				bool bodyOk = true;
				while (bodyOk && request.size () < len)
				{
					size_t have = request.size ();
					DWORD chunk = (DWORD)std::min<size_t> (len - have, kBufferSize);
					request.resize (have + chunk);
					bodyOk = readExact (pipe, request.data () + have, chunk, stop, ioEvent);
				}
				if (bodyOk)
				{
					// The handler may take seconds (a "load" compiles an orchestra); the client
					// is simply kept waiting. Any exception becomes an error reply so a bug
					// in a command handler can never kill the IPC thread.
					std::string reply;
					try
					{
						reply = handler ? handler (request) : std::string ("{\"ok\":false}");
					}
					catch (...)
					{
						reply = "{\"ok\":false,\"error\":\"internal error\"}";
					}
					// Reply framing: same uint32 little-endian length prefix, then the bytes.
					uint32_t rl = (uint32_t)reply.size ();
					uint8_t rh[4] = {(uint8_t)rl, (uint8_t)(rl >> 8), (uint8_t)(rl >> 16), (uint8_t)(rl >> 24)};
					if (writeAll (pipe, rh, 4, stop, ioEvent) &&
					    writeAll (pipe, reply.data (), rl, stop, ioEvent))
					{
						// Wait (bounded) for the client to close its end so the reply is read
						// before the instance is disconnected.
						// DisconnectNamedPipe discards any bytes still in the pipe buffer that
						// the client has not read yet, and FlushFileBuffers could block
						// indefinitely on a stuck client. Instead, keep reading until the
						// client closes (ReadFile fails with ERROR_BROKEN_PIPE), any extra
						// bytes are thrown away, each wait is limited to 3 s, and the loop is
						// capped at 1024 iterations so a chatty client cannot hold the thread.
						uint8_t sink[64];
						OVERLAPPED rov {};
						rov.hEvent = ioEvent;
						ResetEvent (ioEvent);
						DWORD got = 0;
						for (int guard = 0; guard < 1024; ++guard)
						{
							if (ReadFile (pipe, sink, sizeof (sink), &got, &rov))
								continue; // client sent extra bytes: ignore
							DWORD err = GetLastError ();
							if (err != ERROR_IO_PENDING)
								break; // ERROR_BROKEN_PIPE: client closed
							if (!waitIo (pipe, rov, stop, 3000, got))
								break;
							ResetEvent (ioEvent);
						}
					}
				}
			}
		}
		// End of session (success or any failure above): this server instance is not
		// reused; the spare `listening` instance takes over.
		DisconnectNamedPipe (pipe);
		CloseHandle (pipe);
	}

	// Stopped: release the spare instance (pending clients get a broken pipe) and events.
	if (listening != INVALID_HANDLE_VALUE)
		CloseHandle (listening);
	CloseHandle (connectEvent);
	CloseHandle (ioEvent);
}

//------------------------------------------------------------------------------
/// Resolves %LOCALAPPDATA% through the Known Folders API (not the environment variable,
/// which a host could have altered) and creates the subfolders with SHCreateDirectoryExW,
/// which builds the whole chain and reports an already existing folder as success-like
/// ERROR_ALREADY_EXISTS / ERROR_FILE_EXISTS.
std::wstring descriptorDirectory ()
{
	PWSTR local = nullptr;
	std::wstring dir;
	if (SUCCEEDED (SHGetKnownFolderPath (FOLDERID_LocalAppData, 0, nullptr, &local)) && local)
		dir = local;
	if (local)
		CoTaskMemFree (local);
	if (dir.empty ())
		return {};
	dir += L"\\CsoundVisual\\vst-instances";
	int r = SHCreateDirectoryExW (nullptr, dir.c_str (), nullptr);
	if (r != ERROR_SUCCESS && r != ERROR_ALREADY_EXISTS && r != ERROR_FILE_EXISTS)
		return {};
	return dir;
}

/// Write-to-temp-then-rename: the app may read the descriptor at any moment, so it must
/// never observe a half-written JSON file. MoveFileExW with
/// REPLACE_EXISTING replaces the target in one step on NTFS; WRITE_THROUGH makes the call
/// return only after the move is flushed to disk. The file is written as raw bytes
/// (no BOM, no newline translation).
bool writeTextFileAtomic (const std::wstring& path, const std::string& utf8)
{
	std::wstring tmp = path + L".tmp";
	// Exclusive (share mode 0) and truncating: a leftover .tmp from an earlier failure
	// is simply overwritten.
	HANDLE f = CreateFileW (tmp.c_str (), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
	                        FILE_ATTRIBUTE_NORMAL, nullptr);
	if (f == INVALID_HANDLE_VALUE)
		return false;
	DWORD written = 0;
	BOOL ok = WriteFile (f, utf8.data (), (DWORD)utf8.size (), &written, nullptr);
	CloseHandle (f);
	if (!ok || written != utf8.size ())
	{
		DeleteFileW (tmp.c_str ());
		return false;
	}
	if (!MoveFileExW (tmp.c_str (), path.c_str (), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
	{
		DeleteFileW (tmp.c_str ());
		return false;
	}
	return true;
}

/// For every *.json file whose name starts with a PID other than ours, checks whether that
/// process still runs and deletes the file if not. Liveness test:
///   * OpenProcess succeeds -> alive unless its handle is already signalled (exited but
///     the process object is still referenced by someone);
///   * OpenProcess fails with ACCESS_DENIED -> the process exists (e.g. elevated or another
///     user's), so keep the file;
///   * any other failure (typically ERROR_INVALID_PARAMETER: no such PID) -> stale.
/// PID reuse can make a stale file look alive; clients must still verify the pipe answers.
/// Files of the current process are never touched (they belong to live instances).
void cleanStaleDescriptors ()
{
	std::wstring dir = descriptorDirectory ();
	if (dir.empty ())
		return;
	WIN32_FIND_DATAW fd;
	HANDLE h = FindFirstFileW ((dir + L"\\*.json").c_str (), &fd);
	if (h == INVALID_HANDLE_VALUE)
		return;
	DWORD self = GetCurrentProcessId ();
	do
	{
		std::wstring file = fd.cFileName;
		// "<pid>.<n>.json": wcstoul stops at the first '.'; 0 means "not a descriptor name".
		DWORD pid = (DWORD)wcstoul (file.c_str (), nullptr, 10);
		if (pid == 0 || pid == self)
			continue;
		bool alive = false;
		if (HANDLE p = OpenProcess (SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid))
		{
			alive = WaitForSingleObject (p, 0) == WAIT_TIMEOUT;
			CloseHandle (p);
		}
		else
			alive = GetLastError () == ERROR_ACCESS_DENIED; // exists but not ours to open
		if (!alive)
			DeleteFileW ((dir + L"\\" + file).c_str ());
	} while (FindNextFileW (h, &fd));
	FindClose (h);
}

} // namespace CsoundVisual
