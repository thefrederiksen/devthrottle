using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;

// Proof harness for issue #369: boots a REAL GatewayHost exactly the way the production tray
// runs it - authEnabled: false, so the global AuthMiddleware is OFF and only the new
// endpoint-level voice-turn token gate stands between the network and the endpoints.
// Tailscale is disabled (CC_GATEWAY_NO_TAILSCALE=1) and the instances dir is isolated, so
// nothing on the machine is touched. Every request in the trace is a REAL curl.exe process
// (a separate OS process hitting the Gateway over loopback HTTP), per the proof plan.
//
// 202 path: a Director is registered via the real POST /directors/register endpoint and the
// session is seeded into the SessionOwnerCache (the production fast path). The submit then
// answers 202 Accepted - the genuine endpoint response; the background Director call landing
// in the error stage afterwards is the documented async contract and irrelevant to auth.
// (The full 202 -> reply path against a real Director is covered by
// GatewayVoiceTurnAsyncTests, which now authenticate every request.)

Environment.SetEnvironmentVariable("CC_GATEWAY_NO_TAILSCALE", "1");

const string token = "proof-token-369";
var instancesDir = Path.Combine(Path.GetTempPath(), "cc-proof-369-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(instancesDir);

var gateway = new GatewayHost(port: FreePort(), token: token, authEnabled: false,
    instancesDirectory: instancesDir,
    workListsPath: Path.Combine(instancesDir, "worklists.json"));
await gateway.StartAsync();
var baseUrl = $"http://127.0.0.1:{gateway.Port}";

var sb = new StringBuilder();
void Log(string s) { Console.WriteLine(s); sb.AppendLine(s); }

Log($"GATEWAY: real GatewayHost, PRODUCTION auth mode (authEnabled=false -> global middleware OFF)");
Log($"LISTENING: {baseUrl}  (loopback, Tailscale disabled, isolated instances dir)");
Log($"TOKEN: {token}");
Log("");

// Seed an owning Director for the session (the SessionOwnerCache fast path) so an
// authenticated submit reaches the 202. The registration goes through the real endpoint.
var directorId = Guid.NewGuid().ToString();
using (var http = new HttpClient())
{
    var reg = await http.PostAsync($"{baseUrl}/directors/register",
        new StringContent(JsonSerializer.Serialize(new DirectorRegistrationRequest
        {
            DirectorId = directorId,
            TailnetEndpoint = $"http://127.0.0.1:{FreePort()}",
            MachineName = "proof-369-machine",
        }), Encoding.UTF8, "application/json"));
    Log($"SETUP: POST /directors/register -> {(int)reg.StatusCode} {reg.StatusCode} (fake owning Director {directorId[..8]}...)");
}
var sid = Guid.NewGuid().ToString();
gateway.SessionOwners.Remember(sid, directorId);
Log($"SETUP: session {sid} owner-cached to that Director");
Log("");

// ---- the curl trace ---------------------------------------------------------

async Task<string> Curl(string label, string args)
{
    var psi = new ProcessStartInfo("curl.exe", $"-s -i {args}")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    var p = Process.Start(psi)
        ?? throw new InvalidOperationException("curl.exe failed to start");
    var output = await p.StandardOutput.ReadToEndAsync();
    await p.WaitForExitAsync();
    Log($"=== {label} ===");
    Log($"$ curl -s -i {args}");
    Log(output.TrimEnd());
    Log("");
    return output;
}

void Check(string output, string mustContain, string what)
{
    var ok = output.Contains(mustContain, StringComparison.OrdinalIgnoreCase);
    Log($"    ASSERT {(ok ? "PASS" : "FAIL")}: {what}");
    Log("");
    if (!ok) { Console.Error.WriteLine($"PROOF FAILED: {what}"); Environment.Exit(2); }
}

// AC1: submit with NO token -> 401
var o1 = await Curl("AC1: submit, NO token",
    $"-X POST {baseUrl}/sessions/{sid}/voice-turn/submit -H \"Content-Type: application/json\" -d \"{{\\\"text\\\":\\\"hello\\\"}}\"");
Check(o1, "401", "submit without token answers 401");
Check(o1, "missing or invalid token", "401 body names the reason");

// AC1b: submit with a WRONG token -> 401
var o2 = await Curl("AC1b: submit, WRONG token",
    $"-X POST {baseUrl}/sessions/{sid}/voice-turn/submit -H \"Authorization: Bearer wrong-token\" -H \"Content-Type: application/json\" -d \"{{\\\"text\\\":\\\"hello\\\"}}\"");
Check(o2, "401", "submit with wrong token answers 401");

// AC3: submit with the VALID token (the header the phone sends via DirectorVoiceClient.NewClient) -> 202
var o3 = await Curl("AC3: submit, VALID Bearer token",
    $"-X POST {baseUrl}/sessions/{sid}/voice-turn/submit -H \"Authorization: Bearer {token}\" -H \"Content-Type: application/json\" -d \"{{\\\"text\\\":\\\"hello\\\"}}\"");
Check(o3, "202", "submit with valid token answers 202 Accepted");
Check(o3, "turn_id", "202 body carries turn_id");

// Extract the turn id for the poll legs.
var turnId = JsonDocument.Parse(o3[o3.IndexOf('{')..]).RootElement.GetProperty("turn_id").GetString()
    ?? throw new InvalidOperationException("202 body had no turn_id string");
Log($"    turn_id = {turnId}");
Log("");

// AC2: poll with NO token -> 401 (even for a REAL turn id - existence never leaks)
var o4 = await Curl("AC2: poll, NO token",
    $"{baseUrl}/sessions/{sid}/voice-turn/{turnId}");
Check(o4, "401", "poll without token answers 401");

// AC2b: poll with a WRONG token -> 401
var o5 = await Curl("AC2b: poll, WRONG token",
    $"{baseUrl}/sessions/{sid}/voice-turn/{turnId} -H \"Authorization: Bearer wrong-token\"");
Check(o5, "401", "poll with wrong token answers 401");

// AC3b: poll with the VALID token -> 200 with the job stage
var o6 = await Curl("AC3b: poll, VALID Bearer token",
    $"{baseUrl}/sessions/{sid}/voice-turn/{turnId} -H \"Authorization: Bearer {token}\"");
Check(o6, "200", "poll with valid token answers 200");
Check(o6, "stage", "200 body carries the job stage");

Log("ALL ASSERTIONS PASSED.");

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
