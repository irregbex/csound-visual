<CsoundSynthesizer>
<CsOptions>
-odac
</CsOptions>
<CsInstruments>
/*
  SHOWCASE 10 - CONVOLUTION SPACES  (~75 s)

  BIG IDEA
    A room is completely described by its IMPULSE RESPONSE: what you hear after a
    single click. Convolution (ftconv) replays every sample of your sound through
    that response, which is how high-end reverb plug-ins work. Here no recording
    is needed: Csound COMPUTES three impulse responses at start-up (a room, a
    concert hall and a cathedral) from decaying noise whose high frequencies die
    faster, plus a handful of early reflections - and then convolves a melody
    with them in real time.

  LISTEN FOR
    - The hand-clap at the start: it "draws" the room for you.
    - With "tour" on, the melody travels: small room -> hall -> cathedral -> room.
    - Pre-delay separates the dry note from the reverb (sense of distance/size).
    - In the cathedral, staccato notes become sustained chords.

  SLIDERS
    tour     : 1 = the space changes automatically with the music, 0 = use "size".
    size     : 0 = room (1.2 s), 0.5 = hall (3 s), 1 = cathedral (7 s); crossfades.
    wet      : dry/wet balance.
    predelay : time before the reverb starts (43-250 ms; values below 43 ms = 43 ms).
    tone     : darkness/brightness of the reverb tail.

  MIDI: no.   Headphones: optional.
*/

sr = 48000
ksmps = 64
nchnls = 2
0dbfs = 1
seed 0

chnk "tour",     1, 1, 1,    0, 1
chnk "size",     1, 2, 0.5,  0, 1
chnk "wet",      1, 2, 0.45, 0, 1
chnk "predelay", 1, 2, 0.05, 0, 0.25
chnk "tone",     1, 2, 0.6,  0, 1
chnset 1,    "tour"
chnset 0.5,  "size"
chnset 0.45, "wet"
chnset 0.05, "predelay"
chnset 0.6,  "tone"

giSine ftgen 0, 0, 16384, 10, 1
gkTour init 0          ; size requested by the score (used when tour = 1)
gaDry  init 0

; ---------------------------------------------------------------- impulse response maker
; Fills a table with a synthetic impulse response: exponentially decaying noise
; filtered by a low-pass whose cut-off falls over time (air absorbs highs),
; a fade-in (diffuse build-up) and some discrete early reflections.
opcode MakeIR, i, iii
  iT60, iearly, iseed xin
  ilen  = int(iT60 * 1.1 * sr)
  itab  ftgen 0, 0, -ilen, -2, 0
  seed  iseed
  iy    = 0
  ienergy = 0
  in    = 0
  while in < ilen do
    it   = in / sr
    ienv = exp(-6.91 * it / iT60) * (1 - exp(-it / iearly))
    ifc  = 300 + 11000 * exp(-it * 5 / iT60)              ; air absorption
    ia   = exp(-6.2832 * ifc / sr)
    iy   = (1 - ia) * random:i(-1, 1) + ia * iy
    iv   = iy * ienv
    tabwi iv, in, itab
    ienergy += iv * iv
    in += 1
  od
  ; early reflections in the first 80 ms
  ik = 0
  while ik < 14 do
    ipos = int(random:i(0.004, 0.08) * sr)
    ig   = random:i(0.3, 1) * (ik % 2 == 0 ? 1 : -1) * 0.25
    tabwi tabi(ipos, itab) + ig, ipos, itab
    ienergy += ig * ig
    ik += 1
  od
  ; normalise the energy so all spaces are equally loud
  inorm = 0.5 / sqrt(ienergy)
  in = 0
  while in < ilen do
    tabwi tabi(in, itab) * inorm, in, itab
    in += 1
  od
  xout itab
endop

giRoomL MakeIR 1.2, 0.002, 11
giRoomR MakeIR 1.2, 0.002, 12
giHallL MakeIR 3.0, 0.012, 21
giHallR MakeIR 3.0, 0.012, 22
giCathL MakeIR 7.0, 0.035, 31
giCathR MakeIR 7.0, 0.035, 32

; ---------------------------------------------------------------- sounds
; a hand clap: the best way to hear a room
instr Clap
  aenv linseg 0, 0.001, 1, 0.006, 0.15, 0.001, 0.9, 0.006, 0.1, 0.001, 1, 0.09, 0, 1, 0
  anz  butterbp rand:a(1), 1400, 1200
  gaDry += anz * aenv * p4
endin

; a breathy flute: sine + a little 2nd/3rd harmonic + breath noise
instr Flute
  icps = mtof(p4)
  aenv linseg 0, 0.04, 1, p3 - 0.1, 0.8, 0.06, 0
  kvib poscil 0.004, 5, giSine
  asig poscil 1, icps * (1 + kvib), giSine
  asig += poscil(0.2, icps * 2, giSine) + poscil(0.08, icps * 3, giSine)
  abr  butterbp rand:a(1), icps * 2, icps
  abr *= linseg(1, 0.08, 0.3, 1, 0.3)
  gaDry += (asig + abr * 0.4) * aenv * p5
endin

; a celesta-like chime (FM, short)
instr Chime
  icps = mtof(p4)
  aenv expseg 1, p3, 0.001
  amod poscil icps * 2 * expseg(2, 0.3, 0.2, 1, 0.2), icps * 4, giSine
  asig poscil p5, icps + amod, giSine
  gaDry += asig * aenv
endin

; a deep drum
instr Drum
  kf   expseg 90, 0.15, 48, p3, 42
  asig poscil 1, kf, giSine
  asig += butterlp(rand:a(1), 600) * expseg(1, 0.03, 0.001, 1, 0.001) * 0.5
  gaDry += asig * expseg(1, p3, 0.001) * p4
endin

; the score moves the room around (p4 = target size, p5 = glide time)
instr TourTo
  gkTour = p4
endin

; ---------------------------------------------------------------- the convolution reverb
instr Space
  ktour  chnget "tour"
  ksize  chnget "size"
  kwet   chnget "wet"
  kpre   chnget "predelay"
  ktone  chnget "tone"
  ksel   = (ktour > 0.5 ? gkTour : ksize)
  ksel   port ksel, 1.5                     ; smooth travel between spaces
  kwet   port kwet, 0.05
  kpre   port kpre, 0.05
  ktone  port ktone, 0.05
  ; equal-power crossfade: room <-> hall <-> cathedral
  kpos   limit ksel * 2, 0, 2
  kgR    = (kpos < 1 ? cos(kpos * $M_PI_2) : 0)
  kgH    = (kpos < 1 ? sin(kpos * $M_PI_2) : cos((kpos - 1) * $M_PI_2))
  kgC    = (kpos < 1 ? 0 : sin((kpos - 1) * $M_PI_2))
  ; pre-delay (ftconv itself adds 2048 samples = 43 ms of latency, the shortest pre-delay)
  ain    vdelay3 gaDry, max(0, kpre - 2048 / sr) * 1000 + 0.1, 300
  aRL    ftconv ain, giRoomL, 2048
  aRR    ftconv ain, giRoomR, 2048
  aHL    ftconv ain, giHallL, 2048
  aHR    ftconv ain, giHallR, 2048
  aCL    ftconv ain, giCathL, 2048
  aCR    ftconv ain, giCathR, 2048
  awL    = aRL * kgR + aHL * kgH + aCL * kgC
  awR    = aRR * kgR + aHR * kgH + aCR * kgC
  kcut   = 1500 * 2 ^ (ktone * 3.5)
  awL    tone awL, kcut
  awR    tone awR, kcut
  awL    *= 3
  awR    *= 3
  out gaDry * (1 - kwet) + awL * kwet, gaDry * (1 - kwet) + awR * kwet
  gaDry = 0
endin

instr Master
  aL, aR monitor
  out tanh(aL * 1.2) - aL, tanh(aR * 1.2) - aR
endin

</CsInstruments>
<CsScore>
i "Space"  0 73
i "Master" 0 73
; ---- small room  (starts at 0.0 s)
i "TourTo"  0.00 0.1 0.0
i "Clap"    0.30 0.3 0.9
i "Flute"   1.800 0.210 62 0.307
i "Flute"   2.220 0.210 65 0.315
i "Flute"   2.640 0.210 67 0.318
i "Flute"   3.060 0.210 69 0.327
i "Flute"   3.480 0.420 72 0.314
i "Flute"   4.320 0.210 69 0.325
i "Flute"   4.740 0.630 67 0.272
i "Chime"   6.300 1.5 86 0.12
i "Chime"   6.510 1.5 84 0.12
i "Chime"   6.720 1.5 81 0.12
i "Flute"   7.830 0.189 69 0.298
i "Flute"   8.250 0.189 72 0.327
i "Flute"   8.670 0.189 74 0.309
i "Flute"   9.090 0.189 72 0.324
i "Flute"   9.510 0.189 69 0.277
i "Flute"   9.930 0.189 67 0.298
i "Flute"  10.350 0.189 65 0.285
i "Flute"  10.770 0.756 62 0.303
i "Chime"  12.750 1.5 79 0.12
i "Chime"  13.000 1.5 77 0.12
i "Chime"  13.250 1.5 74 0.12
; ---- concert hall  (starts at 15.3 s)
i "TourTo" 13.80 0.1 0.5
i "Clap"   15.60 0.3 0.9
i "Flute"  17.100 0.210 62 0.304
i "Flute"  17.520 0.210 65 0.271
i "Flute"  17.940 0.210 67 0.283
i "Flute"  18.360 0.210 69 0.287
i "Flute"  18.780 0.420 72 0.325
i "Flute"  19.620 0.210 69 0.316
i "Flute"  20.040 0.630 67 0.280
i "Chime"  21.600 1.5 86 0.12
i "Chime"  21.810 1.5 84 0.12
i "Chime"  22.020 1.5 81 0.12
i "Flute"  23.130 0.189 69 0.318
i "Flute"  23.550 0.189 72 0.278
i "Flute"  23.970 0.189 74 0.307
i "Flute"  24.390 0.189 72 0.278
i "Flute"  24.810 0.189 69 0.270
i "Flute"  25.230 0.189 67 0.322
i "Flute"  25.650 0.189 65 0.283
i "Flute"  26.070 0.756 62 0.283
i "Chime"  28.050 1.5 79 0.12
i "Chime"  28.300 1.5 77 0.12
i "Chime"  28.550 1.5 74 0.12
; ---- cathedral  (starts at 31.8 s)
i "TourTo" 30.30 0.1 1.0
i "Clap"   32.10 0.3 0.9
i "Flute"  33.600 0.210 62 0.329
i "Flute"  34.020 0.210 65 0.322
i "Flute"  34.440 0.210 67 0.287
i "Flute"  34.860 0.210 69 0.328
i "Flute"  35.280 0.420 72 0.302
i "Flute"  36.120 0.210 69 0.311
i "Flute"  36.540 0.630 67 0.282
i "Chime"  38.100 1.5 86 0.12
i "Chime"  38.310 1.5 84 0.12
i "Chime"  38.520 1.5 81 0.12
i "Flute"  39.630 0.189 69 0.326
i "Flute"  40.050 0.189 72 0.311
i "Flute"  40.470 0.189 74 0.328
i "Flute"  40.890 0.189 72 0.324
i "Flute"  41.310 0.189 69 0.288
i "Flute"  41.730 0.189 67 0.292
i "Flute"  42.150 0.189 65 0.280
i "Flute"  42.570 0.756 62 0.279
i "Drum"   31.80 3 0.8
i "Flute"  45.050 0.084 74 0.254
i "Flute"  45.260 0.084 72 0.268
i "Flute"  45.470 0.084 69 0.286
i "Flute"  45.680 0.084 72 0.250
i "Flute"  45.890 0.336 74 0.291
i "Flute"  46.730 0.084 77 0.270
i "Flute"  46.940 0.084 76 0.269
i "Flute"  47.150 0.084 74 0.299
i "Flute"  47.360 0.084 72 0.279
i "Flute"  47.570 0.672 69 0.269
i "Chime"  49.450 1.5 81 0.1
i "Chime"  49.630 1.5 84 0.1
i "Chime"  49.810 1.5 86 0.1
i "Chime"  49.990 1.5 89 0.1
i "Flute"  51.170 0.189 69 0.279
i "Flute"  51.590 0.189 72 0.292
i "Flute"  52.010 0.189 74 0.253
i "Flute"  52.430 0.189 72 0.309
i "Flute"  52.850 0.189 69 0.251
i "Flute"  53.270 0.189 67 0.295
i "Flute"  53.690 0.189 65 0.301
i "Flute"  54.110 0.756 62 0.251
; ---- back to the small room  (starts at 58.8 s)
i "TourTo" 57.29 0.1 0.0
i "Clap"   59.09 0.3 0.9
i "Flute"  60.590 0.210 62 0.317
i "Flute"  61.010 0.210 69 0.292
i "Flute"  61.430 0.210 67 0.305
i "Flute"  61.850 0.210 65 0.271
i "Flute"  62.270 0.210 64 0.273
i "Flute"  62.690 0.210 65 0.281
i "Flute"  63.110 0.840 62 0.327
i "Chime"  65.290 1.5 86 0.1
i "Chime"  65.590 1.5 81 0.1
i "Chime"  65.890 1.5 74 0.1
i "Chime"  66.190 1.5 69 0.1
i "Clap"   67.99 0.3 0.9
e
</CsScore>
</CsoundSynthesizer>
