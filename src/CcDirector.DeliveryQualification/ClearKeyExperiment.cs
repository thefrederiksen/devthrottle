using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;

namespace CcDirector.DeliveryQualification;

/// <summary>
/// MEASURES which keystroke empties an agent's composer, instead of assuming one. The submit code clears a composer
/// with Escape before retyping, and on 24 September that Escape was proven to leave Codex's composer untouched: the
/// retype doubled the prompt and the next send was appended to it. For each candidate key this starts a fresh agent,
/// types a draft, presses the key, and reads the composer back through the Director's own reader.
///
/// Two drafts were added for issue #4380 (23 October 2026): a "refused-command" - a line starting with "/" that the
/// agent refuses on Enter, which Codex keeps in its composer with the cursor at its START, where the measured Ctrl+U
/// (delete to the start of the line) deletes nothing - and an "empty" composer, to show a key does no harm there.
/// Optional: --keys-only names the candidates to run, so one key can be measured without starting every other one.
/// </summary>
public static class ClearKeyExperiment
{
    private static readonly (string Name, byte[][] Keys)[] Candidates =
    [
        ("escape", [[0x1B]]),
        ("escape-twice", [[0x1B], [0x1B]]),
        ("ctrl-u", [[0x15]]),
        ("ctrl-c", [[0x03]]),
        ("end-then-backspaces", [[0x05], Enumerable.Repeat((byte)0x7F, 80).ToArray()]),
        ("end-then-ctrl-u", [[0x05], [0x15]]),
    ];

    public static async Task RunAsync(SessionManager manager, string agent, AgentKind kind, string args, string repo,
        IReadOnlyCollection<string>? only = null)
    {
        foreach (var (name, keys) in Candidates.Where(c => only is null || only.Contains(c.Name)))
        {
            foreach (var draftKind in new[] { "typed", "pasted-two-lines", "refused-command", "empty" })
            {
                var session = manager.CreateSession(repo, kind, string.IsNullOrWhiteSpace(args) ? null : args, SessionBackendType.ConPty, null);
                try
                {
                    if (FirstPromptGate.CanProve(kind))
                    {
                        var ready = await FirstPromptGate.WaitUntilAcceptingInputAsync(
                            kind, () => Frame(session), b => session.SendInput(b, null, SubmissionProvenance.Typed("delivery-qa-rig", "rig")),
                            () => session.ActivityState == ActivityState.Exited, TimeSpan.FromMinutes(3));
                        if (ready.Outcome != FirstPromptGateOutcome.Ready)
                        {
                            Console.WriteLine($"[keys] {agent} {name} {draftKind}: agent never ready ({ready.Detail})");
                            continue;
                        }
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromSeconds(25));
                    }
                    var loadWatch = System.Diagnostics.Stopwatch.StartNew();
                    while (loadWatch.Elapsed < TimeSpan.FromSeconds(120)
                           && session.SnapshotScreenRows().Any(r => r.Contains("loading", StringComparison.OrdinalIgnoreCase)))
                        await Task.Delay(250);
                    Console.WriteLine($"[keys] {agent} {name} {draftKind}: probe back after gate; 'loading' gone after a further {loadWatch.Elapsed.TotalSeconds:F1}s");
                    var draft = draftKind switch
                    {
                        "typed" => "alpha beta gamma",
                        "refused-command" => "/Tokenzz alpha beta gamma",
                        "empty" => "",
                        _ => "alpha beta\nsecond line delta",
                    };
                    byte[] bytes = draftKind is "typed" or "refused-command" or "empty"
                        ? Encoding.UTF8.GetBytes(draft)
                        : [.. "\x1b[200~"u8.ToArray(), .. Encoding.UTF8.GetBytes(draft), .. "\x1b[201~"u8.ToArray()];
                    if (bytes.Length > 0)
                        session.SendInput(bytes, null, SubmissionProvenance.Typed("delivery-qa-rig", "rig"));
                    var echoWatch = System.Diagnostics.Stopwatch.StartNew();
                    while (FirstPromptGate.CanProve(kind) && echoWatch.Elapsed < TimeSpan.FromSeconds(60)
                           && AgentRecords.Normalize(DoorbellSafety.ReadComposerText(kind, Frame(session)).Text) != AgentRecords.Normalize(draft))
                        await Task.Delay(250);
                    Console.WriteLine($"[keys]   draft fully echoed after {echoWatch.Elapsed.TotalSeconds:F1}s");
                    if (draftKind == "refused-command")
                    {
                        // Enter on an unknown command: the agent refuses it and, in Codex, keeps the line.
                        session.SendInput([0x0D], null, SubmissionProvenance.Typed("delivery-qa-rig", "rig"));
                        await Task.Delay(2000);
                        var kept = Frame(session);
                        Console.WriteLine($"[keys]   after Enter on the command: cursorRow={kept.CursorRow}, cursorCol={kept.CursorCol}, " +
                                          $"row='{(kept.CursorRow >= 0 && kept.CursorRow < kept.Rows.Count ? kept.Rows[kept.CursorRow].TrimEnd() : "")}'");
                    }
                    if (!FirstPromptGate.CanProve(kind)) await Task.Delay(3000);
                    var before = DoorbellSafety.ReadComposerText(kind, Frame(session));
                    var bottomBefore = string.Join(" | ", session.SnapshotScreenRows().Where(r => !string.IsNullOrWhiteSpace(r)).TakeLast(4));
                    foreach (var k in keys)
                    {
                        session.SendInput(k, null, SubmissionProvenance.Typed("delivery-qa-rig", "rig"));
                        await Task.Delay(250);
                    }
                    await Task.Delay(1500);
                    var after = DoorbellSafety.ReadComposerText(kind, Frame(session));
                    var bottom = string.Join(" | ", session.SnapshotScreenRows().Where(r => !string.IsNullOrWhiteSpace(r)).TakeLast(3));
                    Console.WriteLine($"[keys] {agent,-7} {name,-20} {draftKind,-17} before={before.Reading}:'{before.Text.Replace("\n", "\\n")}' " +
                                      $"after={after.Reading}:'{after.Text.Replace("\n", "\\n")}' exited={session.ActivityState == ActivityState.Exited}\n        BEFORE: {bottomBefore}\n        AFTER:  {bottom}");
                }
                finally
                {
                    try { await manager.KillSessionAsync(session.Id); } catch (Exception ex) { Console.WriteLine($"[keys] kill: {ex.Message}"); }
                    manager.RemoveSession(session.Id);
                }
            }
        }
    }

    private static ScreenFrame Frame(Session s) => s.SnapshotLiveFrame();
}
