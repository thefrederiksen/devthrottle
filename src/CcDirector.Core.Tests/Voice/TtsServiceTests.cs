using System.Diagnostics;
using System.Net;
using CcDirector.Core.Configuration;
using CcDirector.Core.Voice;
using Xunit;

namespace CcDirector.Core.Tests.Voice;

/// <summary>
/// Resilience coverage for <see cref="TtsService"/> (issue #389).
///
/// The bug: a single stalled OpenAI /v1/audio/speech request blocked the whole
/// voice turn on the shared 180 s HttpClient.Timeout, then returned empty audio.
/// These tests drive TtsService through an injected <see cref="HttpMessageHandler"/>
/// (no network) and prove the new per-request timeout, retry-once, fast permanent
/// failure, and a byte-identical success path.
///
/// To keep the suite fast the tests do NOT wait the real 30 s per-request timeout;
/// the "stall" double cancels itself as soon as the per-request token fires, which
/// is exactly the observable behaviour the production CancellationTokenSource +
/// CancelAfter produces - the chunk fails on cancellation, not on a wall clock.
/// </summary>
public sealed class TtsServiceTests
{
    private static readonly byte[] Mp3A = { 1, 2, 3, 4 };
    private static readonly byte[] Mp3B = { 9, 8, 7 };

    private static AgentOptions OptionsWithKey() =>
        new() { OpenAiKey = "sk-test-key" };

    // ===== test double =====================================================

    /// <summary>
    /// Programmable OpenAI stand-in. Each call pops the next scripted behaviour
    /// from <see cref="Behaviours"/>; if the queue is exhausted it returns the
    /// last one. Records how many times it was invoked.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public enum Kind { Ok, ServerError, ClientError, Empty, Stall }

        private readonly Queue<Kind> _behaviours;
        private Kind _last;
        public int CallCount { get; private set; }
        public byte[] OkBytes { get; init; } = Mp3A;

        public ScriptedHandler(params Kind[] behaviours)
        {
            if (behaviours.Length == 0)
                throw new ArgumentException("At least one behaviour is required", nameof(behaviours));
            _behaviours = new Queue<Kind>(behaviours);
            _last = behaviours[^1];
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            var kind = _behaviours.Count > 0 ? _behaviours.Dequeue() : _last;

            switch (kind)
            {
                case Kind.Ok:
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(OkBytes),
                    };
                case Kind.ServerError:
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("upstream stalled"),
                    };
                case Kind.ClientError:
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("bad voice"),
                    };
                case Kind.Empty:
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(Array.Empty<byte>()),
                    };
                case Kind.Stall:
                    // Model a hung request: never complete on our own, just wait
                    // for the per-request cancellation token to fire (which the
                    // production CancelAfter triggers). This is what the 180 s
                    // hang looked like, minus the 180 s wall time.
                    await Task.Delay(Timeout.Infinite, ct);
                    throw new InvalidOperationException("unreachable: Task.Delay(Infinite) always throws on cancel");
                default:
                    throw new InvalidOperationException($"Unhandled behaviour {kind}");
            }
        }
    }

    // ===== (a) per-request timeout triggers and retries ====================

    [Fact]
    public async Task GenerateAsync_FirstChunkStalls_PerRequestTimeoutFires_ThenRetries()
    {
        // First attempt stalls (per-request timeout fires); the retry succeeds.
        // Proves the per-request timeout is what ends the stalled call AND that a
        // timeout is treated as transient (retried), not propagated.
        var handler = new ScriptedHandler(ScriptedHandler.Kind.Stall, ScriptedHandler.Kind.Ok);
        var svc = new TtsService(OptionsWithKey(), handler);

        var result = await svc.GenerateAsync("Hello world.", null, null);

        Assert.True(result.Success);
        Assert.Equal(2, handler.CallCount); // one stalled attempt + one retry
        Assert.NotNull(result.AudioBytes);
        Assert.Equal(Mp3A, result.AudioBytes);
    }

    // ===== (b) retry succeeds on second attempt ============================

    [Fact]
    public async Task GenerateAsync_TransientServerError_RetriesOnce_AndSucceeds()
    {
        // A 5xx on the first attempt is transient; the second attempt returns audio.
        var handler = new ScriptedHandler(ScriptedHandler.Kind.ServerError, ScriptedHandler.Kind.Ok);
        var svc = new TtsService(OptionsWithKey(), handler);

        var result = await svc.GenerateAsync("Hello world.", null, null);

        Assert.True(result.Success);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(Mp3A, result.AudioBytes);
    }

    [Fact]
    public async Task GenerateAsync_EmptyBody_IsTransient_AndRetried()
    {
        // An empty body (the exact "audio_bytes=0" symptom) is transient and retried.
        var handler = new ScriptedHandler(ScriptedHandler.Kind.Empty, ScriptedHandler.Kind.Ok);
        var svc = new TtsService(OptionsWithKey(), handler);

        var result = await svc.GenerateAsync("Hello world.", null, null);

        Assert.True(result.Success);
        Assert.Equal(2, handler.CallCount);
    }

    // ===== (c) permanent failure returns quickly (NOT after 180 s) =========

    [Fact]
    public async Task GenerateAsync_PermanentFailure_ReturnsErrorQuickly_NotAfter180s()
    {
        // Both attempts 5xx -> permanent failure after exactly one retry. The key
        // assertion: it returns fast (well under the old 180 s), proving no chunk
        // can block the turn.
        var handler = new ScriptedHandler(ScriptedHandler.Kind.ServerError, ScriptedHandler.Kind.ServerError);
        var svc = new TtsService(OptionsWithKey(), handler);

        var sw = Stopwatch.StartNew();
        var result = await svc.GenerateAsync("Hello world.", null, null);
        sw.Stop();

        Assert.False(result.Success);
        Assert.Equal(2, handler.CallCount); // attempt + single retry, then give up
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"Took {sw.Elapsed.TotalSeconds:0.0}s - must fail fast, not block for 180s");
    }

    [Fact]
    public async Task GenerateAsync_ClientError_IsPermanent_NotRetried()
    {
        // A 4xx is a permanent request error - it must NOT be retried (would waste
        // a call and delay the text-only fallback).
        var handler = new ScriptedHandler(ScriptedHandler.Kind.ClientError);
        var svc = new TtsService(OptionsWithKey(), handler);

        var result = await svc.GenerateAsync("Hello world.", null, null);

        Assert.False(result.Success);
        Assert.Equal(1, handler.CallCount); // no retry on a 4xx
    }

    [Fact]
    public async Task GenerateAsync_PersistentStall_FailsFast_WithinBudget()
    {
        // Every attempt stalls. The per-request timeout ends each attempt, the
        // single retry also stalls, and the call returns an error - it never
        // waits the old 180 s ceiling.
        var handler = new ScriptedHandler(ScriptedHandler.Kind.Stall);
        var svc = new TtsService(OptionsWithKey(), handler);

        var sw = Stopwatch.StartNew();
        var result = await svc.GenerateAsync("Hello world.", null, null);
        sw.Stop();

        Assert.False(result.Success);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(70), $"Took {sw.Elapsed.TotalSeconds:0.0}s - must stay within the overall budget, not block for 180s");
    }

    // ===== (d) happy path unchanged (byte-identical concatenation) =========

    [Fact]
    public async Task GenerateAsync_SingleChunk_ReturnsBytesUnchanged()
    {
        var handler = new ScriptedHandler(ScriptedHandler.Kind.Ok) { OkBytes = Mp3A };
        var svc = new TtsService(OptionsWithKey(), handler);

        var result = await svc.GenerateAsync("Short reply.", null, null);

        Assert.True(result.Success);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(Mp3A, result.AudioBytes); // byte-identical, single chunk
        Assert.Equal("audio/mpeg", result.ContentType);
    }

    [Fact]
    public async Task GenerateAsync_MultiChunk_ConcatenatesBytesInOrder()
    {
        // Build text guaranteed to split into multiple chunks, then prove the
        // returned bytes are the per-chunk MP3s concatenated in order. Every
        // chunk gets the same Ok bytes from the handler, so N chunks -> N copies.
        var text = BuildMultiChunkText(out int expectedChunks);
        Assert.True(expectedChunks >= 2, "test text must split into at least 2 chunks");

        var handler = new ScriptedHandler(ScriptedHandler.Kind.Ok) { OkBytes = Mp3B };
        var svc = new TtsService(OptionsWithKey(), handler);

        var result = await svc.GenerateAsync(text, null, null);

        Assert.True(result.Success);
        Assert.Equal(expectedChunks, handler.CallCount);
        Assert.NotNull(result.AudioBytes);

        // Expected = Mp3B repeated once per chunk, in order.
        var expected = new byte[Mp3B.Length * expectedChunks];
        for (int i = 0; i < expectedChunks; i++)
            Buffer.BlockCopy(Mp3B, 0, expected, i * Mp3B.Length, Mp3B.Length);
        Assert.Equal(expected, result.AudioBytes);
    }

    /// <summary>
    /// Build a body that splits into &gt;= 2 chunks at the real chunk size, and
    /// report how many chunks the splitter produces so the test can assert the
    /// exact concatenation.
    /// </summary>
    private static string BuildMultiChunkText(out int chunkCount)
    {
        var sentence = new string('a', 300) + ". ";
        var text = string.Concat(Enumerable.Repeat(sentence, 8)); // ~2400 chars
        chunkCount = TtsService.SplitIntoChunks(text, TtsService.MaxChunkChars).Count;
        return text;
    }

    // ===== guards ==========================================================

    [Fact]
    public async Task GenerateAsync_EmptyText_ReturnsError_WithoutCallingOpenAi()
    {
        var handler = new ScriptedHandler(ScriptedHandler.Kind.Ok);
        var svc = new TtsService(OptionsWithKey(), handler);

        var result = await svc.GenerateAsync("", null, null);

        Assert.False(result.Success);
        Assert.Equal("empty_text", result.Status);
        Assert.Equal(0, handler.CallCount);
    }
}
