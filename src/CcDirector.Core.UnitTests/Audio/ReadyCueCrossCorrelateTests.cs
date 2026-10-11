using CcDirector.Core.Audio;
using Xunit;

namespace CcDirector.Core.UnitTests.Audio;

/// <summary>
/// The ready-cue search computes dot(window, template) at every position through the discrete Fourier
/// transform. These tests hold it to the definition: the plain sum, written out, over signals with the
/// shape the search sees - a quiet room, the cue itself, and loud speech - at every valid position, to a
/// tolerance far below anything the threshold or the match position could feel.
/// </summary>
public sealed class ReadyCueCrossCorrelateTests
{
    private const int Rate = 16000;

    /// <summary>The definition, with no transform anywhere near it.</summary>
    private static double[] SlidingDot(double[] signal, double[] template)
    {
        var result = new double[signal.Length - template.Length + 1];
        for (int i = 0; i < result.Length; i++)
        {
            double sum = 0;
            for (int j = 0; j < template.Length; j++) sum += signal[i + j] * template[j];
            result[i] = sum;
        }
        return result;
    }

    private static double[] RoomNoise(int samples, int peak, int seed)
    {
        var rng = new Random(seed);
        var s = new double[samples];
        for (int i = 0; i < s.Length; i++) s[i] = rng.Next(-peak, peak + 1);
        return s;
    }

    [Theory]
    [InlineData(3200, 3200)]      // one window exactly: the transform length equals the signal length
    [InlineData(5000, 3200)]      // a length that is not a power of two, so the padding matters
    [InlineData(8192, 3200)]
    [InlineData(9600, 100)]       // a short template against a long signal
    public void MatchesTheSlidingDotProduct_AtEveryPosition(int signalSamples, int templateSamples)
    {
        var signal = RoomNoise(signalSamples, 32000, seed: 11);
        var template = RoomNoise(templateSamples, 1000, seed: 12);

        var fast = ReadyCue.CrossCorrelate(signal, template);
        var slow = SlidingDot(signal, template);

        // Full-scale 16-bit samples against a template in the thousands: the sums reach 1e11. A relative
        // tolerance of 1e-9 is a hundred times looser than double arithmetic and a hundred thousand times
        // tighter than anything that could move a score across the 0.5 threshold.
        double scale = Math.Max(1.0, slow.Max(v => Math.Abs(v)));
        for (int i = 0; i < slow.Length; i++)
            Assert.True(Math.Abs(fast[i] - slow[i]) <= 1e-9 * scale,
                $"position {i}: transform {fast[i]:R}, definition {slow[i]:R}");
    }

    [Fact]
    public void TheCueInAQuietRoom_ScoresAndLandsWhereTheDefinitionSays()
    {
        // The search's own inputs: the synthesised cue heard at 30 percent amplitude at 137 ms over room
        // noise, searched over the first 600 ms, exactly as the recorder does it.
        var cue = ReadyCue.Synthesize(Rate);
        var signal = RoomNoise(600 * Rate / 1000, 40, seed: 7);
        const int cueAt = 137 * Rate / 1000;
        for (int i = 0; i < cue.Length; i++) signal[cueAt + i] += cue[i] * short.MaxValue * 0.3;

        var template = (double[])cue.Clone();
        double mean = template.Average();
        double norm = 0;
        for (int j = 0; j < template.Length; j++) { template[j] -= mean; norm += template[j] * template[j]; }
        norm = Math.Sqrt(norm) + 1e-12;
        for (int j = 0; j < template.Length; j++) template[j] /= norm;

        var fast = ReadyCue.CrossCorrelate(signal, template);
        var slow = SlidingDot(signal, template);

        int fastBest = 0, slowBest = 0;
        for (int i = 0; i < slow.Length; i++)
        {
            if (Math.Abs(fast[i]) > Math.Abs(fast[fastBest])) fastBest = i;
            if (Math.Abs(slow[i]) > Math.Abs(slow[slowBest])) slowBest = i;
        }
        Assert.Equal(cueAt, slowBest);
        Assert.Equal(slowBest, fastBest);
        Assert.Equal(slow[slowBest], fast[fastBest], tolerance: 1e-6);
    }

    [Fact]
    public void ATemplateLongerThanTheSignal_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => ReadyCue.CrossCorrelate(new double[10], new double[11]));
        Assert.Equal("template", ex.ParamName);
    }

    [Fact]
    public void Find_TheExactCue_IsFoundAtItsSample_WithAScoreOfOne()
    {
        // Through the public search, end to end: the cue alone, at full amplitude, over silence.
        var cue = ReadyCue.Synthesize(Rate);
        int clipSamples = 600 * Rate / 1000;
        const int cueAt = 211 * Rate / 1000;
        var pcm = new byte[clipSamples * 2];
        for (int i = 0; i < cue.Length; i++)
        {
            short v = (short)Math.Clamp(Math.Round(cue[i] * short.MaxValue), short.MinValue, short.MaxValue);
            pcm[(cueAt + i) * 2] = (byte)(v & 0xFF);
            pcm[(cueAt + i) * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }

        var search = ReadyCue.Find(pcm, Rate, pcm.Length);

        Assert.True(search.Found);
        Assert.Equal(cueAt, search.MatchSample);
        Assert.InRange(search.BestScore, 0.999, 1.0 + 1e-9);
    }
}
