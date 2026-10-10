namespace CcDirector.Gateway.Contracts;

/// <summary>
/// How busy each machine's schedules keep it over the next 24 hours (the owner, 2026-10-09): one bar per hour, the
/// most scheduled sessions open at once in that hour, against the number a machine is meant to carry. Folded on the
/// Gateway from every active schedule's next fires and its own measured run length, so the Schedule page and
/// <c>cc-devthrottle schedule load</c> show the same answer and neither works one out.
///
///   GET /cron/load -> CronLoadDto
/// </summary>
public sealed class CronLoadDto
{
    /// <summary>When the forecast was made.</summary>
    public DateTime GeneratedUtc { get; set; }

    /// <summary>How many scheduled sessions one machine is meant to have open at once. An hour above it is over.</summary>
    public int Capacity { get; set; }

    /// <summary>One entry per machine that has an active schedule, busiest first.</summary>
    public List<CronMachineLoadDto> Machines { get; set; } = new();
}

/// <summary>One machine's next 24 hours.</summary>
public sealed class CronMachineLoadDto
{
    /// <summary>The machine the schedules target.</summary>
    public string Machine { get; set; } = "";

    /// <summary>The time zone the hour labels are in: the one most of this machine's schedules use.</summary>
    public string TimeZoneId { get; set; } = "";

    /// <summary>How many active schedules target this machine.</summary>
    public int Schedules { get; set; }

    /// <summary>The highest <see cref="CronLoadHourDto.Concurrent"/> across the 24 hours.</summary>
    public int Peak { get; set; }

    /// <summary>How many of the 24 hours are above capacity.</summary>
    public int HoursOver { get; set; }

    /// <summary>The hour with the fewest scheduled sessions open (ties go to the fewest starts, then the earliest).</summary>
    public CronLoadHourDto Quietest { get; set; } = new();

    /// <summary>One sentence for the strip's caption, for example "peak 7 at 07:00 - 2 hours over 6 - quietest 13:00".</summary>
    public string Summary { get; set; } = "";

    /// <summary>The 24 hours, starting with the current one.</summary>
    public List<CronLoadHourDto> Hours { get; set; } = new();

    /// <summary>
    /// The schedules whose run length is a guess because none of their runs has ended yet, so the bars that hold them
    /// are estimates. Empty when every length was measured.
    /// </summary>
    public List<string> EstimatedJobIds { get; set; } = new();

    /// <summary>
    /// The sentence that says the bars are partly a guess, for example "2 schedules have never finished a run, so they
    /// are counted at 30 min each", or empty when every length was measured.
    /// </summary>
    public string EstimateNote { get; set; } = "";

    /// <summary>The schedules left out because their time zone is not known on this host.</summary>
    public List<string> UnplacedJobIds { get; set; } = new();

    /// <summary>The sentence that says some schedules are left out, or empty when none are.</summary>
    public string UnplacedNote { get; set; } = "";
}

/// <summary>One hour on one machine.</summary>
public sealed class CronLoadHourDto
{
    /// <summary>The hour's start.</summary>
    public DateTime StartUtc { get; set; }

    /// <summary>The hour's start on the machine's clock, "07:00".</summary>
    public string Label { get; set; } = "";

    /// <summary>The most scheduled sessions open at the same moment during the hour.</summary>
    public int Concurrent { get; set; }

    /// <summary>
    /// The most FACTORY scheduled sessions open at once during the hour - the part of the bar drawn in the factory
    /// colour. Never more than <see cref="Concurrent"/>; the rest of the bar is the owner's own jobs.
    /// </summary>
    public int FactoryConcurrent { get; set; }

    /// <summary>How many scheduled runs start during the hour.</summary>
    public int Starts { get; set; }

    /// <summary>Whether <see cref="Concurrent"/> is above the capacity.</summary>
    public bool Over { get; set; }

    /// <summary>The schedules with a session open at some point during the hour - what the page filters to when the bar is tapped.</summary>
    public List<string> JobIds { get; set; } = new();
}
