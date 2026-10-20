using CcDirector.Avalonia.Voice;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// macOS desktop dictation: the two things that are different about this platform.
///
/// One - dictation IS offered on a Mac now. Capture runs on AudioToolbox's AudioQueue, which is part
/// of the operating system, so unlike Linux there is no recorder for the user to install and the only
/// thing that can be missing is a microphone to record from.
///
/// Two - a microphone macOS has not granted does NOT fail to open. The queue starts, runs at exactly
/// the right byte rate, and every sample it delivers is zero, so the empty-capture gate (issue #586)
/// never sees anything wrong and the user is handed an empty transcript with no explanation. These
/// tests pin the silence gate that refuses such a recording and names the real cause instead.
///
/// Driven through the injected <see cref="IAudioSource"/> seam, so they prove the gate on every
/// platform the suite runs on, with no real microphone.
/// </summary>
public sealed class MacMicCaptureTests
{
    /// <summary>
    /// A microphone that reports whether it heard anything, as <see cref="MacMicAudioCapture"/> does.
    /// What it emits and what it claims to have heard are set separately, so a test can produce the
    /// exact shape a refused macOS microphone produces: a full recording's worth of zero bytes.
    /// </summary>
    private sealed class SilenceReportingMic : IAudioSource, IAudioSilenceReport
    {
        public event Action<byte[]>? OnAudioChunk;
        public string Description => "Yealink UH36";
        public bool SawSound { get; init; }
        public string SilentCaptureExplanation =>
            "Open System Settings > Privacy & Security > Microphone and switch Director on, check the "
            + "microphone's own mute button, then dictate again.";

        public void Start() { }
        public void Stop() { }
        public void Emit(byte[] chunk) => OnAudioChunk?.Invoke(chunk);
        public Task StopAsync(TimeSpan drainTimeout) => Task.CompletedTask;
    }

    /// <summary>A microphone that says nothing about silence, as the Windows and Linux captures do.</summary>
    private sealed class PlainMic : IAudioSource
    {
        public event Action<byte[]>? OnAudioChunk;
        public string Description => "Fake Test Microphone";
        public void Start() { }
        public void Stop() { }
        public void Emit(byte[] chunk) => OnAudioChunk?.Invoke(chunk);
        public Task StopAsync(TimeSpan drainTimeout) => Task.CompletedTask;
    }

    private static BatchDictationRecorder NewRecorder(IAudioSource mic, Action<byte[]>? onTranscribe = null)
        => new(new AgentOptions(), _ => mic,
            (pcm, _, _) =>
            {
                onTranscribe?.Invoke(pcm);
                return Task.FromResult(new DictationResult("raw text", "clean text", 0));
            });

    [Fact]
    public void CaptureUnavailableMessage_OnMacWithAnInputDevice_OffersDictation()
    {
        Assert.Null(SpeakDialog.CaptureUnavailableMessage(
            isWindows: false, isLinux: false, linuxCaptureToolsPresent: false,
            isMacOS: true, macHasInputDevice: true));
    }

    [Fact]
    public void CaptureUnavailableMessage_OnMacWithNoInputDevice_SaysToConnectOne()
    {
        // The Mac mini has no built-in microphone, so this is the ordinary case there, not an edge.
        var message = SpeakDialog.CaptureUnavailableMessage(
            isWindows: false, isLinux: false, linuxCaptureToolsPresent: false,
            isMacOS: true, macHasInputDevice: false);

        Assert.NotNull(message);
        Assert.Contains("Connect one", message);
        Assert.Contains("System Settings", message);
        // Nothing to install on this platform: that advice belongs to Linux and would send the user nowhere.
        Assert.DoesNotContain("pw-record", message);
        Assert.DoesNotContain("arecord", message);
    }

    [Fact]
    public async Task TranscribeAsync_EverySampleSilent_RefusesAndNamesTheDeviceAndWhereToGrantAccess()
    {
        // Arrange - the exact shape of a refused macOS microphone: bytes at the right rate, all zero.
        var mic = new SilenceReportingMic { SawSound = false };
        var transcribeCalled = false;
        await using var recorder = NewRecorder(mic, _ => transcribeCalled = true);
        await recorder.StartAsync();
        mic.Emit(new byte[4800]);

        // Act
        var thrown = await Assert.ThrowsAsync<SilentCaptureException>(() => recorder.TranscribeAsync());

        // Assert - the clip never leaves the machine, and the failure says what to go and change.
        Assert.False(transcribeCalled);
        Assert.Equal("Yealink UH36", thrown.DeviceDescription);
        Assert.Contains("Yealink UH36", thrown.Message);
        Assert.Contains("System Settings > Privacy & Security > Microphone", thrown.Message);
    }

    [Fact]
    public async Task StopAndGetWavAsync_EverySampleSilent_RefusesToo()
    {
        // Send goes through this path, not TranscribeAsync, so the gate has to hold on both.
        var mic = new SilenceReportingMic { SawSound = false };
        await using var recorder = NewRecorder(mic);
        await recorder.StartAsync();
        mic.Emit(new byte[4800]);

        await Assert.ThrowsAsync<SilentCaptureException>(() => recorder.StopAndGetWavAsync());
    }

    [Fact]
    public async Task TranscribeAsync_MicrophoneHeardSomething_IsNotRefused()
    {
        // A quiet passage inside a recording that DID hear sound is not silence; it must transcribe.
        var mic = new SilenceReportingMic { SawSound = true };
        byte[]? transcribed = null;
        await using var recorder = NewRecorder(mic, pcm => transcribed = pcm);
        await recorder.StartAsync();
        mic.Emit(new byte[4800]);

        var result = await recorder.TranscribeAsync();

        Assert.Equal("clean text", result.CleanedTranscript);
        Assert.NotNull(transcribed);
        Assert.Equal(4800, transcribed.Length);
    }

    [Fact]
    public async Task TranscribeAsync_SourceThatDoesNotReportSilence_IsUnchanged()
    {
        // Windows and Linux do not watch the samples, and this change must not start refusing their
        // recordings: an all-zero clip from a source that reports nothing still goes to transcription.
        var mic = new PlainMic();
        byte[]? transcribed = null;
        await using var recorder = NewRecorder(mic, pcm => transcribed = pcm);
        await recorder.StartAsync();
        mic.Emit(new byte[4800]);

        var result = await recorder.TranscribeAsync();

        Assert.Equal("clean text", result.CleanedTranscript);
        Assert.NotNull(transcribed);
    }

    [Fact]
    public async Task NoBytesAtAll_StillTheEmptyCaptureGate_NotTheSilenceGate()
    {
        // The two gates mean different things and are reported differently (the empty one is the
        // speaker's own outcome and stays quiet; the silent one is the machine refusing the
        // microphone and is shown). A capture that delivered nothing must keep the first.
        var mic = new SilenceReportingMic { SawSound = false };
        await using var recorder = NewRecorder(mic);
        await recorder.StartAsync();

        await Assert.ThrowsAsync<NoAudioCapturedException>(() => recorder.TranscribeAsync());
    }

    [Fact]
    public void MacCapture_ReportsSilence_SoTheGateCanFire()
    {
        // The gate is opt-in per source. If MacMicAudioCapture ever stops implementing this, the gate
        // goes quiet on the one platform that needs it and nothing else in the suite would notice.
        Assert.True(typeof(IAudioSilenceReport).IsAssignableFrom(typeof(MacMicAudioCapture)));
    }
}
