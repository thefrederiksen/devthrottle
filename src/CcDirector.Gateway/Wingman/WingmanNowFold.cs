using CcDirector.Core.Drivers;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// What the Wingman tab's Now view is handed, gathered by the route handler and never read again by the fold: the
/// session's FOLDED roster row (the same fold the roster serves, so the pill and the row cannot disagree), its stored
/// verdicts newest first, and its stored conversation.
/// </summary>
/// <param name="SessionId">The session Now is about.</param>
/// <param name="Row">The session's folded roster row, or null when this account's Directors have pushed no row for
/// it. A null row is not an error: the verdict record is held on this Gateway and outlives a machine going away.</param>
/// <param name="VerdictsNewestFirst">The session's stored verdicts, newest first, each with the moment the owner's
/// answer to it was confirmed.</param>
/// <param name="Conversation">The session's stored conversation, or null when nothing has been stored for it.</param>
/// <param name="WingmanSwitchedOff">Whether this ACCOUNT's Wingman is switched off - either switch, because with the
/// colour switch off every row is stamped "none" and no verdict reaches the screen, which is indistinguishable to a
/// reader from the judge never having run. NULL means the route was not told, and then no claim is made: a Gateway
/// that cannot see the account's settings says nothing about them rather than guessing that they are on.</param>
/// <param name="OwnedSessions">What this session's OWN sessions are doing, or null when it owns none. The
/// carrying-on clock does not run while one of them is still alive, so this is what decides whether the
/// carrying-on card can name a deadline at all.</param>
/// <param name="AccountRoster">Every row of this account's folded roster, or null when the caller did not read
/// one. It is the SAME fold the Sessions list serves, ordered here by the SAME rule, so the tab and the list can
/// never point him at two different sessions. Null means no claim is made about what else needs him.</param>
/// <param name="NowUtc">The moment the request is being answered, or null when the caller supplied no clock.
///
/// The fold stays pure - a moment IN, the finished view out - and it is the design's own shape for exactly one
/// rule: his answer stays on the screen for five minutes and then the session is simply working again. Null means
/// that rule cannot be evaluated, and then it is not evaluated: the session reads as plain working rather than as
/// an answer that might be an hour old.</param>
public sealed record WingmanNowInputs(
    string SessionId,
    SessionDto? Row,
    IReadOnlyList<AnsweredTurnVerdict> VerdictsNewestFirst,
    WingmanNowConversation? Conversation,
    bool? WingmanSwitchedOff = null,
    OwnedSessionsFacts? OwnedSessions = null,
    IReadOnlyList<SessionDto>? AccountRoster = null,
    DateTime? NowUtc = null);

/// <summary>One session's stored conversation as the Now fold reads it.</summary>
/// <param name="Supported">False when the session's agent tool does not produce a conversation this Gateway can
/// store - which is why "no messages" and "nothing will ever arrive" are different answers.</param>
/// <param name="Messages">The stored messages, oldest first, as the turn log reads them.</param>
public sealed record WingmanNowConversation(bool Supported, IReadOnlyList<HistoryMessageDto> Messages);

/// <summary>
/// THE ONE FOLD FOR THE WINGMAN TAB'S NOW VIEW (the Wingman tab, version 3, item 1): a folded row, the stored
/// verdicts and the stored conversation in, the finished view out.
///
/// PURE - no clock, no store, no settings, no request. Every state is therefore tested by handing it inputs, which is
/// the whole reason the route handler does nothing but gather them.
///
/// EVERY OWNER-FACING WORD IS DECIDED HERE, so the Cockpit renders and decides nothing (CLAUDE.md rule 7): the state,
/// the pill's words, the lead on each timed sentence, the headings, the reply box's placeholder, and whether an option
/// may still be tapped. The Cockpit formats the UTC instants into local time and lays the rest out.
///
/// THE COLOUR IS COPIED, NEVER COMPUTED. <see cref="WingmanNowResponse.PillColour"/> and its pixel come from the row's
/// own <see cref="SessionDto.EffectiveColor"/>. A second colour authority in this file would be free to disagree with
/// the Sessions list about the same session, which is exactly the defect the dumb-client rule exists to prevent.
///
/// WHAT THIS FOLD DOES NOT DECIDE. It rules on the stopped states the Wingman explained - needs you (sure and not
/// sure), done, report and carrying on - on the three with no explanation to show - being read, refused, and
/// switched off - on working, on just answered, and on the voice control. Everything else folds to
/// <see cref="WingmanNowStates.Other"/>, which wears the row's own label so the view is never blank. An "other"
/// answer is therefore not a claim that the row has no better state - it is a claim that this fold has not been
/// taught one.
/// </summary>
public static class WingmanNowFold
{
    /// <summary>The quiet tag beside the pill when the Wingman's own answer says it is not sure.</summary>
    public const string UnsureTag = "The Wingman is not sure";

    /// <summary>The sentence inside the card when the Wingman is not sure - it asks him to read the agent's own
    /// words before acting on a judgement that was not confident.</summary>
    public const string UnsureLine =
        "The Wingman is not sure this is a question for you. Check the reply above before answering.";

    /// <summary>The heading over what the session needs.</summary>
    public const string NeedsHeading = "What it needs from you";

    /// <summary>What leads the agent's own recommendation. The recommendation is the AGENT's, not the Wingman's, and
    /// "It" is the session - so the lead says whose opinion is being read.</summary>
    public const string RecommendsLead = "It recommends: ";

    /// <summary>The line under the needs heading, saying what a tap does. The options are drawn as boxes and
    /// nothing else on the card says a click is the answer going out.</summary>
    public const string OptionsLeadText = "Click an option to send it as your answer";

    // THE RISK WORDS, IN THE OWNER'S OWN WORDS. The judge answers one of four (see the turn-verdict contract);
    // "none" is the fourth and carries nothing. Each flag is the short warning and each line is what it means.
    public const string RiskFlagIrreversible = "Cannot be undone";
    public const string RiskLineIrreversible = "Answering this stop does something that cannot be taken back.";
    public const string RiskFlagStandingGrant = "Says yes from now on";
    public const string RiskLineStandingGrant =
        "Answering this stop can say yes to everything of this shape from now on, not only this once.";
    public const string RiskFlagSpendsMoney = "Spends real money";
    public const string RiskLineSpendsMoney = "Answering this stop spends real money.";

    /// <summary>The lead on the moment a session stopped.</summary>
    public const string StoppedAtLead = "Stopped at";

    /// <summary>The pill on the state that follows his answer. It says what the SESSION is doing - it took what he
    /// sent and went back to work - which is the confirmation he is looking at the screen for.</summary>
    public const string PillJustAnswered = "Working again";

    /// <summary>The lead on the moment he answered: "You answered 20 seconds ago".</summary>
    public const string AnsweredWhenLead = "You answered";

    /// <summary>What leads the headline over his own answer.</summary>
    public const string AnsweredHeadlineLead = "You answered: ";

    /// <summary>The lead on the moment his answer was sent.</summary>
    public const string AnsweredSentLead = "Sent at";

    /// <summary>The confirmation that the session took it, around the gap: "The session started working again 2
    /// seconds later."</summary>
    public const string WorkingAgainBefore = "The session started working again ";

    /// <summary>The end of the confirmation the session took it.</summary>
    public const string WorkingAgainAfter = " later.";

    /// <summary>The lead on the past stop once he has answered it - it is no longer merely the last one.</summary>
    public const string AnsweredStopLead = "The stop you answered";

    /// <summary>The words over the next session waiting on him.</summary>
    public const string NextHeading = "Next that needs you";

    /// <summary>The words on the control when there is audio to play.</summary>
    public const string VoicePlayLabel = "Play";

    /// <summary>The words on the control while the audio is being made. It is not a button - there is nothing to
    /// press yet - and the words say what is happening rather than inviting a press that does nothing.</summary>
    public const string VoicePreparingLabel = "Preparing audio...";

    /// <summary>The offer when voice is off for this session. FROM NOW ON, because that is what it does: it is a
    /// setting for the session, not a request to narrate the stop already on the screen.</summary>
    public const string VoiceTurnOnLabel = "Read this session aloud from now on";

    /// <summary>What to say after he turns it on when there IS a stop to read.</summary>
    public const string VoiceAfterTurnOnThisStop =
        "Voice is on for this session. This stop will be read aloud in a few seconds.";

    /// <summary>What to say after he turns it on when there is NOT. Promising him this stop would be a promise
    /// nothing can keep.</summary>
    public const string VoiceAfterTurnOnNextStop =
        "Voice is on for this session. The next stop will be read aloud.";

    /// <summary>The words on the way to it.</summary>
    public const string NextLinkText = "Go there";

    /// <summary>
    /// HOW LONG HIS ANSWER STAYS ON THE SCREEN. After this the session is simply working, and the card that says
    /// what he sent would be describing something he has long since moved on from.
    ///
    /// Five minutes is the Architect's number, not the owner's - it is inferred, and it is written ONCE here so it
    /// is one line to change when he rules otherwise. The other half of the rule needs no number: the state is
    /// only ever reached while the session is working, so a session that stops again leaves it by itself.
    /// </summary>
    public static readonly TimeSpan JustAnsweredWindow = TimeSpan.FromMinutes(5);

    /// <summary>Who said it, when the row does not name the agent tool. Never a guessed tool name.</summary>
    public const string SomethingSaidWho = "The session said";

    // The reply box's placeholder, per state. The box goes to the session as the owner's own message, so the words
    // differ by what answering MEANS here: a stop that asked him something, a stop that only told him something, and
    // a row this fold has no better words for. A null placeholder means the state offers no reply box at all.
    public const string ReplyPlaceholderNeedsYou = "Or answer in your own words.";
    public const string ReplyPlaceholderReport = "Reply if you want it to do something about this.";
    public const string ReplyPlaceholderOther = "Answer the session directly.";

    // WHAT SENDING DOES, beside the box's own Send. The first sentence is true wherever the box is offered. The
    // second is true only where something was ASKED - and it was being shown on done, on a report, on a working
    // session and on a snoozed one, none of which asked him anything.
    public const string ReplyHintPlain = "Sent to the session as your message.";
    public const string ReplyHintAsked =
        "Sent to the session as your message. You can answer more than one question in one reply.";

    // The cards on a stop that needs nothing from the owner.
    public const string DoneHeading = "The work is complete";
    public const string DoneBody = "Nothing is needed from you. You can close this session when you are ready.";

    /// <summary>
    /// ONE NAME FOR THE TELLING-YOU STATE, and it is this one - the pill, this card's heading and the row's own
    /// label in the Sessions list all say it (<see cref="SessionOrdering.CalmReportLabel"/> is the same words).
    ///
    /// It used to be three: the pill said "Report", the card said "Only telling you", and the row said "Report -
    /// Review the QA report". "Report" beside "QA report" also read as a noun about the document rather than as
    /// what the session is doing.
    /// </summary>
    public const string ReportHeading = SessionOrdering.CalmReportLabel;

    public const string ReportBody = "Nothing is needed from you, and the work is not finished yet.";
    public const string CarryingOnHeading = "Nothing needed from you";

    // The pill's words, per state.
    public const string PillNeedsYou = "Needs you";
    public const string PillCarryingOn = "Carrying on";
    public const string PillDone = "Done";

    /// <inheritdoc cref="ReportHeading"/>
    public const string PillReport = ReportHeading;

    /// <summary>The words a row wears when it carries no label of its own. A row always has one on a served roster,
    /// so this is the answer for a session whose machine has gone away and left nothing folded.</summary>
    public const string OtherWithNoLabel = "Stopped";

    // ------------------------------------------------------------------ slice 2: reading, failed, switched off

    /// <summary>The pill while the Wingman is forming its answer. It says BOTH facts, because "reading" alone reads
    /// as activity in the session rather than in the Wingman.</summary>
    public const string PillReading = "Stopped - the Wingman is reading it";

    /// <summary>The pill on an account whose Wingman is switched off: the session's PLAIN state, not a judgement.
    /// The row's own label would read "Needs you" here - the words a red unjudged row wears - and nothing has read
    /// this stop, so claiming it needs him would be the Gateway saying something no judge said.</summary>
    public const string PillSwitchedOff = "Stopped";

    public const string ReadingHeadline = "The session stopped. The Wingman is reading its screen...";
    public const string ReadingStory = "This usually takes a few seconds.";

    /// <summary>The reply box while the Wingman reads. Answering now is not a workaround - the owner may already
    /// know what the session needs, and waiting for a judgement he does not need is pure delay.</summary>
    public const string ReplyPlaceholderReading = "You can answer now without waiting.";

    public const string FailedHeadline = "The Wingman could not explain this stop";

    /// <summary>What the refusal leaves the row, appended after the Wingman's own reason. The row is EXACTLY what
    /// the detector made it when a verdict is refused, so this says why the colour is what he is looking at.</summary>
    public const string FailedRowSuffixBefore = "The row stays ";
    public const string FailedRowSuffixAfter = " because the session stopped.";

    /// <summary>The lead on the last stop the Wingman did manage to explain.</summary>
    public const string LastGoodLead = "Last good explanation";

    public const string SwitchedOffHeadline = "The Wingman is switched off for your account";
    public const string SwitchedOffStory = "Nothing reads this session's stops.";
    public const string SwitchedOffLinkText = "Switch it on in Settings";

    /// <summary>Who said the last words, when the row does not name the agent tool.</summary>
    public const string LastWordsWhoUnnamed = "Its last words";

    /// <summary>How much of the last reply stands in for a headline. Long enough to recognise the stop, short
    /// enough that it does not become the whole reply in a slot that is not built for one.</summary>
    public const int LastWordsLength = 200;

    // ------------------------------------------------------------------ slice 3: the carrying-on deadline

    /// <summary>The words before the deadline instant.</summary>
    public const string CarryingOnDeadlineBefore = "If it has not worked again by";

    /// <summary>The words after it. The comma opens the clause, so the client joins the three parts with single
    /// spaces and nothing else.</summary>
    public const string CarryingOnDeadlineAfter =
        ", and none of the sessions it owns is still working, this turns red and says so.";

    // ------------------------------------------------------------------ slice 4: working, and what it was asked

    /// <summary>The pill while the session is working.</summary>
    public const string PillWorking = "Working";

    /// <summary>The lead on how long it has been working. The sentence is the elapsed time alone - "Working for 6
    /// minutes" - so there is no clock time in it.</summary>
    public const string WorkingForLead = "Working for";

    /// <summary>The heading over what the session was last asked.</summary>
    public const string LastAskedHeading = "What it was last asked";

    /// <summary>Who asked, when it was the owner himself.</summary>
    public const string AskedByYou = "You";

    /// <summary>The words before the time when nobody is named.</summary>
    public const string AskedWhenLeadUnnamed = "at";

    /// <summary>The words between a named asker and the time.</summary>
    public const string AskedWhenLeadSuffix = ", at";

    /// <summary>The lead on the stop a working session has just come from.</summary>
    public const string LastStopLead = "Last stop";

    /// <summary>
    /// The whole line for a stop the CARRYING-ON CLOCK wrote, as a sentence.
    ///
    /// Every other past stop reads "&lt;pill words&gt; - &lt;the Wingman's headline&gt;", and for a judged stop that
    /// is a state word in front of an ask, which reads. For this one it was a state word glued to a reason code:
    /// "Needs you - Said it would continue and did not". The clock's own record is recognised by its
    /// <see cref="TurnVerdictWatchdog.ClockModel"/> stamp - a fact on the record, never a match on the label -
    /// so this cannot start firing for a judged stop whose words happen to look similar.
    /// </summary>
    public const string ExpiredPastText = "It said it would carry on, then stopped, so it needed you.";

    /// <summary>The lead before the moment a snoozed session comes back.</summary>
    public const string SnoozedUntilLead = "Snoozed until";

    /// <summary>The headline on a snooze with no moment to name - a hold that has not landed yet, or one that
    /// waits for him. The sentence says what ends it instead of naming a time nothing recorded.</summary>
    public const string SnoozedNoDeadlineHeadline = "Snoozed until you wake it";

    /// <summary>The lead on the stop a snooze was taken over. It is PAST and says so, but it is not thrown away:
    /// the Wingman's account of it is the only reason the snooze meant anything.</summary>
    public const string SnoozedOverLead = "The stop you snoozed over";

    /// <summary>The reply box while it works. It says what actually happens, because a message sent to a working
    /// session is not lost and is not delivered either - it waits.</summary>
    public const string ReplyPlaceholderWorking =
        "Send it something while it works - it is queued until it is ready.";

    /// <summary>
    /// How close the owner's own recorded turn must be to a message before it is called HIS.
    ///
    /// The Director stamps when the owner last typed into a session, and the conversation stores when a message
    /// was recorded; neither is the other, so they are compared with a tolerance. Half a minute is wide enough to
    /// cover the gap between typing and the transcript being written, and narrow enough that an unrelated message
    /// arriving later is not credited to him. Outside it, nobody is named at all.
    /// </summary>
    public static readonly TimeSpan OwnerTurnTolerance = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The carrying-on sentence while the session still has one of its OWN sessions running: no clock is counting,
    /// so there is no instant to name.
    ///
    /// This is the design's own sentence, kept verbatim. Its "it" is the session, as in the dated form beside it.
    /// </summary>
    public const string CarryingOnNoDeadlineBody =
        "It turns red if it stops working and none of the sessions it owns is still working.";

    /// <summary>The Now view for one session.</summary>
    public static WingmanNowResponse Fold(WingmanNowInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var row = inputs.Row;
        var verdicts = inputs.VerdictsNewestFirst ?? Array.Empty<AnsweredTurnVerdict>();

        // THE VERDICT IN FORCE IS THE ROW'S OWN, not one this fold picks out of history. The row was coloured and
        // labelled from it, so reading anything else here is how a pill comes to describe a different stop than the
        // dot beside it. History is read for the moment the owner's answer was confirmed, which the row cannot carry,
        // and (from slice 2) for the stops that are past.
        var live = row is not null
                   && string.Equals(row.VerdictState, VerdictStates.Judged, StringComparison.Ordinal)
                   && row.TurnVerdict is { Failed: false } accepted
            ? accepted
            : null;

        var state = StateOf(live, row, inputs.WingmanSwitchedOff);

        // JUST ANSWERED IS WORKING, PLUS HIS ANSWER STILL ON THE SCREEN. It is not a state of the session - the
        // session is working, and the pill says so - it is a state of the CONVERSATION between him and it, which
        // is why it is settled here from the answer rather than in StateOf from the row. A session with no answer
        // to show, or an answer older than the window, is plainly working and reads that way.
        WingmanNowAnsweredDto? answered = null;
        if (string.Equals(state, WingmanNowStates.Working, StringComparison.Ordinal))
        {
            answered = Answered(verdicts, inputs.Conversation, WorkingSince(verdicts), inputs.NowUtc);
            if (answered is not null) state = WingmanNowStates.JustAnswered;
        }

        var answer = new WingmanNowResponse
        {
            SessionId = inputs.SessionId,
            // WHICH SESSION THIS IS, FIRST AND IN EVERY STATE - set here, outside every branch below, because
            // there is no state in which the owner may be left to guess whose question he is answering.
            SessionLine = SessionLine(inputs.SessionId, row),
            State = state,
            PillText = PillText(state, row),
            PillColour = row?.EffectiveColor,
            PillColourHex = row?.EffectiveColorHex,
            // THE ONE STATE THAT HIDES THE LINK: with the Wingman switched off there is no verdict behind the
            // colour, so "why this colour?" would open an explanation of a rule that did not run.
            ShowWhyColour = !string.Equals(state, WingmanNowStates.SwitchedOff, StringComparison.Ordinal),
            When = When(state, live, row, verdicts, answered),
            Voice = Voice(state, row, live),
            VerdictId = live?.VerdictId,
            ReplyPlaceholder = ReplyPlaceholder(state),
            ReplyHint = ReplyPlaceholder(state) is null ? null : ReplyHint(state),
        };

        // WORKING. Most of a session's life, and the state that outranks every other - see StateOf. There is no
        // stop to explain, so the view says what it is doing instead: how long it has been at it, what it was
        // last asked, and the stop it has just come from.
        if (state is WingmanNowStates.Working or WingmanNowStates.JustAnswered)
        {
            answer.LastAsked = LastAsked(inputs.Conversation, row, verdicts);
            answer.LastStop = LastStop(verdicts, answered is null ? LastStopLead : AnsweredStopLead);

            // AND WHERE TO GO NEXT, only on the state that follows his answer. It is the one moment the question
            // "what now?" is his - on a session that is merely working he did not ask anything and is not owed an
            // answer, and a standing pointer at another session would read as this one nagging him about it.
            if (answered is not null)
            {
                answer.Answered = answered;
                answer.NextNeedsYou = NextNeedsYou(inputs.AccountRoster, inputs.SessionId);
            }

            return answer;
        }

        // SNOOZED. The owner parked it, so nothing here is waiting on him - but the screen used to say the word
        // "Snoozed" twice and nothing else, and the Wingman's account of the stop he snoozed OVER was simply
        // dropped. Both halves are restored: when it comes back, and what it was he decided to leave.
        if (string.Equals(state, WingmanNowStates.Snoozed, StringComparison.Ordinal))
        {
            if (row?.SnoozeUntil is { } until)
            {
                answer.SnoozedUntil = new WingmanNowWhenDto { Lead = SnoozedUntilLead, AtUtc = until };
            }
            else
            {
                // A DEFERRED HOLD HAS NO DEADLINE YET (see SessionDto.SnoozeUntil), and neither has a snooze
                // that waits for him. The headline says what ends it rather than naming a moment nothing
                // recorded - a lead with no instant after it is a sentence that stops in the middle.
                answer.Headline = SnoozedNoDeadlineHeadline;
            }

            // THE STOP IT WAS SNOOZED OVER, read from the stored history rather than from the row: a snooze may
            // or may not leave a verdict in force on the row, and the account of the stop is worth the same
            // either way. When one IS still in force, its words come too - that narration is the whole reason
            // the snooze meant anything, and throwing it away is what left this screen with two words on it.
            answer.LastStop = LastStop(verdicts, SnoozedOverLead);
            if (live is not null)
            {
                answer.Story = NullIfBlank(live.Summary);
                answer.AgentSaid = AgentSaid(live, row);
                answer.WholeReply = WholeReply(inputs.Conversation);
            }

            return answer;
        }

        // THE THREE STATES WITH NO WINGMAN ACCOUNT OF THE STOP TO SHOW - it has not finished reading, its answer was
        // refused, or it was never switched on. Each shows the SESSION's own last words instead, so the owner can act
        // on what the agent said without an explanation he may not need.
        if (state is WingmanNowStates.Reading or WingmanNowStates.Failed or WingmanNowStates.SwitchedOff)
        {
            answer.LastWords = LastWords(inputs.Conversation, row);

            if (string.Equals(state, WingmanNowStates.Reading, StringComparison.Ordinal))
            {
                answer.Headline = ReadingHeadline;
                answer.Story = ReadingStory;
            }
            else if (string.Equals(state, WingmanNowStates.Failed, StringComparison.Ordinal))
            {
                answer.FailedHeadline = FailedHeadline;
                answer.FailedStory = FailedStory(row);
                answer.LastGood = LastGood(verdicts, row);
            }
            else
            {
                answer.SwitchedOff = new WingmanNowSwitchedOffDto
                {
                    Headline = SwitchedOffHeadline,
                    Story = SwitchedOffStory,
                    SettingsLinkText = SwitchedOffLinkText,
                };
            }

            return answer;
        }

        if (live is null)
        {
            // An "other" row says what the ROW says and nothing more. Inventing a headline for a state this fold has
            // not been taught would be the Gateway guessing, which is the failure the dumb-client rule is about.
            answer.Headline = answer.PillText;
            return answer;
        }

        answer.Headline = NullIfBlank(live.Label);
        answer.Story = NullIfBlank(live.Summary);
        answer.AgentSaid = AgentSaid(live, row);
        answer.WholeReply = WholeReply(inputs.Conversation);

        // AMBIGUOUS IS THE JUDGE'S OWN WORD FOR "not sure", and "cannot tell" is the answer that says the record did
        // not support a decision. Both leave the row exactly as the detector made it (an ambiguous answer never
        // demotes a red row), so the tag is the only thing that tells the owner the words above were not confident.
        answer.Unsure = IsUnsure(live);
        if (answer.Unsure)
        {
            answer.UnsureTag = UnsureTag;
            answer.UnsureLine = UnsureLine;
        }

        if (string.Equals(state, WingmanNowStates.NeedsYou, StringComparison.Ordinal))
        {
            answer.Needs = Needs(live);
            answer.CanAnswerByOption = CanAnswerByOption(live, verdicts);
        }
        else
        {
            answer.CalmCard = CalmCard(state);
            if (string.Equals(state, WingmanNowStates.CarryingOn, StringComparison.Ordinal))
            {
                // THE INSTANT COMES FROM THE FUNCTION THE CLOCK ITSELF EXPIRES ON, never from arithmetic repeated
                // here. The sentence promises the owner a moment; if this file worked that moment out a second
                // way, the promise and the expiry would be free to disagree, and the one he would notice is the
                // row going red at a time the card told him it would not.
                var deadline = TurnVerdictWatchdog.DeadlineFor(live, inputs.OwnedSessions);
                if (deadline is { } at)
                {
                    answer.CarryingOnDeadline = new WingmanNowDeadlineDto
                    {
                        Before = CarryingOnDeadlineBefore,
                        AtUtc = at,
                        After = CarryingOnDeadlineAfter,
                    };
                }
                else
                {
                    // No clock is counting, because a session it owns is still running. The card says so in words
                    // that name no time - there is none to name - rather than leaving the owner to wonder why the
                    // sentence he saw last time has gone.
                    answer.CalmCard!.Body = CarryingOnNoDeadlineBody;
                }
            }
        }

        return answer;
    }

    /// <summary>
    /// Which state the view is in, from the verdict in force.
    ///
    /// The two "needs a person" words and "cannot tell" all land on needs you: a stop nobody could read is a stop no
    /// judgement should be trusted on, so it goes to the owner rather than being quietly filed as calm. That is the
    /// same direction every rule in the verdict contract leans.
    /// </summary>
    private static string StateOf(TurnVerdictDto? live, SessionDto? row, bool? wingmanSwitchedOff)
    {
        // WORKING OUTRANKS EVERYTHING, and that is the product's own law, not a preference here: if a session is
        // working it is blue, always, and nothing may be added above that check. So a working session on an account
        // whose Wingman is switched off is NOT "switched off" wearing the word "Stopped" - it falls through to the
        // row's own state, which is true. (Working gets its own drawing in slice 4; until then it is "other".)
        var working = row is not null && SessionOrdering.IsWorkingSession(row);

        // SWITCHED OFF IS AN ACCOUNT FACT, so it outranks every stopped state below: when nothing reads this
        // session's stops, "the Wingman is reading it" and "the Wingman could not explain it" are both false.
        if (working) return WingmanNowStates.Working;

        // SNOOZED SITS DIRECTLY UNDER WORKING, exactly where the row's own colour ladder puts it
        // (SessionOrdering.EffectiveColor: `IsWorking ? blue : s.OnHold ? grey`). It reads the SAME field that
        // ladder reads, so a snoozed session that still carries a judged verdict cannot show a red "Needs you"
        // pill over a grey dot. It is above the switched-off arm for the same reason: with the account's
        // Wingman off the row still says "Snoozed", and that is the true thing about the session.
        if (row is not null && row.OnHold) return WingmanNowStates.Snoozed;

        if (wingmanSwitchedOff == true) return WingmanNowStates.SwitchedOff;

        if (row is not null)
        {
            // The row's own verdict state, which the roster fold stamped. Reading and failed are facts ABOUT the
            // Wingman rather than about the stop, so they are read from there and not from a verdict word.
            if (string.Equals(row.VerdictState, VerdictStates.Reading, StringComparison.Ordinal))
                return WingmanNowStates.Reading;
            if (string.Equals(row.VerdictState, VerdictStates.Failed, StringComparison.Ordinal))
                return WingmanNowStates.Failed;
        }

        if (live is null) return WingmanNowStates.Other;
        return live.Verdict switch
        {
            TurnVerdictVocabulary.Finished =>
                string.Equals(live.FinishedKind, "report", StringComparison.Ordinal)
                    ? WingmanNowStates.Report
                    // "done" and a verdict stored before the kind existed both read as done: the work is complete is
                    // what "finished" meant before the owner split it, so an old record keeps its old meaning.
                    : WingmanNowStates.Done,
            TurnVerdictVocabulary.ContinuesAlone => WingmanNowStates.CarryingOn,
            TurnVerdictVocabulary.NeededYou => WingmanNowStates.NeedsYou,
            TurnVerdictVocabulary.StuckNeedsPerson => WingmanNowStates.NeedsYou,
            TurnVerdictVocabulary.StuckRecoverable => WingmanNowStates.NeedsYou,
            TurnVerdictVocabulary.CannotTell => WingmanNowStates.NeedsYou,
            // A word this Gateway does not know is not forced into a drawing. It falls to "other", where the row's
            // own label is shown - true about the session whatever the judge answered.
            _ => WingmanNowStates.Other,
        };
    }

    /// <summary>
    /// WHICH SESSION THIS IS: "112 - Cube Data and Projects - Architect".
    ///
    /// The number leads when the row carries one - it is the short name the Sessions list and the command line
    /// both use - and the name follows it. A row this Gateway has nothing pushed for leaves only the session's
    /// identifier: machine output, and shown anyway, because on the one screen where sending the right answer to
    /// the wrong session is the damage, an identifier he can check beats a blank he cannot.
    /// </summary>
    private static string SessionLine(string sessionId, SessionDto? row)
    {
        var name = NullIfBlank(row?.Name);
        if (row?.Number is { } number)
            return name is null ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                : number.ToString(System.Globalization.CultureInfo.InvariantCulture) + " - " + name;
        return name ?? sessionId;
    }

    /// <summary>What sending the reply box does. The second sentence is true only where something was asked.</summary>
    private static string ReplyHint(string state)
        => string.Equals(state, WingmanNowStates.NeedsYou, StringComparison.Ordinal)
            ? ReplyHintAsked
            : ReplyHintPlain;

    private static string PillText(string state, SessionDto? row) => state switch
    {
        WingmanNowStates.NeedsYou => PillNeedsYou,
        WingmanNowStates.Reading => PillReading,
        WingmanNowStates.Working => PillWorking,
        WingmanNowStates.JustAnswered => PillJustAnswered,
        WingmanNowStates.SwitchedOff => PillSwitchedOff,
        WingmanNowStates.CarryingOn => PillCarryingOn,
        WingmanNowStates.Done => PillDone,
        WingmanNowStates.Report => PillReport,
        // The row's own words, verbatim - "Working", "Snoozed", "Exited", "Wingman reading". The roster fold already
        // decided them, and a second set of words for the same row is a second answer to one question.
        _ => NullIfBlank(row?.StateLabel) ?? OtherWithNoLabel,
    };

    private static WingmanNowWhenDto? When(string state, TurnVerdictDto? live, SessionDto? row,
        IReadOnlyList<AnsweredTurnVerdict> verdicts, WingmanNowAnsweredDto? answered)
    {
        // WORKING MEASURES FORWARD, not back: "Working for 6 minutes". The moment it started is the moment its last
        // stop stopped being the live one, which is what SupersededAtUtc records - so the number is the length of
        // this working stretch and not the age of a stop that is over.
        // HIS ANSWER'S OWN MOMENT, measured forward and shown with "ago": "You answered 20 seconds ago". The
        // clock time is not repeated here - the card below it already says when it was sent.
        if (string.Equals(state, WingmanNowStates.JustAnswered, StringComparison.Ordinal))
        {
            return answered is null ? null : new WingmanNowWhenDto
            {
                Lead = AnsweredWhenLead,
                AtUtc = answered.AtUtc,
                ShowAgo = true,
                ElapsedOnly = true,
            };
        }

        if (string.Equals(state, WingmanNowStates.Working, StringComparison.Ordinal))
        {
            var since = WorkingSince(verdicts);
            // No superseded record means nothing here knows when this stretch began - a session that has not
            // stopped since the Gateway learned of it. The pill still says Working; no number is invented.
            return since is null ? null : new WingmanNowWhenDto
            {
                Lead = WorkingForLead,
                AtUtc = since.Value,
                ElapsedOnly = true,
            };
        }

        var at = live is not null && live.TurnEndObservedAtUtc != default
            ? live.TurnEndObservedAtUtc
            : row?.WaitingSince;
        if (at is null) return null;
        return new WingmanNowWhenDto
        {
            Lead = StoppedAtLead,
            AtUtc = at.Value,
            // BOTH CLOCKS, ALWAYS. This used to drop "ago" on done and on a report, so that nothing was said to be
            // pressing about a session that wanted nothing - and the cost of that was that a session that finished
            // two minutes ago and one that finished yesterday were the same line on the screen. "How long ago" is
            // not pressure, it is which of two identical sentences he is reading.
            ShowAgo = true,
        };
    }

    private static string? ReplyPlaceholder(string state) => state switch
    {
        WingmanNowStates.NeedsYou => ReplyPlaceholderNeedsYou,
        WingmanNowStates.Report => ReplyPlaceholderReport,
        WingmanNowStates.Reading => ReplyPlaceholderReading,
        WingmanNowStates.Working or WingmanNowStates.JustAnswered => ReplyPlaceholderWorking,
        // Failed and switched off both fall to the "other" words below on purpose - with no judgement to answer,
        // the box goes to the SESSION, which is exactly what those words say.
        WingmanNowStates.Failed or WingmanNowStates.SwitchedOff => ReplyPlaceholderOther,
        // Done and carrying on offer no reply box: the session is finished, or it is about to work again on its own,
        // and a box that invites an answer to neither is an invitation to interrupt for no reason.
        WingmanNowStates.Done or WingmanNowStates.CarryingOn => null,
        _ => ReplyPlaceholderOther,
    };

    /// <summary>
    /// The calm card, INCLUDING ITS COLOUR. The approved mockup tints the finished work and the report cyan and
    /// carrying on purple, and the colour is named here for the same reason the words are: reading it off the state
    /// name in the client would be the client deciding what a state means.
    /// </summary>
    private static WingmanNowCardDto? CalmCard(string state) => state switch
    {
        WingmanNowStates.Done => new WingmanNowCardDto
        {
            Heading = DoneHeading, Body = DoneBody, Tone = WingmanNowCardTones.Cyan,
        },
        WingmanNowStates.Report => new WingmanNowCardDto
        {
            Heading = ReportHeading, Body = ReportBody, Tone = WingmanNowCardTones.Cyan,
        },
        // THE HEADING ALONE, and its body is filled in by the caller. The sentence under this one is about the
        // clock, and it takes two forms - a deadline with an instant in the middle of it, or no clock at all
        // because a session it owns is still running - so the caller, which has the verdict and the owned
        // sessions, decides which. Both forms are this fold's words; neither is the client's.
        WingmanNowStates.CarryingOn => new WingmanNowCardDto
        {
            Heading = CarryingOnHeading, Tone = WingmanNowCardTones.Purple,
        },
        _ => null,
    };

    private static bool IsUnsure(TurnVerdictDto live)
        => string.Equals(live.Confidence, "ambiguous", StringComparison.Ordinal)
           || string.Equals(live.Verdict, TurnVerdictVocabulary.CannotTell, StringComparison.Ordinal)
           // The Wingman never answers this word (it belongs to the detector) and the contract rejects it, so this
           // arm is a belt on a rule enforced elsewhere: if one ever reached a row, "the boundary fired while the
           // session was still working" is precisely a stop nobody should act on without reading it.
           || string.Equals(live.Verdict, TurnVerdictVocabulary.NotATurnEnd, StringComparison.Ordinal);

    private static WingmanNowSaidDto? AgentSaid(TurnVerdictDto live, SessionDto? row)
    {
        var sentence = NullIfBlank(live.Evidence);
        if (sentence is null) return null;
        var tool = NullIfBlank(row?.AgentToolDisplay);
        return new WingmanNowSaidDto
        {
            Who = tool is null ? SomethingSaidWho : tool + " said",
            Text = sentence,
        };
    }

    private static WingmanNowNeedsDto Needs(TurnVerdictDto live)
    {
        var needs = new WingmanNowNeedsDto
        {
            Heading = NeedsHeading,
            Recommends = Recommends(live),
            Question = NullIfBlank(live.Menu?.Question),
            OptionsLead = live.Options.Count > 0 ? OptionsLeadText : null,
            RiskFlag = RiskFlag(live.Risk),
            RiskLine = RiskLine(live.Risk),
            ConfirmBeforeSending = RiskFlag(live.Risk) is not null,
        };
        for (var i = 0; i < live.Options.Count; i++)
        {
            var option = live.Options[i];
            needs.Options.Add(new WingmanNowOptionDto
            {
                // The POSITION, because that is what the answer route takes. The option's own bytes are deliberately
                // not carried: the Cockpit sends an index and the Gateway decides what that means, so nothing a
                // client holds can be replayed into a session as keystrokes.
                Index = i,
                // And the number he READS, which is that plus one. See WingmanNowOptionDto.Number.
                Number = i + 1,
                Key = option.Key,
                Note = option.Note,
                Recommended = option.Recommended,
            });
        }
        return needs;
    }

    /// <summary>
    /// THE AGENT'S RECOMMENDATION, OR NOTHING - and nothing is the common answer.
    ///
    /// The screen already carries the ask three times over: the headline, the story, and the agent's own decisive
    /// sentence. A judge asked for "the agent's own recommendation, quoted or closely paraphrased" very often
    /// answers with the ask again, and a fourth copy of one sentence under the words "It recommends" is how a
    /// reader learns to stop reading the card. So this line is shown only when it ADDS something:
    ///
    /// 1. NOT A QUESTION. A recommendation cannot be a question. When the agent's own words are one - "Want me to
    ///    land it to main and deploy?" - there is nothing to state, and rewriting a question into a statement
    ///    would be this fold putting words in the agent's mouth. The RECOMMENDED mark on the option is what says
    ///    which way it leans, and it says it without a sentence.
    /// 2. NOT THE ASK AGAIN. Measured against the three sentences already on the card - the menu's question, the
    ///    Wingman's headline and the agent's receipt - with punctuation, case and spacing ignored. Either
    ///    containing the other counts, because a paraphrase that only adds "Should I " adds nothing.
    ///
    /// What survives both is a statement that says something the rest of the card does not, and the lead makes it
    /// read as one: "It recommends: committing and deploying."
    /// </summary>
    private static string? Recommends(TurnVerdictDto live)
    {
        if (NullIfBlank(live.AgentRecommends) is not { } recommends) return null;
        if (recommends.TrimEnd().EndsWith('?')) return null;

        foreach (var already in new[] { live.Menu?.Question, live.Label, live.Evidence })
        {
            if (NullIfBlank(already) is { } other && SaysTheSameThing(other, recommends)) return null;
        }

        return RecommendsLead + recommends;
    }

    /// <summary>
    /// Whether two owner-facing sentences say the same thing: equal, or one inside the other, once punctuation,
    /// case and runs of whitespace are taken out.
    ///
    /// CONTAINMENT RATHER THAN EQUALITY, because the sentences this is asked about differ by a lead-in and
    /// nothing else - "Commit and deploy the fixes?" against "Want me to commit and deploy the fixes?". A test
    /// for equality would pass every one of them through and change nothing.
    /// </summary>
    private static bool SaysTheSameThing(string left, string right)
    {
        var a = Normalise(left);
        var b = Normalise(right);
        if (a.Length == 0 || b.Length == 0) return false;
        return a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
    }

    /// <summary>A sentence with its punctuation, case and spacing taken out, for comparing one against another.
    /// It is never shown to anybody - only compared - so nothing here has to read.</summary>
    private static string Normalise(string text)
    {
        var chars = new List<char>(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) chars.Add(char.ToLowerInvariant(c));
            else if (c == ' ' && chars.Count > 0 && chars[^1] != ' ') chars.Add(' ');
        }
        return new string(chars.ToArray()).Trim();
    }

    /// <summary>The short warning for a risk word, or null for "none" and for a record that carries none.</summary>
    private static string? RiskFlag(string? risk) => risk switch
    {
        TurnVerdictVocabulary.RiskIrreversible => RiskFlagIrreversible,
        TurnVerdictVocabulary.RiskStandingGrant => RiskFlagStandingGrant,
        TurnVerdictVocabulary.RiskSpendsMoney => RiskFlagSpendsMoney,
        _ => null,
    };

    /// <summary>What that warning means, in one sentence.</summary>
    private static string? RiskLine(string? risk) => risk switch
    {
        TurnVerdictVocabulary.RiskIrreversible => RiskLineIrreversible,
        TurnVerdictVocabulary.RiskStandingGrant => RiskLineStandingGrant,
        TurnVerdictVocabulary.RiskSpendsMoney => RiskLineSpendsMoney,
        _ => null,
    };

    /// <summary>
    /// Whether the options may still be tapped. Every gate below can only turn a tap OFF, and each one alone is
    /// enough: the verdict is the one in force (it is the row's, so it is neither failed nor superseded), nobody has
    /// answered it, and it offers two or more ways of answering - one option is not a choice.
    ///
    /// AN UNKNOWN ANSWER MOMENT COUNTS AS ANSWERED. When the verdict in force is not in the history window read for
    /// this view, this fold cannot say whether the owner has already answered it, and offering a tap it cannot
    /// support would end in the answer route refusing it with "that was already answered". The options stay readable
    /// and the reply box stays open, so nothing is lost but the one-tap path.
    /// </summary>
    private static bool CanAnswerByOption(TurnVerdictDto live, IReadOnlyList<AnsweredTurnVerdict> verdicts)
    {
        if (live.Options.Count < 2) return false;
        var stored = verdicts.FirstOrDefault(v =>
            string.Equals(v.Verdict.VerdictId, live.VerdictId, StringComparison.Ordinal));
        return stored is not null && stored.AnsweredAtUtc is null;
    }

    /// <summary>
    /// The agent's whole last reply, word for word: the TEXT parts of the newest assistant message, joined.
    ///
    /// Thinking, tool calls and tool results are left out deliberately. This is shown to the owner as "the whole
    /// reply, word for word", and a reply is what the agent SAID - folding a tool's raw JSON into it would make that
    /// sentence false.
    /// </summary>
    private static string? WholeReply(WingmanNowConversation? conversation)
    {
        if (conversation is null || !conversation.Supported) return null;
        var messages = conversation.Messages;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (!string.Equals(message.Role, "Assistant", StringComparison.OrdinalIgnoreCase)) continue;
            var text = string.Join("\n\n", message.Parts
                .Where(p => string.Equals(p.Kind, "Text", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t)));
            return NullIfBlank(text);
        }
        return null;
    }

    /// <summary>
    /// The session's own last words, shortened, with who said them.
    ///
    /// Cut on a WORD BOUNDARY and closed with an ellipsis, because this stands where a headline stands: a cut through
    /// the middle of a word reads as a rendering fault rather than as an excerpt. A reply already shorter than the
    /// limit is shown whole, with no ellipsis promising more.
    /// </summary>
    private static WingmanNowSaidDto? LastWords(WingmanNowConversation? conversation, SessionDto? row)
    {
        var text = WholeReply(conversation);
        if (text is null) return null;

        text = Shorten(text);
        if (text.Length == 0) return null;

        var tool = NullIfBlank(row?.AgentToolDisplay);
        return new WingmanNowSaidDto
        {
            // "Claude Code's last words" when the row names the tool; never a guessed name.
            Who = tool is null ? LastWordsWhoUnnamed : tool + "'s last words",
            Text = text,
        };
    }

    /// <summary>
    /// Why the Wingman's answer was refused, in ITS OWN WORDS, then what the refusal leaves the row.
    ///
    /// The reason is carried verbatim and never rewritten: it is the record of a refusal, and a tidied-up version of
    /// it is a different claim about what happened. When no reason was recorded there is no story - a sentence about
    /// the colour alone would be the Gateway explaining a refusal it cannot describe.
    /// </summary>
    private static string? FailedStory(SessionDto? row)
    {
        var reason = NullIfBlank(row?.TurnVerdict?.FailureReason);
        if (reason is null) return null;

        var colour = NullIfBlank(row?.EffectiveColor);
        if (colour is null) return reason;

        var joined = reason.EndsWith('.') || reason.EndsWith('!') || reason.EndsWith('?') ? reason : reason + ".";
        return joined + " " + FailedRowSuffixBefore + colour + FailedRowSuffixAfter;
    }

    /// <summary>
    /// The last stop the Wingman DID explain - the newest ACCEPTED verdict in the stored history that is not the
    /// record in force.
    ///
    /// The history arrives newest first, so the first accepted record that is not the live one is the answer. A
    /// verdict whose word this Gateway has no pill words for is skipped rather than shown with a blank: the line is
    /// "&lt;pill words&gt; - &lt;headline&gt;", and half of it missing is not a reassurance about anything.
    /// </summary>
    private static WingmanNowPastDto? LastGood(IReadOnlyList<AnsweredTurnVerdict> verdicts, SessionDto? row)
    {
        var liveId = row?.TurnVerdict?.VerdictId;
        foreach (var stored in verdicts)
        {
            var verdict = stored.Verdict;
            if (verdict.Failed) continue;
            if (liveId is not null && string.Equals(verdict.VerdictId, liveId, StringComparison.Ordinal)) continue;

            if (PastPillWords(verdict) is null) continue;

            var at = verdict.TurnEndObservedAtUtc != default ? verdict.TurnEndObservedAtUtc : verdict.JudgedAtUtc;
            if (at == default) continue;

            return new WingmanNowPastDto
            {
                Lead = LastGoodLead,
                AtUtc = at,
                Text = PastText(verdict),
            };
        }

        return null;
    }

    /// <summary>The pill words a PAST verdict would have worn. Null for a verdict word this fold draws no state
    /// for - the row's own label stands in for that live, and a past record has no row of its own to borrow.
    /// </summary>
    private static string? PastPillWords(TurnVerdictDto verdict) => StateOf(verdict, null, null) switch
    {
        WingmanNowStates.NeedsYou => PillNeedsYou,
        WingmanNowStates.CarryingOn => PillCarryingOn,
        WingmanNowStates.Done => PillDone,
        WingmanNowStates.Report => PillReport,
        _ => null,
    };

    /// <summary>When this working stretch began: the newest superseded record's superseding moment. Null when no
    /// record has been superseded, which is a session that has not stopped since this Gateway learned of it.
    /// </summary>
    private static DateTime? WorkingSince(IReadOnlyList<AnsweredTurnVerdict> verdicts)
    {
        foreach (var stored in verdicts)
        {
            if (stored.Verdict.SupersededAtUtc is { } at) return at;
        }

        return null;
    }

    /// <summary>
    /// The stop the row has just come from, as one line - the newest ACCEPTED record, superseded or not.
    ///
    /// Superseded is the normal case here rather than an edge: a working session's last stop is superseded by
    /// definition, because going back to work is what superseded it.
    /// </summary>
    private static WingmanNowPastDto? LastStop(IReadOnlyList<AnsweredTurnVerdict> verdicts, string lead)
    {
        if (LastStopRecord(verdicts) is not { } stored) return null;

        var verdict = stored.Verdict;
        return new WingmanNowPastDto
        {
            Lead = lead,
            AtUtc = StopMoment(verdict),
            Text = PastText(verdict),
        };
    }

    /// <summary>
    /// What a past stop SAYS, as one line: the pill's words, a dash, and the Wingman's own headline.
    ///
    /// EXCEPT FOR A STOP THE CARRYING-ON CLOCK WROTE, which is a whole sentence instead - see
    /// <see cref="ExpiredPastText"/>. That record's label is a reason code rather than an ask, and the ordinary
    /// shape glued a status word onto it with a dash: "Needs you - Said it would continue and did not". It is
    /// recognised by the model stamped on the record, which the clock and only the clock writes.
    /// </summary>
    private static string PastText(TurnVerdictDto verdict)
    {
        if (string.Equals(verdict.Model, TurnVerdictWatchdog.ClockModel, StringComparison.Ordinal))
            return ExpiredPastText;

        var words = PastPillWords(verdict)!;
        var label = NullIfBlank(verdict.Label);
        return label is null ? words : words + " - " + label;
    }

    /// <summary>
    /// The RECORD behind that line - the same scan, kept in one place because two readers need it: the line the
    /// owner reads, and the answer to it. A second copy of this scan is how a card comes to say what he answered
    /// while the line beside it names a different stop.
    /// </summary>
    private static AnsweredTurnVerdict? LastStopRecord(IReadOnlyList<AnsweredTurnVerdict> verdicts)
    {
        foreach (var stored in verdicts)
        {
            var verdict = stored.Verdict;
            if (verdict.Failed) continue;
            if (PastPillWords(verdict) is null) continue;
            if (StopMoment(verdict) == default) continue;
            return stored;
        }

        return null;
    }

    /// <summary>When a stop happened: the moment the turn ended, falling back to the moment it was judged.</summary>
    private static DateTime StopMoment(TurnVerdictDto verdict)
        => verdict.TurnEndObservedAtUtc != default ? verdict.TurnEndObservedAtUtc : verdict.JudgedAtUtc;

    /// <summary>
    /// WHAT HE ANSWERED - from either route, as one answer, because from his side they are one act.
    ///
    /// 1. THE OPTION ROUTE. The verdict row carries the moment and, beside it, the answer that route stored: the
    ///    option positions he chose and the words those options are. THIS VIEW READS THAT ONE RECORD and keeps
    ///    none of its own, so what it reads back to him and what the walkthrough recorded cannot disagree about
    ///    what he decided. Both or neither: a row with a moment and no stored answer is every row answered before
    ///    that record existed, and "You answered:" with nothing after the colon is worse than saying nothing.
    ///    An answer that chose NO option is that route's confirm of a reply typed on the screen, and its words
    ///    are a sentence about the sending rather than about a decision - so it falls through to case 2, which
    ///    has what he actually said.
    /// 2. A TYPED REPLY, which touches no verdict at all - he answered in the terminal, on the phone, by voice, or
    ///    in the reply box. It is the FIRST user message after the stop.
    ///
    /// Null whenever the answer is older than <see cref="JustAnsweredWindow"/>, which is what ends the state.
    /// </summary>
    private static WingmanNowAnsweredDto? Answered(IReadOnlyList<AnsweredTurnVerdict> verdicts,
        WingmanNowConversation? conversation, DateTime? workingSince, DateTime? nowUtc)
    {
        if (nowUtc is not { } now) return null;
        if (LastStopRecord(verdicts) is not { } stop) return null;

        string? text;
        DateTime at;
        if (stop.AnsweredAtUtc is { } optionAt && stop.Answer is { OptionIndexes.Count: > 0 } answer
            && NullIfBlank(answer.Words) is { } chosen)
        {
            text = chosen;
            at = optionAt;
        }
        else if (TypedReply(conversation, StopMoment(stop.Verdict)) is { } typed)
        {
            text = typed.Text;
            at = typed.At;
        }
        else
        {
            return null;
        }

        if (now - at >= JustAnsweredWindow) return null;

        return new WingmanNowAnsweredDto
        {
            Headline = AnsweredHeadlineLead + text,
            Text = text,
            SentLead = AnsweredSentLead,
            AtUtc = at,
            WorkingAgainAfterText = WorkingAgainText(workingSince, at),
        };
    }

    /// <summary>
    /// The first thing HE typed after the stop, or null.
    ///
    /// A DOORBELL LINE stops the search rather than being skipped past: the first thing typed after a stop is the
    /// answer to it, and a doorbell line was typed by this product, not by him. Skipping past it would show him a
    /// later message as though it had answered this stop; claiming it would put our own delivery line in front of
    /// him under the words "You answered".
    ///
    /// Recognising it is a fact rather than a guess, because <see cref="FleetDoorbellLine.Marker"/> is a string
    /// this product itself writes and is the whole of the line's fixed opening. A session-to-session message used
    /// to be recognised the same way, by reading back the frame the Gateway wrote around it; that frame was
    /// deleted on 17 September 2026 with the Message Load mission, because a fleet message is now a queued inbox
    /// record and nothing types one into a session. The doorbell line is what is typed instead.
    /// </summary>
    private static (string Text, DateTime At)? TypedReply(WingmanNowConversation? conversation, DateTime after)
    {
        if (conversation is null || !conversation.Supported) return null;

        foreach (var message in conversation.Messages)
        {
            if (!string.Equals(message.Role, "User", StringComparison.OrdinalIgnoreCase)) continue;
            if (message.Timestamp is not { } stamp) continue;

            var at = stamp.UtcDateTime;
            if (at <= after) continue;

            var text = MessageText(message);
            if (text is null) continue;
            if (text.StartsWith(FleetDoorbellLine.Marker, StringComparison.Ordinal)) return null;

            return (Shorten(text), at);
        }

        return null;
    }

    /// <summary>
    /// That the session took his answer, in finished words. Null when this Gateway cannot tell - no record of it
    /// going back to work, or one from before he answered, which would make the confirmation a lie.
    /// </summary>
    private static string? WorkingAgainText(DateTime? workingSince, DateTime answeredAt)
    {
        if (workingSince is not { } since) return null;
        if (since < answeredAt) return null;
        return WorkingAgainBefore + Gap(since - answeredAt) + WorkingAgainAfter;
    }

    /// <summary>
    /// A short gap in the words a person says: "1 second", "2 seconds", "3 minutes", "2 hours". Owner-facing, so
    /// it lives here with every other owner-facing word rather than borrowing a log formatter that rounds "2
    /// seconds" down to "0 minutes".
    /// </summary>
    private static string Gap(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;

        var seconds = (long)Math.Round(span.TotalSeconds);
        if (seconds < 60) return seconds == 1 ? "1 second" : seconds + " seconds";

        var minutes = seconds / 60;
        if (minutes < 60) return minutes == 1 ? "1 minute" : minutes + " minutes";

        var hours = minutes / 60;
        return hours == 1 ? "1 hour" : hours + " hours";
    }

    /// <summary>
    /// THE NEXT SESSION WAITING ON HIM, in the Sessions list's own order over the Sessions list's own fold.
    ///
    /// The order and the bucket are both <see cref="SessionOrdering"/>'s, so this decides nothing about which
    /// session is next - it reads the same answer the list reads. Deciding it here a second way is exactly how
    /// the tab would come to send him somewhere the list does not have at the top.
    /// </summary>
    private static WingmanNowNextDto? NextNeedsYou(IReadOnlyList<SessionDto>? roster, string sessionId)
    {
        if (roster is null || roster.Count == 0) return null;

        foreach (var row in SessionOrdering.InWaitingOrder(roster))
        {
            if (string.Equals(row.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) continue;
            // InWaitingOrder carries the calm band after the reds; only the reds are waiting on him.
            if (SessionOrdering.Classify(row) != SessionOrdering.TriageBucket.NeedsYou) continue;

            return new WingmanNowNextDto
            {
                Heading = NextHeading,
                SessionId = row.SessionId,
                Name = NullIfBlank(row.Name) ?? row.SessionId,
                Label = NullIfBlank(SessionOrdering.StateLabel(row)),
                LinkText = NextLinkText,
            };
        }

        return null;
    }

    /// <summary>
    /// What the session was last asked, and by whom WHEN THAT IS KNOWN.
    ///
    /// Two outcomes for who, and the second is the important one:
    ///
    /// 1. THE OWNER, but only when the Director's record of his last turn in this session sits at or after the
    ///    previous stop and within <see cref="OwnerTurnTolerance"/> of the message. Both conditions matter: the
    ///    stamp alone would credit him with every message that arrived after he last typed, however much later.
    /// 2. NOBODY NAMED. The card says what was asked and when, and nothing about who. A guess here is a name in
    ///    front of the owner that nothing verified, which is worse than an honest silence.
    ///
    /// A session-to-session message used to be a third outcome, recognised by reading back a frame this Gateway
    /// itself wrote. That frame no longer exists: since the Message Load mission a fleet message is a queued
    /// inbox record and nothing types a framed message into a session, so there is no format left to parse and
    /// keeping a parser for one would be reading a string nothing writes. Such a message is simply not claimed
    /// to be from anyone, which is outcome 2.
    ///
    /// A USER MESSAGE WITH NO WORDS IN IT IS SKIPPED, NOT AN ANSWER OF "NOTHING" - and that one line is why this
    /// card was blank on the owner's screen for every working session. In a Claude Code transcript a TOOL RESULT
    /// is a message of role "user" whose only part is a ToolResult (see ClaudeTranscriptReader: a line of type
    /// "user" carrying a tool_result block). A working session is mid-tool-loop almost by definition, so the
    /// newest "user" message is almost always one of those - and this scan used to stop dead on it and say
    /// nothing had been asked. What was asked is the newest user message that carries WORDS, which is what the
    /// card claims to show.
    ///
    /// A missing TIMESTAMP still ends the scan rather than skipping past it: the card says when it was asked,
    /// and a message this Gateway cannot place in time is not a better answer than silence.
    /// </summary>
    private static WingmanNowAskedDto? LastAsked(WingmanNowConversation? conversation, SessionDto? row,
        IReadOnlyList<AnsweredTurnVerdict> verdicts)
    {
        if (conversation is null || !conversation.Supported) return null;

        var messages = conversation.Messages;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (!string.Equals(message.Role, "User", StringComparison.OrdinalIgnoreCase)) continue;

            var text = MessageText(message);
            if (text is null) continue;
            if (message.Timestamp is not { } stamp) return null;

            var at = stamp.UtcDateTime;
            string? by = OwnerAskedIt(row, verdicts, at) ? AskedByYou : null;

            return new WingmanNowAskedDto
            {
                Heading = LastAskedHeading,
                Text = text,
                AtUtc = at,
                By = by,
                WhenLead = by is null ? AskedWhenLeadUnnamed : by + AskedWhenLeadSuffix,
            };
        }

        return null;
    }

    /// <summary>
    /// Was this message the owner's own? Only when his recorded turn in this session is at or after the previous
    /// stop AND within the tolerance of the message.
    ///
    /// The "at or after the previous stop" half is what stops a stamp from a much earlier conversation being read
    /// as an answer to this one. With no previous stop recorded there is nothing to be after, so the tolerance
    /// alone decides.
    /// </summary>
    private static bool OwnerAskedIt(SessionDto? row, IReadOnlyList<AnsweredTurnVerdict> verdicts, DateTime askedAt)
    {
        if (row?.LastOwnerTurnAtUtc is not { } owner) return false;
        var ownerUtc = owner.Kind == DateTimeKind.Utc ? owner : owner.ToUniversalTime();

        var previousStop = LastStop(verdicts, LastStopLead)?.AtUtc;
        if (previousStop is { } stop && ownerUtc < stop) return false;

        var gap = ownerUtc > askedAt ? ownerUtc - askedAt : askedAt - ownerUtc;
        return gap <= OwnerTurnTolerance;
    }

    /// <summary>One message's own text - its Text parts joined, or null when it carries none.</summary>
    private static string? MessageText(HistoryMessageDto message)
        => NullIfBlank(string.Join("\n\n", message.Parts
            .Where(p => string.Equals(p.Kind, "Text", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))));

    /// <summary>
    /// Owner-facing text cut to one line and to <see cref="LastWordsLength"/>.
    ///
    /// Cut on a WORD BOUNDARY and closed with an ellipsis, because this stands where a headline stands: a cut
    /// through the middle of a word reads as a rendering fault rather than as an excerpt. Text already shorter
    /// than the limit is shown whole, with no ellipsis promising more.
    /// </summary>
    private static string Shorten(string text)
    {
        // One line: the slot is one line high, and the text's own newlines would make it several.
        text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length <= LastWordsLength) return text;

        var cut = text.LastIndexOf(' ', LastWordsLength - 1);
        // A first "word" longer than the whole limit has no boundary to cut on, so it is cut at the limit - an
        // unbroken 200-character token is a URL or a hash, and showing none of it is worse.
        return (cut > 0 ? text[..cut] : text[..LastWordsLength]).TrimEnd() + " ...";
    }

    /// <summary>
    /// THE VOICE CONTROL: one of four offers, and the fourth is "nothing to offer".
    ///
    /// FOUR STATES ARE OFFERED NOTHING - being read, working, just answered, and the Wingman switched off. Three of
    /// them have no stop on the screen to read aloud, and the fourth has no Wingman to read it. A control there
    /// would be an affordance for something that is not there.
    ///
    /// THE PLAY OFFER IS THE VOICE FOLD'S OWN ANSWER, copied and not recomputed. Whether there is anything to play
    /// is <see cref="VoiceDisplayFold"/>'s verdict, already on the row, and asking a second time here is how one
    /// session comes to be "preparing audio" on one screen and silent on another.
    ///
    /// A ROW WITH NO VOICE VERDICT AT ALL offers nothing rather than guessing. The verdict is stamped by the
    /// Gateway; a row that reaches this fold without one came from a caller that could not see the voice service,
    /// and "no audio" is a claim that caller has not earned.
    /// </summary>
    private static WingmanNowVoiceDto Voice(string state, SessionDto? row, TurnVerdictDto? live)
    {
        if (row is null) return new WingmanNowVoiceDto();
        if (state is WingmanNowStates.Reading or WingmanNowStates.Working
                  or WingmanNowStates.JustAnswered or WingmanNowStates.SwitchedOff)
            return new WingmanNowVoiceDto();

        if (!row.VoiceMode)
        {
            return new WingmanNowVoiceDto
            {
                Kind = WingmanNowVoiceKinds.TurnOn,
                Label = VoiceTurnOnLabel,
                AfterTurnOnText = live is null ? VoiceAfterTurnOnNextStop : VoiceAfterTurnOnThisStop,
            };
        }

        if (row.VoiceDisplay is not { } display) return new WingmanNowVoiceDto();

        return display.CanPlay
            ? new WingmanNowVoiceDto { Kind = WingmanNowVoiceKinds.Play, Label = VoicePlayLabel }
            : new WingmanNowVoiceDto { Kind = WingmanNowVoiceKinds.Preparing, Label = VoicePreparingLabel };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
