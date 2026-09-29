using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;

namespace CcDirector.DeliveryQualification;

/// <summary>
/// CAPTURE CLAUDE CODE'S COMPOSER WITH A TITLE IN ITS TOP RULE (issue 3481). Once a Claude Code session has a name,
/// Claude Code draws that name inside the rule above the composer ("──── keynote deck slide reordering ─"). The
/// composer reader required that row to be a plain rule, so a named session read as NotFound and, with a retention
/// mark set, refused every send. The screens the fix is proven against are not written from a guess: this starts a
/// real Claude Code in the Director's own terminal, names the session with /rename, and dumps the empty composer and
/// the composer holding an unsent sentence. Nothing is submitted to the model.
/// </summary>
public static class ClaudeTitledComposerCapture
{
    public const string Title = "keynote deck slide reordering";
    public const string Draft = "make a new video of version 36";

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
            Console.WriteLine($"[claude-titled-capture] Claude Code ready: {ready.Outcome}, pid={session.ProcessId}");
            if (ready.Outcome != FirstPromptGateOutcome.Ready)
                return 1;
            await Rig.WaitUntilIdleAsync(session, TimeSpan.FromMinutes(1));

            // /rename is a local command: it names the session without a turn.
            session.SendInput(Encoding.UTF8.GetBytes("/rename " + Title), null, provenance);
            await Task.Delay(TimeSpan.FromSeconds(2));
            session.SendInput("\r"u8.ToArray(), null, provenance);
            await Task.Delay(TimeSpan.FromSeconds(5));
            Save(session, outDir, "claude-titled-idle-empty");

            session.SendInput(Encoding.ASCII.GetBytes(Draft), null, provenance);
            await Task.Delay(TimeSpan.FromSeconds(3));
            Save(session, outDir, "claude-titled-owner-text");
            session.SendInput(Enumerable.Repeat((byte)0x7f, Draft.Length).ToArray(), null, provenance);
            await Task.Delay(TimeSpan.FromSeconds(3));
            Save(session, outDir, "claude-titled-idle-empty-after-erase");
            return 0;
        }
        finally
        {
            try { await manager.KillSessionAsync(session.Id); } catch (Exception ex) { Console.WriteLine($"[claude-titled-capture] kill: {ex.Message}"); }
            manager.RemoveSession(session.Id);
        }
    }

    private static void Save(Session session, string outDir, string label)
    {
        var (screenRows, cursorRow, cursorCol, cursorVisible, alternate) = session.SnapshotLiveScreen();
        var frame = new ScreenFrame(screenRows, cursorRow, cursorCol, cursorVisible);
        var (reading, text) = DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, frame);
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
        Console.WriteLine($"[claude-titled-capture] {label}: cursor={cursorRow},{cursorCol} visible={cursorVisible} " +
                          $"reading={reading} text='{text}'");
        for (var i = 0; i < screenRows.Length; i++)
            if (screenRows[i].Length > 0)
                Console.WriteLine($"      {i,2}|{screenRows[i]}");
    }

    private static ScreenFrame Frame(Session s)
    {
        var (rows, row, col, visible, _) = s.SnapshotLiveScreen();
        return new ScreenFrame(rows, row, col, visible);
    }
}
