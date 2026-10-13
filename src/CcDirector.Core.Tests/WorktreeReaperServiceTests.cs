using CcDirector.Core.Tests.Git;
using System.Diagnostics;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Integration tests for the reaper against real git repositories (with a local bare origin).
/// Proves it removes exactly the safe set, leaves everything else untouched, honours the
/// live-session guard, and reports - rather than hides - a folder it could not delete.
///
/// QUARANTINED (Reliable Tests, 11 October 2026): it runs real git processes, which gate tests may no longer do,
/// but it is the only real coverage of a sweep that DELETES worktrees, so it is kept and run on its own: test-
/// local.ps1 leaves it out of every gate run and runs it under -Quarantine, whose result never decides a release.
/// </summary>
[Trait("Gate", "Quarantine")]
public sealed class WorktreeReaperServiceTests : IClassFixture<WorktreeReaperServiceTests.Template>, IDisposable
{
    private readonly string _root;
    private readonly string _origin;
    private readonly string _primary;
    // A leftover store scoped to this test's temp dir - so no test ever reads or deletes real
    // leftovers under the user's %LOCALAPPDATA%.
    private readonly WorktreeLeftoverStore _leftovers;

    /// <summary>The bare origin and its primary clone, built once for the class and copied per test.</summary>
    public sealed class Template : GitRepositoryTemplate
    {
        public Template() : base("ccd-reaper-") { }

        protected override IReadOnlyList<string> Clones => new[] { "primary" };

        protected override void Build(string root)
        {
            var origin = Path.Combine(root, "origin.git");
            var primary = Path.Combine(root, "primary");

            RunGit(root, "-c", "init.defaultBranch=main", "init", "--bare", origin);
            RunGit(root, "-c", "init.defaultBranch=main", "clone", origin, primary);
            RunGit(primary, "config", "user.email", "test@cc-director.local");
            RunGit(primary, "config", "user.name", "CC Director Test");
            RunGit(primary, "config", "commit.gpgsign", "false");

            WriteFile(primary, "README.md", "initial\n");
            RunGit(primary, "add", "-A");
            RunGit(primary, "commit", "-m", "initial commit");
            RunGit(primary, "branch", "-M", "main");
            RunGit(primary, "push", "-u", "origin", "main");
        }
    }

    public WorktreeReaperServiceTests(Template template)
    {
        _root = template.CopyTo();
        _leftovers = new WorktreeLeftoverStore(Path.Combine(_root, "leftovers"));
        _origin = Path.Combine(_root, "origin.git");
        _primary = Path.Combine(_root, "primary");
    }

    public void Dispose()
    {
        // The copy is this test's own; a delete that fails is reported, not retried and swallowed - the
        // swallowed version leaked a folder per test for months, because git's objects are read-only.
        TestTempRoot.DeleteTree(_root);
    }

    /// <summary>A clock an hour in the future, so freshly-created test worktrees are past the
    /// activity cooling-off window and can be reaped (the cooling-off itself has its own test).</summary>
    private static readonly Func<DateTime> Later = () => DateTime.UtcNow.AddHours(1);

    /// <summary>An authoritative roster that positively reports no live sessions (reap may proceed).</summary>
    private static readonly Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>> NoSessions =
        _ => Task.FromResult<IReadOnlyList<LiveSessionRef>>(Array.Empty<LiveSessionRef>());

    /// <summary>
    /// Makes one file's directory refuse deletion for as long as this is held, so a reap has something
    /// it genuinely CANNOT delete and must report as a leftover.
    ///
    /// The two platforms need opposite mechanisms, and that is the whole reason this exists. On Windows
    /// an open handle without delete sharing blocks the delete, which is what these tests used. On macOS
    /// and Linux an open file does NOT block anything - the entry is unlinked and the data lives on
    /// until the last handle closes - so holding the file open left the directory perfectly deletable,
    /// the reap succeeded, and the tests failed on a condition that was never created. Removing write
    /// permission from the CONTAINING directory is the Unix way to make unlinking its entries fail, and
    /// it is reversible, which the retry test needs.
    /// </summary>
    private sealed class UndeletableFile : IDisposable
    {
        private readonly FileStream? _handle;
        private readonly string? _directory;
        private readonly UnixFileMode _originalMode;

        public UndeletableFile(string filePath)
        {
            if (OperatingSystem.IsWindows())
            {
                _handle = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return;
            }

            _directory = Path.GetDirectoryName(filePath)!;
            _originalMode = File.GetUnixFileMode(_directory);
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        public void Dispose()
        {
            _handle?.Dispose();
            // The platform guard is explicit rather than implied by _directory being set: the
            // compatibility analyzer reads the guard, not the invariant.
            if (!OperatingSystem.IsWindows() && _directory is not null)
            {
                // Restore, or the fixture's own cleanup cannot remove the tree either.
                try { File.SetUnixFileMode(_directory, _originalMode); } catch { }
            }
        }
    }

    [Fact]
    public async Task Reap_EmptyRepo_NoWorktrees_IsANoOpSuccess()
    {
        var result = await new WorktreeReaperService(leftovers: _leftovers, utcNow: Later).ReapAsync(_primary, NoSessions);

        Assert.True(result.Success);
        Assert.Equal(0, result.RemovedCount);
        Assert.Empty(result.Leftovers);
    }

    private static readonly DateTime ReservationOwnerStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ----- helpers -----

    /// <summary>A git runner that records every "git fetch ..." invocation and runs it for real.</summary>
    private sealed class CaptureFetchGitRunner : GitCommandRunner
    {
        public readonly List<string[]> Fetches = new();
        public override async Task<GitCommandResult> RunAsync(
            string workingDirectory, string[] args, CancellationToken ct = default)
        {
            if (args.Length >= 1 && args[0] == "fetch")
                Fetches.Add(args);
            return await base.RunAsync(workingDirectory, args, ct);
        }
    }

    /// <summary>A git runner that fails the "git fetch --prune" call and runs everything else for real.</summary>
    private sealed class FailFetchGitRunner : GitCommandRunner
    {
        public override async Task<GitCommandResult> RunAsync(
            string workingDirectory, string[] args, CancellationToken ct = default)
        {
            if (args.Length >= 2 && args[0] == "fetch" && args[1] == "--prune")
                return new GitCommandResult { Success = false, ExitCode = 1, Error = "injected fetch failure (network down)" };
            return await base.RunAsync(workingDirectory, args, ct);
        }
    }

    /// <summary>
    /// A git runner that creates an untracked file in <c>worktreePath</c> the first time a
    /// "git worktree remove" command runs, then delegates to real git - which then refuses the
    /// removal because the worktree just became dirty. Reproduces the check-to-remove race
    /// deterministically. Every other command runs for real.
    /// </summary>
    private sealed class DirtyBeforeRemoveGitRunner : GitCommandRunner
    {
        private readonly string _worktreePath;
        private readonly string _fileToCreate;
        private int _fired;

        public DirtyBeforeRemoveGitRunner(string worktreePath, string fileToCreate)
        {
            _worktreePath = worktreePath;
            _fileToCreate = fileToCreate;
        }

        public override async Task<GitCommandResult> RunAsync(
            string workingDirectory, string[] args, CancellationToken ct = default)
        {
            if (args.Length >= 2 && args[0] == "worktree" && args[1] == "remove"
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                File.WriteAllText(_fileToCreate, "work that arrived after the safety scan\n");
            }
            return await base.RunAsync(workingDirectory, args, ct);
        }
    }

    private static void WriteFile(string repo, string relPath, string content)
    {
        var full = Path.Combine(repo, relPath);
        var dir = Path.GetDirectoryName(full);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
    }

    private static string RunGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
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
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {p.ExitCode}): {stderr}");
        return stdout;
    }
}
