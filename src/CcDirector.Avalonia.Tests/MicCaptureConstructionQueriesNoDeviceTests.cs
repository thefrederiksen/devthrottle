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
            // Linux reaches the microphone through the sound system's own recorder; the rule is the same.
            Assert.IsType(OperatingSystem.IsLinux() ? typeof(LinuxMicAudioCapture) : typeof(MicAudioCapture), source);
            Assert.Equal("Resolved Test Mic", source.Description);
        }
        finally
        {
            (source as IDisposable)?.Dispose();
        }
    }
}
