using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory.Inbox;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Inbox;

/// <summary>
/// THE FACTORY INBOX ROUTES (Factory Inbox mission, phase 1), booted on an in-memory test server exactly as GatewayHost maps
/// them, over the real store and a throwaway database. A stand-in for the authentication middleware puts on each request
/// the identity the real one would: the owner's phone or browser, a session's key, or the machine token.
///
/// These pin what only the routes know: the send is the owner's alone; an unknown, archived or Boss-less factory is
/// refused in words; only a factory's Secretary files and only its Boss (started as itself) decides; DELEGATED IS REFUSED
/// WITHOUT A SESSION THAT BOSS STARTED; a claim held by an ended session is taken by the next Secretary through the routes;
/// and every state word and trail line arrives finished.
/// </summary>
public sealed class FactoryInboxEndpointTests : IAsyncLifetime
{
    private const string TenantHeader = "X-Test-Account";
    private const string CallerHeader = "X-Test-Caller";
    private const string Phone = "phone";
    private const string Browser = "browser";
    private const string MachineToken = "machine";
    private const string Secretary1 = "11111111-1111-4111-8111-111111111111";
    private const string Secretary2 = "22222222-2222-4222-8222-222222222222";
    private const string Boss = "33333333-3333-4333-8333-333333333333";
    private const string BossChild = "44444444-4444-4444-8444-444444444444";
    private const string Stranger = "55555555-5555-4555-8555-555555555555";
    private const string OtherFactorySession = "66666666-6666-4666-8666-666666666666";
    /// <summary>The Boss a session in no factory started (issue #4598): rules-only kit, recorded as started as the Boss.</summary>
    private const string OwnersBoss = "77777777-7777-4777-8777-777777777777";
    private static readonly TenantId A = new("acct-a");

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase _db = null!;
    private FactoryInboxStore _store = null!;
    private WebApplication? _app;
    private HttpClient _client = null!;
    private DateTime _now = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
    private readonly HashSet<string> _ended = new(StringComparer.Ordinal);
    // What the routes told the wake (phase 2): each logged message, and each handed bundle.
    private readonly List<(TenantId Tenant, string Factory, DateTime SentAtUtc)> _sentToWake = new();
    private readonly List<(TenantId Tenant, string Factory)> _bundlesToWake = new();
    private FactoryInboxWakeStore _wakes = null!;

    private readonly Dictionary<string, SessionFactoryLookup> _seats = new(StringComparer.Ordinal)
    {
        [Secretary1] = SessionFactoryLookup.In("invoices", FactoryInboxSeats.Secretary) with { Kit = FactoryKits.Seat },
        [Secretary2] = SessionFactoryLookup.In("invoices", FactoryInboxSeats.Secretary) with { Kit = FactoryKits.Seat },
        [Boss] = SessionFactoryLookup.In("invoices", "boss", 3) with { Kit = FactoryKits.Seat },
        // A child of the Boss carries the Boss seat with a rules-only kit: it is not the Boss.
        [BossChild] = SessionFactoryLookup.In("invoices", "boss", 3) with { Kit = FactoryKits.RulesOnly },
        [Stranger] = SessionFactoryLookup.InNoFactory,
        [OtherFactorySession] = SessionFactoryLookup.In("payroll", "boss", 1) with { Kit = FactoryKits.Seat },
        [OwnersBoss] = SessionFactoryLookup.In("invoices", "boss", 3) with { Kit = FactoryKits.RulesOnly, StartedAsTheBoss = true },
    };

    // The sessions the Gateway has a history row for, and who started each; a session missing here has no row yet.
    private readonly Dictionary<string, string?> _parents = new(StringComparer.Ordinal)
    {
        [BossChild] = Boss,
        [Secretary1] = Stranger,
        [Secretary2] = null,
    };

    public async Task InitializeAsync()
    {
        _db = _h.Open();
        _store = new FactoryInboxStore(_db, FactoryInboxStoreTests.DictionaryOf);
        _wakes = new FactoryInboxWakeStore(_db);
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
        FactoryInboxEndpoints.Map(app, new FactoryInboxEndpoints.Parts(
            _store, Resolve, _ => Factories(), (_, sid) => _seats.TryGetValue(sid, out var l) ? l : SessionFactoryLookup.NotKnown,
            (_, sid) => _parents.TryGetValue(sid, out var p) ? new FactoryInboxRecordedParent(p) : null, (_, sid) => _ended.Contains(sid), () => _now,
            _wakes, (t, f, at) => _sentToWake.Add((t, f, at)), (t, f) => _bundlesToWake.Add((t, f)), (_, _) => []));
        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        _h.Dispose();
    }

    private static List<RegisteredFactoryDto> Factories() =>
    [
        new() { Factory = "invoices", Title = "Invoices", BossSeat = "boss" },
        new() { Factory = "payroll", Title = "Payroll", BossSeat = "boss" },
        new() { Factory = "old-shop", Title = "Old shop", BossSeat = "boss", ArchivedAtUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) },
        new() { Factory = "no-boss", Title = "No boss", BossSeat = null },
    ];

    private static readonly Func<HttpContext, TenantId?> Resolve = ctx =>
        ctx.Request.Headers.TryGetValue(TenantHeader, out var v) && !string.IsNullOrWhiteSpace(v) ? new TenantId(v.ToString()) : null;

    private static void StandInForAuthentication(HttpContext ctx)
    {
        var caller = ctx.Request.Headers[CallerHeader].ToString();
        var tenant = Resolve(ctx) ?? A;
        if (caller is Phone or Browser)
        {
            ctx.Items[AuthMiddleware.DeviceTypeItemKey] = caller;
            ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity($"owner-{caller}-1", tenant.Value, caller, "active");
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

    private async Task<(HttpStatusCode Status, JsonElement Body)> Call(string method, string path, string caller, object? body = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(TenantHeader, A.Value);
        request.Headers.Add(CallerHeader, caller);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Get(string path, string caller) => Call("GET", path, caller);
    private Task<(HttpStatusCode Status, JsonElement Body)> Post(string path, string caller, object? body = null) => Call("POST", path, caller, body ?? new { });

    private async Task<string> Leave(string text, string factory = "invoices", string caller = Phone)
    {
        var (status, body) = await Post($"/gateway/factory/{factory}/inbox", caller, new { text });
        Assert.Equal(HttpStatusCode.Created, status);
        return body.GetProperty("message").GetProperty("id").GetString()!;
    }

    private async Task<string> LeaveFiledAndHanded(string text)
    {
        var id = await Leave(text);
        Assert.Equal(HttpStatusCode.OK, (await Post($"/sessions/{Secretary1}/factory-inbox/receive", Secretary1)).Status);
        Assert.Equal(HttpStatusCode.Created, (await Post($"/sessions/{Secretary1}/factory-inbox/bundle", Secretary1)).Status);
        return id;
    }

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    // ---------- the owner's send ----------

    [Fact]
    public async Task Send_FromThePhone_IsLoggedWithItsTrail()
    {
        var (status, body) = await Post("/gateway/factory/invoices/inbox", Phone, new { text = "Check the totals", note = "bad" });

        Assert.Equal(HttpStatusCode.Created, status);
        var message = body.GetProperty("message");
        Assert.Equal("sent", message.GetProperty("state").GetString());
        Assert.Equal("Sent", message.GetProperty("stateLabel").GetString());
        Assert.Equal("Bad", message.GetProperty("noteLabel").GetString());
        Assert.Equal("phone", message.GetProperty("surface").GetString());
        var step = Assert.Single(message.GetProperty("trail").EnumerateArray());
        Assert.Equal("Sent from the phone", step.GetProperty("label").GetString());
    }

    [Fact]
    public async Task Send_FromTheCockpit_IsMarkedCockpit()
    {
        var (_, body) = await Post("/gateway/factory/invoices/inbox", Browser, new { text = "Hello" });
        Assert.Equal("cockpit", body.GetProperty("message").GetProperty("surface").GetString());
    }

    [Fact]
    public async Task Send_WithASessionKey_IsRefusedAndNothingIsLogged()
    {
        var (status, body) = await Post("/gateway/factory/invoices/inbox", Boss, new { text = "as the owner" });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("session_key_out_of_scope", Code(body));
        Assert.Equal(0, _store.Thread(A, "invoices", 10).Total);
    }

    [Fact]
    public async Task Send_WithTheMachineToken_IsRefused()
    {
        var (status, body) = await Post("/gateway/factory/invoices/inbox", MachineToken, new { text = "hi" });
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("owner_device_required", Code(body));
    }

    [Theory]
    [InlineData("nowhere", HttpStatusCode.NotFound, "factory_not_found")]
    [InlineData("old-shop", HttpStatusCode.Conflict, "factory_archived")]
    [InlineData("no-boss", HttpStatusCode.Conflict, "factory_has_no_boss")]
    public async Task Send_ToAFactoryThatCannotTakeIt_IsRefusedInWords(string factory, HttpStatusCode expected, string code)
    {
        var (status, body) = await Post($"/gateway/factory/{factory}/inbox", Phone, new { text = "hi" });

        Assert.Equal(expected, status);
        Assert.Equal(code, Code(body));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Send_SpokenRawTextOnly_GetsTheAccountsListedCorrectionsAndNoOthers()
    {
        var (status, body) = await Post("/gateway/factory/invoices/inbox", Phone, new
        {
            source = "spoken",
            rawText = "Do not ask dev throttle now",
        });

        Assert.Equal(HttpStatusCode.Created, status);
        var message = body.GetProperty("message");
        Assert.Equal("Do not ask dev throttle now", message.GetProperty("rawText").GetString());
        Assert.Equal("Do not ask DevThrottle now", message.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Send_WithCorrectionsFromTheClient_IsRefusedInWords()
    {
        // The review's case: an unlisted pair that would turn "not" into "now".
        var (status, body) = await Post("/gateway/factory/invoices/inbox", Phone, new
        {
            source = "spoken",
            rawText = "do not deploy",
            corrections = new[] { new { find = "not", replace = "now" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("corrections_not_accepted", Code(body));
        Assert.Equal(0, _store.Thread(A, "invoices", 10).Total);
    }

    [Fact]
    public async Task Send_ACorrectionWithNoFind_IsRefusedInWords_NotAnInternalError()
    {
        // The review's reproduction of finding 1: this body answered 500 before.
        var (status, body) = await Post("/gateway/factory/invoices/inbox", Phone, new
        {
            text = "Hello",
            source = "spoken",
            rawText = "Hello",
            corrections = new[] { new { replace = "x" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("corrections_not_accepted", Code(body));
    }

    [Fact]
    public async Task Send_SpokenThatAlsoCarriesText_IsRefused()
    {
        var (status, body) = await Post("/gateway/factory/invoices/inbox", Phone, new
        {
            text = "do now deploy",
            source = "spoken",
            rawText = "do not deploy",
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("spoken_with_text", Code(body));
    }

    // ---------- the owner's reads ----------

    [Fact]
    public async Task List_ShowsEveryFactoryWithABoss_AndItsWaitingCount()
    {
        await Leave("One");
        _now = _now.AddSeconds(30);
        await Leave("Two");

        var (status, body) = await Get("/gateway/factory/inbox", Browser);

        Assert.Equal(HttpStatusCode.OK, status);
        var factories = body.GetProperty("factories").EnumerateArray().ToList();
        Assert.Equal(["invoices", "payroll"], factories.Select(f => f.GetProperty("factory").GetString()));
        Assert.Equal(2, body.GetProperty("count").GetInt32());
        Assert.Equal(2, factories[0].GetProperty("waiting").GetInt32());
        Assert.Equal("Two", factories[0].GetProperty("lastMessage").GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, factories[1].GetProperty("lastMessage").ValueKind);
    }

    [Fact]
    public async Task List_AndSend_AgreeOnEveryFactory()
    {
        // Every factory the list shows takes a message; every registered factory it leaves out (archived with a Boss
        // seat, as warmforward and ten more were on the live account, or Boss-less) is refused in words at the send.
        var (_, body) = await Get("/gateway/factory/inbox", Browser);
        var listed = body.GetProperty("factories").EnumerateArray().Select(f => f.GetProperty("factory").GetString()!).ToHashSet();

        foreach (var f in Factories())
        {
            var (status, sent) = await Post($"/gateway/factory/{f.Factory}/inbox", Phone, new { text = "hi" });
            if (listed.Contains(f.Factory))
                Assert.Equal(HttpStatusCode.Created, status);
            else
            {
                Assert.Equal(HttpStatusCode.Conflict, status);
                Assert.False(string.IsNullOrWhiteSpace(sent.GetProperty("error").GetString()));
            }
        }
        Assert.Equal(["invoices", "payroll"], listed.Order());
    }

    [Fact]
    public async Task OwnerReads_ASessionMayReadThem_TheMachineTokenMayNot()
    {
        await Leave("One");

        Assert.Equal(HttpStatusCode.OK, (await Get("/gateway/factory/invoices/inbox/summary", Boss)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Get("/gateway/factory/invoices/todo", Boss)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/gateway/factory/invoices/inbox", MachineToken)).Status);
    }

    [Fact]
    public async Task Thread_ADeclinedMessage_ShowsItsWholeTrailInFinishedWords()
    {
        var id = await LeaveFiledAndHanded("Delete every invoice");
        _now = _now.AddMinutes(5);
        var (decided, _) = await Post($"/sessions/{Boss}/factory-inbox/{id}/decide", Boss, new { kind = "declined", why = "That would lose the records." });
        Assert.Equal(HttpStatusCode.OK, decided);

        var (status, body) = await Get("/gateway/factory/invoices/inbox", Browser);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Invoices", body.GetProperty("title").GetString());
        var message = Assert.Single(body.GetProperty("messages").EnumerateArray());
        Assert.Equal("Declined", message.GetProperty("stateLabel").GetString());
        Assert.Equal(
            ["Sent from the phone", "Received by the Secretary", "Handed to the Boss", "Declined: That would lose the records."],
            message.GetProperty("trail").EnumerateArray().Select(s => s.GetProperty("label").GetString()));
        Assert.Equal("Declined", message.GetProperty("decision").GetProperty("label").GetString());
    }

    [Fact]
    public async Task Summary_SaysWhatCameInAndWhatWasDecided()
    {
        var id = await LeaveFiledAndHanded("One");
        await Leave("Two");
        await Post($"/sessions/{Boss}/factory-inbox/{id}/decide", Boss, new { kind = "noted" });

        var (status, body) = await Get("/gateway/factory/invoices/inbox/summary?since=2026-11-04T08:00:00Z", Browser);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("2 messages came in; 1 decision made (Noted 1); 1 still waiting.", body.GetProperty("line").GetString());
        Assert.Equal(2, body.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Todo_ATodoDecision_IsOnTheFactorysList()
    {
        var id = await LeaveFiledAndHanded("Add an export button");

        var (decided, decideBody) = await Post($"/sessions/{Boss}/factory-inbox/{id}/decide", Boss, new { kind = "todo" });
        var (status, body) = await Get("/gateway/factory/invoices/todo", Browser);

        Assert.Equal(HttpStatusCode.OK, decided);
        Assert.Equal(HttpStatusCode.OK, status);
        var item = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal("Add an export button", item.GetProperty("text").GetString());
        Assert.Equal(decideBody.GetProperty("todo").GetProperty("id").GetString(), item.GetProperty("id").GetString());
        Assert.Equal(1, body.GetProperty("count").GetInt32());
    }

    // ---------- the Secretary ----------

    [Fact]
    public async Task Secretary_ListsReceivesAndHandsABundle()
    {
        var id = await Leave("One");

        var (listed, list) = await Get($"/sessions/{Secretary1}/factory-inbox", Secretary1);
        var (received, receive) = await Post($"/sessions/{Secretary1}/factory-inbox/receive", Secretary1, new { ids = new[] { id } });
        var (handed, bundle) = await Post($"/sessions/{Secretary1}/factory-inbox/bundle", Secretary1);

        Assert.Equal(HttpStatusCode.OK, listed);
        Assert.Equal(1, list.GetProperty("count").GetInt32());
        Assert.Equal(HttpStatusCode.OK, received);
        Assert.Equal("Received", receive.GetProperty("received")[0].GetProperty("stateLabel").GetString());
        Assert.Equal(HttpStatusCode.Created, handed);
        Assert.Equal(1, bundle.GetProperty("bundle").GetProperty("count").GetInt32());
        Assert.Equal("With the Boss", bundle.GetProperty("bundle").GetProperty("items")[0].GetProperty("stateLabel").GetString());
    }

    [Fact]
    public async Task Secretary_AClaimHeldByAnEndedSession_IsTakenByTheNextOne()
    {
        var id = await Leave("One");
        await Post($"/sessions/{Secretary1}/factory-inbox/receive", Secretary1);

        var (_, whileAlive) = await Get($"/sessions/{Secretary2}/factory-inbox", Secretary2);
        Assert.Equal(0, whileAlive.GetProperty("count").GetInt32());

        _ended.Add(Secretary1);
        var (_, afterEnd) = await Get($"/sessions/{Secretary2}/factory-inbox", Secretary2);
        var (status, taken) = await Post($"/sessions/{Secretary2}/factory-inbox/receive", Secretary2);

        Assert.Equal(1, afterEnd.GetProperty("count").GetInt32());
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(id, taken.GetProperty("received")[0].GetProperty("id").GetString());
        Assert.Equal(Secretary2, _store.Get(A, Guid.Parse(id))!.ClaimSessionId);
    }

    [Theory]
    [InlineData(Boss, "not_the_secretary")]
    [InlineData(BossChild, "not_the_secretary")]
    [InlineData(Stranger, "not_in_a_factory")]
    public async Task SecretaryRoutes_AnyoneButTheSecretary_IsRefused(string caller, string code)
    {
        await Leave("One");

        var (status, body) = await Post($"/sessions/{caller}/factory-inbox/receive", caller);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(code, Code(body));
        Assert.Equal(FactoryInboxWords.States.Sent, _store.Thread(A, "invoices", 10).Messages.Single().State);
    }

    [Fact]
    public async Task SessionRoutes_NamingAnotherSession_AreRefused()
    {
        var (status, body) = await Get($"/sessions/{Secretary2}/factory-inbox", Secretary1);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("not_your_session", Code(body));
    }

    [Fact]
    public async Task SessionRoutes_ADeviceKey_IsRefused()
    {
        var (status, body) = await Get($"/sessions/{Secretary1}/factory-inbox", Browser);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("session_key_required", Code(body));
    }

    // ---------- the Boss ----------

    [Fact]
    public async Task Boss_ReadsItsOpenBundles()
    {
        await LeaveFiledAndHanded("One");

        var (status, body) = await Get($"/sessions/{Boss}/factory-inbox/bundle", Boss);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, body.GetProperty("count").GetInt32());
        Assert.Equal("One", body.GetProperty("bundles")[0].GetProperty("items")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Boss_ABossOfAnotherFactory_SeesNoneOfThisFactorysBundles()
    {
        await LeaveFiledAndHanded("One");

        var (status, body) = await Get($"/sessions/{OtherFactorySession}/factory-inbox/bundle", OtherFactorySession);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, body.GetProperty("count").GetInt32());
    }

    [Theory]
    [InlineData(Secretary1)]
    [InlineData(BossChild)]
    public async Task Decide_ByAnyoneButTheBossStartedAsItself_IsRefused(string caller)
    {
        var id = await LeaveFiledAndHanded("One");

        var (status, body) = await Post($"/sessions/{caller}/factory-inbox/{id}/decide", caller, new { kind = "noted" });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("not_the_boss", Code(body));
        Assert.Empty(_store.DecisionsOf(A, [Guid.Parse(id)]));
    }

    [Fact]
    public async Task Decide_ByTheBossASessionInNoFactoryStarted_IsAccepted()
    {
        // The owner's ruling of 6 November 2026, which overturned the Tech Lead's ruling of 5 November 2026 for a Boss the
        // Gateway recorded as started by a session in no factory: it decides the factory inbox's bundles.
        var id = await LeaveFiledAndHanded("One");

        var (status, body) = await Post($"/sessions/{OwnersBoss}/factory-inbox/{id}/decide", OwnersBoss, new { kind = "noted" });

        Assert.True(status == HttpStatusCode.OK, body.ToString());
        Assert.Single(_store.DecisionsOf(A, [Guid.Parse(id)]));
    }

    [Fact]
    public async Task Decide_DelegatedWithoutAStartedSession_IsRefused()
    {
        var id = await LeaveFiledAndHanded("Build the export");

        // A session this Boss did not start - its recorded parent is someone else.
        var (notMine, notMineBody) = await Post($"/sessions/{Boss}/factory-inbox/{id}/decide", Boss, new { kind = "delegated", session = Secretary1 });
        // A session with a history row that names no parent at all.
        var (orphan, orphanBody) = await Post($"/sessions/{Boss}/factory-inbox/{id}/decide", Boss, new { kind = "delegated", session = Secretary2 });
        // No session at all.
        var (none, noneBody) = await Post($"/sessions/{Boss}/factory-inbox/{id}/decide", Boss, new { kind = "delegated" });

        Assert.Equal(HttpStatusCode.Forbidden, notMine);
        Assert.Equal("not_started_by_this_boss", Code(notMineBody));
        Assert.Equal(HttpStatusCode.Forbidden, orphan);
        Assert.Equal("not_started_by_this_boss", Code(orphanBody));
        Assert.Equal(HttpStatusCode.BadRequest, none);
        Assert.Equal("delegated_without_session", Code(noneBody));
        Assert.Empty(_store.DecisionsOf(A, [Guid.Parse(id)]));
    }

    [Fact]
    public async Task Decide_DelegatedToASessionTheGatewayHasNoRecordOfYet_SaysTryAgain_NotStartAnother()
    {
        // A session seconds old is not on the Director's next push yet. Telling the Boss it is not its own would make it
        // start a second session for the same message (review finding 4).
        var id = await LeaveFiledAndHanded("Build the export");

        var (status, body) = await Post($"/sessions/{Boss}/factory-inbox/{id}/decide", Boss,
            new { kind = "delegated", session = "77777777-7777-4777-8777-777777777777" });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("delegated_session_not_yet_known", Code(body));
        Assert.Contains("do not start another", body.GetProperty("error").GetString());
        Assert.Empty(_store.DecisionsOf(A, [Guid.Parse(id)]));

        // Once its row arrives, naming this Boss as its parent, the same decision is accepted.
        _parents["77777777-7777-4777-8777-777777777777"] = Boss;
        var (later, _) = await Post($"/sessions/{Boss}/factory-inbox/{id}/decide", Boss,
            new { kind = "delegated", session = "77777777-7777-4777-8777-777777777777" });
        Assert.Equal(HttpStatusCode.OK, later);
    }

    [Fact]
    public async Task Decide_DelegatedToASessionThisBossStarted_IsAccepted()
    {
        var id = await LeaveFiledAndHanded("Build the export");

        var (status, body) = await Post($"/sessions/{Boss}/factory-inbox/{id}/decide", Boss, new { kind = "delegated", session = BossChild });

        Assert.Equal(HttpStatusCode.OK, status);
        var message = body.GetProperty("message");
        Assert.Equal("Delegated", message.GetProperty("stateLabel").GetString());
        Assert.Equal(BossChild, message.GetProperty("decision").GetProperty("sessionId").GetString());
        Assert.Equal($"Delegated to session {BossChild}", message.GetProperty("trail").EnumerateArray().Last().GetProperty("label").GetString());
    }

    [Fact]
    public async Task Decide_UnknownKind_IsRefused()
    {
        var id = await LeaveFiledAndHanded("One");

        var (status, body) = await Post($"/sessions/{Boss}/factory-inbox/{id}/decide", Boss, new { kind = "ignored" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_decision", Code(body));
    }

    // ---------- the wake (phase 2) ----------

    [Fact]
    public async Task Send_TellsTheWake_TheFactoryAndTheSentTime_AndARefusedSendTellsItNothing()
    {
        await Leave("Check the totals");
        await Post("/gateway/factory/invoices/inbox", Boss, new { text = "as the owner" });

        var told = Assert.Single(_sentToWake);
        Assert.Equal(A, told.Tenant);
        Assert.Equal("invoices", told.Factory);
        Assert.Equal(_now, told.SentAtUtc);
        Assert.Empty(_bundlesToWake);
    }

    [Fact]
    public async Task HandingABundle_TellsTheWake_SoTheBossIsRungOrStarted()
    {
        await LeaveFiledAndHanded("Build the export");

        var told = Assert.Single(_bundlesToWake);
        Assert.Equal(A, told.Tenant);
        Assert.Equal("invoices", told.Factory);
    }

    [Fact]
    public async Task TheThread_ShowsWhyAMessageWaits_InTheWordsTheGatewayDecided()
    {
        await Leave("Check the totals");
        var row = _wakes.Row(A, "invoices", _now);
        Assert.True(_wakes.TryUpdate(A, "invoices", row.Version, w =>
        {
            w.SecretaryWaiting = FactoryInboxWords.Waiting.NoDirector;
            w.SecretaryWaitingDetail = "SOREN_NORTH";
        }, _now));

        var (status, body) = await Get("/gateway/factory/invoices/inbox", Phone);

        Assert.Equal(HttpStatusCode.OK, status);
        var message = Assert.Single(body.GetProperty("messages").EnumerateArray());
        Assert.Equal("sent", message.GetProperty("state").GetString());
        Assert.Equal("Waiting - no Director is running on the Boss's computer, SOREN_NORTH; it goes on when one connects there",
            message.GetProperty("stateLabel").GetString());
    }

    [Fact]
    public async Task TheList_RowSaysWhyTheNewestMessageWaits_InTheThreadsWords()
    {
        // The end-to-end proof of 6 November 2026, defect 6: the row said only "1 waiting for a decision" and "Sent".
        await Leave("Check the totals");
        var row = _wakes.Row(A, "invoices", _now);
        Assert.True(_wakes.TryUpdate(A, "invoices", row.Version, w =>
        {
            w.SecretaryWaiting = FactoryInboxWords.Waiting.NoDirector;
            w.SecretaryWaitingDetail = "SOREN_NORTH";
        }, _now));

        var (status, body) = await Get("/gateway/factory/inbox", Phone);

        Assert.Equal(HttpStatusCode.OK, status);
        var invoices = body.GetProperty("factories").EnumerateArray().Single(f => f.GetProperty("factory").GetString() == "invoices");
        var listed = invoices.GetProperty("row");
        Assert.Equal("Waiting - no Director is running on the Boss's computer, SOREN_NORTH; it goes on when one connects there",
            listed.GetProperty("reasonLine").GetString());
        Assert.Equal("Waiting", listed.GetProperty("badge").GetString());
        Assert.Equal("1 waiting for a decision", listed.GetProperty("waitingLine").GetString());
    }
}
