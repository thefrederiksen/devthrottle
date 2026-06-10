using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CcDirector.Gateway;

// Proof harness for issue #273: boots a REAL GatewayHost (the same host the tray runs)
// with auth on and Tailscale off (CC_GATEWAY_NO_TAILSCALE=1, isolated instances dir) on a free
// loopback port, then drives the full /lists CRUD + ordering + single-consumer claim contract
// over real HTTP, capturing every request/response pair. Criterion 8 (cross-process reachability)
// is proven by spawning a SEPARATE process (PowerShell + Invoke-WebRequest) that hits the same
// Gateway from outside this process and printing its result.

Environment.SetEnvironmentVariable("CC_GATEWAY_NO_TAILSCALE", "1");

const string token = "proof-token-273";
var instancesDir = Path.Combine(Path.GetTempPath(), "cc-proof-273-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(instancesDir);

int port = FreePort();
var gateway = new GatewayHost(port: port, token: token, authEnabled: true, instancesDirectory: instancesDir);
await gateway.StartAsync();

var baseUrl = $"http://127.0.0.1:{gateway.Port}";
var sb = new StringBuilder();
void Log(string s) { Console.WriteLine(s); sb.AppendLine(s); }

Log($"GATEWAY: real GatewayHost (auth ON, Tailscale OFF) listening on {baseUrl}");
Log($"AUTH: Bearer {token}");
Log("");

using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

var jsonOpts = new JsonSerializerOptions { WriteIndented = false };

async Task<(HttpStatusCode code, string body)> Send(string method, string path, string? json, string criterion)
{
    var req = new HttpRequestMessage(new HttpMethod(method), path);
    if (json is not null)
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
    var resp = await http.SendAsync(req);
    var body = await resp.Content.ReadAsStringAsync();
    Log($"[{criterion}] {method} {path}");
    if (json is not null) Log($"    REQ  {json}");
    Log($"    RESP {(int)resp.StatusCode} {resp.StatusCode}  {body}");
    Log("");
    return (resp.StatusCode, body);
}

void Check(bool ok, string what)
{
    Log($"    ASSERT {(ok ? "PASS" : "FAIL")}: {what}");
    Log("");
    if (!ok) { Console.Error.WriteLine($"PROOF FAILED: {what}"); Environment.Exit(2); }
}

// ---- Criterion 1: POST /lists creates; GET /lists returns it ----
Log("=== CRITERION 1: create a named list; GET /lists returns it ===");
var (c1a, _) = await Send("POST", "/lists", "{\"name\":\"backlog\"}", "C1");
Check(c1a == HttpStatusCode.OK, "POST /lists -> 200");
var (c1b, allBody) = await Send("GET", "/lists", null, "C1");
Check(c1b == HttpStatusCode.OK && allBody.Contains("\"backlog\""), "GET /lists contains backlog");

// ---- Criterion 2: append structured ref; round-trips source/id/area in append order ----
Log("=== CRITERION 2: append structured refs; round-trip source/id/area in order ===");
await Send("POST", "/lists/backlog/items", "{\"source\":\"github\",\"id\":\"262\",\"area\":\"Gateway\"}", "C2");
await Send("POST", "/lists/backlog/items", "{\"source\":\"github\",\"id\":\"263\",\"area\":\"Core\"}", "C2");
await Send("POST", "/lists/backlog/items", "{\"source\":\"github\",\"id\":\"264\"}", "C2");
var (_, c2get) = await Send("GET", "/lists/backlog", null, "C2");
using (var d = JsonDocument.Parse(c2get))
{
    var items = d.RootElement.GetProperty("items");
    var ids = items.EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToArray();
    Check(ids.SequenceEqual(new[] { "262", "263", "264" }), "ids in append order 262,263,264");
    Check(items[0].GetProperty("source").GetString() == "github", "item0 source=github round-tripped");
    Check(items[0].GetProperty("area").GetString() == "Gateway", "item0 area=Gateway round-tripped");
}

// ---- Criterion 3: mixed-source refs all stored in order ----
Log("=== CRITERION 3: mixed-source refs coexist in one ordered list ===");
await Send("POST", "/lists", "{\"name\":\"mixed\"}", "C3");
await Send("POST", "/lists/mixed/items", "{\"source\":\"github\",\"id\":\"262\"}", "C3");
await Send("POST", "/lists/mixed/items", "{\"source\":\"devops\",\"id\":\"1203\"}", "C3");
await Send("POST", "/lists/mixed/items", "{\"source\":\"jira\",\"id\":\"CCD-44\"}", "C3");
var (_, c3get) = await Send("GET", "/lists/mixed", null, "C3");
using (var d = JsonDocument.Parse(c3get))
{
    var items = d.RootElement.GetProperty("items");
    var sources = items.EnumerateArray().Select(i => i.GetProperty("source").GetString()).ToArray();
    var ids = items.EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToArray();
    Check(sources.SequenceEqual(new[] { "github", "devops", "jira" }), "sources in order github,devops,jira");
    Check(ids.SequenceEqual(new[] { "262", "1203", "CCD-44" }), "ids in order 262,1203,CCD-44 (no source rejected)");
}

// ---- Criterion 4: PATCH reorders ----
Log("=== CRITERION 4: PATCH reorders the list ===");
await Send("PATCH", "/lists/backlog/items",
    "[{\"source\":\"github\",\"id\":\"264\"},{\"source\":\"github\",\"id\":\"262\"},{\"source\":\"github\",\"id\":\"263\"}]", "C4");
var (_, c4get) = await Send("GET", "/lists/backlog", null, "C4");
using (var d = JsonDocument.Parse(c4get))
{
    var ids = d.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToArray();
    Check(ids.SequenceEqual(new[] { "264", "262", "263" }), "order now 264,262,263");
}

// ---- Criterion 5: DELETE by source+id removes one, keeps order ----
Log("=== CRITERION 5: DELETE /lists/{name}/items/{source}/{id} removes one, keeps rest ===");
await Send("DELETE", "/lists/mixed/items/devops/1203", null, "C5");
var (_, c5get) = await Send("GET", "/lists/mixed", null, "C5");
using (var d = JsonDocument.Parse(c5get))
{
    var ids = d.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToArray();
    Check(ids.SequenceEqual(new[] { "262", "CCD-44" }), "devops/1203 gone; github/262 and jira/CCD-44 keep order");
}

// ---- Criterion 6: single-consumer claim; double-claim refused; release; re-claim ----
Log("=== CRITERION 6: single-consumer claim / double-claim refused / release / re-claim ===");
var (c6a, _) = await Send("POST", "/lists/backlog/consumer", null, "C6");
Check(c6a == HttpStatusCode.OK, "first claim -> 200");
var (c6b, _) = await Send("POST", "/lists/backlog/consumer", null, "C6");
Check(c6b == HttpStatusCode.Conflict, "second claim while held -> 409");
var (c6c, _) = await Send("DELETE", "/lists/backlog/consumer", null, "C6");
Check(c6c == HttpStatusCode.OK, "release -> 200");
var (c6d, _) = await Send("POST", "/lists/backlog/consumer", null, "C6");
Check(c6d == HttpStatusCode.OK, "re-claim after release -> 200");

// ---- Criterion 7: payload has no status/flow field (list or item) ----
Log("=== CRITERION 7: stored list has NO item-status field ===");
var (_, c7get) = await Send("GET", "/lists/mixed", null, "C7");
using (var d = JsonDocument.Parse(c7get))
{
    var listFields = d.RootElement.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToArray();
    Check(!listFields.Contains("status") && !listFields.Contains("flow"),
        $"list fields {{{string.Join(",", listFields)}}} contain no status/flow");
    bool anyItemStatus = d.RootElement.GetProperty("items").EnumerateArray()
        .SelectMany(i => i.EnumerateObject().Select(p => p.Name.ToLowerInvariant()))
        .Any(n => n == "status" || n == "flow");
    Check(!anyItemStatus, "no per-item status/flow field");
}

// ---- Criterion 8: request from OUTSIDE this process (separate PowerShell process) ----
Log("=== CRITERION 8: request issued from a SEPARATE process (cross-process reachability) ===");
var psScript =
    $"$ErrorActionPreference='Stop'; " +
    $"$r = Invoke-WebRequest -Uri '{baseUrl}/lists' -Headers @{{ Authorization = 'Bearer {token}' }} -UseBasicParsing; " +
    $"Write-Output ('STATUS=' + [int]$r.StatusCode); Write-Output ('BODY=' + $r.Content)";
var psi = new ProcessStartInfo("powershell.exe",
    $"-NoProfile -NonInteractive -Command \"{psScript}\"")
{
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
};
var proc = Process.Start(psi)!;
string psOut = await proc.StandardOutput.ReadToEndAsync();
string psErr = await proc.StandardError.ReadToEndAsync();
await proc.WaitForExitAsync();
Log($"    EXTERNAL PROCESS pid={proc.Id} (powershell.exe, separate from gateway pid={Environment.ProcessId})");
Log($"    {psOut.Trim().Replace("\n", "\n    ")}");
if (!string.IsNullOrWhiteSpace(psErr)) Log($"    STDERR {psErr.Trim()}");
Check(proc.ExitCode == 0 && psOut.Contains("STATUS=200") && psOut.Contains("backlog"),
    "external process reached the Gateway /lists and got 200 with the list");

Log("");
Log("ALL CRITERIA PASSED (1-8).");

await gateway.StopAsync();
try { Directory.Delete(instancesDir, true); } catch { }

File.WriteAllText(args.Length > 0 ? args[0] : "proof-output.txt", sb.ToString());
Console.WriteLine("\nProof transcript written.");
return 0;

static int FreePort()
{
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    int p = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return p;
}
