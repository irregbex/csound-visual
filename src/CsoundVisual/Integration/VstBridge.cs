using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CsoundVisual.Integration;

/// <summary>
/// One running instance of the «Csound Visual» VST3 plugin inside a DAW (Cubase…), as announced
/// by the descriptor file it writes in <see cref="VstBridge.InstancesFolder"/>.
/// </summary>
/// <param name="Pipe">Full named-pipe name the instance listens on.</param>
/// <param name="Pid">Process id of the host (the DAW).</param>
/// <param name="Kind">"instrument" or "fx".</param>
/// <param name="Track">Name of the DAW track the plugin is on (may be empty).</param>
/// <param name="CsdName">Name of the orchestra currently loaded in the plugin ("default" at first).</param>
/// <param name="Host">Host application name, if the plugin could learn it.</param>
public sealed record VstInstance(
    [property: JsonPropertyName("pipe")] string Pipe,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("track")] string? Track,
    [property: JsonPropertyName("csdName")] string? CsdName,
    [property: JsonPropertyName("host")] string? Host)
{
    /// <summary>Menu label: «Instrument · Audio 01 · demo.csd».</summary>
    public string Display =>
        $"{(Kind == "fx" ? "FX" : "Instrument")}{(string.IsNullOrWhiteSpace(Track) ? "" : " · " + Track)} · {CsdName ?? "?"}";
}

/// <summary>
/// Client side of the bridge between the app and the «Csound Visual» VST3 plugin
/// (<c>src/CsoundVST</c>). Each plugin instance runs a named-pipe server and announces itself
/// with a small JSON file; the app lists those files, connects, and sends one request per
/// connection.
/// </summary>
/// <remarks>
/// Wire format, both directions: 4-byte little-endian length + UTF-8 JSON.
/// Requests: <c>{"cmd":"load","name","csd"}</c> (compile and swap the orchestra),
/// <c>{"cmd":"status"}</c>, <c>{"cmd":"set","channel","value"}</c>,
/// <c>{"cmd":"meters","samples"}</c> (peaks + scope ring, see <see cref="MetersAsync"/>). Replies
/// always carry <c>"ok"</c>; <c>load</c> also returns the Csound messages of the compile in <c>"log"</c>.
/// Every call is asynchronous I/O with a short timeout, so a frozen DAW never freezes the app.
/// <para>Used by <c>MainViewModel.Daw.cs</c> («Send to DAW», channel forwarding, the VST3 OUTPUT
/// card polled at about 25 Hz). The connection is opened and closed per request, so there is no
/// session state to lose when the DAW reloads or removes the plugin.</para>
/// <para>The length prefix is written with <see cref="BitConverter"/>, i.e. in the machine's
/// native byte order, which is little-endian on every Windows x64/ARM64 target this app runs on.</para>
/// </remarks>
public static class VstBridge
{
    /// <summary>Folder where plugin instances announce themselves.</summary>
    public static string InstancesFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsoundVisual", "vst-instances");

    /// <summary>
    /// Running instances. Descriptor files left behind by a crashed DAW (process gone) are
    /// deleted.
    /// </summary>
    /// <returns>A new list (possibly empty); synchronous file I/O, cheap enough for a menu opening.</returns>
    /// <remarks>Liveness is judged by the DAW's process id only: a recycled pid can keep a stale
    /// descriptor alive until that unrelated process exits (the connection then simply fails).</remarks>
    public static List<VstInstance> ListInstances()
    {
        var list = new List<VstInstance>();
        if (!Directory.Exists(InstancesFolder)) return list;
        foreach (var file in Directory.EnumerateFiles(InstancesFolder, "*.json"))
        {
            try
            {
                var instance = JsonSerializer.Deserialize<VstInstance>(File.ReadAllText(file));
                if (instance is null) continue;
                if (!IsAlive(instance.Pid))
                {
                    TryDelete(file);
                    continue;
                }
                list.Add(instance);
            }
            catch (Exception)
            {
                // A file being rewritten by the plugin at this very moment: skip it this time.
            }
        }
        return list;
    }

    /// <summary>Sends <paramref name="request"/> and returns the reply (throws on timeout or broken pipe).</summary>
    /// <param name="target">Plugin instance to talk to.</param>
    /// <param name="request">JSON request; must contain <c>"cmd"</c>.</param>
    /// <param name="timeoutMs">Budget for the whole exchange (connect + write + read), milliseconds.</param>
    /// <returns>The reply object (check its <c>"ok"</c> member).</returns>
    /// <exception cref="OperationCanceledException">The timeout expired (DAW busy or frozen).</exception>
    /// <exception cref="IOException">The pipe broke (plugin unloaded, DAW closed).</exception>
    /// <exception cref="InvalidDataException">The reply length or content is invalid.</exception>
    public static async Task<JsonObject> SendAsync(VstInstance target, JsonObject request, int timeoutMs = 8000)
    {
        // NamedPipeClientStream wants the bare name; the descriptor stores the full \\.\pipe\ path.
        string shortName = target.Pipe.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase) ? target.Pipe[9..] : target.Pipe;
        using var cts = new CancellationTokenSource(timeoutMs);
        await using var pipe = new NamedPipeClientStream(".", shortName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cts.Token);

        byte[] body = Encoding.UTF8.GetBytes(request.ToJsonString());
        await pipe.WriteAsync(BitConverter.GetBytes(body.Length), cts.Token);
        await pipe.WriteAsync(body, cts.Token);
        await pipe.FlushAsync(cts.Token);

        byte[] header = new byte[4];
        await pipe.ReadExactlyAsync(header, cts.Token);
        int length = BitConverter.ToInt32(header);
        // Sanity bound (64 MiB) so a corrupt header cannot trigger a huge allocation.
        if (length is < 0 or > 64 * 1024 * 1024)
            throw new InvalidDataException("bad reply length " + length);
        byte[] reply = new byte[length];
        await pipe.ReadExactlyAsync(reply, cts.Token);
        return JsonNode.Parse(reply)?.AsObject() ?? throw new InvalidDataException("empty reply");
    }

    /// <summary>Loads an orchestra into the plugin. Returns the plugin's reply (ok, log, channels).</summary>
    /// <param name="target">Plugin instance.</param>
    /// <param name="name">Display name of the orchestra (shown in the DAW and in <see cref="VstInstance.CsdName"/>).</param>
    /// <param name="csd">Full <c>.csd</c> text. The plugin runs with the DAW's working folder, so
    /// relative sound-file paths should be made absolute by the caller first
    /// (<c>MainViewModel.Daw.cs</c> does).</param>
    /// <remarks>20 s timeout: the plugin compiles the orchestra (and may load samples) before replying.</remarks>
    public static Task<JsonObject> LoadAsync(VstInstance target, string name, string csd) =>
        SendAsync(target, new JsonObject { ["cmd"] = "load", ["name"] = name, ["csd"] = csd }, 20000);

    /// <summary>Sets a control channel in the plugin (fire-and-forget friendly, short timeout).</summary>
    public static Task<JsonObject> SetAsync(VstInstance target, string channel, double value) =>
        SendAsync(target, new JsonObject { ["cmd"] = "set", ["channel"] = channel, ["value"] = value }, 1500);

    /// <summary>
    /// Output meters of the plugin: per-channel peak since the previous call (L, R) and the newest
    /// <paramref name="samples"/> samples of the mono mix, for the app's VST3 OUTPUT card.
    /// </summary>
    /// <param name="target">Plugin instance.</param>
    /// <param name="samples">Scope length requested (the plugin keeps a 4096-sample ring).</param>
    /// <returns>Peaks (linear, 0…1 full scale), scope samples (oldest first) and the plugin's
    /// sample rate in Hz (48000 if the reply omits it). Missing members degrade to empty/zero.</returns>
    public static async Task<(float[] Peaks, float[] Scope, double SampleRate)> MetersAsync(VstInstance target, int samples)
    {
        var reply = await SendAsync(target, new JsonObject { ["cmd"] = "meters", ["samples"] = samples }, 1000);
        float[] peaks = reply["peaks"] is JsonArray p ? [.. p.Select(v => (float)(v?.GetValue<double>() ?? 0))] : [0, 0];
        float[] scope = reply["scope"] is JsonArray s ? [.. s.Select(v => (float)(v?.GetValue<double>() ?? 0))] : [];
        double sr = reply["sr"]?.GetValue<double>() ?? 48000;
        return (peaks, scope, sr);
    }

    /// <summary>True when a process with <paramref name="pid"/> exists and has not exited.</summary>
    static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Best-effort delete of a stale descriptor (another app instance may race us).</summary>
    static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (Exception) { }
    }
}
