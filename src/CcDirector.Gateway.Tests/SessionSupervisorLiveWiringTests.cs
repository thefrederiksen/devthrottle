using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Supervision;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #915, THE WIRING PROOF. The engine tests beside this one prove the decision-making with a fake
/// environment; this one proves the thing a fake can never prove - that a REAL session, on a REAL Gateway,
/// crossing a REAL Working -> idle boundary, actually gets a "continue" typed into it over the tunnel.
///
/// Everything in the path is production code: the live <c>TurnEndWatcher</c>, the turn-end callback in
/// <c>GatewayHost</c>, the supervisor and its real <c>GatewaySupervisorEnvironment</c>, the pushed roster read,
/// and the tunnel verbs. The only stand-in is the Director at the far end, which answers the screen read with
/// the exact line from the 2026-07-21 incident and records what it is asked to do.
///
/// REVERT-PROOF: delete the <c>_sessionSupervisor?.OnTurnEnd(signal)</c> line from the turn-end callback in
/// GatewayHost and <see cref="AParkedSessionThatDiedOnAConnectionFault_IsSentContinueOverTheTunnel"/> goes RED -
/// no prompt verb ever reaches the Director. The engine tests would all still pass, which is exactly why this
/// test exists.
///
/// The tenant's first wait is turned down to the validated minimum (5 seconds) so the test costs seconds
/// instead of the shipped 45.
/// </summary>
public sealed class SessionSupervisorLiveWiringTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string Session = "supervised-sess";
    private const string DirectorId = "dir-supervised";

    private TenantId _tenant;
    private GatewayHost _gateway = null!;
    private FakeTunnelDirector _director = null!;
    private readonly ConcurrentQueue<DirectorCommand> _seen = new();

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-supervisor-live-" + Guid.NewGuid().ToString("N"));

    /// <summary>The live screen the fake Director answers with: the July 21 terminating fault.</summary>
    private static readonly string[] FaultScreen =
    {
        "* Running gh pr checks...",
        "API Error: Unable to connect to API (ENOTFOUND)",
    };

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();

        var device = HostedTestEnrollment.Enroll(_gateway, "sub-sup", "sup@example.com", "dev-sup", "MS");
        _tenant = device.Tenant;

        // Turn the first wait down to the validated floor so this test costs seconds, and keep the ceiling at
        // one send so the ladder ends immediately after the assertion below.
        _gateway.TenantSettingsResolver.SetSessionSupervisorFirstRetrySeconds(_tenant, 5, DateTime.UtcNow);
        _gateway.TenantSettingsResolver.SetSessionSupervisorMaxLongRetries(_tenant, 0, DateTime.UtcNow);

        _director = await FakeTunnelDirector.StartAsync(_gateway, device.DeviceKey, DirectorId, "MS",
            dispatch: cmd =>
            {
                _seen.Enqueue(cmd);
                return cmd.Verb switch
                {
                    "screen-grid" => FakeTunnelDirector.Ok(new
                    {
                        sessionId = Session,
                        rows = FaultScreen,
                        cursorRow = -1,
                        cursorCol = -1,
                        cursorVisible = false,
                        isAlternateScreen = true,
                        hasGrid = true,
                    }),
                    "prompt" => FakeTunnelDirector.Ok(new { ok = true, sent = true }),
                    _ => FakeTunnelDirector.Ok(new { ok = true }),
                };
            });

        // The session is live on that Director and parked at a turn end, exactly as the roster would show it.
        await _director.PushSnapshotAsync(ParkedSession());
    }

    public async Task DisposeAsync()
    {
        await _director.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task AParkedSessionThatDiedOnAConnectionFault_IsSentContinueOverTheTunnel()
    {
        Assert.NotNull(_gateway.SessionSupervisorForTest);

        // A REAL Working -> WaitingForInput boundary through the live watcher - the same two observations a
        // Director's doorbell ping and heartbeat produce in production.
        _gateway.TurnEndWatcherForTest!.Observe(_tenant, Session, "Working", DirectorId);
        _gateway.TurnEndWatcherForTest!.Observe(_tenant, Session, "WaitingForInput", DirectorId);

        // POSITIVE CONTROL FIRST: the supervisor read the session's live screen over the tunnel. Without this
        // the absence of a prompt below could pass vacuously in an engine that never woke up at all.
        Assert.NotNull(await WaitForVerb("screen-grid", TimeSpan.FromSeconds(10)));

        // THE POINT: after the (shortened) wait, the session is sent "continue" - no human involved.
        var prompt = await WaitForVerb("prompt", TimeSpan.FromSeconds(30));
        Assert.NotNull(prompt);
        var request = System.Text.Json.JsonSerializer.Deserialize<PromptRequest>(
            prompt!.PayloadJson, FakeTunnelDirector.WebJson);
        Assert.Equal(SessionSupervisor.ContinueText, request!.Text);
        Assert.True(request.AppendEnter);
        Assert.Equal(Session, prompt!.SessionId);
    }

    [Fact]
    public async Task ASessionThatFinishedItsTurnCleanly_IsSentNothing()
    {
        // The same live path, with a healthy screen. The turn-end event fires, the supervisor reads the screen -
        // and stops. This is the invariant that matters most: the engine must be silent on a finished turn.
        _director.OnCommand(cmd =>
        {
            _seen.Enqueue(cmd);
            return cmd.Verb switch
            {
                "screen-grid" => FakeTunnelDirector.Ok(new
                {
                    sessionId = Session,
                    rows = new[] { "All three edits are done and the tests pass. Anything else?" },
                    cursorRow = -1,
                    cursorCol = -1,
                    cursorVisible = false,
                    isAlternateScreen = true,
                    hasGrid = true,
                }),
                _ => FakeTunnelDirector.Ok(new { ok = true }),
            };
        });

        _gateway.TurnEndWatcherForTest!.Observe(_tenant, Session, "Working", DirectorId);
        _gateway.TurnEndWatcherForTest!.Observe(_tenant, Session, "WaitingForInput", DirectorId);

        // POSITIVE CONTROL: the engine did wake up and did look.
        Assert.NotNull(await WaitForVerb("screen-grid", TimeSpan.FromSeconds(10)));

        // ABSENCE: well past the shortened first wait, nothing was ever typed into the session.
        await Task.Delay(TimeSpan.FromSeconds(8));
        Assert.DoesNotContain(_seen, c => c.Verb == "prompt");
    }

    private async Task<DirectorCommand?> WaitForVerb(string verb, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            var hit = _seen.FirstOrDefault(c => c.Verb == verb);
            if (hit is not null) return hit;
            await Task.Delay(50);
        }
        return null;
    }

    private static SessionDto ParkedSession() => new()
    {
        SessionId = Session,
        Agent = "claude",
        RepoPath = "/repo",
        ActivityState = "WaitingForInput",
        Status = "Running",
        StatusColor = "red",
        IsAlternateScreen = true,
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };
}
