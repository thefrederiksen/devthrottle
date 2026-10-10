using System.Net;
using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// The loopback listener that captures the credential the DevThrottle sign-in completion hands back
/// to the Director after the user signs in through the system browser (issue #581). It binds an HTTP
/// listener on <c>127.0.0.1</c> only (never a routable address - the loopback trust boundary in
/// security rule DT-07), on an ephemeral free port the operating system assigns, and serves exactly
/// one callback path.
///
/// The credential hand-back is off the URL query string (issue #1082, which absorbs #877): the token
/// pair NEVER rides in the callback URL. The sign-in completion redirects the browser to the callback
/// with the token pair in the URL FRAGMENT (which the browser never sends to a server); the listener
/// serves the shared <see cref="CredentialHandbackPage"/> whose script reads the fragment and POSTs the
/// pair back to the same callback path as a same-origin request BODY, which the listener then captures.
/// During the cloud-side transition the listener ALSO still accepts the old shape - the token pair in
/// the callback URL query string - so the repo side and the devthrottle.com side can roll out
/// compatibly; the old shape is removed only after the cloud completion emits the new one.
///
/// The token values are never written to the log (security rule DT-05); only the fact that a
/// credential was captured is logged. There is no fallback path - a callback that arrives without
/// both tokens (in the posted body or, during transition, in the query) completes the wait with a
/// failure that the caller surfaces, rather than silently proceeding with a half-credential.
///
/// While the live backend sign-in does not yet exist (a dependency flagged on the issue), the same
/// callback is what a local stand-in completion hands a test-issued token to, so the capture is
/// provable end to end in this repository.
/// </summary>
public sealed class LoopbackLoginListener : IDisposable
{
    private const string CallbackPath = "/devthrottle-login-callback/";

    private readonly HttpListener _listener;
    private readonly Uri _callbackUri;
    private bool _disposed;

    /// <summary>
    /// Creates and starts the loopback listener on a free ephemeral port on <c>127.0.0.1</c>. The
    /// chosen port and callback path become <see cref="CallbackUrl"/>, which the sign-in start URL
    /// carries so the completion knows where to hand the credential back.
    /// </summary>
    public LoopbackLoginListener()
    {
        var port = FindFreeLoopbackPort();
        var prefix = $"http://127.0.0.1:{port}{CallbackPath}";

        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _listener.Start();

        _callbackUri = new Uri(prefix);
        FileLog.Write($"[LoopbackLoginListener] Started on loopback callback {prefix} (127.0.0.1 only)");
    }

    /// <summary>
    /// The full loopback callback URL the sign-in completion must hand the credential back to. It is
    /// always on <c>127.0.0.1</c> with the operating-system-assigned ephemeral port.
    /// </summary>
    public Uri CallbackUrl => _callbackUri;

    /// <summary>
    /// Waits for the sign-in completion to call the loopback callback and returns the captured token
    /// pair. The new secure shape (issue #1082): the completion redirects the browser to the callback with
    /// the token pair in the URL FRAGMENT, the served <see cref="CredentialHandbackPage"/> POSTs the pair
    /// back as a same-origin JSON body, and the listener captures it from that body - so no token ever rides
    /// in the callback URL. During the cloud-side transition the OLD shape (the token pair in the callback
    /// URL query string) is still accepted so sign-in keeps working until the cloud completion migrates. A
    /// callback that yields the credential in NEITHER shape throws (no fallback - a half-credential is never
    /// stored). The wait honors <paramref name="ct"/> so the caller can cancel it (for example, if the user
    /// closes the gate).
    /// </summary>
    public async Task<DevThrottleTokens> WaitForCredentialAsync(CancellationToken ct = default)
    {
        FileLog.Write("[LoopbackLoginListener] WaitForCredentialAsync: awaiting the browser sign-in hand-back");

        using var registration = ct.Register(() =>
        {
            // Cancelling stops the blocking GetContextAsync by closing the listener.
            try { _listener.Stop(); } catch { /* already stopping */ }
        });

        // The hand-back can take two requests: a GET that lands with the token pair in the URL fragment (we
        // serve the hand-back page), then the same-origin POST that page makes with the pair in its body.
        // The loop serves the page and comes back for the POST. The old query-string shape completes in one.
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                // not-an-error: the wait was cancelled on purpose
                FileLog.Write("[LoopbackLoginListener] WaitForCredentialAsync: cancelled before a credential arrived");
                throw new OperationCanceledException(ct);
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                // not-an-error: the wait was cancelled on purpose
                FileLog.Write("[LoopbackLoginListener] WaitForCredentialAsync: cancelled before a credential arrived");
                throw new OperationCanceledException(ct);
            }

            var request = context.Request;

            // NEW SHAPE: the hand-back page posts the token pair (read from the URL fragment) back as a
            // same-origin JSON body, so no token rides in the callback URL (issue #1082, absorbs #877).
            if (string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                var body = await ReadRequestBodyAsync(request).ConfigureAwait(false);
                if (CredentialHandbackPage.TryParseJsonBody(body, out var postedAccess, out var postedRefresh))
                {
                    await RespondTextAsync(context, statusCode: 200, "signed-in").ConfigureAwait(false);
                    FileLog.Write("[LoopbackLoginListener] WaitForCredentialAsync: credential captured from the fragment hand-back (request body - no token in the URL)");
                    return new DevThrottleTokens(postedAccess, postedRefresh);
                }

                await RespondTextAsync(context, statusCode: 400, "missing-credential").ConfigureAwait(false);
                FileLog.Write("[LoopbackLoginListener] WaitForCredentialAsync: posted hand-back arrived without both tokens -> failing loud");
                throw new InvalidOperationException(
                    "The sign-in completion posted back without both the access token and the refresh token.");
            }

            // TRANSITION: the old shape carried the token pair in the callback URL query string. Still
            // accepted while the cloud completion migrates to the fragment shape (removed once it has).
            var query = ParseQuery(request.Url?.Query);
            query.TryGetValue("access_token", out var queryAccess);
            query.TryGetValue("refresh_token", out var queryRefresh);

            if (!string.IsNullOrWhiteSpace(queryAccess) && !string.IsNullOrWhiteSpace(queryRefresh))
            {
                await RespondAsync(context, statusCode: 200,
                    "Signed in to DevThrottle. You can close this tab and return to the Director.")
                    .ConfigureAwait(false);
                FileLog.Write("[LoopbackLoginListener] WaitForCredentialAsync: credential captured from the old query-string hand-back (transition compatibility)");
                return new DevThrottleTokens(queryAccess, queryRefresh);
            }

            if (!string.IsNullOrWhiteSpace(queryAccess) || !string.IsNullOrWhiteSpace(queryRefresh))
            {
                // Old shape but only one token: a half-credential is never stored (no fallback).
                await RespondAsync(context, statusCode: 400,
                    "Sign-in could not be completed: the credential was missing. You can close this tab and try again.")
                    .ConfigureAwait(false);
                FileLog.Write("[LoopbackLoginListener] WaitForCredentialAsync: query hand-back arrived without both tokens -> failing loud");
                throw new InvalidOperationException(
                    "The sign-in completion called back without both the access token and the refresh token.");
            }

            // NEW SHAPE entry: a plain GET with no token in the URL is the browser landing on the callback
            // with the token pair in the URL fragment. Serve the hand-back page whose script reads the
            // fragment and POSTs the pair back, then loop to receive that POST.
            await RespondHtmlPageAsync(context, statusCode: 200, CredentialHandbackPage.BuildHtml()).ConfigureAwait(false);
            FileLog.Write("[LoopbackLoginListener] WaitForCredentialAsync: served the fragment hand-back page (awaiting the same-origin POST)");
        }
    }

    /// <summary>
    /// Reads the full request body as text using the request's own encoding. A small local read so the
    /// listener needs no extra dependency; the body is the JSON the hand-back page posts.
    /// </summary>
    private static async Task<string> ReadRequestBodyAsync(HttpListenerRequest request)
    {
        using var reader = new System.IO.StreamReader(request.InputStream, request.ContentEncoding);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Parses a URL query string (the leading "?" is optional) into its decoded key/value pairs. A
    /// small local parser so the listener does not pull in the System.Web assembly for one call.
    /// </summary>
    private static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query))
            return result;

        var trimmed = query.StartsWith('?') ? query[1..] : query;
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
                continue;
            var key = Uri.UnescapeDataString(pair[..eq]);
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            result[key] = value;
        }
        return result;
    }

    private static Task RespondAsync(HttpListenerContext context, int statusCode, string message)
    {
        var html =
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>DevThrottle</title></head>" +
            "<body style=\"font-family:Segoe UI,Arial,sans-serif;background:#1E1E1E;color:#CCCCCC;" +
            "display:flex;align-items:center;justify-content:center;height:100vh;margin:0\">" +
            $"<div style=\"text-align:center\"><h2 style=\"color:#007ACC\">DevThrottle</h2><p>{message}</p></div>" +
            "</body></html>";
        return WriteResponseAsync(context, statusCode, "text/html; charset=utf-8", html);
    }

    /// <summary>
    /// Sends a full HTML document (already a complete page) as the response - used to serve the shared
    /// credential hand-back page, which is a whole document rather than a one-line message.
    /// </summary>
    private static Task RespondHtmlPageAsync(HttpListenerContext context, int statusCode, string html)
        => WriteResponseAsync(context, statusCode, "text/html; charset=utf-8", html);

    /// <summary>
    /// Sends a tiny plain-text body - the acknowledgement for the same-origin POST the hand-back page makes.
    /// The page's script only inspects the HTTP status (ok vs not), so the body is a short marker, never a token.
    /// </summary>
    private static Task RespondTextAsync(HttpListenerContext context, int statusCode, string text)
        => WriteResponseAsync(context, statusCode, "text/plain; charset=utf-8", text);

    private static async Task WriteResponseAsync(HttpListenerContext context, int statusCode, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    /// <summary>
    /// Asks the operating system for a free TCP port on the loopback interface by binding a throwaway
    /// listener to port 0 and reading back the port the OS assigned. Loopback only - never 0.0.0.0.
    /// </summary>
    private static int FindFreeLoopbackPort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { _listener.Close(); }
        catch (Exception ex) { FileLog.Write($"[LoopbackLoginListener] Dispose: listener close ERROR: {ex.Message}"); }
    }
}
