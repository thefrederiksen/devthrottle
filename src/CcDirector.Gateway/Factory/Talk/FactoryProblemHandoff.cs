using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory.Talk;

/// <summary>
/// WHO HANDLES A FACTORY'S PROBLEM (Factory Control, step 1b) - the one place that decides it. The owner's ruling of
/// 10 October 2026: every problem gets a Handle the issue button that opens a talk with the factory's boss, already
/// told about the problem; a factory with no boss hands the talk to the Factory Manager.
///
/// Step 5 built the Factory Manager, so both answers now open a talk: the boss when the factory names one, otherwise the
/// Factory Manager, briefed in the same words (<see cref="FactoryTalkSeed.ProblemBrief"/>). Whether the Factory Manager
/// can be started right now - a computer and a folder saved, a Director running there - is the start's own question,
/// answered with its own sentence when it cannot.
/// </summary>
public static class FactoryProblemHandoff
{
    /// <summary>Where a problem's talk goes: the boss seat, or the Factory Manager.</summary>
    /// <param name="Seat">The boss seat the talk is opened with; null when it goes to the Factory Manager.</param>
    /// <param name="ToFactoryManager">True when the factory has no boss, so the talk goes to the Factory Manager.</param>
    public sealed record Target(RegisteredFactorySeatDto? Seat, bool ToFactoryManager)
    {
        /// <summary>True when the button opens a talk - with the boss or with the Factory Manager.</summary>
        public bool Available => Seat is not null || ToFactoryManager;

        /// <summary>Never set since step 5: there is always somebody to hand a problem to. Kept for the page's shape.</summary>
        public string? UnavailableText => null;

        /// <summary>What the button says while the talk starts.</summary>
        public string BusyLabel => ToFactoryManager ? "Starting the talk with the Factory Manager..." : "Starting the talk with the boss...";
    }

    /// <summary>The boss when the factory names one; otherwise the Factory Manager.</summary>
    public static Target Route(RegisteredFactoryDto factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var boss = factory.BossSeat is null
            ? null
            : factory.Seats.FirstOrDefault(s => string.Equals(s.Id.Trim(), factory.BossSeat.Trim(), StringComparison.OrdinalIgnoreCase));
        return boss is not null ? new Target(boss, false) : FactoryManager();
    }

    /// <summary>The hand-over for a factory with no boss: the Factory Manager.</summary>
    public static Target FactoryManager() => new(null, true);
}
