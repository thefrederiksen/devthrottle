using System.Net;
using System.Text;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// A SESSION'S REQUEST TO TALK TO ANOTHER, IN THE DIRECTOR (issue #3851). What these hold down: the Director's client
/// reads <c>GET /fleet/link-requests</c> and answers with exactly <c>{ approve: true }</c> or <c>{ decline: true }</c>; a
/// refusal comes back in the Gateway's own words; the cache keeps only waiting requests, keyed by the session that
/// ASKED, keeps its last list when a read fails, and drops an answered request at once; and the words match the
/// Cockpit's.
///
/// The client is the REAL <see cref="GatewayClient"/> behind a stub message handler. Plain [Fact]: nothing here touches
/// an Avalonia object.
/// </summary>
public sealed class LinkAskTests
{
    private const string Investigator = "11111111-2222-3333-4444-555555555555";
    private const string Coordinator = "66666666-7777-8888-9999-000000000000";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

        public string? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Method = request.Method;
            RequestUri = request.RequestUri?.ToString();
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return _answer(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static GatewayClient ClientAnswering(StubHandler handler) =>
        new(new GatewayConfig { Url = "http://gateway.example:7878", Token = "test-token" },
            directorId: "director-under-test", version: "1.0.0", handler);

    private static FleetMessageLinkRequestDto Request(string id, string status = "pending", string requester = Investigator,
        string? targetName = "Cube Coordinator", string? amount = "ongoing") => new()
    {
        RequestId = id,
        RequesterSessionId = requester,
        TargetSessionId = Coordinator,
        TargetSessionName = targetName,
        Reason = "I need the open tickets list",
        RequestedAmount = amount,
        Status = status,
    };

    private sealed class FakeGateway : IGatewayLinkRequests
    {
        public List<FleetMessageLinkRequestDto> List { get; set; } = new();
        public Exception? ReadFails { get; set; }
        public Exception? AnswerFails { get; set; }
        public List<(string Id, bool Approve)> Answers { get; } = new();

        public Task<List<FleetMessageLinkRequestDto>?> ListLinkRequestsAsync(CancellationToken ct = default)
            => ReadFails is { } ex ? Task.FromException<List<FleetMessageLinkRequestDto>?>(ex)
                : Task.FromResult<List<FleetMessageLinkRequestDto>?>(List.ToList());

        public Task<FleetMessageLinkRequestAnswerResponse> AnswerLinkRequestAsync(string requestId, bool approve, CancellationToken ct = default)
        {
            if (AnswerFails is { } ex) return Task.FromException<FleetMessageLinkRequestAnswerResponse>(ex);
            Answers.Add((requestId, approve));
            List.RemoveAll(r => r.RequestId == requestId);
            return Task.FromResult(new FleetMessageLinkRequestAnswerResponse());
        }
    }

    // ---- the client ------------------------------------------------------------------------------------

    [Fact]
    public async Task The_client_reads_the_request_list_route()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            "{\"requests\":[{\"requestId\":\"r1\",\"requesterSessionId\":\"" + Investigator + "\",\"targetSessionId\":\""
            + Coordinator + "\",\"targetSessionName\":\"Cube Coordinator\",\"reason\":\"why\",\"requestedAmount\":\"ongoing\","
            + "\"status\":\"pending\"}],\"answeredWithinDays\":7}"));

        var list = await ClientAnswering(handler).ListLinkRequestsAsync();

        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("http://gateway.example:7878/fleet/link-requests", handler.RequestUri);
        var only = Assert.Single(list!);
        Assert.Equal("Cube Coordinator", only.TargetSessionName);
        Assert.Equal("ongoing", only.RequestedAmount);
    }

    [Theory]
    [InlineData(true, "\"approve\":true")]
    [InlineData(false, "\"decline\":true")]
    public async Task The_client_answers_with_approve_or_decline_and_nothing_else(bool approve, string expected)
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"request\":{\"requestId\":\"r1\",\"status\":\"allowed\"}}"));

        await ClientAnswering(handler).AnswerLinkRequestAsync("r1", approve);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("http://gateway.example:7878/fleet/link-requests/r1/answer", handler.RequestUri);
        Assert.Contains(expected, handler.RequestBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"amount\":\"", handler.RequestBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_refused_answer_throws_the_Gateways_own_words()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Conflict,
            "{\"code\":\"already_answered\",\"error\":\"This request was already declined. Nothing changed.\"}"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ClientAnswering(handler).AnswerLinkRequestAsync("r1", true));
        Assert.Equal("This request was already declined. Nothing changed.", ex.Message);
    }

    // ---- the cache -------------------------------------------------------------------------------------

    [Fact]
    public async Task The_cache_keeps_only_waiting_requests_keyed_by_the_session_that_asked()
    {
        var gateway = new FakeGateway { List = { Request("r1"), Request("r2", status: "allowed"), Request("r3", requester: Coordinator) } };
        var cache = new LinkRequestsCache(() => gateway);
        var changed = 0;
        cache.Changed += () => changed++;

        await cache.RefreshAsync();

        Assert.Equal(new[] { "r1", "r3" }, cache.Current.Select(r => r.RequestId));
        Assert.Equal("r1", Assert.Single(cache.From(Investigator.ToUpperInvariant())).RequestId);
        Assert.Equal(1, changed);

        await cache.RefreshAsync();
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task A_failed_read_keeps_the_last_list_and_says_why()
    {
        var gateway = new FakeGateway { List = { Request("r1") } };
        var cache = new LinkRequestsCache(() => gateway);
        await cache.RefreshAsync();

        gateway.ReadFails = new InvalidOperationException("Gateway unreachable");
        await cache.RefreshAsync();

        Assert.Equal("r1", Assert.Single(cache.Current).RequestId);
        Assert.Equal("Gateway unreachable", cache.Error);
    }

    [Fact]
    public async Task An_answered_request_leaves_at_once_and_a_refusal_leaves_it_waiting()
    {
        var gateway = new FakeGateway { List = { Request("r1"), Request("r2") } };
        var cache = new LinkRequestsCache(() => gateway);
        await cache.RefreshAsync();

        await cache.AnswerAsync("r1", approve: true);
        Assert.Equal(("r1", true), Assert.Single(gateway.Answers));
        Assert.DoesNotContain(cache.Current, r => r.RequestId == "r1");

        gateway.AnswerFails = new InvalidOperationException("This request was already declined. Nothing changed.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.AnswerAsync("r2", approve: false));
        Assert.Contains(cache.Current, r => r.RequestId == "r2");
    }

    [Fact]
    public async Task With_no_Gateway_the_cache_reads_nothing_and_an_answer_fails_loud()
    {
        var cache = new LinkRequestsCache(() => null);
        await cache.RefreshAsync();
        Assert.Empty(cache.Current);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.AnswerAsync("r1", true));
    }

    // ---- the words -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("once", "one message")]
    [InlineData("once-with-reply", "one message and a reply")]
    [InlineData("ongoing", "as much as they need")]
    [InlineData(null, "one message and a reply")]
    public void The_amount_reads_as_the_Cockpit_reads_it(string? amount, string words)
        => Assert.Equal(words, LinkAskWords.Amount(amount));

    [Fact]
    public void The_target_is_named_by_the_Gateway_then_this_Director_then_its_short_id()
    {
        Assert.Equal("Cube Coordinator (66666666)", LinkAskWords.Target(Request("r1"), _ => "Local name"));
        Assert.Equal("Local name (66666666)", LinkAskWords.Target(Request("r1", targetName: null), _ => "Local name"));
        Assert.Equal("66666666", LinkAskWords.Target(Request("r1", targetName: null), _ => null));
    }

    [Fact]
    public void The_line_and_the_chip_say_who_it_wants_to_talk_to()
    {
        var one = new[] { Request("r1") };
        var two = new[] { Request("r1"), Request("r2") };

        Assert.Equal("wants to talk to Cube Coordinator (66666666)", LinkAskWords.Line(one, _ => null));
        Assert.Equal("Wants to talk to Cube Coordinator (66666666)", LinkAskWords.Chip(one, _ => null));
        Assert.Equal("Wants to talk to 2 sessions", LinkAskWords.Chip(two, _ => null));
        Assert.Equal("", LinkAskWords.Line(Array.Empty<FleetMessageLinkRequestDto>(), _ => null));
    }
}
