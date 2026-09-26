using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;

namespace CcDirector.DeliveryQualification;

/// <summary>
/// CAPTURE CODEX'S COMPOSER HOLDING A WRAPPED PROMPT (Voice Delivery mission, phase 6, review finding 1). The
/// composer reader read one row - the cursor's row, which must start with '›' - so a prompt longer than the
/// terminal is wide, whose cursor sits on a continuation row, read as NotFound and the send fell into
/// clear-and-retype. No wrapped Codex screen was ever captured (the account hit its usage limit during the
/// doorbell captures on 17 September 2026), so the continuation rows' shape is not written from a guess here:
/// this starts a real Codex, types a long prompt WITHOUT Enter, and dumps the rendered screen as a fixture.
/// Nothing is submitted, so no model call is made.
/// </summary>
public static class CodexComposerCapture
{
    public static async Task<int> RunAsync(SessionManager manager, string args, string repo, string outDir, short cols, short rows)
    {
        var provenance = SubmissionProvenance.Typed("delivery-qa-rig", "rig");
        var session = manager.CreateSession(repo, AgentKind.Codex, args, SessionBackendType.ConPty, null);
        try
        {
            session.Resize(cols, rows);
            var ready = await FirstPromptGate.WaitUntilAcceptingInputAsync(
                AgentKind.Codex, () => Frame(session), b => session.SendInput(b, null, provenance),
                () => session.ActivityState == ActivityState.Exited, TimeSpan.FromMinutes(5));
            Console.WriteLine($"[codex-composer-capture] Codex ready: {ready.Outcome}, pid={session.ProcessId}");
            if (ready.Outcome != FirstPromptGateOutcome.Ready)
                return 1;

            // A prompt that must wrap on a screen this narrow: letters and spaces only, so the fixture is a
            // plain word wrap with no paste markers or special characters in it.
            const string prompt = "Reply with only the word OK. Marker: fresh quokka seventy one two three four five six " +
                "seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen";
            session.SendInput(Encoding.ASCII.GetBytes(prompt), null, provenance);
            await Task.Delay(TimeSpan.FromSeconds(4));
            var (screenRows, cursorRow, cursorCol, cursorVisible, _) = session.SnapshotLiveScreen();

            var fixture = new Dictionary<string, object?>
            {
                ["label"] = "codex-wrapped-composer",
                ["cursorRow"] = cursorRow,
                ["cursorCol"] = cursorCol,
                ["cursorVisible"] = cursorVisible,
                ["alternateScreen"] = false,
                ["rows"] = screenRows,
            };
            var path = Path.Combine(outDir, "codex-wrapped-composer.json");
            File.WriteAllText(path, JsonSerializer.Serialize(fixture, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"[codex-composer-capture] wrote {path} ({session.CurrentCols}x{session.CurrentRows}, " +
                $"prompt {prompt.Length} chars, cursor {cursorRow},{cursorCol})");
            for (var i = 0; i < screenRows.Length; i++)
                if (screenRows[i].Length > 0)
                    Console.WriteLine($"      {i,2}|{screenRows[i]}");
            return 0;
        }
        finally
        {
            try { await manager.KillSessionAsync(session.Id); } catch (Exception ex) { Console.WriteLine($"[codex-composer-capture] kill: {ex.Message}"); }
            manager.RemoveSession(session.Id);
        }
    }

    private static ScreenFrame Frame(Session s)
    {
        var (rows, row, col, visible, _) = s.SnapshotLiveScreen();
        return new ScreenFrame(rows, row, col, visible);
    }
}
