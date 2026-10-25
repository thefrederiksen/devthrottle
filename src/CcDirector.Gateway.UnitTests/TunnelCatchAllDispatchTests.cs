using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 2 (PR B1/B2): the catch-all session verbs dispatched over the tunnel must
/// produce the SAME HTTP response the Director REST route (via the HTTP dial) returned - the serialized DTO
/// with 200/application/json on success, the matching typed error code otherwise - and must marshal the
/// request faithfully: reads and no-body writes carry no payload, body writes pass the raw body through, and
/// the queue path-parameterised verbs fold the {itemId} path segment into the payload. An unmapped path or no
/// active stream returns false so the caller keeps the HTTP path.
/// </summary>
public sealed class TunnelCatchAllDispatchTests
{
    private static (DefaultHttpContext ctx, MemoryStream body) NewCtx(string method, string requestBody = "")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestBody));
        var body = new MemoryStream();
        ctx.Response.Body = body;
        return (ctx, body);
    }

    private static string BodyText(MemoryStream body) => Encoding.UTF8.GetString(body.ToArray());

    private static DirectorCommandRouter.SendDirectorCommandAsync Capture(out Func<DirectorCommand?> seen, DirectorCommandResult? reply)
    {
        DirectorCommand? captured = null;
        seen = () => captured;
        return (d, c, ct) => { captured = c; return Task.FromResult(reply); };
    }

    // -------------------------------------------------------------------------------- reads ----

    [Fact]
    public async Task Turns_read_writes_the_dto_as_200_json_matching_the_http_dial()
    {
        const string dto = "{\"sessionId\":\"s\",\"status\":\"ok\",\"widgets\":[]}";
        var send = Capture(out var seen, DirectorCommandResult.Success(dto));
        var dispatch = new TunnelCatchAllDispatch(send);
        var (ctx, body) = NewCtx("GET");

        var handled = await dispatch.TryDispatchAsync(ctx, Guid.NewGuid().ToString(), "dir1", "turns");

        Assert.True(handled);
        Assert.Equal("turns", seen()!.Verb);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal("application/json", ctx.Response.ContentType);
        Assert.Equal(dto, BodyText(body));
    }

    [Theory]
    [InlineData("turns", "turns")]
    [InlineData("buffer/html", "buffer-html")]
    [InlineData("usage", "usage")]
    [InlineData("context", "context")]
    [InlineData("github-urls", "github-urls")]
    [InlineData("queue", "queue-read")]
    public async Task Each_catch_all_read_path_maps_to_its_verb(string rest, string expectedVerb)
    {
        var send = Capture(out var seen, DirectorCommandResult.Success("{}"));
        var dispatch = new TunnelCatchAllDispatch(send);
        var (ctx, _) = NewCtx("GET");

        var handled = await dispatch.TryDispatchAsync(ctx, Guid.NewGuid().ToString(), "dir1", rest);

        Assert.True(handled);
        Assert.Equal(expectedVerb, seen()!.Verb);
        Assert.Equal("", seen()!.PayloadJson); // reads carry no payload
    }

    [Fact]
    public async Task History_is_NOT_dispatched_down_the_tunnel_any_more()
    {
        // The turn-push mission's whole point: a conversation is read from the Gateway's own store, not
        // fetched from the owning Director on every 2.5-second Chat poll. The literal route
        // (SessionConversationEndpoint) serves it; this dispatcher must not claim the path, or a request
        // would go back down the tunnel and re-parse a transcript on the user's disk.
        var send = Capture(out var seen, DirectorCommandResult.Success("{}"));
        var dispatch = new TunnelCatchAllDispatch(send);
        var (ctx, _) = NewCtx("GET");

        var handled = await dispatch.TryDispatchAsync(ctx, Guid.NewGuid().ToString(), "dir1", "history");

        Assert.False(handled);
        Assert.Null(seen());
    }

    [Fact]
    public async Task NotFound_maps_to_404_and_BadRequest_to_400()
    {
        var dispatch404 = new TunnelCatchAllDispatch((d, c, ct) =>
            Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found")));
        var (ctx404, _) = NewCtx("GET");
        Assert.True(await dispatch404.TryDispatchAsync(ctx404, Guid.NewGuid().ToString(), "dir1", "turns"));
        Assert.Equal(StatusCodes.Status404NotFound, ctx404.Response.StatusCode);

        var dispatch400 = new TunnelCatchAllDispatch((d, c, ct) =>
            Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format")));
        var (ctx400, _) = NewCtx("GET");
        Assert.True(await dispatch400.TryDispatchAsync(ctx400, "not-a-guid", "dir1", "turns"));
        Assert.Equal(StatusCodes.Status400BadRequest, ctx400.Response.StatusCode);
    }

    // ------------------------------------------------------------------------------- writes ----

    [Fact]
    public async Task Body_write_passes_the_raw_request_body_through_as_the_payload()
    {
        const string reqBody = "{\"cols\":100,\"rows\":40}";
        var send = Capture(out var seen, DirectorCommandResult.Success());
        var dispatch = new TunnelCatchAllDispatch(send);
        var (ctx, _) = NewCtx("POST", reqBody);

        var handled = await dispatch.TryDispatchAsync(ctx, Guid.NewGuid().ToString(), "dir1", "resize");

        Assert.True(handled);
        Assert.Equal("resize", seen()!.Verb);
        Assert.Equal(reqBody, seen()!.PayloadJson);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Finding2_raw_terminal_input_is_not_a_public_route()
    {
        var dispatch = new TunnelCatchAllDispatch(Capture(out var seen, DirectorCommandResult.Success()));
        var (ctx, _) = NewCtx("POST", "{\"provenance\":{\"identityKind\":\"owner\"}}");

        Assert.False(await dispatch.TryDispatchAsync(ctx, Guid.NewGuid().ToString(), "dir1", "terminal-input"));
        Assert.Null(seen());
    }

    [Theory]
    [InlineData("POST", "keys", "terminal-keys")]
    [InlineData("POST", "paste", "terminal-paste")]
    [InlineData("POST", "mouse", "terminal-mouse")]
    [InlineData("POST", "clear-context", "clear-context")]
    [InlineData("POST", "mobile-mode", "mobile-mode")]
    [InlineData("POST", "voice-mode", "voice-mode")]
    [InlineData("POST", "wingman-enabled", "wingman-enabled")]
    [InlineData("POST", "relink", "relink")]
    [InlineData("POST", "execute-action", "execute-action")]
    [InlineData("POST", "queue", "queue-add")]
    [InlineData("DELETE", "queue", "queue-clear")]
    public async Task Each_catch_all_write_path_maps_to_its_verb(string method, string rest, string expectedVerb)
    {
        var send = Capture(out var seen, DirectorCommandResult.Success());
        var dispatch = new TunnelCatchAllDispatch(send);
        var (ctx, _) = NewCtx(method, "{}");

        var handled = await dispatch.TryDispatchAsync(ctx, Guid.NewGuid().ToString(), "dir1", rest);

        Assert.True(handled);
        Assert.Equal(expectedVerb, seen()!.Verb);
    }

    [Fact]
    public async Task Queue_update_folds_the_path_itemId_into_the_body()
    {
        var send = Capture(out var seen, DirectorCommandResult.Success("{}"));
        var dispatch = new TunnelCatchAllDispatch(send);
        var (ctx, _) = NewCtx("PATCH", "{\"text\":\"edited prompt\"}");

        var handled = await dispatch.TryDispatchAsync(ctx, Guid.NewGuid().ToString(), "dir1", "queue/item-123");

        Assert.True(handled);
        Assert.Equal("queue-update", seen()!.Verb);
        var payload = JsonNode.Parse(seen()!.PayloadJson)!.AsObject();
        Assert.Equal("edited prompt", (string?)payload["text"]);
        Assert.Equal("item-123", (string?)payload["itemId"]);
    }

    [Theory]
    [InlineData("DELETE", "queue/item-9", "queue-remove")]
    [InlineData("POST", "queue/item-9/move-up", "queue-move-up")]
    [InlineData("POST", "queue/item-9/move-down", "queue-move-down")]
    [InlineData("POST", "queue/item-9/send", "queue-send")]
    public async Task Queue_path_param_verbs_fold_the_itemId_with_no_body(string method, string rest, string expectedVerb)
    {
        var send = Capture(out var seen, DirectorCommandResult.Success("{}"));
        var dispatch = new TunnelCatchAllDispatch(send);
        var (ctx, _) = NewCtx(method);

        var handled = await dispatch.TryDispatchAsync(ctx, Guid.NewGuid().ToString(), "dir1", rest);

        Assert.True(handled);
        Assert.Equal(expectedVerb, seen()!.Verb);
        var payload = JsonNode.Parse(seen()!.PayloadJson)!.AsObject();
        Assert.Equal("item-9", (string?)payload["itemId"]);
    }

    // ------------------------------------------------------------------------------ fallthrough ----

    [Fact]
    public async Task An_unmapped_rest_path_falls_through_to_http_without_dialing()
    {
        var called = false;
        var dispatch = new TunnelCatchAllDispatch((d, c, ct) => { called = true; return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success("{}")); });
        var (ctx, _) = NewCtx("GET");

        var handled = await dispatch.TryDispatchAsync(ctx, Guid.NewGuid().ToString(), "dir1", "not-a-real-verb");

        Assert.False(handled);
        Assert.False(called);
    }

    [Fact]
    public async Task No_active_stream_falls_through_and_rewinds_the_body_for_the_http_forward()
    {
        var dispatch = new TunnelCatchAllDispatch((d, c, ct) => Task.FromResult<DirectorCommandResult?>(null));
        var (ctx, _) = NewCtx("POST", "{\"cols\":80,\"rows\":24}");

        var handled = await dispatch.TryDispatchAsync(ctx, Guid.NewGuid().ToString(), "dir1", "resize");

        Assert.False(handled);                       // caller falls back to the HTTP proxy path
        Assert.Equal(0, ctx.Request.Body.Position);  // body rewound so the HTTP forward re-reads it
    }
}
