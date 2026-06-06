using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// Tests for <see cref="ExplainContract"/> (issue #217) - the "I am lost - explain"
/// deep-dive prompt and its mechanical validation. Same philosophy as the turn-brief
/// contract tests: validation is mechanical (presence, caps), never interpretation.
/// </summary>
public sealed class ExplainContractTests
{
    private static readonly Guid Sid = Guid.NewGuid();

    private static TurnBriefDto Brief(int turn, string headline = "", string title = "", bool newChapter = false,
        bool degraded = false, params string[] did) => new()
    {
        SessionId = Sid.ToString(),
        TurnNumber = turn,
        Headline = headline,
        TurnTitle = title,
        NewChapter = newChapter,
        Degraded = degraded,
        Did = did.ToList(),
    };

    private static ExplainPackage Package() => new(
        Sid, 12, "ship the release",
        new[] { "t1 CHAPTER[Release v0.3.0] tagged the release" },
        "transcript delta", "screen tail");

    // ------------------------------------------------------------------ story lines

    [Fact]
    public void BuildStoryLines_OldestFirst_MarksChapters_SkipsDegraded()
    {
        // Store order is NEWEST first; the story must read OLDEST first.
        var briefs = new List<TurnBriefDto>
        {
            Brief(3, headline: "Release v0.3.0", title: "published assets", did: "uploaded installer"),
            Brief(2, degraded: true),
            Brief(1, headline: "Release v0.3.0", title: "tagged the release", newChapter: true, did: "pushed tag"),
        };

        var lines = ExplainContract.BuildStoryLines(briefs);

        Assert.Equal(2, lines.Count); // degraded stub carries no story
        Assert.StartsWith("t1 CHAPTER[Release v0.3.0]", lines[0]);
        Assert.Contains("pushed tag", lines[0]);
        Assert.StartsWith("t3", lines[1]);
        Assert.DoesNotContain("CHAPTER", lines[1]); // newChapter=false -> no chapter marker
    }

    [Fact]
    public void BuildStoryLines_MonsterSession_KeepsTheMostRecentLines()
    {
        var briefs = Enumerable.Range(1, ExplainContract.MaxStoryLines + 50)
            .Reverse()
            .Select(i => Brief(i, title: $"turn {i}"))
            .ToList();

        var lines = ExplainContract.BuildStoryLines(briefs);

        Assert.Equal(ExplainContract.MaxStoryLines, lines.Count);
        Assert.Contains($"turn {ExplainContract.MaxStoryLines + 50}", lines[^1]); // newest survives
        Assert.Contains("turn 51", lines[0]);                                     // oldest 50 dropped
    }

    // ------------------------------------------------------------------ prompt

    [Fact]
    public void BuildPrompt_CarriesAllFourMaterialSections()
    {
        var prompt = ExplainContract.BuildPrompt(Package());

        Assert.Contains("I am lost", prompt);
        Assert.Contains("ship the release", prompt);                  // first user prompt
        Assert.Contains("CHAPTER[Release v0.3.0]", prompt);           // story lines
        Assert.Contains("transcript delta", prompt);                  // recent transcript
        Assert.Contains("screen tail", prompt);                       // current screen
    }

    // ------------------------------------------------------------------ validation

    [Fact]
    public void ParseAndValidate_ValidReply_ProducesReport()
    {
        var requested = DateTime.UtcNow.AddSeconds(-30);
        var raw = """{"whatHappened":"Shipped the release.","whatWeDid":["tagged v0.3.0","published assets"],"whatNext":"Close this session."}""";

        var report = ExplainContract.ParseAndValidate(raw, Package(), "gateway-brain/opus", requested);

        Assert.NotNull(report);
        Assert.False(report.Degraded);
        Assert.Equal(Sid.ToString(), report.SessionId);
        Assert.Equal(12, report.TurnNumber);
        Assert.Equal(requested, report.RequestedAtUtc);
        Assert.Equal("gateway-brain/opus", report.Model);
        Assert.Equal("Shipped the release.", report.WhatHappened);
        Assert.Equal(2, report.WhatWeDid.Count);
        Assert.Equal("Close this session.", report.WhatNext);
    }

    [Fact]
    public void ParseAndValidate_FencedJson_IsUnwrapped()
    {
        var raw = "```json\n{\"whatHappened\":\"Story.\",\"whatWeDid\":[\"did a thing\"],\"whatNext\":\"Next step.\"}\n```";

        var report = ExplainContract.ParseAndValidate(raw, Package(), "g", DateTime.UtcNow);

        Assert.NotNull(report);
        Assert.Equal("Story.", report.WhatHappened);
    }

    [Theory]
    [InlineData("""{"whatWeDid":["x"],"whatNext":"n"}""")]                     // missing whatHappened
    [InlineData("""{"whatHappened":"h","whatNext":"n"}""")]                    // missing whatWeDid
    [InlineData("""{"whatHappened":"h","whatWeDid":[],"whatNext":"n"}""")]     // empty whatWeDid
    [InlineData("""{"whatHappened":"h","whatWeDid":["x"]}""")]                 // missing whatNext
    [InlineData("not json at all")]
    [InlineData("")]
    public void ParseAndValidate_MissingSections_Rejects(string raw)
    {
        Assert.Null(ExplainContract.ParseAndValidate(raw, Package(), "g", DateTime.UtcNow));
    }

    [Fact]
    public void ParseAndValidate_AppliesLengthCaps()
    {
        var raw = $$"""{"whatHappened":"{{new string('a', 2000)}}","whatWeDid":["{{new string('b', 400)}}"],"whatNext":"{{new string('c', 1000)}}"}""";

        var report = ExplainContract.ParseAndValidate(raw, Package(), "g", DateTime.UtcNow);

        Assert.NotNull(report);
        Assert.Equal(1200, report.WhatHappened.Length);
        Assert.Equal(250, report.WhatWeDid[0].Length);
        Assert.Equal(700, report.WhatNext.Length);
    }
}
