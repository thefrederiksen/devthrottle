using CcDirector.Gateway.DevReports;
using Xunit;

namespace CcDirector.Gateway.Tests.DevReports;

/// <summary>
/// The address the Gateway hands a host application for one session's reports page (issue #3019), and the
/// rule that says which paths ARE that page. Both are pure, so all of it is proven with no server, no
/// router and no browser.
///
/// The claim these tests exist to hold is the dumb-client rule (CLAUDE.md rule 7): the address is built
/// from the base the CALLER reached this Gateway on. A Director that reached a Gateway at
/// <c>http://soren_north:7878</c> must be handed a page on THAT Gateway, because the key it will hand the
/// page is a key for that Gateway. Substituting the hosted or tailnet surface address would point the page
/// at a different Gateway and read as an empty report list rather than as a mistake.
/// </summary>
public sealed class DevReportPaneUrlTests
{
    private const string Session = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void Build_PlainHostAndPort_IsThatBasePlusThePagePath()
    {
        Assert.Equal(
            "http://soren_north:7878/embed/reports/" + Session,
            DevReportPaneUrl.Build("http", "soren_north:7878", "", Session));
    }

    [Fact]
    public void Build_HostedSchemeAndHost_KeepsTheCallersOwnSchemeAndHost()
    {
        Assert.Equal(
            "https://gateway.devthrottle.com/embed/reports/" + Session,
            DevReportPaneUrl.Build("https", "gateway.devthrottle.com", null, Session));
    }

    [Fact]
    public void Build_PathBase_IsKeptBetweenTheHostAndThePagePath()
    {
        Assert.Equal(
            "https://front.door/gw/embed/reports/" + Session,
            DevReportPaneUrl.Build("https", "front.door", "/gw", Session));
    }

    [Fact]
    public void Build_PathBaseWithATrailingSlash_DoesNotDoubleTheSlash()
    {
        Assert.Equal(
            "https://front.door/gw/embed/reports/" + Session,
            DevReportPaneUrl.Build("https", "front.door", "/gw/", Session));
    }

    [Fact]
    public void Build_NoHost_Throws()
        => Assert.Throws<ArgumentException>(() => DevReportPaneUrl.Build("http", "", "", Session));

    [Fact]
    public void Build_NoScheme_Throws()
        => Assert.Throws<ArgumentException>(() => DevReportPaneUrl.Build("", "host:1", "", Session));

    [Fact]
    public void Build_NoSession_Throws()
        => Assert.Throws<ArgumentException>(() => DevReportPaneUrl.Build("http", "host:1", "", "  "));

    // ---------------------------------------------------------------- the whole answer

    [Fact]
    public void Answer_AHostAndASession_IsTheAddressOnTheCallersOwnBase()
    {
        var answer = DevReportPaneUrl.Answer("http", "soren_north:7878", "", Session);

        Assert.Equal(200, answer.Status);
        Assert.Equal("http://soren_north:7878/embed/reports/" + Session, answer.Url);
        Assert.Null(answer.Code);
        Assert.Null(answer.Error);
    }

    [Fact]
    public void Answer_SessionIdentifierInAnotherForm_IsNormalizedIntoTheAddress()
    {
        // A session identifier is a GUID. Braces and upper case parse, and the address carries the one
        // canonical form, so two spellings of one session never open two different pages.
        var answer = DevReportPaneUrl.Answer("http", "h:1", "", "{11111111-2222-3333-4444-555555555555}");

        Assert.Equal(200, answer.Status);
        Assert.Equal("http://h:1/embed/reports/" + Session, answer.Url);
    }

    [Fact]
    public void Answer_NoSessionNamed_Is400AndSaysToNameOne()
    {
        var answer = DevReportPaneUrl.Answer("http", "h:1", "", "");

        Assert.Equal(400, answer.Status);
        Assert.Equal("session_required", answer.Code);
        Assert.Null(answer.Url);
        Assert.Contains("sessionId", answer.Error);
    }

    [Fact]
    public void Answer_SessionIdentifierThatIsNotOne_Is400AndQuotesWhatArrived()
    {
        var answer = DevReportPaneUrl.Answer("http", "h:1", "", "not-a-session");

        Assert.Equal(400, answer.Status);
        Assert.Equal("bad_session_id", answer.Code);
        Assert.Null(answer.Url);
        Assert.Contains("not-a-session", answer.Error);
    }

    [Fact]
    public void Answer_RequestCarriedNoHost_Is409AndBuildsNothing()
    {
        var answer = DevReportPaneUrl.Answer("http", null, "", Session);

        Assert.Equal(409, answer.Status);
        Assert.Equal("no_host", answer.Code);
        Assert.Null(answer.Url);
    }

    [Fact]
    public void Answer_NoSessionAndNoHost_RefusesTheSessionFirst()
    {
        // The order matters for the reader: "you did not name a session" is what the caller can fix.
        var answer = DevReportPaneUrl.Answer("http", null, "", "");

        Assert.Equal("session_required", answer.Code);
    }

    // ---------------------------------------------------------------- which paths are the page

    [Theory]
    [InlineData("/embed/reports/" + Session)]
    [InlineData("/embed/reports/" + Session + "/")]
    [InlineData("/EMBED/REPORTS/" + Session)]
    public void IsPagePath_ThePagePlusExactlyOneSegment_IsThePage(string path)
        => Assert.True(DevReportPaneUrl.IsPagePath(path));

    [Theory]
    [InlineData("/embed/reports")]
    [InlineData("/embed/reports/")]
    [InlineData("/embed/reports//")]
    [InlineData("/embed/reports/" + Session + "/html")]
    [InlineData("/embed/reports/" + Session + "/anything/deeper")]
    [InlineData("/embed")]
    [InlineData("/embeddings/reports/x")]
    [InlineData("/dev-reports")]
    [InlineData("")]
    [InlineData(null)]
    public void IsPagePath_AnythingElse_IsNotThePage(string? path)
        => Assert.False(DevReportPaneUrl.IsPagePath(path));
}
