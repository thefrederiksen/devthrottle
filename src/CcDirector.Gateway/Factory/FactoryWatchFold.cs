using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.Running;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// "Watch your factory", decided once (version one, piece 2; critical rule 7). Pure: it reads the registered factory
/// and the activity rows of one window and returns the finished watch view - the floor laid out from the factory's
/// shape, one character per seat in a colour that is the seat's own, and the rows as an ordered timeline of steps.
/// The player draws it and decides nothing.
///
/// The rules it owns, each tested:
///  - The layout comes from the factory's shape: any named line gives line halls; otherwise up to 3 seats is a
///    workshop and 4 or more is the Boss in the middle with the others on rings (a further ring beyond 12, and as many
///    as the seats need, so no station ever overlaps another).
///  - A seat's colour is picked from its own id, never from its place in the list or from the activity, so it does
///    not change between windows or when the seats are reordered.
///  - Every activity outcome maps to exactly one step kind (<see cref="StepKind"/>), and a new outcome fails a test.
///  - Runs of empty checks are grouped by the Activity tab's own grouping (<see cref="FactoryAgentsFold.EmptyCheckRuns"/>),
///    so the two views can never disagree on what a quiet stretch is.
///  - The words of a step come from ONE place (<see cref="FactoryWatchWords"/>). No field this fold builds reads a
///    computer, a folder, a session, an actor or a link.
///  - THE CONTRIBUTOR VIEW IS ASSEMBLED FROM AN ALLOW-LIST, never by cleaning the owner's view: the title, the floor's
///    positions, the stations' and characters' labels (each checked against the account's machine names, a refused one
///    replaced by a fixed neutral word, never edited), the colours, the step kinds and times, the contributor sentence
///    when it passes the check again, and whether a step is the contributor's own. Seats and steps get opaque keys. A
///    hiccup has no words; a machine problem is only "Your request is waiting its turn."; work that is not the
///    contributor's own carries no words. Notes, counts, owner acts and every internal field are absent.
/// </summary>
public static class FactoryWatchFold
{
    internal const int WorkshopMaxSeats = 3;
    internal const int RingMaxSeats = 12;
    internal const int MaxPerLineRow = 6;

    internal const double Margin = 1;
    internal const double BenchWidth = 2;
    internal const double BenchHeight = 1.4;
    internal const double BenchGap = 1;
    internal const double HomeOffset = 0.5;
    internal const double BossSize = 2.4;
    internal const double RingStationWidth = 1.8;
    internal const double RingStationHeight = 1.3;
    internal const double InnerRadius = 4.5;
    internal const double RingStep = 3.5;

    /// <summary>
    /// The least distance between the centres of two neighbouring stations on a ring, and between two rings. Two ring
    /// stations whose centres are further apart than the station's diagonal (2.22 tiles) can never overlap, whatever
    /// the angle between them; this keeps a little air on top.
    /// </summary>
    internal const double MinCentreGap = 2.5;
    internal const double HallPad = 0.6;
    internal const double HallGap = 0.8;
    internal const double RowGap = 1.2;

    /// <summary>The characters' colours. Twelve, so every seat of a factory up to 12 seats has its own.</summary>
    internal static readonly string[] Palette =
    {
        "#ffb020", "#5aa0ff", "#4cc38a", "#ff7a59", "#a57bf0", "#2ec4c4",
        "#f06fa8", "#c7d23a", "#8d9cff", "#e0904a", "#6fd1ff", "#b58b6a",
    };

    /// <summary>
    /// The watch view of <paramref name="factory"/> for <paramref name="audience"/>. The contributor audience needs
    /// <paramref name="contributor"/> (what is theirs, and the account's machine names); the owner audience takes none.
    /// </summary>
    public static FactoryWatchViewDto View(RegisteredFactoryDto factory, IReadOnlyList<FactoryActivityDto> windowRows,
        bool windowTruncated, FactoryWindow window, TimeZoneInfo zone, FactoryWatchAudience audience,
        FactoryWatchContributorScope? contributor = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(windowRows);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(zone);
        FileLog.Write($"[FactoryWatchFold] View: factory={factory.Factory}, audience={audience}, seats={factory.Seats.Count}, rows={windowRows.Count}");
        switch (audience)
        {
            case FactoryWatchAudience.Owner when contributor is not null:
                throw new ArgumentException("The owner's watch view takes no contributor scope.", nameof(contributor));
            case FactoryWatchAudience.Contributor when contributor is null:
                throw new ArgumentException("A contributor's watch view needs the contributor's scope: what is theirs, and the account's machine names.", nameof(contributor));
            case FactoryWatchAudience.Owner:
            case FactoryWatchAudience.Contributor:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(audience), audience, "Unknown watch audience.");
        }
        var stranger = windowRows.FirstOrDefault(r => !string.Equals(r.Factory, factory.Factory, StringComparison.OrdinalIgnoreCase));
        if (stranger is not null)
            throw new ArgumentException($"The watch view of '{factory.Factory}' was handed a row of '{stranger.Factory}' ({stranger.Id}); only the factory's own rows may be folded.", nameof(windowRows));

        if (contributor is not null)
            return ContributorView(factory, windowRows, window, zone, contributor);

        var view = new FactoryWatchViewDto
        {
            FactoryId = factory.Factory,
            Title = factory.Title,
            Window = FactoryAgentsFold.WindowDto(window, zone),
        };
        LayOut(factory, view);
        view.Characters = Characters(factory, view.Stations);

        // ---------- the timeline
        var seatIds = factory.Seats.ToDictionary(s => s.Id, s => s.Id, StringComparer.OrdinalIgnoreCase);
        var (runs, ownerActs, unregistered) = Runs(factory, windowRows);
        foreach (var run in runs)
            view.Steps.Add(Step(run, seatIds[run[0].FactoryAgent], window, zone));

        view.EmptyText = view.Steps.Count == 0 ? $"Nothing happened in this factory {FactoryAgentsFold.WindowPhrase(window, zone)}." : null;
        view.TruncatedText = windowTruncated
            ? $"This window holds more than {windowRows.Count} rows; only the first {windowRows.Count} are shown. Choose a shorter window."
            : null;

        // ---------- what it rests on
        view.Notes.Add("Each character is one seat of this factory. What it does comes from the factory's activity record; only the walking and the timing are drawn.");
        if (unregistered > 0)
            view.Notes.Add($"{unregistered} {(unregistered == 1 ? "row names a worker" : "rows name workers")} this factory has not registered as a seat, so {(unregistered == 1 ? "it is" : "they are")} not shown.");

        FileLog.Write($"[FactoryWatchFold] View: factory={factory.Factory}, layout={view.Layout}, stations={view.Stations.Count}, steps={view.Steps.Count}, unregistered={unregistered}, ownerActs={ownerActs}");
        return view;
    }

    /// <summary>
    /// THE MAP FROM AN ACTIVITY OUTCOME TO A STEP KIND. Every outcome in <see cref="FactoryActivityOutcome.All"/> has
    /// exactly one; an outcome this does not know throws, so a new outcome is caught by a test on its first day.
    /// </summary>
    public static string StepKind(string outcome) => outcome switch
    {
        FactoryActivityOutcome.Started => FactoryWatchStepKind.Woke,
        FactoryActivityOutcome.Done => FactoryWatchStepKind.Delivered,
        FactoryActivityOutcome.Screened => FactoryWatchStepKind.Delivered,
        FactoryActivityOutcome.Talked => FactoryWatchStepKind.Talked,
        FactoryActivityOutcome.Asked => FactoryWatchStepKind.Talked,
        FactoryActivityOutcome.Escalated => FactoryWatchStepKind.Talked,
        FactoryActivityOutcome.Allowed => FactoryWatchStepKind.Talked,
        FactoryActivityOutcome.NothingToDo => FactoryWatchStepKind.CheckedNothing,
        FactoryActivityOutcome.Paused => FactoryWatchStepKind.CheckedNothing,
        FactoryActivityOutcome.Skipped => FactoryWatchStepKind.CheckedNothing,
        FactoryActivityOutcome.Blocked => FactoryWatchStepKind.Hiccup,
        FactoryActivityOutcome.SentBack => FactoryWatchStepKind.Hiccup,
        FactoryActivityOutcome.Failed => FactoryWatchStepKind.Hiccup,
        _ => throw new InvalidOperationException($"The watch view has no step for the activity outcome '{outcome}'. Add it to FactoryWatchFold.StepKind."),
    };

    /// <summary>
    /// A row the owner wrote by acting on the factory (archive, restore, mark handled): its actor is the owner's,
    /// or it names the owner as the worker. It is not a seat's work, so it never becomes a seat's step, and it is not
    /// counted with the unregistered workers either: it waits for the owner's desk, which a later piece adds.
    /// </summary>
    internal static bool IsOwnerAct(FactoryActivityDto r) =>
        r.Actor.StartsWith(OwnerActorPrefix, StringComparison.Ordinal)
        || string.Equals(r.FactoryAgent, OwnerAgent, StringComparison.OrdinalIgnoreCase);

    /// <summary>The start of every actor the Gateway stamps for an owner's act: "owner (device:...)".</summary>
    internal const string OwnerActorPrefix = "owner (";

    /// <summary>The worker an owner's act on the whole factory names (archive, restore).</summary>
    internal const string OwnerAgent = "owner";

    // The seats' rows of the window, oldest first, as runs (a run of empty checks is one), with what was left out.
    private static (List<IReadOnlyList<FactoryActivityDto>> Runs, int OwnerActs, int Unregistered) Runs(
        RegisteredFactoryDto factory, IReadOnlyList<FactoryActivityDto> windowRows)
    {
        var seatIds = factory.Seats.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = windowRows.OrderBy(r => r.OccurredUtc).ThenBy(r => r.RecordedUtc).ThenBy(r => r.Id).ToList();
        var ownerActs = ordered.Count(IsOwnerAct);
        var seatRows = ordered.Where(r => !IsOwnerAct(r)).ToList();
        var unregistered = seatRows.Count(r => !seatIds.Contains(r.FactoryAgent));
        seatRows = seatRows.Where(r => seatIds.Contains(r.FactoryAgent)).ToList();
        return (FactoryAgentsFold.EmptyCheckRuns(seatRows), ownerActs, unregistered);
    }

    private static string RunKind(IReadOnlyList<FactoryActivityDto> run) =>
        run.Count > 1 ? FactoryWatchStepKind.CheckedNothing : StepKind(run[0].Outcome);

    private static string RunTime(IReadOnlyList<FactoryActivityDto> run, FactoryWindow window, TimeZoneInfo zone) =>
        run.Count > 1
            ? $"{FactoryAgentsFold.ClockText(run[0].OccurredUtc, window, zone)}-{FactoryAgentsFold.ClockText(run[^1].OccurredUtc, window, zone)}"
            : FactoryAgentsFold.ClockText(run[0].OccurredUtc, window, zone);

    private static FactoryWatchStepDto Step(IReadOnlyList<FactoryActivityDto> run, string seat, FactoryWindow window, TimeZoneInfo zone)
    {
        var kind = RunKind(run);
        return new FactoryWatchStepDto
        {
            Key = run[0].Id.ToString(),
            Kind = kind,
            Seat = seat,
            Utc = run[0].OccurredUtc,
            ToUtc = run.Count > 1 ? run[^1].OccurredUtc : null,
            Time = RunTime(run, window, zone),
            Words = FactoryWatchWords.ForOwner(run),
            EmptyChecks = kind == FactoryWatchStepKind.CheckedNothing ? run.Count : 0,
            Alarm = kind == FactoryWatchStepKind.Hiccup,
        };
    }

    // ---------------------------------------------------------------------------------------------------------
    // The contributor's view: assembled from the allow-list, field by field. Nothing from a row or a seat reaches it
    // except through ContributorSentenceCheck, or as a fixed word, or as a number.

    private static FactoryWatchViewDto ContributorView(RegisteredFactoryDto factory, IReadOnlyList<FactoryActivityDto> windowRows,
        FactoryWindow window, TimeZoneInfo zone, FactoryWatchContributorScope scope)
    {
        var machines = scope.MachineNames;

        // The floor is laid out exactly as the owner's (positions are numbers decided from the seats' shape); then each
        // station is rebuilt with an opaque key and checked words. The scratch layout never leaves this method.
        var floor = new FactoryWatchViewDto();
        LayOut(factory, floor);
        var keyOf = factory.Seats.Select((s, i) => (s.Id, Key: $"seat-{i + 1}"))
            .ToDictionary(x => x.Id, x => x.Key, StringComparer.OrdinalIgnoreCase);
        var colours = Colours(factory.Seats.Select(s => s.Id));
        var lineName = floor.Lines.ToDictionary(l => l.Name, l => FactoryWatchWords.LineName(l.Name, l.Index, machines), StringComparer.Ordinal);

        var view = new FactoryWatchViewDto
        {
            FactoryId = "",
            Title = FactoryWatchWords.Title(factory.Title, machines),
            Layout = floor.Layout,
            FloorWidth = floor.FloorWidth,
            FloorHeight = floor.FloorHeight,
            Window = FactoryAgentsFold.WindowDto(window, zone),
        };
        view.Lines = floor.Lines.Select(l => new FactoryWatchLineDto
        {
            Name = lineName[l.Name], Index = l.Index, Gx = l.Gx, Gy = l.Gy, Width = l.Width, Height = l.Height,
        }).ToList();
        foreach (var st in floor.Stations)
        {
            var seat = factory.Seats.First(s => string.Equals(s.Id, st.Id, StringComparison.OrdinalIgnoreCase));
            var label = Clip(FactoryWatchWords.SeatLabel(seat.Name, st.IsBoss, machines), 22);
            view.Stations.Add(new FactoryWatchStationDto
            {
                Id = keyOf[seat.Id],
                Label = label,
                Sub = Clip(FactoryWatchWords.SeatLabel(seat.Role, st.IsBoss, machines), 30),
                IsBoss = st.IsBoss,
                Gx = st.Gx, Gy = st.Gy, Width = st.Width, Height = st.Height,
                Line = st.Line is null ? null : lineName[st.Line],
            });
            view.Characters.Add(new FactoryWatchCharacterDto
            {
                Seat = keyOf[seat.Id],
                Name = label,
                Colour = colours[seat.Id],
                Station = keyOf[seat.Id],
                HomeGx = Round(st.Gx + st.Width / 2),
                HomeGy = Round(st.Gy + st.Height + HomeOffset / 2),
            });
        }

        var (runs, ownerActs, unregistered) = Runs(factory, windowRows);
        foreach (var run in runs)
        {
            var mine = scope.IsMine(run[0]);
            var kind = run.Count == 1 && IsMachineProblem(run[0]) ? FactoryWatchStepKind.Waiting : RunKind(run);
            view.Steps.Add(new FactoryWatchStepDto
            {
                Key = $"step-{view.Steps.Count + 1}",
                Kind = kind,
                Seat = keyOf[run[0].FactoryAgent],
                Utc = run[0].OccurredUtc,
                ToUtc = run.Count > 1 ? run[^1].OccurredUtc : null,
                Time = RunTime(run, window, zone),
                Words = FactoryWatchWords.ForContributor(run, kind, mine, machines),
                EmptyChecks = kind == FactoryWatchStepKind.CheckedNothing ? run.Count : 0,
                Mine = mine,
                Alarm = false, // a contributor's hiccup has no red tone (piece 3 rule); never an alarm in this view
            });
        }
        view.EmptyText = view.Steps.Count == 0 ? $"Nothing happened in your factory {FactoryAgentsFold.WindowPhrase(window, zone)}." : null;

        FileLog.Write($"[FactoryWatchFold] ContributorView: factory={factory.Factory}, wholeFactory={scope.WholeFactory}, steps={view.Steps.Count}, " +
                      $"mine={view.Steps.Count(s => s.Mine)}, waiting={view.Steps.Count(s => s.Kind == FactoryWatchStepKind.Waiting)}, " +
                      $"left out: ownerActs={ownerActs}, unregistered={unregistered}");
        return view;
    }

    /// <summary>
    /// A MACHINE PROBLEM, decided from who wrote the row, never from its text: a failure written by the Gateway's own
    /// machinery - a trigger or a schedule - means the seat could not start or did not finish as scheduled (no
    /// Director, no free workbench, a start the Gateway gave up on, did not run, did not report). A failure the seat's
    /// own session wrote is the seat's hiccup. Known gap: a schedule's "reported" problem (the seat's session itself
    /// reported a problem) is written by the schedule too, so it reads as waiting; telling it apart needs the text.
    /// </summary>
    internal static bool IsMachineProblem(FactoryActivityDto r) =>
        string.Equals(r.Outcome, FactoryActivityOutcome.Failed, StringComparison.Ordinal)
        && (r.Actor.StartsWith(TriggerService.ActorPrefix, StringComparison.Ordinal)
            || r.Actor.StartsWith(CronRunResultService.ActorPrefix, StringComparison.Ordinal));

    // ---------------------------------------------------------------------------------------------------------
    // The floor

    private static void LayOut(RegisteredFactoryDto factory, FactoryWatchViewDto view)
    {
        var boss = factory.BossSeat is null
            ? null
            : factory.Seats.FirstOrDefault(s => string.Equals(s.Id, factory.BossSeat, StringComparison.OrdinalIgnoreCase));
        var workers = factory.Seats.Where(s => !ReferenceEquals(s, boss)).ToList();

        if (workers.Any(s => s.Line is not null))
            LayOutLines(boss, workers, view);
        else if (factory.Seats.Count <= WorkshopMaxSeats)
            LayOutWorkshop(boss, workers, view);
        else
            LayOutHub(boss, workers, view);
    }

    private static void LayOutWorkshop(RegisteredFactorySeatDto? boss, List<RegisteredFactorySeatDto> workers, FactoryWatchViewDto view)
    {
        view.Layout = FactoryWatchLayout.Workshop;
        var benches = (boss is null ? workers : workers.Prepend(boss)).ToList();
        for (var i = 0; i < benches.Count; i++)
            view.Stations.Add(Station(benches[i], ReferenceEquals(benches[i], boss), Margin + i * (BenchWidth + BenchGap), Margin + 1, BenchWidth, BenchHeight, null));
        var count = Math.Max(1, benches.Count);
        view.FloorWidth = Round(Margin * 2 + count * BenchWidth + (count - 1) * BenchGap);
        view.FloorHeight = Round(Margin * 2 + 1 + BenchHeight + 2);
    }

    private static void LayOutHub(RegisteredFactorySeatDto? boss, List<RegisteredFactorySeatDto> workers, FactoryWatchViewDto view)
    {
        view.Layout = FactoryWatchLayout.Hub;
        // The first ring holds the rest of a 12-seat factory; each further ring, 3.5 tiles out, holds as many as fit
        // with MinCentreGap between neighbours. As many rings as the seats need, so nothing ever overlaps.
        var rings = new List<(double Radius, List<RegisteredFactorySeatDto> Seats)>();
        var left = workers;
        var radius = InnerRadius;
        var capacity = RingMaxSeats - (boss is null ? 0 : 1);
        while (left.Count > 0)
        {
            rings.Add((radius, left.Take(capacity).ToList()));
            left = left.Skip(capacity).ToList();
            radius += RingStep;
            capacity = RingCapacity(radius);
        }
        var outermost = rings.Count == 0 ? InnerRadius : rings[^1].Radius;
        var centre = outermost + RingStationWidth / 2 + Margin + HomeOffset;
        view.FloorWidth = Round(centre * 2);
        view.FloorHeight = Round(centre * 2);

        if (boss is not null)
            view.Stations.Add(Station(boss, true, centre - BossSize / 2, centre - BossSize / 2, BossSize, BossSize, null));
        foreach (var (r, seats) in rings)
            Ring(seats, r, centre, view);
    }

    /// <summary>How many stations fit on a ring of this radius with <see cref="MinCentreGap"/> between neighbours.</summary>
    internal static int RingCapacity(double radius) => (int)Math.Floor(Math.PI / Math.Asin(MinCentreGap / (2 * radius)));

    // The seats around a ring, in the factory's own seat order, the first at the top and on clockwise.
    private static void Ring(List<RegisteredFactorySeatDto> seats, double radius, double centre, FactoryWatchViewDto view)
    {
        for (var i = 0; i < seats.Count; i++)
        {
            var angle = -Math.PI / 2 + i * 2 * Math.PI / seats.Count;
            var cx = centre + radius * Math.Cos(angle);
            var cy = centre + radius * Math.Sin(angle);
            view.Stations.Add(Station(seats[i], false, cx - RingStationWidth / 2, cy - RingStationHeight / 2, RingStationWidth, RingStationHeight, null));
        }
    }

    private static void LayOutLines(RegisteredFactorySeatDto? boss, List<RegisteredFactorySeatDto> workers, FactoryWatchViewDto view)
    {
        view.Layout = FactoryWatchLayout.Lines;
        // The lines in the factory's own seat order, a seat with no line in "Other", always last - as the floor does.
        string LineOf(RegisteredFactorySeatDto s) => s.Line ?? FactoryFloorFold.OtherLane;
        var names = workers.Select(LineOf).Distinct(StringComparer.Ordinal).ToList();
        if (names.Remove(FactoryFloorFold.OtherLane)) names.Add(FactoryFloorFold.OtherLane);
        var widest = Math.Min(MaxPerLineRow, names.Max(n => workers.Count(s => LineOf(s) == n)));
        var hallWidth = HallPad * 2 + widest * BenchWidth + (widest - 1) * BenchGap;

        var top = Margin;
        if (boss is not null)
        {
            view.Stations.Add(Station(boss, true, Margin + (hallWidth - BossSize) / 2, top, BossSize, BenchHeight, null));
            top += BenchHeight + HomeOffset + RowGap;
        }
        for (var li = 0; li < names.Count; li++)
        {
            var seats = workers.Where(s => LineOf(s) == names[li]).ToList();
            var rows = (seats.Count + MaxPerLineRow - 1) / MaxPerLineRow;
            var height = HallPad * 2 + rows * (BenchHeight + HomeOffset) + (rows - 1) * RowGap;
            view.Lines.Add(new FactoryWatchLineDto { Name = names[li], Index = li, Gx = Margin, Gy = Round(top), Width = Round(hallWidth), Height = Round(height) });
            for (var i = 0; i < seats.Count; i++)
            {
                var gx = Margin + HallPad + (i % MaxPerLineRow) * (BenchWidth + BenchGap);
                var gy = top + HallPad + (i / MaxPerLineRow) * (BenchHeight + HomeOffset + RowGap);
                view.Stations.Add(Station(seats[i], false, gx, gy, BenchWidth, BenchHeight, names[li]));
            }
            top += height + HallGap;
        }
        view.FloorWidth = Round(Margin * 2 + hallWidth);
        view.FloorHeight = Round(top - HallGap + Margin);
    }

    private static FactoryWatchStationDto Station(RegisteredFactorySeatDto seat, bool isBoss, double gx, double gy, double width, double height, string? line) => new()
    {
        Id = seat.Id,
        Label = Clip(seat.Name, 22),
        Sub = Clip(seat.Role, 30),
        IsBoss = isBoss,
        Gx = Round(gx), Gy = Round(gy), Width = Round(width), Height = Round(height),
        Line = line,
    };

    // ---------------------------------------------------------------------------------------------------------
    // The characters

    private static List<FactoryWatchCharacterDto> Characters(RegisteredFactoryDto factory, List<FactoryWatchStationDto> stations)
    {
        var colours = Colours(factory.Seats.Select(s => s.Id));
        return stations.Select(st =>
        {
            var seat = factory.Seats.First(s => string.Equals(s.Id, st.Id, StringComparison.OrdinalIgnoreCase));
            return new FactoryWatchCharacterDto
            {
                Seat = seat.Id,
                Name = Clip(seat.Name, 22),
                Colour = colours[seat.Id],
                Station = st.Id,
                HomeGx = Round(st.Gx + st.Width / 2),
                HomeGy = Round(st.Gy + st.Height + HomeOffset / 2),
            };
        }).ToList();
    }

    /// <summary>
    /// Each seat's colour, from its OWN id: a stable hash picks a palette slot, and a slot already taken moves on to the
    /// next free one, the seats taken in id order. The hash is not .NET's string hash, which changes from process to
    /// process. Distinct for up to <see cref="Palette"/> seats; beyond that colours repeat.
    /// </summary>
    internal static Dictionary<string, string> Colours(IEnumerable<string> seatIds)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<int>();
        foreach (var id in seatIds.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(i => i.ToLowerInvariant(), StringComparer.Ordinal))
        {
            var slot = (int)(StableHash(id.ToLowerInvariant()) % (uint)Palette.Length);
            if (taken.Count < Palette.Length)
                while (taken.Contains(slot)) slot = (slot + 1) % Palette.Length;
            taken.Add(slot);
            result[id] = Palette[slot];
        }
        return result;
    }

    // FNV-1a, 32 bits.
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash;
    }

    private static double Round(double value) => Math.Round(value, 2);

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 3)].TrimEnd() + "...";
}

/// <summary>
/// What a contributor's watch view may count as theirs, and the account's machine names its words are checked against.
/// Built by the endpoint, never by a client. FAILS SAFE: a row is the contributor's own only when the scope says so;
/// anything else is a plain crate with no words.
///
/// Version one decides at the factory level: <see cref="WholeFactory"/> is true when the factory was sent to this
/// contributor by name and serves only them, and then every row is theirs. The request level comes later, when the
/// record carries the request a row was done for: the scope then gains the contributor's request ids, and
/// <see cref="IsMine"/> - the one place the rule lives - matches on them.
/// </summary>
public sealed record FactoryWatchContributorScope(bool WholeFactory, IReadOnlyList<string> MachineNames)
{
    /// <summary>True when <paramref name="row"/> is the contributor's own work.</summary>
    public bool IsMine(FactoryActivityDto row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return WholeFactory;
    }
}

/// <summary>
/// THE ONE PLACE A WATCH STEP'S WORDS COME FROM. The owner sees the factory's own sentence, exactly as the Activity
/// tab shows it. A contributor sees only the allow-list: the seat's contributor sentence when it passes
/// <see cref="ContributorSentenceCheck"/> again at read time (a row may predate a machine being registered), otherwise
/// a fixed sentence from <see cref="Fixed"/> - never the row's own text. Never reads a computer, a folder, a session,
/// an actor or a link.
/// </summary>
internal static class FactoryWatchWords
{
    internal const string WaitingTurn = "Your request is waiting its turn.";
    internal const string NeutralBoss = "Boss";
    internal const string NeutralWorker = "Worker";
    internal const string NeutralTitle = "Your factory";

    /// <summary>The fixed sentence for each step kind a contributor may see words for. A hiccup has none.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Fixed = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [FactoryWatchStepKind.Woke] = "Started work.",
        [FactoryWatchStepKind.Delivered] = "Finished a piece of work.",
        [FactoryWatchStepKind.Talked] = "Talked it over.",
        [FactoryWatchStepKind.CheckedNothing] = "Checked for new work. Nothing yet.",
        [FactoryWatchStepKind.Waiting] = WaitingTurn,
    };

    internal static string ForOwner(IReadOnlyList<FactoryActivityDto> run) =>
        run.Count > 1 ? FactoryAgentsFold.CollapsedWhat(run) : run[0].What;

    internal static string ForContributor(IReadOnlyList<FactoryActivityDto> run, string kind, bool mine, IReadOnlyList<string> machines)
    {
        if (!mine) return "";                                           // other people's work: a plain crate
        if (kind == FactoryWatchStepKind.Hiccup) return "";             // a hiccup: no red, no words
        if (kind == FactoryWatchStepKind.Waiting) return WaitingTurn;   // a machine problem, whatever the seat wrote
        if (run.Count == 1 && run[0].ForContributor is { } sentence && Passes(sentence, machines))
            return sentence;
        return Fixed[kind];
    }

    /// <summary>A seat's name or role, or the fixed neutral word for its place when the check refuses it.</summary>
    internal static string SeatLabel(string text, bool isBoss, IReadOnlyList<string> machines) =>
        Passes(text, machines) ? text : isBoss ? NeutralBoss : NeutralWorker;

    internal static string Title(string title, IReadOnlyList<string> machines) =>
        Passes(title, machines) ? title : NeutralTitle;

    internal static string LineName(string name, int index, IReadOnlyList<string> machines) =>
        Passes(name, machines) ? name : $"Line {index + 1}";

    private static bool Passes(string text, IReadOnlyList<string> machines) =>
        !string.IsNullOrWhiteSpace(text) && ContributorSentenceCheck.Refusal(text, machines) is null;
}
