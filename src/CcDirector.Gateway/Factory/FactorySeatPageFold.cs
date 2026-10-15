using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Briefs;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.Running;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// ONE SEAT'S PAGE, FOLDED ONCE (Factory Seats mission, phase 1; critical rule 7). Every seat of a factory has a page
/// of its own, whether or not anything is scheduled for it.
///
/// Why it exists. Until 13 October 2026 the only way into a seat on the Factories screen was the Edit schedule button
/// of its schedule. The owner took the schedule off a factory's boss seat that day, and the screen lost the seat: "It
/// seems that our UI has entangled the scheduling with the agent itself." So this page is built from the
/// REGISTRATION, and a schedule is one of the things it shows rather than the way in.
///
/// Its panels, in order: the head (who the seat is and how it starts), what the seat does (its brief), how it starts
/// (its schedules and triggers, or that it is talk only), the factory's goal and number, and what it has left behind.
/// Pure: it reads what it is handed and returns finished display objects, so every sentence is proven by a unit test.
/// </summary>
public static class FactorySeatPageFold
{
    /// <summary>How many of the seat's newest activity lines the page shows.</summary>
    public const int RecentLines = 15;

    public const string TakeOffLabel = "Take off this seat";

    public const string DefinitionLabel = "Open the Definition tab";

    /// <summary>Why a defined factory's schedules offer no edit and no take-off, and where they change instead.</summary>
    public static string DefinedText(RegisteredFactoryDto factory) =>
        $"{factory.Title} has a definition, and this seat's schedules come from it. To change or remove one, change the " +
        "seat's start in the definition's folder, import it on the Definition tab, and publish it.";

    /// <summary>The factory page's Definition tab.</summary>
    public static string DefinitionHref(string factory) => FactoriesScreenFold.PageHref(factory) + "/definition";

    public const string TalkOnlyText = "Talk only. Nothing is scheduled for this seat. You run it by talking to it.";

    /// <param name="factory">The registered factory.</param>
    /// <param name="seat">One of <paramref name="factory"/>'s seats.</param>
    /// <param name="input">What the Factories screen reads, over the last 7 days, with <c>Talks</c> holding THIS
    /// seat's newest talk rather than the factory's.</param>
    /// <param name="memoryNotes">How many notes the factory's memory holds now.</param>
    /// <param name="brief">How the seat's brief stands on the Gateway: its newest version and its published one.
    /// <see cref="FactoryBriefStanding.None"/> for a seat whose brief is still a file.</param>
    /// <param name="briefDefinition">For a DEFINED factory, the line that says the brief is part of its definition
    /// (<see cref="FactoryBriefPanelFold.DefinitionLine"/>), and <paramref name="brief"/> is then the brief as the
    /// definition holds it. Null for every other factory.</param>
    public static FactorySeatPageViewDto Page(RegisteredFactoryDto factory, RegisteredFactorySeatDto seat,
        FactoriesScreenInputs input, int memoryNotes, FactoryBriefStanding brief, string? briefDefinition = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(seat);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegative(memoryNotes);
        ArgumentNullException.ThrowIfNull(brief);
        var a = input.Activity;
        var isBoss = FactoryTalkSeed.IsBoss(factory, seat);
        // The boss has no name of its own (the owner, 8 October 2026): its page says Boss.
        var name = isBoss ? FactoriesScreenFold.BossRoleWord : seat.Name;
        var schedules = FactoriesScreenFold.SchedulesOf(seat, input.Schedules);
        var named = seat.Schedules.Distinct(StringComparer.Ordinal).ToList();
        var triggers = a.Triggers
            .Where(t => FactoriesScreenFold.SameId(t.Factory, factory.Factory) && FactoriesScreenFold.SameId(t.FactoryAgent, seat.Id))
            .ToList();
        var (startsWord, startsTone) = StartsChip(named.Count, schedules, triggers);
        input.LatestGoalNumbers.TryGetValue(factory.Factory, out var number);

        return new FactorySeatPageViewDto
        {
            FactoryId = factory.Factory,
            FactoryTitle = factory.Title,
            SeatId = seat.Id,
            Crumb = $"Factories / {factory.Title} / Seats / {name}",
            CrumbHrefs = new()
            {
                FactoriesScreenFold.ListHref,
                FactoriesScreenFold.PageHref(factory.Factory),
                FactoriesScreenFold.SeatsHref(factory.Factory),
            },
            Name = name,
            Role = FactoriesScreenFold.SeatWord(seat),
            ComputerText = $"runs on {seat.Computer}",
            StartsWord = startsWord,
            StartsTone = startsTone,
            Talk = new FactoryTalkDto
            {
                Label = isBoss ? FactoriesScreenFold.TalkToTheBoss : $"Talk to {seat.Name}",
                BusyLabel = $"Starting the talk with {(isBoss ? "the boss" : seat.Name)}...",
                FactoryId = factory.Factory,
                SeatId = seat.Id,
            },
            Brief = FactoryBriefPanelFold.Panel(factory, seat, brief, a.Zone, a.NowUtc, briefDefinition),
            Starts = Starts(factory, seat, name, named, schedules, triggers, input, defined: briefDefinition is not null),
            Goal = FactoriesScreenFold.GoalCard(factory),
            GoalNumber = FactoriesScreenFold.GoalNumberCard(number, factory, a.Zone, a.NowUtc),
            LeftBehind = LeftBehind(factory, seat, named.Count, schedules, input, memoryNotes),
            TruncatedText = FactoriesScreenFold.Truncated(a),
        };
    }

    /// <summary>
    /// The chip in the head: how the seat starts. A seat that names no schedule and has no trigger is "Talk only" -
    /// never "Paused", which is a seat whose schedules or triggers exist and are all switched off.
    /// </summary>
    internal static (string Word, string Tone) StartsChip(int namedSchedules, IReadOnlyList<CronJobDto> schedules,
        IReadOnlyList<FactoryTriggerFacts> triggers)
    {
        if (namedSchedules == 0 && triggers.Count == 0) return (FactoriesScreenFold.SeatTalkOnly, FactoryTone.Idle);
        var scheduled = schedules.Any(j => j.Enabled);
        var triggered = triggers.Any(t => !t.Paused);
        if (scheduled && triggered) return ("Scheduled and triggered", FactoryTone.Ok);
        if (scheduled) return ("Scheduled", FactoryTone.Ok);
        if (triggered) return ("Triggered", FactoryTone.Ok);
        // Every schedule it names is gone from the Gateway and no trigger starts it: nothing is paused, something is missing.
        if (schedules.Count == 0 && triggers.Count == 0) return ("Schedule missing", FactoryTone.Amber);
        return ("Paused", FactoryTone.Paused);
    }

    /// <param name="defined">True for a factory with a published definition (the seat page is then handed the brief's
    /// definition line). Its schedules come from the definition, so the Gateway refuses an edit of one and a take-off
    /// (phase 4); the page offers neither, and says where they change instead.</param>
    private static FactorySeatStartsDto Starts(RegisteredFactoryDto f, RegisteredFactorySeatDto seat, string name,
        IReadOnlyList<string> named, IReadOnlyList<CronJobDto> schedules, IReadOnlyList<FactoryTriggerFacts> triggers,
        FactoriesScreenInputs input, bool defined)
    {
        var a = input.Activity;
        var shifts = FactoriesScreenFold.ShiftsBySchedule(seat, input);
        // Taking the last schedule off a seat with no trigger leaves it talk only, and the confirm says so.
        var found = named.Count(id => schedules.Any(j => j.Id == id));
        return new FactorySeatStartsDto
        {
            Heading = "How it starts",
            TalkOnlyText = named.Count == 0 && triggers.Count == 0 ? TalkOnlyText : null,
            Schedules = named
                .Select(id => schedules.FirstOrDefault(j => j.Id == id) is { } job
                    ? new FactorySeatPageScheduleDto
                    {
                        JobId = job.Id,
                        Name = job.Name,
                        WhenText = FactoryScheduleText.Describe(job, a.Zone),
                        ShiftText = ScheduleShifts.Text(shifts[id]),
                        PausedText = job.Enabled ? null : "Paused",
                        EditLabel = defined ? null : "Edit schedule",
                        Paused = !job.Enabled,
                        // A one-off that has run has nothing to pause or resume.
                        ToggleLabel = CronSchedule.LifecycleOf(job) == CronLifecycle.Spent
                            ? null
                            : CronSchedule.ToggleLabelOf(job.Enabled),
                        // A schedule the factory's archive switched off is what its Restore switches back on, so it
                        // is not taken off while the factory is archived (the route refuses it too).
                        // A defined factory's schedule is taken off by publishing a definition without its start.
                        Unlink = defined || (f.ArchivedAtUtc is not null && f.ArchivedSchedules.Contains(job.Id, StringComparer.Ordinal))
                            ? null
                            : UnlinkAction(f, name, job, leavesTalkOnly: found == 1 && triggers.Count == 0),
                    }
                    : new FactorySeatPageScheduleDto
                    {
                        JobId = id,
                        WhenText = $"Schedule {id} is missing",
                        ShiftText = ScheduleShifts.Text(Array.Empty<string>()),
                        MissingText = "This seat names a schedule the Gateway does not have, so nothing runs from it.",
                    })
                .ToList(),
            Triggers = triggers.Select(t => FactoryAgentsFold.WokenBy(t, a.Window, a.Zone)).ToList(),
            TriggersPause = FactoryAgentsFold.PauseFor(triggers, $"the triggers of {name}", "triggers"),
            DefinedText = defined ? DefinedText(f) : null,
            DefinitionLabel = defined ? DefinitionLabel : null,
            DefinitionHref = defined ? DefinitionHref(f.Factory) : null,
        };
    }

    /// <summary>
    /// "Take off this seat" and its confirm. Every sentence says what will be true afterwards, because the act is not
    /// a pause: a schedule that is switched on KEEPS RUNNING, as a plain schedule in no factory.
    /// </summary>
    internal static FactorySeatUnlinkDto UnlinkAction(RegisteredFactoryDto f, string seatName, CronJobDto job, bool leavesTalkOnly)
    {
        var lines = new List<string>
        {
            $"'{job.Name}' stops being a schedule of {seatName}. The sessions it starts no longer belong to {f.Title}.",
            job.Enabled
                ? "It stays switched on and keeps running at its times, as a plain schedule in no factory. Pause it first if it should stop."
                : "It stays switched off, in no factory.",
            "It is kept, not deleted. It is listed on the Schedule page, and cc-devthrottle schedule link puts it back on a seat.",
        };
        if (leavesTalkOnly) lines.Add($"{seatName} will then be talk only.");
        return new FactorySeatUnlinkDto
        {
            JobId = job.Id,
            Label = TakeOffLabel,
            BusyLabel = "Taking it off...",
            ConfirmTitle = $"Take this schedule off {seatName}?",
            ConfirmLines = lines,
            ConfirmLabel = "Take it off",
        };
    }

    /// <summary>
    /// What <c>POST /cron/jobs/{id}/unlink</c> answers, in words: which seat the schedule was taken off, and that it
    /// is still there, switched as it was.
    /// </summary>
    internal static string UnlinkedText(CronJobDto before, RegisteredFactoryDto? factory)
    {
        var title = factory?.Title ?? before.Factory!;
        var seat = factory?.Seats.FirstOrDefault(s => string.Equals(s.Id, before.Seat, StringComparison.Ordinal));
        var from = seat is null
            ? title
            : $"{(FactoryTalkSeed.IsBoss(factory!, seat) ? "the boss" : seat.Name)} of {title}";
        return before.Enabled
            ? $"Taken off {from}. '{before.Name}' is still switched on and keeps running at its times, as a plain schedule in no factory."
            : $"Taken off {from}. '{before.Name}' is still switched off, and is now in no factory.";
    }

    private static FactorySeatLeftBehindDto LeftBehind(RegisteredFactoryDto f, RegisteredFactorySeatDto seat, int namedSchedules,
        IReadOnlyList<CronJobDto> schedules, FactoriesScreenInputs input, int memoryNotes)
    {
        var a = input.Activity;
        var clock = FactoriesScreenFold.SeatClock(schedules, a.Zone);
        var rows = a.WindowRows
            .Where(r => FactoriesScreenFold.SameId(r.Factory, f.Factory) && FactoriesScreenFold.SameId(r.FactoryAgent, seat.Id))
            .OrderBy(r => r.OccurredUtc)
            .ToList();
        var lastTalk = input.Talks
            .Where(r => FactoriesScreenFold.SameId(r.Factory, f.Factory) && FactoriesScreenFold.SameId(r.FactoryAgent, seat.Id)
                        && r.Outcome == FactoryActivityOutcome.Talked)
            .OrderByDescending(r => r.OccurredUtc)
            .FirstOrDefault();
        var (lastRunText, lastRunTone) = FactoriesScreenFold.LastRun(f, seat, schedules, a, clock);
        // A seat nothing is scheduled for has no scheduled runs to judge, so it carries no "closes itself" marker.
        var closing = namedSchedules == 0 ? null : FactoriesScreenFold.SeatClosing(seat, input);
        var lines = FactoryAgentsFold.Collapse(rows, a.Window, a.Zone, FactoryAgentsFold.AllCorrections(a), rows);

        return new FactorySeatLeftBehindDto
        {
            Heading = "What it has left behind",
            LastTalkLabel = "Last talk",
            LastTalkText = lastTalk is null
                ? "None yet."
                : $"Talked with you, {clock.When(lastTalk.OccurredUtc, a.NowUtc, capital: false)} - {lastTalk.What}",
            LastRunLabel = "Last run",
            LastRunText = lastRunText,
            LastRunTone = lastRunTone,
            ClosingText = closing?.Text,
            ClosingTone = closing is null ? null : FactoriesScreenFold.ClosingTone(closing.Verdict),
            NumbersTitle = "Last 7 days",
            Numbers = FactoryAgentsFold.Last7Days(rows),
            RecentTitle = "Recent activity",
            Recent = lines.Count > RecentLines ? lines.Skip(lines.Count - RecentLines).ToList() : lines,
            RecentEmptyText = lines.Count == 0 ? "Nothing recorded in the last 7 days." : null,
            AllLabel = "All activity of this seat",
            AllHref = $"{FactoriesScreenFold.ListHref}?tab=activity&factory={Uri.EscapeDataString(f.Factory)}&agent={Uri.EscapeDataString(seat.Id)}",
            MemoryLabel = "Memory",
            MemoryText = (memoryNotes == 0
                             ? "No notes in the factory's memory yet."
                             : $"{FactoriesScreenFold.Count(memoryNotes, "note")} in the factory's memory.")
                         + " Memory belongs to the whole factory, not to one seat.",
            MemoryLinkLabel = "Open Memory",
            MemoryHref = FactoriesScreenFold.MemoryHref(f.Factory),
        };
    }
}
