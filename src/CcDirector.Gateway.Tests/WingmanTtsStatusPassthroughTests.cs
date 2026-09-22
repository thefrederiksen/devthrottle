using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CcDirector.Core;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Proves <c>POST /wingman/tts</c> tells the caller what the far end actually said.
///
/// The defect: every non-402 upstream answer was flattened into a bare <c>502</c> and the
/// <c>Retry-After</c> header was dropped. The cloud proxy goes out of its way to forward the upstream's
/// status and Retry-After verbatim - it was rewritten for exactly that on 2026-07-15, after a flattened
/// status turned a provider hiccup into a 45-minute misdiagnosis - and then this endpoint destroyed the
/// same evidence one hop later. A client told "the gateway is broken" when the truth is "wait four
/// seconds" cannot back off; it retries straight into the same wall.
///
/// What is asserted here is only PASS-THROUGH. The Gateway must not sleep, retry, or invent a policy of
/// its own on these statuses: the far end is the only layer that knows when to come back, so the whole
/// job is to relay its answer without editing it.
/// </summary>
public sealed class WingmanTtsStatusPassthroughTests
{
    /// <summary>An upstream that answers with a fixed status, body, and optional Retry-After.</summary>
    private sealed class UpstreamStub(HttpStatusCode status, string body, string? retryAfter = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (retryAfter is not null) resp.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return Task.FromResult(resp);
        }
    }

    /// <summary>An upstream that never answers, so TtsSynthesis exhausts its attempts and gives up.</summary>
    private sealed class StallingUpstream : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);   // only the per-attempt timeout ends this
            throw new UnreachableException();
        }
    }

    private static async Task<(WebApplication App, HttpClient Http)> StartAsync(HttpMessageHandler upstream)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        // A key must be present or the endpoint short-circuits to 503 before ever calling upstream.
        var vault = new KeyVault(Path.Combine(Path.GetTempPath(), "cc-tts-pass-" + Guid.NewGuid().ToString("N") + ".vault"));
        vault.Set("DEVTHROTTLE_API_KEY", "dt_live_test_not_a_real_key");
        vault.Set("OPENAI_API_KEY", "sk-test-not-a-real-key");

        var persistPath = Path.Combine(Path.GetTempPath(), "cc-tts-pass-" + Guid.NewGuid().ToString("N") + ".json");
        var settingsData = new GatewayDbTestHarness();
        app.Lifetime.ApplicationStopped.Register(settingsData.Dispose);
        var tenantSettings = new TenantSettingsResolver(new TenantSettingsStore(settingsData.Open()));
        var voice = new WingmanVoiceService(
            (_, _, _, _) => throw new InvalidOperationException("the brain must not be reached by a tts status test"),
            vault, tenantSettings, persistPath);

        GatewayWingmanVoiceEndpoint.Map(
            app,
            new DirectorRegistry(Path.Combine(Path.GetTempPath(), "cc-tts-pass-inst-" + Guid.NewGuid().ToString("N"))),
            (_, _, _, _) => throw new InvalidOperationException("the brain must not be reached by a tts status test"),
            vault,
            voice,
            tenantSettings,
            // The boundary is required and non-nullable now (finding I1-01). Self-host harness, so the REAL
            // self-host boundary: built over the SingleTenantContext, it always resolves Local.
            new CcDirector.Gateway.Tenancy.HostedTenantBoundary(
                new CcDirector.Core.Tenancy.SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry()),
            ttsHttpClient: new HttpClient(upstream) { Timeout = Timeout.InfiniteTimeSpan },
            // The stall test proves a never-answering upstream becomes 504 rather than 502. What is under
            // test is WHICH status comes back, not how long the Gateway is willing to wait - so the deadline
            // is injected short. It used to run production's sixty-second base and cost a full minute of
            // every suite run to assert one status code (issue #1156).
            ttsDeadline: TimeSpan.FromMilliseconds(250));

        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static StringContent Say(string text = "hello") =>
        new($"{{\"text\":\"{text}\"}}", Encoding.UTF8, "application/json");

    // The headline: a 429 reaches the caller AS a 429, carrying the wait the provider asked for.
    [Fact]
    public async Task PostTts_Upstream429_PassesThroughTheStatusAndRetryAfter()
    {
        var (app, http) = await StartAsync(new UpstreamStub(HttpStatusCode.TooManyRequests, "{\"error\":\"slow down\"}", retryAfter: "4"));
        await using var _ = app;

        var res = await http.PostAsync("/wingman/tts", Say());

        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.Equal("4", res.Headers.GetValues("Retry-After").Single());
    }

    // Retry-After also has a DATE form. RetryAfterHeader normalizes both, so a client never has to parse
    // an HTTP date to learn it should wait.
    [Fact]
    public async Task PostTts_Upstream429WithDateRetryAfter_NormalizesTheHintToSeconds()
    {
        var when = DateTimeOffset.UtcNow.AddSeconds(30).ToString("R");
        var (app, http) = await StartAsync(new UpstreamStub(HttpStatusCode.TooManyRequests, "{}", retryAfter: when));
        await using var _ = app;

        var res = await http.PostAsync("/wingman/tts", Say());

        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        var seconds = int.Parse(res.Headers.GetValues("Retry-After").Single());
        Assert.InRange(seconds, 1, 31);   // ~30s, allowing for clock/scheduling slack
    }

    // A 429 with no hint is still a 429. The status is the load-bearing part; the header is a bonus.
    [Fact]
    public async Task PostTts_Upstream429WithNoHint_StillPassesTheStatusThrough()
    {
        var (app, http) = await StartAsync(new UpstreamStub(HttpStatusCode.TooManyRequests, "{}"));
        await using var _ = app;

        var res = await http.PostAsync("/wingman/tts", Say());

        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.False(res.Headers.Contains("Retry-After"));
    }

    // 503 is the other "come back later" answer and travels the same road.
    [Fact]
    public async Task PostTts_Upstream503_PassesThroughTheStatusAndRetryAfter()
    {
        var (app, http) = await StartAsync(new UpstreamStub(HttpStatusCode.ServiceUnavailable, "{}", retryAfter: "12"));
        await using var _ = app;

        var res = await http.PostAsync("/wingman/tts", Say());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Equal("12", res.Headers.GetValues("Retry-After").Single());
    }

    // 402 keeps its existing shape: the account state, not a transport status. Proving it here stops a
    // future edit to the pass-through branch from swallowing the credits flow.
    [Fact]
    public async Task PostTts_Upstream402_StillMapsToTheSharedPaymentState()
    {
        var (app, http) = await StartAsync(new UpstreamStub(HttpStatusCode.PaymentRequired,
            "{\"error\":{\"code\":\"insufficient_credits\",\"message\":\"out of credits\"}}"));
        await using var _ = app;

        var res = await http.PostAsync("/wingman/tts", Say());

        Assert.Equal(HttpStatusCode.PaymentRequired, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("NeedsCredits", body, StringComparison.Ordinal);
    }

    // An upstream 500 is a bad gateway: the far end answered, and it answered badly.
    [Fact]
    public async Task PostTts_Upstream500_Returns502BadGateway()
    {
        var (app, http) = await StartAsync(new UpstreamStub(HttpStatusCode.InternalServerError, "{}"));
        await using var _ = app;

        var res = await http.PostAsync("/wingman/tts", Say());

        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
    }

    // ...and a stall is a gateway TIMEOUT, which is a different fact about the world. These two used to
    // be the same 502, which is precisely what makes an outage unreadable from the outside: "it answered
    // with an error" and "it never answered" call for different responses from a client and different
    // questions from support.
    [Fact]
    public async Task PostTts_UpstreamNeverAnswers_Returns504NotBadGateway()
    {
        var (app, http) = await StartAsync(new StallingUpstream());
        await using var _ = app;

        var res = await http.PostAsync("/wingman/tts", Say());

        Assert.Equal(HttpStatusCode.GatewayTimeout, res.StatusCode);
    }
}
