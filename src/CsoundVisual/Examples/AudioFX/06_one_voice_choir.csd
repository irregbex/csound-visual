<CsoundSynthesizer>
<CsOptions>
-odac
</CsOptions>
<CsInstruments>
/*
  AUDIO FX 06 - ONE-VOICE CHOIR  (~97 s)

  BIG IDEA
    One singer becomes a choir. Csound re-pitches the voice in the frequency
    domain (pvscale) while KEEPING ITS FORMANTS - the resonances of the throat
    that tell us "this is a human voice" - so the copies sound like other
    singers, not chipmunks. Each copy is slightly detuned, delayed and panned,
    because real choirs are never perfectly together. (Think of a harmoniser
    like Cubase's VariAudio-generated harmonies, but live.)

  LISTEN FOR
    - 0-4 s: the single singer alone.
    - Then the choir enters; with "auto" on the harmony changes every phrase:
      octaves -> power fifths -> major thirds -> minor thirds.
    - Turn "formant" to 0: the copies become cartoon voices (formants shifted too).
    - "detune" + "spread" turn a precise harmoniser into a big, loose choir.

  SLIDERS
    auto    : 1 = the harmony type changes by itself every 8 s.
    chord   : 0 = octaves, 1 = fifths, 2 = major thirds, 3 = minor thirds (when auto = 0).
    choir   : level of the extra voices (0 = solo).
    detune  : random pitch drift of each singer (0-25 cents).
    spread  : timing differences between singers (0-60 ms) and stereo width.
    formant : 1 = natural voices (formants kept), 0 = "chipmunk" transposition.

  MIDI: no.   Sample: ../Manual/singFemale.aif (4 s phrase, 44.1 kHz, looped by diskin2).
*/

sr = 48000
ksmps = 64
nchnls = 2
0dbfs = 1
seed 0

chnk "auto",    1, 2, 1,   0, 1
chnk "chord",   1, 2, 2,   0, 3
chnk "choir",   1, 2, 0.8, 0, 1
chnk "detune",  1, 2, 0.4, 0, 1
chnk "spread",  1, 2, 0.5, 0, 1
chnk "formant", 1, 2, 1,   0, 1
chnset 1,   "auto"
chnset 2,   "chord"
chnset 0.8, "choir"
chnset 0.4, "detune"
chnset 0.5, "spread"
chnset 1,   "formant"

; intervals (semitones) of voices 1 and 2 for each chord type
giInt1 ftgen 0, 0, 4, -2, 12, 7, 4, 3
giInt2 ftgen 0, 0, 4, -2, -12, -5, 7, 7

; one extra singer: shifted, drifting, delayed, panned
opcode Singer, aa, afkkkkki
  aDry, fIn, kSemi, kDet, kSpr, kForm, kAmp, iSeed xin
  seed iSeed
  kDrift randi 1, 0.7 + random:i(0, 0.5), random:i(0, 1)
  kRat = 2 ^ ((kSemi + kDrift * kDet * 0.25) / 12)
  fS   pvscale fIn, kRat, (kForm > 0.5 ? 1 : 0)
  aS   pvsynth fS
  ; formant correction removes energy: match the copy's loudness to the original
  kG   = limit(rms:k(aDry) / (rms:k(aS) + 0.0003), 0.25, 4)
  aS  *= interp(portk(kG, 0.05))
  iDel = random:i(0.2, 1)
  aS   vdelay3 aS, 1 + kSpr * 60 * iDel, 80
  iPan = random:i(0, 1)
  kPan = 0.5 + (iPan - 0.5) * kSpr * 1.8
  xout aS * kAmp * sqrt(1 - kPan), aS * kAmp * sqrt(kPan)
endop

instr 1
  kAuto chnget "auto"
  kCh   chnget "chord"
  kAmp  chnget "choir"
  kDet  chnget "detune"
  kSpr  chnget "spread"
  kForm chnget "formant"
  if kAuto > 0.5 then
    kCh = int(timeinsts() / 8) % 4
  endif
  kCh   = int(kCh + 0.5)
  kAmp  portk kAmp, 0.1
  kSpr  portk kSpr, 0.1
  ; the choir enters after the first phrase
  kAmp *= linseg:k(0, 4, 0, 1.5, 1, 1, 1)

  kI1 portk tab:k(kCh, giInt1), 0.02
  kI2 portk tab:k(kCh, giInt2), 0.02

  aV diskin2 "../Manual/singFemale.aif", 1, 0, 1
  fV pvsanal aV, 2048, 512, 2048, 1

  a1L, a1R Singer aV, fV, kI1,       kDet, kSpr, kForm, kAmp * 0.7, 11
  a2L, a2R Singer aV, fV, kI2,       kDet, kSpr, kForm, kAmp * 0.6, 12
  a3L, a3R Singer aV, fV, -12,       kDet, kSpr, kForm, kAmp * 0.45, 13
  a4L, a4R Singer aV, fV, 0,         kDet, kSpr, kForm, kAmp * 0.5, 14
  a5L, a5R Singer aV, fV, kI1,       kDet, kSpr, kForm, kAmp * 0.4, 15
  a6L, a6R Singer aV, fV, kI2 + 12,  kDet, kSpr, kForm, kAmp * 0.25, 16

  aL = aV * 0.8 + a1L + a2L + a3L + a4L + a5L + a6L
  aR = aV * 0.8 + a1R + a2R + a3R + a4R + a5R + a6R
  aRL, aRR reverbsc aL, aR, 0.82, 9000
  kEnv linseg 0, 0.02, 1, p3 - 3.02, 1, 3, 0
  out tanh((aL + aRL * 0.4) * kEnv * 0.55), tanh((aR + aRR * 0.4) * kEnv * 0.55)
endin

</CsInstruments>
<CsScore>
i1 0 97
e
</CsScore>
</CsoundSynthesizer>
