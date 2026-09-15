using System.Text.Json.Serialization;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.TurnLog;

/// <summary>
/// ONE turn end on the fleet, written as one self-contained record.
///
/// THE DESIGN RULE, and it is the owner's: every record stands alone. Reading this one record is enough to
/// re-run any judgement against that turn - no join to another record, no lookup in the conversation store,
/// no live Gateway, no Director still running. Records therefore REPEAT each other: the same recent turns
/// and the same session facts appear in every record of a session's morning, and that duplication is the
/// point rather than waste to normalise away. A corpus whose records only mean something next to the
/// database that produced them rots the moment that database rolls, and it rots exactly where the rare
/// cases are.
///
/// The second rule is store more than we think we need. A field we did not capture is a question we cannot
/// ask later, and the turn is gone. That is why <see cref="Session"/> is the WHOLE session snapshot rather
/// than a chosen handful of its properties, and why the terminal is kept raw and whole rather than as the
/// excerpt a judgement happened to read.
///
/// The wire names are spelled out in snake case and never inferred from the property names. The corpus is
/// meant to outlive this code: a record written today must still parse after somebody renames a property
/// here, so the name in the file is a decision rather than a by-product of one.
/// </summary>
public sealed record TurnLogRecord
{
    /// <summary>The shape of this record. Bumped when a field CHANGES MEANING - never when one is merely
    /// added, because an added field cannot break a reader that ignores it. A corpus mixing two shapes must
    /// be able to say which is which without guessing from the fields present.</summary>
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>This record's own identity, minted here. Lets a verdict written later be attached to
    /// exactly one capture, even after the bundle it lives in is re-packed.</summary>
    [JsonPropertyName("record_id")]
    public Guid RecordId { get; init; } = Guid.NewGuid();

    /// <summary>When the capture began - not when the turn ended. The two differ by however long the
    /// Gateway took to notice, and that gap is itself a measurement we will want.</summary>
    [JsonPropertyName("captured_at_utc")]
    public DateTime CapturedAtUtc { get; init; }

    /// <summary>A few facts lifted to the top of the file so a human scanning a bundle can tell records
    /// apart without parsing the whole thing. Every one of them also appears inside <see cref="Session"/>;
    /// this is a reading convenience and never the authority.</summary>
    [JsonPropertyName("at_a_glance")]
    public TurnLogGlance Glance { get; init; } = new();

    /// <summary>The moment itself: what the turn-end detector saw, and how long everything took.</summary>
    [JsonPropertyName("moment")]
    public TurnLogMoment Moment { get; init; } = new();

    /// <summary>
    /// THE WHOLE session snapshot as the Gateway held it at this instant, serialized entire.
    ///
    /// Deliberately not a chosen subset. The instruction was to log all the information about the session
    /// because we may or may not need it, and a subset is a guess about which questions the corpus will be
    /// asked. Keeping the whole snapshot also means a property added to the session next month appears in
    /// the corpus with no change here.
    /// </summary>
    [JsonPropertyName("session")]
    public SessionDto? Session { get; init; }

    /// <summary>The terminal, whole and raw - the live grid every judgement reads, and the scrollback
    /// behind it.</summary>
    [JsonPropertyName("terminal")]
    public TurnLogTerminal Terminal { get; init; } = new();

    /// <summary>The conversation around the turn: the last full turns from BOTH sides, kept as values
    /// rather than as a flattening of them.</summary>
    [JsonPropertyName("conversation")]
    public TurnLogConversation Conversation { get; init; } = new();

    /// <summary>What the running product actually did with this turn end - reported by the machinery
    /// itself, never inferred here.</summary>
    [JsonPropertyName("observed")]
    public TurnLogObserved Observed { get; init; } = new();

    /// <summary>
    /// What SHOULD have happened. Filled in afterwards by a person or a reviewing seat, and NEVER by the
    /// thing being tested.
    ///
    /// Null means UNLABELLED. It must never be read as "the turn was fine" - that is the same fail-open
    /// mistake as a check whose pass condition is an absence, and it would quietly turn every unexamined
    /// record into evidence of correctness.
    /// </summary>
    [JsonPropertyName("verdict")]
    public TurnLogVerdict? Verdict { get; init; }

    /// <summary>
    /// What we FAILED to collect, and why. A part that could not be gathered is NAMED here rather than
    /// simply being absent, because an absent field and an unavailable field look identical in a corpus and
    /// mean opposite things - one is a turn with no scrollback, the other is a hole exactly where the
    /// interesting case was. Empty means everything asked for was collected.
    /// </summary>
    [JsonPropertyName("gaps")]
    public List<TurnLogGap> Gaps { get; init; } = new();
}

/// <summary>The scannable header. Every field is a copy of something inside the record proper.</summary>
public sealed record TurnLogGlance
{
    [JsonPropertyName("session_id")] public string SessionId { get; init; } = "";
    [JsonPropertyName("session_name")] public string? SessionName { get; init; }
    [JsonPropertyName("computer")] public string? Computer { get; init; }
    [JsonPropertyName("agent")] public string? Agent { get; init; }
    [JsonPropertyName("repository")] public string? Repository { get; init; }
    [JsonPropertyName("director_id")] public string DirectorId { get; init; } = "";

    /// <summary>The owning account, as the log names it.</summary>
    [JsonPropertyName("account")] public string Account { get; init; } = "";
}

/// <summary>The turn boundary: what the detector saw, and the timings around it.</summary>
public sealed record TurnLogMoment
{
    /// <summary>The activity state the session was in BEFORE the boundary, as the watcher remembered it.
    /// Null when this session had not been seen before, which is a startup catch-up rather than a live
    /// boundary.</summary>
    [JsonPropertyName("activity_state_before")] public string? ActivityStateBefore { get; init; }

    [JsonPropertyName("activity_state_after")] public string? ActivityStateAfter { get; init; }

    /// <summary>True only for a live Working to Waiting boundary - a genuinely new turn somebody is now
    /// waiting on. False when the session was FIRST seen already waiting, which is a catch-up of a turn
    /// that ended earlier, possibly long earlier. The distinction matters to every later count: a corpus
    /// that mixes them over-reports turn ends in the minutes after a Gateway restart.</summary>
    [JsonPropertyName("is_new_turn")] public bool IsNewTurn { get; init; }

    /// <summary>
    /// When the DETECTOR observed this turn end, as the boundary itself stamped it - not when this record
    /// was captured, and not when any other consumer finished its own work.
    ///
    /// THIS IS THE JOIN KEY. Everything the product makes out of one stop is produced by a different
    /// consumer running at its own pace: this capture, and the Wingman's verdict row, which can be many
    /// seconds later. Each stamps its own completion time, so the only honest way to say "this record and
    /// that verdict are about the same stop" is one moment both copy from the signal. Without it the pair
    /// can only be matched by nearest timestamp, and a corpus built on nearest-timestamp matching is
    /// silently wrong exactly when the judge was slow, which is exactly when the stop was interesting.
    /// </summary>
    [JsonPropertyName("turn_end_observed_at_utc")] public DateTime TurnEndObservedAtUtc { get; init; }

    /// <summary>How long the session had been quiet when we looked, as the session itself reports it.</summary>
    [JsonPropertyName("idle_seconds")] public double? IdleSeconds { get; init; }

    /// <summary>The quiet threshold this session is judged against - the number that decides a turn is over
    /// at all. Stored per record because it is settable, so a corpus spanning a change to it is otherwise
    /// uninterpretable.</summary>
    [JsonPropertyName("quiet_threshold_seconds")] public double? QuietThresholdSeconds { get; init; }

    /// <summary>When the session last did anything, as the session reports it.</summary>
    [JsonPropertyName("last_activity_at_utc")] public DateTime? LastActivityAtUtc { get; init; }

    /// <summary>When the owner last took a turn in this session.</summary>
    [JsonPropertyName("last_owner_turn_at_utc")] public DateTime? LastOwnerTurnAtUtc { get; init; }

    /// <summary>How long the live grid read took.</summary>
    [JsonPropertyName("screen_read_ms")] public long ScreenReadMs { get; init; }

    /// <summary>How long the scrollback read took.</summary>
    [JsonPropertyName("scrollback_read_ms")] public long ScrollbackReadMs { get; init; }

    /// <summary>
    /// How long GATHERING took - from the start of the capture to the assembled record, covering the session
    /// lookup, the screen, the scrollback, the conversation and the observed state.
    ///
    /// It deliberately does NOT include serializing, compressing and writing the file, because those happen
    /// after this value is stamped into the record and a number cannot contain the cost of writing itself.
    /// The write cost is real but bounded and identical for every record; the gathering cost is the one that
    /// varies with how reachable a computer is, and it is the one worth having per turn. Either way the
    /// whole thing is off the turn-end path, so it can be large without costing the product anything - which
    /// is what we want to SEE rather than assume.
    /// </summary>
    [JsonPropertyName("gather_ms")] public long GatherMs { get; init; }
}

/// <summary>The terminal, whole and raw.</summary>
public sealed record TurnLogTerminal
{
    /// <summary>True when the session actually had a resolved live grid. False means the screen was
    /// UNREADABLE - which is evidence of nothing, and must never be read as an empty screen.</summary>
    [JsonPropertyName("has_grid")] public bool HasGrid { get; init; }

    /// <summary>
    /// The visible grid, top to bottom, exactly as the emulator resolved it. Stored WHOLE and untrimmed.
    ///
    /// Every judgement we have reads some excerpt of this - the last forty lines of real content, say - and
    /// that excerpt is a decision we will want to re-take later with a different window. Keeping only the
    /// excerpt makes that impossible, and by then the turn is gone.
    /// </summary>
    [JsonPropertyName("rows")] public List<string> Rows { get; init; } = new();

    [JsonPropertyName("row_count")] public int RowCount { get; init; }
    [JsonPropertyName("cursor_row")] public int CursorRow { get; init; } = -1;
    [JsonPropertyName("cursor_col")] public int CursorCol { get; init; } = -1;

    /// <summary>Whether the hardware cursor is visible - the discriminator between a text composer, which
    /// shows it, and a drawn full-screen menu, which hides it and draws its own marker.</summary>
    [JsonPropertyName("cursor_visible")] public bool CursorVisible { get; init; }

    /// <summary>Whether the agent has the terminal in the alternate screen buffer. While true the
    /// scrollback is empty BY DESIGN, so an empty <see cref="Scrollback"/> beside this being true is
    /// correct rather than a gap.</summary>
    [JsonPropertyName("is_alternate_screen")] public bool IsAlternateScreen { get; init; }

    /// <summary>The scrollback behind the visible screen, raw, oldest first. What the grid cannot show -
    /// how the turn got to where it ended.</summary>
    [JsonPropertyName("scrollback")] public List<string> Scrollback { get; init; } = new();

    [JsonPropertyName("scrollback_line_count")] public int ScrollbackLineCount { get; init; }

    /// <summary>How many scrollback lines we ASKED for. Kept so a record whose scrollback is short can be
    /// told apart from one that was cut by our own request.</summary>
    [JsonPropertyName("scrollback_lines_requested")] public int ScrollbackLinesRequested { get; init; }
}

/// <summary>The conversation around the turn - both sides, as values.</summary>
public sealed record TurnLogConversation
{
    /// <summary>Whether the agent running this session can supply its conversation at all. False is a fact
    /// about the AGENT, not an empty conversation, and the two must not be confused.</summary>
    [JsonPropertyName("is_supported")] public bool IsSupported { get; init; }

    /// <summary>The generation this conversation belongs to - which transcript the session is on. A session
    /// that has been cleared or resumed starts a new one, and turns either side of that boundary are not
    /// one conversation.</summary>
    [JsonPropertyName("generation")] public string? Generation { get; init; }

    /// <summary>How many messages the store held for this generation at capture time. Read against
    /// <see cref="Messages"/> to see how much was cut off the front.</summary>
    [JsonPropertyName("total_message_count")] public int TotalMessageCount { get; init; }

    /// <summary>How many FULL turns were asked for, a full turn being the user's message and the agent's
    /// reply together.</summary>
    [JsonPropertyName("full_turns_requested")] public int FullTurnsRequested { get; init; }

    /// <summary>True when the front of the conversation was cut off - by the turn count, by the size
    /// ceiling, or by both.</summary>
    [JsonPropertyName("truncated")] public bool Truncated { get; init; }

    /// <summary>
    /// How many whole turns were dropped to get under the size ceiling, beyond any the turn count already
    /// cut. Zero means the ceiling was never reached.
    ///
    /// It is recorded rather than left implicit because the two reasons a conversation is short mean
    /// different things: a session that has only had three turns, and a session whose turns were so large
    /// that we could not keep ten. Reading the second as the first would understate how much context the
    /// judgements actually had.
    /// </summary>
    [JsonPropertyName("turns_dropped_for_size")] public int TurnsDroppedForSize { get; init; }

    /// <summary>The ceiling in force when this record was written, in bytes. Stored per record because it
    /// is a tuning decision that will change, and a corpus spanning a change to it is otherwise
    /// uninterpretable.</summary>
    [JsonPropertyName("size_ceiling_bytes")] public int SizeCeilingBytes { get; init; }

    /// <summary>
    /// When the computer that owns this session last pushed its conversation.
    ///
    /// Read it against the record's capture time. The Director announces a state change BEFORE it pushes the
    /// turn that caused it, so a capture can land between the two and store a conversation that stops one
    /// turn short of the screen beside it. A push time earlier than the capture is the signal for that, and
    /// the recorder also writes a gap when the two disagree - so a stale conversation is visible rather than
    /// silently passing as a complete one.
    /// </summary>
    [JsonPropertyName("last_pushed_at_utc")] public DateTime? LastPushedAtUtc { get; init; }

    /// <summary>
    /// The messages themselves, in order, oldest first, from BOTH sides, with their parts intact rather
    /// than flattened to text - so a tool call and its result survive into the corpus as a tool call and
    /// its result. A screen alone often cannot say whether a session is stuck, waiting, or finished; what
    /// came before it can.
    /// </summary>
    [JsonPropertyName("messages")] public List<HistoryMessageDto> Messages { get; init; } = new();
}

/// <summary>What the running product actually did with this turn end.</summary>
public sealed record TurnLogObserved
{
    /// <summary>Whether the session supervisor is switched on for this account at all. Its being off is the
    /// single most ordinary reason a turn end goes unexamined, and a corpus that cannot see that cannot
    /// tell an absent supervisor from a silent one.</summary>
    [JsonPropertyName("supervisor_enabled")] public bool? SupervisorEnabled { get; init; }

    /// <summary>Whether this session is a voice session, and so whether a spoken summary was due.</summary>
    [JsonPropertyName("voice_session")] public bool? VoiceSession { get; init; }

    /// <summary>The spoken summary as the session carried it - words a model has already written about
    /// this screen. On most turn ends that call has already been made and paid for.</summary>
    [JsonPropertyName("voice_summary")] public string? VoiceSummary { get; init; }

    /// <summary>The state the product decided to SHOW for this session - the folded verdict a client
    /// renders. What the person actually saw, as against what the screen said.</summary>
    [JsonPropertyName("state_label")] public string? StateLabel { get; init; }

    /// <summary>The triage bucket the product sorted this session into.</summary>
    [JsonPropertyName("triage_bucket")] public string? TriageBucket { get; init; }

    /// <summary>Whether the product had already decided this session needs the owner, and since when.</summary>
    [JsonPropertyName("needs_you_since_utc")] public DateTime? NeedsYouSinceUtc { get; init; }
}

/// <summary>What should have happened. Written by a person, afterwards.</summary>
public sealed record TurnLogVerdict
{
    /// <summary>What the right outcome was, in plain words.</summary>
    [JsonPropertyName("should_have")] public string ShouldHave { get; init; } = "";

    /// <summary>Who decided. An unattributed verdict cannot be argued with later.</summary>
    [JsonPropertyName("labelled_by")] public string LabelledBy { get; init; } = "";

    [JsonPropertyName("labelled_at_utc")] public DateTime LabelledAtUtc { get; init; }

    [JsonPropertyName("notes")] public string? Notes { get; init; }
}

/// <summary>One part of the record we could not collect, and the reason.</summary>
public sealed record TurnLogGap
{
    /// <summary>Which part is missing - "terminal", "scrollback", "conversation", or "session".</summary>
    [JsonPropertyName("part")] public string Part { get; init; } = "";

    /// <summary>Why, in the words of whatever failed. Never a tidy placeholder.</summary>
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}
