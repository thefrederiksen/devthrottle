using CcDirector.Core.Agents;
using CcDirector.Core.History;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Builds the parsed, agent-agnostic conversation history for a session. Reuses Core's
/// <see cref="SessionHistoryReader"/> so every supported agent is covered, then maps the normalized
/// <c>ConversationHistory</c> into the wire <see cref="SessionHistoryDto"/> the Cockpit reads, and
/// computes the transcript-derived history state (<see cref="HistoryStateDeriver"/>).
///
/// Gateway Cleanup mission (the cut): the Director no longer registers a <c>/sessions/{sid}/history</c>
/// route - the read reaches this mapper only through the shared <see cref="SessionReadExecutor"/> core
/// (verb "history") over the tunnel. This class survives purely as that mapper's home.
/// </summary>
internal static class SessionHistoryEndpoint
{
    /// <summary>
    /// Build the wire DTO from a session. Pure mapping over the Core reader and deriver - no I/O
    /// of its own beyond what those perform. Internal so it is unit-testable without the host.
    /// </summary>
    internal static SessionHistoryDto BuildHistory(Session session, string sid)
    {
        var dto = new SessionHistoryDto
        {
            SessionId = sid,
            Agent = session.AgentKind.ToString(),
            IsSupported = SessionHistoryReader.IsSupported(session),
            // Gemini has no structured transcript; its history is raw terminal scrollback that the
            // Cockpit must render verbatim, not as Markdown (mirrors the desktop IsRawText path).
            IsRawText = session.AgentKind == AgentKind.Gemini,
        };

        if (!dto.IsSupported)
        {
            dto.Status = "unsupported";
            return dto;
        }

        // Fail loudly, not silently: a Claude session whose transcript file cannot be located
        // would otherwise return an EMPTY history with Status "ok" - indistinguishable from a
        // genuinely empty conversation, which starves every consumer (Cockpit history, Gateway
        // voice mode) with no diagnosable signal. Typical cause: the session-pointer hook has
        // not reported yet, so the Director only holds the launch-time session id, which goes
        // stale on /clear and auto-compaction.
        if (session.AgentKind == AgentKind.ClaudeCode)
        {
            var transcriptPath = SessionHistoryReader.ResolveTranscriptPath(session);
            if (transcriptPath is null || !File.Exists(transcriptPath))
            {
                dto.Status = "transcript-not-found";
                dto.Error = transcriptPath is null
                    ? "No transcript path is known for this session yet (the session-pointer hook has not reported)."
                    : $"The transcript file this session points at does not exist: {transcriptPath}";
                FileLog.Write($"[SessionHistoryEndpoint] transcript-not-found: sid={sid} triedPath={transcriptPath ?? "(null)"}");
                return dto;
            }
        }

        var history = SessionHistoryReader.Read(session);
        foreach (var message in history.Messages)
        {
            var msg = new HistoryMessageDto
            {
                Role = message.Role.ToString(),
                Timestamp = message.Timestamp,
                // Only a user message has an author worth asking about, and only this machine can answer:
                // the transcript shows the owner's typing and the fleet doorbell's typing as the same thing.
                // Joined on the words rather than on a clock - see PromptAuthorBuffer for why - and honestly
                // "unknown" when this Director did not see the submission or has forgotten it.
                Origin = message.Role == ConversationRole.User
                    ? PromptAuthorBuffer.AuthorOf(sid, TextOf(message))
                    : null,
            };
            foreach (var part in message.Parts)
            {
                msg.Parts.Add(new HistoryPartDto
                {
                    Kind = part.Kind.ToString(),
                    Text = part.Text,
                    ToolName = part.ToolName,
                    ToolId = part.ToolId,
                });
            }
            dto.Messages.Add(msg);
        }

        // Transcript-derived history state (#736 / #741): Claude only - the background-agent
        // lifecycle signal lives in the Claude transcript format. Computed here because
        // process-liveness (Backend.IsRunning) is known only Director-side. This NEVER reads or
        // writes the live byte-based status; it is a separate, additive label.
        if (session.AgentKind == AgentKind.ClaudeCode)
        {
            var path = SessionHistoryReader.ResolveTranscriptPath(session);
            var analysis = HistoryStateDeriver.AnalyzeFile(path);
            dto.HistoryState = HistoryStateDeriver.Derive(analysis, session.Backend.IsRunning).ToString();
        }

        return dto;
    }

    /// <summary>
    /// The words of a message, for matching it against what this Director submitted: its Text parts only,
    /// joined the way they were written. A user turn's tool-result parts are the agent feeding itself and
    /// were never typed by anyone, so they are not part of what was submitted and must not be matched on.
    /// </summary>
    private static string TextOf(CcDirector.Core.History.ConversationMessage message)
    {
        if (message.Parts.Count == 1 && message.Parts[0].Kind == CcDirector.Core.History.ConversationPartKind.Text)
            return message.Parts[0].Text;

        var sb = new System.Text.StringBuilder();
        foreach (var part in message.Parts)
        {
            if (part.Kind != CcDirector.Core.History.ConversationPartKind.Text) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(part.Text);
        }
        return sb.ToString();
    }
}
