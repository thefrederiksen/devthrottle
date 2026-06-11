using System.Diagnostics;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Issue #334 - Source Control tab review: large-repo correctness.
///
/// Tests exercise GitStatusProvider.ParsePorcelainOutput and GitWriteService
/// with fixture-scale inputs (2000+ files) to prove correctness and
/// performance of the parser and write operations at large-repo scale.
///
/// BuildTree tests live in CcDirector.Avalonia.Tests/GitChangesViewTreeTests.cs
/// since BuildTree is on a UI-layer class.
/// </summary>
public class GitLargeRepoParserTests
{
    /// <summary>
    /// Build a 2001-entry porcelain string: 50 staged (A), 1950 untracked (??), 1 tracked modified ( M).
    /// This mirrors the fixture produced by scripts/generate-large-repo-fixture.ps1.
    /// </summary>
    private static string BuildLargePorcelainOutput(int stagedCount = 50, int untrackedCount = 1950, int modifiedCount = 1)
    {
        var sb = new System.Text.StringBuilder();

        // Staged added files: src/alpha/file-N.cs
        for (int i = 1; i <= stagedCount; i++)
            sb.AppendLine($"A  src/alpha/file-{i}.cs");

        // Untracked files spread across subdirectories
        int unt = 0;
        string[] dirs = { "src/beta", "src/gamma", "src/delta", "tests/unit", "tests/integration", "docs/api", "tools/build", "config/dev" };
        for (int d = 0; d < dirs.Length && unt < untrackedCount; d++)
        {
            for (int i = 1; unt < untrackedCount; i++, unt++)
                sb.AppendLine($"?? {dirs[d]}/file-{i}.cs");
        }

        // Tracked modified file
        for (int i = 0; i < modifiedCount; i++)
            sb.AppendLine($" M large-tracked-{i}.txt");

        return sb.ToString();
    }

    [Fact]
    public void ParsePorcelain_2001Entries_ParsesCorrectly()
    {
        var output = BuildLargePorcelainOutput();
        var result = GitStatusProvider.ParsePorcelainOutput(output);

        Assert.True(result.Success);
        Assert.Equal(50, result.StagedChanges.Count);
        Assert.Equal(1951, result.UnstagedChanges.Count); // 1950 untracked + 1 tracked modified
    }

    [Fact]
    public void ParsePorcelain_2001Entries_FilePathsAreCorrect()
    {
        var output = BuildLargePorcelainOutput();
        var result = GitStatusProvider.ParsePorcelainOutput(output);

        // All staged are from src/alpha
        Assert.True(result.StagedChanges.All(f => f.FilePath.StartsWith("src/alpha/")));
        Assert.True(result.StagedChanges.All(f => f.Status == GitFileStatus.Added));

        // Untracked count is exact
        int untrackedCount = result.UnstagedChanges.Count(f => f.Status == GitFileStatus.Untracked);
        Assert.Equal(1950, untrackedCount);

        // Modified count is 1
        int modifiedCount = result.UnstagedChanges.Count(f => f.Status == GitFileStatus.Modified);
        Assert.Equal(1, modifiedCount);
    }

    [Fact]
    public void ParsePorcelain_2001Entries_FilenamesExtracted()
    {
        var output = BuildLargePorcelainOutput();
        var result = GitStatusProvider.ParsePorcelainOutput(output);

        // All entries have non-empty filenames
        Assert.True(result.StagedChanges.All(f => !string.IsNullOrEmpty(f.FileName)));
        Assert.True(result.UnstagedChanges.All(f => !string.IsNullOrEmpty(f.FileName)));

        // Verify filename extraction for a staged file
        var first = result.StagedChanges[0];
        Assert.Equal("file-1.cs", first.FileName);
        Assert.Equal("src/alpha/file-1.cs", first.FilePath);
    }

    [Fact]
    public void CountPorcelainLines_2001Entries_MatchesParse()
    {
        var output = BuildLargePorcelainOutput();
        int count = GitStatusProvider.CountPorcelainLines(output);
        var result = GitStatusProvider.ParsePorcelainOutput(output);
        int parseCount = result.StagedChanges.Count + result.UnstagedChanges.Count;

        Assert.Equal(parseCount, count);
    }

    [Fact]
    public void ParsePorcelain_2001Entries_Performance()
    {
        // Parsing 2001-entry porcelain should complete well under 1 second for 10 iterations
        var output = BuildLargePorcelainOutput();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
            GitStatusProvider.ParsePorcelainOutput(output);
        sw.Stop();

        // 10 parses should complete in < 1000ms (100ms each) - well under CLAUDE.md's 100ms UI gate
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"10 parses of 2001-entry output took {sw.ElapsedMilliseconds}ms (expected < 1000ms)");
    }
}

/// <summary>
/// Integration tests that drive real git commands on a throwaway repo.
/// Exercises stage/unstage/discard/commit against a fixture with many files.
/// </summary>
public sealed class GitLargeRepoIntegrationTests : IDisposable
{
    private readonly string _repo;
    private readonly GitWriteService _git = new();

    public GitLargeRepoIntegrationTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "ccd-gitreg-" + Guid.NewGuid().ToString("N"));
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

    private void CreateTrackedFile(string relPath, string content)
    {
        var full = Path.Combine(_repo, relPath);
        var dir = Path.GetDirectoryName(full);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
        RunGit("add", relPath);
    }

    private void CommitAll(string msg) => RunGit("commit", "-m", msg);

    [Fact]
    public async Task Stage50_Unstage25_Discard5_Commit_LeavesCorrectState()
    {
        // Create initial committed state: 100 files in src/
        for (int i = 1; i <= 100; i++)
            CreateTrackedFile($"src/file-{i}.txt", $"content-{i}");
        CommitAll("initial commit");

        // Modify all 100 files (make them unstaged modified)
        for (int i = 1; i <= 100; i++)
            File.WriteAllText(Path.Combine(_repo, $"src/file-{i}.txt"), $"modified-{i}");

        var provider = new GitStatusProvider();
        GitStatusProvider.InvalidateCache(_repo);
        var statusBefore = await provider.GetStatusAsync(_repo);

        Assert.True(statusBefore.Success);
        Assert.Empty(statusBefore.StagedChanges);
        Assert.Equal(100, statusBefore.UnstagedChanges.Count);

        // Stage 50 files
        var toStage = Enumerable.Range(1, 50).Select(i => $"src/file-{i}.txt").ToArray();
        var stageResult = await _git.StageAsync(_repo, toStage);
        Assert.True(stageResult.Success, stageResult.Error);

        GitStatusProvider.InvalidateCache(_repo);
        var afterStage = await provider.GetStatusAsync(_repo);
        Assert.Equal(50, afterStage.StagedChanges.Count);
        Assert.Equal(50, afterStage.UnstagedChanges.Count);

        // Unstage 25 files
        var toUnstage = Enumerable.Range(1, 25).Select(i => $"src/file-{i}.txt").ToArray();
        var unstageResult = await _git.UnstageAsync(_repo, toUnstage);
        Assert.True(unstageResult.Success, unstageResult.Error);

        GitStatusProvider.InvalidateCache(_repo);
        var afterUnstage = await provider.GetStatusAsync(_repo);
        Assert.Equal(25, afterUnstage.StagedChanges.Count);
        Assert.Equal(75, afterUnstage.UnstagedChanges.Count);

        // Discard 5 files (from the unstaged set, files 1-5)
        var toDiscard = Enumerable.Range(1, 5).Select(i => $"src/file-{i}.txt").ToArray();
        var discardResult = await _git.DiscardAsync(_repo, toDiscard);
        Assert.True(discardResult.Success, discardResult.Error);

        // Verify discarded files are reverted
        for (int i = 1; i <= 5; i++)
        {
            var content = File.ReadAllText(Path.Combine(_repo, $"src/file-{i}.txt"));
            Assert.Equal($"content-{i}", content); // back to original
        }

        // Commit the 25 staged files
        var commitResult = await _git.CommitAsync(_repo, "commit 25 staged files");
        Assert.True(commitResult.Success, commitResult.Error);

        // Verify git log shows the commit
        var log = RunGit("log", "--oneline");
        Assert.Contains("commit 25 staged files", log);

        // Verify final state: 70 unstaged (75 - 5 discarded) + 0 staged
        GitStatusProvider.InvalidateCache(_repo);
        var finalStatus = await provider.GetStatusAsync(_repo);
        Assert.Empty(finalStatus.StagedChanges);
        Assert.Equal(70, finalStatus.UnstagedChanges.Count);
    }

    [Fact]
    public async Task GetStatusAsync_2000UntrackedFiles_ParsesAllEntries()
    {
        // Create 2000 untracked files across subdirectories
        var dirs = new[] { "src/a", "src/b", "src/c", "src/d" };
        foreach (var dir in dirs)
        {
            var fullDir = Path.Combine(_repo, dir);
            Directory.CreateDirectory(fullDir);
            for (int i = 1; i <= 500; i++)
                File.WriteAllText(Path.Combine(fullDir, $"file-{i}.txt"), $"content-{i}");
        }

        var provider = new GitStatusProvider();
        GitStatusProvider.InvalidateCache(_repo);

        var sw = Stopwatch.StartNew();
        var status = await provider.GetStatusAsync(_repo);
        sw.Stop();

        Assert.True(status.Success);
        Assert.Equal(2000, status.UnstagedChanges.Count);
        Assert.Empty(status.StagedChanges);

        // GetStatusAsync should complete in reasonable time
        Assert.True(sw.ElapsedMilliseconds < 30000,
            $"GetStatusAsync for 2000 files took {sw.ElapsedMilliseconds}ms (expected < 30000ms)");
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
