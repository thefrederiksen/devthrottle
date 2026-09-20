namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One judged stop: what the Wingman says this turn end MEANS, with the agent's own words as the
/// receipt. Stored on the Gateway per tenant and per session, and served to the clients, which render
/// it and never re-derive it.
///
/// A record is written for EVERY judged stop, including one whose answer was refused. A refused answer
/// sets <see cref="Failed"/> with a <see cref="FailureReason"/> and carries no verdict word, and the
/// session row stays exactly as the detector left it. That is the whole shape of the safety property:
/// silence is never a decision, and a broken answer never moves a row toward a calm colour.
/// </summary>
public sealed class TurnVerdictDto
{
    /// <summary>This verdict's own identity, so a client can say which one it is showing and an
    /// option activation can be bound to the answer it came from.</summary>
    public string VerdictId { get; set; } = "";

    /// <summary>When the judge answered (UTC).</summary>
    public DateTime JudgedAtUtc { get; set; }

    /// <summary>When the detector OBSERVED the turn end (UTC). This is the join key between a verdict
    /// row and a turn-log record: (tenant, session id, this time). It is the moment being judged, not
    /// the moment of judging, so it stays the same however long the judge took.</summary>
    public DateTime TurnEndObservedAtUtc { get; set; }

    /// <summary>The full-grid fingerprint of the screen this verdict was formed on. A repaint changes
    /// it, which is what makes a stale option tap refusable.</summary>
    public string ScreenHash { get; set; } = "";

    /// <summary>Which judge answered.</summary>
    public string Model { get; set; } = "";

    /// <summary>The contract version that produced and validated this answer.</summary>
    public string ContractVersion { get; set; } = "";

    /// <summary>"agent-reply" or "terminal-failure".</summary>
    public string PackageKind { get; set; } = "";

    /// <summary>True when the answer was refused. The reason is in <see cref="FailureReason"/> and
    /// every field below is at its default - except <see cref="Spoken"/>, which a refusal of readable JSON
    /// keeps (contract v2.2).</summary>
    public bool Failed { get; set; }

    /// <summary>Why the answer was refused, in plain words, when <see cref="Failed"/> is true.</summary>
    public string? FailureReason { get; set; }

    /// <summary>One of the six verdict words. Empty when <see cref="Failed"/>.
    ///
    /// THE STORED SPELLING, and from contract v3 no longer what the judge is asked for - see
    /// <see cref="State"/>, which is. It stays because every record written since the Wingman shipped carries
    /// one, because the labelling corpus in the internal repository is graded on these six words, and because a
    /// hundred call sites read it. <c>TurnVerdictVocabulary.SplitState</c> is the one place the two spellings
    /// meet.</summary>
    public string Verdict { get; set; } = "";

    /// <summary>
    /// WHAT HAPPENED, IN ONE WORD - the field the five-field contract names, folded from <see cref="Verdict"/>
    /// and <see cref="FinishedKind"/> rather than stored beside them, so the two can never disagree.
    ///
    /// Serialized to every client, and empty on a refused record. It is what drives the colour of a row and
    /// where it sorts; a client renders it and never re-derives it.
    /// </summary>
    public string State => TurnVerdictStates.Of(Verdict, FinishedKind);

    /// <summary>CUT IN CONTRACT v3 (owner ruling, 2026-09-18): the judge is no longer asked how sure it is,
    /// because "cannot-tell" already means "I could not judge this" and every reader treated the two alike.
    /// Empty on every record written since. Records written before v3 keep the word they were stored with, and
    /// nothing gates on it any more - see <c>SessionOrdering.IsCalmVerdict</c>.</summary>
    public string Confidence { get; set; } = "";

    /// <summary>CUT IN CONTRACT v3 (owner ruling, 2026-09-18): the agent's decisive sentence, quoted as an
    /// anti-invention receipt. It was rendered on no screen, and its only consumer was the rule that threw whole
    /// answers away when the quote did not match character for character - which refused about one reading in
    /// seven, because the reply carries Markdown the judge reads through. Empty on every record written since;
    /// an older record keeps its quote and the Fleet Manager still shows one when it is there.</summary>
    public string Evidence { get; set; } = "";

    /// <summary>The one line a row shows: the ask, or the report.</summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// CUT AS A SEPARATE ASK IN CONTRACT v3 (owner ruling, 2026-09-18), and from that contract it holds the
    /// SAME TEXT as <see cref="Narration"/>.
    ///
    /// The judge used to be asked for a short written version and the narration call for a long spoken one, and
    /// a screen that shows both makes the reader read both to find out they agree - which is the defect the
    /// whole redesign started from. There is now one body, written once, and this field carries it so that the
    /// hundred readers of a "summary" keep reading the words the owner is actually shown. It is set when the
    /// reading completes, never before: a record is not published until both calls are done.
    /// </summary>
    public string Summary { get; set; } = "";

    /// <summary>The agent's own recommendation, quoted or closely paraphrased, when it made one.</summary>
    public string? AgentRecommends { get; set; }

    /// <summary>"reply" (typed words) or "keys" (a selection in a picker). DERIVED from contract v3 - a menu on
    /// the record means keys, no menu means a reply - rather than asked of the judge, which could contradict
    /// itself and have the answer refused for it.</summary>
    public string AnswerVia { get; set; } = "";

    /// <summary>The picker on the screen, when the answer is a selection. Null otherwise.</summary>
    public TurnVerdictMenuDto? Menu { get; set; }

    /// <summary>The ways of answering. Empty, or two or more - never exactly one, because one option
    /// is not a choice.</summary>
    public List<TurnVerdictOptionDto> Options { get; set; } = new();

    /// <summary>CUT IN CONTRACT v3 (owner ruling, 2026-09-18): "none", "irreversible", "standing-grant" or
    /// "spends-money", asked once for the whole stop. Risk now lives on <see cref="TurnVerdictOptionDto.Note"/>
    /// and only there - at the point of decision, attached to the choice it belongs to, where it can change what
    /// the owner presses. A session-level risk word with no choice attached could not.
    ///
    /// WHAT THIS COSTS, STATED: the Now screen's confirm-before-sending was raised by this word, and with no
    /// word there is no confirm. The consequence still reaches the owner, in the note beside the button.
    /// Empty on every record written since v3.</summary>
    public string Risk { get; set; } = "";

    /// <summary>
    /// CUT AS A SEPARATE ASK IN CONTRACT v3 (owner ruling, 2026-09-18), and from that contract it holds the
    /// SAME TEXT as <see cref="Narration"/> and <see cref="Summary"/>.
    ///
    /// It existed to fill the gap before the narration call landed - the row and the audio took the judge's
    /// short version first and swapped it for the narration seconds later. That gap is what made audio restart
    /// mid-sentence, a row re-word itself while the owner looked at it, and an older turn get spoken. A reading
    /// is now published only once both calls are done, so there is nothing to fill and nothing to swap: one
    /// text, read or heard. It does NOT open with the session name, which is prefixed at synthesis.
    ///
    /// Empty on a refused record. A refusal is a reading that could not be read, and it says so.</summary>
    public string Spoken { get; set; } = "";

    /// <summary>The agent's announced next wake-up (UTC) when the stop's package carried one, else null. The
    /// carrying-on clock reads it: a "continues-alone" verdict expires two minutes after this moment when it
    /// is present, and ten minutes after <see cref="JudgedAtUtc"/> when it is not. Carried on the stored
    /// record, rather than held in memory, so a Gateway restart does not forget a clock that was running.</summary>
    public DateTime? NextScheduledWakeUtc { get; set; }

    /// <summary>
    /// When this verdict stopped describing the screen it was formed on (UTC), or null while it still does (the
    /// Wingman-on-every-turn mission, slice G). The session going back to work sets it.
    ///
    /// SERVED ONLY BY THE HISTORY READ, and stamped from the stored row's own column rather than from the saved
    /// answer - it is a fact about the record, not part of what the judge said. The latest-verdict read and the
    /// roster fold never return a superseded record at all, so a client that sees this field non-null is looking
    /// at history. It is here so that history is honest: a superseded record and a live one are different facts,
    /// and a list that showed them alike would have the reader believe a verdict is in force when it is not.
    /// </summary>
    public DateTime? SupersededAtUtc { get; set; }

    /// <summary>
    /// "done" or "report" when <see cref="Verdict"/> is "finished", null for every other verdict (owner ruling,
    /// 2026-09-15): the agent says the work is complete, or it is only informing the owner and asks nothing. Both
    /// are cyan (never the brand-new green, issue #2892), in the same band and uncounted; the row's label leads with "Done" or "Telling you" (see SessionOrdering.CalmReportLabel). A verdict stored
    /// before the field existed carries null.
    /// </summary>
    public string? FinishedKind { get; set; }

    /// <summary>
    /// THE NARRATION, AND THE ONLY BODY THERE IS: what happened, written for someone who has not looked at this
    /// session for hours. One of the five fields of contract v3, and the same text as <see cref="Summary"/> and
    /// <see cref="Spoken"/> - the owner reads it or hears it, and it is never two writings of one turn.
    ///
    /// WRITTEN BEFORE THE RECORD IS PUBLISHED (the atomic-reading rule, 2026-09-18). It used to be saved onto an
    /// already-published record a few seconds later, which is what let a half-finished reading reach a screen.
    /// Null when the narration call failed, when the account has no Wingman on its plan and the read of that plan
    /// itself failed, and for a session another session owns (a Worker under a live owner), whose stops are read
    /// by that owner rather than by the user.
    /// </summary>
    public string? Narration { get; set; }

    /// <summary>
    /// WHICH KIND OF FAILURE THIS IS, as one of <see cref="WingmanFailureKinds"/>, or null on a reading that did not
    /// fail (mission "Wingman error and retry", 2026-09-19). It exists so the card's short plain reason is chosen
    /// from a closed word rather than by matching text inside <see cref="FailureReason"/>, which quotes exception
    /// messages and was never meant to be read by a person on a session card.
    /// </summary>
    public string? FailureKind { get; set; }

    /// <summary>
    /// Why this reading has NO WORDS although the judge's answer was accepted, or null (mission "Wingman error and
    /// retry", 2026-09-19). A reading is both calls, and this is the second one failing: the row keeps the judge's
    /// colour and label, <see cref="Failed"/> stays false, and there is nothing to read or hear. That is a failed
    /// reading to the person looking at it, so it shows the same tag and goes on the same retry schedule. Null when
    /// no narration was owed at all - a session another live session owns.
    /// </summary>
    public string? NarrationFailureReason { get; set; }

    /// <summary>
    /// How many SCHEDULED retries this stop has already spent, on a <see cref="Failed"/> record (mission "Wingman
    /// error and retry", 2026-09-19). Zero after the first failure. A person pressing "Ask again" does not move
    /// it. Carried on the stored record, so a Gateway restart does not forget where a stop is on its schedule.
    /// </summary>
    public int RetriesMade { get; set; }

    /// <summary>
    /// When the Gateway will ask about this failed stop again (UTC), or NULL WHEN NOTHING IS BOOKED - the schedule
    /// is used up, or the record is not a failure. This one field is what every "a retry is coming" sentence is
    /// rendered from, so a card can never promise an attempt that the record does not hold.
    /// </summary>
    public DateTime? NextRetryAtUtc { get; set; }

    /// <summary>
    /// Why this reading's buttons were dropped, in plain words, or null when they were not (mission "Wingman error
    /// and retry", 2026-09-19). The model's option list broke a rule - exactly one option, two marked recommended,
    /// an empty send, an over-long key - so the WHOLE list was dropped and the rest of the reading stands. It is
    /// not a failure: <see cref="Failed"/> stays false, the reading is narrated, and no button is shown that the
    /// model did not clearly choose. Recorded here so it is answerable by query and visible in the debug view.
    /// </summary>
    public string? OptionsDroppedReason { get; set; }
}

/// <summary>The picker on the screen that a "keys" answer selects from.</summary>
public sealed class TurnVerdictMenuDto
{
    /// <summary>The choice being asked, in plain words.</summary>
    public string Question { get; set; } = "";

    /// <summary>"single" or "multiple".</summary>
    public string SelectionMode { get; set; } = "single";

    /// <summary>What completes the selection: empty when selecting is enough, or a carriage return
    /// when the picker needs one. A "multiple" menu always needs one - a checklist with no way to
    /// submit it is unanswerable.</summary>
    public string Submit { get; set; } = "";
}

/// <summary>One way of answering the stop.</summary>
public sealed class TurnVerdictOptionDto
{
    /// <summary>The short label, naming the ACTION being decided rather than the mechanism.</summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// ONLY the bytes that CHOOSE this option, and never a carriage return or a line feed.
    ///
    /// For a reply option the activation route sends these bytes and appends exactly one Enter. For a
    /// keys option it sends the selected options' bytes in the order given and THEN the menu's submit,
    /// under one screen lock - so the confirm lives in <see cref="TurnVerdictMenuDto.Submit"/> and never
    /// inside a send.
    ///
    /// This record said the opposite until the contract was amended: that a keys option carried its own
    /// carriage return and the route appended none. Three parts of the product each described a
    /// different rule, and the result was a multiple-select nobody could answer - the route took one
    /// option, re-checked the screen, and refused the second toggle by its own lock. A reader building
    /// against the old sentence would build exactly the action the validator now rejects.
    /// </summary>
    public string Send { get; set; } = "";

    /// <summary>True on at most one option.</summary>
    public bool Recommended { get; set; }

    /// <summary>The consequence and the risk of choosing this one.</summary>
    public string Note { get; set; } = "";
}

/// <summary>The closed words of <see cref="TurnVerdictDto.FailureKind"/>.</summary>
public static class WingmanFailureKinds
{
    /// <summary>The model gave no answer inside its deadline, or the call never reached it.</summary>
    public const string DidNotAnswer = "did-not-answer";

    /// <summary>The model's provider refused the call and asked for a wait.</summary>
    public const string RateLimited = "rate-limited";

    /// <summary>The model could not be asked at all, or the reading broke before it could be stored.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>The model answered, and the answer could not be used.</summary>
    public const string Refused = "refused";

    /// <summary>The judge's answer was accepted and the narration call that follows it produced no words.</summary>
    public const string NarrationFailed = "narration-failed";
}
