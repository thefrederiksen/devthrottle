namespace CcDirector.Core.Drivers;

/// <summary>
/// Maps a Claude model id to its context-window size in tokens - the denominator the live context
/// gauge needs to turn a raw used-token count into a percent. Owned by the Claude driver (not the
/// user interface) because the model-to-window relationship is a Claude fact we must maintain as
/// models change. An id this table does not recognize returns null, which drives the explicit
/// raw-number fallback (no fake percent) rather than a guess.
/// </summary>
public static class ClaudeContextWindow
{
    /// <summary>The standard Claude context window: 200,000 tokens.</summary>
    public const long StandardWindowTokens = 200_000;

    /// <summary>The extended Opus context window requested via the <c>[1m]</c> model-id suffix:
    /// 1,000,000 tokens.</summary>
    public const long ExtendedWindowTokens = 1_000_000;

    /// <summary>The model families the standard window applies to. The model id carried in the
    /// transcript (e.g. <c>claude-sonnet-4-5-20250929</c>) and the picker ids (e.g. <c>opus</c>)
    /// both contain one of these family names.</summary>
    private static readonly string[] KnownFamilies = ["opus", "sonnet", "haiku", "fable"];

    /// <summary>
    /// The context-window size in tokens for a model id, or null when the id is not a recognized
    /// Claude model (the gauge then shows the raw used-token count with no percent).
    /// </summary>
    /// <param name="modelId">The model id (a picker id like <c>opus[1m]</c> or a transcript model
    /// like <c>claude-opus-4-8[1m]</c>); null or empty yields null.</param>
    public static long? WindowTokensForModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var id = modelId.Trim();

        // The 1-million-token window is requested by the [1m] suffix on the model id; it has no
        // separate flag, so the suffix is the only signal and it wins over the family default.
        if (id.Contains("[1m]", StringComparison.OrdinalIgnoreCase))
            return ExtendedWindowTokens;

        foreach (var family in KnownFamilies)
            if (id.Contains(family, StringComparison.OrdinalIgnoreCase))
                return StandardWindowTokens;

        return null;
    }
}
