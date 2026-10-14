using CcDirector.Avalonia.Voice;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Desktop dictation on Linux. The Speak dialog used to refuse outright off Windows ("Desktop dictation is only
/// available on Windows."), because capture was NAudio's winmm and Core Audio. Linux now captures through the
/// sound system's own recorder, read as raw PCM from its standard output.
///
/// Nothing here starts a process or needs a microphone: the capture reads a recorder the test feeds by hand, so
/// every test runs on every machine.
/// </summary>
public sealed class LinuxMicCaptureTests
{
    // Shaped like pw-dump's output: two inputs, a sink (never a microphone), a playback stream, and the
    // metadata object that names the default input.
    private const string PipeWireDump = """
        [
          { "id": 53, "type": "PipeWire:Interface:Node", "info": { "props": {
              "media.class": "Audio/Source", "node.name": "alsa_input.pci-0000_08_00.3.analog-stereo",
              "node.description": "Built-in Audio Analog Stereo", "object.serial": 58 } } },
          { "id": 55, "type": "PipeWire:Interface:Node", "info": { "props": {
              "media.class": "Audio/Source", "node.name": "alsa_input.usb-Yealink_UH36-00.analog-stereo",
              "node.description": "Yealink UH36 Analog Stereo", "object.serial": 55 } } },
          { "id": 60, "type": "PipeWire:Interface:Node", "info": { "props": {
              "media.class": "Audio/Sink", "node.name": "alsa_output.usb-Yealink_UH36-00.analog-stereo",
              "node.description": "Yealink UH36 Analog Stereo", "object.serial": 60 } } },
          { "id": 71, "type": "PipeWire:Interface:Node", "info": { "props": {
              "media.class": "Stream/Output/Audio", "node.name": "Firefox", "object.serial": 71 } } },
          { "id": 40, "type": "PipeWire:Interface:Metadata", "props": { "metadata.name": "default" },
            "metadata": [
              { "subject": 0, "key": "default.audio.sink", "type": "Spa:String:JSON", "value": { "name": "alsa_output.usb-Yealink_UH36-00.analog-stereo" } },
              { "subject": 0, "key": "default.audio.source", "type": "Spa:String:JSON", "value": { "name": "alsa_input.usb-Yealink_UH36-00.analog-stereo" } }
            ] }
        ]
        """;

    [Fact]
    public void ParsePipeWireDump_ListsOnlyAudioInputs_AndNamesTheDefault()
    {
        var snapshot = LinuxMicDevices.ParsePipeWireDump(PipeWireDump);

        Assert.Equal(LinuxAudioBackend.PipeWire, snapshot.Backend);
        Assert.Equal(
            [
                new LinuxMicSource(58, "alsa_input.pci-0000_08_00.3.analog-stereo", "Built-in Audio Analog Stereo"),
                new LinuxMicSource(55, "alsa_input.usb-Yealink_UH36-00.analog-stereo", "Yealink UH36 Analog Stereo"),
            ],
            snapshot.Sources);
        Assert.Equal("alsa_input.usb-Yealink_UH36-00.analog-stereo", snapshot.DefaultNodeName);
        Assert.Equal("Yealink UH36 Analog Stereo", snapshot.DefaultDescription);
    }

    [Fact]
    public void ParsePipeWireDump_WithNoAudioInput_ListsNothing()
    {
        // PipeWire running for screen sharing only, with another sound system owning audio.
        var snapshot = LinuxMicDevices.ParsePipeWireDump("""
            [ { "id": 0, "type": "PipeWire:Interface:Core", "info": { "props": { "core.name": "pipewire-0" } } } ]
            """);

        Assert.Empty(snapshot.Sources);
        Assert.Null(snapshot.DefaultNodeName);
        Assert.Null(snapshot.DefaultDescription);
    }

    [Fact]
    public void ParsePipeWireDump_ReadsTheExtraArrayPrintedWhenAClientLeftDuringTheDump()
    {
        // The real tail of a dump taken on devlinux while a second pw-dump ran (78 of 200 such dumps ended this
        // way): the other client left, and pw-dump reported it in a second array. Parsed as one JSON value this
        // failed the Speak dialog with "'[' is invalid after a single JSON value".
        var snapshot = LinuxMicDevices.ParsePipeWireDump(PipeWireDump + """

            [
              {
                "id": 102,
                "info": null
              }
            ]

            """);

        Assert.Equal(
            [
                new LinuxMicSource(58, "alsa_input.pci-0000_08_00.3.analog-stereo", "Built-in Audio Analog Stereo"),
                new LinuxMicSource(55, "alsa_input.usb-Yealink_UH36-00.analog-stereo", "Yealink UH36 Analog Stereo"),
            ],
            snapshot.Sources);
        Assert.Equal("alsa_input.usb-Yealink_UH36-00.analog-stereo", snapshot.DefaultNodeName);
    }

    [Fact]
    public void ParsePipeWireDump_DropsAnInputTheExtraArrayRemoves()
    {
        // The headset was unplugged while the dump ran.
        var snapshot = LinuxMicDevices.ParsePipeWireDump(PipeWireDump + """
            [ { "id": 55, "info": null } ]
            """);

        Assert.Equal(
            [new LinuxMicSource(58, "alsa_input.pci-0000_08_00.3.analog-stereo", "Built-in Audio Analog Stereo")],
            snapshot.Sources);
    }

    [Fact]
    public void ParsePipeWireDump_KeepsTheDefaultWhenALaterArrayChangesAnotherMetadataKey()
    {
        // pw-dump reprints a metadata object with only the entries that changed: here the default output moved,
        // and the default input, which did not change, is not in the later array at all.
        var snapshot = LinuxMicDevices.ParsePipeWireDump(PipeWireDump + """
            [ { "id": 40, "type": "PipeWire:Interface:Metadata", "props": { "metadata.name": "default" },
                "metadata": [
                  { "subject": 0, "key": "default.audio.sink", "type": "Spa:String:JSON", "value": { "name": "alsa_output.pci-0000_08_00.3.analog-stereo" } }
                ] } ]
            """);

        Assert.Equal("alsa_input.usb-Yealink_UH36-00.analog-stereo", snapshot.DefaultNodeName);
        Assert.Equal("Yealink UH36 Analog Stereo", snapshot.DefaultDescription);
    }

    [Fact]
    public void ParsePipeWireDump_TakesTheDefaultFromALaterArray()
    {
        // The default input was switched to the built-in microphone while the dump ran; pw-dump prints only
        // that entry.
        var snapshot = LinuxMicDevices.ParsePipeWireDump(PipeWireDump + """
            [ { "id": 40, "type": "PipeWire:Interface:Metadata", "props": { "metadata.name": "default" },
                "metadata": [
                  { "subject": 0, "key": "default.audio.source", "type": "Spa:String:JSON", "value": { "name": "alsa_input.pci-0000_08_00.3.analog-stereo" } }
                ] } ]
            """);

        Assert.Equal(2, snapshot.Sources.Count);
        Assert.Equal("alsa_input.pci-0000_08_00.3.analog-stereo", snapshot.DefaultNodeName);
        Assert.Equal("Built-in Audio Analog Stereo", snapshot.DefaultDescription);
    }

    [Fact]
    public void ParsePipeWireDump_ListsAnInputReprintedInALaterArrayOnce_WithItsNewDetails()
    {
        // pw-dump reprints a changed node in full; it replaces the earlier one rather than listing the input twice.
        var snapshot = LinuxMicDevices.ParsePipeWireDump(PipeWireDump + """
            [ { "id": 55, "type": "PipeWire:Interface:Node", "info": { "props": {
                  "media.class": "Audio/Source", "node.name": "alsa_input.usb-Yealink_UH36-00.analog-stereo",
                  "node.description": "Yealink UH36 Headset", "object.serial": 55 } } } ]
            """);

        Assert.Equal(
            [
                new LinuxMicSource(58, "alsa_input.pci-0000_08_00.3.analog-stereo", "Built-in Audio Analog Stereo"),
                new LinuxMicSource(55, "alsa_input.usb-Yealink_UH36-00.analog-stereo", "Yealink UH36 Headset"),
            ],
            snapshot.Sources);
    }

    [Fact]
    public void ParsePipeWireDump_ReadsARemovalPrintedBeforeTheFullArray()
    {
        // pw-dump prints a removal the moment it happens, which can be before the full array.
        var snapshot = LinuxMicDevices.ParsePipeWireDump("""
            [ { "id": 97, "info": null } ]
            """ + PipeWireDump);

        Assert.Equal(2, snapshot.Sources.Count);
        Assert.Equal("alsa_input.usb-Yealink_UH36-00.analog-stereo", snapshot.DefaultNodeName);
    }

    [Fact]
    public void ParsePipeWireDump_ListsAnInputWhoseIdWasRemovedAndReused()
    {
        // PipeWire hands a freed id straight to the next object: the headset left, and id 55 came back as a webcam.
        var snapshot = LinuxMicDevices.ParsePipeWireDump(PipeWireDump + """
            [ { "id": 55, "info": null } ]
            [ { "id": 55, "type": "PipeWire:Interface:Node", "info": { "props": {
                  "media.class": "Audio/Source", "node.name": "alsa_input.usb-C920-00.analog-stereo",
                  "node.description": "HD Pro Webcam C920 Analog Stereo", "object.serial": 91 } } } ]
            """);

        Assert.Equal(
            [
                new LinuxMicSource(58, "alsa_input.pci-0000_08_00.3.analog-stereo", "Built-in Audio Analog Stereo"),
                new LinuxMicSource(91, "alsa_input.usb-C920-00.analog-stereo", "HD Pro Webcam C920 Analog Stereo"),
            ],
            snapshot.Sources);
    }

    [Fact]
    public void ParsePipeWireDump_ListsAnInputAddedInALaterArray()
    {
        var snapshot = LinuxMicDevices.ParsePipeWireDump(PipeWireDump + """
            [ { "id": 90, "type": "PipeWire:Interface:Node", "info": { "props": {
                  "media.class": "Audio/Source", "node.name": "alsa_input.usb-C920-00.analog-stereo",
                  "node.description": "HD Pro Webcam C920 Analog Stereo", "object.serial": 90 } } } ]
            """);

        Assert.Equal(
            [
                new LinuxMicSource(58, "alsa_input.pci-0000_08_00.3.analog-stereo", "Built-in Audio Analog Stereo"),
                new LinuxMicSource(55, "alsa_input.usb-Yealink_UH36-00.analog-stereo", "Yealink UH36 Analog Stereo"),
                new LinuxMicSource(90, "alsa_input.usb-C920-00.analog-stereo", "HD Pro Webcam C920 Analog Stereo"),
            ],
            snapshot.Sources);
    }

    [Fact]
    public void CaptureUnavailableMessage_OffersDictationOnWindowsAndOnLinuxWithARecorder()
    {
        Assert.Null(SpeakDialog.CaptureUnavailableMessage(isWindows: true, isLinux: false, linuxCaptureToolsPresent: false));
        Assert.Null(SpeakDialog.CaptureUnavailableMessage(isWindows: false, isLinux: true, linuxCaptureToolsPresent: true));
    }

    [Fact]
    public void CaptureUnavailableMessage_OnLinuxWithNoRecorder_SaysWhatToInstall()
    {
        var message = SpeakDialog.CaptureUnavailableMessage(isWindows: false, isLinux: true, linuxCaptureToolsPresent: false);

        Assert.NotNull(message);
        Assert.Contains("pw-record", message);
        Assert.Contains("arecord", message);
    }

    [Fact]
    public void CaptureUnavailableMessage_OnMac_SaysNotAvailableWithoutBlamingWindows()
    {
        var message = SpeakDialog.CaptureUnavailableMessage(isWindows: false, isLinux: false, linuxCaptureToolsPresent: false);

        Assert.NotNull(message);
        Assert.DoesNotContain("only available on Windows", message);
        Assert.Contains("Linux", message);
    }

    [Fact]
    public void PipeWireArguments_AskForTheCaptureFormat_AndTargetOnlyAPickedDevice()
    {
        var defaultDevice = LinuxMicAudioCapture.PipeWireArguments(nodeName: null, rawFlag: false);
        var picked = LinuxMicAudioCapture.PipeWireArguments("alsa_input.usb-Yealink_UH36-00.analog-stereo", rawFlag: true);

        Assert.Equal(["--rate", "24000", "--channels", "1", "--format", "s16", "--latency", "50ms", "-"], defaultDevice);
        Assert.Equal(
            ["--rate", "24000", "--channels", "1", "--format", "s16", "--latency", "50ms", "--raw",
                "--target", "alsa_input.usb-Yealink_UH36-00.analog-stereo", "-"],
            picked);
    }

    [Fact]
    public async Task Capture_DeliversEveryByteTheRecorderWrote_InOrder_InFiftyMillisecondChunks()
    {
        // 12,001 bytes written in ragged pieces: five full 50 ms chunks, and one lone byte that is half a sample.
        var written = Enumerable.Range(0, 12_001).Select(i => (byte)(i % 251)).ToArray();
        var recorder = new FakeRecorder();
        using var capture = new LinuxMicAudioCapture(() => recorder, "test microphone");
        var received = new MemoryStream();
        var chunkSizes = new List<int>();
        var bandFrames = 0;
        capture.OnAudioChunk += chunk => { received.Write(chunk); chunkSizes.Add(chunk.Length); };
        capture.OnAudioBands += bands =>
        {
            Assert.Equal(MicAudioCapture.BandCount, bands.Length);
            bandFrames++;
        };

        capture.Start();
        for (int offset = 0; offset < written.Length; offset += 777)
            recorder.Write(written.AsSpan(offset, Math.Min(777, written.Length - offset)).ToArray());
        await capture.StopAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(written.AsSpan(0, 12_000).ToArray(), received.ToArray());
        Assert.Equal([2400, 2400, 2400, 2400, 2400], chunkSizes);
        Assert.Equal(5, bandFrames);
        Assert.Equal(12_000, capture.GetCaptureHealth().CapturedBytes);
    }

    [Fact]
    public async Task StopAsync_ReturnsOnlyAfterTheTailTheRecorderFlushedOnInterrupt()
    {
        // A recorder holding its last audio until it is interrupted, the way a real one holds a part-filled buffer.
        var recorder = new FakeRecorder { FlushOnInterrupt = "TAIL"u8.ToArray() };
        using var capture = new LinuxMicAudioCapture(() => recorder, "test microphone");
        var received = new MemoryStream();
        capture.OnAudioChunk += chunk => received.Write(chunk);

        capture.Start();
        recorder.Write("HEAD"u8.ToArray());
        await capture.StopAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("HEADTAIL", System.Text.Encoding.ASCII.GetString(received.ToArray()));
        Assert.False(recorder.Killed);
    }

    [Fact]
    public async Task StopAsync_WithARecorderThatIgnoresTheInterrupt_GivesUpAtTheTimeoutAndKillsIt()
    {
        var recorder = new FakeRecorder { IgnoresInterrupt = true };
        using var capture = new LinuxMicAudioCapture(() => recorder, "test microphone");

        capture.Start();
        await capture.StopAsync(TimeSpan.FromMilliseconds(50));

        Assert.True(recorder.Interrupted);
        Assert.True(recorder.Killed);
    }

    [Fact]
    public void Construction_StartsNoRecorder_AndDisposeOfACaptureThatNeverStartedDoesNotThrow()
    {
        var started = false;
        var capture = new LinuxMicAudioCapture(() => { started = true; return new FakeRecorder(); }, "test microphone");

        capture.Dispose();
        capture.Dispose();

        Assert.False(started);
    }

    [Fact]
    public void Start_WhenTheRecorderCannotStart_FailsTheStart()
    {
        using var capture = new LinuxMicAudioCapture(
            () => throw new InvalidOperationException("The selected microphone is no longer connected."), "test microphone");

        var ex = Assert.Throws<InvalidOperationException>(capture.Start);

        Assert.Equal("The selected microphone is no longer connected.", ex.Message);
    }

    [Fact]
    public async Task Start_AfterAStop_IsRefused()
    {
        using var capture = new LinuxMicAudioCapture(() => new FakeRecorder(), "test microphone");
        capture.Start();
        await capture.StopAsync(TimeSpan.FromSeconds(30));

        Assert.Throws<InvalidOperationException>(capture.Start);
    }

    /// <summary>A recorder whose output the test writes by hand; no process, no clock.</summary>
    private sealed class FakeRecorder : ILinuxRecorder
    {
        private readonly FeedStream _output = new();

        public byte[]? FlushOnInterrupt { get; init; }
        public bool IgnoresInterrupt { get; init; }
        public bool Interrupted { get; private set; }
        public bool Killed { get; private set; }

        public string Name => "fake recorder";
        public Stream Output => _output;
        public string? LastErrorLine => null;

        public void Write(byte[] bytes) => _output.Feed(bytes);

        public void Interrupt()
        {
            Interrupted = true;
            if (IgnoresInterrupt) return;
            if (FlushOnInterrupt is not null) _output.Feed(FlushOnInterrupt);
            _output.End();
        }

        public void Kill()
        {
            Killed = true;
            _output.End();
        }

        public void Dispose() { }
    }

    /// <summary>A read-only stream that blocks until bytes are fed and returns 0 once ended and drained.</summary>
    private sealed class FeedStream : Stream
    {
        private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _pieces = new();
        private byte[] _current = [];
        private int _position;

        public void Feed(byte[] bytes) => _pieces.Add(bytes);

        public void End()
        {
            if (!_pieces.IsAddingCompleted) _pieces.CompleteAdding();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (_position == _current.Length)
            {
                if (!_pieces.TryTake(out var next, Timeout.Infinite)) return 0;
                _current = next;
                _position = 0;
            }
            int take = Math.Min(count, _current.Length - _position);
            Buffer.BlockCopy(_current, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
