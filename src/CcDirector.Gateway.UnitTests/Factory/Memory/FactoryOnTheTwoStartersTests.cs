using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Memory;

/// <summary>
/// THE TWO WAYS A FACTORY'S OWN SESSIONS ARE BORN (Factory Memory mission, phase 1; the mission document's
/// section 7: "a trigger session and a schedule session each carry their factory").
///
/// Neither of these goes through a spawn door: both starters live inside the Gateway and hand a create straight
/// to the spawner, so the factory they stamp is the Gateway's own statement about a session it is starting
/// itself. That is what makes the gate on naming a trigger's or a schedule's factory the load-bearing check -
/// there is no second opinion after this point.
///
/// The schedule matters most and is the one that did not exist before this mission: the Website Factory's Scout
/// runs on a 07:00 schedule, so without a factory here the agent with the most to remember would be born
/// outside its factory every morning.
/// </summary>
[Trait("Category", "FactoryMemory")]
public sealed class FactoryOnTheTwoStartersTests
{
    private const string TheFactory = "website-factory";

    private sealed class StubResolver : IDirectorTargetResolver
    {
        public Task<DirectorTargetResult> ResolveAsync(string machine, string? director, CancellationToken ct) =>
            Task.FromResult(new DirectorTargetResult("d-1", null));
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => new(2026, 9, 28, 5, 0, 0, DateTimeKind.Utc);
    }

    private static (DirectorCronSessionStarter starter, Func<NewSessionRequest?> seen) BuildStarter()
    {
        NewSessionRequest? captured = null;
        var spawner = new MachineSessionSpawner(new StubResolver(), (directorId, req, ct) =>
        {
            captured = req;
            return Task.FromResult<(bool, SessionDto?, string?)>((true, new SessionDto { SessionId = "sid-1" }, null));
        });
        return (new DirectorCronSessionStarter(spawner, new FixedClock()), () => captured);
    }

    private static CronJobDto Job(string? factory) => new()
    {
        Id = "cj_1",
        Name = "Scout",
        TimeZoneId = "UTC",
        ScheduleKind = "recurring",
        CronExpression = "0 7 * * *",
        Factory = factory,
        Target = new CronJobTarget { Machine = "MACHINE_A" },
        Action = new CronJobAction { RepoPath = @"C:\repo", Seed = "/scout" },
    };

    [Fact]
    public async Task A_SCHEDULED_SESSION_IS_BORN_INTO_THE_SCHEDULES_FACTORY()
    {
        var (starter, seen) = BuildStarter();

        var (sessionId, _, error) = await starter.StartAsync(Job(TheFactory), CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("sid-1", sessionId);
        Assert.Equal(TheFactory, seen()!.Factory);
    }

    [Fact]
    public async Task A_schedule_with_no_factory_starts_a_session_in_no_factory()
    {
        // Every schedule that exists today is this one, so this is the case that says nothing changed for them.
        var (starter, seen) = BuildStarter();

        await starter.StartAsync(Job(factory: null), CancellationToken.None);

        Assert.Null(seen()!.Factory);
    }

    [Fact]
    public async Task The_scheduled_session_is_still_recorded_as_a_SCHEDULE_origin_not_an_agents_spawn()
    {
        // Guarding the neighbouring fact: the factory rides beside the origin and must not disturb it. A cron
        // firing is neither a person nor an agent, and recording it as either corrupts the one number the origin
        // exists to answer.
        var (starter, seen) = BuildStarter();

        await starter.StartAsync(Job(TheFactory), CancellationToken.None);

        Assert.Equal(Core.Sessions.SessionOriginKinds.Schedule, seen()!.Origin);
        Assert.Equal(Core.Sessions.SessionOriginSurfaces.Cron, seen()!.OriginSurface);
    }
}
