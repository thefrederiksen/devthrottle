using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// <see cref="HostedInferenceBrain"/> is the stateless hosted wingman: one OpenAI-compatible
/// chat-completions call per ask. These prove the request shape (URL, model, Bearer, single user
/// message), the reply extraction, and the no-fallback error paths - all over a stub handler, no network.
/// </summary>
public sealed class HostedInferenceBrainTests
{
    /// <summary>A canned-response handler that records the last request it saw.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public StubHandler(HttpStatusCode status, string body) { _status = status; _body = body; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }

    private static string OkBody(string content) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } });

    // Included AI (issue #1360): this class is where a chat model id meets the DevThrottle deployment
    // credential. The earlier constructor guard compared the base URL for string equality and was
    // bypassed by construction in the phase-2 inspection (https://devthrottle.com:443/api/v1 is the
    // same endpoint but not the same string). The guard is now the IncludedModelId TYPE on the
    // constructor - a catalog id string no longer compiles here - pinned by
    // IncludedModelTypeSurfaceTests, with the mint's own behaviour proven in IncludedModelIdTests.

    [Fact]
    public async Task AskAsync_PostsChatCompletions_WithModelBearerAndUserMessage()
    {
        var stub = new StubHandler(HttpStatusCode.OK, OkBody("the spoken summary"));
        using var http = new HttpClient(stub);
        var brain = new HostedInferenceBrain("https://devthrottle.com/api/v1", "dt_live_abc", IncludedModelId.Wingman, http, _ => { });

        var result = await brain.AskAsync("translate this");

        Assert.Equal("the spoken summary", result.Text);
        Assert.NotNull(stub.LastRequest);
        Assert.Equal(HttpMethod.Post, stub.LastRequest!.Method);
        Assert.Equal("https://devthrottle.com/api/v1/chat/completions", stub.LastRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", stub.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("dt_live_abc", stub.LastRequest.Headers.Authorization.Parameter);

        using var doc = JsonDocument.Parse(stub.LastRequestBody!);
        Assert.Equal("devthrottle/wingman", doc.RootElement.GetProperty("model").GetString());
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("translate this", messages[0].GetProperty("content").GetString());
    }

    /// <summary>
    /// THE REASONING IS TURNED OFF IN THE REQUEST BODY, and this asserts the BYTES rather than the flag that
    /// asked for them. A test on the property alone would pass over a brain that carried the intent and sent a
    /// body without it - which is the entire behaviour, since the argument only does anything upstream.
    ///
    /// Why it matters: with reasoning ON this model wrote 4,290 output tokens a call and answered in about 144
    /// seconds at the median, which is why the judge was on the fast tier until 2026-09-20. See
    /// <see cref="Gateway.Wingman.TurnVerdictJudge"/> for the measurement.
    /// </summary>
    [Fact]
    public async Task AskAsync_ThinkingOff_AsksTheModelNotToReason()
    {
        var stub = new StubHandler(HttpStatusCode.OK, OkBody("the verdict"));
        using var http = new HttpClient(stub);
        var brain = new HostedInferenceBrain("https://devthrottle.com/api/v1", "dt_live_abc",
            IncludedModelId.Wingman, http, _ => { }, thinkingOff: true);

        await brain.AskAsync("judge this");

        using var doc = JsonDocument.Parse(stub.LastRequestBody!);
        Assert.False(doc.RootElement.GetProperty("chat_template_kwargs").GetProperty("thinking").GetBoolean());
        // The rest of the body is untouched: the argument rides ALONGSIDE the call, it does not reshape it.
        Assert.Equal("devthrottle/wingman", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("judge this", doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());
    }

    /// <summary>
    /// AND WITHOUT IT THE KEY IS ABSENT, not present and false-shaped. A reasoning model reads this argument from
    /// its chat template; an empty or defaulted object is not the same input as no object, and every brain in the
    /// product other than the judge's was measured with no object at all.
    /// </summary>
    [Fact]
    public async Task AskAsync_ByDefault_SendsNoThinkingArgumentAtAll()
    {
        var stub = new StubHandler(HttpStatusCode.OK, OkBody("the spoken summary"));
        using var http = new HttpClient(stub);
        var brain = new HostedInferenceBrain("https://devthrottle.com/api/v1", "dt_live_abc",
            IncludedModelId.Wingman, http, _ => { });

        await brain.AskAsync("translate this");

        using var doc = JsonDocument.Parse(stub.LastRequestBody!);
        Assert.False(doc.RootElement.TryGetProperty("chat_template_kwargs", out _));
    }

    [Fact]
    public async Task AskAsync_NoKey_ThrowsWithoutCallingModel()
    {
        var stub = new StubHandler(HttpStatusCode.OK, OkBody("x"));
        using var http = new HttpClient(stub);
        var brain = new HostedInferenceBrain("https://devthrottle.com/api/v1", "", IncludedModelId.Wingman, http, _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.AskAsync("hi"));
        Assert.Null(stub.LastRequest);   // no-fallback: never called the model without a credential
    }

    [Fact]
    public async Task AskAsync_PaymentRequired_ThrowsSharedNeedsCreditsMessage()
    {
        // Issue #939: the 402 message is now the ONE shared copy (branched by code), not a hand-written
        // string - so it matches every other surface by construction.
        var stub = new StubHandler(HttpStatusCode.PaymentRequired, "{\"error\":{\"code\":\"insufficient_credits\"}}");
        using var http = new HttpClient(stub);
        var brain = new HostedInferenceBrain("https://devthrottle.com/api/v1", "dt_live_abc", IncludedModelId.Wingman, http, _ => { });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => brain.AskAsync("hi"));
        Assert.Contains(Core.HostedAi.HostedAiMessages.For(Core.HostedAi.HostedAiState.NeedsCredits).Text, ex.Message);
    }

    [Fact]
    public async Task AskAsync_MonthlyLimit402_ThrowsSharedCapReachedMessage()
    {
        // Branch on the code, not the status: a monthly-cap 402 yields the CapReached copy, distinct
        // from the out-of-credits copy.
        var stub = new StubHandler(HttpStatusCode.PaymentRequired, "{\"error\":{\"code\":\"monthly_limit_reached\"}}");
        using var http = new HttpClient(stub);
        var brain = new HostedInferenceBrain("https://devthrottle.com/api/v1", "dt_live_abc", IncludedModelId.Wingman, http, _ => { });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => brain.AskAsync("hi"));
        Assert.Contains(Core.HostedAi.HostedAiMessages.For(Core.HostedAi.HostedAiState.CapReached).Text, ex.Message);
    }

    /// <summary>A stub that returns a status plus a Retry-After header, to drive the 429 path (issue #1324).</summary>
    private sealed class RetryAfterStub : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly TimeSpan? _retryAfter;
        public RetryAfterStub(HttpStatusCode status, string body, TimeSpan? retryAfter)
        { _status = status; _body = body; _retryAfter = retryAfter; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
            if (_retryAfter is { } ra)
                resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(ra);
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public async Task AskAsync_429_ThrowsRateLimited_WithRetryAfterFromHeader()
    {
        // Issue #1324: a 429 surfaces as the typed rate-limit exception carrying the provider's
        // Retry-After hint, so the caller can back off for exactly that long instead of guessing.
        var stub = new RetryAfterStub(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limited\"}", TimeSpan.FromSeconds(12));
        using var http = new HttpClient(stub);
        var brain = new HostedInferenceBrain("https://devthrottle.com/api/v1", "dt_live_abc", IncludedModelId.Wingman, http, _ => { });

        var ex = await Assert.ThrowsAsync<WingmanModelRateLimitedException>(() => brain.AskAsync("hi"));
        Assert.Equal(TimeSpan.FromSeconds(12), ex.RetryAfter);
        Assert.Contains("429", ex.Message);
    }

    [Fact]
    public async Task AskAsync_429_NoRetryAfter_ThrowsRateLimited_WithNullHint()
    {
        // No Retry-After header: still the typed exception, but with no hint - the caller then uses
        // its own exponential backoff.
        var stub = new RetryAfterStub(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limited\"}", retryAfter: null);
        using var http = new HttpClient(stub);
        var brain = new HostedInferenceBrain("https://devthrottle.com/api/v1", "dt_live_abc", IncludedModelId.Wingman, http, _ => { });

        var ex = await Assert.ThrowsAsync<WingmanModelRateLimitedException>(() => brain.AskAsync("hi"));
        Assert.Null(ex.RetryAfter);
    }

    [Fact]
    public async Task RateLimited429_IsStillAnInvalidOperationException_SoExistingCatchesHold()
    {
        // It extends InvalidOperationException, so every existing catch of the general wingman-call
        // failure keeps catching a 429 unchanged (issue #1324).
        var stub = new RetryAfterStub(HttpStatusCode.TooManyRequests, "{}", retryAfter: null);
        using var http = new HttpClient(stub);
        var brain = new HostedInferenceBrain("https://devthrottle.com/api/v1", "dt_live_abc", IncludedModelId.Wingman, http, _ => { });

        await Assert.ThrowsAsync<WingmanModelRateLimitedException>(() => brain.AskAsync("hi"));
        var caught = await Record.ExceptionAsync(() => brain.AskAsync("hi"));
        Assert.IsAssignableFrom<InvalidOperationException>(caught);
    }

    /// <summary>A handler that never answers until its token is cancelled - the shape a stalled hosted
    /// worker takes. Drives the per-call deadline without a real 60-second (or 180-second) wait.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(Timeout.Infinite, ct);   // answers only when the (linked) token cancels
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task AskAsync_WhenModelStalls_FailsFastAsTimeout_NotAfterThreeMinutes()
    {
        // THE FIX: the model leg used to carry a flat 180-second client timeout and nothing else, so a
        // stalled hosted worker BLOCKED the whole voice generation for three minutes and then threw a raw
        // cancellation - a silent FAILED on the auto path, a false "computer offline" 502 on the manual
        // "generate" button. The call is now bounded per-attempt and fails fast as a clear TimeoutException
        // the voice path maps to Retrying. A tiny injected deadline proves the bound without a real wait.
        var handler = new HangingHandler();
        using var http = new HttpClient(handler);
        var brain = new HostedInferenceBrain("https://devthrottle.com/api/v1", "dt_live_abc", IncludedModelId.Wingman, http, _ => { },
            callTimeout: TimeSpan.FromMilliseconds(100));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => brain.AskAsync("translate this"));
        sw.Stop();

        Assert.Equal(1, handler.Calls);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"a bounded call must fail fast, but took {sw.Elapsed}");
    }

    [Fact]
    public async Task AskAsync_WhenCallerCancels_SurfacesCancellation_NotMislabelledTimeout()
    {
        // The deadline and a real caller-cancel must be told apart: a caller cancelling (shutdown) is NOT
        // the model failing to answer, so it surfaces as cancellation, never as a TimeoutException that the
        // voice path would wrongly turn into a Retrying banner.
        var handler = new HangingHandler();
        using var http = new HttpClient(handler);
        var brain = new HostedInferenceBrain("https://devthrottle.com/api/v1", "dt_live_abc", IncludedModelId.Wingman, http, _ => { },
            callTimeout: TimeSpan.FromMinutes(5));   // deadline far away, so only the caller-cancel can fire

        using var cts = new CancellationTokenSource();
        var task = brain.AskAsync("translate this", cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task AskAsync_EmptyContent_Throws()
    {
        var stub = new StubHandler(HttpStatusCode.OK, OkBody(""));
        using var http = new HttpClient(stub);
        var brain = new HostedInferenceBrain("https://devthrottle.com/api/v1", "dt_live_abc", IncludedModelId.Wingman, http, _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.AskAsync("hi"));
    }

    [Theory]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"hello\"}}]}", "hello")]
    [InlineData("{\"choices\":[]}", "")]
    [InlineData("not json", "")]
    [InlineData("{\"unexpected\":true}", "")]
    public void ExtractContent_ParsesOrDegradesToEmpty(string body, string expected)
        => Assert.Equal(expected, HostedInferenceBrain.ExtractContent(body));

    [Fact]
    public async Task ClearAndRestart_AreNoOps()
    {
        var brain = new HostedInferenceBrain("https://devthrottle.com/api/v1", "dt_live_abc", IncludedModelId.Wingman, new HttpClient(new StubHandler(HttpStatusCode.OK, OkBody("x"))), _ => { });
        await brain.ClearAsync();     // must not throw
        await brain.RestartAsync();   // must not throw
        Assert.Null(brain.SessionId);
    }
}
