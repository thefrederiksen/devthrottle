using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// AN ACT'S REASON MUST BE GROUNDED IN THE SCREEN IT WAS GIVEN (Architect ruling A12).
///
/// This is the check itself. The evaluator's half - an act refused, a decline recorded with the mismatch
/// noted - is in <see cref="RuleEvaluatorTests"/>.
///
/// The case that produced this ruling is the first test: on a live run a rule declined and quoted a
/// sentence that had been on that session's screen twelve minutes earlier, in an unrelated run. The
/// decline was safe. The same unfaithfulness pointed the other way is a rule acting on evidence that was
/// not there.
/// </summary>
public sealed class RuleReasonGroundingTests
{
    private const string TheScreen =
        "C:\\scratch> echo notice\n" +
        "Claude usage limit reached. Your limit will reset at 2:30pm.\n" +
        "C:\\scratch>";

    [Fact]
    public void A_reason_quoting_words_that_are_not_on_the_screen_is_not_grounded_and_names_them()
    {
        // The real sentence from the live run, quoted from the firing record.
        const string reason =
            "the echo output explicitly says 'THE SCREEN HAS MOVED ON WHILE THE RULE WAS THINKING', " +
            "confirming it is no longer stopped on the allowance notice.";

        var verdict = RuleReasonGrounding.Check(reason, TheScreen);

        Assert.False(verdict.IsGrounded);
        Assert.Contains("THE SCREEN HAS MOVED ON", string.Join(" | ", verdict.NotOnTheScreen),
            StringComparison.Ordinal);
        Assert.Contains("THE SCREEN HAS MOVED ON", verdict.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reason_quoting_words_that_ARE_on_the_screen_is_grounded()
    {
        const string reason =
            "the screen says 'Claude usage limit reached', which is the notice the instruction is about.";

        var verdict = RuleReasonGrounding.Check(reason, TheScreen);

        Assert.True(verdict.IsGrounded, verdict.Statement);
        Assert.Empty(verdict.NotOnTheScreen);
        Assert.NotEqual("", verdict.Statement);
    }

    [Fact]
    public void A_quotation_the_terminal_wrapped_across_lines_still_counts_as_being_on_the_screen()
    {
        var wrapped = "Claude usage limit\nreached. Your limit will reset at 2:30pm.";
        const string reason = "the screen says 'Claude usage limit reached'.";

        var verdict = RuleReasonGrounding.Check(reason, wrapped);

        Assert.True(verdict.IsGrounded, verdict.Statement);
    }

    [Fact]
    public void A_reason_that_cites_nothing_cannot_carry_an_act_and_the_record_says_so()
    {
        // THE HALF THAT WAS MISSING. This used to answer "there was nothing to check" and call that
        // grounded, so an agent could avoid the bound entirely by citing nothing and act on evidence nobody
        // can go back and verify - an ABSENCE read as a positive result.
        //
        // The two halves are kept apart rather than merged. Nothing this reason said was CONTRADICTED, so
        // it is not a mismatch and a decline built on it is recorded as it happened. But it cited nothing,
        // so it cannot carry an act. And the statement says which, because "nothing was cited" is a
        // different fact from "the check never ran", and a record that was blank in both could not tell
        // them apart.
        var verdict = RuleReasonGrounding.Check(
            "the session is stopped on an allowance notice and the instruction covers it.", TheScreen);

        Assert.True(verdict.IsGrounded);
        Assert.False(verdict.HasCitation);
        Assert.False(verdict.CanCarryAnAct);
        Assert.NotEqual("", verdict.Statement);
        Assert.Contains("cites nothing", verdict.Statement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_reason_that_cites_the_screen_faithfully_can_carry_an_act()
    {
        // THE PRESENCE. A rule that refused every act would pass every assertion above while making the
        // feature incapable of ever doing anything.
        var verdict = RuleReasonGrounding.Check(
            "the screen says 'Claude usage limit reached', which is the notice the instruction is about.",
            TheScreen);

        Assert.True(verdict.HasCitation);
        Assert.True(verdict.CanCarryAnAct);
    }

    [Fact]
    public void A_reason_that_cites_something_the_screen_does_not_contain_cannot_carry_an_act()
    {
        var verdict = RuleReasonGrounding.Check(
            "the screen says 'THE SCREEN HAS MOVED ON WHILE THE RULE WAS THINKING'.", TheScreen);

        Assert.True(verdict.HasCitation);
        Assert.False(verdict.CanCarryAnAct);
    }

    [Fact]
    public void A_reason_that_is_missing_altogether_is_not_silently_grounded()
    {
        var verdict = RuleReasonGrounding.Check(null, TheScreen);
        Assert.NotEqual("", verdict.Statement);
    }

    [Fact]
    public void A_screen_that_could_not_be_read_never_grounds_a_quotation()
    {
        // If the screen is empty, a quoted passage cannot have come from it. Answering "grounded" here
        // would be the fail-open: an absent screen certifying every claim made about it.
        var verdict = RuleReasonGrounding.Check("the screen says 'Claude usage limit reached'.", "");

        Assert.False(verdict.IsGrounded);
        Assert.NotEqual("", verdict.Statement);
    }

    [Fact]
    public void A_one_word_quotation_is_not_treated_as_a_claim_about_the_screen()
    {
        // A reason that puts the word 'act' in quotes is talking about its own decision, not quoting the
        // terminal, and matching a three-letter string against forty lines of output would be noise in
        // both directions.
        var verdict = RuleReasonGrounding.Check("the decision is 'act' because the notice is present.", TheScreen);

        Assert.True(verdict.IsGrounded, verdict.Statement);

        // And because it is not read as a claim about the screen, it is not read as a CITATION either - so
        // a three-letter quotation cannot be what lets an act through.
        Assert.False(verdict.HasCitation);
        Assert.False(verdict.CanCarryAnAct);
    }

    [Fact]
    public void The_typographic_quotation_marks_a_model_writes_without_being_asked_are_read_too()
    {
        var reason = "the screen says \u201Cwhat is definitely not there at all\u201D.";

        var verdict = RuleReasonGrounding.Check(reason, TheScreen);

        Assert.False(verdict.IsGrounded);
    }

    // ---- ruling G1: an apostrophe is not a quotation mark ---------------------------------------------
    //
    // On a live Windows run the usage-limit rule quoted the screen correctly - 'You've reached your Fable 5
    // limit' - and the act was refused as ungrounded: the apostrophe inside "You've" closed the quotation,
    // and the next pair of single quotes produced a passage that is not on the screen. The notice the rule
    // exists for contains "You've", so the rule could not act on its own target text.

    /// <summary>Firing 2's stored screen, byte for byte, from
    /// docs/missions/session-rules-hooks-2026-10-18/evidence/windows-rows-usage-limit-firings.json.</summary>
    private const string FiringTwoScreen =
        "2024-04-01  03:22 AM             4,127 wait.svg\n" +
        "2024-04-01  03:22 AM             8,774 wait_i.cur\n" +
        "2024-04-01  03:22 AM             8,774 wait_il.cur\n" +
        "2024-04-01  03:22 AM             8,774 wait_im.cur\n" +
        "2024-04-01  03:22 AM             8,774 wait_l.cur\n" +
        "2024-04-01  03:22 AM             8,774 wait_m.cur\n" +
        "2024-04-01  03:22 AM             8,774 wait_r.cur\n" +
        "2024-04-01  03:22 AM             8,774 wait_rl.cur\n" +
        "2024-04-01  03:22 AM             8,774 wait_rm.cur\n" +
        "             206 File(s)     11,501,377 bytes\n" +
        "\n" +
        "     Total Files Listed:\n" +
        "             206 File(s)     11,501,377 bytes\n" +
        "               2 Dir(s)  110,675,312,640 bytes free\n" +
        "\n" +
        "Pinging 127.0.0.1 with 32 bytes of data:\n" +
        "Reply from 127.0.0.1: bytes=32 time<1ms TTL=128\n" +
        "Reply from 127.0.0.1: bytes=32 time<1ms TTL=128\n" +
        "Reply from 127.0.0.1: bytes=32 time<1ms TTL=128\n" +
        "Reply from 127.0.0.1: bytes=32 time<1ms TTL=128\n" +
        "Reply from 127.0.0.1: bytes=32 time<1ms TTL=128\n" +
        "Reply from 127.0.0.1: bytes=32 time<1ms TTL=128\n" +
        "\n" +
        "Ping statistics for 127.0.0.1:\n" +
        "    Packets: Sent = 6, Received = 6, Lost = 0 (0% loss),\n" +
        "Approximate round trip times in milli-seconds:\n" +
        "    Minimum = 0ms, Maximum = 0ms, Average = 0ms\n" +
        "You've reached your Fable 5 limit. Run /usage-credits to continue or switch models with /model.\n" +
        "\n" +
        "C:\\Users\\soren\\AppData\\Local\\session-rules-rig-win\\repos\\repo-b>";

    /// <summary>Firing 2's stated reason, byte for byte, from the same record.</summary>
    private const string FiringTwoReason =
        "The screen explicitly reports the session has run out of its model allowance ('You've reached your Fable 5 limit'), which matches the instruction's trigger condition. The /usage-credits command is the one that shows what is left.";

    [Fact]
    public void Check_FiringTwoExactReasonAgainstItsOwnScreen_IsGroundedAndCanCarryAnAct()
    {
        var verdict = RuleReasonGrounding.Check(FiringTwoReason, FiringTwoScreen);

        Assert.True(verdict.IsGrounded, verdict.Statement);
        Assert.True(verdict.HasCitation);
        Assert.True(verdict.CanCarryAnAct);
        Assert.Empty(verdict.NotOnTheScreen);
    }

    [Fact]
    public void QuotedPassages_FiringTwoReason_ReadsTheWholeQuotationAndNothingElse()
    {
        var passages = RuleReasonGrounding.QuotedPassages(FiringTwoReason);

        Assert.Equal(new[] { "You've reached your Fable 5 limit" }, passages);
    }

    [Fact]
    public void Check_QuotationWithAnApostropheThatIsNotOnTheScreen_IsRefusedAndNamesThePassage()
    {
        // The gate may only become more accurate, never looser: a fabricated citation that contains an
        // apostrophe is still a fabricated citation.
        var verdict = RuleReasonGrounding.Check(
            "the screen says 'You've reached your Fable 5 limit', which is the notice.", TheScreen);

        Assert.False(verdict.IsGrounded);
        Assert.True(verdict.HasCitation);
        Assert.False(verdict.CanCarryAnAct);
        Assert.Equal("You've reached your Fable 5 limit", Assert.Single(verdict.NotOnTheScreen));
        Assert.Contains("'You've reached your Fable 5 limit'", verdict.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_ApostropheBeforeAFabricatedQuotation_NoLongerHidesTheQuotation()
    {
        // Before G1 the apostrophe in "it's" opened a quotation, the real opening quote closed it, and the
        // real quotation was never read at all - an act built on it was refused only for citing nothing.
        // Now the fabricated passage itself is read, and refused by name.
        var verdict = RuleReasonGrounding.Check(
            "it's stopped, the screen says 'nothing like this is on the screen'.", TheScreen);

        Assert.False(verdict.IsGrounded);
        Assert.Equal("nothing like this is on the screen", Assert.Single(verdict.NotOnTheScreen));
    }

    [Fact]
    public void QuotedPassages_PossessiveApostropheAtTheEndOfAWord_StillClosesTheQuotation()
    {
        // G1 needs a letter or digit on BOTH sides. A plural possessive ("the agents' turn") has a letter on
        // one side only, so it is read as a closing quotation mark: the passage is "the agents". This is the
        // decision, pinned. Reading it as an apostrophe instead would need a guess about the writer's
        // grammar, and guessing wrong would swallow the next real quotation. Checking the shorter passage
        // is no looser than before: it must still be on the screen.
        var passages = RuleReasonGrounding.QuotedPassages("the screen says 'the agents' turn' now.");

        Assert.Equal(new[] { "the agents" }, passages);
    }

    [Fact]
    public void Check_PossessiveQuotationNotOnTheScreen_IsRefused()
    {
        var verdict = RuleReasonGrounding.Check("the screen says 'the agents' turn' now.", TheScreen);

        Assert.False(verdict.IsGrounded);
        Assert.Equal("the agents", Assert.Single(verdict.NotOnTheScreen));
    }

    [Fact]
    public void Check_CurlyQuotationWithACurlyApostropheOnTheScreen_IsGrounded()
    {
        // The typographic equivalent: U+2018 opens, U+2019 closes, and U+2019 between two letters is the
        // apostrophe a model writes in "You\u2019ve".
        var screen = "notice\nYou\u2019ve reached your Fable 5 limit. Run /usage-credits to continue.\n>";
        var reason = "the screen says \u2018You\u2019ve reached your Fable 5 limit\u2019, the instruction\u2019s trigger.";

        Assert.Equal(new[] { "You\u2019ve reached your Fable 5 limit" }, RuleReasonGrounding.QuotedPassages(reason));

        var verdict = RuleReasonGrounding.Check(reason, screen);

        Assert.True(verdict.IsGrounded, verdict.Statement);
        Assert.True(verdict.CanCarryAnAct);
    }

    [Fact]
    public void Check_CurlyQuotationWithAnApostropheThatIsNotOnTheScreen_IsRefusedAndNamesThePassage()
    {
        var reason = "the screen says \u2018You\u2019ve reached your Fable 5 limit\u2019, the instruction\u2019s trigger.";

        var verdict = RuleReasonGrounding.Check(reason, TheScreen);

        Assert.False(verdict.IsGrounded);
        Assert.Equal("You\u2019ve reached your Fable 5 limit", Assert.Single(verdict.NotOnTheScreen));
    }

    [Fact]
    public void Check_CurlyApostropheQuotedAgainstAStraightApostropheOnTheScreen_IsStillRefused()
    {
        // Pinned as the gate stands, outside G1: the comparison is character for character, so a curly
        // apostrophe in the citation does not match a straight one on the screen. Changing that is a
        // separate decision, raised in the answers file.
        var reason = "the screen says \u2018You\u2019ve reached your Fable 5 limit\u2019.";

        var verdict = RuleReasonGrounding.Check(reason, FiringTwoScreen);

        Assert.False(verdict.IsGrounded);
    }
}
