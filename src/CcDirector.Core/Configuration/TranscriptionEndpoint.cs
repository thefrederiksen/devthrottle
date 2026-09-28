namespace CcDirector.Core.Configuration;

/// <summary>
/// Provider transport metadata used by the Gateway transcription owner. Production transcription is
/// Gateway-owned and batch-only: callers send complete audio to the Gateway and the Gateway performs
/// the provider upload.
/// </summary>
public enum TranscriptionTransport
{
    /// <summary>Provider-compatible batch upload transport.</summary>
    Batch = 0,
}

/// <summary>Parse/format helpers for <see cref="TranscriptionTransport"/>. Pure - unit-tested.</summary>
public static class TranscriptionTransportExtensions
{
    /// <summary>The wire/config string form: "batch".</summary>
    public static string ToConfigString(this TranscriptionTransport transport) => transport switch
    {
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
            "batch" => TranscriptionTransport.Batch,
            _ => throw new ArgumentException(
                $"transport '{value}' is not valid - it must be \"batch\".", nameof(value)),
        };
    }
}

/// <summary>
/// The resolved DevThrottle transcription target: which base URL the Gateway-owned transcription
/// transport points at, which vault key name holds the credential it presents, which transport the
/// Gateway must use, and which model.
/// </summary>
public sealed record TranscriptionEndpoint
{
    /// <summary>The provider-compatible base URL.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>The vault key name that holds the credential for this mode.</summary>
    public required string KeyName { get; init; }

    /// <summary>
    /// The transport the Gateway transcription service must use for this mode. Production
    /// transcription is batch-only so no Director surface can open a provider realtime socket.
    /// </summary>
    public required TranscriptionTransport Transport { get; init; }

    /// <summary>The DevThrottle transcription model.</summary>
    public required string Model { get; init; }

    /// <summary>The mode this endpoint was resolved for.</summary>
    public required TranscriptionMode Mode { get; init; }

    /// <summary>True when this endpoint targets DevThrottle's hosted transcription.</summary>
    public bool IsDevThrottle => Mode == TranscriptionMode.DevThrottle;

    /// <summary>
    /// The vault key name for this mode. Both modes are key-bearing (issue #887), so this always
    /// returns the name; kept as a method so existing call sites compile unchanged.
    /// </summary>
    public string RequireKeyName() => KeyName;

    /// <summary>
    /// The remote base URL for this mode. Both modes are remote (issue #887), so this always returns
    /// the URL; kept as a method so existing call sites compile unchanged.
    /// </summary>
    public string RequireBaseUrl() => BaseUrl;
}

/// <summary>
/// Maps the configured mode to DevThrottle's hosted endpoint. Legacy mode values are accepted by the
/// enum for upgrade compatibility but route here.
/// </summary>
public static class TranscriptionEndpointResolver
{
    /// <summary>Provider-compatible base URL used by the Gateway for DevThrottle hosted AI.</summary>
    public const string DevThrottleBaseUrl = "https://devthrottle.com/api/v1";

    /// <summary>Vault key name for the DevThrottle-issued key.</summary>
    public const string DevThrottleKeyName = "DEVTHROTTLE_API_KEY";

    /// <summary>
    /// The DevThrottle transcription model: whisper-large-v3, an acoustic, non-generative model. The
    /// hosted DevThrottle API owns the upstream provider and serves every request with whisper-large-v3
    /// (whisper-1 as its fallback) whatever model a client names, so this id is what the Gateway sends,
    /// logs and stores - it must name the model that really transcribes.
    /// </summary>
    public const string DevThrottleModel = "whisper-large-v3";

    /// <summary>
    /// The dictation dictionary-cleanup model id. Kept separate from the general Wingman fast model so
    /// dictation-flavored calls stay distinguishable from wingman chat. This is a DevThrottle-served
    /// internal id (issue #1360): the hosted proxy maps it to its upstream and meters it as an included
    /// service instead of billing credits. Catalog model ids bill credits on every path.
    /// </summary>
    public const string DevThrottleDictationCleanupModel = "devthrottle/dictation-cleanup";

    /// <summary>
    /// The DevThrottle thinking model id. An internal DevThrottle-served id (issue #1360), not a catalog
    /// model: the hosted proxy maps it to its upstream and meters it as the included wingman service,
    /// never billing credits. A catalog id here would bill credits, which the Included AI ruling forbids
    /// for an internal feature.
    /// </summary>
    public const string DevThrottleWingmanModel = "devthrottle/wingman";

    /// <summary>The DevThrottle fast model id. Same contract as <see cref="DevThrottleWingmanModel"/>:
    /// an internal DevThrottle-served id, metered as included wingman usage, never billed to credits.</summary>
    public const string DevThrottleWingmanFastModel = "devthrottle/wingman-fast";

    /// <summary>
    /// The id prefix that marks a model as one of DevThrottle's internal included-service ids
    /// (issue #1360). A saved wingman or Car Mode model that does NOT carry this prefix is a catalog
    /// model - it would bill credits, so resolution falls forward to the included default instead of
    /// honoring it.
    /// </summary>
    public const string DevThrottleIncludedModelPrefix = "devthrottle/";

    /// <summary>True when <paramref name="model"/> is one of DevThrottle's internal included-service
    /// ids (carries the <see cref="DevThrottleIncludedModelPrefix"/>).</summary>
    public static bool IsDevThrottleIncludedModel(string? model)
        => model is not null && model.TrimStart().StartsWith(DevThrottleIncludedModelPrefix, StringComparison.Ordinal);

    /// <summary>The default DevThrottle text-to-speech model.</summary>
    public const string DevThrottleTtsModel = "hexgrad/Kokoro-82M";

    /// <summary>The default DevThrottle voice.</summary>
    public const string DevThrottleTtsVoice = "af_bella";

    /// <summary>The default speech model for <paramref name="mode"/> (see <see cref="ResolveTts"/>.Model).</summary>
    public static string DefaultTtsModel(TranscriptionMode mode) => ResolveTts(mode).Model;

    /// <summary>The default voice for <paramref name="mode"/>.</summary>
    public static string DefaultTtsVoice(TranscriptionMode mode) => DevThrottleTtsVoice;

    /// <summary>
    /// Resolve the wingman chat-completions target for <paramref name="mode"/> (base URL + key name +
    /// model). Same base + key as transcription; the caller appends <c>/chat/completions</c>.
    /// </summary>
    public static ProviderEndpoint ResolveWingman(TranscriptionMode mode) =>
        new(DevThrottleBaseUrl, DevThrottleKeyName, DevThrottleWingmanModel);

    /// <summary>
    /// Resolve the fast wingman chat-completions target for <paramref name="mode"/>. Same base URL and
    /// key as <see cref="ResolveWingman"/>; only the default model differs.
    /// </summary>
    public static ProviderEndpoint ResolveWingmanFast(TranscriptionMode mode) =>
        new(DevThrottleBaseUrl, DevThrottleKeyName, DevThrottleWingmanFastModel);

    /// <summary>
    /// Resolve the dictation-cleanup judge target for <paramref name="mode"/>. Same base URL and key as
    /// <see cref="ResolveWingman"/>; only the model differs, so the judge is metered as the included
    /// dictation service rather than billing a member's credits. The caller appends
    /// <c>/chat/completions</c>.
    /// </summary>
    public static ProviderEndpoint ResolveDictationCleanup(TranscriptionMode mode) =>
        new(DevThrottleBaseUrl, DevThrottleKeyName, DevThrottleDictationCleanupModel);

    /// <summary>
    /// Resolve the text-to-speech target for <paramref name="mode"/> (base URL + key name + model).
    /// Same base + key as transcription; the caller appends <c>/audio/speech</c>. The voice is a
    /// separate choice (<see cref="TtsVoiceConfig"/>), not part of the routing target.
    /// </summary>
    public static ProviderEndpoint ResolveTts(TranscriptionMode mode) =>
        new(DevThrottleBaseUrl, DevThrottleKeyName, DevThrottleTtsModel);

    /// <summary>Resolve the routing target for <paramref name="mode"/> (URL + key + transport + model).</summary>
    public static TranscriptionEndpoint Resolve(TranscriptionMode mode) => new()
    {
        BaseUrl = DevThrottleBaseUrl,
        KeyName = DevThrottleKeyName,
        Transport = TranscriptionTransport.Batch,
        Model = DevThrottleModel,
        Mode = TranscriptionMode.DevThrottle,
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

}

/// <summary>
/// A provider-compatible target: the <c>/v1</c> base URL, the vault key name that holds the
/// credential, and the model. Used for the wingman chat-completions call and the text-to-speech call
/// (<see cref="TranscriptionEndpointResolver.ResolveWingman"/> / <see cref="TranscriptionEndpointResolver.ResolveTts"/>).
/// The caller appends the operation path (<c>/chat/completions</c> or <c>/audio/speech</c>).
/// </summary>
public sealed record ProviderEndpoint(string BaseUrl, string KeyName, string Model);
