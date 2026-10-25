using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Issue #2167 - the desktop Compact button.
///
/// It sits beside a context gauge that turns red, which is exactly when somebody reaches for a button.
/// Before this the only thing offered there was Clear context, which throws the conversation away - the
/// wrong answer to "my context is full", and an easy wrong click.
///
/// The tests that matter are the ones about what is NOT sent: no continuation to a driver that cannot time
/// one, and nothing at all when the confirmation is declined.
/// </summary>
public class CompactButtonTests
{
    [AvaloniaFact]
    public async Task DecliningTheConfirmation_CompactsNothing()
    {
        var driver = new RecordingDriver();
        using var session = NewSession(driver);
        var bar = NewBar(session);
        bar.ConfirmOverride = (_, _) => Task.FromResult(false);

        await bar.CompactContextWithConfirmationAsync();

        Assert.Equal(0, driver.CompactCalls);
    }

    /// <summary>
    /// The button compacts and stops there. A person clicking it has a composer in front of them and can
    /// say what happens next; putting words into their session unasked is not the button's business.
    /// Compact-AND-CONTINUE is a separate verb on the command line, for an agent rescuing a stuck session
    /// with nobody at its keyboard.
    /// </summary>
    [AvaloniaFact]
    public async Task ConfirmingIt_CompactsAndSendsTheSessionNothing()
    {
        var driver = new RecordingDriver();
        using var session = NewSession(driver);
        var bar = NewBar(session);
        bar.ConfirmOverride = (_, _) => Task.FromResult(true);

        await bar.CompactContextWithConfirmationAsync();

        Assert.Equal(1, driver.CompactCalls);
        // The session types the driver's compaction command (issue #4380) - and nothing after it.
        Assert.Equal(["/compact"], ((RecordingBackend)session.Backend).SentTexts);
    }

    /// <summary>Still nothing sent, even for a driver that COULD time a follow-up.</summary>
    [AvaloniaFact]
    public async Task ADriverThatCanTimeAFollowUp_IsStillSentNothing()
    {
        var driver = new RecordingDriver { CanReportCompletion = true };
        using var session = NewSession(driver);
        var bar = NewBar(session);
        bar.ConfirmOverride = (_, _) => Task.FromResult(true);

        await bar.CompactContextWithConfirmationAsync();

        Assert.Equal(1, driver.CompactCalls);
        // The session types the driver's compaction command (issue #4380) - and nothing after it.
        Assert.Equal(["/compact"], ((RecordingBackend)session.Backend).SentTexts);
    }

    /// <summary>Compaction must never be substituted with a clear - they differ by one word and by
    /// everything that matters.</summary>
    [AvaloniaFact]
    public async Task Compacting_NeverClears()
    {
        var driver = new RecordingDriver();
        using var session = NewSession(driver);
        var bar = NewBar(session);
        bar.ConfirmOverride = (_, _) => Task.FromResult(true);

        await bar.CompactContextWithConfirmationAsync();

        Assert.Equal(0, driver.ClearCalls);
    }

    [AvaloniaFact]
    public void TheButtonFollowsTheDeclaredCapability()
    {
        using var withCompaction = NewSession(new RecordingDriver());
        var bar = NewBar(withCompaction);
        Assert.True(bar.CompactButtonVisible);

        using var without = NewSession(new RecordingDriver { CanCompact = false });
        var bar2 = NewBar(without);
        Assert.False(bar2.CompactButtonVisible);
    }

    /// <summary>
    /// The two confirmations must not read alike. Clearing destroys the conversation and says so;
    /// compaction preserves it and must not imply loss, or people learn one reflex for two opposite
    /// outcomes - and the one they learn to click through is the destructive one.
    /// </summary>
    [Fact]
    public void TheCompactionQuestionDoesNotReadLikeTheClearQuestion()
    {
        Assert.NotEqual(SessionActionBar.ClearContextConfirmTitle, SessionActionBar.CompactContextConfirmTitle);
        Assert.Contains("cannot be undone", SessionActionBar.ClearContextConfirmMessage);
        Assert.DoesNotContain("cannot be undone", SessionActionBar.CompactContextConfirmMessage);
        Assert.DoesNotContain("loses", SessionActionBar.CompactContextConfirmMessage);
        Assert.Contains("keeps what it has learned", SessionActionBar.CompactContextConfirmMessage);
        // The dialog promises the session is left alone; the button must not then send it something.
        Assert.Contains("nothing is sent to it", SessionActionBar.CompactContextConfirmMessage);
    }

    private static SessionActionBar NewBar(Session session)
    {
        var bar = new SessionActionBar();
        bar.Configure(sessionManager: null!, session);
        return bar;
    }

    private static Session NewSession(IAgentDriver driver)
    {
        var session = new Session(
            Guid.NewGuid(), repoPath: @"C:\repo", workingDirectory: @"C:\repo", claudeArgs: null,
            backend: new RecordingBackend(), claudeSessionId: "agent-1",
            activityState: ActivityState.WaitingForInput, createdAt: DateTimeOffset.UtcNow,
            customName: null, customColor: null);
        session.DriverOverride = driver;
        return session;
    }

    /// <summary>A driver that compacts instantly and records what it was asked to do.</summary>
    private sealed class RecordingDriver : IAgentDriver
    {
        public bool CanCompact { get; init; } = true;
        public bool CanReportCompletion { get; init; } = true;
        public int CompactCalls { get; private set; }
        public int ClearCalls { get; private set; }

        public AgentKind Kind => AgentKind.ClaudeCode;

        public DriverCapabilities Capabilities =>
            DriverCapabilities.ClearContext
            | (CanCompact ? DriverCapabilities.CompactContext : DriverCapabilities.None)
            | (CanCompact && CanReportCompletion ? DriverCapabilities.CompactCompletionReport : DriverCapabilities.None);

        public IReadOnlyList<AgentSlashCommand> SlashCommands => [];
        public string ModelFlag => "";
        public IReadOnlyList<AgentModelOption> KnownModels => [];
        public string? ReadConfiguredDefaultModel() => null;
        public string ResolveExecutable(string? configuredPath) => throw new NotSupportedException();
        public AgentLaunchSpec BuildLaunchSpec(string? baseArgs, string? resumeSessionId) => throw new NotSupportedException();
        public Task SubmitAsync(ISessionBackend backend, string text) => backend.SendTextAsync(text);
        public DriverInput Cancel() => DriverInput.PressKeys(DriverKey.Escape);
        public DriverInput Interrupt() => DriverInput.PressKeys(DriverKey.CtrlC);
        public DriverInput ShowHistory() => DriverInput.PressKeys(DriverKey.Escape);

        public DriverInput ClearContext()
        {
            ClearCalls++;
            return DriverInput.TypeCommand("/clear");
        }

        public DriverInput CompactContext()
        {
            if (!CanCompact) throw new NotSupportedException("stub declares no compaction");
            CompactCalls++;
            return DriverInput.TypeCommand("/compact");
        }

        // Reports the compaction as finished on the first look, so the button's wait resolves at once.
        public bool HasCompactedSince(string agentSessionId, string workingDirectory, DateTime sinceUtc)
        {
            if (!CanReportCompletion) throw new NotSupportedException("stub cannot report completion");
            return true;
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
