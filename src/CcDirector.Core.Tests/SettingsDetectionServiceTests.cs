using System.Net;
using System.Net.Http;
using CcDirector.Core.Settings;
using Xunit;

namespace CcDirector.Core.Tests;

public class SettingsDetectionServiceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _body;
        public StubHandler(HttpStatusCode code, string body) { _code = code; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_code) { Content = new StringContent(_body) });
    }

    private static SettingsDetectionService WithResponse(HttpStatusCode code, string body)
        => new(new HttpClient(new StubHandler(code, body)));

    [Fact]
    public async Task TestGatewayAsync_ValidHealth_ReturnsOkWithCounts()
    {
        var svc = WithResponse(HttpStatusCode.OK,
            """{"status":"ok","version":"1.2.3","directors":2,"sessions":5}""");

        var r = await svc.TestGatewayAsync("http://gw-host:7878");

        Assert.True(r.Ok);
        Assert.Equal("1.2.3", r.Version);
        Assert.Equal(2, r.Directors);
        Assert.Equal(5, r.Sessions);
        Assert.Contains("2 director(s)", r.Message);
    }

    [Fact]
    public async Task TestGatewayAsync_CaseInsensitiveKeys_StillParses()
    {
        var svc = WithResponse(HttpStatusCode.OK,
            """{"Status":"ok","Version":"9.9","Directors":1,"Sessions":0}""");

        var r = await svc.TestGatewayAsync("http://gw:7878");

        Assert.True(r.Ok);
        Assert.Equal("9.9", r.Version);
    }

    [Fact]
    public async Task TestGatewayAsync_Non2xx_ReturnsFail()
    {
        var svc = WithResponse(HttpStatusCode.InternalServerError, "boom");

        var r = await svc.TestGatewayAsync("http://gw:7878");

        Assert.False(r.Ok);
        Assert.Contains("500", r.Message);
    }

    [Fact]
    public async Task TestGatewayAsync_NonGatewayJson_ReturnsFail()
    {
        var svc = WithResponse(HttpStatusCode.OK, """{"hello":"world"}""");

        var r = await svc.TestGatewayAsync("http://gw:7878");

        Assert.False(r.Ok);
        Assert.Contains("does not look like", r.Message);
    }

    [Fact]
    public async Task TestGatewayAsync_NotJson_ReturnsFail()
    {
        var svc = WithResponse(HttpStatusCode.OK, "<html>not json</html>");

        var r = await svc.TestGatewayAsync("http://gw:7878");

        Assert.False(r.Ok);
        Assert.Contains("not valid gateway JSON", r.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TestGatewayAsync_EmptyUrl_ReturnsFail(string url)
    {
        var svc = WithResponse(HttpStatusCode.OK, "{}");

        var r = await svc.TestGatewayAsync(url);

        Assert.False(r.Ok);
    }

    [Fact]
    public async Task DetectPublicUrlAsync_UnknownPort_ReturnsNull()
    {
        var svc = new SettingsDetectionService();

        var r = await svc.DetectPublicUrlAsync(0);

        Assert.Null(r.Url);
    }
}
