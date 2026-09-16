using CcDirector.Avalonia.Voice;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Issue #2927: capture stopped the instant Send (or Insert, or Pause) was pressed, so the end of a word
/// said on the click - not yet in the capture buffer - was clipped. The recorder now keeps capturing for
/// <see cref="BatchDictationRecorder.StopTailMs"/> before it asks the microphone to stop.
///
/// The fake microphone delivers a chunk shortly AFTER the stop was requested and records whether the
/// recorder had already asked it to stop by then. Without the tail, the stop request comes first and the
/// late audio is not in the clip.
/// </summary>
public sealed class BatchDictationRecorderStopTailTests
{
    private sealed class FakeMic : IAudioSource
    {
        public event Action<byte[]>? OnAudioChunk;
        public string Description => "Fake Test Microphone";
        public volatile bool StopRequested;
        public void Start() { }
        public void Stop() { }
        public Task StopAsync(TimeSpan drainTimeout)
        {
            StopRequested = true;
            return Task.CompletedTask;
        }

        /// <summary>The driver delivering audio - but only while the microphone has not been told to stop.</summary>
        public void Deliver(byte[] chunk)
        {
            if (!StopRequested) OnAudioChunk?.Invoke(chunk);
        }
    }

    private static readonly byte[] Words = { 1, 2, 3, 4 };
    private static readonly byte[] LastWordEnd = { 9, 9, 9, 9 };

    [Fact]
    public async Task TranscribeAsync_AudioDeliveredInTheTailAfterSend_IsInTheTranscribedClip()
    {
        var mic = new FakeMic();
        byte[]? transcribed = null;
        await using var recorder = new BatchDictationRecorder(new AgentOptions(), _ => mic, (pcm, _, _) =>
        {
            transcribed = pcm;
            return Task.FromResult(new DictationResult("raw", "clean", 0));
        });
        await recorder.StartAsync();
        mic.Deliver(Words);

        // Send: the stop begins, and the end of the last word arrives 60 ms later.
        var send = recorder.TranscribeAsync();
        await Task.Delay(60);
        mic.Deliver(LastWordEnd);
        await send;

        Assert.Equal(new byte[] { 1, 2, 3, 4, 9, 9, 9, 9 }, transcribed);
    }

    [Fact]
    public async Task StopAndGetWavAsync_AudioDeliveredInTheTailAfterSend_IsInTheSavedClip()
    {
        // The background Send (the common desktop path) saves this WAV and transcribes it.
        var mic = new FakeMic();
        await using var recorder = new BatchDictationRecorder(new AgentOptions(), _ => mic,
            (_, _, _) => throw new InvalidOperationException("StopAndGetWavAsync must not transcribe"));
        await recorder.StartAsync();
        mic.Deliver(Words);

        var send = recorder.StopAndGetWavAsync();
        await Task.Delay(60);
        mic.Deliver(LastWordEnd);
        var captured = await send;

        Assert.Equal(new byte[] { 1, 2, 3, 4, 9, 9, 9, 9 }, captured.Wav.AsSpan(44, 8).ToArray());
    }
}
