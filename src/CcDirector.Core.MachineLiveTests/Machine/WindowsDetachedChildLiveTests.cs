using System.Diagnostics;
using CcDirector.Core.Machine;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// THE FIRST NAMED RISK OF THE RESOURCE MONITOR MISSION, PROVED ON A REAL WINDOWS MACHINE: a child
/// process that outlives its parent falls out of a tree found by walking parent ids.
///
/// The test starts <c>cmd.exe</c>, which starts <c>ping</c> in the background and exits at once - the
/// shape of any tool that launches something and returns. Then it reads every process through
/// <see cref="WindowsResourceProbe"/> and shows three facts:
/// <list type="bullet">
/// <item>ping is still running, and still names the dead <c>cmd.exe</c> as its parent;</item>
/// <item>that parent is in no process list any more;</item>
/// <item>so a walk down from this test process by parent ids - which is how a session's tree is found
/// today - does not reach ping, although this test started it.</item>
/// </list>
/// This test PASSES WHILE THE DEFECT EXISTS. It is the evidence behind
/// missions/resource-monitor/phase-1/windows-detached-children.md, and it should be rewritten, not
/// deleted, the day sessions run inside their own Job Object.
/// </summary>
[Trait("Category", "Resources")]
public class WindowsDetachedChildLiveTests
{
    private readonly ITestOutputHelper _output;

    public WindowsDetachedChildLiveTests(ITestOutputHelper output) => _output = output;

    [WindowsLiveMachineFact("the process list, after starting a child that outlives its parent")]
    public void DetachedChild_ParentExited_IsNotReachedByWalkingParentIds()
    {
        // Arrange: cmd starts ping without waiting for it, then exits.
        var start = new ProcessStartInfo("cmd.exe", "/c start \"\" /b ping -n 30 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var cmd = Process.Start(start) ?? throw new InvalidOperationException("cmd.exe did not start");
        var cmdId = cmd.Id;
        Assert.True(cmd.WaitForExit(10_000), "cmd.exe did not exit within ten seconds");

        int? pingId = null;
        try
        {
            // Act: one read. The first read already lists every process with its parent and start time;
            // only the rates wait for a second one, and this test needs none.
            var processes = new WindowsResourceProbe().Read().Processes;
            Assert.True(processes.CouldRead, processes.UnreadableReason);

            // Assert 1: ping is alive and still names the dead cmd.exe.
            var ping = Assert.Single(processes.Processes,
                p => p.ParentPid == cmdId && p.Name.Equals("PING.EXE", StringComparison.OrdinalIgnoreCase));
            pingId = ping.Pid;
            _output.WriteLine($"this test: process {Environment.ProcessId}; cmd.exe: process {cmdId}, exited; {ping} names parent {ping.ParentPid}");

            // Assert 2: the parent it names is gone. If its id is already reused, the new holder started
            // AFTER ping, so it is not ping's parent either.
            var holder = processes.Processes.FirstOrDefault(p => p.Pid == cmdId);
            if (holder is not null)
            {
                _output.WriteLine($"process id {cmdId} already reused by {holder}, started {holder.StartTimeUtc:O}");
                Assert.True(holder.StartTimeUtc > ping.StartTimeUtc, "the parent id is held by a process older than ping");
            }

            // Assert 3: walking down from this test process by parent ids does not reach ping.
            var tree = WalkDownByParentIds(processes.Processes, Environment.ProcessId);
            _output.WriteLine($"walk down from process {Environment.ProcessId} by parent ids: {tree.Count} processes");
            Assert.DoesNotContain(ping.Pid, tree);
        }
        finally
        {
            if (pingId is { } id) KillIfRunning(id);
        }
    }

    /// <summary>The plain walk: a root and every process whose parent id is already in the set.</summary>
    private static HashSet<int> WalkDownByParentIds(IReadOnlyList<ProcessReading> processes, int root)
    {
        var tree = new HashSet<int> { root };
        bool grew;
        do
        {
            grew = false;
            foreach (var p in processes)
                if (p.ParentPid is { } parent && tree.Contains(parent) && tree.Add(p.Pid)) grew = true;
        } while (grew);
        return tree;
    }

    private static void KillIfRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch (ArgumentException)
        {
            // Already exited: nothing to clean up.
        }
    }
}
