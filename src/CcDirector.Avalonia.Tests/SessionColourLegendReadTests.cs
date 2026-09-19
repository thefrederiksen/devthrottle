using System.Net;
using System.Text;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// HOW THE DESKTOP LEARNS WHAT THE COLOURS MEAN - the Director's read of
/// <c>GET /gateway/session-colours</c>, and the cache the rail's hover and the legend window both read.
///
/// The read is driven through the REAL <see cref="GatewayClient"/> with a stub message handler in front
/// of it, so the route it calls, the credential it sends and the answer it parses are asserted with no
/// Gateway, no port and nothing machine-global.
///
/// THE DECISIVE ONES ARE THE FAILURES. A test that only checked the happy path would still pass if the
/// cache answered an unreachable Gateway with a built-in copy of the words - which is the one thing it
/// must never do, because a copy compiled into an old Director explains the colours THAT build knows
/// rather than the ones its Gateway is sending.
///
/// Plain [Fact]: nothing here touches an Avalonia object.
/// </summary>
public sealed class SessionColourLegendReadTests
{
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
            RequestUri = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(_answer(request));
        }
    }

    /// <summary>The Gateway's real answer, serialised the way the real route serialises it.</summary>
    private static string TheRealLegendAsJson() =>
        System.Text.Json.JsonSerializer.Serialize(SessionColourLegend.Build(),
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

    private static GatewayClient ClientAnswering(StubHandler handler) =>
        new(new GatewayConfig { Url = "http://gateway.example:7878", Token = "test-token" },
            directorId: "director-under-test", version: "1.0.0", handler);

    [Fact]
    public async Task TheDirectorAsksTheGatewaysOwnRoute_WithItsCredential_AndParsesTheAnswer()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(TheRealLegendAsJson(), Encoding.UTF8, "application/json"),
        });
        using var client = ClientAnswering(handler);

        var legend = await client.GetSessionColourLegendAsync();

        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("http://gateway.example:7878/gateway/session-colours", handler.RequestUri);
        Assert.Equal("Bearer test-token", handler.Authorization);

        Assert.NotNull(legend);
        // The whole vocabulary, in the Gateway's own words - not a subset this build happened to know.
        Assert.Equal(SessionColourLegend.Build().Entries.Count, legend!.Entries.Count);
        Assert.Contains(legend.Entries, e => e.Colour == "cyan");
        Assert.All(legend.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Title)));
        Assert.All(legend.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Means)));
    }

    [Fact]
    public async Task ARefusedRead_Throws_SoNothingCanMistakeItForAnAnswer()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":\"nope\"}", Encoding.UTF8, "application/json"),
        });
        using var client = ClientAnswering(handler);

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetSessionColourLegendAsync());
    }

    [Fact]
    public async Task WithNoGatewayConfigured_TheReadIsNull_AndNothingIsDialled()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new GatewayClient(new GatewayConfig(), "director", "1.0.0", handler);

        Assert.Null(await client.GetSessionColourLegendAsync());
        Assert.Equal(0, handler.Calls);
    }

    // ===== The cache both readers share =====

    private sealed class FakeLegendSeam : IGatewayColourLegend
    {
        public SessionColourLegendDto? Answer { get; set; } = SessionColourLegend.Build();
        public Exception? Throws { get; set; }
        public int Calls { get; private set; }

        public Task<SessionColourLegendDto?> GetSessionColourLegendAsync(CancellationToken ct = default)
        {
            Calls++;
            if (Throws is not null) return Task.FromException<SessionColourLegendDto?>(Throws);
            return Task.FromResult(Answer);
        }
    }

    [Fact]
    public async Task TheCache_HoldsWhatItRead_AndSaysSoToBothReaders()
    {
        var seam = new FakeLegendSeam();
        var cache = new SessionColourLegendCache(() => seam);

        var landed = 0;
        cache.Changed += () => landed++;

        await cache.RefreshAsync();

        Assert.NotNull(cache.Current);
        Assert.Null(cache.Error);
        // The rail subscribes to this: the legend lands seconds AFTER the rows are on screen, so without
        // the event every hover would show the Gateway's label alone until an unrelated repaint.
        Assert.Equal(1, landed);
    }

    /// <summary>
    /// Before the first read the cache knows NOTHING, and that is a different thing from an empty legend:
    /// its readers say less (the hover falls back to the Gateway's stamped label, the window says why)
    /// rather than showing words nobody sent.
    /// </summary>
    [Fact]
    public void TheCache_BeforeItHasRead_KnowsNothing_RatherThanShowingAnEmptyLegend()
    {
        var cache = new SessionColourLegendCache(() => new FakeLegendSeam());

        Assert.Null(cache.Current);
        Assert.Null(cache.Error);
    }

    /// <summary>
    /// THE ONE THAT MATTERS. An unreachable Gateway must leave the cache with what it last read - or with
    /// nothing - and never with invented words. It also has to say WHY, so the legend window shows a
    /// reason instead of an empty list, which would tell the person the colours have no meanings.
    /// </summary>
    [Fact]
    public async Task TheCache_OnAFailedRead_KeepsWhatItHad_AndNeverInventsALegend()
    {
        var seam = new FakeLegendSeam();
        var cache = new SessionColourLegendCache(() => seam);

        // Never read anything: a failure leaves it knowing nothing, not knowing something made up.
        seam.Throws = new HttpRequestException("the gateway is not answering");
        await cache.RefreshAsync();

        Assert.Null(cache.Current);
        Assert.Contains("not answering", cache.Error ?? "", StringComparison.Ordinal);

        // Read once, then fail: it keeps the real answer rather than blanking.
        seam.Throws = null;
        await cache.RefreshAsync();
        var read = cache.Current;
        Assert.NotNull(read);

        seam.Throws = new HttpRequestException("the gateway went away");
        await cache.RefreshAsync();

        Assert.Same(read, cache.Current);
        Assert.Contains("went away", cache.Error ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCache_WithNoGatewayAtAll_StaysEmptyAndDoesNotThrow()
    {
        var cache = new SessionColourLegendCache(() => null);

        await cache.RefreshAsync();

        Assert.Null(cache.Current);
        Assert.Null(cache.Error);
    }
}
