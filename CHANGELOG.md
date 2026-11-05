# Changelog

## 0.1.0 — 2026-11-05

**Paint Rooms** (Ctrl+Shift+I) — compose and design sound by painting images.
* **Paint Synth** room: time × pitch canvas played by Csound (additive `adsynt2` oscillator banks)
  live through the app's engine or rendered to a normalised 24-bit WAV; sines, noise bands and
  sample-loop modes; brightness = loudness, red/green = left/right.
* Pitch axes: linear, logarithmic, equal temperament with any steps per octave, Scala `.scl` files.
* Brushes: pencil, airbrush, spray, harmonics brush, line, rectangle, ellipse, gradient, eraser,
  blur, smudge, selection with move/copy/paste; undo/redo with bounded memory.
* Live parameter previews: moving a brush slider shows the brush on the canvas at its real size,
  hardness, level, pan colour and noise grain; the Feather slider re-shapes the last selection live
  and soft selections are drawn with their real coverage, as a pink overlay (same in every theme)
  so a selection is never mistaken for bright paint.
* Import «Full detail» (one column per picture pixel) and «= picture» rows (one row per pixel of
  height); on a note axis the dialog starts on a logarithmic axis; typed numbers apply at once
  and Enter in a box no longer imports.
* The picture import dialog has a live preview (the resulting canvas with its rulers), lets you
  choose the resolution (rows and columns) and the pitch axis (canvas, logarithmic or linear), and
  explains the chosen colour mapping (and what a grey picture does with it); the same explanations
  are in Paint Synth's «?» help.
* New operations in «Selection ▾»: Pan… and Pan sweep right → left / left → right (a sound that
  travels smoothly across the stereo field, at constant loudness).
* Every setting of the Paint Rooms is undoable: duration, synthesis and Sound ▾ settings (Paint
  Synth), filter settings (Image Filter), analysis / resynthesis settings (Spectrum Room, which also
  restores the picture), tempo, steps, bars, lanes, gate, sound and loop (Image Sequencer). Quick
  repeated changes of one setting are one step. In the Image Sequencer, ↶ / Ctrl+Z undo the newest
  change of the room, pattern or arrangement.
* Image Sequencer: patterns and arrangement are kept for the next session only when saved
  (File ▾ › Save for next session, or «Save» in the prompt shown when Paint Rooms or the app closes
  with unsaved changes); «Don't save» drops them (also work auto-saved by older versions). Closing
  the app after editing the Paint Synth picture asks whether to save it as PNG.
* No work is lost without asking: a **💾 Save** button (Ctrl+S) in the Paint Rooms header saves the
  active room's work (Paint Synth / Image Filter: PNG, Spectrum Room: WAV, Image Sequencer: for the
  next session), and closing Paint Rooms or the app with unsaved work asks Save / Don't save /
  Cancel in each room. «Don't save» discards it; Cancel or a cancelled file dialog keeps the window open. Clip miniatures use the pattern editor's colours
  (imported pictures look the same in the arrangement), with a dark backing behind the caption.
* Image Sequencer arrangement: drag on empty cells to draw a clip of any length, drag a clip's
  right edge to make it longer or shorter; with Stretch the pattern is squeezed / stretched to fill
  the clip, otherwise it loops or is cut. Clip inspector: Length (steps) and Stretch.
* Image Sequencer: painting past the end of a pattern makes it longer while you paint (empty room
  after the last step, dashed line; one undo step with the stroke). **Stretch** next to Steps:
  changing the length squeezes or stretches the content in time instead of cutting / adding silence.
* Image Sequencer: **File ▾** menu in the top bar, in the same place as Paint Synth's (new pattern,
  import picture as pattern, colour mapping); the 🖼 library button remains as a shortcut.
* Selection changes (new shape, select all, deselect, invert) are undo steps: Ctrl+Z / ↶ steps
  back through them instead of jumping over them to the last pixel edit.
* **Selection ▾** menu under the tools (select all, deselect, invert selection + the image
  operations), replacing the «Image ▾» menu at the top of each room.
* Image operations, picture import (three colour mappings, drag and drop) and PNG export, four
  starter examples, «Show Csound code» (readable standalone `.csd`).
* Shared foundation for the next rooms (`src/CsoundVisual/Paint`: `SpectralImage`, `PitchAxis`,
  STFT, live player, renderer) and `--paint-test` (real-time DSP-load check, inaudible).
* **Image Filter** room: a picture as a time-varying stereo equaliser (brightness = band gain,
  red/green = left/right) applied by Csound's phase vocoder (`pvsanal` → `pvsmaska` → `pvsynth`)
  to a WAV, another room's sound or the **live audio input**; fit-to-sound or loop in seconds/bars,
  selectable FFT/overlap with latency read-out, smoothing, dry/wet, latency-compensated 24-bit
  render, «Show Csound code», four starter filters, `--filter-test` (inaudible real-time check).
* **Spectrum Room**: a recording (WAV; AIFF/FLAC/OGG/MP3 decoded by Csound) analysed into an
  editable picture on any pitch axis (true-frequency STFT, dB brightness, stereo layers, aligned
  waveform), resynthesised faithfully (spectral gain mask, untouched parts identical) or additively
  through the Paint Synth engine; band selection on the pitch ruler, solo, remove, gain ±dB,
  extract to WAV, freeze a moment into a sustained tone, send to Paint Synth or to the session.
* **Shaped selections** in all three rooms: ellipse (Shift+M; Shift while dragging = circle),
  lasso (F) and spline (N; smooth Catmull-Rom curve through clicked points, double-click/Enter
  closes, Esc cancels, Backspace removes a point) next to the rectangle (M). Soft anti-aliased
  edges plus an optional **feather**; Shift = add, Alt = subtract; marching ants follow the real
  shape. Every selection-based edit respects the shape: image operations, delete, copy/cut/paste
  (paste keeps the shape), move, gradient, Spectrum Room Solo/Remove/Gain/Extract/Freeze.
* **Undo / Redo buttons** (↶ ↷) in the Paint Rooms header for the active room, greyed out when
  there is nothing to undo/redo, tooltip naming the step.
* **Image Sequencer** room (4th tab): paint small patterns (one column per step, 12-tone axis C1…C9)
  and arrange them on lanes × bars; per clip repeats, transpose, reverse, invert and gain; tempo,
  steps per beat, gate and legato; pattern library (new, duplicate, rename, delete, import picture);
  live playback through Csound with moving cursors (Space; Shift+Space = pattern solo), Render WAV
  (−1 dBTP, 44.1–96 kHz), «→ Paint Synth», «Show Csound code»; starter drums + bass + arpeggio;
  kept across tabs and saved in the settings. The arrangement is composed into one canvas played by
  the Paint Synth engine (sample-accurate timing; edits uploaded column by column).
* **Noise per cell**: an optional noise layer (0 = pure tone … 1 = noise band, equal-power mix, Sines
  mode) painted with the brush's **Noise** slider («Noise only» keeps the levels), set with
  **Image ▾ › Noise amount**, shown as a grainy texture; carried by undo, selections, clipboard,
  transpose/flip/reverse/stretch/blur. Costs what the Noise-bands mode costs (`--paint-test … mixed`).
* **16-bit and RGBA pictures**: 16-bit PNG/TIFF imported with all 65,536 levels (the import dialog
  shows «16-bit» / «8-bit» and «+ alpha»); new mappings **RGB: R = left, G = right, B = noise** and
  **RGBA: + alpha = mask**; **Export PNG** in 16-bit (default, lossless round trip) or 8-bit, blue =
  the noise layer.
* Fixed: Ctrl+Shift+Z undid instead of redoing in Paint Synth; it now redoes in all three rooms.
* Fixed: a fast Play→Stop while the picture was still uploading could leave a held note sounding
  while the UI said «stopped» (Paint Synth, Image Filter, Spectrum Room preview); the Spectrum
  Room preview note could not be stopped at all while another document kept Csound running;
  live edits made during the first upload of a new performance could be dropped.

### Stability
* Fixed: opening the «Selection ▾» menu closed the app (a shortcut label could not be parsed).
* Safety net: an unexpected error in the user interface is reported in the console and the app
  stays open instead of closing.

### Audio
* The live input defaults to «Windows default» (like the output); when Windows has no usable
  recording device (microphone disabled) the app plays without input and says so once per session.

## 0.1.0 — first public version (2026-11)

**Editor and live coding**
* Csound-aware editor: syntax colours, autocompletion, help on hover and F1, word-occurrence
  highlight (F3), error lines, Ctrl+wheel zoom.
* Live recompilation (Ctrl+Enter / Ctrl+Shift+Enter), automatic channel sliders, drag numbers in
  the code, score events from the console.
* Live values, Watch panel with enlarged view, **breakpoints** (F9, F5 continue, F10 next cycle).

**Audio and MIDI**
* Own WASAPI engine (shared/exclusive, latency presets, ksmps), PortAudio/ASIO through Csound.
* Live audio input with resampling and clock-drift correction; IN meter.
* DSP-load and RAM meter; recording to WAV; render to WAV.
* MIDI 1.0 (WinMM) and MIDI 2.0 (Windows MIDI Services), MIDI monitor, MIDI learn, on-screen and
  PC keyboard.

**Music tools**
* Piano roll: draw/move/resize notes, velocity by dragging, box selection (right-drag), loop,
  record MIDI, insert into / import from the score.
* Step sequencer: swing, probability, mute/solo, Euclidean and random fills, velocity by dragging,
  synthesised drum kit, export to the score.

**Plug-ins and integration**
* VST3 host: chain of effects/instruments after Csound, native editor windows, saved chain,
  plug-in manager with automatic black list.
* Csound Visual VST3 plug-in (instrument and effect) and *Send to DAW* with linked sliders and
  VST3 OUTPUT meters.
* Cabbage widget support; Ableton Link (tempo, beat, phase, start/stop).

**Visuals and interface**
* 3D views on the GPU: waterfall spectrogram (configurable resolution), XYZ pad, binaural panner.
* Examples browser with 1,500+ examples and showcase, audio-processing and Cabbage galleries.
* English/Spanish interface, five colour themes, responsive toolbar, Settings window.
