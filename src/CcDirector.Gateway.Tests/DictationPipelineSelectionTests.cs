using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation.Providers;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #513: the dictation pipeline must select its provider/cleanup/preview from the routing
/// TRANSPORT, never opening a wire the provider does not offer. These tests pin
/// <see cref="DictationEndpoint.BuildPipelineComponents"/> (the pure selection step):
///   - batch transport (DevThrottle/Groq) -> the batch <see cref="OpenAiTranscriptionProvider"/>,
///     NO cleanup (no LLM round-trip), NO preview, and crucially NEVER the realtime WebSocket
///     provider.
///   - realtime transport (BYO/OpenAI) -> the <see cref="OpenAiRealtimeProvider"/> with cleanup and
///     preview.
/// In the same assembly that already has InternalsVisibleTo into CcDirector.ControlApi.
/// </summary>
public sealed class DictationPipelineSelectionTests
{
    private static ResolvedTranscription DevThrottleRouting() => new()
    {
        BaseUrl = TranscriptionEndpointResolver.DevThrottleBaseUrl,
        ApiKey = "dt_live_test",
        Transport = TranscriptionTransport.Batch,
        Model = TranscriptionEndpointResolver.DevThrottleModel,
        Mode = TranscriptionMode.DevThrottle,
    };

    private static ResolvedTranscription ByoRouting() => new()
    {
        BaseUrl = TranscriptionEndpointResolver.OpenAiBaseUrl,
        ApiKey = "sk-test",
        Transport = TranscriptionTransport.Realtime,
        Model = TranscriptionEndpointResolver.OpenAiModel,
        Mode = TranscriptionMode.Byo,
    };

    [Fact]
    public void BuildPipeline_BatchTransport_SelectsBatchProvider_NoRealtime_NoCleanup_NoPreview()
    {
        var components = DictationEndpoint.BuildPipelineComponents(
            DevThrottleRouting(), apiKey: "dt_live_test", options: new AgentOptions());

        // The batch provider is selected and the realtime WebSocket provider is NEVER constructed.
        Assert.IsType<OpenAiTranscriptionProvider>(components.Provider);
        Assert.IsNotType<OpenAiRealtimeProvider>(components.Provider);
        // Cleanup and preview are skipped on the batch path (no LLM round-trip to devthrottle.com).
        Assert.Null(components.Cleanup);
        Assert.Null(components.Preview);
    }

    [Fact]
    public async Task BuildPipeline_RealtimeTransport_SelectsRealtimeProvider_WithCleanupAndPreview()
    {
        var components = DictationEndpoint.BuildPipelineComponents(
            ByoRouting(), apiKey: "sk-test", options: new AgentOptions());

        Assert.IsType<OpenAiRealtimeProvider>(components.Provider);
        Assert.NotNull(components.Cleanup);
        Assert.NotNull(components.Preview);

        // Dispose the realtime components the test owns (the batch path created none).
        components.Cleanup?.Dispose();
        if (components.Preview is not null) await components.Preview.DisposeAsync();
        await components.Provider.DisposeAsync();
    }
}
