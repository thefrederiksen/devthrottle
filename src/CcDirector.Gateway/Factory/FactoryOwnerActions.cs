using System.Globalization;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory;

/// <summary>An owner action whose confirm no longer describes what it would do (a 409 at the route): the count or
/// the schedules changed after the owner read the confirm. Nothing is done.</summary>
public sealed class FactoryOwnerActionConflictException : Exception
{
    public FactoryOwnerActionConflictException(string message) : base(message) { }
}

/// <summary>
/// THE OWNER'S ACTIONS ON A FACTORY, DECIDED ONCE (Factories screen mission, round 2; critical rule 7). Pure: it reads
/// the registry entry, the open waiting items and the schedules it is handed and returns the finished confirm, or the
/// rows and schedule switches an action makes. The routes only read, call this, and write what it returns.
///
///  - THE WAITING ORDER. Decisions (escalations) before questions (asked), and newest first within each - the item
///    that most needs the owner and is freshest is at the top, and a stale one sinks instead of burying a new one.
///    Nothing is ever hidden or expired: an item leaves only when a row marks it handled.
///  - MARK EVERYTHING OLDER THAN 7 DAYS AS HANDLED. The confirm states the count the Gateway counted and the cut-off,
///    and the request must send both back unchanged: a different count is refused, so the owner never marks more or
///    fewer than the confirm said. Each item gets its own handled row (the same correcting row "Handled" writes) and
///    one more row records the owner's act - who, when, how many - written together or not at all.
///  - ARCHIVE. The factory leaves the list. The Gateway schedules its registry seats name and that are on are switched
///    off, and named in the confirm; the ones already off are named as left off. History, memory and the registry
///    entry are kept. RESTORE switches back on only the schedules the archive switched off - never one somebody else
///    switched off - and says which.
/// </summary>
public static class FactoryOwnerActions
{
    public const string HandledOlder = "handled-older";
    public const string Archive = "archive";
    public const string Restore = "restore";

    /// <summary>How old an item must be for the bulk "mark handled".</summary>
    public static readonly TimeSpan BulkAge = TimeSpan.FromDays(7);

    /// <summary>The factory agent an owner's own act is recorded under in the activity record.</summary>
    public const string OwnerAgent = "owner";

    public const string WaitingOrderText =
        "Decisions first, then questions; newest first in each. An item stays here until it is marked handled.";

    public const string HandledLabel = "Handled";
    public const string HandledBusyLabel = "Marking it handled...";

    // ---------------------------------------------------------------------------------------------------------
    // Waiting on you

    /// <summary>Decisions before questions, newest first within each; the id settles a tie so the order is stable.</summary>
    public static List<FactoryActivityDto> WaitingOrder(IEnumerable<FactoryActivityDto> open) =>
        open.OrderBy(r => r.Outcome == FactoryActivityOutcome.Escalated ? 0 : 1)
            .ThenByDescending(r => r.OccurredUtc)
            .ThenBy(r => r.Id)
            .ToList();

    /// <summary>The open items the bulk action marks: those that happened before the cut-off.</summary>
    public static List<FactoryActivityDto> OlderThan(IEnumerable<FactoryActivityDto> open, DateTime cutoffUtc) =>
        open.Where(r => r.OccurredUtc < cutoffUtc).ToList();

    /// <summary>The bulk action and its confirm, or null when no open item is older than 7 days.</summary>
    public static FactoryOwnerActionDto? BulkHandledAction(RegisteredFactoryDto f, IReadOnlyList<FactoryActivityDto> open,
        TimeZoneInfo zone, DateTime nowUtc)
    {
        var cutoff = nowUtc - BulkAge;
        var count = OlderThan(open, cutoff).Count;
        if (count == 0) return null;
        var items = Count(count, "item");
        return new FactoryOwnerActionDto
        {
            Action = HandledOlder,
            FactoryId = f.Factory,
            Label = "Mark everything older than 7 days as handled",
            BusyLabel = $"Marking {items} handled...",
            ConfirmTitle = $"Mark {items} handled?",
            ConfirmLines =
            {
                $"This marks {count} {(count == 1 ? "item" : "items")} waiting on you from {f.Title} as handled: every one from before {Stamp(cutoff, zone)}, more than 7 days ago.",
                "Each one gets its own handled row in the activity record, and one more row records that you did this, and how many.",
                "Nothing is deleted. Items from the last 7 days stay on the list.",
            },
            ConfirmLabel = $"Mark {count} handled",
            CutoffUtc = cutoff,
            ExpectedCount = count,
        };
    }

    /// <summary>"Nothing here is older than 7 days." when there are items but none that old, else null.</summary>
    public static string? BulkHandledNote(IReadOnlyList<FactoryActivityDto> open, DateTime nowUtc) =>
        open.Count > 0 && OlderThan(open, nowUtc - BulkAge).Count == 0 ? "Nothing here is older than 7 days." : null;

    /// <summary>
    /// The rows the bulk "mark handled" writes: one correcting row per item older than the cut-off, then the owner's
    /// own row. Refused unless the request sends back the cut-off and count the confirm showed, the cut-off is at
    /// least 7 days ago, and the count is still the same.
    /// </summary>
    public static List<AppendFactoryActivityRequest> BulkHandledRows(RegisteredFactoryDto f, IReadOnlyList<FactoryActivityDto> open,
        IReadOnlyList<FactoryActivityDto> corrections, bool correctionsTruncated, FactoryOwnerActionRequest? request,
        string actor, TimeZoneInfo zone, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(f);
        ArgumentNullException.ThrowIfNull(open);
        if (request?.CutoffUtc is not { } rawCutoff || request.ExpectedCount is not { } expected)
            throw new FactoryViewValidationException("The cut-off and the count the confirm showed are missing. Open the factory again and use its button.");
        var cutoff = DateTime.SpecifyKind(rawCutoff.ToUniversalTime(), DateTimeKind.Utc);
        if (cutoff > nowUtc - BulkAge)
            throw new FactoryViewValidationException("Only items older than 7 days can be marked handled together. Nothing was marked.");
        if (correctionsTruncated)
            throw new FactoryViewValidationException("The record holds more correcting rows than one read returns, so it cannot be told which items are already handled. Nothing was marked.");

        var items = OlderThan(open, cutoff);
        if (items.Count != expected)
            throw new FactoryOwnerActionConflictException(
                $"The confirm said {Count(expected, "item")}, and there are now {items.Count} from before {Stamp(cutoff, zone)}. Nothing was marked. Open the factory again to see the new count.");
        if (items.Count == 0)
            throw new FactoryViewValidationException("No item waiting on you is older than 7 days. Nothing was marked.");

        var why = "in a bulk clear of everything older than 7 days";
        var rows = items.Select(r => FactoryAgentsFold.HandledRow(r, corrections, correctionsTruncated: false, actor, nowUtc, why)).ToList();
        rows.Add(new AppendFactoryActivityRequest
        {
            Factory = f.Factory,
            FactoryAgent = OwnerAgent,
            What = $"The owner marked {Count(items.Count, "item")} waiting on you as handled: everything from before {Stamp(cutoff, zone)}.",
            Outcome = FactoryActivityOutcome.Done,
            Actor = actor,
            OccurredUtc = nowUtc,
        });
        return rows;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Archive and Restore

    /// <summary>
    /// Which schedules an archive or a restore switches. <paramref name="Switch"/> are the ones it changes,
    /// <paramref name="Unchanged"/> the ones already the way it wants them, <paramref name="Missing"/> the ids the
    /// Gateway no longer holds.
    /// </summary>
    public sealed record SchedulePlan(List<CronJobDto> Switch, List<CronJobDto> Unchanged, List<string> Missing)
    {
        public List<string> SwitchIds => Switch.Select(j => j.Id).ToList();
    }

    /// <summary>The schedule ids a factory's registry seats name, each once, in seat order.</summary>
    public static List<string> SeatScheduleIds(RegisteredFactoryDto f) =>
        f.Seats.SelectMany(s => s.Schedules).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Archive switches off every seat schedule that is on.</summary>
    public static SchedulePlan ArchivePlan(RegisteredFactoryDto f, IReadOnlyList<CronJobDto> schedules) =>
        Plan(SeatScheduleIds(f), schedules, wantEnabled: false);

    /// <summary>Restore switches back on every schedule the archive switched off that is still off.</summary>
    public static SchedulePlan RestorePlan(RegisteredFactoryDto f, IReadOnlyList<CronJobDto> schedules) =>
        Plan(f.ArchivedSchedules, schedules, wantEnabled: true);

    private static SchedulePlan Plan(IReadOnlyList<string> ids, IReadOnlyList<CronJobDto> schedules, bool wantEnabled)
    {
        var change = new List<CronJobDto>();
        var unchanged = new List<CronJobDto>();
        var missing = new List<string>();
        foreach (var id in ids)
        {
            var job = schedules.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.Ordinal));
            if (job is null) missing.Add(id);
            else if (job.Enabled == wantEnabled) unchanged.Add(job);
            else change.Add(job);
        }
        return new SchedulePlan(change, unchanged, missing);
    }

    public static FactoryOwnerActionDto ArchiveAction(RegisteredFactoryDto f, IReadOnlyList<CronJobDto> schedules)
    {
        var plan = ArchivePlan(f, schedules);
        var lines = new List<string>
        {
            $"{f.Title} leaves the Factories list. It stays under Show archived, where you can restore it.",
        };
        if (plan.Switch.Count == 0 && plan.Unchanged.Count == 0 && plan.Missing.Count == 0)
            lines.Add("It has no Gateway schedule, so no schedule is switched off.");
        else
        {
            lines.Add(plan.Switch.Count == 0
                ? "None of its Gateway schedules is on, so no schedule is switched off."
                : $"{(plan.Switch.Count == 1 ? "This Gateway schedule is" : "These Gateway schedules are")} switched off: {Names(plan.Switch)}.");
            if (plan.Unchanged.Count > 0)
                lines.Add($"Already off, and left off: {Names(plan.Unchanged)}.");
            if (plan.Missing.Count > 0)
                lines.Add($"Named by its seats but not on the Gateway, so nothing to switch: {string.Join(", ", plan.Missing)}.");
        }
        lines.Add("All its history, its memory and its registry entry are kept.");
        lines.Add("This is recorded in its activity record as your act.");
        return new FactoryOwnerActionDto
        {
            Action = Archive,
            FactoryId = f.Factory,
            Label = "Archive factory",
            BusyLabel = $"Archiving {f.Title}...",
            ConfirmTitle = $"Archive {f.Title}?",
            ConfirmLines = lines,
            ConfirmLabel = $"Archive {f.Title}",
            Schedules = plan.SwitchIds,
        };
    }

    public static FactoryOwnerActionDto RestoreAction(RegisteredFactoryDto f, IReadOnlyList<CronJobDto> schedules)
    {
        var plan = RestorePlan(f, schedules);
        var lines = new List<string> { $"{f.Title} returns to the Factories list." };
        if (f.ArchivedSchedules.Count == 0)
            lines.Add("The archive switched no schedule off, so none is switched on.");
        else
        {
            lines.Add(plan.Switch.Count == 0
                ? "Every schedule the archive switched off is already on again, so none is switched on."
                : $"{(plan.Switch.Count == 1 ? "This schedule the archive switched off is" : "These schedules the archive switched off are")} switched back on: {Names(plan.Switch)}.");
            if (plan.Unchanged.Count > 0)
                lines.Add($"Already on again: {Names(plan.Unchanged)}.");
            if (plan.Missing.Count > 0)
                lines.Add($"No longer on the Gateway, so not switched on: {string.Join(", ", plan.Missing)}.");
        }
        lines.Add("Schedules the archive did not switch off are not touched.");
        lines.Add("This is recorded in its activity record as your act.");
        return new FactoryOwnerActionDto
        {
            Action = Restore,
            FactoryId = f.Factory,
            Label = "Restore",
            BusyLabel = $"Restoring {f.Title}...",
            ConfirmTitle = $"Restore {f.Title}?",
            ConfirmLines = lines,
            ConfirmLabel = $"Restore {f.Title}",
            Schedules = plan.SwitchIds,
        };
    }

    /// <summary>Refuse when the schedules the request carries are not the ones the plan would switch now: the owner
    /// read a confirm that no longer describes the action.</summary>
    public static void RequireSameSchedules(SchedulePlan plan, FactoryOwnerActionRequest? request, string verb)
    {
        if (request?.Schedules is not { } sent)
            throw new FactoryViewValidationException($"The schedules the confirm named are missing. Open the factory again and use its {verb} button.");
        var now = plan.SwitchIds.ToHashSet(StringComparer.Ordinal);
        if (!now.SetEquals(sent))
            throw new FactoryOwnerActionConflictException(
                $"The schedules changed after the confirm was shown: it named {Ids(sent)}, and now it would be {Ids(now)}. Nothing was done. Open it again to see what {verb} does now.");
    }

    /// <summary>The owner's row for an archive.</summary>
    public static AppendFactoryActivityRequest ArchiveRow(RegisteredFactoryDto f, IReadOnlyList<CronJobDto> switchedOff, string actor, DateTime nowUtc) => new()
    {
        Factory = f.Factory,
        FactoryAgent = OwnerAgent,
        What = Clip($"The owner archived {f.Title}: it left the Factories list; " +
                    (switchedOff.Count == 0 ? "no schedule was switched off" : $"schedules switched off: {Names(switchedOff)}") +
                    ". History, memory and the registry entry are kept."),
        Outcome = FactoryActivityOutcome.Done,
        Actor = actor,
        OccurredUtc = nowUtc,
    };

    /// <summary>The owner's row for a restore.</summary>
    public static AppendFactoryActivityRequest RestoreRow(RegisteredFactoryDto f, IReadOnlyList<CronJobDto> switchedOn, string actor, DateTime nowUtc) => new()
    {
        Factory = f.Factory,
        FactoryAgent = OwnerAgent,
        What = Clip($"The owner restored {f.Title} to the Factories list; " +
                    (switchedOn.Count == 0 ? "no schedule was switched back on." : $"schedules switched back on: {Names(switchedOn)}.")),
        Outcome = FactoryActivityOutcome.Done,
        Actor = actor,
        OccurredUtc = nowUtc,
    };

    /// <summary>"Archived 6 Oct 23:50 by the owner".</summary>
    public static string ArchivedText(RegisteredFactoryDto f, TimeZoneInfo zone) =>
        f.ArchivedAtUtc is { } at ? $"Archived {Stamp(at, zone)} by the owner" : "";

    /// <summary>"\"WarmForward Factory - Nora Hale - morning run\" (cj_a721e6) and ...".</summary>
    public static string Names(IReadOnlyList<CronJobDto> jobs) =>
        Join(jobs.Select(j => string.IsNullOrWhiteSpace(j.Name) ? j.Id : $"\"{j.Name}\" ({j.Id})").ToList());

    private static string Ids(IEnumerable<string> ids)
    {
        var list = ids.OrderBy(i => i, StringComparer.Ordinal).ToList();
        return list.Count == 0 ? "no schedule" : Join(list);
    }

    private static string Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };

    /// <summary>"29 Sep 23:50", in the account's zone.</summary>
    internal static string Stamp(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).ToString("d MMM HH:mm", CultureInfo.InvariantCulture);

    private static string Clip(string what) =>
        what.Length <= FactoryActivityRecord.MaxWhatChars ? what : what[..(FactoryActivityRecord.MaxWhatChars - 3)] + "...";

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
