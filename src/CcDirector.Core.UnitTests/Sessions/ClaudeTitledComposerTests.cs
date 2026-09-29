using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// A NAMED CLAUDE CODE SESSION'S COMPOSER IS READABLE (issue 3481). Once a session has a name, Claude Code draws it
/// inside the rule above the composer. The reader required a plain rule there, so every named session's composer read
/// as NotFound; with a retention mark set, the send path took NotFound for "still holds text" and refused every send.
/// These frames are REAL - captured from Claude Code 2.1.284 at 161 by 41 (the size of the session that froze) with
/// the rig's --claude-titled-capture mode; see TestData/doorbell/README.md.
/// </summary>
public sealed class ClaudeTitledComposerTests
{
    private const string Draft = "make a new video of version 36";

    private static ScreenFrame Load(string name) => DoorbellCaptures.Load(name, 41);

    [Theory]
    [InlineData("claude-titled-idle-empty")]
    [InlineData("claude-titled-idle-empty-after-erase")]
    public void ReadComposerText_TitledRuleEmptyComposer_ReadsEmpty(string capture)
    {
        var frame = Load(capture);

        var (reading, text) = DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, frame);

        Assert.Equal(ComposerReading.Empty, reading);
        Assert.Equal("", text);
    }

    [Fact]
    public void ReadComposerText_TitledRuleWithADraft_ReadsTheDraft()
    {
        var frame = Load("claude-titled-owner-text");

        var (reading, text) = DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, frame);

        Assert.Equal(ComposerReading.HoldsText, reading);
        Assert.Equal(Draft, text);
    }

    [Fact]
    public void ComposerHoldsExactly_TitledRuleWithTheTypedLine_IsTrue()
    {
        var frame = Load("claude-titled-owner-text");

        Assert.True(DoorbellSafety.ComposerHoldsExactly(AgentKind.ClaudeCode, frame, Draft));
        Assert.False(DoorbellSafety.ComposerHoldsExactly(AgentKind.ClaudeCode, frame, Draft + " and more"));
    }

    [Fact]
    public void Check_TitledRuleEmptyComposer_Rings()
    {
        var frame = Load("claude-titled-idle-empty-after-erase");
        var facts = new DoorbellFacts(AgentKind.ClaudeCode, Exited: false, DirectorSaysWorking: false, HasTerminalGrid: true,
            ProductMayHaveLeftText: false, Frames: [frame, frame]);

        var verdict = DoorbellSafety.Check(facts);

        Assert.True(verdict.Ring, verdict.Detail);
    }

    [Fact]
    public void Check_TitledRuleWithADraft_DefersForTheOwnersWords()
    {
        var frame = Load("claude-titled-owner-text");
        var facts = new DoorbellFacts(AgentKind.ClaudeCode, Exited: false, DirectorSaysWorking: false, HasTerminalGrid: true,
            ProductMayHaveLeftText: false, Frames: [frame, frame]);

        var verdict = DoorbellSafety.Check(facts);

        Assert.False(verdict.Ring);
        Assert.Equal(FleetRingDeferReasons.ComposerHoldsText, verdict.Reason);
    }

    // The titled rule is recognised by its SHAPE, not loosely: rows that only look a little like it still do not frame
    // a composer, so a '❯' under them stays a picker or transcript line (NotFound), exactly as before.
    [Theory]
    [InlineData("──────────── keynote deck")]                // no closing rule characters: a heading, not a rule
    [InlineData("─── keynote deck ─")]                        // too few leading rule characters
    [InlineData("────────────keynote deck─")]                 // the name is not set off by spaces
    [InlineData("──────────── ❯ 2. Opus ─")]                  // a selection arrow inside is not a name
    public void ReadComposerText_RowThatOnlyResemblesATitledRule_DoesNotFrameAComposer(string top)
    {
        var frame = Load("claude-titled-owner-text");
        frame = DoorbellCaptures.WithRow(frame, 9, top);

        var (reading, _) = DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, frame);

        Assert.Equal(ComposerReading.NotFound, reading);
    }
}
