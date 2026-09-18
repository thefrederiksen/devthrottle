using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Fleet;

/// <summary>
/// HAND OVER (the Fleet Manager mission, step 8): every refusal is one sentence from the Gateway and sends nothing to a
/// Director; the owner's hand over works in both directions; an older Director is refused before anything is sent; and
/// every change is audited and told to the Fleet Manager's events. The roster is resolved by the real
/// <see cref="FleetRoleResolver"/>, as production resolves it; only the Director, the audit trail and the events are
/// doubles.
/// </summary>
public sealed class FleetManagerHandOverServiceTests
{
    private static readonly TenantId Tenant = new("acct-hand-over");
    private static readonly TenantId OtherTenant = new("acct-someone-else");

    private const string Fm = "10000000-0000-4000-8000-000000000001";
    private const string Plain = "10000000-0000-4000-8000-000000000002";
    private const string Owned = "10000000-0000-4000-8000-000000000003";
    private const string Architect = "10000000-0000-4000-8000-000000000004";
    private const string ArchitectWorker = "10000000-0000-4000-8000-000000000005";
    private const string Orphan = "10000000-0000-4000-8000-000000000006";
    private const string Ended = "10000000-0000-4000-8000-000000000007";
    private const string Foreign = "10000000-0000-4000-8000-000000000008";
    private const string Gone = "10000000-0000-4000-8000-000000000099";
    private const string Actor = "device phone phone-1";

    private sealed class FakeWorld : IFleetManagerHandOverEnvironment
    {
        public string? Mark = Fm;
        public readonly Dictionary<TenantId, List<(string DirectorId, SessionDto Session)>> Rosters = new();
        public readonly HashSet<string> OldDirectors = new(StringComparer.Ordinal);
        public readonly List<(string DirectorId, string SessionId, string? Controller)> Sent = new();
        public readonly List<(string SessionId, string Actor, string Detail)> Audited = new();
        public readonly List<SessionDto> OwnerChanges = new();
        public string? DirectorError;
        public string? DirectorAnswersOwner = "unset";
        public bool AuditFails;

        public string? MarkedFleetManager(TenantId tenant) => Mark;

        public string? Successor;

        public string? WaitingFleetManager(TenantId tenant) => Successor;

        public IReadOnlyList<(string DirectorId, SessionDto Session)> Roster(TenantId tenant)
        {
            if (!Rosters.TryGetValue(tenant, out var list)) return Array.Empty<(string, SessionDto)>();
            var copies = list.Select(r => (r.DirectorId, Session: r.Session.Clone())).ToList();
            FleetRoleResolver.Stamp(copies.Select(c => c.Session).ToList(), Mark);
            return copies;
        }

        public bool ChangesOwnerIfExpected(TenantId tenant, string directorId) => !OldDirectors.Contains(directorId);

        public readonly List<string?> Expected = new();
        public string? OwnerMovedError;

        public Task<(SessionDto? Session, string? Error, bool OwnerMoved)> SetControllerAsync(TenantId tenant, string directorId,
            string sessionId, string? expectedControllerSessionId, string? controllerSessionId, CancellationToken ct)
        {
            Sent.Add((directorId, sessionId, controllerSessionId));
            Expected.Add(expectedControllerSessionId);
            if (OwnerMovedError is not null) return Task.FromResult<(SessionDto?, string?, bool)>((null, OwnerMovedError, true));
            if (DirectorError is not null) return Task.FromResult<(SessionDto?, string?, bool)>((null, DirectorError, false));
            var row = Rosters[tenant].First(r => r.Session.SessionId == sessionId).Session.Clone();
            row.ControllerSessionId = DirectorAnswersOwner == "unset" ? controllerSessionId : DirectorAnswersOwner;
            row.IsControlled = row.ControllerSessionId is not null;
            return Task.FromResult<(SessionDto?, string?, bool)>((row, null, false));
        }

        public void Audit(TenantId tenant, string sessionId, string actor, string detail)
        {
            if (AuditFails) throw new InvalidOperationException("the trail is read-only today");
            Audited.Add((sessionId, actor, detail));
        }

        public void OwnerChanged(TenantId tenant, string directorId, SessionDto row) => OwnerChanges.Add(row);
    }

    private readonly FakeWorld _world = new();
    private readonly FleetManagerHandOverService _service;

    public FleetManagerHandOverServiceTests()
    {
        _service = new FleetManagerHandOverService(_world);
        _world.Rosters[Tenant] = new List<(string, SessionDto)>
        {
            ("dir-1", Row(Fm, "The Fleet Manager")),
            ("dir-1", Row(Plain, "Plain work")),
            ("dir-1", Row(Owned, "Already the Fleet Manager's", controller: Fm)),
            ("dir-2", Row(Architect, "An Architect")),
            ("dir-2", Row(ArchitectWorker, "The Architect's Worker", controller: Architect)),
            ("dir-1", Row(Orphan, "Its owner has gone", controller: Gone)),
            ("dir-1", Row(Ended, "Finished and gone", state: "Exited")),
        };
        _world.Rosters[OtherTenant] = new List<(string, SessionDto)> { ("dir-x", Row(Foreign, "Another account's")) };
    }

    private static SessionDto Row(string sid, string name, string? controller = null, string state = "WaitingForInput") => new()
    {
        SessionId = sid,
        Name = name,
        ActivityState = state,
        IsControlled = controller is not null,
        ControllerSessionId = controller,
        MachineName = "WORKSTATION-A",
    };

    private Task<FleetHandOverResult> HandAsync(string session, string to)
        => _service.HandOverAsync(Tenant, new FleetHandOverRequest { Session = session, To = to }, Actor, CancellationToken.None);

    private Task<FleetHandOverResult> HandAsSessionAsync(string caller, string session, string to)
        => _service.HandOverAsync(Tenant, new FleetHandOverRequest { Session = session, To = to }, $"session {caller}",
            CancellationToken.None, callingSessionId: caller);

    private void AssertRefused(FleetHandOverResult result, int status, string sentencePart)
    {
        Assert.Equal(status, result.Status);
        Assert.Null(result.Answer);
        Assert.Contains(sentencePart, result.Error);
        Assert.Empty(_world.Sent);
        Assert.Empty(_world.Audited);
        Assert.Empty(_world.OwnerChanges);
    }

    // ================================================================= the owner's hand over, both ways

    [Theory]
    [InlineData("fleet-manager")]
    [InlineData("owner")]
    public async Task HandOver_TheNewFleetManagerWaitingToTakeOver_IsRefused(string to)
    {
        _world.Successor = Plain;

        var result = await HandAsync(Plain, to);

        AssertRefused(result, 409, "Session \"Plain work\" is the new Fleet Manager, waiting to take over. It answers to you only, so it cannot be handed over.");
    }

    [Fact]
    public async Task HandOver_PlainSessionToTheFleetManager_SetsTheFleetManagerAsOwnerAuditsAndTellsTheEvents()
    {
        var result = await HandAsync(Plain, "fleet-manager");

        Assert.Equal(200, result.Status);
        Assert.Equal(("dir-1", Plain, (string?)Fm), Assert.Single(_world.Sent));
        var answer = result.Answer!;
        Assert.Equal((Plain, "fleet-manager", (string?)Fm, (string?)null), (answer.SessionId, answer.To, answer.OwnerSessionId, answer.PreviousOwnerSessionId));
        Assert.Equal("Session \"Plain work\" is now the Fleet Manager's. When it stops, the Fleet Manager is told instead of you.", answer.Sentence);
        Assert.Equal(Fm, answer.Session!.ControllerSessionId);
        var audited = Assert.Single(_world.Audited);
        Assert.Equal((Plain, Actor), (audited.SessionId, audited.Actor));
        Assert.Equal($"handed to the Fleet Manager {Fm}; owned before by the owner", audited.Detail);
        Assert.Equal(Fm, Assert.Single(_world.OwnerChanges).ControllerSessionId);
    }

    [Theory]
    [InlineData(Plain, "fleet-manager", "none-checked")]
    [InlineData(Orphan, "fleet-manager", Gone)]
    [InlineData(Owned, "owner", Fm)]
    public async Task HandOver_SendsTheOwnerItCheckedForTheDirectorToCompare(string session, string to, string checkedOwner)
    {
        var result = await HandAsync(session, to);

        Assert.Equal(200, result.Status);
        var expected = Assert.Single(_world.Expected);
        Assert.Equal(checkedOwner == "none-checked" ? null : checkedOwner, string.IsNullOrEmpty(expected) ? null : expected);
    }

    [Fact]
    public async Task HandOver_TheDirectorFindsTheOwnerMoved_IsAConflictAndNothingIsRecorded()
    {
        _world.OwnerMovedError = $"session {Plain} is owned by {Architect}, not by (the user) as the change expected";

        var result = await HandAsync(Plain, "fleet-manager");

        Assert.Equal(409, result.Status);
        Assert.Null(result.Answer);
        Assert.StartsWith("Session \"Plain work\" was not handed over: its owner changed while the hand over was on its way", result.Error);
        Assert.Contains(Architect, result.Error);
        Assert.Empty(_world.Audited);
        Assert.Empty(_world.OwnerChanges);
    }

    [Fact]
    public async Task HandOver_FleetManagersSessionBackToTheOwner_ClearsTheOwnerAuditsAndTellsTheEvents()
    {
        var result = await HandAsync(Owned, "owner");

        Assert.Equal(200, result.Status);
        Assert.Equal(("dir-1", Owned, (string?)null), Assert.Single(_world.Sent));
        Assert.Equal("Session \"Already the Fleet Manager's\" is yours again. It asks you directly from now on.", result.Answer!.Sentence);
        Assert.Null(result.Answer.OwnerSessionId);
        Assert.Equal(Fm, result.Answer.PreviousOwnerSessionId);
        Assert.Equal($"handed back to the owner; owned before by the Fleet Manager {Fm}", Assert.Single(_world.Audited).Detail);
        Assert.Null(Assert.Single(_world.OwnerChanges).ControllerSessionId);
    }

    [Fact]
    public async Task HandOver_SessionWhoseOwnerHasEnded_IsHandedOverAndTheSentenceNamesTheOldOwner()
    {
        var result = await HandAsync(Orphan, "fleet-manager");

        Assert.Equal(200, result.Status);
        Assert.Equal(Gone, result.Answer!.PreviousOwnerSessionId);
        Assert.EndsWith($"It was owned by session {Gone}, which is no longer running.", result.Answer.Sentence);
        Assert.Equal($"handed to the Fleet Manager {Fm}; owned before by {Gone}", Assert.Single(_world.Audited).Detail);
    }

    [Theory]
    [InlineData("FLEET-MANAGER")]
    [InlineData(" fleet-manager ")]
    public async Task HandOver_DirectionInAnyCase_IsAccepted(string to)
    {
        var result = await HandAsync(Plain.ToUpperInvariant(), to);
        Assert.Equal(200, result.Status);
        Assert.Equal(Plain, result.Answer!.SessionId);
    }

    // ================================================================= every refusal

    [Fact]
    public async Task HandOver_AnotherAccountsSession_AnswersExactlyAsAnUnknownOne()
    {
        var foreign = await HandAsync(Foreign, "fleet-manager");
        var unknown = await HandAsync(Gone, "fleet-manager");

        AssertRefused(foreign, 404, "is running in this account");
        Assert.Equal(unknown.Error!.Replace(Gone, "X"), foreign.Error!.Replace(Foreign, "X"));
    }

    [Theory]
    [InlineData("fleet-manager")]
    [InlineData("owner")]
    public async Task HandOver_TheFleetManagerItself_IsRefused(string to)
        => AssertRefused(await HandAsync(Fm, to), 409, "is the Fleet Manager itself. It answers to you only");

    [Fact]
    public async Task HandOver_ToAFleetManager_WhenTheAccountHasNoneMarked_IsRefused()
    {
        _world.Mark = null;
        AssertRefused(await HandAsync(Plain, "fleet-manager"), 409,
            "This account has no Fleet Manager, so there is nothing to hand the session to.");
    }

    [Fact]
    public async Task HandOver_ToAFleetManager_WhenTheMarkedOneIsNotRunning_IsRefused()
    {
        _world.Rosters[Tenant].RemoveAll(r => r.Session.SessionId == Fm);
        AssertRefused(await HandAsync(Plain, "fleet-manager"), 409, $"The Fleet Manager (session {Fm}) is not running");
    }

    [Fact]
    public async Task HandOver_ToAFleetManager_WhenTheMarkedOneIsItselfOwned_IsRefused()
    {
        _world.Rosters[Tenant][0].Session.IsControlled = true;
        _world.Rosters[Tenant][0].Session.ControllerSessionId = Architect;
        AssertRefused(await HandAsync(Plain, "fleet-manager"), 409, "is not running");
    }

    [Fact]
    public async Task HandOver_SessionAnotherRunningSessionOwns_IsRefusedAndNotStolen()
        => AssertRefused(await HandAsync(ArchitectWorker, "fleet-manager"), 409,
            $"Session \"The Architect's Worker\" is owned by session \"An Architect\" ({Architect}), which is still running, so it was not handed over.");

    [Fact]
    public async Task HandOver_SessionAlreadyTheFleetManagers_IsRefused()
        => AssertRefused(await HandAsync(Owned, "fleet-manager"), 409, "is already the Fleet Manager's.");

    [Fact]
    public async Task HandBack_SessionTheOwnerAlreadyHas_IsRefused()
        => AssertRefused(await HandAsync(Plain, "owner"), 409, "is already yours: no session owns it.");

    /// <summary>
    /// THE OWNER TAKES BACK FROM ANY SESSION (issue #3096). This used to be refused - only the Fleet Manager's
    /// sessions could be handed back - and that refusal was the missing undo for a take: a session could be taken on
    /// his direction and he had no way to reverse it from his own screens.
    /// </summary>
    [Fact]
    public async Task HandBack_SessionAnotherSessionOwns_IsTheOwnersToTakeBack()
    {
        var result = await HandAsync(ArchitectWorker, "owner");

        Assert.Equal(200, result.Status);
        Assert.Equal(("dir-2", ArchitectWorker, (string?)null), Assert.Single(_world.Sent));
        Assert.Equal(Architect, Assert.Single(_world.Expected));
        Assert.Null(result.Answer!.OwnerSessionId);
        Assert.Equal(Architect, result.Answer.PreviousOwnerSessionId);
        Assert.Equal($"handed back to the owner; owned before by session {Architect}", Assert.Single(_world.Audited).Detail);
    }

    [Fact]
    public async Task HandBack_TheFleetManagersSession_StillNamesTheFleetManagerInTheRecord()
    {
        var result = await HandAsync(Owned, "owner");

        Assert.Equal(200, result.Status);
        Assert.Equal($"handed back to the owner; owned before by the Fleet Manager {Fm}", Assert.Single(_world.Audited).Detail);
    }

    [Fact]
    public async Task HandOver_SessionThatHasEnded_IsRefused()
        => AssertRefused(await HandAsync(Ended, "fleet-manager"), 409, "has ended, so it cannot be handed over.");

    [Theory]
    [InlineData("", "fleet-manager", 400, "\"session\" is required")]
    [InlineData("abc", "fleet-manager", 400, "\"abc\" is not a session id")]
    [InlineData(Plain, "", 400, "\"to\" must be \"fleet-manager\", \"owner\" or \"me\"")]
    [InlineData(Plain, "the-architect", 400, "not \"the-architect\"")]
    public async Task HandOver_MalformedRequest_IsRefusedWithASentence(string session, string to, int status, string part)
        => AssertRefused(await HandAsync(session, to), status, part);

    [Fact]
    public async Task HandOver_NoBody_IsRefused()
    {
        var result = await _service.HandOverAsync(Tenant, null, Actor, CancellationToken.None);
        AssertRefused(result, 400, "A body is required");
    }

    [Fact]
    public async Task HandOver_OnADirectorOlderThanTheVerb_IsRefusedBeforeAnythingIsSent()
    {
        _world.OldDirectors.Add("dir-1");
        AssertRefused(await HandAsync(Plain, "fleet-manager"), 409,
            "The Director running Session \"Plain work\" on WORKSTATION-A is too old to hand a session over safely: it cannot check that the session's owner is still the one checked here, " +
            "so it could overwrite an owner another session set meanwhile. " +
            "Update DevThrottle on that computer, then hand the session over again.");
    }

    [Fact]
    public async Task HandOver_DirectorDidNotMakeTheChange_Answers502AndRecordsNothing()
    {
        _world.DirectorError = "session not found";
        var result = await HandAsync(Plain, "fleet-manager");

        Assert.Equal(502, result.Status);
        Assert.Equal("Session \"Plain work\" was not handed over: its Director did not make the change (session not found).", result.Error);
        Assert.Empty(_world.Audited);
        Assert.Empty(_world.OwnerChanges);
    }

    [Fact]
    public async Task HandOver_DirectorAnsweredWithAnotherOwner_Answers502AndRecordsNothing()
    {
        _world.DirectorAnswersOwner = null;
        var result = await HandAsync(Plain, "fleet-manager");

        Assert.Equal(502, result.Status);
        Assert.Contains("the session's owner is nobody", result.Error);
        Assert.Empty(_world.Audited);
    }

    [Fact]
    public async Task HandOver_AuditTrailRefusesTheRow_TheChangeStandsAndTheAnswerSaysTheRecordIsMissing()
    {
        _world.AuditFails = true;
        var result = await HandAsync(Plain, "fleet-manager");

        Assert.Equal(200, result.Status);
        Assert.EndsWith("The change was made, but the audit trail could not record it.", result.Answer!.Sentence);
        Assert.Single(_world.OwnerChanges);
    }

    // ================================================================= the Fleet Manager's own key

    [Fact]
    public async Task FleetManagerKey_TakesASessionThatAnswersToTheOwner_ToItself()
    {
        var result = await HandAsSessionAsync(Fm, Plain, "fleet-manager");

        Assert.Equal(200, result.Status);
        Assert.Equal(Fm, result.Answer!.OwnerSessionId);
        Assert.Equal(("dir-1", Plain, (string?)Fm), Assert.Single(_world.Sent));
        Assert.Equal($"session {Fm}", Assert.Single(_world.Audited).Actor);
    }

    [Fact]
    public async Task FleetManagerKey_HandsASessionItOwnsBackToTheOwner()
    {
        var result = await HandAsSessionAsync(Fm, Owned, "owner");

        Assert.Equal(200, result.Status);
        Assert.Null(result.Answer!.OwnerSessionId);
        Assert.Equal(("dir-1", Owned, (string?)null), Assert.Single(_world.Sent));
    }

    [Fact]
    public async Task FleetManagerKey_SessionAnotherRunningSessionOwns_IsRefusedAndNotTaken()
        => AssertRefused(await HandAsSessionAsync(Fm, ArchitectWorker, "fleet-manager"), 409, "which is still running");

    [Fact]
    public async Task FleetManagerKey_HandBackOfASessionItDoesNotOwn_IsRefused()
        => AssertRefused(await HandAsSessionAsync(Fm, ArchitectWorker, "owner"), 409,
            "not by the session asking. A session hands back only a session it owns itself; the owner hands any " +
            "session back from the Cockpit or the phone.");

    [Fact]
    public async Task FleetManagerKey_AnotherAccountsSession_AnswersAsAnUnknownOne()
        => AssertRefused(await HandAsSessionAsync(Fm, Foreign, "fleet-manager"), 404, $"No session {Foreign} is running in this account");

    [Theory]
    [InlineData(Plain, "fleet-manager")]
    [InlineData(Architect, "fleet-manager")]
    [InlineData(Architect, "owner")]
    public async Task AnyOtherSessionKey_IsRefusedAsNotTheFleetManagerWithTheReason(string caller, string to)
    {
        var result = await HandAsSessionAsync(caller, Owned, to);

        AssertRefused(result, 403, $"Session {caller} may not hand session {Owned} over: it does not own that " +
                                   $"session, and it is not this account's Fleet Manager session ({Fm}).");
        Assert.Equal(FleetHandOverResult.NotFleetManager, result.Code);
    }

    // ============================================= a session releasing a session it owns (issue #3086)

    [Fact]
    public async Task OwningSessionKey_ReleasesTheSessionItOwnsToTheOwner_AndTheChangeIsRecorded()
    {
        var result = await HandAsSessionAsync(Architect, ArchitectWorker, "owner");

        Assert.Equal(200, result.Status);
        Assert.Equal(("dir-2", ArchitectWorker, (string?)null), Assert.Single(_world.Sent));
        Assert.Equal(Architect, Assert.Single(_world.Expected));
        Assert.Null(result.Answer!.OwnerSessionId);
        Assert.Equal(Architect, result.Answer.PreviousOwnerSessionId);
        Assert.Equal("Session \"The Architect's Worker\" is yours again. It asks you directly from now on.", result.Answer.Sentence);
        var audited = Assert.Single(_world.Audited);
        Assert.Equal((ArchitectWorker, $"session {Architect}"), (audited.SessionId, audited.Actor));
        Assert.Equal($"released to the owner by session {Architect}, which owned it", audited.Detail);
        Assert.Null(Assert.Single(_world.OwnerChanges).ControllerSessionId);
    }

    /// <summary>
    /// WHERE THE RED GOES is the whole point of the release (issue #3086). A session with a live owner is HELD - its
    /// turn end is that owner's to read - and the released session must stop being held, so the person sees it. Read
    /// through the real <see cref="TurnVerdictHeldCheck"/>, from the row the Director answered with, not from the
    /// request: the answer is what the roster carries afterwards.
    /// </summary>
    [Fact]
    public async Task OwningSessionKey_AfterTheRelease_TheSessionsTurnEndReachesTheUser()
    {
        var before = TurnVerdictHeldCheck.Resolve(_world.Roster(Tenant), ArchitectWorker, Fm);
        Assert.True(before.Held, "before the release the Architect holds it, so its turn end is the Architect's to read");

        var result = await HandAsSessionAsync(Architect, ArchitectWorker, "owner");

        Assert.Equal(200, result.Status);
        var row = _world.Rosters[Tenant].First(r => r.Session.SessionId == ArchitectWorker);
        row.Session.ControllerSessionId = result.Answer!.Session!.ControllerSessionId;
        row.Session.IsControlled = result.Answer.Session.IsControlled;

        var after = TurnVerdictHeldCheck.Resolve(_world.Roster(Tenant), ArchitectWorker, Fm);

        Assert.False(after.Held, "after the release nothing holds it, so its red goes to the user");
        Assert.True(FleetManagerSessions.AsksOwnerDirectly(
            _world.Roster(Tenant).First(r => r.Session.SessionId == ArchitectWorker).Session, Fm));
    }

    [Fact]
    public async Task NonOwningSessionKey_MayNotReleaseASessionAnotherSessionOwns()
    {
        var result = await HandAsSessionAsync(Plain, ArchitectWorker, "owner");

        AssertRefused(result, 403, $"Session {Plain} may not hand session {ArchitectWorker} over: it does not own " +
                                   $"that session, and it is not this account's Fleet Manager session ({Fm}).");
        Assert.Equal(FleetHandOverResult.NotFleetManager, result.Code);
    }

    /// <summary>
    /// A SESSION THAT OWNS THE SESSION IS NOT TOLD IT OWNS NOTHING. It asked for the wrong direction, and the sentence
    /// that sends it after the wrong fix - "it does not own that session" - is worse than no sentence at all, because
    /// it is false about the one fact the reader would act on. Raised in review.
    /// </summary>
    [Fact]
    public async Task OwningSessionKey_MayNotTakeTheSessionItOwnsToTheFleetManager()
    {
        var result = await HandAsSessionAsync(Architect, ArchitectWorker, "fleet-manager");

        AssertRefused(result, 403, $"Session {Architect} already owns session {ArchitectWorker}. It may release it: " +
                                   $"cc-devthrottle session hand-over {ArchitectWorker} --to owner.");
        Assert.DoesNotContain("does not own", result.Error);
        Assert.Equal(FleetHandOverResult.NotFleetManager, result.Code);
    }

    /// <summary>The Fleet Manager's own key is unchanged by the owning-caller sentence: a session it already owns is
    /// still answered by the session rules, not by the caller check.</summary>
    [Fact]
    public async Task FleetManagerKey_TakingASessionItAlreadyOwns_IsStillTheOrdinaryRefusal()
        => AssertRefused(await HandAsSessionAsync(Fm, Owned, "fleet-manager"), 409, "is already the Fleet Manager's.");

    [Fact]
    public async Task AnySessionKey_MayNotAcquireASessionThatAnswersToTheOwner()
    {
        var result = await HandAsSessionAsync(Architect, Plain, "fleet-manager");

        AssertRefused(result, 403, $"Session {Architect} may not hand session {Plain} over: it does not own that session");
    }

    [Fact]
    public async Task ARefusedSessionKey_IsToldWhichDirectionItMayHandOver()
    {
        var result = await HandAsSessionAsync(Plain, ArchitectWorker, "owner");

        Assert.Contains("A session may release a session it OWNS to the owner (--to owner)", result.Error);
        Assert.Contains("is the owner's to direct", result.Error);
    }

    [Fact]
    public async Task OwningSessionKey_WhenTheAccountHasNoFleetManagerAtAll_StillReleasesWhatItOwns()
    {
        _world.Mark = null;

        var result = await HandAsSessionAsync(Architect, ArchitectWorker, "owner");

        Assert.Equal(200, result.Status);
        Assert.Equal(("dir-2", ArchitectWorker, (string?)null), Assert.Single(_world.Sent));
    }

    [Fact]
    public async Task OwningSessionKey_ReleasingASessionThatHasEnded_IsRefusedAsBefore()
    {
        _world.Rosters[Tenant].Add(("dir-1", Row(Gone, "Its worker, finished", controller: Architect, state: "Exited")));

        AssertRefused(await HandAsSessionAsync(Architect, Gone, "owner"), 409, "has ended, so it cannot be handed over.");
    }

    [Fact]
    public async Task SessionKey_WhenTheAccountHasNoFleetManager_IsRefusedWithTheReason()
    {
        _world.Mark = null;

        var result = await HandAsSessionAsync(Fm, Plain, "fleet-manager");

        AssertRefused(result, 403, "this account has no Fleet Manager marked");
        Assert.Equal(FleetHandOverResult.NotFleetManager, result.Code);
    }

    // ================================= a session TAKING a session on the owner's direction (issue #3096)

    [Fact]
    public async Task SessionKey_TakesASessionThatAnswersToTheOwner_ToItselfAndTheRecordSaysSo()
    {
        var result = await HandAsSessionAsync(Architect, Plain, "me");

        Assert.Equal(200, result.Status);
        Assert.Equal(("dir-1", Plain, (string?)Architect), Assert.Single(_world.Sent));
        Assert.Null(Assert.Single(_world.Expected));
        var answer = result.Answer!;
        Assert.Equal((Plain, "me", (string?)Architect, (string?)null),
            (answer.SessionId, answer.To, answer.OwnerSessionId, answer.PreviousOwnerSessionId));
        Assert.Equal("Session \"Plain work\" is yours now. When it stops, you are told instead of the owner, and you answer for it.",
            answer.Sentence);
        var audited = Assert.Single(_world.Audited);
        Assert.Equal((Plain, $"session {Architect}"), (audited.SessionId, audited.Actor));
        Assert.Equal($"taken by session {Architect} on the owner's direction; owned before by the owner", audited.Detail);
        Assert.Equal(Architect, Assert.Single(_world.OwnerChanges).ControllerSessionId);
    }

    /// <summary>A take moves work AWAY from the person: the session it reaches stops going red for him and answers to
    /// the session that took it. Read through the real held check, from the row the Director answered with.</summary>
    [Fact]
    public async Task SessionKey_AfterTakingASession_ThatSessionStopsReachingTheUser()
    {
        Assert.False(TurnVerdictHeldCheck.Resolve(_world.Roster(Tenant), Plain, Fm).Held);

        var result = await HandAsSessionAsync(Architect, Plain, "me");

        Assert.Equal(200, result.Status);
        var row = _world.Rosters[Tenant].First(r => r.Session.SessionId == Plain);
        row.Session.ControllerSessionId = result.Answer!.Session!.ControllerSessionId;
        row.Session.IsControlled = result.Answer.Session.IsControlled;

        Assert.True(TurnVerdictHeldCheck.Resolve(_world.Roster(Tenant), Plain, Fm).Held,
            "the session that took it holds it now, so its turn end is that session's to read");
        Assert.False(FleetManagerSessions.AsksOwnerDirectly(
            _world.Roster(Tenant).First(r => r.Session.SessionId == Plain).Session, Fm));
    }

    [Fact]
    public async Task SessionKey_MayNotTakeASessionAnotherRunningSessionOwns()
    {
        AssertRefused(await HandAsSessionAsync(Plain, ArchitectWorker, "me"), 409,
            $"Session \"The Architect's Worker\" is owned by session \"An Architect\" ({Architect}), which is still running, so it was not taken.");
    }

    [Fact]
    public async Task SessionKey_MayTakeASessionWhoseOwnerHasEnded()
    {
        var result = await HandAsSessionAsync(Architect, Orphan, "me");

        Assert.Equal(200, result.Status);
        Assert.Equal(Architect, result.Answer!.OwnerSessionId);
        Assert.Equal(Gone, result.Answer.PreviousOwnerSessionId);
        Assert.Equal(Gone, Assert.Single(_world.Expected));
        Assert.EndsWith($"It was owned by session {Gone}, which is no longer running.", result.Answer.Sentence);
    }

    [Fact]
    public async Task SessionKey_MayNotTakeItself()
        => AssertRefused(await HandAsSessionAsync(Architect, Architect, "me"), 409,
            "is the session asking. A session cannot take itself");

    [Fact]
    public async Task SessionKey_MayNotTakeASessionItAlreadyOwns()
        => AssertRefused(await HandAsSessionAsync(Architect, ArchitectWorker, "me"), 409,
            "Session \"The Architect's Worker\" is already yours.");

    [Fact]
    public async Task SessionKey_MayNotTakeTheFleetManager()
        => AssertRefused(await HandAsSessionAsync(Architect, Fm, "me"), 409, "is the Fleet Manager itself");

    [Fact]
    public async Task SessionKey_MayNotTakeASessionThatHasEnded()
        => AssertRefused(await HandAsSessionAsync(Architect, Ended, "me"), 409, "has ended, so it cannot be handed over.");

    [Fact]
    public async Task SessionKey_MayNotTakeAnotherAccountsSession()
        => AssertRefused(await HandAsSessionAsync(Architect, Foreign, "me"), 404, "is running in this account");

    /// <summary>
    /// THE FORBIDDEN MOVE IS UNSAYABLE, not merely refused. "Put this session under that session" has no spelling:
    /// a session id in <c>to</c> is an unknown direction like any other word, and the sentence says the rule.
    /// </summary>
    [Fact]
    public async Task ASessionIdAsTheDirection_IsRefusedLikeAnyOtherUnknownWord()
    {
        var result = await HandAsSessionAsync(Architect, Plain, ArchitectWorker);

        AssertRefused(result, 400, $"not \"{ArchitectWorker}\".");
        Assert.Contains("may never put one under a third session", result.Error);
    }

    [Fact]
    public async Task TheOwnersOwnDevice_AskingToTakeToMe_IsRefusedAndPointedAtOwner()
        => AssertRefused(await HandAsync(Plain, "me"), 400,
            "is the SESSION making the request, so it needs a session's own key. " +
            "From the Cockpit or the phone you are the owner: use \"owner\".");

    [Fact]
    public async Task ARefusedSessionKey_IsToldItMayTakeAsWellAsRelease()
    {
        var result = await HandAsSessionAsync(Plain, ArchitectWorker, "owner");

        Assert.Contains("may take a session that answers to the owner TO ITSELF (--to me)", result.Error);
        Assert.Contains("No session is ever put under a third session.", result.Error);
    }

    /// <summary>
    /// THE HARM THE REFUSAL BELOW PREVENTS, measured rather than asserted. A session is quietened only because
    /// something alive is holding it, so a RING of live sessions holding each other holds every one of its members and
    /// nothing in it ever reaches the person again. This builds the two-session ring in the roster by hand - no hand
    /// over involved - and reads both through the real <see cref="TurnVerdictHeldCheck"/>. If this ever stops being
    /// true, the refusal is guarding nothing and should be reconsidered rather than kept out of habit.
    /// </summary>
    [Fact]
    public void ARingOfTwoLiveSessionsHoldingEachOther_SilencesBothForTheOwner()
    {
        Assert.False(TurnVerdictHeldCheck.Resolve(_world.Roster(Tenant), Architect, Fm).Held,
            "the Architect answers to the owner before the ring");

        var architect = _world.Rosters[Tenant].First(r => r.Session.SessionId == Architect).Session;
        architect.ControllerSessionId = ArchitectWorker;
        architect.IsControlled = true;

        Assert.True(TurnVerdictHeldCheck.Resolve(_world.Roster(Tenant), Architect, Fm).Held);
        Assert.True(TurnVerdictHeldCheck.Resolve(_world.Roster(Tenant), ArchitectWorker, Fm).Held);
    }

    [Fact]
    public async Task SessionKey_MayNotTakeTheSessionThatOwnsIt()
    {
        var result = await HandAsSessionAsync(ArchitectWorker, Architect, "me");

        AssertRefused(result, 409, "already owns the session it would be handed to, directly or further up that " +
                                   "session's chain, so it was not handed over: the two would answer to each other " +
                                   "and neither would reach the owner again.");
        Assert.Empty(_world.Sent);
        Assert.Empty(_world.Audited);
    }

    /// <summary>Not just the session directly above: anywhere up the chain closes the same ring.</summary>
    [Fact]
    public async Task SessionKey_MayNotTakeASessionFurtherUpItsOwnChain()
    {
        var architect = _world.Rosters[Tenant].First(r => r.Session.SessionId == Architect).Session;
        architect.ControllerSessionId = Plain;
        architect.IsControlled = true;

        var result = await HandAsSessionAsync(ArchitectWorker, Plain, "me");

        AssertRefused(result, 409, "already owns the session it would be handed to");
        Assert.Empty(_world.Sent);
    }

    /// <summary>
    /// A CHAIN THROUGH AN ENDED SESSION IS ALREADY BROKEN, so it cannot silence anybody and must not be refused. The
    /// session asking has an ended owner, so it asks the owner directly; what sits above that ended session is out of
    /// reach of any ring, and taking it is allowed.
    /// </summary>
    [Fact]
    public async Task SessionKey_MayTakeASessionAboveAnEndedLinkInItsOwnChain()
    {
        _world.Rosters[Tenant].Add(("dir-2", Row(Gone, "Its owner, finished", controller: Plain, state: "Exited")));
        var worker = _world.Rosters[Tenant].First(r => r.Session.SessionId == ArchitectWorker).Session;
        worker.ControllerSessionId = Gone;

        var result = await HandAsSessionAsync(ArchitectWorker, Plain, "me");

        Assert.Equal(200, result.Status);
        Assert.Equal(ArchitectWorker, Assert.Single(_world.Sent).Controller);
    }

    [Fact]
    public async Task MarkedSessionKey_ThatIsItselfOwned_IsRefusedWithTheReason()
    {
        _world.Rosters[Tenant][0] = ("dir-1", Row(Fm, "The Fleet Manager", controller: Architect));

        var result = await HandAsSessionAsync(Fm, Plain, "fleet-manager");

        AssertRefused(result, 403, "is marked as the Fleet Manager but is not running as the Fleet Manager");
    }
}
