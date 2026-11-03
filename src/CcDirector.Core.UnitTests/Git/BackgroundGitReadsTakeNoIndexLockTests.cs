using System.Diagnostics;
using System.Security.Cryptography;
using CcDirector.Core.Git;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Git;

/// <summary>
/// Issue #2872: the Director's background git reads must never take <c>index.lock</c>.
///
/// A plain <c>git status</c> refreshes the index and WRITES it back, and it holds <c>index.lock</c>
/// while it does. When a scan is superseded the runner kills the git process tree, and a killed git
/// cannot remove its lock - so a 0-byte <c>index.lock</c> stays behind for good and every later write
/// in that checkout fails with "File exists", while status itself keeps working. That froze a main
/// checkout on the owner's machine for six weeks and left 26 more locks in session worktrees.
///
/// Killing git inside the lock window is a race, so these tests prove the stronger, deterministic
/// property instead: each reader, handed a repository whose index is stale (a tracked file's
/// modification time moved, so status must refresh), leaves the index byte-for-byte untouched. A git
/// that never rewrites the index never takes the lock, so there is nothing a kill can strand.
///
/// The fixture runs a CONTROL - plain git status, with optional locks allowed, on an identical copy
/// of the repository - and every test asserts that one DID rewrite the index. Without that, an index
/// that stayed the same would prove nothing: the setup might simply not have needed a refresh.
///
/// The repository is built once and COPIED per case, because a git process costs over a second to
/// start on a loaded machine and the default gate has a time budget.
/// </summary>
public sealed class BackgroundGitReadsTakeNoIndexLockTests : IClassFixture<BackgroundGitReadsTakeNoIndexLockTests.StaleIndexRepo>
{
    private readonly StaleIndexRepo _template;

    public BackgroundGitReadsTakeNoIndexLockTests(StaleIndexRepo template)
    {
        _template = template;
    }

    public static TheoryData<string> Readers => new()
    {
        "GitStatusProvider",
        "GitSyncStatusProvider",
        "GitCommandRunner",
        "WingmanService",
    };

    [Theory]
    [MemberData(nameof(Readers))]
    public async Task BackgroundRead_StaleIndex_LeavesIndexUntouchedAndNoLock(string reader)
    {
        // A test process that already carries GIT_OPTIONAL_LOCKS would hand it to every child and make
        // this pass whatever the code under test does.
        Assert.Null(Environment.GetEnvironmentVariable(GitOptionalLocks.VariableName));
        // The setup really does make plain git status rewrite the index.
        Assert.True(_template.ControlRewroteIndex, "plain git status did not rewrite the index: the setup proves nothing");

        var repo = _template.CopyWithStaleIndex(reader);
        var before = StaleIndexRepo.HashIndex(repo);

        await RunReaderAsync(reader, repo);

        Assert.Equal(before, StaleIndexRepo.HashIndex(repo));
        Assert.False(File.Exists(Path.Combine(repo, ".git", "index.lock")), "index.lock was left behind");
    }

    [Fact]
    public void ApplyTo_SetsOptionalLocksOffInTheChildEnvironment()
    {
        var psi = new ProcessStartInfo("git");

        GitOptionalLocks.ApplyTo(psi);

        Assert.Equal("0", psi.Environment[GitOptionalLocks.VariableName]);
    }

    private static async Task RunReaderAsync(string reader, string repo)
    {
        switch (reader)
        {
            case "GitStatusProvider":
                var status = await new GitStatusProvider().GetStatusAsync(repo);
                Assert.True(status.Success, status.Error);
                break;
            case "GitSyncStatusProvider":
                // No upstream and no main to compare with, so Success may be false after the status
                // read; the status read itself is what this test is about.
                await new GitSyncStatusProvider().GetSyncStatusAsync(repo);
                break;
            case "GitCommandRunner":
                // The runner the worktree inventory and the worktree reaper read status through.
                var r = await new GitCommandRunner().RunAsync(repo, new[] { "status", "--porcelain" });
                Assert.True(r.Success, r.Error);
                break;
            case "WingmanService":
                var snap = await WingmanService.GitSnapshotAsync(repo);
                Assert.Null(snap.Error);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reader), reader, "unknown reader");
        }
    }

    /// <summary>One committed repository, built once; each case gets its own copy with a stale index.</summary>
    public sealed class StaleIndexRepo : IDisposable
    {
        private readonly string _root;
        private readonly string _template;

        public bool ControlRewroteIndex { get; }

        public StaleIndexRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "ccd-optlocks-" + Guid.NewGuid().ToString("N"));
            _template = Path.Combine(_root, "template");
            Directory.CreateDirectory(_template);
            RunPlainGit(_template, "init", "-q");
            RunPlainGit(_template, "config", "user.email", "test@example.com");
            RunPlainGit(_template, "config", "user.name", "test");
            RunPlainGit(_template, "config", "core.autocrlf", "false");
            File.WriteAllText(Path.Combine(_template, "tracked.txt"), "hello\n");
            RunPlainGit(_template, "add", "tracked.txt");
            RunPlainGit(_template, "commit", "-q", "-m", "init");

            var control = CopyWithStaleIndex("control");
            var before = HashIndex(control);
            RunPlainGit(control, "status", "--porcelain=v1", "-u");
            ControlRewroteIndex = before != HashIndex(control);
        }

        /// <summary>
        /// A copy of the repository whose tracked file has the same content and a different
        /// modification time: the index's cached stat no longer matches, so status must re-hash the
        /// file and - when optional locks are allowed - write the index back.
        /// </summary>
        public string CopyWithStaleIndex(string name)
        {
            var dest = Path.Combine(_root, name);
            CopyDirectory(_template, dest);
            File.SetLastWriteTimeUtc(Path.Combine(dest, "tracked.txt"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            return dest;
        }

        public static string HashIndex(string repo)
            => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(repo, ".git", "index"))));

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        private static void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(dest, Path.GetRelativePath(source, file)));
        }

        private static void RunPlainGit(string repo, params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = repo,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment.Remove(GitOptionalLocks.VariableName);

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            _ = stdout.Result;
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"git {string.Join(' ', args)} exited {proc.ExitCode}: {stderr}");
        }
    }
}
