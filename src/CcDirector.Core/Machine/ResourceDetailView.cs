using System.Globalization;

namespace CcDirector.Core.Machine;

/// <summary>
/// One of the detail screen's tiles: CPU, Memory, and one per fixed drive (free space, brought back by the
/// owner on 2026-11-07). Disk ACTIVITY has no tile; it reaches the owner only as words in the Diagnosis tab.
/// </summary>
/// <param name="Kind">Which resource.</param>
/// <param name="Title">"CPU" or "Memory".</param>
/// <param name="Headline">The big figure, "28%" or "8.86 / 16.0 GB"; the dash when unreadable.</param>
/// <param name="Detail">The line under it, for example "8 cores - busiest core 71%", or why it cannot be read.</param>
/// <param name="Level">The tile's colour, from the verdict. Unknown when the reading failed.</param>
/// <param name="History">The last hour, oldest first, one point per sample. The line the tile draws.</param>
public sealed record ResourceTileView(
    ResourceKind Kind,
    string Title,
    string Headline,
    string Detail,
    ResourceLevel Level,
    IReadOnlyList<ResourceHistoryPoint> History);

/// <summary>
/// One point of a tile's history. <paramref name="Fraction"/> is null for a sample that could not be read,
/// which the chart draws as a GAP - never as a drop to zero.
/// </summary>
/// <param name="AtUtc">When the sample was taken.</param>
/// <param name="Fraction">0.0 to 1.0, or null when that sample could not be read.</param>
public readonly record struct ResourceHistoryPoint(DateTime AtUtc, double? Fraction);

/// <summary>
/// One process in an expanded session row, with its depth in the tree so the view only indents.
/// </summary>
/// <param name="Pid">The process id.</param>
/// <param name="Name">The process name. Always present: a process the platform refuses to measure is still named.</param>
/// <param name="Depth">0 for the session's root process, 1 for its children, and so on.</param>
/// <param name="CpuText">"62%", or the dash when the platform refused the figure.</param>
/// <param name="MemoryText">"1.21 GB", or the dash when refused.</param>
public sealed record ResourceProcessRow(int Pid, string Name, int Depth, string CpuText, string MemoryText);

/// <summary>
/// One row of the Sessions tab: a session, "The Director itself", or "Everything else on the machine".
/// </summary>
/// <param name="Label">What the row is called, for example "140 - Resource Monitor - Dev - Linux probe".</param>
/// <param name="SessionId">The session's id, or null for the two rows that are not sessions.</param>
/// <param name="Factory">The session's Factory value, or the dash when it has none.</param>
/// <param name="CpuText">"62%", "at least 62%" when some processes' processor could not be measured, or the dash.</param>
/// <param name="MemoryText">"1.21 GB", "at least 1.21 GB", or the dash.</param>
/// <param name="ProcessCountText">How many processes, or the dash for "Everything else", which is not counted.</param>
/// <param name="Processes">The process tree, root first, in tree order. Empty for the rows that do not expand.</param>
public sealed record ResourceUsageRow(
    string Label,
    string? SessionId,
    string Factory,
    string CpuText,
    string MemoryText,
    string ProcessCountText,
    IReadOnlyList<ResourceProcessRow> Processes);

/// <summary>
/// A session as the detail screen needs it: its measured usage and the words to show for it. The Director
/// supplies these; the per-tree sums come from Core's attribution (phase 1).
/// </summary>
/// <param name="Usage">The session's process-tree usage.</param>
/// <param name="Label">"140 - Resource Monitor - Dev - Linux probe".</param>
/// <param name="Factory">The session's Factory value, or null when it has none. A factory is not a Mission.</param>
public sealed record ResourceSessionInput(SessionUsage Usage, string Label, string? Factory);

/// <summary>What the detail screen binds to (mission section 5h). The Diagnosis tab binds to <see cref="SlownessDiagnosis"/> directly.</summary>
/// <param name="Title">"Resources - devthrottle-mac-mini".</param>
/// <param name="Cpu">The CPU tile.</param>
/// <param name="Memory">The Memory tile.</param>
/// <param name="Sessions">One row per session, most expensive in processor first.</param>
/// <param name="Director">"The Director itself".</param>
/// <param name="EverythingElse">"Everything else on the machine" - how the owner sees that other applications hold the memory, not us.</param>
public sealed record ResourceDetailView(
    string Title,
    ResourceTileView Cpu,
    ResourceTileView Memory,
    IReadOnlyList<ResourceUsageRow> Sessions,
    ResourceUsageRow Director,
    ResourceUsageRow EverythingElse,
    IReadOnlyList<ResourceTileView> Disks);

/// <summary>
/// Folds the sampler's state, its history and this Director's sessions into the detail screen. A pure
/// function, like <see cref="ResourceMeterFold"/>, so every row the owner can see is pinned by a unit test.
/// It reads no disk evidence: nothing about a disk is ever a tile or a row.
/// </summary>
public static class ResourceDetailFold
{
    /// <summary>The label of the row for the Director's own process.</summary>
    public const string DirectorLabel = "The Director itself";

    /// <summary>The label of the row for everything this Director did not start.</summary>
    public const string EverythingElseLabel = "Everything else on the machine";

    /// <summary>Builds the detail screen.</summary>
    /// <param name="state">The sampler's current state.</param>
    /// <param name="history">The sampler's history, oldest first.</param>
    /// <param name="machineName">The machine's name.</param>
    /// <param name="sessions">This Director's sessions with their usage.</param>
    /// <param name="directorPid">The Director's own process id.</param>
    public static ResourceDetailView Build(
        ResourceSamplerState state,
        IReadOnlyList<ResourceSample> history,
        string machineName,
        IReadOnlyList<ResourceSessionInput> sessions,
        int directorPid)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);

        var sample = state.Latest;
        var processes = sample is { Processes.CouldRead: true } ? sample.Processes.Processes : null;
        var byPid = processes?.ToDictionary(process => process.Pid);

        var sessionRows = sessions
            .OrderByDescending(session => session.Usage.CpuPercent)
            .ThenBy(session => session.Label, StringComparer.Ordinal)
            .Select(session => SessionRow(session, byPid))
            .ToList();

        return new ResourceDetailView(
            $"Resources - {machineName}",
            CpuTile(state, history),
            MemoryTile(state, history),
            sessionRows,
            DirectorRow(directorPid, byPid),
            EverythingElseRow(sample, sessions, directorPid, byPid),
            DiskTiles(state, history));
    }

    /// <summary>
    /// A tile per fixed drive (the approved mock-up): "Disk C:", "6.40 GB free", "of 931 GB - lost 8 GB in the last
    /// 60 minutes", coloured by <see cref="DiskSpacePressure"/>. Its history is how FULL the drive was at each sample,
    /// the same direction as the bar. A drive that cannot be read is a tile that says so - never "0 GB free". When the
    /// drives cannot be listed at all, one tile carries why, so the screen never simply has no disk on it.
    /// </summary>
    private static IReadOnlyList<ResourceTileView> DiskTiles(ResourceSamplerState state, IReadOnlyList<ResourceSample> history)
    {
        if (state.Latest is not { } sample)
            return [new ResourceTileView(ResourceKind.Disk, "Disk", ResourceViewText.CannotReadText,
                ResourceViewText.CannotRead(ResourceSamplerState.NotYetSampledReason), ResourceLevel.Unknown, [])];

        var disks = sample.DiskSpace;
        if (!disks.CouldRead || disks.Drives.Count == 0)
            return [new ResourceTileView(ResourceKind.Disk, "Disk", ResourceViewText.CannotReadText,
                ResourceViewText.CannotRead(disks.CouldRead ? "no fixed drive was found on this machine" : disks.UnreadableReason),
                ResourceLevel.Unknown, [])];

        // The trend's span is NOT worked out from the samples' wall-clock stamps, which a stepped clock would bend: the
        // history is in sampling order and its span is the count of samples times the five-second interval. A read the
        // sampler skipped makes that span a little SHORT, so the loss rate can only be overstated slightly, never hidden.
        var timeline = history
            .Select((s, index) => (At: TimeSpan.FromTicks(ResourceSampling.Interval.Ticks * index), s.DiskSpace))
            .ToList();

        var tiles = new List<ResourceTileView>(disks.Drives.Count);
        foreach (var drive in disks.Drives)
        {
            var points = history
                .Select(s => new ResourceHistoryPoint(s.TakenAtUtc, FractionOf(s.DiskSpace, drive.Name)))
                .ToList();
            if (!drive.CouldRead)
            {
                tiles.Add(new ResourceTileView(ResourceKind.Disk, $"Disk {drive.Name}", ResourceViewText.CannotReadText,
                    ResourceViewText.CannotRead(drive.UnreadableReason), ResourceLevel.Unknown, points));
                continue;
            }

            var trend = DiskSpaceText.Trend(drive.Name, timeline);
            var detail = trend is null
                ? $"of {ResourceViewText.Bytes(drive.TotalBytes)}"
                : $"of {ResourceViewText.Bytes(drive.TotalBytes)} - {trend}";
            tiles.Add(new ResourceTileView(ResourceKind.Disk, $"Disk {drive.Name}", $"{ResourceViewText.Bytes(drive.FreeBytes)} free",
                detail, DiskSpacePressure.Level(drive), points));
        }
        return tiles;
    }

    private static double? FractionOf(DiskSpaceSet disks, string driveName)
    {
        foreach (var drive in disks.Drives)
        {
            if (string.Equals(drive.Name, driveName, StringComparison.Ordinal)) return DiskSpaceText.UsedFraction(drive);
        }
        return null;
    }

    private static ResourceTileView CpuTile(ResourceSamplerState state, IReadOnlyList<ResourceSample> history)
    {
        var points = history.Select(s => new ResourceHistoryPoint(s.TakenAtUtc, ResourceViewText.CpuFraction(s.Cpu))).ToList();
        if (state.Latest is not { } sample)
            return new(ResourceKind.Cpu, "CPU", ResourceViewText.CannotReadText,
                ResourceViewText.CannotRead(ResourceSamplerState.NotYetSampledReason), ResourceLevel.Unknown, points);

        var cpu = sample.Cpu;
        if (!cpu.CouldRead)
            return new(ResourceKind.Cpu, "CPU", ResourceViewText.CannotReadText,
                ResourceViewText.CannotRead(cpu.UnreadableReason), ResourceLevel.Unknown, points);

        var detail = string.Create(CultureInfo.InvariantCulture,
            $"{cpu.LogicalCoreCount} cores - busiest core {ResourceViewText.PercentOf100(cpu.BusiestCorePercent)}");
        return new(ResourceKind.Cpu, "CPU", ResourceViewText.Percent(ResourceViewText.CpuFraction(cpu)), detail,
            state.VerdictFor(ResourceKind.Cpu).Level, points);
    }

    private static ResourceTileView MemoryTile(ResourceSamplerState state, IReadOnlyList<ResourceSample> history)
    {
        var points = history.Select(s => new ResourceHistoryPoint(s.TakenAtUtc, ResourceViewText.MemoryUsedFraction(s.Memory))).ToList();
        if (state.Latest is not { } sample)
            return new(ResourceKind.Memory, "Memory", ResourceViewText.CannotReadText,
                ResourceViewText.CannotRead(ResourceSamplerState.NotYetSampledReason), ResourceLevel.Unknown, points);

        var memory = sample.Memory;
        if (ResourceViewText.MemoryUsedFraction(memory) is null)
            return new(ResourceKind.Memory, "Memory", ResourceViewText.CannotReadText,
                ResourceViewText.CannotRead(memory.CouldRead ? "the memory reading did not add up" : memory.UnreadableReason),
                ResourceLevel.Unknown, points);

        var used = memory.TotalBytes - memory.AvailableBytes;
        var swap = sample.Swap.CouldRead
            ? $"swap {ResourceViewText.Bytes(sample.Swap.UsedBytes)}"
            : $"swap {ResourceViewText.CannotRead(sample.Swap.UnreadableReason)}";
        var headline = $"{ResourceViewText.Bytes(used).Replace(" GB", string.Empty, StringComparison.Ordinal)} / {ResourceViewText.Bytes(memory.TotalBytes)}";
        return new(ResourceKind.Memory, "Memory", headline,
            $"{ResourceViewText.Bytes(memory.AvailableBytes)} available - {swap}",
            state.VerdictFor(ResourceKind.Memory).Level, points);
    }

    private static ResourceUsageRow SessionRow(ResourceSessionInput session, Dictionary<int, ProcessReading>? byPid)
    {
        var usage = session.Usage;
        return new ResourceUsageRow(
            session.Label,
            usage.SessionId,
            session.Factory ?? ResourceViewText.CannotReadText,
            ResourceViewText.LowerBoundPrefix(usage.ProcessesWithUnknownProcessor) + ResourceViewText.PercentOf100(usage.CpuPercent),
            ResourceViewText.LowerBoundPrefix(usage.ProcessesWithUnknownMemory) + ResourceViewText.Bytes(usage.MemoryBytes),
            usage.Pids.Count.ToString(CultureInfo.InvariantCulture),
            byPid is null ? Array.Empty<ResourceProcessRow>() : Tree(usage.RootPid, usage.Pids, byPid));
    }

    private static ResourceUsageRow DirectorRow(int directorPid, Dictionary<int, ProcessReading>? byPid)
    {
        if (byPid is null || !byPid.TryGetValue(directorPid, out var director))
            return new ResourceUsageRow(DirectorLabel, null, ResourceViewText.CannotReadText,
                ResourceViewText.CannotReadText, ResourceViewText.CannotReadText, ResourceViewText.CannotReadText,
                Array.Empty<ResourceProcessRow>());

        return new ResourceUsageRow(DirectorLabel, null, ResourceViewText.CannotReadText,
            ResourceViewText.PercentOf100(director.CpuPercent), ResourceViewText.Bytes(director.MemoryBytes), "1",
            Array.Empty<ResourceProcessRow>());
    }

    // The machine's own totals minus what this Director accounts for. Each part is a dash unless both sides
    // are known completely: a session total that is only a floor, or a refused Director figure, would make
    // "everything else" a guess, and an unreadable machine figure leaves nothing to subtract from.
    private static ResourceUsageRow EverythingElseRow(
        ResourceSample? sample,
        IReadOnlyList<ResourceSessionInput> sessions,
        int directorPid,
        Dictionary<int, ProcessReading>? byPid)
    {
        var dash = ResourceViewText.CannotReadText;
        if (sample is null || byPid is null || !byPid.TryGetValue(directorPid, out var director))
            return new ResourceUsageRow(EverythingElseLabel, null, dash, dash, dash, dash, Array.Empty<ResourceProcessRow>());

        var cpuText = dash;
        if (sample.Cpu.CouldRead && director.CpuPercent is { } directorCpu
            && sessions.All(session => session.Usage.ProcessesWithUnknownProcessor == 0))
        {
            var other = sample.Cpu.TotalBusyPercent - directorCpu - sessions.Sum(session => session.Usage.CpuPercent);
            // Below zero means our processes were measured as using more than the whole machine - they are not
            // all read in the same instant as the machine total. That does not add up, so it is the dash, never
            // a made-up zero.
            if (other >= 0) cpuText = ResourceViewText.PercentOf100(other);
        }

        var memoryText = dash;
        var memory = sample.Memory;
        if (ResourceViewText.MemoryUsedFraction(memory) is not null && director.MemoryBytes is { } directorMemory
            && sessions.All(session => session.Usage.ProcessesWithUnknownMemory == 0))
        {
            var used = memory.TotalBytes - memory.AvailableBytes;
            var ours = directorMemory + sessions.Aggregate(0UL, (sum, session) => sum + session.Usage.MemoryBytes);
            // Our processes can sum past the machine's own used memory - shared pages are counted in every
            // process that maps them, routinely on Windows. That does not add up, so it is the dash, never zero.
            if (ours <= used) memoryText = ResourceViewText.Bytes(used - ours);
        }

        return new ResourceUsageRow(EverythingElseLabel, null, dash, cpuText, memoryText, dash, Array.Empty<ResourceProcessRow>());
    }

    private static IReadOnlyList<ResourceProcessRow> Tree(int rootPid, IReadOnlyList<int> pids, Dictionary<int, ProcessReading> byPid)
    {
        var members = new HashSet<int>(pids);
        var children = new Dictionary<int, List<ProcessReading>>();
        foreach (var pid in pids)
        {
            if (!byPid.TryGetValue(pid, out var process) || process.Pid == rootPid || process.ParentPid is not { } parent) continue;
            if (!members.Contains(parent)) continue;
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = new List<ProcessReading>();
            list.Add(process);
        }

        var rows = new List<ResourceProcessRow>();
        var visited = new HashSet<int>();
        var stack = new Stack<(ProcessReading Process, int Depth)>();
        if (byPid.TryGetValue(rootPid, out var root)) stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (process, depth) = stack.Pop();
            if (!visited.Add(process.Pid)) continue;
            rows.Add(new ResourceProcessRow(process.Pid, process.Name, depth,
                ResourceViewText.PercentOf100(process.CpuPercent), ResourceViewText.Bytes(process.MemoryBytes)));
            if (!children.TryGetValue(process.Pid, out var kids)) continue;
            foreach (var kid in kids.OrderByDescending(kid => kid.Pid))
                stack.Push((kid, depth + 1));
        }

        // A member whose parent is not visible in this sample - the parent has exited, or the platform did not
        // say - is still part of the session and still counted in its row, so it is listed under the root
        // rather than silently dropped.
        var orphanDepth = visited.Contains(rootPid) ? 1 : 0;
        foreach (var pid in pids.Order())
        {
            if (visited.Contains(pid) || !byPid.TryGetValue(pid, out var orphan)) continue;
            visited.Add(pid);
            rows.Add(new ResourceProcessRow(orphan.Pid, orphan.Name, orphanDepth,
                ResourceViewText.PercentOf100(orphan.CpuPercent), ResourceViewText.Bytes(orphan.MemoryBytes)));
        }
        return rows;
    }
}
