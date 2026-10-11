using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Transcription;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Transcription Health over minimized, bounded local history.
///
///   GET    /transcription/stats
///   GET    /transcription/turns
///   GET    /transcription/terms
///   DELETE /transcription/history
///
/// The history never includes transcript text or provider error bodies, is retained for 30 days, and
/// can be cleared by the owner. It SERVES PER-TENANT on hosted (issue #2059): the store is partitioned by
/// tenant (see <see cref="TranscriptionHistoryLog.DirectoryFor"/>), so each route resolves the caller's
/// tenant and reads only that tenant's history; a request with no resolvable tenant is refused (403), never
/// served the Local partition. Self-host resolves to the single Local tenant and reads the existing flat
/// store, unchanged.
/// </summary>
internal static class TranscriptionAnalysisEndpoint
{
    private const int DefaultTurnLimit = 100;
    private const int MaxTurnLimit = 2000;
    private const int DefaultTermTop = 25;

    internal const string Prefix = "/transcription";

    /// <summary>The 403 a route answers when the caller's tenant cannot be resolved (issue #2059). On the
    /// hosted Gateway an authenticated request whose device key has no bound tenant is refused, NEVER served
    /// the Local partition. Self-host always resolves to Local, so this never fires there.</summary>
    private static IResult TenantRequired()
        => Results.Json(new { error = "a tenant could not be resolved for this request" },
            statusCode: StatusCodes.Status403Forbidden);

    public static void Map(
        IEndpointRouteBuilder outer,
        bool hosted,
        // REQUIRED AND NON-NULLABLE (finding I1-01): a forgotten boundary must be a compile error, never a
        // silent default. Self-host callers construct it over the SingleTenantContext.
        Tenancy.HostedTenantBoundary tenantBoundary,
        TranscriptionHistoryReader? reader = null,
        TranscriptionAudioArchive? audioArchive = null)
    {
        FileLog.Write($"[TranscriptionAnalysisEndpoint] mapping {Prefix} history PER-TENANT (issue #2059); hosted={hosted} - each route resolves the caller's tenant, 403 when unresolved");
        var app = outer.MapGroup(Prefix);
        MapRoutes(app, hosted, tenantBoundary, reader, audioArchive);
    }

    // The reader/audio for the CALLER's tenant. A test-supplied reader (self-host fixtures) is used verbatim;
    // otherwise a reader/archive over the tenant's own partition is built.
    private static TranscriptionHistoryReader ReaderFor(TenantId tenant, TranscriptionHistoryReader? overrideReader)
        => overrideReader ?? new TranscriptionHistoryReader(TranscriptionHistoryLog.DirectoryFor(tenant));

    private static TranscriptionAudioArchive AudioFor(bool hosted, TenantId tenant, TranscriptionAudioArchive? overrideArchive)
        => overrideArchive ?? new TranscriptionAudioArchive(hosted, TranscriptionAudioArchive.DirectoryFor(tenant));

    private static void MapRoutes(
        IEndpointRouteBuilder app,
        bool hosted,
        Tenancy.HostedTenantBoundary tenantBoundary,
        TranscriptionHistoryReader? reader,
        TranscriptionAudioArchive? audioArchive)
    {
        app.MapGet("/stats", (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            return Results.Json(ReaderFor(t.Value, reader).ComputeStats(ResolveSince(ctx)));
        });

        app.MapGet("/turns", (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            var limit = ClampInt(ctx.Request.Query["limit"], DefaultTurnLimit, 0, MaxTurnLimit);
            return Results.Json(new { turns = ReaderFor(t.Value, reader).Load(ResolveSince(ctx), limit) });
        });

        app.MapGet("/terms", (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            var top = ClampInt(ctx.Request.Query["top"], DefaultTermTop, 1, 1000);
            return Results.Json(new { terms = ReaderFor(t.Value, reader).TopCorrections(top, ResolveSince(ctx)) });
        });

        app.MapDelete("/history", (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            return Results.Json(new
            {
                removedFiles = ReaderFor(t.Value, reader).Clear(),
                removedAudioClips = AudioFor(hosted, t.Value, audioArchive).Clear(),
            });
        });
    }

    private static DateTime? ResolveSince(HttpContext ctx)
    {
        var daysRaw = ctx.Request.Query["days"].ToString();
        if (!string.IsNullOrWhiteSpace(daysRaw) && double.TryParse(daysRaw, out var days) && days > 0)
            return DateTime.UtcNow.AddDays(-days);

        var sinceRaw = ctx.Request.Query["since"].ToString();
        if (!string.IsNullOrWhiteSpace(sinceRaw)
            && DateTime.TryParse(sinceRaw, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal |
                System.Globalization.DateTimeStyles.AssumeUniversal, out var since))
            return since;
        return null;
    }

    private static int ClampInt(string? raw, int fallback, int min, int max)
    {
        if (!int.TryParse(raw, out var value)) value = fallback;
        return Math.Clamp(value, min, max);
    }
}
