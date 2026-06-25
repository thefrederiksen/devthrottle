using CcDirector.Core.Agents;
using CcDirector.Core.Claude;
using CcDirector.Core.Codex;
using CcDirector.Core.Pi;
using CcDirector.Core.Grok;
using CcDirector.Core.Copilot;
using CcDirector.Core.OpenCode;
using CcDirector.Core.Sessions;

namespace CcDirector.Core.History;

/// <summary>
/// Returns the canonical <see cref="ConversationHistory"/> for a session, choosing the right
/// source per agent. This is the single facade the History tab (and, later, a REST endpoint and
/// other consumers) call, so they never need to know how a given agent stores its conversation.
///
/// Supported today:
/// - Claude: its transcript file via the live pointer (<see cref="Session.ClaudeTranscriptPath"/>,
///   kept current across /clear by the SessionStart hook), falling back to deriving the path from
///   the session id.
/// - Codex: the newest rollout for the session's repo (<see cref="CodexRolloutLocator"/>).
/// - Pi: the newest session file for the session's repo (<see cref="PiSessionLocator"/>).
/// - Grok: the newest chat_history.jsonl for the session's repo (<see cref="GrokSessionLocator"/>).
/// - Copilot: the newest session in its SQLite store whose cwd matches the session's repo
///   (<see cref="CopilotHistoryReader"/>); resolved by repo, not a single transcript file.
/// - OpenCode: the newest session in its SQLite store whose directory matches the session's repo
///   (<see cref="OpenCodeHistoryReader"/>); resolved by repo, not a single transcript file.
///
/// Other agents return <see cref="ConversationHistory.Empty"/> until their providers land.
/// </summary>
public static class SessionHistoryReader
{
    /// <summary>True when a history provider exists for this session's agent.</summary>
    public static bool IsSupported(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.AgentKind is AgentKind.ClaudeCode or AgentKind.Codex or AgentKind.Pi or AgentKind.Grok or AgentKind.Copilot or AgentKind.OpenCode;
    }

    /// <summary>
    /// Resolve the on-disk transcript path a consumer should read for this session, or null if
    /// there is no readable transcript yet (an unsupported agent, or a supported agent whose
    /// transcript has not appeared). A caller can cheaply stat this path to detect changes before
    /// paying to re-parse.
    /// </summary>
    public static string? ResolveTranscriptPath(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return session.AgentKind switch
        {
            AgentKind.ClaudeCode => ResolveClaude(session),
            AgentKind.Codex => CodexRolloutLocator.Resolve(session.Id, session.RepoPath),
            AgentKind.Pi => PiSessionLocator.Resolve(session.Id, session.RepoPath),
            AgentKind.Grok => GrokSessionLocator.Resolve(session.Id, session.RepoPath),
            // Copilot has no per-session transcript file; the readable source is its SQLite store.
            // Return the store path (when present) so a caller can stat it to detect changes.
            AgentKind.Copilot => CopilotHistoryReader.DefaultDatabasePath,
            // OpenCode likewise has no per-session file; its readable source is its SQLite store.
            AgentKind.OpenCode => OpenCodeHistoryReader.DefaultDatabasePath,
            _ => null,
        };
    }

    public static ConversationHistory Read(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var path = ResolveTranscriptPath(session);
        if (path is null)
            return ConversationHistory.Empty;

        return session.AgentKind switch
        {
            AgentKind.ClaudeCode => ClaudeTranscriptReader.Read(path),
            AgentKind.Codex => CodexTranscriptReader.Read(path),
            AgentKind.Pi => PiTranscriptReader.Read(path),
            AgentKind.Grok => GrokTranscriptReader.Read(path),
            // Copilot resolves the conversation from its SQLite store by repo (the path above is
            // the store file, used only as the change-detection / existence signal).
            AgentKind.Copilot => CopilotHistoryReader.Read(session.RepoPath),
            // OpenCode likewise resolves the conversation from its SQLite store by repo.
            AgentKind.OpenCode => OpenCodeHistoryReader.Read(session.RepoPath),
            _ => ConversationHistory.Empty,
        };
    }

    private static string? ResolveClaude(Session session)
    {
        // Prefer the hook-reported path (authoritative across /clear and compaction); fall back
        // to deriving it from the current session id when no hook has fired yet.
        var path = session.ClaudeTranscriptPath;
        if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(session.ClaudeSessionId))
            path = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);

        return string.IsNullOrEmpty(path) ? null : path;
    }
}
