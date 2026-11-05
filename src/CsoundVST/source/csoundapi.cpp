//------------------------------------------------------------------------------
// Csound Visual VST3 - run-time binding of the Csound 7 C API (implementation)
//
// Finds csound7_64.dll in a short list of candidate installation folders, loads
// it by full path, resolves every function of CsoundApi with GetProcAddress and
// performs the one-time process-wide Csound setup (search-path environment
// variables, csoundInitialize). Also hosts the small Win32 helpers shared by the
// rest of the plug-in (UTF-8 <-> UTF-16 conversion, own module directory).
//
// Everything here runs on the first thread that calls CsoundApi::get() (in
// practice the host UI thread during the first instance's initialize/setActive,
// or the IPC thread); std::call_once makes concurrent first calls safe. Nothing
// in this file is ever called from the audio thread before get() has completed.
//------------------------------------------------------------------------------
#include "csoundapi.h"

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <mutex>
#include <vector>

namespace CsoundVisual {

//------------------------------------------------------------------------------
/// UTF-16 -> UTF-8. Two-pass Win32 idiom: the first call (null output buffer) returns the
/// required byte count, the second converts into a string of exactly that size. Explicit
/// lengths are passed, so the input need not be null-terminated and no terminator is
/// written into the result.
std::string toUtf8 (const std::wstring& s)
{
	if (s.empty ())
		return {};
	int n = WideCharToMultiByte (CP_UTF8, 0, s.data (), (int)s.size (), nullptr, 0, nullptr, nullptr);
	std::string out (n, '\0');
	WideCharToMultiByte (CP_UTF8, 0, s.data (), (int)s.size (), out.data (), n, nullptr, nullptr);
	return out;
}

//------------------------------------------------------------------------------
/// UTF-8 -> UTF-16, same two-pass idiom as toUtf8().
std::wstring fromUtf8 (const std::string& s)
{
	if (s.empty ())
		return {};
	int n = MultiByteToWideChar (CP_UTF8, 0, s.data (), (int)s.size (), nullptr, 0);
	std::wstring out (n, L'\0');
	MultiByteToWideChar (CP_UTF8, 0, s.data (), (int)s.size (), out.data (), n);
	return out;
}

//------------------------------------------------------------------------------
/// Any function inside this DLL works as an address to identify the module.
/// (GetModuleHandle(nullptr) would return the host .exe, not the plug-in DLL.)
static void moduleAnchor () {}

/// Directory of the DLL that contains this code. GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS
/// maps the address of moduleAnchor() to its module; UNCHANGED_REFCOUNT avoids having to
/// FreeLibrary the handle afterwards. GetModuleFileNameW is retried with a doubled buffer
/// while the result is truncated (n == buffer size), so long paths (> MAX_PATH) work.
std::wstring moduleDirectory ()
{
	HMODULE mod = nullptr;
	if (!GetModuleHandleExW (GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
	                             GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
	                         reinterpret_cast<LPCWSTR> (&moduleAnchor), &mod))
		return {};
	std::vector<wchar_t> buf (MAX_PATH);
	for (;;)
	{
		DWORD n = GetModuleFileNameW (mod, buf.data (), (DWORD)buf.size ());
		if (n == 0)
			return {};
		if (n < buf.size ())
		{
			std::wstring path (buf.data (), n);
			auto slash = path.find_last_of (L"\\/");
			return slash == std::wstring::npos ? std::wstring () : path.substr (0, slash);
		}
		buf.resize (buf.size () * 2);
	}
}

//------------------------------------------------------------------------------
/// True if `p` names an existing file (not a directory). Used to skip candidate roots
/// cheaply before LoadLibraryExW, whose failure would cost a full DLL search attempt.
static bool fileExists (const std::wstring& p)
{
	DWORD a = GetFileAttributesW (p.c_str ());
	return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

/// Removes one trailing path component ("a\b\c" -> "a\b").
static std::wstring parentOf (const std::wstring& p)
{
	auto slash = p.find_last_of (L"\\/");
	return slash == std::wstring::npos ? std::wstring () : p.substr (0, slash);
}

//------------------------------------------------------------------------------
/// Returns the candidate installation roots in priority order:
///  1. the copy bundled inside the plug-in: <bundle>\Contents\Resources\Csound7
///  2. %CSOUND_VISUAL_HOME% (either the Csound root itself, a folder with a Csound7
///     subfolder, or the source tree layout <home>\@Source\Csound7 used during development)
///  3. C:\Program Files\Csound7
/// A root is a folder with bin\csound7_64.dll, plugins64\ (opcode libraries) and samples\.
/// The bundled copy wins so that the plug-in always uses the exact Csound build it was
/// tested with, regardless of what else is installed on the machine.
static std::vector<std::wstring> candidateRoots ()
{
	std::vector<std::wstring> roots;
	std::wstring dir = moduleDirectory (); // ...\Contents\x86_64-win
	if (!dir.empty ())
		roots.push_back (parentOf (dir) + L"\\Resources\\Csound7");

	// GetEnvironmentVariableW returns 0 when unset and the required size (>= 4096) when the
	// buffer is too small; both cases skip the variable. Trailing slashes are trimmed so the
	// concatenations below never produce "\\".
	wchar_t env[4096];
	DWORD n = GetEnvironmentVariableW (L"CSOUND_VISUAL_HOME", env, 4096);
	if (n > 0 && n < 4096)
	{
		std::wstring home (env, n);
		while (!home.empty () && (home.back () == L'\\' || home.back () == L'/'))
			home.pop_back ();
		roots.push_back (home);
		roots.push_back (home + L"\\Csound7");
		roots.push_back (home + L"\\@Source\\Csound7");
	}
	roots.push_back (L"C:\\Program Files\\Csound7");
	return roots;
}

//------------------------------------------------------------------------------
/// Resolves one export into a typed function pointer.
/// @param lib     loaded csound7_64.dll
/// @param name    exported C symbol name (e.g. "csoundCreate")
/// @param fn      receives the pointer (nullptr when missing); its type F drives the cast
/// @param missing comma-separated list of unresolved names, appended to on failure so all
///                missing exports are reported at once instead of only the first one
/// @return true when the export was found
template <typename F>
static bool resolve (HMODULE lib, const char* name, F& fn, std::string& missing)
{
	fn = reinterpret_cast<F> (GetProcAddress (lib, name));
	if (!fn)
		missing += std::string (missing.empty () ? "" : ", ") + name;
	return fn != nullptr;
}

//------------------------------------------------------------------------------
/// One-time loader. Steps: (1) try each candidate root until a csound7_64.dll loads,
/// (2) resolve all exports, (3) verify cs_float is double, (4) set Csound's global search
/// paths, (5) csoundInitialize, (6) build the status string. Any failure leaves ok == false
/// with an explanatory `status`; the once_flag is still consumed, so failure is permanent.
/// The library handle is intentionally leaked (see CsoundApi).
const CsoundApi& CsoundApi::get ()
{
	static CsoundApi api;
	static std::once_flag once;
	std::call_once (once, [] () {
		HMODULE lib = nullptr;
		std::wstring usedRoot;
		std::string tried;
		for (const auto& root : candidateRoots ())
		{
			std::wstring dll = root + L"\\bin\\csound7_64.dll";
			tried += "\n  " + toUtf8 (dll);
			if (!fileExists (dll))
				continue;
			// LOAD_WITH_ALTERED_SEARCH_PATH: dependencies of csound7_64.dll are searched next
			// to it first, not next to the host executable.
			lib = LoadLibraryExW (dll.c_str (), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
			if (lib)
			{
				usedRoot = root;
				break;
			}
			tried += " (LoadLibrary error " + std::to_string (GetLastError ()) + ")";
		}
		if (!lib)
		{
			api.status = "Csound 7 not found. Tried:" + tried;
			return;
		}

		// Resolve everything before using anything: a partially resolved table is never
		// exposed (ok stays false), so callers cannot hit a null pointer mid-way.
		std::string missing;
		resolve (lib, "csoundInitialize", api.Initialize, missing);
		resolve (lib, "csoundCreate", api.Create, missing);
		resolve (lib, "csoundDestroy", api.Destroy, missing);
		resolve (lib, "csoundGetVersion", api.GetVersion, missing);
		resolve (lib, "csoundGetSizeOfCsFloat", api.GetSizeOfCsFloat, missing);
		resolve (lib, "csoundSetGlobalEnv", api.SetGlobalEnv, missing);
		resolve (lib, "csoundSetOption", api.SetOption, missing);
		resolve (lib, "csoundCompileCSD", api.CompileCSD, missing);
		resolve (lib, "csoundStart", api.Start, missing);
		resolve (lib, "csoundPerformKsmps", api.PerformKsmps, missing);
		resolve (lib, "csoundGetSpin", api.GetSpin, missing);
		resolve (lib, "csoundGetSpout", api.GetSpout, missing);
		resolve (lib, "csoundGetSr", api.GetSr, missing);
		resolve (lib, "csoundGetKsmps", api.GetKsmps, missing);
		resolve (lib, "csoundGetChannels", api.GetChannels, missing);
		resolve (lib, "csoundGet0dBFS", api.Get0dBFS, missing);
		resolve (lib, "csoundGetHostData", api.GetHostData, missing);
		resolve (lib, "csoundSetHostAudioIO", api.SetHostAudioIO, missing);
		resolve (lib, "csoundSetHostMIDIIO", api.SetHostMIDIIO, missing);
		resolve (lib, "csoundSetExternalMidiInOpenCallback", api.SetExternalMidiInOpenCallback, missing);
		resolve (lib, "csoundSetExternalMidiReadCallback", api.SetExternalMidiReadCallback, missing);
		resolve (lib, "csoundSetExternalMidiInCloseCallback", api.SetExternalMidiInCloseCallback, missing);
		resolve (lib, "csoundSetMessageStringCallback", api.SetMessageStringCallback, missing);
		resolve (lib, "csoundListChannels", api.ListChannels, missing);
		resolve (lib, "csoundDeleteChannelList", api.DeleteChannelList, missing);
		resolve (lib, "csoundGetChannelPtr", api.GetChannelPtr, missing);
		resolve (lib, "csoundSetControlChannel", api.SetControlChannel, missing);
		if (!missing.empty ())
		{
			api.status = "csound7_64.dll is missing exports: " + missing;
			return;
		}
		// A float (MYFLT = float) build would make every spin/spout/channel access through
		// `csfloat` (double) read garbage, so refuse it outright.
		if (api.GetSizeOfCsFloat () != (int32_t)sizeof (csfloat))
		{
			api.status = "Unsupported Csound build: cs_float is " +
			             std::to_string (api.GetSizeOfCsFloat ()) + " bytes (expected 8)";
			return;
		}

		api.root = usedRoot;
		std::wstring plugins = usedRoot + L"\\plugins64";
		std::wstring samples = usedRoot + L"\\samples";
		api.pluginsDirUtf8 = toUtf8 (plugins);

		// SADIR / SSDIR: analysis and sound-file search paths (HRTF data, sf_GMbank.sf2…).
		// OPCODE7DIR64: where Csound 7 (double build) looks for opcode plug-in DLLs; it is
		// also passed explicitly to csoundCreate. Setting these here means orchestras find
		// the bundled resources without relying on the user's environment, and the DAW's
		// own environment is not modified (csoundSetGlobalEnv only affects Csound's view).
		// csoundSetGlobalEnv must run before the first csoundCreate.
		std::string samplesUtf8 = toUtf8 (samples);
		api.SetGlobalEnv ("SADIR", samplesUtf8.c_str ());
		api.SetGlobalEnv ("SSDIR", samplesUtf8.c_str ());
		api.SetGlobalEnv ("OPCODE7DIR64", api.pluginsDirUtf8.c_str ());

		// Never let Csound install signal handlers or atexit hooks inside a DAW process.
		api.Initialize (CSOUNDINIT_NO_SIGNAL_HANDLER | CSOUNDINIT_NO_ATEXIT);

		// csoundGetVersion() encodes major*1000 + minor*10 + patch (e.g. 7000 -> "7.0.0").
		int v = api.GetVersion ();
		api.status = "Csound " + std::to_string (v / 1000) + "." + std::to_string ((v / 10) % 100) +
		             "." + std::to_string (v % 10) + " loaded from " + toUtf8 (usedRoot);
		api.ok = true;
	});
	return api;
}

} // namespace CsoundVisual
