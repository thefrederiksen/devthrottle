using CcDirector.Gateway.Cockpit;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Missions;

/// <summary>
/// <c>/missions</c> is two things at one address: the Gateway's JSON mission list (<c>GET /missions</c>) and the
/// Cockpit's <c>/missions</c>, which sends the person on to the Fleet Map. A person's browser loading it (a reload, an
/// old bookmark) must be handed the Cockpit - it was handed the raw list (Web UI Polish, 7 November 2026). A program
/// reading the list must still get the JSON, and the routes under one mission's notes stay the Gateway's.
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

    [Theory]
    [InlineData("POST", "/missions")]
    [InlineData("PATCH", "/missions/f8264528-2e8f-4ce4-84e2-86012250f81d")]
    [InlineData("GET", "/missions/f8264528-2e8f-4ce4-84e2-86012250f81d/notes")]
    public void IsBrowserPageRequest_AWriteOrARouteUnderAMission_StaysTheGateways(string method, string path)
    {
        Assert.False(CockpitReactApp.IsBrowserPageRequest(method, new PathString(path), "text/html"));
    }
}
