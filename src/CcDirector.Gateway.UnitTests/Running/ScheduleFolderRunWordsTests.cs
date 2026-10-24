using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using SessionHistoryStore = CcDirector.Gateway.History.SessionHistoryStore;

namespace CcDirector.Gateway.Tests.Running;

/// <summary>
/// Issue #4421 review, finding 3: a run that did not start because its folder was missing names its machine's system on
/// the Factories screen too, which reads runs through <see cref="CronRunRecordReader.RecentRunsOf"/>. Fixed times only.
/// </summary>
public sealed class ScheduleFolderRunWordsTests : IDisposable
{
    private static readonly DateTime Fired = new(2026, 10, 23, 18, 5, 0, DateTimeKind.Utc);
    private const string Reason = @"it did not start a session: director returned BadRequest: repoPath does not exist: \\documents\\devthrottle";

    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static CronRunRecord NotStarted() => new()
    {
        ScheduledUtc = Fired, FiredUtc = Fired, Machine = "STUDIO_MAC", TargetDirectorId = "d-mac",
        InfraStatus = "not-started", TaskStatus = "unknown",
        Result = CronRunResults.Problem, Problem = CronRunProblems.DidNotRun, ResultReason = Reason,
    };

    [Fact]
    public void RecentRunsOf_AMissingFolderRun_NamesTheMachinesSystem_LikeTheSchedulePage()
    {
        var db = _h.Open();
        var runs = new CronRunHistoryStore(db, _h.LegacyPath("folder-words.runs.json"));
        runs.Append("job-mac", NotStarted());
        var directors = new[] { new DirectorDto { DirectorId = "d-mac", MachineName = "STUDIO_MAC", Os = DirectorOs.MacOs } };

        var recent = CronRunRecordReader.RecentRunsOf(runs, new SessionHistoryStore(db), TenantId.Local, new[] { "job-mac" },
            Fired.AddMinutes(1), machine => ScheduleFolderCheck.ReportedOs(directors, machine));

        Assert.Equal(@"did not start a session: '\\documents\\devthrottle' is a Windows path, but STUDIO_MAC is a Mac. "
                     + "Use a path like /Users/<you>/Documents/devthrottle. Then edit the schedule.",
            recent["job-mac"].Single().EndingText);
    }

    [Fact]
    public void ReportedOs_ReadsTheMachinesOwnDirectors_AndNoneIsNotKnown()
    {
        var directors = new[]
        {
            new DirectorDto { DirectorId = "d-1", MachineName = "STUDIO_MAC", Os = DirectorOs.MacOs },
            new DirectorDto { DirectorId = "d-2", MachineName = "OFFICE_PC", Os = DirectorOs.Windows },
        };

        Assert.Equal(DirectorOs.MacOs, ScheduleFolderCheck.ReportedOs(directors, "studio_mac"));
        Assert.Equal(DirectorOs.Windows, ScheduleFolderCheck.ReportedOs(directors, "OFFICE_PC"));
        Assert.Null(ScheduleFolderCheck.ReportedOs(directors, "NEW_LAPTOP"));
    }
}
