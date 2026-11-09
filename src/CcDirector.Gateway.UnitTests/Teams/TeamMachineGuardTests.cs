using CcDirector.Gateway.Teams;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// INSIDE A TEAM, WORK RUNS ONLY ON THE CALLER'S OWN COMPUTERS (Team implementation 2.1, review note N1): the one check
/// a schedule, a trigger, a factory talk and the folder choices ask, over a real team where the Owner's Director runs on
/// one computer and a Developer's on another. The route families are each driven through their own endpoints in
/// ScheduleFolderRoutesTests, BackupMachinesThroughTheRoutesTests and FactoryTalkEndpointTests.
/// </summary>
public sealed class TeamMachineGuardTests : IDisposable
{
    private readonly TeamMachineFixture _team = new();

    public void Dispose() => _team.Dispose();

    private string? AsOwner(string? machine) => _team.AsOwner(new DefaultHttpContext(), machine, "test");

    [Fact]
    public void Refuse_TheOwnerNamingTheirOwnComputer_IsAllowed()
    {
        Assert.Null(AsOwner(TeamMachineFixture.OwnerPc));
        // A computer name is matched as the Directors report it, whatever its case or spaces.
        Assert.Null(AsOwner(" owner-pc "));
    }

    [Fact]
    public void Refuse_TheOwnerNamingADevelopersEnrolledComputer_IsRefused()
    {
        Assert.Equal(TeamMachineGuard.Refusal, AsOwner(TeamMachineFixture.DeveloperPc));
    }

    [Fact]
    public void Refuse_AComputerNoDirectorOfTheTeamIsOn_GetsTheSameSentence_SoNothingSaysWhoseComputersExist()
    {
        var unknown = AsOwner("NO-SUCH-PC");
        var anothers = AsOwner(TeamMachineFixture.DeveloperPc);

        Assert.Equal(anothers, unknown);
        Assert.DoesNotContain(TeamMachineFixture.DeveloperPc, TeamMachineGuard.Refusal, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TeamMachineFixture.OwnerPc, TeamMachineGuard.Refusal, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Developer", TeamMachineGuard.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuse_AComputerTheOwnerSharesWithAnotherMembersDirector_IsRefused()
    {
        _team.EnrolledDirector(TeamMachineFixture.Developer, "director-developer-2", TeamMachineFixture.OwnerPc);

        Assert.Equal(TeamMachineGuard.Refusal, AsOwner(TeamMachineFixture.OwnerPc));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Refuse_ARequestThatNamesNoComputer_IsNotAsked(string? machine)
    {
        Assert.Null(AsOwner(machine));
    }

    [Fact]
    public void Refuse_APersonalAccount_IsNeverRefused()
    {
        // No TeamCaller: the request is in a personal account, answered exactly as before.
        Assert.Null(TeamMachineGuard.Refuse(null, TeamMachineFixture.DeveloperPc, _team.Directors, "test"));
    }
}
