using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// PAUSE EVERYTHING (issue #3910): the account's one switch that stops the scheduler firing.
///
///   GET /cron/pause                       -> CronPauseDto
///   PUT /cron/pause  body { paused: bool } -> CronPauseDto | 400 | 403
///
/// A PERSON'S CONTROL ONLY. A session may read the switch - it explains why nothing is firing - but may never set
/// it: an agent that could resume a paused fleet would undo the one thing the owner reaches for when something is
/// wrong. The write is refused for a session key here, and it is also left out of <see cref="SessionKeyGuard"/>.
///
/// RESUMING moves every switched-on repeating schedule whose run was missed during the pause on to its next run,
/// the same way a Gateway restart does, BEFORE the switch is cleared - so resuming never starts a burst of late
/// sessions. No schedule's enabled flag is read for writing or written by either direction.
/// </summary>
internal static class CronPauseEndpoint
{
    public const string Route = "/cron/pause";

    public const string SessionRefusal =
        "Pausing or resuming every schedule is the owner's control: a session may read it and may not set it.";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <param name="resolveTenant">The calling account, or null when the caller has none.</param>
    public static void Map(IEndpointRouteBuilder app, CronPauseSwitch pause, CronJobStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(pause);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(nowUtc);

        app.MapGet(Route, (HttpContext ctx) =>
        {
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount("GET");
            return Results.Json(pause.Describe(tenant));
        });

        app.MapPut(Route, async (HttpContext ctx) =>
        {
            if (ctx.Items.TryGetValue(AuthMiddleware.AuthenticatedSessionItemKey, out var si)
                && si is Pairing.SessionCredentialIdentity caller)
            {
                FileLog.Write($"[CronPauseEndpoint] PUT {Route}: REFUSED - session {caller.SessionId} may not set the switch");
                return Results.Json(new { error = SessionRefusal }, statusCode: StatusCodes.Status403Forbidden);
            }
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount("PUT");

            CronPauseRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<CronPauseRequest>(ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[CronPauseEndpoint] PUT {Route} bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
            if (body?.Paused is not { } paused)
                return Results.BadRequest(new { error = "paused is required: true to pause everything, false to resume" });

            var now = nowUtc();
            if (paused)
            {
                pause.Pause(tenant, now);
            }
            else if (pause.IsPaused(tenant))
            {
                // Before the switch clears, so the sweep can never find a missed run with the switch already off.
                var moved = store.MoveMissedRunsOn(tenant, now);
                pause.Resume(tenant);
                FileLog.Write($"[CronPauseEndpoint] PUT {Route}: resumed, {moved} missed run(s) moved on to their next run");
            }
            FileLog.Write($"[CronPauseEndpoint] PUT {Route}: account={tenant.ToLogString()} paused={paused}");
            return Results.Json(pause.Describe(tenant));
        });

        FileLog.Write($"[CronPauseEndpoint] mapped {Route}");
    }

    private static IResult NoAccount(string verb)
    {
        FileLog.Write($"[CronPauseEndpoint] {verb} {Route}: REFUSED - no account for the caller");
        return Results.Json(new { error = "no account for this caller" }, statusCode: StatusCodes.Status403Forbidden);
    }
}
