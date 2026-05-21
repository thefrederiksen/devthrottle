using CcDirector.Core.Utilities;

namespace CcDirector.Core.Dictation;

/// <summary>
/// In-memory ring buffer of audio chunks used while the network is down so
/// that no spoken audio is lost. When the connection returns the consumer
/// drains the buffer and feeds it to the streaming provider in order.
///
/// Phase 1 keeps everything in RAM with a configurable cap. Phase 4 will
/// add disk spill for sessions that exceed the cap.
///
/// Thread safety: a single producer (audio capture) and a single consumer
/// (the network-restore drain) is the intended usage. Internally protected
/// by a lock so callers do not need to coordinate.
/// </summary>
public sealed class AudioBuffer
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _chunks = new();
    private long _bytesBuffered;

    public AudioBuffer(long capacityBytes = DefaultCapacityBytes)
    {
        if (capacityBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes), "capacity must be positive");
        CapacityBytes = capacityBytes;
    }

    /// <summary>Default capacity: ~60 seconds of 16 kHz / 16-bit mono PCM.</summary>
    public const long DefaultCapacityBytes = 16_000 * 2 * 60;

    /// <summary>Maximum bytes the buffer will hold before overflowing.</summary>
    public long CapacityBytes { get; }

    /// <summary>Total bytes currently held.</summary>
    public long BytesBuffered { get { lock (_gate) return _bytesBuffered; } }

    /// <summary>Number of chunks currently held.</summary>
    public int ChunkCount { get { lock (_gate) return _chunks.Count; } }

    /// <summary>True if no chunks are buffered.</summary>
    public bool IsEmpty { get { lock (_gate) return _chunks.Count == 0; } }

    /// <summary>
    /// True if a previous append exceeded <see cref="CapacityBytes"/> and the
    /// oldest chunks had to be dropped. Sticky until <see cref="Clear"/>.
    /// </summary>
    public bool Overflowed { get; private set; }

    /// <summary>
    /// Append a chunk. If adding it would exceed capacity, the oldest chunks
    /// are dropped to make room and <see cref="Overflowed"/> latches to true.
    /// </summary>
    public void Append(byte[] chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Length == 0) return;

        lock (_gate)
        {
            _chunks.Enqueue(chunk);
            _bytesBuffered += chunk.Length;

            while (_bytesBuffered > CapacityBytes && _chunks.Count > 1)
            {
                var dropped = _chunks.Dequeue();
                _bytesBuffered -= dropped.Length;
                if (!Overflowed)
                {
                    Overflowed = true;
                    FileLog.Write($"[AudioBuffer] Overflow: capacity={CapacityBytes} bytes, dropping oldest chunks");
                }
            }
        }
    }

    /// <summary>
    /// Drain all currently buffered chunks in FIFO order. The buffer is left
    /// empty when this method returns. Subsequent appends start a new run.
    /// </summary>
    public IReadOnlyList<byte[]> DrainAll()
    {
        lock (_gate)
        {
            if (_chunks.Count == 0) return Array.Empty<byte[]>();
            var result = _chunks.ToArray();
            _chunks.Clear();
            _bytesBuffered = 0;
            FileLog.Write($"[AudioBuffer] DrainAll: returned {result.Length} chunks");
            return result;
        }
    }

    /// <summary>
    /// Reset to a fresh empty buffer. Clears the overflow flag.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _chunks.Clear();
            _bytesBuffered = 0;
            Overflowed = false;
        }
    }
}
