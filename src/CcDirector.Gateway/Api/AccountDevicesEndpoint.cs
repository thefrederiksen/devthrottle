using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The account device-list proxy (issue #854): <c>GET /account/devices</c> and
/// <c>DELETE /account/devices/{id}</c>. The Cockpit Account page needs the account-wide device list with
/// last-seen and a per-device revoke, but the Cockpit must NEVER hold the account token or call the cloud
/// directly - the token lives on the Gateway. So the Gateway proxies: it reads its own stored account
/// token (<see cref="DevThrottleAccountService.GetAccessTokenForForwarding"/>, the SAME egress credential
/// it already uses to forward telemetry/login to the cloud), calls the cloud device registry
/// (<see cref="DeviceRegistryClient"/>, cloud contract devthrottle_internal#81/#82), and returns a local,
/// token-free DTO.
///
/// This is distinct from the LOCAL pairing registry <c>GET /devices</c> (issue #469): that lists the
/// machines paired to THIS Gateway; this lists the devices registered to the DevThrottle ACCOUNT across
/// the cloud. Both surfaces coexist.
///
/// Security (carries DT-05): the raw account token NEVER appears in the Cockpit-facing response (the DTOs
/// have no token field) and is never written to the log on any path.
///
/// Behaviour at the edges (no fabricated data):
/// <list type="bullet">
/// <item>Signed out / no credential -> an explicit <c>signedIn:false</c> envelope, never a fabricated
/// empty 200 device list.</item>
/// <item>Cloud unreachable / erroring -> a clear 502 error (logged), never a fabricated list or a silent
/// success.</item>
/// </list>
///
/// When Gateway auth is enabled, both routes inherit the host-wide Gateway token middleware exactly like
/// the other <c>/account</c> endpoints (they are not on the public-paths allow-list), so a call with no
/// Gateway token is answered 401 by that middleware before these delegates run.
/// </summary>
internal static class AccountDevicesEndpoint
{
    /// <summary>
    /// Maps <c>GET /account/devices</c> and <c>DELETE /account/devices/{id}</c>.
    /// </summary>
    /// <param name="app">The route builder.</param>
    /// <param name="account">
    /// The Gateway-hosted DevThrottle credential service (issue #636). Null on a host that has no
    /// credential service (a non-Windows host); the endpoints then report an explicit signed-out result.
    /// </param>
    /// <param name="devices">The cloud device-registry client (the injectable cloud egress seam).</param>
    /// <param name="thisDeviceName">
    /// This host's machine name, used to mark the Gateway's own device in the list. Injected for tests.
    /// </param>
    public static void Map(IEndpointRouteBuilder app, DevThrottleAccountService? account, DeviceRegistryClient devices, string thisDeviceName)
    {
        if (devices is null) throw new ArgumentNullException(nameof(devices));
        if (thisDeviceName is null) throw new ArgumentNullException(nameof(thisDeviceName));

        app.MapGet("/account/devices", async (HttpContext ctx) =>
        {
            // Entry point: the delegate is the boundary, so the only try-catch lives here. A signed-out
            // Gateway is an expected state answered explicitly (not an exception); a cloud failure is an
            // unexpected failure caught here and reported as a clear error (never a fabricated list).
            var token = account?.GetAccessTokenForForwarding();
            if (string.IsNullOrEmpty(token))
            {
                FileLog.Write("[AccountDevicesEndpoint] GET /account/devices: no account credential -> signedIn=false (explicit, not an empty list)");
                return Results.Json(new AccountDevicesResponseDto { SignedIn = false });
            }

            try
            {
                var records = await devices.ListDevicesAsync(token, ctx.RequestAborted).ConfigureAwait(false);
                var list = new List<AccountDeviceDto>(records.Count);
                foreach (var record in records)
                    list.Add(ToDto(record, thisDeviceName));

                FileLog.Write($"[AccountDevicesEndpoint] GET /account/devices: signedIn=true, returned {list.Count} device(s)");
                return Results.Json(new AccountDevicesResponseDto { SignedIn = true, Devices = list });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[AccountDevicesEndpoint] GET /account/devices FAILED: {ex.Message}");
                return Results.Json(
                    new { error = "Could not reach the DevThrottle account service to list devices. Try again shortly." },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapDelete("/account/devices/{id}", async (string id, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(id))
                return Results.BadRequest(new { error = "id is required" });

            // Entry point: same boundary rule as the GET above.
            var token = account?.GetAccessTokenForForwarding();
            if (string.IsNullOrEmpty(token))
            {
                FileLog.Write($"[AccountDevicesEndpoint] DELETE /account/devices/{id}: no account credential -> signedIn=false (explicit, no revoke performed)");
                return Results.Json(new RevokeDeviceResponseDto { SignedIn = false, Id = id, Revoked = false });
            }

            try
            {
                var revoked = await devices.RevokeDeviceAsync(token, id, ctx.RequestAborted).ConfigureAwait(false);
                if (!revoked)
                {
                    FileLog.Write($"[AccountDevicesEndpoint] DELETE /account/devices/{id}: not found for this account -> 404");
                    return Results.Json(
                        new RevokeDeviceResponseDto { SignedIn = true, Id = id, Revoked = false },
                        statusCode: StatusCodes.Status404NotFound);
                }

                FileLog.Write($"[AccountDevicesEndpoint] DELETE /account/devices/{id}: revoked");
                return Results.Json(new RevokeDeviceResponseDto { SignedIn = true, Id = id, Revoked = true });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[AccountDevicesEndpoint] DELETE /account/devices/{id} FAILED: {ex.Message}");
                return Results.Json(
                    new { error = "Could not reach the DevThrottle account service to revoke the device. Try again shortly." },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }

    /// <summary>
    /// Maps a masked cloud record to the Cockpit-facing DTO and computes the this-device marker by matching
    /// the record name to this host's machine name (case-insensitive). The match on machine name is the
    /// available signal until device self-registration (a sibling issue) stamps a stronger identity.
    /// </summary>
    private static AccountDeviceDto ToDto(CloudDeviceRecord record, string thisDeviceName)
    {
        return new AccountDeviceDto
        {
            Id = record.Id,
            Name = record.Name,
            Platform = record.Platform,
            DeviceType = record.DeviceType,
            AppVersion = record.AppVersion,
            KeyPrefix = record.KeyPrefix,
            KeyLast4 = record.KeyLast4,
            CreatedAt = record.CreatedAt,
            LastSeenAt = record.LastSeenAt,
            ThisDevice = string.Equals(record.Name, thisDeviceName, StringComparison.OrdinalIgnoreCase),
        };
    }
}
