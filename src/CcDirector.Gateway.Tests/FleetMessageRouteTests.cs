using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Util;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The queued message on a REAL Gateway with a tunnel Director connected (the Message Load mission,
/// slice 1): <c>POST /sessions/{sid}/message</c>, <c>POST /fleet/broadcast</c>, <c>GET /fleet/inbox</c>,
/// and the refusal of every agent path that types into a session.
///
/// WHAT ONLY A BOOTED HOST CAN PROVE:
///
///  - NOTHING IS TYPED. The Director here records every command the Gateway sends down the tunnel. A queued
///    message must send none - the whole point of the mission is that a message never lands mid-turn - and a
///    unit test of the service cannot see the tunnel at all.
///  - THE GUARD AND THE ROUTE AGREE. The guard's unit tests prove its decision about a PATH; only a real host
///    proves the inbox route is mapped where the guard allows it, and that the refused input routes are refused
///    before any command leaves the Gateway.
///  - THE RELATIONSHIP COMES FROM THE ROSTER. The supervisor links here arrive the way a Director pushes them.
/// </summary>
public sealed class FleetMessageRouteTests : IAsyncLifetime
{
    private const string Token = "fleet-message-route-token";
    private const string DirectorId = "director-fleet-message-route";
    private const string Machine = "FLEET-MESSAGE-MACHINE";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _manager = Guid.NewGuid().ToString();
    private readonly string _workerA = Guid.NewGuid().ToString();
    private readonly string _workerB = Guid.NewGuid().ToString();
    private readonly string _stranger = Guid.NewGuid().ToString();

    private readonly ConcurrentQueue<DirectorCommand> _commands = new();

    private GatewayHost _gateway = null!;
    private FakeTunnelDirector _director = null!;
    private HttpClient _owner = null!;
    private HttpClient _asManager = null!;
    private HttpClient _asWorkerA = null!;
    private HttpClient _asWorkerB = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-fleet-message-route-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        _owner = Client(Token);
        _asManager = Client(KeyFor(_manager));
        _asWorkerA = Client(KeyFor(_workerA));
        _asWorkerB = Client(KeyFor(_workerB));

        // Every command is recorded and answered "ok", so a refusal that let one through would be SEEN rather
        // than masked by a failing Director.
        _director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine, cmd =>
        {
            _commands.Enqueue(cmd);
            return cmd.Verb == "compact-context"
                ? FakeTunnelDirector.Ok(new CompactContextResponse())
                : FakeTunnelDirector.Ok(new { accepted = true });
        });
        await _director.PushSnapshotAsync(
            Row(_manager, "Mission - Manager", controller: null),
            Row(_workerA, "Mission - Worker - A", controller: _manager),
            Row(_workerB, "Mission - Worker - B", controller: _manager),
            Row(_stranger, "Someone Else", controller: null));
    }

    public async Task DisposeAsync()
    {
        await _director.DisposeAsync();
        foreach (var c in new[] { _owner, _asManager, _asWorkerA, _asWorkerB }) c.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    private static SessionDto Row(string sid, string name, string? controller) => new()
    {
        SessionId = sid,
        DirectorId = DirectorId,
        Name = name,
        MachineName = Machine,
        ActivityState = "Working",
        ControllerSessionId = controller,
        LastActivityAt = DateTime.UtcNow,
    };

    private string KeyFor(string sid)
    {
        var key = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(TenantId.Local, DirectorId, sid,
            GatewaySessionKey.Hash(key), DateTime.UtcNow.AddHours(1)));
        return key;
    }

    private HttpClient Client(string bearer)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r)
        => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static string S(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private Task<HttpResponseMessage> Send(HttpClient from, string to, string text, string? kind = null)
        => from.PostAsJsonAsync($"sessions/{to}/message", kind is null ? new { text } : (object)new { text, kind });

    private async Task<FleetInboxResponse> Inbox(HttpClient who, bool all = false)
    {
        var r = await who.GetAsync(all ? "fleet/inbox?all=true" : "fleet/inbox");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<FleetInboxResponse>(Web))!;
    }

    /// <summary>
    /// Every command the Gateway sent this Director EXCEPT its own display pushes. The Gateway stamps each
    /// session's resolved role and display state down the tunnel on its own schedule (<c>set-resolved-role</c>,
    /// <c>set-display-state</c>); those write no keystrokes and have nothing to do with a message. Everything
    /// else - prompt, interrupt, escape, compact-context, and any verb added later - is counted, so a new way of
    /// reaching a session's terminal shows up here rather than hiding in an allow list of known input verbs.
    /// </summary>
    private string[] VerbsSent() => _commands.Select(c => c.Verb).Where(v => !v.StartsWith("set-", StringComparison.Ordinal)).ToArray();

    // =========================================================================================
    // Queued, read once, and never typed
    // =========================================================================================

    [Fact]
    public async Task A_supervisor_message_is_queued_read_in_full_once_and_nothing_is_typed()
    {
        const string text = "Please rebase onto main.\nThen run the unit tests.\n\nReport when done.";

        var sent = await Send(_asManager, _workerA, text);

        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var body = await Body(sent);
        Assert.Equal("queued", S(body, "status"));
        Assert.Equal(_workerA, S(body, "recipientSessionId"));
        var messageId = S(body, "messageId");
        Assert.Equal(32, messageId.Length);

        var first = await Inbox(_asWorkerA);
        Assert.Equal(_workerA, first.SessionId);
        Assert.Equal(1, first.UnreadCount);
        var m = Assert.Single(first.Unread);
        Assert.Equal(messageId, m.MessageId);
        Assert.Equal(text, m.Text);
        Assert.Equal(_manager, m.FromSessionId);
        Assert.Equal("Mission - Manager", m.FromName);
        Assert.Equal(Machine, m.FromMachine);
        Assert.Equal("message", m.Kind);
        Assert.NotNull(m.ReadAtUtc);

        var second = await Inbox(_asWorkerA);
        Assert.Equal(0, second.UnreadCount);
        Assert.Empty(second.Unread);

        var withRecent = await Inbox(_asWorkerA, all: true);
        Assert.Equal(messageId, Assert.Single(withRecent.Recent).MessageId);
        // The command line reads these two names off the wire (message inbox --all, inspection 2 ruling 1).
        var raw = await Body(await _asWorkerA.GetAsync("fleet/inbox?all=true"));
        Assert.Equal(1, raw.GetProperty("recentTotal").GetInt32());
        Assert.False(raw.GetProperty("truncated").GetBoolean());

        // THE POINT OF THE MISSION: the Director was told nothing. No prompt, no keystroke, no command at all.
        Assert.Empty(VerbsSent());
    }

    // ---------- The row line (slice 4) ----------

    private async Task<JsonElement> RosterRow(string sid)
    {
        var r = await _owner.GetAsync("sessions");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await Body(r);
        var rows = body.ValueKind == JsonValueKind.Array ? body : body.GetProperty("sessions");
        return rows.EnumerateArray().Single(e => string.Equals(S(e, "sessionId"), sid, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Line(JsonElement row) =>
        row.TryGetProperty("inboxLine", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The first display push for this session, among the commands after the first <paramref name="skip"/>,
    /// that matches - waited for, because the push is sent fire-and-forget.</summary>
    private async Task<SetDisplayStateRequest?> DisplayPushFor(string sid, int skip, Func<SetDisplayStateRequest, bool> match)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var found = _commands
                .Skip(skip)
                .Where(c => c.Verb == "set-display-state" && string.Equals(c.SessionId, sid, StringComparison.OrdinalIgnoreCase))
                .Select(c => JsonSerializer.Deserialize<SetDisplayStateRequest>(c.PayloadJson, Web)!)
                .FirstOrDefault(match);
            if (found is not null) return found;
            await Task.Delay(50);
        }
        return null;
    }

    [Fact]
    public async Task The_roster_the_session_read_and_the_desktop_push_carry_the_row_line_until_it_is_read()
    {
        Assert.Null(Line(await RosterRow(_workerA)));

        Assert.Equal(HttpStatusCode.OK, (await Send(_asManager, _workerA, "first")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(_asWorkerB, _manager, "a report", kind: "report")).StatusCode);

        // The wire names are the contract the Cockpit and the phone read: pinned here as literals.
        Assert.Equal("1 message waiting", Line(await RosterRow(_workerA)));
        Assert.Equal("1 message waiting", Line(await RosterRow(_manager)));
        Assert.Null(Line(await RosterRow(_workerB)));

        var single = await Body(await _owner.GetAsync($"sessions/{_workerA}"));
        Assert.Equal("1 message waiting", Line(single));

        // The desktop cannot read the inbox: the display push carries the same words down the tunnel.
        var before = _commands.Count;
        _gateway.SweepDisplayState();
        var pushed = await DisplayPushFor(_workerA, before, p => p.InboxLine is not null);
        Assert.NotNull(pushed);
        Assert.Equal("1 message waiting", pushed!.InboxLine);

        // Read is the acknowledgement: the line goes, on the roster and down the push.
        Assert.Single((await Inbox(_asWorkerA)).Unread);
        Assert.Null(Line(await RosterRow(_workerA)));
        Assert.Equal("1 message waiting", Line(await RosterRow(_manager)));
        var afterRead = _commands.Count;
        _gateway.SweepDisplayState();
        var cleared = await DisplayPushFor(_workerA, afterRead, p => p.InboxLine is null);
        Assert.NotNull(cleared);
        Assert.Empty(VerbsSent());
    }

    [Fact]
    public async Task One_sessions_inbox_is_not_another_sessions()
    {
        await Send(_asManager, _workerA, "for A only");

        Assert.Empty((await Inbox(_asWorkerB)).Unread);
        Assert.Empty((await Inbox(_asManager)).Unread);
        Assert.Single((await Inbox(_asWorkerA)).Unread);
    }

    [Fact]
    public async Task A_worker_may_report_to_its_supervisor()
    {
        var r = await Send(_asWorkerA, _manager, "Rebased and green.", kind: "report");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("queued", S(await Body(r), "status"));
        Assert.Equal("report", Assert.Single((await Inbox(_asManager)).Unread).Kind);
    }

    // =========================================================================================
    // Refusals
    // =========================================================================================

    [Fact]
    public async Task A_sibling_is_refused_with_the_reason_and_nothing_is_written()
    {
        var r = await Send(_asWorkerA, _workerB, "psst");

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var body = await Body(r);
        Assert.Equal("refused", S(body, "status"));
        Assert.Contains("only the session that started you and the sessions you started", S(body, "error"));
        Assert.Contains("Put it in your report", S(body, "error"));
        Assert.Equal(0, _gateway.FleetMessages.CountUnread(TenantId.Local, _workerB));
    }

    [Fact]
    public async Task A_session_with_no_supervisor_cannot_reach_a_stranger()
    {
        var r = await Send(_asManager, _stranger, "hello");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task The_second_message_to_one_worker_inside_ten_minutes_is_refused_with_429()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(_asManager, _workerA, "one")).StatusCode);

        var r = await Send(_asManager, _workerA, "two");

        Assert.Equal((HttpStatusCode)429, r.StatusCode);
        Assert.Contains("one message per recipient every 10 minutes", S(await Body(r), "error"));
    }

    [Fact]
    public async Task An_unread_repeat_is_answered_duplicate_not_refused()
    {
        await Send(_asWorkerA, _manager, "same words", kind: "report");

        var r = await Send(_asWorkerA, _manager, "same words", kind: "report");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("duplicate", S(await Body(r), "status"));
        Assert.Single((await Inbox(_asManager)).Unread);
    }

    [Fact]
    public async Task Message_ask_is_refused_with_a_sentence_and_nothing_is_queued()
    {
        var r = await _asManager.PostAsJsonAsync($"sessions/{_workerA}/message",
            new { text = "are you done?", waitForIdle = true, timeoutMs = 1000 });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("message ask was removed", S(await Body(r), "error"));
        Assert.Equal(0, _gateway.FleetMessages.CountUnread(TenantId.Local, _workerA));
        Assert.Empty(VerbsSent());
    }

    [Theory]
    [InlineData("system")]
    [InlineData("everyone")]
    [InlineData("team")]
    [InlineData("shout")]
    public async Task A_caller_cannot_choose_a_kind_the_gateway_decides(string kind)
    {
        var r = await Send(_asManager, _workerA, "hi", kind);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(0, _gateway.FleetMessages.CountUnread(TenantId.Local, _workerA));
    }

    [Fact]
    public async Task A_device_key_has_no_sender_and_no_inbox()
    {
        var send = await Send(_owner, _workerA, "hi");
        Assert.Equal(HttpStatusCode.Forbidden, send.StatusCode);

        var inbox = await _owner.GetAsync("fleet/inbox");
        Assert.Equal(HttpStatusCode.Forbidden, inbox.StatusCode);
        Assert.Contains("an inbox belongs to a session", S(await Body(inbox), "error"));
    }

    [Fact]
    public async Task An_unknown_recipient_is_not_found()
    {
        var r = await Send(_asManager, Guid.NewGuid().ToString(), "hi");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    // =========================================================================================
    // Broadcast: the sender's own workers only
    // =========================================================================================

    [Fact]
    public async Task Send_all_queues_one_copy_for_each_of_the_senders_workers_and_nobody_else()
    {
        var r = await _asManager.PostAsJsonAsync("fleet/broadcast", new { text = "stand up" });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = (await r.Content.ReadFromJsonAsync<FleetBroadcastResponse>(Web))!;
        Assert.False(body.Denied);
        Assert.Equal(new[] { _workerA, _workerB }.OrderBy(x => x),
            body.Results.Select(x => x.RecipientSessionId).OrderBy(x => x));
        Assert.All(body.Results, x => Assert.Equal("queued", x.Status));

        Assert.Equal("team", Assert.Single((await Inbox(_asWorkerA)).Unread).Kind);
        Assert.Single((await Inbox(_asWorkerB)).Unread);
        Assert.Equal(0, _gateway.FleetMessages.CountUnread(TenantId.Local, _stranger));
        Assert.Empty((await Inbox(_asManager)).Unread);
        Assert.Empty(VerbsSent());
    }

    [Fact]
    public async Task Send_all_from_a_session_with_no_workers_reaches_nobody_and_says_so()
    {
        var r = await _asWorkerA.PostAsJsonAsync("fleet/broadcast", new { text = "hello team" });

        var body = (await r.Content.ReadFromJsonAsync<FleetBroadcastResponse>(Web))!;
        Assert.Empty(body.Results);
        Assert.Equal("You have no workers to message.", body.Warning);
        // Its sibling and its supervisor are not its workers.
        Assert.Equal(0, _gateway.FleetMessages.CountUnread(TenantId.Local, _workerB));
        Assert.Equal(0, _gateway.FleetMessages.CountUnread(TenantId.Local, _manager));
    }

    [Fact]
    public async Task Everyone_without_a_grant_is_denied_and_queues_nothing()
    {
        var r = await _asWorkerA.PostAsJsonAsync("fleet/broadcast", new { text = "all hands", everyone = true, reason = "because" });

        var body = (await r.Content.ReadFromJsonAsync<FleetBroadcastResponse>(Web))!;
        Assert.True(body.Denied);
        Assert.Contains("human-issued --grant", body.DeniedReason);
        Assert.Equal(0, _gateway.FleetMessages.CountUnread(TenantId.Local, _stranger));
    }

    [Fact]
    public async Task Everyone_with_a_grant_queues_for_every_other_session_and_types_nothing()
    {
        var mint = await _owner.PostAsync("fleet/broadcast-grants", null);
        Assert.Equal(HttpStatusCode.OK, mint.StatusCode);
        var grantId = S(await Body(mint), "grantId");
        Assert.False(string.IsNullOrEmpty(grantId));

        var r = await _asWorkerA.PostAsJsonAsync("fleet/broadcast",
            new { text = "all hands", everyone = true, reason = "the owner asked", grantId });

        var body = (await r.Content.ReadFromJsonAsync<FleetBroadcastResponse>(Web))!;
        Assert.False(body.Denied);
        Assert.Equal(3, body.Results.Count(x => x.Status == "queued"));
        Assert.Equal(1, _gateway.FleetMessages.CountUnread(TenantId.Local, _stranger));
        Assert.Equal("everyone", Assert.Single((await Inbox(_asManager)).Unread).Kind);
        Assert.Empty(VerbsSent());
    }

    // =========================================================================================
    // No agent types into a session (ruling 17)
    // =========================================================================================

    [Theory]
    [InlineData("prompt")]
    [InlineData("interrupt")]
    [InlineData("escape")]
    public async Task A_session_key_may_not_type_into_interrupt_or_escape_its_own_worker(string verb)
    {
        var r = await _asManager.PostAsJsonAsync($"sessions/{_workerA}/{verb}", new { text = "do this now", appendEnter = true });

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(AgentInputRefusal.Typing, S(await Body(r), "error"));
        Assert.Empty(VerbsSent());
    }

    [Fact]
    public async Task A_session_key_may_not_fan_a_prompt_out()
    {
        var r = await _asManager.PostAsJsonAsync("fanout",
            new { sessionIds = new[] { _workerA, _workerB }, text = "go", waitForIdle = false });

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(AgentInputRefusal.Typing, S(await Body(r), "error"));
        Assert.Empty(VerbsSent());
    }

    [Fact]
    public async Task The_owner_still_interrupts_so_the_refusal_is_about_the_key_not_the_route()
    {
        var r = await _owner.PostAsync($"sessions/{_workerA}/interrupt", null);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(new[] { "interrupt" }, VerbsSent());
    }

    [Fact]
    public async Task A_session_key_may_not_compact_and_continue()
    {
        var r = await _asManager.PostAsJsonAsync($"sessions/{_workerA}/compact-context", new { continuePrompt = "continue" });

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(AgentInputRefusal.CompactContinue, S(await Body(r), "error"));
        Assert.Empty(VerbsSent());
    }

    [Fact]
    public async Task A_session_key_may_still_compact_without_continuing()
    {
        var r = await _asManager.PostAsJsonAsync($"sessions/{_workerA}/compact-context", new { });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(new[] { "compact-context" }, VerbsSent());
        var cmd = _commands.Single(c => c.Verb == "compact-context");
        var payload = JsonSerializer.Deserialize<CompactContextRequest>(cmd.PayloadJson, Web);
        Assert.True(string.IsNullOrEmpty(payload?.ContinuePrompt));
    }

    // =========================================================================================
    // A session key cannot invent a supervisor by spawning (inspection 1, ruling 2)
    // =========================================================================================

    private Task<HttpResponseMessage> Spawn(HttpClient who, string controller)
        => who.PostAsJsonAsync($"directors/{DirectorId}/sessions",
            new { repoPath = "/repos/devthrottle", agent = "ClaudeCode", name = "Mission - Worker - C", controllerSessionId = controller });

    [Fact]
    public async Task A_session_key_spawn_naming_an_unrelated_session_as_owner_is_refused_and_nothing_is_started()
    {
        // Without the ruling, worker A could start a child "controlled by" the stranger, and that child could then
        // write into the stranger's inbox as its worker.
        var r = await Spawn(_asWorkerA, _stranger);

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains(_stranger, S(await Body(r), "error"));
        Assert.DoesNotContain("create", VerbsSent());
    }

    [Fact]
    public async Task A_session_key_spawn_naming_itself_as_owner_is_started_with_that_owner()
    {
        var r = await Spawn(_asWorkerA, _workerA.ToUpperInvariant());

        Assert.NotEqual(HttpStatusCode.Forbidden, r.StatusCode);
        var create = Assert.Single(_commands, c => c.Verb == "create");
        var payload = JsonSerializer.Deserialize<NewSessionRequest>(create.PayloadJson, Web)!;
        Assert.Equal(_workerA, payload.ControllerSessionId);
        Assert.Equal(_workerA, payload.ParentSessionId);
    }

    [Fact]
    public async Task The_owner_may_still_start_a_session_owned_by_any_session()
    {
        // The shared token is the owner's; ruling 2 binds session keys only.
        var r = await Spawn(_owner, _stranger);

        Assert.NotEqual(HttpStatusCode.Forbidden, r.StatusCode);
        var create = Assert.Single(_commands, c => c.Verb == "create");
        Assert.Equal(_stranger, JsonSerializer.Deserialize<NewSessionRequest>(create.PayloadJson, Web)!.ControllerSessionId);
    }

    // =========================================================================================
    // A session raises its own hand, and only its own (inspection 1, ruling 3)
    // =========================================================================================

    [Fact]
    public async Task A_session_key_may_raise_its_own_hand()
    {
        // `session raise` is this mission's report channel. The live Gateway refused it to every agent until ruling 3.
        var r = await _asWorkerA.PostAsJsonAsync($"sessions/{_workerA}/needs-manager", new { raised = true, reason = "slice 1 fix round pushed" });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await Body(r);
        Assert.True(body.GetProperty("raised").GetBoolean());
        Assert.Equal("slice 1 fix round pushed", S(body, "reason"));
        Assert.Empty(VerbsSent());
    }

    [Fact]
    public async Task A_session_key_may_not_raise_another_sessions_hand()
    {
        var r = await _asWorkerA.PostAsJsonAsync($"sessions/{_workerB}/needs-manager", new { raised = true, reason = "speaking for B" });

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(
            "a session may raise only its own hand: run cc-devthrottle session raise from inside the session that " +
            "needs its supervisor",
            S(await Body(r), "error"));
    }

    // =========================================================================================
    // Replies without blocking (slice 3, ruling 10)
    // =========================================================================================

    private async Task<JsonElement> Ask(HttpClient from, string to, string text, int? replyByMinutes = null)
    {
        var r = await from.PostAsJsonAsync($"sessions/{to}/message",
            replyByMinutes is null ? new { text, replyWanted = true } : (object)new { text, replyWanted = true, replyByMinutes });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await Body(r);
        Assert.Equal("queued", S(body, "status"));
        return body;
    }

    private Task<HttpResponseMessage> Reply(HttpClient from, string id, string text)
        => from.PostAsJsonAsync("fleet/reply", new { id, text });

    [Fact]
    public async Task A_reply_round_trip_lands_in_the_askers_inbox_with_the_question_and_types_nothing()
    {
        var asked = await Ask(_asManager, _workerA, "which branch are you on?", replyByMinutes: 30);
        var correlation = S(asked, "correlationId");
        Assert.Matches("^[0-9a-f]{32}$", correlation);
        var due = asked.GetProperty("replyByUtc").GetDateTime();
        Assert.InRange(due - DateTime.UtcNow, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));

        // The worker sees that an answer is wanted and how to give it - the wire names pinned.
        var raw = await Body(await _asWorkerA.GetAsync("fleet/inbox"));
        var question = raw.GetProperty("unread")[0];
        Assert.True(question.GetProperty("replyWanted").GetBoolean());
        Assert.Equal(correlation, S(question, "correlationId"));
        Assert.Equal($"cc-devthrottle message reply {correlation} \"<your answer>\"", S(question, "replyHint"));
        Assert.Equal(JsonValueKind.Null, question.GetProperty("inReplyTo").ValueKind);

        var r = await Reply(_asWorkerA, correlation, "mission/message-load");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var answered = await Body(r);
        Assert.Equal("queued", S(answered, "status"));
        Assert.Equal(_manager, S(answered, "recipientSessionId"));
        Assert.Equal(S(asked, "messageId"), S(answered, "inReplyToMessageId"));

        var inbox = await Body(await _asManager.GetAsync("fleet/inbox"));
        var reply = inbox.GetProperty("unread")[0];
        Assert.Equal("reply", S(reply, "kind"));
        Assert.Equal(_workerA, S(reply, "fromSessionId"));
        Assert.Equal("mission/message-load", S(reply, "text"));
        var about = reply.GetProperty("inReplyTo");
        Assert.Equal(S(asked, "messageId"), S(about, "messageId"));
        Assert.Equal("which branch are you on?", S(about, "text"));
        Assert.False(about.GetProperty("late").GetBoolean());
        Assert.Empty(VerbsSent());
    }

    [Fact]
    public async Task A_reply_from_a_session_that_was_not_asked_is_refused()
    {
        var asked = await Ask(_asManager, _workerA, "status?");

        var r = await Reply(_asWorkerB, S(asked, "correlationId"), "I will answer for A");

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var body = await Body(r);
        Assert.Equal("refused", S(body, "status"));
        Assert.Contains("Only the session message " + S(asked, "messageId") + " was sent to may reply to it", S(body, "error"));
        Assert.Equal("", S(body, "recipientSessionId"));
        Assert.Equal(0, (await Inbox(_asManager)).UnreadCount);
    }

    [Fact]
    public async Task A_reply_is_allowed_after_the_relationship_is_gone_where_a_message_is_not()
    {
        var asked = await Ask(_asManager, _workerA, "are you done?");
        // The worker is re-pushed with no supervisor: it is now a stranger to the manager.
        await _director.PushSnapshotAsync(
            Row(_manager, "Mission - Manager", controller: null),
            Row(_workerA, "Mission - Worker - A", controller: null),
            Row(_workerB, "Mission - Worker - B", controller: _manager),
            Row(_stranger, "Someone Else", controller: null));

        // Control: an ordinary message the same way is refused by the relationship rule.
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(_asWorkerA, _manager, "hello")).StatusCode);

        var r = await Reply(_asWorkerA, S(asked, "correlationId"), "yes, done");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("yes, done", Assert.Single((await Inbox(_asManager)).Unread).Text);
    }

    [Fact]
    public async Task A_reply_is_not_held_to_the_spacing_that_refuses_a_second_message()
    {
        var asked = await Ask(_asManager, _workerA, "ready?");
        Assert.Equal(HttpStatusCode.OK, (await Send(_asWorkerA, _manager, "first")).StatusCode);
        // Control: a second message inside ten minutes is refused.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(_asWorkerA, _manager, "second")).StatusCode);

        var r = await Reply(_asWorkerA, S(asked, "messageId"), "ready");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("queued", S(await Body(r), "status"));
    }

    [Fact]
    public async Task A_second_reply_is_refused_and_an_unknown_id_is_not_found()
    {
        var asked = await Ask(_asManager, _workerA, "one question");
        Assert.Equal(HttpStatusCode.OK, (await Reply(_asWorkerA, S(asked, "correlationId"), "one answer")).StatusCode);

        var second = await Reply(_asWorkerA, S(asked, "correlationId"), "two answers");
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("one reply per question", S(await Body(second), "error"));

        var unknown = await Reply(_asWorkerA, "ffffffffffffffffffffffffffffffff", "?");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("refused", S(await Body(unknown), "status"));
    }

    [Fact]
    public async Task A_reply_to_a_message_that_did_not_ask_is_refused()
    {
        var sent = await Body(await Send(_asManager, _workerA, "fyi only"));

        var r = await Reply(_asWorkerA, S(sent, "messageId"), "noted");

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("did not ask for a reply", S(await Body(r), "error"));
    }

    [Theory]
    [InlineData("{\"text\":\"q\",\"replyByMinutes\":5}", "replyByMinutes is a reply deadline and needs replyWanted: true")]
    [InlineData("{\"text\":\"q\",\"replyWanted\":true,\"replyByMinutes\":0}", "replyByMinutes must be between 1 and 1440; 0 was given.")]
    [InlineData("{\"text\":\"q\",\"replyWanted\":true,\"replyByMinutes\":1441}", "replyByMinutes must be between 1 and 1440; 1441 was given.")]
    public async Task A_bad_reply_deadline_is_refused_and_nothing_is_queued(string json, string error)
    {
        var r = await _asManager.PostAsync($"sessions/{_workerA}/message",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(error, S(await Body(r), "error"));
        Assert.Equal(0, (await Inbox(_asWorkerA)).UnreadCount);
    }

    [Fact]
    public async Task A_reply_needs_an_id_and_a_session_key()
    {
        var blank = await _asWorkerA.PostAsJsonAsync("fleet/reply", new { id = " ", text = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        var asked = await Ask(_asManager, _workerA, "owner cannot answer this");
        var owner = await Reply(_owner, S(asked, "correlationId"), "the owner typing");
        Assert.Equal(HttpStatusCode.Forbidden, owner.StatusCode);
        Assert.Equal("a reply is sent by a session; call this with that session's own key", S(await Body(owner), "error"));
    }

    [Fact]
    public async Task The_heartbeat_tells_the_asker_once_when_the_deadline_passes_and_a_late_reply_still_lands()
    {
        var asked = await Ask(_asManager, _workerA, "did the migration run?", replyByMinutes: 1);
        var messageId = S(asked, "messageId");
        // The host's clock is the real one; the deadline is moved into the past rather than waiting a minute.
        using (var ctx = _gateway.GatewayDatabaseForTests.CreateContext(TenantId.Local))
        {
            var row = ctx.FleetMessages.Single(m => m.MessageId == messageId);
            row.ReplyByUtc = DateTime.UtcNow.AddMinutes(-1);
            ctx.SaveChanges();
        }

        await _gateway.FleetDoorbell.SweepAsync();
        await _gateway.FleetDoorbell.SweepAsync();

        var inbox = await Body(await _asManager.GetAsync("fleet/inbox"));
        var unread = inbox.GetProperty("unread");
        Assert.Equal(1, unread.GetArrayLength());
        var notice = unread[0];
        Assert.Equal("system", S(notice, "kind"));
        Assert.Equal("no-reply", S(notice, "notice"));
        Assert.Equal(JsonValueKind.Null, notice.GetProperty("fromSessionId").ValueKind);
        Assert.StartsWith($"No reply to your message {messageId} (correlation {S(asked, "correlationId")}) from Mission - Worker - A",
            S(notice, "text"));
        Assert.Equal("did the migration run?", S(notice.GetProperty("inReplyTo"), "text"));
        // The roster says the manager is working, so nothing was typed.
        Assert.Empty(VerbsSent());

        var late = await Reply(_asWorkerA, S(asked, "correlationId"), "yes, a while ago");
        Assert.Equal(HttpStatusCode.OK, late.StatusCode);
        await _gateway.FleetDoorbell.SweepAsync();

        var after = (await Inbox(_asManager)).Unread;
        var reply = Assert.Single(after);
        Assert.Equal("reply", reply.Kind);
        Assert.True(reply.InReplyTo!.Late);
    }
}
