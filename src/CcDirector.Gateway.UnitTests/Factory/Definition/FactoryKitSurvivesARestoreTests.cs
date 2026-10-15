using CcDirector.ControlApi;
using CcDirector.ControlApi.Drain;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.UnitTests.Restart;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// A CAPTURED SEAT GETS ITS KIT FOLDER BACK ON THE RESTORE THAT EXISTS (Factory Definition mission, phase 5, review of
/// pull request 4041, finding 1). The real sequence, link by link: the create verb's own settle step and the real
/// <see cref="SessionManager.CreateSession"/>; the capture the Gateway makes from the pushed session
/// (<see cref="ControlEndpoints.Map"/> then <see cref="WorkspaceCapture.CaptureSeat"/>); the end the smart shutdown issues,
/// through the drain's own seam and the kill verb; the restore's create built by <see cref="DirectorRestore.BuildRequest"/>
/// from that captured seat; and the create again. It reads the folder and the environment the restored agent was given.
///
/// The sessions start on the Studio backend, whose start runs no process: the verb's create starts a real terminal, so
/// the test runs the verb's settle step and the SessionManager create the verb calls, with the verb's own stamping.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryKitSurvivesARestoreTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dt-kit-restore-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { /* scratch folder */ }
    }

    private string KitRoot => Path.Combine(_scratch, "factory-run");

    private SessionManager Manager()
    {
        var manager = new SessionManager(new AgentOptions { DefaultBufferSizeBytes = 65536, GracefulShutdownTimeoutSeconds = 2 })
        {
            FactoryRunRoot = KitRoot,
            FactoryMemoryRoot = Path.Combine(_scratch, "factory-memory"),
        };
        manager.FactoryMemoryDownload = (_, _) => Array.Empty<FactoryMemoryNoteDto>();
        manager.FactoryKitDownload = (kit, _) => new FactoryKitResponse
        {
            Factory = kit.Factory,
            Seat = kit.Seat,
            Version = kit.Version,
            Kind = kit.Kind,
            Files = new List<FactoryKitFileDto>
            {
                // Every kit the Gateway composes carries kit.json and rules.json; the launch reads both.
                new() { Path = "kit.json", Text = "{}\n" },
                new() { Path = "rules.json", Text = "{\"permissions\":{\"deny\":[],\"ask\":[]}}\n" },
                new() { Path = "brief.md", Text = $"Brief of version {kit.Version}.\n" },
            },
        };
        return manager;
    }

    private string Agent()
    {
        var exe = Path.Combine(_scratch, "cc-test-agent.exe");
        if (!File.Exists(exe)) File.WriteAllText(exe, "");
        return exe;
    }

    /// <summary>What the create verb does with a request, on the Studio backend.</summary>
    private Session CreateAsTheVerbDoes(SessionManager manager, NewSessionRequest req)
    {
        var refusal = SessionCommandExecutor.SettleSeatStart(req, new List<RepositoryConfig>(), "SOREN_NORTH",
            out var kit, out var force);
        Assert.Null(refusal);
        return manager.CreateSession(req.RepoPath ?? "", new RawCliAgent(Agent()), userArgs: null, SessionBackendType.Studio,
            resumeSessionId: null,
            beforeLaunch: s => { s.StampFactory(req.Factory); SessionCommandExecutor.StampSeatStartFacts(s, req); },
            factoryKit: kit, forcePooledWorktree: force);
    }

    private static NewSessionRequest AHomeSeat() => new()
    {
        Agent = "RawCli",
        RepoPath = "",
        Factory = "devthrottle",
        FactorySeat = "outreach",
        FactoryDefinitionVersion = 4,
        FactoryKit = FactoryKits.Seat,
        FactoryHome = true,
    };

    /// <summary>The seat as the capture records it, with the handover the drain notes when the seat hands over.</summary>
    private static WorkspaceSeat Captured(SessionDto pushed)
    {
        var seat = WorkspaceCapture.CaptureSeat(pushed, sortOrder: 0);
        seat.HandoverPath = @"C:\drain\handovers\outreach.md";
        return seat;
    }

    private static StudioBackend Backend(Session session) => Assert.IsType<StudioBackend>(session.Backend);

    [Fact]
    public async Task ARestoredHomeSeat_EndedByTheSmartShutdownAndRestoredFromItsCapture_ResumesInItsFolderWithItsWork()
    {
        using var manager = Manager();
        var first = CreateAsTheVerbDoes(manager, AHomeSeat());
        var dir = first.FactoryKitDirectory!;
        File.WriteAllText(Path.Combine(dir, "work", "draft.md"), "what the seat was writing");

        // The capture: the Gateway records the session the Director pushed.
        var seat = Captured(ControlEndpoints.Map(first, "dir-1"));
        Assert.True(seat.FactoryHome);
        Assert.Equal(dir, seat.FactoryKitDirectory);

        // The end the smart shutdown issues at its limit, through the drain's seam and the kill verb.
        var end = await new SessionManagerDrainControl(manager).EndAsync(first.Id.ToString(), "Smart shutdown: the limit.",
            keepKitForRestore: true);
        Assert.True(end.Ended);
        Assert.Null(manager.GetSession(first.Id));
        Assert.Equal("what the seat was writing", File.ReadAllText(Path.Combine(dir, "work", "draft.md")));

        // The restore's create, built from the captured seat, and the create again.
        var req = DirectorRestore.BuildRequest(seat, owner: null, new WorkspaceRestoreOrder { WorkspaceId = "w-1" });
        Assert.True(req.FactoryHome);
        var restored = CreateAsTheVerbDoes(manager, req);

        Assert.Equal(dir, restored.FactoryKitDirectory);
        Assert.True(restored.FactoryHome);
        Assert.Equal(FactoryRunKit.WorkDirectoryOf(dir), Backend(restored).StartedWorkingDirectory);
        Assert.Equal(dir, Backend(restored).StartedEnvironment![FactoryRunKit.DirectoryEnvVar]);
        Assert.Equal("what the seat was writing", File.ReadAllText(Path.Combine(dir, "work", "draft.md")));
        Assert.Equal("Brief of version 4.\n", File.ReadAllText(Path.Combine(dir, "brief.md")));
    }

    [Fact]
    public async Task AnEndThatIsNotForARestore_DeletesTheKitFolder()
    {
        using var manager = Manager();
        var session = CreateAsTheVerbDoes(manager, AHomeSeat());
        var dir = session.FactoryKitDirectory!;

        var end = await new SessionManagerDrainControl(manager).EndAsync(session.Id.ToString(), "Shut down and ignore all sessions.",
            keepKitForRestore: false);

        Assert.True(end.Ended);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void ASeatTheDrainFlagsAfterItsHandover_KeepsItsKit_AndACancelTakesTheKeepBack()
    {
        using var manager = Manager();
        var session = CreateAsTheVerbDoes(manager, AHomeSeat());
        var control = new SessionManagerDrainControl(manager);

        Assert.True(control.MarkForDeletion(session.Id.ToString(), "Director restart: handover written and read."));
        Assert.True(session.FactoryKitKeptForRestore);

        Assert.True(control.CancelDeletion(session.Id.ToString()));
        Assert.False(session.FactoryKitKeptForRestore);
    }

    [Fact]
    public void ARestoreFromASeatWhoseFolderIsGone_GivesItAFreshKitAndAnEmptyWorkFolder()
    {
        using var manager = Manager();
        var first = CreateAsTheVerbDoes(manager, AHomeSeat());
        var seat = Captured(ControlEndpoints.Map(first, "dir-1"));
        Assert.True(manager.RemoveSession(first.Id));
        Assert.False(Directory.Exists(seat.FactoryKitDirectory));

        var restored = CreateAsTheVerbDoes(manager,
            DirectorRestore.BuildRequest(seat, owner: null, new WorkspaceRestoreOrder { WorkspaceId = "w-1" }));

        Assert.Equal(seat.FactoryKitDirectory, restored.FactoryKitDirectory);
        Assert.True(Directory.Exists(FactoryRunKit.WorkDirectoryOf(seat.FactoryKitDirectory!)));
    }

    // ---- the start-up sweep ----

    private string AFolder(Guid id)
    {
        var dir = FactoryRunKit.DirectoryFor(id, KitRoot);
        Directory.CreateDirectory(Path.Combine(dir, "work"));
        return dir;
    }

    [Fact]
    public async Task TheStartUpSweep_DeletesOnlyAFolderNothingNames()
    {
        using var manager = Manager();
        var live = CreateAsTheVerbDoes(manager, AHomeSeat()).FactoryKitDirectory!;
        var captured = AFolder(Guid.NewGuid());
        var persisted = AFolder(Guid.NewGuid());
        var journaled = Guid.NewGuid();
        var inJournal = AFolder(journaled);
        var orphan = AFolder(Guid.NewGuid());
        var notOurs = Path.Combine(KitRoot, "someone-elses-folder");
        Directory.CreateDirectory(notOurs);
        var gateway = new FakeWayUpGateway().With(new WorkspaceDocument
        {
            Id = "w-1",
            Origin = WorkspaceOrigins.Captured,
            Machine = "SOREN_NORTH",
            Seats = new List<WorkspaceSeat> { new() { SessionId = "a-seat-id", FactoryKitDirectory = captured } },
        });

        var deleted = await FactoryRunSweep.RunAsync(manager, gateway, "SOREN_NORTH",
            () => new[] { persisted }, () => new[] { journaled.ToString() }, CancellationToken.None);

        Assert.Equal(new[] { orphan }, deleted);
        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(live));
        Assert.True(Directory.Exists(captured));
        Assert.True(Directory.Exists(persisted));
        Assert.True(Directory.Exists(inJournal));
        Assert.True(Directory.Exists(notOurs));
    }

    [Fact]
    public async Task TheStartUpSweep_WhenTheGatewayCannotBeRead_DeletesNothing()
    {
        using var manager = Manager();
        var orphan = AFolder(Guid.NewGuid());
        var gateway = new FakeWayUpGateway { Unreachable = "the Gateway did not answer" };

        var deleted = await FactoryRunSweep.RunAsync(manager, gateway, "SOREN_NORTH",
            () => Array.Empty<string>(), () => Array.Empty<string>(), CancellationToken.None);

        Assert.Null(deleted);
        Assert.True(Directory.Exists(orphan));
    }

    [Fact]
    public async Task TheStartUpSweep_WhenACrashJournalCannotBeRead_DeletesNothing()
    {
        using var manager = Manager();
        var orphan = AFolder(Guid.NewGuid());

        var deleted = await FactoryRunSweep.RunAsync(manager, new FakeWayUpGateway(), "SOREN_NORTH",
            () => Array.Empty<string>(), () => throw new InvalidOperationException("the crash journal x could not be read"),
            CancellationToken.None);

        Assert.Null(deleted);
        Assert.True(Directory.Exists(orphan));
    }

    // ---- the home seat is a birth fact on every restore carrier ----

    [Fact]
    public void AHomeSeat_IsCarriedByThePushTheCaptureAndBothRestoreBuilders()
    {
        using var manager = Manager();
        var session = CreateAsTheVerbDoes(manager, AHomeSeat());

        var pushed = ControlEndpoints.Map(session, "dir-1");
        var seat = Captured(pushed);

        Assert.True(pushed.FactoryHome);
        Assert.True(seat.FactoryHome);
        Assert.True(DirectorRestore.BuildRequest(seat, null, new WorkspaceRestoreOrder { WorkspaceId = "w-1" }).FactoryHome);
        Assert.True(ControlApi.SmartRestart.DirectorWayUp.BuildReopen(new WorkspaceDocument { Id = "w-1" }, seat).FactoryHome);
    }

    [Fact]
    public void ASessionThatIsNotAHomeSeat_UNDEFINED_FACTORY_CarriesNoHome()
    {
        using var manager = Manager();
        var repo = Directory.CreateDirectory(Path.Combine(_scratch, "repo")).FullName;
        var session = CreateAsTheVerbDoes(manager, new NewSessionRequest { Agent = "RawCli", RepoPath = repo });

        var seat = Captured(ControlEndpoints.Map(session, "dir-1"));

        Assert.Null(seat.FactoryHome);
        Assert.Null(DirectorRestore.BuildRequest(seat, null, new WorkspaceRestoreOrder { WorkspaceId = "w-1" }).FactoryHome);
    }
}
