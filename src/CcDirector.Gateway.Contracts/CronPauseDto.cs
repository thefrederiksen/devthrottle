namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The account's pause-everything switch (issue #3910), as <c>GET /cron/pause</c> and <c>PUT /cron/pause</c> answer
/// it. While <see cref="Paused"/> is true the scheduler starts nothing; no schedule's own enabled flag is changed.
/// The words are the Gateway's, so the Schedule page and the Factories screen say the same thing.
/// </summary>
public sealed class CronPauseDto
{
    /// <summary>True while the scheduler is paused for this account.</summary>
    public bool Paused { get; set; }

    /// <summary>When the pause began (UTC). Null when not paused.</summary>
    public DateTime? SinceUtc { get; set; }

    /// <summary>The banner to show while paused. Null when not paused.</summary>
    public string? Banner { get; set; }

    /// <summary>The label of the one control: what a tap does now ("Pause everything" or "Resume everything").</summary>
    public string ActionLabel { get; set; } = "";

    /// <summary>The control's tooltip.</summary>
    public string ActionTitle { get; set; } = "";
}

/// <summary>The body of <c>PUT /cron/pause</c>.</summary>
public sealed class CronPauseRequest
{
    /// <summary>True to pause everything, false to resume. Required: a body without it is refused.</summary>
    public bool? Paused { get; set; }
}
