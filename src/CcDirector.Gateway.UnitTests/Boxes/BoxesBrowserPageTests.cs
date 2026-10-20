using CcDirector.Gateway.Cockpit;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Boxes;

/// <summary>
/// <c>/boxes</c> is two things at one address (the Factory Boxes mission, phase 1): the Gateway's JSON list
/// (<c>GET /boxes</c>) and the Cockpit's Boxes page. A person's browser loading the page (a reload, a bookmark, a link)
/// must be handed the Cockpit, or the screen shows the raw list; a program reading the list must still get the JSON;
/// and nothing under a box - its desktop routes, its ticket - may ever be turned into the Cockpit page.
/// </summary>
public sealed class BoxesBrowserPageTests
{
    [Fact]
    public void IsBrowserPageRequest_BrowserLoadsTheBoxesPage_GetsTheCockpit()
    {
        Assert.True(CockpitReactApp.IsBrowserPageRequest("GET", new PathString("/boxes"),
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"));
    }

    [Fact]
    public void IsBrowserPageRequest_ProgramReadsTheList_GetsTheJson()
    {
        Assert.False(CockpitReactApp.IsBrowserPageRequest("GET", new PathString("/boxes"), "application/json"));
        Assert.False(CockpitReactApp.IsBrowserPageRequest("GET", new PathString("/boxes"), null));
    }

    [Theory]
    [InlineData("GET", "/boxes/demo-desktop-soren/desktop/")]
    [InlineData("GET", "/boxes/demo-desktop-soren/desktop/index.html")]
    [InlineData("POST", "/boxes/demo-desktop-soren/desktop-ticket")]
    public void IsBrowserPageRequest_ARouteUnderABox_StaysTheGateways(string method, string path)
    {
        Assert.False(CockpitReactApp.IsBrowserPageRequest(method, new PathString(path), "text/html"));
    }
}
