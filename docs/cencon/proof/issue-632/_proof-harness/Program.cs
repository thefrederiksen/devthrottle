// Live proof harness for issue #632: fire a Director-startup telemetry event to the Gateway on launch.
//
// Drives the REAL DevThrottleDirectorStartupTelemetryReporter over real HTTP against a stub Gateway
// that mirrors the production POST /telemetry/director-startup route. Walks the acceptance criteria
// and prints ASCII proof lines:
//   AC1 - gateway configured: EXACTLY ONE POST with { director_id, machine_name, app_version }.
//   AC2 - no gateway configured: NO-OP, no HTTP call, skip line logged, no throw.
//   AC4 - the reporter target (gateway URL + body) and the no-gateway no-op (also covered by units).
//
// Run:  dotnet run --project _proof-harness -- <outputDir>

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

var outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "issue-632-proof");
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

// Start the real Director FileLog so the reporter's [DevThrottleDirectorStartupTelemetryReporter]
// lines (POST-to-gateway, skip no-op) land on disk - we copy them as proof.
FileLog.Start();
var directorLogPath = FileLog.CurrentLogPath;

static int FreePort()
{
    var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
    l.Start();
    var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return p;
}

// Stub GATEWAY: mirrors POST /telemetry/director-startup and records each inbound request body.
var gatewayHits = new ConcurrentQueue<string>();
var gatewayPort = FreePort();
var gwBuilder = WebApplication.CreateBuilder();
gwBuilder.WebHost.UseUrls($"http://127.0.0.1:{gatewayPort}");
gwBuilder.Logging.ClearProviders();
var gateway = gwBuilder.Build();
gateway.MapPost("/telemetry/director-startup", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    gatewayHits.Enqueue(body);
    return Results.StatusCode(StatusCodes.Status202Accepted);
});
await gateway.StartAsync();
var gatewayUrl = $"http://127.0.0.1:{gatewayPort}";
Log($"Stub gateway listening on {gatewayUrl}/telemetry/director-startup");

var results = new Dictionary<string, object>();

// Make sure the environment override is NOT set, so the reporter resolves from the passed gatewayUrl
// (AC1) / the empty gateway (AC2), exactly mirroring config.json resolution in the real app.
Environment.SetEnvironmentVariable(DevThrottleDirectorStartupTelemetryReporter.EndpointEnvVar, null);

// ---- AC1: gateway configured -> exactly one POST with the contract body ---------------------------
Log("AC1: firing reporter with a configured gateway URL ...");
var reporterA = new DevThrottleDirectorStartupTelemetryReporter(
    machineName: "PROOF-MACHINE", appVersion: "9.9.9", gatewayUrl: gatewayUrl);
await reporterA.ReportStartupAsync("director-proof-id-001");

await Task.Delay(200);
var ac1Count = gatewayHits.Count;
gatewayHits.TryDequeue(out var ac1Body);
var ac1Json = JsonDocument.Parse(ac1Body!).RootElement;
var ac1DirectorId = ac1Json.GetProperty("director_id").GetString();
var ac1Machine = ac1Json.GetProperty("machine_name").GetString();
var ac1Version = ac1Json.GetProperty("app_version").GetString();
var ac1Pass = ac1Count == 1
    && ac1DirectorId == "director-proof-id-001"
    && ac1Machine == "PROOF-MACHINE"
    && ac1Version == "9.9.9";
Log($"AC1: gateway received {ac1Count} POST(s); body director_id={ac1DirectorId}, machine_name={ac1Machine}, app_version={ac1Version} -> {(ac1Pass ? "PASS" : "FAIL")}");
results["ac1_post_count"] = ac1Count;
results["ac1_body"] = ac1Body!;
results["ac1_pass"] = ac1Pass;

// ---- AC2: no gateway configured -> no-op, no HTTP call, skip line, no throw ------------------------
Log("AC2: firing reporter with NO gateway URL (empty) ...");
var beforeHits = gatewayHits.Count;
var threw = false;
var reporterB = new DevThrottleDirectorStartupTelemetryReporter(
    machineName: "PROOF-MACHINE", gatewayUrl: "");
try
{
    await reporterB.ReportStartupAsync("director-proof-id-002");
}
catch (Exception ex)
{
    threw = true;
    Log($"AC2: UNEXPECTED throw: {ex.Message}");
}
await Task.Delay(200);
var afterHits = gatewayHits.Count;
var ac2NoCall = afterHits == beforeHits;
var ac2Pass = ac2NoCall && !threw;
Log($"AC2: gateway hits unchanged ({beforeHits}->{afterHits}), threw={threw} -> {(ac2Pass ? "PASS" : "FAIL")}");
results["ac2_no_call"] = ac2NoCall;
results["ac2_threw"] = threw;
results["ac2_pass"] = ac2Pass;

await gateway.StopAsync();

// Copy the relevant Director FileLog lines (the reporter's own log lines, incl. the skip line) as proof.
FileLog.Stop();
var reporterLogLines = new List<string>();
if (directorLogPath is not null && File.Exists(directorLogPath))
{
    foreach (var line in File.ReadAllLines(directorLogPath))
        if (line.Contains("DevThrottleDirectorStartupTelemetryReporter"))
            reporterLogLines.Add(line);
}
File.WriteAllLines(Path.Combine(outDir, "director-reporter-log-lines.txt"), reporterLogLines);
Log($"Copied {reporterLogLines.Count} reporter log line(s) from {directorLogPath}");

var allPass = ac1Pass && ac2Pass;
results["all_pass"] = allPass;
File.WriteAllText(resultsPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
File.WriteAllLines(harnessLogPath, harnessLines);

Log($"OVERALL: {(allPass ? "ALL ACCEPTANCE CRITERIA PASS" : "FAILURE")}");
return allPass ? 0 : 1;
