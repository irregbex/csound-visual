// PaintRenderer.cs — Paint Rooms foundation.
//
// Role: renders a SpectralImage to a WAV file OFFLINE through Csound, and writes the readable
// standalone .csd behind «Show Csound code».
//
// The render writes the same standalone .csd as «Show Csound code» (with absolute data paths) into
// a temporary folder, runs it on a PRIVATE CsoundEngine instance (its own CSOUND*, its own thread;
// the app's engine may keep playing meanwhile) as fast as the CPU allows, into a 32-bit float
// temporary WAV, then post-processes in C#: measures the true peak, normalises (to −1 dBTP true peak, or only
// down when it would clip), writes the 24-bit target file and deletes the temporaries.
//
// Threading: RenderAsync may be awaited from the UI thread; the work runs on the engine's
// performance thread and a thread-pool thread for the post-processing.

using System.Diagnostics;
using CsoundVisual.Engine;

namespace CsoundVisual.Paint;

/// <summary>Outcome of a render.</summary>
/// <param name="Success">True when the file was written.</param>
/// <param name="Path">Target file.</param>
/// <param name="Seconds">Audio duration.</param>
/// <param name="PeakBefore">Peak (linear) Csound produced, before normalisation.</param>
/// <param name="AppliedGainDb">Gain applied after Csound (normalisation), dB.</param>
/// <param name="RenderSeconds">Wall-clock time Csound took.</param>
/// <param name="Messages">Csound console lines (errors and warnings first matter).</param>
public sealed record PaintRenderResult(bool Success, string Path, double Seconds, double PeakBefore, double AppliedGainDb,
    double RenderSeconds, IReadOnlyList<ConsoleLine> Messages)
{
    /// <summary>The Csound error lines, if any.</summary>
    public IEnumerable<string> Errors => Messages.Where(m => m.IsError).Select(m => m.Text);
}

/// <summary>Offline rendering and code export of Paint Rooms images.</summary>
public static class PaintRenderer
{
    /// <summary>Control period of renders and exported code (samples).</summary>
    public const int RenderKsmps = 32;

    /// <summary>
    /// Finds and loads the Csound library (needed before rendering outside the app, e.g. in
    /// console tests). Returns false when no Csound installation was found.
    /// </summary>
    public static bool InitializeCsound() => Interop.CsoundLocator.Initialize();

    /// <summary>Full path of the command-line <c>csound.exe</c> next to the loaded library, or <c>null</c>.</summary>
    public static string? CsoundExe =>
        Interop.CsoundLocator.BinDir is { } bin && File.Exists(System.IO.Path.Combine(bin, "csound.exe")) ? System.IO.Path.Combine(bin, "csound.exe") : null;

    /// <summary>Folder where «Show Csound code» writes its .csd sets: Documents\Csound Visual\Paint Rooms.</summary>
    public static string CodeFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Csound Visual", "Paint Rooms");

    /// <summary>
    /// Writes the standalone, readable .csd of <paramref name="image"/> into a new sub-folder of
    /// <see cref="CodeFolder"/> (data files referenced by plain name) and returns its path.
    /// </summary>
    /// <param name="image">The canvas.</param>
    /// <param name="settings">Synthesis settings.</param>
    /// <param name="title">Name for the folder and the header comment.</param>
    /// <returns>Full path of the written <c>.csd</c> (its data files sit next to it).</returns>
    /// <remarks>
    /// Runs synchronously on the caller's thread (UI thread in practice): building the tables is a
    /// few milliseconds for typical canvas sizes. The tables use the RENDER sample rate and
    /// <see cref="RenderKsmps"/>, so the exported code sounds like «Render WAV», not like the live
    /// engine's possibly different rate.
    /// </remarks>
    public static string ExportCode(SpectralImage image, PaintSynthSettings settings, string title)
    {
        var t = PaintTables.Build(image, settings, settings.RenderSampleRate, RenderKsmps);
        // File-system-safe folder name: letters, digits, '-' and '_' only; a timestamp keeps every export distinct.
        string safe = string.Concat(title.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')).Trim('_');
        if (safe.Length == 0) safe = "paint";
        string folder = System.IO.Path.Combine(CodeFolder, $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}");
        return PaintCsoundCode.WriteStandaloneCsd(t, folder, "paint", absolutePaths: false, title);
    }

    /// <summary>
    /// Renders <paramref name="image"/> once (no loop) to <paramref name="targetPath"/> (24-bit WAV).
    /// </summary>
    /// <param name="image">The canvas (copied before the render starts, so editing may continue).</param>
    /// <param name="settings">Synthesis settings (sample rate and normalisation included).</param>
    /// <param name="targetPath">WAV file to write.</param>
    /// <param name="progress">Receives 0…1 while rendering (optional).</param>
    /// <param name="cancel">Stops the render (the target is not written).</param>
    /// <returns>
    /// The outcome. A Csound failure is reported through <see cref="PaintRenderResult.Success"/> = false
    /// and the collected <see cref="PaintRenderResult.Messages"/>; cancellation throws
    /// <see cref="OperationCanceledException"/>. The temporary folder is always deleted.
    /// </returns>
    public static async Task<PaintRenderResult> RenderAsync(SpectralImage image, PaintSynthSettings settings, string targetPath,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var snapshot = image.Clone();
        string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "csoundvisual_paint_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var messages = new List<ConsoleLine>();
        try
        {
            // Tables + .csd (absolute paths: the private engine must not change the process's current folder).
            var t = await Task.Run(() => PaintTables.Build(snapshot, settings, settings.RenderSampleRate, RenderKsmps), cancel);
            string csdPath = await Task.Run(() => PaintCsoundCode.WriteStandaloneCsd(t, temp, "paint", absolutePaths: true, "Paint Rooms render", forRender: true), cancel);
            // The <CsOptions> of the written file are dropped: the render passes its own flags below.
            var (csd, _) = CsdTools.ExtractOptions(await File.ReadAllTextAsync(csdPath, cancel));

            // csoundSetOption splits on spaces: the output must be a space-free path (the temp folder normally is).
            string rawWav = System.IO.Path.Combine(temp, "render.wav");
            if (rawWav.Contains(' '))
                throw new InvalidOperationException("The temporary folder path contains spaces; Csound cannot render there.");

            // Private engine: Finished fires (on the engine's thread) with Csound's exit code when the
            // score ends or Stop is called. Flags: -W WAV, -f 32-bit float samples (no clipping before
            // our own normalisation), -o target, -d no displays, -m0 quiet console.
            var engine = new CsoundEngine();
            var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.Finished += code => done.TrySetResult(code);
            var sw = Stopwatch.StartNew();
            engine.Start(new EngineJob(csd, ["-W", "-f", "-o" + rawWav, "-d", "-m0"], null));
            // Expected score length (s) for the progress bar: one pass of the picture plus the release
            // tail (6 release time constants ≈ −52 dB) plus a small margin — the same formula as the
            // score end written by PaintCsoundCode. Polled every 100 ms; console lines are drained as we go.
            double total = t.PassSeconds + t.Settings.ReleaseMs / 1000 * 6 + 0.2;
            using (cancel.Register(engine.Stop))
            {
                while (!done.Task.IsCompleted)
                {
                    await Task.WhenAny(done.Task, Task.Delay(100));
                    progress?.Report(Math.Clamp(engine.ScoreTime / total, 0, 1));
                    while (engine.Messages.TryDequeue(out var m)) messages.Add(m);
                }
            }
            int code = await done.Task;
            sw.Stop();
            while (engine.Messages.TryDequeue(out var m)) messages.Add(m);
            cancel.ThrowIfCancellationRequested();
            if (code != 0 || !File.Exists(rawWav))
                return new PaintRenderResult(false, targetPath, 0, 0, 0, sw.Elapsed.TotalSeconds, messages);

            // Post-processing: exact peak, normalisation, 24-bit output.
            var (peak, gainDb, seconds) = await Task.Run(() =>
            {
                var audio = WavIO.Read(rawWav);
                double p = audio.Peak();
                // True-peak normalisation to −1 dBTP (see AudioData.RenderGain): a sample peak of
                // −1 dBFS still clipped between samples on dense images.
                double g = audio.RenderGain(settings.NormalizeRender);
                foreach (var ch in audio.Channels)
                    for (int i = 0; i < ch.Length; i++) ch[i] = (float)(ch[i] * g);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(targetPath))!);
                WavIO.WritePcm24(targetPath, audio.Channels, audio.SampleRate);
                return (p, 20 * Math.Log10(g), audio.Seconds);
            }, cancel);
            progress?.Report(1);
            return new PaintRenderResult(true, targetPath, seconds, peak, gainDb, sw.Elapsed.TotalSeconds, messages);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* a locked temp file is not worth an error */ }
        }
    }
}
