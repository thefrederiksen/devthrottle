namespace CcDirector.Core.Audio;

/// <summary>
/// The desktop dictation "ready" cue: the ONE generator for its waveform, and the search that finds it in
/// captured audio (issue #2925, mission phase 6 ruling K1).
///
/// The Speak dialog plays the cue while the microphone is open, the microphone records it, and the speech
/// model writes it down as an invented opening word. The cue is removed from the audio sent for
/// transcription by FINDING it - normalised cross-correlation of the exact synthesised cue against the
/// start of the capture - and writing zeros over only those samples. Five inspections showed that placing
/// it by clocks cannot be made right: every clock involved (capture callbacks, dropped buffers, the playback
/// report, output buffering) has unmeasured slack, and slack either leaves cue or erases words. The audio
/// itself has no such slack.
///
/// The player (<c>DesktopAudioCue</c>) and this search share <see cref="Synthesize"/>, so the template is
/// the cue that was played, sample for sample, at whatever rate it is built for. The search is the same one
/// the research used (<c>cue_scan.py</c> in the internal repository): template made zero-mean and unit-norm,
/// score = |dot(window, template)| / norm(window), threshold <see cref="FoundScore"/>.
///
/// The dot product at every position is computed through the discrete Fourier transform
/// (<see cref="CrossCorrelate"/>), not by sliding the template along the clip. The sliding form is a
/// 4,800-sample template against about 32,000 positions at the desktop's 24 kHz capture rate - over 150
/// million multiply-adds per dictation - and whether that takes tens of milliseconds or the better part of a
/// second depends entirely on the just-in-time compiler vectorising the inner loop, which it does not do for a
/// Debug build: the test suites paid the slow case on every recorder test. The transform form does the same
/// arithmetic in about a million operations and is fast in any build.
/// </summary>
public static class ReadyCue
{
    /// <summary>Length of the cue.</summary>
    public const double DurationSeconds = 0.20;

    /// <summary>A match scoring at least this is the cue. The research's value; not tuned.</summary>
    public const double FoundScore = 0.5;

    /// <summary>How far after the capture position at first audio the search reaches. The cue is played when
    /// the first audio arrives, so it starts at or after that position.</summary>
    public const int SearchAfterFirstAudioMs = 1500;

    /// <summary>Blanked before the match start, for the match's own sample-level placement.</summary>
    public const int LeadInMs = 10;

    /// <summary>Blanked after the match end: the speaker and the room ring on briefly.</summary>
    public const int RingDownMs = 50;

    /// <summary>
    /// The cue as samples in [-1, 1]: a sine whose pitch glides up from 380 Hz to 1150 Hz with an exponential
    /// decay and a 6 ms click-free fade at each end - a water-drop "bloop".
    /// </summary>
    public static double[] Synthesize(int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        const double startHz = 380.0;    // pitch glides up...
        const double endHz = 1150.0;     // ...to give the droplet "bloop" chirp
        const double decayRate = 26.0;   // exponential amplitude decay (higher = shorter tail)
        const double amplitude = 0.55;   // peak level, with headroom below clipping
        const double fadeSeconds = 0.006; // click-free attack/release

        int sampleCount = (int)(sampleRate * DurationSeconds);
        var samples = new double[sampleCount];

        // Advance the phase per sample so the instantaneous frequency can rise without
        // introducing a phase discontinuity (a naive sin(2*pi*f(t)*t) would click).
        double phase = 0.0;
        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            double progress = (double)i / sampleCount;

            double freq = startHz + (endHz - startHz) * progress;
            phase += 2.0 * Math.PI * freq / sampleRate;

            double envelope = Math.Exp(-decayRate * t);
            double attack = t < fadeSeconds ? t / fadeSeconds : 1.0;
            double remaining = DurationSeconds - t;
            double release = remaining < fadeSeconds ? remaining / fadeSeconds : 1.0;

            samples[i] = Math.Sin(phase) * envelope * attack * release * amplitude;
        }
        return samples;
    }

    /// <summary>The cue as 16-bit little-endian mono PCM, for playback.</summary>
    public static byte[] SynthesizePcm16(int sampleRate)
    {
        var samples = Synthesize(sampleRate);
        var pcm = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short sample = (short)Math.Clamp(samples[i] * short.MaxValue, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return pcm;
    }

    /// <summary>
    /// Search 16-bit mono PCM for the cue, from its start up to (not past) <paramref name="searchEndByte"/>.
    /// A match must lie wholly inside the searched region. Returns the best score and where it is, and - when
    /// that score reaches <see cref="FoundScore"/> - the span to blank: match start minus
    /// <see cref="LeadInMs"/> to match end plus <see cref="RingDownMs"/>, clamped to the clip, on whole
    /// samples. Pure arithmetic; it never changes <paramref name="pcm16"/>.
    /// </summary>
    public static ReadyCueSearch Find(ReadOnlySpan<byte> pcm16, int sampleRate, long searchEndByte)
    {
        var template = Synthesize(sampleRate);
        int m = template.Length;
        int clipSamples = pcm16.Length / 2;
        int regionSamples = (int)Math.Clamp(searchEndByte / 2, 0, clipSamples);
        if (regionSamples < m)
            return new ReadyCueSearch(0.0, -1, m, 0, 0, regionSamples);

        // Zero-mean, unit-norm template: the score is then independent of the window's level and DC offset in
        // the numerator, exactly as the research computed it.
        double mean = 0;
        foreach (var v in template) mean += v;
        mean /= m;
        double norm = 0;
        for (int j = 0; j < m; j++) { template[j] -= mean; norm += template[j] * template[j]; }
        norm = Math.Sqrt(norm) + 1e-12;
        for (int j = 0; j < m; j++) template[j] /= norm;

        var x = new double[regionSamples];
        for (int i = 0; i < regionSamples; i++)
            x[i] = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));

        // Window energy from exact integer prefix sums of squares.
        var prefix = new long[regionSamples + 1];
        for (int i = 0; i < regionSamples; i++)
            prefix[i + 1] = prefix[i] + (long)x[i] * (long)x[i];

        // dot(window at i, template) for every position at once.
        var dots = CrossCorrelate(x, template);

        // The research added 1e-9 to a norm of samples scaled to [-1, 1]; the same guard in int16 units.
        const double energyGuard = 1e-9 * 32768.0;
        double best = 0;
        int bestAt = 0;
        for (int i = 0; i + m <= regionSamples; i++)
        {
            double score = Math.Abs(dots[i]) / (Math.Sqrt(prefix[i + m] - prefix[i]) + energyGuard);
            if (score > best) { best = score; bestAt = i; }
        }

        if (best < FoundScore)
            return new ReadyCueSearch(best, bestAt, m, 0, 0, regionSamples);

        int leadIn = sampleRate * LeadInMs / 1000;
        int ringDown = sampleRate * RingDownMs / 1000;
        int blankStart = Math.Max(0, bestAt - leadIn);
        int blankEnd = Math.Min(clipSamples, bestAt + m + ringDown);
        return new ReadyCueSearch(best, bestAt, m, blankStart * 2L, blankEnd * 2L, regionSamples);
    }

    /// <summary>
    /// <c>result[i] = sum over j of signal[i + j] * template[j]</c>, for every <c>i</c> from 0 to
    /// <c>signal.Length - template.Length</c>: the dot product of the template against the window starting at
    /// each sample. Computed as a circular correlation through the discrete Fourier transform, on a length
    /// that is the first power of two at or above the signal's. No wrap-around reaches a valid position: the
    /// last sample a valid window touches is <c>signal.Length - 1</c>, inside the transform length, and the
    /// zero padding beyond it contributes nothing. Entries past the last valid position are wrap-around and
    /// are not meaningful. Pure arithmetic; reads its arguments and nothing else.
    /// </summary>
    internal static double[] CrossCorrelate(ReadOnlySpan<double> signal, ReadOnlySpan<double> template)
    {
        if (template.Length > signal.Length)
            throw new ArgumentException("The template is longer than the signal; there is no window to correlate.", nameof(template));

        int length = 1;
        while (length < signal.Length) length <<= 1;

        var signalReal = new double[length];
        var signalImaginary = new double[length];
        var templateReal = new double[length];
        var templateImaginary = new double[length];
        signal.CopyTo(signalReal);
        template.CopyTo(templateReal);

        Transform(signalReal, signalImaginary, inverse: false);
        Transform(templateReal, templateImaginary, inverse: false);

        // Correlation is the product with the conjugate of the template's transform.
        for (int k = 0; k < length; k++)
        {
            double real = signalReal[k] * templateReal[k] + signalImaginary[k] * templateImaginary[k];
            double imaginary = signalImaginary[k] * templateReal[k] - signalReal[k] * templateImaginary[k];
            signalReal[k] = real;
            signalImaginary[k] = imaginary;
        }

        Transform(signalReal, signalImaginary, inverse: true);
        return signalReal;
    }

    /// <summary>
    /// In-place radix-2 fast Fourier transform of a complex sequence held as two arrays whose length is a
    /// power of two. The twiddle factors come from one table of exact cosines and sines, not from a running
    /// product, so no rounding accumulates along a stage. The inverse divides by the length, so a forward
    /// transform followed by an inverse one returns the input.
    /// </summary>
    private static void Transform(double[] real, double[] imaginary, bool inverse)
    {
        int n = real.Length;
        if (n == 0 || (n & (n - 1)) != 0)
            throw new ArgumentException("The transform length must be a power of two.", nameof(real));

        // Bit-reversal permutation, so each stage reads and writes in place.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        // W(n)^k for k in [0, n/2): every stage's twiddle is one of these, taken with a stride.
        int half = n / 2;
        var cosines = new double[Math.Max(half, 1)];
        var sines = new double[Math.Max(half, 1)];
        double sign = inverse ? 1.0 : -1.0;
        for (int k = 0; k < half; k++)
        {
            double angle = sign * 2.0 * Math.PI * k / n;
            cosines[k] = Math.Cos(angle);
            sines[k] = Math.Sin(angle);
        }

        for (int span = 2; span <= n; span <<= 1)
        {
            int halfSpan = span / 2;
            int stride = n / span;
            for (int start = 0; start < n; start += span)
            {
                for (int k = 0; k < halfSpan; k++)
                {
                    int a = start + k;
                    int b = a + halfSpan;
                    double wr = cosines[k * stride];
                    double wi = sines[k * stride];
                    double vr = real[b] * wr - imaginary[b] * wi;
                    double vi = real[b] * wi + imaginary[b] * wr;
                    real[b] = real[a] - vr;
                    imaginary[b] = imaginary[a] - vi;
                    real[a] += vr;
                    imaginary[a] += vi;
                }
            }
        }

        if (inverse)
        {
            for (int i = 0; i < n; i++)
            {
                real[i] /= n;
                imaginary[i] /= n;
            }
        }
    }
}

/// <summary>
/// The result of one <see cref="ReadyCue.Find"/>. <see cref="Found"/> is true only when the best score reached
/// <see cref="ReadyCue.FoundScore"/>; then [<see cref="BlankStartByte"/>, <see cref="BlankEndByte"/>) is the span
/// to write as zeros. <see cref="MatchSample"/> is -1 when the searched region was shorter than the cue.
/// </summary>
public readonly record struct ReadyCueSearch(
    double BestScore,
    int MatchSample,
    int CueSamples,
    long BlankStartByte,
    long BlankEndByte,
    int SearchedSamples)
{
    public bool Found => BestScore >= ReadyCue.FoundScore;
}
