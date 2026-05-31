using System.Diagnostics;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// #6a - git WRITE actions against a real throwaway repo. Proves stage / commit / discard
/// actually mutate the working tree (the desktop Source Control parity the Cockpit needs).
/// </summary>
public sealed class GitWriteServiceTests : IDisposable
{
    private readonly string _repo;
    private readonly GitWriteService _git = new();

    public GitWriteServiceTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "ccd-gitwrite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
        RunGit("init");
        RunGit("config", "user.email", "test@cc-director.local");
        RunGit("config", "user.name", "CC Director Test");
        RunGit("config", "commit.gpgsign", "false");
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { /* temp cleanup */ }
    }

    [Fact]
    public async Task Stage_then_commit_creates_a_commit()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "hello");

        var staged = await _git.StageAsync(_repo, new[] { "a.txt" });
        Assert.True(staged.Success, staged.Error);

        var committed = await _git.CommitAsync(_repo, "add a.txt");
        Assert.True(committed.Success, committed.Error);

        Assert.Contains("add a.txt", RunGit("log", "--oneline"));
    }

    [Fact]
    public async Task Commit_with_empty_message_is_rejected()
    {
        var r = await _git.CommitAsync(_repo, "   ");
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Discard_reverts_an_unstaged_change_to_a_tracked_file()
    {
        var file = Path.Combine(_repo, "tracked.txt");
        File.WriteAllText(file, "original");
        await _git.StageAsync(_repo, new[] { "tracked.txt" });
        await _git.CommitAsync(_repo, "add tracked.txt");

        File.WriteAllText(file, "modified");                  // dirty the tracked file
        var discarded = await _git.DiscardAsync(_repo, new[] { "tracked.txt" });

        Assert.True(discarded.Success, discarded.Error);
        Assert.Equal("original", File.ReadAllText(file));     // reverted
    }

    [Fact]
    public async Task Discard_with_no_paths_is_rejected()
    {
        var r = await _git.DiscardAsync(_repo, Array.Empty<string>());
        Assert.False(r.Success);
    }

    private string RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}
