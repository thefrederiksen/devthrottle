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
}
