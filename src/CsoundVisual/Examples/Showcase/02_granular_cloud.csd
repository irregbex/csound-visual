<CsoundSynthesizer>
<CsOptions>
-odac
</CsOptions>
<CsInstruments>
/*
  SHOWCASE 02 - GRANULAR CLOUD  (endless)

  BIG IDEA
    Granular synthesis chops sound into thousands of tiny "grains" (10-500 ms),
    each one a separate note with its own position, pitch, pan and envelope.
    Here a simple melody is recorded live into a 10-second circular buffer and a
    grain scheduler sprays up to 200 grains per second out of it. Every grain is
    just one instance of instrument "Grain": Csound happily runs hundreds at once.

  LISTEN FOR
    - The plain melody (left in the mix at "source" level) dissolving into a halo.
    - Pitch scatter: grains jump to octaves and fifths -> a shimmering chord.
    - Freeze: the buffer stops recording and the current moment hangs in the air.
    - Small grains = buzzing/rain textures; big grains = smooth, choral clouds.

  SLIDERS
    density : grains per second (2 = sparse droplets, 200 = dense cloud).
    size    : grain length in seconds (0.01 = clicks, 0.5 = long overlapping swells).
    scatter : how far grains jump in pitch (0 = unison, 1 = octaves, fifths, detune).
    freeze  : 0 = live, 1 = stop recording and loop the frozen moment forever.
    source  : level of the dry melody that feeds the granulator.
    space   : reverb amount.

  MIDI: no.   Headphones: recommended (grains are spread across the stereo field).
*/

sr = 48000
ksmps = 32
nchnls = 2
0dbfs = 1
seed 0

chnk "density", 1, 3, 40,   2, 200
chnk "size",    1, 3, 0.12, 0.01, 0.5
chnk "scatter", 1, 2, 0.5,  0, 1
chnk "freeze",  1, 1, 0,    0, 1
chnk "source",  1, 2, 0.3,  0, 1
chnk "space",   1, 2, 0.6,  0, 1
chnset 40,   "density"
chnset 0.12, "size"
chnset 0.5,  "scatter"
chnset 0,    "freeze"
chnset 0.3,  "source"
chnset 0.6,  "space"

giLen  = 2 ^ 19                                 ; 524288 samples = 10.9 s
giBuf  ftgen 0, 0, giLen, 2, 0                  ; circular recording buffer
giHann ftgen 0, 0, 8193, 20, 2, 1               ; grain window
giSine ftgen 0, 0, 16384, 10, 1
giMel[] fillarray 62, 65, 67, 69, 72, 74, 69, 67, 65, 62, 60, 62, 57, 60, 62, 67, 69, 65
giIv[]  fillarray 0, 12, -12, 7, 19, 24, 0, 5   ; intervals a grain may jump to

gkWrite init 0          ; current write position in samples
gaSrc   init 0
gaRvbL  init 0
gaRvbR  init 0

; ---------------------------------------------------------------- source melody
; A slow "glass voice" melody with a few kalimba plucks in between.
instr Source
  kclk metro 0.75
  kn init 0
  if kclk == 1 then
    knote = giMel[kn % lenarray(giMel)]
    schedulek "Voice", 0, 1.9, knote, 0.22
    if kn % 3 == 1 then
      schedulek "Kalimba", 0.33, 2, knote + 12, 0.25
      schedulek "Kalimba", 0.66, 2, knote + 19, 0.18
    endif
    kn += 1
  endif
endin

instr Voice
  icps = mtof(p4)
  aenv linseg 0, 0.25, 1, p3 - 0.75, 0.8, 0.5, 0
  kvib poscil 0.006, 5.2, giSine
  kf = icps * (1 + kvib)
  a1 poscil 1, kf, giSine
  a2 poscil 0.5, kf * 2, giSine
  a3 poscil 0.28, kf * 3, giSine
  a4 poscil 0.12, kf * 5, giSine
  abr butterbp rand:a(1), icps * 3, icps
  asig = (a1 + a2 + a3 + a4 + abr * 0.15) * aenv * p5
  gaSrc += asig
endin

instr Kalimba
  icps = mtof(p4)
  aenv expseg 1, p3, 0.001
  asig poscil 1, icps, giSine
  asig += poscil(0.3, icps * 5.4, giSine) * expseg(1, 0.15, 0.001, 1, 0.001)
  gaSrc += asig * aenv * p5
endin

; ---------------------------------------------------------------- recorder
instr Recorder
  kfrz chnget "freeze"
  ksrc chnget "source"
  ksrc port ksrc, 0.05
  aph  phasor sr / giLen
  aidx = aph * giLen
  if kfrz < 0.5 then
    tablew gaSrc, aidx, giBuf     ; record only while not frozen
    gkWrite = k(aidx)
  endif
  aout = gaSrc * ksrc
  out aout, aout
  gaRvbL += aout * 0.3
  gaRvbR += aout * 0.3
  gaSrc = 0
endin

; ---------------------------------------------------------------- grain scheduler
instr Cloud
  kdens  chnget "density"
  ksize  chnget "size"
  kscat  chnget "scatter"
  kfrz   chnget "freeze"
  kdens  limit kdens, 1, 400
  ksize  limit ksize, 0.005, 1
  ; trigger with a randomly jittered rate so grains never sound like a buzz
  knext  init 0
  ktime  timeinsts
  if ktime >= knext then
    knext = ktime + (1 / kdens) * random:k(0.4, 1.6)
    ; pitch: jump to a musical interval with probability "scatter"
    ksemi = 0
    if random:k(0, 1) < kscat then
      ksemi = giIv[int(random:k(0, lenarray(giIv) - 0.01))]
    endif
    ksemi += random:k(-1, 1) * kscat * 0.25          ; detune (up to 25 cents)
    kratio = semitone(ksemi)
    ; read position: behind the write head, far enough not to cross it
    kback = (ksize * kratio + 0.05 + random:k(0, 0.4 + 2.5 * kscat)) * sr
    kpos = gkWrite - kback
    kpos = (kpos < 0 ? kpos + giLen : kpos)
    kamp = 0.5 / sqrt(max(1, kdens * ksize))         ; keep loudness steady
    schedulek "Grain", 0, ksize, kpos, kratio, kamp, random:k(0, 1)
  endif
endin

instr Grain
  istart = p4
  iratio = p5
  ipan   = p7
  aph  line 0, p3, 1
  aenv tablei aph, giHann, 1
  aidx = istart + aph * (p3 * sr * iratio)
  asig tablei aidx, giBuf, 0, 0, 1                  ; wrap around the circular buffer
  asig *= aenv * p6
  aL = asig * sqrt(1 - ipan)
  aR = asig * sqrt(ipan)
  out aL, aR
  gaRvbL += aL
  gaRvbR += aR
endin

; ---------------------------------------------------------------- reverb + master
instr Space
  kspace chnget "space"
  kspace port kspace, 0.2
  aL, aR reverbsc gaRvbL, gaRvbR, 0.75 + 0.2 * kspace, 10000
  out aL * kspace * 1.2, aR * kspace * 1.2
  clear gaRvbL, gaRvbR
endin

instr Master
  aL, aR monitor
  out tanh(aL * 1.9) - aL, tanh(aR * 1.9) - aR
endin

</CsInstruments>
<CsScore>
i "Source"   0 z
i "Recorder" 0 z
i "Cloud"    0 z
i "Space"    0 z
i "Master"   0 z
</CsScore>
</CsoundSynthesizer>
