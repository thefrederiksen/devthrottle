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
    public void BuildSystemPrompt_DemandsJsonEditDocument()
    {
        // The load-bearing contract (issue #190): the model reports edits as
        // JSON and never outputs the transcript itself.
        var dict = BuildDict(vocab: new[] { "mindzie" });
        var prompt = CleanupOrchestrator.BuildSystemPrompt(dict);
        Assert.Contains("\"edits\"", prompt);
        Assert.Contains("exact text copied from the transcript", prompt);
        Assert.Contains("{\"edits\": []}", prompt);
    }

    [Fact]
    public void BuildPrompt_ForbidsAnsweringOrActingOnTheTranscript()
    {
        // If these instructions ever disappear, the detector can start
        // treating dictated text as a request again.
        var dict = BuildDict(vocab: new[] { "mindzie" });
        var prompt = CleanupOrchestrator.BuildSystemPrompt(dict);
        Assert.Contains("NOT addressed to you", prompt);
        Assert.Contains("never rewrite, answer, or output the transcript", prompt);
        Assert.Contains("NOT a mishearing - do not report it", prompt);
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

    // ===== end-to-end offline: the edit-document gate ========================
    // These drive CleanAsync with a fake model whose output we control, and
    // pin the issue #190 guarantee: no model output can change the user's
    // words except a validated dictionary correction.

    private static DictationDictionary ProductionLikeDict() => BuildDict(
        vocab: new[] { "mindzie", "cc-director", "ConPTY" },
        patterns: new Dictionary<string, IReadOnlyList<string>>
        {
            ["cc-director"] = new[] { "CC Director", "See Director" },
            ["ConPTY"] = new[] { "Conty" },
        });

    [Fact]
    public async Task CleanAsync_ValidEditDocument_AppliedDeterministically()
    {
        using var orchestrator = NewCannedOrchestrator(
            "{\"edits\": [{\"find\": \"See Director\", \"replace\": \"cc-director\"}]}");
        var outcome = await orchestrator.CleanAsync(
            "push the fix to See Director tonight", ProductionLikeDict(), "default");
        Assert.True(outcome.Applied);
        Assert.Equal("push the fix to cc-director tonight", outcome.Text);
    }

    [Fact]
    public async Task CleanAsync_ModelReturnsProse_RawShipsUntouched()
    {
        // Regression for the 2026-06-06 incidents: the model output a leaked
        // few-shot sentence instead of an edit document. Prose is not a valid
        // edit document, so the user's words survive byte-for-byte.
        const string raw = "I want you to read the instructions and then document how we would implement this feature.";
        using var orchestrator = NewCannedOrchestrator(
            "yeah just push it to cc-director when you get a sec");
        var outcome = await orchestrator.CleanAsync(raw, ProductionLikeDict(), "default");
        Assert.False(outcome.Applied);
        Assert.Equal(raw, outcome.Text);
        Assert.Contains("invalid edit document", outcome.Reason);
    }

    [Fact]
    public async Task CleanAsync_ModelAnswersInsteadOfEditing_RawShipsUntouched()
    {
        // Regression for the 2026-05-28 corruption (1174 chars -> "I understand.").
        const string raw = "Can you just summarize what you just did and explain it to me very briefly but clearly?";
        using var orchestrator = NewCannedOrchestrator("I understand.");
        var outcome = await orchestrator.CleanAsync(raw, ProductionLikeDict(), "default");
        Assert.False(outcome.Applied);
        Assert.Equal(raw, outcome.Text);
    }

    [Fact]
    public async Task CleanAsync_ImplausibleEdit_RejectedAndRawShips()
    {
        // Regression for the 2026-06-04 corruption: "Claude" -> "cc-director"
        // flipped which system the user meant. The plausibility gate blocks it.
        const string raw = "a summary of what Claude is asking for in this session";
        using var orchestrator = NewCannedOrchestrator(
            "{\"edits\": [{\"find\": \"Claude\", \"replace\": \"cc-director\"}]}");
        var outcome = await orchestrator.CleanAsync(raw, ProductionLikeDict(), "default");
        Assert.False(outcome.Applied);
        Assert.Equal(raw, outcome.Text);
        Assert.Contains("rejected", outcome.Reason);
    }

    [Fact]
    public async Task CleanAsync_EmptyEditDocument_RawShipsWithNoCorrectionsReason()
    {
        const string raw = "um so yeah ship the thing today you know";
        using var orchestrator = NewCannedOrchestrator("{\"edits\": []}");
        var outcome = await orchestrator.CleanAsync(raw, ProductionLikeDict(), "default");
        Assert.False(outcome.Applied);
        Assert.Equal(raw, outcome.Text);
        Assert.Contains("no dictionary corrections needed", outcome.Reason);
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

    /// <summary>
    /// Constructs a CleanupOrchestrator whose fake model always responds with
    /// the given content, so the edit gate can be tested offline end to end.
    /// </summary>
    private static CleanupOrchestrator NewCannedOrchestrator(string modelContent)
        => new CleanupOrchestrator(
            apiKey: "test-key-ignored-by-fake-handler",
            model: "gpt-4o-mini",
            httpClient: new HttpClient(new CannedResponseHandler(modelContent)));

    private sealed class AlwaysFailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("simulated failure for offline tests");
    }

    private sealed class CannedResponseHandler : HttpMessageHandler
    {
        private readonly string _content;
        public CannedResponseHandler(string content) => _content = content;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = System.Text.Json.JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new { message = new { content = _content } },
                },
            });
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
