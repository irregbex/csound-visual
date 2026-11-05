using CsoundVisual.Localization;
using CsoundVisual.ViewModels;

namespace CsoundVisual.Integration;

/// <summary>
/// Plug-in manager à la Cubase: a black list of VST3 modules that are ignored (not offered in the
/// chain window and skipped when the saved chain is restored). A module lands there
/// automatically when it fails the scan, or when it crashed the app while being loaded or while
/// its window was opening (detected on the next start through a marker file); the user can also
/// switch any module off or on in Settings › VST3 plug-ins.
/// </summary>
/// <remarks>
/// The list lives in <see cref="AppSettings.PluginBlacklist"/>: lower-case module path → reason.
/// Reasons start with <see cref="UserPrefix"/>, <see cref="ScanPrefix"/> or <see cref="CrashPrefix"/>.
/// A module blocked by a scan error is unblocked automatically when a later scan succeeds; the
/// other two only by the user.
/// <para><b>Crash detection.</b> In-process plug-in code cannot be sandboxed: a fail-fast inside a
/// plug-in (e.g. Sylenth1 under .NET's CET shadow stack) kills the app with no managed handler.
/// <see cref="Guard"/> therefore writes a marker file <i>before</i> the risky native call and
/// deletes it after; a marker still present at the next start (<see cref="RecoverFromCrash"/>)
/// names the culprit. Scanning is safe already (separate worker process, see <see cref="VstScanner"/>).</para>
/// <para><b>Threads.</b> All members are meant for the UI thread (settings and the
/// <see cref="Changed"/> event are not synchronised). Callers: <see cref="VstChain"/> (guards and
/// checks), <c>MainViewModel.Plugins.cs</c> (recovery and scan sync at start) and the settings
/// page <c>Views/PluginManagerSection.cs</c>.</para>
/// </remarks>
public static class VstPluginManager
{
    /// <summary>Reason prefix: switched off by the user.</summary>
    public const string UserPrefix = "user:";

    /// <summary>Reason prefix: the scan could not inspect the module.</summary>
    public const string ScanPrefix = "scan:";

    /// <summary>Reason prefix: the module crashed the app.</summary>
    public const string CrashPrefix = "crash:";

    /// <summary>Folder of the crash markers (<c>%LOCALAPPDATA%\CsoundVisual</c>).</summary>
    static readonly string GuardDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsoundVisual");

    /// <summary>
    /// This process's marker, written while a plug-in is being loaded or its window opened; left
    /// behind only by a crash. The name carries the process id (<c>vst-loading-1234.txt</c>).
    /// </summary>
    /// <remarks>
    /// Bug fix: there used to be one shared <c>vst-loading.txt</c>, so a second app instance
    /// starting while the first one was inside a guarded call read the live marker as a crash and
    /// black-listed a healthy plug-in. Now each process writes its own file, and
    /// <see cref="RecoverFromCrash"/> only considers markers whose process is gone.
    /// </remarks>
    static readonly string GuardPath = Path.Combine(GuardDir, $"vst-loading-{Environment.ProcessId}.txt");

    /// <summary>Start time of this process (UTC ticks), written into the marker so a reused process id is not mistaken for the original process.</summary>
    static readonly long ProcessStartTicks = SafeStartTicks(System.Diagnostics.Process.GetCurrentProcess());

    /// <summary>Start time of <paramref name="p"/> in UTC ticks, or −1 when it cannot be read (exited, other user, access denied).</summary>
    static long SafeStartTicks(System.Diagnostics.Process p)
    {
        try { using (p) return p.StartTime.ToUniversalTime().Ticks; }
        catch (Exception) { return -1; }
    }

    /// <summary>
    /// True when the marker <paramref name="file"/> belongs to a process that is still running
    /// (another instance of the app in the middle of a guarded call): such markers are left alone.
    /// </summary>
    /// <param name="file">Marker path (<c>vst-loading-&lt;pid&gt;.txt</c>; the legacy name without a pid is never "alive").</param>
    /// <param name="lines">Marker contents; line 4, when present, is the owner's start time in UTC ticks.</param>
    static bool OwnerAlive(string file, string[] lines)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        int dash = name.LastIndexOf('-');
        if (dash < 0 || !int.TryParse(name[(dash + 1)..], out int pid) || pid == Environment.ProcessId)
            return false; // legacy single marker, or our own id reused from a crashed run
        try
        {
            long start = SafeStartTicks(System.Diagnostics.Process.GetProcessById(pid));
            if (start < 0) return false;
            // Same pid but a different start time: the id was reused by an unrelated process.
            return lines.Length < 4 || !long.TryParse(lines[3].Trim(), out long recorded)
                || Math.Abs(recorded - start) < TimeSpan.TicksPerSecond;
        }
        catch (Exception) { return false; } // no such process: its owner died inside the plug-in
    }

    /// <summary>The settings holding the list (set once by the view model at start).</summary>
    public static AppSettings? Settings { get; set; }

    /// <summary>Raised when the list changed (UI refresh).</summary>
    public static event Action? Changed;

    /// <summary>Normalised dictionary key of a module path: trimmed, no trailing separator, lower case (Windows paths are case-insensitive).</summary>
    static string Key(string path) => path.Trim().TrimEnd('\\', '/').ToLowerInvariant();

    /// <summary>True when the module is on the black list.</summary>
    public static bool IsBlocked(string path) => Settings?.PluginBlacklist.ContainsKey(Key(path)) == true;

    /// <summary>Stored reason (with its prefix) or <c>null</c> when the module is active.</summary>
    public static string? ReasonOf(string path) =>
        Settings?.PluginBlacklist.TryGetValue(Key(path), out var r) == true ? r : null;

    /// <summary>Readable text of a stored reason.</summary>
    public static string Describe(string? reason) => reason switch
    {
        null => Loc.S("activo", "active"),
        var r when r.StartsWith(UserPrefix, StringComparison.Ordinal) => Loc.S("desactivado por ti", "switched off by you"),
        var r when r.StartsWith(ScanPrefix, StringComparison.Ordinal) => Loc.S("falló al examinarlo: ", "failed the scan: ") + r[ScanPrefix.Length..],
        var r when r.StartsWith(CrashPrefix, StringComparison.Ordinal) => Loc.S("cerró la app: ", "crashed the app: ") + r[CrashPrefix.Length..],
        var r => r,
    };

    /// <summary>Puts a module on the list (saves the settings).</summary>
    /// <param name="path">Module path (any case).</param>
    /// <param name="reason">Reason including one of the prefixes (<see cref="UserPrefix"/>…).</param>
    public static void Block(string path, string reason)
    {
        if (Settings is null) return;
        Settings.PluginBlacklist[Key(path)] = reason;
        Settings.Save();
        Changed?.Invoke();
    }

    /// <summary>Takes a module off the list (saves the settings). A module that failed the scan is forgotten in the cache so the next scan tries it again.</summary>
    public static void Unblock(string path)
    {
        if (Settings is null) return;
        if (Settings.PluginBlacklist.Remove(Key(path), out var reason))
        {
            if (reason.StartsWith(ScanPrefix, StringComparison.Ordinal) || reason.StartsWith(CrashPrefix, StringComparison.Ordinal))
                VstScanner.Forget(path);
            Settings.Save();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Brings the list in line with a scan result: modules that failed are blocked (unless the user
    /// already decided about them) and modules that were blocked only by an earlier scan error and
    /// now scan fine are unblocked. Returns how many modules were newly blocked.
    /// </summary>
    /// <param name="modules">Scan result or cache contents (<see cref="VstScanner.Scan"/> / <see cref="VstScanner.LoadCache"/>).</param>
    /// <remarks>User and crash entries are never touched here: only the user can lift them.</remarks>
    public static int SyncWithScan(IEnumerable<VstModuleEntry> modules)
    {
        if (Settings is null) return 0;
        int added = 0;
        bool changed = false;
        foreach (var m in modules)
        {
            string key = Key(m.Path);
            Settings.PluginBlacklist.TryGetValue(key, out var reason);
            if (m.Error is not null)
            {
                if (reason is null)
                {
                    Settings.PluginBlacklist[key] = ScanPrefix + m.Error;
                    added++;
                    changed = true;
                }
            }
            else if (reason is not null && reason.StartsWith(ScanPrefix, StringComparison.Ordinal))
            {
                Settings.PluginBlacklist.Remove(key);
                changed = true;
            }
        }
        if (changed)
        {
            Settings.Save();
            Changed?.Invoke();
        }
        return added;
    }

    /// <summary>
    /// Marks the start of a risky call into a plug-in (load, opening its window). Dispose it when
    /// the call returned; if the app crashes inside, the marker survives and the next start blocks
    /// the module (<see cref="RecoverFromCrash"/>).
    /// </summary>
    /// <param name="path">Module path (first line of the marker).</param>
    /// <param name="what"><c>"load"</c> or <c>"editor"</c> (second line; selects the console wording).</param>
    /// <returns>A token whose <see cref="IDisposable.Dispose"/> deletes the marker. Use with <c>using</c>.</returns>
    /// <remarks>There is a single marker file per process, so guards do not nest; failures to
    /// write it are ignored (the call proceeds unguarded). Each instance of the app has its own
    /// file (named after its process id, with its start time on line 4), so another instance
    /// starting meanwhile does not mistake it for a crash.</remarks>
    public static IDisposable Guard(string path, string what)
    {
        try
        {
            Directory.CreateDirectory(GuardDir);
            File.WriteAllText(GuardPath, path + "\n" + what + "\n" + DateTime.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                                         + "\n" + ProcessStartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception) { }
        return new GuardToken();
    }

    /// <summary>Disposable returned by <see cref="Guard"/>: removes the marker when the guarded call returned.</summary>
    sealed class GuardToken : IDisposable
    {
        /// <summary>Deletes the marker file (best effort).</summary>
        public void Dispose()
        {
            try { File.Delete(GuardPath); } catch (Exception) { }
        }
    }

    /// <summary>
    /// At start: if the previous run died inside a plug-in, blocks that module and returns a
    /// message for the console (<c>null</c> when the last run ended normally).
    /// </summary>
    /// <remarks>Must run after <see cref="Settings"/> is set and before any plug-in is loaded, so the
    /// culprit is not restored again by the saved chain. Every marker in the folder is examined
    /// (<c>vst-loading-&lt;pid&gt;.txt</c>, plus the legacy <c>vst-loading.txt</c> of older versions);
    /// markers whose process is still running belong to another live instance and are skipped.
    /// Several dead markers give several lines in the returned text.</remarks>
    public static string? RecoverFromCrash()
    {
        string[] files;
        try { files = Directory.Exists(GuardDir) ? Directory.GetFiles(GuardDir, "vst-loading*.txt") : []; }
        catch (Exception) { return null; }
        var notes = new List<string>();
        foreach (var file in files)
        {
            try
            {
                // Marker layout: line 1 module path, line 2 "load"/"editor", line 3 local timestamp,
                // line 4 owner's start time (UTC ticks; absent in legacy markers).
                var lines = File.ReadAllLines(file);
                if (OwnerAlive(file, lines)) continue; // another running instance is inside a guarded call
                File.Delete(file);
                if (lines.Length == 0 || lines[0].Trim().Length == 0) continue;
                string path = lines[0].Trim();
                string what = lines.Length > 1 ? lines[1].Trim() : "load";
                string when = lines.Length > 2 ? lines[2].Trim() : "";
                string action = what == "editor" ? Loc.S("al abrir su ventana", "while opening its window") : Loc.S("al cargarlo", "while loading it");
                Block(path, CrashPrefix + action + (when.Length > 0 ? " (" + when + ")" : ""));
                string name = Path.GetFileNameWithoutExtension(path);
                notes.Add(Loc.S($"⚠ El plugin «{name}» cerró la app {action} la última vez: quedó desactivado (Configuración › Plugins VST3 para volver a activarlo).",
                                $"⚠ The plug-in «{name}» crashed the app {action} last time: it was switched off (Settings › VST3 plug-ins to switch it back on)."));
            }
            catch (Exception) { } // unreadable marker (e.g. being written right now): ignore it
        }
        return notes.Count == 0 ? null : string.Join("\n", notes);
    }
}
