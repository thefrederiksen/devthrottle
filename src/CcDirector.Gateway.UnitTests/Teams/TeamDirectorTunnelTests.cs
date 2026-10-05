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
    private readonly DirectorRegistry _directors;
    private readonly PushedSessionStore _store = new();
    private readonly GatewayStreamRegistry _streams = new();
    private readonly DirectorConnectionRegistry _connections = new();
    private readonly GatewayInputStatsAggregator _inputStats;
    private readonly AsyncLocalTenantContext _tenantContext = new();
    private readonly SessionTurnStore _turns;
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
        _tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, _tenants);
        _devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: true);
        _boundary = new HostedTenantBoundary(_tenantContext, _devices);
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
            tenantBoundary: _boundary, connections: _connections, sessionTurns: _turns,
            teamOwnership: new TeamCallerOwnership(_directors, _store, _devices, _turns, _boundary)) { Context = ctx };
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
        new TeamCallerOwnership(_directors, _store, _devices, _turns, _boundary)
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
        var ownership = new TeamCallerOwnership(_directors, _store, _devices, _turns, _boundary);
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
        var ownership = new TeamCallerOwnership(_directors, _store, _devices, _turns, _boundary);

        Assert.Null(ownership.OwnerOf(new TenantId(_teamA), "director-alice"));
        // Control: the same registration in the tenant the key IS bound to names its person.
        Assert.Equal(Alice, ownership.OwnerOf(new TenantId(_teamB), "director-alice"));
    }

    // ---- Seam 2: the person behind a SESSION key, one resolver for both kinds of key (Gateway step 2, item 5) ------

    private const string Mandy = "sub-mandy";

    /// <summary>The team gate exactly as the Gateway wires it: the same ownership answer the hub asks.</summary>
    private TeamEndpointGate Gate(out TeamCallerOwnership ownership)
    {
        ownership = new TeamCallerOwnership(_directors, _store, _devices, _turns, _boundary);
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

    [Fact]
    public void PersonOf_ASessionKeyInATeam_IsItsDirectorsOwner_ReadLive_AndNobodyOnceTheirKeyIsRevoked()
    {
        var alice = Hello(_teamA, Alice, "director-alice");
        var ownership = new TeamCallerOwnership(_directors, _store, _devices, _turns, _boundary);
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
