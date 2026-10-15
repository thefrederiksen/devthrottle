using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Registry;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>
/// WHAT A PUBLISH WILL DO TO THE LIVE FACTORY (Factory Definition, phase 4; design sections 2 to 4). One pure function:
/// a checked definition and the factory as it runs today - its registry row, its schedules and its triggers - go in,
/// and the plan comes out: the registry row to write, each schedule to keep, change, create or delete, each trigger to
/// delete, and one sentence for every step. Or the refusals, and nothing is planned. It reads no database and no clock.
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
    /// <summary>The refusal for an interval start with no check: a publish turns only a timed start into a schedule.</summary>
    public const string IntervalStartNotYet =
        "This start runs on an interval and has no check. Until phase 5b a publish turns only a start with a cron time " +
        "into a schedule, so give it a cron time, or publish it without the start.";

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

        // UNTIL PHASE 5 A DEFINED FACTORY RUNS IN ITS REGISTERED FOLDER, so a factory the registry does not hold has
        // nowhere to run, and an archived one is the owner's to bring back first.
        if (registry is null)
            return Refused(FactoryDefinitionOutcome.NotRegistered,
                $"{factory} is not registered, and until phase 5 a defined factory runs in its registered folder. Register it first, then publish.");
        if (registry.ArchivedAtUtc is not null)
            return Refused(FactoryDefinitionOutcome.Archived, $"{factory} is archived. Restore it, then publish.");

        var findings = new List<DefinitionFinding>();

        // A START WITH A CHECK CANNOT RUN BEFORE PHASE 5B. The whole-definition check refuses it at publish on its
        // check line; refused here too, because a plan that dropped it would quietly lose a start, and one that made it
        // a schedule would run it without its check.
        // An interval start with no check is refused by the check at every save, so a checked definition never holds
        // one; a version stored before that rule can, and it is refused in its own words (review of 4006, observation 4).
        foreach (var seat in definition.Seats)
        {
            if (seat.Starts.Any(s => s.Check is not null))
                findings.Add(Error(SeatFile(seat), FactoryDefinitionCheck.CheckStartNotYet));
            else if (seat.Starts.Any(s => s.Cron is null))
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
            var live = mine.Where(j => j.Seat == seat.Seat).ToList();
            var pairing = FactoryDefinitionChanges.PairStarts(live.Select(AsStart).ToList(), seat.Starts);
            var ids = new List<string>();
            var ofSeat = new List<PlannedSchedule>();
            foreach (var pair in pairing.Pairs)
            {
                var row = live[pair.Old];
                var job = Kept(Job(definition, registry, seat, pair.New), row);
                var action = SameDefinition(row, job) ? PlannedScheduleAction.Keep : PlannedScheduleAction.Update;
                ofSeat.Add(new PlannedSchedule(action, row.Id, seat.Seat, pair.New, job, CronJobStore.ToDto(row)));
                ids.Add(row.Id);
            }
            foreach (var n in pairing.UnpairedNew)
            {
                ofSeat.Add(new PlannedSchedule(PlannedScheduleAction.Create, null, seat.Seat, n, Job(definition, registry, seat, n), null));
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
                row2 = FactoryRegistryStore.Validate(request);
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
            var (ok, error) = CronSchedule.Validate(p.Job!);
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

        var triggers = liveTriggers.Where(t => string.Equals(t.Factory, factory, StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Id)
            .Select(t => new PlannedTriggerDelete(t.Id, t.Name)).ToList();

        var plan = new PublishPlan(factory, row2, RegistrySentences(registry, row2), planned.Concat(deletes).ToList(), triggers);
        FileLog.Write($"[FactoryPublishPlan] Compose: factory={factory}, keep={plan.Schedules.Count(s => s.Action == PlannedScheduleAction.Keep)}, update={plan.Schedules.Count(s => s.Action == PlannedScheduleAction.Update)}, create={plan.Schedules.Count(s => s.Action == PlannedScheduleAction.Create)}, delete={plan.Schedules.Count(s => s.Action == PlannedScheduleAction.Delete)}, triggers={triggers.Count}");
        return new FactoryPublishComposition(plan, null);
    }

    // -------------------------------------------------------------------------------------------------------------
    // The rows
    // -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The schedule one start makes (design section 3): the seat's machine or the default, the start's prompt, the
    /// registry's folder (where today's run starts; phase 5 replaces it), named "title - seat name", with " (2)",
    /// " (3)" in start order when the seat has more than one start, so a rename shows on every schedule.
    /// </summary>
    private static CronJobDto Job(FactoryDefinition d, FactoryRegistryEntity registry, SeatDefinition seat, int startIndex)
    {
        var start = seat.Starts[startIndex];
        return new CronJobDto
        {
            Name = $"{d.Title} - {seat.Name}" + (startIndex == 0 ? "" : $" ({startIndex + 1})"),
            Enabled = true,
            ScheduleKind = CronSchedule.KindRecurring,
            CronExpression = start.Cron,
            RunAt = null,
            TimeZoneId = start.TimeZone ?? "",
            Factory = d.Factory,
            Seat = seat.Seat,
            Target = new CronJobTarget { Machine = seat.MachineOrDefault(d.Defaults) },
            Action = new CronJobAction { RepoPath = registry.Folder, Seed = start.Prompt },
            PreventOverlap = start.PreventOverlap,
            NotifyOn = CronNotify.None,
        };
    }

    /// <summary>
    /// A paired schedule keeps what is the person's, not the definition's: its switch (a publish never turns a schedule
    /// on or off), its notifications and its work-list settings.
    /// </summary>
    private static CronJobDto Kept(CronJobDto job, CronJobEntity live)
    {
        job.Id = live.Id;
        job.Enabled = live.Enabled;
        job.NotifyOn = live.NotifyOn;
        job.NotifyWebhookUrl = live.NotifyWebhookUrl;
        job.Action.WorkListName = live.Action.WorkListName;
        job.Action.AutoDismiss = live.Action.AutoDismiss;
        return job;
    }

    /// <summary>True when a paired schedule already says everything the definition would write.</summary>
    private static bool SameDefinition(CronJobEntity live, CronJobDto job)
        => live.Name == job.Name && live.ScheduleKind == job.ScheduleKind && live.CronExpression == job.CronExpression
           && live.RunAt == job.RunAt && live.TimeZoneId == job.TimeZoneId && live.Factory == job.Factory
           && live.Seat == job.Seat && live.Target.Machine == job.Target.Machine
           && live.Action.RepoPath == job.Action.RepoPath && live.Action.Seed == job.Action.Seed
           && live.PreventOverlap == job.PreventOverlap;

    /// <summary>
    /// A live schedule as a start, so the one pairing can match it: its time (whatever kind it is, it is a time, never
    /// an interval), its zone, its prompt and its overlap. A schedule has no check and no secrets.
    /// </summary>
    private static SeatStartDefinition AsStart(CronJobEntity job)
        => new(job.CronExpression ?? job.RunAt ?? "", job.TimeZoneId, null, null, null, Array.Empty<string>(),
            job.Action.Seed ?? "", job.PreventOverlap);

    /// <summary>
    /// The manifest the registry's own rules are run on (design section 3): factory.yaml's fields, the default machine
    /// as the computer, the folder KEPT from the registered row, no goal file (the goal text is in the definition), and
    /// one seat per seat file in seat id order, its brief file the seat file where the brief now lives.
    /// </summary>
    private static RegisterFactoryRequest Manifest(FactoryDefinition d, FactoryRegistryEntity registry,
        IReadOnlyDictionary<string, List<string>> scheduleIdsOfSeat) => new()
    {
        Factory = d.Factory,
        Title = d.Title,
        Folder = registry.Folder,
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

    private static List<string> RegistrySentences(FactoryRegistryEntity old, FactoryRegistryEntity now)
    {
        var said = new List<string>();
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
                if (Live.PreventOverlap != Job.PreventOverlap) details.Add($"prevent overlap {(Live.PreventOverlap ? "on" : "off")} -> {(Job.PreventOverlap ? "on" : "off")}");
                if (Live.Action.RepoPath != Job.Action.RepoPath) details.Add($"folder {Live.Action.RepoPath} -> {Job.Action.RepoPath}");
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

/// <summary>One trigger the publish deletes. Its run history is kept.</summary>
public sealed record PlannedTriggerDelete(Guid Id, string Name)
{
    /// <summary>The step in one sentence.</summary>
    public string Sentence()
        => $"Trigger {Name} deleted: checks from a definition run from phase 5b, so until then a defined factory has no triggers; its run history is kept.";
}

/// <summary>
/// The plan: the registry row to write (without who registered it and when, which the apply sets), what changes in
/// it, then the schedule steps in the order they are written - each seat's kept, changed and created schedules in
/// seat id and start order, then the deletes - and the triggers to delete.
/// </summary>
public sealed record PublishPlan(string Factory, FactoryRegistryEntity Registry, IReadOnlyList<string> RegistrySentences,
    IReadOnlyList<PlannedSchedule> Schedules, IReadOnlyList<PlannedTriggerDelete> Triggers)
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
