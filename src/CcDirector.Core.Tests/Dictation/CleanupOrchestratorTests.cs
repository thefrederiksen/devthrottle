using System.Net;
using System.Net.Http;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// Pure tests for prompt construction and profile resolution. The Haiku side
/// call is not invoked here. End-to-end coverage with the real model lives in
/// the integration tests Phase 1 ships separately.
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
                ["default"] = new DictationProfile("default", CleanupEnabled: true, StylePrompt: null),
            });

    [Fact]
    public void BuildPrompt_IncludesVocabulary()
    {
        var dict = BuildDict(vocab: new[] { "mindzie", "CenCon" });
        var prompt = CleanupOrchestrator.BuildSystemPrompt(
            dict, dict.Profiles["default"]);
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
        var prompt = CleanupOrchestrator.BuildSystemPrompt(
            dict, dict.Profiles["default"]);
        Assert.Contains("ConPTY", prompt);
        Assert.Contains("Contui", prompt);
        Assert.Contains("ContUI", prompt);
    }

    [Fact]
    public void BuildPrompt_OmitsVocabularySectionWhenEmpty()
    {
        var dict = BuildDict();
        var prompt = CleanupOrchestrator.BuildSystemPrompt(
            dict, dict.Profiles["default"]);
        Assert.DoesNotContain("MUST appear correctly", prompt);
    }

    [Fact]
    public void BuildPrompt_OmitsMistranscriptionSectionWhenEmpty()
    {
        var dict = BuildDict(vocab: new[] { "foo" });
        var prompt = CleanupOrchestrator.BuildSystemPrompt(
            dict, dict.Profiles["default"]);
        Assert.DoesNotContain("observed in real use", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_EndsWithReturnInstruction()
    {
        // The system prompt no longer carries the raw transcript (that's the
        // chat 'user' message). It must end with the return-format instruction
        // so the model produces clean single-line output.
        var dict = BuildDict(vocab: new[] { "mindzie" });
        var prompt = CleanupOrchestrator.BuildSystemPrompt(
            dict, dict.Profiles["default"]);
        Assert.Contains("Return ONLY the cleaned transcript", prompt);
    }

    [Fact]
    public void BuildPrompt_IncludesStylePromptWhenProvided()
    {
        var profiles = new Dictionary<string, DictationProfile>
        {
            ["email"] = new DictationProfile("email", true, "tighten to professional prose"),
        };
        var dict = BuildDict(profiles: profiles);
        var prompt = CleanupOrchestrator.BuildSystemPrompt(
            dict, dict.Profiles["email"]);
        Assert.Contains("tighten to professional prose", prompt);
    }

    [Fact]
    public void BuildPrompt_OmitsStyleSectionWhenNoStylePrompt()
    {
        var dict = BuildDict();
        var prompt = CleanupOrchestrator.BuildSystemPrompt(
            dict, dict.Profiles["default"]);
        Assert.DoesNotContain("Style guidance", prompt);
    }

    [Fact]
    public async Task CleanAsync_EmptyInput_ReturnsEmpty()
    {
        using var orchestrator = NewFailingOrchestrator();
        var dict = BuildDict();
        var outcome = await orchestrator.CleanAsync("", dict, "default");
        Assert.False(outcome.Applied);
        Assert.Equal("", outcome.Text);
    }

    [Fact]
    public async Task CleanAsync_CleanupDisabledProfile_ReturnsRawVerbatim()
    {
        var profiles = new Dictionary<string, DictationProfile>
        {
            ["code"] = new DictationProfile("code", CleanupEnabled: false, StylePrompt: null),
            ["default"] = new DictationProfile("default", CleanupEnabled: true, StylePrompt: null),
        };
        var dict = BuildDict(profiles: profiles);
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
        var dict = BuildDict();
        // Unknown profile should fall back to default (cleanup enabled), then
        // attempt to invoke OpenAI. The HTTP handler simulates a network
        // failure so the call fails open and the raw transcript is returned
        // with a failure reason.
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
