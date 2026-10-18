using CcDirector.Core.Git;
using CcDirector.Core.Rules;
using Xunit;

namespace CcDirector.Core.Tests.Rules;

/// <summary>
/// Session Rules part 2, phase 1: a session's repo identity is its checkout's <c>origin</c> remote, in the
/// one spelling <c>GitRemoteIdentity.Normalise</c> gives (ruling P6). "This checkout has no origin" is a
/// real answer (only machine-wide and everywhere rules reach it); "git could not be asked" is not an
/// answer at all, and the two must never be confused.
/// </summary>
public sealed class SessionRepoIdentityTests : IDisposable
{
    private const string Folder = "/work/devthrottle-worktree";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccd-repo-identity-" + Guid.NewGuid().ToString("N"));

    public SessionRepoIdentityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static GitCommandResult Fatal(string error) => new() { Success = false, ExitCode = 128, Error = error };

    private const string NotARepository = "fatal: not a git repository (or any of the parent directories): .git";

    [Theory]
    [InlineData("https://github.com/TheFrederiksen/devthrottle.git")]
    [InlineData("git@github.com:thefrederiksen/devthrottle.git")]
    [InlineData("ssh://git@github.com:22/thefrederiksen/devthrottle")]
    public async Task ResolveAsync_OriginRemote_ReturnsItsNormalisedForm(string url)
    {
        // Arrange
        var git = new FakeOriginGit();
        git.Origin(Folder, url);

        // Act
        var identity = await SessionRepoIdentity.ResolveAsync(git, Folder, CancellationToken.None);

        // Assert
        Assert.Null(identity.Failure);
        Assert.Equal("github.com/thefrederiksen/devthrottle", identity.Repo);
    }

    [Fact]
    public async Task ResolveAsync_AsksGitForOriginInTheGivenFolder()
    {
        // Arrange
        var git = new FakeOriginGit();
        git.Origin(Folder, "https://github.com/thefrederiksen/devthrottle");

        // Act
        await SessionRepoIdentity.ResolveAsync(git, Folder, CancellationToken.None);

        // Assert
        Assert.Equal(Folder + " :: remote get-url origin", Assert.Single(git.Asked));
    }

    [Fact]
    public async Task ResolveAsync_CheckoutWithNoOrigin_ReturnsTheEmptyIdentity()
    {
        // Arrange: git exits 2, "No such remote 'origin'".
        var git = new FakeOriginGit();

        // Act
        var identity = await SessionRepoIdentity.ResolveAsync(git, Folder, CancellationToken.None);

        // Assert
        Assert.Null(identity.Failure);
        Assert.Equal("", identity.Repo);
    }

    [Fact]
    public async Task ResolveAsync_FolderThatIsNotAGitRepository_ReturnsTheEmptyIdentity()
    {
        // Arrange
        var git = new FakeOriginGit();
        git.Answers[Folder] = new GitCommandResult { Success = false, ExitCode = 128, Error = "fatal: not a git repository" };

        // Act
        var identity = await SessionRepoIdentity.ResolveAsync(git, Folder, CancellationToken.None);

        // Assert
        Assert.Null(identity.Failure);
        Assert.Equal("", identity.Repo);
    }

    [Theory]
    [InlineData(-1, "git is not installed")]
    [InlineData(1, "something else went wrong")]
    public async Task ResolveAsync_GitCouldNotAnswer_FailsWithItsReason(int exitCode, string error)
    {
        // Arrange
        var git = new FakeOriginGit();
        git.Answers[Folder] = new GitCommandResult { Success = false, ExitCode = exitCode, Error = error };

        // Act
        var identity = await SessionRepoIdentity.ResolveAsync(git, Folder, CancellationToken.None);

        // Assert
        Assert.Null(identity.Repo);
        Assert.Contains(error, identity.Failure);
    }

    [Fact]
    public async Task ResolveAsync_Exit128InAPlainFolderWithNoGitAnywhereAbove_ReturnsTheEmptyIdentity()
    {
        // Arrange: a real folder, nothing named .git in it or any parent.
        var folder = Path.Combine(_dir, "plain");
        Directory.CreateDirectory(folder);
        var git = new FakeOriginGit();
        git.Answers[folder] = Fatal(NotARepository);

        // Act
        var identity = await SessionRepoIdentity.ResolveAsync(git, folder, CancellationToken.None);

        // Assert
        Assert.Null(identity.Failure);
        Assert.Equal("", identity.Repo);
    }

    [Fact]
    public async Task ResolveAsync_Exit128InAWorktreeWhoseParentIsGone_Fails()
    {
        // Arrange: a worktree keeps a .git FILE pointing at its parent; git refuses once the parent goes.
        var folder = Path.Combine(_dir, "orphaned-worktree");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ".git"), "gitdir: /gone/main/.git/worktrees/wt");
        var git = new FakeOriginGit();
        git.Answers[folder] = Fatal("fatal: not a git repository: /gone/main/.git/worktrees/wt");

        // Act
        var identity = await SessionRepoIdentity.ResolveAsync(git, folder, CancellationToken.None);

        // Assert: "no repo" here would silently stop every repo rule reaching the session.
        Assert.Null(identity.Repo);
        Assert.Contains("/gone/main/.git/worktrees/wt", identity.Failure);
    }

    [Fact]
    public async Task ResolveAsync_Exit128BelowAFolderThatHoldsAGitEntry_Fails()
    {
        // Arrange: the .git is in a parent - a broken repository, not a plain folder.
        Directory.CreateDirectory(Path.Combine(_dir, "broken", ".git"));
        var folder = Path.Combine(_dir, "broken", "src", "deep");
        Directory.CreateDirectory(folder);
        var git = new FakeOriginGit();
        git.Answers[folder] = Fatal(NotARepository);

        // Act
        var identity = await SessionRepoIdentity.ResolveAsync(git, folder, CancellationToken.None);

        // Assert
        Assert.Null(identity.Repo);
        Assert.NotNull(identity.Failure);
    }

    [Fact]
    public async Task ResolveAsync_Exit128ForAnythingButNotARepository_Fails()
    {
        // Arrange: git uses 128 for every fatal error - here the Windows ownership refusal.
        var folder = Path.Combine(_dir, "someone-elses-clone");
        Directory.CreateDirectory(folder);
        var git = new FakeOriginGit();
        git.Answers[folder] = Fatal("fatal: detected dubious ownership in repository at '" + folder + "'");

        // Act
        var identity = await SessionRepoIdentity.ResolveAsync(git, folder, CancellationToken.None);

        // Assert
        Assert.Null(identity.Repo);
        Assert.Contains("dubious ownership", identity.Failure);
    }
}
