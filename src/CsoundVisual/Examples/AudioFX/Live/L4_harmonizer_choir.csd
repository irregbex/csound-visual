<CsoundSynthesizer>
<CsOptions>
-odac -iadc
</CsOptions>
<CsInstruments>
/*
  LIVE FX L4 - HARMONIZER / CHOIR  (endless, processes the audio input)

  BIG IDEA
    Turn one vocal (or instrument) track into a section. The input is
    analysed with FFTs and copied five times; every copy is transposed by
    pvscale WITH FORMANT PRESERVATION (so it sounds like another singer, not a
    chipmunk), then given its own random pitch drift, timing offset and stereo
    position, the three things that make a real ensemble sound wide.

  LISTEN FOR
    - "chord" chooses the harmony: octaves, fifths, major or minor thirds.
    - "detune" + "spread" go from a tight harmoniser to a big, loose choir.
    - "formant" 0 = the copies change timbre with pitch (cartoon voices).
    Fixed intervals do not follow the key; on a melody, try octaves or fifths first.

  SLIDERS
    chord   : 0 = octaves, 1 = fifths, 2 = major thirds, 3 = minor thirds.
    choir   : level of the copies (0 = dry only).
    detune  : random pitch drift of each copy (0-25 cents).
    spread  : timing offsets (0-60 ms) and stereo width.
    formant : 1 = keep formants (natural), 0 = plain transposition.

  MIDI: no.   Input: the track's audio. For the Csound Visual FX plugin in Cubase.
  Latency: the copies come 2048 samples (~43 ms) after the dry signal, which
  passes untouched.
*/

sr = 48000
ksmps = 64
nchnls = 2
0dbfs = 1
seed 0

chnk "chord",   1, 2, 1,   0, 3
chnk "choir",   1, 2, 0.7, 0, 1
chnk "detune",  1, 2, 0.4, 0, 1
chnk "spread",  1, 2, 0.5, 0, 1
chnk "formant", 1, 2, 1,   0, 1
chnset 1,   "chord"
chnset 0.7, "choir"
chnset 0.4, "detune"
chnset 0.5, "spread"
chnset 1,   "formant"

giInt1 ftgen 0, 0, 4, -2, 12, 7, 4, 3
giInt2 ftgen 0, 0, 4, -2, -12, -5, 7, 7

; one extra singer: shifted, drifting, delayed, panned, loudness-matched
opcode Singer, aa, afkkkkki
  aDry, fIn, kSemi, kDet, kSpr, kForm, kAmp, iSeed xin
  seed iSeed
  kDrift randi 1, 0.7 + random:i(0, 0.5), random:i(0, 1)
  kRat = 2 ^ ((kSemi + kDrift * kDet * 0.25) / 12)
  fS   pvscale fIn, kRat, (kForm > 0.5 ? 1 : 0)
  aS   pvsynth fS
  kG   = limit(rms:k(aDry) / (rms:k(aS) + 0.0003), 0.25, 4)
  aS  *= interp(portk(kG, 0.05))
  iDel = random:i(0.2, 1)
  aS   vdelay3 aS, 1 + kSpr * 60 * iDel, 80
  iPan = random:i(0, 1)
  kPan = 0.5 + (iPan - 0.5) * kSpr * 1.8
  xout aS * kAmp * sqrt(1 - kPan), aS * kAmp * sqrt(kPan)
endop

instr 1
  ain1, ain2 in
  kCh   chnget "chord"
  kAmp  chnget "choir"
  kDet  chnget "detune"
  kSpr  chnget "spread"
  kForm chnget "formant"
  kCh   = int(kCh + 0.5)
  kAmp  portk kAmp, 0.05
  kSpr  portk kSpr, 0.1
  kI1 portk tab:k(kCh, giInt1), 0.02
  kI2 portk tab:k(kCh, giInt2), 0.02

  aM = (ain1 + ain2) * 0.5
  fM pvsanal aM, 2048, 512, 2048, 1
  a1L, a1R Singer aM, fM, kI1,      kDet, kSpr, kForm, kAmp * 0.6,  11
  a2L, a2R Singer aM, fM, kI2,      kDet, kSpr, kForm, kAmp * 0.55, 12
  a3L, a3R Singer aM, fM, -12,      kDet, kSpr, kForm, kAmp * 0.35, 13
  a4L, a4R Singer aM, fM, 0,        kDet, kSpr, kForm, kAmp * 0.45, 14
  a5L, a5R Singer aM, fM, kI1,      kDet, kSpr, kForm, kAmp * 0.35, 15

  aL = a1L + a2L + a3L + a4L + a5L
  aR = a1R + a2R + a3R + a4R + a5R
  kDry = 1 - 0.45 * kAmp                  ; headroom for the choir
  out ain1 * kDry + aL * 0.55, ain2 * kDry + aR * 0.55
endin

</CsInstruments>
<CsScore>
i1 0 z
e
</CsScore>
</CsoundSynthesizer>
