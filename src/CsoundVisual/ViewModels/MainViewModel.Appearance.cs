using CommunityToolkit.Mvvm.ComponentModel;
using CsoundVisual.Theming;

namespace CsoundVisual.ViewModels;

/// <summary>
/// «Apariencia» card: the colour preset of the whole app. Picking one applies it live (see
/// <see cref="AppTheme"/>) and remembers it for the next start.
/// </summary>
/// <remarks>
/// The picker lives in the side panel's appearance card and in the settings window. The state
/// is not stored in the view model: <see cref="AppTheme"/> is the single source of truth (it
/// recolours a fixed set of shared brushes that the XAML reads through <c>DynamicResource</c>),
/// and this property is only a bindable façade over it. The preset's <i>name</i> is what is
/// persisted (<c>AppSettings.ThemePreset</c>); it is an id and is never translated — the
/// picker shows the preset's localised display name instead.
/// </remarks>
public sealed partial class MainViewModel
{
    /// <summary>Available presets (name + colour swatches) for the picker.</summary>
    public IReadOnlyList<ThemePreset> ThemePresets => AppTheme.Presets;

    /// <summary>The active preset; setting it recolours the app and saves the choice.</summary>
    /// <remarks>
    /// Hand-written (not <c>[ObservableProperty]</c>) because the value lives in
    /// <see cref="AppTheme.Current"/>. A <c>null</c> coming from the ComboBox while its items
    /// are being rebuilt is ignored.
    /// </remarks>
    public ThemePreset SelectedThemePreset
    {
        get => AppTheme.Current;
        set
        {
            if (value is null || value == AppTheme.Current) return;
            AppTheme.Apply(value.Name); // recolours every themed brush immediately (UI thread)
            settings.ThemePreset = value.Name;
            settings.Save();
            OnPropertyChanged(); // [CallerMemberName] → "SelectedThemePreset"
        }
    }
}
