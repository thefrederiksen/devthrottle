using CcDirector.Core.Memory;
using Xunit;

namespace CcDirector.Core.Tests;

public class CircularTerminalBufferTests
{
    // Regression: a background timer (TerminalStateDetector) read LastWriteAtUtc while
    // the session's buffer was being disposed on the teardown path. EnterReadLock on the
    // disposed ReaderWriterLockSlim threw ObjectDisposedException on the timer thread,
    // which is unhandled and terminated the whole process. Reads/writes must NEVER throw
    // because the buffer was (or is being) disposed.
    [Fact]
    public void Reads_AfterDispose_DoNotThrow()
    {
        var buffer = new CircularTerminalBuffer(64);
        buffer.Write("Hello"u8.ToArray());
        buffer.Dispose();

        // None of these may throw ObjectDisposedException.
        var exReadStamp = Record.Exception(() => _ = buffer.LastWriteAtUtc);
        var exTotal = Record.Exception(() => _ = buffer.TotalBytesWritten);
        var exDump = Record.Exception(() => _ = buffer.DumpAll());
        var exSince = Record.Exception(() => _ = buffer.GetWrittenSince(0));
        var exWrite = Record.Exception(() => buffer.Write("more"u8.ToArray()));
        var exClear = Record.Exception(() => buffer.Clear());

        Assert.Null(exReadStamp);
        Assert.Null(exTotal);
        Assert.Null(exDump);
        Assert.Null(exSince);
        Assert.Null(exWrite);
        Assert.Null(exClear);
        Assert.Empty(buffer.DumpAll());
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var buffer = new CircularTerminalBuffer(64);
        buffer.Dispose();
        var ex = Record.Exception(() => buffer.Dispose());
        Assert.Null(ex);
    }

    // Reproduces the crash shape: one thread hammers the read accessor (as the idle timer
    // did) while another disposes the buffer. Before the fix this raced into
    // ObjectDisposedException; after the fix the readers must complete without throwing.
    [Fact]
    public void ConcurrentReads_DuringDispose_NeverThrow()
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            var buffer = new CircularTerminalBuffer(4096);
            buffer.Write("seed"u8.ToArray());

            Exception? readerError = null;
            var reader = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 2000; i++)
                    {
                        _ = buffer.LastWriteAtUtc;
                        _ = buffer.TotalBytesWritten;
                        _ = buffer.GetWrittenSince(0);
                    }
                }
                catch (Exception ex)
                {
                    readerError = ex;
                }
            });

            reader.Start();
            Thread.Sleep(1);
            buffer.Dispose();
            reader.Join();

            Assert.Null(readerError);
        }
    }

    [Fact]
    public void WriteSmall_DumpAll_ReturnsWrittenBytes()
    {
        using var buffer = new CircularTerminalBuffer(64);
        var data = "Hello"u8.ToArray();

        buffer.Write(data);

        var dump = buffer.DumpAll();
        Assert.Equal(data, dump);
        Assert.Equal(5, buffer.TotalBytesWritten);
    }

    [Fact]
    public void WriteExactCapacity_DumpAll_ReturnsAllBytes()
    {
        using var buffer = new CircularTerminalBuffer(8);
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        buffer.Write(data);

        var dump = buffer.DumpAll();
        Assert.Equal(data, dump);
        Assert.Equal(8, buffer.TotalBytesWritten);
    }

    [Fact]
    public void WriteWrap_DumpAll_ReturnsLatestBytes()
    {
        using var buffer = new CircularTerminalBuffer(8);
        buffer.Write(new byte[] { 1, 2, 3, 4, 5, 6 }); // 6 bytes
        buffer.Write(new byte[] { 7, 8, 9, 10 });       // 4 more -> wraps

        var dump = buffer.DumpAll();
        // Buffer holds last 8 bytes: 3,4,5,6,7,8,9,10
        Assert.Equal(new byte[] { 3, 4, 5, 6, 7, 8, 9, 10 }, dump);
        Assert.Equal(10, buffer.TotalBytesWritten);
    }

    [Fact]
    public void WriteLargerThanCapacity_KeepsLastCapacityBytes()
    {
        using var buffer = new CircularTerminalBuffer(4);
        buffer.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        var dump = buffer.DumpAll();
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, dump);
        Assert.Equal(8, buffer.TotalBytesWritten);
    }

    [Fact]
    public void GetWrittenSince_FromZero_ReturnsAllWritten()
    {
        using var buffer = new CircularTerminalBuffer(64);
        buffer.Write("Hello"u8.ToArray());

        var (data, newPos) = buffer.GetWrittenSince(0);
        Assert.Equal("Hello"u8.ToArray(), data);
        Assert.Equal(5, newPos);
    }

    [Fact]
    public void GetWrittenSince_FromMiddle_ReturnsOnlyNew()
    {
        using var buffer = new CircularTerminalBuffer(64);
        buffer.Write("Hello"u8.ToArray());
        var (_, pos) = buffer.GetWrittenSince(0);

        buffer.Write(" World"u8.ToArray());
        var (data, newPos) = buffer.GetWrittenSince(pos);

        Assert.Equal(" World"u8.ToArray(), data);
        Assert.Equal(11, newPos);
    }

    [Fact]
    public void GetWrittenSince_CaughtUp_ReturnsEmpty()
    {
        using var buffer = new CircularTerminalBuffer(64);
        buffer.Write("Hello"u8.ToArray());

        var (_, pos) = buffer.GetWrittenSince(0);
        var (data, newPos) = buffer.GetWrittenSince(pos);

        Assert.Empty(data);
        Assert.Equal(pos, newPos);
    }

    [Fact]
    public void GetWrittenSince_StalePosition_ReturnsFullDump()
    {
        using var buffer = new CircularTerminalBuffer(8);
        buffer.Write(new byte[] { 1, 2, 3, 4 });
        var (_, pos) = buffer.GetWrittenSince(0);
        Assert.Equal(4, pos);

        // Write much more data so position 4 is overwritten
        buffer.Write(new byte[] { 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 });

        var (data, newPos) = buffer.GetWrittenSince(pos);
        // Should get full buffer dump since position is stale
        Assert.Equal(8, data.Length);
        Assert.Equal(new byte[] { 7, 8, 9, 10, 11, 12, 13, 14 }, data);
        Assert.Equal(14, newPos);
    }

    [Fact]
    public void Clear_ResetsBuffer()
    {
        using var buffer = new CircularTerminalBuffer(64);
        buffer.Write("Hello"u8.ToArray());

        buffer.Clear();

        Assert.Equal(0, buffer.TotalBytesWritten);
        Assert.Empty(buffer.DumpAll());
    }

    [Fact]
    public void EmptyWrite_IsNoop()
    {
        using var buffer = new CircularTerminalBuffer(64);
        buffer.Write(ReadOnlySpan<byte>.Empty);

        Assert.Equal(0, buffer.TotalBytesWritten);
        Assert.Empty(buffer.DumpAll());
    }

    [Fact]
    public async Task ConcurrentAccess_IsThreadSafe()
    {
        using var buffer = new CircularTerminalBuffer(1024);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var exceptions = new List<Exception>();

        var writers = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
        {
            var data = new byte[10];
            Array.Fill(data, (byte)(i + 1));
            // do-while: on a starved threadpool this task may only get scheduled AFTER
            // the 2s token has fired; each writer must still write at least once so the
            // TotalBytesWritten sanity assert below cannot fail on scheduling alone.
            do
            {
                try { buffer.Write(data); }
                catch (Exception ex) { lock (exceptions) exceptions.Add(ex); return; }
            }
            while (!cts.Token.IsCancellationRequested);
        })).ToArray();

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            long pos = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var (data, newPos) = buffer.GetWrittenSince(pos);
                    pos = newPos;
                    buffer.DumpAll();
                }
                catch (Exception ex) { lock (exceptions) exceptions.Add(ex); return; }
            }
        })).ToArray();

        await Task.WhenAll(writers.Concat(readers).ToArray());
        Assert.Empty(exceptions);
        Assert.True(buffer.TotalBytesWritten > 0);
    }

    [Fact]
    public void Constructor_InvalidCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CircularTerminalBuffer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CircularTerminalBuffer(-1));
    }

    [Fact]
    public void MultipleWraps_DataConsistent()
    {
        using var buffer = new CircularTerminalBuffer(8);

        // Write 3 rounds of data, each wrapping
        for (int round = 0; round < 3; round++)
        {
            buffer.Write(new byte[] { (byte)(round * 10 + 1), (byte)(round * 10 + 2), (byte)(round * 10 + 3),
                                      (byte)(round * 10 + 4), (byte)(round * 10 + 5), (byte)(round * 10 + 6) });
        }

        var dump = buffer.DumpAll();
        // Last 8 bytes written: 23,24,25,26 from round 2 + partial from earlier
        Assert.Equal(8, dump.Length);
        Assert.Equal(18 * 1, buffer.TotalBytesWritten);
    }
}
