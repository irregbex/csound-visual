<CsoundSynthesizer>
<CsOptions>
-odac
</CsOptions>
<CsInstruments>
/*
  SHOWCASE 09 - KARPLUS-STRONG GUITAR ENSEMBLE  (endless)

  BIG IDEA
    In 1983 Karplus and Strong found that a burst of noise fed into a short delay
    line, with the output averaged and fed back, sounds like a plucked string:
    the delay length is the pitch, the averaging is the string losing its highs.
    The whole algorithm is the 10-line UDO "KarplusStrong" below. Two such "guitars" play together:
    a strummed acoustic on the left and an arpeggiated one with a dotted-eighth
    echo on the right.

  LISTEN FOR
    - The strums: six strings hit a few milliseconds apart, down and up strokes.
    - Every note is different (new noise burst each time), like a real string.
    - The dotted-eighth echo turning the arpeggio into a cascade.

  SLIDERS
    tempo   : BPM.
    damping : 0 = bright, ringing strings; 1 = palm-muted, dull strings.
    delay   : echo mix on the arpeggio guitar.
    strum   : strum speed (0 = all strings together, 1 = slow, lazy strum).
    space   : room reverb.

  MIDI: no.   Headphones: optional.
*/

sr = 48000
ksmps = 32
nchnls = 2
0dbfs = 1
seed 0

chnk "tempo",   1, 2, 96,   60, 140
chnk "damping", 1, 2, 0.3,  0, 1
chnk "delay",   1, 2, 0.45, 0, 1
chnk "strum",   1, 2, 0.5,  0, 1
chnk "space",   1, 2, 0.35, 0, 1
chnset 96,   "tempo"
chnset 0.3,  "damping"
chnset 0.45, "delay"
chnset 0.5,  "strum"
chnset 0.35, "space"

; chord voicings for the strummed guitar (6 strings, 0 = string not played):
; G, D/F#, Em, C, Am, C, D, D
giChords[][] init 8, 6
giChords fillarray 43, 47, 50, 55, 59, 67, \
                   42, 45, 50, 57, 62, 66, \
                   40, 47, 52, 55, 59, 64, \
                   0,  48, 52, 55, 60, 64, \
                   0,  45, 52, 57, 60, 64, \
                   0,  48, 52, 55, 60, 64, \
                   0,  0,  50, 57, 62, 66, \
                   0,  0,  50, 57, 62, 66
; four arpeggio notes per chord for the second guitar (high register)
giArp[][] init 8, 4
giArp fillarray 67, 71, 74, 79,  66, 69, 74, 78,  64, 67, 71, 76,  64, 67, 72, 76, \
                64, 69, 72, 76,  64, 67, 72, 79,  66, 69, 74, 78,  66, 69, 74, 81
; strum pattern over 8 eighths: 1 = down, -1 = up, 0 = rest   (D . D U . U D U)
giStrum[] fillarray 1, 0, 1, -1, 0, -1, 1, -1
giArpSeq[] fillarray 0, 1, 2, 1, 3, 2, 1, 2

gaRvbL init 0
gaRvbR init 0
gaEcho init 0

; ---------------------------------------------------------------- the algorithm
opcode KarplusStrong, a, iiii
  icps, iamp, ipick, idamp xin
  ; note: delayr/delayw work in blocks of ksmps samples, so the loop is exact as long
  ; as one period is longer than ksmps (32 samples = up to 1500 Hz at 48 kHz)
  iper  = 1 / icps
  ; excitation: one period of white noise, with a comb filter for pick position
  anz   rand iamp
  aexc  = anz * linseg(1, iper, 1, 0, 0, 1, 0)
  aexc  = aexc - delay(aexc, iper * ipick)
  ; the string: delay line + averaging low-pass + loss
  ifb   = 0.998 - 0.03 * idamp                 ; energy kept per round trip
  ismo  = 0.5 + 0.35 * idamp                   ; extra smoothing when damped
  abuf  delayr 0.1
  aout  deltapi iper - ismo / sr               ; compensate the filter's delay
  afilt = aout * (1 - ismo) + delay1(aout) * ismo   ; averaging filter (KS: 0.5 / 0.5)
  delayw aexc + afilt * ifb
  xout aout
endop

; p4 = MIDI note, p5 = amplitude, p6 = pick position (0..0.5), p7 = pan, p8 = echo send
instr String
  idamp chnget "damping"
  icps  = mtof(p4)
  asig  KarplusStrong icps, p5, p6, idamp
  asig  dcblock2 asig
  ; guitar body: warm resonances
  abody = butterbp(asig, 110, 70) * 0.6 + butterbp(asig, 230, 120) * 0.4
  asig  = asig + abody
  asig *= linseg(1, p3 - 0.08, 1, 0.08, 0)
  aL = asig * sqrt(1 - p7)
  aR = asig * sqrt(p7)
  out aL, aR
  gaRvbL += aL * 0.35
  gaRvbR += aR * 0.35
  gaEcho += asig * p8
endin

; ---------------------------------------------------------------- the band
instr Band
  ktempo chnget "tempo"
  kstrum chnget "strum"
  ktempo limit ktempo, 30, 240
  k8th   = 30 / ktempo
  kclock init 1000
  kslot  init -1          ; eighth-note counter
  kclock += 1 / kr
  if kclock >= k8th then
    kclock = 0
    kslot += 1
    kbar   = int(kslot / 8)
    kpos   = kslot % 8
    kch    = kbar % 8
    kcycle = int(kbar / 8)
    ; ---- strummed guitar (left)
    kdir = giStrum[kpos]
    if kdir != 0 then
      kgap = 0.004 + 0.03 * kstrum                 ; time between strings
      kacc = (kpos == 0 ? 1 : (kdir > 0 ? 0.75 : 0.5))
      kring = k8th * 2.2
      ks = 0
      while ks < 6 do
        ; down strokes go low -> high, up strokes high -> low and skip the bass strings
        kstr = (kdir > 0 ? ks : 5 - ks)
        knote = giChords[kch][kstr]
        if knote > 0 && (kdir > 0 || kstr >= 2) then
          kwhen = ks * kgap
          schedulek "String", kwhen, kring + 0.5, knote, 0.32 * kacc * random:k(0.85, 1.1), 0.18, 0.25 + kstr * 0.02, 0
        endif
        ks += 1
      od
    endif
    ; ---- arpeggio guitar (right), from the second time round, with echo
    if kcycle >= 1 || kbar >= 4 then
      kn = giArp[kch][giArpSeq[kpos]] + (kcycle % 3 == 2 ? 12 : 0)
      schedulek "String", 0, 2.5, kn, 0.22 * (kpos % 2 == 0 ? 1 : 0.8), 0.12, 0.8, 1
    endif
    ; ---- bass notes on beats 1 and 3 (a third, deeper "guitar")
    if kpos == 0 || kpos == 4 then
      kb = giChords[kch][0]
      kb = (kb == 0 ? giChords[kch][1] : kb)
      kb = (kb == 0 ? giChords[kch][2] - 12 : kb)
      schedulek "String", 0, k8th * 4, kb - 12, 0.35, 0.3, 0.5, 0
    endif
  endif
endin

; ---------------------------------------------------------------- dotted-eighth echo + room
instr Effects
  kmix   chnget "delay"
  kspace chnget "space"
  ktempo chnget "tempo"
  kmix   port kmix, 0.1
  kspace port kspace, 0.1
  ktempo limit ktempo, 30, 240
  kdt    port 45 / ktempo, 0.3                 ; dotted eighth in seconds
  adt    interp kdt
  ; one delay line, two taps: first echo on the left, second on the right, repeat
  abuf   delayr 3
  aeL    deltapi adt
  aeR    deltapi adt * 2
  delayw gaEcho + aeR * 0.45
  aeL    butterlp aeL, 5000
  aeR    butterlp aeR, 4000
  aL = aeL * kmix * 0.9
  aR = aeR * kmix * 0.9
  arL, arR reverbsc gaRvbL + aL * 0.3, gaRvbR + aR * 0.3, 0.6 + 0.3 * kspace, 9000
  out aL + arL * kspace, aR + arR * kspace
  clear gaRvbL, gaRvbR, gaEcho
endin

instr Master
  aL, aR monitor
  out tanh(aL * 1.3) - aL, tanh(aR * 1.3) - aR
endin

</CsInstruments>
<CsScore>
i "Band"    0 z
i "Effects" 0 z
i "Master"  0 z
</CsScore>
</CsoundSynthesizer>
