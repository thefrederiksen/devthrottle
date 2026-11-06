using System.Globalization;

namespace CcDirector.Core.Machine;

/// <summary>
/// One bar of the toolbar meter, finished: the view binds to it and decides nothing (CLAUDE.md rule 7).
/// There are exactly two bars, CPU and MEM. Nothing about the disk, a volume or the network is ever a bar.
/// </summary>
/// <param name="Kind">Which resource: <see cref="ResourceKind.Cpu"/> or <see cref="ResourceKind.Memory"/>.</param>
/// <param name="Label">The on-screen label, "CPU" or "MEM".</param>
/// <param name="CouldRead">False when the reading could not be taken. The bar is then empty and the text a dash.</param>
/// <param name="Fraction">How full the bar is, 0.0 to 1.0. Null when unreadable - never zero.</param>
/// <param name="PercentText">"28%", or <see cref="ResourceViewText.CannotReadText"/> when unreadable.</param>
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
/// <param name="NarrowText">What a narrow window shows instead of the bars, for example "28%/55%" (issue #2812).</param>
/// <param name="TooltipLines">The meter's tooltip, one line each, machine figures first and then this Director's own sessions.</param>
public sealed record ResourceMeterView(
    ResourceBarView Cpu,
    ResourceBarView Memory,
    string NarrowText,
    IReadOnlyList<string> TooltipLines);

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
    public static double? CpuFraction(CpuReading cpu) =>
        cpu.CouldRead ? Math.Clamp(cpu.TotalBusyPercent / 100.0, 0, 1) : null;

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
    /// <param name="nowUtc">Now, for "updated 3 seconds ago". Passed in so the fold reads no clock.</param>
    public static ResourceMeterView Build(
        ResourceSamplerState state,
        string machineName,
        int runningSessions,
        SessionUsage? directorSessions,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);

        var cpu = CpuBar(state);
        var memory = MemoryBar(state);

        var lines = new List<string>
        {
            $"{machineName} - {runningSessions} {(runningSessions == 1 ? "session" : "sessions")} running",
            cpu.Tooltip,
            memory.Tooltip,
        };

        if (state.Latest is { } sample)
        {
            lines.Add(sample.Swap.CouldRead
                ? $"Swap {ResourceViewText.Bytes(sample.Swap.UsedBytes)} of {ResourceViewText.Bytes(sample.Swap.TotalBytes)}"
                : $"Swap {ResourceViewText.CannotRead(sample.Swap.UnreadableReason)}");

            if (directorSessions is { } own)
            {
                var memoryPrefix = ResourceViewText.LowerBoundPrefix(own.ProcessesWithUnknownMemory);
                var cpuPrefix = ResourceViewText.LowerBoundPrefix(own.ProcessesWithUnknownProcessor);
                lines.Add($"This Director's sessions {memoryPrefix}{ResourceViewText.Bytes(own.MemoryBytes)} and {cpuPrefix}{ResourceViewText.PercentOf100(own.CpuPercent)} CPU");
            }

            lines.Add($"Updated {Age(nowUtc - sample.TakenAtUtc)} - click for detail");
        }
        else
        {
            lines.Add("Waiting for the first reading - click for detail");
        }

        return new ResourceMeterView(cpu, memory, $"{cpu.PercentText}/{memory.PercentText}", lines);
    }

    private static ResourceBarView CpuBar(ResourceSamplerState state)
    {
        var level = state.VerdictFor(ResourceKind.Cpu).Level;
        if (state.Latest is not { } sample)
            return Unreadable(ResourceKind.Cpu, "CPU", ResourceSamplerState.NotYetSampledReason);

        var cpu = sample.Cpu;
        if (!cpu.CouldRead)
            return Unreadable(ResourceKind.Cpu, "CPU", cpu.UnreadableReason);

        var fraction = ResourceViewText.CpuFraction(cpu);
        var text = ResourceViewText.Percent(fraction);
        var tooltip = string.Create(CultureInfo.InvariantCulture,
            $"CPU {text} of {cpu.LogicalCoreCount} cores - busiest core {ResourceViewText.PercentOf100(cpu.BusiestCorePercent)}");
        return new ResourceBarView(ResourceKind.Cpu, "CPU", CouldRead: true, fraction, text, level, tooltip);
    }

    private static ResourceBarView MemoryBar(ResourceSamplerState state)
    {
        var level = state.VerdictFor(ResourceKind.Memory).Level;
        if (state.Latest is not { } sample)
            return Unreadable(ResourceKind.Memory, "MEM", ResourceSamplerState.NotYetSampledReason);

        var memory = sample.Memory;
        var fraction = ResourceViewText.MemoryUsedFraction(memory);
        if (fraction is null)
            return Unreadable(ResourceKind.Memory, "MEM",
                memory.CouldRead ? "the memory reading did not add up" : memory.UnreadableReason);

        var used = memory.TotalBytes - memory.AvailableBytes;
        var tooltip = $"Memory {ResourceViewText.Bytes(used)} used of {ResourceViewText.Bytes(memory.TotalBytes)} - {ResourceViewText.Bytes(memory.AvailableBytes)} available";
        return new ResourceBarView(ResourceKind.Memory, "MEM", CouldRead: true, fraction, ResourceViewText.Percent(fraction), level, tooltip);
    }

    // An unreadable bar is Unknown whatever the verdict says: the bar shows nothing, so it cannot be coloured as though it did.
    private static ResourceBarView Unreadable(ResourceKind kind, string label, string? reason) =>
        new(kind, label, CouldRead: false, Fraction: null, ResourceViewText.CannotReadText, ResourceLevel.Unknown,
            $"{(kind == ResourceKind.Cpu ? "CPU" : "Memory")} {ResourceViewText.CannotRead(reason)}");

    private static string Age(TimeSpan age)
    {
        if (age < TimeSpan.Zero) return "just now";
        var seconds = (int)age.TotalSeconds;
        if (seconds < 2) return "just now";
        if (seconds < 120) return $"{seconds} seconds ago";
        return $"{seconds / 60} minutes ago";
    }
}
