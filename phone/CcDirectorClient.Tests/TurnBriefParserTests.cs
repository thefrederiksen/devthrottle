using CcDirectorClient.Voice;
using Xunit;

namespace CcDirectorClient.Tests;

public class TurnBriefParserTests
{
    // A needs-you brief as the Gateway serializes it (camelCase, GET /turnbriefs/latest).
    private const string NeedsYouJson = """
    {
      "sessionId": "11111111-1111-1111-1111-111111111111",
      "turnNumber": 7,
      "generatedAtUtc": "2026-06-09T06:01:36Z",
      "model": "wingman:opus",
      "degraded": false,
      "headline": "Migrating auth to OAuth",
      "newChapter": true,
      "intent": "Switch login from API keys to OAuth.",
      "youAsked": "go ahead and run the migration",
      "did": ["Added the oauth_clients table.", "Rewrote 6 endpoints."],
      "needsYou": {
        "statement": "Drop the legacy api_keys table now or keep it one release?",
        "answerVia": "keys",
        "selectionMode": "single",
        "submit": null,
        "options": [
          { "key": "Keep one release", "send": "2\r", "note": "Safe rollback path.", "recommended": true },
          { "key": "Drop now", "send": "1\r", "note": "Irreversible - 1,204 rows.", "recommended": false }
        ],
        "evidence": "(1) Drop now (2) Keep one release (3) Cancel",
        "urgency": "blocking",
        "confidence": "high",
        "ifIgnored": "Claude waits, blocked; migration stays half-applied."
      }
    }
    """;

    // An all-clear + mission-complete brief (needsYou null).
    private const string AllClearJson = """
    {
      "sessionId": "22222222-2222-2222-2222-222222222222",
      "turnNumber": 11,
      "model": "wingman:opus",
      "headline": "Shipping v0.6.18",
      "intent": "Cut the v0.6.18 release.",
      "did": ["Tagged v0.6.18 and pushed the release."],
      "needsYou": null,
      "allClear": "v0.6.18 is published and live - nothing to do.",
      "suggestedAction": { "type": "close_session", "reason": "Release out and verified; nothing pending." }
    }
    """;

    [Fact]
    public void Parse_ReadsHeadlineIntentAndDidBullets()
    {
        var b = TurnBriefParser.Parse(NeedsYouJson);

        Assert.NotNull(b);
        Assert.Equal("11111111-1111-1111-1111-111111111111", b!.SessionId);
        Assert.Equal(7, b.TurnNumber);
        Assert.Equal("wingman:opus", b.Model);
        Assert.Equal("Migrating auth to OAuth", b.Headline);
        Assert.True(b.NewChapter);
        Assert.Equal("go ahead and run the migration", b.YouAsked);
        Assert.Equal(2, b.Did.Count);
        Assert.Equal("Added the oauth_clients table.", b.Did[0]);
    }

    [Fact]
    public void Parse_ReadsNeedsYouWithOptionsAndRecommendedFlag()
    {
        var b = TurnBriefParser.Parse(NeedsYouJson);

        Assert.NotNull(b!.NeedsYou);
        var n = b.NeedsYou!;
        Assert.Equal("keys", n.AnswerVia);
        Assert.Equal("blocking", n.Urgency);
        Assert.Equal("Claude waits, blocked; migration stays half-applied.", n.IfIgnored);
        Assert.Equal("(1) Drop now (2) Keep one release (3) Cancel", n.Evidence);
        Assert.Equal(2, n.Options.Count);

        // The recommended option carries the flag and the exact send a tap transmits.
        var rec = n.Options.Single(o => o.Recommended);
        Assert.Equal("Keep one release", rec.Key);
        Assert.Equal("2\r", rec.Send);
        Assert.Equal("Safe rollback path.", rec.Note);
    }

    [Fact]
    public void Parse_AllClearBrief_HasNoNeedsYouButHasVerdictAndSuggestedClose()
    {
        var b = TurnBriefParser.Parse(AllClearJson);

        Assert.NotNull(b);
        Assert.Null(b!.NeedsYou);
        Assert.Equal("v0.6.18 is published and live - nothing to do.", b.AllClear);
        Assert.NotNull(b.SuggestedAction);
        Assert.Equal("close_session", b.SuggestedAction!.Type);
        Assert.Equal("Release out and verified; nothing pending.", b.SuggestedAction.Reason);
    }

    [Fact]
    public void Parse_EmptyOrBlank_ReturnsNull()
    {
        Assert.Null(TurnBriefParser.Parse(""));
        Assert.Null(TurnBriefParser.Parse("   "));
    }

    [Fact]
    public void Parse_PayloadWithoutSessionId_ReturnsNull()
    {
        // A 404 body ({error, briefingState}) is not a brief; Parse must not treat it as one.
        Assert.Null(TurnBriefParser.Parse("""{ "error": "no brief yet", "briefingState": "Briefing" }"""));
    }

    [Fact]
    public void ParseBriefingState_PullsStateFromThe404Body()
    {
        var state = TurnBriefParser.ParseBriefingState("""{ "error": "no brief yet", "briefingState": "Briefing" }""");
        Assert.Equal("Briefing", state);
    }

    [Fact]
    public void ParseBriefingState_AbsentField_ReturnsEmpty()
    {
        Assert.Equal("", TurnBriefParser.ParseBriefingState("""{ "error": "no brief yet" }"""));
        Assert.Equal("", TurnBriefParser.ParseBriefingState(""));
    }
}
