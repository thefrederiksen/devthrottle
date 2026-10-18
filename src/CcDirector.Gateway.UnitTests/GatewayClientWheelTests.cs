using System.Net;
using System.Text;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The desktop's wheel calls (mission #4070, phase 4): the Terminal view's button goes through the Gateway's verbs,
/// never the session's own switch. These drive the REAL <see cref="GatewayClient"/> with a stub handler in front of
/// it, so the route, the method and the way a refusal's sentence survives the hop are asserted with no Gateway and no
/// port.
/// </summary>
public class GatewayClientWheelTests
{
    private const string SessionId = "9c41e7a2-1111-2222-3333-444455556666";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

        public string? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Method = request.Method;
            RequestUri = request.RequestUri?.ToString();
            return Task.FromResult(_answer(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static GatewayClient ClientAnswering(StubHandler handler, string url = "http://gateway.example:7878") =>
        new(new GatewayConfig { Url = url, Token = "test-token" }, directorId: "director-under-test", version: "1.0.0", handler);

    [Fact]
    public async Task TakeWheelAsync_Accepted_PostsToTheTakeRoute()
    {
        // Arrange
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"accepted\":true}"));
        using var client = ClientAnswering(handler);

        // Act
        await client.TakeWheelAsync(SessionId);

        // Assert
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.EndsWith($"/sessions/{SessionId}/wheel/take", handler.RequestUri);
    }

    [Fact]
    public async Task HandBackWheelAsync_Accepted_PostsToTheHandBackRoute()
    {
        // Arrange
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"accepted\":true}"));
        using var client = ClientAnswering(handler);

        // Act
        await client.HandBackWheelAsync(SessionId);

        // Assert
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.EndsWith($"/sessions/{SessionId}/wheel/hand-back", handler.RequestUri);
    }

    [Fact]
    public async Task TakeWheelAsync_Refused_ThrowsWithTheGatewaysSentenceVerbatim()
    {
        // Arrange
        const string reason = "You already have the wheel of this session: its terminal is open on the conversation. "
            + "Hand it back first if you want the session to continue in API mode.";
        var handler = new StubHandler(_ => Json(HttpStatusCode.Conflict,
            "{\"error\":\"" + reason + "\",\"code\":\"wheel_already_held\"}"));
        using var client = ClientAnswering(handler);

        // Act
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.TakeWheelAsync(SessionId));

        // Assert
        Assert.Equal(reason, ex.Message);
    }

    [Fact]
    public async Task HandBackWheelAsync_NoGatewayConfigured_ThrowsAndCallsNothing()
    {
        // Arrange
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{}"));
        using var client = ClientAnswering(handler, url: "");

        // Act
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.HandBackWheelAsync(SessionId));

        // Assert
        Assert.Contains("not connected to a Gateway", ex.Message);
        Assert.Equal(0, handler.Calls);
    }
}
