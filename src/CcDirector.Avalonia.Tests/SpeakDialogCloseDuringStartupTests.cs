using Avalonia.Headless.XUnit;
using CcDirector.Avalonia.HostedAi;
using CcDirector.Avalonia.Voice;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.HostedAi;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Regression tests for the leak that produced 12.69 GB on a 68-hour Director
/// (devthrottle_internal#1286, #1262).
///
/// THE DEFECT. Dialog startup is an async sequence. Close the window while it is mid-flight and the
/// teardown runs immediately, sees no recorder, and finishes - then the continuation carries on,
/// builds a recorder, and publishes it to a window that no longer exists. There is no second Closed
/// event, so nothing ever disposes it. Each stranded recorder is rooted by its own live NAudio
/// capture thread (WaveInEvent.DoRecording -> MicAudioCapture -> Action&lt;byte[]&gt; ->
/// BatchDictationRecorder -> MemoryStream -> byte[]), so it can never be collected and never stops
/// recording. 87 of them were measured, nine still capturing.
///
/// THE FIX. A volatile close latch, set SYNCHRONOUSLY in Closed, checked before construction and
/// again after the microphone starts - disposing rather than publishing.
///
/// WHY THESE TESTS INJECT EVERYTHING. An earlier version of this file drove the real recorder and a
/// real microphone. On a machine without a capture device StartAsync throws and self-disposes, so the
/// tests passed even with the fix removed - they could not fail. Both the pre-flight and the recorder
/// factory are now injected, so the interleaving is deterministic and device-independent, and each
/// test fails when its own guard is removed.
/// </summary>
public sealed class SpeakDialogCloseDuringStartupTests : IDisposable
{
    public void Dispose() => DesktopHostedAiGate.CheckOverrideForTests = null;

    /// <summary>A microphone that needs no device. Records whether the recorder tore it down.</summary>
    private sealed class FakeMic : IAudioSource
    {
        public event Action<byte[]>? OnAudioChunk;
        public string Description => "Fake Test Microphone";
        public int StopCallCount { get; private set; }
        public void Start() { }
        public void Stop() => StopCallCount++;
        public Task StopAsync(TimeSpan drainTimeout) { StopCallCount++; return Task.CompletedTask; }
        public void Emit(byte[] chunk) => OnAudioChunk?.Invoke(chunk);
    }

    private static BatchDictationRecorder NewFakeRecorder(FakeMic mic) =>
        new(new AgentOptions(), _ => mic, (_, _, _) => Task.FromResult(new DictationResult("raw", "clean", 0)));

    /// <summary>
    /// Close while the hosted-AI pre-flight is still outstanding. Startup must abandon: NO recorder
    /// may be constructed at all, because nothing would ever dispose it.
    /// Fails if the post-pre-flight `_closed` check is removed.
    /// </summary>
    [AvaloniaFact]
    public void ClosingDuringThePreflight_ConstructsNoRecorderAtAll()
    {
        var preflightReached = new TaskCompletionSource();
        var releasePreflight = new TaskCompletionSource();
        DesktopHostedAiGate.CheckOverrideForTests = async _ =>
        {
            preflightReached.TrySetResult();
            await releasePreflight.Task;
            return HostedAiState.Ready;      // the dangerous answer: startup would otherwise proceed
        };

        int built = 0;
        var dialog = new SpeakDialog(new AgentOptions())
        {
            // Pinned for the reason given on the test below - and here it matters twice over, because this
            // test's assertion is that NO recorder was built. A startup that died in device enumeration
            // would satisfy that without the dialog's own guard existing at all.
            ResolveMicForTests = () => new MicDevice(1, "Fake Test Microphone"),
            EnumerateMicsForTests = () => new List<MicDevice> { new(1, "Fake Test Microphone") },
            RecorderFactoryForTests = _ =>
            {
                Interlocked.Increment(ref built);
                return Task.FromResult(NewFakeRecorder(new FakeMic()));
            },
        };
        dialog.Show();

        Pump(() => preflightReached.Task.IsCompleted, TimeSpan.FromSeconds(5));
        Assert.True(preflightReached.Task.IsCompleted, "startup never reached the pre-flight");

        dialog.Close();                       // the user closes mid-pre-flight
        releasePreflight.TrySetResult();      // ...and only then does the pre-flight answer
        Pump(() => false, TimeSpan.FromSeconds(1));

        Assert.Equal(0, Volatile.Read(ref built));
        Assert.Null(dialog.BackgroundRecorder);
    }

    /// <summary>
    /// Close while the microphone is starting - after the pre-flight has passed and the recorder
    /// exists. The recorder that WAS built must be disposed and never published.
    /// Fails if the post-StartAsync `_closed` check is removed: the recorder is then published to a
    /// dead dialog and its microphone is never torn down.
    /// </summary>
    [AvaloniaFact]
    public void ClosingWhileTheMicrophoneStarts_DisposesTheRecorderAndNeverPublishesIt()
    {
        DesktopHostedAiGate.CheckOverrideForTests = _ => Task.FromResult(HostedAiState.Ready);

        var factoryReached = new TaskCompletionSource();
        var releaseFactory = new TaskCompletionSource();
        var mic = new FakeMic();
        BatchDictationRecorder? constructed = null;

        var dialog = new SpeakDialog(new AgentOptions())
        {
            // PIN THE DEVICE, because this test is not about device discovery. Without these the dialog
            // runs the real winmm enumeration on its way to the recorder factory, so the test depended on
            // whatever microphones the machine running it happens to have - and on macOS and Linux, where
            // there is no winmm at all, startup threw before the factory was ever reached.
            ResolveMicForTests = () => new MicDevice(1, "Fake Test Microphone"),
            EnumerateMicsForTests = () => new List<MicDevice> { new(1, "Fake Test Microphone") },
            RecorderFactoryForTests = async _ =>
            {
                factoryReached.TrySetResult();
                await releaseFactory.Task;     // hold startup open at a deterministic barrier
                constructed = NewFakeRecorder(mic);
                return constructed;
            },
        };
        dialog.Show();

        Pump(() => factoryReached.Task.IsCompleted, TimeSpan.FromSeconds(5));
        Assert.True(factoryReached.Task.IsCompleted, "startup never reached recorder construction");

        dialog.Close();                        // closed while the recorder is being built/started
        releaseFactory.TrySetResult();
        Pump(() => constructed is not null && mic.StopCallCount > 0, TimeSpan.FromSeconds(5));

        Assert.NotNull(constructed);
        // Asserted on the EXACT instance that was built, not on a global counter: its microphone was
        // torn down, which only DisposeAsync does.
        Assert.True(mic.StopCallCount > 0,
            "the recorder built during a close must be disposed, not published to a dead dialog");
        Assert.Null(dialog.BackgroundRecorder);
    }

    /// <summary>
    /// Pump the headless dispatcher until the condition holds or the budget expires. Startup resumes
    /// on the UI thread, so it only progresses while this loop runs. global:: because inside
    /// CcDirector.Avalonia.Tests a bare `Avalonia.` binds to CcDirector.Avalonia.
    /// </summary>
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
