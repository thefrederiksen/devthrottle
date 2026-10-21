using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Factory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The one gate in front of every factory route: the activity record, the triggers (the owner's AND the Director's
/// half), and the owner's Factory Agents pages. It answers exactly what an unmapped route answers - 404 with no
/// body - for an account the area is off for, so an account that is off cannot tell the area exists, and a
/// Director of that account is handed no checks to run.
///
/// An account whose PLAN could not be read (<see cref="FactoryAgentsState.Unknown"/>) is not off: it is answered the
/// way the hosted request path answers an unreadable plan - the access lease in <c>AuthMiddleware</c> and
/// <see cref="HostedAi.HostedAiHttp.TenantAiRefusal"/> - 503 with Retry-After and code <c>entitlement_unknown</c>, so
/// a paying account is never told the area does not exist because of a database hiccup.
///
/// Every factory route is mapped INTO the group this returns, so a route added later is gated by construction
/// rather than by remembering to call a check. The switch question itself (<c>/gateway/factory-agents/switch</c>)
/// is NOT behind the gate - it is how the Cockpit learns the answer.
/// </summary>
internal static class FactoryAgentsGate
{
    /// <summary>A route group whose every endpoint refuses with 404 unless the area is on for the calling account, and
    /// answers a temporary 503 while the account's plan cannot be read.</summary>
    public static RouteGroupBuilder Group(IEndpointRouteBuilder app, FactoryAgentsSwitch factorySwitch,
        Func<HttpContext, TenantId?> resolveTenant)
    {
        ArgumentNullException.ThrowIfNull(factorySwitch);
        ArgumentNullException.ThrowIfNull(resolveTenant);

        var group = app.MapGroup(string.Empty);
        // Every endpoint in the group carries the marker, so a test can read the FINAL route table of a real host and
        // prove which factory routes are behind this gate - rather than trusting the Map calls were given the group.
        group.WithMetadata(GatedMarker.Instance);
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var ctx = invocation.HttpContext;
            // A Director's own pulls (its triggers every 30 seconds, a check download, a seat's kit) ask the same
            // decision without its log line: the same timer asking the same question is not a new decision. A route a
            // person opens is logged.
            var answer = IsDirectorHalf(ctx)
                ? factorySwitch.AnswerQuietly(resolveTenant(ctx))
                : AnswerFor(ctx, factorySwitch, resolveTenant);
            if (answer.State == FactoryAgentsState.Unknown)
            {
                FileLog.Write($"[FactoryAgentsGate] UNKNOWN {ctx.Request.Method} {ctx.Request.Path}: {answer.Why} - answering 503");
                return HostedAi.HostedAiHttp.EntitlementUnknown();
            }
            if (!answer.IsOn)
            {
                FileLog.Write($"[FactoryAgentsGate] REFUSED {ctx.Request.Method} {ctx.Request.Path}: {answer.OffReason}: {answer.Why} - answering 404");
                return Results.NotFound();
            }
            return await next(invocation);
        });
        return group;
    }

    /// <summary>The metadata every endpoint behind this gate carries.</summary>
    internal sealed class GatedMarker
    {
        public static readonly GatedMarker Instance = new();
        private GatedMarker() { }
    }

    /// <summary>Whether this request is a Director's own machine pull rather than a person's: every
    /// <c>/directors/{id}/...</c> route (the trigger pull, the check report, the check download) and a seat's kit.</summary>
    internal static bool IsDirectorHalf(HttpContext ctx)
        => ctx.Request.Path.StartsWithSegments("/directors", StringComparison.OrdinalIgnoreCase)
           || (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText == FactoryKitEndpoints.Route;

    /// <summary>The switch's one decision for the account this request is bound to. Internal so it is tested directly,
    /// and so the routes mapped outside the group (talk, the owner's actions) ask exactly what the group asks.</summary>
    internal static FactoryAgentsAnswer AnswerFor(HttpContext ctx, FactoryAgentsSwitch factorySwitch,
        Func<HttpContext, TenantId?> resolveTenant)
        => factorySwitch.Answer(resolveTenant(ctx));

    /// <summary>
    /// The sentence a route mapped OUTSIDE the group (talk, the owner's actions, the Factory Manager's talk) carries on
    /// its 404 when the area is off, ending in what was not done. It names the plan when the plan is why; otherwise it
    /// is the sentence those routes have always said.
    /// </summary>
    internal static string OffSentence(FactoryAgentsAnswer answer, string whatWasNotDone)
        => answer.OffReason == FactoryAgentsOffReasons.PlanRequired
            ? $"Factories come with the Pro and Teams plans, and this account's plan does not include them, so {whatWasNotDone}."
            : $"Factory agents are switched off for this account, so {whatWasNotDone}. The administrator switches them on per account.";
}
