using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CcDirector.Core.Audio;
using CcDirector.Core.Configuration;
using CcDirector.Core.Transcription;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Evidence harness for issue #1139 (long-clip transcription reliability). Transcription is the ONLY
/// thing exercised here - cleanup is out of the path (TranscribeUncorrectedAsync). A long recording is split
/// into ~60s chunks and transcribed up to 4 in parallel; the provider is a deterministic stub so we can
/// inject the exact failure mode the #1139 logs show: the managed proxy returns 504 upstream_timeout for
/// a chunk during a transient provider window.
///
/// The load-bearing finding: <see cref="BatchTranscriptionPipeline"/> joins chunks with Task.WhenAll and
/// rethrows the FIRST chunk failure, so ONE transient chunk fails the WHOLE recording even though every
/// other chunk transcribed fine. For a 20-30 minute clip (~20-30 chunks) this all-or-nothing rule makes
/// long clips disproportionately likely to fail entirely: whole-clip failure = 1 - (1 - p)^N in the
/// number of chunks N. These tests pin that behaviour so a reliability fix can be measured against it.
/// </summary>
public sealed class Issue1139LongClipReliabilityTests
{
    private static ResolvedTranscription Routing() => new()
    {
        BaseUrl = "https://devthrottle.example/api/v1",
        ApiKey = "dt_test_key",
        Transport = TranscriptionTransport.Batch,
        Model = "gpt-4o-transcribe",
        Mode = TranscriptionMode.DevThrottle,
    };

    /// <summary>A synthetic PCM WAV of <paramref name="seconds"/> seconds (16 kHz mono 16-bit silence).
    /// The stub does not care about the audio content; the duration drives how many chunks the splitter
    /// produces (~one per 60s).</summary>
    private static byte[] SilenceWav(int seconds)
    {
        const int sampleRate = 16000, channels = 1, bits = 16;
        var pcm = new byte[sampleRate * channels * (bits / 8) * seconds];
        return PcmWav.Wrap(pcm, sampleRate, channels, bits);
    }

    [Fact]
    public async Task HealthyProvider_LongClip_TranscribesEveryChunkAndJoinsInOrder()
    {
        var stub = new ChunkStub(failChunks: new HashSet<int>(), failStatus: 0);
        using var http = new HttpClient(stub);
        using var pipeline = new BatchTranscriptionPipeline(httpClient: http);

        var text = (await pipeline.TranscribeUncorrectedAsync(SilenceWav(360), "dictation.wav", Routing(), default)).Delivered;

        Assert.True(stub.ChunkCount >= 5, $"expected a genuine multi-chunk long clip, got {stub.ChunkCount}");
        // Joined in original order, every chunk present.
        for (int i = 0; i < stub.ChunkCount; i++)
            Assert.Contains($"chunk{i}", text);
    }

    [Fact]
    public async Task OneTransientChunk_FailsEntireLongClip_EvenThoughAllOthersSucceeded()
    {
        // Chunk index 2 returns 504 upstream_timeout on BOTH its attempts (the pipeline retries a
        // transient failure once), exactly like the #1139 outage window.
        var stub = new ChunkStub(failChunks: new HashSet<int> { 2 }, failStatus: 504);
        using var http = new HttpClient(stub);
        using var pipeline = new BatchTranscriptionPipeline(httpClient: http);

        var ex = await Assert.ThrowsAsync<TranscriptionFailedException>(
            () => pipeline.TranscribeUncorrectedAsync(SilenceWav(360), "dictation.wav", Routing(), default));

        // The failure is correctly CLASSIFIED as transient (504) - a retry could plausibly clear it...
        Assert.Equal(504, ex.StatusCode);
        Assert.True(ex.IsTransient);
        // ...yet every OTHER chunk transcribed successfully and was thrown away with the whole job.
        // That wasted work is the all-or-nothing reliability gap #1139 must address for long clips.
        Assert.True(stub.SucceededChunks.Count >= stub.ChunkCount - 1,
            $"expected all-but-one chunk to succeed and be discarded; succeeded={stub.SucceededChunks.Count} of {stub.ChunkCount}");
    }

    [Fact]
    public async Task CircuitBreakerOpen_LongClip_FailsWithClassifiedTransientStatus()
    {
        // Once the proxy breaker trips it fast-fails every chunk with 502 upstream_unavailable.
        var stub = new ChunkStub(failChunks: null, failStatus: 502); // null => fail ALL chunks
        using var http = new HttpClient(stub);
        using var pipeline = new BatchTranscriptionPipeline(httpClient: http);

        var ex = await Assert.ThrowsAsync<TranscriptionFailedException>(
            () => pipeline.TranscribeUncorrectedAsync(SilenceWav(180), "dictation.wav", Routing(), default));
        Assert.Equal(502, ex.StatusCode);
        Assert.True(ex.IsTransient); // classified retryable, but surfaced to the caller as one opaque failure
    }

    [Fact]
    public async Task OverBudgetNonWav_IsTranscodedToWav_ThenSplitAndTranscribed()
    {
        // A >4MB non-WAV clip (the 5.4MB WebM incident) cannot be sent whole or split as-is. The
        // transcoder turns it into a PCM WAV, which the existing splitter then chunks and transcribes.
        var stub = new ChunkStub(failChunks: new HashSet<int>(), failStatus: 0);
        var transcoder = new StubTranscoder(wav: SilenceWav(360)); // stands in for ffmpeg output
        using var http = new HttpClient(stub);
        using var pipeline = new BatchTranscriptionPipeline(httpClient: http, transcoder: transcoder);

        var webm = new byte[5_000_000]; // not a WAV, over the 4MB budget
        var text = (await pipeline.TranscribeUncorrectedAsync(webm, "dictation.webm", Routing(), default)).Delivered;

        Assert.Equal(1, transcoder.Calls);                 // transcode ran exactly once
        Assert.True(stub.ChunkCount >= 5, $"transcoded WAV should split into chunks, got {stub.ChunkCount}");
        Assert.Contains("chunk0", text);
    }

    [Fact]
    public async Task UndecodableClip_FailsPermanent_AndNeverHitsTheProvider()
    {
        // ffmpeg cannot decode the bytes -> a permanent, non-retryable failure. It must surface as such
        // (so the durable loop stops) and must NOT reach the transcription provider at all.
        var stub = new ChunkStub(failChunks: new HashSet<int>(), failStatus: 0);
        var transcoder = new StubTranscoder(
            error: new TranscriptionPermanentException(TranscriptionPermanentException.UnsupportedFormat, "bad codec"));
        using var http = new HttpClient(stub);
        using var pipeline = new BatchTranscriptionPipeline(httpClient: http, transcoder: transcoder);

        var junk = new byte[5_000_000];
        var ex = await Assert.ThrowsAsync<TranscriptionPermanentException>(
            () => pipeline.TranscribeUncorrectedAsync(junk, "dictation.webm", Routing(), default));

        Assert.Equal(TranscriptionPermanentException.UnsupportedFormat, ex.Code);
        Assert.False(ex.IsTransient);
        Assert.Equal(0, stub.ChunkCount); // nothing was sent to the provider
    }

    /// <summary>Stand-in for the ffmpeg transcoder: returns a fixed PCM WAV, or throws a fixed error.</summary>
    private sealed class StubTranscoder : IAudioTranscoder
    {
        private readonly byte[]? _wav;
        private readonly Exception? _error;
        public int Calls;

        public StubTranscoder(byte[]? wav = null, Exception? error = null)
        {
            _wav = wav;
            _error = error;
        }

        public byte[] ToPcmWav(byte[] audio, string fileName, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            if (_error is not null) throw _error;
            return _wav ?? Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Deterministic provider stub: returns a per-chunk transcript on 200, or the configured failure
    /// status for the selected chunk indices (null = fail every chunk). The chunk index is read from the
    /// multipart filename the pipeline sets (dictation.{idx}.wav), so failure injection is independent of
    /// the (parallel, non-deterministic) request order.
    /// </summary>
    private sealed class ChunkStub : HttpMessageHandler
    {
        private static readonly Regex FilePart = new("\\.(\\d+)\\.wav$", RegexOptions.Compiled);
        private readonly HashSet<int>? _failChunks;
        private readonly int _failStatus;
        private readonly ConcurrentDictionary<int, byte> _chunks = new();

        public ConcurrentDictionary<int, byte> SucceededChunks { get; } = new();
        public int ChunkCount => _chunks.Count;

        public ChunkStub(HashSet<int>? failChunks, int failStatus)
        {
            _failChunks = failChunks;
            _failStatus = failStatus;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Read the part filename (dictation.{idx}.wav) from the multipart headers directly - robust
            // regardless of body serialization or parallel/retry ordering. -1 for an un-indexed name.
            var idx = -1;
            if (request.Content is MultipartFormDataContent mp)
            {
                foreach (var part in mp)
                {
                    var fn = part.Headers.ContentDisposition?.FileName?.Trim('"');
                    var m = fn is null ? Match.Empty : FilePart.Match(fn);
                    if (m.Success) { idx = int.Parse(m.Groups[1].Value); break; }
                }
            }
            if (idx >= 0) _chunks.TryAdd(idx, 0);

            var fail = _failChunks is null || _failChunks.Contains(idx);
            if (fail)
            {
                var err = "{\"error\":{\"message\":\"Upstream provider did not respond within 15000 ms (2 attempts).\","
                          + "\"type\":\"api_error\",\"code\":\"upstream_timeout\"}}";
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)_failStatus)
                {
                    Content = new StringContent(err, Encoding.UTF8, "application/json"),
                });
            }

            if (idx >= 0) SucceededChunks.TryAdd(idx, 0);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"text\":\"chunk{idx}\"}}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
