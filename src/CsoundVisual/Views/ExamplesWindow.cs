using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Styling;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using CsoundVisual.Localization;
using CsoundVisual.Theming;
using CsoundVisual.ViewModels;

namespace CsoundVisual.Views;

/// <summary>
/// Example browser (toolbar «Ejemplos», Ctrl+E): the ~1500 examples of the Csound manual bundled
/// with the app, filterable by category and by words, with a highlighted read-only preview,
/// Previous/Next to walk through them, «Abrir» (open in a tab) and «Tocar» (open and play,
/// replacing whatever is playing). «Tocar al cambiar» plays each example as you move through
/// the list, to audition many quickly. Built in code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Data.</b> The examples come from <see cref="ExampleCatalog"/> (an <c>index.json</c> generated
/// by <c>tools/import_examples.py</c> and copied next to the exe): the manual examples plus the
/// app's own galleries (Showcase, Audio processing, Cabbage instruments), which the index lists
/// first. <see cref="ExampleCatalog.Filter"/> does the word search; this window only groups the
/// result by category.
/// </para>
/// <para>
/// <b>Layout.</b> Two columns: a fixed 400 DIP tree on the left (no splitter, no horizontal
/// scroll, trimmed names) and the preview on the right. The fixed width is deliberate: an earlier
/// category combo box changed width while browsing, which the owner found disturbing.
/// </para>
/// <para>
/// <b>Accordion tree.</b> Categories are branches and examples leaves (<see cref="ExampleNode"/>).
/// Opening a branch closes its siblings, so only one category is expanded at a time. The
/// <see cref="TreeViewItem.IsExpandedProperty"/> of every item is two-way bound to
/// <see cref="ExampleNode.IsExpanded"/> through a style setter (the tree is built in code, so
/// the binding is declared with a <see cref="Style"/> rather than in AXAML).
/// </para>
/// <para>
/// <b>Opening.</b> <see cref="MainViewModel.OpenExample"/> opens a tab (a clean example tab is
/// replaced instead of piling up) and, with <c>play</c>, stops the current performance and
/// starts this one. Examples are never overwritten: Save turns into Save As for them.
/// </para>
/// </remarks>
public sealed class ExamplesWindow : Window
{
    /// <summary>The application view model (opening/playing examples, editor font size, Stop).</summary>
    readonly MainViewModel vm;
    /// <summary>Search box; every change rebuilds the tree (<see cref="Refilter"/>).</summary>
    readonly TextBox search = new();
    /// <summary>The category/example tree (left column).</summary>
    readonly TreeView tree = new();
    /// <summary>Category branches shown by <see cref="tree"/>; also the sibling list of every branch.</summary>
    readonly ObservableCollection<ExampleNode> roots = [];
    /// <summary>"N of M examples · K categories" under the search box.</summary>
    readonly TextBlock count = new() { FontSize = 11.5, Classes = { "dim" } };
    /// <summary>Selected example's label (preview header).</summary>
    readonly TextBlock title = new() { FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = AppTheme.Brush("AccentHi") };
    /// <summary>Selected example's category display name.</summary>
    readonly TextBlock subtitle = new() { FontSize = 12, Foreground = AppTheme.Brush("Accent") };
    /// <summary>Selected example's description (from the manual or the gallery JSON); hidden when empty.</summary>
    readonly TextBlock description = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    /// <summary>Read-only AvaloniaEdit preview with the same Csound highlighting as the main editor.</summary>
    readonly TextEditor preview = new()
    {
        IsReadOnly = true,
        ShowLineNumbers = true,
        FontFamily = new FontFamily("Cascadia Code, Cascadia Mono, Consolas, monospace"),
        Padding = new Thickness(6, 8),
    };
    /// <summary>«Play on change»: when checked, selecting an example also opens and plays it.</summary>
    readonly CheckBox autoPlay = new();
    /// <summary>
    /// The filtered examples in display order (flat, category after category). Previous/Next
    /// walk this list, so they cross category boundaries.
    /// </summary>
    List<ExampleEntry> shown = [];
    /// <summary>
    /// True while <see cref="Refilter"/> rebuilds the tree and auto-selects a match. Those
    /// selection changes are programmatic (a keystroke in the search box), not a choice of the
    /// user, so <see cref="ShowSelected"/> must not open and play them even with «Play on change»
    /// on; otherwise typing "reverb" would start a new example on every letter.
    /// </summary>
    bool refiltering;

    /// <summary>
    /// A node of the example tree: a category (branch, with <see cref="Children"/>) or an example
    /// (leaf, with <see cref="Entry"/>). Branches behave as an accordion: opening one closes the
    /// one that was open, so the tree stays short and its width never changes.
    /// </summary>
    sealed class ExampleNode : INotifyPropertyChanged
    {
        /// <summary>Backing field of <see cref="IsExpanded"/>.</summary>
        bool expanded;
        /// <summary>Branch: "Category (count)"; leaf: the example's label.</summary>
        public required string Title { get; init; }
        /// <summary>Optional second line of a leaf (e.g. "live effect for Cubase"); null hides it.</summary>
        public string? Subtitle { get; init; }
        /// <summary>The example of a leaf; null for a category branch.</summary>
        public ExampleEntry? Entry { get; init; }
        /// <summary>Leaves of a branch (empty for leaves).</summary>
        public ObservableCollection<ExampleNode> Children { get; } = [];
        /// <summary>The list this branch belongs to (the window's roots); used to close the others.</summary>
        public IList<ExampleNode>? Siblings { get; init; }

        /// <summary>
        /// Whether the branch is open. Two-way bound to the <see cref="TreeViewItem"/>; setting it
        /// to true collapses every sibling (accordion). Collapsing siblings re-enters this setter
        /// on them with false, which does not cascade further.
        /// </summary>
        public bool IsExpanded
        {
            get => expanded;
            set
            {
                if (expanded == value) return;
                expanded = value;
                if (value && Siblings is not null)
                    foreach (var other in Siblings)
                        if (other != this) other.IsExpanded = false;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        /// <summary>Raised for <see cref="IsExpanded"/> (the only property that changes after creation).</summary>
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Creates the browser for <paramref name="viewModel"/>.</summary>
    /// <param name="viewModel">The main view model that opens and plays the chosen example.</param>
    public ExamplesWindow(MainViewModel viewModel)
    {
        vm = viewModel;
        Title = "Csound Visual · " + Loc.S("Ejemplos", "Examples");
        Width = 1180;
        Height = 780;
        MinWidth = 760;
        MinHeight = 460;
        Background = AppTheme.Brush("Bg");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // Preview editor: same highlighting definition (recoloured for the theme) and font size
        // as the main editor, so an example looks exactly as it will once opened.
        preview.SyntaxHighlighting = MainWindow.LoadHighlighting();
        preview.FontSize = vm.EditorFontSize;
        preview.Background = AppTheme.Brush("Surface");
        preview.Foreground = AppTheme.Brush("Text");

        // ---- left: filters and list
        search.PlaceholderText = Loc.S("Buscar: opcode, palabra, categoría…", "Search: opcode, word, category…");
        ToolTip.SetTip(search, Loc.S("Escribe una o varias palabras (en inglés, como el manual): oscil, reverb, granular, midi…",
                                     "Type one or more words: oscil, reverb, granular, midi…"));
        search.TextChanged += (_, _) => Refilter();

        // One tree, fixed width: categories are branches, examples are leaves. Long names are
        // trimmed (no horizontal scrolling), so the column never changes width.
        tree.ItemsSource = roots;
        // Template built in code: a branch is one bold line; a leaf is the label plus an optional
        // dim subtitle. The second lambda gives the children of each node to the TreeView.
        tree.ItemTemplate = new FuncTreeDataTemplate<ExampleNode>((node, _) => node.Entry is null
            ? new TextBlock { Text = node.Title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3) }
            : new StackPanel
            {
                Spacing = 1,
                Margin = new Thickness(0, 2),
                Children =
                {
                    new TextBlock { Text = node.Title, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = node.Subtitle, FontSize = 11, Classes = { "dim" }, TextTrimming = TextTrimming.CharacterEllipsis, IsVisible = !string.IsNullOrEmpty(node.Subtitle) },
                },
            }, node => node.Children);
        // Equivalent of <Style Selector="TreeViewItem"><Setter Property="IsExpanded" Value="{Binding IsExpanded, Mode=TwoWay}"/>:
        // each generated TreeViewItem has its ExampleNode as DataContext, so the binding resolves
        // per item and the accordion logic in ExampleNode drives the visual tree.
        tree.Styles.Add(new Style(x => x.OfType<TreeViewItem>())
        {
            Setters = { new Setter(TreeViewItem.IsExpandedProperty, new Binding(nameof(ExampleNode.IsExpanded)) { Mode = BindingMode.TwoWay }) },
        });
        ScrollViewer.SetHorizontalScrollBarVisibility(tree, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        // Selecting a category opens it (and, by the accordion, closes the others); selecting an
        // example shows it in the preview (and plays it when «Play on change» is on).
        // Selecting a category toggles it: a closed branch opens (closing the others), the open one
        // closes. (The previous expression `!x || empty ? true : x` was always true, so a branch
        // could never be closed from its header; toggling is the intended behaviour.) Programmatic
        // selections done by Refilter never land on a branch, so this only reacts to the user.
        tree.SelectionChanged += (_, _) =>
        {
            if (tree.SelectedItem is ExampleNode { Entry: null } branch)
                branch.IsExpanded = !branch.IsExpanded;
            else
                ShowSelected();
        };
        // Double-click / Enter = open in a tab; Ctrl+Enter = open and play.
        tree.DoubleTapped += (_, _) => OpenSelected(play: false);
        tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { OpenSelected(play: e.KeyModifiers.HasFlag(KeyModifiers.Control)); e.Handled = true; }
        };

        var left = new DockPanel { Margin = new Thickness(14, 12, 8, 12) };
        var filters = new StackPanel { Spacing = 8, Children = { search, count } };
        DockPanel.SetDock(filters, Dock.Top);
        left.Children.Add(filters);
        tree.Margin = new Thickness(0, 8, 0, 0);
        left.Children.Add(tree);

        // ---- right: header, buttons, preview
        var prev = Button("◀  " + Loc.S("Anterior", "Previous"), () => Step(-1), Loc.S("Ejemplo anterior (también ↑ en la lista).", "Previous example (also ↑ in the list)."));
        var next = Button(Loc.S("Siguiente", "Next") + "  ▶", () => Step(+1), Loc.S("Ejemplo siguiente (también ↓ en la lista).", "Next example (also ↓ in the list)."));
        var open = Button(Loc.S("Abrir en pestaña", "Open in a tab"), () => OpenSelected(play: false),
            Loc.S("Abre el ejemplo en una pestaña del editor (doble clic o Enter). Si lo modificas, «Guardar» te pedirá otro nombre: los ejemplos originales no se sobrescriben.",
                  "Opens the example in an editor tab (double-click or Enter). If you change it, «Save» asks for a new name: the original examples are never overwritten."));
        var play = Button("▶  " + Loc.S("Tocar", "Play"), () => OpenSelected(play: true),
            Loc.S("Abre y toca el ejemplo; si algo está sonando, se detiene y empieza este (Ctrl+Enter).", "Opens and plays the example; if something is playing it stops and this one starts (Ctrl+Enter)."));
        play.Classes.Add("primary");
        var stop = new Button { Content = "■  " + Loc.S("Detener", "Stop"), Command = vm.StopCommand, Classes = { "tool" } };
        var demo = new Button { Content = Loc.S("Abrir demo", "Open demo"), Command = vm.OpenDemoCommand, Classes = { "tool" } };
        ToolTip.SetTip(demo, Loc.S("Abre la demo de Csound Visual (la del primer arranque) en una pestaña nueva sin nombre.",
                                   "Opens the Csound Visual demo (the first-start one) in a new untitled tab."));
        autoPlay.Content = Loc.S("Tocar al cambiar", "Play on change");
        ToolTip.SetTip(autoPlay, Loc.S("Con esto activado, cada ejemplo que elijas se abre y suena de inmediato: útil para recorrerlos rápido.",
                                       "When on, every example you pick opens and plays at once: handy for going through many."));

        // WrapPanel: the button row reflows on narrow windows instead of clipping.
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, Children = { prev, next, open, play, stop, demo, autoPlay } };
        foreach (var c in buttons.Children) c.Margin = new Thickness(0, 0, 8, 6);
        autoPlay.VerticalAlignment = VerticalAlignment.Center;

        var header = new StackPanel { Spacing = 3, Children = { title, subtitle, description, buttons } };
        buttons.Margin = new Thickness(0, 8, 0, 4);
        var right = new DockPanel { Margin = new Thickness(8, 12, 14, 12) };
        DockPanel.SetDock(header, Dock.Top);
        right.Children.Add(header);
        right.Children.Add(new Border
        {
            Child = preview,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            BorderBrush = AppTheme.Brush("Line"),
            BorderThickness = new Thickness(1),
        });

        // Fixed-width tree column on the left (no splitter): the list never changes width.
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("400,1,*") };
        root.Children.Add(left);
        var line = new Border { Background = AppTheme.Brush("Line") };
        Grid.SetColumn(line, 1);
        root.Children.Add(line);
        Grid.SetColumn(right, 2);
        root.Children.Add(right);
        Content = root;

        // Window keys: Esc closes, Ctrl+F jumps to the search box. The search box gets the focus
        // on open so the user can start typing immediately.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { search.Focus(); e.Handled = true; }
        };
        Opened += (_, _) => search.Focus();
        Refilter();
    }

    /// <summary>Creates a themed ("tool") button with a click action and a tooltip.</summary>
    /// <param name="text">Button caption.</param>
    /// <param name="click">Action run on click.</param>
    /// <param name="tip">Tooltip text.</param>
    /// <returns>The new button.</returns>
    static Button Button(string text, Action click, string tip)
    {
        var b = new Button { Content = text, Classes = { "tool" } };
        b.Click += (_, _) => click();
        ToolTip.SetTip(b, tip);
        return b;
    }

    /// <summary>The selected example, or null when nothing or a category branch is selected.</summary>
    ExampleEntry? Selected => (tree.SelectedItem as ExampleNode)?.Entry;

    /// <summary>
    /// Rebuilds the tree from the search box: one branch per category that has matches. With no
    /// search, the showcase branch starts open; with a search, the first matching branch opens.
    /// </summary>
    /// <remarks>
    /// The previous selection is kept when it still matches; otherwise the first match is
    /// selected. Groups keep the catalogue order (LINQ <c>GroupBy</c> preserves first-appearance order), which is why the showcase category comes first. The tree
    /// is rebuilt from scratch on every keystroke; with ~1500 entries that is fast enough.
    /// The auto-selection is wrapped in <see cref="refiltering"/>, so with «Play on change» on the
    /// first match of each keystroke is previewed but not played; only an explicit selection by
    /// the user (click, arrow keys, Previous/Next) plays.
    /// </remarks>
    void Refilter()
    {
        refiltering = true;
        try { RefilterCore(); }
        finally { refiltering = false; }
    }

    /// <summary>Body of <see cref="Refilter"/>; runs with <see cref="refiltering"/> set.</summary>
    void RefilterCore()
    {
        var keep = Selected;
        shown = ExampleCatalog.Filter(search.Text, null).ToList();
        roots.Clear();
        foreach (var group in shown.GroupBy(e => e.Category))
        {
            var branch = new ExampleNode
            {
                Title = $"{ExampleCatalog.DisplayCategory(group.Key)}  ({group.Count()})",
                Siblings = roots,
            };
            foreach (var e in group)
                branch.Children.Add(new ExampleNode { Title = e.Label, Subtitle = e.Live ? Loc.S("efecto en vivo para Cubase", "live effect for Cubase") : null, Entry = e });
            roots.Add(branch);
        }
        count.Text = Loc.S($"{shown.Count} de {ExampleCatalog.All.Count} ejemplos · {roots.Count} categorías",
                           $"{shown.Count} of {ExampleCatalog.All.Count} examples · {roots.Count} categories");
        var target = keep is not null && shown.Contains(keep) ? keep : shown.FirstOrDefault();
        if (target is not null)
            SelectEntry(target);
        else
            ShowSelected();
    }

    /// <summary>Opens the branch of <paramref name="entry"/> (closing the others) and selects it.</summary>
    /// <param name="entry">Example to select; ignored if it is not in the current tree.</param>
    /// <remarks>
    /// <c>ScrollIntoView</c> is posted at Background priority because the leaf's container only
    /// exists after the branch has expanded and the tree has run a layout pass.
    /// </remarks>
    void SelectEntry(ExampleEntry entry)
    {
        foreach (var branch in roots)
            if (branch.Children.FirstOrDefault(n => n.Entry == entry) is { } leaf)
            {
                branch.IsExpanded = true;
                tree.SelectedItem = leaf;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => tree.ScrollIntoView(leaf), Avalonia.Threading.DispatcherPriority.Background);
                return;
            }
    }

    /// <summary>Previous/Next: moves the selection by <paramref name="delta"/> in <see cref="shown"/>, clamped to the ends.</summary>
    /// <param name="delta">-1 for previous, +1 for next. With no selection, starts from the first example.</param>
    void Step(int delta)
    {
        if (shown.Count == 0) return;
        int i = Selected is { } e ? shown.IndexOf(e) : -1;
        i = Math.Clamp(i + delta, 0, shown.Count - 1);
        SelectEntry(shown[i]);
    }

    /// <summary>
    /// Fills the right-hand side with the selected example (title, category, description and the
    /// file read from disk into the preview) or "No results"; then, if «Play on change» is on,
    /// opens and plays it.
    /// </summary>
    void ShowSelected()
    {
        if (Selected is not { } e)
        {
            title.Text = Loc.S("Sin resultados", "No results");
            subtitle.Text = description.Text = "";
            preview.Text = "";
            return;
        }
        title.Text = e.Label;
        subtitle.Text = e.CategoryDisplay;
        description.Text = e.DisplayDescription;
        description.IsVisible = description.Text.Length > 0;
        try
        {
            preview.Text = File.ReadAllText(e.FullPath);
        }
        catch (Exception ex)
        {
            preview.Text = Loc.S("No se pudo leer el archivo: ", "Could not read the file: ") + ex.Message;
        }
        preview.ScrollToHome();
        // Play only on a selection made by the user, never on the auto-selection of a search.
        if (autoPlay.IsChecked == true && !refiltering)
            vm.OpenExample(e, play: true);
    }

    /// <summary>Opens the selected example in a tab, optionally playing it; no-op on a category.</summary>
    /// <param name="play">True to stop whatever plays and start this example.</param>
    void OpenSelected(bool play)
    {
        if (Selected is { } e)
            vm.OpenExample(e, play);
    }
}
