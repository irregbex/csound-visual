using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsoundVisual.Integration;
using CsoundVisual.Localization;
using CsoundVisual.Theming;
using CsoundVisual.ViewModels;

namespace CsoundVisual.Views;

/// <summary>
/// «Plugins VST3» window (Ctrl+Shift+V): the VST3 plug-ins installed on the PC (scanned on demand
/// in the background, cached) and the insert chain that processes Csound's output — add, reorder,
/// bypass, remove, open each plug-in's own editor window, or edit its parameters with sliders
/// when it has no editor. Built in code like the other tool windows.
/// </summary>
/// <remarks>
/// The chain itself lives in <see cref="MainViewModel.PluginChain"/>: closing this window changes
/// nothing (editor windows stay open, they are owned by the main window). Every change is saved
/// to the settings right away.
/// <para>
/// <b>Layout.</b> A header (title, explanation, warnings when the engine is not the app's own
/// WASAPI engine or the native bridge <c>CsoundVisualVstHost.dll</c> is missing) above two
/// columns: INSTALLED (fixed 380 DIP: search, kind filter, list, Scan / Add) and the CHAIN
/// (slot rows with Bypass / Edit / ▲ / ▼ / ✕, then the PARAMETERS of the selected slot as
/// generic 0…1 sliders).
/// </para>
/// <para>
/// <b>Threading.</b> Scanning runs on a worker task (<see cref="VstScanner.Scan"/> opens each
/// module in a separate process) and reports progress through <see cref="Progress{T}"/>, which
/// posts back to the UI thread. Loading a plug-in and opening its editor happen on the UI thread
/// (the native host requires it). <see cref="VstChain.Changed"/> is raised on the UI thread but
/// often from inside a row's own button handler (▲ / ▼ / ✕), so the redraw is posted rather than
/// run inline, to avoid tearing down the row that is still handling the click. A 1 s <see cref="DispatcherTimer"/> notices plug-ins
/// that crashed on the audio thread (the chain disables them) and redraws their row, and moves the
/// visible parameter sliders to values changed in the plug-in's own editor or by automation.
/// </para>
/// <para>
/// <b>Black list.</b> Modules switched off in Settings › VST3 plug-ins
/// (<see cref="VstPluginManager"/>) are not offered here; modules whose scan failed are listed
/// in the status tooltip.
/// </para>
/// </remarks>
public sealed class PluginChainWindow : Window
{
    /// <summary>The application view model (engine kind, commands).</summary>
    readonly MainViewModel vm;
    /// <summary>The insert chain owned by the view model (<see cref="MainViewModel.PluginChain"/>).</summary>
    readonly VstChain chain;
    /// <summary>Every plug-in class offered in the INSTALLED list (from non-blocked modules).</summary>
    readonly List<VstPluginInfo> installed = [];
    /// <summary>Modules whose last scan reported an error (shown in the status tooltip).</summary>
    readonly List<VstModuleEntry> failedModules = [];
    /// <summary>1 s timer that calls <see cref="RefreshStates"/>.</summary>
    readonly DispatcherTimer timer;
    /// <summary>Non-null while a scan runs; cancelled when the window closes.</summary>
    CancellationTokenSource? scanCancel;

    // Controls rebuilt on language change (assigned by Rebuild → InstalledPane / ChainPane;
    // the refresh methods check for null because they can run before the first build).
    /// <summary>Search box of the INSTALLED list (all words must match name, vendor or category).</summary>
    TextBox search = null!;
    /// <summary>Kind filter: 0 = all, 1 = effects, 2 = instruments.</summary>
    ComboBox kind = null!;
    /// <summary>The INSTALLED list.</summary>
    ListBox pluginList = null!;
    /// <summary>Status line under the list: counts, scan progress, load results.</summary>
    TextBlock scanStatus = null!;
    /// <summary>Rows of the chain, one per <see cref="VstSlot"/>.</summary>
    StackPanel chainPanel = null!;
    /// <summary>Parameter sliders of the selected slot.</summary>
    StackPanel paramPanel = null!;
    /// <summary>Name of the slot whose parameters are shown.</summary>
    TextBlock paramTitle = null!;
    /// <summary>Filter for the parameter list (by title).</summary>
    TextBox paramSearch = null!;
    /// <summary>«Scan» button (disabled while scanning; right-click = full rescan).</summary>
    Button scanButton = null!;
    /// <summary>Slot whose parameters are shown (highlighted row); defaults to the first slot.</summary>
    VstSlot? selected;
    /// <summary>
    /// Number of crashed slots when the chain rows were last drawn; <see cref="RefreshStates"/>
    /// redraws whenever the current count differs, so every new crash shows up (not only the first).
    /// </summary>
    int shownCrashes;
    /// <summary>
    /// The parameter rows on screen: slot, parameter id, slider and value text, so
    /// <see cref="RefreshStates"/> can make the sliders follow changes made in the plug-in's own
    /// editor or by automation. Rebuilt by <see cref="RefreshParams"/>.
    /// </summary>
    readonly List<(VstSlot Slot, uint Id, Slider Slider, TextBlock Text)> paramRows = [];
    /// <summary>True while <see cref="RefreshStates"/> moves a slider to the plug-in's value, so its
    /// <c>ValueChanged</c> handler does not send the same value back to the plug-in.</summary>
    bool syncingParams;

    /// <summary>Creates the window for <paramref name="viewModel"/>.</summary>
    /// <param name="viewModel">The main view model that owns the chain.</param>
    public PluginChainWindow(MainViewModel viewModel)
    {
        vm = viewModel;
        chain = vm.PluginChain;
        Width = 1060;
        Height = 720;
        MinWidth = 760;
        MinHeight = 460;
        Background = AppTheme.Brush("Bg");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        // Start from the cached scan (instant); «Scan» refreshes it on demand.
        foreach (var m in VstScanner.LoadCache())
            Accept(m);
        Loc.Changed += Rebuild;
        chain.Changed += OnChainChanged;
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshStates());
        timer.Start();
        Closed += (_, _) =>
        {
            Loc.Changed -= Rebuild;
            chain.Changed -= OnChainChanged;
            timer.Stop();
            scanCancel?.Cancel();
        };
        Rebuild();
    }

    /// <summary>
    /// Sorts one scanned module into the window's lists: failed modules are remembered for the
    /// status tooltip, black-listed ones are counted and skipped, the rest contribute their
    /// plug-in classes to <see cref="installed"/>.
    /// </summary>
    /// <param name="m">A module from the cache or from a fresh scan.</param>
    void Accept(VstModuleEntry m)
    {
        if (m.Error is not null) failedModules.Add(m);
        if (VstPluginManager.IsBlocked(m.Path))
        {
            if (m.Error is null) disabledModules++;
            return; // black-listed: not offered (Settings › VST3 plug-ins)
        }
        installed.AddRange(m.Classes);
    }

    /// <summary>Black-listed modules that scanned fine (failed ones are counted in <see cref="failedModules"/>).</summary>
    int disabledModules;

    /// <summary>
    /// Builds (or rebuilds after a language change) the whole window content and refills the
    /// list and the chain. Search texts and the kind filter are reset by a rebuild.
    /// </summary>
    void Rebuild()
    {
        Title = "Csound Visual · " + Loc.S("Plugins VST3", "VST3 plug-ins");
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(16, 12, 16, 16) };

        var header = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock { Text = Loc.S("Plugins VST3 después de Csound", "VST3 plug-ins after Csound"), FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = AppTheme.Brush("AccentHi") });
        header.Children.Add(Dim(Loc.S(
            "Como los insertos de un canal: la salida de Csound (canales 1-2) pasa por estos plugins, en orden, antes de los medidores, la grabación y la tarjeta. Los instrumentos suman su sonido y tocan el MIDI de la app. Solo con el motor «Windows · WASAPI».",
            "Like the inserts of a mixer channel: Csound's output (channels 1-2) goes through these plug-ins, in order, before the meters, the recorder and the device. Instruments add their sound and play the app's MIDI. Only with the «Windows · WASAPI» engine.")));
        if (!vm.IsHostBackend)
            header.Children.Add(new TextBlock
            {
                Text = Loc.S("⚠ El motor de audio elegido no es «Windows · WASAPI»: la cadena no se va a escuchar (Configuración › Audio).",
                             "⚠ The selected audio engine is not «Windows · WASAPI»: the chain will not be heard (Settings › Audio)."),
                Foreground = AppTheme.Brush("Warn"), TextWrapping = TextWrapping.Wrap, FontSize = 12,
            });
        if (!VstHostNative.IsAvailable)
            header.Children.Add(new TextBlock
            {
                Text = Loc.S("✗ Falta el puente nativo CsoundVisualVstHost.dll (se compila con tools\\build_vsthost.ps1): el host VST3 está desactivado. ",
                             "✗ The native bridge CsoundVisualVstHost.dll is missing (build it with tools\\build_vsthost.ps1): the VST3 host is disabled. ") + VstHostNative.LoadError,
                Foreground = AppTheme.Brush("Danger"), TextWrapping = TextWrapping.Wrap, FontSize = 12,
            });
        root.Children.Add(header);

        // Two columns: INSTALLED (fixed 380 DIP) | 16 DIP gap | CHAIN + PARAMETERS (rest).
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("380,16,*") };
        Grid.SetRow(columns, 1);
        columns.Children.Add(InstalledPane());
        var right = ChainPane();
        Grid.SetColumn(right, 2);
        columns.Children.Add(right);
        root.Children.Add(columns);
        Content = root;
        RefreshList();
        RefreshChain();
    }

    // ------------------------------------------------------------------------------------------
    // Installed plug-ins

    /// <summary>
    /// Left column: "INSTALLED" caption, search + kind filter, the plug-in list (♪ marks
    /// instruments; second line "vendor · sub-categories"; tooltip = path), Scan / Add buttons
    /// and the status line. Rows: caption, filters, list (star), buttons, status.
    /// </summary>
    /// <returns>The pane.</returns>
    Control InstalledPane()
    {
        search = new TextBox { PlaceholderText = Loc.S("Buscar (nombre, fabricante, tipo)", "Search (name, vendor, type)") };
        search.TextChanged += (_, _) => RefreshList();
        kind = new ComboBox
        {
            ItemsSource = new[] { Loc.S("Todos", "All"), Loc.S("Efectos", "Effects"), Loc.S("Instrumentos", "Instruments") },
            SelectedIndex = 0, Width = 130,
        };
        kind.SelectionChanged += (_, _) => RefreshList();

        pluginList = new ListBox
        {
            ItemTemplate = new FuncDataTemplate<VstPluginInfo>((p, _) =>
            {
                if (p is null) return new TextBlock();
                var row = new StackPanel { Spacing = 1 };
                row.Children.Add(new TextBlock { Text = (p.IsInstrument ? "♪ " : "") + p.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                row.Children.Add(new TextBlock { Text = p.Vendor + " · " + p.SubCategories, FontSize = 11, Foreground = AppTheme.Brush("TextDim"), TextTrimming = TextTrimming.CharacterEllipsis });
                ToolTip.SetTip(row, p.Path);
                return row;
            }),
        };
        pluginList.DoubleTapped += (_, _) => AddSelected();

        var add = Tool(Loc.S("Agregar a la cadena →", "Add to chain →"), Loc.S("Carga el plugin elegido al final de la cadena (o doble clic en la lista).", "Loads the chosen plug-in at the end of the chain (or double-click it in the list)."), AddSelected);
        add.Classes.Add("primary");
        scanButton = Tool(Loc.S("Examinar", "Scan"), Loc.S(
            "Busca plugins nuevos o actualizados en las carpetas VST3 estándar (los demás salen del caché). Clic derecho: examinar todo de nuevo.",
            "Looks for new or updated plug-ins in the standard VST3 folders (the rest come from the cache). Right-click: rescan everything."), () => StartScan(false));
        // Right-click on Scan: full rescan ignoring the cache (a ContextMenu needs a command,
        // so the lambda is wrapped in a toolkit RelayCommand).
        scanButton.ContextMenu = new ContextMenu
        {
            Items = { new MenuItem { Header = Loc.S("Examinar todo de nuevo", "Rescan everything"), Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => StartScan(true)) } },
        };
        scanStatus = new TextBlock { FontSize = 11, Foreground = AppTheme.Brush("TextDim"), TextWrapping = TextWrapping.Wrap };

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto") };
        top.Children.Add(search);
        Grid.SetColumn(kind, 2);
        top.Children.Add(kind);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(scanButton);
        buttons.Children.Add(add);

        var pane = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
        pane.Children.Add(Section(Loc.S("INSTALADOS", "INSTALLED")));
        Grid.SetRow(top, 1);
        top.Margin = new Thickness(0, 6, 0, 6);
        pane.Children.Add(top);
        var listBorder = new Border { Classes = { "card" }, Padding = new Thickness(2), Child = pluginList };
        Grid.SetRow(listBorder, 2);
        pane.Children.Add(listBorder);
        Grid.SetRow(buttons, 3);
        buttons.Margin = new Thickness(0, 8, 0, 4);
        pane.Children.Add(buttons);
        Grid.SetRow(scanStatus, 4);
        pane.Children.Add(scanStatus);
        return pane;
    }

    /// <summary>
    /// Re-filters <see cref="installed"/> into the list (kind filter, then every search word must
    /// appear in <see cref="VstPluginInfo.SearchText"/>, sorted by name) and, unless a scan is
    /// reporting progress, rewrites the status line with the counts.
    /// </summary>
    void RefreshList()
    {
        if (pluginList is null) return;
        string q = search?.Text?.Trim() ?? "";
        int k = kind?.SelectedIndex ?? 0;
        var items = installed
            .Where(p => k == 0 || (k == 1 ? !p.IsInstrument : p.IsInstrument))
            .Where(p => q.Length == 0 || q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => p.SearchText.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        pluginList.ItemsSource = items;
        if (scanStatus is not null && scanCancel is null)
            scanStatus.Text = installed.Count == 0
                ? Loc.S("Todavía no se han examinado los plugins: presiona «Examinar» (cada plugin se abre en un proceso aparte; puede tardar un par de minutos la primera vez).",
                        "Plug-ins have not been scanned yet: press «Scan» (each plug-in is opened in a separate process; the first time can take a couple of minutes).")
                : Loc.S($"{installed.Count} plugins ({items.Count} visibles)", $"{installed.Count} plug-ins ({items.Count} shown)") +
                  (failedModules.Count > 0 ? Loc.S($" · {failedModules.Count} módulos con errores", $" · {failedModules.Count} modules with errors") : "") +
                  (failedModules.Count + disabledModules > 0 ? Loc.S($" · {failedModules.Count + disabledModules} desactivados (Configuración › Plugins VST3)", $" · {failedModules.Count + disabledModules} switched off (Settings › VST3 plug-ins)") : "") +
                  Loc.S(" · no se ofrece CsoundVisual.vst3 (la app ya es Csound)", " · CsoundVisual.vst3 is not offered (the app already is Csound)");
        if (scanStatus is not null && failedModules.Count > 0)
            ToolTip.SetTip(scanStatus, string.Join("\n", failedModules.Take(40).Select(m => Path.GetFileName(m.Path) + ": " + m.Error)));
    }

    /// <summary>
    /// Runs a scan in the background and replaces the lists with its result. Ignored while a
    /// scan is running or when the native host is unavailable.
    /// </summary>
    /// <param name="full">True to rescan every module; false to rescan only new or changed ones (the rest come from the cache).</param>
    /// <remarks>
    /// <c>async void</c> is acceptable here because it is a UI event handler and every exception
    /// is caught. If the window closes mid-scan the token is cancelled and the result is dropped.
    /// After a scan, <see cref="VstPluginManager.SyncWithScan"/> black-lists modules that failed
    /// and releases modules that now pass.
    /// </remarks>
    async void StartScan(bool full)
    {
        if (scanCancel is not null || !VstHostNative.IsAvailable) return;
        scanCancel = new CancellationTokenSource();
        scanButton.IsEnabled = false;
        var token = scanCancel.Token;
        var progress = new Progress<string>(text => { if (scanStatus is not null) scanStatus.Text = Loc.S("Examinando… ", "Scanning… ") + text; });
        try
        {
            var modules = await Task.Run(() => VstScanner.Scan(full, progress, token));
            if (token.IsCancellationRequested) return;
            installed.Clear();
            failedModules.Clear();
            disabledModules = 0;
            VstPluginManager.SyncWithScan(modules);
            foreach (var m in modules) Accept(m);
        }
        catch (Exception ex)
        {
            scanStatus.Text = Loc.S("Error al examinar: ", "Scan error: ") + ex.Message;
        }
        finally
        {
            scanCancel = null;
            scanButton.IsEnabled = true;
        }
        RefreshList();
    }

    /// <summary>
    /// Loads the selected plug-in at the end of the chain (Add button or double-click) and
    /// selects the new slot, or shows why it failed.
    /// </summary>
    void AddSelected()
    {
        if (pluginList.SelectedItem is not VstPluginInfo p) return;
        scanStatus.Text = Loc.S($"Cargando {p.Name}…", $"Loading {p.Name}…");
        // Let the text paint before the (UI-thread) load, which can take a second or two.
        Dispatcher.UIThread.Post(() =>
        {
            var slot = chain.Add(p.Path, p.ClassId, out string? error);
            if (slot is null)
                scanStatus.Text = Loc.S($"✗ No se pudo cargar {p.Name}: {error}", $"✗ Could not load {p.Name}: {error}");
            else
            {
                scanStatus.Text = Loc.S($"✓ {p.Name} agregado.", $"✓ {p.Name} added.");
                selected = slot;
                RefreshChain();
            }
        }, DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------------------------------
    // Chain

    /// <summary>
    /// Right column. Grid rows: "CHAIN" caption, hint, scrollable slot rows (*), "PARAMETERS"
    /// header with the slot name, parameter filter, scrollable sliders (2*, i.e. the parameters
    /// get two thirds of the free height).
    /// </summary>
    /// <returns>The pane.</returns>
    Control ChainPane()
    {
        chainPanel = new StackPanel { Spacing = 6 };
        paramTitle = new TextBlock { FontWeight = FontWeight.SemiBold, Foreground = AppTheme.Brush("AccentHi") };
        paramSearch = new TextBox { PlaceholderText = Loc.S("Filtrar parámetros", "Filter parameters"), Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        paramSearch.TextChanged += (_, _) => RefreshParams();
        paramPanel = new StackPanel { Spacing = 4 };

        var pane = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto,2*") };
        pane.Children.Add(Section(Loc.S("CADENA (EN ORDEN, DESPUÉS DE CSOUND)", "CHAIN (IN ORDER, AFTER CSOUND)")));
        var hint = Dim(Loc.S("Clic en un plugin para ver sus parámetros abajo. «Editar» abre su propia ventana.", "Click a plug-in to see its parameters below. «Edit» opens its own window."));
        hint.Margin = new Thickness(0, 4, 0, 6);
        Grid.SetRow(hint, 1);
        pane.Children.Add(hint);
        var chainScroll = new ScrollViewer { Content = chainPanel };
        Grid.SetRow(chainScroll, 2);
        pane.Children.Add(chainScroll);
        var paramHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 12, 0, 6) };
        paramHeader.Children.Add(Section(Loc.S("PARÁMETROS", "PARAMETERS")));
        paramHeader.Children.Add(paramTitle);
        Grid.SetRow(paramHeader, 3);
        pane.Children.Add(paramHeader);
        Grid.SetRow(paramSearch, 4);
        pane.Children.Add(paramSearch);
        var paramScroll = new ScrollViewer { Content = paramPanel, Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetRow(paramScroll, 5);
        pane.Children.Add(paramScroll);
        return pane;
    }

    /// <summary><see cref="VstChain.Changed"/> handler: defers the redraw to the dispatcher so it runs after the current click handler returns.</summary>
    void OnChainChanged() => Dispatcher.UIThread.Post(RefreshChain);

    /// <summary>
    /// Redraws every slot row (numbered from 1 in processing order), keeps the selection if the
    /// slot still exists (otherwise selects the first one) and refreshes the parameter list.
    /// </summary>
    void RefreshChain()
    {
        if (chainPanel is null) return;
        chainPanel.Children.Clear();
        if (chain.Slots.Count == 0)
        {
            chainPanel.Children.Add(Dim(Loc.S("La cadena está vacía: Csound suena directo. Elige un plugin a la izquierda y presiona «Agregar a la cadena».",
                                              "The chain is empty: Csound sounds directly. Pick a plug-in on the left and press «Add to chain».")));
        }
        int n = 0;
        foreach (var slot in chain.Slots)
            chainPanel.Children.Add(SlotRow(slot, ++n));
        shownCrashes = chain.Slots.Count(s => s.Crashed); // what the rows now show (see RefreshStates)
        if (selected is not null && !chain.Slots.Contains(selected))
            selected = null;
        selected ??= chain.Slots.FirstOrDefault();
        RefreshParams();
    }

    /// <summary>
    /// One chain row: "N. ♪ Name" with "vendor · in→out ch · latency" (or the crash notice in red)
    /// and the Bypass / Edit / ▲ / ▼ / ✕ controls. The selected row gets a 2 px accent border.
    /// </summary>
    /// <param name="slot">The chain slot.</param>
    /// <param name="number">1-based position in the chain (disables ▲ on the first and ▼ on the last).</param>
    /// <returns>The row (a <c>card</c> border; clicking it selects the slot).</returns>
    Control SlotRow(VstSlot slot, int number)
    {
        bool isSel = slot == selected;
        var name = new TextBlock
        {
            Text = $"{number}. " + (slot.IsInstrument ? "♪ " : "") + slot.Name,
            FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        string details = slot.Vendor + $" · {slot.Inputs}→{slot.Outputs} ch" + (slot.Latency > 0 ? Loc.S($" · latencia {slot.Latency} muestras", $" · latency {slot.Latency} samples") : "");
        if (slot.Crashed) details = Loc.S("⚠ se cayó al procesar: desactivado", "⚠ crashed while processing: disabled") + " · " + details;
        var sub = new TextBlock { Text = details, FontSize = 11, Foreground = slot.Crashed ? AppTheme.Brush("Danger") : AppTheme.Brush("TextDim"), TextTrimming = TextTrimming.CharacterEllipsis };
        var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { name, sub } };

        var bypass = new CheckBox { Content = Loc.S("Bypass", "Bypass"), IsChecked = slot.Bypass, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(bypass, Loc.S("Salta el plugin: el sonido pasa sin cambios.", "Skips the plug-in: the sound passes unchanged."));
        // NotifyChanged raises VstChain.Changed: the view model saves the chain (Bypass is part
        // of the saved state) and this window redraws.
        bypass.IsCheckedChanged += (_, _) =>
        {
            slot.Bypass = bypass.IsChecked == true;
            chain.NotifyChanged();
        };
        var edit = Tool(Loc.S("Editar", "Edit"), Loc.S("Abre la ventana del propio plugin.", "Opens the plug-in's own window."), () => OpenEditor(slot));
        edit.IsEnabled = slot.HasEditor;
        var up = Tool("▲", Loc.S("Subir (procesa antes)", "Move up (processes earlier)"), () => chain.Move(slot, -1));
        up.IsEnabled = number > 1;
        var down = Tool("▼", Loc.S("Bajar (procesa después)", "Move down (processes later)"), () => chain.Move(slot, +1));
        down.IsEnabled = number < chain.Slots.Count;
        var remove = Tool("✕", Loc.S("Quitar de la cadena", "Remove from the chain"), () => chain.Remove(slot));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto"), ColumnSpacing = 6 };
        grid.Children.Add(texts);
        Control[] cells = [bypass, edit, up, down, remove];
        for (int i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i + 1);
            grid.Children.Add(cells[i]);
        }
        var border = new Border
        {
            Classes = { "card" },
            Padding = new Thickness(10, 6),
            BorderBrush = isSel ? AppTheme.Brush("Accent") : AppTheme.Brush("Line"),
            BorderThickness = new Thickness(isSel ? 2 : 1),
            Child = grid,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        // Click anywhere on the row except its buttons / check box selects the slot. The
        // Source test is needed because PointerPressed bubbles up from those controls too.
        border.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is null && v.FindAncestorOfType<CheckBox>(includeSelf: true) is null)
            {
                selected = slot;
                RefreshChain();
            }
        };
        return border;
    }

    /// <summary>
    /// Opens the plug-in's own editor in a native Win32 window owned by the main window (not by
    /// this one, so editors survive closing the chain window).
    /// </summary>
    /// <param name="slot">Slot whose editor to open; on failure the status line suggests the generic sliders.</param>
    void OpenEditor(VstSlot slot)
    {
        var main = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        IntPtr owner = main?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (slot.OpenEditor(owner) == IntPtr.Zero)
            scanStatus.Text = Loc.S($"✗ {slot.Name} no pudo abrir su ventana: usa los parámetros de abajo.", $"✗ {slot.Name} could not open its window: use the parameters below.");
    }

    /// <summary>
    /// Fills the PARAMETERS area for the selected slot: visible, writable parameters whose title
    /// matches the filter, at most 120 rows (some plug-ins expose thousands; building that many
    /// sliders would freeze the window).
    /// </summary>
    void RefreshParams()
    {
        if (paramPanel is null) return;
        paramPanel.Children.Clear();
        paramRows.Clear(); // ParamRow re-registers the rows it builds
        if (selected is not { } slot)
        {
            paramTitle.Text = "";
            return;
        }
        paramTitle.Text = slot.Name;
        string q = paramSearch?.Text?.Trim() ?? "";
        var ps = slot.Parameters().Where(p => !p.IsHidden && !p.IsReadOnly && (q.Length == 0 || p.Title.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        if (ps.Count == 0)
        {
            paramPanel.Children.Add(Dim(Loc.S("Este plugin no expone parámetros editables.", "This plug-in exposes no editable parameters.")));
            return;
        }
        const int max = 120;
        foreach (var p in ps.Take(max))
            paramPanel.Children.Add(ParamRow(slot, p));
        if (ps.Count > max)
            paramPanel.Children.Add(Dim(Loc.S($"… y {ps.Count - max} más (usa el filtro).", $"… and {ps.Count - max} more (use the filter).")));
    }

    /// <summary>
    /// One parameter: title | slider | value text. VST3 parameters are normalised 0…1; the text
    /// is the plug-in's own formatting of the value (e.g. "-6.0 dB"), or the raw number.
    /// Stepped parameters (<c>StepCount</c> &gt; 0) snap to their <c>StepCount + 1</c> positions.
    /// Double-click resets to the default.
    /// </summary>
    /// <param name="slot">Slot that owns the parameter.</param>
    /// <param name="p">Parameter description from the native host.</param>
    /// <returns>A three-column grid (200 DIP, *, 110 DIP).</returns>
    /// <remarks>
    /// The row is registered in <see cref="paramRows"/>; <see cref="RefreshStates"/> polls the
    /// plug-in once per second and moves the slider when the value changed elsewhere (the
    /// plug-in's own editor, automation).
    /// </remarks>
    Control ParamRow(VstSlot slot, VstHostNative.ParamInfo p)
    {
        double value = slot.GetParam(p.Id);
        var label = new TextBlock { Text = p.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var text = new TextBlock { Text = slot.ParamText(p.Id, value) ?? value.ToString("0.###"), VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("AccentHi"), FontSize = 12 };
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = value, VerticalAlignment = VerticalAlignment.Center };
        if (p.StepCount > 0)
        {
            slider.TickFrequency = 1.0 / p.StepCount;
            slider.IsSnapToTickEnabled = true;
        }
        slider.ValueChanged += (_, e) =>
        {
            // A sync from the plug-in (RefreshStates) only updates the text; echoing the value
            // back with SetParam would be redundant and could fight the plug-in's own automation.
            if (!syncingParams)
                slot.SetParam(p.Id, e.NewValue);
            text.Text = slot.ParamText(p.Id, e.NewValue) ?? e.NewValue.ToString("0.###");
        };
        paramRows.Add((slot, p.Id, slider, text));
        slider.DoubleTapped += (_, _) => slider.Value = p.Default;
        ToolTip.SetTip(slider, Loc.S("Doble clic: valor por defecto", "Double-click: default value"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*,110"), ColumnSpacing = 8 };
        grid.Children.Add(label);
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>Periodic refresh (1 s timer): crashes on the audio thread and parameter changes made elsewhere show up here.</summary>
    /// <remarks>
    /// <para>Crashes: the rows are redrawn whenever the number of crashed slots differs from the
    /// one recorded by the last <see cref="RefreshChain"/> (<see cref="shownCrashes"/>). Bug fix:
    /// the old check looked for any row already showing "⚠", so a second plug-in crashing later
    /// was never shown.</para>
    /// <para>Parameters: only the sliders currently built (at most 120, see
    /// <see cref="RefreshParams"/>) are polled with <c>vh_param_get</c> (controller side, UI-thread
    /// safe and cheap); a slider whose value differs from the plug-in's is moved under
    /// <see cref="syncingParams"/>. A slider the user is dragging is skipped so the poll does not
    /// fight the mouse.</para>
    /// </remarks>
    void RefreshStates()
    {
        if (chainPanel is not null && chain.Slots.Count(s => s.Crashed) != shownCrashes)
            RefreshChain();
        syncingParams = true;
        try
        {
            foreach (var (slot, id, slider, _) in paramRows)
            {
                if (slot.Handle == IntPtr.Zero || slider.IsPointerOver)
                    continue; // freed slot, or the mouse is on this slider (the user may be dragging it)
                double v = slot.GetParam(id);
                if (Math.Abs(v - slider.Value) > 1e-6)
                    slider.Value = v;
            }
        }
        finally { syncingParams = false; }
    }

    // ------------------------------------------------------------------------------------------
    // Small builders

    /// <summary>An upper-case section caption (style class <c>section</c>).</summary>
    static TextBlock Section(string text) => new() { Text = text, Classes = { "section" }, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>A wrapped explanatory paragraph in the dim text style.</summary>
    static TextBlock Dim(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Classes = { "dim" } };

    /// <summary>A themed ("tool") button with a tooltip and a click action.</summary>
    /// <param name="text">Caption.</param>
    /// <param name="tip">Tooltip.</param>
    /// <param name="click">Action run on click.</param>
    /// <returns>The button.</returns>
    static Button Tool(string text, string tip, Action click)
    {
        var b = new Button { Content = text, Classes = { "tool" }, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }
}
