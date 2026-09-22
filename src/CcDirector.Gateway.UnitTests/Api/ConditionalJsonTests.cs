using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// Traffic optimization, phase 1: the conversation and the roster answer an unchanged poll with a 304 and no
/// body. The whole safety of that rests on one property - the tag changes whenever ANY byte of the answer
/// changes - so these pin it field by field for the things a stale answer would hide: a new turn, an edited
/// turn, the Wingman's verdict, a roster colour, the stale notice and the history state.
///
/// Revert-proof: make <see cref="ConditionalJson.TagFor"/> hash anything less than the whole body (for
/// instance only the message count) and the edited-turn, verdict, stale-notice and history-state cases go red.
/// </summary>
public sealed class ConditionalJsonTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static SessionHistoryDto History(string lastText = "done", string? staleNotice = null, string? historyState = null) => new()
    {
        SessionId = "5b0e1c52-7f59-4c1e-9d1a-0b8b1f6d0a11",
        DirectorId = "dir-a",
        Agent = "ClaudeCode",
        IsSupported = true,
        HistoryState = historyState,
        StaleNotice = staleNotice,
        Messages =
        {
            new HistoryMessageDto { Role = "User", Parts = { new HistoryPartDto { Kind = "Text", Text = "do the thing" } } },
            new HistoryMessageDto { Role = "Assistant", Parts = { new HistoryPartDto { Kind = "Text", Text = lastText } } },
        },
    };

    private static string TagOf(object value) =>
        ConditionalJson.TagFor(JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), Web));

    private static DefaultHttpContext Context(string? ifNoneMatch = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        ctx.Response.Body = new MemoryStream();
        if (ifNoneMatch is not null)
            ctx.Request.Headers.IfNoneMatch = ifNoneMatch;
        return ctx;
    }

    private static async Task<(int Status, byte[] Body)> Execute(DefaultHttpContext ctx, IResult result)
    {
        await result.ExecuteAsync(ctx);
        return (ctx.Response.StatusCode, ((MemoryStream)ctx.Response.Body).ToArray());
    }

    [Fact]
    public void TagFor_SameBytes_SameStrongTag()
    {
        var a = TagOf(History());
        var b = TagOf(History());

        Assert.Equal(a, b);
        Assert.StartsWith("\"", a);
        Assert.False(a.StartsWith("W/", StringComparison.Ordinal));
    }

    [Fact]
    public void TagFor_NewTurn_ChangesTag()
    {
        var before = History();
        var after = History();
        after.Messages.Add(new HistoryMessageDto { Role = "User", Parts = { new HistoryPartDto { Kind = "Text", Text = "next" } } });

        Assert.NotEqual(TagOf(before), TagOf(after));
    }

    [Fact]
    public void TagFor_EditedTurnSameCount_ChangesTag()
    {
        Assert.NotEqual(TagOf(History(lastText: "done")), TagOf(History(lastText: "done.")));
    }

    [Fact]
    public void TagFor_StaleNoticeOrHistoryState_ChangesTag()
    {
        var live = TagOf(History());
        Assert.NotEqual(live, TagOf(History(staleNotice: "That computer is offline.")));
        Assert.NotEqual(live, TagOf(History(historyState: "unsupported")));
    }

    [Fact]
    public void TagFor_RosterVerdictOrColour_ChangesTag()
    {
        SessionDto Row(string color, string? verdictId) => new()
        {
            SessionId = "s1",
            Agent = "claude",
            StatusColor = color,
            TurnVerdict = verdictId is null ? null : new TurnVerdictDto { VerdictId = verdictId },
        };

        var baseline = TagOf(new List<SessionDto> { Row("red", "v1") });
        Assert.Equal(baseline, TagOf(new List<SessionDto> { Row("red", "v1") }));
        Assert.NotEqual(baseline, TagOf(new List<SessionDto> { Row("blue", "v1") }));
        Assert.NotEqual(baseline, TagOf(new List<SessionDto> { Row("red", "v2") }));
        Assert.NotEqual(baseline, TagOf(new List<SessionDto> { Row("red", null) }));
    }

    [Fact]
    public async Task Serve_NoIfNoneMatch_200WithBodyTagAndCacheControl()
    {
        var ctx = Context();
        var dto = History();

        var (status, body) = await Execute(ctx, ConditionalJson.Serve(ctx, dto, Web));

        Assert.Equal(200, status);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(dto, Web), body);
        Assert.Equal(TagOf(dto), ctx.Response.Headers.ETag.ToString());
        Assert.Equal("no-cache, private", ctx.Response.Headers.CacheControl.ToString());
        Assert.Equal("application/json; charset=utf-8", ctx.Response.ContentType);
    }

    [Fact]
    public async Task Serve_MatchingIfNoneMatch_304NoBodySameTag()
    {
        var dto = History();
        var ctx = Context(TagOf(dto));

        var (status, body) = await Execute(ctx, ConditionalJson.Serve(ctx, dto, Web));

        Assert.Equal(304, status);
        Assert.Empty(body);
        Assert.Equal(TagOf(dto), ctx.Response.Headers.ETag.ToString());
        Assert.Equal("no-cache, private", ctx.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Serve_TagOfOlderAnswer_200WithNewBody()
    {
        var old = History(lastText: "working on it");
        var now = History(lastText: "finished");
        var ctx = Context(TagOf(old));

        var (status, body) = await Execute(ctx, ConditionalJson.Serve(ctx, now, Web));

        Assert.Equal(200, status);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(now, Web), body);
        Assert.Equal(TagOf(now), ctx.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task Serve_UsesTheHostJsonOptions_SameBytesAsResultsJson()
    {
        // The overload the endpoints call reads the host's own JSON options, so the body is byte-for-byte what
        // Results.Json wrote before this change - no reader of these two routes sees a different answer.
        var dto = History();
        var viaResultsJson = Context();
        await Results.Json(dto).ExecuteAsync(viaResultsJson);
        var viaConditional = Context();

        await ConditionalJson.Serve(viaConditional, dto).ExecuteAsync(viaConditional);

        Assert.Equal(((MemoryStream)viaResultsJson.Response.Body).ToArray(), ((MemoryStream)viaConditional.Response.Body).ToArray());
        Assert.Equal(viaResultsJson.Response.ContentType, viaConditional.Response.ContentType);
    }

    [Theory]
    [InlineData("\"abc\"", true)]
    [InlineData("W/\"abc\"", true)]              // If-None-Match compares weakly (RFC 9110 13.1.2)
    [InlineData("\"zzz\", \"abc\"", true)]
    [InlineData("*", true)]
    [InlineData("\"abd\"", false)]
    [InlineData("abc", false)]                   // unquoted does not parse: answer in full, the safe direction
    [InlineData("", false)]
    public void Matches_IfNoneMatchForms(string header, bool expected)
    {
        Assert.Equal(expected, ConditionalJson.Matches(header, "\"abc\""));
    }

    [Theory]
    [InlineData("/events", true)]
    [InlineData("/EVENTS", true)]
    [InlineData("/director-stream", true)]
    [InlineData("/director-stream/negotiate", true)]
    [InlineData("/launcher-stream", true)]
    [InlineData("/launcher-stream/negotiate", true)]
    [InlineData("/sessions", false)]
    [InlineData("/sessions/5b0e1c52-7f59-4c1e-9d1a-0b8b1f6d0a11/history", false)]
    [InlineData("/directors/d1/events", false)]  // a JSON list, not a stream
    [InlineData("/director-streams", false)]
    [InlineData("/eventsx", false)]
    public void IsStreamingPath_OnlyTheLiveStreams(string path, bool expected)
    {
        Assert.Equal(expected, GatewayResponseCompression.IsStreamingPath(new PathString(path)));
    }
}
