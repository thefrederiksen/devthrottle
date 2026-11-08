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
/// AN AGENT USES THE ACCOUNT'S INBOX LIKE EMAIL (the owner's decisions of 7 November 2026), on an in-memory test
/// server exactly as GatewayHost maps the routes, over the real Your inbox store, the real Factory inbox store, the real
/// fleet message service and a throwaway database. A stand-in for the authentication middleware puts on each request the
/// identity the real one would: the owner's phone, or a session's key bound to an account.
///
/// These pin: a session reads its own account's inbox and no other; reading leaves a message unread; delete is archive and
/// restore undoes it; marks; reply routing, with the reply shown as the agent's; a message left in a factory shown as from
/// the agent, and refused to that factory's own seats; the owner's routes unchanged; and the agent line folded once, on the
/// Gateway.
/// </summary>
public sealed class YourInboxAgentEndpointTests : IAsyncLifetime
{
    private const string TenantHeader = "X-Test-Account";
    private const string KeyTenantHeader = "X-Test-Key-Account";
    private const string CallerHeader = "X-Test-Caller";
    private const string Phone = "phone";
    private const string Lead = "11111111-1111-4111-8111-111111111111";
    private const string Developer = "22222222-2222-4222-8222-222222222222";
    private const string BookingBoss = "33333333-3333-4333-8333-333333333333";
    private const string Ended = "44444444-4444-4444-8444-444444444444";
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase _db = null!;
    private YourInboxStore _store = null!;
    private FactoryInboxStore _factoryInbox = null!;
    private FleetMessageService _fleet = null!;
    private WebApplication? _app;
    private HttpClient _client = null!;
    private DateTime _now = new(2026, 11, 7, 14, 2, 0, DateTimeKind.Utc);
    private readonly List<(TenantId Tenant, string Factory, DateTime SentAtUtc)> _wakes = [];
    private int _deliveries;

    private static readonly RegisteredFactoryDto Booking = new() { Factory = "booking", Title = "Booking", BossSeat = "boss" };
    private static readonly RegisteredFactoryDto Website = new() { Factory = "website", Title = "Website", BossSeat = "boss" };

    /// <summary>Who started whom, as the roster says: the Lead started the Developer, so they may message each other; the
    /// Booking Boss is related to neither.</summary>
    private static readonly Dictionary<string, string> ControllerOf = new(StringComparer.Ordinal) { [Developer] = Lead };

    private static readonly Dictionary<string, DevReportSessionNaming> Names = new(StringComparer.Ordinal)
    {
        [Lead] = new(114, "Factory Inbox - Delivery Lead"),
        [Developer] = new(412, "Release - Developer"),
        [BookingBoss] = new(301, "Booking - Boss"),
        [Ended] = new(250, "Website - Writer"),
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
            Factories: _ => [Booking, Website],
            Naming: (_, sid) => Names.TryGetValue(sid, out var n) ? n : new DevReportSessionNaming(null, null),
            FactorySeatOf: (_, sid) => sid == BookingBoss ? new DevReportFactorySeatIdentity("booking", "boss") : null,
            ReportTitle: (_, _) => null,
            Liveness: (_, sid) => sid == Ended
                ? new DevReportSessionLiveness(DevReportSessionReach.Ended, null, "test: ended")
                : new DevReportSessionLiveness(DevReportSessionReach.Busy, "dir-a", "test: working"),
            PlaceOf: (_, sid) => sid == Ended ? new YourInboxSessionPlace("dir-a", "/repos/website") : null,
            DeliverToSession: (tenant, sid, text) =>
            {
                _deliveries++;
                return _fleet.Send(tenant, null, new FleetParty(sid, null, null, null), text, FleetMessageKinds.System, FleetMessageExemption.System);
            },
            SendAsAgent: (tenant, from, to, text) => _fleet.Send(tenant,
                new FleetParty(from, ControllerOf.GetValueOrDefault(from), Names.GetValueOrDefault(from)?.Name, null),
                new FleetParty(to, ControllerOf.GetValueOrDefault(to), Names.GetValueOrDefault(to)?.Name, null),
                text, FleetMessageKinds.Message, FleetMessageExemption.None),
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

    /// <summary>The owner's phone, or a session key. A session key is bound to the account in the key header when the
    /// test gives one - so a test can make the request's account differ from the key's - and to the request's otherwise.</summary>
    private static void StandInForAuthentication(HttpContext ctx)
    {
        var caller = ctx.Request.Headers[CallerHeader].ToString();
        var tenant = Resolve(ctx) ?? A;
        if (caller == Phone)
        {
            ctx.Items[AuthMiddleware.DeviceTypeItemKey] = caller;
            ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("owner-phone-1", tenant.Value, caller, "active");
        }
        else if (Guid.TryParse(caller, out var session))
        {
            var keyTenant = ctx.Request.Headers.TryGetValue(KeyTenantHeader, out var k) ? new TenantId(k.ToString()) : tenant;
            ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(session, keyTenant, "dir-a");
        }
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Call(string method, string path, string caller, object? body = null,
        TenantId? tenant = null, TenantId? keyTenant = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(TenantHeader, (tenant ?? A).Value);
        request.Headers.Add(CallerHeader, caller);
        if (keyTenant is { } k) request.Headers.Add(KeyTenantHeader, k.Value);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Get(string path, string caller) => Call("GET", path, caller);
    private Task<(HttpStatusCode Status, JsonElement Body)> Post(string path, string caller, object? body = null) => Call("POST", path, caller, body ?? new { });

    private async Task<string> SendFrom(string session, string subject = "v2.25 notes ready", TenantId? tenant = null)
    {
        var (status, answer) = await Call("POST", $"/sessions/{session}/inbox", session, new { subject, body = "Read them when you can." }, tenant);
        Assert.Equal(HttpStatusCode.Created, status);
        return answer.GetProperty("message").GetProperty("id").GetString()!;
    }

    private Guid FactoryMessage(TenantId tenant, string factory, string subject)
    {
        var row = YourInboxStore.ForScheduleFailure(tenant, factory, subject, "It failed at 03:00.", _now);
        return _store.Add(tenant, row).Id;
    }

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static string[] Lines(JsonElement message)
        => message.GetProperty("agentActions").EnumerateArray().Select(a => a.GetProperty("line").GetString()!).ToArray();

    // ---------- reading ----------

    [Fact]
    public async Task List_AsASession_ReadsItsOwnAccountsInbox_AndNoOther()
    {
        var mine = await SendFrom(Developer, "In account A");
        await SendFrom(Developer, "In account B", tenant: B);

        var (status, body) = await Get($"/sessions/{Lead}/inbox", Lead);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("inbox", body.GetProperty("view").GetString());
        Assert.Equal(1, body.GetProperty("count").GetInt32());
        var m = Assert.Single(body.GetProperty("messages").EnumerateArray());
        Assert.Equal(mine, m.GetProperty("id").GetString());
        Assert.Equal("In account A", m.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task List_AsASession_TakesTheSameViewsAndLimitAsTheOwner()
    {
        var id = await SendFrom(Developer);
        Assert.Equal(HttpStatusCode.OK, (await Post($"/sessions/{Lead}/inbox/{id}/state", Lead, new { state = "archived" })).Status);

        var (_, inbox) = await Get($"/sessions/{Lead}/inbox?view=inbox", Lead);
        var (_, archived) = await Get($"/sessions/{Lead}/inbox?view=archived&limit=5", Lead);
        var (badStatus, bad) = await Get($"/sessions/{Lead}/inbox?view=spam", Lead);

        Assert.Equal(0, inbox.GetProperty("count").GetInt32());
        Assert.Equal(1, archived.GetProperty("count").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, badStatus);
        Assert.Equal("bad_view", Code(bad));
    }

    [Fact]
    public async Task Read_AsASession_LeavesTheMessageUnread_AndRecordsTheRead()
    {
        var id = await SendFrom(Developer);

        var (status, body) = await Get($"/sessions/{Lead}/inbox/{id}", Lead);

        Assert.Equal(HttpStatusCode.OK, status);
        var m = body.GetProperty("message");
        Assert.Equal("unread", m.GetProperty("state").GetString());
        Assert.True(m.GetProperty("unread").GetBoolean());
        Assert.Equal(0, m.GetProperty("replies").GetArrayLength());
        Assert.Equal("Read by an agent: session 114 \"Factory Inbox - Delivery Lead\", 2026-11-07 14:02 UTC",
            m.GetProperty("agentLine").GetString());
        Assert.Equal(YourInboxWords.States.Unread, _store.Get(A, Guid.Parse(id))!.State);
        Assert.Equal(1, _store.UnreadCount(A));
        Assert.Equal("Reply to 412 Release - Developer", body.GetProperty("replyGoesTo").GetString());
        Assert.Equal(YourInboxWords.AgentReadNote, body.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Read_AgainAndAgain_IsRecordedOnce_UntilSomethingElseHappens()
    {
        var id = await SendFrom(Developer);

        for (var i = 0; i < 5; i++)
        {
            _now = _now.AddMinutes(1);
            await Get($"/sessions/{Lead}/inbox/{id}", Lead);
        }
        Assert.Single(_store.AgentActions(A, [Guid.Parse(id)])[Guid.Parse(id)]);

        // Another session's read, or another act, is a new row; then this session's next read is one too.
        await Get($"/sessions/{BookingBoss}/inbox/{id}", BookingBoss);
        _now = _now.AddMinutes(1);
        await Get($"/sessions/{Lead}/inbox/{id}", Lead);
        await Get($"/sessions/{Lead}/inbox/{id}", Lead);

        var acts = _store.AgentActions(A, [Guid.Parse(id)])[Guid.Parse(id)];
        Assert.Equal([Lead, BookingBoss, Lead], acts.Select(a => a.SessionId).ToArray());
    }

    [Fact]
    public async Task Read_AMessageFromTheGateway_SaysNobodyReadsAReply()
    {
        var row = _store.Add(A, YourInboxStore.ForScheduleFailure(A, null, "A schedule failed", "It failed.", _now));

        var (_, body) = await Get($"/sessions/{Lead}/inbox/{row.Id}", Lead);

        Assert.Equal(YourInboxWords.NoReplyLine, body.GetProperty("replyGoesTo").GetString());
    }

    [Fact]
    public async Task Read_AMessageOfAnotherAccount_IsNotFound()
    {
        var other = await SendFrom(Developer, tenant: B);

        var (status, body) = await Get($"/sessions/{Lead}/inbox/{other}", Lead);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("message_not_found", Code(body));
    }

    // ---------- who may ----------

    [Fact]
    public async Task ARequestNamingAnotherAccount_IsRefused_ToAKeyBoundToItsOwn()
    {
        await SendFrom(Developer, tenant: B);

        var (status, body) = await Call("GET", $"/sessions/{Lead}/inbox", Lead, tenant: B, keyTenant: A);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("not_your_account", Code(body));
    }

    [Fact]
    public async Task ASessionNamingAnotherSession_IsRefused()
    {
        var id = await SendFrom(Developer);

        var (listStatus, list) = await Get($"/sessions/{Developer}/inbox", Lead);
        var (stateStatus, _) = await Post($"/sessions/{Developer}/inbox/{id}/state", Lead, new { state = "archived" });

        Assert.Equal(HttpStatusCode.Forbidden, listStatus);
        Assert.Equal("not_your_session", Code(list));
        Assert.Equal(HttpStatusCode.Forbidden, stateStatus);
        Assert.Equal(YourInboxWords.States.Unread, _store.Get(A, Guid.Parse(id))!.State);
    }

    [Fact]
    public async Task TheOwnersPhone_IsRefusedTheSessionRoutes()
    {
        var (status, body) = await Get($"/sessions/{Lead}/inbox", Phone);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("session_key_required", Code(body));
    }

    [Fact]
    public async Task TheOwnersRoutes_AreUnchanged_AndRefuseASessionKey()
    {
        var id = await SendFrom(Developer);

        foreach (var (method, path) in new[]
                 {
                     ("GET", "/gateway/inbox"), ("GET", "/gateway/inbox/count"), ("GET", $"/gateway/inbox/{id}"),
                     ("POST", $"/gateway/inbox/{id}/state"), ("POST", $"/gateway/inbox/{id}/reply"),
                 })
        {
            var (status, body) = await Call(method, path, Lead, method == "POST" ? new { state = "read", text = "x" } : null);
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal("session_key_out_of_scope", Code(body));
        }
        Assert.Equal(YourInboxWords.States.Unread, _store.Get(A, Guid.Parse(id))!.State);

        // The owner's own read still works, does not record an agent act, and his mark is not an agent's.
        var (ownerStatus, owner) = await Get($"/gateway/inbox/{id}", Phone);
        Assert.Equal(HttpStatusCode.OK, ownerStatus);
        Assert.Equal(JsonValueKind.Null, owner.GetProperty("message").GetProperty("agentLine").ValueKind);
        var (_, marked) = await Post($"/gateway/inbox/{id}/state", Phone, new { state = "read" });
        Assert.Equal("read", marked.GetProperty("message").GetProperty("state").GetString());
        Assert.Empty(Lines(marked.GetProperty("message")));
    }

    // ---------- delete is archive; restore undoes it ----------

    [Fact]
    public async Task Delete_IsArchive_AndRestore_PutsAnUnreadMessageBackUnread()
    {
        var id = await SendFrom(Developer);

        var (deleteStatus, deleted) = await Post($"/sessions/{Lead}/inbox/{id}/state", Lead, new { state = "archived" });
        Assert.Equal(HttpStatusCode.OK, deleteStatus);
        Assert.Equal("archived", deleted.GetProperty("message").GetProperty("state").GetString());
        Assert.Equal("Archived by an agent: session 114 \"Factory Inbox - Delivery Lead\", 2026-11-07 14:02 UTC",
            deleted.GetProperty("message").GetProperty("agentLine").GetString());
        Assert.Equal("Archived. Delete is archive: the message left the inbox and is under Archived, where it can be restored. Nothing was deleted.",
            deleted.GetProperty("done").GetString());

        _now = _now.AddMinutes(3);
        var (restoreStatus, restored) = await Post($"/sessions/{Lead}/inbox/{id}/state", Lead, new { state = "restore" });

        Assert.Equal(HttpStatusCode.OK, restoreStatus);
        var m = restored.GetProperty("message");
        Assert.Equal("unread", m.GetProperty("state").GetString());
        Assert.Equal(
            [
                "Archived by an agent: session 114 \"Factory Inbox - Delivery Lead\", 2026-11-07 14:02 UTC",
                "Moved back to the inbox by an agent: session 114 \"Factory Inbox - Delivery Lead\", 2026-11-07 14:05 UTC",
            ],
            Lines(m));
        Assert.Equal(1, _store.UnreadCount(A));
        Assert.Equal("Moved back to the inbox, unread.", restored.GetProperty("done").GetString());
    }

    [Fact]
    public async Task Restore_PutsAMessageSomebodyMarkedReadBackRead_AndLeavesItsReadTimeAsItWas()
    {
        var id = await SendFrom(Developer);
        var readAt = _now;
        await Post($"/gateway/inbox/{id}/state", Phone, new { state = "read" });
        _now = _now.AddMinutes(5);
        await Post($"/sessions/{Lead}/inbox/{id}/state", Lead, new { state = "archived" });
        _now = _now.AddMinutes(5);

        var (_, restored) = await Post($"/sessions/{Lead}/inbox/{id}/state", Lead, new { state = "restore" });

        Assert.Equal("read", restored.GetProperty("message").GetProperty("state").GetString());
        var row = _store.Get(A, Guid.Parse(id))!;
        Assert.Equal(readAt, row.ReadAtUtc);
        Assert.Null(row.ArchivedAtUtc);
    }

    [Fact]
    public async Task Restore_AMessageThatIsNotArchived_ChangesAndRecordsNothing()
    {
        var id = await SendFrom(Developer);

        var (status, body) = await Post($"/sessions/{Lead}/inbox/{id}/state", Lead, new { state = "restore" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("unread", body.GetProperty("message").GetProperty("state").GetString());
        Assert.Equal("Nothing changed: the message was already unread.", body.GetProperty("done").GetString());
        Assert.Empty(_store.AgentActions(A, [Guid.Parse(id)]));
    }

    [Fact]
    public async Task Mark_ReadThenUnread_IsRecordedAsTheAgents_AndAnUnchangedMarkRecordsNothing()
    {
        var id = await SendFrom(Developer);

        var (_, read) = await Post($"/sessions/{Lead}/inbox/{id}/state", Lead, new { state = "read" });
        var (_, again) = await Post($"/sessions/{Lead}/inbox/{id}/state", Lead, new { state = "read" });
        _now = _now.AddMinutes(1);
        var (_, unread) = await Post($"/sessions/{Lead}/inbox/{id}/state", Lead, new { state = "unread" });

        Assert.Equal("read", read.GetProperty("message").GetProperty("state").GetString());
        Assert.Single(Lines(again.GetProperty("message")));
        Assert.Equal("unread", unread.GetProperty("message").GetProperty("state").GetString());
        Assert.Equal(
            [
                "Marked read by an agent: session 114 \"Factory Inbox - Delivery Lead\", 2026-11-07 14:02 UTC",
                "Marked unread by an agent: session 114 \"Factory Inbox - Delivery Lead\", 2026-11-07 14:03 UTC",
            ],
            Lines(unread.GetProperty("message")));
    }

    [Fact]
    public async Task State_AnUnknownState_IsRefused_AndChangesNothing()
    {
        var id = await SendFrom(Developer);

        var (status, body) = await Post($"/sessions/{Lead}/inbox/{id}/state", Lead, new { state = "deleted" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_state", Code(body));
        Assert.Empty(_store.AgentActions(A, [Guid.Parse(id)]));
    }

    [Fact]
    public async Task TheOwnersList_ShowsWhatTheAgentDid()
    {
        var id = await SendFrom(Developer);
        await Get($"/sessions/{Lead}/inbox/{id}", Lead);

        var (_, list) = await Get("/gateway/inbox", Phone);

        var m = Assert.Single(list.GetProperty("messages").EnumerateArray());
        Assert.Equal("Read by an agent: session 114 \"Factory Inbox - Delivery Lead\", 2026-11-07 14:02 UTC",
            m.GetProperty("agentLine").GetString());
        Assert.Equal("unread", m.GetProperty("state").GetString());
    }

    // ---------- reply ----------

    [Fact]
    public async Task Reply_ToARunningSession_GoesIntoItsFleetInbox_AsTheAgents_AndTheMessageStaysUnread()
    {
        var id = await SendFrom(Developer, "Shall I tag v2.25?");

        var (status, body) = await Post($"/sessions/{Lead}/inbox/{id}/reply", Lead, new { text = "Yes, tag it." });

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(0, _deliveries);
        Assert.Equal(YourInboxWords.AgentReplyDone, body.GetProperty("done").GetString());
        Assert.Equal("Sent to 412 Release - Developer, which reads it when it is free. Written by an agent: session 114 \"Factory Inbox - Delivery Lead\"",
            body.GetProperty("reply").GetProperty("label").GetString());
        var m = body.GetProperty("message");
        Assert.Equal("unread", m.GetProperty("state").GetString());
        Assert.Equal("Replied to by an agent: session 114 \"Factory Inbox - Delivery Lead\", 2026-11-07 14:02 UTC",
            m.GetProperty("agentLine").GetString());

        var delivered = Assert.Single(_fleet.ReadInbox(A, Developer, includeRecent: false).Unread);
        Assert.Contains("Yes, tag it.", delivered.Text);
        Assert.Contains("session 114 \"Factory Inbox - Delivery Lead\"", delivered.Text);
        Assert.Contains("The owner did not write this reply", delivered.Text);
        // An ordinary message FROM the agent, never an unattributed Gateway notice (review of #4767, finding 1).
        Assert.Equal(Lead, delivered.FromSessionId);
        Assert.Equal(FleetMessageKinds.Message, delivered.Kind);
        Assert.DoesNotContain("The owner replied", delivered.Text);
        var reply = Assert.Single(_store.Replies(A, Guid.Parse(id)));
        Assert.Equal(Lead, reply.SentBy);
    }

    [Fact]
    public async Task Reply_FromAnUnrelatedSession_IsRefusedByTheMessagePolicy_AndNothingIsDeliveredOrRecorded()
    {
        // The Booking Boss neither started the Developer nor was started by it, and holds no message link: the normal
        // policy refuses it, and that refusal is the route's answer.
        var id = await SendFrom(Developer, "Shall I tag v2.25?");

        var (status, body) = await Post($"/sessions/{BookingBoss}/inbox/{id}/reply", BookingBoss, new { text = "Tag it." });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("reply_refused", Code(body));
        Assert.Contains("Nothing was sent.", body.GetProperty("error").GetString());
        Assert.Empty(_fleet.ReadInbox(A, Developer, includeRecent: false).Unread);
        Assert.Empty(_store.Replies(A, Guid.Parse(id)));
        Assert.Empty(_store.AgentActions(A, [Guid.Parse(id)]));
        Assert.Equal(0, _deliveries);
    }

    [Fact]
    public async Task Reply_ToItsOwnMessage_IsRefused_AndNothingIsDelivered()
    {
        var id = await SendFrom(Lead);

        var (status, body) = await Post($"/sessions/{Lead}/inbox/{id}/reply", Lead, new { text = "Noted." });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("reply_to_yourself", Code(body));
        Assert.Equal(0, _deliveries);
    }

    [Fact]
    public async Task Reply_ToAnEndedSession_OffersANewSession_WhosePromptNamesTheAgent()
    {
        var id = await SendFrom(Ended, "Draft is up");

        var (status, body) = await Post($"/sessions/{Lead}/inbox/{id}/reply", Lead, new { text = "Make it shorter." });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("session_ended", Code(body));
        var prompt = Assert.Single(body.GetProperty("offers").EnumerateArray()).GetProperty("prompt").GetString()!;
        Assert.Contains("A reply from an agent, session 114 \"Factory Inbox - Delivery Lead\" (not from the owner)", prompt);
        Assert.DoesNotContain("The owner's reply", prompt);
        Assert.Equal(0, _deliveries);
    }

    [Fact]
    public async Task Reply_ToAFactorysMessage_IsLeftInItsFactoryInbox_AsFromTheAgent()
    {
        var id = FactoryMessage(A, "website", "Nightly build failed");

        var (status, body) = await Post($"/sessions/{Lead}/inbox/{id}/reply", Lead, new { text = "Rerun it after the fix." });

        Assert.Equal(HttpStatusCode.Created, status);
        var left = Assert.Single(_factoryInbox.Thread(A, "website", 10).Messages);
        Assert.Equal(FactoryInboxWords.Surfaces.Session, left.Surface);
        Assert.Equal(Lead, left.SentBy);
        Assert.Equal("From an agent: session 114 \"Factory Inbox - Delivery Lead\"", FactoryInboxFold.Message(left, null).FromLabel);
        Assert.Single(_wakes);
    }

    [Fact]
    public async Task Reply_IntoItsOwnFactory_IsRefused_AndNothingIsLeft()
    {
        var id = FactoryMessage(A, "booking", "Nightly booking sync failed");

        var (status, body) = await Post($"/sessions/{BookingBoss}/inbox/{id}/reply", BookingBoss, new { text = "On it." });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("own_factory", Code(body));
        Assert.Empty(_factoryInbox.Thread(A, "booking", 10).Messages);
    }

    // ---------- leaving a message in a factory ----------

    [Fact]
    public async Task FactorySend_IsLeftAsFromTheAgent_AndTheFactoryIsWoken()
    {
        var (status, body) = await Post($"/sessions/{Lead}/factories/booking/inbox", Lead, new { text = "Please check the Friday bookings." });

        Assert.Equal(HttpStatusCode.Created, status);
        var m = body.GetProperty("message");
        Assert.Equal("session", m.GetProperty("surface").GetString());
        Assert.Equal("From an agent: session 114 \"Factory Inbox - Delivery Lead\"", m.GetProperty("fromLabel").GetString());
        Assert.Equal("Left by an agent: session 114 \"Factory Inbox - Delivery Lead\"",
            m.GetProperty("trail")[0].GetProperty("label").GetString());
        var left = Assert.Single(_factoryInbox.Thread(A, "booking", 10).Messages);
        Assert.Equal("Please check the Friday bookings.", left.Text);
        Assert.Equal(FactoryInboxWords.States.Sent, left.State);
        Assert.Equal(Lead, left.SentBy);
        Assert.Equal(114, left.SentBySessionNumber);
        Assert.Equal("Factory Inbox - Delivery Lead", left.SentBySessionName);
        Assert.Equal((A, "booking", _now), Assert.Single(_wakes));
    }

    [Fact]
    public async Task FactorySend_FromThatFactorysOwnSeat_IsRefused_ButAnotherFactoryTakesIt()
    {
        var (refusedStatus, refused) = await Post($"/sessions/{BookingBoss}/factories/booking/inbox", BookingBoss, new { text = "Loop" });
        var (otherStatus, _) = await Post($"/sessions/{BookingBoss}/factories/website/inbox", BookingBoss, new { text = "Not a loop" });

        Assert.Equal(HttpStatusCode.Forbidden, refusedStatus);
        Assert.Equal("own_factory", Code(refused));
        Assert.Empty(_factoryInbox.Thread(A, "booking", 10).Messages);
        Assert.Equal(HttpStatusCode.Created, otherStatus);
        Assert.Single(_factoryInbox.Thread(A, "website", 10).Messages);
    }

    [Fact]
    public async Task FactorySend_ToAnUnknownFactory_OrWithNoText_IsRefused()
    {
        var (unknownStatus, unknown) = await Post($"/sessions/{Lead}/factories/nowhere/inbox", Lead, new { text = "Hello" });
        var (emptyStatus, empty) = await Post($"/sessions/{Lead}/factories/booking/inbox", Lead, new { text = "  " });

        Assert.Equal(HttpStatusCode.Conflict, unknownStatus);
        Assert.Equal("factory_not_found", Code(unknown));
        Assert.Equal(HttpStatusCode.BadRequest, emptyStatus);
        Assert.Equal("message_empty", Code(empty));
        Assert.Empty(_wakes);
    }

    [Fact]
    public async Task FactorySend_TheOwnersRouteToAFactory_IsNotOpenedByThis()
    {
        // The owner's send to a factory lives in FactoryInboxEndpoints and is not mapped here; an agent's message goes only
        // through its own route, which names it.
        var (status, _) = await Post("/gateway/factory/booking/inbox", Lead, new { text = "As the owner" });

        Assert.Equal(HttpStatusCode.NotFound, status);
    }
}

/// <summary>
/// THE AGENT LINE IS FOLDED ONCE, ON THE GATEWAY (CLAUDE.md rule 7): one function makes every line, and the message carries
/// the finished string, so the phone and the Cockpit print it and decide nothing.
/// </summary>
public sealed class YourInboxAgentLineTests
{
    private static readonly DateTime At = new(2026, 11, 7, 14, 2, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(YourInboxWords.AgentActs.Read, "Read by an agent")]
    [InlineData(YourInboxWords.AgentActs.MarkedRead, "Marked read by an agent")]
    [InlineData(YourInboxWords.AgentActs.MarkedUnread, "Marked unread by an agent")]
    [InlineData(YourInboxWords.AgentActs.Archived, "Archived by an agent")]
    [InlineData(YourInboxWords.AgentActs.Restored, "Moved back to the inbox by an agent")]
    [InlineData(YourInboxWords.AgentActs.Replied, "Replied to by an agent")]
    [InlineData(YourInboxWords.AgentActs.LeftInFactory, "Left by an agent")]
    public void AgentLine_NamesTheActTheSessionAndTheTime(string act, string label)
        => Assert.Equal($"{label}: session 114 \"Factory Inbox - Delivery Lead\", 2026-11-07 14:02 UTC",
            YourInboxWords.AgentLine(act, 114, "Factory Inbox - Delivery Lead", At));

    [Theory]
    [InlineData(114, null, "session 114")]
    [InlineData(null, "Booking - Boss", "session \"Booking - Boss\"")]
    [InlineData(null, null, "a session")]
    [InlineData(7, "Two\nlines", "session 7 \"Two lines\"")]
    public void AgentSession_NeverShowsAnId_AndSaysWhatItKnows(int? number, string? name, string expected)
        => Assert.Equal(expected, YourInboxWords.AgentSession(number, name));

    [Theory]
    [InlineData("unread", "archived", YourInboxWords.AgentActs.Archived)]
    [InlineData("read", "archived", YourInboxWords.AgentActs.Archived)]
    [InlineData("archived", "unread", YourInboxWords.AgentActs.Restored)]
    [InlineData("archived", "read", YourInboxWords.AgentActs.Restored)]
    [InlineData("unread", "read", YourInboxWords.AgentActs.MarkedRead)]
    [InlineData("read", "unread", YourInboxWords.AgentActs.MarkedUnread)]
    [InlineData("read", "read", null)]
    public void AgentActFor_SaysWhatAStateChangeIs(string was, string state, string? act)
        => Assert.Equal(act, YourInboxWords.AgentActFor(was, state));

    [Fact]
    public void Message_CarriesEveryLine_OldestFirst_AndTheNewestAsItsLine()
    {
        var tenant = new CcDirector.Core.Tenancy.TenantId("acct-a");
        var message = new CcDirector.Gateway.Data.Entities.YourInboxMessageEntity
        {
            TenantId = tenant.Value, Subject = "s", Body = "b", FromKind = YourInboxWords.Senders.Session, SessionId = "x",
            State = YourInboxWords.States.Archived, SentAtUtc = At,
        };
        CcDirector.Gateway.Data.Entities.YourInboxAgentActionEntity Act(string act, int minutes) => new()
        {
            TenantId = tenant.Value, MessageId = message.Id, Action = act, SessionId = "x", SessionNumber = 114,
            SessionName = "Lead", AtUtc = At.AddMinutes(minutes),
        };

        var dto = YourInboxFold.Message(message, new Dictionary<string, string>(), null,
            [Act(YourInboxWords.AgentActs.Archived, 5), Act(YourInboxWords.AgentActs.Read, 0)]);

        Assert.Equal(["read", "archived"], dto.AgentActions.Select(a => a.Act).ToArray());
        Assert.Equal("Archived by an agent: session 114 \"Lead\", 2026-11-07 14:07 UTC", dto.AgentLine);
        Assert.Null(YourInboxFold.Message(message, new Dictionary<string, string>()).AgentLine);
    }
}
