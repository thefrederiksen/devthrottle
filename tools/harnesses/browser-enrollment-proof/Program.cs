// Browser-enrollment live proof harness (issue #1088).
//
// Boots a REAL GatewayHost - auth gate ON, signed in to a test account - plus a local CLOUD FIXTURE
// that stands in for devthrottle.com (the activation page + the /api/v1/devices endpoints). The
// production activation page hard-rejects non-phone enrollment today (tracked cross-repo on issue
// #1081), so the fixture implements the CONTRACT that issue requests: it accepts platform "browser"
// and the Cockpit callback path /device-callback, and hands the device key back in the URL FRAGMENT
// only (the issue #1082 pattern). Everything else in the flow is the real production code path:
//
//   1. A signed-out browser navigation to any Cockpit route -> the real AuthMiddleware 302 to
//      /signin?next=... (never login.html).
//   2. The real React Cockpit (built with VITE_DT_SITE_BASE pointing at the fixture) renders the
//      shared client-core Sign in screen and sends the browser to the fixture's /m-activate.
//   3. Approving hands back ONLY #device_key=...&state=... in the fragment to /device-callback.
//   4. The shared DeviceCallback exchanges it at the real POST /m/enroll; the Gateway verifies the
//      key against the fixture cloud (account-scoped) and issues a real LOCAL device key.
//   5. Every data call is then authorized by that local key alone - the one standing credential.
//   6. POST /__control/revoke + /__control/reconcile model a website revoke and run the REAL
//      ChildDeviceMirrorService reconcile against the LIVE device registry, so the next request 401s
//      and the Cockpit returns to /signin. (In production the periodic heartbeat sweep runs the same
//      reconcile; the control endpoint only makes the proof fast.)
//
// Deliberately NOT part of cc-director.sln (the dev-signin precedent): run on demand with
//   dotnet run --project tools/harnesses/browser-enrollment-proof -- [gatewayPort] [fixturePort]
// after building the Cockpit with the fixture as its site base:
//   VITE_DT_SITE_BASE=http://127.0.0.1:8971 npm run build --workspace @devthrottle/cockpit
//
// ASCII-only output. No device key is ever logged (security rule DT-05).

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway;
using CcDirector.Gateway.Account;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

var gatewayPort = args.Length > 0 && int.TryParse(args[0], out var gp) ? gp : 8970;
var fixturePort = args.Length > 1 && int.TryParse(args[1], out var fp) ? fp : 8971;
var fixtureBase = $"http://127.0.0.1:{fixturePort}";

// Real file logging, so the proof can grep the REAL Gateway log for the DT-05 assertion (no device
// key value in any log line). FileLog.Write is a no-op until Start() is called.
FileLog.Start();

// ---- Environment BEFORE any Gateway type is constructed --------------------------------------
// The Gateway's DeviceRegistryClient instances resolve the cloud base from DEVTHROTTLE_API_URL at
// construction, and the account service resolves the token-signing secret at build - both must point
// at the fixture before GatewayHost is created.
const string SigningSecret = "browser-enrollment-proof-signing-secret-1088";
Environment.SetEnvironmentVariable("DEVTHROTTLE_JWT_SIGNING_SECRET", SigningSecret);
Environment.SetEnvironmentVariable("DEVTHROTTLE_API_URL", fixtureBase);
// ISOLATION (mandatory): this proof Gateway must NEVER touch the machine's Tailscale serve table -
// without this, GatewayHost would point the tailnet 443 front door at itself and fight the
// production Gateway on this machine. The product's own dev/test kill switch disables it.
Environment.SetEnvironmentVariable("CC_GATEWAY_NO_TAILSCALE", "1");

// ---- The cloud fixture state -------------------------------------------------------------------
// The one browser device key the activation page hands out, and the roster row approving mints.
// A fixed value so the proof driver can assert "this exact key never appears in any log line".
const string BrowserCloudKey = "dtd_live_BROWSER_PROOF_KEY_1088";
const string BrowserCloudId = "cloud-browser-proof-1088";
var roster = new List<JsonObject>();  // cloud device rows, shaped like the production API
var rosterLock = new object();
var browserRevoked = false;
var shutdownRequested = new TaskCompletionSource();

// ---- The signed-in Gateway account --------------------------------------------------------------
// An HS256 test token (the GatewayTestJwt shape) signed with the secret above, so the Gateway is
// genuinely signed in and the enroll verify carries a real Bearer to the fixture cloud.
var accessToken = MintHs256Jwt(SigningSecret, subject: "proof-account-1088", expiresAtUtc: DateTime.UtcNow.AddHours(8));
// A self-host proof: the harness runs the enrollment against a self-host Gateway, so the validator keeps HS256.
var account = GatewayAccountFactory.Build(new InMemoryTokenStore(), hosted: false);
account.StoreTokens(new DevThrottleTokens(accessToken, "proof-refresh-1088"));

// ---- Stage the built React Cockpit into this host's web root ------------------------------------
// CockpitReactApp serves from <baseDir>/wwwroot/c; copy the freshly built bundle there so the REAL
// shell (built against the fixture site base) is what the browser receives.
var repoRoot = FindRepoRoot();
var cockpitDist = Path.Combine(repoRoot, "apps", "cockpit", "dist");
if (!Directory.Exists(cockpitDist))
{
    Console.WriteLine("ERROR: apps/cockpit/dist not found. Build the Cockpit first:");
    Console.WriteLine("  VITE_DT_SITE_BASE=" + fixtureBase + " npm run build --workspace @devthrottle/cockpit");
    return 1;
}
var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot", "c");
if (Directory.Exists(webRoot)) Directory.Delete(webRoot, recursive: true);
CopyDirectory(cockpitDist, webRoot);
Console.WriteLine($"[proof] staged Cockpit bundle: {cockpitDist} -> {webRoot}");

// ---- The real Gateway ----------------------------------------------------------------------------
var instancesDir = Path.Combine(Path.GetTempPath(), "cc-browser-proof-instances-" + Guid.NewGuid().ToString("N"));
var gatewayToken = "proof-shared-token-" + Guid.NewGuid().ToString("N");
var gateway = new GatewayHost(
    port: gatewayPort,
    token: gatewayToken,
    authEnabled: true,
    instancesDirectory: instancesDir,
    workListsPath: Path.Combine(instancesDir, "worklists", "worklists.json"),
    devicesPath: Path.Combine(instancesDir, "devices.json"),
    account: account);

// ---- The cloud fixture web app --------------------------------------------------------------------
var fixtureBuilder = WebApplication.CreateBuilder();
fixtureBuilder.Logging.ClearProviders();
var fixture = fixtureBuilder.Build();
fixture.Urls.Add(fixtureBase);

// The activation page: the /m-activate contract, generalized per the #1081 request (accepts a
// non-phone platform and the Cockpit /device-callback redirect path). Shows what is being approved.
fixture.MapGet("/m-activate", (HttpContext ctx) =>
{
    var q = ctx.Request.Query;
    var redirectUri = q["redirect_uri"].ToString();
    var name = q["name"].ToString();
    var installId = q["install_id"].ToString();
    var platform = q["platform"].ToString();
    var state = q["state"].ToString();

    if (redirectUri.Length == 0 || name.Length == 0 || installId.Length == 0 || platform.Length == 0)
    {
        return Results.Content("<h1>This request is not valid</h1><p>Missing enrollment parameters.</p>", "text/html; charset=utf-8");
    }

    var approveUrl = "/m-activate/approve"
        + "?redirect_uri=" + Uri.EscapeDataString(redirectUri)
        + "&name=" + Uri.EscapeDataString(name)
        + "&install_id=" + Uri.EscapeDataString(installId)
        + "&platform=" + Uri.EscapeDataString(platform)
        + "&state=" + Uri.EscapeDataString(state);

    var html = $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>DevThrottle - Connect this device</title>
        <style>
          body { font-family: Segoe UI, sans-serif; background: #0B1020; color: #e8e8ee; display: flex; justify-content: center; }
          .card { max-width: 430px; margin-top: 8vh; background: #151b31; border-radius: 14px; padding: 2rem; }
          .logo { font-weight: 700; background: #2d6cdf; color: white; width: 44px; height: 44px; border-radius: 10px;
                  display: flex; align-items: center; justify-content: center; margin-bottom: 1rem; }
          h1 { font-size: 1.35rem; margin: 0 0 .5rem; }
          .device { background: #0e1428; border-radius: 10px; padding: .8rem 1rem; margin: 1rem 0; }
          .device b { display: block; }
          .device span { opacity: .65; font-size: .85rem; }
          .who { opacity: .75; font-size: .9rem; }
          a.approve { display: block; text-align: center; background: #2d6cdf; color: white; text-decoration: none;
                      padding: .85rem; border-radius: 10px; font-weight: 600; margin-top: 1rem; }
          .fixture { margin-top: 1.25rem; font-size: .75rem; opacity: .5; }
        </style></head><body><div class="card">
          <div class="logo">DT</div>
          <h1>Connect this device?</h1>
          <p class="who">The DevThrottle Cockpit wants to use your account on this device.</p>
          <div class="device"><b>{{Html(name)}}</b><span>{{Html(platform)}}</span></div>
          <p class="who">Signed in as <b>proof-account-1088</b></p>
          <a class="approve" href="{{Html(approveUrl)}}">Connect this device</a>
          <p class="fixture">LOCAL ACTIVATION FIXTURE (issue #1088 proof) - models the devthrottle.com
          contract requested on cross-repo issue #1081. Hands back the device key in the URL fragment only.</p>
        </div></body></html>
        """;
    return Results.Content(html, "text/html; charset=utf-8");
});

// Approving registers the browser on the fixture roster (device_type follows the platform, the #1081
// request) and hands the device key back in the URL FRAGMENT only - never the query string.
fixture.MapGet("/m-activate/approve", (HttpContext ctx) =>
{
    var q = ctx.Request.Query;
    var redirectUri = q["redirect_uri"].ToString();
    var name = q["name"].ToString();
    var installId = q["install_id"].ToString();
    var platform = q["platform"].ToString();
    var state = q["state"].ToString();

    lock (rosterLock)
    {
        roster.RemoveAll(r => (string?)r["install_id"] == installId);
        roster.Add(new JsonObject
        {
            ["id"] = BrowserCloudId,
            ["name"] = name,
            ["platform"] = platform,
            ["device_type"] = platform is "android" or "ios" ? "phone" : "browser",
            ["key_prefix"] = "dtd_live",
            ["key_last4"] = BrowserCloudKey[^4..],
            ["install_id"] = installId,
        });
        browserRevoked = false;
    }
    Console.WriteLine($"[fixture] approved + registered browser device: name={name}, platform={platform} (key not logged)");

    var fragment = "device_key=" + Uri.EscapeDataString(BrowserCloudKey)
        + (state.Length > 0 ? "&state=" + Uri.EscapeDataString(state) : "");
    return Results.Redirect(redirectUri + "#" + fragment);
});

// POST /api/v1/devices/verify - the account-scoped verify the Gateway's enroll path calls.
fixture.MapPost(DeviceRegistryClient.VerifyPath, async (HttpContext ctx) =>
{
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var key = (string?)JsonNode.Parse(body)?["device_key"] ?? "";
    bool valid;
    lock (rosterLock)
    {
        valid = !browserRevoked && key == BrowserCloudKey && roster.Any(r => (string?)r["id"] == BrowserCloudId);
    }
    Console.WriteLine($"[fixture] verify: bearer={(ctx.Request.Headers.Authorization.Count > 0 ? "present" : "MISSING")}, valid={valid} (key not logged)");
    return valid
        ? Results.Json(new { data = new { valid = true, id = BrowserCloudId } })
        : Results.Json(new { data = new { valid = false } });
});

// GET /api/v1/devices - the roster the reconcile sweep lists.
fixture.MapGet(DeviceRegistryClient.DevicesPath, () =>
{
    lock (rosterLock)
    {
        var rows = new JsonArray(roster.Select(r => (JsonNode)r.DeepClone()).ToArray());
        return Results.Content(new JsonObject { ["data"] = rows }.ToJsonString(), "application/json");
    }
});

// POST /api/v1/devices/register - the Gateway registers ITSELF as a device on sign-in; honor it so
// the self-registration service runs its real path against the fixture.
fixture.MapPost(DeviceRegistryClient.RegisterPath, async (HttpContext ctx) =>
{
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var node = JsonNode.Parse(body) ?? new JsonObject();
    var installId = (string?)node["install_id"] ?? "";
    var platform = (string?)node["platform"] ?? "unknown";
    var name = (string?)node["name"] ?? "Gateway";
    var deviceType = (string?)node["device_type"] ?? "gateway";
    var id = "cloud-self-" + installId;
    JsonObject record;
    lock (rosterLock)
    {
        roster.RemoveAll(r => (string?)r["install_id"] == installId && (string?)r["id"] != BrowserCloudId);
        record = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["platform"] = platform,
            ["device_type"] = deviceType,
            ["key_prefix"] = "dtd_live",
            ["key_last4"] = "self",
            ["install_id"] = installId,
        };
        roster.Add(record);
    }
    Console.WriteLine($"[fixture] self-register: install={installId}, platform={platform}, type={deviceType}");
    return Results.Content(new JsonObject
    {
        ["data"] = new JsonObject
        {
            ["device_key"] = "dtd_live_SELF_" + installId,
            ["record"] = (JsonObject)record.DeepClone(),
        },
    }.ToJsonString(), "application/json");
});

// POST /api/v1/devices/heartbeat - 200 for a known install, 404 for an unknown/revoked one.
fixture.MapPost(DeviceRegistryClient.HeartbeatPath, async (HttpContext ctx) =>
{
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var installId = (string?)JsonNode.Parse(body)?["install_id"] ?? "";
    bool known;
    lock (rosterLock)
    {
        known = roster.Any(r => (string?)r["install_id"] == installId);
    }
    return known
        ? Results.Json(new { data = new { recorded = true } })
        : Results.Json(new { error = "unknown install" }, statusCode: StatusCodes.Status404NotFound);
});

// ---- Proof-control endpoints (the fixture side of the revoke round trip) -----------------------
// POST /__control/revoke - the website's "Remove device": the browser row leaves the roster and its
// key stops verifying.
fixture.MapPost("/__control/revoke", () =>
{
    lock (rosterLock)
    {
        browserRevoked = true;
        roster.RemoveAll(r => (string?)r["id"] == BrowserCloudId);
    }
    Console.WriteLine("[fixture] REVOKED the browser device (roster row removed)");
    return Results.Json(new { revoked = true });
});

// POST /__control/reconcile - run the REAL ChildDeviceMirrorService reconcile against the LIVE
// Gateway device registry (in production the periodic heartbeat sweep runs this same reconcile;
// this endpoint only makes the proof immediate instead of waiting out the sweep interval).
fixture.MapPost("/__control/reconcile", async () =>
{
    var mirror = new ChildDeviceMirrorService(account, new DeviceRegistryClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }), gateway.Devices);
    await mirror.ReconcileAsync();
    var remaining = gateway.Devices.Count;
    Console.WriteLine($"[fixture] reconcile ran against the live registry: {remaining} local device(s) remain");
    return Results.Json(new { reconciled = true, remainingLocalDevices = remaining });
});

// GET /__control/roster - the cloud roster as the proof artifact (keys are never in roster rows).
fixture.MapGet("/__control/roster", () =>
{
    lock (rosterLock)
    {
        var rows = new JsonArray(roster.Select(r => (JsonNode)r.DeepClone()).ToArray());
        return Results.Content(new JsonObject { ["data"] = rows }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "application/json");
    }
});

// POST /__control/shutdown - stop the harness cleanly from the proof driver.
fixture.MapPost("/__control/shutdown", () =>
{
    Console.WriteLine("[fixture] shutdown requested");
    shutdownRequested.TrySetResult();
    return Results.Json(new { shuttingDown = true });
});

// ---- Run ------------------------------------------------------------------------------------------
await fixture.StartAsync();
await gateway.StartAsync();

Console.WriteLine();
Console.WriteLine("READY");
Console.WriteLine($"  Gateway (auth ON, signed in):  http://127.0.0.1:{gateway.Port}/");
Console.WriteLine($"  Cloud fixture (activation):    {fixtureBase}/m-activate");
Console.WriteLine($"  Revoke control:                POST {fixtureBase}/__control/revoke then POST {fixtureBase}/__control/reconcile");
Console.WriteLine($"  Gateway log (for the DT-05 grep): {FileLog.CurrentLogPath}");
Console.WriteLine();
Console.WriteLine($"Press Ctrl+C to stop (or POST {fixtureBase}/__control/shutdown).");

Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdownRequested.TrySetResult(); };
await shutdownRequested.Task;

await gateway.StopAsync();
await fixture.StopAsync();
FileLog.Stop();
return 0;

// ---- Helpers ---------------------------------------------------------------------------------------

static string MintHs256Jwt(string secret, string subject, DateTime expiresAtUtc)
{
    var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
    {
        ["alg"] = "HS256",
        ["typ"] = "JWT",
    }));
    var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
    {
        ["sub"] = subject,
        ["exp"] = new DateTimeOffset(expiresAtUtc, TimeSpan.Zero).ToUnixTimeSeconds(),
    }));
    var signingInput = $"{header}.{payload}";
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
    var signature = Base64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes(signingInput)));
    return $"{signingInput}.{signature}";
}

static string Base64Url(byte[] bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

static string Html(string value) => System.Web.HttpUtility.HtmlEncode(value);

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
            return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("Repository root (cc-director.sln) not found above " + AppContext.BaseDirectory);
}

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.GetFiles(source))
        File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
    foreach (var sub in Directory.GetDirectories(source))
        CopyDirectory(sub, Path.Combine(destination, Path.GetFileName(sub)));
}

// An in-memory token store so the harness never touches the Windows Data Protection store.
internal sealed class InMemoryTokenStore : IProtectedTokenStore
{
    private DevThrottleTokens? _tokens;
    public bool HasTokens => _tokens is not null;
    public void Save(DevThrottleTokens tokens) => _tokens = tokens;
    public DevThrottleTokens? Load() => _tokens;
    public void Clear() => _tokens = null;
}
