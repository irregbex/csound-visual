# Roadmap

## Status (2026-11-04)

| Stage | Status |
|---|---|
| **1 · Low-latency audio, help tips, documentation** | ✅ Done: own WASAPI engine (shared and exclusive, 3 ms measured), latency presets, ksmps override, diagnostics (`--audio-probe`, `--audio-test`), help tips everywhere, bilingual HTML docs with diagrams. |
| **2 · Live values, MIDI, live coding, recording, spectrum** | ✅ Done: live values (debugger), watch panel, context menu, F1; WinMM MIDI with monitor, MIDI learn and on-screen/PC keyboard; Ctrl+Enter and Ctrl+Shift+Enter; completion; record to WAV; spectrum. ✅ MIDI 2.0 through Windows MIDI Services (SDK preview 11, verified through the loopback; see [MIDI](midi.md)). |
| **3 · 3D (OpenGL)** | ✅ Done: detachable 3D window (F11), waterfall spectrogram, XYZ pad, binaural spatial panner with an `hrtfmove2` template; OpenGL ES 3 over ANGLE/D3D11 verified on the RTX 3090 Ti. See [3D views](visual-3d.md). |
| **4 · Music tools (piano roll, envelopes, step sequencer, transport, knobs, OSC, templates)** | 🟡 In progress: ✅ **piano roll** and ✅ **step sequencer** with their own transport (tempo, loop, swing), look-ahead scheduling on Csound's clock, score export/import, MIDI recording and a synthesised drum kit (2026-11-04; see [Music tools](music-tools.md)). Pending: envelopes, tables, knobs/XY, OSC, template library. |
| **5 · VST3 host, Cabbage, Link, breakpoints** | ✅ Done (2026-11-05): VST3 host with an effect/instrument chain and plug-in windows ([VST3 host](vst3-host.md)), Cabbage UI ([Cabbage](cabbage.md)), Ableton Link ([Ableton Link](ableton-link.md)), editor breakpoints. Tested on the development PC (Link only against a test peer). |
| **6 · Sound design with images: Paint Rooms** | 🟡 In progress: ✅ shared foundation and the **Paint Synth** room (paint a time × pitch canvas, hear it live through Csound, render to WAV, Scala tunings, harmonics brush; see [Paint Rooms](paint-rooms.md)). Next: **Spectrum Room** (sound → image → edit → resynthesis) and **Image Filter** (a picture as a time-varying stereo EQ). |

## What this installation offers (verified)

* 1485 opcodes: 55 MIDI, 4 OSC, 51 spectral (`pvs*`), 17 granular, 26 physical models, 31
  virtual-analog filters, 14 SoundFont (plus `samples\sf_GMbank.sf2`), 22 Arduino/serial.
* **Not included**: VST, FluidSynth, Ableton Link, Faust, Python.
* Csound 7 APIs used or planned: debugger (live values, breakpoints), graph display callbacks,
  host audio/MIDI I/O, UDP/OSC server, `ugen.h`.
* VST3: the [vst3 opcodes](https://github.com/gogins-dev/csound-vst3-opcodes) exist (beta,
  GPLv3, no plugin editor windows). The VST 3.8 SDK has been
  [MIT licensed](https://sonicstate.com/news/2025/10/30/vst-3-now-available-under-mit-license)
  since 2025-10-29, so the app itself can be a VST3 host.

## Stage 3 — 3D visualisation and control (GPU)

* Base: Avalonia `OpenGlControlBase` (OpenGL ES 3 over ANGLE/Direct3D 11), Vulkan through
  `GpuInterop` later; detachable windows for a second screen; 2D fallback if the GPU fails.
* Views: 3D waterfall spectrogram, stereo 3D scope, **XYZ pad** (three channels at once),
  **3D spatial panner** (`hrtfmove2`/`vbap`, binaural), 3D wavetable, live-performance visualiser,
  3D instrument graph.

## Stage 4 — Music tools

Piano roll of the score (edit notes visually), envelope editor (`linseg`/`expseg`/`madsr`
points), table editor (GEN 10/7/2), step sequencer, transport (BPM, metronome, loop), knobs and
XY pads, phone control over OSC, modern template library, instrument graph, sample waveforms,
background syntax check.

## Stage 5 — Big projects

Own audio engine refinements, **VST3 host** with plugin windows (✅ done 2026-11-04: native bridge
`CsoundVisualVstHost.dll`, insert chain of VST3 effects and instruments after Csound in the
«Windows · WASAPI» engine, plug-in editor windows, scanner in a separate process, chain and states
saved — see [VST3 plug-ins in the app](vst3-host.md); feeding Csound *from* VST3 instruments through
audio channels is not done),
alternative vst3 opcodes, Cabbage compatibility (✅ done 2026-11-04: tolerant parser for Cabbage 2
and Cabbage 3 JSON, «Cabbage UI» card + pop-out window with knobs, sliders, buttons, comboboxes, XY
pad, keyboard, labels, groups and images driving the same channels as the sliders, stand-ins for
`cabbageGetValue`/`SetValue`/`Set`/`Get`/`Changed`, `;@knob` comments in ordinary documents (item 15),
3 examples; tested on the 280 Cabbage 2 examples — see [Cabbage instruments](cabbage.md)), Ableton Link (✅ done 2026-11-04: Link 3.1.5 in
the app, `LINK_*` channels, tools locked to the session tempo and bar, Start Stop Sync — see
[Ableton Link](ableton-link.md)), breakpoints (✅ done 2026-11-04: gutter + F9, pause with the device
playing silence, Debug panel with every variable, continue / next control cycle, line map kept across
live recompiles — see [Breakpoints](editor-intelligence.md#breakpoints)), MIDI capture to
score, Scala tunings.
