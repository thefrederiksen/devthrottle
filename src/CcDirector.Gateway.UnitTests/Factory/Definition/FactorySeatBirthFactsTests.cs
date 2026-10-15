using CcDirector.ControlApi.Drain;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workspaces;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// THE SEAT IS A BIRTH FACT AND TRAVELS WITH THE SESSION (Factory Definition mission, phase 5, design section 2): the
/// history row records it write-once and the spawn door reads a caller's seat from there; a workspace capture keeps it;
/// a drain's restore and a Smart Restart reopen hand it back, with the model the seat was started on. And the read
/// behind "a seat's child on Codex is refused when the seat carries rules" answers from the definition version.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactorySeatBirthFactsTests : IDisposable
{
    private const string TheFactory = "website-factory";
    private const string SeatId = "5eafffff-0000-0000-0000-000000000001";
    private const string Repo = @"D:\repos\devthrottle";
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _h = new();
    public void Dispose() => _h.Dispose();

    private static SessionDto Pushed(string id, string? seat = null, int? version = null, string? kit = null,
        string? startOverride = null, string? factory = TheFactory) => new()
    {
        SessionId = id,
        Name = "Outreach",
        RepoPath = Repo,
        Agent = "ClaudeCode",
        MachineName = "SOREN_NORTH",
        CreatedAt = Now.AddMinutes(-5),
        LastActivityAt = Now,
        ActivityState = "Working",
        Status = "Running",
        Factory = factory,
        FactorySeat = seat,
        FactoryDefinitionVersion = version,
        FactoryKit = kit,
        FactoryStartOverride = startOverride,
    };

    // ---------- the history row ----------

    [Fact]
    public void FactoryOf_ASeatsRow_ReadsTheSeatAndVersionWithTheFactory()
    {
        var store = new SessionHistoryStore(_h.Open());
        store.UpsertLive("director-1", Pushed("s1", "outreach", 4, FactoryKits.Seat), Now);

        var lookup = store.FactoryOf("s1");

        Assert.Equal(TheFactory, lookup.Factory);
        Assert.Equal("outreach", lookup.Seat);
        Assert.Equal(4, lookup.DefinitionVersion);
        Assert.True(lookup.HasASeat);
    }

    [Fact]
    public void FactoryOf_UNDEFINED_FACTORY_ARowWithAFactoryAndNoSeat_HasNoSeat()
    {
        var store = new SessionHistoryStore(_h.Open());
        store.UpsertLive("director-1", Pushed("s1"), Now);

        var lookup = store.FactoryOf("s1");

        Assert.True(lookup.IsInAFactory);
        Assert.Null(lookup.Seat);
        Assert.False(lookup.HasASeat);
    }

    [Fact]
    public void UpsertLive_TheSeatIsWriteOnce_ALaterPushCanNeitherBlankNorMoveIt()
    {
        var store = new SessionHistoryStore(_h.Open());
        store.UpsertLive("director-1", Pushed("s1", "outreach", 4, FactoryKits.Seat, "agent Codex, chosen by the person"), Now);

        store.UpsertLive("director-1", Pushed("s1"), Now.AddMinutes(1));
        store.UpsertLive("director-1", Pushed("s1", "boss", 9, FactoryKits.RulesOnly, "something else"), Now.AddMinutes(2));

        var lookup = store.FactoryOf("s1");
        Assert.Equal("outreach", lookup.Seat);
        Assert.Equal(4, lookup.DefinitionVersion);
        using var ctx = _h.Open().CreateContext();
        var row = ctx.SessionHistory.Single(e => e.SessionId == "s1");
        Assert.Equal(FactoryKits.Seat, row.FactoryKit);
        Assert.Equal("agent Codex, chosen by the person", row.FactoryStartOverride);
    }

    [Fact]
    public void UpsertLive_ARowFirstSeenWithoutTheSeat_IsFilledByTheFirstPushThatCarriesIt()
    {
        var store = new SessionHistoryStore(_h.Open());
        store.UpsertLive("director-1", Pushed("s1"), Now);
        store.UpsertLive("director-1", Pushed("s1", "outreach", 4, FactoryKits.RulesOnly), Now.AddMinutes(1));

        Assert.Equal("outreach", store.FactoryOf("s1").Seat);
    }

    [Fact]
    public void FactoryOf_TheSeatSurvivesAGatewayRestart()
    {
        new SessionHistoryStore(_h.Open()).UpsertLive("director-1", Pushed("s1", "outreach", 4, FactoryKits.Seat), Now);

        var afterRestart = new SessionHistoryStore(_h.Open()).FactoryOf("s1");

        Assert.Equal("outreach", afterRestart.Seat);
        Assert.Equal(4, afterRestart.DefinitionVersion);
    }

    // ---------- capture, restore and reopen hand the seat and its model back ----------

    private static WorkspaceSeat ASeat() => new()
    {
        SessionId = SeatId,
        Name = "Outreach",
        Agent = "ClaudeCode",
        RepoPath = Repo,
        Factory = TheFactory,
        FactorySeat = "outreach",
        FactoryDefinitionVersion = 4,
        FactoryKit = FactoryKits.Seat,
        LaunchModel = "claude-sonnet-5-5",
        HandoverPath = @"D:\handovers\outreach.md",
        ClaudeSessionId = "claude-1",
        Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Undecided },
    };

    [Fact]
    public void CaptureSeat_ASeatsSession_CarriesTheSeatVersionKitAndModel()
    {
        var seat = WorkspaceCapture.CaptureSeat(new SessionDto
        {
            SessionId = SeatId, Name = "Outreach", Agent = "ClaudeCode", RepoPath = Repo, Factory = TheFactory,
            FactorySeat = "outreach", FactoryDefinitionVersion = 4, FactoryKit = FactoryKits.Seat,
            LaunchModel = "claude-sonnet-5-5", CurrentModel = "claude-opus-5-5",
        }, sortOrder: 0);

        Assert.Equal("outreach", seat.FactorySeat);
        Assert.Equal(4, seat.FactoryDefinitionVersion);
        Assert.Equal(FactoryKits.Seat, seat.FactoryKit);
        Assert.Equal("claude-sonnet-5-5", seat.LaunchModel);
    }

    [Fact]
    public void BuildRequest_ARestoredSeat_IsTheSameSeatOnTheSameModel()
    {
        var request = DirectorRestore.BuildRequest(ASeat(), owner: null, new WorkspaceRestoreOrder());

        Assert.Equal("outreach", request.FactorySeat);
        Assert.Equal(4, request.FactoryDefinitionVersion);
        Assert.Equal(FactoryKits.Seat, request.FactoryKit);
        Assert.Equal("claude-sonnet-5-5", request.Model);
    }

    [Fact]
    public void BuildReopen_AReopenedSeat_IsTheSameSeatOnTheSameModel()
    {
        var doc = new WorkspaceDocument { Id = "w-1", Name = "Smart Restart", Origin = WorkspaceOrigins.Captured, CreatedUtc = Now };
        var seat = ASeat();
        seat.RepoPath = Path.GetTempPath();

        var request = DirectorWayUp.BuildReopen(doc, seat);

        Assert.Equal("outreach", request.FactorySeat);
        Assert.Equal(4, request.FactoryDefinitionVersion);
        Assert.Equal(FactoryKits.Seat, request.FactoryKit);
        Assert.Equal("claude-sonnet-5-5", request.Model);
    }

    [Fact]
    public void BuildRequest_UNDEFINED_FACTORY_ARestoredSessionWithNoSeat_AsksForNoneOfTheFields()
    {
        var seat = ASeat();
        seat.FactorySeat = null;
        seat.FactoryDefinitionVersion = null;
        seat.FactoryKit = null;
        seat.LaunchModel = null;

        var request = DirectorRestore.BuildRequest(seat, owner: null, new WorkspaceRestoreOrder());

        Assert.False(request.CarriesSeatStartFields());
        Assert.Equal(TheFactory, request.Factory);
    }

    [Fact]
    public void Save_AWorkspaceWriterCannotMakeASessionASeatOrChangeItsModel()
    {
        var store = new WorkspaceStore(_h.Open());
        var stored = ASeat();
        stored.FactorySeat = null;
        stored.FactoryDefinitionVersion = null;
        stored.FactoryKit = null;
        stored.LaunchModel = null;
        stored.DrainState = WorkspaceDrainStates.Drained;
        stored.Restore = new WorkspaceSeatRestore
        {
            Decision = WorkspaceRestoreDecisions.Restore,
            Why = "the mission is not finished",
            Command = "cc-devthrottle director restore ...",
        };
        store.Create(new WorkspaceDocument
        {
            Id = "drain-1", Name = "Drain", Origin = WorkspaceOrigins.Captured, Machine = "MAC", DirectorId = "old-director",
            Seats = new() { stored }, RestoreAfterRestart = new() { SeatId },
        }, Now);

        var copy = store.Get("drain-1")!;
        copy.Seats.Single().FactorySeat = "boss";
        copy.Seats.Single().FactoryDefinitionVersion = 9;
        copy.Seats.Single().FactoryKit = FactoryKits.Seat;
        copy.Seats.Single().LaunchModel = "claude-opus-5-5";
        store.Save(copy, Now);

        var after = store.Get("drain-1")!.Seats.Single();
        Assert.Null(after.FactorySeat);
        Assert.Null(after.FactoryDefinitionVersion);
        Assert.Null(after.FactoryKit);
        Assert.Null(after.LaunchModel);
    }

    // ---------- does a seat carry rules: read from the definition version ----------

    private static readonly TenantId A = new("acct-a");

    private FactoryDefinitionStore DefinedStore(out int version)
    {
        var store = new FactoryDefinitionStore(_h.Open());
        var saved = store.SaveDraft(A, DefinitionFixture.FactoryId, DefinitionFixture.Valid(), 0,
            FactoryMemoryAuthorKinds.Person, "device:owner", null, Now);
        Assert.True(saved.Ok, saved.Refusal);
        version = saved.Version!.Version;
        return store;
    }

    [Fact]
    public void Read_ASeatThatNamesARuleSet_CarriesRules()
    {
        var store = DefinedStore(out var version);

        Assert.Equal(SeatRulesRead.Carries(true), FactorySeatRules.Read(store, A, DefinitionFixture.FactoryId, version, "boss"));
    }

    [Fact]
    public void Read_ASeatThatNamesNoRuleSet_CarriesNone()
    {
        var store = DefinedStore(out var version);

        Assert.Equal(SeatRulesRead.Carries(false), FactorySeatRules.Read(store, A, DefinitionFixture.FactoryId, version, "writer"));
    }

    [Fact]
    public void Read_AVersionThatDoesNotExist_IsUnreadWithTheReason()
    {
        var store = DefinedStore(out var version);

        var read = FactorySeatRules.Read(store, A, DefinitionFixture.FactoryId, version + 5, "boss");

        Assert.False(read.Read);
        Assert.Equal($"version {version + 5} of the definition of 'devthrottle' cannot be found", read.Why);
    }

    [Fact]
    public void Read_ASeatTheVersionDoesNotHold_IsUnreadWithTheReason()
    {
        var store = DefinedStore(out var version);

        var read = FactorySeatRules.Read(store, A, DefinitionFixture.FactoryId, version, "nobody");

        Assert.False(read.Read);
        Assert.Equal($"version {version} of the definition of 'devthrottle' has no seat 'nobody'", read.Why);
    }

    [Fact]
    public void Read_AnotherAccountsVersion_IsNotFound()
    {
        var store = DefinedStore(out var version);

        Assert.False(FactorySeatRules.Read(store, new TenantId("acct-b"), DefinitionFixture.FactoryId, version, "boss").Read);
    }
}
