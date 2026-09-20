using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Director's own read of the ONE repository list (the one-repository-list mission, phase 6):
/// GET /directors/{id}/known-repositories, on this Director's own Gateway token.
///
/// These drive the REAL <see cref="GatewayClient"/> with a stub message handler in front of it, so the
/// route it calls, the order it preserves and the way it fails are asserted with no Gateway, no port and
/// nothing machine-global - the same shape as <see cref="GatewayClientStopSessionTests"/>.
///
/// THE DECISIVE ONES ARE THE FAILURES, and one of them in particular: an EMPTY list is a list. The
/// dialog above this client is allowed to show the machine's own scan when the Gateway cannot answer, so
/// a client that reported "empty" as a failure would hand a screen the licence to show a different list
/// from the Cockpit and the phone - which is the defect this whole mission exists to end.
/// </summary>
public class GatewayClientKnownRepositoriesTests
{
    private const string DirectorId = "director under test";

    /// <summary>A handler that answers whatever the test says, and remembers what it was asked.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

        public string? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Authorization { get; private set; }
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Method = request.Method;
            RequestUri = request.RequestUri?.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(_answer(request));
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _failure;

        public ThrowingHandler(Exception failure) => _failure = failure;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw _failure;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static GatewayClient ClientAnswering(HttpMessageHandler handler) =>
        new(new GatewayConfig { Url = "http://gateway.example:7878", Token = "test-token" },
            directorId: DirectorId, version: "1.0.0", handler);

    private static string ServedList() => JsonSerializer.Serialize(new object[]
    {
        new { name = "atlas", path = "/home/soren/code/atlas", lastUsed = "2026-09-20T09:00:00Z", neverOpened = false },
        new { name = "zephyr", path = "/home/soren/code/zephyr", lastUsed = "2026-09-19T09:00:00Z", neverOpened = false },
        new { name = "beacon", path = "/home/soren/code/beacon", lastUsed = (string?)null, neverOpened = true },
    });

    /// <summary>
    /// The route, the method and the credential. There is ONE repository list and this is the door onto
    /// it; a client that read somewhere else would be reading a different list, which is the complaint
    /// that started this mission. The Director id is escaped, because a Director's id is not guaranteed
    /// to be free of characters a path would swallow.
    /// </summary>
    [Fact]
    public async Task ItReadsTheOneRepositoryListRoute_ForThisDirector()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, ServedList()));
        var client = ClientAnswering(handler);

        await client.GetKnownRepositoriesAsync();

        Assert.Equal(1, handler.Calls);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal(
            "http://gateway.example:7878/directors/director%20under%20test/known-repositories",
            handler.RequestUri);
        Assert.Equal("Bearer test-token", handler.Authorization);
    }

    /// <summary>
    /// The rows arrive in the order the Gateway served them and nothing here re-orders them. The fixture
    /// is deliberately one no client sort would produce on its own - a never-opened row last, and names
    /// that are not alphabetical - so a client that quietly sorted would be caught rather than agreeing
    /// by luck.
    /// </summary>
    [Fact]
    public async Task TheRowsAreHandedOnInTheOrderTheGatewayServedThem()
    {
        var client = ClientAnswering(new StubHandler(_ => Json(HttpStatusCode.OK, ServedList())));

        var answer = await client.GetKnownRepositoriesAsync();

        Assert.Equal(KnownRepositoryListOutcome.Served, answer.Outcome);
        Assert.Equal(
            new[] { "atlas", "zephyr", "beacon" },
            answer.Repositories.Select(r => r.Name).ToArray());
        Assert.Null(answer.Repositories[2].LastUsed);
        Assert.True(answer.Repositories[2].NeverOpened);
        Assert.False(answer.Repositories[0].NeverOpened);
    }

    /// <summary>
    /// AN EMPTY LIST IS A LIST. This is the one the fallback must never be allowed to treat as a
    /// failure: the Gateway saying "this machine has no repositories" is an answer, and a screen that
    /// swapped in its own list instead would be inventing a second answer for one machine.
    /// </summary>
    [Fact]
    public async Task AnEmptyListIsServed_NotReportedAsAFailure()
    {
        var client = ClientAnswering(new StubHandler(_ => Json(HttpStatusCode.OK, "[]")));

        var answer = await client.GetKnownRepositoriesAsync();

        Assert.Equal(KnownRepositoryListOutcome.Served, answer.Outcome);
        Assert.Empty(answer.Repositories);
        Assert.Null(answer.Reason);
    }

    /// <summary>
    /// A Director with no Gateway has nobody to ask, and says so without dialling. It is not
    /// "unreachable": nothing was unreachable, there was nothing to reach.
    /// </summary>
    [Fact]
    public async Task WithNoGatewayConfigured_ItSaysSo_AndCallsNothing()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, ServedList()));
        var client = new GatewayClient(new GatewayConfig { Url = "" }, DirectorId, "1.0.0", handler);

        var answer = await client.GetKnownRepositoriesAsync();

        Assert.Equal(KnownRepositoryListOutcome.NotConfigured, answer.Outcome);
        Assert.Empty(answer.Repositories);
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>The request never completed - no connection. THIS is what the dialog's fallback is for.</summary>
    [Fact]
    public async Task WhenTheGatewayCannotBeReached_ItSaysUnreachable()
    {
        var client = ClientAnswering(new ThrowingHandler(
            new HttpRequestException("No such host is known. (gateway.example:7878)")));

        var answer = await client.GetKnownRepositoriesAsync();

        Assert.Equal(KnownRepositoryListOutcome.Unreachable, answer.Outcome);
        Assert.Contains("No such host", answer.Reason);
    }

    /// <summary>A timeout is the request not completing, which is the same thing as not being reached.</summary>
    [Fact]
    public async Task WhenTheRequestTimesOut_ItSaysUnreachable()
    {
        var client = ClientAnswering(new ThrowingHandler(new TaskCanceledException("The request timed out.")));

        var answer = await client.GetKnownRepositoriesAsync();

        Assert.Equal(KnownRepositoryListOutcome.Unreachable, answer.Outcome);
    }

    /// <summary>
    /// A Gateway that ANSWERS with an error is not an unreachable Gateway, and the difference is kept
    /// all the way to the screen: its own words are carried so the reason shown is the Gateway's rather
    /// than one this client made up.
    /// </summary>
    [Fact]
    public async Task WhenTheGatewayRefuses_ItCarriesTheGatewaysOwnWords()
    {
        var client = ClientAnswering(new StubHandler(_ => Json(
            HttpStatusCode.Conflict, """{"error":"The Director has not reported a machine name."}""")));

        var answer = await client.GetKnownRepositoriesAsync();

        Assert.Equal(KnownRepositoryListOutcome.Refused, answer.Outcome);
        Assert.Equal("The Director has not reported a machine name.", answer.Reason);
        Assert.Empty(answer.Repositories);
    }

    /// <summary>A refusal with no readable body still reports what little is true: the status line.</summary>
    [Fact]
    public async Task WhenTheGatewayRefusesWithNoBody_ItReportsTheStatusLine()
    {
        var client = ClientAnswering(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var answer = await client.GetKnownRepositoriesAsync();

        Assert.Equal(KnownRepositoryListOutcome.Refused, answer.Outcome);
        Assert.Contains("404", answer.Reason);
    }

    /// <summary>
    /// A 200 whose body is not a list. The Gateway is plainly reachable, so this is a refusal to report
    /// loudly - never a reason to fall back to a different list, which would hide it.
    /// </summary>
    [Fact]
    public async Task WhenTheAnswerIsNotAList_ItIsARefusal_NotUnreachable()
    {
        var client = ClientAnswering(new StubHandler(_ => Json(HttpStatusCode.OK, """{"error":"not a list"}""")));

        var answer = await client.GetKnownRepositoriesAsync();

        Assert.Equal(KnownRepositoryListOutcome.Refused, answer.Outcome);
        Assert.Empty(answer.Repositories);
    }
}
