<CsoundSynthesizer>
<CsOptions>
-odac
</CsOptions>
<CsInstruments>
/*
  AUDIO FX 08 - GRANULAR REBUILD  (~100 s)

  BIG IDEA
    A granulator chops a recording into thousands of tiny overlapping "grains"
    (20-400 ms slices with a smooth fade in/out) and plays them back from a
    read head. If the grains are short, dense and read in order, you hear the
    original recording. Change WHERE they read, HOW LONG they are and at WHAT
    PITCH, and the same sound turns into slow motion, a frozen cloud, or a
    backwards ghost - and back again. Here partikkel (the "all-in-one" granular
    opcode) rebuilds a sung phrase and a marimba line, grain by grain.

  LISTEN FOR
    - 0-12 s  : the singer, recognisable: the phrase is made only of grains.
    - 12-32 s : the read head slows down while the grains grow longer and
                start to scatter in time and in the stereo field; the marimba
                drifts in, also stretched.
    - 32-56 s : the read head stops. A FROZEN, shimmering cloud on one vowel:
                every grain gets its own random pitch (octaves, fifths and a
                little detune), the marimba becomes bells.
    - 56-76 s : the read head runs BACKWARDS and the grains themselves are
                played reversed: the phrase sung back to front.
    - 76-93 s : the head turns around, the grains shrink, the scatter
                disappears... and the original phrase is rebuilt (twice).
    - 93-100 s: the first vowel freezes one last time and fades into the reverb.

  SLIDERS
    auto     : 1 = the piece drives itself (tour above), 0 = YOU drive.
    position : (auto = 0) where the frozen read head sits in the recordings (scrub).
    density  : (auto = 0) grains per second (5 = sparse rain, 200 = continuous).
    grain    : (auto = 0) grain length in ms (10 = buzzy, 500 = smooth smear).
    scatter  : (auto = 0) per-grain pitch scatter + position jitter (0 = pure).
    space    : (auto = 0) reverb amount and stereo width.

  MIDI: no.
  Samples: ../Manual/singFemale.aif and ../Manual/marimba.aif (44.1 kHz mono;
           partikkel reads them at their own rate, so the pitch is correct at 48 kHz).
*/

sr = 48000
ksmps = 32
nchnls = 2
0dbfs = 1

chnk "auto",     1, 2, 1,    0, 1
chnk "position", 1, 2, 0.45, 0, 1
chnk "density",  1, 3, 60,   5, 200
chnk "grain",    1, 3, 120,  10, 500
chnk "scatter",  1, 2, 0.3,  0, 1
chnk "space",    1, 2, 0.5,  0, 1
chnset 1,    "auto"
chnset 0.45, "position"
chnset 60,   "density"
chnset 120,  "grain"
chnset 0.3,  "scatter"
chnset 0.5,  "space"

; ---------------------------------------------------------------- tables
giVoice   ftgen 0, 0, 0, 1, "../Manual/singFemale.aif", 0, 0, 1   ; normalised to peak 1
giMarimba ftgen 0, 0, 0, 1, "../Manual/marimba.aif", 0, 0, 1
giCosine  ftgen 0, 0, 8193, 9, 1, 1, 90                   ; needed by partikkel (trainlets)
giDist    ftgen 0, 0, 32768, 7, 0, 32768, 1               ; for scattered grain timing
giWin     ftgen 0, 0, 4096, 20, 2, 1                      ; Hann grain window: no clicks
giWaveAmp ftgen 0, 0, 8, -2, 0, 0, 1, 0, 0, 0, 0          ; only waveform 1, no trainlet
; per-grain pan positions (0 = left .. 1 = right), random, read in a loop (index 0/1 = loop)
giPan     ftgen 0, 0, 1024, -21, 1
          tablew 0,    0, giPan
          tablew 1000, 1, giPan
; intervals (semitones) a grain may jump to when pitch scatter is on
giInt     ftgen 0, 0, 8, -2, 12, 7, -12, 19, 24, 5, 12, -5

; ---------------------------------------------------------------- the tour (automation)
gkSpd  init 1      ; read-head speed (1 = normal, 0 = frozen, -1 = backwards)
gkRate init 90     ; grains per second
gkSize init 45     ; grain length, ms
gkDist init 0      ; 0 = periodic grains, 1 = scattered in time
gkJit  init 0      ; position jitter, seconds
gkScat init 0      ; pitch scatter 0..1
gkRev  init 0      ; probability that a grain plays reversed
gkMar  init 0      ; marimba layer level
gkWide init 0.3    ; stereo width
gkWet  init 0.2    ; reverb
gkOffV init 0      ; read-head offsets (phase) that park the frozen cloud on a loud vowel
gkOffM init 0

instr 1
  ;                 A: original  B: stretch                   C: frozen cloud           D: backwards                 E: rebuild                  end
  gkSpd  linseg 1, 12.1, 1, 8, 0.3, 8, 0.06, 4, 0.015, 24, 0.015, 6, -0.5, 6, -1, 6, -1, 4, 0, 6, 1, 8.3, 1, 1, 0, 15, 0
  gkRate linseg 90, 12.1, 90, 20, 140, 24, 140, 10, 70, 10, 70, 8, 90, 8.3, 90, 3, 110, 15, 110
  gkSize linseg 45, 12.1, 45, 12, 120, 8, 220, 12, 350, 12, 350, 6, 150, 8, 90, 8, 50, 14.3, 50, 3, 260, 15, 300
  gkDist linseg 0, 12.1, 0, 20, 0.8, 24, 0.8, 10, 0.3, 10, 0.3, 8, 0, 8.3, 0, 3, 0.5, 15, 0.5
  gkJit  linseg 0, 12.1, 0, 20, 0.08, 12, 0.25, 12, 0.2, 10, 0.03, 10, 0.02, 8, 0, 8.3, 0, 3, 0.04, 15, 0.04
  gkScat linseg 0, 12.1, 0, 20, 0.15, 12, 0.8, 8, 0.8, 4, 0.4, 6, 0.1, 8, 0, 22.3, 0, 3, 0.12, 15, 0.12
  gkRev  linseg 0, 56, 0, 4, 1, 14, 1, 4, 0, 1, 0
  gkMar  linseg 0, 16, 0, 10, 0.5, 18, 0.8, 12, 0.6, 20, 0.6, 8, 0.35, 10, 0.35, 5, 0, 1, 0
  gkWide linseg 0.3, 12.1, 0.3, 20, 1, 44, 1, 8, 0.5, 16, 0.8
  gkOffV linseg 0, 14, 0, 18, -0.483, 26, -0.483, 16, 0, 1, 0
  gkOffM linseg 0, 14, 0, 18, -0.381, 26, -0.381, 16, 0, 1, 0
  gkWet  linseg 0.2, 12.1, 0.2, 20, 0.45, 12, 0.6, 12, 0.6, 10, 0.4, 10, 0.35, 8, 0.25, 8.3, 0.25, 3, 0.55, 15, 0.6
endin

; ---------------------------------------------------------------- one granulator voice
; xin: table, read-head phase (0..1), jitter (s), pitch scatter, reverse prob., rate, size, dist, amp
opcode GrainCloud, aa, ikkkkkkkk
  iTab, kPh, kJit, kScat, kRev, kRate, kSize, kDist, kAmp xin
  iDur  = ftlen(iTab) / ftsr(iTab)
  iOrig = 1 / iDur                       ; kwavfreq for original pitch (file's own rate)

  ; per-grain pitch: a little detune always, interval jumps with probability ~scatter
  kDet   random -1, 1
  kJump  random 0, 1
  kIdx   random 0, 7.999
  kSemi  = (kJump < kScat * 0.7 ? table:k(int(kIdx), giInt) : 0)
  kCents = kDet * 35 * kScat + kSemi * 100
  kFlip  random 0, 1
  kSign  = (kFlip < kRev ? -1 : 1)
  kWavF  = kSign * iOrig * cent(kCents)

  ; per-grain read position = head + random jitter (in table phase)
  kJ     random -1, 1
  kPos   = wrap(kPh + kJ * kJit / iDur, 0, 1)
  aPos   = a(kPos)
  aZero  = 0                             ; partikkel wants audio-rate sync / FM inputs

  aL, aR partikkel kRate, kDist, giDist, aZero, 1, giWin, \
                -1, -1, 0.5, 0.5, kSize, kAmp, -1, \
                kWavF, 0, -1, -1, aZero, \
                -1, -1, giCosine, kRate, 1, \
                1, giPan, 0, iTab, iTab, iTab, iTab, \
                giWaveAmp, aPos, aPos, aPos, aPos, \
                1, 1, 1, 1, 200
  xout aL, aR
endop

; ---------------------------------------------------------------- the rebuild
instr 2
  iDurV = ftlen(giVoice) / ftsr(giVoice)
  iDurM = ftlen(giMarimba) / ftsr(giMarimba)

  kAuto chnget "auto"
  kPosS chnget "position"
  kDens chnget "density"
  kGr   chnget "grain"
  kScS  chnget "scatter"
  kSpS  chnget "space"

  ; read heads: integrate the speed (wraps around the recording)
  kPhV  init 0
  kPhM  init 0
  kT    timeinsts
  kRst  trigger kT, 84, 0                ; tour: restart the phrase for the rebuild
  if kRst == 1 then
    kPhV = 0
    kPhM = 0
  endif
  kPhV  = wrap(kPhV + gkSpd * ksmps / (sr * iDurV), 0, 1)
  kPhM  = wrap(kPhM + gkSpd * ksmps / (sr * iDurM), 0, 1)

  kPosP port kPosS, 0.08                 ; manual scrub, smoothed

  if kAuto > 0.5 then
    kHV   = wrap(kPhV + gkOffV, 0, 1)
    kHM   = wrap(kPhM + gkOffM, 0, 1)
    kRate = gkRate
    kSize = gkSize
    kDist = gkDist
    kJit  = gkJit
    kScat = gkScat
    kRev  = gkRev
    kMar  = gkMar
    kWide = gkWide
    kWet  = gkWet
  else
    kHV   = kPosP
    kHM   = kPosP
    kRate = kDens
    kSize = kGr
    kDist = 0.6
    kJit  = 0.01 + 0.2 * kScS
    kScat = kScS
    kRev  = 0
    kMar  = 0.5
    kWide = 0.3 + 0.7 * kSpS
    kWet  = kSpS * 0.8
  endif
  kRate port kRate, 0.1
  kSize port kSize, 0.1
  kDist port kDist, 0.2
  kJit  port kJit, 0.2
  kScat port kScat, 0.2
  kMar  port kMar, 0.2
  kWide port kWide, 0.2
  kWet  port kWet, 0.2

  ; grain amplitude: keep loudness steady when many grains overlap
  kOv   = kRate * kSize / 1000
  ; (dense clouds add up incoherently, so they get less reduction than 1/overlap)
  kAmp  = 0.5 / (max(kOv, 1) ^ 0.4)

  aVL, aVR GrainCloud giVoice,   kHV, kJit, kScat, kRev, kRate, kSize, kDist, kAmp
  aML, aMR GrainCloud giMarimba, kHM, kJit * 0.7, kScat, kRev, kRate * 0.8, kSize, kDist, kAmp * 0.8

  aL = aVL + kMar * aML
  aR = aVR + kMar * aMR

  ; slow automatic gain: evens out the loudness between sparse and dense textures
  kLev  rms (aL + aR) * 0.5, 1
  kAgc  limit 0.11 / (kLev + 0.0001), 0.5, 2.5
  kAgc  port kAgc, 1.2, 1
  aL    = aL * kAgc
  aR    = aR * kAgc

  ; stereo width (mid/side)
  aMid  = (aL + aR) * 0.5
  aSide = (aL - aR) * 0.5 * kWide
  aL    = aMid + aSide
  aR    = aMid - aSide
  aL dcblock2 aL
  aR dcblock2 aR

  ; reverb send: a darker, wider space for the cloud
  aRL, aRR reverbsc aL, aR, 0.86, 7500
  kEnv  linseg 0, 0.05, 1, p3 - 6.05, 1, 6, 0
  aOutL = (aL * (1 - 0.45 * kWet) + aRL * kWet * 0.9) * kEnv
  aOutR = (aR * (1 - 0.45 * kWet) + aRR * kWet * 0.9) * kEnv
  ; gentle safety limiter (rarely touches anything)
  aOutL clip aOutL, 0, 0.98
  aOutR clip aOutR, 0, 0.98
  out aOutL, aOutR
endin

</CsInstruments>
<CsScore>
i1 0 100
i2 0 100
e
</CsScore>
</CsoundSynthesizer>
