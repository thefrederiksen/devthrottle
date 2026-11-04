using System.Net;
using System.Net.Http;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The bridge release: every updater and installer reads releases from our own download server, never
/// from GitHub. stable.json names the newest stable release, and every file of that release - the
/// manifest, the Director, the launcher, the Gateway, the TOOLS bundle - is fetched from the version
/// folder it names. A server that cannot answer is a failure that names the address, never "up to date".
/// </summary>
public class ReleaseSourceDownloadServerTests
{
    private const string Base = FakeDownloadServer.Base;
    private const string ToolsBundle = "cc-tools-pyenv-win-x64.zip";

    [Fact]
    public void TheDownloadServerIsOurOwnAddress()
    {
        Assert.Equal("https://devthrottle.com/download/files", DownloadServer.BaseUrl);
    }

    [Fact]
    public async Task FetchLatest_ReadsStableJson_ThenItsManifest_AndMapsEveryAssetIntoTheTagFolder()
    {
        var server = new FakeDownloadServer()
            .Channel("stable.json", "v2.34.0")
            .Release("v2.34.0", "cc-director-win-x64.exe", "cc-director-launcher-win-x64.exe", ToolsBundle);
        var source = new ReleaseSource(new HttpClient(server));

        var release = await source.FetchLatestAsync(CancellationToken.None);

        Assert.Equal("2.34.0", release.Manifest.Version);
        Assert.Equal(
            new[] { $"{Base}/stable.json", $"{Base}/v2.34.0/release-manifest.json" },
            server.Requests);
        foreach (var name in release.Manifest.Assets.Keys)
            Assert.Equal($"{Base}/v2.34.0/{name}", release.DownloadUrls[name]);
        Assert.Equal($"{Base}/v2.34.0/release-manifest.json", release.DownloadUrls["release-manifest.json"]);
    }

    [Fact]
    public async Task TheToolsBundle_ResolvesToTheTagFolder()
    {
        // "We also need to make sure the tools are there": the Python tools bundle is fetched exactly
        // like every other file - from the version folder, not from GitHub.
        var server = new FakeDownloadServer()
            .Channel("stable.json", "v2.34.0")
            .Release("v2.34.0", ToolsBundle);

        var release = await new ReleaseSource(new HttpClient(server)).FetchLatestAsync(CancellationToken.None);

        Assert.Equal($"{Base}/v2.34.0/{ToolsBundle}", release.DownloadUrls[ToolsBundle]);
    }

    [Fact]
    public async Task AFullFetchAndDownload_AsksOnlyTheDownloadServer_NeverGitHub()
    {
        // Every request a full fetch makes - the channel file, the manifest, and the download of an asset
        // it resolved - goes to https://devthrottle.com/download/files/. Not one goes to github.com.
        var server = new FakeDownloadServer()
            .Channel("stable.json", "v2.34.0")
            .Release("v2.34.0", ToolsBundle)
            .Raw($"{Base}/v2.34.0/{ToolsBundle}", "the tools bundle");
        var source = new ReleaseSource(new HttpClient(server));

        var release = await source.FetchLatestAsync(CancellationToken.None);
        var staged = await source.DownloadAssetAsync(ToolsBundle, release.DownloadUrls, CancellationToken.None);
        File.Delete(staged);

        Assert.Equal(3, server.Requests.Count);
        Assert.All(server.Requests, url => Assert.StartsWith(Base + "/", url, StringComparison.Ordinal));
        Assert.All(release.DownloadUrls.Values, url => Assert.StartsWith(Base + "/", url, StringComparison.Ordinal));
        Assert.DoesNotContain(server.Requests, url => url.Contains("github", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(release.DownloadUrls.Values, url => url.Contains("github", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AChannelFileThatCannotBeRead_FailsAndNamesTheAddress(HttpStatusCode status)
    {
        var server = new FakeDownloadServer().Answer($"{Base}/stable.json", status);
        var source = new ReleaseSource(new HttpClient(server));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchLatestAsync(CancellationToken.None));

        Assert.Contains($"{Base}/stable.json", ex.Message);
        Assert.Contains(((int)status).ToString(), ex.Message);
        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public async Task AChannelThatNamesAFolderWithNoManifest_FailsAndNamesTheManifestAddress()
    {
        // The channel file is written only after every file in its folder is verified, so this cannot
        // be a release still being published. It is a broken server: a failure, by address.
        var server = new FakeDownloadServer().Channel("stable.json", "v2.34.0");
        var source = new ReleaseSource(new HttpClient(server));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchLatestAsync(CancellationToken.None));

        Assert.Contains($"{Base}/v2.34.0/release-manifest.json", ex.Message);
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task AnUnreachableServer_FailsNamingTheChannelAddress_AndAsksForNothingElse()
    {
        // The transport's own error carries no address, so the address in the message can only come
        // from ReleaseSource itself.
        var handler = new UnreachableHandler();
        var source = new ReleaseSource(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchLatestAsync(CancellationToken.None));

        Assert.Contains($"{Base}/stable.json", ex.Message);
        Assert.Null(ex.StatusCode);
        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.Equal("No connection could be made.", ex.InnerException!.Message);
        Assert.Equal(new[] { $"{Base}/stable.json" }, handler.Requested);
    }

    [Fact]
    public async Task AManifestOfAnotherVersion_IsRefused()
    {
        var server = new FakeDownloadServer()
            .Channel("stable.json", "v2.34.0")
            .RawManifest("v2.34.0", """{"version":"2.33.0","assets":{}}""");
        var source = new ReleaseSource(new HttpClient(server));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchLatestAsync(CancellationToken.None));

        Assert.Contains("2.33.0", ex.Message);
        Assert.Contains("v2.34.0", ex.Message);
    }

    [Theory]
    [InlineData("""{"version":"2.34.0"}""")]
    [InlineData("""{"tag":""}""")]
    [InlineData("""{"tag":"../../etc"}""")]
    [InlineData("""[]""")]
    public async Task AChannelFileWithNoUsableTag_IsRefused(string body)
    {
        var server = new FakeDownloadServer().Raw($"{Base}/stable.json", body);
        var source = new ReleaseSource(new HttpClient(server));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchLatestAsync(CancellationToken.None));

        Assert.Contains($"{Base}/stable.json", ex.Message);
        Assert.Single(server.Requests);
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        public List<string> Requested { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!.ToString());
            throw new HttpRequestException("No connection could be made.");
        }
    }
}
