using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>Where Handle the issue reads and writes what it needs to reach the Fleet Manager, one delegate per fact.</summary>
/// <param name="Status">Whether the account's Fleet Manager runs, as the Settings tab's one fold answers it.</param>
/// <param name="Start">Start the Fleet Manager where the owner's setting says, for this request.</param>
/// <param name="LastKnownSession">What the account's Directors last pushed for one session, or null.</param>
/// <param name="Record">Store the problem as an event for the Fleet Manager. Null when it already waits there.</param>
/// <param name="Queued">Book a delivery of what was stored.</param>
internal sealed record FleetManagerProblemSources(
    Func<TenantId, CancellationToken, Task<FleetManagerStatusDto>> Status,
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
/// it has started, so a refused start says its own sentence and leaves nothing behind.
/// </summary>
internal static class FleetManagerProblemEndpoint
{
    /// <summary>The seat a Fleet Manager talk answers with, where a factory talk names its seat.</summary>
    public const string SeatId = "fleet-manager";

    public static async Task<IResult> HandleAsync(HttpContext ctx, TenantId tenant, FleetManagerProblemSources s,
        FleetManagerProblemEvent problem, string factory)
    {
        FileLog.Write($"[FleetManagerProblemEndpoint] HandleAsync: factory={factory}, run={problem.RunId}");
        var status = await s.Status(tenant, ctx.RequestAborted);
        var running = status.State == FleetManagerStatusDto.StateRunning;
        var sessionId = running ? status.SessionId : null;
        if (!running)
        {
            var started = await s.Start(ctx, tenant);
            if (started.Placement is null)
            {
                FileLog.Write($"[FleetManagerProblemEndpoint] the start was refused ({started.Status}): {started.Error}");
                return Results.Json(new { error = started.Error }, statusCode: started.Status);
            }
            sessionId = started.Placement.Status.SessionId;
        }
        if (string.IsNullOrEmpty(sessionId))
            throw new InvalidOperationException($"the Fleet Manager reads as {(running ? "running" : "started")} but names no session");

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
        }, statusCode: running ? StatusCodes.Status200OK : StatusCodes.Status201Created);
    }
}
