using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 2: the finite-read sink writes a file/screenshot's up-frames into the
/// browser HTTP response, setting Content-Type and Content-Length from the open reply BEFORE the first body
/// byte (Architect ruling 4). The header gate is the interesting part: the first Binary up-frame can race the
/// open reply, so WriteFrameAsync must wait for ApplyMetadata before it writes anything.
/// </summary>
public sealed class HttpResponseStreamSinkTests
{
    private static (DefaultHttpContext ctx, MemoryStream body) NewCtx()
    {
        var ctx = new DefaultHttpContext();
        var body = new MemoryStream();
        ctx.Response.Body = body;
        return (ctx, body);
    }

    [Fact]
    public async Task ApplyMetadata_sets_content_type_and_length_then_the_body_is_written()
    {
        var (ctx, body) = NewCtx();
        var sink = new HttpResponseStreamSink(ctx.Response);

        sink.ApplyMetadata(new OpenReadResponse { TotalBytes = 5, ContentType = "text/plain" });
        await sink.WriteFrameAsync(new DirectorStreamFrame { Kind = DirectorStreamFrameType.Binary, Data = new byte[] { 1, 2, 3, 4, 5 } }, default);

        Assert.Equal("text/plain", ctx.Response.ContentType);
        Assert.Equal(5, ctx.Response.ContentLength);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, body.ToArray());
    }

    [Fact]
    public async Task Fail_releases_the_gate_and_WriteFrameAsync_throws_without_writing()
    {
        var (ctx, body) = NewCtx();
        var sink = new HttpResponseStreamSink(ctx.Response);

        sink.Fail();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sink.WriteFrameAsync(new DirectorStreamFrame { Kind = DirectorStreamFrameType.Binary, Data = new byte[] { 1 } }, default));
        Assert.Empty(body.ToArray());
    }
}
