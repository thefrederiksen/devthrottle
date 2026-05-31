namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Request body for POST /sessions/github - create a GitHub Actions remote session
/// whose work runs on a GitHub-hosted runner rather than the local machine.
/// </summary>
public sealed class GitHubSessionRequest
{
    /// <summary>Repository owner / organization, e.g. "thefrederiksen".</summary>
    public string Owner { get; set; } = "";

    /// <summary>Repository name, e.g. "cc-director".</summary>
    public string Repo { get; set; } = "";

    /// <summary>Base branch the runner works against. Defaults to "main".</summary>
    public string BaseBranch { get; set; } = "main";

    /// <summary>"NewIssue" (default), "ExistingThread", or "WorkflowDispatch".</summary>
    public string TriggerMode { get; set; } = "NewIssue";

    /// <summary>The first task to send to @claude. Required.</summary>
    public string InitialPrompt { get; set; } = "";

    /// <summary>Existing issue/PR number, required when TriggerMode is "ExistingThread".</summary>
    public long? ThreadNumber { get; set; }

    /// <summary>Optional title for the created issue (NewIssue mode).</summary>
    public string? IssueTitle { get; set; }

    /// <summary>Workflow file name or id, required when TriggerMode is "WorkflowDispatch".</summary>
    public string? WorkflowFile { get; set; }
}
