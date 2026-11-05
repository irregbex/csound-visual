<CsoundSynthesizer>
<CsOptions>
-odac
</CsOptions>
<CsInstruments>
/*
  AUDIO FX 02 - BEAT SLICER  (~100 s)

  BIG IDEA
    A drum loop is just 16 little slices of sound. Csound cuts the loop into
    sixteenth notes and a tiny sequencer decides, step by step, which slice to
    play, whether to play it backwards, or to stutter it. Because every slice is
    re-triggered on the tempo grid, TEMPO and PITCH are independent (like REX
    files / Cubase's "audio slices to groove"), and the loop never repeats exactly.

  LISTEN FOR
    - Bars 1-2: the original loop, untouched (plus a little synth bass).
    - Then the sequencer starts improvising: shuffled slices, reversed hits,
      32nd-note stutters and snare-roll fills at the end of every 4th bar.
    - With "auto" on, the tempo goes 120 -> 150 -> 90 BPM: the drums keep their
      pitch and sound, only the groove gets faster/slower.

  SLIDERS
    auto    : 1 = the piece arranges itself (chaos/stutter/tempo change by section).
    tempo   : 80-180 BPM (used when auto = 0).
    chaos   : probability that a step plays a random slice instead of its own.
    stutter : probability of 32nd-note repeats and of rolls at phrase ends.
    reverse : probability that a slice is played backwards.
    pitch   : drum pitch in semitones (-12..+12) - does NOT change the tempo.

  MIDI: no.   Sample: ../Manual/drumsMlp.wav (1 bar, 2 s, 44.1 kHz mono).
*/

sr = 48000
ksmps = 32
nchnls = 2
0dbfs = 1
seed 0

chnk "auto",    1, 2, 1,   0, 1
chnk "tempo",   1, 2, 120, 80, 180
chnk "chaos",   1, 2, 0.3, 0, 1
chnk "stutter", 1, 2, 0.2, 0, 1
chnk "reverse", 1, 2, 0.15, 0, 1
chnk "pitch",   1, 2, 0,   -12, 12
chnset 1,    "auto"
chnset 120,  "tempo"
chnset 0.3,  "chaos"
chnset 0.2,  "stutter"
chnset 0.15, "reverse"
chnset 0,    "pitch"

giLoop ftgen 0, 0, 0, -1, "../Manual/drumsMlp.wav", 0, 0, 1
; 2-bar bass line, one MIDI note per 16th (0 = rest), A minor
giBass ftgen 0, 0, 32, -2, 33,0,0,33, 0,0,45,0, 36,0,0,36, 0,38,0,40, \
                           33,0,0,33, 0,0,45,0, 41,0,0,41, 0,40,0,31
gaDrL init 0
gaDrR init 0
gaBass init 0

; ---------------------------------------------------------------- sequencer
instr 1
  kAuto  chnget "auto"
  kBpm   chnget "tempo"
  kChaos chnget "chaos"
  kStut  chnget "stutter"
  kRev   chnget "reverse"
  kSemi  chnget "pitch"
  kTick  init -1
  kStutNow init 0
  kSlice init 0
  kRevNow init 0

  ; automatic arrangement: 16-bar cycle, chosen per bar
  kBar = int(max(kTick, 0) / 32)
  if kAuto > 0.5 then
    kSec  = kBar % 16
    kBpm  = (kSec < 8 ? 120 : (kSec < 12 ? 150 : 90))
    kPart = kSec % 8
    kChaos = (kPart < 2 ? 0 : (kPart < 4 ? 0.3 : (kPart < 6 ? 0.15 : 0.55)))
    kStut  = (kPart < 2 ? 0 : (kPart < 4 ? 0.1 : (kPart < 6 ? 0.4 : 0.5)))
    kRev   = (kPart < 4 ? 0 : (kPart < 6 ? 0.25 : 0.35))
    if kBar < 2 then
      kChaos = 0
      kStut = 0
      kRev = 0
    endif
  endif
  kRatio = 2 ^ (kSemi / 12)

  k32 metro kBpm / 60 * 8               ; 32nd-note clock
  if k32 == 1 then
    kTick += 1
    kDur16 = 15 / kBpm                  ; one 16th note in seconds
    kStep  = int(kTick / 2) % 16
    kEven  = kTick % 2
    kRoll  = ((kBar % 4 == 3) && (kStep >= 12) && (kStut > 0.25) ? 1 : 0)
    if kEven == 0 then
      ; ----- a new 16th: choose the slice
      kSlice = kStep
      if random:k(0, 1) < kChaos then
        kSlice = int(random:k(0, 15.99))
      endif
      kRevNow  = (random:k(0, 1) < kRev ? 1 : 0)
      kStutNow = (random:k(0, 1) < kStut ? 1 : 0)
      if kRoll == 1 then
        kSlice = 4                      ; the snare slice, rolled with rising pitch
        kRevNow = 0
        kStutNow = 1
      endif
      kPr = kRatio * (kRoll == 1 ? 2 ^ ((kStep - 12) / 6) : 1)
      event "i", 10, 0, (kStutNow == 1 ? kDur16 / 2 : kDur16), kSlice, kRevNow, kPr
      ; bass note on this step (2-bar pattern)
      kNote tab int(kTick / 2) % 32, giBass
      if kNote > 0 then
        event "i", 20, 0, kDur16 * 1.8, kNote
      endif
    elseif kStutNow == 1 then
      ; ----- the off-32nd: repeat the same slice (stutter)
      kPr = kRatio * (kRoll == 1 ? 2 ^ ((kStep - 11.5) / 6) : 1)
      event "i", 10, 0, kDur16 / 2, kSlice, kRevNow, kPr
    endif
  endif
endin

; ---------------------------------------------------------------- slice player
instr 10
  iLen   = ftlen(giLoop)
  iSlice = iLen / 16                    ; slice length in file samples
  iRate  = p6 * ftsr(giLoop) / sr       ; file samples per output sample
  iPlay  = min(p3, iSlice / iRate / sr) ; never read past the slice
  p3     = iPlay
  iSt    = p4 * iSlice
  if p5 > 0.5 then                      ; backwards: from the slice end to its start
    aIdx line iSt + iSlice - 1, p3, iSt + iSlice - 1 - iRate * p3 * sr
  else
    aIdx line iSt, p3, iSt + iRate * p3 * sr
  endif
  aSig  table3 aIdx, giLoop
  aEnv  linseg 0, 0.001, 1, max(p3 - 0.005, 0.001), 1, 0.004, 0
  aSig *= aEnv
  iPan = 0.5 + (p5 > 0.5 ? 0.3 : 0) * (random:i(0, 1) < 0.5 ? -1 : 1)
  gaDrL += aSig * sqrt(1 - iPan)
  gaDrR += aSig * sqrt(iPan)
endin

; ---------------------------------------------------------------- synth bass
instr 20
  iF   = mtof:i(p4)
  aOsc vco2 0.5, iF
  aSub poscil 0.4, iF / 2
  kEnv expseg 1, p3, 0.05
  aFlt moogladder aOsc, 150 + 1600 * kEnv, 0.55
  aAmp linseg 0, 0.003, 1, p3 - 0.013, 0.7, 0.01, 0
  gaBass += tanh(1.5 * (aFlt + aSub)) * aAmp * 0.35
endin

; ---------------------------------------------------------------- mixer
instr 99
  aL = gaDrL * 0.8 + gaBass
  aR = gaDrR * 0.8 + gaBass
  ; a short slap-back echo on the drums adds width
  aEL delay gaDrR, 0.011
  aER delay gaDrL, 0.017
  aL += aEL * 0.12
  aR += aER * 0.12
  kEnv linseg 0, 0.01, 1, p3 - 3.01, 1, 3, 0
  out tanh(aL * kEnv), tanh(aR * kEnv)
  clear gaDrL, gaDrR, gaBass
endin

</CsInstruments>
<CsScore>
i1  0 100
i99 0 100
e
</CsScore>
</CsoundSynthesizer>
