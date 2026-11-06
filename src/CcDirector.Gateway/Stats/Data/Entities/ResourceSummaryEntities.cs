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
