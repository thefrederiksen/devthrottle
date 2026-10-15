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
    /// One row per definition trigger of <paramref name="definition"/>'s factory, by seat id and start number.
    /// <paramref name="definition"/> is the published definition, or null when there is none: then there are no checked
    /// starts and the list is empty.
    /// </summary>
    public static List<FactoryCheckStateDto> List(FactoryDefinition? definition, IReadOnlyList<TriggerEntity> liveTriggers,
        Func<string, FactoryCheckStateEntity?> newestOf)
    {
        ArgumentNullException.ThrowIfNull(liveTriggers);
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
        }
        FileLog.Write($"[FactoryCheckStateFold] List: factory={definition.Factory}, checked starts with a trigger={rows.Count}");
        return rows;
    }

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
