using System.Globalization;
using System.Text;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Triggers;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// What the Gateway knows about one trigger, in the shape the fold reads. The trigger store owns triggers; the
/// host adapts its rows to this, so the fold never depends on how a trigger is stored.
/// </summary>
/// <param name="Id">The trigger's id - what Pause and Resume name.</param>
/// <param name="Name">Its name ("New business mail").</param>
/// <param name="Factory">The factory it belongs to.</param>
/// <param name="FactoryAgent">The factory agent it wakes.</param>
/// <param name="IntervalSeconds">How often its check runs.</param>
/// <param name="Paused">True while paused: it checks, records "paused" and starts nothing.</param>
/// <param name="Red">The trigger store's own verdict that the trigger is in fault (check failed, or no checks ran).</param>
/// <param name="StatusText">The store's sentence for that verdict, or null when it is fine.</param>
/// <param name="LastCheckUtc">When its check last reported, or null when it never has.</param>
/// <param name="LastResult">What that check came to, in words ("nothing to do").</param>
public sealed record FactoryTriggerFacts(
    string Id,
    string Name,
    string Factory,
    string FactoryAgent,
    int IntervalSeconds,
    bool Paused,
    bool Red,
    string? StatusText,
    DateTime? LastCheckUtc,
    string? LastResult);

/// <summary>A resolved time window: its preset key and its bounds (from inclusive, to exclusive).</summary>
public sealed record FactoryWindow(string Key, DateTime FromUtc, DateTime ToUtc);

/// <summary>The filters the Activity and Reports tabs send back.</summary>
public sealed record FactoryFilter(string? Factory, string? Agent, string? Outcome)
{
    public static readonly FactoryFilter None = new(null, null, null);
}

/// <summary>
/// Everything one fold reads, taken once per request so a view answers as of a single moment.
/// </summary>
/// <param name="WindowRows">Every record row in the window (factory-filtered at most - never agent- or
/// outcome-filtered, because "no checks ran" must be judged on everything the factory wrote).</param>
/// <param name="WindowTruncated">True when the window held more rows than one read returns.</param>
/// <param name="WaitingCandidates">Every asked and escalated row, whatever its age.</param>
/// <param name="Corrections">Every row that corrects another, from the oldest waiting candidate onward.</param>
/// <param name="Triggers">Every trigger of the account.</param>
/// <param name="LiveSessionIds">The sessions alive in the account's roster now.</param>
/// <param name="Window">The window the rows were read for.</param>
/// <param name="Zone">The account's time zone - every time on screen is in it.</param>
/// <param name="NowUtc">The moment of the fold.</param>
public sealed record FactoryFoldInputs(
    IReadOnlyList<FactoryActivityDto> WindowRows,
    bool WindowTruncated,
    IReadOnlyList<FactoryActivityDto> WaitingCandidates,
    IReadOnlyList<FactoryActivityDto> Corrections,
    IReadOnlyList<FactoryTriggerFacts> Triggers,
    IReadOnlySet<string> LiveSessionIds,
    FactoryWindow Window,
    TimeZoneInfo Zone,
    DateTime NowUtc);

/// <summary>A report someone kept, as stored.</summary>
public sealed record SavedFactoryReport(
    string Id, string Name, string? Factory, string? Agent, string? Outcome,
    string Window, DateTime? FromUtc, DateTime? ToUtc, string SavedBy, DateTime SavedUtc);

/// <summary>A request the Gateway refuses, with the sentence the caller reads (a 400 at the route).</summary>
public sealed class FactoryViewValidationException : Exception
{
    public FactoryViewValidationException(string message) : base(message) { }
}

/// <summary>
/// THE ONE PLACE THE FACTORY AGENTS AREA IS DECIDED (critical rule 7). Pure: it reads the record rows and the
/// trigger facts it is handed and returns finished display objects. It performs no I/O, so every rule the screens
/// show is proven by a unit test that calls it directly.
///
/// The rules it owns, each tested:
///  - Runs of empty trigger checks collapse into one grey line ("Checked for new business mail 25 times - nothing
///    to do"), so a quiet night reads as quiet without hiding that the checks ran.
///  - PRESENCE, NOT ABSENCE. A factory whose triggers exist and which wrote NO row in the window is the fault
///    "No checks ran", shown red - never a quiet night. A trigger the trigger store calls red is red here too.
///  - Waiting for you is every asked and escalated row no later row corrects. A correction is a new row; the
///    record is never edited.
///  - A paused trigger shows as paused, and a factory agent whose triggers are all paused is PAUSED.
/// </summary>
public static class FactoryAgentsFold
{
    public const string DefinitionNotStored = "Definition not stored yet - #2177 mission 1";

    public const string WindowLast24h = "last-24h";
    public const string WindowLast7d = "last-7d";
    public const string WindowLast30d = "last-30d";
    public const string WindowCustom = "custom";

    /// <summary>The longest custom window one read may cover.</summary>
    public static readonly TimeSpan MaxCustomWindow = TimeSpan.FromDays(366);

    // The order outcomes are counted and offered in: what happened, then what was held back, then the quiet rows.
    private static readonly string[] SummaryOrder =
    {
        FactoryActivityOutcome.Done, FactoryActivityOutcome.Asked, FactoryActivityOutcome.Escalated,
        FactoryActivityOutcome.Blocked, FactoryActivityOutcome.SentBack, FactoryActivityOutcome.Failed,
        FactoryActivityOutcome.Allowed,
    };

    public static readonly string[] ReportColumns =
    {
        "Factory agent", "Runs", "Done", "Asked", "Escalated", "Blocked", "Sent back", "Failed", "Empty checks",
    };

    // ---------------------------------------------------------------------------------------------------------
    // Windows

    /// <summary>
    /// Resolve the window a request names. A preset counts back from now; a custom window needs both ends, in
    /// order, no longer than <see cref="MaxCustomWindow"/>. Anything else is refused - a view never silently
    /// shows a different window from the one asked for.
    /// </summary>
    public static FactoryWindow ResolveWindow(string? key, DateTime? fromUtc, DateTime? toUtc, DateTime nowUtc,
        string defaultKey, TimeZoneInfo? zone = null)
    {
        var k = string.IsNullOrWhiteSpace(key) ? defaultKey : key.Trim().ToLowerInvariant();
        switch (k)
        {
            case WindowLast24h: return new FactoryWindow(k, nowUtc.AddHours(-24), nowUtc);
            case WindowLast7d: return new FactoryWindow(k, nowUtc.AddDays(-7), nowUtc);
            case WindowLast30d: return new FactoryWindow(k, nowUtc.AddDays(-30), nowUtc);
            case WindowCustom:
                if (fromUtc is null || toUtc is null)
                    throw new FactoryViewValidationException("A custom window needs both a start and an end.");
                // A date typed into the page carries no zone: it is a time in the account's own zone.
                var from = AsUtc(fromUtc.Value, zone);
                var to = AsUtc(toUtc.Value, zone);
                if (to <= from)
                    throw new FactoryViewValidationException("The window's end must be after its start.");
                if (to - from > MaxCustomWindow)
                    throw new FactoryViewValidationException("A window can cover at most 366 days.");
                return new FactoryWindow(k, from, to);
            default:
                throw new FactoryViewValidationException(
                    $"'{key}' is not a window. Use {WindowLast24h}, {WindowLast7d}, {WindowLast30d} or {WindowCustom}.");
        }
    }

    public static FactoryWindowDto WindowDto(FactoryWindow window, TimeZoneInfo zone) => new()
    {
        Key = window.Key,
        Label = WindowLabel(window, zone),
        FromUtc = window.FromUtc,
        ToUtc = window.ToUtc,
        FromLocal = Local(window.FromUtc, zone).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
        ToLocal = Local(window.ToUtc, zone).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
        Choices = new List<FactoryChoiceDto>
        {
            new() { Value = WindowLast24h, Label = "Last 24 hours" },
            new() { Value = WindowLast7d, Label = "Last 7 days" },
            new() { Value = WindowLast30d, Label = "Last 30 days" },
            new() { Value = WindowCustom, Label = "Choose dates" },
        },
    };

    public static string WindowLabel(FactoryWindow window, TimeZoneInfo zone) => window.Key switch
    {
        WindowLast24h => "Last 24 hours",
        WindowLast7d => "Last 7 days",
        WindowLast30d => "Last 30 days",
        _ => $"{DateTimeText(window.FromUtc, zone)} to {DateTimeText(window.ToUtc, zone)}",
    };

    // "in the last 24 hours" / "between 21 Sep 18:00 and 22 Sep 08:00" - the window inside a sentence.
    private static string WindowPhrase(FactoryWindow window, TimeZoneInfo zone) => window.Key switch
    {
        WindowLast24h => "in the last 24 hours",
        WindowLast7d => "in the last 7 days",
        WindowLast30d => "in the last 30 days",
        _ => $"between {DateTimeText(window.FromUtc, zone)} and {DateTimeText(window.ToUtc, zone)}",
    };

    // ---------------------------------------------------------------------------------------------------------
    // Screen 1: the Factories tab, and the All factory agents tab

    public static FactoriesViewDto Factories(FactoryFoldInputs input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var open = OpenWaiting(input.WaitingCandidates, AllCorrections(input));
        var factoryIds = FactoryIds(input, open);

        var cards = factoryIds.Select(f => Card(f, input, open)).ToList();
        var allAgents = cards.SelectMany(c => c.Agents)
            .OrderBy(a => a.FactoryTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new FactoriesViewDto
        {
            Title = "Factory Agents",
            Subtitle = "Virtual employees, and the factories they work in. They are built and changed by talking to the Fleet Manager.",
            Tabs = Tabs(allAgents.Count),
            Window = WindowDto(input.Window, input.Zone),
            Factories = cards,
            AllAgents = allAgents,
            EmptyText = cards.Count == 0
                ? "No factory yet. A factory appears here when a trigger names it or a factory agent writes to the record."
                : null,
        };
    }

    public static List<FactoryTabDto> Tabs(int agentCount) => new()
    {
        new() { Key = "factories", Label = "Factories" },
        new() { Key = "agents", Label = $"All factory agents ({agentCount})" },
        new() { Key = "activity", Label = "Activity" },
        new() { Key = "reports", Label = "Reports" },
    };

    private static FactoryCardDto Card(string factory, FactoryFoldInputs input, IReadOnlyList<FactoryActivityDto> open)
    {
        var title = Title(factory);
        var triggers = input.Triggers.Where(t => SameId(t.Factory, factory)).ToList();
        var rows = input.WindowRows.Where(r => SameId(r.Factory, factory)).ToList();
        var waiting = open.Where(r => SameId(r.Factory, factory)).ToList();

        var agentIds = triggers.Select(t => t.FactoryAgent)
            .Concat(rows.Select(r => r.FactoryAgent))
            .Concat(waiting.Select(r => r.FactoryAgent))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Humanize, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A trigger's fault - "no checks ran" included - is the trigger's own status, decided on the Gateway from its
        // last check (TriggerStatusFold). The window is never the judge: a trigger that checks daily writes nothing
        // in the last hour, and that is not a fault.
        var faults = triggers.Where(t => t.Red).Select(t => $"Trigger \"{t.Name}\": {TriggerRedText(t)}.").ToList();

        string word, tone;
        if (faults.Count > 0) { word = "FAULT"; tone = FactoryTone.Red; }
        else if (triggers.Count > 0 && triggers.All(t => t.Paused)) { word = "PAUSED"; tone = FactoryTone.Paused; }
        else { word = "RUNNING"; tone = FactoryTone.Ok; }

        return new FactoryCardDto
        {
            Id = factory,
            Title = title,
            StatusWord = word,
            StatusTone = tone,
            Subtitle = $"{Count(agentIds.Count, "factory agent")}, {Count(triggers.Count, "trigger")}",
            FaultText = faults.Count == 0 ? null : string.Join(" ", faults),
            Numbers = CardNumbers(factory, input.Window, rows, waiting),
            Agents = agentIds.Select(a => AgentRow(factory, a, input, rows)).ToList(),
            Pause = PauseFor(triggers, $"the factory {title}", "factory"),
            WaitingHref = WaitingHref(factory),
        };
    }

    private static List<FactoryNumberDto> CardNumbers(string factory, FactoryWindow window,
        IReadOnlyList<FactoryActivityDto> rows, IReadOnlyList<FactoryActivityDto> waiting)
    {
        var numbers = new List<FactoryNumberDto>();
        var waitingHref = WaitingHref(factory);
        string ActivityHref(string outcome) => ActivityHrefFor(factory, outcome, window);
        var asked = waiting.Count(r => Is(r, FactoryActivityOutcome.Asked));
        var escalated = waiting.Count(r => Is(r, FactoryActivityOutcome.Escalated));
        if (asked > 0)
            numbers.Add(new() { Text = $"{asked} asked, waiting for you", Tone = FactoryTone.Neutral, Target = "waiting", Href = waitingHref });
        if (escalated > 0)
            numbers.Add(new() { Text = $"{escalated} escalated to you", Tone = FactoryTone.Amber, Target = "waiting", Href = waitingHref });

        void Add(string outcome, string text, string tone)
        {
            var n = rows.Count(r => Is(r, outcome));
            if (n > 0)
                numbers.Add(new() { Text = text.Replace("{n}", n.ToString(CultureInfo.InvariantCulture)), Tone = tone, Target = "activity", Outcome = outcome, Href = ActivityHref(outcome) });
        }

        var runs = rows.Count(r => Is(r, FactoryActivityOutcome.Started));
        if (runs > 0)
            numbers.Add(new() { Text = Count(runs, "run"), Tone = FactoryTone.Neutral, Target = "activity", Outcome = FactoryActivityOutcome.Started, Href = ActivityHref(FactoryActivityOutcome.Started) });
        Add(FactoryActivityOutcome.Failed, "{n} failed", FactoryTone.Red);
        Add(FactoryActivityOutcome.Blocked, "{n} blocked", FactoryTone.Neutral);
        Add(FactoryActivityOutcome.SentBack, "{n} sent back", FactoryTone.Neutral);
        var empty = rows.Count(r => Is(r, FactoryActivityOutcome.NothingToDo));
        if (empty > 0)
            numbers.Add(new() { Text = Count(empty, "empty check"), Tone = FactoryTone.Grey, Target = "activity", Outcome = FactoryActivityOutcome.NothingToDo, Href = ActivityHref(FactoryActivityOutcome.NothingToDo) });
        var paused = rows.Count(r => Is(r, FactoryActivityOutcome.Paused));
        if (paused > 0)
            numbers.Add(new() { Text = $"{Count(paused, "check")} while paused", Tone = FactoryTone.Paused, Target = "activity", Outcome = FactoryActivityOutcome.Paused, Href = ActivityHref(FactoryActivityOutcome.Paused) });
        return numbers;
    }

    private static FactoryAgentRowDto AgentRow(string factory, string agent, FactoryFoldInputs input,
        IReadOnlyList<FactoryActivityDto> factoryRows)
    {
        var triggers = input.Triggers.Where(t => SameId(t.Factory, factory) && SameId(t.FactoryAgent, agent)).ToList();
        var rows = factoryRows.Where(r => SameId(r.FactoryAgent, agent)).ToList();
        var (word, tone) = AgentStatus(triggers, rows, input.LiveSessionIds);
        return new FactoryAgentRowDto
        {
            FactoryId = factory,
            FactoryTitle = Title(factory),
            AgentId = agent,
            Name = AgentName(agent, rows),
            WokenBy = triggers.Count == 0 ? "No trigger names it" : string.Join("; ", triggers.Select(TriggerText)),
            LastRun = LastRun(rows, input.Window, input.Zone),
            StatusWord = word,
            StatusTone = tone,
            Href = AgentHref(factory, agent),
        };
    }

    // The status of one factory agent. A fault outranks everything, because it is the one thing that needs the
    // owner; then paused; then a live session it started; otherwise idle.
    private static (string Word, string Tone) AgentStatus(IReadOnlyList<FactoryTriggerFacts> triggers,
        IReadOnlyList<FactoryActivityDto> rows, IReadOnlySet<string> live)
    {
        if (triggers.Any(t => t.Red))
            return ("FAULT", FactoryTone.Red);
        if (triggers.Count > 0 && triggers.All(t => t.Paused))
            return ("PAUSED", FactoryTone.Paused);
        if (rows.Any(r => Is(r, FactoryActivityOutcome.Started) && r.SessionId is { } sid && live.Contains(sid)))
            return ("WORKING", FactoryTone.Working);
        return ("IDLE", FactoryTone.Idle);
    }

    private static string LastRun(IReadOnlyList<FactoryActivityDto> rows, FactoryWindow window, TimeZoneInfo zone)
    {
        var started = rows.Where(r => Is(r, FactoryActivityOutcome.Started))
            .OrderByDescending(r => r.OccurredUtc).FirstOrDefault();
        if (started is null)
            return $"No run {WindowPhrase(window, zone)}";

        var time = ClockText(started.OccurredUtc, window, zone);
        if (started.SessionId is null)
            return $"{time} - started";
        var inSession = rows.Where(r => r.SessionId == started.SessionId && !Is(r, FactoryActivityOutcome.Started)).ToList();
        var parts = SummaryOrder
            .Select(o => (o, n: inSession.Count(r => Is(r, o))))
            .Where(x => x.n > 0)
            .Select(x => $"{x.n} {OutcomeLower(x.o)}")
            .ToList();
        return parts.Count == 0 ? $"{time} - started" : $"{time} - {string.Join(", ", parts)}";
    }

    // ---------------------------------------------------------------------------------------------------------
    // Screen 2: one factory agent

    /// <summary>
    /// A factory agent's page. <paramref name="input"/>'s window is the last 7 days and its rows are this factory's.
    /// </summary>
    public static FactoryAgentPageDto AgentPage(string factory, string agent, FactoryFoldInputs input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var title = Title(factory);
        var triggers = input.Triggers.Where(t => SameId(t.Factory, factory) && SameId(t.FactoryAgent, agent)).ToList();
        var rows = input.WindowRows.Where(r => SameId(r.Factory, factory) && SameId(r.FactoryAgent, agent))
            .OrderBy(r => r.OccurredUtc).ToList();

        // Its status is judged on the last day, like its card: seven days of rows would hide a trigger that
        // stopped checking this morning.
        var dayAgo = input.NowUtc.AddHours(-24);
        var lastDay = rows.Where(r => r.OccurredUtc >= dayAgo).ToList();
        var (word, tone) = AgentStatus(triggers, lastDay, input.LiveSessionIds);
        var name = AgentName(agent, rows);

        var corrections = AllCorrections(input);
        var lines = Collapse(rows, input.Window, input.Zone, corrections, rows);

        return new FactoryAgentPageDto
        {
            FactoryId = factory,
            FactoryTitle = title,
            AgentId = agent,
            Name = name,
            StatusWord = word,
            StatusTone = tone,
            DefinitionText = DefinitionNotStored,
            WokenBy = triggers.Select(t => WokenBy(t, input.Window, input.Zone)).ToList(),
            WokenByEmptyText = triggers.Count == 0
                ? "No trigger names this factory agent. A schedule, a person or another factory agent may still start it."
                : null,
            LastCheck = LastCheck(triggers, input.Window, input.Zone),
            Last7DaysTitle = "Last 7 days",
            Last7Days = Last7Days(rows),
            Pause = PauseFor(triggers, $"the factory agent {name}", "factory agent"),
            PauseUnavailableText = triggers.Count == 0
                ? "Nothing to pause: no trigger wakes this factory agent. Pausing one that a schedule starts needs its stored definition (#2177 mission 1)."
                : null,
            AskLabel = "Ask the Fleet Manager to change it",
            AskHref = "/fleet-manager?ask=" + Uri.EscapeDataString(
                $"Please change the factory agent {name} in {title} ({factory} / {agent}): "),
            RecentTitle = "Recent activity",
            Recent = lines.Count > 15 ? lines.Skip(lines.Count - 15).ToList() : lines,
            RecentEmptyText = lines.Count == 0 ? "Nothing recorded in the last 7 days." : null,
        };
    }

    private static FactoryWokenByDto WokenBy(FactoryTriggerFacts t, FactoryWindow window, TimeZoneInfo zone)
    {
        string word, tone;
        if (t.Red) { word = "FAULT"; tone = FactoryTone.Red; }
        else if (t.Paused) { word = "PAUSED"; tone = FactoryTone.Paused; }
        else { word = "OK"; tone = FactoryTone.Ok; }
        return new FactoryWokenByDto
        {
            TriggerId = t.Id,
            Text = TriggerText(t),
            StatusWord = word,
            StatusTone = tone,
            StatusText = t.Red ? TriggerRedText(t) : null,
            LastCheck = t.LastCheckUtc is { } at
                ? $"{DateTimeText(at, zone)} - {t.LastResult ?? "no result"}"
                : "Never checked",
        };
    }

    private static string LastCheck(IReadOnlyList<FactoryTriggerFacts> triggers, FactoryWindow window, TimeZoneInfo zone)
    {
        if (triggers.Count == 0) return "No trigger checks for it";
        var last = triggers.Where(t => t.LastCheckUtc is not null).OrderByDescending(t => t.LastCheckUtc).FirstOrDefault();
        return last is null
            ? "Never checked"
            : $"{DateTimeText(last.LastCheckUtc!.Value, zone)} - {last.LastResult ?? "no result"}";
    }

    private static List<FactoryNumberDto> Last7Days(IReadOnlyList<FactoryActivityDto> rows)
    {
        var numbers = new List<FactoryNumberDto>
        {
            new() { Text = Count(rows.Count(r => Is(r, FactoryActivityOutcome.Started)), "run"), Tone = FactoryTone.Neutral },
        };
        foreach (var o in SummaryOrder)
        {
            var n = rows.Count(r => Is(r, o));
            if (n > 0)
                numbers.Add(new() { Text = $"{n} {OutcomeLower(o)}", Tone = OutcomeTone(o) == FactoryTone.Red ? FactoryTone.Red : FactoryTone.Neutral });
        }
        var empty = rows.Count(r => Is(r, FactoryActivityOutcome.NothingToDo));
        if (empty > 0)
            numbers.Add(new() { Text = Count(empty, "empty check"), Tone = FactoryTone.Grey });
        return numbers;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Screen 3: Activity

    public static FactoryActivityViewDto Activity(FactoryFoldInputs input, FactoryFilter filter, string csvHref)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(filter);
        var shown = Filtered(input.WindowRows, filter).OrderBy(r => r.OccurredUtc).ThenBy(r => r.RecordedUtc).ToList();
        var corrections = AllCorrections(input);
        var lines = Collapse(shown, input.Window, input.Zone, corrections, input.WindowRows);

        return new FactoryActivityViewDto
        {
            Window = WindowDto(input.Window, input.Zone),
            Filters = Filters(input, filter),
            Rows = lines,
            Faults = TriggerFaults(input, filter),
            EmptyText = lines.Count == 0 ? $"Nothing matches these filters {WindowPhrase(input.Window, input.Zone)}." : null,
            TruncatedText = input.WindowTruncated
                ? $"This window holds more than {input.WindowRows.Count} rows; only the first {input.WindowRows.Count} are shown. Choose a shorter window."
                : null,
            Footnote = "These rows are permanent. Nothing here can be edited or deleted, by anyone. A correction is a new row pointing at the old one. "
                       + "Empty trigger checks collapse into one grey line, so a quiet night reads as quiet without hiding that the checks ran.",
            CsvHref = csvHref,
            SaveLabel = "Make a report from this",
        };
    }

    /// <summary>
    /// The Activity lines: one per row, except that a run of empty checks - consecutive "nothing to do" rows with
    /// nothing else between them - is one line per trigger, placed where the run started.
    /// </summary>
    public static List<FactoryActivityRowViewDto> Collapse(IReadOnlyList<FactoryActivityDto> rows, FactoryWindow window,
        TimeZoneInfo zone, IReadOnlyList<FactoryActivityDto> corrections, IReadOnlyList<FactoryActivityDto> context)
    {
        var byId = context.Concat(corrections).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        var correctedBy = context.Concat(corrections)
            .Where(r => r.CorrectsId is not null)
            .GroupBy(r => r.CorrectsId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.OccurredUtc).Last());

        var lines = new List<FactoryActivityRowViewDto>();
        var i = 0;
        while (i < rows.Count)
        {
            if (!IsEmptyTriggerCheck(rows[i]))
            {
                lines.Add(Line(rows[i], window, zone, byId, correctedBy));
                i++;
                continue;
            }

            var j = i;
            while (j < rows.Count && IsEmptyTriggerCheck(rows[j])) j++;
            // One line per trigger: the actor names it. The sentence is for a person to read and is never grouped on.
            var groups = rows.Skip(i).Take(j - i)
                .GroupBy(r => r.Actor, StringComparer.Ordinal)
                .ToList();
            foreach (var g in groups)
            {
                var run = g.ToList();
                lines.Add(run.Count == 1 ? Line(run[0], window, zone, byId, correctedBy) : Collapsed(run, window, zone));
            }
            i = j;
        }
        return lines;
    }

    private static FactoryActivityRowViewDto Line(FactoryActivityDto r, FactoryWindow window, TimeZoneInfo zone,
        IReadOnlyDictionary<Guid, FactoryActivityDto> byId, IReadOnlyDictionary<Guid, FactoryActivityDto> correctedBy)
    {
        string? note = null;
        if (correctedBy.TryGetValue(r.Id, out var fix))
            note = $"Corrected {DateTimeText(fix.OccurredUtc, zone)}: {fix.What}";
        else if (r.CorrectsId is { } target)
            note = byId.TryGetValue(target, out var old)
                ? $"Corrects the row of {DateTimeText(old.OccurredUtc, zone)}: {old.What}"
                : "Corrects an earlier row";

        return new FactoryActivityRowViewDto
        {
            Key = r.Id.ToString(),
            Time = ClockText(r.OccurredUtc, window, zone),
            FactoryTitle = Title(r.Factory),
            Who = WithVersion(Humanize(r.FactoryAgent), r.FactoryAgentVersion),
            What = r.What,
            Subject = r.Subject,
            OutcomeWord = OutcomeWord(r.Outcome),
            OutcomeTone = OutcomeTone(r.Outcome),
            SessionId = r.SessionId,
            SessionLabel = SessionLabel(r.SessionId),
            Link = r.Link,
            Note = note,
        };
    }

    private static FactoryActivityRowViewDto Collapsed(IReadOnlyList<FactoryActivityDto> run, FactoryWindow window, TimeZoneInfo zone)
    {
        var first = run[0];
        var last = run[^1];
        var n = run.Count;
        var what = string.IsNullOrWhiteSpace(first.Subject)
            ? $"Checked {n} times - nothing to do"
            : $"Checked \"{first.Subject.Trim()}\" {n} times - nothing to do";
        return new FactoryActivityRowViewDto
        {
            Key = first.Id.ToString(),
            Time = $"{ClockText(first.OccurredUtc, window, zone)}-{ClockText(last.OccurredUtc, window, zone)}",
            FactoryTitle = Title(first.Factory),
            Who = WithVersion(Humanize(first.FactoryAgent), first.FactoryAgentVersion),
            What = what,
            OutcomeWord = OutcomeWord(FactoryActivityOutcome.NothingToDo),
            OutcomeTone = FactoryTone.Grey,
            Collapsed = true,
        };
    }

    // ---------------------------------------------------------------------------------------------------------
    // Screen 4: Waiting for you

    /// <summary>Every asked and escalated row that no row corrects.</summary>
    public static List<FactoryActivityDto> OpenWaiting(IReadOnlyList<FactoryActivityDto> candidates,
        IReadOnlyList<FactoryActivityDto> corrections)
    {
        var corrected = corrections.Where(r => r.CorrectsId is not null).Select(r => r.CorrectsId!.Value).ToHashSet();
        return candidates
            .Where(r => (Is(r, FactoryActivityOutcome.Asked) || Is(r, FactoryActivityOutcome.Escalated)) && !corrected.Contains(r.Id))
            .GroupBy(r => r.Id).Select(g => g.First())
            .ToList();
    }

    public static FactoryWaitingViewDto Waiting(FactoryFoldInputs input, string? factory)
    {
        ArgumentNullException.ThrowIfNull(input);
        var open = OpenWaiting(input.WaitingCandidates, AllCorrections(input))
            .Where(r => factory is null || SameId(r.Factory, factory))
            .OrderBy(r => Is(r, FactoryActivityOutcome.Escalated) ? 0 : 1)
            .ThenBy(r => r.OccurredUtc)
            .ToList();

        var escalated = open.Count(r => Is(r, FactoryActivityOutcome.Escalated));
        var asked = open.Count - escalated;
        return new FactoryWaitingViewDto
        {
            FactoryId = factory,
            Title = factory is null ? "Waiting for you" : $"Waiting for you - {Title(factory)}",
            Summary = open.Count == 0
                ? "Nothing is waiting for you."
                : $"{Count(open.Count, "item")}: {escalated} escalated, {asked} asked. "
                  + "An asked item clears by itself when the record notes it was handled. An escalation stays here until you mark it handled.",
            Items = open.Select(r => WaitingItem(r, input.Zone)).ToList(),
            EmptyText = open.Count == 0 ? "Nothing asked or escalated is waiting on you." : null,
            TruncatedText = input.WindowTruncated
                ? "The record held more corrections than one read returns, so some items here may already be handled."
                : null,
        };
    }

    private static FactoryWaitingItemDto WaitingItem(FactoryActivityDto r, TimeZoneInfo zone)
    {
        var escalated = Is(r, FactoryActivityOutcome.Escalated);
        return new FactoryWaitingItemDto
        {
            Id = r.Id,
            Word = OutcomeWord(r.Outcome),
            Tone = escalated ? FactoryTone.Amber : FactoryTone.Neutral,
            FactoryTitle = Title(r.Factory),
            Subject = r.Subject,
            What = r.What,
            By = $"{WithVersion(Humanize(r.FactoryAgent), r.FactoryAgentVersion)}, {DateTimeText(r.OccurredUtc, zone)}",
            SessionId = r.SessionId,
            SessionLabel = SessionLabel(r.SessionId),
            Link = r.Link,
            LinkLabel = r.Link is null ? null : "Open",
            HandledLabel = escalated ? "I have handled it" : null,
            HandledBusyLabel = escalated ? "Marking it handled..." : null,
        };
    }

    /// <summary>
    /// The row "I have handled it" appends: a NEW row correcting the escalation. The escalation itself is never
    /// changed. Refused unless the row is an escalation nothing has corrected yet.
    /// </summary>
    public static AppendFactoryActivityRequest HandledRow(FactoryActivityDto escalation, IReadOnlyList<FactoryActivityDto> corrections,
        string actor, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(escalation);
        if (!Is(escalation, FactoryActivityOutcome.Escalated))
            throw new FactoryViewValidationException("Only an escalation can be marked handled; an asked item clears when the record notes it was handled.");
        if (corrections.Any(c => c.CorrectsId == escalation.Id))
            throw new FactoryViewValidationException("This escalation is already handled.");

        var what = $"Marked handled by {actor}: {escalation.What}";
        if (what.Length > FactoryActivityMaxWhat) what = what[..(FactoryActivityMaxWhat - 3)] + "...";
        return new AppendFactoryActivityRequest
        {
            Factory = escalation.Factory,
            FactoryAgent = escalation.FactoryAgent,
            FactoryAgentVersion = escalation.FactoryAgentVersion,
            What = what,
            Outcome = FactoryActivityOutcome.Done,
            Subject = escalation.Subject,
            Actor = actor,
            CorrectsId = escalation.Id,
            OccurredUtc = nowUtc,
        };
    }

    private const int FactoryActivityMaxWhat = 500;

    // ---------------------------------------------------------------------------------------------------------
    // Screen 5: Reports

    public static FactoryReportViewDto Report(FactoryFoldInputs input, FactoryFilter filter, string csvHref,
        IReadOnlyList<SavedFactoryReport> saved, string? openedReportName)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(filter);
        var shown = Filtered(input.WindowRows, filter).ToList();
        var multiFactory = shown.Select(r => r.Factory.ToLowerInvariant()).Distinct().Count() > 1;

        var rows = shown
            .GroupBy(r => (F: r.Factory.ToLowerInvariant(), A: r.FactoryAgent.ToLowerInvariant()))
            .Select(g => new FactoryReportRowDto
            {
                Key = $"{g.First().Factory}/{g.First().FactoryAgent}",
                Name = multiFactory
                    ? $"{Title(g.First().Factory)} / {Humanize(g.First().FactoryAgent)}"
                    : Humanize(g.First().FactoryAgent),
                Cells = ReportCells(g.ToList()),
            })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new FactoryReportViewDto
        {
            Window = WindowDto(input.Window, input.Zone),
            Filters = Filters(input, filter),
            TableTitle = $"Each factory agent, {WindowPhrase(input.Window, input.Zone)}",
            Columns = ReportColumns.ToList(),
            Rows = rows,
            Total = rows.Count > 1 ? new FactoryReportRowDto { Key = "total", Name = "Total", Cells = ReportCells(shown) } : null,
            EmptyText = rows.Count == 0 ? $"Nothing matches these filters {WindowPhrase(input.Window, input.Zone)}." : null,
            TruncatedText = input.WindowTruncated
                ? $"This window holds more than {input.WindowRows.Count} rows, so these counts are short. Choose a shorter window."
                : null,
            Faults = TriggerFaults(input, filter),
            CsvHref = csvHref,
            SaveLabel = "Make a report from this",
            SavedTitle = "Saved reports",
            Saved = saved.OrderByDescending(s => s.SavedUtc).Select(s => SavedDto(s, input.Zone)).ToList(),
            SavedEmptyText = saved.Count == 0
                ? "No saved reports yet. Choose a filter and press \"Make a report from this\" to keep it."
                : null,
            OpenedReportName = openedReportName,
        };
    }

    private static List<string> ReportCells(IReadOnlyList<FactoryActivityDto> rows)
    {
        string N(string outcome) => rows.Count(r => Is(r, outcome)).ToString(CultureInfo.InvariantCulture);
        return new List<string>
        {
            N(FactoryActivityOutcome.Started), N(FactoryActivityOutcome.Done), N(FactoryActivityOutcome.Asked),
            N(FactoryActivityOutcome.Escalated), N(FactoryActivityOutcome.Blocked), N(FactoryActivityOutcome.SentBack),
            N(FactoryActivityOutcome.Failed), N(FactoryActivityOutcome.NothingToDo),
        };
    }

    public static SavedFactoryReportDto SavedDto(SavedFactoryReport s, TimeZoneInfo zone)
    {
        var window = s.Window == WindowCustom && s.FromUtc is { } f && s.ToUtc is { } t
            ? $"{DateTimeText(f, zone)} to {DateTimeText(t, zone)}"
            : s.Window switch
            {
                WindowLast24h => "last 24 hours",
                WindowLast7d => "last 7 days",
                WindowLast30d => "last 30 days",
                _ => s.Window,
            };
        var parts = new[]
        {
            s.Factory is null ? "every factory" : Title(s.Factory),
            s.Agent is null ? "every factory agent" : Humanize(s.Agent),
            s.Outcome is null ? "every outcome" : OutcomeLower(s.Outcome),
            window,
        };
        return new SavedFactoryReportDto
        {
            Id = s.Id,
            Name = s.Name,
            Description = string.Join(", ", parts),
            Href = $"/factory-agents?tab=reports&report={Uri.EscapeDataString(s.Id)}",
            SavedText = $"Saved {DateTimeText(s.SavedUtc, zone)} by {s.SavedBy}",
        };
    }

    // ---------------------------------------------------------------------------------------------------------
    // Export

    private static readonly string[] CsvColumns =
    {
        "occurred_utc", "recorded_utc", "factory", "factory_agent", "factory_agent_version", "outcome", "what",
        "subject", "session_id", "link", "actor", "id", "corrects_id",
    };

    /// <summary>
    /// The filtered rows as CSV - every row, never the collapsed lines, so the export is the record itself. A
    /// cell that a spreadsheet would run as a formula is prefixed with an apostrophe.
    /// </summary>
    public static string Csv(IReadOnlyList<FactoryActivityDto> rows, FactoryFilter filter)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", CsvColumns)).Append("\r\n");
        foreach (var r in Filtered(rows, filter).OrderBy(r => r.OccurredUtc).ThenBy(r => r.RecordedUtc))
        {
            var cells = new[]
            {
                r.OccurredUtc.ToString("o", CultureInfo.InvariantCulture), r.RecordedUtc.ToString("o", CultureInfo.InvariantCulture),
                r.Factory, r.FactoryAgent, r.FactoryAgentVersion, r.Outcome, r.What, r.Subject, r.SessionId, r.Link,
                r.Actor, r.Id.ToString(), r.CorrectsId?.ToString(),
            };
            sb.Append(string.Join(",", cells.Select(CsvCell))).Append("\r\n");
        }
        return sb.ToString();
    }

    internal static string CsvCell(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && (v[0] == '=' || v[0] == '+' || v[0] == '-' || v[0] == '@' || v[0] == '\t' || v[0] == '\r'))
            v = "'" + v;
        return v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Screen 6: the chip on a session

    /// <summary>
    /// Stamp the "factory agent" chip on every row of the roster: set on a session the record says a factory agent
    /// started, and ASSIGNED null on every other, so a Director's echo never survives.
    /// </summary>
    public static void StampChips(IReadOnlyList<SessionDto> sessions, IReadOnlyDictionary<string, FactoryActivityDto> startedBySession)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(startedBySession);
        foreach (var s in sessions)
            s.FactoryAgent = !string.IsNullOrEmpty(s.SessionId) && startedBySession.TryGetValue(s.SessionId, out var row)
                ? Chip(row)
                : null;
    }

    public static SessionFactoryAgentDto Chip(FactoryActivityDto started)
    {
        var name = WithVersion(Humanize(started.FactoryAgent), started.FactoryAgentVersion);
        var title = Title(started.Factory);
        return new SessionFactoryAgentDto
        {
            Label = "Factory agent",
            Text = $"{name} - {title}",
            Title = $"Started as the factory agent {name} of {title}. It is an ordinary session; the chip only says where it came from.",
            Href = AgentHref(started.Factory, started.FactoryAgent),
        };
    }

    // ---------------------------------------------------------------------------------------------------------
    // Shared words

    // Every trigger in fault among the factories shown, in the trigger's own words (TriggerStatusFold).
    private static List<string> TriggerFaults(FactoryFoldInputs input, FactoryFilter filter) =>
        input.Triggers
            .Where(t => t.Red && (filter.Factory is null || SameId(t.Factory, filter.Factory)))
            .Select(t => $"{Title(t.Factory)}: trigger \"{t.Name}\" - {TriggerRedText(t)}.")
            .ToList();

    private static string TriggerRedText(FactoryTriggerFacts t) =>
        string.IsNullOrWhiteSpace(t.StatusText) ? "in fault" : t.StatusText!.Trim().TrimEnd('.');

    private static FactoryFiltersDto Filters(FactoryFoldInputs input, FactoryFilter filter)
    {
        var factories = input.Triggers.Select(t => t.Factory).Concat(input.WindowRows.Select(r => r.Factory))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(Title, StringComparer.OrdinalIgnoreCase).ToList();
        var agents = input.Triggers.Where(t => filter.Factory is null || SameId(t.Factory, filter.Factory)).Select(t => t.FactoryAgent)
            .Concat(input.WindowRows.Where(r => filter.Factory is null || SameId(r.Factory, filter.Factory)).Select(r => r.FactoryAgent))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(Humanize, StringComparer.OrdinalIgnoreCase).ToList();

        var dto = new FactoryFiltersDto { Factory = filter.Factory, Agent = filter.Agent, Outcome = filter.Outcome };
        dto.FactoryChoices.Add(new() { Value = "", Label = "Every factory" });
        dto.FactoryChoices.AddRange(factories.Select(f => new FactoryChoiceDto { Value = f, Label = Title(f) }));
        dto.AgentChoices.Add(new() { Value = "", Label = "Every factory agent" });
        dto.AgentChoices.AddRange(agents.Select(a => new FactoryChoiceDto { Value = a, Label = Humanize(a) }));
        dto.OutcomeChoices.Add(new() { Value = "", Label = "Every outcome" });
        dto.OutcomeChoices.AddRange(FactoryActivityOutcome.All.Select(o => new FactoryChoiceDto { Value = o, Label = OutcomeSentenceCase(o) }));
        return dto;
    }

    /// <summary>Validate a filter's outcome word: an unknown word is refused, never matched to nothing.</summary>
    public static FactoryFilter NormaliseFilter(string? factory, string? agent, string? outcome)
    {
        static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var o = Blank(outcome)?.ToLowerInvariant();
        if (o is not null && !FactoryActivityOutcome.All.Contains(o, StringComparer.Ordinal))
            throw new FactoryViewValidationException(
                $"'{outcome}' is not a factory activity outcome. Allowed: {string.Join(", ", FactoryActivityOutcome.All)}.");
        return new FactoryFilter(Blank(factory), Blank(agent), o);
    }

    private static IEnumerable<FactoryActivityDto> Filtered(IEnumerable<FactoryActivityDto> rows, FactoryFilter f) =>
        rows.Where(r => (f.Factory is null || SameId(r.Factory, f.Factory))
                        && (f.Agent is null || SameId(r.FactoryAgent, f.Agent))
                        && (f.Outcome is null || Is(r, f.Outcome)));

    private static IReadOnlyList<FactoryActivityDto> AllCorrections(FactoryFoldInputs input) =>
        input.Corrections.Concat(input.WindowRows.Where(r => r.CorrectsId is not null)).ToList();

    private static List<string> FactoryIds(FactoryFoldInputs input, IReadOnlyList<FactoryActivityDto> open) =>
        input.Triggers.Select(t => t.Factory)
            .Concat(input.WindowRows.Select(r => r.Factory))
            .Concat(open.Select(r => r.Factory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static FactoryPauseDto? PauseFor(IReadOnlyList<FactoryTriggerFacts> triggers, string what, string noun)
    {
        if (triggers.Count == 0) return null;
        if (triggers.All(t => t.Paused))
            return new FactoryPauseDto { Action = "resume", Label = $"Resume {noun}", BusyLabel = "Resuming..." };
        return new FactoryPauseDto
        {
            Action = "pause",
            Label = $"Pause {noun}",
            BusyLabel = "Pausing...",
            Confirm = $"Pause {what}? {(triggers.Count == 1 ? "Its trigger keeps" : $"Its {triggers.Count} triggers keep")} checking and "
                      + "record \"paused\" each time, and nothing is started until you resume it.",
        };
    }

    public static string WaitingHref(string factory) =>
        $"/factory-agents/waiting?factory={Uri.EscapeDataString(factory)}";

    /// <summary>The Activity tab filtered to one factory and outcome, over the same window the card counted.</summary>
    public static string ActivityHrefFor(string factory, string outcome, FactoryWindow window)
    {
        var href = $"/factory-agents?tab=activity&factory={Uri.EscapeDataString(factory)}&outcome={Uri.EscapeDataString(outcome)}&window={window.Key}";
        if (window.Key == WindowCustom)
            href += $"&from={Uri.EscapeDataString(window.FromUtc.ToString("o", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(window.ToUtc.ToString("o", CultureInfo.InvariantCulture))}";
        return href;
    }

    public static string AgentHref(string factory, string agent) =>
        $"/factory-agents/{Uri.EscapeDataString(factory)}/{Uri.EscapeDataString(agent)}";

    private static string TriggerText(FactoryTriggerFacts t) =>
        $"Trigger: {t.Name}, every {IntervalText(t.IntervalSeconds)}{(t.Paused ? " (paused)" : "")}";

    public static string IntervalText(int seconds)
    {
        if (seconds < 60) return $"{seconds} s";
        if (seconds % 3600 == 0) return seconds == 3600 ? "hour" : $"{seconds / 3600} hours";
        var minutes = seconds / 60;
        return minutes == 1 ? "minute" : $"{minutes} min";
    }

    private static string AgentName(string agent, IReadOnlyList<FactoryActivityDto> rows)
    {
        var version = rows.Where(r => !string.IsNullOrWhiteSpace(r.FactoryAgentVersion))
            .OrderByDescending(r => r.OccurredUtc).Select(r => r.FactoryAgentVersion).FirstOrDefault();
        return WithVersion(Humanize(agent), version);
    }

    private static string WithVersion(string name, string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return name;
        var v = version.Trim();
        return v.StartsWith('v') || v.StartsWith('V') ? $"{name} {v}" : $"{name} v{v}";
    }

    /// <summary>The words shown for an id: "website-business" becomes "Website Business".</summary>
    public static string Humanize(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var words = id.Trim().Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    public static string Title(string factory) => Humanize(factory);

    public static string OutcomeWord(string outcome) => outcome switch
    {
        FactoryActivityOutcome.SentBack => "SENT BACK",
        FactoryActivityOutcome.NothingToDo => "NOTHING TO DO",
        _ => outcome.ToUpperInvariant(),
    };

    private static string OutcomeLower(string outcome) => outcome switch
    {
        FactoryActivityOutcome.SentBack => "sent back",
        FactoryActivityOutcome.NothingToDo => "nothing to do",
        _ => outcome,
    };

    private static string OutcomeSentenceCase(string outcome)
    {
        var lower = OutcomeLower(outcome);
        return char.ToUpperInvariant(lower[0]) + lower[1..];
    }

    public static string OutcomeTone(string outcome) => outcome switch
    {
        FactoryActivityOutcome.Started => FactoryTone.Blue,
        FactoryActivityOutcome.Allowed => FactoryTone.Ok,
        FactoryActivityOutcome.Done => FactoryTone.Ok,
        FactoryActivityOutcome.Asked => FactoryTone.Amber,
        FactoryActivityOutcome.Escalated => FactoryTone.Amber,
        FactoryActivityOutcome.SentBack => FactoryTone.Amber,
        FactoryActivityOutcome.Blocked => FactoryTone.Red,
        FactoryActivityOutcome.Failed => FactoryTone.Red,
        FactoryActivityOutcome.Paused => FactoryTone.Paused,
        _ => FactoryTone.Grey,
    };

    private static string? SessionLabel(string? sessionId) =>
        string.IsNullOrEmpty(sessionId) ? null : "#" + (sessionId.Length > 8 ? sessionId[..8] : sessionId);

    // An empty check a trigger wrote: the outcome "nothing to do" and an actor naming a trigger. Only these collapse;
    // a "nothing to do" a session or a person wrote is its own line.
    private static bool IsEmptyTriggerCheck(FactoryActivityDto r) =>
        Is(r, FactoryActivityOutcome.NothingToDo) && r.Actor.StartsWith(TriggerService.ActorPrefix, StringComparison.Ordinal);

    private static bool Is(FactoryActivityDto r, string outcome) => string.Equals(r.Outcome, outcome, StringComparison.Ordinal);

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static DateTime AsUtc(DateTime t, TimeZoneInfo? zoneForUnspecified = null) => t.Kind switch
    {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => zoneForUnspecified is null
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc)
            : TimeZoneInfo.ConvertTimeToUtc(t, zoneForUnspecified),
    };

    private static DateTime Local(DateTime utc, TimeZoneInfo zone) => TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), zone);

    /// <summary>"21 Sep 05:30".</summary>
    public static string DateTimeText(DateTime utc, TimeZoneInfo zone) =>
        Local(utc, zone).ToString("d MMM HH:mm", CultureInfo.InvariantCulture);

    // A time inside a view: the clock alone for a window of a day or less, with the date for a longer one.
    private static string ClockText(DateTime utc, FactoryWindow window, TimeZoneInfo zone) =>
        window.ToUtc - window.FromUtc <= TimeSpan.FromHours(24)
            ? Local(utc, zone).ToString("HH:mm", CultureInfo.InvariantCulture)
            : DateTimeText(utc, zone);
}
