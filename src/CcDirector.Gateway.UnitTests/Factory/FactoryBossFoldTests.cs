using CcDirector.Gateway.Factory;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// The Boss block's splitter: the published definition's <c>boss.md</c> becomes three parts - why, goal, limits - each
/// the text under its <c>##</c> heading, or "not set yet". Pure; the worked example is Erik's own boss.md, verbatim.
/// </summary>
public sealed class FactoryBossFoldTests
{
    // A checkout may hold this file with Windows line endings, and a raw string keeps them; the splitter answers with Unix
    // ones, so the expected texts are made Unix whatever the checkout. The file itself is split as the checkout gives it.
    private static string Lf(string text) => text.ReplaceLineEndings("\n");

    // devthrottle_internal docs/software-factory-setup/erik/boss.md, as written on 26 October 2026.
    private const string ErikBossFile = """
        # Erik Laursen's factory - the Boss (being defined)

        Built from the owner's answers in `transcript.md`. His intent; wording tidied, nothing added.
        When complete, this goes into the factory definition on the Gateway.

        ## The why (E2) - the Boss cannot change it without the owner's approval

        Erik needs a website for school: he is studying to become a chef at Humber College. This factory
        helps him build his website as a chef.

        We also use it as a learning experience in how to set up factories. So Erik is treated fully as a
        customer - in how we interact with him and how we deal with him - even though he pays for nothing.

        ## The goal (E3) - the Boss cannot change it without the owner's approval

        Help Erik build his website. Erik does not know how to build a website.

        - **Erik is the business owner and the creative arm.** He has the vision, provides the material,
          and reviews and approves all the text that is written.
        - **We are the technical arm - the website builder - not the visionaries.** Think of a small agency
          helping a small business build its website, but on the technical side only. We recommend and
          suggest mostly when Erik asks for it; we become the creative arm only when asked.
        - **Erik works with us by email only.**
        - **The factory implements everything Erik asks for.** When it clearly understands a request and can
          implement it, it goes ahead and implements it.
        - **When it does not understand something, it involves the owner first, then Erik (E3b).** If a
          seat does not understand Erik's request, or it is not clearly defined, the session asks the
          owner - the user of the factory, who can jump into the session - what he thinks it means. If the
          owner does not know either, the factory emails Erik asking him to clarify. So the factory runs on
          its own when it knows what to do, involves the owner when it does not, and Erik after that.
        - **Until we trust the factory, every email back to Erik is reviewed by the owner.** The factory
          writes the reply and leaves it in the drafts folder, leaves its session open, and asks the owner
          to review it. The owner always sends the email; the agent only creates the draft.

        ## Limits the Boss cannot change without the owner's approval (E4)

        With the owner's approval, the Boss may do anything. Without it:

        1. It does not change the why.
        2. It does not change the goal.
        3. It never lets Erik's factory go outside its directories (Erik's folder and his own sites).
        4. It never gives a seat more permission, unless that permission is harmless. If in doubt, it asks
           the owner. _(Reading of an unclear sentence - to confirm.)_
        5. It deploys only through Erik's release tool, to his own sites on Vercel, and every version is
           pushed to his GitHub repo. _("all of those", E4)_
        6. It never deletes anything. _("all of those", E4)_
        7. It never buys anything or spends money; the domain is the owner's decision. _("all of those", E4)_
        8. It never mentions the owner, the tools or anything behind the scenes to Erik. _("all of those", E4)_
        9. Every reply to Erik is a draft; the owner sends it - until we trust the factory. _(from the goal, E3)_

        ## What the Boss may do

        Everything else in the factory (method, stage 1), and anything at all with the owner's approval.
        """;

    private static readonly string ErikWhy = Lf("""
        Erik needs a website for school: he is studying to become a chef at Humber College. This factory
        helps him build his website as a chef.

        We also use it as a learning experience in how to set up factories. So Erik is treated fully as a
        customer - in how we interact with him and how we deal with him - even though he pays for nothing.
        """);

    private static readonly string ErikGoal = Lf("""
        Help Erik build his website. Erik does not know how to build a website.

        - **Erik is the business owner and the creative arm.** He has the vision, provides the material,
          and reviews and approves all the text that is written.
        - **We are the technical arm - the website builder - not the visionaries.** Think of a small agency
          helping a small business build its website, but on the technical side only. We recommend and
          suggest mostly when Erik asks for it; we become the creative arm only when asked.
        - **Erik works with us by email only.**
        - **The factory implements everything Erik asks for.** When it clearly understands a request and can
          implement it, it goes ahead and implements it.
        - **When it does not understand something, it involves the owner first, then Erik (E3b).** If a
          seat does not understand Erik's request, or it is not clearly defined, the session asks the
          owner - the user of the factory, who can jump into the session - what he thinks it means. If the
          owner does not know either, the factory emails Erik asking him to clarify. So the factory runs on
          its own when it knows what to do, involves the owner when it does not, and Erik after that.
        - **Until we trust the factory, every email back to Erik is reviewed by the owner.** The factory
          writes the reply and leaves it in the drafts folder, leaves its session open, and asks the owner
          to review it. The owner always sends the email; the agent only creates the draft.
        """);

    private static readonly string ErikLimits = Lf("""
        With the owner's approval, the Boss may do anything. Without it:

        1. It does not change the why.
        2. It does not change the goal.
        3. It never lets Erik's factory go outside its directories (Erik's folder and his own sites).
        4. It never gives a seat more permission, unless that permission is harmless. If in doubt, it asks
           the owner. _(Reading of an unclear sentence - to confirm.)_
        5. It deploys only through Erik's release tool, to his own sites on Vercel, and every version is
           pushed to his GitHub repo. _("all of those", E4)_
        6. It never deletes anything. _("all of those", E4)_
        7. It never buys anything or spends money; the domain is the owner's decision. _("all of those", E4)_
        8. It never mentions the owner, the tools or anything behind the scenes to Erik. _("all of those", E4)_
        9. Every reply to Erik is a draft; the owner sends it - until we trust the factory. _(from the goal, E3)_
        """);

    [Fact]
    public void Split_ErikBossFile_PutsEachHeadingsTextInItsPart_AndShowsNoOtherHeading()
    {
        var parts = FactoryBossFold.Split(ErikBossFile);

        Assert.Equal(new[] { "goal", "limits", "why" }, parts.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(ErikWhy, parts["why"]);
        Assert.Equal(ErikGoal, parts["goal"]);
        Assert.Equal(ErikLimits, parts["limits"]);
        // "What the Boss may do" and the title are under no part.
        Assert.DoesNotContain(parts.Values, t => t.Contains("Everything else in the factory", StringComparison.Ordinal));
        Assert.DoesNotContain(parts.Values, t => t.Contains("Built from the owner's answers", StringComparison.Ordinal));
    }

    [Fact]
    public void Block_ErikBossFile_HasNoNotSetYet()
    {
        var block = FactoryBossFold.Block(ErikBossFile);

        Assert.Equal(new[] { ErikWhy, ErikGoal, ErikLimits }, block.Parts.Select(p => p.Text));
        Assert.All(block.Parts, p => Assert.Null(p.EmptyText));
    }

    [Fact]
    public void Block_OnlyAWhy_LeavesGoalAndLimitsNotSetYet()
    {
        var block = FactoryBossFold.Block("# A factory\n\n## The why\n\nErik needs a website.\n");

        Assert.Equal("Erik needs a website.", block.Parts[0].Text);
        Assert.Null(block.Parts[0].EmptyText);
        Assert.Equal(new[] { "goal", "limits" }, block.Parts.Skip(1).Select(p => p.Key));
        Assert.All(block.Parts.Skip(1), p =>
        {
            Assert.Null(p.Text);
            Assert.Equal("not set yet", p.EmptyText);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    public void Block_NoFile_IsNotSetYetThreeTimes(string? file)
    {
        var block = FactoryBossFold.Block(file);

        Assert.Equal("The Boss", block.Heading);
        Assert.Equal(new[] { "Why", "Goal", "Limits the Boss cannot change" }, block.Parts.Select(p => p.Heading));
        Assert.All(block.Parts, p => Assert.Equal("not set yet", p.EmptyText));
    }

    [Fact]
    public void Split_MatchesTheHeadingsFirstWords_IgnoringCase_AndOnlyOnASecondLevelHeading()
    {
        var parts = FactoryBossFold.Split(
            "# The why\nA title, not a part.\n\n## THE GOAL: what we do\nGoal text.\n\n## The whyever\nNot the why.\n\n## limits (E4)\nLimit text.\n");

        Assert.False(parts.ContainsKey("why"));
        Assert.Equal("Goal text.", parts["goal"]);
        Assert.Equal("Limit text.", parts["limits"]);
    }

    [Fact]
    public void Split_AHeadingWithNothingUnderIt_IsNotSet_AndASubheadingOrAFencedHeadingStaysInItsPart()
    {
        var parts = FactoryBossFold.Split("## The why\n\n## The goal\nFirst.\n\n### Detail\nSecond.\n```\n## Limits\n```\n");

        Assert.False(parts.ContainsKey("why"));
        Assert.Equal("First.\n\n### Detail\nSecond.\n```\n## Limits\n```", parts["goal"]);
        Assert.False(parts.ContainsKey("limits"));
    }

    // Each heading form the Delivery Lead named (27 October 2026): with or without "The", in any case, with words after it.
    [Theory]
    [InlineData("Why", "why")]
    [InlineData("The why", "why")]
    [InlineData("WHY", "why")]
    [InlineData("The Why (E2)", "why")]
    [InlineData("Goal", "goal")]
    [InlineData("The goal", "goal")]
    [InlineData("The goal (E3)", "goal")]
    [InlineData("Limits", "limits")]
    [InlineData("Limits the Boss cannot change without the owner's approval (E4)", "limits")]
    public void Block_HeadingForm_FillsItsPart(string heading, string key)
    {
        var block = FactoryBossFold.Block($"# A factory\n\n## {heading}\n\nThe text.\n");

        var part = Assert.Single(block.Parts, p => p.Key == key);
        Assert.Equal("The text.", part.Text);
        Assert.Null(part.EmptyText);
        Assert.All(block.Parts.Where(p => p.Key != key), p => Assert.Equal("not set yet", p.EmptyText));
    }

    [Fact]
    public void Block_WhatTheBossMayDo_IsUnderNoPart()
    {
        var block = FactoryBossFold.Block("## What the Boss may do\n\nEverything else.\n");

        Assert.All(block.Parts, p =>
        {
            Assert.Null(p.Text);
            Assert.Equal("not set yet", p.EmptyText);
        });
    }

    [Fact]
    public void Split_WindowsLineEndings_ReadTheSameAsUnix()
    {
        Assert.Equal("Line one\nLine two", FactoryBossFold.Split("## The why\r\nLine one\r\nLine two\r\n")["why"]);
    }
}
