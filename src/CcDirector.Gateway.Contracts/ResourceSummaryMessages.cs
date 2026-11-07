namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The five-minute resource summary a Director sends its Gateway (Mission: Resource Monitor, section 6 of
/// missions/resource-monitor.html). The Director samples memory and the processor every five seconds on its own
/// machine; every five minutes it closes a window and sends ONE summary of it: one machine row and one row per
/// session, each carrying the average, the peak and every amber or red crossing inside the window.
///
/// WHY FOUR NUMBERS AND A CROSSING NOTE, AND NOT TWO. An average alone hides a spike: a processor pinned at 99 per
/// cent for 90 seconds inside an otherwise quiet five minutes averages to about 35 per cent. The peak keeps how high
/// it went and the crossing note keeps that it crossed a limit, at what level and for how long. That is the owner's
/// answer of 6 November 2026 to "what goes in a row".
///
/// HOW IT TRAVELS. On the tunnel the Director already holds open, as the hub method
/// <see cref="ResourceSummaryContract.PushHubMethod"/> beside PushSnapshot. No new connection and no new
/// authentication. A Director sends only when the Gateway's Hello listed the method; an older Gateway is never sent
/// it, and an older Director never calls it.
///
/// WHAT IS DELIBERATELY ABSENT. No disk and no network figure (the owner's narrowing of 6 November 2026), and no
/// tenant: the tenant is the connection's, bound at Hello from the authenticated key, never something the payload
/// can name.
/// </summary>
public static class ResourceSummaryContract
{
    /// <summary>The hub method that carries a batch of five-minute windows up the tunnel.</summary>
    public const string PushHubMethod = "PushResourceSummaries";

    /// <summary>The length of one summary window, in seconds. Windows start on five-minute boundaries of UTC.</summary>
    public const int WindowSeconds = 300;

    /// <summary>How long the Director keeps windows the Gateway has not acknowledged: 24 hours, after which the
    /// oldest are dropped (the Director's own local recording still has every five-second sample).</summary>
    public static readonly TimeSpan BufferSpan = TimeSpan.FromHours(24);

    /// <summary>The most windows that fit in the 24-hour buffer: 288.</summary>
    public static readonly int MaxBufferedWindows = (int)(BufferSpan.TotalSeconds / WindowSeconds);

    /// <summary>The most windows one call carries. A catch-up after a long outage is several calls, each well under
    /// the hub's receive limit even with many sessions per window.</summary>
    public const int MaxWindowsPerBatch = 24;

    /// <summary>The resource name for the processor, as it appears in a crossing.</summary>
    public const string Processor = "processor";

    /// <summary>The resource name for memory, as it appears in a crossing.</summary>
    public const string Memory = "memory";

    /// <summary>A crossing into the warning level (the meter's amber; MemoryPressure's Tight).</summary>
    public const string Amber = "amber";

    /// <summary>A crossing into the critical level (the meter's red; MemoryPressure's Critical).</summary>
    public const string Red = "red";

    /// <summary>The start of the five-minute window that holds <paramref name="utc"/>.</summary>
    public static DateTime WindowStartFor(DateTime utc)
    {
        var u = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
        var ticks = TimeSpan.FromSeconds(WindowSeconds).Ticks;
        return new DateTime(u.Ticks - (u.Ticks % ticks), DateTimeKind.Utc);
    }

    /// <summary>True when <paramref name="utc"/> is exactly the start of a five-minute window.</summary>
    public static bool IsWindowStart(DateTime utc) => WindowStartFor(utc) == DateTime.SpecifyKind(utc, DateTimeKind.Utc);
}

/// <summary>One call's worth of closed windows, oldest first.</summary>
public sealed class ResourceSummaryBatch
{
    public List<ResourceSummaryWindow> Windows { get; set; } = new();
}

/// <summary>One closed five-minute window on one machine, as measured by one Director.</summary>
public sealed class ResourceSummaryWindow
{
    /// <summary>The window's start, in UTC, on a five-minute boundary. With the Director it is the window's identity:
    /// sending the same window twice stores it once.</summary>
    public DateTime WindowStartUtc { get; set; }

    /// <summary>The window's length in seconds; always <see cref="ResourceSummaryContract.WindowSeconds"/>.</summary>
    public int WindowSeconds { get; set; } = ResourceSummaryContract.WindowSeconds;

    /// <summary>The machine the samples were taken on.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>How many five-second samples the window holds (about 60 when the sampler kept time).</summary>
    public int SampleCount { get; set; }

    /// <summary>The whole machine: average, peak and crossings, for memory and the processor.</summary>
    public ResourceSummaryFigures Machine { get; set; } = new();

    /// <summary>One row per session seen in the window. A session's figures include the child processes it started.
    /// An empty list means no session was seen at a sample whose sessions COULD be measured; whether any could is
    /// <see cref="SamplesWithUnknownSessions"/>, never this list's emptiness.</summary>
    public List<ResourceSessionSummary> Sessions { get; set; } = new();

    /// <summary>
    /// How many of the window's samples could not measure the sessions at all (the process list could not be read).
    /// Zero when every sample measured them. Above zero, the session rows rest on the other samples only, and when it
    /// equals <see cref="SampleCount"/> nothing is known about the sessions in this window: an empty
    /// <see cref="Sessions"/> then means "unknown", not "nothing ran". A client renders this field; it never infers it
    /// from an empty list - an empty list is a plausible number, which is exactly what an unknown must not become.
    /// </summary>
    public int SamplesWithUnknownSessions { get; set; }

    /// <summary>Why the sessions could not be measured, from the newest sample that could not, in plain English.
    /// Null when <see cref="SamplesWithUnknownSessions"/> is zero.</summary>
    public string? SessionsUnreadableReason { get; set; }
}

/// <summary>One session's share of a window.</summary>
public sealed class ResourceSessionSummary
{
    public string SessionId { get; set; } = "";

    /// <summary>The session's Factory value, so the Cockpit can show cost per factory. Null when the session belongs to
    /// no factory. A factory is not a Mission.</summary>
    public string? Factory { get; set; }

    /// <summary>How many of the window's samples saw this session.</summary>
    public int SampleCount { get; set; }

    /// <summary>The most processes in the session's tree whose figure could not be read, at any sample in the window.
    /// Above zero, the session's figures are LOWER BOUNDS: those processes are left out, never counted as zero, so a
    /// client shows "at least".</summary>
    public int ProcessesWithUnknownFigures { get; set; }

    public ResourceSummaryFigures Figures { get; set; } = new();
}

/// <summary>
/// Average, peak and crossings for memory and the processor, over the samples of one window.
///
/// A READING THAT COULD NOT BE TAKEN IS NEVER A ZERO. When no sample in the window could read a resource, its
/// CouldRead is false, its figures are null and its reason says why in plain English. When some samples could read it,
/// the figures are over those samples only and the read count says how many.
/// </summary>
public sealed class ResourceSummaryFigures
{
    public bool ProcessorCouldRead { get; set; }

    /// <summary>Why the processor could not be read, when it could not. Null when it could.</summary>
    public string? ProcessorUnreadableReason { get; set; }

    /// <summary>How many samples read the processor; the weight of this window's average in an hourly roll-up.</summary>
    public int ProcessorSamples { get; set; }

    /// <summary>Average processor use as a percentage of the whole machine (all cores), or null when unread.</summary>
    public double? ProcessorAveragePercent { get; set; }

    /// <summary>The highest five-second processor reading in the window, or null when unread.</summary>
    public double? ProcessorPeakPercent { get; set; }

    public bool MemoryCouldRead { get; set; }

    /// <summary>Why memory could not be read, when it could not. Null when it could.</summary>
    public string? MemoryUnreadableReason { get; set; }

    /// <summary>How many samples read memory.</summary>
    public int MemorySamples { get; set; }

    /// <summary>Average memory in use, in bytes, or null when unread.</summary>
    public long? MemoryAverageUsedBytes { get; set; }

    /// <summary>The most memory in use at any sample, in bytes, or null when unread.</summary>
    public long? MemoryPeakUsedBytes { get; set; }

    /// <summary>The machine's total memory in bytes. Set on the machine row only; null on a session row and when
    /// unread.</summary>
    public long? MemoryTotalBytes { get; set; }

    /// <summary>Every amber or red crossing inside the window, in time order. Empty when nothing crossed.</summary>
    public List<ResourceCrossing> Crossings { get; set; } = new();
}

/// <summary>
/// One run of samples during which a resource sat over a limit. On the machine row it is the machine's own crossing.
/// On a session row it is the machine's crossing, attributed to that session because it was the biggest user of the
/// resource when the crossing peaked - the same session the warning banner names.
/// </summary>
public sealed class ResourceCrossing
{
    /// <summary><see cref="ResourceSummaryContract.Processor"/> or <see cref="ResourceSummaryContract.Memory"/>.</summary>
    public string Resource { get; set; } = "";

    /// <summary>The worst level reached: <see cref="ResourceSummaryContract.Amber"/> or
    /// <see cref="ResourceSummaryContract.Red"/>.</summary>
    public string Level { get; set; } = "";

    /// <summary>The first sample over the limit, in UTC.</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>How long the resource stayed over the limit inside this window, in seconds.</summary>
    public int Seconds { get; set; }

    /// <summary>The highest machine reading during the crossing: a processor percentage, or memory in use as a
    /// percentage of the machine's total. Null when the crossing's samples could not read the figure.</summary>
    public double? PeakPercent { get; set; }
}

/// <summary>The Gateway's answer to one batch: which windows it now holds and which it will never take.</summary>
public sealed class ResourceSummaryAck
{
    /// <summary>Windows the Gateway holds after this call - newly stored or already stored by an earlier send. The
    /// Director drops these from its buffer.</summary>
    public List<DateTime> HeldWindowStartsUtc { get; set; } = new();

    /// <summary>Windows the Gateway refused for good (malformed: not on a five-minute boundary, no machine name, from
    /// the future). Sending them again cannot succeed, so the Director drops these too, and logs them.</summary>
    public List<DateTime> RejectedWindowStartsUtc { get; set; } = new();

    /// <summary>Why the batch was not stored, when it was not (the statistics store is not open, for example). The
    /// Director keeps every window and sends again on the next reseed. Null when the batch was taken.</summary>
    public string? NotStoredReason { get; set; }
}
