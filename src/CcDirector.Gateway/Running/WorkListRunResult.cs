using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Running;

/// <summary>How the runner handled one item while draining a list (issue #274).</summary>
public enum WorkListItemOutcome
{
    /// <summary>A github item: a session was started and reached a terminal signal.</summary>
    Ran,

    /// <summary>A non-github item (devops/jira): skipped per the source-gating rule, never started.</summary>
    SkippedNonGithub,

    /// <summary>A github item whose session could not be started (start error); recorded and skipped.</summary>
    StartFailed,
}

/// <summary>
/// The runner's recorded result for one item it processed while draining a list (issue #274,
/// criterion 2/3). The list itself carries no status (child 2 #273); this is the RUNNER's own
/// record of what happened, kept in memory per run.
/// </summary>
public sealed class WorkListItemResult
{
    /// <summary>The item ref the runner acted on (source + id + area).</summary>
    public required WorkListItemRef Item { get; init; }

    /// <summary>How the runner handled it.</summary>
    public required WorkListItemOutcome Outcome { get; init; }

    /// <summary>The session id started for a github item, or null when none was started.</summary>
    public string? SessionId { get; init; }

    /// <summary>The terminal signal the run ended on (only when <see cref="Outcome"/> is Ran).</summary>
    public ImplLoopSignal? Signal { get; init; }

    /// <summary>A human-readable note (start error, skip reason, or terminal reason).</summary>
    public string Note { get; init; } = "";

    /// <summary>UTC time the runner started acting on this item.</summary>
    public DateTime StartedAtUtc { get; init; }

    /// <summary>UTC time the runner finished with this item.</summary>
    public DateTime FinishedAtUtc { get; init; }
}

/// <summary>The aggregate result of one drain of one list (issue #274).</summary>
public sealed class WorkListRunResult
{
    /// <summary>The list that was drained.</summary>
    public required string ListName { get; init; }

    /// <summary>The consumer token the runner held while draining.</summary>
    public required string ConsumerToken { get; init; }

    /// <summary>Per-item results, in the order the runner processed them.</summary>
    public IReadOnlyList<WorkListItemResult> Items { get; init; } = new List<WorkListItemResult>();

    /// <summary>True once the runner released its consumer claim at the end of the drain.</summary>
    public bool ConsumerReleased { get; init; }
}
