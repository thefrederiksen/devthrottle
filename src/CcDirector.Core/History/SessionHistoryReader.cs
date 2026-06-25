using CcDirector.Core.Agents;
using CcDirector.Core.Claude;
using CcDirector.Core.Sessions;

namespace CcDirector.Core.History;

/// <summary>
/// Returns the canonical <see cref="ConversationHistory"/> for a session, choosing the right
/// source per agent. This is the single facade the History tab (and, later, a REST endpoint and
/// other consumers) call, so they never need to know how a given agent stores its conversation.
///
/// First cut: Claude sessions are read from their transcript file via the live pointer
/// (<see cref="Session.ClaudeTranscriptPath"/>, kept current across /clear by the SessionStart
/// hook), falling back to deriving the path from the session id. Other agents return
/// <see cref="ConversationHistory.Empty"/> until their providers land.
/// </summary>
public static class SessionHistoryReader
{
    /// <summary>
    /// Resolve the on-disk transcript path a consumer should read for this session, or null if
    /// the session has no readable transcript (a non-Claude agent today, or a Claude session
    /// whose first hook has not fired and whose id is unknown). A caller can cheaply stat this
    /// path to detect changes before paying to re-parse.
    /// </summary>
    public static string? ResolveTranscriptPath(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.AgentKind != AgentKind.ClaudeCode)
            return null;

        // Prefer the hook-reported path (authoritative across /clear and compaction); fall back
        // to deriving it from the current session id when no hook has fired yet.
        var path = session.ClaudeTranscriptPath;
        if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(session.ClaudeSessionId))
            path = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);

        return string.IsNullOrEmpty(path) ? null : path;
    }

    public static ConversationHistory Read(Session session)
    {
        var path = ResolveTranscriptPath(session);
        return path != null ? ClaudeTranscriptReader.Read(path) : ConversationHistory.Empty;
    }
}
