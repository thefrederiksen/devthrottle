namespace CcDirector.Gateway.Contracts;

/// <summary>The tone a fleet label is shown in. A client maps each one to a colour and to nothing else.</summary>
public static class FleetTone
{
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Bad = "bad";
    public const string Idle = "idle";
}

/// <summary>
/// GET /machines - every machine this account owns, its launcher, the Directors on it, how each version
/// compares to the newest release, and what can be done to it from here (fleet maintenance,
/// devthrottle_internal#2026). Folded once on the Gateway by FleetMachinesFold; a client renders it as sent.
/// </summary>
public sealed class FleetMachinesDto
{
    public NewestReleaseDto NewestRelease { get; set; } = new();

    /// <summary>The one-line facts shown above the table, most important first.</summary>
    public List<FleetHighlightDto> Highlights { get; set; } = new();

    public List<FleetMachineDto> Machines { get; set; } = new();
}

/// <summary>What the Gateway knows about the newest published release.</summary>
public sealed class NewestReleaseDto
{
    /// <summary>The newest release version, or null while it is not known.</summary>
    public string? Version { get; set; }

    /// <summary>The words to show where the version goes: the version itself, "checking" or "unknown".</summary>
    public string Label { get; set; } = "";

    /// <summary>Why the version is missing or may be old, when it is. Null when there is nothing to say.</summary>
    public string? Detail { get; set; }

    /// <summary>When the Gateway last finished reading it.</summary>
    public DateTime? CheckedAtUtc { get; set; }
}

public sealed class FleetHighlightDto
{
    public string Text { get; set; } = "";
    public string Tone { get; set; } = FleetTone.Idle;
}

/// <summary>One machine: its launcher, what the launcher can be asked to do, and the Directors on it.</summary>
public sealed class FleetMachineDto
{
    public string Machine { get; set; } = "";

    /// <summary>The launcher's version, or null when no launcher is registered for this machine.</summary>
    public string? LauncherVersion { get; set; }

    /// <summary>When the launcher last registered or heartbeated, or null when there is no launcher.</summary>
    public DateTime? LauncherLastSeenUtc { get; set; }

    public LauncherReach Reach { get; set; }

    /// <summary>The launcher's state in a few words, e.g. "Launcher connected", "Too old for commands".</summary>
    public string ReachLabel { get; set; } = "";

    public string ReachTone { get; set; } = FleetTone.Idle;

    /// <summary>What that state means and what to do about it, or null when it needs no explanation.</summary>
    public string? ReachDetail { get; set; }

    /// <summary>The launcher's version compared to the newest release.</summary>
    public FleetVersionDto LauncherVersionState { get; set; } = new();

    /// <summary>True when the launcher can be asked for its Director update status (it is connected and declares
    /// director/update-status). A client asks only then, so an older launcher is never sent the question.</summary>
    public bool CanReportUpdateStatus { get; set; }

    /// <summary>Install a downloaded Director update now (the launcher command director/update).</summary>
    public FleetActionDto Update { get; set; } = new();

    /// <summary>Restart the installed Director, only if it holds no sessions.</summary>
    public FleetActionDto Restart { get; set; } = new();

    /// <summary>Start the installed Director.</summary>
    public FleetActionDto Start { get; set; } = new();

    public List<FleetDirectorDto> Directors { get; set; } = new();
}

public sealed class FleetDirectorDto
{
    public string DirectorId { get; set; } = "";

    /// <summary>The name to show: the display name when there is one, otherwise the machine name.</summary>
    public string Name { get; set; } = "";

    public string? Version { get; set; }

    /// <summary>Sessions this Gateway holds for the Director right now.</summary>
    public int Sessions { get; set; }

    /// <summary>"Running", "Stopped" or "Not heard from".</summary>
    public string StateLabel { get; set; } = "";

    public string StateTone { get; set; } = FleetTone.Idle;

    public FleetVersionDto VersionState { get; set; } = new();
}

/// <summary>A version compared to the newest release.</summary>
public sealed class FleetVersionDto
{
    /// <summary>"current", "behind", "newer than release", "unreadable version", or empty when there is nothing
    /// to compare against.</summary>
    public string Label { get; set; } = "";

    public string Tone { get; set; } = FleetTone.Idle;

    public bool Behind { get; set; }
}

/// <summary>One thing that can be done to a machine, whether it can be done now, and why not.</summary>
public sealed class FleetActionDto
{
    public bool Offered { get; set; }

    /// <summary>The button text, or the state in words when nothing is offered ("Up to date").</summary>
    public string Label { get; set; } = "";

    /// <summary>What pressing it will do when offered, or why it is not offered. Null when there is nothing to
    /// say, and the client then shows nothing for this action.</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// The answer to POST /machines/{machine}/director/update and GET /machines/{machine}/director/update-status,
/// folded from what the launcher reported (DirectorUpdateViewFold).
/// </summary>
public sealed class DirectorUpdateViewDto
{
    public string Machine { get; set; } = "";

    /// <summary>One sentence saying what happened or is happening.</summary>
    public string Headline { get; set; } = "";

    public string Tone { get; set; } = FleetTone.Idle;

    /// <summary>True while an update is being installed on that machine.</summary>
    public bool InProgress { get; set; }

    /// <summary>Whether a newer build is downloaded and waiting, in words.</summary>
    public string Downloaded { get; set; } = "";

    /// <summary>When the launcher last recorded an update result, or null when it never has.</summary>
    public DateTimeOffset? LastResultAt { get; set; }
}
