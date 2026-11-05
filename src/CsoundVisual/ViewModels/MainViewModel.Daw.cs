using System.Text.Json.Nodes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CsoundVisual.Engine;
using CsoundVisual.Integration;
using CsoundVisual.Localization;

namespace CsoundVisual.ViewModels;

/// <summary>
/// «Enviar al DAW / Send to DAW»: sends the shown orchestra to a «Csound Visual» VST3 plugin
/// running in Cubase (or any VST3 host) and keeps the app's sliders linked to it, so the same
/// orchestra you edit here plays inside your DAW project.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transport.</b> All communication goes through <see cref="VstBridge"/>, a small JSON-over-named-pipe
/// client: each running instance of the plug-in (<c>src/CsoundVST</c>) announces its pipe in a
/// descriptor file that <see cref="VstBridge.ListInstances"/> reads. The toolbar's
/// «Send to DAW» menu (built in <c>MainWindow.axaml.cs</c>) lists the instances found by
/// <see cref="FindDawTargets"/> and calls <see cref="SendToDawAsync"/> or <see cref="UnlinkDaw"/>.
/// </para>
/// <para>
/// <b>Link state.</b> After a successful send <see cref="LinkedDaw"/> holds the target. While it is
/// set: (1) every slider move is forwarded to the plug-in as well as to the app's own Csound
/// (<see cref="SendChannelValue"/>), coalesced per channel and flushed by a 33 ms timer; (2) a 40 ms
/// timer polls the plug-in's peaks and a mono scope buffer, and the output card shows those instead
/// of the app's engine (<see cref="IsDawLinked"/>, <see cref="OutputTitle"/>). Three consecutive
/// failed sends unlink automatically (the DAW closed the project or removed the plug-in).
/// </para>
/// <para>
/// <b>Threading.</b> Everything here runs on the UI thread: the commands come from menu clicks, the
/// timers are <see cref="DispatcherTimer"/>s, and the async IPC calls resume on the UI
/// synchronisation context. The meter data (<see cref="DawPeaks"/>, <see cref="DawScope"/>) are plain
/// properties, not observable: the main window polls <see cref="DawMetersVersion"/> once per frame
/// and repaints the scope/spectrum only when it changed.
/// </para>
/// </remarks>
public sealed partial class MainViewModel
{
    /// <summary>Plugin instance the sliders are linked to after a successful send (null = none).</summary>
    /// <remarks>
    /// <c>[ObservableProperty]</c> generates <c>LinkedDaw</c>; <c>[NotifyPropertyChangedFor]</c>
    /// makes its setter also notify the three computed properties that depend on it. The generated
    /// <c>OnLinkedDawChanged</c> hook starts/stops the meter polling.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DawLinkText), nameof(IsDawLinked), nameof(OutputTitle))]
    VstInstance? linkedDaw;

    /// <summary>True while the app is linked to a plugin in the DAW: the output card shows the plugin's audio.</summary>
    public bool IsDawLinked => LinkedDaw is not null;

    /// <summary>Title of the output card: «VST3 OUTPUT» while it shows the plugin in the DAW.</summary>
    public string OutputTitle => IsDawLinked ? Loc.S("SALIDA VST3", "VST3 OUTPUT") : Loc.S("SALIDA", "OUTPUT");

    /// <summary>
    /// Re-notifies the computed, language-dependent texts of this partial after a UI language
    /// switch. Called from <see cref="RefreshAudioTexts"/>, the view model's existing
    /// <see cref="Loc.Changed"/> handler (subscribed in the constructor).
    /// </summary>
    /// <remarks>
    /// Fix: <see cref="OutputTitle"/> is computed with <see cref="Loc.S"/> but was only re-notified
    /// when <see cref="LinkedDaw"/> changed, so the output card kept the old language («SALIDA»)
    /// after switching to English until the next DAW link/unlink.
    /// </remarks>
    void RefreshDawTexts() => OnPropertyChanged(nameof(OutputTitle));

    /// <summary>Latest peaks (L, R) received from the plugin, linear full scale (1.0 = 0 dBFS).</summary>
    public float[] DawPeaks { get; private set; } = [0, 0];

    /// <summary>Latest mono scope samples received from the plugin (2048, enough for the spectrum).</summary>
    /// <remarks>
    /// Same length as the engine's own scope ring (<see cref="CsoundEngine.ScopeSize"/>) so the
    /// oscilloscope and spectrum controls can take either source unchanged. A reply of another
    /// length is ignored and the previous buffer kept.
    /// </remarks>
    public float[] DawScope { get; private set; } = new float[CsoundEngine.ScopeSize];

    /// <summary>Sample rate of the plugin (the DAW's), in Hz; needed to label the spectrum's frequency axis.</summary>
    public double DawSampleRate { get; private set; } = 48000;

    /// <summary>Incremented whenever new meter data arrived (the window repaints the scope then).</summary>
    public int DawMetersVersion { get; private set; }

    /// <summary>40 ms (≈25 Hz) timer that polls the plug-in's meters while linked; created lazily on first link.</summary>
    DispatcherTimer? dawMeterTimer;

    /// <summary>
    /// True while a meter request is in flight. The timer keeps ticking during the await, so this
    /// prevents overlapping requests when the IPC round trip takes longer than 40 ms.
    /// </summary>
    bool dawMeterPolling;

    /// <summary>Polls the plugin's meters about 25 times a second while linked.</summary>
    /// <remarks>
    /// CommunityToolkit hook for <see cref="LinkedDaw"/>. On unlink it also zeroes the meter data and
    /// bumps <see cref="DawMetersVersion"/> so the window draws a silent scope instead of a frozen frame.
    /// <see cref="DispatcherPriority.Background"/> keeps the polling below input and rendering.
    /// </remarks>
    /// <param name="value">The new link target, or <c>null</c> when unlinked.</param>
    partial void OnLinkedDawChanged(VstInstance? value)
    {
        dawMeterTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, async (_, _) => await PollDawMetersAsync());
        if (value is null)
        {
            dawMeterTimer.Stop();
            DawPeaks = [0, 0];
            Array.Clear(DawScope);
            DawMetersVersion++;
        }
        else
            dawMeterTimer.Start();
    }

    /// <summary>
    /// One meter poll: asks the linked plug-in for its peaks, a scope buffer of
    /// <see cref="CsoundEngine.ScopeSize"/> samples and its sample rate, then publishes them.
    /// </summary>
    /// <remarks>Errors are swallowed: a missed frame is harmless and link loss is detected by <see cref="FlushDawValuesAsync"/>.</remarks>
    async Task PollDawMetersAsync()
    {
        if (dawMeterPolling || LinkedDaw is not { } target) return;
        dawMeterPolling = true;
        try
        {
            var (peaks, scope, sr) = await VstBridge.MetersAsync(target, CsoundEngine.ScopeSize);
            DawPeaks = peaks;
            if (scope.Length == CsoundEngine.ScopeSize) DawScope = scope;
            DawSampleRate = sr;
            DawMetersVersion++;
        }
        catch (Exception)
        {
            // A missed frame is fine; persistent failures are handled by the slider link.
        }
        finally
        {
            dawMeterPolling = false;
        }
    }

    /// <summary>Short status shown in the status bar while linked (empty when not linked).</summary>
    public string DawLinkText => LinkedDaw is { } d ? "⇄ " + d.Display : "";

    /// <summary>
    /// Latest value per channel waiting to be sent to the plug-in. Coalescing: a slider dragged
    /// through 50 values between two flushes sends only its last value. Guarded by its own lock
    /// (defensive; all current callers are on the UI thread).
    /// </summary>
    readonly Dictionary<string, double> pendingDawValues = [];

    /// <summary>33 ms (≈30 Hz) flush timer for <see cref="pendingDawValues"/>; it stops itself when nothing is pending.</summary>
    DispatcherTimer? dawTimer;

    /// <summary>True while a batch is being sent; a timer tick that arrives meanwhile does nothing (values keep accumulating).</summary>
    bool dawSending;

    /// <summary>Consecutive failed flushes; at 3 the sliders are unlinked. Reset on any success and on a new send.</summary>
    int dawFailures;

    /// <summary>Running plugin instances (for the toolbar menu).</summary>
    /// <returns>The instances that answered <see cref="VstBridge.ListInstances"/> (synchronous, quick local discovery).</returns>
    public List<VstInstance> FindDawTargets() => VstBridge.ListInstances();

    /// <summary>
    /// Sends the shown document to <paramref name="target"/>: the plugin compiles it and swaps it
    /// in without stopping the DAW. Csound's compile messages come back into the console.
    /// </summary>
    /// <param name="target">The plug-in instance chosen in the toolbar menu.</param>
    /// <remarks>
    /// Steps: make sound-file paths absolute (<see cref="AbsolutizeFilePaths"/>), replace
    /// Cabbage-only opcodes with stand-ins, send, echo the last 40 lines of the plug-in's Csound log,
    /// and on success link the sliders (<see cref="LinkedDaw"/>) and push every current slider value
    /// so the plug-in starts from the same settings as the app. The reply is a JSON object with
    /// <c>ok</c>, <c>log</c> and <c>channels</c>. Never throws: errors go to the console.
    /// </remarks>
    public async Task SendToDawAsync(VstInstance target)
    {
        if (SelectedDocument is not { } doc) return;
        AddInfo(Loc.S($"⇢ Enviando {doc.FileName} a {target.Display}…", $"⇢ Sending {doc.FileName} to {target.Display}…"));
        try
        {
            // The plugin runs inside the DAW, whose current folder is not the .csd's: relative sound
            // file names would not be found there. Make them absolute first.
            string csd = AbsolutizeFilePaths(doc.Document.Text, doc.FilePath, out int converted);
            csd = Cabbage.CabbageSupport.PrepareForCsound(csd, out _);   // Cabbage-only opcodes → stand-ins
            if (converted > 0)
                AddInfo(Loc.S($"  {converted} ruta(s) de archivos de sonido convertidas a absolutas para el plugin.",
                              $"  {converted} sound-file path(s) made absolute for the plug-in."));
            var reply = await VstBridge.LoadAsync(target, doc.FileName, csd);
            if (reply["log"]?.GetValue<string>() is { Length: > 0 } log)
                foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(40))
                    ConsoleLines.Add(new ConsoleLine("  [VST] " + line.TrimEnd(), MessageKind.Info));

            if (reply["ok"]?.GetValue<bool>() == true)
            {
                // Copy of the instance record that remembers which document it now plays (shown in DawLinkText).
                LinkedDaw = target with { CsdName = doc.FileName };
                dawFailures = 0;
                var channels = reply["channels"] is JsonArray a ? string.Join(", ", a.Select(n => n?.ToString())) : "";
                AddInfo(Loc.S($"✓ Sonando en el DAW. Los sliders de la app ahora controlan el plugin ({channels}).",
                              $"✓ Playing in the DAW. The app's sliders now control the plugin ({channels})."));
                // Push the current slider values so the plugin starts where the app is.
                foreach (var ch in doc.Channels)
                    QueueDawValue(ch.Name, ch.Value);
            }
            else
                ConsoleLines.Add(new ConsoleLine(Loc.S("✗ El plugin no pudo compilar la orquesta (ver mensajes arriba).",
                                                       "✗ The plugin could not compile the orchestra (see messages above)."), MessageKind.Error));
        }
        catch (Exception ex)
        {
            ConsoleLines.Add(new ConsoleLine(Loc.S($"✗ No se pudo hablar con el plugin: {ex.Message}", $"✗ Could not talk to the plugin: {ex.Message}"), MessageKind.Error));
        }
    }

    /// <summary>
    /// Replaces every quoted relative file name in <paramref name="csd"/> that exists next to the
    /// document (e.g. <c>"../Manual/fox.wav"</c>) by its absolute path with forward slashes, which
    /// Csound accepts on Windows. Names that do not resolve to an existing file are left alone, so
    /// channel names and other strings are never touched.
    /// </summary>
    /// <param name="csd">Full text of the document.</param>
    /// <param name="documentPath">Path of the document on disk, or <c>null</c> for an unsaved one (nothing is converted then).</param>
    /// <param name="converted">Number of file names that were rewritten.</param>
    /// <returns>The text with the resolvable relative names made absolute.</returns>
    /// <remarks>
    /// The pattern takes any double-quoted string without line breaks that ends in a 2–5 character
    /// extension (<c>"x.wav"</c>, <c>"dir/y.sf2"</c>). The file-existence test is what makes this
    /// safe; <c>#include</c> files are converted too, but their own relative names are not.
    /// </remarks>
    static string AbsolutizeFilePaths(string csd, string? documentPath, out int converted)
    {
        converted = 0;
        if (documentPath is null || Path.GetDirectoryName(documentPath) is not { } folder)
            return csd;
        int count = 0;
        string result = System.Text.RegularExpressions.Regex.Replace(csd, @"""([^""\r\n]+\.[A-Za-z0-9]{2,5})""", m =>
        {
            string name = m.Groups[1].Value;
            if (Path.IsPathRooted(name)) return m.Value;
            try
            {
                string full = Path.GetFullPath(Path.Combine(folder, name));
                if (!File.Exists(full)) return m.Value;
                count++;
                return "\"" + full.Replace('\\', '/') + "\"";
            }
            catch (Exception)
            {
                return m.Value; // invalid path characters etc.: leave the string as written
            }
        });
        converted = count;
        return result;
    }

    /// <summary>Stops sending slider moves to the DAW (the plug-in keeps playing its orchestra; only the link is dropped).</summary>
    public void UnlinkDaw()
    {
        LinkedDaw = null;
        lock (pendingDawValues) pendingDawValues.Clear();
    }

    /// <summary>
    /// Every slider move goes through here: to the running Csound of the app, and — when linked —
    /// to the plugin in the DAW (coalesced, at most ~30 messages per second per channel).
    /// </summary>
    /// <param name="channel">Csound channel name (<c>chnget</c>/<c>chnk</c>).</param>
    /// <param name="value">New value in the channel's own units (not normalised).</param>
    /// <remarks>
    /// Passed as a delegate to every <see cref="DocumentViewModel"/> (see <c>MainViewModel.cs</c>), whose
    /// <see cref="ChannelControlViewModel"/>s call it on each change — sliders, MIDI learn, Cabbage
    /// widgets and editor scrubbing all end up here. <c>engine.SetControl</c> only queues the value
    /// for the performance thread, so this is cheap and safe from the UI thread.
    /// </remarks>
    void SendChannelValue(string channel, double value)
    {
        engine.SetControl(channel, value);
        if (LinkedDaw is not null)
            QueueDawValue(channel, value);
    }

    /// <summary>Records the latest <paramref name="value"/> for <paramref name="channel"/> and makes sure the flush timer runs.</summary>
    /// <param name="channel">Channel name.</param>
    /// <param name="value">Value to send.</param>
    /// <remarks>Must be called on the UI thread: it creates/starts a <see cref="DispatcherTimer"/>.</remarks>
    void QueueDawValue(string channel, double value)
    {
        lock (pendingDawValues) pendingDawValues[channel] = value;
        dawTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, async (_, _) => await FlushDawValuesAsync());
        dawTimer.Start();
    }

    /// <summary>
    /// Timer tick: takes a snapshot of <see cref="pendingDawValues"/> and sends it to the linked
    /// plug-in, one IPC message per channel, awaited in sequence.
    /// </summary>
    /// <remarks>
    /// The timer stops itself when there is nothing to send (or nothing is linked) and is restarted
    /// by the next <see cref="QueueDawValue"/>, so it costs nothing while the sliders are idle. A
    /// failed batch is not retried: newer values will follow anyway.
    /// </remarks>
    async Task FlushDawValuesAsync()
    {
        if (dawSending) return; // previous batch still in flight: values stay queued for the next tick
        KeyValuePair<string, double>[] batch;
        lock (pendingDawValues)
        {
            batch = [.. pendingDawValues];
            pendingDawValues.Clear();
        }
        if (batch.Length == 0 || LinkedDaw is not { } target)
        {
            dawTimer?.Stop();
            return;
        }
        dawSending = true;
        try
        {
            foreach (var (channel, value) in batch)
                await VstBridge.SetAsync(target, channel, value);
            dawFailures = 0;
        }
        catch (Exception)
        {
            // The DAW closed the project or removed the plugin: give up after a few misses.
            if (++dawFailures >= 3)
            {
                AddInfo(Loc.S("El plugin del DAW ya no responde; se desvincularon los sliders.", "The DAW plugin no longer answers; sliders unlinked."));
                UnlinkDaw();
            }
        }
        finally
        {
            dawSending = false;
        }
    }
}
