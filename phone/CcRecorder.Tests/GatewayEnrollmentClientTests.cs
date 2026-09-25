using System.Net;
using System.Text;
using System.Text.Json;
using CcRecorder.Account;
using Xunit;

namespace CcRecorder.Tests;

public class GatewayEnrollmentClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public HttpRequestMessage? Seen;
        public string? SeenBody;

        public FakeHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            SeenBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task EnrollAsync_Ok_ReturnsDeviceKeyAndSendsTheAccountTokenAsBearer()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{\"deviceKey\":\"dev-key-1\"}");

        var key = await new GatewayEnrollmentClient(handler)
            .EnrollAsync("https://gateway.example/", "acct-token", "ccrecorder-abc", "CC Recorder (Pixel)", CancellationToken.None);

        Assert.Equal("dev-key-1", key);
        Assert.Equal(HttpMethod.Post, handler.Seen!.Method);
        Assert.Equal("https://gateway.example/mobile/enroll", handler.Seen.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Seen.Headers.Authorization!.Scheme);
        Assert.Equal("acct-token", handler.Seen.Headers.Authorization.Parameter);
        using var body = JsonDocument.Parse(handler.SeenBody!);
        Assert.Equal("ccrecorder-abc", body.RootElement.GetProperty("DeviceId").GetString());
        Assert.Equal("android", body.RootElement.GetProperty("Platform").GetString());
        Assert.Equal("", body.RootElement.GetProperty("DeviceKey").GetString());
    }

    [Fact]
    public async Task EnrollAsync_OkWithoutKey_FailsLoud()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");

        await Assert.ThrowsAsync<SignInFailedException>(() => new GatewayEnrollmentClient(handler)
            .EnrollAsync("https://gateway.example", "t", "d", "n", CancellationToken.None));
    }

    [Fact]
    public async Task EnrollAsync_PaymentRequired_SaysSubscriptionAndCarriesTheGatewaysMessage()
    {
        var handler = new FakeHandler(HttpStatusCode.PaymentRequired,
            "{\"error\":\"not entitled\",\"message\":\"Subscribe at devthrottle.com/pricing\"}");

        var ex = await Assert.ThrowsAsync<SignInFailedException>(() => new GatewayEnrollmentClient(handler)
            .EnrollAsync("https://gateway.example", "t", "d", "n", CancellationToken.None));

        Assert.Contains("subscription", ex.Message);
        Assert.Contains("Subscribe at devthrottle.com/pricing", ex.Message);
    }

    [Fact]
    public async Task EnrollAsync_Unauthorized_AsksToSignInAgain()
    {
        var handler = new FakeHandler(HttpStatusCode.Unauthorized, "{\"error\":\"token expired\"}");

        var ex = await Assert.ThrowsAsync<SignInFailedException>(() => new GatewayEnrollmentClient(handler)
            .EnrollAsync("https://gateway.example", "t", "d", "n", CancellationToken.None));

        Assert.Contains("token expired", ex.Message);
        Assert.Contains("Sign in again", ex.Message);
    }

    [Fact]
    public async Task EnrollAsync_NonJsonError_StillReportsTheStatus()
    {
        var handler = new FakeHandler(HttpStatusCode.BadGateway, "<html>proxy error</html>");

        var ex = await Assert.ThrowsAsync<SignInFailedException>(() => new GatewayEnrollmentClient(handler)
            .EnrollAsync("https://gateway.example", "t", "d", "n", CancellationToken.None));

        Assert.Contains("502", ex.Message);
    }

    [Theory]
    [InlineData("", "t", "d")]
    [InlineData("https://g", "", "d")]
    [InlineData("https://g", "t", "")]
    public async Task EnrollAsync_MissingInput_Throws(string gateway, string token, string deviceId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new GatewayEnrollmentClient(new FakeHandler(HttpStatusCode.OK, "{}"))
            .EnrollAsync(gateway, token, deviceId, "n", CancellationToken.None));
    }

    [Fact]
    public void BuildSignInUrl_CarriesTheEncodedLoopbackCallback()
    {
        var url = GatewayEnrollmentClient.BuildSignInUrl(new Uri("http://127.0.0.1:43123/devthrottle-login-callback/"), "abc123");

        Assert.Equal("https://devthrottle.com/signin?redirect_uri=http%3A%2F%2F127.0.0.1%3A43123%2Fdevthrottle-login-callback%2F&state=abc123",
            url.AbsoluteUri);
    }
}
