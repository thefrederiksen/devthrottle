using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Running;

/// <summary>
/// The per-source adapter contract for the queue runner (issue #300, child 4 of epic #297). One
/// adapter exists per RUNNABLE work-item source; a source with no adapter (jira in v1) is skipped
/// with a note and left in the list, exactly like the old single "github" gate did for everything
/// non-github.
///
/// The adapter is deliberately THIN (decision D-2 on #300): the Gateway runner does NOT perform
/// tracker operations itself - the seeded Claude session does (gh for github, az boards for
/// devops). The adapter therefore owns only the three per-source concerns the runner has:
///
///   1. Being present at all (runnability - dispatch is "an adapter exists for this source").
///   2. Building the seed prompt that starts the implementation-loop in that source's mode.
///   3. Producing the terminal-sentinel correlation key for an item (the IMPL-LOOP-TERMINAL
///      block's <c>issue:</c> field is source-agnostic; the key is the numeric item id).
///
/// Status write-back (claim / done / needs-human / failed) is the seeded session's job, driven by
/// the source mode of the implementation-loop skill - see DEVELOPMENT_METHOD.md Section 7b for the
/// full adapter contract including the devops status mapping. A future jira adapter implements this
/// same interface plus a jira mode in the skill.
/// </summary>
public interface ISourceAdapter
{
    /// <summary>The source name this adapter serves (matches <see cref="WorkListItemRef.Source"/>, case-insensitive).</summary>
    string Source { get; }

    /// <summary>
    /// Build the seed prompt (the new session's PrePrompt) that drives the implementation-loop for
    /// this item in this source's mode. The loop ends by printing the IMPL-LOOP-TERMINAL sentinel
    /// whose <c>issue:</c> field equals the item's correlation key.
    /// </summary>
    string BuildSeedPrompt(WorkListItemRef item);

    /// <summary>
    /// Produce the sentinel correlation key for the item - the integer the IMPL-LOOP-TERMINAL
    /// block's <c>issue:</c> field must carry for this item's run. Returns false when the item's id
    /// cannot produce one (e.g. a non-numeric id); the runner then records the item as
    /// start-failed rather than watching a sentinel it can never match.
    /// </summary>
    bool TryGetCorrelationKey(WorkListItemRef item, out int key);
}
