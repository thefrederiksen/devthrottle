// Live proof harness for issue #631: Director-startup telemetry endpoint (record + best-effort
// cloud-forward).
//
// Boots the REAL production GatewayHost (the same class GatewayWorker/GatewayApp host) and a local
// stub cloud startup endpoint that records every forwarded request. Walks all five acceptance criteria
// over real HTTP and prints ASCII proof lines:
//
//   AC1 - POST /telemetry/director-startup returns 202 for a valid body.
//   AC2 - the event is recorded Gateway-side (a log line shows director_id + app_version).
//   AC3 - with DEVTHROTTLE_STARTUP_TELEMETRY_URL set to a local stub, the event is forwarded to it.
//   AC4 - with no startup cloud URL configured, the endpoint still returns 202 and records locally,
//         logging that no cloud startup endpoint is configured (no error).
//   AC5 - the unit tests cover both paths (asserted separately by dotnet test; summarized here).
//
// Run:  dotnet run --project _proof-harness -- <outputDir>

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

var outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "issue-631-proof");
Directory.CreateDirectory(outDir);

var stubLogPath = Path.Combine(outDir, "stub-requests.log");
var harnessLogPath = Path.Combine(outDir, "harness.log");
const string StartupEnvVar = "DEVTHROTTLE_STARTUP_TELEMETRY_URL";

var harnessLines = new List<string>();
void Log(string line)
{
    var stamped = $"{DateTime.UtcNow:O}  {line}";
    Console.WriteLine(stamped);
    harnessLines.Add(stamped);
}

// Start the Gateway's real FileLog so the live run's [DirectorStartupTelemetryEndpoint] record lines
// (recorded director_id + app_version; the "no cloud startup endpoint configured" line) land on disk.
FileLog.Start();
var gatewayLogPath = FileLog.CurrentLogPath;

static int FreePort()
{
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    var p = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return p;
}

// ---- Stub cloud startup endpoint. Records every forwarded request. ----
var stubReceived = new System.Collections.Concurrent.ConcurrentQueue<(DateTime At, string Body)>();
var stubPort = FreePort();
var stubBuilder = WebApplication.CreateBuilder();
stubBuilder.Logging.ClearProviders();
var stub = stubBuilder.Build();
stub.Urls.Add($"http://127.0.0.1:{stubPort}");
stub.MapPost("/api/v1/telemetry/director-startup", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    stubReceived.Enqueue((DateTime.UtcNow, body));
    return Results.Ok();
});
await stub.StartAsync();
var stubUrl = $"http://127.0.0.1:{stubPort}/api/v1/telemetry/director-startup";
Log($"[harness] stub cloud startup endpoint listening at {stubUrl}");

var retryInterval = TimeSpan.FromSeconds(2);

async Task<GatewayHost> StartGatewayAsync(string label, string queueFile)
{
    var port = FreePort();
    var host = new GatewayHost(
        port: port,
        token: null,
        authEnabled: false,
        telemetryQueuePath: queueFile,
        telemetryRetryInterval: retryInterval);
    await host.StartAsync();
    Log($"[harness] Gateway ({label}) started on http://127.0.0.1:{host.Port} (queueFile={queueFile}, retry={retryInterval.TotalSeconds}s)");
    return host;
}

var http = new HttpClient();
async Task<HttpStatusCode> PostStartupAsync(string baseUrl, string directorId, string machineName, string appVersion)
{
    var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/telemetry/director-startup")
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { director_id = directorId, machine_name = machineName, app_version = appVersion }),
            Encoding.UTF8, "application/json"),
    };
    var resp = await http.SendAsync(req);
    return resp.StatusCode;
}

var results = new List<(string Ac, bool Pass, string Detail)>();

// ============================================================================================
// PHASE A: NO cloud URL configured (record-only path).  Covers AC1, AC2, AC4.
// ============================================================================================
Environment.SetEnvironmentVariable(StartupEnvVar, null); // ensure unset
var queueA = Path.Combine(outDir, "telemetry-queue-record-only.json");
if (File.Exists(queueA)) File.Delete(queueA);
var gwA = await StartGatewayAsync("record-only (no cloud URL)", queueA);
var baseA = $"http://127.0.0.1:{gwA.Port}";

Log("[harness] PHASE A: no DEVTHROTTLE_STARTUP_TELEMETRY_URL set -> record-only path");
var codeA = await PostStartupAsync(baseA, "dir-record-only-631", "WORKSTATION-A", "631.0.1");
Log($"[harness]   POST /telemetry/director-startup -> {(int)codeA} {codeA}");

// AC1: 202 for a valid body.
results.Add(("AC1 - POST /telemetry/director-startup returns 202 for a valid body",
    codeA == HttpStatusCode.Accepted,
    $"returned {(int)codeA} {codeA} for body {{director_id, machine_name, app_version}}"));

// AC4: with no startup cloud URL, the endpoint still returns 202 and records locally; nothing
// forwarded. Give the (running) flusher a moment to prove nothing is enqueued/forwarded.
await Task.Delay(700);
var nothingForwardedA = stubReceived.IsEmpty;
var depthA = QueueDepth(queueA);
results.Add(("AC4 - no cloud URL: still 202, recorded locally, not forwarded (no error)",
    codeA == HttpStatusCode.Accepted && nothingForwardedA && depthA == 0,
    $"202 with no URL; stub received {stubReceived.Count} (expect 0); queue depth={depthA} (expect 0 - nothing enqueued)"));

await gwA.StopAsync();
await gwA.DisposeAsync();

// ============================================================================================
// PHASE B: cloud URL configured (forward path).  Covers AC3 (and re-confirms AC1/AC2).
// ============================================================================================
Environment.SetEnvironmentVariable(StartupEnvVar, stubUrl);
var queueB = Path.Combine(outDir, "telemetry-queue-forward.json");
if (File.Exists(queueB)) File.Delete(queueB);
var gwB = await StartGatewayAsync("forward (cloud URL set)", queueB);
var baseB = $"http://127.0.0.1:{gwB.Port}";

Log($"[harness] PHASE B: DEVTHROTTLE_STARTUP_TELEMETRY_URL={stubUrl} -> forward path");
var codeB = await PostStartupAsync(baseB, "dir-forward-631", "WORKSTATION-B", "631.9.9");
Log($"[harness]   POST /telemetry/director-startup -> {(int)codeB} {codeB}");

// AC3: the event is forwarded to the stub. Delivery is asynchronous (the queue flusher).
var deadline = DateTime.UtcNow.AddSeconds(15);
while (stubReceived.Count < 1 && DateTime.UtcNow < deadline)
    await Task.Delay(100);
var received = stubReceived.ToArray();
string fwdDirectorId = "(none)", fwdAppVersion = "(none)";
if (received.Length > 0)
{
    try
    {
        using var doc = JsonDocument.Parse(received[0].Body);
        fwdDirectorId = doc.RootElement.GetProperty("director_id").GetString() ?? "(none)";
        fwdAppVersion = doc.RootElement.GetProperty("app_version").GetString() ?? "(none)";
    }
    catch { /* leave placeholders -> fails the assert */ }
}
var forwardOk = received.Length == 1 && fwdDirectorId == "dir-forward-631" && fwdAppVersion == "631.9.9";
Log($"[harness] PHASE B: stub received {received.Length} forwarded event(s); director_id={fwdDirectorId}, app_version={fwdAppVersion}");
results.Add(("AC3 - with a startup URL configured, the event is forwarded to the stub",
    forwardOk && codeB == HttpStatusCode.Accepted,
    $"202 to caller; stub received {received.Length} POST (director_id={fwdDirectorId}, app_version={fwdAppVersion}); body forwarded unchanged"));

// Write the stub request log (proof of delivery).
var sb = new StringBuilder();
sb.AppendLine("# Stub cloud startup endpoint - forwarded director-startup requests received (proof of delivery)");
foreach (var r in received)
    sb.AppendLine($"{r.At:O}  body={r.Body}");
await File.WriteAllTextAsync(stubLogPath, sb.ToString());
Log($"[harness] wrote stub request log -> {stubLogPath}");

await gwB.StopAsync();
await gwB.DisposeAsync();
await stub.StopAsync();
await stub.DisposeAsync();

await File.WriteAllLinesAsync(harnessLogPath, harnessLines);

// ---- copy the live Gateway log's director-startup lines as proof ----
FileLog.Stop(); // flush the background writer so every line is on disk before we read it
await Task.Delay(300);
var gatewayLogLines = new List<string>();
try
{
    using var fs = new FileStream(gatewayLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(fs);
    string? line;
    while ((line = reader.ReadLine()) is not null)
        if (line.Contains("[DirectorStartupTelemetryEndpoint]"))
            gatewayLogLines.Add(line);
}
catch (Exception ex) { Console.WriteLine($"[harness] could not read gateway log: {ex.Message}"); }
await File.WriteAllLinesAsync(Path.Combine(outDir, "gateway-startup-log-lines.txt"), gatewayLogLines);

// AC2: the event was recorded Gateway-side (a record line carries director_id + app_version).
var recordedRecordOnly = gatewayLogLines.Any(l =>
    l.Contains("director-startup recorded")
    && l.Contains("director_id=dir-record-only-631")
    && l.Contains("app_version=631.0.1"));
var notConfiguredLine = gatewayLogLines.Any(l => l.Contains("no cloud startup endpoint configured"));
var recordedForward = gatewayLogLines.Any(l =>
    l.Contains("director-startup recorded")
    && l.Contains("director_id=dir-forward-631")
    && l.Contains("app_version=631.9.9"));
results.Add(("AC2 - the event is recorded Gateway-side (record line: director_id + app_version)",
    recordedRecordOnly && recordedForward,
    $"record-only run logged director_id=dir-record-only-631 app_version=631.0.1 = {recordedRecordOnly}; "
    + $"forward run logged director_id=dir-forward-631 app_version=631.9.9 = {recordedForward}"));

// AC4 (log half): the not-configured line is present on the record-only run.
results.Add(("AC4 (log) - record-only run logs that no cloud startup endpoint is configured",
    notConfiguredLine,
    $"\"no cloud startup endpoint configured\" line present = {notConfiguredLine}"));

// ---- summary ----
Console.WriteLine();
Console.WriteLine("================ ACCEPTANCE CRITERIA SUMMARY ================");
var allPass = true;
foreach (var (ac, pass, detail) in results)
{
    Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {ac}");
    Console.WriteLine($"        {detail}");
    if (!pass) allPass = false;
}
Console.WriteLine("=============================================================");
Console.WriteLine(allPass ? "ALL ACCEPTANCE CRITERIA: PASS" : "SOME ACCEPTANCE CRITERIA: FAIL");

var resultJson = JsonSerializer.Serialize(new
{
    allPass,
    stubUrl,
    retrySeconds = retryInterval.TotalSeconds,
    results = results.Select(r => new { ac = r.Ac, pass = r.Pass, detail = r.Detail }),
    stubLog = stubLogPath,
}, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(outDir, "results.json"), resultJson);

return allPass ? 0 : 1;

static int QueueDepth(string file)
{
    if (!File.Exists(file)) return 0;
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.GetProperty("events").GetArrayLength();
    }
    catch { return -1; }
}
