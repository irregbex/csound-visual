using System.Runtime.InteropServices;
using System.Text;

namespace CsoundVisual.Engine;

/// <summary>
/// Records the live output to a 32-bit float WAV file while playing, without blocking the audio
/// thread: blocks are copied into a queue on the performance thread and written to disk by a
/// background thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who uses it.</b> <c>CsoundEngine.StartRecording</c> (UI thread) creates one when the user
/// presses <i>Grabar/Record</i>; the performance thread calls <see cref="Push"/> after every
/// <c>csoundPerformKsmps</c> with Csound's <c>spout</c> buffer; the UI reads <see cref="Seconds"/>
/// for the timer. <c>CsoundEngine.StopRecording</c> swaps the reference out and disposes it,
/// which drains the ring and patches the header. A <see cref="Push"/> racing with that disposal
/// is waited for (in-flight counter), so its block still reaches the file.
/// </para>
/// <para>
/// <b>Threads.</b> Producer = Csound performance thread; consumer = the "WAV recorder" thread
/// (below-normal priority, so disk I/O never competes with audio). The hand-off is a lock-free
/// single-producer/single-consumer ring of floats preallocated in the constructor (about 4 s),
/// plus an <see cref="AutoResetEvent"/> to wake the writer. <see cref="Push"/> does not allocate
/// (earlier versions allocated one array per control cycle); if the disk stalls long enough to
/// fill the ring, whole blocks are dropped and counted in <see cref="DroppedBlocks"/> instead of
/// blocking the audio thread.
/// </para>
/// <para>
/// <b>File format.</b> Canonical 44-byte RIFF/WAVE header with a 16-byte <c>fmt </c> chunk,
/// format tag 3 (IEEE float), 32 bits per sample, interleaved. The header is written with size 0
/// at creation and rewritten with the real sizes on <see cref="Dispose"/>; sizes are clamped to
/// 4 GiB (RIFF limit), so a recording longer than that has a truncated size field.
/// </para>
/// </remarks>
public sealed class WavRecorder : IDisposable
{
    /// <summary>
    /// Single-producer/single-consumer ring of interleaved samples (about
    /// <see cref="RingSeconds"/> seconds), preallocated so <see cref="Push"/> never allocates.
    /// </summary>
    readonly float[] ring;
    /// <summary>Total samples ever written into <see cref="ring"/> (producer-owned; published with <see cref="Volatile.Write(ref long, long)"/>).</summary>
    long writePos;
    /// <summary>Total samples ever taken out of <see cref="ring"/> (consumer-owned; published with <see cref="Volatile.Write(ref long, long)"/>).</summary>
    long readPos;
    /// <summary>Number of <see cref="Push"/> calls currently running; <see cref="Dispose"/> waits for it to reach 0.</summary>
    int activePushes;
    /// <summary>Blocks dropped because the ring was full (disk stalled for seconds). Interlocked.</summary>
    long droppedBlocks;
    /// <summary>Seconds of audio the ring can absorb while the disk is busy.</summary>
    const int RingSeconds = 4;
    /// <summary>Signalled by <see cref="Push"/> and <see cref="Dispose"/> to wake the writer thread.</summary>
    readonly AutoResetEvent pending = new(false);
    /// <summary>The output file (1 MiB buffer; shared for reading so Explorer can peek at it).</summary>
    readonly FileStream file;
    /// <summary>Little-endian writer over <see cref="file"/>; only the writer thread uses it until <see cref="Dispose"/>.</summary>
    readonly BinaryWriter writer;
    /// <summary>The "WAV recorder" background thread running <see cref="WriteLoop"/>.</summary>
    readonly Thread worker;
    /// <summary>Interleaved channel count (≥ 1).</summary>
    readonly int channels;
    /// <summary>Set once by <see cref="Dispose"/>; makes <see cref="Push"/> a no-op and ends the writer after draining.</summary>
    volatile bool stopping;
    /// <summary>Set by <see cref="Dispose"/> once no <see cref="Push"/> is running any more; tells the writer to drain and exit.</summary>
    volatile bool producerDone;
    /// <summary>Bytes of sample data written so far (interlocked; read by <see cref="Seconds"/> on the UI thread).</summary>
    long dataBytes;

    /// <summary>Full path of the file being written.</summary>
    public string Path { get; }

    /// <summary>Seconds recorded so far (bytes ÷ 4 bytes per float ÷ channels ÷ rate); counts only what reached the file.</summary>
    public double Seconds => Interlocked.Read(ref dataBytes) / 4.0 / channels / SampleRate;

    /// <summary>Sample rate of the recording in Hz (Csound's sr).</summary>
    public int SampleRate { get; }

    /// <summary>
    /// Control-cycle blocks dropped because the disk could not keep up for several seconds (the
    /// ring was full). 0 in normal use; a non-zero value means the file has gaps.
    /// </summary>
    public long DroppedBlocks => Interlocked.Read(ref droppedBlocks);

    /// <summary>Creates the file and starts the writer thread.</summary>
    /// <param name="path">Destination file; its folder is created if needed and an existing file is overwritten.</param>
    /// <param name="sampleRate">Csound's sr in Hz.</param>
    /// <param name="channelCount">Interleaved channels per frame (Csound's nchnls); values below 1 become 1.</param>
    /// <exception cref="IOException">When the file cannot be created (the caller reports it).</exception>
    public WavRecorder(string path, int sampleRate, int channelCount)
    {
        Path = path;
        SampleRate = sampleRate;
        channels = Math.Max(1, channelCount);
        // Allocated once here (UI thread), never on the performance thread.
        ring = new float[Math.Max(1, sampleRate) * channels * RingSeconds];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        writer = new BinaryWriter(file);
        WriteHeader(0);
        worker = new Thread(WriteLoop) { IsBackground = true, Name = "WAV recorder", Priority = ThreadPriority.BelowNormal };
        worker.Start();
    }

    /// <summary>
    /// Queues one block of interleaved samples (±1.0 full scale). Called from the performance
    /// thread; only copies memory.
    /// </summary>
    /// <param name="interleaved">Csound's output buffer (<c>spout</c>, doubles in 0dbfs units); read only during the call.</param>
    /// <param name="samples">Total samples (frames × channels) to copy.</param>
    /// <param name="scale">Multiplier to ±1.0 full scale, i.e. <c>1 / 0dbfs</c>. No clipping is applied: float WAV keeps overs.</param>
    public unsafe void Push(double* interleaved, int samples, float scale)
    {
        // Bug fix (Push racing Dispose): announce this call before looking at «stopping».
        // Dispose sets «stopping» and then waits until no Push is in flight, so either this call
        // sees «stopping» and leaves, or Dispose waits for it to finish copying — the last block
        // can no longer be dropped by the writer exiting early, and pending.Set() can no longer
        // run on an already disposed event.
        Interlocked.Increment(ref activePushes);
        try
        {
            if (stopping || samples <= 0) return;

            // Bug fix (allocation on the performance thread): samples are copied into a ring
            // preallocated by the constructor instead of a new float[] per control cycle.
            // Single producer (this thread) / single consumer (WriteLoop): the producer owns
            // «writePos», the consumer owns «readPos»; each publishes with Volatile.Write.
            long write = writePos;
            long free = ring.Length - (write - Volatile.Read(ref readPos));
            if (samples > free)
            {
                // Disk too slow for several seconds: drop the block rather than block the audio.
                Interlocked.Increment(ref droppedBlocks);
                return;
            }
            int at = (int)(write % ring.Length);
            for (int i = 0; i < samples; i++)
            {
                ring[at] = (float)(interleaved[i] * scale);
                if (++at == ring.Length) at = 0;
            }
            Volatile.Write(ref writePos, write + samples);
            pending.Set();
        }
        finally
        {
            Interlocked.Decrement(ref activePushes);
        }
    }

    /// <summary>Writer thread: drains the ring to disk until <see cref="stopping"/> is set and the ring is empty.</summary>
    void WriteLoop()
    {
        while (true)
        {
            // The 200 ms timeout is a safety net; normally Push/Dispose signal the event.
            pending.WaitOne(200);
            // Read «producerDone» BEFORE draining: Dispose only sets it after every Push finished,
            // so when it is seen here everything the producer will ever write is already in the
            // ring. («stopping» is not enough: a Push may still be copying when it is set.)
            bool last = producerDone;
            long write = Volatile.Read(ref writePos);
            while (readPos < write)
            {
                // Write the contiguous part up to the end of the ring, then wrap.
                int at = (int)(readPos % ring.Length);
                int count = (int)Math.Min(write - readPos, ring.Length - at);
                writer.Write(MemoryMarshal.AsBytes(ring.AsSpan(at, count)));   // little-endian floats
                Volatile.Write(ref readPos, readPos + count);
                Interlocked.Add(ref dataBytes, count * 4L);
            }
            if (last)
                break;
        }
    }

    /// <summary>Writes the 44-byte header at the current position for <paramref name="bytes"/> of sample data.</summary>
    /// <remarks>
    /// Strictly, a non-PCM format should carry a <c>fact</c> chunk and a <c>cbSize</c> field; every
    /// common reader accepts this minimal float header, so the simpler layout is kept.
    /// </remarks>
    void WriteHeader(long bytes)
    {
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((uint)Math.Min(uint.MaxValue, 36 + bytes));   // RIFF size = file size − 8
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16u);                                         // fmt chunk size
        writer.Write((ushort)3);                 // IEEE float
        writer.Write((ushort)channels);
        writer.Write((uint)SampleRate);
        writer.Write((uint)(SampleRate * channels * 4));           // byte rate
        writer.Write((ushort)(channels * 4));                      // block align (bytes per frame)
        writer.Write((ushort)32);                                  // bits per sample
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write((uint)Math.Min(uint.MaxValue, bytes));
    }

    /// <summary>Flushes pending audio, fixes the header sizes and closes the file.</summary>
    /// <remarks>
    /// Blocks until the writer thread has drained the queue, so call it from the UI or engine
    /// shutdown path, not from inside a control cycle with a long backlog. Idempotent.
    /// </remarks>
    public void Dispose()
    {
        if (stopping) return;
        stopping = true;
        // Full fence: the store to «stopping» must be visible before activePushes is read
        // (Dekker-style hand-shake with the Interlocked.Increment at the top of Push).
        Interlocked.MemoryBarrier();
        // Wait for a Push that was already past its «stopping» check (it copies one control
        // cycle, microseconds), so its block is drained below and it never touches the event
        // after it is disposed.
        var spin = new SpinWait();
        while (Volatile.Read(ref activePushes) != 0)
            spin.SpinOnce();
        producerDone = true;   // from now on nothing more can enter the ring
        pending.Set();
        worker.Join();
        writer.Flush();
        file.Position = 0;
        WriteHeader(dataBytes);
        writer.Dispose();
        pending.Dispose();
    }
}
