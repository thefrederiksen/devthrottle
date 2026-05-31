using System.Text;
using CcDirector.Core.Backends;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Unit tests for the GitHub Actions remote backend. They drive the backend with a
/// scripted <see cref="StubGitHubClient"/> (no network) and assert the
/// run-status -> ActivityState mapping, comment posting, buffer pumping, and cancel.
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

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
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
        using var backend = new GitHubActionsBackend(NewIssueConfig("fix the bug"), gh);
        backend.ActivitySink = s => { lock (states) states.Add(s); };

        backend.StartRemote();

        var reachedWaiting = await WaitForAsync(() =>
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

        using var backend = new GitHubActionsBackend(NewIssueConfig(), gh);
        backend.StartRemote();

        await WaitForAsync(() => backend.ThreadNumber == 42);
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

        using var backend = new GitHubActionsBackend(config, gh);
        backend.StartRemote();

        var posted = await WaitForAsync(() => gh.PostedComments.Count > 0);
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

        using var backend = new GitHubActionsBackend(NewIssueConfig(), gh);
        backend.StartRemote();

        var seen = await WaitForAsync(() => BufferText(backend).Contains("Working on it: editing files now."));
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

        using var backend = new GitHubActionsBackend(NewIssueConfig(), gh);
        backend.StartRemote();

        await WaitForAsync(() => backend.ThreadNumber == 42);
        await Task.Delay(150);
        Assert.DoesNotContain("A human kibitzing", BufferText(backend));
    }

    [Fact]
    public async Task Shutdown_CancelsActiveRun()
    {
        var gh = new StubGitHubClient();
        // Stays in_progress so a run is active when we shut down.
        gh.RunStatusScript.Enqueue("in_progress");

        using var backend = new GitHubActionsBackend(NewIssueConfig(), gh);
        backend.StartRemote();

        await WaitForAsync(() => gh.CancelledRunId is null && BufferText(backend).Contains("Run started"));
        await backend.GracefulShutdownAsync();

        Assert.Equal(gh.RunId, gh.CancelledRunId);
    }

    [Fact]
    public async Task FailedRun_HandsTurnBack_AndReportsConclusion()
    {
        var gh = new StubGitHubClient { Conclusion = "failure" };
        gh.RunStatusScript.Enqueue("in_progress");
        gh.RunStatusScript.Enqueue("completed");

        var states = new List<ActivityState>();
        using var backend = new GitHubActionsBackend(NewIssueConfig(), gh);
        backend.ActivitySink = s => { lock (states) states.Add(s); };
        backend.StartRemote();

        var done = await WaitForAsync(() =>
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
        using var backend = new GitHubActionsBackend(config, gh);
        backend.ActivitySink = s => { lock (states) states.Add(s); };
        backend.StartRemote();

        var done = await WaitForAsync(() =>
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

        using var backend = new GitHubActionsBackend(config, gh);
        backend.StartRemote();

        await WaitForAsync(() => gh.DispatchedWorkflow != null);
        await backend.SendTextAsync("do more");

        Assert.Empty(gh.PostedComments);
        Assert.Contains("follow-ups are not supported", BufferText(backend));
    }
}
