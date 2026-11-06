using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Factory.Inbox;
using CcDirector.Gateway.Inbox;
using CcDirector.Gateway.Messaging;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Inbox;

/// <summary>
/// THE YOUR INBOX ROUTES (Factory Inbox mission, phase 3), booted on an in-memory test server exactly as GatewayHost maps
/// them, over the real store, the real fleet message service and a throwaway database. A stand-in for the authentication
/// middleware puts on each request the identity the real one would: the owner's phone or browser, a session's key, or the
/// machine token.
///
/// These pin what only the routes know: a session sends only as itself, and who sent it comes from the Gateway's records,
/// never the body; only the owner's phone or Cockpit reads, marks and replies; the unread count follows the marks; every
/// word arrives finished; and REPLY ROUTING - a running session gets the reply in its own fleet inbox, an ended session
/// gets nothing and the owner is told so and offered a new session or its factory's Factory inbox, and a factory's message
/// is answered in that factory's Factory inbox, through the real Factory inbox store.
/// </summary>
public sealed class YourInboxEndpointTests : IAsyncLifetime
{
    private const string TenantHeader = "X-Test-Account";
    private const string CallerHeader = "X-Test-Caller";
    private const string Phone = "phone";
    private const string Browser = "browser";
    private const string MachineToken = "machine";
    private const string Developer = "11111111-1111-4111-8111-111111111111";
    private const string Boss = "22222222-2222-4222-8222-222222222222";
    private const string Ended = "33333333-3333-4333-8333-333333333333";
    private const string EndedNowhere = "44444444-4444-4444-8444-444444444444";
    private const string Other = "55555555-5555-4555-8555-555555555555";
    private const string EndedSeat = "66666666-6666-4666-8666-666666666666";
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");
    private static readonly Guid Report = Guid.Parse("99999999-9999-4999-8999-999999999999");

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase _db = null!;
    private YourInboxStore _store = null!;
    private FactoryInboxStore _factoryInbox = null!;
    private RegisteredFactoryDto _booking = new() { Factory = "booking", Title = "Booking", BossSeat = "boss" };
    private FleetMessageService _fleet = null!;
    private WebApplication? _app;
    private HttpClient _client = null!;
    private DateTime _now = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
    private readonly HashSet<string> _ended = new(StringComparer.Ordinal) { Ended, EndedNowhere, EndedSeat };
    private int _deliveries;
    private readonly List<(TenantId Tenant, string Factory, DateTime SentAtUtc)> _wakes = [];

    private static readonly Dictionary<string, DevReportSessionNaming> Names = new(StringComparer.Ordinal)
    {
        [Developer] = new(412, "Release - Developer"),
        [Boss] = new(301, "Booking - Boss"),
        [Ended] = new(250, "Website - Writer"),
        [EndedNowhere] = new(null, null),
        [Other] = new(500, "Other"),
        [EndedSeat] = new(302, "Booking - Secretary"),
    };

    public async Task InitializeAsync()
    {
        _db = _h.Open();
        _store = new YourInboxStore(_db);
        _factoryInbox = new FactoryInboxStore(_db, _ => DictationDictionary.Empty);
        _fleet = new FleetMessageService(new FleetMessageStore(_db), clock: () => _now);
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
        YourInboxEndpoints.Map(app, new YourInboxEndpoints.Parts(
            Store: _store,
            ResolveTenant: Resolve,
            Factories: _ => [_booking],
            Naming: (_, sid) => Names.TryGetValue(sid, out var n) ? n : new DevReportSessionNaming(null, null),
            FactorySeatOf: (_, sid) => sid switch
            {
                Boss => new DevReportFactorySeatIdentity("booking", "boss"),
                EndedSeat => new DevReportFactorySeatIdentity("booking", "secretary"),
                _ => null,
            },
            ReportTitle: (tenant, id) => tenant == A && id == Report ? "v2.25 release notes" : null,
            Liveness: (_, sid) => _ended.Contains(sid)
                ? new DevReportSessionLiveness(DevReportSessionReach.Ended, null, "test: ended")
                : new DevReportSessionLiveness(DevReportSessionReach.Busy, "dir-a", "test: working"),
            PlaceOf: (_, sid) => sid switch
            {
                Ended => new YourInboxSessionPlace("dir-a", @"D:\repos\website"),
                Boss => new YourInboxSessionPlace("dir-a", @"D:\repos\booking"),
                _ => null,
            },
            DeliverToSession: (tenant, sid, text) =>
            {
                _deliveries++;
                return _fleet.Send(tenant, null, new FleetParty(sid, null, null, null), text, FleetMessageKinds.System, FleetMessageExemption.System);
            },
            SendToFactory: (tenant, input, ct) => _factoryInbox.SendAsync(tenant, input, ct),
            MessageSent: (tenant, factory, sentAtUtc) => _wakes.Add((tenant, factory, sentAtUtc)),
            NowUtc: () => _now));
        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        _h.Dispose();
    }

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

    private async Task<(HttpStatusCode Status, JsonElement Body)> Call(string method, string path, string caller, object? body = null, TenantId? tenant = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(TenantHeader, (tenant ?? A).Value);
        request.Headers.Add(CallerHeader, caller);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Get(string path, string caller, TenantId? tenant = null) => Call("GET", path, caller, tenant: tenant);
    private Task<(HttpStatusCode Status, JsonElement Body)> Post(string path, string caller, object? body = null) => Call("POST", path, caller, body ?? new { });

    private async Task<string> SendFrom(string session, string subject = "v2.25 notes ready", string body = "Read them when you can.")
    {
        var (status, answer) = await Post($"/sessions/{session}/inbox", session, new { subject, body });
        Assert.Equal(HttpStatusCode.Created, status);
        return answer.GetProperty("message").GetProperty("id").GetString()!;
    }

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    // ---------- the session's send ----------

    [Fact]
    public async Task Send_FromASession_IsWrittenUnread_WithWhoSentItAndTheWayBack()
    {
        var (status, body) = await Post($"/sessions/{Developer}/inbox", Developer,
            new { subject = "v2.25 notes ready", body = "Read them when you can.", reportId = Report.ToString() });

        Assert.Equal(HttpStatusCode.Created, status);
        var m = body.GetProperty("message");
        Assert.Equal("v2.25 notes ready", m.GetProperty("subject").GetString());
        Assert.Equal("unread", m.GetProperty("state").GetString());
        Assert.Equal("Unread", m.GetProperty("stateLabel").GetString());
        Assert.True(m.GetProperty("unread").GetBoolean());
        Assert.Equal("session", m.GetProperty("from").GetProperty("kind").GetString());
        Assert.Equal("412 Release - Developer", m.GetProperty("from").GetProperty("label").GetString());
        var links = m.GetProperty("links").EnumerateArray().ToList();
        Assert.Equal(2, links.Count);
        Assert.Equal("session", links[0].GetProperty("kind").GetString());
        Assert.Equal("Open 412 Release - Developer", links[0].GetProperty("label").GetString());
        Assert.Equal(Developer, links[0].GetProperty("target").GetString());
        Assert.Equal("report", links[1].GetProperty("kind").GetString());
        Assert.Equal("Open the report: v2.25 release notes", links[1].GetProperty("label").GetString());
        Assert.Equal(Report.ToString("D"), links[1].GetProperty("target").GetString());
        Assert.Equal("Reply to 412 Release - Developer", m.GetProperty("replyTo").GetProperty("label").GetString());
    }

    [Fact]
    public async Task Send_FromAFactorySeat_CarriesItsFactory_ReadFromTheGatewayRecord()
    {
        var (status, body) = await Post($"/sessions/{Boss}/inbox", Boss, new { subject = "Declined: move Friday", body = "The contract fixes it." });

        Assert.Equal(HttpStatusCode.Created, status);
        var links = body.GetProperty("message").GetProperty("links").EnumerateArray().ToList();
        Assert.Contains(links, l => l.GetProperty("kind").GetString() == "factory"
                                    && l.GetProperty("label").GetString() == "Open Booking"
                                    && l.GetProperty("target").GetString() == "booking");
        var stored = _store.List(A, YourInboxWords.Views.Inbox, 10).Messages.Single();
        Assert.Equal("booking", stored.Factory);
        Assert.Equal("boss", stored.Seat);
    }

    [Fact]
    public async Task Send_WhoSentItIsNeverTakenFromTheBody()
    {
        // A body that claims another sender is read only for subject and body.
        var (status, _) = await Post($"/sessions/{Developer}/inbox", Developer,
            new { subject = "s", body = "b", sessionId = Boss, sessionName = "The owner himself", factory = "booking", fromKind = "factory" });

        Assert.Equal(HttpStatusCode.Created, status);
        var stored = _store.List(A, YourInboxWords.Views.Inbox, 10).Messages.Single();
        Assert.Equal(Developer, stored.SessionId);
        Assert.Equal("Release - Developer", stored.SessionName);
        Assert.Equal(YourInboxWords.Senders.Session, stored.FromKind);
        Assert.Null(stored.Factory);
    }

    [Fact]
    public async Task Send_NamingAnotherSession_IsRefused()
    {
        var (status, body) = await Post($"/sessions/{Boss}/inbox", Developer, new { subject = "s", body = "b" });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("not_your_session", Code(body));
        Assert.Equal(0, _store.UnreadCount(A));
    }

    [Theory]
    [InlineData(Phone)]
    [InlineData(MachineToken)]
    public async Task Send_WithoutASessionKey_IsRefused(string caller)
    {
        var (status, body) = await Post($"/sessions/{Developer}/inbox", caller, new { subject = "s", body = "b" });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("session_key_required", Code(body));
    }

    [Fact]
    public async Task Send_ABadMessage_IsRefusedInWords()
    {
        var (status, body) = await Post($"/sessions/{Developer}/inbox", Developer, new { subject = "", body = "b" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("subject_empty", Code(body));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
    }

    [Theory]
    [InlineData("not-a-guid", HttpStatusCode.BadRequest, "bad_report")]
    [InlineData("88888888-8888-4888-8888-888888888888", HttpStatusCode.NotFound, "report_not_found")]
    public async Task Send_AReportThisAccountDoesNotHave_IsRefused(string reportId, HttpStatusCode expected, string code)
    {
        var (status, body) = await Post($"/sessions/{Developer}/inbox", Developer, new { subject = "s", body = "b", reportId });

        Assert.Equal(expected, status);
        Assert.Equal(code, Code(body));
        Assert.Equal(0, _store.UnreadCount(A));
    }

    // ---------- the owner's reads and marks ----------

    [Fact]
    public async Task List_OnThePhone_ShowsTheMessageUnread_WithTheCount()
    {
        await SendFrom(Developer, "first");
        await SendFrom(Boss, "second");

        var (status, body) = await Get("/gateway/inbox", Phone);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("inbox", body.GetProperty("view").GetString());
        Assert.Equal(2, body.GetProperty("count").GetInt32());
        Assert.Equal(2, body.GetProperty("unread").GetInt32());
        Assert.Equal("2 unread", body.GetProperty("unreadLabel").GetString());
        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, messages.Count);
        var (countStatus, count) = await Get("/gateway/inbox/count", Browser);
        Assert.Equal(HttpStatusCode.OK, countStatus);
        Assert.Equal(2, count.GetProperty("unread").GetInt32());
        Assert.Equal("2 unread", count.GetProperty("label").GetString());
    }

    [Theory]
    [InlineData("GET", "/gateway/inbox")]
    [InlineData("GET", "/gateway/inbox/count")]
    [InlineData("GET", "/gateway/inbox/{id}")]
    [InlineData("POST", "/gateway/inbox/{id}/state")]
    [InlineData("POST", "/gateway/inbox/{id}/reply")]
    public async Task OwnerRoutes_RefuseASessionKey_AndTheMachineToken(string method, string path)
    {
        var id = await SendFrom(Developer);
        var url = path.Replace("{id}", id);

        var (sessionStatus, sessionBody) = await Call(method, url, Developer, method == "POST" ? new { state = "read", text = "hi" } : null);
        var (machineStatus, machineBody) = await Call(method, url, MachineToken, method == "POST" ? new { state = "read", text = "hi" } : null);

        Assert.Equal(HttpStatusCode.Forbidden, sessionStatus);
        Assert.Equal("session_key_out_of_scope", Code(sessionBody));
        Assert.Equal(HttpStatusCode.Forbidden, machineStatus);
        Assert.Equal("owner_device_required", Code(machineBody));
        Assert.Equal(YourInboxWords.States.Unread, _store.Get(A, Guid.Parse(id))!.State);
        Assert.Equal(0, _deliveries);
    }

    [Fact]
    public async Task MarkReadUnreadAndArchive_MoveTheMessage_AndTheCountFollows()
    {
        var id = await SendFrom(Developer);

        var (readStatus, read) = await Post($"/gateway/inbox/{id}/state", Phone, new { state = "read" });
        Assert.Equal(HttpStatusCode.OK, readStatus);
        Assert.Equal("Read", read.GetProperty("message").GetProperty("stateLabel").GetString());
        Assert.Equal(new[] { "Mark unread", "Archive" },
            read.GetProperty("message").GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("label").GetString()));
        Assert.Equal(0, (await Get("/gateway/inbox/count", Phone)).Body.GetProperty("unread").GetInt32());
        Assert.Equal("", (await Get("/gateway/inbox/count", Phone)).Body.GetProperty("label").GetString());

        var (_, archived) = await Post($"/gateway/inbox/{id}/state", Phone, new { state = "archived" });
        Assert.Equal("Archived", archived.GetProperty("message").GetProperty("stateLabel").GetString());
        Assert.Equal(new[] { "Move to inbox", "Mark unread" },
            archived.GetProperty("message").GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("label").GetString()));
        Assert.Equal(0, (await Get("/gateway/inbox", Phone)).Body.GetProperty("count").GetInt32());
        Assert.Equal(1, (await Get("/gateway/inbox?view=archived", Phone)).Body.GetProperty("count").GetInt32());

        var (_, unread) = await Post($"/gateway/inbox/{id}/state", Phone, new { state = "unread" });
        Assert.True(unread.GetProperty("message").GetProperty("unread").GetBoolean());
        Assert.Equal(1, (await Get("/gateway/inbox/count", Phone)).Body.GetProperty("unread").GetInt32());
    }

    [Fact]
    public async Task State_AnUnknownStateOrMessage_IsRefused()
    {
        var id = await SendFrom(Developer);

        var (badStatus, bad) = await Post($"/gateway/inbox/{id}/state", Phone, new { state = "deleted" });
        var (missingStatus, missing) = await Post($"/gateway/inbox/{Guid.NewGuid()}/state", Phone, new { state = "read" });

        Assert.Equal(HttpStatusCode.BadRequest, badStatus);
        Assert.Equal("bad_state", Code(bad));
        Assert.Equal(HttpStatusCode.NotFound, missingStatus);
        Assert.Equal("message_not_found", Code(missing));
    }

    // ---------- the screen's filters, empty words and tones (Factory Inbox mission, step C) ----------

    [Fact]
    public async Task List_CarriesTheFiltersInOrder_WithTheirWordsAndCounts()
    {
        var first = await SendFrom(Developer, "first");
        var second = await SendFrom(Boss, "second");
        await SendFrom(Developer, "third");
        await Post($"/gateway/inbox/{first}/state", Phone, new { state = "read" });
        await Post($"/gateway/inbox/{second}/state", Phone, new { state = "archived" });

        var (_, body) = await Get("/gateway/inbox", Phone);

        var filters = body.GetProperty("filters").EnumerateArray()
            .Select(f => (f.GetProperty("view").GetString(), f.GetProperty("label").GetString(), f.GetProperty("count").GetInt32()))
            .ToList();
        Assert.Equal(new List<(string?, string?, int)> { ("unread", "Unread", 1), ("inbox", "All", 2), ("archived", "Archived", 1) }, filters);
    }

    [Fact]
    public async Task List_TheUnreadView_HoldsOnlyUnreadMessages()
    {
        var first = await SendFrom(Developer, "first");
        await SendFrom(Developer, "second");
        await Post($"/gateway/inbox/{first}/state", Phone, new { state = "read" });

        var (status, body) = await Get("/gateway/inbox?view=unread", Phone);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("unread", body.GetProperty("view").GetString());
        Assert.Equal(1, body.GetProperty("count").GetInt32());
        var message = Assert.Single(body.GetProperty("messages").EnumerateArray());
        Assert.Equal("second", message.GetProperty("subject").GetString());
    }

    [Theory]
    [InlineData("inbox", "Nothing for you. Factories and sessions write here instead of emailing you.")]
    [InlineData("unread", "Nothing unread.")]
    [InlineData("archived", "Nothing archived. An archived message stays here; nothing is ever deleted.")]
    public async Task List_AnEmptyView_SaysSoInTheGatewaysWords(string view, string expected)
    {
        var (_, body) = await Get($"/gateway/inbox?view={view}", Phone);

        Assert.Equal(0, body.GetProperty("count").GetInt32());
        Assert.Equal(expected, body.GetProperty("empty").GetString());
    }

    [Fact]
    public async Task List_EachMessageCarriesTheToneOfItsState()
    {
        var first = await SendFrom(Developer, "first");
        await SendFrom(Developer, "second");
        await Post($"/gateway/inbox/{first}/state", Phone, new { state = "read" });

        var (_, body) = await Get("/gateway/inbox", Phone);

        var tones = body.GetProperty("messages").EnumerateArray()
            .ToDictionary(m => m.GetProperty("subject").GetString()!, m => m.GetProperty("tone").GetString());
        Assert.Equal("red", tones["second"]);
        Assert.Equal("grey", tones["first"]);
    }

    [Fact]
    public async Task List_ABadViewOrLimit_IsRefused()
    {
        Assert.Equal("bad_view", Code((await Get("/gateway/inbox?view=trash", Phone)).Body));
        Assert.Equal("bad_limit", Code((await Get("/gateway/inbox?limit=0", Phone)).Body));
        Assert.Equal("bad_limit", Code((await Get($"/gateway/inbox?limit={YourInboxStore.MaxList + 1}", Phone)).Body));
    }

    [Fact]
    public async Task AnotherAccount_NeverSeesTheMessage()
    {
        var id = await SendFrom(Developer);

        var (_, list) = await Get("/gateway/inbox", Phone, B);
        var (status, _) = await Get($"/gateway/inbox/{id}", Phone, B);

        Assert.Equal(0, list.GetProperty("count").GetInt32());
        Assert.Equal(0, list.GetProperty("unread").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    // ---------- reply routing ----------

    [Fact]
    public async Task Reply_ToTheMorningReportsMessage_IsRefusedInWords_AndItListsWithNoReplyTarget()
    {
        var row = _store.Add(A, YourInboxStore.ForNoFactoryProblems(A, "Schedules in no factory: 1 problem still open - Sunday 11 October",
            "One problem.", new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc)));

        var (listed, list) = await Call("GET", "/gateway/inbox", Phone);
        var (status, body) = await Post($"/gateway/inbox/{row.Id:D}/reply", Phone, new { text = "Thanks." });

        Assert.Equal(HttpStatusCode.OK, listed);
        var m = list.GetProperty("messages").EnumerateArray().Single();
        Assert.Equal(("gateway", "Morning report - schedules in no factory"),
            (m.GetProperty("from").GetProperty("kind").GetString(), m.GetProperty("from").GetProperty("label").GetString()));
        Assert.Equal(JsonValueKind.Null, m.GetProperty("replyTo").ValueKind);
        Assert.Equal(0, m.GetProperty("links").GetArrayLength());
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("no_reply_to_gateway", Code(body));
        Assert.Equal(YourInboxWords.NoReplyToGateway, body.GetProperty("error").GetString());
        Assert.Empty(_store.Replies(A, row.Id));
    }

    [Fact]
    public async Task Reply_ToARunningSession_GoesIntoItsOwnFleetInbox_AndIsKeptOnTheMessage()
    {
        var id = await SendFrom(Developer, "Shall I tag v2.25?");

        var (status, body) = await Post($"/gateway/inbox/{id}/reply", Phone, new { text = "Yes, tag it." });

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(1, _deliveries);
        Assert.Equal("session", body.GetProperty("reply").GetProperty("routedTo").GetString());
        Assert.Equal("Sent to 412 Release - Developer, which reads it when it is free",
            body.GetProperty("reply").GetProperty("label").GetString());
        Assert.Equal("Read", body.GetProperty("message").GetProperty("stateLabel").GetString());
        Assert.Single(body.GetProperty("message").GetProperty("replies").EnumerateArray());

        // The session reads it from its own inbox: the owner's words exactly, and what they answer.
        var inbox = _fleet.ReadInbox(A, Developer, includeRecent: false);
        var delivered = Assert.Single(inbox.Unread);
        Assert.Contains("Yes, tag it.", delivered.Text);
        Assert.Contains("\"Shall I tag v2.25?\"", delivered.Text);
        var reply = Assert.Single(_store.Replies(A, Guid.Parse(id)));
        Assert.Equal(delivered.MessageId, reply.FleetMessageId);
        Assert.Equal("owner-phone-1", reply.SentBy);
    }

    [Fact]
    public async Task Reply_ToAnEndedSession_IsNotSent_SaysSo_AndOffersANewSession()
    {
        var id = await SendFrom(Ended, "Draft is up");

        var (status, body) = await Post($"/gateway/inbox/{id}/reply", Phone, new { text = "Make it shorter." });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("session_ended", Code(body));
        Assert.Equal("250 Website - Writer has ended, so it cannot read a reply. Start a new session with the reply instead.",
            body.GetProperty("error").GetString());
        var offer = Assert.Single(body.GetProperty("offers").EnumerateArray());
        Assert.Equal("new-session", offer.GetProperty("kind").GetString());
        Assert.Equal("dir-a", offer.GetProperty("directorId").GetString());
        Assert.Equal(@"D:\repos\website", offer.GetProperty("repoPath").GetString());
        Assert.Contains("Make it shorter.", offer.GetProperty("prompt").GetString());
        Assert.Contains("Draft is up", offer.GetProperty("prompt").GetString());
        Assert.Equal(0, _deliveries);
        Assert.Empty(_store.Replies(A, Guid.Parse(id)));
        Assert.Equal(YourInboxWords.States.Unread, _store.Get(A, Guid.Parse(id))!.State);
    }

    [Fact]
    public async Task Reply_ToAnEndedSessionWithNoRecordOfWhereItRan_OffersNothing_AndSaysWhy()
    {
        var id = await SendFrom(EndedNowhere);

        var (status, body) = await Post($"/gateway/inbox/{id}/reply", Phone, new { text = "Thanks." });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Empty(body.GetProperty("offers").EnumerateArray());
        Assert.Equal("the session has ended, so it cannot read a reply. The Gateway does not know where it ran, so it cannot offer a new session in its place; nothing was sent.",
            body.GetProperty("error").GetString());
        Assert.Equal(0, _deliveries);
    }

    [Fact]
    public async Task Reply_Empty_IsRefused_AndNothingIsDelivered()
    {
        var id = await SendFrom(Developer);

        var (status, body) = await Post($"/gateway/inbox/{id}/reply", Phone, new { text = "  " });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("reply_empty", Code(body));
        Assert.Equal(0, _deliveries);
    }

    // ---------- replies into a factory's Factory inbox ----------

    /// <summary>A message from the booking factory's Boss, as a Declined decision writes it.</summary>
    private Guid FromTheBoss()
    {
        var row = YourInboxStore.ForBossDecision(A, "booking", Boss, Guid.NewGuid(), "Move the run to Sunday.", _now,
            FactoryInboxWords.Decisions.Declined, "Sunday has no staff.", _now);
        using var ctx = _db.CreateContext(A);
        ctx.YourInboxMessages.Add(row);
        ctx.SaveChanges();
        return row.Id;
    }

    [Theory]
    [InlineData(Phone, FactoryInboxWords.Surfaces.Phone)]
    [InlineData(Browser, FactoryInboxWords.Surfaces.Cockpit)]
    public async Task Reply_ToAFactorysMessage_IsLeftInThatFactorysInbox_AsTheOwnersOwnTypedMessage(string device, string surface)
    {
        var id = FromTheBoss();

        var (status, body) = await Post($"/gateway/inbox/{id}/reply", device, new { text = "Then Saturday morning." });

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("factory", body.GetProperty("reply").GetProperty("routedTo").GetString());
        Assert.Equal("Left in the Factory inbox of Booking", body.GetProperty("reply").GetProperty("label").GetString());
        Assert.Equal("Read", body.GetProperty("message").GetProperty("stateLabel").GetString());
        var left = Assert.Single(_factoryInbox.Thread(A, "booking", 10).Messages);
        Assert.Equal("Then Saturday morning.", left.Text);
        Assert.Equal(FactoryInboxWords.States.Sent, left.State);
        Assert.Equal(FactoryInboxWords.Sources.Typed, left.Source);
        Assert.Equal(surface, left.Surface);
        Assert.Equal($"owner-{device}-1", left.SentBy);
        var reply = Assert.Single(_store.Replies(A, id));
        Assert.Equal(left.Id, reply.FactoryMessageId);
        Assert.Equal("booking", reply.Factory);
        Assert.Null(reply.SessionId);
        Assert.Equal(0, _deliveries);
        // The wake is told, exactly as the owner's own send to the factory tells it.
        var wake = Assert.Single(_wakes);
        Assert.Equal((A, "booking", left.SentAtUtc), wake);
    }

    [Fact]
    public async Task Reply_ToAFactorysMessage_WhenTheFactoryIsArchived_IsRefused_AndNothingIsLeft()
    {
        var id = FromTheBoss();
        _booking = new RegisteredFactoryDto { Factory = "booking", Title = "Booking", BossSeat = "boss", ArchivedAtUtc = _now };

        var (status, body) = await Post($"/gateway/inbox/{id}/reply", Phone, new { text = "Then Saturday morning." });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("factory_archived", Code(body));
        Assert.Equal(0, _factoryInbox.Thread(A, "booking", 10).Total);
        Assert.Empty(_store.Replies(A, id));
        Assert.Empty(_wakes);
    }

    [Fact]
    public async Task Reply_ToAFactorysMessage_WhenTheFactoryHasNoBoss_IsRefused_AndNothingIsLeft()
    {
        var id = FromTheBoss();
        _booking = new RegisteredFactoryDto { Factory = "booking", Title = "Booking", BossSeat = null };

        var (status, body) = await Post($"/gateway/inbox/{id}/reply", Phone, new { text = "Then Saturday morning." });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("factory_has_no_boss", Code(body));
        Assert.Equal(0, _factoryInbox.Thread(A, "booking", 10).Total);
        Assert.Empty(_store.Replies(A, id));
    }

    [Fact]
    public async Task Reply_ToAnEndedFactorySeat_OffersItsFactorysInbox_AndTakingTheOfferLeavesItThere()
    {
        var id = await SendFrom(EndedSeat, "Three messages filed");

        var (status, body) = await Post($"/gateway/inbox/{id}/reply", Phone, new { text = "File the fourth too." });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("session_ended", Code(body));
        Assert.Equal("302 Booking - Secretary has ended, so it cannot read a reply. Leave it in the Factory inbox of Booking instead. " +
                     "The Gateway does not know where the session ran, so it cannot offer a new session in its place.",
            body.GetProperty("error").GetString());
        var offer = Assert.Single(body.GetProperty("offers").EnumerateArray());
        Assert.Equal("factory", offer.GetProperty("kind").GetString());
        Assert.Equal("Leave this reply in the Factory inbox of Booking", offer.GetProperty("label").GetString());
        Assert.Equal("booking", offer.GetProperty("factory").GetString());
        Assert.Equal(0, _factoryInbox.Thread(A, "booking", 10).Total);

        var (taken, answer) = await Post($"/gateway/inbox/{id}/reply", Phone, new { text = "File the fourth too.", to = "factory" });

        Assert.Equal(HttpStatusCode.Created, taken);
        Assert.Equal("Left in the Factory inbox of Booking", answer.GetProperty("reply").GetProperty("label").GetString());
        Assert.Equal("File the fourth too.", Assert.Single(_factoryInbox.Thread(A, "booking", 10).Messages).Text);
        Assert.Equal(0, _deliveries);
    }

    [Fact]
    public async Task Reply_ToAnEndedSessionWithAPlaceAndAFactory_OffersBoth()
    {
        _ended.Add(Boss);
        var id = await SendFrom(Boss, "Bundle decided");

        var (status, body) = await Post($"/gateway/inbox/{id}/reply", Phone, new { text = "Good." });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(new[] { "new-session", "factory" }, body.GetProperty("offers").EnumerateArray().Select(o => o.GetProperty("kind").GetString()));
        Assert.Equal("301 Booking - Boss has ended, so it cannot read a reply. Start a new session with the reply, or leave it in the Factory inbox of Booking, instead.",
            body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Reply_ToFactory_OnAMessageWithNoFactory_IsRefused()
    {
        var id = await SendFrom(Developer);

        var (status, body) = await Post($"/gateway/inbox/{id}/reply", Phone, new { text = "Yes.", to = "factory" });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("no_factory_to_reply_into", Code(body));
        Assert.Equal(0, _deliveries);
        Assert.Empty(_store.Replies(A, Guid.Parse(id)));
    }

    [Fact]
    public async Task Reply_ToAnUnknownPlace_IsRefused()
    {
        var id = await SendFrom(Developer);

        var (status, body) = await Post($"/gateway/inbox/{id}/reply", Phone, new { text = "Yes.", to = "email" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_reply_target", Code(body));
        Assert.Equal(0, _deliveries);
    }

    [Fact]
    public async Task Read_OneMessage_CarriesItsReplies()
    {
        var id = await SendFrom(Developer);
        await Post($"/gateway/inbox/{id}/reply", Phone, new { text = "First." });
        _now = _now.AddMinutes(1);
        await Post($"/gateway/inbox/{id}/reply", Browser, new { text = "Second." });

        var (status, body) = await Get($"/gateway/inbox/{id}", Phone);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(new[] { "First.", "Second." },
            body.GetProperty("message").GetProperty("replies").EnumerateArray().Select(r => r.GetProperty("text").GetString()));
    }
}
