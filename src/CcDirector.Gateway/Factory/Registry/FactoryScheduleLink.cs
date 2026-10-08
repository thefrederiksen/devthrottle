using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory.Registry;

/// <summary>
/// THE LINK BETWEEN A SCHEDULE AND ITS FACTORY SEAT (issue #3650). The owner's rule, 2026-10-08: there are no factory
/// agents without a factory, and it must be impossible rather than merely discouraged.
///
/// Before this, a factory seat was two records with nothing joining them - the schedule that starts the session and
/// the registry's seat list the Factories screen reads - so six DevThrottle seats ran for a day while the screen
/// showed two. Now the link lives ON the schedule, as its factory id and its seat id, and this is the one rule both
/// are held to whenever a schedule is written:
///
///   - no factory and no seat: a plain scheduled job, as before;
///   - a factory needs a seat, and a seat needs its factory;
///   - the factory must be registered, and the seat must be one of its registered seats.
///
/// Anything else is refused with a sentence naming the fix, never stored and patched later. The seat therefore
/// always exists before its schedule does, and the registry's seat list is DERIVED from these links
/// (<see cref="FactoryRegistryStore"/>), so the two can never disagree.
/// </summary>
public static class FactoryScheduleLink
{
    /// <summary>The command that registers a factory's seats, named in every refusal that needs it.</summary>
    public const string RegisterCommand = "cc-devthrottle factory register --manifest <file>";

    /// <summary>
    /// Check a schedule's factory and seat against the registry. Returns null when the pair may be stored, with
    /// <paramref name="seat"/> folded to the one spelling (null for a plain job); otherwise the refusal.
    /// </summary>
    /// <param name="factory">The schedule's factory, already settled and folded by the factory-naming gate, or null.</param>
    /// <param name="requestedSeat">The seat the schedule names, as written, or null.</param>
    /// <param name="find">The account's registration of a factory id, or null when it is not registered.</param>
    public static string? Check(string? factory, string? requestedSeat, Func<string, RegisteredFactoryDto?> find, out string? seat)
    {
        ArgumentNullException.ThrowIfNull(find);
        seat = null;
        var hasFactory = !string.IsNullOrWhiteSpace(factory);
        var hasSeat = !string.IsNullOrWhiteSpace(requestedSeat);
        if (!hasFactory && !hasSeat) return null;

        string? folded = null;
        if (hasSeat && !FactoryNames.TrySeat(requestedSeat, out folded, out var why))
            return $"The seat '{requestedSeat}' is refused: {why}.";

        if (!hasFactory)
            return $"The schedule names the seat '{folded}' but no factory. A seat belongs to a factory: name both, --factory <id> --seat {folded}.";

        var registered = find(factory!);
        if (registered is null)
            return $"No factory '{factory}' is registered, so no schedule can run its work. Register the factory with this seat in its manifest first ({RegisterCommand}), then write the schedule again.";

        var seats = string.Join(", ", registered.Seats.Select(s => s.Id));
        if (!hasSeat)
            return $"The schedule names the factory '{registered.Factory}' but no seat. Factory work runs as one of its seats: name it with --seat <id>. The seats of {registered.Title} are: {seats}.";

        if (!registered.Seats.Any(s => string.Equals(s.Id, folded, StringComparison.Ordinal)))
            return $"'{folded}' is not a seat of {registered.Title} ({registered.Factory}); its seats are: {seats}. Add the seat to the factory's manifest and register it ({RegisterCommand}), then write the schedule again.";

        seat = folded;
        return null;
    }
}
