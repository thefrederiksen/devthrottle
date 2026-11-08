using CcDirector.Gateway.Cockpit;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Missions;

/// <summary>
/// <c>/missions</c> is two things at one address: the Gateway's JSON mission list (<c>GET /missions</c>) and the
/// Cockpit's <c>/missions</c>, which sends the person on to the Fleet Map. A person's browser loading it (a reload, an
/// old bookmark) must be handed the Cockpit - it was handed the raw list (Web UI Polish, 7 November 2026). A program
/// reading the list must still get the JSON, and the writes stay the Gateway's.
/// </summary>
public sealed class MissionsBrowserPageTests
{
    private const string BrowserAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

    [Fact]
    public void IsBrowserPageRequest_BrowserLoadsMissions_GetsTheCockpit()
    {
        Assert.True(CockpitReactApp.IsBrowserPageRequest("GET", new PathString("/missions"), BrowserAccept));
    }

    [Fact]
    public void IsBrowserPageRequest_ProgramReadsTheMissionList_GetsTheJson()
    {
        Assert.False(CockpitReactApp.IsBrowserPageRequest("GET", new PathString("/missions"), "application/json"));
        Assert.False(CockpitReactApp.IsBrowserPageRequest("GET", new PathString("/missions"), null));
    }

    // A decision, recorded: a person pasting one mission's address gets the Cockpit (which goes on to the Fleet Map), not
    // the mission's JSON. The shipped programs that read it send Accept application/json and are unaffected.
    [Fact]
    public void IsBrowserPageRequest_BrowserLoadsOneMission_GetsTheCockpit()
    {
        Assert.True(CockpitReactApp.IsBrowserPageRequest("GET", new PathString("/missions/f8264528-2e8f-4ce4-84e2-86012250f81d"), BrowserAccept));
        Assert.False(CockpitReactApp.IsBrowserPageRequest("GET", new PathString("/missions/f8264528-2e8f-4ce4-84e2-86012250f81d"), "application/json"));
    }

    [Theory]
    [InlineData("POST", "/missions")]
    [InlineData("PATCH", "/missions/f8264528-2e8f-4ce4-84e2-86012250f81d")]
    public void IsBrowserPageRequest_AWriteToMissions_StaysTheGateways(string method, string path)
    {
        Assert.False(CockpitReactApp.IsBrowserPageRequest(method, new PathString(path), "text/html"));
    }
}
