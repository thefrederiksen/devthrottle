using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the single Gateway speech-to-text owner (issue #839),
/// <see cref="GatewayTranscriptionService"/>. Cover the branches that do not call a live provider:
/// the mode-and-key resolution for local / remote-with-key / remote-no-key, and the
/// <see cref="GatewayTranscriptionService.TranscribeAsync"/> outcomes for no-audio and no-key. The
/// success path is exercised by the live provider tests; here we prove the single resolver and the
/// outcome mapping without a network. Uses a temp-file vault and CC_DIRECTOR_ROOT (so the test owns
/// the transcription_mode config) - in the "DirectorRoot" collection because it sets CC_DIRECTOR_ROOT.
/// </summary>
[Collection("DirectorRoot")]
public sealed class GatewayTranscriptionServiceTests : IDisposable
{
    private readonly string? _prevRoot;
    private readonly string _root;
    private readonly string _vaultPath;

    public GatewayTranscriptionServiceTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-gtsvc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _vaultPath = Path.Combine(_root, "keyvault.json");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private GatewayTranscriptionService Service() => new(new KeyVault(_vaultPath));

    [Fact]
    public void Resolve_ByoMode_NoKey_ReportsRemoteWithNoKey()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.Byo);

        var routing = Service().Resolve();

        Assert.False(routing.IsLocal);
        Assert.Null(routing.Key);
        Assert.Equal(TranscriptionMode.Byo, routing.Mode);
    }

    [Fact]
    public void Resolve_ByoMode_WithKey_ComposesOpenAiTarget()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.Byo);
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.OpenAiKeyName, "sk-byo-123");

        var routing = Service().Resolve();

        Assert.False(routing.IsLocal);
        Assert.Equal("sk-byo-123", routing.Key);
        var resolved = routing.ToResolved();
        Assert.Equal(TranscriptionEndpointResolver.OpenAiBaseUrl, resolved.BaseUrl);
        Assert.Equal("sk-byo-123", resolved.ApiKey);
        Assert.Equal(TranscriptionTransport.Realtime, resolved.Transport);
    }

    [Fact]
    public void Resolve_LocalMode_IsLocalWithNoKey()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.Local);

        var routing = Service().Resolve();

        Assert.True(routing.IsLocal);
        Assert.Null(routing.Key);
        // The local routing has no remote target to compose.
        Assert.Throws<InvalidOperationException>(() => routing.ToResolved());
    }

    [Fact]
    public async Task TranscribeAsync_NoAudio_ReturnsNoAudioOutcome()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.Byo);
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.OpenAiKeyName, "sk-byo-123");

        var result = await Service().TranscribeAsync(Array.Empty<byte>(), "audio.webm", "audio/webm", applyCorrection: false, CancellationToken.None);

        Assert.Equal(TranscriptionOutcome.NoAudio, result.Outcome);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task TranscribeAsync_RemoteModeNoKey_ReturnsNoKeyOutcome_WithMode()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.Byo);
        // No key seeded.

        var result = await Service().TranscribeAsync(new byte[] { 1, 2, 3 }, "audio.webm", "audio/webm", applyCorrection: false, CancellationToken.None);

        Assert.Equal(TranscriptionOutcome.NoKey, result.Outcome);
        Assert.Equal("byo", result.Mode);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task TranscribeSegmentRawAsync_RemoteModeNoKey_Throws()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);
        // No key seeded for DevThrottle.

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service().TranscribeSegmentRawAsync(new byte[] { 1, 2, 3 }, "audio.webm", "audio/webm", CancellationToken.None));
    }

    [Theory]
    [InlineData("audio/webm;codecs=opus", "webm")]
    [InlineData("audio/wav", "wav")]
    [InlineData("audio/mp4", "m4a")]
    [InlineData("", "webm")]
    public void ExtensionFor_MapsMimeToExtension(string contentType, string expected)
        => Assert.Equal(expected, GatewayTranscriptionService.ExtensionFor(contentType));
}
