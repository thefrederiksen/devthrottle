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
/// it already uses for account operations), calls the cloud device registry
/// (<see cref="DeviceRegistryClient"/>, the cloud device registry), and returns a local,
/// token-free DTO.
///
/// This is distinct from the LOCAL pairing registry <c>GET /devices</c> (issue #469): that lists the
/// machines paired to THIS Gateway; this lists the devices registered to the DevThrottle ACCOUNT across
/// the cloud. Both surfaces coexist.
///
/// HOSTED (issue #1856 pattern): the self-host cloud proxy above is meaningless on the shared multi-tenant
/// Gateway, which holds NO single account credential - identity arrives per device, bound to the device key
/// at enrollment. On hosted the two routes answer about the CALLER: they resolve the request's tenant from
/// its authenticated device key and serve that tenant's own devices from the local device registry (the
/// database authority since the #2020 cutover), returning 403 when no tenant is bound - NEVER the Local
/// partition, and NEVER a signedIn=false envelope to an authenticated tenant. This mirrors exactly what
/// <c>/account/status</c> and the tenant-scoped <c>/devices</c> listing already do. Gated on the required
/// <c>hosted</c> argument (read once at startup into <c>GatewayHostOptions</c>; not on the boundary having been
/// wired) so a hosted Gateway can never silently fall through to the self-host answer; a missing boundary
/// FAILS CLOSED with a 503.
///
/// Security (carries DT-05): the raw account token NEVER appears in the Cockpit-facing response (the DTOs
/// have no token field) and is never written to the log on any path. On the hosted path no account token
/// exists and the device rows carry only masked key metadata (prefix/last-four), never a raw key.
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
    /// <param name="devices">The cloud device-registry client (the injectable cloud egress seam, self-host).</param>
    /// <param name="thisDeviceName">
    /// This host's machine name, used to mark the Gateway's own device in the list. Injected for tests.
    /// </param>
    /// <param name="localDevices">
    /// The local device registry (the database device-credential authority). On hosted this is the source of
    /// the caller tenant's device list and the target of the per-tenant revoke; unused on the self-host path.
    /// </param>
    /// <param name="tenantBoundary">
    /// The hosted tenant boundary (issue #1856). On hosted it resolves the CALLER's tenant from their
    /// authenticated device key; omitting it on a hosted Gateway does NOT fall back to the self-host answer -
    /// the hosted path FAILS CLOSED with a 503. Ignored off hosted mode.
    /// </param>
    public static void Map(IEndpointRouteBuilder app, bool hosted, DevThrottleAccountService? account, DeviceRegistryClient devices, string thisDeviceName,
        // REQUIRED AND NON-NULLABLE (finding I1-01): a forgotten boundary must be a compile error, never a
        // silent default. Self-host callers construct it over the SingleTenantContext.
        Pairing.DeviceRegistry localDevices, Tenancy.HostedTenantBoundary tenantBoundary)
    {
        if (devices is null) throw new ArgumentNullException(nameof(devices));
        if (thisDeviceName is null) throw new ArgumentNullException(nameof(thisDeviceName));
        if (localDevices is null) throw new ArgumentNullException(nameof(localDevices));

        app.MapGet("/account/devices", async (HttpContext ctx) =>
        {
            // Issue #1856: on HOSTED, answer about the CALLER, not about this Gateway's (absent) account
            // credential. Gated on hosted MODE, not on the boundary being wired, so a hosted Gateway can never
            // silently take the self-host path and report a false signedIn=false; a missing boundary fails
            // closed inside HostedDevices.
            if (hosted)
                return HostedDevices(ctx, tenantBoundary, localDevices, thisDeviceName);

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

            // Issue #1856: on HOSTED, revoke only the caller tenant's OWN device from the local registry. Same
            // hosted-mode gate and fail-closed rule as the GET.
            if (hosted)
                return HostedRevoke(ctx, id, tenantBoundary, localDevices);

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
    /// Hosted <c>GET /account/devices</c>: serve the CALLER tenant's own devices from the local registry.
    /// Fails closed (503) if the boundary is missing on a hosted Gateway, and denies (403) a request with no
    /// bound tenant - never the Local partition, and never a signedIn=false envelope to an authenticated
    /// tenant (that false was the exact lie that made the Account page contradict itself).
    /// </summary>
    private static IResult HostedDevices(HttpContext ctx, Tenancy.HostedTenantBoundary? boundary, Pairing.DeviceRegistry localDevices, string thisDeviceName)
    {
        if (boundary is not { IsHosted: true })
        {
            FileLog.Write("[AccountDevicesEndpoint] GET /account/devices (hosted): MISWIRED - hosted mode but no hosted tenant boundary; refusing rather than reporting a false signed-out state.");
            return Results.Json(new { error = "this hosted gateway cannot resolve the caller's tenant" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var tenant = boundary.ResolveRequestTenant(ctx);
        if (tenant is null)
        {
            FileLog.Write("[AccountDevicesEndpoint] GET /account/devices (hosted): DENIED - no tenant is bound to this request");
            return Results.Json(new { error = "no tenant is bound to this request" },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var records = localDevices.ListForTenant(tenant.Value);
        var list = new List<AccountDeviceDto>(records.Count);
        foreach (var record in records)
            list.Add(ToDto(record, thisDeviceName));

        FileLog.Write($"[AccountDevicesEndpoint] GET /account/devices (hosted): signedIn=true, returned {list.Count} device(s) for the caller's tenant");
        return Results.Json(new AccountDevicesResponseDto { SignedIn = true, Devices = list });
    }

    /// <summary>
    /// Hosted <c>DELETE /account/devices/{id}</c>: revoke a device only when it belongs to the CALLER's own
    /// tenant. A device id that is not the caller tenant's is a 404 (indistinguishable from a non-existent id,
    /// so one tenant cannot probe another's device ids), never a cross-tenant revoke.
    /// </summary>
    private static IResult HostedRevoke(HttpContext ctx, string id, Tenancy.HostedTenantBoundary? boundary, Pairing.DeviceRegistry localDevices)
    {
        if (boundary is not { IsHosted: true })
        {
            FileLog.Write($"[AccountDevicesEndpoint] DELETE /account/devices/{id} (hosted): MISWIRED - hosted mode but no hosted tenant boundary; refusing.");
            return Results.Json(new { error = "this hosted gateway cannot resolve the caller's tenant" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var tenant = boundary.ResolveRequestTenant(ctx);
        if (tenant is null)
        {
            FileLog.Write($"[AccountDevicesEndpoint] DELETE /account/devices/{id} (hosted): DENIED - no tenant is bound to this request");
            return Results.Json(new { error = "no tenant is bound to this request" },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var revoked = localDevices.RemoveForTenant(tenant.Value, id);
        if (!revoked)
        {
            FileLog.Write($"[AccountDevicesEndpoint] DELETE /account/devices/{id} (hosted): not the caller tenant's device -> 404");
            return Results.Json(new RevokeDeviceResponseDto { SignedIn = true, Id = id, Revoked = false },
                statusCode: StatusCodes.Status404NotFound);
        }

        FileLog.Write($"[AccountDevicesEndpoint] DELETE /account/devices/{id} (hosted): revoked for the caller's tenant");
        return Results.Json(new RevokeDeviceResponseDto { SignedIn = true, Id = id, Revoked = true });
    }

    /// <summary>
    /// Maps a local registry record to the Cockpit-facing DTO for the hosted path. The registry carries the
    /// device id, machine name, issued time and masked key metadata; platform / device-type / app-version /
    /// last-seen are not recorded there and are OMITTED (null) rather than guessed. The this-device marker
    /// matches the record's machine name to this host's machine name (case-insensitive), same as the cloud map.
    /// </summary>
    private static AccountDeviceDto ToDto(RegisteredDeviceDto record, string thisDeviceName)
    {
        return new AccountDeviceDto
        {
            Id = record.DeviceId,
            Name = record.MachineName,
            Platform = null,
            DeviceType = null,
            AppVersion = null,
            KeyPrefix = string.IsNullOrEmpty(record.KeyPrefix) ? null : record.KeyPrefix,
            KeyLast4 = string.IsNullOrEmpty(record.KeyLast4) ? null : record.KeyLast4,
            CreatedAt = record.IssuedAtUtc.ToString("o"),
            LastSeenAt = null,
            ThisDevice = string.Equals(record.MachineName, thisDeviceName, StringComparison.OrdinalIgnoreCase),
        };
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
