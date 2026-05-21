using CcDirector.Core.Utilities;

namespace CcDirector.Core.Dictation;

/// <summary>
/// FIFO buffer of audio chunks used while the streaming provider is
/// unavailable so that no spoken audio is lost. The consumer drains the
/// buffer and feeds it to the provider when the connection returns.
///
/// Two modes:
/// - Memory-only (default): when the in-memory cap is exceeded, oldest
///   chunks are dropped and <see cref="Overflowed"/> latches to true.
/// - Memory + disk spill (when <see cref="SpillDirectory"/> is set):
///   instead of dropping, the oldest in-memory chunks are written to disk
///   and tracked separately so original order is preserved on drain.
///   <see cref="Spilled"/> latches when the first spill happens.
///
/// Invariant: every chunk in the disk queue has a lower sequence number
/// than every chunk in the memory queue, because we only ever spill the
/// oldest in-memory chunk. That keeps the two-queue model coherent without
/// per-chunk sorting.
///
/// Thread safety: a single producer and a single consumer is the intended
/// usage. Internally protected by a lock; disk I/O happens inside the lock
/// to keep ordering simple. Audio chunks are small, so the latency is
/// fine.
///
/// Disposal: deletes spill files when spill is enabled.
/// </summary>
public sealed class AudioBuffer : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<DiskChunk> _disk = new();
    private readonly Queue<MemChunk> _mem = new();
    private readonly string? _spillDirectory;
    private long _bytesInMemory;
    private long _bytesOnDisk;
    private long _nextSequence;
    private bool _disposed;

    /// <summary>Default capacity: ~60 seconds of 16 kHz / 16-bit mono PCM.</summary>
    public const long DefaultCapacityBytes = 16_000 * 2 * 60;

    /// <param name="capacityBytes">In-memory cap. Chunks beyond this are spilled to disk if spill is enabled, dropped otherwise.</param>
    /// <param name="spillDirectory">Directory for spill files. Null disables disk spill.</param>
    public AudioBuffer(long capacityBytes = DefaultCapacityBytes, string? spillDirectory = null)
    {
        if (capacityBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes), "capacity must be positive");
        CapacityBytes = capacityBytes;
        _spillDirectory = string.IsNullOrWhiteSpace(spillDirectory) ? null : spillDirectory;

        if (_spillDirectory is not null)
        {
            Directory.CreateDirectory(_spillDirectory);
            FileLog.Write($"[AudioBuffer] ctor: spill enabled at {_spillDirectory}");
        }
    }

    /// <summary>Maximum bytes the buffer will hold in memory before spilling or dropping.</summary>
    public long CapacityBytes { get; }

    /// <summary>Spill directory if disk spill is enabled, else null.</summary>
    public string? SpillDirectory => _spillDirectory;

    /// <summary>Total bytes currently held (memory + disk).</summary>
    public long BytesBuffered { get { lock (_gate) return _bytesInMemory + _bytesOnDisk; } }

    /// <summary>Bytes currently held in memory.</summary>
    public long BytesInMemory { get { lock (_gate) return _bytesInMemory; } }

    /// <summary>Total chunks currently held (memory + disk).</summary>
    public int ChunkCount { get { lock (_gate) return _mem.Count + _disk.Count; } }

    /// <summary>Chunks currently spilled to disk.</summary>
    public int SpilledChunkCount { get { lock (_gate) return _disk.Count; } }

    /// <summary>True if no chunks are buffered.</summary>
    public bool IsEmpty { get { lock (_gate) return _mem.Count == 0 && _disk.Count == 0; } }

    /// <summary>
    /// True if a previous append exceeded <see cref="CapacityBytes"/> while
    /// spill was disabled, causing oldest chunks to be dropped. Sticky until
    /// <see cref="Clear"/>.
    /// </summary>
    public bool Overflowed { get; private set; }

    /// <summary>
    /// True if a previous append spilled chunks to disk. Sticky until
    /// <see cref="Clear"/>.
    /// </summary>
    public bool Spilled { get; private set; }

    /// <summary>
    /// Append a chunk. If adding it would exceed the in-memory capacity, the
    /// oldest in-memory chunks are spilled to disk (when spill is enabled) or
    /// dropped (when spill is disabled) to make room.
    /// </summary>
    public void Append(byte[] chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Length == 0) return;

        lock (_gate)
        {
            ThrowIfDisposed();

            // Free space by displacing oldest in-memory chunks until the new
            // one fits. We never touch chunks already on disk; they have
            // strictly lower sequence numbers and stay in order.
            while (_bytesInMemory + chunk.Length > CapacityBytes && _mem.Count > 0)
            {
                var oldest = _mem.Dequeue();
                _bytesInMemory -= oldest.Data.Length;

                if (_spillDirectory is null)
                {
                    // No spill: drop it.
                    if (!Overflowed)
                    {
                        Overflowed = true;
                        FileLog.Write($"[AudioBuffer] Overflow (no spill dir): dropping oldest chunks");
                    }
                }
                else
                {
                    var path = WriteSpillFile(oldest.Sequence, oldest.Data);
                    _disk.Enqueue(new DiskChunk(oldest.Sequence, path, oldest.Data.Length));
                    _bytesOnDisk += oldest.Data.Length;
                    if (!Spilled)
                    {
                        Spilled = true;
                        FileLog.Write($"[AudioBuffer] Spilled first chunk to {path}");
                    }
                }
            }

            _mem.Enqueue(new MemChunk(_nextSequence++, chunk));
            _bytesInMemory += chunk.Length;
        }
    }

    /// <summary>
    /// Drain all currently buffered chunks in FIFO order. Spilled chunks are
    /// read back from disk; their files are deleted as they are read. The
    /// buffer is left empty when this method returns.
    /// </summary>
    public IReadOnlyList<byte[]> DrainAll()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_mem.Count == 0 && _disk.Count == 0) return Array.Empty<byte[]>();

            var result = new List<byte[]>(_mem.Count + _disk.Count);

            // Disk chunks first (older), then memory chunks (newer).
            while (_disk.Count > 0)
            {
                var d = _disk.Dequeue();
                try
                {
                    result.Add(File.ReadAllBytes(d.Path));
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[AudioBuffer] DrainAll: failed to read {d.Path}: {ex.Message}");
                }
                TryDelete(d.Path);
            }
            while (_mem.Count > 0)
            {
                result.Add(_mem.Dequeue().Data);
            }

            _bytesInMemory = 0;
            _bytesOnDisk = 0;
            FileLog.Write($"[AudioBuffer] DrainAll: returned {result.Count} chunks");
            return result;
        }
    }

    /// <summary>
    /// Reset to a fresh empty buffer. Deletes any spill files. Clears the
    /// overflow and spill flags.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            DeleteAllSpillFilesLocked();
            _mem.Clear();
            _disk.Clear();
            _bytesInMemory = 0;
            _bytesOnDisk = 0;
            Overflowed = false;
            Spilled = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DeleteAllSpillFilesLocked();
            _mem.Clear();
            _disk.Clear();
            _bytesInMemory = 0;
            _bytesOnDisk = 0;
        }
    }

    // ===== internals =========================================================

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AudioBuffer));
    }

    private string WriteSpillFile(long sequence, byte[] data)
    {
        var name = $"{sequence:D10}.bin";
        var final = Path.Combine(_spillDirectory!, name);
        var tmp = final + ".tmp";
        File.WriteAllBytes(tmp, data);
        if (File.Exists(final)) File.Delete(final);
        File.Move(tmp, final);
        return final;
    }

    private void DeleteAllSpillFilesLocked()
    {
        if (_spillDirectory is null) return;
        foreach (var d in _disk) TryDelete(d.Path);
        try
        {
            if (Directory.Exists(_spillDirectory))
            {
                foreach (var f in Directory.EnumerateFiles(_spillDirectory, "*.tmp"))
                    TryDelete(f);
            }
        }
        catch { /* best effort cleanup */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { FileLog.Write($"[AudioBuffer] TryDelete: {path}: {ex.Message}"); }
    }

    private readonly record struct MemChunk(long Sequence, byte[] Data);
    private readonly record struct DiskChunk(long Sequence, string Path, int Length);
}
