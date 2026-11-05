using System.Diagnostics;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Director-side repo-state pusher (issue #2118). The claim under test is the FAIL-SAFE promise, and it
/// is tested rather than asserted in a comment: this is a background feed for a morning email, and it must
/// never be able to disturb the sessions a person is working in.
///
/// The three failure shapes that matter are all here - the Gateway refusing the push, the Gateway throwing
/// at the transport, and the repository enumeration itself failing - and in every one the cycle returns
/// quietly and the NEXT cycle still runs. A pusher that threw, or that stopped trying after a failure,
/// would look identical on a good day and fail silently on a bad one.
/// </summary>
public sealed class RepoStatePusherTests : IDisposable
{
    private readonly string _root;
    private readonly string _repo;
    private readonly string _registryPath;

    public RepoStatePusherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-pusher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _repo = Path.Combine(_root, "repo");
        _registryPath = Path.Combine(_root, "repositories.json");

        RunGit(_root, "-c", "init.defaultBranch=main", "init", _repo);
        RunGit(_repo, "config", "user.email", "test@cc-director.local");
        RunGit(_repo, "config", "user.name", "CC Director Test");
        RunGit(_repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "a\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "initial");
    }

    public void Dispose()
    {
        for (var i = 0; i < 3; i++)
        {
            try { Directory.Delete(_root, recursive: true); return; }
            catch { Thread.Sleep(100); }
        }
    }

    private RepositoryRegistry Registry(bool withRepo = true)
    {
        var registry = new RepositoryRegistry(_registryPath);
        if (withRepo)
            registry.SeedFrom(new[] { new RepositoryConfig { Name = "repo", Path = _repo } });
        return registry;
    }

    private RepoStatePusher NewPusher(
        Func<RepoStatePushRequest, CancellationToken, Task<RepoStatePushResponse?>> push,
        RepositoryRegistry? registry = null)
        => new(push, registry ?? Registry(), "dir-1", "TEST-MACHINE");

    [Fact]
    public async Task A_successful_cycle_pushes_the_registered_repository()
    {
        RepoStatePushRequest? captured = null;
        var pusher = NewPusher((req, _) =>
        {
            captured = req;
            return Task.FromResult<RepoStatePushResponse?>(new RepoStatePushResponse { Stored = req.Repositories.Count });
        });

        var stored = await pusher.PushOnceAsync();

        Assert.Equal(1, stored);
        Assert.NotNull(captured);
        Assert.Equal("dir-1", captured!.DirectorId);
        Assert.Equal("TEST-MACHINE", captured.MachineName);
        Assert.Equal(_repo, Assert.Single(captured.Repositories).Path);
    }

    [Fact]
    public async Task A_Gateway_that_REFUSES_the_push_does_not_throw_and_the_next_cycle_still_runs()
    {
        var attempts = 0;
        var pusher = NewPusher((_, _) =>
        {
            attempts++;
            return Task.FromResult<RepoStatePushResponse?>(null);   // "did not land"
        });

        Assert.Null(await pusher.PushOnceAsync());
        Assert.Null(await pusher.PushOnceAsync());

        // Both cycles ran. A pusher that gave up after a refusal would leave the report permanently blind
        // to this machine after one bad moment.
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task A_Gateway_that_THROWS_is_swallowed_and_the_next_cycle_still_runs()
    {
        var attempts = 0;
        var pusher = NewPusher((_, _) =>
        {
            attempts++;
            throw new HttpRequestException("the Gateway is down");
        });

        // The exception must not escape into the Director. This is the difference between a hygiene feed
        // being unavailable and a person's session dying because an email could not be assembled.
        Assert.Null(await pusher.PushOnceAsync());
        Assert.Null(await pusher.PushOnceAsync());
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task A_repository_that_cannot_be_enumerated_does_not_stop_the_cycle()
    {
        var registry = new RepositoryRegistry(_registryPath);
        registry.SeedFrom(new[]
        {
            new RepositoryConfig { Name = "broken", Path = Path.Combine(_root, "not-a-repo-at-all") },
            new RepositoryConfig { Name = "repo", Path = _repo },
        });
        Directory.CreateDirectory(Path.Combine(_root, "not-a-repo-at-all"));

        RepoStatePushRequest? captured = null;
        var pusher = NewPusher((req, _) =>
        {
            captured = req;
            return Task.FromResult<RepoStatePushResponse?>(new RepoStatePushResponse { Stored = req.Repositories.Count });
        }, registry);

        Assert.Equal(1, await pusher.PushOnceAsync());
        // The healthy repository still went; the broken one was omitted, not pushed as an empty snapshot
        // that would read downstream as a clean bill of health.
        Assert.Equal(_repo, Assert.Single(captured!.Repositories).Path);
    }

    [Fact]
    public async Task With_no_registered_repositories_nothing_is_pushed_at_all()
    {
        var pushed = false;
        var pusher = NewPusher((_, _) =>
        {
            pushed = true;
            return Task.FromResult<RepoStatePushResponse?>(new RepoStatePushResponse());
        }, Registry(withRepo: false));

        Assert.Equal(0, await pusher.PushOnceAsync());
        Assert.False(pushed);
    }

    [Fact]
    public async Task An_install_with_no_Gateway_configured_simply_reports_nothing()
    {
        // The wiring passes a delegate that answers null when there is no Gateway client. That is not a
        // failure and must not be logged or retried as one - there is nowhere to report to.
        var pusher = NewPusher((_, _) => Task.FromResult<RepoStatePushResponse?>(null));

        Assert.Null(await pusher.PushOnceAsync());
    }

    [Fact]
    public void Dispose_before_Start_and_double_Dispose_are_both_safe()
    {
        var pusher = NewPusher((_, _) => Task.FromResult<RepoStatePushResponse?>(new RepoStatePushResponse()));
        pusher.Dispose();
        pusher.Dispose();
    }

    // ---- reporting ONE repository, when a session working in it parks ------------------------------

    [Fact]
    public async Task PushOneFolder_ReportsOnlyThatRepository()
    {
        RepoStatePushRequest? captured = null;
        var pusher = NewPusher((req, _) =>
        {
            captured = req;
            return Task.FromResult<RepoStatePushResponse?>(new RepoStatePushResponse { Stored = req.Repositories.Count });
        });

        var stored = await pusher.PushOneFolderAsync(_repo);

        Assert.Equal(1, stored);
        Assert.Equal(_repo, Assert.Single(captured!.Repositories).Path);
    }

    [Fact]
    public async Task PushOneFolder_FromASubfolderOfTheRepository_ReportsTheRepository()
    {
        var inside = Path.Combine(_repo, "src", "deep");
        Directory.CreateDirectory(inside);
        RepoStatePushRequest? captured = null;
        var pusher = NewPusher((req, _) =>
        {
            captured = req;
            return Task.FromResult<RepoStatePushResponse?>(new RepoStatePushResponse { Stored = req.Repositories.Count });
        });

        Assert.Equal(1, await pusher.PushOneFolderAsync(inside));
        Assert.Equal(_repo, Assert.Single(captured!.Repositories).Path);
    }

    /// <summary>
    /// A folder no registered repository covers reports NOTHING, and says so. Reporting the whole machine
    /// instead would turn a session parking into the six-hourly cycle's work, dozens of times a day.
    /// </summary>
    [Fact]
    public async Task PushOneFolder_AFolderNoRepositoryCovers_PushesNothing()
    {
        var pushes = 0;
        var pusher = NewPusher((_, _) =>
        {
            pushes++;
            return Task.FromResult<RepoStatePushResponse?>(new RepoStatePushResponse { Stored = 1 });
        });

        Assert.Null(await pusher.PushOneFolderAsync(Path.Combine(_root, "somewhere-else")));
        Assert.Equal(0, pushes);
    }

    [Fact]
    public async Task PushOneFolder_AGatewayThatRefuses_DoesNotThrow()
    {
        var pusher = NewPusher((_, _) => Task.FromResult<RepoStatePushResponse?>(null));

        Assert.Null(await pusher.PushOneFolderAsync(_repo));
    }

    [Fact]
    public async Task PushOneFolder_AGatewayThatThrows_DoesNotThrow()
    {
        var pusher = NewPusher((_, _) => throw new HttpRequestException("no route to the Gateway"));

        Assert.Null(await pusher.PushOneFolderAsync(_repo));
    }

    /// <summary>
    /// The longest covering registration wins, and a sibling whose name merely starts with a
    /// repository's name is not inside it. Both decide WHOSE report gets refreshed, and a wrong answer
    /// here would refresh the wrong row and leave the Close rule exactly as blind as before.
    /// </summary>
    [Fact]
    public void CoveringRepository_PicksTheLongestMatch_AndRefusesASibling()
    {
        var registry = new RepositoryRegistry(Path.Combine(_root, "many.json"));
        var outer = Path.Combine(_root, "work");
        var inner = Path.Combine(outer, "widgets");
        var sibling = Path.Combine(outer, "widgets-old");
        registry.SeedFrom(new[]
        {
            new RepositoryConfig { Name = "work", Path = outer },
            new RepositoryConfig { Name = "widgets", Path = inner },
        });
        var pusher = NewPusher((_, _) => Task.FromResult<RepoStatePushResponse?>(null), registry);

        Assert.Equal(inner, pusher.CoveringRepository(Path.Combine(inner, "src"))?.Path);
        Assert.Equal(inner, pusher.CoveringRepository(inner)?.Path);
        Assert.Equal(inner, pusher.CoveringRepository(inner + Path.DirectorySeparatorChar)?.Path);
        // Inside "work", but NOT inside "work/widgets" - the separator boundary is what tells them apart.
        Assert.Equal(outer, pusher.CoveringRepository(sibling)?.Path);
        Assert.Null(pusher.CoveringRepository(Path.Combine(_root, "elsewhere")));
        Assert.Null(pusher.CoveringRepository("   "));
        Assert.Null(pusher.CoveringRepository(null));
    }

    private static void RunGit(string workingDir, params string[] args)
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
        p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {stderr}");
    }
}
