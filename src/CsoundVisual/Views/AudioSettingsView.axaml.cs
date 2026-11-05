using Avalonia.Controls;

namespace CsoundVisual.Views;

/// <summary>
/// Audio settings panel shown in the settings window: engine (WASAPI shared/exclusive,
/// PortAudio…), output device, latency preset, ksmps override, Windows Sound panel and the audio
/// diagnostics. Bound to <see cref="ViewModels.MainViewModel"/>.
/// </summary>
/// <remarks>
/// All the logic lives in the view model (<c>MainViewModel.Audio.cs</c>); the layout and its
/// compiled bindings are in <c>AudioSettingsView.axaml</c>. The hosting
/// <see cref="SettingsWindow"/> creates it with <c>DataContext = vm</c> (the
/// <see cref="ViewModels.MainViewModel"/>), so this code-behind only loads the AXAML.
/// </remarks>
public partial class AudioSettingsView : UserControl
{
    /// <summary>Creates the view and loads its AXAML (generated <c>InitializeComponent</c>).</summary>
    public AudioSettingsView() => InitializeComponent();
}
