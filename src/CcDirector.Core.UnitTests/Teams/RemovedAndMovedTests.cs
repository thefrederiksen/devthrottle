using CcDirector.Core.GatewayConnection;
using CcDirector.Core.Sessions;
using CcDirector.Core.Teams;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Teams;

/// <summary>
/// Live proof F3 (devthrottle_internal#2311): what the Director reads out of the Gateway's 401 for its key. "Removed
/// from the team" is said only when the Gateway's answer carries that reason; any other revoke, and a Gateway too old
/// to send a reason, read as a plain revoke.
/// </summary>
public sealed class GatewayKeyRefusalTests
{
    private const string RemovedBody =
        "{\"error\":\"device credential revoked\",\"code\":\"device_credential_revoked\",\"reason\":\"team_member_removed\"}";
    private const string PlainRevokedBody = "{\"error\":\"device credential revoked\",\"code\":\"device_credential_revoked\"}";

    [Fact]
    public void FromUnauthorizedBody_TeamRemovalReason_IsRemovedFromThatTeam()
    {
        var refusal = GatewayKeyRefusal.FromUnauthorizedBody(RemovedBody, "Team B")!;

        Assert.Equal(GatewayKeyRefusalKind.RemovedFromTeam, refusal.Kind);
        Assert.Equal("Removed from Team B", refusal.ChipText);
        Assert.StartsWith("This Director was removed from Team B", refusal.Summary);
        Assert.Contains("stopped trying to connect", refusal.Summary);
        Assert.Contains("set it up again", refusal.Summary);
        Assert.Contains("choose another team or set it up for yourself", refusal.Summary);
    }

    [Fact]
    public void FromUnauthorizedBody_TeamRemovalWithNoTeamRecorded_SaysItsTeam()
    {
        var refusal = GatewayKeyRefusal.FromUnauthorizedBody(RemovedBody, null)!;

        Assert.Equal("Removed from the team", refusal.ChipText);
        Assert.StartsWith("This Director was removed from its team", refusal.Summary);
    }

    // An older Gateway sends the revoked code with no reason: no deploy order is needed, it reads as a plain revoke.
    [Theory]
    [InlineData(PlainRevokedBody)]
    [InlineData("{\"error\":\"device credential revoked\",\"code\":\"device_credential_revoked\",\"reason\":\"director_moved_to_another_team\"}")]
    [InlineData("{\"error\":\"device credential revoked\",\"code\":\"device_credential_revoked\",\"reason\":\"TEAM_MEMBER_REMOVED\"}")]
    [InlineData("{\"error\":\"device credential revoked\",\"code\":\"device_credential_revoked\",\"reason\":7}")]
    public void FromUnauthorizedBody_AnyOtherRevoke_IsAPlainRevoke_NeverARemoval(string body)
    {
        var refusal = GatewayKeyRefusal.FromUnauthorizedBody(body, "Team B")!;

        Assert.Equal(GatewayKeyRefusalKind.KeyRevoked, refusal.Kind);
        Assert.Equal("Key revoked", refusal.ChipText);
        Assert.StartsWith("The Gateway revoked this Director's key", refusal.Summary);
        Assert.DoesNotContain("removed", refusal.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // Review RM-F1 and RM-F4: only the Gateway's revoke answer IN FULL - error and code together - is a refusal. Anything
    // a proxy, a load balancer or another service could send is no evidence, so there is no refusal and the tunnel keeps
    // retrying: a code with no error, the generic "missing or invalid token", an empty or HTML body.
    [Theory]
    [InlineData("{\"code\":\"device_credential_revoked\"}")]
    [InlineData("{\"code\":\"device_credential_revoked\",\"reason\":\"team_member_removed\"}")]
    [InlineData("{\"error\":\"device credential revoked\"}")]
    [InlineData("{\"error\":\"device credential revoked\",\"code\":\"something_else\"}")]
    [InlineData("{\"error\":\"missing or invalid token\"}")]
    [InlineData("{\"reason\":\"team_member_removed\"}")]
    [InlineData("{\"error\":\"unauthorized\"}")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("<html>401 Unauthorized</html>")]
    [InlineData("[1,2]")]
    public void FromUnauthorizedBody_NotTheGatewaysRevokeAnswerInFull_IsNoRefusal(string? body)
    {
        Assert.Null(GatewayKeyRefusal.FromUnauthorizedBody(body, "Team B"));
    }
}

/// <summary>
/// Live proof F3: the status box, where the removed Director said "Connecting..." for good. A refused key resolves to
/// its own red state worded by the refusal; a network failure still reads "Connecting...".
/// </summary>
public sealed class KeyRefusedStatusBoxTests
{
    private static GatewayConnectionInputs Inputs(GatewayConnectionVerification connection, GatewayKeyRefusal? refusal = null) => new(
        GatewayConfigured: true,
        Connection: connection,
        FailedLeg: GatewayConnectionFailedLeg.None,
        WasEverConnected: true,
        DeviceKeyPresent: true,
        Account: GatewayAccountSignInState.Unavailable,
        Refusal: refusal);

    [Fact]
    public void Describe_KeyRefusedRemovedFromTeam_IsRedAndSaysRemovedWhereItSaidConnecting()
    {
        var refusal = GatewayKeyRefusal.FromUnauthorizedBody(
            "{\"error\":\"device credential revoked\",\"code\":\"device_credential_revoked\",\"reason\":\"team_member_removed\"}", "Team B")!;

        var content = GatewayStatusBoxPresenter.Describe(Inputs(GatewayConnectionVerification.KeyRefused, refusal), "gw.example", null);

        Assert.Equal(GatewayStatusBoxVisual.Red, content.Visual);
        Assert.Equal("Removed from Team B", content.ChipText);
        Assert.Equal(refusal.Summary, content.Tooltip);
        Assert.Equal("gw.example (Removed from Team B)", content.Connected.Text);
        Assert.Equal(GatewayCheckState.Failed, content.Connected.Marker);
        Assert.Equal(GatewayPanelStep.Connect, GatewayConnectionStateResolver.Resolve(Inputs(GatewayConnectionVerification.KeyRefused, refusal)).TargetStep);
    }

    [Fact]
    public void Describe_KeyRevokedForAnotherReason_SaysKeyRevoked()
    {
        var refusal = GatewayKeyRefusal.FromUnauthorizedBody(
            "{\"error\":\"device credential revoked\",\"code\":\"device_credential_revoked\"}", "Team B")!;

        var content = GatewayStatusBoxPresenter.Describe(Inputs(GatewayConnectionVerification.KeyRefused, refusal), "gw.example", null);

        Assert.Equal(GatewayStatusBoxVisual.Red, content.Visual);
        Assert.Equal("Key revoked", content.ChipText);
    }

    // A network failure keeps today's words: the Director is still dialing.
    [Fact]
    public void Describe_StillDialing_StillSaysConnecting()
    {
        var content = GatewayStatusBoxPresenter.Describe(Inputs(GatewayConnectionVerification.Verifying), "gw.example", null);

        Assert.Equal(GatewayStatusBoxVisual.Yellow, content.Visual);
        Assert.Equal("Connecting...", content.ChipText);
    }

    [Fact]
    public void ResolveState_KeyRefusedWithNoRefusal_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            GatewayConnectionStateResolver.ResolveState(Inputs(GatewayConnectionVerification.KeyRefused)));
    }
}

/// <summary>
/// Live proof F4 (devthrottle_internal#2311): a Director's name that is still the one the team question suggested,
/// "&lt;computer&gt; - &lt;team&gt;", follows a move out of THAT team; a name the person typed never changes; a Director
/// with no record keeps its name; a personal Director is never renamed.
/// </summary>
public sealed class DirectorNameFollowsMoveTests : IDisposable
{
    private static readonly DirectorTeam TeamA = new("t-a", "Team A");
    private static readonly DirectorTeam TeamB = new("t-b", "Team B");
    private static readonly DirectorTeam TeamC = new("t-c", "Team C");
    private static readonly TeamChoice TeamBChoice = new("t-b", "Team B", "You are a Developer, 3 people");

    private readonly string _home = Path.Combine(Path.GetTempPath(), "dt-name-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }

    private static DirectorNameSuggestion Suggested(DirectorTeam team) => DirectorNameSuggestion.For("SOREN_NORTH", team);

    // ---- the rule --------------------------------------------------------------------------------------------------

    [Fact]
    public void Decide_NameIsTheRecordedSuggestion_LeavingThatTeam_FollowsToTheNewTeam()
    {
        var decision = DirectorNameAfterMove.Decide("SOREN_NORTH - Team A", Suggested(TeamA), "t-a", TeamB);

        Assert.Equal("SOREN_NORTH - Team B", decision.NewName);
        Assert.Equal(new DirectorNameSuggestion("t-b", "SOREN_NORTH", "SOREN_NORTH - Team B"), decision.Record);
    }

    // No record: a Director set up before this was recorded keeps its name even when it looks like a suggestion.
    [Fact]
    public void Decide_NoRecord_KeepsTheName()
    {
        var decision = DirectorNameAfterMove.Decide("SOREN_NORTH - Team A", null, "t-a", TeamB);

        Assert.Null(decision.NewName);
        Assert.Null(decision.Record);
    }

    [Fact]
    public void Decide_RenamedSinceSetup_KeepsTheNameAndDropsTheRecord()
    {
        var decision = DirectorNameAfterMove.Decide("Build box", Suggested(TeamA), "t-a", TeamB);

        Assert.Null(decision.NewName);
        Assert.Null(decision.Record);
    }

    // Review RM-F5: a record applies only to a move out of the team it was suggested for.
    [Theory]
    [InlineData(null)]
    [InlineData("t-c")]
    public void Decide_RecordForAnotherTeamThanTheOneBeingLeft_KeepsTheName(string? leavingTeamId)
    {
        var decision = DirectorNameAfterMove.Decide("SOREN_NORTH - Team A", Suggested(TeamA), leavingTeamId, TeamB);

        Assert.Null(decision.NewName);
        Assert.Null(decision.Record);
    }

    // Review RM-F2: the personal account is never recorded, even with its suggested name.
    [Fact]
    public void IfSuggested_PersonalWithItsSuggestedName_IsNull()
    {
        Assert.Null(DirectorNameSuggestion.IfSuggested("SOREN_NORTH", DirectorTeam.Personal, "SOREN_NORTH - Personal"));
    }

    [Fact]
    public void Decide_MoveToPersonal_KeepsTheNameAndDropsTheRecord()
    {
        var decision = DirectorNameAfterMove.Decide("SOREN_NORTH - Team A", Suggested(TeamA), "t-a", DirectorTeam.Personal);

        Assert.Null(decision.NewName);
        Assert.Null(decision.Record);
    }

    [Fact]
    public void IfSuggested_TypedName_IsNull_SuggestedName_IsTheSuggestionForThatTeam()
    {
        Assert.Null(DirectorNameSuggestion.IfSuggested("SOREN_NORTH", TeamA, "Build box"));
        Assert.Null(DirectorNameSuggestion.IfSuggested("SOREN_NORTH", TeamA, "SOREN_NORTH - Team B"));
        Assert.Equal(new DirectorNameSuggestion("t-a", "SOREN_NORTH", "SOREN_NORTH - Team A"),
            DirectorNameSuggestion.IfSuggested("SOREN_NORTH", TeamA, " SOREN_NORTH - Team A "));
    }

    // The question's suggestion and the move's follow are built by one builder, so they cannot drift.
    [Fact]
    public void SuggestDirectorName_IsTheRecordedSuggestionsName()
    {
        Assert.Equal(Suggested(TeamB).Name, TeamChoices.SuggestDirectorName("SOREN_NORTH", TeamBChoice));
    }

    // ---- the follower, over the real store -------------------------------------------------------------------------

    private sealed class Director
    {
        public string Name { get; set; } = "";
        public string? TeamId { get; set; }
    }

    // A follower over the real store, for a Director whose name and current team the test controls. A move is FollowMove
    // and then the new team recorded, as the mover does it.
    private DirectorNameFollower Follower(Director director, Action<DirectorNameSuggestion?>? save = null) => new(
        () => director.Name,
        () => director.TeamId,
        n => director.Name = n,
        () => DirectorNameSuggestionStore.LoadAt(_home),
        save ?? (s => DirectorNameSuggestionStore.SaveAt(_home, s)));

    private static string? Move(DirectorNameFollower follower, Director director, DirectorTeam to)
    {
        var renamed = follower.FollowMove(to);
        director.TeamId = to.TeamId;
        return renamed;
    }

    [Fact]
    public void FollowMove_SuggestedName_RenamesAndRecordsTheNewSuggestion()
    {
        DirectorNameSuggestionStore.SaveAt(_home, Suggested(TeamA));
        var director = new Director { Name = "SOREN_NORTH - Team A", TeamId = "t-a" };

        var renamed = Move(Follower(director), director, TeamB);

        Assert.Equal("SOREN_NORTH - Team B", renamed);
        Assert.Equal("SOREN_NORTH - Team B", director.Name);
        Assert.Equal(Suggested(TeamB), DirectorNameSuggestionStore.LoadAt(_home));
    }

    [Fact]
    public void FollowMove_TypedName_NeverChanges()
    {
        // Setup recorded nothing, because the person typed the name.
        var director = new Director { Name = "Build box", TeamId = "t-a" };

        Assert.Null(Move(Follower(director), director, TeamB));
        Assert.Equal("Build box", director.Name);
        Assert.Null(DirectorNameSuggestionStore.LoadAt(_home));
    }

    [Fact]
    public void FollowMove_NoRecord_KeepsANameThatLooksLikeASuggestion()
    {
        var director = new Director { Name = "SOREN_NORTH - Team A", TeamId = "t-a" };

        Assert.Null(Move(Follower(director), director, TeamB));
        Assert.Equal("SOREN_NORTH - Team A", director.Name);
    }

    [Fact]
    public void FollowMove_RenamedSinceSetup_KeepsTheNameAndRemovesTheRecord()
    {
        DirectorNameSuggestionStore.SaveAt(_home, Suggested(TeamA));
        var director = new Director { Name = "Build box", TeamId = "t-a" };

        Assert.Null(Move(Follower(director), director, TeamB));
        Assert.Equal("Build box", director.Name);
        Assert.Null(DirectorNameSuggestionStore.LoadAt(_home));
    }

    // Review RM-F2, setup to move over the real store: Personal chosen with its suggested name records nothing, so a
    // later move to a team leaves the name alone.
    [Fact]
    public void PersonalSetUpWithItsSuggestedName_ThenMovedToATeam_NameUnchanged()
    {
        DirectorNameSuggestionStore.SaveAt(_home,
            DirectorNameSuggestion.IfSuggested("SOREN_NORTH", DirectorTeam.Personal, "SOREN_NORTH - Personal"));
        var director = new Director { Name = "SOREN_NORTH - Personal", TeamId = null };

        Assert.Null(Move(Follower(director), director, TeamB));
        Assert.Equal("SOREN_NORTH - Personal", director.Name);
    }

    // Review RM-F5: Team A record, a move to Personal whose removal of the record FAILS, then Personal to Team B. The
    // record survives but belongs to Team A, which this move is not leaving - so the name is unchanged.
    [Fact]
    public void TeamARecord_MovedToPersonalWithTheRemovalFailing_ThenToTeamB_NameUnchanged()
    {
        DirectorNameSuggestionStore.SaveAt(_home, Suggested(TeamA));
        var director = new Director { Name = "SOREN_NORTH - Team A", TeamId = "t-a" };
        var follower = Follower(director, s =>
        {
            if (s is null) throw new IOException("suggestion file locked");
            DirectorNameSuggestionStore.SaveAt(_home, s);
        });

        Assert.Null(Move(follower, director, DirectorTeam.Personal));
        Assert.Equal(Suggested(TeamA), DirectorNameSuggestionStore.LoadAt(_home));

        Assert.Null(Move(follower, director, TeamB));
        Assert.Equal("SOREN_NORTH - Team A", director.Name);
    }

    // Review RM-F5: a record written before records named their team is treated as no record.
    [Fact]
    public void SuggestionStore_RecordWithNoTeam_IsNoRecord()
    {
        var path = DirectorNameSuggestionStore.SuggestionFileAt(_home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"machineName\":\"SOREN_NORTH\",\"name\":\"SOREN_NORTH - Team A\"}");

        Assert.Null(DirectorNameSuggestionStore.LoadAt(_home));
    }

    // Review RM-F3: the rename happened, then the new record could not be saved. The old record is removed (never left
    // beside the new name), the follower says the name changed but will not follow, and the next move keeps the name.
    [Fact]
    public void FollowMove_RecordCannotBeSavedAfterTheRename_RemovesTheOldRecordAndTheNextMoveKeepsTheName()
    {
        DirectorNameSuggestionStore.SaveAt(_home, Suggested(TeamA));
        var director = new Director { Name = "SOREN_NORTH - Team A", TeamId = "t-a" };
        var follower = Follower(director, s =>
        {
            if (s is not null) throw new IOException("suggestion file locked");
            DirectorNameSuggestionStore.SaveAt(_home, null);
        });

        var thrown = Assert.Throws<NameChangedButNotRecordedException>(() => follower.FollowMove(TeamB));
        director.TeamId = "t-b";

        Assert.Equal("SOREN_NORTH - Team B", thrown.NewName);
        Assert.Equal("SOREN_NORTH - Team B", director.Name);
        Assert.Null(DirectorNameSuggestionStore.LoadAt(_home));

        Assert.Null(Move(follower, director, TeamC));
        Assert.Equal("SOREN_NORTH - Team B", director.Name);
    }

    // If even the removal fails, the follower still reports the change; the stale record belongs to Team A, which the
    // next move does not leave, so that move keeps the name.
    [Fact]
    public void FollowMove_RecordCannotBeSavedOrRemoved_StillSaysTheNameChanged_AndTheNextMoveKeepsTheName()
    {
        DirectorNameSuggestionStore.SaveAt(_home, Suggested(TeamA));
        var director = new Director { Name = "SOREN_NORTH - Team A", TeamId = "t-a" };
        var writable = false;
        var follower = Follower(director, s =>
        {
            if (!writable) throw new IOException("disk gone");
            DirectorNameSuggestionStore.SaveAt(_home, s);
        });

        Assert.Throws<NameChangedButNotRecordedException>(() => follower.FollowMove(TeamB));
        director.TeamId = "t-b";
        Assert.Equal("SOREN_NORTH - Team B", director.Name);

        writable = true;
        Assert.Null(Move(follower, director, TeamC));
        Assert.Equal("SOREN_NORTH - Team B", director.Name);
    }

    [Fact]
    public void SuggestionStore_UnreadableFile_Throws()
    {
        var path = DirectorNameSuggestionStore.SuggestionFileAt(_home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"teamId\":\"t-a\",\"machineName\":\"\",\"name\":\"x\"}");

        Assert.Throws<InvalidDataException>(() => DirectorNameSuggestionStore.LoadAt(_home));
    }

    // ---- the move ---------------------------------------------------------------------------------------------------

    private sealed class MovingService : IDirectorTeamService
    {
        public Task<OperationResult<HostedTeamsAnswer>> ListTeamsAsync(CancellationToken ct) =>
            Task.FromResult(OperationResult<HostedTeamsAnswer>.Ok(new HostedTeamsAnswer(true, Array.Empty<HostedTeam>())));

        public Task<OperationResult<string>> MoveAsync(string directorId, string? teamId, CancellationToken ct) =>
            Task.FromResult(OperationResult<string>.Ok("new-key"));
    }

    // The name follows before the team is recorded (which redraws the title bar with name and chip together) and before
    // the connection is re-applied (whose Hello carries the name to the Fleet Map). The team is recorded with the new key.
    [Fact]
    public async Task MoveAsync_SuggestedName_RenamesBeforeTheTeamIsRecordedAndBeforeTheReconnect()
    {
        var log = new List<string>();
        var mover = new DirectorTeamMover(new MovingService(), r => new SessionCreationHold(0, () => { }),
            (t, key) => log.Add("team:" + t.Name + " for " + key), k => log.Add("key"),
            () => { log.Add("reapply"); return Task.CompletedTask; },
            t => { log.Add("name:" + t.Name); return "SOREN_NORTH - " + t.Name; });

        var result = await mover.MoveAsync("dir-1", TeamBChoice, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "name:Team B", "team:Team B for new-key", "key", "reapply" }, log);
    }

    // Review RM-F3, at the move: the name changed but its record did not save - the move finishes and says exactly that.
    [Fact]
    public async Task MoveAsync_NameChangedButItsRecordNotSaved_TheMoveFinishesAndSaysTheNameWillNotFollow()
    {
        var log = new List<string>();
        var mover = new DirectorTeamMover(new MovingService(), r => new SessionCreationHold(0, () => { }),
            (t, _) => log.Add("team"), k => log.Add("key"),
            () => { log.Add("reapply"); return Task.CompletedTask; },
            _ => throw new NameChangedButNotRecordedException("SOREN_NORTH - Team B", new IOException("suggestion file locked")));

        var result = await mover.MoveAsync("dir-1", TeamBChoice, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(DirectorTeamMover.MovedRenamedButWillNotFollow("Team B", "SOREN_NORTH - Team B", "suggestion file locked"),
            result.ErrorMessage);
        Assert.Contains("will not change on later moves", result.ErrorMessage);
        Assert.Equal(new[] { "team", "key", "reapply" }, log);
    }

    // Review RM-F7: the suggestion save failed after the rename AND the team save failed - both are reported.
    [Fact]
    public async Task MoveAsync_NameRecordAndTeamSaveBothFail_BothAreReported()
    {
        var mover = new DirectorTeamMover(new MovingService(), r => new SessionCreationHold(0, () => { }),
            (_, _) => throw new IOException("disk full"), _ => { }, null,
            _ => throw new NameChangedButNotRecordedException("SOREN_NORTH - Team B", new IOException("suggestion file locked")));

        var result = await mover.MoveAsync("dir-1", TeamBChoice, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(
            DirectorTeamMover.MovedButTeamNotRecorded("Team B", "disk full") + " " +
            DirectorTeamMover.NameChangedWillNotFollowNote("SOREN_NORTH - Team B", "suggestion file locked"),
            result.ErrorMessage);
    }

    // Review RM-F7, the other later failures: each carries the name outcome too.
    [Fact]
    public async Task MoveAsync_NameNotChangedAndKeySaveFails_BothAreReported()
    {
        var mover = new DirectorTeamMover(new MovingService(), r => new SessionCreationHold(0, () => { }),
            (_, _) => { }, _ => throw new UnauthorizedAccessException("file locked"), null,
            _ => throw new IOException("registry locked"));

        var result = await mover.MoveAsync("dir-1", TeamBChoice, CancellationToken.None);

        Assert.Equal(
            DirectorTeamMover.MovedButKeyNotSaved("Team B", "file locked") + " " + DirectorTeamMover.NameNotChangedNote("registry locked"),
            result.ErrorMessage);
    }

    [Fact]
    public async Task MoveAsync_NameChangedButNotRecordedAndReapplyFails_BothAreReported()
    {
        var mover = new DirectorTeamMover(new MovingService(), r => new SessionCreationHold(0, () => { }),
            (_, _) => { }, _ => { }, () => throw new InvalidOperationException("stream down"),
            _ => throw new NameChangedButNotRecordedException("SOREN_NORTH - Team B", new IOException("suggestion file locked")));

        var result = await mover.MoveAsync("dir-1", TeamBChoice, CancellationToken.None);

        Assert.Equal(
            DirectorTeamMover.MovedButNotApplied("Team B", "stream down") + " " +
            DirectorTeamMover.NameChangedWillNotFollowNote("SOREN_NORTH - Team B", "suggestion file locked"),
            result.ErrorMessage);
    }

    [Fact]
    public async Task MoveAsync_NameCannotFollow_TheMoveStillFinishesAndSaysSo()
    {
        var log = new List<string>();
        var mover = new DirectorTeamMover(new MovingService(), r => new SessionCreationHold(0, () => { }),
            (t, _) => log.Add("team"), k => log.Add("key"),
            () => { log.Add("reapply"); return Task.CompletedTask; },
            _ => throw new IOException("registry locked"));

        var result = await mover.MoveAsync("dir-1", TeamBChoice, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(DirectorTeamMover.MovedButNotRenamed("Team B", "registry locked"), result.ErrorMessage);
        Assert.Equal(new[] { "team", "key", "reapply" }, log);
    }
}

/// <summary>
/// Review RM-F6: a removal names a team only when the recorded team was saved for the very key the Gateway refused.
/// </summary>
public sealed class TeamNameForKeyTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "dt-teamkey-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }

    [Fact]
    public void TeamNameForKey_TeamSavedForThatKey_NamesIt()
    {
        DirectorTeamStore.SaveAt(_home, new DirectorTeam("t-b", "Team B"), "team-b-key");

        Assert.Equal("Team B", DirectorTeamStore.TeamNameForKeyAt(_home, "team-b-key"));
    }

    // The Team B key was saved but writing the Team B team failed, so the Team A file - saved for the Team A key - is
    // still there. A refusal of the Team B key must not name Team A.
    [Fact]
    public void TeamNameForKey_TeamFileLeftFromAnotherKey_NamesNoTeam()
    {
        DirectorTeamStore.SaveAt(_home, new DirectorTeam("t-a", "Team A"), "team-a-key");

        Assert.Null(DirectorTeamStore.TeamNameForKeyAt(_home, "team-b-key"));
    }

    [Fact]
    public void TeamNameForKey_TeamSavedWithNoKey_OrNothingSaved_NamesNoTeam()
    {
        Assert.Null(DirectorTeamStore.TeamNameForKeyAt(_home, "any-key"));
        DirectorTeamStore.SaveAt(_home, new DirectorTeam("t-a", "Team A"));
        Assert.Null(DirectorTeamStore.TeamNameForKeyAt(_home, "any-key"));
    }

    [Fact]
    public void SaveAt_WithAKey_NeverWritesTheKey()
    {
        DirectorTeamStore.SaveAt(_home, new DirectorTeam("t-b", "Team B"), "team-b-secret-key");

        Assert.DoesNotContain("team-b-secret-key", File.ReadAllText(DirectorTeamStore.TeamFileAt(_home)));
        Assert.Equal(new DirectorTeam("t-b", "Team B"), DirectorTeamStore.LoadAt(_home));
    }
}
