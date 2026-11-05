//------------------------------------------------------------------------------
// Csound Visual VST3 - version and identity
//
// Single source of truth for the plug-in's version numbers, vendor strings and
// VST3 class IDs. Included by:
//   * factory.cpp            - the vendor/URL/e-mail of the factory and the
//                              version string of each class reported to hosts;
//   * plugin.cpp             - the version printed in the Csound log banner;
//   * resource/csoundvisual.rc - the Windows VERSIONINFO resource (file
//                              properties). The resource compiler parses this
//                              header too, so it must contain only preprocessor
//                              definitions and comments (no C++ code).
// CMakeLists.txt READS the four CSV_VERSION_* numbers below (file(STRINGS)) for
// its project() VERSION, so this header is the only place to edit when
// releasing. Keep each define on one line in the form
// "#define CSV_VERSION_<PART> <number>": that is what the CMake regex expects.
//------------------------------------------------------------------------------
#pragma once

/// Version components (major.minor.patch.build). Shown by hosts in their
/// plug-in manager and stored by some hosts with the project.
#define CSV_VERSION_MAJOR 1
#define CSV_VERSION_MINOR 0
#define CSV_VERSION_PATCH 0
#define CSV_VERSION_BUILD 1

/// Two-level stringification: the outer macro expands its argument first, so
/// CSV_STRINGIFY (CSV_VERSION_MAJOR) yields "1" rather than "CSV_VERSION_MAJOR".
#define CSV_STRINGIFY2(x) #x
#define CSV_STRINGIFY(x) CSV_STRINGIFY2 (x)

/// "major.minor.patch.build" as a string literal (e.g. "1.0.0.1"), built from the
/// numbers above by adjacent-literal concatenation.
#define CSV_VERSION_STR                                                                       \
	CSV_STRINGIFY (CSV_VERSION_MAJOR)                                                         \
	"." CSV_STRINGIFY (CSV_VERSION_MINOR) "." CSV_STRINGIFY (CSV_VERSION_PATCH) "." CSV_STRINGIFY ( \
	    CSV_VERSION_BUILD)

/// Factory info (PFactoryInfo): vendor name shown by hosts as the plug-in's
/// manufacturer (Cubase groups the plug-ins under it), web URL and contact
/// e-mail. The e-mail uses the reserved .invalid domain on purpose: there is
/// no support address.
#define CSV_VENDOR "Csound Visual"
#define CSV_URL "https://csound.com"
#define CSV_EMAIL "mailto:none@csound-visual.invalid"

// Fixed class IDs. Never change them: Cubase projects reference plug-ins by these IDs.
// Each is a 128-bit VST3 class ID (FUID) written as four 32-bit words, the form
// INLINE_UID() expects. Because the plug-in is a single-component effect there is
// no separate controller class ID. factory.cpp spells the same four words out
// literally next to "= CSV_*_UID"; if these ever had to change, both places would.
// Csound Visual Instrument: 82E53EA5-F47D4F4A-9641FC59-93A8CD51
// Csound Visual FX:         210C3013-BA8B4347-ACE9B078-3FB37C5C
#define CSV_INSTRUMENT_UID 0x82E53EA5, 0xF47D4F4A, 0x9641FC59, 0x93A8CD51
#define CSV_FX_UID 0x210C3013, 0xBA8B4347, 0xACE9B078, 0x3FB37C5C
