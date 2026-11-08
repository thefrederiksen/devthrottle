using System.Globalization;

namespace CcDirector.Core.Machine;

/// <summary>
/// One bar of the toolbar meter, finished: the view binds to it and decides nothing (CLAUDE.md rule 7).
/// There are exactly three bars, CPU, MEM and DISK (free space, brought back by the owner on 2026-11-07, always
/// shown). Disk activity and the network are never a bar.
/// </summary>
/// <param name="Kind">Which resource: <see cref="ResourceKind.Cpu"/>, <see cref="ResourceKind.Memory"/> or <see cref="ResourceKind.Disk"/>.</param>
/// <param name="Label">The on-screen label, "CPU", "MEM" or "DISK".</param>
/// <param name="CouldRead">False when the reading could not be taken. The bar is then empty and the text a dash.</param>
/// <param name="Fraction">How full the bar is, 0.0 to 1.0. Null when unreadable - never zero.</param>
/// <param name="PercentText">
/// The figure beside the bar: "28%" for CPU and MEM; for DISK the free space LEFT on the tightest drive, "C: 6 GB",
/// because that is the figure the owner acts on while the bar fills with how FULL it is.
/// <see cref="ResourceViewText.CannotReadText"/> when unreadable.
/// </param>
/// <param name="Level">The colour the bar takes, from the verdict. Unknown is its own look, never Normal's.</param>
/// <param name="Tooltip">One line saying the figure, or that it cannot be read and why.</param>
public sealed record ResourceBarView(
    ResourceKind Kind,
    string Label,
    bool CouldRead,
    double? Fraction,
    string PercentText,
    ResourceLevel Level,
    string Tooltip);

/// <summary>
/// What the toolbar meter binds to (mission sections 5d to 5g).
/// </summary>
/// <param name="Cpu">The CPU bar.</param>
/// <param name="Memory">The MEM bar.</param>
/// <param name="Disk">The DISK bar: the fixed drive with the least room left.</param>
/// <param name="NarrowText">What a narrow window shows instead of the bars, for example "CPU 23% MEM 61% C: 6 GB".</param>
/// <param name="TooltipLines">The meter's tooltip, one line each, machine figures first and then this Director's own sessions.</param>
/// <param name="SwapLevel">
/// Swap's verdict level. SWAP HAS NO BAR - the mission allows exactly two, the processor and memory - but it
/// does have its own warning limits, and until the warning banner was deleted the banner was the ONLY thing
/// that read this level. Without it here, a machine paging hard would show two green bars and say nothing
/// (found in review of pull request 4732). So it colours the frame and speaks in the tooltip instead.
/// </param>
public sealed record ResourceMeterView(
    ResourceBarView Cpu,
    ResourceBarView Memory,
    ResourceBarView Disk,
    string NarrowText,
    IReadOnlyList<string> TooltipLines,
    ResourceLevel SwapLevel = ResourceLevel.Unknown)
{
    /// <summary>
    /// The level the meter's frame takes: the worst of the three bars AND SWAP. Unknown only when all three are
    /// unknown, so one reading that cannot be taken never hides another's warning (the same rule as
    /// <see cref="ResourcePressureVerdict.Worst"/>, and it falls out of Unknown being zero in the enum).
    ///
    /// Swap is here and not in a bar because it has no bar to be in. The frame is the only place a resource
    /// without a bar can still be seen without hovering.
    /// </summary>
    public ResourceLevel FrameLevel
    {
        get
        {
            var worst = Cpu.Level > Memory.Level ? Cpu.Level : Memory.Level;
            if (Disk.Level > worst) worst = Disk.Level;

            // SWAP MAY RAISE THIS FRAME; IT MAY NEVER ASSERT HEALTH. Only a warning level counts. Taking the
            // plain maximum would have let a readable, Normal swap turn a frame whose BOTH BARS CANNOT BE READ
            // from Unknown into Normal - the meter claiming the machine is fine on the strength of the one
            // reading nobody can see (found in the second review of pull request 4732). "Cannot read" is never
            // "normal" anywhere else in this mission and it is not here either.
            return SwapLevel is ResourceLevel.Tight or ResourceLevel.Critical && SwapLevel > worst
                ? SwapLevel
                : worst;
        }
    }

    /// <summary>
    /// What a screen reader says for the whole meter: "Resources: CPU 28%, memory 55%", and when anything is
    /// warning, the warning in words.
    ///
    /// THE WORDS MATTER BECAUSE COLOUR IS NO LONGER ENOUGH. The deleted banner carried its headline and body as
    /// an automation name, so a screen reader announced the warning. With the banner gone the bar's colour would
    /// have been the only carrier, which makes the warning unreachable for anyone who cannot see it (found in
    /// review of pull request 4732).
    /// </summary>
    public string AccessibleName
    {
        get
        {
            var name = $"Resources: CPU {Cpu.PercentText}, memory {Memory.PercentText}, disk {DiskSpoken}";
            var warnings = new List<string>();
            if (Cpu.Level is ResourceLevel.Tight or ResourceLevel.Critical) warnings.Add(Word(ResourceKind.Cpu, Cpu.Level));
            if (Memory.Level is ResourceLevel.Tight or ResourceLevel.Critical) warnings.Add(Word(ResourceKind.Memory, Memory.Level));
            if (Disk.Level is ResourceLevel.Tight or ResourceLevel.Critical) warnings.Add(Word(ResourceKind.Disk, Disk.Level));
            if (SwapLevel is ResourceLevel.Tight or ResourceLevel.Critical) warnings.Add(Word(ResourceKind.Swap, SwapLevel));
            return warnings.Count == 0 ? name : $"{name}. Warning: {string.Join("; ", warnings)}.";
        }
    }

    /// <summary>
    /// THE WORDS ARE PER RESOURCE, NOT ONE PAIR FOR ALL THREE. These are the deleted banner's own headlines,
    /// kept: a busy processor is "running hot", not "running short", and full swap is "nearly full", not
    /// "maxed out". Flattening them to one pair was a wording regression found in the second review of pull
    /// request 4732 - it reached a screen reader as "the processor is running short", which is not English for
    /// what happened.
    /// </summary>
    // "C: 6 GB free" - the bar's figure is free space, so a screen reader says so; a dash is "cannot be read".
    private string DiskSpoken => Disk.CouldRead ? $"{Disk.PercentText} free" : "cannot be read";

    private static string Word(ResourceKind kind, ResourceLevel level)
    {
        var critical = level == ResourceLevel.Critical;
        return kind switch
        {
            ResourceKind.Cpu => critical ? "the processor is maxed out" : "the processor is running hot",
            ResourceKind.Memory => critical ? "memory is nearly gone" : "memory is running short",
            ResourceKind.Disk => critical ? "a drive has under 10 GB free" : "a drive has under 25 GB free",
            _ => critical ? "swap is nearly full" : "swap is filling up",
        };
    }
}

/// <summary>The figures and words every Resource Monitor view shares, so the meter and the detail screen cannot disagree.</summary>
public static class ResourceViewText
{
    private const ulong Megabyte = 1024UL * 1024;
    private const ulong Gigabyte = 1024UL * 1024 * 1024;

    /// <summary>
    /// What a figure that could not be read shows instead of a number. A dash, never a zero and never a
    /// made-up figure; the tooltip carries why. ASCII, because the house rule forbids special symbols.
    /// </summary>
    public const string CannotReadText = "-";

    /// <summary>The whole-machine processor busy figure as a 0-to-1 fraction, or null when unreadable.</summary>
    ///
    /// NEVER CLAMPED. A busy figure outside 0 to 100 cannot be true - <see cref="ResourceRate"/> already turns
    /// an overshoot into null - so one that arrives here is a defect upstream. Clamping it would draw a tidy
    /// full bar and hide the very per-core-against-machine mistake the probe contract exists to catch. It is
    /// null instead, and <see cref="CpuOutOfRangeReason"/> says why.
    public static double? CpuFraction(CpuReading cpu)
    {
        if (!cpu.CouldRead) return null;
        var percent = cpu.TotalBusyPercent;
        if (double.IsNaN(percent) || percent < 0 || percent > 100) return null;
        return percent / 100.0;
    }

    /// <summary>
    /// Why a processor reading that the probe called readable still shows a dash: its figure was outside 0 to
    /// 100. Null when the figure is in range or the reading failed for its own stated reason.
    /// </summary>
    public static string? CpuOutOfRangeReason(CpuReading cpu) =>
        cpu.CouldRead && CpuFraction(cpu) is null
            ? string.Create(CultureInfo.InvariantCulture,
                $"the processor figure was {cpu.TotalBusyPercent:F1}%, which cannot be true")
            : null;

    /// <summary>
    /// The share of memory in USE, 0 to 1: one minus the available fraction, the figure the operating
    /// system's own monitor calls memory used. Null when unreadable, or when the reading is not believable
    /// (more available than installed), so a nonsense reading never draws a confident bar.
    /// </summary>
    public static double? MemoryUsedFraction(MachineMemoryReading memory)
    {
        if (!memory.CouldRead || memory.TotalBytes == 0 || memory.AvailableBytes > memory.TotalBytes) return null;
        return 1.0 - (double)memory.AvailableBytes / memory.TotalBytes;
    }

    /// <summary>A 0-to-1 fraction as a whole percentage, "28%", or the dash when null.</summary>
    public static string Percent(double? fraction) =>
        fraction is { } value
            ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(value * 100, MidpointRounding.AwayFromZero):F0}%")
            : CannotReadText;

    /// <summary>A processor share already in percent, "62%", or the dash when null.</summary>
    public static string PercentOf100(double? percent) =>
        percent is { } value ? Percent(value / 100.0) : CannotReadText;

    /// <summary>
    /// A size the way the mock-ups write it: kilobytes below a megabyte ("512 KB"), megabytes below a gigabyte
    /// ("966 MB"), then three significant figures ("1.21 GB", "16.0 GB", "128 GB"). The dash when null.
    /// </summary>
    public static string Bytes(ulong? bytes)
    {
        if (bytes is not { } value) return CannotReadText;
        var gigabytes = value / (double)Gigabyte;
        // Below a megabyte it is kilobytes, so a small helper process never reads as a made-up "0 MB".
        if (value < Megabyte)
            return string.Create(CultureInfo.InvariantCulture, $"{value / 1024.0:F0} KB");
        if (gigabytes < 1)
            return string.Create(CultureInfo.InvariantCulture, $"{value / (double)Megabyte:F0} MB");
        return gigabytes switch
        {
            < 10 => string.Create(CultureInfo.InvariantCulture, $"{gigabytes:F2} GB"),
            < 100 => string.Create(CultureInfo.InvariantCulture, $"{gigabytes:F1} GB"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{gigabytes:F0} GB"),
        };
    }

    /// <summary>"cannot read: the reason", the one wording for an unreadable figure.</summary>
    public static string CannotRead(string? reason) => $"cannot read: {reason ?? "no reason was given"}";

    /// <summary>"at least " when a total left out processes the platform refused to measure; empty otherwise.</summary>
    public static string LowerBoundPrefix(int processesWithUnknownFigures) =>
        processesWithUnknownFigures > 0 ? "at least " : string.Empty;
}

/// <summary>
/// Folds the sampler's state into the toolbar meter. A pure function - no clock, no platform call - so
/// every state the owner can see is pinned by a unit test.
/// </summary>
public static class ResourceMeterFold
{
    /// <summary>Builds the meter.</summary>
    /// <param name="state">The sampler's current state.</param>
    /// <param name="machineName">The machine's name, for the tooltip's first line.</param>
    /// <param name="runningSessions">How many sessions this Director is running.</param>
    /// <param name="directorSessions">
    /// What this Director's own sessions cost together, summed over their process trees; null when not
    /// known yet. Its <see cref="SessionUsage.SessionId"/> is ignored.
    /// </param>
    /// <param name="latestAge">
    /// How long ago the latest sample was taken, measured by the sampler on the monotonic clock
    /// (<see cref="ResourceSampler.LatestAge"/>). Never worked out from the sample's wall-clock stamp, which a
    /// stepped clock would make read as "just now" or "40 minutes ago". Null before the first read.
    /// </param>
    /// <param name="history">
    /// The sampler's history on its monotonic timeline (<see cref="ResourceSampler.TimelineHistory"/>), for the
    /// tooltip's "C: lost 8 GB in the last 60 minutes" line. Empty before the first read.
    /// </param>
    public static ResourceMeterView Build(
        ResourceSamplerState state,
        string machineName,
        int runningSessions,
        IReadOnlyList<ResourceSessionInput>? sessions,
        TimeSpan? latestAge,
        IReadOnlyList<TimelineSample> history)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);
        ArgumentNullException.ThrowIfNull(history);

        var cpu = CpuBar(state);
        var memory = MemoryBar(state);
        var disk = DiskBar(state);
        var swap = state.VerdictFor(ResourceKind.Swap);
        var directorSessions = Combine(sessions);

        var lines = new List<string>
        {
            $"{machineName} - {runningSessions} {(runningSessions == 1 ? "session" : "sessions")} running",
            cpu.Tooltip,
            memory.Tooltip,
        };

        // EVERY DRIVE, not only the one on the bar (the approved mock-up): the bar shows the tightest, the tooltip
        // says where the rest stand. Then the trend of each drive that is losing room, because "6 GB free" is an
        // emergency at 8 GB an afternoon and nothing at all at 1 GB a month.
        if (state.Latest is { } latestSample)
        {
            var diskSpace = latestSample.DiskSpace;
            if (!diskSpace.CouldRead || diskSpace.Drives.Count == 0) lines.Add(disk.Tooltip);
            foreach (var drive in diskSpace.Drives) lines.Add(DiskSpaceText.Line(drive));

            var timeline = history.OrderBy(h => h.Timeline).Select(h => (h.Timeline, h.Sample.DiskSpace)).ToList();
            foreach (var drive in diskSpace.Drives.Where(d => d.CouldRead))
            {
                if (DiskSpaceText.Trend(drive.Name, timeline) is { } trend && trend.StartsWith("lost ", StringComparison.Ordinal))
                    lines.Add($"{drive.Name} {trend}");
            }
        }

        // THE WARNING BANNER'S ONE UNIQUE FACT, KEPT (the owner's ruling of 2026-11-07, which deleted the banner
        // strip outright: "it should really just show red in the resource meter, that's enough").
        //
        // Everything else the banner said is said better elsewhere: its headline repeated the bar's own colour,
        // and its evidence is already this bar's tooltip line. The one thing NEITHER can say is WHICH PROGRAM is
        // eating the resource, and whether it is one of ours - on the screenshot that prompted this, the answer
        // was chrome.exe at 4.48 GB and not a session of this Director's, which is exactly the fact that tells
        // you whether to look at DevThrottle at all. So that sentence moves here and nothing else does.
        //
        // ONLY FOR A BAR THAT IS ACTUALLY AMBER OR RED. On a quiet machine it would be a line about nothing, and
        // a tooltip padded with lines nobody needs is the banner's own mistake in a smaller space.
        foreach (var bar in new[] { cpu, memory })
        {
            if (bar.Level is ResourceLevel.Tight or ResourceLevel.Critical)
                lines.Add(BiggestUser(bar.Kind, state.Latest, sessions));
        }



        if (state.Latest is { } sample)
        {
            lines.Add(sample.Swap.CouldRead
                ? WhyIfWarning($"Swap {ResourceViewText.Bytes(sample.Swap.UsedBytes)} of {ResourceViewText.Bytes(sample.Swap.TotalBytes)}", swap)
                : $"Swap {ResourceViewText.CannotRead(sample.Swap.UnreadableReason)}");

            // Swap in trouble names the biggest memory user too, exactly as the banner did: a machine paging
            // hard is a machine short of memory, and the program to look at is the same one. Only when memory
            // has not already named it, or the same process is named twice.
            //
            // IT GOES HERE, AFTER THE SWAP LINE, not up with the bars. The second review of pull request 4732
            // found it landing ABOVE the swap line that justifies it and directly under a HEALTHY memory line,
            // so it read as a memory accusation with nothing to explain it. A reason must precede what it
            // explains.
            if (swap.Level is ResourceLevel.Tight or ResourceLevel.Critical
                && memory.Level is not (ResourceLevel.Tight or ResourceLevel.Critical))
            {
                lines.Add(BiggestUser(ResourceKind.Memory, state.Latest, sessions));
            }

            if (directorSessions is { } own)
            {
                var memoryPrefix = ResourceViewText.LowerBoundPrefix(own.ProcessesWithUnknownMemory);
                var cpuPrefix = ResourceViewText.LowerBoundPrefix(own.ProcessesWithUnknownProcessor);
                lines.Add($"This Director's sessions {memoryPrefix}{ResourceViewText.Bytes(own.MemoryBytes)} and {cpuPrefix}{ResourceViewText.PercentOf100(own.CpuPercent)} CPU");
            }

            lines.Add(latestAge is { } age
                ? $"Updated {Age(age)} - click for detail"
                : "Updated - click for detail");
        }
        else
        {
            lines.Add("Waiting for the first reading - click for detail");
        }

        return new ResourceMeterView(cpu, memory, disk, NarrowText(cpu, memory, disk), lines, swap.Level);
    }

    /// <summary>
    /// The compact form keeps all three as text: "CPU 23% MEM 61% C: 6 GB" (the approved mock-up). An unreadable disk
    /// reads "DISK -", so the owner can still tell which figure is missing.
    /// </summary>
    private static string NarrowText(ResourceBarView cpu, ResourceBarView memory, ResourceBarView disk) =>
        $"CPU {cpu.PercentText} MEM {memory.PercentText} {(disk.CouldRead ? disk.PercentText : "DISK " + disk.PercentText)}";

    /// <summary>
    /// The meter when the resource monitor never started: both bars cannot be read, and say why. Never "not read
    /// yet", which would promise a reading that is not coming.
    /// </summary>
    public static ResourceMeterView NotStarted(string machineName, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var why = $"the resource monitor did not start: {reason}";
        var cpu = Unreadable(ResourceKind.Cpu, "CPU", why);
        var memory = Unreadable(ResourceKind.Memory, "MEM", why);
        var disk = Unreadable(ResourceKind.Disk, "DISK", why);
        return new ResourceMeterView(cpu, memory, disk, NarrowText(cpu, memory, disk),
            [machineName, cpu.Tooltip, memory.Tooltip, disk.Tooltip]);
    }

    /// <summary>
    /// "Biggest: chrome.exe (not attributed to any of this Director's sessions), 4.48 GB." - the one sentence the
    /// deleted warning banner carried that neither the bar nor its tooltip line could.
    ///
    /// AN UNKNOWN IS SAID, NEVER READ AS ZERO. A process with no figure is skipped rather than ranked at zero, and
    /// when no process has a figure at all the line says so instead of naming whichever one happened to sort first.
    /// </summary>
    /// <param name="sessions">
    /// This Director's sessions and their process trees. NULL IS NOT EMPTY: an empty list is a Director running no
    /// sessions, so the busiest process is certainly not one of ours and the line may say so; null means nobody has
    /// worked out which processes belong to which session, and the line then claims nothing about ownership.
    /// </param>
    private static string BiggestUser(ResourceKind kind, ResourceSample? sample, IReadOnlyList<ResourceSessionInput>? sessions)
    {
        if (sample is null || !sample.Processes.CouldRead)
            return "The busiest process could not be named: " +
                   (sample is null ? "there is no reading yet." : $"{sample.Processes.UnreadableReason}.");

        var byProcessor = kind == ResourceKind.Cpu;
        ProcessReading? top = null;
        foreach (var process in sample.Processes.Processes)
        {
            if (byProcessor)
            {
                if (process.CpuPercent is not { } cpu) continue;
                if (top is null || cpu > top.CpuPercent!.Value) top = process;
            }
            else
            {
                if (process.MemoryBytes is not { } bytes) continue;
                if (top is null || bytes > top.MemoryBytes!.Value) top = process;
            }
        }

        if (top is null)
            return byProcessor
                ? "The busiest process is not known yet: no process has a processor figure in this reading."
                : "The biggest process is not known: no process has a memory figure in this reading.";

        var figure = byProcessor
            ? ResourceViewText.PercentOf100(top.CpuPercent)
            : ResourceViewText.Bytes(top.MemoryBytes);
        string where;
        if (sessions is null)
        {
            where = string.Empty;
        }
        else
        {
            var owner = sessions.FirstOrDefault(s => s.Usage.Pids.Contains(top.Pid));
            where = owner is null ? " (not attributed to any of this Director's sessions)" : $" in session {owner.Label}";
        }
        return $"{(byProcessor ? "Busiest" : "Biggest")}: {top.Name}{where}, {figure}.";
    }

    /// <summary>
    /// What this Director's sessions cost together - the tooltip's "This Director's sessions" line. Each figure
    /// is the sum over the sessions, and each lower-bound count is the sum of theirs, so the total is a floor
    /// exactly when any session's figure was. Null when there are no sessions or they are not known, so the
    /// tooltip leaves the line out rather than claiming the sessions cost nothing.
    /// </summary>
    public static SessionUsage? Combine(IReadOnlyList<ResourceSessionInput>? sessions)
    {
        if (sessions is null || sessions.Count == 0) return null;
        ulong memory = 0;
        foreach (var session in sessions) memory += session.Usage.MemoryBytes;
        return new SessionUsage(
            "all",
            0,
            sessions.Sum(s => s.Usage.CpuPercent),
            memory,
            sessions.SelectMany(s => s.Usage.Pids).ToList(),
            sessions.Sum(s => s.Usage.ProcessesWithUnknownProcessor),
            sessions.Sum(s => s.Usage.ProcessesWithUnknownMemory));
    }

    private static ResourceBarView CpuBar(ResourceSamplerState state)
    {
        var verdict = state.VerdictFor(ResourceKind.Cpu);
        var level = verdict.Level;
        if (state.Latest is not { } sample)
            return Unreadable(ResourceKind.Cpu, "CPU", ResourceSamplerState.NotYetSampledReason);

        var cpu = sample.Cpu;
        if (!cpu.CouldRead)
            return Unreadable(ResourceKind.Cpu, "CPU", cpu.UnreadableReason);

        var fraction = ResourceViewText.CpuFraction(cpu);
        if (fraction is null)
        {
            var reason = ResourceViewText.CpuOutOfRangeReason(cpu);
            CcDirector.Core.Utilities.FileLog.Write($"[ResourceMeterFold] CpuBar: shown as cannot read - {reason}");
            return Unreadable(ResourceKind.Cpu, "CPU", reason);
        }

        var text = ResourceViewText.Percent(fraction);
        var cores = cpu.LogicalCoreCount is { } count
            ? string.Create(CultureInfo.InvariantCulture, $" of {count} cores")
            : string.Empty;
        var tooltip = WhyIfWarning($"CPU {text}{cores} - busiest core {ResourceViewText.PercentOf100(cpu.BusiestCorePercent)}", verdict);
        return new ResourceBarView(ResourceKind.Cpu, "CPU", CouldRead: true, fraction, text, level, tooltip);
    }

    private static ResourceBarView MemoryBar(ResourceSamplerState state)
    {
        var verdict = state.VerdictFor(ResourceKind.Memory);
        var level = verdict.Level;
        if (state.Latest is not { } sample)
            return Unreadable(ResourceKind.Memory, "MEM", ResourceSamplerState.NotYetSampledReason);

        var memory = sample.Memory;
        var fraction = ResourceViewText.MemoryUsedFraction(memory);
        if (fraction is null)
            return Unreadable(ResourceKind.Memory, "MEM",
                memory.CouldRead ? "the memory reading did not add up" : memory.UnreadableReason);

        var used = memory.TotalBytes - memory.AvailableBytes;
        var tooltip = WhyIfWarning(
            $"Memory {ResourceViewText.Bytes(used)} used of {ResourceViewText.Bytes(memory.TotalBytes)} - {ResourceViewText.Bytes(memory.AvailableBytes)} available",
            verdict);
        return new ResourceBarView(ResourceKind.Memory, "MEM", CouldRead: true, fraction, ResourceViewText.Percent(fraction), level, tooltip);
    }

    /// <summary>
    /// A warning bar says WHY, by appending the verdict's own evidence to its figures.
    ///
    /// WITHOUT THIS A RED BAR CAN SIT BESIDE A HEALTHY-LOOKING NUMBER. The memory verdict holds its level for
    /// sixty seconds after the load eases (<see cref="ResourcePressure.SustainedWindow"/>), so the bar is red
    /// while its own figures already read "60 per cent used, plenty available". The deleted warning banner
    /// printed that reason; the tooltip never did, and review of pull request 4732 found that the claim "the
    /// evidence is already this bar's tooltip line" was simply untrue. The first bug report would have been
    /// "the memory bar is red and the numbers say it is fine".
    ///
    /// Only for a warning level: on a healthy machine the evidence is "the processor is 28% busy", which the
    /// figures already said.
    /// </summary>
    private static string WhyIfWarning(string figures, ResourceVerdict verdict) =>
        verdict.Level is ResourceLevel.Tight or ResourceLevel.Critical && !string.IsNullOrWhiteSpace(verdict.Evidence)
            ? $"{figures} - {verdict.Evidence}"
            : figures;

    /// <summary>
    /// The DISK bar: the readable fixed drive with the least room left. The bar fills with how FULL it is; the figure
    /// beside it is how much is LEFT ("C: 6 GB"); the colour is <see cref="DiskSpacePressure"/>'s line in gigabytes.
    /// A drive that cannot be read never stands in for one that can, and is never drawn as zero: when no drive can be
    /// read the bar is the dashed "cannot read" bar, with the reason.
    /// </summary>
    private static ResourceBarView DiskBar(ResourceSamplerState state)
    {
        if (state.Latest is not { } sample)
            return Unreadable(ResourceKind.Disk, "DISK", ResourceSamplerState.NotYetSampledReason);

        var disks = sample.DiskSpace;
        if (!disks.CouldRead)
            return Unreadable(ResourceKind.Disk, "DISK", disks.UnreadableReason);
        if (disks.Drives.Count == 0)
            return Unreadable(ResourceKind.Disk, "DISK", "no fixed drive was found on this machine");

        if (DiskSpacePressure.Tightest(disks) is not { } drive)
        {
            var first = disks.Drives[0];
            return Unreadable(ResourceKind.Disk, "DISK", $"no drive could be read; {first.Name}: {first.UnreadableReason}");
        }

        var fraction = DiskSpaceText.UsedFraction(drive);
        if (fraction is null)
            return Unreadable(ResourceKind.Disk, "DISK", $"drive {drive.Name} reported a size of zero");

        return new ResourceBarView(ResourceKind.Disk, "DISK", CouldRead: true, fraction, DiskSpaceText.Short(drive),
            DiskSpacePressure.Level(drive), DiskSpaceText.Line(drive));
    }

    // An unreadable bar is Unknown whatever the verdict says: the bar shows nothing, so it cannot be coloured as though it did.
    private static ResourceBarView Unreadable(ResourceKind kind, string label, string? reason) =>
        new(kind, label, CouldRead: false, Fraction: null, ResourceViewText.CannotReadText, ResourceLevel.Unknown,
            $"{BarWord(kind)} {ResourceViewText.CannotRead(reason)}");

    private static string BarWord(ResourceKind kind) => kind switch
    {
        ResourceKind.Cpu => "CPU",
        ResourceKind.Memory => "Memory",
        _ => "Disk space",
    };

    private static string Age(TimeSpan age)
    {
        if (age < TimeSpan.Zero) return "just now";
        var seconds = (int)age.TotalSeconds;
        if (seconds < 2) return "just now";
        if (seconds < 120) return $"{seconds} seconds ago";
        return $"{seconds / 60} minutes ago";
    }
}
