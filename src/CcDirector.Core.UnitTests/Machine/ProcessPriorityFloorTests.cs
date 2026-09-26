using System.ComponentModel;
using System.Diagnostics;
using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The Director runs at normal priority however it was started (Voice Delivery mission, phase 6). On 25 September 2026
/// a Director started by a Windows scheduled task ran at Below Normal - the task's default - and against a machine kept
/// busy at normal priority it answered the prompt verb 32 seconds after receipt (budget 20) and a one-file read 61
/// seconds after. The same code at Normal priority under the same load answered on time.
/// </summary>
public class ProcessPriorityFloorTests
{
    [Theory]
    [InlineData(ProcessPriorityClass.BelowNormal)]
    [InlineData(ProcessPriorityClass.Idle)]
    public void Apply_StartedBelowNormal_RaisesToNormal(ProcessPriorityClass startedAt)
    {
        // Arrange
        var current = startedAt;
        var asked = new List<ProcessPriorityClass>();

        // Act
        var accepted = ProcessPriorityFloor.Apply(() => current, p => { asked.Add(p); current = p; });

        // Assert
        Assert.True(accepted);
        Assert.Equal(new[] { ProcessPriorityClass.Normal }, asked);
        Assert.Equal(ProcessPriorityClass.Normal, current);
    }

    [Theory]
    [InlineData(ProcessPriorityClass.Normal)]
    [InlineData(ProcessPriorityClass.AboveNormal)]
    [InlineData(ProcessPriorityClass.High)]
    [InlineData(ProcessPriorityClass.RealTime)]
    public void Apply_AtOrAboveNormal_NeverChangesIt(ProcessPriorityClass startedAt)
    {
        // Arrange
        var asked = new List<ProcessPriorityClass>();

        // Act
        var accepted = ProcessPriorityFloor.Apply(() => startedAt, p => asked.Add(p));

        // Assert: never lowered - somebody who started it higher meant it.
        Assert.True(accepted);
        Assert.Empty(asked);
    }

    [Fact]
    public void Apply_TheSystemRefusesTheRaise_ReportsItRefused()
    {
        // Arrange: on Linux and macOS an unprivileged process may not lower its own nice value.
        var current = ProcessPriorityClass.BelowNormal;

        // Act
        var accepted = ProcessPriorityFloor.Apply(() => current, _ => throw new Win32Exception(13, "Permission denied"));

        // Assert
        Assert.False(accepted);
        Assert.Equal(ProcessPriorityClass.BelowNormal, current);
    }

    [WindowsOnlyFact("it sets this process's Windows priority class, which is the mechanism a scheduled task uses to start the Director low")]
    public void Apply_ThisProcessStartedBelowNormal_RunsAtNormal()
    {
        // Arrange: this test process as a default scheduled task would start it.
        var me = Process.GetCurrentProcess();
        var original = me.PriorityClass;
        try
        {
            me.PriorityClass = ProcessPriorityClass.BelowNormal;

            // Act
            var accepted = ProcessPriorityFloor.Apply();

            // Assert
            me.Refresh();
            Assert.True(accepted);
            Assert.Equal(ProcessPriorityClass.Normal, me.PriorityClass);
        }
        finally
        {
            me.PriorityClass = original;
        }
    }
}
