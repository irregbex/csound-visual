<CsoundSynthesizer>
<CsOptions>
-odac -iadc
</CsOptions>
<CsInstruments>
/*
  LIVE FX L1 - SPECTRAL FREEZE PEDAL  (endless, processes the audio input)

  BIG IDEA
    A "sustain pedal" for ANY sound. The input is analysed into FFT frames
    (pvsanal); pvsfreeze grabs one frame and resynthesises it forever, so a
    chord strummed once, a vocal syllable or a cymbal becomes an infinite pad
    under the live signal. With "auto" the pedal re-grabs once per bar, in
    time with the Cubase project (HOST_BPM), giving evolving drones.

  LISTEN FOR
    - The frozen layer keeps sounding behind the dry track; it changes on
      every bar line when "auto" is on.
    - "glide" morphs smoothly between grabs instead of jumping.
    - "shimmer" adds the frozen sound an octave higher (shoegaze/ambient).

  SLIDERS
    freeze  : 1 = hold the pedal (grab now and keep it), 0 = release.
    auto    : 1 = re-grab automatically on every bar (4 beats at the host tempo).
    glide   : how slowly one frozen sound morphs into the next.
    shimmer : level of the octave-up copy.
    mix     : level of the frozen layer (the dry signal dips slightly to make room).

  MIDI: no.   Input: the track's audio (ins). Use it in the Csound Visual FX
  plugin in Cubase; in the app, it uses the default audio input.
  Tempo: reads the "HOST_BPM" channel (120 if the host does not send it).
*/

sr = 48000
ksmps = 64
nchnls = 2
0dbfs = 1

chnk "freeze",  1, 2, 0,   0, 1
chnk "auto",    1, 2, 1,   0, 1
chnk "glide",   1, 2, 0.4, 0, 1
chnk "shimmer", 1, 2, 0.3, 0, 1
chnk "mix",     1, 2, 0.6, 0, 1
chnset 0,   "freeze"
chnset 1,   "auto"
chnset 0.4, "glide"
chnset 0.3, "shimmer"
chnset 0.6, "mix"

instr 1
  ain1, ain2 in
  kFrzSw chnget "freeze"
  kAuto  chnget "auto"
  kGlide chnget "glide"
  kShim  chnget "shimmer"
  kMix   chnget "mix"
  kShim  portk kShim, 0.05
  kMix   portk kMix, 0.05
  kBpm   chnget "HOST_BPM"
  kBpm   = (kBpm > 20 ? kBpm : 120)

  ; ---- when to grab a new frame: pressing "freeze", or every bar in auto mode
  kPrev  init 0
  kGrab  = 0
  if kFrzSw > 0.5 && kPrev <= 0.5 then
    kGrab = 1
  endif
  kPrev  = kFrzSw
  if kAuto > 0.5 && metro(kBpm / 240) == 1 then
    kGrab = 1
  endif
  ; wait for a moment when the input is actually sounding (not a gap or a breath),
  ; then open the "shutter" 25 ms so that a new FFT frame (every 512 samples) gets in
  kWant  init 0
  kOpen  init 0
  kLev   rms (ain1 + ain2) * 0.5, 30
  kPeak  init 0.001
  kPeak  = max(kPeak * 0.9998, kLev)       ; slowly decaying recent maximum
  if kGrab == 1 then
    kWant = 1
  endif
  if kWant == 1 && kLev > 0.4 * kPeak && kLev > 0.003 then
    kOpen = 0.025 * sr / ksmps
    kWant = 0
  endif
  kFrz   = (kOpen > 0 ? 0 : 1)
  kOpen  = max(kOpen - 1, 0)
  ; the frozen layer is audible while the pedal is held or auto is on
  kOn    = ((kFrzSw > 0.5 || kAuto > 0.5) ? 1 : 0)
  kOnS   portk kOn, 0.08

  fL  pvsanal ain1, 2048, 512, 2048, 1
  fR  pvsanal ain2, 2048, 512, 2048, 1
  fFL pvsfreeze fL, kFrz, kFrz
  fFR pvsfreeze fR, kFrz, kFrz
  kCut = 0.003 + (1 - kGlide) * 0.3
  fSL pvsmooth fFL, kCut, kCut
  fSR pvsmooth fFR, kCut, kCut
  fUL pvscale fSL, 2
  fUR pvscale fSR, 2
  aFL pvsynth fSL
  aFR pvsynth fSR
  aUL pvsynth fUL
  aUR pvsynth fUR

  ; the dry signal is not delayed (no added latency for the DAW); it is lowered
  ; a little as the frozen layer comes up, to leave headroom
  aWL = (aFL + aUR * kShim * 0.7) * kMix * kOnS
  aWR = (aFR + aUL * kShim * 0.7) * kMix * kOnS
  kDry = 1 - 0.4 * kMix * kOnS
  out ain1 * kDry + aWL, ain2 * kDry + aWR
endin

</CsInstruments>
<CsScore>
i1 0 z
e
</CsScore>
</CsoundSynthesizer>
