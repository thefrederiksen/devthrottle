using CcDirectorClient.Voice;
using Xunit;

namespace CcDirectorClient.Tests;

// Issue #147: a network action must be gated by connectivity so an offline tap fails
// instantly with a clear message instead of dimming the buttons behind the HTTP timeout.
public class OfflineGuardTests
{
    [Fact]
    public void Check_Online_AllowsWithNoMessage()
    {
        var verdict = OfflineGuard.Check(online: true, action: "send your answer");

        Assert.True(verdict.Allowed);
        Assert.Equal("", verdict.Message);
    }

    [Fact]
    public void Check_Offline_BlocksWithActionableMessage()
    {
        var verdict = OfflineGuard.Check(online: false, action: "send your answer");

        Assert.False(verdict.Allowed);
        Assert.Contains("No connection", verdict.Message);
        Assert.Contains("send your answer", verdict.Message);
        Assert.Contains("try again", verdict.Message);
    }

    [Fact]
    public void Check_Offline_EmptyAction_FallsBackToGenericVerb()
    {
        var verdict = OfflineGuard.Check(online: false, action: "");

        Assert.False(verdict.Allowed);
        Assert.Contains("do that", verdict.Message);
    }

    [Fact]
    public void Check_Offline_TrimsTheActionLabel()
    {
        var verdict = OfflineGuard.Check(online: false, action: "  hold this session  ");

        Assert.False(verdict.Allowed);
        Assert.Contains("can't hold this session right now", verdict.Message);
    }
}
