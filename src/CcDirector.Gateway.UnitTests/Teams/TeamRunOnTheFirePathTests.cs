using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Teams;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// A TEAM'S STORED WORK STARTS ONLY ON A COMPUTER OF A MEMBER WHO MAY RUN THE TEAM'S FACTORIES (Team implementation 2.1,
/// review of the second commit). A schedule or a trigger written before the write-time check, or by a path it did not
/// see, fires with no caller; the one place every Gateway-started run goes through, <see cref="MachineSessionSpawner"/>,
/// asks the role table before the create (<see cref="MachineSessionSpawner.RefuseTeamRun"/>, built here exactly as the
/// host builds it, over a real team). Driven through the schedule's own starter, <see cref="DirectorCronSessionStarter"/>.
/// </summary>
public sealed class TeamRunOnTheFirePathTests : IDisposable
{
    private static readonly DateTime FiresAt = new(2026, 11, 9, 5, 0, 0, DateTimeKind.Utc);

    private readonly TeamMachineFixture _team = new();
    private readonly List<string> _createdOn = new();

    public void Dispose() => _team.Dispose();

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => FiresAt;
    }

    /// <summary>Resolves a computer to the team's Director on it, the way the registry does.</summary>
    private sealed class TeamResolver(TeamMachineFixture team) : IDirectorTargetResolver
    {
        public Task<DirectorTargetResult> ResolveAsync(string machine, string? director, CancellationToken ct) =>
            Task.FromResult(team.Directors.FirstOrDefault(d => d.MachineName == machine) is { DirectorId: { } id }
                ? new DirectorTargetResult(id, null)
                : new DirectorTargetResult(null, $"no Director on {machine}"));
    }

    private DirectorCronSessionStarter Starter()
    {
        var spawner = new MachineSessionSpawner(new TeamResolver(_team), (directorId, _, _) =>
        {
            _createdOn.Add(directorId);
            return Task.FromResult<(bool, SessionDto?, string?)>((true, new SessionDto { SessionId = "sid-1" }, null));
        })
        {
            RefuseTeamRun = directorId => _team.ForARun(
                _team.Directors.Single(d => d.DirectorId == directorId).MachineName, "a run the Gateway started"),
        };
        return new DirectorCronSessionStarter(spawner, new FixedClock(), refuseFactoryWork: _ => null,
            seatWithABrief: _ => null, definitionOf: _ => null, runsAsABoss: _ => false);
    }

    private static CronJobDto StoredJobOn(string machine) => new()
    {
        Id = "cj_team",
        Name = "Morning round",
        TimeZoneId = "UTC",
        ScheduleKind = "recurring",
        CronExpression = "0 5 * * *",
        Target = new CronJobTarget { Machine = machine },
        Action = new CronJobAction { RepoPath = @"C:\repo", Seed = "/help" },
    };

    [Fact]
    public async Task Start_AStoredScheduleOnTheOwnersComputer_IsStarted()
    {
        var (sessionId, directorId, error) = await Starter().StartAsync(StoredJobOn(TeamMachineFixture.OwnerPc), CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("sid-1", sessionId);
        Assert.Equal("director-owner", directorId);
        Assert.Equal(new[] { "director-owner" }, _createdOn);
    }

    [Fact]
    public async Task Start_AStoredScheduleOnADevelopersComputer_IsRefused_AndNothingIsCreated()
    {
        var (sessionId, _, error) = await Starter().StartAsync(StoredJobOn(TeamMachineFixture.DeveloperPc), CancellationToken.None);

        Assert.Null(sessionId);
        Assert.Contains(TeamMachineGuard.Refusal, error);
        Assert.Empty(_createdOn);
    }

    [Fact]
    public void RefuseRun_AComputerWithTheOwnersAndADevelopersDirector_IsRefused()
    {
        _team.EnrolledDirector(TeamMachineFixture.Developer, "director-developer-2", TeamMachineFixture.OwnerPc);

        var refusal = _team.ForARun(TeamMachineFixture.OwnerPc, "a run");

        Assert.Equal(TeamMachineGuard.Refusal, refusal);
    }

    [Fact]
    public void RefuseRun_AComputerWithNoDirectorOfTheTeam_IsRefused()
    {
        Assert.Equal(TeamMachineGuard.Refusal, _team.ForARun("NOBODYS-PC", "a run"));
    }

    [Fact]
    public void RefuseRun_NoComputerNamed_IsNotAskedHere()
    {
        Assert.Null(_team.ForARun(" ", "a run"));
    }
}
