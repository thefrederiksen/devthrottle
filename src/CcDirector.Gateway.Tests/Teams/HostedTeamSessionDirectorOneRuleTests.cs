using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Prompts;
using CcDirector.Gateway.Teams;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// ONE ANSWER FOR WHICH DIRECTOR HOLDS A SESSION IN A TEAM (devthrottle_internal#2311), END TO END. A REAL hosted
/// <see cref="GatewayHost"/> with Teams released, a team with Alice and Bob, and both of their Directors connected over the
/// REAL tunnel. The session is Alice's: her Director registered its key, so its key row names her Director. Bob's
/// Director lists the same session id - which the roster accepts from any Director.
///
/// Each test runs two phases, and neither rests on the roster's hash order:
/// <list type="bullet">
/// <item>BOB'S ROW IS THE ONLY ROW of the id - asserted - so the roster's first row IS Bob's. Every path that takes the
/// first row sends Bob's Director what is meant for Alice's session. With the one rule it reaches nobody: Bob's Director
/// receives nothing. This is the red check: bypass the single point and Bob's Director receives it.</item>
/// <item>Alice's Director lists the session too. Now it reaches Alice's Director - the PRESENCE that makes the absence on
/// Bob's side mean something - and still never Bob's.</item>
/// </list>
/// Four paths: the typed prompt route, a held typed prompt the Gateway's driver drives, a dev report's delivery, and the
/// Fleet Manager's retirement close.
///
/// PARKED SUITE. Gateway.Tests serializes machine-wide and does not run in the default gate.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedTeamSessionDirectorOneRuleTests : IAsyncLifetime
{
    private const string Token = "test-token-one-rule";
    private const string AliceDirector = "director-alice";
    private const string BobDirector = "director-bob";
    private readonly string _teamOwner = "sub-or-owner-" + Guid.NewGuid().ToString("N");
    private readonly string _alice = "sub-or-alice-" + Guid.NewGuid().ToString("N");
    private readonly string _bob = "sub-or-bob-" + Guid.NewGuid().ToString("N");
    private readonly string _sessionId = Guid.NewGuid().ToString();
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-one-rule-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<DirectorCommand> _seenByAlice = new();
    private readonly ConcurrentQueue<DirectorCommand> _seenByBob = new();
    private readonly ITestOutputHelper _out;
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector _aliceDirector = null!;
    private FakeTunnelDirector _bobDirector = null!;
    private string _aliceKey = "";
    private TenantId _team;
    private string? _priorHosted;
    private string? _priorRoot;

    /// <summary>What Alice's Director answers a prompt with. Default: delivered.</summary>
    private Func<DirectorCommand, DirectorCommandResult>? _alicePrompt;

    public HostedTeamSessionDirectorOneRuleTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _instancesDir);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true, teamsReleased: true);
        // Only a test's own call drives a held delivery, so nothing races the phases below.
        _gateway.HeldDeliveryTickInterval = TimeSpan.FromHours(1);
        await _gateway.StartAsync();
        HostedTeamBill.CreateTable(_gateway);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"), Timeout = TimeSpan.FromMinutes(2) };

        var teamId = _gateway.TeamRegistry.CreateTeam(_teamOwner, "A").Team!.TeamId;
        Assert.True(_gateway.TeamRegistry.AddMember(teamId, _alice, TeamRole.Developer).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(teamId, _bob, TeamRole.Developer).IsDone);
        HostedTeamBill.Start(_gateway, teamId, seats: 5);
        _team = new TenantId(teamId);

        _aliceKey = TeamKey(teamId, _alice, AliceDirector);
        _aliceDirector = await FakeTunnelDirector.StartAsync(_gateway, _aliceKey, AliceDirector,
            dispatch: cmd => { _seenByAlice.Enqueue(cmd); return AliceAnswers(cmd); });
        _bobDirector = await FakeTunnelDirector.StartAsync(_gateway, TeamKey(teamId, _bob, BobDirector), BobDirector,
            dispatch: cmd => { _seenByBob.Enqueue(cmd); return PromptDelivered(); });

        // THE RECORD THAT MAKES THE SESSION ALICE'S: its key, registered by her Director before it lists the session.
        await _aliceDirector.RegisterSessionKeyAsync(_sessionId, GatewaySessionKey.Mint(), DateTime.UtcNow.AddHours(1));
        Assert.Equal(AliceDirector, _gateway.SessionKeys.DirectorOfSession(_team, _sessionId));

        // Alice's Director is connected and lists another session; Bob's lists Alice's session id. Bob's row is the ONLY
        // row of that id, so the roster's first row is Bob's - asserted, never assumed from hash order.
        await _aliceDirector.PushSnapshotAsync(Row(Guid.NewGuid().ToString(), "WaitingForInput"));
        await _bobDirector.PushSnapshotAsync(Row(_sessionId, "Idle"));
        Assert.Equal(new[] { BobDirector }, _gateway.PushedSessions.DirectorsHoldingSession(_team, _sessionId));
    }

    public async Task DisposeAsync()
    {
        await _aliceDirector.DisposeAsync();
        await _bobDirector.DisposeAsync();
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    // ---- the typed prompt route ------------------------------------------------------------------------------------

    [Fact]
    public async Task ATypedPrompt_NeverReachesAColleaguesDirectorThatListsTheSession_AndReachesTheOwners()
    {
        // ONLY BOB'S ROW: nobody holds the session, so the prompt is HELD waiting for its Director - never sent - and
        // Bob's Director is sent nothing.
        var (held, heldBody) = await PostPrompt("only bob lists it");
        Assert.Equal(HttpStatusCode.Accepted, held);
        Assert.Equal("waiting-for-director", heldBody.GetProperty("directorState").GetString());
        Assert.Empty(Seen(_seenByBob, "prompt"));

        // ALICE'S DIRECTOR LISTS IT TOO: the prompt reaches her Director, and still never Bob's.
        await AliceListsTheSession();
        var (delivered, _) = await PostPrompt("both list it");
        Assert.Equal(HttpStatusCode.OK, delivered);
        Assert.Single(Seen(_seenByAlice, "prompt"));
        Assert.Empty(Seen(_seenByBob, "prompt"));
    }

    // ---- a held typed prompt, driven by the Gateway ----------------------------------------------------------------

    [Fact]
    public async Task AHeldTypedPrompt_IsNeverAskedOfAColleaguesDirector_AndIsAskedOfTheOwners()
    {
        // Held: Alice's Director takes the prompt and gives no answer in time.
        await AliceListsTheSession();
        _alicePrompt = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds");
        var (status, body) = await PostPrompt("typed while the Director is starved");
        Assert.Equal(HttpStatusCode.Accepted, status);
        var deliveryId = body.GetProperty("deliveryId").GetString()!;
        var store = _gateway.TypedPrompts.ForTenant(_team);

        // ONLY BOB'S ROW: Alice's Director stops listing the session. The driver locates nobody and asks nobody.
        await _aliceDirector.PushSnapshotAsync();
        Assert.Equal(new[] { BobDirector }, _gateway.PushedSessions.DirectorsHoldingSession(_team, _sessionId));
        var held = await DriveAsync(store, deliveryId);
        Assert.Equal(TypedDriveResult.Held, held);
        Assert.Empty(Seen(_seenByBob, DeliveryStateRequest.Verb));
        Assert.Empty(Seen(_seenByBob, "prompt"));

        // ALICE'S DIRECTOR LISTS IT AGAIN: the driver asks HER Director what became of it, and never Bob's.
        await AliceListsTheSession();
        await DriveAsync(store, deliveryId);
        Assert.Single(Seen(_seenByAlice, DeliveryStateRequest.Verb));
        Assert.Empty(Seen(_seenByBob, DeliveryStateRequest.Verb));
        Assert.Empty(Seen(_seenByBob, "prompt"));
    }

    // ---- a dev report's delivery -----------------------------------------------------------------------------------

    [Fact]
    public async Task ADevReportsNote_IsNeverDeliveredToAColleaguesDirector_AndIsDeliveredToTheOwners()
    {
        var marker = "MARKER-ONE-RULE-" + Guid.NewGuid().ToString("N");
        var report = _gateway.DevReportsForTest.Publish(_team, _sessionId, $@"C:\work\{Guid.NewGuid():N}.html",
            "<header data-dev-report=\"header\" data-dev-report-status=\"waiting-on-you\"><h1>Report</h1></header>",
            "waiting-on-you", "Report", DateTime.UtcNow, _alice).Report;
        var note = new DevReportItem("n-" + Guid.NewGuid().ToString("N"), DevReportItem.Note, marker,
            new DevReportAnchor(DevReportAnchor.Text, "p", "What changed.", null, null, null), "", "", "", "", "");

        // ONLY BOB'S ROW, Idle: the note is held, and Bob's Director is typed nothing.
        await _gateway.DevReportDeliveryForTest.SendAsync(_team, report, new[] { note }, "device", CancellationToken.None);
        await _gateway.DevReportDeliveryForTest.SettleAsync(_team, _sessionId, CancellationToken.None);
        Assert.DoesNotContain(_seenByBob, c => c.PayloadJson.Contains(marker, StringComparison.Ordinal));

        // ALICE'S DIRECTOR LISTS IT, idle: the settle pass delivers the note to HER Director, and never to Bob's.
        await AliceListsTheSession();
        await _gateway.DevReportDeliveryForTest.SettleAsync(_team, _sessionId, CancellationToken.None);
        Assert.Contains(_seenByAlice, c => c.Verb == "prompt" && c.PayloadJson.Contains(marker, StringComparison.Ordinal));
        Assert.DoesNotContain(_seenByBob, c => c.PayloadJson.Contains(marker, StringComparison.Ordinal));
    }

    // ---- the Fleet Manager's retirement close ----------------------------------------------------------------------

    [Fact]
    public async Task TheFleetManagersRetirementClose_IsNeverSentToAColleaguesDirector_AndIsSentToTheOwners()
    {
        // Alice's session is the team's marked Fleet Manager, and a successor - also on Alice's Director - waits to
        // replace it. The old one is closed once it is seen Idle twice; the close goes to the Director that holds it.
        var successor = Guid.NewGuid().ToString();
        await _aliceDirector.RegisterSessionKeyAsync(successor, GatewaySessionKey.Mint(), DateTime.UtcNow.AddHours(1));
        await _aliceDirector.PushSnapshotAsync(Row(successor, "Working"));
        var now = DateTime.UtcNow;
        _gateway.TenantSettingsResolver.SetFleetManagerSessionId(_team, _sessionId, now);
        _gateway.TenantSettingsResolver.SetFleetManagerSuccessor(_team, successor, _sessionId, now);

        // ONLY BOB'S ROW, Idle: the replacement waits - the old Fleet Manager is not in the roster as anyone's - and Bob's
        // Director is never sent the close, over more than two of the replacement's looks.
        await _gateway.FleetManagerPlacement.ResumePendingAsync(_team);
        await Task.Delay(Fleet.FleetManagerPlacementService.RetirePollInterval * 2 + TimeSpan.FromSeconds(2));
        Assert.Empty(Seen(_seenByBob, "kill"));
        Assert.Equal(_sessionId, _gateway.TenantSettingsResolver.FleetManagerSessionId(_team));

        // ALICE'S DIRECTOR LISTS IT, Idle: the close reaches HER Director, and never Bob's.
        await _aliceDirector.PushSnapshotAsync(Row(_sessionId, "Idle"), Row(successor, "Working"));
        var deadline = DateTime.UtcNow + Fleet.FleetManagerPlacementService.RetirePollInterval * 4;
        while (Seen(_seenByAlice, "kill").Length == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Single(Seen(_seenByAlice, "kill"));
        Assert.Empty(Seen(_seenByBob, "kill"));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private async Task AliceListsTheSession()
    {
        await _aliceDirector.PushSnapshotAsync(Row(_sessionId, "WaitingForInput"));
        Assert.Equal(2, _gateway.PushedSessions.DirectorsHoldingSession(_team, _sessionId).Count);
    }

    /// <summary>One drive of a held typed prompt, inside the team's scope - where the Gateway's own driver runs it (its
    /// per-tenant pass). A hosted Gateway sends nothing down a tunnel with no account in scope.</summary>
    private async Task<TypedDriveResult> DriveAsync(TypedPromptStore store, string deliveryId)
    {
        using var scope = _gateway.TenantBoundaryForTests.EnterScope(_team);
        return await _gateway.TypedPromptDriver.DriveOnceAsync(_team, store, deliveryId, TypedPromptDecisions.DriveTick);
    }

    private DirectorCommandResult AliceAnswers(DirectorCommand cmd)
    {
        if (cmd.Verb == "prompt" && _alicePrompt is { } played)
            return played(cmd);
        if (cmd.Verb == DeliveryStateRequest.Verb)
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "not served in this test");
        return PromptDelivered();
    }

    private static DirectorCommandResult PromptDelivered() =>
        FakeTunnelDirector.Ok(new PromptResponse { Accepted = true, SentAt = DateTime.UtcNow, ActivityState = "Working", DeliveryState = DeliveryState.Delivered });

    private DirectorCommand[] Seen(ConcurrentQueue<DirectorCommand> seen, string verb) =>
        seen.Where(c => c.Verb == verb && c.SessionId == _sessionId).ToArray();

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostPrompt(string text)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"sessions/{_sessionId}/prompt");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _aliceKey);
        req.Content = JsonContent.Create(new { text, appendEnter = true });
        using var resp = await _http.SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"POST sessions/{_sessionId}/prompt -> {(int)resp.StatusCode}: {raw}");
        return (resp.StatusCode, string.IsNullOrEmpty(raw) ? default : JsonDocument.Parse(raw).RootElement.Clone());
    }

    private string TeamKey(string team, string subject, string directorId) =>
        _gateway.Devices.RegisterForTenant(new TenantId(team), subject,
            Api.HostedEnrollmentEndpoint.TeamScopedDeviceId(team, subject, directorId), "M-" + directorId).DeviceKey;

    private static SessionDto Row(string sid, string state) => new()
    {
        SessionId = sid,
        Agent = "claude",
        RepoPath = "/repo",
        ActivityState = state,
        Status = "Running",
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };
}
