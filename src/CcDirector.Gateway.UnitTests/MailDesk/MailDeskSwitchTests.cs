using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.MailDesk;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Api;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Factory.Definition;
using CcDirector.Gateway.Tests.Factory.Triggers;
using CcDirector.TestInfrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// The Mail Desk switch PER ACCOUNT (the Mail Desk mission, issue #4213, section 5.10; phase 1 contract, section 1).
/// Off for every account by default, on only for an account an administrator switched on, with no machine switch at
/// all. Every Mail Desk route answers 404 with no body for an account that is off - even one whose factory agents are
/// on - and a Mail Desk screening check reported for such an account is refused with nothing recorded.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class MailDeskSwitchTests : IDisposable
{
    private const string Token = "test-admin-service-token-md01";
    private const string TenantHeader = "X-Test-Account";
    private const string Director = "dir-north-1";
    private const string Machine = "SOREN_NORTH";

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();
    private readonly string? _prior = Environment.GetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar);
    private readonly DateTime _now = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
    private readonly List<NewSessionRequest> _starts = new();
    private int _nextSession;

    public MailDeskSwitchTests()
        => Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, Token);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, _prior);
        _h.Dispose();
    }

    private MailDeskSwitch Switch() => new(new TenantSettingsStore(Db));

    private static int StatusOf(IResult result)
        => result.GetType().GetProperty("StatusCode")?.GetValue(result) as int? ?? StatusCodes.Status200OK;

    private static HttpContext Authorized()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = $"Bearer {Token}";
        return ctx;
    }

    private static readonly Func<HttpContext, TenantId?> Resolve = ctx =>
        ctx.Request.Headers.TryGetValue(TenantHeader, out var v) && !string.IsNullOrWhiteSpace(v)
            ? new TenantId(v.ToString())
            : null;

    // ---- the switch -----------------------------------------------------------------------------------------

    [Fact]
    public void IsOn_NothingRecorded_IsOffForEveryAccount()
    {
        // Arrange
        var sw = Switch();

        // Act + Assert
        Assert.False(sw.IsOn(new TenantId("acct-a")));
        Assert.False(sw.IsOn(TenantId.Local));
        Assert.Null(sw.Decision(new TenantId("acct-a")));
    }

    [Fact]
    public void IsOn_OneAccountSwitchedOn_IsOnForThatAccountOnly()
    {
        // Arrange
        var sw = Switch();

        // Act
        sw.Set(new TenantId("acct-a"), true, "email@devthrottle.com", "the owner's account only", _now);

        // Assert
        Assert.True(sw.IsOn(new TenantId("acct-a")));
        Assert.False(sw.IsOn(new TenantId("acct-b")));
    }

    [Fact]
    public void Set_SwitchedOnThenOff_IsOffAndRecordsTheLatestDecision()
    {
        // Arrange
        var sw = Switch();
        var a = new TenantId("acct-a");

        // Act
        sw.Set(a, true, "first@example.com", "on", _now);
        sw.Set(a, false, "second@example.com", "off again", _now.AddHours(1));

        // Assert
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
        // Arrange
        var sw = Switch();

        // Act + Assert
        Assert.Throws<ArgumentException>(() => sw.Set(new TenantId("acct-a"), true, " ", "why", _now));
        Assert.Throws<ArgumentException>(() => sw.Set(new TenantId("acct-a"), true, "who", "", _now));
        Assert.False(sw.IsOn(new TenantId("acct-a")));
    }

    [Fact]
    public void Decision_StoredValueIsNotADecision_ThrowsRatherThanAnsweringOff()
    {
        // Arrange
        var store = new TenantSettingsStore(Db);
        store.Set(new TenantId("acct-a"), TenantSettingKeys.MailDeskSwitch, "null", _now);
        var sw = new MailDeskSwitch(store);

        // Act + Assert
        Assert.Throws<InvalidOperationException>(() => sw.IsOn(new TenantId("acct-a")));
    }

    [Fact]
    public void TenantSettingKeys_MailDeskSwitch_IsAStorableKey()
        => Assert.Contains(TenantSettingKeys.MailDeskSwitch, TenantSettingKeys.All);

    // The account's own settings routes are each written for one named setting; none is a general key writer. So the
    // proof that an account cannot switch itself on is in two parts: nothing in the Gateway names the key except its
    // declaration and MailDeskSwitch, and nothing calls MailDeskSwitch.Set except the administrator route.
    [Fact]
    public void MailDeskSwitchKey_IsNamedOnlyByItsDeclarationAndTheSwitch()
    {
        // Arrange
        var named = new List<string>();

        // Act
        foreach (var file in RepositorySourceIndex.Under("src/CcDirector.Gateway", ".cs"))
        {
            var text = File.ReadAllText(file);
            if (text.Contains("TenantSettingKeys.MailDeskSwitch", StringComparison.Ordinal)
                || text.Contains("\"mail_desk_switch\"", StringComparison.Ordinal))
                named.Add(RepositorySourceIndex.Relative(file));
        }

        // Assert: exactly these, so a scan that found nothing cannot pass.
        Assert.Equal(new[] { "src/CcDirector.Gateway/MailDesk/MailDeskSwitch.cs", "src/CcDirector.Gateway/Settings/TenantSettingKeys.cs" },
            named.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    // Read from the COMPILED Gateway, not its text, so a caller is found whatever its variable is called: every method
    // body in the assembly - lambdas and async methods included, as the compiler emits them - is searched for a call to
    // MailDeskSwitch.Set, and each caller is named by the type it was written in.
    [Fact]
    public void MailDeskSwitchSet_IsCalledOnlyByTheAdministratorRoute()
    {
        // Arrange
        var set = typeof(MailDeskSwitch).GetMethod(nameof(MailDeskSwitch.Set))!;

        // Act
        var callers = CallersOf(set).Select(OuterType).Select(t => t.FullName!).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();

        // Assert: exactly this, so a search that found nothing cannot pass.
        Assert.Equal(new[] { typeof(AdminMailDeskEndpoint).FullName! }, callers);
    }

    /// <summary>Every method of the Gateway assembly whose body calls <paramref name="target"/>.</summary>
    private static IEnumerable<System.Reflection.MethodBase> CallersOf(System.Reflection.MethodInfo target)
    {
        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
        var module = target.Module;
        foreach (var type in module.Assembly.GetTypes())
        {
            var methods = type.GetMethods(all).Cast<System.Reflection.MethodBase>().Concat(type.GetConstructors(all));
            foreach (var method in methods)
            {
                var il = method.GetMethodBody()?.GetILAsByteArray();
                if (il is null) continue;
                // call (0x28) and callvirt (0x6F) take a four-byte method token. A byte that only looks like one names
                // some other method or none, so it can never be counted as a call to the target.
                for (var i = 0; i + 4 < il.Length; i++)
                {
                    if (il[i] != 0x28 && il[i] != 0x6F) continue;
                    var token = BitConverter.ToInt32(il, i + 1);
                    if (token != target.MetadataToken) continue;
                    if (module.ResolveMethod(token) == target) { yield return method; break; }
                }
            }
        }
    }

    /// <summary>The type a method was written in: compiler-made closure and state machine types are nested inside it.</summary>
    private static Type OuterType(System.Reflection.MethodBase method)
    {
        var type = method.DeclaringType!;
        while (type.DeclaringType is not null) type = type.DeclaringType;
        return type;
    }

    // ---- the route group ------------------------------------------------------------------------------------

    /// <summary>
    /// The Mail Desk routes, mapped into the gate exactly as GatewayHost maps them (<see cref="MailDeskEndpoints.Map"/>
    /// into <see cref="MailDeskGate.Group"/>), beside one factory route behind the factory agents gate.
    /// </summary>
    private (WebApplication App, MailDeskSwitch MailDesk, FactoryAgentsSwitch Factory) MailDeskApp()
    {
        var mailDesk = Switch();
        var factory = new FactoryAgentsSwitch(false, new TenantSettingsStore(Db));
        var app = WebApplication.CreateBuilder().Build();

        var decisions = new MailDeskDecisionStore(Db);
        CcDirector.Gateway.Api.MailDeskEndpoints.Map(MailDeskGate.Group(app, mailDesk, Resolve), Resolve, new CcDirector.Gateway.Api.MailDeskEndpoints.Parts(
            new MailDeskChannelStore(Db), new MailDeskRulesStore(Db), decisions,
            new MailDeskChannelFold(_ => Array.Empty<TriggerEntity>(), _ => "UTC", decisions),
            _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown, _ => Array.Empty<string>(), () => _now));

        FactoryAgentsGate.Group(app, factory, Resolve).MapGet("/gateway/factory/placeholder", () => Results.Ok());
        return (app, mailDesk, factory);
    }

    /// <summary>Every Mail Desk route the gate must cover, named, so a mapping that lost one cannot pass on an empty list.</summary>
    private static readonly string[] ExpectedMailDeskRoutes =
    {
        "GET " + MailDeskGate.Prefix + "/status",
        "GET " + MailDeskGate.Prefix + "/channels",
        "POST " + MailDeskGate.Prefix + "/channels",
        "POST " + MailDeskGate.Prefix + "/channels/remove",
        "POST " + MailDeskGate.Prefix + "/channels/mode",
        "GET " + MailDeskGate.Prefix + "/rules",
        "GET " + MailDeskGate.Prefix + "/rules/history",
        "POST " + MailDeskGate.Prefix + "/rules/{list}/add",
        "POST " + MailDeskGate.Prefix + "/rules/{list}/remove",
        "GET " + MailDeskGate.Prefix + "/decisions",
    };

    private static IReadOnlyList<RouteEndpoint> Endpoints(WebApplication app)
        => ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();

    private static IReadOnlyList<RouteEndpoint> MailDeskEndpoints(WebApplication app)
        => Endpoints(app).Where(e => e.RoutePattern.RawText!.StartsWith(MailDeskGate.Prefix, StringComparison.Ordinal)).ToList();

    private static async Task<(int Status, string Body)> Invoke(WebApplication app, RouteEndpoint endpoint, string? account,
        string body = "{}", IReadOnlyDictionary<string, string>? routeValues = null)
    {
        var ctx = new DefaultHttpContext { RequestServices = app.Services };
        ctx.Request.Method = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET";
        ctx.Request.Path = endpoint.RoutePattern.RawText;
        if (account is not null) ctx.Request.Headers[TenantHeader] = account;
        foreach (var p in endpoint.RoutePattern.Parameters)
            ctx.Request.RouteValues[p.Name] = routeValues is not null && routeValues.TryGetValue(p.Name, out var v) ? v : "x";
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var response = new MemoryStream();
        ctx.Response.Body = response;
        await endpoint.RequestDelegate!(ctx);
        return (ctx.Response.StatusCode, Encoding.UTF8.GetString(response.ToArray()));
    }

    private static string Describe(RouteEndpoint e)
        => $"{e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.FirstOrDefault()} {e.RoutePattern.RawText}";

    [Fact]
    public async Task EveryMailDeskRoute_AccountOff_Answers404AsIfUnmapped()
    {
        // Arrange
        var (app, mailDesk, _) = MailDeskApp();
        mailDesk.Set(new TenantId("acct-a"), true, "email@devthrottle.com", "the owner's account only", _now);
        var gated = MailDeskEndpoints(app);
        var names = gated.Select(Describe).ToList();
        foreach (var expected in ExpectedMailDeskRoutes) Assert.Contains(expected, names);
        Assert.Equal(ExpectedMailDeskRoutes.Length, gated.Count);

        // Act + Assert
        foreach (var endpoint in gated)
        {
            var off = await Invoke(app, endpoint, "acct-b");
            Assert.True(off.Status == StatusCodes.Status404NotFound && off.Body.Length == 0,
                $"{Describe(endpoint)} answered {off.Status} '{off.Body}' for an account that is off");
            var unbound = await Invoke(app, endpoint, null);
            Assert.True(unbound.Status == StatusCodes.Status404NotFound && unbound.Body.Length == 0,
                $"{Describe(endpoint)} answered {unbound.Status} '{unbound.Body}' for a request bound to no account");
        }
    }

    [Fact]
    public async Task MailDeskRoutes_AccountOn_AreServed()
    {
        // Arrange
        var (app, mailDesk, _) = MailDeskApp();
        mailDesk.Set(new TenantId("acct-a"), true, "email@devthrottle.com", "the owner's account only", _now);
        var byName = Endpoints(app).ToDictionary(Describe);

        // Act
        var status = await Invoke(app, byName["GET " + MailDeskGate.Prefix + "/status"], "acct-a");

        // Assert
        Assert.Equal(StatusCodes.Status200OK, status.Status);
    }

    // The case that matters for the owner's grant (D16): his factories run, so factory agents are on for his account,
    // and that must not let Mail Desk through.
    [Fact]
    public async Task MailDeskRoutes_FactoryAgentsOnButMailDeskOff_Answer404()
    {
        // Arrange
        var (app, _, factory) = MailDeskApp();
        factory.Set(new TenantId("acct-a"), true, "email@devthrottle.com", "the owner's factories run", _now);
        var byName = Endpoints(app).ToDictionary(Describe);

        // Act
        var factoryRoute = await Invoke(app, byName["GET /gateway/factory/placeholder"], "acct-a");
        var mailDeskRoutes = new List<(string, int, string)>();
        foreach (var endpoint in MailDeskEndpoints(app))
        {
            var (status, body) = await Invoke(app, endpoint, "acct-a");
            mailDeskRoutes.Add((Describe(endpoint), status, body));
        }

        // Assert
        Assert.Equal(StatusCodes.Status200OK, factoryRoute.Status);
        Assert.NotEmpty(mailDeskRoutes);
        Assert.All(mailDeskRoutes, r => Assert.True(r.Item2 == StatusCodes.Status404NotFound && r.Item3.Length == 0,
            $"{r.Item1} answered {r.Item2} '{r.Item3}' with factory agents on and Mail Desk off"));
    }

    [Fact]
    public void IsOnFor_NoAccountBound_IsOffEvenWhenAnAccountIsOn()
    {
        // Arrange
        var sw = Switch();
        sw.Set(new TenantId("acct-a"), true, "email@devthrottle.com", "the owner's account only", _now);

        // Act
        var on = MailDeskGate.IsOnFor(new DefaultHttpContext(), sw, Resolve, out var why);

        // Assert
        Assert.False(on);
        Assert.Equal("no account is bound to this request", why);
    }

    // ---- what a Mail Desk start is ---------------------------------------------------------------------------

    private static TriggerEntity DefinitionTrigger(string factory, string? checkPath) => new()
    {
        Name = "t",
        Factory = factory,
        FactoryAgent = "mail-desk",
        Machine = Machine,
        CheckCommand = checkPath is null ? "cc-mail count --json" : "",
        CheckPath = checkPath,
        Seat = checkPath is null ? null : "mail-desk",
        DefinitionVersion = checkPath is null ? null : 1,
    };

    [Theory]
    [InlineData("mail-desk")]
    [InlineData("Mail-Desk")]
    [InlineData("  mail-desk ")]
    public void MailDeskStart_Is_ADefinitionTriggerOfMailDeskWithTheScreenCheck_IsAMatch(string factory)
        => Assert.True(MailDeskStart.Is(DefinitionTrigger(factory, MailDeskTemplate.CheckPath)));

    [Theory]
    [InlineData("website-factory")]
    [InlineData("mail-desk-2")]
    [InlineData("")]
    public void MailDeskStart_Is_AnotherFactory_IsNotAMatch(string factory)
        => Assert.False(MailDeskStart.Is(DefinitionTrigger(factory, MailDeskTemplate.CheckPath)));

    [Theory]
    [InlineData("checks/new_mail.py")]
    [InlineData("checks/Screen.py")]
    [InlineData("checks/screen.py.bak")]
    public void MailDeskStart_Is_AnotherCheckPath_IsNotAMatch(string checkPath)
        => Assert.False(MailDeskStart.Is(DefinitionTrigger(MailDeskTemplate.Factory, checkPath)));

    [Fact]
    public void MailDeskStart_Is_ACommandTrigger_IsNotAMatch()
        => Assert.False(MailDeskStart.Is(DefinitionTrigger(MailDeskTemplate.Factory, checkPath: null)));

    [Theory]
    [InlineData("mail-desk", "checks/screen.py", true)]
    [InlineData("Mail-Desk", "checks/screen.py", true)]
    [InlineData("website-factory", "checks/screen.py", false)]
    [InlineData("mail-desk", "checks/new_mail.py", false)]
    public void MailDeskStart_Is_ATimedStart_MatchesOnTheSameRuleAsATrigger(string factory, string checkPath, bool expected)
        => Assert.Equal(expected, MailDeskStart.Is(new CronJobDto { Factory = factory, CheckPath = checkPath }));

    [Fact]
    public void MailDeskStart_Is_ATimedStartWithNoCheck_IsNotAMatch()
        => Assert.False(MailDeskStart.Is(new CronJobDto { Factory = MailDeskTemplate.Factory, CheckPath = null }));

    // ---- the check report ----------------------------------------------------------------------------------

    private static readonly TenantId Account = new("acct-mail");

    private TriggerService Service(MailDeskSwitch mailDesk)
    {
        // The published definition the start is built from: the fixture's outreach seat, renamed for Mail Desk.
        var d = FactoryDefinitionCheck.Read(DefinitionFixture.Valid()).Definition!;
        var seat = d.Seats.Single(s => s.Seat == "outreach") with { Seat = "mail-desk", Name = "Mail Desk" };
        var published = new PublishedFactoryDefinition(1, d with { Factory = MailDeskTemplate.Factory, BossSeat = "mail-desk", Seats = new[] { seat } });
        return new TriggerService(new TriggerStore(Db), new FactoryActivityRecord(Db),
            (_, request, _) =>
            {
                lock (_starts) _starts.Add(request);
                return Task.FromResult(TriggerStartAttempt.Started($"dddddddd-0000-4000-8000-{++_nextSession:D12}"));
            },
            findSession: (_, _) => null, findSessionByName: (_, _) => null, timeZone: _ => TimeZoneInfo.Utc,
            nowUtc: () => _now, startLifetime: CancellationToken.None,
            directorRunsDefinitionChecks: (_, _) => true, machineRunsDefinitionChecks: (_, _) => true,
            definitionOf: (_, factory) => factory == MailDeskTemplate.Factory ? published : null,
            mailDeskOn: mailDesk.IsOn);
    }

    /// <summary>A definition trigger of the mail-desk factory, as a publish writes it.</summary>
    private Guid AddMailDeskTrigger(string checkPath = MailDeskTemplate.CheckPath)
    {
        using var ctx = Db.CreateContext(Account);
        var row = new TriggerEntity
        {
            Name = "mail-desk-screen",
            Factory = MailDeskTemplate.Factory,
            FactoryAgent = "mail-desk",
            Machine = Machine,
            RepoPath = "",
            CheckCommand = "",
            IntervalSeconds = 300,
            Prompt = "Mail to screen: {count}. {context}",
            CreatedBy = "definition of mail-desk, published by email@devthrottle.com",
            CreatedUtc = _now.AddMinutes(-1),
            Seat = "mail-desk",
            DefinitionVersion = 1,
            CheckPath = checkPath,
        };
        row.TenantId = ctx.ActiveTenant!;
        ctx.Triggers.Add(row);
        ctx.SaveChanges();
        return row.Id;
    }

    private TriggerCheckReport Report(int count, string state = """{"seen":["m-1"]}""")
        => new() { CheckedAtUtc = _now, ExitCode = 0, Output = $$"""{"count": {{count}}, "context": "from a@example.com"}""", State = state };

    private (int Runs, int StateRows, TriggerEntity Trigger) Recorded(Guid triggerId)
    {
        using var ctx = Db.CreateContext(Account);
        var owner = FactoryCheckStateStore.TriggerOwner(triggerId);
        return (ctx.TriggerRuns.Count(r => r.TriggerId == triggerId),
            ctx.FactoryCheckStates.Count(s => s.OwnerKey == owner),
            ctx.Triggers.Single(t => t.Id == triggerId));
    }

    [Fact]
    public async Task CheckReport_MailDeskOff_IsRefusedAndRecordsNothing()
    {
        // Arrange: factory agents need not be asked here - the service is behind their gate, which is on for this
        // account; Mail Desk is off. The check counted work, so without the refusal a session would start.
        var service = Service(Switch());
        var trigger = AddMailDeskTrigger();

        // Act
        var result = await service.ReportCheckAsync(Account, Director, trigger.ToString("D"), Report(count: 3), CancellationToken.None);

        // Assert
        Assert.Equal(TriggerReportRefusal.MailDeskOff, result.Refusal);
        Assert.Null(result.Run);
        Assert.Null(result.Starting);
        Assert.Contains("Mail Desk is not switched on", result.Error);
        var (runs, stateRows, row) = Recorded(trigger);
        Assert.Equal(0, runs);                                 // no run row
        Assert.Equal(0, stateRows);                            // no kept check memory
        Assert.Empty(_starts);                                 // no session start
        Assert.Equal("", row.ClaimedByDirectorId);             // claim unchanged
        Assert.Null(row.ClaimedUtc);
        Assert.Null(row.LastCheckUtc);                         // nor any last-check fact
        Assert.Null(row.LastOutcome);
        Assert.Null(row.LastStartedUtc);                       // no lock taken
        Assert.True(service.HoldsNoLock(Account, trigger));
    }

    [Fact]
    public async Task CheckReport_MailDeskOffButNotAMailDeskStart_IsRecordedAsBefore()
    {
        // Arrange: the same factory and seat, but a different check - not the screening check.
        var service = Service(Switch());
        var trigger = AddMailDeskTrigger(checkPath: "checks/new_mail.py");

        // Act
        var result = await service.ReportCheckAsync(Account, Director, trigger.ToString("D"), Report(count: 3), CancellationToken.None);
        var run = await result.Starting!;

        // Assert
        Assert.Equal(TriggerReportRefusal.None, result.Refusal);
        Assert.Equal(TriggerRunOutcome.Started, run!.Outcome);
        Assert.Single(_starts);
        var (runs, stateRows, row) = Recorded(trigger);
        Assert.Equal(1, runs);
        Assert.Equal(1, stateRows);
        Assert.Equal(Director, row.ClaimedByDirectorId);
    }

    // Deliberate in phase 1: with Mail Desk ON the screening check is recorded exactly as any definition trigger's -
    // a count above zero starts the seat. Phase 3 replaces that with its hand-off; this test says so until then.
    [Fact]
    public async Task CheckReport_MailDeskOn_IsRecordedAsBefore()
    {
        // Arrange
        var mailDesk = Switch();
        mailDesk.Set(Account, true, "email@devthrottle.com", "the owner's account only", _now);
        var service = Service(mailDesk);
        var trigger = AddMailDeskTrigger();

        // Act: a check that counted nothing, then one that counted work.
        var quiet = await service.ReportCheckAsync(Account, Director, trigger.ToString("D"), Report(count: 0), CancellationToken.None);
        var busy = await service.ReportCheckAsync(Account, Director, trigger.ToString("D"),
            Report(count: 2, state: """{"seen":["m-1","m-2"]}"""), CancellationToken.None);
        var started = await busy.Starting!;

        // Assert
        Assert.Equal(TriggerReportRefusal.None, quiet.Refusal);
        Assert.Equal(TriggerRunOutcome.NothingToDo, quiet.Run!.Outcome);
        Assert.Equal(TriggerReportRefusal.None, busy.Refusal);
        Assert.Equal(TriggerRunOutcome.Started, started!.Outcome);
        Assert.Equal(MailDeskTemplate.Factory, Assert.Single(_starts).Factory);
        var (runs, stateRows, row) = Recorded(trigger);
        Assert.Equal(2, runs);
        Assert.Equal(2, stateRows);
        Assert.Equal(Director, row.ClaimedByDirectorId);
    }

    // On the wire: the Director's report route, behind a factory agents gate that is ON for the account, answers the
    // refusal as the contract names it - 409 { error: "mail_desk_off", detail } - and records nothing.
    [Fact]
    public async Task CheckReportRoute_MailDeskOff_Answers409MailDeskOff()
    {
        // Arrange
        var mailDesk = Switch();
        var factory = new FactoryAgentsSwitch(false, new TenantSettingsStore(Db));
        factory.Set(Account, true, "email@devthrottle.com", "the owner's factories run", _now);
        var service = Service(mailDesk);
        var trigger = AddMailDeskTrigger();
        var app = WebApplication.CreateBuilder().Build();
        TriggerEndpoints.Map(FactoryAgentsGate.Group(app, factory, Resolve), Resolve, service,
            directorMachine: (_, _) => Machine, nowUtc: () => _now,
            sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown,
            isDefined: (_, _) => true,
            dueChecks: DueCheckTestParts.NoDueChecks(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".due.json")));
        var report = Endpoints(app).Single(e => Describe(e) == "POST /directors/{directorId}/triggers/{id}/checks");

        // Act
        var (status, body) = await Invoke(app, report, Account.Value, JsonSerializer.Serialize(Report(count: 3)),
            new Dictionary<string, string> { ["directorId"] = Director, ["id"] = trigger.ToString("D") });

        // Assert
        Assert.Equal(StatusCodes.Status409Conflict, status);
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("mail_desk_off", json.GetProperty("error").GetString());
        Assert.Contains("Mail Desk is not switched on", json.GetProperty("detail").GetString());
        Assert.Equal((0, 0), (Recorded(trigger).Runs, Recorded(trigger).StateRows));
        Assert.Empty(_starts);
    }

    // ---- the administrator route ---------------------------------------------------------------------------

    private static AdminMailDeskEndpoint.SetRequest Req(string? account, bool? enabled = true,
        string? actor = "email@devthrottle.com", string? reason = "the owner's account only")
        => new(account, enabled, actor, reason);

    [Fact]
    public void AdminSet_NoServiceToken_IsRefusedAndRecordsNothing()
    {
        // Arrange
        var tenants = new TenantRegistry(Db);
        var a = tenants.MintOrLookupBySubject("subject-a", "a@example.com");
        var sw = Switch();

        // Act
        var result = AdminMailDeskEndpoint.Handle(new DefaultHttpContext(), Req(a.Value), sw, tenants, _now);

        // Assert
        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(result));
        Assert.False(sw.IsOn(a));
        Assert.Null(sw.Decision(a));
    }

    [Theory]
    [InlineData(null, true, "who", "why")]
    [InlineData("  ", true, "who", "why")]
    [InlineData("ACCOUNT", null, "who", "why")]
    [InlineData("ACCOUNT", true, " ", "why")]
    [InlineData("ACCOUNT", true, "who", "")]
    public void AdminSet_MissingField_IsRefused(string? account, bool? enabled, string? actor, string? reason)
    {
        // Arrange
        var tenants = new TenantRegistry(Db);
        var a = tenants.MintOrLookupBySubject("subject-a", "a@example.com");
        var sw = Switch();

        // Act
        var result = AdminMailDeskEndpoint.Handle(Authorized(),
            Req(account == "ACCOUNT" ? a.Value : account, enabled, actor, reason), sw, tenants, _now);

        // Assert
        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Null(sw.Decision(a));
    }

    [Fact]
    public void AdminSet_AccountTheGatewayDoesNotHave_IsRefused()
    {
        // Arrange
        var tenants = new TenantRegistry(Db);
        var sw = Switch();

        // Act
        var result = AdminMailDeskEndpoint.Handle(Authorized(), Req("acct-nobody"), sw, tenants, _now);

        // Assert
        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Null(sw.Decision(new TenantId("acct-nobody")));
    }

    [Fact]
    public void AdminSet_KnownAccount_SwitchesThatAccountOnlyAndReadsBack()
    {
        // Arrange
        var tenants = new TenantRegistry(Db);
        var a = tenants.MintOrLookupBySubject("subject-a", "a@example.com");
        var b = tenants.MintOrLookupBySubject("subject-b", "b@example.com");
        var sw = Switch();

        // Act
        var set = AdminMailDeskEndpoint.Handle(Authorized(), Req(a.Value), sw, tenants, _now);

        // Assert
        Assert.Equal(StatusCodes.Status200OK, StatusOf(set));
        Assert.True(sw.IsOn(a));
        Assert.False(sw.IsOn(b));
        var d = sw.Decision(a)!;
        Assert.Equal("email@devthrottle.com", d.Actor);
        Assert.Equal("the owner's account only", d.Reason);
        Assert.Equal(_now, d.RecordedAtUtc);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(AdminMailDeskEndpoint.Read(sw, tenants, a.Value)));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(AdminMailDeskEndpoint.Read(sw, tenants, "acct-nobody")));
        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(AdminMailDeskEndpoint.Read(sw, tenants, "")));
    }

    // The command an administrator runs holds no device key, so the route carries its own gate and must be on the
    // device-key gate's exempt list beside the factory agents one.
    [Fact]
    public void AdminPath_IsExemptFromTheDeviceKeyGate()
    {
        // Arrange
        var field = typeof(CcDirector.Gateway.Util.AuthMiddleware).GetField("PublicPaths",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        // Act
        var paths = (HashSet<string>)field.GetValue(null)!;

        // Assert
        Assert.Contains(AdminFactoryAgentsEndpoint.Path, paths);
        Assert.Contains(AdminMailDeskEndpoint.Path, paths);
    }
}
