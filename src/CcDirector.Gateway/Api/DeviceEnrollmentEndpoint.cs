using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Device enrollment via local pairing code (issue #469). This is the ONLY way a new device
/// obtains access to the Gateway in the per-device-key trust model.
///
///   POST /devices/register  -> verify the 4-digit pairing code (matches, not expired, not used),
///                              issue a unique per-device key, record the device, consume the code.
///                              4xx when the code is wrong, expired, or already used.
///   GET  /devices           -> the host-readable registry listing (id, machine, issued-at,
///                              status). The per-device key is NEVER returned.
///
/// The register route is deliberately NOT gated by the shared fleet token: a brand-new device has
/// no credential yet, so the pairing code IS the authorization. This is what closes the old path -
/// presenting only the legacy shared token (without a valid pairing code) no longer enrolls a new
/// device. The code is shown only on the Gateway host's local window (Anchor B), so the grant is
/// rooted in local presence, not in any network secret.
/// </summary>
internal static class DeviceEnrollmentEndpoint
{
    public static void Map(IEndpointRouteBuilder app, PairingCodeService pairing, DeviceRegistry devices)
    {
        if (pairing is null) throw new ArgumentNullException(nameof(pairing));
        if (devices is null) throw new ArgumentNullException(nameof(devices));

        app.MapPost("/devices/register", (DeviceRegistrationRequest req) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.DeviceId))
                return Results.BadRequest(new { error = "deviceId is required" });
            if (string.IsNullOrWhiteSpace(req.PairingCode))
                return Results.BadRequest(new { error = "pairingCode is required" });

            FileLog.Write($"[DeviceEnrollment] POST /devices/register: deviceId={req.DeviceId}, machine={req.MachineName}");

            // Verify-and-consume is atomic: a valid code is burned here so it can never be reused
            // (single-use), and a wrong/expired/already-used code is rejected with NO key issued.
            if (!pairing.TryVerifyAndConsume(req.PairingCode))
            {
                FileLog.Write($"[DeviceEnrollment] POST /devices/register REJECTED: invalid/expired/used pairing code for deviceId={req.DeviceId}");
                return Results.Json(
                    new { error = "Pairing code is wrong, expired, or already used. Mint a new code on the gateway host and try again." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var response = devices.Register(req.DeviceId, req.MachineName);
            FileLog.Write($"[DeviceEnrollment] POST /devices/register: issued per-device key for deviceId={req.DeviceId}, machine={req.MachineName}, deviceCount={response.DeviceCount}");
            return Results.Json(response, statusCode: StatusCodes.Status201Created);
        });

        app.MapGet("/devices", () =>
        {
            var list = devices.List();
            return Results.Json(list);
        });
    }
}
