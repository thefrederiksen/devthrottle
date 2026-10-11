using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// Issue #2150 - compact AND CONTINUE, the part that actually rescues a stuck session.
///
/// The failure this prevents is specific. A session whose context window is full swallows everything
/// sent to it, so the follow-up prompt is only worth anything AFTER the compaction has finished. Timing
/// it on a sleep would fire it into a composer mid-summary and it would vanish exactly like every
/// message before it. So the follow-up is gated on the tool's own completion signal, a tool that cannot
/// give that signal is refused rather than guessed at, and a compaction that never reports finishing
/// fails loudly instead of quietly reporting success.
/// </summary>
[Collection("ProcessWideState")]
public sealed class SessionCompactContextTests
{
    private static readonly TimeSpan ShortWait = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(10);

    private static Session NewSession(StubDriver driver, string? agentSessionId = "agent-1")
    {
        var session = new Session(
            Guid.NewGuid(), repoPath: @"C:\repo", workingDirectory: @"C:\repo", claudeArgs: null,
            backend: new RecordingBackend(), claudeSessionId: agentSessionId,
            activityState: ActivityState.WaitingForInput, createdAt: DateTimeOffset.UtcNow,
            customName: null, customColor: null);
        session.DriverOverride = driver;
        return session;
    }

    private static RecordingBackend BackendOf(Session session) => (RecordingBackend)session.Backend;

    [Fact]
    public async Task CompactContextAsync_SubmitsTheCompactionCommand()
    {
        var driver = new StubDriver { CompactedAt = DateTime.UtcNow.AddSeconds(1) };
        using var session = NewSession(driver);

        await session.CompactContextAsync(continuePrompt: null, default, ShortWait, FastPoll);

        Assert.Equal(1, driver.CompactCalls);
    }

    /// <summary>The whole feature in one test: wait for the finish, THEN send the follow-up.</summary>
    [Fact]
    public async Task CompactContextAsync_WithAContinuation_WaitsForTheFinishThenSendsIt()
    {
        var driver = new StubDriver { CompletesAfterProbes = 3 };
        using var session = NewSession(driver);

        var outcome = await session.CompactContextAsync("continue", default, ShortWait, FastPoll);

        Assert.True(outcome.Submitted);
        Assert.True(outcome.CompactionObserved);
        Assert.True(outcome.Continued);
        Assert.Equal("continue", Assert.Single(BackendOf(session).SentTexts));
        Assert.True(driver.Probes >= 3, $"expected the wait to poll for the finish; probes={driver.Probes}");
    }

    /// <summary>
    /// The follow-up must not go out while the tool is still summarizing - that is the exact way the
    /// original message was lost. Nothing is sent on any probe before the completion signal appears.
    /// </summary>
    [Fact]
    public async Task CompactContextAsync_SendsNothingBeforeTheCompactionFinishes()
    {
        var driver = new StubDriver { CompletesAfterProbes = 4 };
        using var session = NewSession(driver);
        var backend = BackendOf(session);
        driver.OnProbe = () => Assert.Empty(backend.SentTexts);

        await session.CompactContextAsync("continue", default, ShortWait, FastPoll);

        Assert.Equal("continue", Assert.Single(backend.SentTexts));
    }

    [Fact]
    public async Task CompactContextAsync_WithoutAContinuation_SendsNothingAfterwards()
    {
        var driver = new StubDriver { CompletesAfterProbes = 1 };
        using var session = NewSession(driver);

        var outcome = await session.CompactContextAsync(continuePrompt: null, default, ShortWait, FastPoll);

        Assert.True(outcome.CompactionObserved);
        Assert.False(outcome.Continued);
        Assert.Empty(BackendOf(session).SentTexts);
    }

    /// <summary>Blank is not a follow-up. A caller passing whitespace gets a compaction, not an empty turn.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CompactContextAsync_TreatsBlankContinuationAsNoContinuation(string blank)
    {
        var driver = new StubDriver { CompletesAfterProbes = 1 };
        using var session = NewSession(driver);

        var outcome = await session.CompactContextAsync(blank, default, ShortWait, FastPoll);

        Assert.False(outcome.Continued);
        Assert.Empty(BackendOf(session).SentTexts);
    }

    /// <summary>
    /// A compaction that never reports finishing must FAIL, and say so. Returning a success outcome here
    /// would tell a rescuing agent the session is moving again when it is still wedged.
    /// </summary>
    [Fact]
    public async Task CompactContextAsync_ThrowsWhenTheCompactionNeverReportsFinishing()
    {
        var driver = new StubDriver { CompletesAfterProbes = int.MaxValue };
        using var session = NewSession(driver);

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => session.CompactContextAsync("continue", default, ShortWait, FastPoll));

        Assert.Contains("did not report a finished compaction", ex.Message);
        Assert.Empty(BackendOf(session).SentTexts);
    }

    /// <summary>
    /// A tool that can be told to compact but cannot report finishing is refused the CONTINUATION - and
    /// refused before anything is typed at it, so the caller can choose to compact plainly instead.
    /// </summary>
    [Fact]
    public async Task CompactContextAsync_RefusesAContinuationWhenTheDriverCannotReportCompletion()
    {
        var driver = new StubDriver { CanReportCompletion = false };
        using var session = NewSession(driver);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => session.CompactContextAsync("continue", default, ShortWait, FastPoll));

        Assert.Contains("cannot report when a compaction finished", ex.Message);
        Assert.Equal(0, driver.CompactCalls);
        Assert.Empty(BackendOf(session).SentTexts);
    }

    /// <summary>Without a continuation, that same tool still compacts - and the outcome says plainly that
    /// the finish was not watched rather than implying it was.</summary>
    [Fact]
    public async Task CompactContextAsync_CompactsWithoutWatchingWhenTheDriverCannotReportCompletion()
    {
        var driver = new StubDriver { CanReportCompletion = false };
        using var session = NewSession(driver);

        var outcome = await session.CompactContextAsync(continuePrompt: null, default, ShortWait, FastPoll);

        Assert.True(outcome.Submitted);
        Assert.False(outcome.CompactionObserved);
        Assert.False(outcome.Continued);
        Assert.Contains("cannot report when it finishes", outcome.Detail);
        Assert.Equal(1, driver.CompactCalls);
    }

    [Fact]
    public async Task CompactContextAsync_RefusesWhenTheDriverDeclaresNoCompaction()
    {
        var driver = new StubDriver { CanCompact = false };
        using var session = NewSession(driver);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => session.CompactContextAsync(continuePrompt: null, default, ShortWait, FastPoll));

        Assert.Contains("declares no compaction", ex.Message);
        Assert.Equal(0, driver.CompactCalls);
    }

    /// <summary>Before the first turn there is no conversation to watch, so a continuation cannot be timed.</summary>
    [Fact]
    public async Task CompactContextAsync_RefusesAContinuationBeforeTheSessionHasAnAgentSessionId()
    {
        var driver = new StubDriver();
        using var session = NewSession(driver, agentSessionId: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.CompactContextAsync("continue", default, ShortWait, FastPoll));

        Assert.Equal(0, driver.CompactCalls);
    }

    /// <summary>
    /// Compaction continues under the SAME agent session id. Re-pointing the session at a new transcript
    /// - which is right for a CLEAR - would here detach it from the conversation it just preserved.
    /// </summary>
    [Fact]
    public async Task CompactContextAsync_LeavesTheAgentSessionIdAlone()
    {
        var driver = new StubDriver { CompletesAfterProbes = 1 };
        using var session = NewSession(driver);

        await session.CompactContextAsync("continue", default, ShortWait, FastPoll);

        Assert.Equal("agent-1", session.ClaudeSessionId);
    }

    /// <summary>
    /// The follow-up is the product's own text, not the owner coming back. Counting it as an owner turn
    /// would silently end a hold the owner had set on the session being rescued.
    /// </summary>
    [Fact]
    public async Task CompactContextAsync_ContinuationDoesNotCountAsAnOwnerTurn()
    {
        var driver = new StubDriver { CompletesAfterProbes = 1 };
        using var session = NewSession(driver);
        var before = session.LastOwnerTurnAtUtc;

        await session.CompactContextAsync("continue", default, ShortWait, FastPoll);

        Assert.Equal(before, session.LastOwnerTurnAtUtc);
    }

    /// <summary>A driver that declares compaction is called for it - and never for a CLEAR instead.</summary>
    [Fact]
    public async Task CompactContextAsync_NeverClearsTheContext()
    {
        var driver = new StubDriver { CompletesAfterProbes = 1 };
        using var session = NewSession(driver);

        await session.CompactContextAsync("continue", default, ShortWait, FastPoll);

        Assert.Equal(0, driver.ClearCalls);
    }

    /// <summary>
    /// The Director's own wait must expire BEFORE the Gateway stops waiting for the Director, or the
    /// specific message ("the tool never reported a finished compaction") is masked by the generic one
    /// ("the Director did not answer"). The Gateway side asserts the other half of this ordering.
    /// </summary>
    [Fact]
    public void CompactionWaitTimeout_IsShorterThanThreeMinutes()
    {
        Assert.True(Session.CompactionWaitTimeout < TimeSpan.FromMinutes(3),
            "the Director's compaction wait must fire before the Gateway's wait for the Director");
    }

    // ===== Stubs =====

    /// <summary>A driver whose capabilities and completion signal the test dictates.</summary>
    private sealed class StubDriver : IAgentDriver
    {
        public bool CanCompact { get; init; } = true;
        public bool CanReportCompletion { get; init; } = true;

        /// <summary>Report the compaction finished once this many probes have been answered.</summary>
        public int CompletesAfterProbes { get; init; } = 1;

        /// <summary>A completion time to report immediately, bypassing the probe count.</summary>
        public DateTime? CompactedAt { get; init; }

        public Action? OnProbe { get; set; }

        public int CompactCalls { get; private set; }
        public int ClearCalls { get; private set; }
        public int Probes { get; private set; }

        public AgentKind Kind => AgentKind.ClaudeCode;

        public DriverCapabilities Capabilities =>
            (CanCompact ? DriverCapabilities.CompactContext : DriverCapabilities.None)
            | (CanReportCompletion ? DriverCapabilities.CompactCompletionReport : DriverCapabilities.None);

        public IReadOnlyList<AgentSlashCommand> SlashCommands => [];
        public string ModelFlag => "";
        public IReadOnlyList<AgentModelOption> KnownModels => [];
        public string? ReadConfiguredDefaultModel() => null;

        public string ResolveExecutable(string? configuredPath) => throw new NotSupportedException();
        public AgentLaunchSpec BuildLaunchSpec(string? baseArgs, string? resumeSessionId) => throw new NotSupportedException();
        public Task SubmitAsync(ISessionBackend backend, string text) => backend.SendTextAsync(text);
        public Task CancelAsync(ISessionBackend backend) => Task.CompletedTask;
        public Task InterruptAsync(ISessionBackend backend) => Task.CompletedTask;
        public Task ShowHistoryAsync(ISessionBackend backend) => Task.CompletedTask;

        public Task ClearContextAsync(ISessionBackend backend)
        {
            ClearCalls++;
            return Task.CompletedTask;
        }

        public Task CompactContextAsync(ISessionBackend backend)
        {
            if (!CanCompact)
                throw new NotSupportedException("stub declares no compaction");
            CompactCalls++;
            return Task.CompletedTask;
        }

        public bool HasCompactedSince(string agentSessionId, string workingDirectory, DateTime sinceUtc)
        {
            if (!CanReportCompletion)
                throw new NotSupportedException("stub cannot report completion");
            OnProbe?.Invoke();
            Probes++;
            if (CompactedAt is not null)
                return CompactedAt.Value >= sinceUtc;
            return Probes >= CompletesAfterProbes;
        }

        public List<TurnWidgetDto> ReadWidgets(string agentSessionId, string workingDirectory) => new();
        public SessionUsageDto? ReadUsage(string agentSessionId, string workingDirectory) => null;
        public List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workingDirectory) => new();
    }

    private sealed class RecordingBackend : ISessionBackend
    {
        public List<string> SentTexts { get; } = new();

        public int ProcessId => 1;
        public string Status => "Running";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067 // Required by the interface, never raised here.
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }

        public Task SendTextAsync(string text)
        {
            SentTexts.Add(text);
            return Task.CompletedTask;
        }

        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
