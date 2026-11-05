# Cabbage instruments

[Cabbage](https://cabbageaudio.com) is a popular tool for turning Csound orchestras into plug-ins. A Cabbage instrument is an ordinary `.csd` file with an extra `<Cabbage> … </Cabbage>` section that declares its knobs, sliders, buttons, XY pads and keyboard. Csound Visual reads that section, **draws the widgets** and lets you play the instrument without Cabbage. The same file still opens in Cabbage, where it can be exported as a VST3/AU plug-in.

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

## Using it

1. Open a Cabbage `.csd` (or **Examples › ★ Cabbage instruments**). A **CABBAGE UI** card appears at the top of the side panel with the instrument's widgets, scaled to the panel's width.
2. **⧉ Window** opens the same UI in its own resizable window (it follows the tab you are looking at).
3. Press **Play**. Every widget sends its value to Csound in real time.

| Widget | Gesture |
|---|---|
| Knob (`rslider`, `encoder`) | drag up/down; **Ctrl** = fine; mouse wheel; **double-click** = default value. The value replaces the label while hovering. |
| Slider (`hslider`, `vslider`), range slider (`hrange`, `vrange`) | click or drag; Ctrl = fine; wheel; double-click = default |
| Number box (`nslider`) | drag up/down, wheel, double-click = default |
| Button | click toggles 0/1; with `latched(0)` it is 1 only while pressed. Buttons/checkboxes with the same `radioGroup` are exclusive |
| Checkbox | click toggles 0/1 |
| Combobox | sends the **1-based** index of the chosen item, as Cabbage does |
| Option button | each click moves to the next item (0-based) |
| XY pad | drag the ball: X → first channel, Y → second channel; double-click resets both |
| Keyboard | plays MIDI notes into Csound, like the on-screen keyboard (notes from your MIDI keyboard light up in green) |

The widgets move **the same channels as the ordinary sliders** of the «Live controls» card, which stay below. That means:

* moving a slider, a MIDI controller (**MIDI learn** is done on the slider of the same name) or dragging a value in the editor moves the widget too;
* **Send to DAW** keeps working: the plug-in in Cubase receives the widget moves;
* when Csound starts, **every widget's initial value is sent** before the first note (Cabbage instruments read their widgets with `chnget`, which would otherwise read 0).

## How a Cabbage file runs in Csound Visual

* Csound ignores the `<Cabbage>` section (verified with Csound 7.0), so nothing is removed.
* Cabbage files usually set `-n -d -+rtmidi=NULL -M0`. Csound Visual decides the output and the MIDI input itself, so `-n`, `-d` and `-+rtmidi=…` are dropped (with `-+rtmidi=NULL` the MIDI keyboards would be silent).
* Instruments made for Cabbage 2.5+ often use **Cabbage-only opcodes**. Csound Visual adds stand-ins (user-defined opcodes over the channel bus) when the file uses them and does not define them, and says so in the console:

| Opcode | Stand-in |
|---|---|
| `cabbageGetValue`, `cabbageGet "channel"` | reads the channel (`k`, `i`, `S`, and `kValue, kTrigger` forms) |
| `cabbageSetValue` | writes the channel (with or without trigger) |
| `cabbageChanged` | trigger when the channel changes |
| `cabbageSet`, `cabbageGet "channel", "identifier"` | accepted and ignored (widget appearance cannot be changed from the orchestra) |

  A `#line` directive keeps Csound's error line numbers pointing at the right line of the editor.
* Not provided: `cabbageCreate`, file/preset/state opcodes (`cabbageGetFilename`, `cabbageWriteStateData`…), MIDI-file and web opcodes. Csound reports them as unknown opcodes.

Tested on the 280 examples of the Cabbage 2 repository: all of them parse; **242/280** pass Csound 7's syntax check after the stand-ins (179 without them). Most of the rest use the unsupported opcodes above or old syntax that Csound 7 no longer accepts.

## Widgets and identifiers

**Drawn**: `form`, `rslider`, `hslider`, `vslider`, `nslider`, `encoder`, `hrange`, `vrange`, `button`, `checkbox`, `combobox`, `optionbutton`, `xypad`, `keyboard`, `label`, `groupbox`, `image` (a coloured rectangle/ellipse, and the picture when `file()` names a PNG/JPG next to the `.csd`), `line`, `infobutton` (inactive). Children inside `{ … }` (plants) are positioned relative to their parent.

**Shown as dashed placeholders**: `gentable`, `soundfiler`, `signaldisplay`, `filebutton`, `texteditor`, `listbox`, `csoundoutput` (see the console instead), meters (`hmeter`, `vmeter`: values written by the orchestra are not read back) and any unknown type. The card lists them under the panel; its tooltip shows the parser's warnings with line numbers.

**Identifiers understood**: `bounds`, `pos`, `size`, `channel` (one or two), `range(min, max, value, skew, increment)` (also `a:b` values for range sliders), `rangeX`, `rangeY`, `value`, `text` (label, or off/on captions, or combobox items), `items`, `caption`, `colour`, `colour:0`, `colour:1`, `fontColour`, `textColour`, `trackerColour`, `outlineColour`, `markerColour`, `ballColour` and other `…colour` names (RGB/RGBA numbers, a grey level, `#RRGGBB`, `#AARRGGBB` or CSS names such as `"cornflowerblue"`), `visible`, `active`, `alpha`, `latched`, `radioGroup`, `file`, `shape`, `outlineThickness`, `corners`, `align`, `fontSize`, `channelType("string")`, `min`, `max`, `increment`. Names are case-insensitive, commas between identifiers are optional, `;` comments, `/* */` blocks and `#define NAME …` / `$NAME` macros work. A line that starts with an identifier continues the previous widget.

**Ignored** (accepted silently): plug-in metadata (`pluginId`, `guiMode`…), `identChannel` (changing widgets from the orchestra), popups, value boxes, filmstrips, table options and similar cosmetics.

The newer **Cabbage 3 JSON** syntax (`<Cabbage>[{"type": "rotarySlider", …}]</Cabbage>`) is read on a best-effort basis (types, bounds, channels with ranges, items, colours as hex strings).

## Knobs in ordinary documents: `;@knob`

You do not need a Cabbage section to get knobs: add a comment to a channel line and the card (titled **KNOBS (;@)**) draws the widget, laid out automatically.

```
chnk "cutoff", 1, 3, 1000, 20, 20000   ;@knob "Cutoff"
chnk "res", 1, 2, 0.3, 0, 0.95         ;@knob
chnk "amp", 1, 2, 0.5, 0, 1            ;@slider "Level"
chnk "wave", 1, 1, 1, 1, 3             ;@combo Saw|Square|Triangle
chnk "x", 1, 2, 0.5, 0, 1              ;@xy "y"
chnk "y", 1, 2, 0.5, 0, 1
chnk "go", 1, 1, 0, 0, 1               ;@button "Go!"
```

Kinds: `knob`, `slider`, `vslider`, `button` (toggle), `momentary`, `checkbox`, `combo` (items separated by `|`), `xy "other channel"`. The range comes from the `chnk` declaration (an exponential `chnk`, itype 3, gets a skewed knob). The first quoted text after the kind is the label.

## Examples

**Examples › ★ Cabbage instruments**:

* **Knob synth** — three detuned oscillators and a ladder filter: knobs, sliders, a combobox, a checkbox and a keyboard. A short demo phrase plays at the start; then use the keyboard.
* **XY space delay** — an effect with an XY pad (delay time × feedback), freeze and ping buttons; it plays a built-in loop or the live input. Uses `cabbageGetValue`.
* **Drum toy** — an 8-step drum machine made of 24 checkboxes, with tempo, swing, tune, decay, bit-crush and room knobs. Uses `cabbageGetValue` at i-time.

All three render without errors through the app's engine (offline, 8 s, peaks 0.43 / 0.34 / 0.84).

## Code

| File | What it does |
|---|---|
| `Cabbage/CabbageModel.cs` | Widget, range (skew), colour and form models |
| `Cabbage/CabbageParser.cs` | Tolerant parser (Cabbage 2 lines, nesting, macros; Cabbage 3 JSON) |
| `Cabbage/CabbageSupport.cs` | Widget → channel specs (ranges/defaults), opcode stand-ins with `#line`, `;@knob` annotations |
| `Cabbage/CabbageControls.cs` | Drawn controls: knob, slider/range, number box, button/checkbox/option, XY pad, placeholder |
| `Cabbage/CabbagePanel.cs` | Builds the canvas (absolute positions, `Viewbox` scaling) and links widgets to the channel view models |
| `Cabbage/CabbageCard.cs` | Side-panel card and the tracker that follows the shown document |
| `Views/CabbageWindow.cs` | Pop-out window |
| `ViewModels/DocumentViewModel.Cabbage.cs` | `CabbageForm`, `IsCabbageDocument`, `HasWidgetPanel` |

Hooks in shared code: `DocumentViewModel.RefreshChannels` merges the widget channels (`CabbageSupport.Merge`), `MainViewModel.Launch` and *Send to DAW* call `CabbageSupport.PrepareForCsound`, `CsdTools.ExtractOptions` drops `-+rtmidi`, and `MainWindow.axaml` hosts `<cab:CabbageCard/>`.
