using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Teams.Mentor;
using CcDirector.Gateway.Tenancy;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Mentor;

/// <summary>
/// The Mentor's weekly run (devthrottle_internal#2305): only teams, only once a week has closed and settled, only once,
/// and never at all with the switch off.
/// </summary>
public sealed class TeamMentorWeeklySweepTests : IDisposable
{
    private readonly MentorRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private TeamMentorWeeklySweep Sweep(bool enabled)
    {
        var ambient = new AsyncLocalTenantContext();
        var boundary = new HostedTenantBoundary(ambient, new CcDirector.Gateway.Pairing.DeviceRegistry(), hosted: false);
        return new TeamMentorWeeklySweep(enabled, boundary, _rig.Tenants, ambient, _rig.Teams, _rig.Store, _rig.Writer(),
            _ => "UTC", () => _rig.Now);
    }

    private void RobRanAWeek()
    {
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1), "a prompt");
        _rig.Brain.Answer = _ => FakeBrain.GoodWeek();
    }

    [Fact]
    public async Task SweepAsync_SwitchedOff_DoesNothing()
    {
        RobRanAWeek();

        await Sweep(enabled: false).SweepAsync();

        Assert.Empty(_rig.Brain.Asked);
        Assert.False(_rig.Store.HasRun(_rig.Team, MentorRig.Week));
        Assert.Empty(_rig.Store.Outcomes(_rig.Team, MentorRig.Week));
    }

    [Fact]
    public async Task SweepAsync_TheTeamsWeekHasClosedAndSettled_WritesIt_Once()
    {
        RobRanAWeek();
        var sweep = Sweep(enabled: true);

        await sweep.SweepAsync();
        await sweep.SweepAsync();

        Assert.True(_rig.Store.HasRun(_rig.Team, MentorRig.Week));
        Assert.Single(_rig.Store.Blocks(_rig.Team, MentorRig.Week));
        Assert.Single(_rig.Brain.Asked);
    }

    [Fact]
    public async Task SweepAsync_TheModelIsDownAtTheFirstTick_ALaterTickWritesTheWeek()
    {
        RobRanAWeek();
        _rig.Brain.Answer = _ => throw new HttpRequestException("down");
        var sweep = Sweep(enabled: true);
        await sweep.SweepAsync();
        Assert.False(_rig.Store.HasRun(_rig.Team, MentorRig.Week));

        _rig.Now = _rig.Now.AddMinutes(15);
        _rig.Brain.Answer = _ => FakeBrain.GoodWeek();
        await sweep.SweepAsync();

        Assert.True(_rig.Store.HasRun(_rig.Team, MentorRig.Week));
        Assert.Single(_rig.Store.Blocks(_rig.Team, MentorRig.Week));
    }

    [Fact]
    public async Task SweepAsync_TheModelIsStillDownOnceTheFollowingWeekHasClosed_TheWeekIsRecordedForGood()
    {
        RobRanAWeek();
        _rig.Brain.Answer = _ => throw new HttpRequestException("down");
        var sweep = Sweep(enabled: true);
        await sweep.SweepAsync();

        // A tick after the FOLLOWING week has closed and settled: the week is now the one before the last closed one.
        _rig.Now = MentorRig.Week.Next.UtcBounds(MentorRig.Zone).ToUtc + TeamMentorWeeklySweep.SettleDelay;
        await sweep.SweepAsync();

        Assert.True(_rig.Store.HasRun(_rig.Team, MentorRig.Week));
        Assert.Equal(MentorOutcomes.ModelFailed, _rig.Store.OutcomeOf(_rig.Team, MentorRig.Week, MentorRig.Rob)!.Outcome);
        Assert.Empty(_rig.Store.Blocks(_rig.Team, MentorRig.Week));
    }

    [Fact]
    public async Task SweepAsync_BeforeTheWeekHasSettled_WritesNothing()
    {
        RobRanAWeek();
        _rig.Now = MentorRig.Week.UtcBounds(MentorRig.Zone).ToUtc.AddMinutes(30);

        await Sweep(enabled: true).SweepAsync();

        Assert.Empty(_rig.Brain.Asked);
        Assert.False(_rig.Store.HasRun(_rig.Team, MentorRig.Week));
    }

    [Fact]
    public async Task SweepAsync_APersonalTenant_IsNeverVisited()
    {
        RobRanAWeek();
        var personal = _rig.Tenants.LookupBySubject(MentorRig.Rob)!.Value;

        await Sweep(enabled: true).SweepAsync();

        Assert.False(_rig.Store.HasRun(personal, MentorRig.Week));
        Assert.Empty(_rig.Store.Outcomes(personal, MentorRig.Week));
    }

    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    public void IsDue_AfterTheWeekClosesAndTheSettleDelay(int minutesAfterClose, bool due)
    {
        var closed = MentorRig.Week.UtcBounds(MentorRig.Zone).ToUtc;

        Assert.Equal(due, TeamMentorWeeklySweep.IsDue(MentorRig.Week, MentorRig.Zone, closed.AddMinutes(minutesAfterClose)));
    }
}
