using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

// =====================================================================================
// The SURVIVING turn-brief pieces after issue #187 deleted the Director-side pipeline:
// TurnPackageBuilder (pure raw-material assembly) and TurnBriefContract (the frozen v2.3
// prompt + validation) - both now consumed by the GATEWAY's warm-brain brief agent.
// =====================================================================================
// =====================================================================================
// TurnPackageBuilder - delta from the prior brief, caps, ReplyPending propagation,
// rolling intent carry.
// =====================================================================================
public sealed class TurnPackageBuilderTests
{
    private static TurnWidgetDto User(string c) => new() { Kind = "UserMessage", Content = c };
    private static TurnWidgetDto Text(string c) => new() { Kind = "Text", Content = c };

    [Fact]
    public void Build_CarriesRollingIntent_AndDelta()
    {
        var widgets = new List<TurnWidgetDto> { User("build it"), Text("built"), User("now test it"), Text("tested, all green. Ship it?") };
        var prior = new TurnBriefDto { TurnNumber = 2, Intent = "building the feature" };

        var p = TurnPackageBuilder.Build(Guid.NewGuid(), widgets, "screen", prior);

        Assert.Equal("building the feature", p.RollingIntent);
        Assert.Equal(4, p.TurnCount);
        Assert.False(p.ReplyPending);
        Assert.Contains("now test it", p.TranscriptDelta);
        Assert.DoesNotContain("0. UserMessage: build it", p.TranscriptDelta); // before the prior brief
        Assert.Equal("tested, all green. Ship it?", p.LastAssistantText);
    }

    [Fact]
    public void Build_ReplyPending_WhenPickerOnScreen()
    {
        var widgets = new List<TurnWidgetDto> { User("ask me with the picker") };
        var p = TurnPackageBuilder.Build(Guid.NewGuid(), widgets, "Which tone? 1. Playful 2. Formal", null);
        Assert.True(p.ReplyPending);
        Assert.Contains("Which tone?", p.ScreenTail);
    }

    [Fact]
    public void Build_CapsOversizeInputs()
    {
        var widgets = new List<TurnWidgetDto> { User("go"), Text(new string('x', 60_000)) };
        var p = TurnPackageBuilder.Build(Guid.NewGuid(), widgets, new string('s', 60_000), null);
        Assert.True(p.TranscriptDelta.Length <= TurnPackageBuilder.DeltaMaxChars + 16);
        Assert.True(p.ScreenTail.Length <= TurnPackageBuilder.ScreenTailMaxChars);
    }

    [Fact]
    public void Build_CurrentHeadline_NewestNonEmpty_SkipsStubBriefs()
    {
        var widgets = new List<TurnWidgetDto> { User("go"), Text("done") };
        // Store order: newest first. Newest brief is a stub with no headline - the standing
        // headline must come from the brief behind it, not vanish.
        var recent = new List<TurnBriefDto>
        {
            new() { TurnNumber = 6, Intent = "stub", Headline = "" },
            new() { TurnNumber = 4, Intent = "x", Headline = "Cockpit gets a session story panel" },
            new() { TurnNumber = 2, Intent = "y", Headline = "Old headline" },
        };
        var p = TurnPackageBuilder.Build(Guid.NewGuid(), widgets, "", recent[0], recent);
        Assert.Equal("Cockpit gets a session story panel", p.CurrentHeadline);
    }

    [Fact]
    public void Build_CurrentHeadline_NullWhenNoBriefsCarryOne()
    {
        var widgets = new List<TurnWidgetDto> { User("go") };
        var p = TurnPackageBuilder.Build(Guid.NewGuid(), widgets, "", null);
        Assert.Null(p.CurrentHeadline);
    }
}

// =====================================================================================
// TurnBriefContract - prompt assembly + the validation layer (D5/D6).
// =====================================================================================
public sealed class TurnBriefContractValidationTests
{
    private static TurnPackage Package(string? reply = "the agent reply. Approve 1+2 and I'll continue.", string screen = "")
        => new(Guid.NewGuid(), 7, "first ask", "last ask", reply, ReplyPending: false,
               TranscriptDelta: "5. Text: " + (reply ?? ""), ScreenTail: screen,
               RollingIntent: "shipping the feature", PriorRailLines: new List<string>());

    [Fact]
    public void BuildPrompt_ContainsPackageParts_AndContractRules()
    {
        var prompt = TurnBriefContract.BuildPrompt(Package(screen: "SCREEN ROW"));
        Assert.Contains("shipping the feature", prompt);   // rolling intent
        Assert.Contains("last ask", prompt);               // this turn's prompt
        Assert.Contains("SCREEN ROW", prompt);             // grid
        Assert.Contains("selectionMode", prompt);          // contract
        Assert.Contains("pick any that apply", prompt);    // multi-select rule
        Assert.Contains("standing grant", prompt);         // permission-scope rule
        Assert.Contains("headline", prompt);               // v2.2 contract field
        Assert.Contains("turnTitle", prompt);              // v2.2 contract field
        Assert.Contains("newChapter", prompt);             // v2.3 contract field
    }

    [Fact]
    public void BuildPrompt_FeedsCurrentChapterTitle_AndChapterRule()
    {
        var p = Package() with { CurrentHeadline = "Cockpit gets a session story panel" };
        var prompt = TurnBriefContract.BuildPrompt(p);
        Assert.Contains("Current chapter title: Cockpit gets a session story panel", prompt);
        Assert.Contains("KEEP the current title", prompt);
        Assert.Contains("newChapter=false", prompt);
    }

    [Fact]
    public void BuildPrompt_NoHeadlineYet_SaysWriteTheFirstOne()
    {
        var prompt = TurnBriefContract.BuildPrompt(Package());
        Assert.Contains("(none yet - write the first one)", prompt);
    }

    [Fact]
    public void Validate_ParsesHeadlineAndTurnTitle()
    {
        var json = """
        { "headline": "Cockpit gets a session story panel",
          "turnTitle": "Added the headline field",
          "intent": "x", "did": ["y"], "needsYou": null }
        """;
        var brief = TurnBriefContract.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.Equal("Cockpit gets a session story panel", brief.Headline);
        Assert.Equal("Added the headline field", brief.TurnTitle);
    }

    [Fact]
    public void Validate_OmittedHeadline_CarriesCurrentForward()
    {
        var p = Package() with { CurrentHeadline = "The standing headline" };
        var brief = TurnBriefContract.ParseAndValidate(
            """{ "intent": "x", "did": [], "needsYou": null }""", p, "wingman:test");
        Assert.NotNull(brief);
        Assert.Equal("The standing headline", brief.Headline);
        Assert.Equal("", brief.TurnTitle);
    }

    [Fact]
    public void Validate_NewChapterTrue_Parsed()
    {
        var p = Package() with { CurrentHeadline = "Old chapter" };
        var json = """{ "headline": "New piece of work", "newChapter": true, "intent": "x", "did": [], "needsYou": null }""";
        var brief = TurnBriefContract.ParseAndValidate(json, p, "wingman:test");
        Assert.NotNull(brief);
        Assert.True(brief.NewChapter);
    }

    [Fact]
    public void Validate_NewChapterOmitted_FalseWhenChapterExists()
    {
        var p = Package() with { CurrentHeadline = "Standing chapter" };
        var json = """{ "headline": "Standing chapter", "intent": "x", "did": [], "needsYou": null }""";
        var brief = TurnBriefContract.ParseAndValidate(json, p, "wingman:test");
        Assert.NotNull(brief);
        Assert.False(brief.NewChapter);
    }

    [Fact]
    public void Validate_FirstTitle_MechanicallyStartsFirstChapter()
    {
        // No current title yet: whatever the model said, the first title IS a chapter start.
        var json = """{ "headline": "First chapter", "newChapter": false, "intent": "x", "did": [], "needsYou": null }""";
        var brief = TurnBriefContract.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.True(brief.NewChapter);
    }

    [Fact]
    public void Validate_OverlongHeadline_Capped()
    {
        var json = $$"""{ "headline": "{{new string('h', 200)}}", "intent": "x", "did": [], "needsYou": null }""";
        var brief = TurnBriefContract.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.Equal(60, brief.Headline.Length);
    }

    [Fact]
    public void Validate_GoodSingleSelect_Accepted_WithVerbatimEvidence()
    {
        var json = """
        { "intent": "shipping the feature; awaiting approval",
          "did": ["built it", "verified it"],
          "needsYou": {
            "statement": "Nothing broken. Approve to continue.",
            "answerVia": "reply", "selectionMode": "single", "submit": null,
            "options": [ {"key": "Approve 1+2", "send": "approve 1+2", "note": null} ],
            "evidence": "Approve 1+2 and I'll continue.",
            "urgency": "review", "confidence": "high", "railLine": "Approve 1+2?" } }
        """;
        var brief = TurnBriefContract.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.False(brief.Degraded);
        Assert.Equal("Approve 1+2 and I'll continue.", brief.NeedsYou?.Evidence);
        Assert.Single(brief.NeedsYou!.Options);
    }

    [Fact]
    public void Validate_FencedJson_Unwrapped()
    {
        var json = "```json\n{ \"intent\": \"x\", \"did\": [], \"needsYou\": null }\n```";
        var brief = TurnBriefContract.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.Null(brief.NeedsYou);
    }

    [Fact]
    public void Validate_ParaphrasedEvidence_DropsReceiptsButKeepsBrief()
    {
        var json = """
        { "intent": "x", "did": ["y"],
          "needsYou": { "statement": "s", "answerVia": "reply", "selectionMode": "single",
            "submit": null, "options": [],
            "evidence": "Please approve options one and two so I can proceed",
            "urgency": "review", "confidence": "high", "railLine": "approve?" } }
        """;
        var brief = TurnBriefContract.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.Equal("", brief.NeedsYou?.Evidence); // receipts killed, visibly
    }

}
