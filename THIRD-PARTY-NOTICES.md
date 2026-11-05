# Third-party notices

Csound Visual is licensed under the **GNU General Public License, version 3 or later** (see
[LICENSE](LICENSE)). It uses, bundles or downloads the following third-party software and content.
Their licence texts are in the [licenses](licenses/) folder.

## Bundled in the release package

| Component | Used for | Licence | Source |
|---|---|---|---|
| **Csound 7** (`Csound7\` folder: `csound7_64.dll`, plug-ins, samples; also inside `CsoundVisual.vst3`) | The sound engine, loaded as a separate, unmodified dynamic library | LGPL-2.1-or-later — © The Csound Developers — [licenses/LGPL-2.1-only.txt](licenses/LGPL-2.1-only.txt) | Exact source of the bundled build, see [Csound source code](#csound-source-code) below |
| **.NET 10 runtime** (self-contained release) | Runs the app | MIT — © .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| **Avalonia 12** (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`) | User interface | MIT — © The AvaloniaUI Project | https://github.com/AvaloniaUI/Avalonia |
| **AvaloniaEdit 12** | Code editor | MIT — © AvaloniaUI, © ICSharpCode (AvalonEdit) | https://github.com/AvaloniaUI/AvaloniaEdit |
| **CommunityToolkit.Mvvm 8** | View models | MIT — © .NET Foundation and Contributors | https://github.com/CommunityToolkit/dotnet |
| **Silk.NET.OpenGLES 2** | OpenGL ES bindings for the 3D views | MIT — © .NET Foundation and Contributors | https://github.com/dotnet/Silk.NET |
| **Microsoft.Windows.CsWinRT 2** | WinRT projection (MIDI 2.0 API) | MIT — © Microsoft Corporation | https://github.com/microsoft/CsWinRT |
| **Windows MIDI Services SDK** (`Windows.Devices.Midi2` 0.99.92-preview.11) | MIDI 2.0 input | MIT — © Microsoft Corporation | https://github.com/microsoft/MIDI |
| **Steinberg VST 3 SDK 3.8** (compiled into `CsoundVisualVstHost.dll` and `CsoundVisual.vst3`) | Hosting VST3 plug-ins; the Csound Visual VST3 plug-in | MIT — © Steinberg Media Technologies GmbH — [licenses/VST3-SDK-MIT.txt](licenses/VST3-SDK-MIT.txt) | https://github.com/steinbergmedia/vst3sdk |
| **Ableton Link 3.1.5** (compiled into `CsoundVisualLink.dll`) | Tempo/beat sync over the network | GPL-2.0-or-later — © Ableton AG — [licenses/Ableton-Link-LICENSE.md](licenses/Ableton-Link-LICENSE.md), [licenses/GPL-2.0-only.txt](licenses/GPL-2.0-only.txt) | https://github.com/Ableton/link |
| **asio (standalone)** (used by Ableton Link) | Networking | Boost Software License 1.0 — © Christopher M. Kohlhoff — [licenses/asio-BSL-1.0.txt](licenses/asio-BSL-1.0.txt) | https://github.com/chriskohlhoff/asio |

The MIT licence text that applies to the MIT components above is in [licenses/MIT.txt](licenses/MIT.txt)
(with each component's copyright holder as listed).

### Libraries included in the Csound 7 binaries

The Csound 7 build is distributed unmodified, as published by the Csound project. Its files
contain these third-party libraries (statically linked by the Csound build):

| Library | Inside | Licence | Source |
|---|---|---|---|
| **libsndfile 1.2.2** | `csound7_64.dll` | LGPL-2.1-or-later — © Erik de Castro Lopo and contributors — [licenses/LGPL-2.1-only.txt](licenses/LGPL-2.1-only.txt) | https://github.com/libsndfile/libsndfile |
| **mpg123** | `csound7_64.dll` | LGPL-2.1 — © Michael Hipp and others — [licenses/mpg123-COPYING.txt](licenses/mpg123-COPYING.txt) | https://www.mpg123.de |
| **LAME** | `csound7_64.dll` | LGPL-2.0-or-later — © the LAME project — [licenses/LGPL-2.0-only.txt](licenses/LGPL-2.0-only.txt) | https://lame.sourceforge.io |
| **FLAC** | `csound7_64.dll` | BSD-3-Clause — © Josh Coalson, © Xiph.Org Foundation — [licenses/FLAC-BSD.txt](licenses/FLAC-BSD.txt) | https://github.com/xiph/flac |
| **libogg** | `csound7_64.dll` | BSD-3-Clause — © Xiph.Org Foundation — [licenses/libogg-BSD.txt](licenses/libogg-BSD.txt) | https://github.com/xiph/ogg |
| **libvorbis 1.3.7** | `csound7_64.dll` | BSD-3-Clause — © Xiph.Org Foundation — [licenses/libvorbis-BSD.txt](licenses/libvorbis-BSD.txt) | https://github.com/xiph/vorbis |
| **Opus 1.5.2** | `csound7_64.dll` | BSD-3-Clause — © Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry, CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo, Mozilla, Amazon — [licenses/opus-BSD.txt](licenses/opus-BSD.txt) | https://github.com/xiph/opus |
| **liblo** | `plugins64\osc.dll` | LGPL-2.1-or-later — © Steve Harris, Stephen Sinclair and contributors — [licenses/LGPL-2.1-only.txt](licenses/LGPL-2.1-only.txt) | https://github.com/radarsat1/liblo |
| **PortAudio 19.7** | `plugins64\rtpa.dll` | MIT — © Ross Bencina and Phil Burk — [licenses/PortAudio-MIT.txt](licenses/PortAudio-MIT.txt) | https://github.com/PortAudio/portaudio |
| **PortMidi** | `plugins64\pmidi.dll` | MIT — © Ross Bencina and Phil Burk, © Roger B. Dannenberg — [licenses/PortMidi-MIT.txt](licenses/PortMidi-MIT.txt) | https://github.com/PortMidi/portmidi |

### Csound source code

Csound Visual 0.1.0 bundles **Csound 7.0 (double samples), built on 23 Oct 2026 from commit
`44d1c5821ed545ccd155d231970b53a8ea0b633c`** (`csound --version` prints it). Its complete source
code is at https://github.com/csound/csound/tree/44d1c5821ed545ccd155d231970b53a8ea0b633c, and a
copy is attached to the Csound Visual release on GitHub as `csound-source-44d1c58.zip`. The source
of the libraries above is at the addresses in the table. Because Csound is loaded as a separate
library, you can replace `Csound7\bin\csound7_64.dll` (and the plug-ins) with your own build of
Csound 7.

**Why GPL-3.0:** Ableton Link is licensed under GPL-2.0-or-later, so a program that includes it must
be distributed under a compatible licence. GPL-3.0-or-later is compatible with Link, with Csound's
LGPL-2.1-or-later and with the MIT components.

## Example content

| Folder (`src/CsoundVisual/Examples/`) | Origin | Licence |
|---|---|---|
| `Manual/` | Examples and sound files of the Canonical Csound Reference Manual (https://github.com/csound/manual) | GNU Free Documentation License 1.2 or later — [licenses/GFDL-1.2-only.txt](licenses/GFDL-1.2-only.txt). See `Manual/LICENSE.md`. |
| `Csound7/` | Examples shipped with the Csound 7 installation | LGPL-2.1-or-later (Csound) |
| `Showcase/`, `AudioFX/`, `Cabbage/` | Written for Csound Visual (the AudioFX pieces play sound files from `Manual/`) | GPL-3.0-or-later, like the rest of the project |

## Trademarks

* **VST** is a registered trademark of Steinberg Media Technologies GmbH. Csound Visual is not
  affiliated with or endorsed by Steinberg.
* **Ableton** and **Link** are trademarks of Ableton AG. Csound Visual is not affiliated with or
  endorsed by Ableton.
* **Cubase** is a trademark of Steinberg Media Technologies GmbH; **Windows** is a trademark of
  Microsoft Corporation; **Cabbage** is a project by Rory Walsh. Csound Visual reads Cabbage's
  widget syntax but contains no Cabbage code.
* Plug-in names mentioned in the documentation belong to their respective owners.

## Not included

* Third-party VST3 plug-ins are never included; Csound Visual only loads those installed on the
  user's computer.
* The SDKs (VST3, Ableton Link, asio) and the MIDI 2.0 package are not stored in the source
  repository: the build scripts in `tools/` download them from their official sources.
