using CcDirectorClient.Voice;
using Xunit;

namespace CcDirectorClient.Tests;

/// <summary>
/// Issue #394: the playback log compares the duration a clip was ESTIMATED to run with
/// the time it actually played, which is what makes a truncated playback measurable.
/// The estimate comes from <see cref="Mp3Duration"/>, which must derive a CBR MP3's
/// length from its frame headers off-device (the real Android decoder cannot run here).
/// </summary>
public class Mp3DurationTests
{
    // A valid MPEG-1 Layer III frame header for the given bitrate/sample-rate fields.
    // Byte 0: 0xFF sync. Byte 1: 0xFB = sync(111) + MPEG-1(11) + Layer III(01) + no-CRC(1).
    // Byte 2: bitrateIndex<<4 | sampleRateIndex<<2. Byte 3: 0x00 (channel/flags, unused here).
    private static byte[] FrameHeader(int bitrateIndex, int sampleRateIndex)
        => new byte[] { 0xFF, 0xFB, (byte)((bitrateIndex << 4) | (sampleRateIndex << 2)), 0x00 };

    /// <summary>Build a synthetic CBR MP3: one real frame header followed by padding so the
    /// stream is a known total length. The estimator treats the whole stream from the first
    /// frame as audio, which is the CBR duration = totalBytes * 8 / bitrate.</summary>
    private static byte[] CbrClip(int bitrateIndex, int sampleRateIndex, int totalBytes)
    {
        var clip = new byte[totalBytes];
        var header = FrameHeader(bitrateIndex, sampleRateIndex);
        System.Array.Copy(header, clip, header.Length);
        return clip;
    }

    [Fact]
    public void Estimate_128kbpsClip_ReturnsBytesTimesEightOverBitrate()
    {
        // 128 kbps = bitrate index 9. 16000 bytes -> 16000*8 / 128000 = 1.0 second.
        var clip = CbrClip(bitrateIndex: 9, sampleRateIndex: 0, totalBytes: 16_000);

        var d = Mp3Duration.Estimate(clip);

        Assert.Equal(1.0, d.TotalSeconds, precision: 2);
    }

    [Fact]
    public void Estimate_LongerClip_ScalesWithByteCount()
    {
        // Same 128 kbps, 8x the bytes -> 8x the duration (8.0 s). This is the "expected ~8s"
        // a clean finish is measured against in the playback log.
        var clip = CbrClip(bitrateIndex: 9, sampleRateIndex: 0, totalBytes: 128_000);

        var d = Mp3Duration.Estimate(clip);

        Assert.Equal(8.0, d.TotalSeconds, precision: 2);
    }

    [Fact]
    public void Estimate_64kbpsClip_IsTwiceAsLongAsAt128kbps()
    {
        // 64 kbps = bitrate index 5. Half the bitrate -> twice the duration for the same bytes.
        var clip = CbrClip(bitrateIndex: 5, sampleRateIndex: 0, totalBytes: 16_000);

        var d = Mp3Duration.Estimate(clip);

        Assert.Equal(2.0, d.TotalSeconds, precision: 2);
    }

    [Fact]
    public void Estimate_SkipsLeadingNonFrameBytes()
    {
        // Garbage before the first frame must not derail the search; the duration is computed
        // from the first valid header onward.
        var clip = CbrClip(bitrateIndex: 9, sampleRateIndex: 0, totalBytes: 16_000);
        var withJunk = new byte[5 + clip.Length];
        for (int i = 0; i < 5; i++) withJunk[i] = 0x55; // arbitrary non-sync bytes
        System.Array.Copy(clip, 0, withJunk, 5, clip.Length);

        var d = Mp3Duration.Estimate(withJunk);

        // The frame starts at offset 5, so audio bytes = 16000, duration = 1.0 s.
        Assert.Equal(1.0, d.TotalSeconds, precision: 2);
    }

    [Fact]
    public void Estimate_SkipsId3v2Tag()
    {
        // "ID3" + version(2) + flags(1) + 4 synch-safe size bytes (size = 8), then 8 tag bytes,
        // then the real frame. The estimator must skip the tag and find the frame after it.
        var frame = CbrClip(bitrateIndex: 9, sampleRateIndex: 0, totalBytes: 16_000);
        var id3 = new byte[10 + 8];
        id3[0] = 0x49; id3[1] = 0x44; id3[2] = 0x33; // "ID3"
        id3[9] = 0x08; // synch-safe size = 8 -> tag body is 8 bytes
        var clip = new byte[id3.Length + frame.Length];
        System.Array.Copy(id3, clip, id3.Length);
        System.Array.Copy(frame, 0, clip, id3.Length, frame.Length);

        var d = Mp3Duration.Estimate(clip);

        Assert.Equal(1.0, d.TotalSeconds, precision: 2);
    }

    [Fact]
    public void Estimate_NullOrEmpty_ReturnsZero()
    {
        Assert.Equal(System.TimeSpan.Zero, Mp3Duration.Estimate(null));
        Assert.Equal(System.TimeSpan.Zero, Mp3Duration.Estimate(System.Array.Empty<byte>()));
    }

    [Fact]
    public void Estimate_NoValidFrameHeader_ReturnsZero()
    {
        // A buffer with no MPEG-1 Layer III sync is reported as unknown (zero), never a wrong number.
        var notMp3 = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77 };

        Assert.Equal(System.TimeSpan.Zero, Mp3Duration.Estimate(notMp3));
    }
}
