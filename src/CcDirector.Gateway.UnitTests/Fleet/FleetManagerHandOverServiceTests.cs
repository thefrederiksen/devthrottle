using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
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

        public bool ChangesOwner(TenantId tenant, string directorId) => !OldDirectors.Contains(directorId);

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

    [Fact]
    public async Task HandBack_SessionAnotherSessionOwns_IsRefused()
        => AssertRefused(await HandAsync(ArchitectWorker, "owner"), 409,
            "not by the Fleet Manager. Only the Fleet Manager's sessions are handed back here.");

    [Fact]
    public async Task HandOver_SessionThatHasEnded_IsRefused()
        => AssertRefused(await HandAsync(Ended, "fleet-manager"), 409, "has ended, so it cannot be handed over.");

    [Theory]
    [InlineData("", "fleet-manager", 400, "\"session\" is required")]
    [InlineData("abc", "fleet-manager", 400, "\"abc\" is not a session id")]
    [InlineData(Plain, "", 400, "\"to\" must be \"fleet-manager\" or \"owner\"")]
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
            "The Director running Session \"Plain work\" on WORKSTATION-A is older than hand over and cannot change a session's owner. " +
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
        => AssertRefused(await HandAsSessionAsync(Fm, ArchitectWorker, "owner"), 409, "not by the Fleet Manager");

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

        AssertRefused(result, 403, $"Only this account's Fleet Manager session ({Fm}) may hand a session over; session {caller} is not it.");
        Assert.Equal(FleetHandOverResult.NotFleetManager, result.Code);
    }

    [Fact]
    public async Task SessionKey_WhenTheAccountHasNoFleetManager_IsRefusedWithTheReason()
    {
        _world.Mark = null;

        var result = await HandAsSessionAsync(Fm, Plain, "fleet-manager");

        AssertRefused(result, 403, "this account has no Fleet Manager marked");
        Assert.Equal(FleetHandOverResult.NotFleetManager, result.Code);
    }

    [Fact]
    public async Task MarkedSessionKey_ThatIsItselfOwned_IsRefusedWithTheReason()
    {
        _world.Rosters[Tenant][0] = ("dir-1", Row(Fm, "The Fleet Manager", controller: Architect));

        var result = await HandAsSessionAsync(Fm, Plain, "fleet-manager");

        AssertRefused(result, 403, "is marked as the Fleet Manager but is not running as the Fleet Manager");
    }
}
