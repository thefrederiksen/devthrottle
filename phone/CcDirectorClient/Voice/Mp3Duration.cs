namespace CcDirectorClient.Voice;

/// <summary>
/// Estimates the playing time of an MP3 byte stream by walking its frame headers
/// (issue #394). The Director's OpenAI tts-1 voice returns MPEG-1 Layer III audio;
/// for a constant-bitrate stream (what tts-1 emits) the duration is simply
/// totalAudioBytes * 8 / bitrate. Rather than trust a single header we read the
/// first valid frame's bitrate + sample rate, which is stable across a CBR clip,
/// and divide the audio byte count by the byte rate.
///
/// This is an ESTIMATE, not a decode: it exists so a log line can say "expected
/// ~8.0s" next to "played 3.1s" and make a truncated playback measurable. It is
/// pure and MAUI/Android-free so it can be unit-tested off-device, where the real
/// MediaPlayer cannot run.
/// </summary>
public static class Mp3Duration
{
    // MPEG-1 Layer III bitrate table (kbps), indexed by the 4-bit bitrate field.
    // Index 0 = "free", 15 = "bad" - both are invalid and treated as no frame.
    private static readonly int[] Mpeg1Layer3BitrateKbps =
        { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };

    // MPEG-1 sample-rate table (Hz), indexed by the 2-bit sample-rate field.
    private static readonly int[] Mpeg1SampleRateHz = { 44100, 48000, 32000, 0 };

    /// <summary>
    /// Estimate the playing time of <paramref name="audio"/>. Returns
    /// <see cref="TimeSpan.Zero"/> for empty input or when no valid MPEG-1 Layer III
    /// frame header can be found (an unexpected format is reported as "unknown
    /// duration" by the caller rather than a wrong number).
    /// </summary>
    public static TimeSpan Estimate(byte[]? audio)
    {
        if (audio is null || audio.Length < 4) return TimeSpan.Zero;

        // Skip an ID3v2 tag if present ("ID3" + version + flags + 4 synch-safe size bytes),
        // so the first frame search starts at the audio, not the metadata.
        int start = 0;
        if (audio.Length >= 10 && audio[0] == 0x49 && audio[1] == 0x44 && audio[2] == 0x33) // "ID3"
        {
            int tagSize = (audio[6] & 0x7F) << 21 | (audio[7] & 0x7F) << 14
                          | (audio[8] & 0x7F) << 7 | (audio[9] & 0x7F);
            start = 10 + tagSize;
            if (start >= audio.Length) return TimeSpan.Zero;
        }

        for (int i = start; i + 3 < audio.Length; i++)
        {
            // Frame sync: 11 set bits (0xFF then top 3 bits of next byte).
            if (audio[i] != 0xFF || (audio[i + 1] & 0xE0) != 0xE0) continue;

            // MPEG Audio version (bits 4-3 of byte 1): 0b11 = MPEG-1. Layer (bits 2-1):
            // 0b01 = Layer III. We only model the tts-1 case (MPEG-1 Layer III).
            int versionBits = (audio[i + 1] >> 3) & 0x03;
            int layerBits = (audio[i + 1] >> 1) & 0x03;
            if (versionBits != 0x03 || layerBits != 0x01) continue;

            int bitrateIndex = (audio[i + 2] >> 4) & 0x0F;
            int sampleRateIndex = (audio[i + 2] >> 2) & 0x03;
            int bitrateKbps = Mpeg1Layer3BitrateKbps[bitrateIndex];
            int sampleRateHz = Mpeg1SampleRateHz[sampleRateIndex];
            if (bitrateKbps == 0 || sampleRateHz == 0) continue; // free/bad - keep scanning

            // The audio payload starts at this first frame; everything from here to the
            // end is sound. For CBR, duration = audioBytes * 8 / bitrate(bits per second).
            long audioBytes = audio.Length - i;
            double seconds = audioBytes * 8.0 / (bitrateKbps * 1000.0);
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.Zero;
    }
}
