# Contributing

Thanks for your interest in Csound Visual! Bug reports, ideas and pull requests are welcome.

* **Bugs:** open an [issue](../../issues) with your Windows version, the audio device and engine
  (Settings › Audio), what you did, and the console output (right-click the console › *Copy whole
  console*). Attach the `.csd` if the problem depends on it.
* **Pull requests:** build instructions are in [BUILDING.md](BUILDING.md). Please keep the style of
  the surrounding code (C# with XML doc comments; views built in AXAML or in code with `AppTheme`
  brushes). Every visible string goes through `Loc.S("español", "English")` or the `{l:T Key}` table
  in `Localization/Strings.cs`, so the interface stays bilingual. Update the matching pages in
  `docs/en` and `docs/es` and run `python tools\build_docs.py`.
* **Licence:** contributions are accepted under the project's licence, GPL-3.0-or-later.
