using CcDirector.Gateway.Teams;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The four roles stack (devthrottle_internal#2098): each can do everything the roles below it can. These pin the
/// one comparison a permission check will ask, so the stacking rule cannot drift into a second place.
/// </summary>
public sealed class TeamRolesTests
{
    [Theory]
    [InlineData(TeamRole.Owner, TeamRole.Owner, true)]
    [InlineData(TeamRole.Owner, TeamRole.Manager, true)]
    [InlineData(TeamRole.Owner, TeamRole.Developer, true)]
    [InlineData(TeamRole.Owner, TeamRole.Collaborator, true)]
    [InlineData(TeamRole.Manager, TeamRole.Owner, false)]
    [InlineData(TeamRole.Manager, TeamRole.Manager, true)]
    [InlineData(TeamRole.Manager, TeamRole.Developer, true)]
    [InlineData(TeamRole.Developer, TeamRole.Manager, false)]
    [InlineData(TeamRole.Developer, TeamRole.Developer, true)]
    [InlineData(TeamRole.Developer, TeamRole.Collaborator, true)]
    [InlineData(TeamRole.Collaborator, TeamRole.Developer, false)]
    [InlineData(TeamRole.Collaborator, TeamRole.Collaborator, true)]
    public void IsAtLeast_EveryPair_FollowsTheStackingOrder(TeamRole held, TeamRole required, bool expected)
    {
        Assert.Equal(expected, TeamRoles.IsAtLeast(held, required));
    }

    [Fact]
    public void IsAtLeast_UndefinedRole_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TeamRoles.IsAtLeast((TeamRole)42, TeamRole.Developer));
        Assert.Throws<ArgumentOutOfRangeException>(() => TeamRoles.IsAtLeast(TeamRole.Owner, (TeamRole)(-1)));
    }

    [Theory]
    [InlineData(TeamRole.Owner, "Owner")]
    [InlineData(TeamRole.Manager, "Manager")]
    [InlineData(TeamRole.Developer, "Developer")]
    [InlineData(TeamRole.Collaborator, "Collaborator")]
    public void Label_EachRole_IsItsPlainName(TeamRole role, string expected)
    {
        Assert.Equal(expected, TeamRoles.Label(role));
    }

    [Theory]
    [InlineData(TeamRole.Owner, "owner")]
    [InlineData(TeamRole.Manager, "manager")]
    [InlineData(TeamRole.Developer, "developer")]
    [InlineData(TeamRole.Collaborator, "collaborator")]
    public void ToStored_EachRole_IsTheFixedLowerCaseWordTheWebsiteReads(TeamRole role, string stored)
    {
        Assert.Equal(stored, TeamRoles.ToStored(role));
        Assert.Equal(role, TeamRoles.FromStored(stored));
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("admin")]
    [InlineData("")]
    public void FromStored_AnythingButTheFourWords_Throws(string stored)
    {
        Assert.Throws<InvalidOperationException>(() => TeamRoles.FromStored(stored));
    }

    [Fact]
    public void Label_UndefinedRole_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TeamRoles.Label((TeamRole)9));
    }
}
