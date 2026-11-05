using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using CsoundVisual.Localization;

namespace CsoundVisual.ViewModels;

/// <summary>
/// Services used by the 3D window: opening it, driving channels from the XYZ pad and the
/// spatial panner, and opening the bundled templates (e.g. the binaural one).
/// </summary>
/// <remarks>
/// <para>
/// The 3D scenes (<c>Visual3D/</c>, hosted by <c>Views/Visual3DWindow.cs</c>) never talk to the
/// engine directly: they go through these methods, which act on the sliders of the
/// <i>selected</i> document (<see cref="SelectedDocument"/>). Writing a slider's
/// <see cref="ChannelControlViewModel.Value"/> moves the side-panel slider too and reaches
/// Csound only if that document is the one playing (see <see cref="DocumentViewModel"/>).
/// </para>
/// <para>
/// <b>Units.</b> The pad works in normalised 0…1 coordinates per axis; the mapping to channel
/// units is linear, or exponential (equal ratios per equal distance, like a frequency knob)
/// when the channel was declared exponential and its minimum is positive (a log mapping is
/// undefined at or below zero). All methods run on the UI thread (pointer handlers and the 3D
/// window's timer).
/// </para>
/// </remarks>
public sealed partial class MainViewModel
{
    /// <summary>Opens (or focuses) the 3D window; provided by the view.</summary>
    public Action? ShowVisual3D { get; set; }

    /// <summary>Opens the detachable 3D window (toolbar «3D», Ctrl+3; generates <c>Open3DCommand</c>).</summary>
    [RelayCommand]
    void Open3D() => ShowVisual3D?.Invoke();

    /// <summary>
    /// Sets a channel from a normalised 0…1 position (XYZ pad): mapped onto the slider's range
    /// (exponentially for exponential channels). Returns the value text for display.
    /// </summary>
    /// <param name="channel">Channel name bound to one pad axis.</param>
    /// <param name="t">Normalised position 0…1 along that axis (expected in range; not clamped here).</param>
    /// <returns>The slider's <see cref="ChannelControlViewModel.DisplayValue"/>, or "—" when the selected document has no such channel.</returns>
    public string SetChannelNormalized(string channel, float t)
    {
        if (SelectedDocument?.FindChannel(channel) is not { } slider)
            return "—";
        // Exponential: min·(max/min)^t, so t = 0.5 lands on the geometric mean (e.g. 20…20000 Hz → 632 Hz).
        double v = slider.Exponential && slider.Minimum > 0
            ? slider.Minimum * Math.Pow(slider.Maximum / slider.Minimum, t)
            : slider.Minimum + t * (slider.Maximum - slider.Minimum);
        slider.Value = v;
        return slider.DisplayValue;
    }

    /// <summary>
    /// Inverse of <see cref="SetChannelNormalized"/>: where a slider currently sits in its range
    /// (0…1), or <c>null</c> when the shown document has no such channel. Lets the XYZ pad start
    /// at the sliders' real positions instead of jumping them to the sphere's old position.
    /// </summary>
    /// <param name="channel">Channel name.</param>
    /// <returns>Position 0…1 (clamped), or <c>null</c> for a missing channel or an empty range.</returns>
    public float? GetChannelNormalized(string channel)
    {
        // An empty or inverted range would divide by zero (or flip the axis): report "unknown".
        if (SelectedDocument?.FindChannel(channel) is not { } slider || slider.Maximum <= slider.Minimum)
            return null;
        // Log inverse of the exponential mapping; it needs a positive current value too.
        double t = slider.Exponential && slider.Minimum > 0 && slider.Value > 0
            ? Math.Log(slider.Value / slider.Minimum) / Math.Log(slider.Maximum / slider.Minimum)
            : (slider.Value - slider.Minimum) / (slider.Maximum - slider.Minimum);
        return (float)Math.Clamp(t, 0, 1);
    }

    /// <summary>
    /// Sends a raw value to a channel: through its slider when the document declares it,
    /// otherwise straight to the engine while playing (used by the spatial panner: az, el, dist).
    /// </summary>
    /// <param name="channel">Channel name.</param>
    /// <param name="value">Value in channel units (degrees for az/el, distance units for dist).</param>
    /// <remarks>
    /// The fallback lets the panner drive an orchestra that reads the channel with
    /// <c>chnget</c> without the analyzer having produced a slider for it. Note that the slider
    /// path only reaches Csound when the selected document is the one playing, whereas the raw
    /// path goes to whatever performance is running.
    /// </remarks>
    public void SendChannel(string channel, double value)
    {
        if (SelectedDocument?.FindChannel(channel) is { } slider)
            slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        else if (IsRunning)
            engine.SetControl(channel, value); // queued; applied by the performance thread next k-cycle
    }

    /// <summary>Opens a bundled template (<c>Assets/&lt;name&gt;.csd</c>) in a new tab.</summary>
    /// <param name="name">Template name without extension, e.g. "Binaural3D" or "DrumKit".</param>
    /// <remarks>
    /// Templates are embedded resources (see the csproj), named
    /// <c>CsoundVisual.Assets.&lt;name&gt;.csd</c> by MSBuild. The new tab is untitled but shows
    /// <c>&lt;name&gt;.csd</c> and is not dirty, so «Save» proposes that name and the embedded
    /// original can never be overwritten. Unlike opening a file, this does not switch playback.
    /// </remarks>
    public void OpenTemplate(string name)
    {
        using var s = typeof(MainViewModel).Assembly.GetManifestResourceStream($"CsoundVisual.Assets.{name}.csd");
        if (s is null)
        {
            AddInfo(Loc.S($"No se encontró la plantilla {name}.", $"Template {name} not found."));
            return;
        }
        var doc = AddDocument(new StreamReader(s).ReadToEnd(), null);
        doc.UntitledName = name + ".csd";
        doc.IsDirty = false;
        SelectedDocument = doc;
        AddInfo(Loc.S(string.Create(CultureInfo.InvariantCulture, $"Plantilla «{name}» abierta. Presiona Tocar."),
                      string.Create(CultureInfo.InvariantCulture, $"Template «{name}» opened. Press Play.")));
    }
}
