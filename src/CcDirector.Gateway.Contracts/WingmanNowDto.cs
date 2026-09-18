namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The answer to <c>GET /sessions/{sid}/wingman-now</c>: the LIVE stop of one session, prepared for the Wingman tab's
/// Now view (the Wingman tab, version 3, item 1).
///
/// EVERYTHING HERE IS A FINISHED STRING OR FLAG, folded once on the Gateway by <c>WingmanNowFold</c>. The Cockpit
/// formats the UTC instants into local time and lays the rest out verbatim. It never decides what a stop means, which
/// words a state wears, whether the Wingman was sure, or whether an option may still be tapped - the product's rule
/// that the client is dumb and the Gateway rules.
///
/// WHY A ROUTE AND NOT MORE FIELDS ON <see cref="SessionDto"/>. The roster row travels for every session on every poll
/// and push; Now needs one session's whole story - the agent's full reply, every option with its note, the
/// conversation - and only while the owner has that session's Wingman tab open. It also makes reads the roster fold
/// does not make (the verdict HISTORY, the stored conversation), so putting it on the row would make every roster poll
/// pay for them.
///
/// IT IS NEVER BLANK. A row none of the drawn states describes folds to <see cref="WingmanNowStates.Other"/> and wears
/// the row's own <see cref="SessionDto.StateLabel"/>, so the tab always says something true about the session.
///
/// TIMES ARE A LEAD PLUS AN INSTANT, never a formatted clock time: the Gateway does not know the owner's time zone.
/// See <see cref="WingmanNowWhenDto"/>.
/// </summary>
public sealed class WingmanNowResponse
{
    /// <summary>The session this is about.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>
    /// WHICH SESSION THIS IS, in finished words, for the first line of the view: "112 - Cube Data and Projects -
    /// Architect". Never empty, and present in EVERY state.
    ///
    /// It is here because the pane carried no name at all, and the owner moves between a dozen sessions on this
    /// one screen. The damage that costs is not a misread line - it is the right answer sent to the wrong
    /// session, which is the worst thing this view can cause.
    ///
    /// The number leads when the row carries one, because that is the short name the Sessions list and the
    /// command line both use. A row that has gone away entirely leaves only the session's identifier, which is
    /// machine output and is said so here rather than dressed up: it is the last truthful thing left.
    /// </summary>
    public string SessionLine { get; set; } = "";

    /// <summary>Which state Now is in - one of <see cref="WingmanNowStates"/>. Never empty.</summary>
    public string State { get; set; } = "";

    /// <summary>The status pill's words, in plain English. Never empty.</summary>
    public string PillText { get; set; } = "";

    /// <summary>The colour word the ROW wears, copied from <see cref="SessionDto.EffectiveColor"/> - never worked out
    /// again here, so the pill and the row cannot disagree. Null only when the row carries none.</summary>
    public string? PillColour { get; set; }

    /// <summary>The canonical pixel for <see cref="PillColour"/>, copied from
    /// <see cref="SessionDto.EffectiveColorHex"/>. Null only when the row carries none.</summary>
    public string? PillColourHex { get; set; }

    /// <summary>True when the "Why this colour?" link is offered beside the pill.</summary>
    public bool ShowWhyColour { get; set; }

    /// <summary>When it stopped, or how long it has been working. Null when there is no moment to show.</summary>
    public WingmanNowWhenDto? When { get; set; }

    /// <summary>True when the Wingman's own answer says it is not sure about this stop.</summary>
    public bool Unsure { get; set; }

    /// <summary>The quiet tag beside the pill while <see cref="Unsure"/>. Null otherwise.</summary>
    public string? UnsureTag { get; set; }

    /// <summary>The sentence inside the card while <see cref="Unsure"/>. Null otherwise.</summary>
    public string? UnsureLine { get; set; }

    /// <summary>The one line across the top of the view - what this stop is about. Null when there is no stop to
    /// describe.</summary>
    public string? Headline { get; set; }

    /// <summary>One or two sentences for a reader who has not looked at this session for hours. Null when there is
    /// none.</summary>
    public string? Story { get; set; }

    /// <summary>The agent's own decisive sentence, with who said it. Null when the stop carries none.</summary>
    public WingmanNowSaidDto? AgentSaid { get; set; }

    /// <summary>The agent's whole last reply, word for word, folded away under the sentence above. Null when the
    /// session's computer does not send conversations, or nothing has been stored for it yet.</summary>
    public string? WholeReply { get; set; }

    /// <summary>What the session needs from the owner. Null on a stop that needs nothing.</summary>
    public WingmanNowNeedsDto? Needs { get; set; }

    /// <summary>True when the options in <see cref="WingmanNowNeedsDto.Options"/> can still be tapped: this verdict is
    /// the one in force, it was accepted, nobody has answered it yet, and it offers a real choice. False leaves the
    /// options readable and the reply box the way to answer.</summary>
    public bool CanAnswerByOption { get; set; }

    /// <summary>The verdict the options came from, which <c>POST /sessions/{sid}/turn-verdict/answer</c> takes
    /// alongside an option's index. Null when there is no verdict in force.</summary>
    public string? VerdictId { get; set; }

    /// <summary>The words inside the empty reply box. Null when this state offers no reply box.</summary>
    public string? ReplyPlaceholder { get; set; }

    /// <summary>
    /// The sentence beside the reply box's Send, saying what sending does. Null exactly when
    /// <see cref="ReplyPlaceholder"/> is - a box that is not offered needs nothing said about it.
    ///
    /// IT DIFFERS BY STATE, which is the point of folding it here. The second half - that one reply can answer
    /// more than one question - is true only where something was asked, and it was showing on done, on a report,
    /// on a working session and on a snoozed one, where nothing had been.
    /// </summary>
    public string? ReplyHint { get; set; }

    /// <summary>
    /// When a snoozed session comes back, as a finished lead plus the instant: "Snoozed until" + 09:00.
    ///
    /// Null when the session is not snoozed, and null on a snooze with no deadline to name - a hold that has not
    /// landed yet, or one that waits for him to wake it. That second case says so in <see cref="Headline"/>
    /// instead, because a lead with no instant after it is a sentence that stops in the middle.
    /// </summary>
    public WingmanNowWhenDto? SnoozedUntil { get; set; }

    /// <summary>The card on a stop that needs nothing from the owner. Null otherwise.</summary>
    public WingmanNowCardDto? CalmCard { get; set; }

    /// <summary>
    /// The session's OWN last words, with who said them - shown where there is no Wingman account of the stop to
    /// show: while it is being read, when the reading failed, and when the Wingman is switched off. Null when the
    /// session's agent tool sends no conversation, or nothing has been stored for it yet.
    ///
    /// Shortened, unlike <see cref="WholeReply"/>: this stands in place of a headline, so it is the opening of the
    /// reply rather than all of it.
    /// </summary>
    public WingmanNowSaidDto? LastWords { get; set; }

    /// <summary>The headline when the Wingman's answer about this stop was refused. Null in every other state.
    /// </summary>
    public string? FailedHeadline { get; set; }

    /// <summary>Why it was refused, in the Wingman's own words, followed by what that leaves the row. Null in every
    /// other state, and null on a refusal that recorded no reason.</summary>
    public string? FailedStory { get; set; }

    /// <summary>The last stop the Wingman DID explain, offered when the live one it could not. Null when this account
    /// has no earlier accepted verdict for this session.</summary>
    public WingmanNowPastDto? LastGood { get; set; }

    /// <summary>The words for an account whose Wingman is switched off. Null in every other state.</summary>
    public WingmanNowSwitchedOffDto? SwitchedOff { get; set; }

    /// <summary>
    /// WHEN A SESSION THAT SAID IT WOULD CARRY ON TURNS RED IF IT DOES NOT - the sentence split around the one
    /// instant the client must format.
    ///
    /// Null in every state but carrying on, AND null while the session has one of its own sessions still running:
    /// no clock is counting then, and the sentence that says so needs no instant, so it is in
    /// <see cref="WingmanNowCardDto.Body"/> instead. A client renders THIS when it is present and the card's body
    /// when it is not; it never has both to choose between.
    /// </summary>
    public WingmanNowDeadlineDto? CarryingOnDeadline { get; set; }

    /// <summary>What the session was last asked, and by whom when that is KNOWN. Null when nothing has been asked
    /// of it, or its computer sends no conversation.</summary>
    public WingmanNowAskedDto? LastAsked { get; set; }

    /// <summary>The stop before this moment, as one line. On a working session it is where the row has just been;
    /// null when the Wingman has never explained a stop for this session.</summary>
    public WingmanNowPastDto? LastStop { get; set; }

    /// <summary>What he answered, on the state that follows his answer - null in every other state.</summary>
    public WingmanNowAnsweredDto? Answered { get; set; }

    /// <summary>
    /// The next session waiting on him, or null when nothing else is. Shown the moment he has finished with this
    /// one, which is the one moment "and now go there" is worth anything to him.
    /// </summary>
    public WingmanNowNextDto? NextNeedsYou { get; set; }

    /// <summary>The voice control at the top of the view. Never null - one of its kinds is "nothing to offer".</summary>
    public WingmanNowVoiceDto Voice { get; set; } = new();
}

/// <summary>
/// THE VOICE CONTROL, as one finished offer rather than the facts to work one out from.
///
/// The Voice screen was the last place a client ruled for itself, and the cost is on the record: with no reason
/// handed down it guessed, and put a hopeful "Generate narration now" button beside a red "Voice unavailable"
/// badge - a button that could never succeed. This view never repeats that. It is told which of four things to
/// draw and what the words are.
/// </summary>
public sealed class WingmanNowVoiceDto
{
    /// <summary>
    /// One of <see cref="WingmanNowVoiceKinds"/>: <c>play</c> (there is audio to play), <c>preparing</c> (it is
    /// being made), <c>turn-on</c> (voice is off for this session and may be turned on), or <c>none</c> (nothing
    /// to offer at all, and the client draws no control).
    /// </summary>
    public string Kind { get; set; } = WingmanNowVoiceKinds.None;

    /// <summary>The words on the control, or empty when there is no control.</summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// What to say once he turns voice on, already finished - null for every other kind.
    ///
    /// It differs on whether there is a stop to read: a session that has just stopped hears THIS stop in a few
    /// seconds, and one that has not will hear the next. Saying the first to a session with nothing to read would
    /// promise audio that never arrives, which is the same broken promise in smaller print.
    /// </summary>
    public string? AfterTurnOnText { get; set; }
}

/// <summary>The four kinds of voice control, named in one place so a rule and the fold cannot spell one two ways.</summary>
public static class WingmanNowVoiceKinds
{
    public const string Play = "play";
    public const string Preparing = "preparing";
    public const string TurnOn = "turn-on";
    public const string None = "none";
}

/// <summary>
/// WHAT HE ANSWERED, and that the session took it - the card that closes the loop.
///
/// It is drawn from the two answering routes as ONE thing, because from his side they are one thing: tapping an
/// option and typing a reply are the same act of answering, and a card that could only see the first would go blank
/// exactly when he answered in the terminal.
/// </summary>
public sealed class WingmanNowAnsweredDto
{
    /// <summary>The whole first line, finished: "You answered: allow the merge".</summary>
    public string Headline { get; set; } = "";

    /// <summary>What he answered on its own - the options he chose in his order, or the reply he typed.</summary>
    public string Text { get; set; } = "";

    /// <summary>The words before the moment he answered: "Sent at". The client adds the local time.</summary>
    public string SentLead { get; set; } = "";

    /// <summary>The moment (UTC) his answer was recorded.</summary>
    public DateTime AtUtc { get; set; }

    /// <summary>
    /// That the session took the answer and went back to work, in finished words: "The session started working again
    /// 2 seconds later." NULL when this Gateway cannot tell - and then nothing is claimed about it, because a
    /// guessed confirmation is worse than none on the one card whose whole job is confirming.
    /// </summary>
    public string? WorkingAgainAfterText { get; set; }
}

/// <summary>
/// THE NEXT SESSION WAITING ON HIM, in the Sessions list's own order, so the tab and the list cannot point him at
/// two different sessions. Everything but the identifier is finished words.
/// </summary>
public sealed class WingmanNowNextDto
{
    /// <summary>The words over the row: "Next that needs you".</summary>
    public string Heading { get; set; } = "";

    /// <summary>The session to go to.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>Its name, as the Sessions list shows it.</summary>
    public string Name { get; set; } = "";

    /// <summary>What it is waiting for, in its own row's words.</summary>
    public string? Label { get; set; }

    /// <summary>The words on the way there: "Go there".</summary>
    public string LinkText { get; set; } = "";
}

/// <summary>
/// What the session was last asked: the words, when, and who asked.
///
/// WHO IS NEVER GUESSED. It is "You" when the owner's own turn is stamped within half a minute of the message and
/// at or after the previous stop. Anything else leaves it null and the card says nothing about who - a name in
/// front of the owner that nobody verified is worse than no name.
/// </summary>
public sealed class WingmanNowAskedDto
{
    /// <summary>The card's heading.</summary>
    public string Heading { get; set; } = "";

    /// <summary>What was asked, word for word.</summary>
    public string Text { get; set; } = "";

    /// <summary>When it was asked (UTC), for the client to format into local time.</summary>
    public DateTime AtUtc { get; set; }

    /// <summary>Who asked, when it is known: "You". Null when it is not.</summary>
    public string? By { get; set; }

    /// <summary>The finished words before the time - "You, at", or just "at" when nobody is named. The client
    /// joins this and the local time, and chooses neither the words nor the punctuation.</summary>
    public string WhenLead { get; set; } = "";
}

/// <summary>
/// A sentence with an instant in the MIDDLE of it: "If it has not worked again by" + 12:05 PM + ", and none of the
/// sessions it owns is still working, this turns red and says so."
///
/// <see cref="WingmanNowWhenDto"/> cannot carry this - its lead comes before a time that ends the sentence, and
/// this one continues afterwards. Both halves are finished words; the client formats the instant into local time
/// and joins the three.
/// </summary>
public sealed class WingmanNowDeadlineDto
{
    /// <summary>The words before the instant.</summary>
    public string Before { get; set; } = "";

    /// <summary>The moment (UTC) the clock runs out - THE SAME instant the carrying-on clock expires on, so the
    /// sentence and the expiry cannot disagree.</summary>
    public DateTime AtUtc { get; set; }

    /// <summary>The words after the instant, punctuation included.</summary>
    public string After { get; set; } = "";
}

/// <summary>
/// A stop that is PAST, as one line: a finished lead, the moment, and what it said.
///
/// The instant is separate for the reason <see cref="WingmanNowWhenDto"/> gives - the Gateway does not know the
/// owner's time zone - and the text is already finished, so the client joins three values and decides nothing.
/// </summary>
public sealed class WingmanNowPastDto
{
    /// <summary>The words before the time: "Last good explanation".</summary>
    public string Lead { get; set; } = "";

    /// <summary>The moment (UTC) the client formats into local time.</summary>
    public DateTime AtUtc { get; set; }

    /// <summary>What that stop said, as the pill's words and the headline the Wingman gave it.</summary>
    public string Text { get; set; } = "";
}

/// <summary>What Now says on an account whose Wingman is switched off: one sentence, and the way to switch it on.
/// </summary>
public sealed class WingmanNowSwitchedOffDto
{
    /// <summary>The headline.</summary>
    public string Headline { get; set; } = "";

    /// <summary>The one sentence under it.</summary>
    public string Story { get; set; } = "";

    /// <summary>The words on the link that leads to the switch. The client owns where the link GOES - that is a
    /// route, not a word.</summary>
    public string SettingsLinkText { get; set; } = "";
}

/// <summary>
/// A timed sentence, as a finished LEAD plus a UTC instant: "Stopped at" + 2026-09-17T11:12:04Z renders "Stopped at
/// 11:12 AM, 8 minutes ago".
///
/// THE GATEWAY DOES NOT KNOW THE OWNER'S TIME ZONE, so it cannot write the clock time itself; and the Cockpit must not
/// decide any WORDS. Splitting the sentence at exactly that seam is what lets both rules hold at once - the same
/// precedent <c>GET /sessions/{sid}/wingman-stops</c> set.
/// </summary>
public sealed class WingmanNowWhenDto
{
    /// <summary>The words before the time: "Stopped at", "Working for", "You answered".</summary>
    public string Lead { get; set; } = "";

    /// <summary>The moment (UTC) the client formats into local time.</summary>
    public DateTime AtUtc { get; set; }

    /// <summary>True when the client also shows how long ago it was ("8 minutes ago"). False on a stop where nothing
    /// is pending and the elapsed time would only add noise.</summary>
    public bool ShowAgo { get; set; }

    /// <summary>
    /// True when the sentence is the ELAPSED TIME ALONE and the clock time is not shown at all: "Working for 6
    /// minutes", not "Working for 11:14 AM". <see cref="AtUtc"/> is then the moment to measure from.
    ///
    /// The client is still deciding no words - it is told which of the two shapes this sentence is, rather than
    /// working it out from the state, which is the branch the dumb-client rule exists to remove.
    /// </summary>
    public bool ElapsedOnly { get; set; }
}

/// <summary>Something that was said, and who said it.</summary>
public sealed class WingmanNowSaidDto
{
    /// <summary>Who said it: "Claude Code said", "Its last words".</summary>
    public string Who { get; set; } = "";

    /// <summary>What was said.</summary>
    public string Text { get; set; } = "";
}

/// <summary>What the session needs from the owner, as the card draws it.</summary>
public sealed class WingmanNowNeedsDto
{
    /// <summary>The card's heading.</summary>
    public string Heading { get; set; } = "";

    /// <summary>The agent's own recommendation, already led in with "It recommends: ". Null when it made none.</summary>
    public string? Recommends { get; set; }

    /// <summary>The choice being asked, in plain words, when the answer is a selection in a picker. Null
    /// otherwise.</summary>
    public string? Question { get; set; }

    /// <summary>The line under the heading saying what a tap does - "Click an option to send it as your answer".
    /// Null when this stop offers no options, where it would be an affordance for something that is not there.
    /// </summary>
    public string? OptionsLead { get; set; }

    /// <summary>
    /// The short warning beside the options when answering this stop costs something that cannot be taken back:
    /// "Cannot be undone", "Says yes from now on", "Spends real money". Null when the judge's risk word is
    /// "none", and null when it recorded none at all - an unrecorded risk is not a safe one, and it is not
    /// claimed to be either way.
    ///
    /// IT IS ABOUT THE STOP, NOT ABOUT ONE OPTION, and that is the honest reading of what reaches here: the
    /// judge answers ONE risk word for the whole answer (see the turn-verdict contract), and nothing in the
    /// record says which of the options carries it. Putting it on the recommended option would be this fold
    /// guessing, and putting it on all of them would say "cannot be undone" about the option that does nothing -
    /// which is how a warning stops being read.
    /// </summary>
    public string? RiskFlag { get; set; }

    /// <summary>The sentence under the flag, saying what the risk is in plain words. Null exactly when
    /// <see cref="RiskFlag"/> is.</summary>
    public string? RiskLine { get; set; }

    /// <summary>True when the screen must ask once before it sends an answer to this stop. Set exactly when
    /// <see cref="RiskFlag"/> is present: the one place a confirmation earns its interruption is the answer
    /// that cannot be taken back.</summary>
    public bool ConfirmBeforeSending { get; set; }

    /// <summary>The ways of answering, in the verdict's own order. Empty when the stop takes typed words only.</summary>
    public List<WingmanNowOptionDto> Options { get; set; } = new();
}

/// <summary>One way of answering the stop.</summary>
public sealed class WingmanNowOptionDto
{
    /// <summary>The option's position in the verdict's own list, which is what
    /// <c>POST /sessions/{sid}/turn-verdict/answer</c> takes. Zero-based, as that route reads it.</summary>
    public int Index { get; set; }

    /// <summary>
    /// THE NUMBER THE OWNER READS - <see cref="Index"/> plus one, so the first option is 1.
    ///
    /// Two numbers for one option looks like duplication and is the opposite: the zero-based one is what the
    /// answer route takes and is an internal detail, and it was being rendered. People count from one, so the
    /// screen was showing him a "0" that meant nothing to him and a "1" that meant the second option. The
    /// display number is folded here, beside every other owner-facing value, rather than by arithmetic in a
    /// client - which is the one place it could quietly go back to agreeing with the route.
    /// </summary>
    public int Number { get; set; }

    /// <summary>The short label, naming the action being decided.</summary>
    public string Key { get; set; } = "";

    /// <summary>What choosing it does, and what it costs.</summary>
    public string Note { get; set; } = "";

    /// <summary>True on at most one option.</summary>
    public bool Recommended { get; set; }
}

/// <summary>A heading and a body - the card on a stop that needs nothing from the owner.</summary>
public sealed class WingmanNowCardDto
{
    public string Heading { get; set; } = "";

    /// <summary>The body sentence. Null when this card has a heading only.</summary>
    public string? Body { get; set; }

    /// <summary>
    /// THE CARD'S COLOUR, NAMED HERE - one of <see cref="WingmanNowCardTones"/>, or null for the neutral card.
    ///
    /// The approved mockup tints the finished work and the report cyan, and carrying on purple, which is the
    /// owner's done-versus-report distinction made visible. A client that read the tone off the state name would
    /// be deciding what a state MEANS, which is the one thing the Now view may never do - so the same fold that
    /// chose the words chooses the colour, and the client renders it.
    /// </summary>
    public string? Tone { get; set; }
}

/// <summary>The calm card's two colours, named in one place so the fold and a rule cannot spell one two ways.</summary>
public static class WingmanNowCardTones
{
    /// <summary>The work is complete, or the session is only telling him something.</summary>
    public const string Cyan = "cyan";

    /// <summary>The session said it would carry on by itself.</summary>
    public const string Purple = "purple";
}

/// <summary>
/// The eleven states Now can be in - <see cref="WingmanNowResponse.State"/>'s closed word list. String constants on
/// the wire, the same convention as <see cref="VerdictStates"/>.
/// </summary>
public static class WingmanNowStates
{
    /// <summary>The session is stopped and the Wingman says a person is needed.</summary>
    public const string NeedsYou = "needs-you";

    /// <summary>The session has stopped and the Wingman is forming its answer.</summary>
    public const string Reading = "reading";

    /// <summary>The session is working.</summary>
    public const string Working = "working";

    /// <summary>
    /// The owner parked this session, and it comes back when the snooze runs out.
    ///
    /// It sits directly under working in the fold's order, exactly where the row's own colour ladder puts it
    /// (<see cref="SessionOrdering.EffectiveColor"/>), so the pill and the dot cannot come to different answers
    /// about a snoozed session that still carries a verdict.
    /// </summary>
    public const string Snoozed = "snoozed";

    /// <summary>The session is working again, moments after the owner answered its last stop.</summary>
    public const string JustAnswered = "just-answered";

    /// <summary>The session stopped, needs nothing, and said it would carry on by itself.</summary>
    public const string CarryingOn = "carrying-on";

    /// <summary>The work is complete.</summary>
    public const string Done = "done";

    /// <summary>The session is only telling the owner something; the work is not finished.</summary>
    public const string Report = "report";

    /// <summary>The Wingman's answer about this stop was refused, or never came.</summary>
    public const string Failed = "failed";

    /// <summary>The Wingman is switched off for this account, so nothing reads this session's stops.</summary>
    public const string SwitchedOff = "switched-off";

    /// <summary>Any row none of the above describes - supervised, exited, brand new, or stopped with no
    /// verdict. It wears the row's own label, so Now is never blank.</summary>
    public const string Other = "other";
}
