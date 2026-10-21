using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Memory;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Teams;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// FACTORIES NEED PRO (mission of 21 Oct 2026, issue #4326). The owner: "factories should be a part of the pro plan
/// only. You need pro to set up factories. Shouldn't be included in the remote." So the Factory Agents area is on for
/// an account when ANY of three grants holds - the machine switch, the owner's per-account switch, or (hosted only) a
/// plan that carries <see cref="EntitlementScopes.Factories"/> - and a plan that cannot be read is Unknown, never off.
///
/// Everything here runs over a real migrated database and the REAL plan read (<see cref="EntitlementRegistry"/>, the
/// trial ledger, the team bill, <see cref="TenantAiGate"/>), with a fixed clock and no host on a port.
///
/// HOW THESE FAIL ON PURPOSE: give Remote or Free the Factories scope and the plan_required rows go red; drop it from Pro
/// or Team and their On rows go red; let the self-host gate's Allowed count as a plan grant and the self-hosted test
/// goes red; fold an unreadable plan into off and the Unknown rows go red; ask the plan before the owner switch and the
/// switched-on Unknown row goes red.
/// </summary>
public sealed class FactoriesNeedProTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FarFuture = new(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string TenantHeader = "X-Test-Account";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly EntitlementRegistry _entitlements;
    private readonly TeamRegistry _teams;
    private readonly TenantAiGate _plans;
    private readonly List<string> _log = new();

    public FactoriesNeedProTests()
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
        // The production shape: live money required and the trial ledger wired, so an account with no paid row and no
        // running trial reads as the FREE tier, exactly as the hosted Gateway reads it.
        _entitlements = new EntitlementRegistry(_db, requireLivemode: true, trials: new TrialRegistry(_db));
        _teams = new TeamRegistry(_db, _tenants);
        // The hosted Gateway's gate, as GatewayHost builds it, on a fixed clock.
        _plans = new TenantAiGate(_entitlements, _tenants, new TeamMemberEntitlement(_teams, _entitlements),
            clock: new FakeTimeProvider(new DateTimeOffset(Now)));
    }

    public void Dispose() => _harness.Dispose();

    // ================================================================= the accounts

    /// <summary>Every kind of account the rule must answer for, by name.</summary>
    private TenantId Account(string kind) => kind switch
    {
        "free" => TenantOf("sub-free"),
        "remote" => Paid("sub-remote", EntitlementRegistry.TierRemote),
        "pro" => Paid("sub-pro", EntitlementRegistry.TierPro),
        "team" => TeamWithARunningBill(),
        "team-no-bill" => _teams.CreateTeam(OwnerOfATeam(), "Team without a bill").Team!.Tenant,
        "legacy" => Paid("sub-legacy", tier: null),
        "hosted" => Paid("sub-hosted", EntitlementRegistry.TierHosted),
        "pro-canceled" => Paid("sub-canceled", EntitlementRegistry.TierPro, EntitlementRegistry.StatusCanceled),
        "pro-past-due-in-grace" => Paid("sub-grace", EntitlementRegistry.TierPro, EntitlementRegistry.StatusPastDue, Now.AddDays(3)),
        "pro-past-due-grace-ended" => Paid("sub-ended", EntitlementRegistry.TierPro, EntitlementRegistry.StatusPastDue, Now.AddDays(-1)),
        "unknown" => Unreadable(Paid("sub-unknown", EntitlementRegistry.TierPro)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "no such account in this test"),
    };

    private TenantId TenantOf(string subject) => _tenants.MintOrLookupBySubject(subject, subject + "@example.com");

    private TenantId Paid(string subject, string? tier, string status = EntitlementRegistry.StatusActive, DateTime? periodEnd = null)
    {
        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO entitlements (subject, status, current_period_end, livemode, tier) VALUES ({0}, {1}, {2}, {3}, {4})",
                subject, status, periodEnd ?? FarFuture, true, tier);
        return TenantOf(subject);
    }

    private string OwnerOfATeam()
    {
        var owner = "sub-team-owner-" + Guid.NewGuid().ToString("N")[..8];
        TenantOf(owner);
        return owner;
    }

    private TenantId TeamWithARunningBill()
    {
        var team = _teams.CreateTeam(OwnerOfATeam(), "Team with a bill").Team!.Tenant;
        TeamBillSeed.Active(_db, team.Value, seats: 3);
        return team;
    }

    /// <summary>The plan read fails from here on: the entitlement table is gone, as when the database is unreachable.</summary>
    private TenantId Unreadable(TenantId tenant)
    {
        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DROP TABLE entitlements");
        return tenant;
    }

    private FactoryAgentsSwitch HostedSwitch(bool machineWide = false)
        => new(machineWide, new TenantSettingsStore(_db), _plans, _log.Add);

    private static void SwitchOn(FactoryAgentsSwitch sw, TenantId tenant)
        => sw.Set(tenant, true, "owner@example.com", "the owner gave this account factories", Now);

    // ================================================================= the scope table

    [Fact]
    public void EntitlementScopes_Factories_IsCarriedByProTeamHostedAndTheLegacyRow_AndNothingElse()
    {
        // Pro and Teams, and the two rows that carry every Pro scope.
        Assert.True(EntitlementScopes.Grants(EntitlementRegistry.TierPro, EntitlementScopes.Factories));
        Assert.True(EntitlementScopes.Grants(EntitlementRegistry.TierTeam, EntitlementScopes.Factories));
        Assert.True(EntitlementScopes.Grants(EntitlementRegistry.TierHosted, EntitlementScopes.Factories));
        Assert.True(EntitlementScopes.Grants(null, EntitlementScopes.Factories));
        // "Shouldn't be included in the remote." Nor Free, nor the self-host plan, nor a tier this code was never taught.
        Assert.False(EntitlementScopes.Grants(EntitlementRegistry.TierRemote, EntitlementScopes.Factories));
        Assert.False(EntitlementScopes.Grants(EntitlementRegistry.TierFree, EntitlementScopes.Factories));
        Assert.False(EntitlementScopes.Grants(EntitlementRegistry.TierProSelfHost, EntitlementScopes.Factories));
        Assert.False(EntitlementScopes.Grants("enterprise-gold", EntitlementScopes.Factories));
    }

    // ================================================================= the rule, every row

    [Theory]
    [InlineData("free", FactoryAgentsState.Off, FactoryAgentsOffReasons.PlanRequired)]
    [InlineData("remote", FactoryAgentsState.Off, FactoryAgentsOffReasons.PlanRequired)]
    [InlineData("pro", FactoryAgentsState.On, null)]
    [InlineData("team", FactoryAgentsState.On, null)]
    [InlineData("team-no-bill", FactoryAgentsState.Off, FactoryAgentsOffReasons.PlanRequired)]
    [InlineData("legacy", FactoryAgentsState.On, null)]
    [InlineData("hosted", FactoryAgentsState.On, null)]
    [InlineData("pro-canceled", FactoryAgentsState.Off, FactoryAgentsOffReasons.PlanRequired)]
    [InlineData("pro-past-due-in-grace", FactoryAgentsState.On, null)]
    [InlineData("pro-past-due-grace-ended", FactoryAgentsState.Off, FactoryAgentsOffReasons.PlanRequired)]
    [InlineData("unknown", FactoryAgentsState.Unknown, FactoryAgentsOffReasons.PlanUnknown)]
    public void Answer_NoOwnerSwitch_IsDecidedByThePlan(string kind, FactoryAgentsState state, string? reason)
    {
        var sw = HostedSwitch();
        var tenant = Account(kind);

        var answer = sw.Answer(tenant);

        Assert.Equal(state, answer.State);
        Assert.Equal(reason, answer.OffReason);
        Assert.Equal(state == FactoryAgentsState.On ? FactoryAgentsGrant.Plan : FactoryAgentsGrant.None, answer.Grant);
        Assert.Equal(state == FactoryAgentsState.On, sw.IsOn(tenant));
    }

    [Theory]
    [InlineData("free")]
    [InlineData("remote")]
    [InlineData("pro")]
    [InlineData("team")]
    [InlineData("legacy")]
    [InlineData("hosted")]
    [InlineData("pro-canceled")]
    [InlineData("pro-past-due-in-grace")]
    [InlineData("unknown")]
    public void Answer_OwnerSwitchOn_IsOnByTheOwnerSwitch_WhateverThePlan_EvenWhenThePlanCannotBeRead(string kind)
    {
        // The owner switch is a local settings read asked BEFORE the plan, so an account switched on never depends on
        // the entitlement read - the "unknown" row is the proof: its plan read would fail, and it is on.
        var sw = HostedSwitch();
        var tenant = Account(kind);
        SwitchOn(sw, tenant);

        var answer = sw.Answer(tenant);

        Assert.Equal(FactoryAgentsState.On, answer.State);
        Assert.Equal(FactoryAgentsGrant.OwnerSwitch, answer.Grant);
        Assert.Null(answer.OffReason);
    }

    [Theory]
    [InlineData("free")]
    [InlineData("remote")]
    [InlineData("pro")]
    [InlineData("team")]
    [InlineData("legacy")]
    [InlineData("pro-canceled")]
    [InlineData("pro-past-due-in-grace")]
    [InlineData("unknown")]
    public void Answer_MachineSwitchOn_IsOnByTheMachineSwitch_ForEveryAccount(string kind)
    {
        var sw = HostedSwitch(machineWide: true);
        var tenant = Account(kind);

        var answer = sw.Answer(tenant);

        Assert.Equal(FactoryAgentsState.On, answer.State);
        Assert.Equal(FactoryAgentsGrant.MachineSwitch, answer.Grant);
    }

    [Fact]
    public void Answer_AnAccountSwitchedOffByHand_StillHasFactoriesByItsProPlan()
    {
        // An off decision removes the owner's grant; it is not a refusal of the plan's.
        var sw = HostedSwitch();
        var pro = Account("pro");
        sw.Set(pro, false, "owner@example.com", "taken back", Now);

        Assert.Equal(FactoryAgentsGrant.Plan, sw.Answer(pro).Grant);
    }

    [Fact]
    public void Answer_NoAccountBoundOnTheHostedGateway_IsOffForThePlan_NeverOn()
    {
        var answer = HostedSwitch().Answer(null);

        Assert.Equal(FactoryAgentsState.Off, answer.State);
        Assert.Equal(FactoryAgentsOffReasons.PlanRequired, answer.OffReason);
    }

    [Fact]
    public void Answer_IsLoggedWithItsGrant_AndTheQuietAnswerIsIdenticalWithNoLine()
    {
        var sw = HostedSwitch();
        var pro = Account("pro");
        var free = Account("free");
        SwitchOn(sw, free);

        var loud = sw.Answer(pro);
        sw.Answer(free);
        var linesAfterLoud = _log.Count;
        var quiet = sw.AnswerQuietly(pro);

        Assert.Contains(_log, l => l.Contains("grant=plan tier pro", StringComparison.Ordinal) && l.Contains("state=On", StringComparison.Ordinal));
        Assert.Contains(_log, l => l.Contains("grant=owner switch", StringComparison.Ordinal));
        Assert.Equal(linesAfterLoud, _log.Count);
        Assert.Equal(loud, quiet);
    }

    // ================================================================= self-hosted is unchanged

    [Fact]
    public void Answer_SelfHostedMachineSwitchOff_IsNotSwitchedOn_EvenGivenTheSelfHostGateThatAllowsEverything()
    {
        // The trap this guards: the self-host AI gate answers Allowed for every scope, factories included...
        Assert.True(TenantAiGate.SelfHost.ForTenant(TenantId.Local, EntitlementScopes.Factories).IsAllowed);

        // ...and a switch handed that gate must not read it as a plan grant, or every self-hosted Gateway with its
        // machine switch off would turn factories on.
        var handedTheSelfHostGate = new FactoryAgentsSwitch(false, new TenantSettingsStore(_db), TenantAiGate.SelfHost, _log.Add);
        var handedNoGate = new FactoryAgentsSwitch(false, new TenantSettingsStore(_db), plans: null, _log.Add);

        foreach (var sw in new[] { handedTheSelfHostGate, handedNoGate })
        {
            Assert.False(sw.ChecksPlans);
            var answer = sw.Answer(TenantId.Local);
            Assert.Equal(FactoryAgentsState.Off, answer.State);
            Assert.Equal(FactoryAgentsOffReasons.NotSwitchedOn, answer.OffReason);
        }
    }

    [Fact]
    public void Answer_SelfHostedMachineSwitchOn_IsOn_AndAnOwnerSwitchStillWorks()
    {
        Assert.Equal(FactoryAgentsGrant.MachineSwitch,
            new FactoryAgentsSwitch(true, new TenantSettingsStore(_db), TenantAiGate.SelfHost).Answer(TenantId.Local).Grant);

        var sw = new FactoryAgentsSwitch(false, new TenantSettingsStore(_db), TenantAiGate.SelfHost);
        SwitchOn(sw, TenantId.Local);
        Assert.Equal(FactoryAgentsGrant.OwnerSwitch, sw.Answer(TenantId.Local).Grant);
    }

    // ================================================================= the routes and the switch question

    /// <summary>The switch question and one real factory route behind the real gate, wired as GatewayHost wires them,
    /// with the calling account named by a header. Invoked in process; no port is opened.</summary>
    private WebApplication FactoryApp(FactoryAgentsSwitch sw, bool hosted = true)
    {
        Func<HttpContext, TenantId?> resolve = ctx =>
            ctx.Request.Headers.TryGetValue(TenantHeader, out var v) && !string.IsNullOrWhiteSpace(v)
                ? new TenantId(v.ToString())
                : null;
        var app = WebApplication.CreateBuilder().Build();
        FactoryAgentsViewEndpoints.MapSwitch(app, hosted, sw, resolve);
        FactoryActivityEndpoints.Map(FactoryAgentsGate.Group(app, sw, resolve), new FactoryActivityRecord(_db));
        return app;
    }

    private sealed record Reply(int Status, string Body, string? RetryAfter);

    private static async Task<Reply> GetAsync(WebApplication app, string route, TenantId account)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == route
                         && e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("GET") == true);
        var ctx = new DefaultHttpContext { RequestServices = app.Services };
        ctx.Request.Method = "GET";
        ctx.Request.Path = route;
        ctx.Request.Headers[TenantHeader] = account.Value;
        var body = new MemoryStream();
        ctx.Response.Body = body;
        await endpoint.RequestDelegate!(ctx);
        return new Reply(ctx.Response.StatusCode, System.Text.Encoding.UTF8.GetString(body.ToArray()),
            ctx.Response.Headers.RetryAfter.ToString() is { Length: > 0 } r ? r : null);
    }

    private const string Activity = "/gateway/factory/activity";
    private const string SwitchRoute = FactoryAgentsViewEndpoints.Prefix + "/switch";

    [Fact]
    public async Task FactoryRoute_Pro_IsServed()
    {
        var sw = HostedSwitch();
        Assert.Equal(StatusCodes.Status200OK, (await GetAsync(FactoryApp(sw), Activity, Account("pro"))).Status);
    }

    [Fact]
    public async Task FactoryRoute_Remote_Answers404WithNoBody_AsIfUnmapped()
    {
        var reply = await GetAsync(FactoryApp(HostedSwitch()), Activity, Account("remote"));

        Assert.Equal(StatusCodes.Status404NotFound, reply.Status);
        Assert.Equal("", reply.Body);
    }

    [Fact]
    public async Task FactoryRoute_FreeWithTheOwnerSwitch_IsServed()
    {
        var sw = HostedSwitch();
        var free = Account("free");
        SwitchOn(sw, free);

        Assert.Equal(StatusCodes.Status200OK, (await GetAsync(FactoryApp(sw), Activity, free)).Status);
    }

    [Fact]
    public async Task FactoryRoute_PlanCannotBeRead_IsTheTemporary503TheRequestPathAnswers_Never404()
    {
        var reply = await GetAsync(FactoryApp(HostedSwitch()), Activity, Account("unknown"));

        // The access lease's answer for an unreadable plan (AuthMiddleware, HostedAccessDecision.RetryUnknown).
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, reply.Status);
        Assert.Equal("10", reply.RetryAfter);
        Assert.Equal("entitlement_unknown", JsonDocument.Parse(reply.Body).RootElement.GetProperty("code").GetString());
    }

    private static JsonElement Json(Reply reply) => JsonDocument.Parse(reply.Body).RootElement;

    private static string? Str(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetString();

    [Fact]
    public async Task SwitchQuestion_Pro_IsEnabled_WithNoReasonAndNoUpgradeFields()
    {
        var reply = await GetAsync(FactoryApp(HostedSwitch()), SwitchRoute, Account("pro"));
        var json = Json(reply);

        Assert.Equal(StatusCodes.Status200OK, reply.Status);
        Assert.True(json.GetProperty("enabled").GetBoolean());
        Assert.Null(Str(json, "reason"));
        Assert.Null(Str(json, "howToStart"));
        Assert.Null(Str(json, "title"));
        Assert.Null(Str(json, "subscribeUrl"));
    }

    [Theory]
    [InlineData("remote")]
    [InlineData("free")]
    public async Task SwitchQuestion_NoPlanWithFactories_IsPlanRequired_WithTheUpgradePage(string kind)
    {
        var reply = await GetAsync(FactoryApp(HostedSwitch()), SwitchRoute, Account(kind));
        var json = Json(reply);

        Assert.Equal(StatusCodes.Status200OK, reply.Status);
        Assert.False(json.GetProperty("enabled").GetBoolean());
        Assert.Equal("plan_required", Str(json, "reason"));
        Assert.Equal("Factories come with Pro", Str(json, "title"));
        Assert.Equal(EntitlementRegistry.SubscribeUrl, Str(json, "subscribeUrl"));
        Assert.Equal(FactoryAgentsViewEndpoints.PlanRequiredText, Str(json, "howToStart"));
        Assert.DoesNotContain("trial", FactoryAgentsViewEndpoints.PlanRequiredText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SwitchQuestion_PlanCannotBeRead_IsStill200_WithPlanUnknownAndASentence()
    {
        var reply = await GetAsync(FactoryApp(HostedSwitch()), SwitchRoute, Account("unknown"));
        var json = Json(reply);

        Assert.Equal(StatusCodes.Status200OK, reply.Status);
        Assert.False(json.GetProperty("enabled").GetBoolean());
        Assert.Equal("plan_unknown", Str(json, "reason"));
        Assert.Equal(FactoryAgentsViewEndpoints.PlanUnknownText, Str(json, "howToStart"));
        Assert.Null(Str(json, "title"));
        Assert.Null(Str(json, "subscribeUrl"));
    }

    [Fact]
    public async Task SwitchQuestion_SelfHostedMachineSwitchOff_IsNotSwitchedOn_WithTodaysSentence()
    {
        var sw = new FactoryAgentsSwitch(false, new TenantSettingsStore(_db), TenantAiGate.SelfHost);
        var reply = await GetAsync(FactoryApp(sw, hosted: false), SwitchRoute, TenantId.Local);
        var json = Json(reply);

        Assert.Equal("not_switched_on", Str(json, "reason"));
        Assert.Equal(FactoryAgentsViewEndpoints.HowToStart(false, hosted: false), Str(json, "howToStart"));
        Assert.Null(Str(json, "title"));
        Assert.Null(Str(json, "subscribeUrl"));
    }

    [Fact]
    public void OffSentence_NamesThePlanOnlyWhenThePlanIsWhy()
    {
        var plan = FactoryAgentsAnswer.Off(FactoryAgentsOffReasons.PlanRequired, EntitlementRegistry.TierRemote, "remote");
        var switchedOff = FactoryAgentsAnswer.Off(FactoryAgentsOffReasons.NotSwitchedOn, null, "off");

        Assert.Equal("Factories come with the Pro and Teams plans, and this account's plan does not include them, so no talk was started.",
            FactoryAgentsGate.OffSentence(plan, "no talk was started"));
        Assert.Equal("Factory agents are switched off for this account, so no talk was started. The administrator switches them on per account.",
            FactoryAgentsGate.OffSentence(switchedOff, "no talk was started"));
    }

    // ================================================================= a session's own key, through the gate (review finding 1)

    /// <summary>
    /// A factory session's request as the hosted Gateway sees it once AuthMiddleware has verified its session key: the
    /// REAL hosted tenant boundary and <see cref="GatewayEndpoints.ResolveReadTenant"/> (the resolver GatewayHost hands
    /// the gate and the route alike), and the memory route mapped INTO the gate as GatewayHost now maps it. The identity
    /// item stands in for the verified key, exactly as FactoryMemoryRouteTests does.
    /// </summary>
    private async Task<Reply> SessionReadsItsFactoryMemory(FactoryAgentsSwitch sw, TenantId account)
    {
        var boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), new DeviceRegistry(), hosted: true);
        Func<HttpContext, TenantId?> resolve = ctx => GatewayEndpoints.ResolveReadTenant(ctx, boundary);
        var app = WebApplication.CreateBuilder().Build();
        FactoryMemoryEndpoints.Map(FactoryAgentsGate.Group(app, sw, resolve), resolve, new FactoryMemoryStore(_db),
            sessionFactoryOf: _ => SessionFactoryLookup.In("mail"), nowUtc: () => Now);

        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == FactoryMemoryEndpoints.Prefix + "/notes"
                         && e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("GET") == true);
        var ctx = new DefaultHttpContext { RequestServices = app.Services };
        ctx.Request.Method = "GET";
        ctx.Request.Path = FactoryMemoryEndpoints.Prefix + "/notes";
        ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(Guid.NewGuid(), account, "director-1");
        var body = new MemoryStream();
        ctx.Response.Body = body;
        await endpoint.RequestDelegate!(ctx);
        return new Reply(ctx.Response.StatusCode, System.Text.Encoding.UTF8.GetString(body.ToArray()), null);
    }

    [Fact]
    public async Task MemoryRoute_ASessionKeyOfASwitchedOnAccount_ResolvesItsAccountThroughTheGate_AndIsServed()
    {
        var sw = HostedSwitch();
        var free = Account("free");
        SwitchOn(sw, free);

        var reply = await SessionReadsItsFactoryMemory(sw, free);

        Assert.True(reply.Status == StatusCodes.Status200OK, $"{reply.Status} '{reply.Body}'");
        Assert.Equal("mail", JsonDocument.Parse(reply.Body).RootElement.GetProperty("factory").GetString());
    }

    [Fact]
    public async Task MemoryRoute_ASessionKeyOfAProAccount_IsServedByThePlan()
        => Assert.Equal(StatusCodes.Status200OK, (await SessionReadsItsFactoryMemory(HostedSwitch(), Account("pro"))).Status);

    [Fact]
    public async Task MemoryRoute_ASessionKeyOfARemoteAccount_Answers404WithNoBody()
    {
        var reply = await SessionReadsItsFactoryMemory(HostedSwitch(), Account("remote"));

        Assert.Equal(StatusCodes.Status404NotFound, reply.Status);
        Assert.Equal("", reply.Body);
    }

    // ================================================================= a Director's own pulls are not logged (finding 2)

    [Fact]
    public async Task Gate_ADirectorsPull_AsksQuietly_WhileARouteAPersonOpensIsLogged()
    {
        var sw = HostedSwitch();
        var pro = Account("pro");
        Func<HttpContext, TenantId?> resolve = ctx => new TenantId(ctx.Request.Headers[TenantHeader].ToString());
        var app = WebApplication.CreateBuilder().Build();
        var gate = FactoryAgentsGate.Group(app, sw, resolve);
        gate.MapGet("/directors/{directorId}/triggers", () => "checks");
        gate.MapGet("/triggers", () => "triggers");

        int SwitchLines() => _log.Count(l => l.StartsWith("[FactoryAgentsSwitch]", StringComparison.Ordinal));
        var before = SwitchLines();
        var pull = await GetAsync(app, "/directors/{directorId}/triggers", pro);
        var afterPull = SwitchLines();
        var owner = await GetAsync(app, "/triggers", pro);

        Assert.Equal((200, 200), (pull.Status, owner.Status));
        Assert.Equal(before, afterPull);
        Assert.Equal(afterPull + 1, SwitchLines());
    }

    // ================================================================= the administrator read reports the grant

    [Fact]
    public async Task AdminRead_ReportsTheGrantThatProducedTheAnswer()
    {
        var sw = HostedSwitch();
        var pro = Account("pro");
        var remote = Account("remote");
        var free = Account("free");
        SwitchOn(sw, free);
        var app = WebApplication.CreateBuilder().Build();

        async Task<JsonElement> Read(TenantId t)
        {
            var ctx = new DefaultHttpContext { RequestServices = app.Services };
            var body = new MemoryStream();
            ctx.Response.Body = body;
            await AdminFactoryAgentsEndpoint.Read(sw, _tenants, t.Value).ExecuteAsync(ctx);
            return JsonDocument.Parse(body.ToArray()).RootElement.Clone();
        }

        var proRead = await Read(pro);
        Assert.True(proRead.GetProperty("enabled").GetBoolean());
        Assert.Equal("plan tier pro", proRead.GetProperty("grant").GetString());
        Assert.Equal("owner switch", (await Read(free)).GetProperty("grant").GetString());
        var remoteRead = await Read(remote);
        Assert.False(remoteRead.GetProperty("enabled").GetBoolean());
        Assert.Equal("plan_required", remoteRead.GetProperty("reason").GetString());
    }

    // ================================================================= the scheduler (the mission's R2)

    private CronJobStore NewJobStore() => new(_db, _harness.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
    private CronRunHistoryStore NewHistory() => new(_db, _harness.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));

    private static CronJobDto Hourly(string name, bool factorySeat) => new()
    {
        Name = name,
        Enabled = true,
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 * * * *",
        TimeZoneId = "UTC",
        Factory = factorySeat ? "mail" : null,
        Seat = factorySeat ? "desk" : null,
        Target = new CronJobTarget { Machine = "NORTH" },
        Action = new CronJobAction { RepoPath = @"D:\work", Seed = "Do the work." },
    };

    private static void MakeDue(CronJobStore store, string id)
        => store.MarkFired(id, Now.AddHours(-1), "started", Now, enabled: true);

    private sealed class SchedulerRig
    {
        public required CronJobStore Store { get; init; }
        public required CronRunHistoryStore History { get; init; }
        public required RecordingStarter Starter { get; init; }
        public required CronEngine Engine { get; init; }
        public required CronJobDto FactoryJob { get; init; }
        public required CronJobDto PlainJob { get; init; }
    }

    /// <summary>One factory seat's schedule and one plain schedule, both due now, on an engine that asks the real switch
    /// about <paramref name="account"/>.</summary>
    private SchedulerRig Scheduler(FactoryAgentsSwitch sw, TenantId account)
    {
        var store = NewJobStore();
        var history = NewHistory();
        var factoryJob = store.Create(Hourly("Mail desk seat", factorySeat: true));
        var plainJob = store.Create(Hourly("Nightly tidy", factorySeat: false));
        MakeDue(store, factoryJob.Id);
        MakeDue(store, plainJob.Id);
        var starter = new RecordingStarter();
        var engine = new CronEngine(store, history, starter, new UnusedWorkListRunner(), new NullCronNotifier(), new FixedClock(Now),
            resolveTenant: () => account, factoryAgents: t => sw.Answer(t));
        return new SchedulerRig { Store = store, History = history, Starter = starter, Engine = engine, FactoryJob = factoryJob, PlainJob = plainJob };
    }

    [Fact]
    public async Task Scheduler_RemoteAccount_FactorySeatScheduleDoesNotStart_PlainScheduleStillDoes()
    {
        var rig = Scheduler(HostedSwitch(), Account("remote"));

        await rig.Engine.EvaluateDueAsync(CancellationToken.None);

        // Only the plain schedule started a session.
        Assert.Equal(new[] { rig.PlainJob.Id }, rig.Starter.Started.Select(j => j.Id).ToArray());
        // The factory seat's run is on record as one that did not run, saying why, and its schedule moved on.
        var run = Assert.Single(rig.History.List(rig.FactoryJob.Id));
        Assert.Equal(CronEngine.FactoryAgentsOffStatus, run.InfraStatus);
        Assert.Equal(CronRunProblems.DidNotRun, run.Problem);
        Assert.Null(run.SessionId);
        Assert.Contains("Pro and Teams", run.ResultReason, StringComparison.Ordinal);
        Assert.Equal(Now.AddHours(1), rig.Store.Get(rig.FactoryJob.Id)!.NextRunUtc);
    }

    [Theory]
    [InlineData("pro")]
    [InlineData("team")]
    public async Task Scheduler_ProOrTeamAccount_BothSchedulesStart(string kind)
    {
        var rig = Scheduler(HostedSwitch(), Account(kind));

        await rig.Engine.EvaluateDueAsync(CancellationToken.None);

        Assert.Equal(new[] { rig.FactoryJob.Id, rig.PlainJob.Id }.OrderBy(i => i), rig.Starter.Started.Select(j => j.Id).OrderBy(i => i));
    }

    [Fact]
    public async Task Scheduler_FreeAccountSwitchedOnByTheOwner_FactorySeatScheduleStarts()
    {
        var sw = HostedSwitch();
        var free = Account("free");
        SwitchOn(sw, free);
        var rig = Scheduler(sw, free);

        await rig.Engine.EvaluateDueAsync(CancellationToken.None);

        Assert.Contains(rig.Starter.Started, j => j.Id == rig.FactoryJob.Id);
    }

    [Fact]
    public async Task Scheduler_RemoteAccount_ACheckedSchedule_SaysThePlan_NotTheSwitch()
    {
        // Review finding 4: one account, one cause, one sentence - the checked schedule names the plan as the seat's does.
        var store = NewJobStore();
        var history = NewHistory();
        var created = store.Create(Hourly("Mail desk check", factorySeat: true));
        using (var ctx = _db.CreateContext(TenantId.Local))
        {
            var row = ctx.CronJobs.Single(j => j.Id == created.Id);
            CronJobStore.ApplyCheck(row, new CronJobDto { CheckPath = "checks/new_mail.py", CheckSecrets = new List<string>() }, 1);
            ctx.SaveChanges();
        }
        MakeDue(store, created.Id);
        var sw = HostedSwitch();
        var remote = Account("remote");
        var engine = new CronEngine(store, history, new RecordingStarter(), new UnusedWorkListRunner(), new NullCronNotifier(),
            new FixedClock(Now), resolveTenant: () => remote, factoryAgents: t => sw.Answer(t));

        await engine.EvaluateDueAsync(CancellationToken.None);

        Assert.Equal(CronEngine.CheckDueSwitchedOffStatus, Assert.Single(history.List(created.Id)).InfraStatus);
        Assert.Equal($"{CronEngine.CheckDueStatus}: {CronEngine.FactoriesNeedAPlanCheckSentence}", store.Get(created.Id)!.LastStatus);
    }

    [Fact]
    public async Task Scheduler_PlanCannotBeRead_SkipsTheFactoryScheduleThisPassAndLeavesItDue_PlainStillStarts()
    {
        var rig = Scheduler(HostedSwitch(), Account("unknown"));

        await rig.Engine.EvaluateDueAsync(CancellationToken.None);

        // Not refused for good: nothing recorded, the schedule not moved on, so the next pass asks again.
        Assert.Equal(new[] { rig.PlainJob.Id }, rig.Starter.Started.Select(j => j.Id).ToArray());
        Assert.Empty(rig.History.List(rig.FactoryJob.Id));
        Assert.Equal(Now, rig.Store.Get(rig.FactoryJob.Id)!.NextRunUtc);
        // A run-now of it is the temporary answer too, never a run.
        Assert.Equal(CronFireOutcome.SkippedFactoriesUnknown, (await rig.Engine.RunNowAsync(rig.FactoryJob.Id, CancellationToken.None)).Outcome);
    }

    // ---- fakes -------------------------------------------------------------------------------

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class RecordingStarter : ICronSessionStarter
    {
        public List<CronJobDto> Started { get; } = new();

        public Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct)
        {
            Started.Add(job);
            return Task.FromResult<(string?, string?, string?)>(($"sid-{Started.Count}", "director-1", null));
        }
    }

    private sealed class UnusedWorkListRunner : ICronWorkListRunner
    {
        public Task<CronWorkListOutcome> TriggerAsync(CronJobDto job, CancellationToken ct) =>
            throw new InvalidOperationException("a seed-job test must not trigger the work-list runner");
    }
}
