using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CcDirector.Avalonia.HostedAi;
using CcDirector.Avalonia.Voice;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.HostedAi;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Issue #2929: the Speak dialog listed capture devices on the interface thread before opening the
/// microphone - 57 to 243 ms per open. The device queries now run in the background: the dialog shows
/// GETTING READY at once, opens the microphone as soon as the saved choice is resolved, and fills the
/// selector whenever the list arrives.
///
/// The list is held at a barrier that is only released at the end. If enumeration ran on the interface
/// thread, or if the microphone waited for it, the dialog would never reach the recorder factory while
/// the barrier is closed - so these fail rather than hang.
/// </summary>
public sealed class SpeakDialogMicEnumerationOffUiThreadTests : IDisposable
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

    [AvaloniaFact]
    public void Open_ShowsGettingReadyAndOpensTheMicrophone_BeforeTheDeviceListArrives()
    {
        DesktopHostedAiGate.CheckOverrideForTests = _ => Task.FromResult(HostedAiState.Ready);
        using var releaseList = new ManualResetEventSlim(false);
        var listStarted = 0;
        BatchDictationRecorder? built = null;
        int? builtForDevice = null;

        var dialog = new SpeakDialog(new AgentOptions())
        {
            ResolveMicForTests = () => new MicDevice(2, "USB Test Mic"),
            EnumerateMicsForTests = () =>
            {
                Interlocked.Exchange(ref listStarted, 1);
                // Blocks whatever thread runs it. On the interface thread this would freeze the dialog.
                releaseList.Wait(TimeSpan.FromSeconds(10));
                return new List<MicDevice> { new(MicDevices.DefaultDeviceNumber, "Default - Test"), new(2, "USB Test Mic") };
            },
            RecorderFactoryForTests = device =>
            {
                builtForDevice = device.Number;
                built = new BatchDictationRecorder(new AgentOptions(), _ => new FakeMic(),
                    (_, _, _) => Task.FromResult(new DictationResult("raw", "clean", 0)));
                return Task.FromResult(built);
            },
        };
        try
        {
            dialog.Show();
            Pump(() => built is not null, TimeSpan.FromSeconds(5));

            var status = dialog.FindControl<TextBlock>("StatusLabel")!;
            var selector = dialog.FindControl<ComboBox>("MicSelector")!;

            Assert.Equal(1, Volatile.Read(ref listStarted));
            Assert.Equal("GETTING READY", status.Text);
            Assert.NotNull(built);                  // the microphone opened while the list was still held
            Assert.Equal(2, builtForDevice);        // ...on the resolved saved device
            Assert.Equal(0, selector.ItemCount);    // ...and the selector had not been filled yet

            releaseList.Set();
            Pump(() => selector.ItemCount == 2, TimeSpan.FromSeconds(5));
            Assert.Equal(2, selector.ItemCount);
            Assert.Equal(2, ((MicDevice)selector.SelectedItem!).Number);
        }
        finally
        {
            releaseList.Set();
            dialog.Close();
            Pump(() => false, TimeSpan.FromMilliseconds(200));
        }
    }

    /// <summary>
    /// Resolving the saved microphone is now a real await, so the window can close during it. No recorder
    /// may be built after that - nothing would dispose it (the close-during-startup leak). The guard that
    /// holds this is the closed check at the top of the recorder start, which this path reaches; removing
    /// that check turns this test red.
    /// </summary>
    [AvaloniaFact]
    public void ClosingWhileTheMicrophoneIsResolved_BuildsNoRecorder()
    {
        DesktopHostedAiGate.CheckOverrideForTests = _ => Task.FromResult(HostedAiState.Ready);
        using var releaseResolve = new ManualResetEventSlim(false);
        var resolveStarted = 0;
        var built = 0;

        var dialog = new SpeakDialog(new AgentOptions())
        {
            ResolveMicForTests = () =>
            {
                Interlocked.Exchange(ref resolveStarted, 1);
                releaseResolve.Wait(TimeSpan.FromSeconds(10));
                return new MicDevice(MicDevices.DefaultDeviceNumber, "Default - Test");
            },
            EnumerateMicsForTests = () => new List<MicDevice> { new(MicDevices.DefaultDeviceNumber, "Default - Test") },
            RecorderFactoryForTests = _ =>
            {
                Interlocked.Increment(ref built);
                return Task.FromResult(new BatchDictationRecorder(new AgentOptions(), _ => new FakeMic(),
                    (_, _, _) => Task.FromResult(new DictationResult("raw", "clean", 0))));
            },
        };
        dialog.Show();
        Pump(() => Volatile.Read(ref resolveStarted) == 1, TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref resolveStarted));

        dialog.Close();
        releaseResolve.Set();
        Pump(() => false, TimeSpan.FromSeconds(1));

        Assert.Equal(0, Volatile.Read(ref built));
    }

    /// <summary>
    /// Inspection two, finding 3: resolving the saved microphone can outlast the GETTING READY window. The
    /// dialog then shows the failure; when resolution finally completes it must NOT open a microphone into
    /// that failed dialog, and the timeout must say it was still resolving, with the elapsed time and the
    /// saved device name.
    /// </summary>
    [AvaloniaFact]
    public void ResolutionOutlastsTheReadyWindow_DialogOpen_BuildsNoRecorderAndLogsTheResolution()
    {
        DesktopHostedAiGate.CheckOverrideForTests = _ => Task.FromResult(HostedAiState.Ready);
        using var releaseResolve = new ManualResetEventSlim(false);
        var resolveStarted = 0;
        var built = 0;

        using var log = CcDirector.Core.Utilities.FileLog.RedirectForTests();
        var dialog = new SpeakDialog(new AgentOptions())
        {
            PersistedMicNameForTests = () => "Slow USB Mic",
            ResolveMicForTests = () =>
            {
                Interlocked.Exchange(ref resolveStarted, 1);
                releaseResolve.Wait(TimeSpan.FromSeconds(10));
                return new MicDevice(3, "Slow USB Mic");
            },
            EnumerateMicsForTests = () => new List<MicDevice> { new(3, "Slow USB Mic") },
            RecorderFactoryForTests = _ =>
            {
                Interlocked.Increment(ref built);
                return Task.FromResult(new BatchDictationRecorder(new AgentOptions(), _ => new FakeMic(),
                    (_, _, _) => Task.FromResult(new DictationResult("raw", "clean", 0))));
            },
            ReadyCueForTests = (_, _) => { },
            RecordMicStartForTests = (_, _) => { },
        };
        var status = dialog.FindControl<TextBlock>("StatusLabel")!;
        try
        {
            dialog.Show();
            Pump(() => Volatile.Read(ref resolveStarted) == 1, TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref resolveStarted));

            dialog.RaiseReadyTimeoutForTests();          // the window runs out; resolution still held
            Assert.Equal("ERROR", status.Text);

            releaseResolve.Set();                        // resolution completes with the dialog still open
            Pump(() => Volatile.Read(ref built) > 0, TimeSpan.FromSeconds(1));
            Assert.Equal(0, Volatile.Read(ref built));
            Assert.Equal("ERROR", status.Text);
        }
        finally
        {
            releaseResolve.Set();
            dialog.Close();
            Pump(() => false, TimeSpan.FromMilliseconds(200));
        }

        var resolvingLine = new System.Text.RegularExpressions.Regex(
            "\\[SpeakDialog\\] ready window ran out while the saved microphone was still being resolved: "
            + "elapsedMs=\\d+, savedDevice=\"Slow USB Mic\"");
        Assert.Single(log.DrainAndReadLines(), l => resolvingLine.IsMatch(l));
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
