using CcDirector.Core.Audio;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// The evidence gate, pipeline v2.0: a transcript sentence that no sound supports is not
/// delivered.
///
/// WHY. whisper-large-v3 is handed a clip and must produce a caption for all of it. It has no
/// way to answer "nothing was said here", so on a stretch with no intelligible speech it emits
/// the highest-prior caption from its training corpus - "Thank you.", "you", "Bye.", "um" - and
/// then repeats it. Since the 2026-08-29 cutover about one dictation in eight has carried a
/// word the speaker never said. The spoken words are the agent's instructions; a word the model
/// invents is an instruction nobody gave.
///
/// THE RULE. Drop a sentence whose loudest moment is more than
/// <see cref="MinDbBelowClip"/> dB below the MIDDLE of the clip's own sentence peaks.
///
/// THE REFERENCE IS THE MIDDLE, NOT THE LOUDEST. Measured, not assumed: with the loudest
/// sentence as the reference, one emphatic sentence drags the reference up and quiet real
/// sentences fall below it - 3 real words deleted, and no threshold clears the inventions
/// without losses. With the middle, there is a 6.7 dB gap between the quietest real sentence
/// and the loudest invented one.
///
/// MEASURED on a 44-clip corpus (255 sentences), labelled by gpt-transcribe - OpenAI's newest
/// transcriber, and NOT a whisper model, so it does not share whisper's hallucination priors:
/// 12 of 12 invented sentences removed, 0 of 221 real sentences deleted, 0 of 22 doubtful ones
/// touched. A held-out third scores the same as the training two-thirds.
/// Research: devthrottle_internal docs/research/transcription/2026-09-09-unspoken-words.md
/// Spec, versioned with its score: tools/transcription-lab/corpus/gate.py
///
/// RAW TEXT IS NEVER TOUCHED. The caller stores the model's FULL output as the raw transcript;
/// this decides only what is delivered, and every drop carries its reason so a removal is
/// auditable by query exactly as a dictionary edit already is.
///
/// FAILS OPEN, DELIBERATELY. Without per-sentence times, or without decodable PCM to measure,
/// the gate returns the transcript UNCHANGED rather than guessing. Losing a word the user said
/// is far worse than keeping one they did not, so every uncertainty resolves towards delivering
/// what the model produced.
/// </summary>
public static class TranscriptEvidenceGate
{
    /// <summary>Pipeline version this rule implements. Travels into the log with every drop.</summary>
    public const string Version = "v2.0";

    /// <summary>
    /// How far below the clip's middle sentence a span must be before its words are treated as
    /// unsupported. 12 dB is a quarter of the amplitude - not a close call. On the corpus the
    /// safe window runs from about 12 to 21 dB; below it real quiet speech starts to be at risk,
    /// above it inventions survive.
    /// </summary>
    public const double MinDbBelowClip = 12.0;

    /// <summary>One sentence the speech model produced, with the span it claims to occupy.</summary>
    /// <param name="Start">Seconds from the start of THIS part's audio.</param>
    /// <param name="End">Seconds from the start of THIS part's audio.</param>
    public sealed record Segment(double Start, double End, string Text);

    /// <summary>A sentence that was not delivered, and why.</summary>
    public sealed record Dropped(double Start, double End, string Text, double DbBelowClip, string Reason);

    /// <summary>The gate's verdict: what to deliver, and what was removed.</summary>
    public sealed record Result(string Text, IReadOnlyList<Dropped> DroppedSegments, bool Applied, string Reason)
    {
        /// <summary>The transcript unchanged, with the reason the gate did not run.</summary>
        public static Result NotApplied(string text, string reason) => new(text, Array.Empty<Dropped>(), false, reason);
    }

    /// <summary>
    /// Apply the gate. <paramref name="audio"/> must be the SAME bytes whose transcription
    /// produced <paramref name="segments"/>, and the segment times must be relative to those
    /// bytes - a long recording is split into parts and each part's times start at zero, so the
    /// gate runs per part, before the parts are joined.
    /// </summary>
    public static Result Apply(byte[] audio, IReadOnlyList<Segment> segments, string fullText)
    {
        if (segments is null || segments.Count == 0)
            return Result.NotApplied(fullText, "the provider returned no per-sentence times");

        var pcm = WavPeakReader.TryRead(audio);
        if (pcm is null)
            return Result.NotApplied(fullText, "the audio is not decodable PCM WAV, so nothing can be measured");

        var peaks = new double[segments.Count];
        for (int i = 0; i < segments.Count; i++)
            peaks[i] = pcm.PeakDbBetween(segments[i].Start, segments[i].End);

        var reference = Median(peaks);
        var kept = new List<string>(segments.Count);
        var dropped = new List<Dropped>();

        for (int i = 0; i < segments.Count; i++)
        {
            var below = reference - peaks[i];
            if (below > MinDbBelowClip)
            {
                dropped.Add(new Dropped(segments[i].Start, segments[i].End, segments[i].Text, Math.Round(below, 1),
                    $"{below:0.0} dB below this clip's speech - no sound supports these words"));
            }
            else
            {
                kept.Add(segments[i].Text);
            }
        }

        if (dropped.Count == 0)
            return Result.NotApplied(fullText, "every sentence is supported by sound");

        // EVERY sentence failed the test. That is a clip which is entirely non-speech, or a
        // measurement gone wrong; either way, delivering nothing is worse than delivering what
        // the model produced. Fail open and say so.
        if (kept.Count == 0)
            return Result.NotApplied(fullText, "every sentence measured as unsupported - refusing to deliver an empty transcript");

        var text = string.Join(" ", kept.Select(t => t.Trim()).Where(t => t.Length > 0));
        foreach (var d in dropped)
            FileLog.Write($"[TranscriptEvidenceGate] {Version} dropped \"{d.Text.Trim()}\" at {d.Start:0.00}-{d.End:0.00}s: {d.Reason}");

        return new Result(text, dropped, true, $"{dropped.Count} sentence(s) had no sound behind them");
    }

    private static double Median(double[] values)
    {
        var v = (double[])values.Clone();
        Array.Sort(v);
        int n = v.Length;
        return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2.0;
    }
}
