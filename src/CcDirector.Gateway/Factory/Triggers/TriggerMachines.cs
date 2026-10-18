using System.Text.Json;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Factory.Triggers;

/// <summary>
/// THE MACHINES A TRIGGER'S CHECK RUNS ON, in order (Mail Desk, phase 5): its own machine first, then its backup
/// machines in the order the start names them. Position 0 is the trigger's own machine; a Director on an earlier
/// position takes the claim from one on a later position (<see cref="TriggerStore.ClaimForDirector"/>). Machine names
/// are compared ignoring case, as everywhere a trigger's machine is.
///
/// The words for the list are composed here, once, so every surface that shows where a trigger runs - the trigger
/// list, the trigger page, the publish's "what changes" - says the same thing (the Gateway rules; the client renders).
/// </summary>
public static class TriggerMachines
{
    /// <summary>The backup machines a trigger row holds, in order; empty when it has none.</summary>
    public static IReadOnlyList<string> BackupsOf(TriggerEntity trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        return Parse(trigger.BackupMachinesJson, trigger.Name);
    }

    /// <summary>The backup machines as the row stores them: a JSON array, <c>[]</c> when there are none.</summary>
    public static string ToJson(IReadOnlyList<string> backups)
    {
        ArgumentNullException.ThrowIfNull(backups);
        return JsonSerializer.Serialize(backups);
    }

    /// <summary>Every machine the check runs on, in order: the trigger's own, then its backups.</summary>
    public static IReadOnlyList<string> AllOf(TriggerEntity trigger)
        => new[] { trigger.Machine }.Concat(BackupsOf(trigger)).ToList();

    /// <summary>Where <paramref name="machine"/> stands in the trigger's list - 0 for its own machine - or -1 when the
    /// machine is not on the list at all.</summary>
    public static int PositionOf(TriggerEntity trigger, string machine)
    {
        if (string.IsNullOrEmpty(machine)) return -1;
        var all = AllOf(trigger);
        for (var i = 0; i < all.Count; i++)
            if (string.Equals(all[i], machine, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// Where the check runs, in words: "machine-a" with no backups; "machine-a, then machine-b if machine-a is away";
    /// "machine-a, then machine-b if machine-a is away, then machine-c if machine-a and machine-b are away".
    /// </summary>
    public static string Text(string machine, IReadOnlyList<string> backups)
    {
        ArgumentException.ThrowIfNullOrEmpty(machine);
        ArgumentNullException.ThrowIfNull(backups);
        var text = machine;
        var before = new List<string> { machine };
        foreach (var backup in backups)
        {
            text += $", then {backup} if {And(before)} {(before.Count == 1 ? "is" : "are")} away";
            before.Add(backup);
        }
        return text;
    }

    /// <summary>The words for one trigger row's machines.</summary>
    public static string Text(TriggerEntity trigger) => Text(trigger.Machine, BackupsOf(trigger));

    private static string And(IReadOnlyList<string> names)
        => names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    private static IReadOnlyList<string> Parse(string? json, string triggerName)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<string>();
        return JsonSerializer.Deserialize<List<string>>(json)
               ?? throw new InvalidOperationException($"trigger {triggerName} holds backup machines that are not a JSON array: {json}");
    }
}
