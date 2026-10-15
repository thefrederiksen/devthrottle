using System.Globalization;
using CcDirector.Core.Agents;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>One of a factory's triggers as the export reads it: the parts a definition could hold, and what it cannot.</summary>
public sealed record LiveTrigger(
    string Name,
    string Seat,
    string Machine,
    string RepoPath,
    string CheckCommand,
    int IntervalSeconds,
    string Prompt,
    bool Paused);

/// <summary>
/// Everything the Gateway holds today about a factory that has no definition (mission decision A8): its registry
/// entry, the published brief of each seat that has one, and the schedules and triggers that name the factory.
/// </summary>
public sealed record FactoryLiveParts(
    RegisteredFactoryDto Registry,
    IReadOnlyDictionary<string, string> PublishedBriefs,
    IReadOnlyList<CronJobDto> Schedules,
    IReadOnlyList<LiveTrigger> Triggers);

/// <summary>Something the factory has today that the exported folder does not hold, and why: the file it would have
/// gone in, and one plain sentence.</summary>
public sealed record ExportReport(string File, string Message);

/// <summary>The folder an export writes, every path in its one order, and what it could not carry.</summary>
public sealed record FactoryDefinitionExportResult(
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<ExportReport> Reported);

/// <summary>
/// THE EXPORT OF A FACTORY THAT HAS NO DEFINITION (Factory Definition, phase 3; mission 5.4 and decision A8). The
/// owner recreates each factory by importing a folder (decision D7), so the first folder is written from what is live:
/// the registry entry gives <c>factory.yaml</c> and one <c>seats/&lt;seat&gt;.yaml</c> per seat, each seat's published
/// brief its <c>brief</c>, and each of the seat's recurring schedules one start.
///
/// WHATEVER CANNOT BE EXPRESSED IS REPORTED, NEVER SILENTLY DROPPED. A definition has no place for the factory's
/// folder, a goal held in a file, a working folder per schedule, a one-off or random schedule, a switched-off
/// schedule, a work list, a notification, a schedule that stays open, a schedule on another machine than its seat, or
/// a trigger whose check is a command rather than a Python file in the definition. Each is named in
/// <see cref="FactoryDefinitionExportResult.Reported"/> with the file it would have gone in. A seat with no published
/// brief is written with an empty brief and reported, so the import's check names the file and line to fill rather
/// than the export inventing text.
///
/// The folder is NOT checked here and may not pass the check: it is what is live, written down. Importing it is where
/// the check runs. Pure: the same parts give the same bytes. Seats, schedules and triggers are taken in ordinal order
/// of their ids, so the order the stores return them in changes nothing.
/// </summary>
public static class FactoryDefinitionExport
{
    /// <summary>The agent every factory seat runs today: both starters launch Claude Code, and nothing records an
    /// agent per seat (mission section 2).</summary>
    public const AgentKind AgentToday = AgentKind.ClaudeCode;

    private const string RecurringKind = "recurring";

    /// <summary>Write the folder for a factory with no definition, from what the Gateway holds today.</summary>
    public static FactoryDefinitionExportResult FromLive(FactoryLiveParts live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var r = live.Registry;
        FileLog.Write($"[FactoryDefinitionExport] FromLive: factory={r.Factory}, seats={r.Seats.Count}, briefs={live.PublishedBriefs.Count}, schedules={live.Schedules.Count}, triggers={live.Triggers.Count}");

        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var reported = new List<ExportReport>();

        files[FactoryDefinitionCheck.FactoryFile] = FactoryFile(r, reported);

        var seats = r.Seats.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        var seatIds = new HashSet<string>(seats.Select(s => s.Id), StringComparer.Ordinal);
        var schedules = live.Schedules.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        var triggers = live.Triggers.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

        foreach (var seat in seats)
        {
            var path = $"seats/{seat.Id}.yaml";
            files[path] = SeatFile(r, seat, live.PublishedBriefs, schedules.Where(s => s.Seat == seat.Id).ToList(), path, reported);
            foreach (var trigger in triggers.Where(t => t.Seat == seat.Id))
                reported.Add(new ExportReport(path, TriggerSentence(trigger)));
        }

        // What names the factory but no seat it has: it has no file to go in.
        foreach (var schedule in schedules.Where(s => s.Seat is null || !seatIds.Contains(s.Seat)))
            reported.Add(new ExportReport(FactoryDefinitionCheck.SeatsPlace,
                $"The schedule {Named(schedule)} names {(schedule.Seat is null ? "no seat" : $"the seat '{schedule.Seat}', which the factory does not have")}, so it is not in the folder. A start belongs to a seat."));
        foreach (var trigger in triggers.Where(t => !seatIds.Contains(t.Seat)))
            reported.Add(new ExportReport(FactoryDefinitionCheck.SeatsPlace,
                $"The trigger '{trigger.Name}' names the factory agent '{trigger.Seat}', which is not a seat of the factory, so it is not in the folder. {TriggerSentence(trigger)}"));

        // The registry's own list of a seat's schedules, against the schedules that exist.
        var existing = new HashSet<string>(schedules.Select(s => s.Id), StringComparer.Ordinal);
        foreach (var seat in seats)
            foreach (var id in seat.Schedules.Where(id => !existing.Contains(id)).OrderBy(id => id, StringComparer.Ordinal))
                reported.Add(new ExportReport($"seats/{seat.Id}.yaml",
                    $"The registry lists the schedule '{id}' for this seat, but no schedule of this factory has that id on the Gateway, so it is not a start."));

        var ordered = reported
            .Select((report, index) => (report, index))
            .OrderBy(x => x.report.File, StringComparer.Ordinal)
            .ThenBy(x => x.index)
            .Select(x => x.report)
            .ToList();
        FileLog.Write($"[FactoryDefinitionExport] FromLive: factory={r.Factory}, files={files.Count}, reported={ordered.Count}");
        return new FactoryDefinitionExportResult(files, ordered);
    }

    private static string FactoryFile(RegisteredFactoryDto r, List<ExportReport> reported)
    {
        const string path = FactoryDefinitionCheck.FactoryFile;
        var y = new DefinitionYamlWriter();
        y.Scalar(0, "factory", r.Factory);
        y.Scalar(0, "title", r.Title);
        if (!string.IsNullOrWhiteSpace(r.Purpose)) y.Scalar(0, "purpose", r.Purpose);

        var hasGoal = !string.IsNullOrWhiteSpace(r.GoalText);
        if (hasGoal) y.Scalar(0, "goalText", r.GoalText!);
        if (!string.IsNullOrWhiteSpace(r.GoalApprovedOn))
        {
            if (hasGoal)
                y.Scalar(0, "goalApprovedOn", r.GoalApprovedOn!);
            else
                reported.Add(new ExportReport(path,
                    $"The goal was approved on {r.GoalApprovedOn}, but the Gateway holds no goal text, so the date is not in the folder: a definition holds the date only with its goal."));
        }
        if (!string.IsNullOrWhiteSpace(r.GoalFile))
            reported.Add(new ExportReport(path,
                $"The goal is the file '{r.GoalFile}' in the factory's folder, which the Gateway does not hold. Write it into goalText."));

        if (!string.IsNullOrWhiteSpace(r.BossSeat)) y.Scalar(0, "bossSeat", r.BossSeat!);

        y.Open(0, "defaults");
        y.Scalar(1, "machine", r.Computer);
        y.Scalar(1, "agent", AgentToday.ToString());
        reported.Add(new ExportReport(path,
            $"No agent or model is recorded for this factory today: every factory seat runs {AgentToday}, on the model its machine uses by default. The folder says agent {AgentToday} and names no model."));

        if (!string.IsNullOrWhiteSpace(r.Folder))
            reported.Add(new ExportReport(path,
                $"The factory's folder '{r.Folder}' on {r.Computer} is not part of a definition: nothing of a factory is kept in a folder of its own. The Gateway holds folders, not repository names, so no repository is listed: name the ones the seats work in under repositories."));
        if (r.ArchivedAtUtc is not null)
            reported.Add(new ExportReport(path,
                $"The factory was archived by {r.ArchivedBy} on {r.ArchivedAtUtc.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}; a definition does not say whether a factory is archived."));
        return y.ToString();
    }

    private static string SeatFile(RegisteredFactoryDto r, RegisteredFactorySeatDto seat,
        IReadOnlyDictionary<string, string> briefs, IReadOnlyList<CronJobDto> schedules, string path, List<ExportReport> reported)
    {
        var y = new DefinitionYamlWriter();
        y.Scalar(0, "seat", seat.Id);
        y.Scalar(0, "name", seat.Name);
        y.Scalar(0, "role", seat.Role);
        if (!string.IsNullOrWhiteSpace(seat.Line)) y.Scalar(0, "line", seat.Line!);
        var machine = string.IsNullOrWhiteSpace(seat.Computer) ? r.Computer : seat.Computer;
        if (!string.Equals(machine, r.Computer, StringComparison.OrdinalIgnoreCase)) y.Scalar(0, "machine", machine);

        if (briefs.TryGetValue(seat.Id, out var brief))
        {
            y.Scalar(0, "brief", brief);
        }
        else
        {
            y.Scalar(0, "brief", "");
            reported.Add(new ExportReport(path,
                $"The seat has no published brief on the Gateway; its brief is the file '{seat.BriefFile}' in the factory's folder. The brief is left empty here: write it in before importing."));
        }

        var starts = new List<CronJobDto>();
        foreach (var schedule in schedules)
        {
            if (WhyNotAStart(schedule) is { } why)
            {
                reported.Add(new ExportReport(path, $"The schedule {Named(schedule)} is not a start here: {why}"));
                continue;
            }
            starts.Add(schedule);
            foreach (var sentence in WhatAStartLeavesOut(schedule, machine))
                reported.Add(new ExportReport(path, $"The schedule {Named(schedule)} is a start here, but {sentence}"));
        }

        if (starts.Count > 0)
        {
            y.Open(0, "starts");
            foreach (var s in starts)
            {
                y.Item(1, "cron", s.CronExpression!);
                y.Scalar(2, "timeZone", s.TimeZoneId);
                y.Scalar(2, "prompt", s.Action.Seed);
                if (!s.PreventOverlap) y.Boolean(2, "preventOverlap", false);
            }
        }
        return y.ToString();
    }

    /// <summary>Why a schedule cannot be a start at all, or null when it can.</summary>
    private static string? WhyNotAStart(CronJobDto s)
    {
        if (!s.Enabled)
            return "it is switched off, and a definition has no switched-off start (publishing it would switch it on). Add it as a start when it should run.";
        if (!string.IsNullOrWhiteSpace(s.Action.WorkListName))
            return $"it drains the work list '{s.Action.WorkListName}', and a start only starts the seat with a prompt.";
        if (!string.Equals(s.ScheduleKind, RecurringKind, StringComparison.Ordinal))
            return $"it is a {s.ScheduleKind} schedule ({s.CronExpression ?? s.RunAt}), and a start is a five-field cron expression or an interval.";
        if (string.IsNullOrWhiteSpace(s.CronExpression))
            return "it has no cron expression.";
        if (string.IsNullOrWhiteSpace(s.Action.Seed))
            return "it has no prompt, and a start needs one.";
        return null;
    }

    /// <summary>What a schedule has that its start does not keep, one sentence each.</summary>
    private static IEnumerable<string> WhatAStartLeavesOut(CronJobDto s, string seatMachine)
    {
        if (!string.IsNullOrWhiteSpace(s.Target.Machine) && !string.Equals(s.Target.Machine, seatMachine, StringComparison.OrdinalIgnoreCase))
            yield return $"it runs on {s.Target.Machine} and the seat on {seatMachine}; a start runs where its seat runs.";
        if (!string.IsNullOrWhiteSpace(s.Action.RepoPath))
            yield return $"its working folder '{s.Action.RepoPath}' is not kept: a seat works in a repository named under repositories in factory.yaml, or in none.";
        if (!string.Equals(s.NotifyOn, CronNotify.None, StringComparison.Ordinal))
            yield return $"its notification ('{s.NotifyOn}'{(string.IsNullOrWhiteSpace(s.NotifyWebhookUrl) ? "" : $" to {s.NotifyWebhookUrl}")}) is not kept: a start has none.";
        if (!s.Action.AutoDismiss)
            yield return "it keeps its session open when the run ends, and a start's session always closes itself.";
    }

    private static string TriggerSentence(LiveTrigger t)
        => $"The trigger '{t.Name}'{(t.Paused ? " (paused)" : "")} is not a start here: its check is the command '{t.CheckCommand}', " +
           $"and a start's check is a Python file under checks/ in the definition. It runs every {t.IntervalSeconds} seconds on {t.Machine}" +
           $"{(string.IsNullOrWhiteSpace(t.RepoPath) ? "" : $" in '{t.RepoPath}'")} with the prompt: {t.Prompt}";

    private static string Named(CronJobDto s) => string.IsNullOrWhiteSpace(s.Name) ? $"'{s.Id}'" : $"'{s.Name}' ({s.Id})";
}
