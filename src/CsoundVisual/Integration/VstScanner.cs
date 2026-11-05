using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CsoundVisual.Localization;

namespace CsoundVisual.Integration;

/// <summary>One plug-in class found by the scanner.</summary>
/// <param name="Path">Module path (<c>.vst3</c> bundle folder or file) the class lives in.</param>
/// <param name="ClassId">32-hex-digit class id passed to <see cref="VstHostNative.Load"/>.</param>
/// <param name="Name">Plug-in name.</param>
/// <param name="Vendor">Class vendor, or the factory vendor when the class has none.</param>
/// <param name="SubCategories">VST3 sub-categories joined by <c>|</c> (e.g. <c>Fx|EQ</c>, <c>Instrument|Synth</c>).</param>
/// <param name="Version">Plug-in version string.</param>
public sealed record VstPluginInfo(string Path, string ClassId, string Name, string Vendor, string SubCategories, string Version)
{
    /// <summary>True for instruments/generators.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool IsInstrument => SubCategories.Contains("Instrument", StringComparison.OrdinalIgnoreCase);

    /// <summary>Text matched by the search box.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string SearchText => Name + " " + Vendor + " " + SubCategories;
}

/// <summary>A scanned module (cache entry).</summary>
public sealed class VstModuleEntry
{
    /// <summary>Module path (.vst3 bundle folder or single file).</summary>
    public string Path { get; set; } = "";
    /// <summary>Last write time of the module binary when it was scanned (UTC ticks).</summary>
    public long Stamp { get; set; }
    /// <summary>Audio processor classes of the module.</summary>
    public List<VstPluginInfo> Classes { get; set; } = [];
    /// <summary>Why the module could not be scanned, or <c>null</c>.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Finds the VST3 plug-ins installed on the PC. Each module is loaded in a separate worker
/// process (<c>CsoundVisual.exe --vst3-scan-worker</c>) so that a plug-in that crashes or hangs
/// while being inspected cannot take the app down: the worker is restarted after the faulty
/// module, which is then listed with an error. Results are cached in
/// <c>%LOCALAPPDATA%\CsoundVisual\vst3-cache.json</c> and a module is only rescanned when its
/// binary changes.
/// </summary>
/// <remarks>
/// <para><b>Parent ↔ worker protocol.</b> The parent writes the module paths to a list file
/// (UTF-8, one per line) and starts <c>CsoundVisual.exe --vst3-scan-worker &lt;list&gt; &lt;out&gt;</c>.
/// The worker appends tab-separated lines to the output file, flushed one by one:
/// <c>BEGIN\tpath</c> before touching a module, then <c>OK\tpath\tjson</c> or
/// <c>ERR\tpath\tmessage</c>. The parent polls the file every 200 ms. If the worker exits (or is
/// killed after <see cref="ModuleTimeoutSeconds"/> without a new line) while a <c>BEGIN</c> is
/// unanswered, that module gets an error and a new worker continues with the remaining list.</para>
/// <para><b>Threads.</b> <see cref="Scan"/> blocks for minutes on a first run, so the UI calls it
/// on a background task; the cache helpers are plain synchronous file I/O.</para>
/// </remarks>
public static class VstScanner
{
    /// <summary>Cache file.</summary>
    public static readonly string CachePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsoundVisual", "vst3-cache.json");

    /// <summary>Serializer options of the cache file (indented, human-readable).</summary>
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Seconds a worker may spend on one module before it is considered hung.</summary>
    const int ModuleTimeoutSeconds = 45;

    /// <summary>The standard VST3 folders: system-wide and per-user.</summary>
    public static IReadOnlyList<string> Folders =>
    [
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3"),
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Common", "VST3"),
    ];

    /// <summary>True for Csound Visual's own plug-in, which is not offered (it would load a second Csound into the app).</summary>
    public static bool IsOwnPlugin(string path) =>
        System.IO.Path.GetFileName(path.TrimEnd('\\', '/')).Equals("CsoundVisual.vst3", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lists every .vst3 module under the standard folders (bundles are not entered).</summary>
    public static List<string> FindModules()
    {
        var list = new List<string>();
        foreach (var root in Folders)
            if (Directory.Exists(root))
                Walk(root, list, 0);
        return list;
    }

    /// <summary>
    /// Recursive folder walk collecting <c>*.vst3</c> entries. A <c>.vst3</c> directory is a bundle
    /// (<c>Contents\x86_64-win\X.vst3</c> inside) and is added without being entered, so the inner
    /// DLL is not listed twice; vendor sub-folders are entered up to 6 levels deep. Unreadable
    /// folders are skipped silently.
    /// </summary>
    static void Walk(string dir, List<string> list, int depth)
    {
        if (depth > 6) return;
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch (Exception) { return; }
        foreach (var e in entries)
        {
            bool isDir = Directory.Exists(e);
            if (e.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsOwnPlugin(e))
                    list.Add(e);
            }
            else if (isDir)
                Walk(e, list, depth + 1);
        }
    }

    /// <summary>Binary of a module: the DLL inside a bundle, or the file itself.</summary>
    public static string BinaryOf(string modulePath)
    {
        if (File.Exists(modulePath))
            return modulePath;
        string inner = System.IO.Path.Combine(modulePath, "Contents", "x86_64-win", System.IO.Path.GetFileName(modulePath));
        return File.Exists(inner) ? inner : modulePath;
    }

    /// <summary>Change stamp of a module: last-write time (UTC ticks) of its binary, 0 when unreadable.</summary>
    static long StampOf(string modulePath)
    {
        try
        {
            string bin = BinaryOf(modulePath);
            return (File.Exists(bin) ? File.GetLastWriteTimeUtc(bin) : Directory.GetLastWriteTimeUtc(bin)).Ticks;
        }
        catch (Exception) { return 0; }
    }

    /// <summary>Reads the cache (empty when missing or invalid).</summary>
    public static List<VstModuleEntry> LoadCache()
    {
        try
        {
            if (File.Exists(CachePath))
                return JsonSerializer.Deserialize<List<VstModuleEntry>>(File.ReadAllText(CachePath), Json) ?? [];
        }
        catch (Exception) { }
        return [];
    }

    /// <summary>Removes one module from the cache so the next scan inspects it again.</summary>
    public static void Forget(string path)
    {
        var entries = LoadCache();
        if (entries.RemoveAll(e => string.Equals(e.Path.TrimEnd('\\', '/'), path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) > 0)
            SaveCache(entries);
    }

    /// <summary>Writes the cache file (best effort: an I/O error just means a rescan next time).</summary>
    static void SaveCache(List<VstModuleEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(entries, Json));
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Scans the standard folders (background thread). Unchanged modules come from the cache
    /// unless <paramref name="full"/>. <paramref name="progress"/> receives short status texts.
    /// </summary>
    /// <param name="full">Re-inspect every module, ignoring the cache.</param>
    /// <param name="progress">Receives localized status texts (from this thread; <see cref="Progress{T}"/> marshals to the UI).</param>
    /// <param name="cancel">Stops between polls; the worker is killed, the cache is not written and
    /// modules that were not reached are omitted from the result (never reported as errors).</param>
    /// <returns>Every module found, sorted by file name; failed modules carry <see cref="VstModuleEntry.Error"/>.
    /// Modules removed from disk drop out of the cache because the cache is rewritten from this list.</returns>
    public static List<VstModuleEntry> Scan(bool full, IProgress<string>? progress, CancellationToken cancel)
    {
        var cache = LoadCache().ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var modules = FindModules();
        var result = new List<VstModuleEntry>();
        var todo = new List<string>();
        foreach (var m in modules)
        {
            long stamp = StampOf(m);
            if (!full && cache.TryGetValue(m, out var hit) && hit.Stamp == stamp)
                result.Add(hit);
            else
                todo.Add(m);
        }
        progress?.Report(Loc.S($"{modules.Count} módulos, {todo.Count} por examinar…", $"{modules.Count} modules, {todo.Count} to inspect…"));

        var scanned = todo.Count > 0 ? RunWorkers(todo, progress, cancel) : [];
        foreach (var m in todo)
        {
            // A cancelled scan stops before reaching some modules. Those are left out of the result
            // instead of being reported as "no result" errors: an error entry would make
            // VstPluginManager.SyncWithScan black-list a healthy plug-in that was simply never
            // inspected. (Modules missing without a cancel are a genuine failure and keep the error.)
            if (!scanned.ContainsKey(m) && cancel.IsCancellationRequested)
                continue;
            var entry = scanned.TryGetValue(m, out var e) ? e : new VstModuleEntry { Path = m, Error = Loc.S("sin resultado", "no result") };
            entry.Stamp = StampOf(m);
            result.Add(entry);
        }
        result.Sort((a, b) => string.Compare(System.IO.Path.GetFileName(a.Path), System.IO.Path.GetFileName(b.Path), StringComparison.OrdinalIgnoreCase));
        if (!cancel.IsCancellationRequested)
            SaveCache(result);
        return result;
    }

    /// <summary>Runs worker processes until every module has a result (a crash skips the culprit).</summary>
    static Dictionary<string, VstModuleEntry> RunWorkers(List<string> todo, IProgress<string>? progress, CancellationToken cancel)
    {
        var results = new Dictionary<string, VstModuleEntry>(StringComparer.OrdinalIgnoreCase);
        // The worker is this same executable in another mode (VstHostCli.TryRun); file names carry
        // our pid so two app instances scanning at once do not share files.
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CsoundVisualScan");
        Directory.CreateDirectory(dir);
        string listFile = System.IO.Path.Combine(dir, $"list-{Environment.ProcessId}.txt");
        string outFile = System.IO.Path.Combine(dir, $"out-{Environment.ProcessId}.txt");
        var remaining = new List<string>(todo);
        int total = todo.Count;

        while (remaining.Count > 0 && !cancel.IsCancellationRequested)
        {
            File.WriteAllLines(listFile, remaining, Encoding.UTF8);
            File.WriteAllText(outFile, "");
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            psi.ArgumentList.Add("--vst3-scan-worker");
            psi.ArgumentList.Add(listFile);
            psi.ArgumentList.Add(outFile);
            using var proc = Process.Start(psi)!;
            string? current = null;                 // module with a BEGIN but no OK/ERR yet
            var lastProgress = Stopwatch.StartNew(); // time since the worker last wrote a line
            int lines = 0;                          // output lines already consumed
            while (true)
            {
                bool exited = proc.WaitForExit(200);
                var all = ReadLines(outFile);
                for (; lines < all.Count; lines++)
                {
                    var parts = all[lines].Split('\t', 3);
                    if (parts.Length < 2) continue;
                    lastProgress.Restart();
                    switch (parts[0])
                    {
                        case "BEGIN":
                            current = parts[1];
                            progress?.Report($"{results.Count + 1}/{total} · {System.IO.Path.GetFileName(current)}");
                            break;
                        case "OK":
                            results[parts[1]] = Parse(parts[1], parts.Length > 2 ? parts[2] : "{}");
                            remaining.Remove(parts[1]);
                            current = null;
                            break;
                        case "ERR":
                            results[parts[1]] = new VstModuleEntry { Path = parts[1], Error = parts.Length > 2 ? parts[2] : "?" };
                            remaining.Remove(parts[1]);
                            current = null;
                            break;
                    }
                }
                if (exited)
                    break;
                if (cancel.IsCancellationRequested || lastProgress.Elapsed.TotalSeconds > ModuleTimeoutSeconds)
                {
                    try { proc.Kill(); } catch (Exception) { }
                    proc.WaitForExit(2000);
                    if (current is not null && !cancel.IsCancellationRequested)
                    {
                        results[current] = new VstModuleEntry { Path = current, Error = Loc.S("no respondió al examinarlo", "hung while being inspected") };
                        remaining.Remove(current);
                    }
                    current = null;
                    break;
                }
            }
            // The worker died (crash) inside a module: skip that one and continue with the rest.
            if (current is not null)
            {
                results[current] = new VstModuleEntry { Path = current, Error = Loc.S($"el plugin se cayó al examinarlo (código {SafeExit(proc)})", $"the plug-in crashed while being inspected (code {SafeExit(proc)})") };
                remaining.Remove(current);
            }
            else if (remaining.Count > 0 && proc.HasExited && lines == 0)
            {
                // The worker could not even start: give up rather than loop forever.
                foreach (var m in remaining)
                    results[m] = new VstModuleEntry { Path = m, Error = Loc.S("el examinador no arrancó", "the scanner did not start") };
                break;
            }
        }
        try { File.Delete(listFile); File.Delete(outFile); } catch (Exception) { }
        return results;
    }

    /// <summary>Exit code as hex (NTSTATUS-style, e.g. <c>0xC0000409</c>), or "?" when unavailable.</summary>
    static string SafeExit(Process p)
    {
        try { return "0x" + p.ExitCode.ToString("X8"); } catch (Exception) { return "?"; }
    }

    /// <summary>
    /// Reads the complete lines of the worker's output file while the worker may still be writing
    /// it (shared read/write/delete open). An unterminated last line is dropped until the next poll.
    /// </summary>
    static List<string> ReadLines(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var list = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
                list.Add(line);
            // A last line without its newline may still be being written: ignore it for now.
            if (list.Count > 0 && fs.Length > 0)
            {
                fs.Seek(-1, SeekOrigin.End);
                if (fs.ReadByte() != '\n')
                    list.RemoveAt(list.Count - 1);
            }
            return list;
        }
        catch (Exception) { return []; }
    }

    /// <summary>Turns the native scan JSON into an entry with the audio processor classes.</summary>
    /// <param name="path">Module path stored in the entry and its classes.</param>
    /// <param name="json">Output of <see cref="VstHostNative.ScanModule"/>.</param>
    /// <returns>The entry; a malformed document sets <see cref="VstModuleEntry.Error"/> instead of throwing.</returns>
    /// <remarks>Only <c>"Audio Module Class"</c> entries (processors) are kept; controller classes
    /// are hosting details resolved by the native side.</remarks>
    public static VstModuleEntry Parse(string path, string json)
    {
        var entry = new VstModuleEntry { Path = path };
        try
        {
            using var doc = JsonDocument.Parse(json);
            string factoryVendor = doc.RootElement.TryGetProperty("vendor", out var fv) ? fv.GetString() ?? "" : "";
            foreach (var c in doc.RootElement.GetProperty("classes").EnumerateArray())
            {
                if (c.GetProperty("category").GetString() != "Audio Module Class")
                    continue;
                string vendor = c.GetProperty("vendor").GetString() ?? "";
                entry.Classes.Add(new VstPluginInfo(path, c.GetProperty("cid").GetString() ?? "", c.GetProperty("name").GetString() ?? "?",
                    vendor.Length > 0 ? vendor : factoryVendor, c.GetProperty("subCategories").GetString() ?? "", c.GetProperty("version").GetString() ?? ""));
            }
        }
        catch (Exception ex)
        {
            entry.Error = ex.Message;
        }
        return entry;
    }

    /// <summary>
    /// Worker process body (<c>--vst3-scan-worker &lt;list&gt; &lt;out&gt;</c>): scans each listed module
    /// and appends <c>BEGIN</c>, then <c>OK</c> + JSON or <c>ERR</c> + message, one line each,
    /// flushing after every line so the parent knows where a crash happened.
    /// </summary>
    /// <returns>0 when the list was processed; 3 when the native bridge is missing (no line is
    /// written, which the parent reads as "the scanner did not start").</returns>
    public static int RunWorker(string listFile, string outFile)
    {
        if (!VstHostNative.IsAvailable)
            return 3;
        using var output = new StreamWriter(new FileStream(outFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
        foreach (var path in File.ReadAllLines(listFile, Encoding.UTF8))
        {
            if (path.Length == 0) continue;
            output.Write("BEGIN\t" + path + "\n");
            output.Flush();
            var (ok, text) = VstHostNative.ScanModule(path);
            // Keep the record on one line (JSON never contains raw tabs, so the tab split holds).
            text = text.Replace('\n', ' ').Replace('\r', ' ');
            output.Write((ok ? "OK\t" : "ERR\t") + path + "\t" + text + "\n");
            output.Flush();
        }
        return 0;
    }
}
