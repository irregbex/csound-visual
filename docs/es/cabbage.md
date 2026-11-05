# Instrumentos Cabbage

[Cabbage](https://cabbageaudio.com) es una herramienta muy usada para convertir orquestas de Csound en plugins. Un instrumento Cabbage es un `.csd` normal con una sección extra `<Cabbage> … </Cabbage>` que declara sus perillas, sliders, botones, pads XY y teclado. Csound Visual lee esa sección, **dibuja los controles** y te deja tocar el instrumento sin Cabbage. El mismo archivo sigue abriéndose en Cabbage, donde se puede exportar como plugin VST3/AU.

```
<Cabbage>
form caption("Synth") size(500, 300), colour(40, 40, 40)
rslider bounds(10, 10, 80, 80), channel("cutoff"), range(20, 20000, 2000, 0.3, 1), text("Cutoff")
keyboard bounds(10, 150, 480, 140)
</Cabbage>
<CsoundSynthesizer>
…  kcut chnget "cutoff"  …
</CsoundSynthesizer>
```

## Cómo se usa

1. Abre un `.csd` de Cabbage (o **Ejemplos › ★ Instrumentos Cabbage**). Arriba del panel lateral aparece la tarjeta **INTERFAZ CABBAGE** con los controles del instrumento, escalados al ancho del panel.
2. **⧉ Ventana** abre la misma interfaz en una ventana propia que se puede agrandar (sigue a la pestaña que estás mirando).
3. Presiona **Tocar**. Cada control manda su valor a Csound en tiempo real.

| Control | Gesto |
|---|---|
| Perilla (`rslider`, `encoder`) | arrastrar arriba/abajo; **Ctrl** = fino; rueda del mouse; **doble clic** = valor inicial. Al pasar el mouse, el valor reemplaza la etiqueta. |
| Slider (`hslider`, `vslider`), slider de rango (`hrange`, `vrange`) | clic o arrastrar; Ctrl = fino; rueda; doble clic = valor inicial |
| Caja numérica (`nslider`) | arrastrar arriba/abajo, rueda, doble clic = valor inicial |
| Botón | el clic alterna 0/1; con `latched(0)` vale 1 solo mientras lo mantienes. Botones/casillas con el mismo `radioGroup` se excluyen |
| Casilla (`checkbox`) | el clic alterna 0/1 |
| Selector (`combobox`) | manda el índice **desde 1** del elemento elegido, como Cabbage |
| Botón de opciones (`optionbutton`) | cada clic pasa al siguiente elemento (desde 0) |
| Pad XY | arrastra la bola: X → primer canal, Y → segundo canal; doble clic vuelve los dos al inicio |
| Teclado | toca notas MIDI en Csound, como el teclado en pantalla (las notas de tu teclado MIDI se iluminan en verde) |

Los controles mueven **los mismos canales que los sliders normales** de la tarjeta «Controles en vivo», que siguen abajo. Por eso:

* mover un slider, un control MIDI (el **MIDI learn** se hace en el slider del mismo nombre) o arrastrar un valor en el editor también mueve el control;
* **Enviar al DAW** sigue funcionando: el plugin en Cubase recibe los movimientos;
* al arrancar Csound se **manda el valor inicial de cada control** antes de la primera nota (los instrumentos Cabbage leen sus controles con `chnget`, que si no leería 0).

## Cómo corre un archivo Cabbage en Csound Visual

* Csound ignora la sección `<Cabbage>` (verificado con Csound 7.0), así que no se quita nada.
* Los archivos Cabbage suelen traer `-n -d -+rtmidi=NULL -M0`. Csound Visual decide la salida y la entrada MIDI, así que descarta `-n`, `-d` y `-+rtmidi=…` (con `-+rtmidi=NULL` los teclados MIDI quedarían mudos).
* Los instrumentos hechos para Cabbage 2.5 o posterior suelen usar **opcodes que solo existen en Cabbage**. Si el archivo los usa y no los define, Csound Visual agrega reemplazos (opcodes de usuario sobre los canales) y lo avisa en la consola:

| Opcode | Reemplazo |
|---|---|
| `cabbageGetValue`, `cabbageGet "canal"` | lee el canal (formas `k`, `i`, `S` y `kValor, kCambio`) |
| `cabbageSetValue` | escribe el canal (con o sin disparador) |
| `cabbageChanged` | disparo cuando cambia el canal |
| `cabbageSet`, `cabbageGet "canal", "identificador"` | se aceptan y no hacen nada (la apariencia de los controles no se cambia desde la orquesta) |

  Una directiva `#line` mantiene los números de línea de los errores de Csound apuntando a la línea correcta del editor.
* No se proveen: `cabbageCreate`, los de archivos/presets/estado (`cabbageGetFilename`, `cabbageWriteStateData`…), los de archivos MIDI y los web. Csound los reporta como opcodes desconocidos.

Probado con los 280 ejemplos del repositorio de Cabbage 2: todos se leen sin problemas; **242 de 280** pasan la revisión de sintaxis de Csound 7 con los reemplazos (179 sin ellos). La mayoría de los demás usa los opcodes no soportados o sintaxis antigua que Csound 7 ya no acepta.

## Controles e identificadores

**Se dibujan**: `form`, `rslider`, `hslider`, `vslider`, `nslider`, `encoder`, `hrange`, `vrange`, `button`, `checkbox`, `combobox`, `optionbutton`, `xypad`, `keyboard`, `label`, `groupbox`, `image` (rectángulo/elipse de color, y la imagen si `file()` nombra un PNG/JPG junto al `.csd`), `line`, `infobutton` (inactivo). Los hijos dentro de `{ … }` (plants) se ubican relativos a su padre.

**Se muestran como marcos punteados**: `gentable`, `soundfiler`, `signaldisplay`, `filebutton`, `texteditor`, `listbox`, `csoundoutput` (mira la consola), los medidores (`hmeter`, `vmeter`: los valores que escribe la orquesta no se leen de vuelta) y cualquier tipo desconocido. La tarjeta los lista bajo el panel; su ayuda emergente muestra los avisos del lector con número de línea.

**Identificadores que se entienden**: `bounds`, `pos`, `size`, `channel` (uno o dos), `range(min, max, valor, skew, incremento)` (también valores `a:b` para sliders de rango), `rangeX`, `rangeY`, `value`, `text` (etiqueta, o textos apagado/encendido, o elementos del selector), `items`, `caption`, `colour`, `colour:0`, `colour:1`, `fontColour`, `textColour`, `trackerColour`, `outlineColour`, `markerColour`, `ballColour` y otros `…colour` (números RGB/RGBA, un nivel de gris, `#RRGGBB`, `#AARRGGBB` o nombres CSS como `"cornflowerblue"`), `visible`, `active`, `alpha`, `latched`, `radioGroup`, `file`, `shape`, `outlineThickness`, `corners`, `align`, `fontSize`, `channelType("string")`, `min`, `max`, `increment`. Los nombres no distinguen mayúsculas, las comas entre identificadores son opcionales, y funcionan los comentarios `;`, los bloques `/* */` y las macros `#define NOMBRE …` / `$NOMBRE`. Una línea que empieza con un identificador continúa el control anterior.

**Se ignoran** (sin aviso): datos del plugin (`pluginId`, `guiMode`…), `identChannel` (cambiar controles desde la orquesta), popups, cajas de valor, filmstrips, opciones de tablas y otros detalles cosméticos.

La sintaxis nueva **JSON de Cabbage 3** (`<Cabbage>[{"type": "rotarySlider", …}]</Cabbage>`) se lee en lo posible (tipos, posición, canales con rango, elementos, colores en hexadecimal).

## Perillas en documentos normales: `;@knob`

No necesitas una sección Cabbage para tener perillas: agrega un comentario a la línea de un canal y la tarjeta (titulada **PERILLAS (;@)**) dibuja el control, ordenado automáticamente.

```
chnk "cutoff", 1, 3, 1000, 20, 20000   ;@knob "Cutoff"
chnk "res", 1, 2, 0.3, 0, 0.95         ;@knob
chnk "amp", 1, 2, 0.5, 0, 1            ;@slider "Nivel"
chnk "wave", 1, 1, 1, 1, 3             ;@combo Saw|Square|Triangle
chnk "x", 1, 2, 0.5, 0, 1              ;@xy "y"
chnk "y", 1, 2, 0.5, 0, 1
chnk "go", 1, 1, 0, 0, 1               ;@button "¡Dale!"
```

Tipos: `knob`, `slider`, `vslider`, `button` (alterna), `momentary`, `checkbox`, `combo` (elementos separados por `|`), `xy "otro canal"`. El rango sale de la declaración `chnk` (un `chnk` exponencial, itype 3, da una perilla con skew). El primer texto entre comillas después del tipo es la etiqueta.

## Ejemplos

**Ejemplos › ★ Instrumentos Cabbage**:

* **Sinte de perillas** — tres osciladores desafinados y un filtro ladder: perillas, sliders, un selector, una casilla y un teclado. Al empezar toca una frase corta; después usa el teclado.
* **Delay espacial XY** — un efecto con pad XY (tiempo × realimentación), botones freeze y ping; toca un loop interno o la entrada en vivo. Usa `cabbageGetValue`.
* **Caja de ritmos de juguete** — una caja de ritmos de 8 pasos hecha con 24 casillas, con perillas de tempo, swing, afinación, decaimiento, reducción de bits y sala. Usa `cabbageGetValue` en tiempo de inicialización.

Los tres se renderizan sin errores con el motor de la app (sin conexión, 8 s, picos 0,43 / 0,34 / 0,84).

## Código

| Archivo | Qué hace |
|---|---|
| `Cabbage/CabbageModel.cs` | Modelos de control, rango (skew), color y formulario |
| `Cabbage/CabbageParser.cs` | Lector tolerante (líneas de Cabbage 2, anidamiento, macros; JSON de Cabbage 3) |
| `Cabbage/CabbageSupport.cs` | Control → canales (rangos/valores iniciales), reemplazos de opcodes con `#line`, anotaciones `;@knob` |
| `Cabbage/CabbageControls.cs` | Controles dibujados: perilla, slider/rango, caja numérica, botón/casilla/opciones, pad XY, marco punteado |
| `Cabbage/CabbagePanel.cs` | Arma el lienzo (posiciones absolutas, escala con `Viewbox`) y enlaza los controles con los canales |
| `Cabbage/CabbageCard.cs` | Tarjeta del panel lateral y el seguidor del documento visible |
| `Views/CabbageWindow.cs` | Ventana aparte |
| `ViewModels/DocumentViewModel.Cabbage.cs` | `CabbageForm`, `IsCabbageDocument`, `HasWidgetPanel` |

Enganches en código compartido: `DocumentViewModel.RefreshChannels` mezcla los canales de los controles (`CabbageSupport.Merge`), `MainViewModel.Launch` y *Enviar al DAW* llaman a `CabbageSupport.PrepareForCsound`, `CsdTools.ExtractOptions` descarta `-+rtmidi`, y `MainWindow.axaml` contiene `<cab:CabbageCard/>`.
