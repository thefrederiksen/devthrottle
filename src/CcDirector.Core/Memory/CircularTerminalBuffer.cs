using CcDirector.Core.Utilities;

namespace CcDirector.Core.Memory;

/// <summary>
/// Thread-safe circular byte buffer for raw terminal output.
/// Stores raw ANSI bytes with no line parsing.
/// </summary>
public sealed class CircularTerminalBuffer : IDisposable
{
    private readonly byte[] _buffer;
    private readonly int _capacity;
    private readonly ReaderWriterLockSlim _lock = new();

    private int _writeHead;       // Next write position in the circular buffer
    private long _totalWritten;   // Monotonic counter - never wraps
    private DateTime _lastWriteAtUtc = DateTime.MinValue;
    private volatile bool _disposed;

    // Lock contention tracking
    private int _writeLockWaitCount;
    private int _readLockWaitCount;
    private DateTime _lastLockLogTime = DateTime.MinValue;

    public CircularTerminalBuffer(int capacity = 2_097_152)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");

        _capacity = capacity;
        _buffer = new byte[capacity];
    }

    /// <summary>Total bytes ever written. Monotonically increasing, used for stream position tracking.</summary>
    public long TotalBytesWritten
    {
        get
        {
            if (!TryEnterReadLock()) return Interlocked.Read(ref _totalWritten);
            try { return _totalWritten; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// UTC timestamp of the most recent successful Write. <see cref="DateTime.MinValue"/>
    /// before the first write. Used as the session-freshness signal surfaced to the
    /// Gateway directory view as <c>LastActivityAt</c>.
    /// </summary>
    public DateTime LastWriteAtUtc
    {
        get
        {
            // A disposed buffer means the session was torn down. Return the last
            // known timestamp without touching the (possibly disposed) lock rather
            // than throwing ObjectDisposedException on a caller's thread -- a throw
            // here from a background timer (TerminalStateDetector) would terminate
            // the whole process.
            if (!TryEnterReadLock()) return _lastWriteAtUtc;
            try { return _lastWriteAtUtc; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Fires after a successful Write, on the producer thread, with a freshly
    /// allocated copy of the data that was just written. Used by per-session
    /// consumers (e.g. a VT emulator for the HTML view) that need every byte
    /// in order without holding the buffer lock. The PTY drain loop is the
    /// only producer in production, so callbacks see writes in chronological
    /// order. Handlers must not throw; an exception is caught and logged.
    /// </summary>
    public event Action<byte[]>? OnBytesWritten;

    /// <summary>Append bytes to the buffer. Wraps around when full.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        if (_disposed) return; // buffer torn down; nothing more is written

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool acquired;
        try { acquired = _lock.TryEnterWriteLock(TimeSpan.FromMilliseconds(100)); }
        catch (ObjectDisposedException) { return; } // raced with Dispose
        sw.Stop();

        if (!acquired)
        {
            _writeLockWaitCount++;
            FileLog.Write($"[CircularTerminalBuffer] Write lock timeout after 100ms, waitCount={_writeLockWaitCount}, dataLen={data.Length}");
            // Force acquire (blocking) - we need to log if this happens
            try { _lock.EnterWriteLock(); }
            catch (ObjectDisposedException) { return; } // raced with Dispose
            FileLog.Write($"[CircularTerminalBuffer] Write lock finally acquired after blocking");
        }
        else if (sw.ElapsedMilliseconds > 10)
        {
            FileLog.Write($"[CircularTerminalBuffer] Write lock slow: {sw.ElapsedMilliseconds}ms, dataLen={data.Length}");
        }

        try
        {
            // If data is larger than capacity, only keep the last _capacity bytes
            if (data.Length >= _capacity)
            {
                data.Slice(data.Length - _capacity).CopyTo(_buffer);
                _writeHead = 0;
                _totalWritten += data.Length;
            }
            else
            {
                int firstPart = Math.Min(data.Length, _capacity - _writeHead);
                data.Slice(0, firstPart).CopyTo(_buffer.AsSpan(_writeHead, firstPart));

                if (firstPart < data.Length)
                {
                    // Wrap around
                    int secondPart = data.Length - firstPart;
                    data.Slice(firstPart, secondPart).CopyTo(_buffer.AsSpan(0, secondPart));
                }

                _writeHead = (_writeHead + data.Length) % _capacity;
                _totalWritten += data.Length;
            }
            _lastWriteAtUtc = DateTime.UtcNow;
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        var handler = OnBytesWritten;
        if (handler is not null)
        {
            var copy = data.ToArray();
            try { handler(copy); }
            catch (Exception ex)
            {
                FileLog.Write($"[CircularTerminalBuffer] OnBytesWritten handler threw: {ex.Message}");
            }
        }
    }

    /// <summary>Return all valid bytes in chronological order.</summary>
    public byte[] DumpAll()
    {
        if (!TryEnterReadLock()) return Array.Empty<byte>();
        try
        {
            if (_totalWritten == 0)
                return Array.Empty<byte>();

            if (_totalWritten < _capacity)
            {
                // Buffer hasn't wrapped yet - data is [0.._writeHead)
                var result = new byte[(int)_totalWritten];
                Array.Copy(_buffer, 0, result, 0, (int)_totalWritten);
                return result;
            }

            // Buffer is full or has wrapped.
            if (_writeHead == 0)
            {
                // Write head at start means buffer is exactly full or multiple-of-capacity
                var result = new byte[_capacity];
                Array.Copy(_buffer, 0, result, 0, _capacity);
                return result;
            }

            // Valid data: [_writeHead.._capacity) + [0.._writeHead)
            var output = new byte[_capacity];
            int tailLen = _capacity - _writeHead;
            Array.Copy(_buffer, _writeHead, output, 0, tailLen);
            Array.Copy(_buffer, 0, output, tailLen, _writeHead);
            return output;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Get bytes written since the given position.
    /// If position is stale (data has been overwritten), returns a full dump.
    /// Returns (data, newPosition) where newPosition should be passed to the next call.
    /// </summary>
    public (byte[] Data, long NewPosition) GetWrittenSince(long position)
    {
        if (_disposed) return (Array.Empty<byte>(), Interlocked.Read(ref _totalWritten));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool acquired;
        try { acquired = _lock.TryEnterReadLock(TimeSpan.FromMilliseconds(100)); }
        catch (ObjectDisposedException) { return (Array.Empty<byte>(), Interlocked.Read(ref _totalWritten)); }
        sw.Stop();

        if (!acquired)
        {
            _readLockWaitCount++;
            FileLog.Write($"[CircularTerminalBuffer] Read lock timeout after 100ms, waitCount={_readLockWaitCount}, pos={position}");
            // Force acquire (blocking)
            try { _lock.EnterReadLock(); }
            catch (ObjectDisposedException) { return (Array.Empty<byte>(), Interlocked.Read(ref _totalWritten)); }
            FileLog.Write($"[CircularTerminalBuffer] Read lock finally acquired after blocking");
        }
        else if (sw.ElapsedMilliseconds > 10)
        {
            FileLog.Write($"[CircularTerminalBuffer] Read lock slow: {sw.ElapsedMilliseconds}ms");
        }

        try
        {
            if (position >= _totalWritten)
            {
                // Caught up - nothing new
                return (Array.Empty<byte>(), _totalWritten);
            }

            long available = _totalWritten - position;

            if (available > _capacity)
            {
                // Position is stale (data was overwritten). Return full buffer.
                FileLog.Write($"[CircularTerminalBuffer] Position stale, returning full dump: pos={position}, total={_totalWritten}, available={available}");
                var dump = DumpAllInternal();
                return (dump, _totalWritten);
            }

            int count = (int)available;
            var result = new byte[count];

            // Calculate where in the circular buffer this data starts
            // The byte at position P is at buffer offset: (writeHead - (totalWritten - P)) mod capacity
            int startOffset = (int)((_writeHead - count % _capacity + _capacity) % _capacity);

            int firstPart = Math.Min(count, _capacity - startOffset);
            Array.Copy(_buffer, startOffset, result, 0, firstPart);

            if (firstPart < count)
            {
                Array.Copy(_buffer, 0, result, firstPart, count - firstPart);
            }

            return (result, _totalWritten);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Reset the buffer.</summary>
    public void Clear()
    {
        if (_disposed) return;
        try { _lock.EnterWriteLock(); }
        catch (ObjectDisposedException) { return; } // raced with Dispose
        try
        {
            Array.Clear(_buffer);
            _writeHead = 0;
            _totalWritten = 0;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Acquire the read lock unless the buffer is being/has been disposed. Returns
    /// false (caller must NOT call ExitReadLock) when the buffer is torn down, so
    /// readers can return a safe default instead of throwing ObjectDisposedException.
    /// This is the last-resort guard for the inherent race between a reader on a
    /// background thread and Dispose() on the teardown path.
    /// </summary>
    private bool TryEnterReadLock()
    {
        if (_disposed) return false;
        try
        {
            _lock.EnterReadLock();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false; // Dispose() raced us between the _disposed check and EnterReadLock
        }
    }

    /// <summary>Internal dump - caller must hold read lock.</summary>
    private byte[] DumpAllInternal()
    {
        if (_totalWritten == 0)
            return Array.Empty<byte>();

        if (_totalWritten < _capacity)
        {
            var result = new byte[(int)_totalWritten];
            Array.Copy(_buffer, 0, result, 0, (int)_totalWritten);
            return result;
        }

        if (_writeHead == 0)
        {
            var result = new byte[_capacity];
            Array.Copy(_buffer, 0, result, 0, _capacity);
            return result;
        }

        var output = new byte[_capacity];
        int tailLen = _capacity - _writeHead;
        Array.Copy(_buffer, _writeHead, output, 0, tailLen);
        Array.Copy(_buffer, 0, output, tailLen, _writeHead);
        return output;
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Drain any in-flight readers/writers before disposing the lock: take the
        // write lock (which waits for current lock holders to exit), flip _disposed
        // while holding it so no new reader proceeds past TryEnterReadLock, then
        // release and dispose. Without this drain, a reader sitting inside the lock
        // when we disposed it would fault. The volatile _disposed plus the ODE
        // guards in the accessors cover the narrow check-then-acquire race.
        bool taken = false;
        try
        {
            _lock.EnterWriteLock();
            taken = true;
        }
        catch (ObjectDisposedException)
        {
            return; // already disposed by a concurrent caller
        }

        try
        {
            _disposed = true;
        }
        finally
        {
            if (taken) _lock.ExitWriteLock();
        }

        _lock.Dispose();
    }
}
