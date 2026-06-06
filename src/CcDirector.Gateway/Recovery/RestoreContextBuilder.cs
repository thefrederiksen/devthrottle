using System.Text;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Recovery;

/// <summary>
/// Builds the continuation context seeded into a restored session (issue #212 W4).
///
/// A restore is a CONTINUATION, never <c>claude --resume</c>: a fresh session is created in
/// the dead session's repo and this text is its first prompt. The content comes from the
/// Gateway's surviving turn-brief history (the wingman's per-turn reading of the session),
/// which is the only structured record that outlives a dead Director. The format is the
/// field-tested shape from the 2026-06-06 incident's manual restore script
/// (.temp/restore_sessions.py): state at death from the latest brief, trajectory from the
/// preceding ones, then explicit marching orders that END with re-asking the user - a
/// restored session must never charge ahead on stale context.
/// </summary>
public static class RestoreContextBuilder
{
    /// <param name="name">The dead session's display name (journal row; may be null).</param>
    /// <param name="sessionId">The dead session's Director session id.</param>
    /// <param name="repoPath">Repo / working directory of the dead session.</param>
    /// <param name="claudeSessionId">The dead session's Claude conversation id, when the journal captured one. The prior transcript file is named after it.</param>
    /// <param name="diedAtUtc">Best estimate of time of death (journal's last update).</param>
    /// <param name="briefs">Turn-brief history for the session, oldest first. May be empty - restore still works, with less context.</param>
    public static string Build(
        string? name,
        string sessionId,
        string repoPath,
        string? claudeSessionId,
        DateTimeOffset diedAtUtc,
        IReadOnlyList<TurnBriefDto> briefs)
    {
        var display = string.IsNullOrWhiteSpace(name) ? sessionId[..Math.Min(8, sessionId.Length)] : name;
        var sb = new StringBuilder();

        sb.AppendLine($"You are a RESTORED session continuing '{display}'.");
        sb.AppendLine();
        sb.AppendLine($"Your predecessor (Director session {sessionId}) was lost to an unexpected");
        sb.AppendLine($"Director shutdown around {diedAtUtc:yyyy-MM-dd HH:mm} UTC before it could finish.");
        sb.AppendLine("This context was rebuilt from its surviving turn-brief history (the wingman's");
        sb.AppendLine("per-turn reading of the session).");
        sb.AppendLine();
        sb.AppendLine($"- Repo / working directory: {repoPath}");
        if (!string.IsNullOrWhiteSpace(claudeSessionId))
        {
            sb.AppendLine($"- Prior Claude conversation id: {claudeSessionId}");
            sb.AppendLine($"  The FULL prior transcript is the file {claudeSessionId}.jsonl in this repo's");
            sb.AppendLine("  project folder under ~/.claude/projects/ - locate it and read its tail.");
        }

        var latest = briefs.Count > 0 ? briefs[^1] : null;
        if (latest is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"## State at death (latest wingman brief, turn {latest.TurnNumber}, {latest.GeneratedAtUtc:o})");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(latest.Headline))
                sb.AppendLine($"Headline: {latest.Headline}");
            if (!string.IsNullOrWhiteSpace(latest.Intent))
                sb.AppendLine($"Mission/intent: {latest.Intent}");
            if (latest.Did.Count > 0)
            {
                sb.AppendLine("Last turn accomplished:");
                foreach (var d in latest.Did) sb.AppendLine($"- {d}");
            }
            if (latest.NeedsYou is { } ny)
            {
                sb.AppendLine("What was pending for the user:");
                if (!string.IsNullOrWhiteSpace(ny.Statement)) sb.AppendLine($"- Statement: {ny.Statement}");
                if (!string.IsNullOrWhiteSpace(ny.RailLine)) sb.AppendLine($"- Rail line: {ny.RailLine}");
                if (ny.Options.Count > 0)
                {
                    sb.AppendLine("- Options that were on the table:");
                    foreach (var o in ny.Options) sb.AppendLine($"  - [{o.Key}] {o.Send}");
                }
            }

            if (briefs.Count > 1)
            {
                sb.AppendLine();
                sb.AppendLine("## Preceding turns (for trajectory)");
                sb.AppendLine();
                for (int i = 0; i < briefs.Count - 1; i++)
                {
                    var b = briefs[i];
                    var rail = b.NeedsYou?.RailLine;
                    sb.AppendLine($"- turn {b.TurnNumber}: {b.TurnTitle}{(string.IsNullOrWhiteSpace(rail) ? "" : " -- " + rail)}");
                }
            }
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("No turn briefs survived for this session - recover context from the prior");
            sb.AppendLine("transcript and the repo state alone.");
        }

        sb.AppendLine();
        sb.AppendLine("## Your job now");
        sb.AppendLine();
        sb.AppendLine("1. Read the tail of the prior transcript (last ~150 lines) to recover concrete");
        sb.AppendLine("   detail: file paths, decisions, the exact pending question.");
        sb.AppendLine("2. Check current state (git status, files mentioned) - the world may have moved");
        sb.AppendLine("   since the shutdown; do not assume the transcript is still accurate.");
        sb.AppendLine("3. Give the user a short WHERE WE LEFT OFF summary and re-ask the pending");
        sb.AppendLine("   question. Do NOT resume work until the user answers.");

        return sb.ToString();
    }
}
