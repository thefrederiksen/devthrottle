using System.Net;
using System.Net.Http;
using System.Text;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Recording;
using CcDirector.Core.Transcription;
using Xunit;

namespace CcDirector.Core.Tests.Recording;

/// <summary>
/// Tests for <see cref="SelectedMethodRecordingTranscriber"/> (issue #591): the phone recorder's
/// production transcriber now routes through the ONE shared batch pipeline using the user-SELECTED
/// transcription method. A stub HTTP handler answers the <c>/audio/transcriptions</c> POST with a
/// canned per-segment transcript and the <c>/chat/completions</c> POST with a canned dictionary-edit
/// document, and records every request so the routing assertions can inspect the exact URL and key
/// used. No real network, no OpenAI key.
/// </summary>
public sealed class SelectedMethodRecordingTranscriberTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<int, string> _transcriptForCall;
        private readonly string _editDocument;
        private int _transcribeCalls;

        public List<(string Url, string? AuthScheme, string? AuthValue)> Requests { get; } = new();

        public RecordingHandler(Func<int, string> transcriptForCall, string editDocument)
        {
            _transcriptForCall = transcriptForCall;
            _editDocument = editDocument;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri?.ToString() ?? "";
            Requests.Add((url, request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter));

            string json;
            if (url.EndsWith("/audio/transcriptions", StringComparison.Ordinal))
            {
                var text = _transcriptForCall(_transcribeCalls++);
                json = "{\"text\": " + System.Text.Json.JsonSerializer.Serialize(text) + "}";
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

    private static SelectedMethodRecordingTranscriber New(
        ResolvedTranscription? routing, DictationDictionary dictionary, HttpClient http)
        => new(
            routingResolver: _ => Task.FromResult(routing),
            dictionaryResolver: _ => Task.FromResult(dictionary),
            cleanupModel: "gpt-4.1-nano",
            httpClient: http);

    // ----- Acceptance criterion 2: the selected method governs the base URL -----

    [Fact]
    public async Task TranscribeChunk_ByoMode_RoutesToOpenAiUrlWithOpenAiKey()
    {
        var handler = new RecordingHandler(_ => "hello", "{\"edits\": []}");
        using var transcriber = New(Byo(), DictationDictionary.Empty, new HttpClient(handler));

        await transcriber.TranscribeChunkAsync(new byte[] { 1, 2, 3 }, "audio/mp4", "0000.m4a");

        var transcribe = handler.Requests.Single(r => r.Url.EndsWith("/audio/transcriptions", StringComparison.Ordinal));
        Assert.StartsWith(TranscriptionEndpointResolver.OpenAiBaseUrl, transcribe.Url);
        Assert.Equal("Bearer", transcribe.AuthScheme);
        Assert.Equal("sk-byo-key", transcribe.AuthValue);
        // The bring-your-own OpenAI key must NEVER reach the devthrottle.com URL.
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("devthrottle.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TranscribeChunk_DevThrottleMode_RoutesToDevThrottleUrl_DifferentBaseUrl()
    {
        // The verifiable acceptance criterion: changing the selected method changes the base URL the
        // recording transcription POSTs to. BYO -> api.openai.com, DevThrottle -> devthrottle.com.
        var byoHandler = new RecordingHandler(_ => "hi", "{\"edits\": []}");
        using (var byo = New(Byo(), DictationDictionary.Empty, new HttpClient(byoHandler)))
            await byo.TranscribeChunkAsync(new byte[] { 1 }, "audio/mp4", "0000.m4a");

        var dtHandler = new RecordingHandler(_ => "hi", "{\"edits\": []}");
        using (var dt = New(DevThrottle(), DictationDictionary.Empty, new HttpClient(dtHandler)))
            await dt.TranscribeChunkAsync(new byte[] { 1 }, "audio/mp4", "0000.m4a");

        var byoUrl = byoHandler.Requests.Single(r => r.Url.EndsWith("/audio/transcriptions", StringComparison.Ordinal)).Url;
        var dtUrl = dtHandler.Requests.Single(r => r.Url.EndsWith("/audio/transcriptions", StringComparison.Ordinal)).Url;

        Assert.StartsWith(TranscriptionEndpointResolver.OpenAiBaseUrl, byoUrl);
        Assert.StartsWith(TranscriptionEndpointResolver.DevThrottleBaseUrl, dtUrl);
        Assert.NotEqual(byoUrl, dtUrl); // the base URL changed with the method
        Assert.Equal("dt_live_devthrottle_key",
            dtHandler.Requests.Single(r => r.Url.EndsWith("/audio/transcriptions", StringComparison.Ordinal)).AuthValue);
        Assert.DoesNotContain(dtHandler.Requests, r => r.Url.Contains("api.openai.com", StringComparison.Ordinal));
    }

    // ----- Acceptance criterion 3: assembled == per-segment raw concat + dictionary edits only -----

    [Fact]
    public async Task PerSegmentBatch_NoDictionaryHit_AssembledEqualsRawConcatByteIdentical()
    {
        // Three segments transcribed in batch; the dictionary is empty so the corrector returns the
        // assembled raw concatenation byte-for-byte. This is the proof that the only text change is a
        // dictionary swap: with no dictionary hit there is no change at all.
        var segs = new[] { "first segment text", "second segment text", "third segment text" };
        var handler = new RecordingHandler(call => segs[call], "{\"edits\": []}");
        using var transcriber = New(Byo(), DictationDictionary.Empty, new HttpClient(handler));

        var raws = new List<string>();
        foreach (var _ in segs)
            raws.Add(await transcriber.TranscribeChunkAsync(new byte[] { 9 }, "audio/mp4", "x.m4a"));

        // Assemble exactly as RecordingIngestService does: per-segment raw text concatenated.
        var assembledRaw = string.Join("\n", raws);
        var outcome = await transcriber.CleanupAsync(assembledRaw);

        Assert.Equal(new[] { "first segment text", "second segment text", "third segment text" }, raws);
        // Byte-identical: no reword, no free-text cleanup - the assembled transcript IS the raw concat.
        Assert.Equal(assembledRaw, outcome.Text);
        Assert.False(outcome.Applied);
        Assert.Empty(outcome.ChangedWords);
    }

    [Fact]
    public async Task Cleanup_WithDictionaryHit_SwapsKnownTermOnly_AgainstSameMethodUrl()
    {
        // The ONLY text change allowed is swapping a known dictionary term. The corrector talks to the
        // SAME resolved base URL as transcription so the key is never crossed.
        var dict = new DictationDictionary(
            new[] { "cc-director" },
            new Dictionary<string, IReadOnlyList<string>> { ["cc-director"] = new[] { "See Director" } },
            new Dictionary<string, DictationProfile> { ["default"] = new("default", CleanupEnabled: true) });
        var edit = "{\"edits\": [{\"find\": \"See Director\", \"replace\": \"cc-director\"}]}";
        var handler = new RecordingHandler(_ => "x", edit);
        using var transcriber = New(Byo(), dict, new HttpClient(handler));

        var outcome = await transcriber.CleanupAsync("push it to See Director when you can");

        Assert.Equal("push it to cc-director when you can", outcome.Text);
        Assert.True(outcome.Applied);
        Assert.Single(outcome.ChangedWords);
        var cleanupCall = handler.Requests.Single(r => r.Url.EndsWith("/chat/completions", StringComparison.Ordinal));
        Assert.StartsWith(TranscriptionEndpointResolver.OpenAiBaseUrl, cleanupCall.Url);
        Assert.Equal("sk-byo-key", cleanupCall.AuthValue);
    }

    // ----- No method resolvable: throws a retryable error, never guesses a URL -----

    [Fact]
    public async Task TranscribeChunk_NoMethodResolvable_Throws_NoNetworkCall()
    {
        var handler = new RecordingHandler(_ => "x", "{\"edits\": []}");
        using var transcriber = New(routing: null, DictationDictionary.Empty, new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transcriber.TranscribeChunkAsync(new byte[] { 1 }, "audio/mp4", "0000.m4a"));
        Assert.Empty(handler.Requests); // never POSTed anywhere - no baked-in URL fallback
    }

    [Fact]
    public async Task TranscribeChunk_EmptyAudio_ReturnsEmpty_NoNetworkCall()
    {
        var handler = new RecordingHandler(_ => "x", "{\"edits\": []}");
        using var transcriber = New(Byo(), DictationDictionary.Empty, new HttpClient(handler));

        var raw = await transcriber.TranscribeChunkAsync(Array.Empty<byte>(), "audio/mp4", "0000.m4a");

        Assert.Equal("", raw);
        Assert.Empty(handler.Requests);
    }
}
