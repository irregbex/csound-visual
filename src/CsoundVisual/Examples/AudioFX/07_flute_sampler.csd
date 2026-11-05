<CsoundSynthesizer>
<CsOptions>
-odac --midi-key=4 --midi-velocity-amp=5
</CsOptions>
<CsInstruments>
/*
  AUDIO FX 07 - BUILD YOUR OWN SAMPLER  (~60 s demo, then 2 more minutes to play)

  BIG IDEA
    A sampler is only four ideas, and Csound gives you each one as a building
    block: (1) play a recording faster/slower to change its pitch, (2) LOOP its
    steady middle part with a crossfade (flooper2) so a 2-second flute note can
    be held forever, (3) shape it with an ADSR envelope, (4) let velocity open
    a filter, as on a real instrument. From one flute note you get a playable,
    expressive instrument - like building a HALion patch from scratch.

  LISTEN FOR
    - A flute melody over "flute choir" chords - every note is the SAME 2.6 s
      recording (a C#4), transposed from E2 up to G5.
    - Long chord notes keep sounding past the end of the recording: that's the loop.
    - A delayed vibrato that fades in on long notes, like a real player.
    - Soft notes are darker than loud ones (velocity -> filter).

  SLIDERS
    attack     : 5 ms (articulated) to 1 s (swelling).
    release    : 50 ms to 3 s.
    brightness : how much velocity opens the filter.
    vibrato    : depth of the delayed vibrato.
    space      : reverb.

  MIDI: YES - play it from your keyboard (any channel; velocity-sensitive).
  The demo plays by itself for 60 s; the instrument stays playable for 3 minutes.
  Sample: ../Manual/flute.aiff (44.1 kHz; pitch measured at MIDI 61.9).
*/

sr = 48000
ksmps = 32
nchnls = 2
0dbfs = 1
seed 0

chnk "attack",     1, 3, 0.03, 0.005, 1
chnk "release",    1, 3, 0.4,  0.05,  3
chnk "brightness", 1, 2, 0.6,  0, 1
chnk "vibrato",    1, 2, 0.4,  0, 1
chnk "space",      1, 2, 0.35, 0, 1
chnset 0.03, "attack"
chnset 0.4,  "release"
chnset 0.6,  "brightness"
chnset 0.4,  "vibrato"
chnset 0.35, "space"

massign 0, 1

giFlute ftgen 0, 0, 0, -1, "../Manual/flute.aiff", 0, 0, 1
giBase  = 61.9                          ; the recorded note (MIDI, measured)
giScale = ftsr(giFlute) / sr            ; 44.1 kHz file in a 48 kHz orchestra
gaL init 0
gaR init 0

; ---------------------------------------------------------------- the sampler voice
; p4 = MIDI note, p5 = velocity 0..1 (from MIDI or from the score)
instr 1
  iNote = p4
  iVel  = p5
  iAtt  chnget "attack"
  iRel  chnget "release"
  iBri  chnget "brightness"
  kVibD chnget "vibrato"

  ; (1) pitch = playback speed
  kVib   lfo 1, 5.2 + random:i(-0.3, 0.3)
  kVibEn linseg 0, 0.35, 0, 0.8, 1        ; vibrato arrives after 0.35 s
  kPitch = 2 ^ ((iNote - giBase) / 12 + kVib * kVibEn * kVibD * 0.02) * giScale

  ; (2) play the attack once, then loop 0.75-1.95 s of the file with a 0.3 s crossfade
  ;     (loop points in table seconds = file seconds * giScale)
  aS flooper2 1, kPitch, 0.75 * giScale, 1.95 * giScale, 0.3 * giScale, giFlute, 0.12 * giScale

  ; (3) ADSR (madsr also adds the release after a MIDI note-off)
  aEnv madsr iAtt, 0.25, 0.8, iRel

  ; (4) velocity -> loudness AND brightness
  iCut = 600 + 12000 * iVel ^ 2 * (0.15 + iBri)
  aS   butlp aS, limit:i(iCut, 200, 18000)
  aS  *= aEnv * iVel * 1.1

  iPan = limit:i(0.5 + (iNote - 62) / 50, 0.15, 0.85)
  gaL += aS * sqrt(1 - iPan)
  gaR += aS * sqrt(iPan)
endin

; ---------------------------------------------------------------- chorus + reverb
instr 99
  kWet chnget "space"
  kWet portk kWet, 0.1
  ; light ensemble: two modulated delays
  aCL vdelay3 gaL, 12 + 3 * lfo:a(1, 0.31), 40
  aCR vdelay3 gaR, 14 + 3 * lfo:a(1, 0.23), 40
  aL = gaL + aCL * 0.35
  aR = gaR + aCR * 0.35
  aRL, aRR reverbsc aL, aR, 0.86, 9000
  out tanh((aL + aRL * kWet) * 0.9), tanh((aR + aRR * kWet) * 0.9)
  clear gaL, gaR
endin

</CsInstruments>
<CsScore>
i99 0 180
; ---- demo (MIDI keyboard works at the same time)
; melody: p4 = note, p5 = velocity
i1   1.000  0.712 69 0.63
i1   1.750  0.712 74 0.74
i1   2.500  1.425 78 0.68
i1   4.000  0.712 76 0.76
i1   4.750  0.712 74 0.77
i1   5.500  0.712 73 0.57
i1   6.250  0.712 74 0.55
i1   7.000  2.137 69 0.84
i1  10.000  0.712 71 0.64
i1  10.750  0.712 74 0.63
i1  11.500  1.425 79 0.90
i1  13.000  0.712 78 0.71
i1  13.750  0.712 76 0.84
i1  14.500  0.712 74 0.72
i1  15.250  0.712 73 0.77
i1  16.000  2.850 74 0.60
i1  19.000  0.712 78 0.77
i1  19.750  0.712 79 0.85
i1  20.500  1.425 81 0.73
i1  22.000  0.712 79 0.81
i1  22.750  0.712 78 0.78
i1  23.500  0.712 76 0.57
i1  24.250  0.712 78 0.82
i1  25.000  2.137 74 0.76
i1  28.000  0.712 71 0.66
i1  28.750  0.712 74 0.56
i1  29.500  1.425 76 0.85
i1  31.000  0.712 78 0.72
i1  31.750  0.712 76 0.80
i1  32.500  0.712 73 0.86
i1  33.250  0.712 71 0.80
i1  34.000  2.850 69 0.87
i1  37.000  0.356 74 0.69
i1  37.375  0.356 76 0.83
i1  37.750  0.712 78 0.71
i1  38.500  1.425 81 0.88
i1  40.000  0.712 83 0.86
i1  40.750  0.712 81 0.58
i1  41.500  0.712 79 0.60
i1  42.250  0.712 76 0.63
i1  43.000  2.850 78 0.89
i1  46.000  4.275 74 0.70
; flute-choir chords (soft = darker)
i1   1.000  3.000 50 0.28
i1   1.020  3.000 57 0.33
i1   1.040  3.000 62 0.38
i1   1.060  3.000 66 0.43
i1   4.000  3.000 47 0.28
i1   4.020  3.000 54 0.33
i1   4.040  3.000 59 0.38
i1   4.060  3.000 62 0.43
i1   7.000  3.000 43 0.28
i1   7.020  3.000 55 0.33
i1   7.040  3.000 59 0.38
i1   7.060  3.000 62 0.43
i1  10.000  3.000 45 0.28
i1  10.020  3.000 57 0.33
i1  10.040  3.000 61 0.38
i1  10.060  3.000 64 0.43
i1  13.000  3.000 50 0.28
i1  13.020  3.000 57 0.33
i1  13.040  3.000 62 0.38
i1  13.060  3.000 66 0.43
i1  16.000  3.000 47 0.28
i1  16.020  3.000 54 0.33
i1  16.040  3.000 59 0.38
i1  16.060  3.000 62 0.43
i1  19.000  3.000 40 0.28
i1  19.020  3.000 55 0.33
i1  19.040  3.000 59 0.38
i1  19.060  3.000 62 0.43
i1  22.000  3.000 45 0.28
i1  22.020  3.000 55 0.33
i1  22.040  3.000 61 0.38
i1  22.060  3.000 64 0.43
i1  25.000  3.000 50 0.28
i1  25.020  3.000 57 0.33
i1  25.040  3.000 62 0.38
i1  25.060  3.000 66 0.43
i1  28.000  3.000 47 0.28
i1  28.020  3.000 54 0.33
i1  28.040  3.000 59 0.38
i1  28.060  3.000 62 0.43
i1  31.000  3.000 43 0.28
i1  31.020  3.000 55 0.33
i1  31.040  3.000 59 0.38
i1  31.060  3.000 62 0.43
i1  34.000  3.000 45 0.28
i1  34.020  3.000 57 0.33
i1  34.040  3.000 61 0.38
i1  34.060  3.000 64 0.43
i1  37.000  3.000 50 0.28
i1  37.020  3.000 57 0.33
i1  37.040  3.000 62 0.38
i1  37.060  3.000 66 0.43
i1  40.000  3.000 43 0.28
i1  40.020  3.000 55 0.33
i1  40.040  3.000 59 0.38
i1  40.060  3.000 64 0.43
i1  43.000  3.000 45 0.28
i1  43.020  3.000 57 0.33
i1  43.040  3.000 61 0.38
i1  43.060  3.000 64 0.43
i1  46.000  3.000 50 0.28
i1  46.020  3.000 57 0.33
i1  46.040  3.000 62 0.38
i1  46.060  3.000 66 0.43
i1  49.000  4.500 50 0.28
i1  49.020  4.500 57 0.33
i1  49.040  4.500 62 0.38
i1  49.060  4.500 66 0.43
e
</CsScore>
</CsoundSynthesizer>
