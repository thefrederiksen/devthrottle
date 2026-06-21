using System.Net;
using System.Net.Http;
using System.Text;
using CcDirector.Avalonia.Voice;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Transcription;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Proof tests for the desktop-dictation batch migration (issue #589). They drive the EXACT
/// transcription call <see cref="BatchDictationRecorder"/> makes - a WAV blob produced by
/// <see cref="WavWriter"/> sent through the shared <see cref="BatchTranscriptionPipeline"/> - against
/// a request-counting stub, so the acceptance criteria are proven deterministically without a live
/// microphone:
///
///   * exactly ONE batch transcription request is made for a turn (no per-partial calls),
///   * the only network calls are plain HTTP POSTs to /audio/transcriptions (+ optional
///     /chat/completions for the dictionary) - there is NO realtime/WebSocket transcription, and
///   * a turn with no dictionary term returns byte-identical to the raw transcription.
/// </summary>
public sealed class DesktopDictationBatchProofTests
{
    /// <summary>Records every request the pipeline sends and answers the two batch endpoints.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly string _transcript;

        public List<string> Urls { get; } = new();
        public int TranscriptionPosts { get; private set; }

        public CountingHandler(string transcript) => _transcript = transcript;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri?.ToString() ?? "";
            Urls.Add($"{request.Method} {url}");

            string json;
            if (url.EndsWith("/audio/transcriptions", StringComparison.Ordinal))
            {
                TranscriptionPosts++;
                json = "{\"text\": " + System.Text.Json.JsonSerializer.Serialize(_transcript) + "}";
            }
            else if (url.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                json = "{\"choices\":[{\"message\":{\"content\": \"{\\\"edits\\\": []}\"}}]}";
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

    // A short, well-formed 24kHz/mono/16-bit WAV clip - the exact shape BatchDictationRecorder
    // produces from the whole captured PCM.
    private static byte[] SampleWavClip()
    {
        var pcm = new byte[4800]; // ~100 ms at 24 kHz mono 16-bit
        new Random(99).NextBytes(pcm);
        return WavWriter.WrapPcm16(pcm, 24_000, 1, 16);
    }

    [Fact]
    public async Task WholeClip_TranscribesInExactlyOneBatchCall_NoRealtimeSocket()
    {
        // Arrange - the desktop recorder's whole-clip WAV and a counting transport.
        var handler = new CountingHandler(transcript: "open the pi session and send the prompt");
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        // Act - one whole-audio transcription, exactly as BatchDictationRecorder.TranscribeAsync does.
        var result = await pipeline.TranscribeAsync(
            SampleWavClip(), "dictation.wav", Byo(), DictationDictionary.Empty);

        // Assert - exactly ONE batch transcription POST for the whole turn.
        Assert.Equal(1, handler.TranscriptionPosts);

        // Every network call is a plain HTTP request to an OpenAI-compatible REST endpoint; there is
        // NO ws:// / wss:// realtime socket anywhere in the path.
        Assert.All(handler.Urls, u => Assert.StartsWith("POST http", u));
        Assert.DoesNotContain(handler.Urls, u => u.Contains("ws://") || u.Contains("wss://") || u.Contains("/realtime"));

        Assert.False(string.IsNullOrEmpty(result.RawTranscript));
    }

    [Fact]
    public async Task WholeClip_NoDictionaryTerms_ReturnsByteIdenticalText()
    {
        // Arrange
        const string raw = "just push the change and let me know when it is done";
        var handler = new CountingHandler(transcript: raw);
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        // Act - empty dictionary, so the only possible text change (a dictionary swap) cannot happen.
        var result = await pipeline.TranscribeAsync(
            SampleWavClip(), "dictation.wav", Byo(), DictationDictionary.Empty);

        // Assert - the corrected transcript is byte-identical to the raw transcription.
        Assert.Equal(raw, result.RawTranscript);
        Assert.Equal(result.RawTranscript, result.CorrectedTranscript);
        Assert.False(result.DictionaryApplied);
        Assert.Empty(result.ChangedWords);
        // And it was still exactly one batch transcription call.
        Assert.Equal(1, handler.TranscriptionPosts);
    }
}
