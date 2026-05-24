using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

public sealed class TerminalStateDetectorTests
{
    [Theory]
    [InlineData("working", ActivityState.Working)]
    [InlineData("waiting_for_permission", ActivityState.WaitingForPerm)]
    [InlineData("waiting_for_input", ActivityState.WaitingForInput)]
    [InlineData("idle", ActivityState.WaitingForInput)]
    [InlineData("cancelled", ActivityState.WaitingForInput)]
    [InlineData("unknown", ActivityState.WaitingForInput)]
    [InlineData("anything-unexpected", ActivityState.WaitingForInput)]
    public void MapVerdictToActivityState_mapsAsDesigned(string verdict, ActivityState expected)
    {
        Assert.Equal(expected, TerminalStateDetector.MapVerdictToActivityState(verdict));
    }

    [Fact]
    public void WingmanLlmThrottle_blocks_a_second_call_within_the_window()
    {
        var sid = Guid.NewGuid();
        Assert.True(WingmanLlmThrottle.TryAcquire(sid));    // first call allowed
        Assert.False(WingmanLlmThrottle.TryAcquire(sid));   // immediate second blocked (< 5s)

        var other = Guid.NewGuid();
        Assert.True(WingmanLlmThrottle.TryAcquire(other));  // a different session is independent
    }
}
