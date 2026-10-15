using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Factory.Triggers;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>
/// WHAT A PUBLISH WILL DO TO THE LIVE FACTORY (Factory Definition, phase 4; design sections 2 to 4). One pure function:
/// a checked definition and the factory as it runs today - its registry row, its schedules and its triggers - go in,
/// and the plan comes out: the registry row to write, each schedule to keep, change, create or delete, each trigger to
/// keep, change, create or delete, and one sentence for every step. Or the refusals, and nothing is planned. It reads
/// no database and no clock.
///
/// A START IS MAPPED BY ITS KIND (phase 5b, design section 4): a start with a cron time becomes a schedule, and a
/// start on an interval - which always has a check - becomes a DEFINITION TRIGGER, a row of the triggers table that
/// carries the check's path instead of a command. A trigger is paired with the live definition triggers of its seat
/// exactly as a schedule is, so an edited start keeps its trigger's id, its pause switch, its run history and the
/// state its check keeps. Every trigger of the factory that is NOT a definition trigger - a command a person wrote
/// before the factory was defined - is deleted, as phase 4 did; its run history is kept.
///
/// EVERY RULE A ROW IS HELD TO RUNS HERE, BEFORE ANY TRANSACTION: the registry's own validation
/// (<see cref="FactoryRegistryStore.ValidateSeat"/> and <see cref="FactoryRegistryStore.Validate"/>), the schedule's
/// (<see cref="CronSchedule.Validate"/>) and the link between a schedule and its seat (<see cref="FactoryScheduleLink.Check"/>).
/// They are called, never copied, so a definition that would make a bad row is refused in their own words and the
/// apply that follows can fail only on the database.
///
/// A START IS MATCHED TO A LIVE SCHEDULE with the one pairing "what changes" uses
/// (<see cref="FactoryDefinitionChanges.PairStarts"/>), per seat, so a publish moves a schedule rather than deleting
/// and recreating it: the schedule keeps its id, its switch, its notifications and its run history.
///
/// ANOTHER FACTORY'S ROWS ARE NEVER IN A PLAN: the live rows handed in are narrowed to this factory first.
/// </summary>
public static class FactoryPublishPlan
{
    /// <summary>The refusal for an interval start with no check: an interval is only ever a check's, and a version
    /// stored before the check said so can still hold one.</summary>
    public const string IntervalStartNotYet =
        "This start runs on an interval and has no check. An interval start is a check that runs often and starts a " +
        "session only when it counts work, so give it a check, or a cron time, or publish it without the start.";

    /// <summary>The name a definition trigger is given: "&lt;factory&gt;-&lt;seat&gt;-start-&lt;N&gt;", where N is THE
    /// number of its start - its place in the seat's <c>starts:</c> list, counting every start from 1. The same number
    /// <c>cc-devthrottle factory check-state --start</c> takes.</summary>
    internal static string TriggerName(string factory, string seat, int startNumber)
        => $"{factory}-{seat}-start-{startNumber}";

    /// <summary>
    /// The plan for publishing <paramref name="definition"/> onto the live factory, or why it cannot be. The live rows
    /// may name other factories; only this factory's are planned.
    /// </summary>
    public static FactoryPublishComposition Compose(FactoryDefinition definition, FactoryRegistryEntity? registry,
        IReadOnlyList<CronJobEntity> liveSchedules, IReadOnlyList<TriggerEntity> liveTriggers)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(liveSchedules);
        ArgumentNullException.ThrowIfNull(liveTriggers);
        var factory = definition.Factory;
        FileLog.Write($"[FactoryPublishPlan] Compose: factory={factory}, seats={definition.Seats.Count}, registered={registry is not null}, liveSchedules={liveSchedules.Count}, liveTriggers={liveTriggers.Count}");

        // A DEFINED FACTORY RUNS WHERE ITS DEFINITION SAYS (Factory Definition mission, phase 5): each seat in the
        // repository it names, or in its kit - never in a registered folder. So a factory the registry does not hold yet
        // is registered by its first publish, with no folder. An archived one is the owner's to bring back first.
        if (registry?.ArchivedAtUtc is not null)
            return Refused(FactoryDefinitionOutcome.Archived, $"{factory} is archived. Restore it, then publish.");

        var findings = new List<DefinitionFinding>();

        // A TIMED START WITH A CHECK CANNOT RUN YET: its check is due when its time comes, and the due check is the next
        // pull request of phase 5b. The whole-definition check refuses it at publish on its check line; refused here
        // too, because a plan that made it a plain schedule would start its session without its check.
        // An interval start with no check is refused by the check at every save, so a checked definition never holds
        // one; a version stored before that rule can, and it is refused in its own words (review of 4006, observation 4).
        foreach (var seat in definition.Seats)
        {
            if (seat.Starts.Any(s => s.Cron is not null && s.Check is not null))
                findings.Add(Error(SeatFile(seat), FactoryDefinitionCheck.TimedCheckStartNotYet));
            else if (seat.Starts.Any(s => s.Cron is null && s.Check is null))
                findings.Add(Error(SeatFile(seat), IntervalStartNotYet));
        }
        if (findings.Count > 0) return RowRulesRefused(factory, findings);

        var mine = liveSchedules.Where(j => string.Equals(j.Factory, factory, StringComparison.Ordinal))
            .OrderBy(j => j.Id, StringComparer.Ordinal).ToList();
        var seatsById = definition.Seats.ToDictionary(s => s.Seat, StringComparer.Ordinal);

        // ---- the schedules, paired per seat ----
        var planned = new List<PlannedSchedule>();
        var deletes = new List<PlannedSchedule>();
        var scheduleIdsOfSeat = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var placeholder = 0;
        foreach (var seat in definition.Seats)
        {
            // Only a start with a cron time is a schedule; an interval start is a trigger (below).
            var timed = Enumerable.Range(0, seat.Starts.Count).Where(i => seat.Starts[i].Cron is not null).ToList();
            var live = mine.Where(j => j.Seat == seat.Seat).ToList();
            var pairing = FactoryDefinitionChanges.PairStarts(live.Select(AsStart).ToList(),
                timed.Select(i => seat.Starts[i]).ToList());
            var ids = new List<string>();
            var ofSeat = new List<PlannedSchedule>();
            foreach (var pair in pairing.Pairs)
            {
                var row = live[pair.Old];
                var job = Kept(Job(definition, seat, timed[pair.New], pair.New), row);
                var action = SameDefinition(row, job) ? PlannedScheduleAction.Keep : PlannedScheduleAction.Update;
                ofSeat.Add(new PlannedSchedule(action, row.Id, seat.Seat, timed[pair.New], job, CronJobStore.ToDto(row)));
                ids.Add(row.Id);
            }
            foreach (var n in pairing.UnpairedNew)
            {
                ofSeat.Add(new PlannedSchedule(PlannedScheduleAction.Create, null, seat.Seat, timed[n],
                    Job(definition, seat, timed[n], n), null));
                ids.Add($"(new schedule {++placeholder})");
            }
            foreach (var o in pairing.UnpairedOld)
                deletes.Add(Deleted(live[o], DeleteReason.NoStart));
            planned.AddRange(ofSeat.OrderBy(p => p.StartIndex));
            scheduleIdsOfSeat[seat.Seat] = ids;
        }
        foreach (var row in mine.Where(j => j.Seat is null || !seatsById.ContainsKey(j.Seat)))
            deletes.Add(Deleted(row, row.Seat is null ? DeleteReason.NoSeat : DeleteReason.SeatNotDefined));

        // ---- the registry row: the registry's own rules ----
        var request = Manifest(definition, registry, scheduleIdsOfSeat);
        foreach (var seat in request.Seats)
        {
            try
            {
                FactoryRegistryStore.ValidateSeat(seat, request.Computer);
            }
            catch (FactoryViewValidationException ex)
            {
                findings.Add(Error($"seats/{seat.Id}.yaml", ex.Message));
            }
        }
        FactoryRegistryEntity? row2 = null;
        if (findings.Count == 0)
        {
            try
            {
                row2 = FactoryRegistryStore.Validate(request, writtenByAPublish: true);
            }
            catch (FactoryViewValidationException ex)
            {
                findings.Add(Error(FactoryDefinitionCheck.FactoryFile, ex.Message));
            }
        }
        if (row2 is null) return RowRulesRefused(factory, findings);

        // ---- each schedule: its own rules and its link to the seat ----
        var asRegistered = Registered(row2);
        foreach (var p in planned.Where(p => p.Action != PlannedScheduleAction.Keep))
        {
            var file = $"seats/{p.Seat}.yaml";
            var (ok, error) = CronSchedule.Validate(p.Job!, factoryIsDefined: true);
            if (!ok)
            {
                findings.Add(Error(file, $"The schedule this start makes is refused: {error}."));
                continue;
            }
            if (FactoryScheduleLink.Check(p.Job!.Factory, p.Job.Seat, id => id == factory ? asRegistered : null, out _,
                    running: p.Job.Enabled) is { } linkRefusal)
                findings.Add(Error(file, linkRefusal));
        }
        if (findings.Count > 0) return RowRulesRefused(factory, findings);

        var triggers = PlanTriggers(definition, registry, liveTriggers, findings);
        foreach (var t in triggers.Where(t => t.Action is PlannedTriggerAction.Create or PlannedTriggerAction.Update))
            if (TriggerDefinition.ValidateDefinitionTrigger(t.Row!.Name, t.Row.FactoryAgent, t.Row.Machine, t.Row.Prompt,
                    t.Row.IntervalSeconds) is { } triggerError)
                findings.Add(Error($"seats/{t.Seat}.yaml", $"The trigger this start makes is refused: {triggerError}."));
        if (findings.Count > 0) return RowRulesRefused(factory, findings);

        var plan = new PublishPlan(factory, row2, RegistrySentences(registry, row2), planned.Concat(deletes).ToList(), triggers);
        FileLog.Write($"[FactoryPublishPlan] Compose: factory={factory}, keep={plan.Schedules.Count(s => s.Action == PlannedScheduleAction.Keep)}, update={plan.Schedules.Count(s => s.Action == PlannedScheduleAction.Update)}, create={plan.Schedules.Count(s => s.Action == PlannedScheduleAction.Create)}, delete={plan.Schedules.Count(s => s.Action == PlannedScheduleAction.Delete)}, triggers keep={triggers.Count(t => t.Action == PlannedTriggerAction.Keep)} update={triggers.Count(t => t.Action == PlannedTriggerAction.Update)} create={triggers.Count(t => t.Action == PlannedTriggerAction.Create)} delete={triggers.Count(t => t.Action == PlannedTriggerAction.Delete)}");
        return new FactoryPublishComposition(plan, null);
    }

    // -------------------------------------------------------------------------------------------------------------
    // The rows
    // -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The schedule one start makes (design section 3): the seat's machine or the default, the start's prompt, NO
    /// folder (phase 5: the run is built from the definition when it fires, and the seat works in the repository it
    /// names or in its kit), named "title - seat name", with " (2)", " (3)" in start order when the seat has more than
    /// one start, so a rename shows on every schedule.
    /// </summary>
    private static CronJobDto Job(FactoryDefinition d, SeatDefinition seat, int startIndex, int ordinal)
    {
        var start = seat.Starts[startIndex];
        return new CronJobDto
        {
            // Numbered among the seat's TIMED starts: an interval start beside it is a trigger and takes no number here.
            Name = $"{d.Title} - {seat.Name}" + (ordinal == 0 ? "" : $" ({ordinal + 1})"),
            Enabled = true,
            ScheduleKind = CronSchedule.KindRecurring,
            CronExpression = start.Cron,
            RunAt = null,
            TimeZoneId = start.TimeZone ?? "",
            Factory = d.Factory,
            Seat = seat.Seat,
            Target = new CronJobTarget { Machine = seat.MachineOrDefault(d.Defaults) },
            Action = new CronJobAction { RepoPath = "", Seed = start.Prompt },
            PreventOverlap = start.PreventOverlap,
            NotifyOn = CronNotify.None,
        };
    }

    /// <summary>
    /// A paired schedule keeps what is the person's, not the definition's: its switch (a publish never turns a schedule
    /// on or off), its notifications, and whether its runs close themselves.
    ///
    /// NOT A WORK LIST (phase 5 P2 review, finding 2). A start only starts the seat with its prompt, built from the
    /// definition; a schedule that drained a work list would fire outside that one builder, with no seat, no kit and -
    /// since phase 5 - no folder. So a paired schedule is written without its work list, and a drain's own close-itself
    /// setting goes with the drain: the run takes the default a start's run has. The step's sentence names the list.
    /// </summary>
    private static CronJobDto Kept(CronJobDto job, CronJobEntity live)
    {
        job.Id = live.Id;
        job.Enabled = live.Enabled;
        job.NotifyOn = live.NotifyOn;
        job.NotifyWebhookUrl = live.NotifyWebhookUrl;
        job.Action.WorkListName = null;
        if (string.IsNullOrWhiteSpace(live.Action.WorkListName))
            job.Action.AutoDismiss = live.Action.AutoDismiss;
        return job;
    }

    /// <summary>True when a paired schedule already says everything the definition would write.</summary>
    private static bool SameDefinition(CronJobEntity live, CronJobDto job)
        => live.Name == job.Name && live.ScheduleKind == job.ScheduleKind && live.CronExpression == job.CronExpression
           && live.RunAt == job.RunAt && live.TimeZoneId == job.TimeZoneId && live.Factory == job.Factory
           && live.Seat == job.Seat && live.Target.Machine == job.Target.Machine
           && live.Action.RepoPath == job.Action.RepoPath && live.Action.Seed == job.Action.Seed
           && live.PreventOverlap == job.PreventOverlap
           && string.IsNullOrWhiteSpace(live.Action.WorkListName) == string.IsNullOrWhiteSpace(job.Action.WorkListName);

    /// <summary>
    /// A live schedule as a start, so the one pairing can match it: its time (whatever kind it is, it is a time, never
    /// an interval), its zone, its prompt and its overlap. A schedule has no check and no secrets.
    /// </summary>
    private static SeatStartDefinition AsStart(CronJobEntity job)
        => new(job.CronExpression ?? job.RunAt ?? "", job.TimeZoneId, null, null, null, Array.Empty<string>(),
            job.Action.Seed ?? "", job.PreventOverlap);

    /// <summary>
    /// The manifest the registry's own rules are run on (design section 3): factory.yaml's fields, the default machine
    /// as the computer, the folder KEPT from the registered row (none for a factory its first publish registers, and
    /// never what a run starts in: phase 5), no goal file (the goal text is in the definition), and
    /// one seat per seat file in seat id order, its brief file the seat file where the brief now lives.
    /// </summary>
    private static RegisterFactoryRequest Manifest(FactoryDefinition d, FactoryRegistryEntity? registry,
        IReadOnlyDictionary<string, List<string>> scheduleIdsOfSeat) => new()
    {
        Factory = d.Factory,
        Title = d.Title,
        Folder = registry?.Folder ?? "",
        Computer = d.Defaults.Machine,
        BossSeat = d.BossSeat,
        GoalText = d.GoalText,
        GoalFile = null,
        GoalApprovedOn = d.GoalApprovedOn,
        Purpose = d.Purpose,
        Seats = d.Seats.Select(s => new FactorySeatManifest
        {
            Id = s.Seat,
            Name = s.Name,
            Role = s.Role,
            BriefFile = SeatFile(s),
            Line = s.Line,
            Computer = s.MachineOrDefault(d.Defaults),
            // What runs the seat after this publish, so the registry's bound on a seat's schedules is held here.
            Schedules = scheduleIdsOfSeat[s.Seat],
        }).ToList(),
    };

    // -------------------------------------------------------------------------------------------------------------
    // The triggers (phase 5b)
    // -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The trigger steps: per seat in seat id order, its interval starts paired with its live definition triggers by
    /// the one pairing; a paired trigger is kept or changed in place, an unpaired start gets a new trigger, an unpaired
    /// trigger is deleted. Then every other trigger of the factory is deleted - a definition trigger whose seat is gone,
    /// and every command trigger.
    ///
    /// A TRIGGER IS NAMED BY ITS START: "&lt;factory&gt;-&lt;seat&gt;-start-&lt;N&gt;", N the start's place in the seat's
    /// list counting every start (<see cref="TriggerName"/>) - so a kept trigger whose start moved in the list is renamed
    /// with it. A trigger name is unique in the account, so two starts whose triggers would share a name, or a name
    /// another factory's trigger (or a factory-less one) already holds, is a refusal on the seat's file, in words;
    /// nothing is renamed to make room.
    /// </summary>
    private static List<PlannedTrigger> PlanTriggers(FactoryDefinition d, FactoryRegistryEntity? registry,
        IReadOnlyList<TriggerEntity> liveTriggers, List<DefinitionFinding> findings)
    {
        var factory = d.Factory;
        var mine = liveTriggers.Where(t => string.Equals(t.Factory, factory, StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Id).ToList();
        var others = liveTriggers.Where(t => !string.Equals(t.Factory, factory, StringComparison.Ordinal))
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var seatIds = d.Seats.Select(s => s.Seat).ToHashSet(StringComparer.Ordinal);
        var planned = new List<PlannedTrigger>();
        var deletes = new List<PlannedTrigger>();

        foreach (var seat in d.Seats.OrderBy(s => s.Seat, StringComparer.Ordinal))
        {
            var interval = Enumerable.Range(0, seat.Starts.Count).Where(i => seat.Starts[i].Cron is null).ToList();
            var live = mine.Where(t => t.IsDefinitionCheck && t.Seat == seat.Seat).ToList();
            var pairing = FactoryDefinitionChanges.PairStarts(live.Select(AsStart).ToList(),
                interval.Select(i => seat.Starts[i]).ToList());
            foreach (var pair in pairing.Pairs)
            {
                var row = live[pair.Old];
                var name = TriggerName(factory, seat.Seat, interval[pair.New] + 1);
                var now = TriggerFor(d, registry, seat, interval[pair.New], name);
                var was = TriggerRow.Of(row);
                planned.Add(new PlannedTrigger(now == was ? PlannedTriggerAction.Keep : PlannedTriggerAction.Update,
                    row.Id, name, seat.Seat, interval[pair.New], now, was));
            }
            foreach (var n in pairing.UnpairedNew)
            {
                var name = TriggerName(factory, seat.Seat, interval[n] + 1);
                planned.Add(new PlannedTrigger(PlannedTriggerAction.Create, null, name, seat.Seat, interval[n],
                    TriggerFor(d, registry, seat, interval[n], name), null));
            }
            foreach (var o in pairing.UnpairedOld)
                deletes.Add(DeletedTrigger(live[o], TriggerDeleteReason.NoStart));
        }
        foreach (var t in mine.Where(t => !t.IsDefinitionCheck))
            deletes.Add(DeletedTrigger(t, TriggerDeleteReason.Command));
        foreach (var t in mine.Where(t => t.IsDefinitionCheck && (t.Seat is null || !seatIds.Contains(t.Seat))))
            deletes.Add(DeletedTrigger(t, TriggerDeleteReason.SeatNotDefined));

        planned = planned.OrderBy(t => t.Seat, StringComparer.Ordinal).ThenBy(t => t.StartIndex).ToList();
        findings.AddRange(DuplicateNames(planned));
        foreach (var t in planned)
            if (others.TryGetValue(t.Name, out var holder))
                findings.Add(Error($"seats/{t.Seat}.yaml", NameTaken(t.Name, holder)));

        return planned.Concat(deletes).ToList();
    }

    /// <summary>
    /// One refusal on each seat file whose start would make a trigger another planned trigger also takes. Trigger names
    /// are unique in the account ignoring case, so two planned triggers may never share one; nothing is renamed to make
    /// room. From valid seat ids this does not happen - a name ends in "-start-" and a number, so it names one seat - but
    /// the plan does not lean on that: a clash is a refusal in words, never a failed write halfway through a publish.
    /// </summary>
    internal static IEnumerable<DefinitionFinding> DuplicateNames(IReadOnlyList<PlannedTrigger> planned)
    {
        foreach (var clash in planned.Where(t => t.Action != PlannedTriggerAction.Delete)
                     .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var starts = clash.ToList();
            foreach (var t in starts)
            {
                var others = string.Join(" and ", starts.Where(o => !ReferenceEquals(o, t))
                    .Select(o => $"start {o.StartIndex + 1} of seat {o.Seat}"));
                yield return Error($"seats/{t.Seat}.yaml",
                    $"Start {t.StartIndex + 1} of this seat makes a trigger named {t.Name}, and so does {others}. " +
                    "A trigger's name is unique in the account; rename one of the seats, then publish.");
            }
        }
    }

    /// <summary>The refusal when the name a start's trigger takes is another trigger's in the account.</summary>
    internal static string NameTaken(string name, TriggerEntity holder)
        => $"The trigger this start makes is named {name}, and that name is taken by the trigger '{holder.Name}' " +
           (string.IsNullOrEmpty(holder.Factory) ? "of no factory" : $"of {holder.Factory}") +
           ". A trigger's name is unique in the account; rename or delete that trigger, then publish.";

    /// <summary>
    /// The trigger an interval start makes: named, on the seat's machine (or the default), its factory agent the seat
    /// id - the key a seat page finds its triggers by - every interval of the start, its prompt, its check and its
    /// declared secrets. Its folder is the registry's, or none for a factory its first publish registers (phase 5: a
    /// defined factory's seat runs where its definition says, and its check start is built by FactorySeatStart); the
    /// Director is never handed it for the check, which runs outside any checkout.
    /// </summary>
    private static TriggerRow TriggerFor(FactoryDefinition d, FactoryRegistryEntity? registry, SeatDefinition seat,
        int startIndex, string name)
    {
        var start = seat.Starts[startIndex];
        return new TriggerRow(name, seat.Seat, seat.MachineOrDefault(d.Defaults), registry?.Folder ?? "",
            checked((int)start.EverySeconds!.Value), start.Prompt, start.Check!, start.Secrets.ToList());
    }

    /// <summary>A live definition trigger as a start, so the one pairing can match it: its interval, its check, its
    /// secrets and its prompt. A check always prevents overlap.</summary>
    internal static SeatStartDefinition AsStart(TriggerEntity t)
        => new(null, null, null, t.IntervalSeconds, t.CheckPath, TriggerService.SecretsOf(t), t.Prompt,
            true);

    private static PlannedTrigger DeletedTrigger(TriggerEntity t, TriggerDeleteReason why)
        => new(PlannedTriggerAction.Delete, t.Id, t.Name, t.Seat, -1, null, TriggerRow.Of(t), why);

    /// <summary>The planned row as the schedule link reads a registration: its id, title and seats.</summary>
    private static RegisteredFactoryDto Registered(FactoryRegistryEntity row) => new()
    {
        Factory = row.Factory,
        Title = row.Title,
        Seats = FactoryRegistryStore.SeatsOf(row),
        ArchivedAtUtc = null,
    };

    private static PlannedSchedule Deleted(CronJobEntity row, DeleteReason why)
        => new(PlannedScheduleAction.Delete, row.Id, row.Seat, -1, null, CronJobStore.ToDto(row), why);

    private static string SeatFile(SeatDefinition seat) => $"seats/{seat.Seat}.yaml";

    // -------------------------------------------------------------------------------------------------------------
    // The registry's sentences
    // -------------------------------------------------------------------------------------------------------------

    private static List<string> RegistrySentences(FactoryRegistryEntity? old, FactoryRegistryEntity now)
    {
        var said = new List<string>();
        if (old is null)
        {
            said.Add($"Registry: {now.Factory} registered from its definition, in no folder: each seat runs where the definition says.");
            return said;
        }
        void Field(string what, string? before, string? after)
        {
            if (string.Equals(before, after, StringComparison.Ordinal)) return;
            said.Add(before is null ? $"Registry: {what} set to {Shown(after)}."
                : after is null ? $"Registry: {what} cleared (was {Shown(before)})."
                : $"Registry: {what} {Shown(before)} -> {Shown(after)}.");
        }

        Field("title", old.Title, now.Title);
        Field("computer", old.Computer, now.Computer);
        Field("boss seat", old.BossSeat, now.BossSeat);
        if (!string.Equals(old.GoalText, now.GoalText, StringComparison.Ordinal))
            said.Add(now.GoalText is null ? "Registry: goal cleared." : old.GoalText is null ? "Registry: goal set." : "Registry: goal text changed.");
        Field("goal approved on", old.GoalApprovedOn, now.GoalApprovedOn);
        Field("goal file", old.GoalFile, now.GoalFile);
        Field("purpose", old.Purpose, now.Purpose);

        var before = FactoryRegistryStore.SeatsOf(old).ToDictionary(s => s.Id, StringComparer.Ordinal);
        var after = FactoryRegistryStore.SeatsOf(now).ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var id in before.Keys.Union(after.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            before.TryGetValue(id, out var o);
            after.TryGetValue(id, out var n);
            if (n is null) { said.Add($"Registry: seat {id} ({o!.Name}) removed."); continue; }
            if (o is null) { said.Add($"Registry: seat {id} ({n.Name}, {n.Role}) added."); continue; }
            if (o.Name != n.Name) said.Add($"Registry: seat {id} renamed {o.Name} -> {n.Name}.");
            if (o.Role != n.Role) said.Add($"Registry: seat {id} role {o.Role} -> {n.Role}.");
            if (o.Line != n.Line) said.Add($"Registry: seat {id} line {Shown(o.Line)} -> {Shown(n.Line)}.");
            if (o.Computer != n.Computer) said.Add($"Registry: seat {id} computer {o.Computer} -> {n.Computer}.");
            if (o.BriefFile != n.BriefFile) said.Add($"Registry: seat {id} brief file {o.BriefFile} -> {n.BriefFile}.");
        }
        if (said.Count == 0) said.Add("Registry: nothing changes but who registered it.");
        return said;
    }

    private static string Shown(string? value) => value is null ? "nothing" : $"\"{Cut(value)}\"";

    internal static string Cut(string value)
        => value.Length <= FactoryDefinitionChanges.MaxValueChars ? value : value[..FactoryDefinitionChanges.MaxValueChars] + "...";

    // -------------------------------------------------------------------------------------------------------------
    // Refusals
    // -------------------------------------------------------------------------------------------------------------

    private static DefinitionFinding Error(string file, string message) => new(DefinitionSeverity.Error, file, 0, message);

    private static FactoryPublishComposition Refused(FactoryDefinitionOutcome outcome, string sentence)
    {
        FileLog.Write($"[FactoryPublishPlan] Compose REFUSED ({outcome}): {sentence}");
        return new FactoryPublishComposition(null, new FactoryPublishRefusal(outcome, sentence, Array.Empty<DefinitionFinding>()));
    }

    private static FactoryPublishComposition RowRulesRefused(string factory, List<DefinitionFinding> findings)
    {
        var sentence = $"The live factory cannot take this definition: {findings.Count} {(findings.Count == 1 ? "row would break its rule" : "rows would break their rules")} " +
                       $"in the registry or the schedules of {factory}. Fix each one where it is and try again; nothing was written.";
        FileLog.Write($"[FactoryPublishPlan] Compose REFUSED (422): {string.Join(" | ", findings.Select(f => $"{f.File}: {f.Message}"))}");
        return new FactoryPublishComposition(null, new FactoryPublishRefusal(FactoryDefinitionOutcome.PlanRefused, sentence, findings));
    }
}

/// <summary>What a schedule step of a plan does.</summary>
public enum PlannedScheduleAction
{
    /// <summary>The live schedule already says what the start says: nothing is written.</summary>
    Keep,
    /// <summary>The live schedule is changed in place: it keeps its id, switch, notifications and run history.</summary>
    Update,
    /// <summary>A new schedule for a start no live schedule runs.</summary>
    Create,
    /// <summary>A live schedule of the factory no start names. Its runs are kept.</summary>
    Delete,
}

/// <summary>Why a live schedule is deleted.</summary>
public enum DeleteReason
{
    /// <summary>Not deleted.</summary>
    None,
    /// <summary>Its seat is in the definition, and none of that seat's starts is paired with it.</summary>
    NoStart,
    /// <summary>It names no seat.</summary>
    NoSeat,
    /// <summary>It names a seat the definition does not have.</summary>
    SeatNotDefined,
}

/// <summary>
/// One schedule step. <see cref="Job"/> is what is written (null for a delete); <see cref="Live"/> a COPY of the row
/// as it runs today (null for a create) - a copy, so writing the row never changes what the step says it changed;
/// <see cref="StartIndex"/> the start's place in its seat (-1 for a delete).
/// </summary>
public sealed record PlannedSchedule(PlannedScheduleAction Action, string? Id, string? Seat, int StartIndex,
    CronJobDto? Job, CronJobDto? Live, DeleteReason Why = DeleteReason.None)
{
    /// <summary>The step in one sentence. <paramref name="mintedId"/> is the id a create was given when it was written;
    /// null in a plan that has not been applied.</summary>
    public string Sentence(string? mintedId = null)
    {
        switch (Action)
        {
            case PlannedScheduleAction.Keep:
                return $"Schedule {Id} ({Job!.Name}) kept as it is.";
            case PlannedScheduleAction.Create:
                return mintedId is null
                    ? $"A new schedule ({Job!.Name}) for seat {Seat}: {When(Job)}."
                    : $"Schedule {mintedId} ({Job!.Name}) created for seat {Seat}: {When(Job)}.";
            case PlannedScheduleAction.Delete:
                var why = Why switch
                {
                    DeleteReason.NoSeat => "it names no seat",
                    DeleteReason.SeatNotDefined => $"its seat '{Seat}' is not in the definition",
                    _ => "no start in the definition names it",
                };
                return $"Schedule {Id} ({Live!.Name}) deleted: {why}; its runs are kept.";
            default:
                var oldWhen = When(Live!);
                var newWhen = When(Job!);
                var details = new List<string>();
                if (Live!.Name != Job!.Name) details.Add($"name \"{Live.Name}\" -> \"{Job.Name}\"");
                if (Live.Target.Machine != Job.Target.Machine) details.Add($"machine {Live.Target.Machine} -> {Job.Target.Machine}");
                if (Live.Action.Seed != Job.Action.Seed) details.Add("prompt changed");
                if (!string.IsNullOrWhiteSpace(Live.Action.WorkListName) && string.IsNullOrWhiteSpace(Job.Action.WorkListName))
                    details.Add($"it no longer drains the work list '{Live.Action.WorkListName}': a start only starts the seat with its prompt");
                if (Live.PreventOverlap != Job.PreventOverlap) details.Add($"prevent overlap {(Live.PreventOverlap ? "on" : "off")} -> {(Job.PreventOverlap ? "on" : "off")}");
                if (Live.Action.RepoPath != Job.Action.RepoPath)
                    details.Add(string.IsNullOrEmpty(Job.Action.RepoPath)
                        ? $"folder {Live.Action.RepoPath} no longer used: the run starts where the definition says"
                        : $"folder {Live.Action.RepoPath} -> {Job.Action.RepoPath}");
                if (Live.ScheduleKind != Job.ScheduleKind) details.Add($"kind {Live.ScheduleKind} -> {Job.ScheduleKind}");
                if (Live.CronExpression != Job.CronExpression && oldWhen == newWhen) details.Add($"cron \"{Live.CronExpression}\" -> \"{Job.CronExpression}\"");
                var tail = details.Count == 0 ? "" : $"; {string.Join("; ", details)}";
                return oldWhen != newWhen
                    ? $"Schedule {Id} ({Job.Name}) moved: {oldWhen} -> {newWhen}{tail}."
                    : $"Schedule {Id} ({Job.Name}) changed: {string.Join("; ", details)}.";
        }
    }

    /// <summary>When a schedule fires, in the words a start's time is said in ("Weekdays 08:00 Europe/Copenhagen").</summary>
    internal static string When(CronJobDto job)
    {
        if (CronSchedule.IsRecurring(job.ScheduleKind)) return $"{FactoryScheduleText.Cron(job.CronExpression)} {job.TimeZoneId}";
        // Another kind (one-off, random, window) is said as the schedule screen says it, in its own zone.
        var zone = CronSchedule.FindZone(job.TimeZoneId) ?? TimeZoneInfo.Utc;
        var enabled = new CronJobDto { ScheduleKind = job.ScheduleKind, CronExpression = job.CronExpression, RunAt = job.RunAt, TimeZoneId = job.TimeZoneId, Enabled = true };
        return $"{FactoryScheduleText.Describe(enabled, zone)} {job.TimeZoneId}";
    }
}

/// <summary>What a trigger step of a plan does.</summary>
public enum PlannedTriggerAction
{
    /// <summary>The live definition trigger already says what the start says: only the version its check is read
    /// from moves to the published one.</summary>
    Keep,
    /// <summary>The live definition trigger is changed in place: it keeps its id, its name, its pause switch, its run
    /// history and its check's state.</summary>
    Update,
    /// <summary>A new definition trigger for an interval start no live trigger runs.</summary>
    Create,
    /// <summary>A live trigger of the factory no interval start names. Its run history is kept.</summary>
    Delete,
}

/// <summary>Why a live trigger is deleted.</summary>
public enum TriggerDeleteReason
{
    /// <summary>Not deleted.</summary>
    None,
    /// <summary>A definition trigger whose seat is in the definition, and none of that seat's starts is paired with it.</summary>
    NoStart,
    /// <summary>A definition trigger whose seat the definition does not have.</summary>
    SeatNotDefined,
    /// <summary>A trigger whose check is a command a person wrote: a defined factory's triggers come from its definition.</summary>
    Command,
}

/// <summary>What a definition trigger holds that a publish writes. A command trigger read as one has an empty check
/// path and no secrets.</summary>
public sealed record TriggerRow(string Name, string FactoryAgent, string Machine, string RepoPath, int IntervalSeconds,
    string Prompt, string CheckPath, IReadOnlyList<string> Secrets)
{
    public static TriggerRow Of(TriggerEntity t) => new(t.Name, t.FactoryAgent, t.Machine, t.RepoPath, t.IntervalSeconds,
        t.Prompt, t.CheckPath ?? "", t.IsDefinitionCheck ? TriggerService.SecretsOf(t) : Array.Empty<string>());

    /// <summary>Equal when every field is, the secrets in order.</summary>
    public bool Equals(TriggerRow? other)
        => other is not null && Name == other.Name && FactoryAgent == other.FactoryAgent && Machine == other.Machine
           && RepoPath == other.RepoPath && IntervalSeconds == other.IntervalSeconds && Prompt == other.Prompt
           && CheckPath == other.CheckPath && Secrets.SequenceEqual(other.Secrets, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Name, Machine, IntervalSeconds, CheckPath);

    /// <summary>The secrets as the row stores them: a JSON array, or null when there are none.</summary>
    public string? SecretsJson => Secrets.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(Secrets);
}

/// <summary>
/// One trigger step. <see cref="Row"/> is what is written (null for a delete); <see cref="Live"/> the trigger as it
/// runs today (null for a create); <see cref="StartIndex"/> the start's place in its seat (-1 for a delete).
/// </summary>
public sealed record PlannedTrigger(PlannedTriggerAction Action, Guid? Id, string Name, string? Seat, int StartIndex,
    TriggerRow? Row, TriggerRow? Live, TriggerDeleteReason Why = TriggerDeleteReason.None)
{
    /// <summary>The step in one sentence. <paramref name="version"/> is the version the publish makes the published
    /// one; null in a plan that has not been applied.</summary>
    public string Sentence(int? version = null)
    {
        var from = version is { } v ? $"version {v}" : "the published version";
        switch (Action)
        {
            case PlannedTriggerAction.Keep:
                return $"Trigger {Name} kept as it is; its check is read from {from}.";
            case PlannedTriggerAction.Create:
                return $"{(version is null ? "A new trigger" : "Trigger")} {Name} {(version is null ? "for" : "created for")} seat {Seat}: " +
                       $"{Every(Row!.IntervalSeconds)}, check {Row.CheckPath}{SecretsText(Row.Secrets)}, on {Row.Machine}.";
            case PlannedTriggerAction.Delete:
                var why = Why switch
                {
                    TriggerDeleteReason.Command => "its check is a command, and a defined factory's triggers come from its definition",
                    TriggerDeleteReason.SeatNotDefined => $"its seat '{Seat}' is not in the definition",
                    _ => "no start in the definition names it",
                };
                return $"Trigger {Name} deleted: {why}; its run history is kept.";
            default:
                var details = new List<string>();
                if (Live!.Name != Row!.Name) details.Add($"renamed {Live.Name} -> {Row.Name}");
                if (Live.IntervalSeconds != Row.IntervalSeconds) details.Add($"{Every(Live.IntervalSeconds)} -> {Every(Row.IntervalSeconds)}");
                if (Live.CheckPath != Row.CheckPath) details.Add($"check {Live.CheckPath} -> {Row.CheckPath}");
                if (!Live.Secrets.SequenceEqual(Row.Secrets, StringComparer.Ordinal))
                    details.Add($"secrets {List(Live.Secrets)} -> {List(Row.Secrets)}");
                if (Live.Prompt != Row.Prompt) details.Add("prompt changed");
                if (Live.Machine != Row.Machine) details.Add($"machine {Live.Machine} -> {Row.Machine}");
                if (Live.FactoryAgent != Row.FactoryAgent) details.Add($"seat {Live.FactoryAgent} -> {Row.FactoryAgent}");
                if (Live.RepoPath != Row.RepoPath) details.Add($"folder {Live.RepoPath} -> {Row.RepoPath}");
                return $"Trigger {Name} changed: {string.Join("; ", details)}; its check is read from {from}.";
        }
    }

    private static string Every(int seconds) => FactoryDefinitionChanges.When(
        new SeatStartDefinition(null, null, null, seconds, null, Array.Empty<string>(), "", true)).ToLowerInvariant();

    private static string SecretsText(IReadOnlyList<string> secrets)
        => secrets.Count == 0 ? "" : $" with {(secrets.Count == 1 ? "secret" : "secrets")} {List(secrets)}";

    private static string List(IReadOnlyList<string> items) => items.Count == 0 ? "none" : string.Join(", ", items);
}

/// <summary>
/// The plan: the registry row to write (without who registered it and when, which the apply sets), what changes in
/// it, then the schedule steps in the order they are written - each seat's kept, changed and created schedules in
/// seat id and start order, then the deletes - and the trigger steps in the same order.
/// </summary>
public sealed record PublishPlan(string Factory, FactoryRegistryEntity Registry, IReadOnlyList<string> RegistrySentences,
    IReadOnlyList<PlannedSchedule> Schedules, IReadOnlyList<PlannedTrigger> Triggers)
{
    /// <summary>Every step in one sentence, in order, as a publish WOULD take them.</summary>
    public IReadOnlyList<string> Sentences()
        => RegistrySentences.Concat(Schedules.Select(s => s.Sentence())).Concat(Triggers.Select(t => t.Sentence())).ToList();
}

/// <summary>A refusal before any write: the outcome, one sentence, and the findings when a row rule refused (file and
/// line, the check's shape).</summary>
public sealed record FactoryPublishRefusal(FactoryDefinitionOutcome Outcome, string Sentence, IReadOnlyList<DefinitionFinding> Findings);

/// <summary>What <see cref="FactoryPublishPlan.Compose"/> returns: the plan, or the refusal.</summary>
public sealed record FactoryPublishComposition(PublishPlan? Plan, FactoryPublishRefusal? Refusal);
