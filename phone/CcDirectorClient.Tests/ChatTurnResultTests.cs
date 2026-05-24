using CcDirectorClient.Voice;
using Xunit;

namespace CcDirectorClient.Tests;

public class ChatTurnResultTests
{
    [Theory]
    [InlineData("ok", true, false)]
    [InlineData("session_not_found", true, false)]
    [InlineData("send_failed", true, false)]
    [InlineData("working", false, true)]
    [InlineData("timeout", false, true)]
    public void StatusDrivesTerminalAndPolling(string status, bool terminal, bool keepPolling)
    {
        var r = new ChatTurnResult { Status = status };
        Assert.Equal(terminal, r.IsTerminal);
        Assert.Equal(keepPolling, r.ShouldKeepPolling);
    }

    [Fact]
    public void SpokenText_PrefersSummaryWhenPresent()
    {
        var r = new ChatTurnResult { Summary = "  short spoken form  ", DisplayText = "the long raw reply" };
        Assert.Equal("short spoken form", r.SpokenText());
    }

    [Fact]
    public void SpokenText_FallsBackToDisplayWhenSummaryEmpty()
    {
        var r = new ChatTurnResult { Summary = "   ", DisplayText = "  the real reply  " };
        Assert.Equal("the real reply", r.SpokenText());
    }

    [Fact]
    public void SpokenText_BothEmpty_ReturnsEmpty()
    {
        Assert.Equal("", new ChatTurnResult().SpokenText());
    }
}
