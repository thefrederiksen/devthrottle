using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Sessions;
using CcDirector.Core.UnitTests.ApiMode;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

public sealed class SessionStateEventTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "state-event-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly Session _session;

    public SessionStateEventTests()
    {
        Directory.CreateDirectory(_directory);
        _session = new Session(Guid.NewGuid(), _directory, _directory, null, new FakeTerminalBackend(), SessionBackendType.ConPty)
        {
            AgentKind = AgentKind.ClaudeCode,
            Clock = _clock,
        };
    }

    public void Dispose()
    {
        _session.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private SessionStateEvent Event(long sequence, string eventName, Guid? eventId = null, string agent = "claude",
        DateTimeOffset? occurredAtUtc = null, string? detail = null, string? cliVersion = "2.1.302") =>
        new(1, eventId ?? Guid.NewGuid(), _session.Id, sequence, occurredAtUtc ?? _clock.GetUtcNow(), eventName,
            "agent", agent, detail, cliVersion);

    [Fact]
    public void ApplyAgentStateEvent_AgentDisagreesWithSilence_AgentWinsAndRecordsDisagreement()
    {
        Assert.True(_session.ApplyAgentStateEvent(Event(1, "turn_started")));

        _clock.Advance(TimeSpan.FromSeconds(15));
        _session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        Assert.Equal(ActivityState.Working, _session.ActivityState);
        Assert.Equal(SessionStateSources.Agent, _session.ActivityStateSource);
        var disagreement = Assert.Single(_session.StateDisagreements);
        Assert.Equal(ActivityState.WaitingForInput, disagreement.ReportedState);
        Assert.Equal(SessionStateSources.SilenceTimer, disagreement.ReportedSource);
        Assert.Equal(SessionStateSources.Agent, disagreement.SelectedSource);
    }

    [Fact]
    public void ApplyAgentStateEvent_PermissionThenWorkResumes_LaterWorkClearsPermission()
    {
        Assert.True(_session.ApplyAgentStateEvent(Event(1, "permission_requested")));
        Assert.Equal(ActivityState.WaitingForPerm, _session.ActivityState);

        _clock.Advance(TimeSpan.FromSeconds(1));
        _session.ApplyTerminalActivityState(ActivityState.Working);

        Assert.Equal(ActivityState.Working, _session.ActivityState);
        Assert.Equal(SessionStateSources.TerminalOutput, _session.ActivityStateSource);
        Assert.False(_session.ActivityStateIsFallback);
        Assert.Single(_session.StateDisagreements);
    }

    [Fact]
    public void ApplyAgentStateEvent_PermissionThenComposerReturns_LaterScreenClearsPermission()
    {
        Assert.True(_session.ApplyAgentStateEvent(Event(1, "permission_requested")));

        _clock.Advance(TimeSpan.FromSeconds(1));
        _session.ApplyScreenActivityState(ActivityState.WaitingForInput);

        Assert.Equal(ActivityState.WaitingForInput, _session.ActivityState);
        Assert.Equal(SessionStateSources.Screen, _session.ActivityStateSource);
        Assert.False(_session.ActivityStateIsFallback);
    }

    [Fact]
    public void ApplyAgentStateEvent_UnsupportedDiagnostic_LeavesStateUnchangedAndRetainsDiagnostic()
    {
        var before = _session.ActivityState;

        Assert.True(_session.ApplyAgentStateEvent(Event(1, "unsupported", detail: "Notification:mystery", cliVersion: "2.1.302")));

        Assert.Equal(before, _session.ActivityState);
        Assert.Equal(SessionStateSources.Initial, _session.ActivityStateSource);
        var diagnostic = Assert.Single(_session.StateDiagnostics);
        Assert.Equal("Notification:mystery", diagnostic.RawType);
        Assert.Equal("2.1.302", diagnostic.CliVersion);
        Assert.Equal("claude", diagnostic.Agent);
    }

    [Fact]
    public void ApplyAgentStateEvent_DuplicateEventIdWithHigherSequence_IsRejected()
    {
        var eventId = Guid.NewGuid();
        Assert.True(_session.ApplyAgentStateEvent(Event(1, "turn_started", eventId)));

        Assert.False(_session.ApplyAgentStateEvent(Event(2, "turn_ended", eventId)));
        Assert.Equal(ActivityState.Working, _session.ActivityState);
    }

    [Theory]
    [InlineData(-31)]
    [InlineData(6)]
    public void ApplyAgentStateEvent_OutsideTimestampWindow_IsRejected(int secondsFromNow)
    {
        var occurred = _clock.GetUtcNow().AddSeconds(secondsFromNow);

        Assert.False(_session.ApplyAgentStateEvent(Event(1, "turn_started", occurredAtUtc: occurred)));
        Assert.Equal(ActivityState.WaitingForInput, _session.ActivityState);
    }

    [Fact]
    public void TryParse_UnsupportedWithoutCommandLineVersion_IsRejected()
    {
        var json = $$"""{"version":1,"eventId":"{{Guid.NewGuid()}}","sessionId":"{{_session.Id}}","sequence":1,"occurredAtUtc":"2026-10-24T12:00:00Z","event":"unsupported","source":"agent","agent":"claude","detail":"mystery","cliVersion":null}""";

        Assert.False(SessionStateEventContract.TryParse(json, out _, out var error));
        Assert.Equal("unsupported cliVersion is required", error);
    }
}
