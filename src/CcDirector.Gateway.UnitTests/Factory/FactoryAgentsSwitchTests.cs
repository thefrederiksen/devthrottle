using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Api;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// The Factory Agents switch PER ACCOUNT. The owner's words decide the hosted case: "Build that switch now and turn
/// it on for your account only". So: off for every account by default, on only for an account an administrator
/// switched on, and every factory route - not just the Cockpit's switch question - refuses for an account that is off.
/// </summary>
[Collection(AdminServiceTokenCollection.Name)]
public sealed class FactoryAgentsSwitchTests : IDisposable
{
    private const string Token = "test-admin-service-token-fa01";
    private const string TenantHeader = "X-Test-Account";

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();
    private readonly string? _prior = Environment.GetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar);
    private readonly DateTime _now = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    public FactoryAgentsSwitchTests()
        => Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, Token);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, _prior);
        _h.Dispose();
    }

    private FactoryAgentsSwitch Switch(bool machineWide = false) => new(machineWide, new TenantSettingsStore(Db));

    private static int StatusOf(IResult result)
        => result.GetType().GetProperty("StatusCode")?.GetValue(result) as int? ?? StatusCodes.Status200OK;

    private static HttpContext Authorized()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = $"Bearer {Token}";
        return ctx;
    }

    // ---- the switch -----------------------------------------------------------------------------------------

    [Fact]
    public void IsOn_NothingRecordedAndMachineOff_IsOffForEveryAccount()
    {
        var sw = Switch();
        Assert.False(sw.IsOn(new TenantId("acct-a")));
        Assert.False(sw.IsOn(TenantId.Local));
        Assert.Null(sw.Decision(new TenantId("acct-a")));
    }

    [Fact]
    public void IsOn_OneAccountSwitchedOn_IsOnForThatAccountOnly()
    {
        var sw = Switch();
        sw.Set(new TenantId("acct-a"), true, "owner@example.com", "the owner asked for his account only", _now);

        Assert.True(sw.IsOn(new TenantId("acct-a")));
        Assert.False(sw.IsOn(new TenantId("acct-b")));
    }

    [Fact]
    public void IsOn_MachineSwitchOn_IsOnForEveryAccountEvenOneSwitchedOff()
    {
        var sw = Switch(machineWide: true);
        sw.Set(new TenantId("acct-b"), false, "owner@example.com", "not this one", _now);

        Assert.True(sw.IsOn(new TenantId("acct-a")));
        Assert.True(sw.IsOn(new TenantId("acct-b")));
    }

    [Fact]
    public void Set_SwitchedOnThenOff_IsOffAndRecordsTheLatestDecision()
    {
        var sw = Switch();
        var a = new TenantId("acct-a");
        sw.Set(a, true, "first@example.com", "on", _now);
        sw.Set(a, false, "second@example.com", "off again", _now.AddHours(1));

        Assert.False(sw.IsOn(a));
        var d = sw.Decision(a)!;
        Assert.False(d.Enabled);
        Assert.Equal("second@example.com", d.Actor);
        Assert.Equal("off again", d.Reason);
        Assert.Equal(_now.AddHours(1), d.RecordedAtUtc);
    }

    [Fact]
    public void Set_NoActorOrNoReason_Throws()
    {
        var sw = Switch();
        Assert.Throws<ArgumentException>(() => sw.Set(new TenantId("acct-a"), true, " ", "why", _now));
        Assert.Throws<ArgumentException>(() => sw.Set(new TenantId("acct-a"), true, "who", "", _now));
        Assert.False(sw.IsOn(new TenantId("acct-a")));
    }

    [Fact]
    public void Decision_StoredValueIsNotADecision_ThrowsRatherThanAnsweringOff()
    {
        var store = new TenantSettingsStore(Db);
        store.Set(new TenantId("acct-a"), TenantSettingKeys.FactoryAgentsSwitch, "null", _now);
        var sw = new FactoryAgentsSwitch(false, store);

        Assert.Throws<InvalidOperationException>(() => sw.IsOn(new TenantId("acct-a")));
    }

    [Fact]
    public void TenantSettingKeys_FactoryAgentsSwitch_IsAStorableKey()
        => Assert.Contains(TenantSettingKeys.FactoryAgentsSwitch, TenantSettingKeys.All);

    // ---- every factory route, account A on and account B off -----------------------------------------------

    /// <summary>
    /// Maps the REAL factory routes - the activity record, the triggers (both halves) and the owner's pages - into
    /// the gate exactly as GatewayHost does, plus the switch question, with the calling account named by a header.
    /// </summary>
    private (WebApplication App, FactoryAgentsSwitch Switch) FactoryApp()
    {
        var sw = Switch();
        var record = new FactoryActivityRecord(Db);
        var triggers = new TriggerService(new TriggerStore(Db), record,
            (_, _, _) => Task.FromResult(TriggerStartAttempt.Failed("no machine in this test")),
            findSession: (_, _) => null, findSessionByName: (_, _) => null,
            timeZone: _ => TimeZoneInfo.Utc, nowUtc: () => _now, startLifetime: CancellationToken.None);
        var sources = new FactoryAgentsSources(
            Query: (tenant, q) => record.Query(tenant, q.Factory, q.Agent, q.Outcome, q.FromUtc, q.ToUtc,
                q.OldestFirst, q.Offset, q.Limit),
            Append: (tenant, request, actor) => record.Append(tenant, request, actor),
            Triggers: tenant => FactoryTriggerSource.Facts(triggers, tenant),
            SetTriggerPaused: (tenant, id, paused, by, ct) => FactoryTriggerSource.SetPausedAsync(triggers, tenant, id, paused, by, ct),
            LiveSessionIds: _ => new HashSet<string>(),
            TimeZone: _ => TimeZoneInfo.Utc,
            NowUtc: () => _now,
            Reports: new FactoryReportStore(new TenantSettingsStore(Db)),
            Maps: new FactoryMapStore(new TenantSettingsStore(Db)));

        Func<HttpContext, TenantId?> resolve = ctx =>
            ctx.Request.Headers.TryGetValue(TenantHeader, out var v) && !string.IsNullOrWhiteSpace(v)
                ? new TenantId(v.ToString())
                : null;

        var app = WebApplication.CreateBuilder().Build();
        FactoryAgentsViewEndpoints.MapSwitch(app, sw, resolve);
        var gate = FactoryAgentsGate.Group(app, sw, resolve);
        FactoryActivityEndpoints.Map(gate, record);
        FactoryMapEndpoints.Map(gate, new FactoryMapStore(new TenantSettingsStore(Db)), resolve);
        FactoryAgentsViewEndpoints.Map(gate, resolve, sources);
        TriggerEndpoints.Map(FactoryAgentsGate.Group(app, sw, resolve), resolve, triggers,
            directorMachine: (_, _) => "NORTH", nowUtc: () => _now);
        return (app, sw);
    }

    private static IReadOnlyList<RouteEndpoint> Endpoints(WebApplication app)
        => ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();

    private static async Task<(int Status, string Body)> Invoke(WebApplication app, RouteEndpoint endpoint, string? account)
    {
        var ctx = new DefaultHttpContext { RequestServices = app.Services };
        ctx.Request.Method = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET";
        ctx.Request.Path = endpoint.RoutePattern.RawText;
        if (account is not null) ctx.Request.Headers[TenantHeader] = account;
        foreach (var p in endpoint.RoutePattern.Parameters)
            ctx.Request.RouteValues[p.Name] = p.Name == "id" ? Guid.NewGuid().ToString("D") : "x";
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream("{}"u8.ToArray());
        var response = new MemoryStream();
        ctx.Response.Body = response;
        await endpoint.RequestDelegate!(ctx);
        return (ctx.Response.StatusCode, System.Text.Encoding.UTF8.GetString(response.ToArray()));
    }

    private static string Describe(RouteEndpoint e)
        => $"{e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.FirstOrDefault()} {e.RoutePattern.RawText}";

    [Fact]
    public async Task EveryFactoryRoute_AccountOff_Answers404AsIfUnmapped()
    {
        var (app, sw) = FactoryApp();
        sw.Set(new TenantId("acct-a"), true, "owner@example.com", "his account only", _now);

        var gated = Endpoints(app).Where(e => !e.RoutePattern.RawText!.EndsWith("/switch", StringComparison.Ordinal)).ToList();
        // The routes this must cover, named, so a mapping that silently lost them cannot pass on an empty list.
        var names = gated.Select(Describe).ToList();
        Assert.Contains("POST /gateway/factory/activity", names);
        Assert.Contains("GET /gateway/factory/activity", names);
        Assert.Contains("GET /triggers", names);
        Assert.Contains("POST /triggers", names);
        Assert.Contains("GET /directors/{directorId}/triggers", names);
        Assert.Contains("POST /directors/{directorId}/triggers/{id}/checks", names);
        Assert.Contains("GET /gateway/factory-agents/factories", names);
        Assert.Contains("GET /gateway/factory-agents/activity", names);
        Assert.Contains("GET /gateway/factory-agents/activity.csv", names);
        Assert.Contains("GET /gateway/factory-agents/waiting", names);
        Assert.Contains("POST /gateway/factory-agents/reports", names);
        Assert.Contains("PUT /gateway/factory/map", names);
        Assert.Contains("GET /gateway/factory-agents/factories/{factory}/map", names);
        Assert.True(gated.Count >= 20, $"expected every factory route, found {gated.Count}: {string.Join(", ", names)}");

        foreach (var endpoint in gated)
        {
            var off = await Invoke(app, endpoint, "acct-b");
            Assert.True(off.Status == StatusCodes.Status404NotFound && off.Body.Length == 0,
                $"{Describe(endpoint)} answered {off.Status} '{off.Body}' for an account that is off");
            var unbound = await Invoke(app, endpoint, null);
            Assert.True(unbound.Status == StatusCodes.Status404NotFound,
                $"{Describe(endpoint)} answered {unbound.Status} for a request bound to no account");
        }
    }

    [Fact]
    public async Task FactoryRoutes_AccountOn_AreServed()
    {
        var (app, sw) = FactoryApp();
        sw.Set(new TenantId("acct-a"), true, "owner@example.com", "his account only", _now);
        var byName = Endpoints(app).ToDictionary(Describe);

        var activity = await Invoke(app, byName["GET /gateway/factory-agents/activity"], "acct-a");
        Assert.Equal(StatusCodes.Status200OK, activity.Status);
        var list = await Invoke(app, byName["GET /triggers"], "acct-a");
        Assert.Equal(StatusCodes.Status200OK, list.Status);
    }

    [Fact]
    public async Task SwitchQuestion_AnswersForTheCallingAccount()
    {
        var (app, sw) = FactoryApp();
        sw.Set(new TenantId("acct-a"), true, "owner@example.com", "his account only", _now);
        var question = Endpoints(app).Single(e => e.RoutePattern.RawText == FactoryAgentsViewEndpoints.Prefix + "/switch");

        static bool Enabled(string body) => JsonDocument.Parse(body).RootElement.GetProperty("enabled").GetBoolean();
        Assert.True(Enabled((await Invoke(app, question, "acct-a")).Body));
        Assert.False(Enabled((await Invoke(app, question, "acct-b")).Body));
        Assert.False(Enabled((await Invoke(app, question, null)).Body));
    }

    // ---- the administrator route ---------------------------------------------------------------------------

    private static AdminFactoryAgentsEndpoint.SetRequest Req(string? account, bool? enabled = true,
        string? actor = "owner@example.com", string? reason = "the owner asked for his account only")
        => new(account, enabled, actor, reason);

    [Fact]
    public void AdminSet_NoServiceToken_IsRefusedAndRecordsNothing()
    {
        var tenants = new TenantRegistry(Db);
        var a = tenants.MintOrLookupBySubject("subject-a", "a@example.com");
        var sw = Switch();

        var result = AdminFactoryAgentsEndpoint.Handle(new DefaultHttpContext(), Req(a.Value), sw, tenants, _now);

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(result));
        Assert.False(sw.IsOn(a));
    }

    [Theory]
    [InlineData(null, true, "who", "why")]
    [InlineData("  ", true, "who", "why")]
    [InlineData("ACCOUNT", null, "who", "why")]
    [InlineData("ACCOUNT", true, " ", "why")]
    [InlineData("ACCOUNT", true, "who", "")]
    public void AdminSet_MissingField_IsRefused(string? account, bool? enabled, string? actor, string? reason)
    {
        var tenants = new TenantRegistry(Db);
        var a = tenants.MintOrLookupBySubject("subject-a", "a@example.com");
        var sw = Switch();

        var result = AdminFactoryAgentsEndpoint.Handle(Authorized(),
            Req(account == "ACCOUNT" ? a.Value : account, enabled, actor, reason), sw, tenants, _now);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.False(sw.IsOn(a));
    }

    [Fact]
    public void AdminSet_AccountTheGatewayDoesNotHave_IsRefused()
    {
        var tenants = new TenantRegistry(Db);
        var result = AdminFactoryAgentsEndpoint.Handle(Authorized(), Req("acct-nobody"), Switch(), tenants, _now);
        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
    }

    [Fact]
    public void AdminSet_KnownAccount_SwitchesThatAccountOnlyAndReadsBack()
    {
        var tenants = new TenantRegistry(Db);
        var a = tenants.MintOrLookupBySubject("subject-a", "a@example.com");
        var b = tenants.MintOrLookupBySubject("subject-b", "b@example.com");
        var sw = Switch();

        var set = AdminFactoryAgentsEndpoint.Handle(Authorized(), Req(a.Value), sw, tenants, _now);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(set));
        Assert.True(sw.IsOn(a));
        Assert.False(sw.IsOn(b));
        var d = sw.Decision(a)!;
        Assert.Equal("owner@example.com", d.Actor);
        Assert.Equal("the owner asked for his account only", d.Reason);
        Assert.Equal(_now, d.RecordedAtUtc);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(AdminFactoryAgentsEndpoint.Read(sw, tenants, a.Value)));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(AdminFactoryAgentsEndpoint.Read(sw, tenants, "acct-nobody")));
        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(AdminFactoryAgentsEndpoint.Read(sw, tenants, "")));
    }

    // The website's admin API and the Delivery Lead's command hold no device key, so the route carries its own gate
    // and must be on the device-key gate's exempt list, like the turn-log and account-lookup routes beside it.
    [Fact]
    public void AdminPath_IsExemptFromTheDeviceKeyGateLikeTheOtherAdminRoutes()
    {
        var field = typeof(CcDirector.Gateway.Util.AuthMiddleware).GetField("PublicPaths",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var paths = (HashSet<string>)field.GetValue(null)!;
        Assert.Contains(AdminTurnLogEndpoint.Path, paths);
        Assert.Contains(AdminFactoryAgentsEndpoint.Path, paths);
    }
}
