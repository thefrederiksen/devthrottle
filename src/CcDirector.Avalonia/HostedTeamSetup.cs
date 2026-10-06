using Avalonia.Controls;
using Avalonia.Threading;
using CcDirector.Core.Instances;
using CcDirector.Core.Teams;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Setup.Engine;

namespace CcDirector.Avalonia;

/// <summary>
/// The hosted join FOR A TEAM, as both desktop surfaces run it - the first-run wizard and the Gateway connection
/// panel (devthrottle_internal#2311). One place, so the two cannot drift: sign in, ask which team on screen D1
/// when there is a team to choose, enroll with that team's id, then name the Director and record its team.
///
/// The name is saved BEFORE the team is recorded, because recording the team is what tells the title bar to
/// redraw, and it redraws the name and the chip together. Once the Gateway has enrolled the Director its key is
/// stored; if the name or the team then cannot be saved, the person is told the join happened and what was not
/// saved - never "could not join" (review findings F4 and F5).
/// </summary>
internal static class HostedTeamSetup
{
    /// <summary>
    /// Sign in, choose the team, enroll. Returns the issued key in the shape every enroll path returns, or the
    /// reason it did not happen. The Gateway's refusal is passed through in its own words.
    /// </summary>
    /// <param name="owner">The window D1 opens over.</param>
    /// <param name="deviceId">This Director's id.</param>
    /// <param name="machineName">This computer's name.</param>
    /// <param name="ct">Cancels the sign-in or the enrollment.</param>
    public static Task<OperationResult<MobileEnrollmentResponse>> SignInChooseTeamAndEnrollAsync(
        Window owner, string deviceId, string machineName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return RunAsync(
            persistTeam => new GatewayAccountEnrollRunner(persistTeam: persistTeam),
            question => AskAsync(owner, question),
            name => NamedInstanceRegistry.Rename(InstanceContext.Slug, name),
            DirectorNameSuggestionStore.Save,
            RecordTeamInThisHome,
            deviceId, machineName, ct);
    }

    /// <summary>The words for a join whose name could not be saved afterwards.</summary>
    public static string JoinedButNotNamed(string teamName, string error) =>
        $"This Director joined {teamName} and its key is saved, but its name could not be saved ({error}). " +
        "Restart the Director to finish connecting, then rename it from File, Rename this director.";

    /// <summary>The words for a join whose team could not be recorded afterwards.</summary>
    public static string JoinedButTeamNotRecorded(string teamName, string error) =>
        $"This Director joined {teamName} and its key is saved, but this computer could not record which team it is for ({error}), " +
        $"so it may show the wrong team. Connect it again from the Gateway tab and choose {teamName}.";

    /// <summary>
    /// The whole D1 transaction with every outside dependency passed in, so a test drives exactly what the two
    /// surfaces run: <paramref name="makeRunner"/> receives the action the runner must call with the chosen team.
    /// </summary>
    internal static async Task<OperationResult<MobileEnrollmentResponse>> RunAsync(
        Func<Action<DirectorTeam?>, GatewayAccountEnrollRunner> makeRunner,
        Func<TeamQuestion, Task<TeamAnswer?>> ask,
        Action<string> rename,
        Action<DirectorNameSuggestion?> recordSuggestion,
        Action<DirectorTeam?> recordTeam,
        string deviceId, string machineName, CancellationToken ct)
    {
        FileLog.Write($"[HostedTeamSetup] RunAsync: deviceId={deviceId}");

        var decided = false;
        DirectorTeam? chosenTeam = null;
        var runner = makeRunner(team => { chosenTeam = team; decided = true; });

        var result = await runner.SignInChooseTeamAndEnrollHostedAsync(deviceId, machineName, (question, _) => ask(question), ct);
        if (!result.Success)
        {
            FileLog.Write($"[HostedTeamSetup] RunAsync: not enrolled: {result.ErrorMessage}");
            return OperationResult<MobileEnrollmentResponse>.Fail(result.ErrorMessage ?? "Could not join the hosted Gateway.");
        }
        if (!decided)
            throw new InvalidOperationException("The hosted enrollment succeeded without deciding this Director's team.");

        // The key is stored. From here a failure is reported as what it is, not as a failed join.
        var teamName = chosenTeam?.Name ?? "the hosted Gateway";
        string? renameError = null;
        if (result.Value!.DirectorName is { } name)
        {
            try
            {
                rename(name);
                // Live proof F4: whether the name is the suggestion "<computer> - <team>" is recorded now, while it
                // is known, so a later move can tell it from a name the person typed. Recorded only once the
                // rename has happened, so the record never names a suggestion the Director does not carry.
                var suggestion = chosenTeam is null ? null : DirectorNameSuggestion.IfSuggested(machineName, chosenTeam, name);
                recordSuggestion(suggestion);
                FileLog.Write($"[HostedTeamSetup] RunAsync: Director renamed from screen D1 ({(suggestion is null ? "a name of the person's own" : "the suggested name")})");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[HostedTeamSetup] RunAsync: enrolled, rename FAILED: {ex.Message}");
                renameError = ex.Message;
            }
        }

        try
        {
            recordTeam(chosenTeam);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[HostedTeamSetup] RunAsync: enrolled, team NOT recorded: {ex.Message}");
            return OperationResult<MobileEnrollmentResponse>.Fail(JoinedButTeamNotRecorded(teamName, ex.Message));
        }

        if (renameError is not null)
            return OperationResult<MobileEnrollmentResponse>.Fail(JoinedButNotNamed(teamName, renameError));

        FileLog.Write($"[HostedTeamSetup] RunAsync: enrolled ({(chosenTeam is null ? "no teams on this Gateway" : chosenTeam.IsPersonal ? "personal account" : "team " + chosenTeam.TeamId)})");
        return OperationResult<MobileEnrollmentResponse>.Ok(new MobileEnrollmentResponse { DeviceKey = result.Value.DeviceKey });
    }

    private static void RecordTeamInThisHome(DirectorTeam? team)
    {
        if (team is null)
            DirectorTeamStore.Clear();
        else
            DirectorTeamStore.Save(team);
    }

    // Screen D1, on the UI thread whatever thread the engine called back on.
    private static Task<TeamAnswer?> AskAsync(Window owner, TeamQuestion question)
        => Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new TeamChoiceDialog(question);
            return await dialog.ShowDialog<TeamAnswer?>(owner);
        });
}
