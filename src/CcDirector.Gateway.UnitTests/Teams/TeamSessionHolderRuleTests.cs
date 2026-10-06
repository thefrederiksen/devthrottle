using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// THE SINGLE POINT for "which Director holds this session" (devthrottle_internal#2311): the session store answers every
/// per-session lookup through <see cref="TeamSessionHolderRule"/> in a team, and through nothing - today's first row - in
/// a personal tenant and on a dark Gateway. Over a real database, real device and Director registries, a real session
/// key registry and a real session store: Alice and Bob in one team, each with an enrolled Director, and Bob's Director
/// listing Alice's session id. Every case that depends on which row the store would find first makes Bob's row the ONLY
/// row of that id and asserts it, so nothing here rests on the store's hash order.
/// </summary>
public sealed class TeamSessionHolderRuleTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Alice = "sub-alice";
    private const string Bob = "sub-bob";
    private const string AliceDirector = "director-alice";
    private const string AliceSecondDirector = "director-alice-2";
    private const string BobDirector = "director-bob";
    private const string AliceSession = "session-alice";
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TeamRegistry _teams;
    private readonly DeviceRegistry _devices;
    private readonly DirectorRegistry _directors;
    private readonly SessionKeyRegistry _keys;
    private readonly PushedSessionStore _sessions = new();
    private readonly TeamCallerOwnership _ownership;
    private readonly TenantId _tenant;
    private bool _released = true;

    public TeamSessionHolderRuleTests()
    {
        _db = _harness.Open();
        var tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, tenants);
        _devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: true);
        _directors = new DirectorRegistry(_harness.LegacyPath("instances"));
        _keys = new SessionKeyRegistry(_db, isHosted: true);
        var boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices);
        _ownership = new TeamCallerOwnership(_directors, _sessions, _devices, new CcDirector.Gateway.History.SessionTurnStore(_db), boundary, _keys);

        var team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        _tenant = new TenantId(team);
        Assert.True(_teams.AddMember(team, Alice, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(team, Bob, TeamRole.Developer).IsDone);

        Enrol(Alice, AliceDirector);
        Enrol(Bob, BobDirector);
    }

    public void Dispose()
    {
        _devices.Dispose();
        _harness.Dispose();
    }

    /// <summary>The rule exactly as the host builds it: governs a team's tenant while Teams is released.</summary>
    private TeamSessionHolderRule Rule() => new(t => _released && _teams.IsTeam(t), _ownership);

    /// <summary>A member's Director, enrolled into the team on its own key and connected, listing nothing yet.</summary>
    private void Enrol(string subject, string directorId)
    {
        var deviceId = _tenant.Value + "|" + directorId;
        _devices.RegisterForTenant(_tenant, subject, deviceId, "M");
        _directors.RegisterFromStream(directorId, "M", "u", "1.0", 1, DateTime.UtcNow, _tenant, directorId, "device:" + deviceId);
        _sessions.RegisterConnection(_tenant, directorId, "conn-" + directorId);
    }

    private long _sequence;

    private void List(string directorId, params string[] sessionIds) =>
        Assert.True(_sessions.ApplySnapshot(_tenant, directorId, "conn-" + directorId, ++_sequence,
            sessionIds.Select(id => new SessionDto { SessionId = id }).ToArray()));

    private void KeyTo(string directorId, string sessionId) =>
        Assert.True(_keys.Register(_tenant, directorId, sessionId, new string('a', 64), DateTime.UtcNow.AddHours(1)));

    private static TurnPushBatch Batch(string generation) => new()
    {
        SessionId = AliceSession,
        Generation = generation,
        GenerationStartedUtc = DateTime.UtcNow.AddMinutes(-5),
        Agent = "ClaudeCode",
        StartOrdinal = 0,
        TotalCount = 1,
        Turns = new List<PushedTurn>
        {
            new() { Ordinal = 0, Role = "User", Parts = { new HistoryPartDto { Kind = "Text", Text = "hello" } }, Timestamp = DateTimeOffset.UtcNow },
        },
    };

    // ---- the rule ---------------------------------------------------------------------------------------------------

    [Fact]
    public void HolderOf_AKeyedSessionListedOnlyByAColleague_IsNobody()
    {
        KeyTo(AliceDirector, AliceSession);
        List(BobDirector, AliceSession);
        Assert.Equal(new[] { BobDirector }, _sessions.DirectorsHoldingSession(_tenant, AliceSession));

        Assert.Null(Rule().HolderOf(_tenant, AliceSession, new[] { BobDirector }));
    }

    [Fact]
    public void HolderOf_AKeyedSessionListedByItsOwnerAndAColleague_IsTheOwnersDirector_InEitherOrder()
    {
        KeyTo(AliceDirector, AliceSession);
        List(AliceDirector, AliceSession);
        List(BobDirector, AliceSession);

        Assert.Equal(AliceDirector, Rule().HolderOf(_tenant, AliceSession, new[] { BobDirector, AliceDirector }));
        Assert.Equal(AliceDirector, Rule().HolderOf(_tenant, AliceSession, new[] { AliceDirector, BobDirector }));
    }

    [Fact]
    public void HolderOf_NoRecordAndTwoListers_IsNobody_ASoleListerIsTheHolder()
    {
        List(AliceDirector, "session-unkeyed");
        Assert.Equal(AliceDirector, Rule().HolderOf(_tenant, "session-unkeyed", new[] { AliceDirector }));

        List(BobDirector, "session-unkeyed");
        Assert.Null(Rule().HolderOf(_tenant, "session-unkeyed", new[] { AliceDirector, BobDirector }));
    }

    [Fact]
    public void HolderOf_SeveralDirectorsOfOnePersonClaimIt_IsTheKeyRowsDirector()
    {
        Enrol(Alice, AliceSecondDirector);
        var turns = new CcDirector.Gateway.History.SessionTurnStore(_db);
        var boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices);
        using (boundary.EnterScope(_tenant))
        {
            // Both of Alice's Directors wrote the session's stored conversation, one generation each.
            turns.Append(AliceDirector, Batch("gen-a"), DateTime.UtcNow);
            turns.Append(AliceSecondDirector, Batch("gen-b"), DateTime.UtcNow);
            Assert.Equal(2, turns.DirectorsOfAnyGeneration(AliceSession).Count);
        }
        KeyTo(AliceSecondDirector, AliceSession);

        Assert.Equal(AliceSecondDirector, Rule().HolderOf(_tenant, AliceSession, new[] { AliceDirector, AliceSecondDirector }));
        Assert.Equal(AliceSecondDirector, Rule().HolderOf(_tenant, AliceSession, new[] { AliceSecondDirector, AliceDirector }));
    }

    [Fact]
    public void Governs_ATeamWhileReleased_NotADarkGatewayNorAPersonalTenant()
    {
        Assert.True(Rule().Governs(_tenant));
        Assert.False(Rule().Governs(new TenantId(Guid.NewGuid().ToString())));
        _released = false;
        Assert.False(Rule().Governs(_tenant));
    }

    // ---- the store answering through it -----------------------------------------------------------------------------

    [Fact]
    public void Store_InATeam_NeverAnswersAColleaguesDirector_ForAKeyedSessionOnlyTheColleagueLists()
    {
        KeyTo(AliceDirector, AliceSession);
        List(BobDirector, AliceSession);
        Assert.Equal(new[] { BobDirector }, _sessions.DirectorsHoldingSession(_tenant, AliceSession));
        // Before the rule is in use the store answers the first row - Bob's, the only one. This is what the rule stops.
        Assert.Equal(BobDirector, _sessions.TryLocate(_tenant, AliceSession, Stale)?.DirectorId);

        _sessions.UseHolderRule(Rule());

        Assert.Null(_sessions.TryLocate(_tenant, AliceSession, Stale));
        Assert.Null(_sessions.TryLocateIgnoringFreshness(_tenant, AliceSession));
        Assert.Null(_sessions.TryGetLastKnownSession(_tenant, AliceSession));
        Assert.False(_sessions.IsHoldersRow(_tenant, BobDirector, AliceSession));
    }

    [Fact]
    public void Store_InATeam_AnswersTheOwnersDirector_WhenItListsTheSessionToo()
    {
        KeyTo(AliceDirector, AliceSession);
        List(BobDirector, AliceSession);
        List(AliceDirector, AliceSession);
        _sessions.UseHolderRule(Rule());

        Assert.Equal(AliceDirector, _sessions.TryLocate(_tenant, AliceSession, Stale)?.DirectorId);
        Assert.Equal(AliceDirector, _sessions.TryLocateIgnoringFreshness(_tenant, AliceSession)?.DirectorId);
        Assert.Equal(AliceDirector, _sessions.TryGetLastKnownSession(_tenant, AliceSession)?.DirectorId);
        Assert.True(_sessions.IsHoldersRow(_tenant, AliceDirector, AliceSession));
        Assert.False(_sessions.IsHoldersRow(_tenant, BobDirector, AliceSession));
    }

    [Fact]
    public void Store_OnADarkGateway_AnswersTheFirstRowAsBefore()
    {
        KeyTo(AliceDirector, AliceSession);
        List(BobDirector, AliceSession);
        _released = false;
        _sessions.UseHolderRule(Rule());

        Assert.Equal(BobDirector, _sessions.TryLocate(_tenant, AliceSession, Stale)?.DirectorId);
        Assert.Equal(BobDirector, _sessions.TryLocateIgnoringFreshness(_tenant, AliceSession)?.DirectorId);
        Assert.Equal(BobDirector, _sessions.TryGetLastKnownSession(_tenant, AliceSession)?.DirectorId);
        Assert.True(_sessions.IsHoldersRow(_tenant, BobDirector, AliceSession));
    }

    [Fact]
    public void Store_InAPersonalTenant_AnswersTheFirstRowAsBefore()
    {
        var personal = new TenantId(Guid.NewGuid().ToString());
        _sessions.RegisterConnection(personal, "director-p", "conn-p");
        Assert.True(_sessions.ApplySnapshot(personal, "director-p", "conn-p", 1, new[] { new SessionDto { SessionId = AliceSession } }));
        _sessions.UseHolderRule(Rule());

        Assert.Equal("director-p", _sessions.TryLocate(personal, AliceSession, Stale)?.DirectorId);
        Assert.Equal("director-p", _sessions.TryLocateIgnoringFreshness(personal, AliceSession)?.DirectorId);
        Assert.Equal("director-p", _sessions.TryGetLastKnownSession(personal, AliceSession)?.DirectorId);
        Assert.True(_sessions.IsHoldersRow(personal, "director-p", AliceSession));
    }

    [Fact]
    public void Store_RefusesASecondRule_AndARuleThatNamesADirectorNotListingTheSession()
    {
        List(BobDirector, AliceSession);
        _sessions.UseHolderRule(new NamesOne(AliceDirector));
        Assert.Throws<InvalidOperationException>(() => _sessions.UseHolderRule(Rule()));
        Assert.Throws<InvalidOperationException>(() => _sessions.TryLocate(_tenant, AliceSession, Stale));
    }

    /// <summary>A rule that governs every tenant and always names one Director.</summary>
    private sealed class NamesOne(string directorId) : ISessionHolderRule
    {
        public bool Governs(TenantId tenant) => true;
        public string? HolderOf(TenantId tenant, string sessionId, IReadOnlyList<string> holders) => directorId;
    }
}
