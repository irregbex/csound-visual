<Cabbage>
form caption("Drum Toy") size(640, 300), colour(12, 16, 28), pluginId("cvd3")
label bounds(12, 4, 300, 16), text("Click the squares to build a beat"), fontColour(140, 160, 210), align("left")
label bounds(86, 22, 38, 16), text("1"), fontColour(110, 130, 170)
label bounds(132, 22, 38, 16), text("2"), fontColour(110, 130, 170)
label bounds(178, 22, 38, 16), text("3"), fontColour(110, 130, 170)
label bounds(224, 22, 38, 16), text("4"), fontColour(110, 130, 170)
label bounds(270, 22, 38, 16), text("5"), fontColour(110, 130, 170)
label bounds(316, 22, 38, 16), text("6"), fontColour(110, 130, 170)
label bounds(362, 22, 38, 16), text("7"), fontColour(110, 130, 170)
label bounds(408, 22, 38, 16), text("8"), fontColour(110, 130, 170)
label bounds(12, 52, 70, 20), text("Kick"), fontColour(255, 120, 60), align("left")
checkbox bounds(86, 44, 38, 38), channel("k1"), value(1), colour:1(255, 120, 60), colour:0(28, 36, 58)
checkbox bounds(132, 44, 38, 38), channel("k2"), value(0), colour:1(255, 120, 60), colour:0(28, 36, 58)
checkbox bounds(178, 44, 38, 38), channel("k3"), value(0), colour:1(255, 120, 60), colour:0(28, 36, 58)
checkbox bounds(224, 44, 38, 38), channel("k4"), value(0), colour:1(255, 120, 60), colour:0(28, 36, 58)
checkbox bounds(270, 44, 38, 38), channel("k5"), value(1), colour:1(255, 120, 60), colour:0(28, 36, 58)
checkbox bounds(316, 44, 38, 38), channel("k6"), value(0), colour:1(255, 120, 60), colour:0(28, 36, 58)
checkbox bounds(362, 44, 38, 38), channel("k7"), value(0), colour:1(255, 120, 60), colour:0(28, 36, 58)
checkbox bounds(408, 44, 38, 38), channel("k8"), value(1), colour:1(255, 120, 60), colour:0(28, 36, 58)
label bounds(12, 98, 70, 20), text("Snare"), fontColour(90, 180, 255), align("left")
checkbox bounds(86, 90, 38, 38), channel("s1"), value(0), colour:1(90, 180, 255), colour:0(28, 36, 58)
checkbox bounds(132, 90, 38, 38), channel("s2"), value(0), colour:1(90, 180, 255), colour:0(28, 36, 58)
checkbox bounds(178, 90, 38, 38), channel("s3"), value(1), colour:1(90, 180, 255), colour:0(28, 36, 58)
checkbox bounds(224, 90, 38, 38), channel("s4"), value(0), colour:1(90, 180, 255), colour:0(28, 36, 58)
checkbox bounds(270, 90, 38, 38), channel("s5"), value(0), colour:1(90, 180, 255), colour:0(28, 36, 58)
checkbox bounds(316, 90, 38, 38), channel("s6"), value(0), colour:1(90, 180, 255), colour:0(28, 36, 58)
checkbox bounds(362, 90, 38, 38), channel("s7"), value(1), colour:1(90, 180, 255), colour:0(28, 36, 58)
checkbox bounds(408, 90, 38, 38), channel("s8"), value(0), colour:1(90, 180, 255), colour:0(28, 36, 58)
label bounds(12, 144, 70, 20), text("Hat"), fontColour(120, 230, 150), align("left")
checkbox bounds(86, 136, 38, 38), channel("h1"), value(1), colour:1(120, 230, 150), colour:0(28, 36, 58)
checkbox bounds(132, 136, 38, 38), channel("h2"), value(1), colour:1(120, 230, 150), colour:0(28, 36, 58)
checkbox bounds(178, 136, 38, 38), channel("h3"), value(1), colour:1(120, 230, 150), colour:0(28, 36, 58)
checkbox bounds(224, 136, 38, 38), channel("h4"), value(0), colour:1(120, 230, 150), colour:0(28, 36, 58)
checkbox bounds(270, 136, 38, 38), channel("h5"), value(1), colour:1(120, 230, 150), colour:0(28, 36, 58)
checkbox bounds(316, 136, 38, 38), channel("h6"), value(1), colour:1(120, 230, 150), colour:0(28, 36, 58)
checkbox bounds(362, 136, 38, 38), channel("h7"), value(1), colour:1(120, 230, 150), colour:0(28, 36, 58)
checkbox bounds(408, 136, 38, 38), channel("h8"), value(1), colour:1(120, 230, 150), colour:0(28, 36, 58)
button bounds(466, 44, 162, 38), channel("run"), text("Stopped", "Running"), value(1), colour:0(40, 40, 52), colour:1(60, 170, 90)
button bounds(466, 90, 162, 38), channel("fill"), text("Hold for a fill"), latched(0), colour:0(40, 40, 52), colour:1(90, 180, 255)
combobox bounds(466, 140, 162, 28), channel("kit"), items("Analog kit", "Toy kit", "Metal kit"), value(1)
groupbox bounds(10, 186, 618, 106), text("Groove and sound"), colour(18, 24, 40), outlineColour(40, 60, 100), fontColour(180, 200, 240) {
  rslider bounds(10, 24, 80, 80), channel("tempo"), range(60, 180, 112, 1, 1), text("Tempo BPM"), trackerColour(255, 200, 60)
  rslider bounds(100, 24, 80, 80), channel("swing"), range(0, 0.5, 0.12, 1, 0.01), text("Swing"), trackerColour(255, 200, 60)
  rslider bounds(190, 24, 80, 80), channel("tune"), range(-12, 12, 0, 1, 1), text("Tune st"), trackerColour(120, 230, 150)
  rslider bounds(280, 24, 80, 80), channel("decay"), range(0.3, 2, 1, 0.6, 0.01), text("Decay"), trackerColour(120, 230, 150)
  rslider bounds(370, 24, 80, 80), channel("crush"), range(3, 16, 16, 1, 1), text("Crush bits"), trackerColour(255, 90, 90)
  rslider bounds(460, 24, 70, 80), channel("room"), range(0, 0.6, 0.15), text("Room"), trackerColour(90, 180, 255)
  rslider bounds(538, 24, 70, 80), channel("volume"), range(0, 1, 0.6), text("Volume"), trackerColour(90, 180, 255)
}
</Cabbage>
<CsoundSynthesizer>
<CsOptions>
-n -d -+rtmidi=NULL -M0 -m0d
</CsOptions>
<CsInstruments>
/*
  CABBAGE 03 - DRUM TOY  (an 8-step drum machine built only from Cabbage widgets)

  The 24 squares are Cabbage checkboxes (channels k1..k8, s1..s8, h1..h8). A clock
  instrument reads them at every step with cabbageGetValue (a Cabbage opcode that
  Csound Visual provides automatically) and fires synthesised drums. Change the
  pattern, tempo and swing while it plays; hold «fill» for a snare roll; «Crush»
  reduces the bit depth for a lo-fi sound.
*/
sr = 48000
ksmps = 32
nchnls = 2
0dbfs = 1

giSine ftgen 0, 0, 8192, 10, 1
gaDrums init 0

; Clock: eighth notes with swing (every other step is delayed by «swing»).
instr Clock
  krun   cabbageGetValue "run"
  ktempo cabbageGetValue "tempo"
  kswing cabbageGetValue "swing"
  kfill  cabbageGetValue "fill"
  kstep init 0
  knext init 0
  kt timeinsts
  if krun == 1 && kt >= knext then
    event "i", "Step", 0, 0.01, kstep % 8
    kdur = 30 / ktempo * (kstep % 2 == 0 ? 1 + kswing : 1 - kswing)
    knext = max(knext + kdur, kt)
    kstep += 1
    ; Fill: a snare on the off-beat sixteenth too.
    if kfill == 1 then
      event "i", "Snare", kdur / 2, 0.25, 0.45
    endif
  elseif krun == 0 then
    knext = kt
  endif
endin

; One step: read the three squares of this column (i-time) and play what is on.
instr Step
  istep = p4 + 1
  ik cabbageGetValue sprintf("k%d", istep)
  is cabbageGetValue sprintf("s%d", istep)
  ih cabbageGetValue sprintf("h%d", istep)
  ifill cabbageGetValue "fill"
  if ik == 1 then
    schedule "Kick", 0, 0.6
  endif
  if is == 1 || ifill == 1 then
    schedule "Snare", 0, 0.35, ifill == 1 && is == 0 ? 0.55 : 1
  endif
  if ih == 1 then
    schedule "Hat", 0, 0.12, istep % 2 == 1 ? 1 : 0.6
  endif
endin

instr Kick
  ikit   cabbageGetValue "kit"
  itune  cabbageGetValue "tune"
  idecay cabbageGetValue "decay"
  ip = semitone(itune) * (ikit == 2 ? 2 : 1)
  idur = p3 * idecay
  aenv expon 1, idur, 0.001
  apitch expon 160 * ip, 0.12, 45 * ip
  a1 poscil aenv * 0.8, apitch, giSine
  if ikit == 3 then
    a1 = a1 + poscil:a(aenv * 0.25, apitch * 3.7, giSine)
  endif
  gaDrums += a1
endin

instr Snare
  ikit   cabbageGetValue "kit"
  itune  cabbageGetValue "tune"
  idecay cabbageGetValue "decay"
  ip = semitone(itune)
  idur = p3 * idecay
  anoise noise 0.5, 0
  anoise butbp anoise, (ikit == 2 ? 3500 : 2200) * ip, 2500
  abody poscil 0.35, 190 * ip * (ikit == 3 ? 2.3 : 1), giSine
  aenv expon 1, idur, 0.001
  gaDrums += (anoise * 1.3 + abody) * aenv * p4
endin

instr Hat
  ikit   cabbageGetValue "kit"
  idecay cabbageGetValue "decay"
  anoise noise 0.35, 0
  if ikit == 3 then
    anoise = anoise + poscil:a(0.2, 7300, giSine) * poscil:a(1, 5100, giSine)
  endif
  ahp buthp anoise, ikit == 2 ? 9000 : 7000
  aenv expon 1, p3 * idecay, 0.001
  gaDrums += ahp * aenv * p4
endin

; Master: bit crusher, small room, volume.
instr Master
  kbits cabbageGetValue "crush"
  kroom cabbageGetValue "room"
  kvol  cabbageGetValue "volume"
  adry = gaDrums
  gaDrums = 0
  kq = 2 ^ (kbits - 1)
  acr = kbits >= 16 ? adry : round(adry * kq) / kq
  aL, aR reverbsc acr, acr, 0.7, 9000
  aoL = (acr + aL * kroom) * kvol
  aoR = (acr + aR * kroom) * kvol
  out clip(aoL, 1, 0.98), clip(aoR, 1, 0.98)
endin
</CsInstruments>
<CsScore>
i "Clock" 0 z
i "Master" 0 z
</CsScore>
</CsoundSynthesizer>
