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
///   FAILING   - a run of one of its seats failed in the last 24 hours: a "failed" row by a seat that no row
///               corrects, or a seat's schedule whose last firing could not start the session.
///   NEEDS YOU - something it asked or escalated is waiting on the owner.
///   PAUSED    - it has schedules or triggers for its seats and every one of them is off.
///   RUNNING   - otherwise.
///
/// Only registry seats are seats. A row written by a session that is not a seat ("Owner Session") still counts
/// toward what is waiting on the owner - it is the factory's question - but it never becomes a row on the Seats tab
/// or a reason for FAILING.
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
                    WaitingText = WaitingText(open.Where(r => SameId(r.Factory, f.Factory)).ToList()),
                    Href = PageHref(f.Factory),
                    Talk = talk,
                    NoCeoText = talk is null ? "No CEO" : null,
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
            CeoText = ceo is null ? "No CEO" : $"CEO {ceo.Name}",
            SeatCountText = Count(factory.Seats.Count, "seat"),
            ComputerText = $"runs on {factory.Computer}",
            ComputerChangeText = ChangeComing,
            Talk = CeoTalk(factory, DuplicateCeoNames(input.Registry)),
            Tabs = PageTabs(factory.Seats.Count),
            Goal = GoalCard(factory),
            GoalNumber = GoalNumberCard(number, factory, zone, now),
            Waiting = new FactoryPageWaitingDto
            {
                Heading = "Waiting on you",
                Items = open.OrderBy(r => r.Outcome == FactoryActivityOutcome.Escalated ? 0 : 1).ThenBy(r => r.OccurredUtc)
                    .Select(r => FactoryAgentsFold.WaitingItem(r, zone)).ToList(),
                EmptyText = open.Count == 0 ? "Nothing is waiting on you." : null,
            },
            CeoLatest = CeoLatest(factory, ceo, input.Activity),
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

    private static FactoryCeoLatestDto CeoLatest(RegisteredFactoryDto f, RegisteredFactorySeatDto? ceo, FactoryFoldInputs a)
    {
        var dto = new FactoryCeoLatestDto { Heading = "Latest from the CEO" };
        if (ceo is null)
        {
            dto.EmptyText = "This factory has no CEO.";
            return dto;
        }
        var lines = a.WindowRows
            .Where(r => SameId(r.Factory, f.Factory) && SameId(r.FactoryAgent, ceo.Id)
                        && r.Outcome is not FactoryActivityOutcome.Started and not FactoryActivityOutcome.NothingToDo
                            and not FactoryActivityOutcome.Paused and not FactoryActivityOutcome.Skipped)
            .OrderByDescending(r => r.OccurredUtc)
            .Take(CeoLines)
            .Select(r => $"{When(r.OccurredUtc, a.Zone, a.NowUtc, capital: true)} - {r.What}")
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
        var (lastText, lastTone) = LastRun(f, seat, schedules, a);
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
            Talk = new FactoryTalkDto { Label = "Talk", FactoryId = f.Factory, SeatId = seat.Id },
        };
    }

    /// <summary>
    /// A seat's last run and how it ended: the latest of its schedules' firings and its own "started" rows, then
    /// what that session's rows came to. "Not run yet" when neither has anything.
    /// </summary>
    private static (string Text, string Tone) LastRun(RegisteredFactoryDto f, RegisteredFactorySeatDto seat,
        IReadOnlyList<CronJobDto> schedules, FactoryFoldInputs a)
    {
        var rows = a.WindowRows.Where(r => SameId(r.Factory, f.Factory) && SameId(r.FactoryAgent, seat.Id)).ToList();
        var started = rows.Where(r => r.Outcome == FactoryActivityOutcome.Started).OrderByDescending(r => r.OccurredUtc).FirstOrDefault();
        var fired = schedules.Where(j => j.LastFiredUtc is not null).OrderByDescending(j => j.LastFiredUtc).FirstOrDefault();

        if (started is null && fired is null) return ("Not run yet", FactoryTone.Grey);

        // A firing newer than the newest start the record holds: that firing is the last run.
        if (fired is not null && (started is null || fired.LastFiredUtc!.Value > started.OccurredUtc.AddMinutes(5)))
        {
            var when = When(fired.LastFiredUtc!.Value, a.Zone, a.NowUtc, capital: true);
            return IsNotStarted(fired.LastStatus)
                ? ($"{when} - did not start", FactoryTone.Red)
                : ($"{when} - started", FactoryTone.Blue);
        }

        var at = When(started!.OccurredUtc, a.Zone, a.NowUtc, capital: true);
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

    internal readonly record struct FactoryStatus(int Rank, string Word, string Tone, string Reason);

    internal static FactoryStatus Status(RegisteredFactoryDto f, FactoriesScreenInputs input, IReadOnlyList<FactoryActivityDto> open)
    {
        var a = input.Activity;
        var since = a.NowUtc - FailingWindow;
        var seatIds = f.Seats.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var corrected = FactoryAgentsFold.AllCorrections(a).Where(r => r.CorrectsId is not null).Select(r => r.CorrectsId!.Value).ToHashSet();

        var failedRow = a.WindowRows
            .Where(r => SameId(r.Factory, f.Factory) && seatIds.Contains(r.FactoryAgent)
                        && r.Outcome == FactoryActivityOutcome.Failed && r.OccurredUtc >= since && !corrected.Contains(r.Id))
            .OrderByDescending(r => r.OccurredUtc).FirstOrDefault();
        if (failedRow is not null)
            return new(0, StatusFailing, FactoryTone.Red,
                $"A run of {SeatName(f, failedRow.FactoryAgent)} failed {When(failedRow.OccurredUtc, a.Zone, a.NowUtc, capital: false)}: {failedRow.What}");

        var seatSchedules = f.Seats.SelectMany(s => SchedulesOf(s, input.Schedules).Select(j => (Seat: s, Job: j))).ToList();
        var notStarted = seatSchedules
            .Where(x => IsNotStarted(x.Job.LastStatus) && x.Job.LastFiredUtc is { } t && t >= since)
            .OrderByDescending(x => x.Job.LastFiredUtc).FirstOrDefault();
        if (notStarted.Job is not null)
            return new(0, StatusFailing, FactoryTone.Red,
                $"The schedule for {notStarted.Seat.Name} could not start its run {When(notStarted.Job.LastFiredUtc!.Value, a.Zone, a.NowUtc, capital: false)}.");

        var waiting = open.Where(r => SameId(r.Factory, f.Factory)).ToList();
        if (waiting.Count > 0)
            return new(1, StatusNeedsYou, FactoryTone.Amber, $"{WaitingText(waiting)} waiting on you.");

        var triggers = a.Triggers.Where(t => SameId(t.Factory, f.Factory) && seatIds.Contains(t.FactoryAgent)).ToList();
        var switches = seatSchedules.Count + triggers.Count;
        if (switches > 0 && seatSchedules.All(x => !x.Job.Enabled) && triggers.All(t => t.Paused))
            return new(2, StatusPaused, FactoryTone.Paused, "Every schedule and trigger of its seats is off.");

        return new(3, StatusRunning, FactoryTone.Ok,
            switches == 0 ? "Nothing failed and nothing is waiting on you. No schedule or trigger runs its seats." : "Nothing failed and nothing is waiting on you.");
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

    /// <summary>
    /// The CEO's Talk button: "Talk to Nora Hale", or "Talk to the CEO" when another registered factory's CEO has the
    /// same name, so two buttons on one list never read alike. Null when the factory has no CEO.
    /// </summary>
    private static FactoryTalkDto? CeoTalk(RegisteredFactoryDto f, IReadOnlySet<string> duplicateNames)
    {
        var ceo = Ceo(f);
        if (ceo is null) return null;
        return new FactoryTalkDto
        {
            Label = duplicateNames.Contains(ceo.Name) ? "Talk to the CEO" : $"Talk to {ceo.Name}",
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

    private static List<CronJobDto> SchedulesOf(RegisteredFactorySeatDto seat, IReadOnlyList<CronJobDto> all) =>
        all.Where(j => seat.Schedules.Contains(j.Id, StringComparer.Ordinal)).ToList();

    private static bool IsNotStarted(string? lastStatus) =>
        string.Equals(lastStatus, ScheduleNotStarted, StringComparison.Ordinal);

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
