namespace CcDirector.Core.Configuration;

/// <summary>
/// The resolved transcription target for a <see cref="TranscriptionMode"/> (issue #497): which
/// base URL the OpenAI-compatible transcription client points at, and which vault key name holds
/// the credential it presents. Pure, immutable, unit-tested - this is the single place that
/// decides routing, so the security-critical rule ("the bring-your-own OpenAI key is NEVER sent
/// to devthrottle.com") is provable in one spot.
/// </summary>
public sealed record TranscriptionEndpoint
{
    /// <summary>The OpenAI-compatible base URL, e.g. <c>https://api.openai.com/v1</c>.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>The vault key name that holds the credential for this mode.</summary>
    public required string KeyName { get; init; }

    /// <summary>The mode this endpoint was resolved for.</summary>
    public required TranscriptionMode Mode { get; init; }

    /// <summary>True when this endpoint targets DevThrottle's managed proxy.</summary>
    public bool IsDevThrottle => Mode == TranscriptionMode.DevThrottle;
}

/// <summary>
/// Maps a <see cref="TranscriptionMode"/> to its <see cref="TranscriptionEndpoint"/> and validates
/// the key formats. Stateless and pure so the routing decision is fully unit-testable.
/// </summary>
public static class TranscriptionEndpointResolver
{
    /// <summary>OpenAI-compatible base URL used by the bring-your-own-key path.</summary>
    public const string OpenAiBaseUrl = "https://api.openai.com/v1";

    /// <summary>OpenAI-compatible base URL used by the DevThrottle managed proxy path.</summary>
    public const string DevThrottleBaseUrl = "https://devthrottle.com/api/v1";

    /// <summary>Vault key name for the user's own OpenAI key (the bring-your-own-key mode).</summary>
    public const string OpenAiKeyName = "OPENAI_API_KEY";

    /// <summary>Vault key name for the DevThrottle-issued key (the DevThrottle mode).</summary>
    public const string DevThrottleKeyName = "DEVTHROTTLE_API_KEY";

    /// <summary>Resolve the routing target for <paramref name="mode"/>.</summary>
    public static TranscriptionEndpoint Resolve(TranscriptionMode mode) => mode switch
    {
        TranscriptionMode.Byo => new TranscriptionEndpoint
        {
            BaseUrl = OpenAiBaseUrl,
            KeyName = OpenAiKeyName,
            Mode = TranscriptionMode.Byo,
        },
        TranscriptionMode.DevThrottle => new TranscriptionEndpoint
        {
            BaseUrl = DevThrottleBaseUrl,
            KeyName = DevThrottleKeyName,
            Mode = TranscriptionMode.DevThrottle,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown transcription mode"),
    };

    /// <summary>
    /// True when <paramref name="key"/> looks like a DevThrottle key (<c>dt_live_</c> or
    /// <c>dt_test_</c> prefix). Format-only - it does not verify the key works.
    /// </summary>
    public static bool IsValidDevThrottleKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var k = key.Trim();
        return k.StartsWith("dt_live_", StringComparison.Ordinal)
            || k.StartsWith("dt_test_", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when <paramref name="key"/> looks like an OpenAI key (<c>sk-</c> prefix). Format-only -
    /// it does not verify the key works.
    /// </summary>
    public static bool IsValidOpenAiKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        return key.Trim().StartsWith("sk-", StringComparison.Ordinal);
    }
}
