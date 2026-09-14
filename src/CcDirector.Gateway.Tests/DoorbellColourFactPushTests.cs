using System.Net.Sockets;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Defect 14: three colour inputs were invisible to the Gateway until something ELSE happened.
///
/// The Gateway folds orange from <c>IsTranscribing</c>, orange from <c>IsAutoExplaining</c> and purple from
/// <c>IsBackgroundRunning</c>, reading them off the pushed <c>SessionDto</c>. But nothing pushed when they
/// CHANGED. Each raised a Director event into an empty room - verified by looking for subscribers rather
/// than call sites: <c>OnIsBackgroundRunningChanged</c> and <c>OnIsExplainingChanged</c> had ZERO
/// subscribers anywhere in the codebase, and <c>OnIsTranscribingChanged</c> had exactly one, a desktop UI
/// handler that pushes nothing. So the fact sat on the Session until some unrelated activity change
/// happened to push a delta, or the ten-second re-push came round - and those three colours lagged by up to
/// that long. Three comments on the events claimed they notified the SessionStatusWingman "so it can
/// repaint the dot", which is how the gap stayed invisible: the events looked wired.
///
/// THIS IS THE REAL WIRE. A real <see cref="GatewayHost"/>, and a real <see cref="ControlApiHost"/> whose
/// OWN config points at it, so the Director builds its real <c>GatewayStreamClient</c> and runs the real
/// <c>WireDoorbellPush</c>. Nothing is hand-pushed: the test flips the flag on the Session exactly as
/// production does and waits for the fact to appear in the Gateway's pushed store.
///
/// THE WINDOW IS THE ASSERTION. Each wait is far shorter than the ten-second re-push that would eventually
/// carry the fact anyway - so a pass means a PUSH happened, not that we waited out the fallback. Remove any
/// of the three subscriptions and the matching test fails by timing out, which is the defect exactly.
///
/// Design: docs/new_architecture/sessions.html, defect 14.
/// </summary>
[Collection("DirectorRoot")]
public sealed class DoorbellColourFactPushTests : IAsyncLifetime
{
    private const string Token = "test-token-doorbell-14";
    private const string DirectorId = "dir-doorbell";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _gatewayInstances = Path.Combine(Path.GetTempPath(), "cc-doorbell-gw-" + Guid.NewGuid().ToString("N"));
    private readonly string _directorInstances = Path.Combine(Path.GetTempPath(), "cc-doorbell-dir-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private SessionManager _sm = null!;
    private ControlApiHost _host = null!;

    /// <summary>Well under the ten-second re-push, so a pass can only be a push.</summary>
    private static readonly TimeSpan PushWindow = TimeSpan.FromSeconds(4);

    public DoorbellColourFactPushTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-doorbell-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _gatewayInstances,
            workListsPath: Path.Combine(_gatewayInstances, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        // Point THIS Director's own config at the real Gateway, so ControlApiHost.StartAsync builds its real
        // stream client and wires the real doorbell. That is the whole point: the push under test is the
        // host's own, not one the test constructs.
        // CcStorage.ConfigJson() is <root>/config/config.json - NOT <root>/config.json.
        var configDir = Path.Combine(_root, "config");
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(Path.Combine(configDir, "config.json"), $$"""
        {
          "gateway": {
            "url": "http://127.0.0.1:{{_gateway.Port}}",
            "token": "{{Token}}",
            "streamMode": true
          }
        }
        """);

        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask,
            directorId: DirectorId, instancesDirectory: _directorInstances);
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _sm.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        TryDelete(_gatewayInstances);
        TryDelete(_directorInstances);
        TryDelete(_root);
    }


    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    /// <summary>Wait for a pushed session to satisfy a predicate, or return false at the deadline.</summary>
    private async Task<bool> WaitForPushed(string sessionId, Func<Contracts.SessionDto, bool> predicate, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            var pushed = _gateway.PushedSessions.SnapshotFresh(TenantId.Local, TimeSpan.FromSeconds(30));
            var mine = pushed.FirstOrDefault(t => t.Session.SessionId == sessionId).Session;
            if (mine is not null && predicate(mine)) return true;
            await Task.Delay(100);
        }
        return false;
    }

    private async Task<Session> ASessionTheGatewayCanSee()
    {
        var s = _sm.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        Assert.True(await WaitForPushed(s.Id.ToString(), _ => true, TimeSpan.FromSeconds(15)),
            "the Director's stream never delivered the session - the harness is broken, not the fix");
        return s;
    }

    [Fact]
    public async Task Transcribing_ReachesTheGateway_OnItsOwnPush()
    {
        var s = await ASessionTheGatewayCanSee();

        s.IsTranscribing = true; // exactly what the desktop's background dictation send does

        Assert.True(await WaitForPushed(s.Id.ToString(), d => d.IsTranscribing, PushWindow),
            "IsTranscribing never reached the Gateway inside the push window - the Gateway folds the " +
            "dictation orange from this fact, so it would lag by up to one ten-second re-push (defect 14)");
    }

    [Fact]
    public async Task BackgroundRunning_ReachesTheGateway_OnItsOwnPush()
    {
        var s = await ASessionTheGatewayCanSee();

        s.SetBackgroundRunning(true, "running in background"); // what ProactiveExplainService does

        Assert.True(await WaitForPushed(s.Id.ToString(), d => d.IsBackgroundRunning, PushWindow),
            "IsBackgroundRunning never reached the Gateway inside the push window - the Gateway folds the " +
            "purple from this fact. Before defect 14 this event had ZERO subscribers anywhere.");
    }

    /// <summary>
    /// THE FOURTH INPUT DEFECT 14 DID NOT COUNT: the GATE on the other three.
    ///
    /// This class says "three colour inputs" and the fold reads four - yellow needs WingmanEnabled AND
    /// IsAutoExplaining, purple needs WingmanEnabled AND IsBackgroundRunning. So turning the Wingman OFF on
    /// a session parked on its background task changes the right answer from purple "Background" to red
    /// "Needs you" while none of the three flags move, nothing pushes, and the phone keeps the stale fold
    /// until the ten-second re-push.
    ///
    /// It survived defect 14 and three later passes hunting exactly this, because a gate is not the thing
    /// being rendered - it does not look like a colour input. Which is why the rule cannot be a judgement
    /// call: if the fold READS it, it pushes.
    /// </summary>
    [Fact]
    public async Task TurningTheWingmanOff_ReachesTheGateway_OnItsOwnPush()
    {
        var s = await ASessionTheGatewayCanSee();

        // The state the gate actually gates: parked on its own background task, Wingman on -> purple.
        s.WingmanEnabled = true;
        s.SetBackgroundRunning(true, "running in background");
        Assert.True(await WaitForPushed(s.Id.ToString(), d => d.WingmanEnabled && d.IsBackgroundRunning, PushWindow),
            "the purple setup never reached the Gateway - the real assertion below cannot mean anything yet");

        // Exactly what the wingman-enabled=false command does (SessionWriteExecutor).
        s.WingmanEnabled = false;

        Assert.True(await WaitForPushed(s.Id.ToString(), d => !d.WingmanEnabled, PushWindow),
            "WingmanEnabled=false never reached the Gateway inside the push window - it GATES the purple " +
            "and yellow the fold reads, so the phone would keep folding purple 'Background' for a session " +
            "the desktop already calls red 'Needs you', until one ten-second re-push");
    }

    [Fact]
    public async Task AutoExplaining_ReachesTheGateway_OnItsOwnPush()
    {
        var s = await ASessionTheGatewayCanSee();

        s.IsExplaining = true; // what ProactiveExplainService sets around a briefing

        Assert.True(await WaitForPushed(s.Id.ToString(), d => d.IsAutoExplaining, PushWindow),
            "IsAutoExplaining never reached the Gateway inside the push window - the Gateway folds the " +
            "auto-explain orange from this fact. Before defect 14 this event had ZERO subscribers anywhere.");
    }
}
