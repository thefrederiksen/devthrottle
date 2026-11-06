namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One machine's processor, memory and spare capacity on the Fleet, Machines page (Mission: Resource Monitor, section
/// 5j). Folded once on the Gateway by ResourceCapacityFold from the five-minute resource summaries; a client renders
/// every string as sent and every tone as a colour, and decides nothing (CLAUDE.md rule 7).
///
/// AN OFFLINE MACHINE SHOWS DASHES, NEVER ZEROS. Offline is not idle: a machine with no running Director, or one that
/// has sent no summary for 15 minutes, has every label set to <see cref="Dash"/> and <see cref="Reporting"/> false, with
/// <see cref="Why"/> saying which.
/// </summary>
public sealed class FleetMachineCapacityDto
{
    /// <summary>The label shown where a figure is not known.</summary>
    public const string Dash = "—";

    /// <summary>True when the figures below are a recent reading of a running machine.</summary>
    public bool Reporting { get; set; }

    /// <summary>The processor now, for example "28%"; "cannot read" when the machine could not read it; a dash when not reporting.</summary>
    public string ProcessorLabel { get; set; } = Dash;

    /// <summary>The second line under it, for example "peak 71%" (the highest five-second reading in the last hour). Null when
    /// there is nothing to say.</summary>
    public string? ProcessorDetail { get; set; }

    public string ProcessorTone { get; set; } = FleetTone.Idle;

    /// <summary>Memory now as a share of the machine, for example "55%".</summary>
    public string MemoryLabel { get; set; } = Dash;

    /// <summary>The second line, for example "8.9 / 16 GB" (in use of total).</summary>
    public string? MemoryDetail { get; set; }

    public string MemoryTone { get; set; } = FleetTone.Idle;

    /// <summary>
    /// The headroom for placing the next factory, for example "7.1 GB": free memory at the last hour's PEAK, not right now, so
    /// a machine that spikes is not advertised as roomy.
    /// </summary>
    public string SpareLabel { get; set; } = Dash;

    /// <summary>The same figure in bytes, for sorting the Spare column. Null when not known, which sorts last.</summary>
    public long? SpareBytes { get; set; }

    /// <summary>A short state for the Status column when the machine is over a limit now, for example "CPU hot" or
    /// "Memory tight". Null when it is not.</summary>
    public string? StatusLabel { get; set; }

    public string StatusTone { get; set; } = FleetTone.Idle;

    /// <summary>Why the figures are dashes or "cannot read", in a sentence. Null when they are a normal reading.</summary>
    public string? Why { get; set; }

    /// <summary>The start of the five-minute window the "now" figures come from. Null when there is none.</summary>
    public DateTime? ReadingWindowStartUtc { get; set; }
}

/// <summary>
/// GET /machines/{machine}/resources - one machine expanded (Mission: Resource Monitor, section 5k): 24 hours of
/// five-minute rows for the chart, each amber or red crossing marked, then the cost per registered factory. Folded once
/// on the Gateway by MachineResourceHistoryFold.
/// </summary>
public sealed class MachineResourceHistoryDto
{
    public string Machine { get; set; } = "";

    /// <summary>The span the rows cover, in hours.</summary>
    public int Hours { get; set; }

    /// <summary>What to show instead of the chart and the table when there are no rows. Null when there are rows.</summary>
    public string? EmptyMessage { get; set; }

    /// <summary>The five-minute rows, oldest first.</summary>
    public List<MachineResourceWindowDto> Windows { get; set; } = new();

    /// <summary>Cost per registered factory over the span, grouped by each session's Factory value (not its Mission),
    /// highest processor peak first, with "No factory" last.</summary>
    public List<FactoryResourceCostDto> Factories { get; set; } = new();
}

/// <summary>One five-minute row of the chart: the average line, the peak it is shaded up to, and its crossings.</summary>
public sealed class MachineResourceWindowDto
{
    public DateTime WindowStartUtc { get; set; }

    /// <summary>Processor average and peak, 0 to 100, or null when the window could not read the processor.</summary>
    public double? ProcessorAveragePercent { get; set; }
    public double? ProcessorPeakPercent { get; set; }

    /// <summary>Memory in use as a share of the machine, average and peak, or null when not read.</summary>
    public double? MemoryAveragePercent { get; set; }
    public double? MemoryPeakPercent { get; set; }

    /// <summary>Each amber or red crossing in the window, to mark on the chart.</summary>
    public List<ResourceCrossingMarkDto> Crossings { get; set; } = new();

    /// <summary>Why the Gateway refused this window's figures, or null when it took them. A refused window has no
    /// figures but is NOT a gap: the machine sent something impossible, which is a fault to show, not an idle stretch.</summary>
    public string? RefusedReason { get; set; }
}

/// <summary>One crossing as the chart marks it.</summary>
public sealed class ResourceCrossingMarkDto
{
    public DateTime StartedUtc { get; set; }

    /// <summary>"CPU" or "Memory".</summary>
    public string Resource { get; set; } = "";

    /// <summary>The words for the mark, for example "CPU red for 90 seconds, peak 99%".</summary>
    public string Label { get; set; } = "";

    public string Tone { get; set; } = FleetTone.Idle;
}

/// <summary>One factory's cost on the machine over the span (section 5k).</summary>
public sealed class FactoryResourceCostDto
{
    /// <summary>The factory's name, or "No factory".</summary>
    public string Factory { get; set; } = "";

    /// <summary>How many distinct sessions of the factory ran in the span.</summary>
    public int Sessions { get; set; }

    /// <summary>For example "22% / 61%": average and peak processor use of the factory's sessions together.</summary>
    public string ProcessorLabel { get; set; } = "";

    /// <summary>For example "3.1 / 5.8 GB": average and peak memory of the factory's sessions together.</summary>
    public string MemoryLabel { get; set; } = "";

    /// <summary>The processor peak as a number, for sorting. Null when never read.</summary>
    public double? ProcessorPeakPercent { get; set; }
}
