using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory.Talk;

/// <summary>
/// WHO HANDLES A FACTORY'S PROBLEM (Factory Control, step 1b) - the one place that decides it. The owner's ruling of
/// 10 October 2026: every problem gets a Handle the issue button that opens a talk with the factory's boss, already
/// told about the problem; a factory with no boss hands the talk to the Factory Manager.
///
/// The Factory Manager seat is built in step 5. Until then a boss-less factory's problem has nobody to talk to, and
/// this says so in plain words rather than opening a talk with a seat that is not the boss. Step 5 fills in
/// <see cref="FactoryManager"/> and nothing else changes: the button, the route and the screen all read this answer.
/// </summary>
public static class FactoryProblemHandoff
{
    /// <summary>Where a problem's talk goes: the seat to talk to, or why there is none yet.</summary>
    /// <param name="Seat">The seat the talk is opened with; null when there is nobody to talk to yet.</param>
    /// <param name="UnavailableText">Why the button cannot open a talk, in plain words; null when it can.</param>
    public sealed record Target(RegisteredFactorySeatDto? Seat, string? UnavailableText)
    {
        public bool Available => Seat is not null;
    }

    /// <summary>The boss when the factory names one; otherwise the Factory Manager.</summary>
    public static Target Route(RegisteredFactoryDto factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var boss = factory.BossSeat is null
            ? null
            : factory.Seats.FirstOrDefault(s => string.Equals(s.Id.Trim(), factory.BossSeat.Trim(), StringComparison.OrdinalIgnoreCase));
        return boss is not null ? new Target(boss, null) : FactoryManager(factory);
    }

    /// <summary>
    /// The hand-over for a factory with no boss: the Factory Manager. It does not exist yet (Factory Control, step 5),
    /// so there is no talk to open, and the words say so and say what the owner can do meanwhile.
    /// </summary>
    public static Target FactoryManager(RegisteredFactoryDto factory) =>
        new(null, $"{factory.Title} has no boss, so this goes to the Factory Manager, which is not built yet. " +
                  "Read the run's session, fix it, then Resolve it here with a reason.");
}
