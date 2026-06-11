using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Running;

/// <summary>
/// The runner's adapter for <c>source = github</c> items (issue #300). Seed prompt is the original
/// v1 form (<c>/implementation-loop &lt;issue#&gt;</c>) so the github path is byte-identical to the
/// pre-adapter runner; the correlation key is the numeric GitHub issue number.
/// </summary>
public sealed class GithubSourceAdapter : ISourceAdapter
{
    public string Source => "github";

    public string BuildSeedPrompt(WorkListItemRef item) => $"/implementation-loop {item.Id}";

    public bool TryGetCorrelationKey(WorkListItemRef item, out int key) =>
        int.TryParse(item.Id, out key);
}

/// <summary>
/// The runner's adapter for <c>source = devops</c> items (issue #300) - Azure DevOps work items.
/// Seed prompt invokes the implementation-loop in its devops mode
/// (<c>/implementation-loop --source devops &lt;workItemId&gt;</c>); the seeded session performs
/// claim and write-back against Azure DevOps via the az boards CLI (decision D-3 on #300 - state
/// transitions + discussion comments), while all engineering mechanics (branch, PR, merge, proof)
/// stay in the GitHub code repo the work item's description names. The correlation key is the
/// numeric work item id (Azure DevOps ids are always integers), so the source-agnostic
/// IMPL-LOOP-TERMINAL contract (<c>issue: &lt;id&gt;</c>) needs no change.
/// </summary>
public sealed class DevopsSourceAdapter : ISourceAdapter
{
    public string Source => "devops";

    public string BuildSeedPrompt(WorkListItemRef item) => $"/implementation-loop --source devops {item.Id}";

    public bool TryGetCorrelationKey(WorkListItemRef item, out int key) =>
        int.TryParse(item.Id, out key);
}

/// <summary>
/// The runner's per-source adapter registry (issue #300). Dispatch IS runnability: a source with a
/// registered adapter is runnable, a source without one (jira in v1) is skipped with a note and
/// left in the list. Adding a future source = registering its adapter here plus adding the matching
/// mode to the implementation-loop skill (DEVELOPMENT_METHOD.md Section 7b).
/// </summary>
public static class SourceAdapters
{
    private static readonly Dictionary<string, ISourceAdapter> BySource =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["github"] = new GithubSourceAdapter(),
            ["devops"] = new DevopsSourceAdapter(),
        };

    /// <summary>The adapter for the source, or null when the source is not runnable (no adapter).</summary>
    public static ISourceAdapter? TryGet(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;
        return BySource.TryGetValue(source, out var adapter) ? adapter : null;
    }

    /// <summary>The runnable source names, for skip notes and diagnostics (stable order).</summary>
    public static string RunnableSourceNames => "github, devops";
}
