using CcDirector.Core.Activity;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Activity;

/// <summary>
/// The shadow producer's contract (docs/PLAN-trustworthy-working-start-2026-07-24.md, increment 2): every
/// submission and every authoritative transition lands in the outbox with an honest shadow cause, and
/// nothing the producer does changes the session's behavior. Sessions are built directly over the
/// recording backend (the SessionInteractiveTests harness) and wired by hand - the manager fan-out is the
/// same Wire call.
/// </summary>
[Collection("ProcessWideState")]
public sealed class ActivityEventProducerTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-activity-producer-tests-" + Guid.NewGuid().ToString("N"));

    private readonly SessionManager _manager;
    private readonly ActivityEventOutbox _outbox;
    private readonly ActivityEventProducer _producer;

    public ActivityEventProducerTests()
    {
        _manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path })
        {
            DirectorId = "dir-test",
        };
        _outbox = new ActivityEventOutbox(Path.Combine(_dir, "outbox.jsonl"));
        _producer = new ActivityEventProducer(_manager, _outbox);
    }

    public void Dispose()
    {
        _producer.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private Session NewWiredSession(ActivityState initial = ActivityState.WaitingForInput)
    {
        var s = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\repo",
            workingDirectory: @"C:\test\repo",
            claudeArgs: null,
            backend: new FakeBackend(),
            claudeSessionId: "claude-test",
            activityState: initial,
            createdAt: DateTimeOffset.UtcNow,
            customName: null,
            customColor: null);
        s.MarkRunning();
        _producer.Wire(s);
        return s;
    }

    private IReadOnlyList<ActivityEventRecord> Events() => _outbox.PendingBatch(100);

    [Fact]
    public async Task An_owner_submission_records_the_turn_and_explains_the_working_transition()
    {
        using var s = NewWiredSession();

        await s.SendTextAsync("make the button blue", SendSource.UserInput, InputOrigin.DesktopTyped);

        var submitted = Assert.Single(Events(), e => e.EventType == ActivityEventTypes.TurnSubmitted);
        Assert.Equal(ActivityCauses.OwnerSubmit, submitted.Cause);
        Assert.Equal("UserInput", submitted.SendSource);
        Assert.Equal("typed/desktop", submitted.InputOrigin);
        Assert.Equal("dir-test", submitted.DirectorId);
        Assert.Equal(s.Id.ToString(), submitted.SessionId);

        var transition = Assert.Single(Events(), e => e.EventType == ActivityEventTypes.ActivityTransition
                                                      && e.NewState == nameof(ActivityState.Working));
        Assert.Equal(ActivityCauses.OwnerSubmit, transition.Cause);
        Assert.Equal(nameof(ActivityState.WaitingForInput), transition.PreviousState);
        Assert.Equal(ActivityEventProducer.DetectorVersion, transition.DetectorVersion);
    }

    [Fact]
    public async Task An_agent_submission_is_attributed_to_the_agent_not_the_owner()
    {
        using var s = NewWiredSession();

        await s.SendTextAsync("fleet message", SendSource.Agent);

        var submitted = Assert.Single(Events(), e => e.EventType == ActivityEventTypes.TurnSubmitted);
        Assert.Equal(ActivityCauses.AgentSubmit, submitted.Cause);

        var transition = Assert.Single(Events(), e => e.EventType == ActivityEventTypes.ActivityTransition);
        Assert.Equal(ActivityCauses.AgentSubmit, transition.Cause);
    }

    [Fact]
    public void A_raw_submit_byte_with_no_origin_is_an_honestly_unknown_submitter()
    {
        using var s = NewWiredSession();

        s.SendInput(new[] { (byte)'x', (byte)0x0D });

        var submitted = Assert.Single(Events(), e => e.EventType == ActivityEventTypes.TurnSubmitted);
        Assert.Equal(ActivityCauses.Unknown, submitted.Cause);
        Assert.Null(submitted.SendSource);
        Assert.Null(submitted.InputOrigin);
    }

    [Fact]
    public void A_working_flip_with_no_submission_at_all_is_not_credited_to_one()
    {
        using var s = NewWiredSession();

        // The detector's write path, with no submission anywhere near it - the phantom-turn shape.
        s.ApplyTerminalActivityState(ActivityState.Working);

        var transition = Assert.Single(Events(), e => e.EventType == ActivityEventTypes.ActivityTransition);
        Assert.NotEqual(ActivityCauses.OwnerSubmit, transition.Cause);
        Assert.NotEqual(ActivityCauses.AgentSubmit, transition.Cause);
        Assert.NotEqual(ActivityCauses.FrameworkSubmit, transition.Cause);
    }

    [Fact]
    public void Settling_after_work_is_the_quiet_threshold()
    {
        using var s = NewWiredSession(ActivityState.Working);

        s.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        var transition = Assert.Single(Events(), e => e.EventType == ActivityEventTypes.ActivityTransition);
        Assert.Equal(ActivityCauses.QuietThreshold, transition.Cause);
    }

    [Fact]
    public void Exit_is_recorded_as_its_own_event_type()
    {
        using var s = NewWiredSession(ActivityState.Working);

        s.ApplyTerminalActivityState(ActivityState.Exited);

        var exited = Assert.Single(Events(), e => e.EventType == ActivityEventTypes.SessionExited);
        Assert.Equal(ActivityCauses.SessionExit, exited.Cause);
    }

    [Fact]
    public void Terminal_evidence_rides_the_output_while_settled_event()
    {
        using var s = NewWiredSession();

        _producer.RecordTerminalOutputWhileSettled(s, outputByteCount: 96,
            beforeScreenHash: "aaa", afterScreenHash: "bbb", boundedScreenDiff: "row 41: [watcher] tick",
            detectorMode: "byte");

        var evidence = Assert.Single(Events(), e => e.EventType == ActivityEventTypes.TerminalOutputWhileSettled);
        Assert.Equal(ActivityCauses.TerminalOutputOnly, evidence.Cause);
        Assert.Equal(96, evidence.OutputByteCount);
        Assert.Equal("byte", evidence.DetectorMode);
        Assert.Equal("row 41: [watcher] tick", evidence.BoundedScreenDiff);
    }

    [Fact]
    public void A_transcript_observation_replays_the_same_identity_on_re_detection()
    {
        using var s = NewWiredSession();
        var ts = DateTime.UtcNow;

        _producer.RecordTurnObserved(s, ts, contextId: "ctx-1", dedupKey: "scope|ts|the reply text");
        _producer.RecordTurnObserved(s, ts, contextId: "ctx-1", dedupKey: "scope|ts|the reply text");

        var observed = Events().Where(e => e.EventType == ActivityEventTypes.TurnObservedInTranscript).ToList();
        Assert.Equal(2, observed.Count);
        // Same content identity -> same event id: the Gateway stores one row and acknowledges the replay.
        Assert.Equal(observed[0].EventId, observed[1].EventId);
        Assert.Equal(ts, observed[0].OccurredUtc);
        Assert.Equal("ctx-1", observed[0].ContextId);
    }

    [Fact]
    public void An_unwired_session_records_nothing()
    {
        using var s = NewWiredSession();
        _producer.Unwire(s);

        s.ApplyTerminalActivityState(ActivityState.Working);

        Assert.Empty(Events());
    }

    /// <summary>A backend that accepts input and text without a process - enough for the choke points.</summary>
    private sealed class FakeBackend : ISessionBackend
    {
        public int ProcessId => 0;
        public string Status => "Fake";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;
#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067
        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(null, true, ActivityCauses.OwnerSubmit)]                  // tagged human origin
    [InlineData(SendSource.UserInput, false, ActivityCauses.OwnerSubmit)] // owner-driven source
    [InlineData(SendSource.Delivery, false, ActivityCauses.OwnerSubmit)]  // queued owner message
    [InlineData(SendSource.Agent, false, ActivityCauses.AgentSubmit)]
    [InlineData(SendSource.Framework, false, ActivityCauses.FrameworkSubmit)]
    [InlineData(null, false, ActivityCauses.Unknown)]                     // raw bytes, nobody tagged
    public void Submission_causes_mirror_the_owner_turn_rule(SendSource? source, bool withOrigin, string expected)
        => Assert.Equal(expected,
            ActivityEventProducer.SubmissionCause(source, withOrigin ? InputOrigin.DesktopTyped : null));
}
