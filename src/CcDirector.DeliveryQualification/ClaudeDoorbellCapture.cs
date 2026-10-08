using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;

namespace CcDirector.DeliveryQualification;

/// <summary>
/// CAPTURE THE SCREENS THE DOORBELL MUST TELL APART WHILE A BACKGROUND TASK RUNS (issues 3186 and 3289). Claude Code
/// prints "esc to interrupt" in its footer while a background task or agent runs, even when the turn has ended and
/// the composer is empty, so the doorbell deferred those sessions as working for ever. The screens the fix is proven
/// against are not written from a guess: this starts a real Claude Code in the Director's own terminal, starts a
/// background shell task, and dumps each frame the doorbell has to judge - idle with the task running, the owner's
/// unsent words with the task running, and a turn running with the task running - together with the activity state
/// the Director's own detector reported at that moment.
/// </summary>
public static class ClaudeDoorbellCapture
{
    public static async Task<int> RunAsync(SessionManager manager, string args, string repo, string outDir, short cols, short rows)
    {
        var provenance = SubmissionProvenance.Typed("delivery-qa-rig", "rig");
        var session = manager.CreateSession(repo, AgentKind.ClaudeCode, args, SessionBackendType.ConPty, null);
        try
        {
            session.Resize(cols, rows);
            var ready = await FirstPromptGate.WaitUntilAcceptingInputAsync(
                AgentKind.ClaudeCode, () => Frame(session), b => session.SendInput(b, null, provenance),
                () => session.ActivityState == ActivityState.Exited, TimeSpan.FromMinutes(3));
            Console.WriteLine($"[claude-doorbell-capture] Claude Code ready: {ready.Outcome}, pid={session.ProcessId}");
            if (ready.Outcome != FirstPromptGateOutcome.Ready)
                return 1;
            await Rig.WaitUntilIdleAsync(session, TimeSpan.FromMinutes(1));
            Save(session, outDir, "claude-idle-placeholder-fresh");

            await session.SendTextAsync(
                "Use the Bash tool with run_in_background set to true to run exactly this command: sleep 900 . " +
                "Do not wait for it and do not check on it. Then reply with only the word STARTED.", provenance);
            await WaitForTurnAsync(session);
            await Task.Delay(TimeSpan.FromSeconds(5));
            Save(session, outDir, "claude-idle-background-task");

            const string draft = "a sentence the owner has not sent";
            session.SendInput(Encoding.ASCII.GetBytes(draft), null, provenance);
            await Task.Delay(TimeSpan.FromSeconds(3));
            Save(session, outDir, "claude-owner-text-background-task");
            session.SendInput(Enumerable.Repeat((byte)0x7f, draft.Length).ToArray(), null, provenance);
            await Task.Delay(TimeSpan.FromSeconds(3));

            await session.SendTextAsync(
                "Run exactly this shell command in the foreground, not in the background, and nothing else, " +
                "then reply with the word FINISHED: python -c \"import time; time.sleep(45)\"", provenance);
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
            while (session.ActivityState is not ActivityState.Working && DateTime.UtcNow < deadline)
                await Task.Delay(200);
            await Task.Delay(TimeSpan.FromSeconds(12));
            Save(session, outDir, "claude-working-background-task");
            await Task.Delay(TimeSpan.FromSeconds(3));
            Save(session, outDir, "claude-working-background-task-later");
            await WaitForTurnAsync(session);
            await Task.Delay(TimeSpan.FromSeconds(5));
            Save(session, outDir, "claude-idle-background-task-after-second-turn");

            await session.SendTextAsync(
                "Use the Monitor tool to watch this command, which prints one line every twenty seconds: " +
                "python -c \"import time; [time.sleep(20) or print('tick', flush=True) for _ in range(99)]\" . " +
                "Do not wait for any event. Then reply with only the word WATCHING.", provenance);
            await WaitForTurnAsync(session);
            await Task.Delay(TimeSpan.FromSeconds(5));
            Save(session, outDir, "claude-idle-monitor");
            await Task.Delay(TimeSpan.FromMilliseconds(120));
            Save(session, outDir, "claude-idle-monitor-second-frame");

            const string draft2 = "owner words while a monitor runs";
            session.SendInput(Encoding.ASCII.GetBytes(draft2), null, provenance);
            await Task.Delay(TimeSpan.FromSeconds(3));
            Save(session, outDir, "claude-owner-text-monitor");
            session.SendInput(Enumerable.Repeat((byte)0x7f, draft2.Length).ToArray(), null, provenance);
            await Task.Delay(TimeSpan.FromSeconds(3));

            await session.SendTextAsync(
                "Run exactly this shell command in the foreground and nothing else, then reply with the word DONE: " +
                "python -c \"import time; time.sleep(45)\"", provenance);
            deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
            while (session.ActivityState is not ActivityState.Working && DateTime.UtcNow < deadline)
                await Task.Delay(200);
            await Task.Delay(TimeSpan.FromSeconds(12));
            Save(session, outDir, "claude-working-monitor");
            await Task.Delay(TimeSpan.FromMilliseconds(120));
            Save(session, outDir, "claude-working-monitor-second-frame");
            await WaitForTurnAsync(session);
            return 0;
        }
        finally
        {
            try { await manager.KillSessionAsync(session.Id); } catch (Exception ex) { Console.WriteLine($"[claude-doorbell-capture] kill: {ex.Message}"); }
            manager.RemoveSession(session.Id);
        }
    }

    /// <summary>Wait for the prompt's turn to start and then end, by the Director's own activity state.</summary>
    private static async Task WaitForTurnAsync(Session session)
    {
        var started = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (session.ActivityState is not ActivityState.Working && DateTime.UtcNow < started)
            await Task.Delay(200);
        if (!await Rig.WaitUntilIdleAsync(session, TimeSpan.FromMinutes(4)))
            Console.WriteLine("[claude-doorbell-capture] the turn did not end within 4 minutes; capturing anyway");
    }

    private static void Save(Session session, string outDir, string label)
    {
        var (screenRows, cursorRow, cursorCol, cursorVisible, alternate) = session.SnapshotLiveScreen();
        var frame = session.SnapshotLiveFrame();
        var verdict = DoorbellSafety.CheckFrame(AgentKind.ClaudeCode, frame);
        var fixture = new Dictionary<string, object?>
        {
            ["label"] = label,
            ["activity"] = session.ActivityState.ToString(),
            ["status"] = session.Status.ToString(),
            ["cursorRow"] = cursorRow,
            ["cursorCol"] = cursorCol,
            ["cursorVisible"] = cursorVisible,
            ["alternateScreen"] = alternate,
            ["rows"] = screenRows,
        };
        var path = Path.Combine(outDir, label + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(fixture, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[claude-doorbell-capture] {label}: activity={session.ActivityState} cursor={cursorRow},{cursorCol} " +
                          $"visible={cursorVisible} verdict={(verdict.Ring ? "RING" : verdict.Reason)} ({verdict.Detail})");
        for (var i = 0; i < screenRows.Length; i++)
            if (screenRows[i].Length > 0)
                Console.WriteLine($"      {i,2}|{screenRows[i]}");
    }

    private static ScreenFrame Frame(Session s) => s.SnapshotLiveFrame();
}
