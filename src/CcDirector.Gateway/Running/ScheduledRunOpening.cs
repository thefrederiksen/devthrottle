using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Talk;

namespace CcDirector.Gateway.Running;

/// <summary>
/// The seat a scheduled run is opened as: its factory and seat as they are registered NOW, and the brief the Gateway
/// holds published for it.
/// </summary>
public sealed record ScheduledRunSeat(RegisteredFactoryDto Factory, RegisteredFactorySeatDto Seat, FactoryGivenBrief Brief);

/// <summary>
/// THE GATEWAY OPENS A SEAT'S SCHEDULED RUN (Factory Seats mission, phase 2c). A schedule's own prompt used to have to
/// say "You are ..." and "your brief is agents/x.yaml", which is why renaming a seat meant replacing its schedules. For
/// a schedule linked to a seat that has a PUBLISHED brief on the Gateway, the run is told who it is - from the
/// registration, in the words a talk uses (<see cref="FactorySeatOpening.WhoYouAre"/>) - and given the brief's text
/// with its version, ahead of the schedule's own prompt. The prompt is then only what is particular to the run.
///
/// Like the closing instruction, this is added at the moment the run is started, to the text that run receives: the
/// schedule's stored prompt is never changed. The ONE place the run's text is put together, and pure.
///
/// EXACTLY AS BEFORE for a schedule in no factory and for a seat with no published brief: the text is
/// <see cref="ScheduledRunClosingInstruction.PromptFor"/> of the stored prompt, byte for byte. A seat whose brief is
/// still a file in a repository is in that state, and its schedules go on saying who it is themselves.
///
/// A PROMPT THAT IS A COMMAND STAYS FIRST. A prompt that begins with a slash is a command the agent runs only when it
/// is the first thing it is given, so the opening follows such a prompt instead of leading it.
///
/// THE CLOSING INSTRUCTION is decided on the schedule's own prompt alone, as it always was. A brief that mentions
/// <c>cc-devthrottle run result</c> does not take it away: a run told twice to report is harmless, and a run that
/// is not told is recorded "did not report".
/// </summary>
public static class ScheduledRunOpening
{
    /// <summary>
    /// The text a scheduled run is given.
    /// </summary>
    /// <param name="storedPrompt">The schedule's own prompt, as the owner wrote it.</param>
    /// <param name="scheduleName">The schedule's name.</param>
    /// <param name="scheduleId">The schedule's id.</param>
    /// <param name="seat">The seat the run is opened as, or null for a schedule in no factory and for a seat with no
    /// published brief.</param>
    /// <param name="runsAsABoss">The run is a Boss's (<see cref="RunsAsABoss"/>). It matters only when
    /// <paramref name="seat"/> is null: a Boss with no published brief is not opened by the Gateway, and is still told a
    /// Boss never does the work.</param>
    public static string PromptFor(string storedPrompt, string scheduleName, string scheduleId, ScheduledRunSeat? seat,
        bool runsAsABoss = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedPrompt);
        if (seat is null)
        {
            var plain = ScheduledRunClosingInstruction.PromptFor(storedPrompt);
            return runsAsABoss ? FactorySeatOpening.WithBossNeverDoesTheWork(plain) : plain;
        }

        var opening = Opening(scheduleName, scheduleId, seat);
        var own = storedPrompt.Trim();
        var given = own.StartsWith('/') ? own + "\n\n" + opening : opening + "\n\n" + own;
        var told = ScheduledRunClosingInstruction.AlreadyTellsItsRunToReport(storedPrompt)
            ? given
            : given + "\n\n" + ScheduledRunClosingInstruction.Text;
        if (FactorySeatOpening.IsABoss(seat.Factory, seat.Seat))
            told = FactorySeatOpening.WithBossNeverDoesTheWork(told);
        return FactorySeatOpening.WithOwnerWord(told);
    }

    /// <summary>
    /// Whether a schedule's run is a Boss's, read from the registration as it stands now, whether or not the seat has a
    /// published brief. A schedule in no factory, or of a factory or seat that is not registered, is not.
    /// </summary>
    /// <param name="findFactory">One registered factory of the schedule's account by id, or null.</param>
    public static bool RunsAsABoss(CronJobDto job, Func<string, RegisteredFactoryDto?> findFactory)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(findFactory);
        if (!CronRunHistoryStore.IsFactoryLinked(job)) return false;
        if (findFactory(job.Factory!.Trim()) is not { } factory) return false;
        var seat = factory.Seats.FirstOrDefault(s => string.Equals(s.Id, job.Seat!.Trim(), StringComparison.OrdinalIgnoreCase));
        return seat is not null && FactorySeatOpening.IsABoss(factory, seat);
    }

    /// <summary>
    /// The seat a schedule's run is opened as, or null when the Gateway does not open it: the schedule is in no
    /// factory, its factory or seat is not registered, or the seat has no published brief. The seat is read from the
    /// registration as it stands now, so a seat renamed since the schedule was written is opened under its new name.
    /// </summary>
    /// <param name="findFactory">One registered factory of the schedule's account by id, or null.</param>
    /// <param name="publishedBrief">The published brief of one seat of one factory, or null when it has none.</param>
    public static ScheduledRunSeat? SeatFor(CronJobDto job, Func<string, RegisteredFactoryDto?> findFactory,
        Func<string, string, FactoryGivenBrief?> publishedBrief)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(findFactory);
        ArgumentNullException.ThrowIfNull(publishedBrief);
        if (!CronRunHistoryStore.IsFactoryLinked(job)) return null;
        if (findFactory(job.Factory!.Trim()) is not { } factory) return null;
        var seat = factory.Seats.FirstOrDefault(s => string.Equals(s.Id, job.Seat!.Trim(), StringComparison.OrdinalIgnoreCase));
        if (seat is null) return null;
        return publishedBrief(factory.Factory, seat.Id) is { } brief ? new ScheduledRunSeat(factory, seat, brief) : null;
    }

    /// <summary>Who the seat is, which run this is, and the seat's brief with its version.</summary>
    public static string Opening(string scheduleName, string scheduleId, ScheduledRunSeat seat)
    {
        ArgumentNullException.ThrowIfNull(seat);
        return FactorySeatOpening.WhoYouAre(seat.Factory, seat.Seat) + "\n\n"
               + $"This is one of your scheduled runs: the schedule \"{scheduleName}\" ({scheduleId}). Your brief is given "
               + $"to you here in full, as the Gateway holds it ({seat.Brief.VersionWords}"
               + (seat.Brief.FromDefinition ? ", part of the factory's definition" : "")
               + "); it is not a file in the factory's folder. With it comes what this schedule asks of this run.\n\n"
               + FactorySeatOpening.BriefBlock(seat.Brief, "\n")
               // A DEFINED FACTORY'S RUN is told where its kit is, and that its goal changes only through the definition
               // (Factory Definition mission, phase 5). Every other run's opening ends at its brief, as before.
               + (seat.Brief.FromDefinition
                   ? "\n\n" + FactorySeatOpening.KitSentence + "\n\n"
                     + "The factory's goal is part of its definition. If it should change, " + FactorySeatOpening.GoalChange
                   : "");
    }
}
