using CcDirector.Core.Agents;
using CcDirector.Core.Claude;
using CcDirector.Core.Codex;
using CcDirector.Core.Pi;
using CcDirector.Core.Grok;
using CcDirector.Core.Copilot;
using CcDirector.Core.OpenCode;
using CcDirector.Core.Gemini;
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
/// - Pi: the session file named by the id the Director launched pi with (<see cref="PiSessionLocator"/>;
///   pi's /new is followed by <see cref="PiSessionRebinder"/>).
/// - Grok: the newest chat_history.jsonl for the session's repo (<see cref="GrokSessionLocator"/>).
/// - Copilot: the newest session in its SQLite store whose cwd matches the session's repo
///   (<see cref="CopilotHistoryReader"/>); resolved by repo, not a single transcript file.
/// - OpenCode: the newest session in its SQLite store whose directory matches the session's repo
///   (<see cref="OpenCodeHistoryReader"/>); resolved by repo, not a single transcript file.
/// - Gemini: the exception - it persists no usable transcript, so its conversation is read from
///   the session's own terminal buffer (<see cref="GeminiTerminalHistory"/>); there is no path.
///
/// Other agents return <see cref="ConversationHistory.Empty"/> until their providers land.
/// </summary>
public static class SessionHistoryReader
{
    /// <summary>True when a history provider exists for this session's agent.</summary>
    public static bool IsSupported(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.AgentKind is AgentKind.ClaudeCode or AgentKind.Codex or AgentKind.Pi or AgentKind.Grok or AgentKind.Copilot or AgentKind.OpenCode or AgentKind.Gemini;
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
            AgentKind.Codex => CodexRolloutLocator.Resolve(session.Id, session.RepoPath, session.CreatedAt),
            // Pi is launched with --session-id, so its file is named by the session's agent id (issue #2670).
            AgentKind.Pi => PiSessionLocator.Resolve(session.ClaudeSessionId),
            AgentKind.Grok => GrokSessionLocator.Resolve(session.Id, session.RepoPath),
            // Copilot has no per-session transcript file; the readable source is its SQLite store.
            // Return the store path (when present) so a caller can stat it to detect changes.
            AgentKind.Copilot => CopilotHistoryReader.DefaultDatabasePath,
            // OpenCode likewise has no per-session file; its readable source is its SQLite store.
            AgentKind.OpenCode => OpenCodeHistoryReader.DefaultDatabasePath,
            // Gemini has no transcript file at all; its conversation lives only in the session's
            // terminal buffer (read directly in Read). There is no path to stat - null is honest.
            AgentKind.Gemini => null,
            _ => null,
        };
    }

    /// <summary>
    /// The session's conversation, as a reader of the conversation expects it: main thread only, no
    /// content-less lines. This is the long-standing contract of this facade and every caller depends
    /// on it.
    ///
    /// The underlying parse deliberately keeps more than this (nested subagent turns, agent-injected
    /// meta lines, lines that carry only token usage) so that ONE read can serve every consumer -
    /// the Agent view wants the subagent turns this hides. Those consumers read
    /// <see cref="ReadAll"/> and narrow it themselves.
    /// </summary>
    public static ConversationHistory Read(Session session) => ReadAll(session).MainThread;

    /// <summary>
    /// The main-thread history read from a transcript path the CALLER already resolved. Exists so a caller
    /// that needs the path for something else (the turn push labels the messages with it as their
    /// generation, and derives the history state from the same file) reads the messages from THAT file -
    /// resolving twice could label one file's messages with another's identity if the pointer moved in
    /// between. Gemini ignores the path (its history is its terminal buffer); the store-backed agents read
    /// by repository as always.
    /// </summary>
    public static ConversationHistory Read(Session session, string? resolvedPath)
    {
        ArgumentNullException.ThrowIfNull(session);
        // No blanket null-path check: Copilot and OpenCode read a store BY REPOSITORY and never needed the
        // path, so cutting them off above would have made them permanently empty here while ReadAll still
        // answered (found in review). Each file-backed arm tests the path itself.
        var history = (session.AgentKind switch
        {
            AgentKind.Gemini => GeminiTerminalHistory.FromBuffer(session.Buffer),
            AgentKind.Copilot => CopilotHistoryReader.Read(session.RepoPath),
            AgentKind.OpenCode => OpenCodeHistoryReader.Read(session.RepoPath),
            AgentKind.ClaudeCode when resolvedPath is not null => ClaudeTranscriptReader.Read(resolvedPath),
            AgentKind.Codex when resolvedPath is not null => CodexTranscriptReader.Read(resolvedPath),
            AgentKind.Pi when resolvedPath is not null => PiTranscriptReader.Read(resolvedPath),
            AgentKind.Grok when resolvedPath is not null => GrokTranscriptReader.Read(resolvedPath),
            _ => ConversationHistory.Empty,
        }).MainThread;
        return StoredPromptPayloadResolver.Resolve(history, session.RepoPath, session.Id);
    }

    /// <summary>
    /// Everything the source holds, unfiltered - including nested subagent turns, meta lines, and
    /// lines carrying only token usage. Prefer <see cref="Read"/> unless you specifically need those.
    /// </summary>
    public static ConversationHistory ReadAll(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        // Gemini is the exception: it has no transcript file, so there is no path to resolve.
        // Its only readable source is the session's own terminal buffer, where the conversation
        // is present as plain text. Build the (single, unstructured) history straight from it.
        if (session.AgentKind == AgentKind.Gemini)
            return StoredPromptPayloadResolver.Resolve(
                GeminiTerminalHistory.FromBuffer(session.Buffer), session.RepoPath, session.Id);

        var path = ResolveTranscriptPath(session);
        if (path is null)
            return ConversationHistory.Empty;

        var history = session.AgentKind switch
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
        return StoredPromptPayloadResolver.Resolve(history, session.RepoPath, session.Id);
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
