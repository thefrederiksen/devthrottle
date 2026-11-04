using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using CcDirector.Core.Update;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Update;

/// <summary>
/// What a check CONCLUDES, and that it writes the conclusion down (issue #1030).
///
/// The Director reads its updates from our own download server: stable.json names the newest stable
/// release, and the version folder it names holds release-manifest.json and every build. Every check
/// here runs against a fake of that server, so nothing needs the live one.
///
/// These run on every platform that has a published Director build, using that platform's own download
/// name, so they are not only proved on Windows.
/// </summary>
[Collection("ProcessWideState")]
public class UpdateCheckOutcomeTests
{
    private const string Base = DownloadServer.BaseUrl;

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

    // ---- What a check concludes ------------------------------------------

    [Fact]
    public async Task AChannelThatNamesAFolderWithNoManifest_IsFailed_NeverUpToDate()
    {
        if (!Supported) return;

        // The channel file is written only after every file in its folder is verified, so a folder with
        // no manifest is not a release still being published - it is a broken server. It is a failure,
        // and the failure names the address that did not answer.
        var server = new FakeDownloadServer().Stable("v9.9.9");

        var (outcome, state) = await CheckAsync(server);

        Assert.Equal(UpdatePhase.Failed, outcome);
        Assert.Equal("Failed", state.LastCheckOutcome);
        Assert.Contains($"{Base}/v9.9.9/release-manifest.json", state.LastCheckError);
        Assert.Contains("404", state.LastCheckError);
        Assert.Null(state.StagedVersion);
    }

    [Fact]
    public async Task ADownloadServerThatAnswersAnError_IsFailed_AndNamesTheAddress()
    {
        var server = new FakeDownloadServer().Answer($"{Base}/stable.json", HttpStatusCode.ServiceUnavailable);

        var (outcome, state) = await CheckAsync(server, platform: (OSPlatform.Windows, Architecture.X64));

        Assert.Equal(UpdatePhase.Failed, outcome);
        Assert.Contains($"{Base}/stable.json", state.LastCheckError);
        Assert.Contains("503", state.LastCheckError);
    }

    [Fact]
    public async Task AManifestOfAnotherVersion_IsFailed_AndNothingIsDownloaded()
    {
        // A folder holding another release's manifest would install the wrong build under the right name.
        var server = new FakeDownloadServer()
            .Stable("v9.9.9")
            .Manifest("v9.9.9", manifestVersion: "9.9.8", ("cc-director-win-x64.exe", "ab"));

        var (outcome, state) = await CheckAsync(server, platform: (OSPlatform.Windows, Architecture.X64));

        Assert.Equal(UpdatePhase.Failed, outcome);
        Assert.Contains("9.9.8", state.LastCheckError);
        Assert.DoesNotContain(server.Requests, u => u.EndsWith("/cc-director-win-x64.exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReleaseThatIsNotNewer_IsUpToDate_AndSaysWhenItLooked()
    {
        if (!Supported) return;

        var (outcome, state) = await CheckAsync(new FakeDownloadServer().Stable("v1.0.0"));

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

        var (outcome, state) = await CheckAsync(new ThrowingHandler(), seed: previous);

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
        var (_, state) = await CheckAsync(new FakeDownloadServer().Stable("v9.9.9"));

        var view = UpdateStatusFold.Fold(new UpdateStatusFacts(
            CurrentVersion: "1.0.0",
            AutomaticUpdatesEnabled: true,
            State: state,
            Live: null,
            RunningSessionCount: 0,
            LauncherRunning: false,
            Now: DateTimeOffset.UtcNow));

        Assert.Equal("CheckFailed", view.State);
        Assert.Contains($"{Base}/v9.9.9/release-manifest.json", view.Tooltip);
    }

    [Fact]
    public async Task AnAlreadyStagedRelease_IsNotDownloadedAgain()
    {
        if (!Supported) return;

        // The defect: a Director that runs for days re-checks every hour, and the check only asked
        // "is the release newer than the running build?" - never "do I already have it?". So the same
        // release was downloaded again every hour until a restart applied it. Here the staged build is
        // already on disk and the server REFUSES any request into the version folder, so the check can
        // only pass by recognising the work is done.
        var stagedFile = Path.Combine(Path.GetTempPath(), $"cc-update-staged-{Guid.NewGuid():N}.exe");
        File.WriteAllText(stagedFile, "the already-downloaded build");
        try
        {
            var seed = new UpdaterState
            {
                StagedVersion = "9.9.9",
                StagedExecutable = stagedFile,
            };
            var server = new FakeDownloadServer().Stable("v9.9.9").RefuseFolder("v9.9.9");

            var (outcome, state) = await CheckAsync(server, seed: seed);

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

    /// <summary>
    /// The launcher applies the staged build while a check is in flight (issue #3666, review of #3680). The
    /// check loaded the whole state before it went to the network and saved that whole copy when it
    /// concluded, so the staged record the launcher had just cleared came back and the launcher's decision
    /// was erased - the next start would try to install the same build again. A check writes only what it
    /// found now.
    /// </summary>
    [Fact]
    public async Task ACheck_DoesNotUndoWhatTheLauncherWroteWhileItWasRunning()
    {
        if (!Supported) return;

        var staged = new UpdaterState { StagedVersion = "1.5.0", StagedExecutable = "x", InstallTarget = "y" };
        var server = new FakeDownloadServer().Stable("v1.0.0").OnEveryRequest(() =>
        {
            var launcher = UpdaterState.Load();
            launcher.StagedVersion = null;
            launcher.StagedExecutable = null;
            launcher.InstallTarget = null;
            launcher.LastApplyDecision = "Applied";
            launcher.Save();
        });

        var (outcome, state) = await CheckAsync(server, seed: staged);

        Assert.Equal(UpdatePhase.UpToDate, outcome);
        Assert.Equal("UpToDate", state.LastCheckOutcome);
        Assert.Null(state.StagedVersion);
        Assert.Equal("Applied", state.LastApplyDecision);
    }

    [Fact]
    public async Task ACompleteReleaseWithNoAssetForThisPlatform_IsNoBuildForThisPlatform()
    {
        if (!Supported) return;

        // The manifest is there and lists only some other file: the release is finished and has nothing for
        // this machine. Waiting does not fix it.
        var server = new FakeDownloadServer()
            .Stable("v9.9.9")
            .Manifest("v9.9.9", "9.9.9", ("devthrottle-setup-win-x64.exe", "ab"));

        var (outcome, state) = await CheckAsync(server);

        Assert.Equal(UpdatePhase.NoBuildForThisPlatform, outcome);
        Assert.Equal("NoBuildForThisPlatform", state.LastCheckOutcome);
        Assert.Contains(CurrentAssetName!, state.LastCheckError);
        Assert.Null(state.StagedVersion);
    }

    [Fact]
    public async Task APlatformWithNoPublishedBuild_ChecksAndSaysSo_InsteadOfClaimingUpToDate()
    {
        // The Linux defect. A platform with no download name returned "up to date" before contacting
        // anything and before writing anything down, so the panel sat on "not checked yet" for ever and
        // every "check for updates now" did nothing. Linux on an Arm processor still has no build, so it
        // stands in for any such platform, on whichever machine runs this.
        var (outcome, state) = await CheckAsync(new FakeDownloadServer().Stable("v9.9.9"),
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
        var server = new FakeDownloadServer()
            .Stable("v9.9.9")
            .Manifest("v9.9.9", "9.9.9", ("cc-director-win-x64.exe", "ab"));

        var (outcome, state) = await CheckAsync(server, platform: (OSPlatform.Linux, Architecture.X64));

        Assert.Equal(UpdatePhase.NoBuildForThisPlatform, outcome);
        Assert.Contains("cc-director-linux-x64", state.LastCheckError);
    }

    [Fact]
    public async Task AFullCheck_StagesFromTheVersionFolder_AndAsksOnlyTheDownloadServer()
    {
        // The bridge: every request a check makes - the channel file, the manifest, the build - goes to
        // the download server, and the build comes from the folder stable.json names. Not one request
        // goes to GitHub.
        const string assetName = "cc-director-win-x64.exe";
        var content = Encoding.UTF8.GetBytes("a staged Windows build");
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
        var server = new FakeDownloadServer()
            .Stable("v9.9.9")
            .Manifest("v9.9.9", "9.9.9", (assetName, sha))
            .File($"{Base}/v9.9.9/{assetName}", content);

        var (outcome, state) = await CheckAsync(server, platform: (OSPlatform.Windows, Architecture.X64));

        Assert.Equal(UpdatePhase.Staged, outcome);
        Assert.Equal("9.9.9", state.StagedVersion);
        Assert.Equal(assetName, Path.GetFileName(state.StagedExecutable));
        Assert.Equal(
            new[] { $"{Base}/stable.json", $"{Base}/v9.9.9/release-manifest.json", $"{Base}/v9.9.9/{assetName}" },
            server.Requests);
        Assert.All(server.Requests, u => Assert.StartsWith(Base + "/", u, StringComparison.Ordinal));
        Assert.DoesNotContain(server.Requests, u => u.Contains("github", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ADownloadWhoseHashDoesNotMatchTheManifest_IsRejected()
    {
        const string assetName = "cc-director-win-x64.exe";
        var server = new FakeDownloadServer()
            .Stable("v9.9.9")
            .Manifest("v9.9.9", "9.9.9", (assetName, new string('a', 64)))
            .File($"{Base}/v9.9.9/{assetName}", Encoding.UTF8.GetBytes("not the build the manifest hashed"));

        var (outcome, state) = await CheckAsync(server, platform: (OSPlatform.Windows, Architecture.X64));

        Assert.Equal(UpdatePhase.Failed, outcome);
        Assert.Null(state.StagedVersion);
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
        var server = new FakeDownloadServer()
            .Stable("v9.9.9")
            .Manifest("v9.9.9", "9.9.9", (assetName, sha))
            .File($"{Base}/v9.9.9/{assetName}", content);

        var (outcome, state) = await CheckAsync(server, platform: (OSPlatform.Linux, Architecture.X64));

        Assert.Equal(UpdatePhase.Staged, outcome);
        Assert.NotNull(state.StagedExecutable);
        Assert.Equal(assetName, Path.GetFileName(state.StagedExecutable));
        Assert.True(File.GetUnixFileMode(state.StagedExecutable!).HasFlag(UnixFileMode.UserExecute),
            "the staged Linux build must be executable");
    }

    // ---- Harness ----------------------------------------------------------

    /// <summary>
    /// Run one check against a fake download server, with the storage root redirected so the state file
    /// written is this test's own. Returns the conclusion and the state as it was left.
    /// </summary>
    private static async Task<(UpdatePhase Outcome, UpdaterState State)> CheckAsync(
        HttpMessageHandler handler, UpdaterState? seed = null, (OSPlatform Os, Architecture Arch)? platform = null)
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
                handler);

            var outcome = await service.CheckAndStageAsync();
            return (outcome, UpdaterState.Load());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// A fake of the download server: serves exactly the files it was given, answers 404 for anything
    /// else, and records every address asked for, in order.
    /// </summary>
    private sealed class FakeDownloadServer : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
        private readonly List<string> _refusedFolders = [];
        private Action? _onEveryRequest;

        public List<string> Requests { get; } = [];

        public FakeDownloadServer Stable(string tag)
        {
            var version = tag.TrimStart('v');
            var json = $$"""{"version":"{{version}}","tag":"{{tag}}","prerelease":false,"manifest":"{{tag}}/release-manifest.json","date":"2026-11-03T00:00:00Z"}""";
            _routes[$"{Base}/stable.json"] = () => Json(json);
            return this;
        }

        public FakeDownloadServer Manifest(string tag, string manifestVersion, params (string Name, string Sha)[] assets)
        {
            var entries = string.Join(",", assets.Select(a =>
                $$"""
                "{{a.Name}}":{"version":"{{manifestVersion}}","sha256":"{{a.Sha}}","platform":"any","size":0}
                """));
            var json = $$"""{"version":"{{manifestVersion}}","tag":"{{tag}}","assets":{""" + entries + "}}";
            _routes[$"{Base}/{tag}/release-manifest.json"] = () => Json(json);
            return this;
        }

        public FakeDownloadServer File(string url, byte[] content)
        {
            _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
            return this;
        }

        public FakeDownloadServer Answer(string url, HttpStatusCode status)
        {
            _routes[url] = () => new HttpResponseMessage(status) { Content = new StringContent("") };
            return this;
        }

        /// <summary>Fail the test outright if anything asks for a file in this version folder.</summary>
        public FakeDownloadServer RefuseFolder(string tag)
        {
            _refusedFolders.Add($"{Base}/{tag}/");
            return this;
        }

        public FakeDownloadServer OnEveryRequest(Action action)
        {
            _onEveryRequest = action;
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            foreach (var folder in _refusedFolders)
                if (url.StartsWith(folder, StringComparison.Ordinal))
                    throw new InvalidOperationException($"the check asked for {url} for a release that is already staged");
            _onEveryRequest?.Invoke();
            return Task.FromResult(_routes.TryGetValue(url, out var route)
                ? route()
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") });
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("the network is unreachable");
    }
}
