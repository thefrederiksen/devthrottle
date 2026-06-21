using System.Text;
using CcDirector.Avalonia.Voice;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Unit tests for <see cref="WavWriter.WrapPcm16"/>, the RIFF/WAV container the
/// whole-audio desktop dictation path (issue #589) wraps the captured PCM in
/// before the single batch transcription upload. A malformed header would make
/// the transcription server reject the whole clip, so the header layout, the
/// declared format, and the payload round-trip are all asserted.
/// </summary>
public class WavWriterTests
{
    // 24 kHz / mono / 16-bit is the desktop mic capture format (MicAudioCapture).
    private const int SampleRate = 24_000;
    private const int Channels = 1;
    private const int Bits = 16;

    [Fact]
    public void WrapPcm16_EmptyPcm_ProducesHeaderOnlyFortyFourBytes()
    {
        // Arrange
        var pcm = Array.Empty<byte>();

        // Act
        var wav = WavWriter.WrapPcm16(pcm, SampleRate, Channels, Bits);

        // Assert
        Assert.Equal(44, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
    }

    [Fact]
    public void WrapPcm16_WithPcm_WritesRiffWaveFmtDataChunks()
    {
        // Arrange - 100 samples of arbitrary PCM16 = 200 bytes.
        var pcm = new byte[200];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i & 0xFF);

        // Act
        var wav = WavWriter.WrapPcm16(pcm, SampleRate, Channels, Bits);

        // Assert - chunk tags at their fixed offsets.
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));

        // RIFF chunk size = 36 + payload.
        Assert.Equal(36 + pcm.Length, BitConverter.ToInt32(wav, 4));
        // data chunk size = payload.
        Assert.Equal(pcm.Length, BitConverter.ToInt32(wav, 40));
        // Total length = 44-byte header + payload.
        Assert.Equal(44 + pcm.Length, wav.Length);
    }

    [Fact]
    public void WrapPcm16_DeclaresCaptureFormat()
    {
        // Arrange
        var pcm = new byte[64];

        // Act
        var wav = WavWriter.WrapPcm16(pcm, SampleRate, Channels, Bits);

        // Assert - the fmt chunk fields the transcription server reads.
        Assert.Equal(16, BitConverter.ToInt32(wav, 16));            // fmt chunk body size
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));             // audio format = PCM
        Assert.Equal(Channels, BitConverter.ToInt16(wav, 22));      // channels
        Assert.Equal(SampleRate, BitConverter.ToInt32(wav, 24));    // sample rate
        Assert.Equal(SampleRate * Channels * Bits / 8, BitConverter.ToInt32(wav, 28)); // byte rate
        Assert.Equal(Channels * Bits / 8, BitConverter.ToInt16(wav, 32)); // block align
        Assert.Equal(Bits, BitConverter.ToInt16(wav, 34));          // bits per sample
    }

    [Fact]
    public void WrapPcm16_RoundTripsPayloadBytes()
    {
        // Arrange
        var pcm = new byte[300];
        new Random(1234).NextBytes(pcm);

        // Act
        var wav = WavWriter.WrapPcm16(pcm, SampleRate, Channels, Bits);

        // Assert - the audio payload follows the 44-byte header verbatim.
        var payload = wav.AsSpan(44).ToArray();
        Assert.Equal(pcm, payload);
    }

    [Fact]
    public void WrapPcm16_NullPcm_Throws()
    {
        byte[]? pcm = null;
        Assert.Throws<ArgumentNullException>(() => WavWriter.WrapPcm16(pcm, SampleRate, Channels, Bits));
    }
}
