using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// MESSAGE LINKS ON A REAL HOSTED HOST (issue #3548). Every request goes over HTTP through the real
/// <c>AuthMiddleware</c> with real minted keys, because the trap on this surface is a route that is mapped and tested
/// but never added to <c>SessionKeyGuard</c> - it answers 403 in production while every direct handler test is green.
///
/// The first test is the BDO case end to end: the owner sets up "one message and a reply" between two unrelated
/// sessions, the sender is told in its inbox, the message goes, the answer comes back, the second message is refused,
/// and all of it is in the governance record.
/// </summary>
public sealed class FleetMessageLinkHostTests : IAsyncLifetime
{
    private const string SharedToken = "message-link-host-token";
    private const string DirectorId = "director-links-a";
    private const string DirectorIdB = "director-links-b";
    private const string GuardCode = "session_key_out_of_scope";

    private readonly ITestOutputHelper _out;

    private GatewayHost _gateway = null!;
    private TenantId _tenantA;
    private TenantId _tenantB;
    private HttpClient _ownerA = null!;
    private HttpClient _ownerB = null!;
    private HttpClient _directorA = null!;
    private HttpClient _investigator = null!;
    private HttpClient _coordinator = null!;
    private HttpClient _fleetManager = null!;

    private readonly string _investigatorId = Guid.NewGuid().ToString();
    private readonly string _coordinatorId = Guid.NewGuid().ToString();
    private readonly string _fleetManagerId = Guid.NewGuid().ToString();
    private readonly string _thirdId = Guid.NewGuid().ToString();
    private readonly string _sessionInB = Guid.NewGuid().ToString();

    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-message-link-host-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;
    private long _pushSequence;

    public FleetMessageLinkHostTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        var subjectA = $"sub-links-a-{_runId}";
        var subjectB = $"sub-links-b-{_runId}";
        var a = HostedTestEnrollment.Enroll(_gateway, subjectA, $"links-a-{_runId}@example.com", $"dev-links-dir-{_runId}", "MLA");
        var b = HostedTestEnrollment.Enroll(_gateway, subjectB, $"links-b-{_runId}@example.com", $"dev-links-dir-b-{_runId}", "MLB");
        _tenantA = a.Tenant;
        _tenantB = b.Tenant;
        Assert.True(_gateway.TenantBoundary.IsHosted, "The harness must be running the HOSTED tenant boundary.");

        var ownerA = _gateway.Devices.RegisterForTenant(_tenantA, subjectA, $"dev-links-owner-{_runId}", "OWNER-A", deviceType: "phone");
        var ownerB = _gateway.Devices.RegisterForTenant(_tenantB, subjectB, $"dev-links-owner-b-{_runId}", "OWNER-B", deviceType: "browser");
        _ownerA = Client(ownerA.DeviceKey);
        _ownerB = Client(ownerB.DeviceKey);
        _directorA = Client(a.DeviceKey);

        _investigator = Client(SessionKey(_investigatorId));
        _coordinator = Client(SessionKey(_coordinatorId));
        _fleetManager = Client(SessionKey(_fleetManagerId));

        var now = DateTime.UtcNow;
        Push(_tenantA, DirectorId,
            Session(_investigatorId, "BDO Argentina bug", now.AddHours(-3)),
            Session(_coordinatorId, "Cube Coordinator", now.AddHours(-2)),
            Session(_fleetManagerId, "Fleet Manager", now.AddHours(-1)),
            Session(_thirdId, "A third session", now.AddMinutes(-30)));
        Push(_tenantB, DirectorIdB, Session(_sessionInB, "Another account's session", now));
    }

    public async Task DisposeAsync()
    {
        foreach (var http in new[] { _ownerA, _ownerB, _directorA, _investigator, _coordinator, _fleetManager })
            http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    // ---- plumbing ----------------------------------------------------------------------------------------

    private static SessionDto Session(string id, string name, DateTime created) => new()
    {
        SessionId = id,
        Name = name,
        ActivityState = "WaitingForInput",
        CreatedAt = created,
        LastActivityAt = DateTime.UtcNow,
    };

    private HttpClient Client(string bearer)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    private string SessionKey(string sessionId)
    {
        var key = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(_tenantA, DirectorId, sessionId,
            GatewaySessionKey.Hash(key), DateTime.UtcNow.AddHours(1)));
        return key;
    }

    private void Push(TenantId tenant, string directorId, params SessionDto[] sessions)
    {
        _gateway.Registry.RegisterFromStream(directorId, "MACHINE-" + directorId, "someone", "1.0", pid: 4321,
            startedAt: DateTime.UtcNow, tenant: tenant);
        _gateway.PushedSessions.RegisterConnection(tenant, directorId, "conn-" + directorId);
        Assert.True(_gateway.PushedSessions.ApplySnapshot(tenant, directorId, "conn-" + directorId, ++_pushSequence,
            sessions.ToList()));
    }

    private async Task<(HttpStatusCode Status, string Body)> Send(HttpClient http, string verb, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(verb), path);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(request);
        var text = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"{verb} {path} -> {(int)resp.StatusCode} {resp.StatusCode}");
        _out.WriteLine("    " + (text.Length > 600 ? text[..600] + " ..." : text));
        return (resp.StatusCode, text);
    }

    private static JsonElement Root(string body) => JsonDocument.Parse(body).RootElement.Clone();

    private static string CodeOf(string body)
        => Root(body).TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString()! : "";

    private Task<(HttpStatusCode Status, string Body)> SetUp(HttpClient caller, string from, string to, string amount)
        => Send(caller, "POST", "fleet/links", new { senderSessionId = from, recipientSessionId = to, amount });

    private Task<(HttpStatusCode Status, string Body)> Message(HttpClient from, string to, string text, bool replyWanted = false)
        => Send(from, "POST", $"sessions/{to}/message", new { text, replyWanted });

    private async Task<List<JsonElement>> Inbox(HttpClient session)
    {
        var (status, body) = await Send(session, "GET", "fleet/inbox");
        Assert.Equal(HttpStatusCode.OK, status);
        return Root(body).GetProperty("unread").EnumerateArray().ToList();
    }

    private async Task<List<JsonElement>> Records(string eventType, string sessionId)
    {
        var (status, body) = await Send(_ownerA, "GET",
            $"gateway/governance/audit-events?category={GovernanceAuditCategory.Permission}&eventType={eventType}&sessionId={sessionId}");
        Assert.Equal(HttpStatusCode.OK, status);
        return Root(body).GetProperty("events").EnumerateArray().ToList();
    }

    // ---- the BDO case ------------------------------------------------------------------------------------

    [Fact]
    public async Task The_BDO_case_end_to_end_over_the_real_pipeline()
    {
        // Before any link, the two are refused, as today.
        var before = await Message(_investigator, _coordinatorId, "did the cube refresh run?");
        Assert.Equal(HttpStatusCode.Forbidden, before.Status);

        var (status, body) = await SetUp(_ownerA, _investigatorId, _coordinatorId, "once-with-reply");
        Assert.Equal(HttpStatusCode.Created, status);
        var link = Root(body).GetProperty("link");
        var linkId = link.GetProperty("linkId").GetString()!;
        Assert.Equal("live", link.GetProperty("status").GetString());
        Assert.Equal("One message and a reply. Live.", link.GetProperty("summary").GetString());
        Assert.StartsWith("device phone", link.GetProperty("setUpBy").GetString());

        // The sender is told, in its inbox, what it may now send.
        var notice = Assert.Single(await Inbox(_investigator));
        Assert.Contains("ONE message, and it may reply once", notice.GetProperty("text").GetString());

        var sent = await Message(_investigator, _coordinatorId, "did the cube refresh run?", replyWanted: true);
        Assert.Equal(HttpStatusCode.OK, sent.Status);
        Assert.Equal("queued", Root(sent.Body).GetProperty("status").GetString());
        var correlationId = Root(sent.Body).GetProperty("correlationId").GetString()!;

        var question = Assert.Single(await Inbox(_coordinator));
        Assert.Equal("did the cube refresh run?", question.GetProperty("text").GetString());

        var reply = await Send(_coordinator, "POST", "fleet/reply", new { id = correlationId, text = "yes, at 02:14 UTC" });
        Assert.Equal(HttpStatusCode.OK, reply.Status);
        var answer = Assert.Single(await Inbox(_investigator));
        Assert.Equal("yes, at 02:14 UTC", answer.GetProperty("text").GetString());

        // The link is used up: a second message is refused.
        var second = await Message(_investigator, _coordinatorId, "one more thing");
        Assert.Equal(HttpStatusCode.Forbidden, second.Status);

        var (_, list) = await Send(_ownerA, "GET", "fleet/links");
        var row = Root(list).GetProperty("links").EnumerateArray().Single(l => l.GetProperty("linkId").GetString() == linkId);
        Assert.Equal("used", row.GetProperty("status").GetString());

        // Recorded, by query: who set it up, and the message it carried.
        Assert.Single(await Records(GovernanceAuditEventType.MessageLinkSetUp, _investigatorId));
        var carried = Assert.Single(await Records(GovernanceAuditEventType.MessageOverLink, _investigatorId));
        Assert.Contains("used the link up", carried.GetProperty("detail").GetString());
    }

    // ---- who may set a link up ---------------------------------------------------------------------------

    [Fact]
    public async Task An_ordinary_session_cannot_set_up_list_or_remove_a_link_the_guard_refuses_it()
    {
        foreach (var answer in new[]
        {
            await SetUp(_investigator, _investigatorId, _coordinatorId, "ongoing"),
            await Send(_investigator, "GET", "fleet/links"),
            await Send(_investigator, "DELETE", "fleet/links/0123456789abcdef0123456789abcdef"),
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
            Assert.Equal(GuardCode, CodeOf(answer.Body));
        }
    }

    [Fact]
    public async Task A_Directors_key_cannot_set_up_a_link()
    {
        var answer = await SetUp(_directorA, _investigatorId, _coordinatorId, "ongoing");

        Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        Assert.Equal("owner_only", CodeOf(answer.Body));
    }

    [Fact]
    public async Task The_shared_machine_token_cannot_set_up_list_or_remove_a_link()
    {
        using var machine = Client(SharedToken);
        foreach (var answer in new[]
        {
            await SetUp(machine, _investigatorId, _coordinatorId, "ongoing"),
            await Send(machine, "GET", "fleet/links"),
            await Send(machine, "DELETE", "fleet/links/0123456789abcdef0123456789abcdef"),
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        }
    }

    [Fact]
    public async Task A_link_between_a_session_and_its_own_worker_is_refused_in_words()
    {
        var worker = Guid.NewGuid().ToString();
        var row = Session(worker, "The investigator's worker", DateTime.UtcNow);
        row.ControllerSessionId = _investigatorId;
        Push(_tenantA, DirectorId,
            Session(_investigatorId, "BDO Argentina bug", DateTime.UtcNow.AddHours(-3)),
            Session(_coordinatorId, "Cube Coordinator", DateTime.UtcNow.AddHours(-2)),
            Session(_fleetManagerId, "Fleet Manager", DateTime.UtcNow.AddHours(-1)),
            Session(_thirdId, "A third session", DateTime.UtcNow.AddMinutes(-30)),
            row);

        var answer = await SetUp(_ownerA, _investigatorId, worker, "once");

        Assert.Equal(HttpStatusCode.Conflict, answer.Status);
        Assert.Equal("already_related", CodeOf(answer.Body));
    }

    [Fact]
    public async Task The_Fleet_Manager_raised_sets_up_a_link_between_two_others_and_it_is_recorded_as_its_act()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(_ownerA, "POST", $"sessions/{_fleetManagerId}/raise")).Status);

        var (status, body) = await SetUp(_fleetManager, _investigatorId, _thirdId, "ongoing");

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal($"session {_fleetManagerId}", Root(body).GetProperty("link").GetProperty("setUpBy").GetString());
        var setUp = Assert.Single(await Records(GovernanceAuditEventType.MessageLinkSetUp, _investigatorId));
        Assert.Equal($"session {_fleetManagerId}", setUp.GetProperty("actor").GetString());
        // The guard's own record of the raised action is there too.
        Assert.Contains(await Records(GovernanceAuditEventType.RaisedAction, _fleetManagerId),
            e => e.GetProperty("detail").GetString()!.Contains("message link route"));
    }

    [Fact]
    public async Task The_Fleet_Manager_never_sets_up_a_link_it_is_part_of()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(_ownerA, "POST", $"sessions/{_fleetManagerId}/raise")).Status);

        var answer = await SetUp(_fleetManager, _fleetManagerId, _coordinatorId, "ongoing");

        Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        Assert.Equal("own_link", CodeOf(answer.Body));
    }

    // ---- amounts, removal, accounts ----------------------------------------------------------------------

    [Fact]
    public async Task An_ongoing_link_carries_both_ways_and_tells_both_sessions()
    {
        Assert.Equal(HttpStatusCode.Created, (await SetUp(_ownerA, _investigatorId, _coordinatorId, "ongoing")).Status);

        Assert.Single(await Inbox(_investigator));
        Assert.Single(await Inbox(_coordinator));
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Message(_investigator, _coordinatorId, $"forward {i}")).Status);
            Assert.Equal(HttpStatusCode.OK, (await Message(_coordinator, _investigatorId, $"back {i}")).Status);
        }
    }

    [Fact]
    public async Task A_removed_link_carries_nothing_and_the_removal_is_recorded()
    {
        var (_, body) = await SetUp(_ownerA, _investigatorId, _coordinatorId, "ongoing");
        var linkId = Root(body).GetProperty("link").GetProperty("linkId").GetString()!;

        var (status, removed) = await Send(_ownerA, "DELETE", $"fleet/links/{linkId}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("removed", Root(removed).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await Message(_investigator, _coordinatorId, "still there?")).Status);
        Assert.Single(await Records(GovernanceAuditEventType.MessageLinkStopped, _investigatorId));
    }

    [Fact]
    public async Task A_one_message_link_with_no_reply_refuses_a_question()
    {
        Assert.Equal(HttpStatusCode.Created, (await SetUp(_ownerA, _investigatorId, _coordinatorId, "once")).Status);

        var answer = await Message(_investigator, _coordinatorId, "a question?", replyWanted: true);

        Assert.Equal(HttpStatusCode.Conflict, answer.Status);
        Assert.Equal(HttpStatusCode.OK, (await Message(_investigator, _coordinatorId, "a statement.")).Status);
    }

    [Fact]
    public async Task Another_accounts_owner_sees_neither_the_sessions_nor_the_links()
    {
        var (_, body) = await SetUp(_ownerA, _investigatorId, _coordinatorId, "ongoing");
        var linkId = Root(body).GetProperty("link").GetProperty("linkId").GetString()!;

        var setUp = await SetUp(_ownerB, _investigatorId, _coordinatorId, "ongoing");
        Assert.Equal(HttpStatusCode.NotFound, setUp.Status);
        Assert.Equal("session_not_found", CodeOf(setUp.Body));

        var (_, list) = await Send(_ownerB, "GET", "fleet/links");
        Assert.Empty(Root(list).GetProperty("links").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(_ownerB, "DELETE", $"fleet/links/{linkId}")).Status);
    }

    [Theory]
    [InlineData("forever", "invalid_amount")]
    [InlineData("", "invalid_amount")]
    public async Task A_link_with_an_unknown_amount_is_refused(string amount, string code)
    {
        var answer = await SetUp(_ownerA, _investigatorId, _coordinatorId, amount);

        Assert.Equal(HttpStatusCode.BadRequest, answer.Status);
        Assert.Equal(code, CodeOf(answer.Body));
    }
}
