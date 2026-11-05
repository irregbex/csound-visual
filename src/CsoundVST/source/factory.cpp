//------------------------------------------------------------------------------
// Csound Visual VST3 - plug-in factory (one module, two classes)
//
// The VST3 module entry point. BEGIN_FACTORY_DEF ... END_FACTORY (from the SDK's
// pluginfactory.h) define the exported GetPluginFactory() function, which the host
// calls after loading CsoundVisual.vst3 to enumerate the classes it contains and
// to create instances of them. (InitDll/ExitDll, the other module exports, come
// from the SDK's dllmain.cpp in the `sdk` library.)
//
// Both classes are created from the same C++ class, CsoundVisualPlugin, with a
// different PluginKind. Each is a single-component effect: the audio processor and
// the edit controller are one object, so it is registered once under
// kVstAudioEffectClass and has no separate controller class ID.
//
// Class flags argument: 0 means "not distributable", i.e. the processor and the
// controller must live in the same process (always true for a single component).
// The subcategory strings decide where hosts list the plug-in (Instrument|Synth
// vs Fx). kManyInstances: any number of instances may be created per process;
// each gets its own Csound instance, pipe name and descriptor file.
//------------------------------------------------------------------------------
#include "plugin.h"
#include "version.h"

#include "public.sdk/source/main/pluginfactory.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivstcomponent.h"

using namespace Steinberg;
using namespace Steinberg::Vst;

BEGIN_FACTORY_DEF (CSV_VENDOR, CSV_URL, CSV_EMAIL)

	// DEF_CLASS2 arguments, in order: class ID, cardinality, category (component
	// type), display name, class flags, subcategories, class version, SDK version
	// the class was built with, create function (returns an FUnknown* with one
	// reference owned by the caller).

	// Instrument: MIDI in -> Csound -> stereo out.
	DEF_CLASS2 (INLINE_UID (0x82E53EA5, 0xF47D4F4A, 0x9641FC59, 0x93A8CD51), // = CSV_INSTRUMENT_UID
	            PClassInfo::kManyInstances,
	            kVstAudioEffectClass,
	            "Csound Visual Instrument",
	            0, // single-component plug-ins are not distributable
	            PlugType::kInstrumentSynth, // "Instrument|Synth"
	            CSV_VERSION_STR,
	            kVstVersionString,
	            CsoundVisual::CsoundVisualPlugin::createInstrument)

	// FX: stereo in (+ MIDI in) -> Csound -> stereo out.
	DEF_CLASS2 (INLINE_UID (0x210C3013, 0xBA8B4347, 0xACE9B078, 0x3FB37C5C), // = CSV_FX_UID
	            PClassInfo::kManyInstances,
	            kVstAudioEffectClass,
	            "Csound Visual FX",
	            0,
	            PlugType::kFx, // "Fx"
	            CSV_VERSION_STR,
	            kVstVersionString,
	            CsoundVisual::CsoundVisualPlugin::createFx)

END_FACTORY
