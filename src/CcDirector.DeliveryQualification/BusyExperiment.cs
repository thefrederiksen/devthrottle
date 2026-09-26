using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;

namespace CcDirector.DeliveryQualification;

/// <summary>
/// THE BUSY-AGENT SEND ON A REAL AGENT (Voice Delivery mission, phase 3). On 25 September 2026 at 09:05 a spoken prompt
/// sent to a working Claude Code waited 122 seconds before its Enter, because the agent's spinner never let the terminal
/// go quiet. This starts a real Claude Code, sets it working on a long shell command, and while its spinner runs sends
/// each text through the Director's own send path - timing the send, and then counting how many times the words are in
/// the agent's OWN conversation file (a queued prompt is written there as an "enqueue" line and again as a user line
/// when Claude Code takes it; an idle one as a user line alone). One is the only right answer.
/// </summary>
public static class BusyExperiment
{
    /// <summary>What the prompt verb waits before it answers; see SessionCommandExecutor.PromptAnswerBudget.</summary>
    private static readonly TimeSpan VerbBudget = TimeSpan.FromSeconds(20);

    public static async Task<int> RunAsync(SessionManager manager, string args, string repo, IReadOnlyList<TextShape> shapes, string outDir)
    {
        var provenance = SubmissionProvenance.Typed("delivery-qa-rig", "rig");
        var session = manager.CreateSession(repo, AgentKind.ClaudeCode, args, SessionBackendType.ConPty, null);
        var lines = new List<string>();
        void Report(string line) { Console.WriteLine(line); lock (lines) lines.Add(line); }
        var failures = 0;
        try
        {
            var ready = await FirstPromptGate.WaitUntilAcceptingInputAsync(
                AgentKind.ClaudeCode, () => Frame(session), b => session.SendInput(b, null, provenance),
                () => session.ActivityState == ActivityState.Exited, TimeSpan.FromMinutes(3));
            Report($"[busy] Claude Code ready: {ready.Outcome}");

            foreach (var shape in shapes)
            {
                if (!await Rig.WaitUntilIdleAsync(session, TimeSpan.FromMinutes(3)))
                    Report("[busy] still not idle after 3 minutes; sending the busy task anyway");

                // Set it working: a shell command that runs for a minute, so the spinner is up for the whole send.
                try
                {
                    await session.SendTextAsync("Run exactly this shell command and nothing else, then reply with the word FINISHED: sleep 60", provenance);
                }
                catch (Exception ex)
                {
                    Report($"[busy] {shape.Name}: the task that sets the agent working was not delivered - skipped: {ex.Message}");
                    failures++;
                    continue;
                }
                var workingSince = await WaitUntilWorkingOnScreenAsync(session, TimeSpan.FromMinutes(2));
                if (workingSince is null)
                {
                    Report($"[busy] {shape.Name}: the agent never showed it was working - skipped");
                    failures++;
                    continue;
                }
                await Task.Delay(TimeSpan.FromSeconds(3));

                var token = $"Q{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}";
                var text = shape.Build(token);
                var sentAt = DateTime.UtcNow;
                var stateAtSend = session.ActivityState;
                var workingAtSend = DoorbellSafety.ShowsWorking(session.SnapshotScreenRows());
                var sw = Stopwatch.StartNew();
                string director = "OK", detail = "";
                // What the Director's composer reader sees while the send runs, once a second - the evidence for what
                // "the paste was taken in" can mean on this agent's real screen.
                using var sampling = new CancellationTokenSource();
                var samples = Task.Run(async () =>
                {
                    while (!sampling.IsCancellationRequested)
                    {
                        var frame = Frame(session);
                        var (reading, held) = DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, frame);
                        var shown = (held.Length > 50 ? held[..50] + "..." : held).Replace('\n', ' ');
                        var sample = $"[busy]   t={sw.Elapsed.TotalSeconds:F1}s composer={reading} text=\"{shown}\" " +
                                     $"working-marker={DoorbellSafety.ShowsWorking(frame.Rows)} cursor={frame.CursorRow},{frame.CursorCol}";
                        lock (lines) lines.Add(sample);
                        try { await Task.Delay(1000, sampling.Token); } catch (TaskCanceledException) { break; }
                    }
                });
                var atSend = "[busy]   screen at send:\n" + string.Join("\n", Frame(session).Rows.Select((r, i) => $"      {i,2}|{r}"));
                lock (lines) lines.Add(atSend);
                try
                {
                    await session.SendTextAsync(text, provenance);
                }
                catch (Exception ex)
                {
                    director = "FAILED";
                    detail = $"{ex.GetType().Name}: {ex.Message}";
                }
                var sendSeconds = sw.Elapsed.TotalSeconds;
                sampling.Cancel();
                await samples;

                // The truth: the agent's own conversation file, given time for the queued prompt to run.
                var copies = 0;
                var deadline = DateTime.UtcNow.AddSeconds(150);
                while (DateTime.UtcNow < deadline)
                {
                    copies = CopiesInClaudeRecords(repo, sentAt, token);
                    if (copies > 0 && await Rig.WaitUntilIdleAsync(session, TimeSpan.FromSeconds(5))) break;
                    await Task.Delay(2000);
                }
                copies = CopiesInClaudeRecords(repo, sentAt, token);
                var verbAnswer = Math.Min(sendSeconds, VerbBudget.TotalSeconds);
                var verbWord = sendSeconds <= VerbBudget.TotalSeconds ? (director == "OK" ? "delivered" : "failed") : "delivering";
                var ok = director == "OK" && copies == 1;
                if (!ok) failures++;
                Report($"[busy] {shape.Name,-15} len={text.Length,-5} state-at-send={stateAtSend} screen-working={workingAtSend} " +
                       $"send={sendSeconds:F1}s verb-answer={verbAnswer:F1}s ({verbWord}) director={director} copies-in-records={copies} " +
                       $"{(ok ? "PASS" : "FAIL")} {detail}");
            }
        }
        finally
        {
            try { await manager.KillSessionAsync(session.Id); } catch (Exception ex) { Console.WriteLine($"[busy] kill: {ex.Message}"); }
            manager.RemoveSession(session.Id);
            lock (lines) File.WriteAllLines(Path.Combine(outDir, "busy-summary.txt"), lines);
        }
        Report(failures == 0 ? "[busy] EVERY BUSY SEND ARRIVED EXACTLY ONCE AND THE DIRECTOR SAID SO." : $"[busy] {failures} send(s) need attention");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>When the screen first shows Claude Code's working marker, or null.</summary>
    private static async Task<DateTime?> WaitUntilWorkingOnScreenAsync(Session session, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (DoorbellSafety.ShowsWorking(session.SnapshotScreenRows())) return DateTime.UtcNow;
            await Task.Delay(250);
        }
        return null;
    }

    /// <summary>
    /// How many times the token reached Claude Code's own records since <paramref name="sinceUtc"/>. Measured on this rig
    /// on 25 September 2026: a prompt queued while Claude Code runs a tool is written as an "enqueue" line and, seconds
    /// later when Claude Code takes it, as a user line - one delivery, two lines. So an enqueue and the user line after
    /// it count once; a user line with no enqueue before it counts once; the "remove" line and the "queued_command"
    /// attachment are the same prompt moving along and are not counted.
    /// </summary>
    internal static int CopiesInClaudeRecords(string repo, DateTime sinceUtc, string token)
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
                if (!line.Contains(token, StringComparison.Ordinal)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                var enqueue = type == "queue-operation" && root.TryGetProperty("operation", out var op) && op.GetString() == "enqueue";
                var user = type == "user" && !(root.TryGetProperty("isMeta", out var m) && m.ValueKind == JsonValueKind.True);
                // A queued prompt is written as an enqueue line and, when Claude Code takes it mid-turn, again as a user
                // line: that pair is ONE delivery. A user line with no enqueue before it is a delivery of its own.
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
