using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.UnitTests.Utilities;

/// <summary>
/// The Director's held answers: a polled Gateway read names the tag it holds, and a "not changed" answer
/// is turned back into the bytes already held. A stub Gateway records what each request carried, so
/// "the tag was sent" and "nothing was sent" are both observed, not assumed.
/// </summary>
public sealed class HeldGatewayAnswersTests
{
    private const string Url = "http://gateway.test/gateway/skills";

    [Fact]
    public async Task SendAsync_TaggedAnswerThen304_ReturnsTheHeldBytesAsA200()
    {
        var gateway = new StubGateway();
        gateway.Next(HttpStatusCode.OK, "{\"skills\":[1]}", etag: "\"abc\"");
        gateway.Next(HttpStatusCode.NotModified);
        var held = new HeldGatewayAnswers();

        var first = await held.SendAsync(gateway.Client, Request(), CancellationToken.None);
        var second = await held.SendAsync(gateway.Client, Request(), CancellationToken.None);

        Assert.Null(gateway.IfNoneMatchSent[0]);
        Assert.Equal("\"abc\"", gateway.IfNoneMatchSent[1]);
        Assert.False(first.FromHeld);
        Assert.True(second.FromHeld);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Equal("{\"skills\":[1]}", Encoding.UTF8.GetString(second.Body));
        Assert.Equal("application/json", second.MediaType);
    }

    [Fact]
    public async Task SendAsync_ChangedAnswer_ReplacesWhatIsHeld()
    {
        var gateway = new StubGateway();
        gateway.Next(HttpStatusCode.OK, "{\"v\":1}", etag: "\"one\"");
        gateway.Next(HttpStatusCode.OK, "{\"v\":2}", etag: "\"two\"");
        gateway.Next(HttpStatusCode.NotModified);
        var held = new HeldGatewayAnswers();

        await held.SendAsync(gateway.Client, Request(), CancellationToken.None);
        await held.SendAsync(gateway.Client, Request(), CancellationToken.None);
        var third = await held.SendAsync(gateway.Client, Request(), CancellationToken.None);

        Assert.Equal("\"one\"", gateway.IfNoneMatchSent[1]);
        Assert.Equal("\"two\"", gateway.IfNoneMatchSent[2]);
        Assert.Equal("{\"v\":2}", Encoding.UTF8.GetString(third.Body));
    }

    [Fact]
    public async Task SendAsync_GatewayWithoutTags_IsNeverAskedAndNothingIsHeld()
    {
        // An older Gateway sends no ETag: the Director keeps downloading in full, exactly as before.
        var gateway = new StubGateway();
        gateway.Next(HttpStatusCode.OK, "{}");
        gateway.Next(HttpStatusCode.OK, "{}");
        var held = new HeldGatewayAnswers();

        await held.SendAsync(gateway.Client, Request(), CancellationToken.None);
        await held.SendAsync(gateway.Client, Request(), CancellationToken.None);

        Assert.Null(gateway.IfNoneMatchSent[1]);
        Assert.Equal(0, held.Count);
    }

    [Fact]
    public async Task SendAsync_FailedRead_ReturnsTheFailureAndKeepsWhatIsHeld()
    {
        var gateway = new StubGateway();
        gateway.Next(HttpStatusCode.OK, "{\"v\":1}", etag: "\"one\"");
        gateway.Next(HttpStatusCode.ServiceUnavailable);
        gateway.Next(HttpStatusCode.NotModified);
        var held = new HeldGatewayAnswers();

        await held.SendAsync(gateway.Client, Request(), CancellationToken.None);
        var failed = await held.SendAsync(gateway.Client, Request(), CancellationToken.None);
        var after = await held.SendAsync(gateway.Client, Request(), CancellationToken.None);

        Assert.False(failed.IsSuccess);
        Assert.Empty(failed.Body);
        Assert.Throws<HttpRequestException>(() => failed.EnsureSuccess(Url));
        Assert.Equal("{\"v\":1}", Encoding.UTF8.GetString(after.Body));
    }

    [Fact]
    public async Task SendAsync_WeakTag_IsNotHeld()
    {
        var gateway = new StubGateway();
        gateway.Next(HttpStatusCode.OK, "{}", etag: "W/\"weak\"");
        gateway.Next(HttpStatusCode.OK, "{}");
        var held = new HeldGatewayAnswers();

        await held.SendAsync(gateway.Client, Request(), CancellationToken.None);
        await held.SendAsync(gateway.Client, Request(), CancellationToken.None);

        Assert.Null(gateway.IfNoneMatchSent[1]);
        Assert.Equal(0, held.Count);
    }

    [Fact]
    public async Task SendAsync_TaglessAnswerAfterATaggedOne_DropsTheHeldCopy()
    {
        // A Gateway rolled back to a version that sends no tag: stop asking, download in full again.
        var gateway = new StubGateway();
        gateway.Next(HttpStatusCode.OK, "{\"v\":1}", etag: "\"one\"");
        gateway.Next(HttpStatusCode.OK, "{\"v\":2}");
        gateway.Next(HttpStatusCode.OK, "{\"v\":2}");
        var held = new HeldGatewayAnswers();

        await held.SendAsync(gateway.Client, Request(), CancellationToken.None);
        await held.SendAsync(gateway.Client, Request(), CancellationToken.None);
        await held.SendAsync(gateway.Client, Request(), CancellationToken.None);

        Assert.Equal("\"one\"", gateway.IfNoneMatchSent[1]);
        Assert.Null(gateway.IfNoneMatchSent[2]);
        Assert.Equal(0, held.Count);
    }

    [Fact]
    public async Task SendAsync_304WithNothingHeld_Throws()
    {
        var gateway = new StubGateway();
        gateway.Next(HttpStatusCode.NotModified);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new HeldGatewayAnswers().SendAsync(gateway.Client, Request(), CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_DifferentAddresses_AreHeldSeparately()
    {
        var gateway = new StubGateway();
        gateway.Next(HttpStatusCode.OK, "{\"a\":1}", etag: "\"a\"");
        gateway.Next(HttpStatusCode.OK, "{\"b\":1}", etag: "\"b\"");
        var held = new HeldGatewayAnswers();

        await held.SendAsync(gateway.Client, Request(Url), CancellationToken.None);
        await held.SendAsync(gateway.Client, Request("http://gateway.test/gateway/workflows"), CancellationToken.None);

        Assert.Null(gateway.IfNoneMatchSent[1]);
        Assert.Equal(2, held.Count);
    }

    private static HttpRequestMessage Request(string url = Url) => new(HttpMethod.Get, url);

    private sealed class StubGateway
    {
        private readonly Queue<HttpResponseMessage> _answers = new();
        public List<string?> IfNoneMatchSent { get; } = new();
        public HttpClient Client { get; }

        public StubGateway() => Client = new HttpClient(new Handler(this));

        public void Next(HttpStatusCode status, string? json = null, string? etag = null)
        {
            var response = new HttpResponseMessage(status);
            if (json is not null)
                response.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (etag is not null)
                response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
            _answers.Enqueue(response);
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly StubGateway _owner;
            public Handler(StubGateway owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                _owner.IfNoneMatchSent.Add(request.Headers.IfNoneMatch.Count == 0
                    ? null
                    : string.Join(",", request.Headers.IfNoneMatch.Select(t => t.ToString())));
                return Task.FromResult(_owner._answers.Dequeue());
            }
        }
    }
}
