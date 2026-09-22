using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace CcDirector.Gateway.Traffic;

/// <summary>
/// A response body feature that counts the bytes written through it and changes nothing else. It sits OUTSIDE
/// response compression, so what it counts is what actually leaves - the compressed bytes when the answer was
/// compressed.
///
/// IT NEVER BUFFERS. The pipe writer hands out the inner writer's own memory and only adds up what is advanced;
/// the stream writes straight through; flushes, file sends, buffering switches and completion are the inner
/// feature's own. So the event stream, which flushes each event as it happens, still does exactly that.
/// </summary>
internal sealed class CountingResponseBody : IHttpResponseBodyFeature
{
    private readonly IHttpResponseBodyFeature _inner;
    private CountingWriteStream? _stream;
    private CountingPipeWriter? _writer;
    private long _bytes;

    public CountingResponseBody(IHttpResponseBodyFeature inner) => _inner = inner;

    /// <summary>The body bytes written so far.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    internal void Add(long count)
    {
        if (count > 0) Interlocked.Add(ref _bytes, count);
    }

    public Stream Stream => _stream ??= new CountingWriteStream(_inner.Stream, this);

    public PipeWriter Writer => _writer ??= new CountingPipeWriter(_inner.Writer, this);

    public void DisableBuffering() => _inner.DisableBuffering();

    public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

    public async Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
    {
        await _inner.SendFileAsync(path, offset, count, cancellationToken);
        Add(count ?? Math.Max(0, new FileInfo(path).Length - offset));
    }

    public Task CompleteAsync() => _inner.CompleteAsync();
}

internal sealed class CountingPipeWriter : PipeWriter
{
    private readonly PipeWriter _inner;
    private readonly CountingResponseBody _owner;

    public CountingPipeWriter(PipeWriter inner, CountingResponseBody owner)
    {
        _inner = inner;
        _owner = owner;
    }

    public override void Advance(int bytes)
    {
        _inner.Advance(bytes);
        _owner.Add(bytes);
    }

    public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);

    public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);

    public override void CancelPendingFlush() => _inner.CancelPendingFlush();

    public override void Complete(Exception? exception = null) => _inner.Complete(exception);

    public override ValueTask CompleteAsync(Exception? exception = null) => _inner.CompleteAsync(exception);

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => _inner.FlushAsync(cancellationToken);

    public override async ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        var result = await _inner.WriteAsync(source, cancellationToken);
        _owner.Add(source.Length);
        return result;
    }

    public override bool CanGetUnflushedBytes => _inner.CanGetUnflushedBytes;

    public override long UnflushedBytes => _inner.UnflushedBytes;
}

internal sealed class CountingWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly CountingResponseBody _owner;

    public CountingWriteStream(Stream inner, CountingResponseBody owner)
    {
        _inner = inner;
        _owner = owner;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        _owner.Add(count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        _owner.Add(buffer.Length);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        _owner.Add(count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken);
        _owner.Add(buffer.Length);
    }
}

/// <summary>
/// Counts a request body that arrives without a Content-Length (chunked). A body WITH a Content-Length is
/// counted from the header and never wrapped - that is the exact number of bytes on the wire whether or not
/// the endpoint reads them.
/// </summary>
internal sealed class CountingReadStream : Stream
{
    private readonly Stream _inner;
    private long _bytes;

    public CountingReadStream(Stream inner) => _inner = inner;

    public long Bytes => Interlocked.Read(ref _bytes);

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        Interlocked.Add(ref _bytes, n);
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer);
        Interlocked.Add(ref _bytes, n);
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var n = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        Interlocked.Add(ref _bytes, n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken);
        Interlocked.Add(ref _bytes, n);
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
