using CcDirector.Core.Account;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// A Director set up for a team, at its tunnel (devthrottle_internal#2311): the REAL <see cref="DirectorHub"/>, its
/// Hello authenticated by a team key minted by the real hosted device registry, exactly as the auth middleware leaves
/// it on the negotiate request. What this leaves out is only the request-path access lease in front of the hub, which
/// refuses every team tenant until the next step reads the team's bill - so these are the tunnel-level assertions that
/// can be made today; the over-the-wire ones move to that step. The enrollment and move tests (review F1 and F3) run the
/// endpoint's own functions over the Gateway's own wiring (<see cref="HostedEnrollmentEndpoint.TeamEnrollment.Over"/>),
/// so the session count a move or a set-up-again reads is this real roster, filled by the real hub.
/// </summary>
public sealed class TeamDirectorTunnelTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Alice = "sub-alice";
    private const string Bob = "sub-bob";
    private const string Audience = "authenticated";
    private const string Issuer = "https://test.example.supabase.co/auth/v1";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly DeviceRegistry _devices;
    private readonly HostedTenantBoundary _boundary;
    private DirectorRegistry _directors;
    private PushedSessionStore _store = new();
    private readonly GatewayStreamRegistry _streams = new();
    private readonly DirectorConnectionRegistry _connections = new();
    private readonly GatewayInputStatsAggregator _inputStats;
    private readonly AsyncLocalTenantContext _tenantContext = new();
    private readonly SessionTurnStore _turns;
    private readonly SessionKeyRegistry _sessionKeys;
    private readonly TestEs256Key _key = new();
    private readonly JwtAccessTokenValidator _validator;
    private int _connectionCount;
    private readonly string _teamA;
    private readonly string _teamB;

    public TeamDirectorTunnelTests()
    {
        // The database reads the SAME ambient tenant the boundary enters, as in production - so a stored-conversation
        // read made outside the team's scope fails here instead of quietly answering from a fixed tenant.
        _db = _harness.Open(_tenantContext);
        _turns = new SessionTurnStore(_db);
        _sessionKeys = new SessionKeyRegistry(_db, isHosted: true);
        _tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, _tenants);
        _devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: true);
        _boundary = new HostedTenantBoundary(_tenantContext, _devices, hosted: false);
        _directors = new DirectorRegistry(_harness.LegacyPath("instances"));
        _inputStats = new GatewayInputStatsAggregator(_harness.LegacyPath("stats.db"));
        _validator = new JwtAccessTokenValidator(
            "test-signing-secret", timeProvider: null, publicKeySetJson: _key.PublicKeySetJson(),
            expectedAudience: Audience, expectedIssuer: Issuer, allowSymmetricHs256: false);
        var revoker = new TeamMemberAccessRevoker(_devices, _connections);
        _teams.MembershipCommitted += change => revoker.OnMembershipCommitted(change);

        _teamA = _teams.CreateTeam(Owner, "A").Team!.TeamId;
        _teamB = _teams.CreateTeam(Owner, "B").Team!.TeamId;
        foreach (var team in new[] { _teamA, _teamB })
        {
            Assert.True(_teams.AddMember(team, Alice, TeamRole.Developer).IsDone);
            Assert.True(_teams.AddMember(team, Bob, TeamRole.Developer).IsDone);
        }
    }

    public void Dispose()
    {
        _inputStats.Dispose();
        _devices.Dispose();
        _harness.Dispose();
        _key.Dispose();
    }

    private sealed record Connected(DirectorHub Hub, FakeHubCtx Context);

    /// <summary>A member's Director, set up for <paramref name="team"/>, says Hello on its own key.</summary>
    private Connected Hello(string team, string subject, string directorId)
    {
        var key = _devices.RegisterForTenant(new TenantId(team), subject,
            HostedEnrollmentEndpoint.TeamScopedDeviceId(team, subject, directorId), "M").DeviceKey;
        var connected = SayHello(key, directorId);
        Assert.False(connected.Context.Aborted);
        return connected;
    }

    /// <summary>A Director says Hello under <paramref name="directorId"/> on <paramref name="key"/>, authenticated exactly
    /// as the auth middleware leaves it. Whether it was accepted is the caller's to assert.</summary>
    private Connected SayHello(string key, string directorId)
    {
        var resolution = _devices.ResolveCredential(key);
        Assert.Equal(DeviceCredentialResolutionKind.Active, resolution.Kind);

        var http = new DefaultHttpContext();
        http.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = resolution.Identity;
        var ctx = new FakeHubCtx("conn-" + directorId + "-" + Interlocked.Increment(ref _connectionCount), http);
        var hub = new DirectorHub(_store, _directors, InputStatsHandle.Available(_inputStats), _streams,
            tenantBoundary: _boundary, connections: _connections, sessionTurns: _turns, sessionKeys: _sessionKeys,
            teamOwnership: new TeamCallerOwnership(_directors, _store, _devices, _turns, _boundary, _sessionKeys)) { Context = ctx };
        hub.Hello(new DirectorStreamHello { DirectorId = directorId, MachineName = "M", User = "u", Version = "1", Pid = 1, StartedAt = DateTime.UtcNow });
        return new Connected(hub, ctx);
    }

    private string Token(string subject) => _key.Token(subject, subject + "@example.com", Audience, Issuer);

    private HostedEnrollmentEndpoint.TeamEnrollment TeamEnrollment() =>
        HostedEnrollmentEndpoint.TeamEnrollment.Over(_teams, new TeamAccess(_teams), _store, _connections);

    /// <summary>Set a Director up through the endpoint's own enrollment, Teams released. No paid gate is wired: these
    /// tests are about where the Director is, not about the bill.</summary>
    private HostedEnrollmentEndpoint.EnrollResult Enroll(string subject, string directorId, string? teamId) =>
        HostedEnrollmentEndpoint.Enroll(Token(subject),
            new EnrollSignedInRequest { DeviceId = directorId, MachineName = "M", Platform = "windows", DeviceType = "workstation", TeamId = teamId },
            _devices, _tenants, _validator, entitlements: null, DateTime.UtcNow, trials: null, TeamEnrollment());

    private HostedEnrollmentEndpoint.EnrollResult Move(string subject, string directorId, string? teamId) =>
        HostedEnrollmentEndpoint.Move(Token(subject), new MoveDirectorRequest { DeviceId = directorId, TeamId = teamId },
            _devices, _tenants, _validator, TeamEnrollment());

    private string EnrolledKey(string subject, string directorId, string? teamId)
    {
        var result = Enroll(subject, directorId, teamId);
        Assert.Equal(200, result.Status);
        return result.Response!.DeviceKey;
    }

    private DeviceCredentialResolutionKind KindOf(string key) => _devices.ResolveCredential(key).Kind;

    private TeamOwnership Whose(string team, string caller, string sessionId) =>
        new TeamCallerOwnership(_directors, _store, _devices, _turns, _boundary, _sessionKeys)
            .Whose(new TenantId(team), caller, "/sessions/{sid}", name => name == "sid" ? sessionId : null);

    private string[] Sessions(string team) =>
        _store.SnapshotConnected(new TenantId(team)).Select(s => s.Session.SessionId).OrderBy(s => s).ToArray();

    private string[] Directors(string team) =>
        _directors.ListDirectors(new TenantId(team)).Select(d => d.DirectorId).OrderBy(d => d).ToArray();

    [Fact]
    public void TwoDirectorsOfOnePerson_OnTwoTeams_EachRegisterInTheirOwnTeam_AndNeverAppearInTheOther()
    {
        var a = Hello(_teamA, Alice, "director-a");
        var b = Hello(_teamB, Alice, "director-b");
        a.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-a" } });
        b.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-b" } });

        Assert.Equal(new[] { "director-a" }, Directors(_teamA));
        Assert.Equal(new[] { "director-b" }, Directors(_teamB));
        Assert.Equal(new[] { "session-a" }, Sessions(_teamA));
        Assert.Equal(new[] { "session-b" }, Sessions(_teamB));
    }

    [Fact]
    public void ASessionRegisteredByADirector_BelongsToThatDirectorsTeamTenant()
    {
        var a = Hello(_teamA, Bob, "director-bob");
        a.Hub.PushDelta(1, new SessionDto { SessionId = "session-bob" });

        var located = _store.TryGetLastKnownSession(new TenantId(_teamA), "session-bob");
        Assert.NotNull(located);
        Assert.Equal("director-bob", located!.Value.DirectorId);
        Assert.Null(_store.TryGetLastKnownSession(new TenantId(_teamB), "session-bob"));
        Assert.Null(_store.TryGetLastKnownSession(_tenants.MintOrLookupBySubject(Bob, null), "session-bob"));
    }

    [Fact]
    public void RemovingAPerson_CutsTheirOpenTunnelOnThatTeamOnly()
    {
        var aliceA = Hello(_teamA, Alice, "director-alice-a");
        var aliceB = Hello(_teamB, Alice, "director-alice-b");
        var bobA = Hello(_teamA, Bob, "director-bob-a");

        Assert.True(_teams.RemoveMember(_teamA, Alice).IsDone);

        Assert.True(aliceA.Context.Aborted);
        Assert.False(aliceB.Context.Aborted);
        Assert.False(bobA.Context.Aborted);
    }

    [Fact]
    public void DemotingAPersonToCollaborator_CutsTheirOpenTunnelOnThatTeamOnly()
    {
        var aliceA = Hello(_teamA, Alice, "director-alice-a");
        var bobA = Hello(_teamA, Bob, "director-bob-a");

        Assert.True(_teams.ChangeRole(_teamA, Alice, TeamRole.Collaborator).IsDone);

        Assert.True(aliceA.Context.Aborted);
        Assert.False(bobA.Context.Aborted);
    }

    // ---- Review F2, point 1: a team key says Hello for its own Director only ---------------------------------------

    [Fact]
    public void ATeamKey_SayingHelloUnderAnotherMembersDirectorId_IsRefused_AndTheRealDirectorStillConnects()
    {
        var bobsKey = EnrolledKey(Bob, "director-bob", _teamA);

        var squat = SayHello(bobsKey, "director-alice");

        Assert.True(squat.Context.Aborted);
        Assert.Empty(Directors(_teamA));
        // Nothing was bound, so Alice's own Director takes its own id, and is its owner's.
        var alice = SayHello(EnrolledKey(Alice, "director-alice", _teamA), "director-alice");
        Assert.False(alice.Context.Aborted);
        Assert.Equal(new[] { "director-alice" }, Directors(_teamA));
    }

    [Fact]
    public void ATeamKey_SayingHelloUnderItsOwnEnrolledId_IsAccepted_InAnyLetterCase()
    {
        var key = EnrolledKey(Alice, "Director-Alice", _teamA);

        Assert.False(SayHello(key, "director-alice").Context.Aborted);
    }

    [Fact]
    public void APersonalKey_SayingHelloUnderAnIdItWasNotEnrolledFor_IsAcceptedAsBefore()
    {
        var personal = _tenants.MintOrLookupBySubject(Alice, null);
        var key = _devices.RegisterForTenant(personal, Alice, "p|director-enrolled", "M").DeviceKey;
        Assert.False(_devices.ResolveCredential(key).Identity!.IsTeamKey);

        var hello = SayHello(key, "director-something-else");

        Assert.False(hello.Context.Aborted);
        Assert.Equal(new[] { "director-something-else" },
            _directors.ListDirectors(personal).Select(d => d.DirectorId).ToArray());
    }

    // ---- Review F2, point 2: a session id two Directors hold is nobody's own ------------------------------------

    [Fact]
    public void TwoMembersDirectors_ClaimingOneSessionId_MakeItNobodysOwn_AndOneHolderMakesItItsOwners()
    {
        var alice = Hello(_teamA, Alice, "director-alice");
        var bob = Hello(_teamA, Bob, "director-bob");
        alice.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-shared" }, new SessionDto { SessionId = "session-alice" } });
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-shared" } });

        Assert.Equal(TeamOwnership.Unknown, Whose(_teamA, Alice, "session-shared"));
        Assert.Equal(TeamOwnership.Unknown, Whose(_teamA, Bob, "session-shared"));
        // The roster does not refuse the duplicate: both Directors still list it.
        Assert.Equal(new[] { "director-alice", "director-bob" },
            _store.DirectorsHoldingSession(new TenantId(_teamA), "session-shared").OrderBy(d => d).ToArray());

        Assert.Equal(TeamOwnership.Callers, Whose(_teamA, Alice, "session-alice"));
        Assert.Equal(TeamOwnership.SomeoneElses, Whose(_teamA, Bob, "session-alice"));
        Assert.Equal(TeamOwnership.Unknown, Whose(_teamA, Alice, "session-nobody-has"));
    }

    // ---- Gateway review, the ended-session gap: a stored conversation is its writers' ----------------------------

    private static TurnPushBatch Conversation(string sid, int start, int count) =>
        Conversation(sid, start, count, "C:/transcripts/" + sid + ".jsonl", new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc));

    private static TurnPushBatch Conversation(string sid, int start, int count, string generation, DateTime startedUtc) => new()
    {
        SessionId = sid,
        Generation = generation,
        GenerationStartedUtc = startedUtc,
        Agent = "ClaudeCode",
        StartOrdinal = start,
        TotalCount = start + count,
        Turns = Enumerable.Range(start, count).Select(i => new PushedTurn
        {
            Ordinal = i,
            Role = i % 2 == 0 ? "User" : "Assistant",
            Parts = { new HistoryPartDto { Kind = "Text", Text = "turn " + i } },
        }).ToList(),
    };

    [Fact]
    public void AColleaguesEndedSession_WhoseOldIdOnlyAnotherMembersDirectorNowHolds_IsRefusedToThatMember()
    {
        var bob = Hello(_teamA, Bob, "director-bob");
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        bob.Hub.PushTurns(1, Conversation("session-bob", 0, 2));
        Assert.Equal(TeamOwnership.Callers, Whose(_teamA, Bob, "session-bob"));

        // Bob's session ends and leaves his roster; Alice's Director then lists his old id - it is the only holder.
        bob.Hub.PushSnapshot(2, Array.Empty<SessionDto>());
        var alice = Hello(_teamA, Alice, "director-alice");
        alice.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        Assert.Equal(new[] { "director-alice" }, _store.DirectorsHoldingSession(new TenantId(_teamA), "session-bob").ToArray());

        Assert.Equal(TeamOwnership.SomeoneElses, Whose(_teamA, Alice, "session-bob"));
    }

    [Fact]
    public void AMemberPushingIntoAColleaguesStoredConversation_IsRefused_AndDoesNotMakeItTheirs()
    {
        var bob = Hello(_teamA, Bob, "director-bob");
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        bob.Hub.PushTurns(1, Conversation("session-bob", 0, 2));
        bob.Hub.PushSnapshot(2, Array.Empty<SessionDto>());

        // Alice's Director appends to Bob's conversation, in the same generation: refused at the write (review round 2,
        // R2-F1), so the head still names Bob's Director and nothing of Alice's is stored.
        var alice = Hello(_teamA, Alice, "director-alice");
        alice.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        Assert.Null(alice.Hub.PushTurns(1, Conversation("session-bob", 2, 1)));
        using (_boundary.EnterScope(new TenantId(_teamA)))
        {
            Assert.Equal("director-bob", _turns.ReadHead("session-bob")!.DirectorId);
            Assert.Equal(new[] { "director-bob" }, _turns.DirectorsOfAnyGeneration("session-bob").ToArray());
        }

        Assert.Equal(TeamOwnership.SomeoneElses, Whose(_teamA, Alice, "session-bob"));
    }

    // ---- Gateway review round 2, R2-F1: a team's turn push is accepted only into the pusher's own session ----------

    [Fact]
    public void PushTurns_ALaterGenerationIntoAColleaguesEndedSession_IsRefused_AndItIsStillNotThePushersOwn()
    {
        var bob = Hello(_teamA, Bob, "director-bob");
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        bob.Hub.PushTurns(1, Conversation("session-bob", 0, 2));
        bob.Hub.PushSnapshot(2, Array.Empty<SessionDto>());
        string bobsGeneration;
        using (_boundary.EnterScope(new TenantId(_teamA)))
            bobsGeneration = _turns.ReadHead("session-bob")!.Generation;

        // Alice's Director becomes the only holder of Bob's old id and pushes a NEW generation, started later - the move
        // that used to switch the head to her Director and leave only her rows in the current generation.
        var alice = Hello(_teamA, Alice, "director-alice");
        alice.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        var answer = alice.Hub.PushTurns(1, Conversation("session-bob", 0, 1, "C:/transcripts/taken-over.jsonl",
            new DateTime(2026, 10, 4, 13, 0, 0, DateTimeKind.Utc)));

        Assert.Null(answer);
        using (_boundary.EnterScope(new TenantId(_teamA)))
        {
            var head = _turns.ReadHead("session-bob")!;
            Assert.Equal("director-bob", head.DirectorId);
            Assert.Equal(bobsGeneration, head.Generation);
            Assert.Equal(new[] { "director-bob" }, _turns.DirectorsOfAnyGeneration("session-bob").ToArray());
        }
        Assert.NotEqual(TeamOwnership.Callers, Whose(_teamA, Alice, "session-bob"));
    }

    [Fact]
    public void PushTurns_AColleaguesPushIntoALiveSession_IsRefused_AndTheOwnerIsStillAnsweredOwn()
    {
        var bob = Hello(_teamA, Bob, "director-bob");
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        bob.Hub.PushTurns(1, Conversation("session-bob", 0, 2));
        Assert.Equal(TeamOwnership.Callers, Whose(_teamA, Bob, "session-bob"));

        var alice = Hello(_teamA, Alice, "director-alice");
        Assert.Null(alice.Hub.PushTurns(1, Conversation("session-bob", 2, 1)));

        Assert.Equal(TeamOwnership.Callers, Whose(_teamA, Bob, "session-bob"));
        // And Bob's own next turn is still taken: the refused row did not hold his ordinal.
        Assert.Equal(3, bob.Hub.PushTurns(2, Conversation("session-bob", 2, 1))!.Count);
    }

    /// <summary>
    /// #3552 review S2-F1, the reviewer's reproduction turned round. It passed on bc0bbedea: Alice enrolled under Bob's
    /// Director id and, once the in-memory registry was empty (every restart), owned his stored sessions. Now the
    /// device table refuses the enrollment, so after a restart there is nothing of Alice's to say Hello with, and Bob's
    /// Director takes its own id back.
    /// </summary>
    [Fact]
    public void AKeyUnderAColleaguesDirectorId_IsRefusedAtEnrollment_SoAfterARestartTheColleagueStillOwnsTheirStoredSessions()
    {
        var bobKey = EnrolledKey(Bob, "director-bob", _teamA);
        var bob = SayHello(bobKey, "director-bob");
        Assert.False(bob.Context.Aborted);
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        Assert.Equal(2, bob.Hub.PushTurns(1, Conversation("session-bob", 0, 2))!.Count);
        Assert.Equal(TeamOwnership.Callers, Whose(_teamA, Bob, "session-bob"));

        // Alice names Bob's Director id as her device id: refused, and nothing is written.
        var alice = Enroll(Alice, "director-bob", _teamA);
        Assert.Equal(409, alice.Status);
        Assert.Equal(HostedEnrollmentEndpoint.DirectorIdTakenInTeamCode, alice.Code);
        using (var ctx = _db.CreateUnscopedContext())
            Assert.False(ctx.DeviceCredentials.Any(d => d.TenantId == _teamA && d.AccountSubject == Alice));
        Assert.Equal(DeviceCredentialResolutionKind.Active, KindOf(bobKey));

        // The Gateway restarts: the Director registry and the roster start empty, the database is the same.
        _directors = new DirectorRegistry(_harness.LegacyPath("instances-after-restart"));
        _store = new PushedSessionStore();
        // Still refused - the rule is in the device table, not in memory.
        Assert.Equal(409, Enroll(Alice, "director-bob", _teamA).Status);

        // Bob's Director says Hello first or last, it makes no difference: the id is his.
        var bobAgain = SayHello(bobKey, "director-bob");
        Assert.False(bobAgain.Context.Aborted);
        bobAgain.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        Assert.Equal(TeamOwnership.Callers, Whose(_teamA, Bob, "session-bob"));
        Assert.Equal(TeamOwnership.SomeoneElses, Whose(_teamA, Alice, "session-bob"));
        Assert.Equal(3, bobAgain.Hub.PushTurns(2, Conversation("session-bob", 2, 1))!.Count);
    }

    /// <summary>
    /// #3552 review S2-F2, the reviewer's reproduction turned round. It passed on bc0bbedea: Alice listed Bob's live
    /// session id and pushed its first rows, and Bob was refused his own session for good. Now nothing-stored is
    /// accepted only from the ONE Director in the tenant whose roster holds the id: while both list it, both are
    /// refused for now (never guessed); once Alice stops, Bob's push is stored and the session is his.
    /// </summary>
    [Fact]
    public void AColleagueListingALiveSessionId_CannotPushItsFirstRows_AndOnceTheyStop_TheOwnersPushIsStoredAndTheSessionIsTheirs()
    {
        // Bob's session is live and in his roster; nothing of it is stored yet.
        var bob = Hello(_teamA, Bob, "director-bob");
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });

        // Alice's Director lists the same id in its own roster and pushes first: refused for now.
        var alice = Hello(_teamA, Alice, "director-alice");
        alice.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        var first = alice.Hub.PushTurns(1, Conversation("session-bob", 0, 1));
        Assert.NotNull(first);
        Assert.Equal("", first!.Generation);
        Assert.Equal(0, first.Count);
        using (_boundary.EnterScope(new TenantId(_teamA)))
            Assert.Empty(_turns.DirectorsOfAnyGeneration("session-bob"));

        // While two Directors hold the id, Bob's push waits too - a delay, never a guess.
        var bobsWhileShared = bob.Hub.PushTurns(1, Conversation("session-bob", 0, 2));
        Assert.Equal(0, bobsWhileShared!.Count);

        // Alice stops listing it. Bob's push is stored, and the session is his.
        alice.Hub.PushSnapshot(2, Array.Empty<SessionDto>());
        Assert.Equal(2, bob.Hub.PushTurns(2, Conversation("session-bob", 0, 2))!.Count);
        using (_boundary.EnterScope(new TenantId(_teamA)))
            Assert.Equal(new[] { "director-bob" }, _turns.DirectorsOfAnyGeneration("session-bob").ToArray());
        Assert.Equal(TeamOwnership.Callers, Whose(_teamA, Bob, "session-bob"));
        Assert.Equal(TeamOwnership.SomeoneElses, Whose(_teamA, Alice, "session-bob"));
        // And Alice listing it again now cannot write into it.
        alice.Hub.PushSnapshot(3, new[] { new SessionDto { SessionId = "session-bob" } });
        Assert.Null(alice.Hub.PushTurns(3, Conversation("session-bob", 2, 1)));
    }

    // ---- #3552 review round 2: S2-F5 (one question, asked one way) and S2-F6 (the session key row says whose) ------

    /// <summary>
    /// #3552 review S2-F5, the reviewer's first round-2 reproduction turned round. It passed on 533a76cf1: Alice
    /// enrolled with the device id <c>x|director-bob</c>, which the old check read as a different id from Bob's, while
    /// Hello read the part after the last bar - Bob's id - and after a restart she owned his stored session. Now a device
    /// id holding a bar is refused at the door, by enrollment and by a move, and nothing is written; Bob keeps his id.
    /// </summary>
    [Fact]
    public void ADeviceIdWithABarBeforeAColleaguesDirectorId_IsRefusedAtEnrollmentAndMove_AndTheColleagueKeepsTheirId()
    {
        var bobKey = EnrolledKey(Bob, "director-bob", _teamA);
        var bob = SayHello(bobKey, "director-bob");
        Assert.False(bob.Context.Aborted);
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        Assert.Equal(2, bob.Hub.PushTurns(1, Conversation("session-bob", 0, 2))!.Count);

        var enrolled = Enroll(Alice, "x|director-bob", _teamA);
        Assert.Equal(400, enrolled.Status);
        Assert.Equal(HostedEnrollmentEndpoint.DeviceIdNotAllowedCode, enrolled.Code);
        // Into her own account as well: a personal key under that id would otherwise be moved in.
        Assert.Equal(400, Enroll(Alice, "x|director-bob", null).Status);
        var moved = Move(Alice, "x|director-bob", _teamA);
        Assert.Equal(400, moved.Status);
        Assert.Equal(HostedEnrollmentEndpoint.DeviceIdNotAllowedCode, moved.Code);
        // A control character is refused the same way.
        Assert.Equal(HostedEnrollmentEndpoint.DeviceIdNotAllowedCode, Enroll(Alice, "director-bob\t", _teamA).Code);
        using (var ctx = _db.CreateUnscopedContext())
            Assert.False(ctx.DeviceCredentials.Any(d => d.AccountSubject == Alice));

        // The Gateway restarts: Alice has no key to say Hello with, and Bob's Director takes its own id back.
        _directors = new DirectorRegistry(_harness.LegacyPath("instances-after-restart"));
        _store = new PushedSessionStore();
        var bobAgain = SayHello(bobKey, "director-bob");
        Assert.False(bobAgain.Context.Aborted);
        bobAgain.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        Assert.Equal(TeamOwnership.Callers, Whose(_teamA, Bob, "session-bob"));
        Assert.Equal(TeamOwnership.SomeoneElses, Whose(_teamA, Alice, "session-bob"));
    }

    /// <summary>
    /// #3552 review S2-F5, the letter-case variant. A real Director id has letters in it. The old check sent
    /// <c>"DeviceId" LIKE '%|&lt;id&gt;'</c> to the database: SQLite's LIKE ignores letter case, PostgreSQL's does not, so
    /// a test of it here proved nothing about production. The check now reads the team's rows and compares in memory
    /// through <see cref="DeviceCredentialIdentity.SameDirectorId"/>, the comparison Hello uses, so this answer is the
    /// same on both databases. The second id is the proof that the comparison really is made in memory and not by the
    /// database: SQLite's LIKE folds the letter case of ASCII letters only, so it does NOT match a capital O with a stroke
    /// to a small one, and against the old query that enrollment was accepted. Hello compares ignoring case for every
    /// letter, so it would have read that key as Bob's.
    /// </summary>
    [Fact]
    public void AColleaguesDirectorIdInAnotherLetterCase_IsRefused_ByTheComparisonHelloUses_MadeInMemory_NotByTheDatabase()
    {
        EnrolledKey(Bob, "director-bob", _teamA);
        var upper = Enroll(Alice, "DIRECTOR-BOB", _teamA);
        Assert.Equal(409, upper.Status);
        Assert.Equal(HostedEnrollmentEndpoint.DirectorIdTakenInTeamCode, upper.Code);

        // "director-bjørn" and "DIRECTOR-BJØRN": the same id to Hello, not the same text to SQLite's LIKE.
        const string bobsSecond = "director-bjørn";
        const string aliceAsks = "DIRECTOR-BJØRN";
        EnrolledKey(Bob, bobsSecond, _teamA);
        Assert.True(DeviceCredentialIdentity.SameDirectorId(bobsSecond, aliceAsks));
        var nonAscii = Enroll(Alice, aliceAsks, _teamA);
        Assert.Equal(409, nonAscii.Status);
        Assert.Equal(HostedEnrollmentEndpoint.DirectorIdTakenInTeamCode, nonAscii.Code);
        using (var ctx = _db.CreateUnscopedContext())
            Assert.False(ctx.DeviceCredentials.Any(d => d.AccountSubject == Alice));
    }

    /// <summary>
    /// #3552 review S2-F5, part 3: Hello holds even for a row written BEFORE the enrollment refusal existed. Alice's row
    /// under Bob's id is written straight into the device table, bypassing enrollment. After a restart her key's Hello
    /// is refused, because another person holds an active key for that id in the team. The same rule refuses Bob's own
    /// Hello while her stray row is active - neither is guessed to be the owner - and once that row is revoked, Bob
    /// connects and his session is his.
    /// </summary>
    [Fact]
    public void Hello_UnderAnIdAnotherPersonHoldsAnActiveKeyFor_IsRefused_EvenForARowWrittenStraightIntoTheDeviceTable()
    {
        var bobKey = EnrolledKey(Bob, "director-bob", _teamA);
        var bob = SayHello(bobKey, "director-bob");
        Assert.False(bob.Context.Aborted);
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        Assert.Equal(2, bob.Hub.PushTurns(1, Conversation("session-bob", 0, 2))!.Count);

        // A row of Alice's under Bob's id, in another letter case, as an older Gateway could have left it.
        var strayDeviceId = HostedEnrollmentEndpoint.TeamScopedDeviceId(_teamA, Alice, "Director-Bob");
        var aliceKey = _devices.RegisterForTenant(new TenantId(_teamA), Alice, strayDeviceId, "M").DeviceKey;

        _directors = new DirectorRegistry(_harness.LegacyPath("instances-after-restart"));
        _store = new PushedSessionStore();

        var alice = SayHello(aliceKey, "director-bob");
        Assert.True(alice.Context.Aborted);
        Assert.Null(_directors.RegisteringCredentialOf(new TenantId(_teamA), "director-bob"));

        Assert.True(SayHello(bobKey, "director-bob").Context.Aborted);

        Assert.True(_devices.RevokeDevice(strayDeviceId, "test: the stray row is removed"));
        var bobAgain = SayHello(bobKey, "director-bob");
        Assert.False(bobAgain.Context.Aborted);
        bobAgain.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        Assert.Equal(TeamOwnership.Callers, Whose(_teamA, Bob, "session-bob"));
        Assert.Equal(TeamOwnership.SomeoneElses, Whose(_teamA, Alice, "session-bob"));
    }

    /// <summary>Register a session key for <paramref name="sessionId"/> over a Director's own tunnel, as a stock Director
    /// does when it creates the session, before the agent starts.</summary>
    /// <summary>
    /// #3552, the second #2309 reports test. A session's own routes are its owner's from its KEY ROW, not only from the
    /// roster: the Director registers the key before it lists the session (#3558), and after a Gateway restart the roster
    /// is empty until the Director reconnects while the key row is still in the database. Before the fix the gate answered
    /// such a session Unknown - so its own key was refused its own reports (403 team_action_refused) - because it asked the
    /// roster first. Now the key row decides, as ClaimOf does: the owner's, keyed and unlisted; still the owner's while a
    /// colleague lists the id; never the colleague's.
    /// </summary>
    [Fact]
    public void Whose_AKeyedSession_IsItsOwnersFromTheKeyRow_UnlistedOrListedByAColleague_AndNeverTheColleagues()
    {
        var bob = Hello(_teamA, Bob, "director-bob");
        RegisterSessionKey(bob, "session-keyed");

        // Keyed, not listed by anyone: the owner's, a colleague's not.
        Assert.Equal(TeamOwnership.Callers, Whose(_teamA, Bob, "session-keyed"));
        Assert.Equal(TeamOwnership.SomeoneElses, Whose(_teamA, Alice, "session-keyed"));

        // A colleague's Director lists the id: still the owner's, still not the colleague's.
        var alice = Hello(_teamA, Alice, "director-alice");
        alice.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-keyed" } });
        Assert.Equal(TeamOwnership.Callers, Whose(_teamA, Bob, "session-keyed"));
        Assert.Equal(TeamOwnership.SomeoneElses, Whose(_teamA, Alice, "session-keyed"));

        // A session with no key row is still decided by the roster alone: unlisted is nobody's.
        Assert.Equal(TeamOwnership.Unknown, Whose(_teamA, Bob, "session-never-keyed"));
    }

    private static void RegisterSessionKey(Connected director, string sessionId) =>
        director.Hub.RegisterSessionKey(new SessionKeyRegistration
        {
            SessionId = sessionId,
            KeyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("key-of-" + sessionId))).ToLowerInvariant(),
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        });

    /// <summary>
    /// #3552 review S2-F6, the reviewer's second round-2 reproduction turned round. It passed on 533a76cf1: Alice listed
    /// Bob's live session id until it ended, every push of Bob's was refused for now, nothing was stored, and once the
    /// session ended Alice was answered its owner. Now the session's key row - registered by Bob's Director when the
    /// session was created - says whose it is: Bob's pushes are stored while Alice lists the id, Alice's push is refused
    /// for good, and after the session ends all three routes are refused to her.
    /// </summary>
    [Fact]
    public void AColleagueWhoListsALiveSessionIdUntilItEnds_IsNotItsOwner_BecauseTheSessionsKeyNamesTheOwnersDirector()
    {
        var bob = Hello(_teamA, Bob, "director-bob");
        RegisterSessionKey(bob, "session-bob");
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        var aliceKey = _devices.RegisterForTenant(new TenantId(_teamA), Alice,
            HostedEnrollmentEndpoint.TeamScopedDeviceId(_teamA, Alice, "director-alice"), "M").DeviceKey;
        var alice = SayHello(aliceKey, "director-alice");
        alice.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });

        // While she lists it, Bob's pushes are stored; hers is refused for good.
        Assert.Null(alice.Hub.PushTurns(1, Conversation("session-bob", 0, 1)));
        for (var i = 1; i <= 3; i++)
        {
            var answer = bob.Hub.PushTurns(i, Conversation("session-bob", 0, 2));
            Assert.NotNull(answer);
            Assert.Equal(2, answer!.Count);
        }
        using (_boundary.EnterScope(new TenantId(_teamA)))
            Assert.Equal(new[] { "director-bob" }, _turns.DirectorsOfAnyGeneration("session-bob").ToArray());

        // Bob's session ends and leaves his roster. Alice is the only holder - and it is not hers.
        bob.Hub.PushSnapshot(2, Array.Empty<SessionDto>());
        Assert.Equal(TeamOwnership.SomeoneElses, Whose(_teamA, Alice, "session-bob"));
        var gate = Gate(out var ownership);
        foreach (var pattern in new[] { "/sessions/{sid}/wingman-stops", "/sessions/{sid}/turn-verdicts", "/sessions/{sid}/recap" })
        {
            var verdict = Ask(gate, ownership, "GET", pattern, "session-bob", new TenantId(_teamA),
                _devices.ResolveCredential(aliceKey).Identity, null);
            Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
        }
    }

    /// <summary>
    /// #3552 review S2-F6, part 1 on its own: with NOTHING stored - the case the stored writers cannot answer - a
    /// session whose key row names another person's Director is not the caller's own, even when the caller's Director is
    /// its only holder. Bob's session ends before any push of it gets through; Alice listed the id all along.
    /// </summary>
    [Fact]
    public void ASessionWithNothingStored_WhoseKeyNamesAnotherPersonsDirector_IsNotTheOnlyHoldersOwn()
    {
        var bob = Hello(_teamA, Bob, "director-bob");
        RegisterSessionKey(bob, "session-bob");
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        var aliceKey = _devices.RegisterForTenant(new TenantId(_teamA), Alice,
            HostedEnrollmentEndpoint.TeamScopedDeviceId(_teamA, Alice, "director-alice"), "M").DeviceKey;
        var alice = SayHello(aliceKey, "director-alice");
        alice.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });

        bob.Hub.PushSnapshot(2, Array.Empty<SessionDto>());
        using (_boundary.EnterScope(new TenantId(_teamA)))
            Assert.Null(_turns.ReadHead("session-bob"));
        Assert.Equal(new[] { "director-alice" }, _store.DirectorsHoldingSession(new TenantId(_teamA), "session-bob").ToArray());

        Assert.Equal(TeamOwnership.SomeoneElses, Whose(_teamA, Alice, "session-bob"));
        var gate = Gate(out var ownership);
        Assert.Null(ownership.PersonOfSession(new TenantId(_teamA), "session-bob"));
        foreach (var pattern in new[] { "/sessions/{sid}/wingman-stops", "/sessions/{sid}/turn-verdicts", "/sessions/{sid}/recap" })
        {
            var verdict = Ask(gate, ownership, "GET", pattern, "session-bob", new TenantId(_teamA),
                _devices.ResolveCredential(aliceKey).Identity, null);
            Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
        }

        // Her own session, keyed by her own Director, is hers as before.
        RegisterSessionKey(alice, "session-alice");
        alice.Hub.PushSnapshot(2, new[] { new SessionDto { SessionId = "session-alice" } });
        Assert.Equal(TeamOwnership.Callers, Whose(_teamA, Alice, "session-alice"));
    }

    // ---- #3552 review round 3: S2-F8 (who may write a session's key row) and S2-F10 (a person's own keys) ----------

    /// <summary>
    /// #3552 review S2-F8, the reviewer's first round-3 reproduction turned round. It passed on 73fcc561e: in the gap
    /// where Bob's session is in his roster and its key has not arrived (a session started while the tunnel was down),
    /// Alice's Director registered a key for Bob's session id, the row named her Director, Bob's own registration was
    /// refused for good, and the session became hers. Now, in a team, the hub refuses a key for an id ANOTHER Director's
    /// roster lists: Alice is refused and nothing is written, Bob's registration is accepted, and the session is his.
    /// </summary>
    [Fact]
    public void AColleaguesDirector_RegisteringASessionKeyForAnIdAnotherDirectorLists_IsRefused_AndTheOwnersKeyMakesItTheirs()
    {
        // The gap: Bob's session is in his roster and its key registration has not arrived yet.
        var bob = Hello(_teamA, Bob, "director-bob");
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        var aliceKey = _devices.RegisterForTenant(new TenantId(_teamA), Alice,
            HostedEnrollmentEndpoint.TeamScopedDeviceId(_teamA, Alice, "director-alice"), "M").DeviceKey;
        var alice = SayHello(aliceKey, "director-alice");

        // Alice's Director registers a key for Bob's session id: refused, and no row is written.
        Assert.Throws<HubException>(() => RegisterSessionKey(alice, "session-bob"));
        Assert.Null(_sessionKeys.DirectorOfSession(new TenantId(_teamA), "session-bob"));

        // Bob's own registration - the honest reseed, roster first and then the key - is accepted.
        RegisterSessionKey(bob, "session-bob");
        Assert.Equal("director-bob", _sessionKeys.DirectorOfSession(new TenantId(_teamA), "session-bob"));

        // The session is Bob's: his pushes are stored, and Alice's is refused for good.
        Assert.Equal(TeamOwnership.Callers, Whose(_teamA, Bob, "session-bob"));
        Assert.Equal(2, bob.Hub.PushTurns(1, Conversation("session-bob", 0, 2))!.Count);
        Assert.Null(alice.Hub.PushTurns(1, Conversation("session-bob", 2, 1)));

        // Bob's session ends; Alice lists the id: it is not hers, and the three routes are refused to her.
        bob.Hub.PushSnapshot(2, Array.Empty<SessionDto>());
        alice.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        Assert.Equal(TeamOwnership.SomeoneElses, Whose(_teamA, Alice, "session-bob"));
        var gate = Gate(out var ownership);
        foreach (var pattern in new[] { "/sessions/{sid}/wingman-stops", "/sessions/{sid}/turn-verdicts", "/sessions/{sid}/recap" })
        {
            var verdict = Ask(gate, ownership, "GET", pattern, "session-bob", new TenantId(_teamA),
                _devices.ResolveCredential(aliceKey).Identity, null);
            Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
        }
    }

    /// <summary>
    /// #3552 review S2-F8: every honest order stays accepted. A stock Director registers a new session's key before it
    /// lists the session; on a reseed it sends its roster and then its keys; and after a reconnect it registers the same
    /// key again. None of these is refused, because no OTHER Director lists the id and nothing of another person's is
    /// stored for it.
    /// </summary>
    [Fact]
    public void ASessionKey_RegisteredBeforeTheRoster_AfterTheRoster_OrAgainAfterAReconnect_IsAccepted()
    {
        var bob = Hello(_teamA, Bob, "director-bob");

        // A new session: key first, then the roster (the stock order).
        RegisterSessionKey(bob, "session-new");
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-new" } });
        Assert.Equal("director-bob", _sessionKeys.DirectorOfSession(new TenantId(_teamA), "session-new"));

        // A key that was lost at launch arrives in the reseed: roster first, then the key.
        bob.Hub.PushSnapshot(2, new[] { new SessionDto { SessionId = "session-new" }, new SessionDto { SessionId = "session-lost" } });
        RegisterSessionKey(bob, "session-lost");
        Assert.Equal("director-bob", _sessionKeys.DirectorOfSession(new TenantId(_teamA), "session-lost"));
        Assert.Equal(2, bob.Hub.PushTurns(1, Conversation("session-lost", 0, 2))!.Count);

        // A reconnect: the same Director on a new connection lists both and registers both again.
        var bobAgain = Hello(_teamA, Bob, "director-bob");
        bobAgain.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-new" }, new SessionDto { SessionId = "session-lost" } });
        RegisterSessionKey(bobAgain, "session-new");
        RegisterSessionKey(bobAgain, "session-lost");
        Assert.Equal(TeamOwnership.Callers, Whose(_teamA, Bob, "session-lost"));
    }

    /// <summary>
    /// #3552 review S2-F8, the second half of the rule: an id with stored rows that another person's Director wrote is
    /// refused a key too, even when nobody lists it any more. Bob's session was stored without a key row (the
    /// sole-holder rule) and has ended; Alice's Director cannot then make the key row name it.
    /// </summary>
    [Fact]
    public void ASessionKey_ForAnIdWithRowsAnotherPersonsDirectorWrote_IsRefused_EvenWhenNobodyListsIt()
    {
        var bob = Hello(_teamA, Bob, "director-bob");
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        Assert.Equal(2, bob.Hub.PushTurns(1, Conversation("session-bob", 0, 2))!.Count);
        bob.Hub.PushSnapshot(2, Array.Empty<SessionDto>());
        Assert.Empty(_store.DirectorsHoldingSession(new TenantId(_teamA), "session-bob"));

        var alice = Hello(_teamA, Alice, "director-alice");
        Assert.Throws<HubException>(() => RegisterSessionKey(alice, "session-bob"));
        Assert.Null(_sessionKeys.DirectorOfSession(new TenantId(_teamA), "session-bob"));
    }

    /// <summary>
    /// #3552 review S2-F10. Setting one Director up again revokes the person's other key for it - and "the same
    /// Director" is decided by <see cref="DeviceCredentialIdentity.SameDirectorId"/> on the person's rows in memory, not
    /// by a suffix match in the database. One person sets one Director id up for team A and then for team B in another
    /// letter case: the first key is revoked, so the Director is in one place. The non-ASCII pair is the case SQLite's
    /// own matching does not fold, so it would keep both keys against the old query on the test database as well as on
    /// PostgreSQL.
    /// </summary>
    [Theory]
    [InlineData("director-alice", "DIRECTOR-ALICE")]
    [InlineData("director-bjørn", "DIRECTOR-BJØRN")]
    public void SettingOneDirectorUpAgain_UnderItsIdInAnotherLetterCase_RevokesThePersonsFirstKey(string first, string again)
    {
        var firstKey = EnrolledKey(Alice, first, _teamA);
        var secondKey = EnrolledKey(Alice, again, _teamB);

        Assert.Equal(DeviceCredentialResolutionKind.Revoked, KindOf(firstKey));
        Assert.Equal(DeviceCredentialResolutionKind.Active, KindOf(secondKey));
        Assert.Single(_devices.ActiveKeysOfDirector(Alice, first));
        Assert.Empty(_devices.ActiveKeysOfDirector(Bob, first));
    }

    /// <summary>#3552 review S2-F9: the refusal tells the person what to do, not only what happened.</summary>
    [Fact]
    public void TheDirectorIdTakenRefusal_TellsThePersonWhatToDo()
    {
        EnrolledKey(Bob, "director-bob", _teamA);
        var refused = Enroll(Alice, "director-bob", _teamA);
        Assert.Equal(409, refused.Status);
        Assert.Contains("Ask the team's Owner, or set this computer up as a new Director.", refused.Error);
    }

    // ---- #3552 review round 4: S2-F11 (who may end a key), S2-F12 (the honest refresh), questions 1 and 2 -----------

    private static string R4Hash(string rawKey) =>
        Convert.ToHexString(CcDirector.Core.Security.GatewaySessionKey.HashBytes(rawKey)).ToLowerInvariant();

    private static void R4Register(Connected director, string sessionId, string rawKey, DateTime expiresUtc) =>
        director.Hub.RegisterSessionKey(new SessionKeyRegistration { SessionId = sessionId, KeyHash = R4Hash(rawKey), ExpiresAtUtc = expiresUtc });

    /// <summary>
    /// #3552 review S2-F11, the reviewer's first round-4 reproduction turned round. It passed on 80d46ca40: Alice's
    /// Director ended the key of Bob's session, and since a revoked row is never revived, Bob's session had no Gateway
    /// credential for the rest of its life. Now, in a team, a revoke ends a row only when it names the calling Director:
    /// Alice's ends nothing, Bob's key still works and can still be refreshed, and Bob's own revoke still ends it.
    /// </summary>
    [Fact]
    public void AColleaguesDirector_RevokingAnotherPersonsSessionKey_EndsNothing_AndTheOwnersOwnRevokeStillWorks()
    {
        var sid = Guid.NewGuid().ToString("D");
        var raw = "raw-key-of-bobs-session";
        var bob = Hello(_teamA, Bob, "director-bob");
        R4Register(bob, sid, raw, DateTime.UtcNow.AddHours(1));
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = sid } });
        Assert.Equal(SessionCredentialResolutionKind.Active, _sessionKeys.ResolveCredential(raw).Kind);

        var alice = Hello(_teamA, Alice, "director-alice");
        alice.Hub.RevokeSessionKey(sid);
        // And a revoke for an id with no key row at all ends nothing either.
        alice.Hub.RevokeSessionKey(Guid.NewGuid().ToString("D"));

        Assert.Equal(SessionCredentialResolutionKind.Active, _sessionKeys.ResolveCredential(raw).Kind);
        R4Register(bob, sid, raw, DateTime.UtcNow.AddHours(2));
        Assert.Equal(SessionCredentialResolutionKind.Active, _sessionKeys.ResolveCredential(raw).Kind);

        bob.Hub.RevokeSessionKey(sid);
        Assert.Equal(SessionCredentialResolutionKind.Revoked, _sessionKeys.ResolveCredential(raw).Kind);
    }

    /// <summary>
    /// #3552 review S2-F11: a PERSONAL tenant is unchanged. It is one person's, so any of that person's Directors may
    /// end a key of that account's sessions, exactly as before - the team rule is asked only on a team key's connection.
    /// </summary>
    [Fact]
    public void InAPersonalTenant_AnotherDirectorOfTheSamePerson_StillEndsTheSessionsKey_AsBefore()
    {
        var personal = _tenants.MintOrLookupBySubject(Alice, null);
        Connected Personal(string directorId)
        {
            var key = _devices.RegisterForTenant(personal, Alice, "personal-namespace|" + directorId, "M").DeviceKey;
            Assert.False(_devices.ResolveCredential(key).Identity!.IsTeamKey);
            var connected = SayHello(key, directorId);
            Assert.False(connected.Context.Aborted);
            return connected;
        }

        var laptop = Personal("director-laptop");
        var desktop = Personal("director-desktop");
        var sid = Guid.NewGuid().ToString("D");
        var raw = "raw-key-of-a-personal-session";
        R4Register(laptop, sid, raw, DateTime.UtcNow.AddHours(1));
        Assert.Equal(SessionCredentialResolutionKind.Active, _sessionKeys.ResolveCredential(raw).Kind);

        desktop.Hub.RevokeSessionKey(sid);
        Assert.Equal(SessionCredentialResolutionKind.Revoked, _sessionKeys.ResolveCredential(raw).Kind);
    }

    /// <summary>
    /// #3552 review S2-F12, the reviewer's second round-4 reproduction turned round. It passed on 80d46ca40: while Alice
    /// listed Bob's session id, every refresh of the key row that already named Bob's Director was refused, and the key
    /// ran out. Now the roster is asked only while there is no key row; with one, the registry decides, so Bob's refresh
    /// is accepted while Alice lists the id and the key does not run out.
    /// </summary>
    [Fact]
    public void WhileAColleagueListsASessionId_ItsOwnDirectorStillRefreshesTheSessionsKey_SoTheKeyDoesNotRunOut()
    {
        var sid = Guid.NewGuid().ToString("D");
        var raw = "raw-key-of-bobs-session-2";
        var bob = Hello(_teamA, Bob, "director-bob");
        R4Register(bob, sid, raw, DateTime.UtcNow.AddSeconds(2));
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = sid } });

        var alice = Hello(_teamA, Alice, "director-alice");
        alice.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = sid } });

        // Bob's refresh of the row that already names his Director is accepted.
        R4Register(bob, sid, raw, DateTime.UtcNow.AddHours(1));
        Thread.Sleep(2500);
        Assert.Equal(SessionCredentialResolutionKind.Active, _sessionKeys.ResolveCredential(raw).Kind);
        Assert.Equal("director-bob", _sessionKeys.DirectorOfSession(new TenantId(_teamA), sid));
        // Alice still cannot write it.
        Assert.Throws<HubException>(() => R4Register(alice, sid, "alice-raw", DateTime.UtcNow.AddHours(1)));
    }

    /// <summary>
    /// #3552 review S2-F12, the reviewer's third round-4 reproduction turned round. It passed on 80d46ca40: Bob's
    /// refresh was refused while Alice listed the id, the key lapsed, the expiry sweep revoked it, and Bob's registration
    /// was refused for good. Now the refresh is accepted, so the sweep finds nothing lapsed and the key stays working
    /// after Alice stops.
    /// </summary>
    [Fact]
    public void WhileAColleagueListsASessionId_TheExpirySweepFindsTheOwnersRefreshedKey_AndLeavesItWorking()
    {
        var sid = Guid.NewGuid().ToString("D");
        var raw = "raw-key-of-bobs-session-3";
        var bob = Hello(_teamA, Bob, "director-bob");
        R4Register(bob, sid, raw, DateTime.UtcNow.AddSeconds(2));
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = sid } });
        var alice = Hello(_teamA, Alice, "director-alice");
        alice.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = sid } });

        R4Register(bob, sid, raw, DateTime.UtcNow.AddHours(1));
        Thread.Sleep(2500);
        Assert.Equal(0, _sessionKeys.SweepExpired());
        alice.Hub.PushSnapshot(2, Array.Empty<SessionDto>());
        R4Register(bob, sid, raw, DateTime.UtcNow.AddHours(1));
        Assert.Equal(SessionCredentialResolutionKind.Active, _sessionKeys.ResolveCredential(raw).Kind);
    }

    /// <summary>
    /// #3552 review S2-F13, the reviewer's fourth round-4 reproduction, kept AS IT IS: it still passes, on purpose. After
    /// a Gateway restart the roster is empty, so a session that still has no key row can be keyed by a colleague's
    /// Director first. The Gateway cannot close this from memory; the Director half - registering keys before the roster
    /// on a reseed - closes it, and Teams must not be released without it. This test pins the gap so that change is
    /// seen when it lands: it is the one to turn round then.
    /// </summary>
    [Fact]
    public void Gap_AfterAGatewayRestart_ASessionStillWithoutAKeyRow_CanBeKeyedByAColleagueFirst_UntilTheDirectorSendsKeysFirst()
    {
        var sid = Guid.NewGuid().ToString("D");
        var bob = Hello(_teamA, Bob, "director-bob");
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = sid } });
        var aliceKey = _devices.RegisterForTenant(new TenantId(_teamA), Alice,
            HostedEnrollmentEndpoint.TeamScopedDeviceId(_teamA, Alice, "director-alice"), "M").DeviceKey;
        var alice = SayHello(aliceKey, "director-alice");
        Assert.Throws<HubException>(() => R4Register(alice, sid, "alice-raw", DateTime.UtcNow.AddHours(1)));

        _directors = new DirectorRegistry(_harness.LegacyPath("instances-after-restart-r4"));
        _store = new PushedSessionStore();
        alice = SayHello(aliceKey, "director-alice");
        R4Register(alice, sid, "alice-raw", DateTime.UtcNow.AddHours(1));
        Assert.Equal("director-alice", _sessionKeys.DirectorOfSession(new TenantId(_teamA), sid));
    }

    /// <summary>
    /// #3552 review round 4, Tech Lead question 2. A turn end reported for a session by a Director that lists a
    /// colleague's session id used to move that session's turn and hand the Wingman the reporting Director's id, so the
    /// Wingman read THAT Director's screen and stored the verdict under the colleague's session. Now, in a team, the
    /// watcher takes a report only from the session's own Director (the one rule, ClaimOf), through both of its feeds:
    /// the hub's report and its own sweep of the roster.
    /// </summary>
    [Fact]
    public async Task ATurnEnd_ReportedForAColleaguesSession_ByADirectorThatListsItsId_IsNotTaken_ThroughEitherFeed()
    {
        var bob = Hello(_teamA, Bob, "director-bob");
        RegisterSessionKey(bob, "session-bob");
        var alice = Hello(_teamA, Alice, "director-alice");
        var ownership = new TeamCallerOwnership(_directors, _store, _devices, _turns, _boundary, _sessionKeys);
        var team = new TenantId(_teamA);
        var signals = new List<CcDirector.Gateway.Briefing.TurnEndSignal>();
        var removed = new List<string>();
        var watcher = new CcDirector.Gateway.Briefing.TurnEndWatcher(
            onTurnEnd: signals.Add,
            onSessionWorking: (_, _, _) => { },
            pushedSessions: _store,
            onSessionRemoved: (_, sid, directorId) => removed.Add(directorId),
            acceptsReport: (tenant, sid, directorId, isRemoval) => ownership.AcceptsReport(tenant, directorId, sid, isRemoval));

        // The hub's feed. Bob's session is working; Alice reports it as waiting: nothing is taken.
        watcher.Observe(team, "session-bob", "Working", "director-bob");
        watcher.Observe(team, "session-bob", "WaitingForInput", "director-alice");
        Assert.Empty(signals);
        // Alice "removing" it forgets nothing of Bob's session.
        watcher.ObserveRemoval(team, "session-bob", "director-alice");
        Assert.Empty(removed);
        // Bob's own stop is taken, with his Director's id - so the Wingman reads his screen.
        watcher.Observe(team, "session-bob", "WaitingForInput", "director-bob");
        var signal = Assert.Single(signals);
        Assert.Equal("director-bob", signal.DirectorId);

        // The sweep's feed: both rosters list the id; only Bob's entry moves the turn.
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob", ActivityState = "Working" } });
        alice.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob", ActivityState = "WaitingForInput" } });
        signals.Clear();
        watcher.Observe(team, "session-bob", "Working", "director-bob");
        await watcher.SweepAsync(sweepAll: true);
        Assert.Empty(signals);
    }

    /// <summary>
    /// #3552 review round 4, Tech Lead question 1. A Director that lists a colleague's session id was sent that
    /// session's folded display state on every change - the state label carries the Wingman's own line about the
    /// session, beside its snooze times and the count of messages waiting for it, none of which any surface shows another
    /// member (the team Fleet Map shows a name and one of three words). Now, in a team, the fold is sent only to the
    /// session's own Director; the stand-in fold below stamps a line on every entry so a send to Alice would carry it.
    /// </summary>
    [Fact]
    public async Task AColleaguesSessionsFoldedState_IsSentOnlyToItsOwnDirector_NotToADirectorThatListsItsId()
    {
        var bob = Hello(_teamA, Bob, "director-bob");
        RegisterSessionKey(bob, "session-bob");
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob", ActivityState = "WaitingForInput" } });
        var alice = Hello(_teamA, Alice, "director-alice");
        alice.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob", ActivityState = "Idle" } });
        var ownership = new TeamCallerOwnership(_directors, _store, _devices, _turns, _boundary, _sessionKeys);
        var team = new TenantId(_teamA);

        var sentTo = new List<string>();
        var observer = new CcDirector.Gateway.Fleet.FleetDisplayStateObserver(
            () => _store.SnapshotConnected(team),
            rows =>
            {
                foreach (var row in rows)
                {
                    row.EffectiveColor = "green";
                    row.StateLabel = "Report - the line the Wingman wrote about Bob's turn " + row.ActivityState;
                }
            },
            (directorId, command, ct) =>
            {
                lock (sentTo) sentTo.Add(directorId);
                return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success());
            },
            mayReceive: (directorId, sid) => ownership.AcceptsReport(team, directorId, sid, isRemoval: false));

        observer.Sweep();
        await observer.PushSessionAsync("session-bob", CancellationToken.None);

        lock (sentTo)
        {
            Assert.Contains("director-bob", sentTo);
            Assert.DoesNotContain("director-alice", sentTo);
        }
    }

    [Fact]
    public void PushTurns_ANewSessionWithNothingStored_IsAcceptedOnlyFromADirectorWhoseRosterHoldsIt()
    {
        var alice = Hello(_teamA, Alice, "director-alice");

        // Not in her roster yet: refused for now, with an answer on no generation so a real Director re-reads and sends
        // it again at its next trigger instead of dropping it.
        var early = alice.Hub.PushTurns(1, Conversation("session-new", 0, 2));
        Assert.NotNull(early);
        Assert.Equal("", early!.Generation);
        Assert.Equal(0, early.Count);
        using (_boundary.EnterScope(new TenantId(_teamA)))
            Assert.Null(_turns.ReadHead("session-new"));

        alice.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-new" } });
        var accepted = alice.Hub.PushTurns(2, Conversation("session-new", 0, 2));
        Assert.Equal(2, accepted!.Count);
        Assert.Equal(TeamOwnership.Callers, Whose(_teamA, Alice, "session-new"));
    }

    [Fact]
    public void PushTurns_TwoDirectorsOfTheSamePerson_MayBothWriteTheirSession()
    {
        var laptop = Hello(_teamA, Alice, "director-alice-laptop");
        laptop.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-alice" } });
        laptop.Hub.PushTurns(1, Conversation("session-alice", 0, 2));

        var desktop = Hello(_teamA, Alice, "director-alice-desktop");
        Assert.Equal(3, desktop.Hub.PushTurns(1, Conversation("session-alice", 2, 1))!.Count);
    }

    [Fact]
    public void PushTurns_APersonalKey_IsNotAsked_AndWritesAsBefore()
    {
        // A personal account's Director: nothing in its roster, and another of the account's Directors wrote the
        // session. Both are accepted, as before the team rule.
        var personal = _tenants.MintOrLookupBySubject(Alice, null);
        var first = SayHello(_devices.RegisterForTenant(personal, Alice, "home-1", "M").DeviceKey, "home-1");
        Assert.False(first.Context.Aborted);
        Assert.Equal(2, first.Hub.PushTurns(1, Conversation("session-home", 0, 2))!.Count);

        var second = SayHello(_devices.RegisterForTenant(personal, Alice, "home-2", "M").DeviceKey, "home-2");
        Assert.Equal(3, second.Hub.PushTurns(1, Conversation("session-home", 2, 1))!.Count);
        using (_boundary.EnterScope(personal))
            Assert.Equal("home-2", _turns.ReadHead("session-home")!.DirectorId);
    }

    [Fact]
    public void OwnerOf_ADirectorWhoseKeyIsRevoked_IsNobodys_AndItsSessionIsRefusedToItsFormerOwner()
    {
        var key = EnrolledKey(Alice, "director-alice", _teamA);
        var alice = SayHello(key, "director-alice");
        alice.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-alice" } });
        var ownership = new TeamCallerOwnership(_directors, _store, _devices, _turns, _boundary, _sessionKeys);
        Assert.Equal(Alice, ownership.OwnerOf(new TenantId(_teamA), "director-alice"));

        Assert.True(_devices.RevokeDevice(_devices.ResolveCredential(key).Identity!.DeviceId, "test_reason"));

        Assert.Null(ownership.OwnerOf(new TenantId(_teamA), "director-alice"));
        Assert.Equal(TeamOwnership.Unknown, Whose(_teamA, Alice, "session-alice"));
        Assert.Null(ownership.OwnerOf(new TenantId(_teamB), "director-alice"));
    }

    [Fact]
    public void AMembersOwnSession_WithItsOwnStoredConversation_IsTheirs_AndNoOneElses()
    {
        var alice = Hello(_teamA, Alice, "director-alice");
        alice.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-alice" } });
        alice.Hub.PushTurns(1, Conversation("session-alice", 0, 2));

        Assert.Equal(TeamOwnership.Callers, Whose(_teamA, Alice, "session-alice"));
        Assert.Equal(TeamOwnership.SomeoneElses, Whose(_teamA, Bob, "session-alice"));
    }

    [Fact]
    public void OwnerOf_ADirectorRegisteredInATeamByAKeyBoundToAnotherTeam_IsNobodys()
    {
        // #3530 review round 3, R3-F4: the line that can fail on the tenant check alone. The Director is listed in team A,
        // registered by an ACTIVE key whose row is bound to team B - the Director still listed in the team it left. Its
        // key is not revoked and the registration is real, so only the tenant check can answer nobody's.
        var deviceId = HostedEnrollmentEndpoint.TeamScopedDeviceId(_teamB, Alice, "director-alice");
        var key = _devices.RegisterForTenant(new TenantId(_teamB), Alice, deviceId, "M").DeviceKey;
        Assert.Equal(DeviceCredentialResolutionKind.Active, KindOf(key));
        _directors.RegisterFromStream("director-alice", "M", "u", "1", 1, DateTime.UtcNow, new TenantId(_teamA),
            registeredByCredential: "device:" + deviceId);
        _directors.RegisterFromStream("director-alice", "M", "u", "1", 1, DateTime.UtcNow, new TenantId(_teamB),
            registeredByCredential: "device:" + deviceId);
        var ownership = new TeamCallerOwnership(_directors, _store, _devices, _turns, _boundary, _sessionKeys);

        Assert.Null(ownership.OwnerOf(new TenantId(_teamA), "director-alice"));
        // Control: the same registration in the tenant the key IS bound to names its person.
        Assert.Equal(Alice, ownership.OwnerOf(new TenantId(_teamB), "director-alice"));
    }

    // ---- Seam 2: the person behind a SESSION key, one resolver for both kinds of key (Gateway step 2, item 5) ------

    private const string Mandy = "sub-mandy";

    /// <summary>The team gate exactly as the Gateway wires it: the same ownership answer the hub asks.</summary>
    private TeamEndpointGate Gate(out TeamCallerOwnership ownership)
    {
        ownership = new TeamCallerOwnership(_directors, _store, _devices, _turns, _boundary, _sessionKeys);
        return new TeamEndpointGate(new TeamAccess(_teams), _teams, _tenants, _boundary, ownership);
    }

    /// <summary>Ask the gate as its middleware does: the caller from the one resolver, and whose the request touches
    /// answered for that caller.</summary>
    private static TeamGateVerdict Ask(TeamEndpointGate gate, TeamCallerOwnership ownership, string method, string pattern,
        string? sid, TenantId tenant, DeviceCredentialIdentity? device, SessionCredentialIdentity? session)
    {
        Func<string, string?> routeValue = name => name == "sid" ? sid : null;
        return gate.Check(method, pattern, routeValue, tenant,
            () => gate.CallerSubject(tenant, device, session),
            _ => gate.CallerSubject(tenant, device, session) is { } person
                ? ownership.Whose(tenant, person, pattern, routeValue)
                : TeamOwnership.Unknown);
    }

    /// <summary>A member's Director says Hello on its own team key; the key's identity, as the auth middleware leaves it.</summary>
    private DeviceCredentialIdentity HelloWithIdentity(string team, string subject, string directorId)
    {
        var key = _devices.RegisterForTenant(new TenantId(team), subject,
            HostedEnrollmentEndpoint.TeamScopedDeviceId(team, subject, directorId), "M").DeviceKey;
        Assert.False(SayHello(key, directorId).Context.Aborted);
        return _devices.ResolveCredential(key).Identity!;
    }

    [Theory]
    [InlineData("GET", "/sessions/{sid}/history")]
    [InlineData("GET", "/sessions/{sid}/wingman-stops")]
    [InlineData("GET", "/sessions/{sid}/turn-verdict")]
    [InlineData("GET", "/sessions/{sid}/turn-verdicts")]
    [InlineData("GET", "/sessions/{sid}/wingman/voice/audio")]
    [InlineData("GET", "/sessions/{sid}/recap")]
    [InlineData("POST", "/sessions/{sid}/recap")]
    [InlineData("GET", "/sessions/{sid}/summary")]
    [InlineData("GET", "/sessions/{sid}/wingman")]
    [InlineData("GET", "/sessions/{sid}/wingman-now")]
    [InlineData("GET", "/sessions/{sid}/wingman-debug")]
    [InlineData("GET", "/sessions/{sid}/wingman/voice")]
    [InlineData("GET", "/sessions/{sid}/wingman/waiting-screen")]
    public void EveryStoredContentRoute_OfAColleaguesEndedSession_IsRefused_ToTheMemberWhoseDirectorNowHoldsItsId(
        string method, string pattern)
    {
        // Gateway review round 2, R2-F1, the walk: every {sid} route that serves Gateway-stored content asks the one
        // ownership answer, and none of them serves a colleague's ended session to the member whose Director took its id.
        var bob = Hello(_teamA, Bob, "director-bob");
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        bob.Hub.PushTurns(1, Conversation("session-bob", 0, 2));
        bob.Hub.PushSnapshot(2, Array.Empty<SessionDto>());
        var aliceKey = _devices.RegisterForTenant(new TenantId(_teamA), Alice,
            HostedEnrollmentEndpoint.TeamScopedDeviceId(_teamA, Alice, "director-alice"), "M").DeviceKey;
        var alice = SayHello(aliceKey, "director-alice");
        alice.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-bob" } });
        var gate = Gate(out var ownership);

        var verdict = Ask(gate, ownership, method, pattern, "session-bob", new TenantId(_teamA),
            _devices.ResolveCredential(aliceKey).Identity, null);

        Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
        Assert.NotEqual(TeamEndpointGate.CallerUnknownRefusal, verdict.Message);
    }

    [Fact]
    public void PersonOf_ASessionKeyInATeam_IsItsDirectorsOwner_ReadLive_AndNobodyOnceTheirKeyIsRevoked()
    {
        var alice = Hello(_teamA, Alice, "director-alice");
        var ownership = new TeamCallerOwnership(_directors, _store, _devices, _turns, _boundary, _sessionKeys);
        var session = new SessionCredentialIdentity(Guid.NewGuid(), new TenantId(_teamA), "director-alice");

        Assert.Equal(Alice, ownership.PersonOf(new TenantId(_teamA), null, session));
        Assert.Null(ownership.PersonOf(new TenantId(_teamB), null, session));
        Assert.Null(ownership.PersonOf(new TenantId(_teamA), null, session with { DirectorId = "director-nobody" }));
        Assert.Null(ownership.PersonOf(new TenantId(_teamA), null, null));

        Assert.True(_teams.RemoveMember(_teamA, Alice).IsDone);
        Assert.Null(ownership.PersonOf(new TenantId(_teamA), null, session));
        Assert.True(alice.Context.Aborted);
    }

    [Fact]
    public void ASessionsOwnSkillsFetch_IsAllowedForItsOwner_AndRefusedAsUnidentifiedOnceTheOwnerIsRemoved()
    {
        Hello(_teamA, Alice, "director-alice");
        var gate = Gate(out var ownership);
        var team = new TenantId(_teamA);
        var session = new SessionCredentialIdentity(Guid.NewGuid(), team, "director-alice");

        var allowed = Ask(gate, ownership, "GET", "/gateway/skills", null, team, null, session);
        Assert.Equal(TeamGateOutcome.Allowed, allowed.Outcome);

        Assert.True(_teams.RemoveMember(_teamA, Alice).IsDone);
        var refused = Ask(gate, ownership, "GET", "/gateway/skills", null, team, null, session);
        Assert.Equal(TeamGateOutcome.Refused, refused.Outcome);
        Assert.Equal(TeamEndpointGate.CallerUnknownRefusal, refused.Message);
    }

    [Fact]
    public void ASessionKey_IsItsDirectorOwnersOwnSession_ForCallersOwn()
    {
        var alice = Hello(_teamA, Alice, "director-alice");
        var sid = Guid.NewGuid();
        alice.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = sid.ToString() } });
        var gate = Gate(out var ownership);
        var team = new TenantId(_teamA);

        var own = Ask(gate, ownership, "GET", "/sessions/{sid}", sid.ToString(), team, null,
            new SessionCredentialIdentity(sid, team, "director-alice"));
        Assert.Equal(TeamGateOutcome.Allowed, own.Outcome);
    }

    [Fact]
    public void CallerSubject_ASessionKeyInAPersonalTenant_IsThatTenantsPerson_AsBefore()
    {
        var personal = _tenants.MintOrLookupBySubject(Alice, null);
        var gate = Gate(out _);

        Assert.Equal(Alice, gate.CallerSubject(personal, null, new SessionCredentialIdentity(Guid.NewGuid(), personal, "home-1")));
        Assert.Equal(Alice, gate.CallerSubject(personal, null, null));
    }

    [Theory]
    [InlineData("GET", "/sessions/{sid}")]
    [InlineData("GET", "/sessions/{sid}/history")]
    [InlineData("POST", "/sessions/{sid}/prompt")]
    public void AManagersTeamKey_AgainstAnotherPersonsSession_IsRefusedAsSomeoneElses_NotAsUnknown_ForADeviceKeyAndASessionKey(
        string method, string pattern)
    {
        // #2312's Test 3 at the gate (reviews/review-2312-pr2.md F2): open, read and type into a colleague's session.
        // The refusal must be the role table's "not your own" answer - a regression to "caller unknown" fails here.
        Assert.True(_teams.AddMember(_teamA, Mandy, TeamRole.Manager).IsDone);
        var bob = Hello(_teamA, Bob, "director-bob");
        var bobsSession = Guid.NewGuid().ToString();
        bob.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = bobsSession } });
        var mandy = HelloWithIdentity(_teamA, Mandy, "director-mandy");
        var gate = Gate(out var ownership);
        var team = new TenantId(_teamA);
        var notYourOwn = new TeamAccess(_teams).Decide(_teamA, Mandy, TeamAction.JoinOrWatchSomeoneElsesSession).Refusal;
        Assert.False(string.IsNullOrEmpty(notYourOwn));

        var byDevice = Ask(gate, ownership, method, pattern, bobsSession, team, mandy, null);
        var bySession = Ask(gate, ownership, method, pattern, bobsSession, team, null,
            new SessionCredentialIdentity(Guid.NewGuid(), team, "director-mandy"));

        foreach (var verdict in new[] { byDevice, bySession })
        {
            Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
            Assert.Equal(TeamAction.JoinOrWatchSomeoneElsesSession, verdict.Action);
            Assert.Equal(notYourOwn, verdict.Message);
            Assert.NotEqual(TeamEndpointGate.CallerUnknownRefusal, verdict.Message);
            Assert.NotEqual(TeamEndpointGate.OwnershipUnknownRefusal, verdict.Message);
        }
    }

    // ---- Review F1: setting a Director up again somewhere else is a move, with the move's rules -------------------

    [Fact]
    public void SettingADirectorUpForAnotherTeam_WhileItHasASessionRegistered_IsRefused_AndItsKeyAndTunnelStay()
    {
        var key = EnrolledKey(Alice, "director-x", _teamA);
        var tunnel = SayHello(key, "director-x");
        tunnel.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-1" } });

        var result = Enroll(Alice, "director-x", _teamB);

        Assert.Equal(409, result.Status);
        Assert.Equal(HostedEnrollmentEndpoint.MoveWithSessionsRefusal, result.Error);
        Assert.False(tunnel.Context.Aborted);
        Assert.Equal(DeviceCredentialResolutionKind.Active, KindOf(key));
        Assert.Equal(new[] { "session-1" }, Sessions(_teamA));
    }

    [Fact]
    public void SettingADirectorUpForThePersonsOwnAccount_WhileItHasASessionInATeam_IsRefused_AndNothingChanges()
    {
        var key = EnrolledKey(Alice, "director-x", _teamA);
        var tunnel = SayHello(key, "director-x");
        tunnel.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-1" } });

        var result = Enroll(Alice, "director-x", null);

        Assert.Equal(409, result.Status);
        Assert.Equal(HostedEnrollmentEndpoint.MoveWithSessionsRefusal, result.Error);
        Assert.False(tunnel.Context.Aborted);
        Assert.Equal(DeviceCredentialResolutionKind.Active, KindOf(key));
    }

    [Fact]
    public void SettingADirectorUpForAnotherTeam_WithNoSession_RevokesTheOldKey_AndCutsTheOldTunnel()
    {
        var key = EnrolledKey(Alice, "director-x", _teamA);
        var tunnel = SayHello(key, "director-x");
        var othersTunnel = Hello(_teamA, Bob, "director-bob");

        var newKey = EnrolledKey(Alice, "director-x", _teamB);

        Assert.True(tunnel.Context.Aborted);
        Assert.False(othersTunnel.Context.Aborted);
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, KindOf(key));
        Assert.Equal(_teamB, _devices.ResolveCredential(newKey).Identity!.TenantId);
        Assert.False(SayHello(newKey, "director-x").Context.Aborted);
    }

    [Fact]
    public void SettingADirectorUpAgainInTheSameTeam_WithASessionAndATunnel_KeepsTodaysBehaviour()
    {
        EnrolledKey(Alice, "director-x", _teamA);
        var tunnel = SayHello(EnrolledKey(Alice, "director-x", _teamA), "director-x");
        tunnel.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-1" } });

        var again = Enroll(Alice, "director-x", _teamA);

        Assert.Equal(200, again.Status);
        Assert.False(tunnel.Context.Aborted);
        Assert.Equal(DeviceCredentialResolutionKind.Active, KindOf(again.Response!.DeviceKey));
        Assert.Equal(new[] { "session-1" }, Sessions(_teamA));
    }

    // ---- Review F3: the move reads the real roster, by the id the Director said Hello with ----------------------

    [Fact]
    public void Move_WithASessionInTheRealRoster_IsRefused_ThenOnceItIsClosed_MovesAndCutsTheTunnel()
    {
        var key = EnrolledKey(Alice, "director-x", _teamA);
        var tunnel = SayHello(key, "director-x");
        tunnel.Hub.PushSnapshot(1, new[] { new SessionDto { SessionId = "session-1" } });

        var refused = Move(Alice, "director-x", _teamB);

        Assert.Equal(409, refused.Status);
        Assert.Equal(HostedEnrollmentEndpoint.MoveWithSessionsRefusal, refused.Error);
        Assert.False(tunnel.Context.Aborted);
        Assert.Equal(DeviceCredentialResolutionKind.Active, KindOf(key));

        // The Director closes its last session and says so in its next snapshot.
        tunnel.Hub.PushSnapshot(2, Array.Empty<SessionDto>());
        var moved = Move(Alice, "director-x", _teamB);

        Assert.Equal(200, moved.Status);
        Assert.True(tunnel.Context.Aborted);
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, KindOf(key));
        Assert.Equal(_teamB, _devices.ResolveCredential(moved.Response!.DeviceKey).Identity!.TenantId);
    }

    private sealed class FakeHubCtx : HubCallerContext
    {
        public FakeHubCtx(string connectionId, HttpContext http)
        {
            ConnectionId = connectionId;
            Features.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(new HttpContextFeatureImpl { HttpContext = http });
        }

        public bool Aborted { get; private set; }
        public override string ConnectionId { get; }
        public override string? UserIdentifier => null;
        public override System.Security.Claims.ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() => Aborted = true;

        private sealed class HttpContextFeatureImpl : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
        {
            public HttpContext? HttpContext { get; set; }
        }
    }
}
