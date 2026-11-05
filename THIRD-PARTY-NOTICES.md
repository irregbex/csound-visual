# Third-party notices

Csound Visual is licensed under the **GNU General Public License, version 3 or later** (see
[LICENSE](LICENSE)). It uses, bundles or downloads the following third-party software and content.
Their licence texts are in the [licenses](licenses/) folder.

## Bundled in the release package

| Component | Used for | Licence | Source |
|---|---|---|---|
| **Csound 7** (`Csound7\` folder: `csound7_64.dll`, plug-ins, samples) | The sound engine, loaded as a separate dynamic library | LGPL-2.1-or-later — [licenses/LGPL-2.1-only.txt](licenses/LGPL-2.1-only.txt) | https://github.com/csound/csound |
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
