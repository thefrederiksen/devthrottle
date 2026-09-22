using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CcDirector.Avalonia.Controls;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The Source Control page does no git work while it cannot be seen. The host hides the whole
/// Source Control panel when another tab is chosen and never touches the page's own IsVisible
/// flag, so the page has to ask whether it is EFFECTIVELY visible. This test mounts the page under
/// a panel that stands in for the host's, hides the panel, and drives both ticks by hand against a
/// real repository with a real origin: the sync tick must not fetch (git writes FETCH_HEAD when it
/// does), and neither tick may do anything at all. Shown again, both run.
///
/// Against the page as it was before 22 September 2026 - the poll checking its own IsVisible, the
/// sync tick checking nothing - the hidden ticks ran and the fetch landed.
/// </summary>
public sealed class GitChangesViewHiddenTabTests : IDisposable
{
    private readonly string _root;
    private readonly string _repo;

    public GitChangesViewHiddenTabTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-hidden-tab-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        var origin = Path.Combine(_root, "origin.git");
        _repo = Path.Combine(_root, "work");
        RunGit(_root, "-c", "init.defaultBranch=main", "init", "--bare", origin);
        RunGit(_root, "-c", "init.defaultBranch=main", "clone", origin, _repo);
        RunGit(_repo, "config", "user.email", "test@example.com");
        RunGit(_repo, "config", "user.name", "Test");
        RunGit(_repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "initial\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "initial commit");
        RunGit(_repo, "branch", "-M", "main");
        RunGit(_repo, "push", "-u", "origin", "main");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string FetchHead => Path.Combine(_repo, ".git", "FETCH_HEAD");

    [AvaloniaFact]
    public async Task WhileTheHostPanelIsHidden_NeitherTickRuns_AndNothingIsFetched()
    {
        var view = new GitChangesView();
        var hostPanel = new Panel { Children = { view } }; // stands in for MainWindow's SourceControlPanel
        var window = new Window { Content = hostPanel };
        window.Show();

        view.Attach(_repo); // starts the timers and does the one initial refresh and fetch
        await WaitUntil(() => File.Exists(FetchHead), "the attach fetch");
        await Task.Delay(300); // let that fetch's process finish before its record is removed
        File.Delete(FetchHead);

        hostPanel.IsVisible = false;
        Assert.False(view.IsEffectivelyVisible, "the panel hides the page, though the page's own flag is untouched");
        Assert.True(view.IsVisible);

        Assert.False(await view.SyncTickAsync(DateTime.UtcNow.AddMinutes(2)), "a hidden page's sync tick must be skipped");
        Assert.False(await view.PollTickAsync(), "a hidden page's poll tick must be skipped");
        await Task.Delay(300);
        Assert.False(File.Exists(FetchHead), "a hidden page must not fetch");

        hostPanel.IsVisible = true;
        Assert.True(await view.SyncTickAsync(DateTime.UtcNow.AddMinutes(2)), "a visible page's sync tick runs");
        Assert.True(await view.PollTickAsync(), "a visible page's poll tick runs");
        Assert.True(File.Exists(FetchHead), "a visible page's due sync tick fetches");

        view.Detach();
        window.Close();
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"waited 15 s for {what}");
            await Task.Delay(50);
        }
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}) in {cwd}:\n{stdout}\n{stderr}");
    }
}
