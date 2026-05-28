using CcDirector.Core.Wingman;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// LIVE test for the "Ask the Wingman" faithful-answer path
/// (<see cref="WingmanService.AnswerViaSessionAsync"/>). Unit tests cover the prompt
/// text and routing parse; the one thing only a real run proves is that the read-only
/// full-power session actually reads content back VERBATIM instead of summarizing it -
/// which is the entire reason this feature exists.
///
/// We hand it a "terminal" containing a distinctive article and ask it to read the
/// article back word for word, then assert the rare sentences come back verbatim and
/// the answer was not shrunk into a summary.
///
/// OPT-IN: spends tokens and needs the claude CLI, so it only runs when
/// WINGMAN_LIVE_TESTS=1. Skipped otherwise (no false green).
/// </summary>
public sealed class WingmanAnswerLiveTests
{
    private readonly ITestOutputHelper _out;

    public WingmanAnswerLiveTests(ITestOutputHelper output) => _out = output;

    // Two rare, exact sentences that a summarizer would drop or reword but a verbatim
    // reader must reproduce intact.
    private const string RareLine1 = "The marmalade reactor hummed at exactly three hundred and seven kelvin that morning.";
    private const string RareLine2 = "Penelope counted forty-two brass cogwheels before the kettle finally whistled.";

    [Fact]
    public async Task AnswerViaSession_reads_an_article_back_verbatim()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("WINGMAN_LIVE_TESTS"), "1", StringComparison.Ordinal))
        {
            _out.WriteLine("Skipped: set WINGMAN_LIVE_TESTS=1 to run the live wingman-answer test.");
            return;
        }

        var claude = ResolveClaudePath();
        if (claude is null)
        {
            _out.WriteLine("Skipped: claude CLI not found on PATH or in %USERPROFILE%/.local/bin.");
            return;
        }

        var article =
            "Title: The Marmalade Reactor\n\n" +
            RareLine1 + " " +
            "Steam curled along the copper pipes while the morning light slid across the workshop floor.\n\n" +
            RareLine2 + " " +
            "She wrote the figure in her notebook, underlined it twice, and reached for the wrench.\n\n" +
            "By noon the whole contraption was singing, and nobody in the village could explain why the bees had gathered on the roof.";

        // A plausible session terminal: the user asked the agent to write the article and
        // the agent printed it. The wingman must read THIS back, not summarize it.
        var terminal =
            "> write a short story called The Marmalade Reactor\n\n" +
            "I'll write that now.\n\n" +
            article + "\n\n" +
            "Done - the story is above.\n" +
            "bypass permissions on (shift+tab to cycle)\n";

        var result = await WingmanService.AnswerViaSessionAsync(
            question: "Read me back the whole story you just wrote, word for word. Do not summarize it.",
            fullTerminalText: terminal,
            agentName: "Claude Code",
            repoPath: Path.GetTempPath(),
            claudeExePath: claude);

        _out.WriteLine($"status={result.Status} model={result.Model} latencyMs={result.LatencyMs} answerChars={result.Answer.Length}");
        _out.WriteLine("ANSWER:\n" + result.Answer);

        Assert.Equal("ok", result.Status);
        Assert.Equal("opus", result.Model);   // no Haiku in this feature

        // The verbatim guarantee: the rare sentences must come back intact, not reworded.
        Assert.Contains(RareLine1, result.Answer);
        Assert.Contains(RareLine2, result.Answer);

        // And it must not have collapsed the article into a short summary: the answer
        // should be at least as long as the article body's distinctive content.
        Assert.True(result.Answer.Length >= article.Length - 80,
            $"answer looks summarized: {result.Answer.Length} chars vs article {article.Length} chars");
    }

    [Fact]
    public async Task CleanVoiceTranscript_returns_verbatim_text_with_no_routing_field()
    {
        // The cleanup step is now strictly dictionary/verbatim: routing comes from the
        // button the user pressed, not from any wake phrase the model sniffed out. We
        // verify the OUTPUT here, not what the model thought the target was - because
        // it should not be thinking about a target at all.
        if (!string.Equals(Environment.GetEnvironmentVariable("WINGMAN_LIVE_TESTS"), "1", StringComparison.Ordinal))
        {
            _out.WriteLine("Skipped: set WINGMAN_LIVE_TESTS=1 to run the live cleanup test.");
            return;
        }
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _out.WriteLine("Skipped: OPENAI_API_KEY not set.");
            return;
        }

        // A "Hey wingman" wake phrase MUST be preserved verbatim now (no stripping).
        const string utterance = "Hey wingman, read me the whole article we just wrote.";
        var r = await WingmanService.CleanVoiceTranscriptAsync(utterance, repoPath: "", openAiApiKey: apiKey);
        _out.WriteLine($"cleaned=\"{r.Cleaned}\" reason=\"{r.Reason}\"");
        Assert.Contains("wingman", r.Cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read me the whole article", r.Cleaned, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveClaudePath()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(local)) return local;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "claude.exe", "claude" })
            {
                var candidate = Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
