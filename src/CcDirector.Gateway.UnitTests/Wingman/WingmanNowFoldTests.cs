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
/// WHAT IS NOT COVERED YET, said plainly rather than implied by silence: the carrying-on deadline sentence (slice 3),
/// working and just answered (slices 4 and 5), and the voice control (slice 6). A row in one of those states folds to
/// "other" today, and no test here pins that - pinning an interim answer would only have to be deleted by the slice
/// that gives it its real one. Reading, failed and switched off ARE covered, below.
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

    /// <summary>
    /// The row says the Wingman's answer was refused, so there is no verdict in force to narrate - whatever words
    /// that refused record happens to carry.
    ///
    /// The record on a refused row can still hold a label, a summary and a sentence: the judge answered, and the
    /// answer was thrown out afterwards for contradicting itself. Serving those words would be serving a judgement
    /// the Gateway REJECTED, so none of them appear. Slice 2 gave the state its own words (the refusal, its reason
    /// and the last good explanation), and this pins the part that must stay true beside them: nothing from the
    /// refused record is served as though it were live.
    /// </summary>
    [Fact]
    public void A_refused_verdict_is_never_narrated_as_the_live_one()
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou);
        var row = Row(verdict);
        row.VerdictState = VerdictStates.Failed;

        var now = Fold(row, verdict);

        Assert.Equal(WingmanNowStates.Failed, now.State);
        // Not the refused record's own label, summary, sentence, options or id - none of it was accepted.
        Assert.Null(now.Headline);
        Assert.Null(now.Story);
        Assert.Null(now.AgentSaid);
        Assert.Null(now.Needs);
        Assert.False(now.CanAnswerByOption);
        Assert.Null(now.VerdictId);
        // What IS served is the refusal itself, in the Gateway's own words.
        Assert.Equal("The Wingman could not explain this stop", now.FailedHeadline);
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

    // ================================================================ slice 2: reading, failed, switched off

    /// <summary>A row the roster fold stamped as being READ - the Wingman has the stop and has not answered yet.
    /// </summary>
    private static SessionDto ReadingRow()
    {
        var row = Row(null, colour: "grey", label: "Wingman reading");
        row.VerdictState = VerdictStates.Reading;
        return row;
    }

    private const string RefusalReason =
        "It said the session needs you but also marked the work as finished, which is not allowed";

    /// <summary>A row whose verdict was REFUSED: the refused record is on the row with its reason, and the row stays
    /// exactly the colour the detector made it.</summary>
    private static SessionDto FailedRow(string? reason = RefusalReason, string verdictId = "verdict-refused")
    {
        var row = Row(null);
        row.VerdictState = VerdictStates.Failed;
        row.TurnVerdict = new TurnVerdictDto
        {
            VerdictId = verdictId,
            JudgedAtUtc = Stopped.AddSeconds(4),
            TurnEndObservedAtUtc = Stopped,
            Failed = true,
            FailureReason = reason,
        };
        return row;
    }

    private static WingmanNowConversation Said(string text) => new(true, new List<HistoryMessageDto>
    {
        new() { Role = "Assistant", Parts = { new HistoryPartDto { Kind = "Text", Text = text } } },
    });

    private static WingmanNowResponse FoldWith(SessionDto? row, IReadOnlyList<AnsweredTurnVerdict> history,
        WingmanNowConversation? conversation = null, bool? switchedOff = null)
        => WingmanNowFold.Fold(new WingmanNowInputs(Sid, row, history, conversation, switchedOff));

    private static readonly AnsweredTurnVerdict[] NoHistory = Array.Empty<AnsweredTurnVerdict>();

    // ---------------------------------------------------------------- reading

    [Fact]
    public void A_stop_the_wingman_is_still_reading_says_so_and_offers_the_reply_box_anyway()
    {
        var now = FoldWith(ReadingRow(), NoHistory,
            Said("I need help with three things: the merge, the changelog and the tag."));

        Assert.Equal(WingmanNowStates.Reading, now.State);
        Assert.Equal("Stopped - the Wingman is reading it", now.PillText);
        Assert.Equal("The session stopped. The Wingman is reading its screen...", now.Headline);
        Assert.Equal("This usually takes a few seconds.", now.Story);
        Assert.Equal("Claude Code's last words", now.LastWords!.Who);
        Assert.Equal("I need help with three things: the merge, the changelog and the tag.", now.LastWords.Text);
        Assert.Equal("You can answer now without waiting.", now.ReplyPlaceholder);
        // Nothing is claimed about a stop nobody has read yet.
        Assert.Null(now.AgentSaid);
        Assert.Null(now.Needs);
        Assert.Null(now.CalmCard);
        Assert.False(now.Unsure);
        // The colour came from the detector and there IS a rule behind it, so the link stands.
        Assert.True(now.ShowWhyColour);
    }

    /// <summary>The moment shown while it is being read is the row's own waiting stamp: the verdict that would carry
    /// one does not exist yet, and the owner still needs to know how long it has been sitting there.</summary>
    [Fact]
    public void A_stop_being_read_still_says_when_it_stopped_and_how_long_ago()
    {
        var now = FoldWith(ReadingRow(), NoHistory);

        Assert.Equal("Stopped at", now.When!.Lead);
        Assert.Equal(Stopped, now.When.AtUtc);
        Assert.True(now.When.ShowAgo);
    }

    // ---------------------------------------------------------------- failed

    [Fact]
    public void A_refused_answer_says_the_wingman_could_not_explain_it_and_why()
    {
        var now = FoldWith(FailedRow(), NoHistory, Said("Two more fixes for steps 7 to 9 are in."));

        Assert.Equal(WingmanNowStates.Failed, now.State);
        Assert.Equal("The Wingman could not explain this stop", now.FailedHeadline);
        Assert.Equal(
            "It said the session needs you but also marked the work as finished, which is not allowed. "
            + "The row stays red because the session stopped.",
            now.FailedStory);
        Assert.Equal("Claude Code's last words", now.LastWords!.Who);
        Assert.Equal("Two more fixes for steps 7 to 9 are in.", now.LastWords.Text);
        // The reply box goes to the SESSION: there is no judgement here to answer.
        Assert.Equal("Answer the session directly.", now.ReplyPlaceholder);
        // The headline and story slots stay empty - the failure has its own two fields, so a client cannot render
        // the failure twice or render half of it.
        Assert.Null(now.Headline);
        Assert.Null(now.Story);
        Assert.Null(now.Needs);
        Assert.True(now.ShowWhyColour);
    }

    /// <summary>The pill keeps the row's own words on a refusal. The detector's colour and label are what the row
    /// actually is; the Wingman having failed changes nothing about the session.</summary>
    [Fact]
    public void A_refused_answer_leaves_the_rows_own_words_on_the_pill()
    {
        var now = FoldWith(FailedRow(), NoHistory);

        Assert.Equal("Needs you", now.PillText);
        Assert.Equal("red", now.PillColour);
    }

    /// <summary>A refusal that recorded no reason gets no story at all. A sentence about the colour on its own would
    /// be the Gateway explaining a refusal it cannot describe.</summary>
    [Fact]
    public void A_refusal_with_no_reason_recorded_says_nothing_it_cannot_support()
    {
        var now = FoldWith(FailedRow(reason: null), NoHistory);

        Assert.Equal("The Wingman could not explain this stop", now.FailedHeadline);
        Assert.Null(now.FailedStory);
    }

    /// <summary>The reason is carried VERBATIM. It is the record of a refusal, and a tidied-up version of it is a
    /// different claim about what happened - only the sentence about the colour is added after it.</summary>
    [Fact]
    public void A_reason_that_already_ends_in_a_full_stop_is_not_given_a_second_one()
    {
        var now = FoldWith(FailedRow(reason: "The judge timed out."), NoHistory);

        Assert.Equal("The judge timed out. The row stays red because the session stopped.", now.FailedStory);
    }

    /// <summary>A refusal on a row with no colour says only what it can: the reason, and nothing about a colour the
    /// row does not carry.</summary>
    [Fact]
    public void A_refusal_on_a_row_with_no_colour_does_not_invent_one()
    {
        var row = FailedRow();
        row.EffectiveColor = null;

        Assert.Equal(RefusalReason, FoldWith(row, NoHistory).FailedStory);
    }

    // ---------------------------------------------------------------- the last good explanation

    [Fact]
    public void A_refused_answer_offers_the_last_stop_the_wingman_did_explain()
    {
        var earlier = Verdict(TurnVerdictVocabulary.ContinuesAlone,
            label: "Fixes and inspections in progress", summary: "It is carrying on by itself.");
        earlier.VerdictId = "verdict-earlier";
        earlier.TurnEndObservedAtUtc = Stopped.AddHours(-1);

        var now = FoldWith(FailedRow(), new[] { new AnsweredTurnVerdict(earlier, null) });

        Assert.Equal("Last good explanation", now.LastGood!.Lead);
        Assert.Equal(Stopped.AddHours(-1), now.LastGood.AtUtc);
        Assert.Equal("Carrying on - Fixes and inspections in progress", now.LastGood.Text);
    }

    /// <summary>A session whose FIRST stop is the one the Wingman could not read has no earlier explanation, and Now
    /// says nothing rather than reaching for the refused record it just reported on.</summary>
    [Fact]
    public void A_refused_answer_with_no_earlier_good_one_offers_none()
    {
        var refused = new TurnVerdictDto
        {
            VerdictId = "verdict-refused",
            JudgedAtUtc = Stopped.AddSeconds(4),
            TurnEndObservedAtUtc = Stopped,
            Failed = true,
            FailureReason = "The judge timed out.",
        };

        var now = FoldWith(FailedRow(), new[] { new AnsweredTurnVerdict(refused, null) });

        Assert.Equal(WingmanNowStates.Failed, now.State);
        Assert.Null(now.LastGood);
    }

    /// <summary>The record in force is never offered back as the last GOOD one, even when history still holds an
    /// accepted row under that same id.</summary>
    [Fact]
    public void The_record_in_force_is_never_offered_as_the_last_good_explanation()
    {
        var live = Verdict(TurnVerdictVocabulary.NeededYou);
        live.VerdictId = "verdict-live";

        var now = FoldWith(FailedRow(reason: "It contradicted itself.", verdictId: "verdict-live"),
            new[] { new AnsweredTurnVerdict(live, null) });

        Assert.Null(now.LastGood);
    }

    /// <summary>
    /// AN OLDER REFUSED RECORD IS NEVER THE "LAST GOOD EXPLANATION", even when it carries a verdict word.
    ///
    /// This is not the same case as the refusal in force - that one is skipped by its id - and it was UNGUARDED
    /// until this test: the rule that skips refused records was removed and all sixty-four tests stayed green,
    /// because the only refused record in any of them carried no verdict word, so the "no pill words for it" rule
    /// happened to skip it for a different reason.
    ///
    /// It is a live case rather than a hypothetical one. A refused record CAN carry a decision - the invented-menu
    /// correction salvages one out of a refusal - and the whole point of this line is that the Gateway threw that
    /// judgement away. Offering it back under the words "last good explanation" would serve a rejected judgement as
    /// the reassuring one.
    /// </summary>
    [Fact]
    public void An_older_refused_record_is_never_offered_as_the_last_good_explanation()
    {
        var salvaged = Verdict(TurnVerdictVocabulary.Finished, finishedKind: "done",
            label: "A decision salvaged out of an answer that was thrown away");
        salvaged.VerdictId = "verdict-older-refusal";
        salvaged.TurnEndObservedAtUtc = Stopped.AddMinutes(-20);
        salvaged.Failed = true;
        salvaged.FailureReason = "It contradicted itself.";

        var good = Verdict(TurnVerdictVocabulary.ContinuesAlone, label: "Fixes in progress");
        good.VerdictId = "verdict-good";
        good.TurnEndObservedAtUtc = Stopped.AddMinutes(-45);

        var now = FoldWith(FailedRow(), new[]
        {
            new AnsweredTurnVerdict(salvaged, null),
            new AnsweredTurnVerdict(good, null),
        });

        // The older refusal is passed over for the accepted record behind it, however recent the refusal was.
        Assert.Equal("Carrying on - Fixes in progress", now.LastGood!.Text);
        Assert.Equal(Stopped.AddMinutes(-45), now.LastGood.AtUtc);
    }

    /// <summary>A past verdict whose word this Gateway draws no state for is skipped, not shown with half a line.
    /// The line is "&lt;pill words&gt; - &lt;headline&gt;" and there are no pill words for a word it does not know.
    /// </summary>
    [Fact]
    public void A_past_verdict_this_gateway_has_no_words_for_is_skipped_for_one_it_does()
    {
        var unknown = Verdict("a-word-from-a-newer-gateway", label: "Something happened");
        unknown.VerdictId = "verdict-unknown";
        unknown.TurnEndObservedAtUtc = Stopped.AddMinutes(-10);
        var known = Verdict(TurnVerdictVocabulary.Finished, finishedKind: "done", label: "The release is tagged");
        known.VerdictId = "verdict-known";
        known.TurnEndObservedAtUtc = Stopped.AddMinutes(-30);

        var now = FoldWith(FailedRow(), new[]
        {
            new AnsweredTurnVerdict(unknown, null),
            new AnsweredTurnVerdict(known, null),
        });

        Assert.Equal("Done - The release is tagged", now.LastGood!.Text);
        Assert.Equal(Stopped.AddMinutes(-30), now.LastGood.AtUtc);
    }

    // ---------------------------------------------------------------- switched off

    [Theory]
    [InlineData(VerdictStates.None)]
    [InlineData(VerdictStates.Reading)]
    [InlineData(VerdictStates.Failed)]
    public void An_account_with_the_wingman_switched_off_says_so_whatever_the_row_carries(string verdictState)
    {
        var row = Row(null);
        row.VerdictState = verdictState;

        var now = FoldWith(row, NoHistory,
            Said("I need help with three things: the merge, the changelog and the tag."), switchedOff: true);

        Assert.Equal(WingmanNowStates.SwitchedOff, now.State);
        Assert.Equal("Stopped", now.PillText);
        Assert.Equal("The Wingman is switched off for your account", now.SwitchedOff!.Headline);
        Assert.Equal("Nothing reads this session's stops.", now.SwitchedOff.Story);
        Assert.Equal("Switch it on in Settings", now.SwitchedOff.SettingsLinkText);
        Assert.Equal("Answer the session directly.", now.ReplyPlaceholder);
        Assert.Equal("I need help with three things: the merge, the changelog and the tag.", now.LastWords!.Text);
        // NOT "the Wingman could not explain this stop", and not "it is reading it" - neither is true when nothing
        // was ever going to read it.
        Assert.Null(now.FailedHeadline);
        Assert.Null(now.Headline);
    }

    /// <summary>THE ONE STATE THAT HIDES THE LINK. With the Wingman off there is no verdict behind the colour, so
    /// "why this colour?" would open an explanation of a rule that did not run.</summary>
    [Fact]
    public void Switched_off_is_the_only_state_that_hides_the_why_this_colour_link()
    {
        Assert.False(FoldWith(Row(null), NoHistory, switchedOff: true).ShowWhyColour);
        Assert.True(FoldWith(Row(null), NoHistory, switchedOff: false).ShowWhyColour);
    }

    /// <summary>
    /// WORKING OUTRANKS SWITCHED OFF, and that is the product's own law rather than a preference: if a session is
    /// working it is blue, always, and nothing may be added above that check. The pill would otherwise read "Stopped"
    /// about a session that is running.
    /// </summary>
    [Fact]
    public void A_working_session_is_never_called_stopped_because_the_wingman_is_off()
    {
        var row = Row(null, colour: "blue", label: "Working");
        row.ActivityState = "Working";

        var now = FoldWith(row, NoHistory, switchedOff: true);

        Assert.NotEqual(WingmanNowStates.SwitchedOff, now.State);
        Assert.Equal("Working", now.PillText);
        Assert.Null(now.SwitchedOff);
    }

    /// <summary>A Gateway that was not told about the account's switches says NOTHING about them. An assumed "on"
    /// would put "the Wingman could not explain this stop" on a session nothing was ever going to explain.</summary>
    [Fact]
    public void A_fold_that_was_not_told_about_the_switches_makes_no_claim_about_them()
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou);

        var now = FoldWith(Row(verdict), new[] { new AnsweredTurnVerdict(verdict, null) });

        Assert.Equal(WingmanNowStates.NeedsYou, now.State);
        Assert.Null(now.SwitchedOff);
    }

    // ---------------------------------------------------------------- the last words themselves

    /// <summary>Cut on a WORD BOUNDARY, because this stands where a headline stands and a cut through the middle of
    /// a word reads as a rendering fault rather than as an excerpt.</summary>
    [Fact]
    public void A_long_reply_is_cut_on_a_word_boundary_and_says_there_is_more()
    {
        var reply = string.Concat(Enumerable.Repeat("responsibility ", 30));   // far past the limit

        var now = FoldWith(ReadingRow(), NoHistory, Said(reply));

        var text = now.LastWords!.Text;
        Assert.EndsWith(" ...", text);
        Assert.True(text.Length <= WingmanNowFold.LastWordsLength + 4,
            "the excerpt stays within the limit; it was " + text.Length);
        // The boundary, not the character count: the last kept word is whole.
        Assert.EndsWith("responsibility ...", text);
    }

    /// <summary>A reply already short enough is shown WHOLE, with no ellipsis promising words that do not exist.
    /// </summary>
    [Fact]
    public void A_short_reply_is_shown_whole_with_nothing_promising_more()
    {
        var now = FoldWith(ReadingRow(), NoHistory, Said("Done."));

        Assert.Equal("Done.", now.LastWords!.Text);
    }

    /// <summary>The slot is one line, so the reply's own line breaks are folded into single spaces rather than
    /// travelling to a client that would render a paragraph in a one-line space.</summary>
    [Fact]
    public void The_last_words_are_one_line_however_many_the_reply_had()
    {
        var now = FoldWith(ReadingRow(), NoHistory, Said("Three things:\n\n  - the merge\n  - the changelog\n"));

        Assert.Equal("Three things: - the merge - the changelog", now.LastWords!.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void There_are_no_last_words_when_the_conversation_is_unsupported_or_absent(bool unsupported)
    {
        var conversation = unsupported
            ? new WingmanNowConversation(false, new List<HistoryMessageDto>
            {
                new() { Role = "Assistant", Parts = { new HistoryPartDto { Kind = "Text", Text = "ignored" } } },
            })
            : null;

        Assert.Null(FoldWith(ReadingRow(), NoHistory, conversation).LastWords);
    }

    /// <summary>Who said it is the row's own tool name, or "Its last words" - never a guessed tool.</summary>
    [Fact]
    public void The_last_words_name_no_tool_when_the_row_names_none()
    {
        var row = ReadingRow();
        row.AgentToolDisplay = "";

        Assert.Equal("Its last words", FoldWith(row, NoHistory, Said("Working on it.")).LastWords!.Who);
    }
}
