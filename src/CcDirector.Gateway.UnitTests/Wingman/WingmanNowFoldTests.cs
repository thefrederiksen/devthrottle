using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// <see cref="WingmanNowFold"/>, the one fold behind the Wingman tab's Now view (the Wingman tab, version 3, item 1):
/// the state a row is in, the pill's words, the timed sentence, the agent's own sentence and whole reply, what the
/// session needs, and whether an option may still be tapped.
///
/// THE ROWS HERE ARE BUILT BY HAND, and that is right for a pure fold and nothing else: what these prove is what the
/// fold says about a row. That a real row carries the colour and label this fold copies is proven by the roster fold's
/// own tests, and that the route really hands this fold a real row and a real store is proven through the handler in
/// <see cref="WingmanNowRouteTests"/>, not here.
///
/// WHAT SLICE 1 DOES NOT COVER, said plainly rather than implied by silence: reading, failed and switched off (slice
/// 2), the carrying-on deadline sentence (slice 3), working and just answered (slices 4 and 5), and the voice control
/// (slice 6). A row in one of those states folds to "other" today, and no test here pins that - pinning an interim
/// answer would only have to be deleted by the slice that gives it its real one.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class WingmanNowFoldTests
{
    private const string Sid = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTime Stopped = new(2026, 9, 17, 11, 12, 0, DateTimeKind.Utc);

    private static TurnVerdictDto Verdict(
        string word,
        string confidence = "high",
        string? finishedKind = null,
        string label = "Merge pull request 3002, or allow me to merge it",
        string summary = "The release notes are pushed and the merge command was refused by a permission check.",
        string evidence = "Either merge 3002 yourself, or allow that command and I will do it.",
        string? recommends = "allow the merge - the notes have been reviewed",
        string? menuQuestion = null,
        int options = 0) => new()
    {
        VerdictId = "verdict-1",
        JudgedAtUtc = Stopped.AddSeconds(4),
        TurnEndObservedAtUtc = Stopped,
        Verdict = word,
        Confidence = confidence,
        FinishedKind = finishedKind,
        Label = label,
        Summary = summary,
        Evidence = evidence,
        AgentRecommends = recommends,
        Menu = menuQuestion is null ? null : new TurnVerdictMenuDto { Question = menuQuestion },
        Options = Enumerable.Range(0, options).Select(i => new TurnVerdictOptionDto
        {
            Key = "Option " + i,
            Note = "What option " + i + " does.",
            Send = i.ToString(),
            Recommended = i == 0,
        }).ToList(),
    };

    /// <summary>A stopped row the roster fold has already coloured and labelled, carrying the verdict in force.</summary>
    private static SessionDto Row(TurnVerdictDto? verdict, string colour = "red", string label = "Needs you") => new()
    {
        SessionId = Sid,
        Name = "Wingman Inspector - Manager",
        AgentToolDisplay = "Claude Code",
        ActivityState = "WaitingForInput",
        WaitingSince = Stopped,
        EffectiveColor = colour,
        EffectiveColorHex = "#ef4444",
        StateLabel = label,
        VerdictState = verdict is null ? VerdictStates.None : VerdictStates.Judged,
        TurnVerdict = verdict,
    };

    private static WingmanNowResponse Fold(SessionDto? row, TurnVerdictDto? verdict,
        DateTime? answeredAt = null, WingmanNowConversation? conversation = null)
    {
        var history = verdict is null
            ? Array.Empty<AnsweredTurnVerdict>()
            : new[] { new AnsweredTurnVerdict(verdict, answeredAt) };
        return WingmanNowFold.Fold(new WingmanNowInputs(Sid, row, history, conversation));
    }

    // ---------------------------------------------------------------- needs you

    [Fact]
    public void A_stop_that_needs_a_person_is_needs_you_with_the_wingmans_own_words()
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou, options: 2);
        var now = Fold(Row(verdict), verdict);

        Assert.Equal(WingmanNowStates.NeedsYou, now.State);
        Assert.Equal("Needs you", now.PillText);
        Assert.Equal("Merge pull request 3002, or allow me to merge it", now.Headline);
        Assert.Equal("The release notes are pushed and the merge command was refused by a permission check.", now.Story);
        Assert.Equal("Claude Code said", now.AgentSaid!.Who);
        Assert.Equal("Either merge 3002 yourself, or allow that command and I will do it.", now.AgentSaid.Text);
        Assert.Equal("What it needs from you", now.Needs!.Heading);
        Assert.Equal("It recommends: allow the merge - the notes have been reviewed", now.Needs.Recommends);
        Assert.Equal(WingmanNowFold.ReplyPlaceholderNeedsYou, now.ReplyPlaceholder);
        Assert.Null(now.CalmCard);
        Assert.False(now.Unsure);
    }

    /// <summary>The pill wears the ROW's colour, not one this fold worked out - so it cannot disagree with the dot
    /// beside the same session in the Sessions list.</summary>
    [Fact]
    public void The_pill_wears_the_rows_own_colour_and_never_one_of_its_own()
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou);
        var row = Row(verdict);
        row.EffectiveColor = "a-colour-this-fold-has-never-heard-of";
        row.EffectiveColorHex = "#123456";

        var now = Fold(row, verdict);

        Assert.Equal("a-colour-this-fold-has-never-heard-of", now.PillColour);
        Assert.Equal("#123456", now.PillColourHex);
    }

    [Fact]
    public void The_stopped_moment_is_the_verdicts_own_and_says_how_long_ago_it_was()
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou);
        var now = Fold(Row(verdict), verdict);

        Assert.Equal("Stopped at", now.When!.Lead);
        Assert.Equal(Stopped, now.When.AtUtc);
        Assert.True(now.When.ShowAgo);
    }

    /// <summary>A verdict stored before the turn-end moment was recorded still has a moment to show: the row's own
    /// waiting stamp. The alternative is a view with no time on it at all.</summary>
    [Fact]
    public void A_verdict_with_no_observed_moment_falls_back_to_the_rows_waiting_stamp()
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou);
        verdict.TurnEndObservedAtUtc = default;
        var row = Row(verdict);
        row.WaitingSince = Stopped.AddMinutes(-3);

        var now = Fold(row, verdict);

        Assert.Equal(Stopped.AddMinutes(-3), now.When!.AtUtc);
    }

    [Fact]
    public void The_options_carry_the_position_the_answer_route_takes_and_the_recommended_mark()
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou, options: 2, menuQuestion: "Which one?");
        var now = Fold(Row(verdict), verdict);

        Assert.Equal("Which one?", now.Needs!.Question);
        Assert.Collection(now.Needs.Options,
            first =>
            {
                Assert.Equal(0, first.Index);
                Assert.Equal("Option 0", first.Key);
                Assert.Equal("What option 0 does.", first.Note);
                Assert.True(first.Recommended);
            },
            second =>
            {
                Assert.Equal(1, second.Index);
                Assert.False(second.Recommended);
            });
        Assert.Equal("verdict-1", now.VerdictId);
        Assert.True(now.CanAnswerByOption);
    }

    /// <summary>Each gate alone closes the one-tap path, and each leaves the options readable and the reply box open.</summary>
    [Theory]
    [InlineData(1, false, false)]   // one option is not a choice
    [InlineData(2, true, false)]    // already answered
    [InlineData(2, false, true)]    // the verdict in force is not in the history window, so it cannot be checked
    public void An_option_cannot_be_tapped_unless_it_is_unanswered_and_a_real_choice(
        int options, bool answered, bool missingFromHistory)
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou, options: options);
        var history = missingFromHistory
            ? Array.Empty<AnsweredTurnVerdict>()
            : new[] { new AnsweredTurnVerdict(verdict, answered ? Stopped.AddMinutes(2) : null) };

        var now = WingmanNowFold.Fold(new WingmanNowInputs(Sid, Row(verdict), history, null));

        Assert.False(now.CanAnswerByOption);
        Assert.Equal(options, now.Needs!.Options.Count);
        Assert.Equal(WingmanNowFold.ReplyPlaceholderNeedsYou, now.ReplyPlaceholder);
    }

    [Fact]
    public void A_stop_the_wingman_could_not_read_is_still_put_in_front_of_the_owner()
    {
        var verdict = Verdict(TurnVerdictVocabulary.CannotTell, evidence: "");
        var now = Fold(Row(verdict), verdict);

        Assert.Equal(WingmanNowStates.NeedsYou, now.State);
        Assert.True(now.Unsure);
        Assert.Null(now.AgentSaid);
    }

    [Fact]
    public void A_session_stuck_and_needing_a_person_is_needs_you()
        => Assert.Equal(WingmanNowStates.NeedsYou,
            Fold(Row(Verdict(TurnVerdictVocabulary.StuckNeedsPerson)), Verdict(TurnVerdictVocabulary.StuckNeedsPerson)).State);

    // ---------------------------------------------------------------- needs you, not sure

    [Fact]
    public void An_ambiguous_answer_is_tagged_not_sure_and_says_to_read_the_reply_first()
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou, confidence: "ambiguous", options: 2);
        var now = Fold(Row(verdict), verdict);

        Assert.Equal(WingmanNowStates.NeedsYou, now.State);
        Assert.True(now.Unsure);
        Assert.Equal("The Wingman is not sure", now.UnsureTag);
        Assert.Equal(
            "The Wingman is not sure this is a question for you. Check the reply above before answering.",
            now.UnsureLine);
        // The words above it are still shown: not sure is a warning about them, not a reason to hide them.
        Assert.NotNull(now.Headline);
        Assert.NotNull(now.Needs);
    }

    [Fact]
    public void A_confident_answer_carries_no_not_sure_wording_at_all()
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou);
        var now = Fold(Row(verdict), verdict);

        Assert.False(now.Unsure);
        Assert.Null(now.UnsureTag);
        Assert.Null(now.UnsureLine);
    }

    // ---------------------------------------------------------------- done and report

    [Fact]
    public void Finished_work_is_done_and_offers_no_reply_box()
    {
        var verdict = Verdict(TurnVerdictVocabulary.Finished, finishedKind: "done",
            label: "Release v2.5.0 is tagged and published");
        var now = Fold(Row(verdict, colour: "cyan", label: "Done"), verdict);

        Assert.Equal(WingmanNowStates.Done, now.State);
        Assert.Equal("Done", now.PillText);
        Assert.Equal("cyan", now.PillColour);
        Assert.Equal("The work is complete", now.CalmCard!.Heading);
        Assert.Equal("Nothing is needed from you. You can close this session when you are ready.", now.CalmCard.Body);
        Assert.Null(now.ReplyPlaceholder);
        Assert.Null(now.Needs);
        Assert.False(now.CanAnswerByOption);
    }

    [Fact]
    public void A_report_says_the_work_is_not_finished_and_keeps_the_reply_box()
    {
        var verdict = Verdict(TurnVerdictVocabulary.Finished, finishedKind: "report",
            label: "The hosted Gateway is already running the latest changes");
        var now = Fold(Row(verdict, colour: "cyan", label: "Report"), verdict);

        Assert.Equal(WingmanNowStates.Report, now.State);
        Assert.Equal("Report", now.PillText);
        Assert.Equal("Only telling you", now.CalmCard!.Heading);
        Assert.Equal("Nothing is needed from you, and the work is not finished yet.", now.CalmCard.Body);
        Assert.Equal(WingmanNowFold.ReplyPlaceholderReport, now.ReplyPlaceholder);
    }

    /// <summary>A verdict stored before the owner split "finished" into done and report keeps its old meaning rather
    /// than being read as the newer, narrower one.</summary>
    [Fact]
    public void Finished_with_no_kind_recorded_reads_as_done()
    {
        var verdict = Verdict(TurnVerdictVocabulary.Finished);
        Assert.Equal(WingmanNowStates.Done, Fold(Row(verdict, colour: "cyan", label: "Done"), verdict).State);
    }

    /// <summary>Nothing is pending on a finished stop, so a running "47 minutes ago" would read as pressure about a
    /// session that wants nothing.</summary>
    [Fact]
    public void A_finished_stop_shows_when_it_stopped_and_not_how_long_ago()
    {
        var verdict = Verdict(TurnVerdictVocabulary.Finished, finishedKind: "done");
        var now = Fold(Row(verdict, colour: "cyan", label: "Done"), verdict);

        Assert.Equal("Stopped at", now.When!.Lead);
        Assert.False(now.When.ShowAgo);
    }

    // ---------------------------------------------------------------- carrying on

    [Fact]
    public void A_session_carrying_on_alone_says_nothing_is_needed_and_offers_no_reply_box()
    {
        var verdict = Verdict(TurnVerdictVocabulary.ContinuesAlone,
            label: "Waiting for its Worker to finish the test run");
        var now = Fold(Row(verdict, colour: "purple", label: "Carrying on"), verdict);

        Assert.Equal(WingmanNowStates.CarryingOn, now.State);
        Assert.Equal("Carrying on", now.PillText);
        Assert.Equal("purple", now.PillColour);
        Assert.Equal("Waiting for its Worker to finish the test run", now.Headline);
        Assert.Equal("Nothing needed from you", now.CalmCard!.Heading);
        Assert.Null(now.ReplyPlaceholder);
        Assert.True(now.When!.ShowAgo);
    }

    /// <summary>The deadline sentence is slice 3's, and it must come from the same function the carrying-on clock
    /// expires on. Until it does, the card carries its heading and no approximation of the sentence.</summary>
    [Fact]
    public void The_carrying_on_card_has_no_deadline_sentence_until_the_clock_is_wired_in()
    {
        var verdict = Verdict(TurnVerdictVocabulary.ContinuesAlone);
        var now = Fold(Row(verdict, colour: "purple", label: "Carrying on"), verdict);

        Assert.Null(now.CalmCard!.Body);
    }

    // ---------------------------------------------------------------- the whole reply

    [Fact]
    public void The_whole_reply_is_the_newest_assistant_messages_text_and_nothing_a_tool_produced()
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou);
        var conversation = new WingmanNowConversation(true, new List<HistoryMessageDto>
        {
            Message("Assistant", ("Text", "An older reply.")),
            Message("User", ("Text", "Go on.")),
            Message("Assistant",
                ("Thinking", "Thinking that is not what it said."),
                ("Text", "I need help with three things."),
                ("ToolUse", "{\"command\":\"gh pr merge\"}"),
                ("Text", "The merge, the release gate, and the changelog.")),
        });

        var now = Fold(Row(verdict), verdict, conversation: conversation);

        Assert.Equal("I need help with three things.\n\nThe merge, the release gate, and the changelog.", now.WholeReply);
    }

    /// <summary>"Nothing stored yet" and "this agent tool cannot send a conversation" both leave the field null, and
    /// neither invents a reply.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void There_is_no_whole_reply_when_the_conversation_is_absent_or_unsupported(bool present)
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou);
        var conversation = present
            ? new WingmanNowConversation(false, new List<HistoryMessageDto> { Message("Assistant", ("Text", "Hidden.")) })
            : null;

        Assert.Null(Fold(Row(verdict), verdict, conversation: conversation).WholeReply);
    }

    // ---------------------------------------------------------------- other

    /// <summary>The state this fold has not been taught a drawing for. It says what the ROW says - never blank, and
    /// never a headline the Gateway made up.</summary>
    [Fact]
    public void A_row_with_no_verdict_wears_its_own_label_and_is_never_blank()
    {
        var row = Row(null, colour: "grey", label: "Snoozed");
        var now = Fold(row, null);

        Assert.Equal(WingmanNowStates.Other, now.State);
        Assert.Equal("Snoozed", now.PillText);
        Assert.Equal("Snoozed", now.Headline);
        Assert.Equal("grey", now.PillColour);
        Assert.Equal(WingmanNowFold.ReplyPlaceholderOther, now.ReplyPlaceholder);
        Assert.Null(now.Story);
        Assert.Null(now.Needs);
        Assert.Null(now.CalmCard);
        Assert.Null(now.VerdictId);
    }

    /// <summary>No row at all - the session's machine has gone away and pushed nothing this fold can read. The record
    /// is still held here, so this is not an error; it is a view with nothing to say about the session's state.</summary>
    [Fact]
    public void A_session_with_no_row_still_answers_and_still_says_something()
    {
        var now = Fold(null, null);

        Assert.Equal(WingmanNowStates.Other, now.State);
        Assert.Equal(WingmanNowFold.OtherWithNoLabel, now.PillText);
        Assert.Equal(Sid, now.SessionId);
        Assert.Null(now.When);
        Assert.Null(now.PillColour);
    }

    /// <summary>A verdict word this Gateway does not know is not forced into a drawing that might be wrong about it.</summary>
    [Fact]
    public void A_verdict_word_this_gateway_does_not_know_falls_to_the_rows_own_label()
    {
        var verdict = Verdict("a-word-from-a-later-contract");
        var now = Fold(Row(verdict, colour: "red", label: "Needs you"), verdict);

        Assert.Equal(WingmanNowStates.Other, now.State);
        Assert.Equal("Needs you", now.PillText);
    }

    /// <summary>The row says the Wingman's answer was refused, so there is no verdict in force to narrate. Slice 2
    /// gives this state its own words; what must be true today is that nothing is served as though it were live.</summary>
    [Fact]
    public void A_refused_verdict_is_never_narrated_as_the_live_one()
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou);
        var row = Row(verdict);
        row.VerdictState = VerdictStates.Failed;

        var now = Fold(row, verdict);

        Assert.Equal(WingmanNowStates.Other, now.State);
        Assert.Equal("Needs you", now.Headline);
        Assert.Null(now.Story);
        Assert.Null(now.AgentSaid);
        Assert.Null(now.VerdictId);
    }

    /// <summary>The tool's name is the row's to state. A row that does not name one says so plainly rather than
    /// guessing a tool the session may not be running.</summary>
    [Fact]
    public void The_agents_sentence_names_no_tool_when_the_row_names_none()
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou);
        var row = Row(verdict);
        row.AgentToolDisplay = "";

        Assert.Equal("The session said", Fold(row, verdict).AgentSaid!.Who);
    }

    private static HistoryMessageDto Message(string role, params (string Kind, string Text)[] parts) => new()
    {
        Role = role,
        Parts = parts.Select(p => new HistoryPartDto { Kind = p.Kind, Text = p.Text }).ToList(),
    };
}
