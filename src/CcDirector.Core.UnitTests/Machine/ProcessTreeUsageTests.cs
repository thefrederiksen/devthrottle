using CcDirector.Core.Machine;
using Xunit;
using static CcDirector.Core.Tests.Machine.ResourceSampleBuilder;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// What each session costs the machine: its root process and every descendant, found by parent
/// process id, guarded against reused process ids, and never counting a process twice.
/// </summary>
[Trait("Category", "Resources")]
public class ProcessTreeUsageTests
{
    private static ProcessReading Process(int pid, int? parent, double cpu = 1, ulong memory = 100, double startMinutesBeforeNoon = 60, string name = "p") =>
        new(pid, parent, name, Start.AddMinutes(-startMinutesBeforeNoon), cpu, memory, null, null);

    private static ProcessReading ProcessWithoutStart(int pid, int? parent, double cpu = 1, ulong memory = 100) =>
        new(pid, parent, "p", null, cpu, memory, null, null);

    private static IReadOnlyList<SessionUsage> Usage(IReadOnlyDictionary<string, int> roots, params ProcessReading[] processes) =>
        ProcessTreeUsage.BySession(ProcessSetReading.Read(processes), roots);

    [Fact]
    public void BySession_RootWithChildrenAndGrandchildren_SumsTheWholeTree()
    {
        var usage = Usage(new Dictionary<string, int> { ["140"] = 10 },
            Process(1, null, cpu: 50, memory: 9_999),
            Process(10, 1, cpu: 2, memory: 100, startMinutesBeforeNoon: 60),
            Process(11, 10, cpu: 30, memory: 1_000, startMinutesBeforeNoon: 30),
            Process(12, 11, cpu: 5, memory: 500, startMinutesBeforeNoon: 10),
            Process(20, 1, cpu: 40, memory: 7_000, startMinutesBeforeNoon: 90));

        var session = Assert.Single(usage);
        Assert.Equal("140", session.SessionId);
        Assert.Equal(10, session.RootPid);
        Assert.Equal(37, session.CpuPercent, precision: 6);
        Assert.Equal(1_600UL, session.MemoryBytes);
        Assert.Equal(new[] { 10, 11, 12 }, session.Pids);
    }

    [Fact]
    public void BySession_RootOnly_CountsTheRoot()
    {
        var session = Assert.Single(Usage(new Dictionary<string, int> { ["a"] = 10 }, Process(10, 1, cpu: 3, memory: 70)));

        Assert.Equal(new[] { 10 }, session.Pids);
        Assert.Equal(3, session.CpuPercent);
        Assert.Equal(70UL, session.MemoryBytes);
    }

    [Fact]
    public void BySession_ReusedParentId_ChildThatStartedBeforeItsParentIsRefused()
    {
        // Process 50 started 90 minutes ago, when an older process 10 was its parent. That process died and
        // the id 10 was reused by the session's agent, started 60 minutes ago. 50 is not the session's.
        var usage = Usage(new Dictionary<string, int> { ["140"] = 10 },
            Process(10, 1, cpu: 1, startMinutesBeforeNoon: 60),
            Process(50, 10, cpu: 80, startMinutesBeforeNoon: 90),
            Process(51, 50, cpu: 5, startMinutesBeforeNoon: 5));

        var session = Assert.Single(usage);
        Assert.Equal(new[] { 10 }, session.Pids);
        Assert.Equal(1, session.CpuPercent);
    }

    [Fact]
    public void BySession_ChildStartedTheSameMomentAsItsParent_IsCounted()
    {
        var session = Assert.Single(Usage(new Dictionary<string, int> { ["a"] = 10 },
            Process(10, 1, startMinutesBeforeNoon: 5),
            Process(11, 10, startMinutesBeforeNoon: 5)));

        Assert.Equal(new[] { 10, 11 }, session.Pids);
    }

    [Fact]
    public void BySession_NoStartTimesOffered_FollowsTheParentIds()
    {
        var session = Assert.Single(Usage(new Dictionary<string, int> { ["a"] = 10 },
            ProcessWithoutStart(10, 1),
            ProcessWithoutStart(11, 10),
            ProcessWithoutStart(12, 11)));

        Assert.Equal(new[] { 10, 11, 12 }, session.Pids);
    }

    [Fact]
    public void BySession_NestedSessions_EachProcessGoesToItsNearestRootAndIsCountedOnce()
    {
        // Session "outer" (root 10) started session "inner" (root 11) as its child.
        var usage = Usage(new Dictionary<string, int> { ["outer"] = 10, ["inner"] = 11 },
            Process(10, 1, cpu: 1, startMinutesBeforeNoon: 60),
            Process(11, 10, cpu: 2, startMinutesBeforeNoon: 50),
            Process(12, 11, cpu: 4, startMinutesBeforeNoon: 40),
            Process(13, 10, cpu: 8, startMinutesBeforeNoon: 30));

        var outer = usage.Single(session => session.SessionId == "outer");
        var inner = usage.Single(session => session.SessionId == "inner");
        Assert.Equal(new[] { 10, 13 }, outer.Pids);
        Assert.Equal(new[] { 11, 12 }, inner.Pids);
        Assert.Empty(outer.Pids.Intersect(inner.Pids));
    }

    [Fact]
    public void BySession_TwoSessionsClaimingOneRoot_CountTheTreeOnce()
    {
        var usage = Usage(new Dictionary<string, int> { ["b"] = 10, ["a"] = 10 },
            Process(10, 1),
            Process(11, 10, startMinutesBeforeNoon: 1));

        var session = Assert.Single(usage);
        Assert.Equal("a", session.SessionId);
        Assert.Equal(new[] { 10, 11 }, session.Pids);
    }

    [Fact]
    public void BySession_RootNotRunning_LeavesTheSessionOutRatherThanZero()
    {
        var usage = Usage(new Dictionary<string, int> { ["gone"] = 999, ["here"] = 10 }, Process(10, 1));

        Assert.Equal(new[] { "here" }, usage.Select(session => session.SessionId));
    }

    [Fact]
    public void BySession_UnreadableProcessList_IsEmpty()
    {
        var usage = ProcessTreeUsage.BySession(
            ProcessSetReading.Unreadable(ResourceUnreadableReasons.FirstSample),
            new Dictionary<string, int> { ["140"] = 10 });

        Assert.Empty(usage);
    }

    [Fact]
    public void BySession_NoSessions_IsEmpty()
    {
        Assert.Empty(Usage(new Dictionary<string, int>(), Process(10, 1)));
    }

    [Fact]
    public void BySession_ParentLoop_DoesNotHangAndCountsOnlyWhatReachesARoot()
    {
        // 30 and 31 name each other as parent - a torn read. Neither reaches the session's root.
        var session = Assert.Single(Usage(new Dictionary<string, int> { ["a"] = 10 },
            ProcessWithoutStart(10, 1),
            ProcessWithoutStart(30, 31),
            ProcessWithoutStart(31, 30),
            ProcessWithoutStart(32, 32)));

        Assert.Equal(new[] { 10 }, session.Pids);
    }

    [Fact]
    public void BySession_ParentMissingFromTheList_IsNotCounted()
    {
        var session = Assert.Single(Usage(new Dictionary<string, int> { ["a"] = 10 },
            Process(10, 1),
            Process(40, 39, startMinutesBeforeNoon: 1)));

        Assert.Equal(new[] { 10 }, session.Pids);
    }

    [Fact]
    public void BySession_DuplicateProcessIdInTheList_IsCountedOnce()
    {
        var session = Assert.Single(Usage(new Dictionary<string, int> { ["a"] = 10 },
            Process(10, 1, cpu: 3),
            Process(10, 1, cpu: 3)));

        Assert.Equal(new[] { 10 }, session.Pids);
        Assert.Equal(3, session.CpuPercent);
    }

    [Fact]
    public void BySession_SeveralSessions_AreReturnedInSessionIdOrder()
    {
        var usage = Usage(new Dictionary<string, int> { ["z"] = 30, ["a"] = 20, ["m"] = 10 },
            Process(10, 1), Process(20, 1), Process(30, 1));

        Assert.Equal(new[] { "a", "m", "z" }, usage.Select(session => session.SessionId));
    }

    [Fact]
    public void BySession_DevlinuxFixture_SessionOneFortyOwnsTheTestRun()
    {
        var processes = Devlinux(TimeSpan.Zero).Build().Processes;

        var session = Assert.Single(ProcessTreeUsage.BySession(processes, new Dictionary<string, int> { ["140"] = 4000 }));

        Assert.Equal(new[] { 4000, 4100 }, session.Pids);
        Assert.Equal(2_600_000_000UL, session.MemoryBytes);
    }
}
