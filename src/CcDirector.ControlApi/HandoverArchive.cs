using System.Text;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Writes a vault-archive markdown file for a handover. Matches the layout the
/// /handover skill (v3.0) used to produce: YAML frontmatter + structured body.
///
/// Path:
///   %LOCALAPPDATA%\cc-director\vault\handovers\YYYYMMDD_HHMM_handover-from-{srcShort}.md
/// </summary>
public static class HandoverArchive
{
    public static string VaultHandoversDir { get; } =
        Path.Combine(CcStorage.Vault(), "handovers");

    /// <summary>Write the archive. Returns the file path on success. Throws on I/O error.</summary>
    public static string Write(SessionSummaryDto summary, string contextSent, string targetSessionId)
    {
        Directory.CreateDirectory(VaultHandoversDir);

        var ts = DateTime.Now;
        var srcShort = summary.SessionId.Length >= 8 ? summary.SessionId[..8] : summary.SessionId;
        var fileName = $"{ts:yyyyMMdd_HHmm}_handover-from-{srcShort}.md";
        var path = Path.Combine(VaultHandoversDir, fileName);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("title: Handover from session " + srcShort);
        sb.AppendLine($"date: {ts:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"from_session_id: {summary.SessionId}");
        sb.AppendLine($"to_session_id: {targetSessionId}");
        if (!string.IsNullOrEmpty(summary.DirectorId))
            sb.AppendLine($"director_id: {summary.DirectorId}");
        if (!string.IsNullOrEmpty(summary.RepoPath))
        {
            sb.AppendLine("repositories:");
            sb.AppendLine($"  - path: {summary.RepoPath}");
        }
        sb.AppendLine($"agent: {summary.Agent}");
        sb.AppendLine($"turn_count: {summary.TurnCount}");
        sb.AppendLine("---");
        sb.AppendLine();

        sb.AppendLine("## Last user prompt");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrEmpty(summary.LastUserPrompt) ? "_(none)_" : summary.LastUserPrompt);
        sb.AppendLine();

        sb.AppendLine("## Last assistant reply");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrEmpty(summary.LastAssistantText) ? "_(none)_" : summary.LastAssistantText);
        sb.AppendLine();

        if (summary.FilesTouched.Count > 0)
        {
            sb.AppendLine("## Files touched");
            foreach (var f in summary.FilesTouched)
                sb.AppendLine($"- {f.Tool}: `{f.Path}`");
            sb.AppendLine();
        }

        if (summary.RecentCommands.Count > 0)
        {
            sb.AppendLine("## Recent commands");
            sb.AppendLine("```");
            foreach (var c in summary.RecentCommands)
                sb.AppendLine(c);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (summary.OpenTodos.Count > 0)
        {
            sb.AppendLine("## Outstanding TODOs");
            foreach (var t in summary.OpenTodos)
            {
                var marker = t.Status switch
                {
                    "completed" => "[x]",
                    "in_progress" => "[~]",
                    _ => "[ ]",
                };
                sb.AppendLine($"- {marker} {t.Content}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Full context sent to the next session");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(contextSent);
        sb.AppendLine("```");

        File.WriteAllText(path, sb.ToString());
        FileLog.Write($"[HandoverArchive] Wrote {path}");
        return path;
    }
}
