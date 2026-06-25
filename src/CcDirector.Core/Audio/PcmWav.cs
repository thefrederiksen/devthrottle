using System.Text;

namespace CcDirector.Core.Audio;

/// <summary>
/// The ONE place that wraps raw PCM in a minimal RIFF/WAV container. Transcription
/// APIs (OpenAI and the OpenAI-compatible proxies) reject raw PCM without a
/// container header, so every batch surface that captures raw PCM - the desktop
/// mic (<c>BatchDictationRecorder</c>), the browser dictation endpoint
/// (<c>DictationEndpoint</c>), and the in-session batch provider
/// (<c>OpenAiTranscriptionProvider</c>) - wraps it here rather than each carrying
/// its own copy of the byte layout.
/// </summary>
public static class PcmWav
{
    /// <summary>
    /// Wrap raw little-endian PCM samples in a RIFF/WAV header. The returned blob is
    /// a complete <c>.wav</c> file the transcription endpoint can decode directly.
    /// </summary>
    /// <param name="pcm">The raw PCM sample bytes (no header).</param>
    /// <param name="sampleRate">Samples per second (e.g. 24000).</param>
    /// <param name="channels">Channel count (e.g. 1 for mono).</param>
    /// <param name="bitsPerSample">Bits per sample (e.g. 16).</param>
    public static byte[] Wrap(byte[] pcm, int sampleRate, int channels, int bitsPerSample)
    {
        if (pcm is null) throw new ArgumentNullException(nameof(pcm));

        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;
        using var ms = new MemoryStream(44 + pcm.Length);
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + pcm.Length);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)bitsPerSample);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(pcm.Length);
        bw.Write(pcm, 0, pcm.Length);
        bw.Flush();
        return ms.ToArray();
    }
}
