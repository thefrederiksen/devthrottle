namespace CcDirector.Core.Backends;

/// <summary>
/// Minimal GitHub REST surface the <see cref="GitHubActionsBackend"/> needs.
/// One interface so the backend can be unit-tested against a scripted stub
/// without touching the network.
/// </summary>
public interface IGitHubClient
{
    /// <summary>Create a new issue. Returns the created issue (number + html url).</summary>
    Task<GhIssue> CreateIssueAsync(string owner, string repo, string title, string body, CancellationToken ct);

    /// <summary>Post a comment on an issue/PR thread. Returns the created comment.</summary>
    Task<GhComment> PostCommentAsync(string owner, string repo, long issueNumber, string body, CancellationToken ct);

    /// <summary>
    /// List comments on a thread, optionally only those created strictly after
    /// <paramref name="sinceUtc"/>. Used to pick up the action's reply/progress comment.
    /// </summary>
    Task<IReadOnlyList<GhComment>> ListCommentsAsync(string owner, string repo, long issueNumber, DateTimeOffset? sinceUtc, CancellationToken ct);

    /// <summary>
    /// List recent workflow runs for the repo filtered by triggering event,
    /// most-recent first. Used to correlate a posted comment to the run it triggered.
    /// </summary>
    Task<IReadOnlyList<GhRun>> ListRunsAsync(string owner, string repo, string eventName, DateTimeOffset createdAfterUtc, CancellationToken ct);

    /// <summary>Get a single workflow run by id (current status/conclusion).</summary>
    Task<GhRun> GetRunAsync(string owner, string repo, long runId, CancellationToken ct);

    /// <summary>Cancel an in-flight workflow run.</summary>
    Task CancelRunAsync(string owner, string repo, long runId, CancellationToken ct);

    /// <summary>
    /// Trigger a workflow_dispatch run. <paramref name="workflowFile"/> is the
    /// workflow file name or numeric id; <paramref name="gitRef"/> the branch/tag;
    /// <paramref name="inputs"/> the workflow inputs. Returns nothing - the run is
    /// discovered afterward via <see cref="ListRunsAsync"/> (event "workflow_dispatch").
    /// </summary>
    Task DispatchWorkflowAsync(string owner, string repo, string workflowFile, string gitRef, IReadOnlyDictionary<string, string> inputs, CancellationToken ct);
}

/// <summary>A created/looked-up GitHub issue.</summary>
public sealed record GhIssue(long Number, string HtmlUrl);

/// <summary>A GitHub issue/PR comment.</summary>
public sealed record GhComment(long Id, string Body, string HtmlUrl, DateTimeOffset CreatedAt, string AuthorLogin);

/// <summary>
/// A GitHub Actions workflow run. <see cref="Status"/> is one of GitHub's run
/// statuses (queued, in_progress, completed, ...); <see cref="Conclusion"/> is
/// only set when Status == completed (success, failure, cancelled, timed_out, ...).
/// </summary>
public sealed record GhRun(long Id, string Status, string? Conclusion, string HtmlUrl, DateTimeOffset CreatedAt, string DisplayTitle);
