using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Tests for self-requested session teardown: a session flags ITSELF (or is flagged) for deletion
/// via the Control API, and the Director's deletion reaper removes it on its next sweep once a grace
/// window has elapsed AND it is not actively Working (option (a): never cut off a final in-flight turn).
/// The flag is set asynchronously to the removal, so the calling process is never yanked mid-request.
/// </summary>
[Collection("ProcessWideState")]
public sealed class SessionDeletionReaperTests
{
    private static Session NewSession(ExitableBackend backend)
    {
        var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            backend, SessionBackendType.ConPty);
        session.MarkRunning();
        return session;
    }

    /// <summary>
    /// THIS TEST WAS THE BUG (defect 23, rewritten 14 July 2026). It used to be called
    /// "MarkForDeletion_sets_the_flag_reason_and_a_winding_down_badge" and asserted
    /// <c>Assert.Equal(StatusColor.Unknown, session.StatusColor)</c> - i.e. it asserted that the
    /// DIRECTOR PAINTS A COLOUR, which law 2 forbids, and which nothing that paints has ever read (the
    /// Gateway is the single fold and reads the Director's cooked StatusColor for NOTHING). A green test
    /// is not proof: this one passed on every run while the behaviour it defended was a defect, and its
    /// name called a colour a "badge", which is how the confusion survived review.
    ///
    /// The rule now: MarkForDeletion RECORDS A FACT AND DECIDES NOTHING. Pending deletion is a badge,
    /// never a colour - the fact crosses the wire on SessionDto.PendingDeletion and the rail renders it
    /// beside the dot.
    /// </summary>
    [Fact]
    public void MarkForDeletion_records_the_fact_and_writes_no_colour()
    {
        using var backend = new ExitableBackend();
        using var session = NewSession(backend);
        session.ApplyTerminalActivityState(ActivityState.Idle);
        var colourBefore = session.StatusColor;
        var reasonBefore = session.LastStatusReason;

        session.MarkForDeletion("jobs-auto: nothing to report");

        Assert.True(session.PendingDeletion);
        Assert.NotNull(session.DeletionRequestedAt);
        Assert.Equal("jobs-auto: nothing to report", session.DeletionReason);
        // The Director reports the fact and decides nothing: flagging touches no colour.
        Assert.Equal(colourBefore, session.StatusColor);
        Assert.Equal(reasonBefore, session.LastStatusReason);
    }

    /// <summary>
    /// THE KEY TEST for defect 23 at the Director: a session flagged for deletion MAY STILL BE WORKING -
    /// the reaper explicitly waits out a running final turn (see
    /// <see cref="SessionManager.ReapPendingDeletions"/> and Reaper_leaves_a_working_session_alone below).
    /// Under the law that session is BLUE. Flagging it must not touch the colour, and it must not stop
    /// the wingman repainting it.
    ///
    /// The deleted <c>SetStatusColor(Unknown, ...)</c> call used StatusColorSource.PositiveEvidence,
    /// which is STICKY: within one activity generation it blocked the activity-state mapping from
    /// repainting the row (see Session.SetStatusColor). So a flagged session could not show blue for its
    /// own work until a genuine state change bumped the generation.
    /// </summary>
    [Fact]
    public void MarkForDeletion_leaves_a_working_session_blue()
    {
        using var backend = new ExitableBackend();
        using var session = NewSession(backend);
        session.ApplyTerminalActivityState(ActivityState.Working);
        session.SetStatusColor(StatusColor.Blue, "working");

        session.MarkForDeletion("jobs-auto: nothing to report");

        Assert.True(session.PendingDeletion);
        Assert.Equal(StatusColor.Blue, session.StatusColor);

        // And the flag left no sticky positive-evidence write behind: the wingman can still repaint.
        session.SetStatusColor(StatusColor.Blue, "still working");
        Assert.Equal(StatusColor.Blue, session.StatusColor);
    }

    /// <summary>The fact travels as a fact: flagging and cancelling each raise
    /// <see cref="Session.OnPendingDeletionChanged"/> so the rail can show/clear the badge. The rail used
    /// to learn about deletion only as a side effect of the (now deleted) colour write, so without this
    /// signal the badge would never appear.</summary>
    [Fact]
    public void MarkForDeletion_and_CancelDeletion_raise_the_fact_changed_event()
    {
        using var backend = new ExitableBackend();
        using var session = NewSession(backend);
        var observed = new List<bool>();
        session.OnPendingDeletionChanged += v => observed.Add(v);

        session.MarkForDeletion("done");
        session.MarkForDeletion("done again"); // idempotent: not a transition, must not re-fire
        session.CancelDeletion();

        Assert.Equal(new[] { true, false }, observed);
    }

    [Fact]
    public void MarkForDeletion_is_idempotent_and_keeps_the_original_request_time()
    {
        using var backend = new ExitableBackend();
        using var session = NewSession(backend);

        session.MarkForDeletion("first");
        var firstAt = session.DeletionRequestedAt;

        session.MarkForDeletion("second");

        Assert.Equal(firstAt, session.DeletionRequestedAt); // grace measured from the FIRST request
        Assert.Equal("second", session.DeletionReason);     // reason refreshed
    }

    [Fact]
    public void CancelDeletion_clears_the_flag()
    {
        using var backend = new ExitableBackend();
        using var session = NewSession(backend);
        session.MarkForDeletion("oops");

        session.CancelDeletion();

        Assert.False(session.PendingDeletion);
        Assert.Null(session.DeletionRequestedAt);
        Assert.Null(session.DeletionReason);
    }

    [Fact]
    public async Task Reaper_removes_a_flagged_idle_session_past_the_grace_window()
    {
        using var manager = new SessionManager(new AgentOptions()) { DeletionGraceMs = 0 };
        var backend = new ExitableBackend();
        var session = NewSession(backend);
        session.ApplyTerminalActivityState(ActivityState.Idle);

        Guid? removed = null;
        manager.OnSessionRemoved += s => removed = s.Id;
        manager.AdoptSession(session);

        session.MarkForDeletion("done");
        manager.ReapPendingDeletions();

        await WaitUntil(() => manager.GetSession(session.Id) is null);
        Assert.Equal(session.Id, removed);
    }

    [Fact]
    public void Reaper_leaves_a_flagged_session_within_the_grace_window()
    {
        using var manager = new SessionManager(new AgentOptions()) { DeletionGraceMs = 60_000 };
        var backend = new ExitableBackend();
        var session = NewSession(backend);
        session.ApplyTerminalActivityState(ActivityState.Idle);
        manager.AdoptSession(session);

        session.MarkForDeletion("done"); // just now; grace has not elapsed
        manager.ReapPendingDeletions();

        Assert.NotNull(manager.GetSession(session.Id)); // still here
    }

    [Fact]
    public async Task Reaper_waits_out_a_working_session_then_reaps_it_when_idle()
    {
        using var manager = new SessionManager(new AgentOptions()) { DeletionGraceMs = 0 };
        var backend = new ExitableBackend();
        var session = NewSession(backend);
        session.ApplyTerminalActivityState(ActivityState.Working); // mid final turn
        manager.AdoptSession(session);

        session.MarkForDeletion("done");
        manager.ReapPendingDeletions();
        Assert.NotNull(manager.GetSession(session.Id)); // option (a): not reaped while Working

        session.ApplyTerminalActivityState(ActivityState.Idle); // turn finished
        manager.ReapPendingDeletions();

        await WaitUntil(() => manager.GetSession(session.Id) is null);
    }

    [Fact]
    public async Task Reaper_spares_a_session_whose_deletion_was_cancelled()
    {
        using var manager = new SessionManager(new AgentOptions()) { DeletionGraceMs = 0 };
        var backend = new ExitableBackend();
        var session = NewSession(backend);
        session.ApplyTerminalActivityState(ActivityState.Idle);
        manager.AdoptSession(session);

        session.MarkForDeletion("done");
        session.CancelDeletion();
        manager.ReapPendingDeletions();

        await Task.Delay(100); // give any (incorrect) async reap a chance to run
        Assert.NotNull(manager.GetSession(session.Id));
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met within timeout");
    }

    /// <summary>An ISessionBackend whose process exit can be raised on demand.</summary>
    private sealed class ExitableBackend : ISessionBackend
    {
        public int ProcessId => 4321;
        public string Status => "Exitable";
        public bool IsRunning => !_exited;
        public bool HasExited => _exited;
        public CircularTerminalBuffer? Buffer => null;
        private bool _exited;

#pragma warning disable CS0067 // StatusChanged is required by the interface but unused here
        public event Action<string>? StatusChanged;
#pragma warning restore CS0067
        public event Action<int>? ProcessExited;

        public void RaiseExit(int code)
        {
            _exited = true;
            ProcessExited?.Invoke(code);
        }

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
