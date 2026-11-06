using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workspaces;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// A VERIFIED RESTORE OF A BOSS IS STILL THE BOSS, AND A RESTORED CHILD IS STILL A CHILD (issue #4598, review finding
/// 1). The record of a Boss started for a session in no factory travels to the session a restore starts in its place
/// only when the captured session itself carried it - never because of who ordered the restore.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryBossStartOnRestoreTests : IDisposable
{
    private const string TheFactory = "website-factory";
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _h = new();
    private readonly SessionHistoryStore _history;
    private readonly DirectorSpawnDoor _door;

    private const string Workspace = "drain";
    private const string DirectorA = "director-a";
    private readonly WorkspaceStore _workspaces;

    public FactoryBossStartOnRestoreTests()
    {
        _history = new SessionHistoryStore(_h.Open());
        _workspaces = new WorkspaceStore(_h.Open());
        _door = new DirectorSpawnDoor(null!, null!, _history.FactoryOf, null, null, null, _workspaces,
            RecordBossStart: (sid, factory, seat, by, kind) => _history.RecordBossStart(sid, factory, seat, by, kind, Now),
            BossStartOf: _history.BossStartOf);
    }

    /// <summary>A drained workspace whose Boss seat Director A has started with the token "token-1".</summary>
    private void StartedRestoreOf(string seat)
    {
        _workspaces.Create(new WorkspaceDocument
        {
            Id = Workspace, Name = "Drain", Origin = WorkspaceOrigins.Captured, Machine = "MAC", DirectorId = "old",
            Seats = new()
            {
                new WorkspaceSeat
                {
                    SessionId = seat, Name = "Boss", Agent = "ClaudeCode", RepoPath = "/repos/x",
                    DrainState = WorkspaceDrainStates.Drained, HandoverPath = "/handovers/boss.md",
                    Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Restore, Why = "test", Command = "x" },
                },
            },
            RestoreAfterRestart = new() { seat },
        }, Now);
        _workspaces.TakeRestoreLease(Workspace, DirectorA, null, Now);
        _workspaces.RecordRestoreMark(Workspace, new WorkspaceRestoreMark
        {
            DirectorId = DirectorA, Kind = WorkspaceRestoreMarkKinds.Started, SeatSessionId = seat, Token = "token-1",
        }, Now);
    }

    public void Dispose() => _h.Dispose();

    private static NewSessionRequest RestoredAs(string seat, string kit = FactoryKits.RulesOnly) => new()
    {
        RepoPath = Path.GetTempPath(),
        Factory = TheFactory,
        FactorySeat = seat,
        FactoryDefinitionVersion = 4,
        FactoryKit = kit,
    };

    private static WorkspaceRestoreClaim Claim(string captured) => new() { WorkspaceId = "ws", SeatSessionId = captured, Token = "t" };

    [Fact]
    public void A_RESTORED_BOSS_CARRIES_ITS_RECORD_to_the_new_session()
    {
        _history.RecordBossStart("old-boss", TheFactory, "boss", "owners-agent", FactoryBossStartKinds.Started, Now);

        GatewayEndpoints.CarryBossStartOnRestore(Claim("old-boss"), RestoredAs("boss"), new SessionDto { SessionId = "new-boss" },
            _door, "test");

        Assert.Equal((TheFactory, "boss"), _history.BossStartOf("new-boss"));
    }

    [Fact]
    public void A_RESTORED_CHILD_OF_THE_BOSS_HAS_NO_RECORD_TO_CARRY_and_stays_inherited()
    {
        _history.RecordBossStart("old-boss", TheFactory, "boss", "owners-agent", FactoryBossStartKinds.Started, Now);

        GatewayEndpoints.CarryBossStartOnRestore(Claim("old-child"), RestoredAs("boss"), new SessionDto { SessionId = "new-child" },
            _door, "test");

        Assert.Null(_history.BossStartOf("new-child"));
    }

    [Fact]
    public void A_RESTORE_CLAIM_WITH_THE_WRONG_TOKEN_WRITES_NO_RECORD_and_the_right_one_does()
    {
        _history.RecordBossStart("old-boss", TheFactory, "boss", "owners-agent", FactoryBossStartKinds.Started, Now);
        StartedRestoreOf("old-boss");

        var forged = new WorkspaceRestoreClaim { WorkspaceId = Workspace, SeatSessionId = "old-boss", Token = "guessed" };
        Assert.False(GatewayEndpoints.RecordRestoreThenCarryBoss(forged, DirectorA, RestoredAs("boss"),
            new SessionDto { SessionId = "forged" }, _door, "test"));
        Assert.Null(_history.BossStartOf("forged"));

        var real = new WorkspaceRestoreClaim { WorkspaceId = Workspace, SeatSessionId = "old-boss", Token = "token-1" };
        Assert.True(GatewayEndpoints.RecordRestoreThenCarryBoss(real, DirectorA, RestoredAs("boss"),
            new SessionDto { SessionId = "new-boss" }, _door, "test"));
        Assert.Equal((TheFactory, "boss"), _history.BossStartOf("new-boss"));
    }

    private static WorkspaceDocument ReopenedDoc(string seat, string? reopenedAs) => new()
    {
        Id = Workspace,
        Seats = new()
        {
            new WorkspaceSeat { SessionId = seat, Restore = new WorkspaceSeatRestore { ReopenedSessionId = reopenedAs } },
        },
    };

    private bool Carry(string from, string to) =>
        SpawnFactory.CarryBossStart(from, to, _history.BossStartOf,
            (sid, factory, seat, by, kind) => _history.RecordBossStart(sid, factory, seat, by, kind, Now), "test");

    [Fact]
    public void A_SMART_RESTART_REOPEN_OF_THE_BOSS_CARRIES_ITS_RECORD_once_the_store_names_the_reopened_session()
    {
        _history.RecordBossStart("old-boss", TheFactory, "boss", "owners-agent", FactoryBossStartKinds.Started, Now);
        var mark = new WorkspaceRestoreMark
        {
            DirectorId = DirectorA, Kind = WorkspaceRestoreMarkKinds.Reopened, SeatSessionId = "old-boss", ReopenedSessionId = "reopened",
        };

        WorkspaceEndpoints.CarryBossStartOnReopen(ReopenedDoc("old-boss", reopenedAs: null), mark, Carry);
        Assert.Null(_history.BossStartOf("reopened"));

        WorkspaceEndpoints.CarryBossStartOnReopen(ReopenedDoc("old-boss", reopenedAs: "reopened"), mark, Carry);
        Assert.Equal((TheFactory, "boss"), _history.BossStartOf("reopened"));
    }

    [Fact]
    public void A_REOPENED_CHILD_OF_THE_BOSS_HAS_NO_RECORD_TO_CARRY()
    {
        _history.RecordBossStart("old-boss", TheFactory, "boss", "owners-agent", FactoryBossStartKinds.Started, Now);
        var mark = new WorkspaceRestoreMark
        {
            DirectorId = DirectorA, Kind = WorkspaceRestoreMarkKinds.Reopened, SeatSessionId = "old-child", ReopenedSessionId = "reopened",
        };

        WorkspaceEndpoints.CarryBossStartOnReopen(ReopenedDoc("old-child", reopenedAs: "reopened"), mark, Carry);

        Assert.Null(_history.BossStartOf("reopened"));
    }

    [Theory]
    [InlineData("writer", FactoryKits.RulesOnly)]
    [InlineData("boss", FactoryKits.Seat)]
    public void A_RESTORE_INTO_ANOTHER_SEAT_OR_KIT_DOES_NOT_CARRY_THE_RECORD(string seat, string kit)
    {
        _history.RecordBossStart("old-boss", TheFactory, "boss", "owners-agent", FactoryBossStartKinds.Started, Now);

        GatewayEndpoints.CarryBossStartOnRestore(Claim("old-boss"), RestoredAs(seat, kit), new SessionDto { SessionId = "new" },
            _door, "test");

        Assert.Null(_history.BossStartOf("new"));
    }
}
