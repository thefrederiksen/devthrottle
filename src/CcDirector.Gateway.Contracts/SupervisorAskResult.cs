namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Phase 5: response from <c>POST /sessions/{sid}/supervisor/ask</c>. One Haiku
/// round-trip; no conversation memory. The <see cref="ContextDigest"/> is a short
/// human-readable string describing what session state was piped into the prompt,
/// so the UI can show the user what the supervisor "saw".
/// </summary>
public sealed class SupervisorAskResult
{
    /// <summary>Plain-text answer from Haiku. Trimmed and length-capped.</summary>
    public string Answer { get; set; } = "";

    /// <summary>Model used for the answer, e.g. "haiku". Empty when supervisor not configured.</summary>
    public string Model { get; set; } = "";

    /// <summary>Round-trip latency for the supervisor call (ms).</summary>
    public long LatencyMs { get; set; }

    /// <summary>
    /// One-line summary of what context was piped to the supervisor, e.g.
    /// "events:12, turns:5, buffer:3.8KB, repo:cc-director". Lets the UI explain
    /// to the user WHY the supervisor's answer is what it is.
    /// </summary>
    public string ContextDigest { get; set; } = "";

    /// <summary>"ok" | "supervisor_failed" | "no_claude" | "bad_request".</summary>
    public string Status { get; set; } = "ok";

    /// <summary>Free-text error detail when Status != "ok".</summary>
    public string? Error { get; set; }
}
