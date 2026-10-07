using System.Globalization;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// Everything one Factories screen view reads, taken once per request.
/// </summary>
/// <param name="Registry">Every registered factory in the account - the list is these and only these.</param>
/// <param name="Activity">The record rows, waiting items and triggers, as the Factory Agents views read them. Its
/// window must reach back at least <see cref="FactoriesScreenFold.FailingWindow"/>.</param>
/// <param name="Schedules">Every schedule in the account; a seat's are the ones its registration names.</param>
/// <param name="LatestGoalNumbers">The newest goal number of each factory, by factory id.</param>
/// <param name="Talks">The newest "talked" rows of the factory or factories in view, whatever their age.</param>
public sealed record FactoriesScreenInputs(
    IReadOnlyList<RegisteredFactoryDto> Registry,
    FactoryFoldInputs Activity,
    IReadOnlyList<CronJobDto> Schedules,
    IReadOnlyDictionary<string, GoalNumberDto> LatestGoalNumbers,
    IReadOnlyList<FactoryActivityDto> Talks);

/// <summary>
/// THE FACTORIES SCREEN, FOLDED ONCE (Factories screen mission, phase B; critical rule 7). The list of factories, one
/// factory's page and its Seats tab are built here from the factory registry, the activity record and the schedules,
/// and the Cockpit renders the words, tones and order it is handed.
///
/// A factory's status is exactly one of four words, worst first (decision 5 of the plan):
///   FAILING   - something of it failed in the last 24 hours and is not over: ANY "failed" row of the factory,
///               whoever wrote it, that is neither marked handled (a row corrects it) nor followed by a later
///               successful row of the same seat and subject (<see cref="IsOver"/>; round 2, PLAN.md); or a seat's
///               schedule whose last firing failed (the engine records that as "not-started", or a work list that
///               could not run).
///   NEEDS YOU - something it asked or escalated is waiting on the owner.
///   PAUSED    - nothing runs its seats on its own: no seat schedule is on and no seat trigger is live. A factory
///               with no schedule at all is PAUSED too, and says "Nothing scheduled" rather than "switched off".
///   RUNNING   - otherwise.
/// (The two build rulings of 2026-10-06, PLAN.md "Decisions added during the build".)
///
/// Every status but RUNNING carries a one-line reason shown under the word, and a link to the items it is about on
/// the factory's page (round 2, mandate item 1). The factory's head is the registry's <c>ceoSeat</c>, whatever its
/// title: the page says that seat's own role ("CFO Ruth Calder").
///
/// Only registry seats are seats. A row written by a session that is not a seat ("Owner Session") counts toward
/// what is waiting on the owner and toward FAILING - both are the factory's - but it never becomes a row on the
/// Seats tab.
/// </summary>
public static class FactoriesScreenFold
{
    public const string StatusFailing = "FAILING";
    public const string StatusNeedsYou = "NEEDS YOU";
    public const string StatusPaused = "PAUSED";
    public const string StatusRunning = "RUNNING";

    /// <summary>How far back a failed run makes a factory FAILING.</summary>
    public static readonly TimeSpan FailingWindow = TimeSpan.FromHours(24);

    /// <summary>How many of the CEO's lines the page shows.</summary>
    public const int CeoLines = 3;

    /// <summary>A schedule's last status when the firing could not start its session.</summary>
    public const string ScheduleNotStarted = "not-started";

    /// <summary>The last statuses the schedule engine writes for a firing that failed. It never writes "failed": a
    /// session that could not start is "not-started", and a work list that could not run says why.</summary>
    public static readonly IReadOnlySet<string> ScheduleFailedStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        ScheduleNotStarted, "worklist-no-list", "worklist-no-director", "worklist-unknown",
    };

    public const string ChangeComing = "change - coming";

    public const string DocumentsText =
        "A factory's documents - its briefs, its goal file, its rules - live in its folder on its computer, not on the " +
        "Gateway yet, so there is nothing to show here. They will appear here when the definitions move onto the Gateway.";

    // ---------------------------------------------------------------------------------------------------------
    // The list

    public static FactoriesListViewDto List(FactoriesScreenInputs input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var open = OpenWaiting(input.Activity);
        var duplicateCeoNames = DuplicateCeoNames(input.Registry);

        var rows = input.Registry.Select(f =>
            {
                var status = Status(f, input, open);
                var talk = CeoTalk(f, duplicateCeoNames);
                return (Rank: status.Rank, Row: new FactoryListRowDto
                {
                    Id = f.Factory,
                    Title = f.Title,
                    StatusWord = status.Word,
                    StatusTone = status.Tone,
                    StatusReason = status.Reason,
                    StatusLine = status.Line,
                    StatusHref = status.Href,
                    WaitingText = WaitingText(open.Where(r => SameId(r.Factory, f.Factory)).ToList()),
                    WaitingHref = open.Any(r => SameId(r.Factory, f.Factory)) ? WaitingHref(f.Factory) : null,
                    Href = PageHref(f.Factory),
                    Talk = talk,
                    NoCeoText = talk is null ? NoHead : null,
                });
            })
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Row.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Row.Id, StringComparer.Ordinal)
            .Select(x => x.Row)
            .ToList();

        return new FactoriesListViewDto
        {
            Title = "Factories",
            Subtitle = "Each factory runs one business or one job on its own, toward the goal you set for it.",
            Tabs = ListTabs(),
            Columns = new() { "Factory", "Waiting on you", "Status" },
            Rows = rows,
            FooterText = rows.Count == 0 ? null : "Worst first: failing, then needs you, then paused, then running.",
            EmptyText = rows.Count == 0
                ? "No factory is registered yet. A factory appears here when it is registered with cc-devthrottle factory register."
                : null,
            TruncatedText = Truncated(input.Activity),
        };
    }

    public static List<FactoryTabDto> ListTabs() => new()
    {
        new() { Key = "factories", Label = "Factories" },
        new() { Key = "activity", Label = "Activity" },
        new() { Key = "reports", Label = "Reports" },
    };

    // ---------------------------------------------------------------------------------------------------------
    // One factory's page

    public static FactoryPageViewDto Page(RegisteredFactoryDto factory, FactoriesScreenInputs input)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(input);
        var zone = input.Activity.Zone;
        var now = input.Activity.NowUtc;
        var open = OpenWaiting(input.Activity).Where(r => SameId(r.Factory, factory.Factory)).ToList();
        var status = Status(factory, input, open);
        var ceo = Ceo(factory);
        input.LatestGoalNumbers.TryGetValue(factory.Factory, out var number);

        return new FactoryPageViewDto
        {
            Id = factory.Factory,
            Title = factory.Title,
            Crumb = $"Factories / {factory.Title}",
            CrumbHref = ListHref,
            StatusWord = status.Word,
            StatusTone = status.Tone,
            StatusReason = status.Reason,
            StatusLine = status.Line,
            StatusHref = status.Href,
            CeoText = ceo is null ? NoHead : HeadText(ceo),
            SeatCountText = Count(factory.Seats.Count, "seat"),
            ComputerText = $"runs on {factory.Computer}",
            ComputerChangeText = ChangeComing,
            Talk = CeoTalk(factory, DuplicateCeoNames(input.Registry)),
            Tabs = PageTabs(factory.Seats.Count),
            Goal = GoalCard(factory),
            GoalNumber = GoalNumberCard(number, factory, zone, now),
            Failures = FailuresCard(factory, input),
            Waiting = new FactoryPageWaitingDto
            {
                Heading = "Waiting on you",
                Items = open.OrderBy(r => r.Outcome == FactoryActivityOutcome.Escalated ? 0 : 1).ThenBy(r => r.OccurredUtc)
                    .Select(r => FactoryAgentsFold.WaitingItem(r, zone)).ToList(),
                EmptyText = open.Count == 0 ? "Nothing is waiting on you." : null,
            },
            CeoLatest = CeoLatest(factory, ceo, input),
            LastTalk = LastTalk(factory, input.Talks, zone, now),
            DocumentsText = DocumentsText,
            TruncatedText = Truncated(input.Activity),
        };
    }

    public static List<FactoryTabDto> PageTabs(int seats) => new()
    {
        new() { Key = "overview", Label = "Overview" },
        new() { Key = "seats", Label = $"Seats ({seats})" },
        new() { Key = "activity", Label = "Activity" },
        new() { Key = "reports", Label = "Reports" },
        new() { Key = "memory", Label = "Memory" },
        new() { Key = "documents", Label = "Documents" },
    };

    private static FactoryGoalCardDto GoalCard(RegisteredFactoryDto f)
    {
        if (string.IsNullOrWhiteSpace(f.GoalText))
            return new FactoryGoalCardDto { Heading = "Goal", EmptyText = "No goal set yet" };
        var approved = f.GoalApprovedOn is { } day
                       && DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? $" Approved {d.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}."
            : "";
        return new FactoryGoalCardDto { Heading = "Goal", Text = f.GoalText.Trim(), Note = "Only you change the goal." + approved };
    }

    private static FactoryGoalNumberCardDto GoalNumberCard(GoalNumberDto? n, RegisteredFactoryDto f, TimeZoneInfo zone, DateTime now)
    {
        if (n is null)
            return new FactoryGoalNumberCardDto { Heading = "Goal number", EmptyText = "No number posted yet" };
        var asOf = DateOnly.TryParseExact(n.AsOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
            : n.AsOf;
        return new FactoryGoalNumberCardDto
        {
            Heading = $"Goal number - posted by {SeatName(f, n.PostedBy)}, {When(n.PostedAtUtc, zone, now, capital: false)}",
            ValueText = $"{Capitalize(n.Unit)}: {n.Value}",
            AsOfText = $"As of {asOf}",
            LinkHref = n.Link,
            LinkLabel = "How it is measured",
        };
    }

    private static FactoryCeoLatestDto CeoLatest(RegisteredFactoryDto f, RegisteredFactorySeatDto? ceo, FactoriesScreenInputs input)
    {
        var a = input.Activity;
        if (ceo is null)
            return new FactoryCeoLatestDto { Heading = "Latest from the head", EmptyText = "This factory has no head named." };
        var dto = new FactoryCeoLatestDto { Heading = $"Latest from the {HeadRole(ceo)}" };
        var clock = SeatClock(SchedulesOf(ceo, input.Schedules), a.Zone);
        if (clock.ZoneName is not null) dto.Heading += $" ({clock.ZoneName} time)";
        var lines = a.WindowRows
            .Where(r => SameId(r.Factory, f.Factory) && SameId(r.FactoryAgent, ceo.Id)
                        && r.Outcome is not FactoryActivityOutcome.Started and not FactoryActivityOutcome.NothingToDo
                            and not FactoryActivityOutcome.Paused and not FactoryActivityOutcome.Skipped)
            .OrderByDescending(r => r.OccurredUtc)
            .Take(CeoLines)
            .Select(r => $"{clock.When(r.OccurredUtc, a.NowUtc, capital: true, named: false)} - {r.What}")
            .ToList();
        dto.Lines = lines;
        dto.EmptyText = lines.Count == 0 ? $"Nothing from {ceo.Name} {WindowPhrase(a.Window)}." : null;
        dto.AllLabel = "All reports";
        dto.AllHref = $"{ListHref}?tab=activity&factory={Uri.EscapeDataString(f.Factory)}&agent={Uri.EscapeDataString(ceo.Id)}";
        return dto;
    }

    private static FactoryLastTalkDto LastTalk(RegisteredFactoryDto f, IReadOnlyList<FactoryActivityDto> talks, TimeZoneInfo zone, DateTime now)
    {
        var last = talks.Where(r => SameId(r.Factory, f.Factory) && r.Outcome == FactoryActivityOutcome.Talked)
            .OrderByDescending(r => r.OccurredUtc).FirstOrDefault();
        return new FactoryLastTalkDto
        {
            Heading = "Last talk with you",
            Text = last is null ? "None yet." : $"Talked with you, {When(last.OccurredUtc, zone, now, capital: false)} - {last.What}",
        };
    }

    // ---------------------------------------------------------------------------------------------------------
    // The Seats tab

    public static FactorySeatsViewDto Seats(RegisteredFactoryDto factory, FactoriesScreenInputs input)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(input);
        var rows = factory.Seats
            .OrderBy(s => SameId(s.Id, factory.CeoSeat ?? "") ? 0 : 1)
            .Select(s => SeatRow(factory, s, input))
            .ToList();
        return new FactorySeatsViewDto
        {
            FactoryId = factory.Factory,
            Title = factory.Title,
            Crumb = $"Factories / {factory.Title} / Seats",
            Columns = new() { "Seat", "When it runs", "Last run", "Computer" },
            Rows = rows,
            Note = "Only seats the CEO hired are listed. Sessions that only wrote activity rows are not seats and do not appear.",
        };
    }

    private static FactorySeatRowDto SeatRow(RegisteredFactoryDto f, RegisteredFactorySeatDto seat, FactoriesScreenInputs input)
    {
        var a = input.Activity;
        var schedules = SchedulesOf(seat, input.Schedules);
        var clock = SeatClock(schedules, a.Zone);
        var (lastText, lastTone) = LastRun(f, seat, schedules, a, clock);
        return new FactorySeatRowDto
        {
            SeatId = seat.Id,
            Name = seat.Name,
            Role = seat.Role,
            WhenText = seat.Schedules.Count == 0
                ? "Not scheduled"
                : string.Join("; ", seat.Schedules.Select(id =>
                    schedules.FirstOrDefault(j => j.Id == id) is { } job
                        ? FactoryScheduleText.Describe(job, a.Zone)
                        : $"Schedule {id} is missing")),
            LastRunText = lastText,
            LastRunTone = lastTone,
            ComputerText = seat.Computer,
            ComputerChangeText = ChangeComing,
            Talk = new FactoryTalkDto
            {
                Label = "Talk",
                BusyLabel = $"Starting the talk with {seat.Name}...",
                FactoryId = f.Factory,
                SeatId = seat.Id,
            },
        };
    }

    /// <summary>
    /// A seat's last run and how it ended: the latest of its schedules' firings and its own "started" rows, then
    /// what that session's rows came to. "Not run yet" when neither has anything.
    /// </summary>
    private static (string Text, string Tone) LastRun(RegisteredFactoryDto f, RegisteredFactorySeatDto seat,
        IReadOnlyList<CronJobDto> schedules, FactoryFoldInputs a, Clock clock)
    {
        var rows = a.WindowRows.Where(r => SameId(r.Factory, f.Factory) && SameId(r.FactoryAgent, seat.Id)).ToList();
        var started = rows.Where(r => r.Outcome == FactoryActivityOutcome.Started).OrderByDescending(r => r.OccurredUtc).FirstOrDefault();
        var fired = schedules.Where(j => j.LastFiredUtc is not null).OrderByDescending(j => j.LastFiredUtc).FirstOrDefault();

        if (started is null && fired is null) return ("Not run yet", FactoryTone.Grey);

        // A firing newer than the newest start the record holds: that firing is the last run.
        if (fired is not null && (started is null || fired.LastFiredUtc!.Value > started.OccurredUtc.AddMinutes(5)))
        {
            var when = clock.When(fired.LastFiredUtc!.Value, a.NowUtc, capital: true);
            return fired.LastStatus is { } st && ScheduleFailedStatuses.Contains(st)
                ? ($"{when} - did not start", FactoryTone.Red)
                : ($"{when} - started", FactoryTone.Blue);
        }

        var at = clock.When(started!.OccurredUtc, a.NowUtc, capital: true);
        var after = started.SessionId is null
            ? new List<FactoryActivityDto>()
            : rows.Where(r => r.SessionId == started.SessionId && r.Outcome != FactoryActivityOutcome.Started).ToList();
        var (word, tone) = Ending(after);
        return ($"{at} - {word}", tone);
    }

    // How a run ended, from the rows its session wrote: a failure outranks a question, a question outranks success.
    private static (string Word, string Tone) Ending(IReadOnlyList<FactoryActivityDto> rows)
    {
        if (rows.Any(r => r.Outcome == FactoryActivityOutcome.Failed)) return ("failed", FactoryTone.Red);
        if (rows.Any(r => r.Outcome is FactoryActivityOutcome.Asked or FactoryActivityOutcome.Escalated)) return ("needs you", FactoryTone.Amber);
        if (rows.Any(r => r.Outcome == FactoryActivityOutcome.Done)) return ("succeeded", FactoryTone.Ok);
        if (rows.Any(r => r.Outcome == FactoryActivityOutcome.NothingToDo)) return ("nothing to do", FactoryTone.Grey);
        if (rows.Count > 0) return ("ran", FactoryTone.Neutral);
        return ("started", FactoryTone.Blue);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Status

    /// <summary>A factory's status: its rank (worst first), its word and tone, the full sentence of why (a
    /// tooltip), the one short line shown under the word (null for RUNNING), and where clicking the word goes
    /// (null for RUNNING).</summary>
    internal readonly record struct FactoryStatus(int Rank, string Word, string Tone, string Reason, string? Line, string? Href);

    /// <summary>How long a quoted row's text may run in a status line before it is cut with "...".</summary>
    public const int LineWhatChars = 90;

    public const string NothingScheduled = "Nothing scheduled";

    internal static FactoryStatus Status(RegisteredFactoryDto f, FactoriesScreenInputs input, IReadOnlyList<FactoryActivityDto> open)
    {
        var a = input.Activity;
        var seatIds = f.Seats.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var failures = OpenFailures(f, a);
        if (failures.Count > 0)
        {
            var newest = failures[0];
            var who = SeatName(f, newest.FactoryAgent);
            var when = SeatClockOf(f, newest.FactoryAgent, input.Schedules, a.Zone).When(newest.OccurredUtc, a.NowUtc, capital: false);
            var seats = failures.Select(r => r.FactoryAgent.Trim().ToLowerInvariant()).Distinct().Count();
            string Say(string what) => failures.Count == 1
                ? $"{who} failed {when}: {what}"
                : seats == 1
                    ? $"{who}: {failures.Count} failures, newest {when}: {what}"
                    : $"{failures.Count} failures from {seats} seats, newest {who} {when}: {what}";
            return new(0, StatusFailing, FactoryTone.Red, Say(newest.What), Say(Shorten(newest.What)), FailingHref(f.Factory));
        }

        var notStarted = NotStarted(f, input);
        if (notStarted.Count > 0)
        {
            var text = NotStartedText(notStarted[0], a);
            return new(0, StatusFailing, FactoryTone.Red, text, text, FailingHref(f.Factory));
        }

        var waiting = open.Where(r => SameId(r.Factory, f.Factory)).OrderByDescending(r => r.OccurredUtc).ToList();
        if (waiting.Count > 0)
        {
            var newest = waiting[0];
            var oldest = waiting[^1];
            var whenNewest = When(newest.OccurredUtc, a.Zone, a.NowUtc, capital: false);
            var head = waiting.Count == 1
                ? $"{SeatName(f, newest.FactoryAgent)}, {whenNewest}"
                : $"{WaitingText(waiting)} since {When(oldest.OccurredUtc, a.Zone, a.NowUtc, capital: false)}; newest {SeatName(f, newest.FactoryAgent)}, {whenNewest}";
            return new(1, StatusNeedsYou, FactoryTone.Amber, $"{head}: {newest.What}", $"{head}: {Shorten(newest.What)}", WaitingHref(f.Factory));
        }

        var seatSchedules = f.Seats.SelectMany(s => SchedulesOf(s, input.Schedules)).ToList();
        var triggers = a.Triggers.Where(t => SameId(t.Factory, f.Factory) && seatIds.Contains(t.FactoryAgent)).ToList();
        if (!seatSchedules.Any(j => j.Enabled) && !triggers.Any(t => !t.Paused))
        {
            var line = PausedText(f, seatSchedules.Select(j => j.Id).Distinct(StringComparer.Ordinal).Count(), triggers.Count, input.Schedules);
            return new(2, StatusPaused, FactoryTone.Paused, line, line, $"{PageHref(f.Factory)}/seats");
        }

        return new(3, StatusRunning, FactoryTone.Ok, "Nothing failed and nothing is waiting on you.", null, null);
    }

    /// <summary>
    /// Why a PAUSED factory is paused (round 2, mandate item 5). "Nothing scheduled" when no seat names a schedule
    /// the Gateway has and no trigger runs one - nothing was ever set to run it - which is a different thing from
    /// schedules the owner switched off, so that says how many: "3 schedules switched off", "1 schedule switched
    /// off, 1 trigger paused". A seat that names a schedule the Gateway no longer has says so, rather than reading as
    /// switched off or as never scheduled.
    /// </summary>
    internal static string PausedText(RegisteredFactoryDto f, int offSchedules, int pausedTriggers, IReadOnlyList<CronJobDto> all)
    {
        var known = all.Select(j => j.Id).ToHashSet(StringComparer.Ordinal);
        var missing = f.Seats.SelectMany(s => s.Schedules).Distinct(StringComparer.Ordinal).Count(id => !known.Contains(id));
        var parts = new List<string>();
        if (offSchedules > 0) parts.Add($"{Count(offSchedules, "schedule")} switched off");
        if (pausedTriggers > 0) parts.Add($"{Count(pausedTriggers, "trigger")} paused");
        if (missing > 0) parts.Add(missing == 1 ? "1 named schedule no longer exists" : $"{missing} named schedules no longer exist");
        return parts.Count == 0 ? NothingScheduled : string.Join(", ", parts);
    }

    /// <summary>
    /// The failed rows that still make the factory FAILING, newest first: a failed row in the last 24 hours counts
    /// until it is over (<see cref="IsOver"/>).
    /// </summary>
    internal static List<FactoryActivityDto> OpenFailures(RegisteredFactoryDto f, FactoryFoldInputs a)
    {
        var since = a.NowUtc - FailingWindow;
        var corrected = FactoryAgentsFold.AllCorrections(a).Where(r => r.CorrectsId is not null).Select(r => r.CorrectsId!.Value).ToHashSet();
        var mine = a.WindowRows.Where(r => SameId(r.Factory, f.Factory)).ToList();
        return mine
            .Where(r => r.Outcome == FactoryActivityOutcome.Failed && r.OccurredUtc >= since && !IsOver(r, mine, corrected))
            .GroupBy(r => r.Id).Select(g => g.First())
            .OrderByDescending(r => r.OccurredUtc)
            .ToList();
    }

    /// <summary>
    /// THE CLEARING RULE (round 2, mandate item 2; written into PLAN.md "Round 2"). A failed row is over when
    ///   - the owner marked it handled: a row corrects it (a NEW row, the same mechanism as an escalation's
    ///     "I have handled it"; the failed row is never edited), or
    ///   - a LATER successful row exists from the same seat about the same subject: same factory, the same factory
    ///     agent (trimmed, ignoring case), the same subject (trimmed, ignoring case; a row with no subject matches only
    ///     a row with no subject), an outcome of "done" or "nothing-to-do", a later time, and not itself a correction
    ///     of another row (a correction says something about an older row, not that the work succeeded).
    /// On the live record that is, for example, Sender's "keep.page (failed): ... answers 404" for All Types Fence and
    /// Deck at 12:02, followed by Sender's "keep.recorded (done)" for the same business at 12:11; and a trigger check
    /// that failed, followed by the same trigger's "nothing to do" check. "started" rows (a step it intends to take)
    /// and "escalated" rows never clear a failure.
    /// </summary>
    internal static bool IsOver(FactoryActivityDto failed, IReadOnlyList<FactoryActivityDto> factoryRows, IReadOnlySet<Guid> corrected)
    {
        if (corrected.Contains(failed.Id)) return true;
        return factoryRows.Any(r => r.Id != failed.Id
                                    && r.CorrectsId is null
                                    && r.Outcome is FactoryActivityOutcome.Done or FactoryActivityOutcome.NothingToDo
                                    && r.OccurredUtc > failed.OccurredUtc
                                    && SameId(r.FactoryAgent?.Trim(), failed.FactoryAgent?.Trim())
                                    && SameSubject(r.Subject, failed.Subject));
    }

    private static bool SameSubject(string? a, string? b)
    {
        var x = string.IsNullOrWhiteSpace(a) ? null : a.Trim();
        var y = string.IsNullOrWhiteSpace(b) ? null : b.Trim();
        return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The seat schedules whose last firing in the last 24 hours could not start its run, newest first.
    /// These clear by themselves: the schedule's next good firing replaces its last status.</summary>
    private static List<(RegisteredFactorySeatDto Seat, CronJobDto Job)> NotStarted(RegisteredFactoryDto f, FactoriesScreenInputs input)
    {
        var since = input.Activity.NowUtc - FailingWindow;
        return f.Seats.SelectMany(s => SchedulesOf(s, input.Schedules).Select(j => (Seat: s, Job: j)))
            .Where(x => x.Job.LastStatus is { } st && ScheduleFailedStatuses.Contains(st) && x.Job.LastFiredUtc is { } t && t >= since)
            .OrderByDescending(x => x.Job.LastFiredUtc)
            .ToList();
    }

    private static string NotStartedText((RegisteredFactorySeatDto Seat, CronJobDto Job) x, FactoryFoldInputs a) =>
        $"The schedule for {x.Seat.Name} could not start its run {SeatClock(new[] { x.Job }, a.Zone).When(x.Job.LastFiredUtc!.Value, a.NowUtc, capital: false)}.";

    /// <summary>
    /// The failures card on a factory's page, where the FAILING word links to: every failure still counting, each
    /// with its text, which seat and when, its evidence, and "Handled". Null when nothing is failing, so a healthy
    /// factory's page carries no empty card.
    /// </summary>
    private static FactoryPageFailuresDto? FailuresCard(RegisteredFactoryDto f, FactoriesScreenInputs input)
    {
        var a = input.Activity;
        var rows = OpenFailures(f, a);
        var schedules = NotStarted(f, input);
        if (rows.Count == 0 && schedules.Count == 0) return null;
        var items = schedules.Select(x => new FactoryFailureItemDto
            {
                What = NotStartedText(x, a),
                By = $"{x.Seat.Name}'s schedule",
                Note = "This clears when the schedule next starts its run.",
            })
            .Concat(rows.Select(r => new FactoryFailureItemDto
            {
                Id = r.Id,
                Subject = r.Subject,
                What = r.What,
                By = $"{SeatName(f, r.FactoryAgent)}, {SeatClockOf(f, r.FactoryAgent, input.Schedules, a.Zone).When(r.OccurredUtc, a.NowUtc, capital: false)}",
                SessionId = r.SessionId,
                SessionLabel = FactoryAgentsFold.SessionLabel(r.SessionId),
                Link = r.Link,
                LinkLabel = r.Link is null ? null : "Open",
                HandledLabel = "Handled",
                HandledBusyLabel = "Marking it handled...",
            }))
            .ToList();
        return new FactoryPageFailuresDto
        {
            Heading = "Failing",
            Note = "A failure stops counting when the same seat later succeeds at the same thing, or when you mark it handled. "
                   + "Handled adds a row to the activity record; the failure itself is kept.",
            Items = items,
        };
    }

    /// <summary>
    /// The row "Handled" appends for a failure: a NEW row correcting it, the same mechanism as an escalation's
    /// "I have handled it". Refused unless the row is a failure of this factory nothing has corrected yet.
    /// </summary>
    public static AppendFactoryActivityRequest FailureHandledRow(string factory, FactoryActivityDto failed,
        IReadOnlyList<FactoryActivityDto> corrections, bool correctionsTruncated, string actor, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(failed);
        if (failed.Outcome != FactoryActivityOutcome.Failed || !SameId(failed.Factory, factory))
            throw new FactoryViewValidationException($"That row is not a failure of '{factory}'. Nothing was written.");
        return FactoryAgentsFold.CorrectingRow(failed, corrections, correctionsTruncated, actor, nowUtc, "failure");
    }

    private static string Shorten(string what)
    {
        var t = (what ?? "").Trim();
        return t.Length <= LineWhatChars ? t : t[..(LineWhatChars - 3)].TrimEnd() + "...";
    }

    /// <summary>"1 question", "2 decisions", "1 question, 1 decision", or "-". An asked row is a question, an
    /// escalation a decision.</summary>
    public static string WaitingText(IReadOnlyList<FactoryActivityDto> open)
    {
        var questions = open.Count(r => r.Outcome == FactoryActivityOutcome.Asked);
        var decisions = open.Count(r => r.Outcome == FactoryActivityOutcome.Escalated);
        var parts = new List<string>();
        if (questions > 0) parts.Add(Count(questions, "question"));
        if (decisions > 0) parts.Add(Count(decisions, "decision"));
        return parts.Count == 0 ? "-" : string.Join(", ", parts);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Shared

    public const string ListHref = "/factories";

    public static string PageHref(string factory) => $"{ListHref}/{Uri.EscapeDataString(factory)}";

    /// <summary>The failures card on the factory's page - where FAILING links to.</summary>
    public static string FailingHref(string factory) => $"{PageHref(factory)}#failing";

    /// <summary>The waiting items on the factory's page - where NEEDS YOU and the waiting count link to.</summary>
    public static string WaitingHref(string factory) => $"{PageHref(factory)}#waiting";

    /// <summary>What a factory with no head says where the head's Talk button would be.</summary>
    public const string NoHead = "No head named";

    /// <summary>"CFO Ruth Calder", "CEO Nora Hale": the head's own role, then its name.</summary>
    private static string HeadText(RegisteredFactorySeatDto head) => $"{HeadRole(head)} {head.Name}";

    private static string HeadRole(RegisteredFactorySeatDto head) =>
        string.IsNullOrWhiteSpace(head.Role) ? "head" : head.Role.Trim();

    /// <summary>
    /// The head's Talk button: "Talk to Ruth Calder", or "Talk to the CEO" (the head's own role) when another
    /// registered factory's head has the same name, so two buttons on one list never read alike. Null when the
    /// factory has no head.
    /// </summary>
    private static FactoryTalkDto? CeoTalk(RegisteredFactoryDto f, IReadOnlySet<string> duplicateNames)
    {
        var ceo = Ceo(f);
        if (ceo is null) return null;
        var who = duplicateNames.Contains(ceo.Name) ? $"the {HeadRole(ceo)}" : ceo.Name;
        return new FactoryTalkDto
        {
            Label = $"Talk to {who}",
            BusyLabel = $"Starting the talk with {who}...",
            FactoryId = f.Factory,
            SeatId = ceo.Id,
        };
    }

    private static IReadOnlySet<string> DuplicateCeoNames(IReadOnlyList<RegisteredFactoryDto> registry) =>
        registry.Select(Ceo).Where(c => c is not null).GroupBy(c => c!.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static RegisteredFactorySeatDto? Ceo(RegisteredFactoryDto f) =>
        f.CeoSeat is null ? null : f.Seats.FirstOrDefault(s => SameId(s.Id, f.CeoSeat));

    private static string SeatName(RegisteredFactoryDto f, string seatId) =>
        f.Seats.FirstOrDefault(s => SameId(s.Id, seatId))?.Name ?? FactoryAgentsFold.Humanize(seatId);

    /// <summary>
    /// The zone one seat's times are told in, and its name when it is not the account's (live QA, 6 Oct 2026). A seat
    /// runs by its schedule's clock, and its schedule is shown in that clock ("Daily 06:15 (America/Toronto)") - so
    /// the run times beside it are too. Before this the schedule said 06:15 Toronto and the run beside it said 10:16,
    /// the UTC time of the same run, and the seat read as four hours late.
    ///
    /// Why the schedule's zone and not the account's for the whole row: a schedule is a wall-clock time in its own
    /// zone, and converting it to another zone is a different hour twice a year, so "Daily 10:15" would be wrong
    /// half the time. A run time converts exactly. A seat whose schedules disagree on a zone has no single clock;
    /// its schedules each name their own and its times are the account's.
    /// </summary>
    internal readonly record struct Clock(TimeZoneInfo Zone, string? ZoneName)
    {
        /// <summary><see cref="FactoriesScreenFold.When"/> in this clock, naming the zone when it is not the
        /// account's - unless the caller has already named it once for a whole block.</summary>
        public string When(DateTime utc, DateTime nowUtc, bool capital, bool named = true)
        {
            var text = FactoriesScreenFold.When(utc, Zone, nowUtc, capital);
            return named && ZoneName is not null ? $"{text} ({ZoneName})" : text;
        }
    }

    internal static Clock SeatClock(IReadOnlyList<CronJobDto> schedules, TimeZoneInfo accountZone)
    {
        var ids = schedules.Select(j => j.TimeZoneId?.Trim()).Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count != 1) return new Clock(accountZone, null);
        // A schedule's zone is checked when it is saved (CronSchedule.Validate), so an id that does not resolve here
        // is a schedule this Gateway cannot run either; it is shown in the account's clock, and its own row names it.
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(ids[0]!, out var zone)) return new Clock(accountZone, null);
        var same = string.Equals(zone.Id, accountZone.Id, StringComparison.OrdinalIgnoreCase) || zone.HasSameRules(accountZone);
        return new Clock(zone, same ? null : ids[0]);
    }

    private static Clock SeatClockOf(RegisteredFactoryDto f, string seatId, IReadOnlyList<CronJobDto> all, TimeZoneInfo accountZone) =>
        f.Seats.FirstOrDefault(s => SameId(s.Id, seatId)) is { } seat
            ? SeatClock(SchedulesOf(seat, all), accountZone)
            : new Clock(accountZone, null);

    private static List<CronJobDto> SchedulesOf(RegisteredFactorySeatDto seat, IReadOnlyList<CronJobDto> all) =>
        all.Where(j => seat.Schedules.Contains(j.Id, StringComparer.Ordinal)).ToList();

    private static List<FactoryActivityDto> OpenWaiting(FactoryFoldInputs a) =>
        FactoryAgentsFold.OpenWaiting(a.WaitingCandidates, FactoryAgentsFold.AllCorrections(a));

    private static string? Truncated(FactoryFoldInputs a) =>
        a.WindowTruncated || a.WaitingTruncated
            ? "The activity record held more rows than one read returns, so a status or a waiting count here may be out of date."
            : null;

    private static string WindowPhrase(FactoryWindow w) =>
        w.Key == FactoryAgentsFold.WindowLast7d ? "in the last 7 days" : "in this window";

    /// <summary>"Today 06:20", "Yesterday 06:18", "3 Oct 06:20" - in the account's zone. Lower case when it is
    /// the middle of a sentence ("posted by Nora Hale, today 06:20").</summary>
    public static string When(DateTime utc, TimeZoneInfo zone, DateTime nowUtc, bool capital)
    {
        var t = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone).Date;
        var clock = t.ToString("HH:mm", CultureInfo.InvariantCulture);
        string text;
        if (t.Date == today) text = $"today {clock}";
        else if (t.Date == today.AddDays(-1)) text = $"yesterday {clock}";
        else return t.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
        return capital ? Capitalize(text) : text;
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
