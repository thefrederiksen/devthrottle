using Avalonia.Headless.XUnit;
using CcDirector.Avalonia.HostedAi;
using CcDirector.Avalonia.Voice;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.HostedAi;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Issue #2925, at the dialog: the Speak dialog plays the ready cue when the microphone goes live, and
/// must hand the device's outcome to the recorder it was played into. The recorder searches for and blanks
/// the cue only after a report that playback COMPLETED (ruling L1, mission phase 7). The recorder tests prove
/// the blanking; this proves the CALLER wires it - that a completion report is what sets the gate, and that
/// playing the cue, a failure report, or no report at all do not.
/// </summary>
public sealed class SpeakDialogReadyCueBlankingTests : IDisposable
{
    public void Dispose() => DesktopHostedAiGate.CheckOverrideForTests = null;

    private sealed class FakeMic : IAudioSource
    {
        public event Action<byte[]>? OnAudioChunk;
        public string Description => "Fake Test Microphone";
        public void Start() { }
        public void Stop() { }
        public Task StopAsync(TimeSpan drainTimeout) => Task.CompletedTask;
        public void Emit(byte[] chunk) => OnAudioChunk?.Invoke(chunk);
    }

    private sealed class FakeClock : TimeProvider
    {
        private long _ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _ms);
        public void AdvanceMs(long ms) => Interlocked.Add(ref _ms, ms);
    }

    private const int BytesPerMs = MicAudioCapture.SampleRate * (MicAudioCapture.BitsPerSample / 8) * MicAudioCapture.Channels / 1000;

    public enum CueOutcome { Completed, Failed, NeverReported }

    [AvaloniaTheory]
    [InlineData(CueOutcome.Completed)]
    [InlineData(CueOutcome.Failed)]
    [InlineData(CueOutcome.NeverReported)]
    public async Task CaptureLive_PlaysTheCue_OnlyAReportedCompletionBlanksItInTheRecorderItWasPlayedInto(CueOutcome outcome)
    {
        DesktopHostedAiGate.CheckOverrideForTests = _ => Task.FromResult(HostedAiState.Ready);

        var mic = new FakeMic();
        var clock = new FakeClock();
        byte[]? transcribed = null;
        BatchDictationRecorder? recorder = null;
        Action? cueFinished = null;
        Action<string>? cueFailed = null;
        var cuePlayed = false;

        var dialog = new SpeakDialog(new AgentOptions())
        {
            // PIN THE DEVICE, because this test is not about device discovery. Without these the dialog
            // runs the real winmm enumeration on its way to the recorder factory, so the test depended on
            // whatever microphones the machine running it happens to have - and on macOS and Linux, where
            // there is no winmm at all, startup threw before the factory was ever reached and the test
            // reported a failure of the ready cue it had not got anywhere near.
            ResolveMicForTests = () => new MicDevice(1, "Fake Test Microphone"),
            EnumerateMicsForTests = () => new List<MicDevice> { new(1, "Fake Test Microphone") },
            RecorderFactoryForTests = _ =>
            {
                recorder = new BatchDictationRecorder(new AgentOptions(), _ => mic, (pcm, _, _) =>
                {
                    transcribed = pcm;
                    return Task.FromResult(new DictationResult("raw", "clean", 0));
                }, timeProvider: clock);
                return Task.FromResult(recorder);
            },
            ReadyCueForTests = (onFinished, onFailed) =>
            {
                cuePlayed = true;
                cueFinished = onFinished;
                cueFailed = onFailed;
            },
        };
        dialog.Show();
        Pump(() => recorder is not null, TimeSpan.FromSeconds(5));
        Assert.NotNull(recorder);

        // The clip: 50 ms of room, the cue the speaker played at 50 ms, its ring-down, then the owner's words.
        var pcm = new ReadyCueTestClip(900).Cue(50).Words(300).Pcm();
        const int buffer = 50 * BytesPerMs;

        // The first 50 ms audio buffer arrives: the dialog flips to RECORDING and plays the cue.
        mic.Emit(pcm.AsSpan(0, buffer).ToArray());
        Pump(() => cuePlayed, TimeSpan.FromSeconds(5));
        Assert.True(cuePlayed, "the dialog must play the ready cue when capture goes live");
        Assert.NotNull(cueFinished);
        Assert.NotNull(cueFailed);

        for (int offset = buffer; offset < pcm.Length; offset += buffer)
            mic.Emit(pcm.AsSpan(offset, buffer).ToArray());
        switch (outcome)
        {
            case CueOutcome.Completed:
                clock.AdvanceMs(300);
                cueFinished!();
                break;
            case CueOutcome.Failed:
                clock.AdvanceMs(300);
                cueFailed!("playback stopped with an error: test");
                break;
            case CueOutcome.NeverReported:
                // The allowance is spent, so the stop does not wait for a report that is not coming.
                clock.AdvanceMs((long)DesktopAudioCue.PlaybackEndAllowance.TotalMilliseconds + 1);
                break;
        }

        await recorder!.TranscribeAsync();
        dialog.Close();

        // The same clip holds the whole cue in every case: the audio cannot tell them apart, only the report can.
        // Completed: the found span, 40 ms to 300 ms, is silence. Failed or never reported: nothing is blanked,
        // because nothing proves the whole cue sounded (ruling L1).
        var expected = (byte[])pcm.Clone();
        if (outcome == CueOutcome.Completed)
            Array.Clear(expected, 40 * BytesPerMs, 260 * BytesPerMs);
        Assert.NotNull(transcribed);
        Assert.True(expected.AsSpan().SequenceEqual(transcribed),
            outcome == CueOutcome.Completed
                ? "a completed cue and its ring-down must reach transcription as silence, and everything else untouched"
                : $"with playback {outcome}, every captured sample must reach transcription untouched");
    }

    private static void Pump(Func<bool> done, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (done()) return;
            Thread.Sleep(10);
        }
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}
