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
/// REQUESTS FOR A MESSAGE LINK ON A REAL HOSTED HOST (issue #3548). Every request goes over HTTP through the real
/// <c>AuthMiddleware</c> and <c>SessionKeyGuard</c> with real minted keys: asking must reach the route from an ORDINARY
/// session key, and listing and answering must not.
///
/// The first test is the whole story: a session is refused, asks, the owner sees the reason, allows it, the session is
/// told in its inbox, and its message goes - with every step in the governance record.
/// </summary>
public sealed class FleetMessageLinkRequestHostTests : IAsyncLifetime
{
    private const string SharedToken = "message-link-request-host-token";
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
        Path.Combine(Path.GetTempPath(), "cc-message-link-request-host-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;
    private long _pushSequence;

    public FleetMessageLinkRequestHostTests(ITestOutputHelper output) => _out = output;

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

    private Task<(HttpStatusCode Status, string Body)> Ask(HttpClient session, string target, string reason = "I need the cube refresh status")
        => Send(session, "POST", "fleet/link-requests", new { targetSessionId = target, reason });

    private Task<(HttpStatusCode Status, string Body)> Answer(HttpClient caller, string requestId, object body)
        => Send(caller, "POST", $"fleet/link-requests/{requestId}/answer", body);

    private async Task<List<JsonElement>> Requests(HttpClient owner)
    {
        var (status, body) = await Send(owner, "GET", "fleet/link-requests");
        Assert.Equal(HttpStatusCode.OK, status);
        return Root(body).GetProperty("requests").EnumerateArray().ToList();
    }

    private static string RequestIdOf(string body) => Root(body).GetProperty("request").GetProperty("requestId").GetString()!;

    // ---- the whole story ---------------------------------------------------------------------------------

    [Fact]
    public async Task Refused_then_asked_then_allowed_then_the_message_goes()
    {
        var refused = await Message(_investigator, _coordinatorId, "did the cube refresh run?");
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Contains($"cc-devthrottle message request {_coordinatorId}", refused.Body);

        var (askStatus, askBody) = await Ask(_investigator, _coordinatorId);
        Assert.Equal(HttpStatusCode.Created, askStatus);
        var requestId = RequestIdOf(askBody);
        Assert.Equal("pending", Root(askBody).GetProperty("request").GetProperty("status").GetString());
        Assert.Contains("the answer arrives in your inbox", Root(askBody).GetProperty("note").GetString());

        var waiting = Assert.Single(await Requests(_ownerA));
        Assert.Equal(requestId, waiting.GetProperty("requestId").GetString());
        Assert.Equal(_investigatorId, waiting.GetProperty("requesterSessionId").GetString());
        Assert.Equal(_coordinatorId, waiting.GetProperty("targetSessionId").GetString());
        Assert.Equal("I need the cube refresh status", waiting.GetProperty("reason").GetString());

        var (status, body) = await Answer(_ownerA, requestId, new { amount = "once-with-reply" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("allowed", Root(body).GetProperty("request").GetProperty("status").GetString());
        var linkId = Root(body).GetProperty("link").GetProperty("linkId").GetString()!;
        Assert.Equal(linkId, Root(body).GetProperty("request").GetProperty("linkId").GetString());

        // Told in its inbox, exactly as for a link set up unasked.
        var notice = Assert.Single(await Inbox(_investigator));
        Assert.Contains("ONE message, and it may reply once", notice.GetProperty("text").GetString());

        var sent = await Message(_investigator, _coordinatorId, "did the cube refresh run?", replyWanted: true);
        Assert.Equal(HttpStatusCode.OK, sent.Status);

        Assert.Single(await Records(GovernanceAuditEventType.MessageLinkRequested, _investigatorId));
        var answered = Assert.Single(await Records(GovernanceAuditEventType.MessageLinkRequestAnswered, _investigatorId));
        Assert.Contains($"allowed, once-with-reply, as message link {linkId}", answered.GetProperty("detail").GetString());
        Assert.Single(await Records(GovernanceAuditEventType.MessageLinkSetUp, _investigatorId));
    }

    [Fact]
    public async Task A_declined_request_tells_the_session_no_and_sets_nothing_up()
    {
        var requestId = RequestIdOf((await Ask(_investigator, _coordinatorId)).Body);

        var (status, body) = await Answer(_ownerA, requestId, new { decline = true });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("declined", Root(body).GetProperty("request").GetProperty("status").GetString());

        var notice = Assert.Single(await Inbox(_investigator));
        Assert.Contains("The user said no to your request to talk to", notice.GetProperty("text").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await Message(_investigator, _coordinatorId, "still?")).Status);
        Assert.Empty(Root((await Send(_ownerA, "GET", "fleet/links")).Body).GetProperty("links").EnumerateArray());
    }

    [Fact]
    public async Task A_request_is_answered_once()
    {
        var requestId = RequestIdOf((await Ask(_investigator, _coordinatorId)).Body);
        Assert.Equal(HttpStatusCode.OK, (await Answer(_ownerA, requestId, new { amount = "once" })).Status);

        var again = await Answer(_ownerA, requestId, new { decline = true });
        Assert.Equal(HttpStatusCode.Conflict, again.Status);
        Assert.Equal("already_answered", CodeOf(again.Body));
        Assert.Single(Root((await Send(_ownerA, "GET", "fleet/links")).Body).GetProperty("links").EnumerateArray());
    }

    [Fact]
    public async Task Asking_twice_for_the_same_session_changes_nothing()
    {
        var first = await Ask(_investigator, _coordinatorId, "first");
        var second = await Ask(_investigator, _coordinatorId, "second");

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.False(Root(second.Body).GetProperty("created").GetBoolean());
        Assert.Equal("first", Assert.Single(await Requests(_ownerA)).GetProperty("reason").GetString());
        Assert.Single(await Records(GovernanceAuditEventType.MessageLinkRequested, _investigatorId));
    }

    [Fact]
    public async Task A_session_that_may_already_message_is_told_to_send_instead()
    {
        Assert.Equal(HttpStatusCode.Created, (await SetUp(_ownerA, _investigatorId, _coordinatorId, "ongoing")).Status);

        var asked = await Ask(_investigator, _coordinatorId);
        Assert.Equal(HttpStatusCode.Conflict, asked.Status);
        Assert.Equal("already_linked", CodeOf(asked.Body));
        // Over an ongoing link the other side may message back too, so it is told the same.
        Assert.Equal("already_linked", CodeOf((await Ask(_coordinator, _investigatorId)).Body));
    }

    [Fact]
    public async Task An_ordinary_session_may_not_list_or_answer_requests()
    {
        var requestId = RequestIdOf((await Ask(_investigator, _coordinatorId)).Body);

        foreach (var answer in new[]
        {
            await Send(_coordinator, "GET", "fleet/link-requests"),
            await Answer(_coordinator, requestId, new { amount = "ongoing" }),
            // Not even the asker allows its own request.
            await Answer(_investigator, requestId, new { amount = "ongoing" }),
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
            Assert.Equal(GuardCode, CodeOf(answer.Body));
        }
        Assert.Equal("pending", Assert.Single(await Requests(_ownerA)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_Fleet_Manager_raised_answers_for_the_owner()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(_ownerA, "POST", $"sessions/{_fleetManagerId}/raise")).Status);
        var requestId = RequestIdOf((await Ask(_investigator, _coordinatorId)).Body);

        Assert.Single(await Requests(_fleetManager));
        var (status, body) = await Answer(_fleetManager, requestId, new { amount = "once" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal($"session {_fleetManagerId}", Root(body).GetProperty("request").GetProperty("answeredBy").GetString());
    }

    [Fact]
    public async Task Only_a_session_asks()
    {
        var asked = await Ask(_ownerA, _coordinatorId);
        Assert.Equal(HttpStatusCode.Forbidden, asked.Status);
        Assert.Equal("session_only", CodeOf(asked.Body));
    }

    [Fact]
    public async Task A_request_without_a_reason_or_to_itself_is_refused_in_words()
    {
        var noReason = await Ask(_investigator, _coordinatorId, "  ");
        Assert.Equal(HttpStatusCode.BadRequest, noReason.Status);
        Assert.Equal("reason_required", CodeOf(noReason.Body));

        Assert.Equal("same_session", CodeOf((await Ask(_investigator, _investigatorId)).Body));
    }

    [Fact]
    public async Task Another_account_sees_none_of_the_requests()
    {
        await Ask(_investigator, _coordinatorId);
        Assert.Empty(await Requests(_ownerB));

        // A session of another account cannot be asked for: it is not in this account.
        Assert.Equal(HttpStatusCode.NotFound, (await Ask(_investigator, _sessionInB)).Status);
    }
}
