using CcDirector.Core.Drivers;

namespace CcDirector.Core.Pi;

/// <summary>
/// Maps a pi model id to its context-window size in tokens - the denominator the context gauge needs.
/// pi runs models from several providers and does NOT record the window in its session file (unlike
/// Codex, which does), so the window is inferred from the model id. A model id this table does not
/// recognize returns null, which drives the explicit raw-number fallback rather than a guessed window.
/// </summary>
public static class PiContextWindow
{
    /// <summary>The window for OpenAI's gpt-5.5 as reported by the Codex backend's
    /// <c>model_context_window</c> (258,400 tokens). pi drives the same backend via the
    /// <c>openai-codex</c> provider, so the value matches.</summary>
    public const long Gpt55WindowTokens = 258_400;

    /// <summary>The context-window size for a pi model id, or null when unrecognized (the gauge then
    /// shows the raw used-token count with no percent).</summary>
    public static long? WindowTokensForModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var id = modelId.Trim();

        // Claude families: reuse the Claude window table (handles the [1m] suffix too).
        var claude = ClaudeContextWindow.WindowTokensForModel(id);
        if (claude is not null)
            return claude;

        if (id.Contains("gpt-5.5", StringComparison.OrdinalIgnoreCase))
            return Gpt55WindowTokens;

        return null;
    }
}
