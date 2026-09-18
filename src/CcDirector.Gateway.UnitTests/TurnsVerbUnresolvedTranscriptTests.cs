using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #2561: the <c>turns</c> verb must not report an UNRESOLVED TRANSCRIPT as a successful read of an
/// empty conversation.
///
/// The non-Claude-Code branch used to stamp <c>Status = "ok"</c> unconditionally, but
/// <c>SessionHistoryReader.ReadAll</c> answers <c>ConversationHistory.Empty</c> whenever
/// <c>ResolveTranscriptPath</c> returns null - which is exactly what the per-agent locators
/// (<c>PiSessionLocator</c>, <c>CodexRolloutLocator</c>, <c>GrokSessionLocator</c>) do before an agent has
/// written its first transcript. So a Pi / Codex / Grok session whose transcript had not been located
/// returned "ok" with no widgets, and the ONE field that could have told the caller otherwise agreed.
///
/// The cost was voice narration going permanently silent: it read this verb, saw no text widget, and
/// recorded the non-failure "nothing to narrate", which is never retried and raises nothing anywhere. A Pi
/// session observed on 12 August sat silent for 48 minutes while the roster showed it "Preparing voice".
/// </summary>
[Collection("DirectorRoot")]
public sealed class TurnsVerbUnresolvedTranscriptTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A session on a repository path that NO LOCATOR CAN RESOLVE A TRANSCRIPT FOR - on any machine,
    /// whatever has ever been run on it.
    ///
    /// That path used to be <c>Path.GetTempPath()</c> itself, which was a claim about the machine rather
    /// than a property of the test, and on SOREN_NORTH the claim was false: the Grok command line had once
    /// been run from the temporary directory, so <c>~/.grok/sessions/C%3A%5C...%5CTemp</c> existed,
    /// <see cref="Core.Grok.GrokSessionLocator"/> matched it by decoded working directory, the verb
    /// correctly answered "ok", and the Grok case failed (issue #3029). On a machine that had never done
    /// that, the same case passed only because the directory happened to be empty - green for a reason that
    /// has nothing to do with the product, which is not a guard at all.
    ///
    /// So the repository path is now a directory THIS RUN creates under a name nothing else has used, and
    /// removes afterwards. Every locator this verb can reach keys on something such a directory cannot
    /// have:
    /// <list type="bullet">
    /// <item><description><see cref="Core.Grok.GrokSessionLocator"/> scans <c>~/.grok/sessions</c> for a
    /// per-working-directory folder whose percent-decoded name equals the repository path. No agent has ever
    /// run in a directory that did not exist a moment ago.</description></item>
    /// <item><description><see cref="Core.Codex.CodexRolloutLocator"/> scans <c>~/.codex/sessions</c> for a
    /// rollout whose <c>session_meta.cwd</c> equals it - the same argument.</description></item>
    /// <item><description><see cref="Core.Pi.PiSessionLocator"/> resolves by the AGENT SESSION ID, which an
    /// embedded test session does not have at all.</description></item>
    /// </list>
    /// A locator added later that keys on the working directory is covered by the same argument. One that
    /// keys on a global store is NOT, which is why the store-backed agents are still deliberately left out
    /// of the assertions below.
    /// </summary>
    private sealed class UnresolvableSession : IDisposable
    {
        public SessionManager Manager { get; }
        public Session Session { get; }
        public string RepoPath { get; }

        public UnresolvableSession()
        {
            RepoPath = Path.Combine(Path.GetTempPath(), "ccd-turns-unresolved-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RepoPath);
            Manager = new SessionManager(new Core.Configuration.AgentOptions());
            Session = Manager.CreateEmbeddedSession(RepoPath, null, new ExecuteActionTestBackend());
        }

        /// <summary>Deletes what the run created. Not best-effort: the directory is this test's own, nothing
        /// holds it open, and a failure to remove it is a real fault worth seeing rather than swallowing.</summary>
        public void Dispose()
        {
            Manager.Dispose();
            Directory.Delete(RepoPath, recursive: true);
        }
    }

    private static TurnsResponse Read(UnresolvableSession fixture)
    {
        var command = new DirectorCommand { CommandId = "t1", Verb = "turns", SessionId = fixture.Session.Id.ToString() };
        var result = SessionReadExecutor.Turns(fixture.Manager, command);
        Assert.True(result.Ok);   // a failed READ still rides a successful command result - that is the trap
        return JsonSerializer.Deserialize<TurnsResponse>(result.BodyJson!, Json)!;
    }

    /// <summary>
    /// A supported non-Claude agent whose transcript has not been located yet. The session is embedded on a
    /// repository path this run created and nothing has ever run in, with a fresh id, so no locator can
    /// resolve a transcript for it - the exact state a freshly-spawned Pi session is in before it writes its
    /// first turn.
    /// </summary>
    [Theory]
    [InlineData(Core.Agents.AgentKind.Pi)]
    [InlineData(Core.Agents.AgentKind.Codex)]
    [InlineData(Core.Agents.AgentKind.Grok)]
    public void Turns_SupportedAgentWithNoTranscriptYet_ReportsNoTranscript_NotOk(Core.Agents.AgentKind agent)
    {
        using var fixture = new UnresolvableSession();
        fixture.Session.AgentKind = agent;

        var resp = Read(fixture);

        Assert.Equal("no_transcript", resp.Status);
        Assert.False(string.IsNullOrWhiteSpace(resp.Error));   // and it says WHY, so a caller can log it
        Assert.Empty(resp.Widgets);
    }

    /// <summary>
    /// The negative control: an agent that exposes no conversation history at all keeps its own distinct
    /// status. "Unsupported" and "the transcript has not appeared yet" are different facts and the voice
    /// service now treats them differently - one is terminal, the other is retried - so collapsing them
    /// would trade one lie for another.
    /// </summary>
    [Fact]
    public void Turns_AgentWithNoHistoryProvider_StillReportsUnsupported()
    {
        using var fixture = new UnresolvableSession();
        fixture.Session.AgentKind = Core.Agents.AgentKind.Cursor;

        var resp = Read(fixture);

        Assert.Equal("unsupported", resp.Status);
    }

    /// <summary>
    /// THE OTHER HALF (found in review): a RESOLVED path is not a proven read either.
    ///
    /// Copilot and OpenCode resolve to a GLOBAL SQLite store, which exists or does not exist for reasons
    /// that have nothing to do with this session - and both readers answer an empty history both for a store
    /// with no repository match AND for a database error they caught. Stamping "ok" on that recreates the
    /// exact false success this whole change removes, so an empty conversation gets its own status.
    ///
    /// Gemini is the case asserted here: it goes down the same branch with a deliberately null transcript
    /// path (it reads the terminal buffer), and an embedded test session's buffer is empty - so it produces
    /// a resolved-but-empty read deterministically, on any machine.
    ///
    /// COPILOT AND OPENCODE ARE DELIBERATELY NOT ASSERTED HERE, and the reason is worth writing down: their
    /// readers open the DEVELOPER'S OWN store at CopilotHistoryReader.DefaultDatabasePath and match by
    /// repository path, so what this test would assert depends on what happens to be in that database on the
    /// machine running it. A first version of this test did include them and failed on exactly that - the
    /// local Copilot store answered a non-empty history for a temporary directory. A test whose verdict
    /// moves with the developer's machine is not evidence, so the store-backed agents are covered by the
    /// status-handling tests in WingmanVoiceServiceTests (which drive "empty_history" directly) rather than
    /// by reading a real database here.
    /// </summary>
    [Theory]
    [InlineData(Core.Agents.AgentKind.Gemini)]
    public void Turns_ResolvedSourceWithNoConversation_ReportsEmptyHistory_NotOk(Core.Agents.AgentKind agent)
    {
        using var fixture = new UnresolvableSession();
        fixture.Session.AgentKind = agent;

        var resp = Read(fixture);

        // Pinned to the EXACT status, not "anything but ok". Accepting either value would have passed
        // just as happily on the no_transcript branch and proved nothing about the one under test.
        Assert.Equal("empty_history", resp.Status);
        Assert.False(string.IsNullOrWhiteSpace(resp.Error));
        Assert.Empty(resp.Widgets);
    }

    /// <summary>
    /// CLAUDE CODE TOO - the agent this verb runs for most often, and the one the first version of this rule
    /// left out (found in review). A transcript file that EXISTS but parses to nothing is a read that
    /// produced no conversation, not a conversation that was read, and stamping "ok" on it is the exact
    /// false success this change exists to remove.
    /// </summary>
    [Fact]
    public void Turns_ClaudeTranscriptThatExistsButIsEmpty_ReportsEmptyHistory_NotOk()
    {
        using var fixture = new UnresolvableSession();

        // Put a REAL, empty transcript exactly where the reader will look for it, so the branch under
        // test is the one that parses a present file - not the no_jsonl branch above it.
        fixture.Session.ClaudeSessionId = Guid.NewGuid().ToString();
        var jsonl = Core.Claude.ClaudeSessionReader.GetJsonlPath(fixture.Session.ClaudeSessionId, fixture.Session.RepoPath);

        // Claude Code names its project folder after the working directory, and this run's working directory
        // is one nothing else has used - so this folder is the run's OWN and the whole of it goes at the end.
        // Deleting only the file would leave an empty folder in the developer's real ~/.claude/projects on
        // every run, which the old shared temporary path never did because its folder already existed.
        var projectFolder = Path.GetDirectoryName(jsonl)!;
        Directory.CreateDirectory(projectFolder);
        File.WriteAllText(jsonl, "");
        try
        {
            var resp = Read(fixture);

            Assert.Equal("empty_history", resp.Status);
            Assert.Empty(resp.Widgets);
        }
        finally { Directory.Delete(projectFolder, recursive: true); }
    }
}
