using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>
/// What a create on one Director needs from the Gateway: the same stores <see cref="GatewayEndpoints.Map"/> is handed.
/// Built once where the routes are mapped, and once more by the host for the Factories screen's Talk route, from the
/// same fields - so both doors resolve the same Director, mission and factory the same way.
/// </summary>
internal sealed record DirectorSpawnDoor(
    Tenancy.HostedTenantBoundary TenantBoundary,
    DirectorRegistry Registry,
    Func<string, History.SessionFactoryLookup> SessionFactoryOf,
    DirectorCommandRouter.SendDirectorCommandAsync? SendCommand,
    Core.Sessions.MissionStore? Missions,
    Workflows.WorkflowRunStore? WorkflowRuns,
    Workspaces.WorkspaceStore? Workspaces);

/// <summary>The answer of one create: the session the Director started, or the refusal to hand the caller.</summary>
internal sealed record DirectorSpawnOutcome(SessionDto? Session, IResult? Refusal)
{
    public static DirectorSpawnOutcome Started(SessionDto session) => new(session, null);
    public static DirectorSpawnOutcome Refused(IResult refusal) => new(null, refusal);
}

internal static partial class GatewayEndpoints
{
    /// <summary>
    /// Start one session on Director <paramref name="id"/> for the calling person or session: the whole of
    /// <c>POST /directors/{id}/sessions</c>, the door a person's New Session uses. It settles who is asking, which
    /// factory the session belongs to and its mission seat from the verified credential, sends the create down the
    /// Director's stream, and records the run participant and a restore's claim.
    ///
    /// ONE PATH, TWO CALLERS. The Factories screen's Talk button (phase C) calls this too rather than copying it,
    /// because a copy is how the two spawn doors drifted apart before (issue #2629).
    /// </summary>
    internal static async Task<DirectorSpawnOutcome> StartSessionOnDirectorAsync(
        HttpContext ctx, string id, NewSessionRequest req, DirectorSpawnDoor door)
    {
        if (!TryResolveOwnedDirector(ctx, door.TenantBoundary, door.Registry, id, out var d, out var ownerErr))
            return DirectorSpawnOutcome.Refused(ownerErr);
        if (req is null || string.IsNullOrWhiteSpace(req.RepoPath))
            return DirectorSpawnOutcome.Refused(Results.BadRequest(new { error = "repoPath is required" }));

        FileLog.Write($"[GatewayEndpoints] POST /directors/{id}/sessions: repo={req.RepoPath}, agent={req.Agent}");

        // THE MISSION NAME AND THE WORKFLOW SEAT, resolved here exactly as the machine door resolves
        // them (issue #2629). This is the door an unqualified `cc-devthrottle session spawn` uses, and
        // it used to forward the create VERBATIM - so a mission-scoped spawn reached the Director
        // carrying an id and no name, the Director read that as an old caller naming a mission in its
        // own stale local store, and refused a mission that was real, active and listed. The seat was
        // missing too, silently: a session in a mission with none of the conduct the mission pins.
        //
        // Both doors now call the SAME resolver, so neither can drift from the other again.
        var spawnTenant = ResolveReadTenant(ctx, door.TenantBoundary);
        if (spawnTenant is null)
            return DirectorSpawnOutcome.Refused(Results.Json(new { error = "no tenant is bound to this request" },
                statusCode: StatusCodes.Status403Forbidden));
        var spawnRoute = $"POST /directors/{id}/sessions";

        // WHO IS ASKING, and WHO WILL OWN THE RESULT (issue #2838). This door previously forwarded the
        // body VERBATIM - no origin stamping of any kind - while the machine door stamped a person's
        // device. It is also the door an unqualified `cc-devthrottle session spawn` uses, so the
        // untrusted path was the common one.
        if (!SpawnOrigin.TryEstablish(req, ctx, spawnRoute, out var originError))
            return DirectorSpawnOutcome.Refused(originError!);

        // WHICH FACTORY the new session belongs to (Factory Memory mission, phase 1), settled from the same
        // credential, in the same one place, for the same reason: this door is the one an unqualified
        // `cc-devthrottle session spawn` uses, so it is the door a session would join a factory through.
        if (!SpawnFactory.TryEstablish(req, ctx, spawnRoute, door.SessionFactoryOf, out var factoryError))
            return DirectorSpawnOutcome.Refused(factoryError!);

        // A RESTORE'S CREATE (the Message Load mission, inspection 7, ruling 3) carries the token its Director
        // stored on the workspace seat before sending it. Only a Director restores, so only a Director's
        // credential may carry one - a claim from anyone else could mark a seat restored as a session of the
        // caller's choosing.
        var restoreClaim = req.RestoreClaim;
        if (restoreClaim is not null)
        {
            if (!WorkspaceEndpoints.IsDirectorCredential(ctx))
            {
                FileLog.Write($"[GatewayEndpoints] {spawnRoute}: REFUSED - a restore claim from a caller that is not a Director");
                return DirectorSpawnOutcome.Refused(Results.Json(new { error = "only a Director restoring a workspace may send a restore claim" },
                    statusCode: StatusCodes.Status403Forbidden));
            }
            // The same binding as the mark route (inspection 11, ruling 1): the claim is sent by the Director the
            // create is for, on the credential that Director said Hello on - not by another key of the account.
            if (!door.Registry.IsRegisteredByCredential(spawnTenant.Value, id, AuthMiddleware.RegisteringCredential(ctx)))
            {
                FileLog.Write($"[GatewayEndpoints] {spawnRoute}: REFUSED - a restore claim on a credential Director {id} is not connected on");
                return DirectorSpawnOutcome.Refused(Results.Json(new { error = $"a restore claim is sent only by Director '{id}' itself, on the credential it is connected on" },
                    statusCode: StatusCodes.Status403Forbidden));
            }
            if (door.Workspaces is null)
                return DirectorSpawnOutcome.Refused(Results.Json(new { error = "this Gateway has no workspace store, so a restore's create cannot be recorded and is not sent" },
                    statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        if (!SpawnMissionAndSeat.TryResolve(req, spawnTenant.Value, door.Missions, door.WorkflowRuns, spawnRoute,
                out var seatRun, out var resolveError))
            return DirectorSpawnOutcome.Refused(resolveError!);

        // Issue #1177 (Phase 1): create rides the target Director's stream. Tunnel-only: a null return
        // means the Director is not connected, and a non-Ok stream result (validation/creation failure)
        // collapses to 502 - both surface as the error below.
        SessionDto? body;
        string? err;
        var streamResult = await DirectorCommandRouter.TrySendAsync(door.SendCommand, id, "create", "", req, CancellationToken.None);
        if (streamResult is null)
        {
            body = null;
            err = "director not connected to the tunnel";
        }
        else
        {
            body = streamResult.Ok ? DirectorCommandRouter.ReadBody<SessionDto>(streamResult) : null;
            err = streamResult.Ok ? null : DirectorCommandRouter.DescribeFailure(streamResult);
        }
        if (body is null)
            return DirectorSpawnOutcome.Refused(Results.Problem(err ?? "failed", statusCode: StatusCodes.Status502BadGateway));

        // The membership row governance reads, on the same terms as the machine door: recorded only
        // when the Director's reply proves the seat landed, and never turned into an HTTP failure the
        // caller would retry into a second session.
        SpawnMissionAndSeat.RecordParticipant(seatRun, door.WorkflowRuns, req, body, d.MachineName ?? "", spawnRoute);

        // The restore's record of this create, written HERE - in the process that performed it, after the
        // Director confirmed it - so it does not depend on the answer reaching the restoring Director. Never
        // turned into an HTTP failure: the session exists, and a failure would be retried into a second one.
        if (restoreClaim is not null && door.Workspaces is not null)
        {
            try
            {
                door.Workspaces.RecordRestoredByClaim(restoreClaim, id, body.SessionId, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayEndpoints] {spawnRoute}: the restore of seat {restoreClaim.SeatSessionId} in workspace {restoreClaim.WorkspaceId} started {body.SessionId} but could NOT be recorded by its token: {ex.Message}");
            }
        }

        return DirectorSpawnOutcome.Started(body);
    }
}
