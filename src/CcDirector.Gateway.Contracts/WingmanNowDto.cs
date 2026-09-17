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

    /// <summary>The ways of answering, in the verdict's own order. Empty when the stop takes typed words only.</summary>
    public List<WingmanNowOptionDto> Options { get; set; } = new();
}

/// <summary>One way of answering the stop.</summary>
public sealed class WingmanNowOptionDto
{
    /// <summary>The option's position in the verdict's own list, which is what
    /// <c>POST /sessions/{sid}/turn-verdict/answer</c> takes. Zero-based, as that route reads it.</summary>
    public int Index { get; set; }

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
}

/// <summary>
/// The ten states Now can be in - <see cref="WingmanNowResponse.State"/>'s closed word list. String constants on the
/// wire, the same convention as <see cref="VerdictStates"/>.
/// </summary>
public static class WingmanNowStates
{
    /// <summary>The session is stopped and the Wingman says a person is needed.</summary>
    public const string NeedsYou = "needs-you";

    /// <summary>The session has stopped and the Wingman is forming its answer.</summary>
    public const string Reading = "reading";

    /// <summary>The session is working.</summary>
    public const string Working = "working";

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

    /// <summary>Any row none of the above describes - snoozed, supervised, exited, brand new, or stopped with no
    /// verdict. It wears the row's own label, so Now is never blank.</summary>
    public const string Other = "other";
}
