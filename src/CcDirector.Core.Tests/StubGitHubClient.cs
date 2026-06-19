using CcDirector.Core.Backends;

namespace CcDirector.Core.Tests;

/// <summary>
/// Scripted in-memory <see cref="IGitHubClient"/> for testing
/// <see cref="GitHubActionsBackend"/> without touching the network. The run-status
/// progression is driven by <see cref="RunStatusScript"/>: each GetRun call advances
/// to the next status (the last entry repeats).
/// </summary>
internal sealed class StubGitHubClient : IGitHubClient
{
    private readonly object _lock = new();

    public long IssueNumber { get; set; } = 42;
    public string? CreatedIssueTitle { get; private set; }
    public string? CreatedIssueBody { get; private set; }
    public int CreateIssueCalls { get; private set; }

    public List<(long Thread, string Body)> PostedComments { get; } = new();
    public List<GhComment> BotComments { get; set; } = new();

    /// <summary>Status returned by successive GetRun calls. Last value repeats.</summary>
    public Queue<string> RunStatusScript { get; } = new();
    public string Conclusion { get; set; } = "success";
    public bool ReturnRunOnDiscovery { get; set; } = true;
    public long RunId { get; set; } = 1000;
    public long? CancelledRunId { get; private set; }

    public Task<GhIssue> CreateIssueAsync(string owner, string repo, string title, string body, CancellationToken ct)
    {
        lock (_lock)
        {
            CreateIssueCalls++;
            CreatedIssueTitle = title;
            CreatedIssueBody = body;
        }
        return Task.FromResult(new GhIssue(IssueNumber, $"https://github.com/{owner}/{repo}/issues/{IssueNumber}"));
    }

    public List<(string Branch, string Path, int Bytes, string Message)> Uploads { get; } = new();
    public string UploadDownloadUrl { get; set; } =
        "https://raw.githubusercontent.com/thefrederiksen/devthrottle/feedback-assets/feedback/screenshots/test.png";

    public Task<string> UploadFileAsync(string owner, string repo, string branch, string path, byte[] content, string commitMessage, CancellationToken ct)
    {
        lock (_lock) Uploads.Add((branch, path, content.Length, commitMessage));
        return Task.FromResult(UploadDownloadUrl);
    }

    public Task<GhComment> PostCommentAsync(string owner, string repo, long issueNumber, string body, CancellationToken ct)
    {
        lock (_lock) PostedComments.Add((issueNumber, body));
        return Task.FromResult(new GhComment(9000 + PostedComments.Count, body,
            $"https://github.com/{owner}/{repo}/issues/{issueNumber}#c", DateTimeOffset.UtcNow, "tester"));
    }

    public Task<IReadOnlyList<GhComment>> ListCommentsAsync(string owner, string repo, long issueNumber, DateTimeOffset? sinceUtc, CancellationToken ct)
    {
        lock (_lock) return Task.FromResult<IReadOnlyList<GhComment>>(BotComments.ToList());
    }

    public Task<IReadOnlyList<GhRun>> ListRunsAsync(string owner, string repo, string eventName, DateTimeOffset createdAfterUtc, CancellationToken ct)
    {
        if (!ReturnRunOnDiscovery)
            return Task.FromResult<IReadOnlyList<GhRun>>(Array.Empty<GhRun>());

        var run = new GhRun(RunId, PeekStatus(), null,
            $"https://github.com/{owner}/{repo}/actions/runs/{RunId}", DateTimeOffset.UtcNow, "run");
        return Task.FromResult<IReadOnlyList<GhRun>>(new[] { run });
    }

    public Task<GhRun> GetRunAsync(string owner, string repo, long runId, CancellationToken ct)
    {
        var status = NextStatus();
        var conclusion = status == "completed" ? Conclusion : null;
        return Task.FromResult(new GhRun(runId, status, conclusion,
            $"https://github.com/{owner}/{repo}/actions/runs/{runId}", DateTimeOffset.UtcNow, "run"));
    }

    public Task CancelRunAsync(string owner, string repo, long runId, CancellationToken ct)
    {
        lock (_lock) CancelledRunId = runId;
        return Task.CompletedTask;
    }

    public string? DispatchedWorkflow { get; private set; }
    public IReadOnlyDictionary<string, string>? DispatchedInputs { get; private set; }

    public Task DispatchWorkflowAsync(string owner, string repo, string workflowFile, string gitRef, IReadOnlyDictionary<string, string> inputs, CancellationToken ct)
    {
        lock (_lock)
        {
            DispatchedWorkflow = workflowFile;
            DispatchedInputs = inputs;
        }
        return Task.CompletedTask;
    }

    private string PeekStatus()
    {
        lock (_lock) return RunStatusScript.Count > 0 ? RunStatusScript.Peek() : "queued";
    }

    private string NextStatus()
    {
        lock (_lock)
        {
            if (RunStatusScript.Count == 0) return "completed";
            if (RunStatusScript.Count == 1) return RunStatusScript.Peek();
            return RunStatusScript.Dequeue();
        }
    }
}
