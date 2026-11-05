# Csound dentro de Cubase (VST3)

Csound Visual trae dos plugins VST3 que llevan **Csound 7 adentro**:

| Plugin | Dónde va en Cubase | Qué hace |
|---|---|---|
| **Csound Visual Instrument** | Pista de instrumento | Sintetizador que se toca con MIDI. Por defecto: «Velvet Poly», un pad polifónico con chorus y reverb. |
| **Csound Visual FX** | Inserto de una pista de audio | Efecto sobre el audio de la pista. Por defecto: «Wide Echo Space», delay ping-pong modulado + chorus + reverb. |

La gracia: **cualquier orquesta que escribas en Csound Visual se puede mandar al plugin** con el botón **Enviar al DAW**, y suena dentro de tu proyecto de Cubase, con tus pistas, tu mezcla y tus efectos.

## Instalación

Ya están instalados en `%LOCALAPPDATA%\Programs\Common\VST3\CsoundVisual.vst3` (la carpeta de plugins VST3 del usuario, que Cubase revisa sola). El plugin es autónomo: lleva su propia copia de Csound, no depende de dónde esté instalado.

Para recompilarlo: `powershell -ExecutionPolicy Bypass -File tools\build_vst.ps1` (con Cubase cerrado, porque Windows bloquea el plugin cargado).

## Paso a paso en Cubase 15

1. Abre Cubase. Si no aparecen los plugins: **Studio › VST Plug-in Manager**, revisa que la ruta `C:\Users\<tú>\AppData\Local\Programs\Common\VST3` esté en las rutas VST 3 y aprieta **Rescan**.
2. Crea una **pista de instrumento** y elige **Csound Visual Instrument** (fabricante «Csound Visual»). Toca tu teclado: debe sonar el pad.
3. En Csound Visual abre una orquesta (la demo, un ejemplo o una pieza de la galería) y aprieta **Enviar al DAW** › elige la pista. Los mensajes de Csound del plugin aparecen en la consola de la app.
4. La tarjeta de salida de la app se pone **verde** y pasa a llamarse **SALIDA VST3**: el osciloscopio, el espectro y los medidores muestran lo que suena **dentro de Cubase**, aunque la app no esté tocando.
5. Desde ese momento **los sliders de la app controlan el plugin** (la barra de estado muestra «⇄ pista · orquesta»). En Cubase, los mismos controles aparecen como parámetros del plugin con el nombre de cada canal (`cutoff`, `reverb`…): se pueden **automatizar** y grabar.
6. Para un efecto: en una pista de audio, inserta **Csound Visual FX** y envía una orquesta que lea la entrada con `inch 1, 2` (o `ain1, ain2 ins`).

## Lo que el plugin le da a la orquesta

* **Audio**: la frecuencia de muestreo de Cubase reemplaza al `sr` del archivo; estéreo de entrada (FX) y de salida.
* **MIDI**: notas, pitch bend, modulación (CC1), CC2, CC11, sustain (CC64) y aftertouch. `massign` y los instrumentos MIDI funcionan igual que en la app.
* **Tempo y transporte** en canales que la orquesta puede leer con `chnget`: `HOST_BPM`, `HOST_PLAYING` (0/1), `HOST_PPQ` (posición en negras) y `HOST_SR`.
* **16 parámetros automatizables** que toman el nombre y el rango de los primeros 16 canales `chnk` de la orquesta.
* **El proyecto recuerda la orquesta**: al guardar el proyecto de Cubase se guardan el código y los valores.

## Limitaciones

* No tiene ventana propia: Cubase muestra su editor genérico de parámetros. El código se edita en Csound Visual y se envía.
* Latencia fija de *ksmps* muestras (Cubase la compensa sola): conviene un ksmps chico (32 o 64).
* La partitura empieza al compilar, no con el transporte de Cubase: para sincronizar usa `HOST_PLAYING` y `HOST_PPQ`.
* Las rutas a archivos de sonido dentro de una orquesta deben ser absolutas.

Detalles técnicos (arquitectura, hilos, protocolo): `src\CsoundVST\README.md`.
