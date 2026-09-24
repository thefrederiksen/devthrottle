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
    ];

    public static async Task RunAsync(SessionManager manager, string agent, AgentKind kind, string args, string repo)
    {
        foreach (var (name, keys) in Candidates)
        {
            foreach (var draftKind in new[] { "typed", "pasted-two-lines" })
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
                    var draft = draftKind == "typed" ? "alpha beta gamma" : "alpha beta\nsecond line delta";
                    var bytes = draftKind == "typed"
                        ? Encoding.UTF8.GetBytes(draft)
                        : [.. "\x1b[200~"u8.ToArray(), .. Encoding.UTF8.GetBytes(draft), .. "\x1b[201~"u8.ToArray()];
                    session.SendInput(bytes, null, SubmissionProvenance.Typed("delivery-qa-rig", "rig"));
                    var echoWatch = System.Diagnostics.Stopwatch.StartNew();
                    while (FirstPromptGate.CanProve(kind) && echoWatch.Elapsed < TimeSpan.FromSeconds(60)
                           && AgentRecords.Normalize(DoorbellSafety.ReadComposerText(kind, Frame(session)).Text) != AgentRecords.Normalize(draft))
                        await Task.Delay(250);
                    Console.WriteLine($"[keys]   draft fully echoed after {echoWatch.Elapsed.TotalSeconds:F1}s");
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

    private static ScreenFrame Frame(Session s)
    {
        var (rows, row, col, visible, _) = s.SnapshotLiveScreen();
        return new ScreenFrame(rows, row, col, visible);
    }
}
