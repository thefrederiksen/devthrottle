using System.Text;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Backends;

/// <summary>
/// Remote backend: the "session" is a handle to a GitHub issue/PR thread watched
/// by the Claude GitHub App. Each turn is a @claude comment that triggers a
/// workflow run on a GitHub-hosted runner. This backend posts comments, polls the
/// run + the action's reply/progress comments, and pumps human-readable text into
/// the terminal buffer so the rest of CC Director (streaming, raw-terminal tab,
/// Wingman) works unchanged.
///
/// Activity state is driven authoritatively from the GitHub run status via
/// <see cref="ActivitySink"/> (not the silence heuristic in TerminalStateDetector,
/// which skips remote sessions). A completed run is a TURN ending, not a session
/// ending: the session only "exits" on an explicit kill.
/// </summary>
public sealed class GitHubActionsBackend : ISessionBackend
{
    private const string LinePrefix = "[github] ";
    private static readonly TimeSpan IdleBackoff = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RunDiscoveryTimeout = TimeSpan.FromSeconds(90);

    private readonly RemoteSessionConfig _config;
    private readonly IGitHubClient _gh;
    private readonly CircularTerminalBuffer _buffer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<long, string> _seenCommentBodies = new();

    private Task? _pollTask;
    private bool _started;
    private bool _disposed;
    private string _status = "Not Started";

    // Thread + run state. Guarded by the poll loop / send lock; long reads are atomic enough.
    private long _threadNumber;
    private long? _currentRunId;
    private DateTimeOffset _lastTriggerUtc;
    private DateTimeOffset? _runDiscoveryDeadline;

    public GitHubActionsBackend(RemoteSessionConfig config, IGitHubClient client, int bufferSizeBytes = 2 * 1024 * 1024)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _gh = client ?? throw new ArgumentNullException(nameof(client));
        _buffer = new CircularTerminalBuffer(bufferSizeBytes);
    }

    /// <summary>
    /// Authoritative activity-state sink, wired by <c>SessionManager.CreateGitHubActionsSession</c>
    /// to the owning session. The poll loop calls this with Working / WaitingForInput as the
    /// GitHub run status changes.
    /// </summary>
    public Action<ActivityState>? ActivitySink { get; set; }

    /// <summary>The "owner/repo" this session runs against.</summary>
    public string RepoSlug => _config.Slug;

    /// <summary>Thread (issue/PR) number once established, or 0.</summary>
    public long ThreadNumber => Volatile.Read(ref _threadNumber);

    /// <summary>Web URL of the thread, or null until established.</summary>
    public string? ThreadUrl { get; private set; }

    /// <summary>Web URL of the most recent run, or null.</summary>
    public string? CurrentRunUrl { get; private set; }

    /// <summary>Last observed run status string (queued/in_progress/completed/...).</summary>
    public string RunStatus { get; private set; } = "none";

    public int ProcessId => 0;
    public string Status => _status;
    public bool IsRunning => _started && !_disposed;
    public bool HasExited => _disposed;
    public CircularTerminalBuffer? Buffer => _buffer;

    public event Action<string>? StatusChanged;
    public event Action<int>? ProcessExited;

    /// <summary>
    /// The generic PTY start signature is meaningless for a remote session. Use
    /// <see cref="StartRemote"/> (called by CreateGitHubActionsSession). Throwing
    /// here makes accidental misuse loud instead of silently doing nothing.
    /// </summary>
    public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null)
        => throw new NotSupportedException("GitHubActionsBackend is started via StartRemote(); see SessionManager.CreateGitHubActionsSession.");

    /// <summary>Begin remote initialization and the poll loop. Non-blocking.</summary>
    public void StartRemote()
    {
        if (_started) throw new InvalidOperationException("Backend already started.");
        _started = true;
        SetStatus("Connecting");
        WriteLine($"Remote session on {_config.Slug} (branch {_config.BaseBranch}). Establishing thread...");
        _pollTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    /// <summary>Keystroke-level writes have no meaning for a remote thread. Use SendTextAsync.</summary>
    public void Write(byte[] data) { /* no-op: remote input is whole comments via SendTextAsync */ }

    /// <summary>Post a follow-up turn as a @claude comment on the thread.</summary>
    public async Task SendTextAsync(string text)
    {
        if (_disposed) return;
        if (string.IsNullOrWhiteSpace(text)) return;

        if (_config.TriggerMode == RemoteTriggerMode.WorkflowDispatch)
        {
            WriteLine("This is a one-shot workflow_dispatch session - follow-ups are not supported. Create a new session for another task.");
            return;
        }

        await _sendLock.WaitAsync(_cts.Token);
        try
        {
            // Establishment may still be in flight on first turn; wait briefly for the thread.
            var waited = TimeSpan.Zero;
            while (Volatile.Read(ref _threadNumber) == 0 && waited < TimeSpan.FromSeconds(30) && !_cts.IsCancellationRequested)
            {
                await Task.Delay(250, _cts.Token);
                waited += TimeSpan.FromMilliseconds(250);
            }

            var thread = Volatile.Read(ref _threadNumber);
            if (thread == 0)
            {
                WriteLine("Thread not established yet - cannot send. Check the errors above.");
                return;
            }

            WriteUser(text);
            await _gh.PostCommentAsync(_config.Owner, _config.Repo, thread, PrefixMention(text), _cts.Token);
            _lastTriggerUtc = DateTimeOffset.UtcNow;
            _currentRunId = null;
            _runDiscoveryDeadline = _lastTriggerUtc + RunDiscoveryTimeout;
            ActivitySink?.Invoke(ActivityState.Working);
            SetStatus("Working");
            WriteLine("Comment posted. Waiting for the runner to pick it up...");
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            WriteLine($"Failed to post comment: {ex.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>No PTY to resize.</summary>
    public void Resize(short cols, short rows) { /* no-op */ }

    /// <summary>Cancel any in-flight run and stop polling. Kill == session exit.</summary>
    public async Task GracefulShutdownAsync(int timeoutMs = 5000)
    {
        if (_disposed) return;
        try { _cts.Cancel(); } catch { /* already cancelling */ }

        var runId = _currentRunId;
        if (runId is { } id)
        {
            try
            {
                await _gh.CancelRunAsync(_config.Owner, _config.Repo, id, CancellationToken.None);
                WriteLine($"Cancelled run {id}.");
            }
            catch (Exception ex)
            {
                WriteLine($"Could not cancel run {id}: {ex.Message}");
            }
        }

        SetStatus("Stopped");
        ProcessExited?.Invoke(0);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            await EstablishThreadAsync(ct);
        }
        catch (OperationCanceledException) { return; }
        catch (GitHubApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            WriteLine(ex.Message);
            SetStatus("Auth failed");
            return;
        }
        catch (Exception ex)
        {
            WriteLine($"Could not establish the GitHub thread: {ex.Message}");
            SetStatus("Failed");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            var active = false;
            try
            {
                active = await PollOnceAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (GitHubApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                WriteLine(ex.Message + " Stopping polling - fix the token and recreate the session.");
                SetStatus("Auth failed");
                break;
            }
            catch (Exception ex)
            {
                WriteLine($"Poll error (will retry): {ex.Message}");
            }

            try { await Task.Delay(active ? _config.PollIntervalMs : (int)IdleBackoff.TotalMilliseconds, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task EstablishThreadAsync(CancellationToken ct)
    {
        if (_config.TriggerMode == RemoteTriggerMode.WorkflowDispatch)
        {
            var workflowFile = _config.WorkflowFile;
            if (workflowFile is null || workflowFile.Trim().Length == 0)
                throw new InvalidOperationException("WorkflowDispatch mode requires a workflow file.");

            _lastTriggerUtc = DateTimeOffset.UtcNow;
            var inputs = new Dictionary<string, string> { ["prompt"] = _config.InitialPrompt };
            await _gh.DispatchWorkflowAsync(_config.Owner, _config.Repo, workflowFile, _config.BaseBranch, inputs, ct);
            // No thread - this is one-shot. _threadNumber stays 0 so comment pumping is skipped.
            WriteUser(_config.InitialPrompt);
            WriteLine($"Dispatched workflow '{_config.WorkflowFile}' on {_config.BaseBranch}. Output lives in the run / resulting PR; follow-ups are not supported in dispatch mode.");
        }
        else if (_config.TriggerMode == RemoteTriggerMode.ExistingThread)
        {
            if (_config.ThreadNumber is not { } existing || existing <= 0)
                throw new InvalidOperationException("ExistingThread mode requires a thread number.");

            Volatile.Write(ref _threadNumber, existing);
            ThreadUrl = $"https://github.com/{_config.Slug}/issues/{existing}";
            _lastTriggerUtc = DateTimeOffset.UtcNow;
            await _gh.PostCommentAsync(_config.Owner, _config.Repo, existing, PrefixMention(_config.InitialPrompt), ct);
            WriteUser(_config.InitialPrompt);
            WriteLine($"Posted to existing thread #{existing}. Waiting for the runner...");
        }
        else
        {
            var configuredTitle = _config.IssueTitle;
            var title = configuredTitle is null || configuredTitle.Trim().Length == 0
                ? DeriveTitle(_config.InitialPrompt)
                : configuredTitle;
            _lastTriggerUtc = DateTimeOffset.UtcNow;
            var issue = await _gh.CreateIssueAsync(_config.Owner, _config.Repo, title, PrefixMention(_config.InitialPrompt), ct);
            Volatile.Write(ref _threadNumber, issue.Number);
            ThreadUrl = issue.HtmlUrl;
            WriteUser(_config.InitialPrompt);
            WriteLine($"Created issue #{issue.Number}: {issue.HtmlUrl}");
            WriteLine("Waiting for the runner to pick it up...");
        }

        _runDiscoveryDeadline = _lastTriggerUtc + RunDiscoveryTimeout;
        _currentRunId = null;
        ActivitySink?.Invoke(ActivityState.Working);
        SetStatus("Working");
    }

    /// <summary>One poll cycle. Returns true if a run is currently active (drives poll cadence).</summary>
    private async Task<bool> PollOnceAsync(CancellationToken ct)
    {
        var thread = Volatile.Read(ref _threadNumber);

        // Pump any new bot comments (reply + sticky progress comment) into the buffer.
        // WorkflowDispatch sessions have no thread (thread == 0); skip comment pumping
        // for them and stream run status only.
        if (thread != 0)
            await PumpNewCommentsAsync(thread, ct);

        // Discover the run our last trigger started, if we don't have it yet.
        if (_currentRunId is null)
        {
            var runs = await _gh.ListRunsAsync(_config.Owner, _config.Repo, eventName: "", _lastTriggerUtc, ct);
            var newest = runs.Count > 0 ? runs[0] : null;
            foreach (var r in runs)
                if (r.CreatedAt > (newest?.CreatedAt ?? DateTimeOffset.MinValue)) newest = r;

            if (newest is not null)
            {
                _currentRunId = newest.Id;
                CurrentRunUrl = newest.HtmlUrl;
                RunStatus = newest.Status;
                _runDiscoveryDeadline = null;
                WriteLine($"Run started: {newest.HtmlUrl} ({newest.Status})");
                ActivitySink?.Invoke(ActivityState.Working);
                return true;
            }

            // No run yet. If we've waited too long, the App/workflow is probably not set up.
            if (_runDiscoveryDeadline is { } deadline && DateTimeOffset.UtcNow > deadline)
            {
                WriteLine("No workflow run appeared. Is the Claude GitHub App installed on " +
                          $"{_config.Slug} and is there a workflow that runs on @claude mentions? " +
                          "See docs/plans/github-actions-backend.md for the required setup.");
                ActivitySink?.Invoke(ActivityState.WaitingForInput);
                SetStatus("No run");
                _runDiscoveryDeadline = null;
            }
            return _runDiscoveryDeadline is not null; // keep fast-polling until the deadline
        }

        // We have a run - check its status.
        var run = await _gh.GetRunAsync(_config.Owner, _config.Repo, _currentRunId.Value, ct);
        CurrentRunUrl = run.HtmlUrl;
        if (!string.Equals(run.Status, RunStatus, StringComparison.Ordinal))
        {
            RunStatus = run.Status;
            WriteLine($"Run {run.Status}.");
        }

        if (run.Status == "completed")
        {
            // Drain the final reply, then hand the turn back.
            if (thread != 0)
                await PumpNewCommentsAsync(thread, ct);
            var conclusion = run.Conclusion ?? "unknown";
            if (conclusion == "success")
            {
                WriteLine("Turn complete. Your move - type a follow-up to continue the thread.");
            }
            else
            {
                WriteLine($"Run finished with conclusion '{conclusion}'. See {run.HtmlUrl}. " +
                          "Type a follow-up to retry or adjust.");
            }
            _currentRunId = null;
            ActivitySink?.Invoke(ActivityState.WaitingForInput);
            SetStatus("Waiting for you");
            return false;
        }

        ActivitySink?.Invoke(ActivityState.Working);
        return true;
    }

    private async Task PumpNewCommentsAsync(long thread, CancellationToken ct)
    {
        // Look at comments from the current turn onward. The action edits a sticky
        // progress comment in place, so we re-print the delta when its body grows.
        var since = _lastTriggerUtc - TimeSpan.FromSeconds(5);
        var comments = await _gh.ListCommentsAsync(_config.Owner, _config.Repo, thread, since, ct);
        foreach (var c in comments)
        {
            if (!IsBotAuthor(c.AuthorLogin)) continue;

            if (_seenCommentBodies.TryGetValue(c.Id, out var prev))
            {
                if (string.Equals(prev, c.Body, StringComparison.Ordinal)) continue;
                // Body changed - print only the new tail when it's an append, else the whole body.
                var delta = c.Body.StartsWith(prev, StringComparison.Ordinal) ? c.Body[prev.Length..] : c.Body;
                _seenCommentBodies[c.Id] = c.Body;
                WriteBlock(delta);
            }
            else
            {
                _seenCommentBodies[c.Id] = c.Body;
                WriteBlock(c.Body);
            }
        }
    }

    private static bool IsBotAuthor(string login)
        => login.Contains("claude", StringComparison.OrdinalIgnoreCase)
        || login.Contains("github-actions", StringComparison.OrdinalIgnoreCase)
        || login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase);

    private static string PrefixMention(string text)
        => text.Contains("@claude", StringComparison.OrdinalIgnoreCase) ? text : "@claude " + text;

    private static string DeriveTitle(string prompt)
    {
        var firstLine = prompt.Replace("\r", " ").Replace("\n", " ").Trim();
        if (firstLine.Length > 72) firstLine = firstLine[..72].TrimEnd() + "...";
        return string.IsNullOrEmpty(firstLine) ? "Director remote session" : firstLine;
    }

    private void WriteUser(string text) => WriteRaw($"\r\n> {text.Trim()}\r\n");

    private void WriteLine(string text) => WriteRaw($"{LinePrefix}{text}\r\n");

    private void WriteBlock(string body)
    {
        var trimmed = body.Replace("\r\n", "\n").TrimEnd('\n');
        if (trimmed.Length == 0) return;
        WriteRaw("\r\n" + trimmed.Replace("\n", "\r\n") + "\r\n");
    }

    private void WriteRaw(string text)
    {
        if (_disposed) return;
        try { _buffer.Write(Encoding.UTF8.GetBytes(text)); }
        catch (Exception ex) { FileLog.Write($"[GitHubActionsBackend] buffer write failed: {ex.Message}"); }
    }

    private void SetStatus(string status)
    {
        _status = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { /* best effort */ }
        try { _pollTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        _cts.Dispose();
        _sendLock.Dispose();
        _buffer.Dispose();
    }
}
