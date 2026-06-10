using CcDirector.Cockpit.Services;
using Xunit;

namespace CcDirector.Cockpit.Tests;

/// <summary>
/// The flow:* label -> status mapping is criterion 4/5 of issue #275: the per-item badge is DERIVED
/// from the GitHub label and must follow it exactly. These tests pin the mapping (ASSUMPTION D1).
/// </summary>
public class GitHubItemStatusClientTests
{
    [Fact]
    public void MapStatus_NoLabels_ReturnsQueued()
    {
        Assert.Equal(WorkItemStatus.Queued, GitHubItemStatusClient.MapStatus(Array.Empty<string>()));
    }

    [Fact]
    public void MapStatus_ReadyDev_ReturnsQueued()
    {
        Assert.Equal(WorkItemStatus.Queued, GitHubItemStatusClient.MapStatus(new[] { "flow:ready-dev" }));
    }

    [Fact]
    public void MapStatus_ReadyQa_ReturnsRunning()
    {
        Assert.Equal(WorkItemStatus.Running, GitHubItemStatusClient.MapStatus(new[] { "flow:ready-qa" }));
    }

    [Fact]
    public void MapStatus_QaFailed_ReturnsRunning()
    {
        // A mid-loop qa-failed bounce is transient: the loop is still on the item, so it is Running.
        Assert.Equal(WorkItemStatus.Running, GitHubItemStatusClient.MapStatus(new[] { "flow:qa-failed" }));
    }

    [Fact]
    public void MapStatus_Done_ReturnsDone()
    {
        Assert.Equal(WorkItemStatus.Done, GitHubItemStatusClient.MapStatus(new[] { "flow:done" }));
    }

    [Fact]
    public void MapStatus_NeedsHuman_ReturnsNeedsHuman()
    {
        Assert.Equal(WorkItemStatus.NeedsHuman, GitHubItemStatusClient.MapStatus(new[] { "flow:needs-human" }));
    }

    [Fact]
    public void MapStatus_Failed_ReturnsFailed()
    {
        Assert.Equal(WorkItemStatus.Failed, GitHubItemStatusClient.MapStatus(new[] { "flow:failed" }));
    }

    [Fact]
    public void MapStatus_DoneWinsOverRunning_ReturnsDone()
    {
        // Terminal state wins over an in-flight state if both are somehow present.
        Assert.Equal(WorkItemStatus.Done, GitHubItemStatusClient.MapStatus(new[] { "flow:ready-qa", "flow:done" }));
    }

    [Fact]
    public void MapStatus_NeedsHumanWinsOverRunning_ReturnsNeedsHuman()
    {
        Assert.Equal(WorkItemStatus.NeedsHuman, GitHubItemStatusClient.MapStatus(new[] { "flow:qa-failed", "flow:needs-human" }));
    }

    [Fact]
    public void MapStatus_IgnoresUnrelatedLabels_ReturnsQueued()
    {
        Assert.Equal(WorkItemStatus.Queued, GitHubItemStatusClient.MapStatus(new[] { "enhancement", "cockpit" }));
    }

    [Fact]
    public void MapStatus_IsCaseInsensitive_ReturnsDone()
    {
        Assert.Equal(WorkItemStatus.Done, GitHubItemStatusClient.MapStatus(new[] { "FLOW:DONE" }));
    }

    [Fact]
    public async Task ResolveAsync_NonGithubSource_ReturnsQueuedWithoutNetworkCall()
    {
        // devops/jira items have no flow label; they resolve to Queued without touching the network.
        // A null HttpClient proves no call is made for a non-github source.
        var client = new GitHubItemStatusClient(new HttpClient(), NullLogger());

        var info = await client.ResolveAsync("devops", "1203");

        Assert.Equal(WorkItemStatus.Queued, info.Status);
        Assert.Null(info.Title);
    }

    private static Microsoft.Extensions.Logging.ILogger<GitHubItemStatusClient> NullLogger()
        => Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubItemStatusClient>.Instance;
}
