using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.Running;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// A factory's open problems, as its page shows them (Factory Control, step 1b). It does NOT decide what a problem is,
/// when one is open, or what its words are - <see cref="CronRunResultFold.Problems"/> does that once for the page, the
/// morning email and the Factory Manager. This only lays that answer out for the page: who it was, when, how long it
/// has been open, and the two actions on it - Handle the issue, routed by <see cref="FactoryProblemHandoff"/>, and
/// Resolve.
/// </summary>
public static class FactoryProblemsFold
{
    public const string HandleLabel = "Handle the issue";

    /// <summary>The problems card for one factory, from the problems fold's answer for that factory.</summary>
    /// <param name="factory">The registered factory.</param>
    /// <param name="problems">The open problems of this factory, as <c>GET /cron/problems?factory=</c> answers.</param>
    /// <param name="zone">The account's time zone.</param>
    /// <param name="nowUtc">Now.</param>
    public static FactoryPageProblemsDto Card(RegisteredFactoryDto factory, CronRunProblemsDto problems, TimeZoneInfo zone, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(problems);
        ArgumentNullException.ThrowIfNull(zone);
        var handoff = FactoryProblemHandoff.Route(factory);
        var items = problems.Problems
            .Where(p => p.State == CronRunProblemStates.Open
                        && string.Equals(p.Factory, factory.Factory, StringComparison.OrdinalIgnoreCase))
            .Select(p => Item(factory, p, handoff, zone, nowUtc))
            .ToList();
        return new FactoryPageProblemsDto
        {
            Heading = "Open problems",
            Note = "A problem stays here until the next run of the same schedule reports ok, or someone resolves it with a " +
                   "one-line reason. Handle the issue opens a talk with the boss, already told about it.",
            Items = items,
            EmptyText = items.Count == 0 ? "No open problems." : null,
        };
    }

    private static FactoryProblemItemDto Item(RegisteredFactoryDto factory, CronRunProblemDto p,
        FactoryProblemHandoff.Target handoff, TimeZoneInfo zone, DateTime nowUtc)
    {
        var seat = p.Seat is null ? null : factory.Seats.FirstOrDefault(s => string.Equals(s.Id, p.Seat, StringComparison.OrdinalIgnoreCase));
        var seatWord = seat is null ? p.Seat : string.IsNullOrWhiteSpace(seat.Role) ? seat.Id : seat.Role.Trim();
        return new FactoryProblemItemDto
        {
            RunId = p.RunId,
            SeatText = seatWord is null ? p.JobName : $"{seatWord} - {p.JobName}",
            What = string.IsNullOrWhiteSpace(p.Reason) ? p.KindText : $"{p.KindText} - {p.Reason.Trim()}",
            WhenText = p.StartedUtc is { } started
                ? $"Started {FactoriesScreenFold.When(started, zone, nowUtc, capital: false)}"
                : $"Due {FactoriesScreenFold.When(p.ScheduledUtc, zone, nowUtc, capital: false)}, never started",
            ShiftText = p.ShiftText,
            OpenForText = $"open {CronRunEndingFold.Duration(nowUtc - p.RecordedUtc)}",
            SessionId = p.SessionId,
            SessionLabel = p.SessionId is null ? null : FactoryAgentsFold.SessionLabel(p.SessionId),
            NoSessionText = p.SessionId is null ? "No session - the run never started one." : null,
            Handle = new FactoryProblemHandleDto
            {
                Label = HandleLabel,
                BusyLabel = "Starting the talk with the boss...",
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
