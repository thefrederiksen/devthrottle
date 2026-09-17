using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.DevReports;

/// <summary>
/// Asks the Gateway where one session's reports page is (issue #3019, <c>GET /dev-reports/pane-url</c>).
///
/// THE DIRECTOR NEVER COMPOSES THAT ADDRESS. It composes the address of the Gateway's own route - the same
/// thing the Cockpit toolbar button does for <c>GET /cockpit</c> - and then opens the address the Gateway
/// answers with, verbatim. The page address is the Gateway's to decide (CLAUDE.md rule 7), so if it ever
/// moves, one Gateway deploy moves every Director with it.
///
/// The Gateway key authenticates this call as the Authorization header and is never logged.
/// </summary>
public static class DevReportPaneUrlClient
{
    /// <summary>The Gateway route that answers with the page address.</summary>
    public const string RoutePath = "/dev-reports/pane-url";

    /// <summary>
    /// The address of the Gateway ROUTE to call - not the page address, which only the Gateway decides. Pure,
    /// so it is unit-testable.
    /// </summary>
    /// <exception cref="ArgumentException">The Gateway base or the session identifier is missing.</exception>
    public static string BuildRequestUrl(string gatewayBase, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(gatewayBase))
            throw new ArgumentException("There is no Gateway address to ask.", nameof(gatewayBase));
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("There is no session to ask about.", nameof(sessionId));

        return gatewayBase.Trim().TrimEnd('/') + RoutePath + "?sessionId=" + Uri.EscapeDataString(sessionId.Trim());
    }

    /// <summary>
    /// The page address out of the Gateway's answer. Pure, so it is unit-testable. An answer without a usable
    /// address FAILS rather than returning null for a caller to paper over: a Gateway that cannot say where
    /// the page is has not given the pane anything to show, and saying so is the whole message.
    /// </summary>
    /// <exception cref="InvalidOperationException">The answer is not an object with a non-empty url string.</exception>
    public static string ReadUrl(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json ?? "");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"The Gateway's answer could not be read as JavaScript Object Notation: {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("url", out var url)
                || url.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(url.GetString()))
            {
                throw new InvalidOperationException(
                    "The Gateway did not say where the reports page is (its answer carried no url).");
            }

            return url.GetString()!.Trim();
        }
    }

    /// <summary>
    /// Ask the Gateway for one session's reports page address. Off the user interface thread, always.
    /// </summary>
    /// <param name="http">The client to dial with; the caller owns its lifetime.</param>
    /// <param name="gatewayBase">The Gateway base address this Director is configured for.</param>
    /// <param name="token">The Gateway key. Sent as the Authorization header; never logged.</param>
    /// <param name="sessionId">The session whose reports the page shows.</param>
    /// <param name="ct">Cancels the request.</param>
    public static async Task<string> FetchAsync(HttpClient http, string gatewayBase, string token, string sessionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "This Director holds no Gateway key, so it cannot open the reports page. Connect it to a Gateway first.");

        var endpoint = BuildRequestUrl(gatewayBase, sessionId);
        FileLog.Write($"[DevReportPaneUrlClient] FetchAsync: GET {endpoint}");

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        FileLog.Write($"[DevReportPaneUrlClient] FetchAsync: response status={(int)response.StatusCode}");

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"The Gateway refused to say where the reports page is ({(int)response.StatusCode}): {Sentence(body)}");

        var url = ReadUrl(body);
        FileLog.Write($"[DevReportPaneUrlClient] FetchAsync: the page is at {url}");
        return url;
    }

    /// <summary>A Gateway refusal reads as its own sentence when it has one, and as its raw body when it does
    /// not. Capped, because an error message is a diagnosis and not a transcript.</summary>
    private static string Sentence(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body ?? "");
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                return error.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            // Not an answer with a sentence in it; the raw body below is all there is to say.
        }

        var text = (body ?? "").Trim();
        return text.Length <= 400 ? text : text[..400] + "... (truncated)";
    }

    /// <summary>A client dialled the way every other Gateway-facing client in this application is.</summary>
    public static HttpClient NewClient() => new(GatewayHttp.Handler()) { Timeout = TimeSpan.FromSeconds(10) };
}
