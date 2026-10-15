using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// A SEAT AT ITS START, THROUGH THE REAL CREATE (Factory Definition mission, phase 5, design sections 5 and 6).
///
/// The guard: after the kit is written and before a slot is taken or a key minted, an agent that cannot take the kit's
/// rules or factory skills is refused with the shared table's reason, and every secret the seat lists is asked about on
/// this machine - not held, not allowed for run, and a cc-secrets that cannot answer each refuse in words. cc-secrets is
/// faked: the question is handed a list that answers as told, so no process starts.
///
/// The line: a Claude Code seat is started with exactly the arguments the Director emits - one --settings naming its
/// own settings file when its kit carries rules, --plugin-dir when it carries factory skills - and a session that is not
/// a seat with the line it always had. Read from the Studio backend, which records what it was started with and runs no
/// process; the hook files, the preamble and the pointer drop box all go to the scratch folder.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactorySeatAtStartTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dt-kit-refuse-").FullName;
    private readonly List<string> _poolTaken = new();
    private readonly string _machine = Environment.MachineName;
    private int _secretListRuns;
    private ProcessRunner.Result _secretList = new(0, "{\"entries\": []}", "", Started: true);

    // What the faked Gateway puts in the kit.
    private string _rules = "{\"permissions\":{\"deny\":[],\"ask\":[]}}\n";
    private string _kitJson = "{\"kind\":\"seat\",\"secrets\":[]}\n";
    private bool _plugin;

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { /* scratch folder */ }
    }

    private string KitRoot => Path.Combine(_scratch, "factory-run");

    private string HooksDirectory => Path.Combine(_scratch, "claude-hooks");

    private string SharedSettings => Path.Combine(HooksDirectory, Claude.ClaudeHookInstaller.SharedSettingsFileName);

    private string Repo()
    {
        var repo = Path.Combine(_scratch, "repo");
        Directory.CreateDirectory(repo);
        return repo;
    }

    private sealed class RecordingPool(List<string> taken) : IWorktreePool
    {
        public PooledWorktree Get(string repoPath, string holder, int poolSize)
        {
            taken.Add(repoPath);
            var slot = Path.Combine(Path.GetDirectoryName(repoPath)!, "repo.worktrees", "wt01");
            Directory.CreateDirectory(slot);
            return new PooledWorktree(repoPath, "wt01", slot, "lease-1");
        }

        public PooledWorktreeReturn Return(PooledWorktree worktree) => new(worktree.Slot, worktree.Path, Freed: true, HeldReason: null);
    }

    private SessionManager Manager()
    {
        var manager = new SessionManager(
            new AgentOptions { DefaultBufferSizeBytes = 65536, GracefulShutdownTimeoutSeconds = 2 },
            reservations: new WorktreeReservationStore(Path.Combine(_scratch, "reservations")),
            worktreePool: new RecordingPool(_poolTaken),
            worktreePoolSetting: _ => new WorktreePoolSetting(Enabled: false, PoolSize: 4))
        {
            FactoryRunRoot = KitRoot,
            FactoryMemoryRoot = Path.Combine(_scratch, "factory-memory"),
            ClaudeHooksDirectory = HooksDirectory,
            SessionPreambleDirectory = Path.Combine(_scratch, "session-preambles"),
            SessionPointerDirectory = Path.Combine(_scratch, "session-pointers"),
        };
        manager.FactorySecretsQuestion = new FactoryMachineQuestion(_ =>
        {
            Interlocked.Increment(ref _secretListRuns);
            return Task.FromResult(_secretList);
        }, TimeSpan.FromSeconds(10));
        manager.FactoryKitDownload = (kit, _) =>
        {
            var files = new List<FactoryKitFileDto>
            {
                new() { Path = "kit.json", Text = _kitJson },
                new() { Path = "rules.json", Text = _rules },
                new() { Path = "brief.md", Text = "Brief.\n" },
            };
            if (_plugin)
            {
                files.Add(new() { Path = "claude-plugin/.claude-plugin/plugin.json", Text = "{\"name\":\"factory-kit\"}\n" });
                files.Add(new() { Path = "claude-plugin/skills/send-mail/SKILL.md", Text = "# Send mail\n" });
            }
            return new FactoryKitResponse { Factory = kit.Factory, Seat = kit.Seat, Version = kit.Version, Kind = kit.Kind, Files = files };
        };
        return manager;
    }

    /// <summary>The create checks that the agent's executable is a file; it is never run on the Studio backend.</summary>
    private string AgentExe()
    {
        var exe = Path.Combine(_scratch, "cc-test-agent.exe");
        if (!File.Exists(exe)) File.WriteAllText(exe, "");
        return exe;
    }

    private Session Create(SessionManager manager, IAgent agent, FactoryKitRequest? kit)
        => manager.CreateSession(Repo(), agent, userArgs: null, SessionBackendType.Studio,
            resumeSessionId: null, factoryKit: kit, forcePooledWorktree: kit is not null);

    private CodexAgent Codex() => new(new AgentOptions { CodexPath = AgentExe() });

    private RawCliAgent RawCli() => new(AgentExe());

    private ClaudeAgent ClaudeCode() => new(new AgentOptions { ClaudePath = AgentExe(), DefaultClaudeArgs = "--dangerously-skip-permissions" });

    private static string Arguments(Session session) => Assert.IsType<StudioBackend>(session.Backend).StartedArguments;

    private static int Count(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static FactoryKitRequest Seat(string kind = FactoryKits.Seat) => new("devthrottle", 4, "outreach", kind);

    /// <summary>The refused create left nothing: no session, no slot, no kit folder.</summary>
    private void AssertNothingLeft(SessionManager manager)
    {
        Assert.Empty(manager.ListSessions());
        Assert.Empty(_poolTaken);
        Assert.False(Directory.Exists(KitRoot) && Directory.EnumerateDirectories(KitRoot).Any());
    }

    [Fact]
    public void CreateSession_ACodexSeatWhoseKitCarriesRules_IsRefusedWithTheRulesTablesReason()
    {
        using var manager = Manager();
        _rules = "{\"permissions\":{\"deny\":[\"Bash(git push:*)\"],\"ask\":[]}}\n";

        var ex = Assert.Throws<FactorySeatRefusedException>(() => Create(manager, Codex(), Seat()));

        Assert.Equal("No session was started: Codex enforces a rule given at launch only while its hook runs; when the " +
                     "hook is missing, fails or times out the command runs anyway, so a Codex seat cannot carry rules.", ex.Message);
        AssertNothingLeft(manager);
    }

    [Fact]
    public void CreateSession_ACodexChildWhoseRulesOnlyKitCarriesRules_IsRefusedWithTheRulesTablesReason()
    {
        using var manager = Manager();
        _rules = "{\"permissions\":{\"deny\":[],\"ask\":[\"Bash(git tag:*)\"]}}\n";
        _kitJson = "{\"kind\":\"rules-only\"}\n";

        var ex = Assert.Throws<FactorySeatRefusedException>(() => Create(manager, Codex(), Seat(FactoryKits.RulesOnly)));

        Assert.Equal("No session was started: " + FactoryRulesSupport.CodexReason, ex.Message);
        AssertNothingLeft(manager);
    }

    [Fact]
    public void CreateSession_ACodexSeatWhoseKitCarriesFactorySkills_IsRefusedWithTheSkillTablesReason()
    {
        using var manager = Manager();
        _plugin = true;

        var ex = Assert.Throws<FactorySeatRefusedException>(() => Create(manager, Codex(), Seat()));

        Assert.Equal("No session was started: Codex reads skills only from the start folder and its parents, the user's " +
                     "home folder and an administrator folder; none of them is for one session alone, so a Codex seat " +
                     "cannot be given a factory skill.", ex.Message);
        AssertNothingLeft(manager);
    }

    [Fact]
    public void CreateSession_ASecretThisMachineDoesNotHold_IsRefusedNamingIt()
    {
        using var manager = Manager();
        _kitJson = "{\"kind\":\"seat\",\"secrets\":[\"smtp-password\",\"crm-token\"]}\n";
        _secretList = new(0, "{\"entries\":[{\"name\":\"smtp-password\",\"kind\":\"secret\",\"uses\":[\"run\"]}]}", "", Started: true);

        var ex = Assert.Throws<FactorySeatRefusedException>(() => Create(manager, RawCli(), Seat()));

        Assert.Equal($"The seat outreach needs the secret 'crm-token', which {_machine} does not hold, so no session was " +
                     "started. Add it there with cc-secrets.", ex.Message);
        Assert.Equal(1, _secretListRuns);
        AssertNothingLeft(manager);
    }

    [Fact]
    public void CreateSession_ASecretNotAllowedForRun_IsRefusedInCcSecretsOwnWords()
    {
        using var manager = Manager();
        _kitJson = "{\"kind\":\"seat\",\"secrets\":[\"crm-token\"]}\n";
        _secretList = new(0, "{\"entries\":[{\"name\":\"crm-token\",\"kind\":\"secret\",\"uses\":[\"login\"]}]}", "", Started: true);

        var ex = Assert.Throws<FactorySeatRefusedException>(() => Create(manager, RawCli(), Seat()));

        Assert.Equal($"The seat outreach needs the secret 'crm-token' on {_machine}, so no session was started: " +
                     "Secret 'crm-token' is not allowed for 'run'. It may be used for: login.", ex.Message);
        AssertNothingLeft(manager);
    }

    [Fact]
    public void CreateSession_CcSecretsCannotAnswer_IsRefusedSayingSoAndNeverReadAsNotHeld()
    {
        using var manager = Manager();
        _kitJson = "{\"kind\":\"seat\",\"secrets\":[\"smtp-password\"]}\n";
        _secretList = new(-1, "", "", Started: false);

        var ex = Assert.Throws<FactorySeatRefusedException>(() => Create(manager, RawCli(), Seat()));

        Assert.Equal($"The seat outreach needs the secret 'smtp-password', and {_machine} could not say whether it holds it, " +
                     $"so no session was started: cc-secrets is not installed on {_machine}, or could not be started there.", ex.Message);
        AssertNothingLeft(manager);
    }

    [Fact]
    public void CreateSession_EverySecretHeldAndAllowedForRun_Starts()
    {
        using var manager = Manager();
        _kitJson = "{\"kind\":\"seat\",\"secrets\":[\"smtp-password\"]}\n";
        _secretList = new(0, "{\"entries\":[{\"name\":\"smtp-password\",\"kind\":\"secret\",\"uses\":[\"run\",\"login\"]}]}", "", Started: true);

        var session = Create(manager, RawCli(), Seat());

        Assert.NotNull(session.FactoryKitDirectory);
        Assert.Equal(1, _secretListRuns);
        Assert.Single(_poolTaken);
    }

    [Fact]
    public void CreateSession_ASeatThatListsNoSecret_NeverAsksCcSecrets()
    {
        using var manager = Manager();

        Create(manager, RawCli(), Seat());

        Assert.Equal(0, _secretListRuns);
    }

    [Fact]
    public void CreateSession_UNDEFINED_FACTORY_ASessionThatIsNotASeat_IsNeitherGuardedNorAsked()
    {
        using var manager = Manager();
        // Whatever a kit would carry, a session with no kit request never reads one.
        _rules = "{\"permissions\":{\"deny\":[\"Bash(git push:*)\"],\"ask\":[]}}\n";
        _kitJson = "{\"kind\":\"seat\",\"secrets\":[\"smtp-password\"]}\n";

        var session = Create(manager, RawCli(), kit: null);

        Assert.Null(session.FactoryKitDirectory);
        Assert.Equal(0, _secretListRuns);
        Assert.Empty(_poolTaken);
    }

    [Fact]
    public void CreateSession_AClaudeCodeSeatWhoseKitCarriesRules_StartsWithOneSettingsNamingItsOwnFile()
    {
        using var manager = Manager();
        _rules = "{\"permissions\":{\"deny\":[\"Bash(git push:*)\"],\"ask\":[]}}\n";

        var session = Create(manager, ClaudeCode(), Seat());

        var own = Path.Combine(session.FactoryKitDirectory!, "settings.json");
        var line = Arguments(session);
        Assert.Equal($"{session.EffectiveLaunchArgs} --settings \"{own}\"", line);
        Assert.Equal(1, Count(line, "--settings"));
        Assert.DoesNotContain("--plugin-dir", line);
        Assert.Contains("git push", File.ReadAllText(own));
        // The shared file was installed for the hooks the seat's file runs, and carries no rule.
        Assert.DoesNotContain("permissions", File.ReadAllText(SharedSettings));
    }

    [Fact]
    public void CreateSession_AClaudeCodeSeatKitWithFactorySkills_StartsWithPluginDir()
    {
        using var manager = Manager();
        _plugin = true;

        var session = Create(manager, ClaudeCode(), Seat());

        Assert.Equal($"{session.EffectiveLaunchArgs} --settings \"{SharedSettings}\" " +
                     $"--plugin-dir \"{Path.Combine(session.FactoryKitDirectory!, "claude-plugin")}\"", Arguments(session));
    }

    [Fact]
    public void CreateSession_AClaudeCodeChildWhoseRulesOnlyKitCarriesRules_HasItsOwnSettingsAndNoPluginDir()
    {
        using var manager = Manager();
        _rules = "{\"permissions\":{\"deny\":[],\"ask\":[\"Bash(git tag:*)\"]}}\n";
        _kitJson = "{\"kind\":\"rules-only\"}\n";

        var session = Create(manager, ClaudeCode(), Seat(FactoryKits.RulesOnly));

        Assert.Equal($"{session.EffectiveLaunchArgs} --settings \"{Path.Combine(session.FactoryKitDirectory!, "settings.json")}\"",
            Arguments(session));
    }

    [Fact]
    public void CreateSession_UNDEFINED_FACTORY_AClaudeCodeSessionThatIsNotASeat_StartsWithTheSharedSettingsAsBefore()
    {
        using var manager = Manager();

        var session = Create(manager, ClaudeCode(), kit: null);

        // The line the Director appended before phase 5: the shared file, once, quoted.
        Assert.Equal($"{session.EffectiveLaunchArgs} --settings \"{SharedSettings}\"".Trim(), Arguments(session));
        Assert.Null(session.FactoryKitDirectory);
    }
}
