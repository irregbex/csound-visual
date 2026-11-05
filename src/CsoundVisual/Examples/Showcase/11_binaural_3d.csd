<CsoundSynthesizer>
<CsOptions>
-odac
</CsOptions>
<CsInstruments>
/*
  SHOWCASE 11 - BINAURAL 3D  (endless)          *** USE HEADPHONES ***

  BIG IDEA
    Your brain locates sounds from tiny differences between your two ears: time,
    level, and the filtering of your head and outer ear (the HRTF, head-related
    transfer function). hrtfmove2 applies measured HRTFs to a mono sound, so on
    headphones it appears OUTSIDE your head: behind you, above you, circling
    around you. Stereo panning can only do left-right; this does full 3D.

  LISTEN FOR
    - A shaker and a music-box melody orbiting your head (front -> right ->
      behind -> left), rising and falling.
    - Behind-you vs in-front-of-you: on headphones that difference is the HRTF.
    - A humming drone you can place anywhere yourself with azimuth/elevation.

  SLIDERS
    speed     : orbit speed in degrees per second (negative = the other way).
    height    : how much the orbit goes up and down (0 = stays at ear level).
    azimuth   : direction of the drone (0 = front, 90 = right, 180 = behind, 270 = left).
    elevation : height of the drone (-40 = below, 0 = ear level, 90 = above).
    drone     : level of the drone.

  MIDI: no.   Headphones: REQUIRED.
  Needs the HRTF data files hrtf-48000-left.dat / hrtf-48000-right.dat
  (Csound "samples" folder, found through the SADIR environment variable).
*/

sr = 48000
ksmps = 32
nchnls = 2
0dbfs = 1
seed 0

chnk "speed",     1, 2, 60,  -180, 180
chnk "height",    1, 2, 0.5,  0, 1
chnk "azimuth",   1, 2, 300,  0, 360
chnk "elevation", 1, 2, 20,  -40, 90
chnk "drone",     1, 2, 0.5,  0, 1
chnset 60,  "speed"
chnset 0.5, "height"
chnset 300, "azimuth"
chnset 20,  "elevation"
chnset 0.5, "drone"

giSine ftgen 0, 0, 16384, 10, 1
gaOrbit init 0
gaRvb   init 0
giMel[] fillarray 72, 76, 79, 83, 84, 83, 79, 76, 74, 77, 81, 79, 76, 72, 71, 74

; ---------------------------------------------------------------- the orbiting source
instr Orbit
  kclk  metro 6                        ; 16th notes at 90 BPM
  kn    init 0
  if kclk == 1 then
    ; shaker on every 16th, accented on the beat
    schedulek "Shaker", 0, 0.12, (kn % 4 == 0 ? 0.5 : 0.25)
    ; music-box note every 8th
    if kn % 2 == 0 then
      schedulek "MusicBox", 0, 1.2, giMel[(kn / 2) % lenarray(giMel)], 0.22
    endif
    kn += 1
  endif
  ; motion
  kspeed chnget "speed"
  kh     chnget "height"
  kspeed port kspeed, 0.3
  kh     port kh, 0.3
  kaz    init 0
  kaz    = kaz + kspeed / kr
  kaz    = kaz - floor(kaz / 360) * 360
  kel    = kh * 50 * sin(times:k() * 0.35)
  kel    limit kel, -40, 90
  aL, aR hrtfmove2 gaOrbit, kaz, kel, "hrtf-48000-left.dat", "hrtf-48000-right.dat"
  out aL, aR
  gaRvb += gaOrbit * 0.25
  gaOrbit = 0
endin

instr Shaker
  anz  butterhp rand:a(1), 5000
  anz  butterbp anz, 8000, 6000
  aenv linseg 0, 0.012, 1, p3 - 0.012, 0
  aenv *= aenv
  gaOrbit += anz * aenv * p4
endin

instr MusicBox
  icps = mtof(p4)
  aenv expseg 1, p3, 0.001
  asig poscil 1, icps, giSine
  asig += poscil(0.35, icps * 4.07, giSine) * expseg(1, 0.2, 0.001, 1, 0.001)
  gaOrbit += asig * aenv * p5
endin

; ---------------------------------------------------------------- the drone you place yourself
instr Drone
  kaz   chnget "azimuth"
  kel   chnget "elevation"
  klev  chnget "drone"
  kaz   port kaz, 0.1
  kel   port kel, 0.1
  klev  port klev, 0.1
  kel   limit kel, -40, 90
  ; humming voice: two detuned saws through "mm"/"oo" formants, slow chord changes
  kroot init 48
  knote[] fillarray 48, 53, 45, 50
  kroot = knote[int(times:k() / 8) % 4]
  kcps  portk mtof:k(kroot), 0.8
  a1    vco2 0.5, kcps * cent(-4)
  a2    vco2 0.5, kcps * 1.5 * cent(3)
  asig  = a1 + a2
  asig  = butterbp(asig, 320, 120) + butterbp(asig, 800, 200) * 0.5 + butterbp(asig, 2600, 400) * 0.15
  kbr   jspline 0.25, 0.2, 0.5
  asig *= (0.75 + kbr) * klev * 0.9
  aL, aR hrtfmove2 asig, kaz, kel, "hrtf-48000-left.dat", "hrtf-48000-right.dat"
  out aL, aR
  gaRvb += asig * 0.1
endin

; ---------------------------------------------------------------- a little room around you
instr Room
  aL, aR reverbsc gaRvb, gaRvb, 0.6, 9000
  out aL * 0.35, aR * 0.35
  gaRvb = 0
endin

instr Master
  aL, aR monitor
  out tanh(aL * 2.2) - aL, tanh(aR * 2.2) - aR
endin

</CsInstruments>
<CsScore>
i "Orbit"  0 z
i "Drone"  0 z
i "Room"   0 z
i "Master" 0 z
</CsScore>
</CsoundSynthesizer>
