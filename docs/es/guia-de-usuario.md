# Guía de usuario

Todos los botones, tarjetas y controles muestran una **ayuda emergente** al dejar el mouse encima.

## La ventana

| Zona | Qué hay |
|---|---|
| Barra superior | Nuevo, Abrir, Guardar · **Tocar** (F5), Detener (Shift+F5), **Grabar** (Ctrl+Shift+R), 3D, **Herramientas** (piano roll, secuenciador), Renderizar (Ctrl+R) · estado, tiempo y reloj de grabación · panel lateral (Ctrl+B). |
| Editor | Pestañas (• = sin guardar, punto verde = sonando), código con resaltado, tarjeta de ayuda al pasar el mouse. |
| Consola | Mensajes de Csound. Clic en un error con número de línea para ir a esa línea. |
| Teclado | Piano en pantalla (se puede ocultar desde la tarjeta MIDI). |
| Panel lateral | Salida (osciloscopio, espectro, medidores) · Controles en vivo · Observar · Evento en vivo · MIDI · Audio · Tablas · Ejemplos. |
| Barra de estado | Versión de Csound · ayuda rápida · **DSP %** y **cortes** · línea y columna. |

## Atajos

| Tecla | Acción |
|---|---|
| F5 / Shift+F5 | Tocar / detener |
| Ctrl+Enter | Live coding: evaluar la selección o el `instr` bajo el cursor (si no suena, empieza a tocar) |
| Ctrl+Shift+Enter | Enviar la línea de partitura (o la selección) como evento en vivo |
| Ctrl+Espacio | Autocompletar |
| F1 | Manual del opcode bajo el cursor |
| Ctrl+Shift+R | Grabar / detener grabación |
| Ctrl+3 | Ventana 3D ([Vistas 3D](visual-3d.md)) |
| Ctrl+P | Piano roll ([Herramientas musicales](herramientas-musicales.md)) |
| Ctrl+Shift+P | Secuenciador por pasos |
| Espacio (en el piano roll o el secuenciador) | Tocar / detener esa herramienta |
| Ctrl+R | Renderizar a WAV |
| Ctrl+N / Ctrl+O / Ctrl+S / Ctrl+Shift+S | Nueva pestaña / abrir / guardar / guardar como |
| Ctrl+B | Mostrar u ocultar el panel lateral |
| Esc (arrastrando un valor) | Volver al valor que tenía antes de arrastrar |

## Tocar y controlar

* **Controles en vivo**: cada `chnk "nombre", …` o `chnget "nombre"` del código crea un slider. Con
  `chnk … , 3, defecto, mín, máx` el rango es exponencial (ideal para frecuencias).
* **MIDI learn**: clic en el botón **MIDI** de un slider y mueve una perilla o fader de tu
  controlador. Clic derecho en el botón › *Quitar asignación MIDI*.
* **Teclado en pantalla**: clic para tocar (más abajo en la tecla = más fuerte), arrastra para
  glissando. Con el foco en el teclado: `A W S E D F T G Y H U J K O L P` tocan, `Z`/`X` cambian la
  octava y `C`/`V` la fuerza. Las notas que llegan de tu controlador se ven en verde.
* **Entradas MIDI** (tarjeta MIDI): activa los puertos que quieras (por ejemplo *Impact LX61+*).
  El monitor muestra lo que llega. Para que un instrumento responda a MIDI usa `massign` y, si
  quieres la nota y la fuerza en p4/p5, `--midi-key=4 --midi-velocity-amp=5` en `<CsOptions>`
  (el demo ya lo hace).
* **Evento en vivo**: escribe `i "Voice" 0 1 64 0.9 0.5` y Enter.

## Herramientas musicales

El botón **Herramientas** de la barra superior abre el **piano roll** (Ctrl+P: dibujar notas en una cuadrícula y tocarlas con un instrumento del documento) y el **secuenciador por pasos** (Ctrl+Shift+P: ritmos con swing, mute/solo y relleno euclidiano), además de **Abrir kit de batería** (8 tambores sintetizados, instrumentos 1 a 8). Si Csound no está en Tocar, el ▶ de la herramienta lo inicia con el documento elegido. Lo que hagas se puede **insertar en la partitura** (bloque marcado que se reemplaza al volver a exportar y se vuelve a cargar al abrir la herramienta). Detalles en [Herramientas musicales](herramientas-musicales.md).

## Ayuda al pasar el mouse y arrastrar valores

Ver [Ayuda al pasar el mouse y valores en vivo](inteligencia-del-editor.md).

## Audio

Elige motor, salida, latencia y ksmps en la tarjeta **Audio**. Detalle en
[Motor de audio y latencia](motor-de-audio.md). Resumen:

* **WASAPI compartido**: lo normal; suena junto a todo lo demás de Windows.
* **WASAPI exclusivo**: la latencia mínima sin ASIO (≈3 ms con la UR22mkII configurada a 48 kHz).
* **Csound · PortAudio**: para drivers ASIO.
* **Diagnóstico de audio**: qué puede hacer cada dispositivo, sin reproducir nada.
* **Ejemplos** (botón de la barra o Ctrl+E): explorador con los ~1500 ejemplos del manual oficial de Csound más los de la instalación, por categoría del manual y con búsqueda; vista previa con colores, Anterior/Siguiente, **Abrir en pestaña**, **Tocar** y «Tocar al cambiar» para recorrerlos rápido. La pestaña de ejemplo se reemplaza al abrir otro (si no la cambiaste), y los originales nunca se sobrescriben: «Guardar» pide otro nombre.
* **Guardar** (Ctrl+S) sobrescribe el archivo; **Guardar como** (Ctrl+Shift+S) pide nombre y carpeta.
* Si abres otro archivo o ejemplo **mientras suena**, el anterior se detiene y empieza el nuevo.
* **Resaltado de palabra**: con el cursor sobre una palabra (por ejemplo `ishift`) se marcan todas sus apariciones; la barra de estado dice cuántas hay aquí y en otras pestañas; F3 / Shift+F3 saltan a la siguiente / anterior.
* **Observar en grande**: clic en una variable observada la muestra al centro de la ventana, con el resto oscurecido; clic afuera o Esc para volver.
* **Configuración** (botón de engranaje en la barra de arriba, o Ctrl+,): **Audio** (motor, salida, latencia, ksmps, panel de sonido y diagnóstico; antes estaba en el panel lateral), **idioma** de la interfaz (Español / English, cambia al instante y también elige la demo con comentarios en ese idioma), tema de colores, tamaño de letra del editor (también Ctrl+rueda en el código), ajuste de líneas largas, accesos al panel de sonido y al diagnóstico de audio, carpeta de grabaciones y versiones.
* **Tocar enciende Csound**: mientras no esté en modo Tocar no suena nada, ni siquiera el teclado MIDI. Si tocas notas con Csound detenido, aparece una burbuja bajo «Tocar» («Para que Csound suene debe estar en modo Tocar») y el botón parpadea.
* **Apariencia** (tarjeta del panel lateral): tema de colores de toda la app (ventanas, editor, medidores, 3D): **Azul deportivo** (predeterminado), Grafito y naranja, Rojo carrera, Verde esmeralda y Ámbar clásico (el aspecto original). Se aplica al instante y se recuerda.
* **Panel de sonido de Windows**: abre el panel clásico de Sonido; en *Reproducción › dispositivo › Propiedades › Opciones avanzadas* se cambia la frecuencia (48000 Hz para ≈3 ms en exclusivo).

La ventana se arrastra desde la barra superior; doble clic la maximiza o la restaura. Recuerda su tamaño y posición, y si la pantalla ya no está conectada vuelve a una pantalla visible.

## Guardar lo que haces

* **Grabar**: graba lo que suena, incluidos los cambios en vivo, en
  `Documentos\Csound Visual\Grabaciones\<archivo>_<fecha>.wav` (float 32 bits). Al terminar, la
  línea «✓ Grabación guardada» de la consola abre el Explorador con el archivo; clic derecho en
  **Grabar** › *Abrir carpeta de grabaciones*.
* **Renderizar**: calcula el archivo completo a WAV, más rápido que en tiempo real.

## Archivos y pestañas

* Abrir un archivo lo agrega como pestaña; si ya está abierto, cambia a esa pestaña.
* Al cerrar una pestaña o la app con cambios, pregunta *Guardar / No guardar / Cancelar*.
* Los archivos abiertos se reabren la próxima vez.
* Preferencias en `%APPDATA%\CsoundVisual\settings.json`.
