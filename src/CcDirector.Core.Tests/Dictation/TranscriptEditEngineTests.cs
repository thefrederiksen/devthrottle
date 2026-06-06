using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// Offline tests for the deterministic edit engine that guards dictation
/// cleanup (issue #190). These pin the property the whole design exists for:
/// NO model output can change the user's words except a validated dictionary
/// correction. Includes regression replays of the real logged corruptions
/// (few-shot leaks, narration, refusals, meaning-flipping swaps).
/// </summary>
public sealed class TranscriptEditEngineTests
{
    // Mirrors the production dictionary.yaml so validation is proven against
    // the real term set.
    private static DictationDictionary ProductionDictionary() => new(
        Vocabulary: new[] { "mindzie", "Tailscale", "CenCon", "ConPTY", "cc-director", "Avalonia", "Soren Frederiksen" },
        CommonMistranscriptions: new Dictionary<string, IReadOnlyList<string>>
        {
            ["mindzie"] = new[] { "Minzy", "Mindsy", "Mindzy", "Mindzie", "Mindseeds", "mindseeds", "Mind Seeds" },
            ["CenCon"] = new[] { "SenCon", "SENCON", "Sencon" },
            ["ConPTY"] = new[] { "Contui", "ContUI", "ContiUI", "Conty" },
            ["cc-director"] = new[] { "CC Director", "See Director", "CC director" },
            ["Soren Frederiksen"] = new[] { "Soren Fredriksen", "Soeren Frederiksen" },
            ["Tailscale"] = new[] { "Teraskale", "Terascale", "Tail Scale", "Tailskale" },
        },
        Profiles: new Dictionary<string, DictationProfile>
        {
            ["default"] = new DictationProfile("default", CleanupEnabled: true),
        });

    // ===== ParseEdits ========================================================

    [Fact]
    public void ParseEdits_ValidDocument_ReturnsEdits()
    {
        var edits = TranscriptEditEngine.ParseEdits(
            "{\"edits\": [{\"find\": \"See Director\", \"replace\": \"cc-director\"}]}");
        Assert.NotNull(edits);
        var edit = Assert.Single(edits);
        Assert.Equal("See Director", edit.Find);
        Assert.Equal("cc-director", edit.Replace);
    }

    [Fact]
    public void ParseEdits_EmptyEditList_ReturnsEmpty()
    {
        var edits = TranscriptEditEngine.ParseEdits("{\"edits\": []}");
        Assert.NotNull(edits);
        Assert.Empty(edits);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[]")]                                   // array, not object
    [InlineData("{\"something\": []}")]                  // missing edits
    [InlineData("{\"edits\": \"oops\"}")]                // edits not an array
    [InlineData("{\"edits\": [{\"find\": \"x\"}]}")]     // missing replace
    [InlineData("{\"edits\": [{\"find\": 1, \"replace\": \"y\"}]}")] // wrong type
    public void ParseEdits_MalformedDocument_ReturnsNull(string? output)
    {
        Assert.Null(TranscriptEditEngine.ParseEdits(output));
    }

    // Regression: the exact model outputs that corrupted real dictations
    // (issue #190). Prose is not an edit document - parsing fails and the
    // caller ships raw, so none of these can ever reach the prompt box again.
    [Theory]
    [InlineData("yeah just push it to cc-director when you get a sec")]                  // 2026-06-06 06:47 few-shot leak
    [InlineData("Can you explain what this function does and then refactor it for me?")] // 2026-06-06 07:11 few-shot leak
    [InlineData("I understand.")]                                                        // 2026-05-28 1174-char wipeout
    [InlineData("I'm sorry, but I can't provide a table with GDP projections for Europe versus the US in 2025.")] // refusal
    [InlineData("I corrected the transcript by replacing all instances of the specified incorrect terms.")]       // narration
    public void ParseEdits_HistoricalCorruptionOutputs_AllRejected(string leakedOutput)
    {
        Assert.Null(TranscriptEditEngine.ParseEdits(leakedOutput));
    }

    // Even a leaked few-shot edit document is harmless: its find text does not
    // exist in the real transcript, so validation strips it and raw survives.
    [Fact]
    public void Validate_LeakedFewShotEditDocument_RejectedAgainstUnrelatedTranscript()
    {
        var edits = TranscriptEditEngine.ParseEdits(
            "{\"edits\": [{\"find\": \"See Director\", \"replace\": \"cc-director\"}]}");
        Assert.NotNull(edits);
        var raw = "please review the install flow and tell me what is broken";
        var validation = TranscriptEditEngine.Validate(edits, raw, ProductionDictionary());
        Assert.Empty(validation.Accepted);
        var rejected = Assert.Single(validation.Rejected);
        Assert.Contains("does not occur", rejected.Reason);
        var (text, applied) = TranscriptEditEngine.Apply(raw, validation.Accepted);
        Assert.Equal(raw, text);
        Assert.Equal(0, applied);
    }

    // ===== Validate ==========================================================

    [Fact]
    public void Validate_EveryListedDictionaryPair_IsAccepted()
    {
        // Calibration: each known wrong form must survive validation when it
        // appears in a transcript. If a future tweak to the plausibility gate
        // breaks a listed mapping, this fails.
        var dict = ProductionDictionary();
        foreach (var (canonical, wrongForms) in dict.CommonMistranscriptions)
        {
            foreach (var wrong in wrongForms)
            {
                var raw = $"talking about {wrong} in this sentence";
                var validation = TranscriptEditEngine.Validate(
                    new[] { new TranscriptEdit(wrong, canonical) }, raw, dict);
                Assert.True(validation.Accepted.Count == 1,
                    $"listed pair rejected: \"{wrong}\" -> {canonical}: "
                    + (validation.Rejected.Count > 0 ? validation.Rejected[0].Reason : "?"));
            }
        }
    }

    [Fact]
    public void Validate_ReplaceNotCanonical_Rejected()
    {
        var raw = "hello there world";
        var validation = TranscriptEditEngine.Validate(
            new[] { new TranscriptEdit("hello", "goodbye") }, raw, ProductionDictionary());
        Assert.Empty(validation.Accepted);
        Assert.Contains("not a canonical", Assert.Single(validation.Rejected).Reason);
    }

    [Fact]
    public void Validate_FindNotInTranscript_Rejected()
    {
        var raw = "nothing relevant here";
        var validation = TranscriptEditEngine.Validate(
            new[] { new TranscriptEdit("Tailskale", "Tailscale") }, raw, ProductionDictionary());
        Assert.Empty(validation.Accepted);
        Assert.Contains("does not occur", Assert.Single(validation.Rejected).Reason);
    }

    [Fact]
    public void Validate_FindIsCanonicalTerm_Rejected()
    {
        // A correct term must never be rewritten into a different term.
        var raw = "we use Tailscale for networking";
        var validation = TranscriptEditEngine.Validate(
            new[] { new TranscriptEdit("Tailscale", "mindzie") }, raw, ProductionDictionary());
        Assert.Empty(validation.Accepted);
        Assert.Contains("already a canonical term", Assert.Single(validation.Rejected).Reason);
    }

    [Fact]
    public void Validate_MeaningFlippingSwap_ClaudeToCcDirector_Rejected()
    {
        // Regression for the 2026-06-04 logged corruption: the model rewrote
        // "Claude" to "cc-director", changing which system the user meant.
        // "Claude" is not a listed wrong form and is nothing like
        // "cc-director", so the plausibility gate must block it.
        var raw = "a summary of what Claude is asking for";
        var validation = TranscriptEditEngine.Validate(
            new[] { new TranscriptEdit("Claude", "cc-director") }, raw, ProductionDictionary());
        Assert.Empty(validation.Accepted);
        Assert.Contains("not a plausible mishearing", Assert.Single(validation.Rejected).Reason);
    }

    [Fact]
    public void Validate_MeaningFlippingSwap_ConformanceToCenCon_Rejected()
    {
        // Regression for the 2026-05-27 logged corruption.
        var raw = "all of our conformance enrichments seem broken";
        var validation = TranscriptEditEngine.Validate(
            new[] { new TranscriptEdit("conformance", "CenCon") }, raw, ProductionDictionary());
        Assert.Empty(validation.Accepted);
        Assert.Contains("not a plausible mishearing", Assert.Single(validation.Rejected).Reason);
    }

    [Fact]
    public void Validate_CapitalizationFix_Accepted()
    {
        var raw = "the MINDZIE dashboard looks fine";
        var validation = TranscriptEditEngine.Validate(
            new[] { new TranscriptEdit("MINDZIE", "mindzie") }, raw, ProductionDictionary());
        Assert.Single(validation.Accepted);
    }

    [Fact]
    public void Validate_UnlistedPhoneticVariant_Accepted()
    {
        // The whole reason the LLM is in the loop: a NEW mishearing not in the
        // dictionary must still be correctable when it is phonetically close.
        var raw = "deploy it with mind zee tonight";
        var validation = TranscriptEditEngine.Validate(
            new[] { new TranscriptEdit("mind zee", "mindzie") }, raw, ProductionDictionary());
        Assert.Single(validation.Accepted);
    }

    [Fact]
    public void Validate_SpanTooLarge_Rejected()
    {
        var raw = "one two three four five six seven mentions Tailscale";
        var validation = TranscriptEditEngine.Validate(
            new[] { new TranscriptEdit("one two three four five", "Tailscale") }, raw, ProductionDictionary());
        Assert.Empty(validation.Accepted);
        Assert.Contains("span exceeds", Assert.Single(validation.Rejected).Reason);
    }

    [Fact]
    public void Validate_NoOpEdit_DroppedSilently()
    {
        var raw = "we use Tailscale here";
        var validation = TranscriptEditEngine.Validate(
            new[] { new TranscriptEdit("Tailscale", "Tailscale") }, raw, ProductionDictionary());
        Assert.Empty(validation.Accepted);
        Assert.Empty(validation.Rejected);
    }

    [Fact]
    public void Validate_EditLimit_ExcessRejected()
    {
        var raw = "the Conty backend crashed";
        // 20 individually valid edits; only MaxEdits survive, the rest are
        // rejected with a reason instead of silently dropped.
        var edits = Enumerable.Range(0, 20)
            .Select(_ => new TranscriptEdit("Conty", "ConPTY"))
            .ToArray();
        var validation = TranscriptEditEngine.Validate(edits, raw, ProductionDictionary());
        Assert.Equal(TranscriptEditEngine.MaxEdits, validation.Accepted.Count);
        Assert.Contains(validation.Rejected, r => r.Reason.Contains("edit limit"));
    }

    // ===== Apply =============================================================

    [Fact]
    public void Apply_ReplacesAllOccurrences()
    {
        var raw = "Conty crashed and then Conty restarted";
        var (text, applied) = TranscriptEditEngine.Apply(
            raw, new[] { new TranscriptEdit("Conty", "ConPTY") });
        Assert.Equal("ConPTY crashed and then ConPTY restarted", text);
        Assert.Equal(1, applied);
    }

    [Fact]
    public void Apply_IsBoundaryAware_NeverRewritesInsideWords()
    {
        var raw = "the Contying process uses Conty internally";
        var (text, _) = TranscriptEditEngine.Apply(
            raw, new[] { new TranscriptEdit("Conty", "ConPTY") });
        Assert.Equal("the Contying process uses ConPTY internally", text);
    }

    [Fact]
    public void Apply_LongerFindsFirst_NoSubstringCorruption()
    {
        var raw = "ship it via CC Director and tell CC about it";
        var (text, _) = TranscriptEditEngine.Apply(raw, new[]
        {
            new TranscriptEdit("CC", "CenCon"),
            new TranscriptEdit("CC Director", "cc-director"),
        });
        // "CC Director" must be consumed as a whole before the short "CC"
        // edit runs, or the phrase would corrupt to "CenCon Director".
        Assert.Equal("ship it via cc-director and tell CenCon about it", text);
    }

    [Fact]
    public void Apply_NoEdits_ReturnsRawUntouched()
    {
        var raw = "um so yeah i was like thinking we should uh ship it";
        var (text, applied) = TranscriptEditEngine.Apply(raw, Array.Empty<TranscriptEdit>());
        Assert.Equal(raw, text);
        Assert.Equal(0, applied);
    }

    // ===== similarity gate sanity ===========================================

    [Theory]
    [InlineData("Claude", "cc-director", false)]      // logged meaning flip
    [InlineData("conformance", "CenCon", false)]      // logged meaning flip
    [InlineData("PTY", "ConPTY", false)]              // distinct real term
    [InlineData("Sensecon", "CenCon", true)]          // unlisted phonetic variant
    [InlineData("mind zee", "mindzie", true)]         // unlisted phonetic variant
    [InlineData("sea sea director", "cc-director", true)] // shares the word "director"
    public void IsPlausibleMishearing_GateBehavesAsCalibrated(string find, string replace, bool expected)
    {
        Assert.Equal(expected,
            TranscriptEditEngine.IsPlausibleMishearing(find, replace, ProductionDictionary()));
    }
}
