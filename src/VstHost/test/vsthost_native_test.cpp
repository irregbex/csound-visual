//------------------------------------------------------------------------------
// vsthost_native_test - console check of CsoundVisualVstHost.dll without .NET
//
//   vsthost_native_test <module.vst3> [class id]
//
// Scans the module (prints the JSON), loads the class (default: the first audio class),
// prepares it at 48 kHz / 512, processes 1 s of a 440 Hz sine and prints peak/RMS before
// and after. Useful to tell a plug-in problem from a problem of the managed host.
//
// It uses only the public C API (vsthost.h), exactly as the C# side does, and links against
// the import library of CsoundVisualVstHost.dll. Built by tools/build_vsthost.ps1 into
// src/VstHost/build/bin/ next to the DLL. Run it from a console:
//
//   src\VstHost\build\bin\vsthost_native_test.exe "C:\Program Files\Common Files\VST3\X.vst3"
//
// Output lines: "scan (<bytes>): <json>", "prepare <code> · <info json>",
// "process <code> · in rms · out rms · peak" (linear amplitude, 1.0 = full scale),
// "api <version> · restart flags <n>", "freed".
// Exit codes: 0 ok, 1 usage, 2 scan failed, 3 load failed. A plug-in that crashes in
// process() shows "process 2". Everything runs on the main thread (no editor is opened, so no
// message pump is needed). Nothing is played: the audio stays in memory.
//------------------------------------------------------------------------------
#include "../source/vsthost.h"

#include <windows.h>

#include <algorithm> // std::min / std::max
#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

/// Entry point. wmain (UTF-16 argv) so module paths with non-ASCII characters reach
/// vh_scan_module / vh_load intact.
/// @param argv  [1] module path, [2] optional class id (32 hex digits, as printed by the scan).
int wmain (int argc, wchar_t** argv)
{
	if (argc < 2)
	{
		std::printf ("usage: vsthost_native_test <module.vst3> [class id]\n");
		return 1;
	}
	// 1 MB is far more than any factory description needs, so no "retry with a bigger
	// buffer" step is required here.
	std::vector<char> buf (1 << 20);
	int n = vh_scan_module (argv[1], buf.data (), (int)buf.size ());
	std::printf ("scan (%d): %s\n", n, buf.data ());
	if (n < 0)
		return 2;
	std::string json (buf.data ());
	std::string cid;
	if (argc > 2)
	{
		// The class id is plain ASCII hex, so a narrowing per-character copy is enough.
		for (wchar_t* p = argv[2]; *p; ++p)
			cid += (char)*p;
	}
	else
	{
		// first class of category "Audio Module Class"
		// (the processor classes; "Component Controller Class" entries are the controllers).
		// A crude string search is enough for a test: the JSON written by vh_scan_module puts
		// "cid" after "category" in every class object, and a cid is always 32 characters.
		size_t pos = json.find ("\"category\":\"Audio Module Class\"");
		size_t c = pos == std::string::npos ? std::string::npos : json.find ("\"cid\":\"", pos);
		if (c != std::string::npos)
			cid = json.substr (c + 7, 32);
	}
	char err[512] = {};
	VhInstance* h = vh_load (argv[1], cid.c_str (), err, sizeof err);
	if (!h)
	{
		std::printf ("load failed: %s\n", err);
		return 3;
	}
	// 48 kHz, at most 512 frames per process() call. The result code is printed, not checked:
	// vh_process_stereo below simply returns 1 if preparing failed.
	int r = vh_prepare (h, 48000, 512);
	vh_info (h, buf.data (), (int)buf.size ());
	std::printf ("prepare %d · %s\n", r, buf.data ());
	// One second of a 440 Hz sine at amplitude 0.25 (-12 dBFS) on both channels.
	// inSum accumulates the energy of both channels for the input RMS.
	const int frames = 48000;
	std::vector<float> L (frames), R (frames);
	double inSum = 0, outSum = 0, outPeak = 0;
	for (int i = 0; i < frames; ++i)
	{
		L[i] = R[i] = (float)(0.25 * std::sin (2 * 3.14159265358979 * 440 * i / 48000.0));
		inSum += 2.0 * L[i] * L[i];
	}
	// Note on, channel 1, middle C (60), velocity 100: makes an instrument sound (effects
	// ignore it). It is queued and delivered with the first block. No note off is sent.
	vh_midi (h, 0x90, 60, 100);
	// Process in place in 512-frame blocks, as the audio thread would, plus a final partial
	// block for the remaining 48000 % 512 = 384 frames. (Before, only whole blocks were
	// processed, so those last 384 frames kept the raw input sine yet were counted in the
	// output RMS/peak below, skewing the numbers.) `r` keeps the result of the first block
	// that did not return 0, or 0 when all succeeded, so an error is not hidden by a later
	// successful block.
	int pr = 0;
	for (int i = 0; i < frames; i += 512)
	{
		const int n = std::min (512, frames - i);
		const int ri = vh_process_stereo (h, L.data () + i, R.data () + i, n);
		if (pr == 0)
			pr = ri;
	}
	r = pr;
	// Output RMS over both channels and peak of the left channel (linear, 1.0 = 0 dBFS).
	for (int i = 0; i < frames; ++i)
	{
		outSum += (double)L[i] * L[i] + (double)R[i] * R[i];
		outPeak = std::max (outPeak, (double)std::fabs (L[i]));
	}
	std::printf ("process %d · in rms %.4f · out rms %.4f peak %.4f\n", r, std::sqrt (inSum / (2.0 * frames)),
	             std::sqrt (outSum / (2.0 * frames)), outPeak);
	// API version 2: restartComponent flags raised during the run (0 = none), e.g. 8 =
	// kLatencyChanged. Also exercises the controller sync path.
	std::printf ("api %d · restart flags %d\n", vh_version (), vh_version () >= 2 ? vh_take_restart_flags (h) : 0);
	vh_free (h);
	std::printf ("freed\n");
	return 0;
}
