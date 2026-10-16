using System.Collections.Concurrent;
using System.Text;
using CcDirector.Core.ApiMode;
using Xunit;

namespace CcDirector.Core.Tests.ApiMode;

public sealed class DedicatedThreadReadStreamTests
{
    /// <summary>
    /// A stream whose Read blocks until the test hands it bytes, as a synchronous Windows pipe does. A null chunk is
    /// the end of the stream; a chunk of one 0xFF byte with <see cref="FailNext"/> set makes the read throw.
    /// </summary>
    private sealed class BlockingStream : Stream
    {
        private readonly BlockingCollection<byte[]?> _chunks = new();

        public Exception? FailNext { get; set; }

        public void Hand(byte[]? chunk) => _chunks.Add(chunk);

        public override int Read(byte[] buffer, int offset, int count)
        {
            var chunk = _chunks.Take();
            if (FailNext is { } failure)
                throw failure;
            if (chunk is null)
                return 0;
            chunk.CopyTo(buffer, offset);
            return chunk.Length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ReadAsync_BytesThenEnd_PassesThemThroughInOrderThenReturnsZero()
    {
        // Arrange
        var inner = new BlockingStream();
        using var stream = new DedicatedThreadReadStream(inner, "test output");
        inner.Hand(Encoding.UTF8.GetBytes("{\"type\":"));
        inner.Hand(Encoding.UTF8.GetBytes("\"agent_start\"}\n"));
        inner.Hand(null);

        // Act
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync();

        // Assert
        Assert.Equal("{\"type\":\"agent_start\"}\n", text);
    }

    [Fact]
    public async Task ReadAsync_CancelledWhileTheBlockingReadWaits_EndsTheReadAtOnce()
    {
        // Arrange: nothing is ever handed, so the dedicated thread stays inside Read.
        var inner = new BlockingStream();
        var stream = new DedicatedThreadReadStream(inner, "test output");
        using var cancellation = new CancellationTokenSource();
        var read = stream.ReadAsync(new byte[16], cancellation.Token).AsTask();

        // Act
        cancellation.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        stream.Dispose();
        inner.Hand(null);
    }

    [Fact]
    public async Task ReadAsync_TheBlockingReadFails_ThrowsThatFailure()
    {
        // Arrange
        var inner = new BlockingStream { FailNext = new IOException("The pipe has been ended.") };
        using var stream = new DedicatedThreadReadStream(inner, "test output");
        inner.Hand([0xFF]);

        // Act
        var ex = await Assert.ThrowsAsync<IOException>(() => stream.ReadAsync(new byte[16]).AsTask());

        // Assert
        Assert.Equal("The pipe has been ended.", ex.Message);
    }

    [Fact]
    public async Task ReadAsync_SmallerBufferThanTheChunk_ReturnsTheRestOnTheNextRead()
    {
        // Arrange
        var inner = new BlockingStream();
        using var stream = new DedicatedThreadReadStream(inner, "test output");
        inner.Hand([1, 2, 3, 4, 5]);
        var buffer = new byte[3];

        // Act
        var first = await stream.ReadAsync(buffer);
        var firstBytes = buffer[..first];
        var second = await stream.ReadAsync(buffer);

        // Assert
        Assert.Equal([1, 2, 3], firstBytes);
        Assert.Equal([4, 5], buffer[..second]);
        inner.Hand(null);
    }
}
