using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.History;
using CcDirector.Gateway.MailDesk;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// THE MAIL DESK ROUTES (Mail Desk mission, phase 1 contract, sections 4 and 5), booted on an in-memory test server
/// exactly as GatewayHost maps them - <see cref="MailDeskEndpoints.Map"/> into <see cref="MailDeskGate.Group"/> - over the
/// real stores, the real fold and a throwaway database. A stand-in for the authentication middleware puts on each request
/// the identity the real one would: a person's device, a session's key, or the account's machine token.
///
/// These pin what only the routes know: WHO MAY DO WHAT (a factory's own session changes the rules and never a channel;
/// the machine token changes nothing; a session the Gateway does not know yet is answered 409), that EVERY CHANGE CARRIES
/// A WHY, that NOTHING IS RECORDED ON ANY REFUSAL (each refusal test compares every Mail Desk row before and after), who
/// a change records, the channel row's fold, every decisions filter, a rules change made again after another writer
/// saved first, and that one account never sees another's.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskEndpointTests : IAsyncLifetime
{
    private const string TenantHeader = "X-Test-Account";
    private const string CallerHeader = "X-Test-Caller";
    private const string Mailbox = "email@devthrottle.com";
    private const string OtherMailbox = "support@example.com";
    private const string Machine = "devlinux";
    private const string Person = "person";
    private const string MachineToken = "machine";
    private const string PersonDevice = "owner-browser-1";
    private const string NoFactorySession = "75000000-0000-4000-8000-000000000001";
    private const string NoFactorySessionName = "Mail Desk - Developer - set up the mailbox";
    private const string FactorySession = "75000000-0000-4000-8000-000000000002";
    private const string FactorySessionName = "Invoices - Clerk - file the bills";
    private const string UnknownSession = "75000000-0000-4000-8000-000000000003";
    private const string P = MailDeskGate.Prefix;
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase _db = null!;
    private MailDeskSwitch _switch = null!;
    private MailDeskChannelStore _channels = null!;
    private MailDeskRulesStore _rules = null!;
    private MailDeskDecisionStore _decisions = null!;
    private WebApplication? _app;
    private HttpClient _client = null!;

    // What the fold and the routes read, changed by a test before it calls.
    private DateTime _now = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
    private string _timeZone = "UTC";
    private readonly List<TriggerEntity> _triggers = new();
    private readonly List<string> _knownMachines = new();

    public async Task InitializeAsync()
    {
        _db = _h.Open();
        _switch = new MailDeskSwitch(new TenantSettingsStore(_db));
        _switch.Set(A, true, "email@devthrottle.com", "the owner's account only", _now);
        _switch.Set(B, true, "email@devthrottle.com", "a second account, for the isolation tests", _now);
        _channels = new MailDeskChannelStore(_db);
        _rules = new MailDeskRulesStore(_db);
        _decisions = new MailDeskDecisionStore(_db);
        await StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        _h.Dispose();
    }

    private MailDeskEndpoints.Parts Parts() => new(_channels, _rules, _decisions, new MailDeskSettingsStore(_db),
        new MailDeskChannelFold(_ => _triggers.ToList(), _ => _timeZone, _decisions),
        FactoryOf, _ => _knownMachines.ToList(), () => _now);

    private async Task StartAsync(Func<MailDeskEndpoints.Parts, MailDeskEndpoints.Parts>? adjust = null)
    {
        if (_app is not null) await _app.DisposeAsync();
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
        var parts = Parts();
        MailDeskEndpoints.Map(MailDeskGate.Group(app, _switch, Resolve), Resolve, adjust is null ? parts : adjust(parts));
        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
    }

    // ---------- who is asking ----------

    private static readonly Func<HttpContext, TenantId?> Resolve = ctx =>
        ctx.Request.Headers.TryGetValue(TenantHeader, out var v) && !string.IsNullOrWhiteSpace(v) ? new TenantId(v.ToString()) : null;

    /// <summary>What the authentication middleware stamps on a request, from the test's caller header.</summary>
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
        {
            ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "the-machine-token";
        }
        else if (Guid.TryParse(caller, out var session))
        {
            ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(session, tenant, "dir-a");
        }
    }

    private static SessionFactoryLookup FactoryOf(string session) => session switch
    {
        NoFactorySession => SessionFactoryLookup.InNoFactory with { Name = NoFactorySessionName },
        FactorySession => SessionFactoryLookup.In("invoices") with { Name = FactorySessionName },
        _ => SessionFactoryLookup.NotKnown,
    };

    /// <summary>Who a person's change records: the identifier their device carries.</summary>
    private static string PersonId => $"device browser {PersonDevice}";

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

    private Task<(HttpStatusCode Status, JsonElement Body)> Get(string path, string caller = Person, TenantId? account = null)
        => Send("GET", path, caller, null, account);

    private Task<(HttpStatusCode Status, JsonElement Body)> Post(string path, object body, string caller = Person, TenantId? account = null)
        => Send("POST", path, caller, body, account);

    private Task<(HttpStatusCode Status, JsonElement Body)> AddMailbox(string caller = Person, string address = Mailbox, TenantId? account = null)
        => Post(P + "/channels", new { address, provider = "outlook", machines = new[] { Machine }, why = "the support mailbox" }, caller, account);

    private static string Error(JsonElement body) => body.GetProperty("error").GetString()!;

    // ---------- what is recorded ----------

    /// <summary>Every Mail Desk row of an account, as one text: a refusal must leave it exactly as it was.</summary>
    private string Recorded(TenantId? account = null)
    {
        using var ctx = _db.CreateContext(account ?? A);
        var channels = ctx.MailDeskChannels.AsNoTracking().OrderBy(c => c.Id).ToList()
            .Select(c => $"{c.Id}|{c.Address}|{c.Mode}|{c.MachinesJson}|{c.RemovedUtc:o}|{c.ChangedUtc:o}");
        var versions = ctx.MailDeskRules.AsNoTracking().OrderBy(r => r.Version).ToList().Select(r => $"{r.Version}|{r.Document}");
        var changes = ctx.MailDeskListChanges.AsNoTracking().OrderBy(c => c.Id).ToList().Select(c => $"{c.Id}|{c.Kind}|{c.Subject}");
        var decisions = ctx.MailDeskDecisions.AsNoTracking().Count();
        return string.Join("\n", channels.Concat(versions).Concat(changes).Append($"decisions={decisions}"));
    }

    private void AssertRefusedAndNothingRecorded((HttpStatusCode Status, JsonElement Body) answer, HttpStatusCode status, string error,
        string before, TenantId? account = null)
    {
        Assert.Equal(status, answer.Status);
        Assert.Equal(error, Error(answer.Body));
        Assert.Equal(before, Recorded(account));
    }

    private MailDeskChannelEntity Channel(string address = Mailbox, TenantId? account = null)
        => _channels.Add(account ?? A, address, "outlook", new[] { Machine }, MailDeskWho.Person("seed"), "seeded", _now).Channel!;

    private void Decide(MailDeskChannelEntity channel, string key, DateTime decidedUtc, string layer = MailDeskWords.Layers.Allow,
        string decision = MailDeskWords.Decisions.Archive, long? costMicros = null, TenantId? account = null)
        => _decisions.Insert(account ?? A, new MailDeskDecisionInput(channel.Id, key, decidedUtc, "news@example.org", "A newsletter",
            layer, decision,
            decision == MailDeskWords.Decisions.Route ? "books" : null, decision == MailDeskWords.Decisions.Route ? "clerk" : null,
            "the allow-list says so", null, costMicros, MailDeskWords.Modes.Observe, null, null, decidedUtc));

    private static TriggerEntity MailDeskTrigger(DateTime? lastCheck = null, string? outcome = null, string? reason = null) => new()
    {
        Name = "mail-desk check",
        Factory = MailDeskTemplate.Factory,
        FactoryAgent = "mail-desk",
        Machine = Machine,
        CheckCommand = "",
        CheckPath = MailDeskTemplate.CheckPath,
        Seat = "mail-desk",
        DefinitionVersion = 1,
        LastCheckUtc = lastCheck,
        LastOutcome = outcome,
        LastReason = reason,
    };

    // ======================================================================================================
    // Every route answers - a person, the switch on
    // ======================================================================================================

    [Fact]
    public async Task Status_AccountOnWithNothingYet_AnswersEnabledVersionZeroAndEveryDecisionAtZero()
    {
        // Act
        var (status, body) = await Get(P + "/status");

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, body.GetProperty("rulesVersion").GetInt32());
        Assert.Equal(0, body.GetProperty("channels").GetArrayLength());
        var today = body.GetProperty("today");
        Assert.Equal(("2026-10-18", "UTC", 0), (today.GetProperty("date").GetString(), today.GetProperty("timeZone").GetString(), today.GetProperty("total").GetInt32()));
        Assert.Equal(MailDeskWords.Decisions.All, today.GetProperty("byDecision").EnumerateObject().Select(p => p.Name));
        Assert.All(today.GetProperty("byDecision").EnumerateObject(), p => Assert.Equal(0, p.Value.GetInt32()));
        Assert.NotEmpty(body.GetProperty("help").EnumerateArray());
    }

    [Fact]
    public async Task AddChannel_Person_Answers201WithTheFoldedRowAndRecordsWhoAndWhy()
    {
        // Act
        var (status, row) = await AddMailbox();

        // Assert
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal((Mailbox, "outlook", "email", "observe"),
            (row.GetProperty("address").GetString(), row.GetProperty("provider").GetString(), row.GetProperty("kind").GetString(), row.GetProperty("mode").GetString()));
        Assert.Equal(MailDeskChannelFold.ModeLabels[MailDeskWords.Modes.Observe], row.GetProperty("modeLabel").GetString());
        Assert.Equal(new[] { Machine }, row.GetProperty("machines").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(("mail-desk", "mail-desk"), (row.GetProperty("factory").GetString(), row.GetProperty("seat").GetString()));
        Assert.Equal(PersonId, row.GetProperty("createdBy").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("removedUtc").ValueKind);
        Assert.Equal(0, row.GetProperty("today").GetProperty("total").GetInt32());

        var change = (await Get(P + "/rules/history")).Body.GetProperty("changes")[0];
        Assert.Equal(("channel", "add", Mailbox), (change.GetProperty("kind").GetString(), change.GetProperty("action").GetString(), change.GetProperty("subject").GetString()));
        Assert.Equal(("person", PersonId), (change.GetProperty("who").GetProperty("kind").GetString(), change.GetProperty("who").GetProperty("id").GetString()));
        Assert.Equal(JsonValueKind.Null, change.GetProperty("who").GetProperty("name").ValueKind);
        Assert.Equal($"a person ({PersonId})", change.GetProperty("whoInWords").GetString());
        Assert.Equal("the support mailbox", change.GetProperty("why").GetString());
    }

    [Fact]
    public async Task AddChannel_SessionInNoFactory_IsAllowedAndRecordsTheSessionsIdAndItsName()
    {
        // Act
        var (status, row) = await AddMailbox(NoFactorySession);

        // Assert
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(NoFactorySession, row.GetProperty("createdBy").GetString());
        var change = (await Get(P + "/rules/history")).Body.GetProperty("changes")[0];
        var who = change.GetProperty("who");
        Assert.Equal(("session", NoFactorySession, NoFactorySessionName),
            (who.GetProperty("kind").GetString(), who.GetProperty("id").GetString(), who.GetProperty("name").GetString()));
        Assert.Equal($"session {NoFactorySession} ({NoFactorySessionName})", change.GetProperty("whoInWords").GetString());
    }

    [Fact]
    public async Task ListChannels_RemovedChannel_IsHiddenUnlessAllIsAsked()
    {
        // Arrange
        await AddMailbox();
        await AddMailbox(address: OtherMailbox);
        await Post(P + "/channels/remove", new { address = OtherMailbox, why = "the mailbox was closed" });

        // Act
        var active = (await Get(P + "/channels")).Body;
        var all = (await Get(P + "/channels?all=true")).Body;

        // Assert
        Assert.Equal(1, active.GetProperty("count").GetInt32());
        Assert.Equal(Mailbox, active.GetProperty("channels")[0].GetProperty("address").GetString());
        Assert.Equal(2, all.GetProperty("count").GetInt32());
        var removed = all.GetProperty("channels").EnumerateArray().Single(c => c.GetProperty("address").GetString() == OtherMailbox);
        Assert.Equal(_now, removed.GetProperty("removedUtc").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task RemoveChannel_Person_AnswersTheRemovedRowAndRecordsTheRemoval()
    {
        // Arrange
        await AddMailbox();

        // Act
        var (status, row) = await Post(P + "/channels/remove", new { address = "Email@DevThrottle.com", why = "the mailbox was closed" });

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Mailbox, row.GetProperty("address").GetString());
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("removedUtc").ValueKind);
        var change = (await Get(P + "/rules/history?kind=channel")).Body.GetProperty("changes")[0];
        Assert.Equal(("remove", "the mailbox was closed"), (change.GetProperty("action").GetString(), change.GetProperty("why").GetString()));
    }

    [Fact]
    public async Task RemoveChannel_NoSuchAddress_Answers404NoSuchChannelAndRecordsNothing()
    {
        // Arrange
        var before = Recorded();

        // Act
        var answer = await Post(P + "/channels/remove", new { address = Mailbox, why = "the mailbox was closed" });

        // Assert
        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.NotFound, "no_such_channel", before);
    }

    [Fact]
    public async Task SetMode_Live_AnswersTheRowInLiveWithItsLabelAndRecordsFromAndTo()
    {
        // Arrange
        await AddMailbox();

        // Act
        var (status, row) = await Post(P + "/channels/mode", new { address = Mailbox, mode = "live", why = "a week of observe looked right" });

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("live", row.GetProperty("mode").GetString());
        Assert.Equal(MailDeskChannelFold.ModeLabels[MailDeskWords.Modes.Live], row.GetProperty("modeLabel").GetString());
        var change = (await Get(P + "/rules/history?kind=mode")).Body.GetProperty("changes")[0];
        Assert.Equal(("observe", "live"),
            (change.GetProperty("detail").GetProperty("from").GetString(), change.GetProperty("detail").GetProperty("to").GetString()));
        Assert.Equal(JsonValueKind.Null, change.GetProperty("versionBefore").ValueKind);
    }

    [Fact]
    public async Task SetMode_NotAMode_Answers400AndRecordsNothing()
    {
        // Arrange
        await AddMailbox();
        var before = Recorded();

        // Act
        var answer = await Post(P + "/channels/mode", new { address = Mailbox, mode = "loud", why = "testing" });

        // Assert
        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.BadRequest, "bad_mode", before);
    }

    // ---------- adding a channel: its refusals ----------

    [Fact]
    public async Task AddChannel_AProviderOtherThanOutlook_Answers400NamingOutlookAndRecordsNothing()
    {
        var before = Recorded();

        var answer = await Post(P + "/channels", new { address = Mailbox, provider = "gmail", machines = new[] { Machine }, why = "try gmail" });

        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.BadRequest, "bad_provider", before);
        Assert.Contains("'outlook'", answer.Body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task AddChannel_NoMachines_Answers400AndRecordsNothing()
    {
        var before = Recorded();

        var answer = await Post(P + "/channels", new { address = Mailbox, provider = "outlook", machines = Array.Empty<string>(), why = "no machine" });

        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.BadRequest, "bad_machines", before);
    }

    [Fact]
    public async Task AddChannel_NotAnAddress_Answers400AndRecordsNothing()
    {
        var before = Recorded();

        var answer = await Post(P + "/channels", new { address = "*@example.com", provider = "outlook", machines = new[] { Machine }, why = "a domain" });

        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.BadRequest, "bad_address", before);
    }

    [Fact]
    public async Task AddChannel_AMachineTheAccountsDirectorsNeverReported_Answers400ListingTheKnownOnesAndRecordsNothing()
    {
        // Arrange
        _knownMachines.AddRange(new[] { "DEVLINUX", "BACKUP-PC" });
        var before = Recorded();

        // Act
        var answer = await Post(P + "/channels", new { address = Mailbox, provider = "outlook", machines = new[] { "devlinux", "laptop" }, why = "x" });

        // Assert
        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.BadRequest, "unknown_machine", before);
        var detail = answer.Body.GetProperty("detail").GetString()!;
        Assert.Contains("'laptop'", detail);
        Assert.Contains("BACKUP-PC, DEVLINUX", detail);
    }

    [Fact]
    public async Task AddChannel_KnownMachineInAnotherCase_IsKeptAsItsDirectorSpellsIt()
    {
        // Arrange
        _knownMachines.Add("DEVLINUX");

        // Act
        var (status, row) = await AddMailbox();

        // Assert
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(new[] { "DEVLINUX" }, row.GetProperty("machines").EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public async Task AddChannel_AddressAlreadyActive_Answers409AndRecordsNothing()
    {
        // Arrange
        await AddMailbox();
        var before = Recorded();

        // Act
        var answer = await AddMailbox(address: "EMAIL@devthrottle.com");

        // Assert
        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.Conflict, "channel_exists", before);
    }

    [Fact]
    public async Task AddChannel_BodyIsNotJson_Answers400AndRecordsNothing()
    {
        var before = Recorded();

        var answer = await Send("POST", P + "/channels", Person, "{ not json");

        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.BadRequest, "bad_request", before);
    }

    // ======================================================================================================
    // Who may do what (contract section 4) - every refusal records nothing
    // ======================================================================================================

    /// <summary>The three channel changes, each with a body that would succeed for a person.</summary>
    public static TheoryData<string, string> ChannelChanges => new()
    {
        { "/channels", JsonSerializer.Serialize(new { address = OtherMailbox, provider = "outlook", machines = new[] { Machine }, why = "a second mailbox" }) },
        { "/channels/remove", JsonSerializer.Serialize(new { address = Mailbox, why = "closing it" }) },
        { "/channels/mode", JsonSerializer.Serialize(new { address = Mailbox, mode = "live", why = "going live" }) },
    };

    [Theory]
    [MemberData(nameof(ChannelChanges))]
    public async Task ChannelChange_AFactorysSession_Answers403FactorySessionRefusedAndRecordsNothing(string path, string body)
    {
        // Arrange
        await AddMailbox();
        var before = Recorded();

        // Act
        var answer = await Send("POST", P + path, FactorySession, body);

        // Assert
        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.Forbidden, "factory_session_refused", before);
        Assert.Equal(MailDeskEndpoints.FactorySessionRefusedOutcome, answer.Body.GetProperty("outcome").GetString());
        Assert.Equal("FactorySessionRefused", MailDeskEndpoints.FactorySessionRefusedOutcome);
    }

    [Theory]
    [MemberData(nameof(ChannelChanges))]
    public async Task ChannelChange_TheMachineToken_Answers403NotAnAuthorAndRecordsNothing(string path, string body)
    {
        await AddMailbox();
        var before = Recorded();

        var answer = await Send("POST", P + path, MachineToken, body);

        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.Forbidden, "not_an_author", before);
    }

    [Theory]
    [MemberData(nameof(ChannelChanges))]
    public async Task ChannelChange_ASessionTheGatewayDoesNotKnowYet_Answers409AndRecordsNothing(string path, string body)
    {
        await AddMailbox();
        var before = Recorded();

        var answer = await Send("POST", P + path, UnknownSession, body);

        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.Conflict, SpawnFactory.NotYetKnown, before);
    }

    [Fact]
    public async Task AllowAdd_TheMachineToken_Answers403NotAnAuthorAndRecordsNothing()
    {
        var before = Recorded();

        var answer = await Post(P + "/rules/allow/add",
            new { from = "news@example.org", what = "a newsletter", @do = "keep-for-owner", why = "I read it" }, MachineToken);

        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.Forbidden, "not_an_author", before);
    }

    [Fact]
    public async Task AllowAdd_ASessionTheGatewayDoesNotKnowYet_Answers409AndRecordsNothing()
    {
        var before = Recorded();

        var answer = await Post(P + "/rules/allow/add",
            new { from = "news@example.org", what = "a newsletter", @do = "keep-for-owner", why = "I read it" }, UnknownSession);

        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.Conflict, SpawnFactory.NotYetKnown, before);
    }

    [Fact]
    public async Task AllowAdd_AFactorysSession_IsAllowedAndRecordsTheSessionsIdAndName()
    {
        // Act (D7: the rules change freely, every change recorded)
        var (status, body) = await Post(P + "/rules/allow/add",
            new { from = "news@example.org", what = "a newsletter", @do = "keep-for-owner", why = "the owner reads it" }, FactorySession);

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        var who = body.GetProperty("change").GetProperty("who");
        Assert.Equal(("session", FactorySession, FactorySessionName),
            (who.GetProperty("kind").GetString(), who.GetProperty("id").GetString(), who.GetProperty("name").GetString()));
    }

    [Theory]
    [InlineData("/status")]
    [InlineData("/channels")]
    [InlineData("/rules")]
    [InlineData("/rules/history")]
    [InlineData("/decisions")]
    public async Task EveryRead_EveryKindOfCaller_Answers200(string path)
    {
        foreach (var caller in new[] { Person, NoFactorySession, FactorySession, MachineToken })
        {
            var (status, _) = await Get(P + path, caller);
            Assert.True(status == HttpStatusCode.OK, $"{caller} reading {path} was answered {status}");
        }
    }

    // ---------- every change carries a why ----------

    /// <summary>Every change, with a body that would succeed but for its why, written in <c>{why}</c>.</summary>
    public static TheoryData<string, string> EveryChange => new()
    {
        { "/channels", """{"address":"support@example.com","provider":"outlook","machines":["devlinux"]{why}}""" },
        { "/channels/remove", """{"address":"email@devthrottle.com"{why}}""" },
        { "/channels/mode", """{"address":"email@devthrottle.com","mode":"live"{why}}""" },
        { "/rules/allow/add", """{"from":"news@example.org","what":"a newsletter","do":"archive"{why}}""" },
        { "/rules/block/add", """{"from":"*@spammy-domain.example"{why}}""" },
        { "/rules/rule/add", """{"name":"citizenship","when":{"subjectContains":["citizen"]},"route":{"factory":"citizenship","seat":"intake"}{why}}""" },
        { "/rules/route/add", """{"factory":"devthrottle","seat":"mail-desk","handles":"customers of DevThrottle"{why}}""" },
        { "/rules/allow/remove", """{"key":"kept@example.org"{why}}""" },
    };

    [Theory]
    [MemberData(nameof(EveryChange))]
    public async Task EveryChange_WithoutWhy_Answers400WhyRequiredAndRecordsNothing(string path, string body)
    {
        // Arrange
        await AddMailbox();
        await Post(P + "/rules/allow/add", new { from = "kept@example.org", what = "kept", @do = "archive", why = "seed" });
        var before = Recorded();

        // Act
        var missing = await Send("POST", P + path, Person, body.Replace("{why}", ""));
        var blank = await Send("POST", P + path, Person, body.Replace("{why}", ",\"why\":\"   \""));

        // Assert
        AssertRefusedAndNothingRecorded(missing, HttpStatusCode.BadRequest, "why_required", before);
        AssertRefusedAndNothingRecorded(blank, HttpStatusCode.BadRequest, "why_required", before);
    }

    [Theory]
    [MemberData(nameof(EveryChange))]
    public async Task EveryChange_WithWhy_Succeeds(string path, string body)
    {
        // The same bodies with a why: proves each refusal above was the why, and nothing else.
        await AddMailbox();
        await Post(P + "/rules/allow/add", new { from = "kept@example.org", what = "kept", @do = "archive", why = "seed" });

        var (status, _) = await Send("POST", P + path, Person, body.Replace("{why}", ",\"why\":\"because\""));

        Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Created, $"{path} was answered {status}");
    }

    // ======================================================================================================
    // The rules
    // ======================================================================================================

    [Fact]
    public async Task RulesAdd_EveryList_AppendsSavesANewVersionAndReadsBackInItsOneSpelling()
    {
        // Act
        var allow = await Post(P + "/rules/allow/add",
            new { from = "Billing@Example.NET", what = "invoices", @do = new { route = new { factory = "books", seat = "clerk" } }, why = "the bills" });
        var block = await Post(P + "/rules/block/add", new { from = "*@spammy-domain.example", why = "cold sales, never answered" });
        var rule = await Post(P + "/rules/rule/add",
            new { name = "citizenship", when = new { subjectContains = new[] { "citizen" } }, route = new { factory = "citizenship", seat = "intake" }, why = "the case" });
        var route = await Post(P + "/rules/route/add", new { factory = "devthrottle", seat = "mail-desk", handles = "customers of DevThrottle", why = "support" });
        var read = (await Get(P + "/rules")).Body;

        // Assert
        Assert.Equal(new[] { 1, 2, 3, 4 }, new[] { allow, block, rule, route }.Select(a => a.Body.GetProperty("version").GetInt32()));
        var change = allow.Body.GetProperty("change");
        Assert.Equal(("allow", "add", "billing@example.net", 0, 1),
            (change.GetProperty("kind").GetString(), change.GetProperty("action").GetString(), change.GetProperty("subject").GetString(),
             change.GetProperty("versionBefore").GetInt32(), change.GetProperty("versionAfter").GetInt32()));
        Assert.Equal("books", change.GetProperty("detail").GetProperty("do").GetProperty("route").GetProperty("factory").GetString());
        Assert.Equal("cold sales, never answered", block.Body.GetProperty("change").GetProperty("why").GetString());
        Assert.Equal("devthrottle/mail-desk", route.Body.GetProperty("change").GetProperty("subject").GetString());

        Assert.Equal(4, read.GetProperty("version").GetInt32());
        Assert.Equal(PersonId, read.GetProperty("savedBy").GetString());
        var document = read.GetProperty("document");
        Assert.Equal("billing@example.net", document.GetProperty("allow")[0].GetProperty("from").GetString());
        Assert.Equal("cold sales, never answered", document.GetProperty("block")[0].GetProperty("why").GetString());
        Assert.Equal("citizenship", document.GetProperty("rules")[0].GetProperty("name").GetString());
        Assert.Equal("customers of DevThrottle", document.GetProperty("routes")[0].GetProperty("handles").GetString());
    }

    [Fact]
    public async Task RulesRead_AnOlderVersionAndVersionZero_ReadBackAsTheyWere()
    {
        // Arrange
        await Post(P + "/rules/block/add", new { from = "*@spammy-domain.example", why = "cold sales" });
        await Post(P + "/rules/block/add", new { from = "*@other.example", why = "more cold sales" });

        // Act
        var one = (await Get(P + "/rules?version=1")).Body;
        var zero = (await Get(P + "/rules?version=0")).Body;
        var missing = await Get(P + "/rules?version=9");

        // Assert
        Assert.Equal(1, one.GetProperty("document").GetProperty("block").GetArrayLength());
        Assert.Equal(0, zero.GetProperty("version").GetInt32());
        Assert.Equal(0, zero.GetProperty("document").GetProperty("block").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, zero.GetProperty("savedUtc").ValueKind);
        Assert.Equal((HttpStatusCode.NotFound, "no_such_version"), (missing.Status, Error(missing.Body)));
    }

    [Fact]
    public async Task RulesRemove_AnEntryThatIsThere_SavesTheDocumentWithoutItAndRecordsWhatWasRemoved()
    {
        // Arrange
        await Post(P + "/rules/route/add", new { factory = "devthrottle", seat = "mail-desk", handles = "customers", why = "support" });

        // Act
        var (status, body) = await Post(P + "/rules/route/remove", new { key = "DevThrottle/Mail-Desk", why = "handled elsewhere now" });

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, body.GetProperty("version").GetInt32());
        var change = body.GetProperty("change");
        Assert.Equal(("route", "remove", "devthrottle/mail-desk"),
            (change.GetProperty("kind").GetString(), change.GetProperty("action").GetString(), change.GetProperty("subject").GetString()));
        Assert.Equal("customers", change.GetProperty("detail").GetProperty("handles").GetString());
        Assert.Equal(0, (await Get(P + "/rules")).Body.GetProperty("document").GetProperty("routes").GetArrayLength());
    }

    [Fact]
    public async Task RulesRemove_NoSuchEntry_Answers404AndRecordsNothing()
    {
        await Post(P + "/rules/block/add", new { from = "*@spammy-domain.example", why = "cold sales" });
        var before = Recorded();

        var answer = await Post(P + "/rules/block/remove", new { key = "nobody@example.com", why = "tidy" });

        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.NotFound, "no_such_entry", before);
    }

    [Fact]
    public async Task RulesAdd_APatternAlreadyOnTheOtherList_Answers400WithTheValidatorsSentenceAndRecordsNothing()
    {
        // Arrange
        await Post(P + "/rules/block/add", new { from = "news@example.org", why = "too much" });
        var before = Recorded();

        // Act
        var answer = await Post(P + "/rules/allow/add", new { from = "News@Example.org", what = "a newsletter", @do = "archive", why = "I read it" });

        // Assert
        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.BadRequest, "invalid_rules", before);
        Assert.Contains("both the allow-list and the block list", answer.Body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task RulesAdd_AFieldTheEntryDoesNotHave_Answers400AndRecordsNothing()
    {
        var before = Recorded();

        var answer = await Post(P + "/rules/rule/add",
            new { name = "x", when = new { subjectContain = new[] { "citizen" } }, route = new { factory = "a", seat = "b" }, why = "typo" });

        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.BadRequest, "bad_entry", before);
    }

    [Fact]
    public async Task RulesAdd_NotAList_Answers404NamingTheListsAndRecordsNothing()
    {
        var before = Recorded();

        var answer = await Post(P + "/rules/friends/add", new { from = "a@example.com", why = "x" });

        AssertRefusedAndNothingRecorded(answer, HttpStatusCode.NotFound, "no_such_list", before);
        Assert.Contains("'allow', 'block', 'rule' or 'route'", answer.Body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task RulesHistory_KindAndN_FilterNewestFirstAndAnUnknownKindIs400()
    {
        // Arrange
        await AddMailbox();
        _now = _now.AddMinutes(1);
        await Post(P + "/rules/block/add", new { from = "*@a.example", why = "one" });
        _now = _now.AddMinutes(1);
        await Post(P + "/rules/block/add", new { from = "*@b.example", why = "two" });

        // Act
        var blocks = (await Get(P + "/rules/history?kind=block&n=1")).Body;
        var bad = await Get(P + "/rules/history?kind=friend");

        // Assert
        Assert.Equal((1, 2), (blocks.GetProperty("count").GetInt32(), blocks.GetProperty("total").GetInt32()));
        Assert.Equal("*@b.example", blocks.GetProperty("changes")[0].GetProperty("subject").GetString());
        Assert.Equal((HttpStatusCode.BadRequest, "bad_filter"), (bad.Status, Error(bad.Body)));
        Assert.Contains("'mode'", bad.Body.GetProperty("detail").GetString());
    }

    // ---------- another writer saved first ----------

    /// <summary>Parts whose newest-rules read lets a rival writer save a block entry first, <paramref name="races"/> times.</summary>
    private Func<MailDeskEndpoints.Parts, MailDeskEndpoints.Parts> RivalSavesFirst(int races)
    {
        var raced = 0;
        return parts => parts with
        {
            ReadNewestRules = tenant =>
            {
                var read = _rules.Newest(tenant);
                if (raced < races)
                {
                    raced++;
                    var pattern = $"rival{raced}@example.net";
                    _rules.Save(tenant, read.Version, read.Document.WithBlock(new MailDeskBlockEntry { From = pattern, Why = "a rival" }),
                        new MailDeskChange(MailDeskWords.ChangeKinds.Block, MailDeskWords.ChangeActions.Add, pattern, null,
                            MailDeskWho.Person("rival-device"), "a rival writer"), _now);
                }
                // The version read BEFORE the rival saved: the route's save is made on it, and is stale.
                return read;
            },
        };
    }

    [Fact]
    public async Task RulesAdd_AnotherWriterSavedFirstOnce_MakesTheChangeAgainOnTheNewestAndKeepsBoth()
    {
        // Arrange
        await StartAsync(RivalSavesFirst(1));

        // Act
        var (status, body) = await Post(P + "/rules/allow/add", new { from = "news@example.org", what = "a newsletter", @do = "archive", why = "I read it" });

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, body.GetProperty("version").GetInt32());
        Assert.Equal(1, body.GetProperty("change").GetProperty("versionBefore").GetInt32());
        var document = _rules.Newest(A).Document;
        Assert.Equal("rival1@example.net", Assert.Single(document.Block).From);
        Assert.Equal("news@example.org", Assert.Single(document.Allow).From);
    }

    [Fact]
    public async Task RulesAdd_AnotherWriterSavesFirstEveryTime_Answers409RulesConflictAndRecordsOnlyTheRivals()
    {
        // Arrange
        await StartAsync(RivalSavesFirst(MailDeskEndpoints.RulesSaveAttempts));

        // Act
        var answer = await Post(P + "/rules/allow/add", new { from = "news@example.org", what = "a newsletter", @do = "archive", why = "I read it" });

        // Assert
        Assert.Equal((HttpStatusCode.Conflict, "rules_conflict"), (answer.Status, Error(answer.Body)));
        var newest = _rules.Newest(A);
        Assert.Equal(MailDeskEndpoints.RulesSaveAttempts, newest.Version);
        Assert.Empty(newest.Document.Allow);
        Assert.All(_rules.History(A).Changes, c => Assert.Equal("a rival writer", c.Why));
    }

    // ======================================================================================================
    // The channel row's fold
    // ======================================================================================================

    private async Task<JsonElement> OnlyRow() => (await Get(P + "/channels")).Body.GetProperty("channels")[0];

    [Fact]
    public async Task Fold_NoMailDeskTrigger_SignedInUnknownAndLastCheckSaysTheFactoryIsNotPublished()
    {
        // Arrange - a trigger of another factory is no Mail Desk start
        await AddMailbox();
        _triggers.Add(new TriggerEntity { Name = "other", Factory = "invoices", CheckPath = MailDeskTemplate.CheckPath, LastCheckUtc = _now, LastOutcome = TriggerRunOutcome.Failed });

        // Act
        var row = await OnlyRow();

        // Assert
        Assert.Equal(MailDeskChannelFold.SignedInStates.Unknown, row.GetProperty("signedIn").GetProperty("state").GetString());
        var lastCheck = row.GetProperty("lastCheck");
        Assert.Equal(JsonValueKind.Null, lastCheck.GetProperty("utc").ValueKind);
        Assert.Equal(JsonValueKind.Null, lastCheck.GetProperty("outcome").ValueKind);
        Assert.Equal("Not checked yet - the mail-desk factory is not published", lastCheck.GetProperty("label").GetString());
    }

    [Fact]
    public async Task Fold_TriggerNeverChecked_SignedInUnknownAndLastCheckSaysSo()
    {
        await AddMailbox();
        _triggers.Add(MailDeskTrigger());

        var row = await OnlyRow();

        Assert.Equal(MailDeskChannelFold.SignedInStates.Unknown, row.GetProperty("signedIn").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("lastCheck").GetProperty("utc").ValueKind);
        Assert.Equal(MailDeskChannelFold.NeverCheckedLabel, row.GetProperty("lastCheck").GetProperty("label").GetString());
    }

    [Fact]
    public async Task Fold_LastCheckSucceeded_SignedInYesAndLastCheckCarriesTheTimeAndOutcome()
    {
        // Arrange
        await AddMailbox();
        _timeZone = "Europe/Copenhagen";
        _triggers.Add(MailDeskTrigger(new DateTime(2026, 10, 18, 8, 45, 0, DateTimeKind.Utc), TriggerRunOutcome.NothingToDo));

        // Act
        var row = await OnlyRow();

        // Assert
        Assert.Equal(MailDeskChannelFold.SignedInStates.Yes, row.GetProperty("signedIn").GetProperty("state").GetString());
        var lastCheck = row.GetProperty("lastCheck");
        Assert.Equal(new DateTime(2026, 10, 18, 8, 45, 0, DateTimeKind.Utc), lastCheck.GetProperty("utc").GetDateTime().ToUniversalTime());
        Assert.Equal(TriggerRunOutcome.NothingToDo, lastCheck.GetProperty("outcome").GetString());
        // 08:45 UTC is 10:45 in Copenhagen in October, before the clocks go back.
        Assert.Equal("Checked 2026-10-18 10:45: no new mail", lastCheck.GetProperty("label").GetString());
    }

    [Fact]
    public async Task Fold_LastCheckFailed_SignedInFailingQuotingTheChecksOwnReason()
    {
        // Arrange
        await AddMailbox();
        const string reason = "email@devthrottle.com is not signed in on devlinux: run cc-outlook -a support auth";
        _triggers.Add(MailDeskTrigger(_now.AddMinutes(-5), TriggerRunOutcome.Failed, reason));

        // Act
        var row = await OnlyRow();

        // Assert
        var signedIn = row.GetProperty("signedIn");
        Assert.Equal(MailDeskChannelFold.SignedInStates.Failing, signedIn.GetProperty("state").GetString());
        Assert.Contains(reason, signedIn.GetProperty("label").GetString());
        Assert.Contains(reason, row.GetProperty("lastCheck").GetProperty("label").GetString());
    }

    [Fact]
    public async Task Fold_TodayInATimeZoneThatIsNotUtc_CountsOnlyDecisionsSinceLocalMidnight()
    {
        // Arrange - Copenhagen is two hours ahead of UTC on 18 October 2026, so its day began at 22:00 UTC on the 17th.
        var channel = Channel();
        Decide(channel, "before-local-midnight", new DateTime(2026, 10, 17, 21, 30, 0, DateTimeKind.Utc));
        Decide(channel, "after-local-midnight", new DateTime(2026, 10, 17, 22, 30, 0, DateTimeKind.Utc), decision: MailDeskWords.Decisions.Junk,
            layer: MailDeskWords.Layers.Block);
        Decide(channel, "this-morning", new DateTime(2026, 10, 18, 7, 0, 0, DateTimeKind.Utc));

        // Act
        _timeZone = "Europe/Copenhagen";
        var copenhagen = (await Get(P + "/status")).Body;
        _timeZone = "UTC";
        var utc = (await Get(P + "/status")).Body;

        // Assert
        var row = copenhagen.GetProperty("channels")[0].GetProperty("today");
        Assert.Equal(2, row.GetProperty("total").GetInt32());
        Assert.Equal((1, 1), (row.GetProperty("byDecision").GetProperty("junk").GetInt32(), row.GetProperty("byDecision").GetProperty("archive").GetInt32()));
        Assert.Equal(("2026-10-18", "Europe/Copenhagen", 2),
            (copenhagen.GetProperty("today").GetProperty("date").GetString(), copenhagen.GetProperty("today").GetProperty("timeZone").GetString(),
             copenhagen.GetProperty("today").GetProperty("total").GetInt32()));
        Assert.Equal(1, utc.GetProperty("today").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Status_AChannelRemovedToday_StillCountsItsDecisionsInTheDayAndAgreesWithDecisions()
    {
        // Arrange - the support mailbox junks one email and archives one this morning, then is removed; the other
        // mailbox, still active, archives one. Removing a channel keeps its decisions, so the day keeps them too.
        var support = Channel();
        var other = Channel(OtherMailbox);
        Decide(support, "junked", _now.AddHours(-3), MailDeskWords.Layers.Block, MailDeskWords.Decisions.Junk);
        Decide(support, "archived", _now.AddHours(-2));
        Decide(other, "still-active", _now.AddHours(-1));
        var (removedStatus, _) = await Post(P + "/channels/remove", new { address = Mailbox, why = "the mailbox was closed" });
        Assert.Equal(HttpStatusCode.OK, removedStatus);

        // Act
        var status = (await Get(P + "/status")).Body;
        var decisions = (await Get(P + "/decisions?since=2026-10-18T00:00:00Z")).Body;

        // Assert - the channel list is the active one, the day is the account's.
        Assert.Equal(OtherMailbox, status.GetProperty("channels").EnumerateArray().Single().GetProperty("address").GetString());
        var today = status.GetProperty("today");
        Assert.Equal(3, today.GetProperty("total").GetInt32());
        Assert.Equal((1, 2), (today.GetProperty("byDecision").GetProperty("junk").GetInt32(), today.GetProperty("byDecision").GetProperty("archive").GetInt32()));
        Assert.Equal(decisions.GetProperty("total").GetInt32(), today.GetProperty("total").GetInt32());
        Assert.Equal(decisions.GetProperty("byDecision").GetProperty("junk").GetInt32(), today.GetProperty("byDecision").GetProperty("junk").GetInt32());
    }

    [Fact]
    public void Today_JustBeforeAndAfterLocalMidnight_IsTheLocalDate()
    {
        // 21:59 UTC on the 17th is 23:59 in Copenhagen; 22:00 UTC is midnight, the 18th.
        var before = MailDeskChannelFold.Today("Europe/Copenhagen", new DateTime(2026, 10, 17, 21, 59, 0, DateTimeKind.Utc));
        var after = MailDeskChannelFold.Today("Europe/Copenhagen", new DateTime(2026, 10, 17, 22, 0, 0, DateTimeKind.Utc));

        Assert.Equal(("2026-10-17", new DateTime(2026, 10, 16, 22, 0, 0), new DateTime(2026, 10, 17, 22, 0, 0)), (before.Date, before.FromUtc, before.ToUtc));
        Assert.Equal(("2026-10-18", new DateTime(2026, 10, 17, 22, 0, 0)), (after.Date, after.FromUtc));
        // The day the clocks go back is 25 hours long.
        var longDay = MailDeskChannelFold.Today("Europe/Copenhagen", new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(TimeSpan.FromHours(25), longDay.ToUtc - longDay.FromUtc);
    }

    // ======================================================================================================
    // The decisions
    // ======================================================================================================

    [Fact]
    public async Task Decisions_EveryFilter_AppliesAndTheRowCarriesTheChannelAndCostInDollars()
    {
        // Arrange
        var support = Channel();
        var other = Channel(OtherMailbox);
        Decide(support, "m1", _now.AddHours(-3), MailDeskWords.Layers.Allow, MailDeskWords.Decisions.Archive);
        Decide(support, "m2", _now.AddHours(-2), MailDeskWords.Layers.Block, MailDeskWords.Decisions.Junk);
        Decide(support, "m3", _now.AddHours(-1), MailDeskWords.Layers.RouteModel, MailDeskWords.Decisions.Route, costMicros: 1250);
        Decide(other, "m4", _now.AddMinutes(-30), MailDeskWords.Layers.Allow, MailDeskWords.Decisions.Archive);

        // Act
        var all = (await Get(P + "/decisions")).Body;
        var byLayer = (await Get(P + "/decisions?layer=block")).Body;
        var byDecision = (await Get(P + "/decisions?decision=route")).Body;
        var byChannel = (await Get(P + "/decisions?channel=Support@Example.com")).Body;
        var since = (await Get(P + "/decisions?since=" + Uri.EscapeDataString(_now.AddMinutes(-90).ToString("o")))).Body;
        var newest = (await Get(P + "/decisions?n=1")).Body;

        // Assert
        Assert.Equal(new[] { "m4", "m3", "m2", "m1" }, all.GetProperty("decisions").EnumerateArray().Select(d => d.GetProperty("messageKey").GetString()));
        Assert.Equal(2, all.GetProperty("byDecision").GetProperty("archive").GetInt32());
        Assert.Equal("m2", Assert.Single(byLayer.GetProperty("decisions").EnumerateArray()).GetProperty("messageKey").GetString());
        var routed = Assert.Single(byDecision.GetProperty("decisions").EnumerateArray());
        Assert.Equal((Mailbox, 0.00125m, "books", "clerk"),
            (routed.GetProperty("channel").GetString(), routed.GetProperty("costUsd").GetDecimal(),
             routed.GetProperty("route").GetProperty("factory").GetString(), routed.GetProperty("route").GetProperty("seat").GetString()));
        Assert.Equal("m4", Assert.Single(byChannel.GetProperty("decisions").EnumerateArray()).GetProperty("messageKey").GetString());
        Assert.Equal(new[] { "m4", "m3" }, since.GetProperty("decisions").EnumerateArray().Select(d => d.GetProperty("messageKey").GetString()));
        Assert.Equal((1, 4), (newest.GetProperty("count").GetInt32(), newest.GetProperty("total").GetInt32()));
        Assert.Equal(JsonValueKind.Null, all.GetProperty("decisions")[0].GetProperty("route").ValueKind);
        Assert.Equal(JsonValueKind.Null, all.GetProperty("decisions")[0].GetProperty("costUsd").ValueKind);
    }

    [Theory]
    [InlineData("layer=spam", "'sender-screen'")]
    [InlineData("decision=delete", "'keep-for-owner'")]
    [InlineData("since=yesterday", "ISO time")]
    [InlineData("n=0", "1 to 500")]
    [InlineData("channel=*@example.com", "not an email address")]
    public async Task Decisions_ABadFilter_Answers400NamingWhatIsValid(string query, string named)
    {
        var (status, body) = await Get(P + "/decisions?" + query);

        Assert.Equal((HttpStatusCode.BadRequest, "bad_filter"), (status, Error(body)));
        Assert.Contains(named, body.GetProperty("detail").GetString());
    }

    // ======================================================================================================
    // One account never sees another's
    // ======================================================================================================

    [Fact]
    public async Task TenantIsolation_AnotherAccount_SeesNoneOfThisAccountsChannelsRulesOrDecisions_AndCannotChangeThem()
    {
        // Arrange
        await AddMailbox();
        await Post(P + "/rules/block/add", new { from = "*@spammy-domain.example", why = "cold sales" });
        Decide(_channels.FindActive(A, Mailbox)!, "m1", _now.AddMinutes(-1));
        var beforeA = Recorded(A);

        // Act
        var channels = (await Get(P + "/channels?all=true", account: B)).Body;
        var rules = (await Get(P + "/rules", account: B)).Body;
        var history = (await Get(P + "/rules/history", account: B)).Body;
        var decisions = (await Get(P + "/decisions", account: B)).Body;
        var remove = await Post(P + "/channels/remove", new { address = Mailbox, why = "not mine" }, account: B);
        var unblock = await Post(P + "/rules/block/remove", new { key = "*@spammy-domain.example", why = "not mine" }, account: B);
        var addSame = await AddMailbox(account: B);

        // Assert
        Assert.Equal(0, channels.GetProperty("count").GetInt32());
        Assert.Equal(0, rules.GetProperty("version").GetInt32());
        Assert.Equal(0, history.GetProperty("total").GetInt32());
        Assert.Equal(0, decisions.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, remove.Status);
        Assert.Equal(HttpStatusCode.NotFound, unblock.Status);
        // The same mailbox may be a channel of two accounts; each sees only its own.
        Assert.Equal(HttpStatusCode.Created, addSame.Status);
        Assert.Equal(beforeA, Recorded(A));
    }

    // ======================================================================================================
    // The switch off: every route answers 404 with no body
    // ======================================================================================================

    [Fact]
    public async Task EveryRoute_AccountOff_Answers404WithNoBodyAndRecordsNothing()
    {
        // Arrange
        await AddMailbox();
        _switch.Set(A, false, "email@devthrottle.com", "switched off for the test", _now);
        var before = Recorded();
        var routes = ((IEndpointRouteBuilder)_app!).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith(P, StringComparison.Ordinal)).ToList();
        Assert.Equal(12, routes.Count); // phase 1's ten, and phase 3's GET and POST /settings

        // Act + Assert
        foreach (var endpoint in routes)
        {
            var method = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single();
            var path = endpoint.RoutePattern.RawText!.Replace("{list}", "allow");
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            request.Headers.Add(TenantHeader, A.Value);
            request.Headers.Add(CallerHeader, Person);
            if (method == "POST")
                request.Content = new StringContent("""{"address":"email@devthrottle.com","key":"x","why":"y"}""", Encoding.UTF8, "application/json");
            using var response = await _client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.NotFound && text.Length == 0,
                $"{method} {path} answered {(int)response.StatusCode} '{text}' with Mail Desk off");
        }
        Assert.Equal(before, Recorded());
    }

    // ======================================================================================================
    // A session's key reaches every route in production
    // ======================================================================================================

    [Fact]
    public void SessionKeyGuard_EveryMappedMailDeskRoute_IsAllowedToASessionKey()
    {
        var routes = ((IEndpointRouteBuilder)_app!).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith(P, StringComparison.Ordinal)).ToList();
        Assert.Equal(12, routes.Count); // phase 1's ten, and phase 3's GET and POST /settings
        foreach (var endpoint in routes)
        {
            var method = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single();
            foreach (var list in MailDeskEndpoints.Lists)
            {
                var path = endpoint.RoutePattern.RawText!.Replace("{list}", list);
                Assert.True(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} is mapped and the session key guard refuses it");
            }
        }
    }

    [Theory]
    [InlineData("DELETE", "/gateway/mail-desk/channels")]
    [InlineData("PUT", "/gateway/mail-desk/rules")]
    [InlineData("POST", "/gateway/mail-desk/status")]
    [InlineData("GET", "/gateway/mail-desk/channels/remove")]
    [InlineData("POST", "/gateway/mail-desk/rules")]
    [InlineData("GET", "/gateway/mail-desk/rules/allow/add")]
    [InlineData("POST", "/gateway/mail-desk/rules/allow/rename")]
    [InlineData("GET", "/gateway/mail-desk")]
    [InlineData("GET", "/gateway/mail-desk/anything")]
    public void SessionKeyGuard_ARouteThatIsNotMapped_IsRefused(string method, string path)
        => Assert.False(SessionKeyGuard.Check(method, path).Allowed);
}
