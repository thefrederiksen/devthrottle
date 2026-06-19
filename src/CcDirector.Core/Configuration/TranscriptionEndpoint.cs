namespace CcDirector.Core.Configuration;

/// <summary>
/// HOW the dictation pipeline talks to the transcription provider (issue #513). Different
/// providers expose different transports, and the pipeline must honor the one the routing names -
/// it can NEVER open a transport a provider does not offer.
///
///   - <see cref="Realtime"/>: OpenAI's Realtime transcription WebSocket
///     (<c>wss://api.openai.com/v1/realtime?intent=transcription</c>). True low-latency partials.
///     Only OpenAI offers it, so it is the BYO/OpenAI transport.
///   - <see cref="Batch"/>: the OpenAI-COMPATIBLE batch endpoint
///     (<c>POST /audio/transcriptions</c>). Record a speech chunk, upload it, get text back.
///     This is what the DevThrottle proxy (Groq Whisper) implements - and the ONLY thing it
///     implements; Groq has no Realtime API. So it is the DevThrottle transport.
/// </summary>
public enum TranscriptionTransport
{
    /// <summary>OpenAI Realtime transcription WebSocket. The BYO/OpenAI transport.</summary>
    Realtime = 0,

    /// <summary>OpenAI-compatible batch <c>/audio/transcriptions</c>. The DevThrottle transport.</summary>
    Batch = 1,
}

/// <summary>Parse/format helpers for <see cref="TranscriptionTransport"/>. Pure - unit-tested.</summary>
public static class TranscriptionTransportExtensions
{
    /// <summary>The wire/config string form: "realtime" or "batch".</summary>
    public static string ToConfigString(this TranscriptionTransport transport) => transport switch
    {
        TranscriptionTransport.Realtime => "realtime",
        TranscriptionTransport.Batch => "batch",
        _ => throw new ArgumentOutOfRangeException(nameof(transport), transport, "Unknown transcription transport"),
    };

    /// <summary>
    /// Parse a wire value. Any unrecognized value (including null/empty/whitespace) THROWS with the
    /// allowed set named (no-fallback rule: a typo or a missing field must not silently pick a
    /// transport, which would let the pipeline open the wrong wire to the wrong provider).
    /// </summary>
    public static TranscriptionTransport Parse(string? value)
    {
        return (value?.Trim().ToLowerInvariant()) switch
        {
            "realtime" => TranscriptionTransport.Realtime,
            "batch" => TranscriptionTransport.Batch,
            _ => throw new ArgumentException(
                $"transport '{value}' is not valid - it must be \"realtime\" or \"batch\".", nameof(value)),
        };
    }
}

/// <summary>
/// The resolved transcription target for a <see cref="TranscriptionMode"/> (issue #497): which
/// base URL the OpenAI-compatible transcription client points at, which vault key name holds the
/// credential it presents, which transport the pipeline must use, and which model. Pure, immutable,
/// unit-tested - this is the single place that decides routing, so the security-critical rule
/// ("the bring-your-own OpenAI key is NEVER sent to devthrottle.com") is provable in one spot.
/// </summary>
public sealed record TranscriptionEndpoint
{
    /// <summary>The OpenAI-compatible base URL, e.g. <c>https://api.openai.com/v1</c>.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>The vault key name that holds the credential for this mode.</summary>
    public required string KeyName { get; init; }

    /// <summary>
    /// The transport the dictation pipeline must use for this mode (issue #513). Part of the routing
    /// target so the pipeline never opens a wire the provider does not offer - DevThrottle/Groq is
    /// batch-only, BYO/OpenAI is realtime. Pinned in the same pure spot that pins the URL and model.
    /// </summary>
    public required TranscriptionTransport Transport { get; init; }

    /// <summary>
    /// The transcription model this mode uses - provider-correct (issue #513): <c>gpt-4o-transcribe</c>
    /// for BYO/OpenAI, <c>whisper-large-v3</c> for DevThrottle/Groq (the proxy returns 404
    /// model_not_found for gpt-4o-transcribe). Part of the routing target so the Gateway serves the
    /// full pair in one call (issue #506) - the same pure spot that pins the URL also pins the model.
    /// </summary>
    public required string Model { get; init; }

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

    /// <summary>
    /// The BYO/OpenAI transcription model. Matches <c>OpenAiRealtimeProvider.DefaultModel</c> /
    /// <c>OpenAiTranscriptionProvider.DefaultModel</c>; carried in the routing target the Gateway
    /// serves (issue #506). Named "Default" for back-compat with the pre-#513 shared constant.
    /// </summary>
    public const string DefaultModel = "gpt-4o-transcribe";

    /// <summary>The OpenAI/BYO transcription model (alias of <see cref="DefaultModel"/>, issue #513).</summary>
    public const string OpenAiModel = DefaultModel;

    /// <summary>
    /// The DevThrottle/Groq transcription model (issue #513). The DevThrottle batch Whisper proxy
    /// serves <c>whisper-large-v3</c> and returns 404 model_not_found for <c>gpt-4o-transcribe</c>,
    /// so DevThrottle mode must carry this provider-correct model - never the shared OpenAI default.
    /// </summary>
    public const string DevThrottleModel = "whisper-large-v3";

    /// <summary>Resolve the routing target for <paramref name="mode"/> (URL + key + transport + model).</summary>
    public static TranscriptionEndpoint Resolve(TranscriptionMode mode) => mode switch
    {
        TranscriptionMode.Byo => new TranscriptionEndpoint
        {
            BaseUrl = OpenAiBaseUrl,
            KeyName = OpenAiKeyName,
            Transport = TranscriptionTransport.Realtime,
            Model = OpenAiModel,
            Mode = TranscriptionMode.Byo,
        },
        TranscriptionMode.DevThrottle => new TranscriptionEndpoint
        {
            BaseUrl = DevThrottleBaseUrl,
            KeyName = DevThrottleKeyName,
            Transport = TranscriptionTransport.Batch,
            Model = DevThrottleModel,
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
