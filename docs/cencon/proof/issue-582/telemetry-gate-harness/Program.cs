using System.Text;
using CcDirector.Core.Account;
using CcDirector.Core.Configuration;

// Proof harness for issue #582: exercises the usage-telemetry toggle gate and read-back against an
// isolated config root, so it proves the behavior without touching the user's real config.json.
//   AC3 (read-back): a persisted telemetry.enabled value reads back the same on a fresh read - the
//        "after a restart" case (a fresh process reads exactly what was written to config.json).
//   AC4 (gate): with the toggle OFF, UsageTelemetry.Record does NOT write and returns false, while
//        the always-on authentication floor (AuthEventLog) still records; with the toggle ON, the
//        richer usage path writes.
// ASCII output only.

var root = Path.Combine(Path.GetTempPath(), "cc-dt-582-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);

var outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var outFile = Path.Combine(outDir, "telemetry-gate-output.txt");
var sb = new StringBuilder();

void Line(string s) { Console.WriteLine(s); sb.AppendLine(s); }

Line("===== Issue #582 telemetry gate proof =====");
// Redact the temp path's user profile prefix so the committed output never carries a username.
var tempPrefix = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
var redactedRoot = root.Replace(tempPrefix, "%TEMP%", StringComparison.OrdinalIgnoreCase);
Line($"Isolated config root: {redactedRoot}");
Line("");

// --- AC3: default is ON, then persist OFF and read it back fresh ---
Line($"AC3 default (no persisted value)        -> IsEnabled() = {TelemetrySettings.IsEnabled()}  (expected True)");
TelemetrySettings.SetEnabled(false);
Line($"AC3 after SetEnabled(false), fresh read -> IsEnabled() = {TelemetrySettings.IsEnabled()}  (expected False - the restart case)");
var cfgPath = Path.Combine(root, "config", "config.json");
Line($"AC3 config.json contents                -> {File.ReadAllText(cfgPath).Replace(Environment.NewLine, " ")}");
Line("");

// --- AC4: with toggle OFF, richer usage is gated; auth floor still fires ---
var usageOffSink = Path.Combine(root, "usage-off.jsonl");
var authLog = new AuthEventLog(Path.Combine(root, "auth-off.jsonl"));
var usageOff = new UsageTelemetry(isEnabled: TelemetrySettings.IsEnabled, sinkPath: usageOffSink);

var wroteOff = usageOff.Record("session-created");   // gated -> false, nothing written
authLog.RecordLoggedIn();                            // always-on floor -> still fires
authLog.RecordLoggedOut();
Line($"AC4 toggle OFF: UsageTelemetry.Record returned {wroteOff}  (expected False - richer usage suppressed)");
Line($"AC4 toggle OFF: richer usage sink written? {File.Exists(usageOffSink)}  (expected False)");
var authEvents = authLog.ReadAll();
Line($"AC4 toggle OFF: authentication-floor events recorded = {authEvents.Count}  (expected 2: logged-in + logout - still fire)");
foreach (var e in authEvents)
    Line($"      auth event: kind={e.Kind} at={e.At:o}");
Line("");

// --- AC4: with toggle ON, richer usage fires ---
TelemetrySettings.SetEnabled(true);
var usageOnSink = Path.Combine(root, "usage-on.jsonl");
var usageOn = new UsageTelemetry(isEnabled: TelemetrySettings.IsEnabled, sinkPath: usageOnSink);
var wroteOn = usageOn.Record("session-created");
Line($"AC4 toggle ON:  UsageTelemetry.Record returned {wroteOn}  (expected True - richer usage fires)");
var usageEvents = usageOn.ReadAll();
Line($"AC4 toggle ON:  richer usage events recorded = {usageEvents.Count}  (expected 1)");
foreach (var e in usageEvents)
    Line($"      usage event: name={e.Name} at={e.At:o}");
Line("");
Line("===== proof complete =====");

File.WriteAllText(outFile, sb.ToString());
Console.WriteLine($"[harness] wrote {outFile}");

try { Directory.Delete(root, recursive: true); } catch { /* best effort temp cleanup */ }
