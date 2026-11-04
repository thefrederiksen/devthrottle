using System.Net;
using System.Net.Http;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Issue #1294: a pre-release setup executable must install its matching pre-release, resolved in
/// memory from the setup exe's own stamped version - never the latest stable. A stable setup exe still
/// installs the latest stable, unchanged.
///
/// On the download server that means: a stable setup exe reads stable.json; a pre-release setup exe
/// reads its OWN version folder when it exists, and otherwise the newest release of any kind that
/// prerelease.json names.
/// </summary>
public class ReleaseSourceSetupVersionTests
{
    private const string Base = FakeDownloadServer.Base;

    /// <summary>The server as it stands with 1.0.7 stable and two pre-releases, rc3 and rc4, published.</summary>
    private static FakeDownloadServer PublishedServer() => new FakeDownloadServer()
        .Channel("stable.json", "v1.0.7")
        .Channel("prerelease.json", "v1.1.0-rc4", prerelease: true)
        .Release("v1.0.7")
        .Release("v1.1.0-rc3")
        .Release("v1.1.0-rc4");

    private static ReleaseSource NewSource(FakeDownloadServer server) => new(new HttpClient(server));

    [Fact]
    public async Task FetchReleaseForSetup_StableSetupVersion_InstallsLatestStableFromStableJson()
    {
        var server = PublishedServer();

        var release = await NewSource(server).FetchReleaseForSetupAsync(CancellationToken.None, setupVersion: "1.0.7");

        Assert.Equal("1.0.7", release.Manifest.Version);
        Assert.Equal(new[] { $"{Base}/stable.json", $"{Base}/v1.0.7/release-manifest.json" }, server.Requests);
    }

    [Fact]
    public async Task FetchReleaseForSetup_PreReleaseSetupVersion_InstallsItsOwnFolder()
    {
        // The rc3 setup exe installs rc3 even though rc4 is newer: an exact match is exactly what this
        // setup exe is. Neither channel file is consulted.
        var server = PublishedServer();

        var release = await NewSource(server).FetchReleaseForSetupAsync(CancellationToken.None, setupVersion: "1.1.0-rc3");

        Assert.Equal("1.1.0-rc3", release.Manifest.Version);
        Assert.Equal(new[] { $"{Base}/v1.1.0-rc3/release-manifest.json" }, server.Requests);
        Assert.Equal($"{Base}/v1.1.0-rc3/release-manifest.json", release.DownloadUrls["release-manifest.json"]);
    }

    [Fact]
    public async Task FetchReleaseForSetup_PreReleaseWithLeadingVAndBuildMetadata_StillFindsItsFolder()
    {
        // The stamped version carries "+commit"; the folder is named for the tag, with a leading "v".
        var server = PublishedServer();

        var release = await NewSource(server).FetchReleaseForSetupAsync(CancellationToken.None, setupVersion: "v1.1.0-rc3+deadbeef");

        Assert.Equal("1.1.0-rc3", release.Manifest.Version);
        Assert.Equal(new[] { $"{Base}/v1.1.0-rc3/release-manifest.json" }, server.Requests);
    }

    [Fact]
    public async Task FetchReleaseForSetup_PreReleaseWithNoFolder_InstallsWhatPreReleaseJsonNames()
    {
        // rc9 was never published, so there is no folder for it; the newest release named by
        // prerelease.json (rc4) is installed. Never stable.json.
        var server = PublishedServer();

        var release = await NewSource(server).FetchReleaseForSetupAsync(CancellationToken.None, setupVersion: "1.1.0-rc9");

        Assert.Equal("1.1.0-rc4", release.Manifest.Version);
        Assert.Equal(
            new[]
            {
                $"{Base}/v1.1.0-rc9/release-manifest.json",
                $"{Base}/prerelease.json",
                $"{Base}/v1.1.0-rc4/release-manifest.json",
            },
            server.Requests);
        Assert.DoesNotContain($"{Base}/stable.json", server.Requests);
    }

    [Fact]
    public async Task FetchReleaseForSetup_PreReleaseWithNoFolderAndNoPreReleaseJson_FailsByAddress()
    {
        // Nothing to install: it fails loudly and names the address, rather than silently installing stable.
        var server = new FakeDownloadServer()
            .Channel("stable.json", "v1.0.7")
            .Release("v1.0.7");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            NewSource(server).FetchReleaseForSetupAsync(CancellationToken.None, setupVersion: "1.1.0-rc4"));

        Assert.Contains($"{Base}/prerelease.json", ex.Message);
        Assert.DoesNotContain($"{Base}/stable.json", server.Requests);
    }

    [Fact]
    public async Task FetchReleaseForSetup_OwnFolderAnswersAnError_FailsAndDoesNotFallBack()
    {
        // Only a 404 means "no folder for this version". Any other answer is the server failing, and is
        // reported - it does not quietly switch to prerelease.json.
        var server = PublishedServer().Answer($"{Base}/v1.1.0-rc3/release-manifest.json", HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            NewSource(server).FetchReleaseForSetupAsync(CancellationToken.None, setupVersion: "1.1.0-rc3"));

        Assert.Contains($"{Base}/v1.1.0-rc3/release-manifest.json", ex.Message);
        Assert.DoesNotContain($"{Base}/prerelease.json", server.Requests);
    }
}
