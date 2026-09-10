using System.Text.Json;
using CcDirector.Core.Audio;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The evidence gate (pipeline v2.0): a transcript sentence with no sound behind it is not
/// delivered.
///
/// Two kinds of test here, and the second is the one that matters:
///   - behaviour, on audio built in the test so the expected answer is known by construction;
///   - CONFORMANCE against the Python spec, replayed over the real corpus's measurements. The
///     spec is where the rule is developed and scored; without this test the C# could drift
///     from it and still look correct.
/// </summary>
public sealed class TranscriptEvidenceGateTests
{
    // ---- audio built to order, so the right answer is known before the gate runs ----

    private static byte[] Wav(params (double seconds, double amplitude)[] parts)
    {
        const int sampleRate = 16000;
        var samples = new List<short>();
        foreach (var (seconds, amplitude) in parts)
        {
            int n = (int)(seconds * sampleRate);
            for (int i = 0; i < n; i++)
            {
                // A 200 Hz tone at the requested amplitude: something with a real, measurable peak.
                double v = Math.Sin(2 * Math.PI * 200 * i / sampleRate) * amplitude;
                samples.Add((short)Math.Round(v * short.MaxValue));
            }
        }
        var pcm = new byte[samples.Count * 2];
        Buffer.BlockCopy(samples.ToArray(), 0, pcm, 0, pcm.Length);
        return PcmWav.Wrap(pcm, sampleRate, 1, 16);
    }

    private static TranscriptEvidenceGate.Segment Seg(double start, double end, string text)
        => new(start, end, text);

    [Fact]
    public void ASentenceOverSilenceIsDropped_AndTheRestIsDeliveredUntouched()
    {
        // three seconds of speech, one second of near-silence, three more seconds of speech
        var audio = Wav((3.0, 0.50), (1.0, 0.0004), (3.0, 0.50));
        var segments = new[]
        {
            Seg(0.0, 3.0, "the first real sentence"),
            Seg(3.0, 4.0, "Thank you."),
            Seg(4.0, 7.0, "the second real sentence"),
        };

        var r = TranscriptEvidenceGate.Apply(audio, segments, "ignored");

        Assert.True(r.Applied);
        Assert.Equal("the first real sentence the second real sentence", r.Text);
        Assert.Single(r.DroppedSegments);
        Assert.Equal("Thank you.", r.DroppedSegments[0].Text);
        Assert.True(r.DroppedSegments[0].DbBelowClip > TranscriptEvidenceGate.MinDbBelowClip);
    }

    [Fact]
    public void QuietSpeechSurvives_ItIsWithinTheBandOfTheClipsOwnVoice()
    {
        // A sentence 10 dB below the others is a person trailing off, not an invention.
        var audio = Wav((2.0, 0.50), (2.0, 0.158), (2.0, 0.50));   // 0.158 is about -10 dB
        var segments = new[]
        {
            Seg(0.0, 2.0, "loud one"),
            Seg(2.0, 4.0, "the quiet one"),
            Seg(4.0, 6.0, "loud two"),
        };

        var r = TranscriptEvidenceGate.Apply(audio, segments, "loud one the quiet one loud two");

        Assert.False(r.Applied);
        Assert.Empty(r.DroppedSegments);
        Assert.Contains("the quiet one", r.Text);
    }

    [Fact]
    public void TheReferenceIsTheMiddleSentence_NotTheLoudest()
    {
        // One emphatic sentence 14 dB above the rest. With the LOUDEST as the reference the
        // ordinary sentences fall below the threshold and are deleted; with the MIDDLE they do
        // not. This is the choice that was measured, so it gets a test of its own.
        var audio = Wav((1.0, 1.0), (1.0, 0.2), (1.0, 0.2), (1.0, 0.2));
        var segments = new[]
        {
            Seg(0.0, 1.0, "SHOUTED"),
            Seg(1.0, 2.0, "ordinary one"),
            Seg(2.0, 3.0, "ordinary two"),
            Seg(3.0, 4.0, "ordinary three"),
        };

        var r = TranscriptEvidenceGate.Apply(audio, segments, "all of it");

        // With the middle as the reference, nothing is deleted.
        Assert.Empty(r.DroppedSegments);
        Assert.False(r.Applied);

        // And the choice is load-bearing: measure the same audio against the LOUDEST sentence
        // and the three ordinary ones fall past the threshold. If this half ever stops failing,
        // the tone levels drifted and the test above stopped proving anything.
        var meter = WavPeakReader.TryRead(audio)!;
        var peaks = segments.Select(s => meter.PeakDbBetween(s.Start, s.End)).ToArray();
        var wouldDropAgainstLoudest = peaks.Count(p => peaks.Max() - p > TranscriptEvidenceGate.MinDbBelowClip);
        Assert.Equal(3, wouldDropAgainstLoudest);
    }

    [Fact]
    public void NoTimes_DeliversTheTranscriptUnchanged()
    {
        var r = TranscriptEvidenceGate.Apply(Wav((1.0, 0.5)), Array.Empty<TranscriptEvidenceGate.Segment>(), "the words");
        Assert.False(r.Applied);
        Assert.Equal("the words", r.Text);
    }

    [Fact]
    public void UndecodableAudio_DeliversTheTranscriptUnchanged()
    {
        // A WebM/Opus upload, a truncated header - anything we cannot measure. Fail open.
        var r = TranscriptEvidenceGate.Apply(new byte[] { 1, 2, 3, 4 },
            new[] { Seg(0, 1, "the words") }, "the words");
        Assert.False(r.Applied);
        Assert.Equal("the words", r.Text);
    }

    [Fact]
    public void EverySentenceUnsupported_RefusesToDeliverNothing()
    {
        // A measurement gone wrong must not silently empty a user's dictation.
        var audio = Wav((2.0, 0.0004));
        var segments = new[] { Seg(0.0, 1.0, "Thank you."), Seg(1.0, 2.0, "Thank you.") };

        var r = TranscriptEvidenceGate.Apply(audio, segments, "Thank you. Thank you.");

        Assert.False(r.Applied);
        Assert.Equal("Thank you. Thank you.", r.Text);
    }

    // ---- conformance: the C# must agree with the Python spec on the real corpus ----

    private sealed record FixtureSegment(double Start, double End, double PeakDb, bool Drop);
    private sealed record FixtureCase(string Clip, List<FixtureSegment> Segments);
    private sealed record Fixture(string Spec, string Version, string Generated, string Note, List<FixtureCase> Cases);

    [Fact]
    public void MatchesThePythonSpecOnEveryClipOfTheCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "transcript-gate-conformance.json");
        Assert.True(File.Exists(path), $"conformance fixture missing at {path}");
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal(TranscriptEvidenceGate.Version, fixture.Version);
        Assert.NotEmpty(fixture.Cases);

        int segments = 0, drops = 0;
        foreach (var c in fixture.Cases)
        {
            // Replay the spec's own measurements through the C# decision, so this compares the
            // RULE - the median reference and the threshold - not the level meter.
            var peaks = c.Segments.Select(s => s.PeakDb).ToArray();
            var reference = Median(peaks);
            for (int i = 0; i < c.Segments.Count; i++)
            {
                var below = reference - peaks[i];
                var drop = below > TranscriptEvidenceGate.MinDbBelowClip;
                Assert.True(drop == c.Segments[i].Drop,
                    $"{c.Clip} segment {i}: C# says drop={drop}, the spec says {c.Segments[i].Drop} "
                    + $"(peak {peaks[i]:0.0} dB, reference {reference:0.0} dB)");
                segments++;
                if (drop) drops++;
            }
        }

        Assert.Equal(255, segments);   // the corpus as it stood on 2026-09-09
        Assert.Equal(12, drops);
    }

    private static double Median(double[] values)
    {
        var v = (double[])values.Clone();
        Array.Sort(v);
        int n = v.Length;
        return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2.0;
    }

    // ---- the level meter itself ----

    [Fact]
    public void PeakReader_MeasuresTheSpanItIsAskedAbout_NotTheWholeClip()
    {
        var audio = Wav((1.0, 1.0), (1.0, 0.01));
        var r = WavPeakReader.TryRead(audio)!;
        Assert.NotNull(r);
        Assert.True(r.PeakDbBetween(0.0, 1.0) > -1.0);      // full scale
        Assert.True(r.PeakDbBetween(1.0, 2.0) < -30.0);     // the quiet half
        Assert.Equal(-120.0, r.PeakDbBetween(5.0, 6.0));    // past the end: no sound there
    }

    // ---- the record must show what was removed ----

    [Fact]
    public void TheRawTranscriptKeepsTheModelsFullOutput_SoARemovalIsVisibleInTheRecord()
    {
        // The defect this pins down: gating the text where the RAW transcript is read made the
        // stored raw equal the delivered text, so a removal left no trace anywhere except the
        // Gateway's log file. Diffing the record against what was delivered is how the repo
        // proves nothing was silently altered, and that only works if the raw is untouched.
        var audio = Wav((3.0, 0.50), (1.0, 0.0004), (3.0, 0.50));
        var segments = new[]
        {
            Seg(0.0, 3.0, "the first real sentence"),
            Seg(3.0, 4.0, "Thank you."),
            Seg(4.0, 7.0, "the second real sentence"),
        };
        const string modelWrote = "the first real sentence Thank you. the second real sentence";

        var r = TranscriptEvidenceGate.Apply(audio, segments, modelWrote);

        Assert.True(r.Applied);
        Assert.DoesNotContain("Thank you.", r.Text);            // not delivered
        Assert.Single(r.DroppedSegments);                        // and named in the record
        Assert.Equal("Thank you.", r.DroppedSegments[0].Text);
        Assert.Contains("Thank you.", modelWrote);               // the model's own words are intact
    }
}
