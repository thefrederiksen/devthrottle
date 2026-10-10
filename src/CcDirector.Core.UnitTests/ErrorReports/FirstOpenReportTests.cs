using System.Net;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.ErrorReports;
using Xunit;

namespace CcDirector.Core.UnitTests.ErrorReports;

/// <summary>
/// Issue #3722: one report the first time DevThrottle opens on a machine that has not signed in, so a person
/// who installed and stopped at the sign-in screen is visible. Pinned here: sent once, under the machine's
/// install id, only before sign-in, and tried again at the next start when it could not be delivered.
/// </summary>
public sealed class FirstOpenReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-first-open-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public readonly List<(string Url, string Body)> Requests = new();
        public HttpStatusCode Status = HttpStatusCode.Accepted;
        public Exception? Throw;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.ToString(), body));
            if (Throw is not null) throw Throw;
            return new HttpResponseMessage(Status);
        }
    }

    private FirstOpenReport Report(StubHandler handler, bool signedIn) => new(
        _root,
        () => signedIn ? new GatewayConfig { Url = "https://gw.example", Token = "dt_device_x" } : new GatewayConfig(),
        () => "https://gateway.example/",
        new HttpClient(handler),
        "2.18.0+abc");

    private string Marker => Path.Combine(_root, FirstOpenReport.MarkerFileName);

    [Fact]
    public async Task FirstOpenBeforeSignIn_SendsOneDirectorOpenedReportUnderTheInstallId()
    {
        var handler = new StubHandler();

        Assert.True(await Report(handler, signedIn: false).SendIfFirstAsync(CancellationToken.None));

        var (url, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://gateway.example/install-reports", url);
        var payload = JsonSerializer.Deserialize<InstallReportPayload>(body)!;
        Assert.Equal(FirstOpenReport.Step, payload.Step);
        Assert.Equal("director", payload.Installer);
        Assert.Equal(InstallId.ReadOrCreate(_root), payload.InstallId);
        Assert.Equal("2.18.0+abc", payload.ProductVersion);
        Assert.True(File.Exists(Marker));
    }

    [Fact]
    public async Task SecondOpen_SendsNothing()
    {
        var handler = new StubHandler();
        await Report(handler, signedIn: false).SendIfFirstAsync(CancellationToken.None);

        Assert.False(await Report(handler, signedIn: false).SendIfFirstAsync(CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AlreadySignedIn_SendsNothingAndMarksTheFirstOpenAsSeen()
    {
        var handler = new StubHandler();

        Assert.False(await Report(handler, signedIn: true).SendIfFirstAsync(CancellationToken.None));

        Assert.Empty(handler.Requests);
        Assert.True(File.Exists(Marker));
    }

    [Fact]
    public async Task Refused_LeavesNoMarker_SoTheNextStartTriesAgain()
    {
        var handler = new StubHandler { Status = HttpStatusCode.TooManyRequests };

        Assert.False(await Report(handler, signedIn: false).SendIfFirstAsync(CancellationToken.None));
        Assert.False(File.Exists(Marker));

        handler.Status = HttpStatusCode.Accepted;
        Assert.True(await Report(handler, signedIn: false).SendIfFirstAsync(CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Unreachable_DoesNotThrowAndLeavesNoMarker()
    {
        var handler = new StubHandler { Throw = new HttpRequestException("no route to host") };

        Assert.False(await Report(handler, signedIn: false).SendIfFirstAsync(CancellationToken.None));
        Assert.False(File.Exists(Marker));
    }
}
