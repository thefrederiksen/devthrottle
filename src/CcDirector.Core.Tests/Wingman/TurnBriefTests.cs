using CcDirector.Core.Sessions;
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
    public void Validate_ParaphrasedEvidence_Rejected()
    {
        // FIDELITY GUARD (v3.4): evidence that is NOT verbatim from the reply or screen means
        // the brief is not anchored to Claude's words - it is rejected (degrades to a stub),
        // not shipped with the receipt merely dropped. Pre-v3.4 this kept the brief.
        var json = """
        { "intent": "x", "did": ["y"],
          "needsYou": { "statement": "s", "answerVia": "reply", "selectionMode": "single",
            "submit": null, "options": [],
            "evidence": "Please approve options one and two so I can proceed",
            "urgency": "review", "confidence": "high", "railLine": "approve?" } }
        """;
        Assert.Null(TurnBriefContract.ParseAndValidate(json, Package(), "wingman:test"));
    }

    [Fact]
    public void Validate_NeedsYouWithoutEvidence_Rejected()
    {
        // FIDELITY GUARD (v3.4): a needsYou with no verbatim anchor, while there IS source
        // text to quote (the default package carries an agent reply), is rejected. This is the
        // teeth behind "the brief must not re-derive what Claude said".
        var json = """
        { "intent": "x", "did": ["y"],
          "needsYou": { "statement": "Approve to continue.", "answerVia": "reply",
            "selectionMode": "single", "submit": null,
            "options": [ { "key": "Approve", "send": "approve" } ],
            "evidence": "", "urgency": "review", "confidence": "high", "railLine": "approve?" } }
        """;
        Assert.Null(TurnBriefContract.ParseAndValidate(json, Package(), "wingman:test"));
    }

    [Fact]
    public void Validate_ContradictoryStatement_RejectedWhenUnanchored()
    {
        // The real failure that drove this change (cc-director "??" brief): the agent's reply
        // found a real bug, but the brief inverted it to "nothing is broken" with no verbatim
        // anchor. Under v3.4 an unanchored needsYou like this cannot ship.
        var p = Package(reply: "I found a real encoding bug: the name is a CustomName set via rename and the ?? is a mangled emoji.");
        var json = """
        { "intent": "x", "did": ["looked at the session name"],
          "needsYou": {
            "statement": "The ?? is just a placeholder that was never set. Nothing is broken; it is purely cosmetic.",
            "answerVia": "reply", "selectionMode": "single", "submit": null,
            "options": [ { "key": "Rename it", "send": "rename it" }, { "key": "Leave it", "send": "leave it" } ],
            "evidence": "", "urgency": "fyi", "confidence": "high", "railLine": "rename or leave?" } }
        """;
        Assert.Null(TurnBriefContract.ParseAndValidate(json, p, "wingman:test"));
    }

    [Fact]
    public void Validate_NeedsYouWithVerbatimEvidence_FromScreen_Accepted()
    {
        // The anchor may come from the SCREEN (on-screen menu the agent never restated in a
        // reply), not only the agent reply - same as the existing evidence machinery.
        var p = Package(reply: null, screen: "Do you want to proceed with the deploy? (y/n)");
        var json = """
        { "intent": "x", "did": ["prepared the deploy"],
          "needsYou": {
            "statement": "The deploy is staged and the agent is asking whether to proceed.",
            "answerVia": "keys", "selectionMode": "single", "submit": null,
            "options": [ { "key": "Yes", "send": "y" } ],
            "evidence": "Do you want to proceed with the deploy? (y/n)",
            "urgency": "review", "confidence": "high", "railLine": "proceed with deploy?" } }
        """;
        var brief = TurnBriefContract.ParseAndValidate(json, p, "wingman:test");
        Assert.NotNull(brief);
        Assert.Equal("Do you want to proceed with the deploy? (y/n)", brief.NeedsYou?.Evidence);
    }

    [Fact]
    public void Validate_NeedsYouNoSourceText_KeptWithoutReceipt()
    {
        // Degenerate case: empty reply AND empty screen - nothing to quote. The brief is not
        // rejected (there is no source to anchor to), but it carries no invented receipt.
        var p = Package(reply: null, screen: "");
        var json = """
        { "intent": "x", "did": [],
          "needsYou": { "statement": "s", "answerVia": "reply", "selectionMode": "single",
            "submit": null, "options": [ { "key": "a", "send": "a" } ],
            "evidence": "", "urgency": "review", "confidence": "high", "railLine": "r" } }
        """;
        var brief = TurnBriefContract.ParseAndValidate(json, p, "wingman:test");
        Assert.NotNull(brief);
        Assert.Equal("", brief.NeedsYou?.Evidence);
    }

    // ===== suggestedAction (v2.4, issue #201: mission-complete close suggestion) =====

    [Fact]
    public void BuildPrompt_ContainsSuggestedActionContract()
    {
        var prompt = TurnBriefContract.BuildPrompt(Package());
        Assert.Contains("suggestedAction", prompt);          // v2.4 contract field
        Assert.Contains("close_session", prompt);            // the enumerated type
        Assert.Contains("MISSION COMPLETE", prompt);         // the when-rule
        Assert.Contains("NEVER suggest close_session", prompt); // the guard rule
    }

    [Fact]
    public void BuildPrompt_ProductType_InjectsFileIssueMissionClause()
    {
        // Issue #236 (#254 rename): a Product session's brief carries the type-specific mission
        // clause that makes the close suggestion fire reliably once an issue is filed.
        var bugPkg = Package() with { SessionType = SessionType.Product };
        var prompt = TurnBriefContract.BuildPrompt(bugPkg);
        Assert.Contains("SESSION TYPE: PRODUCT", prompt);
        Assert.Contains("ONLY mission is to file one GitHub issue", prompt);
        Assert.Contains("must NOT fix the bug", prompt);
    }

    [Fact]
    public void BuildPrompt_DeveloperType_NoTypeClause_BackCompat()
    {
        // The default type adds no clause - the brief is byte-for-byte the pre-#236 prompt.
        var prompt = TurnBriefContract.BuildPrompt(Package());
        Assert.DoesNotContain("SESSION TYPE: PRODUCT", prompt);
        Assert.DoesNotContain("SESSION TYPE: DISCUSS", prompt);
    }

    [Fact]
    public void Validate_SuggestedActionClose_Parsed()
    {
        var json = """
        { "intent": "x", "did": ["filed the bug"], "needsYou": null,
          "suggestedAction": { "type": "close_session", "reason": "Bug filed as #198; nothing pending" } }
        """;
        var brief = TurnBriefContract.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.NotNull(brief.SuggestedAction);
        Assert.Equal("close_session", brief.SuggestedAction!.Type);
        Assert.Equal("Bug filed as #198; nothing pending", brief.SuggestedAction.Reason);
    }

    [Fact]
    public void Validate_SuggestedActionUnknownType_DroppedButBriefKept()
    {
        var json = """
        { "intent": "x", "did": [], "needsYou": null,
          "suggestedAction": { "type": "delete_repo", "reason": "free text never survives" } }
        """;
        var brief = TurnBriefContract.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);                 // the brief survives
        Assert.Null(brief.SuggestedAction);    // the action does not
    }

    [Fact]
    public void Validate_SuggestedActionMissingReason_Dropped()
    {
        var json = """
        { "intent": "x", "did": [], "needsYou": null,
          "suggestedAction": { "type": "close_session", "reason": "" } }
        """;
        var brief = TurnBriefContract.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.Null(brief.SuggestedAction);
    }

    [Fact]
    public void Validate_SuggestedActionOmitted_Null()
    {
        var brief = TurnBriefContract.ParseAndValidate(
            """{ "intent": "x", "did": [], "needsYou": null }""", Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.Null(brief.SuggestedAction);
    }

    [Fact]
    public void Validate_SuggestedActionOverlongReason_Capped()
    {
        var json = $$"""
        { "intent": "x", "did": [], "needsYou": null,
          "suggestedAction": { "type": "close_session", "reason": "{{new string('r', 200)}}" } }
        """;
        var brief = TurnBriefContract.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.Equal(100, brief.SuggestedAction!.Reason.Length);
    }

    // ------------------------------------------------------------------ v3 (#205)

    [Fact]
    public void BuildPrompt_ContainsColdReaderContract()
    {
        var prompt = TurnBriefContract.BuildPrompt(Package());
        Assert.Contains("COLD-READER BAR", prompt);
        Assert.Contains("remembers NOTHING", prompt);
        Assert.Contains("ifIgnored", prompt);
        Assert.Contains("allClear", prompt);
        Assert.Contains("recommended", prompt);
        Assert.Contains("situation recap", prompt);
    }

    [Fact]
    public void Validate_V3Fields_Parsed()
    {
        var json = """
        { "intent": "x", "did": ["y"],
          "needsYou": {
            "statement": "The release audit is finished. Decide what to do with the leftovers.",
            "answerVia": "reply", "selectionMode": "single", "submit": null,
            "options": [
              { "key": "sweep all", "send": "sweep", "note": "commits 14 files belonging to other sessions - risky" },
              { "key": "leave them", "send": "leave", "note": "each session commits its own work", "recommended": true }
            ],
            "evidence": "Approve 1+2 and I'll continue.", "urgency": "review", "confidence": "high",
            "railLine": "leftovers - sweep or leave?",
            "ifIgnored": "nothing breaks - the session just sits idle" } }
        """;
        var brief = TurnBriefContract.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.NotNull(brief.NeedsYou);
        Assert.Equal("nothing breaks - the session just sits idle", brief.NeedsYou.IfIgnored);
        Assert.False(brief.NeedsYou.Options[0].Recommended);
        Assert.True(brief.NeedsYou.Options[1].Recommended);
    }

    [Fact]
    public void Validate_MultipleRecommended_KeepsFirstOnly()
    {
        var json = """
        { "intent": "x", "did": [],
          "needsYou": {
            "statement": "s", "answerVia": "reply", "selectionMode": "single", "submit": null,
            "options": [
              { "key": "a", "send": "a", "recommended": true },
              { "key": "b", "send": "b", "recommended": true }
            ],
            "evidence": "Approve 1+2 and I'll continue.", "urgency": "review", "confidence": "high", "railLine": "r" } }
        """;
        var brief = TurnBriefContract.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.NotNull(brief.NeedsYou);
        Assert.True(brief.NeedsYou.Options[0].Recommended);
        Assert.False(brief.NeedsYou.Options[1].Recommended); // extra flag dropped, brief kept
    }

    [Fact]
    public void Validate_BlockingWithoutIfIgnored_Rejected()
    {
        var json = """
        { "intent": "x", "did": [],
          "needsYou": {
            "statement": "s", "answerVia": "reply", "selectionMode": "single", "submit": null,
            "options": [], "evidence": "", "urgency": "blocking", "confidence": "high", "railLine": "r" } }
        """;
        Assert.Null(TurnBriefContract.ParseAndValidate(json, Package(), "wingman:test"));
    }

    [Fact]
    public void Validate_BlockingWithIfIgnored_Accepted()
    {
        var json = """
        { "intent": "x", "did": [],
          "needsYou": {
            "statement": "s", "answerVia": "reply", "selectionMode": "single", "submit": null,
            "options": [], "evidence": "Approve 1+2 and I'll continue.", "urgency": "blocking", "confidence": "high", "railLine": "r",
            "ifIgnored": "the session stays blocked until you answer" } }
        """;
        var brief = TurnBriefContract.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
    }

    [Fact]
    public void Validate_NonBlockingWithoutIfIgnored_StillAccepted()
    {
        // Back-compat: pre-v3 shapes (no ifIgnored, no recommended, no allClear) stay valid.
        var json = """
        { "intent": "x", "did": [],
          "needsYou": {
            "statement": "s", "answerVia": "reply", "selectionMode": "single", "submit": null,
            "options": [ { "key": "a", "send": "a" } ],
            "evidence": "Approve 1+2 and I'll continue.", "urgency": "review", "confidence": "high", "railLine": "r" } }
        """;
        var brief = TurnBriefContract.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.NotNull(brief.NeedsYou);
        Assert.Null(brief.NeedsYou.IfIgnored);
        Assert.False(brief.NeedsYou.Options[0].Recommended);
    }

    [Fact]
    public void Validate_AllClear_ParsedWhenNothingNeeded()
    {
        var json = """{ "intent": "x", "did": ["y"], "needsYou": null, "allClear": "v0.3.0 published and live - nothing to do" }""";
        var brief = TurnBriefContract.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.Equal("v0.3.0 published and live - nothing to do", brief.AllClear);
    }

    [Fact]
    public void Validate_AllClearAlongsideNeedsYou_Dropped()
    {
        // Contradictory: a needs-you and an all-clear cannot both be true.
        var json = """
        { "intent": "x", "did": [], "allClear": "all good",
          "needsYou": {
            "statement": "s", "answerVia": "reply", "selectionMode": "single", "submit": null,
            "options": [], "evidence": "Approve 1+2 and I'll continue.", "urgency": "review", "confidence": "high", "railLine": "r" } }
        """;
        var brief = TurnBriefContract.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.NotNull(brief.NeedsYou);
        Assert.Null(brief.AllClear);
    }

    [Fact]
    public void Validate_OverlongV3Fields_Capped()
    {
        var json = $$"""
        { "intent": "x", "did": [], "needsYou": null, "allClear": "{{new string('a', 400)}}" }
        """;
        var brief = TurnBriefContract.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.NotNull(brief.AllClear);
        Assert.Equal(250, brief.AllClear.Length);
    }

    // =================================================================================
    // v3.2 (issue #208): parked composer reply - mechanical capture + invariants.
    // =================================================================================

    [Fact]
    public void Validate_ContractVersion_StampedOnEveryBrief()
    {
        var brief = TurnBriefContract.ParseAndValidate(
            """{ "intent": "x", "did": [], "needsYou": null }""", Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.Equal(TurnBriefContract.Version, brief.ContractVersion);
    }

    [Fact]
    public void Validate_YouAsked_StampedFromPackage()
    {
        // v3.2 (issue #208): the resolved last user prompt rides the brief so consumers
        // can show real words where the raw ask was a dictated @file path.
        var brief = TurnBriefContract.ParseAndValidate(
            """{ "intent": "x", "did": [], "needsYou": null }""", Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.Equal("last ask", brief.YouAsked);

        var oversize = Package() with { LastUserPrompt = new string('q', 1_000) };
        var capped = TurnBriefContract.ParseAndValidate(
            """{ "intent": "x", "did": [], "needsYou": null }""", oversize, "wingman:test");
        Assert.NotNull(capped);
        Assert.NotNull(capped.YouAsked);
        Assert.Equal(603, capped.YouAsked.Length); // 600 + "..."
    }

    [Fact]
    public void Validate_ParkedReply_NotQuotedInStatement_Rejected()
    {
        var p = Package() with { ParkedComposerText = "Posted, it's live" };
        var json = """
        { "intent": "x", "did": [],
          "needsYou": {
            "statement": "The agent staged the post and waits for you to submit it.",
            "answerVia": "reply", "selectionMode": "single", "submit": null,
            "options": [ { "key": "a", "send": "a" } ],
            "evidence": "", "urgency": "review", "confidence": "high", "railLine": "r" } }
        """;
        Assert.Null(TurnBriefContract.ParseAndValidate(json, p, "wingman:test"));
    }

    [Fact]
    public void Validate_ParkedReply_QuotedInStatement_Accepted()
    {
        var p = Package() with { ParkedComposerText = "Posted, it's live" };
        var json = """
        { "intent": "x", "did": [],
          "needsYou": {
            "statement": "Your reply \"Posted, it's live\" is typed but unsent - press Enter.",
            "answerVia": "keys", "selectionMode": "single", "submit": null,
            "options": [ { "key": "send my typed reply", "send": "\r", "recommended": true } ],
            "evidence": "", "urgency": "review", "confidence": "high", "railLine": "press Enter" } }
        """;
        var brief = TurnBriefContract.ParseAndValidate(json, p, "wingman:test");
        Assert.NotNull(brief);
        Assert.NotNull(brief.NeedsYou);
        Assert.Equal("\r", brief.NeedsYou.Options[0].Send);
        Assert.True(brief.NeedsYou.Options[0].Recommended);
    }

    [Fact]
    public void Validate_ParkedReply_NeedsYouNull_Rejected()
    {
        // A parked reply means the user must act; an all-clear brief would hide it.
        var p = Package() with { ParkedComposerText = "commit this and close issue 14" };
        Assert.Null(TurnBriefContract.ParseAndValidate(
            """{ "intent": "x", "did": [], "needsYou": null, "allClear": "all done" }""",
            p, "wingman:test"));
    }

    [Fact]
    public void Validate_LeadingProseBeforeJson_UnwrappedMechanically()
    {
        // Replay rounds 2+4 (issue #208): models narrate a sentence before the JSON.
        var raw = """
        This is a PARKED REPLY situation. The user already typed their answer.

        { "intent": "x", "did": ["y"], "needsYou": null, "allClear": "filed and confirmed - nothing to do" }
        """;
        var brief = TurnBriefContract.ParseAndValidate(raw, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.Equal("filed and confirmed - nothing to do", brief.AllClear);
    }

    [Fact]
    public void Validate_ProseWithoutAnyJson_StillRejected()
    {
        Assert.Null(TurnBriefContract.ParseAndValidate(
            "I could not produce a brief for this turn.", Package(), "wingman:test"));
    }

    [Fact]
    public void Prompt_CarriesActionFirstRule()
    {
        // v3.3 (issue #208, the human reviewer's verdict): buttons and rail lines name
        // the ACTION, never the mechanism.
        var prompt = TurnBriefContract.BuildPrompt(Package());
        Assert.Contains("ACTION-FIRST", prompt);
        Assert.Contains("not 'send", prompt);
        Assert.Contains("railLine names the action too", prompt);
    }

    [Fact]
    public void Prompt_CarriesNonContradictionRule()
    {
        // v3.4 (the trust fix): the agent's reply is ground truth; the brief must never
        // contradict it, and every needsYou must carry a verbatim evidence anchor.
        var prompt = TurnBriefContract.BuildPrompt(Package());
        Assert.Contains("GROUND TRUTH", prompt);
        Assert.Contains("NON-CONTRADICTION", prompt);
        Assert.Contains("no verifiable verbatim evidence is REJECTED", prompt);
    }

    [Fact]
    public void Prompt_ParkedReply_GetsItsOwnSection()
    {
        // The full section header (the RULES text also mentions the section by name,
        // so the assertion targets the header with its parenthetical).
        const string section = "=== PARKED, UNSENT USER REPLY (extracted mechanically from the composer) ===";
        var p = Package() with { ParkedComposerText = "start on #7624" };
        var prompt = TurnBriefContract.BuildPrompt(p);
        Assert.Contains(section, prompt);
        Assert.Contains("start on #7624", prompt);

        var without = TurnBriefContract.BuildPrompt(Package());
        Assert.DoesNotContain(section, without);
    }
}

// =====================================================================================
// v3.2 (issue #208): ExtractParkedComposerText - the conservative screen heuristic.
// =====================================================================================
public sealed class ParkedComposerTextTests
{
    [Fact]
    public void Extract_TextAfterIdleFooter_IsParked()
    {
        var tail = "some reply text\n❯ \n  ⏵⏵ bypass permissions on (shift+tab to cycle) · ← for agents\n\nPosted, it's live";
        Assert.Equal("Posted, it's live", TurnPackageBuilder.ExtractParkedComposerText(tail));
    }

    [Fact]
    public void Extract_NoFooterMarker_Null()
    {
        Assert.Null(TurnPackageBuilder.ExtractParkedComposerText("plain screen with a prompt\n❯ "));
        Assert.Null(TurnPackageBuilder.ExtractParkedComposerText(""));
    }

    [Fact]
    public void Extract_OnlyChromeAfterMarker_Null()
    {
        // Usage banners, tips, separators and prompt markers are not composer text.
        var tail = "reply\n· ← for agents\nYou've used 92% of your session limit · resets 6pm\nTip: ctrl+s to stash\n❯ \n──────";
        Assert.Null(TurnPackageBuilder.ExtractParkedComposerText(tail));
    }

    [Fact]
    public void Extract_MarkerGluedToText_NoNewline_IsParked()
    {
        // Rendering tears glue the footer and the composer text together on one capture.
        var tail = "✻Crunched fo 19s❯ ← for agentsThe agent ran the fix, can you verify from your side now?";
        Assert.Equal("The agent ran the fix, can you verify from your side now?",
            TurnPackageBuilder.ExtractParkedComposerText(tail));
    }

    [Fact]
    public void Extract_OversizeBlob_Null()
    {
        // A huge trailing blob means the heuristic caught rendering debris, not a reply.
        var tail = "x\n· ← for agents\n" + new string('y', 600);
        Assert.Null(TurnPackageBuilder.ExtractParkedComposerText(tail));
    }

    [Fact]
    public void Build_PopulatesParkedComposerText()
    {
        var widgets = new List<TurnWidgetDto> { new() { Kind = "UserMessage", Content = "go" } };
        var tail = "done\n  ⏵⏵ bypass permissions on (shift+tab to cycle) · ← for agents\n\nyes";
        var p = TurnPackageBuilder.Build(Guid.NewGuid(), widgets, tail, null);
        Assert.Equal("yes", p.ParkedComposerText);
    }
}
