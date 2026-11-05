# Csound Visual — hoja de ruta

## Estado (2026-11-04)

| Etapa | Estado |
|---|---|
| **1 · Audio de baja latencia, ayudas emergentes, documentación** | ✅ Hecho: motor WASAPI propio (compartido y exclusivo, 3 ms medidos), presets de latencia, ksmps, diagnóstico (`--audio-probe`, `--audio-test`), ayudas emergentes en toda la interfaz, documentación bilingüe en HTML con diagramas. |
| **2 · Valores en vivo, MIDI, live coding, grabar, espectro** | ✅ Hecho: valores en vivo (debugger), panel Observar, menú contextual, F1; MIDI WinMM con monitor, MIDI learn y teclado en pantalla/PC; Ctrl+Enter y Ctrl+Shift+Enter; autocompletado; grabar a WAV; espectro. ✅ MIDI 2.0 con Windows MIDI Services (SDK preview 11, verificado por loopback; ver [MIDI](midi.md)). |
| **3 · 3D (OpenGL)** | ✅ Hecho: ventana 3D desacoplable (F11), espectrograma en cascada, pad XYZ, panorámica espacial binaural con plantilla `hrtfmove2`; OpenGL ES 3 sobre ANGLE/D3D11 verificado en la RTX 3090 Ti. Ver [Vistas 3D](visual-3d.md). |
| **4 · Herramientas musicales (piano roll, envolventes, secuenciador, transporte, perillas, OSC, plantillas)** | 🟡 En curso: ✅ **piano roll** y ✅ **secuenciador por pasos** con transporte propio (tempo, loop, swing), programación anticipada sobre el reloj de Csound, exportar/importar a la partitura, grabación MIDI y un kit de batería sintetizado (2026-11-04; ver [Herramientas musicales](herramientas-musicales.md)). Pendiente: envolventes, tablas, perillas/XY, OSC, biblioteca de plantillas. |
| **5 · Host VST3, Cabbage, Link, breakpoints** | ✅ Hecho (2026-11-05): host VST3 con cadena de efectos/instrumentos y sus ventanas ([Host VST3](host-vst3.md)), interfaz Cabbage ([Cabbage](cabbage.md)), Ableton Link ([Ableton Link](ableton-link.md)), breakpoints en el editor. Probado en el PC de desarrollo (Link solo contra un par de prueba). |
| **6 · Diseño sonoro con imágenes: Paint Rooms** | 🟡 En curso: ✅ base compartida y la sala **Paint Synth** (pintar un lienzo tiempo × altura, oírlo en vivo con Csound, renderizar a WAV, afinaciones Scala, pincel armónico; ver [Paint Rooms](salas-de-pintura.md)). Siguen: **Spectrum Room** (sonido → imagen → editar → resíntesis) e **Image Filter** (una imagen como ecualizador estéreo que cambia en el tiempo). |

Propuesta del 2026-11-03, hecha a partir de la documentación de Csound 7, los headers de la API que trae `@Source\Csound7\include` y el inventario real de opcodes de esta instalación.

## Qué trae esta instalación (verificado)

- **1485 opcodes.** Entre ellos:
  - 55 de MIDI, con entrada y salida, arpegiador, MIDI clock y archivos MIDI;
  - 4 de OSC;
  - 51 de análisis espectral (`pvs*`);
  - 17 granulares (`partikkel`, `mincer`, `syncgrain`…);
  - 26 modelos físicos;
  - 31 filtros analógicos virtuales (`moogladder`, `zdf_*`, `diode_ladder`, `K35_*`…);
  - 14 de SoundFont (`sfload`/`sfplay`). Además viene `samples\sf_GMbank.sf2`;
  - 22 de Arduino y puerto serial.
- **No trae** VST, FluidSynth, Ableton Link, Faust ni Python. Habría que compilarlos o hacerlos del lado de la app.
- Plugins: `rtpa` (PortAudio, que incluye **ASIO**: lista Realtek ASIO, Steinberg built-in ASIO y otros), `rtwasapi`, `rtwinmm`, `pmidi` (PortMidi), `ipmidi`, `osc`, `stdutil`.
- **APIs de Csound 7 útiles para la interfaz:**
  - `csdebug.h`: breakpoints por línea, y un callback **en cada ciclo de control** que permite leer las variables de cada instancia;
  - `csound_graph_display.h`: callbacks para dibujar los opcodes `display`/`dispfft`;
  - `csound_rtaudio.h` y `csound_rtmidi.h`: `csoundSetHostAudioIO` y `csoundSetHostMIDIIO`, para que la app maneje el audio y el MIDI;
  - `csound_server.h`: servidor UDP y OSC (`/csound/channel/<nombre>`, `/csound/event`, `/csound/compile`);
  - `ugen.h`: usar opcodes sueltos como objetos.
- **Lenguaje Csound 7:**
  - tipos explícitos (`freq:k`) y globales con `@global`;
  - booleanos;
  - `Complex`;
  - `struct`;
  - `for … in`;
  - arrays literales `[1, 2, 3]`;
  - UDO con la sintaxis `opcode nombre(entradas):(salidas)`;
  - objetos `InstrDef`/`Instr`/`Opcode` (`create`, `play`, `pause`, `setp`);
  - JSON;
  - tablas que no necesitan ser potencias de 2.
- **VST3:**
  - existen los [vst3 opcodes de Michael Gogins](https://github.com/gogins-dev/csound-vst3-opcodes) (`vst3init`, …). Son solo para Csound 7, están en beta, usan GPLv3 y **no abren la ventana del plugin**;
  - desde el 29-10-2025, el [VST 3.8 SDK es MIT](https://sonicstate.com/news/2025/10/30/vst-3-now-available-under-mit-license), así que la app misma puede ser host VST3 sin problemas de licencia.

## Fase 1 — rápido y alto impacto (días)

| # | Función | Cómo |
|---|---|---|
| 1 | **Panel de latencia**: ksmps, `-b` y `-B`, latencia en ms calculada, contador de cortes (xruns) y presets *Ultra baja / Equilibrada / Segura* | opciones al lanzar, más medir el tiempo de `PerformKsmps` y leer los avisos de underrun |
| 2 | **ASIO a la vista**: agrupar los dispositivos por API (ASIO, WASAPI, MME…) y sugerir el driver ASIO de la UR22mkII | parsear `[ASIO, …]` del nombre que da PortAudio |
| 3 | **Valores en vivo**: la tarjeta de ayuda muestra el valor *actual* de `kenv`, `kcut`… mientras suena, con una mini gráfica de su historia; panel "Observar" | callback por ciclo de control de `csdebug.h` |
| 4 | **Entrada MIDI**: elegir el dispositivo, monitor MIDI y **MIDI learn** (clic derecho en un slider → mover una perilla del controlador) | módulo `pmidi` o `csoundSetHostMIDIIO` |
| 5 | **Teclado en pantalla** y el teclado del PC como piano, con velocidad y octava | eventos MIDI del host |
| 6 | **Live coding**: Ctrl+Enter evalúa el `instr` o la selección sin detener el audio; modo "siempre encendido" sin partitura | `csoundStart` antes de compilar, más `csoundCompileOrc` (ya se usa al arrastrar) |
| 7 | **Autocompletar opcodes** con firma, ayuda de argumentos mientras escribes y F1 para abrir la página del manual | ya existe el catálogo de 2516 firmas |
| 8 | **Grabar** lo que suena a WAV con un botón, sin renderizar aparte | `fout`/`monitor` inyectado, o escribir `spout` desde la app |
| 9 | **Analizador de espectro y espectrograma** junto al osciloscopio | FFT de `spout` en la app |

## Fase 2 — herramientas visuales para hacer música (semanas)

| # | Función |
|---|---|
| 10 | **Piano roll de la partitura**: las líneas `i` se ven como notas (tiempo × altura o instrumento), se editan arrastrando y se escriben de vuelta en el código, con un cursor de reproducción sincronizado |
| 11 | **Editor de envolventes**: los puntos de `linseg`/`expseg`/`madsr` se arrastran en una gráfica y el código se actualiza solo |
| 12 | **Editor de tablas**: dibujar armónicos (GEN 10), formas (GEN 7) o escalas (GEN 2) y escuchar el cambio al tiro |
| 13 | **Secuenciador por pasos** (grilla de 16 pasos) que genera una tabla o una partitura (`trigseq`, `timedseq`) y se edita en vivo |
| 14 | **Transporte**: BPM global, metrónomo, compás:tiempo y región de loop (`t`, `metrobpm`) |
| 15 | **Más tipos de control**: perilla, pad XY (dos canales), botón y selector, declarados con un comentario (`chnk "cutoff", … ;@knob`) o con la sintaxis de **Cabbage** — ✅ **Hecho (2026-11-04)** junto con el 23: `;@knob`, `;@slider`, `;@vslider`, `;@button`, `;@momentary`, `;@checkbox`, `;@combo` (elementos separados por barras), `;@xy "y"` (ver [Instrumentos Cabbage](cabbage.md)) |
| 16 | **Control desde el celular por OSC** (TouchOSC u otros): mostrar el puerto y un código QR |
| 17 | **Biblioteca de plantillas modernas**: supersaw, FM, pad granular, caja de ritmos, Karplus-Strong, delay y reverb de envío, sidechain, sonidos GM con `sf_GMbank.sf2` |
| 18 | **Grafo del instrumento**: diagrama `vco2 → moogladder → pan2 → out` generado por el analizador; clic en un nodo para ir al código |
| 19 | **Formas de onda** de los samples que usa el código (`diskin2`, `loscil`); arrastrar un WAV al editor inserta la línea |
| 20 | **Revisión mientras escribes**: compilación en segundo plano con `-n` y subrayado rojo de los errores antes de tocar |

## Fase 3 — proyectos grandes

| # | Función | Notas |
|---|---|---|
| 21 | **Motor de audio propio** (la app maneja ASIO/WASAPI exclusivo y llama `PerformKsmps` dentro del callback) | la latencia más baja posible; es la base para el 22 |
| 22 | **La app como host VST3**, con las ventanas de los plugins dentro de la app: cadena de efectos después de Csound e instrumentos VST3 que alimentan Csound por canales de audio | ✅ **Hecho (2026-11-04)**: puente nativo `CsoundVisualVstHost.dll` (`src/VstHost`, `tools/build_vsthost.ps1`); cadena de efectos e instrumentos VST3 **después** de Csound en el motor «Windows · WASAPI» (antes de medidores y grabación), ventanas propias de los plugins, examen en proceso aparte con caché, cadena y estados guardados, Ctrl+Shift+V; pruebas `--vsthost-test`/`--vsthost-engine-test`. Ver [Plugins VST3 en la app](host-vst3.md). Falta: instrumentos VST3 que entren *a* Csound por canales de audio; Sylenth1 no carga (CET) |
| 22b | Alternativa rápida: compilar los **vst3 opcodes** para Csound 7 en Windows y darles controles (parámetros como sliders, presets) | beta, GPLv3, sin ventana del plugin |
| 23 | **Compatibilidad con Cabbage**: leer la sección `<Cabbage>` y dibujar sus controles, para usar instrumentos existentes y exportarlos como VST3 con Cabbage | ✅ **Hecho (2026-11-04)**: lector tolerante (Cabbage 2 y JSON de Cabbage 3), tarjeta «Interfaz Cabbage» + ventana aparte con perillas, sliders, botones, selectores, pad XY, teclado, etiquetas, grupos e imágenes; los controles mueven los mismos canales que los sliders (valores iniciales al tocar, MIDI learn, Enviar al DAW); reemplazos de `cabbageGetValue`/`SetValue`/`Set`/`Get`/`Changed`; 3 ejemplos en «★ Instrumentos Cabbage». Probado con los 280 ejemplos de Cabbage 2 (242 compilan en Csound 7). Ver [Instrumentos Cabbage](cabbage.md). Falta: `gentable`, `soundfiler`, medidores, `identChannel`, `cabbageCreate` |
| 24 | **Ableton Link** para sincronizar el tempo con DAWs y apps del celular | ✅ **Hecho (2026-11-04)**: Link 3.1.5 integrado en la app (`CsoundVisualLink.dll`, `tools/build_link.ps1`); canales `LINK_BPM/BEAT/PHASE/PLAYING/PEERS/QUANTUM`; piano roll y secuenciador siguen el tempo, empiezan en el compás y quedan en fase; Start Stop Sync; ejemplo Showcase 13; prueba `--link-test`. Ver [Ableton Link](ableton-link.md). Cubase no tiene Link (usa `HOST_*` del plugin) |
| 25 | **Breakpoints** en el margen del editor: pausar e inspeccionar las variables | ✅ hecho 2026-11-04: margen + F9, pausa con el dispositivo en silencio, panel Depurar con todas las variables, continuar / siguiente ciclo, números de línea correctos tras recompilar en vivo — ver [Breakpoints](inteligencia-del-editor.md#breakpoints) (`csoundSetBreakpoint`) |
| 26 | **Captura MIDI → partitura**: grabar lo que tocas como líneas `i` | |
| 27 | **Afinaciones y microtonalidad**: importar escalas Scala (`cpstun`) | |

## Fase 3D — visualización y control en 3D (GPU)

**Base técnica (verificada en Avalonia 12.1.5):**
- `OpenGlControlBase`: OpenGL ES 3 sobre ANGLE/Direct3D 11 en Windows. Es la ruta más estable y la recomendada para empezar. Los bindings serían Silk.NET.OpenGL y los shaders se escriben en GLSL.
- `GpuInterop` + `CompositionDrawingSurface`: un renderer **Vulkan** (Silk.NET.Vulkan) comparte su imagen con la interfaz. Queda como etapa 2 si OpenGL no alcanza.
- Un hilo de render independiente con datos de audio por un ring buffer sin locks, que ya existe en `CsoundEngine`. Si el audio sufre, el 3D baja su tasa de cuadros, nunca al revés.
- **Ventanas desacoplables:** cada visualizador puede ir en el panel o en su propia ventana (segundo monitor o proyector, pantalla completa), y la disposición se guarda en `settings.json`.
- Robustez: si la GPU o el contexto fallan, el visualizador cae a la versión 2D actual en vez de cerrar la app. Además: límite de FPS, pausa cuando la ventana está oculta y liberación de recursos al cerrar.

| # | Visualizador / control 3D | Qué muestra o controla |
|---|---|---|
| 3D-1 | **Espectrograma en cascada 3D** | FFT de la salida como un terreno que avanza en el tiempo; cámara orbital; escala log/lineal |
| 3D-2 | **Osciloscopio estéreo 3D** (Lissajous + tiempo) | fase L/R como una espiral en el espacio; sirve para ver la amplitud estéreo y los problemas de mono |
| 3D-3 | **Pad XYZ** | una esfera que se arrastra en un cubo y mueve 3 canales a la vez (por ejemplo cutoff, resonancia, reverb); con inercia y opción de grabar el movimiento |
| 3D-4 | **Panorámica espacial 3D** | fuentes como esferas en una sala, alrededor de quien escucha; mueven el azimut, la elevación y la distancia de `hrtfmove2`/`vbap`. Es audio binaural real para audífonos |
| 3D-5 | **Wavetable 3D** | las tablas (`ftgen`) apiladas como superficie; un cursor muestra la posición del morph |
| 3D-6 | **Visualizador de escena para tocar en vivo** | partículas y geometría que responden a canales y bandas de frecuencia, a pantalla completa en un segundo monitor |
| 3D-7 | **Grafo del instrumento en 3D** | nodos (opcodes) y cables (variables) con el nivel de señal en vivo en cada cable |

Orden dentro de esta fase: base OpenGL + ventanas desacoplables → 3D-1 → 3D-3 → 3D-4 → 3D-2 → 3D-5 → 3D-6 → 3D-7.

## Orden recomendado

1, 2 y 3 primero (latencia y valores en vivo) → 4 y 5 (tocar con MIDI) → 6 y 7 (live coding y autocompletar) → 8 y 9 → fase 2 empezando por 10, 11 y 14 → fase 3 empezando por 21 y 22.

Fuentes:
- [Novedades de Csound 7](https://csound.com/manual/intro/whats-new-in-csound-7)
- [Optimizar la latencia](https://csound.com/docs/manual/UsingOptimizing.html)
- [Audio en tiempo real](https://csound.com/manual/overview/rt-audio/)
- [vst3 opcodes](https://github.com/gogins-dev/csound-vst3-opcodes)
- [VST 3.8 bajo licencia MIT](https://sonicstate.com/news/2025/10/30/vst-3-now-available-under-mit-license)
- [Opcodes de Ableton Link](https://csound.com/docs/manual/OSCNetwork.html)
