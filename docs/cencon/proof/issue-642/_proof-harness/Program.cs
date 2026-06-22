// Live proof harness for issue #642: the Director holds no credential of its own and its telemetry
// POSTs to the Gateway carry NO Authorization header.
//
// Drives the REAL DevThrottleLoginTelemetryReporter and DevThrottleDirectorStartupTelemetryReporter
// over real HTTP against a stub Gateway that mirrors the production POST routes and RECORDS the inbound
// Authorization header for each request. Also exercises the REAL DevThrottleCredentialMigration against
// a temp blob. Walks the acceptance criteria and prints ASCII proof lines:
//   AC3a - login telemetry POST: exactly one POST, body source=app, NO Authorization header.
//   AC3b - director-startup telemetry POST: exactly one POST, NO Authorization header.
//   AC2  - a pre-existing credential blob is deleted by the migration (present before, absent after).
//   AC1  - the migration with no blob present creates nothing and reports nothing to delete.
//
// Run:  dotnet run --project _proof-harness -- <outputDir>

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

var outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "issue-642-proof");
Directory.CreateDirectory(outDir);

var harnessLogPath = Path.Combine(outDir, "harness.log");
var resultsPath = Path.Combine(outDir, "results.json");
var harnessLines = new List<string>();
void Log(string line)
{
    var stamped = $"{DateTime.UtcNow:O}  {line}";
    Console.WriteLine(stamped);
    harnessLines.Add(stamped);
}

FileLog.Start();

static int FreePort()
{
    var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
    l.Start();
    var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return p;
}

var hits = new ConcurrentQueue<Captured>();
var gatewayPort = FreePort();
var gwBuilder = WebApplication.CreateBuilder();
gwBuilder.WebHost.UseUrls($"http://127.0.0.1:{gatewayPort}");
gwBuilder.Logging.ClearProviders();
var gateway = gwBuilder.Build();

async Task Record(HttpContext ctx)
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var hasAuth = ctx.Request.Headers.TryGetValue("Authorization", out var authVal);
    hits.Enqueue(new Captured(ctx.Request.Path, body, hasAuth, hasAuth ? authVal.ToString() : "<none>"));
    ctx.Response.StatusCode = StatusCodes.Status202Accepted;
}

gateway.MapPost("/telemetry/login", Record);
gateway.MapPost("/telemetry/director-startup", Record);
await gateway.StartAsync();
var gatewayUrl = $"http://127.0.0.1:{gatewayPort}";
Log($"Stub gateway listening on {gatewayUrl} (records Authorization header for each POST)");

// Ensure the env override seams are unset so the reporters resolve from the passed gatewayUrl, exactly
// mirroring config.json resolution in the real app.
Environment.SetEnvironmentVariable(DevThrottleLoginTelemetryReporter.EndpointEnvVar, null);
Environment.SetEnvironmentVariable(DevThrottleDirectorStartupTelemetryReporter.EndpointEnvVar, null);

var results = new Dictionary<string, object>();

// ---- AC3a: login telemetry POST carries NO Authorization header -----------------------------------
Log("AC3a: firing the REAL login telemetry reporter against the stub gateway ...");
var loginReporter = new DevThrottleLoginTelemetryReporter(appVersion: "9.9.9", gatewayUrl: gatewayUrl);
// The access token is still passed through the signature, but it must NOT appear on the wire.
await loginReporter.ReportLoginAsync("proof-access-token-should-never-be-sent");
await Task.Delay(200);
hits.TryDequeue(out var loginHit);
var loginJson = JsonDocument.Parse(loginHit!.Body).RootElement;
var loginSource = loginJson.GetProperty("source").GetString();
var ac3aPass = loginHit.Path == "/telemetry/login" && !loginHit.HasAuthorization && loginSource == "app";
Log($"AC3a: gateway received POST {loginHit.Path}; source={loginSource}; Authorization header present={loginHit.HasAuthorization} (value={loginHit.AuthorizationValue}) -> {(ac3aPass ? "PASS" : "FAIL")}");
results["ac3a_login_path"] = loginHit.Path;
results["ac3a_login_body"] = loginHit.Body;
results["ac3a_login_has_authorization"] = loginHit.HasAuthorization;
results["ac3a_pass"] = ac3aPass;

// ---- AC3b: director-startup telemetry POST carries NO Authorization header -------------------------
Log("AC3b: firing the REAL director-startup telemetry reporter against the stub gateway ...");
var startupReporter = new DevThrottleDirectorStartupTelemetryReporter(machineName: "PROOF-MACHINE", appVersion: "9.9.9", gatewayUrl: gatewayUrl);
await startupReporter.ReportStartupAsync("director-proof-id-642");
await Task.Delay(200);
hits.TryDequeue(out var startupHit);
var ac3bPass = startupHit!.Path == "/telemetry/director-startup" && !startupHit.HasAuthorization;
Log($"AC3b: gateway received POST {startupHit.Path}; Authorization header present={startupHit.HasAuthorization} (value={startupHit.AuthorizationValue}) -> {(ac3bPass ? "PASS" : "FAIL")}");
results["ac3b_startup_path"] = startupHit.Path;
results["ac3b_startup_body"] = startupHit.Body;
results["ac3b_startup_has_authorization"] = startupHit.HasAuthorization;
results["ac3b_pass"] = ac3bPass;

await gateway.StopAsync();

// ---- AC2: a pre-existing credential blob is deleted by the migration -------------------------------
var tempDir = Path.Combine(Path.GetTempPath(), "issue-642-migration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var blobPath = Path.Combine(tempDir, "devthrottle-credential.bin");
File.WriteAllBytes(blobPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
var presentBefore = File.Exists(blobPath);
var deleted = DevThrottleCredentialMigration.DeleteStaleDirectorCredential(blobPath);
var absentAfter = !File.Exists(blobPath);
var ac2Pass = presentBefore && deleted && absentAfter;
Log($"AC2: blob presentBefore={presentBefore}, migration deleted={deleted}, absentAfter={absentAfter} -> {(ac2Pass ? "PASS" : "FAIL")}");
results["ac2_present_before"] = presentBefore;
results["ac2_deleted"] = deleted;
results["ac2_absent_after"] = absentAfter;
results["ac2_pass"] = ac2Pass;

// ---- AC1: the migration with no blob present is a harmless no-op (creates nothing) -----------------
var freshDeleted = DevThrottleCredentialMigration.DeleteStaleDirectorCredential(blobPath);
var stillAbsent = !File.Exists(blobPath);
var ac1Pass = !freshDeleted && stillAbsent;
Log($"AC1: migration with no blob -> reported deleted={freshDeleted} (expected False), file created={!stillAbsent} (expected False) -> {(ac1Pass ? "PASS" : "FAIL")}");
results["ac1_reported_deleted"] = freshDeleted;
results["ac1_still_absent"] = stillAbsent;
results["ac1_pass"] = ac1Pass;
Directory.Delete(tempDir, recursive: true);

FileLog.Stop();

var allPass = ac3aPass && ac3bPass && ac2Pass && ac1Pass;
results["all_pass"] = allPass;
File.WriteAllText(resultsPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
File.WriteAllLines(harnessLogPath, harnessLines);

Log($"OVERALL: {(allPass ? "ALL ACCEPTANCE CRITERIA PASS" : "FAILURE")}");
return allPass ? 0 : 1;

// A single captured request: the path, the body, and whether an Authorization header was present.
record Captured(string Path, string Body, bool HasAuthorization, string AuthorizationValue);
