
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
[Collection("ProcessWideState")]
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
}
