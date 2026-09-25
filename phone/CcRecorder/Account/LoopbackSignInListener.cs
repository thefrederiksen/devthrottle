using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace CcRecorder.Account;

/// <summary>The account token pair devthrottle.com hands back after a browser sign-in.</summary>
public sealed record SignInTokens(string AccessToken, string RefreshToken);

/// <summary>
/// Receives the devthrottle.com sign-in hand-back ON THE PHONE ITSELF. It listens on <c>127.0.0.1</c> only,
/// on a port the operating system picks, and serves one path: <see cref="CallbackPath"/>. The website's
/// strict allow-list accepts exactly <c>http://127.0.0.1:&lt;port&gt;/devthrottle-login-callback/</c>, so
/// this is the same security-reviewed hand-back the desktop Director uses
/// (<c>CcDirector.Core.Account.LoopbackLoginListener</c>), with the same two shapes:
/// <list type="bullet">
/// <item>the token pair in the callback URL QUERY (what the website sends today), and</item>
/// <item>the token pair in the URL FRAGMENT, which <see cref="SignInHandbackPage"/> posts back as a body.</item>
/// </list>
/// A raw <see cref="TcpListener"/> is used instead of <c>HttpListener</c> so the behaviour is identical on
/// Android and in the desktop test run. There is no fallback: a hand-back without both tokens fails loud.
/// No token is ever written to the log.
///
/// ONE-TIME STATE. Every app on an Android phone shares 127.0.0.1, so any of them could call this port. The
/// sign-in address carries a random <see cref="State"/>, the website returns it with the tokens, and a
/// hand-back without the matching value is refused and IGNORED: it neither completes nor cancels the real
/// sign-in. A malformed or oversized request is likewise answered and dropped, never fatal to the wait.
/// </summary>
public sealed class LoopbackSignInListener : IDisposable
{
    /// <summary>The one callback path the website's allow-list accepts.</summary>
    public const string CallbackPath = "/devthrottle-login-callback/";

    // A hand-back request is a few kilobytes at most; anything bigger is not ours.
    private const int MaxRequestBytes = 64 * 1024;

    private readonly TcpListener _listener;
    private bool _disposed;

    /// <summary>Binds <c>127.0.0.1</c> on a free port and starts listening.</summary>
    public LoopbackSignInListener()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        CallbackUrl = new Uri($"http://127.0.0.1:{port}{CallbackPath}");
        State = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        RecorderLog.Write($"[LoopbackSignInListener] listening on {CallbackUrl} (127.0.0.1 only)");
    }

    /// <summary>The full callback address to hand the website as <c>redirect_uri</c>.</summary>
    public Uri CallbackUrl { get; }

    /// <summary>The one-time value the website must hand back with the tokens. Never logged.</summary>
    public string State { get; }

    /// <summary>
    /// Serves the browser until it hands back a complete token pair, then returns it. Throws
    /// <see cref="SignInFailedException"/> when the hand-back is incomplete or the user declined, and
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> fires first.
    /// </summary>
    public async Task<SignInTokens> WaitForTokensAsync(CancellationToken ct)
    {
        RecorderLog.Write("[LoopbackSignInListener] WaitForTokensAsync: waiting for the browser hand-back");
        using var registration = ct.Register(() =>
        {
            try { _listener.Stop(); } catch (SocketException) { /* already stopped */ }
        });

        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ct.IsCancellationRequested && ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                RecorderLog.Write("[LoopbackSignInListener] WaitForTokensAsync: cancelled before a credential arrived");
                throw new OperationCanceledException(ct);
            }

            using (client)
            {
                var stream = client.GetStream();
                HttpRequestData? request;
                try
                {
                    request = await ReadRequestAsync(stream, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SignInFailedException or IOException)
                {
                    // Not a request we can read. Drop this connection and keep waiting for the real hand-back.
                    RecorderLog.Write($"[LoopbackSignInListener] WaitForTokensAsync: dropped an unreadable request: {ex.Message}");
                    continue;
                }
                if (request is null)
                    continue; // the browser opened and closed a connection without a request

                var outcome = Handle(request, State);
                await WriteResponseAsync(stream, outcome.Status, outcome.ContentType, outcome.Body, ct).ConfigureAwait(false);
                if (outcome.Tokens is not null)
                {
                    RecorderLog.Write($"[LoopbackSignInListener] WaitForTokensAsync: credential captured ({outcome.How})");
                    return outcome.Tokens;
                }
                if (outcome.Failure is not null)
                {
                    RecorderLog.Write($"[LoopbackSignInListener] WaitForTokensAsync FAILED: {outcome.Failure}");
                    throw new SignInFailedException(outcome.Failure);
                }
            }
        }
    }

    // ===== request handling (pure, so every branch is testable without a socket) =====

    internal sealed record HttpRequestData(string Method, string Path, string Query, string Body);

    internal sealed record Outcome(int Status, string ContentType, string Body, SignInTokens? Tokens, string? Failure, string How);

    internal static Outcome Handle(HttpRequestData request, string expectedState)
    {
        var path = request.Path.EndsWith('/') ? request.Path : request.Path + "/";
        if (!string.Equals(path, CallbackPath, StringComparison.Ordinal))
            return new Outcome(404, "text/plain; charset=utf-8", "not found", null, null, "");

        if (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            // The hand-back page only ever posts a complete body carrying the state, so a body that is not
            // one did not come from this sign-in: ignore it rather than let it end the wait.
            if (!SignInHandbackPage.TryParseJsonBody(request.Body, out var access, out var refresh, out var postedState)
                || !StateMatches(postedState, expectedState))
                return NotOurs();
            return new Outcome(200, "text/plain; charset=utf-8", "signed-in", new SignInTokens(access, refresh), null, "fragment hand-back, posted body");
        }

        var query = ParseQuery(request.Query);
        var carriesAnswer = query.ContainsKey("error")
            || query.ContainsKey(SignInHandbackPage.AccessTokenField)
            || query.ContainsKey(SignInHandbackPage.RefreshTokenField);
        if (carriesAnswer)
        {
            query.TryGetValue(SignInHandbackPage.StateField, out var queryState);
            if (!StateMatches(queryState, expectedState))
                return NotOurs();
        }

        if (query.TryGetValue("error", out var error) && !string.IsNullOrWhiteSpace(error))
            return new Outcome(200, "text/html; charset=utf-8", SignInHandbackPage.BuildFailedHtml(), null,
                $"The sign-in was not completed ({error}).", "");

        query.TryGetValue(SignInHandbackPage.AccessTokenField, out var queryAccess);
        query.TryGetValue(SignInHandbackPage.RefreshTokenField, out var queryRefresh);
        var hasAccess = !string.IsNullOrWhiteSpace(queryAccess);
        var hasRefresh = !string.IsNullOrWhiteSpace(queryRefresh);
        if (hasAccess && hasRefresh)
            return new Outcome(200, "text/html; charset=utf-8", SignInHandbackPage.BuildDoneHtml(),
                new SignInTokens(queryAccess!, queryRefresh!), null, "query-string hand-back");
        if (hasAccess || hasRefresh)
            return new Outcome(400, "text/html; charset=utf-8", SignInHandbackPage.BuildFailedHtml(), null,
                "The sign-in page called back without both the access token and the refresh token.", "");

        // No token in the URL: the pair is in the fragment. Serve the page that posts it back.
        return new Outcome(200, "text/html; charset=utf-8", SignInHandbackPage.BuildHtml(), null, null, "");
    }

    // A hand-back that does not carry this sign-in's state is not ours. Refuse it and keep waiting: letting it
    // fail the wait would let any app on the phone cancel a sign-in in progress.
    private static Outcome NotOurs()
        => new(400, "text/plain; charset=utf-8", "not-this-sign-in", null, null, "");

    private static bool StateMatches(string? received, string expected)
        => !string.IsNullOrEmpty(received)
           && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(received), Encoding.UTF8.GetBytes(expected));

    internal static Dictionary<string, string> ParseQuery(string? query)
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
            result[Uri.UnescapeDataString(pair[..eq].Replace('+', ' '))] = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return result;
    }

    // ===== minimal HTTP/1.1 over the socket =====

    private static async Task<HttpRequestData?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
                return buffer.Length == 0 ? null : throw new SignInFailedException("The browser closed the connection mid-request.");
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxRequestBytes)
                throw new SignInFailedException("The sign-in hand-back request was larger than expected and was refused.");
            headerEnd = IndexOfHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
        }

        var all = buffer.GetBuffer();
        var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
            throw new SignInFailedException("The browser sent a request the recorder could not read.");

        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && (!int.TryParse(line[(colon + 1)..].Trim(), out contentLength) || contentLength < 0))
                throw new SignInFailedException("The request carried an unreadable Content-Length.");
        }
        if (contentLength > MaxRequestBytes)
            throw new SignInFailedException("The sign-in hand-back body was larger than expected and was refused.");

        var bodyStart = headerEnd + 4;
        while (buffer.Length - bodyStart < contentLength)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
                throw new SignInFailedException("The browser closed the connection before sending the whole sign-in body.");
            buffer.Write(chunk, 0, read);
        }
        var body = Encoding.UTF8.GetString(buffer.GetBuffer(), bodyStart, contentLength);

        var target = requestLine[1];
        var q = target.IndexOf('?');
        var path = q < 0 ? target : target[..q];
        var query = q < 0 ? "" : target[q..];
        return new HttpRequestData(requestLine[0], path, query, body);
    }

    private static int IndexOfHeaderEnd(byte[] data, int length)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string contentType, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", _ => "Error" };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\n"
                   + "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _listener.Stop(); }
        catch (SocketException ex) { RecorderLog.Write($"[LoopbackSignInListener] Dispose: stop error: {ex.Message}"); }
    }
}

/// <summary>The sign-in did not produce a usable credential. The message is safe to show the user.</summary>
public sealed class SignInFailedException : Exception
{
    public SignInFailedException(string message) : base(message) { }
}
