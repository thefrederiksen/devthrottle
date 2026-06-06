// HTTP-level benchmark of the NEW GET /screenshots against the REAL screenshots folder.
// Boots a real ControlApiHost (same as the endpoint tests) with CC_DIRECTOR_ROOT in a temp
// dir and config.json mapping the screenshots folder to the real one, then times requests.
using System.Diagnostics;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;

var realShots = args.Length > 0 ? args[0] : @"D:\Personal\OneDrive\Pictures\Screenshots";

var root = Path.Combine(Path.GetTempPath(), "ccd-shots-bench-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
Directory.CreateDirectory(CcStorage.Config());
await File.WriteAllTextAsync(
    CcStorage.ConfigJson(),
    JsonSerializer.Serialize(new { screenshots = new { source_directory = realShots } }));

using var sm = new SessionManager(new AgentOptions());
var host = new ControlApiHost(sm, "bench", () => Task.CompletedTask, useEphemeralPort: true);
var port = await host.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

// Warm-up (JIT + OS file cache), then measure.
_ = await client.GetStringAsync("screenshots");

for (var i = 1; i <= 5; i++)
{
    var sw = Stopwatch.StartNew();
    var body = await client.GetStringAsync("screenshots");
    sw.Stop();
    using var doc = JsonDocument.Parse(body);
    var items = doc.RootElement.GetProperty("items").GetArrayLength();
    var total = doc.RootElement.GetProperty("total").GetInt32();
    Console.WriteLine($"new build: {sw.Elapsed.TotalMilliseconds,6:N1} ms, {body.Length / 1024.0,5:N0} KB ({items} items of {total})");
}

await host.StopAsync();
try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
