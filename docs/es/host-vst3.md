# Plugins VST3 dentro de Csound Visual

Csound Visual puede **alojar plugins VST3**: la salida de Csound pasa por una cadena de tus propios efectos (FabFilter, iZotope, Native Instruments, Plugin Alliance, Steinberg…) antes de llegar a los medidores, a la grabación y a la tarjeta de sonido, como los insertos de un canal de mezcla. También se pueden poner **instrumentos** VST3 en la cadena: suman su sonido al de Csound y tocan el mismo MIDI que Csound (puertos MIDI y teclado en pantalla). Cada plugin abre su **propia ventana**.

Es el camino inverso de [Csound en Cubase](vst3-cubase.md): allá Cubase aloja a Csound; acá Csound Visual aloja a los plugins.

## Cómo se usa

1. **Ctrl+Shift+V** (o la entrada «Plugins», cuando esté en la barra o el menú) abre la ventana **Plugins VST3**.
2. La primera vez, presiona **Examinar**. Cada módulo VST3 instalado se inspecciona en un **proceso aparte**, así que un plugin que se cae o se cuelga no tumba la app (queda en la lista de errores). El resultado se guarda en caché; las siguientes veces solo se examinan los plugins nuevos o actualizados. Clic derecho en **Examinar** › *Examinar todo de nuevo* para empezar de cero. En el PC de desarrollo el primer examen completo tardó unos 3 minutos para 190 módulos.
3. Busca por nombre, fabricante o tipo (*Fx*, *EQ*, *Reverb*, *Instrument*…), elige **Todos / Efectos / Instrumentos**, marca un plugin y presiona **Agregar a la cadena →** (o doble clic).
4. En la cadena: **Bypass** salta un plugin, **▲/▼** cambian el orden (arriba = primero), **✕** lo quita, **Editar** abre la ventana del propio plugin. Clic en una fila para ver sus **parámetros** como sliders abajo (sirve cuando un plugin no tiene ventana; doble clic en un slider = valor por defecto).
5. Presiona **Tocar** como siempre. La consola muestra la cadena al empezar: `🎛 Plugins VST3: Raum → Saturn 2 · 48000 Hz`.

La cadena — orden, bypass y **el estado completo de cada plugin** (todo lo que ajustes en su ventana) — se guarda en la configuración y vuelve en el siguiente arranque. Las ventanas de los plugins siguen abiertas aunque cierres la ventana de la cadena; pertenecen a la ventana principal.

## Dónde actúan los plugins

```
Csound (csoundPerformKsmps) ─► escala 0dbfs ─► [cadena VST3, canales 1-2] ─► medidores · osciloscopio · espectro · grabación WAV ─► tarjeta
```

* La cadena corre **una vez por bloque de control** (`ksmps` muestras) en el hilo de audio, justo después de que Csound calcula el bloque. Medidores, osciloscopio, espectro, vistas 3D y **Grabar** ven el sonido procesado.
* **Solo con el motor propio de la app «Windows · WASAPI»** (compartido o exclusivo). Con los módulos de audio de Csound (PortAudio, ASIO, WASAPI de Csound) el audio no pasa por la app, y **Renderizar a WAV** es una pasada offline de Csound: en esos casos la cadena se salta y la consola lo avisa.
* Los canales 1 y 2 pasan por la cadena. Una orquesta mono se procesa como mono doble y se vuelve a juntar; los canales 3 en adelante quedan como están.
* Los **instrumentos** suman su salida en su lugar de la cadena (un efecto puesto después también los procesa) y reciben note on/off, CC, pitch bend y presión de canal del MIDI de la app. Los CC y el pitch bend llegan al plugin por su mapa MIDI (IMidiMapping) cuando lo tiene.
* La **latencia** de un plugin (limitadores con anticipación, EQ de fase lineal) se muestra en la cadena pero no se compensa: no hay nada con qué alinearla.
* A los plugins que leen el transporte se les informa 120 BPM, 4/4, tocando.
* Con `ksmps` muy chicos (1–8) cada plugin procesa bloques diminutos: funciona, pero gasta más CPU. Entre 32 y 128 va bien.

## Plugins probados en el PC de desarrollo (2026-11-04)

Todos cargaron, procesaron un seno de 440 Hz, guardaron y restauraron su estado y abrieron su ventana (verificada visible y con tamaño, capturada con PrintWindow, cerrada con WM_CLOSE):

| Plugin | Tipo | Notas |
|---|---|---|
| FabFilter Pro-Q 3 | EQ | banda +24 dB puesta desde el host → la ventana la muestra, salida −15,1 → −5,0 dBFS RMS |
| FabFilter Saturn 2 | Distorsión | |
| Native Instruments Supercharger, Raum | Dinámica, reverb | plugins de un solo componente |
| iZotope Neoverb | Reverb | latencia 272 muestras |
| PSP VintageWarmer2 | Dinámica | latencia 639 muestras |
| Plugin Alliance bx_clipper | Clipper | |
| Steinberg Retrologue | **Instrumento** | nota desde el host → pico −11 dBFS |
| Sonic Charge Microtonic | **Instrumento** | |
| Xfer Serum 2 FX | Efecto | Serum 2 (instrumento) está en el mismo módulo |

Con el motor WASAPI real y la demo (volumen al 5 %), Raum → Saturn 2 subió el RMS grabado de −50,4 a −47,0 dBFS (la cola de la reverb), DSP 6 %, 0 cortes. En la app real: la cadena guardada se restaura al arrancar, «Editar» (por UI Automation) abre la ventana de Raum como ventana propia de la principal, y al cerrar la app se guarda el estado.

**Problema conocido: Sylenth1** (LennarDigital 3.0.7) se cae dentro del proceso de la app con `0xC0000409`, código fail-fast 57 (lo más probable: la **pila sombra CET** que las apps .NET 9/10 activan por defecto rechaza los trucos de su protección anticopia), mientras que el validador de Steinberg y el host de prueba nativo lo cargan bien. El examinador lo aísla («el plugin se cayó al examinarlo»), así que nunca llega a la lista. Compilar la app con `<CETCompat>false</CETCompat>` en `CsoundVisual.csproj` muy probablemente lo arreglaría (los DAW no activan CET), a costa de esa protección para toda la app: no se hace por defecto; el administrador de plugins (más abajo) desactiva Sylenth1 automáticamente.

`CsoundVisual.vst3` (nuestro propio plugin) **no se ofrece** a propósito: la app ya es Csound, y cargar el plugin metería un segundo Csound en el mismo proceso.

## Compilar el puente nativo

El código de host es una DLL de C++ pequeña, `CsoundVisualVstHost.dll` (`src/VstHost`, VST3 SDK 3.8 con licencia MIT, CRT estático):

```
powershell -ExecutionPolicy Bypass -File tools\build_vsthost.ps1
```

Configura CMake + Ninja con las herramientas x64 de Visual Studio, compila la DLL y `vsthost_native_test.exe` y copia la DLL a `src\CsoundVisual\native\`, desde donde la compilación de la app la copia junto a `CsoundVisual.exe`. Usa el SDK descargado para el plugin (`src\CsoundVST\external\vst3sdk`; si falta, correr una vez `tools\build_vst.ps1`). **Sin la DLL la app funciona normal**; la ventana de plugins explica que el host está desactivado.

## Pruebas (sin interfaz; no suena nada salvo donde se indica)

```
CsoundVisual.exe --vst3-list [full]
CsoundVisual.exe --vsthost-test "<módulo.vst3>" [nombre de clase] [segundos] [--set "Título del parámetro=0.9"] [--editor captura.bmp]
CsoundVisual.exe --vsthost-engine-test "<a.vst3>|<b.vst3>" [segundos ≤ 3]
src\VstHost\build\bin\vsthost_native_test.exe "<módulo.vst3>" [id de clase]
```

* `--vst3-list` examina (con caché) e imprime todos los plugins.
* `--vsthost-test` carga un plugin, procesa un seno en memoria e imprime pico/RMS antes y después, sus parámetros, una ida y vuelta del estado a una segunda instancia y, con `--editor`, abre su ventana, la captura y la cierra con WM_CLOSE.
* `--vsthost-engine-test` toca la demo **bajito** (volumen 5 %) dos veces por el motor WASAPI real — sin y con la cadena —, graba ambas pasadas y compara los niveles.
* `vsthost_native_test` hace la misma carga y proceso sin .NET (para separar un problema del plugin de uno del host).

## Cómo funciona (para desarrolladores)

Ver la sección equivalente en inglés: [VST3 plug-ins inside Csound Visual](../en/vst3-host.md). Resumen: `src/VstHost/source/vsthost.cpp` (API C `vh_*`, ventana del editor, SEH alrededor de `process()`), `Integration/VstHostNative.cs`, `Integration/VstChain.cs` (arreglo inmutable para el hilo de audio, liberación segura), `Integration/VstScanner.cs` (procesos trabajadores y caché en `%LOCALAPPDATA%\CsoundVisual\vst3-cache.json`), `Engine/CsoundEngine.Inserts.cs` (cadena aplicada en `Step` antes de medidores y grabación), `Views/PluginChainWindow.cs`, `ViewModels/MainViewModel.Plugins.cs` y `PluginChain` en `settings.json`.

## Administrador de plugins (lista negra)

Como Cubase, Csound Visual guarda una lista de plugins que se ignoran: **Configuración › Plugins VST3** muestra todos los módulos encontrados al examinar, con una casilla *activo*.

* Un módulo que **falla al examinarlo** (por ejemplo Sylenth1, que se cae dentro de apps .NET 10) se desactiva solo y muestra el motivo.
* Un módulo que **cierra la app** al cargarse o al abrir su ventana queda desactivado en el siguiente inicio (un archivo marcador `%LOCALAPPDATA%\CsoundVisual\vst-loading.txt` sobrevive a la caída) y la consola lo avisa.
* Los desactivados no aparecen en la ventana de la cadena y se saltan al restaurar la cadena guardada. Marca la casilla para volver a probarlo (un módulo que falló se examina de nuevo en el próximo *Examinar*); «Activar todos» limpia la lista.
