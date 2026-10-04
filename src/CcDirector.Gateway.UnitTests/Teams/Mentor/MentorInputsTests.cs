using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Teams;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Mentor;

/// <summary>
/// The two things the Mentor reads besides the prompt log (devthrottle_internal#2305): the person on a team session,
/// and a team's member list for the Gateway's own background work.
/// </summary>
public sealed class MentorInputsTests : IDisposable
{
    private readonly MentorRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void PersonsWithSessions_NamesEachPersonWhoseSessionOverlapsTheWindow_InThatTeamOnly()
    {
        var (from, to) = MentorRig.Week.UtcBounds(MentorRig.Zone);
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(2), "s-rob-2");
        _rig.SessionOf(MentorRig.Dana, MentorRig.InWeek(-2), "s-dana-before");
        _rig.SessionOf(MentorRig.Manager, to, "s-priya-after");   // starts the instant the week ends: not in it
        var personal = _rig.Tenants.LookupBySubject(MentorRig.Owner)!.Value;
        _rig.SessionOf(MentorRig.Owner, MentorRig.InWeek(1), "s-personal", personal);

        var people = _rig.Sessions.PersonsWithSessions(_rig.Team, from, to);

        Assert.Equal(new[] { MentorRig.Rob }, people.ToArray());
    }

    [Fact]
    public void UpsertLive_ThePersonIsWriteOnce_AndUnknownNeverBlanksIt()
    {
        var (from, to) = MentorRig.Week.UtcBounds(MentorRig.Zone);
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-1");
        // The same session pushed again, the resolver naming nobody (the Director gone) and then someone else.
        _rig.SessionOf(null!, MentorRig.InWeek(1).AddMinutes(10), "s-1");
        _rig.SessionOf(MentorRig.Dana, MentorRig.InWeek(1).AddMinutes(20), "s-1");

        Assert.Equal(new[] { MentorRig.Rob }, _rig.Sessions.PersonsWithSessions(_rig.Team, from, to).ToArray());
    }

    [Fact]
    public void MembersOf_ListsEveryMemberWithTheirRoleAndEmail_AndNothingForAnUnknownTeam()
    {
        var members = _rig.Teams.MembersOf(_rig.Team.Value);

        Assert.Equal(5, members.Count);
        Assert.Equal(TeamRole.Owner, members[0].Role);
        Assert.Contains(members, m => m.AccountSubject == MentorRig.Rob && m.Email == "rob.keller@example.com" && m.Role == TeamRole.Developer);
        Assert.Empty(_rig.Teams.MembersOf(Guid.NewGuid().ToString()));
        Assert.Throws<ArgumentException>(() => _rig.Teams.MembersOf(" "));
    }
}
