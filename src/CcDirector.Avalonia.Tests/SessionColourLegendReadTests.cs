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

    /// <summary>
    /// THE WORDS ON THE SCREEN ARE THE ONES THAT CAME DOWN THE WIRE, and this is the one test that can say
    /// so. The test above cannot: its expected value is <see cref="SessionColourLegend.Build"/>, the
    /// vocabulary compiled into THIS build, so a Director that threw the response away and rendered its own
    /// words would satisfy it exactly. An independent inspector did precisely that - replaced the
    /// deserialised legend in <c>GatewayClient.GetSessionColourLegendAsync</c> with
    /// <c>SessionColourLegend.Build()</c> - and all 100 targeted tests passed.
    ///
    /// So the Gateway here answers with a legend whose every word exists nowhere in this build
    /// (<see cref="GatewayWordsNoBuildKnows"/>, proved word by word in the test below), a different NUMBER
    /// of entries, and a colour name this build has never heard of. Every field is asserted character for
    /// character. There is nothing here this Director could have supplied for itself.
    /// </summary>
    [Fact]
    public async Task TheWordsThatReachTheDesktopAreTheOnesTheGatewaySent_NotThisBuildsOwnVocabulary()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(GatewayWordsNoBuildKnows.AsTheRouteSerialisesIt(), Encoding.UTF8, "application/json"),
        });
        using var client = ClientAnswering(handler);

        var read = await client.GetSessionColourLegendAsync();

        Assert.NotNull(read);
        var sent = GatewayWordsNoBuildKnows.Legend();
        // The words first, so a Director explaining itself fails by NAMING the vocabulary it substituted
        // rather than by an entry count a future legend could coincidentally match.
        Assert.Equal(sent.Entries.Select(e => e.Title), read!.Entries.Select(e => e.Title));
        Assert.Equal(sent.Entries.Select(e => e.Means), read.Entries.Select(e => e.Means));
        Assert.Equal(sent.Entries.Count, read.Entries.Count);
        for (var i = 0; i < sent.Entries.Count; i++)
        {
            Assert.Equal(sent.Entries[i].Colour, read.Entries[i].Colour);
            Assert.Equal(sent.Entries[i].Hex, read.Entries[i].Hex);
            Assert.Equal(sent.Entries[i].Title, read.Entries[i].Title);
            Assert.Equal(sent.Entries[i].Means, read.Entries[i].Means);
            Assert.Equal(sent.Entries[i].AsksForYou, read.Entries[i].AsksForYou);
        }
        Assert.Equal(sent.VerdictNote, read.VerdictNote);
    }

    /// <summary>
    /// THE INSTRUMENT CHECK for the test above, and it is not ceremony. That test is only decisive while its
    /// expected words are ones this build could not have produced, and nothing else stops somebody adding
    /// "Pressing ahead" to the shipped legend one day and quietly turning it back into a test that passes on
    /// a Director explaining itself.
    /// </summary>
    [Fact]
    public void TheWireOnlyWords_AppearInNoCompiledConstant()
    {
        var everythingThisBuildKnows = System.Text.Json.JsonSerializer.Serialize(SessionColourLegend.Build());

        foreach (var word in GatewayWordsNoBuildKnows.EveryWord)
            Assert.DoesNotContain(word, everythingThisBuildKnows, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same words, carried the rest of the way: through the cache both the hover and the legend window
    /// read. A substitution anywhere on that path - the client, the cache - shows up here.
    /// </summary>
    [Fact]
    public async Task TheCache_HandsOnTheGatewaysWords_Unaltered()
    {
        var seam = new FakeLegendSeam { Answer = GatewayWordsNoBuildKnows.Legend() };
        var cache = new SessionColourLegendCache(() => seam);

        await cache.RefreshAsync();

        var held = cache.Current;
        Assert.NotNull(held);
        Assert.Equal(GatewayWordsNoBuildKnows.LaterTitle, held!.Entries[1].Title);
        Assert.Equal(GatewayWordsNoBuildKnows.LaterMeans, held.Entries[1].Means);
        Assert.Equal(GatewayWordsNoBuildKnows.Note, held.VerdictNote);
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

    /// <summary>
    /// A COLD WINDOW ASKS THE GATEWAY ONCE. Opening "What the colours mean" on a Director that has read
    /// nothing yet reads <see cref="SessionColourLegendCache.Current"/> - which starts a background read
    /// because the answer is absent - and then has to wait for one. Awaiting <c>RefreshAsync</c> directly
    /// walked past the in-flight guard and opened a SECOND request to the Gateway for the same words;
    /// <c>ReadNowAsync</c> joins the read already running.
    /// </summary>
    [Fact]
    public async Task AColdWindowReadingThenWaiting_DialsTheGatewayOnce_NotTwice()
    {
        var firstReadReached = new TaskCompletionSource();
        var letItFinish = new TaskCompletionSource();
        var seam = new SlowLegendSeam(firstReadReached, letItFinish);
        var cache = new SessionColourLegendCache(() => seam);

        // Exactly what the window does: read what is held (which starts a read), then wait for an answer.
        Assert.Null(cache.Current);
        await firstReadReached.Task;
        var waiting = cache.ReadNowAsync();

        letItFinish.SetResult();
        await waiting;

        Assert.Equal(1, seam.Calls);
        Assert.NotNull(cache.Current);
    }

    private sealed class SlowLegendSeam : IGatewayColourLegend
    {
        private readonly TaskCompletionSource _reached;
        private readonly TaskCompletionSource _release;

        public SlowLegendSeam(TaskCompletionSource reached, TaskCompletionSource release)
        {
            _reached = reached;
            _release = release;
        }

        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public async Task<SessionColourLegendDto?> GetSessionColourLegendAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            _reached.TrySetResult();
            await _release.Task;
            return SessionColourLegend.Build();
        }
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
