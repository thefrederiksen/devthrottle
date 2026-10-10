using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The Source Control page does no git work while it cannot be seen. The host hides the whole
/// Source Control panel when another tab is chosen and never touches the page's own IsVisible
/// flag, so the page has to ask whether it is EFFECTIVELY visible. This test mounts the page under
/// a panel that stands in for the host's, hides the panel, and drives both ticks by hand: the
/// sync tick must not fetch, and neither tick may do anything at all. Shown again, both run.
///
/// The page is handed a git that counts what it is asked to do, so "nothing was fetched" is a
/// count of zero rather than the absence of a file that a real git process, still running from
/// the attach, could write after the test looked (issue #3771).
///
/// Against the page as it was before 22 September 2026 - the poll checking its own IsVisible, the
/// sync tick checking nothing - the hidden ticks ran and the fetch was counted.
/// </summary>
public sealed class GitChangesViewHiddenTabTests : IDisposable
{
    private readonly string _repo;

    public GitChangesViewHiddenTabTests()
    {
        // The page only checks that the folder exists; every git call goes to the counting git.
        _repo = Path.Combine(Path.GetTempPath(), "ccd-hidden-tab-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        // No catch: a folder this test cannot remove is a fault to see, not scratch to leave behind.
        Directory.Delete(_repo, recursive: true);
    }

    [AvaloniaFact]
    public async Task WhileTheHostPanelIsHidden_NeitherTickRuns_AndNothingIsFetched()
    {
        var git = new CountingGit();
        var view = new GitChangesView(git);
        var hostPanel = new Panel { Children = { view } }; // stands in for MainWindow's SourceControlPanel
        var window = new Window { Content = hostPanel };
        window.Show();

        view.Attach(_repo); // starts the timers and does the one initial refresh and fetch
        await view.AttachRefresh;
        Assert.Equal(1, git.Fetches);
        var statusReadsAfterAttach = git.StatusReads;

        hostPanel.IsVisible = false;
        Assert.False(view.IsEffectivelyVisible, "the panel hides the page, though the page's own flag is untouched");
        Assert.True(view.IsVisible);

        Assert.False(await view.SyncTickAsync(DateTime.UtcNow.AddMinutes(2)), "a hidden page's sync tick must be skipped");
        Assert.False(await view.PollTickAsync(), "a hidden page's poll tick must be skipped");
        Assert.Equal(1, git.Fetches);
        Assert.Equal(statusReadsAfterAttach, git.StatusReads);

        hostPanel.IsVisible = true;
        Assert.True(await view.SyncTickAsync(DateTime.UtcNow.AddMinutes(2)), "a visible page's sync tick runs");
        Assert.True(await view.PollTickAsync(), "a visible page's poll tick runs");
        Assert.Equal(2, git.Fetches);
        Assert.True(git.StatusReads > statusReadsAfterAttach, "a visible page's poll tick reads the status");

        view.Detach();
        window.Close();
    }

    /// <summary>A git that answers at once and counts the fetches and status reads it is asked for.</summary>
    private sealed class CountingGit : IGitChangesGit
    {
        public int Fetches { get; private set; }
        public int StatusReads { get; private set; }

        public Task<GitStatusResult> GetStatusAsync(string repoPath)
        {
            StatusReads++;
            return Task.FromResult(new GitStatusResult { Success = true });
        }

        public string? GetCachedRawOutput(string repoPath) => null;

        public Task FetchAsync(string repoPath)
        {
            Fetches++;
            return Task.CompletedTask;
        }

        public Task<GitSyncStatus> GetSyncStatusAsync(string repoPath) =>
            Task.FromResult(new GitSyncStatus { Success = true, BranchName = "main", HasUpstream = true });
    }
}
