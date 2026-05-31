namespace CcDirector.Core.Backends;

/// <summary>
/// How a GitHub Actions remote session is triggered.
/// </summary>
public enum RemoteTriggerMode
{
    /// <summary>Create a fresh issue and drive it with @claude comments (default).</summary>
    NewIssue,

    /// <summary>Attach to an existing issue or PR by number and drive it with @claude comments.</summary>
    ExistingThread,

    /// <summary>
    /// Fire a one-shot workflow_dispatch run with the prompt as a "prompt" input. No
    /// thread, so follow-ups are not supported; output lives in the run / resulting PR.
    /// </summary>
    WorkflowDispatch
}

/// <summary>
/// Configuration for a GitHub Actions remote session. Carries everything the
/// <see cref="GitHubActionsBackend"/> needs to create or attach to a thread and
/// drive it. This deliberately does NOT reuse the generic
/// (executable, args, workingDir, cols, rows) backend start signature - those
/// parameters are meaningless for a remote session and forcing config through
/// them would hide intent.
/// </summary>
public sealed class RemoteSessionConfig
{
    /// <summary>Repository owner / organization, e.g. "thefrederiksen".</summary>
    public required string Owner { get; init; }

    /// <summary>Repository name, e.g. "cc-director".</summary>
    public required string Repo { get; init; }

    /// <summary>Base branch the runner works against, e.g. "main".</summary>
    public string BaseBranch { get; init; } = "main";

    /// <summary>How the thread is established.</summary>
    public RemoteTriggerMode TriggerMode { get; init; } = RemoteTriggerMode.NewIssue;

    /// <summary>
    /// The first task to send. For <see cref="RemoteTriggerMode.NewIssue"/> this
    /// becomes the issue body (prefixed with @claude); for
    /// <see cref="RemoteTriggerMode.ExistingThread"/> it becomes the first comment.
    /// </summary>
    public required string InitialPrompt { get; init; }

    /// <summary>
    /// Existing issue/PR number to attach to. Required when
    /// <see cref="TriggerMode"/> is <see cref="RemoteTriggerMode.ExistingThread"/>;
    /// ignored otherwise.
    /// </summary>
    public long? ThreadNumber { get; init; }

    /// <summary>Title for the created issue (NewIssue mode only). Falls back to a derived title.</summary>
    public string? IssueTitle { get; init; }

    /// <summary>
    /// Workflow file name (e.g. "claude-dispatch.yml") or numeric id. Required for
    /// <see cref="RemoteTriggerMode.WorkflowDispatch"/>; ignored otherwise.
    /// </summary>
    public string? WorkflowFile { get; init; }

    /// <summary>Poll cadence while a run is active. Backs off automatically when idle.</summary>
    public int PollIntervalMs { get; init; } = 4000;

    /// <summary>"owner/repo" convenience string.</summary>
    public string Slug => $"{Owner}/{Repo}";
}
