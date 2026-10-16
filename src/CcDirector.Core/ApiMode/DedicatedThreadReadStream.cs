using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.ApiMode;

/// <summary>
/// A read-only stream that reads a blocking stream on a thread of its own and hands the bytes over through a
/// channel, so every read from it can be cancelled.
///
/// Why it exists: on Windows, .NET pipes a child's standard output and error through SYNCHRONOUS anonymous pipes.
/// A ReadAsync on such a stream is a blocking read on a thread-pool thread, and its cancellation token is checked
/// only before the read starts. When an agent exits while something it started still holds its pipes, the
/// transport's reads could then never be stopped, and each would hold a pool thread until that grandchild exits.
/// Here the transport's read waits on the channel, which cancellation always ends; the blocking read is on a
/// dedicated thread, and on Windows disposal cancels it with CancelSynchronousIo.
///
/// One known gap, on Windows only: a read that begins in the instant between the thread's check for disposal and
/// the cancel is not interrupted, and that one thread then stays blocked until the pipe closes - a dedicated thread,
/// never a pool thread. On Linux and macOS this stream is not used: their pipe reads are cancellable already.
/// </summary>
internal sealed class DedicatedThreadReadStream : Stream
{
    private const int ChunkBytes = 64 * 1024;

    private readonly Stream _inner;
    private readonly string _name;
    private readonly Channel<byte[]> _chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16) { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly object _threadGate = new();
    private readonly Thread _thread;
    private IntPtr _threadHandle;
    private byte[] _current = [];
    private int _currentOffset;
    private int _disposed;

    public DedicatedThreadReadStream(Stream inner, string name)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _name = name;
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = $"API-mode {name} reader" };
        _thread.Start();
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_currentOffset >= _current.Length)
        {
            // Throws the reader thread's failure, if it ended with one.
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                return 0;
            if (_chunks.Reader.TryRead(out var next))
            {
                _current = next;
                _currentOffset = 0;
            }
        }

        var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
        _current.AsMemory(_currentOffset, count).CopyTo(buffer);
        _currentOffset += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            FileLog.Write($"[DedicatedThreadReadStream] Dispose: {_name}");
            _stop.Cancel();
            lock (_threadGate)
            {
                if (OperatingSystem.IsWindows() && _threadHandle != IntPtr.Zero && !NativeMethods.CancelSynchronousIo(_threadHandle))
                {
                    // ERROR_NOT_FOUND (1168): the thread was not inside a read at that moment, which is the ordinary case.
                    var error = Marshal.GetLastWin32Error();
                    if (error != 1168)
                        FileLog.Write($"[DedicatedThreadReadStream] Dispose FAILED: {_name}: CancelSynchronousIo error {error}");
                }
            }
        }
        base.Dispose(disposing);
    }

    private void ReadLoop()
    {
        if (OperatingSystem.IsWindows())
        {
            lock (_threadGate)
                _threadHandle = NativeMethods.OpenThread(NativeMethods.ThreadTerminate, false, NativeMethods.GetCurrentThreadId());
        }

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var buffer = new byte[ChunkBytes];
                var read = _inner.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                _chunks.Writer.WriteAsync(buffer[..read], _stop.Token).AsTask().GetAwaiter().GetResult();
            }

            _chunks.Writer.TryComplete();
        }
        catch (Exception ex) when (_stop.IsCancellationRequested)
        {
            // not-an-error: the stream was disposed, which is what stopped the read
            FileLog.Write($"[DedicatedThreadReadStream] ReadLoop: {_name} stopped on disposal ({ex.GetType().Name})");
            _chunks.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DedicatedThreadReadStream] ReadLoop FAILED: {_name}: {ex.Message}");
            _chunks.Writer.TryComplete(ex);
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                lock (_threadGate)
                {
                    if (_threadHandle != IntPtr.Zero)
                        NativeMethods.CloseHandle(_threadHandle);
                    _threadHandle = IntPtr.Zero;
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static class NativeMethods
    {
        public const uint ThreadTerminate = 0x0001;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CancelSynchronousIo(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
