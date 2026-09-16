using System.Net;
using System.Net.Http;
using System.Text;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the single Gateway speech-to-text owner (issue #839),
/// <see cref="GatewayTranscriptionService"/>. Cover the branches that do not call a live provider:
/// the mode-and-key resolution, legacy mode migration, and the
/// <see cref="GatewayTranscriptionService.TranscribeAsync"/> outcomes for no-audio, no-key, and
/// out-of-credits (issue #885). The success path is exercised by the live provider tests; here we
/// prove the single resolver and the outcome mapping without a network. Uses a temp-file vault and
/// CC_DIRECTOR_ROOT (so the test owns the transcription_mode config) - in the "DirectorRoot"
/// collection because it sets CC_DIRECTOR_ROOT.
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

    /// <summary>
    /// A scratch archive under this test's own root. EVERY service built here must be given one:
    /// an omitted archive defaults to one over the REAL user's transcription-audio folder.
    /// CC_DIRECTOR_ROOT alone does NOT protect against that - it is process-wide, and xUnit runs other
    /// collections in parallel that clear it mid-test, so the default can resolve to the real location.
    /// Injection is the only isolation that holds.
    /// </summary>
    private TranscriptionAudioArchive ScratchArchive() => new(Path.Combine(_root, "archive-scratch"));

    private GatewayTranscriptionService Service()
        => new(new KeyVault(_vaultPath), audioArchive: ScratchArchive());

    /// <summary>Answers the transcription POST with a fixed status + body (issue #885).</summary>
    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public StatusHandler(HttpStatusCode status, string body) { _status = status; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public async Task TranscribeAsync_Success_KEEPSTheAudio()
    {
        // THE regression. The old desktop net saved the clip then deleted it the moment the words were
        // delivered, so it caught a transcription that FAILED but never one that LIED - a truncated
        // transcript is a "success", and its audio was deleted seconds later. A real turn came back with
        // 7 words out of a full 18-second recording and there was nothing left to play. Success is
        // exactly the case that must keep the audio.
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_abc");

        var archiveDir = Path.Combine(_root, "archive");
        var service = new GatewayTranscriptionService(
            new KeyVault(_vaultPath),
            http: new HttpClient(new StatusHandler(HttpStatusCode.OK, "{\"text\":\"only the first sentence\"}")),
            audioArchive: new TranscriptionAudioArchive(archiveDir));

        var audio = Enumerable.Repeat((byte)0x42, 512).ToArray();
        var result = await service.TranscribeAsync(audio, "clip.wav", "audio/wav", applyCorrection: false, default);

        Assert.Equal(TranscriptionOutcome.Ok, result.Outcome);
        var archived = Directory.GetFiles(archiveDir, "turn-*.wav");
        var file = Assert.Single(archived);
        Assert.Equal(audio, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task TranscribeAsync_ArchivedClipIsNamedForTheHistoryTurnId()
    {
        // The clip is only useful if a suspicious history line leads to it. Both the history record
        // and the file are keyed by the same turn id, so the log IS the index.
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_abc");

        var archiveDir = Path.Combine(_root, "archive");
        var historyDir = Path.Combine(_root, "history");
        var service = new GatewayTranscriptionService(
            new KeyVault(_vaultPath),
            http: new HttpClient(new StatusHandler(HttpStatusCode.OK, "{\"text\":\"hello\"}")),
            history: new TranscriptionHistoryLog(historyDir),
            audioArchive: new TranscriptionAudioArchive(archiveDir));

        await service.TranscribeAsync(
            Enumerable.Repeat((byte)0x42, 512).ToArray(), "clip.wav", "audio/wav", applyCorrection: false, default);

        var line = Assert.Single(File.ReadAllLines(Directory.GetFiles(historyDir, "*.jsonl").Single()));
        using var doc = System.Text.Json.JsonDocument.Parse(line);
        var loggedTurnId = doc.RootElement.GetProperty("turnId").GetString();
        Assert.NotNull(loggedTurnId);
        Assert.True(File.Exists(Path.Combine(archiveDir, $"turn-{loggedTurnId}.wav")));
    }

    [Fact]
    public async Task TranscribeAsync_ProviderFails_StillKeepsTheAudio()
    {
        // The clip is archived BEFORE the attempt, so a provider failure cannot lose the only copy of
        // what was said either.
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_abc");

        var archiveDir = Path.Combine(_root, "archive");
        var body = "{\"error\":{\"code\":\"insufficient_credits\",\"message\":\"no credits\"}}";
        var service = new GatewayTranscriptionService(
            new KeyVault(_vaultPath),
            http: new HttpClient(new StatusHandler(HttpStatusCode.PaymentRequired, body)),
            audioArchive: new TranscriptionAudioArchive(archiveDir));

        var audio = Enumerable.Repeat((byte)0x42, 512).ToArray();
        await service.TranscribeAsync(audio, "clip.wav", "audio/wav", applyCorrection: false, default);

        Assert.Single(Directory.GetFiles(archiveDir, "turn-*.wav"));
    }

    [Fact]
    public async Task TranscribeAsync_ProviderReturns402_YieldsOutOfCreditsOutcome()
    {
        // Issue #885: a 402 insufficient_credits from the hosted service becomes a distinct
        // OutOfCredits result (not a generic provider error), carrying the machine-readable code so
        // the endpoint returns 402 and the client shows the add-credits state.
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_abc");

        var body = "{\"error\":{\"code\":\"insufficient_credits\",\"message\":\"no credits\"}}";
        var service = new GatewayTranscriptionService(
            new KeyVault(_vaultPath),
            http: new HttpClient(new StatusHandler(HttpStatusCode.PaymentRequired, body)),
            audioArchive: ScratchArchive());

        var result = await service.TranscribeAsync(new byte[] { 1, 2, 3 }, "clip.webm", "audio/webm", applyCorrection: false, CancellationToken.None);

        Assert.Equal(TranscriptionOutcome.OutOfCredits, result.Outcome);
        Assert.Equal("insufficient_credits", result.Code);
    }

    [Fact]
    public void Resolve_LegacyByoMode_NoKey_ReportsDevThrottleWithNoKey()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.Byo);

        var routing = Service().Resolve();

        Assert.Null(routing.Key);
        Assert.Equal(TranscriptionMode.DevThrottle, routing.Mode);
    }

    [Fact]
    public void Resolve_LegacyByoMode_WithDevThrottleKey_ComposesDevThrottleTarget()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.Byo);
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_abc");

        var routing = Service().Resolve();

        Assert.Equal("dt_live_abc", routing.Key);
        var resolved = routing.ToResolved();
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, resolved.BaseUrl);
        Assert.Equal("dt_live_abc", resolved.ApiKey);
        Assert.Equal(TranscriptionTransport.Batch, resolved.Transport);
    }

    [Fact]
    public void Resolve_DevThrottleMode_NoKey_ReportsNoKey()
    {
        // Issue #887: DevThrottle is the default hosted mode. With no key set, the routing carries no
        // key (the caller reports it unavailable) and has no remote target to compose.
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);

        var routing = Service().Resolve();

        Assert.Null(routing.Key);
        Assert.Equal(TranscriptionMode.DevThrottle, routing.Mode);
        Assert.Throws<InvalidOperationException>(() => routing.ToResolved());
    }

    [Fact]
    public void Resolve_DevThrottleMode_WithKey_ComposesDevThrottleTarget()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_abc");

        var routing = Service().Resolve();

        Assert.Equal("dt_live_abc", routing.Key);
        var resolved = routing.ToResolved();
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, resolved.BaseUrl);
        Assert.Equal("dt_live_abc", resolved.ApiKey);
        Assert.Equal(TranscriptionTransport.Batch, resolved.Transport);
    }

    [Fact]
    public async Task TranscribeAsync_NoAudio_ReturnsNoAudioOutcome()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_abc");

        var result = await Service().TranscribeAsync(Array.Empty<byte>(), "audio.webm", "audio/webm", applyCorrection: false, CancellationToken.None);

        Assert.Equal(TranscriptionOutcome.NoAudio, result.Outcome);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task TranscribeAsync_NoKey_ReturnsNoKeyOutcome_WithMode()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.Byo);
        // No key seeded.

        var result = await Service().TranscribeAsync(new byte[] { 1, 2, 3 }, "audio.webm", "audio/webm", applyCorrection: false, CancellationToken.None);

        Assert.Equal(TranscriptionOutcome.NoKey, result.Outcome);
        Assert.Equal("devthrottle", result.Mode);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task TranscribeSegmentUncorrectedAsync_RemoteModeNoKey_Throws()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);
        // No key seeded for DevThrottle.

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service().TranscribeSegmentUncorrectedAsync(new byte[] { 1, 2, 3 }, "audio.webm", "audio/webm", CancellationToken.None));
    }

    [Fact]
    public async Task TranscribeAsync_DictionaryProviderThrows_StillReturnsRawText()
    {
        // Issue #2483: a malformed or unreadable dictionary file made the provider read throw AFTER
        // the transcription had already succeeded, failing the whole request. The documented contract
        // is fail-open - the dictionary corrector degrades to the raw transcript on any fault, and a
        // fault reading the dictionary is exactly such a fault.
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_abc");

        var service = new GatewayTranscriptionService(
            new KeyVault(_vaultPath),
            dictionaryProvider: _ => throw new InvalidDataException("dictionary file is malformed"),
            http: new HttpClient(new StatusHandler(HttpStatusCode.OK, "{\"text\":\"the raw words\"}")),
            audioArchive: ScratchArchive());

        var result = await service.TranscribeAsync(
            new byte[] { 1, 2, 3 }, "clip.wav", "audio/wav", applyCorrection: true, CancellationToken.None);

        Assert.Equal(TranscriptionOutcome.Ok, result.Outcome);
        Assert.Equal("the raw words", result.Text);
    }

    [Fact]
    public async Task CleanupAsync_DictionaryProviderThrows_FailsOpenToRawText()
    {
        // The Notes assemble-then-clean path goes through CleanupAsync directly; a throwing
        // dictionary provider must yield the raw text there too, never an exception.
        var service = new GatewayTranscriptionService(
            new KeyVault(_vaultPath),
            dictionaryProvider: _ => throw new InvalidDataException("dictionary file is malformed"),
            audioArchive: ScratchArchive());

        var outcome = await service.CleanupAsync("the raw words", ct: CancellationToken.None);

        Assert.Equal("the raw words", outcome.Text);
        Assert.False(outcome.Applied);
        Assert.Contains("dictionary unavailable", outcome.Reason);
    }

    [Theory]
    [InlineData("audio/webm;codecs=opus", "webm")]
    [InlineData("audio/wav", "wav")]
    [InlineData("audio/mp4", "m4a")]
    [InlineData("", "webm")]
    public void ExtensionFor_MapsMimeToExtension(string contentType, string expected)
        => Assert.Equal(expected, GatewayTranscriptionService.ExtensionFor(contentType));
}
