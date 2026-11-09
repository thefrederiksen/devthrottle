using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// "Watch your factory", decided once (version one, piece 2; critical rule 7). Pure: it reads the registered factory
/// and the activity rows of one window and returns the finished watch view - the floor laid out from the factory's
/// shape, one character per seat in a colour that is the seat's own, and the rows as an ordered timeline of steps.
/// The player draws it and decides nothing.
///
/// The rules it owns, each tested:
///  - The layout comes from the factory's shape: any named line gives line halls; otherwise up to 3 seats is a
///    workshop and 4 or more is the Boss in the middle with the others on a ring (a second ring beyond 12).
///  - A seat's colour is picked from its own id, never from its place in the list or from the activity, so it does
///    not change between windows or when the seats are reordered.
///  - Every activity outcome maps to exactly one step kind (<see cref="StepKind"/>), and a new outcome fails a test.
///  - Runs of empty checks are grouped by the Activity tab's own grouping (<see cref="FactoryAgentsFold.EmptyCheckRuns"/>),
///    so the two views can never disagree on what a quiet stretch is.
///  - The words of a step come from ONE place (<see cref="FactoryWatchWords"/>), which the contributor audience
///    replaces with an allow-list. No field this fold builds reads a computer, a folder, a session, an actor or a link.
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
    internal const double OuterRadius = 8;
    internal const double HallPad = 0.6;
    internal const double HallGap = 0.8;
    internal const double RowGap = 1.2;

    /// <summary>The characters' colours. Twelve, so every seat of a factory up to 12 seats has its own.</summary>
    internal static readonly string[] Palette =
    {
        "#ffb020", "#5aa0ff", "#4cc38a", "#ff7a59", "#a57bf0", "#2ec4c4",
        "#f06fa8", "#c7d23a", "#8d9cff", "#e0904a", "#6fd1ff", "#b58b6a",
    };

    public static FactoryWatchViewDto View(RegisteredFactoryDto factory, IReadOnlyList<FactoryActivityDto> windowRows,
        bool windowTruncated, FactoryWindow window, TimeZoneInfo zone, FactoryWatchAudience audience)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(windowRows);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(zone);
        FileLog.Write($"[FactoryWatchFold] View: factory={factory.Factory}, audience={audience}, seats={factory.Seats.Count}, rows={windowRows.Count}");
        if (audience != FactoryWatchAudience.Owner)
            throw new NotSupportedException($"The watch view for the {audience} audience is not built yet: it comes with the contributor filter (Watch your factory, piece 3).");
        var stranger = windowRows.FirstOrDefault(r => !string.Equals(r.Factory, factory.Factory, StringComparison.OrdinalIgnoreCase));
        if (stranger is not null)
            throw new ArgumentException($"The watch view of '{factory.Factory}' was handed a row of '{stranger.Factory}' ({stranger.Id}); only the factory's own rows may be folded.", nameof(windowRows));

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
        var ordered = windowRows.OrderBy(r => r.OccurredUtc).ThenBy(r => r.RecordedUtc).ThenBy(r => r.Id).ToList();
        var ownerActs = ordered.Count(IsOwnerAct);
        var seatRows = ordered.Where(r => !IsOwnerAct(r)).ToList();
        var unregistered = seatRows.Count(r => !seatIds.ContainsKey(r.FactoryAgent));
        seatRows = seatRows.Where(r => seatIds.ContainsKey(r.FactoryAgent)).ToList();

        foreach (var run in FactoryAgentsFold.EmptyCheckRuns(seatRows))
            view.Steps.Add(Step(run, seatIds[run[0].FactoryAgent], window, zone, audience));

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

    private static FactoryWatchStepDto Step(IReadOnlyList<FactoryActivityDto> run, string seat, FactoryWindow window,
        TimeZoneInfo zone, FactoryWatchAudience audience)
    {
        var first = run[0];
        var last = run[^1];
        var kind = run.Count > 1 ? FactoryWatchStepKind.CheckedNothing : StepKind(first.Outcome);
        return new FactoryWatchStepDto
        {
            Key = first.Id.ToString(),
            Kind = kind,
            Seat = seat,
            Utc = first.OccurredUtc,
            ToUtc = run.Count > 1 ? last.OccurredUtc : null,
            Time = run.Count > 1
                ? $"{FactoryAgentsFold.ClockText(first.OccurredUtc, window, zone)}-{FactoryAgentsFold.ClockText(last.OccurredUtc, window, zone)}"
                : FactoryAgentsFold.ClockText(first.OccurredUtc, window, zone),
            Words = FactoryWatchWords.For(run, audience),
            EmptyChecks = kind == FactoryWatchStepKind.CheckedNothing ? run.Count : 0,
        };
    }

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
        var innerCapacity = RingMaxSeats - (boss is null ? 0 : 1);
        var inner = workers.Take(innerCapacity).ToList();
        var outer = workers.Skip(innerCapacity).ToList();
        var radius = outer.Count == 0 ? InnerRadius : OuterRadius;
        var centre = radius + RingStationWidth / 2 + Margin + HomeOffset;
        view.FloorWidth = Round(centre * 2);
        view.FloorHeight = Round(centre * 2);

        if (boss is not null)
            view.Stations.Add(Station(boss, true, centre - BossSize / 2, centre - BossSize / 2, BossSize, BossSize, null));
        Ring(inner, InnerRadius, centre, view);
        Ring(outer, OuterRadius, centre, view);
    }

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
/// THE ONE PLACE A WATCH STEP'S WORDS COME FROM. The owner sees the factory's own sentence, exactly as the Activity
/// tab shows it. The contributor audience (piece 3) replaces this method with an allow-list: a step it does not allow
/// carries no words at all. Never reads a computer, a folder, a session, an actor or a link.
/// </summary>
internal static class FactoryWatchWords
{
    internal static string For(IReadOnlyList<FactoryActivityDto> run, FactoryWatchAudience audience) => audience switch
    {
        FactoryWatchAudience.Owner => run.Count > 1 ? FactoryAgentsFold.CollapsedWhat(run) : run[0].What,
        _ => throw new NotSupportedException($"No words are decided for the {audience} audience yet (Watch your factory, piece 3)."),
    };
}
