using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// The phase-one turn-detection rule, decided here as a pure function - no session, no terminal
/// buffer, no clock.
///
/// EVERY CONDITION IS TESTED IN BOTH DIRECTIONS. A filter that rejects everything would pass a
/// file full of "this row does not count" assertions and would also switch a session's colour off
/// permanently, so each condition is paired with a case that must still be reported as new. The
/// negative control at the bottom runs a row of genuinely new prose past ALL FOUR filters at once
/// with the real marker list in place.
/// </summary>
public sealed class TerminalContentNoveltyTests
{
    // A plausible settled screen: the agent has answered, and its own footer is drawn below.
    private static readonly string[] SettledScreen =
    [
        "> summarise the deployment",
        "",
        "  The deployment finished successfully.",
        "  Three services were restarted.",
        "",
        "  Reticulating splines for the report",
        "  esc to interrupt - 12:04 - 43% context left",
    ];

    private static readonly string[] ClaudeCodeMarkers =
    [
        "esc to interrupt", "context left", "Checking for updates", "Update installed",
    ];

    private static string[] SettledPlus(params string[] extraRows)
    {
        var rows = new List<string>(SettledScreen);
        rows.InsertRange(4, extraRows);
        return rows.ToArray();
    }

    // ------------------------------------------------------------------------------------------
    // Condition one: a row needs at least three letters or digits
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_row_of_box_drawing_and_punctuation_is_not_gained_content()
    {
        // Two letters, and the rest is border. Below the substance floor, so it does not count
        // however new it is.
        var current = SettledPlus("  +------------------------+", "  | ok |");

        Assert.False(TerminalContentNovelty.GainedContent(
            SettledScreen, current, ClaudeCodeMarkers, out var row));
        Assert.Null(row);
    }

    [Fact]
    public void A_row_with_exactly_three_letters_is_gained_content()
    {
        // The other direction: the floor is three, so three passes. Without this the test above
        // would also pass against a filter that rejected every row on earth.
        var current = SettledPlus("  abc");

        Assert.True(TerminalContentNovelty.GainedContent(
            SettledScreen, current, ClaudeCodeMarkers, out var row));
        Assert.Equal("  abc", row);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("  ---===---  ", 0)]
    [InlineData("  ab  ", 2)]
    [InlineData("  a1b2  ", 4)]
    public void Substance_counts_only_letters_and_digits(string row, int expected)
    {
        Assert.Equal(expected, TerminalContentNovelty.Substance(row));
    }

    // ------------------------------------------------------------------------------------------
    // Condition two: the agent's own chrome never counts
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_row_carrying_one_of_the_agents_markers_is_not_gained_content()
    {
        // Claude Code's update notice: a row the product draws about itself, thirty minutes into
        // an idle session. This is the row the whole phase exists to stop turning a session blue.
        var current = SettledPlus("  Update installed - restart to apply");

        Assert.False(TerminalContentNovelty.GainedContent(
            SettledScreen, current, ClaudeCodeMarkers, out var row));
        Assert.Null(row);
    }

    [Fact]
    public void The_same_row_counts_when_the_agent_declares_no_markers()
    {
        // The other direction, and the reason the markers are a per-driver trait: an agent that
        // declares nothing is exactly as well off as it is today, never worse. The row is filtered
        // because of the LIST, not because of anything intrinsic to it.
        var current = SettledPlus("  Update installed - restart to apply");

        Assert.True(TerminalContentNovelty.GainedContent(
            SettledScreen, current, System.Array.Empty<string>(), out var row));
        Assert.Equal("  Update installed - restart to apply", row);
    }

    [Fact]
    public void Markers_match_whatever_case_the_agent_drew_them_in()
    {
        var current = SettledPlus("  UPDATE INSTALLED - restart to apply");

        Assert.False(TerminalContentNovelty.GainedContent(
            SettledScreen, current, ClaudeCodeMarkers, out _));
    }

    // ------------------------------------------------------------------------------------------
    // Condition three: the key is letters only, so digits and drawing glyphs are invisible
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_ticking_clock_is_not_gained_content()
    {
        // The same footer row one minute later. Every character that changed was a digit, so the
        // key is identical and the row is the settled screen redrawn. Note this row also carries a
        // marker - it is tested here with NO markers supplied, so the pass is condition three
        // doing the work and not condition two standing in for it.
        var settled = new[] { "  esc to interrupt - 12:04 - 43% context left" };
        var current = new[] { "  esc to interrupt - 12:05 - 43% context left" };

        Assert.False(TerminalContentNovelty.GainedContent(
            settled, current, System.Array.Empty<string>(), out var row));
        Assert.Null(row);
    }

    [Fact]
    public void A_row_whose_letters_changed_is_gained_content()
    {
        // The other direction: change a letter rather than a digit and the key moves, so the same
        // machinery reports it. Condition three suppresses numbers, not content.
        var settled = new[] { "  the build is running" };
        var current = new[] { "  the build has completed and the artefacts are published" };

        Assert.True(TerminalContentNovelty.GainedContent(
            settled, current, System.Array.Empty<string>(), out var row));
        Assert.Equal("  the build has completed and the artefacts are published", row);
    }

    [Theory]
    [InlineData("  esc to interrupt - 12:04 - 43% context left", "esctointerruptcontextleft")]
    [InlineData("[2026-09-15] DONE", "done")]
    [InlineData("12345", "")]
    public void The_key_is_lower_case_letters_only(string row, string expected)
    {
        Assert.Equal(expected, TerminalContentNovelty.Key(row));
    }

    // ------------------------------------------------------------------------------------------
    // Condition four: a near-duplicate at eighty percent is the same row redrawn
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_torn_repaint_of_a_settled_row_is_not_gained_content()
    {
        // The terminal redrew a row and dropped a character on the way. Its key is not identical,
        // so condition three lets it through; at 0.98 similarity condition four catches it. This
        // filter was the single largest improvement in the measurement behind the rule.
        var current = SettledPlus("  The deploymnt finished successfully.");

        Assert.False(TerminalContentNovelty.GainedContent(
            SettledScreen, current, ClaudeCodeMarkers, out var row));
        Assert.Null(row);
    }

    [Fact]
    public void A_different_row_of_the_same_length_is_gained_content()
    {
        // The other direction, and the one that matters: a row of the same shape and length as a
        // settled row, carrying different words, must NOT be swallowed as a repaint. At 0.30
        // similarity it is nowhere near the threshold.
        var current = SettledPlus("  The migration completed with warnings.");

        Assert.True(TerminalContentNovelty.GainedContent(
            SettledScreen, current, ClaudeCodeMarkers, out var row));
        Assert.Equal("  The migration completed with warnings.", row);
    }

    [Fact]
    public void The_length_band_only_skips_rows_that_could_never_reach_the_threshold()
    {
        // The band exists for speed: a settled key far shorter or far longer than the candidate is
        // not compared at all. That is only safe while every skipped row was already unreachable,
        // and the ratio cannot exceed 2 * min(lengths) / (sum of lengths). This walks the whole
        // range of key lengths a terminal row can produce and proves the bound holds at 0.80, so
        // anyone lowering the threshold finds out here that the band starts deciding things.
        for (int candidate = 1; candidate <= 400; candidate++)
        {
            int band = System.Math.Max(8, candidate / 2);

            int justTooLong = candidate + band + 1;
            double bestIfLong = 2.0 * candidate / (candidate + justTooLong);
            Assert.True(bestIfLong < TerminalContentNovelty.NearDuplicateSimilarity,
                $"a settled key of {justTooLong} could reach {bestIfLong} against a candidate of {candidate}");

            int justTooShort = candidate - band - 1;
            if (justTooShort < 0) continue;
            double bestIfShort = 2.0 * justTooShort / (candidate + justTooShort);
            Assert.True(bestIfShort < TerminalContentNovelty.NearDuplicateSimilarity,
                $"a settled key of {justTooShort} could reach {bestIfShort} against a candidate of {candidate}");
        }
    }

    // ------------------------------------------------------------------------------------------
    // The negative control: all four filters active, and real work still opens the turn
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Genuinely_new_prose_is_gained_content_with_every_filter_active()
    {
        // The whole rule, at once, against the case it must never get wrong: the agent came back
        // with an answer. If this ever goes red, the owner has a session that finished and never
        // told him - which is a worse failure than the phantom blue this phase exists to remove.
        var current = SettledPlus(
            "  I have finished the refactor. The nine call sites now go through one",
            "  helper, and the two tests that covered the old shape were rewritten.");

        Assert.True(TerminalContentNovelty.GainedContent(
            SettledScreen, current, ClaudeCodeMarkers, out var row));
        Assert.Equal("  I have finished the refactor. The nine call sites now go through one", row);
    }

    // ------------------------------------------------------------------------------------------
    // The load-bearing numbers, pinned so they cannot move under a green suite
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_near_duplicate_threshold_is_eighty_percent_and_a_verdict_turns_on_it()
    {
        // Pinned in BOTH directions and independently of the value under test. The examples
        // elsewhere in this file sit at about 0.98 and 0.30, so raising the threshold to 0.90 or
        // dropping it to 0.70 left every one of them passing: the number was free to move. These
        // two rows bracket it - one similar enough to be the same row redrawn, one not - and their
        // ratios are asserted first, from the ratio function, so this test pins the THRESHOLD
        // rather than restating it.
        var settled = new[] { "  the build is running on the agent" };
        const string redrawn = "  the build was running on the other agent";
        const string different = "  the build is queued on the agent";

        double redrawnRatio = TerminalContentNovelty.SequenceRatio(
            TerminalContentNovelty.Key(settled[0]), TerminalContentNovelty.Key(redrawn));
        double differentRatio = TerminalContentNovelty.SequenceRatio(
            TerminalContentNovelty.Key(settled[0]), TerminalContentNovelty.Key(different));

        Assert.True(redrawnRatio > 0.80 && redrawnRatio < 0.90,
            $"the near-duplicate example must sit between 0.80 and 0.90 to pin the threshold, and is {redrawnRatio}");
        Assert.True(differentRatio > 0.70 && differentRatio < 0.80,
            $"the different-row example must sit between 0.70 and 0.80 to pin the threshold, and is {differentRatio}");

        // At 0.80 the first is a repaint and the second is not. At 0.90 the first would be new
        // content; at 0.70 the second would be swallowed as a repaint.
        Assert.False(TerminalContentNovelty.GainedContent(
            settled, new[] { settled[0], redrawn }, System.Array.Empty<string>(), out _),
            "a row this close to a settled row is the same row redrawn");
        Assert.True(TerminalContentNovelty.GainedContent(
            settled, new[] { settled[0], different }, System.Array.Empty<string>(), out var row),
            "a row this far from a settled row is new content");
        Assert.Equal(different, row);

        // Last, and deliberately last: the literal. Put first it would fire on every mutation and
        // hide whether the two verdicts above actually turn on this number.
        Assert.Equal(0.80, TerminalContentNovelty.NearDuplicateSimilarity, 10);
    }

    [Fact]
    public void The_size_rule_starts_at_two_hundred_changed_characters()
    {
        // The starting threshold was self-referenced rather than pinned: the one test that used it
        // fed it a sample of exactly 201 characters, so moving the constant to 201 changed nothing,
        // and the boundary test built its own unrelated threshold. This walks the real boundary
        // with the shipped rule, and asserts the magnitudes first so the boundary is independent of
        // the number it is pinning.
        var under = SettledPlus("  " + new string('u', 197));
        var at = SettledPlus("  " + new string('a', 198));

        Assert.Equal(199, TerminalContentNovelty.ChangedCharacters(SettledScreen, under));
        Assert.Equal(200, TerminalContentNovelty.ChangedCharacters(SettledScreen, at));

        var rule = TerminalContentNovelty.StartingSizeRule();
        Assert.Equal(TerminalContentNovelty.StartingChangedCharacterThreshold, rule.Threshold);
        Assert.False(rule.GainedContent(SettledScreen, under, System.Array.Empty<string>(), out _),
            "199 changed characters is under the starting threshold");
        Assert.True(rule.GainedContent(SettledScreen, at, System.Array.Empty<string>(), out _),
            "200 changed characters is at the starting threshold");

        // Last, for the same reason as the near-duplicate threshold above.
        Assert.Equal(200, TerminalContentNovelty.StartingChangedCharacterThreshold);
    }

    [Fact]
    public void The_size_rules_magnitude_is_the_measurement_its_verdict_was_taken_from()
    {
        // The interface promise: the number written into the log comes from the same object that
        // ruled on it, so the two can never describe different functions.
        var current = SettledPlus("  The migration completed with warnings.");
        var rule = TerminalContentNovelty.SizeRule(10);

        int magnitude = rule.Measure(SettledScreen, current);
        Assert.Equal(TerminalContentNovelty.ChangedCharacters(SettledScreen, current), magnitude);
        Assert.True(rule.GainedContent(SettledScreen, current, System.Array.Empty<string>(), out var evidence));
        Assert.Equal($"changed {magnitude} characters", evidence);
    }

    [Fact]
    public void The_settling_window_and_its_cap_are_the_numbers_that_ship()
    {
        // Every behaviour test injects its own timings, so nothing pinned the production pair. A
        // reader who wants to know how long a repaint is given to finish drawing, and how long a
        // chattering agent can defer its own judgement, reads it here.
        Assert.Equal(System.TimeSpan.FromMilliseconds(400), TerminalStateDetector.SettleCheckDelay);
        Assert.Equal(System.TimeSpan.FromSeconds(3), TerminalStateDetector.MaxSettleCheckDeferral);
        Assert.True(TerminalStateDetector.MaxSettleCheckDeferral > TerminalStateDetector.SettleCheckDelay,
            "a cap at or below the window would judge every burst half drawn");
    }

    [Fact]
    public void An_unchanged_screen_is_not_gained_content()
    {
        Assert.False(TerminalContentNovelty.GainedContent(
            SettledScreen, SettledScreen, ClaudeCodeMarkers, out var row));
        Assert.Null(row);
    }

    [Fact]
    public void A_first_screen_with_no_settled_side_is_all_gained_content()
    {
        // Nothing was settled, so the first row with substance is new.
        //
        // A comment here used to say the detector never asks the rule with no settled side. That
        // was false - a settle whose screen could not be read leaves no baseline at all - and the
        // detector no longer asks: it calls that frame ambiguous and opens the turn, because a
        // real reply scored against an empty baseline can fall under the size threshold and hold a
        // session red with nothing left to ask again. The function still answers honestly here.
        Assert.True(TerminalContentNovelty.GainedContent(
            System.Array.Empty<string>(), SettledScreen, ClaudeCodeMarkers, out var row));
        Assert.Equal("> summarise the deployment", row);
    }

    [Fact]
    public void Blank_rows_are_neither_candidates_nor_settled_keys()
    {
        var settled = new[] { "   ", "", "  the answer" };
        var current = new[] { "", "  the answer", "    " };

        Assert.False(TerminalContentNovelty.GainedContent(
            settled, current, System.Array.Empty<string>(), out _));
    }

    // ------------------------------------------------------------------------------------------
    // The second candidate: a threshold on how much text changed
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_size_rule_sees_nothing_in_an_unchanged_screen()
    {
        Assert.Equal(0, TerminalContentNovelty.ChangedCharacters(SettledScreen, SettledScreen));
    }

    [Fact]
    public void The_size_rule_counts_the_characters_of_the_rows_that_appeared()
    {
        const string added = "  The migration completed with warnings.";
        var current = SettledPlus(added);

        Assert.Equal(added.Length, TerminalContentNovelty.ChangedCharacters(SettledScreen, current));
    }

    [Fact]
    public void The_size_rule_counts_a_row_the_terminal_redrew_as_changed()
    {
        // Row alignment is coarse ON PURPOSE and this is where it shows: one dropped character
        // makes the whole row unmatched, so a torn repaint reads as 37 changed characters rather
        // than one. That is the honest weakness of the size candidate, and it is why the choice
        // between the two is made on live bytes rather than asserted here.
        var settled = new[] { "  The deployment finished successfully." };
        var current = new[] { "  The deploymnt finished successfully." };

        Assert.Equal(current[0].Length,
            TerminalContentNovelty.ChangedCharacters(settled, current));
    }

    [Fact]
    public void The_size_rule_opens_a_turn_only_at_or_above_its_threshold()
    {
        const string added = "  The migration completed with warnings.";
        var current = SettledPlus(added);

        var under = TerminalContentNovelty.SizeRule(added.Length + 1);
        Assert.False(under.GainedContent(SettledScreen, current, System.Array.Empty<string>(), out var none));
        Assert.Null(none);

        var at = TerminalContentNovelty.SizeRule(added.Length);
        Assert.True(at.GainedContent(SettledScreen, current, System.Array.Empty<string>(), out var evidence));
        Assert.Equal($"changed {added.Length} characters", evidence);
    }

    [Fact]
    public void The_size_rule_ignores_the_marker_list()
    {
        // Its selling point and its weakness in one test: no list to keep current, and therefore
        // no way to tell an agent's own update notice from the conversation gaining a line.
        var current = SettledPlus("  Update installed - restart to apply, and here is some padding");

        var rule = TerminalContentNovelty.SizeRule(10);
        Assert.True(rule.GainedContent(SettledScreen, current, ClaudeCodeMarkers, out _));
    }

    // ------------------------------------------------------------------------------------------
    // One interface, so item five can score the two against each other
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Both_candidates_answer_through_one_interface()
    {
        var candidates = new List<ITerminalNoveltyRule>
        {
            TerminalContentNovelty.RowRule,
            TerminalContentNovelty.SizeRule(TerminalContentNovelty.StartingChangedCharacterThreshold),
        };

        // Long enough to clear the size candidate's starting threshold as well as the row rule.
        // A reply this size is what both candidates are meant to agree about.
        var current = SettledPlus(
            "  I have finished the refactor. The nine call sites now go through one",
            "  helper, and the two tests that covered the old shape were rewritten.",
            "  Nothing else in the module changed, and the suite is green.");

        foreach (var rule in candidates)
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Name));
            Assert.True(rule.GainedContent(SettledScreen, current, ClaudeCodeMarkers, out var evidence),
                $"{rule.Name} should see a real reply as gained content");
            Assert.False(string.IsNullOrWhiteSpace(evidence));

            Assert.False(rule.GainedContent(SettledScreen, SettledScreen, ClaudeCodeMarkers, out var quiet),
                $"{rule.Name} should see nothing in an unchanged screen");
            Assert.Null(quiet);
        }
    }

    [Fact]
    public void A_rule_name_is_plain_ascii_because_it_is_written_to_the_log()
    {
        foreach (var rule in new ITerminalNoveltyRule[]
                 { TerminalContentNovelty.RowRule, TerminalContentNovelty.SizeRule(200) })
        {
            foreach (char c in rule.Name)
                Assert.InRange(c, ' ', '~');
        }
    }

    [Fact]
    public void The_two_candidates_disagree_about_an_agents_own_update_notice()
    {
        // The reason both are being carried rather than one being chosen now. On exactly the row
        // this phase exists to suppress, the row rule holds red and the size rule opens the turn.
        // Neither is assumed right here; the choice is made on live bytes in work item five.
        var current = SettledPlus(
            "  Update installed - restart to apply this version of the command line tool");

        Assert.False(TerminalContentNovelty.RowRule.GainedContent(
            SettledScreen, current, ClaudeCodeMarkers, out _));
        Assert.True(TerminalContentNovelty.SizeRule(20).GainedContent(
            SettledScreen, current, ClaudeCodeMarkers, out _));
    }
}
