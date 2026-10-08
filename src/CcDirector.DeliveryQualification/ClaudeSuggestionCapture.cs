using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;

namespace CcDirector.DeliveryQualification;

/// <summary>
/// CAPTURE CLAUDE CODE'S GREY PROMPT SUGGESTION (the Prompt Delivery mission, 8 October 2026). After a turn ends,
/// Claude Code draws a guess at the owner's next prompt in grey inside the EMPTY composer ("yes, go on 2.18.0 after
/// the follow-up merges"); Tab accepts it. On 7 October 2026 the composer reader read that grey text as typed text,
/// so a send of "go" was refused and every later send to the session with it. The screens the fix is proven against
/// are not written from a guess: this starts a real Claude Code, runs two turns, the second ending in a yes-or-no question,
/// waits for the suggestion, and dumps the rows AND the raw bytes Claude Code drew them with, then types a short
/// prompt over the suggestion and dumps again. Nothing after the first turn is submitted.
/// </summary>
public static class ClaudeSuggestionCapture
{
    public const string WarmUp = "Reply with exactly this one word and nothing else: ready";

    public const string Question =
        "Reply with exactly this one sentence and nothing else: Shall I go ahead and cut release 2.18.0 now?";

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
            Console.WriteLine($"[claude-suggestion-capture] Claude Code ready: {ready.Outcome}, pid={session.ProcessId}");
            if (ready.Outcome != FirstPromptGateOutcome.Ready)
                return 1;
            await Rig.WaitUntilIdleAsync(session, TimeSpan.FromMinutes(1));

            // Claude Code suggests nothing before the conversation holds two answers ("early_conversation").
            foreach (var turn in new[] { WarmUp, Question })
            {
                session.SendInput(Encoding.UTF8.GetBytes(turn), null, provenance);
                await Task.Delay(TimeSpan.FromSeconds(2));
                session.SendInput("\r"u8.ToArray(), null, provenance);
                await Task.Delay(TimeSpan.FromSeconds(5));
                await Rig.WaitUntilIdleAsync(session, TimeSpan.FromMinutes(2));
            }

            // The suggestion is generated after the turn ends; wait for the composer row to carry text.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            var seen = false;
            while (DateTime.UtcNow < deadline)
            {
                // The suggestion is FAINT, so the composer reads empty with or without it: wait for faint text on screen.
                var frame = Frame(session);
                if (!frame.Rows.SequenceEqual(frame.RowsWithoutFaint ?? frame.Rows)) { seen = true; break; }
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
            Console.WriteLine($"[claude-suggestion-capture] suggestion drawn: {seen}");
            await Task.Delay(TimeSpan.FromSeconds(2));
            Save(session, outDir, "claude-suggestion-idle", 0);

            var before = session.Buffer?.TotalBytesWritten ?? 0;
            session.SendInput("go"u8.ToArray(), null, provenance);
            await Task.Delay(TimeSpan.FromSeconds(3));
            Save(session, outDir, "claude-suggestion-typed-go", before);

            before = session.Buffer?.TotalBytesWritten ?? 0;
            session.SendInput(ComposerClearKeys.For(AgentKind.ClaudeCode, 2)!, null, provenance);
            await Task.Delay(TimeSpan.FromSeconds(3));
            Save(session, outDir, "claude-suggestion-after-clear", before);
            return seen ? 0 : 2;
        }
        finally
        {
            try { await manager.KillSessionAsync(session.Id); } catch (Exception ex) { Console.WriteLine($"[claude-suggestion-capture] kill: {ex.Message}"); }
            manager.RemoveSession(session.Id);
        }
    }

    private static void Save(Session session, string outDir, string label, long bytesSince)
    {
        var (screenRows, cursorRow, cursorCol, cursorVisible, alternate) = session.SnapshotLiveScreen();
        var frame = session.SnapshotLiveFrame();
        var (reading, text) = DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, frame);
        var raw = session.Buffer is { } buffer
            ? (bytesSince > 0 ? buffer.GetWrittenSince(bytesSince).Data : buffer.DumpTail(16384))
            : [];
        var fixture = new Dictionary<string, object?>
        {
            ["label"] = label,
            ["activity"] = session.ActivityState.ToString(),
            ["cursorRow"] = cursorRow,
            ["cursorCol"] = cursorCol,
            ["cursorVisible"] = cursorVisible,
            ["alternateScreen"] = alternate,
            ["rows"] = screenRows,
            ["rowsWithoutFaint"] = frame.RowsWithoutFaint,
            ["raw"] = Encoding.UTF8.GetString(raw),
        };
        File.WriteAllText(Path.Combine(outDir, label + ".json"),
            JsonSerializer.Serialize(fixture, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[claude-suggestion-capture] {label}: cursor={cursorRow},{cursorCol} visible={cursorVisible} " +
                          $"reading={reading} text='{text}' rawBytes={raw.Length}");
        for (var i = 0; i < screenRows.Length; i++)
            if (screenRows[i].Length > 0)
                Console.WriteLine($"      {i,2}|{screenRows[i]}");
    }

    private static ScreenFrame Frame(Session s) => s.SnapshotLiveFrame();
}
