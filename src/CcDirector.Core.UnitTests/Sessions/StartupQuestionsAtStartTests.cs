using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// THE START-UP QUESTIONS, THROUGH THE REAL CREATE (Command Line Agent Handling mission). A Codex session is started with
/// its update check off, and trusted for the run when Claude Code's record trusts its folder; a Claude Code session that
/// nobody will answer and that would stop on the Bypass Permissions warning is refused before it starts, while one the
/// owner opens starts as always. The sessions start on the Studio backend, which records what it was started with and
/// runs no process; Claude Code's record is a scratch file, never the user's own.
/// </summary>
public sealed class StartupQuestionsAtStartTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dt-startup-at-start-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { /* scratch folder */ }
    }

    private string Config => Path.Combine(_scratch, ".claude.json");

    private string Repo()
    {
        var repo = Path.Combine(_scratch, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        return repo;
    }

    private const string Refusal = "No session was started: the bypass warning would wait.";

    private SessionManager Manager(Func<string?, string, string, string?>? bypassCheck = null) => new(
        new AgentOptions { DefaultBufferSizeBytes = 65536, GracefulShutdownTimeoutSeconds = 2 },
        reservations: new WorktreeReservationStore(Path.Combine(_scratch, "reservations")))
    {
        ClaudeHooksDirectory = Path.Combine(_scratch, "claude-hooks"),
        SessionPreambleDirectory = Path.Combine(_scratch, "session-preambles"),
        SessionPointerDirectory = Path.Combine(_scratch, "session-pointers"),
        ClaudeFolderTrust = new ClaudeFolderTrust(Config),
        ClaudeBypassCheck = bypassCheck,
    };

    private string AgentExe()
    {
        var exe = Path.Combine(_scratch, "cc-test-agent.exe");
        if (!File.Exists(exe)) File.WriteAllText(exe, "");
        return exe;
    }

    private static string Arguments(Session session) => Assert.IsType<StudioBackend>(session.Backend).StartedArguments;

    private Session Start(SessionManager manager, IAgent agent, Guid? controller = null)
        => manager.CreateSession(Repo(), agent, userArgs: null, SessionBackendType.Studio, resumeSessionId: null,
            controllerSessionId: controller);

    [Fact]
    public void CreateSession_ACodexSessionInAFolderClaudeCodeTrusts_StartsWithTheUpdateCheckOffAndTheFolderTrusted()
    {
        var repo = Repo();
        File.WriteAllText(Config, $"{{\"projects\": {{\"{ClaudeFolderTrust.KeyFor(repo).Replace("\\", "\\\\")}\": {{\"hasTrustDialogAccepted\": true}}}}}}");
        using var manager = Manager();

        var line = Arguments(Start(manager, new CodexAgent(new AgentOptions { CodexPath = AgentExe() })));

        Assert.Contains("-c check_for_update_on_startup=false", line);
        Assert.Contains($"-c \"projects={{'{Path.GetFullPath(repo)}'={{trust_level='trusted'}}}}\"", line);
    }

    [Fact]
    public void CreateSession_ACodexSessionInAFolderNobodyTrusted_StartsWithTheUpdateCheckOffAndNoTrust()
    {
        File.WriteAllText(Config, "{\"projects\": {}}");
        using var manager = Manager();

        var line = Arguments(Start(manager, new CodexAgent(new AgentOptions { CodexPath = AgentExe() })));

        Assert.Contains("-c check_for_update_on_startup=false", line);
        Assert.DoesNotContain("trust_level", line);
    }

    private ClaudeAgent ClaudeBypass() => new(new AgentOptions { ClaudePath = AgentExe(), DefaultClaudeArgs = "--dangerously-skip-permissions" });

    [Fact]
    public void CreateSession_AClaudeSessionOpenedByAnotherSessionThatWouldStopOnTheBypassWarning_IsRefusedInWords()
    {
        string? askedWith = null;
        using var manager = Manager((_, args, _) => { askedWith = args; return Refusal; });

        var ex = Assert.Throws<ClaudeStartRefusedException>(() => Start(manager, ClaudeBypass(), controller: Guid.NewGuid()));

        Assert.Equal(Refusal, ex.Message);
        Assert.Contains("--dangerously-skip-permissions", askedWith);
        Assert.Empty(manager.ListSessions());
    }

    [Fact]
    public void CreateSession_AClaudeSessionTheOwnerOpens_StartsWithoutAskingTheBypassCheck()
    {
        var asked = false;
        using var manager = Manager((_, _, _) => { asked = true; return Refusal; });

        var session = Start(manager, ClaudeBypass(), controller: null);

        Assert.False(asked);
        Assert.Contains("--dangerously-skip-permissions", Arguments(session));
    }

    [Fact]
    public void CreateSession_AClaudeSessionOpenedByAnotherSessionWhoseConsentIsRecorded_Starts()
    {
        using var manager = Manager((_, _, _) => null);

        var session = Start(manager, ClaudeBypass(), controller: Guid.NewGuid());

        Assert.Contains("--dangerously-skip-permissions", Arguments(session));
    }
}
