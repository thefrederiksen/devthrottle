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
        var prompt = CleanupOrchestrator.BuildPrompt(
            "raw", dict, dict.Profiles["default"]);
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
        var prompt = CleanupOrchestrator.BuildPrompt(
            "raw", dict, dict.Profiles["default"]);
        Assert.Contains("ConPTY", prompt);
        Assert.Contains("Contui", prompt);
        Assert.Contains("ContUI", prompt);
    }

    [Fact]
    public void BuildPrompt_OmitsVocabularySectionWhenEmpty()
    {
        var dict = BuildDict();
        var prompt = CleanupOrchestrator.BuildPrompt(
            "raw", dict, dict.Profiles["default"]);
        Assert.DoesNotContain("MUST appear correctly", prompt);
    }

    [Fact]
    public void BuildPrompt_OmitsMistranscriptionSectionWhenEmpty()
    {
        var dict = BuildDict(vocab: new[] { "foo" });
        var prompt = CleanupOrchestrator.BuildPrompt(
            "raw", dict, dict.Profiles["default"]);
        Assert.DoesNotContain("observed in real use", prompt);
    }

    [Fact]
    public void BuildPrompt_AppendsRawTranscriptAtEnd()
    {
        var dict = BuildDict(vocab: new[] { "mindzie" });
        var prompt = CleanupOrchestrator.BuildPrompt(
            "Tell mindsy hello.", dict, dict.Profiles["default"]);
        Assert.EndsWith("Tell mindsy hello.", prompt);
    }

    [Fact]
    public void BuildPrompt_IncludesStylePromptWhenProvided()
    {
        var profiles = new Dictionary<string, DictationProfile>
        {
            ["email"] = new DictationProfile("email", true, "tighten to professional prose"),
        };
        var dict = BuildDict(profiles: profiles);
        var prompt = CleanupOrchestrator.BuildPrompt(
            "raw", dict, dict.Profiles["email"]);
        Assert.Contains("tighten to professional prose", prompt);
    }

    [Fact]
    public void BuildPrompt_OmitsStyleSectionWhenNoStylePrompt()
    {
        var dict = BuildDict();
        var prompt = CleanupOrchestrator.BuildPrompt(
            "raw", dict, dict.Profiles["default"]);
        Assert.DoesNotContain("Style guidance", prompt);
    }

    [Fact]
    public async Task CleanAsync_EmptyInput_ReturnsEmpty()
    {
        var orchestrator = new CleanupOrchestrator(@"C:\does\not\matter.exe");
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
        var orchestrator = new CleanupOrchestrator(@"C:\does\not\matter.exe");
        var outcome = await orchestrator.CleanAsync("hello world", dict, "code");
        Assert.False(outcome.Applied);
        Assert.Equal("hello world", outcome.Text);
        Assert.Contains("cleanup disabled", outcome.Reason);
    }

    [Fact]
    public async Task CleanAsync_UnknownProfile_FallsBackToDefault()
    {
        var orchestrator = new CleanupOrchestrator(@"C:\does\not\exist.exe");
        var dict = BuildDict();
        // Unknown profile should fall back to default (cleanup enabled), then
        // attempt to invoke claude. We are using a bogus path so the call
        // fails open and the raw transcript is returned with a failure reason.
        var outcome = await orchestrator.CleanAsync("hello", dict, "no-such-profile");
        Assert.False(outcome.Applied);
        Assert.Equal("hello", outcome.Text);
        Assert.NotNull(outcome.Reason);
        Assert.Contains("cleanup failed", outcome.Reason!);
    }

    [Fact]
    public void Ctor_BlankClaudePath_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CleanupOrchestrator(""));
        Assert.Throws<ArgumentException>(() => new CleanupOrchestrator("   "));
    }
}
