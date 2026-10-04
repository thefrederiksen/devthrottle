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
/// redraw, and it redraws the name and the chip together.
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
    public static async Task<OperationResult<MobileEnrollmentResponse>> SignInChooseTeamAndEnrollAsync(
        Window owner, string deviceId, string machineName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(owner);
        FileLog.Write($"[HostedTeamSetup] SignInChooseTeamAndEnrollAsync: deviceId={deviceId}");

        var recorded = false;
        DirectorTeam? chosenTeam = null;
        var runner = new GatewayAccountEnrollRunner(persistTeam: team => { chosenTeam = team; recorded = true; });

        var result = await runner.SignInChooseTeamAndEnrollHostedAsync(
            deviceId, machineName, (question, _) => AskAsync(owner, question), ct);

        if (!result.Success)
        {
            FileLog.Write($"[HostedTeamSetup] SignInChooseTeamAndEnrollAsync: not enrolled: {result.ErrorMessage}");
            return OperationResult<MobileEnrollmentResponse>.Fail(result.ErrorMessage ?? "Could not join the hosted Gateway.");
        }
        if (!recorded)
            throw new InvalidOperationException("The hosted enrollment succeeded without deciding this Director's team.");

        if (result.Value!.DirectorName is { } name)
        {
            NamedInstanceRegistry.Rename(InstanceContext.Slug, name);
            FileLog.Write("[HostedTeamSetup] SignInChooseTeamAndEnrollAsync: Director renamed from screen D1");
        }

        if (chosenTeam is null)
            DirectorTeamStore.Clear();
        else
            DirectorTeamStore.Save(chosenTeam);

        FileLog.Write($"[HostedTeamSetup] SignInChooseTeamAndEnrollAsync: enrolled ({(chosenTeam is null ? "no teams on this Gateway" : chosenTeam.IsPersonal ? "personal account" : "team " + chosenTeam.TeamId)})");
        return OperationResult<MobileEnrollmentResponse>.Ok(new MobileEnrollmentResponse { DeviceKey = result.Value.DeviceKey });
    }

    // Screen D1, on the UI thread whatever thread the engine called back on.
    private static Task<TeamAnswer?> AskAsync(Window owner, TeamQuestion question)
        => Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new TeamChoiceDialog(question);
            return await dialog.ShowDialog<TeamAnswer?>(owner);
        });
}
