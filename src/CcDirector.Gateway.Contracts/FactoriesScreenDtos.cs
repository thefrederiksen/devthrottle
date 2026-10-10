namespace CcDirector.Gateway.Contracts;

// The Factories screen (Factories screen mission, phase B): the list of factories, one factory's page, and its
// Seats tab. Every word, tone and order here is decided on the Gateway (FactoriesScreenFold) and the Cockpit renders
// it verbatim - critical rule 7. Built from the factory registry, so a factory with no activity is still listed and
// a session that only wrote activity rows is never shown as a seat.

/// <summary>
/// What a Talk button starts: a session seated as one seat of one factory. The Cockpit sends the two ids back to the
/// Talk endpoint (phase C); it never works out which agent a button means.
/// </summary>
public sealed class FactoryTalkDto
{
    /// <summary>The button's words: "Talk to the boss", or "Talk" on a seat row.</summary>
    public string Label { get; set; } = "";

    /// <summary>What the button says while the talk is being started: "Starting the talk with the boss...".</summary>
    public string BusyLabel { get; set; } = "";

    public string FactoryId { get; set; } = "";
    public string SeatId { get; set; } = "";
}

/// <summary><c>GET /gateway/factories</c>: the Factories tab - one row per registered factory, worst first.</summary>
public sealed class FactoriesListViewDto
{
    /// <summary>"Factories".</summary>
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";

    /// <summary>Factories, Activity, Reports - in that order.</summary>
    public List<FactoryTabDto> Tabs { get; set; } = new();

    /// <summary>The column headings, in order: "Factory", "Waiting on you", "Status".</summary>
    public List<string> Columns { get; set; } = new();

    /// <summary>Worst first (FAILING, NEEDS YOU, PAUSED, RUNNING), then by title. The Cockpit re-sorts them in the
    /// order the owner picks; this order stays for older clients.</summary>
    public List<FactoryListRowDto> Rows { get; set; } = new();

    /// <summary>The sentence under the list ("Worst first: ..."). Kept for older clients; the Cockpit now states the
    /// order the owner picked instead.</summary>
    public string? FooterText { get; set; }

    /// <summary>Set when no factory is registered; the list is then empty.</summary>
    public string? EmptyText { get; set; }

    /// <summary>Set when the record held more rows than one read returns, so a status may be stale.</summary>
    public string? TruncatedText { get; set; }

    /// <summary>"Show archived (1)": the control that opens the archived factories (round 2).</summary>
    public string ShowArchivedLabel { get; set; } = "";

    /// <summary>"Hide archived".</summary>
    public string HideArchivedLabel { get; set; } = "";

    /// <summary>The archived factories, by title. They are never in <see cref="Rows"/> nor in its status order.</summary>
    public List<FactoryArchivedRowDto> ArchivedRows { get; set; } = new();

    /// <summary>"No factory is archived." when <see cref="ArchivedRows"/> is empty, else null.</summary>
    public string? ArchivedEmptyText { get; set; }

    /// <summary>"Schedules outside any factory" (issue #3650).</summary>
    public string OutsideTitle { get; set; } = "";

    /// <summary>What the list is and what to do about a row on it.</summary>
    public string OutsideText { get; set; } = "";

    /// <summary>Every enabled schedule that runs as no registered factory seat, by name. Never hidden: a stray
    /// schedule is seen the day it appears.</summary>
    public List<FactoryOutsideScheduleRowDto> OutsideRows { get; set; } = new();

    /// <summary>Set when there are none.</summary>
    public string? OutsideEmptyText { get; set; }
}

/// <summary>One enabled schedule that is no seat of any registered factory (issue #3650).</summary>
public sealed class FactoryOutsideScheduleRowDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>When it runs, in words, in the account's zone.</summary>
    public string WhenText { get; set; } = "";

    /// <summary>The computer it runs on.</summary>
    public string Machine { get; set; } = "";

    /// <summary>Why it is outside: in no factory, or naming a factory or seat the registry does not have.</summary>
    public string Reason { get; set; } = "";
}

/// <summary>One archived factory: what it is, when and by whom it was archived, and its Restore.</summary>
public sealed class FactoryArchivedRowDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>The factory's page, which still opens.</summary>
    public string Href { get; set; } = "";

    /// <summary>"Archived 6 Oct 23:50 by the owner".</summary>
    public string ArchivedText { get; set; } = "";

    public FactoryOwnerActionDto Restore { get; set; } = new();
}

/// <summary>
/// One owner-only action and the confirm it asks first (round 2): every word of the confirm is the Gateway's, and
/// what the Cockpit sends back is exactly what the confirm described - the cut-off and count it showed, or the
/// schedules it named - so the Gateway refuses rather than do something other than what the owner read.
/// </summary>
public sealed class FactoryOwnerActionDto
{
    /// <summary>"handled-older", "archive" or "restore".</summary>
    public string Action { get; set; } = "";
    public string FactoryId { get; set; } = "";

    /// <summary>The button: "Mark everything older than 7 days as handled", "Archive factory", "Restore".</summary>
    public string Label { get; set; } = "";

    /// <summary>What the button says while the Gateway works.</summary>
    public string BusyLabel { get; set; } = "";

    /// <summary>The confirm's heading: "Archive Tallyhand?".</summary>
    public string ConfirmTitle { get; set; } = "";

    /// <summary>The confirm's sentences, in order: exactly what will happen.</summary>
    public List<string> ConfirmLines { get; set; } = new();

    /// <summary>The confirm's button: "Mark 61 handled", "Archive Tallyhand".</summary>
    public string ConfirmLabel { get; set; } = "";

    /// <summary>True when the confirm's button is drawn as destructive (red): the bulk clear and Archive. Restore is
    /// not. Decided here so the Cockpit never branches on the action's kind (critical rule 7).</summary>
    public bool Danger { get; set; }

    /// <summary>handled-older: items older than this are marked. Sent back unchanged.</summary>
    public DateTime? CutoffUtc { get; set; }

    /// <summary>handled-older: how many the confirm said. Sent back unchanged.</summary>
    public int? ExpectedCount { get; set; }

    /// <summary>archive and restore: the schedule ids the confirm said it switches. Sent back unchanged.</summary>
    public List<string> Schedules { get; set; } = new();
}

/// <summary>The body of an owner action: what the confirm showed, sent back.</summary>
public sealed class FactoryOwnerActionRequest
{
    public DateTime? CutoffUtc { get; set; }
    public int? ExpectedCount { get; set; }
    public List<string>? Schedules { get; set; }
}

/// <summary>What an owner action did, in one sentence the Cockpit shows.</summary>
public sealed class FactoryOwnerActionResultDto
{
    public string Text { get; set; } = "";

    /// <summary>handled-older: the rows written for the items (not counting the owner's own row).</summary>
    public int Marked { get; set; }

    /// <summary>archive and restore: the schedule ids actually switched.</summary>
    public List<string> SchedulesSwitched { get; set; } = new();
}

/// <summary>One factory on the list: name, status, what is waiting on the owner, and the boss's Talk button.</summary>
public sealed class FactoryListRowDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>Exactly one of FAILING, NEEDS YOU, PAUSED, RUNNING.</summary>
    public string StatusWord { get; set; } = "";
    public string StatusTone { get; set; } = "";

    /// <summary>Where <see cref="StatusWord"/> stands worst first: 0 FAILING, 1 NEEDS YOU, 2 PAUSED, 3 RUNNING. The
    /// Cockpit sorts by it when the owner picks "Status (worst first)"; the meaning stays the Gateway's.</summary>
    public int StatusRank { get; set; }

    /// <summary>Why the status is what it is, in full, for a tooltip ("Nora Hale failed today 06:20: ...").</summary>
    public string StatusReason { get; set; } = "";

    /// <summary>The one short line shown under the status word: "Sender: 4 failures, newest today 12:02: keep.page
    /// (failed): ...", "2 decisions since 21 Sep 10:00; newest ...", "Nothing scheduled", "3 schedules switched off".
    /// Null for RUNNING, which needs no explaining.</summary>
    public string? StatusLine { get; set; }

    /// <summary>Where clicking the status word goes: the failures on the factory's page for FAILING, its waiting
    /// items for NEEDS YOU, its Seats tab for PAUSED. Null for RUNNING.</summary>
    public string? StatusHref { get; set; }

    /// <summary>"1 question", "2 decisions", "1 question, 1 decision", or "-".</summary>
    public string WaitingText { get; set; } = "";

    /// <summary>How many open items <see cref="WaitingText"/> counts (questions plus decisions); 0 for "-". The Cockpit
    /// sorts by it when the owner picks "Waiting on you".</summary>
    public int WaitingCount { get; set; }

    /// <summary>Where clicking the waiting count goes - the waiting items on the factory's page. Null when nothing
    /// is waiting.</summary>
    public string? WaitingHref { get; set; }

    /// <summary>The factory's page.</summary>
    public string Href { get; set; } = "";

    /// <summary>The boss's Talk button ("Talk to the boss"), or null when the factory has no boss. The boss is the
    /// registry's <c>bossSeat</c>; it has no name of its own.</summary>
    public FactoryTalkDto? Talk { get; set; }

    /// <summary>"No boss named" when <see cref="Talk"/> is null, else null.</summary>
    public string? NoBossText { get; set; }

    /// <summary>The factory's one-line purpose from the registry ("Builds and sells websites for local trades"),
    /// shown under the name on its card; null when none is set.</summary>
    public string? Purpose { get; set; }

    /// <summary>The boss's role word ("Boss", or "CFO") for the card's avatar and name line - never a person's
    /// name (the owner, 8 October 2026); null when no boss is named.</summary>
    public string? BossName { get; set; }
}

/// <summary><c>GET /gateway/factories/{factory}</c>: one factory's page - its header, tabs and Overview.</summary>
public sealed class FactoryPageViewDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>"Factories / WarmForward".</summary>
    public string Crumb { get; set; } = "";
    public string CrumbHref { get; set; } = "";

    public string StatusWord { get; set; } = "";
    public string StatusTone { get; set; } = "";
    public string StatusReason { get; set; } = "";

    /// <summary>The one short line shown under the status word: "Sender: 4 failures, newest today 12:02: keep.page
    /// (failed): ...", "2 decisions since 21 Sep 10:00; newest ...", "Nothing scheduled", "3 schedules switched off".
    /// Null for RUNNING, which needs no explaining.</summary>
    public string? StatusLine { get; set; }

    /// <summary>Where clicking the status word goes: the failures on the factory's page for FAILING, its waiting
    /// items for NEEDS YOU, its Seats tab for PAUSED. Null for RUNNING.</summary>
    public string? StatusHref { get; set; }

    /// <summary>The boss seat's role word - "Boss", or a distinct one the factory registered ("CFO") - or "No boss
    /// named". Never a person's name: the boss has none.</summary>
    public string BossText { get; set; } = "";

    /// <summary>"4 seats".</summary>
    public string SeatCountText { get; set; } = "";

    /// <summary>"runs on SOREN_NORTH".</summary>
    public string ComputerText { get; set; } = "";

    /// <summary>The boss's Talk button, or null when there is no boss.</summary>
    public FactoryTalkDto? Talk { get; set; }

    /// <summary>Overview, Seats (n), Activity, Reports, Memory, Documents - in that order.</summary>
    public List<FactoryTabDto> Tabs { get; set; } = new();

    public FactoryGoalCardDto Goal { get; set; } = new();
    public FactoryGoalNumberCardDto GoalNumber { get; set; } = new();

    /// <summary>What is failing now, where FAILING links to (<c>#failing</c>). Null when nothing is.</summary>
    public FactoryPageFailuresDto? Failures { get; set; }

    public FactoryPageWaitingDto Waiting { get; set; } = new();
    public FactoryBossLatestDto BossLatest { get; set; } = new();
    public FactoryLastTalkDto LastTalk { get; set; } = new();

    /// <summary>What the Documents tab says. The definitions are not on the Gateway yet, so it says that, and
    /// shows no documents.</summary>
    public string DocumentsText { get; set; } = "";

    /// <summary>Set when the record held more rows than one read returns.</summary>
    public string? TruncatedText { get; set; }

    /// <summary>"Archive factory" with its confirm, or null when the factory is archived.</summary>
    public FactoryOwnerActionDto? Archive { get; set; }

    /// <summary>"Archived 6 Oct 23:50 by the owner. It is not on the Factories list." when archived, else null.</summary>
    public string? ArchivedText { get; set; }

    /// <summary>"Restore" with its confirm when archived, else null.</summary>
    public FactoryOwnerActionDto? Restore { get; set; }
}

/// <summary>The goal, as the owner approved it.</summary>
public sealed class FactoryGoalCardDto
{
    /// <summary>"Goal".</summary>
    public string Heading { get; set; } = "";

    /// <summary>The goal's text, or null when there is none.</summary>
    public string? Text { get; set; }

    /// <summary>"Only you change the goal. Approved 4 Oct 2026." (or without the date when none was recorded).
    /// Null when there is no goal.</summary>
    public string? Note { get; set; }

    /// <summary>"No goal set yet" when there is no goal, else null.</summary>
    public string? EmptyText { get; set; }
}

/// <summary>The newest goal number the boss posted.</summary>
public sealed class FactoryGoalNumberCardDto
{
    /// <summary>"Goal number - posted by Nora Hale, today 06:20", or "Goal number" when none was posted.</summary>
    public string Heading { get; set; } = "";

    /// <summary>"Propane saved this season: not yet proven" - the unit, then the value. Null when none.</summary>
    public string? ValueText { get; set; }

    /// <summary>"As of 6 Oct 2026". Null when none.</summary>
    public string? AsOfText { get; set; }

    /// <summary>The link to how it was measured, and its words ("How it is measured"). Null when none.</summary>
    public string? LinkHref { get; set; }
    public string? LinkLabel { get; set; }

    /// <summary>"No number posted yet" when none, else null.</summary>
    public string? EmptyText { get; set; }
}

/// <summary>What is waiting on the owner from this factory.</summary>
public sealed class FactoryPageWaitingDto
{
    /// <summary>"Waiting on you".</summary>
    public string Heading { get; set; } = "";
    public List<FactoryWaitingItemDto> Items { get; set; } = new();

    /// <summary>"Nothing is waiting on you." when empty, else null.</summary>
    public string? EmptyText { get; set; }

    /// <summary>The order the items are in, said once: "Decisions first, then questions; newest first in each." Null
    /// when empty.</summary>
    public string? OrderText { get; set; }

    /// <summary>"Mark everything older than 7 days as handled" with its confirm, or null when no item is that old.</summary>
    public FactoryOwnerActionDto? BulkHandled { get; set; }

    /// <summary>"Nothing here is older than 7 days." when there are items but none that old, else null.</summary>
    public string? BulkHandledNote { get; set; }
}

/// <summary>The failures that still make a factory FAILING: none has been marked handled, and its seat has not
/// since succeeded at the same subject (Factories screen round 2).</summary>
public sealed class FactoryPageFailuresDto
{
    /// <summary>"Failing".</summary>
    public string Heading { get; set; } = "";

    /// <summary>How a failure stops counting, and that Handled adds a row rather than changing one.</summary>
    public string Note { get; set; } = "";

    /// <summary>Newest first: any schedule that could not start its run, then the failed rows.</summary>
    public List<FactoryFailureItemDto> Items { get; set; } = new();
}

/// <summary>One failure on a factory's page.</summary>
public sealed class FactoryFailureItemDto
{
    /// <summary>The failed row; Handled sends it back. Null for a schedule that could not start its run, which is
    /// not a row and clears by itself.</summary>
    public Guid? Id { get; set; }

    public string? Subject { get; set; }

    /// <summary>The row's own words.</summary>
    public string What { get; set; } = "";

    /// <summary>"Sender, today 12:02".</summary>
    public string By { get; set; } = "";

    public string? SessionId { get; set; }

    /// <summary>The session's short label, as the waiting items show it. Null when there is no session.</summary>
    public string? SessionLabel { get; set; }

    /// <summary>The evidence the row links to, and its words ("Open"). Null when it has none.</summary>
    public string? Link { get; set; }
    public string? LinkLabel { get; set; }

    /// <summary>"Handled", and what it says while it is written. Null when the item cannot be marked handled.</summary>
    public string? HandledLabel { get; set; }
    public string? HandledBusyLabel { get; set; }

    /// <summary>Set when the item cannot be marked handled: how it clears instead.</summary>
    public string? Note { get; set; }
}

/// <summary>The boss's latest lines in the activity record.</summary>
public sealed class FactoryBossLatestDto
{
    /// <summary>"Latest from the boss".</summary>
    public string Heading { get; set; } = "";

    /// <summary>Newest first: "Today 06:20 - Feed healthy. Found zone 8 holding 20 C; asked you."</summary>
    public List<string> Lines { get; set; } = new();

    /// <summary>Set when there is nothing to show ("This factory has no boss named." or "Nothing from the boss in the
    /// last 7 days.").</summary>
    public string? EmptyText { get; set; }

    /// <summary>"All reports" and where it goes: the Activity tab filtered to the boss. Null when there is no boss.</summary>
    public string? AllLabel { get; set; }
    public string? AllHref { get; set; }
}

/// <summary>The last time the owner talked with this factory (rows whose outcome is "talked").</summary>
public sealed class FactoryLastTalkDto
{
    /// <summary>"Last talk with you".</summary>
    public string Heading { get; set; } = "";

    /// <summary>"Talked with you, today 08:10 - decided: bunkie back to 13 C.", or "None yet."</summary>
    public string Text { get; set; } = "";
}

/// <summary><c>GET /gateway/factories/{factory}/seats</c>: the Seats tab - every registered seat, the boss first.</summary>
public sealed class FactorySeatsViewDto
{
    public string FactoryId { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>"Factories / WarmForward / Seats".</summary>
    public string Crumb { get; set; } = "";

    /// <summary>The column headings, in order: "Seat", "When it runs", "Last run", "Computer".</summary>
    public List<string> Columns { get; set; } = new();

    public List<FactorySeatRowDto> Rows { get; set; } = new();

    /// <summary>"Only seats the boss hired are listed. ..."</summary>
    public string Note { get; set; } = "";
}

/// <summary>One seat: who it is, when it runs, how its last run ended, where it runs, and its Talk button.</summary>
public sealed class FactorySeatRowDto
{
    public string SeatId { get; set; } = "";

    /// <summary>"Nora Hale".</summary>
    public string Name { get; set; } = "";

    /// <summary>"Boss", "Scout".</summary>
    public string Role { get; set; } = "";

    /// <summary>"Daily 06:15", "06:00 and 18:00", "Wednesday 05:30", "Not scheduled".</summary>
    public string WhenText { get; set; } = "";

    /// <summary>"Today 06:20 - succeeded", "Not run yet".</summary>
    public string LastRunText { get; set; } = "";
    public string LastRunTone { get; set; } = "";

    /// <summary>The computer the seat runs on.</summary>
    public string ComputerText { get; set; } = "";

    public FactoryTalkDto Talk { get; set; } = new();

    /// <summary>
    /// Each of the seat's schedules that exists, with its own words and the address the Cockpit edits it by - the
    /// factory is where a factory's schedules are changed (the owner, 2026-10-09). A schedule the seat names but the
    /// store no longer has is left out here; <see cref="WhenText"/> already says it is missing.
    /// </summary>
    public List<FactorySeatScheduleDto> Schedules { get; set; } = new();
}

/// <summary>One schedule of a seat on the Seats tab, with the button that edits it.</summary>
public sealed class FactorySeatScheduleDto
{
    /// <summary>The schedule's id, which the Cockpit reads it by to open the editor.</summary>
    public string JobId { get; set; } = "";

    /// <summary>"Daily 06:15" - the same words as the seat's When it runs.</summary>
    public string WhenText { get; set; } = "";

    /// <summary>"Edit schedule".</summary>
    public string EditLabel { get; set; } = "";
}
