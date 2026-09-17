using CcDirector.Avalonia.DevReports;
using Xunit;

namespace CcDirector.Avalonia.Tests.DevReports;

/// <summary>
/// How the Director asks the Gateway where a session's reports page is, and how it reads the answer
/// (issue #3019).
///
/// The claim held here is the dumb-client rule (CLAUDE.md rule 7): the Director composes the address of the
/// GATEWAY ROUTE and then opens whatever page address the Gateway answers with, verbatim. It never builds a
/// page address of its own, and it never guesses one when the Gateway does not supply it.
/// </summary>
public sealed class DevReportPaneUrlClientTests
{
    private const string Session = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void BuildRequestUrl_AGatewayBaseAndASession_IsTheRouteWithTheSessionAsked()
    {
        Assert.Equal(
            "http://127.0.0.1:7878/dev-reports/pane-url?sessionId=" + Session,
            DevReportPaneUrlClient.BuildRequestUrl("http://127.0.0.1:7878", Session));
    }

    [Fact]
    public void BuildRequestUrl_ABaseWithATrailingSlash_DoesNotDoubleTheSlash()
    {
        Assert.Equal(
            "http://127.0.0.1:7878/dev-reports/pane-url?sessionId=" + Session,
            DevReportPaneUrlClient.BuildRequestUrl("http://127.0.0.1:7878/", Session));
    }

    [Fact]
    public void BuildRequestUrl_ASessionWithCharactersToEscape_IsEscaped()
    {
        Assert.Contains("sessionId=a%20b%26c", DevReportPaneUrlClient.BuildRequestUrl("http://h:1", "a b&c"));
    }

    [Fact]
    public void BuildRequestUrl_NoGatewayBase_Throws()
        => Assert.Throws<ArgumentException>(() => DevReportPaneUrlClient.BuildRequestUrl("", Session));

    [Fact]
    public void BuildRequestUrl_NoSession_Throws()
        => Assert.Throws<ArgumentException>(() => DevReportPaneUrlClient.BuildRequestUrl("http://h:1", " "));

    [Fact]
    public void ReadUrl_TheGatewaysAnswer_IsThatAddressVerbatim()
    {
        var page = "http://soren_north:7878/embed/reports/" + Session;
        Assert.Equal(page, DevReportPaneUrlClient.ReadUrl("{\"url\":\"" + page + "\"}"));
    }

    [Fact]
    public void ReadUrl_AnAddressTheDirectorWouldNotHaveGuessed_IsStillUsedVerbatim()
    {
        // The page address is the Gateway's to decide. If it moves, the Director follows it without being
        // rebuilt - so this test would fail if anything here ever re-composed a path of its own.
        const string moved = "https://gateway.devthrottle.com/reports-somewhere-else/" + Session + "?v=2";
        Assert.Equal(moved, DevReportPaneUrlClient.ReadUrl("{\"url\":\"" + moved + "\"}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"url\":null}")]
    [InlineData("{\"url\":\"\"}")]
    [InlineData("{\"url\":\"   \"}")]
    [InlineData("{\"url\":42}")]
    public void ReadUrl_AnAnswerWithNoAddressInIt_FailsRatherThanGuessing(string json)
        => Assert.Throws<InvalidOperationException>(() => DevReportPaneUrlClient.ReadUrl(json));

    [Fact]
    public async Task FetchAsync_NoGatewayKey_SaysSoAndAsksNothing()
    {
        using var http = DevReportPaneUrlClient.NewClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DevReportPaneUrlClient.FetchAsync(http, "http://127.0.0.1:1", "", Session));

        Assert.Contains("holds no Gateway key", ex.Message);
    }
}
