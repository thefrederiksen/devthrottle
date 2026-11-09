using System.Net;
using System.Text;
using CcDirector.ControlApi;
using CcDirector.ControlApi.Triggers;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Director's trigger poll, <c>GET /directors/{id}/triggers</c>, against a stub Gateway (no port, no Gateway).
/// Team implementation 2.1, review note N2: inside a team, a Director whose person may not run the team's factories is
/// refused by the team gate every 30 seconds. That is a standing answer - run nothing here - so it is Off, which also
/// clears whatever the Director held; any other 403 is still a failure.
/// </summary>
public sealed class GatewayClientFetchTriggersTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(answer(request));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Task<TriggerFetch> Fetch(HttpResponseMessage response) =>
        new GatewayClient(new GatewayConfig { Url = "http://gateway.example:7878", Token = "test-token" },
                "director-1", "1.0.0", new StubHandler(_ => response))
            .FetchTriggersAsync("director-1", CancellationToken.None);

    [Fact]
    public async Task FetchTriggersAsync_TheTeamGatesRefusal_IsOff()
    {
        var fetch = await Fetch(Json(HttpStatusCode.Forbidden,
            """{"error":"In this team you are a Developer, and a Developer may not open, run and talk to the team's factories.","code":"team_action_refused"}"""));

        Assert.Equal(TriggerFetchKind.Off, fetch.Kind);
        Assert.Empty(fetch.Triggers);
    }

    [Fact]
    public async Task FetchTriggersAsync_AnyOther403_IsStillAFailure()
    {
        var fetch = await Fetch(Json(HttpStatusCode.Forbidden, """{"error":"no account for this caller"}"""));

        Assert.Equal(TriggerFetchKind.Failed, fetch.Kind);
    }

    [Fact]
    public void FetchTriggersAsync_TheCodeIsTheGatewaysOwnRefusalCode()
    {
        Assert.Equal(CcDirector.Gateway.Teams.TeamEndpointGate.RefusalCode, GatewayClient.TeamActionRefusedCode);
    }
}
