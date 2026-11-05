namespace CsoundVisual.Localization;

/// <summary>
/// The key-based translation table: every text that AXAML views show through <c>{l:T Key}</c>
/// (see <see cref="Loc"/> and <see cref="TExtension"/>), as a (Spanish, English) pair per key.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two ways to translate.</b> C# code normally does <i>not</i> use this table: it writes both
/// languages inline with <c>Loc.S("español", "English")</c>, so a message can never lose its
/// translation. The table exists because XAML cannot call <c>Loc.S</c>; a view writes
/// <c>Text="{l:T MW008}"</c> and <see cref="TExtension"/> turns that into a one-way binding to
/// <c>Loc.Instance["MW008"]</c>. When the language changes, <see cref="Loc.SetLanguage"/> raises
/// <c>PropertyChanged("Item[]")</c> and every such binding re-reads its entry, so the main window
/// switches language instantly. A few C# call sites also read the table directly through the
/// indexer (<c>Loc.Instance["Dialog.OpenCsd"]</c>, keyboard-shortcut labels in
/// <c>MainWindow.axaml.cs</c>) to reuse a text that already lives here.
/// </para>
/// <para>
/// <b>Key scheme.</b>
/// <list type="bullet">
/// <item><c>MWnnn</c> (MW001 … MW092): the main-window strings of the first translation pass of
/// <c>Views/MainWindow.axaml</c>, numbered in document order (toolbar first, status bar last).
/// Most of the audio-settings block (MW068–MW080) has since moved, with its keys unchanged, to
/// <c>Views/AudioSettingsView.axaml</c>. The numbers carry no meaning beyond that order; do not renumber them, the views reference them literally.</item>
/// <item><c>Area.Name</c> (e.g. <c>Toolbar.SaveAs</c>, <c>Audio.InputTip</c>): everything added
/// after that first pass. New entries must use this descriptive form; a <c>…Tip</c> suffix marks
/// a tooltip text.</item>
/// </list>
/// </para>
/// <para>
/// <b>Values.</b> Each value is a <c>(Es, En)</c> tuple: Spanish first (the app's original
/// language), English second. Texts use «guillemets» to quote UI labels, <c>\n</c> to break a
/// tooltip into a title line and a body, and <c>\\</c> for a literal backslash in Windows paths.
/// Names of UI elements inside a text must match the labels actually shown in that language.
/// </para>
/// <para>
/// <b>Missing keys.</b> <c>Loc.Instance[key]</c> returns the key itself when it is not in the
/// table, so a typo shows up on screen as the raw key (e.g. "MW999") instead of crashing.
/// </para>
/// </remarks>
public static class Strings
{
    /// <summary>Key → (Spanish, English). Read-only after start-up, so safe to read from any thread.</summary>
    public static readonly IReadOnlyDictionary<string, (string Es, string En)> Table = new Dictionary<string, (string, string)>
    {
        // ── Toolbar buttons added after the first translation pass (MainWindow.axaml, top bar
        //    and its «More» overflow menu). ──
        ["Toolbar.SaveAs"] = ("Guardar como", "Save as"),
        ["Toolbar.SaveAsTip"] = ("Guardar con otro nombre o en otra carpeta (Ctrl+Shift+S). «Guardar» (Ctrl+S) sobrescribe el archivo actual.", "Save under another name or folder (Ctrl+Shift+S). «Save» (Ctrl+S) overwrites the current file."),
        ["Toolbar.Examples"] = ("Ejemplos", "Examples"),
        ["Toolbar.Plugins"] = ("Plugins", "Plug-ins"),
        ["Toolbar.Paint"] = ("Pintar", "Paint"),
        ["Toolbar.PaintTip"] = ("Paint Rooms  (Ctrl+Shift+I)\nComponer y diseñar sonido pintando imágenes: lienzo tiempo × altura, pinceles, imágenes importadas, escalas; lo toca Csound.",
                                "Paint Rooms  (Ctrl+Shift+I)\nCompose and design sound by painting images: time × pitch canvas, brushes, imported pictures, scales; played by Csound."),
        ["Toolbar.PluginsTip"] = ("Plugins VST3  (Ctrl+Shift+V)\nCadena de efectos e instrumentos VST3 después de Csound, con sus propias ventanas. Se guarda entre sesiones.",
                                  "VST3 plug-ins  (Ctrl+Shift+V)\nChain of VST3 effects and instruments after Csound, with their own windows. Kept between sessions."),
        ["Toolbar.MoreTip"] = ("Más: con la ventana angosta, aquí están Nuevo, Abrir, Guardar como, Ejemplos, Piano roll, Secuenciador, Renderizar y Enviar al DAW.",
                               "More: with a narrow window, New, Open, Save as, Examples, Piano roll, Step sequencer, Render and Send to DAW are here."),
        // ── «Examples» toolbar button and its menu (example browser, demo). ──
        ["Examples.Tip"] = ("Explorador de ejemplos (Ctrl+E): unos 1500 ejemplos del manual de Csound por categoría, con búsqueda, vista previa, Abrir y Tocar.", "Example browser (Ctrl+E): about 1500 examples from the Csound manual by category, with search, preview, Open and Play."),
        ["Examples.Demo"] = ("Abrir demo", "Open demo"),
        ["Examples.DemoTip"] = ("Abre la demo de Csound Visual (la del primer arranque) en una pestaña nueva sin nombre: «Guardar» pedirá un nombre, el original no se toca.", "Opens the Csound Visual demo (the first-start one) in a new untitled tab: «Save» asks for a name, the original is never touched."),
        ["Examples.Browse"] = ("Explorar ejemplos…", "Browse examples…"),
        // ── WATCH panel: enlarged view of a watched variable's graph (overlay in the main window). ──
        ["Watch.ClickToEnlarge"] = ("Clic para verla en grande en el centro de la ventana.", "Click to see it enlarged in the centre of the window."),
        ["Watch.Close"] = ("Cerrar (Esc)", "Close (Esc)"),
        ["Watch.CloseHint"] = ("Clic afuera o Esc para volver", "Click outside or Esc to go back"),
        // ── «Send to DAW»: hands the document to the Csound Visual VST3 plug-in running in a DAW. ──
        ["Daw.Button"] = ("Enviar al DAW", "Send to DAW"),
        ["Daw.Tip"] = ("Envía este código al plugin VST3 «Csound Visual» abierto en Cubase (u otro DAW): suena dentro de tu proyecto y los sliders de la app lo controlan en vivo.", "Sends this code to the «Csound Visual» VST3 plugin open in Cubase (or another DAW): it plays inside your project and the app's sliders control it live."),
        ["Daw.LinkTip"] = ("Los sliders de la app están conectados a este plugin del DAW.", "The app's sliders are linked to this DAW plugin."),
        // ── «Tools» toolbar menu: piano roll and step sequencer. ──
        ["Tools.Button"] = ("Herramientas", "Tools"),
        ["Tools.Tip"] = ("Herramientas musicales: Piano roll (Ctrl+P) para dibujar notas y Secuenciador por pasos (Ctrl+Shift+P) para ritmos, con kit de batería. Tocan los instrumentos del documento en vivo con Csound o se escriben en la partitura.",
                         "Music tools: Piano roll (Ctrl+P) to draw notes and Step sequencer (Ctrl+Shift+P) for beats, with a drum kit. They play the document's instruments live through Csound or are written into the score."),
        ["Tools.PianoRoll"] = ("Piano roll", "Piano roll"),
        ["Tools.Sequencer"] = ("Secuenciador por pasos", "Step sequencer"),
        ["Tools.SequencerShort"] = ("Pasos", "Steps"),
        ["Tools.PianoRollTip"] = ("Piano roll  (Ctrl+P)\nDibuja notas en una cuadrícula altura × tiempo y tócalas con un instrumento del documento, o insértalas en la partitura.",
                                  "Piano roll  (Ctrl+P)\nDraw notes on a pitch × time grid and play them with an instrument of the document, or insert them into the score."),
        ["Tools.SequencerTip"] = ("Secuenciador por pasos  (Ctrl+Shift+P)\nRitmos por pasos con swing, mute/solo y relleno euclidiano. Trae un kit de batería listo para sonar («Abrir kit de batería»).",
                                  "Step sequencer  (Ctrl+Shift+P)\nStep-based beats with swing, mute/solo and Euclidean fills. Comes with a drum kit ready to play («Open drum kit»)."),
        // ── Settings button tooltip and the per-port tooltip of the MIDI panel. ──
        ["Settings.Tip"] = ("Configuración (Ctrl+,): idioma, tema, editor, carpetas y versiones.", "Settings (Ctrl+,): language, theme, editor, folders and versions."),
        ["Midi.PortTip"] = ("Activa este puerto. «MIDI 2.0» = Windows MIDI Services (alta resolución, recomendado). «WinMM» = la API MIDI clásica (MIDI 1.0). No actives los dos para el mismo teclado.", "Enable this port. «MIDI 2.0» = Windows MIDI Services (high resolution, recommended). «WinMM» = the classic MIDI API (MIDI 1.0). Do not enable both for the same keyboard."),
        // ── Audio: live-input settings (Views/AudioSettingsView.axaml) and the input / DSP meters of
        //    the main window. ──
        ["Audio.Input"] = ("Entrada", "Input"),
        ["Audio.InputTip"] = ("Micrófono o entrada de la interfaz que llega a Csound en vivo (opcodes «ain1, ain2 in» o «inch»). «Ninguna» = como antes, «in» lee silencio. Funciona en modo compartido junto a otras apps; si su frecuencia difiere de la salida, la app remuestrea y corrige la deriva de reloj entre los dos dispositivos. Usa audífonos para evitar acoples.", "Microphone or interface input fed live to Csound (opcodes «ain1, ain2 in» or «inch»). «None» = as before, «in» reads silence. Works in shared mode alongside other apps; if its sample rate differs from the output, the app resamples and corrects the clock drift between the two devices. Use headphones to avoid feedback."),
        ["Audio.InputRouting"] = ("Canales de entrada", "Input channels"),
        ["Audio.InputRoutingTip"] = ("Cómo llegan las entradas de la interfaz a Csound. Una interfaz como la UR22 entrega sus dos entradas como un dispositivo estéreo (entrada 1 = izquierda, entrada 2 = derecha). «Estéreo» las mantiene como dos canales independientes: en el código, inch 1 = entrada 1 e inch 2 = entrada 2. Si conectas un solo micrófono, elige «Mono: solo la entrada 1» para que los efectos estéreo lo escuchen en ambos lados. Se aplica al instante, también mientras suena.",
                                      "How the interface's inputs reach Csound. An interface like the UR22 delivers its two inputs as one stereo device (input 1 = left, input 2 = right). «Stereo» keeps them as two independent channels: in the code, inch 1 = input 1 and inch 2 = input 2. With a single microphone, choose «Mono: input 1 only» so stereo effects hear it on both sides. Applies instantly, also while playing."),
        ["Audio.InputHostOnly"] = ("La entrada solo se usa con «Windows · WASAPI». Con PortAudio/ASIO agrega -iadc en <CsOptions>.", "The input is only used with «Windows · WASAPI». With PortAudio/ASIO add -iadc to <CsOptions>."),
        ["Audio.InMeter"] = ("ENT", "IN"),
        ["Audio.DspTip"] = ("Carga DSP: cuánto del tiempo de cada buffer de audio necesita Csound para calcularlo. Verde = holgado; amarillo (70 %) = queda poco margen; rojo (90 %) = a punto de cortarse. La rayita marca el pico reciente. Si sube: aumenta ksmps o la latencia en Configuración › Audio, o simplifica la orquesta. RAM = memoria de toda la app (Csound corre adentro; las tablas y samples cargados cuentan aquí).",
                            "DSP load: how much of each audio buffer's time Csound needs to compute it. Green = plenty of room; yellow (70 %) = little headroom; red (90 %) = about to break up. The tick marks the recent peak. If it climbs: raise ksmps or the latency in Settings › Audio, or simplify the orchestra. RAM = memory of the whole app (Csound runs inside it; loaded tables and samples count here)."),
        ["Audio.InMeterTip"] = ("Nivel de la entrada de audio en vivo (micrófono o interfaz) antes de Csound, de −60 a 0 dBFS. Si llega al rojo, baja la ganancia en la interfaz.", "Level of the live audio input (microphone or interface) before Csound, −60 to 0 dBFS. If it reaches red, lower the gain on the interface."),
        // ── File-dialog titles, read from C# (MainWindow.axaml.cs) through Loc.Instance[…]. ──
        ["Dialog.OpenCsd"] = ("Abrir archivo de Csound", "Open Csound file"),
        ["Dialog.SaveCsd"] = ("Guardar archivo de Csound", "Save Csound file"),
        ["Dialog.RenderWav"] = ("Renderizar a WAV", "Render to WAV"),
        // ═══ MWnnn: main window, in AXAML document order. ═══
        // MW001–MW020: toolbar — New / Open / Save, transport (Play, Stop), recording, 3D window,
        // render, the "playing" indicator and the recording timer.
        ["MW001"] = ("Nuevo (Ctrl+N)", "New (Ctrl+N)"),
        ["MW002"] = ("Nuevo", "New"),
        ["MW003"] = ("Abrir (Ctrl+O)", "Open (Ctrl+O)"),
        ["MW004"] = ("Abrir", "Open"),
        ["MW005"] = ("Guardar (Ctrl+S)", "Save (Ctrl+S)"),
        ["MW006"] = ("Guardar", "Save"),
        ["MW007"] = ("Tocar (F5): enciende Csound. Mientras no esté en modo Tocar, nada suena (ni el teclado MIDI).", "Play (F5): switches Csound on. Until it is in Play mode nothing sounds (not even the MIDI keyboard)."),
        ["MW008"] = ("Tocar", "Play"),
        ["MW009"] = ("Para que Csound suene debe estar en modo Tocar", "Csound must be in Play mode to make sound"),
        ["MW010"] = ("Presiona Tocar (F5): enciende el motor de sonido. Luego el teclado y el MIDI suenan.", "Press Play (F5): it switches the sound engine on. Then the keyboard and MIDI make sound."),
        ["MW011"] = ("Detener (Shift+F5)", "Stop (Shift+F5)"),
        ["MW012"] = ("Detener", "Stop"),
        ["MW013"] = ("Abrir carpeta de grabaciones", "Open recordings folder"),
        ["MW014"] = ("Graba lo que está sonando (con tus cambios en vivo) a un WAV en Documentos\\Csound Visual\\Grabaciones. Se habilita solo mientras suena algo: presiona Tocar primero. Clic de nuevo para terminar. Clic derecho: abrir la carpeta de grabaciones.", "Records what is playing (with your live changes) to a WAV in Documents\\Csound Visual\\Grabaciones. Enabled only while something is playing: press Play first. Click again to finish. Right-click: open the recordings folder."),
        ["MW015"] = ("Detener grabación", "Stop recording"),
        ["MW016"] = ("Abre la ventana 3D (OpenGL): espectrograma en cascada, pad XYZ para mover tres canales a la vez y panorámica espacial binaural. Se puede llevar a otro monitor y poner en pantalla completa (F11). Atajo: Ctrl+3.", "Opens the 3D window (OpenGL): waterfall spectrogram, XYZ pad to move three channels at once and binaural spatial panner. It can go to another monitor and full screen (F11). Shortcut: Ctrl+3."),
        ["MW017"] = ("Renderizar a WAV (Ctrl+R)", "Render to WAV (Ctrl+R)"),
        ["MW018"] = ("Renderizar", "Render"),
        ["MW019"] = ("Verde y latiendo = Csound está sonando.", "Green and pulsing = Csound is playing."),
        ["MW020"] = ("Tiempo grabado.", "Recorded time."),
        // MW021–MW024: side-panel toggle and the document tab strip.
        ["MW021"] = ("Panel lateral (Ctrl+B)", "Side panel (Ctrl+B)"),
        ["MW022"] = ("Archivos abiertos. El punto verde marca el que suena; • indica cambios sin guardar.", "Open files. The green dot marks the one playing; • means unsaved changes."),
        ["MW023"] = ("Cerrar pestaña", "Close tab"),
        ["MW024"] = ("Nueva pestaña (Ctrl+N)", "New tab (Ctrl+N)"),
        ["Editor.UndoTip"] = ("Deshacer en el editor (Ctrl+Z)", "Undo in the editor (Ctrl+Z)"),
        ["Editor.RedoTip"] = ("Rehacer en el editor (Ctrl+Y)", "Redo in the editor (Ctrl+Y)"),
        // MW025–MW029: console (splitter, header, error hint, clear button).
        ["MW025"] = ("Arrastra para cambiar el alto de la consola.", "Drag to change the console height."),
        ["MW026"] = ("CONSOLA", "CONSOLE"),
        ["MW027"] = ("Mensajes de Csound. Rojo = error, naranja = advertencia. Clic en una línea con número para ir a esa línea del código.", "Csound messages. Red = error, orange = warning. Click a line with a number to jump to that line of code."),
        ["MW028"] = ("clic en un error para ir a su línea", "click an error to jump to its line"),
        ["MW029"] = ("Limpiar consola", "Clear console"),
        // MW030–MW033: on-screen MIDI keyboard. MW034: side-panel width splitter.
        ["MW030"] = ("TECLADO", "KEYBOARD"),
        ["MW031"] = ("Toca notas MIDI con el mouse o con el teclado del PC. Csound las recibe como cualquier controlador MIDI.", "Play MIDI notes with the mouse or the PC keyboard. Csound receives them like any MIDI controller."),
        ["MW032"] = ("Clic aquí y usa A W S E D F… · Z/X octava · C/V fuerza", "Click here and use A W S E D F… · Z/X octave · C/V velocity"),
        ["MW033"] = ("Clic para tocar (más abajo en la tecla = más fuerte), arrastra para glissando. Con el foco aquí: A W S E D F T G Y H U J K O L P tocan notas, Z/X bajan o suben la octava, C/V bajan o suben la fuerza. Las notas que llegan de tu controlador MIDI se ven en verde.", "Click to play (lower on the key = louder), drag for glissando. With the focus here: A W S E D F T G Y H U J K O L P play notes, Z/X move the octave down or up, C/V lower or raise the velocity. Notes coming from your MIDI controller show in green."),
        ["MW034"] = ("Arrastra para cambiar el ancho del panel lateral.", "Drag to change the width of the side panel."),
        // Side-panel cards, top to bottom.
        // MW035–MW039: OUTPUT card (oscilloscope, spectrum, peak meters).
        ["MW035"] = ("SALIDA", "OUTPUT"),
        ["MW036"] = ("Lo que está sonando: forma de onda y niveles.", "What is playing: waveform and levels."),
        ["MW037"] = ("Osciloscopio: la forma de onda de la mezcla de salida, sincronizada para que no se mueva.", "Oscilloscope: the waveform of the output mix, synchronised so it stands still."),
        ["MW038"] = ("Espectro de la salida: cuánta energía hay en cada frecuencia (20 Hz – 20 kHz, escala logarítmica). La línea clara retiene los picos.", "Output spectrum: how much energy there is at each frequency (20 Hz – 20 kHz, logarithmic scale). The light line holds the peaks."),
        ["MW039"] = ("Medidores de pico por canal (de −60 a 0 dBFS). La marca clara retiene el pico reciente; el rojo indica que te acercas a saturar.", "Peak meters per channel (−60 to 0 dBFS). The light mark holds the recent peak; red means you are getting close to clipping."),
        // MW040–MW045: LIVE CONTROLS card (one slider per chnget/chnk channel, MIDI learn).
        ["MW040"] = ("CONTROLES EN VIVO", "LIVE CONTROLS"),
        ["MW041"] = ("Un slider por cada canal de control (chnk o chnget) del código. Muévelos mientras suena.", "One slider per control channel (chnk or chnget) in the code. Move them while it plays."),
        ["MW042"] = ("Usa chnget \"nombre\" o chnk en la orquesta y aparecerá un slider aquí.", "Use chnget \"name\" or chnk in the orchestra and a slider will appear here."),
        ["MW043"] = ("MIDI learn: clic y luego mueve una perilla o fader de tu controlador para asignarlo a este slider. Clic de nuevo para cancelar.", "MIDI learn: click, then move a knob or fader on your controller to assign it to this slider. Click again to cancel."),
        ["MW044"] = ("Quitar asignación MIDI", "Remove MIDI assignment"),
        ["MW045"] = ("Tip: también puedes arrastrar la variable o el número directamente en el código.", "Tip: you can also drag the variable or the number directly in the code."),
        // MW046–MW052: WATCH card (pinned variables read live through the Csound 7 debugger).
        ["MW046"] = ("Variables que fijaste para ver su valor mientras suena. Clic derecho sobre una variable del código › Observar.", "Variables you pinned to watch their value while it plays. Right-click a variable in the code › Watch."),
        ["MW047"] = ("OBSERVAR", "WATCH"),
        ["MW048"] = ("Valores en vivo", "Live values"),
        ["MW049"] = ("Lee los valores de las variables mientras suena (con el debugger de Csound 7). Se ven en la ayuda al pasar el mouse y en este panel. Cuesta un poco de CPU; desactívalo si necesitas la latencia mínima.", "Reads the variables' values while it plays (with the Csound 7 debugger). They show in the hover help and in this panel. It costs a little CPU; turn it off if you need the lowest latency."),
        ["MW050"] = ("Clic derecho sobre una variable del código › «Observar» para fijarla aquí.", "Right-click a variable in the code › «Watch» to pin it here."),
        ["MW051"] = ("Dejar de observar", "Stop watching"),
        ["MW052"] = ("Los últimos ≈3 segundos de la variable.", "The last ≈3 seconds of the variable."),
        // MW053–MW057: LIVE EVENT card (send one score line to the running performance).
        ["MW053"] = ("EVENTO EN VIVO", "LIVE EVENT"),
        ["MW054"] = ("Envía una nota o evento de partitura a lo que está sonando.", "Sends a note or score event to what is playing."),
        ["MW055"] = ("Formato: i instrumento inicio duración p4 p5 … (por ejemplo i \"Voice\" 0 1 64 0.9 0.5). Enter para enviar.", "Format: i instrument start duration p4 p5 … (for example i \"Voice\" 0 1 64 0.9 0.5). Enter to send."),
        ["MW056"] = ("Enviar (Enter)", "Send (Enter)"),
        ["MW057"] = ("Una línea de partitura, p. ej. i 1 0 2 440. Se envía mientras suena.", "One score line, e.g. i 1 0 2 440. Sent while it plays."),
        // MW058–MW063: MIDI card (ports, keyboard toggle, rescan, MIDI 2.0 note, monitor).
        ["MW058"] = ("Entradas MIDI. Lo que llega por los puertos activados va a Csound (massign, notnum, ctrl7…) y a los sliders asignados con MIDI learn.", "MIDI inputs. What arrives through the enabled ports goes to Csound (massign, notnum, ctrl7…) and to the sliders assigned with MIDI learn."),
        ["MW059"] = ("Mostrar u ocultar el teclado en pantalla.", "Show or hide the on-screen keyboard."),
        ["MW060"] = ("Teclado", "Keyboard"),
        ["MW061"] = ("Volver a buscar dispositivos MIDI (por ejemplo si conectaste uno nuevo).", "Search MIDI devices again (for example if you plugged in a new one)."),
        ["MW062"] = ("MIDI 2.0 usa Windows MIDI Services (Windows 11 25H2+): velocidad de 16 bits y controladores de 32 bits. Un mismo teclado aparece como «MIDI 2.0» y como «WinMM»: activa solo uno, si no las notas llegan dos veces.", "MIDI 2.0 uses Windows MIDI Services (Windows 11 25H2+): 16-bit velocity and 32-bit controllers. The same keyboard shows up as «MIDI 2.0» and as «WinMM»: enable only one, otherwise notes arrive twice."),
        ["MW063"] = ("Monitor MIDI: los últimos mensajes recibidos (el más nuevo arriba).", "MIDI monitor: the last messages received (newest on top)."),
        // MW064–MW066: APPEARANCE (colour theme presets).
        ["MW064"] = ("Colores de toda la aplicación: ventanas, editor, medidores y vistas 3D. Se aplica al instante y se recuerda.", "Colours of the whole application: windows, editor, meters and 3D views. Applied instantly and remembered."),
        ["MW065"] = ("APARIENCIA", "APPEARANCE"),
        ["MW066"] = ("Elige un tema de colores. «Azul deportivo» es el predeterminado; «Ámbar clásico» es el aspecto original.", "Choose a colour theme. «Azul deportivo» (sports blue) is the default; «Ámbar clásico» is the original look."),
        // MW067–MW080: audio output settings (engine, device, latency, ksmps, diagnostics, stream
        // info); MW068–MW080 are now used by Views/AudioSettingsView.axaml, and MW067 (the old card
        // tooltip) is no longer referenced by any view.
        ["MW067"] = ("Dónde y cómo suena Csound. Los cambios se aplican la próxima vez que presiones Tocar.", "Where and how Csound sounds. Changes apply the next time you press Play."),
        ["MW068"] = ("buscando dispositivos…", "looking for devices…"),
        ["MW069"] = ("Motor", "Engine"),
        ["MW070"] = ("WASAPI compartido: la salida normal de Windows 11, rápida y sin cortar otras apps. Exclusivo: la latencia más baja sin ASIO. PortAudio: para drivers ASIO.", "Shared WASAPI: Windows 11's normal output, fast and without cutting off other apps. Exclusive: the lowest latency without ASIO. PortAudio: for ASIO drivers."),
        ["MW071"] = ("Salida", "Output"),
        ["MW072"] = ("El dispositivo que suena. «Predeterminado de Windows» sigue al que elijas en Configuración › Sonido.", "The device that plays. «Windows default» follows the one you choose in Settings › Sound."),
        ["MW073"] = ("Latencia", "Latency"),
        ["MW074"] = ("Cuánto tarda en sonar lo que tocas. Menos latencia exige más al equipo: si oyes cortes, sube un nivel.", "How long it takes for what you play to sound. Lower latency asks more of the computer: if you hear dropouts, go up one level."),
        ["MW075"] = ("Muestras por ciclo de control. Más chico = controles y MIDI más precisos pero más CPU. «Del archivo» respeta el ksmps del .csd.", "Samples per control cycle. Smaller = more precise controls and MIDI but more CPU. «From file» keeps the .csd's ksmps."),
        ["MW076"] = ("Abre el panel de Sonido de Windows. Ahí, en Reproducción › tu dispositivo › Propiedades › Opciones avanzadas, cambias la frecuencia (por ejemplo a 48000 Hz para tener ≈3 ms en exclusivo) y permites o no el modo exclusivo.", "Opens the Windows Sound panel. There, in Playback › your device › Properties › Advanced, you change the sample rate (for example to 48000 Hz for ≈3 ms in exclusive mode) and allow or block exclusive mode."),
        ["MW077"] = ("Panel de sonido de Windows", "Windows Sound panel"),
        ["MW078"] = ("Muestra qué puede hacer cada salida de audio: formato, latencia en modo compartido y exclusivo. No reproduce nada.", "Shows what each audio output can do: format, latency in shared and exclusive mode. It plays nothing."),
        ["MW079"] = ("Diagnóstico de audio", "Audio diagnostics"),
        ["MW080"] = ("El stream de audio que realmente se abrió: dispositivo, modo, frecuencia, formato y latencia estimada. «dispositivo … Hz» es la frecuencia real del dispositivo en Windows y «Csound sr» la que usa Csound (con el motor WASAPI de la app siempre coinciden: el sr del .csd se reemplaza por el del dispositivo). Si hay entrada, su línea va debajo.", "The audio stream that was actually opened: device, mode, sample rate, format and estimated latency. «device … Hz» is the device's real rate in Windows and «Csound sr» the one Csound uses (with the app's WASAPI engine they always match: the .csd's sr is replaced by the device's). With an input, its line comes below."),
        // MW081–MW085: TABLES card (function tables read from the running Csound).
        ["MW081"] = ("TABLAS", "TABLES"),
        ["MW082"] = ("Las tablas de función (ftgen, líneas f) leídas de Csound mientras suena.", "The function tables (ftgen, f lines) read from Csound while it plays."),
        ["MW083"] = ("Volver a leer las tablas", "Read the tables again"),
        ["MW084"] = ("Elige qué tabla ver.", "Choose which table to show."),
        ["MW085"] = ("Contenido de la tabla elegida: barras si es corta (escalas, secuencias), curva si es larga (formas de onda, envolventes).", "Contents of the chosen table: bars if it is short (scales, sequences), a curve if it is long (waveforms, envelopes)."),
        // MW086–MW087: EXAMPLES card. MW088–MW092: status bar (Csound version, editor hint,
        // DSP / dropout read-out, cursor position, file type).
        ["MW086"] = ("EJEMPLOS", "EXAMPLES"),
        ["MW087"] = ("Archivos de ejemplo que vienen con Csound. Clic para abrirlos en una pestaña.", "Example files that come with Csound. Click to open them in a tab."),
        ["MW088"] = ("Versión de la biblioteca de Csound cargada.", "Version of the Csound library loaded."),
        ["MW089"] = ("Pasa el mouse sobre una variable o número para ver su ayuda · arrástralo para cambiarlo · Esc vuelve al valor anterior", "Hover a variable or number to see its help · drag it to change it · Esc goes back to the previous value"),
        ["MW090"] = ("Carga de DSP: cuánto del tiempo disponible usa Csound para calcular el audio (sobre 100 % se corta). Cortes: veces que el dispositivo se quedó sin muestras. «entrada: cortes» (si hay entrada en vivo): veces que la entrada de audio no llegó a tiempo y se reemplazó por silencio.", "DSP load: how much of the available time Csound uses to compute the audio (above 100 % it drops out). Dropouts: times the device ran out of samples. «input: dropouts» (with a live input): times the audio input did not arrive in time and was replaced by silence."),
        ["MW091"] = ("Línea y columna del cursor.", "Line and column of the cursor."),
        ["MW092"] = ("Tipo de archivo del editor.", "Type of file in the editor."),
    };
}
