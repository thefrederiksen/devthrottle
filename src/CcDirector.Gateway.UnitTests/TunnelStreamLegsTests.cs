using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Xunit;
using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 2: in-process integration of the browser-facing up-stream legs over the
/// tunnel. A fake sendCommand plays the owning Director: on an open command it returns the typed result AND
/// (for a success) drives the Gateway <see cref="GatewayStreamRegistry"/> with produced frames exactly as the
/// real Director's StreamUp would. This exercises the file/screenshot and terminal legs end to end through the
/// real sinks and registry, including the Architect's error-parity and teardown proof additions.
/// </summary>
public sealed class TunnelStreamLegsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // -------------------------------------------------------------------------------- file leg ----

    [Fact]
    public async Task File_leg_streams_the_bytes_with_content_length_then_fires_close_stream()
    {
        var registry = new GatewayStreamRegistry();
        var fileBytes = Encoding.UTF8.GetBytes("the file contents, delivered as up-frames over the tunnel");
        var closeStreamSent = new List<string>();

        DirectorCommandRouter.SendDirectorCommandAsync send = (directorId, command, ct) =>
        {
            switch (command.Verb)
            {
                case "read-file":
                    var req = JsonSerializer.Deserialize<OpenStreamRequest>(command.PayloadJson, Json)!;
                    _ = registry.ConsumeAsync(req.StreamId, new StreamOwner(TenantId.Local, "dir1"), FileFrames(req.StreamId, fileBytes), CancellationToken.None);
                    return Ok(new OpenReadResponse { TotalBytes = fileBytes.Length, ContentType = "text/plain" });
                case "close-stream":
                    closeStreamSent.Add(JsonSerializer.Deserialize<CloseStreamRequest>(command.PayloadJson, Json)!.StreamId);
                    return Ok();
                default:
                    return Fail(DirectorCommandStatus.BadRequest, "unexpected verb");
            }
        };

        var legs = new TunnelStreamLegs(registry, send);
        var (ctx, body) = NewHttpContext();

        var handled = await legs.TryServeFileAsync(ctx, Guid.NewGuid().ToString(), TenantId.Local, "dir1", "/some/abs/path.txt");

        Assert.True(handled);
        Assert.Equal("text/plain", ctx.Response.ContentType);
        Assert.Equal(fileBytes.Length, ctx.Response.ContentLength);
        Assert.Equal(fileBytes, body.ToArray());
        Assert.Single(closeStreamSent);
        Assert.Equal(0, registry.LiveStreamCount);
    }

    [Fact]
    public async Task File_leg_maps_a_missing_path_to_404_matching_the_http_dial()
    {
        var registry = new GatewayStreamRegistry();
        DirectorCommandRouter.SendDirectorCommandAsync send = (d, c, ct) =>
            c.Verb == "read-file" ? Fail(DirectorCommandStatus.NotFound, "file not found") : Ok();
        var legs = new TunnelStreamLegs(registry, send);
        var (ctx, _) = NewHttpContext();

        var handled = await legs.TryServeFileAsync(ctx, Guid.NewGuid().ToString(), TenantId.Local, "dir1", "/missing");

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        Assert.Equal(0, registry.LiveStreamCount);
    }

    // Issue #4078: a refused read carries the Director's decision - its sentence AND its code - and the Gateway
    // hands both to the viewer verbatim, so the viewer can say why and, for a folder, open the listing.
    [Theory]
    [InlineData(DirectorCommandStatus.Conflict, "folder", "This is a folder, not a file.", StatusCodes.Status409Conflict)]
    [InlineData(DirectorCommandStatus.BadRequest, "not-allowed", "This file is not allowed for this session.", StatusCodes.Status400BadRequest)]
    [InlineData(DirectorCommandStatus.NotFound, "not-on-disk", "This file is not on disk.", StatusCodes.Status404NotFound)]
    public async Task File_leg_passes_the_directors_sentence_and_code_through(
        DirectorCommandStatus status, string code, string sentence, int expectedHttp)
    {
        var registry = new GatewayStreamRegistry();
        DirectorCommandRouter.SendDirectorCommandAsync send = (d, c, ct) =>
        {
            if (c.Verb != "read-file") return Ok();
            var refused = DirectorCommandResult.Fail(status, sentence);
            refused.BodyJson = JsonSerializer.Serialize(new PathStatusDto { Path = "/p", Code = code, Sentence = sentence }, Json);
            return Task.FromResult<DirectorCommandResult?>(refused);
        };
        var legs = new TunnelStreamLegs(registry, send);
        var (ctx, body) = NewHttpContext();

        var handled = await legs.TryServeFileAsync(ctx, Guid.NewGuid().ToString(), TenantId.Local, "dir1", "/p");

        Assert.True(handled);
        Assert.Equal(expectedHttp, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(body.ToArray());
        Assert.Equal(sentence, doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(code, doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task File_leg_returns_false_for_http_fallback_when_the_stream_was_lost()
    {
        var registry = new GatewayStreamRegistry();
        DirectorCommandRouter.SendDirectorCommandAsync send = (d, c, ct) => Task.FromResult<DirectorCommandResult?>(null);
        var legs = new TunnelStreamLegs(registry, send);
        var (ctx, body) = NewHttpContext();

        var handled = await legs.TryServeFileAsync(ctx, Guid.NewGuid().ToString(), TenantId.Local, "dir1", "/x");

        Assert.False(handled); // caller falls back to the HTTP proxy path
        Assert.Empty(body.ToArray());
        Assert.Equal(0, registry.LiveStreamCount);
    }

    // ---------------------------------------------------------------------------- terminal leg ----

    [Fact]
    public async Task Terminal_leg_open_notfound_tells_the_browser_and_closes()
    {
        var registry = new GatewayStreamRegistry();
        DirectorCommandRouter.SendDirectorCommandAsync send = (d, c, ct) =>
            c.Verb == "open-terminal-stream" ? Fail(DirectorCommandStatus.NotFound, "session not found") : Ok();
        var legs = new TunnelStreamLegs(registry, send);
        var stub = new StubServerWebSocket();
        var ctx = NewWebSocketContext(stub);

        await legs.ServeTerminalAsync(ctx, Guid.NewGuid().ToString(), TenantId.Local, "dir1");

        Assert.Contains(stub.SentText, t => t.Contains("\"type\":\"closed\"") && t.Contains("session not found"));
        Assert.Equal(0, registry.LiveStreamCount);
    }

    // ---------------------------------------------------------------------------------- helpers ----

    private static Task<DirectorCommandResult?> Ok(object? body = null) =>
        Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success(body is null ? null : JsonSerializer.Serialize(body, Json)));

    private static Task<DirectorCommandResult?> Fail(DirectorCommandStatus status, string error) =>
        Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Fail(status, error));

    private static async IAsyncEnumerable<DirectorStreamFrame> FileFrames(string streamId, byte[] bytes, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var mid = bytes.Length / 2;
        yield return new DirectorStreamFrame { StreamId = streamId, Kind = DirectorStreamFrameType.Binary, Data = bytes[..mid] };
        await Task.Yield();
        yield return new DirectorStreamFrame { StreamId = streamId, Kind = DirectorStreamFrameType.Binary, Data = bytes[mid..] };
        yield return new DirectorStreamFrame { StreamId = streamId, Kind = DirectorStreamFrameType.Closed, Reason = "eof" };
    }

    private static (DefaultHttpContext ctx, MemoryStream body) NewHttpContext()
    {
        var ctx = new DefaultHttpContext();
        var body = new MemoryStream();
        ctx.Response.Body = body;
        return (ctx, body);
    }

    private static DefaultHttpContext NewWebSocketContext(StubServerWebSocket stub)
    {
        var ctx = new DefaultHttpContext();
        ctx.Features.Set<IHttpWebSocketFeature>(new FakeWebSocketFeature(stub));
        return ctx;
    }

    private sealed class FakeWebSocketFeature : IHttpWebSocketFeature
    {
        private readonly WebSocket _ws;
        public FakeWebSocketFeature(WebSocket ws) => _ws = ws;
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(_ws);
    }

    // A server-side stub WebSocket: records sends, and its ReceiveAsync blocks until SignalClose() (or a
    // cancellation) delivers a Close result - modelling the browser disconnecting.
    private sealed class StubServerWebSocket : WebSocket
    {
        private readonly TaskCompletionSource _close = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> SentText { get; } = new();
        private int _binary;
        public int SentBinaryCount => Volatile.Read(ref _binary);
        private WebSocketState _state = WebSocketState.Open;

        public void SignalClose() => _close.TrySetResult();

        public override WebSocketState State => _state;

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct)
        {
            if (messageType == WebSocketMessageType.Text)
                lock (SentText) SentText.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            else
                Interlocked.Increment(ref _binary);
            return Task.CompletedTask;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            using var reg = ct.Register(() => _close.TrySetResult());
            await _close.Task;
            _state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) { _state = WebSocketState.CloseSent; return Task.CompletedTask; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }
    }
}
