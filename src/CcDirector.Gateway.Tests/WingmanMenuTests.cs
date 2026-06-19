using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Menu handling (issue #531): the cheap "is this a menu" gate, local mapping of a spoken answer to
/// an option, tolerant parsing of the brain's menu JSON, and the speakable reading of a menu.
/// </summary>
public sealed class WingmanMenuTests
{
    private static WingmanMenu PermissionMenu() => new()
    {
        IsMenu = true,
        Question = "Do you want to proceed?",
        SelectionMode = "single",
        Submit = "",
        Options = new()
        {
            new WingmanMenuOption { Key = "1. Yes", Send = "1\r" },
            new WingmanMenuOption { Key = "2. Yes, and don't ask again", Send = "2\r", Note = "A standing grant for this command", Recommended = true },
            new WingmanMenuOption { Key = "3. No", Send = "3\r" },
        },
    };

    // ===== LooksLikeMenu (the cheap gate) =====

    [Fact]
    public void LooksLikeMenu_NumberedOptions_IsTrue()
    {
        var term = "Do you want to proceed?\n❯ 1. Yes\n  2. Yes, and don't ask again\n  3. No\n";
        Assert.True(WingmanMenuLogic.LooksLikeMenu(term));
    }

    [Fact]
    public void LooksLikeMenu_PlainProse_IsFalse()
    {
        var term = "I finished editing the file and ran the tests. Everything passes. What would you like next?";
        Assert.False(WingmanMenuLogic.LooksLikeMenu(term));
    }

    [Fact]
    public void LooksLikeMenu_ClaudeCodePermissionPrompt_IsTrue()
    {
        // A boxed permission prompt whose option lines start with box-drawing - caught by the
        // "❯ 1" cursor / "don't ask again" fingerprints even when the per-line regex is fooled.
        var term =
            "╭──────────────────────────────────────────╮\n" +
            "│ Bash command                              │\n" +
            "│ Do you want to proceed?                   │\n" +
            "│ ❯ 1. Yes                                  │\n" +
            "│   2. Yes, and don't ask again this session│\n" +
            "│   3. No, and tell Claude what to do       │\n" +
            "╰──────────────────────────────────────────╯\n";
        Assert.True(WingmanMenuLogic.LooksLikeMenu(term));
    }

    [Fact]
    public void LooksLikeMenu_NumberedListInScrollback_IsFalse()
    {
        // A numbered list the agent printed earlier, now buried under a long tail of normal output,
        // is NOT an active menu - the gate must ignore it (only the last ~40 lines count).
        var top = "Here are the choices:\n1. Yes\n2. No\n3. Maybe\n";
        var filler = string.Concat(System.Linq.Enumerable.Repeat("...working on the next thing...\n", 60));
        Assert.False(WingmanMenuLogic.LooksLikeMenu(top + filler));
    }

    [Fact]
    public void LooksLikeMenu_EmptyOrNull_IsFalse()
    {
        Assert.False(WingmanMenuLogic.LooksLikeMenu(null));
        Assert.False(WingmanMenuLogic.LooksLikeMenu(""));
    }

    // ===== MatchOption (local spoken-answer mapping) =====

    [Theory]
    [InlineData("two", 1)]
    [InlineData("number 3", 2)]
    [InlineData("option 1", 0)]
    public void MatchOption_ByNumber(string said, int expected)
        => Assert.Equal(expected, WingmanMenuLogic.MatchOption(PermissionMenu(), said));

    [Fact]
    public void MatchOption_Recommended_PicksTheRecommendedOption()
        => Assert.Equal(1, WingmanMenuLogic.MatchOption(PermissionMenu(), "go with the recommended one"));

    [Theory]
    [InlineData("the first one", 0)]
    [InlineData("third", 2)]
    [InlineData("the last one", 2)]
    public void MatchOption_ByOrdinal(string said, int expected)
        => Assert.Equal(expected, WingmanMenuLogic.MatchOption(PermissionMenu(), said));

    [Fact]
    public void MatchOption_ByLabel_MatchesTheLongestContainedLabel()
        => Assert.Equal(1, WingmanMenuLogic.MatchOption(PermissionMenu(), "yes and don't ask again please"));

    [Fact]
    public void MatchOption_NoConfidentMatch_ReturnsMinusOne()
        => Assert.Equal(-1, WingmanMenuLogic.MatchOption(PermissionMenu(), "hmm I'm not sure what to do here"));

    // ===== ParseMenu (tolerant of the model's JSON) =====

    [Fact]
    public void ParseMenu_ValidJson_BuildsTheMenu()
    {
        var json = "{\"isMenu\":true,\"question\":\"Proceed?\",\"selectionMode\":\"single\",\"submit\":\"\"," +
                   "\"options\":[{\"key\":\"1. Yes\",\"send\":\"1\\r\",\"recommended\":true},{\"key\":\"2. No\",\"send\":\"2\\r\"}]}";
        var menu = WingmanTranslator.ParseMenu(json);
        Assert.True(menu.IsMenu);
        Assert.Equal(2, menu.Options.Count);
        Assert.Equal("1\r", menu.Options[0].Send);
        Assert.True(menu.Options[0].Recommended);
    }

    [Fact]
    public void ParseMenu_DropsOptionsWithNoSend()
    {
        var json = "{\"isMenu\":true,\"options\":[{\"key\":\"1. Yes\",\"send\":\"1\\r\"},{\"key\":\"bad\",\"send\":\"\"}]}";
        var menu = WingmanTranslator.ParseMenu(json);
        Assert.Single(menu.Options);
    }

    [Fact]
    public void ParseMenu_Garbage_DegradesToNotAMenu()
    {
        Assert.False(WingmanTranslator.ParseMenu("the model rambled with no json").IsMenu);
        Assert.False(WingmanTranslator.ParseMenu("").IsMenu);
    }

    // ===== BuildMenuSpoken (ear-friendly reading) =====

    [Fact]
    public void BuildMenuSpoken_ReadsQuestionOptionsAndHowToAnswer()
    {
        var s = WingmanTranslator.BuildMenuSpoken(PermissionMenu());
        Assert.Contains("Do you want to proceed?", s);
        Assert.Contains("Option 1: Yes", s);              // the leading "1." marker is stripped for speech
        Assert.Contains("(recommended)", s);
        Assert.Contains("Say the number", s);
    }
}
