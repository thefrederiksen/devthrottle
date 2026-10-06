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
        var refusal = GatewayKeyRefusal.FromUnauthorizedBody(RemovedBody, "Team B");

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
        var refusal = GatewayKeyRefusal.FromUnauthorizedBody(RemovedBody, null);

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
        var refusal = GatewayKeyRefusal.FromUnauthorizedBody(body, "Team B");

        Assert.Equal(GatewayKeyRefusalKind.KeyRevoked, refusal.Kind);
        Assert.Equal("Key revoked", refusal.ChipText);
        Assert.StartsWith("The Gateway revoked this Director's key", refusal.Summary);
        Assert.DoesNotContain("removed", refusal.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // The reason without the revoked code is not a revoke the Gateway stated.
    [Theory]
    [InlineData("{\"error\":\"missing or invalid token\"}")]
    [InlineData("{\"reason\":\"team_member_removed\"}")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("<html>401 Unauthorized</html>")]
    [InlineData("[1,2]")]
    public void FromUnauthorizedBody_NoRevokedCode_IsAKeyNotAccepted(string? body)
    {
        var refusal = GatewayKeyRefusal.FromUnauthorizedBody(body, "Team B");

        Assert.Equal(GatewayKeyRefusalKind.KeyNotAccepted, refusal.Kind);
        Assert.Equal("Key not accepted", refusal.ChipText);
        Assert.DoesNotContain("removed", refusal.Summary, StringComparison.OrdinalIgnoreCase);
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
            "{\"code\":\"device_credential_revoked\",\"reason\":\"team_member_removed\"}", "Team B");

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
        var refusal = GatewayKeyRefusal.FromUnauthorizedBody("{\"code\":\"device_credential_revoked\"}", "Team B");

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
/// "&lt;computer&gt; - &lt;team&gt;", follows a move; a name the person typed never changes; a Director with no record
/// keeps its name.
/// </summary>
public sealed class DirectorNameFollowsMoveTests : IDisposable
{
    private static readonly DirectorTeam TeamA = new("t-a", "Team A");
    private static readonly DirectorTeam TeamB = new("t-b", "Team B");
    private static readonly TeamChoice TeamBChoice = new("t-b", "Team B", "You are a Developer, 3 people");

    private readonly string _home = Path.Combine(Path.GetTempPath(), "dt-name-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }

    // ---- the rule --------------------------------------------------------------------------------------------------

    [Fact]
    public void Decide_NameIsTheRecordedSuggestion_FollowsToTheNewTeam()
    {
        var decision = DirectorNameAfterMove.Decide("SOREN_NORTH - Team A", DirectorNameSuggestion.For("SOREN_NORTH", "Team A"), TeamB);

        Assert.Equal("SOREN_NORTH - Team B", decision.NewName);
        Assert.Equal(new DirectorNameSuggestion("SOREN_NORTH", "SOREN_NORTH - Team B"), decision.Record);
    }

    // No record: a Director set up before this was recorded keeps its name even when it looks like a suggestion.
    [Fact]
    public void Decide_NoRecord_KeepsTheName()
    {
        var decision = DirectorNameAfterMove.Decide("SOREN_NORTH - Team A", null, TeamB);

        Assert.Null(decision.NewName);
        Assert.Null(decision.Record);
    }

    [Fact]
    public void Decide_RenamedSinceSetup_KeepsTheNameAndDropsTheRecord()
    {
        var decision = DirectorNameAfterMove.Decide("Build box", DirectorNameSuggestion.For("SOREN_NORTH", "Team A"), TeamB);

        Assert.Null(decision.NewName);
        Assert.Null(decision.Record);
    }

    [Fact]
    public void IfSuggested_TypedName_IsNull_SuggestedName_IsTheSuggestion()
    {
        Assert.Null(DirectorNameSuggestion.IfSuggested("SOREN_NORTH", TeamA, "Build box"));
        Assert.Null(DirectorNameSuggestion.IfSuggested("SOREN_NORTH", TeamA, "SOREN_NORTH - Team B"));
        Assert.Equal(new DirectorNameSuggestion("SOREN_NORTH", "SOREN_NORTH - Team A"),
            DirectorNameSuggestion.IfSuggested("SOREN_NORTH", TeamA, " SOREN_NORTH - Team A "));
    }

    // The question's suggestion and the move's follow are built by one builder, so they cannot drift.
    [Fact]
    public void SuggestDirectorName_IsTheRecordedSuggestionsName()
    {
        Assert.Equal(DirectorNameSuggestion.For("SOREN_NORTH", "Team B").Name,
            TeamChoices.SuggestDirectorName("SOREN_NORTH", TeamBChoice));
    }

    // ---- the follower, over the real store -------------------------------------------------------------------------

    private (DirectorNameFollower Follower, Func<string> Name) Follower(string startName)
    {
        var name = startName;
        var follower = new DirectorNameFollower(
            () => name,
            n => name = n,
            () => DirectorNameSuggestionStore.LoadAt(_home),
            s => DirectorNameSuggestionStore.SaveAt(_home, s));
        return (follower, () => name);
    }

    [Fact]
    public void FollowMove_SuggestedName_RenamesAndRecordsTheNewSuggestion()
    {
        DirectorNameSuggestionStore.SaveAt(_home, DirectorNameSuggestion.For("SOREN_NORTH", "Team A"));
        var (follower, name) = Follower("SOREN_NORTH - Team A");

        var renamed = follower.FollowMove(TeamB);

        Assert.Equal("SOREN_NORTH - Team B", renamed);
        Assert.Equal("SOREN_NORTH - Team B", name());
        Assert.Equal(new DirectorNameSuggestion("SOREN_NORTH", "SOREN_NORTH - Team B"), DirectorNameSuggestionStore.LoadAt(_home));
    }

    [Fact]
    public void FollowMove_TypedName_NeverChanges()
    {
        // Setup recorded nothing, because the person typed the name.
        var (follower, name) = Follower("Build box");

        Assert.Null(follower.FollowMove(TeamB));
        Assert.Equal("Build box", name());
        Assert.Null(DirectorNameSuggestionStore.LoadAt(_home));
    }

    [Fact]
    public void FollowMove_NoRecord_KeepsANameThatLooksLikeASuggestion()
    {
        var (follower, name) = Follower("SOREN_NORTH - Team A");

        Assert.Null(follower.FollowMove(TeamB));
        Assert.Equal("SOREN_NORTH - Team A", name());
    }

    [Fact]
    public void FollowMove_RenamedSinceSetup_KeepsTheNameAndRemovesTheRecord()
    {
        DirectorNameSuggestionStore.SaveAt(_home, DirectorNameSuggestion.For("SOREN_NORTH", "Team A"));
        var (follower, name) = Follower("Build box");

        Assert.Null(follower.FollowMove(TeamB));
        Assert.Equal("Build box", name());
        Assert.Null(DirectorNameSuggestionStore.LoadAt(_home));
    }

    [Fact]
    public void SuggestionStore_UnreadableFile_Throws()
    {
        var path = DirectorNameSuggestionStore.SuggestionFileAt(_home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"machineName\":\"\",\"name\":\"x\"}");

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
    // the connection is re-applied (whose Hello carries the name to the Fleet Map).
    [Fact]
    public async Task MoveAsync_SuggestedName_RenamesBeforeTheTeamIsRecordedAndBeforeTheReconnect()
    {
        var log = new List<string>();
        var mover = new DirectorTeamMover(new MovingService(), r => new SessionCreationHold(0, () => { }),
            t => log.Add("team:" + t.Name), k => log.Add("key"),
            () => { log.Add("reapply"); return Task.CompletedTask; },
            t => { log.Add("name:" + t.Name); return "SOREN_NORTH - " + t.Name; });

        var result = await mover.MoveAsync("dir-1", TeamBChoice, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "name:Team B", "team:Team B", "key", "reapply" }, log);
    }

    [Fact]
    public async Task MoveAsync_NameCannotFollow_TheMoveStillFinishesAndSaysSo()
    {
        var log = new List<string>();
        var mover = new DirectorTeamMover(new MovingService(), r => new SessionCreationHold(0, () => { }),
            t => log.Add("team"), k => log.Add("key"),
            () => { log.Add("reapply"); return Task.CompletedTask; },
            _ => throw new IOException("registry locked"));

        var result = await mover.MoveAsync("dir-1", TeamBChoice, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(DirectorTeamMover.MovedButNotRenamed("Team B", "registry locked"), result.ErrorMessage);
        Assert.Equal(new[] { "team", "key", "reapply" }, log);
    }
}
