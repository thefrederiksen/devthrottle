// HQ-11 smoke: TWO HostedAgents in ONE process, each owning its own claude.exe.
//
// This host is a WINDOWLESS exe (OutputType WinExe) that writes its result log to a
// file itself. It must NOT be a console app with redirected stdio: hosting a ConPty
// from such a process broke the claude spawn live ("Input must be provided either
// through stdin or as a prompt argument when using --print") - the same family as
// the nested-ConPty trap. Launch directly via Task Scheduler:
//   Start-ScheduledTask hosted-agent-smoke-launch
// Result: smoke-output.txt next to the exe (last line PASS/FAIL).

using CcDirector.Core.Agents;
using CcDirector.HostedAgent;

var exeDir = AppContext.BaseDirectory;
var outFile = Path.Combine(exeDir, "smoke-output.txt");
File.WriteAllText(outFile, "");
void Log(string line) => File.AppendAllText(outFile, line + Environment.NewLine);

try
{
    // Working dirs live UNDER the repo tree: claude shows a trust-folder prompt for
    // never-seen locations (e.g. %TEMP%), and a modal prompt means no composer - the
    // echo-verified submit then fails by design. Subdirectories of an already-trusted
    // project skip the prompt. (Live finding from smoke v2.)
    var baseDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "sandboxes");
    var dirA = Path.GetFullPath(Path.Combine(baseDir, "brain-a"));
    var dirB = Path.GetFullPath(Path.Combine(baseDir, "brain-b"));
    Directory.CreateDirectory(dirA);
    Directory.CreateDirectory(dirB);

    Log($"[smoke] host process pid={Environment.ProcessId}");

    using var brainA = HostedAgent.For(AgentKind.ClaudeCode, new HostedAgentOptions { WorkingDirectory = dirA });
    using var brainB = HostedAgent.For(AgentKind.ClaudeCode, new HostedAgentOptions { WorkingDirectory = dirB });

    Log("[smoke] starting brain A and brain B in parallel...");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await Task.WhenAll(brainA.StartAsync(), brainB.StartAsync());
    Log($"[smoke] both ready in {sw.Elapsed.TotalSeconds:F1}s");
    Log($"[smoke] brain A: claude pid={brainA.ProcessId}, session={brainA.SessionId}");
    Log($"[smoke] brain B: claude pid={brainB.ProcessId}, session={brainB.SessionId}");

    if (brainA.ProcessId == brainB.ProcessId)
    {
        Log("[smoke] FAIL: both brains report the same claude pid");
        return 1;
    }

    Log("[smoke] asking both in parallel...");
    var askA = brainA.AskAsync("Reply with exactly one word: ALPHA");
    var askB = brainB.AskAsync("Reply with exactly one word: BRAVO");
    await Task.WhenAll(askA, askB);
    Log($"[smoke] A answered: {askA.Result.Text.Trim()} ({askA.Result.ReplySeconds:F1}s)");
    Log($"[smoke] B answered: {askB.Result.Text.Trim()} ({askB.Result.ReplySeconds:F1}s)");

    if (!askA.Result.Text.Contains("ALPHA") || !askB.Result.Text.Contains("BRAVO"))
    {
        Log("[smoke] FAIL: unexpected answers");
        return 1;
    }

    Log("[smoke] killing brain A; brain B must stay alive and answer...");
    await brainA.KillAsync();
    var healthB = await brainB.GetHealthAsync();
    Log($"[smoke] brain B after A's death: alive={healthB.IsAlive}");
    var follow = await brainB.AskAsync("Reply with exactly one word: STILL-HERE");
    Log($"[smoke] B follow-up: {follow.Text.Trim()}");
    await brainB.KillAsync();

    if (!healthB.IsAlive || !follow.Text.Contains("STILL-HERE"))
    {
        Log("[smoke] FAIL: brain B did not survive independently");
        return 1;
    }

    Log("[smoke] PASS: two independent hosted brains in one process");
    return 0;
}
catch (Exception ex)
{
    Log($"[smoke] FAIL: {ex}");
    return 1;
}
