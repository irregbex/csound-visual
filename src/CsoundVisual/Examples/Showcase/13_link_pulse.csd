<CsoundSynthesizer>
<CsOptions>
-odac
</CsOptions>
<CsInstruments>
/*
  13 - ABLETON LINK PULSE  (endless: a metronome + arpeggio locked to Link)

  BIG IDEA
    Turn on Ableton Link in Csound Visual (title bar or Settings > Sync) and in
    Ableton Live (the "Link" switch at the top left). From then on the app
    writes six channels into every orchestra, once per control cycle:

        LINK_BPM      session tempo (0 while Link is off)
        LINK_BEAT     beat position on the shared timeline
        LINK_PHASE    position inside the bar, 0 ... quantum (beats per bar)
        LINK_PLAYING  1 while the shared transport plays (Start Stop Sync)
        LINK_PEERS    how many other Link apps are in the session
        LINK_QUANTUM  beats per bar chosen in Settings > Sync

    This piece reads LINK_BEAT and derives a sixteenth-note clock from it, so
    the click, the bass and the arpeggio land exactly on Live's beats and bars:
    change the tempo in Live and Csound follows; change it in Csound Visual's
    piano roll or step sequencer and Live follows. The downbeat (accented
    click, chord change) is phase 0 of the bar, the same "1" Live counts.

    When Link is off (LINK_BPM = 0) the piece runs on its own clock at the
    "tempo" slider, so it always plays something.

  LISTEN FOR
    - A high click on every beat, brighter on the "1" of each bar.
    - A plucked arpeggio in sixteenths over Am - F - C - G (one chord per bar)
      and a soft sub bass on each downbeat.
    - Set Live's tempo to 90 and back to 128: everything glides along.

  SLIDERS
    volume      : master level.
    click       : metronome level (0 = arpeggio only).
    arp         : arpeggio + bass level.
    tempo       : own tempo, used only while Link is off.
    follow_play : 1 = stay silent unless the Link transport plays
                  (needs Start Stop Sync here and in Live).

  The beat position arrives at the start of each control block; the clock
  places every note at its exact offset inside the block (sub-block timing).
*/

sr = 48000
ksmps = 32
nchnls = 2
0dbfs = 1

chnk "volume",      1, 2, 0.7, 0, 1
chnk "click",       1, 2, 0.5, 0, 1
chnk "arp",         1, 2, 0.6, 0, 1
chnk "tempo",       1, 2, 110, 60, 180
chnk "follow_play", 1, 2, 0,   0, 1
chnset 0.7, "volume"
chnset 0.5, "click"
chnset 0.6, "arp"
chnset 110, "tempo"
chnset 0,   "follow_play"

; Chords (MIDI notes) Am - F - C - G, four tones each, one chord per bar.
giChords ftgen 0, 0, 16, -2,  57,60,64,69,  53,57,60,65,  48,55,60,64,  55,59,62,67
; Arpeggio order over the 4 chord tones (+12 = one octave up), 16 sixteenths.
giOrder  ftgen 0, 0, 16, -2,  0,1,2,3, 14,13,2,1, 0,2,1,3, 16,15,14,13

gaRevL init 0
gaRevR init 0

; ------------------------------------------------------------------ the clock
instr 1
  kbpm    chnget "LINK_BPM"
  kbeatL  chnget "LINK_BEAT"
  kphase  chnget "LINK_PHASE"
  kplay   chnget "LINK_PLAYING"
  kpeers  chnget "LINK_PEERS"
  kown    chnget "tempo"
  kfollow chnget "follow_play"

  klinked = (kbpm > 0 ? 1 : 0)
  kTempo  = (klinked == 1 ? kbpm : kown)
  kdt     = ksmps / sr

  ; Own clock (only advances while Link is off).
  kownBeat init 0
  if klinked == 0 then
    kownBeat += kown / 60 * kdt
  endif
  kbeat = (klinked == 1 ? kbeatL : kownBeat)
  ; Beats per bar: LINK_QUANTUM (Settings > Sync), 4 for the own clock.
  kq      chnget "LINK_QUANTUM"
  kq      = (klinked == 1 && kq >= 1 ? kq : 4)
  kph     = (klinked == 1 ? kphase : kownBeat - floor(kownBeat / 4) * 4)

  ; Sixteenth clock: is there a sixteenth boundary inside this block?
  kpos  = kbeat * 4                 ; position in sixteenths at block start
  krate = kTempo / 60 * 4           ; sixteenths per second
  kend  = kpos + krate * kdt
  kprevEnd init -1
  ; After a jump (Link switched on, tempo or beat re-aligned) resync silently.
  if abs(kpos - kprevEnd) > 0.5 then
    kprevEnd = kpos
  endif
  kgate = (kfollow > 0.5 && klinked == 1 && kplay < 0.5 ? 0 : 1)
  if floor(kend) > floor(kpos) && kgate == 1 then
    kstep = floor(kend)             ; the sixteenth that starts inside this block
    koff  = (kstep - kpos) / krate  ; its offset in seconds from the block start
    kstepBeat = kstep / 4
    k16  = kstep - floor(kstepBeat) * 4    ; 0..3 inside the beat
    ; Phase of this sixteenth inside the bar (phase 0 = Live's "1").
    ksp  = kph + (kstepBeat - kbeat)
    ksp  = ksp - floor(ksp / kq) * kq
    if ksp > kq - 0.01 then
      ksp = 0
    endif
    kdown  = (ksp < 0.01 ? 1 : 0)
    kbarNo = round((kstepBeat - ksp) / kq)
    kchord = wrap(kbarNo, 0, 4)            ; one chord per bar
    kslot  = wrap(round(ksp * 4), 0, 16)
    if k16 == 0 then
      schedulek 10, koff, 0.06, kdown
    endif
    korder table kslot, giOrder
    ktone  = wrap(korder, 0, 4)
    koct   = floor(korder / 4)
    knote  table kchord * 4 + ktone, giChords
    schedulek 11, koff, 60 / kTempo * 0.45, knote + 12 * koct, (k16 == 0 ? 0.9 : 0.6)
    if kdown == 1 then
      kroot table kchord * 4, giChords
      schedulek 12, koff, 60 / kTempo * 3.5, kroot - 12
    endif
  endif
  kprevEnd = kend

  ; Status line every 2 s.
  if metro(0.5) == 1 then
    if klinked == 1 then
      printks "Link: %.1f BPM  beat %.2f  phase %.2f  peers %d  playing %d\n", 0, kbpm, kbeatL, kphase, kpeers, kplay
    else
      printks "Link off: own tempo %.1f BPM\n", 0, kown
    endif
  endif
endin

; ------------------------------------------------------------------ click
instr 10
  iacc  = p4
  ivol  chnget "click"
  imast chnget "volume"
  ifreq = (iacc == 1 ? 2400 : 1600)
  aenv  expsegr 1, 0.05, 0.001, 0.01, 0.001
  asig  oscili 0.25 * ivol * imast * (iacc == 1 ? 1 : 0.6), ifreq
  asig  = asig * aenv
  out asig, asig
endin

; ------------------------------------------------------------------ arpeggio pluck
instr 11
  ivol  chnget "arp"
  imast chnget "volume"
  iamp  = 0.11 * p5 * ivol * imast
  ifrq  = cpsmidinn(p4)
  aenv  expsegr 1, p3, 0.02, 0.05, 0.001
  aosc  vco2 iamp, ifrq, 0
  aosc2 vco2 iamp * 0.5, ifrq * 1.005, 0
  kcut  expseg 6000, p3, 500
  aflt  moogladder aosc + aosc2, kcut, 0.25
  asig  = aflt * aenv
  ipan  = 0.3 + 0.4 * ((p4 % 12) / 12)
  aL, aR pan2 asig, ipan
  out aL, aR
  gaRevL += aL * 0.25
  gaRevR += aR * 0.25
endin

; ------------------------------------------------------------------ sub bass
instr 12
  ivol  chnget "arp"
  imast chnget "volume"
  aenv  linsegr 0, 0.01, 1, p3 - 0.01, 0.3, 0.1, 0
  asig  oscili 0.22 * ivol * imast, cpsmidinn(p4)
  asig  = asig * aenv
  out asig, asig
endin

; ------------------------------------------------------------------ reverb
instr 99
  aL, aR reverbsc gaRevL, gaRevR, 0.75, 9000
  out aL, aR
  clear gaRevL, gaRevR
endin

</CsInstruments>
<CsScore>
i 1  0 3600
i 99 0 3600
</CsScore>
</CsoundSynthesizer>
