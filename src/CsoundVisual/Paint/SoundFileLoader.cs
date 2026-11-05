// SoundFileLoader.cs — Paint Rooms: reading sound files of any common format (Spectrum Room).
//
// Role: gives the Spectrum Room (and any later room) one call that turns a file into AudioData:
//   * WAV (PCM 8/16/24/32, float 32/64, extensible) is read directly by WavIO — instant, exact;
//   * everything else (AIFF/AIFC, FLAC, OGG/Vorbis, MP3, W64, CAF…, and WAV encodings WavIO does
//     not know such as ADPCM) is DECODED BY CSOUND: the app has no audio decoders of its own, but
//     Csound reads all those formats through its bundled libsndfile. Two tiny generated .csd files
//     run on the command-line csound.exe (by full path, next to the loaded Csound library):
//       pass 1 (no audio output, -n) prints the file's duration, channel count and sample rate
//              (opcodes filelen, filenchnls, filesr);
//       pass 2 renders the file with diskin2 at ITS OWN sample rate and channel count (so nothing is
//              resampled or mixed) into a 32-bit float temporary WAV, which WavIO then reads; the
//              result is trimmed to the exact number of frames (Csound renders whole control periods).
//     The temporary folder is deleted afterwards.
//
// Threading: LoadAsync does its work on the thread pool and in the csound.exe child processes; it
// may be awaited from the UI thread. Cancellation kills the child process.
//
// Why a child process and not the app's own CsoundEngine: decoding must not disturb (or wait for)
// a live performance in the engine, and a separate csound.exe isolates crashes in exotic decoders.
// Why diskin2 and not GEN01 + a table dump: diskin2 streams, so a long file never has to fit in a
// Csound table, and its output is written by Csound's own file writer.
//
// Csound command-line flags used: -n no audio output (pass 1), -d no displays, -W WAV output,
// -f 32-bit float samples (no clipping or requantisation of the decoded data), -o<file> target.

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CsoundVisual.Paint;

/// <summary>A loaded sound and how it was read.</summary>
/// <param name="Audio">Samples and sample rate.</param>
/// <param name="Path">The file.</param>
/// <param name="DecodedByCsound">True when Csound decoded it (not a plain WAV).</param>
public sealed record LoadedSound(AudioData Audio, string Path, bool DecodedByCsound);

/// <summary>Reads WAV directly and every other format through Csound (see the file header).</summary>
public static class SoundFileLoader
{
    /// <summary>Extensions offered in the file picker and accepted by drag-and-drop (lower case, with dot).</summary>
    /// <remarks>What libsndfile (bundled with Csound) decodes; MP3 support depends on that build (libsndfile ≥ 1.1).
    /// An unsupported file still fails cleanly, with Csound's own error message.</remarks>
    public static readonly string[] Extensions = [".wav", ".wave", ".aif", ".aiff", ".aifc", ".flac", ".ogg", ".oga", ".mp3", ".w64", ".caf", ".snd", ".au"];

    /// <summary>True when the file extension is one of <see cref="Extensions"/>.</summary>
    /// <param name="path">Any path (only its extension is looked at; case-insensitive).</param>
    public static bool IsSoundFile(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Reads <paramref name="path"/>. WAV files are read directly (falling back to Csound when the
    /// encoding is unusual); other formats are decoded by Csound.
    /// </summary>
    /// <param name="path">The sound file.</param>
    /// <param name="cancel">Cancels the work (kills a running csound.exe).</param>
    /// <returns>The decoded audio, at the FILE's own sample rate and channel count.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file could not be read (message says why).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was triggered.</exception>
    public static async Task<LoadedSound> LoadAsync(string path, CancellationToken cancel = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("File not found.", path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".wav" or ".wave")
        {
            try
            {
                var audio = await Task.Run(() => WavIO.Read(path), cancel);
                return new LoadedSound(audio, path, false);
            }
            catch (InvalidDataException)
            {
                // e.g. ADPCM or µ-law WAV: let Csound (libsndfile) try.
            }
        }
        var decoded = await DecodeWithCsoundAsync(path, cancel);
        return new LoadedSound(decoded, path, true);
    }

    /// <summary>
    /// Decodes any format libsndfile knows by running csound.exe twice (info, then render at the
    /// file's own rate and channel count). See the file header.
    /// </summary>
    /// <param name="path">The sound file (must not contain a double quote: it is embedded in Csound strings).</param>
    /// <param name="cancel">Cancels the work (kills the running csound.exe).</param>
    /// <returns>The decoded samples, trimmed to the file's exact frame count.</returns>
    /// <exception cref="InvalidDataException">No csound.exe, Csound could not read/decode the file,
    /// or it is longer than <see cref="SpectralAnalysis.MaxSeconds"/>.</exception>
    public static async Task<AudioData> DecodeWithCsoundAsync(string path, CancellationToken cancel = default)
    {
        // A private, unique temp folder: it is also csound.exe's working folder, and is deleted at the end.
        string exe = PaintRenderer.CsoundExe ?? throw new InvalidDataException("Csound (csound.exe) was not found, so this format cannot be decoded. Use a WAV file.");
        string temp = Path.Combine(Path.GetTempPath(), "csoundvisual_decode_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // Csound strings treat «\» as an escape character: forward slashes work on Windows too.
            string file = path.Replace('\\', '/');
            if (file.Contains('"')) throw new InvalidDataException("The file name contains a quote character.");
            var ci = CultureInfo.InvariantCulture;

            // ---- pass 1: what is in the file?
            // filelen (seconds), filenchnls and filesr are i-time opcodes, so they run as global
            // (instr 0) code; «prints» writes one marker line that is easy to find in the log.
            // The score is just «e»: nothing plays, Csound exits right after the header code.
            // (sr/nchnls of this orchestra are irrelevant: -n means no audio output at all.)
            string info = Path.Combine(temp, "info.csd");
            await File.WriteAllTextAsync(info,
                "<CsoundSynthesizer>\n<CsInstruments>\n" +
                "; Csound Visual · Spectrum Room: asks libsndfile what the file contains.\n" +
                "sr = 48000\nksmps = 64\nnchnls = 1\n0dbfs = 1\n" +
                $"giLen filelen \"{file}\"\ngiCh filenchnls \"{file}\"\ngiSr filesr \"{file}\"\n" +
                "prints \"CSV_SOUNDINFO %.9f %d %d\\n\", giLen, giCh, giSr\n" +
                "</CsInstruments>\n<CsScore>\ne\n</CsScore>\n</CsoundSynthesizer>\n", cancel);
            var (_, log1) = await RunCsoundAsync(exe, ["-n", "-d", info], temp, cancel);
            // The exit code is ignored: the marker line is the proof that the file was understood.
            var line = log1.Split('\n').FirstOrDefault(l => l.Contains("CSV_SOUNDINFO"))
                       ?? throw new InvalidDataException("Csound could not read this file: " + LastError(log1));
            // Everything after the 13-character marker: «seconds channels rate».
            var parts = line[(line.IndexOf("CSV_SOUNDINFO", StringComparison.Ordinal) + 13)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double seconds = double.Parse(parts[0], ci);
            int channels = (int)double.Parse(parts[1], ci), rate = (int)Math.Round(double.Parse(parts[2], ci));
            if (seconds <= 0 || channels < 1 || rate < 1000)
                throw new InvalidDataException("Csound could not read this file (no audio found).");
            if (seconds > SpectralAnalysis.MaxSeconds)
                throw new InvalidDataException($"The sound is longer than {SpectralAnalysis.MaxSeconds / 60:0} minutes.");

            // ---- pass 2: render it unchanged (own rate, own channels) to a float WAV.
            // sr = the file's rate and nchnls = its channel count, so diskin2 (pitch ratio 1) neither
            // resamples nor mixes; «a1, a2, …» receives one output per channel. The score note's
            // p3 = 1 is overwritten at init time with the file's length. ksmps 16 keeps the
            // rendered overshoot (rounded up to a whole control period) tiny; it is trimmed below.
            string outs = string.Join(", ", Enumerable.Range(1, channels).Select(i => "a" + i));
            string render = Path.Combine(temp, "decode.csd");
            await File.WriteAllTextAsync(render,
                "<CsoundSynthesizer>\n<CsInstruments>\n" +
                "; Csound Visual · Spectrum Room: decodes a sound file to WAV with diskin2 (no resampling).\n" +
                $"sr = {rate}\nksmps = 16\nnchnls = {channels}\n0dbfs = 1\n" +
                "instr 1\n" +
                $"  p3 = filelen(\"{file}\")       ; play the whole file\n" +
                $"  {outs} diskin2 \"{file}\", 1\n" +
                $"  out {outs}\n" +
                "endin\n</CsInstruments>\n<CsScore>\ni 1 0 1\n</CsScore>\n</CsoundSynthesizer>\n", cancel);
            string wav = Path.Combine(temp, "decoded.wav");
            var (code, log2) = await RunCsoundAsync(exe, ["-W", "-f", "-d", "-o" + wav, render], temp, cancel);
            if (code != 0 || !File.Exists(wav))
                throw new InvalidDataException("Csound could not decode this file: " + LastError(log2));
            // Read synchronously. Note: the awaits above do not use ConfigureAwait(false), so when
            // awaited from the UI thread this read (and the trim) runs back on the UI thread.
            var audio = WavIO.Read(wav);
            // Trim the end of the last control period: exactly the file's frames.
            // (filelen gives seconds with 9 decimals, so seconds × rate rounds back to the frame count.)
            int frames = Math.Min(audio.Frames, (int)Math.Round(seconds * rate));
            var channelsData = audio.Channels.Select(c => c.Length == frames ? c : c[..frames]).ToArray();
            return new AudioData(channelsData, rate);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Runs csound.exe with <paramref name="args"/> (no window), returns its exit code and combined output.</summary>
    /// <param name="exe">Full path of csound.exe.</param>
    /// <param name="args">Arguments, each passed as one argv entry (ArgumentList quotes them as needed).</param>
    /// <param name="workDir">Working folder of the child process.</param>
    /// <param name="cancel">Kills the process tree and rethrows <see cref="OperationCanceledException"/>.</param>
    /// <returns>Exit code, and stdout + «\n» + stderr (Csound prints its messages on stderr).</returns>
    static async Task<(int Code, string Log)> RunCsoundAsync(string exe, IEnumerable<string> args, string workDir, CancellationToken cancel)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = workDir,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidDataException("csound.exe could not be started.");
        var log = new StringBuilder();
        // Both pipes are drained concurrently from the start: reading them only after exit could
        // deadlock once the child fills a pipe buffer (Csound is chatty on stderr).
        var stdout = p.StandardOutput.ReadToEndAsync(cancel);
        var stderr = p.StandardError.ReadToEndAsync(cancel);
        try
        {
            await p.WaitForExitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        log.Append(await stdout).Append('\n').Append(await stderr);
        return (p.ExitCode, log.ToString());
    }

    /// <summary>The most informative line of a Csound log (the last error, or the last line).</summary>
    /// <param name="log">Combined output of a csound.exe run.</param>
    /// <returns>The last non-empty line mentioning «error» or «could not»; else the last non-empty line; else «?».</returns>
    static string LastError(string log)
    {
        var lines = log.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return lines.LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("could not", StringComparison.OrdinalIgnoreCase))
               ?? lines.LastOrDefault() ?? "?";
    }
}
