using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Unit tests for the records-only producers (issue #1637): the model and the cumulative token spend,
/// both read from the tool's own records at turn-end. Covers <see cref="Session.SetCurrentModel"/> and
/// <see cref="Session.SetTokenTotals"/> semantics (a missed read never erases the last known value),
/// <see cref="SessionRecordsWatcher.RefreshModel"/> / <see cref="SessionRecordsWatcher.RefreshTokens"/>
/// stamping from the driver, and the capability gating that keeps a driver from being asked a question it
/// cannot answer (no NotSupportedException as control flow). The token PARSING itself lives in
/// SessionTokenUsageTests; these cover the wire.
/// </summary>
[Collection("ProcessWideState")]
public sealed class SessionRecordsWatcherTests
{
    [Fact]
    public void SetCurrentModel_TrimsAndLogsOnce_IgnoresNullAndBlank()
    {
        using var session = NewSession(claudeArgs: null);

        Assert.Null(session.CurrentModel);

        session.SetCurrentModel("  claude-fable-5  ");
        Assert.Equal("claude-fable-5", session.CurrentModel);

        // A missed read (null/blank) is NOT evidence the session lost its model - the value stands.
        session.SetCurrentModel(null);
        Assert.Equal("claude-fable-5", session.CurrentModel);
        session.SetCurrentModel("   ");
        Assert.Equal("claude-fable-5", session.CurrentModel);

        // A real switch replaces it.
        session.SetCurrentModel("claude-opus-4-8");
        Assert.Equal("claude-opus-4-8", session.CurrentModel);
    }

    [Fact]
    public void RefreshModel_ClaudeNoTurnYet_NeverStampsTheLaunchAlias()
    {
        // A brand-new Claude session launched with an explicit --model: no transcript exists (random
        // ids), and the launch value is an ALIAS (opus[1m]) whose concrete transcript id differs
        // (claude-opus-4-8) - two names for one model that would split a statistics fold. The
        // producer is records-only: it must stamp NOTHING here, not the alias, so an alias can never
        // accompany a non-zero input-stats delta (the bucket increments at submission, before any
        // turn-end stamp - the gateway-sqlite Architect's review finding on #1651).
        using var session = NewSession(claudeArgs: "--dangerously-skip-permissions --model opus[1m]");

        SessionRecordsWatcher.RefreshModel(session);

        Assert.Null(session.CurrentModel);
    }

    [Fact]
    public void RefreshModel_ClaudeNoTurnNoLaunchModel_StampsNothing()
    {
        using var session = NewSession(claudeArgs: "--dangerously-skip-permissions");

        SessionRecordsWatcher.RefreshModel(session);

        Assert.Null(session.CurrentModel);
    }

    [Fact]
    public void RefreshModel_AgentWithoutModelReport_DoesNotThrowAndStampsNothing()
    {
        // Gemini's driver does not declare ModelReport; the refresh is a caught no-op, never a crash.
        using var session = NewSession(claudeArgs: "--model gemini-2.5-pro");
        session.AgentKind = AgentKind.Gemini;

        SessionRecordsWatcher.RefreshModel(session);

        Assert.Null(session.CurrentModel);
    }

    [Fact]
    public void SetTokenTotals_IgnoresNull_KeepsLastKnown()
    {
        using var session = NewSession(claudeArgs: null);

        Assert.Null(session.TokenTotals);

        session.SetTokenTotals(new Gateway.Contracts.TokenTotalsDto
        {
            InputTokens = 100, OutputTokens = 50, CacheReadTokens = 900, CacheCreationTokens = 40,
            ContextTokens = 1200,
        });
        Assert.Equal(100, session.TokenTotals!.InputTokens);
        Assert.Equal(50, session.TokenTotals!.OutputTokens);

        // A missed read (torn records, agent restarting) is NOT evidence the spend vanished - the last
        // known totals stand, the same discipline as SetCurrentModel.
        session.SetTokenTotals(null);
        Assert.Equal(100, session.TokenTotals!.InputTokens);

        // A fresh read replaces the whole snapshot.
        session.SetTokenTotals(new Gateway.Contracts.TokenTotalsDto { InputTokens = 130, OutputTokens = 70 });
        Assert.Equal(130, session.TokenTotals!.InputTokens);
        Assert.Equal(70, session.TokenTotals!.OutputTokens);
    }

    [Fact]
    public void RefreshTokens_ClaudeNoTranscript_StampsNothing()
    {
        // A Claude session over a random repo path: no transcript exists, so ReadUsage finds no usage
        // and the stamp is a no-op. Not zero, not a crash - null, honestly "not read yet".
        using var session = NewSession(claudeArgs: "--dangerously-skip-permissions");

        SessionRecordsWatcher.RefreshTokens(session);

        Assert.Null(session.TokenTotals);
    }

    [Fact]
    public void RefreshFromRecords_Claude_RefreshesBothWithoutThrow()
    {
        // The combined turn-end path. Over a random repo neither fact can be read, so both stay null -
        // but the point is that asking for both in one handler does not throw and one fact's absence does
        // not suppress the other.
        using var session = NewSession(claudeArgs: "--dangerously-skip-permissions --model opus[1m]");

        SessionRecordsWatcher.RefreshFromRecords(session);

        Assert.Null(session.CurrentModel);
        Assert.Null(session.TokenTotals);
    }

    [Fact]
    public void RefreshFromRecords_AgentWithoutTokenUsage_NeverAsksForTokens_AndDoesNotThrow()
    {
        // Codex declares ContextUsage (window occupancy) but NOT TokenUsage (cumulative spend), and its
        // ReadUsage is a throw. RefreshFromRecords must gate on the capability and never call it, so the
        // throw is never reached as control flow. TokenTotals stays null and nothing crashes.
        using var session = NewSession(claudeArgs: null);
        session.AgentKind = AgentKind.Codex;

        Assert.False(session.Driver.Capabilities.HasFlag(DriverCapabilities.TokenUsage));

        SessionRecordsWatcher.RefreshFromRecords(session);

        Assert.Null(session.TokenTotals);
    }

    [Fact]
    public void TokenUsageCapability_IsSpendNotOccupancy_ClaudeHasItCodexDoesNot()
    {
        // The distinction the whole design turns on: cumulative SPEND (summable, governance) versus
        // context OCCUPANCY (a point-in-time gauge). Claude reports spend and declares TokenUsage; Codex
        // reports only occupancy, so it declares ContextUsage and NOT TokenUsage. Gating token reads on
        // TokenUsage is what keeps occupancy out of a spend total.
        using var claude = NewSession(claudeArgs: null);
        Assert.True(claude.Driver.Capabilities.HasFlag(DriverCapabilities.TokenUsage));

        using var codex = NewSession(claudeArgs: null);
        codex.AgentKind = AgentKind.Codex;
        Assert.True(codex.Driver.Capabilities.HasFlag(DriverCapabilities.ContextUsage));
        Assert.False(codex.Driver.Capabilities.HasFlag(DriverCapabilities.TokenUsage));
    }

    /// <summary>A session over a random (nonexistent) repo path so no real transcript can match.</summary>
    private static Session NewSession(string? claudeArgs)
    {
        var repo = Path.Combine(Path.GetTempPath(), "model-watcher-" + Guid.NewGuid().ToString("N"));
        return new Session(
            Guid.NewGuid(), repoPath: repo, workingDirectory: repo, claudeArgs: claudeArgs,
            backend: new NullBackend(), claudeSessionId: Guid.NewGuid().ToString(),
            activityState: ActivityState.WaitingForInput,
            createdAt: DateTimeOffset.UtcNow, customName: null, customColor: null);
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
