<CsoundSynthesizer>
<CsOptions>
-odac --midi-key=4 --midi-velocity-amp=5
</CsOptions>
<CsInstruments>
/*
  SHOWCASE 05 - FM ELECTRIC PIANO AND BELLS  (playable, ~70 s demo)

  BIG IDEA
    Frequency modulation (the sound of the Yamaha DX7) makes rich, living timbres
    from just two sine waves: one oscillator wobbles the pitch of another so fast
    that new partials appear. The RATIO between the two decides the character
    (whole numbers = harmonic, like a piano; 3.5 = inharmonic, like a bell) and the
    INDEX (how hard it wobbles) decides the brightness. Here a decaying index gives
    the classic "tine" electric piano, followed by FM tubular bells.

  LISTEN FOR
    - The bright "ping" of each note that mellows within a second (falling index).
    - Harder notes are brighter, like a real Rhodes/DX piano (velocity -> index).
    - At ~50 s the same algorithm with ratio 3.5 becomes a carillon of bells.
    - Chorus + autopan widen the piano across the speakers.

  SLIDERS
    index      : depth of modulation (0 = pure sine, 4 = brassy/metallic).
    ratio      : modulator/carrier ratio for MIDI notes (1 = e-piano, 3.5 = bell,
                 try 1.41 or 2.76 for strange metals).
    brightness : amount of the high "tine" attack and velocity sensitivity.
    chorus     : stereo chorus and tremolo depth.
    space      : reverb amount.

  MIDI: yes (velocity sensitive, sustain while held).
*/

sr = 48000
ksmps = 32
nchnls = 2
0dbfs = 1
seed 0

chnk "index",      1, 2, 1.2, 0, 4
chnk "ratio",      1, 2, 1,   0.5, 8
chnk "brightness", 1, 2, 0.5, 0, 1
chnk "chorus",     1, 2, 0.5, 0, 1
chnk "space",      1, 2, 0.3, 0, 1
chnset 1.2, "index"
chnset 1,   "ratio"
chnset 0.5, "brightness"
chnset 0.5, "chorus"
chnset 0.3, "space"

massign 0, "Play"
giSine ftgen 0, 0, 16384, 10, 1
gaChoL init 0
gaChoR init 0
gaRvbL init 0
gaRvbR init 0

; p4 = MIDI note, p5 = amplitude 0..1, p6 = ratio override (score only, 0 = slider)
instr Play
  icps  = mtof(p4)
  ivel  = p5
  iratio = p6
  if iratio <= 0 then
    iratio chnget "ratio"
  endif
  iindex chnget "index"
  ibr    chnget "brightness"
  ; how far the ratio is from a whole number: 0 = harmonic (piano), 1 = bell
  ibell  = min(1, abs(iratio - round(iratio)) * 4)
  ; decay is longer for low notes and for bells
  idec   = (2.5 + 4 * ibell) * (261 / icps) ^ 0.45
  idec   limit idec, 0.6, 12
  irel   = 0.18 + 1.5 * ibell
  ; amplitude envelope (attack, long decay, release on note-off)
  aenv   expsegr 0.001, 0.003, 1, idec, 0.001, irel, 0.0001
  ; tone pair: index falls from bright to mellow, scaled by velocity
  ivsens = 0.4 + 1.2 * ibr
  kidx   expsegr 1, 0.9 + ibell, 0.25, irel, 0.2
  kI1    = iindex * (0.3 + ivsens * ivel) * kidx
  amod1  poscil kI1 * icps * iratio, icps * iratio, giSine
  acar1  poscil 1, icps + amod1, giSine
  ; tine pair: very high modulator, very short index burst = metallic attack
  kI2    expseg 3 * ibr * ivel + 0.01, 0.06, 0.01, 1, 0.01
  amod2  poscil kI2 * icps * 14, icps * 14, giSine
  acar2  poscil 0.35 * (1 - ibell * 0.5), icps * 1.003 + amod2, giSine
  asig   = (acar1 + acar2) * aenv * ivel * 0.28
  ; key-tracked pan: low notes slightly left, high slightly right
  ipan   = 0.5 + (p4 - 60) / 80
  ipan   limit ipan, 0.15, 0.85
  aL = asig * sqrt(1 - ipan)
  aR = asig * sqrt(ipan)
  gaChoL += aL
  gaChoR += aR
endin

; ---------------------------------------------------------------- chorus, tremolo, reverb
instr Effects
  kcho  chnget "chorus"
  kspace chnget "space"
  kcho  port kcho, 0.1
  kspace port kspace, 0.2
  ; stereo autopan (Rhodes "suitcase" tremolo)
  ktrem poscil 0.5 * kcho, 4.2, giSine
  aL = gaChoL * (1 - ktrem)
  aR = gaChoR * (1 + ktrem)
  ; chorus: two slowly modulated short delays, cross-panned
  ad1 poscil 0.0022, 0.31, giSine
  ad2 poscil 0.0022, 0.47, giSine, 0.3
  ac1 vdelay3 aL, (0.009 + ad1) * 1000, 30
  ac2 vdelay3 aR, (0.011 + ad2) * 1000, 30
  aL = aL + ac2 * kcho * 0.7
  aR = aR + ac1 * kcho * 0.7
  arL, arR reverbsc aL, aR, 0.78 + 0.18 * kspace, 9000
  aL += arL * kspace
  aR += arR * kspace
  out aL, aR
  clear gaChoL, gaChoR
endin

instr Master
  aL, aR monitor
  out tanh(aL * 1.2) - aL, tanh(aR * 1.2) - aR
endin

</CsInstruments>
<CsScore>
; p4 = MIDI note  p5 = velocity (0..1)  p6 = ratio (0 = use slider)
i "Effects" 0 72
i "Master"  0 72
; ---- e-piano ballad (ratio from the slider, 1 = harmonic)
i "Play"   0.500  3.11  43 0.75 0
i "Play"   0.500  1.11  58 0.43 0
i "Play"   1.684  1.89  58 0.35 0
i "Play"   0.512  1.11  62 0.47 0
i "Play"   1.694  1.89  62 0.34 0
i "Play"   0.524  1.11  65 0.45 0
i "Play"   1.704  1.89  65 0.37 0
i "Play"   0.536  1.11  69 0.41 0
i "Play"   1.714  1.89  69 0.38 0
i "Play"   0.500  1.18  74 0.63 0
i "Play"   1.684  0.39  72 0.69 0
i "Play"   2.079  1.58  70 0.63 0
i "Play"   3.658  3.11  36 0.75 0
i "Play"   3.658  1.11  58 0.41 0
i "Play"   4.842  1.89  58 0.37 0
i "Play"   3.670  1.11  64 0.48 0
i "Play"   4.852  1.89  64 0.34 0
i "Play"   3.682  1.11  67 0.42 0
i "Play"   4.862  1.89  67 0.39 0
i "Play"   3.694  1.11  69 0.49 0
i "Play"   4.872  1.89  69 0.39 0
i "Play"   3.658  0.79  69 0.68 0
i "Play"   4.447  0.79  70 0.78 0
i "Play"   5.237  1.58  72 0.63 0
i "Play"   6.816  3.11  41 0.75 0
i "Play"   6.816  1.11  57 0.49 0
i "Play"   8.000  1.89  57 0.36 0
i "Play"   6.828  1.11  60 0.41 0
i "Play"   8.010  1.89  60 0.34 0
i "Play"   6.840  1.11  64 0.43 0
i "Play"   8.020  1.89  64 0.41 0
i "Play"   6.852  1.11  67 0.42 0
i "Play"   8.030  1.89  67 0.39 0
i "Play"   6.816  2.37  76 0.72 0
i "Play"   9.184  0.79  74 0.68 0
i "Play"   9.974  3.11  38 0.75 0
i "Play"   9.974  1.11  57 0.45 0
i "Play"  11.158  1.89  57 0.34 0
i "Play"   9.986  1.11  60 0.41 0
i "Play"  11.168  1.89  60 0.35 0
i "Play"   9.998  1.11  64 0.47 0
i "Play"  11.178  1.89  64 0.37 0
i "Play"  10.010  1.11  65 0.43 0
i "Play"  11.188  1.89  65 0.39 0
i "Play"   9.974  0.79  72 0.69 0
i "Play"  10.763  0.79  69 0.67 0
i "Play"  11.553  1.58  72 0.75 0
i "Play"  13.132  3.11  46 0.75 0
i "Play"  13.132  1.11  57 0.47 0
i "Play"  14.316  1.89  57 0.35 0
i "Play"  13.144  1.11  62 0.46 0
i "Play"  14.326  1.89  62 0.38 0
i "Play"  13.156  1.11  65 0.49 0
i "Play"  14.336  1.89  65 0.40 0
i "Play"  13.168  1.11  69 0.43 0
i "Play"  14.346  1.89  69 0.43 0
i "Play"  13.132  1.18  74 0.64 0
i "Play"  14.316  0.39  77 0.69 0
i "Play"  14.711  1.58  76 0.74 0
i "Play"  16.289  3.11  45 0.75 0
i "Play"  16.289  1.11  55 0.42 0
i "Play"  17.474  1.89  55 0.38 0
i "Play"  16.301  1.11  60 0.40 0
i "Play"  17.484  1.89  60 0.40 0
i "Play"  16.313  1.11  64 0.48 0
i "Play"  17.494  1.89  64 0.39 0
i "Play"  16.325  1.11  67 0.49 0
i "Play"  17.504  1.89  67 0.36 0
i "Play"  16.289  1.58  72 0.73 0
i "Play"  17.868  0.79  71 0.72 0
i "Play"  18.658  0.79  72 0.71 0
i "Play"  19.447  3.11  43 0.75 0
i "Play"  19.447  1.11  58 0.45 0
i "Play"  20.632  1.89  58 0.41 0
i "Play"  19.459  1.11  62 0.49 0
i "Play"  20.642  1.89  62 0.38 0
i "Play"  19.471  1.11  65 0.47 0
i "Play"  20.652  1.89  65 0.34 0
i "Play"  19.483  1.11  69 0.47 0
i "Play"  20.662  1.89  69 0.39 0
i "Play"  19.447  1.58  74 0.78 0
i "Play"  21.026  0.79  72 0.75 0
i "Play"  21.816  0.79  70 0.67 0
i "Play"  22.605  3.11  36 0.75 0
i "Play"  22.605  1.11  58 0.44 0
i "Play"  23.789  1.89  58 0.40 0
i "Play"  22.617  1.11  62 0.40 0
i "Play"  23.799  1.89  62 0.38 0
i "Play"  22.629  1.11  64 0.42 0
i "Play"  23.809  1.89  64 0.34 0
i "Play"  22.641  1.11  67 0.41 0
i "Play"  23.819  1.89  67 0.41 0
i "Play"  22.605  3.16  72 0.64 0
i "Play"  25.763  3.11  43 0.75 0
i "Play"  25.763  1.11  58 0.42 0
i "Play"  26.947  1.89  58 0.37 0
i "Play"  25.775  1.11  62 0.49 0
i "Play"  26.957  1.89  62 0.34 0
i "Play"  25.787  1.11  65 0.44 0
i "Play"  26.967  1.89  65 0.38 0
i "Play"  25.799  1.11  69 0.49 0
i "Play"  26.977  1.89  69 0.41 0
i "Play"  25.763  1.18  74 0.76 0
i "Play"  26.947  0.39  72 0.66 0
i "Play"  27.342  1.58  70 0.69 0
i "Play"  28.921  3.11  36 0.75 0
i "Play"  28.921  1.11  58 0.44 0
i "Play"  30.105  1.89  58 0.42 0
i "Play"  28.933  1.11  64 0.50 0
i "Play"  30.115  1.89  64 0.35 0
i "Play"  28.945  1.11  67 0.42 0
i "Play"  30.125  1.89  67 0.35 0
i "Play"  28.957  1.11  69 0.42 0
i "Play"  30.135  1.89  69 0.38 0
i "Play"  28.921  0.79  69 0.71 0
i "Play"  29.711  0.79  70 0.66 0
i "Play"  30.500  0.79  72 0.62 0
i "Play"  31.289  0.79  76 0.69 0
i "Play"  32.079  3.11  41 0.75 0
i "Play"  32.079  1.11  57 0.44 0
i "Play"  33.263  1.89  57 0.39 0
i "Play"  32.091  1.11  60 0.50 0
i "Play"  33.273  1.89  60 0.40 0
i "Play"  32.103  1.11  64 0.45 0
i "Play"  33.283  1.89  64 0.39 0
i "Play"  32.115  1.11  67 0.47 0
i "Play"  33.293  1.89  67 0.34 0
i "Play"  32.079  2.37  79 0.76 0
i "Play"  34.447  0.79  77 0.74 0
i "Play"  35.237  3.11  38 0.75 0
i "Play"  35.237  1.11  57 0.49 0
i "Play"  36.421  1.89  57 0.41 0
i "Play"  35.249  1.11  60 0.44 0
i "Play"  36.431  1.89  60 0.37 0
i "Play"  35.261  1.11  64 0.41 0
i "Play"  36.441  1.89  64 0.39 0
i "Play"  35.273  1.11  65 0.41 0
i "Play"  36.451  1.89  65 0.34 0
i "Play"  35.237  0.79  76 0.65 0
i "Play"  36.026  0.79  74 0.65 0
i "Play"  36.816  1.58  72 0.67 0
i "Play"  38.395  3.11  46 0.75 0
i "Play"  38.395  1.11  57 0.41 0
i "Play"  39.579  1.89  57 0.33 0
i "Play"  38.407  1.11  62 0.42 0
i "Play"  39.589  1.89  62 0.34 0
i "Play"  38.419  1.11  65 0.44 0
i "Play"  39.599  1.89  65 0.33 0
i "Play"  38.431  1.11  69 0.49 0
i "Play"  39.609  1.89  69 0.39 0
i "Play"  38.395  1.18  74 0.64 0
i "Play"  39.579  0.39  77 0.66 0
i "Play"  39.974  1.58  81 0.68 0
i "Play"  41.553  3.11  36 0.75 0
i "Play"  41.553  1.11  58 0.44 0
i "Play"  42.737  1.89  58 0.34 0
i "Play"  41.565  1.11  62 0.48 0
i "Play"  42.747  1.89  62 0.43 0
i "Play"  41.577  1.11  64 0.45 0
i "Play"  42.757  1.89  64 0.38 0
i "Play"  41.589  1.11  67 0.41 0
i "Play"  42.767  1.89  67 0.34 0
i "Play"  41.553  1.58  79 0.67 0
i "Play"  43.132  0.79  76 0.66 0
i "Play"  43.921  0.79  74 0.75 0
i "Play"  44.711  3.11  41 0.75 0
i "Play"  44.711  1.11  57 0.42 0
i "Play"  45.895  1.89  57 0.33 0
i "Play"  44.723  1.11  60 0.50 0
i "Play"  45.905  1.89  60 0.38 0
i "Play"  44.735  1.11  64 0.41 0
i "Play"  45.915  1.89  64 0.38 0
i "Play"  44.747  1.11  67 0.40 0
i "Play"  45.925  1.89  67 0.38 0
i "Play"  44.711  3.16  76 0.78 0
i "Play"  47.868  3.11  41 0.75 0
i "Play"  47.868  1.11  57 0.49 0
i "Play"  47.880  1.11  60 0.47 0
i "Play"  47.892  1.11  64 0.43 0
i "Play"  47.904  1.11  67 0.44 0
; ---- tubular bells: same FM algorithm, ratio 3.5
i "Play"  51.026  3.00  84 0.53 3.5
i "Play"  51.626  3.00  83 0.65 3.5
i "Play"  52.226  3.00  81 0.61 3.5
i "Play"  52.826  3.00  79 0.66 3.5
i "Play"  53.426  3.00  77 0.57 3.5
i "Play"  54.026  3.00  76 0.54 3.5
i "Play"  54.626  3.00  74 0.66 3.5
i "Play"  55.226  3.00  72 0.70 3.5
i "Play"  56.026  3.00  60 0.67 3.5
i "Play"  56.626  3.00  62 0.66 3.5
i "Play"  57.226  3.00  64 0.66 3.5
i "Play"  57.826  3.00  65 0.65 3.5
i "Play"  58.426  3.00  67 0.55 3.5
i "Play"  59.026  3.00  69 0.60 3.5
i "Play"  59.626  3.00  71 0.57 3.5
i "Play"  60.226  3.00  72 0.51 3.5
i "Play"  62.026  4.00  53 0.55 3.5
i "Play"  62.056  4.00  60 0.55 3.5
i "Play"  62.086  4.00  64 0.55 3.5
i "Play"  62.116  4.00  67 0.55 3.5
i "Play"  62.146  4.00  72 0.55 3.5
i "Play"  62.026  5.00  41 0.50 0
i "Play"  62.046  5.00  57 0.50 0
i "Play"  62.066  5.00  60 0.50 0
i "Play"  62.086  5.00  64 0.50 0
i "Play"  62.106  5.00  67 0.50 0
i "Play"  62.126  5.00  74 0.50 0
e
</CsScore>
</CsoundSynthesizer>
