using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CsoundVisual.Localization;
using CsoundVisual.Music;
using CsoundVisual.Theming;
using CsoundVisual.ViewModels;

namespace CsoundVisual.Views;

/// <summary>
/// Play/stop logic shared by the piano roll and the step sequencer windows: owns a
/// <see cref="TransportScheduler"/> connected to the app's engine, starts Csound when the tool's
/// Play is pressed while Csound is stopped, and reports the playhead and what happened.
/// UI thread only (the scheduler itself runs on its own thread).
/// </summary>
/// <remarks>
/// <para>
/// <b>How sound is produced.</b> The tools never synthesise anything: the
/// <see cref="TransportScheduler"/> thread reads Csound's score clock and queues <c>i</c> events
/// ~120 ms ahead through <see cref="EngineScoreTarget"/> → <c>CsoundEngine.SendEventAt</c>. So
/// Csound must be in Play mode (<see cref="MainViewModel.IsPlayingLive"/>); when it is not,
/// <see cref="Play"/> starts it on the chosen document and enters a <i>pending start</i> state
/// that <see cref="OnTick"/> resolves once the performance is really running.
/// </para>
/// <para>
/// <b>Units.</b> Positions are <i>timeline beats</i> (quarter notes from the start of the
/// pattern/roll); <see cref="Bpm"/> converts them to seconds of Csound score time.
/// </para>
/// <para>
/// <b>Polling.</b> A 30 ms <see cref="DispatcherTimer"/> at <see cref="DispatcherPriority.Render"/>
/// drives the playhead (<see cref="Tick"/>), the pending-start state machine, the late-note
/// warning and Ableton Link following (<c>ToolTransport.Link.cs</c>). Polling is used instead of
/// events because the score clock lives on the engine thread and is read as a snapshot.
/// </para>
/// <para>
/// One instance per tool window (<see cref="PianoRollWindow"/>, <see cref="StepSequencerWindow"/>);
/// the window disposes it when it closes. <see cref="ToolUi"/> below holds the small control
/// factories both windows use.
/// </para>
/// </remarks>
public sealed partial class ToolTransport : IDisposable
{
    /// <summary>The app's view model: gives the engine, the run mode and document start helpers.</summary>
    readonly MainViewModel vm;

    /// <summary>Look-ahead note scheduler (own thread) that turns the pattern into timed Csound events.</summary>
    readonly TransportScheduler scheduler;

    /// <summary>30 ms UI-thread poll timer (see <see cref="OnTick"/>); runs for the life of the window.</summary>
    readonly DispatcherTimer timer;

    /// <summary>
    /// True between a ▶ that had to start Csound first and the moment the performance runs
    /// (or the attempt is given up). <see cref="IsPlaying"/> already reports true meanwhile so the
    /// window's ▶ button shows the playing state immediately.
    /// </summary>
    bool pendingStart;

    /// <summary>UTC time the pending start began; used for the 0.5 s / 10 s give-up timeouts.</summary>
    DateTime pendingSince;

    /// <summary>Timeline beat to start from once the pending start resolves.</summary>
    double pendingFrom;

    /// <summary>
    /// Value of <see cref="MainViewModel.ToolLateEvents"/> already reported; the engine counter only
    /// grows, so the difference is the number of new late (skipped) notes to warn about.
    /// </summary>
    int lateSeen;

    /// <summary>Creates the transport for <paramref name="viewModel"/> playing <paramref name="source"/>.</summary>
    /// <param name="viewModel">The main view model (engine access, run mode, playback helpers).</param>
    /// <param name="source">Initial immutable pattern snapshot to play.</param>
    public ToolTransport(MainViewModel viewModel, IPatternSource source)
    {
        vm = viewModel;
        // The target writes into the app's engine; the delegate lets the scheduler stop when
        // Csound is no longer performing live.
        scheduler = new TransportScheduler(new EngineScoreTarget(vm.Engine, () => vm.IsPlayingLive), source);
        // Raised on the scheduler thread: marshal to the UI thread before touching listeners.
        // engineStillRunning = true means the (non-looping) pattern ended; false means Csound stopped.
        scheduler.StoppedByItself += engineStillRunning => Dispatcher.UIThread.Post(() =>
        {
            Message?.Invoke(engineStillRunning
                ? Loc.S("■ Fin: se llegó al final (activa el loop para repetir).", "■ End reached (turn the loop on to repeat).")
                : Loc.S("■ Csound se detuvo, así que el transporte también. Si la partitura del documento terminó, agrega un evento largo (p. ej. «i 100 0 3600» o «f 0 3600»).",
                        "■ Csound stopped, so the transport stopped too. If the document's score ended, add a long event (e.g. «i 100 0 3600» or «f 0 3600»)."), !engineStillRunning);
            StateChanged?.Invoke();
        });
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, (_, _) => OnTick());
        timer.Start();
    }

    /// <summary>Raised when playing/stopped changes.</summary>
    public event Action? StateChanged;

    /// <summary>Raised with a message for the tool's status line (text, is-warning).</summary>
    public event Action<string, bool>? Message;

    /// <summary>Raised ~33 times per second with the playhead (timeline beats; <c>null</c> when stopped).</summary>
    public event Action<double?>? Tick;

    /// <summary>True while playing, or while waiting for Csound to start.</summary>
    public bool IsPlaying => scheduler.IsPlaying || pendingStart;

    /// <summary>Tempo (BPM); may change while playing.</summary>
    public double Bpm
    {
        get => scheduler.Bpm;
        set
        {
            scheduler.Bpm = value;
            PushLinkTempo(value); // Ableton Link: the session follows (ToolTransport.Link.cs)
        }
    }

    /// <summary>The pattern played (replace it after every edit with a fresh snapshot).</summary>
    public IPatternSource Source
    {
        get => scheduler.Source;
        set => scheduler.Source = value;
    }

    /// <summary>Timeline beat sounding at score time <paramref name="scoreTime"/> (MIDI recording), or <c>null</c>.</summary>
    /// <param name="scoreTime">Csound score time in seconds (e.g. the time a MIDI note arrived).</param>
    /// <returns>The timeline beat (loop-folded by the scheduler), or <c>null</c> when not playing.</returns>
    public double? BeatAt(double scoreTime) => scheduler.TimelineBeatAt(scoreTime);

    /// <summary>
    /// Starts playing from <paramref name="fromBeat"/>. If Csound is not playing, it is started on
    /// <paramref name="doc"/> first and the transport follows as soon as the performance runs.
    /// </summary>
    /// <param name="doc">Document to start Csound with when it is stopped (ignored if Csound already plays).</param>
    /// <param name="fromBeat">Timeline beat to start from.</param>
    /// <remarks>
    /// Decision order: restart if already playing → refuse while rendering to WAV → start the
    /// scheduler immediately if Csound is live → otherwise ask the view model to start Csound
    /// (<see cref="MainViewModel.StartPlaybackFor"/>) and enter the pending-start state, or show the
    /// «press Play» hint when that is impossible.
    /// </remarks>
    public void Play(DocumentViewModel? doc, double fromBeat)
    {
        if (IsPlaying)
        {
            restarting = true; // a restart must not stop the Link session
            Stop();
            restarting = false;
        }
        RememberPlay(doc, fromBeat);
        if (vm.Mode == RunMode.Render)
        {
            Message?.Invoke(Loc.S("Csound está renderizando a WAV; espera a que termine.", "Csound is rendering to WAV; wait until it finishes."), true);
            return;
        }
        if (vm.IsPlayingLive)
        {
            // Csound already performs: start right away (only late notes from now on are reported).
            lateSeen = vm.ToolLateEvents;
            PrepareSync();
            scheduler.Start(fromBeat);
            Message?.Invoke(Loc.S($"▶ Tocando con Csound ({vm.PlayingDocument?.FileName}).", $"▶ Playing through Csound ({vm.PlayingDocument?.FileName})."), false);
            AnnounceLinkStart();
            StateChanged?.Invoke();
            return;
        }
        if (doc is null)
        {
            Message?.Invoke(Loc.S("No hay documento abierto.", "No document is open."), true);
            return;
        }
        // Csound must be in Play mode for anything to sound: start it on the target document.
        if (!vm.StartPlaybackFor(doc))
        {
            vm.HintPressPlay();
            Message?.Invoke(Loc.S("Para que Csound suene debe estar en modo Tocar (F5 en la ventana principal).",
                                  "Csound must be in Play mode to make sound (F5 in the main window)."), true);
            return;
        }
        // Csound is compiling/starting asynchronously; OnTick starts the scheduler when it runs.
        pendingStart = true;
        pendingSince = DateTime.UtcNow;
        pendingFrom = fromBeat;
        Message?.Invoke(Loc.S($"Csound no estaba en Tocar: iniciándolo con {doc.FileName}…", $"Csound was not in Play mode: starting it with {doc.FileName}…"), false);
        StateChanged?.Invoke();
    }

    /// <summary>Stops the transport (Csound keeps playing; notes already started finish).</summary>
    /// <remarks>Also cancels a pending start and clears the playhead (<see cref="Tick"/> with <c>null</c>).</remarks>
    public void Stop()
    {
        bool was = IsPlaying;
        pendingStart = false;
        scheduler.Stop();
        PropagateLinkStop(was);
        Tick?.Invoke(null);
        if (was) StateChanged?.Invoke();
    }

    /// <summary>
    /// 30 ms poll (UI thread): follows Ableton Link, resolves a pending start, publishes the playhead
    /// and reports newly late notes.
    /// </summary>
    /// <remarks>
    /// Pending-start state machine: <i>pending</i> → <i>playing</i> once Csound is live and its score
    /// clock has advanced past 20 ms (so the clock mapping is valid; the scheduler gets a 0.1 s
    /// lead-in to avoid late first notes) → or <i>given up</i> when Csound is not running 0.5 s after
    /// the request (compile error) or has not started after 10 s.
    /// </remarks>
    void OnTick()
    {
        FollowLink();
        if (pendingStart)
        {
            if (vm.IsPlayingLive && vm.Engine.ScoreTime > 0.02)
            {
                pendingStart = false;
                lateSeen = vm.ToolLateEvents;
                PrepareSync();
                scheduler.Start(pendingFrom, leadIn: 0.1);
                Message?.Invoke(Loc.S($"▶ Tocando con Csound ({vm.PlayingDocument?.FileName}).", $"▶ Playing through Csound ({vm.PlayingDocument?.FileName})."), false);
                AnnounceLinkStart();
                StateChanged?.Invoke();
            }
            else if ((!vm.IsRunning && DateTime.UtcNow - pendingSince > TimeSpan.FromSeconds(0.5)) ||
                     DateTime.UtcNow - pendingSince > TimeSpan.FromSeconds(10))
            {
                pendingStart = false;
                Message?.Invoke(Loc.S("Csound no pudo empezar a tocar: revisa la consola de la ventana principal.",
                                      "Csound could not start playing: check the console in the main window."), true);
                StateChanged?.Invoke();
            }
            return;
        }
        if (!scheduler.IsPlaying) return;
        Tick?.Invoke(scheduler.TimelineBeatAt(vm.Engine.ScoreTime));
        int late = vm.ToolLateEvents;
        if (late > lateSeen)
        {
            Message?.Invoke(Loc.S($"⚠ {late - lateSeen} notas llegaron tarde y se omitieron (¿CPU ocupada?).",
                                  $"⚠ {late - lateSeen} notes arrived late and were skipped (CPU busy?)."), true);
            lateSeen = late;
        }
    }

    /// <summary>Stops the poll timer and the scheduler thread (notes already sent keep sounding).</summary>
    public void Dispose()
    {
        timer.Stop();
        scheduler.Dispose();
    }
}

/// <summary>Small factory helpers so the tool windows (built in code) look like the rest of the app.</summary>
/// <remarks>
/// The tool windows are built in C# rather than AXAML, so these helpers apply the same style
/// classes (<c>tool</c>, <c>primary</c>, <c>dim</c>…) and theme brushes (<see cref="AppTheme"/>)
/// that <c>Styles/Theme.axaml</c> defines for the main window. Icons are <see cref="Geometry"/>
/// resources looked up by key (e.g. <c>IconPlay</c>) in the application resources.
/// </remarks>
public static class ToolUi
{
    /// <summary>A toolbar-style button with a tooltip (and optional icon from the app's icon set).</summary>
    /// <param name="text">Caption (already localised); may be empty for an icon-only button.</param>
    /// <param name="tip">Tooltip text.</param>
    /// <param name="click">Action run on click.</param>
    /// <param name="icon">Resource key of a <see cref="Geometry"/> icon, or null.</param>
    /// <param name="primary">Adds the accent-coloured <c>primary</c> class.</param>
    /// <returns>The new button.</returns>
    public static Button Button(string text, string tip, Action click, string? icon = null, bool primary = false)
    {
        var b = new Button { Classes = { "tool" }, Content = Content(text, icon) };
        if (primary) b.Classes.Add("primary");
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A toggle button styled like the toolbar buttons.</summary>
    /// <remarks>No style class is added; padding and corner radius are set inline to match the <c>tool</c> buttons next to it.</remarks>
    /// <param name="text">Caption.</param>
    /// <param name="tip">Tooltip text.</param>
    /// <param name="icon">Optional icon resource key.</param>
    /// <returns>The toggle button (the caller subscribes to its <c>IsChecked</c> changes).</returns>
    public static ToggleButton Toggle(string text, string tip, string? icon = null)
    {
        var b = new ToggleButton { Content = Content(text, icon), Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(b, tip);
        return b;
    }

    /// <summary>Icon + text content.</summary>
    /// <param name="text">Caption; omitted from the panel when empty.</param>
    /// <param name="icon">Icon resource key, or null.</param>
    /// <returns>
    /// The plain string when there is no icon (or the key is not a <see cref="Geometry"/>
    /// resource), otherwise a horizontal panel with a 14 px <see cref="PathIcon"/> and the text.
    /// </returns>
    public static object Content(string text, string? icon)
    {
        if (icon is null || Application.Current?.TryFindResource(icon, out var res) != true || res is not Geometry g)
            return text;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        panel.Children.Add(new PathIcon { Data = g, Width = 14, Height = 14 });
        if (text.Length > 0) panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    /// <summary>A dim caption.</summary>
    /// <param name="text">Caption text.</param>
    /// <param name="tip">Optional tooltip.</param>
    /// <returns>A 12 px text block in the theme's <c>TextDim</c> brush.</returns>
    public static TextBlock Label(string text, string? tip = null)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("TextDim"), FontSize = 12 };
        if (tip is not null) ToolTip.SetTip(t, tip);
        return t;
    }

    /// <summary>
    /// A numeric box (integer by default). Without <paramref name="spinner"/> the up/down buttons are
    /// hidden to save room (the value still changes with ↑/↓ and the mouse wheel, or by typing).
    /// </summary>
    /// <param name="min">Minimum value (typed values are clipped).</param>
    /// <param name="max">Maximum value.</param>
    /// <param name="value">Initial value.</param>
    /// <param name="step">Increment per arrow key / wheel notch.</param>
    /// <param name="tip">Tooltip text.</param>
    /// <param name="format">.NET format string for the displayed value (default integer).</param>
    /// <param name="width">Fixed width in DIP (also the minimum, so the tool bar never reflows).</param>
    /// <param name="spinner">Whether to show the up/down spinner buttons.</param>
    /// <returns>The numeric box; <see cref="NumericUpDown"/> works in <see cref="decimal"/>, hence the casts.</returns>
    public static NumericUpDown Number(double min, double max, double value, double step, string tip, string format = "0", double width = 96, bool spinner = true)
    {
        var n = new NumericUpDown
        {
            Minimum = (decimal)min, Maximum = (decimal)max, Value = (decimal)value, Increment = (decimal)step,
            FormatString = format, Width = width, MinWidth = width, VerticalAlignment = VerticalAlignment.Center,
            ClipValueToMinMax = true, AllowSpin = true, ShowButtonSpinner = spinner,
        };
        ToolTip.SetTip(n, tip);
        return n;
    }

    /// <summary>A thin vertical separator for tool bars.</summary>
    public static Border Separator() =>
        new() { Width = 1, Height = 24, Margin = new Thickness(6, 0), Background = AppTheme.Brush("Line"), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Wraps a control with a caption on its left.</summary>
    /// <param name="caption">Caption text (rendered with <see cref="Label"/>).</param>
    /// <param name="control">The control being captioned.</param>
    /// <param name="tip">Optional tooltip on the caption.</param>
    /// <returns>A horizontal stack panel holding caption and control.</returns>
    public static StackPanel Captioned(string caption, Control control, string? tip = null) =>
        new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { Label(caption, tip), control } };

    /// <summary>The «Csound must be playing» explanation shared by both tools' help lines.</summary>
    public static string PlayExplanation => Loc.S(
        "Los sonidos los hace Csound: si no está en Tocar, el botón ▶ de esta ventana lo inicia con el documento elegido.",
        "Csound makes the sound: if it is not in Play mode, this window's ▶ button starts it with the chosen document.");
}
