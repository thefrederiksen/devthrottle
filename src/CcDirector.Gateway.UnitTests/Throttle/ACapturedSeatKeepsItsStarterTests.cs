using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Throttle;

/// <summary>
/// Your Throttle counts only sessions a person started (2026-09-27), so a restored session must be born with the
/// starter of the session it continues. The workspace seat is the record a restart restores from, so the capture
/// has to carry the starter into it; the restore paths read it back from there.
/// </summary>
public sealed class ACapturedSeatKeepsItsStarterTests
{
    [Theory]
    [InlineData("human")]
    [InlineData("agent")]
    [InlineData("schedule")]
    [InlineData("unknown")]
    public void TheCaptureCopiesTheStarter(string starter)
    {
        var seat = WorkspaceCapture.CaptureSeat(new SessionDto
        {
            SessionId = "s1", Name = "a seat", Agent = "ClaudeCode", RepoPath = @"D:\repo", OriginKind = starter,
        }, sortOrder: 0);

        Assert.Equal(starter, seat.OriginKind);
    }

    [Fact]
    public void ASessionWithNoRecordedStarter_IsCapturedWithNone_SoTheRestoreFallsToWhoAsked()
    {
        var seat = WorkspaceCapture.CaptureSeat(new SessionDto
        {
            SessionId = "s1", Name = "a seat", Agent = "ClaudeCode", RepoPath = @"D:\repo", OriginKind = null,
        }, sortOrder: 0);

        Assert.Null(seat.OriginKind);
    }
}
