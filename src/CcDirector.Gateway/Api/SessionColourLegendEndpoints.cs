using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// <c>GET /gateway/session-colours</c> - what every session colour means, for the "What do the colours mean?" legend
/// the Cockpit and the phone render verbatim. The words are <see cref="SessionColourLegend"/>, written beside the
/// fold they explain.
///
/// It reads no tenant's data: the legend is the same for every account, like <c>/gateway/about</c>. It sits behind the
/// ordinary credential gate all the same, because it is part of the signed-in product, not a public page. It takes the
/// <see cref="HttpContext"/> only to log who asked, so it is not a context-less route and needs no census row.
/// </summary>
internal static class SessionColourLegendEndpoints
{
    /// <summary>Map the route.</summary>
    /// <param name="app">The route builder.</param>
    public static void Map(IEndpointRouteBuilder app)
    {
        FileLog.Write($"[SessionColourLegendEndpoints] mapping GET {SessionColourLegend.Route} - what every session colour means");

        app.MapGet(SessionColourLegend.Route, (HttpContext ctx) =>
        {
            FileLog.Write($"[SessionColourLegendEndpoints] GET {SessionColourLegend.Route}: identity={AuthMiddleware.IdentityKind(ctx)}");
            return Results.Json(SessionColourLegend.Build());
        });
    }
}
