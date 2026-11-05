// WavIO.cs — Paint Rooms foundation.
//
// Role: minimal, dependency-free reading and writing of RIFF/WAVE files for Paint Rooms:
//   * reading sound files for the sample-loop mode, the offline render post-processing
//     (peak normalisation) and the next rooms (Spectrum Room analysis, Image Filter input);
//   * writing 32-bit float data files that the generated Csound code loads with GEN01
//     (images and noise as «sound files» are the fastest exact way to move big tables into Csound),
//     and the final 24-bit / 32-bit float renders.
//
// Supported input: PCM 8/16/24/32-bit integer, IEEE float 32/64-bit, WAVE_FORMAT_EXTENSIBLE with
// those sub-formats, any channel count. Output: 32-bit float or 24-bit PCM.
//
// Users: PaintCsoundCode (data tables of the standalone .csd), PaintRenderer (final renders and
// peak normalisation), PaintTables (sample-loop source), SoundFileLoader (fast path for WAV files;
// everything WavIO rejects is handed to Csound/libsndfile instead) and the Spectrum Room / Image
// Filter code.
//
// Design decisions:
//   * The whole file is read into memory at once (File.ReadAllBytes). Paint Rooms works on sounds
//     of at most a few minutes, so simplicity wins over streaming.
//   * Samples are always decoded to float −1…1, channels de-interleaved (one array per channel),
//     which is the layout every consumer (STFT, mixing, Csound tables) wants.
//   * Only the canonical 16-byte «fmt » chunk is written (no WAVE_FORMAT_EXTENSIBLE): libsndfile,
//     Csound's GEN01, Cubase and SpectraLayers all read that form, also for float data.
//
// RIFF/WAVE layout reminder (all integers little-endian):
//   "RIFF" <u32 size of everything after this field> "WAVE"
//   then chunks: <4-byte id> <u32 body size> <body> [pad byte when the body size is odd]
//   «fmt » body: u16 format tag, u16 channels, u32 sample rate, u32 byte rate,
//                u16 block align (bytes per frame), u16 bits per sample
//                [extensible: u16 cbSize, u16 valid bits, u32 channel mask, 16-byte sub-format GUID]
//   «data» body: interleaved frames (ch0 ch1 … ch0 ch1 …).

using System.Buffers.Binary;
using System.Text;

namespace CsoundVisual.Paint;

/// <summary>Decoded audio: one float array per channel (−1…1 full scale) and the sample rate.</summary>
/// <remarks>
/// Immutable in shape but not in content: the arrays are shared, not copied, so a consumer that
/// wants to modify samples must clone them first. It is the common currency of Paint Rooms
/// (<see cref="WavIO"/>, <see cref="SoundFileLoader"/>, <see cref="PaintSession.Sound"/>, the
/// Spectrum Room analysis and the Image Filter input).
/// </remarks>
/// <param name="Channels">Samples per channel (all the same length), de-interleaved; index 0 = left.</param>
/// <param name="SampleRate">Frames per second (Hz).</param>
public sealed record AudioData(float[][] Channels, int SampleRate)
{
    /// <summary>Frames (samples per channel).</summary>
    public int Frames => Channels.Length == 0 ? 0 : Channels[0].Length;

    /// <summary>Duration in seconds.</summary>
    public double Seconds => SampleRate > 0 ? (double)Frames / SampleRate : 0;

    /// <summary>Average of all channels (a down-mix with gain 1/channels, so it never clips).</summary>
    /// <returns>A NEW array of <see cref="Frames"/> samples (also for mono input: a clone, never the original).</returns>
    public float[] Mono()
    {
        // Mono input: return a copy so callers may modify the result freely.
        if (Channels.Length == 1) return (float[])Channels[0].Clone();
        var m = new float[Frames];
        foreach (var ch in Channels)
            for (int i = 0; i < m.Length; i++) m[i] += ch[i];
        float inv = 1f / Channels.Length;
        for (int i = 0; i < m.Length; i++) m[i] *= inv;
        return m;
    }

    /// <summary>
    /// Target of every rendered WAV of Paint Rooms: −1 dBTP (true peak), the usual mastering ceiling.
    /// A sample peak of −1 dBFS is NOT enough: in dense material the reconstructed waveform between
    /// samples can rise above full scale, and DAWs/converters then show (and produce) clipping.
    /// </summary>
    public const double RenderCeilingDb = -1.0;

    /// <summary>
    /// Estimated TRUE peak over all channels (linear; 1 = 0 dBTP): the highest value of the waveform
    /// reconstructed between samples, found by 4× oversampling with a windowed-sinc interpolator
    /// (the method of ITU-R BS.1770 meters). Always ≥ <see cref="Peak"/>.
    /// </summary>
    /// <remarks>
    /// 32-tap Kaiser-ish (Blackman) windowed sinc per phase; the three in-between points of every
    /// sample interval are computed. Cost ≈ 100 multiply-adds per sample: about 0.1 s for a minute of
    /// stereo audio, run on the renderers' background threads.
    /// </remarks>
    public double TruePeak()
    {
        const int Over = 4, Half = 16; // 4× oversampling, ±16 input samples per interpolated point
        // Pre-computed kernel for the 3 fractional phases (1/4, 2/4, 3/4).
        var kernel = new double[Over - 1, 2 * Half];
        for (int ph = 1; ph < Over; ph++)
        {
            double frac = (double)ph / Over;
            for (int k = 0; k < 2 * Half; k++)
            {
                double x = k - (Half - 1) - frac;                     // distance from the target point, in samples
                double sinc = Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                double w = 0.42 + 0.5 * Math.Cos(Math.PI * x / Half) + 0.08 * Math.Cos(2 * Math.PI * x / Half); // Blackman
                kernel[ph - 1, k] = sinc * w;
            }
        }
        double peak = Peak();
        foreach (var ch in Channels)
            for (int i = 0; i + 1 < ch.Length; i++)
                for (int ph = 1; ph < Over; ph++)
                {
                    // Point between sample i and i+1, built from samples i−15 … i+16 (zero outside).
                    double s = 0;
                    for (int k = 0; k < 2 * Half; k++)
                    {
                        int j = i - (Half - 1) + k;
                        if ((uint)j < (uint)ch.Length) s += ch[j] * kernel[ph - 1, k];
                    }
                    double a = Math.Abs(s);
                    if (a > peak) peak = a;
                }
        return peak;
    }

    /// <summary>
    /// Gain for a rendered file: with <paramref name="normalize"/> the true peak is brought exactly to
    /// <see cref="RenderCeilingDb"/>; without it the level is kept unless the true peak would exceed
    /// that ceiling (then it is lowered just enough). Never returns more gain than needed to reach it.
    /// </summary>
    /// <param name="normalize">True = loudness up/down to the ceiling; false = only reduce when needed.</param>
    /// <returns>Linear gain to multiply every sample by (1 = untouched).</returns>
    public double RenderGain(bool normalize)
    {
        double tp = TruePeak(), ceiling = Math.Pow(10, RenderCeilingDb / 20);
        if (tp < 1e-6) return 1;          // silence: nothing to scale
        if (normalize || tp > ceiling) return ceiling / tp;
        return 1;
    }

    /// <summary>Largest absolute sample over all channels (linear; 1 = 0 dBFS).</summary>
    /// <remarks>Sample peak only; renders use <see cref="TruePeak"/> through <see cref="RenderGain"/>. Can exceed 1 for float data.</remarks>
    public float Peak()
    {
        float p = 0;
        foreach (var ch in Channels)
            foreach (float v in ch)
            {
                float a = MathF.Abs(v);
                if (a > p) p = a;
            }
        return p;
    }
}

/// <summary>Reads and writes WAV files (see the file header for the supported formats).</summary>
/// <remarks>Stateless and thread-safe; every call works on its own file and buffers.</remarks>
public static class WavIO
{
    /// <summary>
    /// «fmt » format tags: 1 = integer PCM, 3 = IEEE float, 0xFFFE = WAVE_FORMAT_EXTENSIBLE (the
    /// real format is then the first two bytes of the sub-format GUID, which for KSDATAFORMAT_SUBTYPE_PCM
    /// / _IEEE_FLOAT are again 1 / 3).
    /// </summary>
    const ushort FormatPcm = 1, FormatFloat = 3, FormatExtensible = 0xFFFE;

    /// <summary>Reads a WAV file completely into memory and decodes it to float.</summary>
    /// <param name="path">The WAV file.</param>
    /// <returns>The de-interleaved samples (−1…1) and the file's sample rate.</returns>
    /// <exception cref="InvalidDataException">Not a WAV file, or an unsupported encoding (e.g. ADPCM, µ-law,
    /// A-law: <see cref="SoundFileLoader"/> then lets Csound decode it).</exception>
    /// <remarks>
    /// Tolerant of streaming writers that leave the «data» size at 0 / 0xFFFFFFFF, and of unknown
    /// chunks (LIST, bext, cue…), which are skipped. Chunks after «data» are never looked at, so a
    /// file whose «fmt » comes after its «data» is rejected (practically never seen).
    /// </remarks>
    public static AudioData Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var span = bytes.AsSpan();
        if (span.Length < 12 || Encoding.ASCII.GetString(span[..4]) != "RIFF" || Encoding.ASCII.GetString(span.Slice(8, 4)) != "WAVE")
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        // The RIFF size field (bytes 4…7) is deliberately ignored: many writers get it wrong; the
        // chunk walk below simply stops at the end of the buffer.
        int pos = 12;
        ushort format = 0, channels = 0, bits = 0;
        int rate = 0;
        int dataOffset = -1, dataLength = 0;
        // Walk the chunks: «fmt » describes the encoding, «data» holds the samples. Chunks are
        // word-aligned (an odd size is followed by one pad byte).
        while (pos + 8 <= span.Length)
        {
            string id = Encoding.ASCII.GetString(span.Slice(pos, 4));
            int size = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 4, 4));
            int body = pos + 8;
            if (size < 0 || body > span.Length) break;
            if (id == "fmt ")
            {
                // Offsets inside the «fmt » body: 0 format tag, 2 channels, 4 sample rate,
                // 8 byte rate, 12 block align, 14 bits per sample (byte rate and block align are
                // derived values, not needed here).
                format = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(body + 2, 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(body + 14, 2));
                // Extensible: the sub-format GUID starts at body offset 24 (after cbSize, valid
                // bits and channel mask). «bits» stays the CONTAINER size (e.g. 24-bit audio in
                // 32-bit words is decoded as 32-bit integers, which is still correctly scaled).
                if (format == FormatExtensible && size >= 40)
                    format = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(body + 24, 2)); // first 2 bytes of the sub-format GUID
            }
            else if (id == "data")
            {
                dataOffset = body;
                // Some writers leave the size at 0 or 0xFFFFFFFF while streaming: use what is there.
                dataLength = size <= 0 || body + size > span.Length ? span.Length - body : size;
                break; // everything needed is known; trailing chunks are ignored
            }
            // Next chunk: skip the body plus the RIFF pad byte of odd-sized bodies.
            pos = body + size + (size & 1);
        }
        if (dataOffset < 0 || channels == 0 || rate <= 0)
            throw new InvalidDataException("The WAV file has no usable fmt/data chunks.");
        int bytesPerSample = bits / 8;
        if (bytesPerSample == 0 || (format != FormatPcm && format != FormatFloat))
            throw new InvalidDataException($"Unsupported WAV encoding (format {format}, {bits} bits).");
        // Whole frames only: a truncated last frame (interrupted download/recording) is dropped.
        int frames = dataLength / (bytesPerSample * channels);
        var data = new float[channels][];
        for (int c = 0; c < channels; c++) data[c] = new float[frames];
        var d = span.Slice(dataOffset, frames * bytesPerSample * channels);
        // De-interleave: k is the byte offset of the next sample, advancing one sample at a time
        // through the interleaved frames.
        int k = 0;
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < channels; c++, k += bytesPerSample)
                data[c][f] = DecodeSample(d.Slice(k, bytesPerSample), format, bits);
        return new AudioData(data, rate);
    }

    /// <summary>Decodes one sample to −1…1.</summary>
    /// <param name="s">The sample's bytes (little-endian, exactly bits/8 long).</param>
    /// <param name="format"><see cref="FormatPcm"/> or <see cref="FormatFloat"/> (extensible already resolved).</param>
    /// <param name="bits">Container size: 8/16/24/32 for PCM, 32/64 for float.</param>
    /// <returns>The sample as float. Integer PCM is divided by 2^(bits−1), so the most negative code
    /// maps to exactly −1 and the most positive to just below +1. Unknown widths decode as 0 (silence).</returns>
    static float DecodeSample(ReadOnlySpan<byte> s, ushort format, int bits)
    {
        if (format == FormatFloat)
            return bits == 64 ? (float)BinaryPrimitives.ReadDoubleLittleEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s);
        return bits switch
        {
            8 => (s[0] - 128) / 128f,                       // 8-bit WAV is unsigned
            16 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
            24 => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f, // sign-extend 24 → 32 bits
            32 => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
            _ => 0f,
        };
    }

    /// <summary>Writes a 32-bit float WAV file (any channel count).</summary>
    /// <param name="path">Target file (overwritten).</param>
    /// <param name="channels">One array per channel, all the same length; values are written unclipped.</param>
    /// <param name="sampleRate">Sample rate stored in the header (Hz).</param>
    public static void WriteFloat32(string path, IReadOnlyList<float[]> channels, int sampleRate) =>
        Write(path, channels, sampleRate, 32, FormatFloat);

    /// <summary>Writes a mono 32-bit float WAV from doubles (used for the Csound data tables).</summary>
    /// <param name="path">Target file (overwritten).</param>
    /// <param name="samples">The numbers to store. They need not be audio: the standalone .csd reads
    /// them back with GEN01 (no rescaling), so this is just a compact, exact-to-float container for
    /// hundreds of thousands of table values.</param>
    /// <param name="sampleRate">Header sample rate; irrelevant for GEN01 table loading, hence the default.</param>
    /// <remarks>Values are narrowed to float (≈ 7 significant digits), which is what Csound's
    /// GEN01 would keep from a float file anyway.</remarks>
    public static void WriteFloat32(string path, double[] samples, int sampleRate = 48000)
    {
        var f = new float[samples.Length];
        for (int i = 0; i < f.Length; i++) f[i] = (float)samples[i];
        Write(path, [f], sampleRate, 32, FormatFloat);
    }

    /// <summary>Writes a 24-bit PCM WAV file (samples are clamped to −1…1 and rounded).</summary>
    /// <param name="path">Target file (overwritten).</param>
    /// <param name="channels">One array per channel, all the same length.</param>
    /// <param name="sampleRate">Sample rate stored in the header (Hz).</param>
    /// <remarks>No dither is applied: the renders are normalised to about −1 dBFS, where 24-bit
    /// truncation noise (≈ −144 dBFS) is irrelevant.</remarks>
    public static void WritePcm24(string path, IReadOnlyList<float[]> channels, int sampleRate) =>
        Write(path, channels, sampleRate, 24, FormatPcm);

    /// <summary>Common writer: RIFF header, «fmt » (16-byte PCM/float form) and «data».</summary>
    /// <param name="path">Target file (created or truncated).</param>
    /// <param name="channels">One array per channel; the length of channel 0 sets the frame count.</param>
    /// <param name="sampleRate">Header sample rate (Hz).</param>
    /// <param name="bits">32 (with <see cref="FormatFloat"/>) or 24 (with <see cref="FormatPcm"/>).</param>
    /// <param name="format">Format tag written in «fmt ».</param>
    /// <exception cref="InvalidOperationException">The data would not fit the 32-bit RIFF size fields (≈ 2 GB).</exception>
    static void Write(string path, IReadOnlyList<float[]> channels, int sampleRate, int bits, ushort format)
    {
        int nch = channels.Count;
        int frames = nch == 0 ? 0 : channels[0].Length;
        int bps = bits / 8;
        long dataBytes = (long)frames * nch * bps;
        if (dataBytes > int.MaxValue - 64)
            throw new InvalidOperationException("The audio is too long for a WAV file.");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var w = new BinaryWriter(fs); // BinaryWriter is little-endian, as RIFF requires
        w.Write("RIFF"u8);
        w.Write((int)(36 + dataBytes));         // RIFF size = "WAVE" (4) + fmt chunk (8 + 16) + data header (8) + data
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);                            // fmt body size: the plain PCM/float form
        w.Write(format);
        w.Write((ushort)nch);
        w.Write(sampleRate);
        w.Write(sampleRate * nch * bps);        // byte rate
        w.Write((ushort)(nch * bps));           // block align = bytes per frame
        w.Write((ushort)bits);
        w.Write("data"u8);
        w.Write((int)dataBytes);
        // Samples are encoded into a 4096-frame buffer and written in blocks (fast, bounded memory).
        // Note: no pad byte is written after an odd-sized data chunk (24-bit, odd channels × frames).
        var buf = new byte[Math.Max(1, nch * bps) * 4096];
        int n = 0;
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < nch; c++)
            {
                float v = channels[c][f];
                if (format == FormatFloat)
                    BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(n, 4), v);
                else
                {
                    // 24-bit PCM: scale by 2^23 − 1 (symmetric, so +1 does not overflow) and store
                    // the three low bytes of the two's-complement int, least significant first.
                    int q = (int)Math.Round(Math.Clamp(v, -1f, 1f) * 8388607f);
                    buf[n] = (byte)q; buf[n + 1] = (byte)(q >> 8); buf[n + 2] = (byte)(q >> 16);
                }
                n += bps;
            }
            if (n == buf.Length) { w.Write(buf, 0, n); n = 0; } // buffer holds whole frames: flush when full
        }
        if (n > 0) w.Write(buf, 0, n);
    }
}
