using System.Runtime.InteropServices;

// The whole application is Windows-only (it binds csound7_64.dll and Windows Core Audio).
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace CsoundVisual.Audio;

/// <summary>
/// Minimal COM interop for the Windows Core Audio APIs (MMDevice + WASAPI) used by the
/// application's own low-latency audio output.
/// </summary>
/// <remarks>
/// <para>
/// Classic <c>[ComImport]</c> interfaces do not inherit vtables, so every interface re-declares
/// the methods of its base interfaces <b>in vtable order</b>. <see cref="IAudioClient3"/> therefore
/// lists the 12 <c>IAudioClient</c> methods, then the 3 <c>IAudioClient2</c> methods, then its own.
/// Do not reorder anything in this file.
/// </para>
/// <para>
/// All objects are created and used on the audio thread (an MTA thread), so no cross-apartment
/// marshalling happens on the real-time path.
/// </para>
/// <para>
/// Every method is declared <c>[PreserveSig]</c>: it returns the raw <c>HRESULT</c> instead of
/// throwing, so callers can recognise specific codes (see the <c>AUDCLNT_E_*</c> constants) and
/// so the real-time path never pays for an exception. Durations in WASAPI are
/// <c>REFERENCE_TIME</c> values: 100-nanosecond units (10 000 per millisecond).
/// </para>
/// <para>
/// Users: <see cref="WasapiOutput"/> (render endpoints, shared and exclusive) and
/// <see cref="WasapiInput"/> (capture endpoints and loopback, shared only).
/// </para>
/// </remarks>
internal static class WasapiInterop
{
    // ---- CLSIDs / IIDs ----
    /// <summary>CLSID of the system <c>MMDeviceEnumerator</c> coclass (entry point to all endpoints).</summary>
    public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    /// <summary>IID of the base <c>IAudioClient</c>. Kept for reference; the app always activates <see cref="IID_IAudioClient3"/>.</summary>
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    /// <summary>IID of <c>IAudioClient3</c> (Windows 10+; adds low-latency shared-mode periods).</summary>
    public static readonly Guid IID_IAudioClient3 = new("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
    /// <summary>IID passed to <c>IAudioClient::GetService</c> to obtain the render client.</summary>
    public static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    /// <summary>IID passed to <c>IAudioClient::GetService</c> to obtain the capture client.</summary>
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT.</summary>
    public static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>KSDATAFORMAT_SUBTYPE_PCM.</summary>
    public static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    // ---- Constants ----
    /// <summary>EDataFlow value of playback endpoints (speakers, headphones, interface outputs).</summary>
    public const int eRender = 0;
    /// <summary>EDataFlow value of recording endpoints (microphones, line inputs, interface inputs).</summary>
    public const int eCapture = 1;
    /// <summary>Capture packet flag: the packet is a gap (glitch) after lost data.</summary>
    public const uint AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY = 0x1;
    /// <summary>Capture packet flag: treat the packet as silence whatever the buffer contains.</summary>
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    /// <summary>ERole "console": the default device for games/system sounds (what Windows calls the default device).</summary>
    public const int eConsole = 0;
    /// <summary>ERole "multimedia": the default device for music and video playback.</summary>
    public const int eMultimedia = 1;
    /// <summary>Endpoint state mask: only devices that are present and enabled.</summary>
    public const uint DEVICE_STATE_ACTIVE = 0x1;
    /// <summary>CLSCTX_INPROC_SERVER | INPROC_HANDLER | LOCAL_SERVER | REMOTE_SERVER, for <c>IMMDevice.Activate</c>.</summary>
    public const uint CLSCTX_ALL = 0x17;
    /// <summary>Shared mode: the stream goes through the Windows audio engine mixer (other apps keep playing).</summary>
    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    /// <summary>Exclusive mode: the app owns the hardware; the format must be one the driver accepts natively.</summary>
    public const int AUDCLNT_SHAREMODE_EXCLUSIVE = 1;
    /// <summary>Event-driven stream: Windows signals an event handle (<c>SetEventHandle</c>) once per period.</summary>
    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    /// <summary>Capture what a render endpoint plays (shared mode only).</summary>
    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    /// <summary>Shared mode only: let the audio engine convert channel count / sample rate to the mix format.</summary>
    public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    /// <summary>With <see cref="AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM"/>: use the default-quality (better than linear) resampler.</summary>
    public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    /// <summary>WAVEFORMATEX tag for integer PCM.</summary>
    public const ushort WAVE_FORMAT_PCM = 1;
    /// <summary>WAVEFORMATEX tag for IEEE float samples.</summary>
    public const ushort WAVE_FORMAT_IEEE_FLOAT = 3;
    /// <summary>WAVEFORMATEX tag meaning "look at <see cref="WaveFormatExtensible.SubFormat"/>".</summary>
    public const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    // ---- HRESULTs worth recognising ----
    /// <summary>The endpoint was unplugged, disabled or its format changed; the stream must be rebuilt.</summary>
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    /// <summary>The requested format is not accepted (typical in exclusive mode with float32).</summary>
    public const int AUDCLNT_E_UNSUPPORTED_FORMAT = unchecked((int)0x88890008);
    /// <summary>The user disabled "Allow applications to take exclusive control" for this device.</summary>
    public const int AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED = unchecked((int)0x8889000E);
    /// <summary>Another application already holds the device in exclusive mode.</summary>
    public const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);
    /// <summary>
    /// Exclusive event-driven <c>Initialize</c> failed because the period is not aligned to the
    /// driver's granularity. The caller must read <c>GetBufferSize</c>, release the client, and
    /// re-initialise a fresh one with the aligned duration.
    /// </summary>
    public const int AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED = unchecked((int)0x88890019);

    /// <summary>
    /// PKEY_Device_FriendlyName ("Speakers (Realtek USB Audio)"). A mutable static field rather
    /// than a property because <see cref="IPropertyStore.GetValue"/> takes it by <c>ref</c>.
    /// </summary>
    public static PropertyKey PKEY_Device_FriendlyName = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

    /// <summary>Property key (GUID + id) used by <see cref="IPropertyStore"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey
    {
        /// <summary>Property set GUID.</summary>
        public Guid fmtid;
        /// <summary>Property id inside the set.</summary>
        public int pid;
    }

    /// <summary>
    /// Simplified PROPVARIANT: only the type tag and the first pointer-sized value are read
    /// (enough for <c>VT_LPWSTR</c> strings). Always released with <see cref="PropVariantClear"/>.
    /// </summary>
    /// <remarks>
    /// The native layout on x64 is 2-byte <c>vt</c>, three reserved words (6 bytes), then a
    /// 16-byte union; hence the value at offset 8 and the total size of 24 bytes. The string the
    /// union points to is owned by the PROPVARIANT and freed by <see cref="PropVariantClear"/>.
    /// </remarks>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PropVariant
    {
        /// <summary>VARTYPE tag (31 = <c>VT_LPWSTR</c>).</summary>
        [FieldOffset(0)] public ushort vt;
        /// <summary>First pointer of the value union (the string pointer for <c>VT_LPWSTR</c>).</summary>
        [FieldOffset(8)] public IntPtr pointerValue;
    }

    /// <summary>
    /// WAVEFORMATEXTENSIBLE (packed, 40 bytes). The first 18 bytes are the plain WAVEFORMATEX
    /// header; the rest is only meaningful when <see cref="FormatTag"/> is
    /// <see cref="WAVE_FORMAT_EXTENSIBLE"/>.
    /// </summary>
    /// <remarks>
    /// Formats returned by Windows (<c>GetMixFormat</c>, <c>IsFormatSupported</c>'s closest match)
    /// are allocated with <c>CoTaskMemAlloc</c>: the callers copy them with
    /// <c>Marshal.PtrToStructure</c> and free the pointer with <see cref="CoTaskMemFree"/>. Formats
    /// passed <i>to</i> Windows are marshalled into a temporary unmanaged block by the caller.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WaveFormatExtensible
    {
        /// <summary>WAVE_FORMAT_PCM, WAVE_FORMAT_IEEE_FLOAT or WAVE_FORMAT_EXTENSIBLE.</summary>
        public ushort FormatTag;
        /// <summary>Interleaved channels per frame.</summary>
        public ushort Channels;
        /// <summary>Sample rate in Hz (frames per second).</summary>
        public uint SamplesPerSec;
        /// <summary>Bytes per second = <see cref="SamplesPerSec"/> × <see cref="BlockAlign"/>.</summary>
        public uint AvgBytesPerSec;
        /// <summary>Bytes per frame = channels × container bytes.</summary>
        public ushort BlockAlign;
        /// <summary>Container size of one sample in bits (16, 24 or 32).</summary>
        public ushort BitsPerSample;
        /// <summary>Bytes of extra data after the 18-byte header (22 for WAVEFORMATEXTENSIBLE).</summary>
        public ushort Size;
        /// <summary>Meaningful bits inside the container (e.g. 24 for "int24 in 32").</summary>
        public ushort ValidBitsPerSample;
        /// <summary>Speaker-position bit mask (SPEAKER_FRONT_LEFT = 0x1, …).</summary>
        public uint ChannelMask;
        /// <summary>KSDATAFORMAT subtype: <see cref="SubtypePcm"/> or <see cref="SubtypeFloat"/>.</summary>
        public Guid SubFormat;

        /// <summary>True for 32-bit float samples (plain or extensible).</summary>
        public readonly bool IsFloat =>
            FormatTag == WAVE_FORMAT_IEEE_FLOAT || (FormatTag == WAVE_FORMAT_EXTENSIBLE && SubFormat == SubtypeFloat);

        /// <summary>Builds an extensible PCM or float format.</summary>
        /// <param name="rate">Sample rate in Hz.</param>
        /// <param name="channels">Channel count; 1/2/4/6/8 get the standard speaker masks, others 0 (unspecified).</param>
        /// <param name="containerBits">Bits each sample occupies in memory (16, 24 or 32).</param>
        /// <param name="validBits">Meaningful bits (≤ <paramref name="containerBits"/>), e.g. 24 for int24-in-32.</param>
        /// <param name="isFloat"><c>true</c> for IEEE float samples, <c>false</c> for integer PCM.</param>
        public static WaveFormatExtensible Create(uint rate, int channels, int containerBits, int validBits, bool isFloat)
        {
            ushort block = (ushort)(channels * containerBits / 8);
            // Channel masks: mono = FRONT_CENTER (0x4); stereo = FL|FR (0x3); quad = FL|FR|BL|BR (0x33);
            // 5.1 = FL|FR|FC|LFE|BL|BR (0x3F); 7.1 = 5.1 with side instead of back + back pair (0x63F).
            return new WaveFormatExtensible
            {
                FormatTag = WAVE_FORMAT_EXTENSIBLE,
                Channels = (ushort)channels,
                SamplesPerSec = rate,
                AvgBytesPerSec = rate * block,
                BlockAlign = block,
                BitsPerSample = (ushort)containerBits,
                Size = 22,
                ValidBitsPerSample = (ushort)validBits,
                ChannelMask = channels switch { 1 => 0x4u, 2 => 0x3u, 4 => 0x33u, 6 => 0x3Fu, 8 => 0x63Fu, _ => 0u },
                SubFormat = isFloat ? SubtypeFloat : SubtypePcm,
            };
        }

        /// <summary>Human readable description ("float32", "int24 en 32", "int16") shown in the console and diagnostics.</summary>
        public readonly string Describe() => IsFloat
            ? $"float{BitsPerSample}"
            : ValidBitsPerSample != 0 && ValidBitsPerSample != BitsPerSample ? $"int{ValidBitsPerSample} en {BitsPerSample}" : $"int{BitsPerSample}";
    }

    /// <summary>IMMDeviceEnumerator: lists endpoints, returns the default one, or opens one by id.</summary>
    /// <remarks>The notification-callback methods are declared only to keep the vtable order; they are not used.</remarks>
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    /// <summary>IMMDeviceCollection: indexed list of endpoints returned by <see cref="IMMDeviceEnumerator.EnumAudioEndpoints"/>.</summary>
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    /// <summary>
    /// IMMDevice: one audio endpoint. <c>Activate</c> creates an <c>IAudioClient</c>/<c>IAudioClient3</c>
    /// on it; <c>GetId</c> returns the stable endpoint id string that the settings store.
    /// </summary>
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    /// <summary>IPropertyStore: key/value metadata of an endpoint (only the friendly name is read).</summary>
    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    /// <summary>IAudioClient3 including the inherited IAudioClient and IAudioClient2 methods, in vtable order.</summary>
    /// <remarks>
    /// <para>
    /// Units: <c>bufferDuration</c>, <c>periodicity</c>, latencies and device periods are
    /// REFERENCE_TIME (100 ns). <c>GetSharedModeEnginePeriod</c> and
    /// <c>InitializeSharedAudioStream</c> work in <b>frames</b>. Format pointers returned by
    /// <c>GetMixFormat</c>/<c>GetCurrentSharedModeEnginePeriod</c> must be freed with
    /// <see cref="CoTaskMemFree"/>.
    /// </para>
    /// </remarks>
    [ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient3
    {
        // IAudioClient
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        // IAudioClient2
        [PreserveSig] int IsOffloadCapable(int category, out int offloadCapable);
        [PreserveSig] int SetClientProperties(IntPtr properties);
        [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven, out long minBufferDuration, out long maxBufferDuration);
        // IAudioClient3
        [PreserveSig] int GetSharedModeEnginePeriod(IntPtr format, out uint defaultPeriodFrames, out uint fundamentalPeriodFrames, out uint minPeriodFrames, out uint maxPeriodFrames);
        [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint currentPeriodFrames);
        [PreserveSig] int InitializeSharedAudioStream(uint streamFlags, uint periodInFrames, IntPtr format, IntPtr audioSessionGuid);
    }

    /// <summary>
    /// IAudioRenderClient: <c>GetBuffer(n)</c> lends a pointer to room for <c>n</c> frames in the
    /// endpoint buffer, <c>ReleaseBuffer(n, flags)</c> commits them (flag 2 = silent). The pointer is
    /// owned by WASAPI and is valid only between the two calls.
    /// </summary>
    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint framesRequested, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint framesWritten, uint flags);
    }

    /// <summary>
    /// IAudioCaptureClient: reads packets recorded by a capture stream. Each packet is obtained
    /// with <see cref="GetBuffer"/> and must be returned with <see cref="ReleaseBuffer"/> before the
    /// next one; <see cref="GetNextPacketSize"/> returns 0 when no packet is waiting.
    /// </summary>
    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint framesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint framesRead);
        [PreserveSig] int GetNextPacketSize(out uint framesInNextPacket);
    }

    /// <summary>Frees the memory owned by a PROPVARIANT (e.g. the string of a <c>VT_LPWSTR</c>).</summary>
    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PropVariant value);

    /// <summary>Frees a block allocated by COM for the caller (mix formats, closest-match formats).</summary>
    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr ptr);

    /// <summary>Creates a Win32 event; the audio threads use an auto-reset one as the WASAPI period signal.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, IntPtr name);

    /// <summary>Waits for a handle; returns 0 (WAIT_OBJECT_0) when signalled or 0x102 (WAIT_TIMEOUT).</summary>
    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    /// <summary>Closes a Win32 handle (the period events).</summary>
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);

    /// <summary>Registers the calling thread with MMCSS ("Pro Audio") so Windows schedules it as real-time audio.</summary>
    /// <param name="taskName">MMCSS task name from the registry (<c>"Pro Audio"</c>).</param>
    /// <param name="taskIndex">In: 0; out: the task index Windows assigned.</param>
    /// <returns>A handle for <see cref="AvRevertMmThreadCharacteristics"/>, or <see cref="IntPtr.Zero"/> on failure.</returns>
    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    /// <summary>Undoes <see cref="AvSetMmThreadCharacteristicsW"/>; must be called on the same thread before it exits.</summary>
    [DllImport("avrt.dll")]
    public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    /// <summary>Creates the MMDevice enumerator.</summary>
    /// <remarks>The RCW is bound to the calling thread's apartment; create it on the thread that uses it.</remarks>
    public static IMMDeviceEnumerator CreateEnumerator() =>
        (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!)!;

    /// <summary>Reads a string property (e.g. the friendly name) of a device, or <c>null</c>.</summary>
    public static string? ReadStringProperty(IMMDevice device, PropertyKey key)
    {
        if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0)
            return null;
        try
        {
            if (store.GetValue(ref key, out var value) != 0)
                return null;
            try
            {
                return value.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.pointerValue) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }
}
