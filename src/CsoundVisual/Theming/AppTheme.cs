using System.Numerics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace CsoundVisual.Theming;

/// <summary>
/// A named colour preset: one hex colour (#RRGGBB or #AARRGGBB) per role. See
/// <see cref="AppTheme"/> for the list of roles and how they are used.
/// </summary>
/// <param name="Name">Stable Spanish name (saved in the settings file; also the Spanish display name).</param>
/// <param name="EnglishName">Name shown when the interface is in English.</param>
/// <param name="Colors">Role → hex colour.</param>
public sealed record ThemePreset(string Name, string EnglishName, IReadOnlyDictionary<string, string> Colors) : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>Name in the interface language (the picker binds to it and follows language changes).</summary>
    public string DisplayName => CsoundVisual.Localization.Loc.S(Name, EnglishName);

    /// <inheritdoc/>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Tells the picker that <see cref="DisplayName"/> changed (called on language change).</summary>
    internal void RaiseDisplayNameChanged() =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayName)));

    /// <summary>Four representative colours for the picker preview (background, surface, accent, highlight).</summary>
    public IReadOnlyList<IBrush> Swatches { get; } =
        [Solid(Colors["Bg"]), Solid(Colors["Surface2"]), Solid(Colors["Accent"]), Solid(Colors["AccentHi"])];

    /// <summary>Immutable brush for a hex colour (the swatches never change, unlike the role brushes of <see cref="AppTheme"/>).</summary>
    static IBrush Solid(string hex) => new SolidColorBrush(Color.Parse(hex)).ToImmutable();
}

/// <summary>
/// Central colour theme of the whole app (windows, side-panel controls, editor syntax colours and
/// the 3D scenes), with presets the user picks in the «Apariencia» card.
/// </summary>
/// <remarks>
/// <para><b>How it works.</b> Every colour is a <i>role</i> («Bg», «Accent», «TextDim», «SynKeyword»…).
/// <see cref="Brush"/> hands out <b>one stable <see cref="SolidColorBrush"/> per role</b>; switching
/// preset only changes the <c>Color</c> of those same objects, so everything that holds them
/// (XAML through <c>{DynamicResource RoleBrush}</c>, the custom controls through static fields)
/// repaints without being rebuilt. A role may carry an alpha suffix: <c>"Accent_40"</c> is the
/// accent with alpha 0x40.</para>
/// <para>Things that cannot hold a brush (the AvaloniaEdit syntax definition, which is frozen
/// once loaded, the GPU clear colour, gradient stops) read <see cref="ColorOf(string)"/> or listen to
/// <see cref="Changed"/> and rebuild.</para>
/// <para>Roles: BgDeep, Bg, Surface, Surface2, ChromeLow, Line, Grid, Accent, AccentHi,
/// AccentLight, AccentDeep, AccentMid, Selection, Text, TextDim, TextFaint, Disabled, OnAccent,
/// Danger, Record, Warn, Hot, Clip, Good, PianoWhite, TitleStart, TitleEnd and the syntax
/// colours SynComment, SynString, SynTag, SynKeyword, SynHeader, SynOpcode, SynARate, SynKRate,
/// SynIRate, SynSVar, SynFVar, SynGlobal, SynPField, SynNumber, SynMacro.</para>
/// <para><b>Threading.</b> Everything here is UI-thread only (Avalonia brushes are thread-affine
/// and the caches are plain dictionaries/lists).</para>
/// <para>The preset name is persisted in the settings (<c>ThemePreset</c>); it is the Spanish
/// <see cref="ThemePreset.Name"/>, which therefore must never be renamed.</para>
/// </remarks>
public static class AppTheme
{
    /// <summary>Preset used on first run.</summary>
    public const string DefaultPreset = "Azul deportivo";

    /// <summary>Role (with optional alpha suffix) → the one brush handed out for it; recoloured in place by <see cref="Apply"/>.</summary>
    static readonly Dictionary<string, SolidColorBrush> brushes = [];

    /// <summary>Syntax colours shared by the presets (each preset overrides a few to match its accent).</summary>
    static readonly Dictionary<string, string> Syntax = new()
    {
        ["SynString"] = "#FFB86B", ["SynHeader"] = "#FF9EC4", ["SynOpcode"] = "#5CE1E6",
        ["SynARate"] = "#FF9F6E", ["SynKRate"] = "#A6E36E", ["SynIRate"] = "#C3A6FF",
        ["SynSVar"] = "#FFB86B", ["SynFVar"] = "#E0A8FF", ["SynPField"] = "#FF9EC4",
        ["SynNumber"] = "#FFD580", ["SynMacro"] = "#FF8A65",
    };

    /// <summary>Builds a preset, filling every syntax role the preset does not define from <see cref="Syntax"/>.</summary>
    static ThemePreset Make(string name, string englishName, Dictionary<string, string> colors)
    {
        foreach (var (k, v) in Syntax)
            colors.TryAdd(k, v);
        return new ThemePreset(name, englishName, colors);
    }

    // Keep the preset picker's display names in the current UI language.
    static AppTheme() =>
        CsoundVisual.Localization.Loc.Changed += () =>
        {
            foreach (var p in Presets) p.RaiseDisplayNameChanged();
        };

    /// <summary>All presets, in picker order.</summary>
    public static IReadOnlyList<ThemePreset> Presets { get; } =
    [
        // Deep midnight navy with an intense electric blue, like a sports-car paint.
        Make("Azul deportivo", "Sports blue", new()
        {
            ["BgDeep"] = "#060B16", ["Bg"] = "#0A1222", ["Surface"] = "#0E1A30", ["Surface2"] = "#13213C",
            ["ChromeLow"] = "#08101E", ["Line"] = "#1F3560", ["Grid"] = "#16294A",
            ["Accent"] = "#1E6BFF", ["AccentHi"] = "#5AA0FF", ["AccentLight"] = "#A8CCFF", ["AccentDeep"] = "#0B3A9E", ["AccentMid"] = "#1554D6",
            ["Selection"] = "#661E6BFF", ["Text"] = "#E6EEFF", ["TextDim"] = "#8095BC", ["TextFaint"] = "#5A6F96",
            ["Disabled"] = "#2A3B5C", ["OnAccent"] = "#FFFFFF",
            ["Danger"] = "#FF5D5D", ["Record"] = "#FF3B30", ["Warn"] = "#FFB547", ["Hot"] = "#FF8A1C", ["Clip"] = "#FF3B30", ["Good"] = "#3DDC97",
            ["PianoWhite"] = "#EEF4FF", ["TitleStart"] = "#10306E", ["TitleEnd"] = "#0A1426",
            ["SynComment"] = "#6B7FA6", ["SynTag"] = "#4D9CFF", ["SynKeyword"] = "#7FB8FF", ["SynGlobal"] = "#9FD0FF",
        }),
        // Graphite with papaya orange.
        Make("Grafito y naranja", "Graphite & orange", new()
        {
            ["BgDeep"] = "#0C0C0E", ["Bg"] = "#131316", ["Surface"] = "#1A1A1F", ["Surface2"] = "#222228",
            ["ChromeLow"] = "#101013", ["Line"] = "#34343D", ["Grid"] = "#26262D",
            ["Accent"] = "#FF8000", ["AccentHi"] = "#FFA640", ["AccentLight"] = "#FFD4A3", ["AccentDeep"] = "#9A4D00", ["AccentMid"] = "#D96C00",
            ["Selection"] = "#66FF8000", ["Text"] = "#F2F2F5", ["TextDim"] = "#9A9AA8", ["TextFaint"] = "#6E6E7A",
            ["Disabled"] = "#3A3A42", ["OnAccent"] = "#101010",
            ["Danger"] = "#FF5D5D", ["Record"] = "#FF3B30", ["Warn"] = "#FFC247", ["Hot"] = "#FF5A1C", ["Clip"] = "#FF2D2D", ["Good"] = "#3DDC97",
            ["PianoWhite"] = "#F5F5F7", ["TitleStart"] = "#2A2A31", ["TitleEnd"] = "#131316",
            ["SynComment"] = "#7A7A88", ["SynTag"] = "#FF8000", ["SynKeyword"] = "#FFA640", ["SynGlobal"] = "#FFD4A3",
        }),
        // Racing red on dark wine.
        Make("Rojo carrera", "Racing red", new()
        {
            ["BgDeep"] = "#120607", ["Bg"] = "#1A0A0C", ["Surface"] = "#221013", ["Surface2"] = "#2B151A",
            ["ChromeLow"] = "#160809", ["Line"] = "#4A1F26", ["Grid"] = "#33161B",
            ["Accent"] = "#E8202F", ["AccentHi"] = "#FF5A5F", ["AccentLight"] = "#FFB3B5", ["AccentDeep"] = "#8A0F18", ["AccentMid"] = "#C21826",
            ["Selection"] = "#66E8202F", ["Text"] = "#FFECEC", ["TextDim"] = "#B88A8E", ["TextFaint"] = "#8A5F64",
            ["Disabled"] = "#4A2A2E", ["OnAccent"] = "#FFFFFF",
            ["Danger"] = "#FF8A5C", ["Record"] = "#FF3B30", ["Warn"] = "#FFC247", ["Hot"] = "#FF8A1C", ["Clip"] = "#FFE14D", ["Good"] = "#4ADE80",
            ["PianoWhite"] = "#FFF1F1", ["TitleStart"] = "#5A0D14", ["TitleEnd"] = "#1A0A0C",
            ["SynComment"] = "#9A7378", ["SynTag"] = "#FF4D57", ["SynKeyword"] = "#FF7A80", ["SynGlobal"] = "#FFB3B5",
        }),
        // Emerald green on a racing-green night.
        Make("Verde esmeralda", "Emerald green", new()
        {
            ["BgDeep"] = "#04110B", ["Bg"] = "#071A11", ["Surface"] = "#0B2418", ["Surface2"] = "#102D1F",
            ["ChromeLow"] = "#06150E", ["Line"] = "#1D4A34", ["Grid"] = "#153826",
            ["Accent"] = "#22C55E", ["AccentHi"] = "#5EE08F", ["AccentLight"] = "#B5F2CB", ["AccentDeep"] = "#0F6B3A", ["AccentMid"] = "#1A9E4B",
            ["Selection"] = "#6622C55E", ["Text"] = "#E8FFF0", ["TextDim"] = "#82B096", ["TextFaint"] = "#5A8670",
            ["Disabled"] = "#23422F", ["OnAccent"] = "#04110B",
            ["Danger"] = "#FF6B5D", ["Record"] = "#FF3B30", ["Warn"] = "#FFC247", ["Hot"] = "#FF8A1C", ["Clip"] = "#FF3B30", ["Good"] = "#A6E36E",
            ["PianoWhite"] = "#F0FFF5", ["TitleStart"] = "#0E4A2B", ["TitleEnd"] = "#071A11",
            ["SynComment"] = "#6F8F7E", ["SynTag"] = "#22C55E", ["SynKeyword"] = "#6EE7A0", ["SynGlobal"] = "#B5F2CB",
        }),
        // The original look: inverted yellow, deep ochre and amber.
        Make("Ámbar clásico", "Classic amber", new()
        {
            ["BgDeep"] = "#1A1400", ["Bg"] = "#241C00", ["Surface"] = "#2B2200", ["Surface2"] = "#332900",
            ["ChromeLow"] = "#1F1800", ["Line"] = "#4D3E00", ["Grid"] = "#3D3100",
            ["Accent"] = "#F5B800", ["AccentHi"] = "#FFD84A", ["AccentLight"] = "#FFE873", ["AccentDeep"] = "#8A6400", ["AccentMid"] = "#C98A00",
            ["Selection"] = "#80A67C00", ["Text"] = "#FFF2B0", ["TextDim"] = "#A89040", ["TextFaint"] = "#8A7320",
            ["Disabled"] = "#5C4A00", ["OnAccent"] = "#1A1400",
            ["Danger"] = "#FF7A59", ["Record"] = "#FF5A3C", ["Warn"] = "#FFB84D", ["Hot"] = "#FF8A1C", ["Clip"] = "#FF4A2E", ["Good"] = "#C5E478",
            ["PianoWhite"] = "#FFF6D8", ["TitleStart"] = "#4A3700", ["TitleEnd"] = "#2E2300",
            ["SynComment"] = "#8F7F4F", ["SynString"] = "#F5A65B", ["SynTag"] = "#F5B800", ["SynKeyword"] = "#FFD84A",
            ["SynHeader"] = "#FFB0C8", ["SynOpcode"] = "#7FD1C7", ["SynKRate"] = "#C5E478", ["SynIRate"] = "#9CC7FF",
            ["SynSVar"] = "#F5A65B", ["SynFVar"] = "#D6A8FF", ["SynGlobal"] = "#FFE873", ["SynPField"] = "#FFB0C8",
            ["SynNumber"] = "#E6B3FF",
        }),
    ];

    /// <summary>The active preset.</summary>
    public static ThemePreset Current { get; private set; } = Presets[0];

    /// <summary>Raised (on the UI thread) after a preset has been applied.</summary>
    public static event Action? Changed;

    /// <summary>
    /// The stable brush of <paramref name="role"/> (optionally with an alpha suffix, e.g.
    /// <c>"Accent_40"</c>). The same object is returned every time and recoloured by <see cref="Apply"/>.
    /// </summary>
    public static SolidColorBrush Brush(string role)
    {
        if (!brushes.TryGetValue(role, out var brush))
        {
            brush = new SolidColorBrush(ColorOf(role));
            brushes[role] = brush;
        }
        return brush;
    }

    /// <summary>Current colour of <paramref name="role"/> (alpha suffix allowed).</summary>
    /// <param name="role">Role name, optionally followed by <c>_</c> and a two-digit hex alpha (<c>"Accent_40"</c> = alpha 0x40 ≈ 25 %).</param>
    /// <returns>The colour; an unknown role yields magenta so the mistake is obvious on screen.</returns>
    /// <exception cref="FormatException">The alpha suffix is not valid hex.</exception>
    public static Color ColorOf(string role)
    {
        string name = role;
        byte? alpha = null;
        int us = role.IndexOf('_');
        if (us > 0)
        {
            name = role[..us];
            alpha = Convert.ToByte(role[(us + 1)..], 16);
        }
        var c = Current.Colors.TryGetValue(name, out var hex) ? Avalonia.Media.Color.Parse(hex) : Colors.Magenta;
        return alpha is { } a ? new Avalonia.Media.Color(a, c.R, c.G, c.B) : c;
    }

    /// <summary>Colour of <paramref name="role"/> as an RGBA vector for the GPU scenes.</summary>
    /// <param name="role">Role name (alpha suffix allowed).</param>
    /// <param name="alpha">Overrides the alpha (0…1) when given.</param>
    /// <returns>Components normalised to 0…1 (sRGB values, not linearised).</returns>
    public static Vector4 Vector(string role, float? alpha = null)
    {
        var c = ColorOf(role);
        return new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, alpha ?? c.A / 255f);
    }

    /// <summary>
    /// Every gradient stop created by <see cref="Gradient"/>, with its role, so <see cref="Apply"/>
    /// can recolour them.
    /// </summary>
    /// <remarks>
    /// Bug fix (list only grew): the stops used to be held strongly and never removed, so any
    /// caller creating gradients repeatedly (per control instance instead of once per type)
    /// leaked every stop and brush for the life of the app. They are now held through weak
    /// references, and dead entries are pruned by <see cref="Gradient"/> and <see cref="Apply"/>.
    /// A gradient that is still in use is referenced by its brush, so it is never lost.
    /// </remarks>
    static readonly List<(WeakReference<GradientStop> Stop, string Role)> gradientStops = [];

    /// <summary>
    /// A gradient whose stops follow the theme (used by the level meter and the spectrum).
    /// </summary>
    /// <param name="start">Start point (relative).</param>
    /// <param name="end">End point (relative).</param>
    /// <param name="stops">(role, offset) pairs.</param>
    public static LinearGradientBrush Gradient(RelativePoint start, RelativePoint end, params (string Role, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        gradientStops.RemoveAll(e => !e.Stop.TryGetTarget(out _));   // keep the registry bounded
        foreach (var (r, offset) in stops)
        {
            var stop = new GradientStop(ColorOf(r), offset);
            gradientStops.Add((new WeakReference<GradientStop>(stop), r));
            brush.GradientStops.Add(stop);
        }
        return brush;
    }

    /// <summary>Roles registered as application resources «RoleBrush» for <c>{DynamicResource}</c> in XAML.</summary>
    static readonly string[] ResourceRoles =
    [
        "BgDeep", "Bg", "Surface", "Surface2", "Line", "Grid", "Accent", "AccentHi", "AccentLight", "AccentDeep",
        "Text", "TextDim", "TextFaint", "Disabled", "OnAccent", "Danger", "Record", "Warn", "Good",
        "AccentHi_33", "Accent_40", "Danger_40", "Surface2_F2", "Good_18",
    ];

    /// <summary>
    /// Activates the preset called <paramref name="name"/> (falls back to the default): recolours
    /// every brush handed out so far, updates the XAML resources and the Fluent palette, and
    /// raises <see cref="Changed"/>. Call it once before the first window is created, and again
    /// whenever the user picks another preset.
    /// </summary>
    public static void Apply(string? name)
    {
        Current = Presets.FirstOrDefault(p => p.Name == name) ?? Presets.First(p => p.Name == DefaultPreset);
        foreach (var (role, brush) in brushes)
            brush.Color = ColorOf(role);
        // Forget stops whose gradient was garbage-collected, then recolour the live ones.
        gradientStops.RemoveAll(e => !e.Stop.TryGetTarget(out _));
        foreach (var (weak, role) in gradientStops)
            if (weak.TryGetTarget(out var stop))
                stop.Color = ColorOf(role);

        if (Application.Current is { } app)
        {
            // Re-assigning the same brush objects is a no-op after the first call; it matters only
            // the first time, when the resources are created.
            foreach (var role in ResourceRoles)
                app.Resources[role + "Brush"] = Brush(role);
            // The title gradient is replaced (not recoloured): a new resource value makes every
            // DynamicResource consumer pick it up.
            app.Resources["TitleBrush"] = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(ColorOf("TitleStart"), 0), new GradientStop(ColorOf("TitleEnd"), 1) },
            };
            UpdateFluentPalette(app);
        }
        Changed?.Invoke();
    }

    /// <summary>Recolours Avalonia's Fluent controls (combo boxes, check boxes, scroll bars…).</summary>
    /// <remarks>
    /// The app always runs in the Dark variant, so only that palette is touched. Fluent derives its
    /// own shades from these few base colours; the mapping below picks the role whose meaning is
    /// closest (Alt = window background, Base = foreground text, Chrome = control surfaces).
    /// </remarks>
    static void UpdateFluentPalette(Application app)
    {
        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is not { } fluent ||
            !fluent.Palettes.TryGetValue(ThemeVariant.Dark, out var palette))
            return;
        palette.Accent = ColorOf("Accent");
        palette.AltHigh = ColorOf("BgDeep");
        palette.RegionColor = ColorOf("Bg");
        palette.BaseHigh = ColorOf("Text");
        palette.ChromeMediumLow = ColorOf("Surface");
        palette.ChromeMedium = ColorOf("Surface2");
        palette.ChromeLow = ColorOf("ChromeLow");
        palette.ChromeHigh = ColorOf("Line");
        palette.ErrorText = ColorOf("Danger");
    }
}
