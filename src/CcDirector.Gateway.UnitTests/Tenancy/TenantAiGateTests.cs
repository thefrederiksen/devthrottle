using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.HostedAi;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Transcription;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CcDirector.Gateway.Tests.Tenancy;

/// <summary>
/// THE PER-TENANT AI GATE (Pricing Change mission, B1). The hosted Gateway calls the models with one shared key, so
/// before this nothing checked, per member, that the plan included what a call paid for. <see cref="TenantAiGate"/> is
/// the one place that answers it, reading the plan through the real <see cref="EntitlementRegistry"/> (a real database,
/// the real trial ledger) and the scope table <see cref="EntitlementScopes"/>.
///
/// HOW THESE FAIL ON PURPOSE: give the free or remote line in EntitlementScopes any AI scope and the refusal tests go
/// red; make the gate answer Allowed by default and every refusal test goes red while the Pro control stays green; let a
/// failed read be a refusal and the Unknown test goes red.
/// </summary>
public sealed class TenantAiGateTests : IDisposable
{
    private const string FreeSubject = "sub-free";
    private const string RemoteSubject = "sub-remote";
    private const string ProSubject = "sub-pro";
    private const string HostedSubject = "sub-hosted";
    private const string LegacySubject = "sub-legacy";
    private const string TrialSubject = "sub-trial";

    private static readonly string[] AiScopes = { EntitlementScopes.Dictation, EntitlementScopes.Tts, EntitlementScopes.Wingman };

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly EntitlementRegistry _entitlements;
    private readonly TenantAiGate _gate;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tenant-ai-gate-" + Guid.NewGuid().ToString("N"));

    public TenantAiGateTests()
    {
        _db = _harness.Open();
        using (var ctx = _db.CreateUnscopedContext())
        {
            ctx.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS entitlements (" +
                "subject TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, " +
                "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, updated_at TEXT NULL, " +
                "livemode INTEGER NULL, tier TEXT NULL)");
        }
        _tenants = new TenantRegistry(_db);
        var trials = new TrialRegistry(_db);
        // The production shape: live money required, and the trial ledger wired, so an account with no paid row and
        // no running trial reads as the FREE tier - exactly as the hosted Gateway reads it.
        _entitlements = new EntitlementRegistry(_db, requireLivemode: true, trials: trials);
        _gate = new TenantAiGate(_entitlements, _tenants);

        Paid(RemoteSubject, EntitlementRegistry.TierRemote);
        Paid(ProSubject, EntitlementRegistry.TierPro);
        Paid(HostedSubject, EntitlementRegistry.TierHosted);
        Paid(LegacySubject, tier: null);
        trials.GrantIfFirstArrival(TrialSubject, alreadyKnownToGateway: false, DateTime.UtcNow);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _harness.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void Paid(string subject, string? tier)
    {
        using var ctx = _db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO entitlements (subject, status, current_period_end, livemode, tier) VALUES ({0}, {1}, {2}, {3}, {4})",
            subject, EntitlementRegistry.StatusActive, new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc), true, tier);
    }

    private TenantId TenantOf(string subject) => _tenants.MintOrLookupBySubject(subject, subject + "@example.com");

    // ================================================================= the scope table

    [Fact]
    public void EntitlementScopes_Remote_GrantsTheHostedGatewayAndNoAiScope()
    {
        // Decision D3: Remote is the hosted Gateway, the Cockpit and the phone app - and no dictation, TTS or Wingman.
        var granted = EntitlementScopes.ForTier(EntitlementRegistry.TierRemote);

        Assert.Equal(new[] { EntitlementScopes.HostedGateway }, granted.ToArray());
        Assert.True(EntitlementScopes.GrantsHostedGateway(EntitlementRegistry.TierRemote));
        foreach (var scope in AiScopes)
            Assert.False(EntitlementScopes.Grants(EntitlementRegistry.TierRemote, scope));
    }

    // ================================================================= the plan matrix

    [Theory]
    [InlineData(EntitlementScopes.Dictation)]
    [InlineData(EntitlementScopes.Tts)]
    [InlineData(EntitlementScopes.Wingman)]
    public void ForTenant_TheFreePlan_IsRefusedEveryAiScope(string scope)
    {
        // No paid row and no running trial: the free tier, which grants the hosted Gateway and no model call.
        var decision = _gate.ForTenant(TenantOf(FreeSubject), scope);

        Assert.Equal(TenantAiVerdict.Refused, decision.Verdict);
        Assert.Equal(EntitlementRegistry.TierFree, decision.Tier);
    }

    [Theory]
    [InlineData(EntitlementScopes.Dictation)]
    [InlineData(EntitlementScopes.Tts)]
    [InlineData(EntitlementScopes.Wingman)]
    public void ForTenant_TheRemotePlan_IsRefusedEveryAiScope(string scope)
    {
        var decision = _gate.ForTenant(TenantOf(RemoteSubject), scope);

        Assert.Equal(TenantAiVerdict.Refused, decision.Verdict);
        Assert.Equal(EntitlementRegistry.TierRemote, decision.Tier);
    }

    [Theory]
    [InlineData(ProSubject)]
    [InlineData(HostedSubject)]
    [InlineData(LegacySubject)]
    [InlineData(TrialSubject)]
    public void ForTenant_ProHostedALegacyNullTierAndARunningTrial_AreAllowedEveryAiScope(string subject)
    {
        // Pro is the control for the refusals above. A legacy row with no tier is an old hosted row and keeps
        // everything; a running trial counts as Pro (the registry answers it at the Pro tier).
        foreach (var scope in AiScopes)
            Assert.Equal(TenantAiVerdict.Allowed, _gate.ForTenant(TenantOf(subject), scope).Verdict);
    }

    [Fact]
    public void ForRequest_AndForSession_InAPersonalTenant_AnswerTheAccountsPlan()
    {
        var ctx = new DefaultHttpContext();
        Assert.Equal(TenantAiVerdict.Refused, _gate.ForRequest(ctx, TenantOf(FreeSubject), EntitlementScopes.Tts).Verdict);
        Assert.Equal(TenantAiVerdict.Allowed, _gate.ForRequest(ctx, TenantOf(ProSubject), EntitlementScopes.Tts).Verdict);
        Assert.Equal(TenantAiVerdict.Refused, _gate.ForSession(TenantOf(FreeSubject), "s-1", EntitlementScopes.Wingman).Verdict);
        Assert.Equal(TenantAiVerdict.Allowed, _gate.ForSession(TenantOf(ProSubject), "s-1", EntitlementScopes.Wingman).Verdict);
    }

    [Fact]
    public void ForTenant_ATenantWithNoAccount_AndTheSelfHostTenant_AreRefusedOnTheHostedGateway()
    {
        // A hosted call that names no account cannot be checked, so it is not made.
        Assert.Equal(TenantAiVerdict.Refused, _gate.ForTenant(new TenantId("tenant-nobody-minted"), EntitlementScopes.Wingman).Verdict);
        Assert.Equal(TenantAiVerdict.Refused, _gate.ForTenant(TenantId.Local, EntitlementScopes.Wingman).Verdict);
        Assert.Equal(TenantAiVerdict.Refused, _gate.ForTenant(null, EntitlementScopes.Wingman).Verdict);
    }

    [Fact]
    public void ForTenant_AnUnreadableEntitlementTable_IsUnknown_NeverARefusalOrAGrant()
    {
        var tenant = TenantOf(ProSubject);
        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DROP TABLE entitlements");

        var decision = _gate.ForTenant(tenant, EntitlementScopes.Dictation);

        Assert.Equal(TenantAiVerdict.Unknown, decision.Verdict);
        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void EnsureAllowedForTenant_Refused_ThrowsWithTheDecision_AndAllowedDoesNot()
    {
        var ex = Assert.Throws<TenantAiRefusedException>(
            () => _gate.EnsureAllowedForTenant(TenantOf(FreeSubject), EntitlementScopes.Wingman));
        Assert.Equal(TenantAiVerdict.Refused, ex.Decision.Verdict);

        _gate.EnsureAllowedForTenant(TenantOf(ProSubject), EntitlementScopes.Wingman);
    }

    // ================================================================= self-host is unchanged (decision D5)

    [Fact]
    public void SelfHost_NoEntitlementRegistry_AllowsEveryScope_ForAnyTenant()
    {
        Assert.False(TenantAiGate.SelfHost.IsGating);
        var unwired = new TenantAiGate(entitlements: null, tenants: null);
        foreach (var gate in new[] { TenantAiGate.SelfHost, unwired })
        foreach (var scope in AiScopes)
        {
            Assert.Equal(TenantAiVerdict.Allowed, gate.ForTenant(TenantId.Local, scope).Verdict);
            Assert.Equal(TenantAiVerdict.Allowed, gate.ForTenant(null, scope).Verdict);
            Assert.Equal(TenantAiVerdict.Allowed, gate.ForRequest(new DefaultHttpContext(), TenantId.Local, scope).Verdict);
        }
    }

    // ================================================================= a refusal is only a refusal

    [Fact]
    public async Task FreeTenant_RefusedEveryAiScope_KeepsItsDeviceKeyAndItsHostedAccess()
    {
        // Goal 3/4: a Free member keeps their Director. Refusing every AI call must not revoke, tombstone or touch a
        // device key, and the hosted access lease must still serve the tenant afterwards.
        var tenant = TenantOf(FreeSubject);
        var devices = _harness.OpenDevices();
        var device = devices.Register("device-free", "MACHINE-FREE");
        Assert.True(devices.SetAccountBinding("device-free", FreeSubject, tenant.Value));
        var revoker = new RecordingRevoker();
        var leases = new HostedAccessLeaseService(_entitlements, _tenants, revoker);

        foreach (var scope in AiScopes)
        {
            Assert.False(_gate.ForTenant(tenant, scope).IsAllowed);
            Assert.False(_gate.ForRequest(new DefaultHttpContext(), tenant, scope).IsAllowed);
            Assert.False(_gate.ForSession(tenant, "s-1", scope).IsAllowed);
        }

        Assert.Equal(tenant.Value, devices.TenantForKey(device.DeviceKey));
        Assert.Equal(HostedAccessDecision.Allow, await leases.AuthorizeAsync(tenant));
        Assert.Empty(revoker.Revoked);
    }

    private sealed class RecordingRevoker : ITenantAccessRevoker
    {
        public List<string> Revoked { get; } = new();

        public Task RevokeAsync(TenantId tenant, string reason, CancellationToken ct = default)
        {
            Revoked.Add(tenant.Value);
            return Task.CompletedTask;
        }
    }

    // ================================================================= the HTTP answer

    [Fact]
    public async Task TenantAiRefusal_Refused_IsThe402SubscriptionRequiredContract_TheDirectorAndThePhoneBothRead()
    {
        var (status, headers, body) = await ExecuteAsync(HostedAiHttp.TenantAiRefusal(
            new TenantAiDecision(TenantAiVerdict.Refused, EntitlementScopes.Dictation, EntitlementRegistry.TierFree, "free")));

        Assert.Equal(402, status);
        // The Director's parser: reads the machine code and maps it to the subscription state.
        Assert.Equal(HostedAiErrorMapper.SubscriptionRequiredCode, HostedAiErrorMapper.ParseErrorCode(body));
        Assert.Equal(HostedAiState.SubscriptionRequired, HostedAiErrorMapper.Map402(body));
        // The phone's and the Cockpit's parser (client-core creditsErrorFrom): state, text, and the pricing page.
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(nameof(HostedAiState.SubscriptionRequired), root.GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("text").GetString()));
        Assert.Equal(root.GetProperty("text").GetString(), root.GetProperty("error").GetString());
        Assert.Equal(HostedAiUrls.Pricing, root.GetProperty("ctaUrl").GetString());
        Assert.False(headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public async Task TenantAiRefusal_Unknown_IsATemporary503WithRetryAfter_NeverA402()
    {
        var (status, headers, body) = await ExecuteAsync(HostedAiHttp.TenantAiRefusal(
            new TenantAiDecision(TenantAiVerdict.Unknown, EntitlementScopes.Tts, null, "unreadable")));

        Assert.Equal(503, status);
        Assert.Equal("10", headers["Retry-After"].ToString());
        Assert.Contains("entitlement_unknown", body);
    }

    [Fact]
    public void TenantAiRefusal_AnAllowedCall_HasNoRefusalToAnswer()
    {
        Assert.Throws<ArgumentException>(() => HostedAiHttp.TenantAiRefusal(
            new TenantAiDecision(TenantAiVerdict.Allowed, EntitlementScopes.Tts, EntitlementRegistry.TierPro, "pro")));
    }

    private static async Task<(int Status, IHeaderDictionary Headers, string Body)> ExecuteAsync(IResult result)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (ctx.Response.StatusCode, ctx.Response.Headers, body);
    }

    // ================================================================= dictation: the transcription service

    [Fact]
    public async Task TranscribeAsync_FreeTenant_AnswersSubscriptionRequired_AndNeverCallsTheProvider()
    {
        var provider = new CountingHandler();
        var service = Transcription(provider);

        var result = await service.TranscribeAsync(new byte[] { 1, 2, 3 }, "a.wav", "audio/wav", applyCorrection: true,
            CancellationToken.None, tenant: TenantOf(FreeSubject), source: "dictation");

        Assert.Equal(TranscriptionOutcome.OutOfCredits, result.Outcome);
        Assert.Equal(HostedAiErrorMapper.SubscriptionRequiredCode, result.Code);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task TranscribeAsync_ProTenant_IsNotRefusedByThePlan()
    {
        // The control: the same call for a Pro tenant passes the gate (whatever the provider then answers, it is not
        // the plan's refusal).
        var service = Transcription(new CountingHandler());

        var result = await service.TranscribeAsync(new byte[] { 1, 2, 3 }, "a.wav", "audio/wav", applyCorrection: false,
            CancellationToken.None, tenant: TenantOf(ProSubject), source: "dictation");

        Assert.NotEqual(HostedAiErrorMapper.SubscriptionRequiredCode, result.Code);
    }

    [Fact]
    public async Task TranscribeSegmentUncorrectedAsync_FreeTenant_RefusesWithoutCallingTheProvider()
    {
        // The phone Notes path: refused the way a missing key is, so the recording's job is retried later.
        var provider = new CountingHandler();
        var service = Transcription(provider);

        await Assert.ThrowsAsync<TenantAiRefusedException>(() => service.TranscribeSegmentUncorrectedAsync(
            new byte[] { 1, 2, 3 }, "a.wav", "audio/wav", CancellationToken.None, TenantOf(FreeSubject), "notes"));
        Assert.Equal(0, provider.Calls);
    }

    private GatewayTranscriptionService Transcription(HttpMessageHandler provider)
    {
        var vaultPath = Path.Combine(_root, "keyvault-" + Guid.NewGuid().ToString("N") + ".json");
        new KeyVault(vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_test");
        return new GatewayTranscriptionService(
            new KeyVault(vaultPath),
            new TranscriptionAudioArchive(true, Path.Combine(_root, "archive")),
            _gate,
            dictionaryProvider: _ => DictationDictionary.Empty,
            modeProvider: () => TranscriptionMode.DevThrottle,
            http: new HttpClient(provider),
            history: new TranscriptionHistoryLog(Path.Combine(_root, "history")));
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\":\"hello\"}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
