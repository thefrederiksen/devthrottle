using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Engine.Dispatcher;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Maps <c>POST /dispatch</c> (issue #329, plan 1B) - the comm-dispatch verb: mechanically
/// execute the machine-bound send of ONE communication-queue item that the human approval
/// workflow has ALREADY approved. The body names the item by queue id; the Director runs the
/// existing Engine dispatch path (route lookup, channel tool via argument-list Process.Start,
/// item advanced to posted) and reports the outcome. The verb contains zero decision logic:
/// an item not currently in the approved state is refused (409) and nothing sends - this
/// endpoint preserves the standing human-approval rule for outgoing communications by
/// construction. Token-gated by the host's auth middleware like every other route, and
/// FileLog-audited on every call (item id, channel, outcome, caller).
/// </summary>
internal static class DispatchEndpoint
{
    public static void Map(IEndpointRouteBuilder app, Func<CommunicationDispatcher?> dispatcherAccessor)
    {
        app.MapPost("/dispatch", async (HttpContext ctx) =>
        {
            DispatchRequest? req;
            try
            {
                req = await ctx.Request.ReadFromJsonAsync<DispatchRequest>(ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[DispatchEndpoint] POST /dispatch: invalid JSON body ({ex.Message})");
                return Results.BadRequest(new { error = "invalid JSON request body" });
            }

            if (req is null || string.IsNullOrWhiteSpace(req.QueueItemId))
                return Results.BadRequest(new { error = "request body is required: { queueItemId } - the id of an APPROVED communication queue item" });

            // Resolved per request: the dispatcher exists only after the Engine's deferred
            // email-tool discovery finishes (and not at all when the Engine is not hosted).
            var dispatcher = dispatcherAccessor();
            if (dispatcher is null)
            {
                FileLog.Write($"[DispatchEndpoint] POST /dispatch: dispatcher not ready (item={Truncate(req.QueueItemId)})");
                return Results.Json(
                    new { error = "communication dispatcher is not ready on this Director (engine starting or channel discovery still running) - retry shortly" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // The audit line (issue #329): every dispatch attempt is recorded with caller.
            FileLog.Write($"[DispatchEndpoint] POST /dispatch: item={Truncate(req.QueueItemId)}, caller={ctx.Connection.RemoteIpAddress}");

            var result = await dispatcher.DispatchByIdAsync(req.QueueItemId);

            FileLog.Write($"[DispatchEndpoint] POST /dispatch result: item={Truncate(req.QueueItemId)}, ticket=#{result.TicketNumber}, outcome={result.Outcome}, channel={result.Channel ?? "(none)"}");

            var dto = ToDto(result);
            var statusCode = result.Outcome switch
            {
                QueueDispatchOutcome.Dispatched => StatusCodes.Status200OK,
                QueueDispatchOutcome.NotFound => StatusCodes.Status404NotFound,
                QueueDispatchOutcome.NotApproved => StatusCodes.Status409Conflict,
                QueueDispatchOutcome.UnsupportedPlatform => StatusCodes.Status409Conflict,
                QueueDispatchOutcome.InvalidItem => StatusCodes.Status422UnprocessableEntity,
                QueueDispatchOutcome.SendFailed => StatusCodes.Status502BadGateway,
                _ => throw new InvalidOperationException($"unmapped dispatch outcome: {result.Outcome}"),
            };
            return Results.Json(dto, statusCode: statusCode);
        });
    }

    private static DispatchResultDto ToDto(QueueDispatchResult result) => new()
    {
        QueueItemId = result.QueueItemId,
        Dispatched = result.Dispatched,
        Outcome = OutcomeName(result.Outcome),
        TicketNumber = result.TicketNumber > 0 ? result.TicketNumber : null,
        Channel = result.Channel,
        ItemStatus = result.ItemStatus,
        Error = result.Error,
    };

    /// <summary>Wire names are camelCase to match the rest of the API surface.</summary>
    private static string OutcomeName(QueueDispatchOutcome outcome) => outcome switch
    {
        QueueDispatchOutcome.Dispatched => "dispatched",
        QueueDispatchOutcome.NotFound => "notFound",
        QueueDispatchOutcome.NotApproved => "notApproved",
        QueueDispatchOutcome.UnsupportedPlatform => "unsupportedPlatform",
        QueueDispatchOutcome.InvalidItem => "invalidItem",
        QueueDispatchOutcome.SendFailed => "sendFailed",
        _ => throw new InvalidOperationException($"unmapped dispatch outcome: {outcome}"),
    };

    /// <summary>Keep caller-supplied ids log-safe (never log unbounded input).</summary>
    private static string Truncate(string value)
        => value.Length <= 80 ? value : value[..80] + "...";
}
