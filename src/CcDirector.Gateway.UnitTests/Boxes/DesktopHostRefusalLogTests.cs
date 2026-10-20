using System.Net;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tests;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Boxes;

/// <summary>
/// Factory Boxes (issue #4258): every route the desktop host refuses is logged with its method, path and user agent, so
/// that after the desktop host is set in production a Director or phone still configured with that address can be
/// found in the log. In the FileLog capture collection because it swaps the process-wide log writer.
/// </summary>
[Collection(FileLogCaptureCollection.Name)]
public sealed class DesktopHostRefusalLogTests : IAsyncLifetime
{
    private DesktopRelayRig _rig = null!;

    public async Task InitializeAsync() => _rig = await DesktopRelayRig.StartAsync();

    public async Task DisposeAsync() => await _rig.DisposeAsync();

    [Fact]
    public async Task DesktopHost_ARefusedRoute_IsLoggedWithMethodPathAndUserAgent()
    {
        using var log = FileLog.RedirectForTests();
        using var desktop = _rig.Desktop(cookie: null);
        var request = new HttpRequestMessage(HttpMethod.Post, "/director-stream/negotiate");
        request.Headers.UserAgent.ParseAdd("cc-director/2.19.0");

        var response = await desktop.SendAsync(request).Within();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var line = Assert.Single(log.DrainAndReadLines(), l => l.Contains("desktop host REFUSED"));
        Assert.Contains("POST /director-stream/negotiate", line);
        Assert.Contains("user-agent=\"cc-director/2.19.0\"", line);
    }
}
