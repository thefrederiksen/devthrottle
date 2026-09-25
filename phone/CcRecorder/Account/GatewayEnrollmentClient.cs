using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CcRecorder.Account;

/// <summary>
/// Turns a devthrottle.com account sign-in into this phone's OWN Gateway device key.
///
/// It is the one call the hosted Gateway already offers every phone and browser: <c>POST /mobile/enroll</c>
/// with the account access token as the Bearer. The Gateway validates the token once, mints a tenant-scoped,
/// individually revocable device key, and returns it. The recorder keeps only that device key; the account
/// tokens are used for this one call and then dropped.
/// </summary>
public sealed class GatewayEnrollmentClient
{
    /// <summary>The address of the devthrottle.com sign-in page (the same one the desktop Director opens).</summary>
    public const string SignInPage = "https://devthrottle.com/signin";

    private readonly HttpMessageHandler? _handler;

    /// <param name="handler">Only for tests; production uses the platform default handler.</param>
    public GatewayEnrollmentClient(HttpMessageHandler? handler = null)
    {
        _handler = handler;
    }

    /// <summary>
    /// The sign-in address for the browser, carrying the phone's loopback callback and the one-time state
    /// value the website hands back with the tokens (see <see cref="LoopbackSignInListener.State"/>).
    /// </summary>
    public static Uri BuildSignInUrl(Uri callbackUrl, string state)
        => new($"{SignInPage}?redirect_uri={Uri.EscapeDataString(callbackUrl.ToString())}&state={Uri.EscapeDataString(state)}");

    /// <summary>
    /// Exchanges the account access token for this phone's device key. Throws
    /// <see cref="SignInFailedException"/> with a message fit to show the user on every refusal.
    /// </summary>
    public async Task<string> EnrollAsync(string gatewayUrl, string accessToken, string deviceId, string deviceName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl)) throw new ArgumentException("The Gateway address is empty.", nameof(gatewayUrl));
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ArgumentException("The access token is empty.", nameof(accessToken));
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("The device id is empty.", nameof(deviceId));

        var url = gatewayUrl.TrimEnd('/') + "/mobile/enroll";
        RecorderLog.Write($"[GatewayEnrollmentClient] EnrollAsync: POST {url} deviceId={deviceId} (token not logged)");

        using var http = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(60);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var body = JsonSerializer.Serialize(new { DeviceKey = "", DeviceId = deviceId, Name = deviceName, Platform = "android" });
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            RecorderLog.Write($"[GatewayEnrollmentClient] EnrollAsync FAILED: could not reach {url}: {ex}");
            throw new SignInFailedException($"Could not reach the Gateway at {gatewayUrl}: {ex.Message}");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var key = ReadField(text, "deviceKey");
                if (string.IsNullOrWhiteSpace(key))
                    throw new SignInFailedException("The Gateway accepted the sign-in but returned no device key.");
                RecorderLog.Write("[GatewayEnrollmentClient] EnrollAsync: enrolled, device key received (not logged)");
                return key;
            }

            var reason = ReadField(text, "message") ?? ReadField(text, "error") ?? response.ReasonPhrase ?? "no reason given";
            RecorderLog.Write($"[GatewayEnrollmentClient] EnrollAsync FAILED: HTTP {(int)response.StatusCode}: {reason}");
            throw new SignInFailedException(DescribeRefusal(response.StatusCode, reason));
        }
    }

    internal static string DescribeRefusal(HttpStatusCode status, string reason) => status switch
    {
        HttpStatusCode.Unauthorized => $"The Gateway did not accept the sign-in ({reason}). Press Sign in again.",
        HttpStatusCode.PaymentRequired => $"This account has no active DevThrottle subscription: {reason}",
        HttpStatusCode.Forbidden => $"The Gateway refused this phone ({reason}).",
        _ => $"The Gateway could not enroll this phone (HTTP {(int)status}): {reason}",
    };

    private static string? ReadField(string json, string field)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    return p.Value.GetString();
            }
            return null;
        }
        catch (JsonException)
        {
            // A body that is not JSON (a proxy error page) carries no field; the status code still speaks.
            return null;
        }
    }
}
