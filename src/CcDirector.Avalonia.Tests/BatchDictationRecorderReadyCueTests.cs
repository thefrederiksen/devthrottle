using CcDirector.Avalonia.Voice;
using CcDirector.Core.Audio;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Issue #2925: the desktop ready cue is played while the microphone is open, the microphone records it,
/// and the speech model writes it down as an invented opening word ("Yeah.", "you", "Bye."). The recorder
/// blanks the cue out of the audio sent for transcription.
///
/// Mission phase 6, ruling K1: the cue is FOUND in the captured audio - normalised cross-correlation of the
/// exact synthesised cue over the start of the clip, threshold 0.5 - and only the found span (match start
/// minus 10 ms to match end plus 50 ms) is zeroed. Five inspections showed that placing it by clocks cannot be
/// made right, so these tests use real waveforms, not byte markers, and no clock positions anything. The
/// injected clock only runs the cue's wait allowance.
///
/// Mission phase 7, ruling L1: the search runs only after playback REPORTED SUCCESSFUL COMPLETION on the
/// recorder. A partial cue still scores over the threshold, and a failed or unstarted playback proves nothing
/// sounded, so an error, no start, or no report blanks nothing.
///
/// Driven through the fake-microphone seam; every case runs on both the TranscribeAsync path and the
/// background Send's StopAndGetWavAsync path, which must carry the same blanking.
/// </summary>
public sealed class BatchDictationRecorderReadyCueTests
{
    private sealed class FakeMic : IAudioSource
    {
        public event Action<byte[]>? OnAudioChunk;
        public string Description => "Fake Test Microphone";
        public Action? OnStopDrain { get; set; }
        public void Start() { }
        public void Stop() { }
        public void Emit(byte[] chunk) => OnAudioChunk?.Invoke(chunk);
        public Task StopAsync(TimeSpan drainTimeout)
        {
            OnStopDrain?.Invoke();
            return Task.CompletedTask;
        }
    }

    /// <summary>A monotonic clock in whole milliseconds that only moves when told to.</summary>
    private sealed class FakeClock : TimeProvider
    {
        private long _ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _ms);
        public void AdvanceMs(long ms) => Interlocked.Add(ref _ms, ms);
    }

    private const int Rate = MicAudioCapture.SampleRate;
    private const int SamplesPerMs = Rate / 1000;
    private const int BytesPerMs = SamplesPerMs * (MicAudioCapture.BitsPerSample / 8) * MicAudioCapture.Channels;

    // MicAudioCapture delivers 50 ms buffers.
    private const int BufferMs = 50;

    // What ruling K1 blanks around a found cue, written out here rather than read from ReadyCue, so a change to
    // the product's numbers turns these tests red instead of moving the expectation with it.
    private const int CueMs = 200, LeadInMs = 10, RingDownMs = 50, SearchAfterFirstAudioMs = 1500;
    private static int BlankStartMs(int cueAtMs) => Math.Max(0, cueAtMs - LeadInMs);
    private static int BlankEndMs(int cueAtMs) => cueAtMs + CueMs + RingDownMs;

    private static byte[] WithoutMs(byte[] pcm, int fromMs, int toMs)
        => pcm.Take(fromMs * BytesPerMs).Concat(pcm.Skip(toMs * BytesPerMs)).ToArray();

    private static byte[] Blanked(byte[] pcm, int fromMs, int toMs)
    {
        var copy = (byte[])pcm.Clone();
        Array.Clear(copy, fromMs * BytesPerMs, Math.Min(toMs * BytesPerMs, copy.Length) - fromMs * BytesPerMs);
        return copy;
    }

    private static (BatchDictationRecorder Recorder, FakeMic Mic, FakeClock Clock, Func<byte[]?> Transcribed) NewRecorder()
    {
        var mic = new FakeMic();
        var clock = new FakeClock();
        byte[]? transcribed = null;
        var recorder = new BatchDictationRecorder(new AgentOptions(), _ => mic, (pcm, _, _) =>
        {
            transcribed = pcm;
            return Task.FromResult(new DictationResult("raw", "clean", 0));
        }, timeProvider: clock);
        return (recorder, mic, clock, () => transcribed);
    }

    /// <summary>Emit <paramref name="pcm"/> from <paramref name="fromMs"/> in 50 ms buffers; the dialog plays the
    /// cue on the first buffer when <paramref name="cuePlayed"/>.</summary>
    private static void Emit(BatchDictationRecorder recorder, FakeMic mic, byte[] pcm, bool cuePlayed, int fromMs = 0, int toMs = -1)
    {
        int end = toMs < 0 ? pcm.Length : Math.Min(pcm.Length, toMs * BytesPerMs);
        for (int offset = fromMs * BytesPerMs; offset < end; offset += BufferMs * BytesPerMs)
        {
            mic.Emit(pcm.AsSpan(offset, Math.Min(BufferMs * BytesPerMs, end - offset)).ToArray());
            if (offset == 0 && cuePlayed) recorder.NoteReadyCuePlaying();
        }
    }

    private static async Task<byte[]> Snapshot(BatchDictationRecorder recorder, Func<byte[]?> transcribed, bool transcribePath, int pcmBytes)
    {
        if (transcribePath)
        {
            await recorder.TranscribeAsync();
            return transcribed()!;
        }
        const int header = 44;
        return (await recorder.StopAndGetWavAsync()).Wav.AsSpan(header, pcmBytes).ToArray();
    }

    private static void AssertPcm(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);                   // nothing trimmed
        for (int i = 0; i < expected.Length; i++)
            if (expected[i] != actual[i])
                Assert.Fail($"byte {i} ({i / BytesPerMs} ms): expected {expected[i]:X2}, got {actual[i]:X2}"
                    + (expected[i] == 0 ? " - this is inside the cue span and must be silence" : " - this is not the cue and must be untouched"));
    }

    private static async Task<byte[]> Record(bool transcribePath, byte[] pcm, bool cuePlayed, bool reportEnd = true)
    {
        var (recorder, mic, _, transcribed) = NewRecorder();
        await using var _ = recorder;
        await recorder.StartAsync();
        Emit(recorder, mic, pcm, cuePlayed);
        if (reportEnd) recorder.NoteReadyCueFinished();
        return await Snapshot(recorder, transcribed, transcribePath, pcm.Length);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 50)]
    [InlineData(true, 100)]
    [InlineData(true, 150)]
    [InlineData(true, 1300)]
    [InlineData(false, 0)]
    [InlineData(false, 50)]
    [InlineData(false, 100)]
    [InlineData(false, 150)]
    [InlineData(false, 1300)]
    public async Task CueInTheAudio_BlanksExactlyTheCueAndRingDown_WordsRightAfterUntouched(bool transcribePath, int cueAtMs)
    {
        // The owner starts talking the instant the ring-down ends. Everything before the lead-in and every word
        // must reach transcription exactly as captured. 1300 ms is a cue delayed by a slow output device, still
        // wholly inside the search, which ends 1.5 s after the first 50 ms buffer.
        var pcm = new ReadyCueTestClip(BlankEndMs(cueAtMs) + 600).Cue(cueAtMs).Words(BlankEndMs(cueAtMs)).Pcm();

        var actual = await Record(transcribePath, pcm, cuePlayed: true);

        AssertPcm(Blanked(pcm, BlankStartMs(cueAtMs), BlankEndMs(cueAtMs)), actual);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FaintCueInNoisyRoom_ScoringJustOverTheThreshold_IsBlanked(bool transcribePath)
    {
        // Threshold 0.5 is the research's value. A cue heard faintly in a noisy room, scoring between 0.5 and
        // 0.7, is still the cue and must be blanked - a raised threshold would leave it for the model to write.
        var pcm = new ReadyCueTestClip(900, noise: 2700).Cue(50).Pcm();
        var score = ReadyCue.Find(pcm, Rate, pcm.Length).BestScore;
        Assert.InRange(score, 0.5, 0.7);

        var actual = await Record(transcribePath, pcm, cuePlayed: true);

        AssertPcm(Blanked(pcm, BlankStartMs(50), BlankEndMs(50)), actual);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DroppedBufferBeforeTheCue_TheCueIsFoundWhereItIs_WordsUntouched(bool transcribePath)
    {
        // Inspection five, finding 1: a dropped capture buffer shifts everything after it in the clip, so a
        // position computed from time lands in the words. The audio carries no such offset. The cue played at
        // 150 ms of wall time and the buffer [50, 100) was dropped, so the clip holds it at 100 ms.
        var wall = new ReadyCueTestClip(900).Cue(150).Words(BlankEndMs(150)).Pcm();
        var pcm = WithoutMs(wall, 50, 100);

        var actual = await Record(transcribePath, pcm, cuePlayed: true);

        AssertPcm(Blanked(pcm, BlankStartMs(100), BlankEndMs(100)), actual);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DroppedBufferBetweenTheCueAndTheWords_WordsUntouched(bool transcribePath)
    {
        // The cue at 50 ms, a 100 ms pause after its ring-down, and the buffer [300, 350) of that pause dropped.
        var wall = new ReadyCueTestClip(900).Cue(50).Words(400).Pcm();
        var pcm = WithoutMs(wall, 300, 350);

        var actual = await Record(transcribePath, pcm, cuePlayed: true);

        AssertPcm(Blanked(pcm, BlankStartMs(50), BlankEndMs(50)), actual);
    }

    [Fact]
    public async Task DroppedBufferInsideTheCue_WordsWithinThatLengthOfTheRingDown_LoseThatMuch()
    {
        // The one gap ruling K1 leaves, pinned so it cannot change unnoticed. A buffer dropped INSIDE the cue
        // shortens the cue in the clip, but the span blanked is the full cue length from the match. The match
        // lands on the cue's start, where nearly all its energy is, so the blank reaches one dropped buffer past
        // the cue's real end. Words beginning within that much of the ring-down lose it: here the owner speaks
        // the instant the ring-down ends and [100, 150) of the cue was dropped, so the first 50 ms of words are
        // zeroed. The corpus puts the earliest speech 179.5 ms after the cue's end (evidence, phase 4).
        var wall = new ReadyCueTestClip(900).Cue(50).Words(BlankEndMs(50)).Pcm();
        var pcm = WithoutMs(wall, 100, 150);

        var actual = await Record(transcribePath: true, pcm, cuePlayed: true);

        AssertPcm(Blanked(pcm, BlankStartMs(50), BlankEndMs(50)), actual);
        var lostWords = pcm.AsSpan((BlankEndMs(50) - BufferMs) * BytesPerMs, BufferMs * BytesPerMs).ToArray();
        Assert.Contains(Enumerable.Range(0, lostWords.Length / 2), i => Math.Abs((short)(lostWords[2 * i] | (lostWords[2 * i + 1] << 8))) > 1000);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlaybackReportedLate_PositionsNothing_WordsUntouched(bool transcribePath)
    {
        // Inspection five, finding 2: NAudio's padded output buffer makes the playback report up to ~100 ms
        // late. The report must not move the span: capture runs past the cue and the ring-down, the owner speaks
        // at once, and the report arrives only after all of it.
        var pcm = new ReadyCueTestClip(900).Cue(50).Words(BlankEndMs(50)).Pcm();
        var (recorder, mic, clock, transcribed) = NewRecorder();
        await using var _ = recorder;
        await recorder.StartAsync();

        Emit(recorder, mic, pcm, cuePlayed: true);
        clock.AdvanceMs(850);
        recorder.NoteReadyCueFinished();

        AssertPcm(Blanked(pcm, BlankStartMs(50), BlankEndMs(50)), await Snapshot(recorder, transcribed, transcribePath, pcm.Length));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlaybackEndNeverReported_NothingIsBlanked(bool transcribePath)
    {
        // Ruling L1: with no completion report inside its allowance nothing shows the whole cue sounded, so even
        // a whole cue-shaped sound in the audio is left alone. The stop does not wait past the allowance.
        var pcm = new ReadyCueTestClip(900).Cue(50).Words(BlankEndMs(50)).Pcm();
        var (recorder, mic, clock, transcribed) = NewRecorder();
        await using var _ = recorder;
        await recorder.StartAsync();

        Emit(recorder, mic, pcm, cuePlayed: true);
        clock.AdvanceMs((long)DesktopAudioCue.PlaybackEndAllowance.TotalMilliseconds + 1);

        AssertPcm(pcm, await Snapshot(recorder, transcribed, transcribePath, pcm.Length));
    }

    [Theory]
    [InlineData(true, 50)]
    [InlineData(true, 100)]
    [InlineData(false, 50)]
    [InlineData(false, 100)]
    public async Task PlaybackErrorAfterAPartialCue_ScoringOverTheThreshold_NothingIsBlanked(bool transcribePath, int playedMs)
    {
        // Inspection six, finding 1, the inspector's timeline: the cue starts at 150 ms, playback stops with an
        // error after 50 (or 100) ms, the microphone hears it at 20% of the generated amplitude, and the owner's
        // quiet first word starts right after a 50 ms ring-down. The partial cue still matches over the threshold,
        // and blanking the full cue span from that match would erase the start of the word. The player reports
        // the error, so nothing is blanked.
        const int cueAtMs = 150;
        int wordsAtMs = cueAtMs + playedMs + RingDownMs;
        var pcm = new ReadyCueTestClip(900).Cue(cueAtMs, gain: 0.2, playedMs: playedMs).Words(wordsAtMs, level: 1500).Pcm();
        var search = ReadyCue.Find(pcm, Rate, pcm.Length);
        Assert.True(search.Found && BlankEndMs(cueAtMs) > wordsAtMs,
            $"the partial cue scored {search.BestScore:F2}; the test needs one the search would blank into the words");

        var (recorder, mic, clock, transcribed) = NewRecorder();
        await using var _ = recorder;
        await recorder.StartAsync();
        Emit(recorder, mic, pcm, cuePlayed: true);
        clock.AdvanceMs(cueAtMs + playedMs);
        recorder.NoteReadyCueFailed("playback stopped with an error: device lost");

        AssertPcm(pcm, await Snapshot(recorder, transcribed, transcribePath, pcm.Length));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlaybackNeverStarted_ACueShapedSoundIsNotBlanked(bool transcribePath)
    {
        // Inspection six, finding 2: the dialog hands the cue to the device and the device fails to initialise,
        // so no cue sounded. Something at the start of the clip that the search would match - the corpus holds a
        // speech segment scoring 0.509 - must not be erased on the strength of a playback that never happened.
        var pcm = new ReadyCueTestClip(900).Cue(50).Words(BlankEndMs(50)).Pcm();
        Assert.True(ReadyCue.Find(pcm, Rate, pcm.Length).Found);

        var (recorder, mic, _, transcribed) = NewRecorder();
        await using var _ = recorder;
        await recorder.StartAsync();
        Emit(recorder, mic, pcm, cuePlayed: true);
        recorder.NoteReadyCueFailed("playback failed: no output device");

        AssertPcm(pcm, await Snapshot(recorder, transcribed, transcribePath, pcm.Length));
    }

    [Fact]
    public async Task CompletionReportedAfterAFailure_DoesNotSetTheGate()
    {
        // Only the first outcome counts: a success report after a failure does not turn the failed cue into a
        // blank.
        var pcm = new ReadyCueTestClip(900).Cue(50).Words(BlankEndMs(50)).Pcm();
        var (recorder, mic, _, transcribed) = NewRecorder();
        await using var _ = recorder;
        await recorder.StartAsync();
        Emit(recorder, mic, pcm, cuePlayed: true);
        recorder.NoteReadyCueFailed("playback stopped with an error: device lost");
        recorder.NoteReadyCueFinished();

        AssertPcm(pcm, await Snapshot(recorder, transcribed, transcribePath: true, pcm.Length));
    }

    [Fact]
    public async Task SendInsideTheCue_FailureReported_StopsWaitingAtOnce()
    {
        // A failure report releases the stop just as a completion does: the background Send does not sit out the
        // three-second allowance for a cue that is not going to finish.
        var pcm = new ReadyCueTestClip(450).Cue(50).Pcm();
        var (recorder, mic, _, transcribed) = NewRecorder();
        await using var _ = recorder;
        await recorder.StartAsync();
        Emit(recorder, mic, pcm, cuePlayed: true, toMs: 150);
        var rest = Task.Run(async () =>
        {
            await Task.Delay(400);
            recorder.NoteReadyCueFailed("playback stopped with an error: device lost");
        });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var actual = await Snapshot(recorder, transcribed, transcribePath: true, 150 * BytesPerMs);
        await rest;

        Assert.True(watch.ElapsedMilliseconds < 2000, $"the stop waited {watch.ElapsedMilliseconds} ms after the failure report");
        AssertPcm(pcm.AsSpan(0, 150 * BytesPerMs).ToArray(), actual);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NoCuePlayed_ACueShapedSoundIsNotBlanked(bool transcribePath)
    {
        // A recorder the dialog never played a cue into (the wake-word test, a capture with no published
        // recorder) keeps every sample, even one that sounds exactly like the cue.
        var pcm = new ReadyCueTestClip(900).Cue(50).Words(BlankEndMs(50)).Pcm();

        AssertPcm(pcm, await Record(transcribePath, pcm, cuePlayed: false, reportEnd: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CuePlayedButNotInTheAudio_NothingIsBlanked(bool transcribePath)
    {
        // A headset, a quiet speaker, a device that does not hear its own output: the cue was played but the
        // microphone never heard it. There is no clock-based position behind the search.
        var pcm = new ReadyCueTestClip(900).Words(300).Pcm();

        AssertPcm(pcm, await Record(transcribePath, pcm, cuePlayed: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CuePlayed_SpeechLikeSoundInTheSearchWindow_IsNotBlanked(bool transcribePath)
    {
        // The owner talks over the start of the clip and the cue was not heard: speech must not pass for it.
        var pcm = new ReadyCueTestClip(1700).Words(0).Pcm();
        var search = ReadyCue.Find(pcm, Rate, pcm.Length);
        Assert.True(search.BestScore < 0.5, $"the stand-in speech scored {search.BestScore:F2}; the test needs a non-cue sound");

        AssertPcm(pcm, await Record(transcribePath, pcm, cuePlayed: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CueSoundLaterThanTheSearchWindow_IsNotBlanked(bool transcribePath)
    {
        // The search reaches 1.5 s past the first audio. The cue is played on first audio, so a cue-shaped sound
        // later in the clip is not it - something the owner played on purpose, for instance.
        const int lateCueMs = BufferMs + SearchAfterFirstAudioMs + 100;
        var pcm = new ReadyCueTestClip(2300).Words(0, 1000).Cue(lateCueMs).Pcm();

        AssertPcm(pcm, await Record(transcribePath, pcm, cuePlayed: true));
    }

    // Send while the cue is still playing. The microphone has captured 50 ms of room and the first 100 ms of the
    // cue; the rest of the cue and 200 ms of room reach the capture only as the cue finishes, which the report
    // marks. The recorder must keep capturing until that report - a stop before it cuts the cue in half, and a
    // half cue is not found.
    private static async Task<byte[]> SendInsideTheCue(bool transcribePath, int reportAfterSendMs)
    {
        var pcm = new ReadyCueTestClip(450).Cue(50).Pcm();
        var (recorder, mic, _, transcribed) = NewRecorder();
        await using var _ = recorder;
        await recorder.StartAsync();

        Emit(recorder, mic, pcm, cuePlayed: true, toMs: 150);
        var rest = Task.Run(async () =>
        {
            await Task.Delay(reportAfterSendMs);
            Emit(recorder, mic, pcm, cuePlayed: false, fromMs: 150);
            recorder.NoteReadyCueFinished();
        });

        var actual = await Snapshot(recorder, transcribed, transcribePath, pcm.Length);
        await rest;
        AssertPcm(Blanked(pcm, BlankStartMs(50), BlankEndMs(50)), actual);
        return actual;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendInsideTheCue_ReportDuringTheStopTail_TheWholeCueIsBlanked(bool transcribePath)
        => await SendInsideTheCue(transcribePath, reportAfterSendMs: 20);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendInsideTheCue_ReportAfterTheStopTail_CaptureWaitsAndTheWholeCueIsBlanked(bool transcribePath)
        => await SendInsideTheCue(transcribePath, reportAfterSendMs: 600);

    [Fact]
    public void Find_TheExactCue_MatchesAtItsSample()
    {
        var pcm = new ReadyCueTestClip(600).Cue(137).Pcm();

        var search = ReadyCue.Find(pcm, Rate, pcm.Length);

        Assert.True(search.Found);
        Assert.Equal(137 * SamplesPerMs, search.MatchSample);
        Assert.Equal(BlankStartMs(137) * BytesPerMs, search.BlankStartByte);
        Assert.Equal(BlankEndMs(137) * BytesPerMs, search.BlankEndByte);
    }

    [Fact]
    public void Find_SearchRegionShorterThanTheCue_FindsNothing()
    {
        var pcm = new ReadyCueTestClip(600).Cue(0).Pcm();

        var search = ReadyCue.Find(pcm, Rate, (CueMs - 1) * BytesPerMs);

        Assert.False(search.Found);
        Assert.Equal(-1, search.MatchSample);
    }
}
