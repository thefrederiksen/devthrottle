using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Util;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A SESSION TYPES INTO A SESSION IT OWNS, AND INTO NO OTHER - on a real HOSTED host (Parent Control, fix 1). Every
/// request goes over HTTP through the real <c>AuthMiddleware</c> and <c>SessionKeyGuard</c> with real minted session
/// keys, across two real accounts, because the rule is split between the guard (which lets the prompt shape through)
/// and the route (which decides ownership) and only the pair, run together, says what an agent can actually do.
///
/// HOW A PASS IS READ. No Director is connected to the tunnel, so a send that clears every check is answered 502 by the
/// tunnel - "the Director is not connected" - which is a specific presence: the request got past the guard AND the
/// ownership rule and was on its way to the Director. A refusal is a specific presence too: 403 with the rule's own
/// sentence, or the guard's code. That what the Director then receives is the guarded send, and that the owner's
/// unsent words stop it, is proven over a real tunnel in <c>PromptAttributionIsGatewayAuthoritativeTests</c>.
/// </summary>
public sealed class OwnedSessionInputHostTests : IAsyncLifetime
{
    private const string SharedToken = "owned-session-input-host-token";
    private const string DirectorId = "director-owned-a";
    private const string DirectorIdB = "director-owned-b";
    private const string GuardCode = "session_key_out_of_scope";
    private const string NotLocated = "session_not_found";

    private readonly ITestOutputHelper _out;

    private GatewayHost _gateway = null!;
    private TenantId _tenantA;
    private TenantId _tenantB;
    private HttpClient _ownerA = null!;
    private HttpClient _parent = null!;
    private HttpClient _stranger = null!;

    private readonly string _parentId = Guid.NewGuid().ToString();
    private readonly string _childId = Guid.NewGuid().ToString();
    private readonly string _grandchildId = Guid.NewGuid().ToString();
    private readonly string _ownersOwnId = Guid.NewGuid().ToString();
    private readonly string _strangerId = Guid.NewGuid().ToString();
    private readonly string _strangersChildId = Guid.NewGuid().ToString();
    private readonly string _sessionInB = Guid.NewGuid().ToString();

    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-owned-input-host-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;
    private long _pushSequence;

    public OwnedSessionInputHostTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        var subjectA = $"sub-owned-a-{_runId}";
        var subjectB = $"sub-owned-b-{_runId}";
        var a = HostedTestEnrollment.Enroll(_gateway, subjectA, $"owned-a-{_runId}@example.com", $"dev-owned-dir-{_runId}", "MOA");
        var b = HostedTestEnrollment.Enroll(_gateway, subjectB, $"owned-b-{_runId}@example.com", $"dev-owned-dir-b-{_runId}", "MOB");
        _tenantA = a.Tenant;
        _tenantB = b.Tenant;
        Assert.True(_gateway.TenantBoundary.IsHosted, "The harness must be running the HOSTED tenant boundary.");
        Assert.NotEqual(_tenantA.Value, _tenantB.Value);

        var ownerA = _gateway.Devices.RegisterForTenant(_tenantA, subjectA, $"dev-owned-owner-{_runId}", "OWNER-A", deviceType: "browser");
        _ownerA = Client(ownerA.DeviceKey);
        _parent = Client(SessionKey(_parentId));
        _stranger = Client(SessionKey(_strangerId));

        Push(_tenantA, DirectorId,
            Session(_parentId, owner: null),
            Session(_childId, owner: _parentId),
            Session(_grandchildId, owner: _childId),
            Session(_ownersOwnId, owner: null),
            Session(_strangerId, owner: null),
            Session(_strangersChildId, owner: _strangerId));
        // Another account's session, which names the parent as its owner: an id is not an account, and a session of
        // another account is never found at all, whatever its row says.
        Push(_tenantB, DirectorIdB, Session(_sessionInB, owner: _parentId));
    }

    public async Task DisposeAsync()
    {
        foreach (var http in new[] { _ownerA, _parent, _stranger })
            http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    // ---- plumbing ----------------------------------------------------------------------------------------

    private static SessionDto Session(string id, string? owner) => new()
    {
        SessionId = id,
        Name = "session " + id[..8],
        ActivityState = "WaitingForInput",
        IsControlled = owner is not null,
        ControllerSessionId = owner,
        CreatedAt = DateTime.UtcNow.AddHours(-1),
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

    /// <summary>Stand in for the Hello of a Director that checks the session is waiting before it types.</summary>
    private void DirectorChecksBeforeTyping(bool checks)
        => _gateway.TurnPushCapabilities.Record(_tenantA, DirectorId, pushesTurns: true, checksIdleBeforeTyping: checks);

    private async Task<(HttpStatusCode Status, string Body)> Send(HttpClient http, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        using var resp = await http.SendAsync(request);
        var text = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"POST {path} -> {(int)resp.StatusCode} {resp.StatusCode}");
        _out.WriteLine("    " + (text.Length > 600 ? text[..600] + " ..." : text));
        return (resp.StatusCode, text);
    }

    private static string Field(string body, string name)
    {
        if (string.IsNullOrWhiteSpace(body) || body.TrimStart()[0] != '{') return "";
        var root = JsonDocument.Parse(body).RootElement;
        return root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    }

    private static void AssertReachedTheTunnel((HttpStatusCode Status, string Body) answer)
    {
        // Past the guard and the ownership rule: the only thing left to say no was the Director's absence.
        Assert.Equal(HttpStatusCode.BadGateway, answer.Status);
        Assert.Contains("not connected", answer.Body);
    }

    // ---- prompt --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Prompt_SessionKeyOnItsOwnChild_ReachesTheDirector()
    {
        DirectorChecksBeforeTyping(true);

        AssertReachedTheTunnel(await Send(_parent, $"sessions/{_childId}/prompt", new { text = "carry on", appendEnter = true }));
    }

    [Fact]
    public async Task Prompt_SessionKeyOnASessionTheOwnerRunsDirectly_IsRefusedAsNotYours()
    {
        DirectorChecksBeforeTyping(true);

        var answer = await Send(_parent, $"sessions/{_ownersOwnId}/prompt", new { text = "carry on" });

        Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        Assert.Equal(AgentInputRefusal.NotYourSession, Field(answer.Body, "error"));
    }

    [Fact]
    public async Task Prompt_SessionKeyOnAnotherSessionsChild_IsRefusedAsNotYours()
    {
        DirectorChecksBeforeTyping(true);

        var answer = await Send(_parent, $"sessions/{_strangersChildId}/prompt", new { text = "carry on" });

        Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        Assert.Equal(AgentInputRefusal.NotYourSession, Field(answer.Body, "error"));
    }

    [Fact]
    public async Task Prompt_SessionKeyOnItsGrandchild_IsRefusedAsNotYours()
    {
        DirectorChecksBeforeTyping(true);

        var answer = await Send(_parent, $"sessions/{_grandchildId}/prompt", new { text = "carry on" });

        Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        Assert.Equal(AgentInputRefusal.NotYourSession, Field(answer.Body, "error"));
    }

    [Fact]
    public async Task Prompt_SessionKeyOnAnotherAccountsSessionThatNamesItAsOwner_IsNotFound()
    {
        DirectorChecksBeforeTyping(true);

        var answer = await Send(_parent, $"sessions/{_sessionInB}/prompt", new { text = "carry on" });

        Assert.Equal(HttpStatusCode.NotFound, answer.Status);
        Assert.Equal(NotLocated, Field(answer.Body, "code"));
    }

    [Fact]
    public async Task Prompt_OwnChildOnADirectorThatDoesNotCheckFirst_IsRefusedAndNothingIsSent()
    {
        DirectorChecksBeforeTyping(false);

        var answer = await Send(_parent, $"sessions/{_childId}/prompt", new { text = "carry on" });

        Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        Assert.Equal(AgentInputRefusal.DirectorTooOld, Field(answer.Body, "error"));
    }

    [Fact]
    public async Task Prompt_OwnChildAskingToLeaveTheTextUnsent_IsRefused()
    {
        DirectorChecksBeforeTyping(true);

        var answer = await Send(_parent, $"sessions/{_childId}/prompt", new { text = "carry on", appendEnter = false });

        Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        Assert.Equal(AgentInputRefusal.NoSubmit, Field(answer.Body, "error"));
    }

    [Fact]
    public async Task Prompt_TheOwnersOwnDevice_IsNotHeldToTheOwnershipRule()
    {
        // The owner's device types into any session of his account, owned by a session or not, on any Director -
        // exactly as before. It never reaches the ownership rule.
        DirectorChecksBeforeTyping(false);

        AssertReachedTheTunnel(await Send(_ownerA, $"sessions/{_childId}/prompt", new { text = "from the owner" }));
        AssertReachedTheTunnel(await Send(_ownerA, $"sessions/{_ownersOwnId}/prompt", new { text = "from the owner" }));
    }

    // ---- interrupt and escape stay the owner's ------------------------------------------------------------

    [Theory]
    [InlineData("interrupt")]
    [InlineData("escape")]
    public async Task InterruptAndEscape_SessionKeyOnItsOwnChild_AreStillRefusedByTheGuard(string verb)
    {
        DirectorChecksBeforeTyping(true);

        var answer = await Send(_parent, $"sessions/{_childId}/{verb}", new { });

        Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        Assert.Equal(GuardCode, Field(answer.Body, "code"));
    }

    // ---- compact and continue -----------------------------------------------------------------------------

    [Fact]
    public async Task CompactContinue_SessionKeyOnItsOwnChild_ReachesTheDirector()
    {
        DirectorChecksBeforeTyping(true);

        var answer = await Send(_parent, $"sessions/{_childId}/compact-context", new { continuePrompt = "continue" });

        Assert.Equal(HttpStatusCode.BadGateway, answer.Status);
    }

    [Fact]
    public async Task CompactContinue_SessionKeyOnAnotherSessionsChild_IsRefusedBeforeAnythingIsCompacted()
    {
        DirectorChecksBeforeTyping(true);

        var answer = await Send(_parent, $"sessions/{_strangersChildId}/compact-context", new { continuePrompt = "continue" });

        Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        Assert.Equal(AgentInputRefusal.CompactContinue, Field(answer.Body, "error"));
    }

    [Fact]
    public async Task CompactContinue_OwnChildOnADirectorThatDoesNotCheckFirst_IsRefused()
    {
        DirectorChecksBeforeTyping(false);

        var answer = await Send(_parent, $"sessions/{_childId}/compact-context", new { continuePrompt = "continue" });

        Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        Assert.Equal(AgentInputRefusal.DirectorTooOld, Field(answer.Body, "error"));
    }

    [Fact]
    public async Task PlainCompact_SessionKeyOnAnySessionOfTheAccount_IsUnchanged()
    {
        // A compaction with no follow-up types nothing afterwards and stays open to every session key.
        DirectorChecksBeforeTyping(false);

        var answer = await Send(_stranger, $"sessions/{_childId}/compact-context", new { });

        Assert.Equal(HttpStatusCode.BadGateway, answer.Status);
    }
}
