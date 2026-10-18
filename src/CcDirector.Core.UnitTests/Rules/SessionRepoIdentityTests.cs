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
public sealed class SessionRepoIdentityTests
{
    private const string Folder = "/work/devthrottle-worktree";

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
}
