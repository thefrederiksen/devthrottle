using System.Net;
using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// The loopback listener that captures the credential the DevThrottle sign-in completion hands back
/// to the Director after the user signs in through the system browser (issue #581). It binds an HTTP
/// listener on <c>127.0.0.1</c> only (never a routable address - the loopback trust boundary in
/// security rule DT-07), on an ephemeral free port the operating system assigns, and serves exactly
/// one callback path. When the sign-in completion redirects the browser to that callback with the
/// access-plus-refresh token pair, the listener reads the pair, shows the user a "you can close this
/// tab" page, and completes <see cref="WaitForCredentialAsync"/> with the captured tokens.
///
/// The token values are never written to the log (security rule DT-05); only the fact that a
/// credential was captured is logged. There is no fallback path - a callback that arrives without
/// both tokens completes the wait with a failure that the caller surfaces, rather than silently
/// proceeding with a half-credential.
///
/// While the live backend sign-in does not yet exist (a dependency flagged on the issue), the same
/// callback is what a local stand-in completion posts a test-issued token to, so the capture is
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
    /// pair. The completion hands the credential back as the <c>access_token</c> and
    /// <c>refresh_token</c> query parameters on the callback URL. A callback that arrives without both
    /// tokens throws (no fallback - a half-credential is never stored). The wait honors
    /// <paramref name="ct"/> so the caller can cancel it (for example, if the user closes the gate).
    /// </summary>
    public async Task<DevThrottleTokens> WaitForCredentialAsync(CancellationToken ct = default)
    {
        FileLog.Write("[LoopbackLoginListener] WaitForCredentialAsync: awaiting the browser sign-in hand-back");

        using var registration = ct.Register(() =>
        {
            // Cancelling stops the blocking GetContextAsync by closing the listener.
            try { _listener.Stop(); } catch { /* already stopping */ }
        });

        HttpListenerContext context;
        try
        {
            context = await _listener.GetContextAsync().ConfigureAwait(false);
        }
        catch (HttpListenerException) when (ct.IsCancellationRequested)
        {
            FileLog.Write("[LoopbackLoginListener] WaitForCredentialAsync: cancelled before a credential arrived");
            throw new OperationCanceledException(ct);
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
            FileLog.Write("[LoopbackLoginListener] WaitForCredentialAsync: cancelled before a credential arrived");
            throw new OperationCanceledException(ct);
        }

        var query = ParseQuery(context.Request.Url?.Query);
        query.TryGetValue("access_token", out var accessToken);
        query.TryGetValue("refresh_token", out var refreshToken);

        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
        {
            await RespondAsync(context, statusCode: 400,
                "Sign-in could not be completed: the credential was missing. You can close this tab and try again.")
                .ConfigureAwait(false);
            FileLog.Write("[LoopbackLoginListener] WaitForCredentialAsync: callback arrived without both tokens -> failing loud");
            throw new InvalidOperationException(
                "The sign-in completion called back without both the access token and the refresh token.");
        }

        await RespondAsync(context, statusCode: 200,
            "Signed in to DevThrottle. You can close this tab and return to the Director.")
            .ConfigureAwait(false);

        FileLog.Write("[LoopbackLoginListener] WaitForCredentialAsync: credential captured from the browser hand-back");
        return new DevThrottleTokens(accessToken, refreshToken);
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

    private static async Task RespondAsync(HttpListenerContext context, int statusCode, string message)
    {
        var html =
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>DevThrottle</title></head>" +
            "<body style=\"font-family:Segoe UI,Arial,sans-serif;background:#1E1E1E;color:#CCCCCC;" +
            "display:flex;align-items:center;justify-content:center;height:100vh;margin:0\">" +
            $"<div style=\"text-align:center\"><h2 style=\"color:#007ACC\">DevThrottle</h2><p>{message}</p></div>" +
            "</body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";
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
        catch (Exception ex) { FileLog.Write($"[LoopbackLoginListener] Dispose: listener close error: {ex.Message}"); }
    }
}
