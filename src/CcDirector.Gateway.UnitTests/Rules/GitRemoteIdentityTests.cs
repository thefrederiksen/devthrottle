using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Rules;

/// <summary>
/// A repository is recognised by its git remote (mission decision D3, Tech Lead ruling P6). Every form of
/// the same remote must come out as the same identity, because the Director computes it from a checkout and
/// the Gateway computes it from what the account pasted into a rule, and the two are compared as text.
/// </summary>
public sealed class GitRemoteIdentityTests
{
    private const string Expected = "github.com/thefrederiksen/devthrottle";

    [Theory]
    [InlineData("https://github.com/TheFrederiksen/devthrottle.git")]
    [InlineData("git@github.com:thefrederiksen/devthrottle.git")]
    [InlineData("ssh://git@github.com:22/thefrederiksen/devthrottle")]
    [InlineData("https://github.com/thefrederiksen/devthrottle")]
    [InlineData("https://github.com/thefrederiksen/devthrottle/")]
    [InlineData("https://github.com/thefrederiksen/devthrottle.git/")]
    [InlineData("http://github.com/thefrederiksen/devthrottle")]
    [InlineData("git://github.com/thefrederiksen/devthrottle.git")]
    [InlineData("https://soren@github.com/thefrederiksen/devthrottle.git")]
    [InlineData("https://soren:secret-token@github.com/thefrederiksen/devthrottle.git")]
    [InlineData("https://github.com:443/thefrederiksen/devthrottle.git")]
    [InlineData("github.com:thefrederiksen/devthrottle.git")]
    [InlineData("  GIT@GitHub.com:TheFrederiksen/DevThrottle.git \n")]
    [InlineData("github.com/thefrederiksen/devthrottle")]
    public void Normalise_AnyFormOfTheSameRemote_ReturnsOneIdentity(string remote)
        => Assert.Equal(Expected, GitRemoteIdentity.Normalise(remote));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalise_NoRemote_ReturnsEmpty(string? remote)
        => Assert.Equal("", GitRemoteIdentity.Normalise(remote));

    [Fact]
    public void Normalise_DifferentRepositories_StayDifferent()
        => Assert.NotEqual(
            GitRemoteIdentity.Normalise("git@github.com:thefrederiksen/devthrottle.git"),
            GitRemoteIdentity.Normalise("git@github.com:thefrederiksen/devthrottle_internal.git"));

    [Fact]
    public void Normalise_AzureDevOpsSshForm_KeepsTheWholePath()
        => Assert.Equal(
            "ssh.dev.azure.com/v3/org/project/repo",
            GitRemoteIdentity.Normalise("git@ssh.dev.azure.com:v3/Org/Project/Repo"));

    [Fact]
    public void Normalise_AlreadyNormalised_IsUnchanged()
        => Assert.Equal(Expected, GitRemoteIdentity.Normalise(GitRemoteIdentity.Normalise(Expected)));
}
