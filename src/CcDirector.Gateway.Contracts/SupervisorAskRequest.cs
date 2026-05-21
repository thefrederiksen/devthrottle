namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Phase 5: body for <c>POST /sessions/{sid}/supervisor/ask</c>. The user's question
/// is piped into a fresh, stateless Haiku call alongside this session's recent
/// state. The supervisor never has memory between asks.
/// </summary>
public sealed class SupervisorAskRequest
{
    /// <summary>The user's question about this session. Free text, max ~2000 chars.</summary>
    public string Question { get; set; } = "";
}
