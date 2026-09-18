using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>Where the page route reads its facts. Delegates, so the tenant partition, the roster fold and the
/// settings stay in the one place that owns each of them.</summary>
/// <param name="LiveRoster">This account's live sessions (fresh pushes only), stamped by the roster fold.</param>
/// <param name="MarkedSessionId">The session the account marked as its Fleet Manager, or null.</param>
/// <param name="LastKnownSession">The last row any Director of this account reported for one session, live or not.</param>
/// <param name="LatestVerdict">The Wingman's latest stored reading of one session in this account.</param>
/// <param name="TimeZone">The account's display time zone.</param>
/// <param name="NowUtc">The clock.</param>
/// <param name="AnswerEvents">The events carrying the owner's answers to the named records to the Fleet Manager.</param>
/// <param name="SuccessorSessionId">The new Fleet Manager waiting to take over, or null.</param>
internal sealed record FleetManagerPageSources(
    Func<TenantId, IReadOnlyList<SessionDto>> LiveRoster,
    Func<TenantId, string?> MarkedSessionId,
    Func<TenantId, string, SessionDto?> LastKnownSession,
    Func<TenantId, string, TurnVerdictDto?> LatestVerdict,
    Func<TenantId, TimeZoneInfo> TimeZone,
    Func<DateTime> NowUtc,
    Func<TenantId, IReadOnlyCollection<string>, IReadOnlyDictionary<string, FleetManagerEventDto>>? AnswerEvents = null,
    Func<TenantId, string?>? SuccessorSessionId = null);

/// <summary>
/// The Fleet Manager page (the Fleet Manager mission, step 6):
///
///   GET /gateway/fleet-manager/page   the cards, the live right panel, the badge count and the quick prompts
///
/// THE OWNER'S READ, NOT A SESSION'S. The page is what the owner sees; the Fleet Manager reads the same facts from
/// its digest, which applies the shadow rule to the Wingman's readings for a session key. This route shows those
/// readings' labels unconditionally, so <see cref="SessionKeyGuard"/> refuses a session key here, and the handler
/// refuses it again.
/// </summary>
internal static class FleetManagerPageEndpoints
{
    public const string PageRoute = FleetManagerEndpoints.Prefix + "/page";

    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant,
        FleetOutcomeStore outcomes, FleetManagerPageSources sources)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(sources);

        app.MapGet(PageRoute, (HttpContext ctx) => Read(ctx, resolveTenant, outcomes, sources));
        FileLog.Write($"[FleetManagerPageEndpoints] mapped {PageRoute}");
    }

    internal static IResult Read(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        FleetOutcomeStore outcomes, FleetManagerPageSources sources)
    {
        FileLog.Write("[FleetManagerPageEndpoints] GET page");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return Results.Json(new { error = "no account is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            if (AuthMiddleware.CallingSession(ctx) is not null)
            {
                FileLog.Write("[FleetManagerPageEndpoints] REFUSED: a session key asked for the owner's page");
                return Results.Json(new { error = "The Fleet Manager page is the owner's. A session reads GET /gateway/fleet-manager/digest." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var marked = sources.MarkedSessionId(tenant);
            // Every open record, never a first page: each is a card with its buttons, and the badge and "Waiting on
            // you" must not undercount. Only the answered history is limited.
            var open = outcomes.ListOpen(tenant);
            var answered = outcomes.List(tenant, FleetOutcomeStore.StatusAnswered, kind: null, FleetManagerPageFold.CardCount);
            var inputs = new FleetManagerPageInputs(
                marked,
                string.IsNullOrWhiteSpace(marked) ? null : sources.LastKnownSession(tenant, marked.Trim()),
                sources.LiveRoster(tenant),
                open,
                answered,
                sid => sources.LatestVerdict(tenant, sid),
                sources.TimeZone(tenant),
                sources.NowUtc(),
                sources.AnswerEvents?.Invoke(tenant, answered.Select(o => o.Id).ToList()),
                sources.SuccessorSessionId?.Invoke(tenant));
            var dto = FleetManagerPageFold.Fold(inputs);

            FileLog.Write($"[FleetManagerPageEndpoints] GET page: marked={dto.FleetManagerSessionId}, cards={dto.Cards.Count}, "
                          + $"waiting={dto.Waiting.Count}, underWay={dto.UnderWay.Count}, answeredToday={dto.Landed.Count}, notMine={dto.NotMine.Count}");
            return Results.Json(dto);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerPageEndpoints] GET page FAILED: {ex.Message}");
            throw;
        }
    }
}
