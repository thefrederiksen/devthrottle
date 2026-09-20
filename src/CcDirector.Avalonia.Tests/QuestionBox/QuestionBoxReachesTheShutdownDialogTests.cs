using CcDirector.Avalonia.SmartRestart;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Avalonia.Tests.QuestionBox;

/// <summary>
/// THE TWO HALVES, JOINED. Phase 2 proved the Smart shutdown dialog reads
/// <see cref="Session.PendingInteraction"/> correctly, but had to set that property by reflection
/// because nothing in the product ever filled it - so the "Answer these first?" section could never
/// appear on a real Director. This is the test that shows it now can: a real transcript on disk, read
/// by the real detection, stamped on a real session, and <see cref="SmartShutdownSessionReader.Read"/>
/// reporting HasQuestionBoxOpen true for it. No reflection anywhere.
///
/// It lives OUTSIDE the SmartRestart namespace on purpose. The dialog and its reader are not this
/// phase's to change, and the phase's check is that the SmartRestart-filtered count does not move; a
/// test added under that name would move it and the check would stop meaning anything. What is proved
/// here is the new half reaching the old one, so it is named for that.
/// </summary>
public sealed class QuestionBoxReachesTheShutdownDialogTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "QuestionBoxToDialog_" + Guid.NewGuid().ToString("N"));

    public QuestionBoxReachesTheShutdownDialogTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
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

    private Session SessionOn(string name, params string[] transcriptLines)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, transcriptLines);

        var session = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\repo",
            workingDirectory: @"C:\test\repo",
            claudeArgs: null,
            backend: new SilentBackend(),
            claudeSessionId: null,
            activityState: ActivityState.WaitingForInput,
            createdAt: DateTimeOffset.UtcNow,
            customName: name,
            customColor: null);
        session.UpdateClaudeSessionPointer(null, path, "test");
        return session;
    }

    [Fact]
    public void ASessionHoldingAQuestion_IsShownToTheDialogAsHavingOneAndAnAnsweredOneIsNot()
    {
        using var asking = SessionOn("asking", AsksWhichBranch);
        using var answered = SessionOn("answered", AsksWhichBranch, WhichBranchAnswered);

        // The product's own path: read the transcript, stamp what it says on the session.
        PendingInteractionWatcher.Refresh(asking, asking.ActivityGeneration);
        PendingInteractionWatcher.Refresh(answered, answered.ActivityGeneration);

        var described = SmartShutdownSessionReader.Read([asking, answered]);

        Assert.True(described.Single(s => s.DisplayName == "asking").HasQuestionBoxOpen);
        Assert.False(described.Single(s => s.DisplayName == "answered").HasQuestionBoxOpen);
    }

    private sealed class SilentBackend : ISessionBackend
    {
        public CircularTerminalBuffer? Buffer => null;
        public int ProcessId => 1;
        public string Status => "Silent";
        public bool IsRunning => true;
        public bool HasExited => false;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows,
            Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
