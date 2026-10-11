using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Transcription;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Drives the REAL /voice-test endpoint over real HTTP.
///
/// The behaviour worth pinning is not the happy path - it is what happens when transcription is not
/// available. The clip must still be stored, because a clip that could not be transcribed is the most
/// interesting one there is, and the caller must still be told plainly rather than shown an empty
/// result that reads as "your microphone is broken".
///
/// Storage is redirected to a temp root through CC_DIRECTOR_ROOT so the suite never writes into the
/// developer's own clip store.
/// </summary>
[Collection("VoiceTestEndpoint")] // serial: mutates the CC_DIRECTOR_ROOT process env var
public sealed class VoiceTestEndpointTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousRoot;

    public VoiceTestEndpointTests()
    {
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "cc-voice-test-ep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Boot the real endpoint against a real clip store in a temp directory. The transcription service
    /// is real but has no key, which is exactly the state a self-host install starts in.
    /// </summary>
    private async Task<(WebApplication App, HttpClient Http, VoiceTestClipStore Store)> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        var vault = new KeyVault(Path.Combine(_root, "test.vault"));
        var store = new VoiceTestClipStore(Path.Combine(_root, "clips"));
        // The boundary is required and non-nullable now (finding I1-01). Self-host harness, so the REAL
        // self-host boundary: built over the SingleTenantContext, it always resolves Local.
        VoiceTestEndpoint.Map(app, new GatewayTranscriptionService(vault, new TranscriptionAudioArchive(false)),
            tenantBoundary: new CcDirector.Gateway.Tenancy.HostedTenantBoundary(
                new CcDirector.Core.Tenancy.SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry(), hosted: false),
            storeOverride: store);

        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, store);
    }

    private static MultipartFormDataContent Clip(
        string kind, byte[]? audio = null, string? language = null, string? expected = null, string? quality = null)
    {
        var form = new MultipartFormDataContent();
        if (audio is not null)
        {
            var file = new ByteArrayContent(audio);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "audio", "voice-test.wav");
        }
        form.Add(new StringContent(kind), "kind");
        if (language is not null) form.Add(new StringContent(language), "language");
        if (expected is not null) form.Add(new StringContent(expected), "expected");
        if (quality is not null) form.Add(new StringContent(quality), "quality");
        return form;
    }

    private static byte[] Audio(int bytes = 2048) => Enumerable.Range(0, bytes).Select(i => (byte)i).ToArray();

    [Fact]
    public async Task MicrophoneClip_IsStoredWithItsMeasurements_AndNeedsNoTranscription()
    {
        var (app, http, store) = await StartAsync();
        await using var _ = app;

        var res = await http.PostAsync("/voice-test/clip",
            Clip(VoiceTestKind.Microphone, Audio(), quality: """{"narrowband":true}"""));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var stored = Assert.Single(store.List());
        Assert.Equal(VoiceTestKind.Microphone, stored.Kind);
        Assert.NotNull(stored.Quality);
        Assert.True(stored.Quality!.Value.GetProperty("narrowband").GetBoolean());
        // A microphone check asks nothing of the transcriber, so it must not be blocked by a missing key.
        Assert.Null(stored.Transcript);
    }

    [Fact]
    public async Task TranscriptionClip_WithNoKey_SaysSoPlainly_AndSTILLStoresTheClip()
    {
        // The case that matters: the check cannot run, but the evidence must not be thrown away, and
        // the user must be told why rather than shown a blank score.
        var (app, http, store) = await StartAsync();
        await using var _ = app;

        var res = await http.PostAsync("/voice-test/clip",
            Clip(VoiceTestKind.Transcription, Audio(), language: "da", expected: "I gaar afsluttede jeg seks"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("no key configured", body, StringComparison.OrdinalIgnoreCase);

        var stored = Assert.Single(store.List());
        Assert.Equal("da", stored.Language);
        Assert.Equal("I gaar afsluttede jeg seks", stored.ExpectedText);
        Assert.Equal("no_key", stored.Outcome);
    }

    [Fact]
    public async Task TheStoredClipKeepsTheAudioOnDisk_NotJustTheMetadata()
    {
        var (app, http, store) = await StartAsync();
        await using var _ = app;

        await http.PostAsync("/voice-test/clip", Clip(VoiceTestKind.Microphone, Audio(1234)));

        var stored = Assert.Single(store.List());
        var audioPath = Path.Combine(store.Directory, $"clip-{stored.ClipId}.wav");
        Assert.True(File.Exists(audioPath));
        Assert.Equal(1234, new FileInfo(audioPath).Length);
        Assert.Equal(1234, stored.AudioBytes);
    }

    [Fact]
    public async Task NoAudio_Is400()
    {
        var (app, http, _) = await StartAsync();
        await using var __ = app;

        var res = await http.PostAsync("/voice-test/clip", Clip(VoiceTestKind.Microphone, audio: null));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task AnUnknownKind_Is400_RatherThanBeingStoredAsSomethingUnreadable()
    {
        var (app, http, store) = await StartAsync();
        await using var _ = app;

        var res = await http.PostAsync("/voice-test/clip", Clip("something-else", Audio()));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task AClipOverTheLimit_IsRefused()
    {
        var (app, http, store) = await StartAsync();
        await using var _ = app;

        var tooBig = new byte[CcDirector.Gateway.Voice.VoiceUploadLimits.MaxOneShotFileBytes + 1024];
        var res = await http.PostAsync("/voice-test/clip", Clip(VoiceTestKind.Microphone, tooBig));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task UnparseableMeasurements_AreDropped_NotFatal()
    {
        // The measurements are a bonus for later analysis. Losing them must never cost the user the
        // clip they were actually asking about.
        var (app, http, store) = await StartAsync();
        await using var _ = app;

        var res = await http.PostAsync("/voice-test/clip",
            Clip(VoiceTestKind.Microphone, Audio(), quality: "{ not json"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var stored = Assert.Single(store.List());
        Assert.Null(stored.Quality);
    }

    [Fact]
    public async Task AnOverlongPassage_IsTrimmedRatherThanStoredWhole()
    {
        var (app, http, store) = await StartAsync();
        await using var _ = app;

        await http.PostAsync("/voice-test/clip",
            Clip(VoiceTestKind.Microphone, Audio(), expected: new string('x', 20_000)));

        var stored = Assert.Single(store.List());
        Assert.NotNull(stored.ExpectedText);
        Assert.True(stored.ExpectedText!.Length <= 4000);
    }

    [Fact]
    public async Task ListReturnsWhatWasStored_AndDeleteRemovesIt()
    {
        var (app, http, _) = await StartAsync();
        await using var __ = app;

        await http.PostAsync("/voice-test/clip", Clip(VoiceTestKind.Microphone, Audio()));
        await http.PostAsync("/voice-test/clip", Clip(VoiceTestKind.Microphone, Audio()));

        var listed = await http.GetStringAsync("/voice-test/clips");
        using (var doc = JsonDocument.Parse(listed))
        {
            Assert.Equal(2, doc.RootElement.GetProperty("clips").GetArrayLength());
        }

        var deleted = await http.DeleteAsync("/voice-test/clips");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        var afterwards = await http.GetStringAsync("/voice-test/clips");
        using (var doc = JsonDocument.Parse(afterwards))
        {
            Assert.Equal(0, doc.RootElement.GetProperty("clips").GetArrayLength());
        }
    }
}
