namespace CcDirector.Gateway.Stats.Data.Entities;

/// <summary>
/// The figures every resource summary row carries, five-minute or hourly, machine or session (Mission: Resource
/// Monitor, section 6): average and peak for memory and the processor, how many samples read each, why one could not
/// be read, and every amber or red crossing.
///
/// A figure that could not be read is NULL with its reason beside it - never a zero. The sample counts are the weights
/// an hourly roll-up averages by, so an hour whose first window was read from 12 samples and its second from 60 is not
/// averaged as if both counted the same.
/// </summary>
public interface IResourceFigureColumns
{
    int ProcessorSamples { get; set; }
    double? ProcessorAveragePercent { get; set; }
    double? ProcessorPeakPercent { get; set; }
    string? ProcessorUnreadableReason { get; set; }
    int MemorySamples { get; set; }
    long? MemoryAverageUsedBytes { get; set; }
    long? MemoryPeakUsedBytes { get; set; }
    long? MemoryTotalBytes { get; set; }
    string? MemoryUnreadableReason { get; set; }

    /// <summary>The crossings as a JSON array of <see cref="Contracts.ResourceCrossing"/>; "[]" when nothing crossed.</summary>
    string CrossingsJson { get; set; }
}

/// <summary>
/// One machine's five-minute window as one Director measured it (<c>resource_window</c>). Kept 90 days. Keyed by
/// (tenant, Director, window start), so a window the Director sends twice - a catch-up that overlaps an earlier send -
/// is stored once.
/// </summary>
public sealed class ResourceWindowEntity : IResourceFigureColumns
{
    public string Tenant { get; set; } = "";
    public string DirectorId { get; set; } = "";
    public DateTime WindowStartUtc { get; set; }

    /// <summary>The UTC hour the window falls in, stored so the hourly roll-up can find an hour's windows, and the
    /// hours still to roll, with plain equality on both providers.</summary>
    public DateTime HourStartUtc { get; set; }

    public string MachineName { get; set; } = "";
    public int WindowSeconds { get; set; }
    public int SampleCount { get; set; }

    /// <summary>When the Gateway stored the window, by the Gateway's clock. A window received after its hour was rolled
    /// up makes that hour roll up again.</summary>
    public DateTime ReceivedUtc { get; set; }

    /// <summary>
    /// Why the Gateway refused this window's figures, or null when it took them. A window whose figures no machine can
    /// produce (a processor above 100 per cent, a peak below its average, more memory in use than the machine has) is
    /// STORED, with every figure null and this reason, rather than dropped: a dropped window is a silent gap that reads
    /// exactly like an idle or offline machine, and a refused window is neither.
    /// </summary>
    public string? RefusedReason { get; set; }

    public int ProcessorSamples { get; set; }
    public double? ProcessorAveragePercent { get; set; }
    public double? ProcessorPeakPercent { get; set; }
    public string? ProcessorUnreadableReason { get; set; }
    public int MemorySamples { get; set; }
    public long? MemoryAverageUsedBytes { get; set; }
    public long? MemoryPeakUsedBytes { get; set; }
    public long? MemoryTotalBytes { get; set; }
    public string? MemoryUnreadableReason { get; set; }
    public string CrossingsJson { get; set; } = "[]";

    /// <summary>
    /// Each fixed drive's free space in the window, as the JSON of a list of
    /// <see cref="CcDirector.Gateway.Contracts.ResourceDriveSummary"/> (Disk Space on the Meter, 7 November 2026).
    /// "[]" when the Director reported no drives - one older than that sends none - and on a refused row.
    /// </summary>
    public string DrivesJson { get; set; } = "[]";
}

/// <summary>
/// Where one drive on one machine stands for the owner's disk space notices (<c>resource_disk_notice</c>, Disk Space
/// on the Meter, 7 November 2026). One row per tenant, machine and drive. It is what makes "at most once a day per
/// drive" and "a recovery message only after a red one" survive a Gateway restart.
/// </summary>
public sealed class ResourceDiskNoticeEntity
{
    public string Tenant { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string Drive { get; set; } = "";

    /// <summary>True while the drive is under the red line, as of <see cref="LastWindowStartUtc"/>.</summary>
    public bool IsRed { get; set; }

    /// <summary>When the last red notice for this drive was written; null when none ever was.</summary>
    public DateTime? LastRedNoticeUtc { get; set; }

    /// <summary>True when the current red spell was told to the owner, so its recovery is told too.</summary>
    public bool RedSpellNoticed { get; set; }

    /// <summary>The start of the newest window this row has judged. An older window that arrives late is not judged.</summary>
    public DateTime LastWindowStartUtc { get; set; }
}

/// <summary>One session's share of a five-minute window (<c>resource_session_window</c>). Kept 90 days.</summary>
public sealed class ResourceSessionWindowEntity : IResourceFigureColumns
{
    public string Tenant { get; set; } = "";
    public string DirectorId { get; set; } = "";
    public DateTime WindowStartUtc { get; set; }
    public string SessionId { get; set; } = "";
    public DateTime HourStartUtc { get; set; }
    public string MachineName { get; set; } = "";

    /// <summary>The session's Factory value, or null when it belongs to no factory. Not a Mission.</summary>
    public string? Factory { get; set; }

    public int SampleCount { get; set; }

    /// <summary>The most processes whose figure could not be read; above zero the figures are lower bounds.</summary>
    public int ProcessesWithUnknownFigures { get; set; }

    public DateTime ReceivedUtc { get; set; }

    public int ProcessorSamples { get; set; }
    public double? ProcessorAveragePercent { get; set; }
    public double? ProcessorPeakPercent { get; set; }
    public string? ProcessorUnreadableReason { get; set; }
    public int MemorySamples { get; set; }
    public long? MemoryAverageUsedBytes { get; set; }
    public long? MemoryPeakUsedBytes { get; set; }
    public long? MemoryTotalBytes { get; set; }
    public string? MemoryUnreadableReason { get; set; }
    public string CrossingsJson { get; set; } = "[]";
}

/// <summary>
/// One machine's UTC hour, rolled up from its five-minute windows (<c>resource_hour</c>). Kept two years. Averages
/// are weighted by the samples that read them, peaks are the highest window peak, crossings are every window's.
/// </summary>
public sealed class ResourceHourEntity : IResourceFigureColumns
{
    public string Tenant { get; set; } = "";
    public string DirectorId { get; set; } = "";
    public DateTime HourStartUtc { get; set; }
    public string MachineName { get; set; } = "";

    /// <summary>How many five-minute windows the hour was rolled from (12 for a whole hour).</summary>
    public int WindowCount { get; set; }

    /// <summary>How many of those windows had their figures refused; they count towards nothing else.</summary>
    public int RefusedWindows { get; set; }

    public int SampleCount { get; set; }

    /// <summary>When the roll-up that wrote this row began. A window received at or after this time makes the hour
    /// roll up again.</summary>
    public DateTime RolledAtUtc { get; set; }

    public int ProcessorSamples { get; set; }
    public double? ProcessorAveragePercent { get; set; }
    public double? ProcessorPeakPercent { get; set; }
    public string? ProcessorUnreadableReason { get; set; }
    public int MemorySamples { get; set; }
    public long? MemoryAverageUsedBytes { get; set; }
    public long? MemoryPeakUsedBytes { get; set; }
    public long? MemoryTotalBytes { get; set; }
    public string? MemoryUnreadableReason { get; set; }
    public string CrossingsJson { get; set; } = "[]";
}

/// <summary>One session's UTC hour, rolled up from its five-minute rows (<c>resource_session_hour</c>). Kept two
/// years.</summary>
public sealed class ResourceSessionHourEntity : IResourceFigureColumns
{
    public string Tenant { get; set; } = "";
    public string DirectorId { get; set; } = "";
    public DateTime HourStartUtc { get; set; }
    public string SessionId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string? Factory { get; set; }
    public int WindowCount { get; set; }
    public int SampleCount { get; set; }

    /// <summary>The most processes whose figure could not be read in any of the hour's windows; above zero the
    /// figures are lower bounds.</summary>
    public int ProcessesWithUnknownFigures { get; set; }

    public DateTime RolledAtUtc { get; set; }

    public int ProcessorSamples { get; set; }
    public double? ProcessorAveragePercent { get; set; }
    public double? ProcessorPeakPercent { get; set; }
    public string? ProcessorUnreadableReason { get; set; }
    public int MemorySamples { get; set; }
    public long? MemoryAverageUsedBytes { get; set; }
    public long? MemoryPeakUsedBytes { get; set; }
    public long? MemoryTotalBytes { get; set; }
    public string? MemoryUnreadableReason { get; set; }
    public string CrossingsJson { get; set; } = "[]";
}
