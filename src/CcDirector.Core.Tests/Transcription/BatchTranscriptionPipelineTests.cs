using System.Net;
using System.Net.Http;
using System.Text;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Transcription;
using Xunit;

namespace CcDirector.Core.Tests.Transcription;

/// <summary>
/// Tests for the ONE shared batch transcription pipeline (issue #587). They prove the four
/// behaviors the issue requires without a real network: a stub handler answers the
/// <c>/audio/transcriptions</c> POST with a canned transcript and the <c>/chat/completions</c> POST
/// with a canned dictionary-edit document, and records every request so the routing assertions can
/// inspect the exact URL and key used.
/// </summary>
public sealed class BatchTranscriptionPipelineTests
{
    // ----- A stub that records requests and returns canned answers per endpoint -----

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _transcript;
        private readonly string _editDocument;

        /// <summary>Every request the pipeline sent, in order, with its URL and Authorization header.</summary>
        public List<(string Url, string? AuthScheme, string? AuthValue)> Requests { get; } = new();

        public RecordingHandler(string transcript, string editDocument)
        {
            _transcript = transcript;
            _editDocument = editDocument;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri?.ToString() ?? "";
            Requests.Add((url, request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter));

            string json;
            if (url.EndsWith("/audio/transcriptions", StringComparison.Ordinal))
            {
                json = "{\"text\": " + System.Text.Json.JsonSerializer.Serialize(_transcript) + "}";
            }
            else if (url.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                var content = System.Text.Json.JsonSerializer.Serialize(_editDocument);
                json = "{\"choices\":[{\"message\":{\"content\": " + content + "}}]}";
            }
            else
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static ResolvedTranscription Byo() => new()
    {
        BaseUrl = TranscriptionEndpointResolver.OpenAiBaseUrl,
        ApiKey = "sk-byo-key",
        Transport = TranscriptionTransport.Batch,
        Model = TranscriptionEndpointResolver.OpenAiModel,
        Mode = TranscriptionMode.Byo,
    };

    private static ResolvedTranscription DevThrottle() => new()
    {
        BaseUrl = TranscriptionEndpointResolver.DevThrottleBaseUrl,
        ApiKey = "dt_live_devthrottle_key",
        Transport = TranscriptionTransport.Batch,
        Model = TranscriptionEndpointResolver.DevThrottleModel,
        Mode = TranscriptionMode.DevThrottle,
    };

    private static DictationDictionary DictWith(params string[] vocab) => new(
        vocab,
        new Dictionary<string, IReadOnlyList<string>>(),
        new Dictionary<string, DictationProfile> { ["default"] = new("default", CleanupEnabled: true) });

    private static DictationDictionary DictWithMistranscription(string canonical, params string[] wrongForms) => new(
        new[] { canonical },
        new Dictionary<string, IReadOnlyList<string>> { [canonical] = wrongForms },
        new Dictionary<string, DictationProfile> { ["default"] = new("default", CleanupEnabled: true) });

    // ----- Acceptance criterion: no-dictionary-hit transcript is byte-identical -----

    [Fact]
    public async Task TranscribeAsync_NoDictionaryTerms_ReturnsRawByteIdentical()
    {
        const string raw = "just push the change and let me know when it is done";
        // The dictionary is empty, so CleanupOrchestrator short-circuits before any model call:
        // the corrected text must equal the raw text byte-for-byte and nothing is reported changed.
        var handler = new RecordingHandler(transcript: raw, editDocument: "{\"edits\": []}");
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        var result = await pipeline.TranscribeAsync(
            new byte[] { 1, 2, 3 }, "utterance.webm", Byo(), DictationDictionary.Empty);

        Assert.Equal(raw, result.RawTranscript);
        // Byte-identical to the raw transcription, proving no rewrite happened (Assert.Equal on
        // strings is an ordinal comparison).
        Assert.Equal(result.RawTranscript, result.CorrectedTranscript);
        Assert.False(result.DictionaryApplied);
        Assert.Empty(result.ChangedWords);
    }

    // ----- Acceptance criterion: a dictionary hit is swapped and reported -----

    [Fact]
    public async Task TranscribeAsync_WithDictionaryHit_SwapsTermAndReportsChange()
    {
        const string raw = "push it to See Director when you get a sec";
        // The model proposes one find/replace; the deterministic engine validates it against the
        // raw text and the dictionary, then applies it. "See Director" -> "cc-director".
        var edit = "{\"edits\": [{\"find\": \"See Director\", \"replace\": \"cc-director\"}]}";
        var handler = new RecordingHandler(transcript: raw, editDocument: edit);
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        var result = await pipeline.TranscribeAsync(
            new byte[] { 1, 2, 3 }, "utterance.webm", Byo(),
            DictWithMistranscription("cc-director", "See Director"));

        Assert.Equal(raw, result.RawTranscript);
        Assert.Equal("push it to cc-director when you get a sec", result.CorrectedTranscript);
        Assert.True(result.DictionaryApplied);
        // The change list names exactly the one term that changed - and nothing else.
        Assert.Single(result.ChangedWords);
        Assert.Equal("See Director", result.ChangedWords[0].Find);
        Assert.Equal("cc-director", result.ChangedWords[0].Replace);
    }

    // ----- Acceptance criterion: each method routes to the right URL with the right key -----

    [Fact]
    public async Task TranscribeAsync_ByoMode_RoutesToOpenAiUrlWithOpenAiKey()
    {
        var handler = new RecordingHandler("hello", "{\"edits\": []}");
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        await pipeline.TranscribeAsync(new byte[] { 1 }, "a.webm", Byo(), DictationDictionary.Empty);

        var transcribe = handler.Requests.Single(r => r.Url.EndsWith("/audio/transcriptions", StringComparison.Ordinal));
        Assert.StartsWith(TranscriptionEndpointResolver.OpenAiBaseUrl, transcribe.Url);
        Assert.Equal("Bearer", transcribe.AuthScheme);
        Assert.Equal("sk-byo-key", transcribe.AuthValue);
        // The bring-your-own OpenAI key must NEVER reach the devthrottle.com URL.
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("devthrottle.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TranscribeAsync_DevThrottleMode_RoutesToDevThrottleUrlWithDevThrottleKey()
    {
        var handler = new RecordingHandler("hello", "{\"edits\": []}");
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        await pipeline.TranscribeAsync(new byte[] { 1 }, "a.webm", DevThrottle(), DictationDictionary.Empty);

        var transcribe = handler.Requests.Single(r => r.Url.EndsWith("/audio/transcriptions", StringComparison.Ordinal));
        Assert.StartsWith(TranscriptionEndpointResolver.DevThrottleBaseUrl, transcribe.Url);
        Assert.Equal("Bearer", transcribe.AuthScheme);
        Assert.Equal("dt_live_devthrottle_key", transcribe.AuthValue);
        // The DevThrottle key must NEVER reach api.openai.com.
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("api.openai.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TranscribeAsync_SendsResolvedModel_NotAHardcodedWhisper1()
    {
        // The model sent to the provider is the one the routing resolver chose, never a baked-in
        // "whisper-1". DevThrottle resolves to whisper-large-v3.
        string? body = null;
        var handler = new BodyCapturingHandler("hi", b => body = b);
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        await pipeline.TranscribeAsync(new byte[] { 1 }, "a.webm", DevThrottle(), DictationDictionary.Empty);

        Assert.NotNull(body);
        // The multipart body carries the "model" form field with the resolved model and never the
        // legacy hardcoded "whisper-1".
        Assert.Contains(TranscriptionEndpointResolver.DevThrottleModel, body);
        Assert.DoesNotContain("whisper-1", body);
    }

    // ----- Empty audio is refused (the gate must have run upstream) -----

    [Fact]
    public async Task TranscribeAsync_EmptyAudio_Throws()
    {
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(new RecordingHandler("x", "{\"edits\": []}")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            pipeline.TranscribeAsync(Array.Empty<byte>(), "a.webm", Byo(), DictationDictionary.Empty));
    }

    private sealed class BodyCapturingHandler : HttpMessageHandler
    {
        private readonly string _transcript;
        private readonly Action<string?> _onBody;

        public BodyCapturingHandler(string transcript, Action<string?> onBody)
        {
            _transcript = transcript;
            _onBody = onBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri?.ToString() ?? "";
            if (url.EndsWith("/audio/transcriptions", StringComparison.Ordinal))
            {
                _onBody(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"text\": \"" + _transcript + "\"}", Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"edits\\\": []}\"}}]}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
