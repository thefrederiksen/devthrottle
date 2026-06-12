using CcDirector.Core.Voice.Services;
using Xunit;

namespace CcDirector.Core.Tests.Voice;

/// <summary>
/// Coverage for ClaudeSummarizer pre-processing and prompt content (issue #367):
/// non-Latin scripts (Korean, Japanese, ...) are valid voice content and must
/// survive CleanupForSpeech, and the summarizer prompts must instruct the model
/// to summarize faithfully in the source language instead of refusing.
/// </summary>
public sealed class ClaudeSummarizerTests
{
    [Fact]
    public void ClaudeSummarizer_NonLatinInput_IsNotDropped()
    {
        // Korean agent reply - the exact defect class from the flagged voice turn.
        var korean = "안녕하세요, 도움이 필요하시면 말씀해 주세요.";

        var result = ClaudeSummarizer.CleanupForSpeech(korean);

        Assert.False(string.IsNullOrWhiteSpace(result));
        // Not near-empty: the full Korean sentence survives, not a stripped husk.
        Assert.Equal(korean, result);
    }

    [Fact]
    public void CleanupForSpeech_JapaneseInput_IsNotDropped()
    {
        var japanese = "ビルドは成功しました。テストはすべて合格です。";

        var result = ClaudeSummarizer.CleanupForSpeech(japanese);

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.Equal(japanese, result);
    }

    [Fact]
    public void CleanupForSpeech_NonLatinWithMarkdown_KeepsTextRemovesMarkdown()
    {
        // Markdown stripping must remove only the markdown syntax, never the
        // non-Latin content it wraps.
        var korean = "완료되었습니다";
        var input = $"**{korean}**";

        var result = ClaudeSummarizer.CleanupForSpeech(input);

        Assert.Equal(korean, result);
    }

    [Fact]
    public void ClaudeSummarizer_BacktickIdentifier_IsPreserved()
    {
        // Issue #368: inline-backtick identifiers are the content of the answer
        // (session names, flag values) - the markers go, the text stays.
        var result = ClaudeSummarizer.CleanupForSpeech("Enable session `my-session` is done");

        Assert.Contains("my-session", result);
        Assert.DoesNotContain("`", result);
    }

    [Fact]
    public void ClaudeSummarizer_BacktickKeyValue_IsPreserved()
    {
        var result = ClaudeSummarizer.CleanupForSpeech("Set `wingmanEnabled=False`");

        Assert.Contains("wingmanEnabled=False", result);
        Assert.DoesNotContain("`", result);
    }

    [Fact]
    public void ClaudeSummarizer_TripleBacktickCodeBlock_IsStripped()
    {
        // Code blocks are not speakable - they are still removed entirely,
        // including their content.
        var input = "Here is the fix:\n```csharp\nvar x = 1;\n```\nDone.";

        var result = ClaudeSummarizer.CleanupForSpeech(input);

        Assert.DoesNotContain("var x = 1;", result);
        Assert.DoesNotContain("csharp", result);
        Assert.DoesNotContain("`", result);
        Assert.Contains("Here is the fix:", result);
        Assert.Contains("Done.", result);
    }

    [Fact]
    public void SummarizationPrompt_AllowsNonLatinInput_NoRejectionInstructions()
    {
        // The prompt must explicitly authorize any language/script...
        Assert.Contains("ANY language", ClaudeSummarizer.SummarizationPrompt);
        Assert.Contains("never encoding corruption", ClaudeSummarizer.SummarizationPrompt);
        Assert.Contains("never refuse", ClaudeSummarizer.SummarizationPrompt);

        // ...and must not instruct the model to reject, flag, or replace
        // non-ASCII characters as invalid.
        Assert.DoesNotContain("reject", ClaudeSummarizer.SummarizationPrompt, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invalid", ClaudeSummarizer.SummarizationPrompt, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ASCII", ClaudeSummarizer.SummarizationPrompt, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProgressPrompt_AllowsNonLatinInput_NoRejectionInstructions()
    {
        Assert.Contains("any language", ClaudeSummarizer.ProgressPrompt);
        Assert.Contains("never refuse", ClaudeSummarizer.ProgressPrompt);

        Assert.DoesNotContain("reject", ClaudeSummarizer.ProgressPrompt, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invalid", ClaudeSummarizer.ProgressPrompt, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ASCII", ClaudeSummarizer.ProgressPrompt, System.StringComparison.OrdinalIgnoreCase);
    }
}
