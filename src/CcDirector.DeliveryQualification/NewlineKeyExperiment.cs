using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Sessions;

namespace CcDirector.DeliveryQualification;

/// <summary>
/// MEASURES which keystroke puts a new line into an agent's composer, instead of assuming one (issue #4379). Shift+Enter
/// on the desktop sent a plain Enter, so every line a person meant as a new line submitted the message in pieces. A
/// terminal has no single "Shift+Enter" byte: it depends on what the terminal and the agent agreed. For each candidate
/// this types a first line, presses the key, types a second line, and looks: did the key submit the first line on its
/// own (the agent's records hold it), or does Enter then submit both lines as ONE turn holding the line break? The
/// agent's own records decide. It also reports which keyboard modes the agent asked the terminal for (the kitty
/// keyboard protocol, xterm's modifyOtherKeys), because those decide what a real terminal sends for Shift+Enter.
/// </summary>
public static class NewlineKeyExperiment
{
    internal static readonly (string Name, byte[] Key)[] Candidates =
    [
        ("ctrl-j", [0x0A]),
        ("alt-enter", [0x1B, 0x0D]),
        ("kitty-shift-enter", "\x1b[13;2u"u8.ToArray()),
        ("xterm-shift-enter", "\x1b[27;2;13~"u8.ToArray()),
        ("backslash-enter", [(byte)'\\', 0x0D]),
    ];

    public static async Task<int> RunAsync(SessionManager manager, string agent, AgentKind kind, string args, string repo, IReadOnlyList<string>? only)
    {
        var provenance = SubmissionProvenance.Typed("delivery-qa-rig", "rig");
        Session? session = null;
        try
        {
            foreach (var (name, key) in Candidates.Where(c => only is null || only.Contains(c.Name)))
            {
                if (session is null || session.Status is SessionStatus.Exited or SessionStatus.Failed)
                {
                    session = manager.CreateSession(repo, kind, string.IsNullOrWhiteSpace(args) ? null : args, SessionBackendType.ConPty, null);
                    if (!await Rig.WaitUntilIdleAsync(session, TimeSpan.FromSeconds(120)))
                        Console.WriteLine($"[newline] {agent}: not waiting for input after 120s; going on");
                    await Task.Delay(1500);
                    Console.WriteLine($"[newline] {agent}: keyboard modes the agent asked for: {KeyboardModes(session)}");
                }
                else if (!await Rig.WaitUntilIdleAsync(session, TimeSpan.FromSeconds(120)))
                    Console.WriteLine($"[newline] {agent}: not waiting for input after 120s; going on");

                var token = EchoLayer.Token();
                var first = $"Token {token} first line";
                const string second = "Do not run any tools. Reply with exactly: ACK";
                var sentAt = DateTime.UtcNow;
                session.SendInput(Encoding.UTF8.GetBytes(first), null, provenance);
                await Task.Delay(700);
                session.SendInput(key, null, provenance);
                await Task.Delay(700);
                session.SendInput(Encoding.UTF8.GetBytes(second), null, provenance);
                await Task.Delay(2500);
                var early = AgentRecords.UserTurnText(agent, sentAt, token);
                var composer = string.Join(" | ", session.SnapshotScreenRows().Where(r => !string.IsNullOrWhiteSpace(r)).TakeLast(5));
                string verdict;
                string? turn = early;
                if (early is not null)
                    verdict = "SUBMITS (the key sent the first line on its own)";
                else
                {
                    session.SendInput([0x0D], null, provenance);
                    var deadline = DateTime.UtcNow.AddSeconds(60);
                    while (turn is null && DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(1000);
                        turn = AgentRecords.UserTurnText(agent, sentAt, token);
                    }
                    var want = first + "\n" + second;
                    verdict = turn is null ? "LOST (nothing recorded after Enter)"
                        : EchoAnalysis.Canonical(turn).Trim() == want ? "NEWLINE (one turn, the break kept)"
                        : $"ALTERED ({EchoAnalysis.Show(turn)})";
                }
                Console.WriteLine($"[newline] {agent,-7} {name,-18} {verdict}\n          screen before Enter: {composer}");
                await Rig.WaitUntilIdleAsync(session, TimeSpan.FromSeconds(120));
                var clear = CcDirector.Core.Drivers.ComposerClearKeys.For(kind, 200);
                if (clear is not null)
                    for (var i = 0; i < 3; i++) session.SendInput(clear, null, provenance);
                await Task.Delay(1000);
            }
        }
        finally
        {
            if (session is not null)
            {
                try { await manager.KillSessionAsync(session.Id); } catch (Exception ex) { Console.WriteLine($"[newline] kill: {ex.Message}"); }
                manager.RemoveSession(session.Id);
            }
        }
        return 0;
    }

    /// <summary>The keyboard-mode requests in the agent's output so far: kitty's <c>CSI &gt; flags u</c> and xterm's
    /// <c>CSI &gt; 4 ; n m</c>.</summary>
    private static string KeyboardModes(Session session)
    {
        if (session.Buffer is not { } buffer) return "no buffer";
        var text = Encoding.UTF8.GetString(buffer.DumpTail(4 * 1024 * 1024));
        var found = System.Text.RegularExpressions.Regex.Matches(text, @"\x1b\[(>[0-9;]*u|>4;[0-9]*m|<[0-9]*u|=[0-9;]*u|\?u)")
            .Select(m => m.Value.Replace("\x1b", "ESC")).Distinct().ToList();
        return found.Count == 0 ? "none" : string.Join(" ", found);
    }
}
