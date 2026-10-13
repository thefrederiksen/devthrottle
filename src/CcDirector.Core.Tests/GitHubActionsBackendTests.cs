using System.Text;
using CcDirector.Core.Backends;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Unit tests for the GitHub Actions remote backend. They drive the backend with a
/// scripted <see cref="StubGitHubClient"/> (no network) and assert the
/// run-status -> ActivityState mapping, comment posting, buffer pumping, and cancel.
///
/// NO TEST HERE WAITS ON A CLOCK. The backend polls on a background task, and these tests used to sleep in a loop
/// and give up after four seconds. Under a loaded machine the loop did not get its turn in time: on 12 October 2026
/// a release gate went red on a wait that returned false after 8.6 seconds, on correct code. Each test now hands the
/// backend a <see cref="PollPacer"/> as its wait between polls, so the test decides when the next poll runs and
/// reads the backend only while the loop is standing still. A wait is bounded by a number of polls, never by time.
/// </summary>
public sealed class GitHubActionsBackendTests
{
    private static RemoteSessionConfig NewIssueConfig(string prompt = "do the thing") => new()
    {
        Owner = "acme",
        Repo = "widget",
        BaseBranch = "main",
        TriggerMode = RemoteTriggerMode.NewIssue,
        InitialPrompt = prompt,
        PollIntervalMs = 25,
    };

    /// <summary>
    /// The backend's wait between polls, held by the test. The poll loop stops here after every poll; the test lets
    /// it go round again with <see cref="PollUntilAsync"/> or <see cref="PollAsync"/>.
    /// </summary>
    private sealed class PollPacer
    {
        // More polls than any script here needs (the longest is three statuses). Reaching it means the condition
        // never came true, which is a failed test, not a slow machine.
        private const int MostPolls = 50;

        private readonly SemaphoreSlim _standingStill = new(0);
        private readonly object _lock = new();
        private TaskCompletionSource? _next;
        private GitHubActionsBackend? _backend;

        /// <summary>Hand this pacer to the backend as its wait, before it starts.</summary>
        public void Pace(GitHubActionsBackend backend)
        {
            _backend = backend;
            backend.WaitAsync = WaitAsync;
        }

        private Task WaitAsync(TimeSpan _, CancellationToken ct)
        {
            var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => next.TrySetCanceled(ct));
            lock (_lock) _next = next;
            _standingStill.Release();
            return next.Task;
        }

        /// <summary>Wait until the loop has finished a poll and is standing still. False when the loop has ended
        /// instead (it could not establish its thread, or it was shut down), so no caller waits for ever.</summary>
        private async Task<bool> StandingStillAsync()
        {
            var loop = _backend!.PollLoop ?? throw new InvalidOperationException("the backend was not started");
            var still = _standingStill.WaitAsync();
            return await Task.WhenAny(still, loop) == still;
        }

        private void LetItPoll()
        {
            lock (_lock) _next!.TrySetResult();
        }

        /// <summary>Let the loop poll until <paramref name="condition"/> holds, looking only while the loop is
        /// standing still. True when it holds - and the loop is then still standing still, so nothing changes
        /// under the assertions that follow. False when it never held.</summary>
        public async Task<bool> PollUntilAsync(Func<bool> condition)
        {
            for (var poll = 0; poll < MostPolls; poll++)
            {
                if (!await StandingStillAsync()) return condition();
                if (condition())
                {
                    _standingStill.Release();   // still standing still, for whoever asks next
                    return true;
                }
                LetItPoll();
            }
            return false;
        }

        /// <summary>Let the loop run exactly <paramref name="polls"/> more polls and stand still again.</summary>
        public async Task PollAsync(int polls)
        {
            for (var poll = 0; poll < polls; poll++)
            {
                Assert.True(await StandingStillAsync(), "the poll loop ended before it had polled");
                LetItPoll();
            }
            Assert.True(await StandingStillAsync(), "the poll loop ended before it had polled");
            _standingStill.Release();
        }
    }

    /// <summary>A backend whose polls the returned pacer decides.</summary>
    private static GitHubActionsBackend Paced(RemoteSessionConfig config, StubGitHubClient gh, out PollPacer pacer)
    {
        var backend = new GitHubActionsBackend(config, gh);
        pacer = new PollPacer();
        pacer.Pace(backend);
        return backend;
    }

    private static string BufferText(GitHubActionsBackend backend)
    {
        Assert.NotNull(backend.Buffer);
        return Encoding.UTF8.GetString(backend.Buffer.DumpAll());
    }

    [Fact]
    public async Task NewIssue_PostsClaudeMention_AndDrivesWorkingThenWaiting()
    {
        var gh = new StubGitHubClient();
        gh.RunStatusScript.Enqueue("queued");
        gh.RunStatusScript.Enqueue("in_progress");
        gh.RunStatusScript.Enqueue("completed");

        var states = new List<ActivityState>();
        using var backend = Paced(NewIssueConfig("fix the bug"), gh, out var pacer);
        backend.ActivitySink = s => { lock (states) states.Add(s); };

        backend.StartRemote();

        var reachedWaiting = await pacer.PollUntilAsync(() =>
        {
            lock (states) return states.Count > 0 && states[^1] == ActivityState.WaitingForInput;
        });

        Assert.True(reachedWaiting, "Backend should end the turn in WaitingForInput.");
        Assert.Equal(1, gh.CreateIssueCalls);
        Assert.Contains("@claude", gh.CreatedIssueBody);
        Assert.Contains("fix the bug", gh.CreatedIssueBody);

        lock (states)
        {
            Assert.Contains(ActivityState.Working, states);
            Assert.True(states.IndexOf(ActivityState.Working) < states.LastIndexOf(ActivityState.WaitingForInput));
        }

        var text = BufferText(backend);
        Assert.Contains("Created issue #42", text);
        Assert.Contains("Turn complete", text);
    }

    [Fact]
    public async Task SendTextAsync_PostsFollowUpComment_WithMention()
    {
        var gh = new StubGitHubClient();
        gh.RunStatusScript.Enqueue("completed");

        using var backend = Paced(NewIssueConfig(), gh, out var pacer);
        backend.StartRemote();

        await pacer.PollUntilAsync(() => backend.ThreadNumber == 42);
        await backend.SendTextAsync("now write tests");

        Assert.Contains(gh.PostedComments, c => c.Thread == 42 && c.Body.Contains("@claude") && c.Body.Contains("now write tests"));
    }

    [Fact]
    public async Task ExistingThread_PostsComment_AndDoesNotCreateIssue()
    {
        var gh = new StubGitHubClient();
        gh.RunStatusScript.Enqueue("completed");

        var config = new RemoteSessionConfig
        {
            Owner = "acme",
            Repo = "widget",
            TriggerMode = RemoteTriggerMode.ExistingThread,
            ThreadNumber = 7,
            InitialPrompt = "look at PR 7",
            PollIntervalMs = 25,
        };

        using var backend = Paced(config, gh, out var pacer);
        backend.StartRemote();

        var posted = await pacer.PollUntilAsync(() => gh.PostedComments.Count > 0);
        Assert.True(posted);
        Assert.Equal(0, gh.CreateIssueCalls);
        Assert.Equal(7, backend.ThreadNumber);
        Assert.Contains(gh.PostedComments, c => c.Thread == 7 && c.Body.Contains("@claude"));
    }

    [Fact]
    public async Task BotComment_IsPumpedIntoBuffer()
    {
        var gh = new StubGitHubClient();
        gh.RunStatusScript.Enqueue("in_progress");
        gh.BotComments = new List<GhComment>
        {
            new(5001, "Working on it: editing files now.", "https://x", DateTimeOffset.UtcNow, "claude[bot]"),
        };

        using var backend = Paced(NewIssueConfig(), gh, out var pacer);
        backend.StartRemote();

        var seen = await pacer.PollUntilAsync(() => BufferText(backend).Contains("Working on it: editing files now."));
        Assert.True(seen, "Bot comment body should be pumped into the terminal buffer.");
    }

    [Fact]
    public async Task NonBotComment_IsIgnored()
    {
        var gh = new StubGitHubClient();
        gh.RunStatusScript.Enqueue("in_progress");
        gh.BotComments = new List<GhComment>
        {
            new(5002, "A human kibitzing on the thread.", "https://x", DateTimeOffset.UtcNow, "some-human"),
        };

        using var backend = Paced(NewIssueConfig(), gh, out var pacer);
        backend.StartRemote();

        Assert.True(await pacer.PollUntilAsync(() => backend.ThreadNumber == 42));
        // Three more polls, each of which reads the thread's comments: the human's comment was offered to the
        // backend every time and was never written.
        await pacer.PollAsync(3);
        Assert.DoesNotContain("A human kibitzing", BufferText(backend));
    }

    [Fact]
    public async Task Shutdown_CancelsActiveRun()
    {
        var gh = new StubGitHubClient();
        // Stays in_progress so a run is active when we shut down.
        gh.RunStatusScript.Enqueue("in_progress");

        using var backend = Paced(NewIssueConfig(), gh, out var pacer);
        backend.StartRemote();

        await pacer.PollUntilAsync(() => gh.CancelledRunId is null && BufferText(backend).Contains("Run started"));
        await backend.GracefulShutdownAsync();

        Assert.Equal(gh.RunId, gh.CancelledRunId);
    }

    /// <summary>
    /// A REFUSED CANCELLATION IS A FAILED STOP (mission "Stop a session", inspection 1, finding I3).
    ///
    /// This backend used to catch the failure, write it into its own terminal buffer, set its status to
    /// "Stopped" and raise the exit event with code zero. The stop verb, seeing an exited session with no
    /// process identifier, then reported "no process was running" and cleared the row - so a remote run that
    /// was still going lost the only row the fleet had for it. The shutdown still does not THROW, because
    /// callers all over the desktop depend on that; it reports.
    /// </summary>
    [Fact]
    public async Task Shutdown_WhenTheRunWillNotCancel_ReportsTheFailureAndDoesNotClaimTheSessionEnded()
    {
        var gh = new StubGitHubClient();
        gh.RunStatusScript.Enqueue("in_progress");   // a run is active when the shutdown arrives

        using var backend = Paced(NewIssueConfig(), gh, out var pacer);
        var exitCodes = new List<int>();
        backend.ProcessExited += code => { lock (exitCodes) exitCodes.Add(code); };
        backend.StartRemote();

        Assert.True(await pacer.PollUntilAsync(() => backend.CurrentRunUrl is not null),
            "no run was ever discovered, so this test never reached the cancellation it is about");
        gh.CancelRunFailure = "403 the token may not cancel this run";

        // It must not throw: the desktop stop path has no catch around this.
        await backend.GracefulShutdownAsync();

        Assert.Equal(1, gh.CancelRunCalls);
        Assert.NotNull(backend.LastShutdownFailure);
        Assert.Contains("would not cancel", backend.LastShutdownFailure);
        Assert.Contains("403 the token may not cancel this run", backend.LastShutdownFailure);
        Assert.Contains("still be running on GitHub", backend.LastShutdownFailure);

        // And it does NOT claim the session ended - that claim is what removed the row.
        lock (exitCodes) Assert.Empty(exitCodes);
        Assert.NotEqual("Stopped", backend.Status);
    }

    /// <summary>The successful shutdown is unchanged, and reports no failure - so the stop verb has nothing
    /// to surface and goes on to its ordinary answer.</summary>
    [Fact]
    public async Task Shutdown_WhenTheRunCancels_ReportsNoFailureAndEndsTheSession()
    {
        var gh = new StubGitHubClient();
        gh.RunStatusScript.Enqueue("in_progress");

        using var backend = Paced(NewIssueConfig(), gh, out var pacer);
        var exitCodes = new List<int>();
        backend.ProcessExited += code => { lock (exitCodes) exitCodes.Add(code); };
        backend.StartRemote();

        Assert.True(await pacer.PollUntilAsync(() => backend.CurrentRunUrl is not null),
            "no run was ever discovered, so this test never reached the cancellation it is about");
        await backend.GracefulShutdownAsync();

        Assert.Equal(gh.RunId, gh.CancelledRunId);
        Assert.Null(backend.LastShutdownFailure);
        lock (exitCodes) Assert.Single(exitCodes);
    }

    [Fact]
    public async Task FailedRun_HandsTurnBack_AndReportsConclusion()
    {
        var gh = new StubGitHubClient { Conclusion = "failure" };
        gh.RunStatusScript.Enqueue("in_progress");
        gh.RunStatusScript.Enqueue("completed");

        var states = new List<ActivityState>();
        using var backend = Paced(NewIssueConfig(), gh, out var pacer);
        backend.ActivitySink = s => { lock (states) states.Add(s); };
        backend.StartRemote();

        var done = await pacer.PollUntilAsync(() =>
        {
            lock (states) return states.Count > 0 && states[^1] == ActivityState.WaitingForInput;
        });

        Assert.True(done);
        Assert.Contains("conclusion 'failure'", BufferText(backend));
    }

    [Fact]
    public async Task WorkflowDispatch_DispatchesWorkflow_PassesPromptInput_NoThread()
    {
        var gh = new StubGitHubClient();
        gh.RunStatusScript.Enqueue("in_progress");
        gh.RunStatusScript.Enqueue("completed");

        var config = new RemoteSessionConfig
        {
            Owner = "acme",
            Repo = "widget",
            TriggerMode = RemoteTriggerMode.WorkflowDispatch,
            WorkflowFile = "claude-dispatch.yml",
            InitialPrompt = "run the export job",
            PollIntervalMs = 25,
        };

        var states = new List<ActivityState>();
        using var backend = Paced(config, gh, out var pacer);
        backend.ActivitySink = s => { lock (states) states.Add(s); };
        backend.StartRemote();

        var done = await pacer.PollUntilAsync(() =>
        {
            lock (states) return states.Count > 0 && states[^1] == ActivityState.WaitingForInput;
        });

        Assert.True(done);
        Assert.Equal("claude-dispatch.yml", gh.DispatchedWorkflow);
        Assert.NotNull(gh.DispatchedInputs);
        Assert.Equal("run the export job", gh.DispatchedInputs["prompt"]);
        Assert.Equal(0, gh.CreateIssueCalls);
        Assert.Equal(0, backend.ThreadNumber);
    }

    [Fact]
    public async Task WorkflowDispatch_RefusesFollowUps()
    {
        var gh = new StubGitHubClient();
        gh.RunStatusScript.Enqueue("in_progress");

        var config = new RemoteSessionConfig
        {
            Owner = "acme",
            Repo = "widget",
            TriggerMode = RemoteTriggerMode.WorkflowDispatch,
            WorkflowFile = "claude-dispatch.yml",
            InitialPrompt = "run it",
            PollIntervalMs = 25,
        };

        using var backend = Paced(config, gh, out var pacer);
        backend.StartRemote();

        await pacer.PollUntilAsync(() => gh.DispatchedWorkflow != null);
        await backend.SendTextAsync("do more");

        Assert.Empty(gh.PostedComments);
        Assert.Contains("follow-ups are not supported", BufferText(backend));
    }
}
