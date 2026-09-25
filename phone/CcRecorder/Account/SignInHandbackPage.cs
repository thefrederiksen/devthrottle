using System.Text.Json;

namespace CcRecorder.Account;

/// <summary>
/// The page the phone's own loopback listener serves when devthrottle.com hands the sign-in back.
///
/// The same contract as the desktop Director's <c>CcDirector.Core.Account.CredentialHandbackPage</c>, copied
/// rather than referenced because the recorder cannot take a dependency on the desktop core library. The
/// completion may put the token pair in the URL FRAGMENT (which the browser never sends to any server); this
/// page's script reads the fragment, POSTs the pair back to the same path as a same-origin JSON body, and
/// strips the fragment from the address bar. The listener then captures the pair from that body. The page
/// itself never carries a token.
/// </summary>
public static class SignInHandbackPage
{
    /// <summary>The access-token field name in the fragment, the query string and the posted JSON body.</summary>
    public const string AccessTokenField = "access_token";

    /// <summary>The refresh-token field name in the fragment, the query string and the posted JSON body.</summary>
    public const string RefreshTokenField = "refresh_token";

    /// <summary>The one-time value that ties a hand-back to the sign-in this phone started.</summary>
    public const string StateField = "state";

    /// <summary>The hand-back page: reads the fragment and posts the token pair back same-origin.</summary>
    public static string BuildHtml() => HandbackHtml;

    /// <summary>The page shown once the recorder has the credential: tells the user where to go next.</summary>
    public static string BuildDoneHtml() => MessageHtml("Almost done. Return to CC Recorder to finish signing in.");

    /// <summary>The page shown when the hand-back arrived without a complete credential.</summary>
    public static string BuildFailedHtml() => MessageHtml("Sign-in did not complete. Return to CC Recorder and press Sign in again.");

    /// <summary>
    /// Parses the JSON body the hand-back script posts
    /// (<c>{"access_token":"...","refresh_token":"...","state":"..."}</c>). Returns false - never throws -
    /// when the body is missing, is not a JSON object, or lacks either non-empty token, so the caller fails
    /// loud on a half-credential instead of using one. <paramref name="state"/> is empty when absent.
    /// </summary>
    public static bool TryParseJsonBody(string? json, out string accessToken, out string refreshToken, out string state)
    {
        accessToken = string.Empty;
        refreshToken = string.Empty;
        state = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var access = ReadStringField(document.RootElement, AccessTokenField);
            var refresh = ReadStringField(document.RootElement, RefreshTokenField);
            if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh))
                return false;

            accessToken = access;
            refreshToken = refresh;
            state = ReadStringField(document.RootElement, StateField) ?? string.Empty;
            return true;
        }
        catch (JsonException)
        {
            // Untrusted input that is not JSON is simply not a complete credential.
            return false;
        }
    }

    private static string? ReadStringField(JsonElement root, string field)
        => root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string MessageHtml(string message) =>
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
        + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>CC Recorder - Sign in</title></head>"
        + "<body style=\"margin:0;background:#141B2E;color:#DDE3EE;font-family:sans-serif;min-height:100vh;"
        + "display:flex;align-items:center;justify-content:center\"><div style=\"max-width:420px;margin:16px;text-align:center\">"
        + "<h1 style=\"font-size:18px\">CC Recorder</h1><p>" + message + "</p></div></body></html>";

    // ASCII only. Reads the fragment, posts the pair back same-origin, strips the fragment. No token in markup.
    private const string HandbackHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>CC Recorder - Sign in</title>
</head>
<body style="margin:0;background:#141B2E;color:#DDE3EE;font-family:sans-serif;min-height:100vh;display:flex;align-items:center;justify-content:center">
  <div style="max-width:420px;margin:16px;text-align:center">
    <h1 style="font-size:18px">CC Recorder</h1>
    <p id="status">Completing sign-in...</p>
  </div>
  <script>
  (function () {
    var status = document.getElementById("status");
    function fail() {
      status.textContent = "Sign-in did not complete. Return to CC Recorder and press Sign in again.";
    }
    function stripFragment() {
      try { history.replaceState(null, "", location.pathname); } catch (e) { /* address bar left as is */ }
    }
    var raw = location.hash && location.hash.charAt(0) === "#" ? location.hash.substring(1) : (location.hash || "");
    var params = new URLSearchParams(raw);
    var access = params.get("access_token");
    var refresh = params.get("refresh_token");
    var state = params.get("state") || "";
    if (!access || !refresh) {
      stripFragment();
      fail();
      return;
    }
    fetch(location.pathname, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ access_token: access, refresh_token: refresh, state: state })
    }).then(function (response) {
      stripFragment();
      if (response.ok) {
        status.textContent = "Almost done. Return to CC Recorder to finish signing in.";
      } else {
        fail();
      }
    }).catch(function () {
      stripFragment();
      fail();
    });
  })();
  </script>
</body>
</html>
""";
}
