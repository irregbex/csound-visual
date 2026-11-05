using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CsoundVisual.Interop;
using CsoundVisual.Localization;
using CsoundVisual.Theming;
using CsoundVisual.ViewModels;

namespace CsoundVisual.Views;

/// <summary>
/// «Configuración» / «Settings» window (gear button in the title bar, Ctrl+,). Built in code.
/// Sections: General (language, colour theme), Editor (font size, word wrap), Audio (engine,
/// output, latency, ksmps, diagnostics — moved here from the side panel), Sync (Ableton Link,
/// <see cref="LinkStatusView.SettingsCard"/>), VST3 plug-ins (the black list,
/// <see cref="PluginManagerSection"/>), Recordings, How it works (a diagram of the app around
/// Csound, as a reminder) and About (versions, folders and the GPL notice).
/// </summary>
/// <remarks>
/// <para>
/// Every change applies immediately and is saved by <see cref="MainViewModel"/>. When the
/// language changes, the window rebuilds its content so its own texts switch too.
/// </para>
/// <para>
/// The page is one vertical <see cref="StackPanel"/> of "cards" (<see cref="Card"/>, style class
/// <c>card</c> from <c>Styles/Theme.axaml</c>) inside a <see cref="ScrollViewer"/>. Controls are
/// bound to view-model properties with bindings created in code
/// (<c>new Binding(name) { Source = vm, Mode = TwoWay }</c>): the window has no DataContext of
/// its own, so each binding names its source explicitly. The view-model setters
/// (<c>Language</c>, <c>SelectedThemePreset</c>, <c>EditorFontSize</c>, <c>EditorWordWrap</c>)
/// apply the change and persist it to <c>settings.json</c>.
/// </para>
/// </remarks>
public sealed class SettingsWindow : Window
{
    /// <summary>The application view model; source of every binding in the window.</summary>
    readonly MainViewModel vm;

    /// <summary>Creates the window for <paramref name="viewModel"/>.</summary>
    /// <param name="viewModel">The main view model.</param>
    public SettingsWindow(MainViewModel viewModel)
    {
        vm = viewModel;
        Width = 640;
        Height = 720;
        MinWidth = 480;
        MinHeight = 400;
        // Same icon as the main window (taskbar / Alt+Tab), taken from the desktop lifetime.
        Icon = (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Icon;
        Background = AppTheme.Brush("Bg");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        // Loc.Changed is static: unsubscribe on close so the window can be collected.
        Loc.Changed += Rebuild;
        Closed += (_, _) => Loc.Changed -= Rebuild;
        Rebuild();
    }

    /// <summary>
    /// Builds (or rebuilds after a language change) the whole page. The scroll position is not
    /// preserved across rebuilds. Theme changes do not need a rebuild: <see cref="AppTheme"/>
    /// brushes are recoloured in place.
    /// </summary>
    void Rebuild()
    {
        Title = "Csound Visual · " + Loc.S("Configuración", "Settings");
        var page = new StackPanel { Spacing = 14, Margin = new Thickness(20, 18, 20, 24) };
        page.Children.Add(new TextBlock { Text = Loc.S("Configuración", "Settings"), FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = AppTheme.Brush("AccentHi") });
        page.Children.Add(Dim(Loc.S("Los cambios se aplican al instante y se guardan.", "Changes apply instantly and are saved.")));
        page.Children.Add(General());
        page.Children.Add(EditorSection());
        page.Children.Add(AudioSection());
        page.Children.Add(LinkStatusView.SettingsCard(vm)); // Sync: Ableton Link
        page.Children.Add(PluginManagerSection.Build(vm)); // VST3 plug-in manager (black list)
        page.Children.Add(Recordings());
        page.Children.Add(HowItWorks());
        page.Children.Add(About());
        var scroller = new ScrollViewer { Content = page };
        Content = scroller;
        // Test hook (screenshots without input): scroll to the end once laid out.
        if (Environment.GetEnvironmentVariable("CSV_SETTINGS_SCROLL_END") == "1")
            Avalonia.Threading.Dispatcher.UIThread.Post(() => scroller.ScrollToEnd(), Avalonia.Threading.DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------------------------------

    /// <summary>GENERAL card: interface language and colour-theme preset.</summary>
    /// <returns>The card.</returns>
    Control General()
    {
        // Language names are shown in their own language on purpose ("English", "Español"),
        // so a user who picked the wrong one can still find theirs.
        var language = new ComboBox
        {
            ItemsSource = new[] { UiLanguage.Spanish, UiLanguage.English },
            ItemTemplate = new FuncDataTemplate<UiLanguage>((lang, _) => new TextBlock { Text = lang == UiLanguage.English ? "English" : "Español" }),
            MinWidth = 220,
        };
        language.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(MainViewModel.Language)) { Source = vm, Mode = BindingMode.TwoWay });
        ToolTip.SetTip(language, Loc.S("Idioma de menús, botones, ayudas y mensajes. Los mensajes que ya están en la consola no cambian.",
                                       "Language of menus, buttons, help and messages. Messages already in the console keep their language."));

        // Theme presets: each item shows its colour swatches as small dots and its localised
        // DisplayName (the stored preset name is never translated).
        var theme = new ComboBox
        {
            ItemsSource = AppTheme.Presets,
            ItemTemplate = new FuncDataTemplate<ThemePreset>((preset, _) =>
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
                foreach (var brush in preset?.Swatches ?? [])
                    dots.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = brush, BorderBrush = new SolidColorBrush(Color.Parse("#40FFFFFF")), BorderThickness = new Thickness(1) });
                row.Children.Add(dots);
                row.Children.Add(new TextBlock { Text = preset?.DisplayName, VerticalAlignment = VerticalAlignment.Center });
                return row;
            }),
            MinWidth = 260,
        };
        theme.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedThemePreset)) { Source = vm, Mode = BindingMode.TwoWay });
        ToolTip.SetTip(theme, Loc.S("Colores de toda la app: ventanas, editor, medidores y 3D.", "Colours of the whole app: windows, editor, meters and 3D."));

        return Card(Loc.S("GENERAL", "GENERAL"),
            Row(Loc.S("Idioma", "Language"), language),
            Row(Loc.S("Tema de colores", "Colour theme"), theme));
    }

    /// <summary>EDITOR card: font size slider (whole points, with a live "N pt" label) and word wrap.</summary>
    /// <returns>The card.</returns>
    Control EditorSection()
    {
        var size = new Slider { Minimum = MainViewModel.MinEditorFontSize, Maximum = MainViewModel.MaxEditorFontSize, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 220 };
        size.Bind(RangeBase.ValueProperty, new Binding(nameof(MainViewModel.EditorFontSize)) { Source = vm, Mode = BindingMode.TwoWay });
        var sizeText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 40, Foreground = AppTheme.Brush("AccentHi") };
        sizeText.Bind(TextBlock.TextProperty, new Binding(nameof(MainViewModel.EditorFontSize)) { Source = vm, StringFormat = "{0:0} pt" });
        ToolTip.SetTip(size, Loc.S("Tamaño de la letra del código (también con Ctrl+rueda en el editor).", "Font size of the code (Ctrl+wheel in the editor also works)."));

        var wrap = new CheckBox { Content = Loc.S("Ajustar líneas largas al ancho del editor", "Wrap long lines to the editor width") };
        wrap.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.EditorWordWrap)) { Source = vm, Mode = BindingMode.TwoWay });

        return Card(Loc.S("EDITOR", "EDITOR"),
            Row(Loc.S("Tamaño de letra", "Font size"), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { size, sizeText } }),
            wrap);
    }

    /// <summary>AUDIO card: hosts the AXAML <see cref="AudioSettingsView"/> with the view model as its DataContext.</summary>
    /// <returns>The card.</returns>
    Control AudioSection() =>
        Card(Loc.S("AUDIO", "AUDIO"),
            Dim(Loc.S("Dónde y cómo suena Csound. Los cambios se aplican la próxima vez que presiones Tocar.",
                      "Where and how Csound sounds. Changes apply the next time you press Play.")),
            new AudioSettingsView { DataContext = vm });

    /// <summary>RECORDINGS card: where the Record button writes its WAV files, plus "Open folder".</summary>
    /// <returns>The card.</returns>
    Control Recordings()
    {
        var open = Tool(Loc.S("Abrir carpeta", "Open folder"), vm.OpenRecordingsFolderCommand);
        return Card(Loc.S("GRABACIONES", "RECORDINGS"),
            Dim(Loc.S("Lo que grabas con el botón Grabar se guarda en:", "What you record with the Record button is saved in:")),
            Mono(MainViewModel.RecordingsFolder),
            open);
    }

    /// <summary>
    /// ABOUT card: app version (assembly version, 3 parts), Csound version and installation
    /// folder (from <see cref="CsoundLocator"/>), settings folder and the GPL notice.
    /// </summary>
    /// <returns>The card.</returns>
    Control About()
    {
        string app = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        string csound = CsoundLocator.Home is null ? Loc.S("no encontrado", "not found") : CsoundLocator.VersionText;
        var openSettings = new Button { Content = Loc.S("Abrir carpeta de configuración", "Open settings folder"), Classes = { "tool" } };
        openSettings.Click += (_, _) => Explore(MainViewModel.SettingsFolder);
        return Card(Loc.S("ACERCA DE", "ABOUT"),
            Row(Loc.S("Versión", "Version"), Mono("Csound Visual " + app)),
            Row("Csound", Mono(csound)),
            Row(Loc.S("Instalación de Csound", "Csound installation"), Mono(CsoundLocator.Home ?? "—")),
            Row(Loc.S("Configuración", "Settings file"), Mono(MainViewModel.SettingsFolder)),
            openSettings,
            // GPL «Appropriate Legal Notices» (section 0 of the GPL-3.0).
            Dim(Loc.S(
                "Csound Visual es software libre bajo la Licencia Pública General de GNU, versión 3 o posterior (archivo LICENSE). Se entrega SIN NINGUNA GARANTÍA. Usa Csound (LGPL-2.1), el VST 3 SDK de Steinberg (MIT), Ableton Link (GPL-2.0+), Avalonia (MIT) y otros componentes: ver THIRD-PARTY-NOTICES.md. VST es una marca registrada de Steinberg Media Technologies GmbH.",
                "Csound Visual is free software under the GNU General Public License, version 3 or later (file LICENSE). It comes with ABSOLUTELY NO WARRANTY. It uses Csound (LGPL-2.1), the Steinberg VST 3 SDK (MIT), Ableton Link (GPL-2.0+), Avalonia (MIT) and other components: see THIRD-PARTY-NOTICES.md. VST is a registered trademark of Steinberg Media Technologies GmbH.")));
    }

    /// <summary>
    /// «Cómo funciona»: a diagram of the pieces around Csound — the app, the Csound library with
    /// its plugins and data, the app's own WASAPI output, MIDI — plus where Csound is looked for
    /// and the fact that its folder is portable. Drawn with ordinary controls so it follows the
    /// theme and the language.
    /// </summary>
    /// <returns>The card.</returns>
    /// <remarks>The search order listed here must match <see cref="CsoundLocator"/>.</remarks>
    Control HowItWorks()
    {
        var diagram = new StackPanel { Spacing = 0, Margin = new Thickness(0, 4, 0, 0) };

        diagram.Children.Add(Box("Csound Visual",
            Loc.S("C# · .NET 10 · Avalonia — este programa", "C# · .NET 10 · Avalonia — this program"),
            Loc.S("Editor · sliders · 3D · medidores · grabación · temas · idiomas", "Editor · sliders · 3D · meters · recording · themes · languages"),
            strong: true));
        diagram.Children.Add(Arrows(
            Loc.S("código .csd, valores de canales, notas MIDI", ".csd code, channel values, MIDI notes"),
            Loc.S("audio calculado, valores en vivo, tablas", "computed audio, live values, tables")));
        diagram.Children.Add(Box("csound7_64.dll",
            Loc.S("el motor de Csound 7, cargado DENTRO de la app (no se ejecuta csound.exe)", "the Csound 7 engine, loaded INSIDE the app (csound.exe is not run)"),
            Loc.S(@"plugins64\  opcodes y módulos · samples\  HRTF, soundfonts", @"plugins64\  opcodes and modules · samples\  HRTF, soundfonts"),
            strong: false));
        diagram.Children.Add(Arrows(Loc.S("muestras de audio", "audio samples"), null));
        diagram.Children.Add(Box(Loc.S("Motor WASAPI propio", "Own WASAPI engine"),
            Loc.S("compartido (convive con otras apps) o exclusivo (latencia mínima)", "shared (coexists with other apps) or exclusive (lowest latency)"),
            null, strong: false));
        diagram.Children.Add(Arrows(null, null));
        diagram.Children.Add(Box(Loc.S("Tarjeta de sonido", "Sound card"), Loc.S("audífonos, parlantes, UR22…", "headphones, speakers, UR22…"), null, strong: false));

        var midi = Box("MIDI",
            Loc.S("Windows MIDI Services (MIDI 2.0) o WinMM (MIDI 1.0) → MidiHub → Csound", "Windows MIDI Services (MIDI 2.0) or WinMM (MIDI 1.0) → MidiHub → Csound"),
            Loc.S("el teclado en pantalla entra por el mismo camino", "the on-screen keyboard takes the same path"), strong: false);
        midi.Margin = new Thickness(0, 12, 0, 0);
        diagram.Children.Add(midi);

        return Card(Loc.S("CÓMO FUNCIONA", "HOW IT WORKS"),
            diagram,
            Dim(Loc.S("Dónde busca Csound al arrancar, en este orden:", "Where Csound is looked for at start-up, in this order:")),
            Mono(Loc.S(@"1. Variable de entorno CSOUND_VISUAL_HOME
2. Una carpeta Csound7 junto al .exe (paquete portátil)
3. Una carpeta @Source\Csound7 en alguna carpeta superior al .exe
4. C:\Program Files\Csound7 (instalador oficial)",
                       @"1. Environment variable CSOUND_VISUAL_HOME
2. A Csound7 folder next to the .exe (portable package)
3. An @Source\Csound7 folder in any folder above the .exe
4. C:\Program Files\Csound7 (official installer)")),
            Dim(Loc.S("La carpeta de Csound es portátil: no necesita instalador, solo usa DLLs de Windows. Se puede copiar a otro PC junto con la app.",
                      "The Csound folder is portable: it needs no installer and only uses Windows DLLs. It can be copied to another PC together with the app.")),
            Row(Loc.S("En uso ahora", "In use now"), Mono(CsoundLocator.Home ?? "—")));
    }

    /// <summary>One block of the diagram.</summary>
    /// <param name="title">Bold first line.</param>
    /// <param name="line1">Description line.</param>
    /// <param name="line2">Optional dim detail line (null = none).</param>
    /// <param name="strong">True for the highlighted block (accent background, used for the app itself).</param>
    /// <returns>A rounded, themed border.</returns>
    static Border Box(string title, string line1, string? line2, bool strong)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14, Foreground = AppTheme.Brush(strong ? "OnAccent" : "AccentHi") });
        stack.Children.Add(new TextBlock { Text = line1, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Brush(strong ? "OnAccent" : "Text") });
        if (line2 is not null)
            stack.Children.Add(new TextBlock { Text = line2, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Brush(strong ? "OnAccent" : "TextDim"), Opacity = strong ? 0.85 : 1 });
        return new Border
        {
            Child = stack,
            Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(8),
            Background = AppTheme.Brush(strong ? "Accent" : "Surface2"),
            BorderBrush = AppTheme.Brush(strong ? "AccentHi" : "Line"),
            BorderThickness = new Thickness(1),
        };
    }

    /// <summary>The connector between two blocks: ▼ what goes down (left) and ▲ what comes back (right).</summary>
    /// <param name="down">Label of the downward flow (null = bare arrow).</param>
    /// <param name="up">Label of the upward flow (null = no return arrow).</param>
    /// <returns>A two-column grid.</returns>
    static Control Arrows(string? down, string? up)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(18, 2, 18, 2) };
        grid.Children.Add(new TextBlock { Text = "▼  " + (down ?? ""), FontSize = 11.5, Foreground = AppTheme.Brush("Accent"), TextWrapping = TextWrapping.Wrap });
        if (up is not null)
        {
            var back = new TextBlock { Text = "▲  " + up, FontSize = 11.5, Foreground = AppTheme.Brush("Good"), TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(back, 1);
            grid.Children.Add(back);
        }
        return grid;
    }

    // ---- small builders shared by the sections ----------------------------------------------

    /// <summary>A settings card: upper-case section title followed by <paramref name="children"/>, spaced 10 DIP.</summary>
    /// <param name="title">Section title (style class <c>section</c>).</param>
    /// <param name="children">Card content, top to bottom.</param>
    /// <returns>A <see cref="Border"/> with the <c>card</c> style class.</returns>
    static Border Card(string title, params Control[] children)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock { Text = title, Classes = { "section" } });
        foreach (var c in children) stack.Children.Add(c);
        return new Border { Classes = { "card" }, Child = stack };
    }

    /// <summary>A label/control row with a fixed 170 DIP label column, so rows of a card line up.</summary>
    /// <param name="label">Text on the left.</param>
    /// <param name="control">Control on the right (left-aligned in its column).</param>
    /// <returns>The row grid.</returns>
    static Control Row(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>A wrapped explanatory paragraph in the dim text style.</summary>
    static TextBlock Dim(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Classes = { "dim" } };

    /// <summary>A wrapped monospaced value (paths, versions) in the accent colour.</summary>
    static TextBlock Mono(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
        Foreground = AppTheme.Brush("AccentHi"),
    };

    /// <summary>A themed ("tool") button bound to a view-model command.</summary>
    static Button Tool(string text, System.Windows.Input.ICommand command) =>
        new() { Content = text, Command = command, Classes = { "tool" } };

    /// <summary>Opens <paramref name="folder"/> in Windows Explorer, creating it first if missing.</summary>
    /// <param name="folder">Absolute folder path (quoted on the command line, so spaces are fine).</param>
    static void Explore(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening Explorer is a convenience; failing silently is fine.
        }
    }
}
