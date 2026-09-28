using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

public sealed class WingmanNarrationSourceTests
{
    [Fact]
    public void LaterUserMessageAndAuthenticationFailure_SelectsTheLiveFailureNotTheOlderReply()
    {
        var widgets = Widgets(
            ("UserMessage", "say hello"),
            ("Text", "Hello."),
            ("UserMessage", "continue with the work"));
        var rows = new[]
        {
            "continue with the work",
            "Error: API key auth failed for provider openai-mindzie",
            ">",
        };

        var source = WingmanNarrationSource.Select(widgets, rows, "transcript-1", completedTurns: 2);

        Assert.NotNull(source);
        Assert.Equal(WingmanNarrationSourceKind.TerminalFailure, source!.Kind);
        Assert.Contains("API key auth failed", source.Content);
        Assert.DoesNotContain("Hello", source.Content);
        Assert.NotNull(source.Identity);
        Assert.NotEqual(source.Content, source.Identity);
    }

    [Fact]
    public void CompletedAgentReply_WinsEvenWhenAnOldFailureStillLingersOnScreen()
    {
        var widgets = Widgets(
            ("UserMessage", "fix it"),
            ("Text", "The fix is complete."));

        var source = WingmanNarrationSource.Select(
            widgets,
            new[] { "Error: API key auth failed for provider old-provider" },
            "transcript-1");

        Assert.NotNull(source);
        Assert.Equal(WingmanNarrationSourceKind.AgentReply, source!.Kind);
        Assert.Equal("The fix is complete.", source.Content);
        // The identity is the occurrence and the words: the same words elsewhere in the conversation are another stop.
        Assert.NotNull(source.Identity);
        Assert.EndsWith(source.Content, source.Identity!);
        Assert.NotEqual(source.Content, source.Identity);
    }

    [Fact]
    public void LaterUserMessageWithoutRecognizedFailure_DoesNotReplayTheOlderReply()
    {
        var widgets = Widgets(
            ("Text", "An old answer."),
            ("UserMessage", "a new question"));

        Assert.Null(WingmanNarrationSource.Select(widgets, new[] { "Waiting for input" }));
    }

    [Fact]
    public void TerminalFailureIdentityChangesWhenTheVisibleFailureChanges()
    {
        var widgets = Widgets(("UserMessage", "run it"));

        var first = WingmanNarrationSource.Select(widgets, new[] { "Error: API key auth failed for provider one" }, "transcript-1", 1);
        var second = WingmanNarrationSource.Select(widgets, new[] { "Error: API key auth failed for provider two" }, "transcript-1", 1);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(first!.Identity);
        Assert.NotEqual(first.Identity, second!.Identity);
    }

    // ------------------------------------------- an occurrence is named only when every part of it is known (round 4)

    [Fact]
    public void AReplyWithNoProvenGeneration_HasAnUnknownIdentity_AndNoFingerprint()
    {
        var widgets = Widgets(("UserMessage", "fix it"), ("Text", "The fix is complete."));

        foreach (var generation in new[] { null, "" })
        {
            var source = WingmanNarrationSource.Select(widgets, null, generation);
            Assert.NotNull(source);                                   // the reply is still what is narrated
            Assert.Equal("The fix is complete.", source!.Content);
            Assert.Null(source.Identity);                             // but which occurrence it is, is unknown
            Assert.Null(WingmanNarrationSource.Fingerprint(source));
        }

        // Control: the same reply inside a proven generation IS named.
        Assert.NotNull(WingmanNarrationSource.Fingerprint(WingmanNarrationSource.Select(widgets, null, "transcript-1")));
    }

    [Fact]
    public void AFailureWithNoTurnCount_HasAnUnknownIdentity_NeverAPlaceholderTwoFailuresShare()
    {
        var widgets = Widgets(("UserMessage", "run it"));
        var rows = new[] { "Error: API key auth failed for provider one" };

        var noCount = WingmanNarrationSource.Select(widgets, rows, "transcript-1", completedTurns: null);
        Assert.NotNull(noCount);
        Assert.Equal(WingmanNarrationSourceKind.TerminalFailure, noCount!.Kind);
        Assert.Null(noCount.Identity);
        Assert.Null(WingmanNarrationSource.Fingerprint(noCount));

        var noGeneration = WingmanNarrationSource.Select(widgets, rows, null, completedTurns: 3);
        Assert.Null(noGeneration!.Identity);

        // Control: a known generation and a known count name it.
        Assert.NotNull(WingmanNarrationSource.Select(widgets, rows, "transcript-1", 3)!.Identity);
    }

    [Fact]
    public void NoStoredConversationAtAll_ClassifiesTheLiveScreen()
    {
        // A screen-only agent, or a Director too old to store a conversation. This used to answer null
        // before it looked at the screen, so every caller that still needed an answer wrote its own rule
        // - and the turn-verdict package builder did exactly that, and the two rules then disagreed.
        var source = WingmanNarrationSource.Select(
            new List<TurnWidgetDto>(),
            new[] { "Error: API key auth failed for provider openai-mindzie", ">" });

        Assert.NotNull(source);
        Assert.Equal(WingmanNarrationSourceKind.TerminalFailure, source!.Kind);
        Assert.Contains("API key auth failed", source.Content);
    }

    [Fact]
    public void NoStoredConversationAndNothingWrongOnScreen_IsStillNothingToNarrate()
    {
        // The control: the screen is the only source there is, and an ordinary prompt on it is not a
        // failure. Without this, the test above would pass against a rule that called every screen-only
        // stop a failure.
        Assert.Null(WingmanNarrationSource.Select(
            new List<TurnWidgetDto>(),
            new[] { "working on it", "> " }));
    }

    [Fact]
    public void NoStoredConversationAndNoScreen_IsNothingToNarrate()
    {
        Assert.Null(WingmanNarrationSource.Select(new List<TurnWidgetDto>(), null));
    }

    private static List<TurnWidgetDto> Widgets(params (string Kind, string Content)[] values)
        => values.Select(value => new TurnWidgetDto
        {
            Kind = value.Kind,
            Content = value.Content,
        }).ToList();
}
