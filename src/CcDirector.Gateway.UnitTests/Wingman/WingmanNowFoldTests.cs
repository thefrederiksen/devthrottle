using CcDirector.Core.Drivers;
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
/// WHAT IS NOT COVERED YET, said plainly rather than implied by silence: the voice control (slice 6). A row in that
/// state folds to "other" today, and no test here pins that - pinning an interim answer would only have to be
/// deleted by the slice that gives it its real one. Reading, failed, switched off, working and just answered ARE
/// covered, below.
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

    /// <summary>
    /// A CLIENT NEVER HAS TWO SENTENCES TO CHOOSE BETWEEN. When a deadline is running, the sentence is the
    /// deadline - words, instant, words - and the card's body stays empty; when no clock is counting, the body
    /// carries the whole sentence and there is no deadline object. Exactly one of the two is ever present.
    /// </summary>
    [Fact]
    public void The_carrying_on_card_offers_the_deadline_or_a_body_and_never_both()
    {
        var verdict = Verdict(TurnVerdictVocabulary.ContinuesAlone);
        var now = Fold(Row(verdict, colour: "purple", label: "Carrying on"), verdict);

        Assert.NotNull(now.CarryingOnDeadline);
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
        WingmanNowConversation? conversation = null, bool? switchedOff = null,
        IReadOnlyList<SessionDto>? roster = null, DateTime? now = null)
        => WingmanNowFold.Fold(new WingmanNowInputs(Sid, row, history, conversation, switchedOff, null, roster, now));

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

        Assert.Equal(WingmanNowStates.Working, now.State);
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

    // ================================================================ slice 3: the carrying-on deadline

    private static OwnedSessionsFacts Owned(int working, int live, DateTime? lastActivity = null)
        => new(Working: working, Live: live, Stopped: 1, NeedYou: 0, LastActivityAtUtc: lastActivity);

    private static TurnVerdictDto CarryingOn(DateTime? announcedWake = null)
    {
        var verdict = Verdict(TurnVerdictVocabulary.ContinuesAlone,
            label: "Waiting for its Worker to finish the slice J test run",
            summary: "The Worker is running the full test gate.");
        verdict.NextScheduledWakeUtc = announcedWake;
        return verdict;
    }

    private static WingmanNowResponse FoldCarryingOn(TurnVerdictDto verdict, OwnedSessionsFacts? owned)
        => WingmanNowFold.Fold(new WingmanNowInputs(Sid, Row(verdict, colour: "purple", label: "Carrying on"),
            new[] { new AnsweredTurnVerdict(verdict, null) }, null, null, owned));

    /// <summary>
    /// THE INSTANT IS THE ONE THE CLOCK ACTUALLY EXPIRES ON, and this asserts it against the CLOCK rather than
    /// against the same arithmetic done twice.
    ///
    /// A test that recomputed "judged plus ten minutes" here would pass just as well if both the card and the test
    /// were wrong together. So the instant the card names is handed straight back to the watchdog: it must not be
    /// expired one tick before, and must be expired at it. That is the whole promise the sentence makes.
    /// </summary>
    [Fact]
    public void The_deadline_the_card_names_is_the_moment_the_clock_expires_on()
    {
        var verdict = CarryingOn();

        var now = FoldCarryingOn(verdict, owned: null);

        Assert.Equal(WingmanNowStates.CarryingOn, now.State);
        Assert.Equal("If it has not worked again by", now.CarryingOnDeadline!.Before);
        Assert.Equal(", and none of the sessions it owns is still working, this turns red and says so.",
            now.CarryingOnDeadline.After);

        var at = now.CarryingOnDeadline.AtUtc;
        Assert.False(TurnVerdictWatchdog.IsExpired(verdict, at.AddTicks(-1)), "not expired a tick before the card's own moment");
        Assert.True(TurnVerdictWatchdog.IsExpired(verdict, at), "expired at the card's own moment");
    }

    /// <summary>An announced wake-up moves the deadline, and the card moves with it - so the sentence cannot be a
    /// fixed ten minutes that happens to agree with the clock in the common case.</summary>
    [Fact]
    public void An_announced_wake_up_moves_the_card_and_the_clock_together()
    {
        var wake = Stopped.AddHours(2);
        var verdict = CarryingOn(announcedWake: wake);

        var at = FoldCarryingOn(verdict, owned: null).CarryingOnDeadline!.AtUtc;

        Assert.Equal(wake + TurnVerdictWatchdog.AfterAnnouncedWake, at);
        Assert.False(TurnVerdictWatchdog.IsExpired(verdict, at.AddTicks(-1)));
        Assert.True(TurnVerdictWatchdog.IsExpired(verdict, at));
        // And it is genuinely later than the no-wake-up answer, so the two cases are not the same number.
        Assert.True(at > Stopped.AddSeconds(4) + TurnVerdictWatchdog.WithoutAnnouncedWake);
    }

    /// <summary>
    /// A SESSION WITH ONE OF ITS OWN STILL RUNNING NAMES NO TIME, because no clock is counting: the card says so in
    /// words instead. Naming a moment here would be naming one the clock will not act on.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]   // a Worker working
    [InlineData(0, 1)]   // a Worker alive but quiet - inside one long silent command
    public void A_session_with_one_of_its_own_still_running_names_no_deadline(int working, int live)
    {
        var verdict = CarryingOn();

        var now = FoldCarryingOn(verdict, Owned(working, live, Stopped.AddMinutes(1)));

        Assert.Equal(WingmanNowStates.CarryingOn, now.State);
        Assert.Null(now.CarryingOnDeadline);
        Assert.Equal("Nothing needed from you", now.CalmCard!.Heading);
        Assert.Equal("It turns red if it stops working and none of the sessions it owns is still working.",
            now.CalmCard.Body);
        // And the clock agrees there is nothing to expire.
        Assert.Null(TurnVerdictWatchdog.DeadlineFor(verdict, Owned(working, live, Stopped.AddMinutes(1))));
    }

    /// <summary>Once every owned session has stopped the clock runs again, and it runs from the LAST of them - so
    /// the card names that moment and not the judging moment.</summary>
    [Fact]
    public void Once_its_own_sessions_have_stopped_the_deadline_runs_from_the_last_of_them()
    {
        var verdict = CarryingOn();
        var lastStopped = Stopped.AddMinutes(30);
        var owned = new OwnedSessionsFacts(Working: 0, Live: 0, Stopped: 2, NeedYou: 0, LastActivityAtUtc: lastStopped);

        var now = FoldCarryingOn(verdict, owned);

        Assert.Equal(lastStopped + TurnVerdictWatchdog.WithoutAnnouncedWake, now.CarryingOnDeadline!.AtUtc);
        Assert.True(TurnVerdictWatchdog.IsExpired(verdict, now.CarryingOnDeadline.AtUtc, owned));
        Assert.False(TurnVerdictWatchdog.IsExpired(verdict, now.CarryingOnDeadline.AtUtc.AddTicks(-1), owned));
    }

    /// <summary>The clock sentence belongs to carrying on alone. Done and report need nothing from the owner AND
    /// nothing is counting, so a deadline on either would be a threat about a session that has finished.</summary>
    [Theory]
    [InlineData(TurnVerdictVocabulary.Finished, "done")]
    [InlineData(TurnVerdictVocabulary.Finished, "report")]
    [InlineData(TurnVerdictVocabulary.NeededYou, null)]
    public void No_other_state_names_a_deadline(string word, string? finishedKind)
    {
        var verdict = Verdict(word, finishedKind: finishedKind);

        var now = WingmanNowFold.Fold(new WingmanNowInputs(Sid, Row(verdict),
            new[] { new AnsweredTurnVerdict(verdict, null) }, null, null, Owned(0, 0, Stopped)));

        Assert.Null(now.CarryingOnDeadline);
    }

    /// <summary>The calm cards that already had a body keep it - the clock sentence is only ever written into the
    /// one card that had none.</summary>
    [Fact]
    public void The_done_and_report_cards_keep_their_own_bodies()
    {
        var done = Verdict(TurnVerdictVocabulary.Finished, finishedKind: "done");
        var report = Verdict(TurnVerdictVocabulary.Finished, finishedKind: "report");

        Assert.Equal("Nothing is needed from you. You can close this session when you are ready.",
            Fold(Row(done), done).CalmCard!.Body);
        Assert.Equal("Nothing is needed from you, and the work is not finished yet.",
            Fold(Row(report), report).CalmCard!.Body);
    }

    // ================================================================ slice 4: working, and what it was asked

    private static SessionDto WorkingRow(DateTime? ownerTurn = null)
    {
        var row = Row(null, colour: "blue", label: "Working");
        row.ActivityState = "Working";
        row.LastOwnerTurnAtUtc = ownerTurn;
        return row;
    }

    /// <summary>A stop that has been superseded - the session went back to work at that moment.</summary>
    private static AnsweredTurnVerdict Superseded(DateTime supersededAt, string word = TurnVerdictVocabulary.NeededYou)
    {
        var verdict = Verdict(word);
        verdict.SupersededAtUtc = supersededAt;
        return new AnsweredTurnVerdict(verdict, null);
    }

    private static WingmanNowConversation Asked(string text, DateTime? at = null) => new(true, new List<HistoryMessageDto>
    {
        new() { Role = "Assistant", Parts = { new HistoryPartDto { Kind = "Text", Text = "Earlier reply." } } },
        new()
        {
            Role = "User",
            Parts = { new HistoryPartDto { Kind = "Text", Text = text } },
            Timestamp = new DateTimeOffset(at ?? Stopped.AddMinutes(2), TimeSpan.Zero),
        },
    });

    // ---------------------------------------------------------------- how long it has been working

    /// <summary>
    /// WORKING MEASURES FORWARD. The sentence is the elapsed time alone - "Working for 6 minutes" - so the client
    /// is told there is no clock time in it, rather than working that out from the state itself.
    /// </summary>
    [Fact]
    public void A_working_session_says_how_long_it_has_been_working_and_names_no_clock_time()
    {
        var wentBackToWork = Stopped.AddMinutes(1);

        var now = FoldWith(WorkingRow(), new[] { Superseded(wentBackToWork) });

        Assert.Equal(WingmanNowStates.Working, now.State);
        Assert.Equal("Working", now.PillText);
        Assert.Equal("Working for", now.When!.Lead);
        Assert.Equal(wentBackToWork, now.When.AtUtc);
        Assert.True(now.When.ElapsedOnly);
        Assert.Equal("Send it something while it works - it is queued until it is ready.", now.ReplyPlaceholder);
    }

    /// <summary>The moment is when the last stop stopped being the live one, NOT when that stop happened - the
    /// number is the length of this working stretch, not the age of a stop that is over.</summary>
    [Fact]
    public void The_working_moment_is_when_it_went_back_to_work_and_not_when_it_stopped()
    {
        var now = FoldWith(WorkingRow(), new[] { Superseded(Stopped.AddMinutes(6)) });

        Assert.Equal(Stopped.AddMinutes(6), now.When!.AtUtc);
        Assert.NotEqual(Stopped, now.When.AtUtc);
    }

    /// <summary>A session that has not stopped since this Gateway learned of it has no superseded record, so
    /// nothing knows when the stretch began. The pill still says Working; no number is invented.</summary>
    [Fact]
    public void A_session_that_has_never_stopped_says_it_is_working_and_invents_no_duration()
    {
        var now = FoldWith(WorkingRow(), NoHistory);

        Assert.Equal(WingmanNowStates.Working, now.State);
        Assert.Equal("Working", now.PillText);
        Assert.Null(now.When);
    }

    // ---------------------------------------------------------------- what it was last asked, and by whom

    /// <summary>What was asked is always shown, with its heading and its moment, whoever asked it.</summary>
    [Fact]
    public void What_it_was_last_asked_is_shown_with_its_heading()
    {
        var now = FoldWith(WorkingRow(), new[] { Superseded(Stopped.AddMinutes(1)) },
            Asked("Please rebase before you push."));

        Assert.Equal("What it was last asked", now.LastAsked!.Heading);
        Assert.Equal("Please rebase before you push.", now.LastAsked.Text);
    }

    /// <summary>THE OWNER IS NAMED ONLY ON THE STAMP. His recorded turn sits within half a minute of the message,
    /// and at or after the previous stop.</summary>
    [Fact]
    public void The_owner_is_named_when_his_own_recorded_turn_sits_beside_the_message()
    {
        var askedAt = Stopped.AddMinutes(2);

        var now = FoldWith(WorkingRow(ownerTurn: askedAt.AddSeconds(3)), new[] { Superseded(Stopped.AddMinutes(1)) },
            Asked("Allow the merge.", askedAt));

        Assert.Equal("You", now.LastAsked!.By);
        Assert.Equal("You, at", now.LastAsked.WhenLead);
        Assert.Equal("Allow the merge.", now.LastAsked.Text);
        Assert.Equal(askedAt, now.LastAsked.AtUtc);
    }

    /// <summary>
    /// NOBODY IS NAMED WHEN NOTHING SAYS WHO. The card still says what was asked and when - a name in front of
    /// the owner that nothing verified is worse than an honest silence.
    /// </summary>
    [Fact]
    public void Nobody_is_named_when_the_owners_stamp_is_too_far_from_the_message()
    {
        var askedAt = Stopped.AddMinutes(2);

        // His turn is well outside the tolerance - a different message, earlier in the same conversation.
        var now = FoldWith(WorkingRow(ownerTurn: askedAt.AddMinutes(-10)), new[] { Superseded(Stopped.AddMinutes(1)) },
            Asked("Allow the merge.", askedAt));

        Assert.Null(now.LastAsked!.By);
        Assert.Equal("at", now.LastAsked.WhenLead);
        Assert.Equal("Allow the merge.", now.LastAsked.Text);
    }

    /// <summary>
    /// THE TOLERANCE IS A REAL CHECK, not a formality - this is the case that proves it.
    ///
    /// The owner's turn here is AFTER the previous stop, so the first half of the rule is satisfied and only the
    /// half-minute decides. It was unguarded until this test: the tolerance was replaced with "always true" and
    /// every other test stayed green, because in all of them the stamp also failed the previous-stop half and
    /// never reached this one.
    ///
    /// What it protects against is the ordinary case of him typing into a session, walking away, and another
    /// session messaging it twenty minutes later - which would otherwise be shown to him as his own words.
    /// </summary>
    [Fact]
    public void An_owner_turn_after_the_previous_stop_but_long_before_the_message_names_nobody()
    {
        var ownerTurn = Stopped.AddMinutes(1);          // after the stop at Stopped - the first half passes
        var askedAt = ownerTurn.AddMinutes(20);         // and far outside the half-minute

        var now = FoldWith(WorkingRow(ownerTurn), new[] { Superseded(Stopped.AddSeconds(30)) },
            Asked("Please rebase before you push.", askedAt));

        Assert.Null(now.LastAsked!.By);
        Assert.Equal("at", now.LastAsked.WhenLead);
    }

    /// <summary>Nobody is named when the Director recorded no owner turn at all for this session.</summary>
    [Fact]
    public void Nobody_is_named_when_no_owner_turn_was_ever_recorded()
    {
        var now = FoldWith(WorkingRow(ownerTurn: null), new[] { Superseded(Stopped.AddMinutes(1)) },
            Asked("Allow the merge."));

        Assert.Null(now.LastAsked!.By);
        Assert.Equal("at", now.LastAsked.WhenLead);
    }

    /// <summary>
    /// THE STAMP ALONE IS NOT ENOUGH. An owner turn from BEFORE the previous stop cannot be the answer to a
    /// message sent after it, however close the two happen to sit - otherwise every message arriving after he
    /// last typed would be credited to him.
    /// </summary>
    [Fact]
    public void An_owner_turn_from_before_the_previous_stop_does_not_claim_a_later_message()
    {
        var stoppedAgain = Stopped;                        // the previous stop, from the verdict
        var askedAt = stoppedAgain.AddSeconds(-20);        // a message just BEFORE that stop
        var ownerTurn = askedAt.AddSeconds(2);             // and his turn beside it - within the tolerance

        var now = FoldWith(WorkingRow(ownerTurn), new[] { Superseded(Stopped.AddMinutes(1)) },
            Asked("Something from before the stop.", askedAt));

        Assert.Null(now.LastAsked!.By);
        Assert.Equal("at", now.LastAsked.WhenLead);
    }

    /// <summary>A message with no recorded time cannot be placed, so no card is drawn rather than one that says
    /// "at" and nothing after it.</summary>
    [Fact]
    public void A_message_with_no_recorded_time_draws_no_card()
    {
        var conversation = new WingmanNowConversation(true, new List<HistoryMessageDto>
        {
            new() { Role = "User", Parts = { new HistoryPartDto { Kind = "Text", Text = "Allow the merge." } } },
        });

        Assert.Null(FoldWith(WorkingRow(), NoHistory, conversation).LastAsked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void There_is_no_asked_card_when_the_conversation_is_unsupported_or_absent(bool unsupported)
    {
        var conversation = unsupported
            ? new WingmanNowConversation(false, Asked("Allow the merge.").Messages)
            : null;

        Assert.Null(FoldWith(WorkingRow(), NoHistory, conversation).LastAsked);
    }

    // ---------------------------------------------------------------- the stop it came from

    [Fact]
    public void A_working_session_shows_the_stop_it_has_just_come_from()
    {
        var now = FoldWith(WorkingRow(), new[] { Superseded(Stopped.AddMinutes(1)) });

        Assert.Equal("Last stop", now.LastStop!.Lead);
        Assert.Equal(Stopped, now.LastStop.AtUtc);
        Assert.Equal("Needs you - Merge pull request 3002, or allow me to merge it", now.LastStop.Text);
    }

    /// <summary>A SUPERSEDED record is the normal case here, not an edge: going back to work is what superseded
    /// it. A session whose last stop was never superseded still shows it.</summary>
    [Fact]
    public void The_stop_it_came_from_is_shown_whether_or_not_the_record_was_superseded()
    {
        var notSuperseded = new AnsweredTurnVerdict(Verdict(TurnVerdictVocabulary.NeededYou), null);

        var now = FoldWith(WorkingRow(), new[] { notSuperseded });

        Assert.Equal("Last stop", now.LastStop!.Lead);
        Assert.Equal(Stopped, now.LastStop.AtUtc);
    }

    /// <summary>A session the Wingman has never explained a stop for shows none, rather than an empty line.
    /// </summary>
    [Fact]
    public void A_session_with_no_explained_stop_shows_none()
    {
        Assert.Null(FoldWith(WorkingRow(), NoHistory).LastStop);
    }

    /// <summary>A refused record is not the stop it came from either - the same rule the last good explanation
    /// keeps, for the same reason: the Gateway threw that judgement away.</summary>
    [Fact]
    public void A_refused_record_is_not_shown_as_the_stop_it_came_from()
    {
        var refused = Verdict(TurnVerdictVocabulary.NeededYou);
        refused.VerdictId = "verdict-refused";
        refused.Failed = true;
        refused.FailureReason = "It contradicted itself.";
        refused.SupersededAtUtc = Stopped.AddMinutes(1);

        var good = Verdict(TurnVerdictVocabulary.ContinuesAlone, label: "Carrying on with the slice");
        good.VerdictId = "verdict-good";
        good.TurnEndObservedAtUtc = Stopped.AddMinutes(-30);

        var now = FoldWith(WorkingRow(), new[]
        {
            new AnsweredTurnVerdict(refused, null),
            new AnsweredTurnVerdict(good, null),
        });

        Assert.Equal("Carrying on - Carrying on with the slice", now.LastStop!.Text);
    }

    /// <summary>Working shows what it is DOING, and claims nothing about a stop it is not in. No headline, no
    /// story, no agent's sentence, no needs, no calm card - those all describe a stop.</summary>
    [Fact]
    public void A_working_session_claims_nothing_about_a_stop_it_is_not_in()
    {
        var now = FoldWith(WorkingRow(), new[] { Superseded(Stopped.AddMinutes(1)) }, Asked("Allow the merge."));

        Assert.Null(now.Headline);
        Assert.Null(now.Story);
        Assert.Null(now.AgentSaid);
        Assert.Null(now.Needs);
        Assert.Null(now.CalmCard);
        Assert.Null(now.LastWords);
        Assert.Null(now.CarryingOnDeadline);
        Assert.False(now.Unsure);
    }

    // ================================================================ slice 5: what he answered, and what is next

    /// <summary>The moment he answered, two minutes after the stop.</summary>
    private static readonly DateTime AnsweredAt = Stopped.AddMinutes(2);

    /// <summary>A stop he answered THROUGH AN OPTION: the verdict row carries the moment, and beside it the answer
    /// the answer route stored - the position he picked and the words that option is. The session went back to work
    /// two seconds later.</summary>
    private static AnsweredTurnVerdict AnsweredByOption(string chosen = "Allow the merge",
        DateTime? answeredAt = null, DateTime? backToWorkAt = null)
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou);
        verdict.SupersededAtUtc = backToWorkAt ?? (answeredAt ?? AnsweredAt).AddSeconds(2);
        return new AnsweredTurnVerdict(verdict, answeredAt ?? AnsweredAt, Chose(verdict, chosen));
    }

    /// <summary>The answer route's own record of one option being picked, as it stores it.</summary>
    private static TurnVerdictStoredAnswer Chose(TurnVerdictDto verdict, string words)
        => new(verdict.VerdictId, verdict.TurnEndObservedAtUtc, new[] { 0 }, words);

    /// <summary>The answer route's record of the one answer that picks NO option: the confirm of a reply already
    /// typed into the session. Its words are about the sending, not about a decision.</summary>
    private static TurnVerdictStoredAnswer ConfirmedATypedReply(TurnVerdictDto verdict)
        => new(verdict.VerdictId, verdict.TurnEndObservedAtUtc, Array.Empty<int>(),
            TurnVerdictStoredAnswer.TypedReplyWords);

    /// <summary>A stop with no answer recorded against it - the shape a typed reply leaves, because a typed reply
    /// touches no verdict at all.</summary>
    private static AnsweredTurnVerdict UnansweredStop(DateTime? backToWorkAt = null)
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou);
        verdict.SupersededAtUtc = backToWorkAt ?? AnsweredAt.AddSeconds(2);
        return new AnsweredTurnVerdict(verdict, null);
    }

    // ---------------------------------------------------------------- what he answered

    /// <summary>
    /// TAPPING AN OPTION IS ANSWERING, and the screen says so back to him: his own words, that they were sent, and
    /// that the session took them and went back to work.
    ///
    /// The pill says what the SESSION is doing - "Working again" - because that is the confirmation he is looking
    /// at the screen for. The timed sentence measures forward from his answer and names no clock time, since the
    /// card underneath already says when it was sent.
    /// </summary>
    [Fact]
    public void An_option_he_chose_is_read_back_to_him_with_the_session_working_again()
    {
        var now = FoldWith(WorkingRow(), new[] { AnsweredByOption() }, now: AnsweredAt.AddSeconds(20));

        Assert.Equal(WingmanNowStates.JustAnswered, now.State);
        Assert.Equal("Working again", now.PillText);

        Assert.Equal("You answered", now.When!.Lead);
        Assert.Equal(AnsweredAt, now.When.AtUtc);
        Assert.True(now.When.ShowAgo);
        Assert.True(now.When.ElapsedOnly);

        Assert.Equal("You answered: Allow the merge", now.Answered!.Headline);
        Assert.Equal("Allow the merge", now.Answered.Text);
        Assert.Equal("Sent at", now.Answered.SentLead);
        Assert.Equal(AnsweredAt, now.Answered.AtUtc);
        Assert.Equal("The session started working again 2 seconds later.", now.Answered.WorkingAgainAfterText);

        // The stop is no longer merely the last one - it is the one he dealt with.
        Assert.Equal("The stop you answered", now.LastStop!.Lead);
    }

    /// <summary>
    /// TYPING IS ANSWERING TOO - in the terminal, on the phone, by voice, or in the reply box - and none of those
    /// touch a verdict. The answer is the first thing he said after the stop.
    ///
    /// Without this the card would go blank exactly when he answered anywhere but the option buttons, which is most
    /// of the time.
    /// </summary>
    [Fact]
    public void A_reply_he_typed_answers_the_stop_just_as_an_option_does()
    {
        var now = FoldWith(WorkingRow(), new[] { UnansweredStop() },
            Asked("Yes - merge it and push the notes.", AnsweredAt), now: AnsweredAt.AddSeconds(20));

        Assert.Equal(WingmanNowStates.JustAnswered, now.State);
        Assert.Equal("You answered: Yes - merge it and push the notes.", now.Answered!.Headline);
        Assert.Equal(AnsweredAt, now.Answered.AtUtc);
        Assert.Equal("The session started working again 2 seconds later.", now.Answered.WorkingAgainAfterText);
    }

    /// <summary>
    /// THE ROUTE'S CONFIRM OF A TYPED REPLY IS NOT WHAT HE ANSWERED. Pressing send on a reply already typed into
    /// the session picks no option, so the stored answer's words are a sentence about the sending - "Sent the
    /// reply typed on the screen." - and reading that back under "You answered" would tell him what the product
    /// did instead of what he decided.
    ///
    /// So it falls through to the reply itself, which is in the conversation and is his own words.
    /// </summary>
    [Fact]
    public void A_stored_answer_that_chose_no_option_falls_through_to_the_reply_he_typed()
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou);
        verdict.SupersededAtUtc = AnsweredAt.AddSeconds(2);
        var stop = new AnsweredTurnVerdict(verdict, AnsweredAt, ConfirmedATypedReply(verdict));

        var now = FoldWith(WorkingRow(), new[] { stop }, Asked("Run the tests first.", AnsweredAt),
            now: AnsweredAt.AddSeconds(20));

        Assert.Equal(WingmanNowStates.JustAnswered, now.State);
        Assert.Equal("You answered: Run the tests first.", now.Answered!.Headline);
        Assert.DoesNotContain(TurnVerdictStoredAnswer.TypedReplyWords, now.Answered.Headline);
    }

    /// <summary>
    /// HIS ANSWER COMES OFF THE SCREEN. Five minutes later the session is simply working, and a card still saying
    /// what he sent would be describing something he moved on from long ago.
    ///
    /// The boundary is tested at the boundary: exactly five minutes is already too old.
    /// </summary>
    [Fact]
    public void An_answer_older_than_the_window_leaves_the_session_plainly_working()
    {
        var now = FoldWith(WorkingRow(), new[] { AnsweredByOption() },
            now: AnsweredAt.Add(WingmanNowFold.JustAnsweredWindow));

        Assert.Equal(WingmanNowStates.Working, now.State);
        Assert.Null(now.Answered);
        Assert.Null(now.NextNeedsYou);
        Assert.Equal("Last stop", now.LastStop!.Lead);
        Assert.Equal("Working for", now.When!.Lead);
    }

    /// <summary>
    /// NO CLOCK, NO CLAIM. A caller that supplies no moment cannot have the five-minute rule applied for it, so it
    /// is not applied: the session reads as plainly working rather than as an answer that might be an hour old.
    /// </summary>
    [Fact]
    public void With_no_moment_supplied_the_session_reads_as_plainly_working()
    {
        var now = FoldWith(WorkingRow(), new[] { AnsweredByOption() });

        Assert.Equal(WingmanNowStates.Working, now.State);
        Assert.Null(now.Answered);
    }

    /// <summary>
    /// A MOMENT WITH NO STORED ANSWER IS NOT AN ANSWER TO SHOW. Every stop answered before the answer route kept
    /// a record carries exactly that - a recorded moment and nothing saying what was chosen - and "You answered:"
    /// with nothing after the colon is worse than saying nothing at all.
    /// </summary>
    [Fact]
    public void A_recorded_answer_with_no_stored_choice_is_not_read_back_to_him()
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou);
        verdict.SupersededAtUtc = AnsweredAt.AddSeconds(2);
        var stop = new AnsweredTurnVerdict(verdict, AnsweredAt, null);

        var now = FoldWith(WorkingRow(), new[] { stop }, now: AnsweredAt.AddSeconds(20));

        Assert.Equal(WingmanNowStates.Working, now.State);
        Assert.Null(now.Answered);
    }

    /// <summary>
    /// OUR OWN DOORBELL LINE IS NOT HIM ANSWERING. The first thing typed after a stop is the answer to it, so a
    /// doorbell line there means he has not answered yet - and the card must not tell him he has, in his own
    /// words, with our delivery line as the text.
    ///
    /// It STOPS the search rather than skipping past: skipping would find a later message and present it as the
    /// answer to a stop that this one had already moved the session off.
    /// </summary>
    [Fact]
    public void A_doorbell_line_after_the_stop_is_never_read_back_as_his_own_answer()
    {
        var doorbell = FleetDoorbellLine.For(2);

        var now = FoldWith(WorkingRow(), new[] { UnansweredStop() }, Asked(doorbell, AnsweredAt),
            now: AnsweredAt.AddSeconds(20));

        Assert.Equal(WingmanNowStates.Working, now.State);
        Assert.Null(now.Answered);
    }

    /// <summary>
    /// A MESSAGE BEFORE THE STOP DID NOT ANSWER IT. What he said before the session stopped is what it was working
    /// on when it stopped - reading it as the answer would close a loop nobody closed.
    /// </summary>
    [Fact]
    public void A_message_sent_before_the_stop_is_not_an_answer_to_it()
    {
        var now = FoldWith(WorkingRow(), new[] { UnansweredStop() },
            Asked("Carry on with the release notes.", Stopped.AddMinutes(-3)), now: Stopped.AddSeconds(20));

        Assert.Equal(WingmanNowStates.Working, now.State);
        Assert.Null(now.Answered);
    }

    /// <summary>
    /// NOTHING IS CLAIMED ABOUT THE SESSION TAKING IT when this Gateway has no record of it going back to work.
    /// The card is still right about what he sent; it simply stops short of confirming what it cannot see.
    /// </summary>
    [Fact]
    public void Nothing_is_claimed_about_working_again_when_no_return_to_work_is_recorded()
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou);
        var stop = new AnsweredTurnVerdict(verdict, AnsweredAt, Chose(verdict, "Allow the merge"));

        var now = FoldWith(WorkingRow(), new[] { stop }, now: AnsweredAt.AddSeconds(20));

        Assert.Equal(WingmanNowStates.JustAnswered, now.State);
        Assert.Equal("You answered: Allow the merge", now.Answered!.Headline);
        Assert.Null(now.Answered.WorkingAgainAfterText);
    }

    /// <summary>
    /// A RETURN TO WORK BEFORE HIS ANSWER CONFIRMS NOTHING. The session picked itself up on its own; saying it
    /// started working again "later" would credit his answer with something it did not cause.
    /// </summary>
    [Fact]
    public void Nothing_is_claimed_about_working_again_when_it_went_back_to_work_first()
    {
        var now = FoldWith(WorkingRow(), new[] { AnsweredByOption(backToWorkAt: AnsweredAt.AddSeconds(-30)) },
            now: AnsweredAt.AddSeconds(20));

        Assert.Equal(WingmanNowStates.JustAnswered, now.State);
        Assert.Null(now.Answered!.WorkingAgainAfterText);
    }

    // ---------------------------------------------------------------- and where to go next

    /// <summary>A red row of this account's roster, waiting since the given moment.</summary>
    private static SessionDto Waiting(string sessionId, string name, DateTime since)
    {
        var row = Row(Verdict(TurnVerdictVocabulary.NeededYou));
        row.SessionId = sessionId;
        row.Name = name;
        row.NeedsYouSince = since;
        // The stamped line the Sessions list reads for a judged red row - the Wingman's own words for that row.
        row.VerdictLabel = "Should I open an issue for the dev Gateway?";
        return row;
    }

    /// <summary>
    /// THE NEXT SESSION IS THE SESSIONS LIST'S OWN FIRST ROW - the longest wait, by the list's own rule over the
    /// list's own fold. Sending him somewhere the list does not have at the top would be this tab deciding the
    /// order a second way.
    /// </summary>
    [Fact]
    public void The_next_session_that_needs_him_is_the_waiting_lines_own_first_row()
    {
        var roster = new[]
        {
            Waiting("33333333-3333-3333-3333-333333333333", "Dev Reports - Worker", Stopped.AddMinutes(-2)),
            Waiting("44444444-4444-4444-4444-444444444444", "Dev Reports - Architect", Stopped.AddMinutes(-40)),
        };

        var now = FoldWith(WorkingRow(), new[] { AnsweredByOption() }, roster: roster,
            now: AnsweredAt.AddSeconds(20));

        Assert.Equal("Next that needs you", now.NextNeedsYou!.Heading);
        Assert.Equal("44444444-4444-4444-4444-444444444444", now.NextNeedsYou.SessionId);
        Assert.Equal("Dev Reports - Architect", now.NextNeedsYou.Name);
        // Its own row's words, which for a judged red row are the Wingman's own line.
        Assert.Equal("Should I open an issue for the dev Gateway?", now.NextNeedsYou.Label);
        Assert.Equal("Go there", now.NextNeedsYou.LinkText);
    }

    /// <summary>
    /// IT NEVER POINTS HIM AT THE SESSION HE IS ALREADY ON, and never at one that is not his to watch. A snoozed
    /// or supervised session is not waiting on him - that is the whole of what the bucket means - so a pointer to
    /// one would send him to a session that would not thank him for arriving.
    /// </summary>
    [Fact]
    public void The_next_session_skips_this_one_and_anything_not_waiting_on_him()
    {
        var thisOne = Waiting(Sid, "Wingman Inspector - Manager", Stopped.AddMinutes(-60));
        var snoozed = Waiting("33333333-3333-3333-3333-333333333333", "Snoozed one", Stopped.AddMinutes(-50));
        snoozed.OnHold = true;
        var real = Waiting("44444444-4444-4444-4444-444444444444", "Dev Reports - Architect", Stopped.AddMinutes(-2));

        var now = FoldWith(WorkingRow(), new[] { AnsweredByOption() }, roster: new[] { thisOne, snoozed, real },
            now: AnsweredAt.AddSeconds(20));

        Assert.Equal("44444444-4444-4444-4444-444444444444", now.NextNeedsYou!.SessionId);
    }

    /// <summary>Nothing else waiting means nothing offered - never an empty row, and never a working session
    /// dressed up as one that needs him.</summary>
    [Fact]
    public void Nothing_is_offered_when_no_other_session_needs_him()
    {
        var working = Waiting("33333333-3333-3333-3333-333333333333", "Busy one", Stopped.AddMinutes(-30));
        working.ActivityState = "Working";

        var now = FoldWith(WorkingRow(), new[] { AnsweredByOption() }, roster: new[] { working },
            now: AnsweredAt.AddSeconds(20));

        Assert.Null(now.NextNeedsYou);
    }

    /// <summary>
    /// A SESSION THAT IS MERELY WORKING IS NOT SENT ANYWHERE. He asked nothing, so he is owed no answer; a
    /// standing pointer at another session would read as this one nagging him about it.
    /// </summary>
    [Fact]
    public void A_working_session_he_has_not_just_answered_points_him_nowhere()
    {
        var roster = new[] { Waiting("44444444-4444-4444-4444-444444444444", "Architect", Stopped.AddMinutes(-40)) };

        var now = FoldWith(WorkingRow(), new[] { Superseded(Stopped.AddMinutes(1)) }, roster: roster,
            now: AnsweredAt.AddSeconds(20));

        Assert.Equal(WingmanNowStates.Working, now.State);
        Assert.Null(now.NextNeedsYou);
    }
}
