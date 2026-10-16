using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.Running;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// A factory's open problems, as its page shows them (Factory Control, step 1b). It does NOT decide what a problem is,
/// when one is open, or what its words are - <see cref="CronRunResultFold.Problems"/> does that once for the page, the
/// morning email and the Factory Manager, and the sentence, the "Started ..." text and the "open ..." text shown here
/// are that list's own, word for word. This only lays that answer out for the page: the seat by its role word, the
/// session to read, and the two actions on it - Handle the issue, routed by <see cref="FactoryProblemHandoff"/>, and
/// Resolve.
/// </summary>
public static class FactoryProblemsFold
{
    public const string HandleLabel = "Handle the issue";

    /// <summary>The problems card for one factory, from the problems fold's answer for that factory.</summary>
    /// <param name="factory">The registered factory.</param>
    /// <param name="problems">The open problems of this factory, as <c>GET /cron/problems?factory=</c> answers.</param>
    public static FactoryPageProblemsDto Card(RegisteredFactoryDto factory, CronRunProblemsDto problems)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(problems);
        var handoff = FactoryProblemHandoff.Route(factory);
        var open = problems.Problems
            .Where(p => p.State == CronRunProblemStates.Open
                        && string.Equals(p.Factory, factory.Factory, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var items = open.Select(p => Item(factory, p, handoff)).ToList();
        // A failed check clears differently from a run (Factory Definition, phase 5b), so the note says how - only when
        // one is listed, so a factory with no checks reads as it always has.
        var checks = CronRunResultFold.AnyCheckProblem(open) ? CronRunResultFold.CheckClearsSentence + " " : "";
        return new FactoryPageProblemsDto
        {
            Heading = "Open problems",
            Note = "A problem stays here until the next run of the same schedule reports ok, or someone resolves it with a " +
                   "one-line reason. " + checks + (handoff.ToFactoryManager
                       ? "This factory has no boss, so Handle the issue opens a talk with the Factory Manager, already told about it."
                       : "Handle the issue opens a talk with the boss, already told about it."),
            Items = items,
            EmptyText = items.Count == 0 ? "No open problems." : null,
        };
    }

    private static FactoryProblemItemDto Item(RegisteredFactoryDto factory, CronRunProblemDto p, FactoryProblemHandoff.Target handoff)
    {
        // A seat is named by its role word, never its id. A run of a seat the factory no longer has says so in words
        // rather than showing the old id.
        var seat = p.Seat is null ? null : factory.Seats.FirstOrDefault(s => string.Equals(s.Id, p.Seat, StringComparison.OrdinalIgnoreCase));
        var seatText = p.Seat is null ? p.JobName
            : seat is null ? $"A seat no longer on this factory - {p.JobName}"
            : $"{FactoriesScreenFold.SeatWord(seat)} - {p.JobName}";
        return new FactoryProblemItemDto
        {
            RunId = p.RunId,
            SeatText = seatText,
            What = p.Sentence,
            WhenText = p.WhenText,
            ShiftText = p.ShiftText,
            // The list says how long every OPEN problem has been open, and only open ones are laid out here, so a
            // missing text means the list was built without its as-of instant.
            OpenForText = p.OpenForText ?? throw new InvalidOperationException(
                $"Open problem {p.RunId} carries no open-for text. The problems list sets it on every open problem."),
            SessionId = p.SessionId,
            SessionLabel = p.SessionId is null ? null : FactoryAgentsFold.SessionLabel(p.SessionId),
            NoSessionText = p.SessionId is null ? "No session - the run never started one." : null,
            Handle = new FactoryProblemHandleDto
            {
                Label = HandleLabel,
                BusyLabel = handoff.BusyLabel,
                FactoryId = factory.Factory,
                RunId = p.RunId,
                Available = handoff.Available,
                UnavailableText = handoff.UnavailableText,
            },
            ResolveLabel = "Resolve",
            ResolvePrompt = "Why is it resolved? One line.",
            ResolveConfirmLabel = "Resolve with this reason",
            ResolveBusyLabel = "Resolving...",
        };
    }
}
