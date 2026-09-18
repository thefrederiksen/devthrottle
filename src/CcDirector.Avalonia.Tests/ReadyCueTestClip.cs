using CcDirector.Avalonia.Voice;
using CcDirector.Core.Audio;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// A clip under construction, in int16 sample units: room noise everywhere, the cue added where the speaker
/// played it (attenuated, as a microphone hears a speaker), and speech-like words where the owner talks.
/// </summary>
internal sealed class ReadyCueTestClip
{
    private const int Rate = MicAudioCapture.SampleRate;
    private const int SamplesPerMs = Rate / 1000;

    private readonly double[] _s;
    /// <param name="noise">Peak level of the uniform room noise, in int16 units.</param>
    public ReadyCueTestClip(int ms, int seed = 7, int noise = 40)
    {
        _s = new double[ms * SamplesPerMs];
        var rng = new Random(seed);
        for (int i = 0; i < _s.Length; i++) _s[i] = rng.Next(-noise, noise + 1);
    }

    /// <param name="playedMs">How much of the cue sounded before playback stopped; the whole cue when negative.</param>
    public ReadyCueTestClip Cue(int atMs, double gain = 0.3, int playedMs = -1)
    {
        var cue = ReadyCue.Synthesize(Rate);
        int length = playedMs < 0 ? cue.Length : Math.Min(cue.Length, playedMs * SamplesPerMs);
        for (int i = 0; i < length; i++) _s[atMs * SamplesPerMs + i] += cue[i] * short.MaxValue * gain;
        return this;
    }

    /// <summary>Voiced, syllable-paced speech stand-in: eight harmonics of a wandering 110-170 Hz pitch.</summary>
    /// <param name="level">Peak of the voiced waveform before the syllable envelope, in int16 units.</param>
    public ReadyCueTestClip Words(int fromMs, int toMs = -1, double level = 6000)
    {
        int start = fromMs * SamplesPerMs, end = toMs < 0 ? _s.Length : toMs * SamplesPerMs;
        double phase = 0;
        for (int i = start; i < end; i++)
        {
            double t = (double)(i - start) / Rate;
            phase += 2 * Math.PI * (140 + 30 * Math.Sin(2 * Math.PI * 3 * t)) / Rate;
            double voiced = 0;
            for (int h = 1; h <= 8; h++) voiced += Math.Sin(h * phase) / h;
            double syllable = 0.2 + 0.8 * Math.Pow(Math.Sin(2 * Math.PI * 4 * t), 2);
            _s[i] += voiced * syllable * level;
        }
        return this;
    }

    public byte[] Pcm()
    {
        var pcm = new byte[_s.Length * 2];
        for (int i = 0; i < _s.Length; i++)
        {
            var v = (short)Math.Clamp(Math.Round(_s[i]), short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)(v & 0xFF);
            pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return pcm;
    }
}
