
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// The watcher that stamps what the transcript says onto the session, and the clearing that keeps it
/// from going stale.
///
/// THESE DRIVE THE REAL TRIGGER. The session is wired to a real
/// <see cref="PendingInteractionWatcher"/> and then flipped through a real activity-state change, so
/// what is under test is the handler the Director actually runs - not a refresh called by hand, which
/// would stay green if the subscription were deleted.
/// </summary>
public sealed class PendingInteractionWatcherTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "PendingInteractionWatcher_" + Guid.NewGuid().ToString("N"));
    private readonly SessionManager _manager = new(new AgentOptions { ClaudePath = TestShell.Path });

    public PendingInteractionWatcherTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a temp directory that will not go is not a test failure */ }
    }

    private const string AsksWhichBranch =
        """
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_01Branch","name":"AskUserQuestion","input":{"questions":[{"question":"Which branch should I cut the worktree from?","header":"Branch","multiSelect":false,"options":[{"label":"origin/main","description":"the trunk"}]}]}}]},"uuid":"33333333-3333-4333-8333-333333333333","timestamp":"2026-09-20T10:00:02.000Z"}
        """;

    private const string WhichBranchAnswered =
        """
        {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_01Branch","content":"origin/main"}]},"uuid":"44444444-4444-4444-8444-444444444444","timestamp":"2026-09-20T10:01:00.000Z"}
        """;

    private string WriteTranscript(params string[] lines)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>A Claude Code session mid-turn, pointed at a transcript on disk.</summary>
    private static Session WorkingSessionOn(string transcriptPath)
    {
        var session = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\repo",
            workingDirectory: @"C:\test\repo",
            claudeArgs: null,
            backend: new QuietBackend(),
            claudeSessionId: null,
            activityState: ActivityState.Working,
            createdAt: DateTimeOffset.UtcNow,
            customName: "asking",
            customColor: null);
        session.UpdateClaudeSessionPointer(null, transcriptPath, "test");
        return session;
    }

    /// <summary>The stamp happens off the event thread, so the assertion waits for it rather than
    /// racing it. A failure here is the absence of the stamp, not a slow machine.</summary>
    private static async Task<PendingInteraction?> WaitForStamp(Session session, bool expectSet)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (session.PendingInteraction is not null == expectSet) return session.PendingInteraction;
            await Task.Delay(50);
        }
        return session.PendingInteraction;
    }

    /// <summary>
    /// The end-to-end shape: a session running Claude Code finishes a turn while an AskUserQuestion call
    /// is outstanding, and the watcher's own handler puts the question on the session.
    /// </summary>
    [Fact]
    public async Task TurnEnd_WithAQuestionOutstanding_StampsTheQuestionOnTheSession()
    {
        using var watcher = new PendingInteractionWatcher(_manager);
        using var session = WorkingSessionOn(WriteTranscript(AsksWhichBranch));
        watcher.WireSession(session);

        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        var stamped = await WaitForStamp(session, expectSet: true);
        Assert.NotNull(stamped);
        Assert.Equal(PendingInteractionKind.Question, stamped!.Kind);
        Assert.Equal("Which branch should I cut the worktree from?", stamped.Prompt);
    }

    /// <summary>
    /// THE SESSION GOES BACK TO WORK AND THE FLAG GOES WITH IT. The user answered, the session is
    /// working again, and a question box left standing in the Smart shutdown dialog would send the owner
    /// hunting for a question nobody is asking.
    /// </summary>
    [Fact]
    public async Task SessionStartsWorkingAgain_TheQuestionGoesBackToNull()
    {
        using var watcher = new PendingInteractionWatcher(_manager);
        using var session = WorkingSessionOn(WriteTranscript(AsksWhichBranch));
        watcher.WireSession(session);

        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        Assert.NotNull(await WaitForStamp(session, expectSet: true));

        session.ApplyTerminalActivityState(ActivityState.Working);

        Assert.Null(session.PendingInteraction);
    }

    /// <summary>
    /// The other way it clears: the session settles again and the transcript now shows the result, so
    /// the next turn end writes null. This covers the path that never passes through Working.
    /// </summary>
    [Fact]
    public async Task TurnEnd_OnceTheAnswerIsInTheTranscript_ClearsTheQuestion()
    {
        using var watcher = new PendingInteractionWatcher(_manager);
        var transcript = WriteTranscript(AsksWhichBranch);
        using var session = WorkingSessionOn(transcript);
        watcher.WireSession(session);

        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        Assert.NotNull(await WaitForStamp(session, expectSet: true));

        // The user answered: Claude Code appends the tool result. The session parks again without ever
        // being seen to work (a WaitingForPerm hop keeps Session's own clearing out of this test, so the
        // only thing that can clear it is the watcher's next read).
        File.AppendAllLines(transcript, [WhichBranchAnswered]);
        session.ApplyTerminalActivityState(ActivityState.WaitingForPerm);
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        Assert.Null(await WaitForStamp(session, expectSet: false));
    }

    /// <summary>
    /// A transcript that cannot be read leaves whatever was there alone. Erasing a real question because
    /// a file was momentarily unreadable would be the worst of both: no flag, and no sign that anything
    /// went wrong.
    /// </summary>
    [Fact]
    public void Refresh_WhenTheTranscriptCannotBeRead_LeavesThePropertyAsItWas()
    {
        using var session = WorkingSessionOn(Path.Combine(_directory, "never-written.jsonl"));
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        session.SetPendingInteraction(new PendingInteraction
        {
            Kind = PendingInteractionKind.Question,
            CreatedAt = DateTimeOffset.UtcNow,
            Prompt = "Which branch should I cut the worktree from?",
        });

        PendingInteractionWatcher.Refresh(session, session.ActivityGeneration);

        Assert.NotNull(session.PendingInteraction);
        Assert.Equal("Which branch should I cut the worktree from?", session.PendingInteraction!.Prompt);
    }

    /// <summary>
    /// The read takes real time, and in that time the user can answer and the session can go back to
    /// work. An answer about a moment that has passed is dropped rather than stamped - otherwise the
    /// clearing above would be undone by a read that started before it.
    /// </summary>
    [Fact]
    public void Refresh_WhenTheSessionHasMovedOnSinceTheRead_DropsTheReading()
    {
        using var session = WorkingSessionOn(WriteTranscript(AsksWhichBranch));
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        var staleGeneration = session.ActivityGeneration;

        session.ApplyTerminalActivityState(ActivityState.Working);
        PendingInteractionWatcher.Refresh(session, staleGeneration);

        Assert.Null(session.PendingInteraction);
    }

    /// <summary>
    /// A session that is ALREADY parked when it is wired - a restored session after a Director restart,
    /// which is the case this mission exists for - is read once at wire-up. Without it a session waiting
    /// on a question would carry nothing, because it will not end another turn until someone answers.
    /// </summary>
    [Fact]
    public async Task WiringASessionAlreadyParkedOnAQuestion_StampsItImmediately()
    {
        using var watcher = new PendingInteractionWatcher(_manager);
        using var session = WorkingSessionOn(WriteTranscript(AsksWhichBranch));
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        watcher.WireSession(session);

        Assert.NotNull(await WaitForStamp(session, expectSet: true));
    }
}
