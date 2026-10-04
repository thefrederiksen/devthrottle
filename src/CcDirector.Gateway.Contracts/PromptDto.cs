using System.Text.Json.Serialization;

namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One recorded message on its way from a Director to the Gateway's prompt log (issue #1551), and the
/// shape the Gateway stores and serves back.
///
/// The Director captures this - it is the only thing that sees a prompt at all, and the only thing that
/// knows whether it was typed or spoken and from which surface. The Gateway holds it, because the
/// Gateway is what the whole fleet reports to and what moves to the server.
/// </summary>
public sealed record PromptRecord
{
    [JsonPropertyName("ts")] public required DateTime TsUtc { get; init; }

    /// <summary>Which machine this came from. The Gateway holds the whole fleet, so without this the
    /// records of two developers are indistinguishable.</summary>
    [JsonPropertyName("machine")] public string? Machine { get; init; }

    /// <summary>The DIRECTOR session (the window/slot). Stable for its whole life - it does NOT reset
    /// when the context is cleared - so it groups every context in one workstream.</summary>
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }

    /// <summary>
    /// The AGENT's own id for the context this message belonged to. Resets whenever the context does:
    /// Claude mints a new one on /clear AND on auto-compaction. Group by this to replay one
    /// conversation exactly as the agent saw it - everything that shared a context window.
    ///
    /// Null when the agent exposes no context identity of its own. Never invented.
    /// </summary>
    [JsonPropertyName("contextId")] public string? ContextId { get; init; }

    [JsonPropertyName("sessionName")] public string? SessionName { get; init; }
    [JsonPropertyName("repoPath")] public string? RepoPath { get; init; }
    [JsonPropertyName("agent")] public string? Agent { get; init; }
    [JsonPropertyName("missionName")] public string? MissionName { get; init; }

    /// <summary>"user" or "assistant".</summary>
    [JsonPropertyName("role")] public required string Role { get; init; }

    /// <summary>"typed" or "voice"; null for an assistant reply or an unmatched user message.</summary>
    [JsonPropertyName("modality")] public string? Modality { get; init; }

    /// <summary>"desktop" / "cockpit" / "phone" / "unknown"; null for an assistant reply.</summary>
    [JsonPropertyName("surface")] public string? Surface { get; init; }

    /// <summary>True when the source agent supplied a real timestamp; false when the Director stamped
    /// it at capture because the agent carries none (Gemini). Keeps an inferred time from reading as
    /// measured.</summary>
    [JsonPropertyName("tsFromAgent")] public required bool TimestampFromAgent { get; init; }

    [JsonPropertyName("charCount")] public required int CharCount { get; init; }
    [JsonPropertyName("wordCount")] public required int WordCount { get; init; }
    [JsonPropertyName("text")] public required string Text { get; init; }

    /// <summary>
    /// A stable reference to this one record, minted by the GATEWAY when a record is written into a team's tenant
    /// (devthrottle_internal#2305), so the Mentor can name a prompt it quotes. Whatever a client sends here is
    /// overwritten; null in a personal tenant and on every record written before it existed.
    /// </summary>
    [JsonPropertyName("id")] public string? PromptId { get; init; }

    /// <summary>
    /// In a team's tenant, the account subject of the person whose prompt this is, stamped by the GATEWAY from the
    /// calling key (devthrottle_internal#2305) - never taken from the client, whose value is overwritten. Null in a
    /// personal tenant, where the tenant already names the one person.
    /// </summary>
    [JsonPropertyName("person")] public string? PersonSubject { get; init; }
}

/// <summary>A Director's push of recorded messages to the Gateway's prompt log.</summary>
public sealed record PromptIngestRequest
{
    [JsonPropertyName("records")] public required IReadOnlyList<PromptRecord> Records { get; init; }
}

/// <summary>What the Gateway wrote, so the Director can log the outcome honestly.</summary>
public sealed record PromptIngestResponse
{
    [JsonPropertyName("written")] public required int Written { get; init; }
}
