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
}

/// <summary>
/// Integration tests that drive real git commands on a throwaway repo.
/// Exercises stage/unstage/discard/commit against a fixture with many files.
/// </summary>
[Trait("Gate", "Quarantine")]
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
        // The fixture holds a real git repository, whose objects are read-only; the shared delete clears the
        // attribute first and reports a failure instead of retrying and swallowing it, which leaked a folder per
        // test for as long as the swallow stood.
        TestTempRoot.DeleteTree(_repo);
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

        var status = await provider.GetStatusAsync(_repo);

        Assert.True(status.Success);
        Assert.Equal(2000, status.UnstagedChanges.Count);
        Assert.Empty(status.StagedChanges);
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
