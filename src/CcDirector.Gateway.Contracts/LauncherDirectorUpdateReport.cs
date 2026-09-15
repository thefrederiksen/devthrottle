namespace CcDirector.Gateway.Contracts;

/// <summary>
/// What a launcher reports about its Director's update, in answer to the launcher commands director/update and
/// director/update-status (fleet maintenance, devthrottle_internal#2021 and #2022).
///
/// FACTS ONLY, NO SENTENCES. The Gateway turns these into the words a person reads (DirectorUpdateViewFold), so
/// the wording lives in one place and a newer launcher cannot put a sentence on a screen the Gateway never saw.
/// </summary>
public sealed class LauncherDirectorUpdateReport
{
    /// <summary>True when this answer carries the decision of an update pass that ran to completion.</summary>
    public bool Finished { get; set; }

    /// <summary>The finished pass's decision, by name (one of <see cref="KnownDecisions"/>). Null when the pass is
    /// still running, or when no pass was run.</summary>
    public string? Decision { get; set; }

    /// <summary>True when an update pass was already running, so the command started nothing new.</summary>
    public bool AlreadyRunning { get; set; }

    /// <summary>True while an update pass is running on this launcher.</summary>
    public bool PassInProgress { get; set; }

    /// <summary>The version a still-running pass is installing, when it is known.</summary>
    public string? InstallingVersion { get; set; }

    /// <summary>A newer Director build downloaded on this machine and waiting to be installed, or null.</summary>
    public string? StagedVersion { get; set; }

    /// <summary>The last update decision the launcher recorded, by name, or null when it never recorded one.</summary>
    public string? LastDecision { get; set; }

    public DateTimeOffset? LastDecisionAt { get; set; }

    /// <summary>The version that last recorded decision was about.</summary>
    public string? LastVersion { get; set; }

    /// <summary>The launcher's own detail for that decision, as it recorded it.</summary>
    public string? LastDetail { get; set; }

    /// <summary>
    /// Every decision name a launcher may send. The launcher's tests pin its decision enumeration to this list and
    /// the Gateway's tests pin a sentence to every entry, so a new decision cannot reach a screen without words.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownDecisions = new[]
    {
        "NothingStaged",
        "HeldBecauseBusy",
        "HeldBecauseUnknown",
        "HeldBecauseAnotherSwapIsRunning",
        "HeldBecauseDirectorNotRunning",
        "HeldBecauseNoDisplay",
        "SkippedPinnedBadVersion",
        "Applied",
        "RolledBack",
        "Failed",
    };
}
