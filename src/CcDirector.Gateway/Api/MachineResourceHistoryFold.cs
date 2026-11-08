using System.Globalization;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data.Entities;
using DiskSpacePressure = CcDirector.Core.Machine.DiskSpacePressure;
using DiskSpaceSet = CcDirector.Core.Machine.DiskSpaceSet;
using DiskSpaceText = CcDirector.Core.Machine.DiskSpaceText;
using DriveSpaceReading = CcDirector.Core.Machine.DriveSpaceReading;
using ResourceLevel = CcDirector.Core.Machine.ResourceLevel;
using ResourceViewText = CcDirector.Core.Machine.ResourceViewText;

namespace CcDirector.Gateway.Api;

/// <summary>
/// One machine expanded (Mission: Resource Monitor, section 5k), folded once on the Gateway: the span it covers, the
/// five-minute rows for the chart with a note wherever a figure is missing, the gaps where no summary arrived, every
/// crossing marked with its words and tone, one list of those events, and the cost per registered factory, grouped by
/// each session's Factory value - never by Mission. Pure. The Cockpit draws every word and tone as sent (CLAUDE.md rule 7).
/// </summary>
internal static class MachineResourceHistoryFold
{
    public const string NoFactory = "No factory";

    public const string NoFactoriesMessage = "No sessions were recorded on this machine in this span.";

    /// <summary>Where the caller reads windows from: one window before the span, so the window straddling its start is in.</summary>
    public static DateTime ReadFromUtc(DateTime fromUtc) => fromUtc - ResourceWindowGaps.Window;

    /// <param name="fromUtc">The start of the span the Gateway read.</param>
    /// <param name="toUtc">The end of the span: the Gateway's now.</param>
    public static MachineResourceHistoryDto Fold(string machine, int hours, DateTime fromUtc, DateTime toUtc,
        IReadOnlyList<ResourceWindowEntity>? windows, IReadOnlyList<ResourceSessionWindowEntity>? sessionWindows)
    {
        var dto = new MachineResourceHistoryDto { Machine = machine, Hours = hours, FromUtc = fromUtc, ToUtc = toUtc };
        if (windows is null || sessionWindows is null)
        {
            dto.EmptyMessage = "The Gateway's statistics store is not open, so this machine's history cannot be shown.";
            return dto;
        }
        // The caller reads from one window before the span (see ReadFromUtc), so the window straddling the span's start
        // can close a gap that would otherwise be reported from the span's start. Only windows inside the span are shown.
        var straddling = windows.Where(w => string.Equals(w.MachineName, machine, StringComparison.OrdinalIgnoreCase)).ToList();
        var mine = straddling.Where(w => w.WindowStartUtc >= fromUtc).ToList();
        if (mine.Count == 0)
        {
            dto.EmptyMessage = $"No resource summaries from {machine} in the last {hours} hours.";
            return dto;
        }

        // Two Directors on one machine both measure the whole machine: one row per window start, the fuller one.
        var rows = mine.GroupBy(w => w.WindowStartUtc).Select(g => g.OrderByDescending(x => x.SampleCount).First())
            .OrderBy(w => w.WindowStartUtc)
            .ToList();
        foreach (var w in rows)
        {
            var total = w.MemoryTotalBytes is > 0 ? w.MemoryTotalBytes : null;
            var window = new MachineResourceWindowDto
            {
                WindowStartUtc = w.WindowStartUtc,
                ProcessorAveragePercent = w.ProcessorAveragePercent,
                ProcessorPeakPercent = w.ProcessorPeakPercent,
                MemoryAveragePercent = total is { } t1 && w.MemoryAverageUsedBytes is { } a ? a * 100.0 / t1 : null,
                MemoryPeakPercent = total is { } t2 && w.MemoryPeakUsedBytes is { } p ? p * 100.0 / t2 : null,
                Crossings = ResourceSummaryStore.CrossingsOf(w).Select(Mark).ToList(),
                RefusedReason = w.RefusedReason,
            };
            window.ProcessorNote = window.ProcessorAveragePercent is null || window.ProcessorPeakPercent is null
                ? NoteFor(w, "Processor", w.ProcessorUnreadableReason)
                : null;
            window.MemoryNote = window.MemoryAveragePercent is null || window.MemoryPeakPercent is null
                ? NoteFor(w, "Memory", w.MemoryUnreadableReason
                    ?? (total is null ? "the machine's total memory was not read, so no share can be given" : null))
                : null;
            dto.Windows.Add(window);
        }

        foreach (var (start, end, isTail) in ResourceWindowGaps.Of(straddling, fromUtc, toUtc))
            dto.Gaps.Add(new ResourceGapDto
            {
                StartUtc = start,
                EndUtc = end,
                Label = isTail
                    ? $"No resource summary has arrived for {Duration(end - start)}"
                    : $"No resource summary arrived for {Duration(end - start)}",
                Tone = FleetTone.Idle,
            });

        // The event list, oldest first: crossings, refused windows and gaps. An unread figure is drawn on the chart where
        // it happened and is not listed - a counter that did not answer for twenty minutes would otherwise be eight lines.
        dto.Events = dto.Windows.SelectMany(w => w.Crossings.Select(c => new ResourceEventDto { AtUtc = c.StartedUtc, Label = c.Label, Tone = c.Tone }))
            .Concat(dto.Windows.Where(w => w.RefusedReason is not null)
                .Select(w => new ResourceEventDto { AtUtc = w.WindowStartUtc, Label = $"Figures refused: {w.RefusedReason}", Tone = FleetTone.Bad }))
            .Concat(dto.Gaps.Select(g => new ResourceEventDto { AtUtc = g.StartUtc, Label = g.Label, Tone = g.Tone }))
            .OrderBy(e => e.AtUtc)
            .ToList();

        // A refused window keeps no session rows, so every factory's cost over a span that holds one is a lower bound.
        var anyRefused = mine.Any(w => w.RefusedReason is not null);
        dto.Factories = sessionWindows
            .Where(s => string.Equals(s.MachineName, machine, StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => string.IsNullOrWhiteSpace(s.Factory) ? NoFactory : s.Factory!, StringComparer.OrdinalIgnoreCase)
            .Select(g => FactoryCost(g, anyRefused))
            .OrderBy(f => f.Factory == NoFactory ? 1 : 0)
            .ThenByDescending(f => f.ProcessorPeakPercent ?? -1)
            .ThenBy(f => f.Factory, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (dto.Factories.Count == 0) dto.NoFactoriesMessage = NoFactoriesMessage;

        dto.Drives = DriveHistory(rows);
        if (dto.Drives.Count == 0)
            dto.NoDrivesMessage = $"No resource summary from {machine} in the last {hours} hours reported a drive. "
                                  + "A Director older than 7 November 2026 does not report disk space; updating it adds the drives here.";
        return dto;
    }

    /// <summary>
    /// Each drive's free space over the span, from the windows' uploaded drive figures (Disk Space on the Meter,
    /// 7 November 2026). The words and the tone are decided here, with the same lines and the same wording as the
    /// Director's DISK bar, so the machine page and the toolbar cannot disagree (CLAUDE.md rule 7).
    /// </summary>
    internal static List<MachineDriveHistoryDto> DriveHistory(IReadOnlyList<ResourceWindowEntity> rows)
    {
        var order = new List<string>();
        var byDrive = new Dictionary<string, List<(DateTime Start, ResourceDriveSummary Drive)>>(StringComparer.Ordinal);
        foreach (var row in rows.OrderBy(r => r.WindowStartUtc))
        {
            foreach (var drive in ResourceSummaryStore.DrivesOf(row))
            {
                if (!byDrive.TryGetValue(drive.Name, out var list))
                {
                    list = new List<(DateTime, ResourceDriveSummary)>();
                    byDrive[drive.Name] = list;
                    order.Add(drive.Name);
                }
                list.Add((row.WindowStartUtc, drive));
            }
        }

        var result = new List<MachineDriveHistoryDto>(order.Count);
        foreach (var name in order)
        {
            var seen = byDrive[name];
            var newest = seen[^1].Drive;
            var dto = new MachineDriveHistoryDto { Name = name };
            if (newest is { CouldRead: true, FreeBytesAtEnd: { } free, TotalBytes: { } total })
            {
                var level = DiskSpacePressure.LevelOfFree((ulong)free);
                dto.Headline = $"{ResourceViewText.Bytes((ulong)free)} free of {ResourceViewText.Bytes((ulong)total)}";
                dto.LevelLabel = DiskSpacePressure.LevelWords(level);
                dto.Tone = level switch
                {
                    ResourceLevel.Critical => FleetTone.Bad,
                    ResourceLevel.Tight => FleetTone.Warn,
                    _ => FleetTone.Ok,
                };
            }
            else
            {
                dto.Headline = ResourceViewText.CannotRead(newest.UnreadableReason);
                dto.LevelLabel = DiskSpacePressure.LevelWords(ResourceLevel.Unknown);
                dto.Tone = FleetTone.Idle;
            }

            var read = seen.Where(x => x.Drive is { CouldRead: true, FreeBytesAtEnd: not null, LowestFreeBytes: not null, TotalBytes: > 0 }).ToList();
            dto.Points = read.Select(x => new MachineDrivePointDto
            {
                WindowStartUtc = x.Start,
                LowestFreeBytes = x.Drive.LowestFreeBytes!.Value,
                UsedPercent = 100.0 - x.Drive.LowestFreeBytes!.Value * 100.0 / x.Drive.TotalBytes!.Value,
            }).ToList();

            // The trend reads free space at each window's END, oldest against newest, through the toolbar's own wording.
            if (read.Count > 0)
            {
                var origin = read[0].Start;
                var timeline = read
                    .Select(x => (x.Start - origin, DiskSpaceSet.Read([DriveSpaceReading.Read(name, (ulong)x.Drive.TotalBytes!.Value, (ulong)x.Drive.FreeBytesAtEnd!.Value)])))
                    .ToList();
                dto.TrendLabel = DiskSpaceText.Trend(name, timeline);
            }
            result.Add(dto);
        }
        return result;
    }

    /// <summary>Why one window has no figure for one resource. A refused window says so, in the fault tone; an unread one
    /// carries the reason the store kept.</summary>
    private static ResourceWindowNoteDto NoteFor(ResourceWindowEntity w, string resource, string? unreadReason) =>
        w.RefusedReason is { } refused
            ? new ResourceWindowNoteDto { Label = $"Figures refused: {refused}", Tone = FleetTone.Bad }
            : new ResourceWindowNoteDto
            {
                Label = $"{resource} not read: {unreadReason ?? "the Director gave no reason"}",
                Tone = FleetTone.Idle,
            };

    /// <summary>"2 hours 10 minutes", "45 minutes", "1 day 3 hours" - whole minutes, never rounded up.</summary>
    internal static string Duration(TimeSpan span)
    {
        var minutes = (long)Math.Floor(span.TotalMinutes);
        var days = minutes / 1440;
        var hours = minutes % 1440 / 60;
        var mins = minutes % 60;
        var parts = new List<string>();
        if (days > 0) parts.Add(Unit(days, "day"));
        if (hours > 0) parts.Add(Unit(hours, "hour"));
        if (mins > 0 && days == 0) parts.Add(Unit(mins, "minute"));
        return parts.Count == 0 ? "under a minute" : string.Join(" ", parts);

        static string Unit(long n, string word) => n.ToString(CultureInfo.InvariantCulture) + " " + word + (n == 1 ? "" : "s");
    }

    private static ResourceCrossingMarkDto Mark(ResourceCrossing c)
    {
        var resource = c.Resource == ResourceSummaryContract.Processor ? "CPU" : "Memory";
        var peak = c.PeakPercent is { } p ? $", peak {ResourceCapacityFold.Percent(p)}" : "";
        return new ResourceCrossingMarkDto
        {
            StartedUtc = c.StartedUtc,
            Resource = resource,
            Label = $"{resource} {c.Level} for {c.Seconds} seconds{peak}",
            Tone = c.Level == ResourceSummaryContract.Red ? FleetTone.Bad : FleetTone.Warn,
        };
    }

    /// <summary>
    /// A factory's sessions run side by side, so within one window their figures ADD: the factory's average is the mean,
    /// over the windows it ran in, of its sessions' summed averages, and its peak is the highest summed peak.
    ///
    /// AN UNKNOWN IS NEVER ADDED AS ZERO. When a session's figure could not be read, or a session had processes whose
    /// figures could not be read, the sum leaves them out and is a LOWER BOUND, and the label says "at least" - a cost
    /// reported without that would be too cheap and still look plausible, which is the worst kind of wrong.
    /// </summary>
    private static FactoryResourceCostDto FactoryCost(IGrouping<string, ResourceSessionWindowEntity> factory, bool spanHasRefusedWindow)
    {
        var byWindow = factory.GroupBy(s => s.WindowStartUtc).ToList();
        var partialProcesses = spanHasRefusedWindow || factory.Any(s => s.ProcessesWithUnknownFigures > 0);

        var cpuAverages = Sums(byWindow, s => s.ProcessorAveragePercent);
        var cpuPeaks = Sums(byWindow, s => s.ProcessorPeakPercent);
        var memAverages = Sums(byWindow, s => s.MemoryAverageUsedBytes);
        var memPeaks = Sums(byWindow, s => s.MemoryPeakUsedBytes);

        double? cpuPeak = cpuPeaks.Values.Count == 0 ? null : cpuPeaks.Values.Max();
        var cpuAtLeast = partialProcesses || cpuAverages.LeftOut || cpuPeaks.LeftOut ? "at least " : "";
        var memAtLeast = partialProcesses || memAverages.LeftOut || memPeaks.LeftOut ? "at least " : "";
        return new FactoryResourceCostDto
        {
            Factory = factory.Key,
            Sessions = factory.Select(s => s.SessionId).Distinct(StringComparer.Ordinal).Count(),
            ProcessorPeakPercent = cpuPeak,
            ProcessorLabel = cpuAverages.Values.Count == 0 || cpuPeak is null
                ? "cannot read"
                : $"{cpuAtLeast}{ResourceCapacityFold.Percent(cpuAverages.Values.Average())} / {ResourceCapacityFold.Percent(cpuPeak.Value)}",
            MemoryLabel = memAverages.Values.Count == 0 || memPeaks.Values.Count == 0
                ? "cannot read"
                : $"{memAtLeast}{ResourceCapacityFold.Gigabytes((long)memAverages.Values.Average())} / {ResourceCapacityFold.Gigabytes((long)memPeaks.Values.Max())} GB",
        };
    }

    /// <summary>Per window, the sum of the figures that were read - and whether any session's figure had to be left out.</summary>
    private static (List<double> Values, bool LeftOut) Sums<T>(List<IGrouping<DateTime, ResourceSessionWindowEntity>> byWindow,
        Func<ResourceSessionWindowEntity, T?> figure) where T : struct, IConvertible
    {
        var values = new List<double>();
        var leftOut = false;
        foreach (var window in byWindow)
        {
            var read = window.Select(figure).Where(v => v.HasValue).Select(v => Convert.ToDouble(v!.Value)).ToList();
            if (read.Count < window.Count()) leftOut = true;
            if (read.Count > 0) values.Add(read.Sum());
        }
        return (values, leftOut);
    }
}

/// <summary>
/// THE ONE DEFINITION of "a stretch we did not measure" in the Gateway (Mission: Resource Monitor). The expanded machine's
/// chart draws these, and the capacity fold's hour (issue #4679) is meant to read the same function rather than invent a
/// second one, so the columns and the chart under them cannot contradict each other on one screen.
///
/// A gap is a stretch of the span covered by no window that is at least one window long - long enough to have held a
/// summary. Before the newest window the rule is exact, because windows are five-minute slots and abut. After it, a
/// window can only arrive once it has ENDED, so at any instant the newest complete window may legitimately be up to one
/// window old, plus <see cref="DeliveryMargin"/> to build and deliver it: the tail is a gap past
/// <see cref="TailAllowance"/>. This is a question about MEASUREMENT, and deliberately not
/// <see cref="ResourceCapacityFold.SilentAfter"/>, which answers REACHABILITY (when the row shows dashes); borrowing it let
/// a Director that stopped twelve minutes ago claim a whole hour (lead's ruling on #4687). The capacity row's hour reads
/// this same function, so the row and the chart under it still cannot disagree. A window straddling the span's start (read by the caller,
/// see <see cref="MachineResourceHistoryFold.ReadFromUtc"/>) closes the stretch it covers; a gap never starts before the span.
/// </summary>
internal static class ResourceWindowGaps
{
    /// <summary>One window, the five-minute logging contract.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(ResourceSummaryContract.WindowSeconds);

    /// <summary>
    /// How long a finished window may take to be built and to reach the Gateway: the Director summarises the window when
    /// it ends and sends it over the stream it already holds open, which takes seconds. One minute is that with room to
    /// spare. It is a GUESS made before any Director reported; once real fleets do, it moves on what their delivery times
    /// show - never bigger "to be safe", because every minute here is a minute an hour can claim it measured and did not.
    /// </summary>
    public static readonly TimeSpan DeliveryMargin = TimeSpan.FromMinutes(1);

    /// <summary>How long after the newest window ends before the stretch after it is a gap: one window, plus delivery.</summary>
    public static readonly TimeSpan TailAllowance = Window + DeliveryMargin;

    /// <summary>The gaps in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>], oldest first. IsTail marks the stretch
    /// running up to <paramref name="toUtc"/> - "has not arrived since", rather than "did not arrive".</summary>
    public static IReadOnlyList<(DateTime StartUtc, DateTime EndUtc, bool IsTail)> Of(
        IEnumerable<ResourceWindowEntity> windows, DateTime fromUtc, DateTime toUtc)
    {
        var gaps = new List<(DateTime, DateTime, bool)>();
        var cursor = fromUtc;
        foreach (var w in windows.OrderBy(w => w.WindowStartUtc))
        {
            var start = w.WindowStartUtc;
            var end = start.AddSeconds(w.WindowSeconds > 0 ? w.WindowSeconds : ResourceSummaryContract.WindowSeconds);
            if (start - cursor >= Window) gaps.Add((cursor, start, false));
            if (end > cursor) cursor = end;
        }
        // The window after the newest is due one window after the newest ended, plus delivery; past that it is missing.
        if (toUtc - cursor > TailAllowance) gaps.Add((cursor, toUtc, true));
        return gaps;
    }
}
