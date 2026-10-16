using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Receipts;
using CcDirector.Gateway.Streaming;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// <c>GET /sessions/{sid}/receipts</c> - every stored receipt of one API-mode session (mission #4070, phase 2), served
/// from the Gateway's own store, oldest prompt first. It never travels to the Director.
///
/// It locates the session the way <c>GET /sessions/{sid}/history</c> does: the request's tenant is resolved from its
/// authenticated credential (403 when none is bound), the store is read INSIDE that tenant's scope, and the pushed
/// roster is asked in that tenant only. So another account's session id finds no receipts and no roster entry, and is
/// answered 404 exactly as an id that does not exist - the answer never says the id exists elsewhere.
///
/// A session this Gateway knows (on the roster, or with receipts) answers 200 with <c>count</c>, which is 0 for a
/// terminal-mode session or an API-mode session that has sent nothing yet.
/// </summary>
public static class SessionReceiptsEndpoint
{
    public static void Map(
        IEndpointRouteBuilder app,
        ApiModeReceiptStore receipts,
        PushedSessionStore pushedSessions,
        TimeSpan staleAfter,
        Tenancy.HostedTenantBoundary? tenantBoundary)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(receipts);
        ArgumentNullException.ThrowIfNull(pushedSessions);

        app.MapGet("/sessions/{sid}/receipts", (string sid, HttpContext ctx) =>
        {
            try
            {
                return Read(ctx, sid, receipts, pushedSessions, staleAfter, tenantBoundary);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionReceipts] receipts sid={sid} FAILED: {ex.GetType().Name}: {ex.Message}");
                return Results.Json(new { error = "the receipts could not be read" }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }

    /// <summary>The route's handler, callable without a host so its exits can be proven directly.</summary>
    internal static IResult Read(
        HttpContext ctx,
        string sid,
        ApiModeReceiptStore receipts,
        PushedSessionStore pushedSessions,
        TimeSpan staleAfter,
        Tenancy.HostedTenantBoundary? tenantBoundary)
    {
        var tenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
        if (tenant is null)
            return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
        if (!Guid.TryParse(sid, out _))
            return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);

        // THE STORE IS READ INSIDE THE CALLER'S TENANT SCOPE: its rows are partitioned by the context's ambient tenant,
        // and a read taken outside a scope would answer from whatever tenant happened to be ambient.
        IReadOnlyList<ApiModeReceiptDto> stored;
        using (tenantBoundary?.EnterScope(tenant.Value))
            stored = receipts.Read(sid);

        var known = stored.Count > 0
                    || pushedSessions.TryLocate(tenant.Value, sid, staleAfter) is not null
                    || pushedSessions.TryLocateIgnoringFreshness(tenant.Value, sid) is not null;
        if (!known)
        {
            FileLog.Write($"[SessionReceipts] receipts sid={sid} tenant={tenant.Value.ToLogString()}: no such session in this account");
            return Results.Json(new { error = "session not found" }, statusCode: StatusCodes.Status404NotFound);
        }

        // THE FINISHED WORDS, ruled here so a client never composes a receipt (CLAUDE.md rule 7). The live stream words
        // its receipt items with the same function, so a receipt reads the same on both.
        foreach (var receipt in stored)
        {
            receipt.Words = ApiModeDisplay.ApiModeLiveRuling.ReceiptWords(receipt);
            receipt.Tone = ApiModeDisplay.ApiModeLiveRuling.ReceiptTone(receipt);
        }

        return Results.Json(new SessionReceiptsDto
        {
            SessionId = sid,
            Count = stored.Count,
            Receipts = stored.ToList(),
        });
    }
}
