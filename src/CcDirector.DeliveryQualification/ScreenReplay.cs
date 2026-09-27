using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Terminal.Core;

namespace CcDirector.DeliveryQualification;

/// <summary>
/// REPLAY A SESSION'S RAW TERMINAL OUTPUT THROUGH THE DIRECTOR'S OWN PARSER (issue 3289). The doorbell deferred
/// thousands of rings a day as "no composer was recognised", and no live grid of those sessions can be read from
/// outside the Director. The Gateway's buffer route returns the session's raw output (<c>?raw=true</c>); this feeds
/// it through the same <see cref="AnsiParser"/> a session uses, at the session's width, and prints the grid the
/// doorbell would have read, with the verdict it would have given.
/// </summary>
public static class ScreenReplay
{
    public static int Run(string file, short cols, short rows)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
        var text = doc.RootElement.GetProperty("text").GetString() ?? "";
        var parser = new AnsiParser(new TerminalCell[cols, rows], cols, rows, new List<TerminalCell[]>(), 1000);
        parser.Parse(Encoding.UTF8.GetBytes(text));
        var (screenRows, cursorRow, cursorCol) = parser.SnapshotActiveRows();
        var frame = new ScreenFrame(screenRows, cursorRow, cursorCol, parser.IsCursorVisible);
        var verdict = DoorbellSafety.CheckFrame(AgentKind.ClaudeCode, frame);
        Console.WriteLine($"[replay] {Path.GetFileName(file)} {cols}x{rows}: cursor={cursorRow},{cursorCol} visible={parser.IsCursorVisible} " +
                          $"alternate={parser.IsAlternateScreen} verdict={(verdict.Ring ? "RING" : verdict.Reason)} ({verdict.Detail})");
        for (var i = 0; i < screenRows.Length; i++)
            Console.WriteLine($"      {i,2}|{screenRows[i]}");
        return 0;
    }
}
