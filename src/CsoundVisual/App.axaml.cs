using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CsoundVisual.Interop;
using CsoundVisual.Theming;
using CsoundVisual.ViewModels;
using CsoundVisual.Views;

namespace CsoundVisual;

/// <summary>
/// Application entry object: loads the styles, applies the saved colour preset, locates Csound and
/// creates the main window.
/// A file passed on the command line (e.g. "Open with…") is opened in a tab.
/// </summary>
/// <remarks>
/// <para>Created by Avalonia from <c>Program.BuildAvaloniaApp()</c> when <c>Program.Main</c> starts the
/// classic desktop lifetime (only after the console self-test switches such as
/// <c>--audio-test</c> or <c>--link-test</c> have been ruled out; those never create an
/// <see cref="App"/>). The XAML half (<c>App.axaml</c>) declares the style stack: Fluent theme,
/// AvaloniaEdit styles and <c>Styles/Theme.axaml</c>.</para>
/// <para>Start-up order in <see cref="OnFrameworkInitializationCompleted"/> matters:
/// language → Csound location → view model → colour preset → main window → command-line file.
/// See the inline comments there for why.</para>
/// <para>If Csound 7 cannot be found the app still starts; the view model receives
/// <c>false</c> and disables everything that needs the engine.</para>
/// </remarks>
public partial class App : Application
{
    /// <summary>
    /// Loads <c>App.axaml</c> (compiled XAML): registers the Fluent theme with its dark palette and
    /// the application styles. Called by Avalonia before
    /// <see cref="OnFrameworkInitializationCompleted"/>; no window exists yet.
    /// </summary>
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Called by Avalonia once the platform (windowing, rendering, styles) is ready. Builds the
    /// main view model and window for the classic desktop lifetime; other lifetimes (none are used
    /// by this app) get no window.
    /// </summary>
    /// <remarks>
    /// The command-line file is opened only when it is the first argument and exists on disk;
    /// anything else on the command line is ignored here.
    /// </remarks>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Language first: the view model already writes console messages and loads the demo
            // (whose comments depend on the language) while it is constructed. The settings are
            // read here only for the language; the view model loads its own copy
            // (%APPDATA%\CsoundVisual\settings.json).
            var saved = AppSettings.Load();
            CsoundVisual.Localization.Loc.SetLanguage(CsoundVisual.Localization.Loc.Parse(saved.Language));
            // Finds csound7_64.dll (env var, Csound7 next to the exe, @Source\Csound7, Program Files)
            // and registers the native library resolver; must run before any Csound call.
            bool found = CsoundLocator.Initialize();
            var vm = new MainViewModel(found);
            // Colour preset before the window exists, so it never flashes in the wrong colours.
            // Brushes handed out during the view model's construction use the first preset and
            // are recoloured in place by Apply.
            AppTheme.Apply(vm.Settings.ThemePreset);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            // Safety net: an unexpected error in any button, menu or timer of the UI thread is
            // reported in the console (with where it happened) instead of closing the app and
            // losing unsaved work. Errors are still bugs to fix; this only keeps the user safe.
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                var ex = e.Exception;
                // The first stack line inside the app's own code says where it happened.
                string where = ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("CsoundVisual."))?.Trim() ?? "";
                vm.ConsoleLines.Add(new Engine.ConsoleLine(CsoundVisual.Localization.Loc.S(
                    $"✗ Error interno (el programa sigue abierto; por favor repórtalo): {ex.GetType().Name}: {ex.Message} {where}",
                    $"✗ Internal error (the app stays open; please report it): {ex.GetType().Name}: {ex.Message} {where}"), Engine.MessageKind.Error));
            };
            // "Open with…" / double-clicked .csd: Windows passes the path as the first argument.
            if (desktop.Args is [var file, ..] && File.Exists(file))
                vm.OpenPath(file);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
