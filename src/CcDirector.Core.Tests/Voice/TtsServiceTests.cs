using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Time.Testing;
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
/// The deadlines run on the service's injected clock, and the clock here is a
/// <see cref="FakeTimeProvider"/> the stall double moves: a stalled call advances the
/// clock past the per-request timeout and then waits on its token, which the
/// deadline timer has by then cancelled. So the thirty-second and sixty-second
/// waits are decided, not sat out - until 10 October 2026 the two stall tests
/// took 60 s and 30 s of real time, waiting out the production constants.
/// </summary>
public sealed class TtsServiceTests
{
    private static readonly byte[] Mp3A = { 1, 2, 3, 4 };
    private static readonly byte[] Mp3B = { 9, 8, 7 };

    private static AgentOptions OptionsWithKey() =>
        new() { OpenAiKey = "sk-test-key" };

    private static TtsService Service(ScriptedHandler handler) =>
        new(OptionsWithKey(), handler, clock: handler.Clock);

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

        /// <summary>The clock the service under test runs its deadlines on; a stall moves it.</summary>
        public FakeTimeProvider Clock { get; } = new();

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
                    // Model a hung request: never complete on our own. The per-request deadline
                    // runs on the service's clock, so move that clock past it - the deadline timer
                    // cancels the token at once - and then wait on the token exactly as a hung
                    // call would. Nothing here knows how the service enforces the deadline.
                    Clock.Advance(TtsService.PerRequestTimeout + TimeSpan.FromSeconds(1));
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
        var svc = Service(handler);

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
        var svc = Service(handler);

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
        var svc = Service(handler);

        var result = await svc.GenerateAsync("Hello world.", null, null);

        Assert.True(result.Success);
        Assert.Equal(2, handler.CallCount);
    }

    // ===== (c) permanent failure returns quickly (NOT after 180 s) =========

    [Fact]
    public async Task GenerateAsync_PermanentFailure_ReturnsErrorQuickly_NotAfter180s()
    {
        // Both attempts 5xx -> permanent failure after exactly one retry, and no deadline is
        // involved: a 5xx answers at once, so the only facts here are the outcome and that the
        // retry rule stopped after one retry.
        var handler = new ScriptedHandler(ScriptedHandler.Kind.ServerError, ScriptedHandler.Kind.ServerError);
        var svc = Service(handler);

        var result = await svc.GenerateAsync("Hello world.", null, null);

        Assert.False(result.Success);
        Assert.Equal(2, handler.CallCount); // attempt + single retry, then give up
    }

    [Fact]
    public async Task GenerateAsync_ClientError_IsPermanent_NotRetried()
    {
        // A 4xx is a permanent request error - it must NOT be retried (would waste
        // a call and delay the text-only fallback).
        var handler = new ScriptedHandler(ScriptedHandler.Kind.ClientError);
        var svc = Service(handler);

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
        var svc = Service(handler);

        var sw = Stopwatch.StartNew();
        var result = await svc.GenerateAsync("Hello world.", null, null);
        sw.Stop();

        Assert.False(result.Success);
        Assert.Equal(2, handler.CallCount); // the stalled attempt and its one retry
        // The first stall is ended by its per-request deadline at 31 s on the clock; the retry's stall moves the clock
        // to 62 s, past the 60 s overall budget, so the budget deadline fires before the retry's own and the whole
        // reply fails on the budget. That is the path pinned here, on purpose.
        Assert.Equal("tts_budget_exceeded", result.Status);
        // The deadlines ran on the test's clock, which the stall moved; had they run on the system clock this call
        // would have sat out the real thirty seconds twice. Ten seconds is daylight for a starved machine, not a
        // timing the test depends on.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"took {sw.Elapsed.TotalSeconds:0.0}s of real time - the deadlines did not run on the service's clock");
    }

    // ===== (d) happy path unchanged (byte-identical concatenation) =========

    [Fact]
    public async Task GenerateAsync_SingleChunk_ReturnsBytesUnchanged()
    {
        var handler = new ScriptedHandler(ScriptedHandler.Kind.Ok) { OkBytes = Mp3A };
        var svc = Service(handler);

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
        var svc = Service(handler);

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
        var svc = Service(handler);

        var result = await svc.GenerateAsync("", null, null);

        Assert.False(result.Success);
        Assert.Equal("empty_text", result.Status);
        Assert.Equal(0, handler.CallCount);
    }
}
