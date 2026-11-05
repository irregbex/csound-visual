# Ableton Link

**Ableton Link** mantiene a tiempo varios programas de música por la red local: comparten un **tempo**, una **grilla de pulsos** y la **fase dentro del compás**, y (si se quiere) **tocar/detener**. Lo hablan Ableton Live, Bitwig Studio, Reason, Traktor, muchas apps de iOS/Android y ahora Csound Visual. Cualquiera puede cambiar el tempo y los demás lo siguen en pocos milisegundos; no hay "maestro".

> **Cubase no tiene Link.** Con Cubase 15 usa el plugin VST3 de Csound Visual: dentro de Cubase la orquesta recibe el tempo y la posición del host en `HOST_BPM`, `HOST_PLAYING` y `HOST_PPQ` (ver [Csound en Cubase](vst3-cubase.md)).

## Cómo se activa

* **Configuración › Sincronización** (Ctrl+,): **Activar Ableton Link**, **Pulsos por compás** (el *quantum*, 4 por defecto) y **Compartir tocar/detener (Start Stop Sync)**. Las tres opciones se guardan; si Link estaba activo al cerrar la app, vuelve a activarse al abrirla.
* El pequeño **indicador de Link** (punto + «Link · 1 par · 120.0 BPM») lo activa y desactiva con un clic. Colores del punto: gris = apagado, azul = activo y solo, verde = activo con pares.

## Configuración en Ableton Live (última versión)

1. **Preferencias › Link, Tempo & MIDI** (Live 12; «Link/MIDI» en versiones anteriores): pon **Show Link Toggle** en *Show*. Activa **Start Stop Sync** si quieres que Play/Stop de Live arranque y detenga el piano roll y el secuenciador de Csound Visual (y al revés).
2. Haz clic en el botón **Link** arriba a la izquierda de la barra de control de Live. Muestra «1 Link» cuando Csound Visual está en la sesión.
3. En Csound Visual activa Link. El indicador muestra «Link · 1 par · <tempo de Live> BPM».
4. Cambia el tempo en Live: el indicador, toda orquesta que lea `LINK_BPM` y las herramientas abiertas lo siguen. Cámbialo en el piano roll o el secuenciador: Live lo sigue.
5. Usa el mismo **quantum** en ambos programas (la cuantización de lanzamiento de Live: «1 Bar» en 4/4 = 4 pulsos).

Dos programas en el mismo PC funcionan al instante. Con otro computador o un celular, deben estar en la misma red (Wi-Fi o cable, misma subred, multicast permitido).

## Qué recibe la orquesta

Mientras Csound toca y Link está activo, la app escribe estos canales de control **antes de cada ciclo de control** (en el propio hilo de Csound, sin reservar memoria):

| Canal | Valor |
|---|---|
| `LINK_BPM` | tempo de la sesión; **0 mientras Link está apagado** (sirve para volver al tempo propio) |
| `LINK_BEAT` | posición en pulsos en la línea de tiempo compartida |
| `LINK_PHASE` | posición dentro del compás: 0 … quantum (0 = el "1" que cuenta Live) |
| `LINK_PLAYING` | 1 mientras el transporte compartido toca (con Start Stop Sync), si no 0 |
| `LINK_PEERS` | cuántas otras apps Link hay en la sesión |
| `LINK_QUANTUM` | pulsos por compás elegidos en Configuración › Sincronización |

Los valores se calculan para el momento en que el bloque **se va a escuchar**, no cuando se calcula: el reloj de Link (µs) se traduce al reloj de partitura de Csound con un desfase suavizado, más la latencia de salida que conoce el motor. Con el motor WASAPI propio es la latencia del stream que informa Windows más un periodo y medio del dispositivo; con PortAudio/ASIO (Csound maneja el dispositivo) la latencia es desconocida y se toma como 0, así que el sonido puede llegar unos ms tarde respecto de Live.

```csound
kbpm  chnget "LINK_BPM"
kbeat chnget "LINK_BEAT"
kph   chnget "LINK_PHASE"
kbpm  = (kbpm > 0 ? kbpm : 110)         ; tempo propio si Link está apagado
if changed(floor(kbeat)) == 1 then       ; empezó un pulso nuevo en este bloque
  schedulek "Click", 0, 0.05, (kph < 0.5 ? 1 : 0)
endif
```

El ejemplo **Showcase › 13 · Pulso Ableton Link** (`Examples/Showcase/13_link_pulse.csd`) lo hace bien: deriva un reloj de semicorcheas de `LINK_BEAT`, pone cada nota en su desfase exacto dentro del bloque de control, acentúa el primer tiempo con `LINK_PHASE`, cambia de acorde en cada compás y vuelve a su propio slider de tempo cuando Link está apagado.

Ojo: Link comparte la **fase**, no el número de pulso. Dos pares pueden contar los compases desde orígenes distintos (`LINK_BEAT` = 37,0 aquí y 5,0 en otra app), pero `LINK_PHASE` y la fracción del pulso siempre coinciden.

## Piano roll y secuenciador por pasos

Con Link activo, el transporte de las herramientas:

* **sigue el tempo de la sesión** (la casilla de tempo se actualiza sola) y un tempo escrito en la herramienta pasa a ser el de la sesión; pero solo abrir una ventana nunca cambia el tempo de Live;
* **empieza en el próximo compás** (fase 0 del quantum), como un clip en Live, y queda **en fase**: en cada pasada del programador (~4 ms) se re-ancla la correspondencia pulso → tiempo de Csound sobre la línea de tiempo de Link, así que los cambios de tempo de cualquier par se siguen sin deriva;
* con **Start Stop Sync**: ▶ en la herramienta arranca el transporte compartido (en el primer tiempo); Play en Live arranca las herramientas abiertas (en el compás de inicio de Live si aún no llega, si no en el siguiente); Stop en cualquier lado detiene al otro.

Las notas ya encoladas (el programador trabaja 120 ms adelantado) conservan el tempo anterior si un par lo cambia dentro de esa ventana: como mucho una o dos notas caen un poco antes o después justo en el instante del cambio.

## Firewall

Link usa UDP multicast (puerto 20808). La primera vez el Firewall de Windows puede preguntar si Csound Visual puede comunicarse en redes: permite **redes privadas** para sincronizar con otros computadores o celulares. Los programas del mismo PC funcionan aunque no lo permitas. En nuestras pruebas en este PC no apareció ningún aviso.

## Probar sin Live

* `CsoundVisual.exe --link-test [segundos] [--peer ruta\link_peer_test.exe]`: prueba completa y en silencio (Csound toca ceros por WASAPI). Un segundo proceso entra a 97 BPM, cambia el tempo, arranca y detiene el transporte; la prueba revisa los pares, la convergencia del tempo en ambos sentidos, la fase en los mismos instantes del reloj, los canales `LINK_*` tal como los ve la orquesta y un transporte enganchado a Link.
* `link_peer_test.exe --bpm 97 --seconds 30 --tempo-at 10:133 --play-at 15 --stop-at 20 --start-stop`: un par Link independiente que imprime la línea de tiempo; también sirve junto a Live.

## Compilación

`powershell -ExecutionPolicy Bypass -File tools\build_link.ps1` descarga Ableton Link 3.1.5 y el commit de asio-standalone que referencia en `src\AbletonLink\external\`, compila `CsoundVisualLink.dll` (Link + su API C oficial `abl_link`, x64, CRT estático, Release) y `link_peer_test.exe` con Visual Studio 2022, hace una prueba con dos pares y copia la DLL a `src\CsoundVisual\native\` (el csproj la copia junto al exe). **Sin la DLL la app funciona normal**: Link aparece como no disponible.

## Licencia

Ableton Link es **GPL-2.0 o posterior** (o una licencia comercial de Ableton). Compilarlo dentro de Csound Visual para uso personal está bien. Distribuir la app junto con `CsoundVisualLink.dll` deja esa distribución bajo la GPL (código disponible, misma licencia), salvo que Ableton otorgue una licencia propietaria (link-devs@ableton.com). La DLL es un archivo aparte y opcional, así que la app también se puede entregar sin ella.
