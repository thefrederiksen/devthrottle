using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Definition;
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
    public static string PromptFor(string storedPrompt, string scheduleName, string scheduleId, ScheduledRunSeat? seat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedPrompt);
        if (seat is null)
            return ScheduledRunClosingInstruction.PromptFor(storedPrompt);

        var opening = Opening(scheduleName, scheduleId, seat);
        var own = storedPrompt.Trim();
        var given = own.StartsWith('/') ? own + "\n\n" + opening : opening + "\n\n" + own;
        return ScheduledRunClosingInstruction.AlreadyTellsItsRunToReport(storedPrompt)
            ? given
            : given + "\n\n" + ScheduledRunClosingInstruction.Text;
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

    /// <summary>
    /// The seat a DEFINED factory's session is opened as, read from its published definition alone: the factory's title,
    /// id and boss seat and the seat's id, name and role - exactly what a publish writes into the registration a
    /// scheduled run is opened as - and the seat's brief at that same version. For a start that holds the definition but
    /// not the registration (the Mail Desk's router, Mail Desk phase 4, section 3.3).
    /// </summary>
    public static ScheduledRunSeat SeatOf(PublishedFactoryDefinition published, SeatDefinition seat)
    {
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(seat);
        var definition = published.Definition;
        var registered = new RegisteredFactorySeatDto { Id = seat.Seat, Name = seat.Name, Role = seat.Role };
        var factory = new RegisteredFactoryDto
        {
            Factory = definition.Factory,
            Title = definition.Title,
            BossSeat = definition.BossSeat,
            Seats = { registered },
        };
        return new ScheduledRunSeat(factory, registered, published.BriefOf(seat));
    }

    /// <summary>Who the seat is, which run this is, and the seat's brief with its version.</summary>
    public static string Opening(string scheduleName, string scheduleId, ScheduledRunSeat seat)
        => Opening(seat, $"This is one of your scheduled runs: the schedule \"{scheduleName}\" ({scheduleId}).",
            "what this schedule asks of this run");

    /// <summary>
    /// A session the Mail Desk started for emails routed to a seat (Mail Desk phase 4, section 3.3): opened in the same
    /// words as the seat's scheduled run - who it is, its brief at the published version, where its kit is - with only
    /// the sentence that says which run this is changed, since it is not one of the seat's schedules.
    /// </summary>
    public static string MailDeskOpening(ScheduledRunSeat seat)
        => Opening(seat, "This session was started by the Mail Desk for emails routed to this seat.",
            "what the Mail Desk asks of this session");

    private static string Opening(ScheduledRunSeat seat, string whichRun, string whatIsAsked)
    {
        ArgumentNullException.ThrowIfNull(seat);
        return FactorySeatOpening.WhoYouAre(seat.Factory, seat.Seat) + "\n\n"
               + whichRun + " Your brief is given "
               + $"to you here in full, as the Gateway holds it ({seat.Brief.VersionWords}"
               + (seat.Brief.FromDefinition ? ", part of the factory's definition" : "")
               + $"); it is not a file in the factory's folder. With it comes {whatIsAsked}.\n\n"
               + FactorySeatOpening.BriefBlock(seat.Brief, "\n")
               // A DEFINED FACTORY'S RUN is told where its kit is, and that its goal changes only through the definition
               // (Factory Definition mission, phase 5). Every other run's opening ends at its brief, as before.
               + (seat.Brief.FromDefinition
                   ? "\n\n" + FactorySeatOpening.KitSentence + "\n\n"
                     + "The factory's goal is part of its definition. If it should change, " + FactorySeatOpening.GoalChange
                   : "");
    }
}
