using System.Net;
using System.Net.Http.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Enrolls THIS Director with a Gateway using a pairing code (issue #469). This is the one-time
/// "Connect to Gateway" act: the user types the Gateway URL and the 4-digit code read off the
/// gateway host's screen, and this client POSTs <c>/devices/register</c> to obtain the unique
/// per-device key the Gateway issues. The key is then written to the local credential file the
/// Director and the local cc-* tools both read.
///
/// Separate from <see cref="GatewayClient"/> (the running registration/heartbeat lifecycle): a
/// brand-new device has no credential yet, so enrollment is a standalone request authorized by the
/// pairing code, not by a token. Pure of UI - the dialog calls this and renders the result.
/// </summary>
public static class GatewayEnrollmentClient
{
    /// <summary>
    /// Enroll with the Gateway at <paramref name="gatewayUrl"/> using <paramref name="pairingCode"/>.
    /// Returns the issued per-device key on success, or a human-readable reason on failure. Never
    /// throws for an expected failure (wrong code, unreachable Gateway); the dialog shows the reason.
    /// </summary>
    public static async Task<OperationResult<DeviceRegistrationResponse>> EnrollAsync(
        string gatewayUrl, string deviceId, string machineName, string pairingCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            return OperationResult<DeviceRegistrationResponse>.Fail("Enter the Gateway URL.");
        if (string.IsNullOrWhiteSpace(deviceId))
            return OperationResult<DeviceRegistrationResponse>.Fail("This Director has no device id.");
        if (string.IsNullOrWhiteSpace(pairingCode))
            return OperationResult<DeviceRegistrationResponse>.Fail("Enter the 4-digit pairing code.");

        FileLog.Write($"[GatewayEnrollmentClient] EnrollAsync: gateway={gatewayUrl}, deviceId={deviceId}, machine={machineName}");
        var request = new DeviceRegistrationRequest
        {
            DeviceId = deviceId,
            MachineName = machineName,
            PairingCode = pairingCode,
        };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.BaseAddress = new Uri(gatewayUrl.TrimEnd('/') + "/");
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsJsonAsync("devices/register", request, ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayEnrollmentClient] EnrollAsync transport FAILED: {ex.Message}");
            return OperationResult<DeviceRegistrationResponse>.Fail(
                $"Could not reach the Gateway at {gatewayUrl}: {ex.Message}");
        }

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            FileLog.Write($"[GatewayEnrollmentClient] EnrollAsync rejected: HTTP {(int)resp.StatusCode}");
            return OperationResult<DeviceRegistrationResponse>.Fail(
                "Pairing code is wrong, expired, or already used. Mint a new code on the gateway host and try again.");
        }
        if (!resp.IsSuccessStatusCode)
        {
            FileLog.Write($"[GatewayEnrollmentClient] EnrollAsync failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            return OperationResult<DeviceRegistrationResponse>.Fail(
                $"The Gateway refused enrollment: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }

        var body = await resp.Content.ReadFromJsonAsync<DeviceRegistrationResponse>(ct);
        if (body is null || string.IsNullOrWhiteSpace(body.DeviceKey))
        {
            FileLog.Write("[GatewayEnrollmentClient] EnrollAsync: 2xx with no device key in body");
            return OperationResult<DeviceRegistrationResponse>.Fail(
                "The Gateway accepted the code but returned no device key.");
        }

        FileLog.Write($"[GatewayEnrollmentClient] EnrollAsync: per-device key issued for machine={body.MachineName}");
        return OperationResult<DeviceRegistrationResponse>.Ok(body);
    }
}
