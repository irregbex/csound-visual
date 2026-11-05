using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CsoundVisual.Integration;
using CsoundVisual.Localization;
using CsoundVisual.Theming;
using CsoundVisual.ViewModels;

namespace CsoundVisual.Views;

/// <summary>
/// Settings › «VST3 plug-ins»: every module found by the scan with an «active» check box, like
/// Cubase's plug-in manager. Modules that failed the scan or crashed the app are switched off
/// automatically and show why; switched-off modules are not offered in the chain window and are
/// skipped when the saved chain is restored.
/// </summary>
/// <remarks>
/// <para>
/// <b>Data sources.</b> The module list is the scan cache (<see cref="VstScanner.LoadCache"/>,
/// <c>%LOCALAPPDATA%\CsoundVisual\vst3-cache.json</c>, written by «Scan» in the
/// <see cref="PluginChainWindow"/>). The on/off state is the black list kept by
/// <see cref="VstPluginManager"/> in <c>AppSettings.PluginBlacklist</c> (lower-case path →
/// reason with a <c>user:</c>, <c>scan:</c> or <c>crash:</c> prefix). A module is shown as off
/// when it is black-listed <i>or</i> its cache entry carries a scan error; the latter is shown
/// with the <see cref="VstPluginManager.ScanPrefix"/> reason even if it is not in the list.
/// </para>
/// <para>
/// <b>Built in code, no view model.</b> A static builder returning a settings card; local
/// functions capture the controls. Each check box writes straight to
/// <see cref="VstPluginManager"/> (which saves the settings). <see cref="SettingsWindow"/>
/// calls <see cref="Build"/> again on every rebuild (language change).
/// </para>
/// </remarks>
public static class PluginManagerSection
{
    /// <summary>Builds the card for <paramref name="vm"/>.</summary>
    /// <param name="vm">The main view model (its settings, and the command that opens the chain window).</param>
    /// <returns>A <c>card</c>-styled <see cref="Border"/>: intro text, search + filter, scrollable list, summary and buttons.</returns>
    public static Control Build(MainViewModel vm)
    {
        // The manager normally gets its settings in MainViewModel.InitPluginHost; make sure it
        // has them even if the settings window is opened first.
        VstPluginManager.Settings ??= vm.Settings;
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock { Text = Loc.S("PLUGINS VST3", "VST3 PLUG-INS"), Classes = { "section" } });
        stack.Children.Add(Dim(Loc.S(
            "Todos los plugins encontrados al examinar. Los desmarcados se ignoran: no aparecen en la cadena y no se cargan al iniciar. Los que fallan al examinarlos o cierran la app se desmarcan solos (como en Cubase); puedes volver a activarlos para probarlos de nuevo.",
            "Every plug-in found by the scan. Unchecked ones are ignored: they are not offered for the chain and are not loaded at start. Those that fail the scan or crash the app are unchecked automatically (like in Cubase); you can switch them back on to try them again.")));

        var search = new TextBox { PlaceholderText = Loc.S("Buscar…", "Search…"), Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        var onlyOff = new CheckBox { Content = Loc.S("Solo los desactivados", "Only switched-off ones") };
        var summary = new TextBlock { FontSize = 12, Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center };
        var list = new StackPanel { Spacing = 2 };
        // The list scrolls inside the card (max 380 DIP) so ~190 plug-ins do not make the
        // settings page endless; vertical scroll only, long names are trimmed.
        var scroller = new ScrollViewer { Content = list, MaxHeight = 380, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        var enableAll = new Button { Content = Loc.S("Activar todos", "Switch all on"), Classes = { "tool" } };
        ToolTip.SetTip(enableAll, Loc.S("Vuelve a activar todos los plugins (los que fallaron se examinan de nuevo en el próximo «Examinar»).",
                                        "Switches every plug-in back on (those that failed are inspected again at the next «Scan»)."));
        var openChain = new Button { Content = Loc.S("Abrir cadena de plugins…", "Open plug-in chain…"), Classes = { "tool" } };
        ToolTip.SetTip(openChain, Loc.S("Ventana de la cadena (Ctrl+Shift+V): ahí está «Examinar».", "Chain window (Ctrl+Shift+V): «Scan» is there."));
        openChain.Click += (_, _) => vm.OpenPluginChainCommand.Execute(null);

        // The scan cache, read once per card build (bug fix: it used to be re-read from disk on
        // every search keystroke) and reloaded when the plug-in manager reports a change (a scan in
        // the chain window rewrites the cache and syncs the black list, which raises Changed).
        var modules = VstScanner.LoadCache();

        // Off state of one module: its black-list reason, or the cached scan error when it is not listed.
        static string? ReasonFor(VstModuleEntry m) =>
            VstPluginManager.ReasonOf(m.Path) ?? (m.Error is not null ? VstPluginManager.ScanPrefix + m.Error : null);

        // Recomputes only the "N modules · N active · N switched off" line. The "off" count covers
        // all modules, not only the ones that pass the filters. Called by Refresh and, so the count
        // follows the check boxes (bug fix), after every toggle and every manager change.
        void UpdateSummary()
        {
            int off = modules.Count(m => ReasonFor(m) is not null);
            summary.Text = modules.Count == 0
                ? Loc.S("Todavía no se han examinado plugins: abre la cadena y presiona «Examinar».", "No plug-ins scanned yet: open the chain and press «Scan».")
                : Loc.S($"{modules.Count} módulos · {modules.Count - off} activos · {off} desactivados", $"{modules.Count} modules · {modules.Count - off} active · {off} switched off");
        }

        // Rebuilds the list and the summary from the cached modules and the black list, applying
        // the search text (matches name, vendor or path) and the «only switched-off» filter.
        void Refresh()
        {
            string q = search.Text?.Trim() ?? "";
            list.Children.Clear();
            // Switched-off modules first, so the problem ones are visible at a glance.
            foreach (var m in modules.OrderBy(m => VstPluginManager.IsBlocked(m.Path) || m.Error is not null ? 0 : 1).ThenBy(m => Title(m), StringComparer.OrdinalIgnoreCase))
            {
                string? reason = ReasonFor(m);
                if (onlyOff.IsChecked == true && reason is null) continue;
                string title = Title(m);
                string vendor = m.Classes.FirstOrDefault()?.Vendor ?? "";
                if (q.Length > 0 && !(title + " " + vendor + " " + m.Path).Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                list.Children.Add(Row(m, title, vendor, reason));
            }
            UpdateSummary();
        }

        // One module: check box (tooltip = path) + name and "vendor · kind — reason" (in the warning
        // colour when off). Toggling writes to the black list immediately with a "user:" reason;
        // the list is not rebuilt (so the row does not jump), but the summary count is updated.
        Control Row(VstModuleEntry m, string title, string vendor, string? reason)
        {
            var box = new CheckBox { IsChecked = reason is null, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            ToolTip.SetTip(box, m.Path);
            box.IsCheckedChanged += (_, _) =>
            {
                if (box.IsChecked == true) VstPluginManager.Unblock(m.Path);
                else VstPluginManager.Block(m.Path, VstPluginManager.UserPrefix);
                UpdateSummary(); // keep "N active · N switched off" in step with the boxes
            };
            // Kind from the module's audio-processor classes: all instruments, mixed, or all effects.
            string kind = m.Classes.Count == 0 ? "" : m.Classes.All(c => c.IsInstrument) ? Loc.S("instrumento", "instrument")
                        : m.Classes.Any(c => c.IsInstrument) ? Loc.S("instrumento + efecto", "instrument + effect") : Loc.S("efecto", "effect");
            var name = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var detail = new TextBlock
            {
                Text = string.Join(" · ", new[] { vendor, kind }.Where(s => s.Length > 0)) + (reason is null ? "" : "   —   " + VstPluginManager.Describe(reason)),
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = reason is null ? AppTheme.Brush("TextDim") : AppTheme.Brush("Warn"),
            };
            ToolTip.SetTip(detail, (reason is null ? "" : VstPluginManager.Describe(reason) + "\n") + m.Path);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 2) };
            grid.Children.Add(box);
            var texts = new StackPanel { Spacing = 0, Children = { name, detail } };
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);
            return grid;
        }

        // «Switch all on» clears every black-list entry. Modules whose cached scan failed still
        // show as off until a new scan succeeds (their reason comes from the cache, not the list).
        enableAll.Click += (_, _) =>
        {
            foreach (var m in modules)
                if (VstPluginManager.IsBlocked(m.Path)) VstPluginManager.Unblock(m.Path);
            modules = VstScanner.LoadCache(); // Unblock may have dropped failed modules from the cache
            Refresh();
        };
        search.TextChanged += (_, _) => Refresh();
        onlyOff.IsCheckedChanged += (_, _) => Refresh();

        stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { search, onlyOff } });
        stack.Children.Add(scroller);
        stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { summary, enableAll, openChain } });
        stack.Children.Add(Dim(Loc.S("Los cambios valen para la lista de la cadena y el próximo inicio; un plugin que ya está en la cadena sigue sonando hasta que lo quites.",
                                     "Changes apply to the chain's list and the next start; a plug-in already in the chain keeps playing until you remove it.")));
        Refresh();
        var card = new Border { Classes = { "card" }, Child = stack };
        // Follow manager changes (crash recovery, a scan in the chain window, a toggle here) while
        // the card is on screen: reload the cache and update the summary only, so ticking a box
        // does not reorder the rows under the mouse. The handler is attached/detached with the
        // visual tree because the event is static and the card is rebuilt on every language change.
        void OnManagerChanged()
        {
            modules = VstScanner.LoadCache();
            UpdateSummary();
        }
        card.AttachedToVisualTree += (_, _) => VstPluginManager.Changed += OnManagerChanged;
        card.DetachedFromVisualTree += (_, _) => VstPluginManager.Changed -= OnManagerChanged;
        return card;
    }

    /// <summary>
    /// Display name of a module: the distinct names of its classes joined with commas (one
    /// .vst3 bundle can contain several plug-ins), or the bundle file name when the scan found none.
    /// </summary>
    /// <param name="m">Scan-cache entry.</param>
    /// <returns>The title used for sorting, searching and display.</returns>
    static string Title(VstModuleEntry m)
    {
        var names = m.Classes.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count > 0 ? string.Join(", ", names) : Path.GetFileNameWithoutExtension(m.Path.TrimEnd('\\', '/'));
    }

    /// <summary>A wrapped explanatory paragraph in the dim text style.</summary>
    static TextBlock Dim(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Classes = { "dim" } };
}
