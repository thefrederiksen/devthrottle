using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using Xunit;

namespace CcDirector.Gateway.UnitTests;

/// <summary>
/// THE DIRECTOR ANSWERS A START AT ONCE AND NEVER BLOCKS ITS COMMAND CHANNEL (factory failures of 6 November 2026,
/// findings 1, fixes A and B).
///
/// On 6 November a create for a seat in cc-consult waited 430 seconds inside `cc-worktrees get` on the thread that
/// delivered it: the Gateway gave up at 30 seconds, no other command reached the Director from 08:06:49 to 08:14:04, and
/// the trigger's lock lapsed so a second writer was started. These tests drive the command handler the SignalR client
/// calls (<see cref="GatewayStreamClient.HandleCommandAsync"/>) the way its delivery loop does - one call after another
/// on one thread - against a pool that waits on a gate the test opens, so nothing here depends on time.
/// </summary>
public sealed class CreateAnswersAtOnceTests : IDisposable
{
    private const string DirectorId = "dir-start-at-once";
    private const string ToolRefusal = "pool full (4 of 4): every slot is held";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _scratch = Directory.CreateTempSubdirectory("dt-create-at-once-").FullName;
    private readonly GatedPool _pool = new();
    private readonly SessionManager _manager;

    public CreateAnswersAtOnceTests()
    {
        _manager = new SessionManager(
            new AgentOptions { DefaultBufferSizeBytes = 65536, GracefulShutdownTimeoutSeconds = 2 },
            reservations: new WorktreeReservationStore(Path.Combine(_scratch, "reservations")),
            worktreePool: _pool,
            worktreePoolSetting: _ => new WorktreePoolSetting(Enabled: true, PoolSize: 4))
        {
            FactoryRunRoot = Path.Combine(_scratch, "factory-run"),
            FactoryMemoryRoot = Path.Combine(_scratch, "factory-memory"),
            ClaudeHooksDirectory = Path.Combine(_scratch, "claude-hooks"),
            SessionPreambleDirectory = Path.Combine(_scratch, "session-preambles"),
            SessionPointerDirectory = Path.Combine(_scratch, "session-pointers"),
        };
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { /* scratch folder */ }
    }

    /// <summary>True on a thread only while it is inside a delivery (<see cref="Deliver"/>). Per thread, and it does
    /// not flow into tasks, so the pool reads true only when it is asked inside the delivering call itself.</summary>
    [ThreadStatic] private static bool _inDelivery;

    /// <summary>A pool whose answer the test gives. It records whether it was asked inside the delivering call.</summary>
    private sealed class GatedPool : IWorktreePool
    {
        private readonly TaskCompletionSource<PooledWorktree> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _askedInsideDelivery = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the start asks for its slot; true when that was inside the delivering call.</summary>
        public Task<bool> AskedInsideDelivery => _askedInsideDelivery.Task;

        public Task<PooledWorktree> GetAsync(string repoPath, string holder, int poolSize, CancellationToken cancellationToken)
        {
            _askedInsideDelivery.TrySetResult(_inDelivery);
            return _gate.Task;
        }

        /// <summary>True once the test has given the pool's answer.</summary>
        public bool Answered => _gate.Task.IsCompleted;

        public void Refuse(string message) =>
            _gate.TrySetException(new CcWorktreesRefusedException("pool-full", message, 4, new[] { "cc-worktrees list --repo <repo>" }));

        public PooledWorktreeReturn Return(PooledWorktree worktree) => new(worktree.Slot, worktree.Path, Freed: true, HeldReason: null);
    }

    private DirectorCommand CreateCommand()
    {
        var repo = Path.Combine(_scratch, "cc-consult");
        Directory.CreateDirectory(repo);
        var agent = Path.Combine(_scratch, "cc-test-agent.exe");
        if (!File.Exists(agent)) File.WriteAllText(agent, "");
        return new DirectorCommand
        {
            CommandId = "cmd-create",
            Verb = "create",
            SessionId = "",
            PayloadJson = JsonSerializer.Serialize(new NewSessionRequest
            {
                RepoPath = repo,
                Agent = "RawCli",
                Command = agent,
                Name = "writer - dev-sales-writer-start-1 - 2026-11-06 08:06",
                Origin = SessionOriginKinds.Schedule,
                OriginSurface = SessionOriginSurfaces.Trigger,
                Factory = "dev-sales",
            }, Web),
        };
    }

    /// <summary>One delivery, as the SignalR client makes it: the handler is called and its task taken.</summary>
    private Task<DirectorCommandResult> Deliver(DirectorCommand command, SessionCommandServices? services = null)
    {
        _inDelivery = true;
        try
        {
            return GatewayStreamClient.HandleCommandAsync(command, upStreamHandler: null,
                commandDispatcher: c => SessionCommandExecutor.DispatchAsync(_manager, DirectorId, c, services));
        }
        finally
        {
            _inDelivery = false;
        }
    }

    private static SessionDto RowOf(DirectorCommandResult result)
    {
        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        return JsonSerializer.Deserialize<SessionDto>(result.BodyJson!, Web)
               ?? throw new InvalidOperationException("the create answered without a session row");
    }

    private async Task<PendingSessionStart> RefuseAndWaitForTheFailedRowAsync()
    {
        var failed = new TaskCompletionSource<PendingSessionStart>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.OnPendingStartChanged += p => { if (p.FailedAtUtc is not null) failed.TrySetResult(p); };
        _pool.Refuse(ToolRefusal);
        return await failed.Task;
    }

    [Fact]
    public async Task Create_PoolWaitingOnAGate_AnswersOkWithTheStartingRow()
    {
        // Act: delivered on this thread, as the client's loop delivers it.
        var answer = Deliver(CreateCommand());

        // Assert: answered while the pool still holds the start (the test never opens its gate before the answer), with the
        // session's id and name in the Starting state - and the pool was asked outside the delivering call, never inside it.
        var row = RowOf(await answer);
        Assert.False(_pool.Answered);
        Assert.Equal("Starting", row.ActivityState);
        Assert.Equal("writer - dev-sales-writer-start-1 - 2026-11-06 08:06", row.Name);
        Assert.Equal("dev-sales", row.Factory);
        Assert.Equal(SessionOriginSurfaces.Trigger, row.OriginSurface);
        Assert.False(row.Crashed);
        Assert.False(await _pool.AskedInsideDelivery);

        await RefuseAndWaitForTheFailedRowAsync();
    }

    [Fact]
    public async Task Create_HeldInThePool_DoesNotDelayASecondCommand()
    {
        // Arrange: the first command's start is held in the pool.
        var created = RowOf(await Deliver(CreateCommand()));
        await _pool.AskedInsideDelivery;

        // Act: a second command on the same channel, aimed at the session still starting.
        var second = Deliver(new DirectorCommand { CommandId = "cmd-kill", Verb = "kill", SessionId = created.SessionId });

        // Assert: answered while the first is still in the pool - refused in words, because the session is still starting.
        Assert.True(second.IsCompleted);
        var refused = await second;
        Assert.Equal(DirectorCommandStatus.Conflict, refused.Status);
        Assert.Contains("still starting", refused.Error);
        Assert.Null(_manager.GetPendingStart(Guid.Parse(created.SessionId))!.FailedAtUtc);

        await RefuseAndWaitForTheFailedRowAsync();
    }

    [Fact]
    public async Task Create_ReservedName_IsInTheSnapshotTheGatewayReadsAndReadsAsAlive()
    {
        // Act
        var created = RowOf(await Deliver(CreateCommand()));
        await _pool.AskedInsideDelivery;

        // Assert: the row the Gateway is pushed carries the id and the name while the start runs, and the Gateway's
        // one-at-a-time locks read a Starting row as alive (TriggerService.IsLastSessionAlive, via IsGone).
        var row = Assert.Single(ControlApiHost.BuildSessionSnapshot(_manager, DirectorId));
        Assert.Equal(created.SessionId, row.SessionId);
        Assert.Equal(created.Name, row.Name);
        Assert.Equal("Starting", row.ActivityState);
        Assert.False(FleetManagerSessions.IsGone(row));

        await RefuseAndWaitForTheFailedRowAsync();
    }

    [Fact]
    public async Task Create_PoolRefuses_TheRowEndsFailedWithTheToolsWordsAndReadsAsGone()
    {
        // Arrange
        var created = RowOf(await Deliver(CreateCommand()));
        await _pool.AskedInsideDelivery;

        // Act
        await RefuseAndWaitForTheFailedRowAsync();

        // Assert: the Gateway is pushed a crashed row whose first-prompt failure carries cc-worktrees' own sentence -
        // the field a schedule's run result already turns into a recorded problem - and the lock reads it as gone.
        var row = Assert.Single(ControlApiHost.BuildSessionSnapshot(_manager, DirectorId));
        Assert.Equal(created.SessionId, row.SessionId);
        Assert.True(row.Crashed);
        Assert.Equal("Exited", row.ActivityState);
        Assert.NotNull(row.FirstPromptFailedAtUtc);
        Assert.Contains(ToolRefusal, row.FirstPromptFailureReason);
        Assert.True(row.StartFailed);
        Assert.True(FleetManagerSessions.IsGone(row));
    }

    [Fact]
    public async Task Kill_AFailedStart_ClosesItsRow()
    {
        // Arrange
        var created = RowOf(await Deliver(CreateCommand()));
        await _pool.AskedInsideDelivery;
        await RefuseAndWaitForTheFailedRowAsync();

        // Act
        var answer = await Deliver(new DirectorCommand { CommandId = "cmd-close", Verb = "kill", SessionId = created.SessionId });

        // Assert
        Assert.Equal(DirectorCommandStatus.Ok, answer.Status);
        var stop = JsonSerializer.Deserialize<DirectorStopResult>(answer.BodyJson!, Web)!;
        Assert.True(stop.RowRemoved);
        Assert.Empty(ControlApiHost.BuildSessionSnapshot(_manager, DirectorId));
    }
    // ---- a seat in a repository (review of pull request 4755, findings 1 and 2) ----------------------------------------

    /// <summary>A seat of dev-sales that works in cc-consult, whose kit lists <paramref name="secrets"/>; the machine
    /// holds none of them. Returns the command and the services that know the repository.</summary>
    private (DirectorCommand Command, SessionCommandServices Services) SeatCreate(string secrets)
    {
        var repo = Path.Combine(_scratch, "cc-consult");
        Directory.CreateDirectory(repo);
        var registry = new RepositoryRegistry(Path.Combine(_scratch, "config", "repositories.json"));
        registry.Load();
        Assert.True(registry.TryAdd(repo));
        var agent = Path.Combine(_scratch, "cc-test-agent.exe");
        if (!File.Exists(agent)) File.WriteAllText(agent, "");

        _manager.FactorySecretsQuestion = new FactoryMachineQuestion(
            _ => Task.FromResult(new CcDirector.Core.Utilities.ProcessRunner.Result(0, "{\"entries\": []}", "", Started: true)),
            TimeSpan.FromSeconds(10));
        _manager.FactoryKitDownload = (kit, _) => new FactoryKitResponse
        {
            Factory = kit.Factory, Seat = kit.Seat, Version = kit.Version, Kind = kit.Kind,
            Files = new List<FactoryKitFileDto>
            {
                new() { Path = "kit.json", Text = "{\"kind\":\"seat\",\"secrets\":[" + secrets + "]}\n" },
                new() { Path = "rules.json", Text = "{\"permissions\":{\"ask\":[]}}\n" },
                new() { Path = "brief.md", Text = "Brief.\n" },
            },
        };

        var command = new DirectorCommand
        {
            CommandId = "cmd-seat",
            Verb = "create",
            SessionId = "",
            PayloadJson = JsonSerializer.Serialize(new NewSessionRequest
            {
                Agent = "RawCli",
                Command = agent,
                Name = "writer - dev-sales - 2026-11-06 08:06",
                Origin = SessionOriginKinds.Schedule,
                OriginSurface = SessionOriginSurfaces.Trigger,
                Factory = "dev-sales",
                FactorySeat = "writer",
                FactoryDefinitionVersion = 4,
                FactoryKit = FactoryKits.Seat,
                FactoryWorktree = "cc-consult",
            }, Web),
        };
        return (command, new SessionCommandServices { Repositories = registry });
    }

    [Fact]
    public async Task Create_ASeatInARepositoryWhoseSecretIsNotHeld_IsAnsweredAsARefusalBeforeThePoolIsAsked()
    {
        // Arrange
        var (command, services) = SeatCreate("\"crm-token\"");

        // Act
        var answer = await Deliver(command, services);

        // Assert: still the Conflict the spawn door turns into its 409, in the seat's own words - not a Starting row
        // that fails minutes later - and nothing was taken from the pool or left on the roster.
        Assert.Equal(DirectorCommandStatus.Conflict, answer.Status);
        Assert.Contains("crm-token", answer.Error);
        Assert.False(_pool.AskedInsideDelivery.IsCompleted);
        Assert.Empty(ControlApiHost.BuildSessionSnapshot(_manager, DirectorId));
    }

    [Fact]
    public async Task Create_ASeatInARepositoryHeldInThePool_IsAnsweredWithAStartingRowThatCarriesItsSeat()
    {
        // Arrange
        var (command, services) = SeatCreate("");

        // Act
        var row = RowOf(await Deliver(command, services));

        // Assert: answered while the pool holds the start, and the row names the seat and its kit, which is what the
        // Gateway seats the session by and recognises the seat by.
        Assert.False(await _pool.AskedInsideDelivery);
        Assert.Equal("Starting", row.ActivityState);
        Assert.Equal("writer", row.FactorySeat);
        Assert.Equal(4, row.FactoryDefinitionVersion);
        Assert.Equal(FactoryKits.Seat, row.FactoryKit);
        Assert.Equal("terminal", row.ConnectionMode);

        await RefuseAndWaitForTheFailedRowAsync();
    }
}
