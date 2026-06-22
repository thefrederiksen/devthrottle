using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// DevThrottle local development sign-in stand-in.
//
// Serves a real sign-in page on loopback and completes the same browser hand-back the Director's
// LoopbackLoginListener expects (a redirect to the redirect_uri carrying access_token + refresh_token),
// so the first-run login flow works end to end with no backend. See the .csproj header for why this
// exists and README.md for how to run it.

const string DefaultSigningSecret = "devthrottle-local-dev-secret";
const int DefaultPort = 8765;

// The signing secret MUST match what the Director's validator uses (DEVTHROTTLE_JWT_SIGNING_SECRET),
// or the token this tool mints will fail signature verification and the gate will stay closed.
var signingSecret = Environment.GetEnvironmentVariable("DEVTHROTTLE_JWT_SIGNING_SECRET");
if (string.IsNullOrWhiteSpace(signingSecret))
    signingSecret = DefaultSigningSecret;

var portText = Environment.GetEnvironmentVariable("DEVTHROTTLE_DEV_SIGNIN_PORT");
var port = int.TryParse(portText, out var p) ? p : DefaultPort;

var prefix = $"http://127.0.0.1:{port}/";
var listener = new HttpListener();
listener.Prefixes.Add(prefix);
listener.Start();

Console.WriteLine($"[dev-signin] Listening on {prefix}signin");
Console.WriteLine($"[dev-signin] Point the Director at it:  DEVTHROTTLE_SIGNIN_URL={prefix}signin");
Console.WriteLine($"[dev-signin] Signing secret: {(signingSecret == DefaultSigningSecret ? "<default dev secret>" : "<from DEVTHROTTLE_JWT_SIGNING_SECRET>")}");
Console.WriteLine("[dev-signin] Ctrl+C to stop.");

while (true)
{
    HttpListenerContext context;
    try
    {
        context = await listener.GetContextAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[dev-signin] Accept failed: {ex.Message}");
        continue;
    }

    _ = HandleAsync(context, signingSecret);
}

// ----------------------------------------------------------------------------------------------

static async Task HandleAsync(HttpListenerContext context, string signingSecret)
{
    try
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var query = ParseQuery(context.Request.Url?.Query);
        Console.WriteLine($"[dev-signin] {context.Request.HttpMethod} {path}");

        switch (path.TrimEnd('/'))
        {
            case "":
            case "/signin":
                await ServeSignInPageAsync(context, query.GetValueOrDefault("redirect_uri"));
                break;

            case "/complete":
                await CompleteAsync(context, query, signingSecret);
                break;

            default:
                await RespondHtmlAsync(context, 404, "<p>Not found.</p>");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[dev-signin] Request failed: {ex.Message}");
        try { await RespondHtmlAsync(context, 500, "<p>Something went wrong in the dev sign-in stand-in.</p>"); }
        catch { /* response already closed */ }
    }
}

// Serves the two-panel sign-in page. The redirect_uri (the Director's loopback callback) is carried
// through onto each provider action so the completion knows where to hand the credential back.
static async Task ServeSignInPageAsync(HttpListenerContext context, string? redirectUri)
{
    if (string.IsNullOrWhiteSpace(redirectUri) || !IsLoopback(redirectUri))
    {
        await RespondHtmlAsync(context, 400,
            "<p>Missing or non-loopback <code>redirect_uri</code>. Open this page from the Director's Sign in button.</p>");
        return;
    }

    var encodedRedirect = Uri.EscapeDataString(redirectUri);
    string Action(string provider) => $"/complete?provider={provider}&redirect_uri={encodedRedirect}";

    var html = $$"""
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Sign in to DevThrottle</title>
  <style>
    :root { color-scheme: dark; }
    * { box-sizing: border-box; }
    body { margin: 0; font-family: 'Segoe UI', system-ui, Arial, sans-serif; background: #1E1E1E; color: #E6E6E6; }
    .shell { display: grid; grid-template-columns: 1.1fr 1fr; min-height: 100vh; }
    .brand { background: linear-gradient(160deg, #0a3a5c 0%, #07263b 60%, #1E1E1E 100%);
             padding: 64px 56px; display: flex; flex-direction: column; justify-content: center; gap: 22px; }
    .brand .logo { font-family: 'Cascadia Mono', Consolas, monospace; font-weight: 700; letter-spacing: 2px; color: #4FC1FF; font-size: 20px; }
    .brand h1 { font-size: 34px; line-height: 1.2; margin: 0; font-weight: 600; }
    .brand p { font-size: 16px; color: #A9C7DB; margin: 0; max-width: 420px; }
    .panel { display: flex; align-items: center; justify-content: center; padding: 56px; }
    .card { width: 100%; max-width: 380px; display: flex; flex-direction: column; gap: 14px; }
    .card h2 { font-size: 22px; margin: 0 0 6px; font-weight: 600; }
    .card .sub { color: #9A9A9A; font-size: 14px; margin: 0 0 14px; }
    .btn { display: flex; align-items: center; gap: 12px; width: 100%; padding: 14px 16px; border-radius: 10px;
           border: 1px solid #3C3C3C; background: #2A2A2A; color: #F0F0F0; font-size: 15px; font-weight: 500;
           cursor: pointer; text-decoration: none; transition: background .12s, border-color .12s; }
    .btn:hover { background: #333; border-color: #555; }
    .btn .ico { width: 20px; height: 20px; flex: 0 0 20px; }
    .divider { display: flex; align-items: center; gap: 10px; color: #6A6A6A; font-size: 12px; margin: 6px 0; }
    .divider::before, .divider::after { content: ""; height: 1px; background: #333; flex: 1; }
    form { display: flex; flex-direction: column; gap: 10px; }
    input[type=email] { padding: 13px 14px; border-radius: 10px; border: 1px solid #3C3C3C; background: #161616; color: #F0F0F0; font-size: 15px; }
    input[type=email]:focus { outline: none; border-color: #007ACC; }
    .btn.primary { background: #007ACC; border-color: #007ACC; justify-content: center; color: #fff; }
    .btn.primary:hover { background: #0a86d6; }
    .devnote { margin-top: 18px; font-size: 12px; color: #6A6A6A; }
    @media (max-width: 820px) { .shell { grid-template-columns: 1fr; } .brand { display: none; } }
  </style>
</head>
<body>
  <div class="shell">
    <section class="brand">
      <div class="logo">DEVTHROTTLE</div>
      <h1>Run more coding agents, with less chaos.</h1>
      <p>Sign in to start. The Director runs locally on your machine once you are in.</p>
    </section>
    <section class="panel">
      <div class="card">
        <h2>Sign in</h2>
        <p class="sub">Choose how you want to continue.</p>

        <a class="btn" href="{{Action("google")}}">
          <svg class="ico" viewBox="0 0 48 48"><path fill="#FFC107" d="M43.6 20.5H42V20H24v8h11.3C33.7 32.9 29.3 36 24 36c-6.6 0-12-5.4-12-12s5.4-12 12-12c3.1 0 5.9 1.2 8 3.1l5.7-5.7C34.5 6.5 29.5 4.5 24 4.5 13.2 4.5 4.5 13.2 4.5 24S13.2 43.5 24 43.5c10.7 0 19.5-8.7 19.5-19.5 0-1.2-.1-2.3-.4-3.5z"/><path fill="#FF3D00" d="M6.3 14.7l6.6 4.8C14.7 16 19 13 24 13c3.1 0 5.9 1.2 8 3.1l5.7-5.7C34.5 6.5 29.5 4.5 24 4.5 16.3 4.5 9.6 8.9 6.3 14.7z"/><path fill="#4CAF50" d="M24 43.5c5.2 0 10-2 13.6-5.2l-6.3-5.3C29.2 34.7 26.7 35.5 24 35.5c-5.3 0-9.7-3.1-11.3-7.5l-6.5 5C9.5 39 16.2 43.5 24 43.5z"/><path fill="#1976D2" d="M43.6 20.5H42V20H24v8h11.3c-.8 2.2-2.2 4.1-4 5.5l6.3 5.3C41.4 36.3 43.5 30.6 43.5 24c0-1.2-.1-2.3-.4-3.5z"/></svg>
          Continue with Google
        </a>

        <a class="btn" href="{{Action("github")}}">
          <svg class="ico" viewBox="0 0 24 24" fill="#fff"><path d="M12 .5C5.7.5.5 5.7.5 12c0 5.1 3.3 9.4 7.9 10.9.6.1.8-.2.8-.6v-2c-3.2.7-3.9-1.4-3.9-1.4-.5-1.3-1.3-1.7-1.3-1.7-1.1-.7.1-.7.1-.7 1.2.1 1.8 1.2 1.8 1.2 1 .1.7 1.3 2.6 1 .1-.7.4-1.2.7-1.5-2.6-.3-5.3-1.3-5.3-5.7 0-1.3.4-2.3 1.2-3.1-.1-.3-.5-1.5.1-3.1 0 0 1-.3 3.3 1.2a11.5 11.5 0 016 0C17.3 5.1 18.3 5.4 18.3 5.4c.6 1.6.2 2.8.1 3.1.8.8 1.2 1.8 1.2 3.1 0 4.4-2.7 5.4-5.3 5.7.4.4.8 1.1.8 2.2v3.3c0 .4.2.7.8.6 4.6-1.5 7.9-5.8 7.9-10.9C23.5 5.7 18.3.5 12 .5z"/></svg>
          Continue with GitHub
        </a>

        <div class="divider">or</div>

        <form action="/complete" method="get">
          <input type="hidden" name="provider" value="email" />
          <input type="hidden" name="redirect_uri" value="{{WebUtility.HtmlEncode(redirectUri)}}" />
          <input type="email" name="email" placeholder="you@example.com" required />
          <button class="btn primary" type="submit">Continue with email</button>
        </form>

        <p class="devnote">Local development sign-in. Any choice signs you in with a test account so you can use the Director without the live backend.</p>
      </div>
    </section>
  </div>
</body>
</html>
""";

    await RespondHtmlAsync(context, 200, html);
}

// Mints a token and redirects the browser back to the Director's loopback callback, completing the
// hand-back exactly as the real backend would.
static async Task CompleteAsync(HttpListenerContext context, Dictionary<string, string> query, string signingSecret)
{
    var redirectUri = query.GetValueOrDefault("redirect_uri");
    var provider = query.GetValueOrDefault("provider") is { Length: > 0 } pv ? pv : "email";
    var email = query.GetValueOrDefault("email");

    if (string.IsNullOrWhiteSpace(redirectUri) || !IsLoopback(redirectUri))
    {
        await RespondHtmlAsync(context, 400, "<p>Missing or non-loopback <code>redirect_uri</code>.</p>");
        return;
    }

    if (string.IsNullOrWhiteSpace(email))
        email = provider switch { "google" => "dev.user@gmail.com", "github" => "dev.user@users.noreply.github.com", _ => "dev.user@example.com" };

    var accessToken = MintToken(signingSecret, email, provider, DateTimeOffset.UtcNow.AddHours(1));
    var refreshToken = MintToken(signingSecret, email, provider, DateTimeOffset.UtcNow.AddDays(30));

    var separator = redirectUri.Contains('?') ? '&' : '?';
    var location = $"{redirectUri}{separator}access_token={Uri.EscapeDataString(accessToken)}&refresh_token={Uri.EscapeDataString(refreshToken)}";

    Console.WriteLine($"[dev-signin] Completing sign-in (provider={provider}, email={email}) -> redirecting to loopback callback");
    context.Response.StatusCode = 302;
    context.Response.RedirectLocation = location;
    context.Response.Close();
    await Task.CompletedTask;
}

// Mints an HMAC-SHA256 ("HS256") JSON Web Token with the Supabase-shaped identity claims the
// Director's JwtIdentityReader reads (email, app_metadata.provider) and an exp claim. Mirrors the
// shape the real backend issues, so the validator accepts it when the signing secrets match.
static string MintToken(string secret, string email, string provider, DateTimeOffset expiresAt)
{
    var header = B64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
    var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
    {
        ["sub"] = "dev-" + provider,
        ["email"] = email,
        ["app_metadata"] = new Dictionary<string, object> { ["provider"] = provider },
        ["exp"] = expiresAt.ToUnixTimeSeconds(),
    });
    var payload = B64Url(payloadBytes);

    var signingInput = $"{header}.{payload}";
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
    var signature = B64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes(signingInput)));
    return $"{signingInput}.{signature}";
}

static bool IsLoopback(string url) =>
    Uri.TryCreate(url, UriKind.Absolute, out var uri)
    && (uri.Host == "127.0.0.1" || uri.Host == "localhost");

static string B64Url(byte[] bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

// Parses a URL query string (leading "?" optional) into decoded key/value pairs. A small local
// parser so the tool needs no System.Web reference, matching LoopbackLoginListener's approach.
static Dictionary<string, string> ParseQuery(string? query)
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

static async Task RespondHtmlAsync(HttpListenerContext context, int statusCode, string bodyOrPage)
{
    // A bare fragment (starting with "<p") gets wrapped in a minimal dark page; a full document is sent as-is.
    var html = bodyOrPage.TrimStart().StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
        ? bodyOrPage
        : "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>DevThrottle</title></head>" +
          "<body style=\"font-family:Segoe UI,Arial,sans-serif;background:#1E1E1E;color:#CCC;display:flex;" +
          "align-items:center;justify-content:center;height:100vh;margin:0\"><div style=\"text-align:center\">" +
          $"<h2 style=\"color:#007ACC\">DevThrottle</h2>{bodyOrPage}</div></body></html>";

    var buffer = Encoding.UTF8.GetBytes(html);
    context.Response.StatusCode = statusCode;
    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.ContentLength64 = buffer.Length;
    await context.Response.OutputStream.WriteAsync(buffer);
    context.Response.Close();
}
