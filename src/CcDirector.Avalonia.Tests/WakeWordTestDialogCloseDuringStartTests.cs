using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using CcDirector.Avalonia.Voice;
using CcDirector.Core.Audio;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using Xunit;
using Avalonia.Controls;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Inspection six, finding 3: the Wake Word test dialog resolves the microphone's name off the interface thread
/// (issue #2929) before it builds the recorder. The close handler disposes only the published recorder, so a
/// window closed during that query - or while the microphone starts - left a live capture with no owner to stop
/// it. These tests prove no microphone is left recording after the window is gone.
/// </summary>
public sealed class WakeWordTestDialogCloseDuringStartTests
{
    private sealed class FakeMic : IAudioSource
    {
        public event Action<byte[]>? OnAudioChunk;
        public string Description => "Fake Test Microphone";
        public Action? OnStart { get; set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public bool Recording => Starts > Stops;
        public void Start()
        {
            Starts++;
            OnStart?.Invoke();
        }
        public void Stop() => Stops++;
        public Task StopAsync(TimeSpan drainTimeout)
        {
            Stops++;
            return Task.CompletedTask;
        }
        public void Emit(byte[] chunk) => OnAudioChunk?.Invoke(chunk);
    }

    private static BatchDictationRecorder NewRecorder(FakeMic mic)
        => new(new AgentOptions(), _ => mic, (_, _, _) => Task.FromResult(new DictationResult("raw", "clean", 0)));

    private static void ClickStart(WakeWordTestDialog dialog)
        => dialog.FindControl<Button>("StartButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void CloseWhileTheDeviceQueryIsHeld_NoRecorderIsBuiltAndNoMicrophoneRecords()
    {
        var mic = new FakeMic();
        var query = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var built = 0;
        var dialog = new WakeWordTestDialog(new AgentOptions())
        {
            DescribeDefaultDeviceForTests = () => query.Task,
            RecorderFactoryForTests = _ =>
            {
                built++;
                return NewRecorder(mic);
            },
        };
        dialog.Show();

        ClickStart(dialog);
        dialog.Close();
        query.SetResult("Fake Test Microphone");
        Pump(() => built > 0 || mic.Starts > 0, TimeSpan.FromSeconds(1));

        Assert.False(mic.Recording, "a microphone was left recording after the window closed");
        Assert.Equal(0, built);
    }

    [AvaloniaFact]
    public void CloseWhileTheMicrophoneStarts_TheStartStopsIt()
    {
        var mic = new FakeMic();
        WakeWordTestDialog? dialog = null;
        dialog = new WakeWordTestDialog(new AgentOptions())
        {
            DescribeDefaultDeviceForTests = () => Task.FromResult("Fake Test Microphone"),
            RecorderFactoryForTests = _ => NewRecorder(mic),
        };
        // The window closes at the moment the microphone is opened, after the close handler can find nothing.
        mic.OnStart = () => dialog.Close();
        dialog.Show();

        ClickStart(dialog);
        Pump(() => mic.Starts > 0 && !mic.Recording, TimeSpan.FromSeconds(5));

        Assert.Equal(1, mic.Starts);
        Assert.False(mic.Recording, "a microphone was left recording after the window closed");
    }

    [AvaloniaFact]
    public void CloseAfterListeningStarted_StopsTheMicrophone()
    {
        // Control: the ordinary close, which already worked, still stops a published recorder.
        var mic = new FakeMic();
        var dialog = new WakeWordTestDialog(new AgentOptions())
        {
            DescribeDefaultDeviceForTests = () => Task.FromResult("Fake Test Microphone"),
            RecorderFactoryForTests = _ => NewRecorder(mic),
        };
        dialog.Show();

        ClickStart(dialog);
        Pump(() => mic.Recording, TimeSpan.FromSeconds(5));
        Assert.True(mic.Recording, "listening never started");

        dialog.Close();
        Pump(() => !mic.Recording, TimeSpan.FromSeconds(5));

        Assert.False(mic.Recording, "a microphone was left recording after the window closed");
    }

    // Runs interface-thread jobs until done or the budget is spent. A "nothing happened" check waits the whole
    // budget, so a late start after the query is released has time to show.
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
