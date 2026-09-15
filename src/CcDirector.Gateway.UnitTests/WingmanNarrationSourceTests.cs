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

        var source = WingmanNarrationSource.Select(widgets, rows);

        Assert.NotNull(source);
        Assert.Equal(WingmanNarrationSourceKind.TerminalFailure, source!.Kind);
        Assert.Contains("API key auth failed", source.Content);
        Assert.DoesNotContain("Hello", source.Content);
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
            new[] { "Error: API key auth failed for provider old-provider" });

        Assert.NotNull(source);
        Assert.Equal(WingmanNarrationSourceKind.AgentReply, source!.Kind);
        Assert.Equal("The fix is complete.", source.Content);
        Assert.Equal(source.Content, source.Identity);
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

        var first = WingmanNarrationSource.Select(widgets, new[] { "Error: API key auth failed for provider one" });
        var second = WingmanNarrationSource.Select(widgets, new[] { "Error: API key auth failed for provider two" });

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.Identity, second!.Identity);
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

    [Fact]
    public void LiveScreenIsNeededOnlyWhenThePersonSpokeAfterTheLatestReply()
    {
        Assert.True(WingmanNarrationSource.NeedsLiveScreen(Widgets(
            ("Text", "old"),
            ("UserMessage", "new"))));
        Assert.False(WingmanNarrationSource.NeedsLiveScreen(Widgets(
            ("UserMessage", "question"),
            ("Text", "answer"))));
    }

    private static List<TurnWidgetDto> Widgets(params (string Kind, string Content)[] values)
        => values.Select(value => new TurnWidgetDto
        {
            Kind = value.Kind,
            Content = value.Content,
        }).ToList();
}
