using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// A SEAT'S KIT AT LAUNCH, THROUGH THE REAL CREATE (Factory Definition mission, phase 5, design sections 2 and 4). The
/// sessions start on the Studio backend, whose start runs no process, so each case reads exactly what the agent would
/// have been started with - its folder and its environment - with no real process, port or clock.
///
/// The cases: the kit is written before the agent starts and named in <c>CC_FACTORY_KIT_DIR</c>; a seat with no
/// repository works in the kit's work/ and its repository path is ignored; a seat in a repository takes a pooled slot
/// with the setting off; a failed download stops the create with the reason and takes no slot; the folder goes when
/// the session ends; a restored seat resumes in the same folder with work/ kept; and a session that is not a seat is
/// exactly what it was.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryKitAtLaunchTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dt-kit-launch-").FullName;
    private readonly FakePool _pool = new();
    private readonly List<(FactoryKitRequest Kit, Guid Session)> _downloads = new();
    private string? _refuseWith;

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { /* scratch folder */ }
    }

    private string KitRoot => Path.Combine(_scratch, "factory-run");

    private string Repo()
    {
        var repo = Path.Combine(_scratch, "repo");
        Directory.CreateDirectory(repo);
        return repo;
    }

    private sealed class FakePool : IWorktreePool
    {
        public readonly List<(string Repo, string Holder, int PoolSize)> Taken = new();
        public string? RefuseWith;

        public PooledWorktree Get(string repoPath, string holder, int poolSize)
        {
            Taken.Add((repoPath, holder, poolSize));
            if (RefuseWith is not null) throw new CcWorktreesRefusedException("pool-full", RefuseWith, 3, Array.Empty<string>());
            var slot = Path.Combine(Path.GetDirectoryName(repoPath)!, "repo.worktrees", "wt01");
            Directory.CreateDirectory(slot);
            return new PooledWorktree(repoPath, "wt01", slot, "lease-1");
        }

        public PooledWorktreeReturn Return(PooledWorktree worktree) => new(worktree.Slot, worktree.Path, Freed: true, HeldReason: null);
    }

    private SessionManager Manager(bool poolSettingOn = false)
    {
        var manager = new SessionManager(
            new AgentOptions { DefaultBufferSizeBytes = 65536, GracefulShutdownTimeoutSeconds = 2 },
            reservations: new WorktreeReservationStore(Path.Combine(_scratch, "reservations")),
            worktreePool: _pool,
            worktreePoolSetting: _ => new WorktreePoolSetting(Enabled: poolSettingOn, PoolSize: 6))
        {
            FactoryRunRoot = KitRoot,
            FactoryMemoryRoot = Path.Combine(_scratch, "factory-memory"),
        };
        manager.FactoryKitDownload = (kit, session) =>
        {
            _downloads.Add((kit, session));
            if (_refuseWith is not null) throw new InvalidOperationException(_refuseWith);
            return new FactoryKitResponse
            {
                Factory = kit.Factory,
                Seat = kit.Seat,
                Version = kit.Version,
                Kind = kit.Kind,
                Files = new List<FactoryKitFileDto>
                {
                    new() { Path = "kit.json", Text = "{\"files\":[{\"path\":\"brief.md\"},{\"path\":\"rules.json\"}]}\n" },
                    new() { Path = "rules.json", Text = "{\"permissions\":{\"deny\":[],\"ask\":[]}}\n" },
                    new() { Path = "brief.md", Text = $"Brief of version {kit.Version}.\n" },
                },
            };
        };
        return manager;
    }

    /// <summary>The create checks that the agent's executable is a file; it is never run on the Studio backend.</summary>
    private string Agent()
    {
        var exe = Path.Combine(_scratch, "cc-test-agent.exe");
        if (!File.Exists(exe)) File.WriteAllText(exe, "");
        return exe;
    }

    private Session Create(SessionManager manager, string repoPath, FactoryKitRequest? kit = null, bool force = false,
        Action<Session>? beforeLaunch = null)
        => manager.CreateSession(repoPath, new RawCliAgent(Agent()), userArgs: null, SessionBackendType.Studio,
            resumeSessionId: null, beforeLaunch: beforeLaunch, factoryKit: kit, forcePooledWorktree: force);

    private static StudioBackend Backend(Session session) => Assert.IsType<StudioBackend>(session.Backend);

    private static FactoryKitRequest Seat(bool home = false, string? returning = null) =>
        new("devthrottle", 4, "outreach", FactoryKits.Seat, home, returning);

    [Fact]
    public void CreateSession_ASeat_WritesTheKitBeforeTheAgentStartsAndNamesItInTheEnvironment()
    {
        using var manager = Manager();

        var session = Create(manager, Repo(), Seat());

        var kitDir = FactoryRunKit.DirectoryFor(session.Id, KitRoot);
        Assert.Equal(kitDir, session.FactoryKitDirectory);
        Assert.Equal("Brief of version 4.\n", File.ReadAllText(Path.Combine(kitDir, "brief.md")));
        Assert.True(Directory.Exists(Path.Combine(kitDir, "work")));
        Assert.Equal(kitDir, Backend(session).StartedEnvironment![FactoryRunKit.DirectoryEnvVar]);
        var (asked, forSession) = Assert.Single(_downloads);
        Assert.Equal(Seat(), asked);
        Assert.Equal(session.Id, forSession);
    }

    [Fact]
    public void CreateSession_AFactoryHomeSeat_WorksInTheKitsWorkFolderAndIgnoresTheRepositoryPath()
    {
        using var manager = Manager(poolSettingOn: true);

        // A path that does not exist: a seat with no repository is not run anywhere it names.
        var session = Create(manager, Path.Combine(_scratch, "no-such-folder"), Seat(home: true));

        var work = FactoryRunKit.WorkDirectoryOf(FactoryRunKit.DirectoryFor(session.Id, KitRoot));
        Assert.Equal(work, session.RepoPath);
        Assert.Equal(work, Backend(session).StartedWorkingDirectory);
        Assert.Empty(_pool.Taken);
        Assert.Null(session.PooledWorktree);
    }

    [Fact]
    public void CreateSession_ASeatInARepositoryWithThePoolSettingOff_TakesAPooledSlotOfTheSettingsSize()
    {
        using var manager = Manager(poolSettingOn: false);
        var repo = Repo();

        var session = Create(manager, repo, Seat(), force: true);

        var (taken, _, size) = Assert.Single(_pool.Taken);
        Assert.Equal(repo, taken);
        Assert.Equal(6, size);
        Assert.Equal("wt01", session.PooledWorktree!.Slot);
        Assert.Equal(session.PooledWorktree.Path, Backend(session).StartedWorkingDirectory);
        // The kit sits beside the worktree, never in it.
        Assert.False(session.FactoryKitDirectory!.StartsWith(session.PooledWorktree.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateSession_ARefusedKitDownload_StopsTheCreateWithTheReasonAndTakesNoSlot()
    {
        using var manager = Manager();
        _refuseWith = "the Gateway refused the kit (HTTP 409): version 4 of the definition of devthrottle is a draft";

        var ex = Assert.Throws<FactoryKitUnavailableException>(() => Create(manager, Repo(), Seat(), force: true));

        Assert.Equal("No session was opened. It is the seat 'outreach' of the factory 'devthrottle', and a seat never " +
                     "starts without its kit; the kit of version 4 could not be put in place: the Gateway refused the kit " +
                     "(HTTP 409): version 4 of the definition of devthrottle is a draft", ex.Message);
        Assert.Empty(_pool.Taken);
        Assert.Empty(manager.ListSessions());
        Assert.False(Directory.Exists(KitRoot) && Directory.EnumerateDirectories(KitRoot).Any());
    }

    [Fact]
    public void CreateSession_NoGatewayToDownloadFrom_StopsTheCreateAndSaysSo()
    {
        using var manager = Manager();
        manager.FactoryKitDownload = null;

        var ex = Assert.Throws<FactoryKitUnavailableException>(() => Create(manager, Repo(), Seat()));

        Assert.EndsWith("this Director is not connected to a Gateway, so there is nowhere to download the kit from", ex.Message);
    }

    [Fact]
    public void RemoveSession_ASeat_DeletesItsKitFolder()
    {
        using var manager = Manager();
        var session = Create(manager, Repo(), Seat(home: true));
        var dir = session.FactoryKitDirectory!;
        File.WriteAllText(Path.Combine(dir, "work", "draft.md"), "x");

        Assert.True(manager.RemoveSession(session.Id));

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void CreateSession_AForcedSeatWhosePoolIsRefused_IsTheSeatsRefusal_SayingASeatAlwaysWorksInAPooledWorktree()
    {
        using var manager = Manager(poolSettingOn: false);
        var repo = Repo();
        _pool.RefuseWith = "every slot is in use";

        // A refusal, not a fault (review finding 1): the Director answers it Conflict, and the Gateway a 409.
        var ex = Assert.Throws<FactorySeatRefusedException>(() => Create(manager, repo, Seat(), force: true));

        Assert.Equal($"No session was opened. A seat of a defined factory always works in a pooled worktree of {repo}, " +
                     "and cc-worktrees refused: every slot is in use.", ex.Message);
    }

    [Fact]
    public void CreateSession_UNDEFINED_FACTORY_ASessionWhoseOwnPoolSettingIsRefused_IsStillAFault()
    {
        using var manager = Manager(poolSettingOn: true);
        var repo = Repo();
        _pool.RefuseWith = "every slot is in use";

        var ex = Assert.Throws<InvalidOperationException>(() => Create(manager, repo));

        Assert.IsNotType<FactorySeatRefusedException>(ex);
        Assert.Equal($"No session was opened. {repo} is set to run its sessions in a pooled worktree, " +
                     "and cc-worktrees refused: every slot is in use.", ex.Message);
    }

    [Fact]
    public void CreateSession_AForcedPooledWorktreeWithNoSeat_IsRefusedBeforeAnythingStarts()
    {
        using var manager = Manager();

        var ex = Assert.Throws<ArgumentException>(() => Create(manager, Repo(), kit: null, force: true));

        Assert.StartsWith("A pooled worktree is forced only for a seat, which always comes with its kit.", ex.Message);
    }

    // The writer's refresh, reached through the create with the first session still alive. The restore that exists -
    // the session ended by the smart shutdown, its create built from the capture - is proven end to end in
    // FactoryKitSurvivesARestoreTests.
    [Fact]
    public void CreateSession_AFolderHandedBackWhileItsSeatIsStillAlive_IsRefreshedInPlaceWithItsWorkKept()
    {
        using var manager = Manager();
        var first = Create(manager, Repo(), Seat(home: true));
        var dir = first.FactoryKitDirectory!;
        File.WriteAllText(Path.Combine(dir, "work", "draft.md"), "what the seat was writing");
        var firstWork = first.RepoPath;

        // The restore builds a NEW session, handing back the folder the seat ran in.
        var restored = Create(manager, "", Seat(home: true, returning: dir));

        Assert.NotEqual(first.Id, restored.Id);
        Assert.Equal(dir, restored.FactoryKitDirectory);
        Assert.Equal(firstWork, restored.RepoPath);
        Assert.Equal(firstWork, Backend(restored).StartedWorkingDirectory);
        Assert.Equal("what the seat was writing", File.ReadAllText(Path.Combine(dir, "work", "draft.md")));
        Assert.Equal(dir, Backend(restored).StartedEnvironment![FactoryRunKit.DirectoryEnvVar]);
    }

    [Fact]
    public void CreateSession_ARestoreHandingBackAFolderOutsideTheKitRoot_IsRefused()
    {
        using var manager = Manager();
        var outside = Path.Combine(_scratch, "elsewhere", Guid.NewGuid().ToString());

        var ex = Assert.Throws<FactoryKitUnavailableException>(() => Create(manager, Repo(), Seat(returning: outside)));

        Assert.Contains("the kit folder handed back by the restore is refused.", ex.Message);
        Assert.Contains("is not directly under", ex.Message);
        Assert.Empty(_downloads);
    }

    // WHAT A CHECK FOUND (phase 5b, design 3.4): check.json in the kit, listed in kit.json, before the agent starts.

    private static FactoryCheckDto TheCheck() => new()
    {
        CheckPath = "checks/new_mail.py",
        Count = 3,
        Context = "three unread mails from customers",
        CheckedUtc = new DateTime(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc),
        DefinitionVersion = 4,
        Seat = "outreach",
        Start = 2,
    };

    private const string TheCheckJson =
        "{\n  \"checkPath\": \"checks/new_mail.py\",\n  \"count\": 3,\n  \"context\": \"three unread mails from customers\",\n" +
        "  \"checkedUtc\": \"2026-10-15T08:00:00Z\",\n  \"definitionVersion\": 4,\n  \"seat\": \"outreach\",\n  \"start\": 2\n}\n";

    private static FactoryKitRequest CheckedSeat(bool home = false, string? returning = null) =>
        new("devthrottle", 4, "outreach", FactoryKits.Seat, home, returning, TheCheck());

    private static string? CheckJsonIn(Session session)
    {
        var path = Path.Combine(session.FactoryKitDirectory!, FactoryRunKit.CheckFileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Fact]
    public void CreateSession_ASeatStartedByACheck_HasCheckJsonWithExactlyWhatTheCheckFoundBeforeTheAgentStarts()
    {
        using var manager = Manager();
        string? atLaunch = null;

        var session = Create(manager, Repo(), CheckedSeat(), beforeLaunch: s => atLaunch = CheckJsonIn(s));

        Assert.Equal(TheCheckJson, atLaunch);
        Assert.Equal(TheCheckJson, CheckJsonIn(session));
        var kitJson = File.ReadAllText(Path.Combine(session.FactoryKitDirectory!, "kit.json"));
        Assert.Contains("\"path\": \"check.json\",\n      \"source\": \"check\",\n      \"from\": [\n        \"checks/new_mail.py\"", kitJson);
    }

    [Fact]
    public void CreateSession_UNDEFINED_FACTORY_ASeatStartedWithNoCheck_HasNoCheckJsonAndItsKitJsonAsSent()
    {
        using var manager = Manager();

        var session = Create(manager, Repo(), Seat());

        Assert.Null(CheckJsonIn(session));
        Assert.Equal("{\"files\":[{\"path\":\"brief.md\"},{\"path\":\"rules.json\"}]}\n",
            File.ReadAllText(Path.Combine(session.FactoryKitDirectory!, "kit.json")));
    }

    [Fact]
    public void RemoveSession_ASeatStartedByACheck_DeletesCheckJsonWithTheKit()
    {
        using var manager = Manager();
        var session = Create(manager, Repo(), CheckedSeat(home: true));
        var dir = session.FactoryKitDirectory!;
        Assert.NotNull(CheckJsonIn(session));

        Assert.True(manager.RemoveSession(session.Id));

        Assert.False(File.Exists(Path.Combine(dir, FactoryRunKit.CheckFileName)));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void CreateSession_ARestoredSeatThatACheckStarted_KeepsItsCheckJsonAndKitJsonStillListsIt()
    {
        using var manager = Manager();
        var first = Create(manager, Repo(), CheckedSeat(home: true));
        var dir = first.FactoryKitDirectory!;

        // A restore's create carries no check: the capture does not hold one. The folder does.
        var restored = Create(manager, "", Seat(home: true, returning: dir));

        Assert.Equal(TheCheckJson, CheckJsonIn(restored));
        Assert.Contains("\"path\": \"check.json\"", File.ReadAllText(Path.Combine(dir, "kit.json")));
    }

    [Fact]
    public void CreateSession_ARestoredSeatWhoseCheckJsonTheAgentDamaged_StillStartsWithTheFileAsItWas()
    {
        using var manager = Manager();
        var first = Create(manager, Repo(), CheckedSeat(home: true));
        var dir = first.FactoryKitDirectory!;
        File.WriteAllText(Path.Combine(dir, FactoryRunKit.CheckFileName), "{ the agent wrote over this");

        var restored = Create(manager, "", Seat(home: true, returning: dir));

        Assert.Equal(dir, restored.FactoryKitDirectory);
        Assert.Equal("{ the agent wrote over this", CheckJsonIn(restored));
        Assert.Contains("\"path\": \"check.json\"", File.ReadAllText(Path.Combine(dir, "kit.json")));
    }

    [Fact]
    public void CreateSession_ACheckWhoseKitHasNoListOfFiles_StopsTheCreateAndWritesNoKit()
    {
        using var manager = Manager();
        var download = manager.FactoryKitDownload!;
        manager.FactoryKitDownload = (kit, session) =>
        {
            var composed = download(kit, session);
            composed.Files[0].Text = "{}\n";
            return composed;
        };

        var ex = Assert.Throws<FactoryKitUnavailableException>(() => Create(manager, Repo(), CheckedSeat()));

        Assert.EndsWith("The kit's kit.json has no list of files to name check.json in, so it was not written.", ex.Message);
        Assert.Empty(manager.ListSessions());
        Assert.False(Directory.Exists(KitRoot) && Directory.EnumerateDirectories(KitRoot).Any());
    }

    [Fact]
    public void CreateSession_UNDEFINED_FACTORY_ASessionThatIsNotASeat_GetsNoKitAndNoVariable()
    {
        using var manager = Manager();
        var repo = Repo();

        var session = Create(manager, repo);

        Assert.Null(session.FactoryKitDirectory);
        Assert.Empty(_downloads);
        Assert.Empty(_pool.Taken);
        Assert.Equal(repo, Backend(session).StartedWorkingDirectory);
        Assert.False(Backend(session).StartedEnvironment!.ContainsKey(FactoryRunKit.DirectoryEnvVar));
    }
}
