using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Definition;

namespace CcDirector.Gateway.Factory.Triggers;

/// <summary>
/// EVERY CHECKED START OF A DEFINED FACTORY, AND THE STATE ITS CHECK KEEPS (Factory Definition mission, phase 5b, design
/// 1.4) - what <c>cc-devthrottle factory check-state show</c> prints and what a reset is aimed with. Pure: the published
/// definition, the factory's live triggers and a reader of the newest state go in; one row per checked start comes out.
///
/// A START IS NAMED BY ITS SEAT AND ITS NUMBER: its place in that seat's <c>starts:</c> list, counting EVERY start from
/// 1 - the number in its trigger's name (<see cref="FactoryPublishPlan.TriggerName"/>) and the words a person editing
/// the definition already uses. The live trigger that runs it is found with the one pairing a publish uses
/// (<see cref="FactoryDefinitionChanges.PairStarts"/>), so the number a person reads here is the start the publish
/// wrote that trigger from.
/// </summary>
public static class FactoryCheckStateFold
{
    /// <summary>
    /// One row per checked start of <paramref name="definition"/>'s factory that a live row runs - a definition trigger
    /// for an interval start, a schedule with a check for a timed one (owner key <c>cron:&lt;schedule id&gt;</c>) - by
    /// seat id and start number. <paramref name="definition"/> is the published definition, or null when there is none:
    /// then there are no checked starts and the list is empty.
    /// </summary>
    public static List<FactoryCheckStateDto> List(FactoryDefinition? definition, IReadOnlyList<TriggerEntity> liveTriggers,
        IReadOnlyList<CronJobDto> liveSchedules, Func<string, FactoryCheckStateEntity?> newestOf)
    {
        ArgumentNullException.ThrowIfNull(liveTriggers);
        ArgumentNullException.ThrowIfNull(liveSchedules);
        ArgumentNullException.ThrowIfNull(newestOf);
        var rows = new List<FactoryCheckStateDto>();
        if (definition is null) return rows;

        foreach (var seat in definition.Seats.OrderBy(s => s.Seat, StringComparer.Ordinal))
        {
            // The seat's checked interval starts, by their place in the whole list: an interval start is the one kind a
            // trigger runs, and it always has a check.
            var interval = Enumerable.Range(0, seat.Starts.Count)
                .Where(i => seat.Starts[i].Check is not null && seat.Starts[i].Cron is null).ToList();
            var live = liveTriggers
                .Where(t => t.IsDefinitionCheck && string.Equals(t.Factory, definition.Factory, StringComparison.Ordinal)
                            && t.Seat == seat.Seat)
                .OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Id).ToList();
            var pairing = FactoryDefinitionChanges.PairStarts(live.Select(FactoryPublishPlan.AsStart).ToList(),
                interval.Select(i => seat.Starts[i]).ToList());
            foreach (var pair in pairing.Pairs.OrderBy(p => interval[p.New]))
            {
                var trigger = live[pair.Old];
                var number = interval[pair.New] + 1;
                rows.Add(Row(trigger, seat.Seat, number, newestOf(FactoryCheckStateStore.TriggerOwner(trigger.Id))));
            }

            // The seat's checked TIMED starts (phase 5b, design 3.3), paired with its schedules the way a publish pairs
            // them; only a schedule that carries a check keeps a state.
            var timed = Enumerable.Range(0, seat.Starts.Count)
                .Where(i => seat.Starts[i].Cron is not null).ToList();
            var schedules = liveSchedules
                .Where(j => string.Equals(j.Factory, definition.Factory, StringComparison.Ordinal) && j.Seat == seat.Seat)
                .OrderBy(j => j.Id, StringComparer.Ordinal).ToList();
            var timedPairing = FactoryDefinitionChanges.PairStarts(schedules.Select(AsStart).ToList(),
                timed.Select(i => seat.Starts[i]).ToList());
            foreach (var pair in timedPairing.Pairs.OrderBy(p => timed[p.New]))
            {
                var job = schedules[pair.Old];
                if (!job.HasCheck || seat.Starts[timed[pair.New]].Check is null) continue;
                rows.Add(Row(job, seat.Seat, timed[pair.New] + 1, newestOf(FactoryCheckStateStore.ScheduleOwner(job.Id))));
            }
        }
        rows = rows.OrderBy(r => r.Seat, StringComparer.Ordinal).ThenBy(r => r.Start).ToList();
        FileLog.Write($"[FactoryCheckStateFold] List: factory={definition.Factory}, checked starts with a trigger={rows.Count}");
        return rows;
    }

    // A live schedule as a start, the way the publish plan reads one, so the pairing here is the publish's.
    private static SeatStartDefinition AsStart(CronJobDto job)
        => new(job.CronExpression ?? job.RunAt ?? "", job.TimeZoneId, null, null, job.CheckPath,
            (IReadOnlyList<string>?)job.CheckSecrets ?? Array.Empty<string>(), job.Action.Seed ?? "", job.PreventOverlap);

    private static FactoryCheckStateDto Row(CronJobDto job, string seat, int number, FactoryCheckStateEntity? newest) => new()
    {
        OwnerKey = FactoryCheckStateStore.ScheduleOwner(job.Id),
        Seat = seat,
        Start = number,
        Name = job.Name,
        CheckPath = job.CheckPath!,
        Content = newest?.Content ?? "",
        WrittenUtc = newest is null ? null : DateTime.SpecifyKind(newest.WrittenUtc, DateTimeKind.Utc),
        WrittenBy = newest?.WrittenBy,
        RunRef = newest?.RunRef,
        Reset = newest?.Reset ?? false,
    };

    private static FactoryCheckStateDto Row(TriggerEntity trigger, string seat, int number, FactoryCheckStateEntity? newest) => new()
    {
        OwnerKey = FactoryCheckStateStore.TriggerOwner(trigger.Id),
        Seat = seat,
        Start = number,
        Name = trigger.Name,
        CheckPath = trigger.CheckPath!,
        Content = newest?.Content ?? "",
        WrittenUtc = newest is null ? null : DateTime.SpecifyKind(newest.WrittenUtc, DateTimeKind.Utc),
        WrittenBy = newest?.WrittenBy,
        RunRef = newest?.RunRef,
        Reset = newest?.Reset ?? false,
    };
}
