using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;
using System.Text.Json;

namespace CcDirector.DeliveryQualification;

/// <summary>
/// THE SAME SHORT PROMPT SENT TWICE IN A ROW, ON A REAL CLAUDE CODE (Voice Delivery mission, phase 6, task C). On
/// 25 September 2026 (the owner's 09:26 case, and the rig's target 2) a prompt identical to words already visible
/// on screen could not be sent: the echo check counted copies of the text ANYWHERE on screen, and after one send
/// the prompt's own words were in the conversation above the composer, so the count demanded a new copy on the
/// whole screen - and the send was reported not-delivered with its text left sitting in the composer. The next
/// send then read that conversation as the composer and was refused forever (the wedge).
///
/// This sends the same short prompt three times to one real session. Each is counted in the agent's OWN
/// conversation file BY LINE TIMESTAMP, because the three prompts are identical and the file holds all of them:
/// the count for each send is the copies whose own line was written after that send began. The second and third
/// sends are the test: their words are already visible in the conversation above the composer.
/// </summary>
public static class RepeatShortPromptExperiment
{
    public static async Task<int> RunAsync(SessionManager manager, string args, string repo, string outDir)
    {
        var provenance = SubmissionProvenance.Typed("delivery-qa-rig", "rig");
        var session = manager.CreateSession(repo, AgentKind.ClaudeCode, args, SessionBackendType.ConPty, null);
        var lines = new List<string>();
        void Report(string line) { Console.WriteLine(line); lock (lines) lines.Add(line); }
        void Screen(string label) =>
            Report($"[repeat-short-prompt]   screen {label} ({session.CurrentCols}x{session.CurrentRows}):\n" +
                   string.Join("\n", session.SnapshotScreenRows().Select((r, i) => (r, i)).Where(x => x.r.Length > 0).Select(x => $"      {x.i,2}|{x.r}")));
        var problems = 0;
        // The identical text of every send. No marker: the whole point is that the words repeat.
        const string prompt = "Reply with only the word OK";
        try
        {
            var ready = await FirstPromptGate.WaitUntilAcceptingInputAsync(
                AgentKind.ClaudeCode, () => Frame(session), b => session.SendInput(b, null, provenance),
                () => session.ActivityState == ActivityState.Exited, TimeSpan.FromMinutes(3));
            Report($"[repeat-short-prompt] Claude Code ready: {ready.Outcome}, pid={session.ProcessId}");
            if (!await Rig.WaitUntilIdleAsync(session, TimeSpan.FromMinutes(2)))
                Report("[repeat-short-prompt] not idle after 2 minutes; going on");

            var results = new List<(string Label, string Outcome, double Seconds, DateTime Since)>();
            for (var i = 1; i <= 3; i++)
            {
                var label = i == 1 ? "send 1 (first time on screen)" : $"send {i} (identical, already visible above)";
                var since = DateTime.UtcNow;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string outcome;
                try
                {
                    if (i == 1)
                        await session.DeliverPrePromptAsync(prompt, TimeSpan.FromSeconds(60));
                    else
                        await session.SendTextAsync(prompt, provenance, SendSource.UserInput);
                    outcome = "OK";
                }
                catch (Exception ex)
                {
                    outcome = $"FAILED ({ex.GetType().Name}): {ex.Message}";
                }
                sw.Stop();
                Report($"[repeat-short-prompt] {label} after {sw.Elapsed.TotalSeconds:F1}s: {outcome}");
                results.Add((label, outcome, sw.Elapsed.TotalSeconds, since));
                if (!await Rig.WaitUntilIdleAsync(session, TimeSpan.FromMinutes(2)))
                    Report("[repeat-short-prompt] not idle 2 minutes after the send; counting anyway");
                Screen($"after send {i}");
            }

            // The truth, from the agent's own records, one count per send: identical prompts all live in the same
            // file, so a copy counts for the send whose line was written after it began and before the next one did.
            for (var i = 0; i < results.Count; i++)
            {
                var (label, outcome, seconds, since) = results[i];
                var until = i + 1 < results.Count ? results[i + 1].Since : DateTime.MaxValue;
                var copies = CopiesBetween(repo, since, until, prompt);
                var ok = outcome == "OK" && copies == 1;
                if (!ok) problems++;
                Report($"[repeat-short-prompt] RESULT {label}: director={(outcome == "OK" ? "OK" : "FAILED")} " +
                       $"send={seconds:F1}s copies-in-records-in-its-own-window={copies} {(ok ? "PASS" : "FAIL")}");
            }
        }
        finally
        {
            try { await manager.KillSessionAsync(session.Id); } catch (Exception ex) { Console.WriteLine($"[repeat-short-prompt] kill: {ex.Message}"); }
            manager.RemoveSession(session.Id);
            lock (lines) File.WriteAllLines(Path.Combine(outDir, "repeat-short-prompt-summary.txt"), lines);
        }
        Report(problems == 0
            ? "[repeat-short-prompt] EVERY IDENTICAL SHORT PROMPT ARRIVED EXACTLY ONCE, ITS WORDS ALREADY VISIBLE IN THE CONVERSATION ABOVE."
            : $"[repeat-short-prompt] {problems} problem(s)");
        return problems == 0 ? 0 : 1;
    }

    /// <summary>How many copies of the text the agent's records hold on lines whose own timestamp falls in
    /// [<paramref name="sinceUtc"/>, <paramref name="untilUtc"/>) - one count per send of an identical prompt: the
    /// window of a send runs from its start to the next send's. An enqueue line and the user line Claude Code writes
    /// when it takes it mid-turn are ONE delivery, as in BusyExperiment.CopiesInClaudeRecords.</summary>
    private static int CopiesBetween(string repo, DateTime sinceUtc, DateTime untilUtc, string text)
    {
        var projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        var key = Path.GetFileName(repo.TrimEnd('\\', '/'));
        var copies = 0;
        foreach (var dir in Directory.EnumerateDirectories(projects).Where(d => Path.GetFileName(d).Contains("delivery-qa", StringComparison.OrdinalIgnoreCase)
                                                                          && Path.GetFileName(d).EndsWith(key, StringComparison.OrdinalIgnoreCase)))
        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl").Where(f => File.GetLastWriteTimeUtc(f) >= sinceUtc.AddSeconds(-5)))
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            var unpairedEnqueues = 0;
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains(text, StringComparison.Ordinal)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("timestamp", out var ts) || !DateTime.TryParse(ts.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var written))
                    continue;
                // The clock that wrote it is this machine's; two seconds of skew are allowed at the window's opening edge.
                if (written < sinceUtc - TimeSpan.FromSeconds(2) || written >= untilUtc) continue;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                var enqueue = type == "queue-operation" && root.TryGetProperty("operation", out var op) && op.GetString() == "enqueue";
                var user = type == "user" && !(root.TryGetProperty("isMeta", out var m) && m.ValueKind == JsonValueKind.True);
                if (enqueue) { copies++; unpairedEnqueues++; }
                else if (user && unpairedEnqueues > 0) unpairedEnqueues--;
                else if (user) copies++;
            }
        }
        return copies;
    }

    private static ScreenFrame Frame(Session s)
    {
        var (rows, row, col, visible, _) = s.SnapshotLiveScreen();
        return new ScreenFrame(rows, row, col, visible);
    }
}
