using CommunityToolkit.Mvvm.Input;
using CsoundVisual.Localization;

namespace CsoundVisual.ViewModels;

/// <summary>
/// «Configuración» window support: interface language, editor font size, and the command that
/// opens the window (the window itself is <c>Views/SettingsWindow</c>, provided by the view).
/// </summary>
/// <remarks>
/// <para>
/// The properties here are hand-written façades over <see cref="AppSettings"/> rather than
/// <c>[ObservableProperty]</c> fields: the settings object is the single place the value lives,
/// and each setter validates, writes it there, saves <c>settings.json</c> immediately and then
/// raises <c>PropertyChanged</c> so every bound control (settings window, editor) follows.
/// The equality checks avoid a disk write when a binding echoes the same value back.
/// </para>
/// <para>
/// Audio options shown in the settings window are in <c>MainViewModel.Audio.cs</c>; the colour
/// theme is in <c>MainViewModel.Appearance.cs</c>.
/// </para>
/// </remarks>
public sealed partial class MainViewModel
{
    /// <summary>Opens (or focuses) the settings window; provided by the view.</summary>
    /// <remarks>A delegate so the view model never references an Avalonia window; set by <c>MainWindow</c>.</remarks>
    public Action? ShowSettings { get; set; }

    /// <summary>Opens the settings window (gear button, Ctrl+,).</summary>
    [RelayCommand]
    void OpenSettings() => ShowSettings?.Invoke();

    /// <summary>Interface language; changing it switches the UI live and saves the choice.</summary>
    /// <remarks>
    /// <see cref="Loc.SetLanguage"/> switches the static string tables (C# <c>Loc.S(es, en)</c>
    /// calls and XAML <c>{l:T Key}</c> lookups); <see cref="RefreshAllTexts"/> then makes the
    /// bindings of this view model re-read their localised computed texts. The setting is stored
    /// as a language code ("es"/"en").
    /// </remarks>
    public UiLanguage Language
    {
        get => Loc.Language;
        set
        {
            if (value == Loc.Language) return;
            Loc.SetLanguage(value);
            settings.Language = Loc.Code(value);
            settings.Save();
            RefreshAllTexts();
        }
    }

    /// <summary>Smallest and largest editor font sizes offered in the settings.</summary>
    public const double MinEditorFontSize = 10, MaxEditorFontSize = 26;

    /// <summary>Font size of the code editor (points); saved.</summary>
    /// <remarks>
    /// Also changed with Ctrl+mouse wheel in the editor. The stored value is optional
    /// (<c>null</c> = never changed → 14) and is clamped on read too, in case settings.json was
    /// edited by hand.
    /// </remarks>
    public double EditorFontSize
    {
        get => settings.EditorFontSize is double v ? Math.Clamp(v, MinEditorFontSize, MaxEditorFontSize) : 14;
        set
        {
            // Whole sizes only, so repeated wheel steps don't accumulate fractions.
            value = Math.Clamp(Math.Round(value), MinEditorFontSize, MaxEditorFontSize);
            if (value == EditorFontSize) return;
            settings.EditorFontSize = value;
            settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Wrap long lines in the code editor; saved.</summary>
    public bool EditorWordWrap
    {
        get => settings.EditorWordWrap;
        set
        {
            if (value == settings.EditorWordWrap) return;
            settings.EditorWordWrap = value;
            settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Folder holding settings.json (shown and opened from the settings window).</summary>
    public static string SettingsFolder => Path.GetDirectoryName(AppSettings.FilePath)!;

    /// <summary>
    /// After a language change: every computed text property (status, button labels, device
    /// names…) is re-read by the bindings. Messages already in the console keep their language.
    /// </summary>
    /// <remarks>
    /// <c>PropertyChanged</c> with an empty property name is the standard INotifyPropertyChanged
    /// signal for "all properties changed", so no list of localised properties has to be kept.
    /// Windows built in code (piano roll, settings…) are not covered by this: each one refreshes
    /// (or rebuilds) its own texts, if it does so at all.
    /// </remarks>
    public void RefreshAllTexts() => OnPropertyChanged(string.Empty);
}
