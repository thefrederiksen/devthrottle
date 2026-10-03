namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// NO LONGER WRITTEN OR READ (October 2026): the AI day paragraphs were removed because nobody read them.
/// The table stays so no schema change is needed, and its remaining rows age out with the 90-day retention
/// in <see cref="History.SessionHistoryStore.PurgeOlderThan"/>. Dropping it is a separate, owner-approved
/// migration. What follows is the original description.
///
/// One repository group's one-day roll-up paragraph, in the <c>session_history_rollups</c> table
/// (issue #2194). The History page shows a written summary per repository per day; producing that
/// with a model on every page load would be a real recurring spend, so the roll-up is computed ONCE
/// in the background history sweep and CACHED here. A past day whose sessions have all ended never
/// changes, so its cached row is effectively permanent; <see cref="InputHash"/> detects the cases
/// that do change (today's sessions still running, a late-arriving session summary) and marks the
/// row stale for recomputation.
///
/// Derived from prompt-derived session summaries, so it is customer content: tenant-scoped by the
/// global query filter like everything else in this database. Retention matches session_history
/// (90 days), pruned by the same sweep.
/// </summary>
public sealed class SessionHistoryRollupEntity : TenantScopedEntity
{
    /// <summary>The grouping key: owner/repo when the origin remote is known, else the repository
    /// path. Caller-supplied (derived from pushed session facts), so the key is composite with the
    /// tenant - the session_spend reasoning.</summary>
    public string RepoKey { get; set; } = "";

    /// <summary>The UTC day this roll-up covers (date component only).</summary>
    public DateTime DayUtc { get; set; }

    /// <summary>The written paragraph. Null while pending or after giving up (see Attempts).</summary>
    public string? SummaryText { get; set; }

    /// <summary>Hash of the inputs (session ids, their summary state, their endings) the paragraph
    /// was computed from. A mismatch against the current inputs means the row is stale.</summary>
    public string InputHash { get; set; } = "";

    /// <summary>Bounded retry counter, reset whenever the inputs change.</summary>
    public int Attempts { get; set; }

    public DateTime ComputedAtUtc { get; set; }
}
