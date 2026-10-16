using System.Text.Json.Nodes;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// A SEAT'S FOLDERS, THROUGH THE REAL CREATE (Factory Definition mission, phase 5, P7; QA findings 1, 2 and 3).
///
/// Folder trust: a Claude Code seat's folder - the factory-run root, which covers every kit's work/, or the pooled slot
/// the seat took - is recorded as trusted in Claude Code's configuration before the agent starts, and no other folder is;
/// a session in no factory, and a seat of another agent, write nothing; a configuration that cannot be read refuses the
/// start in words and gives the slot back. The configuration is a scratch file, never the user's own.
///
/// The prompt file: a seat's agent is started with its kit named in the environment, and that names the kit's prompts
/// folder as where its prompts sent as files go; a session in no factory names none, which keeps .temp/.
///
/// The sessions start on the Studio backend, which records what it was started with and runs no process.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactorySeatFolderTrustTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dt-kit-trust-").FullName;
    private readonly List<PooledWorktree> _returned = new();

    public FactorySeatFolderTrustTests()
    {
        File.WriteAllText(Config, "{\"numStartups\": 3, \"projects\": {\"D:/Somewhere/else\": {\"hasTrustDialogAccepted\": true}}}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { /* scratch folder */ }
    }

    private string Config => Path.Combine(_scratch, ".claude.json");

    private string KitRoot => Path.Combine(_scratch, "factory-run");

    private string RepoPath => Path.Combine(_scratch, "repo");

    private string Slot => Path.Combine(_scratch, "repo.worktrees", "wt01");

    private string Repo()
    {
        Directory.CreateDirectory(RepoPath);
        return RepoPath;
    }

    private sealed class Pool(List<PooledWorktree> returned, string slot) : IWorktreePool
    {
        public PooledWorktree Get(string repoPath, string holder, int poolSize)
        {
            Directory.CreateDirectory(slot);
            return new PooledWorktree(repoPath, "wt01", slot, "lease-1");
        }

        public PooledWorktreeReturn Return(PooledWorktree worktree)
        {
            returned.Add(worktree);
            return new(worktree.Slot, worktree.Path, Freed: true, HeldReason: null);
        }
    }

    private SessionManager Manager(bool trustWriter = true) => new(
        new AgentOptions { DefaultBufferSizeBytes = 65536, GracefulShutdownTimeoutSeconds = 2 },
        reservations: new WorktreeReservationStore(Path.Combine(_scratch, "reservations")),
        worktreePool: new Pool(_returned, Slot),
        worktreePoolSetting: _ => new WorktreePoolSetting(Enabled: false, PoolSize: 4))
    {
        FactoryRunRoot = KitRoot,
        FactoryMemoryRoot = Path.Combine(_scratch, "factory-memory"),
        ClaudeHooksDirectory = Path.Combine(_scratch, "claude-hooks"),
        SessionPreambleDirectory = Path.Combine(_scratch, "session-preambles"),
        SessionPointerDirectory = Path.Combine(_scratch, "session-pointers"),
        ClaudeFolderTrust = trustWriter ? new ClaudeFolderTrust(Config) : null,
        FactoryKitDownload = Kit,
    };

    private static FactoryKitResponse Kit(FactoryKitRequest kit, Guid session) => new()
    {
        Factory = kit.Factory, Seat = kit.Seat, Version = kit.Version, Kind = kit.Kind,
        Files = new List<FactoryKitFileDto>
        {
            new() { Path = "kit.json", Text = "{\"kind\":\"seat\",\"secrets\":[]}\n" },
            new() { Path = "rules.json", Text = "{\"permissions\":{\"deny\":[],\"ask\":[]}}\n" },
            new() { Path = "brief.md", Text = "Brief.\n" },
        },
    };

    private string AgentExe()
    {
        var exe = Path.Combine(_scratch, "cc-test-agent.exe");
        if (!File.Exists(exe)) File.WriteAllText(exe, "");
        return exe;
    }

    private ClaudeAgent ClaudeCode() => new(new AgentOptions { ClaudePath = AgentExe(), DefaultClaudeArgs = "--dangerously-skip-permissions" });

    private Session Create(SessionManager manager, IAgent agent, FactoryKitRequest? kit)
        => manager.CreateSession(Repo(), agent, userArgs: null, SessionBackendType.Studio,
            resumeSessionId: null, factoryKit: kit, forcePooledWorktree: kit is not null && !kit.Home);

    private static FactoryKitRequest Seat(bool home) => new("devthrottle", 4, "outreach", FactoryKits.Seat, home, null);

    /// <summary>The project keys whose hasTrustDialogAccepted is true, besides the one the scratch file starts with.</summary>
    private string[] TrustedAdded()
    {
        var projects = (JsonObject)JsonNode.Parse(File.ReadAllText(Config))!["projects"]!;
        return projects.Where(p => p.Key != "D:/Somewhere/else" && p.Value?["hasTrustDialogAccepted"]?.GetValue<bool>() == true)
            .Select(p => p.Key).ToArray();
    }

    [Fact]
    public void CreateSession_AClaudeSeatWithNoRepository_TrustsTheFactoryRunRootAndNoOtherFolder()
    {
        using var manager = Manager();

        var session = Create(manager, ClaudeCode(), Seat(home: true));

        Assert.Equal(new[] { ClaudeFolderTrust.KeyFor(KitRoot) }, TrustedAdded());
        // The root covers the folder the agent works in (proven on Claude Code: trust-proof-output.txt, case B).
        Assert.StartsWith(ClaudeFolderTrust.KeyFor(KitRoot) + "/", ClaudeFolderTrust.KeyFor(session.RepoPath));
    }

    [Fact]
    public void CreateSession_ASecondClaudeSeatWithNoRepository_WritesNothingMore()
    {
        using var manager = Manager();
        Create(manager, ClaudeCode(), Seat(home: true));
        var bytes = File.ReadAllBytes(Config);

        Create(manager, ClaudeCode(), Seat(home: true));

        Assert.Equal(bytes, File.ReadAllBytes(Config));
    }

    [Fact]
    public void CreateSession_AClaudeSeatInARepository_TrustsThePooledSlotItTookAndNotTheCheckoutOrThePoolRoot()
    {
        using var manager = Manager();

        var session = Create(manager, ClaudeCode(), Seat(home: false));

        Assert.Equal(Slot, session.RepoPath);
        Assert.Equal(new[] { ClaudeFolderTrust.KeyFor(Slot) }, TrustedAdded());
    }

    [Fact]
    public void CreateSession_UNDEFINED_FACTORY_AClaudeSessionThatIsNotASeat_WritesNothing()
    {
        using var manager = Manager();
        var bytes = File.ReadAllBytes(Config);

        Create(manager, ClaudeCode(), kit: null);

        Assert.Equal(bytes, File.ReadAllBytes(Config));
    }

    [Fact]
    public void CreateSession_ASeatOfAnotherAgent_WritesNothing()
    {
        using var manager = Manager();
        var bytes = File.ReadAllBytes(Config);

        Create(manager, new RawCliAgent(AgentExe()), Seat(home: true));

        Assert.Equal(bytes, File.ReadAllBytes(Config));
    }

    [Fact]
    public void CreateSession_AManagerGivenNoTrustWriter_StartsTheSeatAndWritesNothing()
    {
        using var manager = Manager(trustWriter: false);
        var bytes = File.ReadAllBytes(Config);

        Create(manager, ClaudeCode(), Seat(home: true));

        Assert.Equal(bytes, File.ReadAllBytes(Config));
    }

    [Fact]
    public void CreateSession_AClaudeSeatWhoseConfigCannotBeRead_RefusesTheStartInWordsAndGivesTheSlotBack()
    {
        using var manager = Manager();
        File.WriteAllText(Config, "{ not json");

        var error = Assert.Throws<ClaudeFolderTrustException>(() => Create(manager, ClaudeCode(), Seat(home: false)));

        Assert.StartsWith($"No seat was started: the folder {Slot} could not be marked trusted, because {Config} could not be read", error.Message);
        Assert.Empty(manager.ListSessions());
        Assert.Equal(Slot, Assert.Single(_returned).Path);
        Assert.False(Directory.Exists(KitRoot) && Directory.EnumerateFileSystemEntries(KitRoot).Any(), "the kit this create wrote goes");
    }

    [Fact]
    public void CreateSession_AClaudeSeatWhoseConfigIsLocked_RefusesTheStartInWords()
    {
        var lockDir = Config + ".lock";
        Directory.CreateDirectory(lockDir);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        Directory.SetLastWriteTimeUtc(lockDir, clock.GetUtcNow().UtcDateTime);
        using var manager = new SessionManager(
            new AgentOptions { DefaultBufferSizeBytes = 65536, GracefulShutdownTimeoutSeconds = 2 },
            reservations: new WorktreeReservationStore(Path.Combine(_scratch, "reservations")),
            worktreePool: new Pool(_returned, Slot),
            worktreePoolSetting: _ => new WorktreePoolSetting(Enabled: false, PoolSize: 4))
        {
            FactoryRunRoot = KitRoot,
            FactoryMemoryRoot = Path.Combine(_scratch, "factory-memory"),
            ClaudeHooksDirectory = Path.Combine(_scratch, "claude-hooks"),
            SessionPreambleDirectory = Path.Combine(_scratch, "session-preambles"),
            SessionPointerDirectory = Path.Combine(_scratch, "session-pointers"),
            ClaudeFolderTrust = new ClaudeFolderTrust(Config, clock, pause =>
            {
                clock.Advance(pause);
                Directory.SetLastWriteTimeUtc(lockDir, clock.GetUtcNow().UtcDateTime); // its holder keeps renewing it
            }),
            FactoryKitDownload = Kit,
        };

        var error = Assert.Throws<ClaudeFolderTrustException>(() => Create(manager, ClaudeCode(), Seat(home: true)));

        Assert.Contains($"another Claude Code process held the lock on {Config} for the 10 seconds this Director waited", error.Message);
        Assert.Empty(manager.ListSessions());
    }

    [Fact]
    public void CreateSession_ASeat_StartsItsAgentWithItsKitsPromptsFolderAsWhereItsPromptFilesGo()
    {
        using var manager = Manager();

        var session = Create(manager, ClaudeCode(), Seat(home: false));

        var environment = Assert.IsType<StudioBackend>(session.Backend).StartedEnvironment;
        Assert.Equal(Path.Combine(session.FactoryKitDirectory!, FactoryRunKit.PromptFolderName), FactoryRunKit.PromptDirectoryFor(environment));
        Assert.False(Path.GetFullPath(FactoryRunKit.PromptDirectoryFor(environment)).StartsWith(Path.GetFullPath(Slot), StringComparison.OrdinalIgnoreCase),
            "the prompts folder is never inside the checkout the seat works in");
    }

    [Fact]
    public void CreateSession_UNDEFINED_FACTORY_ASessionThatIsNotASeat_NamesNoPromptsFolder()
    {
        using var manager = Manager();

        var session = Create(manager, ClaudeCode(), kit: null);

        Assert.Equal("", FactoryRunKit.PromptDirectoryFor(Assert.IsType<StudioBackend>(session.Backend).StartedEnvironment));
    }
}
