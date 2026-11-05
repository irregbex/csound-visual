<CsoundSynthesizer>
<CsOptions>
-odac
</CsOptions>
<CsInstruments>
/*
  AUDIO FX 03 - FROZEN VOICE PADS  (~104 s)

  BIG IDEA
    A sound's SPECTRUM can be photographed and held forever. Csound analyses
    two seconds of a singer (pvsanal), freezes one FFT frame (pvsfreeze) - one
    instant of her voice - and keeps it sounding as an endless note. It then
    measures the pitch of that frozen frame (ptrack), re-tunes copies of it to
    the notes of a chord progression (pvscale, keeping the vocal formants) and
    smears it in time (pvsblur / pvsmooth). Result: an ambient choir pad made
    from 2 seconds of audio and no synthesizer at all.

  LISTEN FOR
    - 0-4 s: the raw sample (a woman singing "ahh" for 2 seconds).
    - Then it never stops: every few seconds a new "photo" is taken and the
      pad glides slowly from the old timbre to the new one.
    - Chords change every 8 s (Fmaj7 - Am7 - G6 - Em7 ...): it is the SAME
      frozen voice, retuned.

  SLIDERS
    evolve     : how often a new snapshot of the voice is taken (0 = hold the current one forever).
    glide      : how slowly the pad morphs between snapshots and chords.
    blur       : time-smear of the spectrum (more = softer, more "shimmer").
    brightness : tone of the pad (dark to airy).
    space      : reverb amount.

  MIDI: no.   Sample: ../Manual/sing.wav (44.1 kHz mono, played by diskin2 with resampling).
*/

sr = 48000
ksmps = 64
nchnls = 2
0dbfs = 1
seed 0

chnk "evolve",     1, 2, 0.5, 0, 1
chnk "glide",      1, 2, 0.5, 0, 1
chnk "blur",       1, 2, 0.3, 0, 1
chnk "brightness", 1, 2, 0.6, 0, 1
chnk "space",      1, 2, 0.5, 0, 1
chnset 0.5, "evolve"
chnset 0.5, "glide"
chnset 0.3, "blur"
chnset 0.6, "brightness"
chnset 0.5, "space"

; four-voice chords (MIDI notes), one row per chord, 8 s each
giChords ftgen 0, 0, 32, -2, 53,57,60,64,  57,60,64,67,  55,59,62,64,  52,55,59,62, \
                             53,57,60,67,  50,57,60,65,  55,59,62,67,  52,56,59,64

; ---------------------------------------------------------------- the raw sample first
instr 1
  aS diskin2 "../Manual/sing.wav", 1
  aE linseg 1, p3 - 0.3, 1, 0.3, 0
  out aS * aE * 0.6, aS * aE * 0.6
endin

; ---------------------------------------------------------------- the pad engine
instr 2
  kEvolve chnget "evolve"
  kGlide  chnget "glide"
  kBlur   chnget "blur"
  kBright chnget "brightness"
  kWet    chnget "space"
  kBlur   portk kBlur, 0.2
  kBright portk kBright, 0.2
  kWet    portk kWet, 0.2

  ; the voice loops silently in the background: it is only the "camera's" subject
  iLen filelen "../Manual/sing.wav"
  aSrc diskin2 "../Manual/sing.wav", 1, 0, 1
  fSrc pvsanal aSrc, 4096, 1024, 4096, 1

  ; --- snapshot logic: freeze = 1 holds; one k-cycle of 0 grabs a new frame.
  ;     Only grab while the singer is in her steady vowel (0.45 - 1.4 s of the loop).
  kT      timeinsts
  kInLoop = kT % iLen
  kWant   init 1
  kPeriod = 2 + (1 - kEvolve) * 10
  if metro(1 / kPeriod) == 1 && kEvolve > 0.01 then
    kWant = 1
  endif
  ; the "shutter" stays open for 0.06 s so that at least one new FFT frame
  ; (one every 1024 samples) reaches pvsfreeze
  kOpen init 0
  if kWant == 1 && kInLoop > 0.45 && kInLoop < 1.3 then
    kOpen = 0.06 * (sr / ksmps)
    kWant = 0
  endif
  kFrz = (kOpen > 0 ? 0 : 1)
  kOpen = max(kOpen - 1, 0)
  fFrz pvsfreeze fSrc, kFrz, kFrz

  ; --- morph: pvsmooth low-passes every bin's amplitude and frequency, so the
  ;     pad glides from one snapshot to the next instead of jumping
  kCut  = 0.002 + (1 - kGlide) * 0.05
  fSm   pvsmooth fFrz, kCut, kCut

  ; --- measure the pitch of what we actually hear (the smoothed frozen frame),
  ;     so it can be re-tuned to exact notes. ptrack on a steady tone is precise.
  aSm  pvsynth fSm
  kF0, kA0 ptrack aSm, 1024
  kF0Ok init 216
  ; the singer is near 216 Hz: fold octave errors of the tracker back
  kF0 = (kF0 > 300 ? kF0 / 2 : kF0)
  kF0 = (kF0 > 300 ? kF0 / 2 : kF0)
  kF0 = (kF0 < 150 ? kF0 * 2 : kF0)
  if kF0 > 150 && kF0 < 300 && kA0 > -60 then
    kF0Ok = kF0
  endif

  fBl   pvsblur fSm, 0.02 + kBlur * 0.25, 0.3

  ; --- chord: target pitches for 4 voices + a sub-octave
  kRow  = int(max(kT - 7.5, 0) / 8) % 8   ; a new chord every 8 s
  kN1 tab kRow * 4,     giChords
  kN2 tab kRow * 4 + 1, giChords
  kN3 tab kRow * 4 + 2, giChords
  kN4 tab kRow * 4 + 3, giChords
  kGt   = 0.04 + kGlide * 0.3        ; portk half-time: chord portamento
  kR1 portk cpsmidinn(kN1) / kF0Ok, kGt
  kR2 portk cpsmidinn(kN2) / kF0Ok, kGt
  kR3 portk cpsmidinn(kN3) / kF0Ok, kGt
  kR4 portk cpsmidinn(kN4) / kF0Ok, kGt
  kDr1 = 1 + 0.003 * lfo:k(1, 0.13)    ; tiny drifts = ensemble
  kDr2 = 1 - 0.003 * lfo:k(1, 0.17)
  f1 pvscale fBl, kR1 * kDr1, 1
  f2 pvscale fBl, kR2 * kDr2, 1
  f3 pvscale fBl, kR3 * kDr1, 1
  f4 pvscale fBl, kR4 * kDr2, 1
  f5 pvscale fBl, kR1 * 0.5, 1
  a1 pvsynth f1
  a2 pvsynth f2
  a3 pvsynth f3
  a4 pvsynth f4
  a5 pvsynth f5

  aL = 0.9 * a1 + 0.7 * a2 + 0.4 * a3 + 0.5 * a4 + 0.6 * a5
  aR = 0.4 * a1 + 0.5 * a2 + 0.9 * a3 + 0.7 * a4 + 0.6 * a5
  ; brightness: low-pass from 1.2 kHz to 14 kHz
  kFc = 1200 * 2 ^ (kBright * 3.5)
  aL butlp aL, kFc
  aR butlp aR, kFc
  aL dcblock2 aL
  aR dcblock2 aR
  aRL, aRR reverbsc aL, aR, 0.9, 8000
  kEnv linseg 0, 6, 1, p3 - 12, 1, 6, 0
  aOL = (aL * (1 - 0.4 * kWet) + aRL * kWet * 1.2) * kEnv * 0.6
  aOR = (aR * (1 - 0.4 * kWet) + aRR * kWet * 1.2) * kEnv * 0.6
  out aOL, aOR
endin

</CsInstruments>
<CsScore>
i1 0 2.2
i2 2.5 101.5
e
</CsScore>
</CsoundSynthesizer>
