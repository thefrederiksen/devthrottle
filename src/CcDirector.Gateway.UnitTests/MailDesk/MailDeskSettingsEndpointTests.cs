using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.History;
using CcDirector.Gateway.MailDesk;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// THE MAIL DESK SETTINGS (Mail Desk phase 3 contract, section 7), booted on an in-memory test server exactly as
/// GatewayHost maps them, over the real stores and a throwaway database: the defaults, what was spent and whether the
/// limit stopped the desk, who may change a setting (a person, or a session in no factory; a factory's own session is
/// refused 403), that every change carries a why and is recorded as a change of kind <c>setting</c>, that NOTHING IS
/// RECORDED ON ANY REFUSAL, the spend block on status, and the 26th route refused when it is added.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskSettingsEndpointTests : IAsyncLifetime
{
    private const string TenantHeader = "X-Test-Account";
    private const string CallerHeader = "X-Test-Caller";
    private const string Person = "person";
    private const string MachineToken = "machine";
    private const string PersonDevice = "owner-browser-1";
    private const string NoFactorySession = "75000000-0000-4000-8000-000000000001";
    private const string NoFactorySessionName = "Mail Desk - Developer - tune the thresholds";
    private const string FactorySession = "75000000-0000-4000-8000-000000000002";
    private const string P = MailDeskGate.Prefix;
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");
    private static readonly TenantId Off = new("acct-off");

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase _db = null!;
    private MailDeskSwitch _switch = null!;
    private MailDeskChannelStore _channels = null!;
    private MailDeskRulesStore _rules = null!;
    private MailDeskDecisionStore _decisions = null!;
    private MailDeskSettingsStore _settings = null!;
    private WebApplication? _app;
    private HttpClient _client = null!;
    private readonly DateTime _now = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        _db = _h.Open();
        _switch = new MailDeskSwitch(new TenantSettingsStore(_db));
        _switch.Set(A, true, "email@devthrottle.com", "the owner's account only", _now);
        _switch.Set(B, true, "email@devthrottle.com", "a second account, for the isolation tests", _now);
        _channels = new MailDeskChannelStore(_db);
        _rules = new MailDeskRulesStore(_db);
        _decisions = new MailDeskDecisionStore(_db);
        _settings = new MailDeskSettingsStore(_db);

        // An EMPTY builder: no configuration files, so no file watchers.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            StandInForAuthentication(ctx);
            await next();
        });
        var parts = new MailDeskEndpoints.Parts(_channels, _rules, _decisions, _settings,
            new MailDeskChannelFold(_ => new List<TriggerEntity>(), _ => "UTC", _decisions, new MailDeskChannelCheckStore(_db)),
            FactoryOf, _ => new List<string>(), () => _now, tenant => MailDeskSpend.Of(tenant, _decisions, _settings),
            new MailDeskRoutedStore(_db));
        MailDeskEndpoints.Map(MailDeskGate.Group(app, _switch, Resolve), Resolve, parts);
        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        _h.Dispose();
    }

    // ---------- who is asking ----------

    private static readonly Func<HttpContext, TenantId?> Resolve = ctx =>
        ctx.Request.Headers.TryGetValue(TenantHeader, out var v) && !string.IsNullOrWhiteSpace(v) ? new TenantId(v.ToString()) : null;

    private static void StandInForAuthentication(HttpContext ctx)
    {
        var caller = ctx.Request.Headers[CallerHeader].ToString();
        var tenant = Resolve(ctx) ?? A;
        if (caller == Person)
        {
            ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
            ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity(PersonDevice, tenant.Value, "browser", "active");
        }
        else if (caller == MachineToken)
            ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "the-machine-token";
        else if (Guid.TryParse(caller, out var session))
            ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(session, tenant, "dir-a");
    }

    private static SessionFactoryLookup FactoryOf(string session) => session switch
    {
        NoFactorySession => SessionFactoryLookup.InNoFactory with { Name = NoFactorySessionName },
        FactorySession => SessionFactoryLookup.In("invoices") with { Name = "Invoices - Clerk - file the bills" },
        _ => SessionFactoryLookup.NotKnown,
    };

    // ---------- calling ----------

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(string method, string path, string caller = Person,
        object? body = null, TenantId? account = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(TenantHeader, (account ?? A).Value);
        request.Headers.Add(CallerHeader, caller);
        if (body is not null)
            request.Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Get(string path, TenantId? account = null) => Send("GET", path, Person, null, account);

    private Task<(HttpStatusCode Status, JsonElement Body)> Set(string name, object value, string? why = "tuned after a week of observe mode",
        string caller = Person, TenantId? account = null)
        => Send("POST", P + "/settings", caller, new { name, value, why }, account);

    /// <summary>Every Mail Desk row and Mail Desk setting of an account, as one text: a refusal must leave it as it was.</summary>
    private string Recorded(TenantId? account = null)
    {
        using var ctx = _db.CreateContext(account ?? A);
        var keys = new List<string> { TenantSettingKeys.MailDeskRouteThreshold, TenantSettingKeys.MailDeskSpamThreshold, TenantSettingKeys.MailDeskSpendLimitUsd };
        var settings = ctx.TenantSettings.AsNoTracking().Where(s => keys.Contains(s.Key)).OrderBy(s => s.Key).ToList().Select(s => $"{s.Key}={s.Value}");
        var versions = ctx.MailDeskRules.AsNoTracking().OrderBy(r => r.Version).ToList().Select(r => $"{r.Version}|{r.Document}");
        var changes = ctx.MailDeskListChanges.AsNoTracking().OrderBy(c => c.Id).ToList().Select(c => $"{c.Id}|{c.Kind}|{c.Subject}|{c.Detail}");
        return string.Join("\n", settings.Concat(versions).Concat(changes));
    }

    private void AssertRefusedAndNothingRecorded((HttpStatusCode Status, JsonElement Body) answer, HttpStatusCode status, string error, string before)
    {
        Assert.Equal(status, answer.Status);
        Assert.Equal(error, answer.Body.GetProperty("error").GetString());
        Assert.Equal(before, Recorded());
    }

    private void Spent(params long?[] costsMicros)
    {
        var channel = _channels.Add(A, "email@devthrottle.com", "outlook", new[] { "devlinux" }, MailDeskWho.Person("seed"), "seeded", _now).Channel!;
        var i = 0;
        foreach (var cost in costsMicros)
            _decisions.Insert(A, new MailDeskDecisionInput(channel.Id, $"key-{++i}", _now, "someone@example.net", "A question",
                MailDeskWords.Layers.RouteModel, MailDeskWords.Decisions.MailDeskAgent, null, null, "route-model: unsure", null, cost,
                MailDeskWords.Modes.Observe, null, null, _now));
    }

    private static (decimal Route, decimal Spam, decimal Limit, decimal Spent, bool Stopped) Values(JsonElement s)
        => (s.GetProperty("routeThreshold").GetDecimal(), s.GetProperty("spamThreshold").GetDecimal(), s.GetProperty("spendLimitUsd").GetDecimal(),
            s.GetProperty("spentUsd").GetDecimal(), s.GetProperty("stopped").GetBoolean());

    // ======================================================================================================
    // Reading
    // ======================================================================================================

    [Fact]
    public async Task GetSettings_NothingSet_AnswersTheDefaultsAndNothingSpent()
    {
        // Act
        var (status, body) = await Get(P + "/settings");

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal((0.90m, 0.95m, 5.00m, 0m, false), Values(body));
        Assert.Equal("$0.00 spent of the $5.00 spend limit", body.GetProperty("label").GetString());
    }

    [Fact]
    public async Task GetSettings_DecisionsWithCosts_SpentIsTheSumOfEveryRowsCost()
    {
        // Arrange: two rows that cost, one that asked nothing.
        Spent(100, null, 250);

        // Act
        var (_, body) = await Get(P + "/settings");

        // Assert
        Assert.Equal(0.00035m, body.GetProperty("spentUsd").GetDecimal());
        Assert.False(body.GetProperty("stopped").GetBoolean());
        Assert.Equal("$0.00035 spent of the $5.00 spend limit", body.GetProperty("label").GetString());
    }

    [Fact]
    public async Task GetSettings_SpentOverTheLimit_IsStoppedWithTheSpendSentence()
    {
        // Arrange: the spend is over the limit and the screening recorded that the limit stopped a call.
        Spent(5_000_300);
        _settings.MarkSpendStopped(A, _now);

        // Act
        var (_, body) = await Get(P + "/settings");

        // Assert
        Assert.True(body.GetProperty("stopped").GetBoolean());
        Assert.Equal("Mail Desk stopped asking the model: the spend limit of $5.00 is reached ($5.0003 spent). " +
                     "Raise it with maildesk set spend-limit <dollars> --why \"<reason>\".", body.GetProperty("label").GetString());
    }

    // Phase 3, rule 7 (task F): GET /settings reads "stopped" and its sentence from the SAME fold as the spend problem
    // (MailDeskSpend), so the two can never disagree - here the limit is reached but no call was ever stopped, so
    // neither the settings nor the problem list says the Mail Desk stopped.
    [Fact]
    public async Task GetSettings_SpentOverTheLimitButNoCallStopped_IsNotStoppedAndHasNoSpendProblem()
    {
        // Arrange
        Spent(5_000_300);

        // Act
        var (_, body) = await Get(P + "/settings");

        // Assert
        Assert.False(body.GetProperty("stopped").GetBoolean());
        Assert.Null(MailDeskProblems.SpendProblem(MailDeskSpend.Of(A, _decisions, _settings), TimeZoneInfo.Utc, _now));
    }

    [Fact]
    public async Task GetSettings_Stopped_LabelIsTheSpendProblemsSentence()
    {
        // Arrange
        Spent(5_000_300);
        _settings.MarkSpendStopped(A, _now);

        // Act
        var (_, body) = await Get(P + "/settings");
        var problem = MailDeskProblems.SpendProblem(MailDeskSpend.Of(A, _decisions, _settings), TimeZoneInfo.Utc, _now);

        // Assert
        Assert.NotNull(problem);
        Assert.Equal(problem!.Sentence, body.GetProperty("label").GetString());
    }

    [Fact]
    public async Task SetSpendLimit_RaisedAboveWhatWasSpent_ClearsTheStopSoTheNextStopIsRecordedAfresh()
    {
        // Arrange
        Spent(5_000_300);
        _settings.MarkSpendStopped(A, _now);

        // Act
        await Set(MailDeskSettingNames.SpendLimit, 10);

        // Assert
        Assert.Null(_settings.SpendStoppedUtc(A));
        Assert.False((await Get(P + "/settings")).Body.GetProperty("stopped").GetBoolean());
        Assert.True(_settings.MarkSpendStopped(A, _now.AddMinutes(5)));
        Assert.Equal(_now.AddMinutes(5), _settings.SpendStoppedUtc(A));
    }

    // Review of 4243, finding 4: the spend problem is open "until the limit is raised above what was spent" (section 7).
    // A change that leaves the limit at or under the spend - set again, or lowered - keeps the stop and the problem.
    [Theory]
    [InlineData(5)]
    [InlineData(4)]
    [InlineData(5.0003)]
    public async Task SetSpendLimit_AtOrUnderWhatWasSpent_KeepsTheStopAndTheSpendProblem(double limit)
    {
        // Arrange
        Spent(5_000_300);
        _settings.MarkSpendStopped(A, _now);

        // Act
        await Set(MailDeskSettingNames.SpendLimit, limit);

        // Assert
        Assert.Equal(_now, _settings.SpendStoppedUtc(A));
        Assert.True((await Get(P + "/settings")).Body.GetProperty("stopped").GetBoolean());
        Assert.NotNull(MailDeskProblems.SpendProblem(MailDeskSpend.Of(A, _decisions, _settings), TimeZoneInfo.Utc, _now));
    }

    [Fact]
    public void MarkSpendStopped_Twice_KeepsTheFirstStop()
    {
        // Act
        var first = _settings.MarkSpendStopped(A, _now);
        var second = _settings.MarkSpendStopped(A, _now.AddMinutes(5));

        // Assert
        Assert.True(first);
        Assert.False(second);
        Assert.Equal(_now, _settings.SpendStoppedUtc(A));
    }

    [Fact]
    public async Task GetSettings_SpentExactlyTheLimit_IsStopped()
    {
        // Arrange
        Spent(5_000_000);
        _settings.MarkSpendStopped(A, _now);

        // Act
        var (_, body) = await Get(P + "/settings");

        // Assert
        Assert.True(body.GetProperty("stopped").GetBoolean());
    }

    [Fact]
    public async Task SetSpendLimit_RaisedAboveWhatWasSpent_IsNoLongerStopped()
    {
        // Arrange
        Spent(5_000_300);

        // Act
        var (status, body) = await Set(MailDeskSettingNames.SpendLimit, 10);

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("settings").GetProperty("stopped").GetBoolean());
        Assert.Equal((0.90m, 0.95m, 10m, 5.0003m, false), Values((await Get(P + "/settings")).Body));
    }

    [Fact]
    public async Task Status_CarriesTheSameSpendBlockAsSettings()
    {
        // Arrange
        Spent(1_200);
        await Set(MailDeskSettingNames.SpamThreshold, 0.97);

        // Act
        var (_, status) = await Get(P + "/status");
        var (_, settings) = await Get(P + "/settings");

        // Assert
        Assert.Equal(settings.GetRawText(), status.GetProperty("spend").GetRawText());
        Assert.Equal(0.97m, status.GetProperty("spend").GetProperty("spamThreshold").GetDecimal());
        Assert.True(status.TryGetProperty("today", out _));
    }

    [Fact]
    public async Task GetSettings_SwitchOff_Answers404WithNoBody()
    {
        // Act
        var (status, body) = await Get(P + "/settings", Off);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(JsonValueKind.Undefined, body.ValueKind);
    }

    // ======================================================================================================
    // Changing, and the record of it
    // ======================================================================================================

    [Fact]
    public async Task SetRouteThreshold_Person_ChangesItAndRecordsASettingChangeWithFromToWhoAndWhy()
    {
        // Act
        var (status, body) = await Set(MailDeskSettingNames.RouteThreshold, 0.85);

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0.85m, body.GetProperty("settings").GetProperty("routeThreshold").GetDecimal());
        var change = body.GetProperty("change");
        Assert.Equal((MailDeskWords.ChangeKinds.Setting, MailDeskWords.ChangeActions.Set, MailDeskSettingNames.RouteThreshold, "tuned after a week of observe mode"),
            (change.GetProperty("kind").GetString(), change.GetProperty("action").GetString(), change.GetProperty("subject").GetString(), change.GetProperty("why").GetString()));
        Assert.Equal((0.90m, 0.85m), (change.GetProperty("detail").GetProperty("from").GetDecimal(), change.GetProperty("detail").GetProperty("to").GetDecimal()));
        Assert.Equal((MailDeskWords.WhoKinds.Person, $"device browser {PersonDevice}"),
            (change.GetProperty("who").GetProperty("kind").GetString(), change.GetProperty("who").GetProperty("id").GetString()));
        Assert.Equal(0.85m, _settings.Read(A).RouteThreshold);
        using var ctx = _db.CreateContext(A);
        var stored = Assert.Single(ctx.MailDeskListChanges.AsNoTracking().ToList());
        Assert.Equal((MailDeskWords.ChangeKinds.Setting, MailDeskSettingNames.RouteThreshold, """{"from":0.90,"to":0.85}"""),
            (stored.Kind, stored.Subject, stored.Detail));
    }

    [Fact]
    public async Task SetSetting_SecondChange_RecordsTheFirstChangesValueAsFrom()
    {
        // Arrange
        await Set(MailDeskSettingNames.SpamThreshold, 0.97);

        // Act
        var (_, body) = await Set(MailDeskSettingNames.SpamThreshold, 0.99);

        // Assert
        var detail = body.GetProperty("change").GetProperty("detail");
        Assert.Equal((0.97m, 0.99m), (detail.GetProperty("from").GetDecimal(), detail.GetProperty("to").GetDecimal()));
    }

    [Fact]
    public async Task SetSetting_SessionInNoFactory_IsAllowedAndRecordsTheSession()
    {
        // Act
        var (status, body) = await Set(MailDeskSettingNames.SpendLimit, 7.5, caller: NoFactorySession);

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        var who = body.GetProperty("change").GetProperty("who");
        Assert.Equal((MailDeskWords.WhoKinds.Session, NoFactorySession, NoFactorySessionName),
            (who.GetProperty("kind").GetString(), who.GetProperty("id").GetString(), who.GetProperty("name").GetString()));
        Assert.Equal(7.5m, _settings.Read(A).SpendLimitUsd);
    }

    [Fact]
    public async Task History_KindSetting_ListsTheSettingChanges()
    {
        // Arrange
        await Set(MailDeskSettingNames.RouteThreshold, 0.8);
        await Set(MailDeskSettingNames.SpendLimit, 2);

        // Act
        var (status, body) = await Get(P + "/rules/history?kind=setting");

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(new[] { MailDeskSettingNames.SpendLimit, MailDeskSettingNames.RouteThreshold },
            body.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("subject").GetString()));
    }

    [Theory]
    [InlineData(MailDeskSettingNames.RouteThreshold, 0.5)]
    [InlineData(MailDeskSettingNames.RouteThreshold, 1.0)]
    [InlineData(MailDeskSettingNames.SpamThreshold, 0.5)]
    [InlineData(MailDeskSettingNames.SpamThreshold, 1.0)]
    [InlineData(MailDeskSettingNames.SpendLimit, 0)]
    [InlineData(MailDeskSettingNames.SpendLimit, 100)]
    public async Task SetSetting_AtEachEndOfItsRange_IsAccepted(string name, double value)
    {
        // Act
        var (status, _) = await Set(name, value);

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal((decimal)value, _settings.Read(A).Get(name));
    }

    [Fact]
    public async Task SetSetting_OneAccount_LeavesAnotherAccountsSettingsAtTheirDefaults()
    {
        // Act
        await Set(MailDeskSettingNames.RouteThreshold, 0.7, account: B);

        // Assert
        Assert.Equal(MailDeskSettings.Defaults, _settings.Read(A));
        Assert.Equal(0.7m, _settings.Read(B).RouteThreshold);
    }

    // ======================================================================================================
    // Every refusal records nothing
    // ======================================================================================================

    [Fact]
    public async Task SetSetting_FactorysOwnSession_IsRefused403AndRecordsNothing()
    {
        // Arrange
        var before = Recorded();

        // Act
        var answer = await Set(MailDeskSettingNames.SpendLimit, 50, caller: FactorySession);

        // Assert
        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.Forbidden, "factory_session_refused", before);
        Assert.Contains("thresholds or its spend limit", answer.Body.GetProperty("detail").GetString());
        Assert.Contains("'invoices'", answer.Body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task SetSetting_MachineToken_IsRefused403AndRecordsNothing()
    {
        // Arrange
        var before = Recorded();

        // Act
        var answer = await Set(MailDeskSettingNames.SpendLimit, 50, caller: MachineToken);

        // Assert
        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.Forbidden, "not_an_author", before);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task SetSetting_NoWhy_IsRefused400AndRecordsNothing(string? why)
    {
        // Arrange
        var before = Recorded();

        // Act
        var answer = await Set(MailDeskSettingNames.RouteThreshold, 0.8, why);

        // Assert
        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.BadRequest, "why_required", before);
    }

    [Theory]
    [InlineData("routeThreshold")]
    [InlineData("threshold")]
    [InlineData("")]
    public async Task SetSetting_UnknownName_IsRefused400NamingTheSettingsAndRecordsNothing(string name)
    {
        // Arrange
        var before = Recorded();

        // Act
        var answer = await Set(name, 0.8);

        // Assert
        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.BadRequest, "unknown_setting", before);
        Assert.Contains("'route-threshold', 'spam-threshold' or 'spend-limit'", answer.Body.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData(MailDeskSettingNames.RouteThreshold, 0.49)]
    [InlineData(MailDeskSettingNames.RouteThreshold, 1.01)]
    [InlineData(MailDeskSettingNames.SpamThreshold, 0)]
    [InlineData(MailDeskSettingNames.SpendLimit, -0.01)]
    [InlineData(MailDeskSettingNames.SpendLimit, 100.01)]
    public async Task SetSetting_OutOfRange_IsRefused400AndRecordsNothing(string name, double value)
    {
        // Arrange
        var before = Recorded();

        // Act
        var answer = await Set(name, value);

        // Assert
        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.BadRequest, "bad_value", before);
        Assert.Contains("is outside that", answer.Body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task SetSetting_ValueNotANumber_IsRefused400AndRecordsNothing()
    {
        // Arrange
        var before = Recorded();

        // Act
        var answer = await Set(MailDeskSettingNames.RouteThreshold, "0.8");

        // Assert
        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.BadRequest, "bad_value", before);
    }

    [Fact]
    public async Task SetSetting_SwitchOff_Answers404AndRecordsNothing()
    {
        // Act
        var (status, _) = await Set(MailDeskSettingNames.RouteThreshold, 0.8, account: Off);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(MailDeskSettings.Defaults, _settings.Read(Off));
    }

    // ======================================================================================================
    // The store, and the route cap through the real route
    // ======================================================================================================

    [Fact]
    public void Read_StoredValueUnreadable_Throws()
    {
        // Arrange
        new TenantSettingsStore(_db).Set(A, TenantSettingKeys.MailDeskRouteThreshold, "ninety percent", _now);

        // Act + Assert
        var ex = Assert.Throws<InvalidOperationException>(() => _settings.Read(A));
        Assert.Contains("route-threshold", ex.Message);
    }

    [Fact]
    public async Task AddRoute_TheTwentySixth_IsRefused400AndRecordsNothing()
    {
        // Arrange
        for (var i = 1; i <= 25; i++)
        {
            var (added, _) = await Send("POST", P + "/rules/route/add", Person,
                new { factory = "books", seat = $"clerk-{i}", handles = $"bills of kind {i}", why = "a route for the model" });
            Assert.Equal(HttpStatusCode.OK, added);
        }
        var before = Recorded();

        // Act
        var answer = await Send("POST", P + "/rules/route/add", Person,
            new { factory = "books", seat = "clerk-26", handles = "one too many", why = "a route for the model" });

        // Assert
        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.BadRequest, "invalid_rules", before);
        Assert.Contains("at most 25", answer.Body.GetProperty("detail").GetString());
    }
}
