using System.Net;
using CcDirectorClient.Voice;
using Xunit;

namespace CcDirectorClient.Tests;

/// <summary>
/// Resilience tests for the Gateway async voice-turn submit + poll loop (issue #405).
/// The pipeline is designed so the phone submits once and the Gateway caches the result for
/// ~10 minutes; these tests prove the client now lives up to that design - a transient drop on
/// submit or poll no longer aborts the turn, while a terminal condition (error stage, expired
/// 404, gone 410, deadline) stops cleanly with a clear message.
///
/// A fake <see cref="IVoiceTurnChannel"/> scripts each submit/poll outcome, and the retry policy's
/// clock and delay are injected so the whole loop runs instantly with no real wall-clock waits.
/// </summary>
public class VoiceTurnRunnerTests
{
    private const string Gw = "http://gw";
    private const string Sid = "session-1";
    private const string Tid = "turn-abc";

    // ===== test seam: a scripted fake channel + a virtual clock =============

    /// <summary>A scripted channel: each call dequeues the next outcome (a result or an
    /// exception to throw). Records how many times submit/poll were called.</summary>
    private sealed class FakeChannel : IVoiceTurnChannel
    {
        private readonly Queue<Func<VoiceTurnSubmitResult>> _submits = new();
        private readonly Queue<Func<VoiceTurnPollResult>> _polls = new();

        // Audio-fetch script (issue #407). The backing clip is served in slices; each scripted
        // step is either "serve up to maxChunk bytes from the requested offset" or "throw" (a
        // simulated mid-download drop). The resume loop re-requests from where it left off.
        private byte[] _audioClip = Array.Empty<byte>();
        private readonly Queue<Func<long, VoiceTurnAudioChunk>> _audioSteps = new();

        public int SubmitCalls { get; private set; }
        public int PollCalls { get; private set; }
        public int AudioCalls { get; private set; }

        public FakeChannel SubmitReturns(VoiceTurnSubmitResult r) { _submits.Enqueue(() => r); return this; }
        public FakeChannel SubmitThrows(Exception ex) { _submits.Enqueue(() => throw ex); return this; }
        public FakeChannel PollReturns(VoiceTurnPollResult r) { _polls.Enqueue(() => r); return this; }
        public FakeChannel PollThrows(Exception ex) { _polls.Enqueue(() => throw ex); return this; }

        /// <summary>Set the full reply audio the dedicated endpoint will serve.</summary>
        public FakeChannel AudioClip(byte[] clip) { _audioClip = clip; return this; }

        /// <summary>Serve a partial slice (206-style): up to <paramref name="maxChunk"/> bytes from
        /// the requested offset, reporting the full clip length so the loop knows the total.</summary>
        public FakeChannel AudioServesChunk(int maxChunk)
        {
            _audioSteps.Enqueue(from =>
            {
                var remaining = (int)(_audioClip.Length - from);
                var take = Math.Min(maxChunk, Math.Max(0, remaining));
                var slice = new byte[take];
                Array.Copy(_audioClip, from, slice, 0, take);
                var isPartial = from > 0 || take < _audioClip.Length;
                return new VoiceTurnAudioChunk(slice, _audioClip.Length, isPartial);
            });
            return this;
        }

        /// <summary>Serve the WHOLE remaining clip from the requested offset (full/last leg).</summary>
        public FakeChannel AudioServesRest() => AudioServesChunk(int.MaxValue);

        /// <summary>A simulated mid-download drop on the next fetch.</summary>
        public FakeChannel AudioThrows(Exception ex) { _audioSteps.Enqueue(_ => throw ex); return this; }

        public Task<VoiceTurnSubmitResult> SubmitVoiceTurnAsync(
            string gatewayBase, string sessionId, byte[] audio, string mime, CancellationToken ct = default)
        {
            SubmitCalls++;
            return Task.FromResult(_submits.Dequeue()());
        }

        public Task<VoiceTurnPollResult> PollVoiceTurnAsync(
            string gatewayBase, string sessionId, string turnId, CancellationToken ct = default)
        {
            PollCalls++;
            // When the script runs dry on a "throws forever" test, keep throwing the last kind
            // so the deadline (not an empty-queue crash) is what stops the loop.
            var next = _polls.Count > 0 ? _polls.Dequeue() : (() => throw new HttpRequestException("still offline"));
            return Task.FromResult(next());
        }

        public Task<VoiceTurnAudioChunk> FetchVoiceTurnAudioAsync(
            string gatewayBase, string sessionId, string turnId, long fromByte, CancellationToken ct = default)
        {
            AudioCalls++;
            // When the script runs dry, serve the rest of the clip from the offset (the healthy
            // path after a scripted drop), so a "drop then resume" test completes deterministically.
            var step = _audioSteps.Count > 0 ? _audioSteps.Dequeue() : DefaultRest();
            return Task.FromResult(step(fromByte));
        }

        private Func<long, VoiceTurnAudioChunk> DefaultRest() => from =>
        {
            var remaining = (int)(_audioClip.Length - from);
            var take = Math.Max(0, remaining);
            var slice = new byte[take];
            Array.Copy(_audioClip, from, slice, 0, take);
            return new VoiceTurnAudioChunk(slice, _audioClip.Length, IsPartial: from > 0);
        };
    }

    /// <summary>A virtual clock that advances by exactly the delay the runner asks to wait, so
    /// the overall deadline is reached deterministically without any real sleeping.</summary>
    private static VoiceTurnRetryPolicy FastPolicy(TimeSpan? deadline = null, int submitAttempts = 4)
    {
        var now = new DateTimeOffset(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
        return new VoiceTurnRetryPolicy
        {
            OverallDeadline = deadline ?? TimeSpan.FromMinutes(10),
            SubmitAttempts = submitAttempts,
            UtcNow = () => now,
            DelayAsync = (d, ct) => { now = now.Add(d); return Task.CompletedTask; },
        };
    }

    private static VoiceTurnPollResult Stage(string stage, string? summary = null, string? error = null, string? audio = null)
        => new(stage, Transcript: null, Summary: summary, AudioBase64: audio, Error: error);

    // ===== POLL: transient drop is tolerated ================================

    [Fact]
    public async Task Poll_ThrowsOnceThenSucceeds_TurnCompletes()
    {
        // A single dropped poll (network error) must NOT end the turn: the loop retries and
        // completes once connectivity returns and the cached reply is available.
        var channel = new FakeChannel()
            .PollThrows(new HttpRequestException("connection reset"))
            .PollReturns(Stage("reply", summary: "All done."));
        var runner = new VoiceTurnRunner(channel, FastPolicy());

        var result = await runner.PollToCompletionAsync(Gw, Sid, Tid);

        Assert.Equal("reply", result.Stage);
        Assert.Equal("All done.", result.Summary);
        Assert.Equal(2, channel.PollCalls); // proves it retried rather than threw out
    }

    [Fact]
    public async Task Poll_TransientServerErrorThenReply_Completes()
    {
        // A 5xx is transient too - re-poll the same cached turn id.
        var channel = new FakeChannel()
            .PollThrows(new VoiceTurnHttpException(HttpStatusCode.BadGateway, "502 upstream"))
            .PollReturns(Stage("reply", summary: "Done."));
        var runner = new VoiceTurnRunner(channel, FastPolicy());

        var result = await runner.PollToCompletionAsync(Gw, Sid, Tid);

        Assert.Equal("reply", result.Stage);
        Assert.Equal(2, channel.PollCalls);
    }

    [Fact]
    public async Task Poll_ThrowsRepeatedlyPastDeadline_FailsCleanly()
    {
        // Poll keeps failing forever; the loop must give up at the deadline with a clear
        // timeout, not loop endlessly or throw the raw network exception.
        var channel = new FakeChannel(); // empty script -> the fake throws HttpRequestException forever
        var runner = new VoiceTurnRunner(channel, FastPolicy(deadline: TimeSpan.FromSeconds(20)));

        await Assert.ThrowsAsync<VoiceTurnTimeoutException>(
            () => runner.PollToCompletionAsync(Gw, Sid, Tid));

        Assert.True(channel.PollCalls > 0); // it actually tried before giving up
    }

    // ===== POLL: terminal conditions stop immediately =======================

    [Fact]
    public async Task Poll_ErrorStage_StopsImmediately()
    {
        var channel = new FakeChannel()
            .PollReturns(Stage("error", error: "director unreachable: timeout"));
        var runner = new VoiceTurnRunner(channel, FastPolicy());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.PollToCompletionAsync(Gw, Sid, Tid));

        Assert.Contains("director unreachable", ex.Message);
        Assert.Equal(1, channel.PollCalls); // did NOT keep polling after the terminal stage
    }

    [Fact]
    public async Task Poll_Expired404_SurfacesPleaseResend_NotUnhandled()
    {
        var channel = new FakeChannel()
            .PollThrows(new VoiceTurnHttpException(HttpStatusCode.NotFound, "404 unknown turn"));
        var runner = new VoiceTurnRunner(channel, FastPolicy());

        var ex = await Assert.ThrowsAsync<VoiceTurnExpiredException>(
            () => runner.PollToCompletionAsync(Gw, Sid, Tid));

        Assert.Contains("send it again", ex.Message);
    }

    [Fact]
    public async Task Poll_SessionGone410_StopsImmediately()
    {
        var channel = new FakeChannel()
            .PollThrows(new VoiceTurnHttpException(HttpStatusCode.Gone, "410 gone"));
        var runner = new VoiceTurnRunner(channel, FastPolicy());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.PollToCompletionAsync(Gw, Sid, Tid));

        Assert.Contains("exited", ex.Message);
    }

    [Fact]
    public async Task Poll_FiresOnStageOncePerDistinctStage()
    {
        var channel = new FakeChannel()
            .PollReturns(Stage("transcribing"))
            .PollReturns(Stage("transcribing")) // duplicate - must NOT fire again
            .PollReturns(Stage("thinking"))
            .PollReturns(Stage("reply", summary: "ok"));
        var runner = new VoiceTurnRunner(channel, FastPolicy());

        var stages = new List<string>();
        await runner.PollToCompletionAsync(Gw, Sid, Tid, onStage: p => stages.Add(p.Stage));

        Assert.Equal(new[] { "transcribing", "thinking", "reply" }, stages);
    }

    [Fact]
    public async Task Poll_Cancelled_ThrowsOperationCanceled()
    {
        var channel = new FakeChannel().PollReturns(Stage("reply", summary: "ok"));
        var runner = new VoiceTurnRunner(channel, FastPolicy());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.PollToCompletionAsync(Gw, Sid, Tid, ct: cts.Token));
    }

    // ===== SUBMIT: transient drop is retried ================================

    [Fact]
    public async Task Submit_ThrowsOnceThenSucceeds_ReturnsTurnId()
    {
        // A drop mid-upload must be retried (bounded) before the turn is surfaced as failed,
        // and the recorded clip is re-sent rather than discarded on the first failure.
        var channel = new FakeChannel()
            .SubmitThrows(new HttpRequestException("upload reset"))
            .SubmitReturns(new VoiceTurnSubmitResult(Tid, ExpiresAt: null));
        var runner = new VoiceTurnRunner(channel, FastPolicy());

        var result = await runner.SubmitAsync(Gw, Sid, new byte[] { 1, 2, 3 }, "audio/mp4");

        Assert.Equal(Tid, result.TurnId);
        Assert.Equal(2, channel.SubmitCalls);
    }

    [Fact]
    public async Task Submit_ThrowsPastBudget_FailsCleanly()
    {
        var channel = new FakeChannel()
            .SubmitThrows(new HttpRequestException("offline"))
            .SubmitThrows(new HttpRequestException("offline"));
        var runner = new VoiceTurnRunner(channel, FastPolicy(submitAttempts: 2));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.SubmitAsync(Gw, Sid, new byte[] { 1 }, "audio/mp4"));

        Assert.Contains("please try again", ex.Message);
        Assert.Equal(2, channel.SubmitCalls); // exactly the budget, no more
    }

    [Fact]
    public async Task Submit_ServerError5xx_IsRetried()
    {
        var channel = new FakeChannel()
            .SubmitThrows(new VoiceTurnHttpException(HttpStatusCode.ServiceUnavailable, "503"))
            .SubmitReturns(new VoiceTurnSubmitResult(Tid, ExpiresAt: null));
        var runner = new VoiceTurnRunner(channel, FastPolicy());

        var result = await runner.SubmitAsync(Gw, Sid, new byte[] { 1 }, "audio/mp4");

        Assert.Equal(Tid, result.TurnId);
        Assert.Equal(2, channel.SubmitCalls);
    }

    // ===== AUDIO FETCH: dedicated, resumable endpoint (issue #407) ===========

    private static byte[] Clip(int n)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[i] = (byte)(i % 251);
        return b;
    }

    [Fact]
    public async Task FetchAudio_FullBodyInOneGo_ReturnsWholeClip()
    {
        // A single 200 response carrying the whole clip: nothing to resume, returned as-is.
        var clip = Clip(64);
        var channel = new FakeChannel().AudioClip(clip).AudioServesRest();
        var runner = new VoiceTurnRunner(channel, FastPolicy());

        var audio = await runner.FetchAudioToCompletionAsync(Gw, Sid, Tid);

        Assert.Equal(clip, audio);
        Assert.Equal(1, channel.AudioCalls);
    }

    [Fact]
    public async Task FetchAudio_MidDownloadDrop_ResumesFromOffset_NotRestart()
    {
        // The acceptance-criteria case: deliver part of the clip, DROP mid-download, then resume.
        // The loop must re-request only the missing tail (Range) and reassemble the WHOLE clip -
        // proving it does not restart from byte 0.
        var clip = Clip(100);
        var channel = new FakeChannel()
            .AudioClip(clip)
            .AudioServesChunk(40)                               // 1st leg: bytes 0..39
            .AudioThrows(new HttpRequestException("connection reset mid-download")) // drop
            .AudioServesChunk(40)                               // resume from 40: bytes 40..79
            .AudioServesRest();                                 // resume from 80: bytes 80..99
        var runner = new VoiceTurnRunner(channel, FastPolicy());

        var audio = await runner.FetchAudioToCompletionAsync(Gw, Sid, Tid);

        Assert.Equal(clip, audio);                              // reassembled correctly, no corruption
        Assert.Equal(4, channel.AudioCalls);                   // 3 successful legs + the dropped one
    }

    [Fact]
    public async Task FetchAudio_TransientServerError_IsRetriedThenCompletes()
    {
        // A 5xx mid-fetch is transient: back off and resume from the same offset.
        var clip = Clip(50);
        var channel = new FakeChannel()
            .AudioClip(clip)
            .AudioServesChunk(30)                               // bytes 0..29
            .AudioThrows(new VoiceTurnHttpException(HttpStatusCode.BadGateway, "502"))
            .AudioServesRest();                                 // resume from 30: bytes 30..49
        var runner = new VoiceTurnRunner(channel, FastPolicy());

        var audio = await runner.FetchAudioToCompletionAsync(Gw, Sid, Tid);

        Assert.Equal(clip, audio);
    }

    [Fact]
    public async Task FetchAudio_Expired404_SurfacesPleaseResend()
    {
        var channel = new FakeChannel()
            .AudioClip(Clip(10))
            .AudioThrows(new VoiceTurnHttpException(HttpStatusCode.NotFound, "404 no audio"));
        var runner = new VoiceTurnRunner(channel, FastPolicy());

        var ex = await Assert.ThrowsAsync<VoiceTurnExpiredException>(
            () => runner.FetchAudioToCompletionAsync(Gw, Sid, Tid));

        Assert.Contains("send it again", ex.Message);
    }

    [Fact]
    public async Task FetchAudio_KeepsDroppingPastDeadline_FailsCleanly()
    {
        // Every fetch fails forever: the loop must give up at the deadline with a clear timeout,
        // not loop endlessly. The empty audio script -> DefaultRest would succeed, so script a
        // run of throws long enough that the (short) deadline trips first.
        var channel = new FakeChannel().AudioClip(Clip(10));
        for (var i = 0; i < 1000; i++)
            channel.AudioThrows(new HttpRequestException("still offline"));
        var runner = new VoiceTurnRunner(channel, FastPolicy(deadline: TimeSpan.FromSeconds(20)));

        await Assert.ThrowsAsync<VoiceTurnTimeoutException>(
            () => runner.FetchAudioToCompletionAsync(Gw, Sid, Tid));

        Assert.True(channel.AudioCalls > 0);
    }

    [Fact]
    public async Task FetchAudio_EmptyClip_ReturnsEmpty_NoSpin()
    {
        // No audio (e.g. no TTS key but audioReady somehow true): total 0, returns empty cleanly.
        var channel = new FakeChannel().AudioClip(Array.Empty<byte>()).AudioServesRest();
        var runner = new VoiceTurnRunner(channel, FastPolicy());

        var audio = await runner.FetchAudioToCompletionAsync(Gw, Sid, Tid);

        Assert.Empty(audio);
    }
}
