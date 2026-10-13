using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE PER-TENANT AI GATE OVER THE WIRE (Pricing Change mission, B1). The Wingman's routes are mapped on a HOSTED
/// boundary, the caller is an authenticated device key bound to a real tenant, and the gate reads that tenant's plan
/// from a real database through the real entitlement registry. What is asserted is what a phone or the Cockpit sees:
/// the status, the 402 body, and - the point of the gate - that the model or speech provider is never reached for a
/// plan that does not include it.
///
/// HOW THESE FAIL ON PURPOSE: remove the plan check from a route and its Free/Remote test goes red (the provider is
/// reached); make the gate refuse everyone and the Pro control goes red.
/// </summary>
public sealed class HostedAiGateRouteTests : IAsyncLifetime
{
    private const string TenantHeader = "X-Test-Tenant";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hosted-ai-gate-routes-" + Guid.NewGuid().ToString("N"));
    private readonly CountingSpeech _speech = new();
    private int _brainCalls;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private TenantId _free;
    private TenantId _remote;
    private TenantId _pro;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var db = _harness.Open();
        using (var ctx = db.CreateUnscopedContext())
        {
            ctx.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS entitlements (" +
                "subject TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, " +
                "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, updated_at TEXT NULL, " +
                "livemode INTEGER NULL, tier TEXT NULL)");
            foreach (var (subject, tier) in new[] { ("sub-remote", EntitlementRegistry.TierRemote), ("sub-pro", EntitlementRegistry.TierPro) })
                ctx.Database.ExecuteSqlRaw(
                    "INSERT INTO entitlements (subject, status, current_period_end, livemode, tier) VALUES ({0}, {1}, {2}, {3}, {4})",
                    subject, EntitlementRegistry.StatusActive, new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc), true, tier);
        }
        var tenants = new TenantRegistry(db);
        _free = tenants.MintOrLookupBySubject("sub-free", "free@example.com");
        _remote = tenants.MintOrLookupBySubject("sub-remote", "remote@example.com");
        _pro = tenants.MintOrLookupBySubject("sub-pro", "pro@example.com");
        var gate = new TenantAiGate(new EntitlementRegistry(db, requireLivemode: true, trials: new TrialRegistry(db)), tenants);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");
        // The caller is an authenticated device key bound to the tenant the test names - what the auth middleware
        // stamps for a real request.
        _app.Use(async (ctx, next) =>
        {
            ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity(
                "dev-" + ctx.Request.Headers[TenantHeader], ctx.Request.Headers[TenantHeader].ToString(),
                DeviceRegistry.DefaultDeviceType, DeviceRegistry.StatusActive);
            await next();
        });

        var vault = new KeyVault(Path.Combine(_root, "keyvault.json"));
        vault.Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_test_not_a_real_key");
        vault.Set("OPENAI_API_KEY", "sk-test-not-a-real-key");
        var tenantSettings = new TenantSettingsResolver(new TenantSettingsStore(_harness.Open()));
        Func<TenantId, WingmanModelRole, string, CancellationToken, Task<IAgentBrain>> brain = (_, _, _, _) =>
        {
            Interlocked.Increment(ref _brainCalls);
            throw new InvalidOperationException("the test brain answers nothing; reaching it is what is asserted");
        };
        var voice = new WingmanVoiceService(brain, vault, tenantSettings, hosted: true, gate, Path.Combine(_root, "voice-sessions.json"));
        var boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), new DeviceRegistry(), hosted: true);

        GatewayWingmanVoiceEndpoint.Map(
            _app, hosted: true,
            new DirectorRegistry(Path.Combine(_root, "directors")),
            brain, vault, voice, tenantSettings, boundary, gate,
            ttsHttpClient: new HttpClient(_speech),
            audioArchive: new CcDirector.Gateway.Transcription.TranscriptionAudioArchive(true, Path.Combine(_root, "archive")),
            history: new CcDirector.Gateway.Transcription.TranscriptionHistoryLog(Path.Combine(_root, "history")));
        TranscriptionCleanupEndpoint.Map(_app, vault, boundary, gate);

        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _harness.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private Task<HttpResponseMessage> PostJsonAsync(TenantId tenant, string path, string json)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Add(TenantHeader, tenant.Value);
        return _http.SendAsync(req);
    }

    private Task<HttpResponseMessage> PostAudioAsync(TenantId tenant)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(new byte[] { 1, 2, 3, 4 });
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "audio", "clip.wav");
        var req = new HttpRequestMessage(HttpMethod.Post, "/wingman/transcribe") { Content = form };
        req.Headers.Add(TenantHeader, tenant.Value);
        return _http.SendAsync(req);
    }

    private static async Task AssertSubscriptionRequiredAsync(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.PaymentRequired, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Equal(HostedAiState.SubscriptionRequired, HostedAiErrorMapper.Map402(body));
        Assert.Contains("\"state\":\"SubscriptionRequired\"", body);
        Assert.Contains("\"code\":\"subscription_required\"", body);
    }

    // ================================================================= Free and Remote: refused, nothing reached

    [Fact]
    public async Task FreeAndRemote_TextToSpeech_Are402_AndTheSpeechProviderIsNeverCalled()
    {
        foreach (var tenant in new[] { _free, _remote })
            await AssertSubscriptionRequiredAsync(await PostJsonAsync(tenant, "/wingman/tts", "{\"text\":\"hello\"}"));

        Assert.Equal(0, _speech.Calls);
    }

    [Fact]
    public async Task FreeAndRemote_TheWingman_Is402_AndTheModelIsNeverAsked()
    {
        foreach (var tenant in new[] { _free, _remote })
        {
            await AssertSubscriptionRequiredAsync(await PostJsonAsync(tenant, "/wingman/ask-direct", "{\"text\":\"what now\"}"));
            await AssertSubscriptionRequiredAsync(await PostJsonAsync(tenant, "/wingman/ask-devthrottle", "{\"text\":\"how do I\"}"));
        }

        Assert.Equal(0, _brainCalls);
    }

    [Fact]
    public async Task FreeAndRemote_Dictation_Is402()
    {
        foreach (var tenant in new[] { _free, _remote })
        {
            await AssertSubscriptionRequiredAsync(await PostAudioAsync(tenant));
            await AssertSubscriptionRequiredAsync(await PostJsonAsync(tenant, "/transcription/cleanup", "{\"text\":\"the mindzee change\",\"terms\":[\"mindzie\"]}"));
        }
    }

    // ================================================================= Pro: through the gate

    [Fact]
    public async Task Pro_TextToSpeech_ReachesTheProviderAndAnswers200()
    {
        var res = await PostJsonAsync(_pro, "/wingman/tts", "{\"text\":\"hello\"}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, _speech.Calls);
    }

    [Fact]
    public async Task Pro_TheWingman_ReachesTheModel()
    {
        // The test brain throws once reached, so the route answers its own failure - not the plan's 402.
        var res = await PostJsonAsync(_pro, "/wingman/ask-direct", "{\"text\":\"what now\"}");

        Assert.NotEqual(HttpStatusCode.PaymentRequired, res.StatusCode);
        Assert.Equal(1, _brainCalls);
    }

    /// <summary>A speech provider that counts its calls and answers with a few bytes of audio.</summary>
    private sealed class CountingSpeech : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("ID3fake")) };
            res.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            return Task.FromResult(res);
        }
    }
}
