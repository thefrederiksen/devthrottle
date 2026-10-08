using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="DirectorCronSessionStarter"/> - the cron seam that builds the
/// <see cref="NewSessionRequest"/> for a due job and hands it to the shared
/// <see cref="MachineSessionSpawner"/>. The behavior under test is the naming rule: a fired schedule
/// spawns a session NAMED after the schedule plus when it ran (e.g. "Daily Error Triage -
/// 2026-07-24 05:00"), stamped by the Gateway rather than the seeded agent (CLAUDE.md rule 7). A stub
/// resolver + capturing create delegate stand in for the live registry/Director, and a fixed clock
/// pins the timestamp, so the request the starter builds is asserted without a live Director.
/// </summary>
public sealed class DirectorCronSessionStarterTests
{
    private sealed class StubResolver : IDirectorTargetResolver
    {
        private readonly DirectorTargetResult _result;
        public StubResolver(DirectorTargetResult result) => _result = result;
        public Task<DirectorTargetResult> ResolveAsync(string machine, string? director, CancellationToken ct) =>
            Task.FromResult(_result);
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTime utcNow) => UtcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        public DateTime UtcNow { get; }
    }

    private static (DirectorCronSessionStarter starter, Func<NewSessionRequest?> seenReq) Build(DateTime utcNow)
    {
        NewSessionRequest? captured = null;
        var resolver = new StubResolver(new DirectorTargetResult("d-1", null));
        var spawner = new MachineSessionSpawner(resolver, (directorId, req, ct) =>
        {
            captured = req;
            return Task.FromResult<(bool, SessionDto?, string?)>((true, new SessionDto { SessionId = "sid-1" }, null));
        });
        var starter = new DirectorCronSessionStarter(spawner, new FixedClock(utcNow), refuseFactoryWork: _ => null);
        return (starter, () => captured);
    }

    private static CronJobDto Job(string name, string timeZoneId) => new()
    {
        Id = "cj_1",
        Name = name,
        TimeZoneId = timeZoneId,
        ScheduleKind = "recurring",
        CronExpression = "0 5 * * *",
        Target = new CronJobTarget { Machine = "MACHINE_A" },
        Action = new CronJobAction { RepoPath = @"C:\repo", Seed = "/help" },
    };

    [Fact]
    public async Task Start_AJobWhoseFactorySeatIsNotRegistered_NeverReachesAMachine_AndSaysWhy()
    {
        // Issue #3650: the last door. A link that stopped validating after it was written (a seat dropped while its
        // schedule was off, then switched back on) must not start factory work as a seat nobody registered.
        var spawned = false;
        var spawner = new MachineSessionSpawner(new StubResolver(new DirectorTargetResult("d-1", null)), (_, _, _) =>
        {
            spawned = true;
            return Task.FromResult<(bool, SessionDto?, string?)>((true, new SessionDto { SessionId = "sid-1" }, null));
        });
        var job = Job("Mail Desk - morning", "UTC");
        job.Factory = "devthrottle";
        job.Seat = "mail-desk";
        var starter = new DirectorCronSessionStarter(spawner, new FixedClock(DateTime.UtcNow),
            refuseFactoryWork: j => j.Seat == "mail-desk" ? "'mail-desk' is not a seat of DevThrottle" : null);

        var (sessionId, directorId, error) = await starter.StartAsync(job, CancellationToken.None);

        Assert.False(spawned);
        Assert.Null(sessionId);
        Assert.Null(directorId);
        Assert.Equal("not started: 'mail-desk' is not a seat of DevThrottle", error);
    }

    [Fact]
    public async Task Start_NamesSessionAfterScheduleAndLocalFireTime()
    {
        // 05:00 UTC on a US-Eastern job is 01:00 local - the label uses the JOB's zone, not UTC.
        var (starter, seenReq) = Build(new DateTime(2026, 7, 24, 5, 0, 0, DateTimeKind.Utc));

        var (sessionId, _, error) = await starter.StartAsync(
            Job("Daily Error Triage", "Eastern Standard Time"), CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("sid-1", sessionId);
        var req = seenReq();
        Assert.NotNull(req);
        Assert.Equal("Daily Error Triage - 2026-07-24 01:00", req!.Name);
    }

    [Fact]
    public async Task Start_UtcJob_NamesSessionWithUtcWallClock()
    {
        var (starter, seenReq) = Build(new DateTime(2026, 7, 24, 5, 0, 0, DateTimeKind.Utc));

        await starter.StartAsync(Job("Nightly Backup", "UTC"), CancellationToken.None);

        Assert.Equal("Nightly Backup - 2026-07-24 05:00", seenReq()!.Name);
    }
}
