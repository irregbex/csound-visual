<CsoundSynthesizer>
<CsOptions>
-odac --midi-key=4 --midi-velocity-amp=5
</CsOptions>
<CsInstruments>
; ---------------------------------------------------------------
;  Csound Visual · demo
;  Every chnk (or chnget) channel becomes a slider in the
;  "Live controls" panel. Move them while it plays (F5).
;  Also play the on-screen keyboard or your MIDI controller:
;  massign sends every MIDI note to the Voice instrument.
; ---------------------------------------------------------------
sr     = 48000
ksmps  = 64
nchnls = 2
0dbfs  = 1

;  type: 2 = linear, 3 = exponential (min > 0)
;         name         mode type  default  min   max
chnk      "cutoff",     1,   3,    1800,    80,   8000
chnk      "resonance",  1,   2,    0.35,    0,    0.95
chnk      "detune",     1,   2,    0.15,    0,    1
chnk      "tempo",      1,   2,    6,       1,    16
chnk      "reverb",     1,   2,    0.35,    0,    1
chnk      "volume",     1,   2,    0.7,     0,    1

massign 0, "Voice"

giScale ftgen 0, 0, 8, -2, 57, 60, 62, 64, 67, 69, 72, 74
gaRevL  init 0
gaRevR  init 0

instr Sequencer
  kTempo chnget "tempo"
  kTrig  metro kTempo
  if kTrig == 1 then
    kIdx  random 0, 7.99
    kNote table int(kIdx), giScale
    kOct  random 0, 1.99
    kPan  random 0.15, 0.85
    schedulek "Voice", 0, 0.35, kNote + int(kOct) * 12, 0.8, kPan
  endif
endin

instr Voice
  ifreq  = cpsmidinn(p4)
  ipan   = p6 > 0 ? p6 : 0.5          ; MIDI notes carry no p6: centre
  kcut   chnget "cutoff"
  kres   chnget "resonance"
  kdet   chnget "detune"
  kvol   chnget "volume"
  krev   chnget "reverb"
  kcut   port kcut, 0.05
  kenv   madsr 0.005, 0.2, 0.5, 0.3
  a1     vco2 0.25, ifreq * (1 + kdet * 0.01)
  a2     vco2 0.25, ifreq * (1 - kdet * 0.01)
  asig   moogladder a1 + a2, limit(kcut * (0.35 + kenv), 40, 18000), kres
  asig   = asig * kenv * kvol * p5
  aL, aR pan2 asig, ipan
         out aL, aR
  gaRevL += aL * krev
  gaRevR += aR * krev
endin

instr Reverb
  aL, aR reverbsc gaRevL, gaRevR, 0.88, 11000
         out aL, aR
         clear gaRevL, gaRevR
endin
</CsInstruments>
<CsScore>
i "Reverb"    0 3600
i "Sequencer" 0 3600
</CsScore>
</CsoundSynthesizer>
