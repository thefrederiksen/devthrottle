using System.Text.Json.Nodes;
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
/// Issue #2928: every dictation start logs its first-audio time, and the Speak dialog's microphone selector
/// shows how long each used device typically takes to wake up - the median of its last 20 measured starts,
/// kept in config.json beside the saved microphone choice. A device never used shows no figure.
///
/// Config is redirected to a temp root through CC_DIRECTOR_ROOT; the assembly runs sequentially, so the
/// process-wide variable is not raced.
/// </summary>
public sealed class MicStartTimesTests : IDisposable
{
    private readonly string? _oldRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-director-mic-start-tests", Guid.NewGuid().ToString("N"));

    public MicStartTimesTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        DesktopHostedAiGate.CheckOverrideForTests = null;
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _oldRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ---- the typical figure ----

    [Fact]
    public void TypicalMs_IsTheMedian_NotTheMean()
    {
        // One 3.5 s start must not brand a device that usually wakes in 0.7 s.
        Assert.Equal(700, MicStartTimes.TypicalMs(new[] { 690, 3500, 700, 710, 650 }));
        Assert.Equal(705, MicStartTimes.TypicalMs(new[] { 700, 710 }));
    }

    [Fact]
    public void TypicalMs_NeverUsed_IsNull()
        => Assert.Null(MicStartTimes.TypicalMs(Array.Empty<int>()));

    [Fact]
    public void Append_KeepsOnlyTheMostRecentTwenty()
    {
        IReadOnlyList<int> history = Array.Empty<int>();
        for (int i = 1; i <= 25; i++) history = MicStartTimes.Append(history, i);

        Assert.Equal(MicStartTimes.HistoryLength, history.Count);
        Assert.Equal(6, history[0]);
        Assert.Equal(25, history[^1]);
    }

    [Fact]
    public void Label_UsedDevice_ShowsTheMeasuredFigure_UnusedDevice_ShowsTheNameAlone()
    {
        Assert.Equal("Webcam Mic - takes 0.7 s to wake up", new MicDevice(1, "Webcam Mic", 700).Label);
        Assert.Equal("USB Mic", new MicDevice(2, "USB Mic").Label);
    }

    [Fact]
    public void Record_ThenRead_RoundTripsThroughConfig_AndLeavesTheSavedChoiceAlone()
    {
        CcDirectorConfigService.MergePatch(new JsonObject
        {
            ["dictation"] = new JsonObject { ["mic_device_name"] = "Webcam Mic" },
        });

        MicStartTimes.Record("Webcam Mic", 710);
        MicStartTimes.Record("Webcam Mic", 690);
        MicStartTimes.Record("USB Mic", 70);

        var config = CcDirectorConfigService.ReadRaw();
        var history = MicStartTimes.Read(config);
        Assert.Equal(new[] { 710, 690 }, history["Webcam Mic"]);
        Assert.Equal(new[] { 70 }, history["USB Mic"]);
        Assert.Equal("Webcam Mic", config["dictation"]?["mic_device_name"]?.GetValue<string>());

        var decorated = MicStartTimes.Decorate(
            new[] { new MicDevice(1, "Webcam Mic"), new MicDevice(2, "USB Mic"), new MicDevice(3, "Never Used") }, history);
        Assert.Equal(700, decorated[0].TypicalStartMs);
        Assert.Equal(70, decorated[1].TypicalStartMs);
        Assert.Null(decorated[2].TypicalStartMs);
    }

    // ---- the recorder measures it ----

    private sealed class FakeMic : IAudioSource
    {
        public event Action<byte[]>? OnAudioChunk;
        public string Description => "Fake Test Microphone";
        public void Start() { }
        public void Stop() { }
        public Task StopAsync(TimeSpan drainTimeout) => Task.CompletedTask;
        public void Emit(byte[] chunk) => OnAudioChunk?.Invoke(chunk);
    }

    [Fact]
    public async Task Recorder_StartToFirstAudio_IsMeasuredWhenTheFirstAudioArrives()
    {
        var mic = new FakeMic();
        await using var recorder = new BatchDictationRecorder(new AgentOptions(), _ => mic,
            (_, _, _) => Task.FromResult(new DictationResult("raw", "clean", 0)));
        recorder.RequestedAtTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        await recorder.StartAsync();
        Assert.Null(recorder.StartToFirstAudioMs);

        await Task.Delay(40);
        mic.Emit(new byte[] { 1, 2 });

        Assert.NotNull(recorder.StartToFirstAudioMs);
        Assert.InRange(recorder.StartToFirstAudioMs!.Value, 30, 5000);
        Assert.Equal("Fake Test Microphone", recorder.DeviceDescription);
    }

    // ---- the dialog records it, and shows it ----

    [AvaloniaFact]
    public void Dialog_RecordsEachMeasuredStart_AndTheSelectorCarriesTheTypicalFigure()
    {
        DesktopHostedAiGate.CheckOverrideForTests = _ => Task.FromResult(HostedAiState.Ready);
        MicStartTimes.Record("Webcam Mic", 700);

        var mic = new FakeMic();
        BatchDictationRecorder? built = null;
        (string Device, int Ms)? recorded = null;
        var dialog = new SpeakDialog(new AgentOptions())
        {
            ResolveMicForTests = () => new MicDevice(1, "Fake Test Microphone"),
            EnumerateMicsForTests = () => new List<MicDevice> { new(1, "Webcam Mic"), new(2, "Never Used") },
            RecorderFactoryForTests = _ =>
            {
                built = new BatchDictationRecorder(new AgentOptions(), _ => mic,
                    (_, _, _) => Task.FromResult(new DictationResult("raw", "clean", 0)));
                return Task.FromResult(built);
            },
            ReadyCueForTests = (_, _) => { },
            RecordMicStartForTests = (device, ms) => recorded = (device, ms),
        };
        try
        {
            dialog.Show();
            var selector = dialog.FindControl<ComboBox>("MicSelector")!;
            Pump(() => built is not null && selector.ItemCount == 2, TimeSpan.FromSeconds(5));

            var items = selector.Items.Cast<MicDevice>().ToList();
            Assert.Equal("Webcam Mic - takes 0.7 s to wake up", items[0].Label);
            Assert.Equal("Never Used", items[1].Label);

            mic.Emit(new byte[] { 1, 2 });
            Pump(() => recorded is not null, TimeSpan.FromSeconds(5));
            Assert.NotNull(recorded);
            Assert.Equal("Fake Test Microphone", recorded!.Value.Device);
            Assert.Equal(built!.StartToFirstAudioMs, recorded.Value.Ms);
        }
        finally
        {
            dialog.Close();
            Pump(() => false, TimeSpan.FromMilliseconds(200));
        }
    }

    [AvaloniaFact]
    public void Dialog_DeviceWithNoHistory_FirstAudioArrives_TheOpenSelectorShowsItsFigure()
    {
        // Inspection three, finding 2: the list is read once at opening, so a first-ever start used to reach
        // config and never the selector - the figure appeared only after closing and reopening the dialog.
        // The real record path runs here (config is redirected to a temp root), not the test seam.
        DesktopHostedAiGate.CheckOverrideForTests = _ => Task.FromResult(HostedAiState.Ready);

        var mic = new FakeMic();
        BatchDictationRecorder? built = null;
        var recordersBuilt = 0;
        var dialog = new SpeakDialog(new AgentOptions())
        {
            ResolveMicForTests = () => new MicDevice(1, "Fake Test Microphone"),
            EnumerateMicsForTests = () => new List<MicDevice> { new(0, "Other Mic"), new(1, "Fake Test Microphone") },
            RecorderFactoryForTests = _ =>
            {
                recordersBuilt++;
                built = new BatchDictationRecorder(new AgentOptions(), _ => mic,
                    (_, _, _) => Task.FromResult(new DictationResult("raw", "clean", 0)));
                return Task.FromResult(built);
            },
            ReadyCueForTests = (_, _) => { },
        };
        try
        {
            dialog.Show();
            var selector = dialog.FindControl<ComboBox>("MicSelector")!;
            Pump(() => built is not null && selector.ItemCount == 2, TimeSpan.FromSeconds(5));
            Assert.Equal("Fake Test Microphone", selector.Items.Cast<MicDevice>().ToList()[1].Label);
            Assert.Equal(1, selector.SelectedIndex);

            mic.Emit(new byte[] { 1, 2 });
            string Label() => selector.Items.Cast<MicDevice>().ToList()[1].Label;
            Pump(() => Label() != "Fake Test Microphone", TimeSpan.FromSeconds(5));

            var expected = new MicDevice(1, "Fake Test Microphone", built!.StartToFirstAudioMs).Label;
            Assert.Equal(expected, Label());
            Assert.Contains("to wake up", Label());
            Assert.Equal("Other Mic", selector.Items.Cast<MicDevice>().ToList()[0].Label);
            Assert.Equal(1, selector.SelectedIndex);
            Pump(() => false, TimeSpan.FromMilliseconds(200));
            Assert.Equal(1, recordersBuilt);   // the refresh did not switch the microphone
        }
        finally
        {
            dialog.Close();
            Pump(() => false, TimeSpan.FromMilliseconds(200));
        }
    }

    // ---- a start that never delivers audio still gets its line (inspection one, finding 3) ----

    private static readonly System.Text.RegularExpressions.Regex NoFirstAudioLine = new(
        "\\[BatchDictationRecorder\\] no first audio: device=\"Fake Test Microphone\", clickToTimeoutMs=\\d+, startRecordingToTimeoutMs=\\d+");

    [Fact]
    public async Task Recorder_NoAudioWithinTheReadyWindow_LogsTheDeviceAndTimings_OnlyWhenNoAudioArrived()
    {
        var mic = new FakeMic();
        await using var recorder = new BatchDictationRecorder(new AgentOptions(), _ => mic,
            (_, _, _) => Task.FromResult(new DictationResult("raw", "clean", 0)));
        recorder.RequestedAtTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        await recorder.StartAsync();

        using (var log = CcDirector.Core.Utilities.FileLog.RedirectForTests())
        {
            recorder.LogNoAudioWithinReadyWindow();
            var lines = log.DrainAndReadLines();
            Assert.Single(lines, l => NoFirstAudioLine.IsMatch(l));
        }

        mic.Emit(new byte[] { 1, 2 });
        using (var log = CcDirector.Core.Utilities.FileLog.RedirectForTests())
        {
            recorder.LogNoAudioWithinReadyWindow();   // audio arrived: that start has its first-audio line
            Assert.DoesNotContain(log.DrainAndReadLines(), l => l.Contains("no first audio"));
        }
    }

    [AvaloniaFact]
    public void Dialog_ReadyWindowRunsOutWithNoAudio_LogsTheNoFirstAudioLineForThatStart()
    {
        DesktopHostedAiGate.CheckOverrideForTests = _ => Task.FromResult(HostedAiState.Ready);
        var mic = new FakeMic();   // never emits: a microphone that does not start
        BatchDictationRecorder? built = null;

        using var log = CcDirector.Core.Utilities.FileLog.RedirectForTests();
        var dialog = new SpeakDialog(new AgentOptions())
        {
            ResolveMicForTests = () => new MicDevice(1, "Fake Test Microphone"),
            EnumerateMicsForTests = () => new List<MicDevice> { new(1, "Fake Test Microphone") },
            RecorderFactoryForTests = _ =>
            {
                built = new BatchDictationRecorder(new AgentOptions(), _ => mic,
                    (_, _, _) => Task.FromResult(new DictationResult("raw", "clean", 0)));
                return Task.FromResult(built);
            },
            ReadyCueForTests = (_, _) => { },
            RecordMicStartForTests = (_, _) => { },
        };
        var status = dialog.FindControl<TextBlock>("StatusLabel")!;
        try
        {
            dialog.Show();
            Pump(() => built is not null && status.Text == "GETTING READY", TimeSpan.FromSeconds(5));
            Assert.NotNull(built);
            dialog.RaiseReadyTimeoutForTests();
            Assert.Equal("ERROR", status.Text);
        }
        finally
        {
            dialog.Close();
            Pump(() => false, TimeSpan.FromMilliseconds(200));
        }

        Assert.Single(log.DrainAndReadLines(), l => NoFirstAudioLine.IsMatch(l));
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
