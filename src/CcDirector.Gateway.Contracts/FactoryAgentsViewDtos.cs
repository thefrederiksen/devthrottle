namespace CcDirector.Gateway.Contracts;

// THE FACTORY AGENTS AREA OF THE COCKPIT, AS THE GATEWAY FOLDS IT (Website Business Factory, product track).
//
// Critical rule 7: the client is dumb. Every word, number, colour and grouping on the four tabs - the factory
// cards, a factory agent's page, the Activity rows with the empty trigger checks already collapsed, the Waiting
// for you list, and the Reports table - is decided ONCE on the Gateway (FactoryAgentsFold) and carried here as
// finished strings. The Cockpit renders them verbatim: it never counts, groups, colours or decides what a state
// means. A "tone" is the one word the client maps to a colour class; it is layout, not meaning.
//
// A factory and its factory agents are only what the triggers and the record name. There is no factory table and
// no stored definition yet (#2177 mission 1), so a factory agent's character, limits, skills and versions are not
// here; the page says so in one sentence the Gateway supplies.

/// <summary>The tones a display value may carry. The client maps each to one colour class and nothing else.</summary>
public static class FactoryTone
{
    public const string Ok = "ok";
    public const string Working = "working";
    public const string Idle = "idle";
    public const string Paused = "paused";
    public const string Amber = "amber";
    public const string Red = "red";
    public const string Grey = "grey";
    public const string Neutral = "neutral";
    public const string Blue = "blue";
}

/// <summary><c>GET /gateway/factory-agents/switch</c>: whether the Factory Agents area is on for this Gateway. Always
/// mapped, so the Cockpit can ask even when the area is off; when off, the rail item does not render.</summary>
public sealed class FactoryAgentsSwitchDto
{
    public bool Enabled { get; set; }
}

/// <summary>One of the four tabs, with its finished label ("All factory agents (9)").</summary>
public sealed class FactoryTabDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
}

/// <summary>One choice in a filter list: the value sent back to the Gateway and the words shown.</summary>
public sealed class FactoryChoiceDto
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
}

/// <summary>The time window a view was folded over, and the windows on offer.</summary>
public sealed class FactoryWindowDto
{
    /// <summary>"last-24h", "last-7d", "last-30d" or "custom".</summary>
    public string Key { get; set; } = "";

    /// <summary>The window in words, in the account's time zone ("Last 24 hours", "21 Sep 18:00 to 22 Sep 08:00").</summary>
    public string Label { get; set; } = "";

    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }

    /// <summary>The start and end as the date-time inputs show them, in the account's time zone (yyyy-MM-ddTHH:mm).</summary>
    public string FromLocal { get; set; } = "";
    public string ToLocal { get; set; } = "";

    public List<FactoryChoiceDto> Choices { get; set; } = new();
}

/// <summary>One number on a factory card or a factory agent page ("6 asked, waiting for you").</summary>
public sealed class FactoryNumberDto
{
    public string Text { get; set; } = "";
    public string Tone { get; set; } = FactoryTone.Neutral;

    /// <summary>Where the number opens: "waiting" (the Waiting for you list) or "activity" (Activity filtered to
    /// <see cref="Outcome"/>), or null when it opens nothing.</summary>
    public string? Target { get; set; }

    /// <summary>The outcome word the Activity filter is set to when <see cref="Target"/> is "activity".</summary>
    public string? Outcome { get; set; }

    /// <summary>The Cockpit route the number opens, or null when it opens nothing.</summary>
    public string? Href { get; set; }
}

/// <summary>The Pause or Resume control, finished. Null on a view means there is nothing to pause, and
/// <see cref="FactoryAgentPageDto.PauseUnavailableText"/> says why.</summary>
public sealed class FactoryPauseDto
{
    /// <summary>"pause" or "resume" - the route the button posts to.</summary>
    public string Action { get; set; } = "";
    public string Label { get; set; } = "";
    public string BusyLabel { get; set; } = "";

    /// <summary>The question asked before the change, or null to act on one press.</summary>
    public string? Confirm { get; set; }
}

/// <summary>One factory agent as a row: on a factory card and on the All factory agents tab.</summary>
public sealed class FactoryAgentRowDto
{
    public string FactoryId { get; set; } = "";
    public string FactoryTitle { get; set; } = "";
    public string AgentId { get; set; } = "";

    /// <summary>The factory agent's name with its version when known ("Front Desk v3").</summary>
    public string Name { get; set; } = "";

    /// <summary>What wakes it ("Trigger: New business mail, every 5 min").</summary>
    public string WokenBy { get; set; } = "";

    /// <summary>Its last run in words ("07:00 - 3 asked"), or that there was none in the window.</summary>
    public string LastRun { get; set; } = "";

    public string StatusWord { get; set; } = "";
    public string StatusTone { get; set; } = FactoryTone.Idle;

    /// <summary>The Cockpit route of this factory agent's page.</summary>
    public string Href { get; set; } = "";
}

/// <summary>One factory on the Factories tab (Screen 1).</summary>
public sealed class FactoryCardDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string StatusWord { get; set; } = "";
    public string StatusTone { get; set; } = FactoryTone.Ok;

    /// <summary>"5 factory agents, 1 trigger".</summary>
    public string Subtitle { get; set; } = "";

    /// <summary>The fault in one sentence ("No checks ran in the last 24 hours..."), or null when there is none.</summary>
    public string? FaultText { get; set; }

    public List<FactoryNumberDto> Numbers { get; set; } = new();
    public List<FactoryAgentRowDto> Agents { get; set; } = new();
    public FactoryPauseDto? Pause { get; set; }

    /// <summary>The Cockpit route of this factory's Waiting for you list.</summary>
    public string WaitingHref { get; set; } = "";
}

/// <summary><c>GET /gateway/factory-agents/factories</c>: the Factories tab and the All factory agents tab.</summary>
public sealed class FactoriesViewDto
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public List<FactoryTabDto> Tabs { get; set; } = new();
    public FactoryWindowDto Window { get; set; } = new();
    public List<FactoryCardDto> Factories { get; set; } = new();

    /// <summary>Every factory agent across every factory, for the All factory agents tab.</summary>
    public List<FactoryAgentRowDto> AllAgents { get; set; } = new();

    /// <summary>What the tab says when there is no factory at all, or null when there is one.</summary>
    public string? EmptyText { get; set; }
}

/// <summary>One trigger that wakes a factory agent, on its page.</summary>
public sealed class FactoryWokenByDto
{
    public string TriggerId { get; set; } = "";

    /// <summary>"Trigger: New business mail, every 5 min".</summary>
    public string Text { get; set; } = "";

    public string StatusWord { get; set; } = "";
    public string StatusTone { get; set; } = FactoryTone.Ok;

    /// <summary>The trigger's own status sentence ("check failed: exit code 1"), or null when it is fine.</summary>
    public string? StatusText { get; set; }

    /// <summary>"07:55 - nothing to do", or that it has never checked.</summary>
    public string LastCheck { get; set; } = "";
}

/// <summary><c>GET /gateway/factory-agents/factories/{factory}/agents/{agent}</c>: one factory agent (Screen 2).</summary>
public sealed class FactoryAgentPageDto
{
    public string FactoryId { get; set; } = "";
    public string FactoryTitle { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string Name { get; set; } = "";
    public string StatusWord { get; set; } = "";
    public string StatusTone { get; set; } = FactoryTone.Idle;

    /// <summary>Exactly "Definition not stored yet - #2177 mission 1" until definitions are stored.</summary>
    public string DefinitionText { get; set; } = "";

    public List<FactoryWokenByDto> WokenBy { get; set; } = new();

    /// <summary>What the "Woken by" section says when no trigger names this factory agent, or null.</summary>
    public string? WokenByEmptyText { get; set; }

    public string LastCheck { get; set; } = "";
    public string Last7DaysTitle { get; set; } = "";
    public List<FactoryNumberDto> Last7Days { get; set; } = new();

    public FactoryPauseDto? Pause { get; set; }
    public string? PauseUnavailableText { get; set; }

    public string AskLabel { get; set; } = "";

    /// <summary>The Fleet Manager page with the request already written; opening it sends nothing.</summary>
    public string AskHref { get; set; } = "";

    public string RecentTitle { get; set; } = "";
    public List<FactoryActivityRowViewDto> Recent { get; set; } = new();
    public string? RecentEmptyText { get; set; }
}

/// <summary>One line of the Activity tab: a record row, or a run of empty trigger checks collapsed into one.</summary>
public sealed class FactoryActivityRowViewDto
{
    /// <summary>A stable key for the line: the row's id, or the first collapsed row's id.</summary>
    public string Key { get; set; } = "";

    /// <summary>"02:00", or "02:05-04:05" for a collapsed run; with the date when the window is longer than a day.</summary>
    public string Time { get; set; } = "";

    public string FactoryTitle { get; set; } = "";

    /// <summary>"Front Desk v3".</summary>
    public string Who { get; set; } = "";

    public string What { get; set; } = "";
    public string? Subject { get; set; }
    public string OutcomeWord { get; set; } = "";
    public string OutcomeTone { get; set; } = FactoryTone.Neutral;

    /// <summary>True for a run of empty checks shown as one grey line.</summary>
    public bool Collapsed { get; set; }

    public string? SessionId { get; set; }

    /// <summary>"#a1b2c3d4".</summary>
    public string? SessionLabel { get; set; }

    public string? Link { get; set; }

    /// <summary>A correction in words: "Handled - Marked handled by you at 08:12", or "Corrects the row at 05:30".</summary>
    public string? Note { get; set; }
}

/// <summary>The filters shared by the Activity and Reports tabs, with what was chosen and what is on offer.</summary>
public sealed class FactoryFiltersDto
{
    public string? Factory { get; set; }
    public string? Agent { get; set; }
    public string? Outcome { get; set; }
    public List<FactoryChoiceDto> FactoryChoices { get; set; } = new();
    public List<FactoryChoiceDto> AgentChoices { get; set; } = new();
    public List<FactoryChoiceDto> OutcomeChoices { get; set; } = new();
}

/// <summary><c>GET /gateway/factory-agents/activity</c>: the Activity tab (Screen 3).</summary>
public sealed class FactoryActivityViewDto
{
    public FactoryWindowDto Window { get; set; } = new();
    public FactoryFiltersDto Filters { get; set; } = new();
    public List<FactoryActivityRowViewDto> Rows { get; set; } = new();

    /// <summary>"No checks ran" and its sentence when a factory whose triggers exist wrote nothing in the window.</summary>
    public List<string> Faults { get; set; } = new();

    public string? EmptyText { get; set; }

    /// <summary>Said when the window held more rows than one read returns, so a cut list never reads as complete.</summary>
    public string? TruncatedText { get; set; }

    public string Footnote { get; set; } = "";
    public string CsvHref { get; set; } = "";
    public string SaveLabel { get; set; } = "";
}

/// <summary>One item in the Waiting for you list (Screen 4): an asked or escalated row nothing has corrected.</summary>
public sealed class FactoryWaitingItemDto
{
    public Guid Id { get; set; }
    public string Word { get; set; } = "";
    public string Tone { get; set; } = FactoryTone.Amber;
    public string FactoryTitle { get; set; } = "";
    public string? Subject { get; set; }
    public string What { get; set; } = "";

    /// <summary>"Front Desk, 21 Sep 05:30".</summary>
    public string By { get; set; } = "";

    public string? SessionId { get; set; }
    public string? SessionLabel { get; set; }
    public string? Link { get; set; }
    public string? LinkLabel { get; set; }

    /// <summary>"I have handled it" on an escalation; null where the item clears by itself.</summary>
    public string? HandledLabel { get; set; }
    public string? HandledBusyLabel { get; set; }
}

/// <summary><c>GET /gateway/factory-agents/waiting</c>: the Waiting for you list (Screen 4).</summary>
public sealed class FactoryWaitingViewDto
{
    public string? FactoryId { get; set; }
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<FactoryWaitingItemDto> Items { get; set; } = new();
    public string? EmptyText { get; set; }
    public string? TruncatedText { get; set; }
}

/// <summary>One row of the Reports summary table: a factory agent, or the total.</summary>
public sealed class FactoryReportRowDto
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Cells { get; set; } = new();
}

/// <summary>A report someone kept: a saved filter that reopens the same view.</summary>
public sealed class SavedFactoryReportDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>The filter in words ("Website Business, all factory agents, all outcomes, last 7 days").</summary>
    public string Description { get; set; } = "";

    /// <summary>The Cockpit route that reopens it.</summary>
    public string Href { get; set; } = "";

    public string SavedText { get; set; } = "";
}

/// <summary><c>GET /gateway/factory-agents/reports</c>: the Reports tab (Screen 5).</summary>
public sealed class FactoryReportViewDto
{
    public FactoryWindowDto Window { get; set; } = new();
    public FactoryFiltersDto Filters { get; set; } = new();
    public string TableTitle { get; set; } = "";
    public List<string> Columns { get; set; } = new();
    public List<FactoryReportRowDto> Rows { get; set; } = new();
    public FactoryReportRowDto? Total { get; set; }
    public string? EmptyText { get; set; }
    public string? TruncatedText { get; set; }
    public List<string> Faults { get; set; } = new();
    public string CsvHref { get; set; } = "";
    public string SaveLabel { get; set; } = "";
    public string SavedTitle { get; set; } = "";
    public List<SavedFactoryReportDto> Saved { get; set; } = new();
    public string? SavedEmptyText { get; set; }

    /// <summary>The saved report this view was opened from, or null.</summary>
    public string? OpenedReportName { get; set; }
}

/// <summary>Body of <c>POST /gateway/factory-agents/reports</c>: keep the current filter as a report.</summary>
public sealed class SaveFactoryReportRequest
{
    public string? Name { get; set; }
    public string? Factory { get; set; }
    public string? Agent { get; set; }
    public string? Outcome { get; set; }

    /// <summary>"last-24h", "last-7d", "last-30d" or "custom".</summary>
    public string? Window { get; set; }

    /// <summary>For a custom window only.</summary>
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
}

/// <summary>The "factory agent" chip on a session in the Sessions list (Screen 6). Stamped by the roster fold on a
/// session the record says a factory agent started; null on every other session.</summary>
public sealed class SessionFactoryAgentDto
{
    /// <summary>"Factory agent".</summary>
    public string Label { get; set; } = "";

    /// <summary>"Front Desk v3 - Website Business".</summary>
    public string Text { get; set; } = "";

    /// <summary>The hover sentence.</summary>
    public string Title { get; set; } = "";

    /// <summary>The Cockpit route of the factory agent's page.</summary>
    public string Href { get; set; } = "";
}
