using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Rules;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Rules;

/// <summary>
/// Session Rules part 2, phase 1: the per-session rules file (W2) is MAINTAINED, the way the preamble
/// file is - fetched from the Gateway for the session's own machine and repo (W1), rewritten after every
/// store refresh, written when a session is created, and deleted when it goes.
///
/// The test that matters most is <see cref="RefreshAllAsync_RuleCreatedAfterLaunch_ReachesTheRunningSession"/>:
/// rules are created while sessions run, and a file written once at launch would hide every one of them
/// from every session already running - with nothing looking broken.
///
/// Everything here runs in memory: the Gateway is a fake, git is a fake, the clock is moved by hand.
/// </summary>
public sealed class SessionRulesMaintainerTests : IDisposable
{
    private const string Machine = "TEST-MACHINE";
    private const string NormalisedRepo = "github.com/thefrederiksen/devthrottle";

    private readonly string _dir;
    private readonly SessionManager _sessions;
    private readonly FakeSessionRuleGateway _gateway = new();
    private readonly FakeOriginGit _git = new();
    private readonly ManualClock _clock = new();

    public SessionRulesMaintainerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccd-rules-maint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _sessions = new SessionManager(new AgentOptions());
    }

    public void Dispose()
    {
        _sessions.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private Session Adopt(string checkout = "repo", AgentKind kind = AgentKind.ClaudeCode)
    {
        var folder = Path.Combine(_dir, checkout);
        var session = new Session(
            Guid.NewGuid(),
            repoPath: folder,
            workingDirectory: folder,
            claudeArgs: null,
            backend: new StubSessionBackend(),
            claudeSessionId: null,
            activityState: ActivityState.Idle,
            createdAt: DateTimeOffset.UtcNow,
            customName: "rules-maintainer-test",
            customColor: null);
        session.AgentKind = kind;
        _sessions.AdoptSession(session);
        return session;
    }

    private string Folder(string checkout) => Path.Combine(_dir, checkout);

    private SessionRulesMaintainer Maintainer(Func<ISessionRuleGateway?>? gateway = null)
        => new(_sessions, gateway ?? (() => _gateway), _git, _dir, Machine, () => _clock.UtcNow);

    private string RulesPath(Session session) => SessionHookFiles.RulesPathFor(session.Id, _dir);

    private JsonElement ReadRulesFile(Session session)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RulesPath(session)));
        return doc.RootElement.Clone();
    }

    private void AnswerFor(string repo, params HookRuleEntry[] rules)
        => _gateway.FetchAnswer = (_, asked) => string.Equals(asked, repo, StringComparison.Ordinal)
            ? EffectiveHookRulesFetch.Answered(rules)
            : EffectiveHookRulesFetch.Answered(Array.Empty<HookRuleEntry>());

    [Fact]
    public async Task RefreshAllAsync_SessionInARepo_AsksForItsMachineAndNormalisedRepoAndWritesTheAnswer()
    {
        // Arrange
        var session = Adopt();
        _git.Origin(Folder("repo"), "git@github.com:TheFrederiksen/devthrottle.git");
        AnswerFor(NormalisedRepo, RuleSamples.DenyEcho());
        var maintainer = Maintainer();

        // Act
        await maintainer.RefreshAllAsync();

        // Assert
        Assert.Contains((Machine, NormalisedRepo), _gateway.Fetches);
        var file = ReadRulesFile(session);
        Assert.Equal(1, file.GetProperty("version").GetInt32());
        Assert.Equal(session.Id.ToString(), file.GetProperty("sessionId").GetString());
        Assert.Equal(Machine, file.GetProperty("machine").GetString());
        Assert.Equal(NormalisedRepo, file.GetProperty("repo").GetString());
        var rule = Assert.Single(file.GetProperty("rules").EnumerateArray().ToList());
        Assert.Equal("3f2c0000-0000-0000-0000-000000000001", rule.GetProperty("id").GetString());
        Assert.Equal("echo RULE-PROBE", rule.GetProperty("match")[0].GetString());
    }

    [Fact]
    public async Task RefreshAllAsync_NoHookRulesReachTheSession_WritesAnEmptyListNotNoFile()
    {
        // Arrange
        var session = Adopt();
        _git.Origin(Folder("repo"), "https://github.com/thefrederiksen/devthrottle");
        var maintainer = Maintainer();

        // Act
        await maintainer.RefreshAllAsync();

        // Assert
        Assert.True(File.Exists(RulesPath(session)));
        Assert.Empty(ReadRulesFile(session).GetProperty("rules").EnumerateArray());
    }

    [Fact]
    public async Task RefreshAllAsync_CheckoutWithNoOrigin_AsksWithAnEmptyRepoAndSaysSoInTheFile()
    {
        // Arrange: the fake git answers exit 2, "No such remote 'origin'", for an unknown folder.
        var session = Adopt("no-origin");
        var maintainer = Maintainer();

        // Act
        await maintainer.RefreshAllAsync();

        // Assert
        Assert.Contains((Machine, ""), _gateway.Fetches);
        Assert.Equal("", ReadRulesFile(session).GetProperty("repo").GetString());
    }

    [Fact]
    public async Task RefreshAllAsync_FolderIsNotAGitRepository_TreatsItAsNoRepo()
    {
        // Arrange
        var session = Adopt("plain-folder");
        _git.Answers[Folder("plain-folder")] = new GitCommandResult
        {
            Success = false, ExitCode = 128, Error = "fatal: not a git repository (or any of the parent directories): .git",
        };
        var maintainer = Maintainer();

        // Act
        await maintainer.RefreshAllAsync();

        // Assert
        Assert.Equal("", ReadRulesFile(session).GetProperty("repo").GetString());
    }

    [Fact]
    public async Task RefreshAllAsync_GitCannotStart_WritesNoFileAndTriesAgainNextRefresh()
    {
        // Arrange
        var session = Adopt();
        _git.Answers[Folder("repo")] = new GitCommandResult { Success = false, ExitCode = -1, Error = "git is not installed" };
        var maintainer = Maintainer();

        // Act
        await maintainer.RefreshAllAsync();

        // Assert: no repo identity is not the same as "this checkout has no origin" - writing the file
        // with an empty repo would hand the session only the machine-wide rules and call that the truth.
        Assert.False(File.Exists(RulesPath(session)));
        Assert.Empty(_gateway.Fetches);

        // And once git answers, the next refresh writes it.
        _git.Origin(Folder("repo"), "https://github.com/thefrederiksen/devthrottle.git");
        await maintainer.RefreshAllAsync();
        Assert.Equal(NormalisedRepo, ReadRulesFile(session).GetProperty("repo").GetString());
    }

    [Fact]
    public async Task RefreshAllAsync_GatewayFailsBeforeEverAnswering_WritesNoFileRatherThanAnEmptyList()
    {
        // Arrange
        var session = Adopt();
        _git.Origin(Folder("repo"), "https://github.com/thefrederiksen/devthrottle.git");
        _gateway.FetchAnswer = (_, _) => EffectiveHookRulesFetch.Failed("HTTP 503");
        var maintainer = Maintainer();

        // Act
        await maintainer.RefreshAllAsync();

        // Assert: an empty list would say "no rule reaches you", which nobody knows.
        Assert.False(File.Exists(RulesPath(session)));
    }

    [Fact]
    public async Task RefreshAllAsync_GatewayFailsAfterAnAnswer_KeepsTheLastAnsweredRules()
    {
        // Arrange
        var session = Adopt();
        _git.Origin(Folder("repo"), "https://github.com/thefrederiksen/devthrottle.git");
        AnswerFor(NormalisedRepo, RuleSamples.DenyEcho());
        var maintainer = Maintainer();
        await maintainer.RefreshAllAsync();

        // Act
        _gateway.FetchAnswer = (_, _) => EffectiveHookRulesFetch.Failed("HTTP 503");
        await maintainer.RefreshAllAsync();

        // Assert
        Assert.Single(ReadRulesFile(session).GetProperty("rules").EnumerateArray());
    }

    [Fact]
    public async Task RefreshAllAsync_RuleCreatedAfterLaunch_ReachesTheRunningSession()
    {
        // Arrange: a session running with no rules.
        var session = Adopt();
        _git.Origin(Folder("repo"), "https://github.com/thefrederiksen/devthrottle.git");
        var maintainer = Maintainer();
        maintainer.Start();
        await maintainer.WhenCreateRefreshDone(session.Id);
        await maintainer.RefreshAllAsync();
        Assert.Empty(ReadRulesFile(session).GetProperty("rules").EnumerateArray());

        // Act: the account creates a rule; the Director's next store refresh runs.
        AnswerFor(NormalisedRepo, RuleSamples.DenyEcho());
        await maintainer.RefreshAllAsync();

        // Assert: what the session's next shell command reads.
        Assert.Single(ReadRulesFile(session).GetProperty("rules").EnumerateArray());
    }

    [Fact]
    public async Task RefreshAllAsync_RulesUnchanged_DoesNotTouchTheFile()
    {
        // Arrange
        var session = Adopt();
        _git.Origin(Folder("repo"), "https://github.com/thefrederiksen/devthrottle.git");
        AnswerFor(NormalisedRepo, RuleSamples.DenyEcho());
        var maintainer = Maintainer();
        await maintainer.RefreshAllAsync();
        var before = File.ReadAllText(RulesPath(session));
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(RulesPath(session), stamp);

        // Act: the clock moves and the same rules come back.
        _clock.Advance(TimeSpan.FromMinutes(5));
        var changed = await maintainer.RefreshAllAsync();

        // Assert
        Assert.Equal(0, changed);
        Assert.Equal(before, File.ReadAllText(RulesPath(session)));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(RulesPath(session)));
    }

    [Fact]
    public async Task RefreshAllAsync_RulesChanged_StampsTheNewGenerationTime()
    {
        // Arrange
        var session = Adopt();
        _git.Origin(Folder("repo"), "https://github.com/thefrederiksen/devthrottle.git");
        var maintainer = Maintainer();
        await maintainer.RefreshAllAsync();

        // Act
        _clock.Advance(TimeSpan.FromMinutes(5));
        AnswerFor(NormalisedRepo, RuleSamples.DenyEcho());
        await maintainer.RefreshAllAsync();

        // Assert
        Assert.Equal("2026-10-18T12:05:00.0000000Z", ReadRulesFile(session).GetProperty("generatedUtc").GetString());
    }

    [Fact]
    public async Task RefreshAllAsync_TwoSessionsInOneRepo_AsksTheGatewayOnce()
    {
        // Arrange: two worktrees of the same repo - the whole point of git-remote identity (D3).
        var first = Adopt("worktree-a");
        var second = Adopt("worktree-b");
        _git.Origin(Folder("worktree-a"), "https://github.com/TheFrederiksen/devthrottle.git");
        _git.Origin(Folder("worktree-b"), "git@github.com:thefrederiksen/devthrottle.git");
        AnswerFor(NormalisedRepo, RuleSamples.DenyEcho());
        var maintainer = Maintainer();

        // Act
        await maintainer.RefreshAllAsync();

        // Assert
        Assert.Single(_gateway.Fetches);
        Assert.Single(ReadRulesFile(first).GetProperty("rules").EnumerateArray());
        Assert.Single(ReadRulesFile(second).GetProperty("rules").EnumerateArray());
    }

    [Fact]
    public async Task RefreshAllAsync_SessionThatIsNotClaudeCode_GetsNoFile()
    {
        // Arrange: phase 1 registers the before-a-command hook for Claude Code alone (P9).
        var session = Adopt(kind: AgentKind.Codex);
        _git.Origin(Folder("repo"), "https://github.com/thefrederiksen/devthrottle.git");
        var maintainer = Maintainer();

        // Act
        await maintainer.RefreshAllAsync();

        // Assert
        Assert.False(File.Exists(RulesPath(session)));
        Assert.Empty(_gateway.Fetches);
    }

    [Fact]
    public async Task RefreshAllAsync_NoGatewayConfigured_WritesNothing()
    {
        // Arrange
        var session = Adopt();
        _git.Origin(Folder("repo"), "https://github.com/thefrederiksen/devthrottle.git");
        var maintainer = Maintainer(() => null);

        // Act
        await maintainer.RefreshAllAsync();

        // Assert
        Assert.False(File.Exists(RulesPath(session)));
    }

    [Fact]
    public async Task Start_SessionCreated_WritesItsFileWithoutWaitingForTheNextRefresh()
    {
        // Arrange
        _git.Origin(Folder("repo"), "https://github.com/thefrederiksen/devthrottle.git");
        AnswerFor(NormalisedRepo, RuleSamples.DenyEcho());
        var maintainer = Maintainer();
        maintainer.Start();

        // Act
        var session = Adopt();
        await maintainer.WhenCreateRefreshDone(session.Id);

        // Assert
        Assert.Single(ReadRulesFile(session).GetProperty("rules").EnumerateArray());
    }

    [Fact]
    public async Task Start_SessionAlreadyOnTheRoster_GetsItsFile()
    {
        // Arrange: a session restored at Director start was never created by this maintainer.
        _git.Origin(Folder("repo"), "https://github.com/thefrederiksen/devthrottle.git");
        var session = Adopt();
        var maintainer = Maintainer();

        // Act
        maintainer.Start();
        await maintainer.WhenCreateRefreshDone(session.Id);

        // Assert
        Assert.True(File.Exists(RulesPath(session)));
    }

    [Fact]
    public async Task Start_SessionRemoved_DeletesItsFile()
    {
        // Arrange
        _git.Origin(Folder("repo"), "https://github.com/thefrederiksen/devthrottle.git");
        var maintainer = Maintainer();
        maintainer.Start();
        var session = Adopt();
        await maintainer.WhenCreateRefreshDone(session.Id);
        Assert.True(File.Exists(RulesPath(session)));

        // Act
        _sessions.RemoveSession(session.Id);

        // Assert
        Assert.False(File.Exists(RulesPath(session)));
    }

    [Fact]
    public async Task RefreshAllAsync_SessionRemovedMeanwhile_DoesNotWriteItsFileBack()
    {
        // Arrange: the removal lands while the Gateway is being asked.
        _git.Origin(Folder("repo"), "https://github.com/thefrederiksen/devthrottle.git");
        var maintainer = Maintainer();
        maintainer.Start();
        var session = Adopt();
        await maintainer.WhenCreateRefreshDone(session.Id);
        _gateway.FetchAnswer = (_, _) =>
        {
            _sessions.RemoveSession(session.Id);
            return EffectiveHookRulesFetch.Answered(new[] { RuleSamples.DenyEcho() });
        };

        // Act
        await maintainer.RefreshAllAsync();

        // Assert
        Assert.False(File.Exists(RulesPath(session)));
    }
}
