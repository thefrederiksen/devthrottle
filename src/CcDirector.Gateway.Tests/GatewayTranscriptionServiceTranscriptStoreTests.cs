using System.Net;
using System.Net.Http;
using System.Text;
using CcDirector.Core;
using CcDirector.Core.Audio;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Integration proof for issue #509: a single Gateway transcribe lands ONE per-tenant row in the transcript
/// store, through the one <c>RecordHistory</c> hook, with no <c>IsHosted</c> branch. Self-host resolves to the
/// <see cref="TenantId.Local"/> tenant; two tenants land in separate partitions; and an error outcome (which
/// produced no text) stores nothing, so the retention cap is spent only on real transcripts.
///
/// In the "DirectorRoot" collection because it sets CC_DIRECTOR_ROOT (owning the transcription_mode config and
/// the vault). Each transcription service is given a scratch audio archive so it never writes the real user's
/// archive, exactly as <see cref="GatewayTranscriptionServiceTests"/> does.
/// </summary>
[Collection("DirectorRoot")]
public sealed class GatewayTranscriptionServiceTranscriptStoreTests : IDisposable
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");

    private readonly string? _prevRoot;
    private readonly string _root;
    private readonly string _vaultPath;

    public GatewayTranscriptionServiceTranscriptStoreTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-gtsvc-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _vaultPath = Path.Combine(_root, "keyvault.json");

        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_abc");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

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

    private GatewayTranscriptionService ServiceFor(TranscriptStore transcripts, string okText = "hello there")
        => new(
            new KeyVault(_vaultPath),
            http: new HttpClient(new StatusHandler(HttpStatusCode.OK, "{\"text\":\"" + okText + "\"}")),
            audioArchive: new TranscriptionAudioArchive(Path.Combine(_root, "archive-scratch")),
            transcripts: transcripts);

    [Fact]
    public async Task Transcribe_WithResolvedTenant_LandsOneRowUnderThatTenant()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TranscriptStore(h.Open());
        var service = ServiceFor(store, okText: "the recorded words");

        var result = await service.TranscribeAsync(
            new byte[] { 1, 2, 3 }, "clip.webm", "audio/webm", applyCorrection: false, CancellationToken.None,
            tenant: TenantA, source: "dictation");

        Assert.Equal(TranscriptionOutcome.Ok, result.Outcome);
        var row = Assert.Single(store.List(TenantA));
        Assert.Equal("the recorded words", row.RawText);
        Assert.Equal("dictation", row.Source);
    }

    [Fact]
    public async Task Transcribe_SelfHostNoTenant_LandsUnderLocal()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TranscriptStore(h.Open());
        var service = ServiceFor(store);

        // No tenant supplied - the self-host case. It must default to the Local tenant, with no IsHosted branch.
        await service.TranscribeAsync(
            new byte[] { 1, 2, 3 }, "clip.webm", "audio/webm", applyCorrection: false, CancellationToken.None);

        Assert.Equal(1, store.Count(TenantId.Local));
    }

    [Fact]
    public async Task Transcribe_TwoTenants_LandInSeparatePartitions()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TranscriptStore(h.Open());

        await ServiceFor(store, okText: "alpha words").TranscribeAsync(
            new byte[] { 1 }, "a.webm", "audio/webm", applyCorrection: false, CancellationToken.None, tenant: TenantA);
        await ServiceFor(store, okText: "bravo words").TranscribeAsync(
            new byte[] { 2 }, "b.webm", "audio/webm", applyCorrection: false, CancellationToken.None, tenant: TenantB);

        Assert.Equal("alpha words", Assert.Single(store.List(TenantA)).RawText);
        Assert.Equal("bravo words", Assert.Single(store.List(TenantB)).RawText);
    }

    [Fact]
    public async Task Transcribe_ProviderError_StoresNoTranscriptRow()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TranscriptStore(h.Open());

        // A 402 out-of-credits produces no text - the health write still records the outcome, but there is
        // nothing to mine, so the transcript store must stay empty (the cap is spent only on real transcripts).
        var body = "{\"error\":{\"code\":\"insufficient_credits\",\"message\":\"no credits\"}}";
        var service = new GatewayTranscriptionService(
            new KeyVault(_vaultPath),
            http: new HttpClient(new StatusHandler(HttpStatusCode.PaymentRequired, body)),
            audioArchive: new TranscriptionAudioArchive(Path.Combine(_root, "archive-scratch")),
            transcripts: store);

        var result = await service.TranscribeAsync(
            new byte[] { 1, 2, 3 }, "clip.webm", "audio/webm", applyCorrection: false, CancellationToken.None,
            tenant: TenantA);

        Assert.Equal(TranscriptionOutcome.OutOfCredits, result.Outcome);
        Assert.Equal(0, store.Count(TenantA));
    }

    // ---- issue #2926: the stored raw text is the model's full output, not the gated text ----

    /// <summary>A PCM WAV built to order: each part is a 200 Hz tone at the given amplitude, so the evidence
    /// gate's answer is known before it runs (the same construction TranscriptEvidenceGateTests uses).</summary>
    private static byte[] ToneWav(params (double seconds, double amplitude)[] parts)
    {
        const int sampleRate = 16000;
        var samples = new List<short>();
        foreach (var (seconds, amplitude) in parts)
        {
            int n = (int)(seconds * sampleRate);
            for (int i = 0; i < n; i++)
                samples.Add((short)Math.Round(Math.Sin(2 * Math.PI * 200 * i / sampleRate) * amplitude * short.MaxValue));
        }
        var pcm = new byte[samples.Count * 2];
        Buffer.BlockCopy(samples.ToArray(), 0, pcm, 0, pcm.Length);
        return PcmWav.Wrap(pcm, sampleRate, 1, 16);
    }

    [Fact]
    public async Task Transcribe_GateDropsASentence_StoredRawKeepsIt_CleanedAndReturnedTextDoNot()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TranscriptStore(h.Open());

        // Three seconds of speech, one second of near-silence, three more seconds of speech. The model
        // "wrote" a sentence over the silent second - the gate must remove it from what is delivered, and
        // the record must still show it, or the removal is invisible to the verbatim audit.
        var audio = ToneWav((3.0, 0.50), (1.0, 0.0004), (3.0, 0.50));
        const string body = "{\"text\":\"the first real sentence Thank you. the second real sentence\","
            + "\"segments\":["
            + "{\"start\":0.0,\"end\":3.0,\"text\":\"the first real sentence\"},"
            + "{\"start\":3.0,\"end\":4.0,\"text\":\"Thank you.\"},"
            + "{\"start\":4.0,\"end\":7.0,\"text\":\"the second real sentence\"}]}";
        var service = new GatewayTranscriptionService(
            new KeyVault(_vaultPath),
            http: new HttpClient(new StatusHandler(HttpStatusCode.OK, body)),
            audioArchive: new TranscriptionAudioArchive(Path.Combine(_root, "archive-scratch")),
            transcripts: store);

        var result = await service.TranscribeAsync(
            audio, "clip.wav", "audio/wav", applyCorrection: false, CancellationToken.None,
            tenant: TenantA, source: "dictation");

        Assert.Equal(TranscriptionOutcome.Ok, result.Outcome);
        Assert.DoesNotContain("Thank you.", result.Text);
        Assert.Equal("Thank you.", Assert.Single(result.DroppedSentences).Text);

        var row = Assert.Single(store.List(TenantA));
        Assert.Equal("the first real sentence Thank you. the second real sentence", row.RawText);
        Assert.DoesNotContain("Thank you.", row.CleanedText);
        Assert.Equal(result.Text, row.CleanedText);
    }
}
