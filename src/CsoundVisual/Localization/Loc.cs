using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace CsoundVisual.Localization;

/// <summary>Interface languages.</summary>
public enum UiLanguage
{
    /// <summary>Spanish (the original language of the app).</summary>
    Spanish,

    /// <summary>English.</summary>
    English,
}

/// <summary>
/// Interface language of the whole app (Spanish / English), switchable at run time from
/// «Configuración».
/// </summary>
/// <remarks>
/// <para>Two ways to write a translatable text:</para>
/// <list type="bullet">
/// <item><b>In C#</b>: <c>Loc.S("Texto en español", "English text")</c>. Both versions sit next to
/// each other, so a message can never lose its translation. The text is picked when the line
/// runs: messages already printed keep their language.</item>
/// <item><b>In XAML</b>: <c>Text="{l:T Key}"</c>, where <c>Key</c> is an entry of
/// <see cref="Strings.Table"/>. It is a live binding to <see cref="Instance"/>'s indexer, so the
/// window switches language instantly.</item>
/// </list>
/// <para>Windows built in code (3D, dialogs, settings) read <c>Loc.S</c> when they are created;
/// they show the new language the next time they open.</para>
/// <para>The language is applied by <c>App</c> from the saved settings before the main view model
/// is created, so texts built during start-up are already in the right language.
/// <see cref="S"/> may be called from any thread (it only reads <see cref="Language"/>);
/// <see cref="SetLanguage"/> must run on the UI thread because it fires binding notifications.</para>
/// </remarks>
public sealed class Loc : INotifyPropertyChanged
{
    /// <summary>Singleton used as binding source by <see cref="TExtension"/>.</summary>
    public static Loc Instance { get; } = new();

    /// <summary>Private: the only instance is <see cref="Instance"/>.</summary>
    Loc() { }

    /// <summary>Active language.</summary>
    public static UiLanguage Language { get; private set; } = UiLanguage.English;

    /// <summary>True when the interface is in English.</summary>
    public static bool IsEnglish => Language == UiLanguage.English;

    /// <summary>Raised after the language changed (on the UI thread).</summary>
    public static event Action? Changed;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Picks the text for the active language.</summary>
    /// <param name="es">Spanish text.</param>
    /// <param name="en">English text.</param>
    public static string S(string es, string en) => IsEnglish ? en : es;

    /// <summary>Text of a <see cref="Strings.Table"/> entry in the active language (the key itself if unknown).</summary>
    public string this[string key] =>
        Strings.Table.TryGetValue(key, out var t) ? (IsEnglish ? t.En : t.Es) : key;

    /// <summary>Changes the language and refreshes every <c>{l:T …}</c> binding.</summary>
    public static void SetLanguage(UiLanguage language)
    {
        if (language == Language) return;
        Language = language;
        // "Item[]" is the conventional property name for "every indexer value changed": Avalonia
        // re-evaluates all bindings whose path is an indexer on this object, i.e. every {l:T …}.
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        Changed?.Invoke();
    }

    /// <summary>Parses the stored setting ("en" / "es"); anything else (including no setting) means English, the default.</summary>
    public static UiLanguage Parse(string? code) => code == "es" ? UiLanguage.Spanish : UiLanguage.English;

    /// <summary>Code stored in the settings file for <paramref name="language"/>.</summary>
    public static string Code(UiLanguage language) => language == UiLanguage.English ? "en" : "es";
}

/// <summary>
/// XAML markup extension for translated texts: <c>Text="{l:T Toolbar.Play}"</c>. Produces a
/// one-way binding to <c>Loc.Instance[Key]</c>, so the text follows language changes live.
/// </summary>
public sealed class TExtension : MarkupExtension
{
    /// <summary>Creates the extension (XAML positional argument = key).</summary>
    public TExtension(string key) => Key = key;

    /// <summary>Key in <see cref="Strings.Table"/>.</summary>
    public string Key { get; set; }

    /// <summary>Returns the binding <c>Loc.Instance[Key]</c> (one-way) that the XAML property is bound to.</summary>
    /// <param name="serviceProvider">XAML service provider (unused).</param>
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
}
