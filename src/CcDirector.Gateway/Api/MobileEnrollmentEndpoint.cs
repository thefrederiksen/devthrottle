using System;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Browser device enrollment (issue #908 for the phone, generalized for the desktop Cockpit in issue
/// #1088): <c>POST /mobile/enroll</c>. The ONE enrollment seam every browser shell uses - the mobile PWA
/// and the desktop Cockpit both exchange their cloud device key here. The seam canonicalized from
/// <c>/m/enroll</c> to <c>/mobile/enroll</c> with the app's <c>/m</c>-&gt;<c>/mobile</c> re-base; the
/// endpoint is ALSO mapped at the old <c>/m/enroll</c> (same handler) so an installed phone PWA still
/// running the previous bundle keeps enrolling - the POST analog of the Gateway's 301 for GET
/// navigations, which cannot preserve a POST body. The device signs in on
/// devthrottle.com, is registered as a device on the account, and receives its per-device key; it POSTs
/// that key here, and the Gateway confirms (account-scoped) the key belongs to its OWN signed-in
/// account and issues the device a LOCAL device key it validates offline. The account session is never
/// sent here - only the per-device key. The platform field decides the recorded device type
/// (android/ios -> phone, "browser" -> browser; see MobileDeviceEnrollmentService.DeviceTypeForPlatform).
///
/// The route lives under <c>/m/</c>, which the auth middleware lets through without the host-wide token
/// (the sign-in screens must load before the device has any credential). Like
/// <c>/devices/enroll-signed-in</c>,
/// it carries its OWN authorization - the account-scoped device key - so opening the route does not
/// weaken the trust model: a call with no valid device key on this account is answered 403 with no key
/// issued.
///
/// The device key is never written to the log (security rule DT-05); only the device id and platform are.
/// This endpoint delegate is the boundary, so it owns the single try/catch: a cloud failure during the
/// verify becomes a clear 502, never a fabricated success.
/// </summary>
internal static class MobileEnrollmentEndpoint
{
    /// <param name="hosted">The owning host's deployment signal (<see cref="GatewayHost.Hosted"/>). REQUIRED - see
    /// <see cref="GatewayHostOptions"/>.</param>
    public static void Map(IEndpointRouteBuilder app, bool hosted, MobileDeviceEnrollmentService service, HostedEnrollDependencies? hostedDependencies = null)
    {
        if (service is null) throw new ArgumentNullException(nameof(service));

        // FAIL CLOSED, at startup, on a miswired hosted host. The hosted-vs-self-host path is decided by the
        // INDEPENDENT hosted-mode value (the required `hosted` parameter, read once at startup into
        // GatewayHostOptions), never by
        // whether this optional argument was passed - deciding on arg-presence FAILS OPEN, silently routing a
        // hosted deployment through the self-host device-key-in-body path on a one-word omission. So a hosted
        // Gateway mapped without its mint dependencies refuses to START rather than degrade unseen.
        if (hosted && hostedDependencies is null)
            throw new InvalidOperationException(
                "This Gateway is in hosted mode but /mobile/enroll was mapped without hosted enrollment dependencies. Refusing to start rather than fall through to the self-host device-key-in-body path.");

        // The one handler, mapped at the canonical /mobile/enroll AND the legacy /m/enroll (back-compat).
        Func<MobileEnrollmentRequest?, HttpContext, Task<IResult>> handler = async (req, ctx) =>
        {
            try
            {
                FileLog.Write($"[MobileEnrollment] POST {ctx.Request.Path}: deviceId={req?.DeviceId}, platform={req?.Platform} (device key not logged)");

                // HOSTED (decided by the INDEPENDENT hosted-mode signal, not by the argument): this is a HUMAN
                // account sign-in, not a cloud-device-key exchange. The account access token rides in the
                // Authorization: Bearer header (a public pre-auth route, so AuthMiddleware does not pre-validate
                // it as a device key - this endpoint reads it as the account token). It is turned into a
                // tenant-scoped device key by the ONE hosted mint - the same single mint path the hosted Cockpit
                // callback and a hosted Director use. hosted is non-null here by the map-time fail-closed guard.
                // The self-host device-key-in-body path below is untouched.
                if (hosted)
                    return CompleteHostedEnroll(ctx, req, hostedDependencies!);

                var outcome = await service
                    .EnrollAsync(req?.DeviceKey, req?.DeviceId, req?.Name, req?.Platform, ctx.RequestAborted)
                    .ConfigureAwait(false);

                // On success, ALSO set the issued local device key as the cc-gateway-token cookie
                // SERVER-side, with the same options POST /login uses. The client mirrors the key into
                // this cookie via document.cookie for the terminal WebSocket, but a JavaScript write is
                // silently REFUSED when a stale HttpOnly cookie of the same name already exists (for
                // example from a previous raw-token /login) - the stale credential then rides every
                // document navigation and WebSocket handshake while the Bearer calls work, stranding
                // the browser half-authenticated. A Set-Cookie response header replaces the old cookie
                // atomically regardless of its HttpOnly flag (found live during issue #1088 proving).
                if (outcome.Kind == MobileEnrollmentOutcome.ResultKind.Ok
                    && !string.IsNullOrEmpty(outcome.LocalDeviceKey))
                {
                    GatewayTokenCookie.Set(ctx, outcome.LocalDeviceKey, hosted);
                }

                return outcome.Kind switch
                {
                    MobileEnrollmentOutcome.ResultKind.Ok =>
                        Results.Json(new MobileEnrollmentResponse { DeviceKey = outcome.LocalDeviceKey ?? "" }),
                    MobileEnrollmentOutcome.ResultKind.BadRequest =>
                        Results.BadRequest(new { error = outcome.Message }),
                    MobileEnrollmentOutcome.ResultKind.NotSignedIn =>
                        Results.Json(new { error = outcome.Message }, statusCode: StatusCodes.Status409Conflict),
                    MobileEnrollmentOutcome.ResultKind.Rejected =>
                        Results.Json(new { error = outcome.Message }, statusCode: StatusCodes.Status403Forbidden),
                    _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
                };
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MobileEnrollment] POST {ctx.Request.Path} FAILED: {ex.Message}");
                return Results.Json(
                    new { error = "Could not reach the DevThrottle account service to enroll this device. Try again shortly." },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        };

        app.MapPost("/mobile/enroll", handler);
        app.MapPost("/m/enroll", handler);
    }

    /// <summary>
    /// Completes a HOSTED human sign-in on <c>/mobile/enroll</c>: read the account access token from the
    /// Authorization Bearer header, run the ONE hosted mint (<see cref="HostedEnrollmentEndpoint.Enroll"/>) with
    /// the request's device id, and on a successful mint set the tenant-scoped device key as the session cookie
    /// and return it. This method does NOT validate the token, gate on entitlement, or mint a tenant/key itself:
    /// every decision - a missing/forged/expired/wrong-audience token (401), an unentitled account (402), an
    /// unknown entitlement read (503) - is inherited from the single <c>Enroll</c> call, which sets NO cookie
    /// and mints NOTHING on any of them. The account token is never logged (security rule DT-05).
    /// </summary>
    private static IResult CompleteHostedEnroll(HttpContext ctx, MobileEnrollmentRequest? req, HostedEnrollDependencies hosted)
    {
        // The browser device id flows INTO Enroll so Enroll namespaces it with the resolved tenant hash (two
        // accounts presenting the same device id cannot collide). We do NOT hash or scope it here.
        var enrollReq = new EnrollSignedInRequest
        {
            DeviceId = req?.DeviceId ?? "",
            MachineName = req?.Name ?? "",
            Platform = req?.Platform ?? "",
            DeviceType = MobileDeviceEnrollmentService.DeviceTypeForPlatform(req?.Platform),
        };

        var result = HostedEnrollmentEndpoint.Enroll(BearerToken.Read(ctx), enrollReq, hosted.Devices,
            hosted.Tenants, hosted.AccountTokenValidator, hosted.Entitlements, DateTime.UtcNow, hosted.Trials);

        if (result.Status != StatusCodes.Status200OK || result.Response is null)
        {
            FileLog.Write($"[MobileEnrollment] POST /mobile/enroll (hosted): the hosted mint did not enroll -> {result.Status} (no cookie set)");

            // A payment refusal says what to do about it (issue #2117), with the SAME sentence and address the
            // Director enrollment route returns - one wording, owned by the Gateway, on every surface.
            if (result.Status == StatusCodes.Status402PaymentRequired)
            {
                return Results.Json(new
                {
                    error = result.Error,
                    message = Tenancy.EntitlementRegistry.SubscribeMessage,
                    subscribeUrl = Tenancy.EntitlementRegistry.SubscribeUrl,
                }, statusCode: result.Status);
            }

            return Results.Json(new { error = result.Error }, statusCode: result.Status);
        }

        // The one credential the browser keeps is the tenant-scoped device key, set here through the single
        // cookie helper - the same cookie the self-host path sets, so both surfaces are set exactly one way.
        GatewayTokenCookie.Set(ctx, result.Response.DeviceKey, hosted: true);
        FileLog.Write("[MobileEnrollment] POST /mobile/enroll (hosted): signed in - minted a tenant-scoped device key and set the session cookie (account token not logged)");
        return Results.Json(new MobileEnrollmentResponse { DeviceKey = result.Response.DeviceKey });
    }
}
