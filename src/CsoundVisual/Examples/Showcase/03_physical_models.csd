<CsoundSynthesizer>
<CsOptions>
-odac --midi-key=4 --midi-velocity-amp=5
</CsOptions>
<CsInstruments>
/*
  SHOWCASE 03 - PHYSICAL MODELLING  (playable, ~75 s demo)

  BIG IDEA
    Instead of playing back recordings, physical modelling simulates the object
    itself: a string as a delay line with losses (wgpluck2), a bow sticking and
    slipping on a string (wgbow), a wooden bar or a bronze bell as a bank of
    resonant "modes" struck by a mallet (mode). Change the physics and the
    instrument changes: harder mallet, more damping, more bow pressure...

  LISTEN FOR
    - 0-17 s guitar, 17-34 s marimba, 34-55 s cello over marimba, 55 s+ church bells.
    - How natural the attacks and decays are, and that no two notes are identical.
    - Play your MIDI keyboard at any time: the "model" slider chooses the instrument.

  SLIDERS
    model      : instrument for MIDI keys - 1 guitar, 2 marimba, 3 cello (bowed), 4 bell.
    brightness : material / mallet hardness / pick position (dark wood -> bright metal).
    damping    : how fast the vibration dies (0 = rings long, 1 = muted).
    pressure   : bow pressure (cello) and strike strength (other models).
    space      : reverb amount.

  MIDI: yes (velocity sensitive; the cello sustains while the key is held).
*/

sr = 48000
ksmps = 32
nchnls = 2
0dbfs = 1
seed 0

chnk "model",      1, 1, 1,   1, 4
chnk "brightness", 1, 2, 0.5, 0, 1
chnk "damping",    1, 2, 0.3, 0, 1
chnk "pressure",   1, 2, 3,   1, 5
chnk "space",      1, 2, 0.4, 0, 1
chnset 1,   "model"
chnset 0.5, "brightness"
chnset 0.3, "damping"
chnset 3,   "pressure"
chnset 0.4, "space"

massign 0, "Play"          ; every MIDI channel plays instrument "Play"
giSine ftgen 0, 0, 16384, 10, 1
gaRvbL init 0
gaRvbR init 0

; A bank of resonant modes (recursive UDO: each call adds one mode, then calls
; itself for the next). iRat = frequency ratios, iGain = gains, iT60 = decay time
; of the fundamental in seconds (higher modes decay faster).
opcode ModalBank, a, ai[]i[]iii
  aexc, iRat[], iGain[], ifreq, iT60, idx xin
  irat = iRat[idx]
  iq   = max(5, 0.4548 * ifreq * irat * iT60 / sqrt(irat))   ; Q from T60
  aout mode aexc, ifreq * irat, iq
  aout *= iGain[idx]
  if idx < lenarray(iRat) - 1 then
    anext ModalBank aexc, iRat, iGain, ifreq, iT60, idx + 1
    aout += anext
  endif
  xout aout
endop

; ---------------------------------------------------------------- dispatcher
; p4 = MIDI note, p5 = amplitude 0..1, p6 = model (score only; 0 or missing = slider)
instr Play
  inote  = p4
  iamp   = p5
  imodel = p6
  if imodel < 1 then
    imodel = round(chnget:i("model"))
  endif
  if imodel == 3 then
    ; the bow plays inside this instance so it can follow the key release
    icps   = mtof(inote)
    kpres  chnget "pressure"
    kbr    chnget "brightness"
    kdmp   chnget "damping"
    kpres  port kpres, 0.05
    aenv   linsegr 0, 0.12, 1, 0.3, 0.85, 0.25 + 0.6 * (1 - i(kdmp)), 0
    kvibd  linseg 0, 0.4, 0, 0.8, 1
    kvib   poscil 0.0045 * kvibd, 5.3, giSine
    kfreq  init icps
    kfreq  = icps * (1 + kvib)
    abow   wgbow 0.35, kfreq, kpres, 0.127 + 0.05 * kbr, 0, 0, giSine, icps * 0.5
    abow   dcblock2 abow
    ; cello body: a few broad wooden resonances (band-pass filters)
    abody  = butterbp(abow, 220, 120) + butterbp(abow, 420, 200) * 0.8 + butterbp(abow, 1200, 600) * 0.4
    asig   = (abow * 0.6 + abody) * aenv * iamp * 2
    asig   butterlp asig, 1500 + 7000 * kbr
    out asig * 0.7, asig * 0.8
    gaRvbL += asig * 0.45
    gaRvbR += asig * 0.5
  else
    schedule "Struck", 0, 7, inote, iamp, imodel
    turnoff
  endif
endin

; ---------------------------------------------------------------- struck / plucked models
instr Struck
  inote  = p4
  iamp   = p5
  imodel = p6
  icps   = mtof(inote)
  ibr    chnget "brightness"
  idmp   chnget "damping"
  ipres  chnget "pressure"
  ihard  = ibr * 0.7 + (ipres - 1) / 4 * 0.3        ; mallet/pick hardness 0..1
  ipan   = 0.5 + 0.35 * ((inote - 60) / 30) + random(-0.08, 0.08)
  ipan   limit ipan, 0.05, 0.95
  asig   = 0

  if imodel == 1 then
    ; ---- plucked string (digital waveguide)
    iplk  = 0.08 + 0.35 * (1 - ihard)                 ; pluck nearer the bridge = brighter
    irefl = 0.92 - 0.5 * idmp                          ; energy lost at each reflection
    asig  wgpluck2 iplk, iamp * 0.6, icps, 0.22, irefl
    asig  dcblock2 asig
    abody = butterbp(asig, 110, 60) * 0.8 + butterbp(asig, 240, 120) * 0.5   ; wooden body
    asig  = asig * 0.7 + abody
    asig  butterlp asig, 1200 + 9000 * ihard
    asig *= linseg(1, p3 - 0.05, 1, 0.05, 0)
  elseif imodel == 2 then
    ; ---- marimba: a rosewood bar (modes 1 : 3.99 : 10.65) + a tube resonator
    imal  = 0.0025 - 0.0018 * ihard                  ; mallet contact time
    aexc  linseg 0, imal * 0.5, 1, imal * 0.5, 0, 1, 0
    aexc  *= iamp / (0.225 * sr * imal)              ; normalise the mallet impulse
    iR[]  fillarray 1, 3.99, 10.65
    iG[]  fillarray 1, 0.35 * (0.3 + ihard), 0.12 * ihard * ihard
    iT    = (1.8 - 1.6 * idmp) * (440 / icps) ^ 0.35
    asig  ModalBank aexc, iR, iG, icps, iT, 0
    asig += mode(aexc, icps, max(5, 0.4548 * icps * iT * 1.3)) * 0.5   ; tube resonator
    asig *= 0.6
  else
    ; ---- church bell: hum, prime, tierce (minor 3rd), quint, nominal...
    imal  = 0.003 - 0.0022 * ihard
    aexc  linseg 0, imal * 0.5, 1, imal * 0.5, 0, 1, 0
    aexc  *= iamp / (0.225 * sr * imal)
    iRb[] fillarray 0.5, 1, 1.183, 1.506, 2.0, 2.514, 3.011
    iGb[] fillarray 0.6, 0.8, 0.7, 0.35, 0.8, 0.4 * ihard + 0.1, 0.3 * ihard + 0.05
    iT    = (7 - 5 * idmp) * (300 / icps) ^ 0.3
    asig  ModalBank aexc, iRb, iGb, icps, iT, 0
    asig *= 0.35
  endif
  asig *= linseg(1, p3 - 0.1, 1, 0.1, 0)
  aL = asig * sqrt(1 - ipan)
  aR = asig * sqrt(ipan)
  out aL, aR
  gaRvbL += aL * 0.5
  gaRvbR += aR * 0.5
endin

; ---------------------------------------------------------------- room + master
instr Room
  kspace chnget "space"
  kspace port kspace, 0.2
  aL, aR reverbsc gaRvbL, gaRvbR, 0.7 + 0.25 * kspace, 8000
  out aL * kspace * 1.3, aR * kspace * 1.3
  clear gaRvbL, gaRvbR
endin

instr Master
  aL, aR monitor
  out tanh(aL * 1.2) - aL, tanh(aR * 1.2) - aR
endin

</CsInstruments>
<CsScore>
; p4 = MIDI note  p5 = amplitude  p6 = model (1 guitar, 2 marimba, 3 cello, 4 bell)
i "Room"   0 85
i "Master" 0 85
; ---- 1. guitar: fingerpicking (wgpluck2 waveguide)
i "Play"   0.500  1.00  45 0.72 1
i "Play"   0.750  1.00  57 0.50 1
i "Play"   1.000  1.00  60 0.49 1
i "Play"   1.250  1.00  64 0.51 1
i "Play"   1.500  1.00  60 0.51 1
i "Play"   1.750  1.00  57 0.46 1
i "Play"   2.000  1.00  52 0.45 1
i "Play"   2.250  1.00  60 0.53 1
i "Play"   2.500  1.00  41 0.73 1
i "Play"   2.750  1.00  53 0.47 1
i "Play"   3.000  1.00  57 0.55 1
i "Play"   3.250  1.00  60 0.50 1
i "Play"   3.500  1.00  57 0.53 1
i "Play"   3.750  1.00  53 0.50 1
i "Play"   4.000  1.00  48 0.51 1
i "Play"   4.250  1.00  57 0.47 1
i "Play"   4.500  1.00  48 0.76 1
i "Play"   4.750  1.00  55 0.54 1
i "Play"   5.000  1.00  60 0.50 1
i "Play"   5.250  1.00  64 0.52 1
i "Play"   5.500  1.00  60 0.52 1
i "Play"   5.750  1.00  55 0.46 1
i "Play"   6.000  1.00  52 0.53 1
i "Play"   6.250  1.00  60 0.51 1
i "Play"   6.500  1.00  43 0.73 1
i "Play"   6.750  1.00  50 0.45 1
i "Play"   7.000  1.00  55 0.54 1
i "Play"   7.250  1.00  59 0.50 1
i "Play"   7.500  1.00  55 0.52 1
i "Play"   7.750  1.00  50 0.54 1
i "Play"   8.000  1.00  47 0.52 1
i "Play"   8.250  1.00  55 0.54 1
i "Play"   8.500  1.00  45 0.74 1
i "Play"   8.750  1.00  57 0.53 1
i "Play"   9.000  1.00  60 0.49 1
i "Play"   9.250  1.00  64 0.54 1
i "Play"   9.500  1.00  60 0.54 1
i "Play"   9.750  1.00  57 0.46 1
i "Play"  10.000  1.00  52 0.46 1
i "Play"  10.250  1.00  60 0.47 1
i "Play"  10.500  1.00  41 0.80 1
i "Play"  10.750  1.00  53 0.49 1
i "Play"  11.000  1.00  57 0.51 1
i "Play"  11.250  1.00  60 0.48 1
i "Play"  11.500  1.00  57 0.50 1
i "Play"  11.750  1.00  53 0.49 1
i "Play"  12.000  1.00  48 0.49 1
i "Play"  12.250  1.00  57 0.51 1
i "Play"  12.500  1.00  40 0.76 1
i "Play"  12.750  1.00  52 0.54 1
i "Play"  13.000  1.00  56 0.52 1
i "Play"  13.250  1.00  59 0.54 1
i "Play"  13.500  1.00  56 0.54 1
i "Play"  13.750  1.00  52 0.55 1
i "Play"  14.000  1.00  47 0.52 1
i "Play"  14.250  1.00  56 0.47 1
i "Play"  14.500  1.00  40 0.79 1
i "Play"  14.750  1.00  52 0.55 1
i "Play"  15.000  1.00  56 0.54 1
i "Play"  15.250  1.00  59 0.51 1
i "Play"  15.500  1.00  56 0.52 1
i "Play"  15.750  1.00  52 0.47 1
i "Play"  16.000  1.00  47 0.53 1
i "Play"  16.250  1.00  56 0.51 1
i "Play"  16.500  1.00  45 0.60 1
i "Play"  16.535  1.00  52 0.60 1
i "Play"  16.570  1.00  57 0.60 1
i "Play"  16.605  1.00  60 0.60 1
i "Play"  16.640  1.00  64 0.60 1
; ---- 2. marimba: sixteenth-note arpeggios (modal synthesis)
i "Play"  18.000  1.00  64 0.78 2
i "Play"  18.125  1.00  69 0.51 2
i "Play"  18.250  1.00  72 0.59 2
i "Play"  18.375  1.00  76 0.60 2
i "Play"  18.500  1.00  76 0.76 2
i "Play"  18.625  1.00  81 0.58 2
i "Play"  18.750  1.00  76 0.54 2
i "Play"  18.875  1.00  76 0.52 2
i "Play"  19.000  1.00  72 0.78 2
i "Play"  19.125  1.00  69 0.58 2
i "Play"  19.250  1.00  64 0.59 2
i "Play"  19.375  1.00  69 0.50 2
i "Play"  19.500  1.00  72 0.81 2
i "Play"  19.625  1.00  76 0.50 2
i "Play"  19.750  1.00  76 0.57 2
i "Play"  19.875  1.00  81 0.53 2
i "Play"  20.000  1.00  60 0.84 2
i "Play"  20.125  1.00  65 0.60 2
i "Play"  20.250  1.00  69 0.55 2
i "Play"  20.375  1.00  72 0.60 2
i "Play"  20.500  1.00  72 0.78 2
i "Play"  20.625  1.00  77 0.51 2
i "Play"  20.750  1.00  72 0.56 2
i "Play"  20.875  1.00  72 0.50 2
i "Play"  21.000  1.00  69 0.77 2
i "Play"  21.125  1.00  65 0.54 2
i "Play"  21.250  1.00  60 0.56 2
i "Play"  21.375  1.00  65 0.52 2
i "Play"  21.500  1.00  69 0.75 2
i "Play"  21.625  1.00  72 0.59 2
i "Play"  21.750  1.00  72 0.53 2
i "Play"  21.875  1.00  77 0.60 2
i "Play"  22.000  1.00  64 0.84 2
i "Play"  22.125  1.00  67 0.54 2
i "Play"  22.250  1.00  72 0.55 2
i "Play"  22.375  1.00  76 0.55 2
i "Play"  22.500  1.00  76 0.81 2
i "Play"  22.625  1.00  79 0.56 2
i "Play"  22.750  1.00  76 0.56 2
i "Play"  22.875  1.00  76 0.56 2
i "Play"  23.000  1.00  72 0.84 2
i "Play"  23.125  1.00  67 0.55 2
i "Play"  23.250  1.00  64 0.54 2
i "Play"  23.375  1.00  67 0.57 2
i "Play"  23.500  1.00  72 0.77 2
i "Play"  23.625  1.00  76 0.53 2
i "Play"  23.750  1.00  76 0.60 2
i "Play"  23.875  1.00  79 0.55 2
i "Play"  24.000  1.00  59 0.80 2
i "Play"  24.125  1.00  62 0.50 2
i "Play"  24.250  1.00  67 0.54 2
i "Play"  24.375  1.00  71 0.56 2
i "Play"  24.500  1.00  71 0.75 2
i "Play"  24.625  1.00  74 0.56 2
i "Play"  24.750  1.00  71 0.56 2
i "Play"  24.875  1.00  71 0.51 2
i "Play"  25.000  1.00  67 0.81 2
i "Play"  25.125  1.00  62 0.55 2
i "Play"  25.250  1.00  59 0.57 2
i "Play"  25.375  1.00  62 0.54 2
i "Play"  25.500  1.00  67 0.82 2
i "Play"  25.625  1.00  71 0.57 2
i "Play"  25.750  1.00  71 0.50 2
i "Play"  25.875  1.00  74 0.51 2
i "Play"  26.000  1.00  64 0.82 2
i "Play"  26.125  1.00  69 0.60 2
i "Play"  26.250  1.00  72 0.53 2
i "Play"  26.375  1.00  76 0.55 2
i "Play"  26.500  1.00  76 0.81 2
i "Play"  26.625  1.00  81 0.53 2
i "Play"  26.750  1.00  76 0.54 2
i "Play"  26.875  1.00  76 0.53 2
i "Play"  27.000  1.00  72 0.79 2
i "Play"  27.125  1.00  69 0.56 2
i "Play"  27.250  1.00  64 0.53 2
i "Play"  27.375  1.00  69 0.54 2
i "Play"  27.500  1.00  72 0.83 2
i "Play"  27.625  1.00  76 0.50 2
i "Play"  27.750  1.00  76 0.56 2
i "Play"  27.875  1.00  81 0.57 2
i "Play"  28.000  1.00  60 0.78 2
i "Play"  28.125  1.00  65 0.52 2
i "Play"  28.250  1.00  69 0.58 2
i "Play"  28.375  1.00  72 0.52 2
i "Play"  28.500  1.00  72 0.77 2
i "Play"  28.625  1.00  77 0.54 2
i "Play"  28.750  1.00  72 0.57 2
i "Play"  28.875  1.00  72 0.51 2
i "Play"  29.000  1.00  69 0.78 2
i "Play"  29.125  1.00  65 0.53 2
i "Play"  29.250  1.00  60 0.58 2
i "Play"  29.375  1.00  65 0.54 2
i "Play"  29.500  1.00  69 0.84 2
i "Play"  29.625  1.00  72 0.52 2
i "Play"  29.750  1.00  72 0.53 2
i "Play"  29.875  1.00  77 0.57 2
i "Play"  30.000  1.00  59 0.84 2
i "Play"  30.125  1.00  64 0.55 2
i "Play"  30.250  1.00  68 0.52 2
i "Play"  30.375  1.00  71 0.51 2
i "Play"  30.500  1.00  71 0.80 2
i "Play"  30.625  1.00  76 0.52 2
i "Play"  30.750  1.00  71 0.58 2
i "Play"  30.875  1.00  71 0.58 2
i "Play"  31.000  1.00  68 0.77 2
i "Play"  31.125  1.00  64 0.53 2
i "Play"  31.250  1.00  59 0.58 2
i "Play"  31.375  1.00  64 0.56 2
i "Play"  31.500  1.00  68 0.83 2
i "Play"  31.625  1.00  71 0.53 2
i "Play"  31.750  1.00  71 0.51 2
i "Play"  31.875  1.00  76 0.53 2
i "Play"  32.000  1.00  59 0.83 2
i "Play"  32.125  1.00  64 0.53 2
i "Play"  32.250  1.00  68 0.53 2
i "Play"  32.375  1.00  71 0.54 2
i "Play"  32.500  1.00  71 0.79 2
i "Play"  32.625  1.00  76 0.54 2
i "Play"  32.750  1.00  71 0.59 2
i "Play"  32.875  1.00  71 0.52 2
i "Play"  33.000  1.00  68 0.75 2
i "Play"  33.125  1.00  64 0.59 2
i "Play"  33.250  1.00  59 0.59 2
i "Play"  33.375  1.00  64 0.60 2
i "Play"  33.500  1.00  68 0.79 2
i "Play"  33.625  1.00  71 0.60 2
i "Play"  33.750  1.00  71 0.59 2
i "Play"  33.875  1.00  76 0.52 2
i "Play"  34.000  1.00  69 0.80 2
i "Play"  34.000  1.00  57 0.70 2
; ---- 3. cello (wgbow bowed string) over a soft marimba
i "Play"  35.000  1.00  64 0.40 2
i "Play"  35.500  1.00  69 0.30 2
i "Play"  36.000  1.00  72 0.30 2
i "Play"  36.500  1.00  76 0.30 2
i "Play"  37.000  1.00  60 0.40 2
i "Play"  37.500  1.00  65 0.30 2
i "Play"  38.000  1.00  69 0.30 2
i "Play"  38.500  1.00  72 0.30 2
i "Play"  39.000  1.00  64 0.40 2
i "Play"  39.500  1.00  67 0.30 2
i "Play"  40.000  1.00  72 0.30 2
i "Play"  40.500  1.00  76 0.30 2
i "Play"  41.000  1.00  59 0.40 2
i "Play"  41.500  1.00  62 0.30 2
i "Play"  42.000  1.00  67 0.30 2
i "Play"  42.500  1.00  71 0.30 2
i "Play"  43.000  1.00  64 0.40 2
i "Play"  43.500  1.00  69 0.30 2
i "Play"  44.000  1.00  72 0.30 2
i "Play"  44.500  1.00  76 0.30 2
i "Play"  45.000  1.00  60 0.40 2
i "Play"  45.500  1.00  65 0.30 2
i "Play"  46.000  1.00  69 0.30 2
i "Play"  46.500  1.00  72 0.30 2
i "Play"  47.000  1.00  59 0.40 2
i "Play"  47.500  1.00  64 0.30 2
i "Play"  48.000  1.00  68 0.30 2
i "Play"  48.500  1.00  71 0.30 2
i "Play"  49.000  1.00  59 0.40 2
i "Play"  49.500  1.00  64 0.30 2
i "Play"  50.000  1.00  68 0.30 2
i "Play"  50.500  1.00  71 0.30 2
i "Play"  51.000  1.00  64 0.40 2
i "Play"  51.500  1.00  69 0.30 2
i "Play"  52.000  1.00  72 0.30 2
i "Play"  52.500  1.00  76 0.30 2
i "Play"  53.000  1.00  64 0.40 2
i "Play"  53.500  1.00  69 0.30 2
i "Play"  54.000  1.00  72 0.30 2
i "Play"  54.500  1.00  76 0.30 2
i "Play"  35.000  1.58  45 0.80 3
i "Play"  36.500  0.58  47 0.80 3
i "Play"  37.000  2.08  48 0.80 3
i "Play"  39.000  1.08  52 0.80 3
i "Play"  40.000  1.08  50 0.80 3
i "Play"  41.000  1.58  48 0.80 3
i "Play"  42.500  0.58  47 0.80 3
i "Play"  43.000  2.08  45 0.80 3
i "Play"  45.000  1.08  43 0.80 3
i "Play"  46.000  1.08  45 0.80 3
i "Play"  47.000  2.08  47 0.80 3
i "Play"  49.000  2.08  44 0.80 3
i "Play"  51.000  4.08  45 0.80 3
; ---- 4. church bells (inharmonic modes) and a last guitar chord
i "Play"  57.000  1.00  64 0.75 4
i "Play"  57.550  1.00  62 0.77 4
i "Play"  58.100  1.00  60 0.73 4
i "Play"  58.650  1.00  59 0.70 4
i "Play"  59.200  1.00  57 0.66 4
i "Play"  59.750  1.00  55 0.67 4
i "Play"  60.300  1.00  52 0.65 4
i "Play"  60.850  1.00  50 0.61 4
i "Play"  61.400  1.00  64 0.72 4
i "Play"  61.950  1.00  62 0.66 4
i "Play"  62.500  1.00  60 0.76 4
i "Play"  63.050  1.00  59 0.61 4
i "Play"  63.600  1.00  57 0.78 4
i "Play"  64.150  1.00  55 0.74 4
i "Play"  64.700  1.00  52 0.78 4
i "Play"  65.250  1.00  50 0.78 4
i "Play"  66.500  1.00  45 0.80 4
i "Play"  66.520  1.00  57 0.80 4
i "Play"  66.540  1.00  64 0.80 4
i "Play"  67.000  1.00  45 0.55 1
i "Play"  67.050  1.00  52 0.55 1
i "Play"  67.100  1.00  57 0.55 1
i "Play"  67.150  1.00  60 0.55 1
i "Play"  67.200  1.00  64 0.55 1
i "Play"  67.250  1.00  69 0.55 1
e
</CsScore>
</CsoundSynthesizer>
