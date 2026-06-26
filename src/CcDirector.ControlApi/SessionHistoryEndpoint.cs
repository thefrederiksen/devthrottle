using CcDirector.Core.Agents;
using CcDirector.Core.History;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// GET /sessions/{sid}/history - the parsed, agent-agnostic conversation history for a session.
/// Reuses Core's <see cref="SessionHistoryReader"/> so every supported agent is covered, then maps
/// the normalized <c>ConversationHistory</c> into the wire <see cref="SessionHistoryDto"/> the
/// Cockpit reads. Also computes the transcript-derived history state (<see cref="HistoryStateDeriver"/>)
/// so the Cockpit can show the same experimental label as the desktop History tab without
/// re-reading the transcript itself. The Gateway forwards this verbatim through its generic
/// <c>/sessions/{sid}/{**rest}</c> proxy, so no Gateway change is needed.
/// </summary>
internal static class SessionHistoryEndpoint
{
    public static void Map(IEndpointRouteBuilder app, SessionManager sessionManager)
    {
        app.MapGet("/sessions/{sid}/history", (string sid) =>
        {
            FileLog.Write($"[SessionHistoryEndpoint] GET /sessions/{sid}/history");
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            try
            {
                var dto = BuildHistory(session, sid);
                FileLog.Write($"[SessionHistoryEndpoint] history: sid={sid} agent={dto.Agent} messages={dto.Messages.Count} state={dto.HistoryState ?? "(none)"}");
                return Results.Json(dto);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionHistoryEndpoint] history FAILED: sid={sid} {ex.Message}");
                return Results.Problem($"history read failed: {ex.Message}");
            }
        });
    }

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

        var history = SessionHistoryReader.Read(session);
        foreach (var message in history.Messages)
        {
            var msg = new HistoryMessageDto
            {
                Role = message.Role.ToString(),
                Timestamp = message.Timestamp,
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
}
