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
/// Inspection one, finding 2 (issue #2929): the Speak dialog builds its recorder on the interface thread,
/// and the production <see cref="MicAudioCapture"/> constructor used to ask Windows for the device's name
/// there (an MMDeviceEnumerator call for the default, <c>WaveInEvent.GetCapabilities</c> for a concrete
/// device). The name is now resolved off the interface thread with the device number and handed in.
///
/// The proof uses a device number no machine has. Any device query for it throws, so a constructor or a
/// switch path that still queries Windows fails these tests on every machine - with or without a
/// microphone - instead of passing quietly because the query happened to be fast.
/// </summary>
public sealed class MicCaptureConstructionQueriesNoDeviceTests : IDisposable
{
    // No winmm capture device has this number; waveInGetDevCaps answers BADDEVICEID for it.
    private const int NoSuchDevice = 4096;

    // Switching microphone saves the choice to config.json; keep that out of the real config. The assembly
    // runs sequentially, so the process-wide variable is not raced.
    private readonly string? _oldRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-director-mic-query-tests", Guid.NewGuid().ToString("N"));

    public MicCaptureConstructionQueriesNoDeviceTests()
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

    [Fact]
    public void MicAudioCapture_Constructor_TakesTheResolvedNameAndQueriesNoDevice()
    {
        using var capture = new MicAudioCapture(NoSuchDevice, "Resolved Test Mic");

        Assert.Equal("Resolved Test Mic", capture.Description);
    }

    [Fact]
    public void BatchDictationRecorder_ProductionMicrophone_CarriesTheResolvedNameAndQueriesNoDevice()
    {
        var source = BatchDictationRecorder.CreateMicrophone(NoSuchDevice, "Resolved Test Mic");
        try
        {
            Assert.IsType<MicAudioCapture>(source);
            Assert.Equal("Resolved Test Mic", source.Description);
        }
        finally
        {
            (source as IDisposable)?.Dispose();
        }
    }

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
    public void SwitchingMicrophone_UsesTheListedName_AndQueriesNoDeviceOnTheInterfaceThread()
    {
        DesktopHostedAiGate.CheckOverrideForTests = _ => Task.FromResult(HostedAiState.Ready);
        var built = new List<MicDevice>();

        var dialog = new SpeakDialog(new AgentOptions())
        {
            ResolveMicForTests = () => new MicDevice(MicDevices.DefaultDeviceNumber, "Default - Test"),
            EnumerateMicsForTests = () => new List<MicDevice>
            {
                new(MicDevices.DefaultDeviceNumber, "Default - Test"),
                new(NoSuchDevice, "Listed Test Mic"),
            },
            RecordMicStartForTests = (_, _) => { },
            RecorderFactoryForTests = device =>
            {
                lock (built) built.Add(device);
                return Task.FromResult(new BatchDictationRecorder(new AgentOptions(), _ => new FakeMic(),
                    (_, _, _) => Task.FromResult(new DictationResult("raw", "clean", 0))));
            },
        };
        try
        {
            dialog.Show();
            var selector = dialog.FindControl<ComboBox>("MicSelector")!;
            Pump(() => selector.ItemCount == 2 && built.Count == 1, TimeSpan.FromSeconds(5));
            Assert.Equal(2, selector.ItemCount);

            selector.SelectedIndex = 1;
            Pump(() => built.Count == 2, TimeSpan.FromSeconds(5));

            var status = dialog.FindControl<TextBlock>("StatusLabel")!;
            Assert.NotEqual("ERROR", status.Text);
            Assert.Equal(2, built.Count);
            Assert.Equal(new MicDevice(NoSuchDevice, "Listed Test Mic"), built[1]);
        }
        finally
        {
            dialog.Close();
            Pump(() => false, TimeSpan.FromMilliseconds(200));
        }
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
