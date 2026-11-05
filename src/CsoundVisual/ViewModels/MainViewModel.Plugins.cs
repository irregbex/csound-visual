using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using CsoundVisual.Engine;
using CsoundVisual.Integration;
using CsoundVisual.Localization;

namespace CsoundVisual.ViewModels;

/// <summary>
/// VST3 host (roadmap item 22): the chain of the user's VST3 plug-ins that processes Csound's
/// output in the app's «Windows · WASAPI» engine, its persistence and the window that edits it.
/// </summary>
/// <remarks>
/// The view calls <see cref="InitPluginHost"/> once the main window is open (the saved chain is
/// then restored in the background of the UI thread, so the window appears first) and
/// <see cref="ShutdownPluginHost"/> when it closes (the chain and each plug-in's state are saved).
/// <para>
/// <b>Threads.</b> Three threads touch the chain: the UI thread (edits, editors, saving), the
/// Csound performance thread (runs the chain's <c>Process</c> on each block, via
/// <see cref="CsoundEngine.Inserts"/>), and MIDI driver threads (<see cref="VstChain.SendMidi"/>).
/// <see cref="VstChain"/> itself handles the hand-offs; this partial only wires them up.
/// </para>
/// <para>
/// <b>Persistence.</b> The chain (plug-in paths, order, bypass, opaque state blobs) is stored in
/// <see cref="AppSettings.PluginChain"/> and rewritten on every <see cref="VstChain.Changed"/>.
/// The blacklist and crash recovery are handled by <see cref="VstPluginManager"/>.
/// </para>
/// </remarks>
public sealed partial class MainViewModel
{
    /// <summary>Set by the first <see cref="InitPluginHost"/> call; guards against double initialisation and against saving before the host is wired.</summary>
    bool pluginHostReady;

    /// <summary>Polls the plug-ins' restart requests (see <see cref="VstChain.PollRestartRequests"/>); started by <see cref="InitPluginHost"/>.</summary>
    DispatcherTimer? restartTimer;

    /// <summary>The insert chain (UI thread owns it; the engine runs it on the audio thread).</summary>
    public VstChain PluginChain { get; } = new();

    /// <summary>Opens (or focuses) the plug-in chain window; provided by the view.</summary>
    public Action? ShowPluginChain { get; set; }

    /// <summary>VST3 plug-ins after Csound (Ctrl+Shift+V). Generated <c>OpenPluginChainCommand</c> for the toolbar button, ⋯ menu and key binding.</summary>
    [RelayCommand]
    void OpenPluginChain() => ShowPluginChain?.Invoke();

    /// <summary>True when the native VST3 bridge (CsoundVisualVstHost.dll) is present; it is optional (built by <c>tools/build_vsthost.ps1</c>).</summary>
    public static bool PluginHostAvailable => VstHostNative.IsAvailable;

    /// <summary>
    /// Connects the chain to the engine and to the MIDI hub and restores the saved chain.
    /// Call once, on the UI thread, after the main window opened.
    /// </summary>
    /// <remarks>
    /// Order matters: the plug-in manager's crash recovery runs <i>before</i> the saved chain is
    /// restored, so a plug-in that took the app down last time is blacklisted and skipped instead
    /// of crashing it again. The restore itself is posted at <c>Background</c> priority so the
    /// window finishes its first layout and render before slow plug-ins load.
    /// </remarks>
    public void InitPluginHost()
    {
        if (pluginHostReady)
            return;
        pluginHostReady = true;
        engine.Inserts = PluginChain;
        // Preparing plug-ins (setActive) belongs to the UI thread: the performance thread asks
        // for it through the dispatcher and waits (bounded) for the answer. Send is the highest
        // dispatcher priority, so the request jumps ahead of rendering and input; the 5 s bound
        // keeps the audio thread from hanging forever if the UI thread is itself blocked (e.g.
        // waiting for the audio thread), at the cost of a dropout. Returns false on timeout.
        // On timeout the dispatcher operation is NOT removed from the queue (it may even be
        // running already); VstChain.Prepare handles that by bumping its generation number, which
        // turns the late work into a no-op, and keeps the chain bypassed until a prepare completes.
        PluginChain.UiInvoke = action =>
        {
            if (Dispatcher.UIThread.CheckAccess()) { action(); return true; } // already on the UI thread: run inline (waiting would deadlock)
            var op = Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Send);
            return op.GetTask().Wait(TimeSpan.FromSeconds(5));
        };
        // Instruments in the chain play the same MIDI as Csound (ports, on-screen keyboard).
        // MessageReceived fires on the producing thread (driver callback or UI), not the UI
        // thread; SendMidi is safe there. The subscription lives as long as the view model.
        midiHub.MessageReceived += m =>
        {
            var (status, d1, d2) = m.ToMidi1();
            PluginChain.SendMidi(status, d1, d2);
        };
        PluginChain.Changed += SavePluginChain;
        // VST3 restartComponent requests (latency / I/O / parameter changes reported by a plug-in,
        // e.g. after switching a linear-phase mode): handled on the UI thread every 250 ms. With an
        // older bridge DLL (ABI < 2) PollRestartRequests returns at once, so the timer is harmless.
        restartTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => PluginChain.PollRestartRequests());
        restartTimer.Start();
        // Plug-in manager: a plug-in that crashed the app last time is switched off before anything loads.
        VstPluginManager.Settings = settings;
        if (VstPluginManager.RecoverFromCrash() is { } crashNote)
            ConsoleLines.Add(new ConsoleLine(crashNote, MessageKind.Warning));
        // Align the blacklist with the cached scan (plug-ins that failed to scan are switched off,
        // ones that now scan fine are switched back on). Only the cache file is read, no scan runs.
        VstPluginManager.SyncWithScan(VstScanner.LoadCache());
        // Test hooks (UI automation without keyboard input): open these windows at start.
        if (Environment.GetEnvironmentVariable("CSV_OPEN_SETTINGS") == "1")
            Dispatcher.UIThread.Post(() => ShowSettings?.Invoke(), DispatcherPriority.Background);
        if (Environment.GetEnvironmentVariable("CSV_OPEN_PLUGIN_CHAIN") == "1")
            Dispatcher.UIThread.Post(() => ShowPluginChain?.Invoke(), DispatcherPriority.Background);

        if (settings.PluginChain.Count == 0)
            return;
        if (!VstHostNative.IsAvailable)
        {
            ConsoleLines.Add(new ConsoleLine(Loc.S(
                $"⚠ Hay {settings.PluginChain.Count} plugin(s) VST3 guardados, pero falta CsoundVisualVstHost.dll: la cadena queda desactivada.",
                $"⚠ {settings.PluginChain.Count} VST3 plug-in(s) are saved, but CsoundVisualVstHost.dll is missing: the chain is disabled."), MessageKind.Warning));
            return;
        }
        var saved = settings.PluginChain.ToList(); // snapshot: restoring must not see later edits of the settings list
        Dispatcher.UIThread.Post(() =>
        {
            PluginChain.Changed -= SavePluginChain; // do not rewrite the settings while restoring
            var problems = PluginChain.Restore(saved);
            PluginChain.Changed += SavePluginChain;
            if (PluginChain.Describe() is { } d)
                ConsoleLines.Add(new ConsoleLine("🎛 " + d, MessageKind.Info));
            foreach (var p in problems)
                ConsoleLines.Add(new ConsoleLine(Loc.S("⚠ Plugin VST3 no restaurado: ", "⚠ VST3 plug-in not restored: ") + p, MessageKind.Warning));
            // Entries that could not be loaded (e.g. a plug-in temporarily uninstalled) are not
            // removed here: since saving was suspended during the restore, they stay in the settings
            // until the user edits the chain, whose next save writes only what is actually loaded.
        }, DispatcherPriority.Background);
    }

    /// <summary>Saves the chain (order, bypass and each plug-in's state) into the settings.</summary>
    /// <remarks>
    /// Subscribed to <see cref="VstChain.Changed"/> and called once more by
    /// <see cref="ShutdownPluginHost"/>, which captures parameter changes made in plug-in editors
    /// since the last structural edit. Without the native bridge nothing
    /// is saved, so a chain saved on a machine that had the bridge is not wiped by one that lacks it.
    /// </remarks>
    public void SavePluginChain()
    {
        if (!pluginHostReady || !VstHostNative.IsAvailable)
            return;
        settings.PluginChain = PluginChain.ToSettings();
        settings.Save();
    }

    /// <summary>Saves the chain and releases every plug-in (main window closed).</summary>
    /// <remarks>
    /// Saves first (plug-in state blobs can only be read while the plug-ins are alive), then
    /// unsubscribes so clearing the chain does not save an empty chain over it, then detaches the
    /// chain from the engine before freeing the plug-ins.
    /// </remarks>
    public void ShutdownPluginHost()
    {
        if (!pluginHostReady)
            return;
        SavePluginChain();
        PluginChain.Changed -= SavePluginChain;
        restartTimer?.Stop(); // no more native calls into plug-ins that are about to be freed
        engine.Inserts = null;
        PluginChain.Clear(); // waits for a running audio block to finish before freeing each plug-in
    }
}
