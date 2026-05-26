using System.Net.Http;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// Pure tests for prompt construction and profile resolution. The model side
/// call is not invoked here. The prompt is now a strict dictionary-only
/// find-and-replace: these tests pin the rules that forbid rewording so a
/// future edit cannot silently reintroduce the summarizing behavior.
/// </summary>
public sealed class CleanupOrchestratorTests
{
    private static DictationDictionary BuildDict(
        IReadOnlyList<string>? vocab = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? patterns = null,
        IReadOnlyDictionary<string, DictationProfile>? profiles = null)
        => new(
            vocab ?? Array.Empty<string>(),
            patterns ?? new Dictionary<string, IReadOnlyList<string>>(),
            profiles ?? new Dictionary<string, DictationProfile>
            {
                ["default"] = new DictationProfile("default", CleanupEnabled: true),
            });

    [Fact]
    public void BuildPrompt_IncludesVocabulary()
    {
        var dict = BuildDict(vocab: new[] { "mindzie", "CenCon" });
        var prompt = CleanupOrchestrator.BuildSystemPrompt(dict);
        Assert.Contains("mindzie", prompt);
        Assert.Contains("CenCon", prompt);
    }

    [Fact]
    public void BuildPrompt_IncludesMistranscriptionPatterns()
    {
        var patterns = new Dictionary<string, IReadOnlyList<string>>
        {
            ["ConPTY"] = new[] { "Contui", "ContUI" },
        };
        var dict = BuildDict(patterns: patterns);
        var prompt = CleanupOrchestrator.BuildSystemPrompt(dict);
        Assert.Contains("ConPTY", prompt);
        Assert.Contains("Contui", prompt);
        Assert.Contains("ContUI", prompt);
    }

    [Fact]
    public void BuildPrompt_OmitsVocabularySectionWhenEmpty()
    {
        var dict = BuildDict();
        var prompt = CleanupOrchestrator.BuildSystemPrompt(dict);
        Assert.DoesNotContain("CANONICAL TERMS", prompt);
    }

    [Fact]
    public void BuildPrompt_OmitsMistranscriptionSectionWhenEmpty()
    {
        var dict = BuildDict(vocab: new[] { "foo" });
        var prompt = CleanupOrchestrator.BuildSystemPrompt(dict);
        Assert.DoesNotContain("KNOWN MISTRANSCRIPTIONS", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_EndsWithReturnInstruction()
    {
        var dict = BuildDict(vocab: new[] { "mindzie" });
        var prompt = CleanupOrchestrator.BuildSystemPrompt(dict);
        Assert.Contains("Return ONLY the corrected transcript", prompt);
    }

    [Fact]
    public void BuildPrompt_ForbidsRewordingAndSummarizing()
    {
        // These are the load-bearing instructions. If any of them ever
        // disappears, dictation can start changing the user's words again.
        var dict = BuildDict(vocab: new[] { "mindzie" });
        var prompt = CleanupOrchestrator.BuildSystemPrompt(dict);
        Assert.Contains("Change NOTHING except the dictionary corrections", prompt);
        Assert.Contains("Do NOT reword, rephrase, shorten, summarize", prompt);
        Assert.Contains("Do NOT remove or alter filler words", prompt);
        Assert.Contains("Do NOT add or delete words", prompt);
        Assert.Contains("Do NOT guess", prompt);
    }

    [Fact]
    public void BuildPrompt_HasNoStyleOrFillerRemovalLatitude()
    {
        // The old prompt told the model to remove fillers and apply per-profile
        // style. Those clauses are gone for good.
        var dict = BuildDict(vocab: new[] { "mindzie" });
        var prompt = CleanupOrchestrator.BuildSystemPrompt(dict);
        Assert.DoesNotContain("Style guidance", prompt);
        Assert.DoesNotContain("fix obvious filler words", prompt);
        Assert.DoesNotContain("plausible near-miss", prompt);
    }

    [Fact]
    public async Task CleanAsync_EmptyInput_ReturnsEmpty()
    {
        using var orchestrator = NewFailingOrchestrator();
        var dict = BuildDict(vocab: new[] { "mindzie" });
        var outcome = await orchestrator.CleanAsync("", dict, "default");
        Assert.False(outcome.Applied);
        Assert.Equal("", outcome.Text);
    }

    [Fact]
    public async Task CleanAsync_EmptyDictionary_ReturnsRawVerbatimWithoutCallingModel()
    {
        // No vocab and no patterns means there is nothing to correct, so the
        // model is never called and the raw text is returned untouched. The
        // orchestrator's HTTP handler would throw if it were invoked.
        using var orchestrator = NewFailingOrchestrator();
        var dict = BuildDict();
        var outcome = await orchestrator.CleanAsync("hello there um world", dict, "default");
        Assert.False(outcome.Applied);
        Assert.Equal("hello there um world", outcome.Text);
        Assert.Contains("no dictionary terms", outcome.Reason);
    }

    [Fact]
    public async Task CleanAsync_CleanupDisabledProfile_ReturnsRawVerbatim()
    {
        var profiles = new Dictionary<string, DictationProfile>
        {
            ["code"] = new DictationProfile("code", CleanupEnabled: false),
            ["default"] = new DictationProfile("default", CleanupEnabled: true),
        };
        var dict = BuildDict(vocab: new[] { "mindzie" }, profiles: profiles);
        using var orchestrator = NewFailingOrchestrator();
        var outcome = await orchestrator.CleanAsync("hello world", dict, "code");
        Assert.False(outcome.Applied);
        Assert.Equal("hello world", outcome.Text);
        Assert.Contains("cleanup disabled", outcome.Reason);
    }

    [Fact]
    public async Task CleanAsync_UnknownProfile_FallsBackToDefault()
    {
        using var orchestrator = NewFailingOrchestrator();
        // Vocab present so the call proceeds past the empty-dictionary short
        // circuit. Unknown profile falls back to default (cleanup enabled),
        // then attempts OpenAI. The fake handler fails the call so it fails
        // open and returns the raw transcript with a failure reason.
        var dict = BuildDict(vocab: new[] { "mindzie" });
        var outcome = await orchestrator.CleanAsync("hello", dict, "no-such-profile");
        Assert.False(outcome.Applied);
        Assert.Equal("hello", outcome.Text);
        Assert.NotNull(outcome.Reason);
        Assert.Contains("cleanup failed", outcome.Reason!);
    }

    /// <summary>
    /// Constructs a CleanupOrchestrator whose HTTP client always fails fast,
    /// so tests run offline and don't hit the real OpenAI endpoint.
    /// </summary>
    private static CleanupOrchestrator NewFailingOrchestrator()
        => new CleanupOrchestrator(
            apiKey: "test-key-ignored-by-fake-handler",
            model: "gpt-4o-mini",
            httpClient: new HttpClient(new AlwaysFailHandler()));

    private sealed class AlwaysFailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("simulated failure for offline tests");
    }
}
