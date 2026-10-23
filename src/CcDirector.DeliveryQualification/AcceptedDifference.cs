namespace CcDirector.DeliveryQualification;

/// <summary>
/// THE ONE DIFFERENCE THE PARITY MATRIX ACCEPTS (issue #4378, the mission's 11.2 and 11.5): the agent's own trim of the
/// spaces at the edge of a prompt. Codex and Pi drop the leading spaces and Claude Code the trailing ones, whatever route
/// the text came by - a person typing at the agent gets exactly the same - so it is not a Director fault. Only that trim,
/// on that agent's edge, is accepted: a text trimmed at the other edge, at both, or changed anywhere else still fails.
/// </summary>
public static class AcceptedDifference
{
    /// <summary>
    /// Why <paramref name="got"/> (the agent's record, line endings made canonical) is accepted although it differs from
    /// <paramref name="sent"/>, or null when it is not. Null too when the two are equal - nothing needs accepting.
    /// </summary>
    public static string? AgentTrim(string agent, string sent, string got)
    {
        var want = EchoAnalysis.Canonical(sent);
        if (got == want) return null;
        var (trimmed, why) = agent switch
        {
            "claude" => (want.TrimEnd(' '), "Claude Code trims trailing spaces"),
            "codex" => (want.TrimStart(' '), "Codex trims leading spaces"),
            "pi" => (want.TrimStart(' '), "Pi trims leading spaces"),
            _ => (want, ""),
        };
        return trimmed != want && got == trimmed ? why : null;
    }
}
