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
    /// <summary>The button's words: "Talk to Nora Hale", "Talk to the CEO", or "Talk" on a seat row.</summary>
    public string Label { get; set; } = "";

    /// <summary>What the button says while the talk is being started: "Starting the talk with Nora Hale...".</summary>
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

    /// <summary>Worst first (FAILING, NEEDS YOU, PAUSED, RUNNING), then by title.</summary>
    public List<FactoryListRowDto> Rows { get; set; } = new();

    /// <summary>The sentence under the list ("Worst first: ...").</summary>
    public string? FooterText { get; set; }

    /// <summary>Set when no factory is registered; the list is then empty.</summary>
    public string? EmptyText { get; set; }

    /// <summary>Set when the record held more rows than one read returns, so a status may be stale.</summary>
    public string? TruncatedText { get; set; }
}

/// <summary>One factory on the list: name, status, what is waiting on the owner, and the CEO's Talk button.</summary>
public sealed class FactoryListRowDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>Exactly one of FAILING, NEEDS YOU, PAUSED, RUNNING.</summary>
    public string StatusWord { get; set; } = "";
    public string StatusTone { get; set; } = "";

    /// <summary>Why the status is what it is, for a tooltip ("A run of Nora Hale failed at 06:20.").</summary>
    public string StatusReason { get; set; } = "";

    /// <summary>"1 question", "2 decisions", "1 question, 1 decision", or "-".</summary>
    public string WaitingText { get; set; } = "";

    /// <summary>The factory's page.</summary>
    public string Href { get; set; } = "";

    /// <summary>The CEO's Talk button, or null when the factory has no CEO.</summary>
    public FactoryTalkDto? Talk { get; set; }

    /// <summary>"No CEO" when <see cref="Talk"/> is null, else null.</summary>
    public string? NoCeoText { get; set; }
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

    /// <summary>"CEO Nora Hale", or "No CEO".</summary>
    public string CeoText { get; set; } = "";

    /// <summary>"4 seats".</summary>
    public string SeatCountText { get; set; } = "";

    /// <summary>"runs on SOREN_NORTH".</summary>
    public string ComputerText { get; set; } = "";

    /// <summary>"change - coming": moving a factory to another computer is not built yet, and this is a label,
    /// never a control.</summary>
    public string ComputerChangeText { get; set; } = "";

    /// <summary>The CEO's Talk button, or null when there is no CEO.</summary>
    public FactoryTalkDto? Talk { get; set; }

    /// <summary>Overview, Seats (n), Activity, Reports, Memory, Documents - in that order.</summary>
    public List<FactoryTabDto> Tabs { get; set; } = new();

    public FactoryGoalCardDto Goal { get; set; } = new();
    public FactoryGoalNumberCardDto GoalNumber { get; set; } = new();
    public FactoryPageWaitingDto Waiting { get; set; } = new();
    public FactoryCeoLatestDto CeoLatest { get; set; } = new();
    public FactoryLastTalkDto LastTalk { get; set; } = new();

    /// <summary>What the Documents tab says. The definitions are not on the Gateway yet, so it says that, and
    /// shows no documents.</summary>
    public string DocumentsText { get; set; } = "";

    /// <summary>Set when the record held more rows than one read returns.</summary>
    public string? TruncatedText { get; set; }
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

/// <summary>The newest goal number the CEO posted.</summary>
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
}

/// <summary>The CEO's latest lines in the activity record.</summary>
public sealed class FactoryCeoLatestDto
{
    /// <summary>"Latest from the CEO".</summary>
    public string Heading { get; set; } = "";

    /// <summary>Newest first: "Today 06:20 - Feed healthy. Found zone 8 holding 20 C; asked you."</summary>
    public List<string> Lines { get; set; } = new();

    /// <summary>Set when there is nothing to show ("No CEO." or "Nothing from Nora Hale in the last 7 days.").</summary>
    public string? EmptyText { get; set; }

    /// <summary>"All reports" and where it goes: the Activity tab filtered to the CEO. Null when there is no CEO.</summary>
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

/// <summary><c>GET /gateway/factories/{factory}/seats</c>: the Seats tab - every registered seat, CEO first.</summary>
public sealed class FactorySeatsViewDto
{
    public string FactoryId { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>"Factories / WarmForward / Seats".</summary>
    public string Crumb { get; set; } = "";

    /// <summary>The column headings, in order: "Seat", "When it runs", "Last run", "Computer".</summary>
    public List<string> Columns { get; set; } = new();

    public List<FactorySeatRowDto> Rows { get; set; } = new();

    /// <summary>"Only seats the CEO hired are listed. ..."</summary>
    public string Note { get; set; } = "";
}

/// <summary>One seat: who it is, when it runs, how its last run ended, where it runs, and its Talk button.</summary>
public sealed class FactorySeatRowDto
{
    public string SeatId { get; set; } = "";

    /// <summary>"Nora Hale".</summary>
    public string Name { get; set; } = "";

    /// <summary>"CEO".</summary>
    public string Role { get; set; } = "";

    /// <summary>"Daily 06:15", "06:00 and 18:00", "Wednesday 05:30", "Not scheduled".</summary>
    public string WhenText { get; set; } = "";

    /// <summary>"Today 06:20 - succeeded", "Not run yet".</summary>
    public string LastRunText { get; set; } = "";
    public string LastRunTone { get; set; } = "";

    /// <summary>The computer the seat runs on.</summary>
    public string ComputerText { get; set; } = "";

    /// <summary>"change - coming": a label, never a control (decision 7).</summary>
    public string ComputerChangeText { get; set; } = "";

    public FactoryTalkDto Talk { get; set; } = new();
}
