using System.Net;
using System.Net.Http;
using System.Text;
using CcDirector.Core.Dictation;
using Xunit;

#nullable enable

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// Offline tests for <see cref="LivePreviewTranscriber"/> - the live
/// transcript preview that re-transcribes the growing clip while the user is
/// still recording (issue #215). A fake HTTP handler stands in for OpenAI's
/// batch transcription endpoint; timing knobs are shrunk so the loop ticks
/// fast enough for CI.
/// </summary>
public sealed class LivePreviewTranscriberTests
{
    // ===== test doubles =====================================================

    /// <summary>
    /// Records every request body and answers with canned JSON. A responder
    /// callback can override per-request behavior (e.g. fail the first pass).
    /// </summary>
    private sealed class PreviewHandler : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public List<string> Bodies { get; } = new();
        public Func<int, HttpResponseMessage>? Responder { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Bodies) Bodies.Add(body);
            var n = Interlocked.Increment(ref _requests);
            return Responder?.Invoke(n) ?? Ok("preview text");
        }

        public static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"text\":\"" + text + "\"}", Encoding.UTF8, "application/json"),
        };
    }

    private static LivePreviewTranscriber NewTranscriber(PreviewHandler handler) =>
        new(apiKey: "test-key", httpClient: new HttpClient(handler))
        {
            TickInterval = TimeSpan.FromMilliseconds(25),
            MinNewBytes = 4,
        };

    private static async Task<string> WaitForPreviewAsync(LivePreviewTranscriber t)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        t.OnPreview += text => tcs.TrySetResult(text);
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ===== the live loop ====================================================

    [Fact]
    public async Task Loop_TranscribesGrowingClip_AndRaisesPreview()
    {
        var handler = new PreviewHandler();
        await using var t = NewTranscriber(handler);
        var previewTask = WaitForPreviewAsync(t);

        t.Start("vocab prompt here");
        t.Append(new byte[64]);

        var text = await previewTask;
        Assert.Equal("preview text", text);
        Assert.True(handler.Requests >= 1);

        // The multipart upload must carry the model, the vocabulary prompt,
        // and a WAV file (the RIFF magic survives the string read).
        string body;
        lock (handler.Bodies) body = handler.Bodies[0];
        Assert.Contains("gpt-4o-mini-transcribe", body);
        Assert.Contains("vocab prompt here", body);
        Assert.Contains("preview.wav", body);
        Assert.Contains("RIFF", body);
    }

    [Fact]
    public async Task Loop_NoNewAudio_MakesNoFurtherRequests()
    {
        var handler = new PreviewHandler();
        await using var t = NewTranscriber(handler);
        var previewTask = WaitForPreviewAsync(t);

        t.Start("");
        t.Append(new byte[64]);
        await previewTask;                 // first pass done
        var after = handler.Requests;

        // Many ticks pass with zero new audio: the loop must stay quiet.
        await Task.Delay(300);
        Assert.Equal(after, handler.Requests);
    }

    [Fact]
    public async Task Loop_FailedPass_RetriesNextTickWithMoreAudio()
    {
        var handler = new PreviewHandler
        {
            Responder = n => n == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    { Content = new StringContent("boom") }
                : PreviewHandler.Ok("recovered"),
        };
        await using var t = NewTranscriber(handler);

        var previews = new List<string>();
        var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        t.OnPreview += text => { lock (previews) previews.Add(text); got.TrySetResult(text); };

        t.Start("");
        t.Append(new byte[64]);
        // Wait until the failing first pass has happened, then feed more audio
        // so the next tick has something new to transcribe.
        await WaitUntilAsync(() => handler.Requests >= 1);
        t.Append(new byte[64]);

        var text = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("recovered", text);
    }

    [Fact]
    public async Task Cap_ClipBeyondLimit_FreezesPreview()
    {
        var handler = new PreviewHandler();
        await using var t = NewTranscriber(handler);
        t.MaxPreviewBytes = 32;

        t.Start("");
        t.Append(new byte[64]);            // already over the cap
        await Task.Delay(300);

        Assert.Equal(0, handler.Requests);
        Assert.Equal(64, t.ClipBytes);     // the clip itself still accumulates
    }

    [Fact]
    public async Task StopAsync_NoPreviewsAfterStop()
    {
        var handler = new PreviewHandler();
        await using var t = NewTranscriber(handler);
        var previewTask = WaitForPreviewAsync(t);

        t.Start("");
        t.Append(new byte[64]);
        await previewTask;

        await t.StopAsync();
        var requestsAtStop = handler.Requests;
        var firedAfterStop = false;
        t.OnPreview += _ => firedAfterStop = true;

        t.Append(new byte[64]);
        await Task.Delay(200);

        Assert.Equal(requestsAtStop, handler.Requests);
        Assert.False(firedAfterStop);
    }

    [Fact]
    public void StartTwice_Throws()
    {
        var handler = new PreviewHandler();
        var t = NewTranscriber(handler);
        t.Start("");
        Assert.Throws<InvalidOperationException>(() => t.Start(""));
    }

    // ===== WAV wrapping =====================================================

    [Fact]
    public void WrapPcm16InWav_ProducesValidMonoHeader()
    {
        var pcm = new byte[] { 1, 2, 3, 4 };
        var wav = LivePreviewTranscriber.WrapPcm16InWav(pcm, 24000);

        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(36 + pcm.Length, BitConverter.ToInt32(wav, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(16, BitConverter.ToInt32(wav, 16));      // PCM fmt chunk
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));       // PCM format
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));       // mono
        Assert.Equal(24000, BitConverter.ToInt32(wav, 24));   // sample rate
        Assert.Equal(48000, BitConverter.ToInt32(wav, 28));   // byte rate
        Assert.Equal(2, BitConverter.ToInt16(wav, 32));       // block align
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));      // bits/sample
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(pcm.Length, BitConverter.ToInt32(wav, 40));
        Assert.Equal(pcm, wav[44..]);
    }

    // ===== helpers ==========================================================

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met within 5s");
            await Task.Delay(10);
        }
    }
}
