namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body for <c>POST /sessions/{sid}/wingman/ask</c>. Stateless: the wingman never has
/// memory between asks.
/// </summary>
public sealed class WingmanAskRequest
{
    /// <summary>The user's question about this session. Free text, max ~2000 chars.</summary>
    public string Question { get; set; } = "";

    /// <summary>
    /// Optional mode.
    /// <list type="bullet">
    /// <item>"explain": ignore <see cref="Question"/> and produce a terse session
    /// briefing (what's happened + what the agent is waiting on) on the strong model.</item>
    /// <item>null / anything else (with a <see cref="Question"/>): the faithful
    /// "Ask the Wingman" channel - a read-only full-power session over the whole
    /// terminal + repo, on the strong model, that answers completely and reads content
    /// VERBATIM when asked rather than summarizing.</item>
    /// </list>
    /// </summary>
    public string? Mode { get; set; }
}
