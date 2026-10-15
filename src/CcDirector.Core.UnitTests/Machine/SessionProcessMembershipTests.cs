using CcDirector.Core.Machine;
using Xunit;
using static CcDirector.Core.Tests.Machine.ResourceSampleBuilder;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// Which session each process belongs to, remembered across samples, so a child that detaches from its
/// parent is still counted against the session that started it (phase 1 Windows finding). Each step is a
/// pure update - previous membership, new process list, roots in; new membership out - so the "time"
/// here is nothing but the order the test applies the updates in.
/// </summary>
[Trait("Category", "Resources")]
public class SessionProcessMembershipTests
{
    private static readonly IReadOnlyDictionary<string, int> Session140 = new Dictionary<string, int> { ["140"] = 10 };

    private static ProcessReading Process(int pid, int? parent, double startMinutesBeforeNoon, double cpu = 1, ulong memory = 100) =>
        new(pid, parent, $"process-{pid}", Start.AddMinutes(-startMinutesBeforeNoon), cpu, memory, null, null);

    private static ProcessSetReading List(params ProcessReading[] processes) => ProcessSetReading.Read(processes);

    private static SessionProcessMembership Step(SessionProcessMembership previous, IReadOnlyDictionary<string, int> roots, params ProcessReading[] processes) =>
        SessionProcessMembership.Update(previous, List(processes), roots);

    [Fact]
    public void Update_FromEmpty_HoldsWhatTheParentWalkFinds()
    {
        var membership = Step(SessionProcessMembership.Empty, Session140,
            Process(10, 1, 60), Process(11, 10, 30), Process(20, 1, 90));

        Assert.Equal(2, membership.Count);
        Assert.Equal("140", membership.SessionOf(new ProcessIdentity(11, Start.AddMinutes(-30))));
        Assert.Null(membership.SessionOf(new ProcessIdentity(20, Start.AddMinutes(-90))));
    }

    [Fact]
    public void Update_DetachedChildOutlivesItsParent_StaysWithTheSession()
    {
        // Sample 1: the session's agent (10) started a shell (11), which started a test run (12).
        var first = Step(SessionProcessMembership.Empty, Session140,
            Process(10, 1, 60), Process(11, 10, 30), Process(12, 11, 10, cpu: 70));

        // Sample 2: the shell exited. On Windows the test run still names parent 11, which is gone; on
        // Linux it has been handed to process 1. Either way the parent walk no longer reaches the session.
        var windowsStyle = Step(first, Session140, Process(10, 1, 60), Process(12, 11, 10, cpu: 70));
        var linuxStyle = Step(first, Session140, Process(1, null, 6000), Process(10, 1, 60), Process(12, 1, 10, cpu: 70));

        Assert.Equal("140", windowsStyle.SessionOf(new ProcessIdentity(12, Start.AddMinutes(-10))));
        Assert.Equal("140", linuxStyle.SessionOf(new ProcessIdentity(12, Start.AddMinutes(-10))));

        var usage = Assert.Single(linuxStyle.Usage(List(Process(1, null, 6000), Process(10, 1, 60), Process(12, 1, 10, cpu: 70)), Session140));
        Assert.Equal(new[] { 10, 12 }, usage.Pids);
        Assert.Equal(71, usage.CpuPercent);
    }

    [Fact]
    public void Update_DetachedChildOutlivesTheRootItself_IsStillCountedWhileTheSessionIsKnown()
    {
        var first = Step(SessionProcessMembership.Empty, Session140, Process(10, 1, 60), Process(12, 10, 10, cpu: 40));

        var second = Step(first, Session140, Process(12, 1, 10, cpu: 40));

        var usage = Assert.Single(second.Usage(List(Process(12, 1, 10, cpu: 40)), Session140));
        Assert.Equal("140", usage.SessionId);
        Assert.Equal(10, usage.RootPid);
        Assert.Equal(new[] { 12 }, usage.Pids);
    }

    [Fact]
    public void Update_SessionNoLongerListed_ForgetsItsMembers()
    {
        var first = Step(SessionProcessMembership.Empty, Session140, Process(10, 1, 60), Process(12, 10, 10));

        var second = Step(first, new Dictionary<string, int>(), Process(12, 1, 10));

        Assert.Equal(0, second.Count);
    }

    [Fact]
    public void Update_MemberExited_IsForgotten()
    {
        var first = Step(SessionProcessMembership.Empty, Session140, Process(10, 1, 60), Process(12, 10, 10));

        var second = Step(first, Session140, Process(10, 1, 60));

        Assert.Null(second.SessionOf(new ProcessIdentity(12, Start.AddMinutes(-10))));
        Assert.Equal(1, second.Count);
    }

    [Fact]
    public void Update_MemberIdReusedByANewerProcess_IsForgottenAndTheNewcomerIsNotTheSessions()
    {
        var first = Step(SessionProcessMembership.Empty, Session140, Process(10, 1, 60), Process(12, 10, 10));

        // Process 12 exited and the id was reused by an unrelated process started a minute ago.
        var second = Step(first, Session140, Process(10, 1, 60), Process(12, 1, 1));

        Assert.Null(second.SessionOf(new ProcessIdentity(12, Start.AddMinutes(-10))));
        Assert.Null(second.SessionOf(new ProcessIdentity(12, Start.AddMinutes(-1))));
        var usage = Assert.Single(second.Usage(List(Process(10, 1, 60), Process(12, 1, 1)), Session140));
        Assert.Equal(new[] { 10 }, usage.Pids);
    }

    [Fact]
    public void Update_ReusedParentId_ChildOlderThanTheParentIsRefused()
    {
        // 50 started 90 minutes ago under an older process 10 that died; the session's agent reused id 10.
        var membership = Step(SessionProcessMembership.Empty, Session140, Process(10, 1, 60), Process(50, 10, 90));

        Assert.Null(membership.SessionOf(new ProcessIdentity(50, Start.AddMinutes(-90))));
        Assert.Equal(1, membership.Count);
    }

    [Fact]
    public void Update_RememberedMemberNowUnderAnotherSessionsTree_StaysWithTheFirstAndIsCountedOnce()
    {
        var roots = new Dictionary<string, int> { ["140"] = 10, ["141"] = 30 };
        var first = Step(SessionProcessMembership.Empty, roots, Process(10, 1, 60), Process(30, 1, 50), Process(12, 10, 10));

        // A torn read or a reparenting puts 12 under session 141's root.
        var processes = new[] { Process(10, 1, 60), Process(30, 1, 50), Process(12, 30, 10) };
        var second = Step(first, roots, processes);

        Assert.Equal("140", second.SessionOf(new ProcessIdentity(12, Start.AddMinutes(-10))));
        var usage = second.Usage(List(processes), roots);
        Assert.Equal(new[] { 10, 12 }, usage.Single(session => session.SessionId == "140").Pids);
        Assert.Equal(new[] { 30 }, usage.Single(session => session.SessionId == "141").Pids);
    }

    [Fact]
    public void Update_UnreadableProcessList_ChangesNothingButEndedSessions()
    {
        var roots = new Dictionary<string, int> { ["140"] = 10, ["141"] = 30 };
        var first = Step(SessionProcessMembership.Empty, roots, Process(10, 1, 60), Process(12, 10, 10), Process(30, 1, 50));

        var kept = SessionProcessMembership.Update(first, ProcessSetReading.Unreadable("could not list /proc"), roots);
        var ended = SessionProcessMembership.Update(first, ProcessSetReading.Unreadable("could not list /proc"), Session140);

        Assert.Equal(3, kept.Count);
        Assert.Equal(2, ended.Count);
        Assert.Null(ended.SessionOf(new ProcessIdentity(30, Start.AddMinutes(-50))));
    }

    [Fact]
    public void Usage_UnreadableProcessList_IsEmpty()
    {
        var membership = Step(SessionProcessMembership.Empty, Session140, Process(10, 1, 60));

        Assert.Empty(membership.Usage(ProcessSetReading.Unreadable(ResourceUnreadableReasons.FirstSample), Session140));
    }

    [Fact]
    public void Usage_SessionWithNoLiveProcess_IsLeftOut()
    {
        var membership = Step(SessionProcessMembership.Empty, Session140, Process(10, 1, 60));

        Assert.Empty(membership.Usage(List(Process(99, 1, 5)), Session140));
    }

    [Fact]
    public void Update_NoStartTimesOffered_RemembersByIdAlone()
    {
        var withoutStart = new Func<int, int?, ProcessReading>((pid, parent) => new ProcessReading(pid, parent, "p", null, 1, 100, null, null));
        var first = SessionProcessMembership.Update(SessionProcessMembership.Empty, List(withoutStart(10, 1), withoutStart(12, 10)), Session140);

        var second = SessionProcessMembership.Update(first, List(withoutStart(10, 1), withoutStart(12, 1)), Session140);

        Assert.Equal("140", second.SessionOf(new ProcessIdentity(12, null)));
    }

    [Fact]
    public void Diagnose_WithAKeptMembership_NamesTheSessionOfADetachedTestRun()
    {
        // The test run (4100) was seen under session 140's agent (4000), then its parent link was cut.
        var seen = Devlinux(TimeSpan.Zero).Build().Processes;
        var membership = SessionProcessMembership.Update(SessionProcessMembership.Empty, seen, new Dictionary<string, int> { ["140"] = 4000 });

        var history = Every5Seconds(TimeSpan.FromSeconds(60), offset =>
        {
            var sample = Devlinux(offset);
            var detached = sample.Processes.Processes.Select(process => process.Pid == 4100 ? process with { ParentPid = 1 } : process).ToList();
            return sample with { Processes = ProcessSetReading.Read(detached) };
        });

        var withMembership = SlownessDiagnoser.Diagnose(history, membership);
        var withRootsOnly = SlownessDiagnoser.Diagnose(history, DevlinuxSessions);

        Assert.Equal("140", withMembership.Culprit?.SessionId);
        Assert.Contains("session 140", withMembership.Summary);
        Assert.Null(withRootsOnly.Culprit?.SessionId);
    }
}
