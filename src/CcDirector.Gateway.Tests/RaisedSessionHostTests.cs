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
/// RAISED SESSIONS ON A REAL HOSTED HOST (the Fleet Manager Improvement mission, phase 1, issue #3177). Every request
/// here goes over HTTP through the real <c>AuthMiddleware</c> with a real minted session key, because the known trap
/// on this surface is a route that is mapped and tested but never added to <c>SessionKeyGuard</c>: the verbs answer 403
/// in production while every test that calls the handler directly stays green.
///
/// HOW A PASS IS READ. No Director is connected to the tunnel, so a request that clears the guard is answered by the
/// ROUTE - and the route's answer to the raised key is compared with its answer to the owner's own device for the
/// same request. "Passed the guard" is therefore a specific presence: the owner's own status code, from the route.
/// "Refused" is a specific presence too: 403 with the code <c>session_key_out_of_scope</c> from the guard, or the
/// route's own named refusal. Each grant is asked of an UNRAISED key in the same test, so a row proves the widening
/// and not merely that the route is open.
/// </summary>
public sealed class RaisedSessionHostTests : IAsyncLifetime
{
    private const string SharedToken = "raised-session-host-token";
    private const string DirectorId = "director-raised-a";
    private const string DirectorIdB = "director-raised-b";
    private const string GuardCode = "session_key_out_of_scope";

    private readonly ITestOutputHelper _out;

    private GatewayHost _gateway = null!;
    private TenantId _tenantA;
    private TenantId _tenantB;
    private HttpClient _ownerA = null!;
    private HttpClient _ownerB = null!;
    private HttpClient _directorA = null!;
    private HttpClient _raised = null!;
    private HttpClient _unraised = null!;
    private HttpClient _worker = null!;
    private string _directorKeyA = "";

    private readonly string _raisedId = Guid.NewGuid().ToString();
    private readonly string _unraisedId = Guid.NewGuid().ToString();
    private readonly string _workerId = Guid.NewGuid().ToString();
    private readonly string _strangerId = Guid.NewGuid().ToString();
    private readonly string _sessionInB = Guid.NewGuid().ToString();

    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-raised-session-host-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;
    private long _pushSequence;

    public RaisedSessionHostTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        var subjectA = $"sub-raised-a-{_runId}";
        var subjectB = $"sub-raised-b-{_runId}";
        var a = HostedTestEnrollment.Enroll(_gateway, subjectA, $"raised-a-{_runId}@example.com", $"dev-raised-dir-{_runId}", "MRA");
        var b = HostedTestEnrollment.Enroll(_gateway, subjectB, $"raised-b-{_runId}@example.com", $"dev-raised-dir-b-{_runId}", "MRB");
        _tenantA = a.Tenant;
        _tenantB = b.Tenant;
        Assert.True(_gateway.TenantBoundary.IsHosted, "The harness must be running the HOSTED tenant boundary.");
        Assert.NotEqual(_tenantA.Value, _tenantB.Value);

        // The enrolment helper registers a Director's key. The owner's signed-in browser and phone are further
        // devices of the same accounts, with the device types a sign-in stamps.
        var ownerA = _gateway.Devices.RegisterForTenant(_tenantA, subjectA, $"dev-raised-owner-{_runId}", "OWNER-A", deviceType: "browser");
        var ownerB = _gateway.Devices.RegisterForTenant(_tenantB, subjectB, $"dev-raised-owner-b-{_runId}", "OWNER-B", deviceType: "phone");
        _ownerA = Client(ownerA.DeviceKey);
        _ownerB = Client(ownerB.DeviceKey);
        _directorKeyA = a.DeviceKey;
        _directorA = Client(a.DeviceKey);

        _raised = Client(SessionKey(_raisedId));
        _unraised = Client(SessionKey(_unraisedId));
        _worker = Client(SessionKey(_workerId));

        var now = DateTime.UtcNow;
        Push(_tenantA, DirectorId,
            Session(_raisedId, "The session the owner raises", now.AddHours(-3)),
            Session(_unraisedId, "A session nobody raised", now.AddHours(-2)),
            Session(_workerId, "Another session of the account", now.AddHours(-1)),
            Session(_strangerId, "A session related to nobody", now.AddMinutes(-30)));
        Push(_tenantB, DirectorIdB, Session(_sessionInB, "Another account's session", now));

        // The judged-stop answer route keeps a second wall of its own: while an account's verdict colours are off,
        // no session key may answer one. That wall is not what these tests are about, so the colours are on.
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantA, true, DateTime.UtcNow);
    }

    public async Task DisposeAsync()
    {
        foreach (var http in new[] { _ownerA, _ownerB, _directorA, _raised, _unraised, _worker })
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

    /// <summary>The <c>code</c> a route put on its answer, or "" when the answer carries none.</summary>
    private static string CodeOf(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.TrimStart()[0] != '{') return "";
        return Root(body).TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString()! : "";
    }

    private async Task Raise(string sessionId)
    {
        var (status, body) = await Send(_ownerA, "POST", $"sessions/{sessionId}/raise");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(Root(body).GetProperty("raise").GetProperty("raised").GetBoolean());
    }

    private static void AssertRefusedByTheGuard((HttpStatusCode Status, string Body) answer)
    {
        Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        Assert.Equal(GuardCode, Root(answer.Body).GetProperty("code").GetString());
    }

    /// <summary>The record, read the way the owner reads it: by query, from his own device.</summary>
    private async Task<List<JsonElement>> Records(string eventType, string sessionId)
    {
        var (status, body) = await Send(_ownerA, "GET",
            $"gateway/governance/audit-events?category={GovernanceAuditCategory.Permission}&eventType={eventType}&sessionId={sessionId}");
        Assert.Equal(HttpStatusCode.OK, status);
        return Root(body).GetProperty("events").EnumerateArray().ToList();
    }

    /// <summary>A live judged stop on <paramref name="sessionId"/>, stored fresh so no earlier attempt has touched it.</summary>
    private string FreshVerdict(string sessionId)
    {
        var verdictId = "tv-raised-" + Guid.NewGuid().ToString("N")[..8];
        _gateway.TurnVerdicts.Store(_tenantA, sessionId, new TurnVerdictDto
        {
            VerdictId = verdictId,
            JudgedAtUtc = DateTime.UtcNow,
            TurnEndObservedAtUtc = DateTime.UtcNow.AddSeconds(-12),
            ScreenHash = "screen-hash-raised",
            Model = "devthrottle/wingman-fast",
            ContractVersion = "v2",
            PackageKind = "agent-reply",
            Verdict = Core.Wingman.TurnVerdictVocabulary.NeededYou,
            Confidence = "high",
            Evidence = "Apply the migration now?",
            Label = "Apply the migration now?",
            Summary = "The session is asking whether to apply the migration.",
            AnswerVia = "keys",
            Menu = new TurnVerdictMenuDto { Question = "Apply the migration now?", SelectionMode = "single", Submit = "" },
            Options = new List<TurnVerdictOptionDto>
            {
                new() { Key = "Yes", Send = "1", Recommended = true, Note = "Applies it." },
                new() { Key = "No", Send = "2", Recommended = false, Note = "Leaves it." },
            },
            Risk = "none",
            Spoken = "The session asks whether to apply the migration.",
        });
        return verdictId;
    }

    /// <summary>The five ways of typing into a session, each as (verb, path, body). Building the list stores a fresh
    /// judged stop on the target, so each caller answers one nobody has touched.</summary>
    private (string Name, string Verb, string Path, object? Body)[] AgentInput(string target) => new (string, string, string, object?)[]
    {
        ("prompt", "POST", $"sessions/{target}/prompt", new { text = "carry on" }),
        ("interrupt", "POST", $"sessions/{target}/interrupt", null),
        ("escape", "POST", $"sessions/{target}/escape", null),
        ("fan-out", "POST", "fanout", new { sessionIds = new[] { target }, text = "carry on", waitForIdle = false }),
        ("answering a judged stop", "POST", $"sessions/{target}/turn-verdict/answer", new { verdictId = FreshVerdict(target), optionIndexes = new[] { 0 } }),
    };

    /// <summary>The Fleet Manager routes that are otherwise the owner's alone and that a raised session is granted.</summary>
    private static readonly (string Verb, string Path)[] OwnerOnlyFleetManagerRoutes =
    {
        ("GET", "gateway/fleet-manager/placement"),
        ("GET", "gateway/fleet-manager/page"),
        ("GET", "gateway/fleet-manager/walkthrough"),
        ("POST", "gateway/fleet-manager/restart"),
        ("POST", "gateway/fleet-manager/move"),
    };

    // ---- raise and lower ---------------------------------------------------------------------------------

    [Fact]
    public async Task Raise_FromTheOwnersDevice_RaisesTheSession_StampsTheRoster_AndIsRecorded()
    {
        var (status, body) = await Send(_ownerA, "POST", $"sessions/{_raisedId}/raise");

        Assert.Equal(HttpStatusCode.OK, status);
        var raise = Root(body).GetProperty("raise");
        Assert.True(raise.GetProperty("raised").GetBoolean());
        Assert.Equal(SessionRaiseDto.OfferLower, raise.GetProperty("offer").GetString());
        Assert.True(_gateway.RaisedSessions.IsRaised(_tenantA, _raisedId));

        // The roster the clients read carries the finished values for both rows (critical rule 7).
        var (_, roster) = await Send(_ownerA, "GET", "sessions");
        var rows = Root(roster).EnumerateArray().ToList();
        var raisedRow = rows.Single(r => r.GetProperty("sessionId").GetString() == _raisedId).GetProperty("raise");
        var otherRow = rows.Single(r => r.GetProperty("sessionId").GetString() == _unraisedId).GetProperty("raise");
        Assert.True(raisedRow.GetProperty("raised").GetBoolean());
        Assert.Equal("Raised", raisedRow.GetProperty("mark").GetString());
        Assert.Equal(SessionRaiseDto.OfferLower, raisedRow.GetProperty("offer").GetString());
        Assert.False(otherRow.GetProperty("raised").GetBoolean());
        Assert.Equal(SessionRaiseDto.OfferRaise, otherRow.GetProperty("offer").GetString());

        var record = Assert.Single(await Records(GovernanceAuditEventType.SessionRaised, _raisedId));
        Assert.Equal(_raisedId, record.GetProperty("sessionId").GetString());
        Assert.Contains("browser", record.GetProperty("actor").GetString());
    }

    [Fact]
    public async Task Lower_FromTheOwnersDevice_LowersTheSession_TakesTheGrantAway_AndIsRecorded()
    {
        await Raise(_raisedId);
        var before = await Send(_raised, "POST", $"sessions/{_workerId}/interrupt");
        Assert.NotEqual(HttpStatusCode.Forbidden, before.Status);

        var (status, body) = await Send(_ownerA, "POST", $"sessions/{_raisedId}/lower");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(Root(body).GetProperty("raise").GetProperty("raised").GetBoolean());
        Assert.False(_gateway.RaisedSessions.IsRaised(_tenantA, _raisedId));
        AssertRefusedByTheGuard(await Send(_raised, "POST", $"sessions/{_workerId}/interrupt"));

        var record = Assert.Single(await Records(GovernanceAuditEventType.SessionLowered, _raisedId));
        Assert.Equal(_raisedId, record.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task RaiseAndLower_FromAnySessionKey_RaisedOrNot_AreRefusedByTheGuard_AndChangeNothing()
    {
        await Raise(_raisedId);

        // A raised session may not raise another, may not raise or lower itself, and may not lower another.
        AssertRefusedByTheGuard(await Send(_raised, "POST", $"sessions/{_workerId}/raise"));
        AssertRefusedByTheGuard(await Send(_raised, "POST", $"sessions/{_raisedId}/raise"));
        AssertRefusedByTheGuard(await Send(_raised, "POST", $"sessions/{_raisedId}/lower"));
        // An unraised one may do none of it either.
        AssertRefusedByTheGuard(await Send(_unraised, "POST", $"sessions/{_unraisedId}/raise"));
        AssertRefusedByTheGuard(await Send(_unraised, "POST", $"sessions/{_raisedId}/lower"));

        Assert.True(_gateway.RaisedSessions.IsRaised(_tenantA, _raisedId));
        Assert.False(_gateway.RaisedSessions.IsRaised(_tenantA, _workerId));
        Assert.False(_gateway.RaisedSessions.IsRaised(_tenantA, _unraisedId));
        Assert.Equal(new[] { _raisedId }, _gateway.RaisedSessions.List(_tenantA).Select(r => r.SessionId));
    }

    [Fact]
    public async Task Raise_FromADirectorsKey_IsRefusedByTheRoute_AsTheOwnersAlone()
    {
        var (status, body) = await Send(_directorA, "POST", $"sessions/{_raisedId}/raise");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("owner_only", Root(body).GetProperty("code").GetString());
        Assert.False(_gateway.RaisedSessions.IsRaised(_tenantA, _raisedId));
    }

    [Fact]
    public async Task Raise_AnotherAccountsSession_AnswersAsAnUnknownOne_AndRaisesNothing()
    {
        var foreign = await Send(_ownerB, "POST", $"sessions/{_raisedId}/raise");
        var unknownId = Guid.NewGuid().ToString();
        var unknown = await Send(_ownerB, "POST", $"sessions/{unknownId}/raise");

        Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
        Assert.Equal("session_not_found", Root(foreign.Body).GetProperty("code").GetString());
        Assert.Equal(unknown.Body.Replace(unknownId, "SID"), foreign.Body.Replace(_raisedId, "SID"));
        Assert.False(_gateway.RaisedSessions.IsRaised(_tenantA, _raisedId));
        Assert.False(_gateway.RaisedSessions.IsRaised(_tenantB, _raisedId));
    }

    // ---- the guard ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AgentInput_WithARaisedKey_ReachesTheRouteAsTheOwnersDeviceDoes_AndAnUnraisedKeyIsRefusedAsToday()
    {
        await Raise(_raisedId);

        var count = AgentInput(_workerId).Length;
        for (var i = 0; i < count; i++)
        {
            // The list is built once per caller, so each answers a judged stop nobody has touched.
            var (name, verb, path, _) = AgentInput(_workerId)[i];
            var owner = await Send(_ownerA, verb, path, AgentInput(_workerId)[i].Body);
            var raised = await Send(_raised, verb, path, AgentInput(_workerId)[i].Body);
            var unraised = await Send(_unraised, verb, path, AgentInput(_workerId)[i].Body);

            // The owner's own answer is the control: it comes from the route, never from the guard.
            Assert.NotEqual(HttpStatusCode.Forbidden, owner.Status);
            Assert.NotEqual(HttpStatusCode.Unauthorized, owner.Status);
            Assert.True(raised.Status == owner.Status, $"{name}: a raised key answered {raised.Status}, the owner's device {owner.Status}");
            // Where the route names its answer with a code, the raised key got the very same one - so it went as far
            // down the route as the owner's own device did.
            Assert.Equal(CodeOf(owner.Body), CodeOf(raised.Body));

            if (name == "prompt")
            {
                // Parent Control, fix 1: any session key reaches the prompt route, which types only into a session the
                // caller owns. The unraised key does not own this one, so the ROUTE refuses it, with its own sentence.
                Assert.Equal(HttpStatusCode.Forbidden, unraised.Status);
                Assert.Equal(Util.AgentInputRefusal.NotYourSession, Root(unraised.Body).GetProperty("error").GetString());
                continue;
            }
            AssertRefusedByTheGuard(unraised);
            Assert.Equal(Util.AgentInputRefusal.Typing, Root(unraised.Body).GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task CompactContinue_ARaisedKeyOnASessionItDoesNotOwn_IsRecordedBeforeAnythingIsSent_AndAnUnraisedKeyIsRefused()
    {
        // Parent Control, fix 1: a raised key is not held to ownership on compact-continue, and that door records the
        // action itself - the guard gives compact-context no grant, so the middleware writes nothing for it.
        await Raise(_raisedId);
        _gateway.TurnPushCapabilities.Record(_tenantA, DirectorId, pushesTurns: true, checksIdleBeforeTyping: true);
        var path = $"sessions/{_workerId}/compact-context";

        var unraised = await Send(_unraised, "POST", path, new { continuePrompt = "continue" });
        var raised = await Send(_raised, "POST", path, new { continuePrompt = "continue" });

        Assert.Equal(HttpStatusCode.Forbidden, unraised.Status);
        Assert.Equal(Util.AgentInputRefusal.CompactContinue, Root(unraised.Body).GetProperty("error").GetString());
        // No Director is connected, so the raised request ends at the tunnel - after its record was written.
        Assert.NotEqual(HttpStatusCode.Forbidden, raised.Status);
        var details = (await Records(GovernanceAuditEventType.RaisedAction, _raisedId))
            .Select(r => r.GetProperty("detail").GetString()!).ToList();
        Assert.Single(details, d => d.EndsWith($"POST /{path}", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(await Records(GovernanceAuditEventType.RaisedAction, _unraisedId));
    }

    [Fact]
    public async Task OwnerOnlyFleetManagerRoutes_WithARaisedKey_ReachTheRouteAsTheOwnersDeviceDoes_AndAnUnraisedKeyIsRefusedAsToday()
    {
        await Raise(_raisedId);

        foreach (var (verb, path) in OwnerOnlyFleetManagerRoutes)
        {
            var owner = await Send(_ownerA, verb, path);
            var raised = await Send(_raised, verb, path);
            var unraised = await Send(_unraised, verb, path);

            Assert.NotEqual(HttpStatusCode.Forbidden, owner.Status);
            Assert.NotEqual(HttpStatusCode.Unauthorized, owner.Status);
            Assert.True(raised.Status == owner.Status, $"{verb} {path}: a raised key answered {raised.Status}, the owner's device {owner.Status}");
            AssertRefusedByTheGuard(unraised);
        }
    }

    [Fact]
    public async Task WhatRaisedDoesNotBuy_IsRefusedToARaisedKey_ByteForByteAsToAnUnraisedOne()
    {
        await Raise(_raisedId);
        var walkthroughId = Guid.NewGuid().ToString();
        var refused = new (string Verb, string Path)[]
        {
            // The admission surface.
            ("GET", "devices"),
            ("GET", "account/devices"),
            ("DELETE", "account/devices/some-device"),
            ("POST", "account/logout"),
            ("POST", "account/email"),
            ("GET", "account/trial"),
            ("GET", "account/credits"),
            // Shutting the Gateway down.
            ("POST", "shutdown"),
            // The walkthrough's writes, which store that THE OWNER answered, snoozed or closed.
            ("POST", $"gateway/fleet-manager/walkthrough/{walkthroughId}/answered"),
            ("POST", $"gateway/fleet-manager/walkthrough/{walkthroughId}/snoozed"),
            ("POST", $"gateway/fleet-manager/walkthrough/{walkthroughId}/close"),
            // Its own trail.
            ("GET", "gateway/governance/audit-events"),
        };

        foreach (var (verb, path) in refused)
        {
            var raised = await Send(_raised, verb, path);
            var unraised = await Send(_unraised, verb, path);

            AssertRefusedByTheGuard(raised);
            Assert.Equal(unraised.Status, raised.Status);
            Assert.Equal(unraised.Body, raised.Body);
        }

        // None of those refusals is an action, so none of them left a record.
        Assert.Empty(await Records(GovernanceAuditEventType.RaisedAction, _raisedId));
    }

    [Fact]
    public async Task AnotherAccountsSession_IsAnsweredToARaisedKeyExactlyAsAnUnknownSessionIs()
    {
        await Raise(_raisedId);
        var unknownId = Guid.NewGuid().ToString();

        // POSITIVE CONTROL: the same verb on a session of its own account reaches the Director step.
        var own = await Send(_raised, "POST", $"sessions/{_workerId}/interrupt");
        var foreign = await Send(_raised, "POST", $"sessions/{_sessionInB}/interrupt");
        var unknown = await Send(_raised, "POST", $"sessions/{unknownId}/interrupt");

        Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
        Assert.Equal(unknown.Status, foreign.Status);
        Assert.Equal(unknown.Body.Replace(unknownId, "SID"), foreign.Body.Replace(_sessionInB, "SID"));
        Assert.NotEqual(foreign.Status, own.Status);
    }

    [Fact]
    public async Task ARaisedKeyOfOneAccount_IsNotRaisedInAnother()
    {
        // The same session id is raised in account B only. Account A's key for that id gains nothing from it.
        _gateway.RaisedSessions.Raise(_tenantB, _unraisedId, "device phone test", DateTime.UtcNow);

        AssertRefusedByTheGuard(await Send(_unraised, "POST", $"sessions/{_workerId}/interrupt"));
    }

    // ---- the record --------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryRaisedAction_LeavesARecordNamingTheSessionThatTookIt_AndAnUnraisedAttemptLeavesNone()
    {
        await Raise(_raisedId);

        foreach (var (_, verb, path, body) in AgentInput(_workerId))
            await Send(_raised, verb, path, body);
        foreach (var (verb, path) in OwnerOnlyFleetManagerRoutes)
            await Send(_raised, verb, path);
        foreach (var (_, verb, path, body) in AgentInput(_workerId))
            await Send(_unraised, verb, path, body);

        var records = await Records(GovernanceAuditEventType.RaisedAction, _raisedId);
        var details = records.Select(r => r.GetProperty("detail").GetString()!).ToList();
        foreach (var r in records)
        {
            Assert.Equal(_raisedId, r.GetProperty("sessionId").GetString());
            Assert.Equal($"session {_raisedId}", r.GetProperty("actor").GetString());
        }

        foreach (var (name, verb, path, _) in AgentInput(_workerId))
            Assert.True(details.Count(d => d.EndsWith($"{verb} /{path}", StringComparison.OrdinalIgnoreCase)) == 1,
                $"{name}: expected exactly one record ending '{verb} /{path}', got: {string.Join(" | ", details)}");
        foreach (var (verb, path) in OwnerOnlyFleetManagerRoutes)
            Assert.True(details.Count(d => d.EndsWith($"{verb} /{path}", StringComparison.OrdinalIgnoreCase)) == 1,
                $"expected exactly one record ending '{verb} /{path}', got: {string.Join(" | ", details)}");
        Assert.Equal(AgentInput(_workerId).Length + OwnerOnlyFleetManagerRoutes.Length, records.Count);

        // The prompt's words are never in the record.
        Assert.DoesNotContain(details, d => d.Contains("carry on"));
        // The unraised key was refused every time and has no record at all.
        Assert.Empty(await Records(GovernanceAuditEventType.RaisedAction, _unraisedId));
    }

    [Fact]
    public async Task ARouteEverySessionKeyReaches_LeavesNoRaisedRecord()
    {
        await Raise(_raisedId);

        var (status, _) = await Send(_raised, "GET", "sessions");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(await Records(GovernanceAuditEventType.RaisedAction, _raisedId));
    }

    // ---- raised follows the Fleet Manager mark -----------------------------------------------------------

    [Fact]
    public async Task SettingUpTheFleetManager_FromTheOwnersDevice_RaisesIt_AndRaisedFollowsTheMarkWhenItMovesAndClears()
    {
        var first = await Send(_ownerA, "PUT", "gateway/fleet-manager", new { sessionId = _raisedId });
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.True(_gateway.RaisedSessions.IsRaised(_tenantA, _raisedId));
        Assert.NotEqual(HttpStatusCode.Forbidden, (await Send(_raised, "POST", $"sessions/{_workerId}/interrupt")).Status);
        Assert.Single(await Records(GovernanceAuditEventType.SessionRaised, _raisedId));

        // The mark moves: the old one is lowered at that moment and the new one raised.
        var moved = await Send(_ownerA, "PUT", "gateway/fleet-manager", new { sessionId = _unraisedId });
        Assert.Equal(HttpStatusCode.OK, moved.Status);
        Assert.False(_gateway.RaisedSessions.IsRaised(_tenantA, _raisedId));
        Assert.True(_gateway.RaisedSessions.IsRaised(_tenantA, _unraisedId));
        AssertRefusedByTheGuard(await Send(_raised, "POST", $"sessions/{_workerId}/interrupt"));
        Assert.NotEqual(HttpStatusCode.Forbidden, (await Send(_unraised, "POST", $"sessions/{_workerId}/interrupt")).Status);
        Assert.Single(await Records(GovernanceAuditEventType.SessionLowered, _raisedId));
        Assert.Single(await Records(GovernanceAuditEventType.SessionRaised, _unraisedId));

        // The mark clears: nobody is raised.
        var cleared = await Send(_ownerA, "PUT", "gateway/fleet-manager", new { sessionId = (string?)null });
        Assert.Equal(HttpStatusCode.OK, cleared.Status);
        Assert.False(_gateway.RaisedSessions.IsRaised(_tenantA, _unraisedId));
        AssertRefusedByTheGuard(await Send(_unraised, "POST", $"sessions/{_workerId}/interrupt"));
        Assert.Single(await Records(GovernanceAuditEventType.SessionLowered, _unraisedId));
        Assert.Empty(_gateway.RaisedSessions.List(_tenantA));
    }

    /// <summary>
    /// THE HOLE THIS CLOSES. Any session key may set the mark (<c>fleet-manager set</c> marks the caller). If the mark
    /// alone raised a session, any session could raise itself in one call. Only the owner's own device raises by
    /// marking.
    /// </summary>
    [Fact]
    public async Task MarkingItselfTheFleetManager_WithASessionKey_DoesNotRaiseTheSession()
    {
        var marked = await Send(_unraised, "PUT", "gateway/fleet-manager", new { sessionId = _unraisedId });

        // POSITIVE CONTROL: the mark really was set - the route was reached and did its work.
        Assert.Equal(HttpStatusCode.OK, marked.Status);
        Assert.Equal(_unraisedId, Root(marked.Body).GetProperty("sessionId").GetString());

        Assert.False(_gateway.RaisedSessions.IsRaised(_tenantA, _unraisedId));
        AssertRefusedByTheGuard(await Send(_unraised, "POST", $"sessions/{_workerId}/interrupt"));
        AssertRefusedByTheGuard(await Send(_unraised, "GET", "gateway/fleet-manager/placement"));
        Assert.Empty(await Records(GovernanceAuditEventType.SessionRaised, _unraisedId));
    }

    [Fact]
    public async Task ASessionKeyMovingTheMarkAway_LowersTheFleetManagerTheOwnerSetUp_AndRaisesNobody()
    {
        await Send(_ownerA, "PUT", "gateway/fleet-manager", new { sessionId = _raisedId });
        Assert.True(_gateway.RaisedSessions.IsRaised(_tenantA, _raisedId));

        var moved = await Send(_unraised, "PUT", "gateway/fleet-manager", new { sessionId = _unraisedId });

        Assert.Equal(HttpStatusCode.OK, moved.Status);
        Assert.False(_gateway.RaisedSessions.IsRaised(_tenantA, _raisedId));
        Assert.False(_gateway.RaisedSessions.IsRaised(_tenantA, _unraisedId));
        Assert.Single(await Records(GovernanceAuditEventType.SessionLowered, _raisedId));
    }

    // ---- an entry ends with its session ------------------------------------------------------------------

    [Fact]
    public async Task WhenTheSessionEnds_ItsEntryIsGone_AndItsKeyNoLongerPasses()
    {
        await Raise(_raisedId);
        Assert.Single(_gateway.RaisedSessions.List(_tenantA));

        // A Director of the account says the session is over, over a real tunnel connection, through the hub method
        // a Director really calls when it reaps a session.
        await using (var director = await FakeTunnelDirector.StartAsync(_gateway, _directorKeyA, "director-raised-reaper", "MRA"))
            await director.RevokeSessionKeyAsync(_raisedId);

        Assert.Empty(_gateway.RaisedSessions.List(_tenantA));
        Assert.False(_gateway.RaisedSessions.IsRaised(_tenantA, _raisedId));
        var after = await Send(_raised, "POST", $"sessions/{_workerId}/interrupt");
        Assert.Equal(HttpStatusCode.Unauthorized, after.Status);
    }

    // ---- messages ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Messages_FromARaisedSender_PassTheRelationshipRuleAndBothRates_AndAnIdenticalUnreadOneIsStillDropped()
    {
        await Raise(_raisedId);

        // The stranger is related to nobody. Eight in a row, to one recipient: over six an hour and inside ten minutes.
        for (var i = 1; i <= 8; i++)
        {
            var sent = await Send(_raised, "POST", $"sessions/{_strangerId}/message", new { text = $"note {i} from the raised session" });
            Assert.Equal(HttpStatusCode.OK, sent.Status);
            Assert.Equal("queued", Root(sent.Body).GetProperty("status").GetString());
        }

        // The duplicate rule stays: the recipient has not read "note 8", so the same words again are dropped.
        var again = await Send(_raised, "POST", $"sessions/{_strangerId}/message", new { text = "note 8 from the raised session" });
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal("duplicate", Root(again.Body).GetProperty("status").GetString());

        // Every message the waiver let through is on the record, naming the sender.
        var records = await Records(GovernanceAuditEventType.RaisedAction, _raisedId);
        Assert.Equal(8, records.Count);
        Assert.All(records, r => Assert.Equal($"session {_raisedId}", r.GetProperty("actor").GetString()));
        Assert.All(records, r => Assert.Contains(_strangerId, r.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task Messages_FromAnUnraisedSender_AreLimitedAsToday()
    {
        var (status, body) = await Send(_unraised, "POST", $"sessions/{_strangerId}/message", new { text = "a note from a session nobody raised" });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("refused", Root(body).GetProperty("status").GetString());
        Assert.StartsWith("You may message only the session that started you and the sessions you started.", Root(body).GetProperty("error").GetString());
        Assert.Empty(await Records(GovernanceAuditEventType.RaisedAction, _unraisedId));
    }

    // ---- the list survives a restart ---------------------------------------------------------------------

    [Fact]
    public async Task TheList_SurvivesAGatewayRestart()
    {
        await Raise(_raisedId);
        await _gateway.StopAsync();

        // A NEW host over the same instances directory: nothing of the old process is left but its files.
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        Assert.True(_gateway.RaisedSessions.IsRaised(_tenantA, _raisedId));
        var entry = Assert.Single(_gateway.RaisedSessions.List(_tenantA));
        Assert.Equal(_raisedId, entry.SessionId);
        Assert.Equal(Fleet.RaisedSessionSources.Owner, entry.Source);
        Assert.Empty(_gateway.RaisedSessions.List(_tenantB));
    }
}
