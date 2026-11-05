<CsoundSynthesizer>
<CsOptions>
-odac
</CsOptions>
<CsInstruments>
; ---------------------------------------------------------------
;  Csound Visual · binaural 3D (usa audífonos)
;  Abre la ventana 3D › "Panorámica espacial" y arrastra la fuente:
;  la app envía az (azimut, °), el (elevación, °) y dist (distancia)
;  a estos canales, y hrtfmove2 coloca el sonido en el espacio.
; ---------------------------------------------------------------
sr     = 48000
ksmps  = 32
nchnls = 2
0dbfs  = 1

;         nombre   modo tipo defecto  mín   máx
chnk      "az",    1,   2,   45,      0,    360
chnk      "el",    1,   2,   10,      -40,  90
chnk      "dist",  1,   2,   1.2,     0.3,  2.8

; Datos HRTF que vienen con Csound (carpeta samples). La app agrega esa carpeta a SADIR.
gSleft  init "hrtf-48000-left.dat"
gSright init "hrtf-48000-right.dat"
if sr == 44100 then
  gSleft  = "hrtf-44100-left.dat"
  gSright = "hrtf-44100-right.dat"
elseif sr == 96000 then
  gSleft  = "hrtf-96000-left.dat"
  gSright = "hrtf-96000-right.dat"
endif

instr Source
  kaz    chnget "az"
  kel    chnget "el"
  kdist  chnget "dist"
  kaz    port kaz, 0.03
  kel    port kel, 0.03
  kdist  port kdist, 0.05
  ; un sonido con ataques claros, fácil de ubicar: pulsos de ruido filtrado + un tono
  kph    phasor 5                       ; 5 pulsos por segundo
  kenv   = (1 - kph) ^ 6                ; ataque instantáneo y caída rápida
  anoise noise 0.6, 0
  abp    butbp anoise, 1800, 900
  atone  vco2 0.15, 330, 12
  asrc   = (abp * kenv + atone * kenv * 0.5)
  ; la distancia atenúa y oscurece el sonido
  kgain  = 1 / max(kdist, 0.3)
  asrc   tone asrc * kgain * 0.5, 12000 / max(kdist, 1)
  aL, aR hrtfmove2 asrc, kaz, kel, gSleft, gSright
         out aL, aR
endin
</CsInstruments>
<CsScore>
i "Source" 0 3600
</CsScore>
</CsoundSynthesizer>
