using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Director's own stop call (mission "Stop a session", Ruling 5). The Director window's Stop control
/// goes through this method and nothing else - the local kill it used to do recorded no reason, which is
/// the one thing Ruling 4 makes mandatory.
///
/// These drive the REAL <see cref="GatewayClient"/> with a stub message handler in front of it, so the
/// route it calls, the body it sends, the answer it parses and the way it fails are all asserted with no
/// Gateway, no port and nothing machine-global. The stub is what makes this a unit test rather than a
/// second copy of the Gateway's own endpoint tests.
///
/// THE DECISIVE ONES ARE THE FAILURES. A test that only checked the happy path would still pass if the
/// method quietly fell back to a local kill, which is exactly the defect Ruling 5 names in terms.
/// </summary>
public class GatewayClientStopSessionTests
{
    private const string SessionId = "9c41e7a2-1111-2222-3333-444455556666";

    /// <summary>A handler that answers whatever the test says, and remembers what it was asked.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

        public string? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? RequestBody { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
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

    private static string FoldedAnswer(
        string verdict = SessionStopVerdict.Stopped,
        string headline = "stopped 9c41e7a2 - process 51884 ended and the row was removed",
        params string[] details) =>
        JsonSerializer.Serialize(new
        {
            verdict,
            headline,
            details,
            sessionId = SessionId,
            shortId = "9c41e7a2",
            processId = 51884,
            processEnded = true,
            rowRemoved = true,
            reason = "spawned into the wrong mode",
            stoppedBy = "director-under-test",
        });

    /// <summary>
    /// The route and the body. There is ONE stop route and this is the door onto it - a client that posted
    /// somewhere else, or that sent no reason, would be refused by the Gateway and the operator would see a
    /// refusal instead of a stop.
    /// </summary>
    [Fact]
    public async Task ItPostsTheReasonToTheOneStopRoute()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, FoldedAnswer()));
        using var client = ClientAnswering(handler);

        await client.StopSessionAsync(SessionId, "spawned into the wrong mode");

        Assert.Equal(1, handler.Calls);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal($"http://gateway.example:7878/sessions/{SessionId}/stop", handler.RequestUri);

        using var sent = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("spawned into the wrong mode", sent.RootElement.GetProperty("reason").GetString());
    }

    /// <summary>
    /// The answer comes back whole - the verdict word, the headline, every detail line in order, and the
    /// facts underneath. The window renders those strings verbatim, so anything dropped here is a sentence
    /// the operator never sees.
    /// </summary>
    [Fact]
    public async Task ItParsesTheWholeFoldedAnswer()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, FoldedAnswer(
            details: new[]
            {
                "Its working tree at C:\\repo has uncommitted changes. They were left exactly as they were.",
                "Reason recorded: spawned into the wrong mode",
            })));
        using var client = ClientAnswering(handler);

        var answer = await client.StopSessionAsync(SessionId, "spawned into the wrong mode");

        Assert.Equal(SessionStopVerdict.Stopped, answer.Verdict);
        Assert.Equal("stopped 9c41e7a2 - process 51884 ended and the row was removed", answer.Headline);
        Assert.Equal(2, answer.Details.Count);
        Assert.StartsWith("Its working tree at C:\\repo", answer.Details[0]);
        Assert.Equal("Reason recorded: spawned into the wrong mode", answer.Details[1]);
        Assert.Equal(51884, answer.ProcessId);
        Assert.True(answer.ProcessEnded);
        Assert.True(answer.RowRemoved);
        Assert.Equal("spawned into the wrong mode", answer.Reason);
    }

    /// <summary>
    /// A verdict word this build has never heard of is carried through untouched. There are four today and
    /// a fifth is one edit on the Gateway; a client that only understood the words it was compiled against
    /// would turn that edit into a release of every surface.
    /// </summary>
    [Fact]
    public async Task ItCarriesAVerdictWordItDoesNotRecognise()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            FoldedAnswer(verdict: "somethingTheGatewayAddedLater", headline: "a sentence written on the Gateway")));
        using var client = ClientAnswering(handler);

        var answer = await client.StopSessionAsync(SessionId, "why");

        Assert.Equal("somethingTheGatewayAddedLater", answer.Verdict);
        Assert.Equal("a sentence written on the Gateway", answer.Headline);
    }

    /// <summary>
    /// A refusal is a failure and it carries the Gateway's own sentence. The Gateway wrote the words for
    /// this case; re-wording them here would throw away the one sentence written for it.
    /// </summary>
    [Fact]
    public async Task ARefusalThrowsCarryingTheGatewaysOwnSentence()
    {
        const string gatewaySentence =
            "A stop needs a reason. Say why this session is being stopped - it is recorded with the stop.";
        var handler = new StubHandler(_ => Json(HttpStatusCode.BadRequest,
            JsonSerializer.Serialize(new { error = gatewaySentence })));
        using var client = ClientAnswering(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StopSessionAsync(SessionId, "why"));

        Assert.Equal(gatewaySentence, ex.Message);
    }

    /// <summary>
    /// A failure with no sentence in it still throws, and says what it does know. The one thing it must
    /// never do is return quietly, because a caller that got no exception has been told the stop worked.
    /// </summary>
    [Fact]
    public async Task AFailureWithNoSentenceStillThrows()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        using var client = ClientAnswering(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StopSessionAsync(SessionId, "why"));

        Assert.Contains("502", ex.Message);
    }

    /// <summary>
    /// A BROKEN INSTRUMENT, NOT A FOURTH VERDICT - the same rule the command line applies to the same
    /// answer. Every answer this route gives carries a headline, so a 200 without one is a Gateway that did
    /// not understand the request; returning it would leave the window with nothing to render and no idea
    /// anything was wrong.
    /// </summary>
    [Fact]
    public async Task A200WithNoHeadlineIsNotAnAnswer()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { verdict = SessionStopVerdict.Stopped, headline = "" })));
        using var client = ClientAnswering(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StopSessionAsync(SessionId, "why"));

        Assert.Contains("nothing that says what happened", ex.Message);
    }

    /// <summary>
    /// NO GATEWAY, NO STOP, AND NO FALLBACK. Ruling 5 states this cost out loud and takes it: a local stop
    /// that quietly worked without the Gateway would be a stop with no recorded reason. The test that
    /// matters is that it THROWS - if this method ever grew a local kill, this is the test that would go
    /// red.
    /// </summary>
    [Fact]
    public async Task WithNoGatewayConfiguredItThrowsRatherThanStoppingLocally()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, FoldedAnswer()));
        using var client = new GatewayClient(new GatewayConfig(), "director-under-test", "1.0.0", handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StopSessionAsync(SessionId, "why"));

        Assert.Contains("not connected to a Gateway", ex.Message);
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>
    /// A blank reason is refused before the round trip, the way the command line refuses it. The Gateway
    /// refuses it too; asking it to tell us what we already know just costs a network call.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankReasonIsRefusedBeforeAnythingIsSent(string reason)
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, FoldedAnswer()));
        using var client = ClientAnswering(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => client.StopSessionAsync(SessionId, reason));

        Assert.Equal(0, handler.Calls);
    }
}
