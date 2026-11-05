<CsoundSynthesizer>
<CsOptions>
-odac
</CsOptions>
<CsInstruments>
; ---------------------------------------------------------------
;  Csound Visual · drum kit (synthesised, no samples)
;  Made for Tools › Step sequencer: track n plays instr n.
;  Every drum takes   p4 = MIDI note (tunes the drum; 0 = its default)
;                     p5 = velocity 0..1 (0 = 0.8)
;  so the piano roll can play it too (pitch = tuning).
;  Try it by hand:  i 1 0 1 36 0.9   (kick)   i 6 0 1 50 0.8   (high tom)
; ---------------------------------------------------------------
sr     = 48000
ksmps  = 32
nchnls = 2
0dbfs  = 1

;         name        mode type default  min  max
chnk      "volume",   1,   2,   0.8,     0,   1
chnk      "reverb",   1,   2,   0.18,    0,   1
chnk      "drive",    1,   2,   0.3,     0,   1
; Csound 7: a chnk default does not initialise the channel, so set it here too
; (the app sends the slider values right after the start anyway).
chnset 0.8,  "volume"
chnset 0.18, "reverb"
chnset 0.3,  "drive"

gaMixL init 0         ; dry drum bus
gaMixR init 0
gaRev  init 0         ; reverb send (mono)

; Common start of every drum: note (with default), velocity (with default) and the
; tuning ratio relative to the drum's default note.
opcode DrumIn, ii, ii
  idefault, ipitch xin
  inote = ipitch > 0 ? ipitch : idefault
  xout inote, 2 ^ ((inote - idefault) / 12)
endop

; Sends a mono drum to the mix: pan (0 = left, 1 = right) and reverb amount.
opcode DrumOut, 0, aii
  asig, ipan, isend xin
  aL, aR pan2 asig, ipan
  gaMixL += aL
  gaMixR += aR
  gaRev  += asig * isend
endop

; Six detuned square waves: the classic metallic source of the 808 hats and cymbals.
opcode Metal, a, i
  itune xin
  a1 vco2 0.16, 205.3 * itune, 2, 0.5
  a2 vco2 0.16, 304.4 * itune, 2, 0.5
  a3 vco2 0.16, 369.6 * itune, 2, 0.5
  a4 vco2 0.16, 522.7 * itune, 2, 0.5
  a5 vco2 0.16, 540.0 * itune, 2, 0.5
  a6 vco2 0.16, 800.0 * itune, 2, 0.5
  xout a1 + a2 + a3 + a4 + a5 + a6
endop

instr 1 ; Kick
  inote, itune DrumIn 36, p4
  ivel   = p5 > 0 ? p5 : 0.8
  p3     = 0.7
  apitch expseg 260 * itune, 0.012, 120 * itune, 0.09, 58 * itune, 0.6, 46 * itune
  aenv   expseg 1, 0.06, 0.75, 0.55, 0.0005
  abody  poscil 1, apitch
  ; Short filtered click for the attack.
  aclick rand 1
  aclick butterbp aclick, 3200, 2400
  aclkenv expseg 1, 0.008, 0.0005, 0.1, 0.0005
  asig   = abody * aenv + aclick * aclkenv * 0.35
  asig   = tanh(asig * 1.6) * 0.78 * ivel
  DrumOut asig, 0.5, 0.02
endin

instr 2 ; Snare
  inote, itune DrumIn 38, p4
  ivel   = p5 > 0 ? p5 : 0.8
  p3     = 0.45
  ; Two shell modes with a quick downward bend...
  kbend  expseg 1.25, 0.02, 1, 0.4, 1
  atone1 poscil 0.6, 185 * itune * kbend
  atone2 poscil 0.35, 330 * itune * kbend
  atenv  expseg 1, 0.12, 0.0005, 0.3, 0.0005
  ; ...and the snares: bright noise with a longer tail.
  anoise rand 1
  anoise butterhp anoise, 1800
  anoise butterlp anoise, 9000
  anenv  expseg 1, 0.2, 0.0005, 0.25, 0.0005
  asig   = (atone1 + atone2) * atenv * 0.8 + anoise * anenv * 0.55
  asig   = asig * 0.75 * ivel
  DrumOut asig, 0.46, 0.16
endin

instr 3 ; Closed hat
  inote, itune DrumIn 42, p4
  ivel   = p5 > 0 ? p5 : 0.8
  p3     = 0.12
  ; A closed hat chokes a ringing open hat (like a real hi-hat pedal).
  kfirst init 1
  if kfirst == 1 then
    turnoff2 4, 0, 1
    kfirst = 0
  endif
  ametal Metal itune
  ametal butterhp ametal, 7000
  ametal butterbp ametal, 10000, 6000
  aenv   expseg 1, 0.05, 0.0005, 0.07, 0.0005
  asig   = ametal * aenv * 2.2 * ivel
  DrumOut asig, 0.62, 0.05
endin

instr 4 ; Open hat
  inote, itune DrumIn 46, p4
  ivel   = p5 > 0 ? p5 : 0.8
  p3     = 0.6
  ametal Metal itune
  ametal butterhp ametal, 6500
  ametal butterbp ametal, 9500, 7000
  aenv   expseg 1, 0.45, 0.0005, 0.15, 0.0005
  ; Release ramp so the choke from the closed hat does not click.
  krel   linsegr 1, 1, 1, 0.025, 0
  asig   = ametal * aenv * krel * 1.8 * ivel
  DrumOut asig, 0.62, 0.08
endin

instr 5 ; Clap
  inote, itune DrumIn 39, p4
  ivel   = p5 > 0 ? p5 : 0.8
  p3     = 0.5
  anoise rand 1
  anoise butterbp anoise, 1150 * itune, 900
  ; Three quick hand claps, then the room tail.
  abursts linseg 0, 0.001, 1, 0.009, 0.15, 0.001, 0.9, 0.009, 0.15, 0.001, 0.8, 0.009, 0.1, 0.4, 0
  atail  expseg 0.0005, 0.029, 0.0005, 0.001, 0.6, 0.3, 0.0005, 0.1, 0.0005
  asig   = anoise * (abursts + atail) * 1.5 * ivel
  DrumOut asig, 0.54, 0.25
endin

instr 6 ; Tom
  inote, itune DrumIn 45, p4
  ivel   = p5 > 0 ? p5 : 0.8
  p3     = 0.7
  ifreq  = cpsmidinn(inote)                ; a melodic tom: p4 is its pitch
  apitch expseg ifreq * 1.6, 0.04, ifreq * 1.05, 0.6, ifreq
  aenv   expseg 1, 0.5, 0.0005, 0.2, 0.0005
  abody  poscil 1, apitch
  anoise rand 1
  anoise butterbp anoise, ifreq * 4, ifreq * 3
  anenv  expseg 1, 0.03, 0.0005, 0.6, 0.0005
  asig   = (abody * aenv + anoise * anenv * 0.25) * 0.7 * ivel
  DrumOut asig, 0.4, 0.14
endin

instr 7 ; Rim
  inote, itune DrumIn 37, p4
  ivel   = p5 > 0 ? p5 : 0.8
  p3     = 0.08
  atone  poscil 1, 1720 * itune
  atone2 poscil 0.6, 520 * itune
  anoise rand 1
  anoise butterbp anoise, 2500 * itune, 1500
  aenv   expseg 1, 0.025, 0.0005, 0.05, 0.0005
  asig   = (atone * 0.5 + atone2 * 0.4 + anoise * 0.6) * aenv * 0.7 * ivel
  DrumOut asig, 0.38, 0.1
endin

instr 8 ; Cowbell
  inote, itune DrumIn 56, p4
  ivel   = p5 > 0 ? p5 : 0.8
  p3     = 0.5
  a1     vco2 0.5, 540 * itune, 2, 0.5
  a2     vco2 0.5, 800 * itune, 2, 0.5
  asig   butterbp a1 + a2, 820 * itune, 700
  aenv   expseg 1, 0.015, 0.45, 0.35, 0.0005, 0.1, 0.0005
  asig   = asig * aenv * 0.9 * ivel
  DrumOut asig, 0.58, 0.12
endin

; Master: reverb on the send bus, volume and a soft saturator that keeps the
; sum below full scale (several loud hits at once never clip).
instr 100 ; Master
  kvol   chnget "volume"
  krev   chnget "reverb"
  kdrive chnget "drive"
  kvol   port kvol, 0.02
  arL, arR reverbsc gaRev * krev, gaRev * krev, 0.72, 9000
  aL     = gaMixL + arL
  aR     = gaMixR + arR
  ; tanh soft clip: drive 0 = clean, 1 = punchy; normalised so a full-scale hit stays below 1.
  kpre   = 1 + kdrive * 2
  aL     = tanh(aL * kpre) / tanh(kpre) * 0.95
  aR     = tanh(aR * kpre) / tanh(kpre) * 0.95
         out aL * kvol, aR * kvol
         clear gaMixL, gaMixR, gaRev
endin
</CsInstruments>
<CsScore>
; The master runs for an hour; the step sequencer and the piano roll send the notes live.
i 100 0 3600
</CsScore>
</CsoundSynthesizer>
