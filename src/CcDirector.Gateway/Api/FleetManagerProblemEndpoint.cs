using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>Where Handle the issue reads and writes what it needs to reach the Fleet Manager, one delegate per fact.</summary>
/// <param name="Status">Whether the account's Fleet Manager runs, as the Settings tab's one fold answers it.</param>
/// <param name="Successor">The session waiting to take over in a restart or a move, or null.</param>
/// <param name="Start">Start the Fleet Manager where the owner's setting says, for this request.</param>
/// <param name="LastKnownSession">What the account's Directors last pushed for one session, or null.</param>
/// <param name="Record">Store the problem as an event for the Fleet Manager. Null when it already waits there.</param>
/// <param name="Queued">Book a delivery of what was stored.</param>
internal sealed record FleetManagerProblemSources(
    Func<TenantId, CancellationToken, Task<FleetManagerStatusDto>> Status,
    Func<TenantId, string?> Successor,
    Func<HttpContext, TenantId, Task<FleetManagerPlacementResult>> Start,
    Func<TenantId, string, SessionDto?> LastKnownSession,
    Func<TenantId, FleetManagerProblemEvent, FleetManagerEventDto?> Record,
    Action<TenantId> Queued);

/// <summary>
/// HANDLE THE ISSUE ON A FACTORY WITH NO BOSS. The problem goes to the account's one Fleet Manager as a <c>problem</c>
/// event, and the answer opens that session.
///
/// AN EVENT AND NOT A TYPED PROMPT, because the Fleet Manager may be in the middle of a turn: an event reaches it only
/// while it is idle, survives a restart or a move, and stays until it is acknowledged. When no Fleet Manager runs one is
/// started where the owner's setting says, by the same service as the Settings tab, and the event is stored only once
/// it has started, so a refused start says its own sentence and leaves nothing behind. DURING A RESTART OR A MOVE the
/// old session may have closed before the new one takes the mark: the event is stored all the same, because delivery
/// holds it for the session the mark reaches. A start that lost a race to another press finds that session running.
/// </summary>
internal static class FleetManagerProblemEndpoint
{
    /// <summary>The seat a Fleet Manager talk answers with, where a factory talk names its seat.</summary>
    public const string SeatId = "fleet-manager";

    public static async Task<IResult> HandleAsync(HttpContext ctx, TenantId tenant, FleetManagerProblemSources s,
        FleetManagerProblemEvent problem, string factory)
    {
        FileLog.Write($"[FleetManagerProblemEndpoint] HandleAsync: factory={factory}, run={problem.RunId}");
        var sessionId = await ReachableAsync(tenant, s, ctx.RequestAborted);
        var started = false;
        if (sessionId is null)
        {
            var start = await s.Start(ctx, tenant);
            if (start.Placement is not null)
            {
                sessionId = start.Placement.Status.SessionId;
                started = true;
                if (string.IsNullOrEmpty(sessionId))
                    throw new InvalidOperationException("the Fleet Manager reads as started but names no session");
            }
            else
            {
                sessionId = await ReachableAsync(tenant, s, ctx.RequestAborted);
                if (sessionId is null)
                {
                    FileLog.Write($"[FleetManagerProblemEndpoint] the start was refused ({start.Status}): {start.Error}");
                    return Results.Json(new { error = start.Error }, statusCode: start.Status);
                }
            }
        }

        var stored = s.Record(tenant, problem);
        s.Queued(tenant);
        FileLog.Write($"[FleetManagerProblemEndpoint] run {problem.RunId} {(stored is null ? "already waits for" : "sent to")} the Fleet Manager {sessionId}");

        var session = s.LastKnownSession(tenant, sessionId);
        return Results.Json(new FactoryTalkStartedDto
        {
            SessionId = sessionId,
            SessionName = session?.Name ?? "Fleet Manager",
            Href = "/session/" + Uri.EscapeDataString(sessionId),
            Factory = factory,
            Seat = SeatId,
            Computer = session?.MachineName ?? "",
            DirectorId = session?.DirectorId ?? "",
        }, statusCode: started ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }

    /// <summary>The running Fleet Manager, or the session waiting to take over; null when neither exists.</summary>
    private static async Task<string?> ReachableAsync(TenantId tenant, FleetManagerProblemSources s, CancellationToken ct)
    {
        var status = await s.Status(tenant, ct);
        if (status.State == FleetManagerStatusDto.StateRunning)
        {
            if (string.IsNullOrEmpty(status.SessionId))
                throw new InvalidOperationException("the Fleet Manager reads as running but names no session");
            return status.SessionId;
        }
        return s.Successor(tenant);
    }
}
