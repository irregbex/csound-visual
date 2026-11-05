<CsoundSynthesizer>
<CsOptions>
-odac
</CsOptions>
<CsInstruments>
/*
  AUDIO FX 05 - CONVOLUTION SPACES  (~96 s)

  BIG IDEA
    Any sound can be placed INSIDE any other sound. Convolution (ftconv)
    multiplies every sample of a dry recording by a whole "impulse response":
    a real room's echo, a computed cathedral - or a marimba. The same voice
    and flute travel through four spaces; the last one is impossible: the voice
    is convolved with a marimba recording, so it rings like wooden bars.
    (This is what Cubase's REVerence does; here you can feed it anything.)

  LISTEN FOR
    - With "auto" on, every 12 s the space changes: small real room (a measured
      IR) -> concert hall (2.5 s, computed) -> cathedral (7 s, computed) ->
      inside a marimba -> and back through the cathedral and the hall.
    - Pre-delay and "tone" change the perceived size and the material of the walls.
    - In the marimba space, listen to the voice's words turning into pitched wood.

  SLIDERS
    auto     : 1 = tour through the spaces, 0 = choose with "space".
    space    : 0 = room, 1 = hall, 2 = cathedral, 3 = marimba (crossfades in between).
    wet      : dry/wet balance.
    predelay : 0-150 ms before the reverb starts.
    tone     : dark (0) to bright (1) reverb.

  MIDI: no.   Samples: ../Manual/fox.wav, flute.aiff (sources), rv_stereo.wav (real
  room IR), marimba.aif (used as an impulse response). The hall and cathedral
  IRs are computed at start-up (takes ~1 s).
*/

sr = 48000
ksmps = 64
nchnls = 2
0dbfs = 1
seed 0

chnk "auto",     1, 2, 1,    0, 1
chnk "space",    1, 2, 1,    0, 3
chnk "wet",      1, 2, 0.5,  0, 1
chnk "predelay", 1, 2, 0.02, 0, 0.15
chnk "tone",     1, 2, 0.6,  0, 1
chnset 1,    "auto"
chnset 1,    "space"
chnset 0.5,  "wet"
chnset 0.02, "predelay"
chnset 0.6,  "tone"

; ---------------------------------------------------------------- helpers
; scale a table so that its total energy is iE (all spaces equally loud)
opcode NormIR, 0, ii
  itab, iE xin
  ilen = ftlen(itab)
  isum = 0
  in = 0
  while in < ilen do
    iv = tabi(in, itab)
    isum += iv * iv
    in += 1
  od
  ig = sqrt(iE / (isum + 1e-12))
  in = 0
  while in < ilen do
    tabwi tabi(in, itab) * ig, in, itab
    in += 1
  od
endop

; a computed impulse response: decaying noise whose highs die faster (air),
; with a soft build-up and some early reflections
opcode MakeIR, i, iii
  iT60, iearly, iseed xin
  ilen  = int(iT60 * 1.1 * sr)
  itab  ftgen 0, 0, -ilen, -2, 0
  seed  iseed
  iy    = 0
  in    = 0
  while in < ilen do
    it   = in / sr
    ienv = exp(-6.91 * it / iT60) * (1 - exp(-it / iearly))
    ifc  = 300 + 11000 * exp(-it * 5 / iT60)
    ia   = exp(-6.2832 * ifc / sr)
    iy   = (1 - ia) * random:i(-1, 1) + ia * iy
    tabwi iy * ienv, in, itab
    in += 1
  od
  ik = 0
  while ik < 12 do
    ipos = int(random:i(0.005, 0.07) * sr)
    tabwi tabi(ipos, itab) + random:i(0.2, 0.5) * (ik % 2 == 0 ? 1 : -1) * 0.05, ipos, itab
    ik += 1
  od
  NormIR itab, 0.5
  xout itab
endop

giRoomL ftgen 0, 0, 0, -1, "../Manual/rv_stereo.wav", 0, 0, 1
giRoomR ftgen 0, 0, 0, -1, "../Manual/rv_stereo.wav", 0, 0, 2
giMarim ftgen 0, 0, 0, -1, "../Manual/marimba.aif", 0, 0, 1
NormIR giRoomL, 0.5
NormIR giRoomR, 0.5
NormIR giMarim, 0.25
giHallL MakeIR 2.5, 0.015, 11
giHallR MakeIR 2.5, 0.015, 12
giCathL MakeIR 7,   0.04,  21
giCathR MakeIR 7,   0.04,  22

giTour ftgen 0, 0, 8, -2, 0, 1, 2, 3, 2, 1
gaDry init 0

; ---------------------------------------------------------------- the sources
instr 1
  ; alternate: spoken voice, flute note, voice, flute up a fifth ...
  iWhich = p4
  if iWhich == 0 then
    aS diskin2 "../Manual/fox.wav", 1
  else
    aS diskin2 "../Manual/flute.aiff", p5
  endif
  aE linseg 0, 0.01, 1, p3 - 0.11, 1, 0.1, 0
  gaDry += aS * aE * 0.8
endin

instr 2   ; scheduler: one phrase every 3.2 s, forever
  kCnt init 0
  if metro(1 / 3.2) == 1 then
    kSel = kCnt % 4
    if kSel == 0 || kSel == 2 then
      event "i", 1, 0, 2.8, 0, 1
    else
      event "i", 1, 0, 2.5, 1, (kSel == 1 ? 1 : 1.3348)
    endif
    kCnt += 1
  endif
endin

; ---------------------------------------------------------------- the spaces
instr 10
  kAuto chnget "auto"
  kSp   chnget "space"
  kWet  chnget "wet"
  kPre  chnget "predelay"
  kTone chnget "tone"
  if kAuto > 0.5 then
    ; 12 s per space: room, hall, cathedral, marimba, cathedral, hall, room ...
    kSp tab int(timeinsts() / 12) % 6, giTour
  endif
  kSp   portk kSp, 0.4                    ; ~2 s crossfade between spaces
  kWet  portk kWet, 0.1
  kPre  portk kPre, 0.1
  kTone portk kTone, 0.1

  ; gains of the four spaces: triangular crossfade around 0,1,2,3
  kG0 = max(0, 1 - abs(kSp - 0))
  kG1 = max(0, 1 - abs(kSp - 1))
  kG2 = max(0, 1 - abs(kSp - 2))
  kG3 = max(0, 1 - abs(kSp - 3))

  aIn  vdelay3 gaDry, 1 + kPre * 1000, 200
  aR0L ftconv aIn, giRoomL, 512
  aR0R ftconv aIn, giRoomR, 512
  aR1L ftconv aIn, giHallL, 2048
  aR1R ftconv aIn, giHallR, 2048
  aR2L ftconv aIn, giCathL, 4096   ; long IRs: bigger partitions = cheaper
  aR2R ftconv aIn, giCathR, 4096
  aR3  ftconv aIn, giMarim, 2048
  aR3D delay aR3, 0.013                   ; the marimba is mono: widen it a bit

  aWL = aR0L * kG0 + aR1L * kG1 + aR2L * kG2 + aR3  * kG3
  aWR = aR0R * kG0 + aR1R * kG1 + aR2R * kG2 + aR3D * kG3
  kFc = 1500 * 2 ^ (kTone * 3.5)
  aWL tone aWL, kFc
  aWR tone aWR, kFc

  kEnv linseg 0, 0.05, 1, p3 - 4.05, 1, 4, 0
  aL = (gaDry * (1 - kWet) + aWL * kWet * 2) * kEnv
  aR = (gaDry * (1 - kWet) + aWR * kWet * 2) * kEnv
  out tanh(aL * 0.9), tanh(aR * 0.9)
  clear gaDry
endin

</CsInstruments>
<CsScore>
i2  0 88
i10 0 96
e
</CsScore>
</CsoundSynthesizer>
