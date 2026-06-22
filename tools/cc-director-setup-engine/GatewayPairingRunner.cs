using System.Net;
using System.Net.Http.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Setup.Engine;

/// <summary>
/// The installer-time gateway pairing gate for a Workstation install (issue #646). When a machine
/// joins an existing fleet as a Workstation it MUST pair to a gateway before the install can finish:
/// the gateway is the account authority, so a Workstation with no gateway connection is useless.
///
/// This runner reuses the EXISTING device pairing-code flow rather than inventing a new mechanism:
/// it POSTs the same <c>/devices/register</c> contract the Director's "Connect to Gateway" dialog
/// uses (issue #469), so the gateway verifies the 4-digit pairing code (matches, not expired, not
/// already used) and issues a unique per-device key. On success it persists the gateway URL + the
/// issued device key to <c>config.json</c> (via <see cref="GatewayCredentialStore"/>) so the Director
/// connects on first run.
///
/// It never throws for an expected failure (unreachable gateway, wrong/expired code, no key in the
/// reply); it returns a human-readable reason so the installer step renders it and BLOCKS completion.
/// The HTTP handler and the persist action are injectable so the verify-before-finish logic, the
/// reachability/pairing calls, and the config persistence are unit-testable without a live gateway.
/// </summary>
public sealed class GatewayPairingRunner
{
    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly Action<string, string> _persist;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Build a runner. By default it makes a real HTTPS/HTTP call and persists with
    /// <see cref="GatewayCredentialStore.SaveEnrolledKey"/>. Tests inject a fake handler and a
    /// capturing persist action so the gate logic runs with no network and no disk writes.
    /// </summary>
    /// <param name="handlerFactory">Supplies the <see cref="HttpMessageHandler"/> for the call; null
    /// uses a real <see cref="HttpClientHandler"/>.</param>
    /// <param name="persist">Persists the verified (gatewayUrl, deviceKey) pair; null uses
    /// <see cref="GatewayCredentialStore.SaveEnrolledKey"/>.</param>
    /// <param name="timeout">Per-call timeout; null uses 15 seconds.</param>
    public GatewayPairingRunner(
        Func<HttpMessageHandler>? handlerFactory = null,
        Action<string, string>? persist = null,
        TimeSpan? timeout = null)
    {
        _handlerFactory = handlerFactory ?? (() => new HttpClientHandler());
        _persist = persist ?? GatewayCredentialStore.SaveEnrolledKey;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Verify reachability + pairing against the gateway at <paramref name="gatewayUrl"/> using
    /// <paramref name="pairingCode"/>, and on success persist the gateway URL + issued device key.
    /// Returns the issued <see cref="DeviceRegistrationResponse"/> on success, or a human-readable
    /// reason on any expected failure (bad input, unreachable, wrong/expired code, missing key). The
    /// installer gates the Finish button on <see cref="OperationResult{T}.Success"/>.
    /// </summary>
    public async Task<OperationResult<DeviceRegistrationResponse>> VerifyAndSaveAsync(
        string gatewayUrl, string deviceId, string machineName, string pairingCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            return OperationResult<DeviceRegistrationResponse>.Fail("Enter the gateway URL.");
        if (string.IsNullOrWhiteSpace(deviceId))
            return OperationResult<DeviceRegistrationResponse>.Fail("This machine has no device id.");
        if (string.IsNullOrWhiteSpace(pairingCode))
            return OperationResult<DeviceRegistrationResponse>.Fail("Enter the 4-digit pairing code.");
        if (!Uri.TryCreate(gatewayUrl.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            return OperationResult<DeviceRegistrationResponse>.Fail(
                "The gateway URL is not valid. Use http://host:port or https://host:port.");

        var url = gatewayUrl.Trim();
        EngineLog.Write($"[GatewayPairingRunner] VerifyAndSaveAsync: gateway={url}, deviceId={deviceId}, machine={machineName}");

        var verify = await RegisterAsync(url, deviceId, machineName, pairingCode, ct);
        if (!verify.Success || verify.Value is null)
            return verify;

        var response = verify.Value;
        // Persist only AFTER the gateway has verified the code and issued a key: the verified gateway
        // URL + device key go into config.json so the Director connects on first run.
        _persist(url, response.DeviceKey);
        EngineLog.Write($"[GatewayPairingRunner] VerifyAndSaveAsync: persisted gateway url + per-device key (machine={response.MachineName})");
        return OperationResult<DeviceRegistrationResponse>.Ok(response);
    }

    /// <summary>
    /// POST the existing <c>/devices/register</c> contract and map the reply to a success-or-reason
    /// result. A transport failure, a 4xx (wrong/expired/used code), any other non-2xx, or a 2xx with
    /// no device key all return a clear reason and NO key - so the installer can never finish on a
    /// pairing that did not actually issue a device key.
    /// </summary>
    private async Task<OperationResult<DeviceRegistrationResponse>> RegisterAsync(
        string gatewayUrl, string deviceId, string machineName, string pairingCode, CancellationToken ct)
    {
        var request = new DeviceRegistrationRequest
        {
            DeviceId = deviceId,
            MachineName = machineName,
            PairingCode = pairingCode,
        };

        using var http = new HttpClient(_handlerFactory(), disposeHandler: true) { Timeout = _timeout };
        http.BaseAddress = new Uri(gatewayUrl.TrimEnd('/') + "/");

        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsJsonAsync("devices/register", request, ct);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayPairingRunner] RegisterAsync transport FAILED: {ex.Message}");
            return OperationResult<DeviceRegistrationResponse>.Fail(
                $"Could not reach the gateway at {gatewayUrl}. Check the URL and that the gateway is running.");
        }

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            EngineLog.Write($"[GatewayPairingRunner] RegisterAsync rejected: HTTP {(int)resp.StatusCode}");
            return OperationResult<DeviceRegistrationResponse>.Fail(
                "Pairing code is wrong, expired, or already used. Mint a new code on the gateway host and try again.");
        }
        if (!resp.IsSuccessStatusCode)
        {
            EngineLog.Write($"[GatewayPairingRunner] RegisterAsync failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            return OperationResult<DeviceRegistrationResponse>.Fail(
                $"The gateway refused the pairing: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        }

        DeviceRegistrationResponse? body;
        try
        {
            body = await resp.Content.ReadFromJsonAsync<DeviceRegistrationResponse>(ct);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayPairingRunner] RegisterAsync: could not read reply: {ex.Message}");
            return OperationResult<DeviceRegistrationResponse>.Fail(
                "The gateway accepted the code but its reply could not be read.");
        }

        if (body is null || string.IsNullOrWhiteSpace(body.DeviceKey))
        {
            EngineLog.Write("[GatewayPairingRunner] RegisterAsync: 2xx with no device key in reply");
            return OperationResult<DeviceRegistrationResponse>.Fail(
                "The gateway accepted the code but returned no device key.");
        }

        EngineLog.Write($"[GatewayPairingRunner] RegisterAsync: per-device key issued for machine={body.MachineName}");
        return OperationResult<DeviceRegistrationResponse>.Ok(body);
    }
}
