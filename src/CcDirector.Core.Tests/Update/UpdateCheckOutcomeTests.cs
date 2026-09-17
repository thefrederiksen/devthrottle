using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using CcDirector.Core.Update;
using Xunit;

namespace CcDirector.Core.Tests.Update;

/// <summary>
/// What a check CONCLUDES, and that it writes the conclusion down (issues #1030 and #1079).
///
/// Two defects meet here. A release is "latest" the instant its tag is pushed and its downloads are
/// attached about five and a half minutes later, so any machine checking inside that window finds a
/// newer release it cannot fetch - and the code reported that as UP TO DATE. And no conclusion of any
/// kind survived the check that produced it, so a Director could not say what its last check had found
/// even seconds afterwards. Together those are two of the five situations that all looked like an
/// unchanged version number.
///
/// These run on every platform that has a published Director build, using that platform's own download
/// name, so they are not only proved on Windows.
/// </summary>
public class UpdateCheckOutcomeTests
{
    /// <summary>The download name for the machine running the tests, or null when none is published.</summary>
    private static string? CurrentAssetName => UpdateService.AssetNameFor(CurrentPlatform, RuntimeInformation.OSArchitecture);

    private static OSPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? OSPlatform.Windows
        : OperatingSystem.IsMacOS() ? OSPlatform.OSX
        : OSPlatform.Linux;

    /// <summary>
    /// Whether this machine is one the release publishes an asset for. On anything else these tests have
    /// no download name to put in the fabricated release, and the tests further down that pin the
    /// platform cover that case instead.
    /// </summary>
    private static bool Supported => CurrentAssetName is not null;

    [Fact]
    public async Task ALatestReleaseWithNoAssetsYet_IsReleaseNotReady_NotUpToDate()
    {
        if (!Supported) return;

        var (outcome, state) = await CheckAgainstReleaseAsync(tag: "v9.9.9", withAssets: false);

        Assert.Equal(UpdatePhase.ReleaseNotReady, outcome);
        Assert.Equal("ReleaseNotReady", state.LastCheckOutcome);
        Assert.Equal("9.9.9", state.LastCheckLatestVersion);
        // Nothing was downloaded, so nothing may be recorded as staged.
        Assert.Null(state.StagedVersion);
    }

    [Fact]
    public async Task AReleaseThatIsNotNewer_IsUpToDate_AndSaysWhenItLooked()
    {
        if (!Supported) return;

        var (outcome, state) = await CheckAgainstReleaseAsync(tag: "v1.0.0", withAssets: false);

        Assert.Equal(UpdatePhase.UpToDate, outcome);
        Assert.Equal("UpToDate", state.LastCheckOutcome);
        Assert.NotNull(state.LastCheckedAt);
    }

    [Fact]
    public async Task AFailedCheck_IsWrittenDown_SoTheDisplayStopsClaimingUpToDate()
    {
        if (!Supported) return;

        // The machine was up to date an hour ago and the network has since gone. The old code left the
        // successful conclusion in place, so the display kept asserting "up to date" on the strength of
        // a check that had not worked since.
        var previous = new UpdaterState
        {
            LastCheckedAt = DateTimeOffset.UtcNow.AddHours(-1),
            LastCheckOutcome = "UpToDate",
        };

        var (outcome, state) = await CheckAgainstReleaseAsync(tag: "v9.9.9", withAssets: false,
            seed: previous, handler: new ThrowingHandler());

        Assert.Equal(UpdatePhase.Failed, outcome);
        Assert.Equal("Failed", state.LastCheckOutcome);
        Assert.False(string.IsNullOrWhiteSpace(state.LastCheckError));
    }

    [Fact]
    public async Task TheConclusionIsWhatTheFoldThenShows()
    {
        if (!Supported) return;

        // The end-to-end claim of #1030: what the check concluded is what the screen says. Reading the
        // persisted record is the ONLY way anything other than the checking code can know.
        var (_, state) = await CheckAgainstReleaseAsync(tag: "v9.9.9", withAssets: false);

        var view = UpdateStatusFold.Fold(new UpdateStatusFacts(
            CurrentVersion: "1.0.0",
            AutomaticUpdatesEnabled: true,
            State: state,
            Live: null,
            RunningSessionCount: 0,
            LauncherRunning: false,
            Now: DateTimeOffset.UtcNow));

        Assert.Equal("ReleaseNotReady", view.State);
        Assert.Contains("9.9.9", view.Detail);
    }

    [Fact]
    public async Task AnAlreadyStagedRelease_IsNotDownloadedAgain()
    {
        if (!Supported) return;

        // The defect: a Director that runs for days re-checks every hour, and the check only asked
        // "is the release newer than the running build?" - never "do I already have it?". So the same
        // release was downloaded again every hour until a restart applied it. Here the staged build is
        // already on disk and the handler REFUSES any download, so the check can only pass by
        // recognising the work is done.
        var stagedFile = Path.Combine(Path.GetTempPath(), $"cc-update-staged-{Guid.NewGuid():N}.exe");
        File.WriteAllText(stagedFile, "the already-downloaded build");
        try
        {
            var seed = new UpdaterState
            {
                StagedVersion = "9.9.9",
                StagedExecutable = stagedFile,
            };

            var (outcome, state) = await CheckAgainstReleaseAsync(tag: "v9.9.9", withAssets: true,
                seed: seed, handler: new DownloadRefusingHandler(tag: "v9.9.9"));

            Assert.Equal(UpdatePhase.Staged, outcome);
            Assert.Equal("Staged", state.LastCheckOutcome);
            Assert.Equal("9.9.9", state.StagedVersion);
            Assert.Equal(stagedFile, state.StagedExecutable);
        }
        finally
        {
            File.Delete(stagedFile);
        }
    }

    // ---- Harness ----------------------------------------------------------

    /// <summary>
    /// Run one check against a fabricated "latest release", with the storage root redirected so the
    /// state file written is this test's own. Returns the conclusion and the state as it was left.
    /// </summary>
    private static async Task<(UpdatePhase Outcome, UpdaterState State)> CheckAgainstReleaseAsync(
        string tag, bool withAssets, UpdaterState? seed = null, HttpMessageHandler? handler = null, bool withManifest = false,
        (OSPlatform Os, Architecture Arch)? platform = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-update-outcome-" + Guid.NewGuid().ToString("N"));
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            seed?.Save();

            var service = new UpdateService(
                new UpdateOptions
                {
                    Enabled = true,
                    CurrentVersion = new Version(1, 0, 0),
                    InstallTarget = Path.Combine(root, "cc-director.exe"),
                    PlatformOverride = platform,
                },
                handler ?? new ReleaseHandler(tag, withAssets, withManifest));

            var outcome = await service.CheckAndStageAsync();
            return (outcome, UpdaterState.Load());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    /// <summary>Answers the releases/latest call with a release that has, or has not, had its files attached.</summary>
    private sealed class ReleaseHandler(string tag, bool withAssets, bool withManifest = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var entries = new List<string>();
            if (withAssets)
                entries.Add($$"""{"name":"{{CurrentAssetName}}","browser_download_url":"https://example.invalid/a"}""");
            if (withAssets || withManifest)
                entries.Add("""{"name":"release-manifest.json","browser_download_url":"https://example.invalid/m"}""");
            var assets = "[" + string.Join(",", entries) + "]";
            var body = $$"""{"tag_name":"{{tag}}","assets":{{assets}}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("the network is unreachable");
    }

    /// <summary>
    /// Answers the releases/latest call with a complete release, and fails the test outright if
    /// anything tries to download one of its files. This is how the already-staged test can prove a
    /// negative: the only way through it is to not download.
    /// </summary>
    private sealed class DownloadRefusingHandler(string tag) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri is { } uri && uri.Host == "example.invalid")
                throw new InvalidOperationException($"the check tried to download {uri} for a release that is already staged");

            var body = $$"""
                {"tag_name":"{{tag}}","assets":[
                  {"name":"{{CurrentAssetName}}","browser_download_url":"https://example.invalid/a"},
                  {"name":"release-manifest.json","browser_download_url":"https://example.invalid/m"}
                ]}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task ACompleteReleaseWithNoAssetForThisPlatform_IsItsOwnFault_NotThePublishWindow()
    {
        if (!Supported) return;

        // These two shared one line and reported UpToDate together. A release that HAS its manifest is
        // finished publishing, so a missing platform asset is a real fault - waiting does not fix it and
        // it must not drive the short retry.
        var (outcome, state) = await CheckAgainstReleaseAsync(tag: "v9.9.9", withAssets: false, withManifest: true);

        Assert.Equal(UpdatePhase.NoBuildForThisPlatform, outcome);
        Assert.Equal("NoBuildForThisPlatform", state.LastCheckOutcome);
        Assert.Contains(CurrentAssetName!, state.LastCheckError);
        Assert.Null(state.StagedVersion);

        // The retry policy must treat it as ordinary, or a finished release gets polled for ever.
        Assert.Equal(TimeSpan.FromHours(1),
            new ReleaseNotReadyRetry().NextDelay(outcome, TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task APlatformWithNoPublishedBuild_ChecksAndSaysSo_InsteadOfClaimingUpToDate()
    {
        // The Linux defect. A platform with no download name returned "up to date" before contacting
        // anything and before writing anything down, so the panel sat on "not checked yet" for ever and
        // every "check for updates now" did nothing. Linux on an Arm processor still has no build, so it
        // stands in for any such platform, on whichever machine runs this.
        var (outcome, state) = await CheckAgainstReleaseAsync(tag: "v9.9.9", withAssets: true,
            platform: (OSPlatform.Linux, Architecture.Arm64));

        Assert.Equal(UpdatePhase.NoBuildForThisPlatform, outcome);
        Assert.Equal("NoBuildForThisPlatform", state.LastCheckOutcome);
        Assert.Equal("9.9.9", state.LastCheckLatestVersion);
        Assert.NotNull(state.LastCheckedAt);
        Assert.Null(state.StagedVersion);

        var view = UpdateStatusFold.Fold(new UpdateStatusFacts(
            CurrentVersion: "1.0.0",
            AutomaticUpdatesEnabled: true,
            State: state,
            Live: null,
            RunningSessionCount: 0,
            LauncherRunning: false,
            Now: DateTimeOffset.UtcNow));
        Assert.Equal("NoBuildForThisPlatform", view.State);
        Assert.NotEqual("NotCheckedYet", view.State);
    }

    [Fact]
    public async Task ALinuxCheck_LooksForTheLinuxBuild()
    {
        // A complete release whose only Director build is the Windows one. A Linux check must look for
        // cc-director-linux-x64 by name and report that it is missing - proving the Linux name is what
        // the check actually uses, not only what the mapping returns.
        var (outcome, state) = await CheckAgainstReleaseAsync(tag: "v9.9.9", withAssets: false,
            handler: new NamedAssetsHandler("v9.9.9", "cc-director-win-x64.exe", "release-manifest.json"),
            platform: (OSPlatform.Linux, Architecture.X64));

        Assert.Equal(UpdatePhase.NoBuildForThisPlatform, outcome);
        Assert.Contains("cc-director-linux-x64", state.LastCheckError);
    }

    [Fact]
    public async Task ALinuxDownload_IsStagedRunnable()
    {
        if (OperatingSystem.IsWindows()) return;

        // On Linux the relauncher starts the downloaded file itself. A download is written without the
        // executable bit, so without this the install would fail after the old Director had already exited.
        const string assetName = "cc-director-linux-x64";
        var content = Encoding.UTF8.GetBytes("#!/bin/sh\necho a staged build\n");
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));

        var root = Path.Combine(Path.GetTempPath(), "cc-update-linux-" + Guid.NewGuid().ToString("N"));
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            var service = new UpdateService(
                new UpdateOptions
                {
                    Enabled = true,
                    CurrentVersion = new Version(1, 0, 0),
                    InstallTarget = Path.Combine(root, assetName),
                    PlatformOverride = (OSPlatform.Linux, Architecture.X64),
                },
                new DownloadableReleaseHandler("v9.9.9", assetName, content, sha));

            var outcome = await service.CheckAndStageAsync();
            var state = UpdaterState.Load();

            Assert.Equal(UpdatePhase.Staged, outcome);
            Assert.NotNull(state.StagedExecutable);
            Assert.Equal(assetName, Path.GetFileName(state.StagedExecutable));
            Assert.True(File.GetUnixFileMode(state.StagedExecutable!).HasFlag(UnixFileMode.UserExecute),
                "the staged Linux build must be executable");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    /// <summary>Answers the releases/latest call with a complete release carrying exactly the named files.</summary>
    private sealed class NamedAssetsHandler(string tag, params string[] names) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var assets = string.Join(",", names.Select(n => $$"""{"name":"{{n}}","browser_download_url":"https://example.invalid/{{n}}"}"""));
            var body = $$"""{"tag_name":"{{tag}}","assets":[{{assets}}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>Serves a release, its one build, and a manifest carrying that build's hash.</summary>
    private sealed class DownloadableReleaseHandler(string tag, string assetName, byte[] content, string sha) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpContent body;
            if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
                body = new StringContent($$"""
                    {"tag_name":"{{tag}}","assets":[
                      {"name":"{{assetName}}","browser_download_url":"https://example.invalid/asset"},
                      {"name":"release-manifest.json","browser_download_url":"https://example.invalid/manifest"}
                    ]}
                    """, Encoding.UTF8, "application/json");
            else if (path == "/manifest")
                body = new StringContent("{\"assets\":{\"" + assetName + "\":{\"sha256\":\"" + sha + "\"}}}", Encoding.UTF8, "application/json");
            else if (path == "/asset")
                body = new ByteArrayContent(content);
            else
                throw new InvalidOperationException($"unexpected request {request.RequestUri}");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        }
    }
}
