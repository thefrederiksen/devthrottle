using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// What the turn-detection switch resolves to, pinned where the everyday gate runs it.
///
/// THIS LIVES HERE, NOT BESIDE THE DETECTOR'S OTHER TESTS, ON PURPOSE. It is a pure call on a static
/// resolver - no session, no timers - and the owner turned the row rule on by default. The detector's
/// behaviour tests drive real timers and sit in a parked suite the everyday gate does not run, so a
/// default that quietly moved back to off would have passed every commit and been caught only by a
/// release gate. Pinned here, it cannot. The companion test proving the resolved value reaches a
/// constructed detector needs a real session manager, and stays with the detector tests.
/// </summary>
public sealed class ContentRuleSwitchTests
{
    [Fact]
    public void The_switch_ships_on_with_the_row_rule_and_anything_unreadable_is_off()
    {
        // Unset is the ROW rule: the owner turned it on by default. The variable now exists to move
        // AWAY from that default, most likely to turn a misbehaving rule off, so an unrecognised value
        // falls to OFF - a misspelt "off" must never leave the rule running.
        Assert.Equal(TurnContentRule.Row, TerminalStateDetector.ResolveContentRule(null));
        Assert.Equal(TurnContentRule.Row, TerminalStateDetector.ResolveContentRule(""));
        Assert.Equal(TurnContentRule.Row, TerminalStateDetector.ResolveContentRule("   "));

        Assert.Equal(TurnContentRule.Off, TerminalStateDetector.ResolveContentRule("off"));
        Assert.Equal(TurnContentRule.Off, TerminalStateDetector.ResolveContentRule("0"));
        Assert.Equal(TurnContentRule.Off, TerminalStateDetector.ResolveContentRule("false"));
        Assert.Equal(TurnContentRule.Off, TerminalStateDetector.ResolveContentRule("no"));
        Assert.Equal(TurnContentRule.Off, TerminalStateDetector.ResolveContentRule("of"));

        Assert.Equal(TurnContentRule.Row, TerminalStateDetector.ResolveContentRule("1"));
        Assert.Equal(TurnContentRule.Row, TerminalStateDetector.ResolveContentRule("row"));
        Assert.Equal(TurnContentRule.Row, TerminalStateDetector.ResolveContentRule("on"));
        Assert.Equal(TurnContentRule.Row, TerminalStateDetector.ResolveContentRule("true"));
        Assert.Equal(TurnContentRule.Row, TerminalStateDetector.ResolveContentRule("YES"));
        Assert.Equal(TurnContentRule.Size, TerminalStateDetector.ResolveContentRule("size"));
    }
}
