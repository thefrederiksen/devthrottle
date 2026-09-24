using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;

namespace CcDirector.DeliveryQualification;

/// <summary>
/// MEASURES how an agent receives characters outside ASCII through each way of writing them to its terminal. On 24
/// September Codex recorded "Caf\u00e9" as "Caf\u00c3\u00a9": every UTF-8 byte arrived as a character of its own, and
/// bytes 0x80-0x9F vanished, while Claude Code and Pi received the same bytes intact.
/// </summary>
public static class CharsExperiment
{
    private const string Sample = "e\u00e9 d\u2013 q\u201cx\u201d p\u00a3 u\u20ac";

    private static byte[] Win32Input(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            sb.Append($"\x1b[0;0;{(int)ch};1;0;1_");
            sb.Append($"\x1b[0;0;{(int)ch};0;0;1_");
        }
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    public static async Task RunAsync(SessionManager manager, string agent, AgentKind kind, string args, string repo)
    {
        var modes = new (string Name, Func<IEnumerable<byte[]>> Writes)[]
        {
            ("utf8-one-write", () => [Encoding.UTF8.GetBytes(Sample)]),
            ("utf8-per-char", () => Sample.Select(c => Encoding.UTF8.GetBytes(c.ToString()))),
            ("bracketed-paste", () => [[.. "\x1b[200~"u8.ToArray(), .. Encoding.UTF8.GetBytes(Sample), .. "\x1b[201~"u8.ToArray()]]),
            ("win32-input-mode", () => [Win32Input(Sample)]),
            ("paste-win32", () => [[.. "\x1b[200~"u8.ToArray(), .. Win32Input("line one " + Sample + "\r"), .. Win32Input("line two"), .. "\x1b[201~"u8.ToArray()]]),
        };
        var session = manager.CreateSession(repo, kind, string.IsNullOrWhiteSpace(args) ? null : args, SessionBackendType.ConPty, null);
        try
        {
            if (FirstPromptGate.CanProve(kind))
            {
                var ready = await FirstPromptGate.WaitUntilAcceptingInputAsync(
                    kind, () => Frame(session), b => session.SendInput(b, null, SubmissionProvenance.Typed("delivery-qa-rig", "rig")),
                    () => session.ActivityState == ActivityState.Exited, TimeSpan.FromMinutes(3));
                Console.WriteLine($"[chars] {agent} gate: {ready.Outcome}");
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(25));
            }
            await Task.Delay(3000);
            Console.WriteLine($"[chars] expected: {Esc(Sample)}");
            foreach (var (name, writes) in modes)
            {
                foreach (var w in writes())
                {
                    session.SendInput(w, null, SubmissionProvenance.Typed("delivery-qa-rig", "rig"));
                    await Task.Delay(30);
                }
                await Task.Delay(3000);
                if (FirstPromptGate.CanProve(kind))
                {
                    var got = DoorbellSafety.ReadComposerText(kind, Frame(session));
                    Console.WriteLine($"[chars] {agent} {name,-18} {got.Reading}: {Esc(got.Text)}");
                }
                else
                {
                    var row = session.SnapshotScreenRows().LastOrDefault(r => r.Contains("e\u00e9", StringComparison.Ordinal) || r.Contains(" qx ", StringComparison.Ordinal)) ?? "(no row)";
                    Console.WriteLine($"[chars] {agent} {name,-18} {(row.Contains(Sample, StringComparison.Ordinal) ? "INTACT" : "ALTERED")}: {Esc(row.Trim())}");
                }
                session.SendInput([0x15], null, SubmissionProvenance.Typed("delivery-qa-rig", "rig"));
                await Task.Delay(1500);
            }
        }
        finally
        {
            try { await manager.KillSessionAsync(session.Id); } catch (Exception ex) { Console.WriteLine($"[chars] kill: {ex.Message}"); }
            manager.RemoveSession(session.Id);
        }
    }

    private static string Esc(string s) => string.Concat(s.Select(c => c < 128 ? c.ToString() : "\\u" +((int)c).ToString("x4")));

    private static ScreenFrame Frame(Session s)
    {
        var (rows, row, col, visible, _) = s.SnapshotLiveScreen();
        return new ScreenFrame(rows, row, col, visible);
    }
}
