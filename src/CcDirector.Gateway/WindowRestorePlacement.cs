using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway;

/// <summary>
/// A FACTORY'S RESTORE GIVES ITS WINDOW SCHEDULES THEIR MINUTES. A save chooses a window schedule's minute whenever it
/// leaves one switched on (CronJobEndpoints), but Restore switches schedules on through the store, so without this a
/// window schedule that was never placed came back on with no next run.
///
/// Restore switches every schedule on first and calls this after, so a window that runs after another of the
/// factory's schedules finds it running. Anchors are placed before the windows that follow them. A window no minute
/// can be found for stays switched on as it was, and the answer is a sentence for the owner - never only a log line.
/// </summary>
internal static class WindowRestorePlacement
{
    public static IReadOnlyList<string> PlaceSwitchedOn(CronJobStore store, TenantId tenant, IReadOnlyList<string> switchedOnIds,
        Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, TimeSpan>> runLengthsOf, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(switchedOnIds);
        ArgumentNullException.ThrowIfNull(runLengthsOf);

        var pending = switchedOnIds
            .Select(id => store.Get(tenant, id))
            .Where(j => j is not null && j.Enabled && CronSchedule.IsWindow(j.ScheduleKind))
            .Select(j => j!)
            .ToList();
        var problems = new List<string>();
        while (pending.Count > 0)
        {
            // A window whose anchor is still waiting to be placed goes after it. When every one left waits on another
            // (a loop), the first is placed anyway and its own answer says what is wrong.
            var ids = pending.Select(j => j.Id).ToHashSet(StringComparer.Ordinal);
            var next = pending.FirstOrDefault(j => WindowSchedule.Parse(j.CronExpression).Settings?.AfterJobId is not { } a || !ids.Contains(a))
                ?? pending[0];
            pending.Remove(next);
            if (Place(store, tenant, next, runLengthsOf, nowUtc) is { } problem)
                problems.Add(problem);
        }
        return problems;
    }

    private static string? Place(CronJobStore store, TenantId tenant, CronJobDto job,
        Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, TimeSpan>> runLengthsOf, DateTime nowUtc)
    {
        if (WindowSchedule.Parse(job.CronExpression).Settings is null || CronSchedule.FindZone(job.TimeZoneId) is null)
        {
            FileLog.Write($"[WindowRestorePlacement] {job.Id} '{job.Name}': settings or time zone unreadable, not placed");
            return $"'{job.Name}' is switched on but has no time to run: its window settings or time zone cannot be read. Edit it on the Schedule page.";
        }
        var all = store.ListAll(tenant);
        var (expression, error) = WindowSchedule.PlaceAgain(job, all, runLengthsOf(all.Select(j => j.Id).ToList()), nowUtc);
        if (expression is null)
        {
            FileLog.Write($"[WindowRestorePlacement] {job.Id} '{job.Name}' could not be placed: {error}");
            return $"'{job.Name}' is switched on but could not be given a time to run: {error}.";
        }
        store.SetEnabled(tenant, job.Id, true, expression);
        FileLog.Write($"[WindowRestorePlacement] {job.Id} '{job.Name}' placed: {expression}");
        return null;
    }
}
