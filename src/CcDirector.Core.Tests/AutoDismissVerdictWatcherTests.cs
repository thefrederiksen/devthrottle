using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Unit tests for <see cref="AutoDismissVerdictWatcher"/> (issue #1200): reading an auto-dismiss session's
/// assistant text from the transcript (via an injected reader, no disk), parsing the CC-DISMISS verdict, and
/// stamping <see cref="Session.DismissVerdict"/>. The conservative rule is verified: no block leaves the
/// verdict null, so the Gateway never auto-closes without an explicit "done".
/// </summary>
[Collection("ProcessWideState")]
public sealed class AutoDismissVerdictWatcherTests
{
    [Fact]
    public void ScanAndStamp_DoneBlockInFinalMessage_StampsDone()
    {
        using var session = NewAutoDismissSession();
        var reader = new FakeReader(TextWidget("All quiet.\n\nCC-DISMISS\nverdict: done\nreason: nothing to act on\n"));

        new AutoDismissVerdictWatcher(reader).ScanAndStamp(session);

        Assert.Equal("done", session.DismissVerdict);
    }

    [Fact]
    public void ScanAndStamp_NeedsHumanBlock_StampsNeedsHuman()
    {
        using var session = NewAutoDismissSession();
        var reader = new FakeReader(TextWidget("CC-DISMISS\nverdict: needs-human\nreason: a report to file\n"));

        new AutoDismissVerdictWatcher(reader).ScanAndStamp(session);

        Assert.Equal("needs-human", session.DismissVerdict);
    }

    [Fact]
    public void ScanAndStamp_NoBlock_LeavesVerdictNull()
    {
        using var session = NewAutoDismissSession();
        var reader = new FakeReader(TextWidget("Just a normal reply with no sentinel."));

        new AutoDismissVerdictWatcher(reader).ScanAndStamp(session);

        Assert.Null(session.DismissVerdict);
    }

    [Fact]
    public void ScanAndStamp_VerdictAcrossTurns_LastWins()
    {
        using var session = NewAutoDismissSession();
        // Two turns: an early "done" then a later "needs-human". The concatenated scan must land on the last.
        var reader = new FakeReader(
            TextWidget("CC-DISMISS\nverdict: done\nreason: first pass\n"),
            TextWidget("CC-DISMISS\nverdict: needs-human\nreason: found something\n"));

        new AutoDismissVerdictWatcher(reader).ScanAndStamp(session);

        Assert.Equal("needs-human", session.DismissVerdict);
    }

    [Fact]
    public void ReadAssistantText_NoClaudeSessionId_ReturnsNull()
    {
        // A non-Claude / not-yet-hooked session has no transcript; the watcher must not throw and must not stamp.
        using var session = new Session(
            Guid.NewGuid(), repoPath: @"C:\repo", workingDirectory: @"C:\repo", claudeArgs: null,
            backend: new NullBackend(), claudeSessionId: null, activityState: ActivityState.WaitingForInput,
            createdAt: DateTimeOffset.UtcNow, customName: null, customColor: null) { AutoDismiss = true };
        var reader = new FakeReader(TextWidget("CC-DISMISS\nverdict: done\n"));

        Assert.Null(new AutoDismissVerdictWatcher(reader).ReadAssistantText(session));
    }

    private static Session NewAutoDismissSession() =>
        new Session(
            Guid.NewGuid(), repoPath: @"C:\repo", workingDirectory: @"C:\repo", claudeArgs: null,
            backend: new NullBackend(), claudeSessionId: "cs-1", activityState: ActivityState.WaitingForInput,
            createdAt: DateTimeOffset.UtcNow, customName: null, customColor: null)
        { AutoDismiss = true };

    private static TurnWidgetDto TextWidget(string content) => new() { Kind = "Text", Content = content };

    /// <summary>An <see cref="ITranscriptReader"/> that returns a fixed widget list, no disk access.</summary>
    private sealed class FakeReader : ITranscriptReader
    {
        private readonly List<TurnWidgetDto> _widgets;
        public FakeReader(params TurnWidgetDto[] widgets) => _widgets = widgets.ToList();
        public List<TurnWidgetDto> ReadWidgets(string claudeSessionId, string repoPath) => _widgets;
        public SessionUsageDto? ReadUsage(string claudeSessionId, string repoPath) => null;
        public List<(string ClaudeSessionId, DateTime LastWriteUtc)> ListTranscripts(string repoPath) => new();
        public DateTime? LastCompactionUtc(string claudeSessionId, string repoPath) => null;
    }

    private sealed class NullBackend : ISessionBackend
    {
        public CircularTerminalBuffer? Buffer => null;
        public int ProcessId => 1;
        public string Status => "Null";
        public bool IsRunning => true;
        public bool HasExited => false;

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
}
