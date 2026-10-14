namespace CcDirector.Gateway.Contracts;

// One seat's page (Factory Seats mission, phase 1): who the seat is, what it is told to do, how it starts, the
// factory's goal and number, and what it has left behind. A seat has this page whether or not anything is scheduled.
// Every word, tone and order here is decided on the Gateway (FactorySeatPageFold) and the Cockpit renders it
// verbatim - critical rule 7.

/// <summary><c>GET /gateway/factories/{factory}/seats/{seat}</c>: one seat's page.</summary>
public sealed class FactorySeatPageViewDto
{
    public string FactoryId { get; set; } = "";
    public string FactoryTitle { get; set; } = "";
    public string SeatId { get; set; } = "";

    /// <summary>"Factories / WarmForward / Seats / Scout".</summary>
    public string Crumb { get; set; } = "";

    /// <summary>Where each part of <see cref="Crumb"/> but the last goes, in order: the Factories list, the factory's
    /// page, its Seats tab.</summary>
    public List<string> CrumbHrefs { get; set; } = new();

    /// <summary>The seat's name, or "Boss" for the boss seat, which has no name of its own.</summary>
    public string Name { get; set; } = "";

    /// <summary>The seat's role: "Boss", "Scout".</summary>
    public string Role { get; set; } = "";

    /// <summary>"runs on SOREN_NORTH".</summary>
    public string ComputerText { get; set; } = "";

    /// <summary>How the seat starts, in one or two words: "Talk only", "Scheduled", "Triggered", "Scheduled and
    /// triggered", "Paused", or "Schedule missing".</summary>
    public string StartsWord { get; set; } = "";
    public string StartsTone { get; set; } = FactoryTone.Grey;

    public FactoryTalkDto Talk { get; set; } = new();

    public FactorySeatBriefDto Brief { get; set; } = new();
    public FactorySeatStartsDto Starts { get; set; } = new();
    public FactoryGoalCardDto Goal { get; set; } = new();
    public FactoryGoalNumberCardDto GoalNumber { get; set; } = new();
    public FactorySeatLeftBehindDto LeftBehind { get; set; } = new();

    /// <summary>Set when the record held more rows than one read returns.</summary>
    public string? TruncatedText { get; set; }
}

/// <summary>The three ways a seat's brief can stand, as <see cref="FactorySeatBriefDto.State"/> says it.</summary>
public static class FactorySeatBriefStates
{
    /// <summary>The seat has a published brief on the Gateway: the panel shows its text.</summary>
    public const string Published = "published";

    /// <summary>The seat has drafts on the Gateway and none is published.</summary>
    public const string Drafts = "drafts";

    /// <summary>The seat has no brief on the Gateway: its brief is a file on its computer.</summary>
    public const string File = "file";
}

/// <summary>
/// "What this seat does": the seat's brief (Factory Seats mission, phases 1 and 2b). A seat with a published brief on
/// the Gateway shows its text, its version, who published it and when. A seat with only drafts says so. A seat with
/// no brief on the Gateway shows where its brief file is. Every word is the Gateway's
/// (<c>FactoryBriefPanelFold</c>); the Cockpit draws the fields that are not null.
/// </summary>
public sealed class FactorySeatBriefDto
{
    /// <summary>"What this seat does".</summary>
    public string Heading { get; set; } = "";

    /// <summary>One of <see cref="FactorySeatBriefStates"/>.</summary>
    public string State { get; set; } = FactorySeatBriefStates.File;

    /// <summary>The published brief's whole text; null when nothing is published.</summary>
    public string? Text { get; set; }

    /// <summary>"Version 3, published by a person (device browser abc) today 20:14."; null when nothing is published.</summary>
    public string? VersionText { get; set; }

    /// <summary>What is waiting: that a newer draft exists and is not what talks and runs are given, or that the
    /// seat has drafts and none is published. Null when no draft waits.</summary>
    public string? DraftText { get; set; }

    /// <summary>Set when as many drafts wait as a session may leave: sessions are refused another until one is
    /// published. Null below that.</summary>
    public string? WaitingText { get; set; }

    /// <summary>"Brief file"; null when the brief is published on the Gateway.</summary>
    public string? PathLabel { get; set; }

    /// <summary>The brief file's full path on its computer; null when the brief is published on the Gateway.</summary>
    public string? PathText { get; set; }

    /// <summary>"Computer"; null when the brief is published on the Gateway.</summary>
    public string? ComputerLabel { get; set; }

    /// <summary>The computer the file is on: the seat's. Null when the brief is published on the Gateway.</summary>
    public string? ComputerText { get; set; }

    /// <summary>That the brief is a file on that computer and what a talk and a run are told. Null when the brief is
    /// published on the Gateway.</summary>
    public string? Note { get; set; }

    /// <summary>The button that opens the editor: "Edit", "Open the newest draft" or "Write the brief here".</summary>
    public string EditLabel { get; set; } = "";

    /// <summary>"History"; null for a seat with no version on the Gateway, which has no history.</summary>
    public string? HistoryLabel { get; set; }
}

/// <summary>"How it starts": the seat's schedules and triggers, or that it is talk only.</summary>
public sealed class FactorySeatStartsDto
{
    /// <summary>"How it starts".</summary>
    public string Heading { get; set; } = "";

    /// <summary>"Talk only. Nothing is scheduled for this seat; you run it by talking to it." when the seat has no
    /// schedule and no trigger, else null.</summary>
    public string? TalkOnlyText { get; set; }

    /// <summary>Every schedule the seat names, in its order.</summary>
    public List<FactorySeatPageScheduleDto> Schedules { get; set; } = new();

    /// <summary>Every trigger that starts the seat.</summary>
    public List<FactoryWokenByDto> Triggers { get; set; } = new();

    /// <summary>Pause or Resume for the seat's triggers, or null when it has none.</summary>
    public FactoryPauseDto? TriggersPause { get; set; }
}

/// <summary>One schedule of a seat, on the seat's page.</summary>
public sealed class FactorySeatPageScheduleDto
{
    /// <summary>The schedule's id.</summary>
    public string JobId { get; set; } = "";

    /// <summary>The schedule's name, or null for one the Gateway no longer has.</summary>
    public string? Name { get; set; }

    /// <summary>"Daily 06:15", or "Schedule cj_x is missing".</summary>
    public string WhenText { get; set; } = "";

    /// <summary>The shifts it starts runs in: "Morning shift", "No run in the next 7 days".</summary>
    public string ShiftText { get; set; } = "";

    /// <summary>"Paused" when it is switched off, else null.</summary>
    public string? PausedText { get; set; }

    /// <summary>What to do about a schedule the Gateway no longer has, else null.</summary>
    public string? MissingText { get; set; }

    /// <summary>"Edit schedule"; null for a schedule the Gateway no longer has.</summary>
    public string? EditLabel { get; set; }

    /// <summary>True when this schedule is switched off. The toggle beside it then resumes it.</summary>
    public bool Paused { get; set; }

    /// <summary>"Pause" or "Resume"; null for a schedule the Gateway no longer has and for a one-off that has run.</summary>
    public string? ToggleLabel { get; set; }

    /// <summary>"Take off this seat" with its confirm; null for a schedule the Gateway no longer has.</summary>
    public FactorySeatUnlinkDto? Unlink { get; set; }
}

/// <summary>
/// "Take off this seat": the button and the confirm it asks first. The schedule stays, switched as it was, and
/// belongs to no factory afterwards.
/// </summary>
public sealed class FactorySeatUnlinkDto
{
    public string JobId { get; set; } = "";

    /// <summary>"Take off this seat".</summary>
    public string Label { get; set; } = "";

    /// <summary>What the button says while the Gateway works.</summary>
    public string BusyLabel { get; set; } = "";

    /// <summary>The confirm's heading.</summary>
    public string ConfirmTitle { get; set; } = "";

    /// <summary>The confirm's sentences, in order: exactly what will happen.</summary>
    public List<string> ConfirmLines { get; set; } = new();

    /// <summary>The confirm's button.</summary>
    public string ConfirmLabel { get; set; } = "";
}

/// <summary><c>POST /cron/jobs/{id}/unlink</c>: what taking a schedule off its seat did.</summary>
public sealed class CronUnlinkResultDto
{
    /// <summary>What happened, in one or two sentences.</summary>
    public string Text { get; set; } = "";

    /// <summary>The factory the schedule was taken out of.</summary>
    public string Factory { get; set; } = "";

    /// <summary>The seat it was taken off, or null when the schedule named a factory and no seat.</summary>
    public string? Seat { get; set; }

    /// <summary>The schedule as it is now: in no factory, switched as it was.</summary>
    public CronJobDto Job { get; set; } = new();
}

/// <summary>"What it has left behind": the seat's last talk, its last run, its recent activity and the factory's memory.</summary>
public sealed class FactorySeatLeftBehindDto
{
    /// <summary>"What it has left behind".</summary>
    public string Heading { get; set; } = "";

    /// <summary>"Last talk".</summary>
    public string LastTalkLabel { get; set; } = "";

    /// <summary>"Talked with you, today 08:10 - decided: bunkie back to 13 C.", or "None yet."</summary>
    public string LastTalkText { get; set; } = "";

    /// <summary>"Last run".</summary>
    public string LastRunLabel { get; set; } = "";

    /// <summary>"Today 06:20 - succeeded", "Not run yet".</summary>
    public string LastRunText { get; set; } = "";
    public string LastRunTone { get; set; } = FactoryTone.Grey;

    /// <summary>Whether its sessions close themselves; null for a seat with no schedule, which has no runs to judge.</summary>
    public string? ClosingText { get; set; }
    public string? ClosingTone { get; set; }

    /// <summary>"Last 7 days".</summary>
    public string NumbersTitle { get; set; } = "";
    public List<FactoryNumberDto> Numbers { get; set; } = new();

    /// <summary>"Recent activity".</summary>
    public string RecentTitle { get; set; } = "";
    public List<FactoryActivityRowViewDto> Recent { get; set; } = new();

    /// <summary>"Nothing recorded in the last 7 days." when <see cref="Recent"/> is empty, else null.</summary>
    public string? RecentEmptyText { get; set; }

    /// <summary>"All activity of this seat" and where it goes: the Activity tab filtered to the seat.</summary>
    public string AllLabel { get; set; } = "";
    public string AllHref { get; set; } = "";

    /// <summary>"Memory".</summary>
    public string MemoryLabel { get; set; } = "";

    /// <summary>"3 notes in the factory's memory. Memory belongs to the factory, not to one seat."</summary>
    public string MemoryText { get; set; } = "";

    /// <summary>"Open Memory" and the factory's Memory tab.</summary>
    public string MemoryLinkLabel { get; set; } = "";
    public string MemoryHref { get; set; } = "";
}
