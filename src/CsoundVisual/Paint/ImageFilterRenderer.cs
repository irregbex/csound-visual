// ImageFilterRenderer.cs — Paint Rooms · «Image Filter»: offline render to WAV and «Show Csound code».
//
// Role: filters a sound file with a picture OFFLINE through Csound and writes a 24-bit WAV, and
// writes the readable standalone .csd behind «Show Csound code».
//
// The render writes the same standalone .csd as «Show Csound code» (absolute data paths) into a
// temporary folder, runs it on a PRIVATE CsoundEngine instance (own CSOUND*, own thread; the app's
// engine may keep playing) as fast as the CPU allows into a 32-bit float temporary WAV, then
// post-processes in C#:
//   * LATENCY COMPENSATION: the phase vocoder delays everything by FftSize + 2·hop samples
//     (ImageFilterSettings.LatencySamples). Csound renders that much longer and the first
//     LatencySamples samples are dropped, so the result lines up sample-accurately with the source
//     (drop it on the same Cubase track position and it is in time);
//   * the peak is measured; the file is normalised to −1 dBTP (true peak) when asked, otherwise only lowered
//     when it would clip;
//   * the target is written as 24-bit PCM at the SOURCE's sample rate (diskin2 converts nothing
//     then; the render runs at that rate).
//
// Threading: RenderAsync may be awaited from the UI thread; the work runs on the private engine's
// performance thread and on thread-pool threads.

using System.Diagnostics;
using CsoundVisual.Engine;

namespace CsoundVisual.Paint;

/// <summary>Outcome of an Image Filter render.</summary>
/// <param name="Success">True when the file was written.</param>
/// <param name="Path">Target file.</param>
/// <param name="Seconds">Audio duration written.</param>
/// <param name="PeakBefore">Peak (linear) Csound produced, before any gain.</param>
/// <param name="AppliedGainDb">Gain applied after Csound (normalisation / clip protection), dB.</param>
/// <param name="RenderSeconds">Wall-clock time Csound took.</param>
/// <param name="LatencySamples">Samples removed from the start (latency compensation).</param>
/// <param name="Messages">Csound console lines.</param>
public sealed record FilterRenderResult(bool Success, string Path, double Seconds, double PeakBefore, double AppliedGainDb,
    double RenderSeconds, int LatencySamples, IReadOnlyList<ConsoleLine> Messages)
{
    /// <summary>The Csound error lines, if any.</summary>
    public IEnumerable<string> Errors => Messages.Where(m => m.IsError).Select(m => m.Text);
}

/// <summary>Offline rendering and code export of the Image Filter (see the file header).</summary>
public static class ImageFilterRenderer
{
    /// <summary>Control period of renders and exported code (samples).</summary>
    public const int RenderKsmps = 32;

    /// <summary>Seconds a live-input .csd written by «Show Csound code» runs (it is meant to be stopped by hand).</summary>
    public const double LiveCodeSeconds = 3600;

    /// <summary>
    /// Writes the standalone, readable .csd for <paramref name="image"/> filtering
    /// <paramref name="source"/> (<c>null</c> = live input) into a new sub-folder of
    /// <see cref="PaintRenderer.CodeFolder"/> and returns its path.
    /// </summary>
    /// <param name="image">The filter picture.</param>
    /// <param name="settings">Filter settings written into the .csd (channels, p-fields, header comments).</param>
    /// <param name="source">The sound to filter (copied next to the .csd), or <c>null</c> for the live input.</param>
    /// <param name="title">Picture name: header comment and part of the folder name (sanitised to letters, digits, «-», «_»).</param>
    /// <returns>Path of the written <c>filter.csd</c> (its data files sit next to it, referenced by plain names).</returns>
    /// <remarks>
    /// Synchronous file I/O (the gain tables can be tens of MB). The live-input variant runs at
    /// 48 kHz for <see cref="LiveCodeSeconds"/>; a file variant runs at the source's rate for the
    /// source length plus the filter latency (+ 50 ms) so the tail is not cut.
    /// </remarks>
    public static string ExportCode(SpectralImage image, ImageFilterSettings settings, AudioData? source, string title)
    {
        double sr = source?.SampleRate ?? 48000;
        var t = ImageFilterTables.Build(image, settings, sr);
        string safe = string.Concat(title.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')).Trim('_');
        if (safe.Length == 0) safe = "filter";
        string folder = Path.Combine(PaintRenderer.CodeFolder, $"filter_{safe}_{DateTime.Now:yyyyMMdd_HHmmss}");
        double pass = settings.PassSeconds(source?.Seconds ?? 0);
        double dur = source is null ? LiveCodeSeconds : source.Seconds + settings.LatencySamples / sr + 0.05;
        return ImageFilterCode.WriteStandaloneCsd(t, settings, sr, RenderKsmps, source, pass, dur, folder, "filter", absolutePaths: false, title);
    }

    /// <summary>
    /// Filters <paramref name="source"/> once with <paramref name="image"/> and writes
    /// <paramref name="targetPath"/> (24-bit WAV, same length and sample rate as the source,
    /// latency removed).
    /// </summary>
    /// <param name="image">The filter picture (copied before the render starts).</param>
    /// <param name="settings">Filter settings (time mapping, FFT, mix, gain…).</param>
    /// <param name="source">The sound to filter.</param>
    /// <param name="targetPath">WAV file to write.</param>
    /// <param name="progress">Receives 0…1 while rendering (optional).</param>
    /// <param name="cancel">Stops the render (the target is not written).</param>
    /// <returns>The outcome; <see cref="FilterRenderResult.Success"/> is false when Csound failed (see its messages).</returns>
    /// <exception cref="OperationCanceledException">When <paramref name="cancel"/> fires.</exception>
    /// <exception cref="InvalidOperationException">When the temporary folder path contains spaces.</exception>
    /// <remarks>
    /// The temporary folder (.csd, gain tables, source copy, raw render) is always deleted, also
    /// on failure or cancellation. Progress is polled every 100 ms from the private engine's score
    /// time.
    /// </remarks>
    public static async Task<FilterRenderResult> RenderAsync(SpectralImage image, ImageFilterSettings settings, AudioData source, string targetPath,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var snapshot = image.Clone();
        settings = settings.Sanitized();
        string temp = Path.Combine(Path.GetTempPath(), "csoundvisual_filter_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var messages = new List<ConsoleLine>();
        int latency = settings.LatencySamples;
        try
        {
            double sr = source.SampleRate;
            double pass = settings.PassSeconds(source.Seconds);
            // Csound renders the latency on top of the sound, so after dropping it the length matches the source.
            // Two extra control periods (2 × 32 samples) are a safety margin for the k-rate turnoff.
            double total = source.Seconds + (latency + RenderKsmps * 2) / sr;
            string csdPath = await Task.Run(() =>
            {
                var t = ImageFilterTables.Build(snapshot, settings, sr);
                return ImageFilterCode.WriteStandaloneCsd(t, settings, sr, RenderKsmps, source, pass, total, temp, "filter", absolutePaths: true, "Image Filter render", forRender: true);
            }, cancel);
            // The .csd's own <CsOptions> say «-odac» (meant for the editor); strip them and pass the
            // render options below instead.
            var (csd, _) = CsdTools.ExtractOptions(await File.ReadAllTextAsync(csdPath, cancel));

            // csoundSetOption splits on spaces: the output must be a space-free path (the temp folder normally is).
            string rawWav = Path.Combine(temp, "render.wav");
            if (rawWav.Contains(' '))
                throw new InvalidOperationException("The temporary folder path contains spaces; Csound cannot render there.");

            var engine = new CsoundEngine();
            var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.Finished += code => done.TrySetResult(code);
            var sw = Stopwatch.StartNew();
            // -W = WAV header, -f = 32-bit float samples, -o = output file (no audio device, so
            // Csound runs as fast as the CPU allows), -d = no graphs, -m0 = no per-note messages.
            engine.Start(new EngineJob(csd, ["-W", "-f", "-o" + rawWav, "-d", "-m0"], null));
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
                return new FilterRenderResult(false, targetPath, 0, 0, 0, sw.Elapsed.TotalSeconds, latency, messages);

            var (peak, gainDb, seconds) = await Task.Run(() =>
            {
                var raw = WavIO.Read(rawWav);
                // Latency compensation: drop the first `latency` samples, keep the source's length.
                int frames = source.Frames;
                var outCh = new float[raw.Channels.Length][];
                for (int c = 0; c < outCh.Length; c++)
                {
                    outCh[c] = new float[frames];
                    int n = Math.Clamp(raw.Frames - latency, 0, frames);
                    Array.Copy(raw.Channels[c], Math.Min(latency, raw.Frames), outCh[c], 0, n);
                }
                var audio = new AudioData(outCh, raw.SampleRate);
                double p = audio.Peak();
                // True-peak normalisation to −1 dBTP (see AudioData.RenderGain): a sample peak of
                // −1 dBFS still clipped between samples on dense images.
                double g = audio.RenderGain(settings.NormalizeRender);
                foreach (var ch in audio.Channels)
                    for (int i = 0; i < ch.Length; i++) ch[i] = (float)(ch[i] * g);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
                WavIO.WritePcm24(targetPath, audio.Channels, audio.SampleRate);
                return (p, 20 * Math.Log10(g), audio.Seconds);
            }, cancel);
            progress?.Report(1);
            return new FilterRenderResult(true, targetPath, seconds, peak, gainDb, sw.Elapsed.TotalSeconds, latency, messages);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* a locked temp file is not worth an error */ }
        }
    }
}
