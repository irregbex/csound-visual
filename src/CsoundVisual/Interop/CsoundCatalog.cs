using System.Collections.Frozen;
using System.Runtime.InteropServices;
using CsoundVisual.Localization;

namespace CsoundVisual.Interop;

/// <summary>A real-time audio backend that can be selected with <c>-+rtaudio=&lt;Id&gt;</c>.</summary>
/// <param name="Id">Value passed to <c>-+rtaudio</c> (e.g. <c>"pa_cb"</c>, <c>"wasapi"</c>).</param>
/// <param name="Label">Friendly name shown in the UI.</param>
public sealed record AudioModule(string Id, string Label)
{
    /// <inheritdoc/>
    public override string ToString() => Label;
}

/// <summary>An audio output device reported by a real-time module.</summary>
/// <param name="Id">Device identifier passed as <c>-o&lt;Id&gt;</c> (e.g. <c>"dac3"</c>); <c>"dac"</c> means the module default.</param>
/// <param name="Name">Human readable device name.</param>
/// <param name="Channels">Maximum number of output channels (0 when unknown).</param>
public sealed record AudioDevice(string Id, string Name, int Channels)
{
    /// <summary>
    /// Text shown in the device picker. Csound device ids (<c>dac3</c>) are shown because users
    /// refer to them in <c>-o</c> options; WASAPI endpoint ids are long GUIDs and are hidden.
    /// </summary>
    public string Display => Id.StartsWith("dac", StringComparison.Ordinal) && Id != "dac" ? $"{Id} · {Name}" : Name;

    /// <inheritdoc/>
    public override string ToString() => Display;
}

/// <summary>One overload of an opcode, as reported by <c>csoundNewOpcodeList</c>.</summary>
/// <param name="OutTypes">Csound type letters of the outputs (empty when the opcode returns nothing).</param>
/// <param name="InTypes">Csound type letters of the inputs.</param>
/// <param name="Deprecated">Whether this overload is deprecated in Csound 7.</param>
public sealed record OpcodeSignature(string OutTypes, string InTypes, bool Deprecated);

/// <summary>
/// Information queried once from Csound through short-lived helper instances:
/// the list of opcodes (for the editor) and the available audio modules/devices.
/// </summary>
/// <remarks>
/// Every method creates its own <c>CSOUND*</c>, uses it on the calling thread and destroys it, so
/// they can run on a background thread while another instance is performing. They are slow-ish
/// (PortAudio scans every host API), so callers should use <see cref="Task.Run(Action)"/>.
/// <para>
/// Consumers: the editor's <c>OpcodeIndex</c> (opcode names and signatures for highlighting,
/// completion and hover help) and the audio settings (module and device pickers for the
/// Csound-driven backends). Each helper instance is created with the installation's plugin folder
/// (<see cref="CsoundLocator.PluginsDir"/>) so plugin opcodes and rtaudio modules are included.
/// </para>
/// </remarks>
public static unsafe class CsoundCatalog
{
    /// <summary>Friendly labels for the module names Csound 7 reports on Windows.</summary>
    /// <param name="id">Module name as returned by <c>csoundGetModule</c>.</param>
    /// <returns>The label, or <c>null</c> for unknown modules (the caller then shows the raw id).</returns>
    static string? ModuleLabel(string id) => id.ToLowerInvariant() switch
    {
        "pa_cb" => "PortAudio (callback)",
        "pa_bl" => Loc.S("PortAudio (bloqueante)", "PortAudio (blocking)"),
        "portaudio" => "PortAudio",
        "wasapi" => "WASAPI",
        "winmm" => "WinMM (MME)",
        _ => null,
    };

    /// <summary>
    /// Returns every opcode name with all its overloads, keyed case-sensitively (Csound opcode
    /// names are case-sensitive). Returns an empty dictionary on failure.
    /// </summary>
    public static FrozenDictionary<string, OpcodeSignature[]> LoadOpcodes()
    {
        var result = new Dictionary<string, List<OpcodeSignature>>(StringComparer.Ordinal);
        IntPtr cs = CsoundNative.csoundCreate(IntPtr.Zero, CsoundLocator.PluginsDir);
        if (cs == IntPtr.Zero)
            return FrozenDictionary<string, OpcodeSignature[]>.Empty;
        try
        {
            // Csound allocates the array; the strings inside it belong to Csound's opcode table,
            // so they are copied to managed strings before the list is disposed.
            CsoundNative.OpcodeListEntry* list = null;
            int n = CsoundNative.csoundNewOpcodeList(cs, &list);
            // Bug fix (leak on exceptions): the list is now disposed in a finally block, so an
            // exception while copying the entries (e.g. a malformed string) no longer skips
            // csoundDisposeOpcodeList.
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var e = list[i];
                    string name = Marshal.PtrToStringUTF8(e.OpName) ?? "";
                    // Internal helpers such as "##add" or "=.k" are not user-facing names.
                    if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_'))
                        continue;
                    if (!result.TryGetValue(name, out var overloads))
                        result[name] = overloads = [];
                    overloads.Add(new OpcodeSignature(
                        Marshal.PtrToStringUTF8(e.OutTypes) ?? "",
                        Marshal.PtrToStringUTF8(e.InTypes) ?? "",
                        e.Deprecated != 0));
                }
            }
            finally
            {
                if (list is not null)
                    CsoundNative.csoundDisposeOpcodeList(cs, list);
            }
        }
        finally
        {
            CsoundNative.csoundDestroy(cs);
        }
        return result.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Lists the real-time audio modules available in this installation (e.g. PortAudio, WASAPI).
    /// MIDI modules and the duplicate generic "PortAudio" entry are skipped.
    /// </summary>
    public static List<AudioModule> GetAudioModules()
    {
        var modules = new List<AudioModule>();
        IntPtr cs = CsoundNative.csoundCreate(IntPtr.Zero, CsoundLocator.PluginsDir);
        if (cs == IntPtr.Zero)
            return modules;
        try
        {
            IntPtr name, type;
            for (int i = 0; CsoundNative.csoundGetModule(cs, i, &name, &type) == 0; i++)
            {
                string n = Marshal.PtrToStringUTF8(name) ?? "";
                if (Marshal.PtrToStringUTF8(type) != "audio" || n.Equals("PortAudio", StringComparison.OrdinalIgnoreCase))
                    continue;
                modules.Add(new AudioModule(n, ModuleLabel(n) ?? n));
            }
        }
        finally
        {
            CsoundNative.csoundDestroy(cs);
        }
        // PortAudio callback first: it is Csound's default and the most compatible choice.
        return [.. modules.OrderBy(m => m.Id == "pa_cb" ? 0 : m.Id.StartsWith("pa") ? 1 : 2)];
    }

    /// <summary>
    /// Lists the output devices of <paramref name="moduleId"/>. The first entry is always the module
    /// default (<c>-odac</c>).
    /// </summary>
    /// <remarks>
    /// The module selected with <c>-+rtaudio</c> is only loaded by the first <c>csoundGetModule</c>
    /// call, so the module list is walked before asking for devices; otherwise the list is empty.
    /// </remarks>
    /// <param name="moduleId">Module id as in <see cref="AudioModule.Id"/> (e.g. <c>"pa_cb"</c>).</param>
    /// <returns>The default entry followed by every output device the module reported.</returns>
    public static List<AudioDevice> GetOutputDevices(string moduleId)
    {
        var devices = new List<AudioDevice> { new("dac", Loc.S("Predeterminado del sistema", "System default"), 0) };
        IntPtr cs = CsoundNative.csoundCreate(IntPtr.Zero, CsoundLocator.PluginsDir);
        if (cs == IntPtr.Zero)
            return devices;
        try
        {
            CsoundNative.csoundSetOption(cs, "-+rtaudio=" + moduleId);
            IntPtr name, type;
            // Walk the module list only for its side effect: it loads the -+rtaudio module.
            for (int i = 0; CsoundNative.csoundGetModule(cs, i, &name, &type) == 0; i++) { }

            // Two-pass query: count first, then fill a managed array pinned for the native call.
            // The second call may report fewer devices if one disappeared in between.
            int count = CsoundNative.csoundGetAudioDevList(cs, null, 1);
            if (count > 0)
            {
                var buffer = new CsoundNative.CsAudioDevice[count];
                fixed (CsoundNative.CsAudioDevice* p = buffer)
                {
                    count = Math.Min(count, CsoundNative.csoundGetAudioDevList(cs, p, 1));
                    for (int i = 0; i < count; i++)
                        devices.Add(new AudioDevice(
                            CsoundNative.FixedString(p[i].DeviceId, 128),
                            CsoundNative.FixedString(p[i].DeviceName, 128),
                            p[i].MaxNchnls));
                }
            }
        }
        finally
        {
            CsoundNative.csoundDestroy(cs);
        }
        return devices;
    }
}
