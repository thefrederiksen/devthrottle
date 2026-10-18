using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// Session Rules part 2, phase 1: the Director's two session-rule calls on its existing Gateway client -
/// W1 (the effective hook rules for a machine and repo) and W5 (forwarding the hook's decisions as
/// firings). These drive the REAL <see cref="GatewayClient"/> through a stub message handler, so the
/// route, the query, the credential, the body and every way it fails are asserted with no Gateway and no
/// port. The Gateway side of both routes is built in parallel to the same contract; this pins the
/// Director's half of it.
///
/// The decisive ones are the failures: a failed fetch must never read as "no rules", because the
/// maintainer would then write an empty rules file and tell the hook nothing applies.
/// </summary>
public sealed class GatewayClientSessionRulesTests
{
    private const string Repo = "github.com/thefrederiksen/devthrottle";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

        public string? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? RequestBody { get; private set; }
        public string? Authorization { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Method = request.Method;
            RequestUri = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return _answer(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static GatewayClient ClientAnswering(StubHandler handler) =>
        new(new GatewayConfig { Url = "http://gateway.example:7878", Token = "director-token" },
            directorId: "director-under-test", version: "1.0.0", handler);

    private const string OneRule = """
        {"machine":"devlinux","repo":"github.com/thefrederiksen/devthrottle","rules":[
          {"id":"3f2c","rank":3,"appliesTo":"repo","event":"before_command","tool":"shell",
           "match":["echo RULE-PROBE"],"result":"deny","reason":"Not here.","state":"dry_run"}]}
        """;

    [Fact]
    public async Task FetchEffectiveHookRulesAsync_GatewayAnswers_CallsTheW1RouteWithTheDirectorCredentialAndParsesTheRules()
    {
        // Arrange
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, OneRule));
        using var client = ClientAnswering(handler);

        // Act
        var fetch = await client.FetchEffectiveHookRulesAsync("devlinux", Repo, CancellationToken.None);

        // Assert
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("http://gateway.example:7878/gateway/rules/effective?kind=hook&machine=devlinux&repo=github.com%2Fthefrederiksen%2Fdevthrottle",
            handler.RequestUri);
        Assert.Equal("Bearer director-token", handler.Authorization);
        Assert.Null(fetch.Failure);
        var rule = Assert.Single(fetch.Rules!);
        Assert.Equal("3f2c", rule.Id);
        Assert.Equal(3, rule.Rank);
        Assert.Equal("repo", rule.AppliesTo);
        Assert.Equal("before_command", rule.Event);
        Assert.Equal("shell", rule.Tool);
        Assert.Equal(new[] { "echo RULE-PROBE" }, rule.Match);
        Assert.Equal("deny", rule.Result);
        Assert.Equal("Not here.", rule.Reason);
        Assert.Equal("dry_run", rule.State);
    }

    [Fact]
    public async Task FetchEffectiveHookRulesAsync_CheckoutWithNoRepo_SendsAnEmptyRepo()
    {
        // Arrange
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"machine":"devlinux","repo":"","rules":[]}"""));
        using var client = ClientAnswering(handler);

        // Act
        var fetch = await client.FetchEffectiveHookRulesAsync("devlinux", "", CancellationToken.None);

        // Assert
        Assert.EndsWith("/gateway/rules/effective?kind=hook&machine=devlinux&repo=", handler.RequestUri);
        Assert.NotNull(fetch.Rules);
        Assert.Empty(fetch.Rules!);
    }

    [Fact]
    public async Task FetchEffectiveHookRulesAsync_MachineNameInADifferentCase_IsTheSameMachine()
    {
        // Arrange: machine names compare case-insensitively (ruling P5).
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, OneRule.Replace("\"devlinux\"", "\"DEVLINUX\"")));
        using var client = ClientAnswering(handler);

        // Act
        var fetch = await client.FetchEffectiveHookRulesAsync("devlinux", Repo, CancellationToken.None);

        // Assert
        Assert.Null(fetch.Failure);
    }

    [Theory]
    [InlineData("""{"machine":"devlinux","repo":"github.com/someone/else","rules":[]}""")]
    [InlineData("""{"machine":"other-machine","repo":"github.com/thefrederiksen/devthrottle","rules":[]}""")]
    public async Task FetchEffectiveHookRulesAsync_AnswerIsForADifferentMachineOrRepo_Fails(string body)
    {
        // Arrange: rules meant for somewhere else must never land in this session's file.
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, body));
        using var client = ClientAnswering(handler);

        // Act
        var fetch = await client.FetchEffectiveHookRulesAsync("devlinux", Repo, CancellationToken.None);

        // Assert
        Assert.Null(fetch.Rules);
        Assert.NotNull(fetch.Failure);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"error":"not found"}""")]
    [InlineData(HttpStatusCode.Forbidden, """{"error":"session_key_out_of_scope"}""")]
    [InlineData(HttpStatusCode.InternalServerError, "boom")]
    public async Task FetchEffectiveHookRulesAsync_GatewayRefuses_FailsWithTheStatusNotAnEmptyList(HttpStatusCode status, string body)
    {
        // Arrange
        var handler = new StubHandler(_ => Json(status, body));
        using var client = ClientAnswering(handler);

        // Act
        var fetch = await client.FetchEffectiveHookRulesAsync("devlinux", Repo, CancellationToken.None);

        // Assert
        Assert.Null(fetch.Rules);
        Assert.Contains(((int)status).ToString(), fetch.Failure);
    }

    [Theory]
    [InlineData("""{"machine":"devlinux","repo":"github.com/thefrederiksen/devthrottle"}""")]
    [InlineData("""[]""")]
    [InlineData("""not json""")]
    public async Task FetchEffectiveHookRulesAsync_AnswerIsNotTheContractShape_Fails(string body)
    {
        // Arrange
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, body));
        using var client = ClientAnswering(handler);

        // Act
        var fetch = await client.FetchEffectiveHookRulesAsync("devlinux", Repo, CancellationToken.None);

        // Assert
        Assert.Null(fetch.Rules);
        Assert.NotNull(fetch.Failure);
    }

    [Fact]
    public async Task FetchEffectiveHookRulesAsync_PageFallbackInsteadOfJson_Fails()
    {
        // Arrange: a Gateway without the route can answer its web app's page.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html></html>", Encoding.UTF8, "text/html"),
        });
        using var client = ClientAnswering(handler);

        // Act
        var fetch = await client.FetchEffectiveHookRulesAsync("devlinux", Repo, CancellationToken.None);

        // Assert
        Assert.Null(fetch.Rules);
        Assert.Contains("text/html", fetch.Failure);
    }

    [Fact]
    public async Task FetchEffectiveHookRulesAsync_GatewayUnreachable_Fails()
    {
        // Arrange
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        using var client = ClientAnswering(handler);

        // Act
        var fetch = await client.FetchEffectiveHookRulesAsync("devlinux", Repo, CancellationToken.None);

        // Assert
        Assert.Null(fetch.Rules);
        Assert.Contains("connection refused", fetch.Failure);
    }

    [Fact]
    public async Task FetchEffectiveHookRulesAsync_NoGatewayConfigured_FailsWithoutCalling()
    {
        // Arrange
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, OneRule));
        using var client = new GatewayClient(new GatewayConfig(), "director-under-test", "1.0.0", handler);

        // Act
        var fetch = await client.FetchEffectiveHookRulesAsync("devlinux", Repo, CancellationToken.None);

        // Assert
        Assert.Equal(0, handler.Calls);
        Assert.Null(fetch.Rules);
    }

    [Fact]
    public async Task PostHookFiringsAsync_GatewayAnswers_PostsEachDecisionVerbatimToTheW5Route()
    {
        // Arrange
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{"accepted":1,"duplicates":1,"refused":[{"decisionId":"d3","reason":"rule_not_found"}]}"""));
        using var client = ClientAnswering(handler);
        using var first = JsonDocument.Parse("""{"version":1,"decisionId":"d1","futureField":{"kept":true}}""");
        using var second = JsonDocument.Parse("""{"version":1,"decisionId":"d2"}""");
        using var third = JsonDocument.Parse("""{"version":1,"decisionId":"d3"}""");

        // Act
        var post = await client.PostHookFiringsAsync(
            new[] { first.RootElement, second.RootElement, third.RootElement }, CancellationToken.None);

        // Assert
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("http://gateway.example:7878/gateway/rules/hook-firings", handler.RequestUri);
        Assert.Equal("Bearer director-token", handler.Authorization);
        using var sent = JsonDocument.Parse(handler.RequestBody!);
        var decisions = sent.RootElement.GetProperty("decisions").EnumerateArray().ToList();
        Assert.Equal(3, decisions.Count);
        Assert.True(decisions[0].GetProperty("futureField").GetProperty("kept").GetBoolean());
        Assert.Null(post.Failure);
        Assert.Equal(1, post.Answer!.Accepted);
        Assert.Equal(1, post.Answer.Duplicates);
        var refused = Assert.Single(post.Answer.Refused!);
        Assert.Equal("d3", refused.DecisionId);
        Assert.Equal("rule_not_found", refused.Reason);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task PostHookFiringsAsync_GatewayRefusesThePost_Fails(HttpStatusCode status)
    {
        // Arrange
        var handler = new StubHandler(_ => Json(status, """{"error":"nope"}"""));
        using var client = ClientAnswering(handler);
        using var decision = JsonDocument.Parse("""{"version":1,"decisionId":"d1"}""");

        // Act
        var post = await client.PostHookFiringsAsync(new[] { decision.RootElement }, CancellationToken.None);

        // Assert
        Assert.Null(post.Answer);
        Assert.Contains(((int)status).ToString(), post.Failure);
    }

    [Fact]
    public async Task PostHookFiringsAsync_GatewayUnreachable_Fails()
    {
        // Arrange
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        using var client = ClientAnswering(handler);
        using var decision = JsonDocument.Parse("""{"version":1,"decisionId":"d1"}""");

        // Act
        var post = await client.PostHookFiringsAsync(new[] { decision.RootElement }, CancellationToken.None);

        // Assert
        Assert.Null(post.Answer);
        Assert.Contains("connection refused", post.Failure);
    }

    [Fact]
    public async Task PostHookFiringsAsync_NoGatewayConfigured_FailsWithoutCalling()
    {
        // Arrange
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"accepted":1,"duplicates":0,"refused":[]}"""));
        using var client = new GatewayClient(new GatewayConfig(), "director-under-test", "1.0.0", handler);
        using var decision = JsonDocument.Parse("""{"version":1,"decisionId":"d1"}""");

        // Act
        var post = await client.PostHookFiringsAsync(new[] { decision.RootElement }, CancellationToken.None);

        // Assert
        Assert.Equal(0, handler.Calls);
        Assert.Null(post.Answer);
    }
}
