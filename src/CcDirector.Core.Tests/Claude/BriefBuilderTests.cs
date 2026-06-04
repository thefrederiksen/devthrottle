using CcDirector.Core.Claude;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

/// <summary>
/// Unit tests for the Brief's pure halves: widget extraction, the verbatim-by-construction
/// fallback, the substring validation that enforces the fidelity invariant (a paraphrased
/// needs-you is never shown), and cache staleness. The OpenAI condensation itself is
/// covered by the live E2E (docs/plans/cockpit-brief-view.md Phase 3), not unit tests.
/// </summary>
public sealed class BriefBuilderTests
{
    private static TurnWidgetDto User(string content) => new() { Kind = "UserMessage", Content = content };
    private static TurnWidgetDto Text(string content) => new() { Kind = "Text", Content = content };
    private static TurnWidgetDto Bash(string content) => new() { Kind = "Bash", Content = content };

    // ===== Extract =====

    [Fact]
    public void Extract_EmptyStream_AllNull()
    {
        var e = BriefBuilder.Extract(new List<TurnWidgetDto>());
        Assert.Null(e.FirstUserPrompt);
        Assert.Null(e.LastUserPrompt);
        Assert.Null(e.LastAssistantText);
        Assert.Equal(0, e.TurnCount);
    }

    [Fact]
    public void Extract_MultiTurn_FirstAndLastPromptsDiffer()
    {
        var widgets = new List<TurnWidgetDto>
        {
            User("build the launcher page"),
            Text("done, shipped 3 cards"),
            Bash("dotnet build"),
            User("make the cards bigger"),
            Text("proposed 3 changes. Approve 1+2?"),
        };
        var e = BriefBuilder.Extract(widgets);
        Assert.Equal("build the launcher page", e.FirstUserPrompt);
        Assert.Equal("make the cards bigger", e.LastUserPrompt);
        Assert.Equal("proposed 3 changes. Approve 1+2?", e.LastAssistantText);
        Assert.Equal(5, e.TurnCount);
    }

    [Fact]
    public void Extract_SingleTurn_FirstEqualsLast()
    {
        var e = BriefBuilder.Extract(new List<TurnWidgetDto> { User("hello"), Text("hi") });
        Assert.Equal("hello", e.FirstUserPrompt);
        Assert.Equal("hello", e.LastUserPrompt);
    }

    [Fact]
    public void Extract_AssistantTextNotTruncated()
    {
        var big = new string('x', 50_000);
        var e = BriefBuilder.Extract(new List<TurnWidgetDto> { User("go"), Text(big) });
        Assert.Equal(50_000, e.LastAssistantText?.Length);
    }

    [Fact]
    public void Extract_BlankMessages_TreatedAsNull()
    {
        var e = BriefBuilder.Extract(new List<TurnWidgetDto> { User("  "), Text("") });
        Assert.Null(e.FirstUserPrompt);
        Assert.Null(e.LastAssistantText);
    }

    [Fact]
    public void Extract_ReplyPending_WhenLastUserMessageHasNoTextAfterIt()
    {
        // Mid-reply, or blocked in an interactive on-screen prompt: the transcript ends at
        // the user's prompt. The brief must not condense the PREVIOUS reply against this ask.
        var e = BriefBuilder.Extract(new List<TurnWidgetDto>
        {
            User("first ask"), Text("first reply"), User("second ask"), Bash("dir"),
        });
        Assert.True(e.ReplyPending);
        Assert.Equal("first reply", e.LastAssistantText);
    }

    [Fact]
    public void Extract_ReplyPresent_NotPending()
    {
        var e = BriefBuilder.Extract(new List<TurnWidgetDto>
        {
            User("ask"), Text("reply"),
        });
        Assert.False(e.ReplyPending);
    }

    [Fact]
    public void Extract_NoUserMessages_NotPending()
    {
        var e = BriefBuilder.Extract(new List<TurnWidgetDto> { Text("orphan reply") });
        Assert.False(e.ReplyPending);
    }

    // ===== FallbackNeedsYou =====

    [Fact]
    public void FallbackNeedsYou_ReturnsFinalParagraph()
    {
        var reply = "I did a bunch of work.\n\nHere are details.\n\nApprove 1+2 and I'll continue?";
        Assert.Equal("Approve 1+2 and I'll continue?", BriefBuilder.FallbackNeedsYou(reply));
    }

    [Fact]
    public void FallbackNeedsYou_WindowsLineEndings()
    {
        var reply = "Work done.\r\n\r\nShall I proceed?";
        Assert.Equal("Shall I proceed?", BriefBuilder.FallbackNeedsYou(reply));
    }

    [Fact]
    public void FallbackNeedsYou_NullOrBlank_ReturnsNull()
    {
        Assert.Null(BriefBuilder.FallbackNeedsYou(null));
        Assert.Null(BriefBuilder.FallbackNeedsYou("   "));
    }

    [Fact]
    public void FallbackNeedsYou_LongParagraph_KeepsTail()
    {
        var reply = "intro\n\n" + new string('a', 1000);
        var got = BriefBuilder.FallbackNeedsYou(reply, maxChars: 100);
        Assert.NotNull(got);
        Assert.Equal(100, got.Length);
    }

    // ===== FindVerbatim (the fidelity gate) =====

    [Fact]
    public void FindVerbatim_ExactSubstring_Accepted()
    {
        var reply = "Lots of prose. Approve 1+2 (and your verdict on 3) and I'll make the changes.";
        var got = BriefBuilder.FindVerbatim(reply, "Approve 1+2 (and your verdict on 3) and I'll make the changes.");
        Assert.Equal("Approve 1+2 (and your verdict on 3) and I'll make the changes.", got);
    }

    [Fact]
    public void FindVerbatim_WhitespaceDifferences_ReturnsReplySpan()
    {
        var reply = "Do you want me to:\n  1. fix it now\n  2. wait for review?";
        // Model normalized the newlines/indentation to single spaces.
        var got = BriefBuilder.FindVerbatim(reply, "Do you want me to: 1. fix it now 2. wait for review?");
        Assert.Equal("Do you want me to:\n  1. fix it now\n  2. wait for review?", got);
    }

    [Fact]
    public void FindVerbatim_Paraphrase_Rejected()
    {
        var reply = "Approve 1+2 and I'll make the changes while the server's up.";
        Assert.Null(BriefBuilder.FindVerbatim(reply, "Please approve options 1 and 2 so changes can be made."));
    }

    [Fact]
    public void FindVerbatim_TrimmedWords_Rejected()
    {
        var reply = "Approve 1+2 and I'll make the changes while the server's up.";
        // Dropping words is a fidelity violation even when the rest matches.
        Assert.Null(BriefBuilder.FindVerbatim(reply, "Approve 1+2 and make the changes"));
    }

    [Fact]
    public void FindVerbatim_RegexMetacharacters_Escaped()
    {
        var reply = "Run scripts\\build.ps1 (slot 5)? [y/n]";
        var got = BriefBuilder.FindVerbatim(reply, "Run scripts\\build.ps1 (slot 5)? [y/n]");
        Assert.Equal(reply, got);
    }

    // ===== TryCreate degrade =====

    [Fact]
    public void TryCreate_NoApiKey_ReturnsNull()
    {
        var prior = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            Assert.Null(BriefBuilder.TryCreate());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", prior);
        }
    }
}

/// <summary>Cache staleness: an entry is only current for its exact turn count.</summary>
public sealed class BriefCacheTests
{
    private static BriefCache.Entry Entry(int turnCount) => new()
    {
        AtTurnCount = turnCount,
        DidBullets = new List<string> { "did a thing" },
        NeedsYouVerbatim = null,
        Condenser = "openai:test",
        GeneratedAt = DateTime.UtcNow,
    };

    [Fact]
    public void TryGetCurrent_MatchingTurnCount_ReturnsEntry()
    {
        var id = Guid.NewGuid();
        BriefCache.Set(id, Entry(7));
        Assert.NotNull(BriefCache.TryGetCurrent(id, 7));
        BriefCache.Remove(id);
    }

    [Fact]
    public void TryGetCurrent_StaleTurnCount_ReturnsNull()
    {
        var id = Guid.NewGuid();
        BriefCache.Set(id, Entry(7));
        Assert.Null(BriefCache.TryGetCurrent(id, 9));
        BriefCache.Remove(id);
    }

    [Fact]
    public void TryGetCurrent_UnknownSession_ReturnsNull()
    {
        Assert.Null(BriefCache.TryGetCurrent(Guid.NewGuid(), 1));
    }
}
