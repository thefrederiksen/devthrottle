using CcDirector.Avalonia.Voice;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Regression tests for the desktop-dictation tail-clipping bug: the Speak dialog
/// was "not getting the end of our speech sometimes". The cause was in
/// <see cref="BatchDictationRecorder.TranscribeAsync"/>, which detached the audio
/// chunk handler and snapshotted the buffer BEFORE the microphone had delivered its
/// final buffered audio - so the last words the user spoke (still sitting in the
/// driver's buffers at the moment of the stop) were dropped.
///
/// The fix stops via <see cref="IAudioSource.StopAsync"/> and only snapshots once the
/// source has drained its tail. These tests drive that ordering through the injected
/// <see cref="IAudioSource"/> seam with a fake source that delivers a tail chunk
/// DURING the drain, and assert the tail survives all the way to transcription -
/// with no real microphone and no network.
/// </summary>
public sealed class BatchDictationRecorderTailTests
{
    /// <summary>
    /// Fake microphone. Emits "recording" chunks on demand via <see cref="Emit"/>, and
    /// models the real NAudio driver by delivering a final buffered "tail" chunk during
    /// <see cref="StopAsync"/> - i.e. AFTER the stop is requested but BEFORE it completes.
    /// A recorder that detaches the handler before draining (the old bug) would lose it.
    /// </summary>
    private sealed class FakeMic : IAudioSource
    {
        private readonly byte[] _tail;

        public FakeMic(byte[] tail) => _tail = tail;

        public event Action<byte[]>? OnAudioChunk;
        public string Description => "Fake Test Microphone";
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }

        /// <summary>Whether the recorder's chunk handler was still attached when the tail was delivered.</summary>
        public bool HandlerAttachedDuringDrain { get; private set; }

        public void Start() => Started = true;
        public void Stop() => Stopped = true;

        /// <summary>Simulate the driver delivering a captured buffer while recording.</summary>
        public void Emit(byte[] chunk) => OnAudioChunk?.Invoke(chunk);

        public async Task StopAsync(TimeSpan drainTimeout)
        {
            // A real driver keeps delivering for a moment after the stop is requested.
            // The await makes this a genuine drain window: only a caller that actually
            // waits on StopAsync will still have the handler attached when the tail lands.
            await Task.Delay(20);
            HandlerAttachedDuringDrain = OnAudioChunk is not null;
            if (_tail.Length > 0)
                OnAudioChunk?.Invoke(_tail);
            Stopped = true;
        }
    }

    [Fact]
    public async Task TranscribeAsync_IncludesTailDeliveredDuringStopDrain()
    {
        // Arrange - a fake mic that delivers a distinct tail chunk during the drain,
        // and a transcription stub that records exactly which PCM the recorder snapshotted.
        var body = new byte[] { 1, 2, 3, 4 };
        var tail = new byte[] { 5, 6, 7, 8 };
        var fake = new FakeMic(tail);
        byte[]? transcribedPcm = null;

        await using var recorder = new BatchDictationRecorder(
            new AgentOptions(),
            _ => fake,
            (pcm, _, _) =>
            {
                transcribedPcm = pcm;
                return Task.FromResult(new DictationResult("raw text", "clean text", 0));
            });

        await recorder.StartAsync();
        fake.Emit(body);   // words captured while recording

        // Act - Send/Insert finalize: stop, drain the tail, snapshot, transcribe.
        var result = await recorder.TranscribeAsync();

        // Assert - the recorder kept listening through the drain, so the tail was
        // appended after the body and the whole clip reached transcription in order.
        Assert.True(fake.HandlerAttachedDuringDrain,
            "the chunk handler must stay attached until StopAsync's drain completes");
        Assert.NotNull(transcribedPcm);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, transcribedPcm);
        Assert.Equal("clean text", result.CleanedTranscript);
        Assert.True(fake.Stopped);
    }

    [Fact]
    public async Task TranscribeAsync_NoAudioCaptured_StillEnforcesCompletenessGate()
    {
        // The completeness gate (issue #586) must still fire when nothing was captured:
        // an interrupted turn produces no transcript and never reaches the transcriber.
        var fake = new FakeMic(Array.Empty<byte>());
        var transcribeCalled = false;

        await using var recorder = new BatchDictationRecorder(
            new AgentOptions(),
            _ => fake,
            (_, _, _) =>
            {
                transcribeCalled = true;
                return Task.FromResult(new DictationResult("", "", 0));
            });

        await recorder.StartAsync();
        // No Emit, and the drain delivers no tail: zero bytes captured.

        await Assert.ThrowsAsync<NoAudioCapturedException>(() => recorder.TranscribeAsync());
        Assert.False(transcribeCalled);
    }
}
