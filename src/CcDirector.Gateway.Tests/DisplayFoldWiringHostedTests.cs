using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The trace colour and the display push take their fold inputs from ONE place, proved through the host's own wiring
/// (the Wingman inspector, phase 2, inspection round 2, finding 2).
///
/// THE GAP THIS CLOSES. <c>DisplayFoldTraceMatchesPushTests</c> builds its own <see cref="Fleet.DisplayFold"/> and its own
/// <see cref="TurnVerdictTraceRowStamp"/>, and the source scan there looks only for a second caller of the push fold. So
/// when the inspector changed the one line in <see cref="GatewayHost"/> that hands the trace stamp its fold - to a fold
/// that named its own inputs and left out the voice-waiting clock, the exact shape of the round 1 defect - 133 targeted
/// tests stayed green. This test uses nothing it built: the host's push (<see cref="Fleet.FleetDisplayStateObserver.FoldedFleet"/>,
/// the fold the display sweep sends) and the host's trace stamp, over the host's roster, clocks and registries.
///
/// WHAT IT EXERCISES. One row per input a bypass is most likely to drop, each put into a state where that input changes
/// the row: a voice session whose wait has given up (the voice facts and the voice-waiting clock) and a snoozed session (the
/// snooze registry). For each, the push is checked to really be in that state before the trace is compared to it.
///
/// WHAT IT DOES NOT PROVE. A bypass that keeps these inputs and changes another - the needs-you clock, the snooze-expiry
/// memory, the hand raises - is not seen here. (A raised hand was tried and dropped: it changes only a supervised row, and
/// on an unsupervised one it pushed the same red "Needs you" as a row with no input, so it proved nothing.) It is a guard on the wiring for the inputs it exercises, not a proof that every input is
/// shared; that is what <see cref="Fleet.DisplayFold"/>'s one private fold is for.
/// </summary>
public sealed class DisplayFoldWiringHostedTests : IAsyncLifetime
{
    private const string SharedToken = "display-fold-wiring-token";
    private const string DirectorId = "director-fold-wiring";

    private readonly ITestOutputHelper _out;

    private GatewayHost _gateway = null!;
    private TenantId _tenant;

    private readonly string _voiceSession = Guid.NewGuid().ToString();
    private readonly string _snoozedSession = Guid.NewGuid().ToString();
    private readonly string _plainSession = Guid.NewGuid().ToString();
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-display-fold-wiring-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;

    public DisplayFoldWiringHostedTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        var account = HostedTestEnrollment.Enroll(
            _gateway, $"sub-fold-{_runId}", $"fold-{_runId}@example.com", $"dev-fold-{_runId}", "MFW");
        _tenant = account.Tenant;
        Assert.True(_gateway.TenantBoundary.IsHosted, "The harness must be running the HOSTED tenant boundary.");
    }

    public async Task DisposeAsync()
    {
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    private static SessionDto Row(string id, bool voiceMode = false) => new()
    {
        SessionId = id,
        Name = id,
        ActivityState = "WaitingForInput",
        LastActivityAt = DateTime.UtcNow,
        VoiceMode = voiceMode,
    };

    private TurnVerdictTrace SkippedTrace(string sessionId) => new()
    {
        TraceId = "trace-" + sessionId,
        SessionId = sessionId,
        RecordedAtUtc = DateTime.UtcNow,
        TurnEndObservedAtUtc = DateTime.UtcNow,
        Trigger = "clock",
        Outcome = TurnVerdictTraceOutcomes.Skipped,
        ColourEnabled = true,
    };

    [Fact]
    public void The_hosts_trace_stamp_records_the_colour_and_label_the_hosts_push_shows_for_each_input_it_folds()
    {
        _gateway.Registry.RegisterFromStream(DirectorId, "MACHINE-FOLD", "soren", "1.0", pid: 4321,
            startedAt: DateTime.UtcNow, tenant: _tenant);
        _gateway.PushedSessions.RegisterConnection(_tenant, DirectorId, "conn-fold");
        Assert.True(_gateway.PushedSessions.ApplySnapshot(_tenant, DirectorId, "conn-fold", 1, new List<SessionDto>
        {
            Row(_voiceSession, voiceMode: true),
            Row(_snoozedSession),
            Row(_plainSession),
        }));

        // Each input in a state that changes its row. The voice wait began four minutes ago, past the give-up.
        _gateway.VoiceWaitingClockForTest.StartWaitForTest(_tenant, _voiceSession, DateTime.UtcNow.AddMinutes(-4));
        List<SessionDto> pushed;
        using (_gateway.TenantBoundary.EnterScope(_tenant))
        {
            _gateway.SnoozeRegistry.Snooze(_snoozedSession, DateTime.UtcNow.AddHours(1), DirectorId);

            // THE HOST'S PUSH: the fold the display sweep sends, inside the account's pass as the sweep runs it.
            pushed = _gateway.FleetDisplayState.FoldedFleet();
        }

        foreach (var row in pushed)
            _out.WriteLine($"push  {row.SessionId}: {row.EffectiveColor} \"{row.StateLabel}\"");
        var voice = pushed.Single(r => r.SessionId == _voiceSession);
        var snoozed = pushed.Single(r => r.SessionId == _snoozedSession);

        // THE CONTROLS: the push really is in each state, so an agreement below is about that input.
        Assert.Equal("red", voice.EffectiveColor);
        Assert.StartsWith("Voice did not arrive", voice.StateLabel);
        // A row with neither input, folded by the same push, is what the snoozed row would show if its snooze were lost.
        var plain = pushed.Single(r => r.SessionId == _plainSession);
        Assert.NotEqual((plain.EffectiveColor, plain.StateLabel), (snoozed.EffectiveColor, snoozed.StateLabel));

        // THE HOST'S TRACE STAMP, called as the writer calls it: on a thread with no account in scope.
        var stamp = _gateway.TurnVerdictTraceRowStampForTest;
        foreach (var expected in new[] { voice, snoozed })
        {
            var trace = stamp.Stamp(_tenant, SkippedTrace(expected.SessionId));
            _out.WriteLine($"trace {expected.SessionId}: {trace.RowColour} \"{trace.RowLabel}\"");
            Assert.Equal((expected.EffectiveColor, expected.StateLabel), (trace.RowColour, trace.RowLabel));
        }
    }
}
